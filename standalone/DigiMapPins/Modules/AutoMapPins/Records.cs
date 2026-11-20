// Auto Map Pins, records: what was found where, saved per world and character in
// BepInEx/config/DigiAutoMapPins/<world>-<id>.<character>.txt (one line each, written beside and then swapped in, so a
// crash never leaves half a file). A record holds the objects behind its pin (several bushes or deposits close together
// share one pin), whether you deleted or crossed it off, and until when it is faded (picked, regrowing) or whether it
// is emptied (a looted chest). Looked up through a grid of 64 m cells, so nearby checks stay cheap with thousands.
// The objects are only known for this session: Valheim numbers its objects anew every time a world loads (an old number
// can then belong to anything), so they are not saved; each record finds its objects again by position as they load.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace AutoMapPins
{
    internal sealed class Record
    {
        public string Thing;
        public Heightmap.Biome Biome;
        public Vector3 Pos;
        public int Count = 1;
        public bool Deleted, Checked, Emptied;
        public long FadedUntil;     // world time (ticks) until which a picked plant regrows
        public string Label;        // own label (portal tag, dungeon name), else the thing's
        public string Icon;         // own icon item (what this spawner drops when it differs from its kind's), else the thing's
        public readonly List<ZDOID> Members = new List<ZDOID>();
        public float UnseenFor;     // seconds nearby without any of its objects (records taken over without objects)
        // Not saved, worked out when the pin or the settings change: how the pin is drawn.
        public bool Tinted;         // a white icon, drawn in the thing's colour
        public Color Tint = Color.white;
        public float Scale = 1f;
        public bool OnMinimap = true;
        public int Level;           // the large map's zoom step from which it shows (0 zoomed out, 1 halfway, 2 zoomed in)
        public int Filter;          // its filter button (MapView)
        // Merged on the large map: the pin it is shown as (hidden itself), or for the pin shown its count with the
        // merged ones and how many pins that is.
        public Record MergedInto;
        public int Total = 1, Pins = 1;
        public bool NameOk;         // its name may show now (large map, zoom, not merged away)
        // A portal's look: its prefab (wood or stone portal) and, with Portal List, the colour picked there and whether it
        // keeps the portal private from you (built by someone else, not shared).
        public int PortalPrefab, PortalColorIndex = -1;
        public bool PortalPrivate;

        public bool IsPortal => Thing == Catalog.PortalId;
    }

    internal static class Records
    {
        private const float Cell = 64f;
        internal static readonly List<Record> All = new List<Record>();
        private static readonly Dictionary<long, List<Record>> Grid = new Dictionary<long, List<Record>>();
        private static readonly Dictionary<ZDOID, Record> ByMember = new Dictionary<ZDOID, Record>();
        internal static string Key { get; private set; }
        private static string _path;
        internal static bool Dirty;

        internal static string Folder => Path.Combine(BepInEx.Paths.ConfigPath, "DigiAutoMapPins");

        private static long CellOf(Vector3 pos) =>
            ((long)Mathf.FloorToInt(pos.x / Cell) << 32) ^ (uint)Mathf.FloorToInt(pos.z / Cell);

        internal static void Add(Record record)
        {
            All.Add(record);
            var cell = CellOf(record.Pos);
            if (!Grid.TryGetValue(cell, out var list)) Grid[cell] = list = new List<Record>();
            list.Add(record);
            foreach (var id in record.Members) ByMember[id] = record;
            Dirty = true;
        }

        internal static void Remove(Record record)
        {
            All.Remove(record);
            if (Grid.TryGetValue(CellOf(record.Pos), out var list)) list.Remove(record);
            foreach (var id in record.Members)
                if (ByMember.TryGetValue(id, out var owner) && owner == record) ByMember.Remove(id);
            Dirty = true;
        }

        internal static void AddMember(Record record, ZDOID id)
        {
            if (id.IsNone() || ByMember.ContainsKey(id)) return;
            record.Members.Add(id);
            ByMember[id] = record;
            Dirty = true;
        }

        internal static void RemoveMember(Record record, ZDOID id)
        {
            record.Members.Remove(id);
            if (ByMember.TryGetValue(id, out var owner) && owner == record) ByMember.Remove(id);
            Dirty = true;
        }

        internal static Record OfMember(ZDOID id) => ByMember.TryGetValue(id, out var record) ? record : null;

        // Records within radius (XZ), optionally only of one thing.
        internal static IEnumerable<Record> Near(Vector3 pos, float radius, string thing = null)
        {
            var reach = Mathf.CeilToInt(radius / Cell);
            var cx = Mathf.FloorToInt(pos.x / Cell);
            var cz = Mathf.FloorToInt(pos.z / Cell);
            for (var x = cx - reach; x <= cx + reach; x++)
                for (var z = cz - reach; z <= cz + reach; z++)
                {
                    if (!Grid.TryGetValue(((long)x << 32) ^ (uint)z, out var list)) continue;
                    foreach (var record in list)
                        if ((thing == null || record.Thing == thing) && Utils.DistanceXZ(record.Pos, pos) <= radius) yield return record;
                }
        }

        // The closest spawner record of any creature (also one that does not know its creature yet).
        internal static Record ClosestSpawner(Vector3 pos, float radius, Record except = null)
        {
            Record best = null;
            var bestDistance = float.MaxValue;
            foreach (var record in Near(pos, radius))
            {
                if (record == except || !record.Thing.StartsWith(Catalog.SpawnerId, StringComparison.Ordinal)) continue;
                var distance = Utils.DistanceXZ(record.Pos, pos);
                if (distance < bestDistance) { best = record; bestDistance = distance; }
            }
            return best;
        }

        internal static Record Closest(Vector3 pos, float radius, string thing)
        {
            Record best = null;
            var bestDistance = float.MaxValue;
            foreach (var record in Near(pos, radius, thing))
            {
                var distance = Utils.DistanceXZ(record.Pos, pos);
                if (distance < bestDistance) { best = record; bestDistance = distance; }
            }
            return best;
        }

        // --- Saving --------------------------------------------------------------------------------------------

        // The world and character now played, or null before both are known.
        internal static string CurrentKey()
        {
            var profile = Game.instance != null ? Game.instance.GetPlayerProfile() : null;
            if (ZNet.instance == null || profile == null || Player.m_localPlayer == null || ZNet.instance.GetWorldName() == null) return null;
            return $"{Clean(ZNet.instance.GetWorldName())}-{ZNet.instance.GetWorldUID()}.{Clean(profile.GetFilename())}";
        }

        private static string Clean(string name)
        {
            var builder = new StringBuilder();
            foreach (var c in name ?? "") builder.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            return builder.ToString();
        }

        internal static void Load(string key)
        {
            Clear();
            Key = key;
            Directory.CreateDirectory(Folder);
            _path = Path.Combine(Folder, key + ".txt");
            if (!File.Exists(_path) && File.Exists(_path + ".tmp")) File.Move(_path + ".tmp", _path);
            if (!File.Exists(_path)) return;
            foreach (var line in File.ReadAllLines(_path))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var record = Parse(line);
                if (record != null) Add(record);
            }
            Dirty = false;
        }

        internal static bool Exists(string key) => File.Exists(Path.Combine(Folder, key + ".txt"));

        internal static void Save()
        {
            if (_path == null || !Dirty) return;
            var lines = new List<string> { "# Digi's Auto Map Pins: thing, biome, x, y, z, count, flags (D deleted, C crossed off, E emptied), faded until, label, icon, (unused)" };
            lines.AddRange(All.Select(Format));
            File.WriteAllLines(_path + ".tmp", lines);
            if (File.Exists(_path)) File.Delete(_path);
            File.Move(_path + ".tmp", _path);
            Dirty = false;
        }

        internal static void Clear()
        {
            All.Clear();
            Grid.Clear();
            ByMember.Clear();
            Key = null;
            _path = null;
            Dirty = false;
        }

        private static string Format(Record r)
        {
            var c = CultureInfo.InvariantCulture;
            var flags = (r.Deleted ? "D" : "") + (r.Checked ? "C" : "") + (r.Emptied ? "E" : "");
            return string.Join("\t", r.Thing, ((int)r.Biome).ToString(c), r.Pos.x.ToString("0.##", c), r.Pos.y.ToString("0.##", c),
                r.Pos.z.ToString("0.##", c), r.Count.ToString(c), flags, r.FadedUntil.ToString(c), Text(r.Label), Text(r.Icon), "");
        }

        private static string Text(string value) => (value ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

        private static Record Parse(string line)
        {
            var c = CultureInfo.InvariantCulture;
            var f = line.Split('\t');
            if (f.Length < 11) return null;
            try
            {
                var record = new Record
                {
                    Thing = f[0],
                    Biome = (Heightmap.Biome)int.Parse(f[1], c),
                    Pos = new Vector3(float.Parse(f[2], c), float.Parse(f[3], c), float.Parse(f[4], c)),
                    Count = int.Parse(f[5], c),
                    Deleted = f[6].Contains("D"),
                    Checked = f[6].Contains("C"),
                    Emptied = f[6].Contains("E"),
                    FadedUntil = long.Parse(f[7], c),
                    Label = f[8].Length > 0 ? f[8] : null,
                    Icon = f[9].Length > 0 ? f[9] : null,
                };
                return record; // f[10], the objects of an older file: numbers of another session, not used
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }
}
