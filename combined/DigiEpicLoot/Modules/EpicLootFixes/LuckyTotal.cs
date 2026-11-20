// Epic Loot Fixes, Lucky Loot total: Epic Loot adds up the Lucky Loot of every shard (and set bonus) you wear, and a kill
// uses that total: chance = the total in %, drops x (1 + half the total), at most its cap (10x by default). Each item's
// tooltip only shows its own shard ("12% Chance of 7x Drops"), so with two shards nobody could tell they make 10x.
// With more than one Lucky Loot source worn, the tooltip of a worn item that has Lucky Loot gets one more line: what they
// add up to (chance, drop multiplier, extra magic item rolls). The numbers come from Epic Loot itself, the same a kill uses.
// Light: only while such a tooltip is made.
using System;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using EpicLoot;
using HarmonyLib;
using UnityEngine;

namespace EpicLootFixes
{
    internal static class LuckyTotal
    {
        private static ConfigEntry<bool> _enabled;
        private static MethodInfo _multiplier, _maxMultiplier, _rollsMin, _rollsMax;

        internal static void Bind(DigiCore.ModuleConfig config)
        {
            _enabled = config.Bind("Lucky Loot", "Lucky Loot total in tooltip", true,
                "With more than one Lucky Loot shard worn, the tooltip of a worn item with Lucky Loot also shows what they add up " +
                "to: the chance, the drop multiplier (Epic Loot caps it at 10x) and the extra magic item rolls. Each shard's own " +
                "line shows only that shard.");
        }

        internal static void Patch(Harmony harmony, BepInEx.Logging.ManualLogSource log)
        {
            var lucky = AccessTools.TypeByName("EpicLoot.MagicItemEffects.Shards.LuckyLoot");
            _multiplier = lucky == null ? null : AccessTools.Method(lucky, "Multiplier", new[] { typeof(float) });
            _maxMultiplier = lucky == null ? null : AccessTools.Method(lucky, "GetMaxMultiplier");
            _rollsMin = lucky == null ? null : AccessTools.Method(lucky, "GetBonusRollsMin");
            _rollsMax = lucky == null ? null : AccessTools.Method(lucky, "GetBonusRollsMax");
            var tooltip = typeof(ItemDrop.ItemData).GetMethods(BindingFlags.Static | BindingFlags.Public)
                .Where(m => m.Name == nameof(ItemDrop.ItemData.GetTooltip) && m.ReturnType == typeof(string) &&
                            m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(ItemDrop.ItemData))
                .OrderByDescending(m => m.GetParameters().Length).FirstOrDefault();
            if (_multiplier == null || _maxMultiplier == null || _rollsMin == null || _rollsMax == null || tooltip == null)
            {
                log.LogWarning("Lucky Loot total in tooltip is off: this Epic Loot version has no Lucky Loot code we know.");
                return;
            }
            // Last: after Epic Loot has written its own magic tooltip.
            harmony.Patch(tooltip, postfix: new HarmonyMethod(typeof(LuckyTotal), nameof(AddTotal)) { priority = Priority.Last });
        }

        private static void AddTotal(ItemDrop.ItemData __0, ref string __result)
        {
            try
            {
                var item = __0;
                if (!_enabled.Value || item == null || __result == null || !item.m_equipped || !item.IsMagic()) return;
                var player = Player.m_localPlayer;
                if (player == null || !player.IsItemEquiped(item)) return;
                var own = item.GetMagicItem()?.GetEffects(MagicEffectType.LuckyLoot, true);
                if (own == null || own.Count == 0) return;
                var sources = player.GetAllActiveMagicEffects(MagicEffectType.LuckyLoot).Count;
                if (sources < 2) return;
                var value = player.GetTotalActiveMagicEffectValue(MagicEffectType.LuckyLoot);
                if (value <= 0f) return;
                var multiplier = (int)_multiplier.Invoke(null, new object[] { value });
                var capped = multiplier >= (int)_maxMultiplier.Invoke(null, null);
                var min = Mathf.RoundToInt((float)_rollsMin.Invoke(null, null));
                var max = Mathf.RoundToInt((float)_rollsMax.Invoke(null, null));
                __result += $"\n\n<color=#FFD700>All your Lucky Loot ({sources} shards): {Mathf.Min(100f, value):0.#}% chance of " +
                            $"{multiplier}x drops{(capped ? " (max)" : "")}, {min}-{max} extra magic rolls</color>";
            }
            catch
            {
                // a tooltip never fails because of this line
            }
        }
    }
}
