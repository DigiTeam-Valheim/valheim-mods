// The crafting window and the build panel show what your bag and the chests hold together: a green "+24" right above the
// amount needed (this was the Craft Storage Count sub-mod, which read Local Craft From Storage's numbers). The chests'
// part is a lookup in the totals (DigiStorage.Chests), and a label is only touched when its number changes, so an open
// window costs next to nothing. Not shown in the Recycle tab (there the amounts are what you get back) or for a recipe
// that needs only one of its ingredients.
using System.Collections.Generic;
using DigiStorage;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace CraftFromChests
{
    internal static class ChestCounts
    {
        private const string LabelName = "digi_storage_count";
        private static readonly Color Green = new Color(0.45f, 0.95f, 0.35f);

        private sealed class Label
        {
            public TMP_Text Text;
            public int Shown = -1;
        }

        private static readonly Dictionary<Transform, Label> Labels = new Dictionary<Transform, Label>();
        private static bool _inBuildPanel;

        internal static void Clear()
        {
            foreach (var label in Labels.Values)
                if (label.Text != null) Object.Destroy(label.Text.gameObject);
            Labels.Clear();
        }

        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.SetupRequirement))]
        [HarmonyPostfix]
        private static void Requirement(Transform elementRoot, Piece.Requirement req, Player player, bool craft, bool __result)
        {
            if (!craft || _inBuildPanel || elementRoot == null) return;
            var gui = InventoryGui.instance;
            var single = gui != null && gui.m_selectedRecipe.Recipe != null && gui.m_selectedRecipe.Recipe.m_requireOnlyOneIngredient;
            var shown = __result && req?.m_resItem != null && player == Player.m_localPlayer && Plugin.ShowCounts.Value &&
                        Plugin.Crafting.Value && !Linking.Recycling && !single;
            Set(elementRoot, shown ? CraftCount(player, req.m_resItem) : 0);
        }

        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.HideRequirement))]
        [HarmonyPostfix]
        private static void Hidden(Transform elementRoot)
        {
            if (elementRoot != null) Set(elementRoot, 0);
        }

        // The hammer's panel shows the piece's materials, then its workbench in the next cell: that cell and unused ones
        // must not keep a number from the piece shown before.
        [HarmonyPatch(typeof(Hud), "SetupPieceInfo")]
        [HarmonyPrefix]
        private static void BuildPanelStart() => _inBuildPanel = true;

        [HarmonyPatch(typeof(Hud), "SetupPieceInfo")]
        [HarmonyPostfix]
        private static void BuildPanel(Hud __instance, Piece piece)
        {
            var player = Player.m_localPlayer;
            var cells = __instance.m_requirementItems;
            var resources = piece != null ? piece.m_resources : null;
            var on = Plugin.ShowCounts.Value && Plugin.Building.Value && player != null;
            for (var i = 0; cells != null && i < cells.Length; i++)
            {
                if (cells[i] == null) continue;
                var item = on && resources != null && i < resources.Length ? resources[i]?.m_resItem : null;
                Set(cells[i].transform, item != null ? BuildCount(player, item) : 0);
            }
        }

        [HarmonyPatch(typeof(Hud), "SetupPieceInfo")]
        [HarmonyFinalizer]
        private static void BuildPanelEnd() => _inBuildPanel = false;

        // Bag and chests, of the quality you have most of (like the game's own check: mixed qualities do not add up).
        private static int CraftCount(Player player, ItemDrop item)
        {
            var shared = item.m_itemData.m_shared;
            var area = Plugin.Here();
            var best = 0;
            for (var quality = 1; quality <= shared.m_maxQuality; quality++)
                best = Mathf.Max(best, Plugin.Own(player.m_inventory, shared.m_name, quality) + Chests.Count(area, shared.m_name, quality));
            return best;
        }

        // Any quality, like the game's own check for building.
        private static int BuildCount(Player player, ItemDrop item)
        {
            var name = item.m_itemData.m_shared.m_name;
            return Plugin.Own(player.m_inventory, name) + Chests.Count(Plugin.Here(), name);
        }

        private static void Set(Transform root, int count)
        {
            if (!Labels.TryGetValue(root, out var label) || label.Text == null)
            {
                if (count <= 0) return;
                var text = Create(root);
                if (text == null) return;
                label = new Label { Text = text };
                Labels[root] = label;
            }
            if (label.Shown == count) return;
            label.Shown = count;
            label.Text.text = count > 0 ? "+" + count : "";
            if (label.Text.gameObject.activeSelf != count > 0) label.Text.gameObject.SetActive(count > 0);
        }

        // A copy of the amount's text (same font and outline), right above it: sized from the amount, as the crafting
        // window's and the build panel's cells differ. A label left by the old Craft Storage Count is reused.
        private static TMP_Text Create(Transform root)
        {
            var amountObject = root.Find("res_amount");
            var amount = amountObject != null ? amountObject.GetComponent<TMP_Text>() : null;
            if (amount == null) return null;
            var old = amount.transform.parent.Find(LabelName);
            if (old != null) Object.Destroy(old.gameObject);
            var copy = Object.Instantiate(amount, amount.transform.parent);
            copy.name = LabelName;
            copy.raycastTarget = false;
            copy.color = Green;
            copy.enableAutoSizing = true;
            var size = amount.enableAutoSizing ? amount.fontSizeMax : amount.fontSize;
            copy.fontSizeMin = size * 0.47f;
            copy.fontSizeMax = size * 0.87f;
            copy.alignment = TextAlignmentOptions.Bottom;
            copy.text = "";
            var box = amount.rectTransform.sizeDelta;
            copy.rectTransform.sizeDelta = new Vector2(box.x * 1.26f, box.y * 0.8f);
            copy.rectTransform.anchoredPosition = amount.rectTransform.anchoredPosition + new Vector2(0f, box.y * 0.65f);
            return copy;
        }
    }
}
