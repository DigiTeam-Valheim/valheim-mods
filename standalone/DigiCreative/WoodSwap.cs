// Wood Swap (Creative world): one hammer tool per wood type ("Wood Swap: Timber", "Wood Swap: Ashwood", ...), at the
// end of the hammer's Building tab (or type "swap" in the build menu's search). Click a wooden building piece with one:
// it becomes the same shape in that wood, on the same snap points (DigiShared/WoodShapes.cs, shared with Building
// Tweaks). Hold Left Shift: every piece of the clicked piece's wood in that building. With a mirror line the mirrored
// twins are swapped too. Free, and the old piece drops nothing.
// When Building Tweaks' Wood Swap is on (the same 8 tools, which take materials outside the creative world), these
// stay out of the hammer, so it shows one set: Building Tweaks' tools are free here too, through free building.
// The tools are only in the hammer while you play the creative world (Tools.cs puts them there).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using DigiShared;
using UnityEngine;

namespace DigiCreative
{
    internal class WoodSwap : MonoBehaviour
    {
        internal static ConfigEntry<bool> On;
        internal static ConfigEntry<KeyboardShortcut> BuildingKey;
        private static BepInEx.Logging.ManualLogSource _log;
        private static WoodSwap _instance;


        internal static void Bind(ConfigFile config, BepInEx.Logging.ManualLogSource log)
        {
            _log = log;
            On = config.Bind("6 - Wood Swap", "Wood Swap tools", true,
                "One Wood Swap tool per wood type in the hammer menu (Building tab, at the end; or type swap in the search). " +
                "Click a wooden building piece with one: it becomes the same shape in that wood, on the same snap points.");
            BuildingKey = config.Bind("6 - Wood Swap", "Whole building key", new KeyboardShortcut(KeyCode.LeftShift),
                "Hold this while clicking with a Wood Swap tool: every piece of the clicked piece's wood in that building is swapped.");
        }

        // One tool per wood type, put in the hammer by Tools.
        internal static void Register()
        {
            for (var i = 0; i < WoodShapes.Types.Length; i++)
            {
                var type = i;
                var wood = WoodShapes.Types[i];
                Tools.Add(WoodShapes.ToolId(i), "Wood Swap: " + wood,
                    $"Click a wooden building piece: it becomes {wood}, the same shape on the same snap points. " +
                    $"Hold {BuildingKey.Value} for every piece of that wood in the building. Mirror lines are followed.",
                    WoodShapes.IconPieces[i], () => On.Value && !Tools.BuildingTweaksHas("Wood Swap"), player =>
                    {
                        if (_instance != null) _instance.StartCoroutine(Swap(player, type, BuildingKey.Value.IsPressed()));
                    });
            }
        }

        private void Awake() => _instance = this;

        private static bool _busy;

        private static IEnumerator Swap(Player player, int type, bool building)
        {
            if (_busy) yield break;
            var start = player.GetHoveringPiece();
            if (start == null || !start.IsPlacedByPlayer()) yield break;
            var name = Utils.GetPrefabName(start.gameObject);
            var member = WoodShapes.MemberOf(name);
            var what = Localization.instance.Localize(start.m_name);
            if (member == null)
            {
                Say(player, $"Wood Swap: {what} has no other wood types");
                yield break;
            }
            if (member.Type == type && !building)
            {
                Say(player, $"{what} is {WoodShapes.Types[type]} already");
                yield break;
            }
            if (member.Row[type].Length == 0 && !building)
            {
                Say(player, $"There is no {WoodShapes.Types[type]} version of {what}");
                yield break;
            }
            _busy = true;
            try
            {
                var targets = new List<Piece>();
                if (building)
                {
                    var wood = member.Type;
                    var found = new List<Piece>();
                    var steps = 0;
                    foreach (var piece in Tools.Building(start, found))
                        if (++steps % 150 == 0) yield return null;
                    targets.AddRange(found.Where(p => p != null && WoodShapes.MemberOf(Utils.GetPrefabName(p.gameObject))?.Type == wood));
                }
                else targets.Add(start);

                var index = Mirror.Active ? Index() : null;
                var done = new HashSet<Piece>();
                var swapped = 0;
                var none = 0;
                foreach (var old in targets)
                {
                    if (old == null || !done.Add(old)) continue;
                    var result = SwapOne(player, old, type, index, done);
                    if (result > 0) swapped += result;
                    else if (result < 0) none++;
                    if (swapped % 40 < result) yield return null;
                }
                var text = swapped == 1 ? $"{what} is {WoodShapes.Types[type]} now" : $"{swapped} pieces are {WoodShapes.Types[type]} now";
                if (none > 0) text += $" ({none} have no {WoodShapes.Types[type]} version)";
                player.Message(MessageHud.MessageType.TopLeft, text);
            }
            finally { _busy = false; }
        }

