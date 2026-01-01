using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace BaseScanner.Analyzers;

/// <summary>
/// Analyzes architectural concerns: public API surface, call graphs, inheritance depth,
/// and interface segregation.
/// </summary>
public class ArchitectureAnalyzer
{
    public record PublicApiMember
    {
        public required string TypeName { get; init; }
        public required string MemberName { get; init; }
        public required string MemberType { get; init; } // Method, Property, Field, Event
        public required string FilePath { get; init; }
        public required int Line { get; init; }
        public required string Signature { get; init; }
        public required bool HasDocumentation { get; init; }
        public required int ReferenceCount { get; init; }
        public required string BreakingChangeRisk { get; init; } // High, Medium, Low
    }

    public record CallGraphNode
    {
        public required string TypeName { get; init; }
        public required string MethodName { get; init; }
        public required string FilePath { get; init; }
        public required int Line { get; init; }
        public required bool IsEntryPoint { get; init; }
        public required bool IsDeadEnd { get; init; }
        public required int IncomingCalls { get; init; }
        public required int OutgoingCalls { get; init; }
        public required List<string> CalledBy { get; init; }
        public required List<string> Calls { get; init; }
    }

    public record InheritanceInfo
    {
        public required string TypeName { get; init; }
        public required string FilePath { get; init; }
        public required int Line { get; init; }
        public required int InheritanceDepth { get; init; }
        public required List<string> InheritanceChain { get; init; }
        public required int DerivedTypeCount { get; init; }
        public required bool HasCompositionOpportunity { get; init; }
        public required string CompositionSuggestion { get; init; }
    }

    public record InterfaceSegregationIssue
    {
        public required string InterfaceName { get; init; }
        public required string FilePath { get; init; }
        public required int Line { get; init; }
        public required int MemberCount { get; init; }
        public required List<string> Members { get; init; }
        public required List<(string Implementor, List<string> UnusedMembers)> PartialImplementations { get; init; }
        public required List<string> SuggestedSplits { get; init; }
    }

    public async Task<(List<PublicApiMember> PublicApi, List<CallGraphNode> CallGraph,
        List<InheritanceInfo> InheritanceIssues, List<InterfaceSegregationIssue> InterfaceIssues)> AnalyzeAsync(Project project)
    {
        var publicApi = new List<PublicApiMember>();
        var callGraphNodes = new Dictionary<string, CallGraphNode>();
        var inheritanceIssues = new List<InheritanceInfo>();
        var interfaceIssues = new List<InterfaceSegregationIssue>();

        var compilation = await project.GetCompilationAsync();
        if (compilation == null)
            return (publicApi, callGraphNodes.Values.ToList(), inheritanceIssues, interfaceIssues);

        // Build call graph and analyze
        var methodCalls = new Dictionary<string, HashSet<string>>(); // caller -> callees
        var methodCalledBy = new Dictionary<string, HashSet<string>>(); // callee -> callers

        foreach (var document in project.Documents)
        {
            if (document.FilePath == null) continue;
            if (document.FilePath.Contains(".Designer.cs")) continue;

            var syntaxRoot = await document.GetSyntaxRootAsync();
            var semanticModel = await document.GetSemanticModelAsync();
            if (syntaxRoot == null || semanticModel == null) continue;

            // Analyze public API
            publicApi.AddRange(await AnalyzePublicApi(syntaxRoot, semanticModel, document.FilePath, project.Solution));

            // Build call graph
            BuildCallGraph(syntaxRoot, semanticModel, document.FilePath, methodCalls, methodCalledBy);

            // Analyze inheritance
            var inheritanceInfo = AnalyzeInheritance(syntaxRoot, semanticModel, document.FilePath, compilation);
            inheritanceIssues.AddRange(inheritanceInfo);

            // Analyze interfaces
            var interfaceInfo = await AnalyzeInterfaces(syntaxRoot, semanticModel, document.FilePath, project);
            interfaceIssues.AddRange(interfaceInfo);
        }

        // Convert call graph to nodes
        var allMethods = methodCalls.Keys.Union(methodCalledBy.Keys).Distinct();
        foreach (var method in allMethods)
        {
            var parts = method.Split('.');
            var typeName = parts.Length > 1 ? string.Join(".", parts.Take(parts.Length - 1)) : "";
            var methodName = parts.Last();

            var calls = methodCalls.GetValueOrDefault(method, new HashSet<string>());
            var calledBy = methodCalledBy.GetValueOrDefault(method, new HashSet<string>());

            callGraphNodes[method] = new CallGraphNode
            {
                TypeName = typeName,
                MethodName = methodName,
                FilePath = "",
                Line = 0,
                IsEntryPoint = calledBy.Count == 0 && calls.Count > 0,
                IsDeadEnd = calledBy.Count == 0 && calls.Count == 0,
                IncomingCalls = calledBy.Count,
                OutgoingCalls = calls.Count,
                CalledBy = calledBy.Take(10).ToList(),
                Calls = calls.Take(10).ToList()
            };
        }

        return (publicApi.OrderByDescending(p => p.BreakingChangeRisk == "High").ThenBy(p => p.TypeName).ToList(),
                callGraphNodes.Values.OrderByDescending(n => n.OutgoingCalls).ToList(),
                inheritanceIssues.OrderByDescending(i => i.InheritanceDepth).ToList(),
                interfaceIssues.OrderByDescending(i => i.MemberCount).ToList());
    }

