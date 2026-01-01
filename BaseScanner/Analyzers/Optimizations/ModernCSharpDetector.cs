using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using BaseScanner.Context;

namespace BaseScanner.Analyzers.Optimizations;

/// <summary>
/// Detects opportunities to use modern C# language features.
/// Respects the project's target framework and language version.
/// </summary>
public class ModernCSharpDetector : IOptimizationDetector
{
    public string Category => "Modernization";

    public Task<List<OptimizationOpportunity>> DetectAsync(
        Document document,
        SemanticModel semanticModel,
        SyntaxNode root,
        CodeContext context)
    {
        var opportunities = new List<OptimizationOpportunity>();
        var filePath = document.FilePath ?? "";

        // Get the C# language version from the project
        var langVersion = GetLanguageVersion(document);

        // C# 6.0+: Null-conditional operator (?.)
        if (langVersion >= LanguageVersion.CSharp6)
        {
            DetectNullCheckPatterns(root, semanticModel, filePath, opportunities);
        }

        // C# 6.0+: Null-coalescing (??) - actually C# 2.0, but assignment (??=) is C# 8.0
        DetectNullCoalescingOpportunities(root, semanticModel, filePath, opportunities, langVersion);

        // C# 8.0+: Switch expressions
        if (langVersion >= LanguageVersion.CSharp8)
        {
            DetectSwitchExpressionOpportunities(root, semanticModel, filePath, opportunities);
        }

        // C# 7.0+: Pattern matching with 'is' declaration
        if (langVersion >= LanguageVersion.CSharp7)
        {
            DetectPatternMatchingOpportunities(root, semanticModel, filePath, opportunities);
        }

        // C# 9.0+: Target-typed new
        if (langVersion >= LanguageVersion.CSharp9)
        {
            DetectExplicitTypeOpportunities(root, semanticModel, filePath, opportunities);
        }

        return Task.FromResult(opportunities);
    }

    private LanguageVersion GetLanguageVersion(Document document)
    {
        if (document.Project.ParseOptions is CSharpParseOptions csharpOptions)
        {
            return csharpOptions.LanguageVersion;
        }
        // Default to C# 7.3 for .NET Framework projects
        return LanguageVersion.CSharp7_3;
    }

    private void DetectNullCheckPatterns(
        SyntaxNode root,
        SemanticModel semanticModel,
        string filePath,
        List<OptimizationOpportunity> opportunities)
    {
        // Pattern: if (x != null) { x.Method(); }
        foreach (var ifStatement in root.DescendantNodes().OfType<IfStatementSyntax>())
        {
            if (ifStatement.Condition is not BinaryExpressionSyntax binary)
                continue;

            if (!binary.IsKind(SyntaxKind.NotEqualsExpression))
                continue;

            // Check for != null
            ExpressionSyntax? checkedExpr = null;
            if (binary.Right.IsKind(SyntaxKind.NullLiteralExpression))
                checkedExpr = binary.Left;
            else if (binary.Left.IsKind(SyntaxKind.NullLiteralExpression))
                checkedExpr = binary.Right;
            else
                continue;

            // Check if the body is a single statement accessing the checked expression
            if (ifStatement.Statement is not BlockSyntax block || block.Statements.Count != 1)
                continue;

            if (ifStatement.Else != null)
                continue;

            var statement = block.Statements[0];
            if (statement is not ExpressionStatementSyntax exprStmt)
                continue;

            if (exprStmt.Expression is not InvocationExpressionSyntax invocation)
                continue;

            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                continue;

            // Check if accessing the same variable
            var accessedExpr = memberAccess.Expression.ToString();
            var checkedExprStr = checkedExpr.ToString();
            if (accessedExpr != checkedExprStr)
                continue;

            var lineSpan = ifStatement.GetLocation().GetLineSpan();
            var methodName = memberAccess.Name.Identifier.Text;

            opportunities.Add(new OptimizationOpportunity
            {
                Category = Category,
                Type = "NullConditionalOperator",
                FilePath = filePath,
                StartLine = lineSpan.StartLinePosition.Line + 1,
                EndLine = lineSpan.EndLinePosition.Line + 1,
                Description = "Null check followed by member access can use null-conditional operator.",
                CurrentCode = $"if ({checkedExprStr} != null) {{ {checkedExprStr}.{methodName}(...); }}",
                SuggestedCode = $"{checkedExprStr}?.{methodName}(...);",
                Confidence = OptimizationConfidence.High,
                Impact = OptimizationImpact.Low,
                IsSemanticallySafe = true
            });
        }
    }

