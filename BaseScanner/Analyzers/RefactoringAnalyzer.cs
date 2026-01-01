using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BaseScanner.Analyzers;

/// <summary>
/// Analyzes code for refactoring opportunities: long methods, extract candidates,
/// god classes, feature envy, and parameter objects.
/// </summary>
public class RefactoringAnalyzer
{
    public record MethodMetrics
    {
        public required string ClassName { get; init; }
        public required string MethodName { get; init; }
        public required string FilePath { get; init; }
        public required int StartLine { get; init; }
        public required int EndLine { get; init; }
        public required int LineCount { get; init; }
        public required int ParameterCount { get; init; }
        public required int LocalVariableCount { get; init; }
        public required int CyclomaticComplexity { get; init; }
        public required int StatementCount { get; init; }
        public required List<ExtractCandidate> ExtractCandidates { get; init; }
    }

    public record ExtractCandidate
    {
        public required int StartLine { get; init; }
        public required int EndLine { get; init; }
        public required string Reason { get; init; }
        public required string SuggestedName { get; init; }
        public required int StatementCount { get; init; }
    }

    public record GodClass
    {
        public required string ClassName { get; init; }
        public required string FilePath { get; init; }
        public required int Line { get; init; }
        public required int MethodCount { get; init; }
        public required int FieldCount { get; init; }
        public required int PropertyCount { get; init; }
        public required int LineCount { get; init; }
        public required double LCOM { get; init; } // Lack of Cohesion of Methods (0-1, higher = worse)
        public required int ResponsibilityCount { get; init; }
        public required List<string> Responsibilities { get; init; }
    }

    public record FeatureEnvy
    {
        public required string ClassName { get; init; }
        public required string MethodName { get; init; }
        public required string FilePath { get; init; }
        public required int Line { get; init; }
        public required string EnviedClass { get; init; }
        public required int OwnMemberAccess { get; init; }
        public required int EnviedMemberAccess { get; init; }
        public required double EnvyRatio { get; init; }
    }

    public record ParameterSmell
    {
        public required string ClassName { get; init; }
        public required string MethodName { get; init; }
        public required string FilePath { get; init; }
        public required int Line { get; init; }
        public required int ParameterCount { get; init; }
        public required List<string> Parameters { get; init; }
        public required string SmellType { get; init; } // "LongParameterList", "DataClump", "PrimitiveObsession"
        public required string Suggestion { get; init; }
    }

    public record DataClump
    {
        public required List<string> Parameters { get; init; }
        public required List<(string ClassName, string MethodName, string FilePath, int Line)> Occurrences { get; init; }
        public required string SuggestedClassName { get; init; }
    }

    public async Task<(List<MethodMetrics> LongMethods, List<GodClass> GodClasses,
        List<FeatureEnvy> FeatureEnvies, List<ParameterSmell> ParameterSmells,
        List<DataClump> DataClumps)> AnalyzeAsync(Project project)
    {
        var longMethods = new List<MethodMetrics>();
        var godClasses = new List<GodClass>();
        var featureEnvies = new List<FeatureEnvy>();
        var parameterSmells = new List<ParameterSmell>();
        var parameterSets = new Dictionary<string, List<(string ClassName, string MethodName, string FilePath, int Line)>>();

        foreach (var document in project.Documents)
        {
            if (document.FilePath == null) continue;
            if (document.FilePath.Contains(".Designer.cs")) continue;

            var syntaxRoot = await document.GetSyntaxRootAsync();
            var semanticModel = await document.GetSemanticModelAsync();
            if (syntaxRoot == null || semanticModel == null) continue;

            // Analyze methods for extraction candidates
            var methods = syntaxRoot.DescendantNodes().OfType<MethodDeclarationSyntax>();
            foreach (var method in methods)
            {
                var metrics = AnalyzeMethod(method, document.FilePath, semanticModel);
                if (metrics != null && (metrics.LineCount > 30 || metrics.ExtractCandidates.Count > 0))
                {
                    longMethods.Add(metrics);
                }

                // Check for feature envy
                var envy = DetectFeatureEnvy(method, document.FilePath, semanticModel);
                if (envy != null)
                {
                    featureEnvies.Add(envy);
                }

                // Collect parameter patterns for data clump detection
                var paramSmell = AnalyzeParameters(method, document.FilePath, semanticModel);
                if (paramSmell != null)
                {
                    parameterSmells.Add(paramSmell);

                    // Track parameter combinations for data clump detection
                    if (paramSmell.ParameterCount >= 3)
                    {
                        var paramKey = string.Join(",", paramSmell.Parameters.OrderBy(p => p));
                        if (!parameterSets.ContainsKey(paramKey))
                            parameterSets[paramKey] = new();
                        parameterSets[paramKey].Add((paramSmell.ClassName, paramSmell.MethodName, paramSmell.FilePath, paramSmell.Line));
                    }
                }
            }

            // Analyze classes for god class detection
            var classes = syntaxRoot.DescendantNodes().OfType<ClassDeclarationSyntax>();
            foreach (var classDecl in classes)
            {
                var godClass = AnalyzeForGodClass(classDecl, document.FilePath, semanticModel);
                if (godClass != null)
                {
                    godClasses.Add(godClass);
                }
            }
        }

        // Find data clumps
        var dataClumps = FindDataClumps(parameterSets);

        return (longMethods.OrderByDescending(m => m.LineCount).ToList(),
                godClasses.OrderByDescending(g => g.MethodCount + g.FieldCount).ToList(),
                featureEnvies.OrderByDescending(f => f.EnvyRatio).ToList(),
                parameterSmells.OrderByDescending(p => p.ParameterCount).ToList(),
                dataClumps);
    }

