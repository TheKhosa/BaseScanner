using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using BaseScanner.Context;
using BaseScanner.Services;
using BaseScanner.Analyzers.Optimizations;
using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace BaseScanner.Analyzers;

/// <summary>
/// Main analyzer for detecting optimization opportunities and generating refactored code.
/// </summary>
public class OptimizationAnalyzer
{
    private readonly List<IOptimizationDetector> _detectors;

    public OptimizationAnalyzer()
    {
        _detectors = new List<IOptimizationDetector>
        {
            new LinqOptimizationDetector(),
            new CollectionOptimizationDetector(),
            new AsyncPatternDetector(),
            new ModernCSharpDetector()
        };
    }

    /// <summary>
    /// Analyze a project for optimization opportunities.
    /// </summary>
    public async Task<OptimizationResult> AnalyzeAsync(Project project)
    {
        var opportunities = new ConcurrentBag<OptimizationOpportunity>();
        var compilation = await project.GetCompilationAsync();
        if (compilation == null)
            return CreateEmptyResult();

        // Build codebase context for cross-file analysis
        var context = await BuildCodeContextAsync(project, compilation);

        // Analyze each document
        await Parallel.ForEachAsync(
            project.Documents,
            new ParallelOptions { MaxDegreeOfParallelism = 4 },
            async (document, ct) =>
            {
                if (document.FilePath == null ||
                    document.FilePath.Contains(".Designer.cs") ||
                    document.FilePath.Contains(".g.cs"))
                    return;

                var semanticModel = await document.GetSemanticModelAsync(ct);
                var syntaxRoot = await document.GetSyntaxRootAsync(ct);
                if (semanticModel == null || syntaxRoot == null)
                    return;

                // Run all detectors on this document
                foreach (var detector in _detectors)
                {
                    try
                    {
                        var detectedOpportunities = await detector.DetectAsync(
                            document, semanticModel, syntaxRoot, context);

                        foreach (var opp in detectedOpportunities)
                        {
                            opportunities.Add(opp);
                        }
                    }
                    catch (Exception)
                    {
                        // Continue with other detectors if one fails
                    }
                }
            });

        return BuildResult(opportunities.ToList(), project);
    }

    private async Task<CodeContext> BuildCodeContextAsync(Project project, Compilation compilation)
    {
        var callGraph = new CallGraph();
        var methods = new ConcurrentDictionary<string, MethodContext>();
        var types = new ConcurrentDictionary<string, TypeContext>();

        await Parallel.ForEachAsync(
            project.Documents,
            new ParallelOptions { MaxDegreeOfParallelism = 4 },
            async (document, ct) =>
            {
                if (document.FilePath == null) return;

                var semanticModel = await document.GetSemanticModelAsync(ct);
                var syntaxRoot = await document.GetSyntaxRootAsync(ct);
                if (semanticModel == null || syntaxRoot == null) return;

                // Analyze methods
                foreach (var method in syntaxRoot.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    var methodSymbol = semanticModel.GetDeclaredSymbol(method);
                    if (methodSymbol == null) continue;

                    var methodKey = GetMethodKey(methodSymbol);
                    callGraph.AddMethod(methodKey);

                    // Find method calls
                    foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
                    {
                        var invokedSymbol = semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                        if (invokedSymbol != null)
                        {
                            var calleeKey = GetMethodKey(invokedSymbol);
                            callGraph.AddEdge(methodKey, calleeKey);
                        }
                    }

                    // Build method context
                    var methodContext = BuildMethodContext(method, methodSymbol, semanticModel, document.FilePath);
                    methods[methodKey] = methodContext;
                }

                // Analyze types
                foreach (var typeDecl in syntaxRoot.DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    var typeSymbol = semanticModel.GetDeclaredSymbol(typeDecl);
                    if (typeSymbol == null) continue;

                    var typeContext = BuildTypeContext(typeDecl, typeSymbol, document.FilePath);
                    types[typeSymbol.ToDisplayString()] = typeContext;
                }
            });

        return new CodeContext
        {
            ProjectPath = project.FilePath ?? "",
            BuiltAt = DateTime.UtcNow,
            Methods = methods.ToImmutableDictionary(),
            Types = types.ToImmutableDictionary(),
            CallGraph = callGraph,
            UsageMetrics = BuildUsageMetrics(callGraph, methods)
        };
    }

