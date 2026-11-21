// Epic Loot Fixes, extra stars: Epic Loot puts 1-3 coloured stars after an effect by how rarely it rolls
// (MagicEffectRarity.GetTier: the effect's SelectionWeight against the median of all effects). Double Magic Shot rolls
// about as often as most effects (weight 2), so it had none, though it doubles a staff's shots. Here an effect gets at
// least the stars set for it (default: Double Magic Shot 2) wherever Epic Loot draws stars, as all of it asks GetTier:
// tooltips, the enchanting and augment windows, the reveal after enchanting, and Sort effects' order. Only the look: how
// often an effect rolls goes by its weight, which stays as it is. (The user's wish, 2026-11-20.)
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using EpicLoot;
using HarmonyLib;

namespace EpicLootFixes
{
    internal static class ExtraStars
    {
        private static ConfigEntry<string> _stars;
        private static readonly Dictionary<string, int> Stars = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        internal static void Bind(DigiCore.ModuleConfig config)
        {
            _stars = config.Bind("Tooltip", "Extra stars", "DoubleMagicShot:2",
                "Effects shown with at least this many stars (1 to 3), as EffectType:stars, separated by commas (the types are " +
                "the names in Epic Loot's magiceffects.json). Only the look: how often an effect rolls stays the same. Empty: " +
                "Epic Loot's own stars only.");
            _stars.SettingChanged += (_, __) => Read();
            Read();
        }

        private static void Read()
        {
            Stars.Clear();
            foreach (var part in (_stars.Value ?? "").Split(','))
            {
                var pair = part.Split(':');
                if (pair.Length == 2 && pair[0].Trim().Length > 0 && int.TryParse(pair[1].Trim(), out var n) && n > 0)
                    Stars[pair[0].Trim()] = Math.Min(3, n);
            }
        }

        internal static void Patch(Harmony harmony, BepInEx.Logging.ManualLogSource log)
        {
            var getTier = AccessTools.Method(typeof(MagicEffectRarity), "GetTier", new[] { typeof(MagicItemEffectDefinition) });
            if (getTier == null)
            {
                log.LogWarning("Extra stars: this Epic Loot version has no MagicEffectRarity.GetTier, so stars stay as Epic Loot draws them.");
                return;
            }
            harmony.Patch(getTier, postfix: new HarmonyMethod(typeof(ExtraStars), nameof(AtLeast)));
        }

        private static void AtLeast(MagicItemEffectDefinition effectDef, ref int __result)
        {
            if (effectDef?.Type == null || Stars.Count == 0 || !Stars.TryGetValue(effectDef.Type, out var min)) return;
            if (__result < min) __result = min;
        }
    }
}
