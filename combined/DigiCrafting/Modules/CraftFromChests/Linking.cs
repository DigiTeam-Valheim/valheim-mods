// Counting: while the game checks the local player's materials, the bag's counts include the chests around them.
// Entered around the game's own checks only, so nothing else (Store Nearby, other mods, the bag's own window) ever sees
// a changed number:
// - Player.HaveRequirementItems: recipe list, craft button, upgrade list (crafting).
// - Player.HaveRequirements(Piece): build menu icons, placing a piece (building).
// - InventoryGui.SetupRequirement: the amounts in the crafting window and the build panel (not red when chests have it).
// While an action is about to pay (Paying, Filling) only chests that can pay at once are counted (ReadyOnly).
using System.Collections.Generic;
using HarmonyLib;

namespace CraftFromChests
{
    internal static class Linking
    {
        internal static int Depth;      // > 0: the local player's bag counts include the chests
        internal static bool ReadyOnly; // count only chests that can pay right now

        private static readonly Stack<bool> RecipeChecks = new Stack<bool>();
        private static readonly Stack<bool> PieceChecks = new Stack<bool>();
        private static readonly Stack<bool> Displays = new Stack<bool>();

        internal static void Reset()
        {
            Depth = 0;
            ReadyOnly = false;
            RecipeChecks.Clear();
            PieceChecks.Clear();
            Displays.Clear();
        }

        private static bool Local(Player player) => player != null && player == Player.m_localPlayer;

        // Recycle Tab's tab is open (reached by name: Craft From Chests is also a Hexium single without it).
        private static System.Func<bool> _recycling;
        private static bool _recyclingResolved;

        internal static bool Recycling
        {
            get
            {
                if (!_recyclingResolved)
                {
                    _recyclingResolved = true;
                    var tab = AccessTools.TypeByName("RecycleTab.Plugin");
                    var getter = tab != null ? AccessTools.PropertyGetter(tab, "Active") : null;
                    if (getter != null) _recycling = AccessTools.MethodDelegate<System.Func<bool>>(getter);
                }
                return _recycling != null && _recycling();
            }
        }

        // ---- where counting includes the chests

        [HarmonyPatch(typeof(Player), "HaveRequirementItems")]
        [HarmonyPrefix]
        private static void RecipeCheck(Player __instance, bool discover)
        {
            var on = !discover && Plugin.Crafting.Value && Local(__instance) && !Recycling;
            RecipeChecks.Push(on);
            if (on) Depth++;
        }

        [HarmonyPatch(typeof(Player), "HaveRequirementItems")]
        [HarmonyFinalizer]
        private static void RecipeCheckDone()
        {
            if (RecipeChecks.Count > 0 && RecipeChecks.Pop()) Depth--;
        }

        [HarmonyPatch(typeof(Player), nameof(Player.HaveRequirements), typeof(Piece), typeof(Player.RequirementMode))]
        [HarmonyPrefix]
        private static void PieceCheck(Player __instance, Player.RequirementMode mode)
        {
            var on = Plugin.Building.Value && Local(__instance) && mode != Player.RequirementMode.IsKnown;
            PieceChecks.Push(on);
            if (on) Depth++;
        }

        [HarmonyPatch(typeof(Player), nameof(Player.HaveRequirements), typeof(Piece), typeof(Player.RequirementMode))]
        [HarmonyFinalizer]
        private static void PieceCheckDone()
        {
            if (PieceChecks.Count > 0 && PieceChecks.Pop()) Depth--;
        }

        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.SetupRequirement))]
        [HarmonyPrefix]
        private static void Display(Player player, bool craft)
        {
            var on = Local(player) && (craft ? Plugin.Crafting.Value && !Recycling : Plugin.Building.Value);
            Displays.Push(on);
            if (on) Depth++;
        }

        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.SetupRequirement))]
        [HarmonyFinalizer]
        private static void DisplayDone()
        {
            if (Displays.Count > 0 && Displays.Pop()) Depth--;
        }

        // ---- the counts

        private static bool ForBag(Inventory inventory)
        {
            if (Depth <= 0) return false;
            var player = Player.m_localPlayer;
            return player != null && inventory == player.m_inventory;
        }

        internal static int ChestCount(string name, int quality, bool matchWorldLevel)
        {
            var area = Plugin.Here();
            return ReadyOnly ? DigiStorage.Chests.CountReady(area, name, quality, matchWorldLevel) : DigiStorage.Chests.Count(area, name, quality, matchWorldLevel);
        }

        [HarmonyPatch(typeof(Inventory), nameof(Inventory.CountItems), typeof(string), typeof(int), typeof(bool))]
        [HarmonyPostfix]
        private static void Counted(Inventory __instance, string name, int quality, bool matchWorldLevel, ref int __result)
        {
            if (!ForBag(__instance)) return;
            __result += ChestCount(name, quality, matchWorldLevel);
        }

        [HarmonyPatch(typeof(Inventory), nameof(Inventory.HaveItem), typeof(string), typeof(bool))]
        [HarmonyPostfix]
        private static void Has(Inventory __instance, string name, bool matchWorldLevel, ref bool __result)
        {
            if (__result || !ForBag(__instance)) return;
            __result = ChestCount(name, -1, matchWorldLevel) > 0;
        }
    }
}
