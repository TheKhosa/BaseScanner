using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BaseScanner.Analyzers;

public class DependencyAnalyzer
{
    public record TypeDependency
    {
        public required string FromType { get; init; }
        public required string FromNamespace { get; init; }
        public required string ToType { get; init; }
        public required string ToNamespace { get; init; }
        public required string FilePath { get; init; }
    }

    public record CouplingMetric
    {
        public required string TypeName { get; init; }
        public required string Namespace { get; init; }
        public required string FilePath { get; init; }
        public required int Line { get; init; }
        public required int AfferentCoupling { get; init; }  // Types that depend on this
        public required int EfferentCoupling { get; init; }  // Types this depends on
        public required double Instability { get; init; }    // Ce / (Ca + Ce)
    }

    public record CircularDependency
    {
        public required List<string> Cycle { get; init; }
        public required string Type { get; init; } // "Namespace" or "Type"
    }

    public record Issue
    {
        public required string Type { get; init; }
        public required string Severity { get; init; }
        public required string Message { get; init; }
        public required string FilePath { get; init; }
        public required int Line { get; init; }
        public required string CodeSnippet { get; init; }
    }

    public async Task<(List<Issue> Issues, List<CouplingMetric> Metrics, List<CircularDependency> Cycles)> AnalyzeAsync(Project project)
    {
        var issues = new List<Issue>();
        var dependencies = new List<TypeDependency>();
        var typeLocations = new Dictionary<string, (string FilePath, int Line)>();

        foreach (var document in project.Documents)
        {
            if (document.FilePath == null) continue;
            if (document.FilePath.Contains(".Designer.cs")) continue;

            var syntaxRoot = await document.GetSyntaxRootAsync();
            var semanticModel = await document.GetSemanticModelAsync();
            if (syntaxRoot == null || semanticModel == null) continue;

            // Collect type declarations and their dependencies
            var types = syntaxRoot.DescendantNodes().OfType<TypeDeclarationSyntax>();

            foreach (var typeDecl in types)
            {
                var symbol = semanticModel.GetDeclaredSymbol(typeDecl);
                if (symbol == null) continue;

                var fullName = symbol.ToDisplayString();
                var ns = symbol.ContainingNamespace?.ToDisplayString() ?? "";

                typeLocations[fullName] = (document.FilePath, typeDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1);

                // Find dependencies
                var deps = ExtractDependencies(typeDecl, semanticModel, fullName, ns, document.FilePath);
                dependencies.AddRange(deps);
            }
        }

        // Calculate coupling metrics
        var metrics = CalculateCouplingMetrics(dependencies, typeLocations);

        // Find high coupling
        foreach (var metric in metrics.Where(m => m.EfferentCoupling > 15))
        {
            issues.Add(new Issue
            {
                Type = "HighCoupling",
                Severity = "Warning",
                Message = $"Type '{metric.TypeName}' has high efferent coupling ({metric.EfferentCoupling} dependencies)",
                FilePath = metric.FilePath,
                Line = metric.Line,
                CodeSnippet = $"Ce={metric.EfferentCoupling}, Ca={metric.AfferentCoupling}, I={metric.Instability:F2}"
            });
        }

        // Find circular dependencies
        var cycles = FindCircularDependencies(dependencies);

        foreach (var cycle in cycles.Where(c => c.Type == "Namespace").Take(10))
        {
            issues.Add(new Issue
            {
                Type = "CircularDependency",
                Severity = "Critical",
                Message = $"Circular namespace dependency: {string.Join(" -> ", cycle.Cycle)}",
                FilePath = "",
                Line = 0,
                CodeSnippet = string.Join(" -> ", cycle.Cycle)
            });
        }

        foreach (var cycle in cycles.Where(c => c.Type == "Type").Take(10))
        {
            var firstType = cycle.Cycle.First();
            var location = typeLocations.GetValueOrDefault(firstType);

            issues.Add(new Issue
            {
                Type = "CircularDependency",
                Severity = "Warning",
                Message = $"Circular type dependency detected",
                FilePath = location.FilePath ?? "",
                Line = location.Line,
                CodeSnippet = string.Join(" -> ", cycle.Cycle.Select(t => t.Split('.').Last()))
            });
        }

        return (issues, metrics, cycles);
    }

    private IEnumerable<TypeDependency> ExtractDependencies(TypeDeclarationSyntax typeDecl, SemanticModel semanticModel, string fromType, string fromNamespace, string filePath)
    {
        var dependencies = new HashSet<(string ToType, string ToNamespace)>();

        // Base types
        if (typeDecl.BaseList != null)
        {
            foreach (var baseType in typeDecl.BaseList.Types)
            {
                var typeInfo = semanticModel.GetTypeInfo(baseType.Type);
                if (typeInfo.Type is INamedTypeSymbol namedType && !namedType.IsImplicitlyDeclared)
                {
                    var toType = namedType.ToDisplayString();
                    var toNs = namedType.ContainingNamespace?.ToDisplayString() ?? "";
                    if (!string.IsNullOrEmpty(toNs) && !toNs.StartsWith("System"))
                    {
                        dependencies.Add((toType, toNs));
                    }
                }
            }
        }

        // Field types
        foreach (var field in typeDecl.Members.OfType<FieldDeclarationSyntax>())
        {
            var typeInfo = semanticModel.GetTypeInfo(field.Declaration.Type);
            AddTypeDependency(typeInfo.Type, dependencies);
        }

        // Property types
        foreach (var prop in typeDecl.Members.OfType<PropertyDeclarationSyntax>())
        {
            var typeInfo = semanticModel.GetTypeInfo(prop.Type);
            AddTypeDependency(typeInfo.Type, dependencies);
        }

        // Method parameters and return types
        foreach (var method in typeDecl.Members.OfType<MethodDeclarationSyntax>())
        {
            var returnTypeInfo = semanticModel.GetTypeInfo(method.ReturnType);
            AddTypeDependency(returnTypeInfo.Type, dependencies);

            foreach (var param in method.ParameterList.Parameters)
            {
                if (param.Type != null)
                {
                    var paramTypeInfo = semanticModel.GetTypeInfo(param.Type);
                    AddTypeDependency(paramTypeInfo.Type, dependencies);
                }
            }
        }

        // Object creations in the type
        foreach (var creation in typeDecl.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var typeInfo = semanticModel.GetTypeInfo(creation);
            AddTypeDependency(typeInfo.Type, dependencies);
        }

        return dependencies.Select(d => new TypeDependency
        {
            FromType = fromType,
            FromNamespace = fromNamespace,
            ToType = d.ToType,
            ToNamespace = d.ToNamespace,
            FilePath = filePath
        });
    }