    private async Task<List<PublicApiMember>> AnalyzePublicApi(SyntaxNode root, SemanticModel semanticModel,
        string filePath, Solution solution)
    {
        var members = new List<PublicApiMember>();

        // Find public types
        var publicTypes = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Where(t => t.Modifiers.Any(SyntaxKind.PublicKeyword));

        foreach (var type in publicTypes)
        {
            var typeSymbol = semanticModel.GetDeclaredSymbol(type);
            if (typeSymbol == null) continue;

            // Add the type itself
            members.Add(new PublicApiMember
            {
                TypeName = typeSymbol.Name,
                MemberName = typeSymbol.Name,
                MemberType = type switch
                {
                    ClassDeclarationSyntax => "Class",
                    InterfaceDeclarationSyntax => "Interface",
                    StructDeclarationSyntax => "Struct",
                    RecordDeclarationSyntax => "Record",
                    _ => "Type"
                },
                FilePath = filePath,
                Line = type.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                Signature = GetTypeSignature(type),
                HasDocumentation = HasXmlDocumentation(type),
                ReferenceCount = 0,
                BreakingChangeRisk = type is InterfaceDeclarationSyntax ? "High" : "Medium"
            });

            // Analyze public members
            foreach (var member in type.Members)
            {
                if (!IsPublicMember(member)) continue;

                var memberSymbol = semanticModel.GetDeclaredSymbol(member);
                if (memberSymbol == null) continue;

                var (memberType, signature, risk) = GetMemberInfo(member);

                members.Add(new PublicApiMember
                {
                    TypeName = typeSymbol.Name,
                    MemberName = memberSymbol.Name,
                    MemberType = memberType,
                    FilePath = filePath,
                    Line = member.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    Signature = signature,
                    HasDocumentation = HasXmlDocumentation(member),
                    ReferenceCount = 0,
                    BreakingChangeRisk = risk
                });
            }
        }

        return members;
    }

    private void BuildCallGraph(SyntaxNode root, SemanticModel semanticModel, string filePath,
        Dictionary<string, HashSet<string>> methodCalls, Dictionary<string, HashSet<string>> methodCalledBy)
    {
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>();

        foreach (var method in methods)
        {
            var methodSymbol = semanticModel.GetDeclaredSymbol(method);
            if (methodSymbol == null) continue;

            var callerKey = $"{methodSymbol.ContainingType.Name}.{methodSymbol.Name}";

            if (!methodCalls.ContainsKey(callerKey))
                methodCalls[callerKey] = new HashSet<string>();

            // Find all method invocations in this method
            var invocations = method.DescendantNodes().OfType<InvocationExpressionSyntax>();

            foreach (var invocation in invocations)
            {
                var invokedSymbol = semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                if (invokedSymbol == null) continue;

                // Skip system/library methods
                var ns = invokedSymbol.ContainingNamespace?.ToDisplayString() ?? "";
                if (ns.StartsWith("System") || ns.StartsWith("Microsoft")) continue;

                var calleeKey = $"{invokedSymbol.ContainingType?.Name ?? ""}.{invokedSymbol.Name}";

                methodCalls[callerKey].Add(calleeKey);

                if (!methodCalledBy.ContainsKey(calleeKey))
                    methodCalledBy[calleeKey] = new HashSet<string>();
                methodCalledBy[calleeKey].Add(callerKey);
            }
        }
    }

