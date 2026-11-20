// Auto Map Pins, the list of kinds (the last button of the filter bar on the large map): every kind of pin, under the
// title of its filter button, with its icon, the biomes it is found in and how many pins of it this world has. Its
// button is the kind's switches of F1, all biomes at once: On, Off, or Partly (on in some biomes; a click turns it on
// everywhere). Off hides its pins (they stay saved) and makes no new ones, as in F1. A search box finds a kind by name.
// Kinds rarely worth a pin (F1's Advanced settings) are listed once this world has a pin of them.
// It opens over the map, on its left, so the map shows each change at once; it closes with the map.
// Light: built when it opens, thrown away when it closes.
using System;
using System.Collections.Generic;
using System.Linq;
using DigiShared;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace AutoMapPins
{
    internal static class PinList
    {
        private const float Width = 760f, Height = 820f, RowHeight = 46f, HeaderHeight = 40f, ListTop = 156f;
        private static Windows.Window _window;
        private static string _query = "";

        internal static bool IsOpen => _window != null && Windows.IsOpen && Windows.Current == _window;

        internal static void OpenFromMap(Minimap map)
        {
            if (IsOpen)
            {
                Windows.Close();
                return;
            }
            var w = Windows.Open(Lang.T("Kinds of pins"), Lang.T("Each kind on or off, in all biomes at once (as in F1)."),
                Width, Height, Lang.T("Off: its pins hidden, none added. Partly: on in some biomes. Esc closes."),
                RowHeight * 2f, null, ListTop);
            if (w == null) return;
            w.OverMap = true;
            _window = w;
            // On the left of the map, so the map stays in view.
            var canvas = w.Panel.GetComponentInParent<Canvas>();
            var half = canvas != null ? ((RectTransform)canvas.transform).rect.width / 2f : 960f;
            w.Panel.anchoredPosition = new Vector2(-Mathf.Max(0f, half - Width / 2f - 70f), 0f);
            var search = Windows.SearchBox(w.Panel, Windows.Pad, 104f, 340f, 38f, Lang.T("Search a kind..."));
            w.Search = search; // while typing in it, no key reaches the game (M would close the map)
            search.text = _query;
            search.onValueChanged.AddListener(text =>
            {
                _query = text ?? "";
                Fill();
            });
            Fill();
        }

        private static void Fill()
        {
            var w = _window;
            if (w == null || w.Content == null) return;
            for (var i = w.Content.childCount - 1; i >= 0; i--) Object.Destroy(w.Content.GetChild(i).gameObject);
            var counts = new Dictionary<string, int>();
            foreach (var record in Records.All)
            {
                if (record.Deleted || record.IsPortal || record.Thing == null) continue;
                counts.TryGetValue(record.Thing, out var n);
                counts[record.Thing] = n + 1;
            }
            var query = _query.Trim();
            var y = 0f;
            for (var filter = 1; filter < MapView.Filters; filter++)
            {
                var kinds = Catalog.ById.Values
                    .Where(t => t.Kind != Kind.Portal && MapView.FilterOf(t) == filter && (!t.Advanced || counts.ContainsKey(t.Id)) &&
                                t.Slots.Any(s => s != null)) // in no biome's section: nothing to switch
                    .Select(t => new Entry { Thing = t, Name = NameOf(t) })
                    .Where(e => query.Length == 0 || e.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                if (kinds.Count == 0) continue;
                Header(w, filter, y);
                y += HeaderHeight;
                var odd = false;
                foreach (var entry in kinds)
                {
                    counts.TryGetValue(entry.Thing.Id, out var count);
                    Row(w, entry, count, y, odd);
                    y += RowHeight;
                    odd = !odd;
                }
            }
            if (y == 0f)
                Windows.Place(Windows.Text(w.Content, Lang.T("Nothing found"), 20f, Windows.Dim, TextAlignmentOptions.Left).rectTransform, 12f, 8f, w.RowWidth - 24f, 32f);
            Windows.Done(w, Mathf.Max(y, 40f));
        }

        private sealed class Entry
        {
            public Thing Thing;
            public string Name;
        }

        private static void Header(Windows.Window w, int filter, float y)
        {
            var row = Windows.Rect("Group", w.Content);
            Windows.Place(row, 0f, y, w.RowWidth, HeaderHeight);
            var icon = Windows.Picture(row, WhiteIcons.Get(MapView.FilterIcon(filter)), 10f, 7f, 26f, 26f);
            icon.color = Windows.Gold;
            Windows.Place(Windows.Text(row, Lang.T(MapView.FilterTitle(filter)), 22f, Windows.Gold, TextAlignmentOptions.Left).rectTransform,
                46f, 0f, w.RowWidth - 60f, HeaderHeight);
        }

        private static void Row(Windows.Window w, Entry entry, int count, float y, bool odd)
        {
            var thing = entry.Thing;
            var row = Windows.Rect("Kind", w.Content);
            Windows.Place(row, 0f, y, w.RowWidth, RowHeight);
            row.gameObject.AddComponent<Image>().color = odd ? Windows.RowOdd : Windows.RowEven;
            var icon = Windows.Picture(row, null, 12f, 6f, 34f, 34f);
            Look(thing, icon);
            var name = Windows.Text(row, entry.Name, 21f, Windows.Body, TextAlignmentOptions.Left);
            name.overflowMode = TextOverflowModes.Ellipsis;
            Windows.Place(name.rectTransform, 56f, 0f, 250f, RowHeight);
            var biomes = Windows.Text(row, Biomes(thing), 16f, Windows.Dim, TextAlignmentOptions.Left);
            biomes.overflowMode = TextOverflowModes.Ellipsis;
            Windows.Place(biomes.rectTransform, 312f, 0f, 170f, RowHeight);
            Windows.Place(Windows.Text(row, count > 0 ? count.ToString() : "", 17f, Windows.Dim, TextAlignmentOptions.Right).rectTransform,
                w.RowWidth - 186f, 0f, 60f, RowHeight);
            Button button = null;
            button = Windows.Button(row, State(thing), w.RowWidth - 118f, 6f, 110f, RowHeight - 12f, () =>
            {
                Click(thing);
                Windows.SetLabel(button, State(thing));
            });
        }

        // The icon its pins have (by the icon style and its choice in F1), in its colour when it is a white one.
        private static void Look(Thing thing, Image image)
        {
            var biome = FirstBiome(thing);
            if (!thing.IconTried) Icons.Prepare(thing);
            var sprite = Icons.Resolve(thing, biome, false, null, out var tinted);
            image.sprite = sprite;
            image.color = sprite == null ? new Color(0f, 0f, 0f, 0.3f) : tinted ? Opaque(Catalog.ColorOf(biome, thing)) : Color.white;
        }

        private static Color Opaque(Color c) => new Color(c.r, c.g, c.b, 1f);

        private static Heightmap.Biome FirstBiome(Thing thing)
        {
            for (var i = 0; i < Catalog.Biomes.Length; i++)
                if (thing.Slots[i] != null) return Catalog.Biomes[i];
            return Heightmap.Biome.Meadows;
        }

        private static string Biomes(Thing thing)
        {
            var names = new List<string>();
            for (var i = 0; i < Catalog.Biomes.Length; i++)
                if (thing.Slots[i] != null) names.Add(BiomeName(Catalog.Biomes[i]));
            return names.Count <= 2 ? string.Join(", ", names) : names[0] + $" +{names.Count - 1}";
        }

        // The game's own name of a biome (in the game's language).
        private static string BiomeName(Heightmap.Biome biome)
        {
            var token = "$biome_" + biome.ToString().ToLowerInvariant();
            var text = Localization.instance != null ? Localization.instance.Localize(token) : token;
            return string.IsNullOrEmpty(text) || text.StartsWith("[", StringComparison.Ordinal) ? Catalog.BiomeName(biome) : text;
        }

        // What its pins say, in the game's language where the game has a name for it.
        private static string NameOf(Thing thing)
        {
            var label = thing.Label ?? thing.Name ?? thing.Id;
            if (Localization.instance == null || !label.Contains("$")) return label;
            var text = Localization.instance.Localize(label);
            return string.IsNullOrEmpty(text) || text.StartsWith("[", StringComparison.Ordinal) ? thing.Name ?? label : text;
        }

        // Its F1 switches: how many there are and how many are on.
        private static void Switches(Thing thing, out int total, out int on)
        {
            total = on = 0;
            foreach (var slot in thing.Slots)
            {
                if (slot?.Toggle == null) continue;
                total++;
                if (slot.Toggle.Value) on++;
            }
        }

        private static string State(Thing thing)
        {
            Switches(thing, out var total, out var on);
            return on == 0 ? Lang.T("Off") : on == total ? Lang.T("On") : Lang.T("Partly");
        }

        // On -> off everywhere; off or partly -> on everywhere.
        private static void Click(Thing thing)
        {
            Switches(thing, out var total, out var on);
            var value = on < total;
            foreach (var slot in thing.Slots)
                if (slot?.Toggle != null) slot.Toggle.Value = value;
        }
    }
}
