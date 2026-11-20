// Valheim Legends' Enchanter Charm lasts a flat 30 s (times the server's vl_svr_enchanterCharm). This makes it grow with
// the caster's Alteration skill: 30 s at 0, 45 s at 50, 60 s at 100 (both ends configurable). The server multiplier
// still applies on top. Only the caster needs this mod: the charm is applied by the caster's game.
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using ValheimLegends;

namespace CharmScaling
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.charmscaling";
        public const string Name = "Digi's Charm Scaling";
        public const string Version = "1.0.2";

        private static ConfigEntry<float> _baseSeconds;
        private static ConfigEntry<float> _maxSeconds;

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
            _baseSeconds = Config.Bind("Charm", "Duration at skill 0 (s)", 30f, "How long Charm lasts, in seconds, at Alteration skill 0.");
            _maxSeconds = Config.Bind("Charm", "Duration at skill 100 (s)", 60f,
                "How long Charm lasts, in seconds, at Alteration skill 100. In between it grows evenly (45 s at skill 50 with the defaults).");
            PatchModule(typeof(Plugin));
        }

        // Legends sets m_ttl = 30 * multiplier right before this call; AddStatusEffect clones it, so changing it here sticks.
        [HarmonyPatch(typeof(SEMan), nameof(SEMan.AddStatusEffect), typeof(StatusEffect), typeof(bool), typeof(int), typeof(float), typeof(short))]
        [HarmonyPrefix]
        private static void AddStatusEffect(StatusEffect statusEffect)
        {
            if (!(statusEffect is SE_Charm charm) || charm.summoner == null) return;
            var skill = charm.summoner.GetSkills().GetSkillList().Find(s => s.m_info == ValheimLegends.ValheimLegends.AlterationSkillDef);
            var level = Mathf.Clamp(skill?.m_level ?? 0f, 0f, 100f);
            var seconds = Mathf.Lerp(_baseSeconds.Value, _maxSeconds.Value, level / 100f);
            // Keep the server multiplier: Legends' value is base 30 s times it.
            charm.m_ttl *= seconds / SE_Charm.m_baseTTL;
        }
    }
}
