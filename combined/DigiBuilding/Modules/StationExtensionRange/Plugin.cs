// Crafting station extensions (chopping block, tanning rack, forge bellows, ...) connect from further away, and can
// stand right next to each other. One distance for all stations, or a separate one per station (workbench, forge,
// cauldron, ... including stations from other mods; the list is filled once you are in a world).
// Changes apply immediately to extensions that are already built.
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace StationExtensionRange
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.stationextensionrange";
        public const string Name = "Digi's Station Extension Range";
        public const string Version = "1.0.1";
        private const float VanillaDistance = 5f;
        private const string PerStation = "Per station";

        private static Plugin _instance;
        private static ConfigEntry<bool> _enabled, _allowClose;
        private static ConfigEntry<float> _distance;
        // Station name token ($piece_forge, ...) -> its own distance (0 = use the general distance).
        private static readonly Dictionary<string, ConfigEntry<float>> StationDistance = new Dictionary<string, ConfigEntry<float>>();

        private static readonly AccessTools.FieldRef<List<StationExtension>> AllExtensions =
            AccessTools.StaticFieldRefAccess<List<StationExtension>>(AccessTools.Field(typeof(StationExtension), "m_allExtensions"));

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
            _instance = this;
            MigrateConfig();
            _enabled = Config.Bind("General", "Enabled", true, "Turn the mod on or off.");
            _distance = Config.Bind("General", "Connection distance (m)", 30f,
                new ConfigDescription("How far a station upgrade (chopping block, tanning rack, forge bellows, ...) can be from its crafting station, in meters. Normal is 5. Stations with their own distance under \"Per station\" use that instead.",
                    new AcceptableValueRange<float>(1f, 100f)));
            _allowClose = Config.Bind("General", "Allow extensions next to each other", true,
                "Upgrades of the same kind can be built close together (normally blocked).");
            _enabled.SettingChanged += (_, __) => ApplyAll();
            _distance.SettingChanged += (_, __) => ApplyAll();
            PatchModule(typeof(Plugin));
        }

        private static float DistanceFor(StationExtension extension)
        {
            if (!_enabled.Value) return VanillaDistance;
            var station = extension.m_craftingStation != null ? extension.m_craftingStation.m_name : null;
            if (station != null && StationDistance.TryGetValue(station, out var own) && own.Value > 0f) return own.Value;
            return _distance.Value;
        }

        private static void ApplyAll()
        {
            var all = AllExtensions();
            if (all == null) return;
            foreach (var extension in all)
                if (extension != null) extension.m_maxStationDistance = DistanceFor(extension);
        }

        // Once the game's pieces are loaded: one setting per crafting station that has extensions.
        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        [HarmonyPostfix]
        private static void SceneAwake(ZNetScene __instance)
        {
            var added = false;
            foreach (var prefab in __instance.m_prefabs)
            {
                var extension = prefab != null ? prefab.GetComponent<StationExtension>() : null;
                var station = extension != null ? extension.m_craftingStation : null;
                if (station == null || string.IsNullOrEmpty(station.m_name) || StationDistance.ContainsKey(station.m_name)) continue;
                var display = Localization.instance != null ? Localization.instance.Localize(station.m_name) : station.m_name;
                var entry = _instance.Config.Bind(PerStation, display + " (m)", 0f,
                    new ConfigDescription($"Own connection distance for {display} upgrades, in meters. 0 = use the general distance.",
                        new AcceptableValueRange<float>(0f, 100f)));
                entry.SettingChanged += (_, __) => ApplyAll();
                StationDistance[station.m_name] = entry;
                added = true;
            }
            if (added) SettingOrder.Apply(_instance.Config.File);
        }

        [HarmonyPatch(typeof(StationExtension), "Awake")]
        [HarmonyPrefix]
        private static void ExtensionAwake(StationExtension __instance) => __instance.m_maxStationDistance = DistanceFor(__instance);

        [HarmonyPatch(typeof(StationExtension), nameof(StationExtension.OtherExtensionInRange))]
        [HarmonyPostfix]
        private static void OtherExtensionInRange(ref bool __result)
        {
            if (_enabled.Value && _allowClose.Value) __result = false;
        }
    }
}
