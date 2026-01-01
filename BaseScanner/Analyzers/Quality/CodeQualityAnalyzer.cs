using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using BaseScanner.VirtualWorkspace;

namespace BaseScanner.Analyzers.Quality;

/// <summary>
/// Comprehensive code quality analyzer including code smells, testability, and design issues.
/// </summary>
public class CodeQualityAnalyzer
{
    private const int MaxParameters = 5;
    private const int MaxNestingDepth = 4;
    private const int MaxMethodLines = 30;
    private const int MaxClassMethods = 20;
    private const int MaxClassLines = 500;
    private const int MaxLocals = 10;
    private const int CognitiveComplexityThreshold = 15;

    public async Task<CodeQualityResult> AnalyzeAsync(Project project)
    {
        var issues = new List<CodeQualityIssue>();
        var metrics = new List<MethodMetrics>();

        foreach (var document in project.Documents)
        {
            var semanticModel = await document.GetSemanticModelAsync();
            var root = await document.GetSyntaxRootAsync();

            if (semanticModel == null || root == null) continue;

            var (docIssues, docMetrics) = await AnalyzeDocumentAsync(document, semanticModel, root);
            issues.AddRange(docIssues);
            metrics.AddRange(docMetrics);
        }

        return new CodeQualityResult
        {
            TotalIssues = issues.Count,
            Issues = issues,
            MethodMetrics = metrics,
            IssuesByCategory = issues.GroupBy(i => i.Category).ToDictionary(g => g.Key, g => g.Count()),
            AverageCognitiveComplexity = metrics.Any() ? metrics.Average(m => m.CognitiveComplexity) : 0,
            MethodsAboveThreshold = metrics.Count(m => m.CognitiveComplexity > CognitiveComplexityThreshold)
        };
    }

    private async Task<(List<CodeQualityIssue>, List<MethodMetrics>)> AnalyzeDocumentAsync(
        Document document, SemanticModel model, SyntaxNode root)
    {
        var issues = new List<CodeQualityIssue>();
        var metrics = new List<MethodMetrics>();

        // Class-level analysis
        foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            issues.AddRange(AnalyzeClass(classDecl, document, model));
        }

