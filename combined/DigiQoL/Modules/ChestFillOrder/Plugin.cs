// Chest Fill Order: what you put into a chest (or a cart, ship, backpack or tombstone: any storage) goes into the first
// free slot from the top left, row by row, instead of the game's bottom row first (the game puts only weapons, tools
// and a few more at the top). Topping up a stack of the same item still comes first, as always. Your own inventory
// keeps the game's order (new items stay out of the hotbar row).
// Store Nearby uses the game's own way of adding, so it fills from the top left too.
using System;
using HarmonyLib;

namespace ChestFillOrder
{
    public class Plugin : DigiCore.DigiModule
    {
        private void Awake()
        {
            PatchModule(typeof(Plugin));
        }

        private static bool IsStorage(Inventory inventory)
        {
            var player = Player.m_localPlayer;
            return inventory != null && (player == null || inventory != player.GetInventory());
        }

        // The game: every item into a storage counts as "top first".
        [HarmonyPatch(typeof(Inventory), "TopFirst")]
        [HarmonyPostfix]
        private static void TopLeft(Inventory __instance, ref bool __result)
        {
            if (!__result && IsStorage(__instance)) __result = true;
        }
    }
}
