// Lag Meter, sampling and reports. Every frame the frame loop, script timers and Unity counters are added up; every
// second that becomes one sample: FPS, slow frames, where the main thread's time went (per engine system and per
// script timer), what the render thread and graphics card took, what was drawn, and what was around the player.
// - Data file: one JSON line per second in BepInEx\LagMeter\, for analysis afterwards.
// - [LowFPS] in the log as soon as a second is below the threshold, again every few seconds while it lasts, and an
//   end line; it names what held the frame back and what was unusual compared with this session's good seconds.
// - [Spike] for single frames slower than the spike limit, with that frame's own breakdown.
// - [Lag] every interval and [Summary] every 5 minutes; [Mark] when the mark key is pressed (the last 10 seconds).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx.Logging;
using UnityEngine;

namespace LagMeter
{
    internal sealed class Context
    {
        public float X, Z, Day;
        public string Biome = "", Weather = "", Open = "";
        public bool Interior, Ship, Teleporting;
        public int Objects, Pieces, PiecesNear, Characters, Players, Ais, Smoke;
        // Counted every 10 s (they need a scene search), carried over in between.
        public int LightsOn = -1, ShadowLights = -1, ParticleSystems = -1, Particles = -1, BodiesAwake = -1, Plants = -1;
    }

    internal sealed class Sample
    {
        public float Time;
        public string Clock;
        public int Frames, Slow, GcRuns;
        public double Ms, Max, Low1, Steps, SelfMs, HeavyMs, CallsPerFrame;
        public double[] Slots;                                    // ms per frame, per frame-loop system
        public Dictionary<int, double> Timers = new Dictionary<int, double>(); // ms per frame
        public Dictionary<int, double> TimerCalls = new Dictionary<int, double>(); // calls per frame
        public Dictionary<string, double> Counters = new Dictionary<string, double>();
        public Context Ctx;
        public double Fps => Ms > 0 ? 1000.0 / Ms : 0;
    }

    internal static class Sampler
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly double TickMs = 1000.0 / Stopwatch.Frequency;

        internal static ManualLogSource Log;
        internal static Func<float> Threshold = () => 40f, SpikeMs = () => 100f, ReportEvery = () => 10f, Interval = () => 10f;
        internal static Func<bool> WriteFile = () => true;

        // This second.
        private static int _frames, _slow, _gc0;
        private static double _msSum, _msMax, _steps, _selfTicks;
        private static readonly List<double> FrameMs = new List<double>(256);
        private static long[] _slotSum = new long[0], _timerSum = new long[64], _callSum = new long[64];
        private static float _secondStart = -1f;
        private static int _lastGc;

        // History and state.
        private static readonly List<Sample> Recent = new List<Sample>();   // last 60 s
        private static readonly List<Sample> Good = new List<Sample>();     // up to 300 seconds at or above the threshold
        private static readonly List<Sample> Episode = new List<Sample>();  // since the last [LowFPS] report
        private static readonly List<Sample> SummaryWindow = new List<Sample>();
        private static float _episodeStart = -1f, _nextReport, _nextSpike, _nextInterval, _nextSummary, _nextHeavy;
        private static int _episodeSeconds, _spikesSkipped;
        private static double _episodeMin = double.MaxValue, _episodeFrames, _episodeMs;
        private static Context _lastHeavy = new Context();
        private static StreamWriter _file;
        private static int _linesSinceFlush;

        internal static void Start()
        {
            FrameLoop.FrameEnded = EndFrame;
            _secondStart = -1f;
        }

        internal static void Stop()
        {
            FrameLoop.FrameEnded = null;
            CloseFile();
        }

        private static void CloseFile()
        {
            try { _file?.Flush(); _file?.Dispose(); }
            catch (Exception) { }
            _file = null;
        }

