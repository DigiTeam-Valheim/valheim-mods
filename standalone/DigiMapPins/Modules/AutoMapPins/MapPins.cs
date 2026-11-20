// Auto Map Pins, on the map: every shown record is a map pin the game does not save itself (the records are saved), so
// the style settings (icon style, colours, names, sizes, minimap) apply to all of them at once. A place you or anyone
// else has a pin on already (or a vegvisir marked) shows no second pin there, for as long as that pin is there. The game only lets you click
// (right-click deletes, left-click crosses off) the pins it saves, so this mod's pins are offered to that lookup too; a
// deleted pin is remembered and stays deleted. The game sets every pin white each time it redraws them, so the colour,
// fading and size of this mod's pins are set again right after (only when the map redraws its pins).
// Cartography tables: while the game writes your map to a table, this mod's shown pins take part as normal pins
// (portals with the portal icon, the rest with the round one, with their full name), so players without the mod get
// them too. Pins read from a table that this mod knows (by name) become its own pins, or are dropped when it has
// that pin already, so nothing shows twice.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;

namespace AutoMapPins
{
    internal static class MapPins
    {
        private static readonly Dictionary<Record, Minimap.PinData> Pins = new Dictionary<Record, Minimap.PinData>();
        private static readonly Dictionary<Minimap.PinData, Record> ByPin = new Dictionary<Minimap.PinData, Record>();
        private static readonly AccessTools.FieldRef<Minimap, List<Minimap.PinData>> AllPins =
            AccessTools.FieldRefAccess<Minimap, List<Minimap.PinData>>("m_pins");
        private static readonly AccessTools.FieldRef<Minimap, bool> UpdateRequired =
            AccessTools.FieldRefAccess<Minimap, bool>("m_pinUpdateRequired");
        private static Minimap _map;
        private static bool _internal, _restyle = true;

        // A pin not of this mod was added or removed: places near it may need to show or hide (checked once a second).
        internal static bool PlacesChanged;

        private static bool Ready()
        {
            var map = Minimap.instance;
            if (map == null) return false;
            if (map != _map)
            {
                Pins.Clear();
                ByPin.Clear();
                _map = map;
            }
            return true;
        }

        private static bool Visible(Record record)
        {
            if (record.Deleted) return false;
            if (record.IsPortal) return Plugin.PortalsOn && !record.PortalPrivate; // Portal List keeps it private from you
            var thing = Catalog.Get(record.Thing);
            if (thing == null || !Catalog.IsOn(record.Biome, thing)) return false;
            // Marked already by you or a table; a boss altar shows its own pin (the boss's head) over a vegvisir's boss pin.
            if (thing.Kind == Kind.Place) return !OtherPinNear(record.Pos, 25f, !Catalog.IsBoss(thing));
            // A spawner at a dungeon, cave, camp or village: the place's own pin is enough (unless asked for).
            return thing.Kind != Kind.Spawner || Plugin.LairSpawners || !Lairs.Near(record.Pos);
        }

        // Names are switched per kind of thing; places always say the thing's own name ("Crypt", not "Sunken Crypt").
        private static string LabelOf(Record record)
        {
            if (record.IsPortal) return Plugin.PortalNames ? record.Label ?? "" : "";
            var thing = Catalog.Get(record.Thing);
            if (thing == null || !Plugin.NamesFor(thing)) return "";
            var label = (thing.Kind == Kind.Place ? null : record.Label) ?? thing.Label ?? "";
            return record.Count > 1 ? $"{label} x{record.Count}" : label;
        }

        private static Sprite IconOf(Record record)
        {
            bool tinted;
            var sprite = record.IsPortal ? Icons.PortalIcon(record, out tinted)
                : Icons.Resolve(Catalog.Get(record.Thing), record.Biome, record.Emptied, record.Icon, out tinted);
            record.Tinted = tinted;
            return sprite ?? Icons.PinSprite(Minimap.PinType.Icon3);
        }

        // Colour, size, minimap, zoom level and filter of a pin, worked out when it or the settings change (not each time
        // the map redraws). White icons are drawn in their colour, game icons as they are.
        private static void Style(Record record)
        {
            var thing = record.IsPortal ? null : Catalog.Get(record.Thing);
            record.Tint = !record.Tinted ? Color.white : record.IsPortal ? Plugin.PortalColor : Catalog.ColorOf(record.Biome, thing);
            record.Scale = record.IsPortal ? Plugin.PortalSize : Plugin.SizeFor(thing);
            record.OnMinimap = record.IsPortal ? Plugin.PortalsOnMinimap : Plugin.OnMinimap(thing);
            record.Level = record.IsPortal || thing != null ? Plugin.LevelFor(thing) : 2;
            record.Filter = MapView.FilterOf(record, thing);
        }

