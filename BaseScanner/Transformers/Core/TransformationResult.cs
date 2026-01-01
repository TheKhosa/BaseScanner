namespace BaseScanner.Transformers.Core;

/// <summary>
/// Result of a code transformation.
/// </summary>
public record TransformationResult
{
    public required bool Success { get; init; }
    public required string TransformationType { get; init; }
    public List<FileChange> Changes { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public string? ErrorMessage { get; init; }
    public SemanticValidation? Validation { get; init; }

    public static TransformationResult Failed(string transformationType, string errorMessage) =>
        new()
        {
            Success = false,
            TransformationType = transformationType,
            ErrorMessage = errorMessage
        };

    public static TransformationResult Succeeded(
        string transformationType,
        List<FileChange> changes,
        SemanticValidation? validation = null) =>
        new()
        {
            Success = true,
            TransformationType = transformationType,
            Changes = changes,
            Validation = validation
        };
}

/// <summary>
/// Represents a change to a single file.
/// </summary>
public record FileChange
{
    public required string FilePath { get; init; }
    public required string OriginalCode { get; init; }
    public required string TransformedCode { get; init; }
    public required string UnifiedDiff { get; init; }
    public required int StartLine { get; init; }
    public required int EndLine { get; init; }
}

/// <summary>
/// Result of semantic validation.
/// </summary>
public record SemanticValidation
{
    public required bool IsEquivalent { get; init; }
    public required bool CompilesSuccessfully { get; init; }
    public List<string> BreakingChanges { get; init; } = [];
    public List<string> TypeChanges { get; init; } = [];
    public List<string> SignatureChanges { get; init; } = [];
    public List<string> CompilationErrors { get; init; } = [];
}

/// <summary>
/// Utility class for generating unified diffs.
/// </summary>
public static class DiffGenerator
{
    /// <summary>
    /// Generate a unified diff between original and transformed code.
    /// </summary>
    public static string GenerateUnifiedDiff(
        string originalCode,
        string transformedCode,
        string filePath,
        int contextLines = 3)
    {
        var originalLines = originalCode.Split('\n');
        var transformedLines = transformedCode.Split('\n');

        var diff = new System.Text.StringBuilder();
        diff.AppendLine($"--- a/{filePath}");
        diff.AppendLine($"+++ b/{filePath}");

        // Simple diff generation - finds changed lines
        var hunks = ComputeHunks(originalLines, transformedLines, contextLines);

        foreach (var hunk in hunks)
        {
            diff.AppendLine($"@@ -{hunk.OriginalStart},{hunk.OriginalLength} +{hunk.TransformedStart},{hunk.TransformedLength} @@");
            foreach (var line in hunk.Lines)
            {
                diff.AppendLine(line);
            }
        }

        return diff.ToString();
    }

    private static List<DiffHunk> ComputeHunks(string[] original, string[] transformed, int contextLines)
    {
        var hunks = new List<DiffHunk>();
        var changes = new List<(int origLine, int transLine, ChangeType type)>();

        // Find differences using simple LCS-based approach
        int i = 0, j = 0;
        while (i < original.Length || j < transformed.Length)
        {
            if (i >= original.Length)
            {
                changes.Add((i, j, ChangeType.Added));
                j++;
            }
            else if (j >= transformed.Length)
            {
                changes.Add((i, j, ChangeType.Removed));
                i++;
            }
            else if (original[i].TrimEnd() == transformed[j].TrimEnd())
            {
                i++;
                j++;
            }
            else
            {
                // Look ahead to find best match
                var foundOriginal = Array.FindIndex(transformed, j, t => t.TrimEnd() == original[i].TrimEnd());
                var foundTransformed = Array.FindIndex(original, i, o => o.TrimEnd() == transformed[j].TrimEnd());

                if (foundOriginal >= 0 && (foundTransformed < 0 || foundOriginal - j <= foundTransformed - i))
                {
                    // Lines were added
                    while (j < foundOriginal)
                    {
                        changes.Add((i, j, ChangeType.Added));
                        j++;
                    }
                }
                else if (foundTransformed >= 0)
                {
                    // Lines were removed
                    while (i < foundTransformed)
                    {
                        changes.Add((i, j, ChangeType.Removed));
                        i++;
                    }
                }
                else
                {
                    // Lines were changed
                    changes.Add((i, j, ChangeType.Removed));
                    changes.Add((i, j, ChangeType.Added));
                    i++;
                    j++;
                }
            }
        }

        if (changes.Count == 0)
            return hunks;

        // Group changes into hunks with context
        var currentHunk = new DiffHunk
        {
            OriginalStart = Math.Max(1, changes[0].origLine - contextLines + 1),
            TransformedStart = Math.Max(1, changes[0].transLine - contextLines + 1)
        };

        var hunkLines = new List<string>();
        int lastChangeOrig = -1;
        int lastChangeTrans = -1;

        foreach (var change in changes)
        {
            // Add context lines before this change
            if (lastChangeOrig >= 0)
            {
                var gapStart = lastChangeOrig + 1;
                var gapEnd = Math.Min(change.origLine, lastChangeOrig + contextLines + 1);
                for (int line = gapStart; line < gapEnd && line < original.Length; line++)
                {
                    hunkLines.Add($" {original[line]}");
                }
            }

            // Add the change
            if (change.type == ChangeType.Removed && change.origLine < original.Length)
            {
                hunkLines.Add($"-{original[change.origLine]}");
                lastChangeOrig = change.origLine;
            }
            else if (change.type == ChangeType.Added && change.transLine < transformed.Length)
            {
                hunkLines.Add($"+{transformed[change.transLine]}");
                lastChangeTrans = change.transLine;
            }
        }

        currentHunk.Lines = hunkLines;
        currentHunk.OriginalLength = hunkLines.Count(l => !l.StartsWith("+"));
        currentHunk.TransformedLength = hunkLines.Count(l => !l.StartsWith("-"));
        hunks.Add(currentHunk);

        return hunks;
    }

    private enum ChangeType { Added, Removed }

    private class DiffHunk
    {
        public int OriginalStart { get; set; }
        public int OriginalLength { get; set; }
        public int TransformedStart { get; set; }
        public int TransformedLength { get; set; }
        public List<string> Lines { get; set; } = [];
    }
}
