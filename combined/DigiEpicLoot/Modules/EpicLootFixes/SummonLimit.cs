// Epic Loot Fixes, summon limit: a summoning staff's spawn (the Staff of the Wild's roots) stops at a fixed number of
// summons around (10 roots; the skill-based "max instances" the game also stores is only used for summons that follow
// you, which roots do not). Double Magic Shot fires two of them per cast, so it reaches that limit twice as fast. On:
// with Double Magic Shot on the staff the limit is doubled. Set by the host, the same for everyone.
// Light: one check each time a staff's summon spawns.
using BepInEx.Configuration;
using EpicLoot;
using HarmonyLib;

namespace EpicLootFixes
{
    internal static class SummonLimit
    {
        private static ConfigEntry<bool> _double;

        internal static void Bind(DigiCore.ModuleConfig config)
        {
            _double = config.Bind("Double and Triple Shot", "Double Magic Shot doubles the summon limit", true, new ConfigDescription(
                "A summoning staff (the Staff of the Wild's roots) allows 10 summons around at once. Double Magic Shot fires " +
                "two per cast, so it hits that limit twice as fast. On: with Double Magic Shot on the staff the limit is 20. " +
                "Set by the host, the same for everyone.", null, new ConfigurationManagerAttributes { IsAdminOnly = true }));
        }

        // Before the summon spawns: the weapon that fired it is known here.
        [HarmonyPatch(typeof(SpawnAbility), nameof(SpawnAbility.Setup))]
        [HarmonyPrefix]
        private static void Setup(SpawnAbility __instance, ItemDrop.ItemData item)
        {
            if (_double.Value && item != null && __instance.m_maxSpawned > 0 && item.HasMagicEffect("DoubleMagicShot"))
                __instance.m_maxSpawned *= 2;
        }
    }
}