        // This mod's pins on the map (for pointing at them).
        internal static Dictionary<Minimap.PinData, Record> Shown => ByPin;

        // A pin of this mod (shown), by its record.
        internal static Minimap.PinData PinOf(Record record) => Pins.TryGetValue(record, out var pin) ? pin : null;

        // Shows, updates or hides the pin of one record.
        internal static void Refresh(Record record)
        {
            if (!Ready()) return;
            MapView.Dirty = true; // counts and merging again
            Pins.TryGetValue(record, out var pin);
            if (!Visible(record))
            {
                if (pin != null) Take(record, pin);
                return;
            }
            var label = LabelOf(record);
            var icon = IconOf(record);
            Style(record);
            if (pin != null && pin.m_name == label && pin.m_icon == icon)
            {
                pin.m_checked = record.Checked;
                return;
            }
            if (pin != null) Take(record, pin);
            _internal = true;
            try
            {
                pin = Minimap.instance.AddPin(record.Pos, Minimap.PinType.None, label, false, record.Checked);
            }
            finally
            {
                _internal = false;
            }
            pin.m_icon = icon;
            Pins[record] = pin;
            ByPin[pin] = record;
            MapView.Dirty = true;
        }

        internal static void RefreshAll()
        {
            if (!Ready()) return;
            var current = new HashSet<Record>(Records.All);
            foreach (var record in new List<Record>(Pins.Keys))
                if (!current.Contains(record)) Take(record, Pins[record]);
            foreach (var record in Records.All) Refresh(record);
            Restyle();
        }

        // The record is gone (mined out, destroyed): so is its pin.
        internal static void Forget(Record record)
        {
            if (Pins.TryGetValue(record, out var pin)) Take(record, pin);
        }

        internal static void Clear()
        {
            // The game's pins this mod hid come back.
            foreach (var pin in HiddenGamePins)
                if (pin.m_uiElement != null) pin.m_uiElement.gameObject.SetActive(true);
            HiddenGamePins.Clear();
            if (Minimap.instance != null && Minimap.instance == _map)
                foreach (var pair in new List<KeyValuePair<Record, Minimap.PinData>>(Pins)) Take(pair.Key, pair.Value);
            Pins.Clear();
            ByPin.Clear();
        }

        private static void Take(Record record, Minimap.PinData pin)
        {
            _internal = true;
            try
            {
                if (Minimap.instance != null) Minimap.instance.RemovePin(pin);
            }
            finally
            {
                _internal = false;
            }
            Pins.Remove(record);
            ByPin.Remove(pin);
            MapView.Forget(record);
        }

        // Colour, fading and size are set when the map next redraws its pins.
        internal static void Restyle()
        {
            _restyle = true;
            if (Minimap.instance != null) UpdateRequired(Minimap.instance) = true;
        }

        // Only fading changed (a plant picked or regrown, a chest emptied): no need to work the styles out again.
        internal static void Redraw()
        {
            if (Minimap.instance != null) UpdateRequired(Minimap.instance) = true;
        }

        // Places near a pin that came or went (yours, a table's, a vegvisir's): shown or hidden again.
        internal static void RefreshPlaces()
        {
            PlacesChanged = false;
            if (!Ready()) return;
            DropCopies();
            foreach (var record in Records.All)
                if (!record.IsPortal && Catalog.Get(record.Thing)?.Kind == Kind.Place) Refresh(record);
        }

