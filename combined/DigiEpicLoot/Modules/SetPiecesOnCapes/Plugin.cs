// Epic Loot's sets have a helmet, chest and legs piece, and a fourth (sometimes fifth) piece that is a weapon or a shield:
// Thor's maul, Heimdall's tower shield, Egil's bow. To finish a set you had to carry that exact weapon or shield.
// This lets those pieces also be a cape or a backpack (both are shoulder items): in drops, when an item is enchanted to
// legendary or better, and when a set rune is etched onto a cape or backpack. The piece keeps its own guaranteed
// effects; Epic Loot adds up every worn item's effects, so a weapon piece worn as a cape boosts the weapon in your hand.
// Epic Loot counts each piece once, so a cape and a weapon that are the same piece never count twice.
using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using EpicLoot;
using EpicLoot.LegendarySystem;
using HarmonyLib;

namespace SetPiecesOnCapes
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.setpiecesoncapes";
        public const string Name = "Digi's Set Pieces On Capes";
        public const string Version = "1.0.0";

        private static ConfigEntry<bool> _enabled;
        private static readonly HashSet<string> ArmorTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Helmet", "Chest", "Legs", "Shoulder" };

        private void Awake()
        {
            _enabled = Config.Bind("General", "Weapon and shield pieces on capes", true,
                "A set's weapon or shield piece (Thor's maul, Heimdall's tower shield, Egil's bow, ...) can also be a cape " +
                "or backpack: in drops, when enchanting to legendary or better, and when etching a set rune. It keeps its " +
                "own effects, which work from the cape slot too.");
            PatchModule(typeof(Plugin));
        }

        // The weapon or shield of a set: a set piece whose item types include no armour slot.
        private static bool IsOffPiece(LegendaryInfo piece)
        {
            var types = piece?.Requirements?.AllowedItemTypes;
            if (piece == null || !piece.IsSetItem || types == null || types.Count == 0) return false;
            foreach (var type in types)
                if (ArmorTypes.Contains(type)) return false;
            return true;
        }

        private static bool IsShoulder(ItemDrop.ItemData item) =>
            item?.m_shared != null && item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shoulder;

        // Drops and enchanting to legendary: the set pieces a cape or backpack can become.
        [HarmonyPatch(typeof(UniqueLegendaryHelper), nameof(UniqueLegendaryHelper.GetAvailableSetPieces))]
        [HarmonyPostfix]
        private static void AvailablePieces(ItemDrop.ItemData baseItem, ItemRarity rarity, List<LegendaryInfo> __result)
        {
            if (!_enabled.Value || __result == null || !IsShoulder(baseItem)) return;
            foreach (var pair in UniqueLegendaryHelper.ItemsToSetMap)
            {
                if (!UniqueLegendaryHelper.IsEnabledAt(pair.Value, rarity) || !UniqueLegendaryHelper.AllUniques.TryGetValue(pair.Key, out var piece)) continue;
                if (IsOffPiece(piece) && !__result.Contains(piece)) __result.Add(piece);
            }
        }

        // Etching a set rune: a weapon or shield piece fits a cape or backpack too.
        [HarmonyPatch(typeof(UniqueLegendaryHelper), "PieceFits")]
        [HarmonyPostfix]
        private static void PieceFits(LegendaryInfo piece, ItemDrop.ItemData item, ref bool __result)
        {
            if (!__result && _enabled.Value && IsShoulder(item) && IsOffPiece(piece)) __result = true;
        }

        // ...but a set's own cape piece still comes first when the rune does not name the weapon or shield piece
        // (Epic Loot tries the rune's piece, then the set's pieces in order, and the weapon is often listed first).
        [HarmonyPatch(typeof(UniqueLegendaryHelper), nameof(UniqueLegendaryHelper.TryGetSetPieceForItem))]
        [HarmonyPostfix]
        private static void PreferOwnPiece(LegendarySetInfo set, ItemDrop.ItemData item, ItemRarity rarity, string preferredID,
            ref LegendaryInfo piece, bool __result)
        {
            if (!__result || !IsShoulder(item) || !IsOffPiece(piece) || piece.ID == preferredID) return;
            var probe = new MagicItem { Rarity = rarity };
            foreach (var own in UniqueLegendaryHelper.GetDefinedSetPieces(set))
                if (!IsOffPiece(own) && (own.Requirements == null || own.Requirements.CheckRequirements(item, probe)))
                {
                    piece = own;
                    return;
                }
        }
    }
}
