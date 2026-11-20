// Epic Loot Fixes, sorted effects: tooltips list an item's effects in the order they were rolled, and an augmented one
// stays where the old effect was. Here they are shown with the most stars first, then by name. Only the tooltip changes:
// the item keeps its own order, as augmenting goes by the place in it (the augment window lists them that way too).
// Epic Loot 0.15.2 and later gather a tooltip's effects into a list first (TooltipGatherer.GatherEffects, used only for
// tooltips) and then print it; this sorts that list. The list is gathered each time a tooltip is made, so it follows an
// augment at once.
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using EpicLoot;
using HarmonyLib;

namespace EpicLootFixes
{
    internal static class SortedEffects
    {
        private static readonly Regex Tags = new Regex("<[^>]*>");
        private static ConfigEntry<bool> _enabled;

        internal static void Bind(DigiCore.ModuleConfig config)
        {
            _enabled = config.Bind("Tooltip", "Sort effects", true,
                "Item tooltips show the magic effects with the most stars first, then by name. Only the tooltip: the item itself keeps " +
                "its order, and the augment window its list.");
        }

        internal static void Patch(Harmony harmony, BepInEx.Logging.ManualLogSource log)
        {
            var gather = AccessTools.Method(AccessTools.TypeByName("EpicLoot.TooltipGatherer"), "GatherEffects");
            if (gather == null)
            {
                log.LogWarning("Sort effects: this Epic Loot version has no TooltipGatherer.GatherEffects, so effects stay unsorted.");
                return;
            }
            harmony.Patch(gather, postfix: new HarmonyMethod(typeof(SortedEffects), nameof(Sort)));
        }

        private static void Sort(List<TooltipEffect> __result)
        {
            if (!_enabled.Value || __result == null || __result.Count < 2) return;
            var count = __result.Count;
            var stars = new int[count];
            var names = new string[count];
            var order = new int[count];
            for (var i = 0; i < count; i++)
            {
                order[i] = i;
                MagicItemEffectDefinitions.AllDefinitions.TryGetValue(__result[i].EffectType, out var definition);
                stars[i] = MagicEffectRarity.GetTier(definition);
                names[i] = Tags.Replace(Localization.instance.Localize(__result[i].Text ?? ""), "");
            }
            Array.Sort(order, (a, b) =>
            {
                var result = stars[b].CompareTo(stars[a]);
                if (result == 0) result = string.Compare(names[a], names[b], StringComparison.OrdinalIgnoreCase);
                return result != 0 ? result : a.CompareTo(b);
            });
            var sorted = new List<TooltipEffect>(count);
            foreach (var i in order) sorted.Add(__result[i]);
            __result.Clear();
            __result.AddRange(sorted);
        }
    }
}