        // A plain round pin (the map's "Icon3", a white dot) with the name of a thing that this mod has pinned right there
        // is a copy of this mod's pin: from a cartography table or a map-sharing mod that it did not get to take over
        // (another language, an older version, a pin read outside a table), or one you placed by hand with that name.
        // It hid this mod's coloured pin under a white dot, as any pin of yours at a place does. The copy goes and this
        // mod's pin shows (a crossed-off copy crosses it off). A pin of a thing you deleted here stays.
        private static void DropCopies()
        {
            var map = Minimap.instance;
            if (map == null) return;
            var copies = new List<Minimap.PinData>();
            var changed = new List<Record>();
            foreach (var pin in AllPins(map))
            {
                if (pin.m_type != Minimap.PinType.Icon3 || !pin.m_save || ByPin.ContainsKey(pin) || string.IsNullOrEmpty(pin.m_name)) continue;
                var record = CopyOf(pin);
                if (record == null) continue;
                copies.Add(pin);
                if (pin.m_checked && !record.Checked)
                {
                    record.Checked = true;
                    Records.Dirty = true;
                }
                changed.Add(record);
            }
            if (copies.Count == 0) return;
            _internal = true;
            try
            {
                foreach (var pin in copies) map.RemovePin(pin);
            }
            finally
            {
                _internal = false;
            }
            foreach (var record in changed) Refresh(record);
            Plugin.Log?.LogInfo($"{copies.Count} plain pins named like this mod's pins at the same place were replaced by this mod's own.");
        }

        private static Record CopyOf(Minimap.PinData pin)
        {
            var name = pin.m_name;
            var match = Counted.Match(name);
            if (match.Success) name = match.Groups[1].Value;
            var thing = Catalog.ForName(name) ?? (name.EndsWith(" spawner") ? Catalog.Get(Catalog.SpawnerId) : null);
            if (thing == null || thing.Kind == Kind.Portal) return null;
            // As close as a place hides its own pin under another (25 m), or the thing's own spacing when smaller.
            var radius = Mathf.Min(Mathf.Max(thing.Spacing, 5f), 25f);
            var record = thing.Kind == Kind.Spawner ? Records.ClosestSpawner(pin.m_pos, radius) : Records.Closest(pin.m_pos, radius, thing.Id);
            return record != null && !record.Deleted ? record : null;
        }

        // A pin of someone else close by (yours, a vegvisir's boss pin, Hildir's, a cartography table's): the place is
        // already marked. Death markers and the bed do not count.
        internal static bool OtherPinNear(Vector3 pos, float radius, bool bossPins = true)
        {
            if (Minimap.instance == null) return false;
            foreach (var pin in AllPins(Minimap.instance))
                if (!ByPin.ContainsKey(pin) && Utils.DistanceXZ(pin.m_pos, pos) < radius && Marks(pin) &&
                    (bossPins || pin.m_type != Minimap.PinType.Boss)) return true;
            return false;
        }

        internal static List<Minimap.PinData> GamePins() => Minimap.instance != null ? AllPins(Minimap.instance) : null;

        // --- Patches -------------------------------------------------------------------------------------------

        // After the game placed its pins (it does so when the map moves, zooms or a pin changes): this mod's pins get their
        // colour, fading and size, and on the large map show by zoom level and filter, merged where they would overlap.
        // Names are only ever hidden here (the game shows them on the large map, never on the minimap).
        [HarmonyPatch(typeof(Minimap), "UpdatePins")]
        [HarmonyPostfix]
        private static void Styled()
        {
            if (ByPin.Count == 0) return;
            var map = Minimap.instance;
            if (_restyle)
            {
                _restyle = false;
                foreach (var record in ByPin.Values) Style(record);
                MapView.Dirty = true;
            }
            var now = ZNet.instance != null ? ZNet.instance.GetTime().Ticks : 0L;
            var large = map.m_mode == Minimap.MapMode.Large;
            var step = MapView.Step(map);
            if (large) MapView.Merge(map, ByPin.Values, step);
            foreach (var pair in ByPin)
            {
                var pin = pair.Key;
                var record = pair.Value;
                if (pin.m_checked != record.Checked)
                {
                    record.Checked = pin.m_checked;
                    Records.Dirty = true;
                }
                if (pin.m_uiElement == null || pin.m_iconElement == null) continue;
                var shown = large ? MapView.ShownLarge(record, step) : record.OnMinimap && !MapView.Filtered(record);
                if (pin.m_uiElement.gameObject.activeSelf != shown) pin.m_uiElement.gameObject.SetActive(shown);
                var name = pin.m_NamePinData?.PinNameGameObject;
                record.NameOk = shown && large && MapView.NameShown(record, step);
                if (name != null && name.activeSelf && !record.NameOk && !TidyPins) name.SetActive(false);
                MapView.Badge(pin, record, large && shown);
                if (!shown) continue;
                var color = record.Tint;
                if (record.Emptied || record.FadedUntil > now) color.a *= Plugin.FadedOpacity;
                pin.m_iconElement.color = color;
                var text = pin.m_NamePinData?.PinNameText;
                if (text != null)
                {
                    // A portal's name in the colour picked for it in Portal List.
                    var named = record.IsPortal ? PortalLink.ColorOf(record) ?? Color.white : Color.white;
                    text.color = new Color(named.r, named.g, named.b, color.a);
                    NameSize(text);
                }
                if (!Mathf.Approximately(pin.m_uiElement.localScale.x, record.Scale)) pin.m_uiElement.localScale = new Vector3(record.Scale, record.Scale, 1f);
                if (!record.IsPortal) continue;
                // Portals on top of every other pin (the map draws pins in the order they were added), their names too.
                pin.m_uiElement.SetAsLastSibling();
                pin.m_NamePinData?.PinNameRectTransform?.SetAsLastSibling();
            }
            OtherPins(map);
            if (large) SideBySide(map);
        }

