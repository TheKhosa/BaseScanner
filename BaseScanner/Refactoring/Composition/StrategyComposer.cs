using BaseScanner.Refactoring.Models;
using BaseScanner.Refactoring.Strategies;

namespace BaseScanner.Refactoring.Composition;

/// <summary>
/// Composes refactoring strategies into optimal execution chains.
/// </summary>
public class StrategyComposer
{
    private readonly Dictionary<RefactoringType, IRefactoringStrategy> _strategies;

    public StrategyComposer()
    {
        _strategies = new Dictionary<RefactoringType, IRefactoringStrategy>();
    }

    /// <summary>
    /// Register a strategy for use in composition.
    /// </summary>
    public void RegisterStrategy(IRefactoringStrategy strategy)
    {
        _strategies[strategy.RefactoringType] = strategy;
    }

    /// <summary>
    /// Compose an optimal chain for remediating a god class.
    /// </summary>
    public StrategyChain ComposeForGodClass()
    {
        // Optimal order for god class remediation:
        // 1. SimplifyMethod - Reduce complexity within methods first
        // 2. ExtractMethod - Break down long methods
        // 3. SplitGodClass OR ExtractClass - Split by responsibility
        // 4. ExtractInterface - Add interfaces for testability

        return new StrategyChain
        {
            Strategies = new List<RefactoringType>
            {
                RefactoringType.SimplifyMethod,
                RefactoringType.ExtractMethod,
                RefactoringType.SplitGodClass,
                RefactoringType.ExtractInterface
            },
            Description = "Comprehensive god class remediation: simplify methods, extract methods, split class, add interfaces",
            EstimatedImpact = 80,
            Prerequisites = new List<string>
            {
                "Class has multiple responsibilities (LCOM4 > 1)",
                "Class has more than 15 methods"
            }
        };
    }

    /// <summary>
    /// Compose an optimal chain for remediating a long method.
    /// </summary>
    public StrategyChain ComposeForLongMethod()
    {
        return new StrategyChain
        {
            Strategies = new List<RefactoringType>
            {
                RefactoringType.SimplifyMethod,
                RefactoringType.ExtractMethod
            },
            Description = "Long method remediation: simplify with guard clauses, then extract cohesive blocks",
            EstimatedImpact = 60,
            Prerequisites = new List<string>
            {
                "Method has more than 30 lines",
                "Method has deep nesting or complex conditionals"
            }
        };
    }

    /// <summary>
    /// Compose an optimal chain for improving testability.
    /// </summary>
    public StrategyChain ComposeForTestability()
    {
        return new StrategyChain
        {
            Strategies = new List<RefactoringType>
            {
                RefactoringType.ExtractInterface,
                RefactoringType.ExtractClass,
                RefactoringType.ReplaceConditional
            },
            Description = "Testability improvement: extract interfaces, separate concerns, use polymorphism",
            EstimatedImpact = 70,
            Prerequisites = new List<string>
            {
                "Class has public methods without interfaces",
                "Class has dependencies that are hard to mock"
            }
        };
    }

    /// <summary>
    /// Compose an optimal chain for reducing complexity.
    /// </summary>
    public StrategyChain ComposeForComplexity()
    {
        return new StrategyChain
        {
            Strategies = new List<RefactoringType>
            {
                RefactoringType.SimplifyMethod,
                RefactoringType.ReplaceConditional,
                RefactoringType.ExtractMethod
            },
            Description = "Complexity reduction: simplify methods, replace conditionals, extract methods",
            EstimatedImpact = 65,
            Prerequisites = new List<string>
            {
                "High cyclomatic complexity",
                "Deep nesting or complex switch statements"
            }
        };
    }

    /// <summary>
    /// Compose a custom chain from specified strategies.
    /// </summary>
    public StrategyChain ComposeCustom(IEnumerable<RefactoringType> strategies, string description)
    {
        var orderedStrategies = OrderStrategies(strategies.ToList());

        return new StrategyChain
        {
            Strategies = orderedStrategies,
            Description = description,
            EstimatedImpact = CalculateEstimatedImpact(orderedStrategies)
        };
    }

