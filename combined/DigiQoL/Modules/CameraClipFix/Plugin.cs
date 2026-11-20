// Camera Clip Fix: in third person the camera no longer ends up partly inside a floor above you (or a roof or wall).
// The game tests the camera's path for things in the way from a point lowered to keep the camera above snow: in the
// Deep North always 0.4 m lower, elsewhere by the ground's snow height. A floor overhead was then found too late and
// the camera stopped partly inside it, filling the top of the screen with the floor's underside. This also tests the
// camera's real path and brings the camera closer when something is in the way, with a small gap. Where the camera was
// clear already nothing changes.
// Light: one or two physics casts per frame, in third person only.
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace CameraClipFix
{
    public class Plugin : DigiCore.DigiModule
    {
        private static ConfigEntry<float> _gap;

        private void Awake()
        {
            _gap = Config.Bind("General", "Gap to floors and walls (m)", 0.2f,
                new ConfigDescription("How close the camera may come to a floor above you, a roof or a wall, in meters. " +
                                      "The game itself keeps about 0.15 (where it finds them in time); more brings the camera " +
                                      "closer to you under low ceilings.",
                    new AcceptableValueRange<float>(0.1f, 1f)));
            PatchModule(typeof(Plugin));
        }

        [HarmonyPatch(typeof(GameCamera), "GetCameraPosition")]
        [HarmonyPostfix]
        private static void KeepClear(GameCamera __instance, float dt, ref Vector3 pos)
        {
            var player = Player.m_localPlayer;
            if (player == null || player.InIntro() || __instance.m_distance <= 0f) return; // first person: nothing to fix
            var mask = (int)__instance.m_blockCameraMask;
            var gap = _gap.Value;
            var start = __instance.GetOffsetedEyePos(); // the camera's path starts here (a little above your eyes)
            var from = start;
            // Under a very low ceiling that point can be too close to it already: then from your eyes.
            if (Physics.CheckSphere(from, gap, mask, QueryTriggerInteraction.Ignore)) from = player.m_eye.position;
            var path = pos - from;
            var length = path.magnitude;
            if (length < 0.001f) return;
            var dir = path / length;
            RaycastHit hit;
            if (!Physics.CheckSphere(from, gap, mask, QueryTriggerInteraction.Ignore))
            {
                if (!Physics.SphereCast(from, gap, dir, out hit, length, mask, QueryTriggerInteraction.Ignore)) return;
                pos = from + dir * hit.distance;
            }
            else
            {
                // No room for the gap at all: at least never past the surface.
                if (!Physics.Raycast(from, dir, out hit, length, mask, QueryTriggerInteraction.Ignore)) return;
                pos = from + dir * Mathf.Max(0f, hit.distance - __instance.m_nearClipPlaneMin);
            }
            __instance.UpdateNearClipping(start, pos, dt); // the game picked the near clip for the old spot
        }
    }
}
