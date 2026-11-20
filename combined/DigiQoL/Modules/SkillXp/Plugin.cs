// Shows each skill's XP in the Skills window: how much it has towards the next level and how much that level needs,
// e.g. "24  12/63" for Swords 24. The hover tooltip of a skill gets a line with the details too. The numbers come
// from the game's own skill data and level formula, so mods that change XP are shown correctly.
// Light: only runs when the Skills window opens (once per skill).
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace SkillXp
{
    public class Plugin : DigiCore.DigiModule
    {
        public enum Style { Amount, Percent, AmountAndPercent }

        private static ConfigEntry<Style> _style;
        private static ConfigEntry<bool> _tooltip;

        private static readonly AccessTools.FieldRef<SkillsDialog, List<GameObject>> Elements =
            AccessTools.FieldRefAccess<SkillsDialog, List<GameObject>>("m_elements");
        private static readonly Func<Skills.Skill, float> NextLevelXp =
            AccessTools.MethodDelegate<Func<Skills.Skill, float>>(AccessTools.Method(typeof(Skills.Skill), "GetNextLevelRequirement"));

        private void Awake()
        {
            _style = Config.Bind("General", "Show", Style.Amount,
                "What appears after the level in the Skills window. Amount: XP you have / XP the next level needs, e.g. " +
                "\"24  12/63\". Percent: how far you are to the next level, e.g. \"24  19%\". AmountAndPercent: both.");
            _tooltip = Config.Bind("General", "Details in tooltip", true,
                "Hovering a skill also shows its XP in full, e.g. \"XP to level 25: 12.3 / 63 (19%)\".");
            PatchModule(typeof(Plugin));
        }

        // Runs after the game filled in the window (each time it opens).
        [HarmonyPatch(typeof(SkillsDialog), nameof(SkillsDialog.Setup))]
        [HarmonyPostfix]
        private static void Setup(SkillsDialog __instance, Player player)
        {
            if (player == null) return;
            var skills = player.GetSkills().GetSkillList();
            var elements = Elements(__instance);
            for (var i = 0; i < skills.Count && i < elements.Count; i++)
            {
                var skill = skills[i];
                var element = elements[i];
                var level = Mathf.FloorToInt(skill.m_level);
                var max = skill.m_level >= 100f;
                var have = skill.m_accumulator;
                var need = max ? 0f : NextLevelXp(skill);
                var percent = need > 0f ? Mathf.Clamp01(have / need) * 100f : 0f;

                var text = Utils.FindChild(element.transform, "leveltext")?.GetComponent<TMP_Text>();
                if (text != null)
                {
                    var extra = max ? "max" : _style.Value == Style.Percent ? $"{percent:0}%"
                        : _style.Value == Style.AmountAndPercent ? $"{Amount(have, need)} ({percent:0}%)" : Amount(have, need);
                    text.text = $"{level}  <size=70%><color=#FFFFFFB3>{extra}</color></size>";
                }

                if (!_tooltip.Value) continue;
                var tooltip = element.GetComponentInChildren<UITooltip>();
                if (tooltip == null) continue;
                var line = max ? "Maximum level reached."
                    : $"XP to level {level + 1}: {have:0.#} / {need:0.#} ({percent:0}%)";
                tooltip.m_text = skill.m_info.m_description + "\n\n<color=yellow>" + line + "</color>";
            }
        }

        // Small numbers get a decimal (level 1 needs 1.9 XP), bigger ones are shown whole.
        private static string Amount(float have, float need) =>
            need < 10f ? $"{have:0.#}/{need:0.#}" : $"{Mathf.FloorToInt(have)}/{Mathf.RoundToInt(need)}";
    }
}
