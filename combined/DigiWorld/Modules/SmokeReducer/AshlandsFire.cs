// Smoke Reducer, Ashlands fire: the spreading fire of the Ashlands ("Fire": burning ground, and "HouseFire" on burning
// buildings) puts out a lot of flame puffs and sparks, rises in big smoke clouds, and every time it burns something
// (about once a second per fire) its burn effect throws more smoke puffs and sparks.
// - Flames: scales how many flame particles and sparks each fire emits.
// - Smoke: scales the burn effect's puffs, and makes the smoke clouds the fire sends up fainter. Those clouds are
//   physical (fires choke on their own smoke and can go out), so they keep coming as before; only how much you see of
//   them changes (the game draws each one with a transparency it asks the cloud for).
// Visual only: the fire burns, spreads and hurts exactly as before.
// Lag: a fire's emitters are scaled once when it appears; the burn effect's template is scaled once, so every new
// puff is lighter at no cost; a fire's smoke cloud is marked once when it spawns, and its transparency is one lookup
// per frame. A slider change re-applies to the fires that are loaded.
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SmokeReducer
{
    internal static class AshlandsFire
    {
        private sealed class Original
        {
            public float Rate, RatePerMeter;
            public int Max;
            public float[] BurstChance;
        }

        private static ConfigEntry<int> _flames, _smoke;
        private static readonly ConditionalWeakTable<ParticleSystem, Original> Originals = new ConditionalWeakTable<ParticleSystem, Original>();
        private static readonly List<ParticleSystem> BurnTemplates = new List<ParticleSystem>();
        private static bool _burnFound;
        private static readonly object Mark = new object();
        private static readonly ConditionalWeakTable<SmokeSpawner, object> FireSpawners = new ConditionalWeakTable<SmokeSpawner, object>();
        private static readonly ConditionalWeakTable<Smoke, object> FireSmoke = new ConditionalWeakTable<Smoke, object>();
        private static bool _spawningFireSmoke;

        internal static void Bind(DigiCore.ModuleConfig config)
        {
            _flames = config.Bind("Ashlands fire", "Fire flames (%)", 50, new ConfigDescription(
                "The flame puffs and sparks of the spreading fire in the Ashlands (burning ground and burning buildings): " +
                "100 = normal, 50 = half, 0 = none. Visual only: the fire still burns and hurts, so keep some flames to " +
                "see where it is.", new AcceptableValueRange<int>(0, 100)));
            _smoke = config.Bind("Ashlands fire", "Fire smoke (%)", 50, new ConfigDescription(
                "The smoke of the spreading fire: the smoke clouds it sends up (made fainter; they still rise, since " +
                "fires choke on their own smoke) and the puffs each time it burns something: 100 = normal, 50 = half, " +
                "0 = none.", new AcceptableValueRange<int>(0, 100)));
            _flames.SettingChanged += OnChanged;
            _smoke.SettingChanged += OnChanged;
        }

        internal static void ScanLoaded()
        {
            foreach (var fire in Fire.s_fires) Loaded(fire);
        }

        internal static void Restore()
        {
            _flames.SettingChanged -= OnChanged;
            _smoke.SettingChanged -= OnChanged;
            foreach (var fire in Fire.s_fires)
                if (fire != null)
                    foreach (var system in fire.GetComponentsInChildren<ParticleSystem>(true)) Scale(system, 1f);
            foreach (var system in BurnTemplates)
                if (system != null) Scale(system, 1f);
            BurnTemplates.Clear();
            _burnFound = false;
        }

        [HarmonyPatch(typeof(Fire), "Awake")]
        [HarmonyPostfix]
        private static void Loaded(Fire __instance)
        {
            if (__instance == null) return;
            var flames = _flames.Value / 100f;
            foreach (var system in __instance.GetComponentsInChildren<ParticleSystem>(true)) Scale(system, flames);
            if (__instance.m_smokeSpawner != null && !FireSpawners.TryGetValue(__instance.m_smokeSpawner, out _))
                FireSpawners.Add(__instance.m_smokeSpawner, Mark);
            if (_burnFound || __instance.m_hitEffect?.m_effectPrefabs == null) return;
            _burnFound = true;
            foreach (var effect in __instance.m_hitEffect.m_effectPrefabs)
                if (effect?.m_prefab != null)
                    foreach (var system in effect.m_prefab.GetComponentsInChildren<ParticleSystem>(true))
                    {
                        BurnTemplates.Add(system);
                        Scale(system, _smoke.Value / 100f);
                    }
        }

        // --- The fire's smoke clouds: marked when a fire's smoke spawner makes them, drawn fainter ---------------------

        [HarmonyPatch(typeof(SmokeSpawner), "Spawn")]
        [HarmonyPrefix]
        private static void SpawnStart(SmokeSpawner __instance) => _spawningFireSmoke = FireSpawners.TryGetValue(__instance, out _);

        [HarmonyPatch(typeof(SmokeSpawner), "Spawn")]
        [HarmonyFinalizer]
        private static void SpawnEnd() => _spawningFireSmoke = false;

        [HarmonyPatch(typeof(Smoke), "Awake")]
        [HarmonyPostfix]
        private static void SmokeMade(Smoke __instance)
        {
            if (_spawningFireSmoke) FireSmoke.Add(__instance, Mark);
        }

        [HarmonyPatch(typeof(Smoke), nameof(Smoke.GetAlpha))]
        [HarmonyPostfix]
        private static void Fainter(Smoke __instance, ref float __result)
        {
            if (_smoke.Value < 100 && FireSmoke.TryGetValue(__instance, out _)) __result *= _smoke.Value / 100f;
        }

        private static void OnChanged(object sender, System.EventArgs e)
        {
            var flames = _flames.Value / 100f;
            foreach (var fire in Fire.s_fires)
                if (fire != null)
                    foreach (var system in fire.GetComponentsInChildren<ParticleSystem>(true)) Scale(system, flames);
            foreach (var system in BurnTemplates)
                if (system != null) Scale(system, _smoke.Value / 100f);
        }

        // Emission per second and per meter, burst chance and particle cap, from the system's own original values.
        private static void Scale(ParticleSystem system, float factor)
        {
            var emission = system.emission;
            var main = system.main;
            if (!Originals.TryGetValue(system, out var original))
            {
                original = new Original
                {
                    Rate = emission.rateOverTimeMultiplier, RatePerMeter = emission.rateOverDistanceMultiplier,
                    Max = main.maxParticles, BurstChance = new float[emission.burstCount],
                };
                for (var i = 0; i < original.BurstChance.Length; i++) original.BurstChance[i] = emission.GetBurst(i).probability;
                Originals.Add(system, original);
            }
            emission.rateOverTimeMultiplier = original.Rate * factor;
            emission.rateOverDistanceMultiplier = original.RatePerMeter * factor;
            main.maxParticles = Mathf.RoundToInt(original.Max * factor);
            for (var i = 0; i < original.BurstChance.Length && i < emission.burstCount; i++)
            {
                var burst = emission.GetBurst(i);
                burst.probability = original.BurstChance[i] * factor;
                emission.SetBurst(i, burst);
            }
        }
    }
}
