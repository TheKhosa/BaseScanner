using Microsoft.CodeAnalysis;
using BaseScanner.VirtualWorkspace;
using BaseScanner.Refactoring.Models;

namespace BaseScanner.Refactoring.Strategies;

/// <summary>
/// Extended interface for refactoring strategies that work with the virtual workspace.
/// </summary>
public interface IRefactoringStrategy : ITransformationStrategy
{
    /// <summary>
    /// The type of refactoring this strategy performs.
    /// </summary>
    RefactoringType RefactoringType { get; }

    /// <summary>
    /// Code smell types this strategy can address.
    /// </summary>
    IReadOnlyList<CodeSmellType> AddressesSmells { get; }

    /// <summary>
    /// Estimate the improvement this strategy would provide.
    /// </summary>
    Task<RefactoringEstimate> EstimateImprovementAsync(Document document, CodeSmell? targetSmell = null);

    /// <summary>
    /// Apply the strategy targeting a specific code smell.
    /// </summary>
    Task<Solution> ApplyAsync(Solution solution, DocumentId documentId, CodeSmell targetSmell);

    /// <summary>
    /// Check if this strategy can be composed with another strategy.
    /// </summary>
    bool CanComposeWith(IRefactoringStrategy other);

    /// <summary>
    /// Get the order this strategy should be executed relative to another.
    /// </summary>
    CompositionOrder GetCompositionOrder(IRefactoringStrategy other);

    /// <summary>
    /// Get details about changes this strategy would make.
    /// </summary>
    Task<RefactoringDetails> GetProposedChangesAsync(Document document, CodeSmell targetSmell);
}

/// <summary>
/// Base class providing common functionality for refactoring strategies.
/// </summary>
public abstract class RefactoringStrategyBase : IRefactoringStrategy
{
    public abstract string Name { get; }
    public abstract string Category { get; }
    public abstract string Description { get; }
    public abstract RefactoringType RefactoringType { get; }
    public abstract IReadOnlyList<CodeSmellType> AddressesSmells { get; }

    public abstract Task<bool> CanApplyAsync(Document document);
    public abstract Task<Solution> ApplyAsync(Solution solution, DocumentId documentId);
    public abstract Task<RefactoringEstimate> EstimateImprovementAsync(Document document, CodeSmell? targetSmell = null);
    public abstract Task<Solution> ApplyAsync(Solution solution, DocumentId documentId, CodeSmell targetSmell);
    public abstract Task<RefactoringDetails> GetProposedChangesAsync(Document document, CodeSmell targetSmell);

    public virtual bool CanComposeWith(IRefactoringStrategy other)
    {
        return GetCompositionOrder(other) != CompositionOrder.Incompatible;
    }

    public virtual CompositionOrder GetCompositionOrder(IRefactoringStrategy other)
    {
        // Default composition rules based on strategy types
        return (RefactoringType, other.RefactoringType) switch
        {
            // SimplifyMethod should come before ExtractMethod
            (RefactoringType.SimplifyMethod, RefactoringType.ExtractMethod) => CompositionOrder.Before,
            (RefactoringType.ExtractMethod, RefactoringType.SimplifyMethod) => CompositionOrder.After,

            // ExtractMethod should come before ExtractClass
            (RefactoringType.ExtractMethod, RefactoringType.ExtractClass) => CompositionOrder.Before,
            (RefactoringType.ExtractClass, RefactoringType.ExtractMethod) => CompositionOrder.After,

            // ExtractMethod should come before SplitGodClass
            (RefactoringType.ExtractMethod, RefactoringType.SplitGodClass) => CompositionOrder.Before,
            (RefactoringType.SplitGodClass, RefactoringType.ExtractMethod) => CompositionOrder.After,

            // ExtractClass/SplitGodClass should come before ExtractInterface
            (RefactoringType.ExtractClass, RefactoringType.ExtractInterface) => CompositionOrder.Before,
            (RefactoringType.SplitGodClass, RefactoringType.ExtractInterface) => CompositionOrder.Before,
            (RefactoringType.ExtractInterface, RefactoringType.ExtractClass) => CompositionOrder.After,
            (RefactoringType.ExtractInterface, RefactoringType.SplitGodClass) => CompositionOrder.After,

            // ReplaceConditional is generally independent
            (RefactoringType.ReplaceConditional, _) => CompositionOrder.Either,
            (_, RefactoringType.ReplaceConditional) => CompositionOrder.Either,

            // Same type strategies are incompatible
            var (a, b) when a == b => CompositionOrder.Incompatible,

            // Default to either order
            _ => CompositionOrder.Either
        };
    }

    /// <summary>
    /// Generate a meaningful name based on the content.
    /// </summary>
    protected string GenerateMethodName(string content, string prefix = "")
    {
        // Extract verbs and nouns from the content
        var words = ExtractMeaningfulWords(content);
        if (words.Count == 0)
            return prefix + "ExtractedMethod";

        var name = string.Join("", words.Take(3).Select(ToPascalCase));
        return prefix + name;
    }

    /// <summary>
    /// Generate a meaningful class name based on responsibility.
    /// </summary>
    protected string GenerateClassName(string responsibility, string originalClassName)
    {
        if (string.IsNullOrWhiteSpace(responsibility))
            return originalClassName + "Helper";

        var words = ExtractMeaningfulWords(responsibility);
        if (words.Count == 0)
            return originalClassName + "Helper";

        return string.Join("", words.Take(2).Select(ToPascalCase)) + "Service";
    }

    private List<string> ExtractMeaningfulWords(string content)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "is", "are", "was", "were", "be", "been", "being",
            "have", "has", "had", "do", "does", "did", "will", "would", "could",
            "should", "may", "might", "must", "shall", "can", "if", "else",
            "for", "while", "return", "var", "new", "this", "that", "null",
            "true", "false", "void", "int", "string", "bool", "get", "set"
        };

        var words = System.Text.RegularExpressions.Regex.Split(content, @"[^a-zA-Z]+")
            .Where(w => w.Length > 2 && !stopWords.Contains(w))
            .Distinct()
            .ToList();

        return words;
    }

    private string ToPascalCase(string word)
    {
        if (string.IsNullOrEmpty(word))
            return word;
        return char.ToUpperInvariant(word[0]) + word.Substring(1).ToLowerInvariant();
    }
}
