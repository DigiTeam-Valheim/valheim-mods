// Enchanting Layout: Epic Loot's enchanting table window, laid out evenly. Its tabs (icons only, in one column down the
// left, so tight that two could show a name) are split over both sides: the first half down the left, the rest down the
// right, in the same order, spread over the window's full height with room between them; each shows its name under its
// icon in a label of its own, locked ones too (Epic Loot's own hover text on a locked tab says what unlocks it). The wooden frame is made wider on the right by as much as it reaches left of the panels, so
// both columns sit inside it. Epic Loot's screens themselves are untouched, but for the Sacrifice screen: its status
// bar lay over the last row of rarity buttons, so the bar and the Sacrifice button sit lower.
// Other mods' tabs in Epic Loot's tab column (Socket Combine's Combine tab) are laid out with the rest. A marker object
// "DigiEnchantingLayout" under the window's Root tells them the layout is on (Socket Combine then joins the column
// instead of hanging its own tab on the right).
// How Epic Loot builds it (EpicLoot_UnityLib.EnchantingTableUI): Root holds the frame, the TabHandler (a column with a
// layout group; its children are the tab buttons, named like EnchantingTabs values) and the Panels. Its tab switches
// (EnchantingUIController.TabActivation) turn children of the TabHandler on and off by name, so the buttons stay its
// children here; only the column's layout group, the mask that clipped it to its own area and its darker background
// are off, and each button gets its own place.
// Switched off (F1 "0 - Sub-mods"): every position, size, text and the frame are put back.
// Light: the layout is redone only when the window opens or its set of tabs changes (checked while it is open).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EpicLoot_UnityLib;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EnchantingLayout
{
    public class Plugin : DigiCore.DigiModule
    {
        internal const string Marker = "DigiEnchantingLayout";
        private const string LabelName = "DigiTabName";
        private const float NameSize = 15f;
        private static readonly FieldInfo SetUp = AccessTools.Field(typeof(EnchantingTableUI), "_setUp");
        private static BepInEx.Logging.ManualLogSource _log;

        // Epic Loot's tab names (the buttons are named after EnchantingTabs) as shown under the icons.
        private static readonly Dictionary<string, string> Names = new Dictionary<string, string>
        {
            { "Sacrifice", "Sacrifice" }, { "ConvertMaterials", "Convert" }, { "Enchant", "Enchant" }, { "Augment", "Augment" },
            { "Disenchant", "Disenchant" }, { "Rune", "Runes" }, { "Storage", "Storage" }, { "Upgrade", "Upgrade" },
            { "DigiSocketCombine", "Combine" },
        };

        // What was changed, to put back.
        private sealed class Saved
        {
            public RectTransform Rect;
            public Vector2 AnchorMin, AnchorMax, Pivot, Position, Size;
        }


        private static EnchantingTableUI _ui;
        private static GameObject _marker;
        private static readonly List<Saved> Rects = new List<Saved>();
        private static readonly List<GameObject> Labels = new List<GameObject>();
        private static readonly List<CanvasGroup> Hidden = new List<CanvasGroup>();
        private static readonly List<Behaviour> Disabled = new List<Behaviour>();
        private static float _leftX;
        private static bool _wasOpen;
        private static string _laidOut = "";

        private void Awake() => _log = Logger;

        private void Update()
        {
            var ui = EnchantingTableUI.instance;
            if (ui == null || ui.Root == null) return;
            if (ui != _ui)
            {
                if (SetUp != null && !(SetUp.GetValue(ui) is bool ready && ready)) return;
                Restore();
                _ui = ui;
                try { Prepare(ui); }
                catch (Exception e)
                {
                    _log.LogError("Laying out the enchanting window failed: " + e);
                    Restore();
                    _ui = ui; // not again for this window
                    return;
                }
            }
            if (_marker == null) return;
            var open = ui.Root.activeInHierarchy;
            if (open && (!_wasOpen || Signature(ui) != _laidOut))
            {
                try { Lay(ui); }
                catch (Exception e) { _log.LogError("Laying out the enchanting tabs failed: " + e); }
            }
            _wasOpen = open;
            // Epic Loot lays its screens out after the window opens: the Sacrifice screen is checked twice a second while
            // open (nothing moves once it is right).
            if (open && Time.unscaledTime >= _nextSpacing)
            {
                _nextSpacing = Time.unscaledTime + 0.5f;
                try { SacrificeSpacing(ui); }
                catch (Exception e) { _log.LogError("Spacing the Sacrifice screen failed: " + e.Message); }
            }
        }

        private static float _nextSpacing;

        private void OnDestroy() => Restore();

        private static void Keep(RectTransform rect)
        {
            if (rect == null || Rects.Any(r => r.Rect == rect)) return;
            Rects.Add(new Saved { Rect = rect, AnchorMin = rect.anchorMin, AnchorMax = rect.anchorMax, Pivot = rect.pivot, Position = rect.anchoredPosition, Size = rect.sizeDelta });
        }

        private static Vector3[] Corners(RectTransform rect)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            return corners;
        }

        private static bool Holds(Vector3[] outer, Vector3[] inner) =>
            inner[0].x >= outer[0].x - 2f && inner[0].y >= outer[0].y - 2f && inner[2].x <= outer[2].x + 2f && inner[2].y <= outer[2].y + 2f;

        private static List<RectTransform> Tabs(EnchantingTableUI ui) =>
            ui.TabHandler.transform.Cast<Transform>().Where(t => t.gameObject.activeSelf && t.GetComponent<Button>() != null)
                .OrderBy(t => t.name == "DigiSocketCombine" ? 1 : 0).ThenBy(t => t.GetSiblingIndex()).Select(t => (RectTransform)t).ToList();

        private static string Signature(EnchantingTableUI ui) => string.Join("|", Tabs(ui).Select(t => t.name));

        private static RectTransform Panel(EnchantingTableUI ui) =>
            ui.Panels != null ? ui.Panels.Where(p => p != null).Select(p => (RectTransform)p.transform).FirstOrDefault() : null;

        // Once per window: the column's layout off, the frame wider, the marker for other mods.
        private static void Prepare(EnchantingTableUI ui)
        {
            var column = (RectTransform)ui.TabHandler.transform;
            var panel = Panel(ui);
            var tabs = Tabs(ui);
            if (panel == null || tabs.Count == 0)
            {
                _log.LogWarning("Epic Loot's table window has no tabs or panels: left as it is.");
                return;
            }
            var first = Corners(tabs[0]);
            _leftX = (first[0].x + first[2].x) * 0.5f;
            foreach (var group in column.GetComponents<LayoutGroup>()) Disable(group);
            foreach (var fitter in column.GetComponents<ContentSizeFitter>()) Disable(fitter);
            // The column clips to its own area (a mask on it or above it): tabs on the right would be cut away.
            for (var t = column.transform; t != null && t != ui.Root.transform.parent; t = t.parent)
            {
                foreach (var clip in t.GetComponents<RectMask2D>()) Disable(clip);
                foreach (var mask in t.GetComponents<Mask>())
                {
                    if (!mask.enabled) continue;
                    var hidden = !mask.showMaskGraphic ? mask.GetComponent<Graphic>() : null; // shown once the mask is off
                    Disable(mask);
                    if (hidden != null) Disable(hidden);
                }
            }
            // The column's own darker background ("Tabs", TabBackground: any picture overlapping the first tab but not
            // around the panels, not part of a button): there is none on the right, so it goes while laid out.
            var firstCorners = Corners(tabs[0]);
            var panelCorners = Corners(panel);
            foreach (var image in ui.Root.GetComponentsInChildren<Image>(true))
            {
                if (image == null || !image.enabled || image.gameObject == ui.Scrim || image.gameObject == ui.TabScrim) continue;
                if (image.GetComponentInParent<Button>(true) != null) continue;
                var c = Corners(image.rectTransform);
                var overlaps = c[0].x < firstCorners[2].x && c[2].x > firstCorners[0].x && c[0].y < firstCorners[2].y && c[2].y > firstCorners[0].y;
                if (overlaps && !Holds(c, panelCorners)) Disable(image);
            }
            _marker = new GameObject(Marker);
            _marker.transform.SetParent(ui.Root.transform, false);
            _marker.SetActive(false);
            _laidOut = "";
            _log.LogInfo("Enchanting window laid out on both sides.");
        }

        private static void Disable(Behaviour b)
        {
            if (b == null || !b.enabled) return;
            b.enabled = false;
            Disabled.Add(b);
        }

        // The window's wooden board: the smallest picture (not a scrim) around both the tab column and the panels.
        private static RectTransform Frame(EnchantingTableUI ui, RectTransform tab, RectTransform panel)
        {
            RectTransform best = null;
            var bestArea = float.MaxValue;
            var t = Corners(tab);
            var p = Corners(panel);
            foreach (var image in ui.Root.GetComponentsInChildren<Image>(true))
            {
                if (image == null || image.gameObject == ui.Scrim || image.gameObject == ui.TabScrim || image.sprite == null) continue;
                var c = Corners(image.rectTransform);
                if (!Holds(c, t) || !Holds(c, p)) continue;
                var area = (c[2].x - c[0].x) * (c[2].y - c[0].y);
                if (area >= bestArea) continue;
                best = image.rectTransform;
                bestArea = area;
            }
            return best;
        }

        // The frame's right edge out by extra (its own units); its children stay where they were, whatever their anchors.
        private static void Grow(RectTransform frame, float extra)
        {
            Keep(frame);
            frame.offsetMax += new Vector2(extra, 0f);
            foreach (Transform t in frame)
            {
                if (!(t is RectTransform child) || (child.anchorMin.x == 0f && child.anchorMax.x == 0f)) continue;
                Keep(child);
                child.offsetMin -= new Vector2(child.anchorMin.x * extra, 0f);
                child.offsetMax -= new Vector2(child.anchorMax.x * extra, 0f);
            }
        }

        // The tabs: the first half down the left, the rest down the right, rows spread over the panels' height.
        private static void Lay(EnchantingTableUI ui)
        {
            var tabs = Tabs(ui);
            var panel = Panel(ui);
            if (tabs.Count == 0 || panel == null) return;
            // The frame as wide on the right as it reaches left of the panels (checked each time: Socket Combine, alone,
            // widens it too and puts it back when it joins the column).
            var frame = Frame(ui, tabs[0], panel);
            if (frame != null)
            {
                var f = Corners(frame);
                var pc = Corners(panel);
                var grow = pc[2].x + (pc[0].x - f[0].x) - f[2].x;
                if (grow > 1f) Grow(frame, grow / Mathf.Max(0.0001f, frame.lossyScale.x));
            }
            var p = Corners(panel);
            var rightX = p[2].x + (p[0].x - _leftX);
            var rows = (tabs.Count + 1) / 2;
            var size = Corners(tabs[0]);
            var height = size[2].y - size[0].y;
            var top = p[2].y - height * 0.5f;
            var step = rows > 1 ? (p[2].y - p[0].y - height) / (rows - 1) : 0f;
            for (var i = 0; i < tabs.Count; i++)
            {
                var tab = tabs[i];
                Keep(tab);
                var left = i < rows;
                var row = left ? i : i - rows;
                var c = Corners(tab);
                var center = new Vector3((c[0].x + c[2].x) * 0.5f, (c[0].y + c[2].y) * 0.5f, tab.position.z);
                var target = new Vector3(left ? _leftX : rightX, top - row * step, center.z);
                tab.position += target - center;
                Name(tab);
            }
            _laidOut = Signature(ui);
        }

        // Epic Loot's Sacrifice screen: its feature status bar (FeatureStatusSmaller) lay over the last row of the "Select
        // all" rarity buttons (Mythic, Ancient). It goes just below them, and the Sacrifice button (with its "Bonus!" text)
        // below the bar; only ever down, and only as far as needed, so laying out again moves nothing (2026-11-20).
        private static void SacrificeSpacing(EnchantingTableUI ui)
        {
            var panel = ui.Panels?.Where(p => p != null).Select(p => p.transform).FirstOrDefault(p => p.name == "SacrificeContent");
            if (panel == null || !panel.gameObject.activeInHierarchy) return;
            var status = panel.Find("FeatureStatusSmaller") as RectTransform;
            var button = panel.Find("SacrificeButton") as RectTransform;
            var rarity = panel.Find("RarityButtons/Buttons") as RectTransform;
            var bonus = panel.Find("Bonus") as RectTransform;
            if (status == null || button == null || rarity == null) return;
            var low = rarity.GetComponentsInChildren<Button>(true).Select(b => Corners((RectTransform)b.transform)[0].y)
                .DefaultIfEmpty(Corners(rarity)[0].y).Min();
            var gap = 5f * panel.lossyScale.y;
            var down = low - gap - Corners(status)[2].y;
            if (down < -0.5f)
            {
                Keep(status);
                status.position += new Vector3(0f, down, 0f);
            }
            down = Corners(status)[0].y - gap - Corners(button)[2].y;
            if (down >= -0.5f) return;
            Keep(button);
            button.position += new Vector3(0f, down, 0f);
            if (bonus == null) return;
            Keep(bonus);
            bonus.position += new Vector3(0f, down, 0f);
        }

        // The tab's name under its icon, big enough to read, also on a locked tab: a label of its own on the button (Epic
        // Loot's small label sits in the part its FeatureStatus hides while a feature is locked), in Epic Loot's tab font.
        // Epic Loot's own labels (TextMeshPro and plain UI text, the normal and the selected one) are made invisible with a
        // CanvasGroup at alpha 0: switching them off was undone by Epic Loot itself (selecting a tab, a feature's status),
        // and their text showed through ours (2026-11-20).
        private static void Name(RectTransform tab)
        {
            if (!Names.TryGetValue(tab.name, out var name)) return;
            var own = tab.Find(LabelName);
            var theirs = tab.GetComponentsInChildren<TMP_Text>(true).Where(t => t.name != LabelName).ToList();
            var texts = theirs.Cast<Graphic>().Concat(tab.GetComponentsInChildren<Text>(true));
            foreach (var text in texts)
            {
                if (text.gameObject == tab.gameObject || text.transform.IsChildOf(own != null ? own : text.transform.parent) && own != null) continue;
                var group = text.GetComponent<CanvasGroup>();
                if (group == null)
                {
                    group = text.gameObject.AddComponent<CanvasGroup>();
                    Hidden.Add(group);
                }
                group.alpha = 0f;
                group.blocksRaycasts = false;
                group.interactable = false;
            }
            if (own == null)
            {
                var label = new GameObject(LabelName, typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
                label.transform.SetParent(tab, false);
                var font = theirs.FirstOrDefault(t => t.transform.parent == null || t.transform.parent.name != "Selected") ?? theirs.FirstOrDefault();
                if (font != null)
                {
                    label.font = font.font;
                    label.fontSharedMaterial = font.fontSharedMaterial;
                }
                label.color = new Color(0.95f, 0.93f, 0.88f);
                label.fontSize = NameSize;
                label.alignment = TextAlignmentOptions.Bottom;
                label.textWrappingMode = TextWrappingModes.NoWrap;
                label.overflowMode = TextOverflowModes.Overflow;
                label.raycastTarget = false;
                var rect = label.rectTransform;
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = new Vector2(2f, 3f);
                rect.offsetMax = new Vector2(-2f, -2f);
                Labels.Add(label.gameObject);
                own = label.transform;
            }
            own.GetComponent<TMP_Text>().text = name;
            own.SetAsLastSibling();
        }

        private static void Restore()
        {
            for (var i = Rects.Count - 1; i >= 0; i--)
            {
                var r = Rects[i];
                if (r.Rect == null) continue;
                r.Rect.anchorMin = r.AnchorMin;
                r.Rect.anchorMax = r.AnchorMax;
                r.Rect.pivot = r.Pivot;
                r.Rect.sizeDelta = r.Size;
                r.Rect.anchoredPosition = r.Position;
            }
            foreach (var label in Labels)
                if (label != null) Destroy(label);
            foreach (var group in Hidden)
                if (group != null) Destroy(group);
            foreach (var b in Disabled)
                if (b != null) b.enabled = true;
            Rects.Clear();
            Labels.Clear();
            Hidden.Clear();
            Disabled.Clear();
            if (_marker != null) Destroy(_marker);
            _marker = null;
            _ui = null;
            _wasOpen = false;
            _laidOut = "";
        }
    }
}
