using System.Diagnostics;
using System.Text.RegularExpressions;

namespace BaseScanner.Analyzers;

public class GitChurnAnalyzer
{
    public record FileChurn
    {
        public required string FilePath { get; init; }
        public required string RelativePath { get; init; }
        public required int CommitCount { get; init; }
        public required int AddedLines { get; init; }
        public required int DeletedLines { get; init; }
        public required int TotalChurn { get; init; }
        public required DateTime? LastModified { get; init; }
        public required DateTime? FirstCommit { get; init; }
        public required int DaysSinceLastChange { get; init; }
        public required int AgeInDays { get; init; }
        public required List<string> TopContributors { get; init; }
    }

    public record Hotspot
    {
        public required string FilePath { get; init; }
        public required double HotspotScore { get; init; } // Combines churn + complexity
        public required int ChurnCount { get; init; }
        public required string Reason { get; init; }
    }

    public record Issue
    {
        public required string Type { get; init; }
        public required string Severity { get; init; }
        public required string Message { get; init; }
        public required string FilePath { get; init; }
        public required int Line { get; init; }
        public required string CodeSnippet { get; init; }
    }

    public async Task<(List<Issue> Issues, List<FileChurn> Churns, List<Hotspot> Hotspots, bool GitAvailable)> AnalyzeAsync(string projectDirectory)
    {
        var issues = new List<Issue>();
        var churns = new List<FileChurn>();
        var hotspots = new List<Hotspot>();

        // Check if git is available
        if (!await IsGitRepository(projectDirectory))
        {
            return (issues, churns, hotspots, false);
        }

        // Get file churn data
        churns = await GetFileChurnData(projectDirectory);

        // Identify hotspots (high churn files)
        hotspots = IdentifyHotspots(churns, projectDirectory);

        // Generate issues for problematic patterns
        foreach (var churn in churns.Where(c => c.CommitCount > 50))
        {
            issues.Add(new Issue
            {
                Type = "HighChurn",
                Severity = "Info",
                Message = $"File changed {churn.CommitCount} times - consider refactoring if changes are bug fixes",
                FilePath = churn.FilePath,
                Line = 0,
                CodeSnippet = $"{churn.CommitCount} commits, {churn.TotalChurn} lines churned"
            });
        }

        foreach (var churn in churns.Where(c => c.DaysSinceLastChange > 365 * 2))
        {
            issues.Add(new Issue
            {
                Type = "StaleCode",
                Severity = "Info",
                Message = $"File hasn't been modified in {churn.DaysSinceLastChange / 365} years - may need review",
                FilePath = churn.FilePath,
                Line = 0,
                CodeSnippet = $"Last modified: {churn.LastModified:yyyy-MM-dd}"
            });
        }

        return (issues, churns, hotspots, true);
    }

    private async Task<bool> IsGitRepository(string directory)
    {
        try
        {
            var result = await RunGitCommand(directory, "rev-parse --is-inside-work-tree");
            return result.Trim() == "true";
        }
        catch
        {
            return false;
        }
    }

