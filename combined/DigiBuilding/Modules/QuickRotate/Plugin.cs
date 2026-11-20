// Quick Rotate: while placing a building piece, R turns it by 90 degrees (45 or 180 in the settings), Shift + R turns it
// back. The mouse wheel still turns it in the game's 22.5 degree steps.
// R is normally the game's "Hide" key, which puts the hammer away: with the hammer in hand it no longer does (switchable),
// and with other tools R turns a piece that can turn instead of hiding (elsewhere it hides as before).
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace QuickRotate
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.quickrotate";
        public const string Name = "Digi's Quick Rotate";
        public const string Version = "1.0.0";

        private static ConfigEntry<KeyCode> _key;
        private static ConfigEntry<int> _degrees;
        private static ConfigEntry<bool> _keepHammer;

        private static readonly AccessTools.FieldRef<Player, int> Turn = AccessTools.FieldRefAccess<Player, int>("m_placeRotation");
        private static readonly AccessTools.FieldRef<Player, float> TurnStep = AccessTools.FieldRefAccess<Player, float>("m_placeRotationDegrees");
        private static readonly AccessTools.FieldRef<Player, GameObject> Ghost = AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> RightItem = AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_rightItem");

        private void Awake()
        {
            _key = Config.Bind("General", "Rotate key", KeyCode.R,
                "While placing a building piece: this key turns it by the angle below, with Shift the other way. With " +
                "other tools than the hammer it turns a piece that can turn instead of putting the tool away.");
            _degrees = Config.Bind("General", "Rotate by (degrees)", 90, new ConfigDescription(
                "How far one press turns the piece.", new AcceptableValueList<int>(45, 90, 180)));
            _keepHammer = Config.Bind("General", "Hide key keeps the hammer out", true,
                "The game's Hide key (R) no longer puts the hammer away. Other tools and weapons still go away with it.");
            PatchModule(typeof(Plugin));
        }

        private void Update()
        {
            var player = Player.m_localPlayer;
            if (!CanTurn(player) || !UnityInput.Current.GetKeyDown(_key.Value)) return;
            var steps = Mathf.Max(1, Mathf.RoundToInt(_degrees.Value / Mathf.Max(1f, TurnStep(player))));
            var back = UnityInput.Current.GetKey(KeyCode.LeftShift) || UnityInput.Current.GetKey(KeyCode.RightShift);
            Turn(player) += back ? -steps : steps;
        }

        // Placing a piece that can turn, nothing typed, no build menu open.
        private static bool CanTurn(Player player)
        {
            if (player == null || !player.InPlaceMode() || Hud.IsPieceSelectionVisible() || Typing()) return false;
            var piece = player.GetSelectedPiece();
            var ghost = Ghost(player);
            return piece != null && piece.m_canRotate && ghost != null && ghost.activeInHierarchy;
        }

        private static bool Typing() =>
            (Chat.instance != null && Chat.instance.HasFocus()) || Console.IsVisible() || TextInput.IsVisible() || Minimap.InTextInput() ||
            (Hud.instance != null && Hud.instance.m_buildUi != null && Hud.instance.m_buildUi.SearchFieldFocused); // the build menu's search box

        // The game's "Hide" (R): never for the hammer, and never while the same key turns the piece.
        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetButtonDown))]
        [HarmonyPostfix]
        private static void NoHideWhileTurning(string name, ref bool __result)
        {
            if (!__result || name != "Hide") return;
            var player = Player.m_localPlayer;
            if ((_keepHammer.Value && HoldsHammer(player)) || (UnityInput.Current.GetKey(_key.Value) && CanTurn(player))) __result = false;
        }

        private static bool HoldsHammer(Player player)
        {
            var item = player != null ? RightItem(player) : null;
            return item?.m_dropPrefab != null && item.m_dropPrefab.name == "Hammer";
        }
    }
}
