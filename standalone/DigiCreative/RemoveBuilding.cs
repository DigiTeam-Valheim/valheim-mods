// Remove Building (Creative world): a hammer tool to take away a whole building at once (a misplaced schematic, a test
// build). Click a piece: every piece connected to it (like Mirror's Shift+N) lights up and the message says how many;
// click the same building again within 5 seconds to remove them all. One click alone never removes anything. The marked
// pieces are tinted once (and the tint taken off again), not lit again every frame: a building can have thousands.
// Nothing drops, and mirror lines do not remove more (the mirrored half goes only if it is connected).
// Also used by the schematic library's "Undo last paste", which removes exactly the pieces of the last paste.
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using DigiShared;
using UnityEngine;

namespace DigiCreative
{
    internal class RemoveBuilding : MonoBehaviour
    {
        internal static ConfigEntry<bool> On;
        private static RemoveBuilding _instance;
        private static bool _busy;
        private static List<Piece> _marked;
        private static float _markedUntil;
        private static readonly Color MarkColor = new Color(1f, 0.45f, 0.3f);
        private const float ConfirmSeconds = 5f;
        private const int PerFrame = 100, MostHighlighted = 4000;

        internal static void Bind(ConfigFile config)
        {
            On = config.Bind("9 - Remove Building", "Remove Building tool", true,
                "A Remove Building tool in the hammer menu (Building tab, at the end). Click a building: every piece connected " +
                "to the one you click lights up; click it again within 5 seconds to remove them all (nothing drops).");
        }

        internal static void Register() =>
            Tools.Add("removebuilding", "Remove Building",
                "Click a building: every piece connected to the one you click lights up. Click it again within 5 seconds to " +
                "remove them all. Nothing drops.", "BombDynamite", () => On.Value, Click);

        private void Awake() => _instance = this;

        private static void Click(Player player)
        {
            if (_busy || _instance == null || Windows.IsOpen) return;
            var start = player.GetHoveringPiece();
            if (start == null || !start.IsPlacedByPlayer())
            {
                player.Message(MessageHud.MessageType.Center, "Aim at a piece of the building to remove");
                return;
            }
            if (_marked != null && Time.time < _markedUntil && _marked.Contains(start))
            {
                var pieces = _marked;
                _marked = null; // no need to take the tint off: they go
                Remove(player, pieces, "");
                return;
            }
            Unmark();
            _instance.StartCoroutine(Mark(player, start));
        }

        private static IEnumerator Mark(Player player, Piece start)
        {
            _busy = true;
            var found = new List<Piece>();
            try
            {
                var steps = 0;
                foreach (var piece in Tools.Building(start, found))
                    if (++steps % 150 == 0) yield return null;
            }
            finally { _busy = false; }
            _marked = found.Where(p => p != null).ToList();
            _markedUntil = Time.time + ConfirmSeconds;
            Tint(true);
            player.Message(MessageHud.MessageType.Center,
                $"{BuildMaterials.Number(_marked.Count)} pieces lit up: click again within {ConfirmSeconds:0} seconds to remove them all");
        }

        // The marked pieces lose their tint when the 5 seconds are over.
        private void Update()
        {
            if (_marked != null && Time.time >= _markedUntil) Unmark();
        }

        private void OnDestroy() => Unmark();

        private static void Unmark()
        {
            if (_marked == null) return;
            Tint(false);
            _marked = null;
        }

        // On or off, once for all marked pieces (up to MostHighlighted).
        private static void Tint(bool on)
        {
            if (_marked == null || MaterialMan.instance == null) return;
            var n = 0;
            foreach (var piece in _marked)
            {
                if (piece == null) continue;
                if (on)
                {
                    MaterialMan.instance.SetValue(piece.gameObject, ShaderProps._Color, MarkColor);
                    MaterialMan.instance.SetValue(piece.gameObject, ShaderProps._EmissionColor, MarkColor * 0.35f);
                }
                else
                {
                    MaterialMan.instance.ResetValue(piece.gameObject, ShaderProps._Color);
                    MaterialMan.instance.ResetValue(piece.gameObject, ShaderProps._EmissionColor);
                }
                if (++n >= MostHighlighted) break;
            }
        }

        // Removes these pieces, PerFrame a frame; what: added to the message ("of Tower" for an undone paste).
        internal static void Remove(Player player, List<Piece> pieces, string what)
        {
            if (_instance != null && !_busy) _instance.StartCoroutine(Removing(player, pieces, what));
        }

        private static IEnumerator Removing(Player player, List<Piece> pieces, string what)
        {
            _busy = true;
            var removed = 0;
            try
            {
                foreach (var piece in pieces)
                {
                    if (piece == null) continue;
                    var was = Mirror.Mirroring;
                    Mirror.Mirroring = true; // not mirrored: only these pieces
                    try
                    {
                        var wear = piece.GetComponent<WearNTear>();
                        if (wear != null) wear.Remove(true);
                        else if (ZNetScene.instance != null) ZNetScene.instance.Destroy(piece.gameObject);
                    }
                    finally { Mirror.Mirroring = was; }
                    if (++removed % PerFrame == 0) yield return null;
                }
            }
            finally { _busy = false; }
            if (player != null) player.Message(MessageHud.MessageType.Center, $"Removed {BuildMaterials.Number(removed)} pieces{what}");
        }
    }
}
