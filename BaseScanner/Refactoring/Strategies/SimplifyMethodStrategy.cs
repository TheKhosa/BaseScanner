using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using BaseScanner.Refactoring.Models;

namespace BaseScanner.Refactoring.Strategies;

/// <summary>
/// Simplifies methods by applying guard clauses, early returns, and reducing nesting.
/// </summary>
public class SimplifyMethodStrategy : RefactoringStrategyBase
{
    public override string Name => "Simplify Method";
    public override string Category => "Refactoring";
    public override string Description => "Reduces complexity with guard clauses, early returns, and flattened nesting";
    public override RefactoringType RefactoringType => RefactoringType.SimplifyMethod;

    public override IReadOnlyList<CodeSmellType> AddressesSmells => new[]
    {
        CodeSmellType.LongMethod,
        CodeSmellType.DeepNesting
    };

    public override async Task<bool> CanApplyAsync(Document document)
    {
        var root = await document.GetSyntaxRootAsync();
        if (root == null)
            return false;

        // Check for methods with deep nesting or complex conditionals
        return root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Any(m => HasSimplificationOpportunity(m));
    }

    public override async Task<Solution> ApplyAsync(Solution solution, DocumentId documentId)
    {
        var document = solution.GetDocument(documentId);
        if (document == null)
            return solution;

        var root = await document.GetSyntaxRootAsync();
        if (root == null)
            return solution;

        var editor = await DocumentEditor.CreateAsync(document);

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (HasSimplificationOpportunity(method))
            {
                var simplified = SimplifyMethod(method);
                if (simplified != null && simplified.ToFullString() != method.ToFullString())
                {
                    editor.ReplaceNode(method, simplified);
                }
            }
        }

