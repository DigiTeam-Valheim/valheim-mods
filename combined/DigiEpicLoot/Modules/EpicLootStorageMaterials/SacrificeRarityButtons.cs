// Epic Loot Storage Materials, rarity buttons in the Sacrifice tab, in the middle column: one button per rarity in that
// rarity's colour. A click selects every magic item of that rarity in the list (as Select All does, and only what the
// filter shows); when they all are selected already, it deselects them. Shardstones and trophies are not touched.
// Epic Loot 0.15.6 put its own "Selection" dropdown right under the red warning: the buttons go under that dropdown,
// as a "Select all:" heading and two columns of rarity names sized to the room above the table's level line. Older
// Epic Loot: one column right under the warning ("Select all Magic", ...). Hidden while identifying (that mode uses the
// middle). Light: only runs when a button is clicked or the tab opens.
// Above them an "Include set pieces" checkbox (a copy of Select All): off, the Sacrifice list leaves every set piece
// out (Plugin.SacrificeListPatch), so Select All and the rarity buttons never pick one.
// The checkboxes sit under an "Include:" heading: "Set pieces", then a row ★ / ★★ / ★★★ (Epic Loot's own enchantment
// stars, in its colours): an unticked one leaves out every item with an enchantment of that many stars, so the rare ones
// are never sacrificed by Select All. Default: 1 star in, 2 and 3 stars out.
using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using BepInEx.Logging;
using EpicLoot;
using EpicLoot_UnityLib;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EpicLootStorageMaterials
{
    internal static class SacrificeRarityButtons
    {
        private const string PanelName = "RarityButtons";
        private const string CategoryPanelName = "SacrificeCategoryPanel"; // Epic Loot 0.15.6's "Selection" dropdown
        private static readonly ItemRarity[] Rarities =
            { ItemRarity.Magic, ItemRarity.Rare, ItemRarity.Epic, ItemRarity.Legendary, ItemRarity.Mythic, ItemRarity.Ancient };

        private static ConfigEntry<bool> _enabled, _setPieces;
        private const string SetPiecesLabel = "Set pieces";
        private static readonly System.Reflection.MethodInfo Refresh = AccessTools.Method(typeof(SacrificeUI), "RefreshAvailableItems");

        internal static bool IncludeSetPieces => _setPieces == null || _setPieces.Value;
        private static readonly ConfigEntry<bool>[] Tiers = new ConfigEntry<bool>[4]; // 1..3 stars
        private static readonly string[] TierColors = { "", "#8FD8FF", "#C79BFF", "#FFD24A" }; // Epic Loot's MagicEffectRarity colours

        // Items with an enchantment of this many stars are left out of the Sacrifice list.
        internal static bool LeavesOut(int stars) => stars >= 1 && stars <= 3 && Tiers[stars] != null && !Tiers[stars].Value;
        internal static bool LeavesOutAny => LeavesOut(1) || LeavesOut(2) || LeavesOut(3);
        private static ManualLogSource _log;
        private static readonly AccessTools.FieldRef<EnchantingTableUIPanelBase, bool> Locked =
            AccessTools.FieldRefAccess<EnchantingTableUIPanelBase, bool>("_locked");

        internal static void Bind(DigiCore.ModuleConfig config)
        {
            _enabled = config.Bind("Sacrifice", "Select by rarity buttons", true,
                "Buttons in the middle of the Sacrifice tab, one per rarity (Magic, Rare, ...): each selects every magic item " +
                "of that rarity in the list (a second click deselects them).");
            _setPieces = config.Bind("Sacrifice", "Include set pieces", false,
                "The Sacrifice list also shows set pieces (Heimdall's, Thor's, ...). Off: they are left out, so Select All and the " +
                "rarity buttons never pick one. Same as the \"Include set pieces\" checkbox above the rarity buttons.");
            for (var stars = 1; stars <= 3; stars++)
                Tiers[stars] = config.Bind("Sacrifice", $"Include {stars}-star enchantments", stars == 1,
                    $"The Sacrifice list also shows items with an enchantment of {stars} star{(stars > 1 ? "s" : "")} (Epic Loot's rarity " +
                    "stars on an enchantment). Off: they are left out, so Select All and the rarity buttons never pick one. Same as " +
                    "the star checkboxes under \"Include set pieces\".");
        }

        internal static void Patch(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            harmony.Patch(AccessTools.Method(typeof(SacrificeUI), "OnEnable"), postfix: new HarmonyMethod(typeof(SacrificeRarityButtons), nameof(AddButtons)));
            harmony.Patch(AccessTools.Method(typeof(SacrificeUI), "SacrificeModeSelected"), postfix: new HarmonyMethod(typeof(SacrificeRarityButtons), nameof(ModeChanged)));
            harmony.Patch(AccessTools.Method(typeof(SacrificeUI), "IdentifyModeSelected"), postfix: new HarmonyMethod(typeof(SacrificeRarityButtons), nameof(ModeChanged)));
        }

        private static Transform Panel(SacrificeUI ui) => ui.Warning != null ? ui.Warning.transform.parent.Find(PanelName) : null;

        // Shown in sacrifice mode only (the tab opens in it).
        private static void ModeChanged(SacrificeUI __instance)
        {
            var panel = Panel(__instance);
            if (panel != null) panel.gameObject.SetActive(_enabled.Value && __instance.SacrificeToggle != null && __instance.SacrificeToggle.isOn);
        }

        private static void AddButtons(SacrificeUI __instance)
        {
            try
            {
                // Built fresh each time the tab opens (six buttons), so a changed setting or colour shows at once.
                var existing = Panel(__instance);
                if (existing != null) UnityEngine.Object.DestroyImmediate(existing.gameObject);
                if (!_enabled.Value || __instance.Warning == null || __instance.MainButton == null) return;
                var warning = __instance.Warning;
                var parent = (RectTransform)warning.transform.parent;

                var width = __instance.SacrificeToggle != null ? ((RectTransform)__instance.SacrificeToggle.transform).rect.width : 200f;
                if (width < 120f) width = 200f;
                warning.ForceMeshUpdate();
                var under = parent.InverseTransformPoint(warning.transform.TransformPoint(new Vector3(warning.textBounds.center.x, warning.textBounds.min.y, 0f)));

                var panel = new GameObject(PanelName, typeof(RectTransform));
                var rect = (RectTransform)panel.transform;
                rect.SetParent(parent, false);
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 1f);
                var category = parent.Find(CategoryPanelName) as RectTransform;
                var grid = category != null;
                var holder = rect;
                float height, fontSize;
                if (!grid)
                {
                    // A column right under the warning's text, as wide as the Sacrifice / Identify buttons.
                    height = 30f;
                    fontSize = 16f;
                    rect.sizeDelta = new Vector2(width, (Rarities.Length + 3) * (height + 4f));
                    rect.localPosition = new Vector3(under.x, under.y - 12f, 0f);
                    // Each goes first: so the order is "Include:", set pieces, stars, then the rarity buttons.
                    AddStarToggles(__instance, rect, 0f, width, height, true);
                    AddSetPieceToggle(__instance, rect, 0f, width, height, true);
                    if (__instance.AvailableItems?.SelectAllToggle != null) Heading(rect, warning, "Include:", 0f, height, true);
                    var layout = panel.AddComponent<VerticalLayoutGroup>();
                    layout.spacing = 4f;
                    layout.childControlWidth = layout.childControlHeight = true;
                    layout.childForceExpandWidth = true;
                    layout.childForceExpandHeight = false;
                }
                else
                {
                    // Under Epic Loot's dropdown, down to the table's level line (or the Sacrifice button).
                    const float headingHeight = 20f, gap = 4f, toggleHeight = 24f;
                    // "Include:", the set pieces box, the star boxes.
                    var toggleSpace = __instance.AvailableItems?.SelectAllToggle != null ? headingHeight + gap + 2f * (toggleHeight + gap) : 0f;
                    var top = Edge(parent, category, false, false) - 8f;
                    var floor = Edge(parent, (RectTransform)__instance.MainButton.transform, false, true) + 8f;
                    if (__instance.LevelDisplay != null && __instance.LevelDisplay.activeInHierarchy)
                        floor = Mathf.Max(floor, Edge(parent, (RectTransform)__instance.LevelDisplay.transform, true, true) + 6f);
                    var rows = (Rarities.Length + 1) / 2;
                    height = Mathf.Clamp((top - floor - toggleSpace - headingHeight - gap - (rows - 1) * gap) / rows, 22f, 30f);
                    fontSize = height >= 26f ? 16f : 14f;
                    rect.sizeDelta = new Vector2(width, toggleSpace + headingHeight + gap + rows * height + (rows - 1) * gap);
                    rect.localPosition = new Vector3(under.x, top, 0f);
                    if (toggleSpace > 0f)
                    {
                        Heading(rect, warning, "Include:", 0f, headingHeight, false);
                        AddSetPieceToggle(__instance, rect, -(headingHeight + gap), width, toggleHeight, false);
                        AddStarToggles(__instance, rect, -(headingHeight + gap + toggleHeight + gap), width, toggleHeight, false);
                    }

                    var heading = new GameObject("Heading", typeof(RectTransform), typeof(TextMeshProUGUI));
                    var headingRect = (RectTransform)heading.transform;
                    headingRect.SetParent(rect, false);
                    headingRect.anchorMin = new Vector2(0f, 1f);
                    headingRect.anchorMax = new Vector2(1f, 1f);
                    headingRect.pivot = new Vector2(0.5f, 1f);
                    headingRect.sizeDelta = new Vector2(0f, headingHeight);
                    headingRect.anchoredPosition = new Vector2(0f, -toggleSpace);
                    var headingText = heading.GetComponent<TextMeshProUGUI>();
                    headingText.font = warning.font;
                    headingText.fontSize = 16f;
                    headingText.alignment = TextAlignmentOptions.Center;
                    headingText.color = Color.white;
                    headingText.raycastTarget = false;
                    headingText.text = "Select all:";

                    var cells = new GameObject("Buttons", typeof(RectTransform), typeof(GridLayoutGroup));
                    holder = (RectTransform)cells.transform;
                    holder.SetParent(rect, false);
                    holder.anchorMin = Vector2.zero;
                    holder.anchorMax = Vector2.one;
                    holder.offsetMin = Vector2.zero;
                    holder.offsetMax = new Vector2(0f, -(toggleSpace + headingHeight + gap));
                    var layout = cells.GetComponent<GridLayoutGroup>();
                    layout.cellSize = new Vector2((width - gap) / 2f, height);
                    layout.spacing = new Vector2(gap, gap);
                    layout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
                    layout.constraintCount = 2;
                }

                foreach (var rarity in Rarities)
                {
                    var copy = UnityEngine.Object.Instantiate(__instance.MainButton.gameObject, holder, false);
                    copy.name = "Select" + rarity;
                    for (var i = 0; i < copy.transform.childCount; i++)
                    {
                        var child = copy.transform.GetChild(i);
                        if (child.name.StartsWith("Hint", StringComparison.Ordinal)) child.gameObject.SetActive(false); // gamepad hint
                    }
                    foreach (var pad in copy.GetComponentsInChildren<UIGamePad>(true)) UnityEngine.Object.Destroy(pad); // not the Sacrifice key
                    // Epic Loot's buttons reset their label to its first colour every frame; these keep theirs.
                    foreach (var keeper in copy.GetComponentsInChildren<SelectableTextColor>(true)) UnityEngine.Object.DestroyImmediate(keeper);
                    if (!grid)
                    {
                        var element = copy.GetComponent<LayoutElement>() ?? copy.AddComponent<LayoutElement>();
                        element.preferredHeight = element.minHeight = height;
                    }

                    // The rarity's colour as a fill inside the frame, under a white label.
                    var colour = EpicLoot.EpicLoot.GetRarityColorARGB(rarity);
                    var fill = new GameObject("RarityFill", typeof(RectTransform), typeof(Image));
                    var fillRect = (RectTransform)fill.transform;
                    fillRect.SetParent(copy.transform, false);
                    fillRect.SetSiblingIndex(0);
                    fillRect.anchorMin = Vector2.zero;
                    fillRect.anchorMax = Vector2.one;
                    fillRect.offsetMin = new Vector2(4f, 4f);
                    fillRect.offsetMax = new Vector2(-4f, -4f);
                    var fillImage = fill.GetComponent<Image>();
                    fillImage.color = new Color(colour.r, colour.g, colour.b, 0.55f);
                    fillImage.raycastTarget = false;

                    var name = EpicLoot.EpicLoot.GetRarityDisplayName(rarity);
                    if (Localization.instance != null) name = Localization.instance.Localize(name);
                    foreach (var text in copy.GetComponentsInChildren<TMP_Text>(true))
                    {
                        if (text.GetComponent<GamepadGlyph>() != null) continue;
                        text.text = grid ? name : "Select all " + name;
                        text.enableAutoSizing = false;
                        text.fontSize = fontSize;
                        text.color = Color.white;
                    }
                    var button = copy.GetComponent<Button>();
                    button.onClick = new Button.ButtonClickedEvent();
                    button.interactable = true;
                    var ui = __instance;
                    var which = rarity;
                    button.onClick.AddListener(() => Select(ui, which));
                }
                ModeChanged(__instance);
            }
            catch (Exception e)
            {
                _log?.LogWarning("Could not add the rarity buttons to the Sacrifice tab: " + e.Message);
            }
        }

        // 2 and 3 stars off once for everyone (Epic Loot Add-ons 1.0.19 had them on for a short while).
        internal static void StarDefaults()
        {
            if (Tiers[2] != null) Tiers[2].Value = false;
            if (Tiers[3] != null) Tiers[3].Value = false;
        }

        // A small heading in the panel ("Include:"), in the warning's font.
        private static void Heading(RectTransform panel, TMP_Text like, string text, float y, float height, bool inLayout)
        {
            var go = new GameObject("IncludeHeading", typeof(RectTransform), typeof(TextMeshProUGUI));
            var rect = (RectTransform)go.transform;
            rect.SetParent(panel, false);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.sizeDelta = new Vector2(0f, height);
            rect.anchoredPosition = new Vector2(0f, y);
            if (inLayout)
            {
                go.transform.SetAsFirstSibling();
                var element = go.AddComponent<LayoutElement>();
                element.preferredHeight = element.minHeight = height;
            }
            var label = go.GetComponent<TextMeshProUGUI>();
            label.font = like.font;
            label.fontSize = 16f;
            label.alignment = TextAlignmentOptions.Left;
            label.color = Color.white;
            label.raycastTarget = false;
            label.text = text;
        }

        // "Set pieces": a copy of Epic Loot's Select All checkbox under "Include:", bound to the setting.
        private static void AddSetPieceToggle(SacrificeUI ui, RectTransform panel, float y, float width, float height, bool inLayout)
        {
            var selectAll = ui.AvailableItems?.SelectAllToggle;
            if (selectAll == null) return;
            var copy = UnityEngine.Object.Instantiate(selectAll.gameObject, panel, false);
            copy.name = "IncludeSetPiecesToggle";
            var rect = (RectTransform)copy.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(0f, y);
            rect.sizeDelta = new Vector2(width, height);
            if (inLayout)
            {
                copy.transform.SetAsFirstSibling();
                var element = copy.GetComponent<LayoutElement>() ?? copy.AddComponent<LayoutElement>();
                element.preferredHeight = element.minHeight = height;
            }
            foreach (var text in copy.GetComponentsInChildren<Text>(true)) text.text = SetPiecesLabel;
            foreach (var text in copy.GetComponentsInChildren<TMP_Text>(true)) text.text = SetPiecesLabel;
            var toggle = copy.GetComponent<Toggle>();
            toggle.onValueChanged = new Toggle.ToggleEvent();
            toggle.SetIsOnWithoutNotify(_setPieces.Value);
            toggle.interactable = true;
            toggle.onValueChanged.AddListener(on =>
            {
                _setPieces.Value = on;
                Refresh?.Invoke(ui, null);
            });
        }

        // ★ / ★★ / ★★★ under "Include:": three copies of Select All's checkbox in one row, each bound to its setting.
        private static void AddStarToggles(SacrificeUI ui, RectTransform panel, float y, float width, float height, bool inLayout)
        {
            var selectAll = ui.AvailableItems?.SelectAllToggle;
            if (selectAll == null) return;
            var row = new GameObject("StarToggles", typeof(RectTransform));
            var rowRect = (RectTransform)row.transform;
            rowRect.SetParent(panel, false);
            rowRect.anchorMin = rowRect.anchorMax = new Vector2(0f, 1f);
            rowRect.pivot = new Vector2(0f, 1f);
            rowRect.anchoredPosition = new Vector2(0f, y);
            rowRect.sizeDelta = new Vector2(width, height);
            if (inLayout)
            {
                row.transform.SetAsFirstSibling();
                var element = row.AddComponent<LayoutElement>();
                element.preferredHeight = element.minHeight = height;
            }
            var glyph = Glyph();
            float[] widths = { 0.28f, 0.33f, 0.39f };
            var x = 0f;
            for (var stars = 1; stars <= 3; stars++)
            {
                var entry = Tiers[stars];
                var marks = $"<color={TierColors[stars]}>{new string(glyph, stars)}</color>";
                var label = marks;
                var copy = UnityEngine.Object.Instantiate(selectAll.gameObject, rowRect, false);
                copy.name = $"Include{stars}StarToggle";
                var rect = (RectTransform)copy.transform;
                rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
                rect.pivot = new Vector2(0f, 1f);
                rect.anchoredPosition = new Vector2(x, 0f);
                rect.sizeDelta = new Vector2(width * widths[stars - 1], height);
                x += width * widths[stars - 1];
                foreach (var text in copy.GetComponentsInChildren<Text>(true)) { text.supportRichText = true; text.text = label; }
                foreach (var text in copy.GetComponentsInChildren<TMP_Text>(true)) { text.richText = true; text.text = label; }
                var toggle = copy.GetComponent<Toggle>();
                toggle.onValueChanged = new Toggle.ToggleEvent();
                toggle.SetIsOnWithoutNotify(entry.Value);
                toggle.interactable = true;
                toggle.onValueChanged.AddListener(on =>
                {
                    entry.Value = on;
                    Refresh?.Invoke(ui, null);
                });
            }
        }

        // The mark Epic Loot puts on an enchantment's stars (★ when its fonts have it).
        private static char Glyph()
        {
            try
            {
                AccessTools.Method(typeof(MagicEffectRarity), "ResolveGlyph")?.Invoke(null, null);
                if (AccessTools.Field(typeof(MagicEffectRarity), "_tierGlyph")?.GetValue(null) is char c) return c;
            }
            catch { }
            return '★';
        }

        // The top or bottom edge of a rect in the parent's local space; with glyphs, of the text's letters in it.
        private static float Edge(RectTransform parent, RectTransform target, bool glyphs, bool top)
        {
            if (glyphs)
            {
                var text = target.GetComponentInChildren<TMP_Text>();
                if (text != null && text.gameObject.activeInHierarchy && !string.IsNullOrEmpty(text.text))
                {
                    text.ForceMeshUpdate();
                    var bounds = text.textBounds;
                    var point = text.transform.TransformPoint(new Vector3(bounds.center.x, top ? bounds.max.y : bounds.min.y, 0f));
                    return parent.InverseTransformPoint(point).y;
                }
            }
            var corners = new Vector3[4];
            target.GetWorldCorners(corners);
            var a = parent.InverseTransformPoint(corners[0]).y;
            var b = parent.InverseTransformPoint(corners[1]).y;
            return top ? Mathf.Max(a, b) : Mathf.Min(a, b);
        }

        private static void Select(SacrificeUI ui, ItemRarity rarity)
        {
            var list = ui.AvailableItems;
            if (list == null || Locked(ui)) return;
            var matching = new List<MultiSelectItemListElement>();
            list.ForeachVisibleElement((_, element) =>
            {
                var item = element.GetItem();
                if (item != null && item.IsMagic(out var magic) && magic.Rarity == rarity) matching.Add(element);
            });
            if (matching.Count == 0) return;
            var deselect = matching.All(e => e.IsMaxSelected());
            foreach (var element in matching)
            {
                if (deselect) element.Deselect(true);
                else element.SelectMaxQuantity(true);
            }
            list.RefreshSelectAllToggle();
            if (ui.Audio != null && ui.MainActionSFX != null) ui.Audio.PlayOneShot(ui.MainActionSFX);
        }
    }
}
