using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BaseScanner.Analyzers;

public class MagicValueAnalyzer
{
    // Common acceptable magic numbers
    private static readonly HashSet<int> AcceptableNumbers = new()
    {
        -1, 0, 1, 2, 10, 100, 1000,
        8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096, // Powers of 2
        24, 60, 365, // Time-related
        200, 201, 204, 301, 302, 400, 401, 403, 404, 500, 502, 503 // HTTP status codes
    };

    // Common acceptable strings (partial match)
    private static readonly string[] AcceptableStringPatterns = new[]
    {
        "http", "https", "ftp", "mailto",  // URLs
        ".json", ".xml", ".csv", ".txt", ".pdf", // File extensions
        "yyyy", "MM", "dd", "HH", "mm", "ss", // Date formats
        "utf-8", "utf-16", "ascii", // Encodings
        "application/json", "text/plain", "text/html", // MIME types
        "GET", "POST", "PUT", "DELETE", "PATCH", // HTTP methods
        "true", "false", "null", // Common values
        "\\n", "\\r", "\\t", " ", "", // Whitespace
        "id", "Id", "ID", "name", "Name", "type", "Type", // Common field names
    };

    public record Issue
    {
        public required string Type { get; init; }
        public required string Severity { get; init; }
        public required string Message { get; init; }
        public required string FilePath { get; init; }
        public required int Line { get; init; }
        public required string CodeSnippet { get; init; }
        public required string Value { get; init; }
        public required int Occurrences { get; init; }
    }

    public async Task<List<Issue>> AnalyzeAsync(Project project)
    {
        var magicNumbers = new Dictionary<string, List<(string FilePath, int Line, string Context)>>();
        var magicStrings = new Dictionary<string, List<(string FilePath, int Line, string Context)>>();

        foreach (var document in project.Documents)
        {
            if (document.FilePath == null) continue;
            if (document.FilePath.Contains(".Designer.cs")) continue;

            var syntaxRoot = await document.GetSyntaxRootAsync();
            if (syntaxRoot == null) continue;

            // Find magic numbers
            CollectMagicNumbers(syntaxRoot, document.FilePath, magicNumbers);

            // Find magic strings
            CollectMagicStrings(syntaxRoot, document.FilePath, magicStrings);
        }

        var issues = new List<Issue>();

        // Report magic numbers that appear multiple times
        foreach (var (number, locations) in magicNumbers.Where(kv => kv.Value.Count >= 2))
        {
            var first = locations.First();
            issues.Add(new Issue
            {
                Type = "MagicNumber",
                Severity = locations.Count >= 5 ? "Warning" : "Info",
                Message = $"Magic number '{number}' appears {locations.Count} times - consider using a named constant",
                FilePath = first.FilePath,
                Line = first.Line,
                CodeSnippet = first.Context,
                Value = number,
                Occurrences = locations.Count
            });
        }

        // Report magic strings that appear multiple times
        foreach (var (str, locations) in magicStrings.Where(kv => kv.Value.Count >= 3))
        {
            var first = locations.First();
            var displayStr = str.Length > 30 ? str.Substring(0, 30) + "..." : str;

            issues.Add(new Issue
            {
                Type = "MagicString",
                Severity = locations.Count >= 5 ? "Warning" : "Info",
                Message = $"Magic string \"{displayStr}\" appears {locations.Count} times - consider using a constant",
                FilePath = first.FilePath,
                Line = first.Line,
                CodeSnippet = first.Context,
                Value = str,
                Occurrences = locations.Count
            });
        }

        return issues.OrderByDescending(i => i.Occurrences).ToList();
    }

