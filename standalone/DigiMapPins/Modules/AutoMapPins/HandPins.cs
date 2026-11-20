// Auto Map Pins, your own pins: a button in F1 removes every pin placed by hand (the map's own pin icons, yours and
// those read from cartography tables), after a second click within 5 seconds. What it removed is kept in
// DigiAutoMapPins/<world>-<id>.<character>.hand-pins.txt, and "Bring back" puts those pins back. Death markers, the
// bed, vegvisir boss pins and this mod's own pins are never touched.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace AutoMapPins
{
    internal static class HandPins
    {
        private static readonly HashSet<Minimap.PinType> Types = new HashSet<Minimap.PinType>
        {
            Minimap.PinType.Icon0, Minimap.PinType.Icon1, Minimap.PinType.Icon2, Minimap.PinType.Icon3, Minimap.PinType.Icon4,
        };

        private static float _armedUntil, _countedAt = -10f;
        private static int _count;

        private static string BackupPath => Records.Key == null ? null : Path.Combine(Records.Folder, Records.Key + ".hand-pins.txt");

        // This mod's pins are not saved by the game (only lent to a cartography table for a moment), so saved = by hand.
        private static List<Minimap.PinData> Find() =>
            MapPins.GamePins()?.Where(pin => pin.m_save && Types.Contains(pin.m_type)).ToList() ?? new List<Minimap.PinData>();

        // F1: "Remove all (N)", which asks once more, and "Bring back".
        internal static void Draw(ConfigEntryBase entry)
        {
            GUILayout.BeginHorizontal();
            if (Minimap.instance == null || Records.Key == null)
            {
                GUILayout.Label("Load a world first.");
                GUILayout.EndHorizontal();
                return;
            }
            if (Time.unscaledTime - _countedAt > 1f)
            {
                _count = Find().Count;
                _countedAt = Time.unscaledTime;
            }
            var armed = Time.unscaledTime < _armedUntil;
            var enabled = GUI.enabled;
            GUI.enabled = enabled && _count > 0;
            if (GUILayout.Button(armed ? $"Sure? Click again to remove {_count}" : $"Remove all ({_count})"))
            {
                if (armed) Remove();
                else _armedUntil = Time.unscaledTime + 5f;
            }
            GUI.enabled = enabled && File.Exists(BackupPath);
            if (GUILayout.Button("Bring back", GUILayout.ExpandWidth(false))) Restore();
            GUI.enabled = enabled;
            GUILayout.EndHorizontal();
        }

        private static void Remove()
        {
            _armedUntil = 0f;
            _countedAt = -10f;
            var pins = Find();
            var c = CultureInfo.InvariantCulture;
            try
            {
                // Added to what an earlier removal kept, so everything removed comes back.
                File.AppendAllLines(BackupPath, pins.Select(pin => string.Join("\t", pin.m_type.ToString(), pin.m_pos.x.ToString(c),
                    pin.m_pos.y.ToString(c), pin.m_pos.z.ToString(c), pin.m_checked ? "1" : "0", pin.m_ownerID.ToString(c),
                    (pin.m_name ?? "").Replace('\t', ' ').Replace('\n', ' '))));
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("Hand-placed pins not removed, could not keep a copy: " + e.Message);
                return;
            }
            foreach (var pin in pins) Minimap.instance.RemovePin(pin);
            Plugin.Log?.LogInfo($"Removed {pins.Count} hand-placed pins (a copy is in {Path.GetFileName(BackupPath)}).");
        }

        private static void Restore()
        {
            _countedAt = -10f;
            var path = BackupPath;
            var c = CultureInfo.InvariantCulture;
            var existing = Find();
            var back = 0;
            foreach (var line in File.ReadAllLines(path))
            {
                var f = line.Split('\t');
                if (f.Length < 7 || !Enum.TryParse(f[0], out Minimap.PinType type) ||
                    !float.TryParse(f[1], NumberStyles.Float, c, out var x) || !float.TryParse(f[2], NumberStyles.Float, c, out var y) ||
                    !float.TryParse(f[3], NumberStyles.Float, c, out var z) || !long.TryParse(f[5], NumberStyles.Integer, c, out var owner)) continue;
                var pos = new Vector3(x, y, z);
                if (existing.Any(pin => pin.m_type == type && pin.m_name == f[6] && Utils.DistanceXZ(pin.m_pos, pos) < 0.5f)) continue;
                Minimap.instance.AddPin(pos, type, f[6], true, f[4] == "1", owner);
                back++;
            }
            File.Delete(path);
            Plugin.Log?.LogInfo($"Brought back {back} hand-placed pins.");
        }
    }
}
