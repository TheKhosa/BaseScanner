using Microsoft.CodeAnalysis;
using BaseScanner.VirtualWorkspace;
using BaseScanner.Refactoring.Models;
using BaseScanner.Refactoring.Strategies;
using BaseScanner.Refactoring.Scoring;
using BaseScanner.Refactoring.Analysis;
using BaseScanner.Refactoring.Composition;
using BaseScanner.Services;
using BaseScanner.Analyzers.Quality;

namespace BaseScanner.Refactoring;

/// <summary>
/// Central orchestrator for refactoring operations.
/// Coordinates analysis, strategy comparison, and safe application of refactorings.
/// </summary>
public class RefactoringOrchestrator
{
    private readonly VirtualWorkspaceManager _workspaceManager;
    private readonly CohesionAnalyzer _cohesionAnalyzer;
    private readonly RefactoringScorer _scorer;
    private readonly TransformationScorer _baseScorer;
    private readonly DiffEngine _diffEngine;
    private readonly StrategyComposer _composer;
    private readonly BackupService _backupService;
    private readonly List<IRefactoringStrategy> _strategies;

    public RefactoringOrchestrator(
        VirtualWorkspaceManager? workspaceManager = null,
        BackupService? backupService = null)
    {
        _workspaceManager = workspaceManager ?? new VirtualWorkspaceManager();
        _cohesionAnalyzer = new CohesionAnalyzer();
        _scorer = new RefactoringScorer();
        _baseScorer = new TransformationScorer();
        _diffEngine = new DiffEngine();
        _composer = new StrategyComposer();
        _backupService = backupService ?? new BackupService();
        _strategies = InitializeStrategies();
    }

    private List<IRefactoringStrategy> InitializeStrategies()
    {
        return new List<IRefactoringStrategy>
        {
            new ExtractMethodStrategy(),
            new ExtractClassStrategy(_cohesionAnalyzer),
            new SplitGodClassStrategy(_cohesionAnalyzer),
            new SimplifyMethodStrategy(),
            new ExtractInterfaceStrategy(),
            new ReplaceConditionalStrategy()
        };
    }

    /// <summary>
    /// Analyze a project for refactoring opportunities.
    /// </summary>
    public async Task<RefactoringPlan> AnalyzeRefactoringAsync(
        Project project,
        RefactoringOptions? options = null)
    {
        options ??= new RefactoringOptions();
        var opportunities = new List<RefactoringOpportunity>();

        // Use CodeQualityAnalyzer to detect smells
        var qualityAnalyzer = new CodeQualityAnalyzer();
        var qualityResult = await qualityAnalyzer.AnalyzeAsync(project);

        foreach (var document in project.Documents)
        {
            if (document.FilePath == null)
                continue;

            // Skip generated files
            if (IsGeneratedFile(document.FilePath))
                continue;

            var semanticModel = await document.GetSemanticModelAsync();
            var syntaxRoot = await document.GetSyntaxRootAsync();

            if (semanticModel == null || syntaxRoot == null)
                continue;

            // Convert quality issues to code smells
            var documentSmells = MapQualityIssuesToSmells(
                qualityResult.Issues.Where(i => i.FilePath == document.FilePath).ToList(),
                document.FilePath);

            // Add cohesion-based smells
            var cohesionSmells = await _cohesionAnalyzer.DetectCohesionSmellsAsync(document);
            documentSmells.AddRange(cohesionSmells);

            // Filter by minimum severity
            var filteredSmells = documentSmells
                .Where(s => s.Severity >= options.MinimumSeverity)
                .ToList();

            foreach (var smell in filteredSmells)
            {
                var applicableStrategies = GetApplicableStrategies(smell, options);
                if (applicableStrategies.Count == 0)
                    continue;

                var estimates = await GetStrategyEstimatesAsync(document, smell, applicableStrategies);

                opportunities.Add(new RefactoringOpportunity
                {
                    Smell = smell,
                    DocumentId = document.Id,
                    ApplicableStrategies = applicableStrategies.Select(s => s.RefactoringType).ToList(),
                    EstimatedComplexityReduction = (int)estimates.Max(e => e.EstimatedComplexityReduction),
                    EstimatedCohesionImprovement = (int)estimates.Max(e => e.EstimatedCohesionImprovement),
                    Recommendation = GenerateRecommendation(smell, applicableStrategies)
                });
            }
        }

        // Sort by severity and estimated impact
        opportunities = opportunities
            .OrderByDescending(o => (int)o.Smell.Severity)
            .ThenByDescending(o => o.EstimatedComplexityReduction + o.EstimatedCohesionImprovement)
            .ToList();

        return new RefactoringPlan
        {
            ProjectPath = project.FilePath ?? "",
            AnalyzedAt = DateTime.UtcNow,
            Opportunities = opportunities,
            Summary = BuildSummary(opportunities)
        };
    }

