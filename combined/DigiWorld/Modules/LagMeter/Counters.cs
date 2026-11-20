// Lag Meter, Unity's own counters, read once a frame: how long the main thread, the render thread and the graphics card
// took, how long the main thread waited for the other two, and what the frame drew (draw calls, batches, material
// switches, triangles, shadow casters, animated meshes). These work in a release build (checked in game); a counter the
// game does not provide is left out. The values are the latest Unity has, which can be a frame or two behind.
using System.Collections.Generic;
using Unity.Profiling;

namespace LagMeter
{
    internal static class Counters
    {
        internal sealed class Counter
        {
            public string Key, Label;
            public bool Time; // nanoseconds, shown in ms
            public ProfilerRecorder Recorder;
            public double Sum, Max; // this second
        }

        internal static readonly List<Counter> All = new List<Counter>();
        private static readonly Dictionary<string, Counter> ByKey = new Dictionary<string, Counter>();

        internal static void Start()
        {
            if (All.Count > 0) return;
            Add("cpu", "main thread", ProfilerCategory.Render, "CPU Main Thread Frame Time", true);
            Add("render", "render thread", ProfilerCategory.Render, "CPU Render Thread Frame Time", true);
            Add("gpu", "graphics card", ProfilerCategory.Render, "GPU Frame Time", true);
            Add("waitRender", "main thread waits for render thread", ProfilerCategory.Render, "Gfx.WaitForRenderThread", true);
            Add("waitPresent", "render thread waits for graphics card", ProfilerCategory.Render, "Gfx.WaitForPresentOnGfxThread", true);
            Add("draws", "draw calls", ProfilerCategory.Render, "Draw Calls Count", false);
            Add("batches", "batches", ProfilerCategory.Render, "Batches Count", false);
            Add("setpass", "material switches", ProfilerCategory.Render, "SetPass Calls Count", false);
            Add("tris", "triangles", ProfilerCategory.Render, "Triangles Count", false);
            Add("shadowCasters", "shadow casters", ProfilerCategory.Render, "Shadow Casters Count", false);
            Add("skinned", "animated meshes", ProfilerCategory.Render, "Visible Skinned Meshes Count", false);
            Add("gcMemory", "managed memory", ProfilerCategory.Memory, "GC Used Memory", false);
            Add("memory", "memory in use", ProfilerCategory.Memory, "System Used Memory", false);
        }

        private static void Add(string key, string label, ProfilerCategory category, string name, bool time)
        {
            var recorder = ProfilerRecorder.StartNew(category, name, 1);
            if (!recorder.Valid)
            {
                recorder.Dispose();
                return;
            }
            var counter = new Counter { Key = key, Label = label, Time = time, Recorder = recorder };
            All.Add(counter);
            ByKey[key] = counter;
        }

        internal static void Stop()
        {
            foreach (var counter in All) counter.Recorder.Dispose();
            All.Clear();
            ByKey.Clear();
        }

        internal static void ReadFrame()
        {
            foreach (var counter in All)
            {
                double value = counter.Recorder.LastValue;
                if (counter.Time) value /= 1e6;
                counter.Sum += value;
                if (value > counter.Max) counter.Max = value;
            }
        }

        internal static void ResetSecond()
        {
            foreach (var counter in All) counter.Sum = counter.Max = 0;
        }

        // Average per frame over the frames since the last reset; -1 when the game has no such counter.
        internal static double Average(string key, int frames) =>
            ByKey.TryGetValue(key, out var counter) && frames > 0 ? counter.Sum / frames : -1;
    }
}
