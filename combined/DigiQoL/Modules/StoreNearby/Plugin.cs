// Store Nearby: a button under your inventory that puts what you carry into the chests around you that already hold
// that item (their stacks first, then a free slot in that chest). Our own replacement for Local Craft From Storage
// Prime's button (with Craft From Chests in Crafting and Automation); that mod, if installed too, is switched off. Also
// folds in Store Nearby Plus (food, potions, extra slots, the chest checkbox) and Backpack Store Nearby (the button under
// an open Adventure Backpacks backpack stores your bag and the backpack).
// What goes: materials (also stackable odds and ends such as the Frozen King's Hateful Blood, not coins), trophies,
// fish, berries, mushrooms and other raw food; cooked food and potions by switch.
// What stays: the hotbar (and as many rows below it as set), worn gear, gear and ammo, quest items, keys, extra slots
// that other mods add, and the always-keep list. The always-store list sends anything else.
// Epic Loot gear (magic and unidentified, switchable): into chests that already hold Epic Loot gear, any piece of it,
// unless worn, in a kept row (the hotbar), favourited or always kept. Recognised by Epic Loot's own mark on the item
// (no Epic Loot reference). Not part of Sendable, so To Backpacks and the enchanting table are not changed by it.
// Multiplayer: like Craft From Chests, a chest can only be filled by its owner: chests that are not ours are asked for
// first (the game's own open request), and the button waits a moment for the answers.
// Hook points for other sub-mods (by name, so their Hexium singles work too): BeforeStore (the Epic Loot table),
// AfterStore (Chest Sorter), Sendable / KeepsRow / Button / Running (To Backpacks).
// Light: nothing runs until the button is pressed, except a check twice a second whether a chest is in range.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using DigiStorage;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StoreNearby
{
    public class Plugin : DigiCore.DigiModule
    {
        internal const string LcfspGuid = "LuminairPrime.valheim.localcraftfromstorageprime";
        private const string LcfspConfig = "LuminairPrime.valheim.localcraftfromstorageprime.cfg";
        private const string Label = "Store Nearby";

        internal static ConfigEntry<float> Range;
        internal static ConfigEntry<bool> StoreFood, StorePotions, StoreMagic, ExtraSlots, Backpack, Checkbox;
        internal static ConfigEntry<int> KeepRows;
        internal static ConfigEntry<string> AlwaysKeep, AlwaysStore;

        public static bool Running;
        internal static Button Button;

        private static readonly ChestRules Rules = new ChestRules { BlockFlag = DigiCore.ChestFlags.NoStore };
        private static readonly Chests.Area Area = new Chests.Area(Rules);
        private static Plugin _instance;
        private static ChestCheckbox _box;
        private static float _nextCheck;
        private static bool _busy;
        private static System.Reflection.FieldInfo _backpackIsOpen;
        private static bool _backpackResolved;

        private const string CheckboxTooltip =
            "<color=yellow>Ticked</color> (normal): Store Nearby may put your items into this chest.\n" +
            "<color=yellow>Unticked</color>: Store Nearby skips this chest.\n\n" +
            "Crafting, building and the auto stations keep using it as normal (that is the other box).";

        private void Awake()
        {
            _instance = this;
            const string store = "1 - Store Nearby", what = "2 - What goes", chest = "3 - Chests";
            Range = Config.Bind(store, "Range (m)", 50f, new ConfigDescription("How far from you a chest may be.", new AcceptableValueRange<float>(5f, 100f)));
            Backpack = Config.Bind(store, "Open backpack too", true,
                "With an Adventure Backpacks backpack open, the button shows under it and stores your bag and the backpack.");
            StoreFood = Config.Bind(what, "Store food", true,
                "Cooked food (stews, pies, cooked meat, ...) goes too, into chests that already hold it. Off: it stays with you. " +
                "Raw food (berries, mushrooms, honey, ...) always goes.");
            StorePotions = Config.Bind(what, "Store potions", true, "Meads and potions go too, into chests that already hold them. Off: they stay with you.");
            StoreMagic = Config.Bind(what, "Store magic gear", true,
                "Epic Loot gear (magic and unidentified weapons, armour, ...) goes too, into chests that already hold Epic Loot gear " +
                "(any piece). Worn gear, the hotbar and kept rows, favourites and the always-keep list stay with you. Off: it stays with you.");
            KeepRows = Config.Bind(what, "Keep rows", 0, new ConfigDescription(
                "Rows of your inventory below the hotbar that stay with you (the hotbar, the top row, always stays).", new AcceptableValueRange<int>(0, 7)));
            ExtraSlots = Config.Bind(what, "Leave extra slots alone", true,
                "Items in slots that other mods add outside the inventory (equipment and quick slots) are never stored.");
            AlwaysKeep = Config.Bind(what, "Always keep", "",
                "These always stay with you: item names or prefab names, comma-separated (\"Honey, Tankard\"), or the kinds food, potions, materials, trophies, fish, magic.");
            AlwaysStore = Config.Bind(what, "Always store", "",
                "These always go, even gear or keys that normally stay: item names or prefab names, comma-separated. Always keep wins.");
            Checkbox = Config.Bind(chest, "Chest checkbox", true,
                "A \"Store Nearby\" checkbox in chest windows (where the chest's title was): untick it and Store Nearby skips that chest.");
            Checkbox.SettingChanged += (_, __) => RefreshBox();
            AlwaysKeep.SettingChanged += (_, __) => _keepTokens = null;
            AlwaysStore.SettingChanged += (_, __) => _storeTokens = null;
            if (FirstTime("from local craft from storage")) TakeOverSettings();
        }

        private void Start()
        {
            SwitchOffReplaced(LcfspGuid, "Local Craft From Storage Prime"); // installed too: it goes off, ours runs
            Chests.Use();
            PatchModule(typeof(Plugin));
            Running = true;
        }

        private void OnDestroy()
        {
            if (!Running) return;
            Running = false;
            Chests.Release();
            if (Button != null) Destroy(Button.gameObject);
            Button = null;
            _box?.Destroy();
            _box = null;
        }

        // ------------------------------------------------------------------ hook points (other sub-mods patch these)

        // Right before storing, with the inventories it stores from (the bag, and an open backpack) and its range.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void BeforeStore(Player player, List<Inventory> sources, float range) { }

        // Right after storing, with the chests that got something.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void AfterStore(List<Container> chests) { }

        // The rows of the bag that stay with you: the hotbar and the kept rows below it.
        public static bool KeepsRow(int row) => row <= (KeepRows != null ? KeepRows.Value : 0);

        // ------------------------------------------------------------------ the button

        private static ChestCheckbox Box =>
            _box ?? (_box = new ChestCheckbox(ChestCheckbox.StoreName, DigiCore.ChestFlags.NoStore, Label, "Store Nearby puts into it", CheckboxTooltip));

        private static void RefreshBox()
        {
            if (InventoryGui.instance != null) Box.Refresh(InventoryGui.instance, Checkbox.Value);
        }

        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Show))]
        [HarmonyPostfix]
        private static void Shown(InventoryGui __instance)
        {
            _nextCheck = 0f;
            Box.Refresh(__instance, Checkbox.Value);
        }

        [HarmonyPatch(typeof(InventoryGui), "Update")]
        [HarmonyPostfix]
        private static void Updated(InventoryGui __instance)
        {
            if (!InventoryGui.IsVisible())
            {
                if (Button != null && Button.gameObject.activeSelf) Button.gameObject.SetActive(false);
                return;
            }
            // Closing (it slides away): nothing changes, or the button popped up over Place stacks as the chest left.
            if (DigiStorage.ChestCheckbox.Closing(__instance)) return;
            Box.Update(__instance, Checkbox.Value);
            var backpack = BackpackOpen(__instance);
            var show = __instance.m_currentContainer == null || backpack;
            if (!show)
            {
                if (Button != null && Button.gameObject.activeSelf) Button.gameObject.SetActive(false);
                return;
            }
            if (Button == null) Create(__instance);
            if (Button == null) return;
            var parent = backpack ? __instance.m_container : __instance.m_player;
            if (Button.transform.parent != parent) Button.transform.SetParent(parent, false);
            if (!Button.gameObject.activeSelf) Button.gameObject.SetActive(true);
            if (Time.unscaledTime >= _nextCheck)
            {
                _nextCheck = Time.unscaledTime + 0.5f;
                Button.interactable = !_busy && Here().Count > 0;
            }
        }

        private static void Create(InventoryGui gui)
        {
            var source = gui.m_takeAllButton != null ? gui.m_takeAllButton : gui.m_stackAllButton;
            if (source == null || gui.m_player == null) return;
            var old = gui.m_player.Find("DigiStoreNearbyButton");
            if (old != null) Destroy(old.gameObject);
            var copy = Instantiate(source.gameObject, gui.m_player, false);
            copy.name = "DigiStoreNearbyButton";
            foreach (var localize in copy.GetComponentsInChildren<Localize>(true)) localize.enabled = false;
            Button = copy.GetComponent<Button>();
            Button.onClick = new Button.ButtonClickedEvent();
            Button.onClick.AddListener(Clicked);
            var text = copy.GetComponentInChildren<TMP_Text>(true);
            var rect = (RectTransform)copy.transform;
            if (text != null)
            {
                text.text = Label;
                var width = text.GetPreferredValues(Label).x + 40f;
                if (width > rect.sizeDelta.x) rect.sizeDelta = new Vector2(width, rect.sizeDelta.y);
            }
            // Below the panel, at its right edge (as Local Craft From Storage's was, so To Backpacks fits left of it).
            rect.anchorMin = rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(1f, 1f);
            rect.anchoredPosition = new Vector2(-1.3f, -10f);
            rect.localScale = Vector3.one;
        }

        private static bool BackpackOpen(InventoryGui gui)
        {
            if (!_backpackResolved)
            {
                _backpackResolved = true;
                var patches = AccessTools.TypeByName("AdventureBackpacks.Patches.InventoryGuiPatches");
                _backpackIsOpen = patches != null ? AccessTools.Field(patches, "BackpackIsOpen") : null;
            }
            return Backpack.Value && _backpackIsOpen != null && gui.m_currentContainer != null && (bool)_backpackIsOpen.GetValue(null);
        }

        private static List<Container> Here()
        {
            Rules.Range = Range.Value;
            var player = Player.m_localPlayer;
            return player != null ? Chests.Near(Area, player.transform.position) : new List<Container>();
        }

        // ------------------------------------------------------------------ storing

        private static void Clicked()
        {
            var player = Player.m_localPlayer;
            var gui = InventoryGui.instance;
            if (_busy || player == null || gui == null || player.IsDead() || player.IsTeleporting()) return;
            var sources = new List<Inventory> { player.m_inventory };
            var exclude = gui.m_currentContainer;
            if (BackpackOpen(gui)) sources.Add(gui.m_currentContainer.GetInventory());
            try
            {
                BeforeStore(player, sources, Range.Value);
            }
            catch (Exception e)
            {
                _instance?.Logger.LogWarning("Before Store Nearby: " + e.Message);
            }
            var wanted = new HashSet<string>();
            var magic = false;
            foreach (var source in sources)
                foreach (var item in source.GetAllItems())
                {
                    if (Sendable(item, source)) wanted.Add(item.m_shared.m_name);
                    if (SendableMagic(item, source)) magic = true;
                }
            if (wanted.Count == 0 && !magic)
            {
                player.Message(MessageHud.MessageType.Center, "Nothing to store");
                return;
            }
            // Chests that are not ours yet but hold something to store: ask their owners first.
            var asked = false;
            foreach (var chest in Here())
            {
                if (chest == null || chest == exclude || Chests.Ready(chest) || Chests.OpenByOthers(chest)) continue;
                if (!chest.GetInventory().GetAllItems().Any(i => i?.m_shared != null && (wanted.Contains(i.m_shared.m_name) || (magic && IsMagic(i))))) continue;
                Chests.Request(chest);
                asked = true;
            }
            if (asked && Chests.Waiting() && _instance != null) _instance.StartCoroutine(StoreWhenReady(player, sources, exclude));
            else Store(player, sources, exclude);
        }

        private static IEnumerator StoreWhenReady(Player player, List<Inventory> sources, Container exclude)
        {
            _busy = true;
            var until = Time.unscaledTime + 2.5f;
            while (Chests.Waiting() && Time.unscaledTime < until) yield return null;
            _busy = false;
            if (player != null && player == Player.m_localPlayer && InventoryGui.IsVisible()) Store(player, sources, exclude);
        }

        private static void Store(Player player, List<Inventory> sources, Container exclude)
        {
            var chests = Here();
            var filled = new List<Container>();
            var moved = 0;
            var matched = false;
            foreach (var source in sources)
                foreach (var item in source.GetAllItems().ToList())
                {
                    var magic = SendableMagic(item, source); // into chests that hold Epic Loot gear
                    var same = Sendable(item, source);       // into chests that hold this item
                    if (!magic && !same) continue;
                    foreach (var chest in chests)
                    {
                        if (item.m_stack <= 0 || !source.ContainsItem(item)) break;
                        if (chest == null || chest == exclude || !Chests.Ready(chest)) continue;
                        Chests.Fresh(chest);
                        var inventory = chest.GetInventory();
                        if (!(magic && HoldsMagic(inventory)) && !(same && inventory.ContainsItemByName(item.m_shared.m_name))) continue;
                        matched = true;
                        var n = MoveInto(inventory, source, item);
                        if (n <= 0) continue;
                        moved += n;
                        if (!filled.Contains(chest)) filled.Add(chest);
                    }
                }
            if (moved > 0)
                player.Message(MessageHud.MessageType.Center, $"Stored {moved} item{(moved == 1 ? "" : "s")} in {filled.Count} chest{(filled.Count == 1 ? "" : "s")}");
            else
                player.Message(MessageHud.MessageType.Center, matched ? "The chests with these items are full" : "No chest nearby holds these items");
            try
            {
                AfterStore(filled);
            }
            catch (Exception e)
            {
                _instance?.Logger.LogWarning("After Store Nearby: " + e.Message);
            }
        }

        // Into the chest's stacks of that item first, then (if it has room) a free slot of the chest; returns how many moved.
        private static int MoveInto(Inventory target, Inventory source, ItemDrop.ItemData item)
        {
            var before = item.m_stack;
            if (target.HaveEmptySlot())
            {
                if (target.AddItem(item)) // the whole stack: the item itself now lies in the chest
                {
                    source.RemoveItem(item);
                    return before;
                }
                source.Changed();
                return before - item.m_stack;
            }
            var moved = 0;
            while (item.m_stack > 0)
            {
                var stack = target.FindFreeStackItem(item.m_shared.m_name, item.m_quality, item.m_worldLevel);
                if (stack == null) break;
                var n = Mathf.Min(item.m_stack, stack.m_shared.m_maxStackSize - stack.m_stack);
                if (n <= 0) break;
                stack.m_stack += n;
                item.m_stack -= n;
                moved += n;
            }
            if (moved > 0)
            {
                if (item.m_stack <= 0) source.RemoveItem(item);
                else source.Changed();
                target.Changed();
            }
            return moved;
        }

        // ------------------------------------------------------------------ what goes

        private static readonly string[] KeepPrefabs =
        {
            "CryptKey", "DvergrKey", "BloodGoldKey", "HildirKey_forestcrypt", "HildirKey_mountaincave", "HildirKey_plainsfortress",
            "chest_hildir1", "chest_hildir2", "chest_hildir3", "BarberKit", "BombSiege", "Catapult_ammo", "Catapult_Ammo_BloodGold",
        };

        private static HashSet<string> _keepTokens, _storeTokens, _made;
        private static ObjectDB _madeFor;

        // May Store Nearby move this item out of this inventory (the bag, or an open backpack)?
        public static bool Sendable(ItemDrop.ItemData item, Inventory source)
        {
            if (!Movable(item, source)) return false;
            var kind = Kind(item);
            if (Listed(item, kind, ref _keepTokens, AlwaysKeep)) return false;
            if (Listed(item, kind, ref _storeTokens, AlwaysStore)) return true;
            switch (kind)
            {
                case "materials":
                case "trophies":
                case "fish":
                    return !Kept(item);
                case "food":
                    return StoreFood == null || StoreFood.Value;
                case "potions":
                    return StorePotions == null || StorePotions.Value;
                case "raw":
                    return !Kept(item);
                default:
                    return false;
            }
        }

        // Not worn, no quest item, not in a kept row (the hotbar) or an extra slot of the bag.
        private static bool Movable(ItemDrop.ItemData item, Inventory source)
        {
            if (item?.m_shared == null || item.m_stack <= 0) return false;
            var player = Player.m_localPlayer;
            if (player == null || item.m_equipped || player.IsItemEquiped(item) || item.m_shared.m_questItem) return false;
            var bag = player.m_inventory;
            if (source != bag) return true;
            var p = item.m_gridPos;
            var outside = p.x < 0 || p.y < 0 || p.x >= bag.GetWidth() || p.y >= bag.GetHeight();
            if (outside) return ExtraSlots != null && !ExtraSlots.Value;
            return !KeepsRow(p.y);
        }

        // ---- Epic Loot gear

        // Epic Loot keeps a magic (or unidentified) item's data under this key in the item's custom data.
        private const string MagicKey = "randyknapp.mods.epicloot#EpicLoot.MagicItemComponent";
        private static Func<ItemDrop.ItemData, bool> _favourite;
        private static bool _favouriteResolved;

        // Epic Loot gear Store Nearby may move: into chests that already hold Epic Loot gear (see Store).
        internal static bool SendableMagic(ItemDrop.ItemData item, Inventory source)
        {
            if (StoreMagic == null || !StoreMagic.Value || !IsMagic(item) || !(item.IsEquipable() || Unidentified(item)) || !Movable(item, source)) return false;
            if (Listed(item, "magic", ref _keepTokens, AlwaysKeep)) return false;
            return !Favourite(item);
        }

        // An unidentified Epic Loot item is an item of its own ("<Biome>_<Rarity>_Unidentified", not equipable) until the
        // enchanting table identifies it: gear all the same.
        private static bool Unidentified(ItemDrop.ItemData item)
        {
            var prefab = item.m_dropPrefab != null ? item.m_dropPrefab.name : "";
            if (prefab.EndsWith("_Unidentified", StringComparison.Ordinal)) return true;
            return item.m_customData.TryGetValue(MagicKey, out var raw) && raw != null && raw.IndexOf("\"IsUnidentified\":true", StringComparison.Ordinal) >= 0;
        }

        private static bool IsMagic(ItemDrop.ItemData item)
        {
            var data = item?.m_customData;
            if (data == null || data.Count == 0 || !data.TryGetValue(MagicKey, out var raw)) return false;
            return !string.IsNullOrEmpty(raw) && raw != "null";
        }

        private static bool HoldsMagic(Inventory inventory)
        {
            foreach (var other in inventory.GetAllItems())
                if (IsMagic(other)) return true;
            return false;
        }

        // Favourited (Item Favorite Framework, as Epic Loot itself asks it): stays with you.
        private static bool Favourite(ItemDrop.ItemData item)
        {
            if (!_favouriteResolved)
            {
                _favouriteResolved = true;
                var method = AccessTools.Method(AccessTools.TypeByName("EpicLoot.Compatibility.ItemFavorites"), "IsProtected", new[] { typeof(ItemDrop.ItemData) });
                if (method != null) _favourite = AccessTools.MethodDelegate<Func<ItemDrop.ItemData, bool>>(method);
            }
            try
            {
                return _favourite != null && _favourite(item);
            }
            catch
            {
                return false;
            }
        }

        private static bool Kept(ItemDrop.ItemData item)
        {
            var prefab = item.m_dropPrefab != null ? item.m_dropPrefab.name : "";
            return Array.IndexOf(KeepPrefabs, prefab) >= 0 || prefab.StartsWith("FireworksRocket", StringComparison.Ordinal) ||
                   prefab.StartsWith("Saddle", StringComparison.Ordinal);
        }

        // materials, trophies, fish, food (made), potions (made), raw (food not made: berries, mushrooms, honey), other.
        private static string Kind(ItemDrop.ItemData item)
        {
            switch (item.m_shared.m_itemType)
            {
                case ItemDrop.ItemData.ItemType.Material: return "materials";
                // Stackable odds and ends count as materials: the Frozen King's Hateful Blood is "Misc" (2026-11-20).
                // Coins stay with you (traders).
                case ItemDrop.ItemData.ItemType.Misc:
                    return item.m_shared.m_maxStackSize > 1 && (item.m_dropPrefab == null || item.m_dropPrefab.name != "Coins") ? "materials" : "other";
                case ItemDrop.ItemData.ItemType.Trophy: return "trophies";
                case ItemDrop.ItemData.ItemType.Fish: return "fish";
                case ItemDrop.ItemData.ItemType.Consumable:
                    if (!Made(item)) return "raw";
                    var food = item.m_shared.m_food > 0f || item.m_shared.m_foodStamina > 0f || item.m_shared.m_foodEitr > 0f;
                    return food ? "food" : "potions";
                default: return "other";
            }
        }

        // Made by the player (a recipe, a cooking station, a fermenter): cooked food and meads, not what grows or drops.
        private static bool Made(ItemDrop.ItemData item)
        {
            if (_made == null || _madeFor != ObjectDB.instance)
            {
                _made = new HashSet<string>();
                _madeFor = ObjectDB.instance;
                if (ObjectDB.instance != null)
                    foreach (var recipe in ObjectDB.instance.m_recipes)
                        if (recipe?.m_item != null) _made.Add(recipe.m_item.m_itemData.m_shared.m_name);
                if (ZNetScene.instance != null)
                    foreach (var prefab in ZNetScene.instance.m_prefabs)
                    {
                        if (prefab == null) continue;
                        var cooking = prefab.GetComponent<CookingStation>();
                        if (cooking != null)
                            foreach (var c in cooking.m_conversion)
                                if (c?.m_to != null) _made.Add(c.m_to.m_itemData.m_shared.m_name);
                        var fermenter = prefab.GetComponent<Fermenter>();
                        if (fermenter != null)
                            foreach (var c in fermenter.m_conversion)
                                if (c?.m_to != null) _made.Add(c.m_to.m_itemData.m_shared.m_name);
                    }
            }
            return _made.Contains(item.m_shared.m_name);
        }

        private static bool Listed(ItemDrop.ItemData item, string kind, ref HashSet<string> tokens, ConfigEntry<string> setting)
        {
            if (setting == null || string.IsNullOrWhiteSpace(setting.Value)) return false;
            if (tokens == null)
                tokens = new HashSet<string>(setting.Value.Split(',').Select(Normal).Where(t => t.Length > 0));
            if (tokens.Count == 0) return false;
            if (tokens.Contains(kind) || (kind == "raw" && tokens.Contains("food"))) return true;
            var prefab = item.m_dropPrefab != null ? item.m_dropPrefab.name : null;
            return (prefab != null && tokens.Contains(Normal(prefab))) || tokens.Contains(Normal(item.m_shared.m_name)) ||
                   (Localization.instance != null && tokens.Contains(Normal(Localization.instance.Localize(item.m_shared.m_name))));
        }

        // "$item_wood", "Wood", "wood " all become "wood"; spaces and underscores are left out ("Snowball Big" = "SnowballBig").
        private static string Normal(string text)
        {
            var t = (text ?? "").Trim().ToLowerInvariant();
            if (t.StartsWith("$item_")) t = t.Substring(6);
            else if (t.StartsWith("$")) t = t.Substring(1);
            return new string(t.Where(c => !char.IsWhiteSpace(c) && c != '_').ToArray());
        }

        // ------------------------------------------------------------------ settings taken over once

        private void TakeOverSettings()
        {
            var lcfsp = Read(Path.Combine(Paths.ConfigPath, LcfspConfig));
            if (lcfsp.TryGetValue("05 - Store Nearby|Distance", out var d) &&
                float.TryParse(d, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var range))
                Range.Value = Mathf.Clamp(range, 5f, 100f);
            if (lcfsp.TryGetValue("05 - Store Nearby|AlwaysKeep", out var keep)) AlwaysKeep.Value = keep;
            if (lcfsp.TryGetValue("05 - Store Nearby|AlwaysSend", out var send)) AlwaysStore.Value = send;
            if (lcfsp.TryGetValue("05 - Store Nearby|KeepInventoryRows", out var rows) && int.TryParse(rows, out var n)) KeepRows.Value = Mathf.Clamp(n, 0, 7);
            // Store Nearby Plus lived in this combined mod's own settings file.
            var plus = Read(Config.ConfigFilePath);
            bool On(string key, bool fallback) => plus.TryGetValue(key, out var v) ? v.Equals("true", StringComparison.OrdinalIgnoreCase) : fallback;
            StoreFood.Value = On("Store Nearby Plus - 1 - Store Nearby|Store food", StoreFood.Value);
            StorePotions.Value = On("Store Nearby Plus - 1 - Store Nearby|Store potions", StorePotions.Value);
            ExtraSlots.Value = On("Store Nearby Plus - 1 - Store Nearby|Leave extra slots alone", ExtraSlots.Value);
            Checkbox.Value = On("Store Nearby Plus - 2 - Chest checkbox|Show checkbox", Checkbox.Value);
            if (lcfsp.Count > 0 || plus.Count > 0)
                Logger.LogInfo($"Took over the settings of Local Craft From Storage's Store Nearby and Store Nearby Plus once: range {Range.Value:0} m, " +
                               $"always store '{AlwaysStore.Value}', always keep '{AlwaysKeep.Value}', keep rows {KeepRows.Value}, food {StoreFood.Value}, potions {StorePotions.Value}.");
        }

        private static Dictionary<string, string> Read(string path)
        {
            var values = new Dictionary<string, string>();
            if (!File.Exists(path)) return values;
            var section = "";
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith("[") && line.EndsWith("]")) section = line.Substring(1, line.Length - 2);
                else if (!line.StartsWith("#") && line.Contains(" = "))
                {
                    var eq = line.IndexOf(" = ", StringComparison.Ordinal);
                    values[section + "|" + line.Substring(0, eq).Trim()] = line.Substring(eq + 3).Trim();
                }
            }
            return values;
        }
    }
}