    private void DetectNullCoalescingOpportunities(
        SyntaxNode root,
        SemanticModel semanticModel,
        string filePath,
        List<OptimizationOpportunity> opportunities,
        LanguageVersion langVersion)
    {
        // Pattern: x == null ? y : x -> x ?? y
        foreach (var conditional in root.DescendantNodes().OfType<ConditionalExpressionSyntax>())
        {
            if (conditional.Condition is not BinaryExpressionSyntax binary)
                continue;

            ExpressionSyntax? nullCheckedExpr = null;
            bool isEqualsNull = false;

            if (binary.IsKind(SyntaxKind.EqualsExpression))
            {
                isEqualsNull = true;
                if (binary.Right.IsKind(SyntaxKind.NullLiteralExpression))
                    nullCheckedExpr = binary.Left;
                else if (binary.Left.IsKind(SyntaxKind.NullLiteralExpression))
                    nullCheckedExpr = binary.Right;
            }
            else if (binary.IsKind(SyntaxKind.NotEqualsExpression))
            {
                isEqualsNull = false;
                if (binary.Right.IsKind(SyntaxKind.NullLiteralExpression))
                    nullCheckedExpr = binary.Left;
                else if (binary.Left.IsKind(SyntaxKind.NullLiteralExpression))
                    nullCheckedExpr = binary.Right;
            }

            if (nullCheckedExpr == null)
                continue;

            var nullCheckedStr = nullCheckedExpr.ToString();

            // For == null: whenTrue is the fallback, whenFalse should be the checked expr
            // For != null: whenTrue should be the checked expr, whenFalse is the fallback
            var checkedBranch = isEqualsNull ? conditional.WhenFalse : conditional.WhenTrue;
            var fallbackBranch = isEqualsNull ? conditional.WhenTrue : conditional.WhenFalse;

            if (checkedBranch.ToString() != nullCheckedStr)
                continue;

            var lineSpan = conditional.GetLocation().GetLineSpan();
            var currentCode = conditional.ToFullString().Trim();

            opportunities.Add(new OptimizationOpportunity
            {
                Category = Category,
                Type = "NullCoalescingOperator",
                FilePath = filePath,
                StartLine = lineSpan.StartLinePosition.Line + 1,
                EndLine = lineSpan.EndLinePosition.Line + 1,
                Description = "Ternary null check can use null-coalescing operator (??).",
                CurrentCode = currentCode,
                SuggestedCode = $"{nullCheckedStr} ?? {fallbackBranch.ToFullString().Trim()}",
                Confidence = OptimizationConfidence.High,
                Impact = OptimizationImpact.Low,
                IsSemanticallySafe = true
            });
        }

        // Pattern: if (x == null) x = y; -> x ??= y; (C# 8.0+)
        if (langVersion < LanguageVersion.CSharp8)
            return;

        foreach (var ifStatement in root.DescendantNodes().OfType<IfStatementSyntax>())
        {
            if (ifStatement.Else != null)
                continue;

            if (ifStatement.Condition is not BinaryExpressionSyntax binary)
                continue;

            if (!binary.IsKind(SyntaxKind.EqualsExpression))
                continue;

            ExpressionSyntax? nullCheckedExpr = null;
            if (binary.Right.IsKind(SyntaxKind.NullLiteralExpression))
                nullCheckedExpr = binary.Left;
            else if (binary.Left.IsKind(SyntaxKind.NullLiteralExpression))
                nullCheckedExpr = binary.Right;
            else
                continue;

            // Check if body is an assignment to the same variable
            StatementSyntax? bodyStatement = ifStatement.Statement;
            if (bodyStatement is BlockSyntax block && block.Statements.Count == 1)
                bodyStatement = block.Statements[0];

            if (bodyStatement is not ExpressionStatementSyntax exprStmt)
                continue;

            if (exprStmt.Expression is not AssignmentExpressionSyntax assignment)
                continue;

            if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
                continue;

            if (assignment.Left.ToString() != nullCheckedExpr.ToString())
                continue;

            var lineSpan = ifStatement.GetLocation().GetLineSpan();

            opportunities.Add(new OptimizationOpportunity
            {
                Category = Category,
                Type = "NullCoalescingAssignment",
                FilePath = filePath,
                StartLine = lineSpan.StartLinePosition.Line + 1,
                EndLine = lineSpan.EndLinePosition.Line + 1,
                Description = "Null check with assignment can use null-coalescing assignment operator (??=).",
                CurrentCode = $"if ({nullCheckedExpr} == null) {nullCheckedExpr} = {assignment.Right.ToFullString().Trim()};",
                SuggestedCode = $"{nullCheckedExpr} ??= {assignment.Right.ToFullString().Trim()};",
                Confidence = OptimizationConfidence.High,
                Impact = OptimizationImpact.Low,
                IsSemanticallySafe = true
            });
        }
    }

