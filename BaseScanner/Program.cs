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
using BaseScanner.Services;

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
            Console.WriteLine("Options:");
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
            Console.WriteLine("  --all           Run all analyses");
            Console.WriteLine();
            Console.WriteLine("  --mcp           Run as MCP server for Claude Code integration");
            return 1;
        }

        var projectPath = args[0];
        var runAll = args.Contains("--all", StringComparer.OrdinalIgnoreCase);

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
            OptimizationAnalysis = runAll || args.Contains("--optimize", StringComparer.OrdinalIgnoreCase)
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
            return modes;
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