        // Method-level analysis
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var (methodIssues, methodMetrics) = AnalyzeMethod(method, document, model);
            issues.AddRange(methodIssues);
            if (methodMetrics != null) metrics.Add(methodMetrics);
        }

        // Error handling analysis
        issues.AddRange(AnalyzeExceptionHandling(root, document, model));

        // Testability analysis
        issues.AddRange(AnalyzeTestability(root, document, model));

        // Design issues
        issues.AddRange(AnalyzeDesign(root, document, model));

        return (issues, metrics);
    }

    private List<CodeQualityIssue> AnalyzeClass(ClassDeclarationSyntax classDecl, Document document, SemanticModel model)
    {
        var issues = new List<CodeQualityIssue>();
        var location = classDecl.Identifier.GetLocation().GetLineSpan();
        var className = classDecl.Identifier.Text;

        // Count methods and lines
        var methods = classDecl.Members.OfType<MethodDeclarationSyntax>().ToList();
        var properties = classDecl.Members.OfType<PropertyDeclarationSyntax>().ToList();
        var fields = classDecl.Members.OfType<FieldDeclarationSyntax>().ToList();
        var lineCount = classDecl.GetText().Lines.Count;

        // Too many methods
        if (methods.Count > MaxClassMethods)
        {
            issues.Add(new CodeQualityIssue
            {
                Category = "CodeSmell",
                IssueType = "GodClass",
                Severity = "Medium",
                Message = $"Class '{className}' has {methods.Count} methods (max {MaxClassMethods}) - consider splitting",
                FilePath = document.FilePath ?? "",
                Line = location.StartLinePosition.Line + 1,
                Suggestion = "Extract related methods into focused classes"
            });
        }

        // Too many lines
        if (lineCount > MaxClassLines)
        {
            issues.Add(new CodeQualityIssue
            {
                Category = "CodeSmell",
                IssueType = "LargeClass",
                Severity = "Medium",
                Message = $"Class '{className}' has {lineCount} lines (max {MaxClassLines})",
                FilePath = document.FilePath ?? "",
                Line = location.StartLinePosition.Line + 1,
                Suggestion = "Split into smaller, focused classes"
            });
        }

        // Check for primitive obsession in properties
        var primitiveCount = properties.Count(p =>
        {
            var typeName = p.Type.ToString();
            return typeName is "string" or "int" or "long" or "double" or "bool";
        });

        if (primitiveCount > 10 && properties.Count > 0)
        {
            var ratio = (double)primitiveCount / properties.Count;
            if (ratio > 0.8)
            {
                issues.Add(new CodeQualityIssue
                {
                    Category = "CodeSmell",
                    IssueType = "PrimitiveObsession",
                    Severity = "Low",
                    Message = $"Class '{className}' has many primitive properties - consider value objects",
                    FilePath = document.FilePath ?? "",
                    Line = location.StartLinePosition.Line + 1,
                    Suggestion = "Group related primitives into value objects (e.g., Email, PhoneNumber, Address)"
                });
            }
        }

        return issues;
    }

    private (List<CodeQualityIssue>, MethodMetrics?) AnalyzeMethod(
        MethodDeclarationSyntax method, Document document, SemanticModel model)
    {
        var issues = new List<CodeQualityIssue>();
        var location = method.Identifier.GetLocation().GetLineSpan();
        var methodName = method.Identifier.Text;

        // Calculate metrics
        var lineCount = method.Body?.GetText().Lines.Count ?? method.ExpressionBody?.GetText().Lines.Count ?? 0;
        var paramCount = method.ParameterList.Parameters.Count;
        var nestingDepth = CalculateMaxNesting(method);
        var cognitiveComplexity = TransformationScorer.CalculateCognitiveComplexity(method);
        var cyclomaticComplexity = TransformationScorer.CalculateCyclomaticComplexity(method);
        var localCount = method.DescendantNodes().OfType<LocalDeclarationStatementSyntax>().Count();

        var metrics = new MethodMetrics
        {
            MethodName = methodName,
            FilePath = document.FilePath ?? "",
            Line = location.StartLinePosition.Line + 1,
            LineCount = lineCount,
            ParameterCount = paramCount,
            NestingDepth = nestingDepth,
            CognitiveComplexity = cognitiveComplexity,
            CyclomaticComplexity = cyclomaticComplexity,
            LocalVariableCount = localCount
        };

        // Too many parameters
        if (paramCount > MaxParameters)
        {
            issues.Add(new CodeQualityIssue
            {
                Category = "CodeSmell",
                IssueType = "TooManyParameters",
                Severity = "Medium",
                Message = $"Method '{methodName}' has {paramCount} parameters (max {MaxParameters})",
                FilePath = document.FilePath ?? "",
                Line = location.StartLinePosition.Line + 1,
                Suggestion = "Create a parameter object or split into multiple methods"
            });
        }

        // Check for boolean parameters (flag arguments)
        var boolParams = method.ParameterList.Parameters.Count(p => p.Type?.ToString() == "bool");
        if (boolParams >= 2)
        {
            issues.Add(new CodeQualityIssue
            {
                Category = "CodeSmell",
                IssueType = "BooleanParameters",
                Severity = "Low",
                Message = $"Method '{methodName}' has {boolParams} boolean parameters - reduces readability",
                FilePath = document.FilePath ?? "",
                Line = location.StartLinePosition.Line + 1,
                Suggestion = "Use an enum or options class instead of multiple booleans"
            });
        }

        // Too many lines
        if (lineCount > MaxMethodLines)
        {
            issues.Add(new CodeQualityIssue
            {
                Category = "CodeSmell",
                IssueType = "LongMethod",
                Severity = "Medium",
                Message = $"Method '{methodName}' has {lineCount} lines (max {MaxMethodLines})",
                FilePath = document.FilePath ?? "",
                Line = location.StartLinePosition.Line + 1,
                Suggestion = "Extract logical sections into separate methods"
            });
        }

        // Deep nesting
        if (nestingDepth > MaxNestingDepth)
        {
            issues.Add(new CodeQualityIssue
            {
                Category = "CodeSmell",
                IssueType = "DeepNesting",
                Severity = "Medium",
                Message = $"Method '{methodName}' has nesting depth of {nestingDepth} (max {MaxNestingDepth})",
                FilePath = document.FilePath ?? "",
                Line = location.StartLinePosition.Line + 1,
                Suggestion = "Use early returns, extract methods, or restructure logic"
            });
        }

        // High cognitive complexity
        if (cognitiveComplexity > CognitiveComplexityThreshold)
        {
            issues.Add(new CodeQualityIssue
            {
                Category = "Complexity",
                IssueType = "HighCognitiveComplexity",
                Severity = "High",
                Message = $"Method '{methodName}' has cognitive complexity of {cognitiveComplexity} (threshold {CognitiveComplexityThreshold})",
                FilePath = document.FilePath ?? "",
                Line = location.StartLinePosition.Line + 1,
                Suggestion = "Simplify control flow, extract methods, reduce nesting"
            });
        }

        // Too many local variables
        if (localCount > MaxLocals)
        {
            issues.Add(new CodeQualityIssue
            {
                Category = "CodeSmell",
                IssueType = "TooManyLocals",
                Severity = "Low",
                Message = $"Method '{methodName}' has {localCount} local variables (max {MaxLocals})",
                FilePath = document.FilePath ?? "",
                Line = location.StartLinePosition.Line + 1,
                Suggestion = "Extract related logic into separate methods"
            });
        }

        // Check for switch on type
        foreach (var switchStmt in method.DescendantNodes().OfType<SwitchStatementSyntax>())
        {
            if (IsSwitchOnType(switchStmt))
            {
                var switchLoc = switchStmt.GetLocation().GetLineSpan();
                issues.Add(new CodeQualityIssue
                {
                    Category = "Design",
                    IssueType = "SwitchOnType",
                    Severity = "Medium",
                    Message = "Switch on type detected - consider using polymorphism",
                    FilePath = document.FilePath ?? "",
                    Line = switchLoc.StartLinePosition.Line + 1,
                    Suggestion = "Use polymorphism or the visitor pattern instead"
                });
            }
        }

        return (issues, metrics);
    }

    private List<CodeQualityIssue> AnalyzeExceptionHandling(SyntaxNode root, Document document, SemanticModel model)
    {
        var issues = new List<CodeQualityIssue>();

        foreach (var catchClause in root.DescendantNodes().OfType<CatchClauseSyntax>())
        {
            var location = catchClause.GetLocation().GetLineSpan();

            // Empty catch block
            if (catchClause.Block.Statements.Count == 0)
            {
                issues.Add(new CodeQualityIssue
                {
                    Category = "ErrorHandling",
                    IssueType = "EmptyCatch",
                    Severity = "High",
                    Message = "Empty catch block swallows exceptions silently",
                    FilePath = document.FilePath ?? "",
                    Line = location.StartLinePosition.Line + 1,
                    Suggestion = "Log the exception or rethrow"
                });
                continue;
            }

            // Catch only contains return
            if (catchClause.Block.Statements.Count == 1 &&
                catchClause.Block.Statements[0] is ReturnStatementSyntax)
            {
                issues.Add(new CodeQualityIssue
                {
                    Category = "ErrorHandling",
                    IssueType = "CatchAndReturn",
                    Severity = "Medium",
                    Message = "Catch block only returns - exception details may be lost",
                    FilePath = document.FilePath ?? "",
                    Line = location.StartLinePosition.Line + 1,
                    Suggestion = "Log the exception before returning"
                });
            }

            // Check for throw ex vs throw
            foreach (var throwStmt in catchClause.Block.DescendantNodes().OfType<ThrowStatementSyntax>())
            {
                if (throwStmt.Expression is IdentifierNameSyntax id &&
                    catchClause.Declaration?.Identifier.Text == id.Identifier.Text)
                {
                    var throwLoc = throwStmt.GetLocation().GetLineSpan();
                    issues.Add(new CodeQualityIssue
                    {
                        Category = "ErrorHandling",
                        IssueType = "LostStackTrace",
                        Severity = "High",
                        Message = "'throw ex' loses the original stack trace",
                        FilePath = document.FilePath ?? "",
                        Line = throwLoc.StartLinePosition.Line + 1,
                        Suggestion = "Use 'throw' instead of 'throw ex'"
                    });
                }
            }

            // Catch generic Exception
            var exceptionType = catchClause.Declaration?.Type.ToString() ?? "";
            if (exceptionType == "Exception" || exceptionType == "System.Exception")
            {
                issues.Add(new CodeQualityIssue
                {
                    Category = "ErrorHandling",
                    IssueType = "CatchGenericException",
                    Severity = "Medium",
                    Message = "Catching generic Exception - may hide programming errors",
                    FilePath = document.FilePath ?? "",
                    Line = location.StartLinePosition.Line + 1,
                    Suggestion = "Catch specific exception types"
                });
            }
        }

        // Check for throw in finally
        foreach (var finallyClause in root.DescendantNodes().OfType<FinallyClauseSyntax>())
        {
            foreach (var throwStmt in finallyClause.DescendantNodes().OfType<ThrowStatementSyntax>())
            {
                var location = throwStmt.GetLocation().GetLineSpan();
                issues.Add(new CodeQualityIssue
                {
                    Category = "ErrorHandling",
                    IssueType = "ThrowInFinally",
                    Severity = "High",
                    Message = "Throw in finally block may mask original exception",
                    FilePath = document.FilePath ?? "",
                    Line = location.StartLinePosition.Line + 1,
                    Suggestion = "Remove throw from finally block or handle differently"
                });
            }
        }

        return issues;
    }

    private List<CodeQualityIssue> AnalyzeTestability(SyntaxNode root, Document document, SemanticModel model)
    {
        var issues = new List<CodeQualityIssue>();

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            // Static method calls (hard to mock)
            foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is MemberAccessExpressionSyntax ma)
                {
                    var symbol = model.GetSymbolInfo(ma).Symbol;
                    if (symbol is IMethodSymbol methodSymbol && methodSymbol.IsStatic)
                    {
                        var typeName = methodSymbol.ContainingType?.Name ?? "";

                        // Common static calls that hurt testability
                        if (typeName is "DateTime" or "File" or "Directory" or "Environment" or "Console")
                        {
                            var location = invocation.GetLocation().GetLineSpan();
                            issues.Add(new CodeQualityIssue
                            {
                                Category = "Testability",
                                IssueType = "StaticDependency",
                                Severity = "Low",
                                Message = $"Static call to {typeName}.{methodSymbol.Name} - hard to test",
                                FilePath = document.FilePath ?? "",
                                Line = location.StartLinePosition.Line + 1,
                                Suggestion = $"Inject an abstraction (e.g., ITimeProvider, IFileSystem)"
                            });
                        }
                    }
                }
            }

            // Direct instantiation of services
            foreach (var creation in method.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                var typeInfo = model.GetTypeInfo(creation);
                var typeName = typeInfo.Type?.Name ?? "";

                // Check for common service patterns
                if (typeName.EndsWith("Service") || typeName.EndsWith("Repository") ||
                    typeName.EndsWith("Client") || typeName.EndsWith("Manager"))
                {
                    var location = creation.GetLocation().GetLineSpan();
                    issues.Add(new CodeQualityIssue
                    {
                        Category = "Testability",
                        IssueType = "DirectInstantiation",
                        Severity = "Medium",
                        Message = $"Direct instantiation of {typeName} - use dependency injection",
                        FilePath = document.FilePath ?? "",
                        Line = location.StartLinePosition.Line + 1,
                        Suggestion = "Inject through constructor instead of instantiating directly"
                    });
                }
            }
        }

        // Sealed classes without interfaces (hard to mock)
        foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (classDecl.Modifiers.Any(SyntaxKind.SealedKeyword) &&
                classDecl.BaseList?.Types.Count == 0)
            {
                var className = classDecl.Identifier.Text;
                if (className.EndsWith("Service") || className.EndsWith("Repository"))
                {
                    var location = classDecl.Identifier.GetLocation().GetLineSpan();
                    issues.Add(new CodeQualityIssue
                    {
                        Category = "Testability",
                        IssueType = "SealedWithoutInterface",
                        Severity = "Low",
                        Message = $"Sealed class '{className}' without interface - hard to mock",
                        FilePath = document.FilePath ?? "",
                        Line = location.StartLinePosition.Line + 1,
                        Suggestion = "Either unseal or add an interface for testing"
                    });
                }
            }
        }

        return issues;
    }

    private List<CodeQualityIssue> AnalyzeDesign(SyntaxNode root, Document document, SemanticModel model)
    {
        var issues = new List<CodeQualityIssue>();

        // Check for mutable public arrays/collections
        foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
        {
            if (!property.Modifiers.Any(SyntaxKind.PublicKeyword)) continue;

            var typeName = property.Type.ToString();

            // Array property
            if (typeName.EndsWith("[]"))
            {
                if (!property.Modifiers.Any(SyntaxKind.ReadOnlyKeyword) &&
                    property.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)) == true)
                {
                    var location = property.Identifier.GetLocation().GetLineSpan();
                    issues.Add(new CodeQualityIssue
                    {
                        Category = "Design",
                        IssueType = "MutablePublicArray",
                        Severity = "Medium",
                        Message = $"Public mutable array property '{property.Identifier.Text}'",
                        FilePath = document.FilePath ?? "",
                        Line = location.StartLinePosition.Line + 1,
                        Suggestion = "Return IReadOnlyList<T> or defensive copy"
                    });
                }
            }

            // List/Collection property
            if (typeName.StartsWith("List<") || typeName.StartsWith("IList<"))
            {
                var location = property.Identifier.GetLocation().GetLineSpan();
                issues.Add(new CodeQualityIssue
                {
                    Category = "Design",
                    IssueType = "MutablePublicCollection",
                    Severity = "Low",
                    Message = $"Public mutable collection property '{property.Identifier.Text}'",
                    FilePath = document.FilePath ?? "",
                    Line = location.StartLinePosition.Line + 1,
                    Suggestion = "Use IReadOnlyList<T> or IReadOnlyCollection<T>"
                });
            }
        }

        // Check for virtual calls in constructors
        foreach (var ctor in root.DescendantNodes().OfType<ConstructorDeclarationSyntax>())
        {
            foreach (var invocation in ctor.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var symbol = model.GetSymbolInfo(invocation).Symbol;
                if (symbol is IMethodSymbol methodSymbol && methodSymbol.IsVirtual)
                {
                    var location = invocation.GetLocation().GetLineSpan();
                    issues.Add(new CodeQualityIssue
                    {
                        Category = "Design",
                        IssueType = "VirtualCallInConstructor",
                        Severity = "Medium",
                        Message = $"Virtual method '{methodSymbol.Name}' called in constructor",
                        FilePath = document.FilePath ?? "",
                        Line = location.StartLinePosition.Line + 1,
                        Suggestion = "Avoid virtual calls in constructors - derived class not yet initialized"
                    });
                }
            }
        }

        // Check for hiding base members
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (method.Modifiers.Any(SyntaxKind.NewKeyword))
            {
                var location = method.Identifier.GetLocation().GetLineSpan();
                issues.Add(new CodeQualityIssue
                {
                    Category = "Design",
                    IssueType = "HidingBaseMethod",
                    Severity = "Medium",
                    Message = $"Method '{method.Identifier.Text}' hides base method with 'new'",
                    FilePath = document.FilePath ?? "",
                    Line = location.StartLinePosition.Line + 1,
                    Suggestion = "Use 'override' or rename the method to avoid confusion"
                });
            }
        }

        return issues;
    }

    // Helper methods
    private int CalculateMaxNesting(MethodDeclarationSyntax method)
    {
        var maxDepth = 0;
        var currentDepth = 0;

        void Walk(SyntaxNode node)
        {
            foreach (var child in node.ChildNodes())
            {
                if (child is BlockSyntax or IfStatementSyntax or ForStatementSyntax or
                    ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax or
                    SwitchStatementSyntax or TryStatementSyntax)
                {
                    currentDepth++;
                    maxDepth = Math.Max(maxDepth, currentDepth);
                    Walk(child);
                    currentDepth--;
                }
                else
                {
                    Walk(child);
                }
            }
        }

        Walk(method);
        return maxDepth;
    }

    private bool IsSwitchOnType(SwitchStatementSyntax switchStmt)
    {
        var expr = switchStmt.Expression.ToString();
        return expr.Contains("GetType") || expr.Contains(".GetType()");
    }
}

public record CodeQualityResult
{
    public int TotalIssues { get; init; }
    public List<CodeQualityIssue> Issues { get; init; } = [];
    public List<MethodMetrics> MethodMetrics { get; init; } = [];
    public Dictionary<string, int> IssuesByCategory { get; init; } = [];
    public double AverageCognitiveComplexity { get; init; }
    public int MethodsAboveThreshold { get; init; }
}

public record CodeQualityIssue
{
    public required string Category { get; init; }
    public required string IssueType { get; init; }
    public required string Severity { get; init; }
    public required string Message { get; init; }
    public required string FilePath { get; init; }
    public required int Line { get; init; }
    public string? Suggestion { get; init; }
    public string? CweId { get; init; }
}

public record MethodMetrics
{
    public required string MethodName { get; init; }
    public required string FilePath { get; init; }
    public required int Line { get; init; }
    public int LineCount { get; init; }
    public int ParameterCount { get; init; }
    public int NestingDepth { get; init; }
    public int CognitiveComplexity { get; init; }
    public int CyclomaticComplexity { get; init; }
    public int LocalVariableCount { get; init; }
}
