namespace SatisfactoryClusterCalculator;

public enum GameplayTag
{
    Basic,
    Advanced,
    FossilFuel,
}

public enum NodeRandomizationMode
{
    None,
    Strict,
    BasicRich,
    AdvancedRich,
    FossilFuelRich,
}

public enum NodePuritySettings
{
    NoChange,
    AllImpure,
    Decrease,
    AllNormal,
    Increase,
    AllPure,
    AllRandom,
}

public enum ResourceDescriptor
{
    OreIron,
    Coal,
    OreCopper,
    Stone,
    RawQuartz,
    LiquidOil,
    Water,
    SAM,
    NitrogenGas,
    OreBauxite,
    OreGold,
    Sulfur,
    OreUranium,
}

public enum ResourcePurity
{
    Impure = 1,
    Normal = 2,
    Pure = 4,
}

public readonly record struct NodePosition(float X, float Y, float Z);

public sealed record ResourceNodeDefinition(
    string Name,
    NodePosition Location,
    ResourceDescriptor Resource,
    ResourcePurity Purity);

public sealed record GeneratedResourceNode(
    string Name,
    NodePosition Location,
    ResourceDescriptor Resource,
    ResourcePurity Purity);

public static class ResourceDescriptorExtensions
{
    public static string GetInternalName(this ResourceDescriptor resource)
    {
        return resource switch
        {
            ResourceDescriptor.OreIron => "Desc_OreIron_C",
            ResourceDescriptor.Coal => "Desc_Coal_C",
            ResourceDescriptor.OreCopper => "Desc_OreCopper_C",
            ResourceDescriptor.Stone => "Desc_Stone_C",
            ResourceDescriptor.RawQuartz => "Desc_RawQuartz_C",
            ResourceDescriptor.LiquidOil => "Desc_LiquidOil_C",
            ResourceDescriptor.Water => "Desc_Water_C",
            ResourceDescriptor.SAM => "Desc_SAM_C",
            ResourceDescriptor.NitrogenGas => "Desc_NitrogenGas_C",
            ResourceDescriptor.OreBauxite => "Desc_OreBauxite_C",
            ResourceDescriptor.OreGold => "Desc_OreGold_C",
            ResourceDescriptor.Sulfur => "Desc_Sulfur_C",
            ResourceDescriptor.OreUranium => "Desc_OreUranium_C",
            _ => throw new ArgumentOutOfRangeException(nameof(resource), resource, null),
        };
    }

    public static string GetDisplayName(this ResourceDescriptor resource)
    {
        return resource switch
        {
            ResourceDescriptor.OreIron => "Iron",
            ResourceDescriptor.Coal => "Coal",
            ResourceDescriptor.OreCopper => "Copper",
            ResourceDescriptor.Stone => "Limestone",
            ResourceDescriptor.RawQuartz => "Quartz",
            ResourceDescriptor.LiquidOil => "Crude Oil",
            ResourceDescriptor.Water => "Water",
            ResourceDescriptor.SAM => "SAM",
            ResourceDescriptor.NitrogenGas => "Nitrogen Gas",
            ResourceDescriptor.OreBauxite => "Bauxite",
            ResourceDescriptor.OreGold => "Caterium",
            ResourceDescriptor.Sulfur => "Sulfur",
            ResourceDescriptor.OreUranium => "Uranium",
            _ => throw new ArgumentOutOfRangeException(nameof(resource), resource, null),
        };
    }

    public static bool HasTag(this ResourceDescriptor resource, GameplayTag tag)
    {
        return tag switch
        {
            GameplayTag.Basic => resource is ResourceDescriptor.OreIron
                or ResourceDescriptor.Coal
                or ResourceDescriptor.OreCopper
                or ResourceDescriptor.Stone,
            GameplayTag.Advanced => resource is ResourceDescriptor.RawQuartz
                or ResourceDescriptor.SAM
                or ResourceDescriptor.OreBauxite
                or ResourceDescriptor.OreGold
                or ResourceDescriptor.Sulfur
                or ResourceDescriptor.OreUranium,
            GameplayTag.FossilFuel => resource is ResourceDescriptor.Coal
                or ResourceDescriptor.LiquidOil
                or ResourceDescriptor.Sulfur,
            _ => false,
        };
    }
}
