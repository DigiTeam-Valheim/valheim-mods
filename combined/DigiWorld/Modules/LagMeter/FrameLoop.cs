// Lag Meter, frame loop: Unity runs every frame as a list of phases (TimeUpdate, Initialization, EarlyUpdate,
// FixedUpdate once per physics step, PreUpdate, Update, PreLateUpdate, PostLateUpdate), each a list of engine systems:
// scripts, physics, animation, particles, rendering, waiting for the graphics card, ... A marker goes before every
// system and after the last one of each phase, so the time between two markers is that system's main-thread time.
// Works in a release build and costs about 300 timestamp reads a frame. The first marker of the loop starts a frame.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine.LowLevel;

namespace LagMeter
{
    internal static class FrameLoop
    {
        private sealed class Marker { } // our systems in the loop (one type per loaded copy of this code)

        internal static readonly List<string> Names = new List<string>();  // "PostLateUpdate.FinishFrameRendering"
        internal static readonly List<string> Groups = new List<string>(); // "Rendering"
        internal static long[] Frame = new long[0]; // ticks per system in the current frame
        internal static int FixedSteps;             // physics steps in the current frame
        internal static Action<long> FrameEnded;    // called with the length of the frame that just ended
        internal static bool Installed;

        private static int _open = -1;
        private static long _openAt, _frameStart;

        internal static void Install()
        {
            if (Installed) return;
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            if (loop.subSystemList == null) return;
            Names.Clear();
            Groups.Clear();
            var phases = loop.subSystemList;
            for (var p = 0; p < phases.Length; p++)
            {
                var phase = phases[p];
                var phaseName = phase.type?.Name ?? "Phase" + p;
                var list = new List<PlayerLoopSystem>();
                foreach (var sub in phase.subSystemList ?? new PlayerLoopSystem[0])
                {
                    if (sub.type == typeof(Marker)) continue;
                    var name = sub.type?.Name ?? "?";
                    list.Add(Mark(Names.Count, p == 0 && list.Count == 0, phaseName == "FixedUpdate" && list.Count == 0));
                    Names.Add(phaseName + "." + name);
                    Groups.Add(GroupOf(name));
                    list.Add(sub);
                }
                list.Add(Mark(-1, false, false));
                phase.subSystemList = list.ToArray();
                phases[p] = phase;
            }
            loop.subSystemList = phases;
            Frame = new long[Names.Count];
            PlayerLoop.SetPlayerLoop(loop);
            Installed = true;
        }

        // Takes out only this copy's markers, whatever else changed the loop since.
        internal static void Uninstall()
        {
            if (!Installed) return;
            Installed = false;
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            var phases = loop.subSystemList;
            if (phases == null) return;
            for (var p = 0; p < phases.Length; p++)
                if (phases[p].subSystemList != null)
                    phases[p].subSystemList = phases[p].subSystemList.Where(s => s.type != typeof(Marker)).ToArray();
            loop.subSystemList = phases;
            PlayerLoop.SetPlayerLoop(loop);
            _open = -1;
            _frameStart = 0;
        }

        private static PlayerLoopSystem Mark(int slot, bool startsFrame, bool startsFixedStep) => new PlayerLoopSystem
        {
            type = typeof(Marker),
            updateDelegate = () =>
            {
                var now = Stopwatch.GetTimestamp();
                if (_open >= 0) Frame[_open] += now - _openAt;
                if (startsFrame)
                {
                    if (_frameStart != 0) FrameEnded?.Invoke(now - _frameStart);
                    _frameStart = now;
                    now = Stopwatch.GetTimestamp(); // our own work is not the next system's
                }
                if (startsFixedStep) FixedSteps++;
                _open = slot;
                _openAt = now;
            },
        };

        // What a system is for, to sum a frame up in a few lines.
        internal static string GroupOf(string system)
        {
            if (system == "WaitForLastPresentationAndUpdateTime" || system == "PresentAfterDraw") return "Waiting (graphics card, frame cap)";
            if (system.StartsWith("ScriptRun")) return "Scripts (game and mods)";
            if (system.StartsWith("Magica")) return "Cloth (capes)";
            if (system.Contains("Physics")) return "Physics";
            if (system.Contains("ParticleSystem") || system == "VFXUpdate") return "Particles";
            if (system.Contains("Canvas") || system.StartsWith("UIElements") || system == "UpdateRectTransform" || system.StartsWith("IMGUI") ||
                system == "GUIClearEvents") return "UI";
            if (system == "FinishFrameRendering" || system.Contains("Render") || system == "UpdateLightProbeProxyVolumes" ||
                system == "EnlightenRuntimeUpdate" || system == "UpdateCameraMotionVectors" || system == "PresentBeforeUpdate" ||
                system == "GpuTimestamp") return "Rendering (preparing the frame)";
            if (system.Contains("Animation") || system.StartsWith("Director") || system == "UpdateAllSkinnedMeshes" ||
                system == "ConstraintManagerUpdate") return "Animation";
            if (system.Contains("Preloading") || system.Contains("Async") || system.Contains("Streaming") || system == "ExecuteMainThreadJobs" ||
                system.Contains("ContentLoading") || system.Contains("Warmup")) return "Loading";
            if (system.StartsWith("AI")) return "Navigation";
            if (system.Contains("Audio")) return "Audio";
            return "Other engine";
        }
    }
}
