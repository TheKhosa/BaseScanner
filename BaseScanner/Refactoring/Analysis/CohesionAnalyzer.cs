using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using BaseScanner.Refactoring.Models;

namespace BaseScanner.Refactoring.Analysis;

/// <summary>
/// Analyzes class cohesion using LCOM4 and identifies method clusters for refactoring.
/// </summary>
public class CohesionAnalyzer
{
    /// <summary>
    /// Calculate LCOM4 (Lack of Cohesion of Methods) for a class.
    /// LCOM4 counts the number of connected components in the method-field graph.
    /// Lower is better (1 = perfectly cohesive).
    /// </summary>
    public async Task<double> CalculateLCOM4Async(Document document, string? className = null)
    {
        var root = await document.GetSyntaxRootAsync();
        var model = await document.GetSemanticModelAsync();

        if (root == null || model == null)
            return 0;

        var classDecl = FindClass(root, className);
        if (classDecl == null)
            return 0;

        return CalculateLCOM4(classDecl, model);
    }

    /// <summary>
    /// Calculate LCOM4 for a class declaration.
    /// </summary>
    public double CalculateLCOM4(ClassDeclarationSyntax classDecl, SemanticModel model)
    {
        var graph = BuildMethodFieldGraph(classDecl, model);
        return CountConnectedComponents(graph);
    }

    /// <summary>
    /// Find cohesive clusters of methods that belong together.
    /// </summary>
    public async Task<List<CohesiveCluster>> FindCohesiveClustersAsync(Document document, string? className = null)
    {
        var root = await document.GetSyntaxRootAsync();
        var model = await document.GetSemanticModelAsync();

        if (root == null || model == null)
            return [];

        var classDecl = FindClass(root, className);
        if (classDecl == null)
            return [];

        return FindCohesiveClusters(classDecl, model);
    }

    /// <summary>
    /// Find cohesive clusters within a class.
    /// </summary>
    public List<CohesiveCluster> FindCohesiveClusters(ClassDeclarationSyntax classDecl, SemanticModel model)
    {
        var graph = BuildMethodFieldGraph(classDecl, model);
        var components = FindConnectedComponents(graph);

        var clusters = new List<CohesiveCluster>();
        var className = classDecl.Identifier.Text;

        foreach (var component in components)
        {
            var methods = component.Where(n => graph.NodeTypes[n] == NodeType.Method).ToList();
            var fields = component.Where(n => graph.NodeTypes[n] == NodeType.Field).ToList();

            // Only consider clusters with multiple methods
            if (methods.Count < 2)
                continue;

            // Calculate cohesion within this cluster
            var internalEdges = CountInternalEdges(component, graph);
            var maxEdges = methods.Count * fields.Count;
            var cohesion = maxEdges > 0 ? (double)internalEdges / maxEdges : 0;

            // Calculate total complexity of methods in cluster
            var totalComplexity = CalculateClusterComplexity(classDecl, methods);

            // Generate suggested class name based on shared fields
            var suggestedName = GenerateClusterName(className, fields, methods);
            var responsibility = InferResponsibility(methods, fields);

            clusters.Add(new CohesiveCluster
            {
                SuggestedClassName = suggestedName,
                MethodNames = methods,
                SharedFields = fields,
                CohesionScore = cohesion,
                TotalComplexity = totalComplexity,
                SuggestedResponsibility = responsibility
            });
        }

        // Sort by cohesion score (higher is better for extraction)
        return clusters
            .Where(c => c.MethodNames.Count >= 3) // Minimum 3 methods for extraction
            .OrderByDescending(c => c.CohesionScore)
            .ThenByDescending(c => c.MethodNames.Count)
            .ToList();
    }

    /// <summary>
    /// Identify distinct responsibility boundaries within a class.
    /// </summary>
    public async Task<List<ResponsibilityBoundary>> IdentifyResponsibilitiesAsync(
        Document document,
        string? className = null)
    {
        var root = await document.GetSyntaxRootAsync();
        var model = await document.GetSemanticModelAsync();

        if (root == null || model == null)
            return [];

        var classDecl = FindClass(root, className);
        if (classDecl == null)
            return [];

        return IdentifyResponsibilities(classDecl, model);
    }