    private List<InheritanceInfo> AnalyzeInheritance(SyntaxNode root, SemanticModel semanticModel,
        string filePath, Compilation compilation)
    {
        var issues = new List<InheritanceInfo>();

        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

        foreach (var classDecl in classes)
        {
            var symbol = semanticModel.GetDeclaredSymbol(classDecl);
            if (symbol == null) continue;

            // Calculate inheritance depth
            var chain = new List<string>();
            var current = symbol.BaseType;
            while (current != null && current.Name != "Object")
            {
                chain.Add(current.Name);
                current = current.BaseType;
            }

            var depth = chain.Count;

            // Only report if depth is concerning or if there are composition opportunities
            if (depth < 3) continue;

            // Check for composition opportunities
            var hasCompositionOpp = false;
            var compositionSuggestion = "";

            // Check if base class is only used for a few methods
            var baseMembers = symbol.BaseType?.GetMembers()
                .Where(m => m.Kind == SymbolKind.Method && !m.IsImplicitlyDeclared)
                .Count() ?? 0;

            var overriddenMembers = classDecl.Members.OfType<MethodDeclarationSyntax>()
                .Count(m => m.Modifiers.Any(SyntaxKind.OverrideKeyword));

            if (baseMembers > 5 && overriddenMembers <= 2)
            {
                hasCompositionOpp = true;
                compositionSuggestion = $"Consider composition: only {overriddenMembers}/{baseMembers} base methods overridden";
            }

            // Count derived types
            var derivedCount = compilation.GlobalNamespace.GetAllTypes()
                .Count(t => t.BaseType?.Equals(symbol, SymbolEqualityComparer.Default) == true);

            issues.Add(new InheritanceInfo
            {
                TypeName = symbol.Name,
                FilePath = filePath,
                Line = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                InheritanceDepth = depth,
                InheritanceChain = chain,
                DerivedTypeCount = derivedCount,
                HasCompositionOpportunity = hasCompositionOpp,
                CompositionSuggestion = compositionSuggestion
            });
        }

        return issues;
    }

    private async Task<List<InterfaceSegregationIssue>> AnalyzeInterfaces(SyntaxNode root, SemanticModel semanticModel,
        string filePath, Project project)
    {
        var issues = new List<InterfaceSegregationIssue>();

        var interfaces = root.DescendantNodes().OfType<InterfaceDeclarationSyntax>();

        foreach (var iface in interfaces)
        {
            var symbol = semanticModel.GetDeclaredSymbol(iface);
            if (symbol == null) continue;

            var members = symbol.GetMembers()
                .Where(m => !m.IsImplicitlyDeclared)
                .Select(m => m.Name)
                .ToList();

            if (members.Count < 5) continue; // Only analyze larger interfaces

            // Find implementations
            var implementations = await SymbolFinder.FindImplementationsAsync(symbol, project.Solution);
            var partialImplementations = new List<(string Implementor, List<string> UnusedMembers)>();

            foreach (var impl in implementations.Take(10))
            {
                if (impl is not INamedTypeSymbol implType) continue;

                // Check which interface members are actually used
                var unusedMembers = new List<string>();
                foreach (var member in symbol.GetMembers().Where(m => !m.IsImplicitlyDeclared))
                {
                    var implMember = implType.FindImplementationForInterfaceMember(member);
                    if (implMember != null)
                    {
                        // Check if the implementation is empty/trivial
                        // This is a simplified check - in reality you'd need to analyze the syntax
                        var implMemberSyntax = implMember.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
                        if (implMemberSyntax is MethodDeclarationSyntax methodSyntax)
                        {
                            var body = methodSyntax.Body?.ToString() ?? methodSyntax.ExpressionBody?.ToString() ?? "";
                            if (body.Contains("throw new NotImplementedException") ||
                                body.Contains("throw new NotSupportedException") ||
                                body.Trim() == "{ }")
                            {
                                unusedMembers.Add(member.Name);
                            }
                        }
                    }
                }

                if (unusedMembers.Count > 0)
                {
                    partialImplementations.Add((implType.Name, unusedMembers));
                }
            }

            // Suggest splits based on member names
            var suggestedSplits = SuggestInterfaceSplits(members);

            if (members.Count >= 7 || partialImplementations.Count > 0 || suggestedSplits.Count > 1)
            {
                issues.Add(new InterfaceSegregationIssue
                {
                    InterfaceName = symbol.Name,
                    FilePath = filePath,
                    Line = iface.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    MemberCount = members.Count,
                    Members = members,
                    PartialImplementations = partialImplementations,
                    SuggestedSplits = suggestedSplits
                });
            }
        }

        return issues;
    }