        // A portal and a place (or the map's own trader icon) that would overlap on the large map are drawn side by side,
        // the portal left, with only the portal's name, under the middle of both (a portal is mostly named after the
        // place: "Boss - The Queen"). Done after the game placed its pins, each time; zoomed in far enough they stand
        // apart by themselves and show as they are.
        private static readonly List<Minimap.PinData> PortalPins = new List<Minimap.PinData>(), PlacePins = new List<Minimap.PinData>();
        private static readonly HashSet<Minimap.PinData> Paired = new HashSet<Minimap.PinData>();

        private static float Width(RectTransform ui) => ui.rect.width * ui.localScale.x;

        private static void SideBySide(Minimap map)
        {
            PortalPins.Clear();
            PlacePins.Clear();
            Paired.Clear();
            foreach (var pair in ByPin)
            {
                var ui = pair.Key.m_uiElement;
                if (ui == null || !ui.gameObject.activeSelf) continue;
                if (pair.Value.IsPortal) PortalPins.Add(pair.Key);
                else if (Catalog.Get(pair.Value.Thing)?.Kind == Kind.Place) PlacePins.Add(pair.Key);
            }
            if (PortalPins.Count == 0) return;
            foreach (var pin in AllPins(map))
                if (pin.m_uiElement != null && pin.m_uiElement.gameObject.activeSelf && LocationIcon(pin)) PlacePins.Add(pin);
            foreach (var portal in PortalPins)
            {
                var left = portal.m_uiElement;
                var leftSize = Width(left);
                Minimap.PinData best = null;
                var bestDistance = float.MaxValue;
                foreach (var place in PlacePins)
                {
                    if (Paired.Contains(place)) continue;
                    var distance = Vector2.Distance(left.anchoredPosition, place.m_uiElement.anchoredPosition);
                    if (distance < (leftSize + Width(place.m_uiElement)) * 0.4f && distance < bestDistance)
                    {
                        best = place;
                        bestDistance = distance;
                    }
                }
                if (best == null) continue;
                Paired.Add(best);
                var right = best.m_uiElement;
                var rightSize = Width(right);
                var middle = (left.anchoredPosition + right.anchoredPosition) * 0.5f;
                const float gap = 2f;
                var start = middle.x - (leftSize + gap + rightSize) * 0.5f;
                left.anchoredPosition = new Vector2(start + leftSize * 0.5f, middle.y);
                right.anchoredPosition = new Vector2(start + leftSize + gap + rightSize * 0.5f, middle.y);
                var portalName = portal.m_NamePinData?.PinNameRectTransform;
                if (portalName != null) portalName.anchoredPosition = middle;
                if (!ByPin.TryGetValue(best, out var record)) continue;
                record.NameOk = false;
                var name = best.m_NamePinData?.PinNameGameObject;
                if (name != null && name.activeSelf && !TidyPins) name.SetActive(false);
            }
        }

        // The game's pins this mod hides: a vegvisir's boss pin where this mod has the altar's pin (with the boss's head),
        // and Epic Loot's bounty and treasure map pins (with their circles) while their filter button hides them.
        private static readonly HashSet<Minimap.PinData> HiddenGamePins = new HashSet<Minimap.PinData>();
        private static readonly List<Vector3> Bosses = new List<Vector3>(), Epic = new List<Vector3>();