    /// <summary>
    /// Compare multiple refactoring strategies for a given opportunity.
    /// </summary>
    public async Task<RefactoringComparison> CompareStrategiesAsync(
        Project project,
        RefactoringOpportunity opportunity,
        RefactoringOptions? options = null)
    {
        options ??= new RefactoringOptions();
        var document = project.GetDocument(opportunity.DocumentId);

        if (document == null)
        {
            throw new ArgumentException($"Document not found: {opportunity.DocumentId}");
        }

        // Get applicable strategies
        var strategies = _strategies
            .Where(s => opportunity.ApplicableStrategies.Contains(s.RefactoringType))
            .Where(s => !options.ExcludedStrategies.Contains(s.RefactoringType))
            .Take(options.MaxStrategiesToCompare)
            .ToList();

        if (strategies.Count == 0)
        {
            return new RefactoringComparison
            {
                OriginalSolution = project.Solution,
                OriginalDocument = document,
                Opportunity = opportunity,
                Results = [],
                BestResult = null
            };
        }

        // Compare strategies using virtual workspace
        _workspaceManager.LoadFromProject(project);
        var transformations = strategies.Cast<ITransformationStrategy>().ToList();
        var comparison = await _workspaceManager.CompareTransformationsAsync(
            document.Id,
            transformations);

        // Convert to refactoring results with extended scoring
        var results = new List<RefactoringStrategyResult>();
        var failedResults = new List<RefactoringStrategyResult>();

        foreach (var branchResult in comparison.Results)
        {
            var strategy = strategies.First(s => s.Name == branchResult.StrategyName);
            var transformedDoc = branchResult.TransformedSolution?.GetDocument(document.Id);

            RefactoringScore refactoringScore;
            if (transformedDoc != null && branchResult.Score.CompilationValid)
            {
                refactoringScore = await _scorer.ScoreRefactoringAsync(
                    document, transformedDoc, branchResult.Score);
            }
            else
            {
                refactoringScore = new RefactoringScore
                {
                    BaseScore = branchResult.Score,
                    OverallRefactoringScore = branchResult.Score.OverallScore
                };
            }

            var result = new RefactoringStrategyResult
            {
                StrategyType = strategy.RefactoringType,
                StrategyName = strategy.Name,
                Description = strategy.Description,
                BranchName = branchResult.BranchName,
                Score = refactoringScore,
                Diff = branchResult.Diff,
                TransformedSolution = branchResult.TransformedSolution,
                Error = branchResult.Error
            };

            if (branchResult.Error != null || !branchResult.Score.CompilationValid)
            {
                failedResults.Add(result);
            }
            else
            {
                results.Add(result);
            }
        }

        // Rank by overall refactoring score
        results = results.OrderByDescending(r => r.Score.OverallRefactoringScore).ToList();
        var bestResult = results.FirstOrDefault(r => r.Score.OverallRefactoringScore >= options.MinimumScore);

        return new RefactoringComparison
        {
            OriginalSolution = project.Solution,
            OriginalDocument = document,
            Opportunity = opportunity,
            Results = results,
            FailedResults = failedResults,
            BestResult = bestResult
        };
    }

