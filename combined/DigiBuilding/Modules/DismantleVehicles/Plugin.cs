// Dismantle Vehicles: the hammer can take apart the ships, carts, battering ram and catapult you built, like any other
// piece: all materials back, and what is stored in them drops out (the game does that itself when they break).
// In vanilla these pieces cannot be removed at all (Piece.m_canBeRemoved is off). This switches it on per piece, only
// for ones a player built (siege engines placed by the world stay as they are). Still refused: a ship with someone on
// board (the game forgets to check that for ships with a cargo hold) and a cart, ram or catapult someone is pulling.
// Light: pieces that can already be removed are skipped after one field check; the rest are set once when they load
// or are built. Nothing runs per frame.
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace DismantleVehicles
{
    public class Plugin : DigiCore.DigiModule
    {
        private enum Kind { None, Ship, Cart, Siege }

        private static ConfigEntry<bool> _ships, _carts, _siege;
        private static readonly HashSet<Piece> Opened = new HashSet<Piece>(); // pieces this switched to removable

        private void Awake()
        {
            _ships = Config.Bind("General", "Ships", true, "Rafts, karves and longships (also the Ashlands one) can be taken apart with the hammer.");
            _carts = Config.Bind("General", "Carts", true, "Carts can be taken apart with the hammer.");
            _siege = Config.Bind("General", "Siege engines", true, "The battering ram and the catapult can be taken apart with the hammer.");
            _ships.SettingChanged += (_, __) => ApplyAll();
            _carts.SettingChanged += (_, __) => ApplyAll();
            _siege.SettingChanged += (_, __) => ApplyAll();
            PatchModule(typeof(Plugin));
            ApplyAll();
        }

        private void OnDestroy()
        {
            foreach (var piece in Opened)
                if (piece != null) piece.m_canBeRemoved = false;
            Opened.Clear();
        }

        private static void ApplyAll()
        {
            foreach (var piece in FindObjectsByType<Piece>(FindObjectsSortMode.None))
                if (!piece.m_canBeRemoved || Opened.Contains(piece)) Apply(piece);
        }

        [HarmonyPatch(typeof(Piece), "Awake")]
        [HarmonyPostfix]
        private static void Loaded(Piece __instance)
        {
            if (!__instance.m_canBeRemoved) Apply(__instance);
        }

        // A piece just built: now it has a creator.
        [HarmonyPatch(typeof(Piece), nameof(Piece.SetCreator))]
        [HarmonyPostfix]
        private static void Built(Piece __instance)
        {
            if (!__instance.m_canBeRemoved) Apply(__instance);
        }

        private static void Apply(Piece piece)
        {
            if (piece == null) return;
            var kind = KindOf(piece);
            if (kind == Kind.None) return;
            var allow = piece.IsPlacedByPlayer() &&
                        (kind == Kind.Ship ? _ships.Value : kind == Kind.Cart ? _carts.Value : _siege.Value);
            if (allow && !piece.m_canBeRemoved)
            {
                piece.m_canBeRemoved = true;
                Opened.RemoveWhere(p => p == null);
                Opened.Add(piece);
            }
            else if (!allow && Opened.Remove(piece)) piece.m_canBeRemoved = false;
        }

        private static Kind KindOf(Piece piece)
        {
            if (piece.GetComponentInChildren<Ship>() != null) return Kind.Ship;
            if (piece.GetComponentInChildren<Vagon>() == null) return Kind.None;
            var name = Utils.GetPrefabName(piece.gameObject);
            return name == "BatteringRam" || name == "Catapult" ? Kind.Siege : Kind.Cart;
        }

        // Not while someone is on board or pulling it.
        [HarmonyPatch(typeof(Piece), nameof(Piece.CanBeRemoved))]
        [HarmonyPostfix]
        private static void NotInUse(Piece __instance, ref bool __result)
        {
            if (!__result || !Opened.Contains(__instance)) return;
            var ship = __instance.GetComponentInChildren<Ship>();
            if (ship != null && !ship.CanBeRemoved()) __result = false;
            var vagon = __instance.GetComponentInChildren<Vagon>();
            if (vagon != null && vagon.IsAttached()) __result = false;
        }
    }
}
