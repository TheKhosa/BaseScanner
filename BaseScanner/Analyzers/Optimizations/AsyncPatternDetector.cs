using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using BaseScanner.Context;

namespace BaseScanner.Analyzers.Optimizations;

/// <summary>
/// Detects async/await patterns that can be improved.
/// </summary>
public class AsyncPatternDetector : IOptimizationDetector
{
    public string Category => "Performance";

    public Task<List<OptimizationOpportunity>> DetectAsync(
        Document document,
        SemanticModel semanticModel,
        SyntaxNode root,
        CodeContext context)
    {
        var opportunities = new List<OptimizationOpportunity>();
        var filePath = document.FilePath ?? "";

        // Detect async void methods (except event handlers)
        DetectAsyncVoid(root, semanticModel, filePath, opportunities);

        // Detect .Result or .Wait() blocking calls
        DetectBlockingCalls(root, semanticModel, filePath, opportunities);

        // Detect missing ConfigureAwait in library code
        DetectMissingConfigureAwait(root, semanticModel, filePath, opportunities);

        // Detect unnecessary async/await
        DetectUnnecessaryAsync(root, semanticModel, filePath, opportunities);

        return Task.FromResult(opportunities);
    }

    private void DetectAsyncVoid(
        SyntaxNode root,
        SemanticModel semanticModel,
        string filePath,
        List<OptimizationOpportunity> opportunities)
    {
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            // Check if async void
            if (!method.Modifiers.Any(SyntaxKind.AsyncKeyword))
                continue;

            if (method.ReturnType is not PredefinedTypeSyntax predefined ||
                predefined.Keyword.Kind() != SyntaxKind.VoidKeyword)
                continue;

            // Exception: event handlers are acceptable as async void
            var symbol = semanticModel.GetDeclaredSymbol(method);
            if (symbol == null)
                continue;

            if (IsEventHandler(symbol))
                continue;

            var lineSpan = method.GetLocation().GetLineSpan();
            var methodSignature = $"async void {method.Identifier.Text}";
            var suggestedSignature = $"async Task {method.Identifier.Text}";

            opportunities.Add(new OptimizationOpportunity
            {
                Category = Category,
                Type = "AsyncVoid",
                FilePath = filePath,
                StartLine = lineSpan.StartLinePosition.Line + 1,
                EndLine = lineSpan.StartLinePosition.Line + 1,
                Description = "async void methods cannot be awaited and exceptions are difficult to handle. Use async Task instead.",
                CurrentCode = methodSignature,
                SuggestedCode = suggestedSignature,
                Confidence = OptimizationConfidence.High,
                Impact = OptimizationImpact.High,
                IsSemanticallySafe = false,
                Risks = ["Callers may need to be updated to await the method"]
            });
        }
    }

    private void DetectBlockingCalls(
        SyntaxNode root,
        SemanticModel semanticModel,
        string filePath,
        List<OptimizationOpportunity> opportunities)
    {
        // Detect .Result
        foreach (var memberAccess in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            if (memberAccess.Name.Identifier.Text != "Result")
                continue;

            var expressionType = semanticModel.GetTypeInfo(memberAccess.Expression).Type;
            if (expressionType == null)
                continue;

            var typeName = expressionType.ToDisplayString();
            if (!typeName.StartsWith("System.Threading.Tasks.Task"))
                continue;

            var lineSpan = memberAccess.GetLocation().GetLineSpan();
            var currentCode = memberAccess.ToFullString().Trim();
            var taskExpr = memberAccess.Expression.ToFullString().Trim();

            opportunities.Add(new OptimizationOpportunity
            {
                Category = Category,
                Type = "TaskResult",
                FilePath = filePath,
                StartLine = lineSpan.StartLinePosition.Line + 1,
                EndLine = lineSpan.EndLinePosition.Line + 1,
                Description = ".Result blocks the calling thread and can cause deadlocks. Use await instead.",
                CurrentCode = currentCode,
                SuggestedCode = $"await {taskExpr}",
                Confidence = OptimizationConfidence.High,
                Impact = OptimizationImpact.Critical,
                IsSemanticallySafe = false,
                Risks = ["Method must be made async", "May require broader refactoring"]
            });
        }

        // Detect .Wait()
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                continue;

            if (memberAccess.Name.Identifier.Text != "Wait")
                continue;

            var expressionType = semanticModel.GetTypeInfo(memberAccess.Expression).Type;
            if (expressionType == null)
                continue;

            var typeName = expressionType.ToDisplayString();
            if (!typeName.StartsWith("System.Threading.Tasks.Task"))
                continue;

            var lineSpan = invocation.GetLocation().GetLineSpan();
            var currentCode = invocation.ToFullString().Trim();
            var taskExpr = memberAccess.Expression.ToFullString().Trim();

            opportunities.Add(new OptimizationOpportunity
            {
                Category = Category,
                Type = "TaskWait",
                FilePath = filePath,
                StartLine = lineSpan.StartLinePosition.Line + 1,
                EndLine = lineSpan.EndLinePosition.Line + 1,
                Description = ".Wait() blocks the calling thread and can cause deadlocks. Use await instead.",
                CurrentCode = currentCode,
                SuggestedCode = $"await {taskExpr}",
                Confidence = OptimizationConfidence.High,
                Impact = OptimizationImpact.Critical,
                IsSemanticallySafe = false,
                Risks = ["Method must be made async", "May require broader refactoring"]
            });
        }

        // Detect .GetAwaiter().GetResult()
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                continue;

            if (memberAccess.Name.Identifier.Text != "GetResult")
                continue;

            if (memberAccess.Expression is not InvocationExpressionSyntax getAwaiter)
                continue;

            if (getAwaiter.Expression is not MemberAccessExpressionSyntax awaiterAccess)
                continue;

            if (awaiterAccess.Name.Identifier.Text != "GetAwaiter")
                continue;

            var taskExpr = awaiterAccess.Expression;
            var expressionType = semanticModel.GetTypeInfo(taskExpr).Type;
            if (expressionType == null)
                continue;

            var typeName = expressionType.ToDisplayString();
            if (!typeName.StartsWith("System.Threading.Tasks.Task"))
                continue;

            var lineSpan = invocation.GetLocation().GetLineSpan();
            var currentCode = invocation.ToFullString().Trim();

            opportunities.Add(new OptimizationOpportunity
            {
                Category = Category,
                Type = "GetAwaiterGetResult",
                FilePath = filePath,
                StartLine = lineSpan.StartLinePosition.Line + 1,
                EndLine = lineSpan.EndLinePosition.Line + 1,
                Description = ".GetAwaiter().GetResult() blocks the calling thread. Use await instead.",
                CurrentCode = currentCode,
                SuggestedCode = $"await {taskExpr.ToFullString().Trim()}",
                Confidence = OptimizationConfidence.High,
                Impact = OptimizationImpact.Critical,
                IsSemanticallySafe = false,
                Risks = ["Method must be made async"]
            });
        }
    }

    private void DetectMissingConfigureAwait(
        SyntaxNode root,
        SemanticModel semanticModel,
        string filePath,
        List<OptimizationOpportunity> opportunities)
    {
        // Skip if this looks like application code (has controllers, pages, etc.)
        if (IsApplicationCode(root))
            return;

        foreach (var awaitExpr in root.DescendantNodes().OfType<AwaitExpressionSyntax>())
        {
            // Check if ConfigureAwait is already called
            if (awaitExpr.Expression is InvocationExpressionSyntax invocation &&
                invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.Name.Identifier.Text == "ConfigureAwait")
            {
                continue;
            }

            var taskType = semanticModel.GetTypeInfo(awaitExpr.Expression).Type;
            if (taskType == null)
                continue;

            var typeName = taskType.ToDisplayString();
            if (!typeName.StartsWith("System.Threading.Tasks.Task") &&
                !typeName.StartsWith("System.Threading.Tasks.ValueTask"))
                continue;

            var lineSpan = awaitExpr.GetLocation().GetLineSpan();
            var currentCode = awaitExpr.ToFullString().Trim();
            var taskExpr = awaitExpr.Expression.ToFullString().Trim();

            opportunities.Add(new OptimizationOpportunity
            {
                Category = Category,
                Type = "MissingConfigureAwait",
                FilePath = filePath,
                StartLine = lineSpan.StartLinePosition.Line + 1,
                EndLine = lineSpan.EndLinePosition.Line + 1,
                Description = "In library code, add ConfigureAwait(false) to avoid capturing synchronization context unnecessarily.",
                CurrentCode = currentCode,
                SuggestedCode = $"await {taskExpr}.ConfigureAwait(false)",
                Confidence = OptimizationConfidence.Medium,
                Impact = OptimizationImpact.Low,
                IsSemanticallySafe = true,
                Assumptions = ["This is library code, not UI code"]
            });
        }
    }

    private void DetectUnnecessaryAsync(
        SyntaxNode root,
        SemanticModel semanticModel,
        string filePath,
        List<OptimizationOpportunity> opportunities)
    {
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (!method.Modifiers.Any(SyntaxKind.AsyncKeyword))
                continue;

            if (method.Body == null)
                continue;

            // Check if the method has exactly one statement that is a return await
            var statements = method.Body.Statements;
            if (statements.Count != 1)
                continue;

            if (statements[0] is not ReturnStatementSyntax returnStatement)
                continue;

            if (returnStatement.Expression is not AwaitExpressionSyntax awaitExpr)
                continue;

            // Check if there are any try-catch blocks
            if (method.Body.DescendantNodes().OfType<TryStatementSyntax>().Any())
                continue;

            // Check if there are any using statements
            if (method.Body.DescendantNodes().OfType<UsingStatementSyntax>().Any())
                continue;

            var lineSpan = method.GetLocation().GetLineSpan();
            var taskExpr = awaitExpr.Expression.ToFullString().Trim();

            opportunities.Add(new OptimizationOpportunity
            {
                Category = "Readability",
                Type = "UnnecessaryAsyncAwait",
                FilePath = filePath,
                StartLine = lineSpan.StartLinePosition.Line + 1,
                EndLine = lineSpan.EndLinePosition.Line + 1,
                Description = "Method only awaits a single task and returns. Consider returning the task directly to avoid state machine overhead.",
                CurrentCode = $"async ... {method.Identifier.Text}(...) {{ return await {taskExpr}; }}",
                SuggestedCode = $"... {method.Identifier.Text}(...) {{ return {taskExpr}; }}",
                Confidence = OptimizationConfidence.Medium,
                Impact = OptimizationImpact.Low,
                IsSemanticallySafe = false,
                Risks = ["Exception stack traces will differ", "May affect finally blocks or using statements"]
            });
        }
    }

    private bool IsEventHandler(IMethodSymbol method)
    {
        // Common event handler patterns
        if (method.Name.StartsWith("On") && method.Name.Length > 2)
            return true;

        if (method.Parameters.Length == 2)
        {
            var firstParam = method.Parameters[0].Type.ToDisplayString();
            var secondParam = method.Parameters[1].Type.ToDisplayString();

            // object sender, EventArgs e pattern
            if (firstParam == "object" && secondParam.EndsWith("EventArgs"))
                return true;
        }

        return false;
    }

    private bool IsApplicationCode(SyntaxNode root)
    {
        // Check for common application code indicators
        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

        foreach (var cls in classes)
        {
            var name = cls.Identifier.Text;
            if (name.EndsWith("Controller") ||
                name.EndsWith("Page") ||
                name.EndsWith("ViewModel") ||
                name.EndsWith("Window") ||
                name.EndsWith("Form"))
            {
                return true;
            }

            // Check base classes
            if (cls.BaseList != null)
            {
                foreach (var baseType in cls.BaseList.Types)
                {
                    var baseName = baseType.Type.ToString();
                    if (baseName.Contains("Controller") ||
                        baseName.Contains("Page") ||
                        baseName.Contains("Window") ||
                        baseName.Contains("Form"))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}
