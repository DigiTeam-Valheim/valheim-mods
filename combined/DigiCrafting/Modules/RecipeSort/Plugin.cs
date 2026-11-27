// Recipe Sort and Search: crafting stations list their recipes by kind (weapons, shields, ammo, armor, ... in a set
// order), then by biome (Meadows first), then A-Z; each step can be switched off. Sorted by kind, a heading row
// ("Weapons", "Shields", ...) stands above each kind. A search box under the list keeps the
// recipes whose name, ingredient, kind or biome contains every word you type. The Recycle tab keeps its own order but
// can be searched too. The biome of an item is worked out once per world (Tiers.cs).
// With MyLittleUI's own crafting filter in place, no second box is added; the sorting still works.
// Type to search: opening a crafting station puts the cursor in the box (not with a gamepad); Tab and Esc still close.
// While the box has the cursor, other mods' hotkeys read as not pressed (ZInput), so typing never triggers them.
// Light: the list is only re-sorted when the game rebuilds it (opening a station, crafting, a tab, typing).
using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RecipeSort
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.recipesort";
        public const string Name = "Digi's Recipe Sort and Search";
        public const string Version = "1.1.0";

        internal static ConfigEntry<bool> ByKind, ByType, ByBiome, NewestFirst, CraftableFirst, ShowSearch, TypeToSearch, SearchIngredients, Headings;
        internal static ConfigEntry<string> KindOrder;

        private const float BoxHeight = 32f;
        private const float HeadingHeight = 26f;
        private const string HeadingName = "digi_kind_heading";
        private static readonly List<GameObject> HeadingRows = new List<GameObject>();
        private static TMP_InputField _box;
        private static RectTransform _list; // the recipe list's panel, made shorter to make room for the box
        private static Vector2 _listOffsetMin;
        private static bool _boxFailed;
        private static bool _focusPending; // a station was just opened: the box gets the cursor once the screen is up
        private static string[] _words = new string[0];
        private static readonly Dictionary<Recipe, string> SearchText = new Dictionary<Recipe, string>();
        private static readonly List<Entry> Entries = new List<Entry>();

        private sealed class Entry
        {
            public InventoryGui.RecipeDataPair Pair;
            public bool CanCraft;
            public int Kind, Type, Tier, Quality, Index;
            public string Name;
        }

        private void Awake()
        {
            ByKind = Config.Bind("Sorting", "Sort by kind", true,
                "Group the list by kind of item, in the order of Kind order.");
            KindOrder = Config.Bind("Sorting", "Kind order", Kinds.DefaultOrder,
                "The order of the kinds, separated by commas. Kinds left out come after, in this default order: " + Kinds.DefaultOrder + ".");
            // Throwables are a kind since Crafting and Automation 1.0.16: an order never changed gets them after the weapons.
            if (FirstTime("throwables kind") && KindOrder.Value == Kinds.OldDefaultOrder) KindOrder.Value = Kinds.DefaultOrder;
            ByBiome = Config.Bind("Sorting", "Sort by biome", true,
                "Within a kind, items of earlier biomes first: Meadows, Black Forest, Swamp, Ocean, Mountains, Plains, Mistlands, " +
                "Ashlands, Deep North. The biome comes from where an item's materials are found; items it cannot tell go last.");
            ByType = Config.Bind("Sorting", "Sort by type", true,
                "Within weapons and armor, by type before the biome: helmets, chest pieces, leggings, gloves, capes; swords, " +
                "axes, maces, knives, spears, atgeirs, fists, bows, crossbows, staffs (one-handed before two-handed).");
            Headings = Config.Bind("Sorting", "Kind headings", true,
                "A heading above each kind in the list (Weapons, Shields, Armor, ...) while it is sorted by kind. Not with " +
                "Craftable first (the kinds come twice there) and not in the Recycle tab.");
            NewestFirst = Config.Bind("Sorting", "Newest biome first", false,
                "Turn the biome order around: the latest biome's items at the top.");
            CraftableFirst = Config.Bind("Sorting", "Craftable first", false,
                "Items you can make right now go to the top, as in vanilla, before kind and biome.");
            ShowSearch = Config.Bind("Search", "Search box", true,
                "A search box under the recipe list at crafting stations.");
            TypeToSearch = Config.Bind("Search", "Type to search", true,
                "Opening a crafting station puts the cursor in the search box: just start typing, no click needed. Tab and " +
                "Esc still close the station; E types an e (click a recipe and E closes as usual). Off: click the box to type.");
            SearchIngredients = Config.Bind("Search", "Search ingredients too", true,
                "The search also finds recipes by their ingredients (\"bronze\" finds everything made with bronze), " +
                "by kind (\"armor\") and by biome (\"plains\").");
            foreach (var entry in new[] { ByKind, ByType, ByBiome, NewestFirst, CraftableFirst, ShowSearch, TypeToSearch, SearchIngredients, Headings })
            {
                entry.SettingChanged -= Changed;
                entry.SettingChanged += Changed;
            }
            KindOrder.SettingChanged -= Changed;
            KindOrder.SettingChanged += Changed;
            PatchModule(typeof(Plugin));
            // While the box has the cursor, every key read through the game's ZInput reads "not pressed": other mods'
            // hotkeys (CDEV's quick slots on C and V ate food while typing) stay quiet. The box reads the keyboard itself.
            var quiet = new HarmonyMethod(typeof(Plugin), nameof(QuietKeys));
            foreach (var m in typeof(ZInput).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance))
            {
                if (m.ReturnType != typeof(bool) || Array.IndexOf(KeyReads, m.Name) < 0) continue;
                try { ModuleHarmony.Patch(m, prefix: quiet); }
                catch (Exception e) { Logger.LogWarning($"Could not quiet ZInput.{m.Name} while typing: {e.Message}"); }
            }
        }

        private static readonly string[] KeyReads = { "GetKeyDown", "GetKey", "GetKeyUp", "GetButtonDown", "GetButton", "GetButtonUp" };

        private void OnDestroy()
        {
            foreach (var entry in new[] { ByKind, ByType, ByBiome, NewestFirst, CraftableFirst, ShowSearch, TypeToSearch, SearchIngredients, Headings })
                if (entry != null) entry.SettingChanged -= Changed;
            ClearHeadings();
            Sorted.Clear();
            if (KindOrder != null) KindOrder.SettingChanged -= Changed;
            RemoveBox();
            _words = new string[0];
            SearchText.Clear();
            Tiers.Forget();
        }

        private static void Changed(object sender, EventArgs e)
        {
            SearchText.Clear();
            Sorted.Clear();
            if (_box != null) _box.gameObject.SetActive(ShowSearch.Value);
            if (!ShowSearch.Value) SetQuery("");
            PlaceList();
            if (InventoryGui.instance != null && InventoryGui.IsVisible()) InventoryGui.instance.UpdateCraftingPanel();
        }

        // --- Sorting and filtering -----------------------------------------------------------------------------------

        // Recycle Tab's own tab keeps its own order. Recycle Tab may sit in this mod or be a mod of its own (the Hexium
        // singles), so it is found by name in every loaded mod, once.
        private static System.Reflection.PropertyInfo[] _recycleTabs;

        private static bool RecycleTabOpen
        {
            get
            {
                if (_recycleTabs == null)
                {
                    var found = new List<System.Reflection.PropertyInfo>();
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type type = null;
                        try { type = assembly.GetType("RecycleTab.Plugin"); }
                        catch { }
                        var active = type?.GetProperty("Active", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        if (active != null && active.PropertyType == typeof(bool)) found.Add(active);
                    }
                    _recycleTabs = found.ToArray();
                }
                foreach (var active in _recycleTabs)
                    if ((bool)active.GetValue(null)) return true;
                return false;
            }
        }

        // The rows as this left them, and the search they were filtered by: Inventory and QoL's Lighter Inventory keeps an
        // unchanged list (the game does not make it anew), and then there is nothing to sort, filter or head again
        // (sorting 449 rows of a free-building character again took 13-17 ms per opening, 2026-11-27).
        private static readonly List<GameObject> Sorted = new List<GameObject>();
        private static string _sortedFor;

        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.UpdateRecipeList))]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void SortList(InventoryGui __instance)
        {
            var list = __instance.m_availableRecipes;
            var words = string.Join(" ", _words);
            if (AsLeft(list, words)) return;
            Sort(__instance, list);
            Sorted.Clear();
            foreach (var pair in list) Sorted.Add(pair.InterfaceElement);
            _sortedFor = words;
        }

        private static bool AsLeft(List<InventoryGui.RecipeDataPair> list, string words)
        {
            if (Sorted.Count == 0 || list.Count != Sorted.Count || words != _sortedFor) return false;
            for (var i = 0; i < list.Count; i++)
                if (list[i].InterfaceElement == null || list[i].InterfaceElement != Sorted[i]) return false;
            return true;
        }

        private static void Sort(InventoryGui __instance, List<InventoryGui.RecipeDataPair> list)
        {
            var changed = ClearHeadings();
            if (_words.Length > 0)
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    if (Matches(list[i].Recipe)) continue;
                    // Off before it goes: destroyed only at the end of the frame, it was switched on and drawn with the
                    // window until then.
                    if (list[i].InterfaceElement != null) list[i].InterfaceElement.SetActive(false);
                    Destroy(list[i].InterfaceElement);
                    list.RemoveAt(i);
                    changed = true;
                }
            if (!RecycleTabOpen && list.Count > 1 && (ByKind.Value || ByBiome.Value || !CraftableFirst.Value))
            {
                Entries.Clear();
                for (var i = 0; i < list.Count; i++)
                {
                    var pair = list[i];
                    var item = pair.Recipe != null ? pair.Recipe.m_item : null;
                    var kind = Kinds.Of(item);
                    Entries.Add(new Entry
                    {
                        Pair = pair,
                        CanCraft = pair.CanCraft,
                        Kind = ByKind.Value ? Kinds.Rank(kind) : 0,
                        Type = ByKind.Value && ByType.Value ? Kinds.TypeOf(item, kind) : 0,
                        Tier = ByBiome.Value ? Tiers.Of(item) : 0,
                        Quality = pair.ItemData != null ? pair.ItemData.m_quality : 0,
                        Index = i,
                        Name = item != null ? Localization.instance.Localize(item.m_itemData.m_shared.m_name) : "",
                    });
                }
                Entries.Sort(Compare);
                list.Clear();
                foreach (var entry in Entries) list.Add(entry.Pair);
                Entries.Clear();
                changed = true;
            }
            var headings = Headings.Value && ByKind.Value && !CraftableFirst.Value && !RecycleTabOpen && list.Count > 0;
            if (!changed && !headings) return;
            var y = 0f;
            var last = -1;
            for (var i = 0; i < list.Count; i++)
            {
                var row = (RectTransform)list[i].InterfaceElement.transform;
                if (headings)
                {
                    var kind = Kinds.Of(list[i].Recipe != null ? list[i].Recipe.m_item : null);
                    if (i == 0 || kind != last) AddHeading(__instance.m_recipeListRoot, row, Kinds.Name(kind), ref y);
                    last = kind;
                }
                row.anchoredPosition = new Vector2(0f, -y);
                y += __instance.m_recipeListSpace;
            }
            __instance.m_recipeListRoot.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, Mathf.Max(__instance.m_recipeListBaseSize, y));
        }

        // A row above a kind: its name in the list's font and the gold of the game's headers, between two thin lines.
        // Not clickable; the recipes below it move down by its height. Built fresh with the list (only when it changes).
        private static void AddHeading(RectTransform root, RectTransform row, string name, ref float y)
        {
            var heading = new GameObject(HeadingName, typeof(RectTransform));
            var rect = (RectTransform)heading.transform;
            rect.SetParent(root, false);
            rect.anchorMin = row.anchorMin;
            rect.anchorMax = row.anchorMax;
            rect.pivot = row.pivot;
            rect.sizeDelta = new Vector2(row.sizeDelta.x, HeadingHeight);
            rect.anchoredPosition = new Vector2(0f, -y);
            HeadingRows.Add(heading);
            y += HeadingHeight;

            var colour = new Color(1f, 0.78f, 0.38f);
            var template = row.Find("name") != null ? row.Find("name").GetComponent<TMP_Text>() : row.GetComponentInChildren<TMP_Text>();
            // Made switched off and given its font before it wakes: a text waking without a font loads a default font the
            // game does not have and logs a warning (11 per opening, one per heading).
            var label = new GameObject("text", typeof(RectTransform));
            label.SetActive(false);
            var labelRect = (RectTransform)label.transform;
            labelRect.SetParent(rect, false);
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;
            var text = label.AddComponent<TextMeshProUGUI>();
            if (template != null) text.font = template.font;
            text.fontSize = 16f;
            text.color = colour;
            text.alignment = TextAlignmentOptions.Center;
            text.enableWordWrapping = false;
            text.raycastTarget = false;
            text.text = name;
            label.SetActive(true);

            var width = row.rect.width > 0f ? row.rect.width : 300f;
            var half = text.GetPreferredValues(name).x / 2f + 8f;
            foreach (var side in new[] { -1f, 1f })
            {
                var line = new GameObject("line", typeof(RectTransform), typeof(Image));
                var lineRect = (RectTransform)line.transform;
                lineRect.SetParent(rect, false);
                lineRect.anchorMin = lineRect.anchorMax = new Vector2(0.5f, 0.5f);
                var length = Mathf.Max(0f, width / 2f - half - 10f);
                lineRect.sizeDelta = new Vector2(length, 1.5f);
                lineRect.anchoredPosition = new Vector2(side * (half + length / 2f), 0f);
                var image = line.GetComponent<Image>();
                image.color = new Color(colour.r, colour.g, colour.b, 0.55f);
                image.raycastTarget = false;
            }
        }

        // True when there were headings (the rows must then be placed again).
        private static bool ClearHeadings()
        {
            if (HeadingRows.Count == 0) return false;
            foreach (var heading in HeadingRows)
            {
                if (heading == null) continue;
                heading.SetActive(false);
                Destroy(heading);
            }
            HeadingRows.Clear();
            return true;
        }

        private static int Compare(Entry a, Entry b)
        {
            int c;
            if (CraftableFirst.Value && (c = b.CanCraft.CompareTo(a.CanCraft)) != 0) return c;
            if ((c = a.Kind.CompareTo(b.Kind)) != 0) return c;
            if ((c = a.Type.CompareTo(b.Type)) != 0) return c;
            if (a.Tier != b.Tier)
            {
                if (a.Tier >= Tiers.Unknown || b.Tier >= Tiers.Unknown || !NewestFirst.Value) return a.Tier.CompareTo(b.Tier);
                return b.Tier.CompareTo(a.Tier);
            }
            if ((c = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase)) != 0) return c;
            if ((c = b.Quality.CompareTo(a.Quality)) != 0) return c; // upgrade tab: highest quality first, as in vanilla
            return a.Index.CompareTo(b.Index);
        }

        private static bool Matches(Recipe recipe)
        {
            if (recipe == null || recipe.m_item == null) return false;
            if (!SearchText.TryGetValue(recipe, out var text))
            {
                var sb = new StringBuilder();
                var item = recipe.m_item;
                sb.Append(Localization.instance.Localize(item.m_itemData.m_shared.m_name)).Append('\n');
                if (SearchIngredients.Value)
                {
                    var kind = Kinds.Of(item);
                    var tier = Tiers.Of(item);
                    sb.Append(Kinds.Name(kind)).Append('\n').Append(Tiers.Name(tier)).Append('\n').Append(Tiers.EnglishName(tier)).Append('\n');
                    if (recipe.m_resources != null)
                        foreach (var r in recipe.m_resources)
                            if (r?.m_resItem != null) sb.Append(Localization.instance.Localize(r.m_resItem.m_itemData.m_shared.m_name)).Append('\n');
                }
                SearchText[recipe] = text = sb.ToString().ToLowerInvariant();
            }
            foreach (var word in _words)
                if (text.IndexOf(word, StringComparison.Ordinal) < 0) return false;
            return true;
        }

        // --- Search box ----------------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.UpdateCraftingPanel))]
        [HarmonyPrefix]
        private static void BeforePanel(InventoryGui __instance) => EnsureBox(__instance);

        // A fresh list each time the inventory opens; the box lets go of the keys.
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Hide))]
        [HarmonyPostfix]
        private static void Closed()
        {
            _focusPending = false;
            if (_box != null)
            {
                if (_box.text.Length > 0) _box.SetTextWithoutNotify("");
                if (_box.isFocused) _box.DeactivateInputField();
                if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject == _box.gameObject)
                    EventSystem.current.SetSelectedGameObject(null);
            }
            _words = new string[0];
        }

        // Opened at a crafting station (not a chest, not the inventory key alone).
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Show))]
        [HarmonyPostfix]
        private static void Opened()
        {
            var player = Player.m_localPlayer;
            _focusPending = ShowSearch.Value && TypeToSearch.Value && player != null && player.GetCurrentCraftingStation() != null;
        }

        // While the box has the keys the game does not read them (it counts as chat): Tab and Esc close it all the same,
        // the way the game does (not while something is being crafted).
        [HarmonyPatch(typeof(InventoryGui), "Update")]
        [HarmonyPrefix]
        private static void CloseKeys(InventoryGui __instance)
        {
            if (_box == null || !_box.isFocused || !InventoryGui.IsVisible() || __instance.m_craftTimer >= 0f) return;
            bool close;
            _ownRead = true;
            try { close = ZInput.GetKeyDown(KeyCode.Escape) || ZInput.GetButtonDown("Inventory"); }
            finally { _ownRead = false; }
            if (!close) return;
            ZInput.ResetButtonStatus("Inventory");
            ZInput.ResetButtonStatus("JoyButtonB");
            ZInput.ResetButtonStatus("JoyButtonY");
            ZInput.ResetButtonStatus("Use");
            __instance.Hide();
        }

        // Two frames after opening (the key that opened the station must not land in the box), with a keyboard only.
        [HarmonyPatch(typeof(InventoryGui), "Update")]
        [HarmonyPostfix]
        private static void FocusBox(InventoryGui __instance)
        {
            if (!_focusPending) return;
            if (!InventoryGui.IsVisible()) { _focusPending = false; return; }
            if (__instance.m_shownFrames < 2 || _box == null || !_box.gameObject.activeInHierarchy) return;
            _focusPending = false;
            if (ZInput.IsGamepadActive() || (Chat.instance != null && Chat.instance.HasFocus()) || Console.IsVisible()) return;
            _box.Select();
            _box.ActivateInputField();
        }

        // Typing in the box counts as chat for the game: Tab, E, Escape and the number keys go to the box.
        [HarmonyPatch(typeof(Chat), nameof(Chat.HasFocus))]
        [HarmonyPostfix]
        private static void Typing(ref bool __result)
        {
            if (_box != null && _box.isFocused) __result = true;
        }

        private static bool _ownRead; // CloseKeys reads Tab and Esc itself while typing

        private static bool QuietKeys(ref bool __result)
        {
            if (_ownRead || _box == null || !_box.isFocused) return true;
            __result = false;
            return false;
        }

        private static void SetQuery(string text)
        {
            _words = (text ?? "").ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static void EnsureBox(InventoryGui gui)
        {
            if (_box != null || _boxFailed || gui == null || gui.m_recipeListScroll == null) return;
            var panel = gui.m_recipeListScroll.transform.parent as RectTransform;
            // The game's own text box (TextInput's), read by name: its type lives in an assembly this mod does not reference.
            var source = TextInput.instance != null ? AccessTools.Field(typeof(TextInput), "m_inputField")?.GetValue(TextInput.instance) as Component : null;
            if (panel == null || panel.parent == null || source == null) return;
            if (panel.parent.Find("MLUI_FilterField") != null) { _boxFailed = true; return; } // MyLittleUI's filter is there
            try
            {
                var copy = Instantiate(source.gameObject, panel.parent);
                copy.name = "DigiRecipeSearch";
                copy.transform.SetSiblingIndex(panel.GetSiblingIndex() + 1);
                // Where the list used to end; the list is made that much shorter (PlaceList).
                var rect = (RectTransform)copy.transform;
                rect.anchorMin = panel.anchorMin;
                rect.anchorMax = panel.anchorMax;
                rect.pivot = panel.pivot;
                rect.offsetMin = panel.offsetMin;
                rect.offsetMax = new Vector2(panel.offsetMax.x, panel.offsetMin.y + BoxHeight);
                _box = copy.GetComponent<TMP_InputField>();
                _box.onValueChanged = new TMP_InputField.OnChangeEvent();
                _box.onEndEdit = new TMP_InputField.SubmitEvent();
                _box.onSubmit = new TMP_InputField.SubmitEvent();
                _box.restoreOriginalTextOnEscape = false;
                _box.characterLimit = 40;
                _box.SetTextWithoutNotify("");
                var hint = copy.transform.Find("Text Area/Placeholder")?.GetComponent<TMP_Text>();
                if (hint != null) hint.text = "Search...";
                _box.onValueChanged.AddListener(text =>
                {
                    SetQuery(text);
                    if (InventoryGui.instance != null && InventoryGui.IsVisible()) InventoryGui.instance.UpdateCraftingPanel();
                });

                // An x that clears the box.
                var clear = new GameObject("Clear", typeof(RectTransform));
                clear.transform.SetParent(copy.transform, false);
                var clearRect = (RectTransform)clear.transform;
                clearRect.anchorMin = clearRect.anchorMax = new Vector2(1f, 0.5f);
                clearRect.pivot = new Vector2(1f, 0.5f);
                clearRect.sizeDelta = new Vector2(28f, 28f);
                clearRect.anchoredPosition = new Vector2(-4f, 0f);
                var x = clear.AddComponent<TextMeshProUGUI>();
                x.font = gui.m_recipeName.font;
                x.text = "x";
                x.fontSize = 20f;
                x.alignment = TextAlignmentOptions.Center;
                x.color = new Color(0.8f, 0.8f, 0.8f, 1f);
                clear.AddComponent<Button>().onClick.AddListener(() => _box.text = "");

                _list = panel;
                _listOffsetMin = panel.offsetMin;
                copy.SetActive(ShowSearch.Value);
                PlaceList();
            }
            catch (Exception e)
            {
                _boxFailed = true;
                RemoveBox();
                BepInEx.Logging.Logger.CreateLogSource("Digi's Recipe Sort and Search").LogWarning("No search box: " + e.Message);
            }
        }

        // The list ends above the box while the box is shown.
        private static void PlaceList()
        {
            if (_list == null) return;
            _list.offsetMin = _listOffsetMin + new Vector2(0f, _box != null && ShowSearch.Value ? BoxHeight + 6f : 0f);
        }

        private static void RemoveBox()
        {
            if (_list != null) _list.offsetMin = _listOffsetMin;
            _list = null;
            if (_box != null) Destroy(_box.gameObject);
            _box = null;
        }
    }
}