    private MethodMetrics? AnalyzeMethod(MethodDeclarationSyntax method, string filePath, SemanticModel semanticModel)
    {
        var body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
        if (body == null) return null;

        var lineSpan = method.GetLocation().GetLineSpan();
        var lineCount = lineSpan.EndLinePosition.Line - lineSpan.StartLinePosition.Line + 1;
        var className = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "Unknown";

        var statements = body.DescendantNodes().OfType<StatementSyntax>().ToList();
        var localVars = body.DescendantNodes().OfType<LocalDeclarationStatementSyntax>().Sum(l => l.Declaration.Variables.Count);

        var extractCandidates = FindExtractCandidates(method, body);

        return new MethodMetrics
        {
            ClassName = className,
            MethodName = method.Identifier.Text,
            FilePath = filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            LineCount = lineCount,
            ParameterCount = method.ParameterList.Parameters.Count,
            LocalVariableCount = localVars,
            CyclomaticComplexity = CalculateComplexity(body),
            StatementCount = statements.Count,
            ExtractCandidates = extractCandidates
        };
    }

    private List<ExtractCandidate> FindExtractCandidates(MethodDeclarationSyntax method, SyntaxNode body)
    {
        var candidates = new List<ExtractCandidate>();

        // Look for comment blocks that indicate logical sections
        var trivia = body.DescendantTrivia()
            .Where(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia))
            .ToList();

        foreach (var comment in trivia)
        {
            var commentText = comment.ToString().TrimStart('/').Trim();
            if (commentText.Length > 5 && !commentText.StartsWith("TODO") && !commentText.StartsWith("HACK"))
            {
                var line = comment.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                // Find statements after this comment
                var statementsAfter = body.DescendantNodes().OfType<StatementSyntax>()
                    .Where(s => s.GetLocation().GetLineSpan().StartLinePosition.Line >= line)
                    .Take(5)
                    .ToList();

                if (statementsAfter.Count >= 3)
                {
                    candidates.Add(new ExtractCandidate
                    {
                        StartLine = line,
                        EndLine = statementsAfter.Last().GetLocation().GetLineSpan().EndLinePosition.Line + 1,
                        Reason = "Commented section could be extracted",
                        SuggestedName = GenerateMethodName(commentText),
                        StatementCount = statementsAfter.Count
                    });
                }
            }
        }

        // Look for nested if/loop blocks
        var nestedBlocks = body.DescendantNodes()
            .Where(n => n is IfStatementSyntax or ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax)
            .Where(n => n.DescendantNodes().OfType<StatementSyntax>().Count() > 5);

