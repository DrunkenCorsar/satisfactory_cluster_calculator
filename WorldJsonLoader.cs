using System.Text.Json;

namespace SatisfactoryClusterCalculator;

public static class WorldJsonLoader
{
    public static IReadOnlyList<ResourceNodeDefinition> LoadResourceNodes(string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);

        if (!document.RootElement.TryGetProperty("resourceNodes", out var resourceNodesElement))
        {
            throw new InvalidDataException("World JSON must contain a resourceNodes array.");
        }

        var resourceNodes = new List<ResourceNodeDefinition>();
        foreach (var nodeElement in resourceNodesElement.EnumerateArray())
        {
            var name = nodeElement.GetProperty("name").GetString()
                ?? throw new InvalidDataException("Resource node is missing name.");
            var locationElement = nodeElement.GetProperty("location");
            var location = new NodePosition(
                locationElement[0].GetSingle(),
                locationElement[1].GetSingle(),
                locationElement[2].GetSingle());
            var resource = ParseResource(nodeElement.GetProperty("resource").GetString());
            var purity = ParsePurity(nodeElement.GetProperty("purity").GetString());

            resourceNodes.Add(new ResourceNodeDefinition(name, location, resource, purity));
        }

        return resourceNodes;
    }

    private static ResourceDescriptor ParseResource(string? resource)
    {
        return resource switch
        {
            "Desc_OreIron_C" => ResourceDescriptor.OreIron,
            "Desc_Coal_C" => ResourceDescriptor.Coal,
            "Desc_OreCopper_C" => ResourceDescriptor.OreCopper,
            "Desc_Stone_C" => ResourceDescriptor.Stone,
            "Desc_RawQuartz_C" => ResourceDescriptor.RawQuartz,
            "Desc_LiquidOil_C" => ResourceDescriptor.LiquidOil,
            "Desc_Water_C" => ResourceDescriptor.Water,
            "Desc_SAM_C" => ResourceDescriptor.SAM,
            "Desc_NitrogenGas_C" => ResourceDescriptor.NitrogenGas,
            "Desc_OreBauxite_C" => ResourceDescriptor.OreBauxite,
            "Desc_OreGold_C" => ResourceDescriptor.OreGold,
            "Desc_Sulfur_C" => ResourceDescriptor.Sulfur,
            "Desc_OreUranium_C" => ResourceDescriptor.OreUranium,
            _ => throw new InvalidDataException($"Unknown resource descriptor '{resource}'."),
        };
    }

    private static ResourcePurity ParsePurity(string? purity)
    {
        return purity switch
        {
            "RP_Inpure" => ResourcePurity.Impure,
            "RP_Impure" => ResourcePurity.Impure,
            "RP_Normal" => ResourcePurity.Normal,
            "RP_Pure" => ResourcePurity.Pure,
            _ => throw new InvalidDataException($"Unknown resource purity '{purity}'."),
        };
    }
}
