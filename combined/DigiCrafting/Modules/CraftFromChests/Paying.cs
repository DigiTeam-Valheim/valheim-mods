// Paying: the game's own payment (Player.ConsumeResources) removes each material from the bag by name; what the bag lacks
// is taken from the chests, nearest first. Only inside a craft or a placement of the local player, and only from chests
// that are ours (multiplayer): the game's own check right before paying then counts only those (Linking.ReadyOnly), in
// the same frame, so a craft or a piece is never made without being paid for in full.
// Multiplayer, so that waiting is rare: when Craft is pressed, the owners of the chests that will be needed are asked
// at once, and the craft's timer waits for their answers (at most 3 s); while a build piece is selected, the chests for it
// are asked for a few times a second. A chest that cannot be had (someone has it open) gives a short message.
using System.Collections.Generic;
using DigiStorage;
using HarmonyLib;
using UnityEngine;

namespace CraftFromChests
{
    internal static class Paying
    {
        private static bool _crafting, _placing, _checkingAll, _holdCraft;
        private static float _holdSince, _nextAsk;
        private static int _paying;
        private static readonly Stack<bool> Payments = new Stack<bool>();

        internal static void Reset()
        {
            _crafting = _placing = _checkingAll = _holdCraft = false;
            _paying = 0;
            Payments.Clear();
        }

        private static bool CraftOn => Plugin.Crafting.Value && !Linking.Recycling;

        private static int Quality(InventoryGui gui) => gui.m_craftUpgradeItem == null ? 1 : gui.m_craftUpgradeItem.m_quality + 1;

        private static int Multiplier(InventoryGui gui) => gui.m_multiCrafting ? gui.m_multiCraftAmount : 1;

        // ------------------------------------------------------------------ crafting

        [HarmonyPatch(typeof(InventoryGui), "OnCraftPressed")]
        [HarmonyPostfix]
        private static void CraftStarted(InventoryGui __instance)
        {
            _holdCraft = false;
            if (!CraftOn || __instance.m_craftTimer != 0f || __instance.m_craftRecipe == null || Player.m_localPlayer == null) return;
            if (AskFor(__instance.m_craftRecipe.m_resources, Quality(__instance), Multiplier(__instance), true))
            {
                _holdCraft = true;
                _holdSince = Time.unscaledTime;
            }
        }

        // While an answer is on its way, the craft's timer stays near its start (at most 3 seconds).
        [HarmonyPatch(typeof(InventoryGui), "UpdateRecipe")]
        [HarmonyPrefix]
        private static void HoldCraft(InventoryGui __instance)
        {
            if (!_holdCraft) return;
            if (__instance.m_craftTimer < 0f || !Chests.Waiting() || Time.unscaledTime - _holdSince > 3f)
            {
                _holdCraft = false;
                return;
            }
            if (__instance.m_craftTimer > 0.25f) __instance.m_craftTimer = 0.25f;
        }

        [HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
        [HarmonyPrefix]
        private static void CraftPays(InventoryGui __instance, Player player)
        {
            _crafting = CraftOn && player != null && player == Player.m_localPlayer;
            if (!_crafting) return;
            Linking.ReadyOnly = true;
            var recipe = __instance.m_craftRecipe;
            if (recipe == null || player.NoCostCheat() || ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoCraftCost)) return;
            // Next comes the game's own check, which now counts only the chests that can pay. When the others would have
            // been enough, it says why nothing is made.
            var quality = Quality(__instance);
            var amount = Multiplier(__instance);
            if (player.HaveRequirements(recipe, false, quality, amount)) return;
            Linking.ReadyOnly = false;
            var anywhere = player.HaveRequirements(recipe, false, quality, amount);
            Linking.ReadyOnly = true;
            if (!anywhere) return;
            AskFor(recipe.m_resources, quality, amount, true);
            Plugin.Say("A chest with the materials is in use: try again");
        }

