// Farm Performance, a field growing up (and harvested). When you come back to a big field, its crops finish growing within seconds of
// each other (the game checks 100 plants per frame), and every one played the grow effect and wrote a line to the log
// ("Starting to grow plant with rotation ..."): a field of thousands stuttered for a second or two. Now per frame only
// the first few plants play the effect (a setting) and no plant writes the line; the crops grow the same.
using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;

namespace FarmPerformance
{
    internal static class Growth
    {
        private static ConfigEntry<int> _effectsPerFrame;
        private static readonly EffectList NoEffects = new EffectList();
        private static int _frame = -1, _grows;
        private static Plant _swapped;
        private static EffectList _saved;

        internal static void Bind(DigiCore.ModuleConfig config)
        {
            _effectsPerFrame = config.Bind("Growing", "Grow effects per frame", 6, new ConfigDescription(
                "When many crops finish growing at once (coming back to a big field), only this many per frame play the grow " +
                "effect; the rest grow without it. 0: no grow effects at all.", new AcceptableValueRange<int>(0, 100)));
        }

        [HarmonyPatch(typeof(Plant), nameof(Plant.Grow))]
        [HarmonyPrefix]
        private static void BeforeGrow(Plant __instance)
        {
            if (_frame != UnityEngine.Time.frameCount)
            {
                _frame = UnityEngine.Time.frameCount;
                _grows = 0;
            }
            if (++_grows <= _effectsPerFrame.Value || _swapped != null) return;
            _swapped = __instance;
            _saved = __instance.m_growEffect;
            __instance.m_growEffect = NoEffects;
        }

        [HarmonyPatch(typeof(Plant), nameof(Plant.Grow))]
        [HarmonyFinalizer]
        private static Exception AfterGrow(Exception __exception)
        {
            if (_swapped != null) _swapped.m_growEffect = _saved;
            _swapped = null;
            _saved = null;
            return __exception;
        }

        // The log line per plant goes (its text is still built, but not written); so does the "Bonus food picked!" line
        // per bonus crop, which a field harvested at once wrote hundreds of times in one frame.
        [HarmonyPatch(typeof(Plant), nameof(Plant.Grow))]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> NoGrowLine(IEnumerable<CodeInstruction> code) => NoLogLine(code);

        [HarmonyPatch(typeof(Pickable), nameof(Pickable.Interact))]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> NoBonusLine(IEnumerable<CodeInstruction> code) => NoLogLine(code);

        private static IEnumerable<CodeInstruction> NoLogLine(IEnumerable<CodeInstruction> code)
        {
            var log = AccessTools.Method(typeof(ZLog), nameof(ZLog.Log), new[] { typeof(object) });
            foreach (var instruction in code)
            {
                if (instruction.Calls(log)) yield return new CodeInstruction(OpCodes.Pop).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                else yield return instruction;
            }
        }
    }
}
