// Staff Aim: a Lightning Strike lands where the crosshair is. The game fires this staff from low in front of you, parallel
// to your view instead of at what you look at, tilted 4 degrees up, with up to 5 degrees of random spread each way, and
// the shot drops on the way. Aimed along flat ground that is about 5 m short at 30 m and 18 m at 50 m, and it hardly gets
// past 40 m; the spread adds metres more in any direction.
// Here the shot's direction is replaced the moment it is fired: from where it leaves the staff to what the crosshair is
// on, in the arc that gets there at the shot's own speed and drop. So nothing else about the shot changes. It does not
// lead a moving creature. Only your own shots change, and the game syncs where a shot is, so nobody else needs this mod.
using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace StaffAim
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.staffaim";
        public const string Name = "Digi's Staff Aim";
        public const string Version = "1.0.0";

        // How far the crosshair is followed; a shot at 30 m/s with this drop reaches 225 m at most.
        private const float CrosshairRange = 300f;
        // The shot starts 2 m in front of you: for anything not at least this far past that, it just follows your view.
        private const float MinAhead = 1f;

        private static ConfigEntry<string> _weapons;
        private static int _mask;

        // Same layers the game's projectiles collide with.
        private static int Mask => _mask != 0 ? _mask : _mask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece",
            "piece_nonsolid", "terrain", "character", "character_net", "character_ghost", "hitbox", "character_noenv", "vehicle");

        private void Awake()
        {
            _weapons = Config.Bind("Aim", "Weapons", "StaffThunderBlood",
                "The weapons whose shots go to the crosshair, as prefab names with commas between them. StaffThunderBlood is Lightning Strike. " +
                "A weapon that fires a spray of shots loses its spray: they all go to one spot.");
            PatchModule(typeof(Plugin));
        }

        // Last, so the speed is the real one. The shot's rotation only matters for what it looks like and how the strike
        // stands: the game reads it again from the speed during the flight.
        [HarmonyPatch(typeof(Projectile), nameof(Projectile.Setup))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        private static void Aim(Projectile __instance, Character owner, ref Vector3 velocity, ItemDrop.ItemData item)
        {
            var camera = GameCamera.instance;
            if (owner == null || owner != Player.m_localPlayer || camera == null || !Listed(item)) return;
            var shot = item.m_shared.m_attack.m_attackProjectile;
            if (shot == null || shot.name != Utils.GetPrefabName(__instance.gameObject)) return; // what a shot spawns stays as it is
            var view = camera.transform;
            var from = __instance.transform.position;
            velocity = Crosshair(owner, view, out var target) && Vector3.Dot(target - from, view.forward) >= MinAhead
                ? Arc(from, target, velocity.magnitude, __instance.m_gravity)
                : view.forward * velocity.magnitude;
            __instance.transform.rotation = Quaternion.LookRotation(velocity);
        }

        private static bool Listed(ItemDrop.ItemData item)
        {
            var prefab = item?.m_dropPrefab;
            if (prefab == null) return false;
            foreach (var name in _weapons.Value.Split(','))
                if (string.Equals(name.Trim(), prefab.name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // What the crosshair is on, looking from level with you on: not what is between the camera and you, nor you.
        internal static bool Crosshair(Character owner, Transform view, out Vector3 target)
        {
            var past = Mathf.Max(0f, Vector3.Dot(owner.GetCenterPoint() - view.position, view.forward));
            var nearest = float.MaxValue;
            target = Vector3.zero;
            foreach (var hit in Physics.RaycastAll(view.position + view.forward * past, view.forward, CrosshairRange, Mask, QueryTriggerInteraction.Ignore))
            {
                if (hit.distance >= nearest || Projectile.FindHitObject(hit.collider) == owner.gameObject) continue;
                nearest = hit.distance;
                target = hit.point;
            }
            return nearest < float.MaxValue;
        }

        // The low arc from one point through another at this speed and drop. Out of reach: 45 degrees, the furthest it goes.
        private static Vector3 Arc(Vector3 from, Vector3 target, float speed, float gravity)
        {
            var to = target - from;
            var flat = new Vector3(to.x, 0f, to.z);
            var distance = flat.magnitude;
            if (gravity <= 0f || distance < 0.01f) return to.normalized * speed;
            var square = speed * speed;
            var root = square * square - gravity * (gravity * distance * distance + 2f * to.y * square);
            var slope = root >= 0f ? (square - Mathf.Sqrt(root)) / (gravity * distance) : 1f;
            return (flat / distance + Vector3.up * slope).normalized * speed;
        }
    }
}