    /// <summary>
    /// Identify responsibilities using semantic clustering.
    /// </summary>
    public List<ResponsibilityBoundary> IdentifyResponsibilities(
        ClassDeclarationSyntax classDecl,
        SemanticModel model)
    {
        var clusters = FindCohesiveClusters(classDecl, model);
        var boundaries = new List<ResponsibilityBoundary>();

        foreach (var cluster in clusters)
        {
            // Find properties that use the cluster's fields
            var properties = FindPropertiesUsingFields(classDecl, cluster.SharedFields, model);

            // Find dependencies (other classes used by this cluster)
            var dependencies = FindClusterDependencies(classDecl, cluster.MethodNames, model);

            boundaries.Add(new ResponsibilityBoundary
            {
                ResponsibilityName = cluster.SuggestedResponsibility,
                Methods = cluster.MethodNames,
                Fields = cluster.SharedFields,
                Properties = properties,
                Cohesion = cluster.CohesionScore,
                Dependencies = dependencies
            });
        }

        return boundaries;
    }

    /// <summary>
    /// Detect cohesion-related code smells.
    /// </summary>
    public async Task<List<CodeSmell>> DetectCohesionSmellsAsync(Document document)
    {
        var smells = new List<CodeSmell>();
        var root = await document.GetSyntaxRootAsync();
        var model = await document.GetSemanticModelAsync();

        if (root == null || model == null)
            return smells;

        foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var lcom4 = CalculateLCOM4(classDecl, model);
            var lineSpan = classDecl.Identifier.GetLocation().GetLineSpan();

            // LCOM4 > 1 means the class has multiple disconnected components
            if (lcom4 > 1)
            {
                var severity = lcom4 switch
                {
                    > 5 => SmellSeverity.Critical,
                    > 3 => SmellSeverity.High,
                    > 2 => SmellSeverity.Medium,
                    _ => SmellSeverity.Low
                };

                smells.Add(new CodeSmell
                {
                    SmellType = CodeSmellType.GodClass,
                    Severity = severity,
                    FilePath = document.FilePath ?? "",
                    StartLine = lineSpan.StartLinePosition.Line + 1,
                    EndLine = lineSpan.EndLinePosition.Line + 1,
                    TargetName = classDecl.Identifier.Text,
                    Description = $"Class has LCOM4 of {lcom4:F1}, indicating {(int)lcom4} distinct responsibilities.",
                    Metrics = new Dictionary<string, object>
                    {
                        ["lcom4"] = lcom4,
                        ["components"] = (int)lcom4
                    }
                });
            }

            // Check for classes with too many disconnected method groups
            var clusters = FindCohesiveClusters(classDecl, model);
            if (clusters.Count >= 3)
            {
                smells.Add(new CodeSmell
                {
                    SmellType = CodeSmellType.GodClass,
                    Severity = SmellSeverity.High,
                    FilePath = document.FilePath ?? "",
                    StartLine = lineSpan.StartLinePosition.Line + 1,
                    EndLine = lineSpan.EndLinePosition.Line + 1,
                    TargetName = classDecl.Identifier.Text,
                    Description = $"Class contains {clusters.Count} distinct method clusters that could be extracted.",
                    Metrics = new Dictionary<string, object>
                    {
                        ["clusterCount"] = clusters.Count,
                        ["clusters"] = clusters.Select(c => c.SuggestedClassName).ToList()
                    }
                });
            }
        }

