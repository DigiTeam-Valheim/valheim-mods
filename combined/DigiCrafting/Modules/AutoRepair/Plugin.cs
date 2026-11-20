// Opening a crafting station repairs everything it can repair at once: one sound, one message ("Repaired 5 items"),
// the same Crafting XP as repairing each item by hand. Only items that station can repair (same rules as the repair
// button), so no free repairs of items that need a better station.
// Repair cost (off by default): repairs cost coins from your bag, by the item's biome (the one Recipe Sort and Search
// works out from where its materials come from), more for each quality level, times how worn it is. Tools (hammer, hoe,
// cultivator, pickaxes, fishing rods, torches) stay free. Equipped items go first when the coins run short. The
// station's own Repair button costs the same then. Not in no-cost mode (the game's own, or a creative world).
using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AutoRepair
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.autorepair";
        public const string Name = "Digi's Auto Repair";
        public const string Version = "1.0.1";

        private const string Coins = "$item_coins";
        private const string DefaultPrices =
            "Meadows 1, Black Forest 2, Swamp 4, Ocean 4, Mountains 6, Plains 10, Mistlands 15, Ashlands 20, Deep North 30";

        private static ConfigEntry<bool> _enabled, _equipped, _xp, _message, _cost, _toolsFree;
        private static ConfigEntry<string> _prices;
        private static ConfigEntry<int> _perLevel, _unknownPrice;
        private static readonly int[] Prices = new int[9]; // by Recipe Sort's biome order, Meadows to Deep North

        private static readonly Func<InventoryGui, ItemDrop.ItemData, bool> CanRepair =
            AccessTools.MethodDelegate<Func<InventoryGui, ItemDrop.ItemData, bool>>(AccessTools.Method(typeof(InventoryGui), "CanRepair"));
        private static readonly List<ItemDrop.ItemData> Worn = new List<ItemDrop.ItemData>();

        // The plugin ID changed from pieter.valheim.* to digi.valheim.*: carry the old settings file over once.
        private void MigrateConfig()
        {
            var old = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, Guid.Replace("digi.valheim.", "pieter.valheim.") + ".cfg");
            if (System.IO.File.Exists(Config.ConfigFilePath) || !System.IO.File.Exists(old)) return;
            System.IO.File.Copy(old, Config.ConfigFilePath);
            Config.Reload();
        }


        private void Awake()
        {
            MigrateConfig();
            _enabled = Config.Bind("General", "Enabled", true, "Opening a workbench, forge or other crafting station repairs everything that station can repair.");
            _equipped = Config.Bind("General", "Include equipped items", true, "Also repair what you are wearing and holding, not only the items in your inventory.");
            _xp = Config.Bind("General", "Crafting XP", true, "You get the same Crafting XP as repairing each item by hand.");
            _message = Config.Bind("General", "Show message", true, "Show \"Repaired 5 items\" in the middle of the screen.");
            _cost = Config.Bind("Repair cost", "Repairs cost coins", false,
                "Repairing costs coins from your bag: the price of the item's biome (Coins per biome), more for each quality " +
                "level, times how worn it is (half worn = half the price), at least 1 coin. Tools stay free (Tools are free). " +
                "When the coins run short, equipped items are repaired first. The station's own Repair button costs the same.");
            _prices = Config.Bind("Repair cost", "Coins per biome", DefaultPrices,
                "What fully repairing a quality 1 item of each biome costs. The biome is where its materials come from, as " +
                "Recipe Sort and Search works it out (a Bronze Sword is Black Forest, a Blackmetal Axe Plains).");
            _perLevel = Config.Bind("Repair cost", "Extra per quality level (%)", 50, new ConfigDescription(
                "Each quality level above 1 adds this much of the biome price: at 50, a quality 3 item costs twice as much.",
                new AcceptableValueRange<int>(0, 500)));
            _unknownPrice = Config.Bind("Repair cost", "Coins for other items", 10, new ConfigDescription(
                "The price for items whose biome cannot be told (some items from other mods).", new AcceptableValueRange<int>(0, 1000)));
            _toolsFree = Config.Bind("Repair cost", "Tools are free", true,
                "Hammer, hoe, cultivator, pickaxes, fishing rods and torches are repaired for free.");
            _prices.SettingChanged += (_, __) => ReadPrices();
            _unknownPrice.SettingChanged += (_, __) => ReadPrices();
            ReadPrices();
            PatchModule(typeof(Plugin));
        }

        private static void ReadPrices()
        {
            var names = new string[Prices.Length];
            for (var i = 0; i < Prices.Length; i++)
            {
                names[i] = RecipeSort.Tiers.EnglishName(i).Replace(" ", "").ToLowerInvariant();
                Prices[i] = _unknownPrice.Value;
            }
            foreach (var part in (_prices.Value ?? "").Split(','))
            {
                var text = part.Trim();
                var space = text.LastIndexOf(' ');
                if (space <= 0 || !int.TryParse(text.Substring(space + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var coins)) continue;
                var name = text.Substring(0, space).Replace(" ", "").ToLowerInvariant();
                var index = Array.IndexOf(names, name);
                if (index >= 0) Prices[index] = Math.Max(0, coins);
            }
        }

        // --- Repair cost -----------------------------------------------------------------------------------------------

        private static bool IsTool(ItemDrop.ItemData item)
        {
            var shared = item.m_shared;
            return shared.m_itemType == ItemDrop.ItemData.ItemType.Tool || shared.m_itemType == ItemDrop.ItemData.ItemType.Torch ||
                   shared.m_skillType == Skills.SkillType.Pickaxes || shared.m_skillType == Skills.SkillType.Fishing;
        }

        // Coins to repair this item now; 0 = free.
        private static int CostOf(Player player, ItemDrop.ItemData item)
        {
            if (!_cost.Value || player == null || player.NoCostCheat() || item == null) return 0;
            if (_toolsFree.Value && IsTool(item)) return 0;
            var prefab = item.m_dropPrefab != null ? item.m_dropPrefab.GetComponent<ItemDrop>() : null;
            var tier = RecipeSort.Tiers.Of(prefab);
            float price = tier >= 0 && tier < Prices.Length ? Prices[tier] : _unknownPrice.Value;
            price *= 1f + Mathf.Max(0, item.m_quality - 1) * _perLevel.Value / 100f;
            var max = item.GetMaxDurability();
            var worn = max > 0f ? 1f - item.m_durability / max : 0f;
            return price <= 0f || worn <= 0f ? 0 : Mathf.Max(1, Mathf.CeilToInt(price * worn));
        }

        // Coins in the bag (any world level, as the game's own coin count for traders).
        private static int CoinsOf(Player player)
        {
            var count = 0;
            foreach (var item in player.GetInventory().GetAllItems())
                if (item.m_shared.m_name == Coins) count += item.m_stack;
            return count;
        }

        private static void Pay(Player player, int coins)
        {
            if (coins > 0) player.GetInventory().RemoveItem(Coins, coins, -1, false);
        }

        // --- Repairing ----------------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Show))]
        [HarmonyPostfix]
        private static void Show(InventoryGui __instance)
        {
            if (!_enabled.Value) return;
            var player = Player.m_localPlayer;
            var station = player != null ? player.GetCurrentCraftingStation() : null;
            if (station == null || !station.CheckUsable(player, false)) return;

            Worn.Clear();
            player.GetInventory().GetWornItems(Worn);
            // When repairs cost coins and they run short, what you wear and hold comes first.
            if (_cost.Value) Worn.Sort((a, b) => b.m_equipped.CompareTo(a.m_equipped));
            var repaired = 0;
            var paid = 0;
            var unpaid = 0;
            var short_ = 0;
            var coins = _cost.Value ? CoinsOf(player) : 0;
            foreach (var item in Worn)
            {
                if (!_equipped.Value && item.m_equipped) continue;
                if (!CanRepair(__instance, item)) continue;
                var cost = CostOf(player, item);
                if (cost > coins - paid)
                {
                    unpaid++;
                    short_ += cost;
                    continue;
                }
                paid += cost;
                if (_xp.Value) player.RaiseSkill(Skills.SkillType.Crafting, 1f - item.m_durability / item.GetMaxDurability());
                item.m_durability = item.GetMaxDurability();
                repaired++;
            }
            Worn.Clear();
            Pay(player, paid);
            if (repaired == 0 && unpaid == 0) return;
            if (repaired > 0) station.m_repairItemDoneEffects.Create(station.transform.position, Quaternion.identity);
            if (!_message.Value && unpaid == 0) return;
            var text = repaired == 0 ? "" : repaired == 1 ? "Repaired 1 item" : $"Repaired {repaired} items";
            if (paid > 0) text += $" for {paid} coin{(paid == 1 ? "" : "s")}";
            if (unpaid > 0)
                text += (text.Length > 0 ? ". " : "") + $"{unpaid} more need{(unpaid == 1 ? "s" : "")} {short_} coins (you have {coins - paid})";
            player.Message(MessageHud.MessageType.Center, text);
        }

        // The station's Repair button: the same price for the item it repairs (the first one, as the game picks it).
        [HarmonyPatch(typeof(InventoryGui), "RepairOneItem")]
        [HarmonyPrefix]
        private static bool RepairButton(InventoryGui __instance)
        {
            var player = Player.m_localPlayer;
            if (!_cost.Value || player == null || player.NoCostCheat()) return true;
            var station = player.GetCurrentCraftingStation();
            if (station == null || !station.CheckUsable(player, false)) return true; // the game repairs nothing then
            Worn.Clear();
            player.GetInventory().GetWornItems(Worn);
            ItemDrop.ItemData next = null;
            foreach (var item in Worn)
                if (CanRepair(__instance, item)) { next = item; break; }
            Worn.Clear();
            var cost = CostOf(player, next);
            if (cost <= 0) return true;
            var coins = CoinsOf(player);
            if (coins < cost)
            {
                player.Message(MessageHud.MessageType.Center,
                    $"Repairing the {Localization.instance.Localize(next.m_shared.m_name)} costs {cost} coins (you have {coins})");
                return false;
            }
            Pay(player, cost);
            return true;
        }
    }
}
