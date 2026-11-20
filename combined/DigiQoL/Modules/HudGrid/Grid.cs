// HUD Grid, the layout (also compiled into tests/probes/hudgrid to try it in a running game): the slots other mods put in
// the bottom-left of the HUD go into one grid beside the health bar, standing on the health panel's bottom: Better
// Archery's quiver slots on top, Valheim Legends' ability icons in the middle, CDEV Equipment Slots' quick slots at the
// bottom, columns lined up. Every slot gets a hotbar-like look: a dark slot, the key small top left, the count at the
// bottom. Legends' ability names go (the key, or the cooldown while it runs, stays). A group that is not there (mod not
// installed, no quiver worn, no class) leaves no gap: the rows close up from the bottom. The game's forsaken power (when
// shown) keeps its place and the grid starts right of it; Epic Loot's ability bar goes above the grid.
// The slot size is fixed (a setting, in the HUD's pixels, times the game's HUD scale). It first followed the health
// bar's length, but that bar grows and shrinks with max health (food wearing off): the slots kept changing size while
// playing, and a fresh character's short bar made them too small to show at all (2026-11-18). By default the slots are
// like the game's food squares left of the health bar: their size, each row level with one of them and the same space
// between slots as between those squares, as close to the bar on its right as they are on its left, so food column,
// bar and grid read as one block (the user's choice B, made the default 2026-11-19); "Use slot size" switches to A.
// Those mods place their parts in Update every frame, so this runs in LateUpdate. Everything is measured in screen
// pixels from the parts themselves. Restore() puts every position, size and text back (Better Archery rewrites its own
// texts on the next frame).
using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HudGrid
{
    internal static class Grid
    {
        private const float Gap = 6f, Margin = 12f, KeyPx = 13f, CountPx = 15f;

        // What a part looked like before (put back by Restore) and what we added to it.
        private sealed class Part
        {
            public Vector3 Scale;
            public Vector2 Position;
            public float BasePixels; // the measured part's width at its own scale, measured once (no size flicker)
            public Image Slot;
            public TMP_Text Key, Count, TheirKey, TheirCount, TheirName;
            public Image TheirSlot;
        }

        private static readonly Dictionary<RectTransform, Part> Parts = new Dictionary<RectTransform, Part>();
        private static readonly List<RectTransform> Row = new List<RectTransform>();
        private static Hud _hud;
        private static RectTransform _health, _abilityBar;
        private static Transform _quiverSlots; // Better Archery's "Slots" is a plain Transform (its slots are RectTransforms)
        private static bool _resolved;
        private static System.Reflection.FieldInfo _legends, _cdevWidgets, _cdevRoot;
        private static GameObject _style;
        private static Sprite _slotSprite;

        // The slot size in the HUD's pixels, and "like the food squares" (the module's settings).
        internal static Func<float> SlotSize = () => 56f;
        internal static Func<bool> LikeFood = () => true;

        private static RectTransform _food0, _food1, _food2, _healthBorder;
        private static readonly float[] RowY = new float[3];

        // ---- every frame ----

        internal static void Apply()
        {
            var hud = Hud.instance;
            if (hud == null || Player.m_localPlayer == null) return;
            if (hud != _hud && !Find(hud)) return;
            Resolve();
            FindLate();

            // Like the food squares (the user's choice B): their size, each row level with one of them, as far apart as
            // they are (also sideways), and as close to the health bar on its right as they are on its left.
            // Otherwise: the Slot size, rows from the health panel's bottom up.
            float size, gap, left;
            var gp = hud.m_gpRoot;
            var gpShown = gp != null && gp.gameObject.activeInHierarchy;
            if (LikeFood() && FoodRows(out size, out gap, out left))
            {
                if (gpShown) left = Mathf.Max(left, OnScreen(gp).xMax + gap);
            }
            else
            {
                size = Mathf.Clamp(SlotSize(), 24f, 128f) * Scale();
                gap = Gap;
                left = gpShown ? OnScreen(gp).xMax + Gap : OnScreen(_health).xMax + Margin;
                var bottom = OnScreen(_health).yMin;
                for (var i = 0; i < 3; i++) RowY[i] = bottom + i * (size + gap);
            }

            var row = 0;
            if (Potions()) Lay(RowY[row++], left, size, gap, false);
            if (Abilities()) Lay(RowY[row++], left, size, gap, false);
            if (Quiver()) Lay(RowY[row++], left, size, gap, true);
            var above = row > 0 ? RowY[row - 1] + size + gap : RowY[0];
            if (_abilityBar != null && _abilityBar.gameObject.activeInHierarchy && _abilityBar.childCount > 0)
                MoveBy(_abilityBar, OnScreen(_abilityBar).min, new Vector2(left, above));
        }

        // The game's three food squares left of the health bar (food0 at the bottom): their size, the space between them,
        // their bottoms as the rows, and the grid's left edge (the bar's right edge plus the food squares' distance to its
        // left edge). False when the HUD has no such squares (then the Slot size layout is used).
        private static bool FoodRows(out float size, out float gap, out float left)
        {
            size = gap = left = 0f;
            if (_food0 == null || _food1 == null || _food2 == null || _healthBorder == null) return false;
            Rect a = OnScreen(_food0), b = OnScreen(_food1), c = OnScreen(_food2), bar = OnScreen(_healthBorder);
            if (a.width < 8f || b.yMin <= a.yMin || c.yMin <= b.yMin) return false;
            size = a.width;
            gap = Mathf.Max(0f, b.yMin - a.yMax);
            left = bar.xMax + Mathf.Max(0f, bar.xMin - a.xMax);
            RowY[0] = a.yMin;
            RowY[1] = b.yMin;
            RowY[2] = c.yMin;
            return true;
        }

        // The parts of this HUD (a new HUD after logging in again: found again).
        private static bool Find(Hud hud)
        {
            Restore();
            _hud = hud;
            _health = hud.m_healthPanel;
            _quiverSlots = null;
            _abilityBar = null;
            _food0 = _health != null ? _health.Find("food0") as RectTransform : null;
            _food1 = _health != null ? _health.Find("food1") as RectTransform : null;
            _food2 = _health != null ? _health.Find("food2") as RectTransform : null;
            _healthBorder = _health != null ? _health.Find("Health/border") as RectTransform : null;
            var hotbar = hud.GetComponentInChildren<HotkeyBar>(true);
            _style = hotbar != null ? hotbar.m_elementPrefab : null;
            // The forsaken power's slot background: the game's dark "Background" (the hotbar prefab's bkg is tinted white).
            _slotSprite = hud.m_gpRoot != null ? hud.m_gpRoot.Find("Bkg")?.GetComponent<Image>()?.sprite : null;
            return _health != null;
        }

        // Better Archery and Epic Loot make their bars when they need them: looked for again until there (a child lookup).
        private static void FindLate()
        {
            if (_quiverSlots == null) _quiverSlots = _health.Find("QuiverHotkeyBar/Slots");
            if (_abilityBar == null && _hud.m_rootObject != null) _abilityBar = _hud.m_rootObject.transform.Find("AbilityBar(Clone)") as RectTransform;
        }

        // The other mods' types, looked up once (a type search scans every assembly).
        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            var legends = AccessTools.TypeByName("ValheimLegends.ValheimLegends");
            _legends = legends != null ? AccessTools.Field(legends, "abilitiesStatus") : null;
            var quickBar = AccessTools.TypeByName("CDEV.EquipmentSlots.QuickBar");
            _cdevWidgets = quickBar != null ? AccessTools.Field(quickBar, "Widgets") : null;
            var widget = quickBar != null ? AccessTools.Inner(quickBar, "Widget") : null;
            _cdevRoot = widget != null ? AccessTools.Field(widget, "Root") : null;
        }

        // ---- the rows ----

        private static bool Potions()
        {
            Row.Clear();
            if (_cdevWidgets?.GetValue(null) is Array widgets && _cdevRoot != null)
                foreach (var w in widgets)
                    if (w != null && _cdevRoot.GetValue(w) is RectTransform root && root != null && root.gameObject.activeInHierarchy)
                        Row.Add(root);
            return Row.Count > 0;
        }

        private static bool Abilities()
        {
            Row.Clear();
            if (_legends?.GetValue(null) is IList list)
                foreach (var o in list)
                    if (o is RectTransform rt && rt != null && rt.gameObject.activeInHierarchy) Row.Add(rt);
            return Row.Count > 0;
        }

        private static bool Quiver()
        {
            Row.Clear();
            if (_quiverSlots != null && _quiverSlots.gameObject.activeInHierarchy)
                foreach (Transform t in _quiverSlots)
                    if (t.gameObject.activeSelf) Row.Add((RectTransform)t);
            return Row.Count > 0;
        }

        // One row: each part on its cell. The quiver's slots are the game's hotbar slots already: only sized, moved and
        // their keys shortened ("LeftAlt + 1" -> "Alt1", "100 / 100" -> "100").
        private static void Lay(float y, float left, float size, float gap, bool hotbarSlots)
        {
            for (var i = 0; i < Row.Count; i++)
            {
                var cell = new Rect(left + i * (size + gap), y, size, size);
                if (hotbarSlots) HotbarSlot(Row[i], cell);
                else OtherSlot(Row[i], cell);
            }
        }

        private static Part PartOf(RectTransform rt)
        {
            if (!Parts.TryGetValue(rt, out var p))
            {
                p = new Part { Scale = rt.localScale, Position = rt.anchoredPosition };
                Parts[rt] = p;
            }
            return p;
        }

        private static void HotbarSlot(RectTransform slot, Rect cell)
        {
            var p = PartOf(slot);
            Size(slot, p, slot, cell.width);
            MoveBy(slot, OnScreen(slot).min, cell.min);
            if (p.TheirKey == null)
            {
                p.TheirKey = slot.Find("binding")?.GetComponent<TMP_Text>();
                p.TheirCount = slot.Find("amount")?.GetComponent<TMP_Text>();
            }
            if (p.TheirKey != null && p.TheirKey.text.Length > 0) p.TheirKey.text = Short(p.TheirKey.text);
            if (p.TheirCount != null)
            {
                var at = p.TheirCount.text.IndexOf(" / ", StringComparison.Ordinal);
                if (at > 0) p.TheirCount.text = p.TheirCount.text.Substring(0, at);
            }
        }

        // "LeftAlt + 1" -> "Alt1", "RightControl + 2" -> "Ctrl2"; a single key stays.
        private static string Short(string key)
        {
            var plus = key.IndexOf(" + ", StringComparison.Ordinal);
            if (plus < 0) return key;
            var mod = key.Substring(0, plus).Replace("Left", "").Replace("Right", "").Replace("Control", "Ctrl");
            return mod + key.Substring(plus + 3);
        }

        // A Legends ability or a CDEV quick slot: its icon at 80% of the cell, centred, on our hotbar-style slot; our
        // key and count labels show their texts (theirs hidden); Legends' ability name hidden.
        private static void OtherSlot(RectTransform root, Rect cell)
        {
            var p = PartOf(root);
            var icon = root.Find("Icon") as RectTransform;
            if (icon == null) return;
            Size(root, p, icon, cell.width * 0.8f);
            MoveBy(root, OnScreen(icon).center, cell.center);
            if (p.Slot == null) Decorate(root, p);
            Fit(p.Slot.rectTransform, cell);
            if (p.TheirKey != null && p.Key != null)
            {
                p.Key.text = p.TheirKey.text;
                Fit(p.Key.rectTransform, new Rect(cell.xMin + 4f, cell.yMax - KeyPx * Scale() - 4f, cell.width - 6f, KeyPx * Scale() + 4f));
                p.Key.fontSize = KeyPx * Scale() / PixelsPerUnit(p.Key.rectTransform);
            }
            if (p.TheirCount != null && p.Count != null)
            {
                var show = p.TheirCount.gameObject.activeInHierarchy && p.TheirCount.text.Length > 0;
                if (p.Count.gameObject.activeSelf != show) p.Count.gameObject.SetActive(show);
                p.Count.text = p.TheirCount.text;
                Fit(p.Count.rectTransform, new Rect(cell.xMin, cell.yMin + 1f, cell.width, CountPx * Scale() + 4f));
                p.Count.fontSize = CountPx * Scale() / PixelsPerUnit(p.Count.rectTransform);
            }
        }

        // Legends: "TimeText" is the key (or the cooldown), "Name" the ability's name. CDEV: "Name" is the key,
        // "TimeText" the count, "Bkg" its own faint slot.
        private static void Decorate(RectTransform root, Part p)
        {
            var legends = root.Find("Cooldown") != null;
            p.TheirKey = root.Find(legends ? "TimeText" : "Name")?.GetComponent<TMP_Text>();
            p.TheirCount = legends ? null : root.Find("TimeText")?.GetComponent<TMP_Text>();
            p.TheirName = legends ? root.Find("Name")?.GetComponent<TMP_Text>() : null;
            p.TheirSlot = legends ? null : root.Find("Bkg")?.GetComponent<Image>();
            foreach (var t in new[] { p.TheirKey, p.TheirCount, p.TheirName })
                if (t != null) t.enabled = false;
            if (p.TheirSlot != null) p.TheirSlot.enabled = false;

            p.Slot = NewPart<Image>(root, "DigiHudGridSlot");
            p.Slot.raycastTarget = false;
            p.Slot.sprite = _slotSprite;
            p.Slot.type = Image.Type.Sliced;
            p.Slot.color = new Color(0f, 0f, 0f, 0.55f); // as dark as the quiver's slots beside it
            p.Slot.transform.SetAsFirstSibling();
            if (p.TheirKey != null) p.Key = Label(root, "DigiHudGridKey", "binding", TextAlignmentOptions.TopLeft);
            if (p.TheirCount != null) p.Count = Label(root, "DigiHudGridCount", "amount", TextAlignmentOptions.Bottom);
        }

        private static TMP_Text Label(RectTransform root, string name, string styleOf, TextAlignmentOptions align)
        {
            var t = NewPart<TextMeshProUGUI>(root, name);
            var style = _style != null ? _style.transform.Find(styleOf)?.GetComponent<TMP_Text>() : null;
            if (style != null)
            {
                t.font = style.font;
                t.fontSharedMaterial = style.fontSharedMaterial;
                t.color = style.color;
            }
            t.enableAutoSizing = false;
            t.enableWordWrapping = false;
            t.overflowMode = TextOverflowModes.Overflow;
            t.alignment = align;
            t.raycastTarget = false;
            return t;
        }

        private static T NewPart<T>(RectTransform parent, string name) where T : Component
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            return go.AddComponent<T>();
        }

        // ---- putting back ----

        internal static void Restore()
        {
            foreach (var pair in Parts)
            {
                var rt = pair.Key;
                var p = pair.Value;
                if (rt == null) continue;
                rt.localScale = p.Scale;
                rt.anchoredPosition = p.Position;
                foreach (var t in new[] { p.TheirKey, p.TheirCount, p.TheirName })
                    if (t != null) t.enabled = true;
                if (p.TheirSlot != null) p.TheirSlot.enabled = true;
                foreach (var made in new Component[] { p.Slot, p.Key, p.Count })
                    if (made != null) UnityEngine.Object.Destroy(made.gameObject);
            }
            Parts.Clear();
            RefreshQuiverTexts();
            _hud = null;
        }

        // Better Archery writes its key labels only when the gamepad state changes and an amount only when the stack
        // changes: flip what it remembers so its next Update writes both again.
        private static void RefreshQuiverTexts()
        {
            var bar = _quiverSlots != null ? _quiverSlots.parent : null;
            var hud = bar != null ? bar.GetComponent("QuiverHud") : null;
            if (hud == null) return;
            var type = hud.GetType();
            var last = AccessTools.Field(type, "_lastGamepad");
            if (last != null && last.FieldType == typeof(bool)) last.SetValue(hud, !(bool)last.GetValue(hud));
            if (AccessTools.Field(type, "_slots")?.GetValue(hud) is Array slots)
                foreach (var slot in slots)
                {
                    var stack = slot != null ? AccessTools.Field(slot.GetType(), "StackText") : null;
                    if (stack != null && stack.FieldType == typeof(int)) stack.SetValue(slot, -1);
                }
        }

        // ---- screen pixels ----

        // Scales a part (from its own size as it was) so that "measured" is "pixels" wide on screen.
        // The measured width at the part's own scale is taken once: measuring every frame followed whatever the mod did
        // to its icon that frame.
        private static void Size(RectTransform rt, Part p, RectTransform measured, float pixels)
        {
            if (p.BasePixels <= 1f)
            {
                rt.localScale = p.Scale;
                p.BasePixels = OnScreen(measured).width;
            }
            if (p.BasePixels > 1f) rt.localScale = p.Scale * (pixels / p.BasePixels);
        }

        // One canvas pixel in screen pixels (the game's HUD scale).
        private static float Scale()
        {
            var canvas = _health != null ? _health.GetComponentInParent<Canvas>() : null;
            return canvas != null ? canvas.rootCanvas.scaleFactor : 1f;
        }

        private static void Fit(RectTransform rt, Rect screen)
        {
            var ppu = PixelsPerUnit(rt);
            rt.sizeDelta = new Vector2(screen.width / ppu, screen.height / ppu);
            MoveBy(rt, OnScreen(rt).center, screen.center);
        }

        private static float PixelsPerUnit(RectTransform rt)
        {
            var c = new Vector3[4];
            rt.GetWorldCorners(c);
            var canvas = rt.GetComponentInParent<Canvas>().rootCanvas;
            var cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            var a = RectTransformUtility.WorldToScreenPoint(cam, c[0]);
            var b = RectTransformUtility.WorldToScreenPoint(cam, c[3]);
            var units = rt.rect.width;
            return units > 0.001f ? Mathf.Max(0.001f, Vector2.Distance(a, b) / units) : Mathf.Max(0.001f, rt.lossyScale.x);
        }

        private static void MoveBy(RectTransform rt, Vector2 from, Vector2 to)
        {
            var canvas = rt.GetComponentInParent<Canvas>().rootCanvas;
            var cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            var area = (RectTransform)canvas.transform;
            RectTransformUtility.ScreenPointToWorldPointInRectangle(area, from, cam, out var a);
            RectTransformUtility.ScreenPointToWorldPointInRectangle(area, to, cam, out var b);
            if ((b - a).sqrMagnitude > 0.0001f) rt.position += b - a;
        }

        private static Rect OnScreen(RectTransform r)
        {
            var canvas = r.GetComponentInParent<Canvas>().rootCanvas;
            var cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            var c = new Vector3[4];
            r.GetWorldCorners(c);
            Vector2 a = RectTransformUtility.WorldToScreenPoint(cam, c[0]), b = RectTransformUtility.WorldToScreenPoint(cam, c[2]);
            return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
        }
    }
}