        [HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
        [HarmonyFinalizer]
        private static void CraftDone()
        {
            if (!_crafting) return;
            _crafting = false;
            Linking.ReadyOnly = false;
        }

        // ------------------------------------------------------------------ placing

        [HarmonyPatch(typeof(Player), "UpdatePlacement")]
        [HarmonyPrefix]
        private static void PlacementStart(Player __instance, bool takeInput)
        {
            // Free building (the game's no-cost mode, Ultimate Builder's creative world) needs nothing from chests: without
            // this the build check below said "try again" over and over.
            _placing = Plugin.Building.Value && takeInput && __instance == Player.m_localPlayer && __instance.InPlaceMode() &&
                       !__instance.m_noPlacementCost;
            if (_placing) Linking.ReadyOnly = true;
        }

        [HarmonyPatch(typeof(Player), "UpdatePlacement")]
        [HarmonyPostfix]
        private static void AskForPiece(Player __instance)
        {
            if (!_placing || Time.unscaledTime < _nextAsk) return;
            _nextAsk = Time.unscaledTime + 0.3f;
            var piece = __instance.GetSelectedPiece();
            if (piece == null || piece.m_repairPiece || piece.m_removePiece || __instance.m_noPlacementCost) return;
            if (ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey())) return;
            AskFor(piece.m_resources, 0, 1, false);
        }

        [HarmonyPatch(typeof(Player), "UpdatePlacement")]
        [HarmonyFinalizer]
        private static void PlacementDone()
        {
            if (!_placing) return;
            _placing = false;
            Linking.ReadyOnly = false;
        }

        // A click that cannot place because a chest is not ours yet: ask for it and say so (instead of "missing").
        [HarmonyPatch(typeof(Player), nameof(Player.HaveRequirements), typeof(Piece), typeof(Player.RequirementMode))]
        [HarmonyPostfix]
        private static void PieceChecked(Player __instance, Piece piece, Player.RequirementMode mode, bool __result)
        {
            if (__result || !_placing || _checkingAll || mode != Player.RequirementMode.CanBuild || __instance != Player.m_localPlayer) return;
            _checkingAll = true;
            Linking.ReadyOnly = false;
            try
            {
                if (__instance.HaveRequirements(piece, mode))
                {
                    AskFor(piece.m_resources, 0, 1, false);
                    Plugin.Say("Getting the materials from a chest: try again");
                }
            }
            finally
            {
                Linking.ReadyOnly = true;
                _checkingAll = false;
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.Message))]
        [HarmonyPrefix]
        private static void Message(Player __instance, ref string msg)
        {
            if (__instance == Player.m_localPlayer) Plugin.Replaces(ref msg);
        }

        // ------------------------------------------------------------------ the payment

        [HarmonyPatch(typeof(Player), nameof(Player.ConsumeResources))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        private static void PayStart(Player __instance)
        {
            var on = (_crafting || _placing) && __instance == Player.m_localPlayer;
            Payments.Push(on);
            if (on) _paying++;
        }

        [HarmonyPatch(typeof(Player), nameof(Player.ConsumeResources))]
        [HarmonyFinalizer]
        private static void PayDone()
        {
            if (Payments.Count > 0 && Payments.Pop()) _paying--;
        }

        // The game removes a material from the bag (after Adventure Backpacks took its share from the backpack): what the
        // bag itself lacks comes out of the chests first.
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem), typeof(string), typeof(int), typeof(int), typeof(bool))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Low)]
        private static void Remove(Inventory __instance, string name, ref int amount, int itemQuality, bool worldLevelBased)
        {
            if ((_paying <= 0 && Filling.Depth <= 0) || amount <= 0) return;
            var player = Player.m_localPlayer;
            if (player == null || __instance != player.m_inventory) return;
            var own = Plugin.Own(__instance, name, itemQuality, worldLevelBased);
            if (own >= amount) return;
            amount -= Chests.Take(Plugin.Here(), name, amount - own, itemQuality, worldLevelBased);
        }

        // ------------------------------------------------------------------ asking for chests

        // Asks the owners of the chests that hold what the bag lacks for these requirements. True if an answer is now on
        // its way (never in single player: every chest is ours).
        internal static bool AskFor(Piece.Requirement[] resources, int quality, int multiplier, bool craft)
        {
            var player = Player.m_localPlayer;
            if (player == null || resources == null) return false;
            var station = craft ? player.GetCurrentCraftingStation() : null;
            var area = Plugin.Here();
            var asked = false;
            foreach (var req in resources)
            {
                if (req?.m_resItem == null) continue;
                if (craft && ((station != null && station.m_upgrader != req.m_upgraderResource) || (station == null && req.m_upgraderResource))) continue;
                var need = (craft ? req.GetAmount(quality) : req.m_amount) * multiplier;
                if (need <= 0) continue;
                var name = req.m_resItem.m_itemData.m_shared.m_name;
                var missing = need - Plugin.Own(player.m_inventory, name);
                if (missing > 0 && Chests.Ask(area, name, missing)) asked = true;
            }
            return asked;
        }
    }
}
