using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BaseScanner;

public class CodeSentimentAnalyzer
{
    public record CodeBlock
    {
        public required string Name { get; init; }
        public required string ContainingType { get; init; }
        public required string FilePath { get; init; }
        public required int StartLine { get; init; }
        public required int EndLine { get; init; }
        public required int LineCount { get; init; }
        public required CodeBlockType BlockType { get; init; }

        // Metrics
        public int CyclomaticComplexity { get; init; }
        public int ParameterCount { get; init; }
        public int LocalVariableCount { get; init; }
        public int NestingDepth { get; init; }
        public double CommentDensity { get; init; }

        // Sentiment
        public List<string> SentimentMarkers { get; init; } = new();
        public int TodoCount { get; init; }
        public int HackCount { get; init; }
        public int FixmeCount { get; init; }
        public int WarningCount { get; init; }

        // Quality score (0-100, higher is better)
        public int QualityScore { get; init; }
        public string QualityRating { get; init; } = "";

        // For similarity detection
        public string NormalizedHash { get; init; } = "";
        public string StructuralSignature { get; init; } = "";
    }

    public enum CodeBlockType
    {
        Method,
        Property,
        Constructor,
        Class,
        Struct
    }

    public record SimilarityGroup
    {
        public required string Signature { get; init; }
        public required List<CodeBlock> Blocks { get; init; }
        public required double SimilarityScore { get; init; }
    }

    public async Task<List<CodeBlock>> AnalyzeProjectAsync(Project project)
    {
        var blocks = new List<CodeBlock>();

        foreach (var document in project.Documents)
        {
            if (document.FilePath == null) continue;

            var syntaxRoot = await document.GetSyntaxRootAsync();
            var semanticModel = await document.GetSemanticModelAsync();
            if (syntaxRoot == null || semanticModel == null) continue;

            // Analyze methods
            var methods = syntaxRoot.DescendantNodes().OfType<MethodDeclarationSyntax>();
            foreach (var method in methods)
            {
                var block = AnalyzeMethod(method, document.FilePath, semanticModel);
                if (block != null) blocks.Add(block);
            }

            // Analyze constructors
            var constructors = syntaxRoot.DescendantNodes().OfType<ConstructorDeclarationSyntax>();
            foreach (var ctor in constructors)
            {
                var block = AnalyzeConstructor(ctor, document.FilePath, semanticModel);
                if (block != null) blocks.Add(block);
            }

            // Analyze properties with bodies
            var properties = syntaxRoot.DescendantNodes().OfType<PropertyDeclarationSyntax>()
                .Where(p => p.AccessorList?.Accessors.Any(a => a.Body != null || a.ExpressionBody != null) == true);
            foreach (var prop in properties)
            {
                var block = AnalyzeProperty(prop, document.FilePath, semanticModel);
                if (block != null) blocks.Add(block);
            }
        }

        return blocks;
    }

    private CodeBlock? AnalyzeMethod(MethodDeclarationSyntax method, string filePath, SemanticModel semanticModel)
    {
        if (method.Body == null && method.ExpressionBody == null) return null;

        var lineSpan = method.GetLocation().GetLineSpan();
        var containingType = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "Unknown";
        var bodyNode = (SyntaxNode?)method.Body ?? method.ExpressionBody;

        var complexity = CalculateCyclomaticComplexity(bodyNode);
        var nesting = CalculateMaxNestingDepth(bodyNode);
        var (todos, hacks, fixmes, warnings, markers) = AnalyzeSentimentMarkers(method);
        var commentDensity = CalculateCommentDensity(method);
        var lineCount = lineSpan.EndLinePosition.Line - lineSpan.StartLinePosition.Line + 1;

        var qualityScore = CalculateQualityScore(
            complexity,
            method.ParameterList.Parameters.Count,
            nesting,
            lineCount,
            todos + hacks + fixmes,
            commentDensity
        );

        return new CodeBlock
        {
            Name = method.Identifier.Text,
            ContainingType = containingType,
            FilePath = filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            LineCount = lineCount,
            BlockType = CodeBlockType.Method,
            CyclomaticComplexity = complexity,
            ParameterCount = method.ParameterList.Parameters.Count,
            LocalVariableCount = CountLocalVariables(bodyNode),
            NestingDepth = nesting,
            CommentDensity = commentDensity,
            TodoCount = todos,
            HackCount = hacks,
            FixmeCount = fixmes,
            WarningCount = warnings,
            SentimentMarkers = markers,
            QualityScore = qualityScore,
            QualityRating = GetQualityRating(qualityScore),
            NormalizedHash = ComputeNormalizedHash(bodyNode),
            StructuralSignature = ComputeStructuralSignature(method)
        };
    }