        private static void EndFrame(long frameTicks)
        {
            var self = Stopwatch.GetTimestamp();
            try
            {
                if (Player.m_localPlayer == null || ZNet.instance == null)
                {
                    ResetSecond();
                    ClearFrame();
                    _secondStart = -1f;
                    if (_file != null) CloseFile();
                    return;
                }
                if (_secondStart < 0f) { _secondStart = Time.realtimeSinceStartup; ClearFrame(); ResetSecond(); return; }

                var ms = frameTicks * TickMs;
                var gc = GC.CollectionCount(0);
                var gcRan = gc != _lastGc;
                _lastGc = gc;
                if (ms > SpikeMs() && !Player.m_localPlayer.IsTeleporting()) Spike(ms, gcRan);

                _frames++;
                _msSum += ms;
                if (ms > _msMax) _msMax = ms;
                if (ms > 1000.0 / Threshold()) _slow++;
                FrameMs.Add(ms);
                _steps += FrameLoop.FixedSteps;
                if (_slotSum.Length != FrameLoop.Frame.Length) _slotSum = new long[FrameLoop.Frame.Length];
                for (var i = 0; i < FrameLoop.Frame.Length; i++) _slotSum[i] += FrameLoop.Frame[i];
                if (_timerSum.Length < Timers.Frame.Length) Array.Resize(ref _timerSum, Timers.Frame.Length);
                if (_callSum.Length < Timers.Calls.Length) Array.Resize(ref _callSum, Timers.Calls.Length);
                for (var i = 0; i < Timers.Frame.Length; i++) _timerSum[i] += Timers.Frame[i];
                for (var i = 0; i < Timers.Calls.Length; i++) _callSum[i] += Timers.Calls[i];
                Counters.ReadFrame();
                ClearFrame();

                var now = Time.realtimeSinceStartup;
                if (now - _secondStart >= 1f) EndSecond(now);
            }
            catch (Exception e)
            {
                Log?.LogWarning("Sampling failed: " + e.Message);
                ClearFrame();
            }
            _selfTicks += Stopwatch.GetTimestamp() - self;
        }

        private static void ClearFrame()
        {
            Array.Clear(FrameLoop.Frame, 0, FrameLoop.Frame.Length);
            Array.Clear(Timers.Frame, 0, Timers.Frame.Length);
            Array.Clear(Timers.Calls, 0, Timers.Calls.Length);
            FrameLoop.FixedSteps = 0;
        }

        private static void ResetSecond()
        {
            _frames = _slow = 0;
            _msSum = _msMax = _steps = _selfTicks = 0;
            FrameMs.Clear();
            Array.Clear(_slotSum, 0, _slotSum.Length);
            Array.Clear(_timerSum, 0, _timerSum.Length);
            Array.Clear(_callSum, 0, _callSum.Length);
            Counters.ResetSecond();
            _gc0 = GC.CollectionCount(0);
        }

        private static void EndSecond(float now)
        {
            var frames = Math.Max(1, _frames);
            var sample = new Sample
            {
                Time = now,
                Clock = DateTime.Now.ToString("HH:mm:ss", Inv),
                Frames = _frames,
                Slow = _slow,
                GcRuns = GC.CollectionCount(0) - _gc0,
                Ms = _msSum / frames,
                Max = _msMax,
                Steps = _steps / frames,
                Slots = new double[_slotSum.Length],
            };
            FrameMs.Sort();
            sample.Low1 = FrameMs.Count > 0 ? FrameMs[Math.Min(FrameMs.Count - 1, (int)(FrameMs.Count * 0.99))] : 0;
            for (var i = 0; i < _slotSum.Length; i++) sample.Slots[i] = _slotSum[i] * TickMs / frames;
            long calls = 0;
            for (var i = 0; i < _timerSum.Length && i < Timers.Names.Count; i++)
            {
                if (_timerSum[i] > 0) sample.Timers[i] = _timerSum[i] * TickMs / frames;
                if (i < _callSum.Length && _callSum[i] > 0) sample.TimerCalls[i] = (double)_callSum[i] / frames;
                if (i < _callSum.Length) calls += _callSum[i];
            }
            sample.CallsPerFrame = (double)calls / frames;
            foreach (var counter in Counters.All) sample.Counters[counter.Key] = counter.Sum / frames;

            var heavy = Stopwatch.GetTimestamp();
            var counting = now >= _nextHeavy;
            sample.Ctx = ReadContext(counting);
            if (counting) _nextHeavy = now + 10f;
            var contextTicks = Stopwatch.GetTimestamp() - heavy;
            if (counting) sample.HeavyMs = contextTicks * TickMs;
            _selfTicks += contextTicks;
            sample.SelfMs = _selfTicks * TickMs;

            Remember(Recent, sample, 60);
            Remember(SummaryWindow, sample, 300);
            var low = sample.Fps < Threshold() && !sample.Ctx.Teleporting;
            if (!low && !sample.Ctx.Teleporting) Remember(Good, sample, 300);
            Write(sample, low);
            LowFps(sample, low, now);
            if (now >= _nextInterval)
            {
                if (_nextInterval > 0f) Log.LogInfo(Line("[Lag]", Recent.Skip(Math.Max(0, Recent.Count - (int)Interval())).ToList(), false));
                _nextInterval = now + Interval();
            }
            if (now >= _nextSummary)
            {
                if (_nextSummary > 0f) Summary();
                _nextSummary = now + 300f;
            }
            ResetSecond();
            _secondStart = now;
        }

