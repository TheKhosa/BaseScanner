using Microsoft.CodeAnalysis;

namespace BaseScanner.Transformers.Core;

/// <summary>
/// Interface for code transformers that apply specific optimizations.
/// </summary>
public interface ICodeTransformer
{
    /// <summary>
    /// The type of transformation this transformer applies.
    /// </summary>
    string TransformationType { get; }

    /// <summary>
    /// Check if this transformer can handle the given syntax node.
    /// </summary>
    bool CanTransform(SyntaxNode node, SemanticModel semanticModel);

    /// <summary>
    /// Apply the transformation to the given syntax node.
    /// </summary>
    Task<TransformationResult> TransformAsync(TransformationContext context, SyntaxNode targetNode);
}

/// <summary>
/// Context for performing transformations.
/// </summary>
public record TransformationContext
{
    public required Workspace Workspace { get; init; }
    public required Solution Solution { get; init; }
    public required Project Project { get; init; }
    public required Document Document { get; init; }
    public required SemanticModel SemanticModel { get; init; }
    public required SyntaxNode SyntaxRoot { get; init; }
    public required Compilation Compilation { get; init; }
    public TransformationOptions Options { get; init; } = new();
}

/// <summary>
/// Options for controlling transformation behavior.
/// </summary>
public record TransformationOptions
{
    /// <summary>
    /// Whether to validate semantic equivalence after transformation.
    /// </summary>
    public bool ValidateSemantics { get; init; } = true;

    /// <summary>
    /// Whether to format the output code.
    /// </summary>
    public bool FormatOutput { get; init; } = true;

    /// <summary>
    /// Whether to preserve comments and whitespace.
    /// </summary>
    public bool PreserveTrivia { get; init; } = true;
}