    private CodeBlock? AnalyzeConstructor(ConstructorDeclarationSyntax ctor, string filePath, SemanticModel semanticModel)
    {
        if (ctor.Body == null && ctor.ExpressionBody == null) return null;

        var lineSpan = ctor.GetLocation().GetLineSpan();
        var containingType = ctor.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "Unknown";
        var bodyNode = (SyntaxNode?)ctor.Body ?? ctor.ExpressionBody;

        var complexity = CalculateCyclomaticComplexity(bodyNode);
        var nesting = CalculateMaxNestingDepth(bodyNode);
        var (todos, hacks, fixmes, warnings, markers) = AnalyzeSentimentMarkers(ctor);
        var lineCount = lineSpan.EndLinePosition.Line - lineSpan.StartLinePosition.Line + 1;

        var qualityScore = CalculateQualityScore(complexity, ctor.ParameterList.Parameters.Count, nesting, lineCount, todos + hacks + fixmes, 0);

        return new CodeBlock
        {
            Name = $"{containingType}.ctor",
            ContainingType = containingType,
            FilePath = filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            LineCount = lineCount,
            BlockType = CodeBlockType.Constructor,
            CyclomaticComplexity = complexity,
            ParameterCount = ctor.ParameterList.Parameters.Count,
            LocalVariableCount = CountLocalVariables(bodyNode),
            NestingDepth = nesting,
            CommentDensity = 0,
            TodoCount = todos,
            HackCount = hacks,
            FixmeCount = fixmes,
            WarningCount = warnings,
            SentimentMarkers = markers,
            QualityScore = qualityScore,
            QualityRating = GetQualityRating(qualityScore),
            NormalizedHash = ComputeNormalizedHash(bodyNode),
            StructuralSignature = ComputeStructuralSignature(ctor)
        };
    }

    private CodeBlock? AnalyzeProperty(PropertyDeclarationSyntax prop, string filePath, SemanticModel semanticModel)
    {
        var lineSpan = prop.GetLocation().GetLineSpan();
        var containingType = prop.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "Unknown";
        var lineCount = lineSpan.EndLinePosition.Line - lineSpan.StartLinePosition.Line + 1;

        if (lineCount < 5) return null; // Skip trivial properties

        var (todos, hacks, fixmes, warnings, markers) = AnalyzeSentimentMarkers(prop);
        var complexity = CalculateCyclomaticComplexity(prop);

        var qualityScore = CalculateQualityScore(complexity, 0, 0, lineCount, todos + hacks + fixmes, 0);

        return new CodeBlock
        {
            Name = prop.Identifier.Text,
            ContainingType = containingType,
            FilePath = filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            LineCount = lineCount,
            BlockType = CodeBlockType.Property,
            CyclomaticComplexity = complexity,
            ParameterCount = 0,
            LocalVariableCount = 0,
            NestingDepth = CalculateMaxNestingDepth(prop),
            CommentDensity = 0,
            TodoCount = todos,
            HackCount = hacks,
            FixmeCount = fixmes,
            WarningCount = warnings,
            SentimentMarkers = markers,
            QualityScore = qualityScore,
            QualityRating = GetQualityRating(qualityScore),
            NormalizedHash = ComputeNormalizedHash(prop),
            StructuralSignature = ""
        };
    }

