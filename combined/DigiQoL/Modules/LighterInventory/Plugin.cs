// An open inventory redraws every slot of every grid each frame, though hardly anything changes. Equipment Slots does
// the same for its panel and measures its layout again, and the recipe panel writes its texts and counts the ingredients
// again (with Local Craft From Storage: in all nearby chests). Together that is most of what an open inventory costs.
// This redraws when something changed (an item moved, used up or picked up, dragging started or ended, another recipe)
// and otherwise a few times a second, so a used item's durability bar still moves. While crafting the recipe panel runs
// every frame, as it runs the crafting timer. Equipment Slots' character preview keeps running smoothly, it is only
// dressed again at those moments. With a gamepad or touch every frame, as those select through it.
// Other mods' postfixes on the recipe panel still run every frame. Some add a line to the description and count on the
// game writing it anew; between redraws such a line would pile up and the text, which sizes itself to fit, would shrink
// and grow. So a frame without a redraw puts back the texts that were drawn.
// The recipe list: the game makes it anew (an object per recipe, the old ones thrown away) every time the inventory or a
// chest opens, and every row is switched on and its texts drawn with the window, also the rows scrolled out of view and
// the old ones still waiting to be destroyed. With many recipes that was one long frame: free building (Ultimate
// Builder, the game's no-cost mode) lists every recipe in the game, 449 with the Digi Modpack, and each opening took
// 200-250 ms (measured 2026-11-27: ~60 ms making the rows, ~90 ms drawing their texts, ~40 ms the window's animator
// taking in the new parts). So an unchanged list (the same recipes, each as craftable as before) is kept, the rows thrown
// away are switched off first, and only the rows in view (and a few around them) are switched on; scrolling switches on
// the rest as they come into view.
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace LighterInventory
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.lighterinventory";
        public const string Name = "Digi's Lighter Inventory";
        public const string Version = "1.0.0";

        private sealed class Drawn
        {
            public Inventory Inventory;
            public ItemDrop.ItemData Drag;
            public int Changes;
            public float At;
        }

        private static readonly AccessTools.FieldRef<InventoryGui, ItemDrop.ItemData> DragItem =
            AccessTools.FieldRefAccess<InventoryGui, ItemDrop.ItemData>("m_dragItem");
        private static readonly Dictionary<Object, Drawn> Screens = new Dictionary<Object, Drawn>();
        private static ConfigEntry<int> _interval;
        private static AccessTools.FieldRef<int> _previewFrame;
        private static System.Func<bool> _infoTab; // Equipment Slots 0.1.20+: its Info tab shows no character preview
        private static int _changes;
        private static Recipe _recipe;
        private static ItemDrop.ItemData _recipeItem;
        private static int _recipeVariant, _recipeAmount;
        private static bool _recipeAlt, _recipeSkipped;
        private static string _drawnName, _drawnText;

        private void Awake()
        {
            _interval = Config.Bind("General", "Redraw at least every (ms)", 100,
                new ConfigDescription("How often items are redrawn when nothing changed. Anything that changes is redrawn at once. 0 = every frame, like normal.",
                    new AcceptableValueRange<int>(0, 500)));
            PatchModule(typeof(Plugin));

            var refresh = AccessTools.Method(AccessTools.TypeByName("CDEV.EquipmentSlots.SlotPanel"), "Refresh");
            var previewFrame = AccessTools.Field(AccessTools.TypeByName("CDEV.EquipmentSlots.PlayerPreview"), "s_lastFrame");
            if (refresh == null || previewFrame == null) return;
            _previewFrame = AccessTools.StaticFieldRefAccess<int>(previewFrame);
            var infoMode = AccessTools.PropertyGetter(AccessTools.TypeByName("CDEV.EquipmentSlots.SlotPanel"), "InfoMode");
            _infoTab = infoMode != null ? (System.Func<bool>)System.Delegate.CreateDelegate(typeof(System.Func<bool>), infoMode) : null;
            ModuleHarmony.Patch(refresh, prefix: new HarmonyMethod(typeof(Plugin), nameof(Panel)));
        }

        private void OnDestroy()
        {
            Screens.Clear();
            ShowAllRows();
            Unhook();
        }

        // --- The recipe list -----------------------------------------------------------------------------------------

        private static readonly Dictionary<Recipe, bool> Listed = new Dictionary<Recipe, bool>();
        private static readonly Vector3[] Corners = new Vector3[4];
        private static InventoryGui _listGui;
        private static ScrollRect _listScroll;
        private static string _listOrder;

        // First, before other mods: a list that would come out the same is kept (the postfixes still run, as after a
        // rebuild). Otherwise the rows about to be thrown away are switched off.
        [HarmonyPatch(typeof(InventoryGui), "UpdateRecipeList")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool KeepList(InventoryGui __instance, List<Recipe> recipes)
        {
            if (Unchanged(__instance, recipes)) return false;
            foreach (var pair in __instance.m_availableRecipes)
                if (pair.InterfaceElement != null) pair.InterfaceElement.SetActive(false);
            return true;
        }

        // The craft tab's rows as the game would make them now: every row still there, one per recipe (none filtered out
        // by a search, none of another tab's), each as craftable as the game would mark it (its own check), in the same
        // sort order and language.
        private static bool Unchanged(InventoryGui gui, List<Recipe> recipes)
        {
            var player = Player.m_localPlayer;
            var rows = gui.m_availableRecipes;
            if (player == null || recipes == null || rows.Count == 0 || !gui.InCraftTab()) return false;
            player.TryGetUniqueKeyValue("sortcraft", out var sort);
            var order = sort + "|" + (Localization.instance != null ? Localization.instance.GetSelectedLanguage() : "");
            if (order != _listOrder)
            {
                _listOrder = order;
                return false;
            }
            Listed.Clear();
            try
            {
                foreach (var pair in rows)
                {
                    if (pair.InterfaceElement == null || pair.ItemData != null || pair.Recipe == null || Listed.ContainsKey(pair.Recipe)) return false;
                    Listed[pair.Recipe] = pair.CanCraft;
                }
                var free = ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoCraftCost);
                var count = 0;
                foreach (var recipe in recipes)
                {
                    if (recipe == null || recipe.m_noCraftOnlyUpgrade) continue;
                    if (!Listed.TryGetValue(recipe, out var could) || could != (player.HaveRequirements(recipe, false, 1) || free)) return false;
                    count++;
                }
                return count == Listed.Count;
            }
            finally { Listed.Clear(); }
        }

        // After the whole panel (the list's only caller): every mod that sorts, filters or places the rows is done, in
        // whatever order their list postfixes ran, and the window is not switched on yet (that comes later in the frame).
        [HarmonyPatch(typeof(InventoryGui), "UpdateCraftingPanel")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void ListDone(InventoryGui __instance)
        {
            if (_listGui != __instance || _listScroll == null) Hook(__instance);
            ShowRowsInView();
        }

        private static void Hook(InventoryGui gui)
        {
            Unhook();
            _listGui = gui;
            for (var t = gui.m_recipeListRoot != null ? gui.m_recipeListRoot.parent : null; t != null && _listScroll == null; t = t.parent)
                _listScroll = t.GetComponent<ScrollRect>();
            if (_listScroll != null) _listScroll.onValueChanged.AddListener(Scrolled);
        }

        private static void Unhook()
        {
            if (_listScroll != null) _listScroll.onValueChanged.RemoveListener(Scrolled);
            _listScroll = null;
            _listGui = null;
        }

        private static void Scrolled(Vector2 position) => ShowRowsInView();

        // Rows within the list's view, plus three rows' height above and below, on; the rest off. Measured in the list's
        // own space (the view's corners brought into it). A view not laid out yet (no height): everything on.
        private static void ShowRowsInView()
        {
            var gui = _listGui;
            if (gui == null || _listScroll == null || gui.m_recipeListRoot == null) return;
            var content = gui.m_recipeListRoot;
            var view = _listScroll.viewport != null ? _listScroll.viewport : (RectTransform)_listScroll.transform;
            view.GetWorldCorners(Corners);
            var low = content.InverseTransformPoint(Corners[0]).y;
            var high = content.InverseTransformPoint(Corners[1]).y;
            var all = high - low < 1f;
            var margin = gui.m_recipeListSpace * 3f;
            foreach (var pair in gui.m_availableRecipes)
            {
                var row = pair.InterfaceElement;
                if (row == null || !(row.transform is RectTransform rect) || rect.parent != content) continue;
                var y = rect.localPosition.y;
                var size = rect.rect;
                var on = all || (y + size.yMax >= low - margin && y + size.yMin <= high + margin);
                if (row.activeSelf != on) row.SetActive(on);
            }
        }

        // Switched off: the whole list on again.
        private static void ShowAllRows()
        {
            if (_listGui == null) return;
            foreach (var pair in _listGui.m_availableRecipes)
                if (pair.InterfaceElement != null && !pair.InterfaceElement.activeSelf) pair.InterfaceElement.SetActive(true);
        }

        [HarmonyPatch(typeof(Inventory), "Changed")]
        [HarmonyPostfix]
        private static void Changed() => _changes++;

        [HarmonyPatch(typeof(InventoryGrid), nameof(InventoryGrid.UpdateInventory))]
        [HarmonyPrefix]
        private static bool Grid(InventoryGrid __instance, Inventory inventory, ItemDrop.ItemData dragItem) =>
            Every() || Due(__instance, inventory, dragItem);

        // Last, after Craft Batch has set the amount that goes with the keys held.
        [HarmonyPatch(typeof(InventoryGui), "UpdateRecipe")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        private static bool RecipePanel(InventoryGui __instance)
        {
            _recipeSkipped = false;
            if (Every() || __instance.m_craftTimer >= 0f) return true;
            var selected = __instance.m_selectedRecipe;
            var alt = ZInput.GetButton("AltPlace");
            var screen = __instance.m_craftButton;
            if (selected.Recipe != _recipe || selected.ItemData != _recipeItem || __instance.m_selectedVariant != _recipeVariant ||
                __instance.m_multiCraftAmount != _recipeAmount || alt != _recipeAlt)
            {
                _recipe = selected.Recipe;
                _recipeItem = selected.ItemData;
                _recipeVariant = __instance.m_selectedVariant;
                _recipeAmount = __instance.m_multiCraftAmount;
                _recipeAlt = alt;
                Screens.Remove(screen);
            }
            _recipeSkipped = !Due(screen, null, null);
            return !_recipeSkipped;
        }

        [HarmonyPatch(typeof(InventoryGui), "UpdateRecipe")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void RecipeTexts(InventoryGui __instance)
        {
            var name = __instance.m_recipeName;
            var text = __instance.m_recipeDecription;
            if (_recipeSkipped)
            {
                if (name.text != _drawnName) name.text = _drawnName;
                if (text.text != _drawnText) text.text = _drawnText;
            }
            else
            {
                _drawnName = name.text;
                _drawnText = text.text;
            }
            _recipeSkipped = false;
        }

        private static bool Panel(InventoryGui gui, Player player)
        {
            if (Every() || player == null || Due(gui, player.GetInventory(), DragItem(gui))) return true;
            // It switches its camera off when it was not asked for a frame; keep it on, except on the Info tab (no preview).
            if (_infoTab == null || !_infoTab()) _previewFrame() = Time.frameCount;
            return false;
        }

        private static bool Every() => _interval.Value <= 0 || ZInput.IsGamepadActive() || ZInput.IsTouchActive();

        private static bool Due(Object screen, Inventory inventory, ItemDrop.ItemData drag)
        {
            if (!Screens.TryGetValue(screen, out var drawn)) Screens[screen] = drawn = new Drawn();
            var now = Time.unscaledTime;
            if (drawn.Inventory == inventory && drawn.Drag == drag && drawn.Changes == _changes && now - drawn.At < _interval.Value / 1000f) return false;
            drawn.Inventory = inventory;
            drawn.Drag = drag;
            drawn.Changes = _changes;
            drawn.At = now;
            return true;
        }
    }
}
