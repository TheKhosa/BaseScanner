using System.Text.Json.Serialization;

namespace BaseScanner.Services;

/// <summary>
/// Complete analysis result containing all analyzer outputs.
/// Designed for JSON serialization to MCP clients.
/// </summary>
public record AnalysisResult
{
    public required string ProjectPath { get; init; }
    public required AnalysisSummary Summary { get; init; }
    public List<string> UnusedFiles { get; init; } = [];
    public List<string> MissingFiles { get; init; } = [];

    // Deep analysis
    public List<DeprecatedCodeItem>? DeprecatedCode { get; init; }
    public List<UsageItem>? DeadCode { get; init; }
    public List<UsageItem>? LowUsageCode { get; init; }

    // Sentiment analysis
    public SentimentResult? Sentiment { get; init; }

    // Performance analysis
    public List<IssueItem>? PerformanceIssues { get; init; }

    // Exception handling
    public List<IssueItem>? ExceptionHandlingIssues { get; init; }

    // Resource leaks
    public List<IssueItem>? ResourceLeakIssues { get; init; }

    // Dependencies
    public DependencyResult? Dependencies { get; init; }

    // Magic values
    public List<MagicValueItem>? MagicValues { get; init; }

    // Git churn
    public GitChurnResult? GitChurn { get; init; }

    // Refactoring
    public RefactoringResult? Refactoring { get; init; }

    // Architecture
    public ArchitectureResult? Architecture { get; init; }

    // Safety
    public SafetyResult? Safety { get; init; }

    // Optimizations
    public OptimizationResult? Optimizations { get; init; }

    // Security Analysis
    public SecurityAnalysisResult? Security { get; init; }

    // Metrics Dashboard
    public MetricsDashboardResult? Metrics { get; init; }
}

// Security result types

public record SecurityAnalysisResult
{
    public int TotalVulnerabilities { get; init; }
    public int CriticalCount { get; init; }
    public int HighCount { get; init; }
    public int MediumCount { get; init; }
    public int LowCount { get; init; }
    public List<SecurityIssueItem> Vulnerabilities { get; init; } = [];
    public Dictionary<string, int> VulnerabilitiesByType { get; init; } = [];
    public Dictionary<string, int> VulnerabilitiesByCwe { get; init; } = [];
}

public record SecurityIssueItem
{
    public required string VulnerabilityType { get; init; }
    public required string Severity { get; init; }
    public required string CweId { get; init; }
    public required string FilePath { get; init; }
    public required int StartLine { get; init; }
    public required int EndLine { get; init; }
    public required string Description { get; init; }
    public required string Recommendation { get; init; }
    public required string VulnerableCode { get; init; }
    public required string SecureCode { get; init; }
    public required string Confidence { get; init; }
    public string CweLink => $"https://cwe.mitre.org/data/definitions/{CweId.Replace("CWE-", "")}.html";
}

// Metrics dashboard result types

public record MetricsDashboardResult
{
    public required int HealthScore { get; init; }
    public required int TotalFiles { get; init; }
    public required int TotalLines { get; init; }
    public required int TotalMethods { get; init; }
    public required int TotalClasses { get; init; }
    public required double AverageCyclomaticComplexity { get; init; }
    public required int MaxCyclomaticComplexity { get; init; }
    public required int MethodsAboveComplexityThreshold { get; init; }
    public required double MaintainabilityIndex { get; init; }
    public required int TechnicalDebtMinutes { get; init; }
    public List<HotspotFileItem> Hotspots { get; init; } = [];
    public Dictionary<string, int> IssuesByCategory { get; init; } = [];
    public Dictionary<string, int> IssuesBySeverity { get; init; } = [];
}

public record HotspotFileItem
{
    public required string FilePath { get; init; }
    public required int IssueCount { get; init; }
    public required int CriticalOrHighCount { get; init; }
    public required int Lines { get; init; }
    public required int Methods { get; init; }
}

public record AnalysisSummary
{
    public int TotalFilesOnDisk { get; init; }
    public int FilesInCompilation { get; init; }
    public int UnusedFiles { get; init; }
    public int MissingFiles { get; init; }
    public int PerformanceIssues { get; init; }
    public int ExceptionIssues { get; init; }
    public int ResourceIssues { get; init; }
    public int MagicValues { get; init; }
    public int LongMethods { get; init; }
    public int GodClasses { get; init; }
    public int NullSafetyIssues { get; init; }
    public int ImmutabilityOpportunities { get; init; }
    public int LoggingGaps { get; init; }
    public int OptimizationOpportunities { get; init; }
    public int TotalIssues { get; init; }
}

