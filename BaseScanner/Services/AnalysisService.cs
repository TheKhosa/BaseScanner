using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using BaseScanner.Analyzers;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace BaseScanner.Services;

public record AnalysisOptions
{
    public bool UnusedFiles { get; init; } = true;
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

    public static AnalysisOptions All => new()
    {
        UnusedFiles = true,
        DeepAnalysis = true,
        SentimentAnalysis = true,
        PerformanceAnalysis = true,
        ExceptionAnalysis = true,
        ResourceAnalysis = true,
        DependencyAnalysis = true,
        MagicValueAnalysis = true,
        GitAnalysis = true,
        RefactoringAnalysis = true,
        ArchitectureAnalysis = true,
        SafetyAnalysis = true,
        OptimizationAnalysis = true
    };

    public static AnalysisOptions Parse(string analyses)
    {
        if (string.IsNullOrWhiteSpace(analyses))
            return new AnalysisOptions();

        var parts = analyses.ToLowerInvariant().Split(',', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Contains("all"))
            return All;

        return new AnalysisOptions
        {
            UnusedFiles = parts.Contains("unused_files") || parts.Length == 0,
            DeepAnalysis = parts.Contains("deep"),
            SentimentAnalysis = parts.Contains("sentiment"),
            PerformanceAnalysis = parts.Contains("perf"),
            ExceptionAnalysis = parts.Contains("exceptions"),
            ResourceAnalysis = parts.Contains("resources"),
            DependencyAnalysis = parts.Contains("deps"),
            MagicValueAnalysis = parts.Contains("magic"),
            GitAnalysis = parts.Contains("git"),
            RefactoringAnalysis = parts.Contains("refactor"),
            ArchitectureAnalysis = parts.Contains("arch"),
            SafetyAnalysis = parts.Contains("safety"),
            OptimizationAnalysis = parts.Contains("optimize") || parts.Contains("optimizations")
        };
    }
}

public class AnalysisService
{
    private static bool _msBuildRegistered = false;
    private static readonly object _registrationLock = new();

    public static void EnsureMSBuildRegistered()
    {
        lock (_registrationLock)
        {
            if (!_msBuildRegistered)
            {
                MSBuildLocator.RegisterDefaults();
                _msBuildRegistered = true;
            }
        }
    }

    public async Task<AnalysisResult> AnalyzeAsync(string projectPath, AnalysisOptions options)
    {
        EnsureMSBuildRegistered();

        // Resolve project path
        if (Directory.Exists(projectPath))
        {
            var csprojFiles = Directory.GetFiles(projectPath, "*.csproj");
            if (csprojFiles.Length == 0)
                throw new ArgumentException($"No .csproj file found in: {projectPath}");
            if (csprojFiles.Length > 1)
                throw new ArgumentException($"Multiple .csproj files found in: {projectPath}. Please specify one.");
            projectPath = csprojFiles[0];
        }

        if (!File.Exists(projectPath))
            throw new FileNotFoundException($"Project file not found: {projectPath}");

        var projectDirectory = Path.GetDirectoryName(projectPath)!;

        // Get all .cs files on disk
        var allCsFilesOnDisk = Directory.GetFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetFullPath(f))
            .Where(f => !f.Contains(Path.Combine(projectDirectory, "obj") + Path.DirectorySeparatorChar) &&
                        !f.Contains(Path.Combine(projectDirectory, "bin") + Path.DirectorySeparatorChar))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Load project
        using var workspace = MSBuildWorkspace.Create();
#pragma warning disable CS0618
        workspace.WorkspaceFailed += (sender, e) => { /* Suppress warnings */ };
#pragma warning restore CS0618

        var project = await workspace.OpenProjectAsync(projectPath);

        var compiledFiles = project.Documents
            .Where(d => d.FilePath != null)
            .Select(d => Path.GetFullPath(d.FilePath!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Build result
        var summary = new AnalysisSummary
        {
            TotalFilesOnDisk = allCsFilesOnDisk.Count,
            FilesInCompilation = compiledFiles.Count
        };

        var unusedFiles = allCsFilesOnDisk.Where(f => !compiledFiles.Contains(f))
            .Select(f => Path.GetRelativePath(projectDirectory, f)).ToList();
        var missingFiles = compiledFiles.Where(f => !File.Exists(f))
            .Select(f => Path.GetRelativePath(projectDirectory, f)).ToList();

        var result = new AnalysisResult
        {
            ProjectPath = projectPath,
            Summary = summary with
            {
                UnusedFiles = unusedFiles.Count,
                MissingFiles = missingFiles.Count
            },
            UnusedFiles = unusedFiles,
            MissingFiles = missingFiles
        };

        // Run optional analyses
        if (options.DeepAnalysis)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation != null)
            {
                var (deprecated, dead, lowUsage) = await AnalyzeDeepAsync(project, compilation, projectDirectory);
                result = result with
                {
                    DeprecatedCode = deprecated,
                    DeadCode = dead,
                    LowUsageCode = lowUsage
                };
            }
        }

