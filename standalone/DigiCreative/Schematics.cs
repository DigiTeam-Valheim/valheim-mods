// Schematics (Creative world): save a building to a file and paste it again, like copy and paste.
// - Save Schematic (hammer tool): click a building. Every piece connected to the one you click is saved (its kind, place
//   and turn, measured from the middle of the building's bottom), with a picture of the building from the side you look
//   at; you type a name. Files: BepInEx\config\DigiCreative\Schematics\<name>.schematic (plain text) and <name>.png, so
//   they can be shared. A name in use gets a number ("House 2"), nothing is overwritten.
// - Schematics (hammer tool): a click opens the library (DigiShared/SchematicLibrary.cs): each schematic with its picture,
//   piece count and size, and Place, Mirrored, Materials (Build Materials' list for it) and Delete (click twice).
// - Placing (DigiShared/SchematicPlacer.cs, also in Building Tweaks): after Place the building follows your aim as a
//   ghost; the scroll wheel turns it, the free placement key + wheel moves it up and down; it snaps onto the buildings
//   around it where the most snap points meet (aimed at the ground: an edge); a click pastes it, again and again; the
//   library key + click opens the list again. Mirrored places the mirror image (left and right swapped, with Mirror's
//   rules per piece). A piece already standing in the same place is left out, so two schematics sharing a wall get it once.
// - Pasting goes from the bottom up, 150 pieces a frame, with one placing sound. It is not copied by mirror lines (Shift+N
//   does that afterwards) and adds no iron cores (a schematic holds its iron already). Pieces of a mod that is not
//   installed are left out and named. "Undo last paste" in the library takes the last paste away again.
// Only in the creative world, like the other tools.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx.Configuration;
using TMPro;
using DigiShared;
using UnityEngine;
using UnityEngine.UI;
using Entry = DigiShared.SchematicLibrary.Entry;
using Schematic = DigiShared.SchematicLibrary.Schematic;

namespace DigiCreative
{
    internal class Schematics : MonoBehaviour
    {
        internal static ConfigEntry<bool> On;
        internal static ConfigEntry<KeyboardShortcut> LibraryKey, FreeKey;
        internal static ConfigEntry<int> GhostPieces;
        internal static ConfigEntry<float> HeightStep;
        private static Schematics _instance;
        private static BepInEx.Logging.ManualLogSource _log;

        private const string PlaceTool = "piece_digi_schematics";
        private const int PictureSize = 256;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // The game's name box hands the typed text here.
        private sealed class NameBox : TextReceiver
        {
            public Action<string> Done;
            public string GetText() => "";
            public void SetText(string text) => Done?.Invoke(text);
        }

        private static string Folder => SchematicLibrary.Folder;

        private static bool _busy, _wasMirroring, _wasPaused;
        private static SchematicPlacer.Host _host;

        internal static void Bind(ConfigFile config, BepInEx.Logging.ManualLogSource log)
        {
            _log = log;
            SchematicLibrary.Log = log;
            On = config.Bind("8 - Schematics", "Schematic tools", true,
                "Save Schematic and Schematics in the hammer menu (Building tab, at the end): save a building to a file and " +
                "paste it again, with a picture of each in the list and a ghost of the building while you place it.");
            LibraryKey = config.Bind("8 - Schematics", "Library key", new KeyboardShortcut(KeyCode.LeftShift),
                "Hold this while clicking with the Schematics tool: the list of schematics opens again (to pick another one).");
            FreeKey = config.Bind("8 - Schematics", "Free placement key", new KeyboardShortcut(KeyCode.LeftAlt),
                "Hold this while placing a schematic: it does not snap onto the buildings around it, and the scroll wheel " +
                "moves it up and down instead of turning it.");
            HeightStep = config.Bind("8 - Schematics", "Height step (m)", 0.25f, new ConfigDescription(
                "How far one scroll wheel step moves a schematic up or down (with the free placement key held).",
                new AcceptableValueRange<float>(0.05f, 2f)));
            GhostPieces = config.Bind("8 - Schematics", "Ghost pieces", 1500, new ConfigDescription(
                "Most pieces the ghost shows while you place a schematic. A bigger one shows that many, spread over it, and a box " +
                "around it; pasting always places all.", new AcceptableValueRange<int>(100, 10000)));
        }