        return smells;
    }

    #region Graph Building

    private MethodFieldGraph BuildMethodFieldGraph(ClassDeclarationSyntax classDecl, SemanticModel model)
    {
        var graph = new MethodFieldGraph();

        // Get all fields
        var fields = classDecl.Members
            .OfType<FieldDeclarationSyntax>()
            .SelectMany(f => f.Declaration.Variables)
            .Select(v => v.Identifier.Text)
            .ToList();

        // Get all properties with backing fields
        var properties = classDecl.Members
            .OfType<PropertyDeclarationSyntax>()
            .Where(p => p.AccessorList?.Accessors.Any(a => a.Body != null || a.ExpressionBody != null) == true)
            .Select(p => p.Identifier.Text)
            .ToList();

        // Add field nodes
        foreach (var field in fields)
        {
            graph.AddNode(field, NodeType.Field);
        }

        // Add property nodes (treat as fields for cohesion analysis)
        foreach (var prop in properties)
        {
            graph.AddNode(prop, NodeType.Field);
        }

        // Get all methods
        var methods = classDecl.Members
            .OfType<MethodDeclarationSyntax>()
            .Where(m => !m.Modifiers.Any(SyntaxKind.StaticKeyword))
            .ToList();

        foreach (var method in methods)
        {
            var methodName = method.Identifier.Text;
            graph.AddNode(methodName, NodeType.Method);

            // Find fields accessed by this method
            var accessedFields = FindAccessedFields(method, fields.Concat(properties).ToList(), model);
            foreach (var field in accessedFields)
            {
                graph.AddEdge(methodName, field);
            }

            // Find other methods called by this method
            var calledMethods = FindCalledMethods(method, methods.Select(m => m.Identifier.Text).ToList(), model);
            foreach (var calledMethod in calledMethods)
            {
                if (calledMethod != methodName)
                {
                    graph.AddEdge(methodName, calledMethod);
                }
            }
        }

        return graph;
    }

    private HashSet<string> FindAccessedFields(
        MethodDeclarationSyntax method,
        List<string> fieldNames,
        SemanticModel model)
    {
        var accessed = new HashSet<string>();

        foreach (var identifier in method.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            var name = identifier.Identifier.Text;
            if (fieldNames.Contains(name))
            {
                // Verify it's actually the field and not a local variable
                var symbol = model.GetSymbolInfo(identifier).Symbol;
                if (symbol is IFieldSymbol || symbol is IPropertySymbol)
                {
                    accessed.Add(name);
                }
            }
        }

        return accessed;
    }

    private HashSet<string> FindCalledMethods(
        MethodDeclarationSyntax method,
        List<string> methodNames,
        SemanticModel model)
    {
        var called = new HashSet<string>();

        foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is IdentifierNameSyntax identifier)
            {
                var name = identifier.Identifier.Text;
                if (methodNames.Contains(name))
                {
                    called.Add(name);
                }
            }
            else if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                if (memberAccess.Expression is ThisExpressionSyntax)
                {
                    var name = memberAccess.Name.Identifier.Text;
                    if (methodNames.Contains(name))
                    {
                        called.Add(name);
                    }
                }
            }
        }

        return called;
    }

    #endregion

    #region Connected Components

    private int CountConnectedComponents(MethodFieldGraph graph)
    {
        var components = FindConnectedComponents(graph);
        return components.Count;
    }

    private List<HashSet<string>> FindConnectedComponents(MethodFieldGraph graph)
    {
        var visited = new HashSet<string>();
        var components = new List<HashSet<string>>();

        foreach (var node in graph.Nodes)
        {
            if (!visited.Contains(node))
            {
                var component = new HashSet<string>();
                DFS(node, graph, visited, component);
                if (component.Count > 0)
                {
                    components.Add(component);
                }
            }
        }

        return components;
    }

    private void DFS(string node, MethodFieldGraph graph, HashSet<string> visited, HashSet<string> component)
    {
        if (visited.Contains(node))
            return;

        visited.Add(node);
        component.Add(node);

        if (graph.Adjacency.TryGetValue(node, out var neighbors))
        {
            foreach (var neighbor in neighbors)
            {
                DFS(neighbor, graph, visited, component);
            }
        }
    }

    private int CountInternalEdges(HashSet<string> component, MethodFieldGraph graph)
    {
        var count = 0;
        foreach (var node in component)
        {
            if (graph.Adjacency.TryGetValue(node, out var neighbors))
            {
                count += neighbors.Count(n => component.Contains(n));
            }
        }
        return count / 2; // Each edge counted twice
    }

    #endregion

    #region Helpers

    private ClassDeclarationSyntax? FindClass(SyntaxNode root, string? className)
    {
        if (className == null)
        {
            // Return the first/main class
            return root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        }

        return root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.Text == className);
    }

    private int CalculateClusterComplexity(ClassDeclarationSyntax classDecl, List<string> methodNames)
    {
        var complexity = 0;
        foreach (var method in classDecl.Members.OfType<MethodDeclarationSyntax>())
        {
            if (methodNames.Contains(method.Identifier.Text))
            {
                complexity += VirtualWorkspace.TransformationScorer.CalculateCyclomaticComplexity(method);
            }
        }
        return complexity;
    }

    private string GenerateClusterName(string className, List<string> fields, List<string> methods)
    {
        // Try to find a common prefix in field names
        if (fields.Count > 0)
        {
            var commonPrefix = FindCommonPrefix(fields);
            if (!string.IsNullOrEmpty(commonPrefix) && commonPrefix.Length >= 3)
            {
                return ToPascalCase(commonPrefix) + "Handler";
            }
        }

        // Try to find a common prefix in method names
        if (methods.Count > 0)
        {
            var commonPrefix = FindCommonPrefix(methods);
            if (!string.IsNullOrEmpty(commonPrefix) && commonPrefix.Length >= 3)
            {
                return ToPascalCase(commonPrefix) + "Service";
            }
        }

        // Fall back to generic name
        return className + "Component";
    }

    private string InferResponsibility(List<string> methods, List<string> fields)
    {
        // Look for common naming patterns
        var allNames = methods.Concat(fields).ToList();

        var patterns = new Dictionary<string, string>
        {
            { "save|persist|store|write", "Persistence" },
            { "load|read|fetch|get", "Data Access" },
            { "valid|check|verify", "Validation" },
            { "format|render|display|show", "Presentation" },
            { "calculate|compute|process", "Computation" },
            { "send|receive|notify|publish", "Communication" },
            { "log|audit|trace", "Logging" },
            { "cache|buffer|store", "Caching" },
            { "auth|login|permission|access", "Authentication" },
            { "config|setting|option", "Configuration" }
        };

        foreach (var pattern in patterns)
        {
            var regex = new System.Text.RegularExpressions.Regex(pattern.Key, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (allNames.Any(n => regex.IsMatch(n)))
            {
                return pattern.Value;
            }
        }

        return "Core Logic";
    }

    private string FindCommonPrefix(List<string> names)
    {
        if (names.Count == 0)
            return "";

        var first = names[0];
        var prefixLen = 0;

        for (var i = 0; i < first.Length; i++)
        {
            var c = first[i];
            if (names.All(n => n.Length > i && n[i] == c))
            {
                prefixLen = i + 1;
            }
            else
            {
                break;
            }
        }

        // Don't return very short prefixes
        return prefixLen >= 3 ? first.Substring(0, prefixLen) : "";
    }

    private string ToPascalCase(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? "";

        // Remove underscores and capitalize, filtering out empty segments
        var words = text.Split('_', '-')
            .Where(w => !string.IsNullOrEmpty(w))
            .Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1).ToLowerInvariant());

        return string.Join("", words);
    }

    private List<string> FindPropertiesUsingFields(
        ClassDeclarationSyntax classDecl,
        List<string> fields,
        SemanticModel model)
    {
        var properties = new List<string>();

        foreach (var prop in classDecl.Members.OfType<PropertyDeclarationSyntax>())
        {
            var accessors = prop.AccessorList?.Accessors ?? [];
            foreach (var accessor in accessors)
            {
                var body = accessor.Body ?? (SyntaxNode?)accessor.ExpressionBody;
                if (body == null)
                    continue;

                var usesField = body.DescendantNodes()
                    .OfType<IdentifierNameSyntax>()
                    .Any(id => fields.Contains(id.Identifier.Text));

                if (usesField)
                {
                    properties.Add(prop.Identifier.Text);
                    break;
                }
            }
        }

        return properties;
    }

    private List<string> FindClusterDependencies(
        ClassDeclarationSyntax classDecl,
        List<string> methodNames,
        SemanticModel model)
    {
        var dependencies = new HashSet<string>();

        foreach (var method in classDecl.Members.OfType<MethodDeclarationSyntax>())
        {
            if (!methodNames.Contains(method.Identifier.Text))
                continue;

            foreach (var identifier in method.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                var symbol = model.GetSymbolInfo(identifier).Symbol;
                if (symbol is INamedTypeSymbol typeSymbol &&
                    typeSymbol.TypeKind == TypeKind.Class &&
                    typeSymbol.Name != classDecl.Identifier.Text)
                {
                    dependencies.Add(typeSymbol.Name);
                }
            }

            foreach (var objectCreation in method.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                var typeInfo = model.GetTypeInfo(objectCreation);
                if (typeInfo.Type is INamedTypeSymbol createdType &&
                    createdType.TypeKind == TypeKind.Class &&
                    createdType.Name != classDecl.Identifier.Text)
                {
                    dependencies.Add(createdType.Name);
                }
            }
        }

        return dependencies.ToList();
    }

    #endregion
}

/// <summary>
/// Graph representing method-field relationships.
/// </summary>
internal class MethodFieldGraph
{
    public HashSet<string> Nodes { get; } = [];
    public Dictionary<string, NodeType> NodeTypes { get; } = [];
    public Dictionary<string, HashSet<string>> Adjacency { get; } = [];

    public void AddNode(string name, NodeType type)
    {
        Nodes.Add(name);
        NodeTypes[name] = type;
        if (!Adjacency.ContainsKey(name))
        {
            Adjacency[name] = [];
        }
    }

    public void AddEdge(string from, string to)
    {
        if (!Adjacency.ContainsKey(from))
            Adjacency[from] = [];
        if (!Adjacency.ContainsKey(to))
            Adjacency[to] = [];

        Adjacency[from].Add(to);
        Adjacency[to].Add(from); // Undirected graph
    }
}

internal enum NodeType
{
    Method,
    Field
}
