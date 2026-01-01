using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using BaseScanner.Context;
using System.Text.RegularExpressions;

namespace BaseScanner.Analyzers.Security;

/// <summary>
/// Detects hardcoded secrets: API keys, passwords, connection strings, private keys.
/// </summary>
public class SecretDetector : ISecurityDetector
{
    public string Category => "Secrets";

    private static readonly List<SecretPattern> Patterns = new()
    {
        // API Keys
        new("AWS Access Key", @"AKIA[0-9A-Z]{16}", "CWE-798", "Critical"),
        new("AWS Secret Key", @"(?i)aws(.{0,20})?['""][0-9a-zA-Z/+]{40}['""]", "CWE-798", "Critical"),
        new("Google API Key", @"AIza[0-9A-Za-z\-_]{35}", "CWE-798", "High"),
        new("Stripe API Key", @"(?:sk|pk)_(?:live|test)_[0-9a-zA-Z]{24}", "CWE-798", "Critical"),
        new("GitHub Token", @"(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9_]{36}", "CWE-798", "Critical"),
        new("Slack Token", @"xox[baprs]-[0-9]{10,13}-[0-9]{10,13}[a-zA-Z0-9-]*", "CWE-798", "High"),
        new("Azure Storage Key", @"(?i)DefaultEndpointsProtocol=https;AccountName=[^;]+;AccountKey=[A-Za-z0-9+/=]{88}", "CWE-798", "Critical"),
        new("SendGrid API Key", @"SG\.[a-zA-Z0-9_-]{22}\.[a-zA-Z0-9_-]{43}", "CWE-798", "High"),
        new("Twilio API Key", @"SK[0-9a-fA-F]{32}", "CWE-798", "High"),

        // Private Keys
        new("RSA Private Key", @"-----BEGIN RSA PRIVATE KEY-----", "CWE-321", "Critical"),
        new("OpenSSH Private Key", @"-----BEGIN OPENSSH PRIVATE KEY-----", "CWE-321", "Critical"),
        new("EC Private Key", @"-----BEGIN EC PRIVATE KEY-----", "CWE-321", "Critical"),
        new("PGP Private Key", @"-----BEGIN PGP PRIVATE KEY BLOCK-----", "CWE-321", "Critical"),

        // Connection Strings
        new("SQL Connection String with Password", @"(?i)(password|pwd)\s*=\s*['""]?[^;'""]+['""]?", "CWE-798", "Critical"),
        new("MongoDB Connection String", @"mongodb(\+srv)?://[^:]+:[^@]+@", "CWE-798", "Critical"),

        // JWT Secrets
        new("JWT Secret", @"(?i)(jwt|bearer|token).{0,20}(secret|key).{0,10}[=:].{0,5}['""][a-zA-Z0-9+/=]{20,}['""]", "CWE-321", "High"),

        // Generic Patterns
        new("Hardcoded Password Variable", @"(?i)(password|passwd|pwd|secret|apikey|api_key|accesskey|access_key)\s*[=:]\s*['""][^'""]{8,}['""]", "CWE-798", "High"),
        new("Bearer Token", @"(?i)bearer\s+[a-zA-Z0-9\-_.~+/]+=*", "CWE-798", "High")
    };

