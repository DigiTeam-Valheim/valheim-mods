// Sell To Haldor: Haldor also buys your food, feasts and every kind of hide and leather. His Sell button sells the first stack
// he takes: valuables first as in vanilla, then hides, then food. Only from the bag: not the hotbar, not the extra slots
// other mods add (they sit outside the inventory's own size; bigger pockets do count as bag), nothing equipped. Two boxes in his window ("Food", "Hides") switch each on or off. Prices: food
// (health + stamina + eitr) / 10 coins each (times a slider), a feast that per serving; hides from a list; hides the list does not know (from
// other mods) get one price. Only Haldor: Hildir and the Bog Witch keep their own rules.
// Light: only runs while Haldor's window is open, when it checks what the Sell button would sell.
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace SellToHaldor
{
    public class Plugin : DigiCore.DigiModule
    {
        private static ConfigEntry<bool> _sellFood, _sellHides;
        private static ConfigEntry<float> _foodPrice;
        private static ConfigEntry<string> _hidePrices;
        private static ConfigEntry<int> _otherHide;
        private static readonly Dictionary<string, int> HidePrices = new Dictionary<string, int>();
        private const string OldHideDefault =
            "LeatherScraps:1, DeerHide:2, BjornHide:5, TrollHide:5, WolfPelt:5, LoxPelt:10, ScaleHide:8, AskHide:12";
        private const string HideDefault = OldHideDefault + ", MooseHide:15, SealHide:15";

        private static readonly AccessTools.FieldRef<StoreGui, Trader> CurrentTrader = AccessTools.FieldRefAccess<StoreGui, Trader>("m_trader");
        private static readonly System.Reflection.MethodInfo FillList = AccessTools.Method(typeof(StoreGui), "FillList");
        private static readonly List<ItemDrop.ItemData> Valuables = new List<ItemDrop.ItemData>();

        private sealed class Box
        {
            public RectTransform Rect;
            public Toggle Toggle;
            public ConfigEntry<bool> Setting;
        }

        private static readonly List<Box> Boxes = new List<Box>();
        private static Sprite _tick;
        private static bool _updating;

        private void Awake()
        {
            _sellFood = Config.Bind("General", "Sell food", true,
                "Haldor buys the food and feasts in your bag (not the hotbar, not extra slots, nothing equipped). The \"Food\" box in his " +
                "window switches this too.");
            _foodPrice = Config.Bind("General", "Food price", 1f, new ConfigDescription(
                "Coins per 10 points of food (health + stamina + eitr): 1 = a cooked meat (50 health, 15 stamina) sells for " +
                "6 coins. A feast counts all its servings: 10 of them. At least 1 coin each.", new AcceptableValueRange<float>(0.1f, 10f)));
            _sellHides = Config.Bind("General", "Sell hides", true,
                "Haldor buys every kind of hide, pelt and leather (from your bag). The \"Hides\" box in his window switches this too.");
            _hidePrices = Config.Bind("General", "Hide prices", HideDefault, "Coins each, as Prefab:coins, separated by commas.");
            // The Deep North's hides were missing (they sold as other hides): a list nobody changed gets them.
            if (_hidePrices.Value == OldHideDefault) _hidePrices.Value = HideDefault;
            _otherHide = Config.Bind("General", "Other hides", 3, new ConfigDescription(
                "Coins each for hides, pelts and leather the list does not know (from other mods).", new AcceptableValueRange<int>(1, 100)));
            _hidePrices.SettingChanged += (_, __) => ReadPrices();
            _sellFood.SettingChanged += (_, __) => SyncToggles();
            _sellHides.SettingChanged += (_, __) => SyncToggles();
            ReadPrices();
            PatchModule(typeof(Plugin));
        }

        private void OnDestroy()
        {
            foreach (var box in Boxes)
                if (box.Rect != null) Destroy(box.Rect.gameObject);
            Boxes.Clear();
        }

        private static void ReadPrices()
        {
            HidePrices.Clear();
            foreach (var part in _hidePrices.Value.Split(','))
            {
                var pair = part.Split(':');
                if (pair.Length == 2 && int.TryParse(pair[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var coins) && coins > 0)
                    HidePrices[pair[0].Trim()] = coins;
            }
        }

        private static bool AtHaldor(StoreGui gui)
        {
            var trader = gui != null ? CurrentTrader(gui) : null;
            return trader != null && trader.m_name == "$npc_haldor";
        }

        // --- What he buys, and for how much ---------------------------------------------------------------------------

        private static int Price(ItemDrop.ItemData item)
        {
            var shared = item.m_shared;
            if (_sellHides.Value && shared.m_itemType == ItemDrop.ItemData.ItemType.Material && item.m_dropPrefab != null)
            {
                var name = item.m_dropPrefab.name;
                if (HidePrices.TryGetValue(name, out var coins)) return coins;
                var lower = name.ToLowerInvariant();
                if (lower.Contains("hide") || lower.Contains("pelt") || lower.Contains("leather")) return _otherHide.Value;
            }
            if (!_sellFood.Value) return 0;
            if (shared.m_itemType == ItemDrop.ItemData.ItemType.Consumable) return FoodPrice(shared, 1);
            var feast = FeastOf(shared);
            return feast != null ? FoodPrice(shared.m_appendToolTip.m_itemData.m_shared, feast.m_eatStacks) : 0;
        }

        private static int FoodPrice(ItemDrop.ItemData.SharedData food, int servings)
        {
            var points = food.m_food + food.m_foodStamina + food.m_foodEitr;
            return points > 0f ? Mathf.Max(1, Mathf.RoundToInt(points / 10f * _foodPrice.Value * servings)) : 0;
        }

        // A feast in the bag is a material whose tooltip shows the placed feast. Raw food also shows what it turns into,
        // but that is no Feast.
        private static Feast FeastOf(ItemDrop.ItemData.SharedData shared) =>
            shared.m_itemType == ItemDrop.ItemData.ItemType.Material && shared.m_appendToolTip != null ? shared.m_appendToolTip.GetComponent<Feast>() : null;

        // Only the bag: not the hotbar (row 1), not the extra slots other mods put outside the inventory's own size
        // (bigger pockets make that size bigger, so their rows count as bag), nothing equipped.
        private static bool InBag(Inventory inventory, ItemDrop.ItemData item) =>
            item.m_gridPos.x >= 0 && item.m_gridPos.x < inventory.GetWidth() && item.m_gridPos.y >= 1 &&
            item.m_gridPos.y < inventory.GetHeight() && !item.m_equipped && !item.m_shared.m_questItem;

        // Hides first, then food (in bag order).
        private static ItemDrop.ItemData FindExtra(Inventory inventory)
        {
            ItemDrop.ItemData food = null;
            foreach (var item in inventory.GetAllItems())
            {
                if (item?.m_shared == null || item.m_shared.m_value > 0 || !InBag(inventory, item) || Price(item) <= 0) continue;
                if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Material && FeastOf(item.m_shared) == null) return item;
                if (food == null) food = item;
            }
            return food;
        }

        private static bool HasValuables(StoreGui gui, Inventory inventory)
        {
            Valuables.Clear();
            inventory.GetValuableItems(Valuables);
            foreach (var item in Valuables)
                if (item.m_shared.m_name != gui.m_coinPrefab.m_itemData.m_shared.m_name) return true;
            return false;
        }

        // The Sell button lights up when there is something he buys.
        [HarmonyPatch(typeof(StoreGui), "GetSellableItem")]
        [HarmonyPostfix]
        private static void Sellable(StoreGui __instance, ref ItemDrop.ItemData __result)
        {
            if (__result != null || Player.m_localPlayer == null || !AtHaldor(__instance)) return;
            __result = FindExtra(Player.m_localPlayer.GetInventory());
        }

        // Selling: valuables as in vanilla; otherwise the next hide or food stack, at our price.
        [HarmonyPatch(typeof(StoreGui), "SellItem")]
        [HarmonyPrefix]
        private static bool Sell(StoreGui __instance)
        {
            var player = Player.m_localPlayer;
            if (player == null || !AtHaldor(__instance)) return true;
            var inventory = player.GetInventory();
            if (HasValuables(__instance, inventory)) return true;
            var item = FindExtra(inventory);
            if (item == null) return true;
            var coins = Price(item) * item.m_stack;
            var cheated = inventory.ItemCheated(item.m_shared.m_name);
            inventory.RemoveItem(item);
            var coin = __instance.m_coinPrefab;
            inventory.AddItem(coin.gameObject.name, coins, coin.m_itemData.m_quality, coin.m_itemData.m_variant, 0L, "", cheated);
            var text = item.m_stack <= 1 ? item.m_shared.m_name : item.m_stack + "x" + item.m_shared.m_name;
            __instance.m_sellEffects.Create(__instance.transform.position, Quaternion.identity);
            player.Message(MessageHud.MessageType.TopLeft, Localization.instance.Localize("$msg_sold", text, coins.ToString()), 0,
                item.m_shared.m_icons.Length > 0 ? item.m_shared.m_icons[0] : null);
            CurrentTrader(__instance).OnSold();
            FillList.Invoke(__instance, null);
            return false;
        }

        // --- The "Food" and "Hides" boxes in Haldor's window ---------------------------------------------------------

        [HarmonyPatch(typeof(StoreGui), nameof(StoreGui.Show))]
        [HarmonyPostfix]
        private static void Shown(StoreGui __instance)
        {
            Boxes.RemoveAll(b => b.Rect == null);
            var show = AtHaldor(__instance);
            if (show && Boxes.Count == 0) Create(__instance);
            foreach (var box in Boxes) box.Rect.gameObject.SetActive(show);
            SyncToggles();
        }

        private static void SyncToggles()
        {
            _updating = true;
            foreach (var box in Boxes)
                if (box.Toggle != null) box.Toggle.isOn = box.Setting.Value;
            _updating = false;
        }

        // Left of the Sell button: [x] Food  [x] Hides  [Sell]; the same look as the other Digi checkboxes.
        private static void Create(StoreGui gui)
        {
            if (gui.m_sellButton == null) return;
            AddBox(gui, "Food", 168f, _sellFood, "Sell food",
                "Ticked: the Sell button also sells the food and feasts in your bag (after valuables and hides). Your hotbar, extra " +
                "slots and equipped items are never sold.");
            AddBox(gui, "Hides", 76f, _sellHides, "Sell hides",
                "Ticked: the Sell button also sells the hides, pelts and leather in your bag (after valuables). Your " +
                "hotbar, extra slots and equipped items are never sold.");
        }

        private static void AddBox(StoreGui gui, string label, float leftOfButton, ConfigEntry<bool> setting, string title, string tip)
        {
            var button = (RectTransform)gui.m_sellButton.transform;
            var go = new GameObject("DigiSell" + label + "Checkbox", typeof(RectTransform));
            go.transform.SetParent(button.parent, false);
            var rect = (RectTransform)go.transform;
            const float size = 26f;
            rect.anchorMin = rect.anchorMax = button.anchorMin;
            rect.pivot = new Vector2(1f, 0.5f);
            rect.sizeDelta = new Vector2(size, size);
            var corners = new Vector3[4];
            button.GetWorldCorners(corners); // 0 = bottom left, 1 = top left
            var leftMiddle = button.parent.InverseTransformPoint((corners[0] + corners[1]) * 0.5f);
            rect.localPosition = new Vector3(leftMiddle.x - leftOfButton, leftMiddle.y, 0f);

            var frame = new GameObject("Background", typeof(RectTransform));
            frame.transform.SetParent(rect, false);
            var frameRect = (RectTransform)frame.transform;
            frameRect.anchorMin = Vector2.zero;
            frameRect.anchorMax = Vector2.one;
            frameRect.offsetMin = frameRect.offsetMax = Vector2.zero;
            var frameImage = frame.AddComponent<Image>();
            var slot = SlotSprite();
            if (slot != null)
            {
                frameImage.sprite = slot;
                frameImage.type = Image.Type.Sliced;
            }
            frameImage.color = new Color(0f, 0f, 0f, 0.5f);

            var buttonText = gui.m_sellButton.GetComponentInChildren<TMPro.TMP_Text>(true);
            var orange = buttonText != null ? buttonText.color : new Color(1f, 0.63f, 0.24f, 1f);
            var mark = new GameObject("Checkmark", typeof(RectTransform));
            mark.transform.SetParent(frameRect, false);
            var markRect = (RectTransform)mark.transform;
            markRect.anchorMin = Vector2.zero;
            markRect.anchorMax = Vector2.one;
            markRect.offsetMin = new Vector2(-4f, -4f);
            markRect.offsetMax = new Vector2(4f, 4f);
            var markImage = mark.AddComponent<Image>();
            markImage.sprite = TickSprite();
            markImage.color = orange;
            markImage.raycastTarget = false;

            if (buttonText != null)
            {
                var text = new GameObject("Label", typeof(RectTransform));
                text.transform.SetParent(rect, false);
                var textRect = (RectTransform)text.transform;
                textRect.anchorMin = new Vector2(1f, 0f);
                textRect.anchorMax = new Vector2(1f, 1f);
                textRect.pivot = new Vector2(0f, 0.5f);
                textRect.anchoredPosition = new Vector2(6f, 0f);
                textRect.sizeDelta = new Vector2(60f, 0f);
                var tmp = text.AddComponent<TMPro.TextMeshProUGUI>();
                tmp.font = buttonText.font;
                tmp.fontSize = 18f;
                tmp.color = orange;
                tmp.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
                tmp.text = label;
                tmp.raycastTarget = false;
            }

            var toggle = go.AddComponent<Toggle>();
            toggle.targetGraphic = frameImage;
            toggle.graphic = markImage;
            toggle.isOn = setting.Value;
            toggle.onValueChanged.AddListener(on =>
            {
                if (!_updating) setting.Value = on;
            });

            var tooltip = go.AddComponent<UITooltip>();
            tooltip.m_tooltipPrefab = InventoryGui.instance?.m_playerGrid?.m_elementPrefab?.GetComponent<UITooltip>()?.m_tooltipPrefab;
            tooltip.Set(title, tip);
            Boxes.Add(new Box { Rect = rect, Toggle = toggle, Setting = setting });
        }

        private static Sprite SlotSprite()
        {
            var element = InventoryGui.instance?.m_playerGrid?.m_elementPrefab;
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
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "DigiSellTick", filterMode = FilterMode.Bilinear };
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
