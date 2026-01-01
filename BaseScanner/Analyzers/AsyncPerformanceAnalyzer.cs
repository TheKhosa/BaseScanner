using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BaseScanner.Analyzers;

public class AsyncPerformanceAnalyzer
{
    public record Issue
    {
        public required string Type { get; init; }
        public required string Severity { get; init; } // Critical, Warning, Info
        public required string Message { get; init; }
        public required string FilePath { get; init; }
        public required int Line { get; init; }
        public required string CodeSnippet { get; init; }
    }

    public async Task<List<Issue>> AnalyzeAsync(Project project)
    {
        var issues = new List<Issue>();

        foreach (var document in project.Documents)
        {
            if (document.FilePath == null) continue;
            if (document.FilePath.Contains(".Designer.cs")) continue; // Skip designer files

            var syntaxRoot = await document.GetSyntaxRootAsync();
            var semanticModel = await document.GetSemanticModelAsync();
            if (syntaxRoot == null || semanticModel == null) continue;

            // Check for async void methods
            issues.AddRange(FindAsyncVoidMethods(syntaxRoot, document.FilePath));

            // Check for .Result and .Wait() calls (potential deadlocks)
            issues.AddRange(FindBlockingAsyncCalls(syntaxRoot, semanticModel, document.FilePath));

            // Check for missing ConfigureAwait in library code
            issues.AddRange(FindMissingConfigureAwait(syntaxRoot, document.FilePath));

            // Check for LINQ in loops
            issues.AddRange(FindLinqInLoops(syntaxRoot, document.FilePath));

            // Check for string concatenation in loops
            issues.AddRange(FindStringConcatInLoops(syntaxRoot, document.FilePath));

            // Check for inefficient collection usage
            issues.AddRange(FindInefficientCollectionUsage(syntaxRoot, semanticModel, document.FilePath));
        }

        return issues;
    }

    private IEnumerable<Issue> FindAsyncVoidMethods(SyntaxNode root, string filePath)
    {
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.Modifiers.Any(SyntaxKind.AsyncKeyword) &&
                       m.ReturnType.ToString() == "void");

