// Backpack Quick Open: middle-click an Adventure Backpacks backpack in your inventory to open its storage without
// wearing it (e.g. while you wear a cape). Middle-click it again to close it, or another backpack to switch to that one.
// Done the way Adventure Backpacks opens the worn backpack: the backpack's own inventory is shown in a stand-in chest on
// the player (named with Adventure Backpacks' proxy prefix, so its own patches treat it as a backpack: no network
// object, always yours, never "in use"). Adventure Backpacks only saves the worn backpack, so every change to the open
// one is saved into its item here at once, and your carried weight is updated. Its "backpack open" flag is set too,
// so the Store Nearby button shows under the backpack as for the worn one.
// While a backpack is worn, middle-click opens only that one (Adventure Backpacks ties its backpack view to the worn
// backpack). The view closes by itself when the backpack leaves your inventory, a backpack gets equipped, or the
// inventory closes. A backpack inside a backpack stays blocked by Adventure Backpacks itself. The open backpack's slot
// has the game's grey "equipped" background while it is open.
// Light: a click handler on the inventory slots, and a check per frame only while a backpack is open this way.
using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace BackpackQuickOpen
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.backpackquickopen";
        public const string Name = "Digi's Backpack Quick Open";
        public const string Version = "1.0.0";
        private const string ProxyName = "AB_BackpackProxy_QuickOpen";

        private static ConfigEntry<bool> _enabled;
        private static BepInEx.Logging.ManualLogSource _log;
        private static MethodInfo _isBackpack, _isEquipped, _isThisEquipped, _openWorn, _data, _getOrCreate, _getInventory, _save, _sizing;
        private static FieldInfo _loading, _backpackIsOpen;
        private static readonly MethodInfo CloseContainer = AccessTools.Method(typeof(InventoryGui), "CloseContainer");
        private static readonly MethodInfo ButtonPos = AccessTools.Method(typeof(InventoryGrid), "GetButtonPos");
        private static readonly MethodInfo UpdateTotalWeight = AccessTools.Method(typeof(Inventory), "UpdateTotalWeight");
        private static readonly AccessTools.FieldRef<InventoryGrid, List<InventoryElement>> Elements =
            AccessTools.FieldRefAccess<InventoryGrid, List<InventoryElement>>("m_elements");
        private static readonly AccessTools.FieldRef<InventoryGui, Container> CurrentContainer =
            AccessTools.FieldRefAccess<InventoryGui, Container>("m_currentContainer");
        private static readonly AccessTools.FieldRef<Container, ZNetView> ContainerView =
            AccessTools.FieldRefAccess<Container, ZNetView>("m_nview");

        private static readonly HashSet<UIInputHandler> Hooked = new HashSet<UIInputHandler>();
        private static InventoryElement _hookedFirst;
        private static int _hookedCount;

        private static Container _proxy;
        private static ItemDrop.ItemData _open;
        private static object _openComponent;
        private static Inventory _openInventory;

        private void Awake()
        {
            _log = Logger;
            _enabled = Config.Bind("General", "Middle-click opens backpacks", true,
                "Middle-click a backpack in your inventory to open its storage without wearing it. Middle-click it again to " +
                "close it, or another backpack to switch. While you wear a backpack, only that one opens this way.");
            var items = AccessTools.TypeByName("AdventureBackpacks.Extensions.ItemDataExtensions");
            var players = AccessTools.TypeByName("AdventureBackpacks.Extensions.PlayerExtensions");
            var component = AccessTools.TypeByName("AdventureBackpacks.Components.BackpackComponent");
            _isBackpack = AccessTools.Method(items, "IsBackpack", new[] { typeof(ItemDrop.ItemData) });
            _isEquipped = AccessTools.Method(players, "IsBackpackEquipped", new[] { typeof(Player) });
            _isThisEquipped = AccessTools.Method(players, "IsThisBackpackEquipped", new[] { typeof(Player), typeof(ItemDrop.ItemData) });
            _openWorn = AccessTools.Method(players, "OpenBackpack", new[] { typeof(Player), typeof(InventoryGui) });
            _data = AccessTools.Method(AccessTools.TypeByName("Vapok.Common.Managers.ItemExtensions"), "Data", new[] { typeof(ItemDrop.ItemData) });
            var info = AccessTools.TypeByName("Vapok.Common.Managers.ItemInfo");
            _getOrCreate = info != null && component != null ? AccessTools.Method(info, "GetOrCreate")?.MakeGenericMethod(component) : null;
            _getInventory = AccessTools.Method(component, "GetInventory");
            _save = AccessTools.Method(component, "Save", new[] { typeof(Inventory) });
            _sizing = AccessTools.Method(component, "UpdateContainerSizing");
            _loading = AccessTools.Field(component, "IsLoadingInventory");
            _backpackIsOpen = AccessTools.Field(AccessTools.TypeByName("AdventureBackpacks.Patches.InventoryGuiPatches"), "BackpackIsOpen");
            if (_isBackpack == null || _isEquipped == null || _isThisEquipped == null || _openWorn == null || _data == null ||
                _getOrCreate == null || _getInventory == null || _save == null || _sizing == null || _loading == null || _backpackIsOpen == null ||
                CloseContainer == null || ButtonPos == null || UpdateTotalWeight == null)
            {
                Logger.LogWarning("Backpack Quick Open is off: this Adventure Backpacks version has no backpack code we know.");
                return;
            }
            PatchModule(typeof(Plugin));
        }

        private void OnDestroy()
        {
            Close(InventoryGui.instance);
            foreach (var handler in Hooked)
                if (handler != null) handler.m_onMiddleClick = (Action<UIInputHandler>)Delegate.Remove(handler.m_onMiddleClick, new Action<UIInputHandler>(MiddleClick));
            Hooked.Clear();
            _hookedFirst = null;
            _hookedCount = 0;
            if (_proxy != null) Destroy(_proxy.gameObject);
            _proxy = null;
        }

        // The open backpack's view ends with the inventory, when the backpack leaves it, or when a backpack is equipped.
        private void Update()
        {
            if (_open == null) return;
            var gui = InventoryGui.instance;
            var player = Player.m_localPlayer;
            if (gui == null || player == null || !InventoryGui.IsVisible() || CurrentContainer(gui) != _proxy ||
                !player.GetInventory().GetAllItems().Contains(_open) || (bool)_isEquipped.Invoke(null, new object[] { player }))
                Close(gui);
        }

        // The player grid's slots get a middle-click handler once (again when the grid rebuilds them).
        [HarmonyPatch(typeof(InventoryGrid), "UpdateGui")]
        [HarmonyPostfix]
        private static void HookSlots(InventoryGrid __instance)
        {
            if (InventoryGui.instance == null || __instance != InventoryGui.instance.m_playerGrid) return;
            var elements = Elements(__instance);
            if (elements == null || elements.Count == 0 || (elements.Count == _hookedCount && elements[0] == _hookedFirst)) return;
            _hookedFirst = elements[0];
            _hookedCount = elements.Count;
            foreach (var element in elements)
            {
                var handler = element != null ? element.GetComponent<UIInputHandler>() : null;
                if (handler == null || !Hooked.Add(handler)) continue;
                handler.m_onMiddleClick = (Action<UIInputHandler>)Delegate.Combine(handler.m_onMiddleClick, new Action<UIInputHandler>(MiddleClick));
            }
        }

        // The open backpack's slot gets the game's own grey "equipped" background, so you see which backpack is open.
        [HarmonyPatch(typeof(InventoryGrid), "UpdateGui")]
        [HarmonyPostfix]
        private static void MarkOpen(InventoryGrid __instance)
        {
            if (_open == null || InventoryGui.instance == null || __instance != InventoryGui.instance.m_playerGrid) return;
            var inventory = __instance.GetInventory();
            var elements = Elements(__instance);
            if (inventory == null || elements == null || !inventory.GetAllItems().Contains(_open)) return;
            var index = _open.m_gridPos.y * inventory.GetWidth() + _open.m_gridPos.x;
            if (index >= 0 && index < elements.Count && elements[index] != null && elements[index].m_equiped != null)
                elements[index].m_equiped.enabled = true;
        }

        private static void MiddleClick(UIInputHandler handler)
        {
            var gui = InventoryGui.instance;
            var player = Player.m_localPlayer;
            if (!_enabled.Value || gui == null || player == null || handler == null) return;
            try
            {
                var pos = (Vector2i)ButtonPos.Invoke(gui.m_playerGrid, new object[] { handler.gameObject });
                var item = player.GetInventory().GetItemAt(pos.x, pos.y);
                if (item == null || !(bool)_isBackpack.Invoke(null, new object[] { item })) return;
                if (item == _open)
                {
                    Close(gui);
                    return;
                }
                if ((bool)_isEquipped.Invoke(null, new object[] { player }))
                {
                    if ((bool)_isThisEquipped.Invoke(null, new object[] { player, item }))
                    {
                        if (gui.IsContainerOpen()) CloseContainer.Invoke(gui, null);
                        _openWorn.Invoke(null, new object[] { player, gui });
                    }
                    else player.Message(MessageHud.MessageType.Center, "Take off your backpack to open another one");
                    return;
                }
                Open(gui, player, item);
            }
            catch (Exception e)
            {
                _log?.LogWarning("Could not open the backpack: " + e.Message);
            }
        }

        private static void Open(InventoryGui gui, Player player, ItemDrop.ItemData item)
        {
            var info = _data.Invoke(null, new object[] { item });
            var component = info != null ? _getOrCreate.Invoke(info, new object[] { "" }) : null;
            var inventory = component != null ? _getInventory.Invoke(component, null) as Inventory : null;
            var proxy = Proxy(player);
            if (inventory == null || proxy == null) return;
            Close(gui);
            if (gui.IsContainerOpen()) CloseContainer.Invoke(gui, null); // a chest: let it go first
            _sizing.Invoke(component, new object[] { proxy }); // the backpack's inventory, name, size and background
            _open = item;
            _openComponent = component;
            _openInventory = inventory;
            // Adventure Backpacks' own "backpack open" flag, set as its OpenBackpack does: the Store Nearby button
            // under the backpack (Backpack Store Nearby) and its backpack key go by it.
            _backpackIsOpen.SetValue(null, true);
            gui.Show(proxy, 1);
        }

        private static void Close(InventoryGui gui)
        {
            if (_open == null) return;
            SaveOpen();
            if (gui != null && _proxy != null && CurrentContainer(gui) == _proxy)
            {
                CloseContainer.Invoke(gui, null);
                _backpackIsOpen.SetValue(null, false);
            }
            _open = null;
            _openComponent = null;
            _openInventory = null;
        }

        // Every change to the open backpack goes into its item at once, and the carried weight follows.
        [HarmonyPatch(typeof(Inventory), "Changed")]
        [HarmonyPostfix]
        private static void Changed(Inventory __instance)
        {
            if (__instance != null && __instance == _openInventory) SaveOpen();
        }

        private static void SaveOpen()
        {
            if (_openComponent == null || _openInventory == null || (bool)_loading.GetValue(_openComponent)) return;
            _save.Invoke(_openComponent, new object[] { _openInventory });
            var player = Player.m_localPlayer;
            if (player != null) UpdateTotalWeight.Invoke(player.GetInventory(), null);
        }

        // The stand-in chest on the player, made like Adventure Backpacks' own (its name keeps it off the network).
        private static Container Proxy(Player player)
        {
            if (_proxy != null && _proxy.transform.parent == player.transform) return _proxy;
            var go = new GameObject(ProxyName);
            go.transform.SetParent(player.transform, false);
            var view = go.AddComponent<ZNetView>();
            _proxy = go.AddComponent<Container>();
            _proxy.m_name = "$vapok_mod_backpack";
            ContainerView(_proxy) = view;
            return _proxy;
        }
    }
}