        foreach (var block in nestedBlocks)
        {
            var blockLine = block.GetLocation().GetLineSpan();
            var stmtCount = block.DescendantNodes().OfType<StatementSyntax>().Count();

            if (stmtCount > 7)
            {
                candidates.Add(new ExtractCandidate
                {
                    StartLine = blockLine.StartLinePosition.Line + 1,
                    EndLine = blockLine.EndLinePosition.Line + 1,
                    Reason = "Complex nested block",
                    SuggestedName = block switch
                    {
                        IfStatementSyntax => "ProcessConditionalLogic",
                        ForStatementSyntax or ForEachStatementSyntax => "ProcessItems",
                        WhileStatementSyntax => "ProcessWhileCondition",
                        _ => "ExtractedMethod"
                    },
                    StatementCount = stmtCount
                });
            }
        }

        // Look for sequential related statements (e.g., multiple assignments to same object)
        var assignments = body.DescendantNodes().OfType<AssignmentExpressionSyntax>().ToList();
        var groupedAssignments = assignments
            .Where(a => a.Left is MemberAccessExpressionSyntax)
            .GroupBy(a => ((MemberAccessExpressionSyntax)a.Left).Expression.ToString())
            .Where(g => g.Count() >= 4);

        foreach (var group in groupedAssignments)
        {
            var first = group.First();
            var last = group.Last();
            candidates.Add(new ExtractCandidate
            {
                StartLine = first.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                EndLine = last.GetLocation().GetLineSpan().EndLinePosition.Line + 1,
                Reason = $"Multiple assignments to '{group.Key}'",
                SuggestedName = $"Configure{ToPascalCase(group.Key)}",
                StatementCount = group.Count()
            });
        }

