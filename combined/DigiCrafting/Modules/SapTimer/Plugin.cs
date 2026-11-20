// Aim at an ancient root or a sap extractor to see the timings the game keeps hidden:
// - Ancient root: how much sap it holds, how fast it regrows, and when it is full (or that extractors are tapping it).
// - Sap extractor: when the next sap comes, when it is full, and how much sap is left in its root.
// Only the hover text changes; nothing runs while you are not looking at one.
using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SapTimer
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.saptimer";
        public const string Name = "Digi's Sap Timer";
        public const string Version = "1.0.1";

        private static ConfigEntry<bool> _roots, _extractors;

        private static readonly AccessTools.FieldRef<SapCollector, ZNetView> ExtractorView = AccessTools.FieldRefAccess<SapCollector, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<SapCollector, ResourceRoot> ExtractorRoot = AccessTools.FieldRefAccess<SapCollector, ResourceRoot>("m_root");
        private static readonly List<SapCollector> Extractors = new List<SapCollector>();


        private void Awake()
        {
            _roots = Config.Bind("General", "Show on ancient roots", true, "Show how much sap an ancient root holds and when it is full again.");
            _extractors = Config.Bind("General", "Show on sap extractors", true, "Show when a sap extractor collects its next sap and when it is full.");
            PatchModule(typeof(Plugin));
        }

        [HarmonyPatch(typeof(SapCollector), "Awake")]
        [HarmonyPostfix]
        private static void ExtractorAwake(SapCollector __instance)
        {
            Extractors.RemoveAll(e => e == null);
            Extractors.Add(__instance);
        }

        // Extractors on this root that are still collecting (not full).
        private static int Tapping(ResourceRoot root)
        {
            var count = 0;
            foreach (var extractor in Extractors)
            {
                if (extractor == null || ExtractorRoot(extractor) != root) continue;
                var view = ExtractorView(extractor);
                if (view != null && view.IsValid() && view.GetZDO().GetInt(ZDOVars.s_level) < extractor.m_maxLevel) count++;
            }
            return count;
        }

        // --- Ancient root --------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(ResourceRoot), nameof(ResourceRoot.GetHoverText))]
        [HarmonyPostfix]
        private static void RootText(ResourceRoot __instance, ref string __result)
        {
            if (!_roots.Value || ZNet.instance == null || __instance.m_regenPerSec <= 0f) return;
            var level = __instance.GetLevel();
            var max = __instance.m_maxLevel;
            var text = $"\nSap: <color=yellow>{Mathf.FloorToInt(level)} / {max:0}</color>, regrows 1 every {Format(1f / __instance.m_regenPerSec)}";
            var tapping = Tapping(__instance);
            if (tapping > 0)
                text += $"\nTapped by {tapping} extractor{(tapping == 1 ? "" : "s")}: {(tapping == 1 ? "it collects" : "they share")} the sap as it regrows";
            else if (level < max)
                text += $"\nFull in <color=yellow>{Format((max - level) / __instance.m_regenPerSec)}</color>";
            __result = Insert(__result, text);
        }

        // --- Sap extractor -------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(SapCollector), nameof(SapCollector.GetHoverText))]
        [HarmonyPostfix]
        private static void ExtractorText(SapCollector __instance, ref string __result)
        {
            if (!_extractors.Value || ZNet.instance == null) return;
            var view = ExtractorView(__instance);
            var root = ExtractorRoot(__instance);
            if (view == null || !view.IsValid() || root == null) return;
            var zdo = view.GetZDO();
            var level = zdo.GetInt(ZDOVars.s_level);
            var max = __instance.m_maxLevel;
            var rootLevel = root.GetLevel();
            string text;
            if (level >= max)
                text = "\n<color=orange>Full: empty it to keep collecting</color>";
            else
            {
                // Progress towards the next sap: saved progress plus the time since the extractor last updated it.
                var since = (float)(ZNet.instance.GetTime() - new DateTime(zdo.GetLong(ZDOVars.s_lastTime, ZNet.instance.GetTime().Ticks))).TotalSeconds;
                var unit = __instance.m_secPerUnit;
                var regen = Mathf.Max(root.m_regenPerSec, 0.000001f);
                var sharing = Mathf.Max(1, Tapping(root));
                float next;
                if (root.CanDrain(1f))
                    next = Mathf.Max(0f, unit - (zdo.GetFloat(ZDOVars.s_product) + Mathf.Max(0f, since)));
                else // the root is dry: wait until it has regrown enough, then the extractor's own timer runs on
                    next = (1.01f - rootLevel) / regen + Mathf.Max(0f, unit - zdo.GetFloat(ZDOVars.s_product));
                var dry = !root.CanDrain(1f);
                text = $"\nNext sap in <color=yellow>{(dry ? "~" : "")}{Format(next)}</color>{(dry ? " (the root is regrowing)" : "")}";

                // Until full: sap still in the root comes at the extractor's own pace (shared with the others on
                // this root), the rest at the speed the root regrows, shared too.
                var needed = max - level;
                var stock = Mathf.Max(0, Mathf.FloorToInt(rootLevel) - 1) / sharing;
                var fast = Mathf.Min(needed, stock);
                var slow = Mathf.Max(unit, sharing / regen);
                var full = (fast > 0 ? next + (fast - 1) * unit + (needed - fast) * slow : next + (needed - 1) * slow);
                if (needed > 1) text += $"\nFull in <color=yellow>~{Format(full)}</color>";
            }
            text += $"\nRoot: {Mathf.FloorToInt(rootLevel)} / {root.m_maxLevel:0} sap";
            __result = Insert(__result, text);
        }

        // --- Helpers -------------------------------------------------------------------------------------------

        // Puts the info right after the first line (name and status), before any key hints.
        private static string Insert(string hover, string text)
        {
            if (string.IsNullOrEmpty(hover)) return text.TrimStart('\n');
            var nl = hover.IndexOf('\n');
            return nl < 0 ? hover + text : hover.Substring(0, nl) + text + hover.Substring(nl);
        }

        private static string Format(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Ceiling(Math.Max(0, seconds)));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }
    }
}
