// Cookbook: press a key (default K) to open a book of every food in the game you have found so far, in a window that
// looks like the crafting menu. Health, stamina, eitr, total, duration and healing per food, where it is made and
// from what, grouped by food type or station, sortable by any column. A checkbox also lists foods not found yet, and
// a search box finds foods by name or ingredient. Meads have a list of their own below the food; an arrow folds each away.
// Food from other mods (HoneyPlus, ...) is included automatically: everything is read from the game's item data.
// While the book is open the mouse is free and your character ignores movement and attack input, like the inventory.
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Cookbook
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.cookbook";
        public const string Name = "Digi's Cookbook";
        public const string Version = "1.0.0";

        internal static ConfigEntry<KeyCode> Key;
        internal static ConfigEntry<bool> ShowUndiscovered, SortDescending, ShowFood, ShowMeads;
        internal static ConfigEntry<GroupBy> Grouping;
        internal static ConfigEntry<Column> SortColumn;
        internal static ConfigEntry<int> WindowWidth, WindowHeight, ScrollRows;
        internal static ConfigEntry<bool> CookingBonusShown;

        internal static BepInEx.Logging.ManualLogSource Log;
        private static CookbookWindow _window;
        private Harmony _harmony;



        private void Awake()
        {
            Key = Config.Bind("1 - General", "Key", KeyCode.K, "Key that opens and closes the cookbook.");
            ShowUndiscovered = Config.Bind("1 - General", "Show undiscovered", false, "Also list foods you have not found yet (the checkbox in the book).");
            Grouping = Config.Bind("1 - General", "Group by", GroupBy.Type, "Type: health, stamina, eitr and balanced food. Station: where it is made. None: one list.");
            SortColumn = Config.Bind("1 - General", "Sort by", Column.Total, "Column the list is sorted by (click a column title in the book to change it).");
            SortDescending = Config.Bind("1 - General", "Highest first", true, "Sort from high to low (click the same column title again to flip it).");
            CookingBonusShown = Config.Bind("1 - General", "Show Cooking skill bonus", true,
                "With Cooking Mastery installed: show what your Cooking skill adds to each food, e.g. \"40 +10\".");
            ShowFood = Config.Bind("1 - General", "Show food", true, "The food list is open (the arrow next to Food in the book).");
            ShowMeads = Config.Bind("1 - General", "Show meads", true, "The meads list is open (the arrow next to Meads in the book).");
            ScrollRows = Config.Bind("2 - Window", "Scroll speed (rows)", 2,
                new ConfigDescription("How many rows one mouse wheel step scrolls.", new AcceptableValueRange<int>(1, 10)));
            WindowWidth = Config.Bind("2 - Window", "Width", 1240,
                new ConfigDescription("Width of the book, in the game's UI units (1920 = full width). Applies the next time you start the game.", new AcceptableValueRange<int>(1000, 1900)));
            WindowHeight = Config.Bind("2 - Window", "Height", 780,
                new ConfigDescription("Height of the book (1080 = full height). Applies the next time you start the game.", new AcceptableValueRange<int>(500, 1050)));
            Log = Logger;
            _window = new CookbookWindow();
            // Own patch ID per load, so a hot reload (ScriptEngine) never removes the new copy's patches with the old one's.
            _harmony = PatchModule(typeof(Plugin));
        }

        // Lets the mod be reloaded while the game runs (ScriptEngine): remove everything it added.
        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            _window?.Destroy();
            _window = null;
        }

        internal static bool IsOpen => _window != null && _window.IsOpen;
        internal static bool Searching => _window != null && _window.Searching;

        internal static void CloseWindow() => _window?.Close();

        private void Update()
        {
            if (_window == null) return;
            _window.Prewarm();
            var player = Player.m_localPlayer;
            if (IsOpen && (player == null || player.IsDead() || InventoryGui.IsVisible() || Minimap.IsOpen() || StoreGui.IsVisible()))
            {
                _window.Close();
                return;
            }
            if (IsOpen)
            {
                _readingWheel = true;
                var wheel = ZInput.GetMouseScrollWheel();
                _readingWheel = false;
                _window.Scroll(wheel, Time.unscaledDeltaTime);
            }
            if (player == null || !UnityInput.Current.GetKeyDown(Key.Value) || Typing()) return;
            if (IsOpen) _window.Close();
            else if (!InventoryGui.IsVisible() && !Menu.IsVisible() && !Minimap.IsOpen() && !StoreGui.IsVisible() && !Hud.IsPieceSelectionVisible())
            {
                try
                {
                    _window.Open();
                }
                catch (System.Exception e)
                {
                    Logger.LogError($"Could not open the cookbook: {e}");
                    _window.Close();
                }
            }
        }

        private static bool Typing() =>
            (Chat.instance != null && Chat.instance.HasFocus()) || Console.IsVisible() || TextInput.IsVisible() || Minimap.InTextInput() ||
            (Hud.instance != null && Hud.instance.m_buildUi != null && Hud.instance.m_buildUi.SearchFieldFocused); // the build menu's search box

        // --- While the book is open: free mouse, no character input, no camera zoom, Escape closes it -----------

        [HarmonyPatch(typeof(PlayerController), "TakeInput")]
        [HarmonyPostfix]
        private static void NoInput(ref bool __result)
        {
            if (IsOpen) __result = false;
        }

        [HarmonyPatch(typeof(GameCamera), nameof(GameCamera.UpdateMouseCapture))]
        [HarmonyPostfix]
        private static void FreeMouse()
        {
            if (!IsOpen) return;
            ZCursor.LockState = CursorLockMode.None;
            ZCursor.Show();
        }

        // The mouse wheel scrolls the book only: everything else in the game reads 0 while it is open (camera zoom, ...).
        private static bool _readingWheel;

        [HarmonyPatch(typeof(ZInput), "Internal_GetMouseScrollWheel")]
        [HarmonyPostfix]
        private static void BookGetsWheel(ref float __result)
        {
            if (IsOpen && !_readingWheel) __result = 0f;
        }

        // Typing in the book's search box counts as typing for the game: M, Enter, Escape and the book's key go to the box.
        [HarmonyPatch(typeof(TextInput), nameof(TextInput.IsVisible))]
        [HarmonyPostfix]
        private static void SearchTyping(ref bool __result)
        {
            if (Searching) __result = true;
        }

        [HarmonyPatch(typeof(Menu), "Update")]
        [HarmonyPrefix]
        private static bool EscapeCloses()
        {
            if (!IsOpen) return true;
            if (Searching) return false; // Escape leaves the search box first
            if (ZInput.GetKeyDown(KeyCode.Escape) || ZInput.GetButtonDown("Menu")) CloseWindow();
            return false;
        }
    }
}