    private void DetectSwitchExpressionOpportunities(
        SyntaxNode root,
        SemanticModel semanticModel,
        string filePath,
        List<OptimizationOpportunity> opportunities)
    {
        foreach (var switchStatement in root.DescendantNodes().OfType<SwitchStatementSyntax>())
        {
            // Check if all sections return a value (good candidate for switch expression)
            var sections = switchStatement.Sections;
            if (sections.Count < 2)
                continue;

            bool allReturn = true;
            bool hasOnlySimpleStatements = true;

            foreach (var section in sections)
            {
                var statements = section.Statements;
                if (statements.Count != 1 && statements.Count != 2)
                {
                    hasOnlySimpleStatements = false;
                    break;
                }

                var lastStatement = statements[^1];
                if (lastStatement is not ReturnStatementSyntax && lastStatement is not BreakStatementSyntax)
                {
                    allReturn = false;
                }

                if (statements.Count == 2 && statements[0] is not ExpressionStatementSyntax)
                {
                    hasOnlySimpleStatements = false;
                }
            }

            if (!hasOnlySimpleStatements)
                continue;

            if (!allReturn)
                continue;

            var lineSpan = switchStatement.GetLocation().GetLineSpan();
            var governingExpr = switchStatement.Expression.ToFullString().Trim();

            opportunities.Add(new OptimizationOpportunity
            {
                Category = Category,
                Type = "SwitchExpression",
                FilePath = filePath,
                StartLine = lineSpan.StartLinePosition.Line + 1,
                EndLine = lineSpan.EndLinePosition.Line + 1,
                Description = "Switch statement with simple return/assignment cases can be converted to a switch expression.",
                CurrentCode = $"switch ({governingExpr}) {{ ... }}",
                SuggestedCode = $"return {governingExpr} switch {{ case1 => value1, case2 => value2, _ => default }};",
                Confidence = OptimizationConfidence.Medium,
                Impact = OptimizationImpact.Low,
                IsSemanticallySafe = true
            });
        }
    }

