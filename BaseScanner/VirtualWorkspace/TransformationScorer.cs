using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BaseScanner.VirtualWorkspace;

/// <summary>
/// Scores transformations based on multiple quality metrics.
/// </summary>
public class TransformationScorer
{
    public async Task<TransformationScore> ScoreAsync(Document original, Document transformed)
    {
        var origRoot = await original.GetSyntaxRootAsync();
        var transRoot = await transformed.GetSyntaxRootAsync();
        var origModel = await original.GetSemanticModelAsync();
        var transModel = await transformed.GetSemanticModelAsync();

        if (origRoot == null || transRoot == null)
        {
            return new TransformationScore { CompilationValid = false, OverallScore = -100 };
        }

        // Verify compilation success
        var transDiagnostics = transModel?.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList() ?? [];

        var compilationValid = transDiagnostics.Count == 0;

        // Calculate metrics
        var complexityDelta = CalculateComplexityDelta(origRoot, transRoot);
        var cognitiveComplexityDelta = CalculateCognitiveComplexityDelta(origRoot, transRoot);
        var locDelta = CalculateLocDelta(origRoot, transRoot);
        var maintainabilityDelta = CalculateMaintainabilityDelta(origRoot, transRoot);

        // Semantic preservation check
        var semanticsPreserved = CheckSemanticsPreserved(origRoot, transRoot);

        // Calculate overall score
        var overallScore = CalculateOverallScore(
            complexityDelta,
            cognitiveComplexityDelta,
            locDelta,
            maintainabilityDelta,
            compilationValid,
            semanticsPreserved);

        return new TransformationScore
        {
            ComplexityDelta = complexityDelta,
            CognitiveComplexityDelta = cognitiveComplexityDelta,
            LocDelta = locDelta,
            MaintainabilityDelta = maintainabilityDelta,
            CompilationValid = compilationValid,
            CompilationErrors = transDiagnostics.Select(d => d.GetMessage()).ToList(),
            SemanticsPreserved = semanticsPreserved,
            OverallScore = overallScore
        };
    }

    private double CalculateComplexityDelta(SyntaxNode orig, SyntaxNode trans)
    {
        var origComplexity = CalculateCyclomaticComplexity(orig);
        var transComplexity = CalculateCyclomaticComplexity(trans);
        return transComplexity - origComplexity; // Negative = improvement
    }

    public static int CalculateCyclomaticComplexity(SyntaxNode node)
    {
        var complexity = 1;

        complexity += node.DescendantNodes().Count(n => n is
            IfStatementSyntax or
            ConditionalExpressionSyntax or
            WhileStatementSyntax or
            ForStatementSyntax or
            ForEachStatementSyntax or
            CaseSwitchLabelSyntax or
            CasePatternSwitchLabelSyntax or
            CatchClauseSyntax or
            ConditionalAccessExpressionSyntax or
            BinaryExpressionSyntax { RawKind: (int)SyntaxKind.CoalesceExpression });

        // Count logical operators
        complexity += node.DescendantTokens().Count(t =>
            t.IsKind(SyntaxKind.AmpersandAmpersandToken) ||
            t.IsKind(SyntaxKind.BarBarToken));

        return complexity;
    }

    private double CalculateCognitiveComplexityDelta(SyntaxNode orig, SyntaxNode trans)
    {
        var origCognitive = CalculateCognitiveComplexity(orig);
        var transCognitive = CalculateCognitiveComplexity(trans);
        return transCognitive - origCognitive; // Negative = improvement
    }

    public static int CalculateCognitiveComplexity(SyntaxNode node)
    {
        var walker = new CognitiveComplexityWalker();
        walker.Visit(node);
        return walker.Complexity;
    }

    private int CalculateLocDelta(SyntaxNode orig, SyntaxNode trans)
    {
        var origLines = orig.GetText().Lines.Count;
        var transLines = trans.GetText().Lines.Count;
        return transLines - origLines; // Negative = improvement
    }

    private double CalculateMaintainabilityDelta(SyntaxNode orig, SyntaxNode trans)
    {
        var origIndex = CalculateMaintainabilityIndex(orig);
        var transIndex = CalculateMaintainabilityIndex(trans);
        return transIndex - origIndex; // Positive = improvement
    }