        private static void Remember(List<Sample> list, Sample sample, int max)
        {
            list.Add(sample);
            if (list.Count > max) list.RemoveAt(0);
        }

        // --- Context --------------------------------------------------------------------------------------------

        private static readonly System.Reflection.FieldInfo SceneInstances = HarmonyLib.AccessTools.Field(typeof(ZNetScene), "m_instances");

        private static Context ReadContext(bool heavy)
        {
            var player = Player.m_localPlayer;
            var pos = player.transform.position;
            var c = new Context
            {
                X = pos.x,
                Z = pos.z,
                Biome = player.GetCurrentBiome().ToString(),
                Interior = player.InInterior(),
                Ship = player.GetStandingOnShip() != null || player.GetControlledShip() != null,
                Teleporting = player.IsTeleporting(),
                Characters = Character.GetAllCharacters().Count,
                Players = Player.GetAllPlayers().Count,
                Ais = BaseAI.Instances.Count,
                Smoke = Smoke.Instances.Count,
                Objects = (SceneInstances?.GetValue(ZNetScene.instance) as System.Collections.ICollection)?.Count ?? -1,
            };
            if (EnvMan.instance != null)
            {
                c.Weather = EnvMan.instance.GetCurrentEnvironment()?.m_name ?? "";
                c.Day = EnvMan.instance.GetDayFraction();
            }
            var open = new List<string>();
            if (InventoryGui.IsVisible()) open.Add("inventory");
            if (Minimap.IsOpen()) open.Add("map");
            if (Menu.IsVisible()) open.Add("menu");
            c.Open = string.Join("+", open);
            var pieces = WearNTear.GetAllInstances();
            c.Pieces = pieces.Count;
            foreach (var piece in pieces)
                if (piece != null && (piece.transform.position - pos).sqrMagnitude < 40f * 40f) c.PiecesNear++;

            if (heavy)
            {
                var h = new Context { LightsOn = 0, ShadowLights = 0, ParticleSystems = 0, Particles = 0, BodiesAwake = 0, Plants = 0 };
                foreach (var light in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                {
                    if (!light.isActiveAndEnabled || light.type == LightType.Directional) continue;
                    h.LightsOn++;
                    if (light.shadows != LightShadows.None) h.ShadowLights++;
                }
                foreach (var system in UnityEngine.Object.FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None))
                {
                    var count = system.particleCount;
                    if (count <= 0) continue;
                    h.ParticleSystems++;
                    h.Particles += count;
                }
                foreach (var body in UnityEngine.Object.FindObjectsByType<Rigidbody>(FindObjectsSortMode.None))
                    if (!body.isKinematic && !body.IsSleeping()) h.BodiesAwake++;
                foreach (var s in SlowUpdate.GetAllInstaces())
                    if (s is Plant) h.Plants++;
                _lastHeavy = h;
            }
            c.LightsOn = _lastHeavy.LightsOn;
            c.ShadowLights = _lastHeavy.ShadowLights;
            c.ParticleSystems = _lastHeavy.ParticleSystems;
            c.Particles = _lastHeavy.Particles;
            c.BodiesAwake = _lastHeavy.BodiesAwake;
            c.Plants = _lastHeavy.Plants;
            return c;
        }

