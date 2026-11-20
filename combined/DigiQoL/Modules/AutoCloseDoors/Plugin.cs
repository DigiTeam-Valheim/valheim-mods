// Auto Close Doors: a door that was opened closes by itself after a while (15 seconds), once nobody stands in it.
// The world's own doors (dungeons, crypts), doors that need a key and doors that cannot be closed are left alone.
// The game owner of the door closes it, with the game's own "use door" (sound and swing as usual), so in multiplayer
// one player with this mod near the door is enough and it never closes twice.
// Lag: one look over the loaded doors twice a second.
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AutoCloseDoors
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.autoclosedoors";
        public const string Name = "Digi's Auto Close Doors";
        public const string Version = "1.0.0";

        private static ConfigEntry<int> _delay;
        private static ConfigEntry<float> _clearance;
        private static ConfigEntry<bool> _builtOnly;
        private static readonly HashSet<Door> Doors = new HashSet<Door>();
        private static readonly Dictionary<Door, float> OpenSince = new Dictionary<Door, float>();
        private static readonly List<Door> Gone = new List<Door>();
        private float _next;

        private void Awake()
        {
            _delay = Config.Bind("General", "Close after (seconds)", 15, new ConfigDescription(
                "An open door closes by itself after this many seconds.", new AcceptableValueRange<int>(3, 300)));
            _clearance = Config.Bind("General", "Wait while someone is within (m)", 2.5f, new ConfigDescription(
                "A door stays open while a player stands this close to it, so it never closes on someone in the doorway. " +
                "0 = close anyway.", new AcceptableValueRange<float>(0f, 5f)));
            _builtOnly = Config.Bind("General", "Only doors players built", true,
                "Leave the world's own doors (dungeons, crypts) alone.");
            foreach (var door in FindObjectsByType<Door>(FindObjectsSortMode.None)) Doors.Add(door);
            PatchModule(typeof(Plugin));
        }

        private void OnDestroy()
        {
            Doors.Clear();
            OpenSince.Clear();
        }

        [HarmonyPatch(typeof(Door), "Awake")]
        [HarmonyPostfix]
        private static void DoorLoaded(Door __instance) => Doors.Add(__instance);

        private void Update()
        {
            var now = Time.time;
            if (now < _next) return;
            _next = now + 0.5f;
            Gone.Clear();
            foreach (var door in Doors)
            {
                if (door == null)
                {
                    Gone.Add(door);
                    continue;
                }
                var view = door.m_nview;
                if (view == null || !view.IsValid()) continue;
                if (view.GetZDO().GetInt(ZDOVars.s_state) == 0 || !Closable(door))
                {
                    OpenSince.Remove(door);
                    continue;
                }
                if (!OpenSince.TryGetValue(door, out var since))
                {
                    OpenSince[door] = now;
                    continue;
                }
                if (now - since < _delay.Value || !view.IsOwner() || SomeoneNear(door.transform.position)) continue;
                view.InvokeRPC("UseDoor", true); // an open door closes (a door still swinging ignores it: tried again)
            }
            foreach (var door in Gone)
            {
                Doors.Remove(door);
                OpenSince.Remove(door);
            }
        }

        private static bool Closable(Door door)
        {
            if (door.m_keyItem != null || door.m_canNotBeClosed) return false;
            if (!_builtOnly.Value) return true;
            var piece = door.GetComponent<Piece>();
            return piece != null && piece.IsPlacedByPlayer();
        }

        private static bool SomeoneNear(Vector3 position)
        {
            var range = _clearance.Value;
            if (range <= 0f) return false;
            foreach (var player in Player.GetAllPlayers())
            {
                if (player == null) continue;
                var offset = player.transform.position - position;
                offset.y *= 2f; // a player on the floor above or below is not in the doorway
                if (offset.sqrMagnitude < range * range) return true;
            }
            return false;
        }
    }
}
