// Fog Reducer: the biomes that get a slider, with their names as players know them (shared by fog and brightness).
namespace FogReducer
{
    internal static class BiomeNames
    {
        internal static readonly Heightmap.Biome[] All =
        {
            Heightmap.Biome.Meadows, Heightmap.Biome.BlackForest, Heightmap.Biome.Swamp, Heightmap.Biome.Mountain,
            Heightmap.Biome.Plains, Heightmap.Biome.Ocean, Heightmap.Biome.Mistlands, Heightmap.Biome.AshLands,
            Heightmap.Biome.DeepNorth,
        };

        internal static string Display(Heightmap.Biome biome) =>
            biome == Heightmap.Biome.BlackForest ? "Black Forest"
            : biome == Heightmap.Biome.AshLands ? "Ashlands"
            : biome == Heightmap.Biome.DeepNorth ? "Deep North"
            : biome.ToString();
    }

    // Read by Configuration Manager to order the entries; it only needs the field name.
    internal sealed class ConfigurationManagerAttributes
    {
        public int? Order;
    }
}
