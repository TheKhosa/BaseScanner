using Microsoft.CodeAnalysis;
using BaseScanner.VirtualWorkspace;

namespace BaseScanner.Refactoring.Models;

/// <summary>
/// Types of refactoring operations.
/// </summary>
public enum RefactoringType
{
    ExtractMethod,
    ExtractClass,
    SplitGodClass,
    SimplifyMethod,
    ExtractInterface,
    ReplaceConditional
}

/// <summary>
/// Severity levels for code smells.
/// </summary>
public enum SmellSeverity
{
    Low = 25,
    Medium = 50,
    High = 75,
    Critical = 100
}

/// <summary>
/// Types of code smells that can be refactored.
/// </summary>
public enum CodeSmellType
{
    GodClass,
    LargeClass,
    LongMethod,
    TooManyParameters,
    DeepNesting,
    FeatureEnvy,
    DataClump,
    PrimitiveObsession,
    SwitchStatement,
    ParallelInheritance,
    LazyClass,
    SpeculativeGenerality,
    TemporaryField,
    MessageChains,
    MiddleMan,
    InappropriateIntimacy,
    AlternativeClassesWithDifferentInterfaces,
    IncompleteLibraryClass,
    DataClass,
    RefusedBequest,
    Comments
}

/// <summary>
/// Represents a detected code smell that can be refactored.
/// </summary>
public record CodeSmell
{
    public required CodeSmellType SmellType { get; init; }
    public required SmellSeverity Severity { get; init; }
    public required string FilePath { get; init; }
    public required int StartLine { get; init; }
    public required int EndLine { get; init; }
    public required string TargetName { get; init; }
    public required string Description { get; init; }
    public Dictionary<string, object> Metrics { get; init; } = [];
}

/// <summary>
/// An opportunity for refactoring with applicable strategies.
/// </summary>
public record RefactoringOpportunity
{
    public required CodeSmell Smell { get; init; }
    public required DocumentId DocumentId { get; init; }
    public required List<RefactoringType> ApplicableStrategies { get; init; }
    public int EstimatedComplexityReduction { get; init; }
    public int EstimatedCohesionImprovement { get; init; }
    public string Recommendation { get; init; } = "";
}

/// <summary>
/// A plan for refactoring a project.
/// </summary>
public record RefactoringPlan
{
    public required string ProjectPath { get; init; }
    public required DateTime AnalyzedAt { get; init; }
    public required List<RefactoringOpportunity> Opportunities { get; init; }
    public RefactoringSummary Summary { get; init; } = new();
}

/// <summary>
/// Summary statistics for a refactoring plan.
/// </summary>
public record RefactoringSummary
{
    public int TotalOpportunities { get; init; }
    public int CriticalCount { get; init; }
    public int HighCount { get; init; }
    public int MediumCount { get; init; }
    public int LowCount { get; init; }
    public Dictionary<CodeSmellType, int> BySmellType { get; init; } = [];
    public Dictionary<RefactoringType, int> ByStrategyType { get; init; } = [];
    public int EstimatedTotalComplexityReduction { get; init; }
}

/// <summary>
/// Estimate of improvement from applying a refactoring strategy.
/// </summary>
public record RefactoringEstimate
{
    public required RefactoringType StrategyType { get; init; }
    public required bool CanApply { get; init; }
    public string? CannotApplyReason { get; init; }
    public double EstimatedComplexityReduction { get; init; }
    public double EstimatedCohesionImprovement { get; init; }
    public double EstimatedMaintainabilityGain { get; init; }
    public int EstimatedNewClassCount { get; init; }
    public int EstimatedNewMethodCount { get; init; }
    public List<string> ProposedNames { get; init; } = [];
}

/// <summary>
/// Extended score for refactoring operations.
/// </summary>
public record RefactoringScore
{
    // Base transformation metrics
    public required TransformationScore BaseScore { get; init; }

    // Cohesion metrics
    public double OriginalLCOM4 { get; init; }
    public double TransformedLCOM4 { get; init; }
    public double CohesionImprovement => OriginalLCOM4 - TransformedLCOM4;

    // Responsibility metrics
    public int OriginalResponsibilityCount { get; init; }
    public int TransformedResponsibilityCount { get; init; }
    public double ResponsibilitySeparationScore { get; init; }

    // Size metrics
    public int OriginalMethodCount { get; init; }
    public int TransformedMethodCount { get; init; }
    public double AverageMethodSizeReduction { get; init; }

    // Quality metrics
    public double TestabilityScore { get; init; }
    public double NamingQualityScore { get; init; }
    public double SingleResponsibilityScore { get; init; }

