// Keep Rested, rest speed: while you rest (sitting or sheltered by a fire) the game counts a delay before Rested
// starts ("Resting", SE_Cozy.m_delay). A slider divides that delay, so Rested comes sooner; 2 = twice as fast.
// Set once each time resting starts (on the game's own copy of the effect); nothing runs while you rest.
using BepInEx.Configuration;
using HarmonyLib;

namespace KeepRested
{
    internal static class RestSpeed
    {
        private static ConfigEntry<float> _speed;

        internal static void Bind(DigiCore.ModuleConfig config)
        {
            _speed = config.Bind("Resting", "Rest speed", 2f, new ConfigDescription(
                "How much faster resting turns into Rested: 2 = twice as fast (half the wait), 1 = normal. The Rested " +
                "buff itself (its length and comfort) is not changed.", new AcceptableValueRange<float>(1f, 10f)));
        }

        // Each time you start resting, the game makes its own copy of the Resting effect: shorten that copy's wait.
        [HarmonyPatch(typeof(SE_Cozy), nameof(SE_Cozy.Setup))]
        [HarmonyPostfix]
        private static void Faster(SE_Cozy __instance)
        {
            if (_speed.Value > 1f) __instance.m_delay /= _speed.Value;
        }
    }
}