    private MethodContext BuildMethodContext(
        MethodDeclarationSyntax method,
        IMethodSymbol symbol,
        SemanticModel semanticModel,
        string filePath)
    {
        var lineSpan = method.GetLocation().GetLineSpan();

        return new MethodContext
        {
            FullyQualifiedName = GetMethodKey(symbol),
            Name = symbol.Name,
            ContainingTypeName = symbol.ContainingType?.ToDisplayString() ?? "",
            FilePath = filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            ReturnType = symbol.ReturnType.ToDisplayString(),
            IsAsync = symbol.IsAsync,
            IsStatic = symbol.IsStatic,
            Accessibility = MapAccessibility(symbol.DeclaredAccessibility),
            Parameters = symbol.Parameters.Select(p => new ParameterInfo
            {
                Name = p.Name,
                Type = p.Type.ToDisplayString(),
                IsRef = p.RefKind == RefKind.Ref,
                IsOut = p.RefKind == RefKind.Out,
                IsParams = p.IsParams,
                HasDefaultValue = p.HasExplicitDefaultValue
            }).ToImmutableList(),
            Semantics = InferSemantics(method, symbol, semanticModel),
            CyclomaticComplexity = CalculateCyclomaticComplexity(method),
            LineCount = lineSpan.EndLinePosition.Line - lineSpan.StartLinePosition.Line + 1
        };
    }

    private TypeContext BuildTypeContext(
        TypeDeclarationSyntax typeDecl,
        INamedTypeSymbol symbol,
        string filePath)
    {
        var lineSpan = typeDecl.GetLocation().GetLineSpan();

        return new TypeContext
        {
            FullyQualifiedName = symbol.ToDisplayString(),
            Name = symbol.Name,
            FilePath = filePath,
            Line = lineSpan.StartLinePosition.Line + 1,
            Kind = MapTypeKind(symbol.TypeKind),
            Accessibility = MapAccessibility(symbol.DeclaredAccessibility),
            BaseTypes = symbol.AllInterfaces
                .Concat(symbol.BaseType != null ? new[] { symbol.BaseType } : Array.Empty<INamedTypeSymbol>())
                .Select(t => t.ToDisplayString())
                .ToImmutableList(),
            Methods = symbol.GetMembers()
                .OfType<IMethodSymbol>()
                .Select(m => GetMethodKey(m))
                .ToImmutableList()
        };
    }

    private MethodSemantics InferSemantics(
        MethodDeclarationSyntax method,
        IMethodSymbol symbol,
        SemanticModel semanticModel)
    {
        var body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
        var purpose = InferPurpose(symbol.Name);
        var behaviors = new List<MethodBehavior>();

        if (body != null)
        {
            // Detect behaviors
            if (body.DescendantNodes().OfType<AwaitExpressionSyntax>().Any())
                behaviors.Add(MethodBehavior.AwaitsAsync);

            if (body.DescendantNodes().OfType<ThrowStatementSyntax>().Any() ||
                body.DescendantNodes().OfType<ThrowExpressionSyntax>().Any())
                behaviors.Add(MethodBehavior.ThrowsExceptions);

            if (body.DescendantNodes().OfType<CatchClauseSyntax>().Any())
                behaviors.Add(MethodBehavior.CatchesExceptions);

            if (body.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Any())
                behaviors.Add(MethodBehavior.CreatesObjects);

            // Check for LINQ usage
            var hasLinq = body.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Any(inv => IsLinqMethod(inv, semanticModel));
            if (hasLinq)
                behaviors.Add(MethodBehavior.UsesLinq);
        }

        return new MethodSemantics
        {
            Purpose = purpose,
            Behaviors = behaviors.ToImmutableList(),
            IsPure = behaviors.Count == 0 && purpose is MethodPurpose.Getter or MethodPurpose.Calculator,
            HasSideEffects = behaviors.Contains(MethodBehavior.WritesState)
        };
    }

