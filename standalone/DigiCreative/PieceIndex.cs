// Where the loaded building pieces are, in half-metre cells: finding the piece at a spot (a mirror twin, a timber
// beam's iron) looks at the few pieces there instead of every loaded one. Over all of them it took ~8 ms per look in a
// creative base of 15,000 pieces, and a mirrored placement or removal looks once per mirror image (and per iron
// piece): 35-45 ms per click (live probe tests/probes/buildlag, 2026-11-21).
// Built the first time it is asked, then kept up to date as pieces load and go (Piece.Awake / OnDestroy); until then
// those two hooks only check a flag. A piece is filed where it was made: building pieces do not move (ships and carts
// do; they are never looked for this way). The placement ghost is never found (its layer).
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace DigiCreative
{
    internal static class PieceIndex
    {
        private const float CellSize = 0.5f;
        private const float Near = 0.05f; // the same spot: within 5 cm (as Mirror.SameSpot)
        private static readonly Dictionary<long, List<Piece>> Cells = new Dictionary<long, List<Piece>>();
        private static readonly Dictionary<Piece, long> Filed = new Dictionary<Piece, long>();
        private static bool _built;
        private static int _ghostLayer = -1;

        internal static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Piece), "Awake"), postfix: new HarmonyMethod(typeof(PieceIndex), nameof(Loaded)));
            harmony.Patch(AccessTools.Method(typeof(Piece), "OnDestroy"), prefix: new HarmonyMethod(typeof(PieceIndex), nameof(Gone)));
        }

        private static int CellOf(float v) => Mathf.FloorToInt(v / CellSize);

        private static long Key(int x, int y, int z) => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);

        private static void Add(Piece piece)
        {
            if (piece == null || Filed.ContainsKey(piece)) return;
            var p = piece.transform.position;
            var key = Key(CellOf(p.x), CellOf(p.y), CellOf(p.z));
            if (!Cells.TryGetValue(key, out var list)) Cells[key] = list = new List<Piece>();
            list.Add(piece);
            Filed[piece] = key;
        }

        private static void Remove(Piece piece)
        {
            if (piece == null || !Filed.TryGetValue(piece, out var key)) return;
            Filed.Remove(piece);
            if (!Cells.TryGetValue(key, out var list)) return;
            list.Remove(piece);
            if (list.Count == 0) Cells.Remove(key);
        }

        private static void Loaded(Piece __instance)
        {
            if (_built) Add(__instance);
        }

        private static void Gone(Piece __instance)
        {
            if (_built) Remove(__instance);
        }

        private static void Build()
        {
            _built = true;
            Cells.Clear();
            Filed.Clear();
            foreach (var piece in Piece.s_allPieces) Add(piece);
        }

        // The piece of this kind (prefab name) standing at this spot the same way round (Mirror.SameSpot: on the same
        // spot with its snap points on each other), other than except; null when there is none.
        internal static Piece At(string name, Vector3 at, Quaternion rot, Piece except)
        {
            if (!_built) Build();
            if (_ghostLayer < 0) _ghostLayer = LayerMask.NameToLayer("ghost");
            int x0 = CellOf(at.x - Near), x1 = CellOf(at.x + Near), y0 = CellOf(at.y - Near), y1 = CellOf(at.y + Near);
            int z0 = CellOf(at.z - Near), z1 = CellOf(at.z + Near);
            for (var x = x0; x <= x1; x++)
                for (var y = y0; y <= y1; y++)
                    for (var z = z0; z <= z1; z++)
                    {
                        if (!Cells.TryGetValue(Key(x, y, z), out var list)) continue;
                        for (var i = list.Count - 1; i >= 0; i--)
                        {
                            var piece = list[i];
                            if (piece == null)
                            {
                                list.RemoveAt(i); // gone without OnDestroy reaching us (should not happen)
                                continue;
                            }
                            if (piece == except || piece.gameObject.layer == _ghostLayer) continue;
                            var pos = piece.transform.position;
                            if ((pos - at).sqrMagnitude >= Near * Near || Utils.GetPrefabName(piece.gameObject) != name) continue;
                            if (Mirror.SameSpot(name, pos, piece.transform.rotation, at, rot)) return piece;
                        }
                    }
            return null;
        }
    }
}