        // The map's own icons of traders and the start get their size (the game draws them twice as big as a pin); the
        // pins hidden by this mod stay hidden, the others come back.
        private static void OtherPins(Minimap map)
        {
            var traderScale = Plugin.TraderSize / 2f;
            var epicHidden = MapView.EpicHidden;
            Bosses.Clear();
            Epic.Clear();
            foreach (var record in ByPin.Values)
                if (Catalog.IsBoss(Catalog.Get(record.Thing))) Bosses.Add(record.Pos);
            var all = AllPins(map);
            if (epicHidden)
                foreach (var pin in all)
                    if (!ByPin.ContainsKey(pin) && MapView.IsEpicPin(pin)) Epic.Add(pin.m_pos);
            foreach (var pin in all)
            {
                if (ByPin.ContainsKey(pin)) continue;
                if (pin.m_uiElement != null && LocationIcon(pin) && !Mathf.Approximately(pin.m_uiElement.localScale.x, traderScale))
                    pin.m_uiElement.localScale = new Vector3(traderScale, traderScale, 1f);
                var hide = pin.m_type == Minimap.PinType.Boss && Near(Bosses, pin.m_pos, 30f) ||
                           epicHidden && (MapView.IsEpicPin(pin) || pin.m_type == Minimap.PinType.EventArea && Near(Epic, pin.m_pos, 1f));
                if (hide) HiddenGamePins.Add(pin);
                else if (!HiddenGamePins.Remove(pin)) continue;
                if (pin.m_uiElement != null && pin.m_uiElement.gameObject.activeSelf != !hide) pin.m_uiElement.gameObject.SetActive(!hide);
                var name = pin.m_NamePinData?.PinNameGameObject;
                if (hide && name != null && name.activeSelf && !TidyPins) name.SetActive(false);
            }
        }

        private static bool Near(List<Vector3> spots, Vector3 pos, float radius)
        {
            foreach (var spot in spots)
                if (Utils.DistanceXZ(spot, pos) < radius) return true;
            return false;
        }

        // The size of a name under this mod's pins ("Name size" times the game's own).
        private static float _gameNameSize = -1f;
        private static readonly AccessTools.FieldRef<Minimap, GameObject> NamePrefab = AccessTools.FieldRefAccess<Minimap, GameObject>("m_pinNamePrefab");

        private static void NameSize(TMPro.TMP_Text text)
        {
            if (_gameNameSize < 0f)
            {
                var prefab = Minimap.instance != null ? NamePrefab(Minimap.instance) : null;
                var model = prefab != null ? prefab.GetComponentInChildren<TMPro.TMP_Text>(true) : null;
                _gameNameSize = model != null ? model.enableAutoSizing ? model.fontSizeMax : model.fontSize : 0f;
            }
            if (_gameNameSize <= 0f) return;
            var size = _gameNameSize * Plugin.NameSize;
            if (text.enableAutoSizing)
            {
                if (!Mathf.Approximately(text.fontSizeMax, size)) text.fontSizeMax = size;
            }
            else if (!Mathf.Approximately(text.fontSize, size)) text.fontSize = size;
        }

        // Each frame after the game's coroutines: the game shows a new pin name a second after it made it, also one this
        // mod hides (zoomed out, merged): hidden again before it is drawn.
        internal static void KeepNamesHidden()
        {
            var map = Minimap.instance;
            if (TidyPins || map == null || map.m_mode != Minimap.MapMode.Large || ByPin.Count == 0) return;
            foreach (var pair in ByPin)
            {
                var name = pair.Key.m_NamePinData?.PinNameGameObject;
                if (name != null && !pair.Value.NameOk && name.activeSelf) name.SetActive(false);
            }
            foreach (var pin in HiddenGamePins)
            {
                var name = pin.m_NamePinData?.PinNameGameObject;
                if (name != null && name.activeSelf) name.SetActive(false);
            }
        }

        // --- Tidy Pins -------------------------------------------------------------------------------------------

        // Tidy Pins (SirJose) sets the size, the icon's opacity and the name of every pin each frame, after the game and
        // after this mod. Right after it, this mod's pins (and the map's own trader icons) get their own size and fading
        // back, with Tidy Pins' growth under the mouse and its fading of crossed-off pins on top. Names are Tidy Pins'
        // then (shown under the mouse, all while its key is held), but never for pins this mod hides (zoom level, filter
        // buttons, merged) or the game's pins it hides; with Tidy Pins' hover names off, this mod's name settings apply.
        internal const string TidyPinsGuid = "com.burks.tidypins";
        internal static bool TidyPins { get; private set; }
        private static BepInEx.Configuration.ConfigEntry<float> _tidyLarge, _tidySmall, _tidyCrossed;
        private static BepInEx.Configuration.ConfigEntry<bool> _tidyHover;

