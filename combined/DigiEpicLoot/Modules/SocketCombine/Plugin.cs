// Socket Combine: a Combine tab at Epic Loot's enchanting table (on the right side of the window). Two of the same item
// (the same kind and rarity, both carried; the one given up has at least as many shard sockets as the one kept, e.g. a
// 4-socket one gives a 1-socket one its second): the one you give up is used and the one you keep gets one more shard
// socket, up to the most its rarity can roll (Epic Loot's own socket table, by default Magic and Rare 1, Epic and
// Legendary 2, Mythic 3, Ancient 4; Brokkr's Gift stops there too). An item already there is listed only as one to
// give up, once one to keep is chosen. Hovering a row shows the item's tooltip (Epic Loot's, with its enchantments).
// The one given up must have no shards in its sockets (take them out first) and must not be worn; the one kept keeps
// everything it has. The cost, with N the socket count after combining: N x "Coins per socket", and N x "Essence per
// socket" and N x "Reagent per socket" and "Runestones" of the item's own rarity (e.g. Ancient 2 -> 3: 3,000 coins, 30
// Ancient Essence, 30 Ancient Reagent, 5 Ancient Runestones). They are taken like Epic Loot's own costs: the inventory,
// the table's Storage and the chests Epic Loot Storage Materials adds.
// Epic Loot rules used: MagicItem.SocketCount (open sockets) and Sockets (the shards in them), saved with SaveMagicItem
// (as Brokkr's Gift does); LootRoller.GetMaxSocketCountForRarity; GatedItemTypeHelper.IsSocketable. The tab is a copy of
// one of Epic Loot's own tab buttons, added to its TabHandler with a page of its own; its name is not one of Epic Loot's
// tab names, so Epic Loot's tab switches (EnchantingUIController.TabActivation) leave it alone.
// The window's wooden frame is made wider on the right so the tab sits inside it, mirrored to Epic Loot's tab column
// (the frame's content keeps its place); switched off (F1 "0 - Sub-mods"), the frame, the tab and its page are as before.
// Light: nothing runs while the table is closed; the page is drawn when it opens and after a combine.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using DigiShared;
using EpicLoot;
using EpicLoot.GatedItemType;
using EpicLoot.ShardStones;
using EpicLoot_UnityLib;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace SocketCombine
{
    public class Plugin : DigiCore.DigiModule
    {
        private const string TabName = "DigiSocketCombine";
        private const float RowHeight = 54f;
        private static readonly Color Green = new Color(0.5f, 0.88f, 0.42f);
        private static readonly Color Red = new Color(1f, 0.45f, 0.38f);

        private static ConfigEntry<int> _coins, _essence, _reagent, _runestones;
        private static BepInEx.Logging.ManualLogSource _log;
        private static readonly FieldInfo SetUp = AccessTools.Field(typeof(EnchantingTableUI), "_setUp");
        private static readonly MethodInfo WithChangeReason = AccessTools.Method(typeof(API), "WithChangeReason");

        private static EnchantingTableUI _ui;
        private static TabHandler.Tab _tab;
        private static GameObject _button, _page;
        private static bool _wasShown, _column;
        private static EnchantingTableUI _seenUi;
        private static float _readySince = -1f;
        private const string LayoutMarker = "DigiEnchantingLayout"; // Enchanting Layout is on: join its tab columns
        private static ItemDrop.ItemData _keep, _give;
        private static bool _sure;

        // One cost line: an item (by its prefab), how many, what you have.
        private sealed class Cost
        {
            public string Prefab, Name;
            public Sprite Icon;
            public int Need, Have;
        }

        private void Awake()
        {
            _log = Logger;
            _coins = Config.Bind("General", "Coins per socket", 1000, new ConfigDescription(
                "Coins a combine costs for each socket the kept item has afterwards (3 sockets: three times this).",
                new AcceptableValueRange<int>(0, 100000)));
            _essence = Config.Bind("General", "Essence per socket", 10, new ConfigDescription(
                "Essence of the item's rarity (Ancient Essence for an Ancient item) for each socket the kept item has afterwards.",
                new AcceptableValueRange<int>(0, 500)));
            _reagent = Config.Bind("General", "Reagent per socket", 10, new ConfigDescription(
                "Reagent of the item's rarity for each socket the kept item has afterwards.",
                new AcceptableValueRange<int>(0, 500)));
            _runestones = Config.Bind("General", "Runestones", 5, new ConfigDescription(
                "Runestones of the item's rarity a combine costs (not per socket).", new AcceptableValueRange<int>(0, 50)));
        }

        private void Update()
        {
            var ui = EnchantingTableUI.instance;
            if (ui == null || ui.Root == null) return;
            // A new table window (each world makes its own): add the tab once Epic Loot has set up its own tabs.
            if (ui != _seenUi)
            {
                _seenUi = ui;
                _readySince = -1f;
            }
            if (SetUp != null && !(SetUp.GetValue(ui) is bool ready && ready)) return;
            if (_readySince < 0f) _readySince = Time.unscaledTime;
            // With Enchanting Layout the tab is one of its columns' (it lays the window out a moment after it is ready:
            // wait a second for it); without, the tab hangs on the right in a wider frame.
            var column = ui.Root.transform.Find(LayoutMarker) != null;
            if (!column && Time.unscaledTime - _readySince < 1f) return;
            if (ui != _ui || column != _column)
            {
                Remove();
                _ui = ui;
                _column = column;
                try { Build(ui, column); }
                catch (Exception e)
                {
                    _log.LogError("Adding the Combine tab failed: " + e);
                    Remove();
                    _ui = ui; // not again for this window
                }
            }
            var shown = _page != null && _page.activeInHierarchy;
            if (shown && !_wasShown)
            {
                _keep = _give = null;
                Draw();
            }
            _wasShown = shown;
        }

        private void OnDestroy() => Remove();

        // --- the tab -------------------------------------------------------------------------------------------------

        private static void Build(EnchantingTableUI ui, bool column)
        {
            var handler = ui.TabHandler;
            var template = handler != null ? handler.m_tabs.Select(t => t.m_button).LastOrDefault(b => b != null) : null;
            var panel = ui.Panels != null ? ui.Panels.FirstOrDefault(p => p != null) : null;
            if (template == null || panel == null)
            {
                _log.LogWarning("Epic Loot's table window has no tab or panel to copy: no Combine tab.");
                return;
            }
            Windows.UseGameFont();
            var first = handler.m_tabs.Select(t => t.m_button).FirstOrDefault(b => b != null) ?? template;
            var panelRect = (RectTransform)panel.transform;

            // The wooden frame grown to the right by as much as it reaches left of the panels (where Epic Loot's tab
            // column is), so the Combine tab sits inside it like the tabs on the left.
            var frame = column ? null : Frame(ui, (RectTransform)first.transform, panelRect);
            if (frame != null)
            {
                var frameCorners = Corners(frame);
                var panelCorners = Corners(panelRect);
                var grow = panelCorners[2].x + (panelCorners[0].x - frameCorners[0].x) - frameCorners[2].x;
                if (grow > 1f) Grow(frame, grow / Mathf.Max(0.0001f, frame.lossyScale.x));
            }

            // The page: where Epic Loot's own panels are, the same size.
            _page = new GameObject(TabName + "Page", typeof(RectTransform));
            var page = (RectTransform)_page.transform;
            page.SetParent(panelRect.parent, false);
            page.anchorMin = panelRect.anchorMin;
            page.anchorMax = panelRect.anchorMax;
            page.pivot = panelRect.pivot;
            page.anchoredPosition = panelRect.anchoredPosition;
            page.sizeDelta = panelRect.sizeDelta;
            _page.SetActive(false);

            // The tab button: a copy of Epic Loot's last tab; in Enchanting Layout's columns (it places it), else as far
            // right of the window as its tabs are left of it.
            _button = UnityEngine.Object.Instantiate(template.gameObject, column ? handler.transform : handler.transform.parent, true);
            _button.name = TabName; // not an EnchantingTabs name: Epic Loot's tab switches leave it alone
            foreach (var c in _button.GetComponentsInChildren<Component>(true))
                if (c != null && c.GetType().Name == "FeatureStatus") UnityEngine.Object.DestroyImmediate(c);
            foreach (var layout in _button.GetComponents<LayoutElement>()) layout.ignoreLayout = true;
            foreach (var text in _button.GetComponentsInChildren<TMP_Text>(true)) text.text = "Combine";
            if (column) _button.transform.SetAsLastSibling();
            else
            {
                // Mirrored to the first tab on the left: its middle as far from the page's right edge as that one's is from the
                // page's left edge (by the middles: the tabs' pivot is their left edge).
                var pageCorners = Corners(page);
                var firstCorners = Corners((RectTransform)first.transform);
                var target = pageCorners[2].x + (pageCorners[0].x - (firstCorners[0].x + firstCorners[2].x) * 0.5f);
                _button.transform.position = first.transform.position;
                var mine = Corners((RectTransform)_button.transform);
                _button.transform.position += new Vector3(target - (mine[0].x + mine[2].x) * 0.5f, 0f, 0f);
            }
            Icon(_button);
            var button = _button.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            _tab = new TabHandler.Tab { m_button = button, m_page = page, m_default = false, m_onClick = new UnityEvent() };
            handler.m_tabs.Add(_tab);
            button.onClick.AddListener(() =>
            {
                var i = handler.m_tabs.IndexOf(_tab);
                if (i >= 0) handler.SetActiveTab(i);
            });
            button.interactable = true;
            var selected = _button.transform.Find("Selected");
            if (selected != null) selected.gameObject.SetActive(false);
            _log.LogInfo("Combine tab added to the enchanting table.");
        }

        private static Vector3[] Corners(RectTransform rect)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            return corners;
        }

        private static bool Holds(Vector3[] outer, Vector3[] inner) =>
            inner[0].x >= outer[0].x - 2f && inner[0].y >= outer[0].y - 2f && inner[2].x <= outer[2].x + 2f && inner[2].y <= outer[2].y + 2f;

        // The window's wooden board: the smallest picture (not a scrim) around both the tab column and the panels.
        private static RectTransform Frame(EnchantingTableUI ui, RectTransform tab, RectTransform panel)
        {
            if (ui.Root == null) return null;
            RectTransform best = null;
            var bestArea = float.MaxValue;
            var tabCorners = Corners(tab);
            var panelCorners = Corners(panel);
            foreach (var image in ui.Root.GetComponentsInChildren<Image>(true))
            {
                if (image == null || image.gameObject == ui.Scrim || image.gameObject == ui.TabScrim || image.sprite == null) continue;
                var c = Corners(image.rectTransform);
                if (!Holds(c, tabCorners) || !Holds(c, panelCorners)) continue;
                var area = (c[2].x - c[0].x) * (c[2].y - c[0].y);
                if (area >= bestArea) continue;
                best = image.rectTransform;
                bestArea = area;
            }
            return best;
        }

        // Rect transforms moved for the wider frame, with their old offsets (put back when the tab goes).
        private sealed class Moved
        {
            public RectTransform Rect;
            public Vector2 Min, Max;
        }

        private static readonly List<Moved> MovedRects = new List<Moved>();

        private static void Keep(RectTransform rect) => MovedRects.Add(new Moved { Rect = rect, Min = rect.offsetMin, Max = rect.offsetMax });

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

        // Its own picture instead of the copied tab's: the Ancient Brokkr's Gift (Epic Loot's socket chisel), else a runestone.
        private static void Icon(GameObject button)
        {
            var item = ObjectDB.instance != null ? (ObjectDB.instance.GetItemPrefab("ShardSlotChiselAncient") ?? ObjectDB.instance.GetItemPrefab("RunestoneAncient")) : null;
            var sprite = item != null ? item.GetComponent<ItemDrop>()?.m_itemData.GetIcon() : null;
            var target = button.GetComponent<Button>()?.targetGraphic;
            var parts = button.GetComponentsInChildren<Image>(true).Where(i => i != target && i.sprite != null && i.transform.parent != null &&
                                                                                i.transform.parent.name != "Selected").ToList();
            _log.LogInfo("Combine tab parts: " + string.Join(", ", button.GetComponentsInChildren<Transform>(true).Select(t =>
                t.name + (t.GetComponent<Image>() != null && t.GetComponent<Image>().sprite != null ? "[" + t.GetComponent<Image>().sprite.name + "]" : ""))));
            // The tab's glyph ("Image", sprite *_tabicon), not the lock's ("LockIcon").
            var icon = parts.FirstOrDefault(i => i.sprite.name.IndexOf("tabicon", StringComparison.OrdinalIgnoreCase) >= 0) ??
                       parts.FirstOrDefault(i => i.name == "Image");
            if (icon != null && sprite != null)
            {
                icon.sprite = sprite;
                icon.color = Color.white;
                icon.preserveAspect = true;
                icon.rectTransform.localScale = Vector3.one * 0.7f; // an item picture fills its square: room for the name
            }
        }

        private static void Remove()
        {
            if (_ui != null && _ui.TabHandler != null && _tab != null)
            {
                var handler = _ui.TabHandler;
                var mine = handler.m_tabs.IndexOf(_tab);
                if (mine >= 0)
                {
                    if (handler.GetActiveTab() == mine && handler.m_tabs.Count > 1) handler.SetActiveTab(mine == 0 ? 1 : 0);
                    handler.m_tabs.RemoveAt(mine);
                }
            }
            if (_button != null) UnityEngine.Object.Destroy(_button);
            if (_page != null) UnityEngine.Object.Destroy(_page);
            for (var i = MovedRects.Count - 1; i >= 0; i--)
            {
                var m = MovedRects[i];
                if (m.Rect == null) continue;
                m.Rect.offsetMin = m.Min;
                m.Rect.offsetMax = m.Max;
            }
            MovedRects.Clear();
            _button = _page = null;
            _tab = null;
            _ui = null;
            _wasShown = false;
            _keep = _give = null;
        }

        // --- what can be combined ------------------------------------------------------------------------------------

        private static string PrefabOf(ItemDrop.ItemData item) => item.m_dropPrefab != null ? item.m_dropPrefab.name : item.m_shared.m_name;

        // The name in its rarity's colour, translated (Epic Loot's decorated name keeps the $ token).
        private static string Name(ItemDrop.ItemData item) => Localization.instance.Localize(item.GetDecoratedName());

        private static MagicItem Magic(ItemDrop.ItemData item) => item != null && item.IsMagic(out var magic) ? magic : null;

        // give can be given up for keep: the same kind and rarity, and at least as many sockets (a 4-socket one may give a
        // 1-socket one its second; asked for 2026-11-20).
        private static bool Twins(ItemDrop.ItemData keep, ItemDrop.ItemData give)
        {
            var mk = Magic(keep);
            var mg = Magic(give);
            return keep != give && mk != null && mg != null && PrefabOf(keep) == PrefabOf(give) && mk.Rarity == mg.Rarity &&
                   mg.SocketCount >= mk.SocketCount;
        }

        private static int Cap(MagicItem magic) => LootRoller.GetMaxSocketCountForRarity(magic.Rarity);

        // Why this one cannot be given up for the kept one, or null.
        private static string CannotGive(ItemDrop.ItemData give)
        {
            if (give.m_equipped) return "it is worn: take it off first";
            var magic = Magic(give);
            if (magic != null && magic.Sockets != null && magic.Sockets.Count > 0) return "it has shards in it: take them out first";
            return null;
        }

        // Every carried socketable magic item that can be kept (below its rarity's most, with one to give up for it); once
        // one is chosen to keep, also those that can be given up for it (also at their most).
        private static List<ItemDrop.ItemData> Candidates(Player player)
        {
            var all = player.GetInventory().GetAllItems().Where(i => Magic(i) != null && GatedItemTypeHelper.IsSocketable(i)).ToList();
            return all.Where(i => Magic(i).SocketCount < Cap(Magic(i)) && all.Any(o => Twins(i, o)) || _keep != null && Twins(_keep, i))
                .OrderByDescending(i => (int)Magic(i).Rarity).ThenBy(i => Localization.instance.Localize(i.m_shared.m_name))
                .ThenByDescending(i => Magic(i).SocketCount).ToList();
        }

        private static List<Cost> Costs(ItemDrop.ItemData keep)
        {
            var magic = Magic(keep);
            var n = magic.SocketCount + 1;
            var rarity = magic.Rarity.ToString();
            var list = new List<Cost>();
            Add(list, "Coins", _coins.Value * n);
            Add(list, "Essence" + rarity, _essence.Value * n);
            Add(list, "Reagent" + rarity, _reagent.Value * n);
            Add(list, "Runestone" + rarity, _runestones.Value);
            return list;
        }

        private static void Add(List<Cost> list, string prefab, int amount)
        {
            if (amount <= 0) return;
            var item = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefab)?.GetComponent<ItemDrop>() : null;
            if (item == null) return; // not in this Epic Loot: no cost
            var name = item.m_itemData.m_shared.m_name;
            list.Add(new Cost
            {
                Prefab = name, Name = Localization.instance.Localize(name), Icon = item.m_itemData.GetIcon(), Need = amount,
                Have = InventoryManagement.Instance.CountItem(name),
            });
        }

        // --- the page ------------------------------------------------------------------------------------------------

        private static void Draw()
        {
            if (_page == null) return;
            for (var i = _page.transform.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(_page.transform.GetChild(i).gameObject);
            var player = Player.m_localPlayer;
            if (player == null) return;
            Windows.UseGameFont();
            var page = (RectTransform)_page.transform;
            var size = page.rect.size;
            float w = size.x, h = size.y;
            var shade = Windows.Rect("Shade", page);
            Windows.Stretch(shade);
            shade.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.55f);

            Windows.Place(Windows.Text(page, "Combine", 28f, Windows.Gold, TextAlignmentOptions.Left).rectTransform, 20f, 12f, w - 40f, 36f);
            var intro = Windows.Text(page, "Two of the same item and rarity, both carried: the one you give up is used and the one you keep gets " +
                                           "one more shard socket, up to the most its rarity can have. The one given up needs at least as many " +
                                           "sockets as the one kept.",
                17f, Windows.Body, TextAlignmentOptions.TopLeft);
            intro.textWrappingMode = TextWrappingModes.Normal;
            intro.overflowMode = TextOverflowModes.Overflow;
            Windows.Place(intro.rectTransform, 20f, 50f, w - 40f, 48f);

            var listWidth = Mathf.Floor(w * 0.5f) - 28f;
            var top = 108f;
            var items = Candidates(player);
            if (_keep != null && (!items.Contains(_keep) || Magic(_keep).SocketCount >= Cap(Magic(_keep)))) _keep = _give = null;
            items = Candidates(player); // the ones to give up for the kept one are listed only while one is kept
            if (_give != null && (!items.Contains(_give) || !Twins(_keep, _give))) _give = null;
            if (_keep != null && _give == null)
            {
                var twins = items.Where(i => Twins(_keep, i) && CannotGive(i) == null).ToList();
                if (twins.Count == 1) _give = twins[0]; // only one fits: chosen for you
            }
            if (items.Count == 0)
            {
                var none = Windows.Text(page, "Nothing to combine: carry two of the same magic item and rarity (the one to give up with at " +
                                              "least as many sockets).", 18f,
                    Windows.Dim, TextAlignmentOptions.TopLeft);
                none.textWrappingMode = TextWrappingModes.Normal;
                Windows.Place(none.rectTransform, 20f, top, w - 40f, 60f);
                return;
            }
            Windows.Place(Windows.Text(page, _keep == null ? "Click the one to keep" : "Click the one to give up",
                19f, Windows.Gold, TextAlignmentOptions.Left).rectTransform, 20f, top, listWidth, 28f);
            var fits = Mathf.Max(1, Mathf.FloorToInt((h - top - 40f - 16f) / RowHeight));
            var y = top + 34f;
            for (var i = 0; i < items.Count && i < fits; i++)
            {
                Row(page, items[i], 20f, y, listWidth, i);
                y += RowHeight;
            }
            if (items.Count > fits)
                Windows.Place(Windows.Text(page, $"and {items.Count - fits} more (put some away)", 16f, Windows.Dim, TextAlignmentOptions.Left).rectTransform,
                    20f, y + 4f, listWidth, 24f);
            Details(page, w * 0.5f + 8f, top, w * 0.5f - 28f, h);
        }

        private static void Row(RectTransform page, ItemDrop.ItemData item, float x, float y, float width, int index)
        {
            var magic = Magic(item);
            var row = Windows.Rect("Row", page);
            Windows.Place(row, x, y, width, RowHeight - 4f);
            var keep = item == _keep;
            var give = item == _give;
            var back = row.gameObject.AddComponent<Image>();
            var normal = keep ? new Color(1f, 0.72f, 0.36f, 0.28f) : give ? new Color(1f, 0.3f, 0.25f, 0.28f) : new Color(0f, 0f, 0f, 0.22f);
            back.color = normal;
            Windows.Picture(row, item.GetIcon(), 6f, 3f, 44f, 44f);
            var name = Windows.Text(row, Name(item), 18f, Windows.Body, TextAlignmentOptions.Left);
            Windows.Place(name.rectTransform, 58f, 4f, width - 64f, 24f);
            var cap = Cap(magic);
            var line = $"{magic.Rarity}, sockets {magic.SocketCount} of {cap}" + (item.m_equipped ? ", worn" : "") +
                       (magic.Sockets != null && magic.Sockets.Count > 0 ? $", {magic.Sockets.Count} shard{(magic.Sockets.Count == 1 ? "" : "s")} in it" : "") +
                       (keep ? "   KEEP" : give ? "   GIVE UP" : "");
            Windows.Place(Windows.Text(row, line, 15f, keep ? Windows.Gold : give ? Red : Windows.Dim, TextAlignmentOptions.Left).rectTransform,
                58f, 27f, width - 64f, 20f);
            var button = row.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None; // the button's own tint made rows grey
            button.targetGraphic = back;
            button.onClick.AddListener(() => Click(item));
            var hover = row.gameObject.AddComponent<RowHover>();
            hover.Image = back;
            hover.Normal = normal;
            hover.Over = keep || give ? normal : new Color(1f, 0.72f, 0.36f, 0.14f);
            Tooltip(row.gameObject, item);
        }

        // The item's own tooltip (Epic Loot's for magic items: its enchantments and sockets), like in the inventory.
        private static GameObject _tooltipPrefab;

        private static void Tooltip(GameObject target, ItemDrop.ItemData item)
        {
            if (_tooltipPrefab == null)
                _tooltipPrefab = InventoryGui.instance?.m_playerGrid?.m_elementPrefab?.GetComponent<UITooltip>()?.m_tooltipPrefab;
            if (_tooltipPrefab == null) return;
            var tip = target.AddComponent<UITooltip>();
            tip.m_tooltipPrefab = _tooltipPrefab;
            tip.Set(Name(item), item.GetTooltip());
        }

        // A row lights up under the mouse (set straight, no fade).
        private sealed class RowHover : MonoBehaviour, UnityEngine.EventSystems.IPointerEnterHandler, UnityEngine.EventSystems.IPointerExitHandler
        {
            public Image Image;
            public Color Normal, Over;

            public void OnPointerEnter(UnityEngine.EventSystems.PointerEventData data)
            {
                if (Image != null) Image.color = Over;
            }

            public void OnPointerExit(UnityEngine.EventSystems.PointerEventData data)
            {
                if (Image != null) Image.color = Normal;
            }
        }

        private static void Click(ItemDrop.ItemData item)
        {
            _sure = false;
            if (item == _keep) _keep = _give = null;          // again: start over
            else if (item == _give) _give = null;
            else if (_keep != null && Twins(_keep, item)) _give = item;
            else if (Magic(item).SocketCount < Cap(Magic(item)))
            {
                _keep = item;
                _give = null;
            }
            Draw();
        }

        // The right side: the pair, the cost, the button.
        private static void Details(RectTransform page, float x, float top, float width, float h)
        {
            var y = top;
            if (_keep == null)
            {
                var hint = Windows.Text(page, "The one you keep keeps its enchantments and shards. The one you give up must have no " +
                                              "shards in it and must not be worn.", 17f, Windows.Dim, TextAlignmentOptions.TopLeft);
                hint.textWrappingMode = TextWrappingModes.Normal;
                Windows.Place(hint.rectTransform, x, y, width, 80f);
                return;
            }
            var magic = Magic(_keep);
            var cap = Cap(magic);
            Windows.Place(Windows.Text(page, "Keep", 19f, Windows.Gold, TextAlignmentOptions.Left).rectTransform, x, y, width, 26f);
            Windows.Place(Windows.Text(page, Name(_keep), 18f, Windows.Body, TextAlignmentOptions.Left).rectTransform, x, y + 26f, width, 24f);
            var atCap = magic.SocketCount >= cap;
            Windows.Place(Windows.Text(page, atCap ? $"Sockets {magic.SocketCount}: the most for {magic.Rarity}"
                    : $"Sockets {magic.SocketCount}  ->  {magic.SocketCount + 1}   (most for {magic.Rarity}: {cap})",
                16f, atCap ? Red : Green, TextAlignmentOptions.Left).rectTransform, x, y + 50f, width, 22f);
            y += 84f;
            Windows.Place(Windows.Text(page, "Give up", 19f, Windows.Gold, TextAlignmentOptions.Left).rectTransform, x, y, width, 26f);
            var why = _give != null ? CannotGive(_give) : null;
            Windows.Place(Windows.Text(page, _give == null ? "Click the one to give up (left): the same item, as many sockets or more" :
                    Name(_give) + (why != null ? ": " + why : ""),
                _give == null ? 16f : 18f, _give == null ? Windows.Dim : why != null ? Red : Windows.Body, TextAlignmentOptions.Left).rectTransform,
                x, y + 26f, width, 24f);
            y += 62f;
            if (atCap) return;
            Windows.Place(Windows.Text(page, "Cost", 19f, Windows.Gold, TextAlignmentOptions.Left).rectTransform, x, y, width, 26f);
            y += 30f;
            var costs = Costs(_keep);
            foreach (var c in costs)
            {
                var enough = c.Have >= c.Need;
                if (c.Icon != null) Windows.Picture(page, c.Icon, x, y, 30f, 30f);
                Windows.Place(Windows.Text(page, c.Name, 17f, Windows.Body, TextAlignmentOptions.Left).rectTransform, x + 38f, y + 4f, width - 160f, 24f);
                Windows.Place(Windows.Text(page, $"{MaterialsCount(c.Have)} / {MaterialsCount(c.Need)}", 17f, enough ? Green : Red,
                    TextAlignmentOptions.Right).rectTransform, x + width - 130f, y + 4f, 130f, 24f);
                y += 34f;
            }
            if (costs.Count == 0)
            {
                Windows.Place(Windows.Text(page, "free (every cost set to 0)", 16f, Windows.Dim, TextAlignmentOptions.Left).rectTransform, x, y, width, 24f);
                y += 30f;
            }
            var ready = _give != null && why == null && costs.All(c => c.Have >= c.Need);
            var buttonY = Mathf.Min(y + 12f, h - (_sure ? 84f : 56f));
            var button = Windows.Button(page, _sure ? "Sure?" : "Combine", x, buttonY, 180f, 42f, () =>
            {
                if (!_sure)
                {
                    _sure = true;
                    Draw();
                    return;
                }
                _sure = false;
                Combine();
            });
            button.interactable = ready;
            if (!ready) foreach (var t in button.GetComponentsInChildren<TMP_Text>(true)) t.color = Windows.Dim;
            if (_sure)
            {
                var warn = Windows.Text(page, "Click again to combine: " + (_give != null ? Name(_give) : "the one given up") + " is used up.",
                    16f, Red, TextAlignmentOptions.TopLeft);
                warn.textWrappingMode = TextWrappingModes.Normal;
                Windows.Place(warn.rectTransform, x, buttonY + 48f, width, 40f);
            }
        }

        private static string MaterialsCount(int n) => n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

        // Checked once more, then paid, the one given up taken, the socket added.
        private static void Combine()
        {
            var player = Player.m_localPlayer;
            if (player == null || _keep == null || _give == null) return;
            var inventory = player.GetInventory();
            var magic = Magic(_keep);
            if (!inventory.ContainsItem(_keep) || !inventory.ContainsItem(_give) || !Twins(_keep, _give) || CannotGive(_give) != null ||
                magic == null || magic.SocketCount >= Cap(magic))
            {
                Draw();
                return;
            }
            var costs = Costs(_keep);
            if (costs.Any(c => c.Have < c.Need))
            {
                Draw();
                return;
            }
            if (ShardUI.IsOpenFor(_keep) || ShardUI.IsOpenFor(_give)) ShardUI.Close();
            foreach (var c in costs) InventoryManagement.Instance.RemoveItem(c.Prefab, c.Need);
            var name = Localization.instance.Localize(_keep.m_shared.m_name);
            inventory.RemoveItem(_give);
            magic.SocketCount += 1;
            var keep = _keep;
            Action save = () => keep.SaveMagicItem(magic);
            if (WithChangeReason != null) WithChangeReason.Invoke(null, new object[] { "AddSocket", save });
            else save();
            _log.LogInfo($"Combined two {name} ({magic.Rarity}, gave up one with {Magic(_give)?.SocketCount ?? 0}): it has {magic.SocketCount} sockets now; paid " +
                         string.Join(", ", costs.Select(c => c.Need + " " + c.Prefab)));
            player.Message(MessageHud.MessageType.Center, $"{name}: {magic.SocketCount} shard sockets");
            var ui = EnchantingTableUI.instance;
            if (ui != null && ui.Audio != null && ui.EnchantBonusSFX != null) ui.Audio.PlayOneShot(ui.EnchantBonusSFX);
            _give = null;
            Draw();
        }
    }
}
