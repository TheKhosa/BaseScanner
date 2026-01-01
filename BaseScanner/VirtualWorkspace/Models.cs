using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace BaseScanner.VirtualWorkspace;

/// <summary>
/// Interface for transformation strategies.
/// </summary>
public interface ITransformationStrategy
{
    string Name { get; }
    string Category { get; }
    string Description { get; }

    Task<Solution> ApplyAsync(Solution solution, DocumentId documentId);
    Task<bool> CanApplyAsync(Document document);
}

/// <summary>
/// Score for a transformation.
/// </summary>
public record TransformationScore
{
    public double ComplexityDelta { get; init; }
    public double CognitiveComplexityDelta { get; init; }
    public int LocDelta { get; init; }
    public double MaintainabilityDelta { get; init; }
    public bool CompilationValid { get; init; }
    public List<string> CompilationErrors { get; init; } = [];
    public bool SemanticsPreserved { get; init; }
    public double OverallScore { get; init; }
}

/// <summary>
/// Result of a transformation on a branch.
/// </summary>
public record TransformationBranchResult
{
    public required string StrategyName { get; init; }
    public required string Category { get; init; }
    public required string Description { get; init; }
    public required string BranchName { get; init; }
    public required TransformationScore Score { get; init; }
    public required DocumentDiff Diff { get; init; }
    public Solution? TransformedSolution { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Comparison of multiple transformations.
/// </summary>
public record TransformationComparison
{
    public required Solution Original { get; init; }
    public required Document OriginalDocument { get; init; }
    public required List<TransformationBranchResult> Results { get; init; }
    public List<TransformationBranchResult> FailedResults { get; init; } = [];
    public TransformationBranchResult? BestResult { get; init; }
}

/// <summary>
/// Diff between two document versions.
/// </summary>
public record DocumentDiff
{
    public string? OriginalPath { get; init; }
    public List<TextChange> TextChanges { get; init; } = [];
    public string UnifiedDiff { get; init; } = "";
    public List<SyntaxChange> SyntaxChanges { get; init; } = [];
    public int AddedLines { get; init; }
    public int RemovedLines { get; init; }
    public int ModifiedRegions { get; init; }
}

/// <summary>
/// A syntax-level change.
/// </summary>
public record SyntaxChange
{
    public SyntaxChangeType ChangeType { get; init; }
    public required string NodeType { get; init; }
    public required string Name { get; init; }
    public string? OriginalCode { get; init; }
    public string? NewCode { get; init; }
}

public enum SyntaxChangeType
{
    Added,
    Removed,
    Modified
}
