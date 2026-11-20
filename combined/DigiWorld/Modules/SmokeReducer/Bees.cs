// Smoke Reducer, beehives: the swarm of little bees around a beehive is not shown (setting, on by default). The game turns
// it on and off (daytime, room for honey) every few seconds; it is switched off again right after. The hive makes honey
// exactly as before. Switching the setting shows the swarms again at once.
// Other mods' hives are built pieces with bee particles of their own, not the game's beehive (HoneyPlus's apiary:
// "BeeEffect/bees", its crystal extension: "magic bees"): on a piece named like an apiary or beehive, every particle
// effect with "bees" in its name goes too (only a name check per piece as it loads).
using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SmokeReducer
{
    internal static class Bees
    {
        private static ConfigEntry<bool> _hide;

        internal static void Bind(DigiCore.ModuleConfig config)
        {
            _hide = config.Bind("Bees", "Hide beehive bees", true,
                "The swarm of little bees flying around a beehive is not shown, also around other mods' hives (HoneyPlus's apiary and its crystal). The hive makes honey as before.");
            _hide.SettingChanged += (_, __) => ApplyAll();
        }

        [HarmonyPatch(typeof(Beehive), "UpdateBees")]
        [HarmonyPostfix]
        private static void AfterUpdate(Beehive __instance)
        {
            if (_hide.Value && __instance.m_beeEffect != null) __instance.m_beeEffect.SetActive(false);
        }

        [HarmonyPatch(typeof(WearNTear), "Awake")]
        [HarmonyPostfix]
        private static void PieceAwake(WearNTear __instance)
        {
            if (_hide.Value) ModBees(__instance, false);
        }

        // Another mod's hive: its bee particles on or off.
        private static void ModBees(Component piece, bool on)
        {
            if (piece == null) return;
            var name = piece.name;
            if (name.IndexOf("apiary", StringComparison.OrdinalIgnoreCase) < 0 && name.IndexOf("bee", StringComparison.OrdinalIgnoreCase) < 0) return;
            foreach (var particles in piece.GetComponentsInChildren<ParticleSystem>(true))
                if (particles.name.IndexOf("bees", StringComparison.OrdinalIgnoreCase) >= 0 && particles.gameObject.activeSelf != on)
                    particles.gameObject.SetActive(on);
        }

        private static void AllModBees(bool on)
        {
            foreach (var piece in WearNTear.GetAllInstances()) ModBees(piece, on);
        }

        // Setting changed (or switched on): hidden at once, or the game decides again for every loaded hive.
        internal static void ApplyAll()
        {
            AllModBees(!_hide.Value);
            foreach (var hive in UnityEngine.Object.FindObjectsByType<Beehive>(FindObjectsSortMode.None))
            {
                if (hive == null || hive.m_beeEffect == null) continue;
                if (_hide.Value) hive.m_beeEffect.SetActive(false);
                else if (hive.m_nview != null && hive.m_nview.IsValid()) hive.UpdateBees();
            }
        }

        internal static void Restore()
        {
            if (_hide == null || !_hide.Value) return;
            AllModBees(true);
            foreach (var hive in UnityEngine.Object.FindObjectsByType<Beehive>(FindObjectsSortMode.None))
                if (hive != null && hive.m_beeEffect != null && hive.m_nview != null && hive.m_nview.IsValid()) hive.UpdateBees();
        }
    }
}