        if (options.SentimentAnalysis)
        {
            result = result with { Sentiment = await AnalyzeSentimentAsync(project, projectDirectory) };
        }

        if (options.PerformanceAnalysis)
        {
            var analyzer = new AsyncPerformanceAnalyzer();
            var issues = await analyzer.AnalyzeAsync(project);
            result = result with
            {
                PerformanceIssues = issues.Select(i => new IssueItem
                {
                    Type = i.Type,
                    Severity = i.Severity,
                    Message = i.Message,
                    FilePath = Path.GetRelativePath(projectDirectory, i.FilePath),
                    Line = i.Line,
                    CodeSnippet = i.CodeSnippet
                }).ToList()
            };
        }

        if (options.ExceptionAnalysis)
        {
            var analyzer = new ExceptionHandlingAnalyzer();
            var issues = await analyzer.AnalyzeAsync(project);
            result = result with
            {
                ExceptionHandlingIssues = issues.Select(i => new IssueItem
                {
                    Type = i.Type,
                    Severity = i.Severity,
                    Message = i.Message,
                    FilePath = Path.GetRelativePath(projectDirectory, i.FilePath),
                    Line = i.Line,
                    CodeSnippet = i.CodeSnippet
                }).ToList()
            };
        }

        if (options.ResourceAnalysis)
        {
            var analyzer = new ResourceLeakAnalyzer();
            var issues = await analyzer.AnalyzeAsync(project);
            result = result with
            {
                ResourceLeakIssues = issues.Select(i => new IssueItem
                {
                    Type = i.Type,
                    Severity = i.Severity,
                    Message = i.Message,
                    FilePath = Path.GetRelativePath(projectDirectory, i.FilePath),
                    Line = i.Line,
                    CodeSnippet = i.CodeSnippet
                }).ToList()
            };
        }

        if (options.DependencyAnalysis)
        {
            var analyzer = new DependencyAnalyzer();
            var (issues, metrics, cycles) = await analyzer.AnalyzeAsync(project);
            result = result with
            {
                Dependencies = new DependencyResult
                {
                    CircularDependencies = cycles.Select(c => new CircularDependencyItem
                    {
                        Type = c.Type,
                        Cycle = c.Cycle
                    }).ToList(),
                    HighCouplingTypes = metrics.Where(m => m.EfferentCoupling > 10)
                        .OrderByDescending(m => m.EfferentCoupling)
                        .Take(20)
                        .Select(m => new CouplingItem
                        {
                            TypeName = m.TypeName,
                            FilePath = !string.IsNullOrEmpty(m.FilePath) ? Path.GetRelativePath(projectDirectory, m.FilePath) : "",
                            EfferentCoupling = m.EfferentCoupling,
                            AfferentCoupling = m.AfferentCoupling,
                            Instability = m.Instability
                        }).ToList()
                }
            };
        }

        if (options.MagicValueAnalysis)
        {
            var analyzer = new MagicValueAnalyzer();
            var issues = await analyzer.AnalyzeAsync(project);
            result = result with
            {
                MagicValues = issues.GroupBy(i => (i.Type, i.Value))
                    .Select(g => new MagicValueItem
                    {
                        Type = g.Key.Type,
                        Value = g.Key.Value,
                        Occurrences = g.First().Occurrences,
                        Locations = g.Select(i => new LocationItem
                        {
                            FilePath = Path.GetRelativePath(projectDirectory, i.FilePath),
                            Line = i.Line
                        }).Take(5).ToList()
                    }).Take(30).ToList()
            };
        }

