// Terrain Tools (until 2026-11 "Level Strength"): the hoe and cultivator as they should be.
// - Level ground can flatten the whole circle ("Level strength"), and the ground can be raised or dug further than the
//   game's 8 m ("Max height change"; the game's 8 m limit is lifted where it levels, raises and draws the ground).
// - Size: hold Left Shift and turn the mouse wheel; every tool keeps its own size.
// - Square mode (G): every hoe and cultivator tool works on a square lined up with the ground's 1 m grid.
// - A steady outline on the ground shows exactly what the tool changes, instead of the game's pulsing circle.
// - New hoe pieces: Smooth, Slope, Raise and Lower (small steps; Left Ctrl + mouse wheel sets the step) and Reset.
// The game itself only sends which piece was used when it changes the ground (whoever owns that ground uses their own
// copy of the piece), so size, shape and the new tools travel with each click as extra data; in multiplayer everyone
// needs this mod, and the host's settings count for everyone.
// With TerraHoe or Advanced Terrain Modifiers installed (they do the same jobs), only Level strength and the height
// limit run.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Utils;
using UnityEngine;

namespace LevelStrength
{
    [SynchronizationMode(AdminOnlyStrictness.IfOnServer)]
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.levelstrength";
        public const string Name = "Digi's Terrain Tools";
        public const string Version = "2.0.0";

        // Other authors' terrain tool mods: same keys, same network data, same pieces.
        private static readonly Dictionary<string, string> Overlapping = new Dictionary<string, string>
        {
            { "terrahoe", "TerraHoe" }, { "Searica.Valheim.TerrainTools", "Advanced Terrain Modifiers" },
        };

        internal const float VanillaLimit = 8f;

        internal static BepInEx.Logging.ManualLogSource Log;
        internal static ConfigEntry<int> Strength, SmoothStrength;
        internal static ConfigEntry<float> MaxHeightChange, MaxRadius, RadiusStep, RaiseStep;
        internal static ConfigEntry<bool> IncludeCultivator, SteadyOutline;
        internal static ConfigEntry<KeyCode> RadiusKey, StepKey;
        internal static ConfigEntry<KeyboardShortcut> SquareKey;

        // The new parts (sizes, square mode, outline, new pieces, the extra network data) run.
        internal static bool ToolsOn;
        private bool _started;

        // The plugin ID changed from pieter.valheim.* to digi.valheim.*: carry the old settings file over once.
        private void MigrateConfig()
        {
            var old = Path.Combine(BepInEx.Paths.ConfigPath, Guid.Replace("digi.valheim.", "pieter.valheim.") + ".cfg");
            if (File.Exists(Config.ConfigFilePath) || !File.Exists(old)) return;
            File.Copy(old, Config.ConfigFilePath);
            Config.Reload();
        }

