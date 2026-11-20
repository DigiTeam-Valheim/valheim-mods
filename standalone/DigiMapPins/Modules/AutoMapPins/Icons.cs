// Auto Map Pins, icons: the game's own icon for each thing (the item it gives, a trophy for a place, the piece for a
// chest). The white icon style uses WhiteIcons.cs, or a white copy of the game icon for a thing without a white icon;
// its default colour per thing is the game icon's own average colour, made bright (worked out once, from the icon's
// own part of its texture only, and kept in the saved list). White copies are only made when one is needed. Per thing (and for portals) an icon can be picked instead. Also draws both icons beside
// each switch in F1, with the colour editor and the icon picker.
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AutoMapPins
{
    internal static class Icons
    {
        private static readonly Dictionary<Sprite, Sprite> WhiteCopies = new Dictionary<Sprite, Sprite>();
        private static readonly Dictionary<Sprite, Color> Averages = new Dictionary<Sprite, Color>();
        private static readonly System.Reflection.MethodInfo GetSprite = AccessTools.Method(typeof(Minimap), "GetSprite");

        // A thing's game icon; a thing without one shows its white icon (never the map's plain round pin).
        internal static void Prepare(Thing thing)
        {
            thing.Icon = Find(thing.IconItem);
            thing.IconTried = true;
            if (thing.ColorKnown) return;
            thing.AutoColor = thing.Icon != null ? Average(thing.Icon) : Color.white;
            thing.ColorKnown = true;
        }

        private static readonly Dictionary<string, Sprite> Found = new Dictionary<string, Sprite>();

        // An item's icon, a piece's (the wooden chest) or one of the map's own location icons ("loc:Vendor_BlackForest").
        internal static Sprite Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (name.StartsWith("loc:", System.StringComparison.Ordinal))
            {
                if (Minimap.instance == null) return null;
                foreach (var data in Minimap.instance.m_locationIcons)
                    if (data.m_name == name.Substring(4)) return data.m_icon;
                return null;
            }
            var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(name) : null;
            var item = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (item != null) return item.m_itemData.GetIcon();
            prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(name) : null;
            var piece = prefab != null ? prefab.GetComponent<Piece>() : null;
            return piece != null ? piece.m_icon : null;
        }

        // The map's own (white) pin icons.
        internal static Sprite PinSprite(Minimap.PinType type)
        {
            if (Minimap.instance == null || GetSprite == null) return null;
            return GetSprite.Invoke(Minimap.instance, new object[] { type }) as Sprite;
        }

        internal static Sprite White(Sprite sprite)
        {
            if (sprite == null) return null;
            if (WhiteCopies.TryGetValue(sprite, out var white)) return white;
            white = sprite;
            Color average = Color.white;
            try
            {
                var copy = Read(sprite);
                var pixels = copy.GetPixels32();
                float r = 0f, g = 0f, b = 0f, weight = 0f;
                for (var i = 0; i < pixels.Length; i++)
                {
                    var a = pixels[i].a / 255f;
                    if (a > 0.3f)
                    {
                        r += pixels[i].r * a; g += pixels[i].g * a; b += pixels[i].b * a; weight += a;
                    }
                    pixels[i] = new Color32(255, 255, 255, pixels[i].a);
                }
                copy.SetPixels32(pixels);
                copy.Apply();
                white = Sprite.Create(copy, new Rect(0f, 0f, copy.width, copy.height), new Vector2(0.5f, 0.5f), sprite.pixelsPerUnit);
                if (weight > 0f)
                {
                    var c = new Color(r / weight / 255f, g / weight / 255f, b / weight / 255f, 1f);
                    var max = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                    average = max > 0.05f ? new Color(c.r / max, c.g / max, c.b / max, 1f) : Color.white;
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log?.LogWarning($"Could not make a white icon from {sprite.name}: {e.Message}");
            }
            WhiteCopies[sprite] = white;
            Averages[sprite] = average;
            return white;
        }

        internal static Color Average(Sprite sprite)
        {
            if (sprite == null) return Color.white;
            White(sprite);
            return Averages.TryGetValue(sprite, out var color) ? color : Color.white;
        }

        // Icons are not readable from code, so the sprite's part of its texture (only that part, not the whole sheet it
        // is on) is drawn into a render texture of its own size and read back.
        private static Texture2D Read(Sprite sprite)
        {
            var texture = sprite.texture;
            var area = sprite.textureRect;
            int width = Mathf.Max(1, (int)area.width), height = Mathf.Max(1, (int)area.height);
            var target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(texture, target, new Vector2(area.width / texture.width, area.height / texture.height),
                    new Vector2(area.x / texture.width, area.y / texture.height));
                RenderTexture.active = target;
                var copy = new Texture2D(width, height, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
                copy.Apply();
                return copy;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
            }
        }

        // --- Which icon a pin shows ---------------------------------------------------------------------------

        internal const string GameChoice = "game", ItemChoice = "item:";

        // Game icons offered in the picker for any thing (the value is "item:<prefab>"): a Surtling core for Burial
        // Chambers, coins for a treasure, a trophy for a camp, ...
        private static readonly string[] GamePicks =
        {
            "SurtlingCore", "Coins", "Ruby", "Amber", "AmberPearl", "SilverNecklace", "Wishbone", "BoneFragments", "TrophySkeleton",
            "TrophyDraugr", "TrophyGreydwarf", "TrophyBoar", "TrophyDeer", "TrophyNeck", "TrophyGoblin", "Barley", "Flax",
            "CarrotSeeds", "TurnipSeeds", "Honey", "Resin", "Flint", "Stone", "Wood", "Iron", "Copper", "Tin", "Silver",
        };

        // The game icon of a thing, or null when it has none.
        internal static Sprite GameIcon(Thing thing) => thing.Icon;

        // A record's own icon item (a spawner that drops something else than its kind), looked up once.
        private static Sprite Own(string item)
        {
            if (string.IsNullOrEmpty(item)) return null;
            if (!Found.TryGetValue(item, out var sprite) || sprite == null) Found[item] = sprite = Find(item);
            return sprite;
        }

        // The icon of a thing's pin, and whether it is a white icon (drawn in the thing's colour) or a game icon (as is).
        internal static Sprite Resolve(Thing thing, Heightmap.Biome biome, bool emptied, string own, out bool tinted)
        {
            tinted = false;
            if (thing == null) return null;
            var choice = Catalog.IconChoice(biome, thing);
            if (string.IsNullOrEmpty(choice) && !Plugin.WhiteStyle)
            {
                var sprite = Own(own);
                if (sprite != null) return Shown(thing, sprite);
            }
            return Resolve(thing, choice, emptied, out tinted);
        }

        private static Sprite Resolve(Thing thing, string choice, bool emptied, out bool tinted)
        {
            tinted = false;
            if (choice == GameChoice && GameIcon(thing) != null) return Shown(thing, GameIcon(thing));
            if (choice != null && choice.StartsWith(ItemChoice, System.StringComparison.Ordinal))
            {
                var item = Own(choice.Substring(ItemChoice.Length));
                if (item != null) return Shown(thing, item);
            }
            var file = CustomIcons.Get(choice, out var plain);
            if (file != null)
            {
                tinted = plain;
                return plain ? file : Shown(thing, file);
            }
            var chosen = WhiteIcons.Get(choice);
            if (chosen != null)
            {
                tinted = true;
                return chosen;
            }
            // By the icon style; spawners keep their game icon (the trophy of what they spawn, or their drop) in both.
            if ((!Plugin.WhiteStyle || thing.Kind == Kind.Spawner) && GameIcon(thing) != null) return Shown(thing, GameIcon(thing));
            tinted = true;
            return (emptied ? WhiteIcons.Get(WhiteIcons.OpenChest) : null) ?? WhiteFor(thing, choice);
        }

        // The white icon a thing has (chosen, one of your own without colour, or its own), for F1 also when the game icon
        // is in use.
        private static Sprite WhiteFor(Thing thing, string choice)
        {
            var file = CustomIcons.Get(choice, out var plain);
            return (plain ? file : null) ?? WhiteIcons.Get(choice) ?? WhiteIcons.For(thing) ?? White(GameIcon(thing));
        }

        // The icon F1 shows as is (not in the thing's colour): a game icon or a coloured icon of your own when one is
        // picked, else the thing's own game icon.
        private static Sprite AsIs(Thing thing, string choice)
        {
            if (choice != null && choice.StartsWith(ItemChoice, System.StringComparison.Ordinal))
            {
                var item = Own(choice.Substring(ItemChoice.Length));
                if (item != null) return item;
            }
            var file = CustomIcons.Get(choice, out var plain);
            return file != null && !plain ? file : GameIcon(thing);
        }

        // A portal's pin: a white icon picked in F1 (in the portal colour), else its own (wood or stone portal) on a soft
        // glow in the portal's colour (the one picked in Portal List, else "Portal color"), so it does not blend into the
        // ground.
        internal static Sprite PortalIcon(Record record, out bool tinted)
        {
            tinted = false;
            var file = CustomIcons.Get(Plugin.PortalIcon, out var plain);
            if (file != null)
            {
                tinted = plain;
                return file;
            }
            var chosen = WhiteIcons.Get(Plugin.PortalIcon);
            if (chosen != null)
            {
                tinted = true;
                return chosen;
            }
            var own = (record != null ? PortalLink.OwnIcon(record) : null) ?? Find("portal_wood");
            if (own != null)
            {
                var color = (record != null ? PortalLink.ColorOf(record) : null) ?? Plugin.PortalColor;
                return Halo(own, new Color(color.r, color.g, color.b, 0.95f));
            }
            tinted = true;
            return WhiteIcons.Get(WhiteIcons.Portal) ?? PinSprite(Minimap.PinType.Icon4);
        }

        // --- Shadows and glows ----------------------------------------------------------------------------------

        private static readonly Dictionary<string, Sprite> Haloed = new Dictionary<string, Sprite>();

        // A game icon as it shows on the map: as the game draws it; a boss altar's trophy on a soft dark shadow (it stands
        // out; boss pins are also bigger).
        private static Sprite Shown(Thing thing, Sprite sprite) =>
            Catalog.IsBoss(thing) ? Halo(sprite, new Color(0f, 0f, 0f, 0.75f)) : sprite;

        // A copy of an icon on a soft, blurred halo of its own shape in a colour (its alpha = how strong), reaching about
        // a tenth of the icon; made once per icon and colour.
        private static Sprite Halo(Sprite sprite, Color color)
        {
            if (sprite == null) return null;
            var key = sprite.GetInstanceID() + "/" + ColorUtility.ToHtmlStringRGBA(color);
            if (Haloed.TryGetValue(key, out var shown) && shown != null) return shown;
            shown = sprite;
            try
            {
                var copy = Read(sprite);
                int w = copy.width, h = copy.height;
                var source = copy.GetPixels32();
                var reach = Mathf.Max(3, w / 11);
                var result = new Color32[source.Length];
                for (var y = 0; y < h; y++)
                    for (var x = 0; x < w; x++)
                    {
                        var i = y * w + x;
                        var own = (Color)source[i];
                        // The halo here: the strongest neighbour, fading with its distance (a soft edge, not a line).
                        var halo = 0f;
                        if (own.a < 0.99f)
                            for (var dy = -reach; dy <= reach; dy++)
                                for (var dx = -reach; dx <= reach; dx++)
                                {
                                    int nx = x + dx, ny = y + dy;
                                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                                    var d = Mathf.Sqrt(dx * dx + dy * dy);
                                    if (d > reach) continue;
                                    var fall = 1f - d / (reach + 1f);
                                    halo = Mathf.Max(halo, source[ny * w + nx].a / 255f * fall * fall);
                                }
                        halo *= color.a;
                        var a = own.a + halo * (1f - own.a);
                        var under = halo * (1f - own.a);
                        result[i] = a <= 0f ? new Color32(0, 0, 0, 0)
                            : (Color32)new Color((own.r * own.a + color.r * under) / a, (own.g * own.a + color.g * under) / a,
                                (own.b * own.a + color.b * under) / a, a);
                    }
                copy.SetPixels32(result);
                copy.Apply();
                shown = Sprite.Create(copy, new Rect(0f, 0f, w, h), new Vector2(0.5f, 0.5f), sprite.pixelsPerUnit);
                shown.name = sprite.name + " (halo)";
            }
            catch (System.Exception e)
            {
                Plugin.Log?.LogWarning($"Could not make a halo for {sprite.name}: {e.Message}");
            }
            Haloed[key] = shown;
            return shown;
        }

        // --- F1 ------------------------------------------------------------------------------------------------

        // The setting whose editor (colour and icon) is open in F1, one at a time.
        private static ConfigEntryBase _editing;
        private static string _hexText;

        // F1: both icons (the game's and the white one in its colour; the one in use is drawn full, the other dimmed),
        // then the switch. Clicking an icon opens the colour and the icon picker below it, live on the map.
        internal static void DrawSwitch(ConfigEntryBase entry, Thing thing, Slot slot)
        {
            var on = entry.BoxedValue is bool value && value;
            if (thing.Icon == null && !thing.IconTried && ObjectDB.instance != null)
            {
                // From the saved list (main menu): items are there already, the rest once a world loads.
                thing.IconTried = true;
                thing.Icon = Find(thing.IconItem);
            }
            var color = slot.Color;
            var icon = slot.Icon;
            var choice = icon?.Value;
            var tint = color != null ? color.Value : thing.AutoColor;
            Resolve(thing, choice, false, out var whiteInUse);
            GUILayout.BeginVertical();
            GUILayout.BeginHorizontal();
            var game = Square(24f);
            var asIs = AsIs(thing, choice);
            Draw(game, asIs, whiteInUse ? new Color(1f, 1f, 1f, 0.35f) : Color.white);
            Hovered(game, asIs, Color.white, "game");
            var white = Square(24f);
            var whiteIcon = WhiteFor(thing, choice);
            Draw(white, whiteIcon, whiteInUse ? tint : new Color(tint.r, tint.g, tint.b, 0.35f));
            Hovered(white, whiteIcon, new Color(tint.r, tint.g, tint.b, 1f), "white");
            var tip = new GUIContent("", "Click to change the colour and the icon");
            if (GUI.Button(game, tip, GUIStyle.none) | GUI.Button(white, tip, GUIStyle.none)) Toggle(entry);
            var now = GUILayout.Toggle(on, on ? " On" : " Off", GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
            if (_editing == entry)
            {
                if (color != null) DrawColor(color, thing.AutoColor);
                if (icon != null) DrawPicker(icon, tint, GameIcon(thing));
            }
            GUILayout.EndVertical();
            DrawPreview();
            if (now != on) entry.BoxedValue = now;
        }

        // F1, "Portal icon": the icon in the portal colour; clicking it opens the picker.
        internal static void DrawPortalIcon(ConfigEntryBase entry)
        {
            if (!(entry is ConfigEntry<string> icon)) return;
            var sprite = PortalIcon(null, out var tinted);
            GUILayout.BeginVertical();
            GUILayout.BeginHorizontal();
            var rect = Square(24f);
            Draw(rect, sprite, tinted ? Plugin.PortalColor : Color.white);
            Hovered(rect, sprite, tinted ? Plugin.PortalColor : Color.white, "portal");
            if (GUI.Button(rect, new GUIContent("", "Click to pick another icon"), GUIStyle.none)) Toggle(entry);
            var picked = WhiteIcons.Get(icon.Value) != null || CustomIcons.Get(icon.Value, out _) != null;
            GUILayout.Label(picked ? IconTitle(icon.Value) : "Each portal's own (or Portal List's)");
            GUILayout.EndHorizontal();
            if (_editing == entry) DrawPicker(icon, Plugin.PortalColor, Find("portal_wood"));
            GUILayout.EndVertical();
            DrawPreview();
        }

        private static void Toggle(ConfigEntryBase entry)
        {
            _editing = _editing == entry ? null : entry;
            _hexText = null;
            if (_editing != null) CustomIcons.Rescan(); // files added since
        }

        private static Rect Square(float size) => GUILayoutUtility.GetRect(size, size, GUILayout.Width(size), GUILayout.Height(size));

        private static string IconTitle(string name) => CustomIcons.IsFile(name) ? name.Substring(CustomIcons.Prefix.Length)
            : name.Substring(name.IndexOf('/') + 1).Replace('-', ' ');

        // The colour like Configuration Manager's own: hex and a swatch, then R G B A as numbers and sliders.
        private static void DrawColor(ConfigEntry<Color> entry, Color auto)
        {
            var c = entry.Value;
            var hex = "#" + ColorUtility.ToHtmlStringRGBA(c);
            // What is typed stays while it is not a colour yet; a colour set elsewhere (sliders) replaces it.
            if (_hexText == null || ColorUtility.TryParseHtmlString(_hexText, out var typed) && typed != c) _hexText = hex;
            var next = c;
            GUILayout.BeginHorizontal();
            var text = GUILayout.TextField(_hexText, GUILayout.Width(100f));
            if (text != _hexText)
            {
                _hexText = text;
                if (ColorUtility.TryParseHtmlString(text, out var parsed)) next = parsed;
            }
            var swatch = GUILayoutUtility.GetRect(40f, 20f, GUILayout.ExpandWidth(true), GUILayout.Height(20f));
            if (Event.current.type == EventType.Repaint)
            {
                var previous = GUI.color;
                GUI.color = new Color(c.r, c.g, c.b, 1f);
                GUI.DrawTexture(swatch, Texture2D.whiteTexture);
                GUI.color = previous;
            }
            if (GUILayout.Button("Reset", GUILayout.ExpandWidth(false))) next = auto;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            next.r = Number("R", next.r);
            next.g = Number("G", next.g);
            next.b = Number("B", next.b);
            next.a = Number("A", next.a);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            next.r = GUILayout.HorizontalSlider(next.r, 0f, 1f, GUILayout.MinWidth(30f));
            next.g = GUILayout.HorizontalSlider(next.g, 0f, 1f, GUILayout.MinWidth(30f));
            next.b = GUILayout.HorizontalSlider(next.b, 0f, 1f, GUILayout.MinWidth(30f));
            next.a = GUILayout.HorizontalSlider(next.a, 0f, 1f, GUILayout.MinWidth(30f));
            GUILayout.EndHorizontal();
            if (next != c) entry.Value = next;
        }

        private static float Number(string label, float value)
        {
            GUILayout.Label(label, GUILayout.ExpandWidth(false));
            var shown = value.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
            var text = GUILayout.TextField(shown, GUILayout.MinWidth(45f));
            return text != shown && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                out var typed) ? Mathf.Clamp01(typed) : value;
        }

        // Every icon to pick from: Auto (by the icon style), the game icon, then all white icons in the thing's colour.
        private static void DrawPicker(ConfigEntry<string> entry, Color tint, Sprite game)
        {
            const int perRow = 10;
            const float size = 28f;
            var current = entry.Value ?? "";
            var names = WhiteIcons.All;
            var picked = current;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(current.Length == 0 ? "[ Auto ]" : "Auto", GUILayout.ExpandWidth(false))) picked = "";
            if (game != null)
            {
                var rect = Cell(size, current == GameChoice);
                Draw(rect, game, Color.white);
                Hovered(rect, game, Color.white, "pick:" + GameChoice);
                if (GUI.Button(rect, new GUIContent("", "Game icon"), GUIStyle.none)) picked = GameChoice;
            }
            GUILayout.EndHorizontal();
            // Other game icons (as they are, not tinted).
            for (var i = 0; i < GamePicks.Length; i += perRow)
            {
                GUILayout.BeginHorizontal();
                for (var j = i; j < Mathf.Min(i + perRow, GamePicks.Length); j++)
                {
                    var sprite = Own(GamePicks[j]);
                    if (sprite == null) continue;
                    var rect = Cell(size, current == ItemChoice + GamePicks[j]);
                    Draw(rect, sprite, Color.white);
                    Hovered(rect, sprite, Color.white, "pick:" + GamePicks[j]);
                    if (GUI.Button(rect, new GUIContent("", GamePicks[j]), GUIStyle.none)) picked = ItemChoice + GamePicks[j];
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }
            // Your own icons (PNG files in the Icons folder): coloured ones as they are, those without colour in its colour.
            CustomIcons.Watch();
            var own = CustomIcons.All;
            GUILayout.BeginHorizontal();
            GUILayout.Label(own.Count == 0 ? "Your own icons: put PNG files in the Icons folder." : "Your own icons:", GUILayout.ExpandWidth(false));
            if (GUILayout.Button(new GUIContent("Open folder", CustomIcons.Folder), GUILayout.ExpandWidth(false))) OpenFolder();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            for (var i = 0; i < own.Count; i += perRow)
            {
                GUILayout.BeginHorizontal();
                for (var j = i; j < Mathf.Min(i + perRow, own.Count); j++)
                {
                    var sprite = CustomIcons.Sprite(own[j]);
                    if (sprite == null) continue;
                    var value = CustomIcons.Prefix + own[j];
                    var color = CustomIcons.Plain(own[j]) ? new Color(tint.r, tint.g, tint.b, 1f) : Color.white;
                    var rect = Cell(size, current == value);
                    Draw(rect, sprite, color);
                    Hovered(rect, sprite, color, "pick:" + value);
                    if (GUI.Button(rect, new GUIContent("", own[j]), GUIStyle.none)) picked = value;
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }
            for (var i = 0; i < names.Count; i += perRow)
            {
                GUILayout.BeginHorizontal();
                for (var j = i; j < Mathf.Min(i + perRow, names.Count); j++)
                {
                    var rect = Cell(size, current == names[j]);
                    var white = WhiteIcons.Get(names[j]);
                    Draw(rect, white, new Color(tint.r, tint.g, tint.b, 1f));
                    Hovered(rect, white, new Color(tint.r, tint.g, tint.b, 1f), "pick:" + names[j]);
                    if (GUI.Button(rect, new GUIContent("", IconTitle(names[j])), GUIStyle.none)) picked = names[j];
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }
            if (picked != current) entry.Value = picked;
        }

        private static void OpenFolder()
        {
            try
            {
                var folder = CustomIcons.Folder;
                if (!System.IO.Directory.Exists(folder)) System.IO.Directory.CreateDirectory(folder);
                Application.OpenURL(new System.Uri(folder).AbsoluteUri);
            }
            catch (System.Exception e)
            {
                Plugin.Log?.LogWarning("Could not open the icons folder: " + e.Message);
            }
        }

        // --- Bigger on hover (F1) -------------------------------------------------------------------------------

        // An icon held under the mouse for a moment shows bigger, over the icons above it (some are hard to tell apart
        // at their small size in F1). IMGUI draws in order, so the preview is drawn when the setting is done (nothing of
        // it drawn later covers it) and grows upwards from the icon (what is above is drawn already, what is below not).
        private const float PreviewDelay = 0.8f, PreviewSize = 96f;
        private static string _hoverKey;
        private static float _hoverSince;
        private static int _hoverFrame = -10;
        private static bool _hoverHere;
        private static Rect _hoverRect;
        private static Sprite _hoverSprite;
        private static Color _hoverTint;
        private static Texture2D _frame;

        private static void Hovered(Rect rect, Sprite sprite, Color tint, string key)
        {
            if (!Plugin.Preview || sprite == null || Event.current.type != EventType.Repaint || !rect.Contains(Event.current.mousePosition)) return;
            var frame = Time.frameCount;
            if (_hoverKey != key || _hoverFrame < frame - 1) _hoverSince = Time.realtimeSinceStartup; // a new icon, or back after a gap
            _hoverKey = key;
            _hoverFrame = frame;
            _hoverHere = true;
            _hoverRect = rect;
            _hoverSprite = sprite;
            _hoverTint = tint;
        }

        private static void DrawPreview()
        {
            if (!_hoverHere) return;
            _hoverHere = false;
            if (Time.realtimeSinceStartup - _hoverSince < PreviewDelay || _hoverSprite == null) return;
            var size = PreviewSize;
            var box = new Rect(Mathf.Max(0f, _hoverRect.center.x - size / 2f), _hoverRect.yMax - size, size, size);
            if (_frame == null)
            {
                _frame = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
                _frame.SetPixel(0, 0, Color.white);
                _frame.Apply();
            }
            var previous = GUI.color;
            GUI.color = new Color(1f, 0.75f, 0.3f, 0.9f);
            GUI.DrawTexture(box, _frame);
            GUI.color = new Color(0.08f, 0.07f, 0.06f, 0.96f);
            GUI.DrawTexture(new Rect(box.x + 2f, box.y + 2f, box.width - 4f, box.height - 4f), _frame);
            GUI.color = previous;
            Draw(new Rect(box.x + 10f, box.y + 10f, box.width - 20f, box.height - 20f), _hoverSprite, _hoverTint);
        }

        // A square of the picker, framed when it is the icon in use.
        private static Rect Cell(float size, bool selected)
        {
            var rect = Square(size);
            if (selected && Event.current.type == EventType.Repaint)
            {
                var previous = GUI.color;
                GUI.color = new Color(1f, 0.75f, 0.3f, 0.6f);
                GUI.DrawTexture(rect, Texture2D.whiteTexture);
                GUI.color = previous;
            }
            return new Rect(rect.x + 2f, rect.y + 2f, rect.width - 4f, rect.height - 4f);
        }

        private static void Draw(Rect rect, Sprite sprite, Color tint)
        {
            if (sprite == null || sprite.texture == null || Event.current.type != EventType.Repaint) return;
            var texture = sprite.texture;
            var area = sprite.textureRect;
            var uv = new Rect(area.x / texture.width, area.y / texture.height, area.width / texture.width, area.height / texture.height);
            var previous = GUI.color;
            GUI.color = tint;
            GUI.DrawTextureWithTexCoords(rect, texture, uv);
            GUI.color = previous;
        }
    }
}