    private static MethodPurpose InferPurpose(string name)
    {
        if (name.StartsWith("Get") || name.StartsWith("Fetch") || name.StartsWith("Read") || name.StartsWith("Load"))
            return MethodPurpose.Getter;
        if (name.StartsWith("Set") || name.StartsWith("Update") || name.StartsWith("Write") || name.StartsWith("Save"))
            return MethodPurpose.Setter;
        if (name.StartsWith("Create") || name.StartsWith("Build") || name.StartsWith("New") || name.StartsWith("Make"))
            return MethodPurpose.Factory;
        if (name.StartsWith("Validate") || name.StartsWith("Check") || name.StartsWith("Is") || name.StartsWith("Has") || name.StartsWith("Can"))
            return MethodPurpose.Validator;
        if (name.StartsWith("Calculate") || name.StartsWith("Compute") || name.StartsWith("Parse") || name.StartsWith("Convert"))
            return MethodPurpose.Calculator;
        if (name.StartsWith("Handle") || name.StartsWith("On"))
            return MethodPurpose.EventHandler;
        if (name == "Dispose" || name.StartsWith("Cleanup"))
            return MethodPurpose.Disposer;
        if (name.StartsWith("Initialize") || name.StartsWith("Setup") || name.StartsWith("Configure"))
            return MethodPurpose.Initializer;
        if (name.StartsWith("Log"))
            return MethodPurpose.Logger;

        return MethodPurpose.Unknown;
    }

    private static bool IsLinqMethod(InvocationExpressionSyntax invocation, SemanticModel semanticModel)
    {
        var symbol = semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        if (symbol == null) return false;

        var containingNamespace = symbol.ContainingNamespace?.ToDisplayString() ?? "";
        return containingNamespace.StartsWith("System.Linq");
    }

    private static int CalculateCyclomaticComplexity(MethodDeclarationSyntax method)
    {
        var complexity = 1; // Base complexity

        var body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
        if (body == null) return complexity;

        foreach (var node in body.DescendantNodes())
        {
            switch (node)
            {
                case IfStatementSyntax:
                case ConditionalExpressionSyntax:
                case CaseSwitchLabelSyntax:
                case CasePatternSwitchLabelSyntax:
                case ForStatementSyntax:
                case ForEachStatementSyntax:
                case WhileStatementSyntax:
                case DoStatementSyntax:
                case CatchClauseSyntax:
                    complexity++;
                    break;
                case BinaryExpressionSyntax binary when
                    binary.IsKind(SyntaxKind.LogicalAndExpression) ||
                    binary.IsKind(SyntaxKind.LogicalOrExpression) ||
                    binary.IsKind(SyntaxKind.CoalesceExpression):
                    complexity++;
                    break;
            }
        }

        return complexity;
    }

    private static ImmutableDictionary<string, UsageMetrics> BuildUsageMetrics(
        CallGraph callGraph,
        ConcurrentDictionary<string, MethodContext> methods)
    {
        var metrics = new Dictionary<string, UsageMetrics>();
        var entryPoints = callGraph.GetEntryPoints();

        foreach (var methodKey in methods.Keys)
        {
            var callerCount = callGraph.GetCallerCount(methodKey);
            var calleeCount = callGraph.GetCalleeCount(methodKey);
            var depth = callGraph.GetDepthFromEntryPoints(methodKey);
            var isEntryPoint = entryPoints.Contains(methodKey);

            // Calculate importance score
            var score = 0.0;
            var reason = ImportanceReason.Unknown;

            if (isEntryPoint)
            {
                score = 100;
                reason = ImportanceReason.EntryPoint;
            }
            else if (callerCount > 10)
            {
                score = 80 + Math.Min(callerCount, 20);
                reason = ImportanceReason.HighlyReferenced;
            }
            else if (depth <= 2)
            {
                score = 70;
                reason = ImportanceReason.OnCriticalPath;
            }
            else
            {
                score = Math.Max(0, 50 - depth * 5);
            }

            metrics[methodKey] = new UsageMetrics
            {
                ReferenceCount = callerCount,
                CallerCount = callerCount,
                CalleeCount = calleeCount,
                DistanceFromEntryPoints = depth,
                OnCriticalPath = depth <= 2,
                ImportanceScore = score,
                ImportanceReason = reason
            };
        }

        return metrics.ToImmutableDictionary();
    }

