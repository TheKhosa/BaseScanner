using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using BaseScanner.Analyzers;
using BaseScanner.Analyzers.Security;
using BaseScanner.Analysis;
using BaseScanner.Services;
using BaseScanner.Transformers;
using BaseScanner.Transformers.Core;

namespace BaseScanner;

class Program
{
    static async Task<int> Main(string[] args)
    {
        // MCP Server mode
        if (args.Contains("--mcp", StringComparer.OrdinalIgnoreCase))
        {
            AnalysisService.EnsureMSBuildRegistered();
            var builder = Host.CreateApplicationBuilder(args);
            builder.Services
                .AddMcpServer()
                .WithStdioServerTransport()
                .WithToolsFromAssembly();
            await builder.Build().RunAsync();
            return 0;
        }

        // CLI mode
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: BaseScanner <project-path> [options]");
            Console.WriteLine("  <project-path>  Path to a .csproj file or directory containing one");
            Console.WriteLine();
            Console.WriteLine("Analysis Options:");
            Console.WriteLine("  --deep          Usage counting, deprecated code detection");
            Console.WriteLine("  --sentiment     Code quality, complexity, duplicates");
            Console.WriteLine("  --perf          Async issues, performance anti-patterns");
            Console.WriteLine("  --exceptions    Exception handling issues");
            Console.WriteLine("  --resources     Resource leaks, IDisposable issues");
            Console.WriteLine("  --deps          Circular dependencies, coupling metrics");
            Console.WriteLine("  --magic         Magic numbers and strings");
            Console.WriteLine("  --git           Git churn analysis, hotspots");
            Console.WriteLine("  --refactor      Refactoring opportunities (long methods, god classes)");
            Console.WriteLine("  --arch          Architecture analysis (API surface, call graph)");
            Console.WriteLine("  --safety        Code safety (null safety, immutability, logging)");
            Console.WriteLine("  --optimize      Optimization opportunities with code suggestions");
            Console.WriteLine("  --security      Security vulnerability analysis (CWE references)");
            Console.WriteLine("  --dashboard     Project health metrics dashboard");
            Console.WriteLine("  --trends        Trend analysis using git history");
            Console.WriteLine("  --all           Run all analyses");
            Console.WriteLine();
            Console.WriteLine("Transformation Options:");
            Console.WriteLine("  --apply         Apply optimizations (use with --preview for dry run)");
            Console.WriteLine("  --preview       Preview transformations without applying");
            Console.WriteLine("  --category=X    Filter by category: performance, readability, modernization");
            Console.WriteLine("  --confidence=X  Minimum confidence: high, medium, low (default: high)");
            Console.WriteLine("  --rollback      Rollback to previous backup");
            Console.WriteLine("  --list-backups  List available transformation backups");
            Console.WriteLine();
            Console.WriteLine("Refactoring Options:");
            Console.WriteLine("  --refactor-analyze        Analyze refactoring opportunities with LCOM4");
            Console.WriteLine("  --refactor-preview        Preview refactoring strategies for a target");
            Console.WriteLine("  --refactor-apply          Apply best refactoring strategy");
            Console.WriteLine("  --refactor-chain          Apply chain of strategies for god class remediation");
            Console.WriteLine("  --file=X                  Target file path for refactoring");
            Console.WriteLine("  --target=X                Target class/method name");
            Console.WriteLine("  --strategy=X              Specific strategy: SimplifyMethod, ExtractMethod,");
            Console.WriteLine("                            ExtractClass, SplitGodClass, ExtractInterface,");
            Console.WriteLine("                            ReplaceConditional");
            Console.WriteLine("  --chain-type=X            Chain type: godclass, longmethod, testability, complexity");
            Console.WriteLine();
            Console.WriteLine("Server Mode:");
            Console.WriteLine("  --mcp           Run as MCP server for Claude Code integration");
            return 1;
        }

        var projectPath = args[0];
        var runAll = args.Contains("--all", StringComparer.OrdinalIgnoreCase);

        // Handle transformation commands first
        if (args.Contains("--rollback", StringComparer.OrdinalIgnoreCase))
        {
            return await HandleRollback(projectPath);
        }

        if (args.Contains("--list-backups", StringComparer.OrdinalIgnoreCase))
        {
            return await HandleListBackups(projectPath);
        }

        if (args.Contains("--apply", StringComparer.OrdinalIgnoreCase) || args.Contains("--preview", StringComparer.OrdinalIgnoreCase))
        {
            var isPreview = args.Contains("--preview", StringComparer.OrdinalIgnoreCase);
            var category = GetArgValue(args, "--category") ?? "all";
            var confidence = GetArgValue(args, "--confidence") ?? "high";
            return await HandleTransformations(projectPath, isPreview, category, confidence);
        }

        // Handle refactoring commands
        if (args.Contains("--refactor-analyze", StringComparer.OrdinalIgnoreCase))
        {
            var severity = GetArgValue(args, "--severity") ?? "medium";
            return await HandleRefactorAnalyze(projectPath, severity);
        }

        if (args.Contains("--refactor-preview", StringComparer.OrdinalIgnoreCase))
        {
            var file = GetArgValue(args, "--file");
            var target = GetArgValue(args, "--target");
            if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(target))
            {
                Console.WriteLine("Error: --refactor-preview requires --file and --target");
                return 1;
            }
            return await HandleRefactorPreview(projectPath, file, target);
        }

        if (args.Contains("--refactor-apply", StringComparer.OrdinalIgnoreCase))
        {
            var file = GetArgValue(args, "--file");
            var target = GetArgValue(args, "--target");
            var strategy = GetArgValue(args, "--strategy");
            if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(target))
            {
                Console.WriteLine("Error: --refactor-apply requires --file and --target");
                return 1;
            }
            return await HandleRefactorApply(projectPath, file, target, strategy);
        }

        if (args.Contains("--refactor-chain", StringComparer.OrdinalIgnoreCase))
        {
            var file = GetArgValue(args, "--file");
            var target = GetArgValue(args, "--target");
            var chainType = GetArgValue(args, "--chain-type") ?? "godclass";
            if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(target))
            {
                Console.WriteLine("Error: --refactor-chain requires --file and --target");
                return 1;
            }
            return await HandleRefactorChain(projectPath, file, target, chainType);
        }

        var options = new CliAnalysisOptions
        {
            DeepAnalysis = runAll || args.Contains("--deep", StringComparer.OrdinalIgnoreCase),
            SentimentAnalysis = runAll || args.Contains("--sentiment", StringComparer.OrdinalIgnoreCase),
            PerformanceAnalysis = runAll || args.Contains("--perf", StringComparer.OrdinalIgnoreCase),
            ExceptionAnalysis = runAll || args.Contains("--exceptions", StringComparer.OrdinalIgnoreCase),
            ResourceAnalysis = runAll || args.Contains("--resources", StringComparer.OrdinalIgnoreCase),
            DependencyAnalysis = runAll || args.Contains("--deps", StringComparer.OrdinalIgnoreCase),
            MagicValueAnalysis = runAll || args.Contains("--magic", StringComparer.OrdinalIgnoreCase),
            GitAnalysis = runAll || args.Contains("--git", StringComparer.OrdinalIgnoreCase),
            RefactoringAnalysis = runAll || args.Contains("--refactor", StringComparer.OrdinalIgnoreCase),
            ArchitectureAnalysis = runAll || args.Contains("--arch", StringComparer.OrdinalIgnoreCase),
            SafetyAnalysis = runAll || args.Contains("--safety", StringComparer.OrdinalIgnoreCase),
            OptimizationAnalysis = runAll || args.Contains("--optimize", StringComparer.OrdinalIgnoreCase),
            SecurityAnalysis = runAll || args.Contains("--security", StringComparer.OrdinalIgnoreCase),
            DashboardAnalysis = args.Contains("--dashboard", StringComparer.OrdinalIgnoreCase),
            TrendAnalysis = args.Contains("--trends", StringComparer.OrdinalIgnoreCase)
        };

        // If directory provided, look for .csproj file
        if (Directory.Exists(projectPath))
        {
            var csprojFiles = Directory.GetFiles(projectPath, "*.csproj");
            if (csprojFiles.Length == 0)
            {
                Console.WriteLine($"No .csproj file found in: {projectPath}");
                return 1;
            }
            if (csprojFiles.Length > 1)
            {
                Console.WriteLine($"Multiple .csproj files found. Please specify one:");
                foreach (var file in csprojFiles)
                    Console.WriteLine($"  {file}");
                return 1;
            }
            projectPath = csprojFiles[0];
        }

        if (!File.Exists(projectPath))
        {
            Console.WriteLine($"Project file not found: {projectPath}");
            return 1;
        }

        Console.WriteLine($"Analyzing project: {projectPath}");
        var modes = options.GetEnabledModes();
        if (modes.Count > 0)
            Console.WriteLine($"Analysis modes: {string.Join(", ", modes)}");
        Console.WriteLine();

        try
        {
            MSBuildLocator.RegisterDefaults();
            await AnalyzeProject(projectPath, options);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 1;
        }

        return 0;
    }

    record CliAnalysisOptions
    {
        public bool DeepAnalysis { get; init; }
        public bool SentimentAnalysis { get; init; }
        public bool PerformanceAnalysis { get; init; }
        public bool ExceptionAnalysis { get; init; }
        public bool ResourceAnalysis { get; init; }
        public bool DependencyAnalysis { get; init; }
        public bool MagicValueAnalysis { get; init; }
        public bool GitAnalysis { get; init; }
        public bool RefactoringAnalysis { get; init; }
        public bool ArchitectureAnalysis { get; init; }
        public bool SafetyAnalysis { get; init; }
        public bool OptimizationAnalysis { get; init; }
        public bool SecurityAnalysis { get; init; }
        public bool DashboardAnalysis { get; init; }
        public bool TrendAnalysis { get; init; }

        public List<string> GetEnabledModes()
        {
            var modes = new List<string>();
            if (DeepAnalysis) modes.Add("deep");
            if (SentimentAnalysis) modes.Add("sentiment");
            if (PerformanceAnalysis) modes.Add("perf");
            if (ExceptionAnalysis) modes.Add("exceptions");
            if (ResourceAnalysis) modes.Add("resources");
            if (DependencyAnalysis) modes.Add("deps");
            if (MagicValueAnalysis) modes.Add("magic");
            if (GitAnalysis) modes.Add("git");
            if (RefactoringAnalysis) modes.Add("refactor");
            if (ArchitectureAnalysis) modes.Add("arch");
            if (SafetyAnalysis) modes.Add("safety");
            if (OptimizationAnalysis) modes.Add("optimize");
            if (SecurityAnalysis) modes.Add("security");
            if (DashboardAnalysis) modes.Add("dashboard");
            if (TrendAnalysis) modes.Add("trends");
            return modes;
        }
    }

    static string? GetArgValue(string[] args, string prefix)
    {
        var arg = args.FirstOrDefault(a => a.StartsWith(prefix + "=", StringComparison.OrdinalIgnoreCase));
        return arg?.Substring(prefix.Length + 1);
    }

    static async Task<int> HandleRollback(string projectPath)
    {
        Console.WriteLine("Rolling back transformations...");
        try
        {
            MSBuildLocator.RegisterDefaults();
            var backupService = new BackupService(projectPath);
            var service = new TransformationService(backupService);
            var result = await service.RollbackAsync(null);

            if (result.Success)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"Successfully restored {result.FilesRestored} files from backup {result.BackupId}");
                Console.ResetColor();
                return 0;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Rollback failed: {result.ErrorMessage}");
                Console.ResetColor();
                return 1;
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: {ex.Message}");
            Console.ResetColor();
            return 1;
        }
    }

    static async Task<int> HandleListBackups(string projectPath)
    {
        try
        {
            var backupService = new BackupService(projectPath);
            var backups = await backupService.ListBackupsAsync();

            if (backups.Count == 0)
            {
                Console.WriteLine("No backups found.");
                return 0;
            }

            Console.WriteLine($"Available backups ({backups.Count}):");
            Console.WriteLine();
            foreach (var backup in backups.OrderByDescending(b => b.CreatedAt))
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write($"  {backup.Id}");
                Console.ResetColor();
                Console.WriteLine($"  {backup.CreatedAt:yyyy-MM-dd HH:mm:ss}  ({backup.FileCount} files)");
                if (!string.IsNullOrEmpty(backup.Description))
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"    {backup.Description}");
                    Console.ResetColor();
                }
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: {ex.Message}");
            Console.ResetColor();
            return 1;
        }
    }

    static async Task<int> HandleTransformations(string projectPath, bool isPreview, string category, string confidence)
    {
        try
        {
            MSBuildLocator.RegisterDefaults();

            var filter = new TransformationFilter
            {
                Categories = category == "all" ? [] : [category],
                MinConfidence = confidence,
                MaxTransformations = 100
            };

            var analysisService = new AnalysisService();
            var project = await analysisService.OpenProjectAsync(projectPath);
            var backupService = new BackupService(projectPath);
            var service = new TransformationService(backupService);

            if (isPreview)
            {
                Console.WriteLine("Previewing transformations...");
                Console.WriteLine();

                var preview = await service.PreviewAsync(project, filter);

                if (preview.Previews.Count == 0)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("No transformations match the specified criteria.");
                    Console.ResetColor();
                    return 0;
                }

                Console.WriteLine($"Found {preview.TotalTransformations} transformations:");
                Console.WriteLine();

                var grouped = preview.Previews.GroupBy(t => t.Category);
                foreach (var group in grouped)
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"  {group.Key.ToUpperInvariant()} ({group.Count()}):");
                    Console.ResetColor();

                    foreach (var t in group.Take(10))
                    {
                        var confidenceColor = t.Confidence switch
                        {
                            "High" => ConsoleColor.Green,
                            "Medium" => ConsoleColor.Yellow,
                            _ => ConsoleColor.DarkGray
                        };

                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.Write($"    {Path.GetFileName(t.FilePath)}:{t.StartLine} ");
                        Console.ForegroundColor = confidenceColor;
                        Console.Write($"[{t.Confidence}] ");
                        Console.ResetColor();
                        Console.WriteLine(t.TransformationType);

                        Console.ForegroundColor = ConsoleColor.DarkRed;
                        Console.WriteLine($"      - {t.OriginalCode.Trim().Replace("\n", " ").Replace("\r", "")}");
                        Console.ForegroundColor = ConsoleColor.DarkGreen;
                        Console.WriteLine($"      + {t.SuggestedCode.Trim().Replace("\n", " ").Replace("\r", "")}");
                        Console.ResetColor();
                    }

                    if (group.Count() > 10)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.WriteLine($"    ... and {group.Count() - 10} more");
                        Console.ResetColor();
                    }
                }

                Console.WriteLine();
                Console.WriteLine($"Run with --apply to apply these transformations.");
                return 0;
            }
            else
            {
                Console.WriteLine("Applying transformations...");
                Console.WriteLine();

                var options = new TransformationOptions
                {
                    FormatOutput = true
                };

                var result = await service.ApplyAsync(project, filter, options);

                if (result.Success)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"Successfully applied {result.TotalTransformations} transformations to {result.FilesModified} files.");
                    Console.ResetColor();

                    if (!string.IsNullOrEmpty(result.BackupId))
                    {
                        Console.WriteLine($"Backup created: {result.BackupId}");
                        Console.WriteLine("Run with --rollback to undo changes.");
                    }
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"Transformations completed with issues.");
                    Console.ResetColor();

                    if (!string.IsNullOrEmpty(result.ErrorMessage))
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"Error: {result.ErrorMessage}");
                        Console.ResetColor();
                    }
                }

                return result.Success ? 0 : 1;
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: {ex.Message}");
            Console.ResetColor();
            return 1;
        }
    }

    static async Task AnalyzeProject(string projectPath, CliAnalysisOptions options)
    {
        var projectDirectory = Path.GetDirectoryName(projectPath)!;

        // Get all .cs files on disk in the project directory
        var allCsFilesOnDisk = Directory.GetFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetFullPath(f))
            .Where(f => !f.Contains(Path.Combine(projectDirectory, "obj") + Path.DirectorySeparatorChar) &&
                        !f.Contains(Path.Combine(projectDirectory, "bin") + Path.DirectorySeparatorChar))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Console.WriteLine($"Found {allCsFilesOnDisk.Count} .cs files on disk (excluding bin/obj)");

        using var workspace = MSBuildWorkspace.Create();

