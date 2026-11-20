// Shared by Building Tweaks (its Stability Meter sub-mod) and Ultimate Builder (each mod compiles this file in and has
// its own copy of the state): a small meter just below the crosshair while the hammer is out, for the building piece you
// aim at. It shows how stable that piece will be once the game has finished working out its support, and turns red
// with "Will collapse" when the piece is going to come down.
// Why not simply the game's own number: the game updates a piece's support a little at a time (WearNTear.UpdateSupport,
// one piece after the other in its list, about 50 a frame, each from its neighbours' current values), so a new piece
// first looks fine and can come down a while later, when the lower values have worked their way to it. This works out
// the end result at once by playing the game's own rule forward on a copy, to the end:
// - It reads the building the way the game does: the same overlap boxes, distances, support points (rests) and
//   material values. A piece touching the ground (or anything that is no building piece) has full support, so the
//   search stops there. At most MaxPieces pieces; beyond that the game's current values count.
// - It starts from the game's current state of each piece: its support now, the neighbours it remembered (the game
//   skips a piece whose remembered neighbours below have not changed, even when a piece above it has: so a building
//   placed in one go can settle higher than a piece-by-piece one), a new piece's "tell the neighbours" flag, and the
//   order of the game's list.
// - Then it repeats the game's rounds over those pieces until a round changes nothing. A piece below its minimum
//   breaks at its first update (the support damage is its full health), unless it cannot be removed (e.g. a chest with
//   items) or the world has "no building fall"; a broken piece's neighbours lose it.
// Tested against the game itself (a stack of walls placed in one go, a post with beams out to the side, a floating beam):
// the same end values and the same pieces falling.
// Speed: while the hammer is out the building in front of you (or the nearest one) is worked out before you aim at it;
// what each piece touches is kept (Cache) until something is built, loaded or removed within a few metres, so looking
// again only replays the rounds; results of several buildings are kept and shown at once, also while a fresh one is
// worked out in the background (every Refresh seconds, and after you build); a piece that has no result yet gets more
// time per frame.
// The percentage: 0% = just enough support to stand (the material's minimum), 100% = full support (on the ground).
// The colour follows the game's own support colours (the hammer's highlight): blue full, green good, red weak.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DigiShared
{
    internal class StabilityMeter : MonoBehaviour
    {
        // Set by the host mod: whether the meter may show now, and whether to add the support value itself.
        internal static Func<bool> Wanted = () => true;
        internal static Func<bool> ShowValue = () => false;

        private const int MaxPieces = 5000;
        private const int MaxRounds = 3000;
        private const int MaxSteps = 3000000;
        private const float UrgentBudget = 0.010f;     // seconds of work per frame while the aimed piece has no result yet
        private const float BackgroundBudget = 0.0015f; // and while the last result is shown and a fresh one is worked out
        private const float CellSize = 8f;              // building or removing a piece forgets the cache this close (cells)
        private const float CacheAge = 60f;             // and anything older than this is read again
        private const float Refresh = 3f;          // seconds before a result is worked out again

        // The game's private parts (Building Tweaks is built against the plain game assembly).
        private static readonly MethodInfo MaterialProperties = AccessTools.Method(typeof(WearNTear), "GetMaterialProperties");
        private static readonly Func<WearNTear, float> SupportNow = AccessTools.MethodDelegate<Func<WearNTear, float>>(AccessTools.Method(typeof(WearNTear), "GetSupport"));
        private static readonly AccessTools.FieldRef<WearNTear, List<WearNTear>> RememberedPieces = AccessTools.FieldRefAccess<WearNTear, List<WearNTear>>("m_supportWearNTear");
        private static readonly AccessTools.FieldRef<WearNTear, List<float>> RememberedValues = AccessTools.FieldRefAccess<WearNTear, List<float>>("m_supportValue");
        private static readonly AccessTools.FieldRef<WearNTear, List<Collider>> RememberedColliders = AccessTools.FieldRefAccess<WearNTear, List<Collider>>("m_supportColliders");
        private static readonly AccessTools.FieldRef<WearNTear, bool> TellNeighbours = AccessTools.FieldRefAccess<WearNTear, bool>("m_clearCachedSupport");
        private static readonly AccessTools.FieldRef<WearNTear, int> ListIndex = AccessTools.FieldRefAccess<WearNTear, int>("m_myIndex");

        // Something the piece leans on: the neighbour's support times the factor is what it can get from it.
        private struct Link
        {
            public int To;
            public float Factor;
        }

        // A point where the piece rests on a neighbour (below its centre of mass); two on opposite sides average.
        private struct Rest
        {
            public int To;
            public float Factor;
        }

        // A neighbour the piece remembers, with the support it had then (To -1: a piece outside the search, Fixed value).
        private struct Memory
        {
            public int To;
            public float Value, Fixed;
        }

        private sealed class Node
        {
            public WearNTear Piece;
            public float Max, Min, Horizontal, Vertical;
            public bool Grounded;  // full support from the ground (or from something that is no building piece)
            public bool Static;    // never changes: needs no support (the game skips it) or outside the search
            public bool Outside;   // past the search limit: the game's current value
            public bool Read;      // its neighbours are read
            public bool Alive = true, CanBreak, CanBreakKnown, Tell, Skip;
            public int Order;      // its place in the game's list
            public float Value;
            public readonly List<Link> Links = new List<Link>();
            public readonly List<Rest> Rests = new List<Rest>();
            public List<int> Pairs = new List<int>();         // two rest indexes per pair (shared with its Geometry)
            public List<WearNTear> Touching = new List<WearNTear>(); // told by a new piece (its first box only)
            public readonly List<Memory> Memory = new List<Memory>();
        }

        // The search: one at a time, for the piece aimed at; its result holds for every piece it read.
        private static readonly List<Node> Nodes = new List<Node>();
        private static readonly Dictionary<WearNTear, int> Index = new Dictionary<WearNTear, int>();
        private static readonly Dictionary<int, int> AtOrder = new Dictionary<int, int>(); // game list place -> node
        private static readonly List<int> Round = new List<int>();
        private static int _read, _roundPos, _rounds, _steps, _listCount;
        private static bool _solving, _changed, _reorder, _dirty, _noFall;
        private static float _solvedAt, _nextWarmUp;
        private static int _prepared;
        private static readonly Dictionary<WearNTear.MaterialType, float[]> Materials = new Dictionary<WearNTear.MaterialType, float[]>();

        private static readonly Collider[] Hits = new Collider[128]; // as many as the game looks at
        private static readonly List<Collider> HitColliders = new List<Collider>();
        private static readonly List<WearNTear> HitPieces = new List<WearNTear>();
        private static readonly List<Collider> Own = new List<Collider>();
        private static int _mask, _pieceMask, _terrain = -1;

        // The last finished result, for every piece it covered: shown at once, also while a new one is worked out.
        private struct Shown
        {
            public float Value, Max, Min;
            public bool Breaks;
        }

        private static readonly Dictionary<WearNTear, Shown> Result = new Dictionary<WearNTear, Shown>();

        // What a piece touches (its overlap boxes read once): neighbours as pieces, so any search can use it.
        private sealed class Geometry
        {
            public bool Grounded;
            public float At;
            public readonly List<WearNTear> Touching = new List<WearNTear>();
            public readonly List<WearNTear> LinkPieces = new List<WearNTear>();
            public readonly List<float> LinkFactors = new List<float>();
            public readonly List<WearNTear> RestPieces = new List<WearNTear>();
            public readonly List<float> RestFactors = new List<float>();
            public readonly List<int> Pairs = new List<int>();
        }

        private static readonly Dictionary<WearNTear, Geometry> Cache = new Dictionary<WearNTear, Geometry>();
        private static readonly Dictionary<long, List<WearNTear>> Cells = new Dictionary<long, List<WearNTear>>();
        private static ZNetScene _scene;

        private GameObject _root;
        private RectTransform _fill;
        private Image _fillImage;
        private TextMeshProUGUI _text;

        internal static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(WearNTear), nameof(WearNTear.OnPlaced)),
                postfix: new HarmonyMethod(typeof(StabilityMeter), nameof(Changed)));
            harmony.Patch(AccessTools.Method(typeof(WearNTear), "Awake"),
                postfix: new HarmonyMethod(typeof(StabilityMeter), nameof(Appeared)));
            harmony.Patch(AccessTools.Method(typeof(WearNTear), "OnDestroy"),
                postfix: new HarmonyMethod(typeof(StabilityMeter), nameof(Gone)));
        }

        // You built a piece: work the result out again (it can change what others lean on).
        private static void Changed() => _dirty = true;

        // A piece appeared (built by anyone, or loaded): what the pieces around it touch has changed.
        private static void Appeared(WearNTear __instance)
        {
            if (Cache.Count == 0) return;
            var view = __instance.GetComponent<ZNetView>();
            if (view == null || view.GetZDO() == null) return; // a placement ghost
            Forget(__instance.transform.position);
        }

        private static void Gone(WearNTear __instance)
        {
            if (Index.ContainsKey(__instance) || Result.Remove(__instance)) _dirty = true;
            if (Cache.Count > 0) Forget(__instance.transform.position);
        }

        private static long CellOf(Vector3 p, int dx, int dy, int dz)
        {
            long x = Mathf.FloorToInt(p.x / CellSize) + dx, y = Mathf.FloorToInt(p.y / CellSize) + dy, z = Mathf.FloorToInt(p.z / CellSize) + dz;
            return ((x & 0x1FFFFF) << 42) | ((y & 0x1FFFFF) << 21) | (z & 0x1FFFFF);
        }

        // Forget what the pieces near this spot touch (they are read again when a search needs them).
        private static void Forget(Vector3 p)
        {
            for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                    for (var dz = -1; dz <= 1; dz++)
                    {
                        var key = CellOf(p, dx, dy, dz);
                        if (!Cells.TryGetValue(key, out var list)) continue;
                        foreach (var piece in list) Cache.Remove(piece);
                        Cells.Remove(key);
                    }
        }

        private void OnDestroy()
        {
            if (_root != null) Destroy(_root);
            Clear();
            Result.Clear();
            Cache.Clear();
            Cells.Clear();
        }

        private void Update()
        {
            var player = Player.m_localPlayer;
            if (player == null || Hud.instance == null || !Wanted() || !player.InPlaceMode())
            {
                Show(false);
                return; // no work at all without the hammer
            }
            var hovered = Hud.IsPieceSelectionVisible() ? null : player.GetHoveringPiece();
            var piece = hovered != null ? hovered.GetComponent<WearNTear>() : null;
            if (piece == null)
            {
                Show(false);
                WarmUp();
                return;
            }
            var known = Result.TryGetValue(piece, out var shown);
            if (!_solving)
            {
                if (!known || _dirty || Time.time - _solvedAt > Refresh) Begin(piece);
            }
            else if (!known && !Index.ContainsKey(piece)) Begin(piece); // another building: this one first
            if (_solving)
            {
                Advance(known ? BackgroundBudget : UrgentBudget);
                if (!known) known = Result.TryGetValue(piece, out shown);
            }
            if (!known)
            {
                Show(false); // still working it out (a few frames)
                return;
            }
            Display(piece, shown);
        }

        // Nothing aimed at (yet): work out the building in front of you, or else the nearest one, so it is ready when you
        // aim at it.
        private static void WarmUp()
        {
            if (_solving)
            {
                Advance(BackgroundBudget);
                return;
            }
            if (Time.time < _nextWarmUp) return;
            _nextWarmUp = Time.time + 0.5f;
            if (_pieceMask == 0) _pieceMask = LayerMask.GetMask("piece", "piece_nonsolid");
            var target = Ahead() ?? Nearest();
            if (target == null || !target.m_noSupportWear) return;
            if (Result.ContainsKey(target) && !_dirty && Time.time - _solvedAt <= Refresh) return;
            Begin(target);
            Advance(BackgroundBudget);
        }

        private static WearNTear Ahead()
        {
            var camera = GameCamera.instance;
            if (camera == null) return null;
            var t = camera.transform;
            return Physics.Raycast(t.position, t.forward, out var hit, 60f, _pieceMask) ? hit.collider.GetComponentInParent<WearNTear>() : null;
        }

        private static WearNTear Nearest()
        {
            var at = Player.m_localPlayer.transform.position;
            var count = Physics.OverlapSphereNonAlloc(at, 15f, Hits, _pieceMask);
            WearNTear best = null;
            var bestDistance = float.MaxValue;
            for (var k = 0; k < count; k++)
            {
                var piece = Hits[k].GetComponentInParent<WearNTear>();
                if (piece == null) continue;
                var distance = (piece.transform.position - at).sqrMagnitude;
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = piece;
            }
            return best;
        }

        // --- The search ---------------------------------------------------------------------------------------------

        private static void Clear()
        {
            Nodes.Clear();
            Index.Clear();
            AtOrder.Clear();
            Round.Clear();
            Materials.Clear();
            _read = _roundPos = _rounds = _steps = 0;
            _solving = _changed = false;
            _prepared = 0;
            _reorder = true;
        }

        private static void Begin(WearNTear piece)
        {
            if (_scene != ZNetScene.instance)
            {
                // Another world (or the first one): nothing kept from before.
                _scene = ZNetScene.instance;
                Cache.Clear();
                Cells.Clear();
                Result.Clear();
            }
            if (Cache.Count > 50000)
            {
                Cache.Clear();
                Cells.Clear();
            }
            Clear();
            _dirty = false;
            _solving = true;
            if (_mask == 0) _mask = LayerMask.GetMask("piece", "Default", "static_solid", "Default_small", "terrain");
            if (_terrain < 0) _terrain = LayerMask.NameToLayer("terrain");
            _noFall = ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoBuildingFall);
            NodeOf(piece);
        }

        // A bit of the work each frame: read the pieces, take over the game's state, then play its rounds to the end.
        private static void Advance(float budget)
        {
            var until = Time.realtimeSinceStartup + budget;
            while (_read < Nodes.Count)
            {
                Read(_read++);
                if ((_read & 15) == 0 && Time.realtimeSinceStartup > until) return;
            }
            if (_prepared == 0) _listCount = WearNTear.GetAllInstances().Count;
            while (_prepared < Nodes.Count)
            {
                Prepare(_prepared++);
                if ((_prepared & 31) == 0 && Time.realtimeSinceStartup > until) return;
            }
            while (true)
            {
                if (_roundPos >= Round.Count)
                {
                    if ((_rounds > 0 && !_changed) || _rounds >= MaxRounds || _steps >= MaxSteps) break;
                    StartRound();
                    continue;
                }
                if (Step(Round[_roundPos++])) _changed = true;
                if ((++_steps & 255) == 0 && Time.realtimeSinceStartup > until) return;
            }
            _solving = false;
            _solvedAt = Time.time;
            if (Result.Count > 30000) Result.Clear();
            foreach (var node in Nodes)
                if (!node.Outside && node.Piece != null)
                    Result[node.Piece] = new Shown { Value = node.Value, Max = node.Max, Min = node.Min, Breaks = !node.Alive };
        }

        private static int NodeOf(WearNTear piece)
        {
            if (Index.TryGetValue(piece, out var i)) return i;
            var node = new Node { Piece = piece };
            Properties(node);
            if (!piece.m_noSupportWear)
            {
                // The game never works out the support of such a piece: it keeps its full support all the time.
                node.Static = node.Grounded = true;
                node.Value = node.Max;
            }
            else if (Nodes.Count >= MaxPieces)
            {
                node.Static = node.Outside = true;
                node.Value = SupportNow(piece);
            }
            i = Nodes.Count;
            Nodes.Add(node);
            Index[piece] = i;
            return i;
        }

        private static void Properties(Node node)
        {
            var type = node.Piece.m_materialType;
            if (!Materials.TryGetValue(type, out var values))
            {
                var args = new object[4];
                MaterialProperties.Invoke(node.Piece, args);
                values = new[] { (float)args[0], (float)args[1], (float)args[2], (float)args[3] };
                Materials[type] = values;
            }
            node.Max = values[0];
            node.Min = values[1];
            node.Horizontal = values[2];
            node.Vertical = values[3];
        }

        private static Vector3 Com(WearNTear piece) => piece.transform.position + piece.transform.rotation * piece.m_comOffset;

        // What the piece touches: from the cache, or measured now (and kept).
        private static void Read(int i)
        {
            var node = Nodes[i];
            if (node.Static || node.Read) return;
            node.Read = true;
            var piece = node.Piece;
            if (piece == null)
            {
                node.Static = true;
                node.Alive = false;
                return;
            }
            if (!Cache.TryGetValue(piece, out var g) || !Fresh(g))
            {
                g = Measure(piece);
                Cache[piece] = g;
                var key = CellOf(piece.transform.position, 0, 0, 0);
                if (!Cells.TryGetValue(key, out var list)) Cells[key] = list = new List<WearNTear>();
                list.Add(piece);
            }
            node.Touching = g.Touching;
            if (g.Grounded)
            {
                node.Grounded = true;
                return;
            }
            for (var k = 0; k < g.LinkPieces.Count; k++) node.Links.Add(new Link { To = NodeOf(g.LinkPieces[k]), Factor = g.LinkFactors[k] });
            for (var k = 0; k < g.RestPieces.Count; k++) node.Rests.Add(new Rest { To = NodeOf(g.RestPieces[k]), Factor = g.RestFactors[k] });
            node.Pairs = g.Pairs;
        }

        // Still right: not too old, and every neighbour it names still there.
        private static bool Fresh(Geometry g)
        {
            if (Time.time - g.At > CacheAge) return false;
            foreach (var other in g.LinkPieces)
                if (other == null) return false;
            return true;
        }

        // What the piece touches, exactly as WearNTear.UpdateSupport finds it.
        private static Geometry Measure(WearNTear piece)
        {
            var g = new Geometry { At = Time.time };
            piece.GetComponentsInChildren(true, Own);
            var com = Com(piece);
            HitColliders.Clear();
            HitPieces.Clear();
            var grounded = false;
            var first = true;
            foreach (var collider in Own)
            {
                if (collider == null || collider.isTrigger || collider.attachedRigidbody != null) continue;
                Vector3 pos, size;
                Quaternion rot;
                var box = collider as BoxCollider;
                if (box != null)
                {
                    var t = box.transform;
                    rot = t.rotation;
                    pos = t.position + t.TransformVector(box.center);
                    var scale = t.lossyScale;
                    size = new Vector3(scale.x * box.size.x, scale.y * box.size.y, scale.z * box.size.z);
                }
                else
                {
                    rot = Quaternion.identity;
                    pos = collider.bounds.center;
                    size = collider.bounds.size;
                }
                size = (size + new Vector3(0.3f, 0.3f, 0.3f)) * 0.5f;
                var count = Physics.OverlapBoxNonAlloc(pos, size, Hits, rot, _mask);
                for (var k = 0; k < count; k++)
                {
                    var hit = Hits[k];
                    if (hit.attachedRigidbody != null || hit.isTrigger || Own.Contains(hit)) continue;
                    var other = hit.GetComponentInParent<WearNTear>();
                    if (first && other != null) g.Touching.Add(other); // what a new piece tells to look again
                    if (hit.gameObject.layer == _terrain)
                    {
                        grounded = true;
                        continue;
                    }
                    if (other == null)
                    {
                        grounded = true; // a rock, a tree, ...: full support in the game too
                        continue;
                    }
                    if (!other.m_supports) continue;
                    HitColliders.Add(hit);
                    HitPieces.Add(other);
                }
                first = false;
            }
            if (grounded)
            {
                g.Grounded = true;
                return g; // full support, whatever else it touches
            }
            float horizontal, vertical;
            Losses(piece, out horizontal, out vertical);
            var rests = new List<Vector3>();
            for (var h = 0; h < HitPieces.Count; h++)
            {
                var other = HitPieces[h];
                var otherCom = Com(other);
                var distance = Vector3.Distance(com, otherCom) + 0.1f;
                var toPivot = Vector3.Distance(com, other.transform.position) + 0.1f;
                if (toPivot < distance && !piece.m_forceCorrectCOMCalculation) distance = toPivot;
                g.LinkPieces.Add(other);
                g.LinkFactors.Add(1f - horizontal * distance);
                var point = SupportPoint(com, otherCom, HitColliders[h]);
                if (point.y < com.y + 0.05f)
                {
                    var direction = (point - com).normalized;
                    if (direction.y < 0f)
                    {
                        var t = Mathf.Acos(1f - Mathf.Abs(direction.y)) / (Mathf.PI / 2f);
                        g.LinkPieces.Add(other);
                        g.LinkFactors.Add(1f - Mathf.Lerp(horizontal, vertical, t) * distance);
                    }
                    g.RestPieces.Add(other);
                    g.RestFactors.Add(1f - vertical * distance);
                    rests.Add(point);
                }
            }
            // Resting on two sides (at least 100 degrees apart, seen from above): the average of both.
            for (var l = 0; l < rests.Count - 1; l++)
            {
                var from = rests[l] - com;
                from.y = 0f;
                for (var m = l + 1; m < rests.Count; m++)
                {
                    var to = rests[m] - com;
                    to.y = 0f;
                    if (Vector3.Angle(from, to) >= 100f)
                    {
                        g.Pairs.Add(l);
                        g.Pairs.Add(m);
                    }
                }
            }
            return g;
        }

        private static void Losses(WearNTear piece, out float horizontal, out float vertical)
        {
            var type = piece.m_materialType;
            if (!Materials.TryGetValue(type, out var values))
            {
                var args = new object[4];
                MaterialProperties.Invoke(piece, args);
                values = new[] { (float)args[0], (float)args[1], (float)args[2], (float)args[3] };
                Materials[type] = values;
            }
            horizontal = values[2];
            vertical = values[3];
        }

        private static Vector3 SupportPoint(Vector3 com, Vector3 otherCom, Collider collider)
        {
            var mesh = collider as MeshCollider;
            if (mesh != null && !mesh.convex)
                return mesh.Raycast(new Ray(com, Vector3.down), out var hit, 10f) ? hit.point : (com + otherCom) * 0.5f;
            return collider.ClosestPoint(com);
        }

        // The game's state of a piece read: its support now, the neighbours it remembers, its flag, its list place.
        private static void Prepare(int i)
        {
            {
                var node = Nodes[i];
                if (node.Outside || node.Static || !node.Alive) return;
                var piece = node.Piece;
                if (piece == null)
                {
                    node.Alive = false; // gone while it was read
                    return;
                }
                node.Order = ListIndex(piece);
                AtOrder[node.Order] = i;
                node.Tell = TellNeighbours(piece);
                node.Value = node.Grounded ? node.Max : SupportNow(piece);
                var pieces = RememberedPieces(piece);
                var values = RememberedValues(piece);
                var colliders = RememberedColliders(piece);
                for (var m = 0; m < pieces.Count && m < values.Count; m++)
                {
                    var other = pieces[m];
                    if (other == null || m >= colliders.Count || colliders[m] == null)
                    {
                        node.Memory.Add(new Memory { To = -2 }); // gone: the game will look again
                        continue;
                    }
                    if (Index.TryGetValue(other, out var j) && !Nodes[j].Outside) node.Memory.Add(new Memory { To = j, Value = values[m] });
                    else node.Memory.Add(new Memory { To = -1, Value = values[m], Fixed = SupportNow(other) });
                }
            }
        }

        // One round of the game's list: the pieces read, in the order of the list.
        private static void StartRound()
        {
            _rounds++;
            _changed = false;
            _roundPos = 0;
            if (!_reorder)
            {
                foreach (var i in Round) Nodes[i].Skip = false;
                return;
            }
            _reorder = false; // only after a piece broke: the list changed
            Round.Clear();
            for (var i = 0; i < Nodes.Count; i++)
            {
                var node = Nodes[i];
                node.Skip = false;
                if (node.Alive && !node.Outside && !node.Static && node.Order >= 0) Round.Add(i);
            }
            Round.Sort((a, b) => Nodes[a].Order.CompareTo(Nodes[b].Order));
        }

        private static float ValueOf(int j) => Nodes[j].Value;

        // One piece's turn, as WearNTear.UpdateSupport (and the support damage in UpdateWear) does it. True: something
        // changed.
        private static bool Step(int i)
        {
            var node = Nodes[i];
            if (!node.Alive || node.Skip) return false;
            if (node.Grounded)
            {
                // Full support all the time; a new one still tells its neighbours to look again.
                if (!node.Tell) return false;
                TellAll(node);
                return true;
            }
            // Its remembered neighbours below unchanged, and one of them at least as strong: the game skips it.
            if (node.Memory.Count > 0)
            {
                var same = 0;
                var strongest = 0f;
                foreach (var memory in node.Memory)
                {
                    if (memory.To == -2 || (memory.To >= 0 && !Nodes[memory.To].Alive)) break;
                    var now = memory.To >= 0 ? ValueOf(memory.To) : memory.Fixed;
                    if (now > strongest) strongest = now;
                    if (!now.Equals(memory.Value)) break;
                    same++;
                }
                if (same == node.Memory.Count && strongest >= node.Value) return Damage(i);
                node.Memory.Clear();
            }
            var changed = false;
            if (node.Tell)
            {
                TellAll(node);
                changed = true;
            }
            var best = 0f;
            foreach (var link in node.Links)
                if (Nodes[link.To].Alive) best = Mathf.Max(best, ValueOf(link.To) * link.Factor);
            for (var p = 0; p + 1 < node.Pairs.Count; p += 2)
            {
                var a = node.Rests[node.Pairs[p]];
                var b = node.Rests[node.Pairs[p + 1]];
                if (!Nodes[a.To].Alive || !Nodes[b.To].Alive) continue;
                best = Mathf.Max(best, (ValueOf(a.To) * a.Factor + ValueOf(b.To) * b.Factor) * 0.5f);
            }
            foreach (var rest in node.Rests)
                if (Nodes[rest.To].Alive) node.Memory.Add(new Memory { To = rest.To, Value = ValueOf(rest.To) });
            best = Mathf.Min(best, node.Max);
            if (!best.Equals(node.Value))
            {
                node.Value = best;
                changed = true;
            }
            if (node.Value < node.Min) node.Memory.Clear();
            return Damage(i) || changed;
        }

        // Below its minimum after its turn: the support damage is its full health, so it breaks (if it may).
        private static bool Damage(int i)
        {
            var node = Nodes[i];
            if (node.Value >= node.Min) return false;
            if (!node.CanBreakKnown)
            {
                // Only asked for a piece that is too weak: a chest with items in it, for one, cannot break.
                node.CanBreakKnown = true;
                var removable = node.Piece != null ? node.Piece.GetComponent<Piece>() : null;
                node.CanBreak = !_noFall && (removable == null || removable.CanBeRemoved());
            }
            if (!node.CanBreak) return false;
            Break(i);
            return true;
        }

        // A new piece's first turn: every piece touching its first box forgets its neighbours (and looks again).
        private static void TellAll(Node node)
        {
            node.Tell = false;
            foreach (var other in node.Touching)
                if (other != null && Index.TryGetValue(other, out var j)) Nodes[j].Memory.Clear();
        }

        // The piece breaks: the last piece of the game's list takes its place there (so it waits for the next round).
        private static void Break(int i)
        {
            var node = Nodes[i];
            node.Alive = false;
            _reorder = true;
            AtOrder.Remove(node.Order);
            _listCount--;
            if (AtOrder.TryGetValue(_listCount, out var last) && last != i)
            {
                AtOrder.Remove(_listCount);
                Nodes[last].Order = node.Order;
                Nodes[last].Skip = true;
                AtOrder[node.Order] = last;
            }
        }

        // --- The meter -----------------------------------------------------------------------------------------------

        private void Display(WearNTear piece, Shown shown)
        {
            if (_root == null && !Build()) return;
            Show(true);
            float value = shown.Value, max = shown.Max, min = shown.Min;
            string text;
            Color color;
            var red = new Color(1f, 0.25f, 0.2f);
            var share = max > min ? Mathf.Clamp01((value - min) / (max - min)) : 1f;
            if (!piece.m_noSupportWear)
            {
                text = "Needs no support";
                color = new Color(0.6f, 0.8f, 1f);
                share = 1f;
            }
            else if (shown.Breaks)
            {
                text = "Will collapse";
                color = red;
                share = 0f;
            }
            else if (value < min)
            {
                text = "Unsupported"; // too weak, but it cannot break (a chest with items in it, or no building fall)
                color = red;
            }
            else if (value >= max - 0.001f)
            {
                text = "Stability 100%";
                color = new Color(0.6f, 0.8f, 1f); // the game's colour for full support
            }
            else
            {
                // The game's support colour: red at the minimum, green from half the maximum.
                var c = Mathf.Clamp01((value - min) / (max * 0.5f - min));
                color = c < 0.5f ? Color.Lerp(red, new Color(1f, 0.85f, 0.2f), c * 2f)
                                 : Color.Lerp(new Color(1f, 0.85f, 0.2f), new Color(0.35f, 1f, 0.35f), (c - 0.5f) * 2f);
                text = $"Stability {Mathf.FloorToInt(share * 100f)}%";
            }
            if (ShowValue() && piece.m_noSupportWear && !shown.Breaks)
                text += $" <size=80%><color=#C8C8C8>({Mathf.Max(0f, value):0.#}/{max:0}, needs {min:0})</color></size>";
            _text.text = text;
            _text.color = color;
            _fillImage.color = color;
            _fill.sizeDelta = new Vector2(Mathf.Max(1f, 70f * share), 0f);
        }

        private void Show(bool on)
        {
            if (_root != null && _root.activeSelf != on) _root.SetActive(on);
        }

        // Below the crosshair, in the game's hover font.
        private bool Build()
        {
            var hud = Hud.instance;
            if (hud == null || hud.m_crosshair == null) return false;
            var cross = hud.m_crosshair.rectTransform;
            _root = new GameObject("DigiStabilityMeter", typeof(RectTransform));
            var rect = (RectTransform)_root.transform;
            rect.SetParent(cross.parent, false);
            rect.anchorMin = cross.anchorMin;
            rect.anchorMax = cross.anchorMax;
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = cross.anchoredPosition + new Vector2(0f, -20f);
            rect.sizeDelta = new Vector2(300f, 30f);

            var bar = new GameObject("Bar", typeof(RectTransform), typeof(Image));
            var barRect = (RectTransform)bar.transform;
            barRect.SetParent(rect, false);
            barRect.anchorMin = barRect.anchorMax = new Vector2(0.5f, 1f);
            barRect.pivot = new Vector2(0.5f, 1f);
            barRect.anchoredPosition = Vector2.zero;
            barRect.sizeDelta = new Vector2(72f, 7f);
            var back = bar.GetComponent<Image>();
            back.color = new Color(0f, 0f, 0f, 0.6f);
            back.raycastTarget = false;

            var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            _fill = (RectTransform)fill.transform;
            _fill.SetParent(barRect, false);
            _fill.anchorMin = new Vector2(0f, 0f);
            _fill.anchorMax = new Vector2(0f, 1f);
            _fill.pivot = new Vector2(0f, 0.5f);
            _fill.anchoredPosition = new Vector2(1f, 0f);
            _fill.offsetMin = new Vector2(1f, 1f);
            _fill.offsetMax = new Vector2(1f, -1f);
            _fillImage = fill.GetComponent<Image>();
            _fillImage.raycastTarget = false;

            var label = new GameObject("Text", typeof(RectTransform));
            var labelRect = (RectTransform)label.transform;
            labelRect.SetParent(rect, false);
            labelRect.anchorMin = labelRect.anchorMax = new Vector2(0.5f, 1f);
            labelRect.pivot = new Vector2(0.5f, 1f);
            labelRect.anchoredPosition = new Vector2(0f, -9f);
            labelRect.sizeDelta = new Vector2(300f, 22f);
            _text = label.AddComponent<TextMeshProUGUI>();
            if (hud.m_hoverName != null)
            {
                _text.font = hud.m_hoverName.font;
                _text.fontSharedMaterial = hud.m_hoverName.fontSharedMaterial;
            }
            _text.fontSize = 16f;
            _text.alignment = TextAlignmentOptions.Top;
            _text.textWrappingMode = TextWrappingModes.NoWrap;
            _text.overflowMode = TextOverflowModes.Overflow;
            _text.raycastTarget = false;
            _text.richText = true;
            return true;
        }
    }
}
