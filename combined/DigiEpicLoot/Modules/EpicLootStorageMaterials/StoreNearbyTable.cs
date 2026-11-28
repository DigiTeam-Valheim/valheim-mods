// Epic Loot Storage Materials, Store Nearby into the enchanting table: the Store Nearby button (Inventory and QoL) fills
// chests that already hold an item. With this, enchanting materials (dust, essences, reagents, shards, runestones,
// shardstones, ...) first go into the Storage of the nearest enchanting table with room, up to the room its Storage level
// allows per item; whatever does not fit goes on to the chests as before. They are only of use at the table, so they go
// there even when it holds none of that kind yet (switchable: then only what it already holds).
// From your inventory, and from your Adventure Backpacks backpack while it is open (Store Nearby stores that too).
// Which items may go follows Store Nearby's own rules (hotbar, equipped items and its keep lists stay with you), by asking
// its item check (Sendable). Store Nearby is reached by name (its BeforeStore hook point), so this works with its Hexium
// single as well. The deposit is Epic Loot's own: items leave the inventory, the table's owner adds them, and anything the
// table did not take is given back. Tables behind a ward you may not open are skipped.
// Light: runs only when the Store Nearby button is pressed.
using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using EpicLoot_UnityLib;
using HarmonyLib;
using UnityEngine;

namespace EpicLootStorageMaterials
{
    internal static class StoreNearbyTable
    {
        private sealed class Entry
        {
            public Inventory From;
            public ItemDrop.ItemData Item;
        }

        private static ConfigEntry<bool> _enabled, _allKinds;
        private static ManualLogSource _log;
        private static MethodInfo _sendable;

        internal static void Bind(DigiCore.ModuleConfig config)
        {
            _enabled = config.Bind("General", "Store Nearby fills the enchanting table", true,
                "The Store Nearby button also puts enchanting materials into the Storage of nearby enchanting tables, up to the " +
                "room its Storage level allows. From your inventory and your open backpack. The rest goes to chests.");
            _allKinds = config.Bind("General", "Store Nearby puts every enchanting material in the table", true,
                "Enchanting materials and shardstones go into the nearest enchanting table's Storage even when it holds none " +
                "of that kind yet, up to the room its Storage level allows. Off: only kinds it already holds (as Store " +
                "Nearby does with chests).");
        }

        internal static void Patch(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            var storeNearby = AccessTools.TypeByName("StoreNearby.Plugin");
            var before = storeNearby != null ? AccessTools.Method(storeNearby, "BeforeStore", new[] { typeof(Player), typeof(List<Inventory>), typeof(float) }) : null;
            _sendable = storeNearby != null ? AccessTools.Method(storeNearby, "Sendable", new[] { typeof(ItemDrop.ItemData), typeof(Inventory) }) : null;
            if (before == null || _sendable == null || !TableSend.Ready(log))
            {
                log.LogInfo("Store Nearby into the enchanting table is off: Store Nearby (Inventory and QoL) or Epic Loot's table storage was not found.");
                return;
            }
            // First, so the table gets its materials before the chests.
            harmony.Patch(before, postfix: new HarmonyMethod(typeof(StoreNearbyTable), nameof(BeforeStoreNearby)) { priority = Priority.First });
        }

        private static void BeforeStoreNearby(Player player, List<Inventory> sources, float range)
        {
            try
            {
                if (_enabled.Value) Deposit(player, sources, range);
            }
            catch (Exception e)
            {
                _log?.LogWarning("Store Nearby into the enchanting table failed: " + e.Message);
            }
        }

        private static void Deposit(Player player, List<Inventory> sources, float range)
        {
            if (player == null || sources == null || player.IsDead() || player.IsTeleporting()) return;
            var origin = player.transform.position;
            var tables = new List<EnchantingTable>();
            foreach (var table in UnityEngine.Object.FindObjectsByType<EnchantingTable>(FindObjectsSortMode.None))
            {
                var stash = table.Stash;
                if (stash == null || !stash.CanDeposit || !stash.HasAccess()) continue;
                if ((table.transform.position - origin).sqrMagnitude <= range * range) tables.Add(table);
            }
            if (tables.Count == 0) return;
            tables.Sort((a, b) => (a.transform.position - origin).sqrMagnitude.CompareTo((b.transform.position - origin).sqrMagnitude));

            var items = new List<Entry>();
            foreach (var from in sources)
                foreach (var item in from.GetAllItems())
                    if (item?.m_shared != null && StashCatalog.IsStashable(item) && (bool)_sendable.Invoke(null, new object[] { item, from }))
                        items.Add(new Entry { From = from, Item = item });
            if (items.Count == 0) return;

            var total = 0;
            var taken = new List<TableSend.Taken>();
            foreach (var table in tables)
            {
                var stash = table.Stash;
                var deltas = new Dictionary<string, int>();
                var moved = 0;
                taken.Clear();
                foreach (var entry in items)
                {
                    var item = entry.Item;
                    if (item.m_stack <= 0 || !entry.From.ContainsItem(item)) continue;
                    var prefab = StashCatalog.GetPrefabName(item);
                    if (prefab == null) continue;
                    // Every kind, or like Store Nearby with chests: only what it already holds.
                    if (!_allKinds.Value && stash.GetCount(prefab) <= 0) continue;
                    deltas.TryGetValue(prefab, out var pending);
                    var amount = Mathf.Min(item.m_stack, stash.GetRoom(prefab) - pending);
                    if (amount <= 0) continue;
                    TableSend.Take(entry.From, item, amount, taken);
                    deltas[prefab] = pending + amount;
                    moved += amount;
                }
                if (moved == 0) continue;
                TableSend.Send(stash, deltas, taken, player); // gives everything back if it fails
                total += moved;
            }
            if (total > 0 && Localization.instance != null)
                player.Message(MessageHud.MessageType.TopLeft, Localization.instance.Localize("$mod_epicloot_storage_deposited", total.ToString()));
        }
    }
}
