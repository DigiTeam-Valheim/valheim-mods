// Plant Anything, while plants grow: crops without sunlight or space when those rules are off, saplings indoors; the
// time until a plant is grown on its hover text; and plants placed "anywhere" (indoors, on floors) that never fall: with
// "Plant indoors" on, what you plant is marked so (Plant Everything's own mark, "pe_placeAnywhere", so its plants keep
// floating too), and a tree grown from a marked sapling is marked as well.
using HarmonyLib;
using UnityEngine;

namespace PlantAnything
{
    internal static class Growing
    {
        private static readonly int Anywhere = "pe_placeAnywhere".GetStableHashCode();
        private static Plant _growing;

        [HarmonyPatch(typeof(Plant), "HaveRoof")]
        [HarmonyPrefix]
        private static bool Roof(Plant __instance, ref bool __result)
        {
            if (!Plugin.Running) return true;
            var tree = Plantables.IsTree(__instance);
            if (tree ? !Plugin.Indoors.Value : Plugin.NeedSun.Value) return true;
            __result = false;
            return false;
        }

        [HarmonyPatch(typeof(Plant), "HaveGrowSpace")]
        [HarmonyPrefix]
        private static bool Space(Plant __instance, ref bool __result)
        {
            if (!Plugin.Running) return true;
            var tree = Plantables.IsTree(__instance);
            if (tree ? !Plugin.Indoors.Value : Plugin.NeedSpace.Value) return true;
            __result = true;
            return false;
        }

        // "Ready in 12 min" under a healthy crop or sapling.
        [HarmonyPatch(typeof(Plant), nameof(Plant.GetHoverText))]
        [HarmonyPostfix]
        private static void Hover(Plant __instance, ref string __result)
        {
            if (!Plugin.Running || !Plugin.ShowTime.Value || __instance.m_status != Plant.Status.Healthy || __instance.m_nview == null ||
                !__instance.m_nview.IsValid()) return;
            var left = __instance.GetGrowTime() - __instance.TimeSincePlanted();
            if (left > 0) __result += $"\n<color=#9be07a>Ready in {Duration(left)}</color>";
        }

        internal static string Duration(double seconds)
        {
            if (seconds < 60) return "less than a minute";
            var minutes = (int)System.Math.Ceiling(seconds / 60);
            return minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60} min";
        }

        // ---- never falling ----

        // A plant you place with "Plant indoors" on keeps its spot (on a floor, under a roof).
        [HarmonyPatch(typeof(Piece), nameof(Piece.SetCreator))]
        [HarmonyPostfix]
        private static void Placed(Piece __instance)
        {
            if (!Plugin.Running || !Plugin.Indoors.Value || __instance.m_nview == null || !__instance.m_nview.IsValid()) return;
            if (!Plantables.Removable(Utils.GetPrefabName(__instance.gameObject), out _)) return;
            var physics = __instance.GetComponent<StaticPhysics>();
            if (physics == null) return;
            physics.m_fall = false;
            __instance.m_nview.GetZDO().Set(Anywhere, true);
        }

        // Marked ones (also by Plant Everything) never fall when they load.
        [HarmonyPatch(typeof(StaticPhysics), nameof(StaticPhysics.Awake))]
        [HarmonyPostfix]
        private static void Loaded(StaticPhysics __instance)
        {
            if (!Plugin.Running || __instance.m_nview == null) return;
            var zdo = __instance.m_nview.GetZDO();
            if (zdo != null && zdo.GetBool(Anywhere)) __instance.m_fall = false;
        }

        // A tree grown from a marked sapling is marked too.
        [HarmonyPatch(typeof(Plant), "Grow")]
        [HarmonyPrefix]
        private static void Growing0(Plant __instance) => _growing = __instance;

        [HarmonyPatch(typeof(Plant), "Grow")]
        [HarmonyFinalizer]
        private static System.Exception Grown(System.Exception __exception)
        {
            _growing = null;
            return __exception;
        }

        [HarmonyPatch(typeof(TreeBase), nameof(TreeBase.Grow))]
        [HarmonyPostfix]
        private static void TreeGrown(TreeBase __instance)
        {
            var from = _growing;
            if (!Plugin.Running || from == null || from.m_nview == null || from.m_nview.GetZDO() == null || !from.m_nview.GetZDO().GetBool(Anywhere)) return;
            var physics = __instance.GetComponent<StaticPhysics>();
            if (physics != null) physics.m_fall = false;
            __instance.m_nview?.GetZDO()?.Set(Anywhere, true);
        }
    }
}