        foreach (var method in methods)
        {
            // Allow async void for event handlers (common pattern)
            var isEventHandler = method.ParameterList.Parameters.Count == 2 &&
                                method.ParameterList.Parameters[1].Type?.ToString().Contains("EventArgs") == true;

            if (!isEventHandler)
            {
                yield return new Issue
                {
                    Type = "AsyncVoid",
                    Severity = "Critical",
                    Message = $"async void method '{method.Identifier.Text}' - exceptions cannot be caught, use async Task instead",
                    FilePath = filePath,
                    Line = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    CodeSnippet = $"async void {method.Identifier.Text}(...)"
                };
            }
        }
    }

    private IEnumerable<Issue> FindBlockingAsyncCalls(SyntaxNode root, SemanticModel semanticModel, string filePath)
    {
        // Find .Result property access
        var memberAccesses = root.DescendantNodes().OfType<MemberAccessExpressionSyntax>();

        foreach (var access in memberAccesses)
        {
            var name = access.Name.Identifier.Text;

            if (name == "Result")
            {
                var typeInfo = semanticModel.GetTypeInfo(access.Expression);
                var typeName = typeInfo.Type?.ToDisplayString() ?? "";

                if (typeName.StartsWith("System.Threading.Tasks.Task"))
                {
                    yield return new Issue
                    {
                        Type = "BlockingAsync",
                        Severity = "Critical",
                        Message = "Using .Result on Task can cause deadlocks - use 'await' instead",
                        FilePath = filePath,
                        Line = access.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        CodeSnippet = access.ToString()
                    };
                }
            }
        }

        // Find .Wait() method calls
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var invocation in invocations)
        {
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                var methodName = memberAccess.Name.Identifier.Text;

                if (methodName == "Wait" || methodName == "GetAwaiter")
                {
                    var typeInfo = semanticModel.GetTypeInfo(memberAccess.Expression);
                    var typeName = typeInfo.Type?.ToDisplayString() ?? "";

                    if (typeName.StartsWith("System.Threading.Tasks.Task"))
                    {
                        yield return new Issue
                        {
                            Type = "BlockingAsync",
                            Severity = "Critical",
                            Message = $"Using .{methodName}() on Task can cause deadlocks - use 'await' instead",
                            FilePath = filePath,
                            Line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                            CodeSnippet = invocation.ToString().Substring(0, Math.Min(50, invocation.ToString().Length))
                        };
                    }
                }
            }
        }
    }

    private IEnumerable<Issue> FindMissingConfigureAwait(SyntaxNode root, string filePath)
    {
        var awaitExpressions = root.DescendantNodes().OfType<AwaitExpressionSyntax>();

        foreach (var await in awaitExpressions)
        {
            var awaitedExpr = await.Expression.ToString();

            // Skip if already has ConfigureAwait
            if (awaitedExpr.Contains("ConfigureAwait"))
                continue;

            // This is more of an info/style issue for library code
            // Only report in non-UI contexts (heuristic: if file doesn't contain Form/Control/Window)
            var fileContent = root.ToString();
            if (!fileContent.Contains(": Form") && !fileContent.Contains(": Control") &&
                !fileContent.Contains(": Window") && !fileContent.Contains("UserControl"))
            {
                // Skip for now - too noisy in app code
                // yield return new Issue { ... }
            }
        }

        return Enumerable.Empty<Issue>();
    }

    private IEnumerable<Issue> FindLinqInLoops(SyntaxNode root, string filePath)
    {
        var loops = root.DescendantNodes()
            .Where(n => n is ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax);

        foreach (var loop in loops)
        {
            var linqCalls = loop.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(inv => inv.Expression is MemberAccessExpressionSyntax memberAccess &&
                             IsLinqMethod(memberAccess.Name.Identifier.Text));

            foreach (var linq in linqCalls)
            {
                var methodName = ((MemberAccessExpressionSyntax)linq.Expression).Name.Identifier.Text;

                // Skip if it's just accessing a pre-computed result
                if (methodName == "First" || methodName == "FirstOrDefault" ||
                    methodName == "Single" || methodName == "SingleOrDefault" ||
                    methodName == "Any" || methodName == "Count")
                {
                    // These are often acceptable in loops
                    continue;
                }

                yield return new Issue
                {
                    Type = "LinqInLoop",
                    Severity = "Warning",
                    Message = $"LINQ method '{methodName}' inside loop - consider moving outside loop or caching result",
                    FilePath = filePath,
                    Line = linq.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    CodeSnippet = linq.ToString().Substring(0, Math.Min(60, linq.ToString().Length))
                };
            }
        }
    }

    private bool IsLinqMethod(string name)
    {
        return name is "Where" or "Select" or "SelectMany" or "OrderBy" or "OrderByDescending" or
            "GroupBy" or "ToList" or "ToArray" or "ToDictionary" or "ToHashSet" or
            "Distinct" or "Union" or "Intersect" or "Except" or "Concat" or
            "Skip" or "Take" or "SkipWhile" or "TakeWhile";
    }

    private IEnumerable<Issue> FindStringConcatInLoops(SyntaxNode root, string filePath)
    {
        var loops = root.DescendantNodes()
            .Where(n => n is ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax);

        foreach (var loop in loops)
        {
            // Find += with string
            var assignments = loop.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Where(a => a.IsKind(SyntaxKind.AddAssignmentExpression));

            foreach (var assignment in assignments)
            {
                var leftType = assignment.Left.ToString();
                var rightSide = assignment.Right.ToString();

                // Heuristic: if the variable name suggests it's a string
                if (leftType.Contains("str", StringComparison.OrdinalIgnoreCase) ||
                    leftType.Contains("text", StringComparison.OrdinalIgnoreCase) ||
                    leftType.Contains("msg", StringComparison.OrdinalIgnoreCase) ||
                    leftType.Contains("result", StringComparison.OrdinalIgnoreCase) ||
                    leftType.Contains("output", StringComparison.OrdinalIgnoreCase) ||
                    rightSide.Contains("\"") || rightSide.Contains("ToString"))
                {
                    yield return new Issue
                    {
                        Type = "StringConcatInLoop",
                        Severity = "Warning",
                        Message = "String concatenation in loop - consider using StringBuilder",
                        FilePath = filePath,
                        Line = assignment.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        CodeSnippet = assignment.ToString().Substring(0, Math.Min(50, assignment.ToString().Length))
                    };
                }
            }

            // Find string + string in loop
            var binaryAdds = loop.DescendantNodes().OfType<BinaryExpressionSyntax>()
                .Where(b => b.IsKind(SyntaxKind.AddExpression) &&
                           (b.Left.ToString().Contains("\"") || b.Right.ToString().Contains("\"")));

            // Only report if there are multiple string concatenations
            if (binaryAdds.Count() > 2)
            {
                var first = binaryAdds.First();
                yield return new Issue
                {
                    Type = "StringConcatInLoop",
                    Severity = "Info",
                    Message = "Multiple string concatenations in loop - consider StringBuilder",
                    FilePath = filePath,
                    Line = first.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    CodeSnippet = "Multiple + operations with strings"
                };
            }
        }
    }

    private IEnumerable<Issue> FindInefficientCollectionUsage(SyntaxNode root, SemanticModel semanticModel, string filePath)
    {
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var invocation in invocations)
        {
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                var methodName = memberAccess.Name.Identifier.Text;

                // Check for .Count() > 0 instead of .Any()
                if (methodName == "Count")
                {
                    var parent = invocation.Parent;
                    if (parent is BinaryExpressionSyntax binary)
                    {
                        var op = binary.OperatorToken.Text;
                        var right = binary.Right.ToString();

                        if ((op == ">" && right == "0") || (op == "!=" && right == "0") ||
                            (op == ">=" && right == "1"))
                        {
                            yield return new Issue
                            {
                                Type = "InefficientCollection",
                                Severity = "Info",
                                Message = "Use .Any() instead of .Count() > 0 for better performance",
                                FilePath = filePath,
                                Line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                                CodeSnippet = binary.ToString()
                            };
                        }
                    }
                }

                // Check for .ToList().ForEach() instead of foreach
                if (methodName == "ForEach")
                {
                    var expr = memberAccess.Expression.ToString();
                    if (expr.Contains("ToList()") || expr.Contains("ToArray()"))
                    {
                        yield return new Issue
                        {
                            Type = "InefficientCollection",
                            Severity = "Info",
                            Message = "Unnecessary ToList()/ToArray() before ForEach - use foreach loop instead",
                            FilePath = filePath,
                            Line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                            CodeSnippet = invocation.ToString().Substring(0, Math.Min(60, invocation.ToString().Length))
                        };
                    }
                }
            }
        }
    }
}