        private void Awake()
        {
            MigrateConfig();
            Log = Logger;
            var admin = new ConfigurationManagerAttributes { IsAdminOnly = true };
            Strength = Config.Bind("Level ground", "Level strength", 0,
                new ConfigDescription("How flat the hoe's Level ground makes things: 0 = normal (only the middle gets flat), 100 = the whole circle becomes exactly flat. Set by the host, the same for everyone.",
                    new AcceptableValueRange<int>(0, 100), admin));
            MaxHeightChange = Config.Bind("Level ground", "Max height change (m)", VanillaLimit,
                new ConfigDescription("How far the ground can be raised above or dug below how the world made it, in meters: the game allows 8 (and only 1 for the hoe's Level ground). Counts for levelling, raising, digging and every tool here. Set by the host, the same for everyone (players without this mod see at most 8 m).",
                    new AcceptableValueRange<float>(VanillaLimit, 200f), admin));
            IncludeCultivator = Config.Bind("Level ground", "Include cultivator", false,
                new ConfigDescription("Also use this strength for the cultivator, which smooths the ground the same way. Set by the host, the same for everyone.",
                    null, admin));

            MaxRadius = Config.Bind("Tools", "Max radius (m)", 30f,
                new ConfigDescription("The largest size a hoe or cultivator tool can be given with the mouse wheel. Set by the host, the same for everyone.",
                    new AcceptableValueRange<float>(2f, 100f), admin));
            RadiusKey = Config.Bind("Tools", "Size key", KeyCode.LeftShift,
                "Hold this key and turn the mouse wheel to make the selected hoe or cultivator tool bigger or smaller. Every tool keeps its own size.");
            RadiusStep = Config.Bind("Tools", "Size step (m)", 0.5f,
                new ConfigDescription("How much one notch of the mouse wheel changes the size.", new AcceptableValueRange<float>(0.1f, 5f)));
            SquareKey = Config.Bind("Tools", "Square key", new KeyboardShortcut(KeyCode.G),
                "Switches every hoe and cultivator tool between round and square (lined up with the ground's 1 m grid), for neat paths, fields and floors.");
            StepKey = Config.Bind("Tools", "Raise and lower step key", KeyCode.LeftControl,
                "With Raise or Lower selected: hold this key and turn the mouse wheel to change how much one click moves the ground.");
            RaiseStep = Config.Bind("Tools", "Raise and lower step (m)", 0.25f,
                new ConfigDescription("How much one click of Raise or Lower moves the ground, in meters.", new AcceptableValueRange<float>(0.05f, 2f)));
            SmoothStrength = Config.Bind("Tools", "Smooth strength (%)", 50,
                new ConfigDescription("How much one click of Smooth evens out the ground: 100 = as even as it gets in one click.",
                    new AcceptableValueRange<int>(10, 100)));
            SteadyOutline = Config.Bind("Tools", "Steady outline", true,
                "Show a steady outline on the ground of exactly what the tool changes, instead of the game's pulsing circle.");
            if (FirstTime("from level strength")) FromLevelStrength();

            PatchModule(typeof(Doing));
            Limits.PatchHeights(ModuleHarmony);
            Logger.LogInfo("Terrain tools ready.");
        }

        // After every plugin has loaded (BepInEx loads them in ID order): only then is it known whether an overlapping
        // terrain mod is installed.
        private void Start()
        {
            _started = true;
            var other = Overlapping.Where(p => Chainloader.PluginInfos.ContainsKey(p.Key)).Select(p => p.Value).FirstOrDefault();
            if (other != null)
            {
                Logger.LogWarning($"{other} is installed: it does the same jobs, so only Level strength and the height limit run here " +
                                  "(no tool sizes, square mode, outline or new hoe pieces). Remove it to use them.");
                return;
            }
            ToolsOn = true;
            PatchModule(typeof(Sending));
            Limits.PatchSquares(ModuleHarmony);
            PatchModule(typeof(Tools));
        }

        private void Update()
        {
            if (_started && ToolsOn) Tools.Tick();
        }

        private void OnDestroy()
        {
            if (ToolsOn) Tools.Remove();
            ToolsOn = false;
            Sending.Reset();
            Ops.Reset();
        }

        // Level Strength's settings (this sub-mod was Level Strength before), once.
        private void FromLevelStrength()
        {
            try
            {
                var path = Path.Combine(BepInEx.Paths.ConfigPath, "digi.valheim.building.cfg");
                if (!File.Exists(path)) return;
                var map = new Dictionary<string, ConfigEntryBase>
                {
                    { "Level strength", Strength }, { "Max height change (m)", MaxHeightChange }, { "Include cultivator", IncludeCultivator },
                };
                var inSection = false;
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        inSection = line == "[Level Strength - Level ground]";
                        continue;
                    }
                    var eq = line.IndexOf(" = ", StringComparison.Ordinal);
                    if (!inSection || line.StartsWith("#") || eq <= 0) continue;
                    if (map.TryGetValue(line.Substring(0, eq).Trim(), out var entry)) entry.SetSerializedValue(line.Substring(eq + 3));
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning("Level Strength's settings not carried over: " + e.Message);
            }
        }
    }
}
