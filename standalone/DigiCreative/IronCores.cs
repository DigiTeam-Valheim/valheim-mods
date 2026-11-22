// Iron inside timber (a switch, off by default): a timber beam or pole placed in the creative world gets Wood Iron pieces
// inside it on the same snap points, so it holds like iron while it looks like timber. The 2 m pieces and the 26, 45
// and 67 degree beams get their Wood Iron twin (the decorated ones only with their own switch, off: the iron shows
// through their holes); the 4 m beam and pole two 2 m iron pieces end
// to end (there is no 4 m iron piece). Timber walls and roof crosses have no iron twin and get none.
// Where the iron goes: Wood Swap's table (DigiShared WoodShapes) lines its snap points up with the timber's; for a 4 m
// piece first two 2 m timber pieces along its two halves, then their iron.
// Mirrored copies and Wood Swap get theirs too (each placed timber piece places its own iron, which is not mirrored
// again); a pasted schematic already holds its iron (Paused). Removing the timber piece with the hammer takes its iron
// along.
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using DigiShared;
using HarmonyLib;
using UnityEngine;

namespace DigiCreative
{
    internal static class IronCores
    {
        internal static ConfigEntry<bool> On, Decorated;
        internal static Func<bool> InCreative = () => false;
        internal static ManualLogSource Log;
        internal static bool Paused; // a schematic being pasted: it brings its own iron

        private const int WoodIron = 3, Timber = 6, DecoratedTimber = 7; // WoodShapes.Types
        // The 4 m timber pieces have no iron twin: two of the 2 m timber piece's iron, end to end.
        private static readonly Dictionary<string, string> Halves = new Dictionary<string, string>
        {
            { "stave_beam_4m", "stave_beam_2m" },
            { "stave_pole_4m", "stave_pole_2m" },
        };
        private static readonly EffectList NoEffects = new EffectList { m_effectPrefabs = new EffectList.EffectData[0] };
        private static bool _busy;

        private sealed class Core
        {
            public Piece Piece;
            public Vector3 Pos;    // in the timber piece's own frame
            public Quaternion Rot; // relative to the timber piece
        }

        // Per timber piece: its iron, worked out once.
        private static readonly Dictionary<string, List<Core>> Cores = new Dictionary<string, List<Core>>();

        internal static void Bind(ConfigFile config)
        {
            On = config.Bind("3 - Player", "Iron inside timber", false,
                "A timber beam or pole you place gets Wood Iron pieces inside it on the same snap points, so it holds like iron " +
                "while it looks like timber: the 2 m pieces and the 26, 45 and 67 degree beams their " +
                "Wood Iron twin, the 4 m beam and pole two 2 m iron pieces end to end. Removing the timber piece takes its iron " +
                "along. Timber walls and roof crosses have no iron twin. Decorated timber has its own switch below.");
            Decorated = config.Bind("3 - Player", "Iron inside decorated timber", false,
                "With Iron inside timber on: decorated timber beams get their Wood Iron twin too. Off by default, the iron " +
                "shows through the holes of the decorated beams.");
            // The iron of each kind is worked out once (Cores): the decorated beams' again when this switch changes.
            Decorated.SettingChanged += (_, __) => Cores.Clear();
        }

