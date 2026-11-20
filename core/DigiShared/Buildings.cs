// Shared by Ultimate Builder and Building Tweaks (each mod compiles this file in): a building = every player-built piece
// touching the one you clicked, and every piece touching those (the box around each piece's solid colliders, 10 cm
// wider, against the piece layers).
using System.Collections.Generic;
using UnityEngine;

namespace DigiShared
{
    internal static class Buildings
    {
        // The box around a piece's solid colliders (its place if it has none).
        internal static Bounds BoundsOf(Piece piece)
        {
            var bounds = new Bounds(piece.transform.position, Vector3.zero);
            foreach (var collider in piece.GetComponentsInChildren<Collider>())
                if (collider.enabled && !collider.isTrigger) bounds.Encapsulate(collider.bounds);
            return bounds;
        }

        // Fills found, a step per piece, so a coroutine can spread a big building over some frames.
        internal static IEnumerable<Piece> Connected(Piece start, List<Piece> found, int max = 20000)
        {
            var seen = new HashSet<Piece> { start };
            found.Add(start);
            var hits = new Collider[128];
            var mask = LayerMask.GetMask("piece", "piece_nonsolid");
            for (var i = 0; i < found.Count && found.Count < max; i++)
            {
                var piece = found[i];
                if (piece == null) continue;
                var bounds = BoundsOf(piece);
                var count = Physics.OverlapBoxNonAlloc(bounds.center, bounds.extents + Vector3.one * 0.1f, hits, Quaternion.identity, mask);
                for (var h = 0; h < count; h++)
                {
                    var other = hits[h] != null ? hits[h].GetComponentInParent<Piece>() : null;
                    if (other != null && other.IsPlacedByPlayer() && seen.Add(other)) found.Add(other);
                }
                yield return piece;
            }
        }
    }
}
