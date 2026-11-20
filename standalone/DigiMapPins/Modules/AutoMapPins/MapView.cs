// Auto Map Pins, the large map's view:
// - Zoom levels: zoomed out only portals, boss altars and traders show, halfway the dungeons and other places too,
//   zoomed in everything (per kind in F1, "Zoom - ..."); names show one step later (portal names halfway, place names
//   zoomed in, the rest close up).
// - Merging: pins of the same thing that would overlap at this zoom are one pin with their count on it, until you
//   zoom in far enough for them to stand apart (worked out again only when the zoom or the pins change).
// - Pointing at a pin tells what it is and how it is (regrows in 12 min, looted, what a place holds, what a spawner
//   drops, how far away); also the map's own trader icons.
// - Filter buttons: a row on the large map that hides or shows a kind of pin (on the minimap too) with one click; the
//   last button opens the list of every kind (PinList.cs) to switch single ones on or off. Sized by "Filter button size".
using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace AutoMapPins
{
    internal static class MapView
    {
        // --- Zoom levels -----------------------------------------------------------------------------------------

        // 0 zoomed out, 1 halfway, 2 zoomed in, 3 close up (all names); the minimap counts as close up.
        internal static int Step(Minimap map)
        {
            if (map == null || map.m_mode != Minimap.MapMode.Large) return 3;
            var zoom = map.LargeZoom;
            return zoom >= Plugin.ZoomOut ? 0 : zoom >= Plugin.ZoomIn ? 1 : zoom >= Plugin.ZoomNames ? 2 : 3;
        }

        private static bool LevelOk(Record record, int step) => !Plugin.ZoomLevels || step >= record.Level;

        internal static bool ShownLarge(Record record, int step) => record.MergedInto == null && !Filtered(record) && LevelOk(record, step);

        internal static bool NameShown(Record record, int step) => !Plugin.ZoomLevels || step >= record.Level + 1;

        // --- Filters ---------------------------------------------------------------------------------------------

        // The last one only with Epic Loot: its bounty and treasure map pins (with their circles) and the pins of the chests
        // of treasure maps you bought.
        private static readonly string[] FilterTitles =
        {
            "Portals", "Bosses and traders", "Dungeons and caves", "Other places", "Ore and deposits", "Plants and pickups", "Spawners",
            "Chests", "Epic Loot bounties and treasure maps",
        };
        private static readonly string[] FilterIcons =
        {
            WhiteIcons.Portal, "lorc/crowned-skull", "delapouite/crypt-entrance", "delapouite/position-marker", "faithtoken/ore",
            "delapouite/raspberry", "lorc/skull-crossed-bones", "lorc/locked-chest", "lorc/treasure-map",
        };
        private const int EpicFilter = 8;
        private static int _epicLoot = -1;

        private static bool EpicLoot
        {
            get
            {
                if (_epicLoot < 0) _epicLoot = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("randyknapp.mods.epicloot") ? 1 : 0;
                return _epicLoot == 1;
            }
        }

        private static int FilterCount => EpicLoot ? FilterTitles.Length : EpicFilter;

        // Epic Loot's bounty and treasure map pins are hidden by their button.
        internal static bool EpicHidden => EpicLoot && (Plugin.HiddenKinds & (1 << EpicFilter)) != 0;

        // A pin of Epic Loot: its bounties and treasure maps (pin types of its own: 18 and 19 now, 800 and 801 before).
        internal static bool IsEpicPin(Minimap.PinData pin)
        {
            var type = (int)pin.m_type;
            return EpicLoot && (type == 18 || type == 19 || type == 800 || type == 801);
        }

        internal static int FilterOf(Record record, Thing thing) => record.IsPortal || thing == null ? 0 : FilterOf(thing);

        internal static string FilterTitle(int filter) => FilterTitles[filter];
        internal static string FilterIcon(int filter) => FilterIcons[filter];
        internal static int Filters => FilterCount;

        // The filter button of a kind of thing (not a portal).
        internal static int FilterOf(Thing thing)
        {
            if (thing.Id == Catalog.MapChestId && EpicLoot) return EpicFilter;
            switch (Plugin.GroupOf(thing))
            {
                case Plugin.Group.Bosses:
                case Plugin.Group.Traders: return 1;
                case Plugin.Group.Dungeons: return 2;
                case Plugin.Group.Places:
                case Plugin.Group.Builds: return 3;
                case Plugin.Group.Deposits: return 4;
                case Plugin.Group.Plants: return 5;
                case Plugin.Group.Spawners: return 6;
                default: return 7;
            }
        }

        internal static bool Filtered(Record record) => (Plugin.HiddenKinds & (1 << record.Filter)) != 0;

        // --- Merging ---------------------------------------------------------------------------------------------

        internal static bool Dirty = true;
        private static float _mergedZoom = -1f;
        private static int _mergedStep = -1, _mergedHidden = -1;
        private static bool _mergedOn;
        private static readonly Dictionary<string, List<Record>> Groups = new Dictionary<string, List<Record>>();

        // Pins of one thing closer together than about a pin's width on the map are shown as the biggest of them, with
        // the count of all. Each pin is merged into a pin it touches (no chains), the biggest pins first.
        internal static void Merge(Minimap map, ICollection<Record> records, int step)
        {
            var zoom = map.LargeZoom;
            var on = Plugin.MergePins;
            var hidden = Plugin.HiddenKinds;
            if (!Dirty && on == _mergedOn && step == _mergedStep && hidden == _mergedHidden && Mathf.Abs(zoom - _mergedZoom) <= _mergedZoom * 0.02f) return;
            Dirty = false;
            _mergedZoom = zoom;
            _mergedStep = step;
            _mergedHidden = hidden;
            _mergedOn = on;
            foreach (var record in records)
            {
                record.MergedInto = null;
                record.Total = record.Count;
                record.Pins = 1;
            }
            if (!on) return;
            var image = map.m_mapImageLarge;
            var height = image != null ? image.rectTransform.rect.height : 0f;
            if (height <= 0f) return;
            var worldPerUnit = image.uvRect.height * map.m_textureSize * map.m_pixelSize / height;
            foreach (var list in Groups.Values) list.Clear();
            foreach (var record in records)
            {
                if (record.IsPortal || Filtered(record) || !LevelOk(record, step)) continue;
                if (!Groups.TryGetValue(record.Thing, out var list)) Groups[record.Thing] = list = new List<Record>();
                list.Add(record);
            }
            foreach (var list in Groups.Values)
            {
                if (list.Count < 2) continue;
                list.Sort(Biggest);
                for (var i = 0; i < list.Count; i++)
                {
                    var lead = list[i];
                    if (lead.MergedInto != null) continue;
                    var reach = map.m_pinSizeLarge * lead.Scale * 0.8f * worldPerUnit;
                    for (var j = i + 1; j < list.Count; j++)
                    {
                        var other = list[j];
                        if (other.MergedInto != null || Utils.DistanceXZ(lead.Pos, other.Pos) > reach) continue;
                        other.MergedInto = lead;
                        lead.Total += other.Count;
                        lead.Pins++;
                    }
                }
            }
        }

        // The biggest first; the same each time (by place), so the shown pin does not hop around while zooming.
        private static int Biggest(Record a, Record b)
        {
            var c = b.Count.CompareTo(a.Count);
            if (c != 0) return c;
            c = a.Pos.x.CompareTo(b.Pos.x);
            return c != 0 ? c : a.Pos.z.CompareTo(b.Pos.z);
        }

        // --- Count on a pin --------------------------------------------------------------------------------------

        private static readonly Dictionary<Record, TMP_Text> Badges = new Dictionary<Record, TMP_Text>();
        private static readonly AccessTools.FieldRef<Minimap, GameObject> NamePrefab = AccessTools.FieldRefAccess<Minimap, GameObject>("m_pinNamePrefab");

        // The count of a pin (with the pins merged into it), at its lower right, on the large map.
        internal static void Badge(Minimap.PinData pin, Record record, bool show)
        {
            Badges.TryGetValue(record, out var text);
            var count = show && Plugin.ShowCounts ? record.Total : 0;
            if (count <= 1)
            {
                if (text != null && text.gameObject.activeSelf) text.gameObject.SetActive(false);
                return;
            }
            if (text == null || text.transform.parent != pin.m_uiElement)
            {
                if (text != null) Object.Destroy(text.gameObject);
                text = NewText(pin.m_uiElement, "DigiPinCount", 19f, false);
                if (text == null) return;
                var rect = text.rectTransform;
                rect.anchorMin = rect.anchorMax = new Vector2(1f, 0f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.anchoredPosition = new Vector2(0f, 3f);
                rect.sizeDelta = new Vector2(40f, 20f);
                text.alignment = TextAlignmentOptions.Center;
                text.fontStyle = FontStyles.Bold;
                Badges[record] = text;
            }
            if (!text.gameObject.activeSelf) text.gameObject.SetActive(true);
            var value = count.ToString();
            if (text.text != value) text.text = value;
        }

        // A text in the font of the map's pin names (outlined, for counts on the map), or of its biome name (plain, to read).
        private static TMP_Text NewText(Transform parent, string name, float size, bool plain)
        {
            if (parent == null) return null;
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<TextMeshProUGUI>();
            var map = Minimap.instance;
            var prefab = map != null ? NamePrefab(map) : null;
            var model = plain && map != null && map.m_biomeNameLarge != null ? map.m_biomeNameLarge
                : prefab != null ? prefab.GetComponentInChildren<TMP_Text>(true) : null;
            if (model != null)
            {
                text.font = model.font;
                text.fontSharedMaterial = model.fontSharedMaterial;
            }
            text.fontSize = size;
            text.color = Color.white;
            text.raycastTarget = false;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Overflow;
            text.richText = true;
            return text;
        }

        // A record's pin went: its count with it.
        internal static void Forget(Record record)
        {
            if (Badges.TryGetValue(record, out var text) && text != null) Object.Destroy(text.gameObject);
            Badges.Remove(record);
        }

        // --- Each frame: the filter buttons and the info of the pin pointed at -----------------------------------

        private static Minimap _uiMap;
        private static Camera _camera;

        internal static void Update()
        {
            var map = Minimap.instance;
            if (map == null || map.m_largeRoot == null) return;
            if (map != _uiMap)
            {
                Clear();
                _uiMap = map;
                var canvas = map.m_largeRoot.GetComponentInParent<Canvas>();
                _camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            }
            var large = map.m_mode == Minimap.MapMode.Large;
            UpdateBar(map, large);
            if (!large)
            {
                HideTip();
                return;
            }
            if (PinList.IsOpen)
            {
                HideTip();
                return;
            }
            Vector2 pointer = ZInput.pointerPosition;
            var text = ButtonTip(pointer) ?? (Plugin.HoverInfo ? PinTip(pointer) : null);
            if (text == null) HideTip();
            else ShowTip(map, text, pointer);
        }

        internal static void Clear()
        {
            foreach (var text in Badges.Values)
                if (text != null) Object.Destroy(text.gameObject);
            Badges.Clear();
            if (_bar != null) Object.Destroy(_bar.gameObject);
            if (_tip != null) Object.Destroy(_tip.gameObject);
            _bar = null;
            _tip = null;
            _tipText = null;
            Buttons.Clear();
            _uiMap = null;
            _pointed = null;
            Dirty = true;
        }

        // --- Pin info --------------------------------------------------------------------------------------------

        private static RectTransform _tip;
        private static TMP_Text _tipText;
        private static object _pointed;
        private static string _pointedText;
        private static float _pointedUntil;

        private static string PinTip(Vector2 pointer)
        {
            // The pointer in the pins' own space, once; then each pin is a plain comparison (no screen maths per pin).
            Record best = null;
            var bestDistance = float.MaxValue;
            var root = Minimap.instance != null ? Minimap.instance.m_pinRootLarge : null;
            if (root != null && RectTransformUtility.ScreenPointToLocalPointInRectangle(root, pointer, _camera, out var local))
                foreach (var pair in MapPins.Shown)
                {
                    var ui = pair.Key.m_uiElement;
                    if (ui == null || !ui.gameObject.activeSelf) continue;
                    var half = ui.rect.width * ui.localScale.x * 0.5f;
                    var offset = (Vector2)ui.localPosition - local;
                    if (offset.x > half || offset.x < -half || offset.y > half || offset.y < -half) continue;
                    var distance = offset.sqrMagnitude;
                    if (distance < bestDistance)
                    {
                        best = pair.Value;
                        bestDistance = distance;
                    }
                }
            if (best != null) return Cached(best, () => Describe(best));
            var location = LocationAt(pointer, out var at);
            return location != null ? Cached(location, () => DescribeLocation(location, at)) : null;
        }

        // The text of what is pointed at, made again at most twice a second (a regrow time counts down).
        private static string Cached(object pointed, Func<string> make)
        {
            if (!Equals(pointed, _pointed) || Time.unscaledTime >= _pointedUntil)
            {
                _pointed = pointed;
                _pointedText = make();
                _pointedUntil = Time.unscaledTime + 0.5f;
            }
            return _pointedText;
        }

        private static string Localize(string text) =>
            string.IsNullOrEmpty(text) || Localization.instance == null ? text ?? "" : Localization.instance.Localize(text);

        private static string ItemName(string item)
        {
            if (string.IsNullOrEmpty(item) || item.StartsWith("loc:", StringComparison.Ordinal)) return null;
            var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(item) : null;
            var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            return drop != null ? Localize(drop.m_itemData.m_shared.m_name) : null;
        }

        private static string Where(Vector3 pos, Heightmap.Biome biome, string kind)
        {
            var player = Player.m_localPlayer;
            var text = kind;
            if (biome != Heightmap.Biome.None) text = (text.Length > 0 ? text + ", " : "") + Catalog.BiomeName(biome);
            if (player != null)
            {
                var distance = Utils.DistanceXZ(player.transform.position, pos);
                text += (text.Length > 0 ? ", " : "") + (distance >= 1000f ? $"{distance / 1000f:0.0} km" : $"{Mathf.RoundToInt(distance)} m") + " away";
            }
            return $"\n<color=#a8a8a8><size=85%>{text}</size></color>";
        }

        private static string KindName(Thing thing)
        {
            switch (Plugin.GroupOf(thing))
            {
                case Plugin.Group.Bosses: return "Boss altar";
                case Plugin.Group.Traders: return "Trader";
                case Plugin.Group.Dungeons: return "Dungeon";
                case Plugin.Group.Places: return "Place";
                case Plugin.Group.Deposits: return "Deposit";
                case Plugin.Group.Spawners: return "Spawner";
                case Plugin.Group.Chests: return "Chest";
                case Plugin.Group.Builds: return "Built";
                default: return thing.Kind == Kind.Beehive ? "Beehive" : "Pickup";
            }
        }

        private static string Describe(Record record)
        {
            var thing = record.IsPortal ? null : Catalog.Get(record.Thing);
            var text = new StringBuilder();
            string title;
            if (record.IsPortal) title = string.IsNullOrEmpty(record.Label) ? "Portal without a name" : record.Label;
            else if (thing == null) title = record.Thing;
            else if (thing.Kind == Kind.Spawner) title = thing.Name;
            else title = Localize(thing.Kind == Kind.Place ? thing.Label : record.Label ?? thing.Label);
            text.Append("<b>").Append(title).Append("</b>");
            if (record.Total > 1) text.Append(" x").Append(record.Total);
            var state = new List<string>();
            if (thing != null)
            {
                var now = ZNet.instance != null ? ZNet.instance.GetTime().Ticks : 0L;
                if (thing.Kind == Kind.Plant && thing.RespawnMinutes > 0f)
                    state.Add(record.FadedUntil > now
                        ? $"<color=#ffcf70>Picked: grows back in {Mathf.Max(1, Mathf.CeilToInt((record.FadedUntil - now) / (float)TimeSpan.TicksPerMinute))} min</color>"
                        : "<color=#9be07a>Ready to pick</color>");
                if (thing.Kind == Kind.Chest && record.Emptied) state.Add("<color=#ffcf70>Emptied</color>");
                if (record.Checked) state.Add(Catalog.IsDungeon(thing) ? "<color=#ffcf70>Looted (crossed off)</color>" : "<color=#ffcf70>Crossed off</color>");
                if (thing.Kind == Kind.Spawner)
                {
                    var drop = ItemName(record.Icon ?? (thing.IconItem != null && !thing.IconItem.StartsWith("Trophy", StringComparison.Ordinal) ? thing.IconItem : null));
                    if (drop != null) state.Add("Destroy it for: " + drop);
                }
                if (thing.Kind == Kind.Place && thing.IconItem != null && !thing.IconItem.StartsWith("Trophy", StringComparison.Ordinal))
                {
                    var holds = ItemName(thing.IconItem);
                    if (holds != null) state.Add("Found here: " + holds);
                }
            }
            if (record.Pins > 1) state.Add($"<color=#a8d8ff>{record.Pins} pins here: zoom in to see each</color>");
            foreach (var line in state) text.Append('\n').Append(line);
            text.Append(Where(record.Pos, record.Biome, record.IsPortal ? "Portal" : thing != null ? KindName(thing) : ""));
            return text.ToString();
        }

        // The map's own icons of places (Haldor, Hildir, the Bog Witch, the start): which one is pointed at.
        private static readonly Dictionary<Vector3, string> LocationNames = new Dictionary<Vector3, string>();
        private static float _locationsRead = -10f;

        private static string LocationAt(Vector2 pointer, out Vector3 at)
        {
            at = Vector3.zero;
            var pins = MapPins.GamePins();
            if (pins == null || ZoneSystem.instance == null) return null;
            foreach (var pin in pins)
            {
                var ui = pin.m_uiElement;
                if (ui == null || !MapPins.LocationIcon(pin) || !ui.gameObject.activeInHierarchy ||
                    !RectTransformUtility.RectangleContainsScreenPoint(ui, pointer, _camera)) continue;
                if (Time.unscaledTime - _locationsRead > 5f)
                {
                    _locationsRead = Time.unscaledTime;
                    LocationNames.Clear();
                    ZoneSystem.instance.GetLocationIcons(LocationNames);
                }
                foreach (var pair in LocationNames)
                    if (Utils.DistanceXZ(pair.Key, pin.m_pos) < 1f)
                    {
                        at = pin.m_pos;
                        return pair.Value;
                    }
            }
            return null;
        }

        private static string DescribeLocation(string prefab, Vector3 at)
        {
            var thing = Catalog.ForLocation(prefab);
            var title = thing != null ? thing.Name : prefab == "StartTemple" ? "The sacrificial stones" : prefab;
            var biome = WorldGenerator.instance != null ? WorldGenerator.instance.GetBiome(at) : Heightmap.Biome.None;
            return $"<b>{title}</b>" + Where(at, biome, thing != null && thing.Trader ? "Trader" : "");
        }

        private static void ShowTip(Minimap map, string text, Vector2 pointer)
        {
            if (_tip == null) MakeTip(map);
            if (_tip == null) return;
            if (_tipText.text != text)
            {
                _tipText.text = text;
                var size = _tipText.GetPreferredValues(text, 360f, 0f);
                _tip.sizeDelta = new Vector2(Mathf.Min(size.x, 360f) + 20f, size.y + 12f);
            }
            var parent = (RectTransform)_tip.parent;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, pointer, _camera, out var local)) return;
            var area = parent.rect;
            var tipSize = _tip.sizeDelta;
            var pos = local + new Vector2(22f, -22f);
            if (pos.x + tipSize.x > area.xMax) pos.x = local.x - 22f - tipSize.x;
            if (pos.y - tipSize.y < area.yMin) pos.y = local.y + 22f + tipSize.y;
            _tip.localPosition = new Vector3(pos.x, pos.y, 0f);
            if (!_tip.gameObject.activeSelf) _tip.gameObject.SetActive(true);
            if (_tip.GetSiblingIndex() != _tip.parent.childCount - 1) _tip.SetAsLastSibling();
        }

        internal static void HideTip()
        {
            if (_tip != null && _tip.gameObject.activeSelf) _tip.gameObject.SetActive(false);
            _pointed = null;
        }

        private static void MakeTip(Minimap map)
        {
            var go = new GameObject("DigiPinInfo", typeof(RectTransform));
            _tip = (RectTransform)go.transform;
            _tip.SetParent(map.m_largeRoot.transform, false);
            _tip.anchorMin = _tip.anchorMax = new Vector2(0.5f, 0.5f);
            _tip.pivot = new Vector2(0f, 1f);
            var back = go.AddComponent<Image>();
            back.color = new Color(0f, 0f, 0f, 0.82f);
            back.raycastTarget = false;
            _tipText = NewText(_tip, "Text", 18f, true);
            var rect = _tipText.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(10f, 6f);
            rect.offsetMax = new Vector2(-10f, -6f);
            _tipText.alignment = TextAlignmentOptions.TopLeft;
            _tipText.textWrappingMode = TextWrappingModes.Normal;
            go.SetActive(false);
        }

        // --- Filter buttons --------------------------------------------------------------------------------------

        private sealed class FilterButton
        {
            public RectTransform Rect;
            public Image Back, Icon;
        }

        private static RectTransform _bar;
        private static readonly List<FilterButton> Buttons = new List<FilterButton>();
        private static bool _barStale = true;
        private static float ButtonSize => 34f * Plugin.ButtonScale;
        private static float ButtonGap => 4f * Plugin.ButtonScale;

        internal static void ButtonsChanged() => _barStale = true;

        // "Filter button size" changed: the bar is made again at its new size.
        internal static void RebuildBar()
        {
            if (_bar != null) Object.Destroy(_bar.gameObject);
            _bar = null;
            Buttons.Clear();
            _barStale = true;
        }

        private static void UpdateBar(Minimap map, bool large)
        {
            if (!large || !Plugin.FilterButtons)
            {
                if (_bar != null && _bar.gameObject.activeSelf) _bar.gameObject.SetActive(false);
                return;
            }
            if (_bar == null) MakeBar(map);
            if (_bar == null) return;
            if (!_bar.gameObject.activeSelf) _bar.gameObject.SetActive(true);
            if (!_barStale) return;
            _barStale = false;
            var hidden = Plugin.HiddenKinds;
            for (var i = 0; i < Buttons.Count && i < FilterCount; i++)
            {
                var off = (hidden & (1 << i)) != 0;
                Buttons[i].Icon.color = off ? new Color(1f, 1f, 1f, 0.22f) : Color.white;
                Buttons[i].Back.color = off ? new Color(0.25f, 0.05f, 0.05f, 0.75f) : new Color(0f, 0f, 0f, 0.6f);
            }
        }

        private static void MakeBar(Minimap map)
        {
            var go = new GameObject("DigiPinFilters", typeof(RectTransform));
            _bar = (RectTransform)go.transform;
            _bar.SetParent(map.m_largeRoot.transform, false);
            _bar.anchorMin = _bar.anchorMax = new Vector2(0.5f, 1f);
            _bar.pivot = new Vector2(0.5f, 1f);
            _bar.sizeDelta = new Vector2((FilterCount + 1) * (ButtonSize + ButtonGap) - ButtonGap, ButtonSize);
            _bar.anchoredPosition = new Vector2(0f, -70f);
            Buttons.Clear();
            for (var i = 0; i <= FilterCount; i++) // the last one: the list of every kind
            {
                var index = i;
                var list = i == FilterCount;
                var button = new GameObject(list ? "Kinds list" : "Filter " + FilterTitles[i], typeof(RectTransform));
                var rect = (RectTransform)button.transform;
                rect.SetParent(_bar, false);
                rect.anchorMin = rect.anchorMax = new Vector2(0f, 0.5f);
                rect.pivot = new Vector2(0f, 0.5f);
                rect.sizeDelta = new Vector2(ButtonSize, ButtonSize);
                rect.anchoredPosition = new Vector2(i * (ButtonSize + ButtonGap), 0f);
                var back = button.AddComponent<Image>();
                back.color = new Color(0f, 0f, 0f, 0.6f);
                var click = button.AddComponent<Button>();
                click.transition = Selectable.Transition.None;
                click.targetGraphic = back;
                if (list) click.onClick.AddListener(() => PinList.OpenFromMap(map));
                else click.onClick.AddListener(() => Toggle(index));
                var iconGo = new GameObject("Icon", typeof(RectTransform));
                var iconRect = (RectTransform)iconGo.transform;
                iconRect.SetParent(rect, false);
                iconRect.anchorMin = Vector2.zero;
                iconRect.anchorMax = Vector2.one;
                iconRect.offsetMin = new Vector2(5f, 5f) * Plugin.ButtonScale;
                iconRect.offsetMax = new Vector2(-5f, -5f) * Plugin.ButtonScale;
                var icon = iconGo.AddComponent<Image>();
                icon.sprite = WhiteIcons.Get(list ? WhiteIcons.MapList : FilterIcons[i]);
                icon.preserveAspect = true;
                icon.raycastTarget = false;
                Buttons.Add(new FilterButton { Rect = rect, Back = back, Icon = icon });
            }
            _barStale = true;
        }

        private static void Toggle(int index)
        {
            Plugin.HiddenKinds ^= 1 << index;
            _barStale = true;
            _pointed = null;
            MapPins.Restyle();
        }

        private static string ButtonTip(Vector2 pointer)
        {
            if (_bar == null || !_bar.gameObject.activeInHierarchy) return null;
            var hidden = Plugin.HiddenKinds;
            for (var i = 0; i < Buttons.Count; i++)
            {
                if (!RectTransformUtility.RectangleContainsScreenPoint(Buttons[i].Rect, pointer, _camera)) continue;
                if (i >= FilterCount)
                    return $"<b>{Lang.T("Every kind of pin")}</b>\n<color=#a8a8a8><size=85%>{Lang.T("Click for the list: switch each kind on or off")}</size></color>";
                var off = (hidden & (1 << i)) != 0;
                return $"<b>{Lang.T(FilterTitles[i])}</b>: {(off ? "<color=#ff9a7a>" + Lang.T("hidden") + "</color>" : Lang.T("shown"))}" +
                       $"\n<color=#a8a8a8><size=85%>{Lang.T(off ? "Click to show these pins" : "Click to hide these pins")}</size></color>";
            }
            return null;
        }
    }
}