    private void CollectMagicNumbers(SyntaxNode root, string filePath, Dictionary<string, List<(string FilePath, int Line, string Context)>> magicNumbers)
    {
        var literals = root.DescendantNodes().OfType<LiteralExpressionSyntax>()
            .Where(l => l.IsKind(SyntaxKind.NumericLiteralExpression));

        foreach (var literal in literals)
        {
            // Skip if in const/readonly declaration
            if (IsInConstantDeclaration(literal)) continue;

            // Skip if in attribute
            if (literal.Ancestors().Any(a => a is AttributeSyntax)) continue;

            // Skip if in enum
            if (literal.Ancestors().Any(a => a is EnumMemberDeclarationSyntax)) continue;

            // Skip array size declarations
            if (literal.Parent is ArrayRankSpecifierSyntax) continue;

            var value = literal.Token.ValueText;

            // Skip acceptable values
            if (int.TryParse(value, out var intValue) && AcceptableNumbers.Contains(intValue)) continue;
            if (double.TryParse(value, out var doubleValue) && (doubleValue == 0.0 || doubleValue == 1.0 || doubleValue == 0.5)) continue;

            // Get context
            var context = GetContext(literal);

            if (!magicNumbers.ContainsKey(value))
                magicNumbers[value] = new List<(string, int, string)>();

            magicNumbers[value].Add((filePath, literal.GetLocation().GetLineSpan().StartLinePosition.Line + 1, context));
        }
    }

    private void CollectMagicStrings(SyntaxNode root, string filePath, Dictionary<string, List<(string FilePath, int Line, string Context)>> magicStrings)
    {
        var literals = root.DescendantNodes().OfType<LiteralExpressionSyntax>()
            .Where(l => l.IsKind(SyntaxKind.StringLiteralExpression));

        foreach (var literal in literals)
        {
            // Skip if in const/readonly declaration
            if (IsInConstantDeclaration(literal)) continue;

            // Skip if in attribute
            if (literal.Ancestors().Any(a => a is AttributeSyntax)) continue;

            // Skip nameof expressions
            if (literal.Ancestors().Any(a => a is InvocationExpressionSyntax inv &&
                inv.Expression.ToString() == "nameof")) continue;

            var value = literal.Token.ValueText;

            // Skip empty or very short strings
            if (string.IsNullOrEmpty(value) || value.Length < 3) continue;

            // Skip acceptable patterns
            if (AcceptableStringPatterns.Any(p => value.Contains(p, StringComparison.OrdinalIgnoreCase))) continue;

            // Skip interpolated strings (they often have dynamic content)
            if (literal.Parent is InterpolatedStringContentSyntax) continue;

            // Skip if it looks like a format string
            if (value.Contains("{0}") || value.Contains("{1}")) continue;

            // Skip if it looks like a path separator
            if (value == "/" || value == "\\" || value == "\\\\") continue;

            // Skip connection strings
            if (value.Contains("Data Source") || value.Contains("Server=") || value.Contains("Database=")) continue;

            var context = GetContext(literal);

            if (!magicStrings.ContainsKey(value))
                magicStrings[value] = new List<(string, int, string)>();

            magicStrings[value].Add((filePath, literal.GetLocation().GetLineSpan().StartLinePosition.Line + 1, context));
        }
    }

    private bool IsInConstantDeclaration(SyntaxNode node)
    {
        var fieldDecl = node.Ancestors().OfType<FieldDeclarationSyntax>().FirstOrDefault();
        if (fieldDecl != null)
        {
            if (fieldDecl.Modifiers.Any(SyntaxKind.ConstKeyword) ||
                fieldDecl.Modifiers.Any(SyntaxKind.ReadOnlyKeyword))
            {
                return true;
            }
        }

        var localDecl = node.Ancestors().OfType<LocalDeclarationStatementSyntax>().FirstOrDefault();
        if (localDecl != null && localDecl.Modifiers.Any(SyntaxKind.ConstKeyword))
        {
            return true;
        }

        return false;
    }

    private string GetContext(SyntaxNode node)
    {
        // Try to get the containing statement or expression
        var statement = node.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
        if (statement != null)
        {
            var text = statement.ToString();
            return text.Length > 60 ? text.Substring(0, 60) + "..." : text;
        }

        var member = node.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault();
        if (member is MethodDeclarationSyntax method)
        {
            return $"in method {method.Identifier.Text}";
        }

        return node.Parent?.ToString().Substring(0, Math.Min(60, node.Parent?.ToString().Length ?? 0)) ?? "";
    }
}
