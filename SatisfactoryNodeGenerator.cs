namespace SatisfactoryClusterCalculator;

public static class SatisfactoryNodeGenerator
{
    public static IReadOnlyList<GeneratedResourceNode> Generate(
        int seed,
        IEnumerable<ResourceNodeDefinition> sourceNodes,
        NodeRandomizationMode randomizationMode = NodeRandomizationMode.Strict,
        NodePuritySettings puritySettings = NodePuritySettings.NoChange)
    {
        var rng = new RandomStream(seed);
        var targetNodes = sourceNodes
            .OrderBy(node => node.Name, StringComparer.Ordinal)
            .ToList();

        if (targetNodes.Count == 0)
        {
            return [];
        }

        if (randomizationMode == NodeRandomizationMode.None)
        {
            return targetNodes
                .Select(node => ToGeneratedNode(node, GetPurityOverride(rng, node.Purity, puritySettings) ?? node.Purity))
                .ToList();
        }

        var nodePool = targetNodes
            .Select(ResourceNodeInfo.From)
            .Order()
            .ToList();

        switch (randomizationMode)
        {
            case NodeRandomizationMode.BasicRich:
                ModifyNodeDistribution(rng, nodePool, GameplayTag.Basic, 1.1f);
                break;
            case NodeRandomizationMode.AdvancedRich:
                ModifyNodeDistribution(rng, nodePool, GameplayTag.Advanced, 3.0f);
                break;
            case NodeRandomizationMode.FossilFuelRich:
                ModifyNodeDistribution(rng, nodePool, GameplayTag.FossilFuel, 2.0f);
                break;
        }

        var generatedNodes = new List<GeneratedResourceNode>(targetNodes.Count);
        foreach (var targetNode in targetNodes)
        {
            var poolIndex = (int)rng.FRandRange(0.0f, nodePool.Count);
            var nodeInfo = nodePool[poolIndex];
            nodePool.RemoveAt(poolIndex);

            var purity = GetPurityOverride(rng, nodeInfo.Purity, puritySettings) ?? nodeInfo.Purity;
            generatedNodes.Add(new GeneratedResourceNode(
                targetNode.Name,
                targetNode.Location,
                nodeInfo.Resource,
                purity));
        }

        return generatedNodes;
    }

    private static GeneratedResourceNode ToGeneratedNode(ResourceNodeDefinition node, ResourcePurity purity)
    {
        return new GeneratedResourceNode(node.Name, node.Location, node.Resource, purity);
    }

    private static ResourcePurity? GetPurityOverride(
        RandomStream rng,
        ResourcePurity purity,
        NodePuritySettings puritySettings)
    {
        return puritySettings switch
        {
            NodePuritySettings.NoChange => null,
            NodePuritySettings.AllPure => ResourcePurity.Pure,
            NodePuritySettings.AllNormal => ResourcePurity.Normal,
            NodePuritySettings.AllImpure => ResourcePurity.Impure,
            NodePuritySettings.AllRandom => (int)rng.FRandRange(0.0f, 3.0f) switch
            {
                0 => ResourcePurity.Impure,
                1 => ResourcePurity.Normal,
                2 => ResourcePurity.Pure,
                _ => null,
            },
            NodePuritySettings.Increase => purity switch
            {
                ResourcePurity.Impure => ResourcePurity.Normal,
                ResourcePurity.Normal or ResourcePurity.Pure => ResourcePurity.Pure,
                _ => null,
            },
            NodePuritySettings.Decrease => purity switch
            {
                ResourcePurity.Impure or ResourcePurity.Normal => ResourcePurity.Impure,
                ResourcePurity.Pure => ResourcePurity.Normal,
                _ => null,
            },
            _ => null,
        };
    }

    private static void ModifyNodeDistribution(
        RandomStream rng,
        List<ResourceNodeInfo> nodePool,
        GameplayTag tag,
        float multiplier)
    {
        if (multiplier < 1.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(multiplier), "Cannot decrease node count.");
        }

        var matchingNodeCount = nodePool.Count(node => node.Resource.HasTag(tag));
        var modifiedNodeCount = (int)MathF.Round(matchingNodeCount * multiplier);
        var resourceOptions = Enum.GetValues<ResourceDescriptor>()
            .Where(resource => resource.HasTag(tag))
            .OrderBy(resource => resource.GetInternalName(), StringComparer.Ordinal)
            .ToList();

        Shuffle(rng, nodePool);

        var seenResources = new HashSet<ResourceDescriptor>();
        foreach (var node in nodePool)
        {
            if (matchingNodeCount >= modifiedNodeCount)
            {
                break;
            }

            if (node.Resource.HasTag(tag))
            {
                continue;
            }

            if (seenResources.Add(node.Resource))
            {
                continue;
            }

            var newResource = resourceOptions[(int)rng.FRandRange(0.0f, resourceOptions.Count)];
            node.Resource = newResource;
            matchingNodeCount++;
        }
    }

    private static void Shuffle<T>(RandomStream rng, IList<T> values)
    {
        for (var i = 0; i < values.Count - 1; i++)
        {
            var swapIndex = i + (int)rng.FRandRange(0.0f, values.Count - i);
            (values[i], values[swapIndex]) = (values[swapIndex], values[i]);
        }
    }

    private sealed class ResourceNodeInfo : IComparable<ResourceNodeInfo>
    {
        private ResourceNodeInfo(ResourceDescriptor resource, ResourcePurity purity)
        {
            Resource = resource;
            Purity = purity;
        }

        public ResourceDescriptor Resource { get; set; }

        public ResourcePurity Purity { get; }

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
            if (resourceComparison != 0)
            {
                return resourceComparison;
            }

            return Purity.CompareTo(other.Purity);
        }
    }
}
