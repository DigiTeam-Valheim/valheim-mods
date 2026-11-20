// Tame Teleport: tamed animals that follow you come along when you teleport (through a portal, Portal List, or anything
// else that teleports you). The game moves only the player: a following wolf, boar or lox stayed behind at the portal.
// How: when your teleport starts (Player.TeleportTo said yes), the tames following you within range are noted (their
// ZDO ids). Once you have arrived (the game's teleport is done: the place is loaded and you stand on its floor), each is
// moved to just behind you: its ZDO's position (taken over when nobody else's game runs it: in multiplayer another
// player standing near it keeps it, and it stays), and its object too when it is still loaded. An object left in an
// area that unloaded was put away by the game and is made anew at the new spot; it follows you again by itself (the game
// keeps who a tame follows in its ZDO, Tameable.UpdateSavedFollowTarget). A teleport that fails (portal blocked: you
// are put back where you were) moves nothing.
// The arrival is seen in the game's own teleport update (Player.UpdateTeleport: its flag going off), in that same frame.
// Light: one look over the loaded creatures per teleport; nothing else runs.
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace TameTeleport
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.tameteleport";
        public const string Name = "Digi's Tame Teleport";
        public const string Version = "1.0.0";

        private static ConfigEntry<float> _range;
        private static ConfigEntry<bool> _onlyFollowing;
        private static readonly List<ZDOID> Going = new List<ZDOID>();
        private static Vector3 _from;
        private static int _solid;
        private static BepInEx.Logging.ManualLogSource _log;

        private void Awake()
        {
            _range = Config.Bind("General", "Range (m)", 10f, new ConfigDescription(
                "Tamed animals within this distance of you when the teleport starts come along.", new AcceptableValueRange<float>(2f, 50f)));
            _onlyFollowing = Config.Bind("General", "Only tames that follow you", true,
                "On: only tames that follow you (told to with E) come along. Off: every tamed animal in range, also those told to stay.");
            _solid = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain");
            _log = Logger;
            PatchModule(typeof(Plugin));
        }

        private void OnDestroy() => Going.Clear();

        // The teleport starts: who comes along.
        [HarmonyPatch(typeof(Player), nameof(Player.TeleportTo))]
        [HarmonyPostfix]
        private static void Leaving(Player __instance, bool __result)
        {
            if (!__result || __instance != Player.m_localPlayer) return;
            Going.Clear();
            _from = __instance.transform.position;
            var range = _range.Value * _range.Value;
            foreach (var character in Character.GetAllCharacters())
            {
                if (character == null || character == __instance || !character.IsTamed() || character.IsDead()) continue;
                if ((character.transform.position - _from).sqrMagnitude > range) continue;
                if (_onlyFollowing.Value && (!(character.GetBaseAI() is MonsterAI ai) || ai.GetFollowTarget() != __instance.gameObject)) continue;
                var view = character.GetComponent<ZNetView>();
                if (view == null || !view.IsValid()) continue;
                Going.Add(view.GetZDO().m_uid);
            }
        }

        // The teleport is done when the game's teleport update puts the player's flag off.
        [HarmonyPatch(typeof(Player), "UpdateTeleport")]
        [HarmonyPrefix]
        private static void BeforeTeleportUpdate(Player __instance, out bool __state) => __state = __instance.IsTeleporting();

        [HarmonyPatch(typeof(Player), "UpdateTeleport")]
        [HarmonyPostfix]
        private static void AfterTeleportUpdate(Player __instance, bool __state)
        {
            if (__state && !__instance.IsTeleporting() && __instance == Player.m_localPlayer) Arrived(__instance);
        }

        private static void Arrived(Player player)
        {
            if (Going.Count == 0) return;
            var going = new List<ZDOID>(Going);
            Going.Clear();
            var here = player.transform.position;
            if ((here - _from).sqrMagnitude < 4f) return; // blocked: put back where it started
            if (ZDOMan.instance == null || ZNetScene.instance == null) return;
            var me = ZDOMan.GetSessionID();
            int moved = 0, kept = 0;
            for (var i = 0; i < going.Count; i++)
            {
                var zdo = ZDOMan.instance.GetZDO(going[i]);
                if (zdo == null || (zdo.HasOwner() && zdo.GetOwner() != me)) // gone, or another player's game runs it
                {
                    kept++;
                    continue;
                }
                moved++;
                var spot = Spot(player, i, going.Count);
                zdo.SetOwner(me);
                zdo.SetPosition(spot);
                var instance = ZNetScene.instance.FindInstance(going[i]);
                if (instance == null) continue; // made anew at the spot by the game
                instance.transform.position = spot;
                var body = instance.GetComponent<Rigidbody>();
                if (body == null) continue;
                body.position = spot;
                body.linearVelocity = Vector3.zero;
            }
            _log?.LogInfo($"{moved} tame{(moved == 1 ? "" : "s")} came along" + (kept > 0 ? $", {kept} stayed (another player's game runs them)" : ""));
        }

        // Just behind the player, side by side: on the floor there (found from above, not higher than the player's head), or
        // on the player's own spot when a wall is in the way.
        private static Vector3 Spot(Player player, int index, int count)
        {
            var t = player.transform;
            var here = t.position;
            var side = (index - (count - 1) * 0.5f) * 1.2f;
            var spot = here - t.forward * 1.5f + t.right * side;
            var chest = here + Vector3.up * 1f;
            if (Physics.Linecast(chest, spot + Vector3.up * 0.5f, _solid)) spot = here + t.right * (side * 0.25f);
            if (Physics.Raycast(spot + Vector3.up * 1.5f, Vector3.down, out var hit, 3f, _solid)) spot.y = hit.point.y + 0.1f;
            else spot.y = here.y + 0.2f;
            return spot;
        }
    }
}
