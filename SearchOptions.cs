namespace SatisfactoryClusterCalculator;

public sealed record SearchOptions(
    string WorldJsonPath,
    string OutputPath,
    ulong StartSeedUnsigned,
    ulong SeedCount,
    int DegreeOfParallelism,
    bool OpenResult,
    TimeSpan MaxRunTime,
    ClusteringMode ClusteringMode,
    double SubsetFraction,
    int? SubsetCount,
    bool IncludeLeast)
{
    public string ResumeKey =>
        FormattableString.Invariant($"{ClusteringMode}|subsetFraction={SubsetFraction:R}|subsetCount={SubsetCount?.ToString() ?? ""}|includeLeast={IncludeLeast}|start={StartSeedUnsigned}|count={SeedCount}");

    public static SearchOptions FromArgs(string[] args)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var projectDirectory = Directory.GetCurrentDirectory();
        var worldJsonPath = FindDefaultWorldJson(baseDirectory, projectDirectory);
        var resultsDirectory = Path.Combine(projectDirectory, "results");
        var outputPath = Path.Combine(resultsDirectory, "clustered-seed-result.txt");
        var outputWasExplicit = false;
        var startSeed = int.MinValue;
        var seedCount = 1UL << 32;
        var degreeOfParallelism = Environment.ProcessorCount;
        var openResult = true;
        var maxRunTime = TimeSpan.FromMinutes(1);
        var clusteringMode = ClusteringMode.AllNodes;
        var subsetFraction = 0.20;
        int? subsetCount = null;
        var includeLeast = false;

        foreach (var arg in args)
        {
            if (arg.StartsWith("--output=", StringComparison.OrdinalIgnoreCase))
            {
                outputPath = arg["--output=".Length..];
                outputWasExplicit = true;
            }
            else if (arg.StartsWith("--start=", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(arg["--start=".Length..], out var parsedStart))
            {
                startSeed = parsedStart;
            }
            else if (arg.StartsWith("--count=", StringComparison.OrdinalIgnoreCase)
                && ulong.TryParse(arg["--count=".Length..], out var parsedCount))
            {
                seedCount = parsedCount;
            }
            else if (arg.StartsWith("--threads=", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(arg["--threads=".Length..], out var parsedThreads))
            {
                degreeOfParallelism = Math.Max(1, parsedThreads);
            }
            else if (arg.Equals("--no-open", StringComparison.OrdinalIgnoreCase))
            {
                openResult = false;
            }
            else if (arg.Equals("--include-least", StringComparison.OrdinalIgnoreCase)
                || arg.Equals("--least", StringComparison.OrdinalIgnoreCase))
            {
                includeLeast = true;
            }
            else if (arg.StartsWith("--max-minutes=", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(arg["--max-minutes=".Length..], out var parsedMinutes))
            {
                maxRunTime = TimeSpan.FromMinutes(Math.Max(0.01, parsedMinutes));
            }
            else if (arg.StartsWith("--max-seconds=", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(arg["--max-seconds=".Length..], out var parsedSeconds))
            {
                maxRunTime = TimeSpan.FromSeconds(Math.Max(1.0, parsedSeconds));
            }
            else if (arg.Equals("--subset", StringComparison.OrdinalIgnoreCase))
            {
                clusteringMode = ClusteringMode.Subset;
            }
            else if (arg.StartsWith("--cluster-mode=", StringComparison.OrdinalIgnoreCase))
            {
                var mode = arg["--cluster-mode=".Length..];
                clusteringMode = mode.Equals("subset", StringComparison.OrdinalIgnoreCase)
                    ? ClusteringMode.Subset
                    : ClusteringMode.AllNodes;
            }
            else if (arg.StartsWith("--subset-percent=", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(arg["--subset-percent=".Length..], out var parsedPercent))
            {
                clusteringMode = ClusteringMode.Subset;
                subsetFraction = Math.Clamp(parsedPercent / 100.0, 0.0001, 1.0);
                subsetCount = null;
            }
            else if (arg.StartsWith("--subset-fraction=", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(arg["--subset-fraction=".Length..], out var parsedFraction))
            {
                clusteringMode = ClusteringMode.Subset;
                subsetFraction = Math.Clamp(parsedFraction, 0.0001, 1.0);
                subsetCount = null;
            }
            else if (arg.StartsWith("--subset-count=", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(arg["--subset-count=".Length..], out var parsedSubsetCount))
            {
                clusteringMode = ClusteringMode.Subset;
                subsetCount = Math.Max(1, parsedSubsetCount);
            }
            else if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                worldJsonPath = arg;
            }
        }

        if (!outputWasExplicit)
        {
            outputPath = Path.Combine(resultsDirectory, GetDefaultOutputFileName(clusteringMode, subsetFraction, subsetCount, includeLeast));
        }

        return new SearchOptions(
            Path.GetFullPath(worldJsonPath),
            outputPath,
            unchecked((uint)startSeed),
            Math.Min(seedCount, 1UL << 32),
            degreeOfParallelism,
            openResult,
            maxRunTime,
            clusteringMode,
            subsetFraction,
            subsetCount,
            includeLeast);
    }

    public int GetSubsetSize(int resourceNodeCount)
    {
        if (resourceNodeCount <= 0)
        {
            return 0;
        }

        var requested = SubsetCount ?? (int)Math.Round(resourceNodeCount * SubsetFraction);
        return Math.Clamp(Math.Max(4, requested), 1, resourceNodeCount);
    }

    private static string GetDefaultOutputFileName(ClusteringMode clusteringMode, double subsetFraction, int? subsetCount, bool includeLeast)
    {
        var leastSuffix = includeLeast ? "-with-least" : "";
        if (clusteringMode == ClusteringMode.AllNodes)
        {
            return $"clustered-seed-result-all-nodes{leastSuffix}.txt";
        }

        var subsetSuffix = subsetCount is { } count
            ? $"count-{count}"
            : FormattableString.Invariant($"percent-{Math.Round(subsetFraction * 100.0, 4):0.####}");

        return $"clustered-seed-result-subset-{subsetSuffix}{leastSuffix}.txt";
    }

    private static string FindDefaultWorldJson(string baseDirectory, string projectDirectory)
    {
        var candidates = EnumerateParentCandidates(projectDirectory)
            .Concat(EnumerateParentCandidates(baseDirectory))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static IEnumerable<string> EnumerateParentCandidates(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            yield return Path.Combine(directory.FullName, "default-world.json");
            directory = directory.Parent;
        }
    }
}

public enum ClusteringMode
{
    AllNodes,
    Subset,
}
