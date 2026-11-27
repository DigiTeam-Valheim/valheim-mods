// Epic Loot's enchanting table (upgrades, sacrifice, convert, enchant, augment, ...) only counts and takes costs from
// the player's inventory. Epic Loot offers an inventory-provider API for this; this registers one backed by nearby
// player-built chests. Epic Loot removes costs synchronously, so only chests this PC already owns on the network,
// that nobody has open and that the player may access are used; their changes save like a normal owner edit.
// Item lists only get trophies, and shardstones and gear (weapons, armour, shields, tools) while the Sacrifice list is
// built (each switchable in the tab; gear off by default), and unidentified items while the Identify list is built or
// identified (what they turn into goes to the inventory); other items in chests are never offered to item lists.
// The Sacrifice list leaves set pieces out unless "Include set pieces" (a checkbox above the rarity buttons) is on.
// The other way round, Store Nearby also fills the enchanting table's own Storage: see StoreNearbyTable.cs, and the
// Storage tab gets a "Deposit from nearby chests" button: see DepositFromChests.cs. The Convert tab can also turn a
// shard into lower shards: see ShardDowngrade.cs, and Salvage Junk makes every boss's offering: see BossSummons.cs.
// The Wishbone can be sacrificed (one always kept): see SacrificeWishbone.cs.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace EpicLootStorageMaterials
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.epiclootstoragematerials";
        public const string Name = "Digi's Epic Loot Storage Materials";
        public const string Version = "1.6.1";
        private const string EpicLootGuid = "randyknapp.mods.epicloot";
        private const string ProviderId = "pieter.nearbychests";

        private static ConfigEntry<bool> _enabled, _trophies, _shardstones, _gear, _unidentified, _perItemDouble;
        private static BepInEx.Logging.ManualLogSource _log;
        private static ConfigEntry<float> _range;
        private static readonly HashSet<Container> Containers = new HashSet<Container>();
        private static MethodInfo _checkAccess, _unregister;

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
            _enabled = Config.Bind("General", "Enabled", true, "The enchanting table also uses materials from nearby chests (for enchanting, upgrading, sacrificing, ...).");
            _range = Config.Bind("General", "Range (m)", 50f,
                new ConfigDescription("Chests up to this many meters from you are used.", new AcceptableValueRange<float>(5f, 100f)));
            _trophies = Config.Bind("General", "Chest trophies in Sacrifice", true,
                "The Sacrifice tab also lists trophies from nearby chests. Same as the \"Include trophies\" checkbox in that tab.");
            _shardstones = Config.Bind("General", "Chest shardstones in Sacrifice", false,
                "The Sacrifice tab also lists shardstones from nearby chests and from the enchanting table's own Storage (they turn into shards). Off by default, so \"Select All\" cannot sacrifice your stored shardstones by accident; shardstones in your inventory are always listed (and Epic Loot's \"Table Storage\" category always shows the Storage). Same as the \"Include shardstones\" checkbox in that tab.");
            _gear = Config.Bind("General", "Chest gear in Sacrifice", false,
                "The Sacrifice tab also lists weapons, armour, shields and tools from nearby chests that can be sacrificed (Epic " +
                "Loot decides which: its magic items), for bulk sacrificing. Off by default: with it on, \"Select All\" also " +
                "selects every such item in the chests in range. Same as the \"Include gear\" checkbox in that tab.");
            _unidentified = Config.Bind("General", "Chest unidentified items in Identify", true,
                "The Identify mode of the Sacrifice tab also lists unidentified items from nearby chests. Identifying one takes it " +
                "from its chest; what it turns into goes to your inventory.");
            _perItemDouble = Config.Bind("Sacrifice", "Roll double output per item", true,
                "The table's chance of double output is rolled for each sacrificed item, instead of once for the whole batch (all doubled or nothing). Same average, no all-or-nothing gamble.");
            StoreNearbyTable.Bind(Config);
            DepositFromChests.Bind(Config);
            SacrificeRarityButtons.Bind(Config);
            if (FirstTime("star defaults")) SacrificeRarityButtons.StarDefaults(); // 2 and 3 stars out of the Sacrifice list
            ShardDowngrade.Bind(Config);
            BossSummons.Bind(Config);
            SacrificeWishbone.Bind(Config);
            _log = Logger;

            _checkAccess = AccessTools.Method(typeof(Container), "CheckAccess", new[] { typeof(long) });
            var api = AccessTools.TypeByName("EpicLoot.API");
            var register = api == null ? null : AccessTools.Method(api, "RegisterInventoryProvider");
            if (register == null || _checkAccess == null)
            {
                Logger.LogError("Disabled, Epic Loot's inventory provider API or Container.CheckAccess was not found.");
                return;
            }

            var harmony = ModuleHarmony;
            harmony.PatchAll(typeof(ContainerRegistry));
            // Chests that loaded before this sub-mod started (switched on, or reloaded, while playing).
            foreach (var chest in UnityEngine.Object.FindObjectsByType<Container>(FindObjectsSortMode.None)) Containers.Add(chest);
            harmony.PatchAll(typeof(SacrificePatch));
            harmony.PatchAll(typeof(FeatureValuePatch));
            PatchPayment(harmony);
            harmony.PatchAll(typeof(ChestToggleUi));
            harmony.PatchAll(typeof(SacrificeListPatch));
            PatchIdentify(harmony);
            _hookStoreNearby = true; // in Start: Inventory and QoL (Store Nearby) loads after this mod
            DepositFromChests.Patch(harmony, Logger);
            SacrificeRarityButtons.Patch(harmony, Logger);
            ShardDowngrade.Patch(harmony, Logger);
            BossSummons.Patch(harmony, Logger);
            SacrificeWishbone.Patch(harmony, Logger);
            // A copy loaded earlier (before a live reload, or before this sub-mod was switched off and on) may still be
            // registered under the same ID, and Epic Loot refuses a second one: this copy replaces it.
            _unregister = AccessTools.Method(api, "UnregisterInventoryProvider");
            _unregister?.Invoke(null, new object[] { ProviderId });
            var ok = (bool)register.Invoke(null, new object[]
            {
                ProviderId,
                (Func<List<ItemDrop.ItemData>>)ChestItems,         // getItems: trophies, and shardstones for the Sacrifice list
                (Func<string, int>)CountItem,
                (Func<string, int, int>)RemoveItem,
                (Func<ItemDrop.ItemData, int, int>)RemoveExactItem,
            });
            Logger.LogInfo(ok ? "Epic Loot can now use materials from nearby chests." : "Epic Loot refused the inventory provider.");
        }

        // BepInEx loads plugins in ID order: Store Nearby (Inventory and QoL, or its Hexium single) is only there once every
        // plugin has loaded, so the Store Nearby hook is made here, not in Awake.
        private bool _hookStoreNearby;

        private void Start()
        {
            if (_hookStoreNearby) StoreNearbyTable.Patch(ModuleHarmony, Logger);
        }

        // Switched off (or unloaded): Epic Loot stops asking this sub-mod for chest items.
        private void OnDestroy()
        {
            try { _unregister?.Invoke(null, new object[] { ProviderId }); }
            catch (Exception e) { Logger.LogWarning("Could not remove the nearby chests from Epic Loot: " + e.Message); }
        }

        [HarmonyPatch(typeof(Container), "Awake")]
        private static class ContainerRegistry
        {
            private static void Postfix(Container __instance) => Containers.Add(__instance);
        }

        // Epic Loot asks for item counts many times per screen refresh; the chest list is worked out once per frame.
        private static readonly List<Container> FrameChests = new List<Container>();
        private static int _frameChestsFrame = -1;

        private static List<Container> UsableChests()
        {
            if (_frameChestsFrame == Time.frameCount) return FrameChests;
            _frameChestsFrame = Time.frameCount;
            FrameChests.Clear();
            var player = Player.m_localPlayer;
            if (!_enabled.Value || player == null) return FrameChests;
            CollectChests(FrameChests, player);
            return FrameChests;
        }

        // The same chests for the Storage tab's "Deposit from nearby chests" button (also when the costs part is off).
        internal static List<Container> ChestsNear(Player player)
        {
            var result = new List<Container>();
            CollectChests(result, player);
            return result;
        }

        private static void CollectChests(List<Container> into, Player player)
        {
            var origin = player.transform.position;
            var maxSqr = _range.Value * _range.Value;
            Containers.RemoveWhere(c => c == null);
            foreach (var chest in Containers)
                if ((chest.transform.position - origin).sqrMagnitude <= maxSqr && IsUsable(chest, player))
                    into.Add(chest);
            into.Sort((a, b) => (a.transform.position - origin).sqrMagnitude.CompareTo((b.transform.position - origin).sqrMagnitude));
        }

        private static bool IsUsable(Container chest, Player player)
        {
            if (!chest.isActiveAndEnabled || chest.GetInventory() == null) return false;
            var piece = chest.GetComponentInParent<Piece>();
            if (piece == null || !piece.IsPlacedByPlayer()) return false;              // no tombstones, loot chests, backpacks
            if (chest.m_wagon != null || chest.GetComponentInParent<Ship>() != null) return false;
            if (!chest.IsOwner() || chest.IsInUse()) return false;                     // only chests this PC may change now
            return (bool)_checkAccess.Invoke(chest, new object[] { player.GetPlayerID() });
        }

        private static int CountItem(string name)
        {
            var total = 0;
            foreach (var chest in UsableChests())
                total += chest.GetInventory().CountItems(name, -1, true);
            return total;
        }

        // --- Sacrifice tab ---------------------------------------------------------------------------------------
        // Epic Loot rolls the table's double-output chance once for a whole sacrifice and then doubles everything.
        // With "per item" the batch roll is suppressed and the chance is rolled for every single sacrificed item, so the
        // average is the same but a batch of 20 can give 20, 23, 31, ... up to 40.

        [ThreadStatic] private static bool _sacrificing;
        private static float _doubleChance;
        private static readonly List<Tuple<ItemDrop.ItemData, int>> Sacrificed = new List<Tuple<ItemDrop.ItemData, int>>();
        private static readonly MethodInfo ProductsMethod =
            AccessTools.Method(AccessTools.TypeByName("EpicLoot.CraftingV2.EnchantingUIController"), "GetSacrificeProducts",
                new[] { typeof(List<Tuple<ItemDrop.ItemData, int>>) });
        private static readonly MethodInfo GiveMethod =
            AccessTools.Method(typeof(EpicLoot_UnityLib.EnchantingTableUIPanelBase), "GiveItemsToPlayer");
        private static readonly MethodInfo RefreshSacrifice =
            AccessTools.Method(typeof(EpicLoot_UnityLib.SacrificeUI), "RefreshAvailableItems");

        [HarmonyPatch(typeof(EpicLoot_UnityLib.SacrificeUI), "SacrificeItems")]
        private static class SacrificePatch
        {
            private static void Prefix(EpicLoot_UnityLib.SacrificeUI __instance)
            {
                CheckSelection(__instance, "Sacrifice");
                Sacrificed.Clear();
                _handedToPay = false;
                _takingSacrifice = true; // chest shardstones and gear stay offered while it is paid for (0.16)
                if (!_perItemDouble.Value || ProductsMethod == null || GiveMethod == null) return;
                var table = EpicLoot_UnityLib.EnchantingTableUI.instance?.SourceTable;
                if (table == null) return;
                var value = table.GetFeatureCurrentValue(EpicLoot_UnityLib.EnchantingFeature.Sacrifice);
                _doubleChance = float.IsNaN(value.Item1) ? 0f : value.Item1 / 100f;
                _sacrificing = true;
            }

            private static void Postfix(EpicLoot_UnityLib.SacrificeUI __instance)
            {
                if (!_handedToPay) _takingSacrifice = false; // nothing to pay (0.16), or taken right away (0.15)
                _handedToPay = false;
                if (!_sacrificing) return;
                _sacrificing = false;
                if (_payThen) return; // 0.16: the doubles come with the payment's "paid" answer (PayThenPrefix)
                var extra = new List<Tuple<ItemDrop.ItemData, int>>();
                foreach (var entry in Sacrificed)
                {
                    var doubled = 0;
                    for (var i = 0; i < entry.Item2; i++)
                        if (UnityEngine.Random.value < _doubleChance) doubled++;
                    if (doubled > 0) extra.Add(Tuple.Create(entry.Item1, doubled));
                }
                if (extra.Count == 0) return;
                var products = ProductsMethod.Invoke(null, new object[] { extra });
                EpicLoot_UnityLib.EnchantingTableUI.instance?.PlayEnchantBonusSFX();
                if (__instance.BonusPanel != null) __instance.BonusPanel.Show();
                GiveMethod.Invoke(null, new[] { products });
                RefreshSacrifice?.Invoke(__instance, null);
            }

            private static Exception Finalizer(Exception __exception)
            {
                _sacrificing = false;
                if (__exception != null) _takingSacrifice = _handedToPay = false;
                return __exception;
            }
        }

        private static readonly FieldInfo StashField = AccessTools.Field(typeof(EpicLoot_UnityLib.InventoryManagement), "ActiveStash");

        // Identifying takes the selected items the same way.
        [HarmonyPatch(typeof(EpicLoot_UnityLib.SacrificeUI), "IdentifyItems")]
        private static class IdentifyCheckPatch
        {
            private static void Prefix(EpicLoot_UnityLib.SacrificeUI __instance) => CheckSelection(__instance, "Identify");
        }

        // Before Epic Loot takes the selected items: a list made a while ago can point at items that are no longer where
        // it found them (a chest the game reloaded holds new copies of the same items), or at one item twice. Epic Loot
        // then skips them with "item unavailable". Each selected row is checked: the same item twice keeps one row; an
        // item no longer there is swapped for the identical one in the chests in range (same item, quality, stack and
        // enchantments), or deselected when there is none. A line in the log says what was done.
        private static void CheckSelection(EpicLoot_UnityLib.SacrificeUI ui, string what)
        {
            try
            {
                var list = ui?.AvailableItems;
                var player = Player.m_localPlayer;
                if (list == null || player == null) return;
                var stash = StashField?.GetValue(null) as EpicLoot_UnityLib.EnchantingTableStash; // the table's own Storage
                var chests = UsableChests();
                var claimed = new HashSet<ItemDrop.ItemData>();
                var later = new List<KeyValuePair<EpicLoot_UnityLib.MultiSelectItemListElement, EpicLoot_UnityLib.InventoryItemListElement>>();
                int rows = 0, duplicates = 0, swapped = 0, gone = 0;
                list.ForeachVisibleElement((index, element) =>
                {
                    if (element.GetSelectedQuantity() <= 0 || !(element.GetListElement() is EpicLoot_UnityLib.InventoryItemListElement entry) || entry.Item == null) return;
                    rows++;
                    if (claimed.Contains(entry.Item))
                    {
                        element.Deselect(true);
                        duplicates++;
                        return;
                    }
                    if (player.GetInventory().ContainsItem(entry.Item) || (stash != null && stash.TryGetSnapshotPrefab(entry.Item, out _)) ||
                        chests.Any(c => c.GetInventory().ContainsItem(entry.Item)))
                        claimed.Add(entry.Item);
                    else later.Add(new KeyValuePair<EpicLoot_UnityLib.MultiSelectItemListElement, EpicLoot_UnityLib.InventoryItemListElement>(element, entry));
                });
                foreach (var pair in later)
                {
                    var stale = pair.Value.Item;
                    var same = chests.SelectMany(c => c.GetInventory().GetAllItems()).FirstOrDefault(i => !claimed.Contains(i) && Same(i, stale));
                    if (same != null)
                    {
                        pair.Value.Item = same;
                        claimed.Add(same);
                        swapped++;
                    }
                    else
                    {
                        pair.Key.Deselect(true);
                        gone++;
                    }
                }
                if (duplicates + swapped + gone == 0) return;
                list.RefreshSelectAllToggle();
                var objects = Containers.Where(c => c != null).Select(c => c.GetComponent<ZNetView>()).Where(v => v != null && v.GetZDO() != null)
                    .GroupBy(v => v.GetZDO().m_uid).Max(g => (int?)g.Count()) ?? 0;
                _log?.LogWarning($"{what}: {rows} selected; {duplicates} listed twice (one kept), {swapped} found again in a chest, " +
                                 $"{gone} no longer there (left out). Chests in range {chests.Count}, most objects for one chest {objects}.");
            }
            catch (Exception e)
            {
                _log?.LogWarning($"Could not check the {what.ToLowerInvariant()} selection: {e.Message}");
            }
        }

        // The same item: name, quality, variant, stack and the same saved data (Epic Loot keeps enchantments there).
        private static bool Same(ItemDrop.ItemData a, ItemDrop.ItemData b)
        {
            if (a?.m_shared == null || b?.m_shared == null || a.m_shared.m_name != b.m_shared.m_name || a.m_quality != b.m_quality ||
                a.m_variant != b.m_variant || a.m_stack < b.m_stack) return false;
            var x = a.m_customData;
            var y = b.m_customData;
            if ((x?.Count ?? 0) != (y?.Count ?? 0)) return false;
            if (x == null || y == null) return true;
            foreach (var pair in x)
                if (!y.TryGetValue(pair.Key, out var value) || value != pair.Value) return false;
            return true;
        }

        // During a sacrifice, Epic Loot's own all-or-nothing roll sees a chance of 0.
        [HarmonyPatch(typeof(EpicLoot_UnityLib.EnchantingTable), nameof(EpicLoot_UnityLib.EnchantingTable.GetFeatureCurrentValue))]
        private static class FeatureValuePatch
        {
            private static void Postfix(EpicLoot_UnityLib.EnchantingFeature feature, ref Tuple<float, float> __result)
            {
                if (_sacrificing && feature == EpicLoot_UnityLib.EnchantingFeature.Sacrifice)
                    __result = Tuple.Create(float.NaN, float.NaN);
            }
        }

        // Epic Loot 0.15: it takes each sacrificed item with InventoryManagement.RemoveExactItem right in SacrificeItems;
        // what was really taken (from the inventory or a chest) is remembered here for the per-item double.
        private static void Removed(ItemDrop.ItemData item, int __result)
        {
            if (_sacrificing && item != null && __result > 0) Sacrificed.Add(Tuple.Create(item, __result));
        }

        // Epic Loot 0.16 pays for a sacrifice or an identify in one go: the panel's PayThen hands a Payment (the exact
        // items) to InventoryManagement.Pay, which first checks that every item is still held - chest items too, through
        // the providers' item lists (API.AppendProviderItems: ChestItems here) - and answers later when the table's own
        // Storage has to confirm its part. So the chest items stay offered (_takingSacrifice, _identifyPaying) until
        // PayThen answers, and the per-item double is given in its "paid" answer, from the payment's own items.
        private static bool _payThen, _handedToPay, _takingSacrifice, _identifyPaying;
        private static readonly FieldInfo PaymentLines = AccessTools.Field(typeof(EpicLoot_UnityLib.InventoryManagement.Payment), "Lines");

        private static void PatchPayment(Harmony harmony)
        {
            var remove = AccessTools.Method(typeof(EpicLoot_UnityLib.InventoryManagement), "RemoveExactItem");
            if (remove != null) harmony.Patch(remove, postfix: new HarmonyMethod(typeof(Plugin), nameof(Removed)));
            var payThen = AccessTools.Method(typeof(EpicLoot_UnityLib.EnchantingTableUIPanelBase), "PayThen");
            if (payThen != null)
            {
                harmony.Patch(payThen, prefix: new HarmonyMethod(typeof(Plugin), nameof(PayThenPrefix)));
                _payThen = true;
            }
            var identify = AccessTools.Method(typeof(EpicLoot_UnityLib.SacrificeUI), "IdentifyItems");
            if (identify != null && payThen != null)
                harmony.Patch(identify, prefix: new HarmonyMethod(typeof(Plugin), nameof(IdentifyPayStart)),
                    finalizer: new HarmonyMethod(typeof(Plugin), nameof(IdentifyPayEnd)));
            if (remove == null && payThen == null) _log?.LogWarning("Epic Loot's sacrifice payment was not found: no per-item double, chest items may not be taken.");
        }

        private static void IdentifyPayStart()
        {
            _handedToPay = false;
            _identifyPaying = true;
        }

        private static Exception IdentifyPayEnd(Exception __exception)
        {
            if (__exception != null || !_handedToPay) _identifyPaying = false;
            _handedToPay = false;
            return __exception;
        }

        private static void PayThenPrefix(object __instance, EpicLoot_UnityLib.InventoryManagement.Payment payment,
            ref Action<EpicLoot_UnityLib.InventoryManagement.Receipt> onPaid, ref Action onFailed)
        {
            var sacrifice = _takingSacrifice;
            var identify = _identifyPaying;
            if (!sacrifice && !identify) return;
            _handedToPay = true;
            var items = sacrifice && _sacrificing ? ExactItems(payment) : null;
            var chance = _doubleChance;
            var panel = __instance as EpicLoot_UnityLib.SacrificeUI;
            var paid = onPaid;
            var failed = onFailed;
            void Done()
            {
                if (sacrifice) _takingSacrifice = false;
                if (identify) _identifyPaying = false;
            }
            onPaid = receipt =>
            {
                try
                {
                    paid?.Invoke(receipt);
                    if (items != null && items.Count > 0) GiveDoubles(panel, items, chance);
                }
                finally { Done(); }
            };
            onFailed = () =>
            {
                try { failed?.Invoke(); }
                finally { Done(); }
            };
        }

        // The exact items of a payment (Payment.Lines is Epic Loot's own: read by reflection).
        private static List<Tuple<ItemDrop.ItemData, int>> ExactItems(EpicLoot_UnityLib.InventoryManagement.Payment payment)
        {
            var result = new List<Tuple<ItemDrop.ItemData, int>>();
            if (payment == null || !(PaymentLines?.GetValue(payment) is System.Collections.IEnumerable lines)) return result;
            foreach (var line in lines)
            {
                var t = Traverse.Create(line);
                var item = t.Field("Exact").GetValue<ItemDrop.ItemData>();
                var amount = t.Field("Amount").GetValue<int>();
                if (item != null && amount > 0) result.Add(Tuple.Create(item, amount));
            }
            return result;
        }

        // The per-item double: each sacrificed item rolls the table's chance on its own; the extras come on top.
        private static void GiveDoubles(EpicLoot_UnityLib.SacrificeUI panel, List<Tuple<ItemDrop.ItemData, int>> items, float chance)
        {
            if (ProductsMethod == null || GiveMethod == null || chance <= 0f) return;
            var extra = new List<Tuple<ItemDrop.ItemData, int>>();
            foreach (var entry in items)
            {
                var doubled = 0;
                for (var i = 0; i < entry.Item2; i++)
                    if (UnityEngine.Random.value < chance) doubled++;
                if (doubled > 0) extra.Add(Tuple.Create(entry.Item1, doubled));
            }
            if (extra.Count == 0) return;
            var products = ProductsMethod.Invoke(null, new object[] { extra });
            EpicLoot_UnityLib.EnchantingTableUI.instance?.PlayEnchantBonusSFX();
            if (panel != null && panel.BonusPanel != null) panel.BonusPanel.Show();
            GiveMethod.Invoke(null, new[] { products });
            if (panel != null) RefreshSacrifice?.Invoke(panel, null);
        }

        // "Include shardstones" and "Include trophies" checkboxes above Select All in the Sacrifice tab.
        [HarmonyPatch(typeof(EpicLoot_UnityLib.SacrificeUI), "OnEnable")]
        private static class ChestToggleUi
        {
            private static void Postfix(EpicLoot_UnityLib.SacrificeUI __instance)
            {
                try
                {
                    var selectAll = __instance.AvailableItems?.SelectAllToggle;
                    if (selectAll == null) return;
                    AddToggle(__instance, selectAll, "IncludeChestsToggle", "Include trophies", 1, _trophies);
                    AddToggle(__instance, selectAll, "IncludeShardstonesToggle", "Include shardstones", 2, _shardstones);
                    AddToggle(__instance, selectAll, "IncludeGearToggle", "Include gear", 3, _gear);
                }
                catch (Exception e)
                {
                    _log?.LogWarning("Could not add the Sacrifice tab checkboxes: " + e.Message);
                }
            }

            // A copy of Select All, placed "row" rows above it, bound to a setting. One made earlier stays in Epic Loot's
            // window: it is bound again, to this copy of the sub-mod (after a live reload the old one is gone).
            private static void AddToggle(EpicLoot_UnityLib.SacrificeUI ui, UnityEngine.UI.Toggle selectAll, string name, string label,
                int row, ConfigEntry<bool> setting)
            {
                var parent = selectAll.transform.parent;
                var existing = parent.Find(name);
                if (existing != null)
                {
                    Bind(ui, existing.GetComponent<UnityEngine.UI.Toggle>(), setting);
                    return;
                }
                var copy = UnityEngine.Object.Instantiate(selectAll.gameObject, parent, false);
                copy.name = name;
                var rect = (RectTransform)copy.transform;
                var source = (RectTransform)selectAll.transform;
                rect.anchoredPosition = source.anchoredPosition + new Vector2(0f, row * (source.rect.height + 4f));
                // Epic Loot 0.15 draws its labels with TextMeshPro, older versions with Unity's Text.
                foreach (var text in copy.GetComponentsInChildren<UnityEngine.UI.Text>(true)) text.text = label;
                foreach (var text in copy.GetComponentsInChildren<TMPro.TMP_Text>(true)) text.text = label;
                Bind(ui, copy.GetComponent<UnityEngine.UI.Toggle>(), setting);
            }

            private static void Bind(EpicLoot_UnityLib.SacrificeUI ui, UnityEngine.UI.Toggle toggle, ConfigEntry<bool> setting)
            {
                if (toggle == null) return;
                toggle.onValueChanged = new UnityEngine.UI.Toggle.ToggleEvent();
                toggle.SetIsOnWithoutNotify(setting.Value);
                toggle.interactable = true;
                toggle.onValueChanged.AddListener(on =>
                {
                    setting.Value = on;
                    RefreshSacrifice?.Invoke(ui, null);
                });
            }
        }

        // Chest shardstones are only offered while Epic Loot builds the Sacrifice list, so other tabs never see them.
        private static bool _buildingSacrificeList;

        [HarmonyPatch]
        private static class SacrificeListPatch
        {
            private static MethodBase TargetMethod() =>
                AccessTools.Method(AccessTools.TypeByName("EpicLoot.CraftingV2.EnchantingUIController"), "GetSacrificeItems");

            private static void Prefix() => _buildingSacrificeList = true;

            // "Include shardstones": the shardstones in the table's own Storage join the All Items and Shardstones lists too
            // (Epic Loot 0.15.6 lists the Storage only under its own "Table Storage" category). They come from Epic Loot's
            // own Table Storage list, so its rules (favourites, what can be sacrificed) still apply, and sacrificing one
            // takes it from the Storage. "Include set pieces" off: no set piece in the list.
            [ThreadStatic] private static bool _addingStorage;

            private static void Postfix(List<EpicLoot_UnityLib.InventoryItemListElement> __result, object __0, MethodBase __originalMethod)
            {
                if (__result == null) return;
                var category = __0?.ToString();
                if (_shardstones.Value && !_addingStorage && (category == "All" || category == "Shardstones"))
                    AddStorageShardstones(__result, __originalMethod, __0.GetType());
                if (!SacrificeRarityButtons.IncludeSetPieces)
                    __result.RemoveAll(e => e?.Item != null && EpicLoot.ItemDataExtensions.IsMagic(e.Item, out var magic) && !string.IsNullOrEmpty(magic.SetID));
                if (SacrificeRarityButtons.LeavesOutAny)
                    __result.RemoveAll(e => e?.Item != null && EpicLoot.ItemDataExtensions.IsMagic(e.Item, out var magic) && HasLeftOutStars(magic));
                if (!_addingStorage) SacrificeWishbone.KeepOne(__result, UsableChests());
            }

            // An enchantment whose stars (Epic Loot's rarity of that enchantment, 1-3) are unticked in the Sacrifice tab.
            private static bool HasLeftOutStars(EpicLoot.MagicItem magic)
            {
                if (magic?.Effects == null) return false;
                foreach (var effect in magic.Effects)
                    if (effect != null && EpicLoot.MagicItemEffectDefinitions.AllDefinitions.TryGetValue(effect.EffectType, out var definition) &&
                        SacrificeRarityButtons.LeavesOut(EpicLoot.MagicEffectRarity.GetTier(definition)))
                        return true;
                return false;
            }

            private static void AddStorageShardstones(List<EpicLoot_UnityLib.InventoryItemListElement> into, MethodBase method, Type categoryType)
            {
                try
                {
                    _addingStorage = true;
                    var storage = method.Invoke(null, new[] { Enum.Parse(categoryType, "TableStorage") }) as List<EpicLoot_UnityLib.InventoryItemListElement>;
                    if (storage == null) return;
                    foreach (var element in storage)
                        if (element?.Item != null && EpicLoot.API.IsShardStone(element.Item) && !into.Any(e => e?.Item == element.Item))
                            into.Add(element);
                }
                catch (Exception e)
                {
                    _log?.LogWarning("Could not add the table's shardstones to the Sacrifice list: " + e.Message);
                }
                finally
                {
                    _addingStorage = false;
                }
            }

            private static Exception Finalizer(Exception __exception)
            {
                _buildingSacrificeList = false;
                return __exception;
            }
        }

        // Chest unidentified items are offered while Epic Loot builds the Identify list and while it identifies (0.15:
        // LootRollSelectedItems checks every selected item is still there and takes it; 0.16: SacrificeUI.IdentifyItems
        // pays for them through PayThen, see PatchPayment).
        private static bool _identifying;

        private static void PatchIdentify(Harmony harmony)
        {
            var controller = AccessTools.TypeByName("EpicLoot.CraftingV2.EnchantingUIController");
            var found = 0;
            foreach (var name in new[] { "GetUnidentifiedItems", "LootRollSelectedItems" })
            {
                var method = controller == null ? null : AccessTools.Method(controller, name);
                if (method == null) continue;
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(Plugin), nameof(IdentifyStart)),
                    finalizer: new HarmonyMethod(typeof(Plugin), nameof(IdentifyEnd)));
                found++;
            }
            if (found == 0 || (found < 2 && AccessTools.Method(typeof(EpicLoot_UnityLib.SacrificeUI), "IdentifyItems") == null))
                _log?.LogWarning("Epic Loot's identify methods were not found: the Identify list shows no chest items.");
        }

        private static void IdentifyStart() => _identifying = true;

        private static Exception IdentifyEnd(Exception __exception)
        {
            _identifying = false;
            return __exception;
        }

        private static List<ItemDrop.ItemData> ChestItems()
        {
            var result = new List<ItemDrop.ItemData>();
            var shardstones = _shardstones.Value && (_buildingSacrificeList || _takingSacrifice);
            var gear = _gear.Value && (_buildingSacrificeList || _takingSacrifice);
            var unidentified = _unidentified.Value && (_identifying || _identifyPaying);
            if (!_trophies.Value && !shardstones && !gear && !unidentified) return result;
            foreach (var chest in UsableChests())
                foreach (var item in chest.GetInventory().GetAllItems())
                {
                    if (item?.m_shared == null) continue;
                    if (_trophies.Value && item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Trophy) result.Add(item);
                    else if (shardstones && EpicLoot.API.IsShardStone(item)) result.Add(item);
                    else if (unidentified && EpicLoot.ItemDataExtensions.IsUnidentified(item)) result.Add(item);
                    else if (gear && IsGear(item)) result.Add(item); // Epic Loot keeps only what it can sacrifice
                }
            return result;
        }

        // Weapons, armour, shields and tools (not ammo, food or materials).
        internal static bool IsGear(ItemDrop.ItemData item)
        {
            switch (item.m_shared.m_itemType)
            {
                case ItemDrop.ItemData.ItemType.OneHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft:
                case ItemDrop.ItemData.ItemType.Bow:
                case ItemDrop.ItemData.ItemType.Shield:
                case ItemDrop.ItemData.ItemType.Helmet:
                case ItemDrop.ItemData.ItemType.Chest:
                case ItemDrop.ItemData.ItemType.Legs:
                case ItemDrop.ItemData.ItemType.Hands:
                case ItemDrop.ItemData.ItemType.Shoulder:
                case ItemDrop.ItemData.ItemType.Utility:
                case ItemDrop.ItemData.ItemType.Trinket:
                case ItemDrop.ItemData.ItemType.Tool:
                case ItemDrop.ItemData.ItemType.Torch:
                case ItemDrop.ItemData.ItemType.Attach_Atgeir:
                    return true;
                default:
                    return false;
            }
        }

        // Removes this exact item (one of the chest trophies listed above) from the chest holding it.
        private static int RemoveExactItem(ItemDrop.ItemData item, int amount)
        {
            if (item == null || amount <= 0) return 0;
            foreach (var chest in UsableChests())
            {
                var inventory = chest.GetInventory();
                if (!inventory.ContainsItem(item)) continue;
                var before = item.m_stack;
                inventory.RemoveItem(item, Mathf.Min(amount, before));
                return before - (inventory.ContainsItem(item) ? item.m_stack : 0);
            }
            return 0;
        }

        private static int RemoveItem(string name, int amount)
        {
            var removed = 0;
            foreach (var chest in UsableChests())
            {
                if (removed >= amount) break;
                var inventory = chest.GetInventory();
                var before = inventory.CountItems(name, -1, true);
                if (before <= 0) continue;
                inventory.RemoveItem(name, Mathf.Min(before, amount - removed), -1, true);
                removed += before - inventory.CountItems(name, -1, true);
            }
            return removed;
        }
    }
}