        if (options.GitAnalysis)
        {
            var analyzer = new GitChurnAnalyzer();
            var (issues, churns, hotspots, gitAvailable) = await analyzer.AnalyzeAsync(projectDirectory);
            result = result with
            {
                GitChurn = new GitChurnResult
                {
                    GitAvailable = gitAvailable,
                    TopChurnedFiles = churns.Take(20).Select(c => new FileChurnItem
                    {
                        FilePath = c.RelativePath,
                        CommitCount = c.CommitCount,
                        TotalChurn = c.TotalChurn,
                        DaysSinceLastChange = c.DaysSinceLastChange
                    }).ToList(),
                    Hotspots = hotspots.Take(15).Select(h => new HotspotItem
                    {
                        FilePath = h.FilePath,
                        Score = h.HotspotScore,
                        ChurnCount = h.ChurnCount,
                        Reason = h.Reason
                    }).ToList(),
                    StaleFiles = churns.Where(c => c.DaysSinceLastChange > 365)
                        .OrderByDescending(c => c.DaysSinceLastChange)
                        .Take(15)
                        .Select(c => new FileChurnItem
                        {
                            FilePath = c.RelativePath,
                            CommitCount = c.CommitCount,
                            TotalChurn = c.TotalChurn,
                            DaysSinceLastChange = c.DaysSinceLastChange
                        }).ToList()
                }
            };
        }

        if (options.RefactoringAnalysis)
        {
            var analyzer = new RefactoringAnalyzer();
            var (longMethods, godClasses, featureEnvies, parameterSmells, dataClumps) = await analyzer.AnalyzeAsync(project);
            result = result with
            {
                Refactoring = new RefactoringResult
                {
                    LongMethods = longMethods.Take(30).Select(m => new LongMethodItem
                    {
                        FilePath = Path.GetRelativePath(projectDirectory, m.FilePath),
                        Line = m.StartLine,
                        ClassName = m.ClassName,
                        MethodName = m.MethodName,
                        LineCount = m.LineCount,
                        Complexity = m.CyclomaticComplexity,
                        ExtractCandidates = m.ExtractCandidates.Take(5).Select(c => new ExtractCandidateItem
                        {
                            StartLine = c.StartLine,
                            EndLine = c.EndLine,
                            SuggestedName = c.SuggestedName,
                            Reason = c.Reason
                        }).ToList()
                    }).ToList(),
                    GodClasses = godClasses.Take(15).Select(g => new GodClassItem
                    {
                        FilePath = Path.GetRelativePath(projectDirectory, g.FilePath),
                        Line = g.Line,
                        ClassName = g.ClassName,
                        MethodCount = g.MethodCount,
                        FieldCount = g.FieldCount,
                        LCOM = g.LCOM,
                        Responsibilities = g.Responsibilities.Take(5).ToList()
                    }).ToList(),
                    FeatureEnvy = featureEnvies.Take(20).Select(e => new FeatureEnvyItem
                    {
                        FilePath = Path.GetRelativePath(projectDirectory, e.FilePath),
                        Line = e.Line,
                        ClassName = e.ClassName,
                        MethodName = e.MethodName,
                        EnviedClass = e.EnviedClass,
                        EnviedMemberAccess = e.EnviedMemberAccess,
                        EnvyRatio = e.EnvyRatio
                    }).ToList(),
                    ParameterSmells = parameterSmells.Take(20).Select(p => new ParameterSmellItem
                    {
                        FilePath = Path.GetRelativePath(projectDirectory, p.FilePath),
                        Line = p.Line,
                        ClassName = p.ClassName,
                        MethodName = p.MethodName,
                        ParameterCount = p.ParameterCount,
                        SmellType = p.SmellType,
                        Suggestion = p.Suggestion
                    }).ToList(),
                    DataClumps = dataClumps.Take(15).Select(d => new DataClumpItem
                    {
                        Parameters = d.Parameters,
                        Occurrences = d.Occurrences.Count,
                        SuggestedClassName = d.SuggestedClassName,
                        Locations = d.Occurrences.Take(5).Select(o => $"{o.ClassName}.{o.MethodName}").ToList()
                    }).ToList()
                }
            };
        }