    private void AddTypeDependency(ITypeSymbol? type, HashSet<(string ToType, string ToNamespace)> dependencies)
    {
        if (type == null) return;

        // Unwrap generic types
        if (type is INamedTypeSymbol namedType)
        {
            if (namedType.IsGenericType)
            {
                foreach (var typeArg in namedType.TypeArguments)
                {
                    AddTypeDependency(typeArg, dependencies);
                }
            }

            var ns = namedType.ContainingNamespace?.ToDisplayString() ?? "";
            if (!string.IsNullOrEmpty(ns) && !ns.StartsWith("System") && !namedType.IsImplicitlyDeclared)
            {
                dependencies.Add((namedType.ToDisplayString(), ns));
            }
        }

        // Handle arrays
        if (type is IArrayTypeSymbol arrayType)
        {
            AddTypeDependency(arrayType.ElementType, dependencies);
        }
    }

    private List<CouplingMetric> CalculateCouplingMetrics(List<TypeDependency> dependencies, Dictionary<string, (string FilePath, int Line)> typeLocations)
    {
        var efferent = dependencies.GroupBy(d => d.FromType)
            .ToDictionary(g => g.Key, g => g.Select(d => d.ToType).Distinct().Count());

        var afferent = dependencies.GroupBy(d => d.ToType)
            .ToDictionary(g => g.Key, g => g.Select(d => d.FromType).Distinct().Count());

        var allTypes = efferent.Keys.Union(afferent.Keys).Distinct();

        return allTypes.Select(type =>
        {
            var ce = efferent.GetValueOrDefault(type, 0);
            var ca = afferent.GetValueOrDefault(type, 0);
            var instability = (ce + ca) > 0 ? (double)ce / (ce + ca) : 0;
            var location = typeLocations.GetValueOrDefault(type);
            var ns = type.Contains('.') ? string.Join(".", type.Split('.').SkipLast(1)) : "";

            return new CouplingMetric
            {
                TypeName = type.Split('.').Last(),
                Namespace = ns,
                FilePath = location.FilePath ?? "",
                Line = location.Line,
                AfferentCoupling = ca,
                EfferentCoupling = ce,
                Instability = instability
            };
        }).ToList();
    }

    private List<CircularDependency> FindCircularDependencies(List<TypeDependency> dependencies)
    {
        var cycles = new List<CircularDependency>();

        // Find namespace-level cycles
        var nsGraph = dependencies
            .Where(d => d.FromNamespace != d.ToNamespace)
            .GroupBy(d => d.FromNamespace)
            .ToDictionary(g => g.Key, g => g.Select(d => d.ToNamespace).Distinct().ToList());

        cycles.AddRange(FindCyclesInGraph(nsGraph).Select(c => new CircularDependency
        {
            Cycle = c,
            Type = "Namespace"
        }));

        // Find type-level cycles
        var typeGraph = dependencies
            .Where(d => d.FromType != d.ToType)
            .GroupBy(d => d.FromType)
            .ToDictionary(g => g.Key, g => g.Select(d => d.ToType).Distinct().ToList());

        cycles.AddRange(FindCyclesInGraph(typeGraph).Take(20).Select(c => new CircularDependency
        {
            Cycle = c,
            Type = "Type"
        }));

        return cycles;
    }

    private List<List<string>> FindCyclesInGraph(Dictionary<string, List<string>> graph)
    {
        var cycles = new List<List<string>>();
        var visited = new HashSet<string>();
        var recursionStack = new HashSet<string>();
        var path = new List<string>();

        void DFS(string node)
        {
            if (recursionStack.Contains(node))
            {
                // Found a cycle
                var cycleStart = path.IndexOf(node);
                if (cycleStart >= 0)
                {
                    var cycle = path.Skip(cycleStart).Append(node).ToList();
                    if (cycle.Count >= 2 && cycle.Count <= 10)
                    {
                        cycles.Add(cycle);
                    }
                }
                return;
            }

            if (visited.Contains(node)) return;

            visited.Add(node);
            recursionStack.Add(node);
            path.Add(node);

            if (graph.TryGetValue(node, out var neighbors))
            {
                foreach (var neighbor in neighbors.Take(20)) // Limit to prevent explosion
                {
                    DFS(neighbor);
                    if (cycles.Count > 50) break; // Limit cycles found
                }
            }

            path.RemoveAt(path.Count - 1);
            recursionStack.Remove(node);
        }

        foreach (var node in graph.Keys.Take(100)) // Limit starting nodes
        {
            if (!visited.Contains(node))
            {
                DFS(node);
            }
            if (cycles.Count > 50) break;
        }

        return cycles.DistinctBy(c => string.Join("->", c.Order())).ToList();
    }
}
