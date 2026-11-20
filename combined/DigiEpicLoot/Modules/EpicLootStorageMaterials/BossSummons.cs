// Epic Loot Storage Materials, boss offerings: the Salvage Junk list already makes a Deer Trophy (Eikthyr's offering)
// from Hard Antlers. This adds every other boss's offering, each from a common drop of that boss's biome, so a boss can
// be fought again without hunting for the rare item. Hard on purpose: about 30-90 kills of that biome's creatures per
// summon (the cost multiplier changes all of them). Listed like Epic Loot's own recipes: only while the player (or a
// nearby chest) has the ingredient.
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using EpicLoot.Crafting;
using EpicLoot.CraftingV2;
using EpicLoot_UnityLib;
using HarmonyLib;
using UnityEngine;

namespace EpicLootStorageMaterials
{
    internal static class BossSummons
    {
        // Offering, ingredient, ingredients per offering. What one summon takes is in the comment.
        private static readonly string[][] Recipes =
        {
            new[] { "AncientSeed", "GreydwarfEye", "10" },       // The Elder: 3 seeds = 30 eyes
            new[] { "WitheredBone", "Entrails", "5" },           // Bonemass: 10 bones = 50 entrails
            new[] { "DragonEgg", "FreezeGland", "10" },          // Moder: 3 eggs = 30 glands
            new[] { "GoblinTotem", "TrophyGoblin", "2" },        // Yagluth: 3 totems = 6 Fuling trophies
            new[] { "TrophySeekerBrute", "Mandible", "10" },     // The Queen (her altar in the citadel): 3 Seeker Soldier trophies = 30 mandibles
            new[] { "BellFragment", "CharredBone", "15" },       // Fader: 3 bells = 9 fragments = 135 charred bones
            new[] { "HatefulBlood", "OrbThunderBlood", "5" },    // the final boss: 3 Malicious Blood = 15 essences
        };

        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<float> _cost;
        private static ManualLogSource _log;

        internal static void Bind(DigiCore.ModuleConfig config)
        {
            _enabled = config.Bind("Salvage Junk", "Boss offerings", true,
                "The Salvage Junk list also makes every boss's offering from a common drop of its biome: Ancient Seed (10 " +
                "Greydwarf Eyes), Withered Bone (5 Entrails), Dragon Egg (10 Freeze Glands), Fuling Totem (2 Fuling Trophies), " +
                "Seeker Soldier Trophy (10 Mandibles), Bell Fragment (15 Charred Bones) and Malicious Blood (5 Thunderblood " +
                "Essences). The Deer Trophy for Eikthyr was already there.");
            _cost = config.Bind("Salvage Junk", "Boss offering cost multiplier", 1f, new ConfigDescription(
                "Ingredients per offering, times this (rounded up): 0.5 = half, 2 = double.", new AcceptableValueRange<float>(0.1f, 5f)));
        }

        internal static void Patch(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            harmony.Patch(AccessTools.Method(typeof(EnchantingUIController), "GetConversionRecipes"),
                postfix: new HarmonyMethod(typeof(BossSummons), nameof(AddOfferings)));
        }

        private static void AddOfferings(MaterialConversionType mode, List<ConversionRecipeUnity> __result)
        {
            if (mode != MaterialConversionType.Junk || __result == null || !_enabled.Value || InventoryManagement.Instance == null) return;
            try
            {
                foreach (var recipe in Recipes)
                {
                    var product = Item(recipe[0]);
                    var ingredient = Item(recipe[1]);
                    if (product == null || ingredient == null || InventoryManagement.Instance.CountItem(ingredient) <= 0) continue;
                    var amount = Mathf.Max(1, Mathf.CeilToInt(int.Parse(recipe[2]) * _cost.Value));
                    __result.Add(new ConversionRecipeUnity
                    {
                        Product = product,
                        Amount = 1,
                        Cost = new List<ConversionRecipeCostUnity> { new ConversionRecipeCostUnity { Item = ingredient, Amount = amount } }
                    });
                }
            }
            catch (Exception e)
            {
                _log?.LogWarning("Could not add the boss offerings to the Salvage Junk list: " + e.Message);
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