    private static readonly HashSet<string> SuspiciousVariableNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "passwd", "pwd", "secret", "apikey", "api_key", "accesskey", "access_key",
        "privatekey", "private_key", "secretkey", "secret_key", "token", "credential",
        "connectionstring", "connection_string", "authtoken", "auth_token"
    };

    public Task<List<SecurityVulnerability>> DetectAsync(
        Document document,
        SemanticModel semanticModel,
        SyntaxNode root,
        CodeContext context)
    {
        var vulnerabilities = new List<SecurityVulnerability>();
        var filePath = document.FilePath ?? "";

        // Skip test files and configuration samples
        if (IsTestOrSampleFile(filePath))
            return Task.FromResult(vulnerabilities);

        // Check string literals for known secret patterns
        DetectSecretPatterns(root, filePath, vulnerabilities);

        // Check variable assignments for suspicious names with literal values
        DetectHardcodedCredentials(root, semanticModel, filePath, vulnerabilities);

        // Check for high-entropy strings that might be secrets
        DetectHighEntropyStrings(root, semanticModel, filePath, vulnerabilities);

        return Task.FromResult(vulnerabilities);
    }

    private bool IsTestOrSampleFile(string filePath)
    {
        var lowerPath = filePath.ToLowerInvariant();
        return lowerPath.Contains("test") ||
               lowerPath.Contains("sample") ||
               lowerPath.Contains("example") ||
               lowerPath.Contains("mock") ||
               lowerPath.Contains("fake");
    }

    private void DetectSecretPatterns(
        SyntaxNode root,
        string filePath,
        List<SecurityVulnerability> vulnerabilities)
    {
        foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            if (!literal.IsKind(SyntaxKind.StringLiteralExpression))
                continue;

            var value = literal.Token.ValueText;
            if (string.IsNullOrEmpty(value) || value.Length < 10)
                continue;

            foreach (var pattern in Patterns)
            {
                if (Regex.IsMatch(value, pattern.Regex, RegexOptions.IgnoreCase))
                {
                    var lineSpan = literal.GetLocation().GetLineSpan();
                    var maskedValue = MaskSecret(value);

                    vulnerabilities.Add(new SecurityVulnerability
                    {
                        VulnerabilityType = pattern.Name,
                        Severity = pattern.Severity,
                        CweId = pattern.CweId,
                        FilePath = filePath,
                        StartLine = lineSpan.StartLinePosition.Line + 1,
                        EndLine = lineSpan.EndLinePosition.Line + 1,
                        Description = $"Detected potential {pattern.Name} hardcoded in source code.",
                        Recommendation = "Store secrets in secure configuration (environment variables, Azure Key Vault, AWS Secrets Manager, etc.)",
                        VulnerableCode = maskedValue,
                        SecureCode = "// Use: Environment.GetEnvironmentVariable(\"SECRET_NAME\")\n// Or: Configuration[\"SecretName\"] with secure configuration provider",
                        Confidence = "High"
                    });
                    break; // Only report first matching pattern per literal
                }
            }
        }
    }

    private void DetectHardcodedCredentials(
        SyntaxNode root,
        SemanticModel semanticModel,
        string filePath,
        List<SecurityVulnerability> vulnerabilities)
    {
        // Check field declarations
        foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
        {
            foreach (var variable in field.Declaration.Variables)
            {
                var name = variable.Identifier.Text;
                if (!IsSuspiciousVariableName(name))
                    continue;

                if (variable.Initializer?.Value is LiteralExpressionSyntax literal &&
                    literal.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    var value = literal.Token.ValueText;
                    if (!string.IsNullOrEmpty(value) && value.Length >= 4 && !IsPlaceholder(value))
                    {
                        ReportHardcodedCredential(variable, name, value, filePath, vulnerabilities);
                    }
                }
            }
        }

        // Check local variable declarations
        foreach (var local in root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
        {
            foreach (var variable in local.Declaration.Variables)
            {
                var name = variable.Identifier.Text;
                if (!IsSuspiciousVariableName(name))
                    continue;

                if (variable.Initializer?.Value is LiteralExpressionSyntax literal &&
                    literal.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    var value = literal.Token.ValueText;
                    if (!string.IsNullOrEmpty(value) && value.Length >= 4 && !IsPlaceholder(value))
                    {
                        ReportHardcodedCredential(variable, name, value, filePath, vulnerabilities);
                    }
                }
            }
        }

        // Check property initializers
        foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
        {
            var name = property.Identifier.Text;
            if (!IsSuspiciousVariableName(name))
                continue;

            if (property.Initializer?.Value is LiteralExpressionSyntax literal &&
                literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                var value = literal.Token.ValueText;
                if (!string.IsNullOrEmpty(value) && value.Length >= 4 && !IsPlaceholder(value))
                {
                    var lineSpan = property.GetLocation().GetLineSpan();
                    vulnerabilities.Add(new SecurityVulnerability
                    {
                        VulnerabilityType = "Hardcoded Credential",
                        Severity = "High",
                        CweId = "CWE-798",
                        FilePath = filePath,
                        StartLine = lineSpan.StartLinePosition.Line + 1,
                        EndLine = lineSpan.EndLinePosition.Line + 1,
                        Description = $"Property '{name}' appears to contain a hardcoded credential.",
                        Recommendation = "Use secure configuration management instead of hardcoded values.",
                        VulnerableCode = MaskSecret(property.ToFullString().Trim()),
                        SecureCode = $"public string {name} => Configuration[\"{name}\"];",
                        Confidence = "Medium"
                    });
                }
            }
        }
    }

    private bool IsSuspiciousVariableName(string name)
    {
        var lowerName = name.ToLowerInvariant();
        return SuspiciousVariableNames.Any(s => lowerName.Contains(s));
    }

    private bool IsPlaceholder(string value)
    {
        var lowerValue = value.ToLowerInvariant();
        return lowerValue.Contains("xxx") ||
               lowerValue.Contains("todo") ||
               lowerValue.Contains("placeholder") ||
               lowerValue.Contains("your_") ||
               lowerValue.Contains("<") ||
               lowerValue.Contains("{") ||
               lowerValue == "password" ||
               lowerValue == "secret" ||
               lowerValue == "changeme" ||
               lowerValue == "test" ||
               lowerValue.All(c => c == '*' || c == 'x');
    }

    private void ReportHardcodedCredential(
        VariableDeclaratorSyntax variable,
        string name,
        string value,
        string filePath,
        List<SecurityVulnerability> vulnerabilities)
    {
        var lineSpan = variable.GetLocation().GetLineSpan();
        vulnerabilities.Add(new SecurityVulnerability
        {
            VulnerabilityType = "Hardcoded Credential",
            Severity = "High",
            CweId = "CWE-798",
            FilePath = filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            Description = $"Variable '{name}' appears to contain a hardcoded credential.",
            Recommendation = "Use secure configuration management instead of hardcoded values.",
            VulnerableCode = MaskSecret(variable.ToFullString().Trim()),
            SecureCode = $"var {name} = Environment.GetEnvironmentVariable(\"{name.ToUpperInvariant()}\");",
            Confidence = "Medium"
        });
    }

    private void DetectHighEntropyStrings(
        SyntaxNode root,
        SemanticModel semanticModel,
        string filePath,
        List<SecurityVulnerability> vulnerabilities)
    {
        foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            if (!literal.IsKind(SyntaxKind.StringLiteralExpression))
                continue;

            var value = literal.Token.ValueText;

            // Skip short strings or obvious non-secrets
            if (value.Length < 20 || value.Length > 500)
                continue;

            // Skip if contains common non-secret patterns
            if (value.Contains(" ") || value.Contains("http") || value.Contains("/") ||
                value.Contains("\\") || value.Contains(".cs") || value.Contains("<"))
                continue;

            var entropy = CalculateEntropy(value);

            // High entropy (> 4.5 bits per character) suggests random/encrypted data
            if (entropy > 4.5 && IsLikelySecret(value))
            {
                var lineSpan = literal.GetLocation().GetLineSpan();
                vulnerabilities.Add(new SecurityVulnerability
                {
                    VulnerabilityType = "High-Entropy String",
                    Severity = "Medium",
                    CweId = "CWE-798",
                    FilePath = filePath,
                    StartLine = lineSpan.StartLinePosition.Line + 1,
                    EndLine = lineSpan.EndLinePosition.Line + 1,
                    Description = $"High-entropy string detected (entropy: {entropy:F2}). This may be a hardcoded secret or key.",
                    Recommendation = "Review this string. If it's a secret, move it to secure configuration.",
                    VulnerableCode = MaskSecret(value),
                    SecureCode = "// Store in environment variable or secure vault",
                    Confidence = "Low"
                });
            }
        }
    }

    private double CalculateEntropy(string value)
    {
        if (string.IsNullOrEmpty(value))
            return 0;

        var frequencies = value.GroupBy(c => c)
            .Select(g => (double)g.Count() / value.Length)
            .ToList();

        return -frequencies.Sum(p => p * Math.Log2(p));
    }

    private bool IsLikelySecret(string value)
    {
        // Check if it looks like a base64 or hex encoded value
        var base64Pattern = @"^[A-Za-z0-9+/=]+$";
        var hexPattern = @"^[A-Fa-f0-9]+$";

        return Regex.IsMatch(value, base64Pattern) || Regex.IsMatch(value, hexPattern);
    }

    private string MaskSecret(string value)
    {
        if (value.Length <= 8)
            return "****";

        return value.Substring(0, 4) + "..." + value.Substring(value.Length - 4);
    }

    private record SecretPattern(string Name, string Regex, string CweId, string Severity);
}
