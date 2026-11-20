// Epic Loot Fixes, sockets on worn gear: Epic Loot's socket shortcut (and its gamepad button) opens the shard sockets of
// the item under the mouse, but it only looks in the inventory grid (SocketOverlayInput.TryGetPressTarget reads
// m_playerGrid). With CDEV Equipment Slots everything you wear sits in its own equipment panel (SlotPanel.Grid, an
// InventoryGrid over the same player inventory), so the shards of worn gear could not be changed without taking it off.
// When Epic Loot finds nothing under the mouse in the inventory grid, the equipment panel is asked too (for the gamepad:
// when that panel has the focus). The item found is the real one: CDEV's cells are places in the player's inventory, so
// Epic Loot's own panel, its checks and the effect refresh after a change work as for any other item.
// Light: only when the socket shortcut is pressed.
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace EpicLootFixes
{
    internal static class WornSockets
    {
        private static ConfigEntry<bool> _enabled;
        private static bool _resolved;
        private static FieldInfo _cdevGrid;
        private static MethodInfo _gamepadPressed;

        internal static void Bind(DigiCore.ModuleConfig config)
        {
            _enabled = config.Bind("Sockets", "Sockets on worn gear", true,
                "Epic Loot's socket shortcut also works on the gear in CDEV Equipment Slots' equipment panel (everything you " +
                "wear sits there), so you can change its shards without taking it off. Without CDEV nothing changes.");
        }

        internal static void Patch(Harmony harmony, BepInEx.Logging.ManualLogSource log)
        {
            var input = AccessTools.TypeByName("EpicLoot.ShardStones.SocketOverlayInput");
            var target = input != null ? AccessTools.Method(input, "TryGetPressTarget") : null;
            if (target == null)
            {
                log.LogWarning("Sockets on worn gear is off: this Epic Loot version has no socket shortcut we know.");
                return;
            }
            _gamepadPressed = AccessTools.Method(input, "GamepadPressed");
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(WornSockets), nameof(AlsoWorn)));
        }

        // CDEV's equipment panel grid, looked up on first use (CDEV may load after us; a type search scans every assembly).
        private static InventoryGrid CdevGrid()
        {
            if (!_resolved)
            {
                _resolved = true;
                var panel = AccessTools.TypeByName("CDEV.EquipmentSlots.SlotPanel");
                _cdevGrid = panel != null ? AccessTools.Field(panel, "Grid") : null;
            }
            var grid = _cdevGrid?.GetValue(null) as InventoryGrid;
            return grid != null && grid.gameObject.activeInHierarchy ? grid : null;
        }

        private static void AlsoWorn(InventoryGui gui, ref bool __result, ref ItemDrop.ItemData item, ref bool gamepad)
        {
            if (!_enabled.Value || gui == null) return;
            if (__result && item != null) return; // Epic Loot found an item in the inventory grid itself
            var grid = CdevGrid();
            if (grid == null) return;
            if (__result && !gamepad)
            {
                var mouse = ZInput.pointerPosition;
                item = grid.GetItem(new Vector2i(Mathf.RoundToInt(mouse.x), Mathf.RoundToInt(mouse.y)));
                return;
            }
            if (!__result && grid.m_uiGroup != null && grid.m_uiGroup.IsActive && _gamepadPressed != null && (bool)_gamepadPressed.Invoke(null, null))
            {
                item = grid.GetGamepadSelectedItem();
                gamepad = true;
                __result = true;
            }
        }
    }
}