    public static double CalculateMaintainabilityIndex(SyntaxNode root)
    {
        var loc = root.GetText().Lines.Count;
        var cc = CalculateCyclomaticComplexity(root);
        var hv = CalculateHalsteadVolume(root);

        if (loc <= 0 || hv <= 0) return 100;

        // MI = 171 - 5.2 * ln(HV) - 0.23 * CC - 16.2 * ln(LOC)
        var mi = 171 - 5.2 * Math.Log(hv) - 0.23 * cc - 16.2 * Math.Log(loc);
        return Math.Max(0, Math.Min(100, mi));
    }

    public static double CalculateHalsteadVolume(SyntaxNode node)
    {
        var operators = new HashSet<string>();
        var operands = new HashSet<string>();
        var totalOperators = 0;
        var totalOperands = 0;

        foreach (var token in node.DescendantTokens())
        {
            if (token.IsKeyword() || IsOperator(token))
            {
                operators.Add(token.Text);
                totalOperators++;
            }
            else if (token.IsKind(SyntaxKind.IdentifierToken) ||
                     token.IsKind(SyntaxKind.NumericLiteralToken) ||
                     token.IsKind(SyntaxKind.StringLiteralToken))
            {
                operands.Add(token.Text);
                totalOperands++;
            }
        }

        var vocabulary = operators.Count + operands.Count;
        var length = totalOperators + totalOperands;

        if (vocabulary <= 0) return 1;
        return length * Math.Log2(vocabulary);
    }

    private static bool IsOperator(SyntaxToken token)
    {
        return token.IsKind(SyntaxKind.PlusToken) ||
               token.IsKind(SyntaxKind.MinusToken) ||
               token.IsKind(SyntaxKind.AsteriskToken) ||
               token.IsKind(SyntaxKind.SlashToken) ||
               token.IsKind(SyntaxKind.EqualsToken) ||
               token.IsKind(SyntaxKind.EqualsEqualsToken) ||
               token.IsKind(SyntaxKind.ExclamationEqualsToken) ||
               token.IsKind(SyntaxKind.LessThanToken) ||
               token.IsKind(SyntaxKind.GreaterThanToken) ||
               token.IsKind(SyntaxKind.AmpersandAmpersandToken) ||
               token.IsKind(SyntaxKind.BarBarToken) ||
               token.IsKind(SyntaxKind.QuestionQuestionToken) ||
               token.IsKind(SyntaxKind.QuestionToken);
    }

    private bool CheckSemanticsPreserved(SyntaxNode orig, SyntaxNode trans)
    {
        // Check public API surface preserved
        var origPublic = GetPublicMembers(orig);
        var transPublic = GetPublicMembers(trans);
        return origPublic.All(m => transPublic.Contains(m));
    }

    private HashSet<string> GetPublicMembers(SyntaxNode root)
    {
        var members = new HashSet<string>();

        foreach (var member in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            if (member.Modifiers.Any(SyntaxKind.PublicKeyword))
            {
                var name = member switch
                {
                    MethodDeclarationSyntax m => m.Identifier.Text,
                    PropertyDeclarationSyntax p => p.Identifier.Text,
                    FieldDeclarationSyntax f => f.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
                    ClassDeclarationSyntax c => c.Identifier.Text,
                    _ => null
                };
                if (name != null) members.Add(name);
            }
        }

        return members;
    }

    private double CalculateOverallScore(
        double complexityDelta,
        double cognitiveComplexityDelta,
        int locDelta,
        double maintainabilityDelta,
        bool compilationValid,
        bool semanticsPreserved)
    {
        if (!compilationValid) return -100;
        if (!semanticsPreserved) return -50;

        var score = 50.0;

        // Complexity reduction is good (weight: 2)
        score -= complexityDelta * 2;

        // Cognitive complexity reduction is good (weight: 3)
        score -= cognitiveComplexityDelta * 3;

        // LOC reduction is good (weight: 0.5)
        score -= locDelta * 0.5;

        // Maintainability improvement is good (weight: 2)
        score += maintainabilityDelta * 2;

        return Math.Max(-100, Math.Min(100, score));
    }
}

/// <summary>
/// Calculates cognitive complexity using Sonar's approach.
/// </summary>
public class CognitiveComplexityWalker : CSharpSyntaxWalker
{
    public int Complexity { get; private set; }
    public List<CognitiveComplexityContributor> Contributors { get; } = new();
    private int _nestingLevel;

    public override void VisitIfStatement(IfStatementSyntax node)
    {
        var increment = 1 + _nestingLevel;
        Complexity += increment;
        Contributors.Add(new CognitiveComplexityContributor("if", increment, node.GetLocation()));

        _nestingLevel++;
        base.VisitIfStatement(node);
        _nestingLevel--;
    }

