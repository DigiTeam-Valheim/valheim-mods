// Diagnostic only: measures what makes the frame rate drop, so a fix can aim at the real cause.
// - Every frame: how long each of Unity's ~150 engine systems took on the main thread (FrameLoop.cs), what Valheim's
//   update batches, heavy game systems and every mod's updates took (Timers.cs), and Unity's own counters: main and
//   render thread time, graphics card time, draw calls, shadow casters, ... (Counters.cs).
// - Every second one sample, with what was around the player (Sampler.cs): written to a data file, [LowFPS] reports
//   while the frame rate is below the threshold, [Spike] lines for single slow frames, a [Lag] line every interval
//   and a [Summary] every 5 minutes. The mark key writes a [Mark] report of the last 10 seconds.
// Once per world, about 30 s in, it also counts every saved object in the world (what a save has to write) by type,
// and every minute it lists the most common loaded objects and what the lights belong to.
// Every save gets a [Save] report: time, memory and garbage collections per save step, and the 15 s after it.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace LagMeter
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.lagmeter";
        public const string Name = "Digi's Lag Meter";
        public const string Version = "2.0.0";

        private static ConfigEntry<bool> _enabled, _dataFile, _timeMods, _deep;
        private static ConfigEntry<int> _interval, _threshold, _spike, _reportEvery;
        private static ConfigEntry<KeyCode> _markKey;

        private static FieldInfo _sceneInstances;
        private bool _censusDone, _running;
        private float _modScanAt = -1f, _loadedAt;
        private int _modScans;
        private static readonly FieldInfo ObjectsById = AccessTools.Field(typeof(ZDOMan), "m_objectsByID");

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
            _enabled = Config.Bind("General", "Enabled", true, "Measure and log. Turn off (or switch the sub-mod off) when done.");
            _interval = Config.Bind("General", "Interval (seconds)", 10, new ConfigDescription(
                "Seconds per [Lag] line in the log (a short report of that time).", new AcceptableValueRange<int>(2, 120)));
            _threshold = Config.Bind("Low FPS", "FPS threshold", 40, new ConfigDescription(
                "A second below this many frames per second gets a [LowFPS] report: what held the frame back and what was unusual.",
                new AcceptableValueRange<int>(10, 240)));
            _reportEvery = Config.Bind("Low FPS", "Report every (seconds)", 10, new ConfigDescription(
                "While the frame rate stays low, another [LowFPS] report this often (each one covers the time since the last).",
                new AcceptableValueRange<int>(2, 120)));
            _spike = Config.Bind("Low FPS", "Spike frame (ms)", 100, new ConfigDescription(
                "A single frame slower than this gets a [Spike] line with that frame's own breakdown (at most one every 2 seconds).",
                new AcceptableValueRange<int>(30, 2000)));
            _dataFile = Config.Bind("Data", "Write data file", true,
                "One line per second with everything measured, in BepInEx\\LagMeter\\ (a new file per world visit), for analysis afterwards.");
            _timeMods = Config.Bind("Data", "Time mods", true,
                "Also time the updates of every mod's own components (labelled with the mod). Takes effect when a world loads.");
            _deep = Config.Bind("Data", "Time every component (deep)", true,
                "Also time the updates of every game component type around you (\"game: Fireplace.Update\"), so all script time is " +
                "accounted for. Adds a fraction of a microsecond per timed call (the reports show the calls per frame). Takes " +
                "effect when a world loads.");
            _markKey = Config.Bind("General", "Mark key", KeyCode.F9,
                "Press when something feels laggy: writes a [Mark] report of the last 10 seconds and marks the moment in the data file.");
            _sceneInstances = AccessTools.Field(typeof(ZNetScene), "m_instances");

            Sampler.Log = Logger;
            Sampler.Threshold = () => _threshold.Value;
            Sampler.SpikeMs = () => _spike.Value;
            Sampler.ReportEvery = () => _reportEvery.Value;
            Sampler.Interval = () => _interval.Value;
            Sampler.WriteFile = () => _dataFile.Value;
            Timers.Init(ModuleHarmony, Logger);
            SaveProbe.Init(ModuleHarmony, Logger);
            _enabled.SettingChanged += (s, e) => Toggle();
            Toggle();
            Logger.LogInfo($"Timing {FrameLoop.Names.Count} engine systems, {Counters.All.Count} Unity counters and {Timers.Names.Count} game systems; " +
                           $"reports below {_threshold.Value} fps.");
        }

        private void Toggle()
        {
            if (_enabled.Value && !_running)
            {
                Counters.Start();
                FrameLoop.Install();
                Sampler.Start();
                _running = true;
            }
            else if (!_enabled.Value && _running)
            {
                Sampler.Stop();
                FrameLoop.Uninstall();
                Counters.Stop();
                _running = false;
            }
        }

        private void OnDestroy()
        {
            Sampler.Stop();
            FrameLoop.Uninstall();
            Counters.Stop();
            Timers.Clear();
            _running = false;
        }

        private void Update()
        {
            SaveProbe.Tick();
            if (!_enabled.Value || Player.m_localPlayer == null)
            {
                _modScanAt = -1f;
                _modScans = 0;
                return;
            }
            var now = Time.realtimeSinceStartup;
            // Mod components: once 10 s into a world, again after 2 minutes (windows opened by then).
            if ((_timeMods.Value || _deep.Value) && _modScans < 2)
            {
                if (_modScanAt < 0f) _modScanAt = now + 10f;
                else if (now >= _modScanAt)
                {
                    var added = Timers.TimeComponents(Logger, _timeMods.Value, _deep.Value);
                    if (added > 0) Logger.LogInfo($"Timing {added} more update methods of {(_deep.Value ? "game and mod" : "mod")} components.");
                    _modScans++;
                    _modScanAt = now + 120f;
                }
            }
            if (_markKey.Value != KeyCode.None && UnityInput.Current.GetKeyDown(_markKey.Value)) Sampler.Mark();
            if (!_censusDone && now > 30f && ZDOMan.instance != null)
            {
                _censusDone = true;
                try { Logger.LogInfo(Census()); }
                catch (Exception e) { Logger.LogWarning("Census failed: " + e.Message); }
            }
            if (now >= _loadedAt)
            {
                if (_loadedAt > 0f)
                {
                    try { Logger.LogInfo(Loaded()); }
                    catch (Exception e) { Logger.LogWarning("Loaded list failed: " + e.Message); }
                }
                _loadedAt = now + 60f;
            }
        }

        // Every saved object in the world by prefab, plus totals by kind.
        private static string Census()
        {
            if (!(ObjectsById?.GetValue(ZDOMan.instance) is IDictionary all)) return "[Census] ZDO list not found";
            var byHash = new Dictionary<int, int>();
            foreach (var value in all.Values)
            {
                if (!(value is ZDO zdo)) continue;
                var hash = zdo.GetPrefab();
                byHash.TryGetValue(hash, out var n);
                byHash[hash] = n + 1;
            }
            var kinds = new Dictionary<string, int>();
            var named = new List<KeyValuePair<string, int>>();
            foreach (var p in byHash)
            {
                var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(p.Key) : null;
                var name = prefab != null ? prefab.name : $"unknown#{p.Key}";
                named.Add(new KeyValuePair<string, int>(name, p.Value));
                var kind = prefab == null ? "unknown (removed mod?)"
                    : prefab.GetComponent<ItemDrop>() != null ? "dropped items"
                    : prefab.GetComponent<Piece>() != null ? "building pieces"
                    : prefab.GetComponent<Character>() != null ? "creatures"
                    : prefab.GetComponent<TreeBase>() != null || prefab.GetComponent<TreeLog>() != null ? "trees/logs"
                    : prefab.GetComponent<Destructible>() != null || prefab.GetComponent<MineRock>() != null || prefab.GetComponent<MineRock5>() != null ? "rocks/bushes/destructibles"
                    : prefab.GetComponent<Pickable>() != null ? "pickables"
                    : prefab.GetComponent<TerrainComp>() != null || prefab.GetComponent<TerrainModifier>() != null ? "terrain edits"
                    : "other";
                kinds.TryGetValue(kind, out var k);
                kinds[kind] = k + p.Value;
            }
            var total = all.Count;
            var sb = new StringBuilder($"[Census] {total:N0} saved objects in the world, {byHash.Count} types");
            sb.Append("\n      by kind: " + string.Join(", ", kinds.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value:N0}")));
            sb.Append("\n      top types: " + string.Join(", ", named.OrderByDescending(n => n.Value).Take(30).Select(n => $"{n.Key} {n.Value:N0}")));
            return sb.ToString();
        }

        // The most common loaded objects and what the lights belong to.
        private static string Loaded()
        {
            var sb = new StringBuilder("[Loaded]");
            if (_sceneInstances?.GetValue(ZNetScene.instance) is IDictionary scene)
            {
                var byName = new Dictionary<string, int>();
                foreach (var value in scene.Values)
                {
                    var view = value as ZNetView;
                    if (view == null) continue;
                    var name = Utils.GetPrefabName(view.gameObject);
                    byName.TryGetValue(name, out var n);
                    byName[name] = n + 1;
                }
                sb.Append($" top objects ({scene.Count} loaded, {byName.Count} types): " +
                          string.Join(", ", byName.OrderByDescending(p => p.Value).Take(25).Select(p => $"{p.Key} {p.Value}")));
            }
            var lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None)
                .Where(l => l.isActiveAndEnabled && l.type != LightType.Directional).ToList();
            sb.Append("\n      lights by object: " + string.Join(", ", lights
                .GroupBy(l => Utils.GetPrefabName(l.transform.root.gameObject))
                .OrderByDescending(g => g.Count()).Take(15)
                .Select(g => $"{g.Key} {g.Count()}" + (g.Any(l => l.shadows != LightShadows.None) ? " (shadows)" : ""))));
            return sb.ToString();
        }
    }
}