        if (options.ArchitectureAnalysis)
        {
            var analyzer = new ArchitectureAnalyzer();
            var (publicApi, callGraph, inheritanceIssues, interfaceIssues) = await analyzer.AnalyzeAsync(project);
            var entryPoints = callGraph.Where(c => c.IsEntryPoint).ToList();
            var deadEnds = callGraph.Where(c => c.IsDeadEnd && !string.IsNullOrEmpty(c.FilePath)).ToList();

            result = result with
            {
                Architecture = new ArchitectureResult
                {
                    PublicApi = publicApi.Take(50).Select(a => new PublicApiItem
                    {
                        TypeName = a.TypeName,
                        MemberName = a.MemberName,
                        MemberType = a.MemberType,
                        FilePath = Path.GetRelativePath(projectDirectory, a.FilePath),
                        Line = a.Line,
                        BreakingChangeRisk = a.BreakingChangeRisk
                    }).ToList(),
                    EntryPoints = entryPoints.Take(20).Select(e => new EntryPointItem
                    {
                        TypeName = e.TypeName,
                        MethodName = e.MethodName,
                        OutgoingCalls = e.OutgoingCalls
                    }).ToList(),
                    DeadEnds = deadEnds.Take(15).Select(d => new DeadEndItem
                    {
                        FilePath = Path.GetRelativePath(projectDirectory, d.FilePath),
                        Line = d.Line,
                        TypeName = d.TypeName,
                        MethodName = d.MethodName,
                        IncomingCalls = d.IncomingCalls
                    }).ToList(),
                    DeepInheritance = inheritanceIssues.Where(i => i.InheritanceDepth > 3).Take(15).Select(i => new InheritanceItem
                    {
                        TypeName = i.TypeName,
                        Depth = i.InheritanceDepth,
                        Chain = i.InheritanceChain.Take(5).ToList()
                    }).ToList(),
                    CompositionCandidates = inheritanceIssues.Where(i => i.HasCompositionOpportunity).Take(15).Select(i => new CompositionCandidateItem
                    {
                        FilePath = Path.GetRelativePath(projectDirectory, i.FilePath),
                        Line = i.Line,
                        TypeName = i.TypeName,
                        Suggestion = i.CompositionSuggestion
                    }).ToList(),
                    InterfaceIssues = interfaceIssues.Take(15).Select(i => new InterfaceIssueItem
                    {
                        FilePath = Path.GetRelativePath(projectDirectory, i.FilePath),
                        Line = i.Line,
                        InterfaceName = i.InterfaceName,
                        MemberCount = i.MemberCount,
                        SuggestedSplits = i.SuggestedSplits
                    }).ToList()
                }
            };
        }

        if (options.SafetyAnalysis)
        {
            var analyzer = new CodeSafetyAnalyzer();
            var (nullIssues, immutabilityIssues, loggingGaps, loggingCoverage) = await analyzer.AnalyzeAsync(project);

            result = result with
            {
                Safety = new SafetyResult
                {
                    NullIssues = nullIssues.Take(50).Select(n => new NullSafetyItem
                    {
                        Type = n.Type,
                        Severity = n.Severity,
                        FilePath = Path.GetRelativePath(projectDirectory, n.FilePath),
                        Line = n.Line,
                        Description = n.Description
                    }).ToList(),
                    ImmutabilityIssues = immutabilityIssues.Take(50).Select(i => new ImmutabilityItem
                    {
                        Type = i.Type,
                        FilePath = Path.GetRelativePath(projectDirectory, i.FilePath),
                        Line = i.Line,
                        MemberName = i.MemberName,
                        Suggestion = i.Suggestion
                    }).ToList(),
                    LoggingGaps = loggingGaps.Take(30).Select(l => new LoggingGapItem
                    {
                        FilePath = Path.GetRelativePath(projectDirectory, l.FilePath),
                        Line = l.Line,
                        ClassName = l.ClassName,
                        MethodName = l.MethodName,
                        GapType = l.GapType,
                        Description = l.Description
                    }).ToList(),
                    AverageLoggingCoverage = loggingCoverage.Count > 0 ? loggingCoverage.Average(c => c.CoveragePercent) : 0,
                    ClassesWithLowCoverage = loggingCoverage.Count(c => c.CoveragePercent < 20)
                }
            };
        }

        if (options.OptimizationAnalysis)
        {
            var analyzer = new OptimizationAnalyzer();
            var optimizations = await analyzer.AnalyzeAsync(project);
            result = result with { Optimizations = optimizations };
        }

