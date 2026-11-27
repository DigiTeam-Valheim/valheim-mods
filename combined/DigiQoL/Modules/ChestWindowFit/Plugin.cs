// Chest Window Fit: a chest with more rows than the chest window shows (the Grausten chest: 8 x 5, the window shows a
// bit over 4) opens in a window tall enough for all of them, so there is no scroll bar. Any other chest or backpack
// that would scroll gets the same; one that fits is left as it is.
// The game's window (measured 2026-11-27): InventoryGui.m_container hangs under the inventory panel (top-anchored), the
// slots sit in ContainerGrid (stretched over the window, 46 px header, a RectMask2D viewport with a ScrollRect whose
// scroll bar hides itself when everything fits), each row m_elementSpace (70) high; InventoryGrid.UpdateGui sets the
// slots' height to rows x 70 every frame. So the window grows downward by what the slots lack. Its other parts follow
// by their anchors: stretched ones (background, grid) grow, bottom ones (weight) go down with the bottom; the ones
// placed from the window's middle (Take All, Place Stacks, the checkboxes of Crafting and Automation and Inventory and
// QoL, other mods' buttons there) would slide down over the first row, so they are moved up by half the growth (and
// back when it shrinks), whoever made them. The window never grows past the bottom of the screen (then it still
// scrolls, less). Closed, or a chest that fits: back as it was.
// Light: one size compare a frame while a chest is open.
using System.Collections.Generic;
using UnityEngine;

