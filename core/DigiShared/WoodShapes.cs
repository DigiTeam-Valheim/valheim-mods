// Shared by Building Tweaks' Wood Swap and Ultimate Builder's Wood Swap (each mod compiles this file in): the same shape
// in each wood, and where a piece of another wood goes so that everything around it still fits.
// The new piece goes where the old one's snap points were, not simply on its spot: the wood types' models have their
// middle in different places (an Ashwood beam's snap points sit 1 m below its middle, a Wood beam's on it) and some are
// built facing the other way (the Darkwood 67 degree beam). So the new piece is moved and turned (in steps of 90 degrees)
// until its snap points lie on the old one's; worked out once per pair. Of a left and a right version (Scalewood's
// triangle walls) the one that fits without turning is taken.
// The table was checked against the game's own pieces (1.0.21): every pair lines up.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DigiShared
{
    internal static class WoodShapes
    {
        // The wood types (the tools, in this order) and the piece whose icon each tool shows.
        internal static readonly string[] Types = { "Wood", "Log", "Darkwood", "Wood Iron", "Ashwood", "Scalewood", "Timber", "Decorated Timber" };
        internal static readonly string[] IconPieces =
            { "woodwall", "wood_wall_log", "darkwood_roof", "woodiron_beam", "ashwood_wall_2x2", "scale_wall_2x2", "stave_wall_2x2", "stave_deco_wall_2x2" };

        // The icon sheet's id of a wood's tool (make_creative_icons.py).
        internal static string ToolId(int type) => "woodswap_" + Types[type].Replace(" ", "").ToLowerInvariant();

        // One shape a line, its piece in each wood type (in the order of Types), separated by |. Several pieces in one wood:
        // all count as that shape; swapping to that wood takes the first, or the left/right version that fits.
        private static readonly string[] Shapes =
        {
            "wood_beam_1 | | | | ashwood_beam_1m | | |",
            "wood_beam | wood_wall_log | darkwood_beam | woodiron_beam | ashwood_beam_2m | | stave_beam_2m | stave_deco_beam_2m",
            "| wood_wall_log_4x0.5 | darkwood_beam4x4 | | | | stave_beam_4m |",
            "wood_beam_26 | wood_log_26 | darkwood_beam_26 | woodiron_beam_26 | ashwood_wall_beam_26 | | stave_beam_26 | stave_deco_beam_26",
            "wood_beam_45 | wood_log_45 | darkwood_beam_45 | woodiron_beam_45 | ashwood_wall_beam_45 | | stave_beam_45 | stave_deco_beam_45",
            "wood_beam_67 | wood_log_67 | darkwood_beam_67 | woodiron_beam_67 | ashwood_wall_beam_67 | | stave_beam_67 | stave_deco_beam_67",
            "wood_pole | | | | ashwood_pole_1m | | |",
            "wood_pole2 | wood_pole_log | darkwood_pole | woodiron_pole | ashwood_pole_2m | | stave_pole_2m | stave_deco_pole_2m",
            "| wood_pole_log_4 | darkwood_pole4 | | | | stave_pole_4m |",
            "woodwall | | | | ashwood_wall_2x2 ashwood_decowall_2x2 | scale_wall_2x2 | stave_wall_2x2 | stave_deco_wall_2x2",
            "wood_wall_half | | | | ashwood_halfwall_1x2 | scale_halfwall_1x2 | |",
            "wood_wall_quarter | | | | ashwood_quarterwall_1x1 | scale_quarterwall_1x1 | |",
            "wood_wall_roof_a | | | | ashwood_wall_roof_26 | scale_wall_roof_26 scale_wall_roof_26_flipped | |",
            "wood_wall_roof_45 | | | | ashwood_wall_roof_45 | scale_wall_roof_45 scale_wall_roof_45_flipped | |",
            "wood_wall_roof_67_a | | | | ashwood_wall_roof_67_a | scale_wall_roof_67 scale_wall_roof_67_flipped | |",
            "wood_wall_roof_upsidedown | | | | ashwood_wall_roof_26_upsidedown | scale_wall_roof_26_upsidedown scale_wall_roof_26_upsidedown_flipped | |",
            "wood_wall_roof_45_upsidedown | | | | ashwood_wall_roof_45_upsidedown | scale_wall_roof_45_upsidedown scale_wall_roof_45_upsidedown_flipped | |",
            "wood_wall_roof_67_upsidedown | | | | ashwood_wall_roof_67_upsidedown | scale_wall_roof_67_upsidedown scale_wall_roof_67_upsidedown_flipped | |",
            "wood_wall_roof_top | | | | ashwood_wall_cross_26 | | stave_wall_cross_26 |",
            "wood_wall_roof_top_45 | | | | ashwood_wall_cross_45 | | stave_wall_cross_45 |",
            "wood_wall_roof_top_67 | | | | ashwood_wall_cross_67 | | stave_wall_cross_67 |",
            "wood_roof | | darkwood_roof | | | | |",
            "wood_roof_45 | | darkwood_roof_45 | | | | |",
            "wood_roof_67 | | darkwood_roof_67 | | | | |",
            "wood_roof_icorner | | darkwood_roof_icorner | | | | |",
            "wood_roof_icorner_45 | | darkwood_roof_icorner_45 | | | | |",
            "wood_roof_icorner_67 | | darkwood_roof_icorner_67 | | | | |",
            "wood_roof_ocorner | | darkwood_roof_ocorner | | | | |",
            "wood_roof_ocorner_45 | | darkwood_roof_ocorner_45 | | | | |",
            "wood_roof_ocorner_67 | | darkwood_roof_ocorner_67 | | | | |",
            "wood_roof_top | | darkwood_roof_top | | | | |",
            "wood_roof_top_45 | | darkwood_roof_top_45 | | | | |",
            "wood_roof_top_67 | | darkwood_roof_top_67 | | | | |",
            "wood_floor | | | | ashwood_floor_2x2 ashwood_deco_floor | | |",
            "wood_floor_1x1 | | | | ashwood_floor_1x1 | | |",
            "wood_door | | | | ashwood_door | | |",
            "| | darkwood_arch | | ashwood_arch_big | | |",
            "| | darkwood_decowall | | ashwood_decowall_divider | | |",
        };

        // A piece in the table: its line (per wood type the pieces of that shape) and its own wood type.
        internal sealed class Member
        {
            public string[][] Row;
            public int Type;
        }

        // Where a new piece goes, relative to the old one: turned by Turn, moved by Move (in the old piece's own frame).
        internal sealed class Fit
        {
            public Piece Piece;
            public Quaternion Turn;
            public Vector3 Move;
        }

        private static Dictionary<string, Member> _members;
        private static readonly Dictionary<string, Fit> Fits = new Dictionary<string, Fit>();
        private static readonly List<Transform> SnapList = new List<Transform>();

        internal static Member MemberOf(string prefab)
        {
            if (_members == null)
            {
                _members = new Dictionary<string, Member>();
                foreach (var line in Shapes)
                {
                    var slots = line.Split('|');
                    var row = new string[Types.Length][];
                    for (var i = 0; i < row.Length; i++)
                        row[i] = i < slots.Length ? slots[i].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries) : new string[0];
                    for (var i = 0; i < row.Length; i++)
                        foreach (var piece in row[i])
                            _members[piece] = new Member { Row = row, Type = i };
                }
            }
            return prefab != null && _members.TryGetValue(prefab, out var member) ? member : null;
        }

        internal static Piece PieceOf(string name)
        {
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(name) : null;
            return prefab != null ? prefab.GetComponent<Piece>() : null;
        }

        // Where a prefab's snap points are, in its own frame.
        private static List<Vector3> SnapsOf(Piece piece)
        {
            var result = new List<Vector3>();
            if (piece == null) return result;
            SnapList.Clear();
            piece.GetSnapPoints(SnapList);
            foreach (var snap in SnapList) result.Add(piece.transform.InverseTransformPoint(snap.position));
            return result;
        }

        // The middle of the box around the points.
        private static Vector3 Middle(List<Vector3> points)
        {
            if (points.Count == 0) return Vector3.zero;
            Vector3 min = points[0], max = points[0];
            foreach (var p in points)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            return (min + max) * 0.5f;
        }

        // How far apart two sets of points are: each point to the nearest of the other set, both ways.
        private static float Distance(List<Vector3> a, List<Vector3> b)
        {
            var total = 0f;
            foreach (var p in a) total += Mathf.Sqrt(b.Min(q => (p - q).sqrMagnitude));
            foreach (var q in b) total += Mathf.Sqrt(a.Min(p => (p - q).sqrMagnitude));
            return total;
        }

        // The piece of that wood (of the shape's pieces there) and its turn and move, so that its snap points lie on the old
        // piece's: the boxes around both sets of snap points share their middle, and of the four turns the one where the
        // points lie closest is taken (no turn when that fits as well; then the first piece listed). Null if none.
        internal static Fit FitFor(string oldName, Member member, int type, Action<string> log = null)
        {
            var key = oldName + "|" + type;
            if (Fits.TryGetValue(key, out var fit)) return fit;
            var a = SnapsOf(PieceOf(oldName));
            var middle = Middle(a);
            var bestKey = int.MaxValue;
            for (var i = 0; i < member.Row[type].Length; i++)
            {
                var piece = PieceOf(member.Row[type][i]);
                if (piece == null) continue;
                var b0 = SnapsOf(piece);
                for (var k = 0; k < 4; k++)
                {
                    var turn = Quaternion.Euler(0f, 90f * k, 0f);
                    var b = b0.Select(p => turn * p).ToList();
                    var move = middle - Middle(b);
                    var error = a.Count > 0 && b.Count > 0 ? Distance(a, b.Select(p => p + move).ToList()) : 0f;
                    // closest first, then unturned, then the first listed
                    var order = Mathf.RoundToInt(error / 0.05f) * 100 + (k == 0 ? 0 : 50) + i;
                    if (order >= bestKey) continue;
                    bestKey = order;
                    fit = new Fit { Piece = piece, Turn = turn, Move = move };
                }
            }
            Fits[key] = fit;
            if (fit != null) log?.Invoke($"Wood Swap: {oldName} -> {fit.Piece.name}, turned {fit.Turn.eulerAngles.y:0}, moved {fit.Move}");
            return fit;
        }
    }
}
