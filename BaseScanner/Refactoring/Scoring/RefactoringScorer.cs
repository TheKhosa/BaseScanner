using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using BaseScanner.VirtualWorkspace;
using BaseScanner.Refactoring.Models;
using BaseScanner.Refactoring.Analysis;

namespace BaseScanner.Refactoring.Scoring;

/// <summary>
/// Scores refactoring transformations using extended quality metrics.
/// </summary>
public class RefactoringScorer
{
    private readonly CohesionAnalyzer _cohesionAnalyzer;

    // Scoring weights (total = 100%)
    private const double CohesionWeight = 0.40;      // 40% - Most important for refactoring
    private const double ComplexityWeight = 0.30;    // 30% - Complexity reduction
    private const double MaintainabilityWeight = 0.20; // 20% - Maintainability improvement
    private const double NamingWeight = 0.10;        // 10% - Code quality/naming

    public RefactoringScorer(CohesionAnalyzer? cohesionAnalyzer = null)
    {
        _cohesionAnalyzer = cohesionAnalyzer ?? new CohesionAnalyzer();
    }

    /// <summary>
    /// Score a refactoring transformation with extended metrics.
    /// </summary>
    public async Task<RefactoringScore> ScoreRefactoringAsync(
        Document original,
        Document transformed,
        TransformationScore baseScore)
    {
        var origRoot = await original.GetSyntaxRootAsync();
        var transRoot = await transformed.GetSyntaxRootAsync();
        var origModel = await original.GetSemanticModelAsync();
        var transModel = await transformed.GetSemanticModelAsync();

        if (origRoot == null || transRoot == null)
        {
            return new RefactoringScore
            {
                BaseScore = baseScore,
                OverallRefactoringScore = baseScore.OverallScore
            };
        }

        // Calculate cohesion metrics
        var origLCOM4 = CalculateTotalLCOM4(origRoot, origModel);
        var transLCOM4 = CalculateTotalLCOM4(transRoot, transModel);

        // Calculate responsibility metrics
        var origResponsibilities = CountResponsibilities(origRoot, origModel);
        var transResponsibilities = CountResponsibilities(transRoot, transModel);
        var responsibilitySeparation = CalculateResponsibilitySeparation(
            origResponsibilities, transResponsibilities);

        // Calculate method metrics
        var origMethodCount = CountMethods(origRoot);
        var transMethodCount = CountMethods(transRoot);
        var avgMethodSizeReduction = CalculateAverageMethodSizeReduction(origRoot, transRoot);

        // Calculate quality scores
        var testabilityScore = CalculateTestabilityScore(transRoot, transModel);
        var namingScore = CalculateNamingQualityScore(transRoot);
        var srScore = CalculateSingleResponsibilityScore(transRoot, transModel);

        // Calculate overall score
        var scoreBreakdown = new Dictionary<string, double>();
        var overallScore = CalculateOverallScore(
            baseScore,
            origLCOM4,
            transLCOM4,
            responsibilitySeparation,
            testabilityScore,
            namingScore,
            scoreBreakdown);

        return new RefactoringScore
        {
            BaseScore = baseScore,
            OriginalLCOM4 = origLCOM4,
            TransformedLCOM4 = transLCOM4,
            OriginalResponsibilityCount = origResponsibilities,
            TransformedResponsibilityCount = transResponsibilities,
            ResponsibilitySeparationScore = responsibilitySeparation,
            OriginalMethodCount = origMethodCount,
            TransformedMethodCount = transMethodCount,
            AverageMethodSizeReduction = avgMethodSizeReduction,
            TestabilityScore = testabilityScore,
            NamingQualityScore = namingScore,
            SingleResponsibilityScore = srScore,
            OverallRefactoringScore = overallScore,
            ScoreBreakdown = scoreBreakdown
        };
    }

    #region LCOM Calculation

    private double CalculateTotalLCOM4(SyntaxNode root, SemanticModel? model)
    {
        if (model == null)
            return 1;

        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>().ToList();
        if (classes.Count == 0)
            return 1;

        var totalLCOM = 0.0;
        foreach (var classDecl in classes)
        {
            totalLCOM += _cohesionAnalyzer.CalculateLCOM4(classDecl, model);
        }

        return totalLCOM / classes.Count; // Average LCOM4
    }

    #endregion

    #region Responsibility Metrics

    private int CountResponsibilities(SyntaxNode root, SemanticModel? model)
    {
        if (model == null)
            return 1;

        var totalResponsibilities = 0;
        foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var clusters = _cohesionAnalyzer.FindCohesiveClusters(classDecl, model);
            totalResponsibilities += Math.Max(1, clusters.Count);
        }

