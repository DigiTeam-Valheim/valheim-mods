// Auto Fuel: your fires and lights keep themselves going from the chests around them: campfires, hearths, bonfires,
// torches, sconces, braziers, lanterns, the hot tub, anything built that burns fuel. Every few seconds each one this game
// controls is checked; when its fuel has burnt down to the refill level it is filled up with its own fuel (wood, resin,
// guck, ...) from chests within the range (100 m), nearest first, in one go (one "fuel added" sound, not one per piece).
// Each kind of fire has a switch of its own (section "Fires"): bound when the world's pieces are known, so it lists
// other mods' fires too. Fires that need no fuel and wild ones (dungeons, villages) are left alone.
// Chests follow the auto stations' rules: built chests only, not carts, ships or tombstones, not someone else's private
// chest, not while someone has it open, and never a chest whose "Take from it" box is unticked. A chest another
// player controls is claimed when nobody has it open and used from the next round on.
// Light: a small check per fire every 5 seconds; chests are only searched for a fire that needs fuel.
using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AutoFuel
{
    public class Plugin : DigiCore.DigiModule
    {
        private const float Interval = 5f;

        private static ConfigEntry<float> _range, _refillBelow;
        private static ConfigEntry<bool> _message;
        private static readonly Dictionary<string, ConfigEntry<bool>> Kinds = new Dictionary<string, ConfigEntry<bool>>();
        private static readonly HashSet<Fireplace> Fires = new HashSet<Fireplace>();
        private static readonly HashSet<Container> Chests = new HashSet<Container>();
        private static Plugin _instance;

        private static readonly AccessTools.FieldRef<Fireplace, ZNetView> FireView = AccessTools.FieldRefAccess<Fireplace, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<Container, ZNetView> ChestView = AccessTools.FieldRefAccess<Container, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<Container, Piece> ChestPiece = AccessTools.FieldRefAccess<Container, Piece>("m_piece");

        private float _timer;

        private void Awake()
        {
            _instance = this;
            _range = Config.Bind("General", "Chest range (m)", 100f, new ConfigDescription(
                "Chests up to this many metres from a fire or light are used for its fuel.", new AcceptableValueRange<float>(5f, 200f)));
            _refillBelow = Config.Bind("General", "Refill below (%)", 50f, new ConfigDescription(
                "A fire is filled up once its fuel is below this share of what it holds (100 = topped up all the time, one at a time).",
                new AcceptableValueRange<float>(5f, 100f)));
            _message = Config.Bind("General", "Show message", false, "A line top left when fires near you were refilled (\"Fires refilled: 12 wood\").");
            PatchModule(typeof(Plugin));
            foreach (var fire in FindObjectsByType<Fireplace>(FindObjectsSortMode.None)) Fires.Add(fire);
            foreach (var chest in FindObjectsByType<Container>(FindObjectsSortMode.None)) Chests.Add(chest);
            if (ZNetScene.instance != null) BindKinds(ZNetScene.instance);
        }

        [HarmonyPatch(typeof(Fireplace), "Awake")]
        [HarmonyPostfix]
        private static void FireAwake(Fireplace __instance) => Fires.Add(__instance);

        [HarmonyPatch(typeof(Container), "Awake")]
        [HarmonyPostfix]
        private static void ChestAwake(Container __instance) => Chests.Add(__instance);

        // Every built piece that burns fuel gets a switch, once the world's pieces are known.
        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        [HarmonyPostfix]
        private static void BindKinds(ZNetScene __instance)
        {
            foreach (var prefab in __instance.m_prefabs)
                if (prefab != null && prefab.GetComponent<Piece>() != null && Burns(prefab.GetComponent<Fireplace>()))
                    Kind(prefab.name, prefab.GetComponent<Piece>());
        }

        private static bool Burns(Fireplace fire) => fire != null && !fire.m_infiniteFuel && fire.m_fuelItem != null;

        private static ConfigEntry<bool> Kind(string prefab, Piece piece)
        {
            if (Kinds.TryGetValue(prefab, out var entry)) return entry;
            var name = piece != null && Localization.instance != null ? Localization.instance.Localize(piece.m_name) : prefab;
            var fire = piece != null ? piece.GetComponent<Fireplace>() : null;
            var fuel = fire != null && fire.m_fuelItem != null && Localization.instance != null
                ? Localization.instance.Localize(fire.m_fuelItem.m_itemData.m_shared.m_name).ToLowerInvariant() : "its fuel";
            entry = _instance.Config.Bind("Fires", $"{name} ({prefab})", true, $"{name} takes {fuel} from nearby chests.");
            Kinds[prefab] = entry;
            return entry;
        }

        private void Update()
        {
            if (Player.m_localPlayer == null || ZNet.instance == null) return;
            _timer -= Time.deltaTime;
            if (_timer > 0f) return;
            _timer = Interval;
            // Ultimate Builder's creative world: its stations stand still, nothing is taken from chests or filled there.
            if (DigiCore.CreativeWorld.Active) return;
            // One save per changed chest for the whole round (StoreRules.HoldSaves).
            StoreRules.HoldSaves();
            try
            {
                Fires.RemoveWhere(f => f == null);
                Chests.RemoveWhere(c => c == null);
                var added = new Dictionary<string, int>();
                foreach (var fire in Fires)
                {
                    try
                    {
                        var amount = Refill(fire);
                        if (amount <= 0) continue;
                        var fuel = fire.m_fuelItem.m_itemData.m_shared.m_name;
                        added[fuel] = (added.TryGetValue(fuel, out var n) ? n : 0) + amount;
                    }
                    catch (Exception e)
                    {
                        Logger.LogWarning($"Fire at {fire.transform.position}: {e.Message}");
                    }
                }
                if (_message.Value && added.Count > 0)
                    Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft,
                        "Fires refilled: " + string.Join(", ", added.Select(p => $"{p.Value} {Localization.instance.Localize(p.Key).ToLowerInvariant()}")));
            }
            finally
            {
                StoreRules.SaveHeld();
            }
        }

        // Fills one fire up from the chests when its fuel is low; how many it put in.
        private static int Refill(Fireplace fire)
        {
            if (!Burns(fire)) return 0;
            var view = FireView(fire);
            if (view == null || !view.IsValid() || !view.IsOwner()) return 0;
            var piece = fire.GetComponent<Piece>();
            if (piece == null || !piece.IsPlacedByPlayer()) return 0; // wild fires stay as the world made them
            if (!Kind(Utils.GetPrefabName(fire.gameObject), piece).Value) return 0;
            var fuel = view.GetZDO().GetFloat(ZDOVars.s_fuel);
            if (fuel >= fire.m_maxFuel * _refillBelow.Value / 100f && _refillBelow.Value < 100f) return 0;
            var room = Mathf.FloorToInt(fire.m_maxFuel - fuel);
            if (room < 1) return 0;
            var chests = ChestsNear(fire.transform.position);
            if (chests.Count == 0) return 0;
            var fuelName = fire.m_fuelItem.m_itemData.m_shared.m_name;
            var taken = 0;
            while (taken < room && StoreRules.TakeOne(chests, item => item.m_shared.m_name == fuelName, out _)) taken++;
            if (taken > 0) view.InvokeRPC("RPC_AddFuelAmount", (float)taken);
            return taken;
        }

        // Chests within range of the spot, nearest first, that this game may change now.
        private static List<Container> ChestsNear(Vector3 at)
        {
            var me = Game.instance.GetPlayerProfile().GetPlayerID();
            var range = _range.Value;
            var result = new List<Container>();
            var dists = new Dictionary<Container, float>();
            foreach (var chest in Chests)
            {
                if (chest == null) continue;
                var dist = (chest.transform.position - at).sqrMagnitude;
                if (dist > range * range) continue;
                if (chest.m_wagon != null || chest.GetComponentInParent<Ship>() != null || chest.GetComponent<TombStone>() != null) continue;
                var piece = ChestPiece(chest);
                if (piece == null) continue; // only built chests
                if (chest.m_privacy == Container.PrivacySetting.Private && piece.GetCreator() != me) continue;
                if (chest.m_privacy == Container.PrivacySetting.Group) continue;
                var view = ChestView(chest);
                if (view == null || !view.IsValid() || chest.GetInventory() == null) continue;
                if (view.GetZDO().GetInt(ZDOVars.s_inUse) == 1 || chest.IsInUse()) continue;
                if (!view.IsOwner())
                {
                    view.ClaimOwnership();
                    continue;
                }
                result.Add(chest);
                dists[chest] = dist;
            }
            return result.OrderBy(c => dists[c]).ToList();
        }
    }
}
