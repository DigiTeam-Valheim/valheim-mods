// Epic Loot Fixes, Darting Thoughts limit: the enchantment gives +X% eitr regen and -X/2% max eitr, and the values of
// all worn items add up. Three items (on a helmet, utility and trinket) easily pass 200%: max eitr drops to 0 and the
// extra regen does nothing. The max eitr penalty now stops at a set limit (75% by default); the regen is unchanged.
// Done by replacing Epic Loot's own max eitr postfix (DartingThoughts.ModifyMaxEitr) with the same code plus the limit.
using BepInEx.Configuration;
using EpicLoot;
using HarmonyLib;
using UnityEngine;

namespace EpicLootFixes
{
    internal static class DartingThoughtsCap
    {
        private const float PenaltyRatio = 0.5f; // Epic Loot's MaxEitrPenaltyRatio: -1% max eitr per 2%
        private static ConfigEntry<int> _limit;

        internal static void Bind(DigiCore.ModuleConfig config)
        {
            _limit = config.Bind("Darting Thoughts", "Max eitr penalty limit (%)", 75, new ConfigDescription(
                "Darting Thoughts lowers max eitr by half its value, and worn items add up: 3 items at 213% took all of " +
                "it (max eitr 0). The penalty stops here, so with 75 you always keep a quarter of your max eitr. The " +
                "extra eitr regen is not changed. 100 = no limit (Epic Loot's own).", new AcceptableValueRange<int>(0, 100)));
        }

        internal static void Patch(Harmony harmony, BepInEx.Logging.ManualLogSource log)
        {
            var target = AccessTools.Method(AccessTools.TypeByName("EpicLoot.Magic.MagicItemEffects.DartingThoughts+ModifyMaxEitr"), "Postfix");
            if (target != null)
                harmony.Patch(target, new HarmonyMethod(typeof(DartingThoughtsCap), nameof(Limited)));
            else
                log.LogWarning("Darting Thoughts limit is off: this Epic Loot version has no Darting Thoughts code we know.");
        }

        // Epic Loot's postfix is static with a parameter named "__instance", which Harmony would fill with the (null)
        // instance of the patched method: its arguments are taken by position instead (player, ref eitr).
        private static bool Limited(Player __0, ref float __1)
        {
            if (_limit.Value >= 100) return true;
            if (__0 != null && __0.HasActiveMagicEffect(MagicEffectType.DartingThoughts, out var value, 0.01f))
                __1 *= 1f - Mathf.Clamp(value * PenaltyRatio, 0f, _limit.Value / 100f);
            return false;
        }
    }
}
