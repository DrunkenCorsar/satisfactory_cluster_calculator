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
    bool IncludeMost,
    bool IncludeLeast)
{
    public string ResumeKey =>
        FormattableString.Invariant($"{ClusteringMode}|subsetFraction={SubsetFraction:R}|subsetCount={SubsetCount?.ToString() ?? ""}|includeMost={IncludeMost}|includeLeast={IncludeLeast}|includeUnchanged={IncludeUnchanged}|includeRandom={IncludeRandom}|start={StartSeedUnsigned}|count={SeedCount}");

    public bool IncludeUnchanged { get; init; } = true;

    public bool IncludeRandom { get; init; }

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
        TimeSpan? maxRunTime = null;
        var clusteringMode = ClusteringMode.AllNodes;
        var subsetFraction = 0.20;
        int? subsetCount = null;
        var includeMost = true;
        var includeLeast = false;
        var includeUnchanged = true;
        var includeRandom = false;

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
            else if (arg.Equals("--no-most", StringComparison.OrdinalIgnoreCase)
                || arg.Equals("--disable-most", StringComparison.OrdinalIgnoreCase))
            {
                includeMost = false;
            }
            else if (arg.Equals("--include-most", StringComparison.OrdinalIgnoreCase)
                || arg.Equals("--most", StringComparison.OrdinalIgnoreCase))
            {
                includeMost = true;
            }
            else if (arg.Equals("--include-random", StringComparison.OrdinalIgnoreCase)
                || arg.Equals("--random", StringComparison.OrdinalIgnoreCase))
            {
                includeRandom = true;
            }
            else if (arg.Equals("--no-random", StringComparison.OrdinalIgnoreCase)
                || arg.Equals("--disable-random", StringComparison.OrdinalIgnoreCase))
            {
                includeRandom = false;
            }
            else if (arg.Equals("--include-unchanged", StringComparison.OrdinalIgnoreCase)
                || arg.Equals("--unchanged", StringComparison.OrdinalIgnoreCase))
            {
                includeUnchanged = true;
            }
            else if (arg.Equals("--no-unchanged", StringComparison.OrdinalIgnoreCase)
                || arg.Equals("--disable-unchanged", StringComparison.OrdinalIgnoreCase))
            {
                includeUnchanged = false;
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

        if (maxRunTime is null)
        {
            throw new ArgumentException("""
                Missing required runtime limit.

                Add one of:
                  --max-seconds=N
                  --max-minutes=N

                Example:
                  dotnet run -c Release -- --subset --subset-percent=20 --max-minutes=180
                """);
        }

        if (!includeMost && !includeLeast)
        {
            throw new ArgumentException("Nothing to compute. Enable --most and/or --least.");
        }

        if (!includeUnchanged && !includeRandom)
        {
            throw new ArgumentException("No purity scenario selected. Enable --unchanged and/or --random.");
        }

        if (!outputWasExplicit)
        {
            outputPath = Path.Combine(resultsDirectory, GetDefaultOutputFileName(clusteringMode, subsetFraction, subsetCount, includeMost, includeLeast, includeUnchanged, includeRandom));
        }

        return new SearchOptions(
            Path.GetFullPath(worldJsonPath),
            outputPath,
            unchecked((uint)startSeed),
            Math.Min(seedCount, 1UL << 32),
            degreeOfParallelism,
            openResult,
            maxRunTime.Value,
            clusteringMode,
            subsetFraction,
            subsetCount,
            includeMost,
            includeLeast)
        {
            IncludeUnchanged = includeUnchanged,
            IncludeRandom = includeRandom,
        };
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

    private static string GetDefaultOutputFileName(
        ClusteringMode clusteringMode,
        double subsetFraction,
        int? subsetCount,
        bool includeMost,
        bool includeLeast,
        bool includeUnchanged,
        bool includeRandom)
    {
        var objectiveSuffix = (includeMost, includeLeast) switch
        {
            (true, false) => "",
            (true, true) => "-with-least",
            (false, true) => "-least-only",
            _ => "-no-objectives",
        };
        var scenarioSuffix = (includeUnchanged, includeRandom) switch
        {
            (true, false) => "",
            (true, true) => "-with-random",
            (false, true) => "-random-only",
            _ => "-no-scenarios",
        };

        if (clusteringMode == ClusteringMode.AllNodes)
        {
            return $"clustered-seed-result-all-nodes{objectiveSuffix}{scenarioSuffix}.txt";
        }

        var subsetSuffix = subsetCount is { } count
            ? $"count-{count}"
            : FormattableString.Invariant($"percent-{Math.Round(subsetFraction * 100.0, 4):0.####}");

        return $"clustered-seed-result-subset-{subsetSuffix}{objectiveSuffix}{scenarioSuffix}.txt";
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
