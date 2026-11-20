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
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

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

        private void OnDestroy() => Screens.Clear();

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
