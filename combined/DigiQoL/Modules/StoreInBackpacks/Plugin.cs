// Store In Backpacks: a small "To Backpacks" button left of the "Store Nearby" button (our Store Nearby sub-mod), in the
// inventory on its own. It does what Store Nearby does, into your Adventure Backpacks instead of chests, in three steps:
//  1. every item Store Nearby would store goes into a backpack in your inventory that already holds that item (its
//     stacks first, then free slots);
//  2. what is left of those goes into a backpack set to "Auto: Materials";
//  3. Epic Loot gear (magic and unidentified items) goes into a backpack set to "Auto: Loot", unless it is worn, in a row
//     Store Nearby keeps (the hotbar) or favourited (Epic Loot's favourite mark, from Item Favorite Framework).
// Store Nearby's own rules decide what may go in steps 1 and 2 (kept rows, worn gear, quest items, its always-keep and
// always-store lists; asked from its Sendable). Normal (not magic) gear always stays, as with Store Nearby. A backpack never goes into a backpack. Carried
// backpacks are filled before the worn one; the worn one only while "Fill the worn backpack" is on.
// A backpack's Auto mode is set with a small button in its window when it is open (worn, or opened with Backpack Quick
// Open's middle-click; it stands left of Place stacks, where the backpack's name is hidden meanwhile), saved on the backpack item itself (it stays with it when moved or traded) and shown in its
// tooltip. The To Backpacks button shows only with the inventory open on its own (no chest, no crafting station, no open
// backpack, as Store Nearby itself) and at least one backpack to fill.
// Adventure Backpacks saves a backpack you do not wear only while you wear one, so every changed backpack is saved here.
// All other mods are reached by reflection only. Light: a check four times a second while the inventory is open.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StoreInBackpacks
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.storeinbackpacks";
        public const string Name = "Digi's Store In Backpacks";
        public const string Version = "1.0.0";
        private const string Label = "To Backpacks";
        private const string ModeKey = "digi.storeinbackpacks.mode";
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private enum Mode { Off, Materials, Loot }

        private sealed class Target
        {
            public ItemDrop.ItemData Item;
            public object Component;
            public Inventory Inventory;
            public Mode Mode;
            public bool Changed;
        }

        private static ConfigEntry<bool> _worn;
        private static BepInEx.Logging.ManualLogSource _log;

        // Adventure Backpacks
        private static MethodInfo _isBackpack, _isThisEquipped, _data, _getOrCreate, _getInventory, _save;
        private static FieldInfo _loading, _backpackIsOpen;
        // Store Nearby (Inventory and QoL), by name
        private static FieldInfo _storeNearbyButton, _storeNearbyRunning;
        private static MethodInfo _sendable, _keepsRow;
        // Epic Loot
        private static MethodInfo _isMagic, _isUnidentified, _isFavourite;

        private static readonly MethodInfo UpdateTotalWeight = AccessTools.Method(typeof(Inventory), "UpdateTotalWeight");
        private static readonly AccessTools.FieldRef<InventoryGui, Container> CurrentContainer =
            AccessTools.FieldRefAccess<InventoryGui, Container>("m_currentContainer");

        private static Button _button, _modeButton;
        private static float _nextCheck;
        private static bool _hasTarget;
        private static ItemDrop.ItemData _openBackpack;
        private static TMP_Text _hiddenName; // the backpack window's title, hidden while the Auto button stands in its place
        private static Container _seenContainer;
        private static Inventory _seenInventory;

        private void Awake()
        {
            _log = Logger;
            _worn = Config.Bind("General", "Fill the worn backpack", true,
                "The backpack you wear also gets items (after the backpacks you carry), as its Auto mode says. Off: only the " +
                "backpacks you carry in your inventory are filled.");
        }

        // The other mods are looked up once every plugin has loaded: BepInEx loads them in ID order, and as Hexium singles
        // Store Nearby loads after this one.
        private void Start()
        {
            var items = AccessTools.TypeByName("AdventureBackpacks.Extensions.ItemDataExtensions");
            var players = AccessTools.TypeByName("AdventureBackpacks.Extensions.PlayerExtensions");
            var component = AccessTools.TypeByName("AdventureBackpacks.Components.BackpackComponent");
            var info = AccessTools.TypeByName("Vapok.Common.Managers.ItemInfo");
            _isBackpack = AccessTools.Method(items, "IsBackpack", new[] { typeof(ItemDrop.ItemData) });
            _isThisEquipped = AccessTools.Method(players, "IsThisBackpackEquipped", new[] { typeof(Player), typeof(ItemDrop.ItemData) });
            _data = AccessTools.Method(AccessTools.TypeByName("Vapok.Common.Managers.ItemExtensions"), "Data", new[] { typeof(ItemDrop.ItemData) });
            _getOrCreate = info != null && component != null ? AccessTools.Method(info, "GetOrCreate")?.MakeGenericMethod(component) : null;
            _getInventory = AccessTools.Method(component, "GetInventory");
            _save = AccessTools.Method(component, "Save", new[] { typeof(Inventory) });
            _loading = AccessTools.Field(component, "IsLoadingInventory");
            _backpackIsOpen = AccessTools.Field(AccessTools.TypeByName("AdventureBackpacks.Patches.InventoryGuiPatches"), "BackpackIsOpen");

            var storeNearby = AccessTools.TypeByName("StoreNearby.Plugin");
            if (storeNearby != null)
            {
                _storeNearbyButton = AccessTools.Field(storeNearby, "Button");
                _storeNearbyRunning = AccessTools.Field(storeNearby, "Running");
                _sendable = AccessTools.Method(storeNearby, "Sendable", new[] { typeof(ItemDrop.ItemData), typeof(Inventory) });
                _keepsRow = AccessTools.Method(storeNearby, "KeepsRow", new[] { typeof(int) });
            }

            var epicItems = AccessTools.TypeByName("EpicLoot.ItemDataExtensions");
            _isMagic = AccessTools.Method(epicItems, "IsMagic", new[] { typeof(ItemDrop.ItemData) });
            _isUnidentified = AccessTools.Method(epicItems, "IsUnidentified", new[] { typeof(ItemDrop.ItemData) });
            _isFavourite = AccessTools.Method(AccessTools.TypeByName("EpicLoot.Compatibility.ItemFavorites"), "IsProtected",
                new[] { typeof(ItemDrop.ItemData) });

            if (_isBackpack == null || _isThisEquipped == null || _data == null || _getOrCreate == null || _getInventory == null ||
                _save == null || _loading == null || _storeNearbyButton == null || UpdateTotalWeight == null)
            {
                Logger.LogWarning("Store In Backpacks is off: this Adventure Backpacks version has no code we know, or Store Nearby is missing.");
                enabled = false;
                return;
            }
            if (_sendable == null)
                Logger.LogWarning("Store Nearby's rules were not found: the hotbar, worn gear and quest items stay, everything else is stored.");
            var tooltip = typeof(ItemDrop.ItemData).GetMethods(BindingFlags.Static | BindingFlags.Public)
                .Where(m => m.Name == nameof(ItemDrop.ItemData.GetTooltip) && m.ReturnType == typeof(string) &&
                            m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(ItemDrop.ItemData))
                .OrderByDescending(m => m.GetParameters().Length).FirstOrDefault();
            if (tooltip != null) ModuleHarmony.Patch(tooltip, postfix: new HarmonyMethod(typeof(Plugin), nameof(TooltipLine)));
            PatchModule(typeof(Plugin));
        }

        private void OnDestroy()
        {
            if (_button != null) Destroy(_button.gameObject);
            HideModeButton();
            if (_modeButton != null) Destroy(_modeButton.gameObject);
            _button = _modeButton = null;
            _openBackpack = null;
            _seenContainer = null;
            _seenInventory = null;
        }

        // Right after the game fills the container window (every frame, before it is drawn): a backpack's window gets the
        // Auto button in place of its title at once, so the title never shows.
        [HarmonyPatch(typeof(InventoryGui), "UpdateContainer")]
        [HarmonyPostfix]
        private static void AfterContainerUpdate(InventoryGui __instance)
        {
            var player = Player.m_localPlayer;
            var container = CurrentContainer(__instance);
            if (player == null || container == null) return;
            var backpack = CurrentBackpack(player, container);
            if (backpack != null && player.GetInventory().ContainsItem(backpack)) ShowModeButton(__instance);
            else if (_hiddenName != null) HideModeButton();
        }

        // The open backpack, worked out again as soon as the window shows another container or storage.
        private static ItemDrop.ItemData CurrentBackpack(Player player, Container container)
        {
            var shown = container.GetInventory();
            if (container != _seenContainer || shown != _seenInventory)
            {
                _seenContainer = container;
                _seenInventory = shown;
                _openBackpack = OpenBackpack(player, container);
            }
            return _openBackpack;
        }

        // --- The buttons -----------------------------------------------------------------------------------------

        private void Update()
        {
            var gui = InventoryGui.instance;
            var player = Player.m_localPlayer;
            if (gui == null || player == null || !InventoryGui.IsVisible())
            {
                Hide(_button);
                HideModeButton();
                _openBackpack = null;
                _nextCheck = 0f; // checked again at once when the inventory opens
                return;
            }
            var container = CurrentContainer(gui);
            var due = Time.unscaledTime >= _nextCheck;
            if (due) _nextCheck = Time.unscaledTime + 0.25f;

            // An open backpack: its Auto button.
            if (container != null)
            {
                CurrentBackpack(player, container);
                if (due) _openBackpack = OpenBackpack(player, container); // also when Adventure Backpacks marks it open later
                if (_openBackpack != null && player.GetInventory().ContainsItem(_openBackpack)) ShowModeButton(gui);
                else HideModeButton();
            }
            else
            {
                _openBackpack = null;
                _seenContainer = null;
                _seenInventory = null;
                HideModeButton();
            }

            // The inventory on its own: To Backpacks, left of Store Nearby.
            var storeNearby = _storeNearbyButton.GetValue(null) as Button;
            var show = storeNearby != null && storeNearby.gameObject.activeInHierarchy && container == null &&
                       player.GetCurrentCraftingStation() == null;
            if (show && due) _hasTarget = Targets(player).Count > 0;
            if (!show || !_hasTarget)
            {
                Hide(_button);
                return;
            }
            if (_button == null) _button = Copy(storeNearby, "DigiStoreInBackpacks", Label, StoreClicked);
            if (_button == null) return;
            if (!_button.gameObject.activeSelf) _button.gameObject.SetActive(true);
            PlaceLeftOf((RectTransform)_button.transform, (RectTransform)storeNearby.transform, 0.75f);
        }

        private static void Hide(Button button)
        {
            if (button != null && button.gameObject.activeSelf) button.gameObject.SetActive(false);
        }

        // In the window's title row, left of its Place stacks button; the backpack's name there is hidden meanwhile (the
        // row has Take all on the left, the name in the middle and Place stacks on the right: no room for both).
        private static void ShowModeButton(InventoryGui gui)
        {
            var reference = gui.m_stackAllButton != null && gui.m_stackAllButton.gameObject.activeInHierarchy ? gui.m_stackAllButton : null;
            if (reference == null)
            {
                HideModeButton();
                return;
            }
            if (_modeButton == null) _modeButton = Copy(reference, "DigiBackpackAutoMode", "", CycleMode);
            if (_modeButton == null) return;
            SetLabel(_modeButton, "Auto: " + ModeOf(_openBackpack));
            if (!_modeButton.gameObject.activeSelf) _modeButton.gameObject.SetActive(true);
            PlaceLeftOf((RectTransform)_modeButton.transform, (RectTransform)reference.transform, 1.25f);
            if (gui.m_containerName != null && gui.m_containerName.enabled)
            {
                gui.m_containerName.enabled = false;
                _hiddenName = gui.m_containerName;
            }
        }

        private static void HideModeButton()
        {
            Hide(_modeButton);
            if (_hiddenName != null) _hiddenName.enabled = true;
            _hiddenName = null;
        }

        // A copy of a game or Store Nearby button, with its own label and click.
        private static Button Copy(Button source, string name, string label, UnityEngine.Events.UnityAction click)
        {
            try
            {
                var go = Instantiate(source.gameObject, source.transform.parent, false);
                go.name = name;
                foreach (var text in go.GetComponentsInChildren<TMP_Text>(true))
                {
                    text.enableAutoSizing = true;
                    text.fontSizeMin = 10f;
                    text.fontSizeMax = Mathf.Max(12f, text.fontSize * 0.85f);
                }
                foreach (var text in go.GetComponentsInChildren<Text>(true))
                {
                    text.resizeTextForBestFit = true;
                    text.resizeTextMinSize = 10;
                }
                var button = go.GetComponent<Button>();
                button.onClick = new Button.ButtonClickedEvent();
                button.onClick.AddListener(click);
                button.interactable = true;
                SetLabel(button, label);
                return button;
            }
            catch (Exception e)
            {
                _log?.LogWarning($"Could not make the {name} button: " + e.Message);
                return null;
            }
        }

        private static void SetLabel(Button button, string label)
        {
            foreach (var text in button.GetComponentsInChildren<TMP_Text>(true))
                if (text.text != label) text.text = label;
            foreach (var text in button.GetComponentsInChildren<Text>(true))
                if (text.text != label) text.text = label;
        }

        // Left of another button, "scale" times its width, following it when it moves.
        private static void PlaceLeftOf(RectTransform rect, RectTransform other, float scale)
        {
            if (rect.parent != other.parent) rect.SetParent(other.parent, false);
            rect.anchorMin = other.anchorMin;
            rect.anchorMax = other.anchorMax;
            rect.pivot = other.pivot;
            var size = other.rect.size;
            var width = size.x * scale;
            rect.sizeDelta = new Vector2(width, size.y);
            // Both are placed by their pivot: move left by the other's width right of its pivot plus this one's left of it.
            var left = size.x * other.pivot.x + width * (1f - other.pivot.x) + 6f;
            rect.anchoredPosition = other.anchoredPosition - new Vector2(left, 0f);
        }

        // --- Auto mode of a backpack -----------------------------------------------------------------------------

        private static Mode ModeOf(ItemDrop.ItemData backpack)
        {
            if (backpack?.m_customData == null || !backpack.m_customData.TryGetValue(ModeKey, out var value)) return Mode.Off;
            return value == "materials" ? Mode.Materials : value == "loot" ? Mode.Loot : Mode.Off;
        }

        private static void CycleMode()
        {
            var backpack = _openBackpack;
            if (backpack == null) return;
            var next = ModeOf(backpack) == Mode.Off ? Mode.Materials : ModeOf(backpack) == Mode.Materials ? Mode.Loot : Mode.Off;
            if (next == Mode.Off) backpack.m_customData.Remove(ModeKey);
            else backpack.m_customData[ModeKey] = next == Mode.Materials ? "materials" : "loot";
            if (_modeButton != null) SetLabel(_modeButton, "Auto: " + next);
            _hasTarget = false;
            _nextCheck = 0f;
        }

        // The backpack whose window is open: the one whose storage the window shows, or else the worn one when Adventure
        // Backpacks says its backpack is open.
        private static ItemDrop.ItemData OpenBackpack(Player player, Container container)
        {
            var shown = container.GetInventory();
            ItemDrop.ItemData worn = null;
            foreach (var item in player.GetInventory().GetAllItems())
            {
                if (!IsBackpack(item)) continue;
                if (shown != null && InventoryOf(item) == shown) return item;
                if ((bool)_isThisEquipped.Invoke(null, new object[] { player, item })) worn = item;
            }
            var open = _backpackIsOpen != null && (bool)_backpackIsOpen.GetValue(null);
            return open ? worn : null;
        }

        private static string TooltipLine(string __result, ItemDrop.ItemData __0)
        {
            var mode = ModeOf(__0);
            if (mode == Mode.Off) return __result;
            var what = mode == Mode.Materials ? "takes new materials" : "takes Epic Loot gear";
            return __result + $"\n<color=orange>To Backpacks:</color> {what}";
        }

        // --- Storing ---------------------------------------------------------------------------------------------

        private static bool IsBackpack(ItemDrop.ItemData item)
        {
            try
            {
                return item != null && (bool)_isBackpack.Invoke(null, new object[] { item });
            }
            catch
            {
                return false;
            }
        }

        private static object ComponentOf(ItemDrop.ItemData backpack)
        {
            var data = _data.Invoke(null, new object[] { backpack });
            return data != null ? _getOrCreate.Invoke(data, new object[] { "" }) : null; // GetOrCreate<T>(string key = "")
        }

        private static Inventory InventoryOf(ItemDrop.ItemData backpack)
        {
            try
            {
                var component = ComponentOf(backpack);
                return component != null ? _getInventory.Invoke(component, null) as Inventory : null;
            }
            catch
            {
                return null;
            }
        }

        // The backpacks in your inventory, carried ones first (in inventory order), the worn one last.
        private static List<Target> Targets(Player player)
        {
            var carried = new List<Target>();
            Target worn = null;
            foreach (var item in player.GetInventory().GetAllItems())
            {
                if (!IsBackpack(item)) continue;
                var isWorn = (bool)_isThisEquipped.Invoke(null, new object[] { player, item });
                if (isWorn && !_worn.Value) continue;
                try
                {
                    var component = ComponentOf(item);
                    var inventory = component != null ? _getInventory.Invoke(component, null) as Inventory : null;
                    if (inventory == null) continue;
                    var target = new Target { Item = item, Component = component, Inventory = inventory, Mode = ModeOf(item) };
                    if (isWorn) worn = target;
                    else carried.Add(target);
                }
                catch (Exception e)
                {
                    _log?.LogWarning("Could not read a backpack: " + e.Message);
                }
            }
            if (worn != null) carried.Add(worn);
            return carried;
        }

        private static void StoreClicked()
        {
            var player = Player.m_localPlayer;
            if (player == null || player.IsDead()) return;
            try
            {
                Store(player);
            }
            catch (Exception e)
            {
                _log?.LogError("To Backpacks failed: " + e);
            }
        }

        private static void Store(Player player)
        {
            var inventory = player.GetInventory();
            var targets = Targets(player);
            if (targets.Count == 0) return;
            int moved = 0, loot = 0;

            // 1 and 2: what Store Nearby would store, into backpacks that hold it, then into "Materials" backpacks.
            foreach (var item in inventory.GetAllItems().ToList())
            {
                if (item?.m_shared == null || item.m_stack <= 0 || IsBackpack(item)) continue;
                var amount = Sendable(inventory, item);
                foreach (var target in targets)
                {
                    if (amount <= 0) break;
                    if (Holds(target.Inventory, item)) amount -= Put(inventory, target, item, amount, ref moved);
                }
                foreach (var target in targets)
                {
                    if (amount <= 0) break;
                    if (target.Mode == Mode.Materials) amount -= Put(inventory, target, item, amount, ref moved);
                }
            }

            // 3: Epic Loot gear into "Loot" backpacks.
            if (targets.Any(t => t.Mode == Mode.Loot))
                foreach (var item in inventory.GetAllItems().ToList())
                {
                    if (!IsLoot(player, inventory, item)) continue;
                    foreach (var target in targets)
                    {
                        if (target.Mode != Mode.Loot || !inventory.ContainsItem(item)) continue;
                        var before = moved;
                        Put(inventory, target, item, item.m_stack, ref moved);
                        if (moved > before)
                        {
                            loot++;
                            if (!inventory.ContainsItem(item)) break;
                        }
                    }
                }

            foreach (var target in targets.Where(t => t.Changed))
                if (!(bool)_loading.GetValue(target.Component)) _save.Invoke(target.Component, new object[] { target.Inventory });
            UpdateTotalWeight.Invoke(inventory, null);
            string text;
            if (moved == 0) text = "Nothing to store: no backpack holds or takes any of these items";
            else
            {
                text = $"Stored {moved} item{(moved == 1 ? "" : "s")} in your backpacks";
                if (loot > 0) text += $" ({loot} Epic Loot piece{(loot == 1 ? "" : "s")})";
            }
            player.Message(MessageHud.MessageType.Center, text);
        }

        // Moves up to "amount" of the item into the backpack, as much as fits; returns how many went.
        private static int Put(Inventory from, Target target, ItemDrop.ItemData item, int amount, ref int moved)
        {
            var n = Mathf.Min(amount, Room(target.Inventory, item));
            if (n <= 0) return 0;
            int added;
            if (n >= item.m_stack)
            {
                // The whole item: moved as it is (an Epic Loot item keeps everything). The game's move fills stacks first
                // and puts the rest in a free slot; whatever finds no room stays in the source, so nothing is lost.
                var before = item.m_stack;
                target.Inventory.MoveItemToThis(from, item);
                added = from.ContainsItem(item) ? before - item.m_stack : before;
            }
            else
            {
                var part = item.Clone();
                part.m_stack = n;
                var ok = target.Inventory.AddItem(part);
                // AddItem fills stacks one by one and then puts the rest in a free slot; without one it keeps that rest.
                added = ok ? n : n - part.m_stack;
                if (added > 0) from.RemoveItem(item, added);
            }
            if (added <= 0) return 0;
            moved += added;
            target.Changed = true;
            return added;
        }

        private static bool Holds(Inventory inventory, ItemDrop.ItemData item)
        {
            foreach (var other in inventory.GetAllItems())
                if (other?.m_shared != null && other.m_shared.m_name == item.m_shared.m_name) return true;
            return false;
        }

        // How many of the item fit: the room in its stacks there, and free slots.
        private static int Room(Inventory inventory, ItemDrop.ItemData item)
        {
            var empty = inventory.GetEmptySlots();
            if (item.m_shared.m_maxStackSize <= 1) return empty > 0 ? item.m_stack : 0;
            return inventory.FindFreeStackSpace(item.m_shared.m_name, item.m_worldLevel) + empty * item.m_shared.m_maxStackSize;
        }

        // Epic Loot gear that may go: magic or unidentified, not worn, not in a row Store Nearby keeps, not favourited.
        private static bool IsLoot(Player player, Inventory inventory, ItemDrop.ItemData item)
        {
            if (item?.m_shared == null || _isMagic == null || IsBackpack(item) || item.m_equipped || player.IsItemEquiped(item)) return false;
            if (KeptRow(item.m_gridPos.y, inventory.GetHeight())) return false;
            try
            {
                var magic = item.IsEquipable() && (bool)_isMagic.Invoke(null, new object[] { item });
                var unidentified = _isUnidentified != null && (bool)_isUnidentified.Invoke(null, new object[] { item });
                if (!magic && !unidentified) return false;
                return _isFavourite == null || !(bool)_isFavourite.Invoke(null, new object[] { item });
            }
            catch
            {
                return false;
            }
        }

        private static bool StoreNearbyRuns => _storeNearbyRunning != null && (bool)_storeNearbyRunning.GetValue(null);

        // The inventory rows Store Nearby keeps (the hotbar, and as many more as its setting says).
        private static bool KeptRow(int row, int height)
        {
            try
            {
                if (_keepsRow != null && StoreNearbyRuns) return (bool)_keepsRow.Invoke(null, new object[] { row });
            }
            catch
            {
                // the hotbar below
            }
            return row == 0;
        }

        // How much of this item may go: Store Nearby's own rules decide (all of it or none).
        private static int Sendable(Inventory inventory, ItemDrop.ItemData item)
        {
            if (_sendable != null && StoreNearbyRuns)
            {
                try
                {
                    return (bool)_sendable.Invoke(null, new object[] { item, inventory }) ? item.m_stack : 0;
                }
                catch (Exception e)
                {
                    _log?.LogWarning("Store Nearby's rules failed for an item: " + e.Message);
                }
            }
            // Without Store Nearby's rules: the hotbar, worn gear and quest items stay.
            return item.m_equipped || item.m_shared.m_questItem || item.m_gridPos.y == 0 ? 0 : item.m_stack;
        }
    }
}
