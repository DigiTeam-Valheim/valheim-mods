// The game's tooltip of an effect (meads, potions, food, set bonuses, ...) leaves out some of its numbers: the damage
// bonus (for all attacks or one weapon skill), skill XP bonuses, health per tick (healing or damage over time),
// stamina drain and the wind modifiers. Mods use those too: HoneyPlus' Aesir mead only says "Boosts all damage." while
// it is +10% for every attack. This adds the missing lines in the game's own style, from the effect's real values,
// and writes the duration as minutes and seconds. Weapon tooltips get a total under their damage types.
// Light: only runs when a tooltip is built.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace EffectDetails
{
    public class Plugin : DigiCore.DigiModule
    {
        private static ConfigEntry<bool> _hidden, _minutes, _damageTotal;

        private void Awake()
        {
            _hidden = Config.Bind("General", "Show hidden values", true,
                "Effect tooltips also show the values the game leaves out: damage bonus (all attacks or one weapon " +
                "skill), skill XP bonus, health or damage per tick, stamina drain per second and wind modifiers. " +
                "Example: Aesir mead shows \"All damage: +10%\".");
            _minutes = Config.Bind("General", "Duration in minutes", true,
                "Effect durations show minutes and seconds, e.g. \"300 (5:00)\" instead of only \"300\".");
            _damageTotal = Config.Bind("General", "Damage total", true,
                "Weapon and ammo tooltips with two or more damage types show their sum under them, e.g. " +
                "\"Total: 255 (82-158)\" (with your skill, like the lines above it).");
            PatchModule(typeof(Plugin));
        }

        // The game and Epic Loot both write a line per damage type: "$inventory_fire: <color=orange>73</color>
        // <color=yellow>(23-45) </color>" (the number, then the range with your skill). The sum of the shown lines goes
        // under the last one. Last of all postfixes, so Epic Loot's own tooltip for magic items is there.
        private static readonly Regex DamageLine = new Regex(
            @"\$inventory_(?:damage|blunt|slash|pierce|fire|frost|lightning|poison|spirit): <color=[^>]*>(-?\d+)</color> <color=[^>]*>\((-?\d+)-(-?\d+)\) ?</color>",
            RegexOptions.Compiled);

        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip), typeof(ItemDrop.ItemData), typeof(int), typeof(bool), typeof(float), typeof(int), typeof(bool))]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void DamageTotal(ref string __result)
        {
            if (!_damageTotal.Value || __result == null || __result.IndexOf("$inventory_", System.StringComparison.Ordinal) < 0) return;
            var lines = DamageLine.Matches(__result);
            if (lines.Count < 2) return;
            int total = 0, min = 0, max = 0;
            foreach (Match line in lines)
            {
                total += int.Parse(line.Groups[1].Value, CultureInfo.InvariantCulture);
                min += int.Parse(line.Groups[2].Value, CultureInfo.InvariantCulture);
                max += int.Parse(line.Groups[3].Value, CultureInfo.InvariantCulture);
            }
            var last = lines[lines.Count - 1];
            __result = __result.Insert(last.Index + last.Length,
                $"\nTotal: <color=orange>{total}</color> <color=yellow>({min}-{max})</color>");
        }

        [HarmonyPatch(typeof(SE_Stats), nameof(SE_Stats.GetTooltipString))]
        [HarmonyPostfix]
        private static void Tooltip(SE_Stats __instance, ref string __result)
        {
            if (__result == null) return;
            // The game ends with "$se_ttl: <color=orange>300</color>" (Effect duration); the extra lines go above it.
            var duration = $"$se_ttl: <color=orange>{__instance.m_ttl}</color>";
            var at = __result.IndexOf(duration, System.StringComparison.Ordinal);
            if (at >= 0 && _minutes.Value && __instance.m_ttl >= 60f)
            {
                var time = System.TimeSpan.FromSeconds(__instance.m_ttl);
                var clock = time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{time.Minutes}:{time.Seconds:00}";
                __result = __result.Substring(0, at) + $"$se_ttl: <color=orange>{__instance.m_ttl} ({clock})</color>" +
                           __result.Substring(at + duration.Length);
            }
            if (!_hidden.Value) return;
            var se = __instance;
            var extra = new StringBuilder();
            if (se.m_modifyAttackSkill != Skills.SkillType.None && Mathf.Abs(se.m_damageModifier - 1f) > 0.001f)
                Line(extra, se.m_modifyAttackSkill == Skills.SkillType.All ? "All damage" : SkillName(se.m_modifyAttackSkill) + " damage",
                    Percent(se.m_damageModifier - 1f));
            if (se.m_raiseSkill != Skills.SkillType.None && Mathf.Abs(se.m_raiseSkillModifier) > 0.001f)
                Line(extra, se.m_raiseSkill == Skills.SkillType.All ? "All skills XP" : SkillName(se.m_raiseSkill) + " XP",
                    Percent(se.m_raiseSkillModifier));
            if (se.m_tickInterval > 0f && Mathf.Abs(se.m_healthPerTick) > 0.001f)
            {
                var every = se.m_tickInterval.ToString("0.#", CultureInfo.InvariantCulture);
                if (se.m_healthPerTick > 0f) Line(extra, $"Health every {every}s", "+" + Number(se.m_healthPerTick));
                else Line(extra, $"Damage every {every}s", Number(-se.m_healthPerTick));
            }
            if (se.m_staminaDrainPerSec > 0f)
                Line(extra, "Stamina drain", Number(se.m_staminaDrainPerSec) + "/s");
            if (Mathf.Abs(se.m_windMovementModifier) > 0.001f)
                Line(extra, "Speed with the wind", Percent(se.m_windMovementModifier));
            if (Mathf.Abs(se.m_windRunStaminaModifier) > 0.001f)
                Line(extra, "Run stamina with the wind", Percent(se.m_windRunStaminaModifier));
            if (extra.Length == 0) return;
            var ttlLine = __result.IndexOf("$se_ttl:", System.StringComparison.Ordinal);
            if (ttlLine >= 0)
            {
                __result = __result.Insert(ttlLine, extra.ToString());
                return;
            }
            if (__result.Length > 0 && !__result.EndsWith("\n")) __result += "\n";
            __result += extra.ToString();
        }

        private static void Line(StringBuilder sb, string label, string value) =>
            sb.Append(label).Append(": <color=orange>").Append(value).Append("</color>\n");

        private static string Percent(float fraction) => (fraction * 100f).ToString("+0.#;-0.#", CultureInfo.InvariantCulture) + "%";

        private static string Number(float value) => value.ToString("0.#", CultureInfo.InvariantCulture);

        private static string SkillName(Skills.SkillType skill) =>
            Localization.instance.Localize("$skill_" + skill.ToString().ToLowerInvariant());
    }
}
