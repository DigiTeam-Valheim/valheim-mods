// Measures every world/character save: time, memory allocated and garbage collections per step (main thread and the
// background write), then how the 15 seconds after the save went (fps, worst frame, collections). One [Save] report
// per save in the BepInEx log. Diagnostic only; it changes nothing about how the game saves.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace LagMeter
{
    internal static class SaveProbe
    {
        private sealed class Probe
        {
            public long Ticks, Memory;
            public int Collections;
        }

        // Report order and labels; indented lines are parts of the line above.
        private static readonly string[] Steps =
        {
            "Character save", "Other players' characters", "World save, main thread (the freeze)",
            "  copy object data", "    clone changed areas", "    copy extra data of ALL objects", "  zone data",
            "World file write (background thread)",
        };

        private static readonly Dictionary<MethodBase, string> Labels = new Dictionary<MethodBase, string>();
        private static readonly Dictionary<string, string> Results = new Dictionary<string, string>();
        private static readonly object Lock = new object();
        private static ManualLogSource _log;

        private static bool _watching, _writeDone;
        private static float _watchStart, _worst;
        private static int _frames, _collectionsAtStart;

        internal static void Init(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            Patch(harmony, Steps[0], AccessTools.Method(typeof(Game), nameof(Game.SavePlayerProfile)));
            Patch(harmony, Steps[1], AccessTools.Method(typeof(ZNet), "SaveOtherPlayerProfiles"));
            Patch(harmony, Steps[2], AccessTools.Method(typeof(ZNet), "SaveWorld"));
            Patch(harmony, Steps[3], AccessTools.Method(typeof(ZDOMan), "PrepareSave"));
            Patch(harmony, Steps[4], AccessTools.Method(typeof(ZDOMan), "GetSaveClonePerChunk"));
            Patch(harmony, Steps[5], AccessTools.Method(typeof(ZDOExtraData), "PrepareSave"));
            Patch(harmony, Steps[6], AccessTools.Method(typeof(ZoneSystem), "PrepareSave"));
            Patch(harmony, Steps[7], AccessTools.Method(typeof(ZNet), "SaveWorldThread"));
        }

        private static void Patch(Harmony harmony, string label, MethodInfo target)
        {
            if (target == null)
            {
                _log.LogWarning($"Save step not measured, method not found: {label}");
                return;
            }
            Labels[target] = label;
            harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(SaveProbe), nameof(Start)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(SaveProbe), nameof(Stop)) { priority = Priority.Last });
        }

        private static void Start(out Probe __state) =>
            __state = new Probe { Ticks = Stopwatch.GetTimestamp(), Memory = GC.GetTotalMemory(false), Collections = GC.CollectionCount(0) };

        private static Exception Stop(Exception __exception, MethodBase __originalMethod, Probe __state)
        {
            if (__state == null || !Labels.TryGetValue(__originalMethod, out var label)) return __exception;
            var ms = (Stopwatch.GetTimestamp() - __state.Ticks) * 1000.0 / Stopwatch.Frequency;
            var mb = (GC.GetTotalMemory(false) - __state.Memory) / 1048576.0;
            var collections = GC.CollectionCount(0) - __state.Collections;
            // A collection inside the step frees memory, so the memory change can even be negative then.
            var text = $"{ms:0} ms, memory {mb:+0;-0;0} MB, {collections} GCs";
            lock (Lock)
            {
                Results[label] = text;
                if (label == Steps[7]) _writeDone = true;
            }
            if (label == Steps[2])
            {
                // The freeze is over; watch the next 15 seconds (runs on the main thread).
                _watching = true;
                _watchStart = Time.realtimeSinceStartup;
                _worst = 0f;
                _frames = 0;
                _collectionsAtStart = GC.CollectionCount(0);
            }
            return __exception;
        }

        // Called every frame by the plugin.
        internal static void Tick()
        {
            if (!_watching) return;
            _frames++;
            _worst = Mathf.Max(_worst, Time.unscaledDeltaTime);
            var elapsed = Time.realtimeSinceStartup - _watchStart;
            if (elapsed < 15f) return;
            _watching = false;

            var sb = new StringBuilder($"[Save] world save at {DateTime.Now:HH:mm:ss}, {Objects()} objects in the world");
            lock (Lock)
            {
                foreach (var step in Steps)
                    if (Results.TryGetValue(step, out var text)) sb.Append($"\n      {step}: {text}");
                if (!_writeDone) sb.Append("\n      World file write (background thread): still running after 15 s");
                Results.Clear();
                _writeDone = false;
            }
            sb.Append($"\n      15 s after the save: {_frames / elapsed:0.0} fps avg, worst frame {_worst * 1000f:0} ms, " +
                      $"{GC.CollectionCount(0) - _collectionsAtStart} GCs, memory now {GC.GetTotalMemory(false) / 1048576.0:0} MB");
            _log.LogInfo(sb.ToString());
        }

        private static string Objects()
        {
            var field = AccessTools.Field(typeof(ZDOMan), "m_objectsByID");
            var all = ZDOMan.instance != null ? field?.GetValue(ZDOMan.instance) as System.Collections.ICollection : null;
            return all != null ? all.Count.ToString("N0") : "?";
        }
    }
}