    private void DetectPatternMatchingOpportunities(
        SyntaxNode root,
        SemanticModel semanticModel,
        string filePath,
        List<OptimizationOpportunity> opportunities)
    {
        // Pattern: (x as T)?.Method() or if (x is T) { ((T)x).Method() }
        foreach (var ifStatement in root.DescendantNodes().OfType<IfStatementSyntax>())
        {
            if (ifStatement.Condition is not BinaryExpressionSyntax binary)
                continue;

            if (!binary.IsKind(SyntaxKind.IsExpression))
                continue;

            var checkedExpr = binary.Left.ToString();
            var targetType = binary.Right.ToString();

            // Check if the body casts the same expression
            var casts = ifStatement.Statement.DescendantNodes()
                .OfType<CastExpressionSyntax>()
                .Where(c => c.Expression.ToString() == checkedExpr &&
                           c.Type.ToString() == targetType);

            if (!casts.Any())
                continue;

            var lineSpan = ifStatement.GetLocation().GetLineSpan();

            opportunities.Add(new OptimizationOpportunity
            {
                Category = Category,
                Type = "PatternMatchingIs",
                FilePath = filePath,
                StartLine = lineSpan.StartLinePosition.Line + 1,
                EndLine = lineSpan.EndLinePosition.Line + 1,
                Description = "Type check with cast can use pattern matching 'is' declaration.",
                CurrentCode = $"if ({checkedExpr} is {targetType}) {{ (({targetType}){checkedExpr}).Method(); }}",
                SuggestedCode = $"if ({checkedExpr} is {targetType} typed) {{ typed.Method(); }}",
                Confidence = OptimizationConfidence.High,
                Impact = OptimizationImpact.Low,
                IsSemanticallySafe = true
            });
        }

        // Pattern: obj.GetType() == typeof(T) -> obj is T
        foreach (var binary in root.DescendantNodes().OfType<BinaryExpressionSyntax>())
        {
            if (!binary.IsKind(SyntaxKind.EqualsExpression))
                continue;

            InvocationExpressionSyntax? getTypeCall = null;
            TypeOfExpressionSyntax? typeOfExpr = null;

            if (binary.Left is InvocationExpressionSyntax leftInv &&
                IsGetTypeCall(leftInv) &&
                binary.Right is TypeOfExpressionSyntax rightTypeof)
            {
                getTypeCall = leftInv;
                typeOfExpr = rightTypeof;
            }
            else if (binary.Right is InvocationExpressionSyntax rightInv &&
                     IsGetTypeCall(rightInv) &&
                     binary.Left is TypeOfExpressionSyntax leftTypeof)
            {
                getTypeCall = rightInv;
                typeOfExpr = leftTypeof;
            }

            if (getTypeCall == null || typeOfExpr == null)
                continue;

            var expr = GetGetTypeReceiver(getTypeCall);
            if (expr == null)
                continue;

            var lineSpan = binary.GetLocation().GetLineSpan();
            var typeName = typeOfExpr.Type.ToString();

            opportunities.Add(new OptimizationOpportunity
            {
                Category = Category,
                Type = "GetTypeEqualsTypeof",
                FilePath = filePath,
                StartLine = lineSpan.StartLinePosition.Line + 1,
                EndLine = lineSpan.EndLinePosition.Line + 1,
                Description = "GetType() == typeof(T) can be simplified to 'is T' (though semantics differ for inheritance).",
                CurrentCode = binary.ToFullString().Trim(),
                SuggestedCode = $"{expr} is {typeName}",
                Confidence = OptimizationConfidence.Medium,
                Impact = OptimizationImpact.Low,
                IsSemanticallySafe = false,
                Risks = ["'is' also matches derived types, GetType() == typeof() is exact match only"]
            });
        }
    }

    private void DetectExplicitTypeOpportunities(
        SyntaxNode root,
        SemanticModel semanticModel,
        string filePath,
        List<OptimizationOpportunity> opportunities)
    {
        // Detect cases where var could be used but explicit type is repeated
        foreach (var varDecl in root.DescendantNodes().OfType<VariableDeclarationSyntax>())
        {
            if (varDecl.Type.IsVar)
                continue;

            foreach (var declarator in varDecl.Variables)
            {
                if (declarator.Initializer == null)
                    continue;

                var initializer = declarator.Initializer.Value;

                // Check for: Type x = new Type()
                if (initializer is ObjectCreationExpressionSyntax creation)
                {
                    var declaredType = varDecl.Type.ToString();
                    var createdType = creation.Type.ToString();

                    if (declaredType == createdType)
                    {
                        var lineSpan = varDecl.GetLocation().GetLineSpan();

                        opportunities.Add(new OptimizationOpportunity
                        {
                            Category = Category,
                            Type = "UseTargetTypedNew",
                            FilePath = filePath,
                            StartLine = lineSpan.StartLinePosition.Line + 1,
                            EndLine = lineSpan.EndLinePosition.Line + 1,
                            Description = "Type is repeated in declaration and initialization. Consider target-typed new or var.",
                            CurrentCode = $"{declaredType} {declarator.Identifier} = new {createdType}(...)",
                            SuggestedCode = $"{declaredType} {declarator.Identifier} = new(...) // or: var {declarator.Identifier} = new {createdType}(...)",
                            Confidence = OptimizationConfidence.Low,
                            Impact = OptimizationImpact.Low,
                            IsSemanticallySafe = true
                        });
                    }
                }
            }
        }
    }

    private bool IsGetTypeCall(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            return memberAccess.Name.Identifier.Text == "GetType" &&
                   invocation.ArgumentList.Arguments.Count == 0;
        }
        return false;
    }

    private string? GetGetTypeReceiver(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            return memberAccess.Expression.ToString();
        }
        return null;
    }
}
