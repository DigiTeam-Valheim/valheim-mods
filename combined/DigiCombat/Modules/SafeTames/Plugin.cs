// Safe Tames: tamed animals (wolves, boars, lox, hens, asksvin, ... and summons) take no damage at all from players or
// from other tamed creatures: no stray staff blast, bomb, fire, frost or lightning, no exploding arrow, also not with PvP
// on. The game itself already keeps your melee swings and plain arrows off them while PvP is off; area damage and PvP hits
// still went through. The Butcher Knife still works: it is made to slaughter them.
// Blocked where a hit starts (Character.Damage, on the attacking player's side), so nothing reaches the animal: no damage,
// no stagger, no burning or poison afterwards. Light: one check per hit on a tamed creature.
using System.Collections.Generic;
using HarmonyLib;

namespace SafeTames
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.safetames";
        public const string Name = "Digi's Safe Tames";
        public const string Version = "1.0.0";

        private static readonly AccessTools.FieldRef<Attack, ItemDrop.ItemData> Weapon = AccessTools.FieldRefAccess<Attack, ItemDrop.ItemData>("m_weapon");
        private static readonly Stack<bool> Swings = new Stack<bool>();
        private static int _butcher; // > 0: inside a swing of a weapon made for tamed animals only (the Butcher Knife)

        private void Awake() => PatchModule(typeof(Plugin));

        private void OnDestroy()
        {
            Swings.Clear();
            _butcher = 0;
        }

        [HarmonyPatch(typeof(Attack), "DoMeleeAttack")]
        [HarmonyPrefix]
        private static void SwingStart(Attack __instance)
        {
            var weapon = Weapon(__instance);
            var butcher = weapon?.m_shared != null && weapon.m_shared.m_tamedOnly;
            Swings.Push(butcher);
            if (butcher) _butcher++;
        }

        [HarmonyPatch(typeof(Attack), "DoMeleeAttack")]
        [HarmonyFinalizer]
        private static void SwingEnd()
        {
            if (Swings.Count > 0 && Swings.Pop()) _butcher--;
        }

        [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
        [HarmonyPrefix]
        private static bool Hit(Character __instance, HitData hit)
        {
            if (hit == null || __instance == null || __instance.IsPlayer() || !__instance.IsTamed()) return true;
            var attacker = hit.GetAttacker();
            if (attacker == null || attacker == __instance || (!attacker.IsPlayer() && !attacker.IsTamed())) return true;
            return _butcher > 0 && attacker.IsPlayer(); // only the Butcher Knife gets through
        }
    }
}
