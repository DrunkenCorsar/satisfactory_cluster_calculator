using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SatisfactoryClusterCalculator;

public sealed class ClusteredSeedSearcher
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const long AllNodesChunkSize = 4_096;
    private const long SubsetChunkSize = 32;

    private readonly ResourceNodeDefinition[] _targetNodes;
    private readonly ResourceNodeInfo[] _sortedNodePool;
    private readonly ResourceDescriptor[] _resources;
    private readonly int[] _poolResourceIndices;
    private readonly float[] _targetX;
    private readonly float[] _targetY;
    private readonly float[] _targetZ;
    private readonly double[] _distanceSquared;
    private readonly double _worldDiagonal;
    private readonly string _outputPath;
    private readonly string _statePath;
    private readonly object _recordsLock = new();
    private readonly object _fileLock = new();

    private readonly SearchRecords _normalPurityRecords;
    private readonly SearchRecords _randomPurityRecords;

    private long _processedSeeds;
    private long _sessionStartOffset;
    private long _completedOffset;
    private ulong _totalSeeds;
    private ulong _startSeedUnsigned;
    private string _resumeKey = "";
    private string _worldJsonPath = "";
    private SearchOptions? _options;
    private Stopwatch? _stopwatch;

    public ClusteredSeedSearcher(IEnumerable<ResourceNodeDefinition> sourceNodes, string outputPath)
    {
        _targetNodes = sourceNodes
            .OrderBy(node => node.Name, StringComparer.Ordinal)
            .ToArray();
        _sortedNodePool = _targetNodes
            .Select(ResourceNodeInfo.From)
            .Order()
            .ToArray();
        _resources = _sortedNodePool
            .Select(node => node.Resource)
            .Distinct()
            .OrderBy(resource => resource.GetInternalName(), StringComparer.Ordinal)
            .ToArray();

        var resourceIndices = _resources
            .Select((resource, index) => (resource, index))
            .ToDictionary(pair => pair.resource, pair => pair.index);
        _poolResourceIndices = _sortedNodePool
            .Select(node => resourceIndices[node.Resource])
            .ToArray();
        _targetX = _targetNodes.Select(node => node.Location.X).ToArray();
        _targetY = _targetNodes.Select(node => node.Location.Y).ToArray();
        _targetZ = _targetNodes.Select(node => node.Location.Z).ToArray();
        _distanceSquared = BuildDistanceSquaredMatrix(_targetNodes);
        _worldDiagonal = CalculateWorldDiagonal(_targetNodes);
        _outputPath = outputPath;
        _statePath = outputPath + ".state.json";

        if (_targetNodes.Length == 0)
        {
            throw new InvalidDataException("World data contains no resource nodes.");
        }

        _normalPurityRecords = new SearchRecords(_resources.Length);
        _randomPurityRecords = new SearchRecords(_resources.Length);
    }

    public void Search(SearchOptions options)
    {
        var totalSeeds = options.SeedCount;
        var stopwatch = Stopwatch.StartNew();
        _totalSeeds = totalSeeds;
        _startSeedUnsigned = options.StartSeedUnsigned;
        _resumeKey = options.ResumeKey;
        _worldJsonPath = options.WorldJsonPath;
        _options = options;
        _stopwatch = stopwatch;

        var resumeOffset = LoadStateIfPresent(options);
        _completedOffset = checked((long)resumeOffset);
        _processedSeeds = checked((long)resumeOffset);
        _sessionStartOffset = checked((long)resumeOffset);

        using var timer = new Timer(_ => DrawProgress(totalSeeds, stopwatch), null, TimeSpan.Zero, ProgressInterval);
        using var saveTimer = new Timer(_ => SaveBestSnapshot(totalSeeds, stopwatch, finished: false), null, SaveInterval, SaveInterval);

        SetCursorVisible(false);
        using var interruptCancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            interruptCancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            var deadlineTimestamp = Stopwatch.GetTimestamp() + (long)(options.MaxRunTime.TotalSeconds * Stopwatch.Frequency);
            var nextOffset = checked((long)resumeOffset);
            var totalOffset = checked((long)totalSeeds);
            var chunkSize = options.ClusteringMode == ClusteringMode.Subset ? SubsetChunkSize : AllNodesChunkSize;
            var workers = Enumerable.Range(0, options.DegreeOfParallelism)
                .Select(_ => Task.Run(() =>
                {
                    var state = CreateWorkerState();
                    while (!interruptCancellation.IsCancellationRequested
                        && Stopwatch.GetTimestamp() < deadlineTimestamp)
                    {
                        var rangeStart = Interlocked.Add(ref nextOffset, chunkSize) - chunkSize;
                        if (rangeStart >= totalOffset)
                        {
                            break;
                        }

                        var rangeEnd = Math.Min(rangeStart + chunkSize, totalOffset);
                        for (var offset = rangeStart; offset < rangeEnd; offset++)
                        {
                            var seed = unchecked((int)(options.StartSeedUnsigned + (ulong)offset));

                            if (options.IncludeUnchanged)
                            {
                                var normalScore = ScoreSeed(seed, state.NormalBuffers, randomizePurity: false);
                                state.NormalRecords.Consider(normalScore, state.NormalBuffers, PurityScenario.Unchanged, options.IncludeMost, options.IncludeLeast);
                            }

                            if (options.IncludeRandom)
                            {
                                var randomScore = ScoreSeed(seed, state.RandomBuffers, randomizePurity: true);
                                state.RandomRecords.Consider(randomScore, state.RandomBuffers, PurityScenario.Random, options.IncludeMost, options.IncludeLeast);
                            }
                        }

                        MergeWorkerRecords(state);
                        state.NormalRecords.Clear();
                        state.RandomRecords.Clear();

                        Interlocked.Add(ref _processedSeeds, rangeEnd - rangeStart);
                    }

                    MergeWorkerRecords(state);
                }, CancellationToken.None))
                .ToArray();

            Task.WaitAll(workers);
            _completedOffset = Math.Min(Volatile.Read(ref nextOffset), totalOffset);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            SetCursorVisible(true);
        }

        DrawProgress(totalSeeds, stopwatch);
        Console.WriteLine();
        SaveBestSnapshot(totalSeeds, stopwatch, finished: _completedOffset >= checked((long)totalSeeds));
    }

    private WorkerState CreateWorkerState()
    {
        return new WorkerState(
            new SearchBuffers(_targetNodes.Length, _resources.Length),
            new SearchBuffers(_targetNodes.Length, _resources.Length),
            new SearchRecords(_resources.Length),
            new SearchRecords(_resources.Length));
    }

    private void MergeWorkerRecords(WorkerState state)
    {
            var changed = false;
            lock (_recordsLock)
            {
                changed |= _normalPurityRecords.MergeFrom(state.NormalRecords, MaterializeCandidate);
                changed |= _randomPurityRecords.MergeFrom(state.RandomRecords, MaterializeCandidate);
            }

        if (changed)
        {
            SaveBestSnapshot(_totalSeeds, _stopwatch ?? Stopwatch.StartNew(), finished: false);
        }
    }

    private ScoredSeed ScoreSeed(int seed, SearchBuffers buffers, bool randomizePurity)
    {
        var options = _options ?? throw new InvalidOperationException("Search options were not initialized.");
        var rng = new RandomStream(seed);
        buffers.Reset(_sortedNodePool.Length);
        var activeCount = _sortedNodePool.Length;

        for (var i = 0; i < _targetNodes.Length; i++)
        {
            var activePoolIndex = (int)rng.FRandRange(0.0f, activeCount);
            var poolIndex = buffers.ActivePool.FindByOrder(activePoolIndex);
            buffers.ActivePool.Add(poolIndex, -1);
            activeCount--;

            var nodeInfo = _sortedNodePool[poolIndex];
            var resourceIndex = _poolResourceIndices[poolIndex];

            buffers.AssignedPoolIndices[i] = poolIndex;
            buffers.AssignedPurities[i] = randomizePurity ? GetRandomPurity(rng) : nodeInfo.Purity;
            buffers.ResourceNodeIndices[resourceIndex, buffers.Counts[resourceIndex]] = i;
            buffers.SumX[resourceIndex] += _targetX[i];
            buffers.SumY[resourceIndex] += _targetY[i];
            buffers.SumZ[resourceIndex] += _targetZ[i];
            buffers.Counts[resourceIndex]++;
        }

        if (options.ClusteringMode == ClusteringMode.AllNodes)
        {
            ScoreAllNodeClusters(buffers);
        }
        else
        {
            ScoreSubsetClusters(buffers, options);
        }

        var mostClusterSum = 0.0;
        var leastClusterSum = 0.0;
        var scoredResourceCount = 0;
        var mostWorstClusterLevel = double.PositiveInfinity;
        var leastWorstClusterLevel = double.PositiveInfinity;
        for (var resourceIndex = 0; resourceIndex < _resources.Length; resourceIndex++)
        {
            var count = buffers.Counts[resourceIndex];
            if (count <= 1)
            {
                continue;
            }

            var mostClusterLevel = buffers.MostClusterLevels[resourceIndex];
            var leastClusterLevel = buffers.LeastClusterLevels[resourceIndex];

            mostClusterSum += mostClusterLevel;
            if (options.IncludeLeast)
            {
                leastClusterSum += leastClusterLevel;
            }
            scoredResourceCount++;

            if (mostClusterLevel < mostWorstClusterLevel)
            {
                mostWorstClusterLevel = mostClusterLevel;
            }

            if (options.IncludeLeast && leastClusterLevel < leastWorstClusterLevel)
            {
                leastWorstClusterLevel = leastClusterLevel;
            }
        }

        var mostAverageClusterLevel = mostClusterSum / scoredResourceCount;
        var leastAverageClusterLevel = options.IncludeLeast ? leastClusterSum / scoredResourceCount : 0.0;
        var mostFinalScore = (mostAverageClusterLevel * 0.85) + (mostWorstClusterLevel * 0.15);
        var leastFinalScore = options.IncludeLeast
            ? (leastAverageClusterLevel * 0.85) + (leastWorstClusterLevel * 0.15)
            : 0.0;

        return new ScoredSeed(
            seed,
            mostFinalScore,
            leastFinalScore,
            mostAverageClusterLevel,
            leastAverageClusterLevel,
            mostWorstClusterLevel,
            options.IncludeLeast ? leastWorstClusterLevel : 0.0);
    }

    private void ScoreAllNodeClusters(SearchBuffers buffers)
    {
        for (var resourceIndex = 0; resourceIndex < _resources.Length; resourceIndex++)
        {
            var count = buffers.Counts[resourceIndex];
            if (count <= 1)
            {
                continue;
            }

            for (var localIndex = 0; localIndex < count; localIndex++)
            {
                var targetIndex = buffers.ResourceNodeIndices[resourceIndex, localIndex];
                var centroidX = (float)(buffers.SumX[resourceIndex] / count);
                var centroidY = (float)(buffers.SumY[resourceIndex] / count);
                var centroidZ = (float)(buffers.SumZ[resourceIndex] / count);
                var dx = _targetX[targetIndex] - centroidX;
                var dy = _targetY[targetIndex] - centroidY;
                var dz = _targetZ[targetIndex] - centroidZ;
                buffers.DistanceSum[resourceIndex] += Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
            }

            var averageDistanceToCenter = buffers.DistanceSum[resourceIndex] / count;
            var clusterLevel = GetClusterLevel(averageDistanceToCenter);
            buffers.MostAverageDistanceToCenter[resourceIndex] = averageDistanceToCenter;
            buffers.MostClusterLevels[resourceIndex] = clusterLevel;
            buffers.MostCenterX[resourceIndex] = (float)(buffers.SumX[resourceIndex] / count);
            buffers.MostCenterY[resourceIndex] = (float)(buffers.SumY[resourceIndex] / count);
            buffers.MostCenterZ[resourceIndex] = (float)(buffers.SumZ[resourceIndex] / count);
            if ((_options?.IncludeLeast).GetValueOrDefault())
            {
                buffers.LeastAverageDistanceToCenter[resourceIndex] = averageDistanceToCenter;
                buffers.LeastClusterLevels[resourceIndex] = clusterLevel;
                buffers.LeastCenterX[resourceIndex] = buffers.MostCenterX[resourceIndex];
                buffers.LeastCenterY[resourceIndex] = buffers.MostCenterY[resourceIndex];
                buffers.LeastCenterZ[resourceIndex] = buffers.MostCenterZ[resourceIndex];
            }
            buffers.ScoredNodeCounts[resourceIndex] = count;
        }
    }

    private void ScoreSubsetClusters(SearchBuffers buffers, SearchOptions options)
    {
        for (var resourceIndex = 0; resourceIndex < _resources.Length; resourceIndex++)
        {
            var count = buffers.Counts[resourceIndex];
            if (count <= 1)
            {
                continue;
            }

            var subsetSize = options.GetSubsetSize(count);
            var subsetScores = ScoreSubsets(buffers, resourceIndex, count, subsetSize, options.IncludeLeast);

            buffers.MostAverageDistanceToCenter[resourceIndex] = subsetScores.Most.AverageDistanceToCenter;
            buffers.MostClusterLevels[resourceIndex] = subsetScores.Most.ClusterLevel;
            buffers.MostCenterX[resourceIndex] = subsetScores.Most.CenterX;
            buffers.MostCenterY[resourceIndex] = subsetScores.Most.CenterY;
            buffers.MostCenterZ[resourceIndex] = subsetScores.Most.CenterZ;
            if (options.IncludeLeast)
            {
                buffers.LeastAverageDistanceToCenter[resourceIndex] = subsetScores.Least.AverageDistanceToCenter;
                buffers.LeastClusterLevels[resourceIndex] = subsetScores.Least.ClusterLevel;
                buffers.LeastCenterX[resourceIndex] = subsetScores.Least.CenterX;
                buffers.LeastCenterY[resourceIndex] = subsetScores.Least.CenterY;
                buffers.LeastCenterZ[resourceIndex] = subsetScores.Least.CenterZ;
            }
            buffers.ScoredNodeCounts[resourceIndex] = subsetSize;
        }
    }

    private SubsetScores ScoreSubsets(
        SearchBuffers buffers,
        int resourceIndex,
        int resourceNodeCount,
        int subsetSize,
        bool includeLeast)
    {
        var bestMostAverageDistance = 0.0;
        var bestMostClusterLevel = double.NegativeInfinity;
        var bestLeastAverageDistance = 0.0;
        var bestLeastClusterLevel = double.PositiveInfinity;

        for (var anchorLocalIndex = 0; anchorLocalIndex < resourceNodeCount; anchorLocalIndex++)
        {
            var anchorTargetIndex = buffers.ResourceNodeIndices[resourceIndex, anchorLocalIndex];

            for (var localIndex = 0; localIndex < resourceNodeCount; localIndex++)
            {
                var targetIndex = buffers.ResourceNodeIndices[resourceIndex, localIndex];
                var distance = _distanceSquared[(anchorTargetIndex * _targetNodes.Length) + targetIndex];
                buffers.NearestCandidateIndices[localIndex] = targetIndex;
                buffers.NearestCandidateDistances[localIndex] = distance;
                buffers.FarthestCandidateIndices[localIndex] = targetIndex;
                buffers.FarthestCandidateDistances[localIndex] = distance;
            }

            SelectPartial(buffers.NearestCandidateDistances, buffers.NearestCandidateIndices, resourceNodeCount, subsetSize, smallest: true);
            var mostAverageDistance = GetAverageDistanceToSubsetCenter(buffers.NearestCandidateIndices, 0, subsetSize);
            var mostClusterLevel = GetClusterLevel(mostAverageDistance);

            if (mostClusterLevel > bestMostClusterLevel)
            {
                bestMostClusterLevel = mostClusterLevel;
                bestMostAverageDistance = mostAverageDistance;
                SaveSubsetCenter(buffers.NearestCandidateIndices, 0, subsetSize, out var centerX, out var centerY, out var centerZ);
                buffers.TempMostCenterX = centerX;
                buffers.TempMostCenterY = centerY;
                buffers.TempMostCenterZ = centerZ;
            }

            if (includeLeast)
            {
                SelectPartial(buffers.FarthestCandidateDistances, buffers.FarthestCandidateIndices, resourceNodeCount, subsetSize, smallest: false);
                var leastAverageDistance = GetAverageDistanceToSubsetCenter(buffers.FarthestCandidateIndices, 0, subsetSize);
                var leastClusterLevel = GetClusterLevel(leastAverageDistance);

                if (leastClusterLevel < bestLeastClusterLevel)
                {
                    bestLeastClusterLevel = leastClusterLevel;
                    bestLeastAverageDistance = leastAverageDistance;
                    SaveSubsetCenter(buffers.FarthestCandidateIndices, 0, subsetSize, out var centerX, out var centerY, out var centerZ);
                    buffers.TempLeastCenterX = centerX;
                    buffers.TempLeastCenterY = centerY;
                    buffers.TempLeastCenterZ = centerZ;
                }
            }
        }

        return new SubsetScores(
            new SubsetScore(bestMostAverageDistance, bestMostClusterLevel, buffers.TempMostCenterX, buffers.TempMostCenterY, buffers.TempMostCenterZ),
            new SubsetScore(bestLeastAverageDistance, bestLeastClusterLevel, buffers.TempLeastCenterX, buffers.TempLeastCenterY, buffers.TempLeastCenterZ));
    }

    private static void SelectPartial(double[] distances, int[] indices, int length, int selectedCount, bool smallest)
    {
        var left = 0;
        var right = length - 1;
        var target = selectedCount - 1;

        while (left < right)
        {
            var pivotIndex = left + ((right - left) / 2);
            pivotIndex = Partition(distances, indices, left, right, pivotIndex, smallest);

            if (target == pivotIndex)
            {
                return;
            }

            if (target < pivotIndex)
            {
                right = pivotIndex - 1;
            }
            else
            {
                left = pivotIndex + 1;
            }
        }
    }

    private static int Partition(double[] distances, int[] indices, int left, int right, int pivotIndex, bool smallest)
    {
        var pivotDistance = distances[pivotIndex];
        var pivotTargetIndex = indices[pivotIndex];
        Swap(distances, indices, pivotIndex, right);

        var storeIndex = left;
        for (var i = left; i < right; i++)
        {
            if (ComesBefore(distances[i], indices[i], pivotDistance, pivotTargetIndex, smallest))
            {
                Swap(distances, indices, storeIndex, i);
                storeIndex++;
            }
        }

        Swap(distances, indices, right, storeIndex);
        return storeIndex;
    }

    private static bool ComesBefore(double leftDistance, int leftIndex, double rightDistance, int rightIndex, bool smallest)
    {
        if (leftDistance < rightDistance)
        {
            return smallest;
        }

        if (leftDistance > rightDistance)
        {
            return !smallest;
        }

        return leftIndex < rightIndex;
    }

    private static void Swap(double[] distances, int[] indices, int left, int right)
    {
        if (left == right)
        {
            return;
        }

        (distances[left], distances[right]) = (distances[right], distances[left]);
        (indices[left], indices[right]) = (indices[right], indices[left]);
    }

    private double GetAverageDistanceToSubsetCenter(int[] targetIndices, int startIndex, int count)
    {
        SaveSubsetCenter(targetIndices, startIndex, count, out var centroidX, out var centroidY, out var centroidZ);
        var distanceSum = 0.0;
        for (var i = startIndex; i < startIndex + count; i++)
        {
            var targetIndex = targetIndices[i];
            var dx = _targetX[targetIndex] - centroidX;
            var dy = _targetY[targetIndex] - centroidY;
            var dz = _targetZ[targetIndex] - centroidZ;
            distanceSum += Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        }

        return distanceSum / count;
    }

    private void SaveSubsetCenter(
        int[] targetIndices,
        int startIndex,
        int count,
        out float centerX,
        out float centerY,
        out float centerZ)
    {
        var sumX = 0.0;
        var sumY = 0.0;
        var sumZ = 0.0;
        for (var i = startIndex; i < startIndex + count; i++)
        {
            var targetIndex = targetIndices[i];
            sumX += _targetX[targetIndex];
            sumY += _targetY[targetIndex];
            sumZ += _targetZ[targetIndex];
        }

        centerX = (float)(sumX / count);
        centerY = (float)(sumY / count);
        centerZ = (float)(sumZ / count);
    }

    private double GetClusterLevel(double averageDistanceToCenter)
    {
        var normalizedSpread = averageDistanceToCenter / _worldDiagonal;
        return 1.0 / (1.0 + normalizedSpread);
    }

    private static double[] BuildDistanceSquaredMatrix(IReadOnlyList<ResourceNodeDefinition> nodes)
    {
        var distances = new double[nodes.Count * nodes.Count];
        for (var left = 0; left < nodes.Count; left++)
        {
            for (var right = 0; right < nodes.Count; right++)
            {
                var dx = nodes[left].Location.X - nodes[right].Location.X;
                var dy = nodes[left].Location.Y - nodes[right].Location.Y;
                var dz = nodes[left].Location.Z - nodes[right].Location.Z;
                distances[(left * nodes.Count) + right] = (dx * dx) + (dy * dy) + (dz * dz);
            }
        }

        return distances;
    }

    private SearchResult CreateSearchResult(string scenarioName, ScoredSeed score, SearchBuffers buffers, ScorePerspective perspective)
    {
        var resourceScores = new List<ResourceClusterScore>(_resources.Length);
        for (var resourceIndex = 0; resourceIndex < _resources.Length; resourceIndex++)
        {
            var count = buffers.Counts[resourceIndex];
            if (count <= 1)
            {
                continue;
            }

            resourceScores.Add(new ResourceClusterScore(
                _resources[resourceIndex],
                buffers.ScoredNodeCounts[resourceIndex],
                perspective == ScorePerspective.Most
                    ? buffers.MostAverageDistanceToCenter[resourceIndex]
                    : buffers.LeastAverageDistanceToCenter[resourceIndex],
                perspective == ScorePerspective.Most
                    ? buffers.MostClusterLevels[resourceIndex]
                    : buffers.LeastClusterLevels[resourceIndex],
                perspective == ScorePerspective.Most
                    ? new NodePosition(buffers.MostCenterX[resourceIndex], buffers.MostCenterY[resourceIndex], buffers.MostCenterZ[resourceIndex])
                    : new NodePosition(buffers.LeastCenterX[resourceIndex], buffers.LeastCenterY[resourceIndex], buffers.LeastCenterZ[resourceIndex])));
        }

        var generatedNodes = new GeneratedResourceNode[_targetNodes.Length];
        for (var i = 0; i < _targetNodes.Length; i++)
        {
            var nodeInfo = _sortedNodePool[buffers.AssignedPoolIndices[i]];
            generatedNodes[i] = new GeneratedResourceNode(
                _targetNodes[i].Name,
                _targetNodes[i].Location,
                nodeInfo.Resource,
                buffers.AssignedPurities[i]);
        }

        return new SearchResult(
            scenarioName,
            score.Seed,
            perspective == ScorePerspective.Most ? score.MostScore : score.LeastScore,
            perspective == ScorePerspective.Most ? score.MostAverageClusterLevel : score.LeastAverageClusterLevel,
            perspective == ScorePerspective.Most ? score.MostWorstClusterLevel : score.LeastWorstClusterLevel,
            resourceScores,
            generatedNodes);
    }

    private Candidate MaterializeCandidate(Candidate candidate)
    {
        if (candidate.Result is not null)
        {
            return candidate;
        }

        var buffers = new SearchBuffers(_targetNodes.Length, _resources.Length);
        var score = ScoreSeed(candidate.Seed, buffers, candidate.Scenario == PurityScenario.Random);
        var scenarioName = candidate.Scenario == PurityScenario.Random ? "Purity random" : "Purity unchanged";
        var result = CreateSearchResult(scenarioName, score, buffers, candidate.Perspective);

        return candidate with { Result = result };
    }

    private void SaveBestSnapshot(ulong totalSeeds, Stopwatch stopwatch, bool finished)
    {
        lock (_fileLock)
        {
            SearchRecords normalSnapshot;
            SearchRecords randomSnapshot;
            lock (_recordsLock)
            {
                normalSnapshot = _normalPurityRecords.CloneShallow();
                randomSnapshot = _randomPurityRecords.CloneShallow();
            }

            var processed = Volatile.Read(ref _processedSeeds);
            SaveState(normalSnapshot, randomSnapshot, Math.Min((ulong)Math.Max(0, _completedOffset), totalSeeds), totalSeeds, finished);
            File.WriteAllText(
                _outputPath,
                FormatReport(normalSnapshot, randomSnapshot, processed, totalSeeds, stopwatch.Elapsed, finished));
        }
    }

    private ulong LoadStateIfPresent(SearchOptions options)
    {
        if (!File.Exists(_statePath))
        {
            return 0;
        }

        try
        {
            var state = JsonSerializer.Deserialize<SearchState>(File.ReadAllText(_statePath));
            if (state is null
                || !string.Equals(Path.GetFullPath(state.WorldJsonPath), options.WorldJsonPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(state.ResumeKey, options.ResumeKey, StringComparison.Ordinal)
                || state.StartSeedUnsigned != options.StartSeedUnsigned
                || state.SeedCount != options.SeedCount
                || state.ResourceCount != _resources.Length)
            {
                return 0;
            }

            _normalPurityRecords.ReplaceFrom(state.NormalPurityRecords);
            _randomPurityRecords.ReplaceFrom(state.RandomPurityRecords);
            return Math.Min(state.CompletedOffset, options.SeedCount);
        }
        catch
        {
            return 0;
        }
    }

    private void SaveState(
        SearchRecords normalRecords,
        SearchRecords randomRecords,
        ulong completedOffset,
        ulong totalSeeds,
        bool finished)
    {
        var state = new SearchState
        {
            Version = 1,
            WorldJsonPath = _worldJsonPath,
            ResumeKey = _resumeKey,
            StartSeedUnsigned = _startSeedUnsigned,
            CompletedOffset = completedOffset,
            SeedCount = totalSeeds,
            ResourceCount = _resources.Length,
            Finished = finished,
            NormalPurityRecords = normalRecords.ToState(),
            RandomPurityRecords = randomRecords.ToState(),
        };

        File.WriteAllText(_statePath, JsonSerializer.Serialize(state, JsonOptions));
    }

    private string FormatReport(
        SearchRecords normalRecords,
        SearchRecords randomRecords,
        long processedSeeds,
        ulong totalSeeds,
        TimeSpan elapsed,
        bool finished)
    {
        var builder = new StringBuilder();
        builder.AppendLine(finished ? "Finished clustered seed search" : "Clustered seed search - best records so far");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Clustering mode: {_options?.ClusteringMode}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Most clustered records: {(_options?.IncludeMost == true ? "enabled" : "disabled")}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Least clustered records: {(_options?.IncludeLeast == true ? "enabled" : "disabled")}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Purity unchanged scenario: {(_options?.IncludeUnchanged == true ? "enabled" : "disabled")}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Purity random scenario: {(_options?.IncludeRandom == true ? "enabled" : "disabled")}");
        if (_options?.ClusteringMode == ClusteringMode.Subset)
        {
            var subsetText = _options.SubsetCount is { } subsetCount
                ? $"fixed count {subsetCount}"
                : $"{_options.SubsetFraction:P2} of each resource, minimum 4";
            builder.AppendLine(CultureInfo.InvariantCulture, $"Subset size: {subsetText}");
        }

        builder.AppendLine(CultureInfo.InvariantCulture, $"Processed seeds: {processedSeeds:N0} / {totalSeeds:N0}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Resume offset: {_completedOffset:N0}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"State file: {_statePath}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Elapsed: {elapsed}");
        builder.AppendLine();

        if (_options?.IncludeUnchanged == true)
        {
            AppendScenario(builder, "Purity unchanged", normalRecords);
        }

        if (_options?.IncludeRandom == true)
        {
            if (_options?.IncludeUnchanged == true)
            {
                builder.AppendLine();
            }

            AppendScenario(builder, "Purity random", randomRecords);
        }

        return builder.ToString();
    }

    private void AppendScenario(StringBuilder builder, string title, SearchRecords records)
    {
        builder.AppendLine($"== {title} ==");
        if ((_options?.IncludeMost).GetValueOrDefault())
        {
            AppendCandidate(builder, "Overall most clustered", records.OverallMost, includeResourceDetails: true);
        }

        if ((_options?.IncludeLeast).GetValueOrDefault())
        {
            AppendCandidate(builder, "Overall least clustered", records.OverallLeast, includeResourceDetails: true);
        }

        if ((_options?.IncludeMost).GetValueOrDefault())
        {
            builder.AppendLine();
            builder.AppendLine("Most clustered by resource:");
            for (var i = 0; i < _resources.Length; i++)
            {
                AppendCandidate(builder, $"  {_resources[i].GetDisplayName()}", records.ResourceMost[i], includeResourceDetails: false, resource: _resources[i]);
            }
        }

        if ((_options?.IncludeLeast).GetValueOrDefault())
        {
            builder.AppendLine();
            builder.AppendLine("Least clustered by resource:");
            for (var i = 0; i < _resources.Length; i++)
            {
                AppendCandidate(builder, $"  {_resources[i].GetDisplayName()}", records.ResourceLeast[i], includeResourceDetails: false, resource: _resources[i]);
            }
        }
    }

    private static void AppendCandidate(
        StringBuilder builder,
        string label,
        Candidate? candidate,
        bool includeResourceDetails,
        ResourceDescriptor? resource = null)
    {
        if (candidate is null)
        {
            builder.AppendLine($"{label}: not scored yet");
            return;
        }

        var result = candidate.Result;
        if (result is null)
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"{label}: seed={candidate.Seed}, record_score={candidate.RankScore:F9}");
            return;
        }

        builder.AppendLine(CultureInfo.InvariantCulture,
            $"{label}: seed={result.Seed}, record_score={candidate.RankScore:F9}, overall={result.Score:F9}, average={result.AverageClusterLevel:F9}, worst={result.WorstClusterLevel:F9}");
        if (includeResourceDetails)
        {
            foreach (var score in result.ResourceScores.OrderBy(score => score.Resource.GetInternalName(), StringComparer.Ordinal))
            {
                builder.AppendLine(CultureInfo.InvariantCulture,
                    $"    {score.Resource.GetDisplayName(),-14} nodes={score.NodeCount,3} center=({score.Center.X:F2}, {score.Center.Y:F2}, {score.Center.Z:F2}) cluster={score.ClusterLevel:F9} avg_distance={score.AverageDistanceToCenter:F2}");
            }
        }
        else
        {
            var score = resource is null
                ? result.ResourceScores.FirstOrDefault()
                : result.ResourceScores.FirstOrDefault(score => score.Resource == resource);
            if (score is not null)
            {
                builder.AppendLine(CultureInfo.InvariantCulture,
                    $"      nodes={score.NodeCount,3} center=({score.Center.X:F2}, {score.Center.Y:F2}, {score.Center.Z:F2}) cluster={score.ClusterLevel:F9} avg_distance={score.AverageDistanceToCenter:F2}");
            }
        }
    }

    private void DrawProgress(ulong totalSeeds, Stopwatch stopwatch)
    {
        var processed = Math.Min((ulong)Math.Max(0, Volatile.Read(ref _processedSeeds)), totalSeeds);
        var sessionProcessed = Math.Max(0, Volatile.Read(ref _processedSeeds) - Volatile.Read(ref _sessionStartOffset));
        var ratio = totalSeeds == 0 ? 1.0 : (double)processed / totalSeeds;
        var seedsPerSecond = stopwatch.Elapsed.TotalSeconds <= 0.0 ? 0.0 : sessionProcessed / stopwatch.Elapsed.TotalSeconds;
        Candidate? normalCandidate;
        Candidate? randomCandidate;

        lock (_recordsLock)
        {
            normalCandidate = GetProgressCandidate(_normalPurityRecords);
            randomCandidate = GetProgressCandidate(_randomPurityRecords);
        }

        var progressParts = new List<string>();
        if (_options?.IncludeUnchanged == true)
        {
            progressParts.Add($"unchanged best {DescribeProgressCandidate(normalCandidate)}");
        }

        if (_options?.IncludeRandom == true)
        {
            progressParts.Add($"random best {DescribeProgressCandidate(randomCandidate)}");
        }

        var line = FormattableString.Invariant(
            $"Processed {processed:N0}/{totalSeeds:N0} ({ratio:P4}) | elapsed {stopwatch.Elapsed:hh\\:mm\\:ss} | {seedsPerSecond:N0} seeds/s | {string.Join(" | ", progressParts)}");

        if (Console.IsOutputRedirected)
        {
            Console.WriteLine(line);
        }
        else
        {
            var width = Math.Max(20, Console.WindowWidth - 1);
            Console.Write("\r" + line.PadRight(width)[..width]);
        }
    }

    private Candidate? GetProgressCandidate(SearchRecords records)
    {
        return _options?.IncludeMost == true
            ? records.OverallMost
            : records.OverallLeast;
    }

    private static string DescribeProgressCandidate(Candidate? candidate)
    {
        return candidate is null
            ? "not scored yet"
            : FormattableString.Invariant($"seed={candidate.Seed}, score={candidate.RankScore:F9}");
    }

    private static ResourcePurity GetRandomPurity(RandomStream rng)
    {
        return (int)rng.FRandRange(0.0f, 3.0f) switch
        {
            0 => ResourcePurity.Impure,
            1 => ResourcePurity.Normal,
            2 => ResourcePurity.Pure,
            _ => ResourcePurity.Normal,
        };
    }

    private static double CalculateWorldDiagonal(IReadOnlyCollection<ResourceNodeDefinition> nodes)
    {
        var minX = nodes.Min(node => node.Location.X);
        var maxX = nodes.Max(node => node.Location.X);
        var minY = nodes.Min(node => node.Location.Y);
        var maxY = nodes.Max(node => node.Location.Y);
        var minZ = nodes.Min(node => node.Location.Z);
        var maxZ = nodes.Max(node => node.Location.Z);
        var dx = maxX - minX;
        var dy = maxY - minY;
        var dz = maxZ - minZ;
        var diagonal = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));

        return diagonal <= 0.0 ? 1.0 : diagonal;
    }

    private static void SetCursorVisible(bool visible)
    {
        try
        {
            if (!Console.IsOutputRedirected)
            {
                Console.CursorVisible = visible;
            }
        }
        catch (IOException)
        {
        }
    }

    private sealed record ResourceNodeInfo(ResourceDescriptor Resource, ResourcePurity Purity)
        : IComparable<ResourceNodeInfo>
    {
        public static ResourceNodeInfo From(ResourceNodeDefinition node)
        {
            return new ResourceNodeInfo(node.Resource, node.Purity);
        }

        public int CompareTo(ResourceNodeInfo? other)
        {
            if (other is null)
            {
                return 1;
            }

            var resourceComparison = string.CompareOrdinal(Resource.GetInternalName(), other.Resource.GetInternalName());
            return resourceComparison != 0 ? resourceComparison : Purity.CompareTo(other.Purity);
        }
    }

    private sealed record WorkerState(
        SearchBuffers NormalBuffers,
        SearchBuffers RandomBuffers,
        SearchRecords NormalRecords,
        SearchRecords RandomRecords);

    private sealed record Candidate(
        double RankScore,
        int Seed,
        PurityScenario Scenario,
        ScorePerspective Perspective,
        SearchResult? Result);

    private enum ScorePerspective
    {
        Most,
        Least,
    }

    private enum PurityScenario
    {
        Unchanged,
        Random,
    }

    private readonly record struct ScoredSeed(
        int Seed,
        double MostScore,
        double LeastScore,
        double MostAverageClusterLevel,
        double LeastAverageClusterLevel,
        double MostWorstClusterLevel,
        double LeastWorstClusterLevel);

    private readonly record struct SubsetScore(
        double AverageDistanceToCenter,
        double ClusterLevel,
        float CenterX,
        float CenterY,
        float CenterZ);

    private readonly record struct SubsetScores(SubsetScore Most, SubsetScore Least);

    private sealed class SearchRecords
    {
        public SearchRecords(int resourceCount)
        {
            ResourceMost = new Candidate?[resourceCount];
            ResourceLeast = new Candidate?[resourceCount];
        }

        public Candidate? OverallMost { get; private set; }

        public Candidate? OverallLeast { get; private set; }

        public Candidate?[] ResourceMost { get; }

        public Candidate?[] ResourceLeast { get; }

        public void Consider(
            ScoredSeed score,
            SearchBuffers buffers,
            PurityScenario scenario,
            bool includeMost,
            bool includeLeast)
        {
            if (includeMost && (OverallMost is null || score.MostScore > OverallMost.RankScore))
            {
                OverallMost = new Candidate(score.MostScore, score.Seed, scenario, ScorePerspective.Most, null);
            }

            if (includeLeast && (OverallLeast is null || score.LeastScore < OverallLeast.RankScore))
            {
                OverallLeast = new Candidate(score.LeastScore, score.Seed, scenario, ScorePerspective.Least, null);
            }

            for (var resourceIndex = 0; resourceIndex < ResourceMost.Length; resourceIndex++)
            {
                if (buffers.Counts[resourceIndex] <= 1)
                {
                    continue;
                }

                if (includeMost)
                {
                    var mostClusterLevel = buffers.MostClusterLevels[resourceIndex];
                    if (ResourceMost[resourceIndex] is null || mostClusterLevel > ResourceMost[resourceIndex]!.RankScore)
                    {
                        ResourceMost[resourceIndex] = new Candidate(mostClusterLevel, score.Seed, scenario, ScorePerspective.Most, null);
                    }
                }

                if (!includeLeast)
                {
                    continue;
                }

                var leastClusterLevel = buffers.LeastClusterLevels[resourceIndex];
                if (ResourceLeast[resourceIndex] is null || leastClusterLevel < ResourceLeast[resourceIndex]!.RankScore)
                {
                    ResourceLeast[resourceIndex] = new Candidate(leastClusterLevel, score.Seed, scenario, ScorePerspective.Least, null);
                }
            }
        }

        public void Clear()
        {
            OverallMost = null;
            OverallLeast = null;
            Array.Clear(ResourceMost);
            Array.Clear(ResourceLeast);
        }

        public bool MergeFrom(SearchRecords other, Func<Candidate, Candidate> materialize)
        {
            var changed = false;
            if (other.OverallMost is not null && (OverallMost is null || other.OverallMost.RankScore > OverallMost.RankScore))
            {
                OverallMost = materialize(other.OverallMost);
                changed = true;
            }

            if (other.OverallLeast is not null && (OverallLeast is null || other.OverallLeast.RankScore < OverallLeast.RankScore))
            {
                OverallLeast = materialize(other.OverallLeast);
                changed = true;
            }

            for (var i = 0; i < ResourceMost.Length; i++)
            {
                if (other.ResourceMost[i] is not null
                    && (ResourceMost[i] is null || other.ResourceMost[i]!.RankScore > ResourceMost[i]!.RankScore))
                {
                    ResourceMost[i] = materialize(other.ResourceMost[i]!);
                    changed = true;
                }

                if (other.ResourceLeast[i] is not null
                    && (ResourceLeast[i] is null || other.ResourceLeast[i]!.RankScore < ResourceLeast[i]!.RankScore))
                {
                    ResourceLeast[i] = materialize(other.ResourceLeast[i]!);
                    changed = true;
                }
            }

            return changed;
        }

        public SearchRecords CloneShallow()
        {
            var clone = new SearchRecords(ResourceMost.Length)
            {
                OverallMost = OverallMost,
                OverallLeast = OverallLeast,
            };

            Array.Copy(ResourceMost, clone.ResourceMost, ResourceMost.Length);
            Array.Copy(ResourceLeast, clone.ResourceLeast, ResourceLeast.Length);

            return clone;
        }

        public SearchRecordsState ToState()
        {
            return new SearchRecordsState
            {
                OverallMost = CandidateState.From(OverallMost),
                OverallLeast = CandidateState.From(OverallLeast),
                ResourceMost = ResourceMost.Select(CandidateState.From).ToArray(),
                ResourceLeast = ResourceLeast.Select(CandidateState.From).ToArray(),
            };
        }

        public void ReplaceFrom(SearchRecordsState? state)
        {
            if (state is null)
            {
                return;
            }

            OverallMost = state.OverallMost?.ToCandidate();
            OverallLeast = state.OverallLeast?.ToCandidate();

            for (var i = 0; i < ResourceMost.Length; i++)
            {
                ResourceMost[i] = i < state.ResourceMost.Length ? state.ResourceMost[i]?.ToCandidate() : null;
                ResourceLeast[i] = i < state.ResourceLeast.Length ? state.ResourceLeast[i]?.ToCandidate() : null;
            }
        }
    }

    private sealed class SearchState
    {
        public int Version { get; set; }

        public string WorldJsonPath { get; set; } = "";

        public string ResumeKey { get; set; } = "";

        public ulong StartSeedUnsigned { get; set; }

        public ulong CompletedOffset { get; set; }

        public ulong SeedCount { get; set; }

        public int ResourceCount { get; set; }

        public bool Finished { get; set; }

        public SearchRecordsState? NormalPurityRecords { get; set; }

        public SearchRecordsState? RandomPurityRecords { get; set; }
    }

    private sealed class SearchRecordsState
    {
        public CandidateState? OverallMost { get; set; }

        public CandidateState? OverallLeast { get; set; }

        public CandidateState?[] ResourceMost { get; set; } = [];

        public CandidateState?[] ResourceLeast { get; set; } = [];
    }

    private sealed class CandidateState
    {
        public double RankScore { get; set; }

        public int Seed { get; set; }

        public PurityScenario Scenario { get; set; }

        public ScorePerspective Perspective { get; set; }

        public SearchResult? Result { get; set; }

        public static CandidateState? From(Candidate? candidate)
        {
            return candidate is null
                ? null
                : new CandidateState
                {
                    RankScore = candidate.RankScore,
                    Seed = candidate.Seed,
                    Scenario = candidate.Scenario,
                    Perspective = candidate.Perspective,
                    Result = candidate.Result,
                };
        }

        public Candidate? ToCandidate()
        {
            return new Candidate(RankScore, Seed, Scenario, Perspective, Result);
        }
    }

    private sealed class SearchBuffers
    {
        public SearchBuffers(int nodeCount, int resourceCount)
        {
            ActivePool = new FenwickTree(nodeCount);
            AssignedPoolIndices = new int[nodeCount];
            AssignedPurities = new ResourcePurity[nodeCount];
            Counts = new int[resourceCount];
            ResourceNodeIndices = new int[resourceCount, nodeCount];
            SumX = new double[resourceCount];
            SumY = new double[resourceCount];
            SumZ = new double[resourceCount];
            DistanceSum = new double[resourceCount];
            MostAverageDistanceToCenter = new double[resourceCount];
            LeastAverageDistanceToCenter = new double[resourceCount];
            MostClusterLevels = new double[resourceCount];
            LeastClusterLevels = new double[resourceCount];
            MostCenterX = new float[resourceCount];
            MostCenterY = new float[resourceCount];
            MostCenterZ = new float[resourceCount];
            LeastCenterX = new float[resourceCount];
            LeastCenterY = new float[resourceCount];
            LeastCenterZ = new float[resourceCount];
            ScoredNodeCounts = new int[resourceCount];
            NearestCandidateDistances = new double[nodeCount];
            FarthestCandidateDistances = new double[nodeCount];
            NearestCandidateIndices = new int[nodeCount];
            FarthestCandidateIndices = new int[nodeCount];
        }

        public FenwickTree ActivePool { get; }

        public int[] AssignedPoolIndices { get; }

        public ResourcePurity[] AssignedPurities { get; }

        public int[] Counts { get; }

        public int[,] ResourceNodeIndices { get; }

        public double[] SumX { get; }

        public double[] SumY { get; }

        public double[] SumZ { get; }

        public double[] DistanceSum { get; }

        public double[] MostAverageDistanceToCenter { get; }

        public double[] LeastAverageDistanceToCenter { get; }

        public double[] MostClusterLevels { get; }

        public double[] LeastClusterLevels { get; }

        public float[] MostCenterX { get; }

        public float[] MostCenterY { get; }

        public float[] MostCenterZ { get; }

        public float[] LeastCenterX { get; }

        public float[] LeastCenterY { get; }

        public float[] LeastCenterZ { get; }

        public float TempMostCenterX { get; set; }

        public float TempMostCenterY { get; set; }

        public float TempMostCenterZ { get; set; }

        public float TempLeastCenterX { get; set; }

        public float TempLeastCenterY { get; set; }

        public float TempLeastCenterZ { get; set; }

        public int[] ScoredNodeCounts { get; }

        public double[] NearestCandidateDistances { get; }

        public double[] FarthestCandidateDistances { get; }

        public int[] NearestCandidateIndices { get; }

        public int[] FarthestCandidateIndices { get; }

        public void Reset(int activeItemCount)
        {
            ActivePool.ResetAllActive(activeItemCount);
            Array.Clear(Counts);
            Array.Clear(SumX);
            Array.Clear(SumY);
            Array.Clear(SumZ);
            Array.Clear(DistanceSum);
            Array.Clear(MostAverageDistanceToCenter);
            Array.Clear(LeastAverageDistanceToCenter);
            Array.Clear(MostClusterLevels);
            Array.Clear(LeastClusterLevels);
            Array.Clear(MostCenterX);
            Array.Clear(MostCenterY);
            Array.Clear(MostCenterZ);
            Array.Clear(LeastCenterX);
            Array.Clear(LeastCenterY);
            Array.Clear(LeastCenterZ);
            Array.Clear(ScoredNodeCounts);
        }
    }

    private sealed class FenwickTree
    {
        private readonly int[] _tree;

        public FenwickTree(int itemCount)
        {
            _tree = new int[itemCount + 1];
        }

        public void ResetAllActive(int itemCount)
        {
            for (var i = 1; i <= itemCount; i++)
            {
                _tree[i] = i & -i;
            }
        }

        public void Add(int zeroBasedIndex, int delta)
        {
            for (var i = zeroBasedIndex + 1; i < _tree.Length; i += i & -i)
            {
                _tree[i] += delta;
            }
        }

        public int FindByOrder(int zeroBasedOrder)
        {
            var target = zeroBasedOrder + 1;
            var index = 0;
            var bitMask = HighestPowerOfTwoLessThan(_tree.Length);

            while (bitMask != 0)
            {
                var nextIndex = index + bitMask;
                if (nextIndex < _tree.Length && _tree[nextIndex] < target)
                {
                    index = nextIndex;
                    target -= _tree[nextIndex];
                }

                bitMask >>= 1;
            }

            return index;
        }

        private static int HighestPowerOfTwoLessThan(int value)
        {
            var power = 1;
            while ((power << 1) < value)
            {
                power <<= 1;
            }

            return power;
        }
    }
}