        // How many pieces were swapped (with its mirrored twins), 0 if it is that wood already, -1 if that wood has no
        // such shape. With mirror lines, a twin of the old piece (same kind, at its mirrored place) is replaced by the
        // mirror image of the new piece, as if it had been placed mirrored (so a left triangle wall gets the right one).
        private static int SwapOne(Player player, Piece old, int type, Dictionary<Vector3Int, List<Piece>> index, HashSet<Piece> done)
        {
            var name = Utils.GetPrefabName(old.gameObject);
            var member = WoodShapes.MemberOf(name);
            if (member == null || member.Row[type].Length == 0) return -1;
            if (member.Type == type) return 0;
            var fit = WoodShapes.FitFor(name, member, type, m => _log?.LogInfo(m));
            var wear = old.GetComponent<WearNTear>();
            if (fit == null || wear == null || !old.m_canBeRemoved) return -1;
            var oldImages = index != null ? Mirror.Images(PlacementOf(old)) : new List<Mirror.Placement>();
            var twins = oldImages.Select(image => Twin(index, image.Name, image.Pos, image.Rot)).ToList();
            var rot = old.transform.rotation * fit.Turn;
            var pos = old.transform.position + old.transform.rotation * fit.Move;
            var count = 0;
            var was = Mirror.Mirroring;
            Mirror.Mirroring = true; // placed and removed on purpose here, not mirrored again
            try
            {
                wear.Remove(true);
                player.PlacePiece(fit.Piece, pos, rot, false, false);
                count++;
                var newImages = Mirror.Images(new Mirror.Placement { Name = fit.Piece.name, Pos = pos, Rot = rot });
                for (var i = 0; i < twins.Count; i++)
                {
                    var twin = twins[i];
                    if (twin == null || !done.Add(twin)) continue;
                    var twinWear = twin.GetComponent<WearNTear>();
                    var place = newImages.Count == twins.Count ? Tools.PieceOf(newImages[i].Name) : null;
                    if (place != null && twinWear != null && twin.m_canBeRemoved)
                    {
                        twinWear.Remove(true);
                        player.PlacePiece(place, newImages[i].Pos, newImages[i].Rot, false, false);
                        count++;
                    }
                    else if (SwapOne(player, twin, type, null, done) > 0) count++; // a piece on a line: on its own
                }
            }
            finally { Mirror.Mirroring = was; }
            return count;
        }

        private static Mirror.Placement PlacementOf(Piece piece) =>
            new Mirror.Placement { Name = Utils.GetPrefabName(piece.gameObject), Pos = piece.transform.position, Rot = piece.transform.rotation };

        // Where the pieces are, for finding twins quickly (in cells of a quarter metre).
        private static Dictionary<Vector3Int, List<Piece>> Index()
        {
            var index = new Dictionary<Vector3Int, List<Piece>>();
            foreach (var piece in Piece.s_allPieces)
            {
                if (piece == null) continue;
                var cell = Vector3Int.FloorToInt(piece.transform.position * 4f);
                if (!index.TryGetValue(cell, out var list)) index[cell] = list = new List<Piece>();
                list.Add(piece);
            }
            return index;
        }

        private static Piece Twin(Dictionary<Vector3Int, List<Piece>> index, string name, Vector3 at, Quaternion rot)
        {
            var c = Vector3Int.FloorToInt(at * 4f);
            for (var x = -1; x <= 1; x++)
                for (var y = -1; y <= 1; y++)
                    for (var z = -1; z <= 1; z++)
                        if (index.TryGetValue(new Vector3Int(c.x + x, c.y + y, c.z + z), out var list))
                            foreach (var piece in list)
                                if (piece != null && (piece.transform.position - at).sqrMagnitude < 0.0025f && Utils.GetPrefabName(piece.gameObject) == name &&
                                    Mirror.SameSpot(name, piece.transform.position, piece.transform.rotation, at, rot))
                                    return piece;
            return null;
        }

        private static void Say(Player player, string text) => player.Message(MessageHud.MessageType.Center, text);
    }
}
