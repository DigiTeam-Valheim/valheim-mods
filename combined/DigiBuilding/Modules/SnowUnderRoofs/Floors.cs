// Snow Under Roofs, floors: the Deep North's deep snow belongs to the terrain, and the game asks the terrain under you how
// deep it is, also while you stand on a floor of your building above it. So on a floor over a deep-snow spot:
// - every landing (a jump, a step down) threw up the deep snow cloud: Character.UpdateGroundContact spawns
//   m_deepSnowWalkObj on landing without asking what you stand on (walking checks IsOnHeightmap, landing does not);
// - you walked slowed as if wading: Character.UpdateWalking compares your height with terrain height + snow depth;
// - a footstep where the terrain reaches the floor's top puffed and sounded like snow (FootStep.GetGroundMaterial asks
//   the terrain when that is what your feet touched last).
// Standing on a building piece (found right under the feet), none of this happens and footsteps sound like the piece
// (wood, stone, metal, ice), as on any floor elsewhere. In the snow itself everything stays as the game made it.
// Light: only in the Deep North, one short ray down per update of a character this game runs, and per footstep on snow.
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SnowUnderRoofs
{
    internal static class Floors
    {
        internal static ConfigEntry<bool> On;
        private static int _mask;
        private static readonly AccessTools.FieldRef<Character, Heightmap.Biome> LastBiome =
            AccessTools.FieldRefAccess<Character, Heightmap.Biome>("m_lastBiome");

        internal static void Bind(DigiCore.ModuleConfig config)
        {
            On = config.Bind("General", "No deep snow on floors", true,
                "In the Deep North, standing on a floor (or stairs, a beam, any building piece) over deep snow no longer throws " +
                "up snow clouds when you land or step, no longer slows you as if wading, and footsteps sound like the floor. " +
                "The game measured the snow of the terrain under the floor.");
            _mask = LayerMask.GetMask("piece");
        }

        // A building piece right under the feet (from 30 cm above to 50 cm below them).
        private static WearNTear PieceUnder(Vector3 feet)
        {
            if (!Physics.Raycast(feet + Vector3.up * 0.3f, Vector3.down, out var hit, 0.8f, _mask, QueryTriggerInteraction.Ignore)) return null;
            return hit.collider != null ? hit.collider.GetComponentInParent<WearNTear>() : null;
        }

        // Both the deep snow slow and the snow cloud (walking and landing) need m_deepSnowSlowMax above 0: it is 0 while the
        // game updates a character standing on a building, and put back right after (the two methods never run inside
        // each other, so one field holds the value).
        private static Character _hidden;
        private static float _was;

        private static void Hide(Character c)
        {
            Show();
            if (!On.Value || c == null || c.m_deepSnowSlowMax == 0f || LastBiome(c) != Heightmap.Biome.DeepNorth) return;
            if (PieceUnder(c.transform.position) == null) return;
            _hidden = c;
            _was = c.m_deepSnowSlowMax;
            c.m_deepSnowSlowMax = 0f;
        }

        private static void Show()
        {
            if (_hidden != null) _hidden.m_deepSnowSlowMax = _was;
            _hidden = null;
        }

        [HarmonyPatch(typeof(Character), "UpdateWalking")]
        [HarmonyPrefix]
        private static void BeforeWalking(Character __instance) => Hide(__instance);

        [HarmonyPatch(typeof(Character), "UpdateWalking")]
        [HarmonyFinalizer]
        private static void AfterWalking() => Show();

        [HarmonyPatch(typeof(Character), "UpdateGroundContact")]
        [HarmonyPrefix]
        private static void BeforeContact(Character __instance) => Hide(__instance);

        [HarmonyPatch(typeof(Character), "UpdateGroundContact")]
        [HarmonyFinalizer]
        private static void AfterContact() => Show();

        // A snowy footstep on a building piece: the piece's own sound and dust, as the game picks for a floor.
        [HarmonyPatch(typeof(FootStep), "GetGroundMaterial")]
        [HarmonyPostfix]
        private static void Footstep(Vector3 point, ref FootStep.GroundMaterial __result)
        {
            if (!On.Value || (__result != FootStep.GroundMaterial.Snow && __result != FootStep.GroundMaterial.SnowDeep &&
                              __result != FootStep.GroundMaterial.SnowVeryDeep)) return;
            var piece = PieceUnder(point);
            if (piece == null) return;
            switch (piece.m_materialType)
            {
                case WearNTear.MaterialType.Wood:
                case WearNTear.MaterialType.HardWood:
                    __result = FootStep.GroundMaterial.Wood;
                    break;
                case WearNTear.MaterialType.Stone:
                case WearNTear.MaterialType.Marble:
                case WearNTear.MaterialType.Ashstone:
                case WearNTear.MaterialType.Ancient:
                    __result = FootStep.GroundMaterial.Stone;
                    break;
                case WearNTear.MaterialType.Iron:
                    __result = FootStep.GroundMaterial.Metal;
                    break;
                case WearNTear.MaterialType.Ice:
                    __result = FootStep.GroundMaterial.Ice;
                    break;
                default:
                    __result = FootStep.GroundMaterial.Default;
                    break;
            }
        }
    }
}