        internal static void Register()
        {
            Tools.Add("schematicsave", "Save Schematic",
                "Click a building: every piece connected to the one you click is saved as a schematic, with a picture. You give it a name.",
                "piece_cartographytable", () => On.Value, player =>
                {
                    if (_instance != null && !_busy && !Windows.IsOpen) _instance.StartCoroutine(Save(player));
                });
            Tools.Add("schematics", "Schematics",
                "Click: your saved schematics. After Place the building follows your aim; the scroll wheel turns it, a click pastes " +
                $"it, {FreeKey.Value} + scroll wheel moves it up and down. Hold {LibraryKey.Value} and click for the list again.", "Hammer", () => On.Value, player =>
                {
                    if (SchematicPlacer.Pasting || Windows.IsOpen) return;
                    if (!SchematicPlacer.Chosen || LibraryKey.Value.IsPressed()) OpenLibrary();
                    else SchematicPlacer.Paste(player);
                });
        }

        private void Awake()
        {
            _instance = this;
            _host = new SchematicPlacer.Host
            {
                Runner = this,
                Allowed = player => Tools.InCreative() && PlaceToolSelected(player),
                FreeKey = () => FreeKey.Value,
                HeightStep = () => HeightStep.Value,
                GhostPieces = () => GhostPieces.Value,
                Reach = 150f,
                Placing = on =>
                {
                    // Mirror lines do not copy a paste and Iron Cores adds none: a schematic holds its iron already.
                    if (on)
                    {
                        _wasMirroring = Mirror.Mirroring;
                        _wasPaused = IronCores.Paused;
                        Mirror.Mirroring = true;
                        IronCores.Paused = true;
                    }
                    else
                    {
                        Mirror.Mirroring = _wasMirroring;
                        IronCores.Paused = _wasPaused;
                    }
                },
                Done = (player, plan, made) =>
                {
                    var text = $"Pasted {plan.Name}: {BuildMaterials.Number(made.Count)} pieces";
                    var extra = new List<string>();
                    if (plan.AlreadyThere > 0) extra.Add($"{BuildMaterials.Number(plan.AlreadyThere)} already there");
                    var missing = SchematicPlacer.MissingText(plan);
                    if (missing.Length > 0)
                    {
                        extra.Add(missing);
                        _log?.LogWarning("Schematic " + plan.Name + ": left out " + string.Join(", ", plan.Missing.Select(m => m.Key + " x" + m.Value)));
                    }
                    if (extra.Count > 0) text += " (" + string.Join("; ", extra) + ")";
                    player?.Message(MessageHud.MessageType.TopLeft, text);
                },
            };
        }

        private void LateUpdate() => SchematicPlacer.Tick();

        private void OnDestroy() => SchematicPlacer.Drop();

        private static bool PlaceToolSelected(Player player)
        {
            var selected = player.m_buildPieces != null ? player.m_buildPieces.GetSelectedPiece() : null;
            return selected != null && selected.gameObject.name == PlaceTool;
        }

        // --- Saving ------------------------------------------------------------------------------------------------

        private static IEnumerator Save(Player player)
        {
            var start = player.GetHoveringPiece();
            if (start == null || !start.IsPlacedByPlayer())
            {
                player.Message(MessageHud.MessageType.Center, "Aim at a piece of the building to save");
                yield break;
            }
            _busy = true;
            var found = new List<Piece>();
            try
            {
                var steps = 0;
                foreach (var piece in Tools.Building(start, found))
                    if (++steps % 150 == 0) yield return null;
            }
            finally { _busy = false; }
            var pieces = found.Where(p => p != null).ToList();
            if (pieces.Count == 0) yield break;
            var bounds = Tools.BoundsOf(pieces[0]);
            foreach (var piece in pieces) bounds.Encapsulate(Tools.BoundsOf(piece));
            var anchor = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
            var entries = pieces.Select(p => new Entry
            {
                Prefab = Utils.GetPrefabName(p.gameObject), Pos = p.transform.position - anchor, Rot = p.transform.rotation,
            }).ToList();
            var picture = Photo(bounds);
            var size = bounds.size;
            // Foundations often reach into the ground: remember how deep, so a paste on the ground sits as deep again.
            var ground = 0f;
            if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(new Vector3(bounds.center.x, 0f, bounds.center.z), out var groundY))
                ground = groundY - bounds.min.y;
            if (ground < -2f) ground = 0f; // built up in the air: pasted on what you aim at
            if (TextInput.instance == null) yield break;
            TextInput.instance.RequestText(new NameBox { Done = name => Store(player, name, entries, size, ground, picture) },
                $"Name of the schematic ({entries.Count} pieces)", 40);
        }