    private List<string> SuggestInterfaceSplits(List<string> members)
    {
        var suggestions = new List<string>();

        // Group by common prefixes
        var prefixGroups = members
            .Select(m => (Member: m, Prefix: GetPrefix(m)))
            .Where(x => x.Prefix.Length > 2)
            .GroupBy(x => x.Prefix)
            .Where(g => g.Count() >= 2)
            .ToList();

        foreach (var group in prefixGroups)
        {
            suggestions.Add($"I{group.Key}able: {string.Join(", ", group.Select(x => x.Member))}");
        }

        // Check for read/write splits
        var readMembers = members.Where(m => m.StartsWith("Get") || m.StartsWith("Read") || m.StartsWith("Find") || m.StartsWith("Is")).ToList();
        var writeMembers = members.Where(m => m.StartsWith("Set") || m.StartsWith("Write") || m.StartsWith("Add") || m.StartsWith("Update") || m.StartsWith("Delete")).ToList();

        if (readMembers.Count >= 2 && writeMembers.Count >= 2)
        {
            suggestions.Add($"IReadable: {string.Join(", ", readMembers)}");
            suggestions.Add($"IWritable: {string.Join(", ", writeMembers)}");
        }

        return suggestions.Distinct().ToList();
    }

    private string GetPrefix(string memberName)
    {
        // Extract common prefixes like "Get", "Set", "Create", etc.
        var prefixes = new[] { "Get", "Set", "Add", "Remove", "Create", "Delete", "Update", "Find", "Is", "Has", "Can" };
        foreach (var prefix in prefixes)
        {
            if (memberName.StartsWith(prefix) && memberName.Length > prefix.Length)
                return prefix;
        }

        // Try to find camelCase split
        for (int i = 1; i < memberName.Length; i++)
        {
            if (char.IsUpper(memberName[i]))
                return memberName.Substring(0, i);
        }

        return memberName;
    }

    private bool IsPublicMember(MemberDeclarationSyntax member)
    {
        return member switch
        {
            MethodDeclarationSyntax m => m.Modifiers.Any(SyntaxKind.PublicKeyword),
            PropertyDeclarationSyntax p => p.Modifiers.Any(SyntaxKind.PublicKeyword),
            FieldDeclarationSyntax f => f.Modifiers.Any(SyntaxKind.PublicKeyword),
            EventDeclarationSyntax e => e.Modifiers.Any(SyntaxKind.PublicKeyword),
            _ => false
        };
    }

    private string GetTypeSignature(TypeDeclarationSyntax type)
    {
        var modifiers = string.Join(" ", type.Modifiers.Select(m => m.Text));
        var keyword = type switch
        {
            ClassDeclarationSyntax => "class",
            InterfaceDeclarationSyntax => "interface",
            StructDeclarationSyntax => "struct",
            RecordDeclarationSyntax => "record",
            _ => "type"
        };
        return $"{modifiers} {keyword} {type.Identifier.Text}";
    }

    private (string MemberType, string Signature, string Risk) GetMemberInfo(MemberDeclarationSyntax member)
    {
        return member switch
        {
            MethodDeclarationSyntax m => ("Method", $"{m.ReturnType} {m.Identifier}({string.Join(", ", m.ParameterList.Parameters.Select(p => $"{p.Type} {p.Identifier}"))})",
                m.Modifiers.Any(SyntaxKind.VirtualKeyword) ? "High" : "Medium"),
            PropertyDeclarationSyntax p => ("Property", $"{p.Type} {p.Identifier}", "Medium"),
            FieldDeclarationSyntax f => ("Field", $"{f.Declaration.Type} {string.Join(", ", f.Declaration.Variables.Select(v => v.Identifier))}", "Low"),
            EventDeclarationSyntax e => ("Event", $"event {e.Type} {e.Identifier}", "High"),
            _ => ("Unknown", "", "Low")
        };
    }

    private bool HasXmlDocumentation(SyntaxNode node)
    {
        return node.GetLeadingTrivia()
            .Any(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                     t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));
    }
}

public static class NamespaceExtensions
{
    public static IEnumerable<INamedTypeSymbol> GetAllTypes(this INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
            yield return type;

        foreach (var nestedNs in ns.GetNamespaceMembers())
        {
            foreach (var type in nestedNs.GetAllTypes())
                yield return type;
        }
    }
}
