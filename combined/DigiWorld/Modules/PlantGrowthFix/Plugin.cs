// Vanilla bug: Plant.SUpdate only does its work while the game's slow-update loop comes back to the plant within 10 s
// (it returns when time > m_updateTime, and only a run pushes m_updateTime forward). The loop goes over every plant
// and static-physics object, 100 per frame, so in a big base with low FPS one pass can take longer than that. A plant
// that misses the window once never updates again: it stays a fully grown crop or sapling that cannot be picked,
// until the area reloads. Here a stuck plant gets its timer moved up to now, so it runs again on the next pass; a plant
// that is not stuck is left alone, so this costs one comparison per plant per pass.
using HarmonyLib;

namespace PlantGrowthFix
{
    public class Plugin : DigiCore.DigiModule
    {
        private static readonly AccessTools.FieldRef<Plant, float> UpdateTime = AccessTools.FieldRefAccess<Plant, float>("m_updateTime");
        private static BepInEx.Logging.ManualLogSource _log;
        private static int _unstuck;
        private static float _nextReport;

        private void Awake()
        {
            _log = Logger;
            PatchModule(typeof(Plugin));
        }

        [HarmonyPatch(typeof(Plant), nameof(Plant.SUpdate))]
        [HarmonyPrefix]
        private static void Unstick(Plant __instance, float time)
        {
            ref var next = ref UpdateTime(__instance);
            if (time <= next) return;
            if (next > 0f) _unstuck++; // 0 = Start has not run yet: not stuck, just new
            next = time;
            if (_unstuck > 0 && time > _nextReport)
            {
                _nextReport = time + 60f;
                _log.LogInfo($"Restarted stuck plants {_unstuck} time(s) (the game's update round took over 10 s, which stops a plant for good in vanilla).");
                _unstuck = 0;
            }
        }
    }
}