        internal static void HookTidyPins(Harmony harmony)
        {
            TidyPins = false;
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(TidyPinsGuid, out var info) || info.Instance == null) return;
            var assembly = info.Instance.GetType().Assembly;
            var plugin = assembly.GetType("TidyPins.TidyPinsPlugin");
            var patch = assembly.GetType("TidyPins.PinsPatch");
            var apply = patch != null ? AccessTools.Method(patch, "Apply", new[] { typeof(Minimap) }) : null;
            _tidyLarge = TidySetting<float>(plugin, "IconScale");
            _tidySmall = TidySetting<float>(plugin, "MinimapIconScale");
            _tidyCrossed = TidySetting<float>(plugin, "CheckedAlpha");
            _tidyHover = TidySetting<bool>(plugin, "HoverNames");
            if (apply == null || _tidyLarge == null || _tidySmall == null || _tidyCrossed == null || _tidyHover == null)
            {
                Plugin.Log?.LogWarning("Tidy Pins is loaded, but not a version this mod knows: its sizes and names apply to this mod's pins as they are.");
                return;
            }
            harmony.Patch(apply, postfix: new HarmonyMethod(typeof(MapPins), nameof(AfterTidyPins)));
            TidyPins = true;
            Plugin.Log?.LogInfo("Tidy Pins found: it shows the names of this mod's pins under the mouse; their sizes and fading stay this mod's.");
        }

        internal static void UnhookTidyPins() => TidyPins = false;

        private static BepInEx.Configuration.ConfigEntry<T> TidySetting<T>(Type type, string field) =>
            type == null ? null : AccessTools.Field(type, field)?.GetValue(null) as BepInEx.Configuration.ConfigEntry<T>;

        // Tidy Pins' Apply is static and its parameter is called __instance, which Harmony keeps for the patched object:
        // the map comes as the first argument (__0).
        private static void AfterTidyPins(Minimap __0)
        {
            var map = __0;
            if (!TidyPins || map == null || Plugin.Waiting) return;
            var large = map.m_mode == Minimap.MapMode.Large;
            var tidy = Mathf.Clamp(large ? _tidyLarge.Value : _tidySmall.Value, 0.3f, 3f);
            var crossed = Mathf.Clamp01(_tidyCrossed.Value);
            var hoverNames = _tidyHover.Value;
            var now = ZNet.instance != null ? ZNet.instance.GetTime().Ticks : 0L;
            foreach (var pair in ByPin)
            {
                var pin = pair.Key;
                var record = pair.Value;
                var ui = pin.m_uiElement;
                if (ui == null) continue;
                var shown = ui.gameObject.activeSelf;
                if (!shown || !large || !hoverNames && !record.NameOk) NoName(pin);
                if (!shown || pin.m_iconElement == null) continue;
                var grow = ui.localScale.x / tidy; // Tidy Pins' growth under the mouse, else 1
                var scale = record.Scale * grow;
                if (!Mathf.Approximately(ui.localScale.x, scale)) ui.localScale = new Vector3(scale, scale, 1f);
                var color = record.Tint;
                if (record.Emptied || record.FadedUntil > now) color.a *= Plugin.FadedOpacity;
                if (pin.m_checked && grow < 1.01f) color.a *= crossed;
                pin.m_iconElement.color = color;
            }
            var traderScale = Plugin.TraderSize / 2f;
            foreach (var pin in AllPins(map))
            {
                if (pin == null || pin.m_uiElement == null || ByPin.ContainsKey(pin)) continue;
                if (HiddenGamePins.Contains(pin)) NoName(pin);
                else if (LocationIcon(pin))
                {
                    var scale = traderScale * pin.m_uiElement.localScale.x / tidy;
                    if (!Mathf.Approximately(pin.m_uiElement.localScale.x, scale)) pin.m_uiElement.localScale = new Vector3(scale, scale, 1f);
                }
            }
        }

        // Tidy Pins shows and hides names by the opacity of a CanvasGroup it puts on them.
        private static void NoName(Minimap.PinData pin)
        {
            var name = pin.m_NamePinData?.PinNameGameObject;
            if (name == null) return;
            var group = name.GetComponent<CanvasGroup>();
            if (group != null && group.alpha != 0f) group.alpha = 0f;
        }

