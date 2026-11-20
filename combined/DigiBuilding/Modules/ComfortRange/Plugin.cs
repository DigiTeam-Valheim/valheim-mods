// Comfort Range: how far away a comfort piece still counts toward your comfort level, per comfort group (fires, beds,
// banners, chairs, tables, rugs, ...). 100% is the game's own 10 m for every group; 200% counts a piece up to 20 m away.
// The game's rules stay as they are: you must be under a roof (sheltered) for any comfort beyond the base level, and of
// each group only the best piece counts (pieces without a group count once per kind). Only where they are counted from
// changes. Resting still needs a fire's warmth (its own small area): that is no comfort range.
// Light: replaces the game's own list of comfort pieces near you (asked about once a second) with one that measures
// each piece against its group's range; at 100% for all groups the game's own code runs.
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ComfortRange
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.comfortrange";
        public const string Name = "Digi's Comfort Range";
        public const string Version = "1.0.0";

        private const float GameRange = 10f; // SE_Rested.c_ComfortRadius

        private static readonly Dictionary<Piece.ComfortGroup, ConfigEntry<int>> Ranges = new Dictionary<Piece.ComfortGroup, ConfigEntry<int>>();
        private static HashSet<Piece> _allComfort;
        private static List<Piece> _temp;
        private static System.Reflection.FieldInfo _ghostLayer;

        private static readonly Dictionary<Piece.ComfortGroup, string> Kinds = new Dictionary<Piece.ComfortGroup, string>
        {
            { Piece.ComfortGroup.Fire, "Fires (campfire, bonfire, hearth, ...)" },
            { Piece.ComfortGroup.Bed, "Beds" },
            { Piece.ComfortGroup.Banner, "Banners" },
            { Piece.ComfortGroup.Chair, "Chairs, stools, benches and thrones" },
            { Piece.ComfortGroup.Table, "Tables" },
            { Piece.ComfortGroup.Carpet, "Rugs" },
            { Piece.ComfortGroup.Display, "The game's Display group" },
            { Piece.ComfortGroup.Decor, "The game's Decor group" },
            { Piece.ComfortGroup.Garland, "Garlands" },
            { Piece.ComfortGroup.Lantern, "Lanterns" },
            { Piece.ComfortGroup.Leisure, "The game's Leisure group (hot tub, ...)" },
        };

        private void Awake()
        {
            foreach (Piece.ComfortGroup group in Enum.GetValues(typeof(Piece.ComfortGroup)))
            {
                var name = group == Piece.ComfortGroup.None ? "Other" : group.ToString();
                var kind = Kinds.TryGetValue(group, out var k) ? k : group == Piece.ComfortGroup.None
                    ? "Comfort pieces without a group of their own (each kind counts once)" : "The game's " + group + " group";
                Ranges[group] = Config.Bind("Range", name + " (%)", 100, new ConfigDescription(
                    kind + ": how far away they still count for comfort. 100% = the game's own 10 m, 200% = 20 m.",
                    new AcceptableValueRange<int>(10, 500)));
            }
            var all = AccessTools.Field(typeof(Piece), "s_allComfortPieces");
            var temp = AccessTools.Field(typeof(SE_Rested), "s_tempPieces");
            _ghostLayer = AccessTools.Field(typeof(Piece), "s_ghostLayer");
            _allComfort = all?.GetValue(null) as HashSet<Piece>;
            _temp = temp?.GetValue(null) as List<Piece>;
            if (_allComfort == null || _temp == null)
            {
                Logger.LogWarning("The game's list of comfort pieces was not found (game update?): comfort ranges stay as in the game.");
                return;
            }
            PatchModule(typeof(Plugin));
        }

        private static bool AllAtGameRange()
        {
            foreach (var range in Ranges.Values)
                if (range.Value != 100) return false;
            return true;
        }

        [HarmonyPatch(typeof(SE_Rested), "GetNearbyComfortPieces")]
        [HarmonyPrefix]
        private static bool Nearby(Vector3 point, ref List<Piece> __result)
        {
            if (AllAtGameRange()) return true; // the game's own code
            var ghost = _ghostLayer != null ? (int)_ghostLayer.GetValue(null) : -1;
            _temp.Clear();
            foreach (var piece in _allComfort)
            {
                if (piece == null || piece.gameObject.layer == ghost) continue;
                var range = Ranges.TryGetValue(piece.m_comfortGroup, out var entry) ? GameRange * entry.Value / 100f : GameRange;
                if (Vector3.Distance(point, piece.transform.position) < range) _temp.Add(piece);
            }
            __result = _temp;
            return false;
        }
    }
}
