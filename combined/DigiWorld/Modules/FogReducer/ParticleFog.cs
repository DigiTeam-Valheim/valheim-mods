// Fog Reducer, particle fog: haze made of particles, which the weather fog sliders do not reach.
// - Ashlands heat haze: every Ashlands weather has "vfx_Ashlands_HeatDistortion" around the player, hundreds of
//   particles that each bend what is behind them, so everything looks smeared and wobbly.
// - Ashlands weather mist: the Ashlands weathers (most of all "misty") have their own fog clouds, ground mist and
//   mist particles, on top of the weather fog. Only the Ashlands weathers' own effects are touched.
// - Morgen caves (Ashlands, location MorgenHole*): about six big "fx_morgenhole_mist" clouds per cave plus the rooms'
//   fog particles.
// - Dungeon haze: the big dust puffs ("InteriorDust", smoke) that follow the player inside dungeons. The tiny floating
//   dust specks next to it are left alone.
// Each slider scales how many particles are emitted (per second and per meter walked): fewer = thinner and less for
// the graphics card to draw; 0 = none. Set when a cave or environment loads and when a slider moves; nothing runs per
// frame. The game only switches these effects on and off, so the scaled rates stay.
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FogReducer
{
    internal static class ParticleFog
    {
        private sealed class Cloud
        {
            public ParticleSystem System;
            public float Rate, RatePerMeter;
            public int Max; // some emitters are held down by their particle cap, not their rate
            public float Applied = 1f;
        }

        private sealed class Kind
        {
            public ConfigEntry<int> Setting;
            public readonly List<Cloud> Clouds = new List<Cloud>();
        }

        private static readonly Kind HeatHaze = new Kind(), AshlandsMist = new Kind(), CaveMist = new Kind(), DungeonHaze = new Kind();
        private static readonly Kind[] Kinds = { HeatHaze, AshlandsMist, CaveMist, DungeonHaze };
        private static ParticleSystem.Particle[] _buffer;

        internal static void Bind(DigiCore.ModuleConfig config)
        {
            HeatHaze.Setting = config.Bind("Ashlands heat", "Heat haze in the air", 0, new ConfigDescription(
                "The shimmering heat haze in the air all around you in the Ashlands, which makes everything look smeared " +
                "and wobbly: 100 = normal, 0 = none. Less haze is also less for the graphics card to draw (every haze " +
                "particle draws what is behind it again).", new AcceptableValueRange<int>(0, 100)));
            AshlandsMist.Setting = config.Bind("Ashlands", "Mist particles", 50, new ConfigDescription(
                "The mist clouds of the Ashlands weather (thickest when it is misty), which come on top of the fog: " +
                "100 = normal, 50 = half, 0 = none. The fog itself is the Ashlands slider under Fog per biome.",
                new AcceptableValueRange<int>(0, 100)));
            CaveMist.Setting = config.Bind("Caves and dungeons", "Morgen cave mist", 50, new ConfigDescription(
                "The thick drifting mist in the Morgen caves of the Ashlands: 100 = normal, 50 = half, 0 = none. Fewer " +
                "mist clouds is also less for the graphics card to draw.", new AcceptableValueRange<int>(0, 100)));
            DungeonHaze.Setting = config.Bind("Caves and dungeons", "Dungeon haze", 50, new ConfigDescription(
                "The cloudy dust haze around you inside dungeons and caves (also in the Morgen caves): 100 = normal, " +
                "50 = half, 0 = none. The tiny floating dust specks stay.", new AcceptableValueRange<int>(0, 100)));
            foreach (var kind in Kinds) kind.Setting.SettingChanged += OnChanged;
        }

        // For what was already loaded when the sub-mod was switched on.
        internal static void ScanLoaded()
        {
            if (EnvMan.instance != null)
                foreach (var env in EnvMan.instance.m_environments) ScanEnvironment(env);
            foreach (var location in UnityEngine.Object.FindObjectsByType<Location>(FindObjectsSortMode.None)) AddCave(location);
        }

        internal static void Restore()
        {
            foreach (var kind in Kinds)
            {
                if (kind.Setting != null) kind.Setting.SettingChanged -= OnChanged;
                foreach (var cloud in kind.Clouds)
                {
                    if (cloud.System == null) continue;
                    var emission = cloud.System.emission;
                    emission.rateOverTimeMultiplier = cloud.Rate;
                    emission.rateOverDistanceMultiplier = cloud.RatePerMeter;
                    var main = cloud.System.main;
                    main.maxParticles = cloud.Max;
                }
                kind.Clouds.Clear();
            }
        }

        // Location.Awake also creates the cave's interior, so all its mist exists right after it.
        [HarmonyPatch(typeof(Location), "Awake")]
        [HarmonyPostfix]
        private static void LocationLoaded(Location __instance) => AddCave(__instance);

        // EnvMan holds every environment's effects; the Ashlands weathers are appended later by their location list.
        [HarmonyPatch(typeof(EnvMan), "Awake")]
        [HarmonyPostfix]
        private static void EnvLoaded(EnvMan __instance)
        {
            foreach (var env in __instance.m_environments) ScanEnvironment(env);
        }

        [HarmonyPatch(typeof(EnvMan), nameof(EnvMan.AppendEnvironment))]
        [HarmonyPostfix]
        private static void EnvAppended(EnvSetup env) => ScanEnvironment(env);

        private static void AddCave(Location location)
        {
            if (location == null || !location.name.StartsWith("MorgenHole", StringComparison.OrdinalIgnoreCase)) return;
            foreach (var system in location.GetComponentsInChildren<ParticleSystem>(true))
                if (system.name.IndexOf("morgenhole_mist", StringComparison.OrdinalIgnoreCase) >= 0 || MaterialIs(system, "grave_fog"))
                    Add(CaveMist, system);
        }

        private static void ScanEnvironment(EnvSetup env)
        {
            if (env?.m_psystems == null) return;
            foreach (var effect in env.m_psystems)
            {
                if (effect == null) continue;
                var interior = effect.name.StartsWith("InteriorDust", StringComparison.OrdinalIgnoreCase);
                var ashlands = effect.name.StartsWith("Ashlands", StringComparison.OrdinalIgnoreCase);
                foreach (var system in effect.GetComponentsInChildren<ParticleSystem>(true))
                {
                    if (MaterialIs(system, "heathaze_distortion")) Add(HeatHaze, system);
                    else if (interior && MaterialIs(system, "smoke")) Add(DungeonHaze, system);
                    else if (ashlands && IsMist(system)) Add(AshlandsMist, Emitter(system));
                }
            }
        }

        private static bool IsMist(ParticleSystem system)
        {
            var renderer = system.GetComponent<ParticleSystemRenderer>();
            var name = renderer != null && renderer.sharedMaterial != null ? renderer.sharedMaterial.name : "";
            return name.IndexOf("fog", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("mist", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Some clouds have no emission of their own: their parent system spawns them (sub-emitter). Scale that one.
        private static ParticleSystem Emitter(ParticleSystem system)
        {
            var emission = system.emission;
            if (emission.rateOverTimeMultiplier > 0f || emission.rateOverDistanceMultiplier > 0f || system.transform.parent == null) return system;
            var parent = system.transform.parent.GetComponent<ParticleSystem>();
            return parent != null ? parent : system;
        }

        private static bool MaterialIs(ParticleSystem system, string name)
        {
            var renderer = system.GetComponent<ParticleSystemRenderer>();
            return renderer != null && renderer.sharedMaterial != null &&
                   renderer.sharedMaterial.name.StartsWith(name, StringComparison.OrdinalIgnoreCase);
        }

        private static void Add(Kind kind, ParticleSystem system)
        {
            kind.Clouds.RemoveAll(c => c.System == null);
            if (kind.Clouds.Exists(c => c.System == system)) return;
            var emission = system.emission;
            var cloud = new Cloud
            {
                System = system, Rate = emission.rateOverTimeMultiplier, RatePerMeter = emission.rateOverDistanceMultiplier,
                Max = system.main.maxParticles,
            };
            kind.Clouds.Add(cloud);
            Apply(cloud, kind.Setting.Value);
        }

        private static void OnChanged(object sender, EventArgs e)
        {
            foreach (var kind in Kinds)
            {
                kind.Clouds.RemoveAll(c => c.System == null);
                foreach (var cloud in kind.Clouds) Apply(cloud, kind.Setting.Value);
            }
        }

        // Scales the emission; when lowered, also removes the extra particles right away instead of waiting for them
        // to fade out.
        private static void Apply(Cloud cloud, int value)
        {
            var factor = value / 100f;
            var system = cloud.System;
            var emission = system.emission;
            emission.rateOverTimeMultiplier = cloud.Rate * factor;
            emission.rateOverDistanceMultiplier = cloud.RatePerMeter * factor;
            var main = system.main;
            main.maxParticles = Mathf.RoundToInt(cloud.Max * factor);
            var count = system.particleCount;
            if (factor < cloud.Applied && count > 0)
            {
                var keep = Mathf.RoundToInt(count * factor / cloud.Applied);
                if (_buffer == null || _buffer.Length < count) _buffer = new ParticleSystem.Particle[Mathf.Max(count, 256)];
                system.GetParticles(_buffer, count);
                system.SetParticles(_buffer, keep);
            }
            cloud.Applied = factor;
        }
    }
}
