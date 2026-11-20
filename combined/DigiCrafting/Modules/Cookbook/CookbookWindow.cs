// The Cookbook window, built from the game's own UI parts so it looks like the crafting menu: the crafting panel's
// background, its fonts, a checkbox like Epic Loot's, the Craft/Upgrade tab buttons and the recipe list's scrollbar.
// Fast: the food list is read once per session and every row is built once (the first time it is shown); sorting,
// grouping, the checkbox and the search only move rows around. A food's tooltip is made when you first point at it.
// The search box keeps the foods whose name or any ingredient contains what you type ("honey": Honey itself and
// everything made with it), meads too, also while a list is folded away.
// Closed, the book costs nothing per frame: Unity re-checks every element under a registered mask each frame, also on a
// disabled canvas (over 2 ms with all rows prepared), and every tooltip has its own LateUpdate. So while it is closed its
// masks leave the clipping list and its tooltips are off; both come back at once when it opens.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Cookbook
{
    internal enum Column { Name, Health, Stamina, Eitr, Total, Duration, Healing, Station }
    internal enum GroupBy { Type, Station, None }

    internal sealed class CookbookWindow
    {
        private const float Pad = 24f, HeaderY = 112f, ListY = 146f, RowHeight = 44f, GroupHeight = 34f, ScrollWidth = 14f;
        private const float SliderTop = RowHeight - 5.5f, SliderHeight = 3f; // as far from the icons above as from the next row's
        private const float Parked = -100000f; // hidden rows wait far outside the list (not drawn, not rebuilt)

        // Column x positions and widths inside a row (the ingredients take the rest).
        private static readonly float[] ColX = { 0f, 300f, 378f, 456f, 534f, 612f, 692f, 768f };
        private static readonly float[] ColW = { 300f, 78f, 78f, 78f, 78f, 80f, 76f, 130f };
        private static readonly string[] ColTitle = { "Food", "Health", "Stamina", "Eitr", "Total", "Lasts", "Healing", "Made at" };
        private const float IngredientsX = 905f;

        private static readonly Color Gold = new Color(1f, 0.72f, 0.36f);
        private static readonly Color Body = new Color(0.92f, 0.9f, 0.86f);
        private static readonly Color Dim = new Color(0.6f, 0.58f, 0.55f);
        private static readonly Color HealthColor = new Color(0.95f, 0.45f, 0.4f);
        private static readonly Color StaminaColor = new Color(1f, 0.82f, 0.35f);
        private static readonly Color EitrColor = new Color(0.6f, 0.65f, 1f);
        private static readonly Color RowEven = new Color(1f, 1f, 1f, 0.035f);
        private static readonly Color RowOdd = new Color(0f, 0f, 0f, 0.12f);

        // One built row, reused for as long as the window exists.
        private sealed class RowView
        {
            public RectTransform Rect;
            public Image Background;
            public CanvasGroup Group;
            public TextMeshProUGUI Name;
            public readonly TextMeshProUGUI[] Cells = new TextMeshProUGUI[ColTitle.Length];
            public LazyTooltip Tooltip;
            public string Shown; // what the texts currently show (discovered + Cooking bonus), to skip unchanged updates
        }

        private sealed class GroupView
        {
            public RectTransform Rect;
            public TextMeshProUGUI Text;
            public Image Line;
        }

        private GameObject _root;
        private RectTransform _content, _view;
        private ScrollRect _scroll;
        private Toggle _undiscovered;
        private TMP_InputField _search;
        private string _query = "";
        private TextMeshProUGUI _footer;
        private readonly Button[] _groupButtons = new Button[3];
        private readonly TextMeshProUGUI[] _headers = new TextMeshProUGUI[ColTitle.Length];
        private TMP_FontAsset _font;
        private Material _fontMaterial;
        private GameObject _tooltipPrefab;
        private float _width, _height;
        private List<Food> _foods;
        private readonly Dictionary<Food, RowView> _rows = new Dictionary<Food, RowView>();
        private readonly List<GroupView> _groups = new List<GroupView>();
        private Image _foodArrow, _meadArrow;
        private RectTransform _meadBar;
        private TextMeshProUGUI _meadTitle;
        private readonly List<RectMask2D> _masks = new List<RectMask2D>();
        private readonly List<UITooltip> _tips = new List<UITooltip>();
        private CookingBonus _bonus = CookingBonus.None;
        private Canvas _sourceCanvas, _canvas;
        private CanvasScaler _scaler;
        private GraphicRaycaster _raycaster;
        private bool _prewarmed;
        private float _prewarmAt;

        // Same size on screen as the game's own menus, also after the GUI scale setting changes.
        private void MatchScale()
        {
            var source = _sourceCanvas != null ? _sourceCanvas.GetComponent<CanvasScaler>() : null;
            if (source == null || _scaler == null) return;
            _scaler.uiScaleMode = source.uiScaleMode;
            _scaler.scaleFactor = source.scaleFactor;
            _scaler.referenceResolution = source.referenceResolution;
            _scaler.screenMatchMode = source.screenMatchMode;
            _scaler.matchWidthOrHeight = source.matchWidthOrHeight;
            _scaler.referencePixelsPerUnit = source.referencePixelsPerUnit;
        }

        internal bool IsOpen => _root != null && _canvas != null && _canvas.enabled;

        // Typing in the search box: the game's keys (map, chat, the book's own key) leave it alone meanwhile.
        internal bool Searching => IsOpen && _search != null && _search.isFocused;

        internal void Open()
        {
            if (InventoryGui.instance == null) return;
            var watch = Stopwatch.StartNew();
            if (_root == null) Rebuild(); // also after returning to the main menu: the game rebuilt its UI
            if (_root == null) return;
            var player = Player.m_localPlayer;
            if (_foods == null) _foods = FoodData.Collect(player);
            else FoodData.UpdateKnown(_foods, player);
            _bonus = CookingBonus.Current(player);
            MatchScale();
            _query = "";
            _search.SetTextWithoutNotify(""); // a fresh book each time, no hidden filter from last time
            Show(true);
            var built = Refresh();
            _scroll.verticalNormalizedPosition = 1f;
            _gliding = false;
            Plugin.Log?.LogInfo($"Opened in {watch.ElapsedMilliseconds} ms: {_foods.Count} foods, {_foods.Count(f => f.Known)} discovered, {built} rows built.");
        }

        // Smooth wheel scrolling: each wheel step sets a target a few rows further, and the list glides there.
        private float _target;
        private bool _gliding;

        internal void Scroll(float wheel, float dt)
        {
            if (!IsOpen) return;
            var range = Mathf.Max(0f, _content.rect.height - _view.rect.height);
            var current = _content.anchoredPosition.y; // 0 = top, range = bottom
            if (Mathf.Abs(wheel) > 0.0001f && range > 0f)
            {
                if (!_gliding) _target = current;
                _target = Mathf.Clamp(_target - Mathf.Sign(wheel) * Plugin.ScrollRows.Value * RowHeight, 0f, range);
                _gliding = true;
            }
            if (!_gliding) return;
            var next = Mathf.Lerp(current, _target, 1f - Mathf.Exp(-dt * 16f));
            if (Mathf.Abs(next - _target) < 0.5f)
            {
                next = _target;
                _gliding = false;
            }
            _content.anchoredPosition = new Vector2(_content.anchoredPosition.x, next);
        }

        internal void Close()
        {
            if (_root != null) Show(false);
            UITooltip.HideTooltip();
        }

        private void Show(bool on)
        {
            _canvas.enabled = on;
            _raycaster.enabled = on;
            foreach (var mask in _masks)
            {
                if (on) ClipperRegistry.Register(mask);
                else ClipperRegistry.Unregister(mask);
            }
            foreach (var tip in _tips) tip.enabled = on;
        }

        private void Rebuild()
        {
            _masks.Clear();
            _tips.Clear();
            Build();
            _foods = null;
            _rows.Clear();
            _groups.Clear();
            _meadBar = null;
            _prewarmed = false;
        }

        // Builds the window and every row in the background a few seconds after you are in the world, a little each
        // frame (about 2 ms), so opening the book, sorting and the checkbox are instant later.
        internal void Prewarm()
        {
            var player = Player.m_localPlayer;
            if (player == null || IsOpen || InventoryGui.instance == null || (_root != null && _prewarmed)) return;
            if (_root == null && _prewarmAt <= 0f)
            {
                _prewarmAt = Time.time + 5f;
                return;
            }
            if (Time.time < _prewarmAt) return;
            if (_root == null)
            {
                Rebuild();
                return;
            }
            if (_foods == null)
            {
                _foods = FoodData.Collect(player);
                _bonus = CookingBonus.Current(player);
                return;
            }
            var watch = Stopwatch.StartNew();
            foreach (var food in _foods)
            {
                if (_rows.ContainsKey(food)) continue;
                var row = _rows[food] = BuildRow(food);
                UpdateRow(food, row);
                Park(row.Rect);
                if (watch.Elapsed.TotalMilliseconds > 2.0) return;
            }
            _prewarmed = true;
            _prewarmAt = 0f;
            Plugin.Log?.LogInfo($"Cookbook ready: {_rows.Count} rows prepared in the background.");
        }

        internal void Destroy()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
        }

        // --- Building the window -------------------------------------------------------------------------------

        private void Build()
        {
            var inv = InventoryGui.instance;
            _font = inv.m_recipeName.font;
            _fontMaterial = inv.m_recipeName.fontSharedMaterial;
            _tooltipPrefab = inv.m_playerGrid?.m_elementPrefab?.GetComponent<UITooltip>()?.m_tooltipPrefab;
            _width = Plugin.WindowWidth.Value;
            _height = Plugin.WindowHeight.Value;

            // Its own screen canvas, scaled like the inventory's and drawn just above it (the game's UI parts each
            // have their own canvas; their common parent is not one).
            _sourceCanvas = inv.m_crafting != null ? inv.m_crafting.GetComponentInParent<Canvas>()?.rootCanvas : null;
            var canvasObject = new GameObject("DigiCookbook", typeof(RectTransform));
            canvasObject.transform.SetParent(inv.transform.parent, false);
            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = (_sourceCanvas != null ? _sourceCanvas.sortingOrder : 0) + 5;
            _scaler = canvasObject.AddComponent<CanvasScaler>();
            MatchScale();
            _raycaster = canvasObject.AddComponent<GraphicRaycaster>();
            _canvas = canvas;
            _root = canvasObject;

            var root = Rect("Window", canvasObject.transform);
            root.anchorMin = root.anchorMax = root.pivot = new Vector2(0.5f, 0.5f);
            root.sizeDelta = new Vector2(_width, _height);
            root.anchoredPosition = Vector2.zero;
            var bg = root.gameObject.AddComponent<Image>();
            var panel = inv.m_crafting != null ? inv.m_crafting.GetComponentsInChildren<Image>(true).FirstOrDefault(i => i.name == "Bkg") : null;
            CopyLook(panel, bg, new Color(0.08f, 0.07f, 0.06f, 0.96f));
            bg.raycastTarget = true;

            var title = Text(root, "Cookbook", 34f, Gold, TextAlignmentOptions.Left);
            Place(title.rectTransform, Pad, 14f, 400f, 44f);

            var close = Text(root, "X", 28f, Body, TextAlignmentOptions.Center);
            Place(close.rectTransform, _width - Pad - 36f, 14f, 36f, 36f);
            close.raycastTarget = true;
            close.gameObject.AddComponent<Button>().onClick.AddListener(Plugin.CloseWindow);

            // Search box, in the title row left of the close button.
            const float searchWidth = 380f;
            _search = SearchBox(root, _width - Pad - 36f - 20f - searchWidth, 16f, searchWidth, 38f);
            _search.onValueChanged.AddListener(text =>
            {
                _query = (text ?? "").Trim();
                Refresh();
                _scroll.verticalNormalizedPosition = 1f;
            });

            // "Show undiscovered" checkbox, in the style of Epic Loot's enchanting table checkboxes.
            _undiscovered = Checkbox(root, "Show undiscovered", Pad, 66f);
            _undiscovered.SetIsOnWithoutNotify(Plugin.ShowUndiscovered.Value);
            _undiscovered.onValueChanged.AddListener(on =>
            {
                Plugin.ShowUndiscovered.Value = on;
                Refresh();
            });

            // "Group by" tabs (the game's Craft/Upgrade tab buttons).
            var groupLabel = Text(root, "Group by", 20f, Dim, TextAlignmentOptions.Right);
            Place(groupLabel.rectTransform, 300f, 64f, 110f, 34f);
            string[] groupNames = { "Type", "Station", "None" };
            for (var i = 0; i < 3; i++)
            {
                var index = i;
                var tab = UnityEngine.Object.Instantiate(inv.m_tabCraft.gameObject, root, false);
                tab.name = "Group" + groupNames[i];
                var button = tab.GetComponent<Button>();
                button.onClick = new Button.ButtonClickedEvent();
                button.onClick.AddListener(() =>
                {
                    Plugin.Grouping.Value = (GroupBy)index;
                    Refresh();
                });
                foreach (var label in tab.GetComponentsInChildren<TMP_Text>(true)) label.text = groupNames[i];
                Place((RectTransform)tab.transform, 420f + i * 132f, 62f, 124f, 38f);
                _groupButtons[i] = button;
            }

            // Column headers: click to sort, click again to reverse.
            var header = Rect("Header", root);
            Place(header, Pad, HeaderY, _width - 2 * Pad - ScrollWidth - 6f, 30f);
            for (var i = 0; i < ColTitle.Length; i++)
            {
                var column = (Column)i;
                var cell = Rect("Col" + ColTitle[i], header);
                Place(cell, ColX[i], 0f, ColW[i], 30f);
                var hit = cell.gameObject.AddComponent<Image>();
                hit.color = new Color(0f, 0f, 0f, 0f);
                cell.gameObject.AddComponent<Button>().onClick.AddListener(() => SortBy(column));
                var text = Text(cell, ColTitle[i], 19f, Gold, i == 0 ? TextAlignmentOptions.Left : TextAlignmentOptions.Center);
                Stretch(text.rectTransform, i == 0 ? 34f : 0f);
                _headers[i] = text;
            }
            // The arrow next to "Food": folds the food away, so the meads come to the top.
            var fold = Rect("FoodArrow", header);
            Place(fold, 4f, 0f, 30f, 30f);
            fold.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
            fold.gameObject.AddComponent<Button>().onClick.AddListener(ToggleFood);
            _foodArrow = Arrow(fold, 8f, 8f);
            Tooltip(fold.gameObject, "Food", "Click to fold the food list away, or to open it again.");
            var ingredientsTitle = Text(header, "Ingredients", 19f, Dim, TextAlignmentOptions.Left);
            Place(ingredientsTitle.rectTransform, IngredientsX, 0f, 200f, 30f);

            // The list.
            _view = Rect("List", root);
            var listHeight = _height - ListY - 48f;
            Place(_view, Pad, ListY, _width - 2 * Pad - ScrollWidth - 6f, listHeight);
            var viewBg = _view.gameObject.AddComponent<Image>();
            viewBg.color = new Color(0f, 0f, 0f, 0.28f);
            AddMask(_view.gameObject);
            _content = Rect("Content", _view);
            _content.anchorMin = new Vector2(0f, 1f);
            _content.anchorMax = new Vector2(1f, 1f);
            _content.pivot = new Vector2(0.5f, 1f);
            _content.anchoredPosition = Vector2.zero;
            _content.sizeDelta = new Vector2(0f, 0f);
            _scroll = _view.gameObject.AddComponent<ScrollRect>();
            _scroll.content = _content;
            _scroll.viewport = _view;
            _scroll.horizontal = false;
            _scroll.vertical = true;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.scrollSensitivity = 0f; // the mouse wheel is handled in Scroll(): Valheim's wheel steps are tiny for a ScrollRect
            _scroll.inertia = false;
            if (inv.m_recipeListScroll != null)
            {
                var bar = UnityEngine.Object.Instantiate(inv.m_recipeListScroll.gameObject, root, false).GetComponent<Scrollbar>();
                bar.name = "Scrollbar";
                bar.onValueChanged = new Scrollbar.ScrollEvent();
                bar.direction = Scrollbar.Direction.BottomToTop;
                Place((RectTransform)bar.transform, _width - Pad - ScrollWidth, ListY, ScrollWidth, listHeight);
                _scroll.verticalScrollbar = bar;
                _scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
            }

            _footer = Text(root, "", 18f, Dim, TextAlignmentOptions.Left);
            Place(_footer.rectTransform, Pad, _height - 40f, _width - 2 * Pad, 28f);
            Show(false);
        }

        // --- Sorting, grouping, placing rows --------------------------------------------------------------------

        private void SortBy(Column column)
        {
            if (Plugin.SortColumn.Value == column) Plugin.SortDescending.Value = !Plugin.SortDescending.Value;
            else
            {
                Plugin.SortColumn.Value = column;
                Plugin.SortDescending.Value = column != Column.Name && column != Column.Station; // numbers: highest first
            }
            Refresh();
        }

        // Places the rows for the current view; builds only rows never shown before. Returns how many it built.
        private int Refresh()
        {
            if (_root == null || _foods == null) return 0;
            var watch = Stopwatch.StartNew();
            var showAll = Plugin.ShowUndiscovered.Value;
            var shown = _foods.Where(f => (f.Known || showAll) && Matches(f, _query)).ToList();
            shown.Sort(Compare);
            var foods = shown.Where(f => !f.IsMead).ToList();
            var meads = shown.Where(f => f.IsMead).ToList();
            var foodOpen = _query.Length > 0 || Plugin.ShowFood.Value; // a search looks through everything
            var meadsOpen = _query.Length > 0 || Plugin.ShowMeads.Value;
            SetArrow(_foodArrow, foodOpen);

            var grouping = Plugin.Grouping.Value;
            for (var i = 0; i < _groupButtons.Length; i++) _groupButtons[i].interactable = (int)grouping != i; // the selected tab looks pressed
            for (var i = 0; i < _headers.Length; i++)
            {
                var active = (int)Plugin.SortColumn.Value == i;
                _headers[i].text = ColTitle[i] + (active ? (Plugin.SortDescending.Value ? " <size=70%>v</size>" : " <size=70%>^</size>") : "");
                _headers[i].color = active ? Color.white : Gold;
            }

            var built = 0;
            var visible = new HashSet<Food>();
            var y = 0f;
            var groupIndex = 0;
            if (foodOpen && grouping == GroupBy.None)
                y = PlaceRows(foods, y, visible, ref built);
            else if (foodOpen)
            {
                var groups = grouping == GroupBy.Type
                    ? foods.GroupBy(f => (int)f.Type).OrderBy(g => g.Key).Select(g => new KeyValuePair<string, List<Food>>(FoodData.TypeName((FoodType)g.Key), g.ToList()))
                    : foods.GroupBy(f => f.StationGroup).OrderBy(g => g.Key == "Gathered" ? "zzz" : g.Key).Select(g => new KeyValuePair<string, List<Food>>(g.Key, g.ToList()));
                foreach (var group in groups)
                {
                    var color = grouping == GroupBy.Type ? FoodData.TypeColor(group.Value[0].Type) : Gold;
                    y = PlaceGroupHeader(groupIndex++, group.Key, group.Value.Count, color, y);
                    y = PlaceRows(group.Value, y, visible, ref built);
                }
            }
            for (var i = groupIndex; i < _groups.Count; i++) Park(_groups[i].Rect);
            if (meads.Count > 0)
            {
                y = PlaceMeadBar(meads.Count, meadsOpen, y);
                if (meadsOpen) y = PlaceRows(meads, y, visible, ref built);
            }
            else if (_meadBar != null) Park(_meadBar);
            foreach (var pair in _rows)
                if (!visible.Contains(pair.Key)) Park(pair.Value.Rect);
            _content.sizeDelta = new Vector2(0f, y + 6f);

            var mead = _foods.Count(f => f.IsMead);
            _footer.text = (_query.Length > 0 ? $"<color=#ffb85c>{shown.Count} {(shown.Count == 1 ? "result" : "results")} with \"{_query}\".</color>   " : "") +
                           $"Discovered {_foods.Count(f => f.Known && !f.IsMead)} of {_foods.Count - mead} foods, {_foods.Count(f => f.Known && f.IsMead)} of {mead} meads.   Click a column to sort; hover a food for its details." +
                           (_bonus.Active ? $"   <color=#8fd17a>Green: your Cooking skill bonus (Cooking {_bonus.Skill:0}).</color>" : "");
            if (watch.ElapsedMilliseconds > 5) Plugin.Log?.LogInfo($"Refresh: {watch.ElapsedMilliseconds} ms, {built} rows built.");
            return built;
        }

        private void ToggleFood()
        {
            if (_query.Length > 0) return; // a search shows everything anyway
            Plugin.ShowFood.Value = !Plugin.ShowFood.Value;
            Refresh();
            _scroll.verticalNormalizedPosition = 1f;
            _gliding = false;
        }

        private void ToggleMeads()
        {
            if (_query.Length > 0) return;
            Plugin.ShowMeads.Value = !Plugin.ShowMeads.Value;
            Refresh();
        }

        // In its name or in any of its ingredients (also what goes into the prepared part, e.g. the unbaked pie).
        private static bool Matches(Food food, string query)
        {
            if (query.Length == 0 || Contains(food.Name, query)) return true;
            foreach (var ingredient in food.Ingredients)
                if (Contains(ingredient.Name, query)) return true;
            return false;
        }

        private static bool Contains(string text, string part) => text != null && text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

        private float PlaceRows(List<Food> foods, float y, HashSet<Food> visible, ref int built)
        {
            var index = 0;
            foreach (var food in foods)
            {
                if (!_rows.TryGetValue(food, out var row))
                {
                    _rows[food] = row = BuildRow(food);
                    built++;
                }
                UpdateRow(food, row);
                row.Background.color = index++ % 2 == 0 ? RowEven : RowOdd;
                row.Rect.anchoredPosition = new Vector2(0f, -y);
                visible.Add(food);
                y += RowHeight;
            }
            return y;
        }

        // The bar above the meads, below the food: click it to fold the meads away. Its titles replace the food's columns.
        private float PlaceMeadBar(int count, bool open, float y)
        {
            if (_meadBar == null)
            {
                _meadBar = Rect("Meads", _content);
                Place(_meadBar, 0f, -Parked, _view.rect.width, GroupHeight);
                _meadBar.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
                _meadBar.gameObject.AddComponent<Button>().onClick.AddListener(ToggleMeads);
                _meadArrow = Arrow(_meadBar, 12f, (GroupHeight - 14f) / 2f);
                _meadTitle = Text(_meadBar, "", 22f, Gold, TextAlignmentOptions.Left);
                Place(_meadTitle.rectTransform, 34f, 0f, 260f, GroupHeight);
                var effect = Text(_meadBar, "Effect", 19f, Gold, TextAlignmentOptions.Left);
                Place(effect.rectTransform, ColX[1] + 6f, 0f, 200f, GroupHeight);
                var lasts = Text(_meadBar, ColTitle[(int)Column.Duration], 19f, Gold, TextAlignmentOptions.Center);
                Place(lasts.rectTransform, ColX[(int)Column.Duration], 0f, ColW[(int)Column.Duration], GroupHeight);
                var line = Rect("Line", _meadBar).gameObject.AddComponent<Image>();
                line.raycastTarget = false;
                line.color = new Color(Gold.r, Gold.g, Gold.b, 0.45f);
                Place(line.rectTransform, 8f, GroupHeight - 4f, _view.rect.width - 16f, 2f);
            }
            _meadTitle.text = $"Meads  <size=75%><color=#9a948a>({count})</color></size>";
            SetArrow(_meadArrow, open);
            _meadBar.anchoredPosition = new Vector2(0f, -(y + 4f));
            return y + GroupHeight + 6f;
        }

        private float PlaceGroupHeader(int index, string name, int count, Color color, float y)
        {
            if (index >= _groups.Count) _groups.Add(BuildGroupHeader());
            var view = _groups[index];
            view.Text.text = $"{name}  <size=75%><color=#9a948a>({count})</color></size>";
            view.Text.color = color;
            view.Line.color = new Color(color.r, color.g, color.b, 0.45f);
            view.Rect.anchoredPosition = new Vector2(0f, -(y + 4f));
            return y + GroupHeight + 6f;
        }

        private static void Park(RectTransform rect)
        {
            if (rect.anchoredPosition.y != Parked) rect.anchoredPosition = new Vector2(0f, Parked);
        }

        private int Compare(Food a, Food b)
        {
            int result;
            switch (Plugin.SortColumn.Value)
            {
                case Column.Health: result = a.Health.CompareTo(b.Health); break;
                case Column.Stamina: result = a.Stamina.CompareTo(b.Stamina); break;
                case Column.Eitr: result = a.Eitr.CompareTo(b.Eitr); break;
                case Column.Total: result = a.Total.CompareTo(b.Total); break;
                case Column.Duration: result = a.Duration.CompareTo(b.Duration); break;
                case Column.Healing: result = a.Healing.CompareTo(b.Healing); break;
                case Column.Station: result = string.Compare(a.Station, b.Station, StringComparison.OrdinalIgnoreCase); break;
                default: result = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); break;
            }
            if (Plugin.SortDescending.Value) result = -result;
            return result != 0 ? result : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        }

        // --- Rows -------------------------------------------------------------------------------------------------

        private GroupView BuildGroupHeader()
        {
            var rect = Rect("Group", _content);
            Place(rect, 0f, -Parked, _view.rect.width, GroupHeight);
            var text = Text(rect, "", 22f, Gold, TextAlignmentOptions.Left);
            Place(text.rectTransform, 10f, 0f, 500f, GroupHeight - 4f);
            var line = Rect("Line", rect).gameObject.AddComponent<Image>();
            line.raycastTarget = false;
            Place(line.rectTransform, 8f, GroupHeight - 4f, _view.rect.width - 16f, 2f);
            return new GroupView { Rect = rect, Text = text, Line = line };
        }

        // Everything that never changes: icon, ingredients, station. Numbers and name are set in UpdateRow.
        private RowView BuildRow(Food food)
        {
            var view = new RowView();
            view.Rect = Rect("Row", _content);
            Place(view.Rect, 0f, -Parked, _view.rect.width, RowHeight);
            view.Background = view.Rect.gameObject.AddComponent<Image>();
            view.Background.raycastTarget = false;
            view.Group = view.Rect.gameObject.AddComponent<CanvasGroup>();

            var icon = Icon(view.Rect, food.Icon, 6f, 4f, 36f);
            if (_tooltipPrefab != null)
            {
                view.Tooltip = icon.gameObject.AddComponent<LazyTooltip>(); // before UITooltip, so it fills the text first
                view.Tooltip.Food = food;
                view.Tooltip.Tooltip = AddTip(icon.gameObject);
                view.Tooltip.Tooltip.m_tooltipPrefab = _tooltipPrefab;
            }
            view.Name = Text(view.Rect, "", 20f, Body, TextAlignmentOptions.Left);
            Place(view.Name.rectTransform, 50f, 0f, ColW[0] - 54f, RowHeight);
            for (var i = 1; i < ColTitle.Length; i++)
            {
                if (food.IsMead && i != (int)Column.Duration && i != (int)Column.Station) continue;
                view.Cells[i] = Text(view.Rect, "", i == (int)Column.Station ? 17f : 20f, Body, TextAlignmentOptions.Center);
                Place(view.Cells[i].rectTransform, ColX[i], 0f, ColW[i], RowHeight);
            }
            if (food.IsMead)
            {
                // over the columns a mead has no value for; the whole text is in its tooltip
                var effect = Text(view.Rect, food.Effect, 16f, Body, TextAlignmentOptions.Left);
                Place(effect.rectTransform, ColX[1] + 6f, 0f, ColX[(int)Column.Duration] - ColX[1] - 8f, RowHeight);
                effect.raycastTarget = true;
                Tooltip(effect.gameObject, food.Name, food.EffectTip);
            }
            view.Cells[(int)Column.Station].text = food.Station + (food.Yield > 1 ? $" <color=#9a948a>x{food.Yield}</color>" : "");

            // The ingredients, on a strip of their own: it slides when they do not fit (AddSlider).
            var room = _view.rect.width - 6f - IngredientsX;
            var window = Rect("Ingredients", view.Rect);
            Place(window, IngredientsX, 0f, room, RowHeight);
            var strip = Rect("Strip", window);
            Place(strip, 0f, 0f, room, RowHeight);
            var x = 0f;
            Ingredient previous = null;
            foreach (var ingredient in food.Ingredients)
            {
                if (ingredient.Apart && previous != null && !previous.Before)
                {
                    Divider(strip, x, ingredient.Nested);
                    x += 8f;
                }
                var image = Icon(strip, ingredient.Icon, x, 6f, 32f);
                Tooltip(image.gameObject, ingredient.Name, $"{ingredient.Amount}x {ingredient.Name}" + (ingredient.Note != null ? "\n" + ingredient.Note : ""));
                if (ingredient.Amount > 1)
                {
                    var amount = Text(strip, ingredient.Amount.ToString(), 15f, Color.white, TextAlignmentOptions.BottomRight);
                    amount.outlineWidth = 0.25f;
                    amount.outlineColor = Color.black;
                    Place(amount.rectTransform, x, 6f, 36f, 36f);
                }
                x += 40f;
                if (ingredient.Before)
                {
                    // "made from": a small arrow between the prepared item and what it is made of
                    var arrow = Text(strip, "=", 20f, Dim, TextAlignmentOptions.Center);
                    Place(arrow.rectTransform, x - 4f, 0f, 16f, RowHeight);
                    x += 12f;
                }
                previous = ingredient;
            }
            if (x - 4f > room) AddSlider(window, strip, room, x - 4f); // the last icon with its amount is 36 wide
            if (food.Ingredients.Count == 0 && food.StationGroup == "Gathered")
            {
                var none = Text(view.Rect, "found in the world", 16f, Dim, TextAlignmentOptions.Left);
                Place(none.rectTransform, IngredientsX, 0f, 220f, RowHeight);
            }
            return view;
        }

        // Between made ingredients: a full line between the recipe's own ingredients, a short one inside one of them.
        private static void Divider(RectTransform strip, float x, bool nested)
        {
            var line = Rect("Divider", strip);
            Place(line, x - 1f, nested ? 14f : 6f, 2f, nested ? 16f : 32f);
            var image = line.gameObject.AddComponent<Image>();
            image.color = Dim;
            image.raycastTarget = false;
        }

        // Ingredients that do not fit: the strip slides behind the column, moved by a thin slider below it or by dragging
        // anywhere in the column. The icons move up a little in such a row to make room for the slider.
        private void AddSlider(RectTransform window, RectTransform strip, float room, float needed)
        {
            const float up = 4f;
            window.sizeDelta = new Vector2(room, SliderTop);
            AddMask(window.gameObject);
            window.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0f); // catches the mouse between the icons
            strip.sizeDelta = new Vector2(needed, RowHeight);
            strip.anchoredPosition = new Vector2(0f, up);

            var track = Rect("Slider", window.parent);
            Place(track, IngredientsX, SliderTop, room, SliderHeight);
            Rounded(track.gameObject.AddComponent<Image>(), new Color(0f, 0f, 0f, 0.11f));
            var area = Rect("Area", track);
            Stretch(area, 0f);
            var handle = Rect("Handle", area);
            Stretch(handle, 0f);
            var grip = handle.gameObject.AddComponent<Image>();
            Rounded(grip, new Color(Gold.r, Gold.g, Gold.b, 0.25f));
            var bar = track.gameObject.AddComponent<Scrollbar>();
            bar.navigation = new Navigation { mode = Navigation.Mode.None };
            bar.direction = Scrollbar.Direction.LeftToRight;
            bar.handleRect = handle;
            bar.targetGraphic = grip;
            bar.size = room / needed;
            bar.value = 0f;
            bar.onValueChanged.AddListener(value => strip.anchoredPosition = new Vector2(-value * (needed - room), up));
            var drag = window.gameObject.AddComponent<DragSlider>();
            drag.Bar = bar;
            drag.Hidden = needed - room;
        }

        // Name and numbers: only rewritten when "discovered" or the Cooking bonus changed since the last time.
        private void UpdateRow(Food food, RowView view)
        {
            var b = _bonus;
            var state = $"{food.Known}|{b.Health}|{b.Stamina}|{b.Eitr}|{b.Healing}|{b.Duration}";
            if (view.Shown == state) return;
            view.Shown = state;
            if (view.Tooltip != null) view.Tooltip.Reset(); // the tooltip shows the Cooking bonus too

            view.Group.alpha = food.Known ? 1f : 0.45f;
            view.Name.text = food.Known ? food.Name : food.Name + " <size=75%><color=#9a948a>(not found yet)</color></size>";
            view.Name.color = food.Known ? Body : Dim;
            if (food.IsMead)
            {
                view.Cells[(int)Column.Duration].text = Minutes(food.Duration);
                return;
            }
            Number(view.Cells[(int)Column.Health], food.Health, b.Health, HealthColor, "0");
            Number(view.Cells[(int)Column.Stamina], food.Stamina, b.Stamina, StaminaColor, "0");
            Number(view.Cells[(int)Column.Eitr], food.Eitr, b.Eitr, EitrColor, "0");
            var total = food.Health * b.Health + food.Stamina * b.Stamina + food.Eitr * b.Eitr;
            Number(view.Cells[(int)Column.Total], food.Total, total / Mathf.Max(food.Total, 0.0001f), Body, "0");
            view.Cells[(int)Column.Duration].text = Minutes(food.Duration) +
                Extra(Mathf.Round(food.Duration * b.Duration / 60f) - Mathf.Round(food.Duration / 60f), "0");
            Number(view.Cells[(int)Column.Healing], food.Healing, b.Healing, Body, "0.#");
        }

        // "40 +10": the food's own value, plus what your Cooking skill adds (Cooking Mastery).
        private static void Number(TextMeshProUGUI cell, float value, float factor, Color color, string format)
        {
            if (value <= 0f)
            {
                cell.text = "-";
                cell.color = Dim;
                return;
            }
            cell.text = value.ToString(format) + Extra(value * factor - value, format);
            cell.color = color;
        }

        private static string Extra(float extra, string format) =>
            extra >= 0.5f || (format == "0.#" && extra >= 0.05f)
                ? $" <size=70%><color=#8fd17a>+{extra.ToString(format)}</color></size>"
                : "";

        private static string Minutes(float seconds)
        {
            if (seconds <= 0f) return "-";
            return seconds >= 60f ? $"{Mathf.RoundToInt(seconds / 60f)}m" : $"{Mathf.RoundToInt(seconds)}s";
        }

        // --- Checkbox -----------------------------------------------------------------------------------------------

        // The same look as Epic Loot's checkboxes (enchanting table), without needing Epic Loot: the game's inventory
        // slot background as the box and a green tick drawn here.
        private Toggle Checkbox(RectTransform parent, string label, float x, float y)
        {
            var box = Rect("Checkbox", parent);
            Place(box, x, y, 280f, 30f);
            var frame = Rect("Background", box);
            Place(frame, 0f, 3f, 24f, 24f);
            var frameImage = frame.gameObject.AddComponent<Image>();
            var slot = SlotSprite();
            if (slot != null)
            {
                frameImage.sprite = slot;
                frameImage.type = Image.Type.Sliced;
            }
            frameImage.color = new Color(0f, 0f, 0f, 0.5f);
            var mark = Rect("Checkmark", frame);
            Place(mark, -4f, -4f, 32f, 32f); // a bit bigger than the box, like Epic Loot's
            var markImage = mark.gameObject.AddComponent<Image>();
            markImage.sprite = TickSprite();
            markImage.color = new Color(0.044f, 1f, 0f, 1f);
            markImage.raycastTarget = false;
            var caption = Text(box, label, 20f, Color.white, TextAlignmentOptions.Left);
            Place(caption.rectTransform, 34f, 0f, 246f, 30f);
            var hit = box.gameObject.AddComponent<Image>();
            hit.color = new Color(0f, 0f, 0f, 0f); // the label is clickable too
            var toggle = box.gameObject.AddComponent<Toggle>();
            toggle.targetGraphic = frameImage;
            toggle.graphic = markImage;
            return toggle;
        }

        // --- Search box -------------------------------------------------------------------------------------------

        // A text box on the same dark slot background as the checkbox, with a hint while empty and an x that clears it.
        private TMP_InputField SearchBox(RectTransform parent, float x, float y, float width, float height)
        {
            var box = Rect("Search", parent);
            Place(box, x, y, width, height);
            box.gameObject.SetActive(false); // the input field is set up before it first wakes up
            var background = box.gameObject.AddComponent<Image>();
            var slot = SlotSprite();
            if (slot != null)
            {
                background.sprite = slot;
                background.type = Image.Type.Sliced;
            }
            background.color = new Color(0f, 0f, 0f, 0.6f);
            var area = Rect("Text Area", box);
            area.anchorMin = Vector2.zero;
            area.anchorMax = Vector2.one;
            area.offsetMin = new Vector2(12f, 2f);
            area.offsetMax = new Vector2(-34f, -2f);
            AddMask(area.gameObject);
            var hint = Text(area, "Search food or ingredient...", 19f, Dim, TextAlignmentOptions.Left);
            Stretch(hint.rectTransform, 0f);
            hint.fontStyle = FontStyles.Italic;
            var text = Text(area, "", 19f, Body, TextAlignmentOptions.Left);
            Stretch(text.rectTransform, 0f);
            text.richText = false;
            var field = box.gameObject.AddComponent<TMP_InputField>();
            field.textViewport = area;
            field.textComponent = text;
            field.placeholder = hint;
            field.targetGraphic = background;
            if (_font != null) field.fontAsset = _font;
            field.pointSize = 19f;
            field.characterLimit = 40;
            field.richText = false;
            field.customCaretColor = true;
            field.caretColor = Body;
            field.caretWidth = 2;
            field.selectionColor = new Color(1f, 0.72f, 0.36f, 0.35f);
            box.gameObject.SetActive(true);

            var clear = Text(box, "x", 22f, Dim, TextAlignmentOptions.Center);
            Place(clear.rectTransform, width - 32f, 0f, 30f, height);
            clear.raycastTarget = true;
            clear.gameObject.AddComponent<Button>().onClick.AddListener(() => field.text = "");
            return field;
        }

        // The game's inventory slot background ("item_background"), from the inventory grid's slot template.
        private static Sprite SlotSprite()
        {
            var element = InventoryGui.instance?.m_playerGrid?.m_elementPrefab;
            if (element == null) return null;
            foreach (var image in element.GetComponentsInChildren<Image>(true))
                if (image.sprite != null && image.sprite.name == "item_background") return image.sprite;
            return null;
        }

        private static Sprite _tick;

        // A tick mark, drawn once: two thick, smooth lines on a 32x32 texture (white, tinted by the image color).
        private static Sprite TickSprite()
        {
            if (_tick != null) return _tick;
            const int size = 32;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "DigiTick", filterMode = FilterMode.Bilinear };
            var a = new Vector2(6f, 16f);
            var b = new Vector2(13f, 8.5f);
            var c = new Vector2(26.5f, 25f);
            const float half = 2.1f;
            var pixels = new Color32[size * size];
            for (var py = 0; py < size; py++)
                for (var px = 0; px < size; px++)
                {
                    var p = new Vector2(px + 0.5f, py + 0.5f);
                    var d = Mathf.Min(SegmentDistance(p, a, b), SegmentDistance(p, b, c));
                    var alpha = Mathf.Clamp01(half + 0.5f - d);
                    pixels[py * size + px] = new Color32(255, 255, 255, (byte)(alpha * 255f));
                }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            _tick = Sprite.Create(texture, new UnityEngine.Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
            _tick.name = "DigiTick";
            return _tick;
        }

        private static Sprite _pill;

        // Round ends at the slider's height: a sprite with 8 pixel corners, drawn so that they are half as high as the slider.
        private static void Rounded(Image image, Color color)
        {
            if (_pill == null)
            {
                const int size = 18;
                var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "DigiPill", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                var pixels = new Color32[size * size];
                for (var py = 0; py < size; py++)
                    for (var px = 0; px < size; px++)
                    {
                        var dx = Mathf.Max(0f, Mathf.Abs(px + 0.5f - size / 2f) - 1f);
                        var dy = Mathf.Max(0f, Mathf.Abs(py + 0.5f - size / 2f) - 1f);
                        var alpha = Mathf.Clamp01(8f - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
                        pixels[py * size + px] = new Color32(255, 255, 255, (byte)(alpha * 255f));
                    }
                texture.SetPixels32(pixels);
                texture.Apply(false, true);
                _pill = Sprite.Create(texture, new UnityEngine.Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(8f, 8f, 8f, 8f));
                _pill.name = "DigiPill";
            }
            image.sprite = _pill;
            image.type = Image.Type.Sliced;
            image.pixelsPerUnitMultiplier = 8f / (SliderHeight / 2f);
            image.color = color;
        }

        private static Sprite _triangle;

        // Points down, drawn once on a 28x28 texture (white, tinted by the image color).
        private static Sprite TriangleSprite()
        {
            if (_triangle != null) return _triangle;
            const int size = 28;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "DigiTriangle", filterMode = FilterMode.Bilinear };
            var pixels = new Color32[size * size];
            for (var py = 0; py < size; py++)
                for (var px = 0; px < size; px++)
                {
                    var u = (px + 0.5f) / size;
                    var v = (py + 0.5f) / size; // from the bottom
                    var half = 0.45f * (v - 0.1f) / 0.75f;
                    var alpha = Mathf.Clamp01(Mathf.Min(half - Mathf.Abs(u - 0.5f), 0.85f - v) * size + 0.5f);
                    pixels[py * size + px] = new Color32(255, 255, 255, (byte)(alpha * 255f));
                }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            _triangle = Sprite.Create(texture, new UnityEngine.Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
            _triangle.name = "DigiTriangle";
            return _triangle;
        }

        // Down when the list it belongs to is open, right when it is folded away.
        private static Image Arrow(RectTransform parent, float x, float y)
        {
            const float size = 14f;
            var rect = Rect("Arrow", parent);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(x + size / 2f, -(y + size / 2f));
            rect.sizeDelta = new Vector2(size, size);
            var image = rect.gameObject.AddComponent<Image>();
            image.sprite = TriangleSprite();
            image.color = Gold;
            image.raycastTarget = false;
            return image;
        }

        private static void SetArrow(Image arrow, bool open) =>
            arrow.rectTransform.localRotation = Quaternion.Euler(0f, 0f, open ? 0f : 90f);

        private static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            var t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return Vector2.Distance(p, a + ab * t);
        }

        // --- Small UI helpers ------------------------------------------------------------------------------------

        private void Tooltip(GameObject target, string topic, string text)
        {
            if (_tooltipPrefab == null || target == null) return;
            var tooltip = AddTip(target);
            tooltip.m_tooltipPrefab = _tooltipPrefab;
            tooltip.Set(topic, text);
        }

        // Rows are also prepared while the book is closed: then what they add starts switched off (see Show).
        private UITooltip AddTip(GameObject target)
        {
            var tip = target.AddComponent<UITooltip>();
            tip.enabled = IsOpen;
            _tips.Add(tip);
            return tip;
        }

        private void AddMask(GameObject target)
        {
            var mask = target.AddComponent<RectMask2D>();
            _masks.Add(mask);
            if (!IsOpen) ClipperRegistry.Unregister(mask);
        }

        private static RectTransform Rect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        // Position from the parent's top-left corner, y going down.
        private static void Place(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
        }

        private static void Stretch(RectTransform rect, float left)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(left, 0f);
            rect.offsetMax = Vector2.zero;
        }

        private TextMeshProUGUI Text(Transform parent, string text, float size, Color color, TextAlignmentOptions align)
        {
            var rect = Rect("Text", parent);
            var t = rect.gameObject.AddComponent<TextMeshProUGUI>();
            if (_font != null) t.font = _font;
            if (_fontMaterial != null) t.fontSharedMaterial = _fontMaterial;
            t.fontSize = size;
            t.color = color;
            t.alignment = align;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Ellipsis;
            t.raycastTarget = false;
            t.richText = true;
            t.text = text;
            return t;
        }

        private static Image Icon(RectTransform parent, Sprite sprite, float x, float y, float size)
        {
            var rect = Rect("Icon", parent);
            Place(rect, x, y, size, size);
            var image = rect.gameObject.AddComponent<Image>();
            image.sprite = sprite;
            image.preserveAspect = true;
            image.color = sprite != null ? Color.white : new Color(0f, 0f, 0f, 0f);
            image.raycastTarget = true; // for the tooltip
            return image;
        }

        private static void CopyLook(Image from, Image to, Color fallback)
        {
            if (from == null || from.sprite == null)
            {
                to.color = fallback;
                return;
            }
            to.sprite = from.sprite;
            to.type = from.type;
            to.material = from.material;
            to.color = from.color;
            to.pixelsPerUnitMultiplier = from.pixelsPerUnitMultiplier;
        }
    }

    // Dragging anywhere in a row's ingredients pulls them along with the mouse, and the slider follows. The icons are
    // children of this object, so a drag that starts on one comes here too, and their tooltips keep working.
    internal sealed class DragSlider : MonoBehaviour, IBeginDragHandler, IDragHandler
    {
        public Scrollbar Bar;
        public float Hidden; // how much of the ingredients does not fit, in UI units

        public void OnBeginDrag(PointerEventData eventData) { }

        public void OnDrag(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left || Bar == null || Hidden <= 0f) return;
            Bar.value = Mathf.Clamp01(Bar.value - eventData.delta.x / transform.lossyScale.x / Hidden);
        }
    }

    // Fills a food icon's tooltip the first time the mouse points at it (making every tooltip up front is slow:
    // other mods add to item tooltips too). Runs before the game's UITooltip on the same object.
    internal sealed class LazyTooltip : MonoBehaviour, IPointerEnterHandler
    {
        public Food Food;
        public UITooltip Tooltip;
        private bool _filled;

        public void Reset() => _filled = false;

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (_filled || Food?.Item == null || Tooltip == null) return;
            _filled = true;
            Tooltip.Set(Food.Name, ItemDrop.ItemData.GetTooltip(Food.Item.m_itemData, 1, false, Game.m_worldLevel));
        }
    }
}
