// Changes inside the game's own terrain methods.
// - Height limit: the game keeps the ground within 8 m of how the world made it, in three places (levelling, raising,
//   and drawing the heightmap); those 8 m become "Max height change". Every player needs the same value, or the same
//   ground is drawn differently (the host's setting is synced to everyone).
// - Square mode: the game's smoothing and painting (paths, paving, cultivating) only know circles; while one of our
//   square clicks is worked out, their distance becomes the distance along the grid (a square).
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace LevelStrength
{
    internal static class Limits
    {
        private static readonly MethodInfo Up = AccessTools.Method(typeof(Limits), nameof(UpLimit));
        private static readonly MethodInfo Down = AccessTools.Method(typeof(Limits), nameof(DownLimit));
        private static readonly MethodInfo Distance = AccessTools.Method(typeof(Vector2), nameof(Vector2.Distance));
        private static readonly MethodInfo OurDistance = AccessTools.Method(typeof(Limits), nameof(ShapeDistance));

        private static float UpLimit() => Ops.Limit;
        private static float DownLimit() => -Ops.Limit;

        private static float ShapeDistance(Vector2 a, Vector2 b) =>
            Ops.Square ? Mathf.Max(Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y)) : Vector2.Distance(a, b);

        internal static void PatchHeights(Harmony harmony)
        {
            var transpiler = new HarmonyMethod(typeof(Limits), nameof(HeightTranspiler));
            foreach (var name in new[] { "LevelTerrain", "RaiseTerrain", "ApplyToHeightmap" })
            {
                var method = AccessTools.Method(typeof(TerrainComp), name);
                if (method == null) Plugin.Log?.LogWarning($"TerrainComp.{name} not found: the height limit stays at 8 m there.");
                else harmony.Patch(method, transpiler: transpiler);
            }
        }

        internal static void PatchSquares(Harmony harmony)
        {
            var transpiler = new HarmonyMethod(typeof(Limits), nameof(SquareTranspiler));
            foreach (var name in new[] { "SmoothTerrain", "PaintCleared" })
            {
                var method = AccessTools.Method(typeof(TerrainComp), name, new[] { typeof(Vector3), typeof(float), typeof(bool), typeof(float) });
                if (name == "PaintCleared") method = AccessTools.Method(typeof(TerrainComp), name, new[] { typeof(Vector3), typeof(Vector3), typeof(TerrainOp.Settings) });
                if (method == null) Plugin.Log?.LogWarning($"TerrainComp.{name} not found: it stays round in square mode.");
                else harmony.Patch(method, transpiler: transpiler);
            }
        }

        private static IEnumerable<CodeInstruction> HeightTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var changed = 0;
            foreach (var code in instructions)
            {
                if (code.opcode == OpCodes.Ldc_R4 && code.operand is float value && (value == 8f || value == -8f))
                {
                    var call = new CodeInstruction(OpCodes.Call, value > 0f ? Up : Down);
                    call.labels.AddRange(code.labels);
                    call.blocks.AddRange(code.blocks);
                    changed++;
                    yield return call;
                    continue;
                }
                yield return code;
            }
            if (changed == 0) Plugin.Log?.LogWarning($"No 8 m limit found in TerrainComp.{__originalMethod?.Name} (game changed?).");
        }

        private static IEnumerable<CodeInstruction> SquareTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var changed = 0;
            foreach (var code in instructions)
            {
                if ((code.opcode == OpCodes.Call || code.opcode == OpCodes.Callvirt) && code.operand is MethodInfo m && m == Distance)
                {
                    code.operand = OurDistance;
                    changed++;
                }
                yield return code;
            }
            if (changed == 0) Plugin.Log?.LogWarning($"No distance found in TerrainComp.{__originalMethod?.Name}: it stays round in square mode.");
        }
    }
}
