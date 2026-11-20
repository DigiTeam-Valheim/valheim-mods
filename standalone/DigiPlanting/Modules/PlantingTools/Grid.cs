// Planting Tools, the grid: where each plant of a grid goes and whether it can grow there, worked out for the whole grid
// at once. The cheap checks (height, ward, biome, cultivated ground, heat and cold) are plain lookups; the two physics
// checks a plant makes itself (a roof above it, room around it) run as Unity's batched physics queries, spread over the
// processor's cores. A grid is only checked again when it moves or changes, and twice a second for changes around it.
// The rules are the game's own (Plant.UpdateHealth), in its order, so a plant that shows green here grows.
using System;
using System.Collections.Generic;
using HarmonyLib;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace PlantingTools
{
    internal enum Spot : byte { Ok, NoSeeds, Taken, NotCultivated, WrongBiome, NoSpace, NoSun, TooHot, TooCold, Blocked, Invalid }

    // What is being planted, read from the prefab once per selection (so other mods' changes to it count).
    internal sealed class Kind
    {
        public GameObject Prefab;
        public Piece Piece;
        public Plant Plant;
        public string Name;
        public bool NeedCultivated, TolerateHeat, TolerateCold, Tree;
        public Heightmap.Biome Biome;
        public float GrowRadius, BodyRadius, Spacing;

        public static Kind Of(GameObject prefab)
        {
            if (prefab == null) return null;
            var piece = prefab.GetComponent<Piece>();
            var plant = prefab.GetComponent<Plant>();
            if (piece == null || piece.m_repairPiece || piece.m_removePiece) return null;
            if (plant == null && prefab.GetComponent<Pickable>() == null) return null;
            if (plant != null && plant.m_attachDistance > 0f) return null; // vines: one at a time, against a wall
            var kind = new Kind { Prefab = prefab, Piece = piece, Plant = plant, Name = prefab.name };
            if (plant != null)
            {
                kind.NeedCultivated = plant.m_needCultivatedGround;
                kind.Biome = plant.m_biome;
                kind.TolerateHeat = plant.m_tolerateHeat;
                kind.TolerateCold = plant.m_tolerateCold;
                kind.GrowRadius = plant.m_growRadius;
                foreach (var grown in plant.m_grownPrefabs)
                    if (grown != null && grown.GetComponent<TreeBase>() != null) kind.Tree = true;
                if (prefab.GetComponent<TreeBase>() != null) kind.Tree = true;
                foreach (var c in prefab.GetComponentsInChildren<Collider>(true))
                    if (((1 << c.gameObject.layer) & Grid.SpaceMask) != 0) kind.BodyRadius = Mathf.Max(kind.BodyRadius, Radius(c));
            }
            else
            {
                kind.NeedCultivated = piece.m_cultivatedGroundOnly;
                kind.Biome = piece.m_onlyInBiome;
            }
            kind.Spacing = Plugin.SpacingFor(kind);
            return kind;
        }

        private static float Radius(Collider c)
        {
            var s = c.transform.lossyScale;
            var flat = Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z));
            switch (c)
            {
                case CapsuleCollider cap: return cap.radius * flat;
                case SphereCollider sph: return sph.radius * flat;
                case BoxCollider box: return Mathf.Max(box.size.x * Mathf.Abs(s.x), box.size.z * Mathf.Abs(s.z)) * 0.5f;
                default: return 0f;
            }
        }

        // Every capsule's radius on the sapling and what it grows into (Plant Easily's "tighter spacing" adds this).
        public float CapsuleRadius()
        {
            var r = 0f;
            var all = new List<GameObject> { Prefab };
            if (Plant != null) all.AddRange(Plant.m_grownPrefabs);
            foreach (var go in all)
                if (go != null)
                    foreach (var cap in go.GetComponentsInChildren<CapsuleCollider>(true)) r = Mathf.Max(r, cap.radius);
            return r;
        }
    }

    internal static class Grid
    {
        public static readonly int SpaceMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "piece_nonsolid");
        private static readonly int RoofMask = LayerMask.GetMask("Default", "static_solid", "piece");
        private static readonly int PickableMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "piece_nonsolid", "item");
        private const int MaxHits = 8;

        public static Kind Kind;
        public static bool Active;              // a grid is shown (the ghost is out and the grid is on)
        public static Vector3 Root, RowStep, ColumnStep;
        public static Quaternion Rotation;
        public static int Rows, Columns, Count;
        public static Vector3[] Positions = new Vector3[0];
        public static Spot[] Spots = new Spot[0];
        public static readonly int[] Tally = new int[Enum.GetValues(typeof(Spot)).Length];
        public static int Version;              // goes up with every check (the preview redraws its list then)
        public static int Seeds = int.MaxValue; // extra plants the seeds you carry pay for (after the first one)
        public static float LastCheckMs;

        public static bool Fill, Snapped;       // a filled patch instead of rows x columns; lined up with plants already there
        public static string Note = "";         // why a fill is smaller than it looks (for the count line)

        private static readonly int AnchorMask = SpaceMask | LayerMask.GetMask("item");
        private static Vector3 _lastRoot, _lastRow;
        private static Quaternion _lastRotation;
        private static int _lastRows, _lastColumns, _lastMax, _lastQueued;
        private static bool _lastFill;
        private static float _lastSpacing, _nextCheck;
        private static Kind _lastKind, _familyFor;
        private static Heightmap _heightmap;
        private static bool _batchFailed;
        private static readonly List<int> Todo = new List<int>();
        private static readonly List<PrivateArea> Wards = new List<PrivateArea>();
        private static readonly List<Location> NoBuild = new List<Location>();
        private static readonly Collider[] Hits = new Collider[MaxHits];
        private static readonly Collider[] Around = new Collider[64];
        private static readonly HashSet<string> Family = new HashSet<string>();
        private static readonly Queue<long> Open = new Queue<long>();
        private static readonly HashSet<long> Seen = new HashSet<long>();

        public static void Clear()
        {
            Active = false;
            Kind = null;
            _lastKind = null;
            _familyFor = null;
        }

        // Each frame after the game has placed its own ghost (the grid's first plant).
        public static void Update(Player player, GameObject ghost)
        {
            Active = false;
            if (Kind == null || ghost == null || !ghost.activeInHierarchy || !Plugin.GridOn) return;
            Fill = Plugin.FillOn;
            Rows = Plugin.Rows;
            Columns = Plugin.Columns;
            if (!Fill && Mathf.Min(Rows * Columns, Plugin.MaxPlants) < 2) return;
            Active = true;
            var s = Kind.Spacing;
            var raw = ghost.transform.position;
            Rotation = ghost.transform.rotation;
            var forward = Plugin.WorldAligned ? Vector3.forward : Utils.DirectionXZ(raw - player.transform.position);
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            var row = Flat(Rotation * forward);
            var column = Vector3.Cross(Vector3.up, row);
            var root = raw;
            Snapped = Plugin.SnapOn && Snap(raw, s, ref row, ref column, out root);
            if (!Snapped && Fill) // a patch on a fixed lattice: it only changes when the cursor reaches another spot of it
                root = row * (Mathf.Round(Vector3.Dot(raw, row) / s) * s) + column * (Mathf.Round(Vector3.Dot(raw, column) / s) * s);
            root.y = raw.y;
            Root = root;
            RowStep = row * s;
            ColumnStep = column * s;

            var changed = Kind != _lastKind || Fill != _lastFill || Rows != _lastRows || Columns != _lastColumns || Plugin.MaxPlants != _lastMax ||
                          !Mathf.Approximately(s, _lastSpacing) || Flat2(Root - _lastRoot) > 0.0001f || Vector3.Angle(row, _lastRow) > 0.5f ||
                          Quaternion.Angle(Rotation, _lastRotation) > 0.5f || Planter.Queued != _lastQueued;
            if (changed || Time.time >= _nextCheck)
            {
                _lastKind = Kind;
                _lastFill = Fill;
                _lastRows = Rows;
                _lastColumns = Columns;
                _lastMax = Plugin.MaxPlants;
                _lastSpacing = s;
                _lastRoot = Root;
                _lastRow = row;
                _lastRotation = Rotation;
                _lastQueued = Planter.Queued;
                _nextCheck = Time.time + 0.5f;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                Layout();
                Check(player);
                LastCheckMs = (float)watch.Elapsed.TotalMilliseconds;
            }
            // the game's ghost (the grid's first plant) stands on the grid's first spot
            if (Count > 0 && (Snapped || Fill)) ghost.transform.position = Positions[0];
        }

        // Where the plants go: rows x columns from the first spot, or (Fill field) every lattice spot of the cultivated
        // patch under the cursor, nearest first (a flood fill that stops at ground that is not cultivated).
        private static void Layout()
        {
            Note = "";
            if (!Fill)
            {
                Count = Mathf.Min(Rows * Columns, Plugin.MaxPlants);
                Ensure(Count);
                for (var i = 0; i < Count; i++) Positions[i] = Root + RowStep * (i / Columns) + ColumnStep * (i % Columns);
                return;
            }
            Count = 0;
            Ensure(1);
            if (!Cultivated(Root))
            {
                Positions[0] = Root;
                Count = 1;
                Note = "not on cultivated ground";
                return;
            }
            var max = Plugin.MaxPlants;
            var reach2 = Plugin.FillReach * Plugin.FillReach;
            Seen.Clear();
            Open.Clear();
            Seen.Add(Key(0, 0));
            Open.Enqueue(Key(0, 0));
            while (Open.Count > 0 && Count < max)
            {
                var key = Open.Dequeue();
                var i = (int)(key >> 32);
                var j = (int)(uint)(key & 0xFFFFFFFFL);
                Ensure(Count + 1);
                Positions[Count++] = Root + RowStep * i + ColumnStep * j;
                for (var n = 0; n < 4; n++)
                {
                    var ni = i + (n == 0 ? 1 : n == 1 ? -1 : 0);
                    var nj = j + (n == 2 ? 1 : n == 3 ? -1 : 0);
                    var k = Key(ni, nj);
                    if (!Seen.Add(k)) continue;
                    var q = Root + RowStep * ni + ColumnStep * nj;
                    if (Flat2(q - Root) <= reach2 && Cultivated(q)) Open.Enqueue(k);
                }
            }
            if (Open.Count > 0 && Count >= max) Note = "more than the most plants per click";
        }

        // Next to plants already in the ground (or still queued) of the same kind, the grid continues their field: their
        // lines, and a first spot one step off the nearest of them (the free spot nearest the cursor), growing away from
        // them so the new grid does not run back into the old one.
        private static bool Snap(Vector3 raw, float s, ref Vector3 row, ref Vector3 column, out Vector3 root)
        {
            root = raw;
            if (_familyFor != Kind)
            {
                _familyFor = Kind;
                Family.Clear();
                Family.Add(Kind.Name);
                if (Kind.Plant != null)
                    foreach (var grown in Kind.Plant.m_grownPrefabs)
                        if (grown != null) Family.Add(grown.name);
            }
            if (!Nearest(raw, Mathf.Max(0.5f, s * 1.6f), raw, false, out var anchor)) return false;
            if (Nearest(anchor, s * 1.6f, anchor, true, out var next))
            {
                var e = Flat(next - anchor);
                if (Mathf.Abs(Vector3.Dot(e, row)) >= Mathf.Abs(Vector3.Dot(e, column)))
                {
                    row = Vector3.Dot(e, row) >= 0f ? e : -e;
                    var c = Vector3.Cross(Vector3.up, row);
                    column = Vector3.Dot(c, column) >= 0f ? c : -c;
                }
                else
                {
                    column = Vector3.Dot(e, column) >= 0f ? e : -e;
                    var r = Vector3.Cross(column, Vector3.up);
                    row = Vector3.Dot(r, row) >= 0f ? r : -r;
                }
            }
            var d = raw - anchor;
            var i = Mathf.RoundToInt(Vector3.Dot(d, row) / s);
            var j = Mathf.RoundToInt(Vector3.Dot(d, column) / s);
            if (i == 0 && j == 0)
            {
                if (Mathf.Abs(Vector3.Dot(d, row)) >= Mathf.Abs(Vector3.Dot(d, column))) i = Vector3.Dot(d, row) >= 0f ? 1 : -1;
                else j = Vector3.Dot(d, column) >= 0f ? 1 : -1;
            }
            root = anchor + row * (s * i) + column * (s * j);
            if (Mathf.Abs(i) >= Mathf.Abs(j)) { if (i < 0) row = -row; }
            else if (j < 0) column = -column;
            return true;
        }

        // The plant of the same kind (planted, grown, or queued) nearest "from", within "radius" of "center".
        private static bool Nearest(Vector3 center, float radius, Vector3 from, bool notAtFrom, out Vector3 found)
        {
            found = Vector3.zero;
            var best = float.MaxValue;
            var r2 = radius * radius;
            var n = Physics.OverlapSphereNonAlloc(center, radius, Around, AnchorMask);
            for (var k = 0; k < n; k++)
            {
                var view = Around[k].GetComponentInParent<ZNetView>();
                if (view == null || !view.IsValid() || !Family.Contains(Utils.GetPrefabName(view.gameObject))) continue;
                var p = view.transform.position;
                var d2 = Flat2(p - from);
                if ((notAtFrom && d2 < 0.0025f) || Flat2(p - center) > r2 || d2 >= best) continue;
                best = d2;
                found = p;
            }
            if (Planter.Left > 0 && Planter.Nearest(center, radius, Kind.Prefab, from, notAtFrom, out var q, out var qd2) && qd2 < best)
            {
                best = qd2;
                found = q;
            }
            return best < float.MaxValue;
        }

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-8f ? v.normalized : Vector3.forward;
        }

        private static float Flat2(Vector3 v) => v.x * v.x + v.z * v.z;

        private static long Key(int i, int j) => ((long)i << 32) | (uint)j;

        private static bool Cultivated(Vector3 p)
        {
            var hm = HeightmapAt(p);
            return hm != null && hm.IsCultivated(p);
        }

        private static void Ensure(int n)
        {
            if (Positions.Length >= n) return;
            var size = Mathf.Max(n, Positions.Length * 2, 64);
            Array.Resize(ref Positions, size);
            Array.Resize(ref Spots, size);
        }

        private static void Check(Player player)
        {
            var plant = Kind.Plant != null;
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            for (var i = 0; i < Count; i++)
            {
                var q = Positions[i];
                if (q.x < minX) minX = q.x;
                if (q.x > maxX) maxX = q.x;
                if (q.z < minZ) minZ = q.z;
                if (q.z > maxZ) maxZ = q.z;
            }
            var center = new Vector3((minX + maxX) * 0.5f, Root.y, (minZ + maxZ) * 0.5f);
            var reach = new Vector2(maxX - minX, maxZ - minZ).magnitude * 0.5f + Kind.Spacing;
            Wards.Clear();
            foreach (var area in PrivateArea.m_allAreas)
                if (area != null && area.IsEnabled() && !area.HaveLocalAccess() && area.IsInside(center, reach)) Wards.Add(area);
            NoBuild.Clear();
            foreach (var loc in Location.s_allLocations)
                if (loc != null && loc.m_noBuild && loc.IsInside(center, reach, true)) NoBuild.Add(loc);
            var shields = ShieldGenerator.m_instances.Count > 0;
            // a plant still waiting in the planting queue takes its own spot (closer than half the spacing); whether it
            // crowds its neighbours is the physics check's business once it stands (tight crops grow fine side by side)
            var taken = Kind.Spacing * 0.5f;

            Todo.Clear();
            for (var i = 0; i < Count; i++)
            {
                var p = Positions[i];
                var spot = Spot.Ok;
                var hm = HeightmapAt(p);
                if (hm == null || !hm.GetWorldHeight(p, out var h)) spot = Spot.Invalid;
                else
                {
                    p.y = h;
                    if (Wards.Count > 0 && !PrivateArea.CheckAccess(p, 0f, false, false)) spot = Spot.Blocked;
                    else if (NoBuild.Count > 0 && InsideNoBuild(p)) spot = Spot.Blocked;
                    else
                    {
                        var biome = hm.GetBiome(p);
                        if (Kind.Biome != Heightmap.Biome.None && (biome & Kind.Biome) == 0) spot = Spot.WrongBiome;
                        else if (Kind.NeedCultivated && !hm.IsCultivated(p)) spot = Spot.NotCultivated;
                        else if (plant && !Kind.TolerateHeat && biome == Heightmap.Biome.AshLands && !(shields && ShieldGenerator.IsInsideShield(p))) spot = Spot.TooHot;
                        else if (plant && !Kind.TolerateCold && (biome == Heightmap.Biome.DeepNorth || biome == Heightmap.Biome.Mountain) &&
                                 !(shields && ShieldGenerator.IsInsideShield(p))) spot = Spot.TooCold;
                        else if (Planter.Left > 0 && Planter.Near(p, taken)) spot = Spot.Taken;
                    }
                }
                Positions[i] = p;
                Spots[i] = spot;
                if (spot == Spot.Ok) Todo.Add(i);
            }

            if (!_batchFailed)
            {
                try { Batched(plant); }
                catch (Exception e)
                {
                    _batchFailed = true;
                    Plugin.Log?.LogWarning("Batched physics checks are not available here, checking one by one: " + e.Message);
                    OneByOne(plant);
                }
            }
            else OneByOne(plant);
            if (plant && GameRulesChanged()) AskTheGame(player);

            // seeds: what you carry pays for the plants in grid order, after the first one (the game's own placement)
            Seeds = Plugin.ExtraPlantsPaid(player, Kind);
            var paid = 0;
            for (var i = 1; i < Count; i++)
            {
                if (Spots[i] != Spot.Ok) continue;
                if (paid < Seeds) paid++;
                else Spots[i] = Spot.NoSeeds;
            }
            Array.Clear(Tally, 0, Tally.Length);
            for (var i = 0; i < Count; i++) Tally[(int)Spots[i]]++;
            Version++;
        }

        private static void Batched(bool plant)
        {
            var n = Todo.Count;
            if (n == 0) return;
            var spheres = new NativeArray<OverlapSphereCommand>(n, Allocator.TempJob);
            var sphereHits = new NativeArray<ColliderHit>(n * MaxHits, Allocator.TempJob);
            var rays = new NativeArray<RaycastCommand>(plant ? n : 0, Allocator.TempJob);
            var rayHits = new NativeArray<RaycastHit>(plant ? n : 0, Allocator.TempJob);
            try
            {
                var space = new QueryParameters(plant ? SpaceMask : PickableMask, false, QueryTriggerInteraction.UseGlobal, false);
                var roof = new QueryParameters(RoofMask, false, QueryTriggerInteraction.UseGlobal, false);
                for (var k = 0; k < n; k++)
                {
                    var p = Positions[Todo[k]];
                    spheres[k] = plant ? new OverlapSphereCommand(p, Kind.GrowRadius, space) : new OverlapSphereCommand(p + Vector3.up * 0.05f, 0.05f, space);
                    if (plant) rays[k] = new RaycastCommand(p, Vector3.up, roof, 100f);
                }
                var job = OverlapSphereCommand.ScheduleBatch(spheres, sphereHits, 32, MaxHits);
                if (plant) job = JobHandle.CombineDependencies(job, RaycastCommand.ScheduleBatch(rays, rayHits, 32, 1));
                job.Complete();
                for (var k = 0; k < n; k++)
                {
                    var i = Todo[k];
                    if (plant && rayHits[k].collider != null) { Spots[i] = Spot.NoSun; continue; }
                    for (var h = 0; h < MaxHits; h++)
                    {
                        var hit = sphereHits[k * MaxHits + h];
                        if (hit.instanceID == 0) break;
                        if (Blocks(hit.collider, plant)) { Spots[i] = Spot.NoSpace; break; }
                    }
                }
            }
            finally
            {
                spheres.Dispose();
                sphereHits.Dispose();
                rays.Dispose();
                rayHits.Dispose();
            }
        }

        private static void OneByOne(bool plant)
        {
            foreach (var i in Todo)
            {
                var p = Positions[i];
                if (plant && Physics.Raycast(p, Vector3.up, 100f, RoofMask)) { Spots[i] = Spot.NoSun; continue; }
                var n = plant ? Physics.OverlapSphereNonAlloc(p, Kind.GrowRadius, Hits, SpaceMask)
                              : Physics.OverlapSphereNonAlloc(p + Vector3.up * 0.05f, 0.05f, Hits, PickableMask);
                for (var h = 0; h < n; h++)
                    if (Blocks(Hits[h], plant)) { Spots[i] = Spot.NoSpace; break; }
            }
        }

        // Another mod may change the game's own roof or room check (Plant Everything's "CropsRequireGrowthSpace = false"
        // lets crops grow side by side): then a spot the fast check turned down is asked again through the game's own
        // (changed) check, with the game's ghost plant moved onto that spot as the probe.
        private static bool? _rulesChanged;
        private static bool _probeFailed;

        private static bool GameRulesChanged()
        {
            if (_rulesChanged.HasValue) return _rulesChanged.Value;
            bool Patched(string name)
            {
                var info = Harmony.GetPatchInfo(AccessTools.Method(typeof(Plant), name));
                return info != null && info.Prefixes.Count + info.Postfixes.Count + info.Transpilers.Count + info.Finalizers.Count > 0;
            }
            _rulesChanged = Patched("HaveGrowSpace") || Patched("HaveRoof");
            if (_rulesChanged.Value) Plugin.Log?.LogInfo("Another mod changes the game's plant room or roof check: spots are checked through it.");
            return _rulesChanged.Value;
        }

        private static void AskTheGame(Player player)
        {
            if (_probeFailed || player.m_placementGhost == null) return;
            var probe = player.m_placementGhost.GetComponent<Plant>();
            if (probe == null) return;
            var t = probe.transform;
            var keep = t.position;
            try
            {
                for (var i = 0; i < Count; i++)
                {
                    if (Spots[i] != Spot.NoSpace && Spots[i] != Spot.NoSun) continue;
                    t.position = Positions[i];
                    Spots[i] = probe.HaveRoof() ? Spot.NoSun : probe.HaveGrowSpace() ? Spot.Ok : Spot.NoSpace;
                }
            }
            catch (Exception e)
            {
                _probeFailed = true;
                Plugin.Log?.LogWarning("Could not ask the game's own plant check: " + e.Message);
            }
            finally { t.position = keep; }
        }

        // The game's rule (Plant.HaveGrowSpace): anything in the way blocks, except a plant that is not healthy itself.
        private static bool Blocks(Collider c, bool plant)
        {
            if (c == null) return false;
            if (!plant) return true;
            var other = c.GetComponent<Plant>();
            return other == null || other.m_status == Plant.Status.Healthy;
        }

        private static Heightmap HeightmapAt(Vector3 p)
        {
            if (_heightmap != null && _heightmap.IsPointInside(p)) return _heightmap;
            _heightmap = Heightmap.FindHeightmap(p);
            return _heightmap;
        }

        private static bool InsideNoBuild(Vector3 p)
        {
            foreach (var loc in NoBuild)
                if (loc != null && loc.IsInside(p, 0f, true)) return true;
            return false;
        }

        public static bool Bad(Spot spot) => spot != Spot.Ok && spot != Spot.NoSeeds && spot != Spot.Taken;
    }
}