public record IssueItem
{
    public required string Type { get; init; }
    public required string Severity { get; init; }
    public required string Message { get; init; }
    public required string FilePath { get; init; }
    public required int Line { get; init; }
    public string? CodeSnippet { get; init; }
}

public record DeprecatedCodeItem
{
    public required string SymbolKind { get; init; }
    public required string SymbolName { get; init; }
    public required string FilePath { get; init; }
    public required int Line { get; init; }
    public required string Message { get; init; }
    public required bool IsError { get; init; }
    public string? Replacement { get; init; }
}

public record UsageItem
{
    public required string SymbolKind { get; init; }
    public required string SymbolName { get; init; }
    public required string FilePath { get; init; }
    public required int Line { get; init; }
    public required int ReferenceCount { get; init; }
}

public record SentimentResult
{
    public required int TotalBlocks { get; init; }
    public required double AverageQualityScore { get; init; }
    public required double AverageComplexity { get; init; }
    public required int HighComplexityCount { get; init; }
    public required int ProblematicCount { get; init; }
    public required int DuplicateGroups { get; init; }
    public required int SimilarGroups { get; init; }
    public required Dictionary<string, int> QualityDistribution { get; init; }
    public required Dictionary<string, int> MarkerCounts { get; init; }
    public List<CodeBlockItem>? ProblematicBlocks { get; init; }
    public List<CodeBlockItem>? HighComplexityBlocks { get; init; }
}

public record CodeBlockItem
{
    public required string FilePath { get; init; }
    public required int Line { get; init; }
    public required string BlockType { get; init; }
    public required string ContainingType { get; init; }
    public required string Name { get; init; }
    public required int QualityScore { get; init; }
    public required string QualityRating { get; init; }
    public required int CyclomaticComplexity { get; init; }
    public required int NestingDepth { get; init; }
    public required int LineCount { get; init; }
    public List<string> SentimentMarkers { get; init; } = [];
}

public record DependencyResult
{
    public List<CircularDependencyItem> CircularDependencies { get; init; } = [];
    public List<CouplingItem> HighCouplingTypes { get; init; } = [];
}

public record CircularDependencyItem
{
    public required string Type { get; init; }
    public required List<string> Cycle { get; init; }
}

public record CouplingItem
{
    public required string TypeName { get; init; }
    public required string FilePath { get; init; }
    public required int EfferentCoupling { get; init; }
    public required int AfferentCoupling { get; init; }
    public required double Instability { get; init; }
}

public record MagicValueItem
{
    public required string Type { get; init; }
    public required string Value { get; init; }
    public required int Occurrences { get; init; }
    public List<LocationItem> Locations { get; init; } = [];
}

public record LocationItem
{
    public required string FilePath { get; init; }
    public required int Line { get; init; }
}

public record GitChurnResult
{
    public required bool GitAvailable { get; init; }
    public List<FileChurnItem> TopChurnedFiles { get; init; } = [];
    public List<HotspotItem> Hotspots { get; init; } = [];
    public List<FileChurnItem> StaleFiles { get; init; } = [];
}

public record FileChurnItem
{
    public required string FilePath { get; init; }
    public required int CommitCount { get; init; }
    public required int TotalChurn { get; init; }
    public required int DaysSinceLastChange { get; init; }
}

public record HotspotItem
{
    public required string FilePath { get; init; }
    public required double Score { get; init; }
    public required int ChurnCount { get; init; }
    public required string Reason { get; init; }
}

public record RefactoringResult
{
    public List<LongMethodItem> LongMethods { get; init; } = [];
    public List<GodClassItem> GodClasses { get; init; } = [];
    public List<FeatureEnvyItem> FeatureEnvy { get; init; } = [];
    public List<ParameterSmellItem> ParameterSmells { get; init; } = [];
    public List<DataClumpItem> DataClumps { get; init; } = [];
}

public record LongMethodItem
{
    public required string FilePath { get; init; }
    public required int Line { get; init; }
    public required string ClassName { get; init; }
    public required string MethodName { get; init; }
    public required int LineCount { get; init; }
    public required int Complexity { get; init; }
    public List<ExtractCandidateItem> ExtractCandidates { get; init; } = [];
}

public record ExtractCandidateItem
{
    public required int StartLine { get; init; }
    public required int EndLine { get; init; }
    public required string SuggestedName { get; init; }
    public required string Reason { get; init; }
}

