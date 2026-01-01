using System.ComponentModel;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using BaseScanner.Services;
using BaseScanner.Refactoring;
using BaseScanner.Refactoring.Models;
using BaseScanner.Refactoring.Analysis;
using BaseScanner.Refactoring.Composition;
using BaseScanner.VirtualWorkspace;

namespace BaseScanner.Tools;

[McpServerToolType]
public static class RefactoringTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [McpServerTool]
    [Description("Analyze a C# project for refactoring opportunities including god classes, long methods, deep nesting, and low cohesion. Returns ranked opportunities with applicable strategies.")]
    public static async Task<string> AnalyzeRefactoringOpportunities(
        [Description("Path to .csproj file or directory containing a C# project")]
        string projectPath,
        [Description("Minimum severity to report: critical, high, medium, low. Default: medium")]
        string minSeverity = "medium",
        [Description("Types to analyze: godclass, longmethod, nesting, all. Default: all")]
        string types = "all")
    {
        try
        {
            var service = new AnalysisService();
            var project = await service.OpenProjectAsync(projectPath);

            var options = new RefactoringOptions
            {
                MinimumSeverity = minSeverity.ToLowerInvariant() switch
                {
                    "critical" => SmellSeverity.Critical,
                    "high" => SmellSeverity.High,
                    "medium" => SmellSeverity.Medium,
                    _ => SmellSeverity.Low
                }
            };

            var workspaceManager = new VirtualWorkspaceManager();
            var backupService = new BackupService(projectPath);
            var orchestrator = new RefactoringOrchestrator(workspaceManager, backupService);

            var plan = await orchestrator.AnalyzeRefactoringAsync(project, options);

            // Filter by type if specified
            var opportunities = plan.Opportunities.AsEnumerable();
            if (types != "all")
            {
                var typeFilters = types.ToLowerInvariant().Split(',').Select(t => t.Trim()).ToHashSet();
                opportunities = opportunities.Where(o =>
                    (typeFilters.Contains("godclass") && o.Smell.SmellType == CodeSmellType.GodClass) ||
                    (typeFilters.Contains("longmethod") && o.Smell.SmellType == CodeSmellType.LongMethod) ||
                    (typeFilters.Contains("nesting") && o.Smell.SmellType == CodeSmellType.DeepNesting) ||
                    (typeFilters.Contains("largeclass") && o.Smell.SmellType == CodeSmellType.LargeClass));
            }

            var result = new
            {
                projectPath = plan.ProjectPath,
                analyzedAt = plan.AnalyzedAt,
                summary = plan.Summary,
                opportunities = opportunities.Take(50).Select(o => new
                {
                    smell = new
                    {
                        type = o.Smell.SmellType.ToString(),
                        severity = o.Smell.Severity.ToString(),
                        filePath = o.Smell.FilePath,
                        startLine = o.Smell.StartLine,
                        endLine = o.Smell.EndLine,
                        targetName = o.Smell.TargetName,
                        description = o.Smell.Description,
                        metrics = o.Smell.Metrics
                    },
                    applicableStrategies = o.ApplicableStrategies.Select(s => s.ToString()).ToList(),
                    estimatedComplexityReduction = o.EstimatedComplexityReduction,
                    estimatedCohesionImprovement = o.EstimatedCohesionImprovement,
                    recommendation = o.Recommendation
                }).ToList()
            };

            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    [McpServerTool]
    [Description("Preview refactoring by comparing multiple strategies in a virtual workspace. Returns ranked results with diffs showing proposed changes without modifying files.")]
    public static async Task<string> PreviewRefactoring(
        [Description("Path to .csproj file or directory containing a C# project")]
        string projectPath,
        [Description("Path to the specific file to refactor")]
        string filePath,
        [Description("Name of the class or method to refactor")]
        string targetName,
        [Description("Maximum strategies to compare. Default: 5")]
        int maxStrategies = 5)
    {
        try
        {
            var service = new AnalysisService();
            var project = await service.OpenProjectAsync(projectPath);

            // Find the document
            var document = project.Documents.FirstOrDefault(d =>
                d.FilePath?.EndsWith(filePath, StringComparison.OrdinalIgnoreCase) == true ||
                d.FilePath == filePath);

            if (document == null)
            {
                return JsonSerializer.Serialize(new { error = $"File not found: {filePath}" }, JsonOptions);
            }

            var workspaceManager = new VirtualWorkspaceManager();
            var backupService = new BackupService(projectPath);
            var orchestrator = new RefactoringOrchestrator(workspaceManager, backupService);
            var cohesionAnalyzer = new CohesionAnalyzer();

            // Detect smells for the target
            var smells = await cohesionAnalyzer.DetectCohesionSmellsAsync(document);
            var targetSmell = smells.FirstOrDefault(s => s.TargetName == targetName);

            if (targetSmell == null)
            {
                // Create a synthetic opportunity for the target
                targetSmell = new CodeSmell
                {
                    SmellType = CodeSmellType.GodClass,
                    Severity = SmellSeverity.High,
                    FilePath = document.FilePath ?? "",
                    StartLine = 1,
                    EndLine = 1,
                    TargetName = targetName,
                    Description = $"Manual refactoring target: {targetName}"
                };
            }

            var opportunity = new RefactoringOpportunity
            {
                Smell = targetSmell,
                DocumentId = document.Id,
                ApplicableStrategies = Enum.GetValues<RefactoringType>().ToList(),
                EstimatedComplexityReduction = 0,
                EstimatedCohesionImprovement = 0,
                Recommendation = ""
            };

            var options = new RefactoringOptions
            {
                MaxStrategiesToCompare = maxStrategies
            };

            var comparison = await orchestrator.CompareStrategiesAsync(project, opportunity, options);

            var result = new
            {
                originalFile = document.FilePath,
                targetName,
                totalStrategies = comparison.Results.Count,
                failedStrategies = comparison.FailedResults.Count,
                results = comparison.Results.Select(r => new
                {
                    strategyType = r.StrategyType.ToString(),
                    strategyName = r.StrategyName,
                    description = r.Description,
                    score = new
                    {
                        overall = Math.Round(r.Score.OverallRefactoringScore, 2),
                        cohesionImprovement = Math.Round(r.Score.CohesionImprovement, 2),
                        complexityDelta = r.Score.BaseScore.ComplexityDelta,
                        cognitiveComplexityDelta = r.Score.BaseScore.CognitiveComplexityDelta,
                        maintainabilityDelta = Math.Round(r.Score.BaseScore.MaintainabilityDelta, 2),
                        testabilityScore = Math.Round(r.Score.TestabilityScore, 2),
                        namingQualityScore = Math.Round(r.Score.NamingQualityScore, 2),
                        compilationValid = r.Score.BaseScore.CompilationValid,
                        semanticsPreserved = r.Score.BaseScore.SemanticsPreserved,
                        breakdown = r.Score.ScoreBreakdown
                    },
                    diff = new
                    {
                        addedLines = r.Diff.AddedLines,
                        removedLines = r.Diff.RemovedLines,
                        modifiedRegions = r.Diff.ModifiedRegions,
                        unifiedDiff = r.Diff.UnifiedDiff
                    },
                    details = r.Details,
                    warnings = r.Warnings
                }).ToList(),
                bestStrategy = comparison.BestResult != null ? new
                {
                    strategyType = comparison.BestResult.StrategyType.ToString(),
                    strategyName = comparison.BestResult.StrategyName,
                    score = Math.Round(comparison.BestResult.Score.OverallRefactoringScore, 2)
                } : null,
                failedDetails = comparison.FailedResults.Select(r => new
                {
                    strategyType = r.StrategyType.ToString(),
                    error = r.Error,
                    compilationErrors = r.Score.BaseScore.CompilationErrors
                }).ToList()
            };

            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    [McpServerTool]
    [Description("Apply the best refactoring strategy to a target. Creates a backup before applying changes that can be rolled back.")]
    public static async Task<string> ApplyRefactoring(
        [Description("Path to .csproj file or directory containing a C# project")]
        string projectPath,
        [Description("Path to the specific file to refactor")]
        string filePath,
        [Description("Name of the class or method to refactor")]
        string targetName,
        [Description("Specific strategy to apply: ExtractMethod, ExtractClass, SplitGodClass, SimplifyMethod, ExtractInterface, ReplaceConditional. If not specified, uses the best strategy.")]
        string? strategy = null,
        [Description("Minimum score required to apply. Default: 0")]
        double minimumScore = 0,
        [Description("Create backup before applying. Default: true")]
        bool createBackup = true)
    {
        try
        {
            var service = new AnalysisService();
            var project = await service.OpenProjectAsync(projectPath);

            var document = project.Documents.FirstOrDefault(d =>
                d.FilePath?.EndsWith(filePath, StringComparison.OrdinalIgnoreCase) == true ||
                d.FilePath == filePath);

            if (document == null)
            {
                return JsonSerializer.Serialize(new { error = $"File not found: {filePath}" }, JsonOptions);
            }

            var workspaceManager = new VirtualWorkspaceManager();
            var backupService = new BackupService(projectPath);
            var orchestrator = new RefactoringOrchestrator(workspaceManager, backupService);
            var cohesionAnalyzer = new CohesionAnalyzer();

            var smells = await cohesionAnalyzer.DetectCohesionSmellsAsync(document);
            var targetSmell = smells.FirstOrDefault(s => s.TargetName == targetName) ?? new CodeSmell
            {
                SmellType = CodeSmellType.GodClass,
                Severity = SmellSeverity.High,
                FilePath = document.FilePath ?? "",
                StartLine = 1,
                EndLine = 1,
                TargetName = targetName,
                Description = $"Manual refactoring target: {targetName}"
            };

            var applicableStrategies = strategy != null
                ? new List<RefactoringType> { Enum.Parse<RefactoringType>(strategy, ignoreCase: true) }
                : Enum.GetValues<RefactoringType>().ToList();

            var opportunity = new RefactoringOpportunity
            {
                Smell = targetSmell,
                DocumentId = document.Id,
                ApplicableStrategies = applicableStrategies,
                EstimatedComplexityReduction = 0,
                EstimatedCohesionImprovement = 0,
                Recommendation = ""
            };

            var options = new RefactoringOptions
            {
                MinimumScore = minimumScore,
                CreateBackup = createBackup
            };

            var comparison = await orchestrator.CompareStrategiesAsync(project, opportunity, options);
            var result = await orchestrator.ApplyBestStrategyAsync(comparison, options);

            return JsonSerializer.Serialize(new
            {
                success = result.Success,
                strategyApplied = result.StrategyResult.StrategyType.ToString(),
                strategyName = result.StrategyResult.StrategyName,
                score = Math.Round(result.StrategyResult.Score.OverallRefactoringScore, 2),
                backupId = result.BackupId,
                modifiedFiles = result.ModifiedFiles,
                createdFiles = result.CreatedFiles,
                appliedAt = result.AppliedAt,
                error = result.Error,
                diff = result.Success ? new
                {
                    addedLines = result.StrategyResult.Diff.AddedLines,
                    removedLines = result.StrategyResult.Diff.RemovedLines,
                    unifiedDiff = result.StrategyResult.Diff.UnifiedDiff
                } : null
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    [McpServerTool]
    [Description("Apply a chain of refactoring strategies for comprehensive god class remediation. Executes strategies in optimal order, stopping on regression.")]
    public static async Task<string> ApplyRefactoringChain(
        [Description("Path to .csproj file or directory containing a C# project")]
        string projectPath,
        [Description("Path to the specific file to refactor")]
        string filePath,
        [Description("Name of the class to refactor")]
        string className,
        [Description("Chain type: godclass (SimplifyMethod -> ExtractMethod -> SplitGodClass -> ExtractInterface), longmethod (SimplifyMethod -> ExtractMethod), testability (ExtractInterface -> ExtractClass), complexity (SimplifyMethod -> ReplaceConditional -> ExtractMethod). Default: godclass")]
        string chainType = "godclass",
        [Description("Stop if any step causes score regression. Default: true")]
        bool stopOnRegression = true,
        [Description("Create backup before applying. Default: true")]
        bool createBackup = true)
    {
        try
        {
            var service = new AnalysisService();
            var project = await service.OpenProjectAsync(projectPath);

            var document = project.Documents.FirstOrDefault(d =>
                d.FilePath?.EndsWith(filePath, StringComparison.OrdinalIgnoreCase) == true ||
                d.FilePath == filePath);

            if (document == null)
            {
                return JsonSerializer.Serialize(new { error = $"File not found: {filePath}" }, JsonOptions);
            }

            var workspaceManager = new VirtualWorkspaceManager();
            var backupService = new BackupService(projectPath);
            var orchestrator = new RefactoringOrchestrator(workspaceManager, backupService);
            var composer = new StrategyComposer();

            var chain = chainType.ToLowerInvariant() switch
            {
                "godclass" => composer.ComposeForGodClass(),
                "longmethod" => composer.ComposeForLongMethod(),
                "testability" => composer.ComposeForTestability(),
                "complexity" => composer.ComposeForComplexity(),
                _ => composer.ComposeForGodClass()
            };

            var options = new RefactoringOptions
            {
                StopOnRegression = stopOnRegression,
                CreateBackup = createBackup
            };

            var result = await orchestrator.ApplyStrategyChainAsync(project, document.Id, chain, options);

            return JsonSerializer.Serialize(new
            {
                success = result.Success,
                chainType,
                chainDescription = chain.Description,
                stepsCompleted = result.StepsCompleted,
                totalSteps = result.TotalSteps,
                finalScore = result.FinalScore != null ? new
                {
                    overall = Math.Round(result.FinalScore.OverallRefactoringScore, 2),
                    cohesionImprovement = Math.Round(result.FinalScore.CohesionImprovement, 2),
                    maintainabilityDelta = Math.Round(result.FinalScore.BaseScore.MaintainabilityDelta, 2)
                } : null,
                stoppedAtStep = result.StoppedAtStep,
                stopReason = result.StopReason,
                stepResults = result.StepResults.Select(sr => new
                {
                    strategy = sr.StrategyResult.StrategyType.ToString(),
                    success = sr.Success,
                    score = Math.Round(sr.StrategyResult.Score.OverallRefactoringScore, 2),
                    backupId = sr.BackupId,
                    modifiedFiles = sr.ModifiedFiles
                }).ToList()
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    [McpServerTool]
    [Description("Calculate LCOM4 (Lack of Cohesion of Methods) for classes in a project. LCOM4 > 1 indicates the class has multiple responsibilities and may benefit from splitting.")]
    public static async Task<string> AnalyzeCohesion(
        [Description("Path to .csproj file or directory containing a C# project")]
        string projectPath,
        [Description("Specific file to analyze. If not specified, analyzes all files.")]
        string? filePath = null,
        [Description("LCOM4 threshold to report. Classes above this are flagged. Default: 1")]
        double threshold = 1)
    {
        try
        {
            var service = new AnalysisService();
            var project = await service.OpenProjectAsync(projectPath);
            var cohesionAnalyzer = new CohesionAnalyzer();

            var results = new List<object>();

            var documents = filePath != null
                ? project.Documents.Where(d =>
                    d.FilePath?.EndsWith(filePath, StringComparison.OrdinalIgnoreCase) == true)
                : project.Documents;

            foreach (var document in documents)
            {
                if (document.FilePath == null || document.FilePath.Contains("obj\\"))
                    continue;

                var root = await document.GetSyntaxRootAsync();
                var model = await document.GetSemanticModelAsync();

                if (root == null || model == null)
                    continue;

                foreach (var classDecl in root.DescendantNodes()
                    .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>())
                {
                    var lcom4 = cohesionAnalyzer.CalculateLCOM4(classDecl, model);

                    if (lcom4 > threshold)
                    {
                        var clusters = cohesionAnalyzer.FindCohesiveClusters(classDecl, model);
                        var responsibilities = cohesionAnalyzer.IdentifyResponsibilities(classDecl, model);

                        results.Add(new
                        {
                            filePath = document.FilePath,
                            className = classDecl.Identifier.Text,
                            lcom4 = Math.Round(lcom4, 2),
                            methodCount = classDecl.Members
                                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
                                .Count(),
                            fieldCount = classDecl.Members
                                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.FieldDeclarationSyntax>()
                                .Sum(f => f.Declaration.Variables.Count),
                            clusters = clusters.Select(c => new
                            {
                                suggestedClassName = c.SuggestedClassName,
                                methodCount = c.MethodNames.Count,
                                methods = c.MethodNames,
                                sharedFields = c.SharedFields,
                                cohesionScore = Math.Round(c.CohesionScore, 2),
                                totalComplexity = c.TotalComplexity,
                                responsibility = c.SuggestedResponsibility
                            }).ToList(),
                            responsibilities = responsibilities.Select(r => new
                            {
                                name = r.ResponsibilityName,
                                methods = r.Methods,
                                fields = r.Fields,
                                properties = r.Properties,
                                cohesion = Math.Round(r.Cohesion, 2),
                                dependencies = r.Dependencies
                            }).ToList()
                        });
                    }
                }
            }

            return JsonSerializer.Serialize(new
            {
                projectPath,
                threshold,
                classesAboveThreshold = results.Count,
                results = results.OrderByDescending(r =>
                    ((dynamic)r).lcom4).ToList()
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    [McpServerTool]
    [Description("Get recommended refactoring strategy chains for different scenarios.")]
    public static string GetRefactoringChains()
    {
        var composer = new StrategyComposer();

        var chains = new[]
        {
            new
            {
                name = "godclass",
                chain = composer.ComposeForGodClass(),
                description = "Comprehensive god class remediation"
            },
            new
            {
                name = "longmethod",
                chain = composer.ComposeForLongMethod(),
                description = "Long method remediation"
            },
            new
            {
                name = "testability",
                chain = composer.ComposeForTestability(),
                description = "Improve testability with interfaces and separation"
            },
            new
            {
                name = "complexity",
                chain = composer.ComposeForComplexity(),
                description = "Reduce complexity and improve readability"
            }
        };

        var result = chains.Select(c => new
        {
            c.name,
            c.description,
            strategies = c.chain.Strategies.Select(s => s.ToString()).ToList(),
            chainDescription = c.chain.Description,
            estimatedImpact = c.chain.EstimatedImpact,
            prerequisites = c.chain.Prerequisites
        }).ToList();

        return JsonSerializer.Serialize(new { refactoringChains = result }, JsonOptions);
    }

    [McpServerTool]
    [Description("List available refactoring strategies and what code smells they address.")]
    public static string ListRefactoringStrategies()
    {
        var strategies = new[]
        {
            new
            {
                type = "SimplifyMethod",
                name = "Simplify Method",
                description = "Reduces complexity with guard clauses, early returns, and flattened nesting",
                addressesSmells = new[] { "LongMethod", "DeepNesting" },
                compositionOrder = "Should be applied first to simplify before extraction"
            },
            new
            {
                type = "ExtractMethod",
                name = "Extract Method",
                description = "Extracts cohesive code blocks into smaller, reusable methods",
                addressesSmells = new[] { "LongMethod", "DeepNesting" },
                compositionOrder = "Apply after SimplifyMethod, before ExtractClass"
            },
            new
            {
                type = "ExtractClass",
                name = "Extract Class",
                description = "Extracts cohesive method clusters into separate classes",
                addressesSmells = new[] { "GodClass", "LargeClass" },
                compositionOrder = "Apply after ExtractMethod, before ExtractInterface"
            },
            new
            {
                type = "SplitGodClass",
                name = "Split God Class",
                description = "Splits god classes into multiple focused classes by responsibility",
                addressesSmells = new[] { "GodClass" },
                compositionOrder = "Alternative to ExtractClass for severe cases"
            },
            new
            {
                type = "ExtractInterface",
                name = "Extract Interface",
                description = "Extracts interface from public members to improve testability",
                addressesSmells = new[] { "GodClass", "LargeClass" },
                compositionOrder = "Apply last to create interfaces from refined classes"
            },
            new
            {
                type = "ReplaceConditional",
                name = "Replace Conditional with Polymorphism",
                description = "Replaces switch-on-type patterns with polymorphic class hierarchy",
                addressesSmells = new[] { "SwitchStatement", "LongMethod" },
                compositionOrder = "Can be applied independently"
            }
        };

        return JsonSerializer.Serialize(new { strategies }, JsonOptions);
    }
}
