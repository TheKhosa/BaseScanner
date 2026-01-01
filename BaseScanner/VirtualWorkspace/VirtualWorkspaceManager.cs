using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace BaseScanner.VirtualWorkspace;

/// <summary>
/// Manages in-memory code transformations without filesystem persistence.
/// Enables comparison of multiple transformation approaches before applying.
/// </summary>
public class VirtualWorkspaceManager : IDisposable
{
    private readonly AdhocWorkspace _workspace;
    private readonly SolutionBranchManager _branchManager;
    private readonly TransformationScorer _scorer;
    private readonly DiffEngine _diffEngine;
    private bool _disposed;

    public VirtualWorkspaceManager()
    {
        _workspace = new AdhocWorkspace();
        _branchManager = new SolutionBranchManager();
        _scorer = new TransformationScorer();
        _diffEngine = new DiffEngine();
    }

    /// <summary>
    /// Load a solution into the virtual workspace from an existing Roslyn project.
    /// </summary>
    public void LoadFromProject(Project project)
    {
        _branchManager.SetOriginal(project.Solution);
    }

    /// <summary>
    /// Create an in-memory project from source files.
    /// </summary>
    public Project CreateProject(string projectName, IEnumerable<(string path, string content)> files)
    {
        var projectId = ProjectId.CreateNewId();
        var solution = _workspace.CurrentSolution
            .AddProject(projectId, projectName, projectName, LanguageNames.CSharp);

        // Add references
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
        };

        solution = solution.AddMetadataReferences(projectId, references);

        // Add documents
        foreach (var (path, content) in files)
        {
            var documentId = DocumentId.CreateNewId(projectId);
            solution = solution.AddDocument(documentId, Path.GetFileName(path),
                SourceText.From(content), filePath: path);
        }

        _branchManager.SetOriginal(solution);
        return solution.GetProject(projectId)!;
    }

    /// <summary>
    /// Apply multiple transformation strategies and compare results.
    /// </summary>
    public async Task<TransformationComparison> CompareTransformationsAsync(
        DocumentId documentId,
        IEnumerable<ITransformationStrategy> strategies)
    {
        var results = new List<TransformationBranchResult>();
        var original = _branchManager.GetOriginal();
        var originalDoc = original.GetDocument(documentId);

        if (originalDoc == null)
            throw new ArgumentException("Document not found in solution", nameof(documentId));

        foreach (var strategy in strategies)
        {
            try
            {
                // Check if strategy can apply
                if (!await strategy.CanApplyAsync(originalDoc))
                    continue;

                // Create branch and apply transformation
                var branchName = $"{strategy.Name}_{Guid.NewGuid():N}";
                var branch = _branchManager.CreateBranch(branchName);
                var transformed = await strategy.ApplyAsync(branch, documentId);
                _branchManager.UpdateBranch(branchName, transformed);

                // Get transformed document
                var transformedDoc = transformed.GetDocument(documentId);
                if (transformedDoc == null) continue;

                // Score the transformation
                var score = await _scorer.ScoreAsync(originalDoc, transformedDoc);

                // Generate diff
                var diff = await _diffEngine.GenerateDiffAsync(originalDoc, transformedDoc);

                results.Add(new TransformationBranchResult
                {
                    StrategyName = strategy.Name,
                    Category = strategy.Category,
                    Description = strategy.Description,
                    BranchName = branchName,
                    Score = score,
                    Diff = diff,
                    TransformedSolution = transformed
                });
            }
            catch (Exception ex)
            {
                // Log but continue with other strategies
                results.Add(new TransformationBranchResult
                {
                    StrategyName = strategy.Name,
                    Category = strategy.Category,
                    Description = strategy.Description,
                    BranchName = $"failed_{strategy.Name}",
                    Score = new TransformationScore { OverallScore = -100, CompilationValid = false },
                    Diff = new DocumentDiff(),
                    Error = ex.Message
                });
            }
        }

        // Rank by score
        var rankedResults = results
            .Where(r => r.Error == null)
            .OrderByDescending(r => r.Score.OverallScore)
            .ToList();

        return new TransformationComparison
        {
            Original = original,
            OriginalDocument = originalDoc,
            Results = rankedResults,
            FailedResults = results.Where(r => r.Error != null).ToList(),
            BestResult = rankedResults.FirstOrDefault()
        };
    }

    /// <summary>
    /// Compare multiple transformations on a single code snippet.
    /// </summary>
    public async Task<TransformationComparison> CompareTransformationsOnCodeAsync(
        string code,
        IEnumerable<ITransformationStrategy> strategies)
    {
        var project = CreateProject("VirtualProject", new[] { ("Virtual.cs", code) });
        var document = project.Documents.First();
        return await CompareTransformationsAsync(document.Id, strategies);
    }

    /// <summary>
    /// Get the best transformation result for a document.
    /// </summary>
    public async Task<TransformationBranchResult?> GetBestTransformationAsync(
        DocumentId documentId,
        IEnumerable<ITransformationStrategy> strategies,
        double minimumScore = 0)
    {
        var comparison = await CompareTransformationsAsync(documentId, strategies);
        return comparison.BestResult?.Score.OverallScore >= minimumScore
            ? comparison.BestResult
            : null;
    }

    /// <summary>
    /// Get all branches for analysis.
    /// </summary>
    public IReadOnlyList<string> GetBranches() => _branchManager.ListBranches();

    /// <summary>
    /// Get a specific branch.
    /// </summary>
    public Solution? GetBranch(string name) => _branchManager.GetBranch(name);

    /// <summary>
    /// Clean up old branches.
    /// </summary>
    public void CleanupBranches(int keepCount = 5)
    {
        _branchManager.Cleanup(keepCount);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _workspace.Dispose();
        _disposed = true;
    }
}
