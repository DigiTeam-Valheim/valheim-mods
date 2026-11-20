// Live copies only (ScriptEngine): the Lag Meter's timers rethrow an error from a timed game method without its inner
// stack, so the first errors in the player's and the camera's updates are written to the log in full here.
using System;
using System.Linq;
using HarmonyLib;

namespace DigiCreative
{
    internal static class Diagnostics
    {
        private static BepInEx.Logging.ManualLogSource _log;
        private static int _logged;

        internal static void Patch(Harmony harmony, BepInEx.Logging.ManualLogSource log)
        {
            _log = log;
            var catcher = new HarmonyMethod(typeof(Diagnostics), nameof(Caught)) { priority = Priority.First + 100 };
            harmony.Patch(AccessTools.Method(typeof(Player), "Update"), finalizer: catcher);
            harmony.Patch(AccessTools.Method(typeof(GameCamera), "LateUpdate"), finalizer: catcher);
            foreach (var target in new[]
                     {
                         AccessTools.Method(typeof(Player), "UpdatePlacement"), AccessTools.Method(typeof(Player), "UpdateBuildGuiInput"),
                         AccessTools.Method(typeof(Hud), nameof(Hud.TogglePieceSelection)), AccessTools.Method(typeof(BuildUi), "OpenBuildMenu"),
                     })
                if (target != null) harmony.Patch(target, finalizer: catcher);
        }

        // What the player's build list holds: pieces that are gone (destroyed) are the usual cause.
        internal static string State()
        {
            var player = Player.m_localPlayer;
            if (player == null) return "no player";
            var table = player.m_buildPieces;
            var hammer = ObjectDB.instance?.GetItemPrefab("Hammer")?.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_buildPieces;
            var selected = player.GetSelectedPiece();
            var text = $"build table {(table == null ? "none" : table.name)} (the hammer's: {table == hammer}), selected " +
                       $"{((object)selected == null ? "none" : selected == null ? "a DESTROYED piece" : selected.name)}";
            if (table == null) return text;
            var gone = table.m_pieces.Count(p => (object)p != null && p == null);
            var goneAvailable = table.m_availablePiecesByCategory.Sum(list => list.Count(p => (object)p != null && p == null));
            var tools = table.m_pieces.Count(p => p != null && p.name.StartsWith("piece_digi_"));
            return text + $", {table.m_pieces.Count} pieces ({gone} gone, {tools} creative tools), {goneAvailable} gone in the available lists";
        }

        private static Exception Caught(Exception __exception, System.Reflection.MethodBase __originalMethod)
        {
            if (__exception != null && _logged < 12)
            {
                _logged++;
                _log?.LogWarning($"Error in {__originalMethod?.DeclaringType?.Name}.{__originalMethod?.Name}: {__exception}");
                if (_logged == 1) _log?.LogWarning(State());
            }
            return __exception;
        }
    }
}
