using Microsoft.CodeAnalysis;
using BaseScanner.Context;
using System.Collections.Concurrent;

namespace BaseScanner.Analyzers.Security;

/// <summary>
/// Main orchestrator for security vulnerability analysis.
/// </summary>
public class SecurityAnalyzer
{
    private readonly List<ISecurityDetector> _detectors;

    public SecurityAnalyzer()
    {
        _detectors = new List<ISecurityDetector>
        {
            new InjectionDetector(),
            new SecretDetector(),
            new CryptoAnalyzer(),
            new AuthenticationDetector(),
            new DeserializationDetector(),
            new PathTraversalDetector()
        };
    }

    /// <summary>
    /// Analyze a project for security vulnerabilities.
    /// </summary>
    public async Task<SecurityResult> AnalyzeAsync(Project project)
    {
        var vulnerabilities = new ConcurrentBag<SecurityVulnerability>();

        // Build code context for cross-file analysis
        var context = await BuildCodeContextAsync(project);

        // Analyze each document in parallel
        await Parallel.ForEachAsync(
            project.Documents,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            async (document, ct) =>
            {
                if (document.FilePath == null)
                    return;

                // Skip generated files
                if (IsGeneratedFile(document.FilePath))
                    return;

                var semanticModel = await document.GetSemanticModelAsync(ct);
                var syntaxRoot = await document.GetSyntaxRootAsync(ct);

                if (semanticModel == null || syntaxRoot == null)
                    return;

                // Run all detectors
                foreach (var detector in _detectors)
                {
                    try
                    {
                        var detected = await detector.DetectAsync(document, semanticModel, syntaxRoot, context);
                        foreach (var vuln in detected)
                        {
                            vulnerabilities.Add(vuln);
                        }
                    }
                    catch (Exception)
                    {
                        // Log but continue with other detectors
                    }
                }
            });

        var vulnerabilityList = vulnerabilities
            .OrderByDescending(v => GetSeverityOrder(v.Severity))
            .ThenBy(v => v.FilePath)
            .ThenBy(v => v.StartLine)
            .ToList();

        return new SecurityResult
        {
            Vulnerabilities = vulnerabilityList,
            Summary = BuildSummary(vulnerabilityList)
        };
    }

    private async Task<CodeContext> BuildCodeContextAsync(Project project)
    {
        // Build a simplified context for security analysis
        var callGraph = new CallGraph();
        var methods = new Dictionary<string, MethodContext>();

        foreach (var document in project.Documents)
        {
            if (document.FilePath == null)
                continue;

            var semanticModel = await document.GetSemanticModelAsync();
            var syntaxRoot = await document.GetSyntaxRootAsync();

            if (semanticModel == null || syntaxRoot == null)
                continue;

            // Extract method information for taint analysis
            foreach (var method in syntaxRoot.DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>())
            {
                var symbol = semanticModel.GetDeclaredSymbol(method);
                if (symbol == null)
                    continue;

                var fqn = symbol.ToDisplayString();
                callGraph.AddMethod(fqn);

                // Track method calls for taint propagation
                foreach (var invocation in method.DescendantNodes()
                    .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>())
                {
                    var calledSymbol = semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                    if (calledSymbol != null)
                    {
                        callGraph.AddEdge(fqn, calledSymbol.ToDisplayString());
                    }
                }
            }
        }

        return new CodeContext
        {
            ProjectPath = project.FilePath ?? "",
            BuiltAt = DateTime.UtcNow,
            CallGraph = callGraph
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

    private int GetSeverityOrder(string severity) => severity switch
    {
        "Critical" => 4,
        "High" => 3,
        "Medium" => 2,
        "Low" => 1,
        _ => 0
    };

    private SecuritySummary BuildSummary(List<SecurityVulnerability> vulnerabilities)
    {
        return new SecuritySummary
        {
            TotalVulnerabilities = vulnerabilities.Count,
            CriticalCount = vulnerabilities.Count(v => v.Severity == "Critical"),
            HighCount = vulnerabilities.Count(v => v.Severity == "High"),
            MediumCount = vulnerabilities.Count(v => v.Severity == "Medium"),
            LowCount = vulnerabilities.Count(v => v.Severity == "Low"),
            VulnerabilitiesByType = vulnerabilities
                .GroupBy(v => v.VulnerabilityType)
                .ToDictionary(g => g.Key, g => g.Count()),
            VulnerabilitiesByCwe = vulnerabilities
                .GroupBy(v => v.CweId)
                .ToDictionary(g => g.Key, g => g.Count())
        };
    }
}