        // Right-click and left-click find the closest pin the game saves; this mod's pins take part too.
        [HarmonyPatch(typeof(Minimap), "GetClosestPin")]
        [HarmonyPostfix]
        private static void Closest(Vector3 pos, float radius, bool mustBeVisible, ref Minimap.PinData __result)
        {
            if (!mustBeVisible || ByPin.Count == 0) return; // the cartography table's merge looks for saved pins only
            var best = __result;
            var bestDistance = best != null ? Utils.DistanceXZ(pos, best.m_pos) : float.MaxValue;
            foreach (var pin in ByPin.Keys)
            {
                if (pin.m_uiElement == null || !pin.m_uiElement.gameObject.activeInHierarchy) continue;
                var distance = Utils.DistanceXZ(pos, pin.m_pos);
                if (distance < radius && distance < bestDistance)
                {
                    best = pin;
                    bestDistance = distance;
                }
            }
            __result = best;
        }

        // --- Cartography tables --------------------------------------------------------------------------------

        private sealed class Lent
        {
            public Minimap.PinData Pin;
            public string Name;
            public Minimap.PinType Type;
        }

        private static readonly List<Lent> LentPins = new List<Lent>();

        // The name a pin has at a table, also when names are hidden on your map: its label and count, or a portal's tag.
        // A spawner says what it is ("Boar spawner"), so players without this mod know.
        private static string SharedName(Record record)
        {
            if (record.IsPortal) return record.Label ?? "";
            var thing = Catalog.Get(record.Thing);
            if (thing == null) return "";
            var label = thing.Kind == Kind.Spawner ? thing.Name
                : thing.Kind == Kind.Place || thing.Kind == Kind.Chest ? thing.Label : record.Label ?? thing.Label;
            return record.Count > 1 ? $"{label} x{record.Count}" : label;
        }

        [HarmonyPatch(typeof(Minimap), nameof(Minimap.GetSharedMapData))]
        [HarmonyPrefix]
        private static void LendPins()
        {
            LentPins.Clear();
            if (!Plugin.ShareAtTables) return;
            foreach (var pair in ByPin)
            {
                var pin = pair.Key;
                if (pair.Value.Thing == Catalog.MapChestId) continue; // nobody else can open it
                LentPins.Add(new Lent { Pin = pin, Name = pin.m_name, Type = pin.m_type });
                pin.m_save = true;
                pin.m_type = pair.Value.IsPortal ? Minimap.PinType.Icon4 : Minimap.PinType.Icon3;
                pin.m_name = SharedName(pair.Value);
            }
        }

        [HarmonyPatch(typeof(Minimap), nameof(Minimap.GetSharedMapData))]
        [HarmonyFinalizer]
        private static Exception ReturnPins(Exception __exception)
        {
            foreach (var lent in LentPins)
            {
                lent.Pin.m_save = false;
                lent.Pin.m_type = lent.Type;
                lent.Pin.m_name = lent.Name;
            }
            LentPins.Clear();
            return __exception;
        }

        private static HashSet<Minimap.PinData> _beforeTable;

        [HarmonyPatch(typeof(Minimap), nameof(Minimap.AddSharedMapData))]
        [HarmonyPrefix]
        private static void BeforeTable()
        {
            _beforeTable = Minimap.instance != null ? new HashSet<Minimap.PinData>(AllPins(Minimap.instance)) : null;
        }

        [HarmonyPatch(typeof(Minimap), nameof(Minimap.AddSharedMapData))]
        [HarmonyFinalizer]
        private static Exception AfterTable(Exception __exception)
        {
            var before = _beforeTable;
            _beforeTable = null;
            if (__exception != null || before == null || Minimap.instance == null || Records.Key == null) return __exception;
            try
            {
                var added = AllPins(Minimap.instance).Where(p => !before.Contains(p) && p.m_ownerID != 0L).ToList();
                var taken = 0;
                foreach (var pin in added)
                {
                    if (!Adopt(pin)) continue;
                    _internal = true;
                    try
                    {
                        Minimap.instance.RemovePin(pin);
                    }
                    finally
                    {
                        _internal = false;
                    }
                    taken++;
                }
                if (taken > 0) Plugin.Log?.LogInfo($"Cartography table: {taken} pins became auto pins (or were known already).");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("Could not take over the table's pins: " + e.Message);
            }
            return __exception;
        }

        private static readonly Regex Counted = new Regex(@"^(.*) x(\d+)$");

