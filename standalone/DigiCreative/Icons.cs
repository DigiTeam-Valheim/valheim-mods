// The creative tools' icons (make_creative_icons.py): CreativeIcons.png / .txt, embedded, read by the shared
// DigiShared/IconSheet.cs.
using DigiShared;
using UnityEngine;

namespace DigiCreative
{
    internal static class Icons
    {
        private static IconSheet _sheet;

        // The icon of a tool (its id without "piece_digi_"); under: the picture for its inner square, if it has one.
        internal static Sprite For(string id, Sprite under) =>
            (_sheet ?? (_sheet = new IconSheet(typeof(Icons).Assembly, "DigiCreative.CreativeIcons", w => Tools.Log?.LogWarning(w)))).For(id, under);
    }
}