    /// <summary>
    /// Compose an optimal chain for a specific code smell.
    /// </summary>
    public StrategyChain ComposeForSmell(CodeSmellType smell)
    {
        return smell switch
        {
            CodeSmellType.GodClass => ComposeForGodClass(),
            CodeSmellType.LargeClass => ComposeForGodClass(),
            CodeSmellType.LongMethod => ComposeForLongMethod(),
            CodeSmellType.DeepNesting => new StrategyChain
            {
                Strategies = new List<RefactoringType>
                {
                    RefactoringType.SimplifyMethod,
                    RefactoringType.ExtractMethod
                },
                Description = "Deep nesting remediation: simplify with guard clauses, extract nested blocks",
                EstimatedImpact = 50
            },
            CodeSmellType.SwitchStatement => new StrategyChain
            {
                Strategies = new List<RefactoringType>
                {
                    RefactoringType.ReplaceConditional
                },
                Description = "Replace switch with polymorphism",
                EstimatedImpact = 40
            },
            CodeSmellType.FeatureEnvy => new StrategyChain
            {
                Strategies = new List<RefactoringType>
                {
                    RefactoringType.ExtractClass,
                    RefactoringType.ExtractMethod
                },
                Description = "Move envious methods to the class they envy",
                EstimatedImpact = 35
            },
            _ => new StrategyChain
            {
                Strategies = new List<RefactoringType>
                {
                    RefactoringType.SimplifyMethod,
                    RefactoringType.ExtractMethod
                },
                Description = "General code improvement chain",
                EstimatedImpact = 30
            }
        };
    }

    /// <summary>
    /// Order strategies according to composition rules.
    /// </summary>
    public List<RefactoringType> OrderStrategies(List<RefactoringType> strategies)
    {
        if (strategies.Count <= 1)
            return strategies;

        // Use topological sort based on composition order
        var graph = new Dictionary<RefactoringType, List<RefactoringType>>();
        var inDegree = new Dictionary<RefactoringType, int>();

        // Initialize
        foreach (var s in strategies)
        {
            graph[s] = new List<RefactoringType>();
            inDegree[s] = 0;
        }

        // Build edges based on composition order
        foreach (var s1 in strategies)
        {
            foreach (var s2 in strategies)
            {
                if (s1 == s2)
                    continue;

                var order = GetCompositionOrder(s1, s2);
                if (order == CompositionOrder.Before)
                {
                    graph[s1].Add(s2);
                    inDegree[s2]++;
                }
            }
        }

        // Topological sort (Kahn's algorithm)
        var result = new List<RefactoringType>();
        var queue = new Queue<RefactoringType>();

        foreach (var s in strategies.Where(s => inDegree[s] == 0))
        {
            queue.Enqueue(s);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            result.Add(current);

            foreach (var neighbor in graph[current])
            {
                inDegree[neighbor]--;
                if (inDegree[neighbor] == 0)
                {
                    queue.Enqueue(neighbor);
                }
            }
        }

        // If not all strategies were added (cycle detected), fall back to priority order
        if (result.Count != strategies.Count)
        {
            return strategies.OrderBy(GetStrategyPriority).ToList();
        }

        return result;
    }

    /// <summary>
    /// Check if two strategies can be composed together.
    /// </summary>
    public bool CanCompose(RefactoringType first, RefactoringType second)
    {
        return GetCompositionOrder(first, second) != CompositionOrder.Incompatible;
    }

