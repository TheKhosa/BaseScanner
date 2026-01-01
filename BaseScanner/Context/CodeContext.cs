using System.Collections.Immutable;

namespace BaseScanner.Context;

/// <summary>
/// Central repository for all code context information.
/// Provides codebase-wide understanding for optimization decisions.
/// </summary>
public record CodeContext
{
    public required string ProjectPath { get; init; }
    public required DateTime BuiltAt { get; init; }

    /// <summary>
    /// All methods indexed by fully qualified name.
    /// </summary>
    public ImmutableDictionary<string, MethodContext> Methods { get; init; } = ImmutableDictionary<string, MethodContext>.Empty;

    /// <summary>
    /// All types indexed by fully qualified name.
    /// </summary>
    public ImmutableDictionary<string, TypeContext> Types { get; init; } = ImmutableDictionary<string, TypeContext>.Empty;

    /// <summary>
    /// Call graph representing method call relationships.
    /// </summary>
    public CallGraph CallGraph { get; init; } = new();

    /// <summary>
    /// Usage metrics for prioritization.
    /// </summary>
    public ImmutableDictionary<string, UsageMetrics> UsageMetrics { get; init; } = ImmutableDictionary<string, UsageMetrics>.Empty;
}

/// <summary>
/// Type-level context information.
/// </summary>
public record TypeContext
{
    public required string FullyQualifiedName { get; init; }
    public required string Name { get; init; }
    public required string FilePath { get; init; }
    public required int Line { get; init; }
    public required TypeKind Kind { get; init; }
    public required AccessModifier Accessibility { get; init; }

    /// <summary>
    /// Base types and interfaces.
    /// </summary>
    public ImmutableList<string> BaseTypes { get; init; } = [];

    /// <summary>
    /// Types that inherit from this type.
    /// </summary>
    public ImmutableList<string> DerivedTypes { get; init; } = [];

    /// <summary>
    /// Types this type depends on.
    /// </summary>
    public ImmutableHashSet<string> Dependencies { get; init; } = [];

    /// <summary>
    /// Methods declared in this type.
    /// </summary>
    public ImmutableList<string> Methods { get; init; } = [];

    /// <summary>
    /// Fields declared in this type.
    /// </summary>
    public ImmutableList<string> Fields { get; init; } = [];

    /// <summary>
    /// Properties declared in this type.
    /// </summary>
    public ImmutableList<string> Properties { get; init; } = [];
}

public enum TypeKind
{
    Class,
    Struct,
    Interface,
    Enum,
    Record,
    Delegate
}

public enum AccessModifier
{
    Public,
    Internal,
    Protected,
    ProtectedInternal,
    Private,
    PrivateProtected
}

/// <summary>
/// Usage metrics for prioritizing optimizations.
/// </summary>
public record UsageMetrics
{
    /// <summary>
    /// Number of references to this symbol.
    /// </summary>
    public required int ReferenceCount { get; init; }

    /// <summary>
    /// Number of methods that call this method.
    /// </summary>
    public int CallerCount { get; init; }

    /// <summary>
    /// Number of methods this method calls.
    /// </summary>
    public int CalleeCount { get; init; }

    /// <summary>
    /// Distance from entry points (0 = entry point).
    /// </summary>
    public int DistanceFromEntryPoints { get; init; } = int.MaxValue;

    /// <summary>
    /// Whether this method is on a critical execution path.
    /// </summary>
    public bool OnCriticalPath { get; init; }

    /// <summary>
    /// Calculated importance score (0-100).
    /// </summary>
    public double ImportanceScore { get; init; }

    /// <summary>
    /// Reason for importance classification.
    /// </summary>
    public ImportanceReason ImportanceReason { get; init; }
}

public enum ImportanceReason
{
    Unknown,
    HighlyReferenced,
    FrequentlyModified,
    OnCriticalPath,
    EntryPoint,
    PublicAPI,
    HighComplexity
}
