using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BaseScanner.Analyzers.Frameworks;

/// <summary>
/// Analyzes ASP.NET Core specific security and best practice issues.
/// </summary>
public class AspNetCoreAnalyzer
{
    public async Task<FrameworkAnalysisResult> AnalyzeAsync(Project project)
    {
        var issues = new List<FrameworkIssue>();

        foreach (var document in project.Documents)
        {
            var semanticModel = await document.GetSemanticModelAsync();
            var root = await document.GetSyntaxRootAsync();

            if (semanticModel == null || root == null) continue;

            issues.AddRange(await AnalyzeDocumentAsync(document, semanticModel, root));
        }

        return new FrameworkAnalysisResult
        {
            Framework = "ASP.NET Core",
            TotalIssues = issues.Count,
            CriticalCount = issues.Count(i => i.Severity == "Critical"),
            HighCount = issues.Count(i => i.Severity == "High"),
            MediumCount = issues.Count(i => i.Severity == "Medium"),
            Issues = issues,
            IssuesByType = issues.GroupBy(i => i.IssueType).ToDictionary(g => g.Key, g => g.Count())
        };
    }

    private async Task<List<FrameworkIssue>> AnalyzeDocumentAsync(
        Document document, SemanticModel model, SyntaxNode root)
    {
        var issues = new List<FrameworkIssue>();

        // Check for controllers
        foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (!IsController(classDecl, model)) continue;

            issues.AddRange(AnalyzeController(classDecl, document, model));
        }

        // Check Startup/Program configuration
        issues.AddRange(AnalyzeConfiguration(root, document, model));

