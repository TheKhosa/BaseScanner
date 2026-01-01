using System.Collections.Immutable;

namespace BaseScanner.Context;

/// <summary>
/// Represents method call relationships for the entire codebase.
/// Provides efficient traversal and analysis algorithms.
/// </summary>
public class CallGraph
{
    // Adjacency lists for efficient traversal
    private readonly Dictionary<string, HashSet<string>> _callees = new();
    private readonly Dictionary<string, HashSet<string>> _callers = new();

    // Cached computations
    private ImmutableHashSet<string>? _entryPoints;
    private ImmutableHashSet<string>? _leaves;
    private ImmutableDictionary<string, int>? _depths;

    /// <summary>
    /// Add a method to the graph.
    /// </summary>
    public void AddMethod(string methodKey)
    {
        if (!_callees.ContainsKey(methodKey))
            _callees[methodKey] = new HashSet<string>();
        if (!_callers.ContainsKey(methodKey))
            _callers[methodKey] = new HashSet<string>();
        InvalidateCache();
    }

    /// <summary>
    /// Add a call edge from caller to callee.
    /// </summary>
    public void AddEdge(string caller, string callee)
    {
        if (!_callees.ContainsKey(caller))
            _callees[caller] = new HashSet<string>();
        if (!_callers.ContainsKey(callee))
            _callers[callee] = new HashSet<string>();

        _callees[caller].Add(callee);
        _callers[callee].Add(caller);
        InvalidateCache();
    }

    /// <summary>
    /// Get all methods called by the given method.
    /// </summary>
    public IEnumerable<string> GetCallees(string method) =>
        _callees.TryGetValue(method, out var callees) ? callees : Enumerable.Empty<string>();

    /// <summary>
    /// Get all methods that call the given method.
    /// </summary>
    public IEnumerable<string> GetCallers(string method) =>
        _callers.TryGetValue(method, out var callers) ? callers : Enumerable.Empty<string>();

    /// <summary>
    /// Get count of methods called by the given method.
    /// </summary>
    public int GetCalleeCount(string method) =>
        _callees.TryGetValue(method, out var callees) ? callees.Count : 0;

    /// <summary>
    /// Get count of methods that call the given method.
    /// </summary>
    public int GetCallerCount(string method) =>
        _callers.TryGetValue(method, out var callers) ? callers.Count : 0;

    /// <summary>
    /// Get all entry points (methods with no callers).
    /// </summary>
    public ImmutableHashSet<string> GetEntryPoints()
    {
        if (_entryPoints == null)
        {
            _entryPoints = _callees.Keys
                .Where(m => !_callers.TryGetValue(m, out var callers) || callers.Count == 0)
                .ToImmutableHashSet();
        }
        return _entryPoints;
    }

    /// <summary>
    /// Get all leaf methods (methods that don't call other methods).
    /// </summary>
    public ImmutableHashSet<string> GetLeaves()
    {
        if (_leaves == null)
        {
            _leaves = _callees.Keys
                .Where(m => !_callees.TryGetValue(m, out var callees) || callees.Count == 0)
                .ToImmutableHashSet();
        }
        return _leaves;
    }

    /// <summary>
    /// Check if a method is an entry point.
    /// </summary>
    public bool IsEntryPoint(string method) => GetEntryPoints().Contains(method);

    /// <summary>
    /// Check if a method is a leaf.
    /// </summary>
    public bool IsLeaf(string method) => GetLeaves().Contains(method);

    /// <summary>
    /// Get the depth of a method from entry points.
    /// </summary>
    public int GetDepthFromEntryPoints(string method)
    {
        if (_depths == null)
            ComputeDepths();
        return _depths!.TryGetValue(method, out var depth) ? depth : int.MaxValue;
    }

