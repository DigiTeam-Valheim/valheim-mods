// Auto Map Pins, your own icons: PNG files in BepInEx/config/DigiAutoMapPins/Icons are offered in the icon picker (F1),
// after the game icons and before the white ones, and a pin shows them like any other icon (the choice is saved as
// "file:<file name>"). A picture without colour (white or grey, like the white icons) is drawn in the thing's colour; a
// coloured one as it is. The folder is read when a pin first needs it and again about once a second while a picker is
// open, so files can be added, changed or removed while the game runs. Kept in the config folder: a mod update (which
// replaces the plugin folder) leaves them alone.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace AutoMapPins
{
    internal static class CustomIcons
    {
        internal const string Prefix = "file:";
        private const int MaxSide = 1024;

        internal static string Folder => Path.Combine(Records.Folder, "Icons");

        private sealed class Icon
        {
            public Sprite Sprite;
            public bool Plain;
            public DateTime Stamp;
        }

        private static readonly Dictionary<string, Icon> ByName = new Dictionary<string, Icon>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> Order = new List<string>();
        private static readonly HashSet<string> Failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // name|stamp, warned once
        private static bool _read;
        private static float _scanned = -10f;

        // ImageConversion.LoadImage, found at runtime (as in WhiteIcons.cs).
        private static readonly System.Reflection.MethodInfo LoadImage = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule")
            ?.GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]) });

        // Every icon's name (the file name without .png), A - Z.
        internal static IList<string> All
        {
            get
            {
                if (!_read) Rescan();
                return Order;
            }
        }

        internal static bool IsFile(string choice) => choice != null && choice.StartsWith(Prefix, StringComparison.Ordinal);

        // The picture of a choice "file:<name>" and whether it is drawn in the thing's colour; null when the choice is not
        // a file or the file is not there (any more).
        internal static Sprite Get(string choice, out bool plain)
        {
            plain = false;
            if (!IsFile(choice)) return null;
            if (!_read) Rescan();
            if (!ByName.TryGetValue(choice.Substring(Prefix.Length), out var icon)) return null;
            plain = icon.Plain;
            return icon.Sprite;
        }

        internal static bool Plain(string name) => ByName.TryGetValue(name, out var icon) && icon.Plain;

        internal static Sprite Sprite(string name) => ByName.TryGetValue(name, out var icon) ? icon.Sprite : null;

        // While a picker is open: the folder again, at most once a second; pins redrawn when something changed.
        internal static void Watch()
        {
            if (Time.realtimeSinceStartup - _scanned < 1f) return;
            if (Rescan()) MapPins.RefreshAll();
        }

        // Reads new and changed files and drops removed ones; true when anything changed.
        internal static bool Rescan()
        {
            _read = true;
            _scanned = Time.realtimeSinceStartup;
            var changed = false;
            try
            {
                var folder = Folder;
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder); // there to be found
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.GetFiles(folder, "*.png"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
                    var stamp = File.GetLastWriteTimeUtc(file);
                    ByName.TryGetValue(name, out var known);
                    if (known != null && known.Stamp == stamp) continue;
                    var icon = Read(file, name, stamp);
                    if (known != null)
                    {
                        Free(known);
                        ByName.Remove(name);
                        changed = true;
                    }
                    if (icon == null) continue;
                    ByName[name] = icon;
                    changed = true;
                }
                foreach (var gone in ByName.Keys.Where(k => !seen.Contains(k)).ToList())
                {
                    Free(ByName[gone]);
                    ByName.Remove(gone);
                    changed = true;
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("Could not read your icons folder: " + e.Message);
            }
            if (changed || Order.Count != ByName.Count)
            {
                Order.Clear();
                Order.AddRange(ByName.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
            }
            return changed;
        }

        private static Icon Read(string file, string name, DateTime stamp)
        {
            var key = name + "|" + stamp.Ticks;
            Texture2D loaded = null;
            try
            {
                if (LoadImage == null) return null;
                loaded = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!(LoadImage.Invoke(null, new object[] { loaded, File.ReadAllBytes(file) }) is bool ok) || !ok)
                    throw new Exception("not a PNG picture");
                if (loaded.width > MaxSide || loaded.height > MaxSide)
                    throw new Exception($"{loaded.width} x {loaded.height} pixels, at most {MaxSide} x {MaxSide} (64 to 256 is plenty)");
                // A copy with mipmaps: the map draws pins small, and without them a big picture turns grainy.
                var pixels = loaded.GetPixels32();
                var texture = new Texture2D(loaded.width, loaded.height, TextureFormat.RGBA32, true)
                    { name = "Auto Map Pins icon " + name, filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp };
                texture.SetPixels32(pixels);
                texture.Apply(true, false);
                var sprite = UnityEngine.Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
                sprite.name = name;
                return new Icon { Sprite = sprite, Plain = Colourless(pixels), Stamp = stamp };
            }
            catch (Exception e)
            {
                if (Failed.Add(key)) Plugin.Log?.LogWarning($"Icon \"{Path.GetFileName(file)}\" not used: {e.Message}");
                return null;
            }
            finally
            {
                if (loaded != null) UnityEngine.Object.Destroy(loaded);
            }
        }

        // Without colour: hardly any of its visible pixels is coloured (white, grey or black only).
        private static bool Colourless(Color32[] pixels)
        {
            int visible = 0, coloured = 0;
            foreach (var p in pixels)
            {
                if (p.a < 80) continue;
                visible++;
                var max = Math.Max(p.r, Math.Max(p.g, p.b));
                var min = Math.Min(p.r, Math.Min(p.g, p.b));
                if (max - min > 30) coloured++;
            }
            return visible > 0 && coloured <= visible / 33;
        }

        private static void Free(Icon icon)
        {
            if (icon?.Sprite == null) return;
            var texture = icon.Sprite.texture;
            UnityEngine.Object.Destroy(icon.Sprite);
            if (texture != null) UnityEngine.Object.Destroy(texture);
        }
    }
}
