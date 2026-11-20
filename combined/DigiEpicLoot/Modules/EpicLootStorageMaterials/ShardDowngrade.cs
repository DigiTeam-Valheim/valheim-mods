// Epic Loot Storage Materials, shard downgrade: Epic Loot only upgrades shards (5 Magic -> 1 Rare, and so on), so shards
// upgraded too far cannot come back. This adds the way down to the Convert tab's Convert list: one shard becomes
// shards one rarity lower, exactly as many as upgrading them costs at this table. That is the Upgrade recipe's amount,
// or the table's Convert level ratio when it has one (Epic Loot's own rule: 5:1 at level 0 down to 1:1 at level 7),
// so going down and up again never gains anything; coins paid for an upgrade are not returned. Shards only, and only
// where an Upgrade recipe exists (a server config without one gets no downgrade either). Listed like Epic Loot's own
// recipes: only while the player (or a nearby chest) has the higher shard.
using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using BepInEx.Logging;
using EpicLoot;
using EpicLoot.Crafting;
using EpicLoot.CraftingV2;
using EpicLoot_UnityLib;
using HarmonyLib;
using UnityEngine;

namespace EpicLootStorageMaterials
{
    internal static class ShardDowngrade
    {
        private static readonly ItemRarity[] Rarities =
            { ItemRarity.Magic, ItemRarity.Rare, ItemRarity.Epic, ItemRarity.Legendary, ItemRarity.Mythic, ItemRarity.Ancient };

        private static ConfigEntry<bool> _enabled;
        private static ManualLogSource _log;

        internal static void Bind(DigiCore.ModuleConfig config)
        {
            _enabled = config.Bind("Convert", "Downgrade shards", true,
                "The Convert tab's Convert list also turns a shard into shards one rarity lower (Rare into Magic, ...): as many " +
                "as upgrading them costs at this table (5 at Convert level 0, down to 1 at level 7). Coins paid for upgrades " +
                "are not returned.");
        }

        internal static void Patch(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            harmony.Patch(AccessTools.Method(typeof(EnchantingUIController), "GetConversionRecipes"),
                postfix: new HarmonyMethod(typeof(ShardDowngrade), nameof(AddDowngrades)));
        }

        private static void AddDowngrades(MaterialConversionType mode, List<ConversionRecipeUnity> __result)
        {
            if (mode != MaterialConversionType.Convert || __result == null || !_enabled.Value) return;
            try
            {
                var table = EnchantingTableUI.instance != null ? EnchantingTableUI.instance.SourceTable : null;
                if (table == null || InventoryManagement.Instance == null) return;
                var level = table.GetFeatureCurrentValue(EnchantingFeature.ConvertMaterials);
                var ratio = float.IsNaN(level.Item1) ? -1f : level.Item1;
                var upgrades = MaterialConversions.Conversions.GetValues(MaterialConversionType.Upgrade, true);
                for (var i = Rarities.Length - 1; i > 0; i--)
                {
                    var higherName = "Shard" + Rarities[i];
                    var lowerName = "Shard" + Rarities[i - 1];
                    var upgrade = upgrades.FirstOrDefault(c => c != null && c.Product == higherName && c.Resources != null &&
                                                               c.Resources.Any(r => r != null && r.Item == lowerName));
                    if (upgrade == null || upgrade.Amount < 1) continue;
                    var higher = Item(higherName);
                    var lower = Item(lowerName);
                    if (higher == null || lower == null || InventoryManagement.Instance.CountItem(higher) <= 0) continue;

                    // What the upgrade costs here, worked out as Epic Loot does it for the Upgrade list.
                    var amount = upgrade.Resources.First(r => r != null && r.Item == lowerName).Amount;
                    if (ratio > 0f && higher.IsMagicCraftingMaterial() && lower.IsMagicCraftingMaterial())
                        amount = Mathf.CeilToInt(ratio * upgrade.Amount);
                    if (amount < 1) continue;
                    __result.Add(new ConversionRecipeUnity
                    {
                        Product = lower,
                        Amount = amount,
                        Cost = new List<ConversionRecipeCostUnity> { new ConversionRecipeCostUnity { Item = higher, Amount = upgrade.Amount } }
                    });
                }
            }
            catch (Exception e)
            {
                _log?.LogWarning("Could not add the shard downgrades to the Convert tab: " + e.Message);
            }
        }

        private static ItemDrop.ItemData Item(string prefabName)
        {
            var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefabName) : null;
            var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (drop == null) return null;
            drop.m_itemData.m_dropPrefab = prefab;
            return drop.m_itemData.Clone();
        }
    }
}