        private static void Store(Player player, string name, List<Entry> entries, Vector3 size, float ground, byte[] picture)
        {
            var bad = Path.GetInvalidFileNameChars().Concat(new[] { ';', '=' }).ToArray(); // ; and = would confuse the file
            var clean = new string((name ?? "").Trim().Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
            if (clean.Length == 0) return;
            try
            {
                Directory.CreateDirectory(Folder);
                var file = Path.Combine(Folder, clean + ".schematic");
                for (var n = 2; File.Exists(file); n++) file = Path.Combine(Folder, $"{clean} {n}.schematic");
                var title = Path.GetFileNameWithoutExtension(file);
                var text = new StringBuilder();
                text.AppendLine("# Digi's Creative World schematic");
                text.AppendLine("name=" + title);
                text.AppendLine("saved=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm", Inv));
                text.AppendLine("pieces=" + entries.Count);
                text.AppendLine("size=" + F(size.x) + " " + F(size.y) + " " + F(size.z));
                text.AppendLine("ground=" + F(ground));
                text.AppendLine("# prefab;x;y;z;qx;qy;qz;qw (metres from the middle of the bottom, world directions)");
                foreach (var e in entries)
                    text.Append(e.Prefab).Append(';').Append(F(e.Pos.x)).Append(';').Append(F(e.Pos.y)).Append(';').Append(F(e.Pos.z))
                        .Append(';').Append(Q(e.Rot.x)).Append(';').Append(Q(e.Rot.y)).Append(';').Append(Q(e.Rot.z)).Append(';')
                        .Append(Q(e.Rot.w)).AppendLine();
                File.WriteAllText(file, text.ToString(), new UTF8Encoding(false));
                if (picture != null) File.WriteAllBytes(Path.ChangeExtension(file, ".png"), picture);
                player.Message(MessageHud.MessageType.Center, $"Saved schematic {title}: {BuildMaterials.Number(entries.Count)} pieces");
                _log?.LogInfo($"Saved schematic {file}: {entries.Count} pieces");
            }
            catch (Exception e)
            {
                player.Message(MessageHud.MessageType.Center, "Could not save the schematic (see the log)");
                _log?.LogError("Saving a schematic failed: " + e);
            }
        }

        private static string F(float v) => v.ToString("0.####", Inv);
        private static string Q(float v) => v.ToString("0.######", Inv);

