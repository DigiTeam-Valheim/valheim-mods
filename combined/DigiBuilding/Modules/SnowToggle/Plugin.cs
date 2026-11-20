// Snow Toggle: hide the snow the game lays on your roofs in the Deep North (or on every building piece), only the look.
// The game shows that snow as a mesh of its own on each piece (normal, worn and broken); this keeps those meshes switched
// off while "Hide snow" is on. The snow itself stays as the game has it: switched off again (or the toggle key), every
// piece shows its snow again exactly as it was. The game sets the meshes again whenever the snow or the piece's wear
// changes, so the check runs right after that (WearNTear.UpdateSnowVisual), and once for every piece when the setting
// changes. The snow that some materials paint on top surfaces is left alone (Snow Under Roofs handles that one).
using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SnowToggle
{
    public class Plugin : DigiCore.DigiModule
    {
        public enum Pieces { Roofs, AllPieces }

        private static ConfigEntry<bool> _hide;
        private static ConfigEntry<Pieces> _which;
        private static ConfigEntry<KeyboardShortcut> _key;
        private static bool _running;

        private void Awake()
        {
            _hide = Config.Bind("General", "Hide snow", false,
                "Hides the snow on your roofs (or on every building piece, see below). Only the look: switched off, the snow is " +
                "back exactly as it was. The toggle key switches it while you play.");
            _which = Config.Bind("General", "Pieces", Pieces.Roofs,
                "Roofs: only roof pieces (every piece with \"roof\" in its name). AllPieces: every building piece (floors, beams, walls too).");
            _key = Config.Bind("General", "Toggle key", KeyboardShortcut.Empty,
                "A key that switches \"Hide snow\" on and off while you play (empty: none, use F1).");
            _hide.SettingChanged += (s, e) => RefreshAll();
            _which.SettingChanged += (s, e) => RefreshAll();
            PatchModule(typeof(Plugin));
            _running = true;
            RefreshAll();
        }

        private void OnDestroy()
        {
            _running = false;
            RefreshAll(); // every piece shows the game's own snow again
        }

        private void Update()
        {
            var player = Player.m_localPlayer;
            if (player == null || _key.Value.MainKey == KeyCode.None || !_key.Value.IsDown()) return;
            if ((Chat.instance != null && Chat.instance.HasFocus()) || global::Console.IsVisible() || TextInput.IsVisible() || Menu.IsVisible()) return;
            _hide.Value = !_hide.Value;
            player.Message(MessageHud.MessageType.TopLeft, (_which.Value == Pieces.Roofs ? "Roof snow " : "Building snow ") + (_hide.Value ? "hidden" : "shown"));
        }

        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.UpdateSnowVisual))]
        [HarmonyPostfix]
        private static void AfterSnow(WearNTear __instance)
        {
            if (!_running || !_hide.Value || __instance == null || __instance.m_snow == null || !Applies(__instance)) return;
            Off(__instance.m_snow);
            Off(__instance.m_snowWorn);
            Off(__instance.m_snowBroken);
        }

        private static void Off(MeshRenderer snow)
        {
            if (snow != null && snow.gameObject.activeSelf) snow.gameObject.SetActive(false);
        }

        private static bool Applies(WearNTear piece) =>
            _which.Value == Pieces.AllPieces || piece.gameObject.name.IndexOf("roof", StringComparison.OrdinalIgnoreCase) >= 0;

        // The game sets every piece's snow again; with "Hide snow" on, the postfix above then hides it.
        private static void RefreshAll()
        {
            try
            {
                foreach (var piece in WearNTear.GetAllInstances())
                    if (piece != null && piece.m_snow != null) piece.UpdateSnowVisual();
            }
            catch (Exception) { /* pieces loading or unloading: the next snow change sets them */ }
        }
    }
}