namespace ChestWindowFit
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.chestwindowfit";
        public const string Name = "Digi's Chest Window Fit";
        public const string Version = "1.0.0";

        private const float Spare = 2f;        // px of room under the last row
        private const float ScreenMargin = 8f; // px kept free above the bottom of the screen

        private RectTransform _panel, _bar;
        private float _baseHeight, _grown, _barBase;
        // Parts placed from the window's middle that were moved up: where they were put (if they are still there, they
        // are ours to move back; a mod that put one somewhere else since gets it moved from there).
        private readonly Dictionary<RectTransform, Vector2> _moved = new Dictionary<RectTransform, Vector2>();
        private readonly List<RectTransform> _gone = new List<RectTransform>();

        private void Awake() => PatchModule(typeof(Plugin));

        // Adventure Backpacks redraws the chest window with the player given (InventoryGrid.UpdateInventory(inventory,
        // player, ...), when switching between the Chest and Backpack tabs), and the game then writes the hotbar keys 1-8
        // over the first row, as on your own inventory. A chest's slots have no keys: those numbers go.
        [HarmonyLib.HarmonyPatch(typeof(InventoryGrid), nameof(InventoryGrid.UpdateInventory))]
        [HarmonyLib.HarmonyPostfix]
        private static void NoHotbarKeys(InventoryGrid __instance, Player player)
        {
            if (player == null || InventoryGui.instance == null || __instance != InventoryGui.instance.m_containerGrid) return;
            foreach (var element in __instance.m_elements)
            {
                var key = element != null ? element.transform.Find("binding") : null;
                var text = key != null ? key.GetComponent<TMPro.TMP_Text>() : null;
                if (text != null && text.enabled) text.enabled = false;
            }
        }

        private void LateUpdate()
        {
            var gui = InventoryGui.instance;
            // Sliding away after closing: it stays as it is until it is gone (back at once, the last row popped out with a
            // scroll bar while the window slid away).
            if (gui != null && InventoryGui.IsVisible() && DigiStorage.ChestCheckbox.Closing(gui)) return;
            var open = gui != null && InventoryGui.IsVisible() && gui.m_currentContainer != null && gui.m_container != null &&
                       gui.m_containerGrid != null && gui.m_containerGrid.m_gridRoot != null;
            if (!open)
            {
                Grow(0f);
                return;
            }
            if (_panel != gui.m_container)
            {
                Grow(0f);
                _panel = gui.m_container;
                _bar = gui.m_containerGrid.m_scrollbar != null ? gui.m_containerGrid.m_scrollbar.transform as RectTransform : null;
            }
            // What the slots need: rows x row height, from the chest itself. Not the slot area's own height: it is stretched
            // over the grid, so it grows with the window until the game sets it again, and the game skips that while
            // nothing in the chest changes (Lighter Inventory): read back, it grew the window a row every frame.
            _root = gui.m_containerGrid.m_gridRoot;
            _slots = gui.m_currentContainer.GetInventory().GetHeight() * gui.m_containerGrid.m_elementSpace;
            var view = ((RectTransform)gui.m_containerGrid.transform).rect.height - _grown;
            var wanted = Mathf.Max(0f, _slots - view + Spare);
            var room = wanted > 0f ? RoomBelow() : -1f;
            if (wanted > 0f) wanted = Mathf.Min(wanted, room);
            if (Mathf.Abs(wanted - _grown) > 0.5f) Grow(wanted);
            else if (_grown > 0f) Follow();
        }

        private RectTransform _root;
        private float _slots;

        // The slot area as the game makes it (rows x row height) for the window as it is now.
        private void FitSlots()
        {
            if (_root != null && _slots > 0f) _root.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, _slots);
        }

        // How far the window may grow before its bottom reaches the bottom of the screen, in its own units: measured from
        // where its bottom is without the growth (measured from the grown bottom, the room shrank as it grew: the window
        // jumped between two heights every frame on a screen where it reaches the bottom).
        private float RoomBelow()
        {
            var corners = new Vector3[4];
            _panel.GetWorldCorners(corners);
            var canvas = _panel.GetComponentInParent<Canvas>();
            var camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            var bottom = RectTransformUtility.WorldToScreenPoint(camera, corners[0]).y;
            var top = RectTransformUtility.WorldToScreenPoint(camera, corners[1]).y;
            var height = _panel.rect.height;
            if (height <= 1f || top - bottom <= 1f) return 0f;
            var pixelsPerUnit = (top - bottom) / height;
            return Mathf.Max(0f, (bottom - ScreenMargin) / pixelsPerUnit + _grown);
        }

        private void Grow(float by)
        {
            if (_panel == null)
            {
                _grown = 0f;
                return;
            }
            var before = _grown;
            if (before <= 0f && by <= 0f)
            {
                // Never grown: nothing to put back. (The base height is only measured when the window grows; written
                // back here after a chest or backpack that fit, it was still 0: the window was squashed flat for the
                // rest of the session, its buttons over the inventory. Alin's report, 2026-11-27.)
                Release();
                return;
            }
            if (before <= 0f)
            {
                _baseHeight = _panel.sizeDelta.y; // as it is without this (another mod may have changed it)
                _barBase = _bar != null ? _bar.sizeDelta.y : 0f;
            }
            _panel.sizeDelta = new Vector2(_panel.sizeDelta.x, _baseHeight + by);
            if (_bar != null) _bar.sizeDelta = new Vector2(_bar.sizeDelta.x, _barBase + by);
            _grown = by;
            FitSlots(); // the stretched slot area would otherwise grow and shrink with the window
            if (by > 0f)
            {
                Shift((by - before) * 0.5f);
                return;
            }
            // Back as it was: the parts still where this put them go down again; ones a mod has placed since stay.
            foreach (var pair in _moved)
                if (pair.Key != null && (pair.Key.anchoredPosition - pair.Value).sqrMagnitude < 0.01f)
                    pair.Key.anchoredPosition -= new Vector2(0f, before * 0.5f);
            Release();
        }

        private void Release()
        {
            _moved.Clear();
            _panel = null;
            _bar = null;
            _grown = 0f;
        }

        // The parts placed from the window's middle: the ones this moved before by the change, new ones by half the growth.
        private void Shift(float up)
        {
            foreach (Transform child in _panel)
            {
                var part = child as RectTransform;
                if (!Middle(part)) continue;
                var ours = _moved.TryGetValue(part, out var at) && (part.anchoredPosition - at).sqrMagnitude < 0.01f;
                part.anchoredPosition += new Vector2(0f, ours ? up : _grown * 0.5f);
                _moved[part] = part.anchoredPosition;
            }
        }

        // A part a mod put back exactly where it was before the window grew (it places it from a fixed spot every frame):
        // moved up again. A part a mod placed somewhere else (it lines it up with another part, as Crafting and
        // Automation's and Inventory and QoL's checkboxes do with Place Stacks, which moved up already) is that mod's: left
        // alone (moved up again, it ended up a second half higher, under Adventure Backpacks' tabs).
        private void Follow()
        {
            var up = _grown * 0.5f;
            _gone.Clear();
            foreach (var pair in _moved)
            {
                var part = pair.Key;
                if (part == null)
                {
                    _gone.Add(part);
                    continue;
                }
                var now = part.anchoredPosition;
                if ((now - pair.Value).sqrMagnitude < 0.01f) continue;
                if ((now - (pair.Value - new Vector2(0f, up))).sqrMagnitude < 0.01f) part.anchoredPosition = pair.Value;
                else _gone.Add(part);
            }
            foreach (var part in _gone) _moved.Remove(part);
        }

        // Placed from the window's middle (vertically), and not the scroll bar (it stays centred on the slots).
        private bool Middle(RectTransform part) =>
            part != null && part != _bar && Mathf.Approximately(part.anchorMin.y, 0.5f) && Mathf.Approximately(part.anchorMax.y, 0.5f);

        private void OnDestroy() => Grow(0f);
    }
}
