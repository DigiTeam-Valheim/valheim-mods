// The forge only offers the Style picker when crafting a new item. This lets you restyle an item you already own
// (e.g. an enchanted Epic Loot shield): at a station that can craft it, hover the item in your inventory or open
// backpack and press the restyle key. The game's own style picker opens and the chosen style is applied for free.
// Only the item's variant changes; quality, durability and enchantments (custom data) are untouched.
using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RestyleItems
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.restyleitems";
        public const string Name = "Digi's Restyle Items";
        public const string Version = "1.0.2";

        private static ConfigEntry<KeyCode> _key;
        private static MethodInfo _hovered;
        private static readonly AccessTools.FieldRef<InventoryGui, InventoryGrid> ContainerGrid =
            AccessTools.FieldRefAccess<InventoryGui, InventoryGrid>("m_containerGrid");

        private static ItemDrop.ItemData _pending;
        private static Inventory _pendingInventory;
        private static readonly MethodInfo InventoryChanged = AccessTools.Method(typeof(Inventory), "Changed");

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
            _key = Config.Bind("General", "Restyle key", KeyCode.R,
                "At a station that can craft the item, hover over it in your inventory and press this key to pick another style.");
            _hovered = AccessTools.Method(typeof(InventoryGrid), "GetHoveredElement");
            if (_hovered == null)
            {
                Logger.LogError("Disabled, InventoryGrid.GetHoveredElement was not found.");
                return;
            }
            ModuleHarmony.PatchAll(typeof(Plugin));
            Logger.LogInfo("Hover an item at its crafting station and press " + _key.Value + " to restyle it.");
        }

        [HarmonyPatch(typeof(InventoryGui), "Update")]
        [HarmonyPostfix]
        private static void UpdatePostfix(InventoryGui __instance)
        {
            if (_pending != null && !__instance.m_variantDialog.gameObject.activeSelf) _pending = null; // closed without picking
            if (!InventoryGui.IsVisible() || __instance.m_variantDialog.gameObject.activeSelf) return;
            if (!ZInput.GetKeyDown(_key.Value) || Console.IsVisible() || TextInput.IsVisible() || Chat.instance?.HasFocus() == true) return;

            var player = Player.m_localPlayer;
            var inventory = __instance.m_playerGrid.GetInventory();
            var item = Hovered(__instance.m_playerGrid);
            if (item == null && ContainerGrid(__instance) != null)
            {
                inventory = ContainerGrid(__instance).GetInventory();
                item = Hovered(ContainerGrid(__instance));
            }
            if (player == null || item?.m_shared == null) return;
            if (item.m_shared.m_variants <= 1)
            {
                player.Message(MessageHud.MessageType.Center, "This item has no other styles");
                return;
            }

            var station = player.GetCurrentCraftingStation();
            var recipe = ObjectDB.instance ? ObjectDB.instance.GetRecipe(item) : null;
            var needed = recipe?.m_craftingStation;
            if (station == null || recipe == null || (needed != null && needed.m_name != station.m_name))
            {
                player.Message(MessageHud.MessageType.Center,
                    needed != null ? $"Restyle at a {needed.m_name}" : "Restyle at a crafting station");
                return;
            }

            _pending = item;
            _pendingInventory = inventory;
            __instance.m_variantDialog.Setup(item);
        }

        private static ItemDrop.ItemData Hovered(InventoryGrid grid)
        {
            if (grid == null) return null;
            var element = _hovered.Invoke(grid, null) as InventoryElement;
            if (element == null) return null;
            return grid.GetInventory()?.GetItemAt(element.Position.x, element.Position.y);
        }

        // The dialog reports the pick to InventoryGui.OnVariantSelected; while restyling, apply it to the owned item
        // instead of changing the craft selection.
        [HarmonyPatch(typeof(InventoryGui), "OnVariantSelected")]
        [HarmonyPrefix]
        private static bool OnVariantSelectedPrefix(int index)
        {
            var item = _pending;
            var inventory = _pendingInventory;
            _pending = null;
            _pendingInventory = null;
            if (item == null) return true;

            var player = Player.m_localPlayer;
            if (player == null || index < 0 || index >= item.m_shared.m_variants) return false;
            if (item.m_variant == index) return false;

            var equipped = player.IsItemEquiped(item);
            if (equipped) player.UnequipItem(item, false);
            item.m_variant = index;
            if (equipped) player.EquipItem(item, false);

            // Saves containers such as an open backpack and refreshes the grid.
            if (inventory != null) InventoryChanged?.Invoke(inventory, null);
            player.Message(MessageHud.MessageType.Center, "Style changed");
            return false;
        }
    }
}
