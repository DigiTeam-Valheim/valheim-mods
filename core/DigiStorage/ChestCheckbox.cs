// A checkbox in the chest window that sets a flag on the chest itself (its network data, so every player sees the same).
// The shape of Epic Loot's checkboxes (and the Cookbook's): the game's slot background and an orange tick, with a short
// label beside it ("Crafting", "Store Nearby"; grey when unticked). The boxes stand where the chest's title was, centred
// between Take all and Place stacks, always in the same order (Crafting, then Store Nearby) whichever combined mod draws
// which: each mod compiles this file in, and the boxes find each other by name. While one shows, the title is hidden
// (the row was too crowded: the title ran into the bare boxes); a title hidden by someone else (Store In Backpacks) is
// left alone. Only in chests built by players (not carts, ships or graves); there the title is as before. A box is laid
// out again while the window is open, so it follows the other box, and a swapped container (Adventure Backpacks' tabs)
// is noticed at once. The box and its label both click; hovering either explains it.
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DigiStorage
{
    public sealed class ChestCheckbox
    {
        public const string StoreName = "DigiStoreNearbyCheckbox";
        public const string TakeName = "DigiTakeFromCheckbox";
        private const string TitleHidden = "DigiChestTitleHidden"; // a mark on a title these boxes hid (and so give back)
        private static readonly string[] Order = { TakeName, StoreName };
        private const float LabelGap = 7f, Between = 26f, Margin = 12f, LabelSize = 18f;
        private static readonly Color Off = new Color(0.55f, 0.53f, 0.5f, 1f);

        private readonly string _name, _flag, _label, _title, _tooltip;
        private RectTransform _box;
        private Toggle _toggle;
        private TMP_Text _text;
        private RectTransform _labelRect;
        private Color _on = new Color(1f, 0.63f, 0.24f, 1f);
        private bool _updating;
        private Container _shownFor;
        private static Sprite _tick;

        public float Size = 26f;

        // flag: the chest's ZDO bool that an unticked box sets. label: the short text beside the box; title and tooltip:
        // what hovering shows.
        public ChestCheckbox(string name, string flag, string label, string title, string tooltip)
        {
            _name = name;
            _flag = flag;
            _label = label;
            _title = title;
            _tooltip = tooltip;
        }

        public bool Visible => _box != null && _box.gameObject.activeSelf;

        public void Destroy()
        {
            if (_box != null)
            {
                _box.gameObject.SetActive(false); // gone for the title check below already (Destroy waits for the frame end)
                Object.Destroy(_box.gameObject);
            }
            _box = null;
            _toggle = null;
            _text = null;
            _labelRect = null;
            _shownFor = null;
            if (InventoryGui.instance != null) SyncTitle(InventoryGui.instance);
        }

        // Call when the inventory opens (and from Update: it only acts when the shown container changed).
        public void Refresh(InventoryGui gui, bool show)
        {
            var chest = gui.m_currentContainer;
            _shownFor = chest;
            if (!show || chest == null || !IsBuiltChest(chest))
            {
                if (_box != null) _box.gameObject.SetActive(false);
                // No container (the chest just closed): the title stays as it is. Given back here, it showed up again
                // while the closing chest panel faded out. It comes back when something else opens (a cart, a backpack).
                if (chest != null || !show) SyncTitle(gui);
                return;
            }
            if (_box == null) Create(gui);
            if (_box == null) return;
            _box.gameObject.SetActive(true);
            Layout(gui);
            _updating = true;
            _toggle.isOn = !Blocked(chest);
            _updating = false;
            Tint();
            SyncTitle(gui);
        }

        // While the window closes (it slides away for a moment, and the game still calls it visible for a frame), nothing
        // changes: the boxes go with the panel. Hidden at once, they vanished before the rest of the chest (2026-11-20).
        public static bool Closing(InventoryGui gui) => gui.m_hiddenFrames > 0; // counts up from the frame the window was hidden

        public void Update(InventoryGui gui, bool show)
        {
            if (Closing(gui)) return;
            if (gui.m_currentContainer != _shownFor) Refresh(gui, show);
            else
            {
                if (Visible) Layout(gui);
                if (gui.m_currentContainer != null) SyncTitle(gui); // the other mod's box may have come or gone
            }
        }

        public static bool IsBuiltChest(Container chest) =>
            chest.GetComponent<Piece>() != null && chest.m_wagon == null && chest.GetComponentInParent<Ship>() == null &&
            chest.GetComponent<TombStone>() == null;

        private bool Blocked(Container chest)
        {
            var view = chest.m_nview;
            return view != null && view.IsValid() && view.GetZDO().GetBool(_flag);
        }

        private void Changed(bool allowed)
        {
            Tint();
            if (_updating || InventoryGui.instance == null) return;
            var chest = InventoryGui.instance.m_currentContainer;
            var view = chest != null ? chest.m_nview : null;
            if (view == null || !view.IsValid()) return;
            if (!view.IsOwner()) view.ClaimOwnership();
            view.GetZDO().Set(_flag, !allowed);
        }

        private void Tint()
        {
            if (_text != null && _toggle != null) _text.color = _toggle.isOn ? _on : Off;
        }

        // The box (left) and its label (right) in one object, which takes the clicks and the hover.
        private void Create(InventoryGui gui)
        {
            if (gui.m_stackAllButton == null) return;
            var parent = gui.m_stackAllButton.transform.parent;
            var old = parent.Find(_name);
            if (old != null) Object.Destroy(old.gameObject); // left by an earlier load of this mod

            var root = new GameObject(_name, typeof(RectTransform));
            root.transform.SetParent(parent, false);
            _box = (RectTransform)root.transform;
            var hit = root.AddComponent<Image>(); // invisible: the label's part of the object takes clicks too
            hit.color = new Color(0f, 0f, 0f, 0f);

            var frame = new GameObject("Background", typeof(RectTransform));
            frame.transform.SetParent(_box, false);
            var frameRect = (RectTransform)frame.transform;
            frameRect.anchorMin = frameRect.anchorMax = new Vector2(0f, 0.5f);
            frameRect.pivot = new Vector2(0f, 0.5f);
            frameRect.anchoredPosition = Vector2.zero;
            frameRect.sizeDelta = new Vector2(Size, Size);
            var frameImage = frame.AddComponent<Image>();
            var slot = SlotSprite(gui);
            if (slot != null)
            {
                frameImage.sprite = slot;
                frameImage.type = Image.Type.Sliced;
            }
            frameImage.color = new Color(0f, 0f, 0f, 0.5f);

            var mark = new GameObject("Checkmark", typeof(RectTransform));
            mark.transform.SetParent(frameRect, false);
            var markRect = (RectTransform)mark.transform;
            markRect.anchorMin = Vector2.zero;
            markRect.anchorMax = Vector2.one;
            markRect.offsetMin = new Vector2(-4f, -4f); // a bit bigger than the box, like Epic Loot's
            markRect.offsetMax = new Vector2(4f, 4f);
            var markImage = mark.AddComponent<Image>();
            markImage.sprite = TickSprite();
            // The same orange as the chest window's buttons (their text colour), the label in the buttons' font.
            var buttonText = gui.m_stackAllButton.GetComponentInChildren<TMP_Text>(true);
            if (buttonText != null) _on = buttonText.color;
            markImage.color = _on;
            markImage.raycastTarget = false;

            var labelObject = new GameObject("Label", typeof(RectTransform));
            labelObject.transform.SetParent(_box, false);
            _labelRect = (RectTransform)labelObject.transform;
            _text = labelObject.AddComponent<TextMeshProUGUI>();
            if (buttonText != null)
            {
                _text.font = buttonText.font;
                _text.fontSharedMaterial = buttonText.fontSharedMaterial;
            }
            _text.fontSize = LabelSize; // the buttons' text sizes itself (it read as 12 here: too small beside the box)
            _text.enableAutoSizing = false;
            _text.enableWordWrapping = false;
            _text.alignment = TextAlignmentOptions.MidlineLeft;
            _text.raycastTarget = false;
            _text.text = _label;
            _labelRect.anchorMin = _labelRect.anchorMax = new Vector2(0f, 0.5f);
            _labelRect.pivot = new Vector2(0f, 0.5f);
            _labelRect.anchoredPosition = new Vector2(Size + LabelGap, 0f);
            FitLabel(_label.Length * LabelSize * 0.55f); // a guess until the text has been measured (FitLabel in Layout)
            _toggle = root.AddComponent<Toggle>();
            _toggle.targetGraphic = frameImage;
            _toggle.graphic = markImage;
            _toggle.isOn = true;
            _toggle.onValueChanged.AddListener(Changed);
            var tooltip = root.AddComponent<UITooltip>();
            tooltip.m_tooltipPrefab = gui.m_playerGrid?.m_elementPrefab?.GetComponent<UITooltip>()?.m_tooltipPrefab;
            tooltip.Set(_title, _tooltip);
        }

        // Where the title was: centred between Take all's right edge and Place stacks' left edge, on Place stacks' middle.
        // Both boxes (whichever mods draw them) use the same order and sums, so they line up; too narrow a row shrinks them.
        public void Layout(InventoryGui gui)
        {
            if (_box == null || gui.m_stackAllButton == null) return;
            // The label's real width: right after the text is made it measures far too small (its font is not ready yet),
            // so it is measured again on every layout until it holds.
            if (_text != null)
            {
                var measured = _text.preferredWidth;
                if (measured > LabelSize) FitLabel(measured);
            }
            var parent = (RectTransform)_box.parent;
            float total = 0f, before = 0f;
            var count = 0;
            foreach (var name in Order)
            {
                var other = parent.Find(name) as RectTransform;
                if (other == null || !other.gameObject.activeSelf) continue;
                if (count++ > 0) total += Between;
                if (other == _box) before = total;
                total += other.sizeDelta.x;
            }
            var right = Edge((RectTransform)gui.m_stackAllButton.transform, parent, false);
            var takeAll = gui.m_takeAllButton != null && gui.m_takeAllButton.gameObject.activeInHierarchy
                ? (RectTransform)gui.m_takeAllButton.transform : null;
            var left = takeAll != null ? Edge(takeAll, parent, true).x : right.x - total - 2f * Margin;
            var room = right.x - left - 2f * Margin;
            var scale = total > room && room > 0f ? room / total : 1f;
            var x = (left + right.x) * 0.5f - total * scale * 0.5f + (before + _box.sizeDelta.x * 0.5f) * scale;
            _box.anchorMin = _box.anchorMax = _box.pivot = new Vector2(0.5f, 0.5f);
            if (Mathf.Abs(_box.localScale.x - scale) > 0.001f) _box.localScale = new Vector3(scale, scale, 1f);
            var position = new Vector3(x, right.y, 0f);
            if ((_box.localPosition - position).sqrMagnitude > 0.01f) _box.localPosition = position;
        }

        private void FitLabel(float textWidth)
        {
            var width = Mathf.Ceil(textWidth) + 2f;
            if (Mathf.Abs(_labelRect.sizeDelta.x - width) < 0.5f) return;
            _labelRect.sizeDelta = new Vector2(width, Size);
            _box.sizeDelta = new Vector2(Size + LabelGap + width, Size);
        }

        // The middle of a button's left (or right) edge, in the parent's space.
        private static Vector3 Edge(RectTransform rect, RectTransform parent, bool rightEdge)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners); // 0 = bottom left, 1 = top left, 2 = top right, 3 = bottom right
            return parent.InverseTransformPoint(rightEdge ? (corners[2] + corners[3]) * 0.5f : (corners[0] + corners[1]) * 0.5f);
        }

        // The chest's title: hidden while one of these boxes shows, back when none does (only if these boxes hid it).
        private static void SyncTitle(InventoryGui gui)
        {
            var title = gui.m_containerName;
            if (title == null || gui.m_stackAllButton == null) return;
            var parent = gui.m_stackAllButton.transform.parent;
            var ours = false;
            foreach (var name in Order)
            {
                var box = parent.Find(name);
                if (box != null && box.gameObject.activeSelf) ours = true;
            }
            var mark = title.transform.Find(TitleHidden);
            if (ours && mark == null && title.enabled)
            {
                title.enabled = false;
                new GameObject(TitleHidden).transform.SetParent(title.transform, false);
            }
            else if (!ours && mark != null)
            {
                mark.SetParent(null); // gone for the next look at once (Destroy waits for the frame end)
                Object.Destroy(mark.gameObject);
                title.enabled = true;
            }
        }

        private static Sprite SlotSprite(InventoryGui gui)
        {
            var element = gui.m_playerGrid?.m_elementPrefab;
            if (element == null) return null;
            foreach (var image in element.GetComponentsInChildren<Image>(true))
                if (image.sprite != null && image.sprite.name == "item_background") return image.sprite;
            return null;
        }

        // A tick mark, drawn once: two thick, smooth lines on a 32x32 texture (white, tinted by the image colour).
        private static Sprite TickSprite()
        {
            if (_tick != null) return _tick;
            const int size = 32;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "DigiChestTick", filterMode = FilterMode.Bilinear };
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
            texture.Apply();
            _tick = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f));
            return _tick;
        }

        private static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            var t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return Vector2.Distance(p, a + ab * t);
        }
    }
}
