using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using BaseScanner.Context;

namespace BaseScanner.Analyzers.Optimizations;

/// <summary>
/// Detects collection usage patterns that can be optimized.
/// </summary>
public class CollectionOptimizationDetector : IOptimizationDetector
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

        // Detect List.Contains in loops -> HashSet
        DetectListContainsInLoop(root, semanticModel, filePath, opportunities);

        // Detect Dictionary without capacity hint
        DetectDictionaryWithoutCapacity(root, semanticModel, filePath, opportunities);

        // Detect string concatenation in loops
        DetectStringConcatenationInLoop(root, semanticModel, filePath, opportunities);

        // Detect repeated List.Add without capacity
        DetectRepeatedListAdd(root, semanticModel, filePath, opportunities);

        return Task.FromResult(opportunities);
    }

    private void DetectListContainsInLoop(
        SyntaxNode root,
        SemanticModel semanticModel,
        string filePath,
        List<OptimizationOpportunity> opportunities)
    {
        var loops = root.DescendantNodes()
            .Where(n => n is ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax);

        foreach (var loop in loops)
        {
            foreach (var invocation in loop.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                    continue;

                if (memberAccess.Name.Identifier.Text != "Contains")
                    continue;

                var symbol = semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                if (symbol == null)
                    continue;

                var containingType = symbol.ContainingType?.ToDisplayString() ?? "";
                if (!containingType.StartsWith("System.Collections.Generic.List") &&
                    !containingType.StartsWith("System.Collections.Generic.IList"))
                    continue;

                // Check if the list is defined outside the loop
                var collectionExpr = memberAccess.Expression;
                var collectionSymbol = semanticModel.GetSymbolInfo(collectionExpr).Symbol;
                if (collectionSymbol == null)
                    continue;

                // Determine if collection is modified in loop
                var isModifiedInLoop = IsModifiedInScope(loop, collectionSymbol, semanticModel);
                if (isModifiedInLoop)
                    continue;

                var lineSpan = invocation.GetLocation().GetLineSpan();
                var currentCode = invocation.ToFullString().Trim();
                var collectionName = collectionExpr.ToFullString().Trim();

                opportunities.Add(new OptimizationOpportunity
                {
                    Category = Category,
                    Type = "ListContainsInLoop",
                    FilePath = filePath,
                    StartLine = lineSpan.StartLinePosition.Line + 1,
                    EndLine = lineSpan.EndLinePosition.Line + 1,
                    Description = $"List.Contains() in loop is O(n) per call. Convert to HashSet before loop for O(1) lookups.",
                    CurrentCode = currentCode,
                    SuggestedCode = $"var {collectionName}Set = {collectionName}.ToHashSet();\n// Then use: {collectionName}Set.Contains(...)",
                    Confidence = OptimizationConfidence.High,
                    Impact = OptimizationImpact.High,
                    IsSemanticallySafe = true,
                    Assumptions = ["Collection is not modified during loop iteration"]
                });
            }
        }
    }

    private void DetectDictionaryWithoutCapacity(
        SyntaxNode root,
        SemanticModel semanticModel,
        string filePath,
        List<OptimizationOpportunity> opportunities)
    {
        foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var typeInfo = semanticModel.GetTypeInfo(creation);
            var typeName = typeInfo.Type?.ToDisplayString() ?? "";

            if (!typeName.StartsWith("System.Collections.Generic.Dictionary"))
                continue;

            // Check if capacity is provided
            if (creation.ArgumentList?.Arguments.Count > 0)
            {
                // Check if first argument is an integer (capacity)
                var firstArg = creation.ArgumentList.Arguments[0];
                var argType = semanticModel.GetTypeInfo(firstArg.Expression).Type;
                if (argType?.SpecialType == SpecialType.System_Int32)
                    continue; // Capacity already provided
            }

            // Look for subsequent Add calls to estimate size
            var containingMethod = creation.FirstAncestorOrSelf<MethodDeclarationSyntax>();
            if (containingMethod == null)
                continue;

            var addCalls = CountAddCalls(containingMethod, creation, semanticModel);
            if (addCalls < 5)
                continue; // Not worth optimizing

            var lineSpan = creation.GetLocation().GetLineSpan();
            var currentCode = creation.ToFullString().Trim();

            opportunities.Add(new OptimizationOpportunity
            {
                Category = Category,
                Type = "DictionaryWithoutCapacity",
                FilePath = filePath,
                StartLine = lineSpan.StartLinePosition.Line + 1,
                EndLine = lineSpan.EndLinePosition.Line + 1,
                Description = $"Dictionary created without initial capacity. {addCalls} Add operations detected. Providing capacity avoids resizing.",
                CurrentCode = currentCode,
                SuggestedCode = currentCode.Replace("()", $"({addCalls})"),
                Confidence = OptimizationConfidence.Medium,
                Impact = OptimizationImpact.Medium,
                IsSemanticallySafe = true,
                Assumptions = ["The estimated add count is accurate"]
            });
        }
    }

    private void DetectStringConcatenationInLoop(
        SyntaxNode root,
        SemanticModel semanticModel,
        string filePath,
        List<OptimizationOpportunity> opportunities)
    {
        var loops = root.DescendantNodes()
            .Where(n => n is ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax);

        foreach (var loop in loops)
        {
            // Find += operations on strings
            foreach (var assignment in loop.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (!assignment.IsKind(SyntaxKind.AddAssignmentExpression))
                    continue;

                var leftType = semanticModel.GetTypeInfo(assignment.Left).Type;
                if (leftType?.SpecialType != SpecialType.System_String)
                    continue;

                var lineSpan = assignment.GetLocation().GetLineSpan();
                var variableName = assignment.Left.ToFullString().Trim();
                var currentCode = assignment.ToFullString().Trim();

                opportunities.Add(new OptimizationOpportunity
                {
                    Category = Category,
                    Type = "StringConcatenationInLoop",
                    FilePath = filePath,
                    StartLine = lineSpan.StartLinePosition.Line + 1,
                    EndLine = lineSpan.EndLinePosition.Line + 1,
                    Description = "String concatenation in loop creates many intermediate strings. Use StringBuilder instead.",
                    CurrentCode = currentCode,
                    SuggestedCode = $"var sb = new StringBuilder();\n// In loop: sb.Append(...);\n// After loop: {variableName} = sb.ToString();",
                    Confidence = OptimizationConfidence.High,
                    Impact = OptimizationImpact.High,
                    IsSemanticallySafe = true
                });
            }

            // Find string + operations in loop
            foreach (var binary in loop.DescendantNodes().OfType<BinaryExpressionSyntax>())
            {
                if (!binary.IsKind(SyntaxKind.AddExpression))
                    continue;

                var resultType = semanticModel.GetTypeInfo(binary).Type;
                if (resultType?.SpecialType != SpecialType.System_String)
                    continue;

                // Check if this is assigned to a variable that's used outside the binary expression
                var parent = binary.Parent;
                if (parent is not AssignmentExpressionSyntax assignment)
                    continue;

                var leftType = semanticModel.GetTypeInfo(assignment.Left).Type;
                if (leftType?.SpecialType != SpecialType.System_String)
                    continue;

                // Already detected as += above
                if (assignment.IsKind(SyntaxKind.AddAssignmentExpression))
                    continue;

                var lineSpan = binary.GetLocation().GetLineSpan();
                var currentCode = binary.ToFullString().Trim();

                opportunities.Add(new OptimizationOpportunity
                {
                    Category = Category,
                    Type = "StringConcatenationInLoop",
                    FilePath = filePath,
                    StartLine = lineSpan.StartLinePosition.Line + 1,
                    EndLine = lineSpan.EndLinePosition.Line + 1,
                    Description = "String concatenation in loop. Consider using StringBuilder or string.Join() for better performance.",
                    CurrentCode = currentCode,
                    SuggestedCode = "// Use StringBuilder.Append() or string.Join()",
                    Confidence = OptimizationConfidence.Medium,
                    Impact = OptimizationImpact.Medium,
                    IsSemanticallySafe = true
                });
            }
        }
    }

    private void DetectRepeatedListAdd(
        SyntaxNode root,
        SemanticModel semanticModel,
        string filePath,
        List<OptimizationOpportunity> opportunities)
    {
        // Look for List<T> creation followed by multiple Add calls
        foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var typeInfo = semanticModel.GetTypeInfo(creation);
            var typeName = typeInfo.Type?.ToDisplayString() ?? "";

            if (!typeName.StartsWith("System.Collections.Generic.List"))
                continue;

            // Check if capacity is provided
            if (creation.ArgumentList?.Arguments.Count > 0)
            {
                var firstArg = creation.ArgumentList.Arguments[0];
                var argType = semanticModel.GetTypeInfo(firstArg.Expression).Type;
                if (argType?.SpecialType == SpecialType.System_Int32)
                    continue; // Capacity already provided
            }

            var containingMethod = creation.FirstAncestorOrSelf<MethodDeclarationSyntax>();
            if (containingMethod == null)
                continue;

            var addCalls = CountAddCalls(containingMethod, creation, semanticModel);
            if (addCalls < 10)
                continue; // Not significant enough

            var lineSpan = creation.GetLocation().GetLineSpan();
            var currentCode = creation.ToFullString().Trim();

            opportunities.Add(new OptimizationOpportunity
            {
                Category = Category,
                Type = "ListWithoutCapacity",
                FilePath = filePath,
                StartLine = lineSpan.StartLinePosition.Line + 1,
                EndLine = lineSpan.EndLinePosition.Line + 1,
                Description = $"List created without initial capacity. {addCalls} Add operations detected. Providing capacity avoids array resizing.",
                CurrentCode = currentCode,
                SuggestedCode = currentCode.Replace("()", $"({addCalls})"),
                Confidence = OptimizationConfidence.Medium,
                Impact = OptimizationImpact.Medium,
                IsSemanticallySafe = true
            });
        }
    }

    private bool IsModifiedInScope(SyntaxNode scope, ISymbol symbol, SemanticModel semanticModel)
    {
        var assignments = scope.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a =>
            {
                var leftSymbol = semanticModel.GetSymbolInfo(a.Left).Symbol;
                return SymbolEqualityComparer.Default.Equals(leftSymbol, symbol);
            });

        if (assignments.Any())
            return true;

        // Check for method calls that might modify the collection
        var modifyingCalls = scope.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(inv =>
            {
                if (inv.Expression is not MemberAccessExpressionSyntax ma)
                    return false;

                var receiverSymbol = semanticModel.GetSymbolInfo(ma.Expression).Symbol;
                if (!SymbolEqualityComparer.Default.Equals(receiverSymbol, symbol))
                    return false;

                var methodName = ma.Name.Identifier.Text;
                return methodName is "Add" or "Remove" or "Clear" or "Insert" or "RemoveAt";
            });

        return modifyingCalls.Any();
    }

    private int CountAddCalls(MethodDeclarationSyntax method, ObjectCreationExpressionSyntax creation, SemanticModel semanticModel)
    {
        // Try to find the variable the collection is assigned to
        var assignment = creation.Parent as AssignmentExpressionSyntax;
        var declarator = creation.Parent as EqualsValueClauseSyntax;

        ISymbol? collectionSymbol = null;

        if (assignment != null)
        {
            collectionSymbol = semanticModel.GetSymbolInfo(assignment.Left).Symbol;
        }
        else if (declarator?.Parent is VariableDeclaratorSyntax varDecl)
        {
            collectionSymbol = semanticModel.GetDeclaredSymbol(varDecl);
        }

        if (collectionSymbol == null)
            return 0;

        // Count Add calls on this collection
        var addCalls = method.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Count(inv =>
            {
                if (inv.Expression is not MemberAccessExpressionSyntax ma)
                    return false;

                if (ma.Name.Identifier.Text != "Add")
                    return false;

                var receiverSymbol = semanticModel.GetSymbolInfo(ma.Expression).Symbol;
                return SymbolEqualityComparer.Default.Equals(receiverSymbol, collectionSymbol);
            });

        return addCalls;
    }
}