        // Update summary totals
        result = result with
        {
            Summary = result.Summary with
            {
                PerformanceIssues = result.PerformanceIssues?.Count ?? 0,
                ExceptionIssues = result.ExceptionHandlingIssues?.Count ?? 0,
                ResourceIssues = result.ResourceLeakIssues?.Count ?? 0,
                MagicValues = result.MagicValues?.Count ?? 0,
                LongMethods = result.Refactoring?.LongMethods.Count ?? 0,
                GodClasses = result.Refactoring?.GodClasses.Count ?? 0,
                NullSafetyIssues = result.Safety?.NullIssues.Count ?? 0,
                ImmutabilityOpportunities = result.Safety?.ImmutabilityIssues.Count ?? 0,
                LoggingGaps = result.Safety?.LoggingGaps.Count ?? 0,
                OptimizationOpportunities = result.Optimizations?.Opportunities.Count ?? 0,
                TotalIssues = (result.PerformanceIssues?.Count ?? 0) +
                              (result.ExceptionHandlingIssues?.Count ?? 0) +
                              (result.ResourceLeakIssues?.Count ?? 0) +
                              (result.Refactoring?.LongMethods.Count ?? 0) +
                              (result.Refactoring?.GodClasses.Count ?? 0) +
                              (result.Safety?.NullIssues.Count ?? 0) +
                              (result.Optimizations?.Opportunities.Count ?? 0)
            }
        };