        // A picture of the building, from the side the camera looks at, from above at 30 degrees; without characters.
        private static byte[] Photo(Bounds bounds)
        {
            var main = Utils.GetMainCamera();
            if (main == null) return null;
            GameObject go = null;
            RenderTexture target = null;
            Texture2D picture = null;
            try
            {
                go = new GameObject("DigiCreative_SchematicCamera");
                var cam = go.AddComponent<Camera>();
                cam.CopyFrom(main);
                cam.enabled = false;
                cam.cullingMask = main.cullingMask & ~LayerMask.GetMask("UI", "character", "character_net", "character_ghost",
                    "character_noenv", "player", "ghost", "item");
                cam.fieldOfView = 35f;
                cam.aspect = 1f;
                var look = main.transform.forward;
                look.y = 0f;
                if (look.sqrMagnitude < 0.01f) look = Vector3.forward;
                var dir = Quaternion.AngleAxis(30f, Vector3.Cross(Vector3.up, look.normalized)) * look.normalized;
                var radius = Mathf.Max(bounds.extents.magnitude, 1f);
                var distance = radius / Mathf.Sin(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.05f;
                cam.transform.position = bounds.center - dir * distance;
                cam.transform.rotation = Quaternion.LookRotation(dir);
                cam.nearClipPlane = Mathf.Max(0.1f, distance - radius - 1f);
                cam.farClipPlane = distance + radius * 3f + 100f;
                target = RenderTexture.GetTemporary(PictureSize, PictureSize, 24, RenderTextureFormat.ARGB32);
                cam.targetTexture = target;
                cam.Render();
                cam.targetTexture = null;
                var previous = RenderTexture.active;
                RenderTexture.active = target;
                picture = new Texture2D(PictureSize, PictureSize, TextureFormat.RGB24, false);
                picture.ReadPixels(new Rect(0, 0, PictureSize, PictureSize), 0, 0);
                picture.Apply();
                RenderTexture.active = previous;
                return IconSheet.EncodePng(picture);
            }
            catch (Exception e)
            {
                _log?.LogWarning("No picture for the schematic: " + e.Message);
                return null;
            }
            finally
            {
                if (target != null) RenderTexture.ReleaseTemporary(target);
                if (go != null) Destroy(go);
                if (picture != null) Destroy(picture);
            }
        }

        // --- The library -------------------------------------------------------------------------------------------

        private static void OpenLibrary()
        {
            SchematicLibrary.Open($"Place: follows your aim; wheel turns it, {FreeKey.Value} + wheel: up/down; click pastes. {LibraryKey.Value} + click: this list.",
                UndoBar, (row, s, width) =>
                {
                    Windows.Button(row, "Place", 124f, 70f, 116f, 38f, () => Choose(s, false));
                    Windows.Button(row, "Mirrored", 248f, 70f, 116f, 38f, () => Choose(s, true));
                    Windows.Button(row, "Materials", 372f, 70f, 116f, 38f, () => SchematicLibrary.ShowMaterials(s, OpenLibrary));
                    var sure = false;
                    Button delete = null;
                    delete = Windows.Button(row, "Delete", width - 132f, 70f, 116f, 38f, () =>
                    {
                        if (!sure)
                        {
                            sure = true;
                            Windows.SetLabel(delete, "Sure?");
                            return;
                        }
                        Delete(s);
                        OpenLibrary();
                    });
                });
        }

        // The last paste, to take it away again (only its own pieces, also where it touches other buildings).
        private static float UndoBar(Windows.Window w, float width)
        {
            var pasted = SchematicPlacer.LastPaste.Where(p => p != null).ToList();
            if (pasted.Count == 0) return 0f;
            var undo = Windows.Rect("Undo", w.Content);
            Windows.Place(undo, 0f, 0f, width, 60f);
            undo.gameObject.AddComponent<Image>().color = new Color(0.45f, 0.12f, 0.08f, 0.35f);
            Windows.Place(Windows.Text(undo, $"Last paste: {SchematicPlacer.LastPasteName}, {BuildMaterials.Number(pasted.Count)} pieces", 20f,
                Windows.Body, TextAlignmentOptions.Left).rectTransform, 16f, 16f, width - 200f, 28f);
            Windows.Button(undo, "Undo paste", width - 172f, 11f, 156f, 38f, () =>
            {
                var player = Player.m_localPlayer;
                if (player == null) return;
                Windows.Close();
                var pieces = SchematicPlacer.LastPaste.Where(p => p != null).ToList();
                SchematicPlacer.LastPaste.Clear();
                RemoveBuilding.Remove(player, pieces, $" (the paste of {SchematicPlacer.LastPasteName})");
            });
            return 68f;
        }

        private static void Delete(Schematic s)
        {
            try
            {
                File.Delete(s.File);
                var png = Path.ChangeExtension(s.File, ".png");
                if (File.Exists(png)) File.Delete(png);
            }
            catch (Exception e) { _log?.LogWarning($"Could not delete {s.File}: {e.Message}"); }
            if (SchematicPlacer.ChosenFile == s.File) SchematicPlacer.Drop();
        }

        // --- Placing -----------------------------------------------------------------------------------------------

        private static void Choose(Schematic s, bool mirrored)
        {
            var full = SchematicLibrary.Full(s);
            if (full == null || _instance == null) return;
            Windows.Close();
            if (full.Pieces.Count == 0)
            {
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "That schematic has no pieces");
                return;
            }
            SchematicPlacer.Choose(_host, full.Name + (mirrored ? " (mirrored)" : ""), full.File, mirrored ? Flip(full.Pieces) : full.Pieces,
                full.Size, full.Ground);
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                $"{full.Name}{(mirrored ? " (mirrored)" : "")}: the scroll wheel turns it, a click pastes it");
        }

        // The mirror image, left and right swapped (across the schematic's own x = 0), with Mirror's rules per piece.
        private static List<Entry> Flip(List<Entry> entries)
        {
            var line = new Mirror.Line { Origin = Vector3.zero, Normal = Vector3.right };
            return entries.Select(e =>
            {
                var image = Mirror.Reflect(new Mirror.Placement { Name = e.Prefab, Pos = e.Pos, Rot = e.Rot }, line);
                return new Entry { Prefab = image.Name, Pos = image.Pos, Rot = image.Rot };
            }).ToList();
        }
    }
}
