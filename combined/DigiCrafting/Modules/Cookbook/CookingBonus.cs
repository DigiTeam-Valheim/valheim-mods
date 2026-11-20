// What Cooking Mastery (our own mod) adds to food at your Cooking skill: read from its settings, so this works
// without a reference to it and simply shows nothing extra when it is not installed or turned off.
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using UnityEngine;

namespace Cookbook
{
    internal sealed class CookingBonus
    {
        private const string Mastery = "digi.valheim.crafting"; // Cooking Mastery is a sub-mod of Crafting and Automation
        private const string Single = "digi.valheim.cookingmastery"; // or a mod of its own (Hexium): sections without its name
        private const string Prefix = "Cooking Mastery - ";

        public float Health = 1f, Stamina = 1f, Eitr = 1f, Healing = 1f, Duration = 1f, Skill;
        public bool Active => Health != 1f || Stamina != 1f || Eitr != 1f || Healing != 1f || Duration != 1f;

        internal static readonly CookingBonus None = new CookingBonus();

        internal static CookingBonus Current(Player player)
        {
            if (player == null || !Plugin.CookingBonusShown.Value) return None;
            ConfigFile config;
            string prefix;
            if (Chainloader.PluginInfos.TryGetValue(Single, out var single) && single.Instance != null)
            {
                config = single.Instance.Config;
                prefix = "";
            }
            else if (Chainloader.PluginInfos.TryGetValue(Mastery, out var info) && info.Instance != null && MasteryRunning(info.Instance))
            {
                config = info.Instance.Config;
                prefix = Prefix;
            }
            else return None;
            if (config.TryGetEntry<bool>(new ConfigDefinition(prefix + "1 - General", "Enabled"), out var enabled) && !enabled.Value) return None;
            var skill = player.GetSkillLevel(Skills.SkillType.Cooking);
            var t = Mathf.Clamp01(skill / 100f);
            return new CookingBonus
            {
                Skill = skill,
                Health = Factor(config, prefix, "Health", t),
                Stamina = Factor(config, prefix, "Stamina", t),
                Eitr = Factor(config, prefix, "Eitr", t),
                Healing = Factor(config, prefix, "Healing per tick", t),
                Duration = Factor(config, prefix, "Duration", t),
            };
        }

        // Same as Cooking Mastery: 1 + skill share of (bonus at Cooking 100 - 1).
        private static float Factor(ConfigFile config, string prefix, string name, float t) =>
            config.TryGetEntry<float>(new ConfigDefinition(prefix + "2 - Bonus at Cooking 100", name), out var entry) ? 1f + t * (entry.Value - 1f) : 1f;

        // Whether the combined mod runs its Cooking Mastery (switched on). Asked by name: the combined mod may be another
        // DLL than this one (when the Cookbook is a mod of its own).
        private static bool MasteryRunning(BepInEx.BaseUnityPlugin host)
        {
            var type = host.GetType().Assembly.GetType("CookingMastery.Plugin");
            var isRunning = host.GetType().GetMethod("IsRunning", new[] { typeof(System.Type) });
            return type != null && isRunning != null && (bool)isRunning.Invoke(host, new object[] { type });
        }
    }
}