        internal static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.PlacePiece)), postfix: new HarmonyMethod(typeof(IronCores), nameof(Placed)));
            harmony.Patch(AccessTools.Method(typeof(WearNTear), nameof(WearNTear.Remove)), prefix: new HarmonyMethod(typeof(IronCores), nameof(Removing)));
        }

        private static bool Wanted => On != null && On.Value && InCreative() && !_busy;

        private static void Placed(Player __instance, Piece piece, Vector3 pos, Quaternion rot)
        {
            if (!Wanted || Paused || piece == null || __instance != Player.m_localPlayer) return;
            var cores = CoresOf(Utils.GetPrefabName(piece.gameObject));
            if (cores.Count == 0) return;
            var was = Mirror.Mirroring;
            _busy = true;
            Mirror.Mirroring = true; // the iron is not mirrored: a mirrored timber piece places its own
            try
            {
                foreach (var core in cores)
                {
                    var effects = core.Piece.m_placeEffect;
                    core.Piece.m_placeEffect = NoEffects; // one placing sound, the timber piece's
                    try { __instance.PlacePiece(core.Piece, pos + rot * core.Pos, rot * core.Rot, false, false); }
                    finally { core.Piece.m_placeEffect = effects; }
                }
            }
            finally
            {
                Mirror.Mirroring = was;
                _busy = false;
            }
        }

        // The timber piece goes: its iron too (found on its spot, same kind, snap points on each other).
        private static void Removing(WearNTear __instance)
        {
            if (!Wanted || __instance == null) return;
            var piece = __instance.GetComponent<Piece>();
            if (piece == null) return;
            var cores = CoresOf(Utils.GetPrefabName(piece.gameObject));
            if (cores.Count == 0) return;
            var t = piece.transform;
            var was = Mirror.Mirroring;
            _busy = true;
            Mirror.Mirroring = true; // a mirrored timber piece takes its own iron along
            try
            {
                foreach (var core in cores)
                {
                    var found = Find(core.Piece.name, t.position + t.rotation * core.Pos, t.rotation * core.Rot, piece);
                    var view = found != null ? found.GetComponent<ZNetView>() : null;
                    var wear = found != null ? found.GetComponent<WearNTear>() : null;
                    if (wear != null && view != null && view.IsValid()) wear.Remove();
                }
            }
            finally
            {
                Mirror.Mirroring = was;
                _busy = false;
            }
        }

        private static Piece Find(string name, Vector3 at, Quaternion rot, Piece except) => PieceIndex.At(name, at, rot, except);

        private static List<Core> CoresOf(string name)
        {
            if (Cores.TryGetValue(name, out var list)) return list;
            list = new List<Core>();
            if (ZNetScene.instance == null) return list; // not cached: the pieces are not there yet
            try
            {
                if (Halves.TryGetValue(name, out var half))
                {
                    var halves = HalvesOf(name, half);
                    for (var i = 0; i < halves.Count; i += 2) AddIron(list, half, halves[i], Quaternion.Euler(halves[i + 1]));
                }
                else AddIron(list, name, Vector3.zero, Quaternion.identity);
            }
            catch (Exception e)
            {
                Log?.LogWarning($"Iron inside timber: no iron for {name}: {e.Message}");
            }
            Cores[name] = list;
            if (list.Count > 0) Log?.LogInfo($"Iron inside timber: {name} gets {list.Count} x {list[0].Piece.name}");
            return list;
        }

        // The Wood Iron twin of this timber piece, the timber piece lying at pos/rot in the frame.
        private static void AddIron(List<Core> list, string timber, Vector3 pos, Quaternion rot)
        {
            var member = WoodShapes.MemberOf(timber);
            if (member == null || member.Row[WoodIron].Length == 0) return;
            if (member.Type != Timber && (member.Type != DecoratedTimber || Decorated == null || !Decorated.Value)) return;
            var fit = WoodShapes.FitFor(timber, member, WoodIron);
            if (fit == null) return;
            list.Add(new Core { Piece = fit.Piece, Pos = pos + rot * fit.Move, Rot = rot * fit.Turn });
        }

        // Where two of the half piece lie along the whole one's halves, in its frame: position, then turn (as Euler angles),
        // for each half. The half piece is turned (in quarter turns) so that its ends lie along the whole one's.
        private static List<Vector3> HalvesOf(string whole, string half)
        {
            var result = new List<Vector3>();
            var a = Mirror.SnapsOf(Tools.PieceOf(whole));
            var b = Mirror.SnapsOf(Tools.PieceOf(half));
            if (a.Count < 2 || b.Count < 2) return result;
            Ends(a, out var p, out var q);
            Ends(b, out var p2, out var q2);
            var length = (q - p).magnitude;
            if (Mathf.Abs(length - 2f * (q2 - p2).magnitude) > 0.1f * length) return result; // not twice as long
            var axis = (q - p).normalized;
            var best = Quaternion.identity;
            var bestDot = -1f;
            for (var k = 0; k < 4; k++)
            {
                var turn = Quaternion.Euler(0f, 90f * k, 0f);
                var dot = Mathf.Abs(Vector3.Dot(axis, (turn * (q2 - p2)).normalized));
                if (dot <= bestDot + 0.001f) continue; // the first of equals: unturned when that fits
                bestDot = dot;
                best = turn;
            }
            if (bestDot < 0.99f) return result;
            var halfMiddle = best * ((p2 + q2) * 0.5f);
            var middle = (p + q) * 0.5f;
            result.Add((p + middle) * 0.5f - halfMiddle);
            result.Add(best.eulerAngles);
            result.Add((q + middle) * 0.5f - halfMiddle);
            result.Add(best.eulerAngles);
            return result;
        }

        // The two points furthest apart: the piece's ends.
        private static void Ends(List<Vector3> points, out Vector3 p, out Vector3 q)
        {
            p = points[0];
            q = points[1];
            var far = -1f;
            for (var i = 0; i < points.Count; i++)
                for (var j = i + 1; j < points.Count; j++)
                {
                    var d = (points[i] - points[j]).sqrMagnitude;
                    if (d <= far) continue;
                    far = d;
                    p = points[i];
                    q = points[j];
                }
        }
    }
}
