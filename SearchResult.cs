namespace SatisfactoryClusterCalculator;

public sealed record ResourceClusterScore(
    ResourceDescriptor Resource,
    int NodeCount,
    double AverageDistanceToCenter,
    double ClusterLevel,
    NodePosition Center);

public sealed record SearchResult(
    string ScenarioName,
    int Seed,
    double Score,
    double AverageClusterLevel,
    double WorstClusterLevel,
    IReadOnlyList<ResourceClusterScore> ResourceScores,
    IReadOnlyList<GeneratedResourceNode> Nodes)
{
    public static SearchResult Empty { get; } = new(
        "",
        0,
        double.NegativeInfinity,
        0.0,
        0.0,
        [],
        []);
}
