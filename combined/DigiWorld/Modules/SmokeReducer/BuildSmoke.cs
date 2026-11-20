// Smoke Reducer, building smoke: placing (or repairing) a building piece throws a big cloud of dust and smoke puffs
// (the piece's place effect), which hides what you build, most of all with a build camera up close.
// "Building smoke (%)" scales how many puffs each of those effects throws (and their cap): at 25 % every placement
// still shows a small puff, a quarter of the cloud. Visual only. The effects' templates are scaled once (every piece of
// one material shares one), so it covers everyone's building, at no cost per placement.
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SmokeReducer
{
    internal static class BuildSmoke
    {
        private sealed class Original
        {
            public float Rate, RatePerMeter;
            public int Max;
            public ParticleSystem.MinMaxCurve[] Bursts;
        }

        private static ConfigEntry<int> _percent;
        private static readonly ConditionalWeakTable<ParticleSystem, Original> Originals = new ConditionalWeakTable<ParticleSystem, Original>();
        private static readonly List<ParticleSystem> Templates = new List<ParticleSystem>();
        private static readonly HashSet<GameObject> Seen = new HashSet<GameObject>();

        internal static void Bind(DigiCore.ModuleConfig config)
        {
            _percent = config.Bind("Smoke", "Building smoke (%)", 25, new ConfigDescription(
                "The cloud of dust and smoke when a building piece is placed or repaired: 100 = normal, 25 = a quarter, " +
                "0 = none.", new AcceptableValueRange<int>(0, 100)));
            _percent.SettingChanged += OnChanged;
        }

        internal static void ScanLoaded()
        {
            if (ZNetScene.instance != null) Collect(ZNetScene.instance);
        }

        internal static void Restore()
        {
            _percent.SettingChanged -= OnChanged;
            foreach (var system in Templates)
                if (system != null) Scale(system, 1f);
            Templates.Clear();
            Seen.Clear();
        }

        // Every piece the game knows; again when the player spawns, for pieces other mods register later.
        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        [HarmonyPostfix]
        private static void SceneLoaded(ZNetScene __instance) => Collect(__instance);

        [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        [HarmonyPostfix]
        private static void Spawned(Player __instance)
        {
            if (__instance == Player.m_localPlayer && ZNetScene.instance != null) Collect(ZNetScene.instance);
        }

        private static void Collect(ZNetScene scene)
        {
            if (scene.m_prefabs == null) return;
            var factor = _percent.Value / 100f;
            foreach (var prefab in scene.m_prefabs)
            {
                var piece = prefab != null ? prefab.GetComponent<Piece>() : null;
                if (piece == null || piece.m_placeEffect?.m_effectPrefabs == null) continue;
                foreach (var effect in piece.m_placeEffect.m_effectPrefabs)
                {
                    if (effect?.m_prefab == null || !Seen.Add(effect.m_prefab)) continue;
                    foreach (var system in effect.m_prefab.GetComponentsInChildren<ParticleSystem>(true))
                    {
                        Templates.Add(system);
                        Scale(system, factor);
                    }
                }
            }
        }

        private static void OnChanged(object sender, System.EventArgs e)
        {
            var factor = _percent.Value / 100f;
            foreach (var system in Templates)
                if (system != null) Scale(system, factor);
        }

        // Puffs per burst, per second and per meter, and the cap, from the system's own original values. A burst keeps at
        // least one puff while the setting is above 0.
        private static void Scale(ParticleSystem system, float factor)
        {
            var emission = system.emission;
            var main = system.main;
            if (!Originals.TryGetValue(system, out var original))
            {
                original = new Original
                {
                    Rate = emission.rateOverTimeMultiplier, RatePerMeter = emission.rateOverDistanceMultiplier,
                    Max = main.maxParticles, Bursts = new ParticleSystem.MinMaxCurve[emission.burstCount],
                };
                for (var i = 0; i < original.Bursts.Length; i++) original.Bursts[i] = emission.GetBurst(i).count;
                Originals.Add(system, original);
            }
            emission.rateOverTimeMultiplier = original.Rate * factor;
            emission.rateOverDistanceMultiplier = original.RatePerMeter * factor;
            main.maxParticles = factor <= 0f ? 0 : Mathf.Max(1, Mathf.RoundToInt(original.Max * factor));
            for (var i = 0; i < original.Bursts.Length && i < emission.burstCount; i++)
            {
                var burst = emission.GetBurst(i);
                burst.count = Scaled(original.Bursts[i], factor);
                emission.SetBurst(i, burst);
            }
        }

        private static ParticleSystem.MinMaxCurve Scaled(ParticleSystem.MinMaxCurve count, float factor)
        {
            float Fewer(float n) => factor <= 0f || n <= 0f ? 0f : Mathf.Max(1f, Mathf.Round(n * factor));
            switch (count.mode)
            {
                case ParticleSystemCurveMode.Constant:
                    return new ParticleSystem.MinMaxCurve(Fewer(count.constant));
                case ParticleSystemCurveMode.TwoConstants:
                    return new ParticleSystem.MinMaxCurve(Fewer(count.constantMin), Fewer(count.constantMax));
                default:
                    var curve = count;
                    curve.curveMultiplier = count.curveMultiplier * factor;
                    return curve;
            }
        }
    }
}
