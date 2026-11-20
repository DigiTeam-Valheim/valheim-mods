// Shows how long a fermenter still needs when you aim at it: "Ready in 12:34", plus how much it will give.
// The fermenter restarts from zero whenever it is exposed (no roof / open to the sky), so that is shown too.
using System;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace FermenterTimer
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.fermentertimer";
        public const string Name = "Digi's Fermenter Timer";
        public const string Version = "1.0.2";

        private static ConfigEntry<bool> _showYield;

        private static readonly AccessTools.FieldRef<Fermenter, ZNetView> View = AccessTools.FieldRefAccess<Fermenter, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<Fermenter, bool> Exposed = AccessTools.FieldRefAccess<Fermenter, bool>("m_exposed");
        private static readonly AccessTools.FieldRef<Fermenter, bool> HasRoof = AccessTools.FieldRefAccess<Fermenter, bool>("m_hasRoof");

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
            _showYield = Config.Bind("General", "Show yield", true, "Also show what the barrel will give, e.g. \"6x Minor healing mead\".");
            PatchModule(typeof(Plugin));
        }

        [HarmonyPatch(typeof(Fermenter), nameof(Fermenter.GetHoverText))]
        [HarmonyPostfix]
        private static void GetHoverText(Fermenter __instance, ref string __result)
        {
            var view = View(__instance);
            if (view == null || !view.IsValid() || ZNet.instance == null) return;
            var zdo = view.GetZDO();
            var content = zdo.GetInt(ZDOVars.s_content);
            if (content == 0) return;

            var extra = "";
            var conversion = __instance.m_conversion.FirstOrDefault(c => c.m_from != null && c.m_from.name.GetStableHashCode() == content);
            if (_showYield.Value && conversion?.m_to != null)
                extra += "\n" + Localization.instance.Localize($"{conversion.m_producedItems}x {conversion.m_to.m_itemData.m_shared.m_name}");

            var start = zdo.GetLong(ZDOVars.s_startTime, 0L);
            if (start != 0L)
            {
                var left = __instance.m_fermentationDuration - (ZNet.instance.GetTime() - new DateTime(start)).TotalSeconds;
                if (left > 0)
                {
                    if (!HasRoof(__instance) || Exposed(__instance))
                        extra += "\n<color=orange>Not fermenting: needs a roof, timer restarts</color>";
                    else
                        extra += $"\nReady in <color=yellow>{Format(left)}</color>";
                }
            }
            if (extra == "") return;
            // Put the info right after the first line (the barrel name and status), before any key hints.
            var nl = __result.IndexOf('\n');
            __result = nl < 0 ? __result + extra : __result.Substring(0, nl) + extra + __result.Substring(nl);
        }

        private static string Format(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Ceiling(seconds));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }
    }
}