#pragma warning disable CS0618
        workspace.WorkspaceFailed += (sender, e) =>
        {
            if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
                Console.WriteLine($"  Warning: {e.Diagnostic.Message}");
        };
#pragma warning restore CS0618

        Console.WriteLine("Loading project...");
        var project = await workspace.OpenProjectAsync(projectPath);

        var compiledFiles = project.Documents
            .Where(d => d.FilePath != null)
            .Select(d => Path.GetFullPath(d.FilePath!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Console.WriteLine($"Found {compiledFiles.Count} files in project compilation");
        Console.WriteLine();

        // Basic file analysis
        AnalyzeUnusedFiles(allCsFilesOnDisk, compiledFiles, projectDirectory);

        // Deep analysis
        if (options.DeepAnalysis)
        {
            Console.WriteLine();
            Console.WriteLine("Performing deep analysis...");
            Console.WriteLine();

            var compilation = await project.GetCompilationAsync();
            if (compilation == null)
            {
                Console.WriteLine("Failed to get compilation");
                return;
            }

            await AnalyzeDeprecatedCode(project, compilation, projectDirectory);
            await AnalyzeUsageCounts(project, compilation, projectDirectory);
        }

        // Sentiment analysis
        if (options.SentimentAnalysis)
        {
            Console.WriteLine();
            Console.WriteLine("Performing sentiment analysis...");
            Console.WriteLine();

            await AnalyzeSentiment(project, projectDirectory);
        }

        // Performance analysis
        if (options.PerformanceAnalysis)
        {
            Console.WriteLine();
            await AnalyzePerformance(project, projectDirectory);
        }

        // Exception handling analysis
        if (options.ExceptionAnalysis)
        {
            Console.WriteLine();
            await AnalyzeExceptionHandling(project, projectDirectory);
        }

        // Resource leak analysis
        if (options.ResourceAnalysis)
        {
            Console.WriteLine();
            await AnalyzeResourceLeaks(project, projectDirectory);
        }

        // Dependency analysis
        if (options.DependencyAnalysis)
        {
            Console.WriteLine();
            await AnalyzeDependencies(project, projectDirectory);
        }

        // Magic value analysis
        if (options.MagicValueAnalysis)
        {
            Console.WriteLine();
            await AnalyzeMagicValues(project, projectDirectory);
        }

        // Git churn analysis
        if (options.GitAnalysis)
        {
            Console.WriteLine();
            await AnalyzeGitChurn(projectDirectory);
        }

        // Refactoring analysis
        if (options.RefactoringAnalysis)
        {
            Console.WriteLine();
            await AnalyzeRefactoring(project, projectDirectory);
        }

        // Architecture analysis
        if (options.ArchitectureAnalysis)
        {
            Console.WriteLine();
            await AnalyzeArchitecture(project, projectDirectory);
        }

        // Safety analysis
        if (options.SafetyAnalysis)
        {
            Console.WriteLine();
            await AnalyzeSafety(project, projectDirectory);
        }

        // Optimization analysis
        if (options.OptimizationAnalysis)
        {
            Console.WriteLine();
            await AnalyzeOptimizations(project, projectDirectory);
        }

        // Security analysis
        if (options.SecurityAnalysis)
        {
            Console.WriteLine();
            await AnalyzeSecurityVulnerabilities(project, projectDirectory);
        }

        // Dashboard analysis
        if (options.DashboardAnalysis)
        {
            Console.WriteLine();
            await ShowDashboard(project, projectDirectory);
        }

        // Trend analysis
        if (options.TrendAnalysis)
        {
            Console.WriteLine();
            await AnalyzeTrends(projectDirectory);
        }
    }

    static async Task AnalyzePerformance(Project project, string projectDirectory)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("=== ASYNC/PERFORMANCE ANALYSIS ===");
        Console.ResetColor();
        Console.WriteLine();

        var analyzer = new AsyncPerformanceAnalyzer();
        var issues = await analyzer.AnalyzeAsync(project);

        if (issues.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  No async/performance issues found.");
            Console.ResetColor();
            return;
        }

        var grouped = issues.GroupBy(i => i.Type).OrderByDescending(g => g.Max(i => i.Severity == "Critical" ? 2 : i.Severity == "Warning" ? 1 : 0));

        foreach (var group in grouped)
        {
            var severity = group.First().Severity;
            Console.ForegroundColor = severity == "Critical" ? ConsoleColor.Red : severity == "Warning" ? ConsoleColor.Yellow : ConsoleColor.Gray;
            Console.WriteLine($"  {group.Key} ({group.Count()}):");
            Console.ResetColor();

            foreach (var issue in group.Take(10))
            {
                var relativePath = Path.GetRelativePath(projectDirectory, issue.FilePath);
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"    {relativePath}:{issue.Line}");
                Console.ResetColor();
                Console.WriteLine($"      {issue.Message}");
            }
            if (group.Count() > 10)
                Console.WriteLine($"      ... and {group.Count() - 10} more");
            Console.WriteLine();
        }

        Console.WriteLine($"  Total issues: {issues.Count} (Critical: {issues.Count(i => i.Severity == "Critical")}, Warning: {issues.Count(i => i.Severity == "Warning")})");
    }

    static async Task AnalyzeExceptionHandling(Project project, string projectDirectory)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("=== EXCEPTION HANDLING ANALYSIS ===");
        Console.ResetColor();
        Console.WriteLine();

        var analyzer = new ExceptionHandlingAnalyzer();
        var issues = await analyzer.AnalyzeAsync(project);

        if (issues.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  No exception handling issues found.");
            Console.ResetColor();
            return;
        }

        var grouped = issues.GroupBy(i => i.Type).OrderByDescending(g => g.Max(i => i.Severity == "Critical" ? 2 : 1));

        foreach (var group in grouped)
        {
            var severity = group.First().Severity;
            Console.ForegroundColor = severity == "Critical" ? ConsoleColor.Red : ConsoleColor.Yellow;
            Console.WriteLine($"  {group.Key} ({group.Count()}):");
            Console.ResetColor();

            foreach (var issue in group.Take(10))
            {
                var relativePath = Path.GetRelativePath(projectDirectory, issue.FilePath);
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"    {relativePath}:{issue.Line}");
                Console.ResetColor();
                Console.WriteLine($"      {issue.Message}");
            }
            if (group.Count() > 10)
                Console.WriteLine($"      ... and {group.Count() - 10} more");
            Console.WriteLine();
        }

        Console.WriteLine($"  Total issues: {issues.Count}");
    }

    static async Task AnalyzeResourceLeaks(Project project, string projectDirectory)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("=== RESOURCE LEAK ANALYSIS ===");
        Console.ResetColor();
        Console.WriteLine();

        var analyzer = new ResourceLeakAnalyzer();
        var issues = await analyzer.AnalyzeAsync(project);

        if (issues.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  No resource leak issues found.");
            Console.ResetColor();
            return;
        }

        var grouped = issues.GroupBy(i => i.Type);

        foreach (var group in grouped)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  {group.Key} ({group.Count()}):");
            Console.ResetColor();

            foreach (var issue in group.Take(10))
            {
                var relativePath = Path.GetRelativePath(projectDirectory, issue.FilePath);
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"    {relativePath}:{issue.Line}");
                Console.ResetColor();
                Console.WriteLine($"      {issue.Message}");
            }
            if (group.Count() > 10)
                Console.WriteLine($"      ... and {group.Count() - 10} more");
            Console.WriteLine();
        }

        Console.WriteLine($"  Total issues: {issues.Count}");
    }

    static async Task AnalyzeDependencies(Project project, string projectDirectory)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("=== DEPENDENCY ANALYSIS ===");
        Console.ResetColor();
        Console.WriteLine();

        var analyzer = new DependencyAnalyzer();
        var (issues, metrics, cycles) = await analyzer.AnalyzeAsync(project);

        // Report circular dependencies
        var nsCycles = cycles.Where(c => c.Type == "Namespace").ToList();
        if (nsCycles.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  CIRCULAR NAMESPACE DEPENDENCIES ({nsCycles.Count}):");
            Console.ResetColor();
            foreach (var cycle in nsCycles.Take(5))
            {
                Console.WriteLine($"    {string.Join(" -> ", cycle.Cycle)}");
            }
            Console.WriteLine();
        }

        var typeCycles = cycles.Where(c => c.Type == "Type").ToList();
        if (typeCycles.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  CIRCULAR TYPE DEPENDENCIES ({typeCycles.Count}):");
            Console.ResetColor();
            foreach (var cycle in typeCycles.Take(5))
            {
                Console.WriteLine($"    {string.Join(" -> ", cycle.Cycle.Select(t => t.Split('.').Last()))}");
            }
            Console.WriteLine();
        }

        // Report high coupling
        var highCoupling = metrics.Where(m => m.EfferentCoupling > 10).OrderByDescending(m => m.EfferentCoupling).Take(15).ToList();
        if (highCoupling.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  HIGH COUPLING TYPES (top {highCoupling.Count}):");
            Console.ResetColor();
            foreach (var metric in highCoupling)
            {
                var relativePath = !string.IsNullOrEmpty(metric.FilePath) ? Path.GetRelativePath(projectDirectory, metric.FilePath) : "";
                Console.WriteLine($"    {metric.TypeName}: Ce={metric.EfferentCoupling}, Ca={metric.AfferentCoupling}, I={metric.Instability:F2}");
            }
            Console.WriteLine();
        }

        Console.WriteLine($"  Summary: {cycles.Count} cycles, {metrics.Count(m => m.EfferentCoupling > 10)} high coupling types");
    }

    static async Task AnalyzeMagicValues(Project project, string projectDirectory)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("=== MAGIC VALUE ANALYSIS ===");
        Console.ResetColor();
        Console.WriteLine();

        var analyzer = new MagicValueAnalyzer();
        var issues = await analyzer.AnalyzeAsync(project);

        if (issues.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  No significant magic values found.");
            Console.ResetColor();
            return;
        }

        var numbers = issues.Where(i => i.Type == "MagicNumber").Take(15).ToList();
        var strings = issues.Where(i => i.Type == "MagicString").Take(15).ToList();

        if (numbers.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  MAGIC NUMBERS ({numbers.Count} shown):");
            Console.ResetColor();
            foreach (var issue in numbers)
            {
                Console.WriteLine($"    '{issue.Value}' appears {issue.Occurrences}x - {issue.CodeSnippet}");
            }
            Console.WriteLine();
        }

        if (strings.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  MAGIC STRINGS ({strings.Count} shown):");
            Console.ResetColor();
            foreach (var issue in strings)
            {
                var displayVal = issue.Value.Length > 25 ? issue.Value.Substring(0, 25) + "..." : issue.Value;
                Console.WriteLine($"    \"{displayVal}\" appears {issue.Occurrences}x");
            }
            Console.WriteLine();
        }

        Console.WriteLine($"  Total: {issues.Count(i => i.Type == "MagicNumber")} magic numbers, {issues.Count(i => i.Type == "MagicString")} magic strings");
    }

    static async Task AnalyzeGitChurn(string projectDirectory)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("=== GIT CHURN ANALYSIS ===");
        Console.ResetColor();
        Console.WriteLine();

        var analyzer = new GitChurnAnalyzer();
        var (issues, churns, hotspots, gitAvailable) = await analyzer.AnalyzeAsync(projectDirectory);

        if (!gitAvailable)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  Git repository not found or git not available. Skipping churn analysis.");
            Console.ResetColor();
            return;
        }

        if (churns.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  No churn data available.");
            Console.ResetColor();
            return;
        }

        // Top churned files
        var topChurn = churns.OrderByDescending(c => c.CommitCount).Take(15).ToList();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  MOST FREQUENTLY CHANGED FILES (top {topChurn.Count}):");
        Console.ResetColor();
        foreach (var churn in topChurn)
        {
            Console.WriteLine($"    {churn.CommitCount,3} commits: {churn.RelativePath}");
        }
        Console.WriteLine();

        // Hotspots
        if (hotspots.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  HOTSPOTS ({hotspots.Count}):");
            Console.ResetColor();
            foreach (var hotspot in hotspots.Take(10))
            {
                Console.WriteLine($"    {hotspot.FilePath} - {hotspot.Reason}");
            }
            Console.WriteLine();
        }

        // Stale files
        var stale = churns.Where(c => c.DaysSinceLastChange > 365).OrderByDescending(c => c.DaysSinceLastChange).Take(10).ToList();
        if (stale.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  POTENTIALLY STALE CODE (not modified in 1+ year):");
            Console.ResetColor();
            foreach (var file in stale)
            {
                Console.WriteLine($"    {file.DaysSinceLastChange / 365}y ago: {file.RelativePath}");
            }
            Console.WriteLine();
        }

        Console.WriteLine($"  Summary: {churns.Count} files tracked, {hotspots.Count} hotspots, {stale.Count} potentially stale");
    }

    static async Task AnalyzeSentiment(Project project, string projectDirectory)
    {
        var analyzer = new CodeSentimentAnalyzer();
        var blocks = await analyzer.AnalyzeProjectAsync(project);

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("=== CODE SENTIMENT ANALYSIS ===");
        Console.ResetColor();
        Console.WriteLine();

        // Overall statistics
        Console.WriteLine($"  Analyzed {blocks.Count} code blocks");
        Console.WriteLine();

        // Quality distribution
        var qualityGroups = blocks.GroupBy(b => b.QualityRating).OrderByDescending(g => g.Key);
        Console.WriteLine("  Quality Distribution:");
        foreach (var group in qualityGroups)
        {
            var color = group.Key switch
            {
                "Excellent" => ConsoleColor.Green,
                "Good" => ConsoleColor.DarkGreen,
                "Acceptable" => ConsoleColor.Yellow,
                "Needs Improvement" => ConsoleColor.DarkYellow,
                "Poor" => ConsoleColor.Red,
                "Critical" => ConsoleColor.DarkRed,
                _ => ConsoleColor.Gray
            };
            Console.ForegroundColor = color;
            Console.WriteLine($"    {group.Key}: {group.Count()} ({100.0 * group.Count() / blocks.Count:F1}%)");
            Console.ResetColor();
        }
        Console.WriteLine();

        // Problematic code (Critical and Poor)
        var problematic = blocks.Where(b => b.QualityScore < 40).OrderBy(b => b.QualityScore).Take(20).ToList();
        if (problematic.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  PROBLEMATIC CODE (lowest quality, showing top {problematic.Count}):");
            Console.ResetColor();
            Console.WriteLine();

            foreach (var block in problematic)
            {
                var relativePath = Path.GetRelativePath(projectDirectory, block.FilePath);
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write($"    {relativePath}");
                Console.ResetColor();
                Console.WriteLine($":{block.StartLine}");

                Console.ForegroundColor = ConsoleColor.DarkRed;
                Console.Write($"      [{block.BlockType}] ");
                Console.ResetColor();
                Console.Write($"{block.ContainingType}.{block.Name}");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($" (Score: {block.QualityScore}, {block.QualityRating})");
                Console.ResetColor();

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"        Complexity: {block.CyclomaticComplexity}, Nesting: {block.NestingDepth}, Lines: {block.LineCount}, Params: {block.ParameterCount}");
                Console.ResetColor();

                if (block.SentimentMarkers.Count > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"        Markers: {string.Join(", ", block.SentimentMarkers)}");
                    Console.ResetColor();
                }
            }
            Console.WriteLine();
        }

        // High complexity code
        var highComplexity = blocks.Where(b => b.CyclomaticComplexity > 15).OrderByDescending(b => b.CyclomaticComplexity).Take(15).ToList();
        if (highComplexity.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  HIGH COMPLEXITY CODE (CC > 15, showing top {highComplexity.Count}):");
            Console.ResetColor();
            Console.WriteLine();

            foreach (var block in highComplexity)
            {
                var relativePath = Path.GetRelativePath(projectDirectory, block.FilePath);
                Console.Write($"    ");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write($"CC={block.CyclomaticComplexity} ");
                Console.ResetColor();
                Console.Write($"{block.ContainingType}.{block.Name}");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($" ({relativePath}:{block.StartLine})");
                Console.ResetColor();
            }
            Console.WriteLine();
        }

        // Code with sentiment markers
        var withMarkers = blocks.Where(b => b.SentimentMarkers.Count > 0).ToList();
        if (withMarkers.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  CODE WITH SENTIMENT MARKERS ({withMarkers.Count} blocks):");
            Console.ResetColor();
            Console.WriteLine();

            var markerCounts = withMarkers.SelectMany(b => b.SentimentMarkers).GroupBy(m => m).OrderByDescending(g => g.Count());
            Console.WriteLine("    Marker summary:");
            foreach (var marker in markerCounts)
            {
                var color = marker.Key switch
                {
                    "HACK" or "FIXME" or "WARNING" => ConsoleColor.Red,
                    "TODO" => ConsoleColor.Yellow,
                    "WORKAROUND" or "TEMPORARY" => ConsoleColor.DarkYellow,
                    "DEPRECATED" => ConsoleColor.Magenta,
                    _ => ConsoleColor.Gray
                };
                Console.ForegroundColor = color;
                Console.WriteLine($"      {marker.Key}: {marker.Count()}");
                Console.ResetColor();
            }
            Console.WriteLine();

            // Show top items with HACK/FIXME
            var urgent = withMarkers.Where(b => b.SentimentMarkers.Any(m => m is "HACK" or "FIXME" or "WARNING")).Take(10).ToList();
            if (urgent.Count > 0)
            {
                Console.WriteLine("    Urgent attention needed:");
                foreach (var block in urgent)
                {
                    var relativePath = Path.GetRelativePath(projectDirectory, block.FilePath);
                    Console.Write($"      ");
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Write($"[{string.Join(",", block.SentimentMarkers.Where(m => m is "HACK" or "FIXME" or "WARNING"))}] ");
                    Console.ResetColor();
                    Console.WriteLine($"{block.ContainingType}.{block.Name} ({relativePath}:{block.StartLine})");
                }
                Console.WriteLine();
            }
        }

        // Similar/duplicate code
        var similarGroups = analyzer.FindSimilarBlocks(blocks);
        if (similarGroups.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"  SIMILAR/DUPLICATE CODE ({similarGroups.Count} groups found):");
            Console.ResetColor();
            Console.WriteLine();

            foreach (var group in similarGroups.Take(10))
            {
                var similarity = group.SimilarityScore >= 1.0 ? "EXACT DUPLICATE" : "Structurally similar";
                Console.ForegroundColor = group.SimilarityScore >= 1.0 ? ConsoleColor.Red : ConsoleColor.Yellow;
                Console.WriteLine($"    {similarity} ({group.Blocks.Count} occurrences):");
                Console.ResetColor();

                foreach (var block in group.Blocks.Take(5))
                {
                    var relativePath = Path.GetRelativePath(projectDirectory, block.FilePath);
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"      - {block.ContainingType}.{block.Name} ({relativePath}:{block.StartLine}, {block.LineCount} lines)");
                    Console.ResetColor();
                }
                if (group.Blocks.Count > 5)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"      ... and {group.Blocks.Count - 5} more");
                    Console.ResetColor();
                }
                Console.WriteLine();
            }
        }

        // Summary
        Console.WriteLine("  === SENTIMENT SUMMARY ===");
        Console.WriteLine($"    Total code blocks:     {blocks.Count}");
        Console.WriteLine($"    Average quality score: {blocks.Average(b => b.QualityScore):F1}");
        Console.WriteLine($"    Avg complexity (CC):   {blocks.Average(b => b.CyclomaticComplexity):F1}");
        Console.WriteLine($"    High complexity (>15): {blocks.Count(b => b.CyclomaticComplexity > 15)}");
        Console.WriteLine($"    Problematic (<40):     {blocks.Count(b => b.QualityScore < 40)}");
        Console.WriteLine($"    With markers:          {withMarkers.Count}");
        Console.WriteLine($"    Duplicate groups:      {similarGroups.Count(g => g.SimilarityScore >= 1.0)}");
        Console.WriteLine($"    Similar groups:        {similarGroups.Count(g => g.SimilarityScore < 1.0)}");
    }

    static void AnalyzeUnusedFiles(HashSet<string> allCsFilesOnDisk, HashSet<string> compiledFiles, string projectDirectory)
    {
        var unusedFiles = allCsFilesOnDisk
            .Where(f => !compiledFiles.Contains(f))
            .OrderBy(f => f)
            .ToList();

        var missingFiles = compiledFiles
            .Where(f => !File.Exists(f))
            .OrderBy(f => f)
            .ToList();

        if (unusedFiles.Count == 0 && missingFiles.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("All .cs files are properly referenced in the project!");
            Console.ResetColor();
        }
        else
        {
            if (unusedFiles.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"=== UNUSED FILES ({unusedFiles.Count}) ===");
                Console.WriteLine("These .cs files exist on disk but are NOT included in the project:");
                Console.ResetColor();
                Console.WriteLine();

                foreach (var file in unusedFiles)
                {
                    var relativePath = Path.GetRelativePath(projectDirectory, file);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Write("  [UNUSED] ");
                    Console.ResetColor();
                    Console.WriteLine(relativePath);
                }
                Console.WriteLine();
            }

            if (missingFiles.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"=== MISSING FILES ({missingFiles.Count}) ===");
                Console.ResetColor();
                Console.WriteLine();

                foreach (var file in missingFiles)
                {
                    var relativePath = Path.GetRelativePath(projectDirectory, file);
                    Console.ForegroundColor = ConsoleColor.Magenta;
                    Console.Write("  [MISSING] ");
                    Console.ResetColor();
                    Console.WriteLine(relativePath);
                }
                Console.WriteLine();
            }

            Console.WriteLine("=== FILE SUMMARY ===");
            Console.WriteLine($"  Total .cs files on disk: {allCsFilesOnDisk.Count}");
            Console.WriteLine($"  Files in compilation:    {compiledFiles.Count}");
            Console.WriteLine($"  Unused files:            {unusedFiles.Count}");
            Console.WriteLine($"  Missing files:           {missingFiles.Count}");
        }
    }

    static async Task AnalyzeDeprecatedCode(Project project, Compilation compilation, string projectDirectory)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("=== DEPRECATED CODE ANALYSIS ===");
        Console.ResetColor();
        Console.WriteLine();

        var deprecatedItems = new List<DeprecatedItem>();

        foreach (var document in project.Documents)
        {
            if (document.FilePath == null) continue;

            var semanticModel = await document.GetSemanticModelAsync();
            var syntaxRoot = await document.GetSyntaxRootAsync();
            if (semanticModel == null || syntaxRoot == null) continue;

            // Find all type and member declarations
            var declarations = syntaxRoot.DescendantNodes()
                .Where(n => n is TypeDeclarationSyntax or MethodDeclarationSyntax or PropertyDeclarationSyntax or FieldDeclarationSyntax or EventDeclarationSyntax);

            foreach (var declaration in declarations)
            {
                ISymbol? symbol = declaration switch
                {
                    TypeDeclarationSyntax typeDecl => semanticModel.GetDeclaredSymbol(typeDecl),
                    MethodDeclarationSyntax methodDecl => semanticModel.GetDeclaredSymbol(methodDecl),
                    PropertyDeclarationSyntax propDecl => semanticModel.GetDeclaredSymbol(propDecl),
                    FieldDeclarationSyntax fieldDecl => fieldDecl.Declaration.Variables.FirstOrDefault() is { } variable
                        ? semanticModel.GetDeclaredSymbol(variable)
                        : null,
                    EventDeclarationSyntax eventDecl => semanticModel.GetDeclaredSymbol(eventDecl),
                    _ => null
                };

                if (symbol == null) continue;

                // Check for Obsolete attribute
                var obsoleteAttr = symbol.GetAttributes()
                    .FirstOrDefault(a => a.AttributeClass?.Name == "ObsoleteAttribute" ||
                                        a.AttributeClass?.ToDisplayString() == "System.ObsoleteAttribute");

                if (obsoleteAttr != null)
                {
                    var message = obsoleteAttr.ConstructorArguments.FirstOrDefault().Value?.ToString() ?? "";
                    var isError = obsoleteAttr.ConstructorArguments.Length > 1 &&
                                 obsoleteAttr.ConstructorArguments[1].Value is true;

                    var replacement = ExtractReplacementSuggestion(message);

                    deprecatedItems.Add(new DeprecatedItem
                    {
                        Symbol = symbol,
                        FilePath = document.FilePath,
                        Line = declaration.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        Message = message,
                        IsError = isError,
                        Replacement = replacement
                    });
                }
            }
        }

        if (deprecatedItems.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  No deprecated code found in this project.");
            Console.ResetColor();
        }
        else
        {
            // Group by file
            var groupedByFile = deprecatedItems.GroupBy(d => d.FilePath).OrderBy(g => g.Key);

            foreach (var fileGroup in groupedByFile)
            {
                var relativePath = Path.GetRelativePath(projectDirectory, fileGroup.Key!);
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"  {relativePath}");
                Console.ResetColor();

                foreach (var item in fileGroup.OrderBy(i => i.Line))
                {
                    var symbolKind = GetSymbolKindName(item.Symbol);
                    var errorIndicator = item.IsError ? " [ERROR]" : "";

                    Console.ForegroundColor = item.IsError ? ConsoleColor.Red : ConsoleColor.Yellow;
                    Console.Write($"    [{symbolKind}]{errorIndicator} ");
                    Console.ResetColor();
                    Console.WriteLine($"{item.Symbol.Name} (line {item.Line})");

                    if (!string.IsNullOrEmpty(item.Message))
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.WriteLine($"      Message: {item.Message}");
                        Console.ResetColor();
                    }

                    if (!string.IsNullOrEmpty(item.Replacement))
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"      Replacement: {item.Replacement}");
                        Console.ResetColor();
                    }
                }
                Console.WriteLine();
            }

            Console.WriteLine($"  Total deprecated items: {deprecatedItems.Count}");
            Console.WriteLine($"    - Warnings: {deprecatedItems.Count(d => !d.IsError)}");
            Console.WriteLine($"    - Errors: {deprecatedItems.Count(d => d.IsError)}");
        }
    }

    static async Task AnalyzeUsageCounts(Project project, Compilation compilation, string projectDirectory)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("=== USAGE ANALYSIS ===");
        Console.ResetColor();
        Console.WriteLine();

        var usageCounts = new ConcurrentDictionary<ISymbol, int>(SymbolEqualityComparer.Default);
        var symbolLocations = new ConcurrentDictionary<ISymbol, (string FilePath, int Line)>(SymbolEqualityComparer.Default);

        // First, collect all symbols defined in this project
        foreach (var document in project.Documents)
        {
            if (document.FilePath == null) continue;

            var semanticModel = await document.GetSemanticModelAsync();
            var syntaxRoot = await document.GetSyntaxRootAsync();
            if (semanticModel == null || syntaxRoot == null) continue;

            // Find type declarations
            var typeDeclarations = syntaxRoot.DescendantNodes().OfType<TypeDeclarationSyntax>();
            foreach (var typeDecl in typeDeclarations)
            {
                var symbol = semanticModel.GetDeclaredSymbol(typeDecl);
                if (symbol != null && !IsGeneratedCode(symbol))
                {
                    usageCounts.TryAdd(symbol, 0);
                    symbolLocations.TryAdd(symbol, (document.FilePath, typeDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
                }
            }

            // Find method declarations (excluding property accessors, constructors for now)
            var methodDeclarations = syntaxRoot.DescendantNodes().OfType<MethodDeclarationSyntax>();
            foreach (var methodDecl in methodDeclarations)
            {
                var symbol = semanticModel.GetDeclaredSymbol(methodDecl);
                if (symbol != null && !IsGeneratedCode(symbol) && !IsSpecialMethod(symbol))
                {
                    usageCounts.TryAdd(symbol, 0);
                    symbolLocations.TryAdd(symbol, (document.FilePath, methodDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
                }
            }
        }

        Console.WriteLine($"  Analyzing references for {usageCounts.Count} symbols...");

        // Count references for each symbol
        foreach (var symbol in usageCounts.Keys.ToList())
        {
            try
            {
                var references = await SymbolFinder.FindReferencesAsync(symbol, project.Solution);
                var refCount = references.Sum(r => r.Locations.Count());
                usageCounts[symbol] = refCount;
            }
            catch
            {
                // Some symbols may fail to analyze
            }
        }

        // Find potentially dead code (0 references)
        var deadCode = usageCounts
            .Where(kv => kv.Value == 0)
            .Select(kv => new { Symbol = kv.Key, Location = symbolLocations.GetValueOrDefault(kv.Key) })
            .Where(x => x.Location.FilePath != null)
            .OrderBy(x => x.Location.FilePath)
            .ThenBy(x => x.Location.Line)
            .ToList();

        // Find low-usage code (1-2 references)
        var lowUsage = usageCounts
            .Where(kv => kv.Value >= 1 && kv.Value <= 2)
            .Select(kv => new { Symbol = kv.Key, Count = kv.Value, Location = symbolLocations.GetValueOrDefault(kv.Key) })
            .Where(x => x.Location.FilePath != null)
            .OrderBy(x => x.Count)
            .ThenBy(x => x.Location.FilePath)
            .ToList();

        Console.WriteLine();

        // Report dead code
        if (deadCode.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  POTENTIALLY DEAD CODE ({deadCode.Count} items with 0 references):");
            Console.ResetColor();
            Console.WriteLine();

            var groupedDead = deadCode.GroupBy(d => d.Location.FilePath).OrderBy(g => g.Key);
            foreach (var fileGroup in groupedDead)
            {
                var relativePath = Path.GetRelativePath(projectDirectory, fileGroup.Key!);
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"    {relativePath}");
                Console.ResetColor();

                foreach (var item in fileGroup.OrderBy(i => i.Location.Line))
                {
                    var symbolKind = GetSymbolKindName(item.Symbol);
                    Console.Write($"      ");
                    Console.ForegroundColor = ConsoleColor.DarkRed;
                    Console.Write($"[{symbolKind}] ");
                    Console.ResetColor();
                    Console.WriteLine($"{item.Symbol.Name} (line {item.Location.Line})");
                }
            }
            Console.WriteLine();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  No potentially dead code found (all symbols have at least 1 reference).");
            Console.ResetColor();
        }

        // Report low usage
        if (lowUsage.Count > 0 && lowUsage.Count <= 50) // Only show if reasonable amount
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  LOW USAGE CODE ({lowUsage.Count} items with 1-2 references):");
            Console.ResetColor();
            Console.WriteLine();

            var groupedLow = lowUsage.GroupBy(d => d.Location.FilePath).OrderBy(g => g.Key);
            foreach (var fileGroup in groupedLow)
            {
                var relativePath = Path.GetRelativePath(projectDirectory, fileGroup.Key!);
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"    {relativePath}");
                Console.ResetColor();

                foreach (var item in fileGroup.OrderBy(i => i.Location.Line))
                {
                    var symbolKind = GetSymbolKindName(item.Symbol);
                    Console.Write($"      ");
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.Write($"[{symbolKind}] ");
                    Console.ResetColor();
                    Console.WriteLine($"{item.Symbol.Name} ({item.Count} ref) - line {item.Location.Line}");
                }
            }
            Console.WriteLine();
        }

        // Summary
        Console.WriteLine("  === USAGE SUMMARY ===");
        Console.WriteLine($"    Total symbols analyzed: {usageCounts.Count}");
        Console.WriteLine($"    Dead code (0 refs):     {deadCode.Count}");
        Console.WriteLine($"    Low usage (1-2 refs):   {lowUsage.Count}");
        Console.WriteLine($"    Active code (3+ refs):  {usageCounts.Count(kv => kv.Value >= 3)}");
    }

    static string? ExtractReplacementSuggestion(string message)
    {
        if (string.IsNullOrEmpty(message)) return null;

        // Common patterns for replacement suggestions
        // Pattern: "Use X instead" or "use X instead"
        var useInstead = Regex.Match(message, @"[Uu]se\s+(\w+(?:\.\w+)*)\s+instead", RegexOptions.IgnoreCase);
        if (useInstead.Success) return useInstead.Groups[1].Value;

        // Pattern: "Replaced by X" or "replaced with X"
        var replacedBy = Regex.Match(message, @"[Rr]eplaced?\s+(?:by|with)\s+(\w+(?:\.\w+)*)", RegexOptions.IgnoreCase);
        if (replacedBy.Success) return replacedBy.Groups[1].Value;

        // Pattern: "See X"
        var see = Regex.Match(message, @"[Ss]ee\s+(\w+(?:\.\w+)*)", RegexOptions.IgnoreCase);
        if (see.Success) return see.Groups[1].Value;

        // Pattern: "Prefer X"
        var prefer = Regex.Match(message, @"[Pp]refer\s+(\w+(?:\.\w+)*)", RegexOptions.IgnoreCase);
        if (prefer.Success) return prefer.Groups[1].Value;

        // Pattern: "Migrate to X"
        var migrate = Regex.Match(message, @"[Mm]igrate\s+to\s+(\w+(?:\.\w+)*)", RegexOptions.IgnoreCase);
        if (migrate.Success) return migrate.Groups[1].Value;

        return null;
    }

    static string GetSymbolKindName(ISymbol symbol) => symbol.Kind switch
    {
        SymbolKind.NamedType => ((INamedTypeSymbol)symbol).TypeKind switch
        {
            TypeKind.Class => "Class",
            TypeKind.Interface => "Interface",
            TypeKind.Struct => "Struct",
            TypeKind.Enum => "Enum",
            TypeKind.Delegate => "Delegate",
            _ => "Type"
        },
        SymbolKind.Method => "Method",
        SymbolKind.Property => "Property",
        SymbolKind.Field => "Field",
        SymbolKind.Event => "Event",
        _ => symbol.Kind.ToString()
    };

    static bool IsGeneratedCode(ISymbol symbol)
    {
        // Check for generated code attributes
        return symbol.GetAttributes().Any(a =>
            a.AttributeClass?.Name == "GeneratedCodeAttribute" ||
            a.AttributeClass?.Name == "CompilerGeneratedAttribute");
    }

    static bool IsSpecialMethod(IMethodSymbol method)
    {
        // Skip special methods that are typically called implicitly
        return method.MethodKind != MethodKind.Ordinary ||
               method.Name.StartsWith("get_") ||
               method.Name.StartsWith("set_") ||
               method.Name.StartsWith("add_") ||
               method.Name.StartsWith("remove_") ||
               method.IsOverride ||
               method.IsVirtual ||
               method.ExplicitInterfaceImplementations.Length > 0;
    }

    static async Task AnalyzeRefactoring(Project project, string projectDirectory)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("=== REFACTORING ANALYSIS ===");
        Console.ResetColor();
        Console.WriteLine();

        var analyzer = new RefactoringAnalyzer();
        var (longMethods, godClasses, featureEnvies, parameterSmells, dataClumps) = await analyzer.AnalyzeAsync(project);

        // Long Methods
        if (longMethods.Count > 0)
        {
            var methodsNeedingExtraction = longMethods.Where(m => m.ExtractCandidates.Count > 0).ToList();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  LONG METHODS ({longMethods.Count} found, {methodsNeedingExtraction.Count} with extract candidates):");
            Console.ResetColor();

            foreach (var method in longMethods.OrderByDescending(m => m.LineCount).Take(15))
            {
                var relativePath = Path.GetRelativePath(projectDirectory, method.FilePath);
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"    {relativePath}:{method.StartLine} ");
                Console.ResetColor();
                Console.Write($"{method.ClassName}.{method.MethodName}");
                Console.ForegroundColor = method.LineCount > 100 ? ConsoleColor.Red : ConsoleColor.Yellow;
                Console.WriteLine($" ({method.LineCount} lines, CC={method.CyclomaticComplexity})");
                Console.ResetColor();

                if (method.ExtractCandidates.Count > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    foreach (var candidate in method.ExtractCandidates.Take(3))
                    {
                        Console.WriteLine($"      -> Extract: {candidate.SuggestedName} (lines {candidate.StartLine}-{candidate.EndLine})");
                    }
                    Console.ResetColor();
                }
            }
            Console.WriteLine();
        }

        // God Classes
        if (godClasses.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  GOD CLASSES ({godClasses.Count} detected):");
            Console.ResetColor();

            foreach (var god in godClasses.OrderByDescending(g => g.MethodCount).Take(10))
            {
                var relativePath = Path.GetRelativePath(projectDirectory, god.FilePath);
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"    {relativePath}:{god.Line} ");
                Console.ResetColor();
                Console.Write($"{god.ClassName}");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($" (LCOM={god.LCOM:F2}, {god.MethodCount} methods, {god.FieldCount} fields)");
                Console.ResetColor();

                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"      Responsibilities: {string.Join(", ", god.Responsibilities.Take(5))}");
                Console.ResetColor();
            }
            Console.WriteLine();
        }

        // Feature Envy
        if (featureEnvies.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  FEATURE ENVY ({featureEnvies.Count} methods):");
            Console.ResetColor();

            foreach (var envy in featureEnvies.OrderByDescending(e => e.EnviedMemberAccess).Take(10))
            {
                var relativePath = Path.GetRelativePath(projectDirectory, envy.FilePath);
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"    {relativePath}:{envy.Line} ");
                Console.ResetColor();
                Console.WriteLine($"{envy.ClassName}.{envy.MethodName} -> envies {envy.EnviedClass} ({envy.EnviedMemberAccess} accesses, ratio {envy.EnvyRatio:F2})");
            }
            Console.WriteLine();
        }

        // Parameter Smells
        if (parameterSmells.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  PARAMETER SMELLS ({parameterSmells.Count} methods):");
            Console.ResetColor();

            foreach (var smell in parameterSmells.OrderByDescending(s => s.ParameterCount).Take(10))
            {
                var relativePath = Path.GetRelativePath(projectDirectory, smell.FilePath);
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"    {relativePath}:{smell.Line} ");
                Console.ResetColor();
                Console.Write($"{smell.ClassName}.{smell.MethodName}");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($" ({smell.ParameterCount} params, {smell.SmellType})");
                Console.ResetColor();

                if (!string.IsNullOrEmpty(smell.Suggestion))
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"      {smell.Suggestion}");
                    Console.ResetColor();
                }
            }
            Console.WriteLine();
        }

        // Data Clumps
        if (dataClumps.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  DATA CLUMPS ({dataClumps.Count} groups):");
            Console.ResetColor();

            foreach (var clump in dataClumps.OrderByDescending(c => c.Occurrences.Count).Take(10))
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write($"    [{string.Join(", ", clump.Parameters)}]");
                Console.ResetColor();
                Console.WriteLine($" appears {clump.Occurrences.Count}x -> suggest: {clump.SuggestedClassName}");

                foreach (var (className, methodName, filePath, line) in clump.Occurrences.Take(3))
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"      - {className}.{methodName}");
                    Console.ResetColor();
                }
            }
            Console.WriteLine();
        }

        Console.WriteLine($"  Summary: {longMethods.Count} long methods, {godClasses.Count} god classes, {featureEnvies.Count} feature envy, {parameterSmells.Count} param smells, {dataClumps.Count} data clumps");
    }

    static async Task AnalyzeArchitecture(Project project, string projectDirectory)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("=== ARCHITECTURE ANALYSIS ===");
        Console.ResetColor();
        Console.WriteLine();

        var analyzer = new ArchitectureAnalyzer();
        var (publicApi, callGraph, inheritanceIssues, interfaceIssues) = await analyzer.AnalyzeAsync(project);

        // Public API Surface
        if (publicApi.Count > 0)
        {
            var highRisk = publicApi.Where(a => a.BreakingChangeRisk == "High").ToList();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  PUBLIC API SURFACE ({publicApi.Count} members, {highRisk.Count} high-risk):");
            Console.ResetColor();

            var byType = publicApi.GroupBy(a => a.TypeName).OrderByDescending(g => g.Count()).Take(10);
            foreach (var group in byType)
            {
                Console.WriteLine($"    {group.Key}: {group.Count()} public members");
            }
            Console.WriteLine();

            if (highRisk.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  HIGH-RISK API MEMBERS ({highRisk.Count}):");
                Console.ResetColor();

                foreach (var api in highRisk.Take(10))
                {
                    var relativePath = Path.GetRelativePath(projectDirectory, api.FilePath);
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write($"    {relativePath}:{api.Line} ");
                    Console.ResetColor();
                    Console.WriteLine($"{api.TypeName}.{api.MemberName} ({api.MemberType})");
                }
                Console.WriteLine();
            }
        }

        // Call Graph (Entry Points and Dead Ends)
        var entryPoints = callGraph.Where(c => c.IsEntryPoint).ToList();
        var deadEnds = callGraph.Where(c => c.IsDeadEnd).ToList();

        if (entryPoints.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  ENTRY POINTS ({entryPoints.Count}):");
            Console.ResetColor();

            foreach (var entry in entryPoints.OrderByDescending(e => e.OutgoingCalls).Take(15))
            {
                Console.Write($"    {entry.TypeName}.{entry.MethodName}");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($" (calls {entry.OutgoingCalls} methods)");
                Console.ResetColor();
            }
            Console.WriteLine();
        }

        if (deadEnds.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  DEAD END METHODS ({deadEnds.Count} - called but call nothing):");
            Console.ResetColor();

            foreach (var dead in deadEnds.Where(d => !string.IsNullOrEmpty(d.FilePath)).Take(10))
            {
                var relativePath = Path.GetRelativePath(projectDirectory, dead.FilePath);
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"    {relativePath}:{dead.Line} ");
                Console.ResetColor();
                Console.WriteLine($"{dead.TypeName}.{dead.MethodName} (called {dead.IncomingCalls}x)");
            }
            Console.WriteLine();
        }

        // Inheritance Issues
        if (inheritanceIssues.Count > 0)
        {
            var deepHierarchies = inheritanceIssues.Where(i => i.InheritanceDepth > 3).ToList();
            var compositionCandidates = inheritanceIssues.Where(i => i.HasCompositionOpportunity).ToList();

            if (deepHierarchies.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  DEEP INHERITANCE HIERARCHIES ({deepHierarchies.Count}):");
                Console.ResetColor();

                foreach (var hier in deepHierarchies.OrderByDescending(h => h.InheritanceDepth).Take(10))
                {
                    Console.Write($"    {hier.TypeName}");
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($" (depth {hier.InheritanceDepth}: {string.Join(" -> ", hier.InheritanceChain.Take(4))})");
                    Console.ResetColor();
                }
                Console.WriteLine();
            }

            if (compositionCandidates.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"  COMPOSITION CANDIDATES ({compositionCandidates.Count}):");
                Console.ResetColor();

                foreach (var comp in compositionCandidates.Take(10))
                {
                    var relativePath = Path.GetRelativePath(projectDirectory, comp.FilePath);
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write($"    {relativePath}:{comp.Line} ");
                    Console.ResetColor();
                    Console.Write($"{comp.TypeName}");
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine($" - {comp.CompositionSuggestion}");
                    Console.ResetColor();
                }
                Console.WriteLine();
            }
        }

        // Interface Segregation Issues
        if (interfaceIssues.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  INTERFACE SEGREGATION ISSUES ({interfaceIssues.Count}):");
            Console.ResetColor();

            foreach (var issue in interfaceIssues.OrderByDescending(i => i.MemberCount).Take(10))
            {
                var relativePath = Path.GetRelativePath(projectDirectory, issue.FilePath);
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"    {relativePath}:{issue.Line} ");
                Console.ResetColor();
                Console.Write($"{issue.InterfaceName}");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($" ({issue.MemberCount} members, {issue.PartialImplementations.Count} partial implementations)");
                Console.ResetColor();

                if (issue.SuggestedSplits.Count > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"      Split into: {string.Join(", ", issue.SuggestedSplits)}");
                    Console.ResetColor();
                }
            }
            Console.WriteLine();
        }

        Console.WriteLine($"  Summary: {publicApi.Count} public API, {entryPoints.Count} entry points, {deadEnds.Count} dead ends, {inheritanceIssues.Count} inheritance issues, {interfaceIssues.Count} ISP violations");
    }

    static async Task AnalyzeSafety(Project project, string projectDirectory)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("=== CODE SAFETY ANALYSIS ===");
        Console.ResetColor();
        Console.WriteLine();

        var analyzer = new CodeSafetyAnalyzer();
        var (nullIssues, immutabilityIssues, loggingGaps, loggingCoverage) = await analyzer.AnalyzeAsync(project);

        // Null Safety Issues
        if (nullIssues.Count > 0)
        {
            var critical = nullIssues.Where(n => n.Severity == "Critical").ToList();
            Console.ForegroundColor = critical.Count > 0 ? ConsoleColor.Red : ConsoleColor.Yellow;
            Console.WriteLine($"  NULL SAFETY ISSUES ({nullIssues.Count}, {critical.Count} critical):");
            Console.ResetColor();

            var grouped = nullIssues.GroupBy(n => n.Type);
            foreach (var group in grouped.OrderByDescending(g => g.Count()))
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"    {group.Key}: {group.Count()}");
                Console.ResetColor();

                foreach (var issue in group.Take(5))
                {
                    var relativePath = Path.GetRelativePath(projectDirectory, issue.FilePath);
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"      {relativePath}:{issue.Line} - {issue.Description}");
                    Console.ResetColor();
                }
                if (group.Count() > 5)
                    Console.WriteLine($"      ... and {group.Count() - 5} more");
            }
            Console.WriteLine();
        }

        // Immutability Issues
        if (immutabilityIssues.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  IMMUTABILITY OPPORTUNITIES ({immutabilityIssues.Count}):");
            Console.ResetColor();

            var grouped = immutabilityIssues.GroupBy(i => i.Type);
            foreach (var group in grouped.OrderByDescending(g => g.Count()))
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"    {group.Key}: {group.Count()}");
                Console.ResetColor();

                foreach (var issue in group.Take(5))
                {
                    var relativePath = Path.GetRelativePath(projectDirectory, issue.FilePath);
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"      {relativePath}:{issue.Line} - {issue.MemberName}: {issue.Suggestion}");
                    Console.ResetColor();
                }
                if (group.Count() > 5)
                    Console.WriteLine($"      ... and {group.Count() - 5} more");
            }
            Console.WriteLine();
        }

        // Logging Gaps
        if (loggingGaps.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  LOGGING GAPS ({loggingGaps.Count}):");
            Console.ResetColor();

            foreach (var gap in loggingGaps.Take(15))
            {
                var relativePath = Path.GetRelativePath(projectDirectory, gap.FilePath);
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"    {relativePath}:{gap.Line} ");
                Console.ResetColor();
                Console.Write($"{gap.ClassName}.{gap.MethodName}");
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($" - {gap.GapType}: {gap.Description}");
                Console.ResetColor();
            }
            if (loggingGaps.Count > 15)
                Console.WriteLine($"    ... and {loggingGaps.Count - 15} more");
            Console.WriteLine();
        }

        // Logging Coverage Summary
        if (loggingCoverage.Count > 0)
        {
            var lowCoverage = loggingCoverage.Where(c => c.CoveragePercent < 20).ToList();

            if (lowCoverage.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  LOW LOGGING COVERAGE ({lowCoverage.Count} classes < 20%):");
                Console.ResetColor();

                foreach (var cls in lowCoverage.OrderBy(c => c.CoveragePercent).Take(15))
                {
                    var relativePath = Path.GetRelativePath(projectDirectory, cls.FilePath);
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write($"    {relativePath} ");
                    Console.ResetColor();
                    Console.Write($"{cls.ClassName}");
                    Console.ForegroundColor = cls.CoveragePercent == 0 ? ConsoleColor.Red : ConsoleColor.Yellow;
                    Console.WriteLine($" ({cls.CoveragePercent:F0}% - {cls.MethodsWithLogging}/{cls.TotalMethods} methods)");
                    Console.ResetColor();
                }
                Console.WriteLine();
            }

            var avgCoverage = loggingCoverage.Average(c => c.CoveragePercent);
            Console.WriteLine($"  Logging coverage summary: {avgCoverage:F1}% average across {loggingCoverage.Count} classes");
        }

        Console.WriteLine($"  Summary: {nullIssues.Count} null issues, {immutabilityIssues.Count} immutability opportunities, {loggingGaps.Count} logging gaps");
    }

    static async Task AnalyzeOptimizations(Project project, string projectDirectory)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("=== OPTIMIZATION ANALYSIS ===");
        Console.ResetColor();
        Console.WriteLine();

        var analyzer = new OptimizationAnalyzer();
        var result = await analyzer.AnalyzeAsync(project);

        if (result.Opportunities.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  No optimization opportunities found.");
            Console.ResetColor();
            return;
        }

        // Group by category
        var grouped = result.Opportunities.GroupBy(o => o.Category).OrderByDescending(g => g.Count());

        foreach (var group in grouped)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"  {group.Key.ToUpperInvariant()} OPTIMIZATIONS ({group.Count()}):");
            Console.ResetColor();

            // Group by type within category
            var byType = group.GroupBy(o => o.Type);
            foreach (var typeGroup in byType)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"    {typeGroup.Key} ({typeGroup.Count()}):");
                Console.ResetColor();

                foreach (var opportunity in typeGroup.Take(5))
                {
                    var relativePath = Path.GetRelativePath(projectDirectory, opportunity.FilePath);
                    var confidenceColor = opportunity.Confidence switch
                    {
                        "High" => ConsoleColor.Green,
                        "Medium" => ConsoleColor.Yellow,
                        _ => ConsoleColor.DarkGray
                    };

                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write($"      {relativePath}:{opportunity.StartLine} ");
                    Console.ResetColor();

                    Console.ForegroundColor = confidenceColor;
                    Console.Write($"[{opportunity.Confidence}] ");
                    Console.ResetColor();

                    Console.WriteLine(opportunity.Description);

                    // Show code suggestion if available
                    if (!string.IsNullOrWhiteSpace(opportunity.SuggestedCode) &&
                        opportunity.SuggestedCode != opportunity.CurrentCode)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkRed;
                        Console.WriteLine($"        - {opportunity.CurrentCode.Trim().Replace("\n", " ").Replace("\r", "")}");
                        Console.ForegroundColor = ConsoleColor.DarkGreen;
                        Console.WriteLine($"        + {opportunity.SuggestedCode.Trim().Replace("\n", " ").Replace("\r", "")}");
                        Console.ResetColor();
                    }
                }

                if (typeGroup.Count() > 5)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"      ... and {typeGroup.Count() - 5} more");
                    Console.ResetColor();
                }
            }
            Console.WriteLine();
        }

        // Summary
        var highConfidence = result.Opportunities.Count(o => o.Confidence == "High");
        var mediumConfidence = result.Opportunities.Count(o => o.Confidence == "Medium");
        var lowConfidence = result.Opportunities.Count(o => o.Confidence == "Low");

        Console.WriteLine("  === OPTIMIZATION SUMMARY ===");
        Console.WriteLine($"    Total opportunities:  {result.Opportunities.Count}");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"    High confidence:      {highConfidence}");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"    Medium confidence:    {mediumConfidence}");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"    Low confidence:       {lowConfidence}");
        Console.ResetColor();

        if (result.Summary != null)
        {
            Console.WriteLine();
            Console.WriteLine($"    Performance:          {result.Summary.PerformanceOptimizations}");
            Console.WriteLine($"    Readability:          {result.Summary.ReadabilityImprovements}");
            Console.WriteLine($"    Modernization:        {result.Summary.ModernizationOpportunities}");
        }
    }

    static async Task AnalyzeSecurityVulnerabilities(Project project, string projectDirectory)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("=== SECURITY VULNERABILITY ANALYSIS ===");
        Console.ResetColor();
        Console.WriteLine();

        var analyzer = new SecurityAnalyzer();
        var securityResult = await analyzer.AnalyzeAsync(project);
        var vulnerabilities = securityResult.Vulnerabilities;

        if (vulnerabilities.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  No security vulnerabilities found.");
            Console.ResetColor();
            return;
        }

        // Group by severity
        var critical = vulnerabilities.Where(v => v.Severity == "Critical").ToList();
        var high = vulnerabilities.Where(v => v.Severity == "High").ToList();
        var medium = vulnerabilities.Where(v => v.Severity == "Medium").ToList();
        var low = vulnerabilities.Where(v => v.Severity == "Low").ToList();

        // Summary
        Console.ForegroundColor = ConsoleColor.Red;
        if (critical.Count > 0) Console.WriteLine($"  CRITICAL: {critical.Count}");
        Console.ForegroundColor = ConsoleColor.DarkRed;
        if (high.Count > 0) Console.WriteLine($"  HIGH: {high.Count}");
        Console.ForegroundColor = ConsoleColor.Yellow;
        if (medium.Count > 0) Console.WriteLine($"  MEDIUM: {medium.Count}");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        if (low.Count > 0) Console.WriteLine($"  LOW: {low.Count}");
        Console.ResetColor();
        Console.WriteLine();

        // Group by type
        var byType = vulnerabilities.GroupBy(v => v.VulnerabilityType).OrderByDescending(g => g.Max(v => v.Severity == "Critical" ? 4 : v.Severity == "High" ? 3 : v.Severity == "Medium" ? 2 : 1));

        foreach (var group in byType)
        {
            var maxSeverity = group.Max(v => v.Severity == "Critical" ? 4 : v.Severity == "High" ? 3 : v.Severity == "Medium" ? 2 : 1);
            Console.ForegroundColor = maxSeverity >= 4 ? ConsoleColor.Red : maxSeverity >= 3 ? ConsoleColor.DarkRed : maxSeverity >= 2 ? ConsoleColor.Yellow : ConsoleColor.DarkGray;
            Console.WriteLine($"  {group.Key} ({group.Count()}):");
            Console.ResetColor();

            foreach (var vuln in group.Take(5))
            {
                var relativePath = Path.GetRelativePath(projectDirectory, vuln.FilePath);
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"    {relativePath}:{vuln.StartLine} ");
                Console.ResetColor();

                Console.ForegroundColor = vuln.Severity == "Critical" ? ConsoleColor.Red : vuln.Severity == "High" ? ConsoleColor.DarkRed : ConsoleColor.Yellow;
                Console.Write($"[{vuln.Severity}] ");
                Console.ResetColor();

                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write($"{vuln.CweId} ");
                Console.ResetColor();
                Console.WriteLine(vuln.Description);

                Console.ForegroundColor = ConsoleColor.DarkGreen;
                Console.WriteLine($"      Fix: {vuln.Recommendation}");
                Console.ResetColor();
            }

            if (group.Count() > 5)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"    ... and {group.Count() - 5} more");
                Console.ResetColor();
            }
            Console.WriteLine();
        }

        Console.WriteLine($"  Total vulnerabilities: {vulnerabilities.Count}");
    }

    static async Task ShowDashboard(Project project, string projectDirectory)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("=== PROJECT HEALTH DASHBOARD ===");
        Console.ResetColor();
        Console.WriteLine();

        var dashboard = new MetricsDashboard();
        var metrics = await dashboard.GenerateDashboardAsync(project);

        // Health Score
        var healthColor = metrics.HealthScore >= 80 ? ConsoleColor.Green : metrics.HealthScore >= 60 ? ConsoleColor.Yellow : ConsoleColor.Red;
        Console.Write("  Health Score: ");
        Console.ForegroundColor = healthColor;
        Console.WriteLine($"{metrics.HealthScore}/100");
        Console.ResetColor();
        Console.WriteLine();

        // Size Metrics
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  CODE SIZE:");
        Console.ResetColor();
        Console.WriteLine($"    Files:            {metrics.TotalFiles}");
        Console.WriteLine($"    Lines of Code:    {metrics.TotalLines:N0}");
        Console.WriteLine($"    Classes:          {metrics.TotalClasses}");
        Console.WriteLine($"    Methods:          {metrics.TotalMethods}");
        Console.WriteLine();

        // Complexity
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  COMPLEXITY:");
        Console.ResetColor();
        var complexityColor = metrics.AverageCyclomaticComplexity <= 5 ? ConsoleColor.Green : metrics.AverageCyclomaticComplexity <= 10 ? ConsoleColor.Yellow : ConsoleColor.Red;
        Console.Write($"    Avg Cyclomatic:   ");
        Console.ForegroundColor = complexityColor;
        Console.WriteLine($"{metrics.AverageCyclomaticComplexity:F1}");
        Console.ResetColor();

        Console.Write($"    Max Cyclomatic:   ");
        Console.ForegroundColor = metrics.MaxCyclomaticComplexity > 20 ? ConsoleColor.Red : ConsoleColor.Yellow;
        Console.WriteLine($"{metrics.MaxCyclomaticComplexity}");
        Console.ResetColor();

        Console.WriteLine($"    Methods > 10 CC:  {metrics.MethodsAboveThreshold}");
        Console.WriteLine();

        // Maintainability
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  MAINTAINABILITY:");
        Console.ResetColor();
        var maintColor = metrics.MaintainabilityIndex >= 70 ? ConsoleColor.Green : metrics.MaintainabilityIndex >= 40 ? ConsoleColor.Yellow : ConsoleColor.Red;
        Console.Write($"    Index:            ");
        Console.ForegroundColor = maintColor;
        Console.WriteLine($"{metrics.MaintainabilityIndex:F1}");
        Console.ResetColor();

        var debtHours = metrics.TechnicalDebtMinutes / 60.0;
        var debtDays = debtHours / 8.0;
        Console.Write($"    Technical Debt:   ");
        Console.ForegroundColor = debtDays > 5 ? ConsoleColor.Red : debtDays > 1 ? ConsoleColor.Yellow : ConsoleColor.Green;
        Console.WriteLine($"{debtHours:F1} hours ({debtDays:F1} days)");
        Console.ResetColor();
        Console.WriteLine();

        // Security Summary
        if (metrics.SecurityVulnerabilities > 0)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  SECURITY:");
            Console.ResetColor();

            Console.Write($"    Vulnerabilities:  ");
            Console.ForegroundColor = metrics.CriticalSecurityIssues > 0 ? ConsoleColor.Red : ConsoleColor.Yellow;
            Console.WriteLine($"{metrics.SecurityVulnerabilities} ({metrics.CriticalSecurityIssues} critical, {metrics.HighSecurityIssues} high)");
            Console.ResetColor();
            Console.WriteLine();
        }

        Console.WriteLine("  Run with --security for detailed vulnerability report.");
    }

    static async Task AnalyzeTrends(string projectDirectory)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("=== TREND ANALYSIS ===");
        Console.ResetColor();
        Console.WriteLine();

        var analyzer = new TrendAnalyzer(projectDirectory);
        var gitTrends = await analyzer.AnalyzeGitTrendsAsync(20);

        if (gitTrends.AnalyzedCommits == 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  Git repository not found or no commits available.");
            Console.ResetColor();
            return;
        }

        Console.WriteLine($"  Analyzed {gitTrends.AnalyzedCommits} recent commits");
        Console.WriteLine();

        // Change stats
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  CHANGE STATISTICS:");
        Console.ResetColor();
        Console.WriteLine($"    Files Changed:    {gitTrends.TotalFilesChanged}");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"    Lines Added:      +{gitTrends.TotalAdditions}");
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"    Lines Deleted:    -{gitTrends.TotalDeletions}");
        Console.ResetColor();
        Console.WriteLine();

        // Hotspots
        if (gitTrends.Hotspots.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  CHANGE HOTSPOTS:");
            Console.ResetColor();

            foreach (var hotspot in gitTrends.Hotspots.Take(10))
            {
                var relativePath = Path.GetRelativePath(projectDirectory, hotspot.FilePath);
                Console.ForegroundColor = hotspot.ChangeCount > 5 ? ConsoleColor.Red : ConsoleColor.Yellow;
                Console.Write($"    {hotspot.ChangeCount,3}x  ");
                Console.ResetColor();
                Console.WriteLine(relativePath);
            }
            Console.WriteLine();
        }

        // Author contributions
        if (gitTrends.AuthorContributions.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  CONTRIBUTOR ACTIVITY:");
            Console.ResetColor();

            foreach (var (author, count) in gitTrends.AuthorContributions.OrderByDescending(kv => kv.Value).Take(5))
            {
                Console.WriteLine($"    {author}: {count} commits");
            }
            Console.WriteLine();
        }

        // Check for regressions
        var regressions = await analyzer.DetectRegressionsAsync();
        if (regressions.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("  DETECTED REGRESSIONS:");
            Console.ResetColor();

            foreach (var regression in regressions)
            {
                Console.ForegroundColor = regression.Severity == "Critical" ? ConsoleColor.Red : ConsoleColor.Yellow;
                Console.Write($"    [{regression.Severity}] ");
                Console.ResetColor();
                Console.WriteLine(regression.Message);
            }
            Console.WriteLine();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  No regressions detected in recent history.");
            Console.ResetColor();
        }
    }

    static async Task<int> HandleRefactorAnalyze(string projectPath, string severity)
    {
        try
        {
            AnalysisService.EnsureMSBuildRegistered();
            Console.WriteLine("Analyzing refactoring opportunities...");
            Console.WriteLine();

            var service = new AnalysisService();
            var project = await service.OpenProjectAsync(projectPath);

            var workspaceManager = new VirtualWorkspace.VirtualWorkspaceManager();
            var projectDir = Path.GetDirectoryName(project.FilePath) ?? Environment.CurrentDirectory;
            var backupService = new BackupService(projectDir);
            var orchestrator = new Refactoring.RefactoringOrchestrator(workspaceManager, backupService);

            var options = new Refactoring.Models.RefactoringOptions
            {
                MinimumSeverity = severity.ToLowerInvariant() switch
                {
                    "critical" => Refactoring.Models.SmellSeverity.Critical,
                    "high" => Refactoring.Models.SmellSeverity.High,
                    "medium" => Refactoring.Models.SmellSeverity.Medium,
                    _ => Refactoring.Models.SmellSeverity.Low
                }
            };

            var plan = await orchestrator.AnalyzeRefactoringAsync(project, options);

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"REFACTORING OPPORTUNITIES: {plan.Summary.TotalOpportunities}");
            Console.ResetColor();
            Console.WriteLine();

            if (plan.Summary.TotalOpportunities == 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("No significant refactoring opportunities found.");
                Console.ResetColor();
                return 0;
            }

            Console.WriteLine($"  Critical: {plan.Summary.CriticalCount}");
            Console.WriteLine($"  High: {plan.Summary.HighCount}");
            Console.WriteLine($"  Medium: {plan.Summary.MediumCount}");
            Console.WriteLine($"  Low: {plan.Summary.LowCount}");
            Console.WriteLine();

            foreach (var opportunity in plan.Opportunities.Take(20))
            {
                var color = opportunity.Smell.Severity switch
                {
                    Refactoring.Models.SmellSeverity.Critical => ConsoleColor.Red,
                    Refactoring.Models.SmellSeverity.High => ConsoleColor.Yellow,
                    _ => ConsoleColor.White
                };

                Console.ForegroundColor = color;
                Console.Write($"  [{opportunity.Smell.Severity}] ");
                Console.ResetColor();
                Console.WriteLine($"{opportunity.Smell.TargetName} ({opportunity.Smell.SmellType})");

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"    {Path.GetFileName(opportunity.Smell.FilePath)}:{opportunity.Smell.StartLine}");
                Console.WriteLine($"    {opportunity.Smell.Description}");
                Console.Write($"    Strategies: ");
                Console.ResetColor();
                Console.WriteLine(string.Join(", ", opportunity.ApplicableStrategies));
                Console.WriteLine();
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            Console.ResetColor();
            return 1;
        }
    }

    static async Task<int> HandleRefactorPreview(string projectPath, string filePath, string target)
    {
        try
        {
            AnalysisService.EnsureMSBuildRegistered();
            Console.WriteLine($"Previewing refactoring strategies for {target}...");
            Console.WriteLine();

            var service = new AnalysisService();
            var project = await service.OpenProjectAsync(projectPath);

            var document = project.Documents.FirstOrDefault(d =>
                d.FilePath?.EndsWith(filePath, StringComparison.OrdinalIgnoreCase) == true ||
                d.FilePath == filePath);

            if (document == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"File not found: {filePath}");
                Console.ResetColor();
                return 1;
            }

            var workspaceManager = new VirtualWorkspace.VirtualWorkspaceManager();
            var projectDir = Path.GetDirectoryName(project.FilePath) ?? Environment.CurrentDirectory;
            var backupService = new BackupService(projectDir);
            var orchestrator = new Refactoring.RefactoringOrchestrator(workspaceManager, backupService);
            var cohesionAnalyzer = new Refactoring.Analysis.CohesionAnalyzer();

            var smells = await cohesionAnalyzer.DetectCohesionSmellsAsync(document);
            var targetSmell = smells.FirstOrDefault(s => s.TargetName == target) ?? new Refactoring.Models.CodeSmell
            {
                SmellType = Refactoring.Models.CodeSmellType.GodClass,
                Severity = Refactoring.Models.SmellSeverity.High,
                FilePath = document.FilePath ?? "",
                StartLine = 1,
                EndLine = 1,
                TargetName = target,
                Description = $"Manual refactoring target: {target}"
            };

            var opportunity = new Refactoring.Models.RefactoringOpportunity
            {
                Smell = targetSmell,
                DocumentId = document.Id,
                ApplicableStrategies = Enum.GetValues<Refactoring.Models.RefactoringType>().ToList(),
                EstimatedComplexityReduction = 0,
                EstimatedCohesionImprovement = 0,
                Recommendation = ""
            };

            var comparison = await orchestrator.CompareStrategiesAsync(project, opportunity);

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"STRATEGY COMPARISON: {comparison.Results.Count} strategies tested");
            Console.ResetColor();
            Console.WriteLine();

            foreach (var result in comparison.Results.OrderByDescending(r => r.Score.OverallRefactoringScore))
            {
                var scoreColor = result.Score.OverallRefactoringScore > 50 ? ConsoleColor.Green :
                                 result.Score.OverallRefactoringScore > 0 ? ConsoleColor.Yellow : ConsoleColor.Red;

                Console.ForegroundColor = scoreColor;
                Console.Write($"  [{result.Score.OverallRefactoringScore:F1}] ");
                Console.ResetColor();
                Console.WriteLine($"{result.StrategyName}");

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"    {result.Description}");
                Console.WriteLine($"    Cohesion: {result.Score.CohesionImprovement:+0.0;-0.0;0}  Complexity: {result.Score.BaseScore.ComplexityDelta:+0;-0;0}  LOC: {result.Score.BaseScore.LocDelta:+0;-0;0}");
                Console.WriteLine($"    Lines: +{result.Diff.AddedLines} -{result.Diff.RemovedLines}");
                Console.ResetColor();
                Console.WriteLine();
            }

            if (comparison.BestResult != null)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"Recommended: {comparison.BestResult.StrategyName} (score: {comparison.BestResult.Score.OverallRefactoringScore:F1})");
                Console.ResetColor();
                Console.WriteLine();
                Console.WriteLine($"Run with --refactor-apply --file=\"{filePath}\" --target=\"{target}\" to apply");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: {ex.Message}");
            Console.ResetColor();
            return 1;
        }
    }

    static async Task<int> HandleRefactorApply(string projectPath, string filePath, string target, string? strategy)
    {
        try
        {
            AnalysisService.EnsureMSBuildRegistered();
            Console.WriteLine($"Applying refactoring to {target}...");
            Console.WriteLine();

            var service = new AnalysisService();
            var project = await service.OpenProjectAsync(projectPath);

            var document = project.Documents.FirstOrDefault(d =>
                d.FilePath?.EndsWith(filePath, StringComparison.OrdinalIgnoreCase) == true ||
                d.FilePath == filePath);

            if (document == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"File not found: {filePath}");
                Console.ResetColor();
                return 1;
            }

            var workspaceManager = new VirtualWorkspace.VirtualWorkspaceManager();
            var projectDir = Path.GetDirectoryName(project.FilePath) ?? Environment.CurrentDirectory;
            var backupService = new BackupService(projectDir);
            var orchestrator = new Refactoring.RefactoringOrchestrator(workspaceManager, backupService);
            var cohesionAnalyzer = new Refactoring.Analysis.CohesionAnalyzer();

            var smells = await cohesionAnalyzer.DetectCohesionSmellsAsync(document);
            var targetSmell = smells.FirstOrDefault(s => s.TargetName == target) ?? new Refactoring.Models.CodeSmell
            {
                SmellType = Refactoring.Models.CodeSmellType.GodClass,
                Severity = Refactoring.Models.SmellSeverity.High,
                FilePath = document.FilePath ?? "",
                StartLine = 1,
                EndLine = 1,
                TargetName = target,
                Description = $"Manual refactoring target: {target}"
            };

            var applicableStrategies = strategy != null
                ? new List<Refactoring.Models.RefactoringType> { Enum.Parse<Refactoring.Models.RefactoringType>(strategy, ignoreCase: true) }
                : Enum.GetValues<Refactoring.Models.RefactoringType>().ToList();

            var opportunity = new Refactoring.Models.RefactoringOpportunity
            {
                Smell = targetSmell,
                DocumentId = document.Id,
                ApplicableStrategies = applicableStrategies,
                EstimatedComplexityReduction = 0,
                EstimatedCohesionImprovement = 0,
                Recommendation = ""
            };

            var comparison = await orchestrator.CompareStrategiesAsync(project, opportunity);
            var result = await orchestrator.ApplyBestStrategyAsync(comparison);

            if (result.Success)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"Successfully applied {result.StrategyResult.StrategyName}");
                Console.ResetColor();
                Console.WriteLine($"  Score: {result.StrategyResult.Score.OverallRefactoringScore:F1}");
                Console.WriteLine($"  Modified: {string.Join(", ", result.ModifiedFiles.Select(Path.GetFileName))}");

                if (!string.IsNullOrEmpty(result.BackupId))
                {
                    Console.WriteLine($"  Backup: {result.BackupId}");
                    Console.WriteLine("  Run with --rollback to undo changes.");
                }
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Refactoring failed: {result.Error}");
                Console.ResetColor();
            }

            return result.Success ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: {ex.Message}");
            Console.ResetColor();
            return 1;
        }
    }

    static async Task<int> HandleRefactorChain(string projectPath, string filePath, string target, string chainType)
    {
        try
        {
            AnalysisService.EnsureMSBuildRegistered();
            Console.WriteLine($"Applying refactoring chain ({chainType}) to {target}...");
            Console.WriteLine();

            var service = new AnalysisService();
            var project = await service.OpenProjectAsync(projectPath);

            var document = project.Documents.FirstOrDefault(d =>
                d.FilePath?.EndsWith(filePath, StringComparison.OrdinalIgnoreCase) == true ||
                d.FilePath == filePath);

            if (document == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"File not found: {filePath}");
                Console.ResetColor();
                return 1;
            }

            var workspaceManager = new VirtualWorkspace.VirtualWorkspaceManager();
            var projectDir = Path.GetDirectoryName(project.FilePath) ?? Environment.CurrentDirectory;
            var backupService = new BackupService(projectDir);
            var orchestrator = new Refactoring.RefactoringOrchestrator(workspaceManager, backupService);
            var composer = new Refactoring.Composition.StrategyComposer();

            var chain = chainType.ToLowerInvariant() switch
            {
                "godclass" => composer.ComposeForGodClass(),
                "longmethod" => composer.ComposeForLongMethod(),
                "testability" => composer.ComposeForTestability(),
                "complexity" => composer.ComposeForComplexity(),
                _ => composer.ComposeForGodClass()
            };

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"Chain: {chain.Description}");
            Console.WriteLine($"Steps: {string.Join(" -> ", chain.Strategies)}");
            Console.ResetColor();
            Console.WriteLine();

            var result = await orchestrator.ApplyStrategyChainAsync(project, document.Id, chain);

            if (result.Success)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"Successfully completed {result.StepsCompleted}/{result.TotalSteps} steps");
                Console.ResetColor();

                if (result.FinalScore != null)
                {
                    Console.WriteLine($"  Final Score: {result.FinalScore.OverallRefactoringScore:F1}");
                    Console.WriteLine($"  Cohesion Improvement: {result.FinalScore.CohesionImprovement:+0.0;-0.0;0}");
                }

                foreach (var step in result.StepResults)
                {
                    Console.ForegroundColor = step.Success ? ConsoleColor.Green : ConsoleColor.Red;
                    Console.Write($"  [{(step.Success ? "OK" : "FAIL")}] ");
                    Console.ResetColor();
                    Console.WriteLine($"{step.StrategyResult.StrategyName}: {step.StrategyResult.Score.OverallRefactoringScore:F1}");
                }
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Chain stopped at step: {result.StoppedAtStep}");
                Console.WriteLine($"Reason: {result.StopReason}");
                Console.ResetColor();
            }

            return result.Success ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: {ex.Message}");
            Console.ResetColor();
            return 1;
        }
    }

    record DeprecatedItem
    {
        public required ISymbol Symbol { get; init; }
        public required string? FilePath { get; init; }
        public required int Line { get; init; }
        public required string Message { get; init; }
        public required bool IsError { get; init; }
        public string? Replacement { get; init; }
    }
}
