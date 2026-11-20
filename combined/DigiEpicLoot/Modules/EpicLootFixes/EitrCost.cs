// Epic Loot Fixes, Reduce Eitr Cost (the shard, on a cape, say): Epic Loot takes its share off every eitr payment, from
// all worn gear together (Player.UseEitr, local player only). Two things did not know about it:
// - Tooltip: a staff's "Use eitr" line only counted the staff's own enchantments. It gets what a cast costs with the
//   gear you wear: "Use eitr: 35.2 (Total:16.7)". The item's own value counts too while it is not worn. Like the line
//   itself, without the skill's discount (up to a third at 100).
// - Cast check: before a cast (or any eitr use) the game checks you have the full cost, then only takes the reduced
//   one. Player.HaveEitr now checks the reduced cost, with the same rule as Epic Loot's payment, so every check and its
//   payment match.
// Light: a string search on tooltips that have an eitr line, and a cached lookup per eitr check.
using System.Globalization;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using EpicLoot;
using HarmonyLib;

namespace EpicLootFixes
{
    internal static class EitrCost
    {
        private const string Section = "Reduce Eitr Cost";
        private static ConfigEntry<bool> _tooltip, _check;
        private static readonly Regex Line = new Regex(@"\n\$item_eitruse: <color=[^>]+>([^<]+)</color>", RegexOptions.Compiled);

        internal static void Bind(DigiCore.ModuleConfig config)
        {
            _tooltip = config.Bind(Section, "Show cost with your gear", true,
                "A staff's eitr cost also shows what a cast costs with the Reduce Eitr Cost shards you wear, e.g. " +
                "\"Use eitr: 35.2 (Total:16.7)\". Epic Loot takes it off when you pay, but the line only counted the " +
                "staff's own enchantments.");
            _check = config.Bind(Section, "Check the reduced cost", true,
                "Casting needs only as much eitr as it really takes. Epic Loot checked the full cost first, so with " +
                "-50% you needed 30 eitr for a cast that took 15.");
        }

        private static float Reduction(Player player) =>
            player.GetTotalActiveMagicEffectValue(MagicEffectType.ReduceEitrCost, 0.01f);

        [HarmonyPatch(typeof(Player), nameof(Player.HaveEitr))]
        [HarmonyPrefix]
        private static void ReducedCheck(Player __instance, ref float amount)
        {
            if (!_check.Value || amount <= 0f || __instance != Player.m_localPlayer) return;
            var reduce = Reduction(__instance);
            if (reduce > 0f) amount *= System.Math.Max(0f, 1f - reduce);
        }

        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip), new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(bool), typeof(float), typeof(int), typeof(bool) })]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void AddTotal(ref string __result, ItemDrop.ItemData item)
        {
            var player = Player.m_localPlayer;
            if (!_tooltip.Value || player == null || item == null || string.IsNullOrEmpty(__result)) return;
            var reduce = Reduction(player);
            var magic = item.GetMagicItem();
            if (magic != null && !player.IsItemEquiped(item)) reduce += magic.GetTotalEffectValue(MagicEffectType.ReduceEitrCost, 0.01f);
            if (reduce <= 0f) return;
            var match = Line.Match(__result);
            if (!match.Success || !float.TryParse(match.Groups[1].Value.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var cost)) return;
            var pay = cost * System.Math.Max(0f, 1f - reduce);
            __result = __result.Insert(match.Index + match.Length, $" ($item_total:<color=#add8e6ff>{pay:0.#}</color>)");
        }
    }
}