    private int CalculateCyclomaticComplexity(SyntaxNode? node)
    {
        if (node == null) return 1;

        int complexity = 1; // Base complexity

        foreach (var descendant in node.DescendantNodes())
        {
            complexity += descendant switch
            {
                IfStatementSyntax => 1,
                ElseClauseSyntax => 0, // else doesn't add complexity, only else-if does
                ConditionalExpressionSyntax => 1, // ternary operator
                SwitchStatementSyntax => 0, // switch itself doesn't add
                SwitchExpressionSyntax => 0,
                CaseSwitchLabelSyntax => 1,
                CasePatternSwitchLabelSyntax => 1,
                SwitchExpressionArmSyntax => 1,
                ForStatementSyntax => 1,
                ForEachStatementSyntax => 1,
                WhileStatementSyntax => 1,
                DoStatementSyntax => 1,
                CatchClauseSyntax => 1,
                BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalAndExpression) => 1,
                BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalOrExpression) => 1,
                BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.CoalesceExpression) => 1,
                ConditionalAccessExpressionSyntax => 1, // ?.
                _ => 0
            };
        }

        return complexity;
    }

    private int CalculateMaxNestingDepth(SyntaxNode? node)
    {
        if (node == null) return 0;

        int maxDepth = 0;

        void CalculateDepth(SyntaxNode current, int depth)
        {
            int currentDepth = depth;

            if (current is BlockSyntax or IfStatementSyntax or ForStatementSyntax or
                ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax or
                TryStatementSyntax or SwitchStatementSyntax or LockStatementSyntax or
                UsingStatementSyntax)
            {
                currentDepth++;
            }

            maxDepth = Math.Max(maxDepth, currentDepth);

            foreach (var child in current.ChildNodes())
            {
                CalculateDepth(child, currentDepth);
            }
        }

        foreach (var child in node.ChildNodes())
        {
            CalculateDepth(child, 0);
        }

        return maxDepth;
    }

    private int CountLocalVariables(SyntaxNode? node)
    {
        if (node == null) return 0;
        return node.DescendantNodes().OfType<LocalDeclarationStatementSyntax>()
            .Sum(l => l.Declaration.Variables.Count);
    }

    private double CalculateCommentDensity(SyntaxNode node)
    {
        var trivia = node.DescendantTrivia();
        var commentLines = trivia.Count(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
                                              t.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
                                              t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia));
        var lineSpan = node.GetLocation().GetLineSpan();
        var totalLines = lineSpan.EndLinePosition.Line - lineSpan.StartLinePosition.Line + 1;

        return totalLines > 0 ? (double)commentLines / totalLines : 0;
    }

    private (int todos, int hacks, int fixmes, int warnings, List<string> markers) AnalyzeSentimentMarkers(SyntaxNode node)
    {
        var markers = new List<string>();
        int todos = 0, hacks = 0, fixmes = 0, warnings = 0;

        var trivia = node.DescendantTrivia()
            .Where(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
                       t.IsKind(SyntaxKind.MultiLineCommentTrivia));

        foreach (var comment in trivia)
        {
            var text = comment.ToString().ToUpperInvariant();

            if (text.Contains("TODO"))
            {
                todos++;
                markers.Add("TODO");
            }
            if (text.Contains("HACK"))
            {
                hacks++;
                markers.Add("HACK");
            }
            if (text.Contains("FIXME") || text.Contains("FIX ME"))
            {
                fixmes++;
                markers.Add("FIXME");
            }
            if (text.Contains("XXX") || text.Contains("BUG") || text.Contains("BROKEN"))
            {
                warnings++;
                markers.Add("WARNING");
            }
            if (text.Contains("TEMPORARY") || text.Contains("TEMP FIX"))
            {
                warnings++;
                markers.Add("TEMPORARY");
            }
            if (text.Contains("WORKAROUND"))
            {
                warnings++;
                markers.Add("WORKAROUND");
            }
            if (text.Contains("DEPRECATED"))
            {
                warnings++;
                markers.Add("DEPRECATED");
            }
            if (text.Contains("REFACTOR"))
            {
                markers.Add("NEEDS_REFACTOR");
            }
        }

        return (todos, hacks, fixmes, warnings, markers.Distinct().ToList());
    }

    private int CalculateQualityScore(int complexity, int paramCount, int nesting, int lineCount, int issueCount, double commentDensity)
    {
        int score = 100;

        // Complexity penalties
        if (complexity > 10) score -= (complexity - 10) * 3;
        if (complexity > 20) score -= (complexity - 20) * 5;

        // Parameter count penalties
        if (paramCount > 4) score -= (paramCount - 4) * 5;
        if (paramCount > 7) score -= (paramCount - 7) * 10;

        // Nesting penalties
        if (nesting > 3) score -= (nesting - 3) * 5;
        if (nesting > 5) score -= (nesting - 5) * 10;

        // Line count penalties (methods shouldn't be too long)
        if (lineCount > 30) score -= (lineCount - 30) / 5;
        if (lineCount > 100) score -= (lineCount - 100) / 3;
        if (lineCount > 200) score -= (lineCount - 200) / 2;

        // Issue markers penalties
        score -= issueCount * 5;

        // Small bonus for having some comments (but not too many)
        if (commentDensity > 0.05 && commentDensity < 0.3)
            score += 5;

        return Math.Max(0, Math.Min(100, score));
    }

    private string GetQualityRating(int score) => score switch
    {
        >= 90 => "Excellent",
        >= 75 => "Good",
        >= 60 => "Acceptable",
        >= 40 => "Needs Improvement",
        >= 20 => "Poor",
        _ => "Critical"
    };

    private string ComputeNormalizedHash(SyntaxNode? node)
    {
        if (node == null) return "";

        // Normalize the code by removing whitespace and replacing identifiers
        var normalized = NormalizeCode(node.ToString());

        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hashBytes)[..16]; // First 16 chars
    }

    private string NormalizeCode(string code)
    {
        // Remove all whitespace
        code = Regex.Replace(code, @"\s+", " ");

        // Normalize string literals
        code = Regex.Replace(code, @"""[^""]*""", "\"STR\"");

        // Normalize numeric literals
        code = Regex.Replace(code, @"\b\d+\b", "NUM");

        return code.Trim();
    }

    private string ComputeStructuralSignature(SyntaxNode node)
    {
        var sb = new StringBuilder();

        void AppendStructure(SyntaxNode n, int depth)
        {
            if (depth > 5) return; // Limit depth

            var kind = n.Kind().ToString();

            // Only include structural elements
            if (n is StatementSyntax or ExpressionSyntax { Parent: not ArgumentSyntax })
            {
                sb.Append(kind[..Math.Min(3, kind.Length)]);
            }

            foreach (var child in n.ChildNodes().Take(10)) // Limit children
            {
                AppendStructure(child, depth + 1);
            }
        }

        AppendStructure(node, 0);
        return sb.ToString();
    }

    public List<SimilarityGroup> FindSimilarBlocks(List<CodeBlock> blocks, double threshold = 0.8)
    {
        var groups = new Dictionary<string, List<CodeBlock>>();

        // Group by normalized hash (exact duplicates)
        foreach (var block in blocks.Where(b => !string.IsNullOrEmpty(b.NormalizedHash)))
        {
            if (!groups.ContainsKey(block.NormalizedHash))
                groups[block.NormalizedHash] = new List<CodeBlock>();
            groups[block.NormalizedHash].Add(block);
        }

        // Filter to only groups with 2+ members
        var similarGroups = groups
            .Where(g => g.Value.Count >= 2)
            .Select(g => new SimilarityGroup
            {
                Signature = g.Key,
                Blocks = g.Value,
                SimilarityScore = 1.0 // Exact match
            })
            .ToList();

        // Also find structurally similar blocks
        var structuralGroups = new Dictionary<string, List<CodeBlock>>();
        foreach (var block in blocks.Where(b => !string.IsNullOrEmpty(b.StructuralSignature) && b.StructuralSignature.Length > 10))
        {
            var sig = block.StructuralSignature[..Math.Min(30, block.StructuralSignature.Length)];
            if (!structuralGroups.ContainsKey(sig))
                structuralGroups[sig] = new List<CodeBlock>();
            structuralGroups[sig].Add(block);
        }

        // Add structural similarity groups
        foreach (var group in structuralGroups.Where(g => g.Value.Count >= 2))
        {
            // Don't add if already in exact match group
            var firstBlock = group.Value.First();
            if (!similarGroups.Any(sg => sg.Blocks.Any(b => b.FilePath == firstBlock.FilePath && b.StartLine == firstBlock.StartLine)))
            {
                similarGroups.Add(new SimilarityGroup
                {
                    Signature = $"Structural:{group.Key[..Math.Min(20, group.Key.Length)]}",
                    Blocks = group.Value,
                    SimilarityScore = 0.8
                });
            }
        }

        return similarGroups.OrderByDescending(g => g.Blocks.Count).ThenByDescending(g => g.SimilarityScore).ToList();
    }
}
