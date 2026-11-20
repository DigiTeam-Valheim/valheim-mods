// Snow Under Roofs: no snow on the beams, floors and walls of your buildings where a roof is above them. Two kinds of snow:
// - The snow layer the game lays on top of a piece in the Deep North (a mesh of its own). The game decides "under a roof"
//   from one point of the piece, which is often outside or even above the roof (a beam the roof passes through, a floor
//   under the eaves), so such pieces got snow from end to end, also inside. Now every small part of the layer is checked
//   and what has something above it goes; a beam half outside keeps the snow on its outside part.
//   Steep roof pieces keep the game's own snow: their rows overlap and stick out over each other, so a test cannot tell
//   "the next row" from "a roof above" and they would come out striped.
// - The snow some materials paint on top surfaces in the mountains and the Deep North, roof or not (iron beams and poles,
//   stone fences, the stone portal, log walls, ...). A piece mostly covered shows its material without that snow: one
//   shared copy per material, so there is nothing extra to draw.
// "Something above" is looked for from above: a thin cast from 60 m up straight down to the snow; anything solid it meets
// on the way (not the piece itself) covers the snow. From above, a roof passing right through a beam or resting on it
// counts, and the pieces beside or below the snow do not. What the game lets rain through ("leaky": every wood, Ashwood
// and iron floor, beams, poles, many walls and furniture) counts when it is flat and wide (a floor used as a ceiling, a
// table top) or rests right on the snow (a wall stacked on a wall); a thin beam or pole high above does not, so it cuts
// no stripes out of the snow below it.
// Only the look changes: the game's snow amounts stay as they are, and switched off everything looks as before.
// Floors (Floors.cs): on a building piece over the Deep North's deep snow, no snow clouds, no wading slow, and footsteps
// that sound like the floor (the game measured the terrain's snow under the floor).
// Light: a piece is checked when its snow shows, and again when pieces load, are built or go nearby (settled for a
// second first); the work is spread over frames, at most about a millisecond per frame. Pieces cut the same way share one
// mesh, so the game still draws them together.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SnowUnderRoofs
{
    public class Plugin : DigiCore.DigiModule
    {
        private const float BudgetMs = 1f, SweepSeconds = 30f, SweepRange = 90f, Cell = 8f, Settle = 1f;
        private const float Height = 60f, CastRadius = 0.05f, FlatEnough = 0.7f, SteepGap = 1.5f, MinWide = 0.9f, RestOn = 0.5f;
        private const string Keyword = "_ADDSNOW_ON";
        private static readonly int AddSnow = Shader.PropertyToID("_AddSnow");

        private static ConfigEntry<bool> _cut, _paint;
        private static int _mask;
        private static readonly RaycastHit[] Hits = new RaycastHit[256];

        // One of a piece's snow meshes (the normal one, and the worn and broken ones some pieces have).
        private sealed class Layer
        {
            public MeshRenderer Renderer;
            public MeshFilter Filter;
            public Mesh Original, Cut;
            public string CutKey;
            public Bounds Bounds;
        }

        // Cut meshes are shared by every layer cut the same way, so the game still draws them together (instanced).
        private sealed class SharedCut
        {
            public Mesh Mesh;
            public int Users;
        }

        // A renderer with materials that paint snow.
        private sealed class Paint
        {
            public Renderer Renderer;
            public Material[] Before; // its materials before this sub-mod took the snow off
            public readonly List<Material> Toggled = new List<Material>(); // its own material copies, changed in place
            public bool Off;
        }

        private sealed class Tracked
        {
            public WearNTear Wear;
            public Layer[] Layers;
            public Paint[] Paints;
            public bool Queued, Shown, Force, Steep;
            public int LayerMask = -1, PaintMask = -1; // results of the sample points last time
            public float LayerCover, PaintCover;       // the share that was covered
        }

        private sealed class MeshData
        {
            public int[][] Triangles;   // per submesh
            public Vector3[][] Centers; // per submesh, per triangle (mesh space)
        }

        private static readonly Dictionary<WearNTear, Tracked> All = new Dictionary<WearNTear, Tracked>();
        private static readonly Queue<Tracked> Work = new Queue<Tracked>();
        private static readonly HashSet<long> Dirty = new HashSet<long>();
        private static float _dirtySince, _nextSweep;
        private static readonly Dictionary<string, SharedCut> Cuts = new Dictionary<string, SharedCut>();
        private static readonly Dictionary<Material, Material> NoSnow = new Dictionary<Material, Material>();
        private static readonly Dictionary<Material, bool> Paints = new Dictionary<Material, bool>();
        private static readonly Dictionary<Mesh, MeshData> Meshes = new Dictionary<Mesh, MeshData>();
        private static readonly Dictionary<Vector3Int, bool> Seen = new Dictionary<Vector3Int, bool>(); // within one check: 20 cm cells
        private static readonly List<Vector3> Points = new List<Vector3>();
        private static readonly List<Vector3> World = new List<Vector3>();
        private static readonly List<int> Order = new List<int>();
        private static readonly Stopwatch Clock = new Stopwatch();
        private static float _gap;

        private void Awake()
        {
            _cut = Config.Bind("General", "Cut snow at the roof edge", true,
                "A beam, floor or wall only partly under a roof (a beam through a wall, a floor under the eaves) keeps its snow " +
                "layer on the outside part only. Off: it keeps all of it, or loses all of it when it is mostly covered.");
            _paint = Config.Bind("General", "Painted snow too", true,
                "Also takes off the snow that some materials paint on top surfaces in the mountains and the Deep North (iron " +
                "beams and poles, stone fences, the stone portal, log walls, ...), on pieces mostly under a roof.");
            _cut.SettingChanged += (_, __) => Recheck();
            _paint.SettingChanged += (_, __) => Recheck();
            _mask = LayerMask.GetMask("piece", "Default", "static_solid", "Default_small", "terrain"); // the game's roof test
            Floors.Bind(Config);
            PatchModule(typeof(Plugin));
            PatchModule(typeof(Floors));
            foreach (var wear in FindObjectsByType<WearNTear>(FindObjectsSortMode.None)) Track(wear); // switched on while playing
        }

        private void OnDestroy()
        {
            foreach (var t in All.Values) Restore(t);
            foreach (var copy in NoSnow.Values) if (copy != null) Destroy(copy);
            foreach (var cut in Cuts.Values) if (cut.Mesh != null) Destroy(cut.Mesh);
            Cuts.Clear();
            All.Clear();
            Work.Clear();
            Dirty.Clear();
            NoSnow.Clear();
            WideSeen.Clear();
        }

        private static void Recheck()
        {
            foreach (var t in All.Values) Enqueue(t, true);
        }

        // --- which pieces --------------------------------------------------------------------------------------------

        private static bool SnowyBiome(Vector3 p)
        {
            if (WorldGenerator.instance == null) return false;
            var biome = WorldGenerator.instance.GetBiome(p.x, p.z);
            return biome == Heightmap.Biome.DeepNorth || biome == Heightmap.Biome.Mountain;
        }

        private static bool PaintsSnow(Material m)
        {
            if (m == null) return false;
            if (!Paints.TryGetValue(m, out var yes))
                Paints[m] = yes = Array.IndexOf(m.shaderKeywords, Keyword) >= 0;
            return yes;
        }

        private static bool Valid(WearNTear wear)
        {
            var nview = wear != null ? wear.GetComponent<ZNetView>() : null;
            return nview != null && nview.IsValid();
        }

        private static void Track(WearNTear wear)
        {
            if (wear == null || All.ContainsKey(wear) || !Valid(wear) || wear.GetComponentInParent<Rigidbody>() != null) return; // no ghosts, ships or carts
            var t = new Tracked { Wear = wear };
            AddLayers(t);
            if (SnowyBiome(wear.transform.position))
            {
                var list = new List<Paint>();
                foreach (var r in wear.GetComponentsInChildren<Renderer>(true))
                {
                    if (r is ParticleSystemRenderer || IsLayer(wear, r)) continue;
                    foreach (var m in r.sharedMaterials)
                        if (PaintsSnow(m)) { list.Add(new Paint { Renderer = r }); break; }
                }
                if (list.Count > 0) t.Paints = list.ToArray();
            }
            if (t.Layers == null && t.Paints == null) return;
            t.Steep = IsSteep(wear);
            All[wear] = t;
            Enqueue(t, true);
        }

        private static bool IsLayer(WearNTear wear, Renderer r) => r == wear.m_snow || r == wear.m_snowWorn || r == wear.m_snowBroken;

        private static void AddLayers(Tracked t)
        {
            var wear = t.Wear;
            if (t.Layers != null || wear.m_snow == null || wear.m_snowBuildup <= 0.25f) return;
            var list = new List<Layer>();
            foreach (var r in new[] { wear.m_snow, wear.m_snowWorn, wear.m_snowBroken })
            {
                var filter = r != null ? r.GetComponent<MeshFilter>() : null;
                if (filter == null || filter.sharedMesh == null) continue;
                list.Add(new Layer { Renderer = r, Filter = filter, Original = filter.sharedMesh });
            }
            if (list.Count > 0) t.Layers = list.ToArray();
        }

        private static void Enqueue(Tracked t, bool again)
        {
            if (again) t.Force = true;
            if (t.Queued) return;
            t.Queued = true;
            Work.Enqueue(t);
        }

        private static long CellOf(Vector3 p) => ((long)Mathf.FloorToInt(p.x / Cell) << 32) ^ (uint)Mathf.FloorToInt(p.z / Cell);

        private static void MarkDirty(Vector3 p)
        {
            Dirty.Add(CellOf(p));
            _dirtySince = Time.time;
        }

        // --- the game's events ---------------------------------------------------------------------------------------

        // A piece loaded or was built (also by others): track it, and check the pieces around it again (a roof may have come).
        [HarmonyPatch(typeof(WearNTear), "Awake")]
        [HarmonyPostfix]
        private static void PieceAwake(WearNTear __instance)
        {
            if (!Valid(__instance)) return;
            Track(__instance);
            MarkDirty(__instance.transform.position);
        }

        [HarmonyPatch(typeof(WearNTear), "OnDestroy")]
        [HarmonyPrefix]
        private static void PieceGone(WearNTear __instance)
        {
            if (!All.TryGetValue(__instance, out var t)) return;
            All.Remove(__instance);
            foreach (var layer in t.Layers ?? new Layer[0]) Release(layer);
            MarkDirty(__instance.transform.position); // a roof may have gone
        }

        // The game shows, hides or changes the snow layer (snow came, the piece got worn or broken).
        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.UpdateSnowVisual))]
        [HarmonyPostfix]
        private static void SnowChanged(WearNTear __instance)
        {
            if (!All.TryGetValue(__instance, out var t))
            {
                Track(__instance);
                return;
            }
            var shown = __instance.m_snowBuildup > 0.25f;
            if (shown == t.Shown && (t.Layers != null || !shown)) return;
            t.Shown = shown;
            AddLayers(t);
            Enqueue(t, true);
        }

        // --- the work, a little per frame ----------------------------------------------------------------------------

        private void Update()
        {
            var player = Player.m_localPlayer;
            if (player == null || All.Count == 0) return;
            if (Dirty.Count > 0 && Time.time - _dirtySince >= Settle)
            {
                foreach (var t in All.Values)
                {
                    if (t.Wear == null) continue;
                    var p = t.Wear.transform.position;
                    var cx = Mathf.FloorToInt(p.x / Cell);
                    var cz = Mathf.FloorToInt(p.z / Cell);
                    for (var dx = -1; dx <= 1 && !t.Force; dx++)
                        for (var dz = -1; dz <= 1; dz++)
                            if (Dirty.Contains(((long)(cx + dx) << 32) ^ (uint)(cz + dz))) { Enqueue(t, true); break; }
                }
                Dirty.Clear();
            }
            if (Time.time >= _nextSweep)
            {
                _nextSweep = Time.time + SweepSeconds;
                var me = player.transform.position;
                foreach (var t in All.Values)
                    if (t.Wear != null && (t.Wear.transform.position - me).sqrMagnitude < SweepRange * SweepRange) Enqueue(t, false);
            }
            Clock.Restart();
            while (Work.Count > 0 && Clock.Elapsed.TotalMilliseconds < BudgetMs)
            {
                var t = Work.Dequeue();
                t.Queued = false;
                if (t.Wear == null) continue;
                try { Check(t); }
                catch (Exception e) { Logger.LogWarning($"{Utils.GetPrefabName(t.Wear.gameObject)}: {e.Message}"); }
            }
        }

        private static bool Under(Transform t, Transform own)
        {
            for (; t != null; t = t.parent)
                if (t == own) return true;
            return false;
        }

        // Something solid above the point: a thin cast from high up straight down to it (the piece itself does not count;
        // what lets rain through only when it is flat and wide or rests on the snow). _gap > 0: only what is at least that
        // far above the point counts.
        private static bool Above(Transform own, Vector3 p)
        {
            var top = p + Vector3.up * Height;
            var n = Physics.SphereCastNonAlloc(top, CastRadius, Vector3.down, Hits, Height - 0.05f, _mask, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < n; i++)
            {
                var c = Hits[i].collider;
                if (c == null || Under(c.transform, own)) continue;
                if (_gap > 0f && Hits[i].distance > 0f && top.y - Hits[i].distance < p.y + _gap) continue;
                if (c.gameObject.CompareTag("leaky") && !Wide(c) && (_gap > 0f || c.bounds.min.y > p.y + RestOn)) continue;
                return true;
            }
            return false;
        }

        // A flat, wide collider (a floor, a table top): at least MinWide across both ways that lie level, thinner upright.
        private static readonly Dictionary<int, bool> WideSeen = new Dictionary<int, bool>();

        private static bool Wide(Collider c)
        {
            var id = c.GetInstanceID();
            if (WideSeen.TryGetValue(id, out var yes)) return yes;
            Vector3 size;
            if (c is BoxCollider box) size = box.size;
            else if (c is MeshCollider mesh && mesh.sharedMesh != null) size = mesh.sharedMesh.bounds.size;
            else
            {
                WideSeen[id] = false;
                return false;
            }
            var t = c.transform;
            var s = t.lossyScale;
            var len = new[] { Mathf.Abs(size.x * s.x), Mathf.Abs(size.y * s.y), Mathf.Abs(size.z * s.z) };
            var up = new[] { Mathf.Abs(t.right.y), Mathf.Abs(t.up.y), Mathf.Abs(t.forward.y) };
            var v = up[0] >= up[1] && up[0] >= up[2] ? 0 : up[1] >= up[2] ? 1 : 2; // the axis closest to upright
            yes = true;
            for (var i = 0; i < 3; i++)
                if (i != v && (len[i] < MinWide || len[i] <= len[v])) yes = false;
            WideSeen[id] = yes;
            return yes;
        }

        // Points 20 cm apart share one answer within a check (snow triangles lie close together).
        private static bool AboveNear(Transform own, Vector3 p)
        {
            var key = new Vector3Int(Mathf.RoundToInt(p.x * 5f), Mathf.RoundToInt(p.y * 5f), Mathf.RoundToInt(p.z * 5f));
            if (Seen.TryGetValue(key, out var yes)) return yes;
            Seen[key] = yes = Above(own, p);
            return yes;
        }

        private static void Check(Tracked t)
        {
            var wear = t.Wear;
            var own = wear.transform;
            var force = t.Force;
            t.Force = false;
            t.Shown = wear.m_snowBuildup > 0.25f;
            var covered = -1f; // share of the snow layer that is covered, -1 = not looked at
            if (t.Layers != null)
            {
                if (!t.Shown || t.Steep) // steep roofs keep the game's own snow
                {
                    foreach (var layer in t.Layers) Show(layer);
                    t.LayerMask = -1;
                }
                else
                {
                    var main = t.Layers[0];
                    _gap = 0f;
                    Samples(main);
                    var mask = Mask(own);
                    if (force || mask != t.LayerMask)
                    {
                        t.LayerMask = mask;
                        foreach (var layer in t.Layers)
                        {
                            var share = Apply(wear, layer);
                            if (layer == main) t.LayerCover = share;
                        }
                    }
                    covered = t.LayerCover;
                }
            }
            if (t.Paints != null)
            {
                var off = false;
                if (_paint.Value)
                {
                    if (covered < 0f)
                    {
                        _gap = t.Steep ? SteepGap : 0f; // a steep piece: not the roof's own rows right above it
                        PaintSamples(t);
                        var mask = Mask(own);
                        if (force || mask != t.PaintMask)
                        {
                            t.PaintMask = mask;
                            var count = 0;
                            for (var i = 0; i < Points.Count; i++) if ((mask & (1 << i)) != 0) count++;
                            t.PaintCover = Points.Count > 0 ? (float)count / Points.Count : 0f;
                        }
                        covered = t.PaintCover;
                        _gap = 0f;
                    }
                    off = covered >= 0.5f;
                }
                var changed = false;
                foreach (var paint in t.Paints) changed |= SetPaint(paint, off);
                if (changed) Remerge(wear);
            }
        }

        // Valheim Enhanced Edition draws plain pieces as merged meshes built from their materials at that moment; after a
        // material change the piece is taken out and merged again (its own "remove, adopt again shortly"), or the merged
        // mesh would keep showing the old material.
        private static bool _veeLooked;
        private static IDictionary _veePieces;
        private static System.Reflection.MethodInfo _veeRemove;

        private static void Remerge(WearNTear wear)
        {
            if (!_veeLooked)
            {
                _veeLooked = true;
                var merge = AccessTools.TypeByName("PerfMod.MergeRender");
                _veePieces = merge != null ? AccessTools.Field(merge, "s_pieces")?.GetValue(null) as IDictionary : null;
                _veeRemove = merge != null ? AccessTools.Method(merge, "Remove") : null;
                if (_veeRemove != null && _veeRemove.GetParameters().Length != 3) _veeRemove = null;
            }
            if (_veePieces == null || _veeRemove == null || !_veePieces.Contains(wear)) return;
            try { _veeRemove.Invoke(null, new[] { _veePieces[wear], "Snow Under Roofs material", (object)0.5f }); }
            catch (Exception) { _veeRemove = null; } // another version of it: leave it alone
        }

        // Which of the sample points have something above them.
        private static int Mask(Transform own)
        {
            var mask = 0;
            for (var i = 0; i < Points.Count && i < 31; i++)
                if (Above(own, Points[i])) mask |= 1 << i;
            return mask;
        }

        private static MeshData DataOf(Mesh mesh)
        {
            if (Meshes.TryGetValue(mesh, out var data)) return data;
            data = new MeshData { Triangles = new int[mesh.subMeshCount][], Centers = new Vector3[mesh.subMeshCount][] };
            var v = mesh.vertices;
            for (var s = 0; s < mesh.subMeshCount; s++)
            {
                var tris = mesh.GetTriangles(s);
                var centers = new Vector3[tris.Length / 3];
                for (var i = 0; i < centers.Length; i++)
                    centers[i] = (v[tris[i * 3]] + v[tris[i * 3 + 1]] + v[tris[i * 3 + 2]]) / 3f;
                data.Triangles[s] = tris;
                data.Centers[s] = centers;
            }
            Meshes[mesh] = data;
            return data;
        }

        private static void GridOver(Bounds b)
        {
            for (var i = 0; i < 3; i++)
                for (var j = 0; j < 3; j++)
                    Points.Add(new Vector3(Mathf.Lerp(b.min.x, b.max.x, 0.15f + 0.35f * i), b.max.y, Mathf.Lerp(b.min.z, b.max.z, 0.15f + 0.35f * j)));
        }

        // Up to 9 points spread over the snow layer: 7 along its length, the 2 outermost across.
        private static void Samples(Layer layer)
        {
            Points.Clear();
            if (!layer.Original.isReadable)
            {
                GridOver(layer.Renderer.bounds);
                return;
            }
            var data = DataOf(layer.Original);
            var m = layer.Renderer.transform.localToWorldMatrix;
            World.Clear();
            foreach (var centers in data.Centers)
                foreach (var c in centers) World.Add(m.MultiplyPoint3x4(c));
            if (World.Count == 0) return;
            var min = World[0];
            var max = World[0];
            foreach (var p in World) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            var alongX = max.x - min.x >= max.z - min.z;
            Order.Clear();
            for (var i = 0; i < World.Count; i++) Order.Add(i);
            Order.Sort((a, b) => (alongX ? World[a].x : World[a].z).CompareTo(alongX ? World[b].x : World[b].z));
            for (var k = 0; k < 7; k++) Points.Add(World[Order[(int)Math.Round(k / 6.0 * (Order.Count - 1))]]);
            Order.Sort((a, b) => (alongX ? World[a].z : World[a].x).CompareTo(alongX ? World[b].z : World[b].x));
            Points.Add(World[Order[0]]);
            Points.Add(World[Order[Order.Count - 1]]);
        }

        // 9 points over the top of the piece's visible parts.
        private static void PaintSamples(Tracked t)
        {
            Points.Clear();
            var have = false;
            var b = new Bounds();
            foreach (var paint in t.Paints)
            {
                var r = paint.Renderer;
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (!have) { b = r.bounds; have = true; }
                else b.Encapsulate(r.bounds);
            }
            if (have) GridOver(b);
        }

        // --- steep or flat -------------------------------------------------------------------------------------------

        // A steep roof piece: its snow mesh lies in a plane more than about 45 degrees off level (the normal of the plane
        // that fits its triangle centres best: stepped shingle snow has ledges and front faces, but as a whole it lies in
        // the roof's plane). Pieces without a readable snow mesh count as flat.
        private static bool IsSteep(WearNTear wear)
        {
            var r = wear.m_snow;
            var filter = r != null ? r.GetComponent<MeshFilter>() : null;
            var mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null || !mesh.isReadable) return false;
            var data = DataOf(mesh);
            var m = r.transform.localToWorldMatrix;
            World.Clear();
            foreach (var cs in data.Centers)
                foreach (var c in cs) World.Add(m.MultiplyPoint3x4(c));
            return Mathf.Abs(PlaneNormal(World).y) < FlatEnough;
        }

        private static Vector3 PlaneNormal(List<Vector3> points)
        {
            if (points.Count < 3) return Vector3.up;
            var mean = Vector3.zero;
            foreach (var p in points) mean += p;
            mean /= points.Count;
            double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            foreach (var p in points)
            {
                var d = p - mean;
                xx += d.x * d.x; xy += d.x * d.y; xz += d.x * d.z; yy += d.y * d.y; yz += d.y * d.z; zz += d.z * d.z;
            }
            // the two directions the layer spreads most in (power iteration); the plane's normal is across both
            var a1 = Spread(xx, xy, xz, yy, yz, zz, new Vector3(1f, 0.1f, 0.3f), Vector3.zero);
            var a2 = Spread(xx, xy, xz, yy, yz, zz, new Vector3(0.3f, 0.1f, 1f), a1);
            var n = Vector3.Cross(a1, a2);
            return n.sqrMagnitude < 1e-6f ? Vector3.up : n.normalized;
        }

        private static Vector3 Spread(double xx, double xy, double xz, double yy, double yz, double zz, Vector3 start, Vector3 not)
        {
            var v = start.normalized;
            for (var i = 0; i < 30; i++)
            {
                if (not != Vector3.zero) v -= Vector3.Dot(v, not) * not;
                var w = new Vector3((float)(xx * v.x + xy * v.y + xz * v.z), (float)(xy * v.x + yy * v.y + yz * v.z), (float)(xz * v.x + yz * v.y + zz * v.z));
                if (w.sqrMagnitude < 1e-12f) break;
                v = w.normalized;
            }
            if (not != Vector3.zero) { v -= Vector3.Dot(v, not) * not; v.Normalize(); }
            return v;
        }

        // --- the snow layer ------------------------------------------------------------------------------------------

        // The layer stops using its shared cut mesh; the last one to stop destroys it.
        private static void Release(Layer layer)
        {
            if (layer.CutKey != null && Cuts.TryGetValue(layer.CutKey, out var shared) && --shared.Users <= 0)
            {
                if (shared.Mesh != null) Destroy(shared.Mesh);
                Cuts.Remove(layer.CutKey);
            }
            layer.Cut = null;
            layer.CutKey = null;
        }

        private static void UseOriginal(Layer layer)
        {
            if (layer.Cut == null) return;
            if (layer.Filter != null) layer.Filter.sharedMesh = layer.Original;
            if (layer.Renderer != null) layer.Renderer.bounds = layer.Bounds; // the game widened them for the snow's height; keep that
            Release(layer);
        }

        private static void Show(Layer layer)
        {
            UseOriginal(layer);
            if (layer.Renderer != null) layer.Renderer.enabled = true;
        }

        private static void Hide(Layer layer)
        {
            UseOriginal(layer);
            if (layer.Renderer != null) layer.Renderer.enabled = false;
        }

        // Every triangle of the layer is checked; only those with nothing above them stay. Returns the covered share.
        private static float Apply(WearNTear wear, Layer layer)
        {
            var own = wear.transform;
            Seen.Clear();
            if (!layer.Original.isReadable) // cannot be cut: all or nothing
            {
                Points.Clear();
                GridOver(layer.Renderer.bounds);
                var count = 0;
                foreach (var p in Points) if (AboveNear(own, p)) count++;
                var share = (float)count / Points.Count;
                if (share >= 0.5f) Hide(layer); else Show(layer);
                return share;
            }
            var data = DataOf(layer.Original);
            var m = layer.Renderer.transform.localToWorldMatrix;
            var keep = new List<int>[data.Triangles.Length];
            var key = new System.Text.StringBuilder(layer.Original.GetInstanceID().ToString()).Append(':');
            int kept = 0, total = 0;
            ulong bits = 0;
            var bit = 0;
            for (var s = 0; s < data.Triangles.Length; s++)
            {
                keep[s] = new List<int>();
                var tris = data.Triangles[s];
                var centers = data.Centers[s];
                for (var i = 0; i < centers.Length; i++)
                {
                    total++;
                    if (!AboveNear(own, m.MultiplyPoint3x4(centers[i])))
                    {
                        keep[s].Add(tris[i * 3]);
                        keep[s].Add(tris[i * 3 + 1]);
                        keep[s].Add(tris[i * 3 + 2]);
                        kept++;
                        bits |= 1UL << bit;
                    }
                    if (++bit == 64) { key.Append(bits.ToString("x")).Append(','); bits = 0; bit = 0; }
                }
            }
            key.Append(bits.ToString("x"));
            var covered = total > 0 ? 1f - (float)kept / total : 0f;
            if (kept == total) Show(layer);
            else if (kept == 0) Hide(layer);
            else if (!_cut.Value) { if (covered >= 0.5f) Hide(layer); else Show(layer); }
            else
            {
                var cutKey = key.ToString();
                if (layer.CutKey != cutKey)
                {
                    if (layer.Cut == null) layer.Bounds = layer.Renderer.bounds;
                    else Release(layer);
                    if (!Cuts.TryGetValue(cutKey, out var shared))
                    {
                        var mesh = Instantiate(layer.Original);
                        mesh.name = layer.Original.name + " (no roof snow)";
                        for (var s = 0; s < keep.Length; s++) mesh.SetTriangles(keep[s], s, true);
                        Cuts[cutKey] = shared = new SharedCut { Mesh = mesh };
                    }
                    shared.Users++;
                    layer.Cut = shared.Mesh;
                    layer.CutKey = cutKey;
                    layer.Filter.sharedMesh = layer.Cut;
                    layer.Renderer.bounds = layer.Bounds;
                }
                layer.Renderer.enabled = true;
            }
            return covered;
        }

        // --- painted snow --------------------------------------------------------------------------------------------

        private static Material NoSnowCopy(Material m)
        {
            if (NoSnow.TryGetValue(m, out var copy) && copy != null) return copy;
            copy = new Material(m) { name = m.name + " (no snow)" };
            copy.shaderKeywords = Array.FindAll(copy.shaderKeywords, k => k != Keyword);
            if (copy.HasProperty(AddSnow)) copy.SetFloat(AddSnow, 0f);
            NoSnow[m] = copy;
            Paints[copy] = false;
            return copy;
        }

        private static void Toggle(Material m, bool snow)
        {
            var words = new List<string>(m.shaderKeywords);
            words.Remove(Keyword);
            if (snow) words.Add(Keyword);
            m.shaderKeywords = words.ToArray();
            if (m.HasProperty(AddSnow)) m.SetFloat(AddSnow, snow ? 1f : 0f);
        }

        private static bool SetPaint(Paint paint, bool off)
        {
            var r = paint.Renderer;
            if (r == null || paint.Off == off) return false;
            if (off)
            {
                paint.Before = r.sharedMaterials;
                var mats = r.sharedMaterials;
                for (var i = 0; i < mats.Length; i++)
                {
                    if (!PaintsSnow(mats[i])) continue;
                    if (mats[i].name.EndsWith("(Instance)")) // this renderer's own copy (the game changes it, e.g. the portal's glow): change it in place
                    {
                        Toggle(mats[i], false);
                        paint.Toggled.Add(mats[i]);
                    }
                    else mats[i] = NoSnowCopy(mats[i]);
                }
                r.sharedMaterials = mats;
            }
            else
            {
                foreach (var m in paint.Toggled) if (m != null) Toggle(m, true);
                paint.Toggled.Clear();
                if (paint.Before != null) r.sharedMaterials = paint.Before;
                paint.Before = null;
            }
            paint.Off = off;
            return true;
        }

        private static void Restore(Tracked t)
        {
            if (t.Layers != null)
                foreach (var layer in t.Layers)
                    if (layer.Renderer != null) Show(layer);
            if (t.Paints == null) return;
            var changed = false;
            foreach (var paint in t.Paints) changed |= SetPaint(paint, false);
            if (changed && t.Wear != null) Remerge(t.Wear);
        }
    }
}
