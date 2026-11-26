// Every smoke puff from a fire, oven or kiln is a separate physics object that lives about 13 seconds, and the game
// keeps up to 100 of them around the player, often trapped under roofs. This spawns puffs less often and lowers the
// cap (the oldest puffs fade out first, as the game itself does at its own cap). Fires burn exactly as before.
// Ovens and cooking stations also have steam/smoke effects per food slot (cooking, done, burnt) that puff out of the
// oven's mouth; their emission rate is scaled by a separate setting.
// And the Ashlands burning ground: its flames and burn smoke (AshlandsFire.cs), and the dust cloud of placing a
// building piece (BuildSmoke.cs). And the bee swarms around beehives (Bees.cs).
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SmokeReducer
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.smokereducer";
        public const string Name = "Digi's Smoke Reducer";
        public const string Version = "1.2.1";

        private static ConfigEntry<float> _intervalMultiplier;
        private static ConfigEntry<int> _maxSmoke;
        private static ConfigEntry<int> _cookingSmoke;

        private sealed class Original { public float Interval; }
        private static readonly ConditionalWeakTable<SmokeSpawner, Original> Originals = new ConditionalWeakTable<SmokeSpawner, Original>();
        private sealed class OriginalRate { public float Rate; }
        private static readonly ConditionalWeakTable<ParticleSystem, OriginalRate> OriginalRates = new ConditionalWeakTable<ParticleSystem, OriginalRate>();
        private static readonly List<CookingStation> Stations = new List<CookingStation>();

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
            _intervalMultiplier = Config.Bind("Smoke", "Spawn interval multiplier", 2f,
                new ConfigDescription("Fires make smoke this many times less often: 2 = half the smoke, 1 = normal. Less smoke = better FPS.",
                    new AcceptableValueRange<float>(1f, 5f)));
            _maxSmoke = Config.Bind("Smoke", "Max smoke puffs", 50,
                new ConfigDescription("Most smoke puffs around you at once (normal 100); the oldest fade first. Lower = better FPS.",
                    new AcceptableValueRange<int>(10, 100)));
            _cookingSmoke = Config.Bind("Smoke", "Oven and cooking steam (%)", 50,
                new ConfigDescription("Steam and smoke from food in ovens and cooking stations (cooking, done and burnt food): 100 = normal, 0 = none.",
                    new AcceptableValueRange<int>(0, 100)));
            _intervalMultiplier.SettingChanged += (_, __) => ApplyAll();
            _cookingSmoke.SettingChanged += (_, __) => ApplyAllStations();

            AshlandsFire.Bind(Config);
            BuildSmoke.Bind(Config);
            Bees.Bind(Config);

            PatchModule(typeof(Plugin));
            PatchModule(typeof(AshlandsFire));
            PatchModule(typeof(BuildSmoke));
            PatchModule(typeof(Bees));
            AshlandsFire.ScanLoaded();
            BuildSmoke.ScanLoaded();
            Bees.ApplyAll();
            Logger.LogInfo($"Smoke puffs x1/{_intervalMultiplier.Value:0.#}, max {_maxSmoke.Value}, cooking steam {_cookingSmoke.Value}%.");
        }

        private void OnDestroy()
        {
            AshlandsFire.Restore();
            BuildSmoke.Restore();
            Bees.Restore();
        }

        [HarmonyPatch(typeof(SmokeSpawner), "Awake")]
        [HarmonyPostfix]
        private static void SpawnerAwake(SmokeSpawner __instance) => Apply(__instance);

        private static void Apply(SmokeSpawner spawner)
        {
            if (spawner == null) return;
            var original = Originals.GetValue(spawner, s => new Original { Interval = s.m_interval });
            spawner.m_interval = original.Interval * _intervalMultiplier.Value;
        }

        private static void ApplyAll()
        {
            foreach (var updater in SmokeSpawner.Instances) Apply(updater as SmokeSpawner);
        }

        // Before a puff is spawned: fade the oldest ones down to the cap (the game only does this above 100).
        [HarmonyPatch(typeof(SmokeSpawner), "Spawn")]
        [HarmonyPrefix]
        private static void BeforeSpawn()
        {
            var guard = 0;
            while (Smoke.GetTotalSmoke() >= _maxSmoke.Value && guard++ < 100) Smoke.FadeOldest();
        }

        // --- Ovens and cooking stations -----------------------------------------------------------------------

        [HarmonyPatch(typeof(CookingStation), "Awake")]
        [HarmonyPostfix]
        private static void StationAwake(CookingStation __instance)
        {
            Stations.RemoveAll(s => s == null);
            Stations.Add(__instance);
            ApplyStation(__instance);
        }

        private static void ApplyAllStations()
        {
            Stations.RemoveAll(s => s == null);
            foreach (var station in Stations) ApplyStation(station);
        }

        // Each food slot has its own particle effects: while cooking (on the slot itself), when done, and when burnt.
        private static void ApplyStation(CookingStation station)
        {
            if (station == null || station.m_slots == null) return;
            foreach (var slot in station.m_slots)
                if (slot != null) Scale(slot.GetComponent<ParticleSystem>());
            if (station.m_donePS != null) foreach (var ps in station.m_donePS) Scale(ps);
            if (station.m_burntPS != null) foreach (var ps in station.m_burntPS) Scale(ps);
        }

        private static void Scale(ParticleSystem ps)
        {
            if (ps == null) return;
            var emission = ps.emission;
            var original = OriginalRates.GetValue(ps, p => new OriginalRate { Rate = p.emission.rateOverTimeMultiplier });
            emission.rateOverTimeMultiplier = original.Rate * _cookingSmoke.Value / 100f;
        }
    }
}
