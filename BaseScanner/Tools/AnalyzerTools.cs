using System.ComponentModel;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using BaseScanner.Services;
using BaseScanner.Analyzers.Security;
using BaseScanner.Analyzers.Concurrency;
using BaseScanner.Analyzers.Frameworks;
using BaseScanner.Analyzers.Quality;
using BaseScanner.Analysis;
using BaseScanner.Transformers;
using BaseScanner.Transformers.Core;
using BaseScanner.VirtualWorkspace;

namespace BaseScanner.Tools;

[McpServerToolType]
public static class AnalyzerTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [McpServerTool]
    [Description("Analyze a C# project for code quality issues, refactoring opportunities, architecture concerns, safety issues, and optimization opportunities using Roslyn compiler analysis.")]
    public static async Task<string> AnalyzeCsharpProject(
        [Description("Path to .csproj file or directory containing a C# project")]
        string projectPath,
        [Description("Comma-separated list of analyses to run. Options: unused_files, deep, sentiment, perf, exceptions, resources, deps, magic, git, refactor, arch, safety, optimize, all. Default: all")]
        string analyses = "all")
    {
        try
        {
            var options = AnalysisOptions.Parse(analyses);
            var service = new AnalysisService();
            var result = await service.AnalyzeAsync(projectPath, options);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (FileNotFoundException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
        catch (ArgumentException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = $"Analysis failed: {ex.Message}" }, JsonOptions);
        }
    }

    [McpServerTool]
    [Description("Get a quick summary of C# project health without detailed analysis. Faster than full analysis.")]
    public static async Task<string> QuickProjectScan(
        [Description("Path to .csproj file or directory containing a C# project")]
        string projectPath)
    {
        try
        {
            var options = new AnalysisOptions
            {
                UnusedFiles = true,
                PerformanceAnalysis = true,
                ExceptionAnalysis = true,
                RefactoringAnalysis = true
            };

            var service = new AnalysisService();
            var result = await service.AnalyzeAsync(projectPath, options);

            // Return just the summary for quick insights
            var quickSummary = new
            {
                projectPath = result.ProjectPath,
                summary = result.Summary,
                unusedFiles = result.UnusedFiles,
                topIssues = new
                {
                    criticalPerformanceIssues = result.PerformanceIssues?
                        .Where(i => i.Severity == "Critical")
                        .Take(5)
                        .Select(i => new { i.FilePath, i.Line, i.Message })
                        .ToList(),
                    godClasses = result.Refactoring?.GodClasses
                        .Take(5)
                        .Select(g => new { g.FilePath, g.ClassName, g.MethodCount, g.LCOM })
                        .ToList(),
                    longMethods = result.Refactoring?.LongMethods
                        .Take(5)
                        .Select(m => new { m.FilePath, m.ClassName, m.MethodName, m.LineCount, m.Complexity })
                        .ToList()
                }
            };

            return JsonSerializer.Serialize(quickSummary, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    [McpServerTool]
    [Description("List available analysis types and their descriptions.")]
    public static string ListAnalysisTypes()
    {
        var analysisTypes = new[]
        {
            new { name = "unused_files", description = "Find .cs files not included in project compilation" },
            new { name = "deep", description = "Usage counting, deprecated code detection, dead code analysis" },
            new { name = "sentiment", description = "Code quality scoring, complexity metrics, duplicate detection" },
            new { name = "perf", description = "Async issues, performance anti-patterns (async void, blocking calls)" },
            new { name = "exceptions", description = "Empty catch blocks, swallowed exceptions, lost stack traces" },
            new { name = "resources", description = "IDisposable leaks, missing using statements, event handler leaks" },
            new { name = "deps", description = "Circular dependencies, high coupling metrics" },
            new { name = "magic", description = "Magic numbers and strings that should be constants" },
            new { name = "git", description = "Git history analysis, file churn, hotspots, stale code" },
            new { name = "refactor", description = "Long methods, god classes, feature envy, parameter smells" },
            new { name = "arch", description = "Public API surface, call graph, inheritance depth, interface segregation" },
            new { name = "safety", description = "Null safety issues, immutability opportunities, logging coverage" },
            new { name = "optimize", description = "Code optimization opportunities with generated refactored code suggestions" },
            new { name = "all", description = "Run all analysis types" }
        };

        return JsonSerializer.Serialize(new { analysisTypes }, JsonOptions);
    }

    [McpServerTool]
    [Description("Analyze C# code for optimization opportunities and generate refactored code suggestions with semantic safety guarantees.")]
    public static async Task<string> AnalyzeOptimizations(
        [Description("Path to .csproj file or directory containing a C# project")]
        string projectPath,
        [Description("Category filter: performance, readability, modernization, all. Default: all")]
        string category = "all",
        [Description("Minimum confidence level: high, medium, low. Default: medium")]
        string minConfidence = "medium")
    {
        try
        {
            var options = new AnalysisOptions { OptimizationAnalysis = true };
            var service = new AnalysisService();
            var result = await service.AnalyzeAsync(projectPath, options);

            if (result.Optimizations == null)
            {
                return JsonSerializer.Serialize(new { message = "No optimization opportunities found" }, JsonOptions);
            }

            // Filter by category
            var opportunities = result.Optimizations.Opportunities.AsEnumerable();

            if (category != "all")
            {
                opportunities = opportunities.Where(o =>
                    o.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
            }

            // Filter by confidence
            var confidenceLevel = minConfidence.ToLowerInvariant() switch
            {
                "high" => new[] { "High" },
                "medium" => new[] { "High", "Medium" },
                _ => new[] { "High", "Medium", "Low" }
            };
            opportunities = opportunities.Where(o => confidenceLevel.Contains(o.Confidence));

            var filtered = new
            {
                summary = result.Optimizations.Summary,
                opportunities = opportunities.ToList()
            };

            return JsonSerializer.Serialize(filtered, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    [McpServerTool]
    [Description("Analyze C# code for security vulnerabilities including injection flaws, hardcoded secrets, weak cryptography, and authentication issues. Returns CWE references and remediation guidance.")]
    public static async Task<string> AnalyzeSecurity(
        [Description("Path to .csproj file or directory containing a C# project")]
        string projectPath,
        [Description("Minimum severity to report: critical, high, medium, low, all. Default: all")]
        string severity = "all")
    {
        try
        {
            var options = new AnalysisOptions { SecurityAnalysis = true };
            var service = new AnalysisService();
            var result = await service.AnalyzeAsync(projectPath, options);

            if (result.Security == null)
            {
                return JsonSerializer.Serialize(new { message = "No security vulnerabilities found" }, JsonOptions);
            }

            // Filter by severity
            var vulnerabilities = result.Security.Vulnerabilities.AsEnumerable();

            if (severity != "all")
            {
                var severityOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["critical"] = 4,
                    ["high"] = 3,
                    ["medium"] = 2,
                    ["low"] = 1
                };

                if (severityOrder.TryGetValue(severity, out var minLevel))
                {
                    vulnerabilities = vulnerabilities.Where(v =>
                        severityOrder.TryGetValue(v.Severity, out var level) && level >= minLevel);
                }
            }

            var filtered = new
            {
                summary = new
                {
                    result.Security.TotalVulnerabilities,
                    result.Security.CriticalCount,
                    result.Security.HighCount,
                    result.Security.MediumCount,
                    result.Security.LowCount,
                    result.Security.VulnerabilitiesByType,
                    result.Security.VulnerabilitiesByCwe
                },
                vulnerabilities = vulnerabilities.ToList()
            };

            return JsonSerializer.Serialize(filtered, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    [McpServerTool]
    [Description("Get a comprehensive project health dashboard with metrics including health score, complexity, maintainability index, technical debt, and hotspots.")]
    public static async Task<string> GetProjectDashboard(
        [Description("Path to .csproj file or directory containing a C# project")]
        string projectPath)
    {
        try
        {
            var options = new AnalysisOptions { DashboardAnalysis = true };
            var service = new AnalysisService();
            var result = await service.AnalyzeAsync(projectPath, options);

            if (result.Metrics == null)
            {
                return JsonSerializer.Serialize(new { error = "Unable to generate metrics dashboard" }, JsonOptions);
            }

            var dashboard = new
            {
                projectPath = result.ProjectPath,
                healthScore = result.Metrics.HealthScore,
                metrics = new
                {
                    result.Metrics.TotalFiles,
                    result.Metrics.TotalLines,
                    result.Metrics.TotalMethods,
                    result.Metrics.TotalClasses,
                    result.Metrics.AverageCyclomaticComplexity,
                    result.Metrics.MaxCyclomaticComplexity,
                    result.Metrics.MethodsAboveComplexityThreshold,
                    result.Metrics.MaintainabilityIndex,
                    technicalDebtHours = result.Metrics.TechnicalDebtMinutes / 60.0
                },
                issuesSummary = new
                {
                    result.Metrics.IssuesByCategory,
                    result.Metrics.IssuesBySeverity
                },
                hotspots = result.Metrics.Hotspots.Take(10).ToList()
            };

            return JsonSerializer.Serialize(dashboard, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    [McpServerTool]
    [Description("Preview code transformations without applying them. Shows what changes would be made for optimization opportunities.")]
    public static async Task<string> PreviewTransformations(
        [Description("Path to .csproj file or directory containing a C# project")]
        string projectPath,
        [Description("Category filter: performance, readability, modernization, all. Default: all")]
        string category = "all",
        [Description("Minimum confidence level: high, medium, low. Default: high")]
        string minConfidence = "high",
        [Description("Maximum number of transformations to preview. Default: 20")]
        int maxTransformations = 20)
    {
        try
        {
            var filter = new TransformationFilter
            {
                Categories = category == "all" ? [] : [category],
                MinConfidence = minConfidence,
                MaxTransformations = maxTransformations
            };

            var analysisService = new AnalysisService();
            var project = await analysisService.OpenProjectAsync(projectPath);
            var backupService = new BackupService(projectPath);
            var service = new TransformationService(backupService);
            var preview = await service.PreviewAsync(project, filter);

            return JsonSerializer.Serialize(new
            {
                preview.Success,
                totalTransformations = preview.TotalTransformations,
                transformationsByType = preview.TransformationsByType,
                transformations = preview.Previews.Select(t => new
                {
                    t.FilePath,
                    t.StartLine,
                    t.EndLine,
                    t.Category,
                    t.TransformationType,
                    t.OriginalCode,
                    t.SuggestedCode,
                    t.Confidence
                }).ToList()
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    [McpServerTool]
    [Description("Apply code transformations to optimize the codebase. Creates a backup before applying changes that can be rolled back.")]
    public static async Task<string> ApplyTransformations(
        [Description("Path to .csproj file or directory containing a C# project")]
        string projectPath,
        [Description("Category filter: performance, readability, modernization, all. Default: all")]
        string category = "all",
        [Description("Minimum confidence level: high, medium, low. Default: high")]
        string minConfidence = "high",
        [Description("Whether to create a backup before applying. Default: true")]
        bool createBackup = true,
        [Description("Maximum number of transformations to apply. Default: 50")]
        int maxTransformations = 50)
    {
        try
        {
            var filter = new TransformationFilter
            {
                Categories = category == "all" ? [] : [category],
                MinConfidence = minConfidence,
                MaxTransformations = maxTransformations
            };

            var options = new TransformationOptions
            {
                FormatOutput = true
            };

            var analysisService = new AnalysisService();
            var project = await analysisService.OpenProjectAsync(projectPath);
            var backupService = new BackupService(projectPath);
            var service = new TransformationService(backupService);
            var result = await service.ApplyAsync(project, filter, options);

            return JsonSerializer.Serialize(new
            {
                result.Success,
                result.TotalTransformations,
                result.FilesModified,
                result.BackupId,
                errorMessage = result.ErrorMessage,
                results = result.Results.Select(r => new
                {
                    r.TransformationType,
                    r.Success,
                    changes = r.Changes.Select(c => new { c.FilePath, c.OriginalCode, c.TransformedCode }).ToList()
                }).ToList()
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    [McpServerTool]
    [Description("Rollback previously applied transformations by restoring from backup.")]
    public static async Task<string> RollbackTransformations(
        [Description("Path to .csproj file or directory containing a C# project")]
        string projectPath,
        [Description("Specific backup ID to restore. If not provided, restores the most recent backup.")]
        string? backupId = null)
    {
        try
        {
            var backupService = new BackupService(projectPath);
            var service = new TransformationService(backupService);
            var result = await service.RollbackAsync(backupId);

            return JsonSerializer.Serialize(new
            {
                result.Success,
                result.BackupId,
                result.FilesRestored,
                result.ErrorMessage
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    [McpServerTool]
    [Description("List available transformation backups that can be used for rollback.")]
    public static async Task<string> ListTransformationBackups(
        [Description("Path to .csproj file or directory containing a C# project")]
        string projectPath)
    {
        try
        {
            var backupService = new BackupService(projectPath);
            var backups = await backupService.ListBackupsAsync();

            return JsonSerializer.Serialize(new
            {
                backups = backups.Select(b => new
                {
                    b.Id,
                    b.CreatedAt,
                    b.FileCount,
                    b.Description
                }).ToList()
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    [McpServerTool]
    [Description("Analyze taint flow from untrusted sources to security-sensitive sinks. Helps identify potential injection vulnerabilities.")]
    public static async Task<string> AnalyzeTaintFlow(
        [Description("Path to .csproj file or directory containing a C# project")]
        string projectPath,
        [Description("Treat all method parameters as tainted. Default: true")]
        bool treatParametersAsTainted = true)
    {
        try
        {
            var analysisService = new AnalysisService();
            var project = await analysisService.OpenProjectAsync(projectPath);

            var taintTracker = new TaintTracker();
            var config = new TaintConfiguration
            {
                TreatParametersAsTainted = treatParametersAsTainted
            };

            var flows = await taintTracker.TrackAsync(project, config);

            var result = new
            {
                totalFlows = flows.Count,
                unsanitizedFlows = flows.Count(f => !f.IsSanitized),
                sanitizedFlows = flows.Count(f => f.IsSanitized),
                flowsBySourceType = flows
                    .GroupBy(f => f.Source.SourceType)
                    .ToDictionary(g => g.Key, g => g.Count()),
                flows = flows.Select(f => new
                {
                    sourceType = f.Source.SourceType,
                    sourceName = f.Source.SourceName,
                    sourceLine = f.Source.Line,
                    sinkType = f.Sink.SinkType,
                    sinkName = f.Sink.SinkName,
                    sinkLine = f.Sink.Line,
                    taintedVariable = f.TaintedVariable,
                    isSanitized = f.IsSanitized,
                    sanitizerLocation = f.SanitizerLocation,
                    path = f.Path
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
    [Description("Analyze project trends over time using git history. Shows metric changes, regressions, and hotspots.")]
    public static async Task<string> AnalyzeTrends(
        [Description("Path to .csproj file or directory containing a C# project")]
        string projectPath,
        [Description("Number of recent commits to analyze. Default: 10")]
        int commitCount = 10)
    {
        try
        {
            var trendAnalyzer = new TrendAnalyzer(projectPath);
            var gitTrends = await trendAnalyzer.AnalyzeGitTrendsAsync(commitCount);
            var regressions = await trendAnalyzer.DetectRegressionsAsync();

            var result = new
            {
                gitAnalysis = new
                {
                    gitTrends.AnalyzedCommits,
                    gitTrends.TotalFilesChanged,
                    gitTrends.TotalAdditions,
                    gitTrends.TotalDeletions,
                    hotspots = gitTrends.Hotspots,
                    authorContributions = gitTrends.AuthorContributions
                },
                regressions = regressions.Select(r => new
                {
                    r.Type,
                    r.Severity,
                    r.Message,
                    r.BaselineValue,
                    r.CurrentValue,
                    r.BaselineCommit,
                    r.CurrentCommit
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
    [Description("Analyze C# code for concurrency and threading issues including floating tasks, async void, lock patterns, race conditions, and deadlock risks.")]
    public static async Task<string> AnalyzeConcurrency(
        [Description("Path to .csproj file or directory containing a C# project")]
        string projectPath)
    {
        try
        {
            var service = new AnalysisService();
            var project = await service.OpenProjectAsync(projectPath);

            var analyzer = new ConcurrencyAnalyzer();
            var result = await analyzer.AnalyzeProjectAsync(project);

            return JsonSerializer.Serialize(new
            {
                result.TotalIssues,
                result.CriticalCount,
                result.HighCount,
                result.MediumCount,
                result.IssuesByType,
                issues = result.Issues.Select(i => new
                {
                    i.IssueType,
                    i.Severity,
                    i.Message,
                    i.FilePath,
                    i.Line,
                    i.CodeSnippet,
                    i.SuggestedFix,
                    i.CweId
                }).ToList()
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    [McpServerTool]
    [Description("Analyze ASP.NET Core specific security issues including missing authorization, CSRF vulnerabilities, insecure CORS, mass assignment, and open redirects.")]
    public static async Task<string> AnalyzeAspNetCore(
        [Description("Path to .csproj file or directory containing a C# project")]
        string projectPath)
    {
        try
        {
            var service = new AnalysisService();
            var project = await service.OpenProjectAsync(projectPath);

            var analyzer = new AspNetCoreAnalyzer();
            var result = await analyzer.AnalyzeAsync(project);

            return JsonSerializer.Serialize(new
            {
                result.Framework,
                result.TotalIssues,
                result.CriticalCount,
                result.HighCount,
                result.MediumCount,
                result.IssuesByType,
                issues = result.Issues.Select(i => new
                {
                    i.IssueType,
                    i.Severity,
                    i.Message,
                    i.FilePath,
                    i.Line,
                    i.CweId,
                    i.SuggestedFix
                }).ToList()
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    [McpServerTool]
    [Description("Analyze Entity Framework Core specific issues including N+1 queries, missing AsNoTracking, Cartesian explosion, raw SQL injection, and lazy loading traps.")]
    public static async Task<string> AnalyzeEntityFramework(
        [Description("Path to .csproj file or directory containing a C# project")]
        string projectPath)
    {
        try
        {
            var service = new AnalysisService();
            var project = await service.OpenProjectAsync(projectPath);

            var analyzer = new EntityFrameworkAnalyzer();
            var result = await analyzer.AnalyzeAsync(project);

            return JsonSerializer.Serialize(new
            {
                result.Framework,
                result.TotalIssues,
                result.CriticalCount,
                result.HighCount,
                result.MediumCount,
                result.IssuesByType,
                issues = result.Issues.Select(i => new
                {
                    i.IssueType,
                    i.Severity,
                    i.Message,
                    i.FilePath,
                    i.Line,
                    i.CweId,
                    i.SuggestedFix
                }).ToList()
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    [McpServerTool]
    [Description("Analyze code quality including cognitive complexity, code smells, testability issues, error handling patterns, and design problems.")]
    public static async Task<string> AnalyzeCodeQuality(
        [Description("Path to .csproj file or directory containing a C# project")]
        string projectPath,
        [Description("Cognitive complexity threshold for methods. Default: 15")]
        int complexityThreshold = 15)
    {
        try
        {
            var service = new AnalysisService();
            var project = await service.OpenProjectAsync(projectPath);

            var analyzer = new CodeQualityAnalyzer();
            var result = await analyzer.AnalyzeAsync(project);

            // Filter by threshold
            var methodsAbove = result.MethodMetrics
                .Where(m => m.CognitiveComplexity > complexityThreshold)
                .OrderByDescending(m => m.CognitiveComplexity)
                .ToList();

            return JsonSerializer.Serialize(new
            {
                result.TotalIssues,
                result.IssuesByCategory,
                result.AverageCognitiveComplexity,
                methodsAboveThreshold = methodsAbove.Count,
                complexMethods = methodsAbove.Select(m => new
                {
                    m.MethodName,
                    m.FilePath,
                    m.Line,
                    m.CognitiveComplexity,
                    m.CyclomaticComplexity,
                    m.LineCount,
                    m.NestingDepth
                }).Take(20).ToList(),
                issues = result.Issues
                    .OrderByDescending(i => i.Severity == "High" ? 3 : i.Severity == "Medium" ? 2 : 1)
                    .Select(i => new
                    {
                        i.Category,
                        i.IssueType,
                        i.Severity,
                        i.Message,
                        i.FilePath,
                        i.Line,
                        i.Suggestion
                    }).ToList()
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    [McpServerTool]
    [Description("Calculate cognitive complexity for all methods in a project using Sonar's algorithm. Reports methods that exceed the threshold.")]
    public static async Task<string> AnalyzeCognitiveComplexity(
        [Description("Path to .csproj file or directory containing a C# project")]
        string projectPath,
        [Description("Complexity threshold. Methods above this will be flagged. Default: 15")]
        int threshold = 15)
    {
        try
        {
            var service = new AnalysisService();
            var project = await service.OpenProjectAsync(projectPath);

            var analyzer = new CodeQualityAnalyzer();
            var result = await analyzer.AnalyzeAsync(project);

            var metrics = result.MethodMetrics
                .OrderByDescending(m => m.CognitiveComplexity)
                .ToList();

            var aboveThreshold = metrics.Where(m => m.CognitiveComplexity > threshold).ToList();

            return JsonSerializer.Serialize(new
            {
                totalMethods = metrics.Count,
                averageComplexity = Math.Round(result.AverageCognitiveComplexity, 2),
                methodsAboveThreshold = aboveThreshold.Count,
                threshold,
                distribution = new
                {
                    low = metrics.Count(m => m.CognitiveComplexity <= 5),
                    moderate = metrics.Count(m => m.CognitiveComplexity > 5 && m.CognitiveComplexity <= 10),
                    high = metrics.Count(m => m.CognitiveComplexity > 10 && m.CognitiveComplexity <= 20),
                    veryHigh = metrics.Count(m => m.CognitiveComplexity > 20)
                },
                complexMethods = aboveThreshold.Select(m => new
                {
                    m.MethodName,
                    m.FilePath,
                    m.Line,
                    m.CognitiveComplexity,
                    m.CyclomaticComplexity,
                    m.LineCount,
                    m.NestingDepth,
                    m.ParameterCount
                }).Take(30).ToList()
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    [McpServerTool]
    [Description("Compare multiple optimization strategies in a virtual workspace without modifying actual files. Returns ranked results based on complexity, maintainability, and semantic safety.")]
    public static async Task<string> CompareOptimizationStrategies(
        [Description("Path to .csproj file or directory containing a C# project")]
        string projectPath,
        [Description("Specific file path to analyze. If not provided, analyzes first file with optimization opportunities.")]
        string? filePath = null,
        [Description("Maximum number of strategies to compare. Default: 5")]
        int maxStrategies = 5)
    {
        try
        {
            var service = new AnalysisService();
            var project = await service.OpenProjectAsync(projectPath);

            // Find a file with optimization opportunities
            Document? targetDoc = null;
            if (filePath != null)
            {
                targetDoc = project.Documents.FirstOrDefault(d =>
                    d.FilePath?.EndsWith(filePath, StringComparison.OrdinalIgnoreCase) == true);
            }
            else
            {
                // Find first file with potential optimizations
                foreach (var doc in project.Documents)
                {
                    var root = await doc.GetSyntaxRootAsync();
                    if (root?.DescendantNodes().Any() == true)
                    {
                        targetDoc = doc;
                        break;
                    }
                }
            }

            if (targetDoc == null)
            {
                return JsonSerializer.Serialize(new { error = "No suitable file found for analysis" }, JsonOptions);
            }

            using var workspace = new VirtualWorkspaceManager();
            workspace.LoadFromProject(project);

            // Create simple transformation strategies based on detected patterns
            var strategies = new List<ITransformationStrategy>();
            // Note: In a full implementation, we'd dynamically create strategies based on detected patterns

            var comparison = await workspace.CompareTransformationsAsync(targetDoc.Id, strategies);

            return JsonSerializer.Serialize(new
            {
                originalFile = targetDoc.FilePath,
                totalStrategies = comparison.Results.Count,
                failedStrategies = comparison.FailedResults.Count,
                results = comparison.Results.Select(r => new
                {
                    r.StrategyName,
                    r.Category,
                    r.Description,
                    score = new
                    {
                        overall = Math.Round(r.Score.OverallScore, 2),
                        complexityDelta = r.Score.ComplexityDelta,
                        cognitiveComplexityDelta = r.Score.CognitiveComplexityDelta,
                        locDelta = r.Score.LocDelta,
                        maintainabilityDelta = Math.Round(r.Score.MaintainabilityDelta, 2),
                        compilationValid = r.Score.CompilationValid,
                        semanticsPreserved = r.Score.SemanticsPreserved
                    },
                    diff = new
                    {
                        addedLines = r.Diff.AddedLines,
                        removedLines = r.Diff.RemovedLines,
                        modifiedRegions = r.Diff.ModifiedRegions
                    }
                }).ToList(),
                bestStrategy = comparison.BestResult != null ? new
                {
                    comparison.BestResult.StrategyName,
                    comparison.BestResult.Description,
                    score = Math.Round(comparison.BestResult.Score.OverallScore, 2)
                } : null
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    [McpServerTool]
    [Description("Run a comprehensive analysis including all detectors: security, concurrency, frameworks, code quality, and optimizations.")]
    public static async Task<string> RunFullAnalysis(
        [Description("Path to .csproj file or directory containing a C# project")]
        string projectPath)
    {
        try
        {
            var service = new AnalysisService();
            var project = await service.OpenProjectAsync(projectPath);

            // Run all analyzers in parallel
            var concurrencyTask = new ConcurrencyAnalyzer().AnalyzeProjectAsync(project);
            var aspNetTask = new AspNetCoreAnalyzer().AnalyzeAsync(project);
            var efTask = new EntityFrameworkAnalyzer().AnalyzeAsync(project);
            var qualityTask = new CodeQualityAnalyzer().AnalyzeAsync(project);

            var options = new AnalysisOptions
            {
                SecurityAnalysis = true,
                OptimizationAnalysis = true,
                DashboardAnalysis = true
            };
            var mainAnalysisTask = service.AnalyzeAsync(projectPath, options);

            await Task.WhenAll(concurrencyTask, aspNetTask, efTask, qualityTask, mainAnalysisTask);

            var concurrency = await concurrencyTask;
            var aspNet = await aspNetTask;
            var ef = await efTask;
            var quality = await qualityTask;
            var main = await mainAnalysisTask;

            return JsonSerializer.Serialize(new
            {
                projectPath,
                summary = new
                {
                    healthScore = main.Metrics?.HealthScore ?? 0,
                    totalIssues = concurrency.TotalIssues + aspNet.TotalIssues + ef.TotalIssues +
                                  quality.TotalIssues + (main.Security?.TotalVulnerabilities ?? 0),
                    criticalIssues = concurrency.CriticalCount + aspNet.CriticalCount + ef.CriticalCount +
                                     (main.Security?.CriticalCount ?? 0),
                    averageCognitiveComplexity = Math.Round(quality.AverageCognitiveComplexity, 2),
                    methodsAboveComplexityThreshold = quality.MethodsAboveThreshold,
                    optimizationOpportunities = main.Optimizations?.Summary.TotalOpportunities ?? 0
                },
                concurrency = new
                {
                    concurrency.TotalIssues,
                    concurrency.IssuesByType,
                    topIssues = concurrency.Issues.Take(10).ToList()
                },
                aspNetCore = new
                {
                    aspNet.TotalIssues,
                    aspNet.IssuesByType,
                    topIssues = aspNet.Issues.Take(10).ToList()
                },
                entityFramework = new
                {
                    ef.TotalIssues,
                    ef.IssuesByType,
                    topIssues = ef.Issues.Take(10).ToList()
                },
                codeQuality = new
                {
                    quality.TotalIssues,
                    quality.IssuesByCategory,
                    topIssues = quality.Issues.Take(10).ToList()
                },
                security = main.Security != null ? new
                {
                    main.Security.TotalVulnerabilities,
                    main.Security.VulnerabilitiesByType,
                    topVulnerabilities = main.Security.Vulnerabilities.Take(10).ToList()
                } : null
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }
}