        return result;
    }

    private async Task<(List<DeprecatedCodeItem>, List<UsageItem>, List<UsageItem>)> AnalyzeDeepAsync(
        Project project, Compilation compilation, string projectDirectory)
    {
        var deprecated = new List<DeprecatedCodeItem>();
        var usageCounts = new ConcurrentDictionary<ISymbol, int>(SymbolEqualityComparer.Default);
        var symbolLocations = new ConcurrentDictionary<ISymbol, (string FilePath, int Line, string Kind)>(SymbolEqualityComparer.Default);

        foreach (var document in project.Documents)
        {
            if (document.FilePath == null) continue;

            var semanticModel = await document.GetSemanticModelAsync();
            var syntaxRoot = await document.GetSyntaxRootAsync();
            if (semanticModel == null || syntaxRoot == null) continue;

            // Find deprecated items
            var declarations = syntaxRoot.DescendantNodes()
                .Where(n => n is TypeDeclarationSyntax or MethodDeclarationSyntax or PropertyDeclarationSyntax);

            foreach (var declaration in declarations)
            {
                ISymbol? symbol = declaration switch
                {
                    TypeDeclarationSyntax typeDecl => semanticModel.GetDeclaredSymbol(typeDecl),
                    MethodDeclarationSyntax methodDecl => semanticModel.GetDeclaredSymbol(methodDecl),
                    PropertyDeclarationSyntax propDecl => semanticModel.GetDeclaredSymbol(propDecl),
                    _ => null
                };

                if (symbol == null) continue;

                var obsoleteAttr = symbol.GetAttributes()
                    .FirstOrDefault(a => a.AttributeClass?.Name == "ObsoleteAttribute");

                if (obsoleteAttr != null)
                {
                    var message = obsoleteAttr.ConstructorArguments.FirstOrDefault().Value?.ToString() ?? "";
                    var isError = obsoleteAttr.ConstructorArguments.Length > 1 &&
                                 obsoleteAttr.ConstructorArguments[1].Value is true;

                    deprecated.Add(new DeprecatedCodeItem
                    {
                        SymbolKind = GetSymbolKind(symbol),
                        SymbolName = symbol.Name,
                        FilePath = Path.GetRelativePath(projectDirectory, document.FilePath),
                        Line = declaration.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        Message = message,
                        IsError = isError,
                        Replacement = ExtractReplacement(message)
                    });
                }

                // Track usage
                if (!IsGeneratedCode(symbol))
                {
                    usageCounts.TryAdd(symbol, 0);
                    symbolLocations.TryAdd(symbol, (document.FilePath, declaration.GetLocation().GetLineSpan().StartLinePosition.Line + 1, GetSymbolKind(symbol)));
                }
            }
        }

        // Count references
        foreach (var symbol in usageCounts.Keys.ToList())
        {
            try
            {
                var references = await SymbolFinder.FindReferencesAsync(symbol, project.Solution);
                usageCounts[symbol] = references.Sum(r => r.Locations.Count());
            }
            catch { }
        }

        var deadCode = usageCounts.Where(kv => kv.Value == 0)
            .Select(kv =>
            {
                var loc = symbolLocations.GetValueOrDefault(kv.Key);
                return new UsageItem
                {
                    SymbolKind = loc.Kind ?? "Unknown",
                    SymbolName = kv.Key.Name,
                    FilePath = !string.IsNullOrEmpty(loc.FilePath) ? Path.GetRelativePath(projectDirectory, loc.FilePath) : "",
                    Line = loc.Line,
                    ReferenceCount = 0
                };
            }).Where(u => !string.IsNullOrEmpty(u.FilePath)).Take(50).ToList();

        var lowUsage = usageCounts.Where(kv => kv.Value >= 1 && kv.Value <= 2)
            .Select(kv =>
            {
                var loc = symbolLocations.GetValueOrDefault(kv.Key);
                return new UsageItem
                {
                    SymbolKind = loc.Kind ?? "Unknown",
                    SymbolName = kv.Key.Name,
                    FilePath = !string.IsNullOrEmpty(loc.FilePath) ? Path.GetRelativePath(projectDirectory, loc.FilePath) : "",
                    Line = loc.Line,
                    ReferenceCount = kv.Value
                };
            }).Where(u => !string.IsNullOrEmpty(u.FilePath)).Take(50).ToList();

        return (deprecated, deadCode, lowUsage);
    }

    private async Task<SentimentResult> AnalyzeSentimentAsync(Project project, string projectDirectory)
    {
        var analyzer = new CodeSentimentAnalyzer();
        var blocks = await analyzer.AnalyzeProjectAsync(project);
        var similarGroups = analyzer.FindSimilarBlocks(blocks);

        var qualityDistribution = blocks.GroupBy(b => b.QualityRating)
            .ToDictionary(g => g.Key, g => g.Count());

        var markerCounts = blocks.SelectMany(b => b.SentimentMarkers)
            .GroupBy(m => m)
            .ToDictionary(g => g.Key, g => g.Count());

        return new SentimentResult
        {
            TotalBlocks = blocks.Count,
            AverageQualityScore = blocks.Count > 0 ? blocks.Average(b => b.QualityScore) : 0,
            AverageComplexity = blocks.Count > 0 ? blocks.Average(b => b.CyclomaticComplexity) : 0,
            HighComplexityCount = blocks.Count(b => b.CyclomaticComplexity > 15),
            ProblematicCount = blocks.Count(b => b.QualityScore < 40),
            DuplicateGroups = similarGroups.Count(g => g.SimilarityScore >= 1.0),
            SimilarGroups = similarGroups.Count(g => g.SimilarityScore < 1.0),
            QualityDistribution = qualityDistribution,
            MarkerCounts = markerCounts,
            ProblematicBlocks = blocks.Where(b => b.QualityScore < 40).Take(15).Select(b => new CodeBlockItem
            {
                FilePath = Path.GetRelativePath(projectDirectory, b.FilePath),
                Line = b.StartLine,
                BlockType = b.BlockType.ToString(),
                ContainingType = b.ContainingType,
                Name = b.Name,
                QualityScore = b.QualityScore,
                QualityRating = b.QualityRating,
                CyclomaticComplexity = b.CyclomaticComplexity,
                NestingDepth = b.NestingDepth,
                LineCount = b.LineCount,
                SentimentMarkers = b.SentimentMarkers
            }).ToList(),
            HighComplexityBlocks = blocks.Where(b => b.CyclomaticComplexity > 15).Take(15).Select(b => new CodeBlockItem
            {
                FilePath = Path.GetRelativePath(projectDirectory, b.FilePath),
                Line = b.StartLine,
                BlockType = b.BlockType.ToString(),
                ContainingType = b.ContainingType,
                Name = b.Name,
                QualityScore = b.QualityScore,
                QualityRating = b.QualityRating,
                CyclomaticComplexity = b.CyclomaticComplexity,
                NestingDepth = b.NestingDepth,
                LineCount = b.LineCount,
                SentimentMarkers = b.SentimentMarkers
            }).ToList()
        };
    }

    private static string GetSymbolKind(ISymbol symbol) => symbol.Kind switch
    {
        SymbolKind.NamedType => ((INamedTypeSymbol)symbol).TypeKind switch
        {
            TypeKind.Class => "Class",
            TypeKind.Interface => "Interface",
            TypeKind.Struct => "Struct",
            TypeKind.Enum => "Enum",
            _ => "Type"
        },
        SymbolKind.Method => "Method",
        SymbolKind.Property => "Property",
        SymbolKind.Field => "Field",
        _ => symbol.Kind.ToString()
    };

    private static bool IsGeneratedCode(ISymbol symbol) =>
        symbol.GetAttributes().Any(a =>
            a.AttributeClass?.Name == "GeneratedCodeAttribute" ||
            a.AttributeClass?.Name == "CompilerGeneratedAttribute");

    private static string? ExtractReplacement(string message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var match = Regex.Match(message, @"[Uu]se\s+(\w+(?:\.\w+)*)\s+instead", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }
}