        // --- Data file ------------------------------------------------------------------------------------------

        private static void Write(Sample s, bool low)
        {
            if (!WriteFile()) { if (_file != null) CloseFile(); return; }
            try
            {
                if (_file == null)
                {
                    var dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "LagMeter");
                    Directory.CreateDirectory(dir);
                    var world = ZNet.instance != null ? ZNet.instance.GetWorldName() : "world";
                    var path = Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{string.Concat(world.Split(Path.GetInvalidFileNameChars()))}.jsonl");
                    _file = new StreamWriter(path, false, new UTF8Encoding(false));
                    var names = new StringBuilder("{\"type\":\"systems\",\"names\":[");
                    for (var i = 0; i < FrameLoop.Names.Count; i++)
                        names.Append(i > 0 ? "," : "").Append(Str(FrameLoop.Names[i]));
                    names.Append("],\"groups\":[");
                    for (var i = 0; i < FrameLoop.Groups.Count; i++)
                        names.Append(i > 0 ? "," : "").Append(Str(FrameLoop.Groups[i]));
                    _file.WriteLine(names.Append("],").Append(Settings()).Append('}').ToString());
                    Log.LogInfo($"Writing one line per second to {path}");
                }
                var b = new StringBuilder(2048);
                b.Append("{\"t\":").Append(N(s.Time, 1)).Append(",\"clock\":").Append(Str(s.Clock))
                    .Append(",\"fps\":").Append(N(s.Fps, 1)).Append(",\"ms\":").Append(N(s.Ms, 2)).Append(",\"max\":").Append(N(s.Max, 1))
                    .Append(",\"low1\":").Append(N(s.Low1, 1)).Append(",\"frames\":").Append(s.Frames).Append(",\"slow\":").Append(s.Slow)
                    .Append(",\"low\":").Append(low ? "true" : "false").Append(",\"steps\":").Append(N(s.Steps, 2))
                    .Append(",\"gc\":").Append(s.GcRuns).Append(",\"self\":").Append(N(s.SelfMs, 2)).Append(",\"selfCounts\":").Append(N(s.HeavyMs, 2))
                    .Append(",\"timedCalls\":").Append(N(s.CallsPerFrame, 0));
                foreach (var p in s.Counters) b.Append(",\"").Append(p.Key).Append("\":").Append(N(p.Value, p.Value > 100 ? 0 : 2));
                b.Append(",\"groups\":{");
                var first = true;
                foreach (var g in GroupTotals(s.Slots).OrderByDescending(g => g.Value))
                {
                    b.Append(first ? "" : ",").Append(Str(g.Key)).Append(':').Append(N(g.Value, 2));
                    first = false;
                }
                b.Append("},\"systems\":{");
                first = true;
                for (var i = 0; i < s.Slots.Length; i++)
                {
                    if (s.Slots[i] < 0.01) continue;
                    b.Append(first ? "" : ",").Append('"').Append(i).Append("\":").Append(N(s.Slots[i], 2));
                    first = false;
                }
                b.Append("},\"scripts\":{");
                first = true;
                foreach (var t in s.Timers.Where(t => t.Value >= 0.01))
                {
                    b.Append(first ? "" : ",").Append(Str(Timers.Names[t.Key])).Append(':').Append(N(t.Value, 2));
                    first = false;
                }
                var c = s.Ctx;
                b.Append("},\"scriptCalls\":{");
                first = true;
                foreach (var t in s.TimerCalls.Where(t => t.Value >= 0.5))
                {
                    b.Append(first ? "" : ",").Append(Str(Timers.Names[t.Key])).Append(':').Append(N(t.Value, 1));
                    first = false;
                }
                b.Append("},\"x\":").Append(N(c.X, 0)).Append(",\"z\":").Append(N(c.Z, 0)).Append(",\"biome\":").Append(Str(c.Biome))
                    .Append(",\"weather\":").Append(Str(c.Weather)).Append(",\"day\":").Append(N(c.Day, 3))
                    .Append(",\"interior\":").Append(c.Interior ? "true" : "false").Append(",\"ship\":").Append(c.Ship ? "true" : "false")
                    .Append(",\"teleporting\":").Append(c.Teleporting ? "true" : "false").Append(",\"open\":").Append(Str(c.Open))
                    .Append(",\"objects\":").Append(c.Objects).Append(",\"pieces\":").Append(c.Pieces).Append(",\"piecesNear\":").Append(c.PiecesNear)
                    .Append(",\"characters\":").Append(c.Characters).Append(",\"players\":").Append(c.Players).Append(",\"ais\":").Append(c.Ais)
                    .Append(",\"smoke\":").Append(c.Smoke).Append(",\"lights\":").Append(c.LightsOn).Append(",\"shadowLights\":").Append(c.ShadowLights)
                    .Append(",\"particleSystems\":").Append(c.ParticleSystems).Append(",\"particles\":").Append(c.Particles)
                    .Append(",\"bodiesAwake\":").Append(c.BodiesAwake).Append(",\"plants\":").Append(c.Plants).Append('}');
                _file.WriteLine(b.ToString());
                if (++_linesSinceFlush >= 10) { _file.Flush(); _linesSinceFlush = 0; }
            }
            catch (Exception e)
            {
                Log.LogWarning("Data file: " + e.Message);
                CloseFile();
            }
        }

        // The settings that decide how much there is to draw, once per file.
        private static string Settings()
        {
            var q = QualitySettings.GetQualityLevel();
            return "\"settings\":{" +
                   $"\"quality\":{Str(QualitySettings.names[q])},\"vsync\":{QualitySettings.vSyncCount},\"targetFps\":{Application.targetFrameRate}," +
                   $"\"shadows\":{Str(QualitySettings.shadows.ToString())},\"shadowResolution\":{Str(QualitySettings.shadowResolution.ToString())}," +
                   $"\"shadowDistance\":{N(QualitySettings.shadowDistance, 0)},\"shadowCascades\":{QualitySettings.shadowCascades}," +
                   $"\"lodBias\":{N(QualitySettings.lodBias, 2)},\"pixelLights\":{QualitySettings.pixelLightCount},\"antiAliasing\":{QualitySettings.antiAliasing}," +
                   $"\"screen\":{Str(Screen.width + "x" + Screen.height + " " + Screen.fullScreenMode)}," +
                   $"\"gpu\":{Str(SystemInfo.graphicsDeviceName + " " + SystemInfo.graphicsDeviceType)},\"cpu\":{Str(SystemInfo.processorType + " x" + SystemInfo.processorCount)}," +
                   $"\"ram\":{SystemInfo.systemMemorySize},\"fixedStep\":{N(Time.fixedDeltaTime, 4)}}}";
        }

        private static string N(double value, int decimals) => Math.Round(value, decimals).ToString(Inv);

        private static string Str(string text) =>
            "\"" + (text ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        // --- Reports ----------------------------------------------------------------------------------------------

        private static Dictionary<string, double> GroupTotals(double[] slots)
        {
            var groups = new Dictionary<string, double>();
            for (var i = 0; i < slots.Length && i < FrameLoop.Groups.Count; i++)
            {
                groups.TryGetValue(FrameLoop.Groups[i], out var sum);
                groups[FrameLoop.Groups[i]] = sum + slots[i];
            }
            return groups;
        }

        private static void LowFps(Sample sample, bool low, float now)
        {
            if (low)
            {
                if (_episodeStart < 0f)
                {
                    _episodeStart = now;
                    _episodeSeconds = 0;
                    _episodeMin = double.MaxValue;
                    _episodeFrames = _episodeMs = 0;
                    _nextReport = now; // the first low second is reported at once
                }
                _episodeSeconds++;
                _episodeFrames += sample.Frames;
                _episodeMs += sample.Ms * sample.Frames;
                if (sample.Fps < _episodeMin) _episodeMin = sample.Fps;
                Episode.Add(sample);
                if (now >= _nextReport)
                {
                    Log.LogWarning(Line($"[LowFPS] {(Episode.Count == _episodeSeconds ? "start" : "still")}:", Episode, true));
                    Episode.Clear();
                    _nextReport = now + ReportEvery();
                }
            }
            else if (_episodeStart >= 0f)
            {
                if (Episode.Count > 0 && _episodeSeconds > 1) Log.LogWarning(Line("[LowFPS] last part:", Episode, true));
                var avg = _episodeMs > 0 ? 1000.0 * _episodeFrames / _episodeMs : 0;
                Log.LogWarning($"[LowFPS] end: {_episodeSeconds} s below {Threshold():0} fps (average {avg:0.0}, lowest second {_episodeMin:0.0}), " +
                               $"now {sample.Fps:0.0} fps");
                Episode.Clear();
                _episodeStart = -1f;
            }
        }

        internal static void Mark()
        {
            if (Recent.Count == 0) return;
            Log.LogWarning(Line("[Mark] (you pressed the mark key) last 10 s:", Recent.Skip(Math.Max(0, Recent.Count - 10)).ToList(), true));
            if (_file != null)
            {
                try { _file.WriteLine("{\"type\":\"mark\",\"t\":" + N(Time.realtimeSinceStartup, 1) + "}"); }
                catch (Exception) { }
            }
        }

        private static void Summary()
        {
            var all = SummaryWindow.Where(s => !s.Ctx.Teleporting).ToList();
            if (all.Count == 0) return;
            var slow = all.Where(s => s.Fps < Threshold()).ToList();
            var fast = all.Where(s => s.Fps >= Threshold()).ToList();
            var sb = new StringBuilder($"[Summary] last {all.Count / 60.0:0} min: {100.0 * fast.Count / all.Count:0}% of the time at {Threshold():0}+ fps, " +
                                       $"average {Average(all, s => s.Fps):0.0} fps");
            if (slow.Count > 0) sb.Append("\n      slow seconds: ").Append(Breakdown(slow));
            if (fast.Count > 0) sb.Append("\n      good seconds: ").Append(Breakdown(fast));
            Log.LogInfo(sb.ToString());
        }

        private static string Breakdown(List<Sample> samples)
        {
            var groups = Merge(samples).Item1;
            return $"{Average(samples, s => s.Fps):0.0} fps, main thread busy {Busy(groups):0.0} ms, graphics card {Avg(samples, "gpu"):0.0} ms; " +
                   string.Join(", ", groups.Where(g => g.Value >= 0.5).OrderByDescending(g => g.Value).Select(g => $"{g.Key} {g.Value:0.0}"));
        }

        private static double Average(List<Sample> samples, Func<Sample, double> value) =>
            samples.Count == 0 ? 0 : samples.Sum(s => value(s) * s.Frames) / Math.Max(1, samples.Sum(s => s.Frames));

        private static double Avg(List<Sample> samples, string counter) =>
            Average(samples, s => s.Counters.TryGetValue(counter, out var v) ? v : 0);

        private static double Median(List<Sample> samples, Func<Sample, double> value)
        {
            if (samples.Count == 0) return -1;
            var values = samples.Select(value).OrderBy(v => v).ToList();
            return values[values.Count / 2];
        }

        private const string Waiting = "Waiting (graphics card, frame cap)";

        private static double Busy(Dictionary<string, double> groups) =>
            groups.Where(g => g.Key != Waiting).Sum(g => g.Value);

        // Frame-weighted averages of a list of seconds: per group, per system, per script timer.
        private static Tuple<Dictionary<string, double>, double[], Dictionary<int, double>> Merge(List<Sample> samples)
        {
            var frames = Math.Max(1, samples.Sum(s => s.Frames));
            var slots = new double[FrameLoop.Names.Count];
            var timers = new Dictionary<int, double>();
            foreach (var s in samples)
            {
                for (var i = 0; i < s.Slots.Length && i < slots.Length; i++) slots[i] += s.Slots[i] * s.Frames;
                foreach (var t in s.Timers)
                {
                    timers.TryGetValue(t.Key, out var sum);
                    timers[t.Key] = sum + t.Value * s.Frames;
                }
            }
            for (var i = 0; i < slots.Length; i++) slots[i] /= frames;
            foreach (var key in timers.Keys.ToList()) timers[key] /= frames;
            return Tuple.Create(GroupTotals(slots), slots, timers);
        }

        // One report over a list of seconds: what it was like, what held the frame back, what took the time, and what
        // was unusual compared with the good seconds of this session.
        private static string Line(string title, List<Sample> samples, bool detail)
        {
            if (samples.Count == 0) return title + " no data";
            var merged = Merge(samples);
            var groups = merged.Item1;
            var slots = merged.Item2;
            var timers = merged.Item3;
            var last = samples[samples.Count - 1].Ctx;
            var fps = Average(samples, s => s.Fps);
            var low1 = samples.Max(s => s.Low1);
            var worst = samples.Max(s => s.Max);
            var busy = Busy(groups);
            double cpu = Avg(samples, "cpu"), render = Avg(samples, "render"), gpu = Avg(samples, "gpu");
            var steps = Average(samples, s => s.Steps);
            var sb = new StringBuilder();
            var where = $"({last.X:0}, {last.Z:0}) {last.Biome}" + (last.Interior ? ", inside a dungeon" : "") + (last.Ship ? ", on a ship" : "") +
                        (last.PiecesNear > 0 ? $", {last.PiecesNear} building pieces within 40 m" : "") + $", {last.Weather}, day {last.Day:0.00}" +
                        (last.Open.Length > 0 ? $", open: {last.Open}" : "");
            sb.Append($"{title} {fps:0.0} fps over {samples.Count} s (slowest frames {low1:0} ms, worst {worst:0} ms) at {where}");

            // Who held the frame back.
            var budget = 1000.0 / Threshold();
            string heldBy;
            if (gpu > 0 && gpu >= busy * 0.9 && gpu > budget * 0.8) heldBy = "the graphics card";
            else if (render > 0 && render >= busy * 0.9 && render > budget * 0.8) heldBy = "the render thread (preparing draw calls)";
            else heldBy = "the main thread (processor)";
            sb.Append($"\n      frame {Average(samples, s => s.Ms):0.0} ms: main thread busy {busy:0.0}, render thread {render:0.0}, graphics card {gpu:0.0}" +
                      (cpu > 0 ? $" (Unity: main {cpu:0.0})" : "") + $" -> held back by {heldBy}");

            // Main thread by group, with the biggest system of each.
            var parts = new List<string>();
            foreach (var g in groups.Where(g => g.Value >= 0.3).OrderByDescending(g => g.Value))
            {
                var top = Enumerable.Range(0, slots.Length).Where(i => FrameLoop.Groups[i] == g.Key && slots[i] >= 0.3)
                    .OrderByDescending(i => slots[i]).Take(2)
                    .Select(i => $"{FrameLoop.Names[i].Substring(FrameLoop.Names[i].IndexOf('.') + 1)} {slots[i]:0.0}").ToList();
                var extra = g.Key == "Physics" ? $", {steps:0.0} steps/frame" : "";
                parts.Add($"{g.Key} {g.Value:0.0}" + (top.Count > 0 || extra.Length > 0 ? $" ({string.Join(", ", top)}{extra})" : ""));
            }
            sb.Append("\n      main thread: ").Append(string.Join(", ", parts));
            var scripts = timers.Where(t => t.Value >= 0.2).OrderByDescending(t => t.Value).Take(detail ? 12 : 8)
                .Select(t => $"{Timers.Names[t.Key]} {t.Value:0.0}");
            sb.Append("\n      scripts: ").Append(string.Join(", ", scripts));

            // What was drawn and what was around, against this session's good seconds.
            var usual = new List<string>();
            void Compare(string label, double now, Func<Sample, double> of, string format = "0")
            {
                if (now < 0) return;
                var normal = Median(Good, of);
                var text = $"{label} {now.ToString(format, Inv)}";
                if (normal > 0 && Good.Count >= 20 && now > normal * 1.3 && now - normal >= 2) text += $" (usual {normal.ToString(format, Inv)})";
                usual.Add(text);
            }
            Compare("draw calls", Avg(samples, "draws"), s => C(s, "draws"));
            Compare("material switches", Avg(samples, "setpass"), s => C(s, "setpass"));
            Compare("shadow casters", Avg(samples, "shadowCasters"), s => C(s, "shadowCasters"));
            Compare("triangles (M)", Avg(samples, "tris") / 1e6, s => C(s, "tris") / 1e6, "0.0");
            Compare("animated meshes", Avg(samples, "skinned"), s => C(s, "skinned"));
            Compare("creatures", last.Characters, s => s.Ctx.Characters);
            Compare("AIs", last.Ais, s => s.Ctx.Ais);
            Compare("building pieces near", last.PiecesNear, s => s.Ctx.PiecesNear);
            Compare("objects loaded", last.Objects, s => s.Ctx.Objects);
            Compare("lights", last.LightsOn, s => s.Ctx.LightsOn);
            Compare("shadow lights", last.ShadowLights, s => s.Ctx.ShadowLights);
            Compare("particles", last.Particles, s => s.Ctx.Particles);
            Compare("moving bodies", last.BodiesAwake, s => s.Ctx.BodiesAwake);
            Compare("smoke puffs", last.Smoke, s => s.Ctx.Smoke);
            var gcRuns = samples.Sum(s => s.GcRuns);
            sb.Append("\n      drawn and around: ").Append(string.Join(", ", usual))
                .Append($", garbage collections {gcRuns}, managed memory {Avg(samples, "gcMemory") / 1048576:0} MB");
            if (detail)
                sb.Append($"; Lag Meter itself {samples.Average(s => s.SelfMs):0.0} ms/s (scene counts every 10 s: {samples.Max(s => s.HeavyMs):0} ms), " +
                          $"{samples.Average(s => s.CallsPerFrame):0} timed calls/frame");
            return sb.ToString();
        }

        private static double C(Sample s, string key) => s.Counters.TryGetValue(key, out var v) ? v : 0;

        // A single slow frame: its own biggest systems and script timers.
        private static void Spike(double ms, bool gcRan)
        {
            var now = Time.realtimeSinceStartup;
            if (now < _nextSpike) { _spikesSkipped++; return; }
            _nextSpike = now + 2f;
            var tick = TickMs;
            var systems = Enumerable.Range(0, FrameLoop.Frame.Length).Where(i => FrameLoop.Frame[i] * tick >= 2.0)
                .OrderByDescending(i => FrameLoop.Frame[i]).Take(6)
                .Select(i => $"{FrameLoop.Names[i]} {FrameLoop.Frame[i] * tick:0}");
            var scripts = Enumerable.Range(0, Math.Min(Timers.Frame.Length, Timers.Names.Count)).Where(i => Timers.Frame[i] * tick >= 2.0)
                .OrderByDescending(i => Timers.Frame[i]).Take(6)
                .Select(i => $"{Timers.Names[i]} {Timers.Frame[i] * tick:0}");
            var pos = Player.m_localPlayer.transform.position;
            Log.LogWarning($"[Spike] {ms:0} ms frame at ({pos.x:0}, {pos.z:0}): {string.Join(", ", systems)}" +
                           (FrameLoop.FixedSteps > 1 ? $" ({FrameLoop.FixedSteps} physics steps)" : "") +
                           $"; scripts: {string.Join(", ", scripts)}" + (gcRan ? "; a garbage collection ran" : "") +
                           (_spikesSkipped > 0 ? $" ({_spikesSkipped} more spikes since the last line)" : ""));
            _spikesSkipped = 0;
        }
    }
}
