// A "Recycle" tab at crafting stations, next to Craft and Upgrade. It lists the items in your inventory that this
// station can make; recycling gives back the materials of the item's current quality level (or a share of them) and
// lowers its quality by one, or removes it at quality 1. Item data (enchantments, crafter name) is kept on downgrade.
// Equipped and hotbar items are hidden by default (a button toggles them) and need a confirmation.
// Deep North gear is cast from a mould and finished in the frost foundry. A switch (off by default) makes recycling it at
// quality 1 give that mould back too: also the staffs, whose own recipe has no mould in it, and gear whose recipe you do
// not know yet (it dropped, so the game does not offer that recipe). Off: finished gear gives no mould back.
// Light: the tab is created once, and the list is only rebuilt when the crafting panel itself refreshes.
using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RecycleTab
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.recycletab";
        public const string Name = "Digi's Recycle Tab";
        public const string Version = "1.0.1";

        private static ConfigEntry<int> _returnPercent;
        private static ConfigEntry<bool> _keepItem, _showEquipped, _confirm, _moulds;
        private static ConfigEntry<string> _excludedStations;

        private static bool _active, _skipConfirm;
        internal static bool Active => _active; // Recipe Sort and Search leaves this tab's own order alone
        private static GameObject _tabObject, _toggleObject;
        private static Button _tab, _toggle;
        private static readonly HashSet<ItemDrop.ItemData> Equipped = new HashSet<ItemDrop.ItemData>();
        private static readonly HashSet<ItemDrop.ItemData> Hotbar = new HashSet<ItemDrop.ItemData>();
        private static readonly List<ItemDrop.ItemData> Temp = new List<ItemDrop.ItemData>();
        // Deep North gear by its name: the recipe that has its mould in it.
        private static readonly Dictionary<string, Recipe> MouldRecipes = new Dictionary<string, Recipe>();
        private static ObjectDB _mouldsOf;

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
            _returnPercent = Config.Bind("General", "Materials returned (%)", 100,
                new ConfigDescription("How much of the crafting materials you get back: 100 = all, 50 = half.", new AcceptableValueRange<int>(0, 100)));
            _keepItem = Config.Bind("General", "Keep item data on downgrade", true,
                "An item that drops one quality level keeps its enchantments and crafter name. Off = you get a plain new item.");
            _showEquipped = Config.Bind("General", "Show equipped and hotbar items", false, "Also list what you are wearing and what is on your hotbar.");
            _confirm = Config.Bind("General", "Confirm equipped and hotbar items", true, "Ask before recycling something you are wearing or have on your hotbar.");
            _excludedStations = Config.Bind("General", "Stations without the tab", "cauldron",
                "Stations that get no Recycle tab, as words matched against the station name, comma-separated (the cauldron by default).");
            _moulds = Config.Bind("General", "Deep North gear gives back its mould", true,
                "Recycling Deep North gear at quality 1 also gives back the mould it was cast in, whatever Materials returned says. " +
                "It is listed at its own station even if you do not know its recipe yet (gear that dropped). Off: finished gear gives no mould back.");
            // On by default since Crafting and Automation 1.0.14: switched on once for everyone.
            if (FirstTime("moulds back on") && !_moulds.Value) _moulds.Value = true;
            PatchModule(typeof(Plugin));
            KeepLocalCraftOut();
        }

        // Switched off in F1 or reloaded: the tab goes too.
        private void OnDestroy()
        {
            if (_tabObject != null) Destroy(_tabObject);
            if (_toggleObject != null) Destroy(_toggleObject);
            _active = false;
        }

        // Local Craft From Storage hooks the same craft step: it checks the materials for the next quality level (which
        // recycling does not use), fetches them from chests and then runs the craft step again itself. That recycled
        // the item twice (quality 3 to 1) and left it rolling back its fetch. While the Recycle tab is active it is
        // told that nothing is needed from storage.
        private void KeepLocalCraftOut()
        {
            var payment = AccessTools.TypeByName("LocalCraftFromStoragePrime.PaymentAction");
            var craft = payment != null ? AccessTools.Method(payment, "Craft", new[] { typeof(InventoryGui), typeof(Player) }) : null;
            if (craft == null) return; // not installed (or changed): nothing to keep out
            ModuleHarmony.Patch(craft, prefix: new HarmonyMethod(typeof(Plugin), nameof(LocalCraftPayment)));
        }

        private static bool LocalCraftPayment(ref bool __result)
        {
            if (!_active) return true;
            __result = true; // "carry on with the normal craft step", which is our recycling
            return false;
        }

        // --- Tab -----------------------------------------------------------------------------------------------

        private static void EnsureTab(InventoryGui gui)
        {
            if (_tabObject != null) return;
            var upgrade = gui.m_tabUpgrade;
            _tabObject = Instantiate(upgrade.gameObject, upgrade.transform.parent, false);
            _tabObject.name = "RecycleTab";
            _tabObject.GetComponentInChildren<TMP_Text>().text = "RECYCLE";
            var width = _tabObject.GetComponent<RectTransform>().rect.width;
            var x = upgrade.transform.localPosition.x;
            foreach (Transform sibling in upgrade.transform.parent)
                if (sibling.gameObject != _tabObject && sibling.gameObject.activeSelf && sibling.GetComponent<Button>() != null)
                    x = Mathf.Max(x, sibling.localPosition.x);
            _tabObject.transform.localPosition = new Vector3(x + width + 10f, upgrade.transform.localPosition.y, upgrade.transform.localPosition.z);
            _tab = _tabObject.GetComponent<Button>();
            _tab.onClick = new Button.ButtonClickedEvent();
            _tab.onClick.AddListener(() =>
            {
                _active = true;
                InventoryGui.instance.UpdateCraftingPanel();
            });
            _tabObject.SetActive(false);

            _toggleObject = Instantiate(_tabObject, gui.m_craftButton.transform.parent, false);
            _toggleObject.name = "RecycleShowEquipped";
            _toggle = _toggleObject.GetComponent<Button>();
            _toggle.onClick = new Button.ButtonClickedEvent();
            _toggle.onClick.AddListener(() =>
            {
                _showEquipped.Value = !_showEquipped.Value;
                UpdateToggleText();
                InventoryGui.instance.UpdateCraftingPanel();
            });
            _toggleObject.transform.localPosition = new Vector3(140f, -45f, 0f);
            _toggleObject.GetComponent<RectTransform>().sizeDelta = new Vector2(160f, 30f);
            UpdateToggleText();
            _toggleObject.SetActive(false);
        }

        private static void UpdateToggleText()
        {
            var text = _toggleObject != null ? _toggleObject.GetComponentInChildren<TMP_Text>() : null;
            if (text == null) return;
            text.text = _showEquipped.Value ? "[X] Include equipped" : "[  ] Include equipped";
            text.fontSize = 16f;
        }

        private static bool StationAllowed(CraftingStation station)
        {
            if (station == null) return false;
            var name = station.gameObject.name.ToLowerInvariant();
            foreach (var word in (_excludedStations.Value ?? "").Split(','))
            {
                var w = word.Trim().ToLowerInvariant();
                if (w.Length > 0 && name.Contains(w)) return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.OnTabCraftPressed))]
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.OnTabUpgradePressed))]
        [HarmonyPrefix]
        private static void OtherTab()
        {
            _active = false;
            if (_tab != null) _tab.interactable = true;
        }

        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Hide))]
        [HarmonyPostfix]
        private static void Hide() => _active = false;

        // Runs before the list is built (so the list matches the tab) and after (to show the tab state).
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.UpdateCraftingPanel))]
        [HarmonyPrefix]
        private static void PanelPrefix(InventoryGui __instance)
        {
            EnsureTab(__instance);
            var player = Player.m_localPlayer;
            if (_active && (player == null || !StationAllowed(player.GetCurrentCraftingStation()))) _active = false;
        }

        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.UpdateCraftingPanel))]
        [HarmonyPostfix]
        private static void PanelPostfix(InventoryGui __instance)
        {
            var player = Player.m_localPlayer;
            var show = player != null && StationAllowed(player.GetCurrentCraftingStation()) && __instance.m_tabUpgrade.gameObject.activeSelf;
            _tabObject.SetActive(show);
            if (!show) _active = false;
            _toggleObject.SetActive(_active);
            if (_active)
            {
                __instance.m_tabCraft.interactable = true;
                __instance.m_tabUpgrade.interactable = true;
                _tab.interactable = false;
            }
            else _tab.interactable = true;
        }

        // --- List ----------------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.UpdateRecipeList))]
        [HarmonyPostfix]
        private static void RecipeList(InventoryGui __instance, List<Recipe> recipes)
        {
            if (!_active) return;
            var player = Player.m_localPlayer;
            var inv = player.GetInventory();
            foreach (var pair in __instance.m_availableRecipes) Destroy(pair.InterfaceElement);
            __instance.m_availableRecipes.Clear();

            Equipped.Clear();
            foreach (var item in inv.GetEquippedItems()) Equipped.Add(item);
            Hotbar.Clear();
            Temp.Clear();
            inv.GetBoundItems(Temp);
            foreach (var item in Temp) Hotbar.Add(item);

            var station = player.GetCurrentCraftingStation();
            var entries = new List<KeyValuePair<Recipe, ItemDrop.ItemData>>();
            foreach (var recipe in recipes)
            {
                var shared = recipe.m_item?.m_itemData.m_shared;
                if (shared == null || shared.m_maxQuality < 1) continue;
                foreach (var item in inv.GetAllItems())
                {
                    if (item.m_shared.m_name != shared.m_name || item.m_quality < 1) continue;
                    if (shared.m_maxStackSize > 1 && item.m_stack < recipe.m_amount) continue;
                    if (Hidden(item)) continue;
                    entries.Add(new KeyValuePair<Recipe, ItemDrop.ItemData>(recipe, item));
                    if (shared.m_maxStackSize > 1) break; // one entry per stackable item
                }
            }
            if (_moulds.Value)
            {
                FindMoulds();
                entries.RemoveAll(e => MouldRecipes.ContainsKey(e.Value.m_shared.m_name));
                foreach (var item in inv.GetAllItems())
                {
                    if (!MouldRecipes.TryGetValue(item.m_shared.m_name, out var recipe) || Hidden(item)) continue;
                    var required = recipe.GetRequiredStation(item.m_quality);
                    if (required != null && station != null && required.m_name == station.m_name)
                        entries.Add(new KeyValuePair<Recipe, ItemDrop.ItemData>(recipe, item));
                }
            }
            // Free items first, equipped/hotbar last.
            entries = entries.OrderBy(e => Equipped.Contains(e.Value) || Hotbar.Contains(e.Value)).ToList();

            foreach (var entry in entries)
            {
                var recipe = entry.Key;
                var item = entry.Value;
                var can = (StationOk(recipe, item, station, player) || player.NoCostCheat()) && HaveSpace(inv, recipe, item);
                __instance.AddRecipeToList(player, recipe, item, can);
                var element = __instance.m_availableRecipes[__instance.m_availableRecipes.Count - 1].InterfaceElement;
                var icon = element.transform.Find("icon")?.GetComponent<Image>();
                if (icon != null) icon.sprite = item.GetIcon();
                var name = element.transform.Find("name")?.GetComponent<TMP_Text>();
                if (name == null) continue;
                if (recipe.m_item.m_itemData.m_shared.m_name != item.m_shared.m_name) name.text = Localization.instance.Localize(item.m_shared.m_name); // the recipe it was cast with
                if (Equipped.Contains(item)) name.text += " <color=yellow>(Equipped)</color>";
                else if (Hotbar.Contains(item)) name.text += " <color=yellow>(Hotbar)</color>";
            }
            __instance.m_recipeListRoot.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,
                Mathf.Max(__instance.m_recipeListBaseSize, __instance.m_availableRecipes.Count * __instance.m_recipeListSpace));
        }

        private static bool Hidden(ItemDrop.ItemData item) => !_showEquipped.Value && (Equipped.Contains(item) || Hotbar.Contains(item));

        private static bool IsMould(Piece.Requirement req) => req.m_resItem != null && req.m_resItem.name.StartsWith("Mold", StringComparison.Ordinal);

        // What a mould's recipe makes goes through a cooking station (the frost foundry) into the finished gear. That gear's
        // own recipe has the mould in it, except for the staffs: those use the recipe they were cast with (same materials).
        private static void FindMoulds()
        {
            if (_mouldsOf == ObjectDB.instance) return;
            _mouldsOf = ObjectDB.instance;
            MouldRecipes.Clear();
            var recipes = ObjectDB.instance.m_recipes;
            foreach (var prefab in ZNetScene.instance.m_prefabs)
            {
                var cooking = prefab != null ? prefab.GetComponent<CookingStation>() : null;
                if (cooking == null) continue;
                foreach (var conversion in cooking.m_conversion)
                {
                    if (conversion.m_from == null || conversion.m_to == null) continue;
                    var cast = recipes.FirstOrDefault(r => r.m_item == conversion.m_from && r.m_resources.Any(IsMould));
                    if (cast == null) continue;
                    MouldRecipes[conversion.m_to.m_itemData.m_shared.m_name] =
                        recipes.FirstOrDefault(r => r.m_item == conversion.m_to && r.m_resources.Any(IsMould)) ?? cast;
                }
            }
        }

        private static bool StationOk(Recipe recipe, ItemDrop.ItemData item, CraftingStation station, Player player)
        {
            var quality = item?.m_quality ?? 1;
            var required = recipe.GetRequiredStation(quality);
            return required == null || (station != null && station.CheckUsable(player, false) && station.GetLevel() >= recipe.GetRequiredStationLevel(quality));
        }

        private static int Returned(Piece.Requirement req, int quality) =>
            IsMould(req) ? req.GetAmount(quality) : (int)Math.Round(_returnPercent.Value / 100.0 * req.GetAmount(quality));

        private static IEnumerable<Piece.Requirement> Returns(Recipe recipe)
        {
            FindMoulds();
            var noMould = !_moulds.Value && recipe.m_item != null && MouldRecipes.ContainsKey(recipe.m_item.m_itemData.m_shared.m_name);
            foreach (var req in recipe.m_resources)
            {
                if (req.m_resItem == null || req.m_upgraderResource || (noMould && IsMould(req))) continue;
                yield return req;
                if (recipe.m_requireOnlyOneIngredient) yield break;
            }
        }

        private static bool HaveSpace(Inventory inv, Recipe recipe, ItemDrop.ItemData item)
        {
            var needed = 0;
            foreach (var req in Returns(recipe))
            {
                var amount = Returned(req, item.m_quality);
                if (amount <= 0) continue;
                var shared = req.m_resItem.m_itemData.m_shared;
                var rest = Mathf.Max(0, amount - inv.FindFreeStackSpace(shared.m_name, req.m_resItem.m_itemData.m_worldLevel));
                if (rest > 0) needed += Mathf.CeilToInt((float)rest / shared.m_maxStackSize);
            }
            var freed = item.m_quality <= 1 && item.m_shared.m_maxStackSize == 1 ? 1 : 0; // the recycled item leaves its slot
            return inv.GetEmptySlots() + freed >= needed;
        }

        // --- Selected item -------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(InventoryGui), "UpdateRecipe")]
        [HarmonyPostfix]
        private static void Recipe(InventoryGui __instance, Player player)
        {
            var label = __instance.m_craftProgressPanel != null ? __instance.m_craftProgressPanel.GetComponentInChildren<TMP_Text>() : null;
            if (!_active || __instance.m_selectedRecipe.Recipe == null)
            {
                if (label != null && label.text == "Recycling...") label.text = Localization.instance.Localize("$inventory_crafting");
                return;
            }
            var recipe = __instance.m_selectedRecipe.Recipe;
            var item = __instance.m_selectedRecipe.ItemData;
            if (item == null) return;
            var station = player.GetCurrentCraftingStation();
            var stationOk = StationOk(recipe, item, station, player);
            var space = HaveSpace(player.GetInventory(), recipe, item);

            var required = recipe.GetRequiredStation(item.m_quality);
            __instance.m_minStationLevelIcon.gameObject.SetActive(required != null);
            if (required != null)
            {
                var level = recipe.GetRequiredStationLevel(item.m_quality);
                __instance.m_minStationLevelText.text = level.ToString();
                __instance.m_minStationLevelText.color = station == null || station.GetLevel() < level
                    ? (Mathf.Sin(Time.time * 10f) > 0f ? Color.red : __instance.m_minStationLevelBasecolor)
                    : __instance.m_minStationLevelBasecolor;
            }
            var itemName = Localization.instance.Localize(item.m_shared.m_name);
            __instance.m_recipeIcon.sprite = item.GetIcon();
            __instance.m_recipeName.text = itemName + (recipe.m_amount > 1 ? " x" + recipe.m_amount : "");
            __instance.m_recipeDecription.enabled = false;
            __instance.m_itemCraftType.gameObject.SetActive(true);
            __instance.m_itemCraftType.text = item.m_quality <= 1 ? "Item will be recycled" : $"Lowers {itemName} to quality {item.m_quality - 1}";

            // Show what you get back: the requirement list with only the returned materials.
            var all = recipe.m_resources;
            recipe.m_resources = Returns(recipe).ToArray();
            try
            {
                __instance.SetupRequirementList(item.m_quality, player, true, 1);
            }
            finally
            {
                recipe.m_resources = all;
            }

            __instance.m_craftButton.interactable = (stationOk || player.NoCostCheat()) && space;
            __instance.m_craftButton.GetComponentInChildren<TMP_Text>().text = "Recycle";
            if (label != null) label.text = "Recycling...";
            var tooltip = __instance.m_craftButton.GetComponent<UITooltip>();
            if (tooltip != null)
                tooltip.m_text = !space ? Localization.instance.Localize("$inventory_full")
                    : !stationOk ? (station != null ? "Workstation level too low" : Localization.instance.Localize("$msg_missingstation"))
                    : "";
        }

        // In the Recycle tab the requirement amounts are what you get back, in green.
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.SetupRequirement))]
        [HarmonyPostfix]
        private static void Requirement(Transform elementRoot, Piece.Requirement req, int quality)
        {
            if (!_active || req == null) return;
            elementRoot.gameObject.SetActive(true);
            var amount = elementRoot.Find("res_amount")?.GetComponent<TMP_Text>();
            if (amount == null) return;
            amount.text = Returned(req, quality).ToString();
            amount.color = Color.green;
        }

        // --- Recycling -----------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(InventoryGui), "OnCraftPressed")]
        [HarmonyPrefix]
        private static bool CraftPressed(InventoryGui __instance)
        {
            if (!_active || _skipConfirm || !_confirm.Value) return true;
            var item = __instance.m_selectedRecipe.ItemData;
            var inv = Player.m_localPlayer?.GetInventory();
            if (item == null || inv == null) return true;
            Temp.Clear();
            inv.GetBoundItems(Temp);
            if (!item.m_equipped && !Temp.Contains(item)) return true;
            UnifiedPopup.Push(new YesNoPopup("Recycle item",
                "This item is equipped or on your hotbar.\nRecycle it anyway?",
                () =>
                {
                    UnifiedPopup.Pop();
                    _skipConfirm = true;
                    try { __instance.m_craftButton.onClick.Invoke(); }
                    finally { _skipConfirm = false; }
                },
                UnifiedPopup.Pop, false, false));
            return false;
        }

        [HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
        [HarmonyPrefix]
        private static bool DoCrafting(InventoryGui __instance, Player player)
        {
            if (!_active) return true;
            Recycle(__instance, player);
            return false;
        }

        private static void Recycle(InventoryGui gui, Player player)
        {
            var recipe = gui.m_craftRecipe;
            var item = gui.m_craftUpgradeItem;
            var inv = player.GetInventory();
            if (recipe == null || item == null || !inv.ContainsItem(item)) return;
            if (!HaveSpace(inv, recipe, item))
            {
                player.Message(MessageHud.MessageType.Center, "$inventory_full");
                return;
            }
            var quality = item.m_quality;
            var returns = Returns(recipe).Select(r => new KeyValuePair<Piece.Requirement, int>(r, Returned(r, quality))).ToList();

            if (item.m_equipped) player.UnequipItem(item);
            if (quality > 1)
            {
                if (_keepItem.Value)
                {
                    item.m_quality = quality - 1;
                    item.m_durability = Mathf.Min(item.m_durability, item.GetMaxDurability());
                }
                else
                {
                    inv.RemoveItem(item);
                    inv.AddItem(item.m_dropPrefab != null ? item.m_dropPrefab.name : recipe.m_item.gameObject.name, recipe.m_amount, quality - 1, item.m_variant,
                        player.GetPlayerID(), player.GetPlayerName(), false);
                }
            }
            else inv.RemoveItem(item, recipe.m_amount);

            foreach (var pair in returns)
            {
                if (pair.Value <= 0) continue;
                var res = pair.Key.m_resItem;
                inv.AddItem(res.gameObject.name, pair.Value, res.m_itemData.m_quality, res.m_itemData.m_variant, 0L, "", false);
            }

            var station = player.GetCurrentCraftingStation();
            (station != null ? station.m_craftItemDoneEffects : gui.m_craftItemDoneEffects)
                .Create(player.transform.position, Quaternion.identity);
            player.Message(MessageHud.MessageType.TopLeft, "Recycled " + Localization.instance.Localize(item.m_shared.m_name));
            gui.UpdateCraftingPanel(true);
        }
    }
}
