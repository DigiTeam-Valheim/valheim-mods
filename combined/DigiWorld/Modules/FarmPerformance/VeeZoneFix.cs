// Farm Performance, a slowdown in Valheim Enhanced Edition. Its "FastUnloadCheck" ([Zones], on by default) keeps a list of
// every loaded object per zone; new objects wait in a "pending" list that only empties while that setting is on. With it
// off (the pack's choice) the pending list kept growing for the whole session, and every object destroyed (a picked crop,
// an item picked up, a felled tree, a killed creature) searched the whole list to remove itself from it: about 12 ms
// each after a while, so a field harvested at once (50+ crops) froze the game for a second or more.
// While that setting is off, its bookkeeping is skipped here (nothing reads it then) and its lists are emptied; turned
// back on, Valheim Enhanced Edition rebuilds them itself (it compares its count with the scene's).
using System;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;

namespace FarmPerformance
{
    internal static class VeeZoneFix
    {
        private static ConfigEntry<bool> _fastUnloadCheck;
        private static MethodInfo _clear;

        internal static void Patch(Harmony harmony, BepInEx.Logging.ManualLogSource log)
        {
            var zones = AccessTools.TypeByName("PerfMod.ZoneUnload");
            var plugin = AccessTools.TypeByName("PerfMod.Plugin");
            if (zones == null || plugin == null) return; // Valheim Enhanced Edition not installed
            _fastUnloadCheck = AccessTools.Field(plugin, "ZoneUnloadOn")?.GetValue(null) as ConfigEntry<bool>;
            _clear = AccessTools.Method(zones, "Clear");
            if (_fastUnloadCheck == null || _clear == null)
            {
                log.LogWarning("Valheim Enhanced Edition's zone bookkeeping was not found; nothing changed there.");
                return;
            }
            var skip = new HarmonyMethod(typeof(VeeZoneFix), nameof(OnlyWhileOn));
            foreach (var name in new[] { "BeforeAdd", "AfterAdd", "BeforeDestroy", "BeforeZdoDestroyed", "AfterRemove" })
            {
                var method = AccessTools.Method(zones, name);
                if (method != null) harmony.Patch(method, prefix: skip);
            }
            _fastUnloadCheck.SettingChanged += SettingChanged;
            if (!_fastUnloadCheck.Value) _clear.Invoke(null, null);
        }

        internal static void Unpatch()
        {
            if (_fastUnloadCheck != null) _fastUnloadCheck.SettingChanged -= SettingChanged;
            _fastUnloadCheck = null;
            _clear = null;
        }

        private static void SettingChanged(object sender, EventArgs e)
        {
            if (_fastUnloadCheck != null && !_fastUnloadCheck.Value) _clear?.Invoke(null, null);
        }

        private static bool OnlyWhileOn() => _fastUnloadCheck == null || _fastUnloadCheck.Value;
    }
}
