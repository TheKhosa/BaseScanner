using System.Collections.Immutable;

namespace BaseScanner.Context;

/// <summary>
/// Deep method-level context information for optimization decisions.
/// </summary>
public record MethodContext
{
    // Identity
    public required string FullyQualifiedName { get; init; }
    public required string Name { get; init; }
    public required string ContainingTypeName { get; init; }
    public required string FilePath { get; init; }
    public required int StartLine { get; init; }
    public required int EndLine { get; init; }

    // Signature
    public ImmutableList<ParameterInfo> Parameters { get; init; } = [];
    public required string ReturnType { get; init; }
    public required bool IsAsync { get; init; }
    public required bool IsStatic { get; init; }
    public required AccessModifier Accessibility { get; init; }

    // Semantic understanding
    public MethodSemantics Semantics { get; init; } = new();

    // Call graph position
    public ImmutableHashSet<string> CalledMethods { get; init; } = [];
    public ImmutableHashSet<string> CalledByMethods { get; init; } = [];
    public bool IsEntryPoint { get; init; }
    public bool IsLeaf { get; init; }
    public int CallDepth { get; init; }

    // Data flow
    public ImmutableHashSet<string> ReadsFields { get; init; } = [];
    public ImmutableHashSet<string> WritesFields { get; init; } = [];
    public ImmutableHashSet<string> ReadsProperties { get; init; } = [];
    public ImmutableHashSet<string> WritesProperties { get; init; } = [];

    // Complexity
    public int CyclomaticComplexity { get; init; }
    public int NestingDepth { get; init; }
    public int LineCount { get; init; }
}

/// <summary>
/// Parameter information.
/// </summary>
public record ParameterInfo
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public bool IsRef { get; init; }
    public bool IsOut { get; init; }
    public bool IsParams { get; init; }
    public bool HasDefaultValue { get; init; }
}

/// <summary>
/// Semantic understanding of what a method does.
/// </summary>
public record MethodSemantics
{
    /// <summary>
    /// Inferred purpose of the method.
    /// </summary>
    public MethodPurpose Purpose { get; init; } = MethodPurpose.Unknown;

    /// <summary>
    /// Detected behaviors in the method.
    /// </summary>
    public ImmutableList<MethodBehavior> Behaviors { get; init; } = [];

    // I/O characteristics
    public bool PerformsIO { get; init; }
    public bool AccessesDatabase { get; init; }
    public bool AccessesNetwork { get; init; }
    public bool AccessesFileSystem { get; init; }

    // State mutation
    public StateMutationLevel StateMutation { get; init; } = StateMutationLevel.Unknown;
    public ImmutableList<string> MutatedFields { get; init; } = [];
    public ImmutableList<string> MutatedParameters { get; init; } = [];

    // Purity
    public bool IsPure { get; init; }
    public bool HasSideEffects { get; init; }

    // Thread safety
    public bool IsThreadSafe { get; init; }
    public ImmutableList<string> LocksAcquired { get; init; } = [];

    // Exceptions
    public ImmutableList<string> PotentialExceptions { get; init; } = [];
    public bool ThrowsExceptions { get; init; }
    public bool CatchesExceptions { get; init; }
}

/// <summary>
/// Inferred purpose of a method based on naming and behavior.
/// </summary>
public enum MethodPurpose
{
    Unknown,
    Getter,           // Pure data retrieval
    Setter,           // State mutation
    Transformer,      // Input -> Output transformation
    Validator,        // Validation/checking
    Factory,          // Object creation
    Initializer,      // Setup/initialization
    Disposer,         // Cleanup/disposal
    EventHandler,     // Event response
    Orchestrator,     // Coordinates multiple operations
    Calculator,       // Pure computation
    IO,               // File/Network/Database operations
    Logger            // Logging operations
}

/// <summary>
/// Specific behaviors detected in a method.
/// </summary>
public enum MethodBehavior
{
    ReadsState,
    WritesState,
    ThrowsExceptions,
    CatchesExceptions,
    PerformsLogging,
    PerformsValidation,
    CreatesObjects,
    DisposesResources,
    AwaitsAsync,
    UsesReflection,
    InvokesCallbacks,
    UsesLinq,
    IteratesCollection,
    ModifiesCollection
}

/// <summary>
/// Level of state mutation performed by a method.
/// </summary>
public enum StateMutationLevel
{
    Unknown,
    None,           // Pure function
    Local,          // Only local variables
    Parameters,     // Mutates ref/out parameters
    Instance,       // Mutates this object's state
    Static,         // Mutates static state
    External        // Mutates external state (DB, file, etc.)
}
