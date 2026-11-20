// What a terrain tool does to the ground. A heightmap (one per 64 m zone) has a vertex every meter; its TerrainComp keeps,
// per vertex, how far the ground was moved from how the world made it (level delta + smooth delta) and whether it was
// touched. Each click reaches every heightmap in range and is worked out by whoever owns that heightmap.
using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace LevelStrength
{
    internal enum OpKind : byte
    {
        Piece = 0, // a game (or other mod's) hoe or cultivator piece, maybe resized or square
        Smooth = 1,
        Slope = 2,
        Raise = 3, // Raise and Lower (Delta < 0)
        Reset = 4,
    }

    // One click: the same on every heightmap it reaches.
    internal sealed class OpData
    {
        public OpKind Kind;
        public int Id;           // tells clicks apart (Smooth works out its result once per click)
        public bool Square;
        public float Radius;     // the size chosen (custom tools; game pieces: their largest radius)
        public float BaseRadius; // game pieces: their own largest radius (the others are scaled with it)
        public float A, B, C;    // Slope: the plane, height = C + A * dx + B * dz (dx, dz from the click)
        public float Delta;      // Raise / Lower: meters
        public float Strength;   // Smooth: 0..1 per click

        // How far from the click the change reaches (a square's corners lie further out than its sides).
        public float Reach => Square ? Radius * 1.4143f : Radius;
    }

    internal static class Terrain
    {
        internal static readonly AccessTools.FieldRef<TerrainComp, Heightmap> Hmap = AccessTools.FieldRefAccess<TerrainComp, Heightmap>("m_hmap");
        internal static readonly AccessTools.FieldRef<TerrainComp, int> Width = AccessTools.FieldRefAccess<TerrainComp, int>("m_width");
        internal static readonly AccessTools.FieldRef<TerrainComp, float[]> LevelDelta = AccessTools.FieldRefAccess<TerrainComp, float[]>("m_levelDelta");
        internal static readonly AccessTools.FieldRef<TerrainComp, float[]> SmoothDelta = AccessTools.FieldRefAccess<TerrainComp, float[]>("m_smoothDelta");
        internal static readonly AccessTools.FieldRef<TerrainComp, bool[]> Modified = AccessTools.FieldRefAccess<TerrainComp, bool[]>("m_modifiedHeight");
        internal static readonly AccessTools.FieldRef<TerrainComp, bool[]> ModifiedPaint = AccessTools.FieldRefAccess<TerrainComp, bool[]>("m_modifiedPaint");
        internal static readonly AccessTools.FieldRef<TerrainComp, int> Operations = AccessTools.FieldRefAccess<TerrainComp, int>("m_operations");
        internal static readonly AccessTools.FieldRef<TerrainComp, Vector3> LastOpPoint = AccessTools.FieldRefAccess<TerrainComp, Vector3>("m_lastOpPoint");
        internal static readonly AccessTools.FieldRef<TerrainComp, float> LastOpRadius = AccessTools.FieldRefAccess<TerrainComp, float>("m_lastOpRadius");
        internal static readonly AccessTools.FieldRef<TerrainComp, ZNetView> View = AccessTools.FieldRefAccess<TerrainComp, ZNetView>("m_nview");
    }

    // Ground heights by world position, for the vertices a click reaches in other heightmaps too.
    internal sealed class Sampler
    {
        private Heightmap _last;

        internal float Height(Vector3 point)
        {
            if (_last == null || !Inside(_last, point)) _last = Heightmap.FindHeightmap(point);
            return _last != null && _last.GetWorldHeight(point, out var height) ? height : float.NaN;
        }

        private static bool Inside(Heightmap map, Vector3 point)
        {
            var half = map.m_width * map.m_scale * 0.5f;
            var centre = map.transform.position;
            return Mathf.Abs(point.x - centre.x) <= half && Mathf.Abs(point.z - centre.z) <= half;
        }
    }

    internal static class Ops
    {
        // Square mode for the game's own smoothing and painting while one of our clicks is worked out (see Limits).
        [ThreadStatic] internal static bool Square;

        private static int _smoothId;
        private static Dictionary<long, float> _smoothTargets;

        internal static void Reset()
        {
            Square = false;
            _smoothTargets = null;
        }

        internal static float Limit => Mathf.Max(Plugin.VanillaLimit, Plugin.MaxHeightChange?.Value ?? Plugin.VanillaLimit);

        // Distance in vertices: round, or square (lined up with the grid).
        internal static float Distance(int dx, int dy, bool square) =>
            square ? Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)) : Mathf.Sqrt(dx * dx + dy * dy);

        private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

        // --- Level ground (the old Level Strength) ---------------------------------------------------------------

        // Vanilla LevelTerrain + SmoothTerrain in one pass, with the ring weight raised by strength and the cap configurable.
        internal static void Level(TerrainComp comp, Vector3 worldPos, float levelRadius, float smoothRadius, float power,
            float strength, float maxChange, bool square)
        {
            var hmap = Terrain.Hmap(comp);
            var levelDelta = Terrain.LevelDelta(comp);
            var smoothDelta = Terrain.SmoothDelta(comp);
            var modified = Terrain.Modified(comp);
            if (hmap == null || levelDelta == null || smoothDelta == null || modified == null) return;

            hmap.WorldToVertex(worldPos, out var cx, out var cy);
            var target = worldPos.y - comp.transform.position.y;
            var levelR = levelRadius / hmap.m_scale;
            var outerR = Mathf.Max(levelR, smoothRadius / hmap.m_scale);
            var reach = Mathf.CeilToInt(outerR);
            var size = Terrain.Width(comp) + 1;

            for (var y = cy - reach; y <= cy + reach; y++)
            {
                for (var x = cx - reach; x <= cx + reach; x++)
                {
                    if (x < 0 || y < 0 || x >= size || y >= size) continue;
                    var dist = Distance(x - cx, y - cy, square);
                    if (dist > outerR) continue;

                    float weight;
                    if (levelR > 0f && dist <= levelR)
                    {
                        weight = 1f;
                    }
                    else
                    {
                        var ratio = dist / outerR;
                        var falloff = 1f - Mathf.Pow(ratio, power);
                        weight = Mathf.Lerp(falloff, 1f, strength);
                    }

                    var index = y * size + x;
                    var height = hmap.GetHeight(x, y);
                    if ((levelR > 0f && dist <= levelR) || strength > 0f)
                    {
                        // Fold the old smooth delta into the level delta (as vanilla does inside the level radius),
                        // so the ring is not limited by the 1 m smooth cap.
                        levelDelta[index] += smoothDelta[index] + (target - height) * weight;
                        smoothDelta[index] = 0f;
                        levelDelta[index] = Mathf.Clamp(levelDelta[index], -maxChange, maxChange);
                    }
                    else
                    {
                        // Strength 0: the ring behaves exactly like vanilla SmoothTerrain.
                        smoothDelta[index] = Mathf.Clamp(smoothDelta[index] + (Mathf.Lerp(height, target, weight) - height), -1f, 1f);
                    }
                    modified[index] = true;
                }
            }
        }

        // --- The new tools -------------------------------------------------------------------------------------------

        internal static void Custom(TerrainComp comp, Vector3 pos, OpData op)
        {
            var hmap = Terrain.Hmap(comp);
            var levelDelta = Terrain.LevelDelta(comp);
            var smoothDelta = Terrain.SmoothDelta(comp);
            var modified = Terrain.Modified(comp);
            if (hmap == null || levelDelta == null || smoothDelta == null || modified == null) return;

            var scale = hmap.m_scale;
            var width = Terrain.Width(comp);
            var size = width + 1;
            var radius = op.Radius / scale;
            var reach = Mathf.CeilToInt(radius);
            var limit = Limit;
            hmap.WorldToVertex(pos, out var cx, out var cy);
            var origin = hmap.transform.position; // vertex heights are kept relative to it (world = origin.y + height)
            var smooth = op.Kind == OpKind.Smooth ? SmoothTargets(op, pos, scale) : null;

            for (var y = cy - reach; y <= cy + reach; y++)
            {
                for (var x = cx - reach; x <= cx + reach; x++)
                {
                    if (x < 0 || y < 0 || x >= size || y >= size) continue;
                    var dist = Distance(x - cx, y - cy, op.Square);
                    if (dist > radius) continue;
                    var index = y * size + x;
                    var height = hmap.GetHeight(x, y);
                    var wx = origin.x + (x - width / 2) * scale;
                    var wz = origin.z + (y - width / 2) * scale;
                    float target; // relative to the heightmap, like height
                    switch (op.Kind)
                    {
                        case OpKind.Reset:
                            levelDelta[index] = 0f;
                            smoothDelta[index] = 0f;
                            modified[index] = false;
                            continue;
                        case OpKind.Raise:
                            target = height + op.Delta;
                            break;
                        case OpKind.Slope:
                            var plane = op.C + op.A * (wx - pos.x) + op.B * (wz - pos.z) - origin.y;
                            target = Mathf.Lerp(height, plane, Edge(dist / radius));
                            break;
                        case OpKind.Smooth:
                            if (!smooth.TryGetValue(Key(Mathf.RoundToInt(wx / scale), Mathf.RoundToInt(wz / scale)), out var even)) continue;
                            var ratio = dist / radius;
                            target = Mathf.Lerp(height, even - origin.y, op.Strength * (1f - ratio * ratio));
                            break;
                        default:
                            continue;
                    }
                    levelDelta[index] += smoothDelta[index] + (target - height);
                    smoothDelta[index] = 0f;
                    levelDelta[index] = Mathf.Clamp(levelDelta[index], -limit, limit);
                    modified[index] = true;
                }
            }
            if (op.Kind == OpKind.Reset) ResetPaint(comp, hmap, pos, radius, op.Square);
        }

        // Full inside three quarters of the radius, then easing out to nothing at the edge, so the slope joins the
        // ground around it.
        private static float Edge(float ratio)
        {
            const float inner = 0.75f;
            if (ratio <= inner) return 1f;
            var t = Mathf.Clamp01((ratio - inner) / (1f - inner));
            return 1f - t * t * (3f - 2f * t);
        }

        // Paths, paving and cultivated soil back to how the world made them.
        private static void ResetPaint(TerrainComp comp, Heightmap hmap, Vector3 pos, float radius, bool square)
        {
            var painted = Terrain.ModifiedPaint(comp);
            if (painted == null) return;
            var size = Terrain.Width(comp) + 1;
            hmap.WorldToVertexMask(pos, out var cx, out var cy);
            var reach = Mathf.CeilToInt(radius);
            for (var y = Math.Max(0, cy - reach); y <= Math.Min(size - 1, cy + reach); y++)
            for (var x = Math.Max(0, cx - reach); x <= Math.Min(size - 1, cx + reach); x++)
            {
                var index = y * size + x;
                if (index < painted.Length && Distance(x - cx, y - cy, square) <= radius) painted[index] = false;
            }
        }

        // Smooth: every vertex in reach moves towards the average of the ground around it (a few meters, more for a
        // bigger tool). Worked out once per click from the ground as it was before the click, so where two heightmaps
        // meet (each has its own copy of the shared vertices) both get exactly the same height.
        private static Dictionary<long, float> SmoothTargets(OpData op, Vector3 pos, float scale)
        {
            if (_smoothTargets != null && _smoothId == op.Id) return _smoothTargets;
            var radius = op.Radius / scale;
            var reach = Mathf.CeilToInt(radius);
            var kernel = Mathf.Clamp(Mathf.RoundToInt(radius * 0.5f), 1, 8); // (0.3 / 6 barely moved a bump at the default size)
            var n = reach + kernel;
            var side = 2 * n + 1;
            var ox = Mathf.FloorToInt(pos.x / scale + 0.5f);
            var oz = Mathf.FloorToInt(pos.z / scale + 0.5f);
            var heights = new float[side * side];
            var sampler = new Sampler();
            for (var iz = 0; iz < side; iz++)
            for (var ix = 0; ix < side; ix++)
                heights[iz * side + ix] = sampler.Height(new Vector3((ox + ix - n) * scale, 0f, (oz + iz - n) * scale));

            var targets = new Dictionary<long, float>();
            for (var dz = -reach; dz <= reach; dz++)
            for (var dx = -reach; dx <= reach; dx++)
            {
                if (Distance(dx, dz, op.Square) > radius) continue;
                float sum = 0f, weights = 0f;
                for (var kz = -kernel; kz <= kernel; kz++)
                for (var kx = -kernel; kx <= kernel; kx++)
                {
                    var d = Mathf.Sqrt(kx * kx + kz * kz);
                    if (d > kernel) continue;
                    var h = heights[(dz + kz + n) * side + (dx + kx + n)];
                    if (float.IsNaN(h)) continue;
                    var w = kernel + 1f - d;
                    sum += h * w;
                    weights += w;
                }
                if (weights > 0f) targets[Key(ox + dx, oz + dz)] = sum / weights;
            }
            _smoothId = op.Id;
            _smoothTargets = targets;
            return targets;
        }

        // Slope: the plane that fits the ground under the tool best (least squares), worked out where the player clicks.
        internal static void FitPlane(OpData op, Vector3 pos)
        {
            var sampler = new Sampler();
            const float scale = 1f;
            var radius = op.Radius / scale;
            var reach = Mathf.CeilToInt(radius);
            var ox = Mathf.FloorToInt(pos.x / scale + 0.5f) * scale;
            var oz = Mathf.FloorToInt(pos.z / scale + 0.5f) * scale;
            double n = 0, sx = 0, sz = 0, sxx = 0, szz = 0, sxz = 0, sh = 0, sxh = 0, szh = 0;
            for (var dz = -reach; dz <= reach; dz++)
            for (var dx = -reach; dx <= reach; dx++)
            {
                if (Distance(dx, dz, op.Square) > radius) continue;
                var wx = ox + dx * scale;
                var wz = oz + dz * scale;
                var h = sampler.Height(new Vector3(wx, 0f, wz));
                if (float.IsNaN(h)) continue;
                double x = wx - pos.x, z = wz - pos.z;
                n++; sx += x; sz += z; sxx += x * x; szz += z * z; sxz += x * z; sh += h; sxh += x * h; szh += z * h;
            }
            if (n < 3)
            {
                op.C = pos.y;
                return;
            }
            // Normal equations of h = c + a x + b z, solved by Cramer's rule.
            var det = n * (sxx * szz - sxz * sxz) - sx * (sx * szz - sxz * sz) + sz * (sx * sxz - sxx * sz);
            if (Math.Abs(det) < 1e-9)
            {
                op.C = (float)(sh / n);
                return;
            }
            var c = (sh * (sxx * szz - sxz * sxz) - sx * (sxh * szz - sxz * szh) + sz * (sxh * sxz - sxx * szh)) / det;
            var a = (n * (sxh * szz - szh * sxz) - sh * (sx * szz - sxz * sz) + sz * (sx * szh - sxh * sz)) / det;
            var b = (n * (sxx * szh - sxz * sxh) - sx * (sx * szh - sxh * sz) + sh * (sx * sxz - sxx * sz)) / det;
            op.A = (float)a;
            op.B = (float)b;
            op.C = (float)c;
        }
    }

    // TerrainComp.InternalDoOperation: Level strength for Level ground, square mode, and the new tools.
    [HarmonyPatch(typeof(TerrainComp), "InternalDoOperation")]
    internal static class Doing
    {
        // Run after other mods' changes to the operation.
        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(TerrainComp __instance, Vector3 pos, TerrainOp.Settings modifier, out int __state)
        {
            __state = 0;
            if (modifier == null) return true;
            var op = Sending.Current;
            if (op != null && op.Kind != OpKind.Piece)
            {
                Ops.Custom(__instance, pos, op);
                Terrain.Operations(__instance)++;
                Terrain.LastOpPoint(__instance) = pos;
                Terrain.LastOpRadius(__instance) = modifier.GetRadius();
                return false;
            }
            var square = op != null && op.Square;
            Ops.Square = square;

            // The hoe's Level ground is a smooth-only operation (m_level false, m_smooth true); some tools use m_level.
            if (!modifier.m_level && !modifier.m_smooth) return true;
            if (modifier.m_square && !square) return true; // the game's own square tools stay as they are
            var cultivator = modifier.m_paintCleared && modifier.m_paintType == TerrainModifier.PaintType.Cultivate;
            var strength = cultivator && !Plugin.IncludeCultivator.Value ? 0f : Mathf.Clamp01(Plugin.Strength.Value / 100f);
            var maxChange = Ops.Limit;
            if (strength <= 0f && maxChange <= Plugin.VanillaLimit && !square) return true;

            var levelRadius = modifier.m_level ? modifier.m_levelRadius : 0f;
            var smoothRadius = modifier.m_smooth ? modifier.m_smoothRadius : 0f;
            if (float.IsNaN(smoothRadius) || float.IsInfinity(smoothRadius)) return true; // other mods' precise tools

            Ops.Level(__instance, pos + Vector3.up * modifier.m_levelOffset, levelRadius,
                smoothRadius, modifier.m_smoothPower, strength, maxChange, square);

            // The level and smooth parts are done; let the original run the remaining parts (raise, paint, bookkeeping).
            // Bit 1: level was on, bit 2: smooth was on; restored exactly afterwards.
            __state = (modifier.m_level ? 1 : 0) | (modifier.m_smooth ? 2 : 0);
            modifier.m_level = false;
            modifier.m_smooth = false;
            return true;
        }

        [HarmonyPriority(Priority.First)]
        private static void Postfix(TerrainComp __instance, TerrainOp.Settings modifier, int __state) => Restore(__instance, modifier, __state);

        private static Exception Finalizer(Exception __exception, TerrainComp __instance, TerrainOp.Settings modifier, int __state)
        {
            if (__exception != null) Restore(__instance, modifier, __state);
            Ops.Square = false;
            return __exception;
        }

        private static void Restore(TerrainComp comp, TerrainOp.Settings modifier, int state)
        {
            Ops.Square = false;
            if (state == 0 || modifier == null) return;
            if ((state & 1) != 0) modifier.m_level = true;
            if ((state & 2) != 0) modifier.m_smooth = true;
            Terrain.LastOpRadius(comp) = modifier.GetRadius();
        }
    }
}
