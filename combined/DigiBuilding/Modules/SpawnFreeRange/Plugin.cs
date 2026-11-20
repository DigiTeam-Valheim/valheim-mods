// Spawn-Free Range: workbenches, fires and portals keep enemies from spawning further away (x2 by default).
// In vanilla many buildings carry a "player base" area that stops spawns: crafting stations a 20 m column, campfires
// 25 m, hearths and bonfires 30 m, torches and braziers 20 m, portals 20 m (balls). Before a creature spawns (world
// spawns and creature spawners) the game asks EffectArea.IsPointInsideArea(point, PlayerBase). When the game itself
// found no area, this checks the same areas again with their size times the slider of their kind. Portals use a
// column like a workbench, so a portal on a tower or a cliff also keeps spawns away below it.
// Only pieces a player built count: the fires and stations of fuling villages, fortresses and other world locations
// keep their vanilla range, or their own creature spawners would never spawn.
// Raids are not affected: how much counts as a base for raids is a separate count the game makes, left as it is.
// Lag: the areas are listed as they load (nothing runs per frame); the check only runs when the game found no area.
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SpawnFreeRange
{
    public class Plugin : DigiCore.DigiModule
    {
        private enum Kind { Station, Fire, Portal, Other }

        private sealed class Area
        {
            public EffectArea Effect;
            public Piece Piece; // its creator is read when the check runs: Piece.Awake may run after EffectArea.Awake
            public Kind Kind;
            public float Radius, HalfHeight; // HalfHeight > 0: a column (capsule); 0: a ball
        }

        private const float PortalHalfHeight = 100f; // like a workbench: 200 m high
        private static readonly ConfigEntry<float>[] Range = new ConfigEntry<float>[4];
        private static readonly List<Area> Areas = new List<Area>();
        private static readonly HashSet<EffectArea> Known = new HashSet<EffectArea>();
        private static GameObject _holder;
        private static EffectArea _baseAnswer; // a stand-in "player base" answer; never active, never in the world

        private void Awake()
        {
            Range[(int)Kind.Station] = Bind("Workbenches and crafting stations", 2f,
                "Workbench, forge, artisan table, black forge, galdr table and other crafting stations (20 m in vanilla).");
            Range[(int)Kind.Fire] = Bind("Campfires and fires", 2f,
                "Campfires (25 m in vanilla), hearths and bonfires (30 m), torches, braziers and other fires (mostly 20 m).");
            Range[(int)Kind.Portal] = Bind("Portals", 2f,
                "Portals (20 m in vanilla, as a ball). Here a column like a workbench, so nothing spawns below a portal on " +
                "a tower or a cliff either.");
            Range[(int)Kind.Other] = Bind("Other buildings", 1f,
                "Everything else with a spawn-free area: beds, smelters, kilns, ovens, guard stones and more (mostly 20 m).");

            _holder = new GameObject("Digi Spawn-Free Range");
            _holder.SetActive(false);
            DontDestroyOnLoad(_holder);
            _baseAnswer = _holder.AddComponent<EffectArea>();
            _baseAnswer.m_type = EffectArea.Type.PlayerBase;

            PatchModule(typeof(Plugin));
            foreach (var area in EffectArea.GetAllAreas()) Add(area);
        }

        private ConfigEntry<float> Bind(string key, float value, string what) =>
            Config.Bind("Range", key, value, new ConfigDescription(
                "How far enemies stay from spawning around these, times their vanilla range: 2 = twice as far, 1 = vanilla. " +
                what, new AcceptableValueRange<float>(1f, 5f)));

        private void OnDestroy()
        {
            Areas.Clear();
            Known.Clear();
            if (_holder != null) Destroy(_holder);
        }

        [HarmonyPatch(typeof(EffectArea), "Awake")]
        [HarmonyPostfix]
        private static void AreaLoaded(EffectArea __instance) => Add(__instance);

        private static void Add(EffectArea effect)
        {
            if (effect == null || (effect.m_type & EffectArea.Type.PlayerBase) == 0 || effect == _baseAnswer || Known.Contains(effect)) return;
            var collider = effect.GetComponent<Collider>();
            var scale = effect.transform.lossyScale.x;
            var area = new Area { Effect = effect, Piece = effect.GetComponentInParent<Piece>(), Kind = KindOf(effect) };
            if (collider is SphereCollider sphere) area.Radius = sphere.radius * scale;
            else if (collider is CapsuleCollider capsule)
            {
                area.Radius = capsule.radius * scale;
                area.HalfHeight = capsule.height * effect.transform.lossyScale.y / 2f;
            }
            else return; // other shapes: left to the game
            if (area.Kind == Kind.Portal) area.HalfHeight = PortalHalfHeight;
            if (area.Radius <= 0f) return;
            Areas.RemoveAll(a => a.Effect == null);
            Known.RemoveWhere(e => e == null);
            Known.Add(effect);
            Areas.Add(area);
        }

        private static Kind KindOf(EffectArea effect)
        {
            if (effect.GetComponentInParent<TeleportWorld>() != null) return Kind.Portal;
            if (effect.GetComponentInParent<CraftingStation>() != null) return Kind.Station;
            if (effect.GetComponentInParent<Fireplace>() != null) return Kind.Fire;
            return Kind.Other;
        }

        [HarmonyPatch(typeof(EffectArea), nameof(EffectArea.IsPointInsideArea))]
        [HarmonyPostfix]
        private static void Wider(Vector3 p, EffectArea.Type type, ref EffectArea __result)
        {
            if ((type & EffectArea.Type.PlayerBase) == 0 || Areas.Count == 0 || __result != null) return;
            for (var i = 0; i < Areas.Count; i++)
            {
                var area = Areas[i];
                var factor = Range[(int)area.Kind].Value;
                if (factor <= 1f && area.Kind != Kind.Portal) continue;
                // Like the game: a switched-off area does not count (a fire nobody built switches its area off).
                if (area.Effect == null || !area.Effect.gameObject.activeInHierarchy) continue;
                if (area.Piece == null || !area.Piece.IsPlacedByPlayer()) continue;
                var offset = area.Effect.transform.position - p;
                var radius = area.Radius * factor;
                var inside = area.HalfHeight > 0f
                    ? offset.x * offset.x + offset.z * offset.z <= radius * radius && Mathf.Abs(offset.y) <= area.HalfHeight
                    : offset.sqrMagnitude <= radius * radius;
                if (inside)
                {
                    __result = _baseAnswer;
                    return;
                }
            }
        }
    }
}
