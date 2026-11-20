// Fog Reducer, brightness: a slider per biome that scales the light while you are in that biome (the ambient light
// that fills the shadows, and the sun or moon), e.g. to see around you in the dark Ashlands. 100% = vanilla. The change
// fades in over a few seconds when you cross into another biome, like the fog. Two multiplications per frame, right
// after the game set the light.
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FogReducer
{
    internal static class Brightness
    {
        private const string Section = "Brightness per biome";
        private static readonly Dictionary<Heightmap.Biome, ConfigEntry<int>> Levels = new Dictionary<Heightmap.Biome, ConfigEntry<int>>();
        private static ConfigEntry<int> _other;
        private static float _current = -1f; // smoothed light factor

        internal static void Bind(DigiCore.ModuleConfig config)
        {
            var order = 100;
            foreach (var biome in BiomeNames.All)
                Levels[biome] = config.Bind(Section, BiomeNames.Display(biome), 100, new ConfigDescription(
                    $"How bright it is in the {BiomeNames.Display(biome)} (the light in the shadows and the sun or moon): " +
                    "100 = normal, 150 = half again as bright, 200 = twice as bright.",
                    new AcceptableValueRange<int>(50, 300), new ConfigurationManagerAttributes { Order = order-- }));
            _other = config.Bind(Section, "Other / unknown", 100, new ConfigDescription(
                "How bright it is in biomes added by other mods: 100 = normal, 200 = twice as bright.",
                new AcceptableValueRange<int>(50, 300), new ConfigurationManagerAttributes { Order = order-- }));
        }

        internal static void Reset() => _current = -1f;

        // SetEnv sets the ambient light and the sun or moon from the weather every frame; scale the result.
        [HarmonyPatch(typeof(EnvMan), "SetEnv")]
        [HarmonyPostfix]
        private static void SetEnv(EnvMan __instance, float dt)
        {
            var target = (Levels.TryGetValue(__instance.GetCurrentBiome(), out var entry) ? entry.Value : _other.Value) / 100f;
            _current = _current < 0f ? target : Mathf.MoveTowards(_current, target, Mathf.Max(dt, 0f) * 0.25f);
            if (_current > 0.999f && _current < 1.001f) return;
            RenderSettings.ambientLight *= _current;
            if (__instance.m_dirLight != null) __instance.m_dirLight.intensity *= _current;
        }
    }
}
