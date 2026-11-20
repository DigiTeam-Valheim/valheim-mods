// Shared by Building Tweaks (its Schematics sub-mod) and Ultimate Builder (each mod compiles this file in): the saved
// schematics, read from BepInEx\config\DigiCreative\Schematics (<name>.schematic, plain text, and <name>.png), and the
// library window that lists them (Windows.cs): each schematic with its picture, piece count, size and date, and the
// buttons the mod gives each row. Building Tweaks shows Materials only (normal worlds: nothing is placed for free);
// Ultimate Builder's creative world also places them (its own Schematics.cs, which saves them too).
// File: a header (name=, saved=, pieces=, size= x y z, ground=), then one line per piece:
// prefab;x;y;z;qx;qy;qz;qw (position and turn from the middle of the building's bottom).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DigiShared
{
    internal static class SchematicLibrary
    {
        internal const float RowHeight = 116f;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly Dictionary<string, Sprite> Pictures = new Dictionary<string, Sprite>();
        internal static BepInEx.Logging.ManualLogSource Log;

        internal sealed class Entry
        {
            public string Prefab;
            public Vector3 Pos;
            public Quaternion Rot;
        }

        internal sealed class Schematic
        {
            public string Name, File, Saved = "";
            public int Count;
            public Vector3 Size;
            public float Ground; // how deep the bottom was below the ground where it was saved (in the ground: positive)
            public readonly List<Entry> Pieces = new List<Entry>();
        }

        internal static string Folder => Path.Combine(BepInEx.Paths.ConfigPath, "DigiCreative", "Schematics");

        // The header only (name, date, count, size), or with every piece.
        internal static Schematic Load(string file, bool pieces)
        {
            var s = new Schematic { File = file, Name = Path.GetFileNameWithoutExtension(file) };
            foreach (var raw in System.IO.File.ReadLines(file))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                if (line.IndexOf(';') < 0)
                {
                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = line.Substring(0, eq).Trim();
                    var value = line.Substring(eq + 1).Trim();
                    if (key == "name" && value.Length > 0) s.Name = value;
                    else if (key == "saved") s.Saved = value;
                    else if (key == "pieces") int.TryParse(value, NumberStyles.Integer, Inv, out s.Count);
                    else if (key == "ground") s.Ground = P(value);
                    else if (key == "size")
                    {
                        var v = value.Split(' ');
                        if (v.Length == 3) s.Size = new Vector3(P(v[0]), P(v[1]), P(v[2]));
                    }
                    continue;
                }
                if (!pieces) break; // the pieces come after the header
                var f = line.Split(';');
                if (f.Length < 8 || f[0].Length == 0) continue;
                var rot = new Quaternion(P(f[4]), P(f[5]), P(f[6]), P(f[7]));
                s.Pieces.Add(new Entry
                {
                    Prefab = f[0], Pos = new Vector3(P(f[1]), P(f[2]), P(f[3])),
                    Rot = rot.x == 0f && rot.y == 0f && rot.z == 0f && rot.w == 0f ? Quaternion.identity : rot.normalized,
                });
            }
            if (pieces) s.Count = s.Pieces.Count;
            return s;
        }

        private static float P(string s) => float.TryParse(s, NumberStyles.Float, Inv, out var v) ? v : 0f;

        // Every saved schematic (headers only), by name.
        internal static List<Schematic> All()
        {
            var list = new List<Schematic>();
            if (!Directory.Exists(Folder)) return list;
            foreach (var file in Directory.GetFiles(Folder, "*.schematic"))
            {
                try { list.Add(Load(file, false)); }
                catch (Exception e) { Log?.LogWarning($"Could not read {file}: {e.Message}"); }
            }
            return list.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // With every piece; null (and a message) when the file cannot be read.
        internal static Schematic Full(Schematic s)
        {
            try { return Load(s.File, true); }
            catch (Exception e)
            {
                Log?.LogError($"Could not read {s.File}: {e}");
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Could not read that schematic (see the log)");
                return null;
            }
        }

        internal static Sprite PictureOf(string file)
        {
            var png = Path.ChangeExtension(file, ".png");
            if (!System.IO.File.Exists(png)) return null;
            var key = png + "|" + System.IO.File.GetLastWriteTimeUtc(png).Ticks;
            if (Pictures.TryGetValue(key, out var sprite) && sprite != null) return sprite;
            var texture = new Texture2D(2, 2);
            if (!IconSheet.LoadPng(texture, System.IO.File.ReadAllBytes(png)))
            {
                UnityEngine.Object.Destroy(texture);
                return null;
            }
            sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));
            Pictures[key] = sprite;
            return sprite;
        }

        // The materials window for a schematic (Total, Have, Missing); Back returns to where it came from.
        internal static void ShowMaterials(Schematic s, Action back)
        {
            var full = Full(s);
            if (full == null) return;
            MaterialsList.Show($"Schematic {full.Name}: {MaterialsList.Number(full.Pieces.Count)} pieces",
                full.Pieces.Select(e => e.Prefab).ToList(), back);
        }

        // The library window. top: draws something above the list (in the content, at the given width) and returns its
        // height; buttons: adds a row's buttons (the row, its schematic, the row width; the space below the info line,
        // from x 124, y 70, is theirs).
        internal static void Open(string footer, Func<Windows.Window, float, float> top, Action<RectTransform, Schematic, float> buttons)
        {
            var list = All();
            var w = Windows.Open("Schematics", list.Count == 0 ? "None saved yet" : list.Count == 1 ? "1 saved" : $"{list.Count} saved",
                900f, 820f, footer, RowHeight);
            if (w == null) return;
            var width = w.RowWidth;
            var y = top != null ? top(w, width) : 0f;
            if (list.Count == 0)
            {
                var none = Windows.Rect("None", w.Content);
                Windows.Place(none, 0f, y, width, 80f);
                Windows.Place(Windows.Text(none, "No schematics yet: save a building with Ultimate Builder's Save Schematic tool (in its creative world).",
                    19f, Windows.Dim, TextAlignmentOptions.Left).rectTransform, 16f, 24f, width - 32f, 30f);
                y += 80f;
            }
            for (var i = 0; i < list.Count; i++)
            {
                var s = list[i];
                var row = Windows.Rect("Row", w.Content);
                Windows.Place(row, 0f, y, width, RowHeight);
                row.gameObject.AddComponent<Image>().color = i % 2 == 0 ? Windows.RowEven : Windows.RowOdd;
                Windows.Picture(row, PictureOf(s.File), 8f, 8f, 100f, 100f);
                Windows.Place(Windows.Text(row, s.Name, 24f, Windows.Gold, TextAlignmentOptions.Left).rectTransform, 124f, 8f, width - 140f, 32f);
                var info = $"{MaterialsList.Number(s.Count)} pieces   ·   {s.Size.x:0} x {s.Size.z:0} m, {s.Size.y:0} m high" +
                           (s.Saved.Length > 0 ? "   ·   saved " + s.Saved : "");
                Windows.Place(Windows.Text(row, info, 18f, Windows.Dim, TextAlignmentOptions.Left).rectTransform, 124f, 40f, width - 140f, 26f);
                buttons?.Invoke(row, s, width);
                y += RowHeight;
            }
            Windows.Done(w, y);
        }
    }
}