    public override void VisitElseClause(ElseClauseSyntax node)
    {
        if (node.Statement is not IfStatementSyntax)
        {
            Complexity += 1;
            Contributors.Add(new CognitiveComplexityContributor("else", 1, node.GetLocation()));
        }
        base.VisitElseClause(node);
    }

    public override void VisitForStatement(ForStatementSyntax node)
    {
        var increment = 1 + _nestingLevel;
        Complexity += increment;
        Contributors.Add(new CognitiveComplexityContributor("for", increment, node.GetLocation()));

        _nestingLevel++;
        base.VisitForStatement(node);
        _nestingLevel--;
    }

    public override void VisitForEachStatement(ForEachStatementSyntax node)
    {
        var increment = 1 + _nestingLevel;
        Complexity += increment;
        Contributors.Add(new CognitiveComplexityContributor("foreach", increment, node.GetLocation()));

        _nestingLevel++;
        base.VisitForEachStatement(node);
        _nestingLevel--;
    }

    public override void VisitWhileStatement(WhileStatementSyntax node)
    {
        var increment = 1 + _nestingLevel;
        Complexity += increment;
        Contributors.Add(new CognitiveComplexityContributor("while", increment, node.GetLocation()));

        _nestingLevel++;
        base.VisitWhileStatement(node);
        _nestingLevel--;
    }

    public override void VisitDoStatement(DoStatementSyntax node)
    {
        var increment = 1 + _nestingLevel;
        Complexity += increment;
        Contributors.Add(new CognitiveComplexityContributor("do-while", increment, node.GetLocation()));

        _nestingLevel++;
        base.VisitDoStatement(node);
        _nestingLevel--;
    }

    public override void VisitCatchClause(CatchClauseSyntax node)
    {
        var increment = 1 + _nestingLevel;
        Complexity += increment;
        Contributors.Add(new CognitiveComplexityContributor("catch", increment, node.GetLocation()));

        _nestingLevel++;
        base.VisitCatchClause(node);
        _nestingLevel--;
    }

    public override void VisitSwitchStatement(SwitchStatementSyntax node)
    {
        var increment = 1 + _nestingLevel;
        Complexity += increment;
        Contributors.Add(new CognitiveComplexityContributor("switch", increment, node.GetLocation()));

        _nestingLevel++;
        base.VisitSwitchStatement(node);
        _nestingLevel--;
    }

    public override void VisitSwitchExpression(SwitchExpressionSyntax node)
    {
        var increment = 1 + _nestingLevel;
        Complexity += increment;
        Contributors.Add(new CognitiveComplexityContributor("switch expression", increment, node.GetLocation()));

        _nestingLevel++;
        base.VisitSwitchExpression(node);
        _nestingLevel--;
    }

    public override void VisitConditionalExpression(ConditionalExpressionSyntax node)
    {
        var increment = 1 + _nestingLevel;
        Complexity += increment;
        Contributors.Add(new CognitiveComplexityContributor("ternary", increment, node.GetLocation()));

        base.VisitConditionalExpression(node);
    }

    public override void VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        if (node.IsKind(SyntaxKind.LogicalAndExpression) ||
            node.IsKind(SyntaxKind.LogicalOrExpression))
        {
            // Only count if this is not part of a chain (parent is not same operator)
            if (node.Parent is not BinaryExpressionSyntax parent ||
                !parent.OperatorToken.IsKind(node.OperatorToken.Kind()))
            {
                Complexity += 1;
                Contributors.Add(new CognitiveComplexityContributor(
                    node.IsKind(SyntaxKind.LogicalAndExpression) ? "&&" : "||",
                    1, node.GetLocation()));
            }
        }
        base.VisitBinaryExpression(node);
    }

    public override void VisitGotoStatement(GotoStatementSyntax node)
    {
        Complexity += 1;
        Contributors.Add(new CognitiveComplexityContributor("goto", 1, node.GetLocation()));
        base.VisitGotoStatement(node);
    }

    public override void VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
    {
        _nestingLevel++;
        base.VisitLocalFunctionStatement(node);
        _nestingLevel--;
    }

    public override void VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node)
    {
        _nestingLevel++;
        base.VisitAnonymousMethodExpression(node);
        _nestingLevel--;
    }

    public override void VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
    {
        _nestingLevel++;
        base.VisitSimpleLambdaExpression(node);
        _nestingLevel--;
    }

    public override void VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
    {
        _nestingLevel++;
        base.VisitParenthesizedLambdaExpression(node);
        _nestingLevel--;
    }
}

public record CognitiveComplexityContributor(string Construct, int Increment, Location Location);
