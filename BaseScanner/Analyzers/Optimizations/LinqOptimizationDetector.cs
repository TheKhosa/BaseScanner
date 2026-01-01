using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using BaseScanner.Context;

namespace BaseScanner.Analyzers.Optimizations;

/// <summary>
/// Detects LINQ patterns that can be optimized.
/// </summary>
public class LinqOptimizationDetector : IOptimizationDetector
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

        // Detect .Count() > 0 -> .Any()
        DetectCountToAny(root, semanticModel, filePath, opportunities);

        // Detect .Where().First() -> .First(predicate)
        DetectWhereFirst(root, semanticModel, filePath, opportunities);

        // Detect .Where().Count() -> .Count(predicate)
        DetectWhereCount(root, semanticModel, filePath, opportunities);

        // Detect .Select().ToList() in loops
        DetectSelectToListInLoop(root, semanticModel, filePath, opportunities);

        // Detect .OrderBy().First() -> MinBy/MaxBy (.NET 6+ only)
        if (IsNet6OrHigher(document))
        {
            DetectOrderByFirst(root, semanticModel, filePath, opportunities);
        }

        return Task.FromResult(opportunities);
    }

    private bool IsNet6OrHigher(Document document)
    {
        // Check if project targets .NET 6+
        // This is a heuristic - we check for certain BCL types that only exist in .NET 6+
        var compilation = document.Project.GetCompilationAsync().Result;
        if (compilation == null) return false;

        // MinBy/MaxBy were added in .NET 6
        var enumerableType = compilation.GetTypeByMetadataName("System.Linq.Enumerable");
        if (enumerableType == null) return false;

        // Check if MinBy method exists
        var hasMinBy = enumerableType.GetMembers("MinBy").Any();
        return hasMinBy;
    }

    private void DetectCountToAny(
        SyntaxNode root,
        SemanticModel semanticModel,
        string filePath,
        List<OptimizationOpportunity> opportunities)
    {
        // Find patterns like: collection.Count() > 0, collection.Count() != 0, collection.Count() >= 1
        foreach (var binary in root.DescendantNodes().OfType<BinaryExpressionSyntax>())
        {
            if (!IsCountComparison(binary, semanticModel, out var invocation, out var isGreaterThanZero))
                continue;

            if (!isGreaterThanZero)
                continue;

            var lineSpan = binary.GetLocation().GetLineSpan();
            var currentCode = binary.ToFullString().Trim();
            var collectionExpr = GetCollectionExpression(invocation!);
            var suggestedCode = $"{collectionExpr}.Any()";

            opportunities.Add(new OptimizationOpportunity
            {
                Category = Category,
                Type = "LinqCountToAny",
                FilePath = filePath,
                StartLine = lineSpan.StartLinePosition.Line + 1,
                EndLine = lineSpan.EndLinePosition.Line + 1,
                Description = "Replace .Count() > 0 with .Any() for better performance. Any() short-circuits on first match.",
                CurrentCode = currentCode,
                SuggestedCode = suggestedCode,
                Confidence = OptimizationConfidence.High,
                Impact = OptimizationImpact.Medium,
                IsSemanticallySafe = true
            });
        }
    }

    private void DetectWhereFirst(
        SyntaxNode root,
        SemanticModel semanticModel,
        string filePath,
        List<OptimizationOpportunity> opportunities)
    {
        // Find patterns like: collection.Where(predicate).First() or .FirstOrDefault()
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                continue;

            var methodName = memberAccess.Name.Identifier.Text;
            if (methodName != "First" && methodName != "FirstOrDefault")
                continue;

            // Check if the receiver is a Where() call
            if (memberAccess.Expression is not InvocationExpressionSyntax whereInvocation)
                continue;

            if (whereInvocation.Expression is not MemberAccessExpressionSyntax whereAccess)
                continue;

            if (whereAccess.Name.Identifier.Text != "Where")
                continue;

            // Extract the predicate from Where()
            if (whereInvocation.ArgumentList.Arguments.Count != 1)
                continue;

            var predicate = whereInvocation.ArgumentList.Arguments[0].Expression;
            var collectionExpr = whereAccess.Expression.ToFullString().Trim();

            var lineSpan = invocation.GetLocation().GetLineSpan();
            var currentCode = invocation.ToFullString().Trim();
            var suggestedCode = $"{collectionExpr}.{methodName}({predicate.ToFullString().Trim()})";

            opportunities.Add(new OptimizationOpportunity
            {
                Category = Category,
                Type = "LinqWhereFirst",
                FilePath = filePath,
                StartLine = lineSpan.StartLinePosition.Line + 1,
                EndLine = lineSpan.EndLinePosition.Line + 1,
                Description = $"Combine .Where().{methodName}() into .{methodName}(predicate) to avoid intermediate iterator allocation.",
                CurrentCode = currentCode,
                SuggestedCode = suggestedCode,
                Confidence = OptimizationConfidence.High,
                Impact = OptimizationImpact.Medium,
                IsSemanticallySafe = true
            });
        }
    }

    private void DetectWhereCount(
        SyntaxNode root,
        SemanticModel semanticModel,
        string filePath,
        List<OptimizationOpportunity> opportunities)
    {
        // Find patterns like: collection.Where(predicate).Count()
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                continue;

            if (memberAccess.Name.Identifier.Text != "Count")
                continue;

            // Check if the receiver is a Where() call
            if (memberAccess.Expression is not InvocationExpressionSyntax whereInvocation)
                continue;

            if (whereInvocation.Expression is not MemberAccessExpressionSyntax whereAccess)
                continue;

            if (whereAccess.Name.Identifier.Text != "Where")
                continue;

            // Extract the predicate from Where()
            if (whereInvocation.ArgumentList.Arguments.Count != 1)
                continue;

            var predicate = whereInvocation.ArgumentList.Arguments[0].Expression;
            var collectionExpr = whereAccess.Expression.ToFullString().Trim();

            var lineSpan = invocation.GetLocation().GetLineSpan();
            var currentCode = invocation.ToFullString().Trim();
            var suggestedCode = $"{collectionExpr}.Count({predicate.ToFullString().Trim()})";

            opportunities.Add(new OptimizationOpportunity
            {
                Category = Category,
                Type = "LinqWhereCount",
                FilePath = filePath,
                StartLine = lineSpan.StartLinePosition.Line + 1,
                EndLine = lineSpan.EndLinePosition.Line + 1,
                Description = "Combine .Where().Count() into .Count(predicate) to avoid intermediate iterator allocation.",
                CurrentCode = currentCode,
                SuggestedCode = suggestedCode,
                Confidence = OptimizationConfidence.High,
                Impact = OptimizationImpact.Medium,
                IsSemanticallySafe = true
            });
        }
    }

    private void DetectSelectToListInLoop(
        SyntaxNode root,
        SemanticModel semanticModel,
        string filePath,
        List<OptimizationOpportunity> opportunities)
    {
        // Find .Select().ToList() or .Select().ToArray() inside loops
        var loops = root.DescendantNodes()
            .Where(n => n is ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax);

        foreach (var loop in loops)
        {
            foreach (var invocation in loop.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                    continue;

                var methodName = memberAccess.Name.Identifier.Text;
                if (methodName != "ToList" && methodName != "ToArray")
                    continue;

                // Check if the receiver is a Select() call
                if (memberAccess.Expression is not InvocationExpressionSyntax selectInvocation)
                    continue;

                if (selectInvocation.Expression is not MemberAccessExpressionSyntax selectAccess)
                    continue;

                if (selectAccess.Name.Identifier.Text != "Select")
                    continue;

                var lineSpan = invocation.GetLocation().GetLineSpan();
                var currentCode = invocation.ToFullString().Trim();

                opportunities.Add(new OptimizationOpportunity
                {
                    Category = Category,
                    Type = "LinqMaterializationInLoop",
                    FilePath = filePath,
                    StartLine = lineSpan.StartLinePosition.Line + 1,
                    EndLine = lineSpan.EndLinePosition.Line + 1,
                    Description = $".Select().{methodName}() inside a loop creates a new collection on each iteration. Consider moving outside the loop or using a different approach.",
                    CurrentCode = currentCode,
                    SuggestedCode = "// Move collection creation outside loop or iterate directly",
                    Confidence = OptimizationConfidence.Medium,
                    Impact = OptimizationImpact.High,
                    IsSemanticallySafe = false,
                    Assumptions = ["The collection doesn't change between iterations"],
                    Risks = ["May change behavior if collection is modified in loop"]
                });
            }
        }
    }

    private void DetectOrderByFirst(
        SyntaxNode root,
        SemanticModel semanticModel,
        string filePath,
        List<OptimizationOpportunity> opportunities)
    {
        // Find .OrderBy().First() patterns - suggest MinBy/MaxBy for better performance
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                continue;

            var methodName = memberAccess.Name.Identifier.Text;
            if (methodName != "First" && methodName != "FirstOrDefault")
                continue;

            // Check if the receiver is an OrderBy/OrderByDescending call
            if (memberAccess.Expression is not InvocationExpressionSyntax orderInvocation)
                continue;

            if (orderInvocation.Expression is not MemberAccessExpressionSyntax orderAccess)
                continue;

            var orderMethod = orderAccess.Name.Identifier.Text;
            if (orderMethod != "OrderBy" && orderMethod != "OrderByDescending")
                continue;

            var suggestedMethod = orderMethod == "OrderBy" ? "MinBy" : "MaxBy";

            if (orderInvocation.ArgumentList.Arguments.Count != 1)
                continue;

            var keySelector = orderInvocation.ArgumentList.Arguments[0].Expression;
            var collectionExpr = orderAccess.Expression.ToFullString().Trim();

            var lineSpan = invocation.GetLocation().GetLineSpan();
            var currentCode = invocation.ToFullString().Trim();
            var suggestedCode = $"{collectionExpr}.{suggestedMethod}({keySelector.ToFullString().Trim()})";

            opportunities.Add(new OptimizationOpportunity
            {
                Category = Category,
                Type = "LinqOrderByFirst",
                FilePath = filePath,
                StartLine = lineSpan.StartLinePosition.Line + 1,
                EndLine = lineSpan.EndLinePosition.Line + 1,
                Description = $"Replace .{orderMethod}().{methodName}() with .{suggestedMethod}() for O(n) instead of O(n log n) complexity.",
                CurrentCode = currentCode,
                SuggestedCode = suggestedCode,
                Confidence = OptimizationConfidence.High,
                Impact = OptimizationImpact.High,
                IsSemanticallySafe = true,
                Assumptions = ["Using .NET 6+ which has MinBy/MaxBy"]
            });
        }
    }

    private bool IsCountComparison(
        BinaryExpressionSyntax binary,
        SemanticModel semanticModel,
        out InvocationExpressionSyntax? countInvocation,
        out bool isGreaterThanZero)
    {
        countInvocation = null;
        isGreaterThanZero = false;

        // Pattern: collection.Count() > 0 or collection.Count() >= 1 or collection.Count() != 0
        if (binary.Left is InvocationExpressionSyntax leftInvocation &&
            IsCountMethod(leftInvocation, semanticModel))
        {
            countInvocation = leftInvocation;

            if (binary.Right is LiteralExpressionSyntax literal)
            {
                var value = literal.Token.Value;
                isGreaterThanZero = binary.Kind() switch
                {
                    SyntaxKind.GreaterThanExpression when value is 0 => true,
                    SyntaxKind.GreaterThanOrEqualExpression when value is 1 => true,
                    SyntaxKind.NotEqualsExpression when value is 0 => true,
                    _ => false
                };
                return true;
            }
        }

        // Pattern: 0 < collection.Count()
        if (binary.Right is InvocationExpressionSyntax rightInvocation &&
            IsCountMethod(rightInvocation, semanticModel))
        {
            countInvocation = rightInvocation;

            if (binary.Left is LiteralExpressionSyntax literal)
            {
                var value = literal.Token.Value;
                isGreaterThanZero = binary.Kind() switch
                {
                    SyntaxKind.LessThanExpression when value is 0 => true,
                    SyntaxKind.LessThanOrEqualExpression when value is 0 => true,
                    _ => false
                };
                return true;
            }
        }

        return false;
    }

    private bool IsCountMethod(InvocationExpressionSyntax invocation, SemanticModel semanticModel)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            return false;

        if (memberAccess.Name.Identifier.Text != "Count")
            return false;

        // Verify it's the LINQ Count() method (not List.Count property)
        var symbol = semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        if (symbol == null)
            return false;

        var containingNamespace = symbol.ContainingNamespace?.ToDisplayString() ?? "";
        return containingNamespace.StartsWith("System.Linq");
    }

    private string GetCollectionExpression(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            return memberAccess.Expression.ToFullString().Trim();
        }
        return invocation.Expression.ToFullString().Trim();
    }
}
