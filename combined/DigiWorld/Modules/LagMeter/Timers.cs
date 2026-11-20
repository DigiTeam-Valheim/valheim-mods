// Lag Meter, script timers (Harmony): what the "Scripts" part of a frame is spent on.
// - Valheim's own batches: MonoUpdaters runs most per-object updates (creatures, AI, ships, smoke, lights, ground,
//   ...) through four helpers that get a name per batch ("MonoUpdaters.FixedUpdate.Character"): each name is timed.
// - Single game systems that are known to be heavy (object streaming, network, map, camera, building wear, ...).
// - Mods: the Update, LateUpdate, FixedUpdate and OnGUI of every mod component found in the scene (plugins, our
//   sub-mods, their windows), labelled with the mod. Time a mod spends inside game methods it patches is counted
//   with that game method, not here.
// - Deep mode: the same for every game component type in the scene ("game: Fireplace"), so the scripts part is
//   accounted for in full. Each timed call costs a fraction of a microsecond; calls are counted, so that cost is known.
// Timers can overlap (a mod's update inside a game update); they say where time goes, they do not add up to a total.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace LagMeter
{
    internal static class Timers
    {
        internal static readonly List<string> Names = new List<string>();
        internal static readonly List<string> Kinds = new List<string>(); // "batch", "game", "mod"
        internal static long[] Frame = new long[64];
        internal static int[] Calls = new int[64]; // timed calls in the current frame
        private static readonly Dictionary<MethodBase, int> ByMethod = new Dictionary<MethodBase, int>();
        private static readonly Dictionary<string, int> ByScope = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> ByName = new Dictionary<string, int>();
        private static Harmony _harmony;

        internal static void Init(Harmony harmony, BepInEx.Logging.ManualLogSource log)
        {
            _harmony = harmony;
            foreach (var name in new[] { "CustomFixedUpdate", "CustomUpdate", "CustomLateUpdate", "UpdateAI" })
            {
                var target = AccessTools.Method(typeof(MonoUpdatersExtra), name);
                if (target == null) { log.LogWarning($"Not timed: MonoUpdatersExtra.{name} not found."); continue; }
                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(Timers), nameof(StartTimer)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(Timers), nameof(StopScope)) { priority = Priority.Last });
            }
            Game(log, "Object streaming", typeof(ZNetScene), "Update");
            Game(log, "Network", typeof(ZNet), "Update");
            Game(log, "Network (late)", typeof(ZNet), "LateUpdate");
            Game(log, "World data (ZDOs)", typeof(ZDOMan), "Update");
            Game(log, "Zones", typeof(ZoneSystem), "Update");
            Game(log, "Building wear/support", typeof(WearNTearUpdater), "Update");
            Game(log, "Plants", typeof(Plant), "SUpdate");
            Game(log, "Fires", typeof(Fireplace), "UpdateFireplace");
            Game(log, "Smoke render", typeof(SmokeRenderer), "LateUpdate");
            Game(log, "Grass", typeof(ClutterSystem), "LateUpdate");
            Game(log, "Light LOD", typeof(LightLod), "UpdateLights");
            Game(log, "Cooking stations", typeof(CookingStation), "Update");
            Game(log, "Terrain edits", typeof(TerrainComp), "Update");
            Game(log, "Player", typeof(Player), "Update");
            Game(log, "Camera", typeof(GameCamera), "LateUpdate");
            Game(log, "Map", typeof(Minimap), "Update");
            Game(log, "HUD", typeof(Hud), "Update");
            Game(log, "Inventory window", typeof(InventoryGui), "Update");
            Game(log, "Weather/environment", typeof(EnvMan), "Update");
        }

        internal static int Id(string name, string kind)
        {
            if (ByName.TryGetValue(name, out var id)) return id;
            id = Names.Count;
            Names.Add(name);
            Kinds.Add(kind);
            ByName[name] = id;
            if (id >= Frame.Length)
            {
                Array.Resize(ref Frame, Frame.Length * 2);
                Array.Resize(ref Calls, Frame.Length);
            }
            return id;
        }

        private static void Game(BepInEx.Logging.ManualLogSource log, string label, Type type, string method)
        {
            var target = AccessTools.DeclaredMethod(type, method);
            if (target == null) { log.LogWarning($"Not timed: {type.Name}.{method} not found."); return; }
            Patch(target, Id(label, "game"));
        }

        private static bool Patch(MethodBase target, int id)
        {
            if (ByMethod.ContainsKey(target)) return false;
            ByMethod[target] = id;
            _harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(Timers), nameof(StartTimer)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(Timers), nameof(StopTimer)) { priority = Priority.Last });
            return true;
        }

        // Every component in the scene that comes from a plugin's assembly (and in deep mode every game component).
        // Scanned when a world loads (once, a short pause) and again later for windows that were not open yet; each
        // type is patched once.
        internal static int TimeComponents(BepInEx.Logging.ManualLogSource log, bool mods, bool deep)
        {
            if (_harmony == null) return 0;
            var owners = new Dictionary<Assembly, string>();
            foreach (var info in Chainloader.PluginInfos.Values)
                if (info.Instance != null && !owners.ContainsKey(info.Instance.GetType().Assembly))
                    owners[info.Instance.GetType().Assembly] = info.Metadata.Name;
            var self = typeof(Timers).Assembly;
            var added = 0;
            foreach (var type in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                         .Where(b => b != null).Select(b => b.GetType()).Distinct())
            {
                if (type.Assembly == self) continue;
                string label, kind;
                if (owners.TryGetValue(type.Assembly, out var mod))
                {
                    if (!mods) continue;
                    // Our combined mods: name the sub-mod (its namespace) instead of the combined plugin.
                    label = mod.StartsWith("Digi's ") && !typeof(BaseUnityPlugin).IsAssignableFrom(type) && type.Namespace != null
                        ? $"{mod}: {type.Namespace}" : mod;
                    kind = "mod";
                }
                else if (deep && type.Assembly.GetName().Name.StartsWith("assembly_"))
                {
                    label = "game: " + type.Name;
                    kind = "component";
                }
                else continue;
                foreach (var method in new[] { "Update", "LateUpdate", "FixedUpdate", "OnGUI" })
                {
                    var target = AccessTools.Method(type, method, Type.EmptyTypes);
                    if (target == null || target.DeclaringType == typeof(MonoBehaviour) || target.DeclaringType == typeof(BaseUnityPlugin) ||
                        target.IsAbstract || target.ContainsGenericParameters || target.DeclaringType == null || target.DeclaringType.ContainsGenericParameters)
                        continue;
                    try
                    {
                        var name = method == "OnGUI" ? label + " (OnGUI)" : kind == "component" ? $"{label}.{method}" : label;
                        if (Patch(target, Id(name, kind))) added++;
                    }
                    catch (Exception e)
                    {
                        log.LogWarning($"Not timed: {type.FullName}.{method}: {e.Message}");
                    }
                }
            }
            return added;
        }

        private static void StartTimer(out long __state) => __state = Stopwatch.GetTimestamp();

        private static Exception StopTimer(Exception __exception, MethodBase __originalMethod, long __state)
        {
            if (ByMethod.TryGetValue(__originalMethod, out var id))
            {
                Frame[id] += Stopwatch.GetTimestamp() - __state;
                Calls[id]++;
            }
            return __exception;
        }

        private static Exception StopScope(Exception __exception, string profileScope, long __state)
        {
            var end = Stopwatch.GetTimestamp();
            if (profileScope == null) return __exception;
            if (!ByScope.TryGetValue(profileScope, out var id))
            {
                id = Id(profileScope.Replace("MonoUpdaters.", ""), "batch");
                ByScope[profileScope] = id;
            }
            Frame[id] += end - __state;
            Calls[id]++;
            return __exception;
        }

        internal static void Clear()
        {
            Names.Clear();
            Kinds.Clear();
            ByMethod.Clear();
            ByScope.Clear();
            ByName.Clear();
            Frame = new long[64];
            Calls = new int[64];
            _harmony = null;
        }
    }
}