    private static string GetMethodKey(IMethodSymbol symbol) =>
        $"{symbol.ContainingType?.ToDisplayString() ?? ""}.{symbol.Name}({string.Join(", ", symbol.Parameters.Select(p => p.Type.ToDisplayString()))})";

    private static AccessModifier MapAccessibility(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => AccessModifier.Public,
        Accessibility.Internal => AccessModifier.Internal,
        Accessibility.Protected => AccessModifier.Protected,
        Accessibility.ProtectedOrInternal => AccessModifier.ProtectedInternal,
        Accessibility.Private => AccessModifier.Private,
        Accessibility.ProtectedAndInternal => AccessModifier.PrivateProtected,
        _ => AccessModifier.Private
    };

    private static Context.TypeKind MapTypeKind(Microsoft.CodeAnalysis.TypeKind typeKind) => typeKind switch
    {
        Microsoft.CodeAnalysis.TypeKind.Class => Context.TypeKind.Class,
        Microsoft.CodeAnalysis.TypeKind.Struct => Context.TypeKind.Struct,
        Microsoft.CodeAnalysis.TypeKind.Interface => Context.TypeKind.Interface,
        Microsoft.CodeAnalysis.TypeKind.Enum => Context.TypeKind.Enum,
        Microsoft.CodeAnalysis.TypeKind.Delegate => Context.TypeKind.Delegate,
        _ => Context.TypeKind.Class
    };

    private OptimizationResult BuildResult(List<OptimizationOpportunity> opportunities, Project project)
    {
        var projectDirectory = Path.GetDirectoryName(project.FilePath) ?? "";

        var items = opportunities
            .OrderByDescending(o => o.Impact)
            .ThenByDescending(o => o.Confidence)
            .Take(100)
            .Select(o => new OptimizationItem
            {
                Category = o.Category,
                Type = o.Type,
                FilePath = !string.IsNullOrEmpty(o.FilePath) ? Path.GetRelativePath(projectDirectory, o.FilePath) : "",
                StartLine = o.StartLine,
                EndLine = o.EndLine,
                Description = o.Description,
                CurrentCode = o.CurrentCode,
                SuggestedCode = o.SuggestedCode,
                Confidence = o.Confidence.ToString(),
                Impact = o.Impact.ToString(),
                IsSemanticallySafe = o.IsSemanticallySafe,
                Assumptions = o.Assumptions,
                Risks = o.Risks
            })
            .ToList();

        var summary = new OptimizationSummary
        {
            TotalOpportunities = items.Count,
            HighConfidenceCount = items.Count(i => i.Confidence == "High"),
            PerformanceOptimizations = items.Count(i => i.Category == "Performance"),
            ReadabilityImprovements = items.Count(i => i.Category == "Readability"),
            ModernizationOpportunities = items.Count(i => i.Category == "Modernization"),
            EstimatedImpactScore = items.Any() ? items.Average(i => ImpactToScore(i.Impact)) : 0
        };

        return new OptimizationResult
        {
            Opportunities = items,
            Summary = summary
        };
    }

    private static double ImpactToScore(string impact) => impact switch
    {
        "Critical" => 100,
        "High" => 75,
        "Medium" => 50,
        "Low" => 25,
        _ => 0
    };

    private static OptimizationResult CreateEmptyResult() => new()
    {
        Opportunities = [],
        Summary = new OptimizationSummary()
    };
}

/// <summary>
/// Internal representation of an optimization opportunity.
/// </summary>
public record OptimizationOpportunity
{
    public required string Category { get; init; }
    public required string Type { get; init; }
    public required string FilePath { get; init; }
    public required int StartLine { get; init; }
    public required int EndLine { get; init; }
    public required string Description { get; init; }
    public required string CurrentCode { get; init; }
    public required string SuggestedCode { get; init; }
    public required OptimizationConfidence Confidence { get; init; }
    public required OptimizationImpact Impact { get; init; }
    public bool IsSemanticallySafe { get; init; } = true;
    public List<string> Assumptions { get; init; } = [];
    public List<string> Risks { get; init; } = [];
}

public enum OptimizationConfidence
{
    High,
    Medium,
    Low
}

public enum OptimizationImpact
{
    Critical,
    High,
    Medium,
    Low
}
