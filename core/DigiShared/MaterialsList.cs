// Shared by Ultimate Builder and Building Tweaks (each mod compiles this file in): the Build Materials window. Every
// material the given pieces cost, with four columns: the total, what you have (your inventory plus the chests within
// ChestRange: player-built ones you may open), in green when it is enough and in red when not, and how many are missing.
// Below that how many of each piece. Click a column title to sort by it, again to turn the order around (like the
// Cookbook); the search box keeps the materials and pieces whose name contains what you type. The footer has the total
// weight and the crafting stations the pieces need. What you have is counted once, when the window opens.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DigiShared
{
    internal static class MaterialsList
    {
        // Set by the host mod from its setting: how far away chests count for "Have".
        internal static float ChestRange = 50f;

        private const float Width = 880f, Height = 820f, RowHeight = 40f, ColumnWidth = 110f, HeaderY = 146f, ListTop = 184f;
        private static readonly Color Green = new Color(0.5f, 0.88f, 0.42f);
        private static readonly Color Red = new Color(1f, 0.45f, 0.38f);
        private static readonly string[] Titles = { "Material", "Total", "Have", "Missing" };
        private static readonly MethodInfo CheckAccess = AccessTools.Method(typeof(Container), "CheckAccess", new[] { typeof(long) });

        private enum Column { Name, Total, Have, Missing }

        // One line of the list: a material (with what you have) or a piece.
        private sealed class Line
        {
            public string Name;
            public Sprite Icon;
            public int Amount, Have;
            public int Missing => Mathf.Max(0, Amount - Have);
        }

        // The sort order stays for the game session.
        private static Column _sort = Column.Total;
        private static bool _descending = true;

        // The open window's lists and parts.
        private static List<Line> _materials = new List<Line>(), _pieces = new List<Line>();
        private static Windows.Window _window;
        private static string _query = "";
        private static readonly TextMeshProUGUI[] Headers = new TextMeshProUGUI[4];

        internal static string Number(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

        // The window for these pieces (prefab names, one per piece); back: opens the window it came from (the library).
        internal static void Show(string subtitle, List<string> prefabs, Action back = null)
        {
            var materials = new Dictionary<string, Line>();
            var pieces = new Dictionary<string, Line>();
            var stations = new SortedSet<string>();
            var weight = 0f;
            foreach (var name in prefabs)
            {
                var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(name) : null;
                var piece = prefab != null ? prefab.GetComponent<Piece>() : null;
                if (piece == null) continue;
                Add(pieces, name, Localization.instance.Localize(piece.m_name), piece.m_icon, 1);
                if (piece.m_craftingStation != null) stations.Add(Localization.instance.Localize(piece.m_craftingStation.m_name));
                if (piece.m_resources == null) continue;
                foreach (var req in piece.m_resources)
                {
                    if (req?.m_resItem == null || req.m_amount <= 0) continue;
                    var item = req.m_resItem.m_itemData;
                    // keyed by the item's own name: that is what inventories count by
                    Add(materials, item.m_shared.m_name, Localization.instance.Localize(item.m_shared.m_name), item.GetIcon(), req.m_amount);
                    weight += item.m_shared.m_weight * req.m_amount;
                }
            }
            var player = Player.m_localPlayer;
            var stores = Stores(player);
            foreach (var pair in materials)
                pair.Value.Have = (player != null ? player.GetInventory().CountItems(pair.Key) : 0) + stores.Sum(inv => inv.CountItems(pair.Key));
            _materials = materials.Values.ToList();
            _pieces = pieces.Values.ToList();

            var footer = "Total weight " + Number(Mathf.RoundToInt(weight)) +
                         (stations.Count > 0 ? "   ·   Needs " + string.Join(", ", stations) : "") +
                         $"   ·   Have: inventory + chests within {ChestRange:0} m";
            _window = Windows.Open("Build Materials", subtitle, Width, Height, footer, 2f * RowHeight, back, ListTop);
            if (_window == null) return;

            _query = "";
            var search = Windows.SearchBox(_window.Panel, Windows.Pad, 100f, 340f, 38f, "Search material or piece...");
            search.onValueChanged.AddListener(text =>
            {
                _query = (text ?? "").Trim();
                Fill();
            });
            _window.Search = search;

            var short_ = _materials.Count(l => l.Missing > 0);
            var summary = Windows.Text(_window.Panel,
                _materials.Count == 0 ? "" : short_ == 0 ? "You have everything" : $"Short of {short_} of {_materials.Count} materials",
                20f, short_ == 0 ? Green : Red, TextAlignmentOptions.Right);
            Windows.Place(summary.rectTransform, Windows.Pad + 360f, 104f, Width - 2 * Windows.Pad - 360f, 30f);

            // The column titles, lined up with the rows' columns; a click sorts.
            var header = Windows.Rect("Header", _window.Panel);
            Windows.Place(header, Windows.Pad, HeaderY, _window.RowWidth, 32f);
            for (var i = 0; i < Titles.Length; i++)
            {
                var column = (Column)i;
                var cell = Windows.Rect("Column " + Titles[i], header);
                if (i == 0) Windows.Place(cell, 12f, 0f, NameWidth(), 32f);
                else Windows.Place(cell, ColumnX(i), 0f, ColumnWidth, 32f);
                var hit = cell.gameObject.AddComponent<Image>();
                hit.color = new Color(0f, 0f, 0f, 0f);
                cell.gameObject.AddComponent<Button>().onClick.AddListener(() => SortBy(column));
                var text = Windows.Text(cell, Titles[i], 19f, Windows.Gold, i == 0 ? TextAlignmentOptions.Left : TextAlignmentOptions.Right);
                Windows.Stretch(text.rectTransform);
                Headers[i] = text;
            }
            Fill();
        }

        private static float NameWidth() => _window.RowWidth - 52f - 3 * ColumnWidth - 16f;

        // Where column i (1 Total, 2 Have, 3 Missing) starts in a row.
        private static float ColumnX(int i) => _window.RowWidth - (4 - i) * ColumnWidth - 12f;

        private static void SortBy(Column column)
        {
            if (_sort == column) _descending = !_descending;
            else
            {
                _sort = column;
                _descending = column != Column.Name; // names A-Z first, numbers highest first
            }
            Fill();
        }

        private static void Add(Dictionary<string, Line> lines, string key, string name, Sprite icon, int amount)
        {
            if (!lines.TryGetValue(key, out var line)) lines[key] = line = new Line { Name = name, Icon = icon };
            line.Amount += amount;
        }

        // Every chest in reach that you may open: built by a player, within ChestRange, access allowed; nearest first.
        internal static List<Container> Chests(Player player)
        {
            var result = new List<Container>();
            if (player == null || ChestRange <= 0f) return result;
            var origin = player.transform.position;
            var range = ChestRange * ChestRange;
            foreach (var chest in UnityEngine.Object.FindObjectsByType<Container>(FindObjectsSortMode.None))
            {
                if (chest == null || chest.GetInventory() == null || (chest.transform.position - origin).sqrMagnitude > range) continue;
                var piece = chest.GetComponentInParent<Piece>();
                if (piece == null || !piece.IsPlacedByPlayer()) continue;
                if (CheckAccess != null && !(bool)CheckAccess.Invoke(chest, new object[] { player.GetPlayerID() })) continue;
                result.Add(chest);
            }
            return result.OrderBy(c => (c.transform.position - origin).sqrMagnitude).ToList();
        }

        private static List<Inventory> Stores(Player player) => Chests(player).Select(c => c.GetInventory()).ToList();

        private static IEnumerable<Line> Sorted(IEnumerable<Line> lines, bool material)
        {
            Func<Line, int> number;
            switch (_sort)
            {
                case Column.Have: number = l => material ? l.Have : l.Amount; break;
                case Column.Missing: number = l => material ? l.Missing : l.Amount; break;
                default: number = l => l.Amount; break;
            }
            if (_sort == Column.Name)
                return _descending ? lines.OrderByDescending(l => l.Name, StringComparer.OrdinalIgnoreCase) : lines.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase);
            return _descending ? lines.OrderByDescending(number).ThenBy(l => l.Name) : lines.OrderBy(number).ThenBy(l => l.Name);
        }

        // The list again: after opening, sorting or typing in the search box.
        private static void Fill()
        {
            if (_window == null || _window.Content == null) return;
            for (var i = 0; i < Headers.Length; i++)
            {
                if (Headers[i] == null) continue;
                var active = (int)_sort == i;
                Headers[i].text = Titles[i] + (active ? (_descending ? " <size=70%>v</size>" : " <size=70%>^</size>") : "");
                Headers[i].color = active ? Color.white : Windows.Gold;
            }
            for (var i = _window.Content.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(_window.Content.GetChild(i).gameObject);
            bool Matches(Line l) => _query.Length == 0 || l.Name.IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0;
            var y = Section(0f, "Materials", true, Sorted(_materials.Where(Matches), true).ToList());
            y = Section(y + 12f, "Pieces", false, Sorted(_pieces.Where(Matches), false).ToList());
            Windows.Done(_window, y);
        }

        private static float Section(float y, string title, bool material, List<Line> lines)
        {
            var width = _window.RowWidth;
            var head = Windows.Rect("Heading", _window.Content);
            Windows.Place(head, 0f, y, width, 34f);
            Windows.Place(Windows.Text(head, title, 22f, Windows.Gold, TextAlignmentOptions.Left).rectTransform, 12f, 2f, NameWidth(), 30f);
            if (!material)
                Windows.Place(Windows.Text(head, "Count", 19f, Windows.Gold, TextAlignmentOptions.Right).rectTransform, ColumnX(1), 4f, ColumnWidth, 28f);
            var underline = Windows.Rect("Line", head);
            Windows.Place(underline, 8f, 33f, width - 16f, 1f);
            underline.gameObject.AddComponent<Image>().color = new Color(Windows.Gold.r, Windows.Gold.g, Windows.Gold.b, 0.4f);
            y += 36f;
            if (lines.Count == 0)
            {
                var none = Windows.Rect("None", _window.Content);
                Windows.Place(none, 0f, y, width, RowHeight);
                Windows.Place(Windows.Text(none, _query.Length > 0 ? "nothing matches" : "nothing", 19f, Windows.Dim, TextAlignmentOptions.Left).rectTransform,
                    52f, 6f, width - 60f, 28f);
                return y + RowHeight;
            }
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                var row = Windows.Rect("Row", _window.Content);
                Windows.Place(row, 0f, y, width, RowHeight);
                row.gameObject.AddComponent<Image>().color = i % 2 == 0 ? Windows.RowEven : Windows.RowOdd;
                if (line.Icon != null) Windows.Picture(row, line.Icon, 10f, 4f, 32f, 32f);
                Windows.Place(Windows.Text(row, line.Name, 20f, Windows.Body, TextAlignmentOptions.Left).rectTransform, 52f, 6f, NameWidth(), 28f);
                Windows.Place(Windows.Text(row, Number(line.Amount), 20f, Windows.Body, TextAlignmentOptions.Right).rectTransform,
                    ColumnX(1), 6f, ColumnWidth, 28f);
                if (material)
                {
                    var enough = line.Missing == 0;
                    Windows.Place(Windows.Text(row, Number(line.Have), 20f, enough ? Green : Red, TextAlignmentOptions.Right).rectTransform,
                        ColumnX(2), 6f, ColumnWidth, 28f);
                    Windows.Place(Windows.Text(row, enough ? "-" : Number(line.Missing), 20f, enough ? Windows.Dim : Red, TextAlignmentOptions.Right).rectTransform,
                        ColumnX(3), 6f, ColumnWidth, 28f);
                }
                y += RowHeight;
            }
            return y;
        }
    }
}
