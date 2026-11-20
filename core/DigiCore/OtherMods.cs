// Another author's mod (or an old separate Digi mod) that one of our sub-mods replaces, installed next to it: that mod
// is switched off and ours runs. It stays installed, but:
// - every Harmony patch from its DLL is removed, except its ServerSync part (that one tells a server the mod is there,
//   so a server that requires the mod still lets you in; the settings it syncs reach nothing);
// - its plugin and its other components are disabled and their coroutines stopped (a plugin disabled before its Start
//   never starts);
// - its settings no longer call into it when they change (a server's synced settings).
// Done when our sub-mod starts (its Start, or the combined mod's for a sub-mod that took a mod's place by ID) and once
// more at the main menu, for patches the mod made later (in its own Start). Only while our sub-mod is switched on: with
// ours off in F1, theirs runs as before (after a restart).
// Every combined mod has its own copy of this code: the list of switched-off mods lives in the AppDomain.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace DigiCore
{
    internal static class OtherMods
    {
        private const string SwitchedKey = "digi.valheim.switchedoff"; // lines "<plugin ID>|<name>"

        private static List<string> Switched
        {
            get
            {
                var list = AppDomain.CurrentDomain.GetData(SwitchedKey) as List<string>;
                if (list == null)
                {
                    list = new List<string>();
                    AppDomain.CurrentDomain.SetData(SwitchedKey, list);
                }
                return list;
            }
        }

        // The names of the mods switched off, for the main menu message.
        internal static List<string> Names() =>
            Switched.Select(l => l.Substring(l.IndexOf('|') + 1)).Distinct().OrderBy(n => n).ToList();

        // Switches the mod with this plugin ID off; false when it is not installed. name: as players know it (null: the
        // name it gives itself).
        internal static bool SwitchOff(string guid, string name, ManualLogSource log)
        {
            if (!Chainloader.PluginInfos.TryGetValue(guid, out var info) || info.Instance == null) return false;
            name = name ?? info.Metadata?.Name ?? guid;
            var removed = Silence(info.Instance);
            if (!Switched.Any(l => l.StartsWith(guid + "|", StringComparison.Ordinal)))
            {
                Switched.Add(guid + "|" + name);
                log?.LogWarning($"{name} is installed too: switched it off ({removed} patches removed), ours does its job. You can remove it.");
            }
            return true;
        }

        internal static void SweepAgain()
        {
            foreach (var line in Switched.ToList())
            {
                var guid = line.Substring(0, line.IndexOf('|'));
                if (Chainloader.PluginInfos.TryGetValue(guid, out var info) && info.Instance != null) Silence(info.Instance);
            }
        }

        private static int Silence(BaseUnityPlugin plugin)
        {
            var assembly = plugin.GetType().Assembly;
            if (assembly == typeof(OtherMods).Assembly) return 0; // never ourselves
            var removed = 0;
            try
            {
                foreach (var component in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (component == null || component.GetType().Assembly != assembly) continue;
                    component.StopAllCoroutines();
                    component.enabled = false;
                }
                if (plugin.enabled) plugin.enabled = false;
                foreach (var method in Harmony.GetAllPatchedMethods().ToList())
                {
                    var patches = Harmony.GetPatchInfo(method);
                    if (patches == null) continue;
                    foreach (var patch in patches.Prefixes.Concat(patches.Postfixes).Concat(patches.Transpilers)
                                 .Concat(patches.Finalizers).Concat(patches.ILManipulators).ToList())
                    {
                        var type = patch.PatchMethod?.DeclaringType;
                        if (type == null || type.Assembly != assembly || (type.Namespace ?? "").StartsWith("ServerSync", StringComparison.Ordinal)) continue;
                        try
                        {
                            new Harmony(patch.owner).Unpatch(method, patch.PatchMethod);
                            removed++;
                        }
                        catch (Exception) { /* left as it is */ }
                    }
                }
                Unhook(plugin.Config, assembly);
            }
            catch (Exception) { /* whatever is left keeps running */ }
            return removed;
        }

        // Takes the mod's own handlers off its settings' "changed" events.
        private static void Unhook(ConfigFile config, Assembly assembly)
        {
            if (config == null) return;
            Strip(config, typeof(ConfigFile), assembly);
            foreach (var pair in config) Strip(pair.Value, pair.Value.GetType(), assembly);
        }

        private static void Strip(object owner, Type type, Assembly assembly)
        {
            var field = type.GetField("SettingChanged", BindingFlags.Instance | BindingFlags.NonPublic);
            if (owner == null || field == null || !(field.GetValue(owner) is Delegate handlers)) return;
            var keep = handlers.GetInvocationList().Where(d => d.Method.DeclaringType == null || d.Method.DeclaringType.Assembly != assembly).ToArray();
            if (keep.Length < handlers.GetInvocationList().Length) field.SetValue(owner, Delegate.Combine(keep));
        }
    }
}