    // Weighted overall score (0-100)
    public double OverallRefactoringScore { get; init; }

    // Score breakdown
    public Dictionary<string, double> ScoreBreakdown { get; init; } = [];
}

/// <summary>
/// Result of comparing multiple refactoring strategies.
/// </summary>
public record RefactoringComparison
{
    public required Solution OriginalSolution { get; init; }
    public required Document OriginalDocument { get; init; }
    public required RefactoringOpportunity Opportunity { get; init; }
    public required List<RefactoringStrategyResult> Results { get; init; }
    public List<RefactoringStrategyResult> FailedResults { get; init; } = [];
    public RefactoringStrategyResult? BestResult { get; init; }
    public DateTime ComparedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Result of applying a single refactoring strategy.
/// </summary>
public record RefactoringStrategyResult
{
    public required RefactoringType StrategyType { get; init; }
    public required string StrategyName { get; init; }
    public required string Description { get; init; }
    public required string BranchName { get; init; }
    public required RefactoringScore Score { get; init; }
    public required DocumentDiff Diff { get; init; }
    public List<DocumentDiff> AdditionalDiffs { get; init; } = []; // For new files created
    public Solution? TransformedSolution { get; init; }
    public string? Error { get; init; }
    public List<string> Warnings { get; init; } = [];
    public RefactoringDetails Details { get; init; } = new();
}

/// <summary>
/// Details about what a refactoring changed.
/// </summary>
public record RefactoringDetails
{
    public List<string> ExtractedMethods { get; init; } = [];
    public List<string> ExtractedClasses { get; init; } = [];
    public List<string> ExtractedInterfaces { get; init; } = [];
    public List<string> RenamedSymbols { get; init; } = [];
    public List<string> MovedMembers { get; init; } = [];
    public List<string> AddedParameters { get; init; } = [];
    public List<string> RemovedDuplication { get; init; } = [];
}

/// <summary>
/// Result of applying a refactoring.
/// </summary>
public record RefactoringResult
{
    public required bool Success { get; init; }
    public required RefactoringStrategyResult StrategyResult { get; init; }
    public string? BackupId { get; init; }
    public List<string> ModifiedFiles { get; init; } = [];
    public List<string> CreatedFiles { get; init; } = [];
    public string? Error { get; init; }
    public DateTime AppliedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// A cluster of cohesive methods that belong together.
/// </summary>
public record CohesiveCluster
{
    public required string SuggestedClassName { get; init; }
    public required List<string> MethodNames { get; init; }
    public required List<string> SharedFields { get; init; }
    public required double CohesionScore { get; init; }
    public required int TotalComplexity { get; init; }
    public string SuggestedResponsibility { get; init; } = "";
}

/// <summary>
/// A logical responsibility boundary within a class.
/// </summary>
public record ResponsibilityBoundary
{
    public required string ResponsibilityName { get; init; }
    public required List<string> Methods { get; init; }
    public required List<string> Fields { get; init; }
    public required List<string> Properties { get; init; }
    public required double Cohesion { get; init; }
    public List<string> Dependencies { get; init; } = [];
}

/// <summary>
/// Order for composing strategies.
/// </summary>
public enum CompositionOrder
{
    Before,
    After,
    Either,
    Incompatible
}

/// <summary>
/// A chain of strategies to apply in sequence.
/// </summary>
public record StrategyChain
{
    public required List<RefactoringType> Strategies { get; init; }
    public required string Description { get; init; }
    public required double EstimatedImpact { get; init; }
    public List<string> Prerequisites { get; init; } = [];
}

/// <summary>
/// Result of applying a chain of strategies.
/// </summary>
public record ChainResult
{
    public required bool Success { get; init; }
    public required List<RefactoringResult> StepResults { get; init; }
    public RefactoringScore? FinalScore { get; init; }
    public int StepsCompleted { get; init; }
    public int TotalSteps { get; init; }
    public string? StoppedAtStep { get; init; }
    public string? StopReason { get; init; }
}

/// <summary>
/// Configuration for refactoring operations.
/// </summary>
public record RefactoringOptions
{
    public double MinimumScore { get; init; } = 0;
    public bool StopOnRegression { get; init; } = true;
    public bool CreateBackup { get; init; } = true;
    public bool PreservePublicApi { get; init; } = true;
    public int MaxStrategiesToCompare { get; init; } = 5;
    public List<RefactoringType> AllowedStrategies { get; init; } = [];
    public List<RefactoringType> ExcludedStrategies { get; init; } = [];
    public SmellSeverity MinimumSeverity { get; init; } = SmellSeverity.Medium;
}