        // A pin read from a table that this mod knows: its own pin (new, or the one it has there already).
        private static bool Adopt(Minimap.PinData pin)
        {
            var name = pin.m_name ?? "";
            var count = 1;
            var match = Counted.Match(name);
            if (match.Success)
            {
                name = match.Groups[1].Value;
                int.TryParse(match.Groups[2].Value, out count);
            }
            if (pin.m_type == Minimap.PinType.Icon4) return AdoptPortal(pin);
            if (name.Length == 0 || Character.InInterior(pin.m_pos)) return false;
            var thing = Catalog.ForName(name) ?? (name.EndsWith(" spawner") ? Catalog.Get(Catalog.SpawnerId) : null);
            if (thing == null || thing.Kind == Kind.Portal) return false;
            if ((thing.Kind == Kind.Spawner ? Records.ClosestSpawner(pin.m_pos, thing.Spacing) : Records.Closest(pin.m_pos, thing.Spacing, thing.Id)) != null)
                return true;
            var record = new Record
            {
                Thing = thing.Id,
                Biome = WorldGenerator.instance != null ? WorldGenerator.instance.GetBiome(pin.m_pos) : Heightmap.Biome.Meadows,
                Pos = pin.m_pos,
                Count = Math.Max(1, count),
                Checked = pin.m_checked,
                Label = thing.Kind == Kind.Plant && name != thing.Label ? name : null, // a pickup's own name ("Bog Iron")
            };
            Records.Add(record);
            Refresh(record);
            return true;
        }

        // A portal pin from a table: this mod's pin of that portal, when the portal is known (the world's portal list),
        // else it stays a normal pin (someone's own pin with the portal icon).
        private static bool AdoptPortal(Minimap.PinData pin)
        {
            if (Records.Closest(pin.m_pos, 5f, Catalog.PortalId) != null) return true;
            var portal = ZDOMan.instance?.GetPortalList().FirstOrDefault(z => z != null && Utils.DistanceXZ(z.GetPosition(), pin.m_pos) < 5f);
            if (portal == null) return false;
            var record = Records.OfMember(portal.m_uid);
            if (record != null) return true;
            var at = portal.GetPosition();
            record = new Record
            {
                Thing = Catalog.PortalId,
                Biome = WorldGenerator.instance != null ? WorldGenerator.instance.GetBiome(at) : Heightmap.Biome.Meadows,
                Pos = at,
                Label = portal.GetString(ZDOVars.s_tag),
            };
            record.Members.Add(portal.m_uid);
            Records.Add(record);
            Refresh(record);
            return true;
        }

        // A pin of this mod removed by the player: deleted for good (until "Restore deleted pins"). Any other pin that
        // goes may uncover a place.
        [HarmonyPatch(typeof(Minimap), nameof(Minimap.RemovePin), typeof(Minimap.PinData))]
        [HarmonyPrefix]
        private static void Removing(Minimap.PinData pin)
        {
            if (pin != null) HiddenGamePins.Remove(pin);
            if (_internal || pin == null) return;
            if (!ByPin.TryGetValue(pin, out var record))
            {
                if (Marks(pin)) PlacesChanged = true;
                return;
            }
            record.Deleted = true;
            Records.Dirty = true;
            ByPin.Remove(pin);
            Pins.Remove(record);
        }

        // A pin of someone else added (yours, a table's, a vegvisir's): a place there hides its own.
        [HarmonyPatch(typeof(Minimap), nameof(Minimap.AddPin))]
        [HarmonyPostfix]
        private static void Added(Minimap.PinData __result)
        {
            // The map's own location icons get their double size only after this: an unsaved pin without a type may be one.
            if (!_internal && __result != null && (Marks(__result) || __result.m_type == Minimap.PinType.None && !__result.m_save))
                PlacesChanged = true;
        }

        // A pin that marks a place (saved, or a vegvisir's or Hildir's, or the map's own icon of a trader), not a player, a
        // ping or a death marker.
        private static bool Marks(Minimap.PinData pin)
        {
            var type = pin.m_type;
            return type == Minimap.PinType.Boss || type == Minimap.PinType.Hildir1 || type == Minimap.PinType.Hildir2 ||
                   type == Minimap.PinType.Hildir3 || pin.m_save && type != Minimap.PinType.Death && type != Minimap.PinType.Bed ||
                   LocationIcon(pin);
        }

        // The map's own icon of a place (Haldor, Hildir, the Bog Witch, the start): unsaved, double size, no type.
        internal static bool LocationIcon(Minimap.PinData pin) =>
            pin.m_type == Minimap.PinType.None && pin.m_doubleSize && !pin.m_save && !ByPin.ContainsKey(pin);
    }
}
