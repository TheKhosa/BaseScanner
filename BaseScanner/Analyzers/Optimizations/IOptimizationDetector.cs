using Microsoft.CodeAnalysis;
using BaseScanner.Context;

namespace BaseScanner.Analyzers.Optimizations;

/// <summary>
/// Interface for optimization detectors.
/// Each detector is responsible for finding a specific category of optimization opportunities.
/// </summary>
public interface IOptimizationDetector
{
    /// <summary>
    /// The category of optimizations this detector finds.
    /// </summary>
    string Category { get; }

    /// <summary>
    /// Detect optimization opportunities in the given document.
    /// </summary>
    Task<List<OptimizationOpportunity>> DetectAsync(
        Document document,
        SemanticModel semanticModel,
        SyntaxNode root,
        CodeContext context);
}