public record GodClassItem
{
    public required string FilePath { get; init; }
    public required int Line { get; init; }
    public required string ClassName { get; init; }
    public required int MethodCount { get; init; }
    public required int FieldCount { get; init; }
    public required double LCOM { get; init; }
    public List<string> Responsibilities { get; init; } = [];
}

public record FeatureEnvyItem
{
    public required string FilePath { get; init; }
    public required int Line { get; init; }
    public required string ClassName { get; init; }
    public required string MethodName { get; init; }
    public required string EnviedClass { get; init; }
    public required int EnviedMemberAccess { get; init; }
    public required double EnvyRatio { get; init; }
}

public record ParameterSmellItem
{
    public required string FilePath { get; init; }
    public required int Line { get; init; }
    public required string ClassName { get; init; }
    public required string MethodName { get; init; }
    public required int ParameterCount { get; init; }
    public required string SmellType { get; init; }
    public required string Suggestion { get; init; }
}

public record DataClumpItem
{
    public required List<string> Parameters { get; init; }
    public required int Occurrences { get; init; }
    public required string SuggestedClassName { get; init; }
    public List<string> Locations { get; init; } = [];
}

public record ArchitectureResult
{
    public List<PublicApiItem> PublicApi { get; init; } = [];
    public List<EntryPointItem> EntryPoints { get; init; } = [];
    public List<DeadEndItem> DeadEnds { get; init; } = [];
    public List<InheritanceItem> DeepInheritance { get; init; } = [];
    public List<CompositionCandidateItem> CompositionCandidates { get; init; } = [];
    public List<InterfaceIssueItem> InterfaceIssues { get; init; } = [];
}

public record PublicApiItem
{
    public required string TypeName { get; init; }
    public required string MemberName { get; init; }
    public required string MemberType { get; init; }
    public required string FilePath { get; init; }
    public required int Line { get; init; }
    public required string BreakingChangeRisk { get; init; }
}

public record EntryPointItem
{
    public required string TypeName { get; init; }
    public required string MethodName { get; init; }
    public required int OutgoingCalls { get; init; }
}

public record DeadEndItem
{
    public required string FilePath { get; init; }
    public required int Line { get; init; }
    public required string TypeName { get; init; }
    public required string MethodName { get; init; }
    public required int IncomingCalls { get; init; }
}

public record InheritanceItem
{
    public required string TypeName { get; init; }
    public required int Depth { get; init; }
    public required List<string> Chain { get; init; }
}

public record CompositionCandidateItem
{
    public required string FilePath { get; init; }
    public required int Line { get; init; }
    public required string TypeName { get; init; }
    public required string Suggestion { get; init; }
}

public record InterfaceIssueItem
{
    public required string FilePath { get; init; }
    public required int Line { get; init; }
    public required string InterfaceName { get; init; }
    public required int MemberCount { get; init; }
    public List<string> SuggestedSplits { get; init; } = [];
}

public record SafetyResult
{
    public List<NullSafetyItem> NullIssues { get; init; } = [];
    public List<ImmutabilityItem> ImmutabilityIssues { get; init; } = [];
    public List<LoggingGapItem> LoggingGaps { get; init; } = [];
    public double AverageLoggingCoverage { get; init; }
    public int ClassesWithLowCoverage { get; init; }
}

public record NullSafetyItem
{
    public required string Type { get; init; }
    public required string Severity { get; init; }
    public required string FilePath { get; init; }
    public required int Line { get; init; }
    public required string Description { get; init; }
}

public record ImmutabilityItem
{
    public required string Type { get; init; }
    public required string FilePath { get; init; }
    public required int Line { get; init; }
    public required string MemberName { get; init; }
    public required string Suggestion { get; init; }
}

public record LoggingGapItem
{
    public required string FilePath { get; init; }
    public required int Line { get; init; }
    public required string ClassName { get; init; }
    public required string MethodName { get; init; }
    public required string GapType { get; init; }
    public required string Description { get; init; }
}

// Optimization result types

public record OptimizationResult
{
    public List<OptimizationItem> Opportunities { get; init; } = [];
    public OptimizationSummary Summary { get; init; } = new();
}

public record OptimizationSummary
{
    public int TotalOpportunities { get; init; }
    public int HighConfidenceCount { get; init; }
    public int PerformanceOptimizations { get; init; }
    public int ReadabilityImprovements { get; init; }
    public int ModernizationOpportunities { get; init; }
    public double EstimatedImpactScore { get; init; }
}

