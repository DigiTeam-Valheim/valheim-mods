// Fog per biome: a slider (0-100%) for each biome that scales the weather fog while you are in that biome, and one for
// the Mistlands' mist particles. 100% = vanilla, 0% = none, default 50%. Visual only and client side: everyone can pick
// their own. The change fades in over a few seconds when you cross into another biome, so the fog does not pop.
// Also the Ashlands heat screen effect (the wobbly blur when you heat up): a slider for the blur and one for the whole
// effect; at 0 the effect is switched off, which also saves a full-screen pass on the graphics card.
// And haze made of particles (ParticleFog.cs): the Ashlands heat haze in the air, Morgen cave mist, dungeon haze.
// And a brightness slider per biome (Brightness.cs), e.g. for the dark Ashlands.
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FogReducer
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.fogreducer";
        public const string Name = "Digi's Fog Reducer";
        public const string Version = "1.0.2";

        private static readonly Dictionary<Heightmap.Biome, ConfigEntry<int>> Fog = new Dictionary<Heightmap.Biome, ConfigEntry<int>>();
        private static ConfigEntry<int> _otherFog;
        private static ConfigEntry<int> _mistParticles;
        private static ConfigEntry<int> _heatBlur;
        private static ConfigEntry<int> _heatEffect;
        private static float _current = -1f; // smoothed fog factor

        // The plugin ID changed from pieter.valheim.* to digi.valheim.*: carry the old settings file over once.
        private void MigrateConfig()
        {
            var old = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, Guid.Replace("digi.valheim.", "pieter.valheim.") + ".cfg");
            if (System.IO.File.Exists(Config.ConfigFilePath) || !System.IO.File.Exists(old)) return;
            System.IO.File.Copy(old, Config.ConfigFilePath);
            Config.Reload();
        }


        private void Awake()
        {
            MigrateConfig();
            var order = 100;
            foreach (var biome in BiomeNames.All)
                Fog[biome] = Config.Bind("Fog per biome", BiomeNames.Display(biome), 50,
                    new ConfigDescription($"Fog in the {BiomeNames.Display(biome)}: 100 = normal, 50 = half, 0 = no fog.",
                        new AcceptableValueRange<int>(0, 100), new ConfigurationManagerAttributes { Order = order-- }));
            _otherFog = Config.Bind("Fog per biome", "Other / unknown", 50,
                new ConfigDescription("Fog in biomes added by other mods: 100 = normal, 50 = half, 0 = no fog.",
                    new AcceptableValueRange<int>(0, 100), new ConfigurationManagerAttributes { Order = order-- }));
            _mistParticles = Config.Bind("Mistlands", "Mist particles", 50,
                new ConfigDescription("The thick Mistlands mist you walk through (not the same as fog): 100 = normal, 0 = none.",
                    new AcceptableValueRange<int>(0, 100)));
            _heatBlur = Config.Bind("Ashlands heat", "Heat blur", 0,
                new ConfigDescription("The wobbly, blurred screen when you heat up in the Ashlands (running in the sun, near "
                    + "lava, in the hot sea): 100 = normal, 0 = no blur. The heat itself and its damage are not changed.",
                    new AcceptableValueRange<int>(0, 100), new ConfigurationManagerAttributes { Order = 2 }));
            _heatEffect = Config.Bind("Ashlands heat", "Heat screen effect", 100,
                new ConfigDescription("The whole heat screen effect (the blur and the hot screen edges): 100 = normal, "
                    + "0 = off. Off also saves one full-screen pass on the graphics card. You still hear the sizzling "
                    + "and see the burning particles around you when you heat up.",
                    new AcceptableValueRange<int>(0, 100), new ConfigurationManagerAttributes { Order = 1 }));
            ParticleFog.Bind(Config);
            Brightness.Bind(Config);
            PatchModule(typeof(Plugin));
            PatchModule(typeof(ParticleFog));
            PatchModule(typeof(Brightness));
            ParticleFog.ScanLoaded();
        }

        private void OnDestroy()
        {
            ParticleFog.Restore();
            Brightness.Reset();
            if (_heat != null) DistortionStrength(_heat) = _baseDistortion;
            _heat = null;
        }

        private static float Target()
        {
            var biome = EnvMan.instance != null ? EnvMan.instance.GetCurrentBiome() : Heightmap.Biome.None;
            return (Fog.TryGetValue(biome, out var entry) ? entry.Value : _otherFog.Value) / 100f;
        }

        // SetEnv rebuilds RenderSettings.fogDensity from the weather every frame; scale the result.
        [HarmonyPatch(typeof(EnvMan), "SetEnv")]
        [HarmonyPostfix]
        private static void SetEnv(float dt)
        {
            var target = Target();
            _current = _current < 0f ? target : Mathf.MoveTowards(_current, target, Mathf.Max(dt, 0f) * 0.25f);
            RenderSettings.fogDensity *= _current;
        }

        // --- Mistlands mist particles: scale how many are emitted -----------------------------------------------------

        private static ParticleMist _mist;
        private static int _localEmission, _localEmissionPerUnit;
        private static float _emissionMax, _emissionPerUnit, _distantEmissionMax;

        [HarmonyPatch(typeof(ParticleMist), "Update")]
        [HarmonyPrefix]
        private static void MistUpdate(ParticleMist __instance)
        {
            if (_mist != __instance)
            {
                _mist = __instance;
                _localEmission = __instance.m_localEmission;
                _localEmissionPerUnit = __instance.m_localEmissionPerUnit;
                _emissionMax = __instance.m_emissionMax;
                _emissionPerUnit = __instance.m_emissionPerUnit;
                _distantEmissionMax = __instance.m_distantEmissionMax;
            }
            var f = _mistParticles.Value / 100f;
            __instance.m_localEmission = Mathf.RoundToInt(_localEmission * f);
            // Also used as a divisor, so it stays at least 1 (then walking adds almost nothing).
            __instance.m_localEmissionPerUnit = Mathf.Max(1, Mathf.RoundToInt(_localEmissionPerUnit * f));
            __instance.m_emissionMax = _emissionMax * f;
            __instance.m_emissionPerUnit = _emissionPerUnit * f;
            __instance.m_distantEmissionMax = _distantEmissionMax * f;
        }

        // --- Ashlands heat: the full-screen heat effect ---------------------------------------------------------------

        private static readonly AccessTools.FieldRef<HeatDistortImageEffect, float> DistortionStrength =
            AccessTools.FieldRefAccess<HeatDistortImageEffect, float>("m_distortionStrength");
        private static HeatDistortImageEffect _heat;
        private static float _baseDistortion;

        // The game sets the effect from the local player's heat right here, every frame (for every creature, so the
        // check for the local player comes first).
        [HarmonyPatch(typeof(Character), "UpdateHeatEffects")]
        [HarmonyPostfix]
        private static void HeatEffects(Character __instance)
        {
            if (__instance != Player.m_localPlayer || GameCamera.instance == null) return;
            var fx = GameCamera.instance.m_heatDistortImageEffect;
            if (fx == null || !fx.enabled) return;
            if (_heat != fx)
            {
                _heat = fx;
                _baseDistortion = DistortionStrength(fx);
            }
            DistortionStrength(fx) = _baseDistortion * _heatBlur.Value / 100f;
            fx.m_intensity *= _heatEffect.Value / 100f;
            if (fx.m_intensity <= 0f) fx.enabled = false;
        }
    }
}