    /// <summary>
    /// Get all methods reachable from the given method (transitive callees).
    /// </summary>
    public ImmutableHashSet<string> GetTransitiveClosure(string method)
    {
        var visited = new HashSet<string>();
        var queue = new Queue<string>();
        queue.Enqueue(method);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current))
                continue;

            foreach (var callee in GetCallees(current))
            {
                if (!visited.Contains(callee))
                    queue.Enqueue(callee);
            }
        }

        visited.Remove(method);
        return visited.ToImmutableHashSet();
    }

    /// <summary>
    /// Get all callers transitively (who eventually calls this method).
    /// </summary>
    public ImmutableHashSet<string> GetTransitiveCallers(string method, int maxDepth = int.MaxValue)
    {
        var visited = new HashSet<string>();
        var queue = new Queue<(string Method, int Depth)>();
        queue.Enqueue((method, 0));

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            if (depth > maxDepth || !visited.Add(current))
                continue;

            foreach (var caller in GetCallers(current))
            {
                if (!visited.Contains(caller))
                    queue.Enqueue((caller, depth + 1));
            }
        }

        visited.Remove(method);
        return visited.ToImmutableHashSet();
    }

    /// <summary>
    /// Get all callers transitively for multiple methods.
    /// </summary>
    public ImmutableHashSet<string> GetTransitiveCallers(IEnumerable<string> methods, int maxDepth = int.MaxValue)
    {
        var result = new HashSet<string>();
        foreach (var method in methods)
        {
            result.UnionWith(GetTransitiveCallers(method, maxDepth));
        }
        return result.ToImmutableHashSet();
    }

    /// <summary>
    /// Find paths between two methods.
    /// </summary>
    public IEnumerable<ImmutableList<string>> FindPathsBetween(string source, string target, int maxDepth = 10)
    {
        var paths = new List<ImmutableList<string>>();
        var currentPath = new List<string> { source };
        var visited = new HashSet<string> { source };

        FindPathsDfs(source, target, maxDepth, currentPath, visited, paths);

        return paths;
    }

    private void FindPathsDfs(
        string current,
        string target,
        int remainingDepth,
        List<string> currentPath,
        HashSet<string> visited,
        List<ImmutableList<string>> paths)
    {
        if (remainingDepth <= 0)
            return;

        foreach (var callee in GetCallees(current))
        {
            if (callee == target)
            {
                var path = currentPath.ToList();
                path.Add(target);
                paths.Add(path.ToImmutableList());
                continue;
            }

            if (!visited.Contains(callee))
            {
                visited.Add(callee);
                currentPath.Add(callee);
                FindPathsDfs(callee, target, remainingDepth - 1, currentPath, visited, paths);
                currentPath.RemoveAt(currentPath.Count - 1);
                visited.Remove(callee);
            }
        }
    }

    /// <summary>
    /// Detect cycles in the call graph.
    /// </summary>
    public IEnumerable<ImmutableList<string>> DetectCycles()
    {
        var cycles = new List<ImmutableList<string>>();
        var visited = new HashSet<string>();
        var recursionStack = new HashSet<string>();
        var path = new List<string>();

        foreach (var method in _callees.Keys)
        {
            if (!visited.Contains(method))
            {
                DetectCyclesDfs(method, visited, recursionStack, path, cycles);
            }
        }

        return cycles;
    }

    private void DetectCyclesDfs(
        string current,
        HashSet<string> visited,
        HashSet<string> recursionStack,
        List<string> path,
        List<ImmutableList<string>> cycles)
    {
        visited.Add(current);
        recursionStack.Add(current);
        path.Add(current);

        foreach (var callee in GetCallees(current))
        {
            if (!visited.Contains(callee))
            {
                DetectCyclesDfs(callee, visited, recursionStack, path, cycles);
            }
            else if (recursionStack.Contains(callee))
            {
                // Found a cycle
                var cycleStart = path.IndexOf(callee);
                if (cycleStart >= 0)
                {
                    var cycle = path.Skip(cycleStart).ToList();
                    cycle.Add(callee);
                    cycles.Add(cycle.ToImmutableList());
                }
            }
        }

        path.RemoveAt(path.Count - 1);
        recursionStack.Remove(current);
    }

    /// <summary>
    /// Get all methods in the graph.
    /// </summary>
    public IEnumerable<string> GetAllMethods() => _callees.Keys;

    /// <summary>
    /// Get the total number of methods in the graph.
    /// </summary>
    public int MethodCount => _callees.Count;

    /// <summary>
    /// Get the total number of edges (calls) in the graph.
    /// </summary>
    public int EdgeCount => _callees.Values.Sum(c => c.Count);

    private void ComputeDepths()
    {
        var depths = new Dictionary<string, int>();
        var queue = new Queue<string>();

        // Initialize entry points with depth 0
        foreach (var entryPoint in GetEntryPoints())
        {
            depths[entryPoint] = 0;
            queue.Enqueue(entryPoint);
        }

        // BFS to compute depths
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var currentDepth = depths[current];

            foreach (var callee in GetCallees(current))
            {
                if (!depths.ContainsKey(callee) || depths[callee] > currentDepth + 1)
                {
                    depths[callee] = currentDepth + 1;
                    queue.Enqueue(callee);
                }
            }
        }

        _depths = depths.ToImmutableDictionary();
    }

    private void InvalidateCache()
    {
        _entryPoints = null;
        _leaves = null;
        _depths = null;
    }
}
