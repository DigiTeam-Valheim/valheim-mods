// Shared by Ultimate Builder and Building Tweaks (each mod compiles this file in): the tool icons made by
// make_creative_icons.py, one style for all (a gold pictogram with a dark outline and a label). A sheet of 128 px cells,
// 4 a row (<base>.png, embedded) and a list (<base>.txt: "id x y size" per cell). A cell with a size gets a picture
// drawn under it there: Wood Swap's icons get that wood's own piece from the game (copied off the graphics card, since
// the game's icons cannot be read directly), so the game's art is not shipped.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace DigiShared
{
    internal sealed class IconSheet
    {
        private const int Cell = 128, Columns = 4;
        private readonly Assembly _assembly;
        private readonly string _resource;
        private readonly Action<string> _warn;
        private Texture2D _sheet;
        private readonly Dictionary<string, int[]> _cells = new Dictionary<string, int[]>(); // id -> index, x, y, size
        private bool _loaded;

        // PNG reading lives in a module built for a newer .NET Standard than these mods compile against (as in Auto Map Pins).
        private static readonly MethodInfo LoadImage = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule")
            ?.GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]) });

        private static readonly MethodInfo EncodeToPng = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule")
            ?.GetMethod("EncodeToPNG", new[] { typeof(Texture2D) });

        internal static bool LoadPng(Texture2D texture, byte[] png) =>
            LoadImage != null && LoadImage.Invoke(null, new object[] { texture, png }) is bool ok && ok;

        internal static byte[] EncodePng(Texture2D texture) => EncodeToPng?.Invoke(null, new object[] { texture }) as byte[];

        // resource: the embedded files' name without .png / .txt, e.g. "DigiCreative.CreativeIcons".
        internal IconSheet(Assembly assembly, string resource, Action<string> warn)
        {
            _assembly = assembly;
            _resource = resource;
            _warn = warn;
        }

        private void Init()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                byte[] png;
                using (var stream = _assembly.GetManifestResourceStream(_resource + ".png"))
                using (var memory = new MemoryStream())
                {
                    if (stream == null) return;
                    stream.CopyTo(memory);
                    png = memory.ToArray();
                }
                string list;
                using (var stream = _assembly.GetManifestResourceStream(_resource + ".txt"))
                {
                    if (stream == null) return;
                    using (var reader = new StreamReader(stream)) list = reader.ReadToEnd();
                }
                var sheet = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!LoadPng(sheet, png)) return;
                _sheet = sheet;
                var index = 0;
                foreach (var line in list.Split('\n'))
                {
                    var f = line.Trim().Split(' ');
                    if (f.Length < 4) continue;
                    _cells[f[0]] = new[] { index++, int.Parse(f[1]), int.Parse(f[2]), int.Parse(f[3]) };
                }
            }
            catch (Exception e)
            {
                _warn?.Invoke("Tool icons not loaded: " + e.Message);
            }
        }

        // The icon with this id; under: the picture for its inner square, if it has one. Null when there is no cell for it.
        internal Sprite For(string id, Sprite under)
        {
            Init();
            if (_sheet == null || !_cells.TryGetValue(id, out var cell)) return null;
            try
            {
                var col = cell[0] % Columns;
                var row = cell[0] / Columns;
                var pixels = _sheet.GetPixels(col * Cell, _sheet.height - (row + 1) * Cell, Cell, Cell); // bottom row first
                var size = cell[3];
                var picture = size > 0 && under != null ? Readable(under) : null;
                if (picture != null)
                {
                    for (var py = 0; py < size; py++)
                        for (var px = 0; px < size; px++)
                        {
                            var x = cell[1] + px;
                            var y = cell[2] + py; // from the top
                            if (x < 0 || x >= Cell || y < 0 || y >= Cell) continue;
                            var below = picture.GetPixelBilinear((px + 0.5f) / size, 1f - (py + 0.5f) / size);
                            var i = (Cell - 1 - y) * Cell + x;
                            pixels[i] = Over(pixels[i], below);
                        }
                    UnityEngine.Object.Destroy(picture);
                }
                var texture = new Texture2D(Cell, Cell, TextureFormat.RGBA32, false) { name = "Digi icon " + id, wrapMode = TextureWrapMode.Clamp };
                texture.SetPixels(pixels);
                texture.Apply(false, true);
                return Sprite.Create(texture, new Rect(0, 0, Cell, Cell), new Vector2(0.5f, 0.5f), 100f);
            }
            catch (Exception e)
            {
                _warn?.Invoke($"Icon {id}: {e.Message}");
                return null;
            }
        }

        // top over bottom (straight alpha).
        private static Color Over(Color top, Color bottom)
        {
            var a = top.a + bottom.a * (1f - top.a);
            if (a <= 0f) return new Color(0f, 0f, 0f, 0f);
            var c = (top * top.a + bottom * bottom.a * (1f - top.a)) / a;
            c.a = a;
            return c;
        }

        // A readable copy of a sprite's picture: drawn on the graphics card into a texture, then read back.
        private static Texture2D Readable(Sprite sprite)
        {
            var source = sprite.texture;
            if (source == null) return null;
            var rect = sprite.textureRect;
            int w = Mathf.Max(1, Mathf.RoundToInt(rect.width)), h = Mathf.Max(1, Mathf.RoundToInt(rect.height));
            var target = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, target, new Vector2(rect.width / source.width, rect.height / source.height),
                    new Vector2(rect.x / source.width, rect.y / source.height));
                RenderTexture.active = target;
                var copy = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
                copy.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                copy.Apply();
                return copy;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
            }
        }
    }
}
