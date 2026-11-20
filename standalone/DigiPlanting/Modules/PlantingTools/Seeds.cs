// Planting Tools, paying: seeds come from what you carry first, then from chests near you (a setting), with the same
// chest rules as the auto stations: only built chests, not someone else's private ones, not chests someone has open,
// and Local Craft From Storage Prime's "excluded" chests and marker slots are left alone. The count shown with the grid
// includes the chests, so a green plant is a paid plant. A chest controlled by another player is asked for (the game
// hands it over like opening it would) and used from the next click on.
// The range: Craft From Chests' (Crafting and Automation) while it runs, else our own setting. Its build hud shows the
// seeds in ITS range ("+297" under the seed); with our 20 m against its 50 m the grid said "1 to plant, 1,848 no seeds"
// for a player whose barley stood 20-50 m away (2026-11-17).
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace PlantingTools
{
    internal static class Seeds
    {
        private static ConfigEntry<bool> _chests;
        private static ConfigEntry<float> _range;
        private static readonly HashSet<Container> Chests = new HashSet<Container>();

        internal static void Bind(DigiCore.ModuleConfig config)
        {
            _chests = config.Bind("Planting", "Seeds from nearby chests", true,
                "When the seeds you carry run out, the grid (and replanting) takes them from chests near you.");
            _range = config.Bind("Planting", "Chest range (m)", 20f, new ConfigDescription(
                "Chests up to this far from you give seeds. With Craft From Chests (Crafting and Automation) on, its Range is " +
                "used instead: the same chests whose seeds the build hud counts.", new AcceptableValueRange<float>(1f, 50f)));
        }

        private static bool _cfcResolved;
        private static System.Reflection.FieldInfo _cfcRunning, _cfcRange;

        // Looked up on first use (by then every plugin is loaded), once: a type search scans every assembly.
        private static float Range()
        {
            if (!_cfcResolved)
            {
                _cfcResolved = true;
                var plugin = AccessTools.TypeByName("CraftFromChests.Plugin");
                if (plugin != null)
                {
                    _cfcRunning = AccessTools.Field(plugin, "Running");
                    _cfcRange = AccessTools.Field(plugin, "Range");
                }
            }
            var runs = _cfcRunning != null && _cfcRunning.GetValue(null) is bool on && on;
            return runs && _cfcRange?.GetValue(null) is ConfigEntry<float> range ? range.Value : _range.Value;
        }

        [HarmonyPatch(typeof(Container), "Awake")]
        [HarmonyPostfix]
        private static void ChestAwake(Container __instance) => Chests.Add(__instance);

        // The chests already loaded when the sub-mod starts (switched on while playing).
        internal static void Known()
        {
            foreach (var chest in Object.FindObjectsByType<Container>(FindObjectsSortMode.None)) Chests.Add(chest);
        }

        // How many of this item you can pay with: carried, plus what usable chests near you hold.
        internal static int Available(Player player, string item)
        {
            var n = player.GetInventory().CountItems(item);
            if (!_chests.Value) return n;
            foreach (var chest in Near(player, false))
            {
                var inv = chest.GetInventory();
                foreach (var it in inv.GetAllItems())
                    if (it.m_shared.m_name == item && !StoreRules.IsProtected(inv, it)) n += it.m_stack;
            }
            return n;
        }

        // Takes "amount" of the item: carried first, then the nearest chests. Returns how many it got.
        internal static int Take(Player player, string item, int amount)
        {
            var bag = player.GetInventory();
            var got = Mathf.Min(amount, bag.CountItems(item));
            if (got > 0) bag.RemoveItem(item, got);
            if (got >= amount || !_chests.Value) return got;
            foreach (var chest in Near(player, true))
            {
                var inv = chest.GetInventory();
                foreach (var it in inv.GetAllItems().Where(i => i.m_shared.m_name == item && !StoreRules.IsProtected(inv, i)).ToList())
                {
                    var take = Mathf.Min(amount - got, it.m_stack);
                    inv.RemoveItem(it, take);
                    got += take;
                    if (got >= amount) return got;
                }
            }
            return got;
        }

        // Usable chests near you, nearest first. With "take", a chest another player controls is asked for and skipped.
        private static List<Container> Near(Player player, bool take)
        {
            Chests.RemoveWhere(c => c == null);
            var me = Game.instance.GetPlayerProfile().GetPlayerID();
            var at = player.transform.position;
            var range = Range();
            var list = new List<Container>();
            var dist = new Dictionary<Container, float>();
            foreach (var chest in Chests)
            {
                if (chest.m_wagon != null || chest.GetComponentInParent<Ship>() != null || chest.GetComponent<TombStone>() != null) continue;
                var piece = chest.m_piece;
                if (piece == null) continue; // only built chests
                var d = Vector3.Distance(at, chest.transform.position);
                if (d > range) continue;
                if (chest.m_privacy == Container.PrivacySetting.Private && piece.GetCreator() != me) continue;
                if (chest.m_privacy == Container.PrivacySetting.Group) continue;
                var view = chest.m_nview;
                if (view == null || !view.IsValid() || chest.GetInventory() == null) continue;
                if (view.GetZDO().GetInt(ZDOVars.s_inUse) == 1 || chest.IsInUse()) continue;
                if (!StoreRules.CanWithdraw(chest)) continue;
                if (take && !view.IsOwner())
                {
                    view.ClaimOwnership();
                    continue;
                }
                list.Add(chest);
                dist[chest] = d;
            }
            return list.OrderBy(c => dist[c]).ToList();
        }
    }
}
