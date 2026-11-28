// Epic Loot Storage Materials: the one way items go into an enchanting table's Storage (Store Nearby into the table,
// Deposit from nearby chests). As Epic Loot's own Deposit All: the items leave where they were, one request goes to the
// table's owner (EnchantingTableStash.Send, private), and whatever it did not take comes back (RefundUnapplied).
// Epic Loot 0.16 gave Send a third parameter ("Action onLost = null"): called by name with two arguments it threw AFTER the
// items had left the inventory or chest, and they were gone (Anna-Drea's report, 2026-11-28). So: Send's shape is checked
// once (any further parameter must be optional and gets its default; another shape and both features stay off), and if
// the call still throws, the items go back where they came from (at your feet when that is full).
using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using EpicLoot_UnityLib;
using HarmonyLib;
using UnityEngine;

namespace EpicLootStorageMaterials
{
    internal static class TableSend
    {
        internal sealed class Taken
        {
            public Inventory From;
            public ItemDrop.ItemData Item; // a copy with the amount taken
        }

        private static MethodInfo _send, _refund;
        private static object[] _args;
        private static bool _resolved;

        // True when Send and RefundUnapplied are there and Send has a shape this can call.
        internal static bool Ready(ManualLogSource log)
        {
            if (_resolved) return _args != null;
            _resolved = true;
            _send = AccessTools.Method(typeof(EnchantingTableStash), "Send");
            _refund = AccessTools.Method(typeof(EnchantingTableStash), "RefundUnapplied", new[] { typeof(Dictionary<string, int>), typeof(Dictionary<string, int>) });
            if (_send == null || _refund == null) return false;
            var parameters = _send.GetParameters();
            if (parameters.Length < 2 || parameters[0].ParameterType != typeof(Dictionary<string, int>) ||
                parameters[1].ParameterType != typeof(Action<Dictionary<string, int>>))
            {
                log?.LogWarning("Epic Loot's table storage changed (Send has an unknown shape): nothing is put into enchanting tables.");
                return false;
            }
            var args = new object[parameters.Length];
            for (var i = 2; i < parameters.Length; i++)
            {
                if (!parameters[i].IsOptional)
                {
                    log?.LogWarning("Epic Loot's table storage changed (Send needs more): nothing is put into enchanting tables.");
                    return false;
                }
                args[i] = parameters[i].DefaultValue is DBNull ? null : parameters[i].DefaultValue;
            }
            _args = args;
            return true;
        }

        // Takes amount of item out of from (a copy is kept in taken, for a give-back).
        internal static void Take(Inventory from, ItemDrop.ItemData item, int amount, List<Taken> taken)
        {
            var copy = item.Clone();
            copy.m_stack = amount;
            from.RemoveItem(item, amount);
            taken.Add(new Taken { From = from, Item = copy });
        }

        // Sends what was taken (deltas: prefab -> amount) to the table; on any failure everything taken goes back, then the
        // exception goes on to the caller's log.
        internal static void Send(EnchantingTableStash stash, Dictionary<string, int> deltas, List<Taken> taken, Player player)
        {
            try
            {
                if (_args == null) throw new InvalidOperationException("Epic Loot's table storage was not found");
                var args = (object[])_args.Clone();
                args[0] = deltas;
                args[1] = (Action<Dictionary<string, int>>)(applied => _refund.Invoke(null, new object[] { deltas, applied }));
                _send.Invoke(stash, args);
            }
            catch
            {
                GiveBack(player, taken); // nothing reached the table: never lose the items
                throw;
            }
        }

        private static void GiveBack(Player player, List<Taken> taken)
        {
            foreach (var entry in taken)
            {
                try
                {
                    if (entry.From.AddItem(entry.Item)) continue; // a partial add leaves the rest in entry.Item.m_stack
                }
                catch (Exception)
                {
                    // dropped below
                }
                if (player == null) continue;
                var at = player.transform.position + player.transform.forward + Vector3.up;
                ItemDrop.DropItem(entry.Item, entry.Item.m_stack, at, Quaternion.identity);
            }
            taken.Clear();
        }
    }
}
