// Unity cleans up unused memory whenever it sees fit, also in a fight or while you run, and with the memory of a big
// modded world each cleanup freezes the game for a moment. This keeps the automatic cleanup from starting and runs it
// at a quiet moment instead: a menu or the map open, sitting or in bed, teleporting, or standing still for a few seconds
// with no creature chasing you. The inventory is no quiet moment: it is where the frame rate is lowest already.
// If no quiet moment comes it runs anyway at a set amount of new memory, so memory use stays limited.
// The cleanup itself is the game's own incremental one, a slice per frame, so it can go on when you walk off.
// Not on PCs with little RAM (memory grows between cleanups) or on a dedicated server (nobody playing, no moments).
using System;
using System.Diagnostics;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Scripting;

namespace QuietCleanup
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.quietcleanup";
        public const string Name = "Digi's Quiet Cleanup";
        public const string Version = "1.0.0";

        private const ulong Slice = 3000000UL; // ns, the game's own (gc-max-time-slice=3 in its boot.config)
        private const long MB = 1048576L;
        private const int MinimumRam = 12288; // MB

        private static ConfigEntry<int> _atQuiet, _anyway;
        private static ConfigEntry<float> _stillSeconds;
        private bool _on, _running;
        private long _baseline, _startedAt;
        private float _timer, _stillTime, _worstFrame;
        private int _frames;
        private string _why;
        private readonly Stopwatch _sliceTime = new Stopwatch();

        private void Awake()
        {
            _atQuiet = Config.Bind("General", "Clean up at a quiet moment after (MB)", 300,
                new ConfigDescription("New memory in use since the last cleanup before a quiet moment is used for the next one.", new AcceptableValueRange<int>(50, 2000)));
            _anyway = Config.Bind("General", "Clean up anyway after (MB)", 2000,
                new ConfigDescription("New memory in use since the last cleanup at which it runs even if no quiet moment came.", new AcceptableValueRange<int>(500, 8000)));
            _stillSeconds = Config.Bind("General", "Standing still counts after (s)", 3f,
                new ConfigDescription("Standing still this long (with no creature chasing you) counts as a quiet moment.", new AcceptableValueRange<float>(1f, 30f)));
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || SystemInfo.systemMemorySize < MinimumRam)
            {
                Logger.LogInfo($"Not active on this PC ({SystemInfo.systemMemorySize} MB RAM, {SystemInfo.graphicsDeviceType}): the game cleans up by itself.");
                return;
            }
            _baseline = GC.GetTotalMemory(false);
            GarbageCollector.GCMode = GarbageCollector.Mode.Manual;
            _on = true;
            Logger.LogInfo($"Automatic cleanup held back, {_baseline / MB} MB in use.");
        }

        private void OnDestroy()
        {
            if (_on) GarbageCollector.GCMode = GarbageCollector.Mode.Enabled;
        }

        private void Update()
        {
            if (!_on) return;
            var player = Player.m_localPlayer;
            var still = player != null && player.GetVelocity().sqrMagnitude < 0.04f && !player.InAttack() && player.GetControlledShip() == null;
            _stillTime = still ? _stillTime + Time.unscaledDeltaTime : 0f;

            if (_running)
            {
                Step();
                return;
            }
            _timer += Time.unscaledDeltaTime;
            if (_timer < 0.5f) return;
            _timer = 0f;
            var used = GC.GetTotalMemory(false);
            var grown = (used - _baseline) / MB;
            if (grown < _atQuiet.Value) return;
            var why = grown >= _anyway.Value ? "limit" : QuietMoment(player);
            if (why == null) return;
            _why = why;
            _running = true;
            _startedAt = used;
            _worstFrame = 0f;
            _frames = 0;
            _sliceTime.Reset();
            Step();
        }

        private void Step()
        {
            _frames++;
            _worstFrame = Mathf.Max(_worstFrame, Time.unscaledDeltaTime);
            _sliceTime.Start();
            var more = GarbageCollector.CollectIncremental(Slice);
            _sliceTime.Stop();
            if (more) return;
            _running = false;
            _baseline = GC.GetTotalMemory(false);
            Logger.LogInfo($"Cleaned up ({_why}): {_startedAt / MB} to {_baseline / MB} MB in use, {_frames} frames, {_sliceTime.ElapsedMilliseconds} ms of work, slowest frame {_worstFrame * 1000f:F0} ms.");
        }

        private string QuietMoment(Player player)
        {
            if (player == null) return "nobody playing";
            if (player.IsTeleporting()) return "teleporting";
            if (BeingChased(player)) return null;
            if (Menu.IsVisible()) return "menu";
            if (Minimap.instance != null && Minimap.instance.m_mode == Minimap.MapMode.Large) return "map";
            if (InventoryGui.IsVisible() || StoreGui.IsVisible()) return null;
            if (player.InBed()) return "bed";
            if (player.IsSitting()) return "sitting";
            return _stillTime >= _stillSeconds.Value ? "standing still" : null;
        }

        private static bool BeingChased(Player player)
        {
            foreach (var ai in BaseAI.BaseAIInstances)
                if (ai != null && ai.GetTargetCreature() == player) return true;
            return false;
        }
    }
}