        return issues;
    }

    private bool IsController(ClassDeclarationSyntax classDecl, SemanticModel model)
    {
        var name = classDecl.Identifier.Text;
        if (name.EndsWith("Controller")) return true;

        // Check for [ApiController] or [Controller] attribute
        foreach (var attrList in classDecl.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var attrName = attr.Name.ToString();
                if (attrName == "ApiController" || attrName == "Controller")
                    return true;
            }
        }

        // Check base class
        if (classDecl.BaseList != null)
        {
            foreach (var baseType in classDecl.BaseList.Types)
            {
                var typeName = baseType.Type.ToString();
                if (typeName.Contains("Controller") || typeName.Contains("ControllerBase"))
                    return true;
            }
        }

        return false;
    }

    private List<FrameworkIssue> AnalyzeController(
        ClassDeclarationSyntax classDecl, Document document, SemanticModel model)
    {
        var issues = new List<FrameworkIssue>();
        var hasAuthorize = HasAttribute(classDecl, "Authorize");
        var hasAllowAnonymous = HasAttribute(classDecl, "AllowAnonymous");

        // Check for missing authorization on controller
        if (!hasAuthorize && !hasAllowAnonymous)
        {
            var location = classDecl.Identifier.GetLocation().GetLineSpan();
            issues.Add(new FrameworkIssue
            {
                IssueType = "MissingAuthorize",
                Severity = "Medium",
                Message = $"Controller '{classDecl.Identifier.Text}' has no [Authorize] attribute - consider adding explicit authorization",
                FilePath = document.FilePath ?? "",
                Line = location.StartLinePosition.Line + 1,
                CweId = "CWE-862",
                SuggestedFix = "Add [Authorize] attribute to the controller or individual actions"
            });
        }

        // Check each action method
        foreach (var method in classDecl.Members.OfType<MethodDeclarationSyntax>())
        {
            if (!method.Modifiers.Any(SyntaxKind.PublicKeyword)) continue;

            issues.AddRange(AnalyzeAction(method, classDecl, document, model, hasAuthorize));
        }

        return issues;
    }

    private List<FrameworkIssue> AnalyzeAction(
        MethodDeclarationSyntax method,
        ClassDeclarationSyntax controller,
        Document document,
        SemanticModel model,
        bool controllerHasAuthorize)
    {
        var issues = new List<FrameworkIssue>();
        var httpMethod = GetHttpMethod(method);
        var location = method.Identifier.GetLocation().GetLineSpan();

        // Check for missing anti-forgery on state-changing operations
        if (httpMethod is "HttpPost" or "HttpPut" or "HttpPatch" or "HttpDelete")
        {
            if (!HasAttribute(method, "ValidateAntiForgeryToken") &&
                !HasAttribute(method, "IgnoreAntiforgeryToken") &&
                !HasAttribute(controller, "ApiController")) // API controllers handle CSRF differently
            {
                issues.Add(new FrameworkIssue
                {
                    IssueType = "MissingAntiforgery",
                    Severity = "High",
                    Message = $"Action '{method.Identifier.Text}' is missing [ValidateAntiForgeryToken] for {httpMethod}",
                    FilePath = document.FilePath ?? "",
                    Line = location.StartLinePosition.Line + 1,
                    CweId = "CWE-352",
                    SuggestedFix = "Add [ValidateAntiForgeryToken] attribute"
                });
            }
        }

        // Check for [AllowAnonymous] on sensitive operations
        if (HasAttribute(method, "AllowAnonymous") && controllerHasAuthorize)
        {
            var methodName = method.Identifier.Text.ToLower();
            if (methodName.Contains("admin") || methodName.Contains("delete") ||
                methodName.Contains("update") || methodName.Contains("create"))
            {
                issues.Add(new FrameworkIssue
                {
                    IssueType = "SensitiveAllowAnonymous",
                    Severity = "High",
                    Message = $"Action '{method.Identifier.Text}' allows anonymous access but appears to be a sensitive operation",
                    FilePath = document.FilePath ?? "",
                    Line = location.StartLinePosition.Line + 1,
                    CweId = "CWE-862",
                    SuggestedFix = "Remove [AllowAnonymous] or ensure this is intentional"
                });
            }
        }

        // Check for open redirect vulnerabilities
        foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var methodName = GetMethodName(invocation);
            if (methodName is "Redirect" or "LocalRedirect" or "RedirectToAction")
            {
                // Check if the URL comes from user input
                var args = invocation.ArgumentList.Arguments;
                if (args.Count > 0)
                {
                    var firstArg = args[0].Expression;
                    if (IsFromUserInput(firstArg, model))
                    {
                        var invLocation = invocation.GetLocation().GetLineSpan();
                        issues.Add(new FrameworkIssue
                        {
                            IssueType = "OpenRedirect",
                            Severity = "High",
                            Message = "Redirect URL may come from user input - potential open redirect vulnerability",
                            FilePath = document.FilePath ?? "",
                            Line = invLocation.StartLinePosition.Line + 1,
                            CweId = "CWE-601",
                            SuggestedFix = "Use LocalRedirect() and validate the URL, or use Url.IsLocalUrl()"
                        });
                    }
                }
            }
        }

        // Check for mass assignment vulnerabilities
        foreach (var parameter in method.ParameterList.Parameters)
        {
            var typeInfo = model.GetTypeInfo(parameter.Type!);
            if (IsEntityType(typeInfo.Type))
            {
                issues.Add(new FrameworkIssue
                {
                    IssueType = "MassAssignment",
                    Severity = "High",
                    Message = $"Binding directly to entity type '{parameter.Type}' - may allow mass assignment attacks",
                    FilePath = document.FilePath ?? "",
                    Line = location.StartLinePosition.Line + 1,
                    CweId = "CWE-915",
                    SuggestedFix = "Use a DTO/ViewModel with explicit properties instead of binding to entity types"
                });
            }
        }

        // Check for sensitive data in logs
        foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var methodName = GetMethodName(invocation);
            if (methodName is "LogInformation" or "LogDebug" or "LogWarning" or "LogError" or "Log")
            {
                var argsText = invocation.ArgumentList.ToString().ToLower();
                if (argsText.Contains("password") || argsText.Contains("secret") ||
                    argsText.Contains("token") || argsText.Contains("apikey") ||
                    argsText.Contains("creditcard") || argsText.Contains("ssn"))
                {
                    var invLocation = invocation.GetLocation().GetLineSpan();
                    issues.Add(new FrameworkIssue
                    {
                        IssueType = "SensitiveDataInLogs",
                        Severity = "High",
                        Message = "Potential sensitive data being logged",
                        FilePath = document.FilePath ?? "",
                        Line = invLocation.StartLinePosition.Line + 1,
                        CweId = "CWE-532",
                        SuggestedFix = "Redact sensitive data before logging"
                    });
                }
            }
        }

        return issues;
    }

    private List<FrameworkIssue> AnalyzeConfiguration(SyntaxNode root, Document document, SemanticModel model)
    {
        var issues = new List<FrameworkIssue>();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var methodName = GetMethodName(invocation);
            var location = invocation.GetLocation().GetLineSpan();

            // Insecure CORS configuration
            if (methodName == "AllowAnyOrigin")
            {
                // Check if credentials are also allowed
                var parent = invocation.Parent;
                while (parent != null && parent is not StatementSyntax)
                {
                    if (parent.ToString().Contains("AllowCredentials"))
                    {
                        issues.Add(new FrameworkIssue
                        {
                            IssueType = "InsecureCors",
                            Severity = "Critical",
                            Message = "CORS policy allows any origin with credentials - this is a security risk",
                            FilePath = document.FilePath ?? "",
                            Line = location.StartLinePosition.Line + 1,
                            CweId = "CWE-346",
                            SuggestedFix = "Specify allowed origins explicitly instead of AllowAnyOrigin"
                        });
                        break;
                    }
                    parent = parent.Parent;
                }
            }

            // Missing HTTPS redirection
            if (methodName == "UseHttpsRedirection")
            {
                // This is good - we track its presence
            }

            // Insecure cookie settings
            if (methodName == "Append" || methodName == "Add")
            {
                var argsText = invocation.ArgumentList.ToString();
                if (argsText.Contains("Cookie"))
                {
                    if (!argsText.Contains("Secure") || !argsText.Contains("HttpOnly"))
                    {
                        issues.Add(new FrameworkIssue
                        {
                            IssueType = "InsecureCookie",
                            Severity = "Medium",
                            Message = "Cookie may be missing Secure or HttpOnly flags",
                            FilePath = document.FilePath ?? "",
                            Line = location.StartLinePosition.Line + 1,
                            CweId = "CWE-614",
                            SuggestedFix = "Set Secure = true and HttpOnly = true for cookies"
                        });
                    }
                }
            }

            // Check for disabled certificate validation
            if (methodName == "ServerCertificateCustomValidationCallback")
            {
                var argsText = invocation.ArgumentList.ToString();
                if (argsText.Contains("true") || argsText.Contains("DangerousAcceptAnyServerCertificateValidator"))
                {
                    issues.Add(new FrameworkIssue
                    {
                        IssueType = "CertificateValidationDisabled",
                        Severity = "Critical",
                        Message = "Certificate validation is disabled - vulnerable to MITM attacks",
                        FilePath = document.FilePath ?? "",
                        Line = location.StartLinePosition.Line + 1,
                        CweId = "CWE-295",
                        SuggestedFix = "Enable proper certificate validation"
                    });
                }
            }
        }

        return issues;
    }

    private bool HasAttribute(MemberDeclarationSyntax member, string attributeName)
    {
        foreach (var attrList in member.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var name = attr.Name.ToString();
                if (name == attributeName || name == attributeName + "Attribute")
                    return true;
            }
        }
        return false;
    }

    private string? GetHttpMethod(MethodDeclarationSyntax method)
    {
        foreach (var attrList in method.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var name = attr.Name.ToString();
                if (name.StartsWith("Http"))
                    return name;
            }
        }
        return null;
    }

    private string GetMethodName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
            IdentifierNameSyntax id => id.Identifier.Text,
            _ => ""
        };
    }

    private bool IsFromUserInput(ExpressionSyntax expression, SemanticModel model)
    {
        var text = expression.ToString().ToLower();
        return text.Contains("request") || text.Contains("query") ||
               text.Contains("form") || text.Contains("route") ||
               text.Contains("header") || text.Contains("model.");
    }

    private bool IsEntityType(ITypeSymbol? type)
    {
        if (type == null) return false;

        // Check for common entity indicators
        var name = type.ToDisplayString();
        if (name.EndsWith("Entity") || name.Contains(".Entities.") || name.Contains(".Models."))
            return true;

        // Check for EF Core attributes
        if (type is INamedTypeSymbol namedType)
        {
            foreach (var attr in namedType.GetAttributes())
            {
                var attrName = attr.AttributeClass?.Name ?? "";
                if (attrName is "Table" or "Key" or "Column")
                    return true;
            }

            // Check for navigation properties (typical of entities)
            var members = namedType.GetMembers();
            var hasCollectionNav = members.Any(m =>
                m is IPropertySymbol p &&
                p.Type.ToDisplayString().StartsWith("System.Collections"));
            var hasIdProperty = members.Any(m =>
                m is IPropertySymbol p &&
                (p.Name == "Id" || p.Name.EndsWith("Id")));

            if (hasCollectionNav && hasIdProperty)
                return true;
        }

        return false;
    }
}

public record FrameworkAnalysisResult
{
    public required string Framework { get; init; }
    public int TotalIssues { get; init; }
    public int CriticalCount { get; init; }
    public int HighCount { get; init; }
    public int MediumCount { get; init; }
    public List<FrameworkIssue> Issues { get; init; } = [];
    public Dictionary<string, int> IssuesByType { get; init; } = [];
}

public record FrameworkIssue
{
    public required string IssueType { get; init; }
    public required string Severity { get; init; }
    public required string Message { get; init; }
    public required string FilePath { get; init; }
    public required int Line { get; init; }
    public string? CweId { get; init; }
    public string? SuggestedFix { get; init; }
    public string? CodeSnippet { get; init; }
}
