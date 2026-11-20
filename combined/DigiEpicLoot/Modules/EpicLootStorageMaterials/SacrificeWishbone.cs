// Epic Loot Storage Materials, the Wishbone in the Sacrifice tab. Epic Loot gives every boss drop a sacrifice except
// Bonemass's Wishbone (The Elder's Swamp Key gives Rare materials, Moder's Dragon Tear Legendary ones); this gives it the
// Epic ones in between: 2 Epic Dust, 1 Epic Essence and 1 Epic Reagent. Only when Epic Loot's own config has none for it.
// "Keep one wishbone": the Sacrifice list always leaves you one. A wishbone you wear, carry or keep in a nearby chest
// that is not in the list counts as that one; otherwise one listed wishbone is left out (the one in your inventory
// first), so Select All can never take the last.
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using BepInEx.Logging;
using EpicLoot.Crafting;
using EpicLoot_UnityLib;
using HarmonyLib;

namespace EpicLootStorageMaterials
{
    internal static class SacrificeWishbone
    {
        private const string Token = "$item_wishbone";
        private static ConfigEntry<bool> _enabled, _keepOne;

        internal static void Bind(DigiCore.ModuleConfig config)
        {
            _enabled = config.Bind("Sacrifice", "Wishbone can be sacrificed", true,
                "Bonemass's Wishbone can be sacrificed for 2 Epic Dust, 1 Epic Essence and 1 Epic Reagent (Epic Loot gives the " +
                "other boss drops a sacrifice: a Swamp Key gives the Rare ones, a Dragon Tear the Legendary ones).");
            _keepOne = config.Bind("Sacrifice", "Keep one wishbone", true,
                "The Sacrifice list always leaves you one Wishbone: one you wear, carry or keep in a nearby chest stays out of it.");
        }

        internal static void Patch(Harmony harmony, ManualLogSource log)
        {
            var method = AccessTools.Method(typeof(EnchantCostsHelper), nameof(EnchantCostsHelper.GetSacrificeProducts),
                new[] { typeof(ItemDrop.ItemData) });
            if (method == null)
            {
                log.LogWarning("Epic Loot's sacrifice products were not found: the Wishbone cannot be sacrificed.");
                return;
            }
            harmony.Patch(method, postfix: new HarmonyMethod(typeof(SacrificeWishbone), nameof(Products)));
        }

        // A fresh list every time: Epic Loot's callers may change what they get.
        private static void Products(ItemDrop.ItemData item, ref List<ItemAmountConfig> __result)
        {
            if (__result != null || !_enabled.Value || item?.m_shared?.m_name != Token) return;
            __result = new List<ItemAmountConfig>
            {
                new ItemAmountConfig { Item = "DustEpic", Amount = 2 },
                new ItemAmountConfig { Item = "EssenceEpic", Amount = 1 },
                new ItemAmountConfig { Item = "ReagentEpic", Amount = 1 },
            };
        }

        // Called with the finished Sacrifice list and the chests it may draw from.
        internal static void KeepOne(List<InventoryItemListElement> list, IEnumerable<Container> chests)
        {
            if (list == null || !_enabled.Value || !_keepOne.Value) return;
            var listed = list.Where(e => e?.Item?.m_shared?.m_name == Token).ToList();
            if (listed.Count == 0) return;
            var inList = new HashSet<ItemDrop.ItemData>(listed.Select(e => e.Item));
            bool Kept(ItemDrop.ItemData item) => item?.m_shared?.m_name == Token && !inList.Contains(item);
            var player = Player.m_localPlayer;
            if (player != null && player.GetInventory().GetAllItems().Any(Kept)) return;
            foreach (var chest in chests)
                if (chest != null && chest.GetInventory() != null && chest.GetInventory().GetAllItems().Any(Kept)) return;
            var keep = listed.FirstOrDefault(e => player != null && player.GetInventory().ContainsItem(e.Item)) ?? listed[0];
            list.Remove(keep);
        }
    }
}
