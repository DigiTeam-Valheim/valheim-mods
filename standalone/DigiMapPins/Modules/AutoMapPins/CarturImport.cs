// Auto Map Pins, taking over from Cartur's Map Pins (which this replaces): Cartur placed its pins as normal saved map
// pins and kept a list of them per world and character (com.jekkle.valheim.carturmappins.<world>-<id>.<character>
// .pins.txt). The first time this mod runs for a world and character, every pin on that list becomes a record of this
// mod (ores, chests, spawners, dungeons, altars, traders, runestones, ...) and its saved map pin is removed, so nothing
// shows twice and no "$carturpins_..." names are left. Pins of a kind this mod does not know stay as normal pins, with
// Cartur's plain name. Cartur's own files are left alone. When this mod learns more kinds (Version), the list is read
// again: pins taken over already are skipped (a spawner one learns what it spawns), the normal pins left behind of a
// kind it now knows are taken over.
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace AutoMapPins
{
    internal static class CarturImport
    {
        // Raised when this mod learns kinds: 2 = copper, silver, mud piles, black marble, sulfur, giant remains;
        // 3 and 4 = spawners by what they spawn; 5 = wild beehives.
        private const int Version = 5;

        internal static void Run()
        {
            var profile = Game.instance != null ? Game.instance.GetPlayerProfile() : null;
            if (profile == null || ZNet.instance == null || Minimap.instance == null || Records.Key == null) return;
            var marker = Path.Combine(Records.Folder, Records.Key + ".cartur");
            if (File.Exists(marker) && int.TryParse(File.ReadAllText(marker).Trim(), out var done) && done >= Version) return;
            var file = Find(ZNet.instance.GetWorldUID(), profile.GetFilename());
            if (file == null) return;
            var pins = MapPins.GamePins();
            if (pins == null) return;

            int taken = 0, kept = 0;
            var c = CultureInfo.InvariantCulture;
            foreach (var line in File.ReadAllLines(file))
            {
                var f = line.Split('|');
                if (f.Length < 6 || !float.TryParse(f[1], NumberStyles.Float, c, out var x) ||
                    !float.TryParse(f[2], NumberStyles.Float, c, out var y) || !float.TryParse(f[3], NumberStyles.Float, c, out var z)) continue;
                var pos = new Vector3(x, y, z);
                var token = f[4];
                var name = f[5];
                var pin = pins.FirstOrDefault(p => p.m_save && Utils.DistanceXZ(p.m_pos, pos) < 1f && (p.m_name == token || p.m_name == name));
                var thing = Map(f[0], name);
                if (thing == null)
                {
                    // Not a kind this mod pins: keep the pin, with a name that does not need Cartur.
                    if (pin != null && pin.m_name.StartsWith("$carturpins"))
                    {
                        Minimap.instance.RemovePin(pin);
                        Minimap.instance.AddPin(pin.m_pos, pin.m_type, name, true, pin.m_checked);
                    }
                    kept++;
                    continue;
                }
                if (pin != null) Minimap.instance.RemovePin(pin);
                var existing = thing.Kind == Kind.Spawner ? Records.ClosestSpawner(pos, thing.Spacing) : Records.Closest(pos, thing.Spacing, thing.Id);
                if (existing != null)
                {
                    if (existing.Thing != thing.Id && thing.Id != Catalog.SpawnerId)
                    {
                        existing.Thing = thing.Id;
                        existing.Label = null;
                        existing.Icon = null;
                        Records.Dirty = true;
                    }
                    continue;
                }
                Records.Add(new Record
                {
                    Thing = thing.Id,
                    Biome = WorldGenerator.instance.GetBiome(pos),
                    Pos = pos,
                    Label = thing.Kind == Kind.Place ? name : null,
                    Checked = pin != null && pin.m_checked,
                });
                taken++;
            }
            Records.Save();
            File.WriteAllText(marker, Version.ToString(CultureInfo.InvariantCulture));
            Plugin.Log?.LogInfo($"Took over {taken} pins from Cartur's Map Pins ({Path.GetFileName(file)}); {kept} of a kind this mod does not pin stay as normal pins.");
        }

        // Cartur's list for this world and character.
        private static string Find(long worldUid, string character)
        {
            var folder = BepInEx.Paths.ConfigPath;
            if (!Directory.Exists(folder)) return null;
            var mark = "-" + worldUid.ToString(CultureInfo.InvariantCulture) + ".";
            return Directory.GetFiles(folder, "com.jekkle.valheim.carturmappins.*.pins.txt")
                .FirstOrDefault(path =>
                {
                    var name = Path.GetFileName(path);
                    return name.Contains(mark) && name.EndsWith("." + character + ".pins.txt", StringComparison.OrdinalIgnoreCase);
                });
        }

        // Cartur's kind ("Ore:Copper", "Dungeon:Crypt", "BossAltar:Moder", "Chest", ...) to this mod's thing.
        private static Thing Map(string category, string name)
        {
            var parts = category.Split(':');
            var kind = parts[0];
            var sub = parts.Length > 1 ? parts[1] : name;
            switch (kind)
            {
                case "Chest": return Catalog.Get(Catalog.ChestId);
                case "Spawner": return (parts.Length > 1 ? Spawning(parts[1]) : null) ?? Catalog.Get(Catalog.SpawnerId);
                case "Beehive": return Catalog.Get("beehive");
                case "Ore": return Ore(sub);
                case "Pickable": return ByName(Kind.Plant, sub);
                case "LoreStone": return Catalog.Get("place:Runestone");
                case "BossAltar": return ByName(Kind.Place, sub + " altar") ?? ByName(Kind.Place, sub);
                case "Dungeon":
                    return Norm(sub) == "crypt" ? Catalog.Get("place:Crypt")
                        : Norm(sub) == "sunkencrypt" ? Catalog.Get("place:SunkenCrypt") : ByName(Kind.Place, sub);
                case "Home": return null;
                default: return ByName(Kind.Place, sub) ?? ByName(Kind.Place, name);
            }
        }

        // Cartur's ores by the item they give: "Iron" is the muddy scrap piles, "Sulfur" a pickup, "Giant Remains" the place.
        private static Thing Ore(string sub)
        {
            switch (Norm(sub))
            {
                case "iron": return Catalog.Get("deposit:mudpile") ?? ByItem(Kind.Deposit, "IronScrap");
                case "giantremains": return Catalog.Get("place:Mistlands_Giant");
                case "sulfur": return ByItem(Kind.Plant, "SulfurStone") ?? ByItem(Kind.Deposit, "SulfurStone");
            }
            return ByName(Kind.Deposit, sub) ?? ByItem(Kind.Deposit, sub);
        }

        // The spawner of a creature by its name in Cartur's list ("Boar", "Greydwarf Shaman").
        private static Thing Spawning(string creature)
        {
            var wanted = Norm(creature);
            if (wanted.Length == 0 || Localization.instance == null) return null;
            return Catalog.ById.Values.FirstOrDefault(t => t.Kind == Kind.Spawner && t.Id != Catalog.SpawnerId &&
                                                           Norm(Localization.instance.Localize(t.Label)) == wanted);
        }

        private static Thing ByItem(Kind kind, string item)
        {
            var wanted = Norm(item);
            return wanted.Length == 0 ? null : Catalog.ById.Values.FirstOrDefault(t => t.Kind == kind && Norm(t.IconItem).StartsWith(wanted));
        }

        private static Thing ByName(Kind kind, string name)
        {
            var wanted = Norm(name);
            if (wanted.Length == 0) return null;
            var things = Catalog.ById.Values.Where(t => t.Kind == kind).ToList();
            return things.FirstOrDefault(t => Norm(t.Name) == wanted) ?? things.FirstOrDefault(t => Norm(t.Name).Contains(wanted));
        }

        private static string Norm(string text) => new string((text ?? "").Where(char.IsLetter).Select(char.ToLowerInvariant).ToArray());
    }
}
