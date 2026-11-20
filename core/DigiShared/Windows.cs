// Shared by Ultimate Builder and Building Tweaks (each mod compiles this file in; each has its own copy of the state):
// windows (Build Materials, the schematic library) in the look of the Cookbook: the crafting panel's background, the
// game's fonts, the recipe list's scrollbar, the Craft tab's buttons and the Cookbook's search box. One window at a
// time; it is built when it opens and thrown away when it closes, so a closed window costs nothing.
// While one is open the mouse is free, the character ignores movement and attack input, the mouse wheel scrolls the
// list (smoothly), typing in a search box goes to the box only, and Escape leaves the search box first and then
// closes the window, like the Cookbook. The host mod adds this component and calls Patch with its Harmony.
using System;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace DigiShared
{
    internal class Windows : MonoBehaviour
    {
        internal const float Pad = 24f, ScrollWidth = 14f;
        internal static readonly Color Gold = new Color(1f, 0.72f, 0.36f);
        internal static readonly Color Body = new Color(0.92f, 0.9f, 0.86f);
        internal static readonly Color Dim = new Color(0.6f, 0.58f, 0.55f);
        internal static readonly Color RowEven = new Color(1f, 1f, 1f, 0.035f);
        internal static readonly Color RowOdd = new Color(0f, 0f, 0f, 0.12f);

        // The open window: its parts for the caller to fill.
        internal sealed class Window
        {
            public GameObject Root;
            public RectTransform Panel, View, Content;
            public TMP_InputField Search;
            public float Width, Height, RowWidth, Step;
            public float Target;
            public bool Gliding;
            public bool OverMap; // opened on the large map: stays open with it (and closes with it)
        }

        internal static Window Current;
        private static TMP_FontAsset _font;
        private static Material _fontMaterial;
        private static bool _readingWheel;

        internal static bool IsOpen => Current != null && Current.Root != null;

        // Typing in the open window's search box: the game's keys leave it alone meanwhile.
        internal static bool Searching => IsOpen && Current.Search != null && Current.Search.isFocused;

        internal static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(PlayerController), "TakeInput"), postfix: new HarmonyMethod(typeof(Windows), nameof(NoInput)));
            harmony.Patch(AccessTools.Method(typeof(GameCamera), nameof(GameCamera.UpdateMouseCapture)), postfix: new HarmonyMethod(typeof(Windows), nameof(FreeMouse)));
            harmony.Patch(AccessTools.Method(typeof(ZInput), "Internal_GetMouseScrollWheel"), postfix: new HarmonyMethod(typeof(Windows), nameof(OwnWheel)));
            harmony.Patch(AccessTools.Method(typeof(Menu), "Update"), prefix: new HarmonyMethod(typeof(Windows), nameof(EscapeCloses)));
            harmony.Patch(AccessTools.Method(typeof(TextInput), nameof(TextInput.IsVisible)), postfix: new HarmonyMethod(typeof(Windows), nameof(SearchTyping)));
            // While typing in a box, no key reaches the game or other mods (a C typed into a search box used a quick slot):
            // every key check, the game's and Unity's own, answers "not pressed". The box itself gets its letters as text
            // events, which these are not.
            var noKeys = new HarmonyMethod(typeof(Windows), nameof(NoKeys));
            var names = new[] { "GetKey", "GetKeyDown", "GetKeyUp", "GetButton", "GetButtonDown", "GetButtonUp" };
            foreach (var method in typeof(ZInput).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                if (method.ReturnType == typeof(bool) && Array.IndexOf(names, method.Name) >= 0) harmony.Patch(method, prefix: noKeys);
            var unityInput = AccessTools.TypeByName("UnityEngine.Input"); // by name: not every mod references its assembly
            if (unityInput != null)
                foreach (var method in unityInput.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                    if (method.ReturnType == typeof(bool) && method.Name.StartsWith("GetKey", StringComparison.Ordinal) && Array.IndexOf(names, method.Name) >= 0)
                        harmony.Patch(method, prefix: noKeys);
        }

        // Typing in a window's box, or just done (Enter ends it in the same frame other code reads the key).
        private static bool Typing => Searching || Time.frameCount - _typingFrame <= 1;

        private static bool NoKeys(ref bool __result)
        {
            if (!Typing) return true;
            __result = false;
            return false;
        }

        private void OnDestroy() => Close();

        private void Update()
        {
            if (Searching) _typingFrame = Time.frameCount;
            if (!IsOpen) return;
            var player = Player.m_localPlayer;
            if (player == null || player.IsDead() || InventoryGui.IsVisible() || Minimap.IsOpen() != Current.OverMap || StoreGui.IsVisible())
            {
                Close();
                return;
            }
            _readingWheel = true;
            var wheel = ZInput.GetMouseScrollWheel();
            _readingWheel = false;
            Scroll(Current, wheel, Time.unscaledDeltaTime);
        }

        // A window with a title, a line under it, a scrolling list (fill Content, then set its height with Done) and a
        // footer line. Step: how far one wheel step scrolls. Back: if given, a Back button that does it (opens the window
        // this one was opened from). ListTop: where the list starts (more than 108 leaves room for the caller's controls).
        internal static Window Open(string title, string subtitle, float width, float height, string footer, float step, Action back = null,
            float listTop = 108f)
        {
            var inv = InventoryGui.instance;
            if (inv == null) return null;
            Close();
            UseGameFont();
            var w = new Window { Width = width, Height = height, Step = step };

            // Its own screen canvas, scaled like the inventory's and drawn just above it.
            var source = inv.m_crafting != null ? inv.m_crafting.GetComponentInParent<Canvas>()?.rootCanvas : null;
            w.Root = new GameObject("DigiCreative_Window", typeof(RectTransform));
            w.Root.transform.SetParent(inv.transform.parent, false);
            var canvas = w.Root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = (source != null ? source.sortingOrder : 0) + 5;
            var scaler = w.Root.AddComponent<CanvasScaler>();
            var sourceScaler = source != null ? source.GetComponent<CanvasScaler>() : null;
            if (sourceScaler != null)
            {
                scaler.uiScaleMode = sourceScaler.uiScaleMode;
                scaler.scaleFactor = sourceScaler.scaleFactor;
                scaler.referenceResolution = sourceScaler.referenceResolution;
                scaler.screenMatchMode = sourceScaler.screenMatchMode;
                scaler.matchWidthOrHeight = sourceScaler.matchWidthOrHeight;
                scaler.referencePixelsPerUnit = sourceScaler.referencePixelsPerUnit;
            }
            w.Root.AddComponent<GraphicRaycaster>();

            var panel = w.Panel = Rect("Window", w.Root.transform);
            panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0.5f, 0.5f);
            panel.sizeDelta = new Vector2(width, height);
            panel.anchoredPosition = Vector2.zero;
            var bg = panel.gameObject.AddComponent<Image>();
            var look = inv.m_crafting != null ? inv.m_crafting.GetComponentsInChildren<Image>(true).FirstOrDefault(i => i.name == "Bkg") : null;
            if (look != null && look.sprite != null)
            {
                bg.sprite = look.sprite;
                bg.type = look.type;
                bg.material = look.material;
                bg.color = look.color;
                bg.pixelsPerUnitMultiplier = look.pixelsPerUnitMultiplier;
            }
            else bg.color = new Color(0.08f, 0.07f, 0.06f, 0.96f);

            Place(Text(panel, title, 34f, Gold, TextAlignmentOptions.Left).rectTransform, Pad, 14f, width - 120f, 44f);
            var close = Text(panel, "X", 28f, Body, TextAlignmentOptions.Center);
            Place(close.rectTransform, width - Pad - 36f, 14f, 36f, 36f);
            close.raycastTarget = true;
            close.gameObject.AddComponent<Button>().onClick.AddListener(Close);
            // Opened from another window (Materials from the schematic library): a button back to it, next to the X.
            if (back != null) Button(panel, "Back", width - Pad - 36f - 12f - 116f, 15f, 116f, 36f, () => back());
            Place(Text(panel, subtitle, 20f, Dim, TextAlignmentOptions.Left).rectTransform, Pad, 62f, width - 2 * Pad, 30f);

            var listY = listTop;
            var listHeight = height - listY - 52f;
            w.View = Rect("List", panel);
            Place(w.View, Pad, listY, width - 2 * Pad - ScrollWidth - 6f, listHeight);
            w.View.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.28f);
            w.View.gameObject.AddComponent<RectMask2D>();
            w.Content = Rect("Content", w.View);
            w.Content.anchorMin = new Vector2(0f, 1f);
            w.Content.anchorMax = new Vector2(1f, 1f);
            w.Content.pivot = new Vector2(0.5f, 1f);
            w.Content.anchoredPosition = Vector2.zero;
            var scroll = w.View.gameObject.AddComponent<ScrollRect>();
            scroll.content = w.Content;
            scroll.viewport = w.View;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 0f; // the wheel is handled in Scroll(): Valheim's wheel steps are tiny for a ScrollRect
            scroll.inertia = false;
            if (inv.m_recipeListScroll != null)
            {
                var bar = Instantiate(inv.m_recipeListScroll.gameObject, panel, false).GetComponent<Scrollbar>();
                bar.name = "Scrollbar";
                bar.onValueChanged = new Scrollbar.ScrollEvent();
                bar.direction = Scrollbar.Direction.BottomToTop;
                Place((RectTransform)bar.transform, width - Pad - ScrollWidth, listY, ScrollWidth, listHeight);
                scroll.verticalScrollbar = bar;
                scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
            }
            w.RowWidth = width - 2 * Pad - ScrollWidth - 6f;
            Place(Text(panel, footer, 18f, Dim, TextAlignmentOptions.Left).rectTransform, Pad, height - 42f, width - 2 * Pad, 28f);
            Current = w;
            return w;
        }

        // The list is filled: its height, scrolled to the top.
        internal static void Done(Window w, float contentHeight)
        {
            w.Content.sizeDelta = new Vector2(0f, contentHeight);
            w.Content.anchoredPosition = Vector2.zero;
            w.Gliding = false;
        }

        internal static void Close()
        {
            if (Current != null && Current.Root != null) Destroy(Current.Root);
            Current = null;
        }

        // Smooth wheel scrolling: each wheel step sets a target further down or up, and the list glides there.
        private static void Scroll(Window w, float wheel, float dt)
        {
            if (w.Content == null || w.View == null) return;
            var range = Mathf.Max(0f, w.Content.rect.height - w.View.rect.height);
            var current = w.Content.anchoredPosition.y; // 0 = top, range = bottom
            if (Mathf.Abs(wheel) > 0.0001f && range > 0f)
            {
                if (!w.Gliding) w.Target = current;
                w.Target = Mathf.Clamp(w.Target - Mathf.Sign(wheel) * w.Step, 0f, range);
                w.Gliding = true;
            }
            if (!w.Gliding) return;
            var next = Mathf.Lerp(current, w.Target, 1f - Mathf.Exp(-dt * 16f));
            if (Mathf.Abs(next - w.Target) < 0.5f)
            {
                next = w.Target;
                w.Gliding = false;
            }
            w.Content.anchoredPosition = new Vector2(w.Content.anchoredPosition.x, next);
        }

        // --- While a window is open: free mouse, no character input, the wheel scrolls the list, Escape closes it ---

        private static void NoInput(ref bool __result)
        {
            if (IsOpen) __result = false;
        }

        private static void FreeMouse()
        {
            if (!IsOpen) return;
            ZCursor.LockState = CursorLockMode.None;
            ZCursor.Show();
        }

        private static void OwnWheel(ref float __result)
        {
            if (IsOpen && !_readingWheel) __result = 0f;
        }

        // The frame in which Escape closed a window: the game's menu must not open from that same key press, also when a
        // second sub-mod of the same mod has patched this too.
        private static int _escapeFrame = -1;

        private static bool EscapeCloses()
        {
            if (_escapeFrame == Time.frameCount) return false;
            if (!IsOpen) return true;
            if (Searching) return false; // Escape leaves the search box first
            if (ZInput.GetKeyDown(KeyCode.Escape) || ZInput.GetButtonDown("Menu"))
            {
                Close();
                _escapeFrame = Time.frameCount;
            }
            return false;
        }

        // For a mod with several sub-mods that show windows: one Windows part on the mod's object, there while any of
        // them uses it.
        private static int _users;

        internal static void Use(GameObject host)
        {
            _users++;
            if (host.GetComponent<Windows>() == null) host.AddComponent<Windows>();
        }

        internal static void Release(GameObject host)
        {
            if (--_users > 0) return;
            _users = 0;
            var part = host != null ? host.GetComponent<Windows>() : null;
            if (part != null) Destroy(part);
        }

        // While typing in a window's box the game sees a text input open (no keys, no chat). Enter ends the typing in the
        // same frame the chat reads it, so the box counts as open one frame longer: Enter does not also open the chat.
        private static int _typingFrame = -10;

        private static void SearchTyping(ref bool __result)
        {
            if (Typing) __result = true;
        }

        // --- Small UI helpers --------------------------------------------------------------------------------------

        // The game's font for Text (set by Open; elements built into another UI, without a window, call this first).
        internal static void UseGameFont()
        {
            var inv = InventoryGui.instance;
            if (inv == null || inv.m_recipeName == null) return;
            _font = inv.m_recipeName.font;
            _fontMaterial = inv.m_recipeName.fontSharedMaterial;
        }

        internal static RectTransform Rect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        // Position from the parent's top-left corner, y going down.
        internal static void Place(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
        }

        internal static TextMeshProUGUI Text(Transform parent, string text, float size, Color color, TextAlignmentOptions align)
        {
            var t = Rect("Text", parent).gameObject.AddComponent<TextMeshProUGUI>();
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

        internal static Image Picture(RectTransform parent, Sprite sprite, float x, float y, float width, float height)
        {
            var rect = Rect("Picture", parent);
            Place(rect, x, y, width, height);
            var image = rect.gameObject.AddComponent<Image>();
            image.sprite = sprite;
            image.preserveAspect = true;
            image.raycastTarget = false;
            image.color = sprite != null ? Color.white : new Color(0f, 0f, 0f, 0.3f);
            return image;
        }

        // A button like the crafting menu's Craft tab (a plain one if that is not found).
        internal static Button Button(RectTransform parent, string label, float x, float y, float width, float height, UnityAction click)
        {
            var inv = InventoryGui.instance;
            GameObject go;
            if (inv != null && inv.m_tabCraft != null)
            {
                go = Instantiate(inv.m_tabCraft.gameObject, parent, false);
                foreach (var text in go.GetComponentsInChildren<TMP_Text>(true)) text.text = label;
            }
            else
            {
                go = Rect("Button", parent).gameObject;
                go.AddComponent<Image>().color = new Color(0.3f, 0.25f, 0.2f, 0.9f);
                go.AddComponent<Button>();
                Text(go.transform, label, 18f, Body, TextAlignmentOptions.Center).rectTransform.sizeDelta = new Vector2(width, height);
            }
            go.name = "Button " + label;
            var button = go.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(click);
            button.interactable = true;
            Place((RectTransform)go.transform, x, y, width, height);
            return button;
        }

        // A search box like the Cookbook's (the inventory slot's background, a hint, an x that empties it).
        internal static TMP_InputField SearchBox(RectTransform parent, float x, float y, float width, float height, string hint)
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
            area.gameObject.AddComponent<RectMask2D>();
            var placeholder = Text(area, hint, 19f, Dim, TextAlignmentOptions.Left);
            Stretch(placeholder.rectTransform);
            placeholder.fontStyle = FontStyles.Italic;
            var text = Text(area, "", 19f, Body, TextAlignmentOptions.Left);
            Stretch(text.rectTransform);
            text.richText = false;
            var field = box.gameObject.AddComponent<TMP_InputField>();
            field.textViewport = area;
            field.textComponent = text;
            field.placeholder = placeholder;
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

        internal static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        internal static void SetLabel(Button button, string label)
        {
            foreach (var text in button.GetComponentsInChildren<TMP_Text>(true)) text.text = label;
        }
    }
}