    /// <summary>
    /// Get the composition order between two strategies.
    /// </summary>
    public CompositionOrder GetCompositionOrder(RefactoringType first, RefactoringType second)
    {
        return (first, second) switch
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

            // SimplifyMethod should come before SplitGodClass
            (RefactoringType.SimplifyMethod, RefactoringType.SplitGodClass) => CompositionOrder.Before,
            (RefactoringType.SplitGodClass, RefactoringType.SimplifyMethod) => CompositionOrder.After,

            // SimplifyMethod should come before ExtractClass
            (RefactoringType.SimplifyMethod, RefactoringType.ExtractClass) => CompositionOrder.Before,
            (RefactoringType.ExtractClass, RefactoringType.SimplifyMethod) => CompositionOrder.After,

            // ExtractClass/SplitGodClass should come before ExtractInterface
            (RefactoringType.ExtractClass, RefactoringType.ExtractInterface) => CompositionOrder.Before,
            (RefactoringType.SplitGodClass, RefactoringType.ExtractInterface) => CompositionOrder.Before,
            (RefactoringType.ExtractInterface, RefactoringType.ExtractClass) => CompositionOrder.After,
            (RefactoringType.ExtractInterface, RefactoringType.SplitGodClass) => CompositionOrder.After,

            // ExtractMethod should come before ExtractInterface
            (RefactoringType.ExtractMethod, RefactoringType.ExtractInterface) => CompositionOrder.Before,
            (RefactoringType.ExtractInterface, RefactoringType.ExtractMethod) => CompositionOrder.After,

            // SimplifyMethod should come before ReplaceConditional
            (RefactoringType.SimplifyMethod, RefactoringType.ReplaceConditional) => CompositionOrder.Before,
            (RefactoringType.ReplaceConditional, RefactoringType.SimplifyMethod) => CompositionOrder.After,

            // ReplaceConditional should come before ExtractClass (cleaner to extract after)
            (RefactoringType.ReplaceConditional, RefactoringType.ExtractClass) => CompositionOrder.Before,
            (RefactoringType.ExtractClass, RefactoringType.ReplaceConditional) => CompositionOrder.After,

            // ExtractClass and SplitGodClass are mutually exclusive
            (RefactoringType.ExtractClass, RefactoringType.SplitGodClass) => CompositionOrder.Incompatible,
            (RefactoringType.SplitGodClass, RefactoringType.ExtractClass) => CompositionOrder.Incompatible,

            // Same type strategies are incompatible
            var (a, b) when a == b => CompositionOrder.Incompatible,

            // Default to either order
            _ => CompositionOrder.Either
        };
    }

    /// <summary>
    /// Validate that a chain of strategies is valid.
    /// </summary>
    public (bool IsValid, string? Error) ValidateChain(StrategyChain chain)
    {
        if (chain.Strategies.Count == 0)
        {
            return (false, "Chain has no strategies");
        }

        // Check for duplicates
        var duplicates = chain.Strategies
            .GroupBy(s => s)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicates.Count > 0)
        {
            return (false, $"Chain contains duplicate strategies: {string.Join(", ", duplicates)}");
        }

        // Check for incompatible pairs
        for (var i = 0; i < chain.Strategies.Count - 1; i++)
        {
            var order = GetCompositionOrder(chain.Strategies[i], chain.Strategies[i + 1]);
            if (order == CompositionOrder.Incompatible)
            {
                return (false, $"Strategies {chain.Strategies[i]} and {chain.Strategies[i + 1]} are incompatible");
            }
            if (order == CompositionOrder.After)
            {
                return (false, $"Strategy {chain.Strategies[i]} should come after {chain.Strategies[i + 1]}");
            }
        }

        return (true, null);
    }

    /// <summary>
    /// Get all valid chains that can be composed from registered strategies.
    /// </summary>
    public List<StrategyChain> GetAllValidChains(int maxLength = 4)
    {
        var allStrategies = Enum.GetValues<RefactoringType>().ToList();
        var validChains = new List<StrategyChain>();

        // Generate all permutations up to maxLength
        for (var length = 1; length <= maxLength; length++)
        {
            foreach (var combination in GetCombinations(allStrategies, length))
            {
                var ordered = OrderStrategies(combination.ToList());
                var chain = new StrategyChain
                {
                    Strategies = ordered,
                    Description = GenerateDescription(ordered),
                    EstimatedImpact = CalculateEstimatedImpact(ordered)
                };

                var (isValid, _) = ValidateChain(chain);
                if (isValid)
                {
                    validChains.Add(chain);
                }
            }
        }

        return validChains
            .OrderByDescending(c => c.EstimatedImpact)
            .ToList();
    }

    private int GetStrategyPriority(RefactoringType type)
    {
        // Lower number = higher priority (should execute first)
        return type switch
        {
            RefactoringType.SimplifyMethod => 1,
            RefactoringType.ReplaceConditional => 2,
            RefactoringType.ExtractMethod => 3,
            RefactoringType.ExtractClass => 4,
            RefactoringType.SplitGodClass => 4,
            RefactoringType.ExtractInterface => 5,
            _ => 10
        };
    }

    private double CalculateEstimatedImpact(List<RefactoringType> strategies)
    {
        var baseImpact = strategies.Count * 15.0;

        // Bonus for well-composed chains
        var hasSimplify = strategies.Contains(RefactoringType.SimplifyMethod);
        var hasExtractMethod = strategies.Contains(RefactoringType.ExtractMethod);
        var hasExtractClass = strategies.Contains(RefactoringType.ExtractClass) ||
                              strategies.Contains(RefactoringType.SplitGodClass);
        var hasInterface = strategies.Contains(RefactoringType.ExtractInterface);

        if (hasSimplify && hasExtractMethod)
            baseImpact += 10;

        if (hasExtractMethod && hasExtractClass)
            baseImpact += 15;

        if (hasExtractClass && hasInterface)
            baseImpact += 10;

        return Math.Min(100, baseImpact);
    }

    private string GenerateDescription(List<RefactoringType> strategies)
    {
        var parts = strategies.Select(s => s switch
        {
            RefactoringType.SimplifyMethod => "simplify methods",
            RefactoringType.ExtractMethod => "extract methods",
            RefactoringType.ExtractClass => "extract class",
            RefactoringType.SplitGodClass => "split god class",
            RefactoringType.ExtractInterface => "extract interface",
            RefactoringType.ReplaceConditional => "replace conditionals",
            _ => s.ToString()
        });

        return string.Join(", then ", parts);
    }

    private IEnumerable<IEnumerable<T>> GetCombinations<T>(List<T> list, int length)
    {
        if (length == 1)
        {
            return list.Select(t => new[] { t });
        }

        return GetCombinations(list, length - 1)
            .SelectMany(t => list.Where(e => !t.Contains(e)),
                (t1, t2) => t1.Concat(new[] { t2 }));
    }
}
