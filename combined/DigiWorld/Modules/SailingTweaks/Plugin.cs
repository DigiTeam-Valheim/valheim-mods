// Sailing quality of life, every part switchable:
// - Tailwind: while you steer, the wind blows from behind the ship (optionally only while the sail is up), with an
//   optional minimum wind strength so you never sit still in a calm.
// - Faster sailing and rowing (the sail's tilt is compensated, so the ship does not heel over more).
// - No wave-slam damage (capsizing and Ashlands ocean damage stay).
// - Walking into a ship from outside pushes it much harder, to turn or launch big ships by hand.
// - Optionally hide your own ship's sail, and reveal more of the map while at sea.
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SailingTweaks
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.sailingtweaks";
        public const string Name = "Digi's Sailing Tweaks";
        public const string Version = "1.0.1";

        private static ConfigEntry<bool> _tailwind, _tailwindSailUpOnly, _noWaveDamage, _push, _hideSail;
        private static ConfigEntry<float> _minWind, _sailSpeed, _rowSpeed, _explore, _pushStrength;

        private static readonly AccessTools.FieldRef<Ship, float> LastDepth = AccessTools.FieldRefAccess<Ship, float>("m_lastDepth");
        private static readonly AccessTools.FieldRef<Ship, float> LastWaterForceTime = AccessTools.FieldRefAccess<Ship, float>("m_lastUpdateWaterForceTime");
        private static readonly AccessTools.FieldRef<Ship, float> LastImpactTime = AccessTools.FieldRefAccess<Ship, float>("m_lastWaterImpactTime");
        private static readonly System.Reflection.FieldInfo SailCloth = AccessTools.Field(typeof(Ship), "m_sailCloth");
        private static readonly ConditionalWeakTable<Ship, Renderer> SailRenderers = new ConditionalWeakTable<Ship, Renderer>();

        // The plugin ID changed from pieter.valheim.* to digi.valheim.*: carry the old settings file over once.
        private void MigrateConfig()
        {
            var old = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, Guid.Replace("digi.valheim.", "pieter.valheim.") + ".cfg");
            if (System.IO.File.Exists(Config.ConfigFilePath) || !System.IO.File.Exists(old)) return;
            System.IO.File.Copy(old, Config.ConfigFilePath);
            Config.Reload();
        }


        private void Awake()
        {
            MigrateConfig();
            _tailwind = Config.Bind("1 - Wind", "Always tailwind", true, "While you steer a ship, the wind always blows from behind.");
            _tailwindSailUpOnly = Config.Bind("1 - Wind", "Only with sail up", false, "Tailwind only while the sail is up; while rowing you get the real wind.");
            _minWind = Config.Bind("1 - Wind", "Minimum wind strength (%)", 0f,
                new ConfigDescription("While you steer, the wind is always at least this strong. 0 = normal (no wind can leave you stuck).",
                    new AcceptableValueRange<float>(0f, 100f)));
            _sailSpeed = Config.Bind("2 - Speed", "Sailing speed multiplier", 4f,
                new ConfigDescription("Push from the sail, times this. Water slows a fast ship down more, so top speed grows less: " +
                    "1 = vanilla, 4 = about 2x as fast, 10 = about 3x as fast.",
                    new AcceptableValueRange<float>(1f, 20f)));
            _rowSpeed = Config.Bind("2 - Speed", "Rowing speed multiplier", 2f,
                new ConfigDescription("Rowing power, times this (forward and backward).", new AcceptableValueRange<float>(1f, 10f)));
            _noWaveDamage = Config.Bind("3 - Ship", "No wave damage", true,
                "Ships take no damage from slamming into waves. Capsizing and the Ashlands' burning sea still hurt.");
            _push = Config.Bind("3 - Ship", "Stronger push", true,
                "Walking into a ship from outside pushes it much harder, so you can turn or launch big ships by hand.");
            _pushStrength = Config.Bind("3 - Ship", "Push strength", 4f,
                new ConfigDescription("How hard you push a ship by walking into it (higher = moves faster).", new AcceptableValueRange<float>(1f, 20f)));
            _hideSail = Config.Bind("4 - View", "Hide sail", false, "Hide the sail of the ship you are on while it is up, so it does not block your view. Only on your screen.");
            _explore = Config.Bind("4 - View", "Map reveal radius at sea", 2f,
                new ConfigDescription("How far around you the map gets uncovered while you are on a ship, times this.", new AcceptableValueRange<float>(1f, 5f)));
            PatchModule(typeof(Plugin));
        }

        private static Ship SteeredShip()
        {
            var player = Player.m_localPlayer;
            return player != null ? player.GetControlledShip() : null;
        }

        private static bool OnShip(Player player, Ship ship) =>
            player != null && (player.GetStandingOnShip() == ship || player.GetControlledShip() == ship);

        // --- Wind ----------------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(EnvMan), nameof(EnvMan.GetWindDir))]
        [HarmonyPostfix]
        private static void WindDir(ref Vector3 __result)
        {
            if (!_tailwind.Value) return;
            var ship = SteeredShip();
            if (ship == null || (_tailwindSailUpOnly.Value && !ship.IsSailUp())) return;
            var forward = ship.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude > 0.0001f) __result = forward.normalized;
        }

        [HarmonyPatch(typeof(EnvMan), nameof(EnvMan.GetWindIntensity))]
        [HarmonyPostfix]
        private static void WindIntensity(ref float __result)
        {
            if (_minWind.Value <= 0f || SteeredShip() == null) return;
            __result = Mathf.Max(__result, _minWind.Value / 100f);
        }

        // --- Speed ---------------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(Ship), "GetSailForce")]
        [HarmonyPostfix]
        private static void SailForce(ref Vector3 __result) => __result *= _sailSpeed.Value;

        // Rowing force up and sail tilt down for the duration of the ship's physics step; also hides the sail.
        [HarmonyPatch(typeof(Ship), "CustomFixedUpdate")]
        [HarmonyPrefix]
        private static void ShipUpdatePrefix(Ship __instance, out Vector2 __state)
        {
            __state = new Vector2(__instance.m_backwardForce, __instance.m_sailForceOffset);
            __instance.m_backwardForce *= _rowSpeed.Value;
            __instance.m_sailForceOffset /= _sailSpeed.Value;
        }

        [HarmonyPatch(typeof(Ship), "CustomFixedUpdate")]
        [HarmonyFinalizer]
        private static void ShipUpdateFinalizer(Ship __instance, Vector2 __state)
        {
            __instance.m_backwardForce = __state.x;
            __instance.m_sailForceOffset = __state.y;
            if (_hideSail.Value || HiddenAny) UpdateSail(__instance);
        }

        // --- Sail visibility -----------------------------------------------------------------------------------

        private static bool HiddenAny;

        private static void UpdateSail(Ship ship)
        {
            var renderer = SailRenderers.GetValue(ship, s =>
            {
                var cloth = SailCloth?.GetValue(s) as Component; // read once per ship, then cached
                return cloth != null ? cloth.GetComponentInChildren<SkinnedMeshRenderer>(true) : null;
            });
            if (renderer == null) return;
            var hide = _hideSail.Value && ship.IsSailUp() && OnShip(Player.m_localPlayer, ship);
            if (renderer.enabled == hide) renderer.enabled = !hide;
            if (hide) HiddenAny = true;
        }

        // --- Waves ---------------------------------------------------------------------------------------------

        // Vanilla UpdateWaterForce without the damage: the splash effect still plays.
        [HarmonyPatch(typeof(Ship), "UpdateWaterForce")]
        [HarmonyPrefix]
        private static bool WaterForce(Ship __instance, float depth, float time)
        {
            if (!_noWaveDamage.Value) return true;
            var change = depth - LastDepth(__instance);
            var dt = time - LastWaterForceTime(__instance);
            LastDepth(__instance) = depth;
            LastWaterForceTime(__instance) = time;
            var speed = change / dt;
            if (speed <= 0f && Mathf.Abs(speed) > __instance.m_minWaterImpactForce &&
                time - LastImpactTime(__instance) > __instance.m_minWaterImpactInterval)
            {
                LastImpactTime(__instance) = time;
                __instance.m_waterImpactEffect.Create(__instance.transform.position, __instance.transform.rotation);
            }
            return false;
        }

        // --- Pushing -------------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(Character), "OnCollisionStay")]
        [HarmonyPostfix]
        private static void Push(Character __instance, Collision collision)
        {
            if (!_push.Value || __instance != Player.m_localPlayer) return;
            var body = collision.rigidbody;
            var ship = body != null ? body.GetComponent<Ship>() : null;
            if (ship == null || !ship.IsOwner()) return;
            var player = Player.m_localPlayer;
            if (OnShip(player, ship)) return;
            var move = player.GetMoveDir();
            move.y = 0f;
            var toShip = ship.transform.position - player.transform.position;
            toShip.y = 0f;
            if (move.sqrMagnitude < 0.01f || Vector3.Dot(move, toShip) <= 0f || collision.contactCount == 0) return;
            body.AddForceAtPosition(move.normalized * (_pushStrength.Value * body.mass), collision.GetContact(0).point, ForceMode.Force);
        }

        // --- Map -----------------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(Minimap), "UpdateExplore")]
        [HarmonyPrefix]
        private static void Explore(Minimap __instance, Player player, out float __state)
        {
            __state = __instance.m_exploreRadius;
            if (player != null && (player.GetStandingOnShip() != null || player.GetControlledShip() != null))
                __instance.m_exploreRadius *= _explore.Value;
        }

        [HarmonyPatch(typeof(Minimap), "UpdateExplore")]
        [HarmonyFinalizer]
        private static void ExploreDone(Minimap __instance, float __state) => __instance.m_exploreRadius = __state;
    }
}
