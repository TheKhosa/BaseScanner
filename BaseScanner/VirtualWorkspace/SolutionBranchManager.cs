using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace BaseScanner.VirtualWorkspace;

/// <summary>
/// Manages multiple solution branches for parallel transformation comparison.
/// Leverages Roslyn's immutable Solution pattern for efficient branching.
/// </summary>
public class SolutionBranchManager
{
    private Solution? _original;
    private readonly Dictionary<string, Solution> _branches = new();
    private readonly List<string> _branchOrder = new(); // Track creation order for cleanup
    private readonly object _lock = new();

    /// <summary>
    /// Set the original solution to branch from.
    /// </summary>
    public void SetOriginal(Solution solution)
    {
        lock (_lock)
        {
            _original = solution;
            _branches.Clear();
            _branchOrder.Clear();
        }
    }

    /// <summary>
    /// Get the original solution.
    /// </summary>
    public Solution GetOriginal()
    {
        lock (_lock)
        {
            return _original ?? throw new InvalidOperationException("No original solution set");
        }
    }

    /// <summary>
    /// Create a new branch from the original solution.
    /// </summary>
    public Solution CreateBranch(string branchName, string? fromBranch = null)
    {
        lock (_lock)
        {
            if (_original == null)
                throw new InvalidOperationException("No original solution set");

            Solution source;
            if (fromBranch != null)
            {
                if (!_branches.TryGetValue(fromBranch, out source!))
                    throw new InvalidOperationException($"Branch '{fromBranch}' not found");
            }
            else
            {
                source = _original;
            }

            // Roslyn Solutions are immutable - this is a reference copy
            _branches[branchName] = source;
            _branchOrder.Add(branchName);
            return source;
        }
    }

    /// <summary>
    /// Update a branch with a new solution state.
    /// </summary>
    public void UpdateBranch(string branchName, Solution solution)
    {
        lock (_lock)
        {
            if (!_branches.ContainsKey(branchName))
                throw new InvalidOperationException($"Branch '{branchName}' not found");

            _branches[branchName] = solution;
        }
    }

    /// <summary>
    /// Apply a syntax transformation to a branch.
    /// </summary>
    public async Task<Solution> ApplyTransformationAsync(
        string branchName,
        DocumentId documentId,
        Func<SyntaxNode, SyntaxNode> transformation)
    {
        Solution solution;
        lock (_lock)
        {
            if (!_branches.TryGetValue(branchName, out solution!))
                throw new InvalidOperationException($"Branch '{branchName}' not found");
        }

        var document = solution.GetDocument(documentId);
        if (document == null) return solution;

        var root = await document.GetSyntaxRootAsync();
        if (root == null) return solution;

        var newRoot = transformation(root);
        var newSolution = solution.WithDocumentSyntaxRoot(documentId, newRoot);

        lock (_lock)
        {
            _branches[branchName] = newSolution;
        }

        return newSolution;
    }

    /// <summary>
    /// Apply a rewriter-based transformation to a branch.
    /// </summary>
    public async Task<Solution> ApplyRewriterAsync(
        string branchName,
        DocumentId documentId,
        CSharpSyntaxRewriter rewriter)
    {
        return await ApplyTransformationAsync(branchName, documentId, root => rewriter.Visit(root));
    }

    /// <summary>
    /// Get a branch by name.
    /// </summary>
    public Solution? GetBranch(string branchName)
    {
        lock (_lock)
        {
            return _branches.TryGetValue(branchName, out var solution) ? solution : null;
        }
    }

    /// <summary>
    /// Delete a branch.
    /// </summary>
    public void DeleteBranch(string branchName)
    {
        lock (_lock)
        {
            _branches.Remove(branchName);
            _branchOrder.Remove(branchName);
        }
    }

    /// <summary>
    /// List all branches.
    /// </summary>
    public IReadOnlyList<string> ListBranches()
    {
        lock (_lock)
        {
            return _branches.Keys.ToList();
        }
    }

    /// <summary>
    /// Clean up old branches, keeping only the most recent.
    /// </summary>
    public void Cleanup(int keepCount = 5)
    {
        lock (_lock)
        {
            while (_branchOrder.Count > keepCount)
            {
                var oldest = _branchOrder[0];
                _branches.Remove(oldest);
                _branchOrder.RemoveAt(0);
            }
        }
    }

    /// <summary>
    /// Get branch count.
    /// </summary>
    public int BranchCount
    {
        get
        {
            lock (_lock)
            {
                return _branches.Count;
            }
        }
    }
}
