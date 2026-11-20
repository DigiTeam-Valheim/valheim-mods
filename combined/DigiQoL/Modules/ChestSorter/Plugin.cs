// Chests sort themselves: by kind of item (materials, food, meads and potions, fish, ammo, weapons, shields, tools,
// armour, trophies, the rest), then A-Z by name, starting top left and filling row by row without gaps. Partial stacks
// are merged; items that do not stack lie side by side, best quality first.
// When: a chest you open is sorted as soon as it is yours and its contents are current (never while you drag an item),
// and again when you close it (Tab, Esc or walking away), so nothing moves under your hand while it is open: what you
// put in or take out stays where it is until then. After Store Nearby the chests it filled are sorted at once. With
// "Sort after each change while open" (off by default; it was the old behaviour, which players found annoying) the open
// chest is sorted again after each change to its contents, once nothing is being dragged. The
// automated stations' chests are not sorted on every output; they are tidied the next time you open them.
// Only player-built chests (and carts, ship storage) this PC owns and nobody else has open. A chest that is already in order is left untouched; otherwise one save, as for any change you make.
// Light: a chest has at most a few dozen stacks, and nothing runs between those moments.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ChestSorter
{
    public class Plugin : DigiCore.DigiModule
    {
        public enum Order { TypeThenName, Name }

        private static ConfigEntry<bool> _onOpen, _onClose, _whileOpen, _afterStore, _merge;
        private static ConfigEntry<Order> _order;
        private static BepInEx.Logging.ManualLogSource _log;

        private static readonly AccessTools.FieldRef<Container, ZNetView> ChestView = AccessTools.FieldRefAccess<Container, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<InventoryGui, Container> OpenChest = AccessTools.FieldRefAccess<InventoryGui, Container>("m_currentContainer");
        private static readonly AccessTools.FieldRef<InventoryGui, ItemDrop.ItemData> DragItem = AccessTools.FieldRefAccess<InventoryGui, ItemDrop.ItemData>("m_dragItem");
        private static readonly AccessTools.FieldRef<Inventory, List<ItemDrop.ItemData>> Items = AccessTools.FieldRefAccess<Inventory, List<ItemDrop.ItemData>>("m_inventory");
        private static readonly Action<Inventory, bool, bool> Changed =
            AccessTools.MethodDelegate<Action<Inventory, bool, bool>>(AccessTools.Method(typeof(Inventory), "Changed", new[] { typeof(bool), typeof(bool) }));

        // The chest you just opened, tried for a short while until it is ready to sort.
        private static Container _pending;
        private static float _pendingUntil;


        private void Awake()
        {
            _log = Logger;
            _onOpen = Config.Bind("General", "Sort when opening", true,
                "A chest is sorted when you open it.");
            _onClose = Config.Bind("General", "Sort when closing", true,
                "A chest is sorted when you close it (Tab, Esc or walking away). While it is open nothing moves: what you put " +
                "in or take out stays where it is.");
            // A new name for the old "Sort while open" (on by default): items jumping while you take or put them annoyed
            // players, so everyone starts with it off.
            _whileOpen = Config.Bind("General", "Sort after each change while open", false,
                "While a chest is open, it is sorted again each time its contents change (you put something in, take " +
                "something out or move it), a moment after you let go of the item. Items move while you work in the chest.");
            _afterStore = Config.Bind("General", "Sort after Store Nearby", true,
                "After the Store Nearby button, the chests it put items into are sorted.");
            _order = Config.Bind("General", "Sort order", Order.TypeThenName,
                "TypeThenName: grouped by kind (materials, food, meads and potions, fish, ammo, weapons, shields, tools, armour, " +
                "trophies, the rest), A-Z within each group. Name: A-Z only.");
            _merge = Config.Bind("General", "Merge stacks", true,
                "Partial stacks of the same item are merged (3 stacks of 20 wood become one of 60).");
            PatchModule(typeof(Plugin));
        }

        // Store Nearby is looked up once every plugin has loaded (as Hexium singles, Store Nearby loads after this one).
        private void Start()
        {
            var storeNearby = AccessTools.TypeByName("StoreNearby.Plugin");
            var afterStore = storeNearby != null ? AccessTools.Method(storeNearby, "AfterStore", new[] { typeof(List<Container>) }) : null;
            if (afterStore != null) ModuleHarmony.Patch(afterStore, postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterStoreNearby)));
        }

        // --- Triggers ------------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Show), typeof(Container), typeof(int))]
        [HarmonyPostfix]
        private static void Opened(Container container)
        {
            if (!_onOpen.Value || container == null) return;
            if (TrySort(container)) return;
            _pending = container; // not ready yet: tried again for a moment
            _pendingUntil = Time.time + 1f;
        }

        private static bool TrySort(Container chest)
        {
            if (InventoryGui.instance != null && DragItem(InventoryGui.instance) != null || !Ready(chest, true)) return false;
            Sort(chest);
            return true;
        }

        // Sort while open: the open chest's contents changed (not by a sort of ours); sorted again once nothing is dragged.
        private static Container _changed;
        private static float _changedAt;
        private static bool _sorting;

        [HarmonyPatch(typeof(Container), "OnContainerChanged")]
        [HarmonyPostfix]
        private static void ContentsChanged(Container __instance)
        {
            if (_sorting || !_whileOpen.Value || InventoryGui.instance == null || OpenChest(InventoryGui.instance) != __instance) return;
            _changed = __instance;
            _changedAt = Time.unscaledTime;
        }

        // Sort when closing: the chest the inventory showed last frame and no longer does was closed (Hide, walking away,
        // another chest opened). By then a dragged item is back and the game has marked the chest as not in use.
        private static Container _shown;

        private void Update()
        {
            var shown = InventoryGui.instance != null ? OpenChest(InventoryGui.instance) : null;
            if (_shown != shown)
            {
                var closed = _shown;
                _shown = shown;
                if (_onClose.Value && closed != null && Ready(closed, false)) Sort(closed);
            }
            if (_changed != null && Time.unscaledTime - _changedAt >= 0.15f)
            {
                var gui = InventoryGui.instance;
                if (gui == null || !InventoryGui.IsVisible() || OpenChest(gui) != _changed || !_whileOpen.Value) _changed = null;
                else if (DragItem(gui) == null && TrySort(_changed)) _changed = null;
            }
            if (_pending == null) return;
            if (Time.time > _pendingUntil || InventoryGui.instance == null || OpenChest(InventoryGui.instance) != _pending)
            {
                _pending = null;
                return;
            }
            if (TrySort(_pending)) _pending = null;
        }

        // After Store Nearby: the chests it filled (ours, nobody has them open: it only fills those).
        private static void AfterStoreNearby(List<Container> chests)
        {
            if (!_afterStore.Value || chests == null) return;
            foreach (var chest in chests)
                if (chest != null && Ready(chest, false)) Sort(chest);
        }

        // Yours to change and its contents the latest: owned here, what it shows is what its network data holds, and open
        // only by you (or, after Store Nearby, by nobody).
        private static bool Ready(Container chest, bool openByMe)
        {
            if (chest == null || chest.GetInventory() == null) return false;
            var piece = chest.GetComponentInParent<Piece>();
            if (piece == null || !piece.IsPlacedByPlayer()) return false; // no tombstones, dungeon chests or backpacks
            var view = ChestView(chest);
            if (view == null || !view.IsValid() || !view.IsOwner()) return false;
            if (!openByMe && chest.IsInUse()) return false;
            return ContentsCurrent(chest, view.GetZDO());
        }

        // The chest's items as saved on the network equal the ones it shows. (Not its data revision: opening a chest sets
        // its "in use" value, which raises the revision without touching the items, and while open it never reloads.)
        // Byte for byte first; if that differs, the same items in the same places count: Epic Loot gear's durability
        // changes a little in memory after loading without a save, which made chests with magic items never sort.
        private static bool ContentsCurrent(Container chest, ZDO zdo)
        {
            var inv = chest.GetInventory();
            var stored = zdo.GetByteArray(ZDOVars.s_items);
            if (stored == null) return inv.NrOfItems() == 0;
            var pkg = new ZPackage();
            inv.Save(pkg);
            var shown = pkg.GetArray();
            if (shown.Length == stored.Length)
            {
                var same = true;
                for (var i = 0; i < shown.Length && same; i++)
                    if (shown[i] != stored[i]) same = false;
                if (same) return true;
            }
            try
            {
                var saved = new Inventory("digi_sort_check", null, inv.GetWidth(), inv.GetHeight());
                saved.Load(new ZPackage(stored));
                var mine = Items(inv);
                var theirs = Items(saved);
                if (mine == null || theirs == null || mine.Count != theirs.Count) return false;
                foreach (var item in mine)
                {
                    var other = theirs.FirstOrDefault(i => i.m_gridPos.x == item.m_gridPos.x && i.m_gridPos.y == item.m_gridPos.y);
                    if (other == null || other.m_shared.m_name != item.m_shared.m_name || other.m_stack != item.m_stack ||
                        other.m_quality != item.m_quality || other.m_variant != item.m_variant) return false;
                }
                return true;
            }
            catch (Exception e)
            {
                _log?.LogWarning($"Could not compare {chest.name} with its saved items: {e.Message}");
                return false;
            }
        }

        // --- Sorting -------------------------------------------------------------------------------------------

        private sealed class Slot
        {
            public ItemDrop.ItemData Item;
            public string Name;
            public int Group;
            public int Stack; // after merging
        }

        // Sorts the chest in place; one change (and so one save) only when the order really changes.
        internal static bool Sort(Container chest)
        {
            _sorting = true;
            try
            {
                return SortInventory(chest.GetInventory());
            }
            catch (Exception e)
            {
                _log?.LogWarning($"Could not sort {chest.name}: {e.Message}");
                return false;
            }
            finally
            {
                _sorting = false;
            }
        }

        internal static bool SortInventory(Inventory inv)
        {
            var list = Items(inv);
            if (list == null || list.Count == 0) return false;
            var isProtected = StoreRules.ProtectedSlots(inv);
            var width = inv.GetWidth();
            var height = inv.GetHeight();

            // Items in the marker slot stay where they are; everything else is sorted.
            var movable = list.Where(i => i != null && (isProtected == null || !isProtected(i.m_gridPos.x, i.m_gridPos.y))).ToList();
            var stacks = new Dictionary<ItemDrop.ItemData, int>();
            foreach (var item in movable) stacks[item] = item.m_stack;
            var gone = new List<ItemDrop.ItemData>();
            if (_merge.Value) Merge(movable, stacks, gone);

            var slots = movable.Where(i => !gone.Contains(i)).Select(i => new Slot
            {
                Item = i,
                Name = Localization.instance != null ? Localization.instance.Localize(i.m_shared.m_name) : i.m_shared.m_name,
                Group = _order.Value == Order.TypeThenName ? GroupOf(i) : 0,
                Stack = stacks[i],
            }).ToList();
            slots.Sort(Compare);

            // Reading order from the top left, skipping the marker slot.
            var targets = new List<Vector2i>();
            for (var y = 0; y < height && targets.Count < slots.Count; y++)
                for (var x = 0; x < width && targets.Count < slots.Count; x++)
                    if (isProtected == null || !isProtected(x, y)) targets.Add(new Vector2i(x, y));
            if (targets.Count < slots.Count) return false; // cannot happen (merging only frees slots); leave it be

            var changed = gone.Count > 0 || stacks.Any(p => p.Key.m_stack != p.Value);
            for (var i = 0; i < slots.Count && !changed; i++)
                if (slots[i].Item.m_gridPos.x != targets[i].x || slots[i].Item.m_gridPos.y != targets[i].y) changed = true;
            if (!changed) return false;

            foreach (var pair in stacks) pair.Key.m_stack = pair.Value;
            for (var i = 0; i < slots.Count; i++) slots[i].Item.m_gridPos = targets[i];
            foreach (var item in gone) list.Remove(item);
            Changed(inv, false, false);
            return true;
        }

        // Fills the first partial stack of each item from the later ones, as the game stacks: same item, quality and world
        // level (and variant and mod data, to be safe). Works on the planned stack sizes; nothing changes until applied.
        private static void Merge(List<ItemDrop.ItemData> items, Dictionary<ItemDrop.ItemData, int> stacks, List<ItemDrop.ItemData> gone)
        {
            for (var i = 0; i < items.Count; i++)
            {
                var into = items[i];
                var max = into.m_shared.m_maxStackSize;
                if (max <= 1 || gone.Contains(into)) continue;
                for (var j = i + 1; j < items.Count && stacks[into] < max; j++)
                {
                    var from = items[j];
                    if (gone.Contains(from) || !SameStack(into, from)) continue;
                    var move = Mathf.Min(max - stacks[into], stacks[from]);
                    stacks[into] += move;
                    stacks[from] -= move;
                    if (stacks[from] <= 0) gone.Add(from);
                }
            }
        }

        private static bool SameStack(ItemDrop.ItemData a, ItemDrop.ItemData b)
        {
            if (a.m_shared.m_name != b.m_shared.m_name || a.m_quality != b.m_quality || a.m_worldLevel != b.m_worldLevel || a.m_variant != b.m_variant)
                return false;
            var ca = a.m_customData;
            var cb = b.m_customData;
            if ((ca?.Count ?? 0) != (cb?.Count ?? 0)) return false;
            if (ca == null || ca.Count == 0) return true;
            foreach (var pair in ca)
                if (!cb.TryGetValue(pair.Key, out var value) || value != pair.Value) return false;
            return true;
        }

        private static int Compare(Slot a, Slot b)
        {
            var c = a.Group.CompareTo(b.Group);
            if (c != 0) return c;
            c = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Item.m_shared.m_name, b.Item.m_shared.m_name); // same shown name, different item
            if (c != 0) return c;
            c = b.Item.m_quality.CompareTo(a.Item.m_quality);                          // best quality first
            if (c != 0) return c;
            c = a.Item.m_variant.CompareTo(b.Item.m_variant);
            if (c != 0) return c;
            return b.Stack.CompareTo(a.Stack);                                          // full stacks first
        }

        // Kinds of items in the order they are laid out.
        private static int GroupOf(ItemDrop.ItemData item)
        {
            var shared = item.m_shared;
            switch (shared.m_itemType)
            {
                case ItemDrop.ItemData.ItemType.Material: return 0;
                case ItemDrop.ItemData.ItemType.Consumable:
                    return shared.m_food > 0f || shared.m_foodStamina > 0f || shared.m_foodEitr > 0f ? 1 : 2; // food, then meads
                case ItemDrop.ItemData.ItemType.Fish: return 3;
                case ItemDrop.ItemData.ItemType.Ammo:
                case ItemDrop.ItemData.ItemType.AmmoNonEquipable: return 4;
                case ItemDrop.ItemData.ItemType.OneHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft:
                case ItemDrop.ItemData.ItemType.Bow:
                case ItemDrop.ItemData.ItemType.Attach_Atgeir:
                case ItemDrop.ItemData.ItemType.Torch: return 5;
                case ItemDrop.ItemData.ItemType.Shield: return 6;
                case ItemDrop.ItemData.ItemType.Tool: return 7;
                case ItemDrop.ItemData.ItemType.Helmet:
                case ItemDrop.ItemData.ItemType.Chest:
                case ItemDrop.ItemData.ItemType.Legs:
                case ItemDrop.ItemData.ItemType.Hands:
                case ItemDrop.ItemData.ItemType.Shoulder:
                case ItemDrop.ItemData.ItemType.Utility:
                case ItemDrop.ItemData.ItemType.Trinket: return 8;
                case ItemDrop.ItemData.ItemType.Trophy: return 9;
                default: return 10;
            }
        }
    }
}
