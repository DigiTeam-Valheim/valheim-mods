// Mirror building (Creative world, hammer out). Up to two mirror lines: N sets the first, Alt+N the second (for a base
// that is the same north-south and east-west: with both lines, every piece is placed in all four quarters).
// N on a building piece: the line runs through the middle of that piece or along one of its edges (whichever is nearest
// to where you aim), along its own axis closest to where you look, so it sits on the building's grid. N on the ground,
// then on a second point: a line through both. From then on every piece you place is placed mirrored too, and removing
// a piece removes its mirrored twins. Shift+N on a building copies the side (or quarter) you aim at to the others: first
// it clears them (the building's pieces there and any of your pieces where the copy goes), pieces on a line stay.
// Ctrl+N removes both lines. The lines are drawn while the hammer is out (first cyan, second orange).
// (The first version used Y, which Epic Loot and Adventure Backpacks already use.)
// Each mirrored piece looks mirrored: a piece that is the same left-right (walls, floors, roofs) is placed flipped that
// way; a piece that is the same across its diagonal (the corner roofs) is flipped across that; a piece with a left and a
// right version (the triangle walls) gets its partner, found as the piece of the same kind and materials whose snap
// points are its own flipped left-right; a beam leaning sideways is turned around. Worked out once per kind of piece.
// Shared by Digi's Creative World and its live helper: the host sets InCreative, binds the settings, patches, and adds
// the component.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace DigiCreative
{
    internal class Mirror : MonoBehaviour
    {
        internal static Func<bool> InCreative = () => false;
        internal static ConfigEntry<bool> On;
        internal static ConfigEntry<KeyboardShortcut> PointKey, SecondKey, BuildingKey, ClearKey;

        // A mirror: an upright plane through Origin, facing Normal.
        internal sealed class Line
        {
            public Vector3 Origin, Normal;
            public float Side(Vector3 p) => Vector3.Dot(p - Origin, Normal);
            public Vector3 Pos(Vector3 p) => p - 2f * Side(p) * Normal;
            public Vector3 Dir(Vector3 d) => d - 2f * Vector3.Dot(d, Normal) * Normal;
        }

        // A piece's place: what, where, turned how.
        internal struct Placement
        {
            public string Name;
            public Vector3 Pos;
            public Quaternion Rot;
        }

        internal static readonly Line[] Lines = new Line[2];
        internal static bool Mirroring; // placing or removing on purpose (a mirrored copy, a Wood Swap): not mirrored again
        private static Vector3 _first;
        private static int _settingLine = -1; // the line whose first ground point is set
        private static bool _busy;
        private readonly LineRenderer[] _ground = new LineRenderer[2], _post = new LineRenderer[2];
        private static readonly List<Transform> Snaps = new List<Transform>();
        private static readonly Color[] LineColours = { new Color(0.2f, 0.9f, 1f, 0.85f), new Color(1f, 0.6f, 0.15f, 0.85f) };

        internal static void Bind(ConfigFile config)
        {
            On = config.Bind("5 - Mirror", "Mirror building", true,
                "With the hammer out: N on a point and N on a second point sets a mirror line through both (snapped to the " +
                "nearest snap point); Alt+N the same for a second line. Then every piece you place or remove is mirrored too " +
                "(with two lines: in all four quarters), Shift+N on a building copies that side to the others (clearing them " +
                "first), Ctrl+N removes the lines.");
            PointKey = config.Bind("5 - Mirror", "Line key", new KeyboardShortcut(KeyCode.N),
                "On a building piece: the mirror line runs through its middle, along its axis closest to where you look. " +
                "On the ground: press on a first and a second point.");
            SecondKey = config.Bind("5 - Mirror", "Second line key", new KeyboardShortcut(KeyCode.N, KeyCode.LeftAlt),
                "The same for a second mirror line, e.g. across the first one: then every piece is placed in all four quarters.");
            BuildingKey = config.Bind("5 - Mirror", "Mirror building key", new KeyboardShortcut(KeyCode.N, KeyCode.LeftShift),
                "Aim at a building and press: every piece connected to it is mirrored to the other side of the line (with two " +
                "lines: to the other three quarters).");
            ClearKey = config.Bind("5 - Mirror", "Clear line key", new KeyboardShortcut(KeyCode.N, KeyCode.LeftControl),
                "Removes the mirror lines: no more mirroring.");
            // The first version used Y, which Epic Loot (Spirit Animal) and Adventure Backpacks (Quickdrop) already use.
            if (PointKey.Value.MainKey == KeyCode.Y) PointKey.Value = new KeyboardShortcut(KeyCode.N);
            if (BuildingKey.Value.MainKey == KeyCode.Y) BuildingKey.Value = new KeyboardShortcut(KeyCode.N, KeyCode.LeftShift);
            if (ClearKey.Value.MainKey == KeyCode.Y) ClearKey.Value = new KeyboardShortcut(KeyCode.N, KeyCode.LeftControl);
        }

        internal static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.PlacePiece)), postfix: new HarmonyMethod(typeof(Mirror), nameof(Placed)));
            harmony.Patch(AccessTools.Method(typeof(WearNTear), nameof(WearNTear.Remove)), prefix: new HarmonyMethod(typeof(Mirror), nameof(Removing)));
        }

        private static bool HasLine => Lines[0] != null || Lines[1] != null;
        internal static bool Active => HasLine && On != null && On.Value && InCreative();
        private static IEnumerable<Line> ActiveLines => Lines.Where(l => l != null);

        // --- Mirror maths ----------------------------------------------------------------------------------------------

        // How a kind of piece looks the same mirrored: which piece goes on the other side (itself, or its left/right
        // partner, like the triangle walls) and across which of its own planes it is the same.
        internal enum Sym { LeftRight, FrontBack, Diagonal, AntiDiagonal }

        internal sealed class Plan
        {
            public string Name;
            public Sym Sym;
        }

        private static readonly Dictionary<string, Plan> Plans = new Dictionary<string, Plan>();
        private static readonly Func<Vector3, Vector3> FlipLeftRight = v => new Vector3(-v.x, v.y, v.z);
        private static readonly Func<Vector3, Vector3> FlipFrontBack = v => new Vector3(v.x, v.y, -v.z);
        private static readonly Func<Vector3, Vector3> FlipDiagonal = v => new Vector3(v.z, v.y, v.x);
        private static readonly Func<Vector3, Vector3> FlipAntiDiagonal = v => new Vector3(-v.z, v.y, -v.x);

        // The mirror of a piece turned r: the piece's own symmetry S, then the mirror (mirror * r * S is a turn again).
        // Its forward is where S sends forward, mirrored; its up is up, mirrored.
        private static Quaternion MirrorRot(Quaternion r, Plan plan, Line line)
        {
            Vector3 forward;
            switch (plan.Sym)
            {
                case Sym.FrontBack: forward = -(r * Vector3.forward); break;
                case Sym.Diagonal: forward = r * Vector3.right; break;
                case Sym.AntiDiagonal: forward = -(r * Vector3.right); break;
                default: forward = r * Vector3.forward; break;
            }
            return Quaternion.LookRotation(line.Dir(forward), line.Dir(r * Vector3.up));
        }

        internal static Placement Reflect(Placement p, Line line)
        {
            var plan = PlanFor(p.Name);
            return new Placement { Name = plan.Name, Pos = line.Pos(p.Pos), Rot = MirrorRot(p.Rot, plan, line) };
        }

        // Every mirrored copy of a piece: across each line, and with two lines across both. Copies that are the piece
        // itself (or an earlier copy) are left out: a symmetric piece on a line is its own mirror there. "The same" goes
        // by the snap points (SameSpot), not only the place: a piece whose corner is on the line (the Ashwood Arch, whose
        // own point is at the foot of its post) has its mirror on that same place, facing the other way.
        internal static List<Placement> Images(Placement p)
        {
            var result = new List<Placement>();
            var lines = ActiveLines.ToList();
            var sets = new List<Line[]>();
            foreach (var line in lines) sets.Add(new[] { line });
            if (lines.Count == 2) sets.Add(new[] { lines[0], lines[1] });
            foreach (var set in sets)
            {
                var image = p;
                foreach (var line in set) image = Reflect(image, line);
                if ((image.Name == p.Name && SameSpot(p.Name, image.Pos, image.Rot, p.Pos, p.Rot)) ||
                    result.Any(r => r.Name == image.Name && SameSpot(image.Name, r.Pos, r.Rot, image.Pos, image.Rot))) continue;
                result.Add(image);
            }
            return result;
        }

        // 1. Left-right the same (walls, floors, roofs): itself, flipped left-right. 2. The same across its diagonal (the
        // corner roofs): itself, flipped across that. 3. Its partner, a piece of the same kind and materials whose snap
        // points are this one's flipped left-right (the other triangle wall): placed that way. 4. Otherwise whichever of
        // its own planes fits best (a beam leaning sideways: turned around front-back).
        internal static Plan PlanFor(string name)
        {
            if (Plans.TryGetValue(name, out var plan)) return plan;
            plan = new Plan { Name = name };
            Plans[name] = plan;
            var piece = PieceOf(name);
            var points = SnapsOf(piece);
            if (points.Count < 2) return plan;
            var leftRight = Mismatch(points, points, FlipLeftRight);
            if (leftRight < 0.05f) return plan;
            var diagonal = Mismatch(points, points, FlipDiagonal);
            var antiDiagonal = Mismatch(points, points, FlipAntiDiagonal);
            if (diagonal < 0.05f || antiDiagonal < 0.05f)
            {
                plan.Sym = diagonal <= antiDiagonal ? Sym.Diagonal : Sym.AntiDiagonal;
                return plan;
            }
            var key = Materials(piece);
            foreach (var prefab in ZNetScene.instance.m_prefabs)
            {
                var other = prefab != null ? prefab.GetComponent<Piece>() : null;
                if (other == null || other == piece || other.m_category != piece.m_category || Materials(other) != key) continue;
                var otherPoints = SnapsOf(other);
                if (otherPoints.Count != points.Count) continue;
                if (Mismatch(points, otherPoints, FlipLeftRight) < 0.05f && Mismatch(otherPoints, points, FlipLeftRight) < 0.05f)
                {
                    plan.Name = prefab.name;
                    return plan;
                }
            }
            var frontBack = Mismatch(points, points, FlipFrontBack);
            var best = leftRight;
            if (frontBack + 0.01f < best) { best = frontBack; plan.Sym = Sym.FrontBack; }
            if (diagonal + 0.01f < best) { best = diagonal; plan.Sym = Sym.Diagonal; }
            if (antiDiagonal + 0.01f < best) plan.Sym = Sym.AntiDiagonal;
            return plan;
        }

        private static Piece PieceOf(string name)
        {
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(name) : null;
            return prefab != null ? prefab.GetComponent<Piece>() : null;
        }

        internal static List<Vector3> SnapsOf(Piece piece)
        {
            var result = new List<Vector3>();
            if (piece == null) return result;
            Snaps.Clear();
            piece.GetSnapPoints(Snaps);
            foreach (var snap in Snaps) result.Add(piece.transform.InverseTransformPoint(snap.position));
            return result;
        }

        private static string Materials(Piece piece) =>
            piece == null || piece.m_resources == null ? "" :
            string.Join(",", piece.m_resources.Where(r => r?.m_resItem != null).Select(r => r.m_resItem.name + "x" + r.m_amount).OrderBy(s => s));

        // How far the points, flipped, are from the other set (each flipped point to its nearest).
        private static float Mismatch(List<Vector3> points, List<Vector3> other, Func<Vector3, Vector3> flip)
        {
            var total = 0f;
            foreach (var p in points)
            {
                var q = flip(p);
                var best = float.MaxValue;
                foreach (var o in other) best = Mathf.Min(best, (q - o).sqrMagnitude);
                total += Mathf.Sqrt(best);
            }
            return total;
        }

        private static Piece FindTwin(string name, Vector3 at, Quaternion rot, Piece except)
        {
            foreach (var piece in Piece.s_allPieces)
                if (piece != null && piece != except && (piece.transform.position - at).sqrMagnitude < 0.0025f && Utils.GetPrefabName(piece.gameObject) == name &&
                    SameSpot(name, piece.transform.position, piece.transform.rotation, at, rot))
                    return piece;
            return null;
        }

        private static readonly Dictionary<string, List<Vector3>> SnapCache = new Dictionary<string, List<Vector3>>();

        // Two pieces of this kind at these places are the same piece: on the same spot with their snap points on each
        // other (so a piece that looks the same turned around counts as the same). Without snap points: the same spot.
        internal static bool SameSpot(string name, Vector3 posA, Quaternion rotA, Vector3 posB, Quaternion rotB)
        {
            if ((posA - posB).sqrMagnitude >= 0.0025f) return false;
            if (!SnapCache.TryGetValue(name, out var snaps)) SnapCache[name] = snaps = SnapsOf(PieceOf(name));
            foreach (var s in snaps)
            {
                var a = posA + rotA * s;
                var found = false;
                foreach (var t in snaps)
                    if ((posB + rotB * t - a).sqrMagnitude < 0.0025f)
                    {
                        found = true;
                        break;
                    }
                if (!found) return false;
            }
            return true;
        }

        // --- Placing and removing --------------------------------------------------------------------------------------

        private static void Placed(Player __instance, Piece piece, Vector3 pos, Quaternion rot)
        {
            if (Mirroring || !Active || piece == null || __instance != Player.m_localPlayer) return;
            foreach (var image in Images(new Placement { Name = Utils.GetPrefabName(piece.gameObject), Pos = pos, Rot = rot }))
            {
                if (FindTwin(image.Name, image.Pos, image.Rot, null) != null) continue;
                var place = PieceOf(image.Name) ?? piece;
                Mirroring = true;
                try { __instance.PlacePiece(place, image.Pos, image.Rot, false, false); }
                finally { Mirroring = false; }
            }
        }

        private static void Removing(WearNTear __instance)
        {
            if (Mirroring || !Active || __instance == null) return;
            var piece = __instance.GetComponent<Piece>();
            if (piece == null) return;
            var p = new Placement { Name = Utils.GetPrefabName(piece.gameObject), Pos = piece.transform.position, Rot = piece.transform.rotation };
            foreach (var image in Images(p))
            {
                var twin = FindTwin(image.Name, image.Pos, image.Rot, piece);
                var wear = twin != null ? twin.GetComponent<WearNTear>() : null;
                if (wear == null) continue;
                Mirroring = true;
                try { wear.Remove(); }
                finally { Mirroring = false; }
            }
        }

        // --- Keys and the lines --------------------------------------------------------------------------------------

        private static bool Typing() =>
            (Chat.instance != null && Chat.instance.HasFocus()) || global::Console.IsVisible() || TextInput.IsVisible() ||
            Menu.IsVisible() || InventoryGui.IsVisible() || Minimap.IsOpen() || Hud.IsPieceSelectionVisible() ||
            (Hud.instance != null && Hud.instance.m_buildUi != null && Hud.instance.m_buildUi.SearchFieldFocused);

        private static void Say(Player player, string text) => player.Message(MessageHud.MessageType.Center, text);

        private void Update()
        {
            var player = Player.m_localPlayer;
            var hammer = player != null && On != null && On.Value && InCreative() && player.InPlaceMode();
            ShowLines(hammer);
            if (!hammer || Typing()) return;
            // The modifier keys first: Shift+N and Ctrl+N would also count as N.
            if (ClearKey.Value.IsDown())
            {
                Lines[0] = Lines[1] = null;
                _settingLine = -1;
                Say(player, "Mirror lines removed");
            }
            else if (BuildingKey.Value.IsDown())
            {
                if (!HasLine) Say(player, $"Set a mirror line first: {PointKey.Value} on two points");
                else if (!_busy) StartCoroutine(MirrorBuilding(player));
            }
            else if (SecondKey.Value.IsDown()) SetLine(player, 1, SecondKey.Value);
            else if (PointKey.Value.IsDown()) SetLine(player, 0, PointKey.Value);
        }

        private static void SetLine(Player player, int index, KeyboardShortcut key)
        {
            var which = index == 0 ? "Mirror line" : "Second mirror line";
            if (!Aim(out var point, out var aimed, false))
            {
                Say(player, "Aim at a building piece or the ground");
                return;
            }
            // On a piece: the line runs through its middle, along its own axis closest to where you look, so the mirror
            // sits exactly on the building's grid (aim at the middle piece, e.g. the front door, facing it).
            if (aimed != null && aimed.IsPlacedByPlayer())
            {
                var look = GameCamera.instance.transform.forward;
                look.y = 0f;
                var right = aimed.transform.right;
                right.y = 0f;
                var forward = aimed.transform.forward;
                forward.y = 0f;
                var axis = Mathf.Abs(Vector3.Dot(right.normalized, look.normalized)) > Mathf.Abs(Vector3.Dot(forward.normalized, look.normalized)) ? right : forward;
                if (axis.sqrMagnitude < 0.01f) axis = Vector3.forward;
                // Across the line: its middle or one of its edges/corners (snap points), whichever is nearest to where
                // you aim.
                var middle = BoundsOf(aimed).center;
                middle.y = aimed.transform.position.y;
                var normal = Vector3.Cross(Vector3.up, axis.normalized).normalized;
                var aimedAt = Vector3.Dot(point - middle, normal);
                var offset = 0f;
                Snaps.Clear();
                aimed.GetSnapPoints(Snaps);
                foreach (var snap in Snaps)
                {
                    var o = Vector3.Dot(snap.position - middle, normal);
                    if (Mathf.Abs(o - aimedAt) < Mathf.Abs(offset - aimedAt)) offset = o;
                }
                Lines[index] = new Line { Origin = middle + normal * offset, Normal = normal };
                _settingLine = -1;
                var where = Mathf.Abs(offset) < 0.05f ? "through the middle of" : "along the edge of";
                Say(player, $"{which} set {where} the {Localization.instance.Localize(aimed.m_name)}. {Hint()}");
                return;
            }
            // On the ground: two points (snapped to a nearby piece's snap point).
            Aim(out point, out _);
            if (_settingLine != index)
            {
                _first = point;
                _settingLine = index;
                Say(player, $"{which}: first point set. Now {key} on the second point (or {key} on a piece: a line through its middle)");
                return;
            }
            var along = point - _first;
            along.y = 0f;
            if (along.magnitude < 0.2f)
            {
                Say(player, "The second point is too close to the first");
                return;
            }
            Lines[index] = new Line { Origin = _first, Normal = Vector3.Cross(Vector3.up, along.normalized).normalized };
            _settingLine = -1;
            Say(player, $"{which} set: placing and removing is mirrored. {Hint()}");
        }

        private static string Hint()
        {
            var lines = ActiveLines.Count();
            var more = lines == 1 ? $" {SecondKey.Value}: a second line (four quarters)." : " Both lines: every piece goes into all four quarters.";
            if (lines == 2 && Vector3.Angle(Lines[0].Normal, Lines[1].Normal) is var angle && Mathf.Abs(angle - 90f) > 2f && Mathf.Abs(angle - 90f) < 88f)
                more += " (The lines are not square to each other, so the quarters do not match up.)";
            return $"{BuildingKey.Value}: copy a whole side. {ClearKey.Value}: remove the lines.{more}";
        }

        // Where the player aims, snapped to the nearest snap point of a piece within 1 m.
        private static bool Aim(out Vector3 point, out Piece piece, bool snapToPoints = true)
        {
            point = Vector3.zero;
            piece = null;
            var camera = GameCamera.instance;
            if (camera == null) return false;
            var mask = LayerMask.GetMask("Default", "static_solid", "terrain", "piece", "piece_nonsolid", "vehicle", "Default_small");
            if (!Physics.Raycast(camera.transform.position, camera.transform.forward, out var hit, 200f, mask)) return false;
            point = hit.point;
            piece = hit.collider.GetComponentInParent<Piece>();
            if (piece == null || !snapToPoints) return true;
            Snaps.Clear();
            piece.GetSnapPoints(Snaps);
            var best = 1f;
            foreach (var snap in Snaps)
            {
                var d = Vector3.Distance(snap.position, hit.point);
                if (d < best)
                {
                    best = d;
                    point = snap.position;
                }
            }
            return true;
        }

        private void ShowLines(bool hammer)
        {
            for (var i = 0; i < 2; i++)
            {
                var line = Lines[i];
                var settingThis = _settingLine == i;
                if (!hammer || (line == null && !settingThis))
                {
                    if (_ground[i] != null) _ground[i].enabled = false;
                    if (_post[i] != null) _post[i].enabled = false;
                    continue;
                }
                if (_ground[i] == null)
                {
                    _ground[i] = NewLine("Digi mirror line " + (i + 1), LineColours[i]);
                    _post[i] = NewLine("Digi mirror post " + (i + 1), LineColours[i]);
                    if (_ground[i] == null || _post[i] == null) return;
                }
                if (line != null)
                {
                    var along = Vector3.Cross(line.Normal, Vector3.up).normalized;
                    var y = new Vector3(0f, 0.05f, 0f);
                    _ground[i].enabled = true;
                    _ground[i].SetPosition(0, line.Origin - along * 150f + y);
                    _ground[i].SetPosition(1, line.Origin + along * 150f + y);
                }
                else _ground[i].enabled = false;
                var post = line != null && !settingThis ? line.Origin : _first;
                _post[i].enabled = true;
                _post[i].SetPosition(0, post);
                _post[i].SetPosition(1, post + Vector3.up * 30f);
            }
        }

        private LineRenderer NewLine(string name, Color colour)
        {
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
            if (shader == null) return null;
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var line = go.AddComponent<LineRenderer>();
            line.material = new Material(shader);
            line.startColor = line.endColor = colour;
            line.startWidth = line.endWidth = 0.08f;
            line.positionCount = 2;
            line.useWorldSpace = true;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            return line;
        }

        private void OnDestroy()
        {
            foreach (var line in _ground.Concat(_post))
                if (line != null) Destroy(line.gameObject);
        }

        // --- Mirroring a whole building ------------------------------------------------------------------------------

        private static Bounds BoundsOf(Piece piece)
        {
            var bounds = new Bounds(piece.transform.position, Vector3.zero);
            foreach (var collider in piece.GetComponentsInChildren<Collider>())
                if (collider.enabled && !collider.isTrigger) bounds.Encapsulate(collider.bounds);
            return bounds;
        }

        private static Vector3Int Cell(Vector3 p) => Vector3Int.FloorToInt(p * 4f);

        private IEnumerator MirrorBuilding(Player player)
        {
            if (!Aim(out _, out var start) || start == null || !start.IsPlacedByPlayer())
            {
                Say(player, "Aim at a piece of the building to mirror");
                yield break;
            }
            _busy = true;
            // Every piece touching the start, and every piece touching those.
            var found = new HashSet<Piece> { start };
            var queue = new Queue<Piece>();
            queue.Enqueue(start);
            var hits = new Collider[128];
            var mask = LayerMask.GetMask("piece", "piece_nonsolid");
            var steps = 0;
            while (queue.Count > 0 && found.Count < 20000)
            {
                var piece = queue.Dequeue();
                if (piece == null) continue;
                var bounds = BoundsOf(piece);
                var count = Physics.OverlapBoxNonAlloc(bounds.center, bounds.extents + Vector3.one * 0.1f, hits, Quaternion.identity, mask);
                for (var i = 0; i < count; i++)
                {
                    var other = hits[i] != null ? hits[i].GetComponentInParent<Piece>() : null;
                    if (other != null && other.IsPlacedByPlayer() && found.Add(other)) queue.Enqueue(other);
                }
                if (++steps % 150 == 0) yield return null;
            }
            // The side (quarter) you aimed at is copied; for a line the piece is on: the side you stand on.
            var lines = ActiveLines.ToList();
            var signs = new float[lines.Count];
            for (var i = 0; i < lines.Count; i++)
            {
                var aimed = lines[i].Side(start.transform.position);
                if (Mathf.Abs(aimed) < 0.05f) aimed = lines[i].Side(player.transform.position);
                if (Mathf.Abs(aimed) < 0.05f)
                {
                    _busy = false;
                    Say(player, "Aim at a piece on the side you want to copy");
                    yield break;
                }
                signs[i] = Mathf.Sign(aimed);
            }
            // Outside the copied side: past a line on its other side (pieces on a line belong to both sides).
            bool Outside(Vector3 p)
            {
                for (var i = 0; i < lines.Count; i++)
                    if (lines[i].Side(p) * signs[i] <= -0.05f) return true;
                return false;
            }
            var source = found.Where(p => p != null && !Outside(p.transform.position)).ToList();

            // First clear the other sides: the building's pieces there, and any of your pieces where the copies will go.
            var area = new Bounds();
            var first = true;
            foreach (var piece in source)
            {
                var b = BoundsOf(piece);
                for (var k = 0; k < 8; k++)
                {
                    var corner = new Vector3((k & 1) == 0 ? b.min.x : b.max.x, (k & 2) == 0 ? b.min.y : b.max.y, (k & 4) == 0 ? b.min.z : b.max.z);
                    foreach (var image in Images(new Placement { Name = "", Pos = corner, Rot = Quaternion.identity }, true))
                    {
                        if (first) { area = new Bounds(image.Pos, Vector3.zero); first = false; }
                        else area.Encapsulate(image.Pos);
                    }
                }
            }
            area.Expand(new Vector3(1f, 1f, 1f));
            var clear = new HashSet<Piece>(found.Where(p => p != null && Outside(p.transform.position)));
            if (!first)
                foreach (var piece in Piece.s_allPieces)
                    if (piece != null && piece.IsPlacedByPlayer() && Outside(piece.transform.position) && area.Contains(piece.transform.position))
                        clear.Add(piece);
            var removed = 0;
            foreach (var piece in clear.ToList())
            {
                if (piece == null) continue;
                Mirroring = true; // not mirrored back
                try
                {
                    var wear = piece.GetComponent<WearNTear>();
                    if (wear != null) wear.Remove(true);
                    else ZNetScene.instance.Destroy(piece.gameObject);
                }
                finally { Mirroring = false; }
                removed++;
                if (removed % 60 == 0) yield return null;
            }
            yield return null;
            yield return null;

            // What stands where now, to skip pieces whose mirror is already there.
            var index = new Dictionary<Vector3Int, List<Piece>>();
            foreach (var piece in Piece.s_allPieces)
            {
                if (piece == null) continue;
                var cell = Cell(piece.transform.position);
                if (!index.TryGetValue(cell, out var list)) index[cell] = list = new List<Piece>();
                list.Add(piece);
            }
            var placed = 0;
            var skipped = 0;
            foreach (var piece in source)
            {
                if (piece == null) continue;
                var p = new Placement { Name = Utils.GetPrefabName(piece.gameObject), Pos = piece.transform.position, Rot = piece.transform.rotation };
                foreach (var image in Images(p))
                {
                    if (HasTwin(index, image.Name, image.Pos, image.Rot)) { skipped++; continue; }
                    var prefabPiece = PieceOf(image.Name);
                    if (prefabPiece == null) continue;
                    Mirroring = true;
                    try { player.PlacePiece(prefabPiece, image.Pos, image.Rot, false, false); }
                    finally { Mirroring = false; }
                    placed++;
                    if (placed % 40 == 0) yield return null;
                }
            }
            _busy = false;
            var where = lines.Count == 2 ? "the other three quarters" : "the other side";
            Say(player, $"Copied {placed} pieces to {where} (cleared {removed} there first; {found.Count} in the building)");
        }

        // Only the places (for the area to clear): no piece kinds involved.
        private static List<Placement> Images(Placement p, bool placesOnly)
        {
            if (!placesOnly) return Images(p);
            var result = new List<Placement>();
            var lines = ActiveLines.ToList();
            foreach (var line in lines) result.Add(new Placement { Pos = line.Pos(p.Pos) });
            if (lines.Count == 2) result.Add(new Placement { Pos = lines[1].Pos(lines[0].Pos(p.Pos)) });
            return result;
        }

        private static bool HasTwin(Dictionary<Vector3Int, List<Piece>> index, string name, Vector3 at, Quaternion rot)
        {
            var c = Cell(at);
            for (var x = -1; x <= 1; x++)
                for (var y = -1; y <= 1; y++)
                    for (var z = -1; z <= 1; z++)
                        if (index.TryGetValue(new Vector3Int(c.x + x, c.y + y, c.z + z), out var list))
                            foreach (var piece in list)
                                if (piece != null && (piece.transform.position - at).sqrMagnitude < 0.0025f && Utils.GetPrefabName(piece.gameObject) == name &&
                                    SameSpot(name, piece.transform.position, piece.transform.rotation, at, rot))
                                    return true;
            return false;
        }
    }
}