    /// <summary>
    /// Apply the best refactoring strategy from a comparison.
    /// </summary>
    public async Task<Models.RefactoringResult> ApplyBestStrategyAsync(
        RefactoringComparison comparison,
        RefactoringOptions? options = null)
    {
        options ??= new RefactoringOptions();

        if (comparison.BestResult == null)
        {
            return new Models.RefactoringResult
            {
                Success = false,
                StrategyResult = comparison.Results.FirstOrDefault() ?? new RefactoringStrategyResult
                {
                    StrategyType = RefactoringType.ExtractMethod,
                    StrategyName = "None",
                    Description = "No applicable strategy",
                    BranchName = "none",
                    Score = new RefactoringScore
                    {
                        BaseScore = new TransformationScore { OverallScore = -100 },
                        OverallRefactoringScore = -100
                    },
                    Diff = new DocumentDiff()
                },
                Error = "No suitable refactoring strategy found that meets the minimum score requirement"
            };
        }

        var bestResult = comparison.BestResult;

        // Verify score meets minimum
        if (bestResult.Score.OverallRefactoringScore < options.MinimumScore)
        {
            return new Models.RefactoringResult
            {
                Success = false,
                StrategyResult = bestResult,
                Error = $"Best strategy score ({bestResult.Score.OverallRefactoringScore:F1}) is below minimum ({options.MinimumScore})"
            };
        }

        // Create backup if requested
        string? backupId = null;
        var modifiedFiles = new List<string>();

        if (options.CreateBackup && comparison.OriginalDocument.FilePath != null)
        {
            var filesToBackup = new List<string> { comparison.OriginalDocument.FilePath };
            backupId = await _backupService.CreateBackupAsync(filesToBackup);
        }

        // Apply the transformation
        try
        {
            if (bestResult.TransformedSolution != null)
            {
                var originalDoc = comparison.OriginalDocument;
                var transformedDoc = bestResult.TransformedSolution.GetDocument(originalDoc.Id);

                if (transformedDoc != null && originalDoc.FilePath != null)
                {
                    var newText = await transformedDoc.GetTextAsync();
                    await File.WriteAllTextAsync(originalDoc.FilePath, newText.ToString());
                    modifiedFiles.Add(originalDoc.FilePath);
                }
            }

            return new Models.RefactoringResult
            {
                Success = true,
                StrategyResult = bestResult,
                BackupId = backupId,
                ModifiedFiles = modifiedFiles
            };
        }
        catch (Exception ex)
        {
            // Attempt rollback if we have a backup
            if (backupId != null)
            {
                await _backupService.RestoreBackupAsync(backupId);
            }

            return new Models.RefactoringResult
            {
                Success = false,
                StrategyResult = bestResult,
                BackupId = backupId,
                Error = $"Failed to apply refactoring: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Apply a chain of refactoring strategies in sequence.
    /// </summary>
    public async Task<ChainResult> ApplyStrategyChainAsync(
        Project project,
        DocumentId documentId,
        StrategyChain chain,
        RefactoringOptions? options = null)
    {
        options ??= new RefactoringOptions();
        var results = new List<Models.RefactoringResult>();
        var currentProject = project;

        // Create backup of original state
        var document = project.GetDocument(documentId);
        string? backupId = null;

        if (options.CreateBackup && document?.FilePath != null)
        {
            backupId = await _backupService.CreateBackupAsync(new List<string> { document.FilePath });
        }

        foreach (var strategyType in chain.Strategies)
        {
            var strategy = _strategies.FirstOrDefault(s => s.RefactoringType == strategyType);
            if (strategy == null)
            {
                return new ChainResult
                {
                    Success = false,
                    StepResults = results,
                    StepsCompleted = results.Count,
                    TotalSteps = chain.Strategies.Count,
                    StoppedAtStep = strategyType.ToString(),
                    StopReason = $"Strategy {strategyType} not found"
                };
            }

            // Get current document
            var currentDoc = currentProject.GetDocument(documentId);
            if (currentDoc == null)
            {
                return new ChainResult
                {
                    Success = false,
                    StepResults = results,
                    StepsCompleted = results.Count,
                    TotalSteps = chain.Strategies.Count,
                    StoppedAtStep = strategyType.ToString(),
                    StopReason = "Document no longer exists"
                };
            }

            // Check if strategy can still apply
            if (!await strategy.CanApplyAsync(currentDoc))
            {
                // Skip this strategy, continue with next
                continue;
            }

            // Apply strategy
            var transformedSolution = await strategy.ApplyAsync(currentProject.Solution, documentId);
            var transformedDoc = transformedSolution.GetDocument(documentId);

            if (transformedDoc == null)
            {
                return new ChainResult
                {
                    Success = false,
                    StepResults = results,
                    StepsCompleted = results.Count,
                    TotalSteps = chain.Strategies.Count,
                    StoppedAtStep = strategyType.ToString(),
                    StopReason = "Transformation failed to produce document"
                };
            }

            // Score the transformation
            var baseScore = await _baseScorer.ScoreAsync(currentDoc, transformedDoc);
            var refactoringScore = await _scorer.ScoreRefactoringAsync(currentDoc, transformedDoc, baseScore);

            // Check for regression
            if (options.StopOnRegression && refactoringScore.OverallRefactoringScore < 0)
            {
                return new ChainResult
                {
                    Success = false,
                    StepResults = results,
                    FinalScore = refactoringScore,
                    StepsCompleted = results.Count,
                    TotalSteps = chain.Strategies.Count,
                    StoppedAtStep = strategyType.ToString(),
                    StopReason = $"Regression detected: score {refactoringScore.OverallRefactoringScore:F1}"
                };
            }

            // Generate diff
            var diff = await _diffEngine.GenerateDiffAsync(currentDoc, transformedDoc);

            var stepResult = new Models.RefactoringResult
            {
                Success = true,
                StrategyResult = new RefactoringStrategyResult
                {
                    StrategyType = strategyType,
                    StrategyName = strategy.Name,
                    Description = strategy.Description,
                    BranchName = $"chain-step-{results.Count + 1}",
                    Score = refactoringScore,
                    Diff = diff,
                    TransformedSolution = transformedSolution
                }
            };

            results.Add(stepResult);

            // Update current project for next iteration
            currentProject = transformedSolution.GetProject(currentProject.Id)!;
        }

        // Apply final changes to disk
        var finalDoc = currentProject.GetDocument(documentId);
        if (finalDoc?.FilePath != null)
        {
            var finalText = await finalDoc.GetTextAsync();
            await File.WriteAllTextAsync(finalDoc.FilePath, finalText.ToString());
        }

        // Calculate final score
        var originalDoc = project.GetDocument(documentId);
        RefactoringScore? finalScore = null;
        if (originalDoc != null && finalDoc != null)
        {
            var baseScore = await _baseScorer.ScoreAsync(originalDoc, finalDoc);
            finalScore = await _scorer.ScoreRefactoringAsync(originalDoc, finalDoc, baseScore);
        }

        return new ChainResult
        {
            Success = true,
            StepResults = results,
            FinalScore = finalScore,
            StepsCompleted = results.Count,
            TotalSteps = chain.Strategies.Count
        };
    }

    /// <summary>
    /// Get a recommended strategy chain for a god class.
    /// </summary>
    public StrategyChain GetGodClassRemediationChain()
    {
        return _composer.ComposeForGodClass();
    }

    /// <summary>
    /// Get a recommended strategy chain for a long method.
    /// </summary>
    public StrategyChain GetLongMethodRemediationChain()
    {
        return _composer.ComposeForLongMethod();
    }

    private List<CodeSmell> MapQualityIssuesToSmells(List<CodeQualityIssue> issues, string filePath)
    {
        var smells = new List<CodeSmell>();

        foreach (var issue in issues)
        {
            var smellType = MapIssueTypeToSmellType(issue.IssueType);
            if (smellType == null)
                continue;

            // Extract target name from message (e.g., "Class 'MyClass' has too many methods")
            var targetName = ExtractTargetName(issue.Message);

            smells.Add(new CodeSmell
            {
                SmellType = smellType.Value,
                Severity = MapSeverity(issue.Severity),
                FilePath = filePath,
                StartLine = issue.Line,
                EndLine = issue.Line,
                TargetName = targetName,
                Description = issue.Message,
                Metrics = new Dictionary<string, object>
                {
                    ["originalSeverity"] = issue.Severity
                }
            });
        }

        return smells;
    }

    private string ExtractTargetName(string message)
    {
        // Try to extract name from patterns like "Class 'MyClass'", "Method 'DoSomething'"
        var match = System.Text.RegularExpressions.Regex.Match(message, @"'([^']+)'");
        return match.Success ? match.Groups[1].Value : "Unknown";
    }

    private CodeSmellType? MapIssueTypeToSmellType(string issueType)
    {
        return issueType switch
        {
            "GodClass" => CodeSmellType.GodClass,
            "LargeClass" => CodeSmellType.LargeClass,
            "LongMethod" => CodeSmellType.LongMethod,
            "TooManyParameters" => CodeSmellType.TooManyParameters,
            "DeepNesting" => CodeSmellType.DeepNesting,
            "FeatureEnvy" => CodeSmellType.FeatureEnvy,
            "DataClump" => CodeSmellType.DataClump,
            _ => null
        };
    }

    private SmellSeverity MapSeverity(string severity)
    {
        return severity switch
        {
            "Critical" => SmellSeverity.Critical,
            "High" => SmellSeverity.High,
            "Medium" => SmellSeverity.Medium,
            _ => SmellSeverity.Low
        };
    }

    private List<IRefactoringStrategy> GetApplicableStrategies(CodeSmell smell, RefactoringOptions options)
    {
        return _strategies
            .Where(s => s.AddressesSmells.Contains(smell.SmellType))
            .Where(s => options.AllowedStrategies.Count == 0 || options.AllowedStrategies.Contains(s.RefactoringType))
            .Where(s => !options.ExcludedStrategies.Contains(s.RefactoringType))
            .ToList();
    }

    private async Task<List<RefactoringEstimate>> GetStrategyEstimatesAsync(
        Document document,
        CodeSmell smell,
        List<IRefactoringStrategy> strategies)
    {
        var estimates = new List<RefactoringEstimate>();

        foreach (var strategy in strategies)
        {
            try
            {
                var estimate = await strategy.EstimateImprovementAsync(document, smell);
                estimates.Add(estimate);
            }
            catch
            {
                // Skip strategies that fail to estimate
            }
        }

        return estimates;
    }

    private string GenerateRecommendation(CodeSmell smell, List<IRefactoringStrategy> strategies)
    {
        if (strategies.Count == 0)
            return "No automated refactoring available for this smell.";

        var primaryStrategy = strategies.First();
        return smell.SmellType switch
        {
            CodeSmellType.GodClass =>
                $"Consider using {primaryStrategy.Name} to split this class into smaller, focused components.",
            CodeSmellType.LargeClass =>
                $"Use {primaryStrategy.Name} to extract cohesive functionality into separate classes.",
            CodeSmellType.LongMethod =>
                $"Apply {primaryStrategy.Name} to break down this method into smaller, reusable pieces.",
            CodeSmellType.DeepNesting =>
                $"Use {primaryStrategy.Name} to reduce nesting with guard clauses and early returns.",
            CodeSmellType.SwitchStatement =>
                $"Consider {primaryStrategy.Name} to replace conditional logic with polymorphism.",
            _ => $"Consider applying {primaryStrategy.Name} to improve code quality."
        };
    }

    private RefactoringSummary BuildSummary(List<RefactoringOpportunity> opportunities)
    {
        return new RefactoringSummary
        {
            TotalOpportunities = opportunities.Count,
            CriticalCount = opportunities.Count(o => o.Smell.Severity == SmellSeverity.Critical),
            HighCount = opportunities.Count(o => o.Smell.Severity == SmellSeverity.High),
            MediumCount = opportunities.Count(o => o.Smell.Severity == SmellSeverity.Medium),
            LowCount = opportunities.Count(o => o.Smell.Severity == SmellSeverity.Low),
            BySmellType = opportunities
                .GroupBy(o => o.Smell.SmellType)
                .ToDictionary(g => g.Key, g => g.Count()),
            ByStrategyType = opportunities
                .SelectMany(o => o.ApplicableStrategies)
                .GroupBy(s => s)
                .ToDictionary(g => g.Key, g => g.Count()),
            EstimatedTotalComplexityReduction = opportunities.Sum(o => o.EstimatedComplexityReduction)
        };
    }

    private bool IsGeneratedFile(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        return fileName.EndsWith(".g.cs") ||
               fileName.EndsWith(".Designer.cs") ||
               fileName.EndsWith(".generated.cs") ||
               filePath.Contains("obj" + Path.DirectorySeparatorChar);
    }
}
