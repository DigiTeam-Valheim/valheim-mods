// Beam Fit: an Ashwood beam is thinner than a Grausten wall, so a beam set into one sits half inside the stone: it shows
// on one side only, in patches, or flickers where its face meets the wall's. This makes the visible beam exactly as
// thick as the wall it lies in (plus a little) and centres it there, so it shows evenly on both sides, like timber
// framing. Visual only: collision, snapping, support and the saved piece are unchanged. A beam that is not set into such
// a wall (on its face, free, or crossing it) is left as it is.
// The beam's three looks (new, worn, broken: the game switches them with its health) are all adjusted, so it stays fixed
// when damaged. Which beams and walls: by name part (settings), Ashwood beams and poles in Grausten walls by default.
// Lag: one physics box test per beam when it (or a wall next to it) loads, is built or is removed. What a piece is
// (beam, wall) is worked out once when it loads (Unity's .name makes a new string each call); a wall that came or went
// finds its beams in one pass over the loaded beams with the walls in 4 m cells. It searched every loaded piece once
// per wall: a pasted schematic or a big base loading (thousands of walls at once) froze the game 1-2 s (2026-11-21).
using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace BeamFit
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.beamfit";
        public const string Name = "Digi's Beam Fit";
        public const string Version = "1.1.2";

        private sealed class Original
        {
            public Vector3 Position, Scale;
        }

        private static ConfigEntry<string> _beams, _walls;
        private static ConfigEntry<float> _stickOut;
        private static string[] _beamNames = new string[0], _wallNames = new string[0];
        private static BepInEx.Logging.ManualLogSource _log;
        private static int _logged;

        private static readonly Dictionary<Piece, float> Pending = new Dictionary<Piece, float>(); // beam -> check after
        private static readonly List<Vector3> WallsChanged = new List<Vector3>();
        private static readonly Dictionary<Transform, Original> Changed = new Dictionary<Transform, Original>();
        private static readonly Dictionary<Piece, List<Transform>> Fitted = new Dictionary<Piece, List<Transform>>();
        private static readonly Dictionary<string, Bounds> WallShapes = new Dictionary<string, Bounds>();
        private static readonly Dictionary<string, float> Bodies = new Dictionary<string, float>(); // beam type/axis -> wood thickness
        private static readonly Collider[] Hits = new Collider[64];
        // What each loaded piece is (bit 1 beam, bit 2 wall), the loaded beams, the changed walls by 4 m cell, the beams
        // fitted this frame.
        private static readonly Dictionary<Piece, byte> Kinds = new Dictionary<Piece, byte>();
        private static readonly HashSet<Piece> Beams = new HashSet<Piece>();
        private static readonly Dictionary<long, List<Vector3>> SpotCells = new Dictionary<long, List<Vector3>>();
        private static readonly List<Piece> Due = new List<Piece>();
        private const float WallReach = 4f; // a wall within this of a beam's middle has the beam checked again
        private static int _ghostLayer = -1;
        private static int _mask;

        private void Awake()
        {
            _log = Logger;
            _beams = Config.Bind("General", "Beams", "ashwood_beam,ashwood_wall_beam,ashwood_pole",
                "Pieces that are fitted into walls: parts of their names, separated by commas.");
            _walls = Config.Bind("General", "Walls", "grausten",
                "Walls the beams are fitted into: parts of their names, separated by commas.");
            _stickOut = Config.Bind("General", "Stick out (cm)", 1f, new ConfigDescription(
                "How far a fitted beam stands out of each side of the wall.", new AcceptableValueRange<float>(0f, 10f)));
            _beams.SettingChanged += (_, __) => Rescan();
            _walls.SettingChanged += (_, __) => Rescan();
            _stickOut.SettingChanged += (_, __) => Rescan();
            _mask = LayerMask.GetMask("piece", "piece_nonsolid");
            ReadNames();
            PatchModule(typeof(Plugin));
            Rescan();
        }

        private void OnDestroy()
        {
            foreach (var beam in Fitted.Keys.ToList()) Restore(beam);
            Fitted.Clear();
            Changed.Clear();
            Pending.Clear();
            WallsChanged.Clear();
            Kinds.Clear();
            Beams.Clear();
        }

        private static void ReadNames()
        {
            string[] Split(string text) => (text ?? "").Split(',').Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0).ToArray();
            _beamNames = Split(_beams.Value);
            _wallNames = Split(_walls.Value);
            WallShapes.Clear();
            Kinds.Clear();
        }

        private static void Rescan()
        {
            ReadNames();
            foreach (var beam in Fitted.Keys.ToList()) Restore(beam);
            Beams.Clear();
            foreach (var piece in UnityEngine.Object.FindObjectsByType<Piece>(FindObjectsSortMode.None))
                if (IsBeam(piece))
                {
                    Beams.Add(piece);
                    Pending[piece] = 0f;
                }
        }

        private static bool Matches(Piece piece, string[] names)
        {
            if (piece == null || names.Length == 0) return false;
            var name = piece.gameObject.name.ToLowerInvariant();
            foreach (var part in names)
                if (name.Contains(part)) return true;
            return false;
        }

        private static byte KindOf(Piece piece)
        {
            if (piece == null) return 0;
            if (Kinds.TryGetValue(piece, out var kind)) return kind;
            kind = (byte)((Matches(piece, _beamNames) ? 1 : 0) | (Matches(piece, _wallNames) ? 2 : 0));
            Kinds[piece] = kind;
            return kind;
        }

        private static bool IsBeam(Piece piece) => (KindOf(piece) & 1) != 0;
        private static bool IsWall(Piece piece) => (KindOf(piece) & 2) != 0;

        // --- When to look -------------------------------------------------------------------------------------------

        // A piece loads or is built: a beam is checked a moment later (its walls may still be loading, and the physics
        // world sees new pieces only after its next step); a wall has the beams around it checked.
        [HarmonyPatch(typeof(Piece), "Awake")]
        [HarmonyPostfix]
        private static void Loaded(Piece __instance)
        {
            if (IsBeam(__instance))
            {
                Beams.Add(__instance);
                Pending[__instance] = Time.time + 0.3f;
            }
            else if (IsWall(__instance)) WallsChanged.Add(__instance.transform.position);
        }

        [HarmonyPatch(typeof(Piece), "OnDestroy")]
        [HarmonyPrefix]
        private static void Removed(Piece __instance)
        {
            Pending.Remove(__instance);
            Fitted.Remove(__instance);
            Beams.Remove(__instance);
            if (IsWall(__instance)) WallsChanged.Add(__instance.transform.position);
            Kinds.Remove(__instance);
        }

        private float _wallsAt;

        private void Update()
        {
            var now = Time.time;
            if (WallsChanged.Count > 0 && _wallsAt <= 0f) _wallsAt = now + 0.3f;
            if (_wallsAt > 0f && now >= _wallsAt)
            {
                _wallsAt = 0f;
                BeamsNearChangedWalls();
                WallsChanged.Clear();
            }
            if (Pending.Count == 0) return;
            // At most 40 a frame: a big base loading is spread over frames.
            Due.Clear();
            foreach (var pair in Pending)
            {
                if (pair.Value > now) continue;
                Due.Add(pair.Key);
                if (Due.Count >= 40) break;
            }
            foreach (var beam in Due)
            {
                Pending.Remove(beam);
                if (beam == null) continue;
                try
                {
                    Fit(beam);
                }
                catch (Exception e)
                {
                    _log?.LogWarning($"Could not fit {beam.name}: {e.Message}");
                }
            }
        }

        private static int Grid(float v) => Mathf.FloorToInt(v / WallReach);

        private static long Cell(int x, int y, int z) => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);

        // The beams within 4 m of a wall that came or went (as the game's GetAllPiecesInRadius around each wall, ghosts
        // left out): the walls' spots go into 4 m cells, then each loaded beam looks in its own and the 26 cells around.
        private static void BeamsNearChangedWalls()
        {
            SpotCells.Clear();
            foreach (var spot in WallsChanged)
            {
                var key = Cell(Grid(spot.x), Grid(spot.y), Grid(spot.z));
                if (!SpotCells.TryGetValue(key, out var list)) SpotCells[key] = list = new List<Vector3>();
                list.Add(spot);
            }
            if (_ghostLayer < 0) _ghostLayer = LayerMask.NameToLayer("ghost");
            Beams.RemoveWhere(b => b == null);
            foreach (var beam in Beams)
            {
                if (beam.gameObject.layer == _ghostLayer) continue;
                var at = beam.transform.position;
                if (WallNear(at)) Pending[beam] = 0f;
            }
        }

        private static bool WallNear(Vector3 at)
        {
            int gx = Grid(at.x), gy = Grid(at.y), gz = Grid(at.z);
            for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                    for (var dz = -1; dz <= 1; dz++)
                    {
                        if (!SpotCells.TryGetValue(Cell(gx + dx, gy + dy, gz + dz), out var spots)) continue;
                        foreach (var spot in spots)
                            if ((spot - at).sqrMagnitude < WallReach * WallReach) return true;
                    }
            return false;
        }

        // --- Fitting -------------------------------------------------------------------------------------------------

        private static void Fit(Piece beam)
        {
            Restore(beam);
            var looks = Looks(beam);
            if (looks.Count == 0)
            {
                // Its look is the piece itself: stretching it would stretch its collider and snap points too.
                if (_logged++ < 5) _log?.LogInfo($"{Utils.GetPrefabName(beam.gameObject)}: no separate look, left as it is.");
                return;
            }
            var root = beam.transform;
            if (!LocalBounds(root, looks[0], out var shape)) return;
            var centre = root.TransformPoint(shape.center);
            var half = Vector3.Scale(shape.extents, root.lossyScale);
            var count = Physics.OverlapBoxNonAlloc(centre, half, Hits, root.rotation, _mask);
            for (var i = 0; i < count; i++)
            {
                var wall = Hits[i] != null ? Hits[i].GetComponentInParent<Piece>() : null;
                if (wall == null || wall == beam || !IsWall(wall) || !WallShape(wall, out var wallShape)) continue;

                // The wall's thickness: its thinnest side (a wall is a slab: the other two sides are much longer).
                var sizes = wallShape.size;
                var thin = sizes.x <= sizes.y && sizes.x <= sizes.z ? 0 : sizes.y <= sizes.z ? 1 : 2;
                var others = Enumerable.Range(0, 3).Where(a => a != thin).Select(a => sizes[a]).ToArray();
                if (sizes[thin] <= 0.01f || others.Min() < sizes[thin] * 1.6f) continue;
                var normal = wall.transform.TransformDirection(Axis(thin)).normalized;
                // An upright slab only: floors and roofs of the same material (Grausten floors) are not walls.
                if (Mathf.Abs(normal.y) > 0.5f) continue;
                var thickness = sizes[thin] * Mathf.Abs(wall.transform.lossyScale[thin]);
                var wallCentre = wall.transform.TransformPoint(wallShape.center);

                // The beam must lie flat in the wall (one of its sides facing the same way) with its middle inside it.
                var axis = 0;
                var best = 0f;
                for (var a = 0; a < 3; a++)
                {
                    var dot = Mathf.Abs(Vector3.Dot(root.TransformDirection(Axis(a)).normalized, normal));
                    if (dot > best)
                    {
                        best = dot;
                        axis = a;
                    }
                }
                if (best < 0.97f) continue;
                // Through the wall is its length (a beam crossing the wall or standing against it): never touched.
                if (shape.size[axis] >= Mathf.Max(shape.size[(axis + 1) % 3], shape.size[(axis + 2) % 3])) continue;
                var offset = Vector3.Dot(centre - wallCentre, normal);
                // The wooden body, not the iron studs on its faces (those make the box 3 cm thicker than the wood).
                var beamThickness = BodyThickness(beam, looks[0], axis, shape.size[axis]) * Mathf.Abs(root.lossyScale[axis]);
                // Its middle in the wall, or at least cutting 2 cm into the stone (a beam that snapped half out of the wall);
                // one only lying against the wall's face is left as it is.
                if (thickness * 0.5f + beamThickness * 0.5f - Mathf.Abs(offset) < 0.02f) continue;
                var wanted = thickness + 2f * _stickOut.Value / 100f;
                if (beamThickness <= 0.01f) continue;
                // Already out of both sides of the wall: leave it (never made thinner).
                if (beamThickness * 0.5f >= thickness * 0.5f + Mathf.Abs(offset) + 0.002f) return;

                var factor = Mathf.Max(1f, wanted / beamThickness);
                var direction = root.TransformDirection(Axis(axis)).normalized;
                var shift = -offset * normal; // onto the wall's middle
                var changed = new List<Transform>();
                foreach (var look in looks)
                    if (Stretch(look.transform, centre, direction, factor, shift)) changed.Add(look.transform);
                if (changed.Count > 0) Fitted[beam] = changed;
                if (_logged++ < 5)
                    _log?.LogInfo($"Fitted {Utils.GetPrefabName(beam.gameObject)} into {Utils.GetPrefabName(wall.gameObject)}: " +
                                  $"wall {thickness:0.###} m, beam {beamThickness:0.###} -> {wanted:0.###} m (x{factor:0.##}), " +
                                  $"moved {-offset:0.###} m, {changed.Count}/{looks.Count} looks.");
                return;
            }
        }

        // Scale one look along a world direction around a world point (the beam's middle), then move it.
        private static bool Stretch(Transform look, Vector3 middle, Vector3 direction, float factor, Vector3 shift)
        {
            var inLook = Quaternion.Inverse(look.rotation) * direction;
            var along = Mathf.Abs(inLook.x) >= 0.99f ? 0 : Mathf.Abs(inLook.y) >= 0.99f ? 1 : Mathf.Abs(inLook.z) >= 0.99f ? 2 : -1;
            if (along < 0)
            {
                if (_logged++ < 5) _log?.LogInfo($"{look.name}: turned at an angle to the wall, left as it is.");
                return false;
            }
            if (!Changed.ContainsKey(look)) Changed[look] = new Original { Position = look.localPosition, Scale = look.localScale };
            var scale = look.localScale;
            scale[along] *= factor;
            look.localScale = scale;
            var depth = Vector3.Dot(look.position - middle, direction);
            look.position += direction * (depth * (factor - 1f)) + shift;
            return true;
        }

        private static void Restore(Piece beam)
        {
            if (beam == null || !Fitted.TryGetValue(beam, out var looks)) return;
            Fitted.Remove(beam);
            foreach (var look in looks)
            {
                if (look == null || !Changed.TryGetValue(look, out var original)) continue;
                look.localPosition = original.Position;
                look.localScale = original.Scale;
                Changed.Remove(look);
            }
        }

        // The beam's looks: new, worn and broken (the game shows one at a time).
        private static List<GameObject> Looks(Piece piece)
        {
            var looks = new List<GameObject>();
            var wear = piece.GetComponent<WearNTear>();
            if (wear != null)
                foreach (var look in new[] { wear.m_new, wear.m_worn, wear.m_broken })
                    if (look != null && look != piece.gameObject && !looks.Contains(look)) looks.Add(look);
            return looks;
        }

        private static bool WallShape(Piece wall, out Bounds shape)
        {
            var key = Utils.GetPrefabName(wall.gameObject);
            if (WallShapes.TryGetValue(key, out shape)) return shape.size != Vector3.zero;
            var looks = Looks(wall);
            var found = LocalBounds(wall.transform, looks.Count > 0 ? looks[0] : wall.gameObject, out shape);
            if (!found) shape = new Bounds();
            WallShapes[key] = shape;
            return found;
        }

        // The meshes' box in the piece's own axes.
        private static bool LocalBounds(Transform root, GameObject look, out Bounds bounds)
        {
            bounds = new Bounds();
            var any = false;
            foreach (var filter in look.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = filter.sharedMesh;
                if (mesh == null || filter.GetComponent<Renderer>() == null) continue;
                var b = mesh.bounds;
                for (var c = 0; c < 8; c++)
                {
                    var corner = b.center + Vector3.Scale(b.extents, new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1));
                    var point = root.InverseTransformPoint(filter.transform.TransformPoint(corner));
                    if (!any)
                    {
                        bounds = new Bounds(point, Vector3.zero);
                        any = true;
                    }
                    else bounds.Encapsulate(point);
                }
            }
            return any;
        }

        // The beam's own thickness along one of its axes (in its own units): between its two largest flat faces across that
        // axis, so iron studs and other small bits standing out of a face do not count. From the mesh when it can be read,
        // else from its box collider, else the meshes' box. Worked out once per beam type and axis.
        private static float BodyThickness(Piece beam, GameObject look, int axis, float boxThickness)
        {
            var key = Utils.GetPrefabName(beam.gameObject) + "/" + axis;
            if (Bodies.TryGetValue(key, out var known)) return known;
            // Only trusted close to the box: a pole's eight sides (or any odd shape) give two big faces far inside it.
            var how = "faces";
            var body = FaceThickness(beam.transform, look, axis);
            if (body < boxThickness * 0.85f)
            {
                how = "collider";
                body = ColliderThickness(beam, axis);
            }
            if (body < boxThickness * 0.85f || body > boxThickness + 0.001f)
            {
                how = "box";
                body = boxThickness;
            }
            Bodies[key] = body;
            _log?.LogInfo($"{Utils.GetPrefabName(beam.gameObject)}: body {body:0.###} m thick ({how}), box {boxThickness:0.###} m.");
            return body;
        }

        private static float FaceThickness(Transform root, GameObject look, int axis)
        {
            var areas = new Dictionary<int, float>(); // the face's place along the axis, in 2 mm steps -> its area
            foreach (var filter in look.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = filter.sharedMesh;
                if (mesh == null || filter.GetComponent<Renderer>() == null) continue;
                if (!mesh.isReadable) return -1f;
                var vertices = mesh.vertices;
                var triangles = mesh.triangles;
                var toRoot = root.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                for (var i = 0; i + 2 < triangles.Length; i += 3)
                {
                    var a = toRoot.MultiplyPoint3x4(vertices[triangles[i]]);
                    var b = toRoot.MultiplyPoint3x4(vertices[triangles[i + 1]]);
                    var c = toRoot.MultiplyPoint3x4(vertices[triangles[i + 2]]);
                    var cross = Vector3.Cross(b - a, c - a);
                    var twice = cross.magnitude;
                    if (twice <= 1e-7f || Mathf.Abs(cross[axis]) < 0.95f * twice) continue; // not facing along the axis
                    var place = Mathf.RoundToInt((a[axis] + b[axis] + c[axis]) / 3f * 500f);
                    areas[place] = (areas.TryGetValue(place, out var sum) ? sum : 0f) + twice * 0.5f;
                }
            }
            if (areas.Count < 2) return -1f;
            var middle = (areas.Keys.Min() + areas.Keys.Max()) * 0.5f;
            int low = 0, high = 0;
            float lowArea = 0f, highArea = 0f;
            foreach (var pair in areas)
            {
                if (pair.Key < middle && pair.Value > lowArea) { low = pair.Key; lowArea = pair.Value; }
                if (pair.Key > middle && pair.Value > highArea) { high = pair.Key; highArea = pair.Value; }
            }
            return lowArea > 0f && highArea > 0f ? (high - low) / 500f : -1f;
        }

        private static float ColliderThickness(Piece beam, int axis)
        {
            var root = beam.transform;
            var direction = root.TransformDirection(Axis(axis)).normalized;
            var best = -1f;
            foreach (var box in beam.GetComponentsInChildren<BoxCollider>(true))
            {
                for (var k = 0; k < 3; k++)
                {
                    if (Mathf.Abs(Vector3.Dot(box.transform.TransformDirection(Axis(k)).normalized, direction)) < 0.99f) continue;
                    var size = box.size[k] * Mathf.Abs(box.transform.lossyScale[k]) / Mathf.Max(0.0001f, Mathf.Abs(root.lossyScale[axis]));
                    best = Mathf.Max(best, size);
                }
            }
            return best;
        }

        private static Vector3 Axis(int a) => a == 0 ? Vector3.right : a == 1 ? Vector3.up : Vector3.forward;
    }
}
