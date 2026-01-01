using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace BaseScanner.VirtualWorkspace;

/// <summary>
/// Generates diffs between document versions.
/// </summary>
public class DiffEngine
{
    public async Task<DocumentDiff> GenerateDiffAsync(Document original, Document transformed)
    {
        var origText = await original.GetTextAsync();
        var transText = await transformed.GetTextAsync();

        // Get text changes
        var changes = await transformed.GetTextChangesAsync(original);
        var changesList = changes.ToList();

        // Build unified diff
        var unifiedDiff = BuildUnifiedDiff(
            origText.ToString(),
            transText.ToString(),
            original.FilePath ?? "original",
            transformed.FilePath ?? "transformed");

        // Get syntax changes
        var origRoot = await original.GetSyntaxRootAsync();
        var transRoot = await transformed.GetSyntaxRootAsync();
        var syntaxChanges = origRoot != null && transRoot != null
            ? ComputeSyntaxChanges(origRoot, transRoot)
            : new List<SyntaxChange>();

        return new DocumentDiff
        {
            OriginalPath = original.FilePath,
            TextChanges = changesList,
            UnifiedDiff = unifiedDiff,
            SyntaxChanges = syntaxChanges,
            AddedLines = CountAddedLines(origText, transText),
            RemovedLines = CountRemovedLines(origText, transText),
            ModifiedRegions = changesList.Count
        };
    }

    private string BuildUnifiedDiff(string orig, string trans, string origPath, string transPath)
    {
        var origLines = orig.Split('\n');
        var transLines = trans.Split('\n');

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"--- {origPath}");
        sb.AppendLine($"+++ {transPath}");

        // Simple line-by-line diff
        var lcs = ComputeLCS(origLines, transLines);
        var origIdx = 0;
        var transIdx = 0;
        var lcsIdx = 0;

        while (origIdx < origLines.Length || transIdx < transLines.Length)
        {
            if (lcsIdx < lcs.Count &&
                origIdx < origLines.Length &&
                transIdx < transLines.Length &&
                origLines[origIdx].Trim() == lcs[lcsIdx] &&
                transLines[transIdx].Trim() == lcs[lcsIdx])
            {
                // Common line
                sb.AppendLine($" {origLines[origIdx]}");
                origIdx++;
                transIdx++;
                lcsIdx++;
            }
            else if (transIdx < transLines.Length &&
                     (lcsIdx >= lcs.Count || transLines[transIdx].Trim() != lcs[lcsIdx]))
            {
                // Added line
                sb.AppendLine($"+{transLines[transIdx]}");
                transIdx++;
            }
            else if (origIdx < origLines.Length &&
                     (lcsIdx >= lcs.Count || origLines[origIdx].Trim() != lcs[lcsIdx]))
            {
                // Removed line
                sb.AppendLine($"-{origLines[origIdx]}");
                origIdx++;
            }
            else
            {
                break;
            }
        }

        return sb.ToString();
    }

    private List<string> ComputeLCS(string[] a, string[] b)
    {
        var aSet = a.Select(s => s.Trim()).ToList();
        var bSet = b.Select(s => s.Trim()).ToList();

        var m = aSet.Count;
        var n = bSet.Count;
        var dp = new int[m + 1, n + 1];

        for (int i = 1; i <= m; i++)
        {
            for (int j = 1; j <= n; j++)
            {
                if (aSet[i - 1] == bSet[j - 1])
                    dp[i, j] = dp[i - 1, j - 1] + 1;
                else
                    dp[i, j] = Math.Max(dp[i - 1, j], dp[i, j - 1]);
            }
        }

        // Backtrack to get LCS
        var lcs = new List<string>();
        var x = m;
        var y = n;
        while (x > 0 && y > 0)
        {
            if (aSet[x - 1] == bSet[y - 1])
            {
                lcs.Insert(0, aSet[x - 1]);
                x--;
                y--;
            }
            else if (dp[x - 1, y] > dp[x, y - 1])
                x--;
            else
                y--;
        }

        return lcs;
    }

    private List<SyntaxChange> ComputeSyntaxChanges(SyntaxNode orig, SyntaxNode trans)
    {
        var changes = new List<SyntaxChange>();

        // Compare methods
        var origMethods = orig.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .ToDictionary(m => GetMethodSignature(m), m => m.ToString());
        var transMethods = trans.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .ToDictionary(m => GetMethodSignature(m), m => m.ToString());

        foreach (var (sig, code) in origMethods)
        {
            if (!transMethods.TryGetValue(sig, out var transCode))
            {
                changes.Add(new SyntaxChange
                {
                    ChangeType = SyntaxChangeType.Removed,
                    NodeType = "Method",
                    Name = sig
                });
            }
            else if (code != transCode)
            {
                changes.Add(new SyntaxChange
                {
                    ChangeType = SyntaxChangeType.Modified,
                    NodeType = "Method",
                    Name = sig,
                    OriginalCode = code,
                    NewCode = transCode
                });
            }
        }

        foreach (var (sig, code) in transMethods)
        {
            if (!origMethods.ContainsKey(sig))
            {
                changes.Add(new SyntaxChange
                {
                    ChangeType = SyntaxChangeType.Added,
                    NodeType = "Method",
                    Name = sig,
                    NewCode = code
                });
            }
        }

        // Compare properties
        var origProps = orig.DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .ToDictionary(p => p.Identifier.Text, p => p.ToString());
        var transProps = trans.DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .ToDictionary(p => p.Identifier.Text, p => p.ToString());

        foreach (var (name, code) in origProps)
        {
            if (!transProps.TryGetValue(name, out var transCode))
            {
                changes.Add(new SyntaxChange
                {
                    ChangeType = SyntaxChangeType.Removed,
                    NodeType = "Property",
                    Name = name
                });
            }
            else if (code != transCode)
            {
                changes.Add(new SyntaxChange
                {
                    ChangeType = SyntaxChangeType.Modified,
                    NodeType = "Property",
                    Name = name,
                    OriginalCode = code,
                    NewCode = transCode
                });
            }
        }

        return changes;
    }

    private string GetMethodSignature(MethodDeclarationSyntax method)
    {
        var parameters = string.Join(",", method.ParameterList.Parameters.Select(p => p.Type?.ToString() ?? "?"));
        return $"{method.Identifier.Text}({parameters})";
    }

    private int CountAddedLines(SourceText orig, SourceText trans)
    {
        var origLines = new HashSet<string>(orig.Lines.Select(l => l.ToString().Trim()));
        return trans.Lines.Count(l => !origLines.Contains(l.ToString().Trim()));
    }

    private int CountRemovedLines(SourceText orig, SourceText trans)
    {
        var transLines = new HashSet<string>(trans.Lines.Select(l => l.ToString().Trim()));
        return orig.Lines.Count(l => !transLines.Contains(l.ToString().Trim()));
    }
}