        return totalResponsibilities;
    }

    private double CalculateResponsibilitySeparation(int original, int transformed)
    {
        // More responsibilities in separate classes = better (if each class is cohesive)
        // But we need to balance with cohesion
        if (transformed > original)
        {
            // Responsibilities were separated - good, but scale by improvement
            return Math.Min(1.0, (transformed - original) * 0.25);
        }
        else if (transformed < original)
        {
            // Responsibilities were consolidated - could be bad
            return -0.25 * (original - transformed);
        }
        return 0;
    }

    #endregion

    #region Method Metrics

    private int CountMethods(SyntaxNode root)
    {
        return root.DescendantNodes().OfType<MethodDeclarationSyntax>().Count();
    }

    private double CalculateAverageMethodSizeReduction(SyntaxNode original, SyntaxNode transformed)
    {
        var origMethods = original.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
        var transMethods = transformed.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();

        if (origMethods.Count == 0 || transMethods.Count == 0)
            return 0;

        var origAvgSize = origMethods.Average(m => m.GetText().Lines.Count);
        var transAvgSize = transMethods.Average(m => m.GetText().Lines.Count);

        return origAvgSize - transAvgSize; // Positive = reduction (good)
    }

    #endregion

    #region Quality Scores

    private double CalculateTestabilityScore(SyntaxNode root, SemanticModel? model)
    {
        var score = 100.0;

        foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            // Check for constructor injection (good for testability)
            var constructors = classDecl.Members.OfType<ConstructorDeclarationSyntax>().ToList();
            if (constructors.Any(c => c.ParameterList.Parameters.Count > 0))
            {
                score += 5; // Bonus for dependency injection
            }

            // Check for interface implementations
            if (classDecl.BaseList?.Types.Count > 0)
            {
                score += 5;
            }

            // Penalize static dependencies
            var staticCalls = classDecl.DescendantNodes()
                .OfType<MemberAccessExpressionSyntax>()
                .Count(ma => IsStaticMemberAccess(ma, model));

            score -= staticCalls * 2;

            // Penalize private dependencies (hard to mock)
            var newExpressions = classDecl.Members
                .OfType<FieldDeclarationSyntax>()
                .Where(f => f.Declaration.Variables.Any(v => v.Initializer?.Value is ObjectCreationExpressionSyntax))
                .Count();

            score -= newExpressions * 5;
        }

        return Math.Max(0, Math.Min(100, score));
    }

    private bool IsStaticMemberAccess(MemberAccessExpressionSyntax memberAccess, SemanticModel? model)
    {
        if (model == null)
            return false;

        var symbol = model.GetSymbolInfo(memberAccess).Symbol;
        return symbol?.IsStatic == true &&
               symbol.ContainingType?.Name != "Math" &&
               symbol.ContainingType?.Name != "String" &&
               symbol.ContainingType?.Name != "Console";
    }

    private double CalculateNamingQualityScore(SyntaxNode root)
    {
        var score = 100.0;
        var issues = 0;

        // Check method names
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var name = method.Identifier.Text;

            // Penalize very short names (except for standard patterns)
            if (name.Length < 4 && name != "Get" && name != "Set" && name != "Add")
            {
                issues++;
            }

            // Penalize names that don't start with verb (for non-property-like methods)
            if (!StartsWithVerb(name) && !IsPropertyLike(name))
            {
                issues++;
            }

            // Penalize generic names
            if (IsGenericName(name))
            {
                issues += 2;
            }
        }

        // Check class names
        foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var name = classDecl.Identifier.Text;

            // Penalize very short names
            if (name.Length < 4)
            {
                issues++;
            }

            // Penalize "Helper", "Manager", "Processor" suffixes (often indicate god classes)
            if (name.EndsWith("Helper") || name.EndsWith("Manager") || name.EndsWith("Processor"))
            {
                issues++;
            }
        }

        score -= issues * 5;
        return Math.Max(0, Math.Min(100, score));
    }

    private bool StartsWithVerb(string name)
    {
        var verbs = new[] { "Get", "Set", "Create", "Build", "Calculate", "Compute", "Find",
            "Search", "Load", "Save", "Update", "Delete", "Remove", "Add", "Insert",
            "Process", "Handle", "Execute", "Run", "Start", "Stop", "Initialize", "Init",
            "Validate", "Check", "Verify", "Is", "Has", "Can", "Should", "Parse", "Format",
            "Convert", "Transform", "Map", "Filter", "Sort", "Order", "Group", "Merge",
            "Split", "Join", "Concat", "Append", "Prepend", "Clear", "Reset", "Refresh",
            "Notify", "Publish", "Subscribe", "Register", "Unregister", "Log", "Trace",
            "Debug", "Warn", "Error", "Throw", "Catch", "Try", "Render", "Display", "Show",
            "Hide", "Enable", "Disable", "Open", "Close", "Read", "Write", "Send", "Receive" };

        return verbs.Any(v => name.StartsWith(v, StringComparison.Ordinal));
    }

    private bool IsPropertyLike(string name)
    {
        // Patterns like OnPropertyChanged, ToString, etc.
        return name.StartsWith("On") || name.StartsWith("To") ||
               name.EndsWith("Changed") || name.EndsWith("Completed");
    }

    private bool IsGenericName(string name)
    {
        var genericNames = new[] { "DoWork", "Process", "Handle", "Execute", "Run",
            "Method", "Function", "Func", "Action", "Task", "Operation",
            "Data", "Info", "Item", "Element", "Thing", "Object", "Value" };

        return genericNames.Any(g => name.Equals(g, StringComparison.OrdinalIgnoreCase));
    }

    private double CalculateSingleResponsibilityScore(SyntaxNode root, SemanticModel? model)
    {
        if (model == null)
            return 50;

        var totalScore = 0.0;
        var classCount = 0;

        foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            classCount++;
            var lcom4 = _cohesionAnalyzer.CalculateLCOM4(classDecl, model);

            // LCOM4 of 1 = perfectly cohesive = 100 points
            // Each additional component reduces score
            var classScore = lcom4 <= 1 ? 100 : Math.Max(0, 100 - (lcom4 - 1) * 25);
            totalScore += classScore;
        }

        return classCount > 0 ? totalScore / classCount : 50;
    }

    #endregion

    #region Overall Score Calculation

    private double CalculateOverallScore(
        TransformationScore baseScore,
        double origLCOM4,
        double transLCOM4,
        double responsibilitySeparation,
        double testabilityScore,
        double namingScore,
        Dictionary<string, double> breakdown)
    {
        // Start with base validation
        if (!baseScore.CompilationValid)
        {
            breakdown["compilation"] = -100;
            return -100;
        }

        if (!baseScore.SemanticsPreserved)
        {
            breakdown["semantics"] = -50;
            return -50;
        }

        // Cohesion improvement (negative LCOM delta is good)
        var cohesionImprovement = origLCOM4 - transLCOM4;
        var cohesionScore = NormalizeCohesionImprovement(cohesionImprovement);
        breakdown["cohesion"] = cohesionScore * CohesionWeight * 100;

        // Complexity improvement (from base score, negative delta is good)
        var complexityScore = NormalizeComplexityImprovement(
            -baseScore.ComplexityDelta,
            -baseScore.CognitiveComplexityDelta);
        breakdown["complexity"] = complexityScore * ComplexityWeight * 100;

        // Maintainability improvement (positive delta is good)
        var maintainabilityScore = NormalizeMaintainabilityImprovement(
            baseScore.MaintainabilityDelta,
            testabilityScore);
        breakdown["maintainability"] = maintainabilityScore * MaintainabilityWeight * 100;

        // Naming quality
        var normalizedNaming = namingScore / 100.0;
        breakdown["naming"] = normalizedNaming * NamingWeight * 100;

        // Calculate weighted total
        var overall = (cohesionScore * CohesionWeight +
                       complexityScore * ComplexityWeight +
                       maintainabilityScore * MaintainabilityWeight +
                       normalizedNaming * NamingWeight) * 100;

        // Apply responsibility separation bonus/penalty
        overall += responsibilitySeparation * 10;
        breakdown["responsibility_bonus"] = responsibilitySeparation * 10;

        // Apply LOC reduction bonus (small methods are good)
        if (baseScore.LocDelta < 0)
        {
            var locBonus = Math.Min(10, -baseScore.LocDelta * 0.5);
            overall += locBonus;
            breakdown["loc_bonus"] = locBonus;
        }

        return Math.Max(-100, Math.Min(100, overall));
    }

    private double NormalizeCohesionImprovement(double improvement)
    {
        // LCOM4 improvement of 1.0 = excellent (score 1.0)
        // No improvement = neutral (score 0.5)
        // Regression = bad (score < 0.5)
        if (improvement > 0)
        {
            return Math.Min(1.0, 0.5 + improvement * 0.25);
        }
        else if (improvement < 0)
        {
            return Math.Max(0, 0.5 + improvement * 0.5);
        }
        return 0.5;
    }

    private double NormalizeComplexityImprovement(double cyclomaticImprovement, double cognitiveImprovement)
    {
        // Combine both complexity metrics
        var combined = cyclomaticImprovement * 0.4 + cognitiveImprovement * 0.6;

        // Map to 0-1 scale
        // Improvement of 5 points = excellent (score 1.0)
        if (combined > 0)
        {
            return Math.Min(1.0, 0.5 + combined * 0.1);
        }
        else if (combined < 0)
        {
            return Math.Max(0, 0.5 + combined * 0.1);
        }
        return 0.5;
    }

    private double NormalizeMaintainabilityImprovement(double miDelta, double testabilityScore)
    {
        // Combine maintainability index delta with testability
        var miComponent = miDelta > 0 ? Math.Min(1.0, 0.5 + miDelta * 0.01) :
                          miDelta < 0 ? Math.Max(0, 0.5 + miDelta * 0.02) : 0.5;

        var testComponent = testabilityScore / 100.0;

        return miComponent * 0.6 + testComponent * 0.4;
    }

    #endregion
}