        return candidates.DistinctBy(c => c.StartLine).Take(5).ToList();
    }

    private GodClass? AnalyzeForGodClass(ClassDeclarationSyntax classDecl, string filePath, SemanticModel semanticModel)
    {
        var methods = classDecl.Members.OfType<MethodDeclarationSyntax>().ToList();
        var fields = classDecl.Members.OfType<FieldDeclarationSyntax>().Sum(f => f.Declaration.Variables.Count);
        var properties = classDecl.Members.OfType<PropertyDeclarationSyntax>().Count();

        var methodCount = methods.Count;
        var lineSpan = classDecl.GetLocation().GetLineSpan();
        var lineCount = lineSpan.EndLinePosition.Line - lineSpan.StartLinePosition.Line + 1;

        // Only flag if class is large enough to be concerning
        if (methodCount < 15 && lineCount < 500 && fields < 15) return null;

        // Calculate LCOM (Lack of Cohesion of Methods)
        var lcom = CalculateLCOM(classDecl, methods);

        // Detect responsibilities based on method name patterns
        var responsibilities = DetectResponsibilities(methods);

        if (methodCount >= 20 || lineCount >= 800 || responsibilities.Count >= 5 || lcom > 0.7)
        {
            return new GodClass
            {
                ClassName = classDecl.Identifier.Text,
                FilePath = filePath,
                Line = lineSpan.StartLinePosition.Line + 1,
                MethodCount = methodCount,
                FieldCount = fields,
                PropertyCount = properties,
                LineCount = lineCount,
                LCOM = lcom,
                ResponsibilityCount = responsibilities.Count,
                Responsibilities = responsibilities
            };
        }

        return null;
    }

    private double CalculateLCOM(ClassDeclarationSyntax classDecl, List<MethodDeclarationSyntax> methods)
    {
        if (methods.Count < 2) return 0;

        var fields = classDecl.Members.OfType<FieldDeclarationSyntax>()
            .SelectMany(f => f.Declaration.Variables.Select(v => v.Identifier.Text))
            .ToHashSet();

        if (fields.Count == 0) return 0;

        // For each method, find which fields it accesses
        var methodFieldAccess = new Dictionary<string, HashSet<string>>();
        foreach (var method in methods)
        {
            var accessed = new HashSet<string>();
            var identifiers = method.DescendantNodes().OfType<IdentifierNameSyntax>();
            foreach (var id in identifiers)
            {
                if (fields.Contains(id.Identifier.Text))
                    accessed.Add(id.Identifier.Text);
            }
            methodFieldAccess[method.Identifier.Text] = accessed;
        }

        // Calculate LCOM: pairs that don't share fields / total pairs
        int noSharedFields = 0;
        int sharedFields = 0;
        var methodNames = methodFieldAccess.Keys.ToList();

        for (int i = 0; i < methodNames.Count; i++)
        {
            for (int j = i + 1; j < methodNames.Count; j++)
            {
                var set1 = methodFieldAccess[methodNames[i]];
                var set2 = methodFieldAccess[methodNames[j]];

                if (set1.Intersect(set2).Any())
                    sharedFields++;
                else
                    noSharedFields++;
            }
        }

        var total = noSharedFields + sharedFields;
        return total > 0 ? (double)noSharedFields / total : 0;
    }

    private List<string> DetectResponsibilities(List<MethodDeclarationSyntax> methods)
    {
        var responsibilities = new HashSet<string>();

        var prefixPatterns = new Dictionary<string, string>
        {
            { "Get", "Data Retrieval" },
            { "Set", "Data Modification" },
            { "Load", "Data Loading" },
            { "Save", "Data Persistence" },
            { "Validate", "Validation" },
            { "Calculate", "Calculation" },
            { "Format", "Formatting" },
            { "Parse", "Parsing" },
            { "Send", "Communication" },
            { "Receive", "Communication" },
            { "Create", "Object Creation" },
            { "Delete", "Object Deletion" },
            { "Update", "Data Update" },
            { "Handle", "Event Handling" },
            { "On", "Event Handling" },
            { "Display", "UI Display" },
            { "Show", "UI Display" },
            { "Hide", "UI Display" },
            { "Render", "Rendering" },
            { "Log", "Logging" },
            { "Export", "Export" },
            { "Import", "Import" },
            { "Convert", "Conversion" },
            { "Initialize", "Initialization" },
            { "Configure", "Configuration" },
        };

        foreach (var method in methods)
        {
            var name = method.Identifier.Text;
            foreach (var (prefix, responsibility) in prefixPatterns)
            {
                if (name.StartsWith(prefix))
                {
                    responsibilities.Add(responsibility);
                    break;
                }
            }
        }

        return responsibilities.ToList();
    }

    private FeatureEnvy? DetectFeatureEnvy(MethodDeclarationSyntax method, string filePath, SemanticModel semanticModel)
    {
        var body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
        if (body == null) return null;

        var className = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "";

        // Count member accesses by target class
        var accessCounts = new Dictionary<string, int>();
        int ownAccess = 0;

        var memberAccesses = body.DescendantNodes().OfType<MemberAccessExpressionSyntax>();

        foreach (var access in memberAccesses)
        {
            var targetType = semanticModel.GetTypeInfo(access.Expression).Type;
            if (targetType == null) continue;

            var targetTypeName = targetType.Name;

            if (targetTypeName == className || access.Expression is ThisExpressionSyntax)
            {
                ownAccess++;
            }
            else if (!targetTypeName.StartsWith("String") && !targetTypeName.StartsWith("Int") &&
                    !targetTypeName.StartsWith("List") && !targetTypeName.StartsWith("Dictionary"))
            {
                accessCounts.TryAdd(targetTypeName, 0);
                accessCounts[targetTypeName]++;
            }
        }

        // Find the most accessed external class
        var maxExternal = accessCounts.OrderByDescending(kv => kv.Value).FirstOrDefault();

        if (maxExternal.Value > 3 && maxExternal.Value > ownAccess * 1.5)
        {
            return new FeatureEnvy
            {
                ClassName = className,
                MethodName = method.Identifier.Text,
                FilePath = filePath,
                Line = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                EnviedClass = maxExternal.Key,
                OwnMemberAccess = ownAccess,
                EnviedMemberAccess = maxExternal.Value,
                EnvyRatio = ownAccess > 0 ? (double)maxExternal.Value / ownAccess : maxExternal.Value
            };
        }

        return null;
    }

    private ParameterSmell? AnalyzeParameters(MethodDeclarationSyntax method, string filePath, SemanticModel semanticModel)
    {
        var parameters = method.ParameterList.Parameters;
        if (parameters.Count < 4) return null;

        var className = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "";
        var paramNames = parameters.Select(p => p.Identifier.Text).ToList();
        var paramTypes = parameters.Select(p => p.Type?.ToString() ?? "").ToList();

        // Check for primitive obsession (many primitives of same type)
        var primitiveCount = paramTypes.Count(t => t is "string" or "int" or "bool" or "double" or "float" or "decimal");
        var stringCount = paramTypes.Count(t => t == "string");

        string smellType;
        string suggestion;

        if (parameters.Count >= 7)
        {
            smellType = "LongParameterList";
            suggestion = $"Consider creating a '{method.Identifier.Text}Options' or '{method.Identifier.Text}Request' class";
        }
        else if (stringCount >= 4)
        {
            smellType = "PrimitiveObsession";
            suggestion = "Multiple string parameters - consider creating a strongly-typed class";
        }
        else if (primitiveCount >= 5)
        {
            smellType = "PrimitiveObsession";
            suggestion = "Many primitive parameters - consider grouping into a parameter object";
        }
        else
        {
            smellType = "LongParameterList";
            suggestion = "Consider using a parameter object pattern";
        }

        return new ParameterSmell
        {
            ClassName = className,
            MethodName = method.Identifier.Text,
            FilePath = filePath,
            Line = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ParameterCount = parameters.Count,
            Parameters = paramNames,
            SmellType = smellType,
            Suggestion = suggestion
        };
    }

    private List<DataClump> FindDataClumps(Dictionary<string, List<(string ClassName, string MethodName, string FilePath, int Line)>> parameterSets)
    {
        var dataClumps = new List<DataClump>();

        // Find parameter combinations that appear in multiple methods
        foreach (var (paramKey, occurrences) in parameterSets.Where(kv => kv.Value.Count >= 3))
        {
            var paramsList = paramKey.Split(',').ToList();

            // Generate a suggested class name
            var suggestedName = GenerateDataClassName(paramsList);

            dataClumps.Add(new DataClump
            {
                Parameters = paramsList,
                Occurrences = occurrences,
                SuggestedClassName = suggestedName
            });
        }

        return dataClumps.OrderByDescending(d => d.Occurrences.Count).Take(10).ToList();
    }

    private int CalculateComplexity(SyntaxNode node)
    {
        int complexity = 1;
        foreach (var descendant in node.DescendantNodes())
        {
            complexity += descendant switch
            {
                IfStatementSyntax => 1,
                ConditionalExpressionSyntax => 1,
                CaseSwitchLabelSyntax => 1,
                CasePatternSwitchLabelSyntax => 1,
                ForStatementSyntax => 1,
                ForEachStatementSyntax => 1,
                WhileStatementSyntax => 1,
                DoStatementSyntax => 1,
                CatchClauseSyntax => 1,
                BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalAndExpression) => 1,
                BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalOrExpression) => 1,
                _ => 0
            };
        }
        return complexity;
    }

    private string GenerateMethodName(string comment)
    {
        var words = comment.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(4);
        return string.Join("", words.Select(ToPascalCase));
    }

    private string GenerateDataClassName(List<string> parameters)
    {
        if (parameters.Count == 0) return "ParameterData";

        // Try to find common prefixes/suffixes
        var commonParts = parameters
            .SelectMany(p => SplitCamelCase(p))
            .GroupBy(p => p.ToLower())
            .Where(g => g.Count() >= 2)
            .Select(g => g.Key)
            .FirstOrDefault();

        if (commonParts != null)
            return ToPascalCase(commonParts) + "Data";

        return ToPascalCase(parameters[0]) + "Parameters";
    }

    private string ToPascalCase(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = text.Replace("_", " ").Replace("-", " ");
        return string.Join("", text.Split(' ').Select(w =>
            string.IsNullOrEmpty(w) ? "" : char.ToUpper(w[0]) + w.Substring(1).ToLower()));
    }

    private IEnumerable<string> SplitCamelCase(string text)
    {
        var result = new List<string>();
        var current = "";
        foreach (var c in text)
        {
            if (char.IsUpper(c) && current.Length > 0)
            {
                result.Add(current);
                current = "";
            }
            current += c;
        }
        if (current.Length > 0) result.Add(current);
        return result;
    }
}
