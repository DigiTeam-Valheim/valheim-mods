// Shared by Ultimate Builder (its creative world: pasting is free) and Building Tweaks (normal worlds, a setting: the host
// checks and pays first): placing a saved schematic (SchematicLibrary.cs). Each mod compiles this file in.
// - After Place the building follows your aim as a ghost (copies of the pieces, only their looks, tinted light blue). The
//   scroll wheel turns it like a piece (22.5 degree steps); with the free key held it moves it up and down instead.
// - Snapping: the ghost looks for the shift (up to 2 m) that makes the most of its snap points meet snap points of the
//   buildings around it, and of those the smallest. Aimed at a building one meeting point is enough (as a single piece
//   snaps), aimed at the ground it takes two (an edge), so a schematic set next to a building lines up with it, wall on
//   wall, without jumping onto every fence post near. The free key held: no snapping.
// - Pasting goes from the bottom up, 150 pieces a frame, with one placing sound. A piece already standing in the same
//   place is left out: the same kind, within 10 cm, turned the same way or half a turn (a quarter too for square ones),
//   so two schematics that share a wall get that wall once. Pieces of a mod that is not installed are left out and named.
// - A big schematic shows at most GhostPieces pieces in the ghost, spread over it, and a box around it.
// Light: the snapping is worked out again only when the ghost moved or turned; nothing runs while no schematic is chosen.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace DigiShared
{
    internal static class SchematicPlacer
    {
        // What the mod using it brings.
        internal sealed class Host
        {
            public MonoBehaviour Runner;                 // runs the ghost building and the paste
            public Func<Player, bool> Allowed;           // the paste tool is selected (and the world allows it)
            public Func<KeyboardShortcut> FreeKey;
            public Func<float> HeightStep;
            public Func<int> GhostPieces;
            public float Reach = 150f;                   // how far you may aim
            public Func<Player, Plan, bool> Check;       // before pasting (says why not itself); null: always
            public Action<bool> Placing;                 // true just before each piece is placed, false right after
            public Action<Player, Plan, List<Piece>> Done; // after pasting (the message is the host's)
        }

        // One paste: what will be placed, at its place in the world.
        internal sealed class Item
        {
            public Piece Piece;
            public string Prefab;
            public Vector3 Pos;
            public Quaternion Rot;
        }

        internal sealed class Plan
        {
            public string Name;
            public readonly List<Item> Items = new List<Item>();
            public int AlreadyThere;
            public readonly SortedDictionary<string, int> Missing = new SortedDictionary<string, int>(); // prefabs no mod brings
        }

        private const int PastePerFrame = 150;
        private const float MaxShift = 2f, Same = 0.1f;
        private static readonly Color GhostTint = new Color(0.12f, 0.3f, 0.4f);
        private static readonly EffectList NoEffects = new EffectList { m_effectPrefabs = new EffectList.EffectData[0] };

        private static Host _host;
        private static string _name, _file;
        private static List<SchematicLibrary.Entry> _entries;
        private static List<Vector3> _snaps;
        private static Vector3 _size;
        private static float _ground;
        private static GameObject _ghost;
        private static int _generation;
        private static bool _building, _pasting, _aimValid, _turnKnown;
        private static Vector3 _pos, _lastBase = new Vector3(float.NaN, 0f, 0f), _lastShift;
        private static Quaternion _rot, _lastRot;
        private static bool _lastOnPiece;
        private static float _wheel, _height;
        private static int _lastTurn;
        private static readonly Dictionary<string, List<Vector3>> PrefabSnaps = new Dictionary<string, List<Vector3>>();
        private static readonly Dictionary<string, bool> PrefabSquare = new Dictionary<string, bool>();
        private static readonly List<Transform> SnapScratch = new List<Transform>();
        private static readonly List<Piece> Around = new List<Piece>();

        // Private in the game (Building Tweaks compiles against it as it is; Ultimate Builder sees them public).
        internal static readonly AccessTools.FieldRef<Player, int> PlaceRotation = AccessTools.FieldRefAccess<Player, int>("m_placeRotation");
        internal static readonly AccessTools.FieldRef<Player, GameObject> PlacementGhost = AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost");
        internal static readonly AccessTools.FieldRef<Player, int> PlaceRayMask = AccessTools.FieldRefAccess<Player, int>("m_placeRayMask");
        internal static readonly AccessTools.FieldRef<Player, PieceTable> BuildPieces = AccessTools.FieldRefAccess<Player, PieceTable>("m_buildPieces");
        internal static readonly AccessTools.FieldRef<Player, bool> NoPlacementCost = AccessTools.FieldRefAccess<Player, bool>("m_noPlacementCost");
        private static readonly AccessTools.FieldRef<Piece, ZNetView> PieceView = AccessTools.FieldRefAccess<Piece, ZNetView>("m_nview");
        private static List<Piece> _allPieces;
        private static List<Piece> AllPieces => _allPieces ?? (_allPieces = AccessTools.Field(typeof(Piece), "s_allPieces")?.GetValue(null) as List<Piece>);

        private static bool Real(Piece piece)
        {
            if (piece == null) return false;
            var view = PieceView(piece);
            return view != null && view.IsValid();
        }

        // The piece the tool in the hand would place (the selected one in the hammer's menu).
        internal static Piece Selected(Player player)
        {
            var table = player != null ? BuildPieces(player) : null;
            return table != null ? table.GetSelectedPiece() : null;
        }

        internal static readonly List<Piece> LastPaste = new List<Piece>();
        internal static string LastPasteName = "";

        internal static bool Chosen => _entries != null;
        internal static string ChosenFile => _file;
        internal static bool Pasting => _pasting;

        // --- choosing -------------------------------------------------------------------------------------------------

        internal static void Choose(Host host, string name, string file, List<SchematicLibrary.Entry> entries, Vector3 size, float ground)
        {
            Drop();
            if (host?.Runner == null || entries == null || entries.Count == 0) return;
            _host = host;
            _name = name;
            _file = file;
            _entries = entries;
            _size = size;
            _ground = ground;
            _snaps = SnapsOf(entries);
            _height = 0f;
            _lastBase = new Vector3(float.NaN, 0f, 0f);
            host.Runner.StartCoroutine(BuildGhost(entries, size));
        }

        internal static void Drop()
        {
            _generation++;
            if (_ghost != null) UnityEngine.Object.Destroy(_ghost);
            _ghost = null;
            _entries = null;
            _name = _file = null;
            _building = false;
            _aimValid = false;
        }

        private static Piece PieceOf(string name)
        {
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(name) : null;
            return prefab != null ? prefab.GetComponent<Piece>() : null;
        }

        // A prefab's snap points, in its own space.
        private static List<Vector3> PrefabSnapsOf(string prefab)
        {
            if (PrefabSnaps.TryGetValue(prefab, out var list)) return list;
            list = new List<Vector3>();
            var piece = PieceOf(prefab);
            if (piece != null)
            {
                SnapScratch.Clear();
                piece.GetSnapPoints(SnapScratch);
                foreach (var t in SnapScratch) list.Add(piece.transform.InverseTransformPoint(t.position));
            }
            PrefabSnaps[prefab] = list;
            return list;
        }

        private static List<Vector3> SnapsOf(List<SchematicLibrary.Entry> entries)
        {
            var result = new List<Vector3>();
            foreach (var e in entries)
                foreach (var p in PrefabSnapsOf(e.Prefab)) result.Add(e.Pos + e.Rot * p);
            return result;
        }

        // Square from above (its snap points as wide as deep): a quarter turn of it covers the same place.
        private static bool Square(string prefab)
        {
            if (PrefabSquare.TryGetValue(prefab, out var yes)) return yes;
            var snaps = PrefabSnapsOf(prefab);
            if (snaps.Count >= 2)
            {
                float minX = snaps.Min(p => p.x), maxX = snaps.Max(p => p.x), minZ = snaps.Min(p => p.z), maxZ = snaps.Max(p => p.z);
                yes = maxX - minX > 0.1f && Mathf.Abs((maxX - minX) - (maxZ - minZ)) < 0.05f;
            }
            PrefabSquare[prefab] = yes;
            return yes;
        }

        // --- the ghost ------------------------------------------------------------------------------------------------

        // Copies of (at most GhostPieces of) the pieces, made like the game's own placement ghost, then stripped to their
        // looks; built a few dozen a frame.
        private static IEnumerator BuildGhost(List<SchematicLibrary.Entry> entries, Vector3 size)
        {
            var generation = ++_generation;
            _building = true;
            var root = new GameObject("Digi_SchematicGhost");
            root.SetActive(false);
            _ghost = root;
            var most = _host.GhostPieces != null ? Mathf.Max(50, _host.GhostPieces()) : 1500;
            var step = entries.Count > most ? (float)entries.Count / most : 1f;
            var made = 0;
            for (var f = 0f; f < entries.Count; f += step)
            {
                if (generation != _generation || root == null) yield break;
                var e = entries[(int)f];
                var copy = GhostCopy(e.Prefab);
                if (copy != null)
                {
                    copy.transform.SetParent(root.transform, false);
                    copy.transform.localPosition = e.Pos;
                    copy.transform.localRotation = e.Rot;
                }
                if (++made % 40 == 0) yield return null;
            }
            if (step > 1f) Box(root, size);
            if (generation == _generation) _building = false;
        }

        private static GameObject GhostCopy(string name)
        {
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(name) : null;
            if (prefab == null) return null;
            var modifier = prefab.GetComponentInChildren<TerrainModifier>();
            var modifierOn = modifier != null && modifier.enabled;
            if (modifier != null) modifier.enabled = false;
            GameObject go;
            ZNetView.m_forceDisableInit = true;
            TerrainOp.m_forceDisableTerrainOps = true;
            try { go = UnityEngine.Object.Instantiate(prefab, new Vector3(0f, -5000f, 0f), Quaternion.identity); }
            finally
            {
                ZNetView.m_forceDisableInit = false;
                TerrainOp.m_forceDisableTerrainOps = false;
                if (modifier != null) modifier.enabled = modifierOn;
            }
            go.name = name + " (ghost)";
            // Only the looks: no scripts (the pieces' own lists, comfort, fires, ...), no physics, lights or sounds.
            var scripts = go.GetComponentsInChildren<MonoBehaviour>(true);
            for (var i = scripts.Length - 1; i >= 0; i--)
                if (scripts[i] != null) UnityEngine.Object.DestroyImmediate(scripts[i]);
            foreach (var joint in go.GetComponentsInChildren<Joint>(true)) UnityEngine.Object.DestroyImmediate(joint);
            foreach (var body in go.GetComponentsInChildren<Rigidbody>(true)) UnityEngine.Object.DestroyImmediate(body);
            foreach (var collider in go.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(collider);
            foreach (var light in go.GetComponentsInChildren<Light>(true)) UnityEngine.Object.DestroyImmediate(light);
            // Sounds and particle effects, by name (their Unity modules are not referenced here).
            foreach (var part in go.GetComponentsInChildren<Component>(true))
            {
                if (part == null) continue;
                var type = part.GetType().Name;
                if (type == "AudioSource") UnityEngine.Object.DestroyImmediate(part);
                else if (type == "ParticleSystem") part.gameObject.SetActive(false);
            }
            var layer = LayerMask.NameToLayer("ghost");
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
            if (MaterialMan.instance != null) MaterialMan.instance.SetValue(go, ShaderProps._EmissionColor, GhostTint);
            return go;
        }

        // A box around the whole schematic (for a ghost that shows only part of it).
        private static void Box(GameObject root, Vector3 size)
        {
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
            if (shader == null) return;
            var go = new GameObject("Box");
            go.transform.SetParent(root.transform, false);
            var line = go.AddComponent<LineRenderer>();
            line.material = new Material(shader);
            line.startColor = line.endColor = new Color(0.2f, 0.9f, 1f, 0.85f);
            line.startWidth = line.endWidth = 0.08f;
            line.useWorldSpace = false;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            float x = size.x * 0.5f, z = size.z * 0.5f, h = size.y;
            Vector3 b0 = new Vector3(-x, 0f, -z), b1 = new Vector3(x, 0f, -z), b2 = new Vector3(x, 0f, z), b3 = new Vector3(-x, 0f, z);
            Vector3 t0 = b0 + Vector3.up * h, t1 = b1 + Vector3.up * h, t2 = b2 + Vector3.up * h, t3 = b3 + Vector3.up * h;
            var path = new[] { b0, b1, b2, b3, b0, t0, t1, b1, t1, t2, b2, t2, t3, b3, t3, t0 };
            line.positionCount = path.Length;
            line.SetPositions(path);
        }

        // --- following the aim (the host calls this from its LateUpdate) ----------------------------------------------

        internal static void Tick()
        {
            if (_entries == null || _host == null) return;
            if (_ghost == null && !_building)
            {
                Drop(); // the world was left: pick it again from the list
                return;
            }
            var player = Player.m_localPlayer;
            var show = player != null && player.InPlaceMode() && (_host.Allowed == null || _host.Allowed(player)) && !_building &&
                       !_pasting && !Windows.IsOpen && !TextInput.IsVisible() && !Menu.IsVisible();
            _aimValid = false;
            if (!show || !Aim(player, out var hit, out var onPiece))
            {
                if (_ghost != null && _ghost.activeSelf) _ghost.SetActive(false);
                _turnKnown = false;
                return;
            }
            // The wheel: the game turns its own ghost with it (and keeps the camera from zooming meanwhile); beyond its
            // reach it shows none, then the wheel is read here. With the free key held a wheel step moves the schematic up
            // or down instead: the game's turn is taken back.
            var free = _host.FreeKey != null && _host.FreeKey().IsPressed();
            var ghost = PlacementGhost(player);
            var gameTurns = ghost != null && ghost.activeInHierarchy;
            var steps = 0;
            if (gameTurns) steps = _turnKnown ? PlaceRotation(player) - _lastTurn : 0;
            else
            {
                _wheel += ZInput.GetMouseScrollWheel();
                if (_wheel > player.m_scrollAmountThreshold) steps = 1;
                else if (_wheel < -player.m_scrollAmountThreshold) steps = -1;
                if (steps != 0) _wheel = 0f;
            }
            if (steps != 0 && free)
            {
                if (gameTurns) PlaceRotation(player) -= steps;
                _height += steps * (_host.HeightStep != null ? _host.HeightStep() : 0.25f);
                player.Message(MessageHud.MessageType.Center, $"Height {(_height >= 0f ? "+" : "")}{_height:0.00} m");
            }
            else if (steps != 0 && !gameTurns) PlaceRotation(player) += steps;
            _lastTurn = PlaceRotation(player);
            _turnKnown = true;
            var rot = Quaternion.Euler(0f, 22.5f * PlaceRotation(player), 0f);
            // Aimed at a building its bottom goes where you aim; at the ground as deep in it as where it was saved; the
            // wheel's height on top; then snapped onto what is around.
            var basePos = onPiece ? hit : hit + Vector3.up * (_height - _ground);
            if (onPiece) basePos += Vector3.up * _height;
            var pos = free ? basePos : basePos + Shift(basePos, rot, onPiece);
            _ghost.transform.SetPositionAndRotation(pos, rot);
            if (!_ghost.activeSelf) _ghost.SetActive(true);
            _pos = pos;
            _rot = rot;
            _aimValid = true;
        }

        private static bool Aim(Player player, out Vector3 point, out bool onPiece)
        {
            point = Vector3.zero;
            onPiece = false;
            var cam = GameCamera.instance;
            if (cam == null) return false;
            if (!Physics.Raycast(cam.transform.position, cam.transform.forward, out var hit, _host.Reach, PlaceRayMask(player), QueryTriggerInteraction.Ignore))
                return false;
            point = hit.point;
            onPiece = hit.collider != null && hit.collider.GetComponentInParent<Piece>() != null;
            return true;
        }

        // The snapping shift: worked out again only when the ghost moved (1 cm) or turned.
        private static Vector3 Shift(Vector3 basePos, Quaternion rot, bool onPiece)
        {
            if ((basePos - _lastBase).sqrMagnitude < 0.0001f && rot == _lastRot && onPiece == _lastOnPiece) return _lastShift;
            _lastBase = basePos;
            _lastRot = rot;
            _lastOnPiece = onPiece;
            _lastShift = BestShift(basePos, rot, onPiece ? 1 : 2);
            return _lastShift;
        }

        // Every pair of a ghost snap point and a building snap point within MaxShift gives a shift; the shift most pairs
        // agree on (to 5 cm) is where the most points meet; of equal ones the smallest. Zero when too few meet.
        private static Vector3 BestShift(Vector3 basePos, Quaternion rot, int need)
        {
            if (_snaps == null || _snaps.Count == 0) return Vector3.zero;
            var radius = _size.magnitude * 0.5f + MaxShift + 1f;
            var center = basePos + Vector3.up * (_size.y * 0.5f);
            Around.Clear();
            Piece.GetAllPiecesInRadius(center, radius + 4f, Around); // + 4: a piece's middle may lie that far from its snap points
            var grid = new Dictionary<Vector3Int, List<Vector3>>();
            var any = false;
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            foreach (var piece in Around)
            {
                if (!Real(piece)) continue;
                SnapScratch.Clear();
                piece.GetSnapPoints(SnapScratch);
                foreach (var t in SnapScratch)
                {
                    var p = t.position;
                    var cell = Cell(p);
                    if (!grid.TryGetValue(cell, out var list)) grid[cell] = list = new List<Vector3>();
                    list.Add(p);
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                    any = true;
                }
            }
            if (!any) return Vector3.zero;
            min -= Vector3.one * MaxShift;
            max += Vector3.one * MaxShift;
            var counts = new Dictionary<Vector3Int, int>();
            var shifts = new Dictionary<Vector3Int, Vector3>();
            foreach (var s in _snaps)
            {
                var w = basePos + rot * s;
                if (w.x < min.x || w.y < min.y || w.z < min.z || w.x > max.x || w.y > max.y || w.z > max.z) continue;
                var c = Cell(w);
                for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                for (var dz = -1; dz <= 1; dz++)
                {
                    if (!grid.TryGetValue(new Vector3Int(c.x + dx, c.y + dy, c.z + dz), out var list)) continue;
                    foreach (var t in list)
                    {
                        var shift = t - w;
                        if (shift.sqrMagnitude > MaxShift * MaxShift) continue;
                        var key = new Vector3Int(Mathf.RoundToInt(shift.x * 20f), Mathf.RoundToInt(shift.y * 20f), Mathf.RoundToInt(shift.z * 20f));
                        counts.TryGetValue(key, out var n);
                        counts[key] = n + 1;
                        if (n == 0) shifts[key] = shift;
                    }
                }
            }
            var best = Vector3.zero;
            var bestCount = need - 1;
            var bestSize = float.MaxValue;
            foreach (var pair in counts)
            {
                var size = shifts[pair.Key].sqrMagnitude;
                if (pair.Value > bestCount || (pair.Value == bestCount && pair.Value >= need && size < bestSize))
                {
                    best = shifts[pair.Key];
                    bestCount = pair.Value;
                    bestSize = size;
                }
            }
            return bestCount >= need ? best : Vector3.zero;
        }

        private static Vector3Int Cell(Vector3 p) =>
            new Vector3Int(Mathf.FloorToInt(p.x / MaxShift), Mathf.FloorToInt(p.y / MaxShift), Mathf.FloorToInt(p.z / MaxShift));

        // --- pasting --------------------------------------------------------------------------------------------------

        internal static bool CanPaste => _entries != null && _aimValid && _ghost != null && !_building && !_pasting;

        // A click: paste where the ghost is.
        internal static void Paste(Player player)
        {
            if (!CanPaste || player == null || _host?.Runner == null) return;
            var plan = MakePlan(_pos, _rot);
            if (_host.Check != null && !_host.Check(player, plan)) return;
            _host.Runner.StartCoroutine(PasteRun(player, plan));
        }

        // What a paste here places: every piece at its place in the world, without those already standing there.
        internal static Plan MakePlan(Vector3 pos, Quaternion rot)
        {
            var plan = new Plan { Name = _name };
            var radius = _size.magnitude * 0.5f + 2f;
            Around.Clear();
            Piece.GetAllPiecesInRadius(pos + Vector3.up * (_size.y * 0.5f), radius + 4f, Around);
            var standing = new Dictionary<Vector3Int, List<Piece>>();
            foreach (var piece in Around)
            {
                if (!Real(piece)) continue;
                var cell = Vector3Int.FloorToInt(piece.transform.position);
                if (!standing.TryGetValue(cell, out var list)) standing[cell] = list = new List<Piece>();
                list.Add(piece);
            }
            foreach (var e in _entries)
            {
                var piece = PieceOf(e.Prefab);
                if (piece == null)
                {
                    plan.Missing.TryGetValue(e.Prefab, out var n);
                    plan.Missing[e.Prefab] = n + 1;
                    continue;
                }
                var p = pos + rot * e.Pos;
                var r = rot * e.Rot;
                if (AlreadyThere(standing, e.Prefab, p, r))
                {
                    plan.AlreadyThere++;
                    continue;
                }
                plan.Items.Add(new Item { Piece = piece, Prefab = e.Prefab, Pos = p, Rot = r });
            }
            return plan;
        }

        private static bool AlreadyThere(Dictionary<Vector3Int, List<Piece>> standing, string prefab, Vector3 p, Quaternion r)
        {
            var c = Vector3Int.FloorToInt(p);
            Vector3 up = r * Vector3.up, forward = r * Vector3.forward;
            for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
            for (var dz = -1; dz <= 1; dz++)
            {
                if (!standing.TryGetValue(new Vector3Int(c.x + dx, c.y + dy, c.z + dz), out var list)) continue;
                foreach (var q in list)
                {
                    if (q == null || (q.transform.position - p).sqrMagnitude > Same * Same) continue;
                    if (Utils.GetPrefabName(q.gameObject) != prefab) continue;
                    if (Vector3.Dot(q.transform.up, up) < 0.98f) continue;
                    var along = Mathf.Abs(Vector3.Dot(q.transform.forward, forward));
                    if (along > 0.98f || (along < 0.17f && Square(prefab))) return true;
                }
            }
            return false;
        }

        // From the bottom up, PastePerFrame pieces a frame, with one placing sound.
        private static IEnumerator PasteRun(Player player, Plan plan)
        {
            _pasting = true;
            var turn = PlaceRotation(player); // pieces that start turned at random change it
            var placed = 0;
            var made = new List<Piece>();
            LastPaste.Clear();
            LastPasteName = plan.Name;
            try
            {
                foreach (var item in plan.Items.OrderBy(x => x.Pos.y))
                {
                    if (player == null) yield break;
                    var piece = item.Piece;
                    var effects = piece.m_placeEffect;
                    if (placed > 0) piece.m_placeEffect = NoEffects;
                    _host?.Placing?.Invoke(true);
                    try
                    {
                        player.PlacePiece(piece, item.Pos, item.Rot, false, false);
                        // The new piece: a piece joins the end of the game's list of pieces when it is made.
                        var all = AllPieces;
                        var last = all != null && all.Count > 0 ? all[all.Count - 1] : null;
                        if (last != null && Utils.GetPrefabName(last.gameObject) == item.Prefab) made.Add(last);
                    }
                    finally
                    {
                        _host?.Placing?.Invoke(false);
                        piece.m_placeEffect = effects;
                    }
                    if (++placed % PastePerFrame == 0) yield return null;
                }
            }
            finally
            {
                _pasting = false;
                if (player != null) PlaceRotation(player) = turn;
                LastPaste.AddRange(made);
            }
            _lastBase = new Vector3(float.NaN, 0f, 0f); // what stands around changed: snap again
            _host?.Done?.Invoke(player, plan, made);
        }

        // "X left out, from mods not installed: a, b, ..." or "".
        internal static string MissingText(Plan plan)
        {
            if (plan.Missing.Count == 0) return "";
            return $"{plan.Missing.Values.Sum()} left out, from mods not installed: {string.Join(", ", plan.Missing.Keys.Take(4))}" +
                   (plan.Missing.Count > 4 ? ", ..." : "");
        }
    }
}