public record OptimizationItem
{
    /// <summary>
    /// Category: Performance, Readability, Modernization
    /// </summary>
    public required string Category { get; init; }

    /// <summary>
    /// Specific optimization type (e.g., LinqAny, AsyncVoid, ListToHashSet)
    /// </summary>
    public required string Type { get; init; }

    public required string FilePath { get; init; }
    public required int StartLine { get; init; }
    public required int EndLine { get; init; }

    /// <summary>
    /// Human-readable description of the optimization opportunity.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// The current code that can be optimized.
    /// </summary>
    public required string CurrentCode { get; init; }

    /// <summary>
    /// The suggested optimized code.
    /// </summary>
    public required string SuggestedCode { get; init; }

    /// <summary>
    /// Confidence level: High, Medium, Low
    /// </summary>
    public required string Confidence { get; init; }

    /// <summary>
    /// Impact level: Critical, High, Medium, Low
    /// </summary>
    public required string Impact { get; init; }

    /// <summary>
    /// Whether the transformation is semantically safe.
    /// </summary>
    public required bool IsSemanticallySafe { get; init; }

    /// <summary>
    /// Assumptions required for the transformation to be safe.
    /// </summary>
    public List<string> Assumptions { get; init; } = [];

    /// <summary>
    /// Potential risks of applying this optimization.
    /// </summary>
    public List<string> Risks { get; init; } = [];
}

// Concurrency analysis result types

public record ConcurrencyAnalysisResultDto
{
    public int TotalIssues { get; init; }
    public int CriticalCount { get; init; }
    public int HighCount { get; init; }
    public int MediumCount { get; init; }
    public List<ConcurrencyIssueDto> Issues { get; init; } = [];
    public Dictionary<string, int> IssuesByType { get; init; } = [];
}

public record ConcurrencyIssueDto
{
    public required string IssueType { get; init; }
    public required string Severity { get; init; }
    public required string Message { get; init; }
    public required string FilePath { get; init; }
    public required int Line { get; init; }
    public required int EndLine { get; init; }
    public string? CodeSnippet { get; init; }
    public string? SuggestedFix { get; init; }
    public string? CweId { get; init; }
}

// Framework analysis result types

public record FrameworkAnalysisResultDto
{
    public required string Framework { get; init; }
    public int TotalIssues { get; init; }
    public int CriticalCount { get; init; }
    public int HighCount { get; init; }
    public int MediumCount { get; init; }
    public List<FrameworkIssueDto> Issues { get; init; } = [];
    public Dictionary<string, int> IssuesByType { get; init; } = [];
}

public record FrameworkIssueDto
{
    public required string IssueType { get; init; }
    public required string Severity { get; init; }
    public required string Message { get; init; }
    public required string FilePath { get; init; }
    public required int Line { get; init; }
    public string? CweId { get; init; }
    public string? SuggestedFix { get; init; }
    public string? CodeSnippet { get; init; }
}

// Code quality result types

public record CodeQualityResultDto
{
    public int TotalIssues { get; init; }
    public List<CodeQualityIssueDto> Issues { get; init; } = [];
    public List<MethodMetricsDto> MethodMetrics { get; init; } = [];
    public Dictionary<string, int> IssuesByCategory { get; init; } = [];
    public double AverageCognitiveComplexity { get; init; }
    public int MethodsAboveThreshold { get; init; }
}

public record CodeQualityIssueDto
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

public record MethodMetricsDto
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

// Virtual workspace comparison result

public record TransformationComparisonResultDto
{
    public required string OriginalFilePath { get; init; }
    public List<TransformationBranchResultDto> Results { get; init; } = [];
    public TransformationBranchResultDto? BestResult { get; init; }
    public int TotalStrategiesApplied { get; init; }
    public int FailedStrategies { get; init; }
}

public record TransformationBranchResultDto
{
    public required string StrategyName { get; init; }
    public required string Category { get; init; }
    public required string Description { get; init; }
    public required double OverallScore { get; init; }
    public double ComplexityDelta { get; init; }
    public double CognitiveComplexityDelta { get; init; }
    public int LocDelta { get; init; }
    public double MaintainabilityDelta { get; init; }
    public bool CompilationValid { get; init; }
    public bool SemanticsPreserved { get; init; }
    public string? UnifiedDiff { get; init; }
    public int AddedLines { get; init; }
    public int RemovedLines { get; init; }
    public string? Error { get; init; }
}
