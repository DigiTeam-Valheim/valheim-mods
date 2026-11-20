// Station Range: the build range of every crafting station (workbench, forge, stonecutter, artisan table, black forge,
// galdr table, ... including stations from other mods), each with its own slider in meters. The default is the game's
// own range, so nothing changes until you move one. The bonus a station's upgrades add (where the game gives one) is
// scaled the same way, so twice the range is twice the range at every level. The game also sizes the station's base
// area (the circle you see while building) from it. The list is filled once you are in a world.
// Changes apply within a moment to stations that are already built; switched off, every station gets its own range back.
// Light: a few numbers set when a station loads or a slider moves; nothing runs per frame.
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;

namespace StationRange
{
    public class Plugin : DigiCore.DigiModule
    {
        private const string Section = "Stations";

        private sealed class Station
        {
            public float Range, PerLevel;
            public ConfigEntry<float> Setting;
        }

        private static Plugin _instance;
        private static bool _on;
        // Station name token ($piece_workbench, ...) -> the game's own numbers and its slider.
        private static readonly Dictionary<string, Station> Stations = new Dictionary<string, Station>();

        private static readonly AccessTools.FieldRef<List<CraftingStation>> AllStations =
            AccessTools.StaticFieldRefAccess<List<CraftingStation>>(AccessTools.Field(typeof(CraftingStation), "m_allStations"));
        private static readonly AccessTools.FieldRef<CraftingStation, float> UpdateTimer =
            AccessTools.FieldRefAccess<CraftingStation, float>("m_updateExtensionTimer");

        private void Awake()
        {
            _instance = this;
            _on = true;
            PatchModule(typeof(Plugin));
            if (ZNetScene.instance != null) Learn(ZNetScene.instance); // switched on while playing
            ApplyAll();
        }

        private void OnDestroy()
        {
            _on = false;
            ApplyAll();
        }

        // Once the game's pieces are loaded: one slider per crafting station, its default the game's own range.
        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        [HarmonyPostfix]
        private static void SceneAwake(ZNetScene __instance) => Learn(__instance);

        private static void Learn(ZNetScene scene)
        {
            var added = false;
            foreach (var prefab in scene.m_prefabs)
            {
                var station = prefab != null ? prefab.GetComponent<CraftingStation>() : null;
                if (station == null || string.IsNullOrEmpty(station.m_name) || station.m_rangeBuild <= 0f || Stations.ContainsKey(station.m_name)) continue;
                var display = Localization.instance != null ? Localization.instance.Localize(station.m_name) : station.m_name;
                var normal = station.m_rangeBuild;
                var bonus = station.m_extraRangePerLevel > 0f ? $" Each upgrade adds {station.m_extraRangePerLevel:0.#} m (normally), scaled the same way." : "";
                var entry = _instance.Config.Bind(Section, display + " (m)", normal,
                    new ConfigDescription($"Build range of the {display}, in meters. Normal is {normal:0.#}.{bonus}",
                        new AcceptableValueRange<float>(1f, 200f)));
                entry.SettingChanged += (_, __) => ApplyAll();
                Stations[station.m_name] = new Station { Range = normal, PerLevel = station.m_extraRangePerLevel, Setting = entry };
                added = true;
            }
            if (added) SettingOrder.Apply(_instance.Config.File);
        }

        private static void ApplyAll()
        {
            var all = AllStations();
            if (all == null) return;
            foreach (var station in all) Apply(station);
        }

        private static void Apply(CraftingStation station)
        {
            if (station == null || !Stations.TryGetValue(station.m_name, out var normal)) return;
            var range = _on ? normal.Setting.Value : normal.Range;
            station.m_rangeBuild = range;
            station.m_extraRangePerLevel = normal.PerLevel * range / normal.Range;
            UpdateTimer(station) = 2f; // the game works the range (and its circle) out again on its next look
        }

        [HarmonyPatch(typeof(CraftingStation), "Start")]
        [HarmonyPostfix]
        private static void StationStart(CraftingStation __instance) => Apply(__instance);
    }
}
