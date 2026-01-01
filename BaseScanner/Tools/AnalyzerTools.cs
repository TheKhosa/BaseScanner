using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using BaseScanner.Services;

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
}