    private async Task<List<FileChurn>> GetFileChurnData(string projectDirectory)
    {
        var churns = new Dictionary<string, FileChurn>();

        try
        {
            // Get commit count per file
            var logOutput = await RunGitCommand(projectDirectory,
                "log --format=format: --name-only --since=\"3 years ago\" -- \"*.cs\"");

            var fileCommitCounts = logOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .GroupBy(f => f.Trim())
                .ToDictionary(g => g.Key, g => g.Count());

            // Get churn statistics (added/deleted lines)
            var numstatOutput = await RunGitCommand(projectDirectory,
                "log --numstat --format=format: --since=\"3 years ago\" -- \"*.cs\"");

            var fileChurnData = new Dictionary<string, (int Added, int Deleted)>();
            foreach (var line in numstatOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length >= 3)
                {
                    var added = int.TryParse(parts[0], out var a) ? a : 0;
                    var deleted = int.TryParse(parts[1], out var d) ? d : 0;
                    var file = parts[2].Trim();

                    if (file.EndsWith(".cs"))
                    {
                        if (!fileChurnData.ContainsKey(file))
                            fileChurnData[file] = (0, 0);

                        var current = fileChurnData[file];
                        fileChurnData[file] = (current.Added + added, current.Deleted + deleted);
                    }
                }
            }

            // Get last modified dates
            var lastModifiedOutput = await RunGitCommand(projectDirectory,
                "log -1 --format=%aI --name-only -- \"*.cs\"");

            var fileDates = new Dictionary<string, DateTime>();
            var currentDate = DateTime.MinValue;
            foreach (var line in lastModifiedOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (DateTime.TryParse(line.Trim(), out var date))
                {
                    currentDate = date;
                }
                else if (line.Trim().EndsWith(".cs"))
                {
                    fileDates[line.Trim()] = currentDate;
                }
            }

            // Get contributors per file
            var shortlogOutput = await RunGitCommand(projectDirectory,
                "shortlog -sn --since=\"3 years ago\" -- \"*.cs\"");

            var topContributors = shortlogOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => Regex.Match(l, @"\d+\s+(.+)"))
                .Where(m => m.Success)
                .Select(m => m.Groups[1].Value.Trim())
                .Take(5)
                .ToList();

            // Combine all data
            var allFiles = fileCommitCounts.Keys
                .Union(fileChurnData.Keys)
                .Distinct();

            foreach (var file in allFiles)
            {
                var fullPath = Path.Combine(projectDirectory, file);
                if (!File.Exists(fullPath)) continue;

                var commitCount = fileCommitCounts.GetValueOrDefault(file, 0);
                var churnData = fileChurnData.GetValueOrDefault(file, (0, 0));
                var lastModified = fileDates.GetValueOrDefault(file, DateTime.MinValue);

                churns[file] = new FileChurn
                {
                    FilePath = fullPath,
                    RelativePath = file,
                    CommitCount = commitCount,
                    AddedLines = churnData.Item1,
                    DeletedLines = churnData.Item2,
                    TotalChurn = churnData.Item1 + churnData.Item2,
                    LastModified = lastModified == DateTime.MinValue ? null : lastModified,
                    FirstCommit = null, // Would need separate query
                    DaysSinceLastChange = lastModified == DateTime.MinValue ? 0 : (int)(DateTime.Now - lastModified).TotalDays,
                    AgeInDays = 0, // Would need separate query
                    TopContributors = topContributors
                };
            }
        }
        catch (Exception)
        {
            // Git commands failed, return empty
        }

        return churns.Values.OrderByDescending(c => c.CommitCount).ToList();
    }

    private List<Hotspot> IdentifyHotspots(List<FileChurn> churns, string projectDirectory)
    {
        var hotspots = new List<Hotspot>();

        foreach (var churn in churns.Where(c => c.CommitCount >= 10))
        {
            var reasons = new List<string>();

            if (churn.CommitCount > 50)
                reasons.Add($"very high commits ({churn.CommitCount})");
            else if (churn.CommitCount > 20)
                reasons.Add($"high commits ({churn.CommitCount})");

            if (churn.TotalChurn > 5000)
                reasons.Add($"high churn ({churn.TotalChurn} lines)");

            // Calculate hotspot score
            var score = (churn.CommitCount * 2.0) + (churn.TotalChurn / 100.0);

            if (reasons.Count > 0)
            {
                hotspots.Add(new Hotspot
                {
                    FilePath = churn.RelativePath,
                    HotspotScore = score,
                    ChurnCount = churn.CommitCount,
                    Reason = string.Join(", ", reasons)
                });
            }
        }

        return hotspots.OrderByDescending(h => h.HotspotScore).ToList();
    }

    private async Task<string> RunGitCommand(string workingDirectory, string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        return output;
    }
}