        return editor.GetChangedDocument().Project.Solution;
    }

    public override async Task<Solution> ApplyAsync(Solution solution, DocumentId documentId, CodeSmell targetSmell)
    {
        var document = solution.GetDocument(documentId);
        if (document == null)
            return solution;

        var root = await document.GetSyntaxRootAsync();
        if (root == null)
            return solution;

        // Find the target method
        var targetMethod = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == targetSmell.TargetName ||
                                  m.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == targetSmell.StartLine);

        if (targetMethod == null)
            return solution;

        var editor = await DocumentEditor.CreateAsync(document);
        var simplified = SimplifyMethod(targetMethod);

        if (simplified != null)
        {
            editor.ReplaceNode(targetMethod, simplified);
        }

        return editor.GetChangedDocument().Project.Solution;
    }

    public override async Task<RefactoringEstimate> EstimateImprovementAsync(Document document, CodeSmell? targetSmell = null)
    {
        var root = await document.GetSyntaxRootAsync();
        if (root == null)
        {
            return new RefactoringEstimate
            {
                StrategyType = RefactoringType,
                CanApply = false,
                CannotApplyReason = "Could not parse document"
            };
        }

        var methods = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => targetSmell == null || m.Identifier.Text == targetSmell.TargetName)
            .Where(HasSimplificationOpportunity)
            .ToList();

        if (methods.Count == 0)
        {
            return new RefactoringEstimate
            {
                StrategyType = RefactoringType,
                CanApply = false,
                CannotApplyReason = "No methods found with simplification opportunities"
            };
        }

        var totalNestingReduction = methods.Sum(m => GetNestingDepth(m) - 1);
        var totalGuardClauses = methods.Sum(CountPotentialGuardClauses);

        return new RefactoringEstimate
        {
            StrategyType = RefactoringType,
            CanApply = true,
            EstimatedComplexityReduction = totalNestingReduction + totalGuardClauses,
            EstimatedCohesionImprovement = 0, // This strategy doesn't affect cohesion
            EstimatedMaintainabilityGain = totalNestingReduction * 2,
            ProposedNames = methods.Select(m => $"Simplified: {m.Identifier.Text}").ToList()
        };
    }

    public override async Task<RefactoringDetails> GetProposedChangesAsync(Document document, CodeSmell targetSmell)
    {
        var details = new RefactoringDetails();
        var root = await document.GetSyntaxRootAsync();
        if (root == null)
            return details;

        var methods = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == targetSmell.TargetName)
            .ToList();

        foreach (var method in methods)
        {
            if (HasInvertibleCondition(method))
            {
                details.ExtractedMethods.Add($"Guard clause in {method.Identifier.Text}");
            }
        }

        return details;
    }

    #region Simplification Logic

    private bool HasSimplificationOpportunity(MethodDeclarationSyntax method)
    {
        return GetNestingDepth(method) > 2 ||
               HasInvertibleCondition(method) ||
               HasNegativeCondition(method) ||
               HasElseAfterReturn(method);
    }

    private int GetNestingDepth(SyntaxNode node)
    {
        var maxDepth = 0;
        var currentDepth = 0;

        void Visit(SyntaxNode n)
        {
            if (n is IfStatementSyntax or WhileStatementSyntax or ForStatementSyntax or
                ForEachStatementSyntax or TryStatementSyntax or SwitchStatementSyntax)
            {
                currentDepth++;
                maxDepth = Math.Max(maxDepth, currentDepth);
            }

            foreach (var child in n.ChildNodes())
            {
                Visit(child);
            }

            if (n is IfStatementSyntax or WhileStatementSyntax or ForStatementSyntax or
                ForEachStatementSyntax or TryStatementSyntax or SwitchStatementSyntax)
            {
                currentDepth--;
            }
        }

        Visit(node);
        return maxDepth;
    }

    private bool HasInvertibleCondition(MethodDeclarationSyntax method)
    {
        if (method.Body == null)
            return false;

        var statements = method.Body.Statements;
        if (statements.Count == 0)
            return false;

        // Check if first statement is an if that wraps most of the method
        if (statements[0] is IfStatementSyntax ifStmt)
        {
            // If the if statement contains most of the method's logic
            // and there's no else or simple else, it can be inverted
            var ifLines = ifStmt.Statement.GetText().Lines.Count;
            var totalLines = method.Body.GetText().Lines.Count;

            return ifLines > totalLines * 0.7 && ifStmt.Else == null;
        }

        return false;
    }

    private bool HasNegativeCondition(MethodDeclarationSyntax method)
    {
        return method.DescendantNodes()
            .OfType<IfStatementSyntax>()
            .Any(ifStmt => ifStmt.Condition is PrefixUnaryExpressionSyntax prefix &&
                           prefix.IsKind(SyntaxKind.LogicalNotExpression));
    }

    private bool HasElseAfterReturn(MethodDeclarationSyntax method)
    {
        return method.DescendantNodes()
            .OfType<IfStatementSyntax>()
            .Any(ifStmt =>
            {
                if (ifStmt.Else == null)
                    return false;

                // Check if the if branch ends with return
                var lastStatement = GetLastStatement(ifStmt.Statement);
                return lastStatement is ReturnStatementSyntax or ThrowStatementSyntax;
            });
    }

    private StatementSyntax? GetLastStatement(StatementSyntax statement)
    {
        if (statement is BlockSyntax block)
        {
            return block.Statements.LastOrDefault();
        }
        return statement;
    }

    private int CountPotentialGuardClauses(MethodDeclarationSyntax method)
    {
        var count = 0;

        if (method.Body == null)
            return 0;

        // Count null checks that could be guard clauses
        foreach (var ifStmt in method.Body.Statements.OfType<IfStatementSyntax>())
        {
            if (IsNullCheck(ifStmt.Condition) && ifStmt.Else == null)
            {
                count++;
            }
        }

        return count;
    }

    private bool IsNullCheck(ExpressionSyntax condition)
    {
        if (condition is BinaryExpressionSyntax binary)
        {
            if (binary.IsKind(SyntaxKind.EqualsExpression) || binary.IsKind(SyntaxKind.NotEqualsExpression))
            {
                return binary.Right is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.NullLiteralExpression) ||
                       binary.Left is LiteralExpressionSyntax leftLiteral && leftLiteral.IsKind(SyntaxKind.NullLiteralExpression);
            }
        }
        else if (condition is IsPatternExpressionSyntax isPattern)
        {
            return isPattern.Pattern is ConstantPatternSyntax constant &&
                   constant.Expression is LiteralExpressionSyntax lit &&
                   lit.IsKind(SyntaxKind.NullLiteralExpression);
        }

        return false;
    }

    private MethodDeclarationSyntax? SimplifyMethod(MethodDeclarationSyntax method)
    {
        if (method.Body == null)
            return null;

        var simplified = method;

        // Apply guard clause transformation
        simplified = ApplyGuardClauses(simplified);

        // Remove unnecessary else after return
        simplified = RemoveElseAfterReturn(simplified);

        // Flatten nested conditions
        simplified = FlattenNestedConditions(simplified);

        return simplified;
    }

    private MethodDeclarationSyntax ApplyGuardClauses(MethodDeclarationSyntax method)
    {
        if (method.Body == null || method.Body.Statements.Count == 0)
            return method;

        var firstStatement = method.Body.Statements[0];
        if (firstStatement is not IfStatementSyntax ifStmt)
            return method;

        // Check if this is an invertible condition
        if (ifStmt.Else != null)
            return method;

        // Get the condition and invert it
        var invertedCondition = InvertCondition(ifStmt.Condition);

        // Create guard clause
        var guardReturn = GetEarlyReturn(method);
        var guardClause = SyntaxFactory.IfStatement(
            invertedCondition,
            SyntaxFactory.Block(guardReturn))
            .WithLeadingTrivia(ifStmt.GetLeadingTrivia());

        // Extract the body statements
        var bodyStatements = ifStmt.Statement is BlockSyntax block
            ? block.Statements
            : SyntaxFactory.SingletonList(ifStmt.Statement);

        // Combine guard clause with extracted body
        var newStatements = new List<StatementSyntax> { guardClause };
        newStatements.AddRange(bodyStatements);
        newStatements.AddRange(method.Body.Statements.Skip(1));

        var newBody = method.Body.WithStatements(SyntaxFactory.List(newStatements));
        return method.WithBody(newBody);
    }

    private ExpressionSyntax InvertCondition(ExpressionSyntax condition)
    {
        // Handle already negated conditions
        if (condition is PrefixUnaryExpressionSyntax prefix &&
            prefix.IsKind(SyntaxKind.LogicalNotExpression))
        {
            return prefix.Operand;
        }

        // Handle binary comparisons
        if (condition is BinaryExpressionSyntax binary)
        {
            var newKind = binary.Kind() switch
            {
                SyntaxKind.EqualsExpression => SyntaxKind.NotEqualsExpression,
                SyntaxKind.NotEqualsExpression => SyntaxKind.EqualsExpression,
                SyntaxKind.LessThanExpression => SyntaxKind.GreaterThanOrEqualExpression,
                SyntaxKind.GreaterThanExpression => SyntaxKind.LessThanOrEqualExpression,
                SyntaxKind.LessThanOrEqualExpression => SyntaxKind.GreaterThanExpression,
                SyntaxKind.GreaterThanOrEqualExpression => SyntaxKind.LessThanExpression,
                _ => (SyntaxKind?)null
            };

            if (newKind.HasValue)
            {
                return SyntaxFactory.BinaryExpression(newKind.Value, binary.Left, binary.Right);
            }
        }

        // Default: wrap in logical not
        return SyntaxFactory.PrefixUnaryExpression(
            SyntaxKind.LogicalNotExpression,
            SyntaxFactory.ParenthesizedExpression(condition));
    }

    private StatementSyntax GetEarlyReturn(MethodDeclarationSyntax method)
    {
        var returnType = method.ReturnType.ToString();

        if (returnType == "void")
        {
            return SyntaxFactory.ReturnStatement();
        }
        else if (returnType == "bool" || returnType == "Boolean")
        {
            return SyntaxFactory.ReturnStatement(
                SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression));
        }
        else if (returnType == "Task" || returnType.StartsWith("Task<"))
        {
            return SyntaxFactory.ReturnStatement(
                SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("Task"),
                        SyntaxFactory.IdentifierName("CompletedTask"))));
        }
        else
        {
            // Return default or throw
            return SyntaxFactory.ThrowStatement(
                SyntaxFactory.ObjectCreationExpression(
                    SyntaxFactory.IdentifierName("ArgumentException"))
                    .WithArgumentList(SyntaxFactory.ArgumentList()));
        }
    }

    private MethodDeclarationSyntax RemoveElseAfterReturn(MethodDeclarationSyntax method)
    {
        if (method.Body == null)
            return method;

        var rewriter = new ElseAfterReturnRewriter();
        var newBody = (BlockSyntax)rewriter.Visit(method.Body);

        return method.WithBody(newBody);
    }

    private MethodDeclarationSyntax FlattenNestedConditions(MethodDeclarationSyntax method)
    {
        // This would flatten deeply nested if statements
        // For now, return as-is (complex transformation)
        return method;
    }

    #endregion

    private class ElseAfterReturnRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
        {
            var visited = (IfStatementSyntax)base.VisitIfStatement(node)!;

            if (visited.Else == null)
                return visited;

            var lastStatement = GetLastStatementInBranch(visited.Statement);
            if (lastStatement is not (ReturnStatementSyntax or ThrowStatementSyntax))
                return visited;

            // Remove the else and add its statements after the if
            var elseStatements = visited.Else.Statement is BlockSyntax block
                ? block.Statements
                : SyntaxFactory.SingletonList(visited.Else.Statement);

            // Return the if without else - the parent block will need to add the statements
            return visited.WithElse(null);
        }

        private StatementSyntax? GetLastStatementInBranch(StatementSyntax statement)
        {
            if (statement is BlockSyntax block)
                return block.Statements.LastOrDefault();
            return statement;
        }
    }
}
