// Epic Loot effects can exclude other effects (e.g. "Elemental Damage Dealt" excludes every "Add X Damage"), and
// shardstones follow those rules, so a shard often cannot go into a weapon that already has a damage enchantment.
// Its "matching item effect" settings only cover the same effect twice. This ignores the exclusion list while a
// shardstone (not a runestone) is being socketed; enchanting, augmenting and loot rolls keep the full rules.
// Epic Loot 0.15 also checks the other way round (an effect on the item that lists the shard's effect) and the shards
// already in the item's sockets; both are skipped for a shard too.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace ShardExclusivity
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.shardexclusivity";
        public const string Name = "Digi's Shard Exclusivity";
        public const string Version = "1.0.3";
        private const string EpicLootGuid = "randyknapp.mods.epicloot";

        private static ConfigEntry<bool> _enabled;
        private static MethodInfo _resolve;
        private static FieldInfo _exclusive;
        private static object _runeColor;
        private static int _shardDepth;

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
            _enabled = Config.Bind("General", "Ignore effect exclusions for shards", true,
                "Shardstones can be put into an item even when their effect normally clashes with an effect the item already has. Enchanting and loot keep the normal rules.");

            var manager = AccessTools.TypeByName("EpicLoot.ShardStones.ShardSocketManager");
            var requirements = AccessTools.TypeByName("EpicLoot.MagicItemEffectRequirements");
            var shardType = AccessTools.TypeByName("EpicLoot.ShardStones.ShardType") ?? AccessTools.TypeByName("EpicLoot.ShardType");
            _resolve = manager == null ? null : AccessTools.Method(manager, "ResolveSocketedEffect");
            _exclusive = requirements == null ? null : AccessTools.Field(requirements, "ExclusiveEffectTypes");
            _runeColor = shardType == null ? null : Enum.Parse(shardType, "None");
            // The CheckRequirements that reports why (an "out RequirementFailure"): found by that, not by its whole parameter
            // list (Epic Loot 0.16 added the socketed effects at the end, and the old exact lookup switched this sub-mod off).
            var failure = AccessTools.TypeByName("EpicLoot.RequirementFailure")?.MakeByRefType();
            var check = requirements == null || failure == null ? null : AccessTools.GetDeclaredMethods(requirements)
                .FirstOrDefault(m => m.Name == "CheckRequirements" && m.GetParameters().Any(p => p.ParameterType == failure));

            if (_resolve == null || _exclusive == null || _runeColor == null || check == null)
            {
                Logger.LogError("Disabled, Epic Loot's shard socket internals were not found.");
                return;
            }

            var harmony = ModuleHarmony;
            foreach (var name in new[] { "CanSocket", "CanCoexist", "AddShard" })
            {
                var method = AccessTools.Method(manager, name);
                if (method == null) continue;
                harmony.Patch(method, prefix: Hook(nameof(SocketPrefix)), finalizer: Hook(nameof(SocketFinalizer)));
            }
            harmony.Patch(check, prefix: Hook(nameof(CheckPrefix)), finalizer: Hook(nameof(CheckFinalizer)));
            // Epic Loot 0.15 and newer: the reverse check and the check against the other sockets.
            var excludes = AccessTools.Method(requirements, "EffectsExclude", new[] { typeof(string), typeof(string) });
            if (excludes != null) harmony.Patch(excludes, prefix: Hook(nameof(ExcludesPrefix)));
            var excluding = AccessTools.Method(requirements, "FindEffectExcluding");
            if (excluding != null) harmony.Patch(excluding, prefix: Hook(nameof(ExcludingPrefix)));
            Logger.LogInfo("Shardstones ignore effect exclusions when socketed.");
        }

        private static HarmonyMethod Hook(string name) =>
            new HarmonyMethod(typeof(Plugin).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static));

        // Mark the socketing check as "shard" (not rune) for the duration of the call.
        private static void SocketPrefix(ItemDrop.ItemData equipment, ItemDrop.ItemData input, out bool __state)
        {
            __state = false;
            if (!_enabled.Value || equipment == null || input == null) return;
            var args = new object[] { equipment, input, null, null, null };
            try
            {
                if (!(bool)_resolve.Invoke(null, args) || Equals(args[3], _runeColor)) return;
            }
            catch
            {
                return;
            }
            _shardDepth++;
            __state = true;
        }

        private static Exception SocketFinalizer(Exception __exception, bool __state)
        {
            if (__state) _shardDepth--;
            return __exception;
        }

        // While a shard is being checked, empty the exclusion list and restore it right after.
        private static void CheckPrefix(object __instance, out List<string> __state)
        {
            __state = null;
            if (_shardDepth <= 0) return;
            if (!(_exclusive.GetValue(__instance) is List<string> list) || list.Count == 0) return;
            __state = list;
            _exclusive.SetValue(__instance, new List<string>());
        }

        private static Exception CheckFinalizer(Exception __exception, object __instance, List<string> __state)
        {
            if (__state != null) _exclusive.SetValue(__instance, __state);
            return __exception;
        }

        // While a shard is being checked: no two effects exclude each other ...
        private static bool ExcludesPrefix(ref bool __result)
        {
            if (_shardDepth <= 0) return true;
            __result = false;
            return false;
        }

        // ... and no effect on the item excludes the shard's effect.
        private static bool ExcludingPrefix(ref string __result)
        {
            if (_shardDepth <= 0) return true;
            __result = null;
            return false;
        }
    }
}
