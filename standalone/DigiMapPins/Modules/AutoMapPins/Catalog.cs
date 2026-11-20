// Auto Map Pins, the catalog: everything that can be found in each biome, taken from the game's own spawn tables, so
// it is complete for the game version (and includes modded content): what grows and lies around (pickables such as
// berries, mushrooms and seeds, also lying flint, stones and branches, off by default; deposits whose drops are worth
// more than wood, stone or flint; wild beehives) and every
// location (dungeons, altars, traders, camps, runestones, ruins, ...). A deposit counts with what it turns into when
// hit (a copper deposit becomes its fractured version, which is what drops the ore). What only lies inside locations
// (fuling totems, surtling core stands, dragon eggs, mud piles, tar, sulfur, ...) is in no spawn table: it is taken from
// the game's prefabs, under the biome it belongs to where that is known, else under the biome it is first found in.
// Treasure chests are placed inside locations, so they are added for every biome; with Epic Loot, so are the chests of
// its treasure maps (on by default, there is no map for the ocean). Spawners (nests and bone piles, and
// the spawn points of single creatures in camps, villages and dungeons) are listed by what they spawn ("Boar
// spawner", with its trophy; all skeletons are one), under the biomes that creature lives in. Each thing gets a switch per biome
// (with its icons drawn beside it in F1), a colour and a chosen icon, under the biome's own section. Locations of
// other mods are listed the same way; those of More World Locations AIO get their names and icons from ModPlaces.cs.
// The spawn tables only exist in a world, so the finished list is saved (DigiAutoMapPins/catalog.txt) and the biome
// sections are made from it at startup: they are in F1 from the main menu on.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using UnityEngine;

namespace AutoMapPins
{
    internal enum Kind { Plant, Deposit, Beehive, Chest, Spawner, Place, Portal, Built }

    internal sealed class Thing
    {
        public string Id;            // stable: kind + item or location group, never shown
        public string Name;          // the setting's name (English, from the game's prefab names)
        public string Label;         // what the pin says: a game token (localized by the game) or a name
        public Kind Kind;
        public Heightmap.Biome Biomes;
        public Heightmap.Biome Learned; // biomes it was found in that no spawn table names (kept when the list is rebuilt)
        public bool DefaultOn;
        public float RespawnMinutes; // > 0: grows back (the pin fades while it does)
        public float Spacing;        // things of this kind closer than this share one pin
        public string IconItem;      // item (or piece) whose icon is used
        public Sprite Icon;
        public bool IconTried;       // looked for already (in the main menu only items are there)
        public bool Dungeon, Trader; // places: a dungeon or cave (crossed off when looted), a trader
        public bool Advanced;        // its switches only in F1 with "Advanced settings" ticked (rarely worth a pin)
        public Color AutoColor = Color.white;
        public bool ColorKnown;      // AutoColor read from the saved list (not worked out from the icon again)
        public readonly Slot[] Slots = new Slot[Catalog.Biomes.Length]; // its settings per biome, in the order of Biomes
    }

    // The settings of a thing in one biome: its switch, colour and chosen icon.
    internal sealed class Slot
    {
        public ConfigEntry<bool> Toggle;
        public ConfigEntry<Color> Color;
        public ConfigEntry<string> Icon;
    }

    internal static partial class Catalog
    {
        internal static bool Built { get; private set; }
        internal static readonly Dictionary<string, Thing> ById = new Dictionary<string, Thing>();
        private static readonly Dictionary<int, Thing> ByPrefab = new Dictionary<int, Thing>();
        private static readonly Dictionary<string, Thing> ByLocation = new Dictionary<string, Thing>(StringComparer.OrdinalIgnoreCase);
        // Every name a thing goes by (its label token, its setting name, the own names of its pickups), for pins read
        // from a cartography table.
        private static readonly Dictionary<string, Thing> ByName = new Dictionary<string, Thing>(StringComparer.OrdinalIgnoreCase); // a pin typed by hand: any case
        private static readonly HashSet<string> UsedKeys = new HashSet<string>();
        private static DigiCore.ModuleConfig _config;
        internal static bool NewDefaults; // this start applies Defaults.Changed (once per game)
        internal static bool NewerDefaults; // and Defaults.ChangedLater (once per game, from 1.2.5)

        internal const string ChestId = "chest", MapChestId = "treasuremap", SpawnerId = "spawner", PortalId = "portal", MorkhallaId = "place:MorkBorg";
        internal static event Action Changed, Recolored;

        // Biomes in the order players meet them; their sections follow General and Portals.
        internal static readonly Heightmap.Biome[] Biomes =
        {
            Heightmap.Biome.Meadows, Heightmap.Biome.BlackForest, Heightmap.Biome.Swamp, Heightmap.Biome.Mountain,
            Heightmap.Biome.Plains, Heightmap.Biome.Mistlands, Heightmap.Biome.AshLands, Heightmap.Biome.DeepNorth,
            Heightmap.Biome.Ocean,
        };

        private static Heightmap.Biome AllBiomes => Biomes.Aggregate(Heightmap.Biome.None, (a, b) => a | b);

        // Boss altars (their names have a switch of their own).
        private static readonly HashSet<string> Bosses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "place:Eikthyrnir", "place:GDKing", "place:Bonemass", "place:Dragonqueen", "place:GoblinKing",
            "place:Mistlands_DvergrBossEntrance", "place:FaderLocation", "place:DN_Bossroom",
        };

        internal static bool IsBoss(Thing thing) => thing != null && Bosses.Contains(thing.Id);

        internal static bool IsTrader(Thing thing) => thing != null && thing.Trader;

        // Dungeons and caves (Burial Chambers, crypts, troll caves, frost caves, mines, ...): names and size of their own,
        // crossed off when looted. With camps and villages they are the places with spawners of their own. The known
        // ones say so in Places; a place of another mod counts by its name.
        private static readonly Regex Dungeon = new Regex(@"(Crypt|Cave|Cavern|Mine|Tunnel|Dungeon|Tomb)", RegexOptions.IgnoreCase);
        private static readonly Regex Camp = new Regex(@"(Camp|Village)", RegexOptions.IgnoreCase);

        internal static bool IsDungeon(Thing thing) => thing != null && thing.Kind == Kind.Place && thing.Dungeon;

        internal static bool IsLair(Thing thing) => IsDungeon(thing) || thing != null && thing.Kind == Kind.Place && Camp.IsMatch(thing.Id);

        internal static string BiomeName(Heightmap.Biome biome) =>
            biome == Heightmap.Biome.BlackForest ? "Black Forest"
            : biome == Heightmap.Biome.AshLands ? "Ashlands"
            : biome == Heightmap.Biome.DeepNorth ? "Deep North"
            : biome.ToString();

        private static string Section(Heightmap.Biome biome) =>
            $"{Array.IndexOf(Biomes, biome) + 3:00} - {BiomeName(biome)}";

        // Drops that make a rock or stump not worth a pin.
        private static readonly HashSet<string> Plain = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Wood", "FineWood", "RoundLog", "ElderBark", "YggdrasilWood", "Blackwood", "Stone", "Flint", "Resin",
            "Grausten", "Feathers", "BoneFragments", "StoneRock", "Snowball", "FirCone",
        };

        // Pickups inside locations that are listed but off by default: props, crops (only ever planted) and lures.
        private static readonly HashSet<string> Minor = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Lantern", "Tankard_dvergr", "FishingRod", "Wisp", "FaderEmber", "Turnip", "Carrot", "Onion", "Kale", "Poteitr",
            "OatSeeds", "Coal",
        };

        // Where things that are only placed inside locations belong (by the item they give), so they are in F1 before
        // they are found. Others go under the biome they are first found in.
        private static readonly Dictionary<string, Heightmap.Biome> Homes = new Dictionary<string, Heightmap.Biome>(StringComparer.OrdinalIgnoreCase)
        {
            ["SurtlingCore"] = Heightmap.Biome.BlackForest, ["Guck"] = Heightmap.Biome.Swamp, ["IronScrap"] = Heightmap.Biome.Swamp,
            ["DragonEgg"] = Heightmap.Biome.Mountain, ["Crystal"] = Heightmap.Biome.Mountain, ["Obsidian"] = Heightmap.Biome.Mountain,
            ["WolfHairBundle"] = Heightmap.Biome.Mountain, ["GoblinTotem"] = Heightmap.Biome.Plains, ["Tar"] = Heightmap.Biome.Plains,
            ["Barley"] = Heightmap.Biome.Plains, ["Flax"] = Heightmap.Biome.Plains, ["BlackCore"] = Heightmap.Biome.Mistlands,
            ["RoyalJelly"] = Heightmap.Biome.Mistlands, ["Softtissue"] = Heightmap.Biome.Mistlands, ["Coins"] = Heightmap.Biome.Mistlands,
            ["Lantern"] = Heightmap.Biome.Mistlands, ["Tankard_dvergr"] = Heightmap.Biome.Mistlands,
            ["MoltenCore"] = Heightmap.Biome.AshLands, ["SulfurStone"] = Heightmap.Biome.AshLands, ["VoltureEgg"] = Heightmap.Biome.AshLands,
            ["BellFragment"] = Heightmap.Biome.AshLands, ["FlametalOreNew"] = Heightmap.Biome.AshLands, ["GoldOre"] = Heightmap.Biome.DeepNorth,
        };

        // Creatures whose spawn points are listed but off by default.
        private static readonly HashSet<string> TameCreatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "$enemy_chicken", "$enemy_hen", "$animal_fish4", "$animal_fish4_cave",
        };

        // Things players build that get a pin anyway, as soon as they are placed (other builds never do).
        private static readonly string[] Builds = { "piece_sapcollector" };

        // Breakable props inside locations worth a pin (other props with drops, such as barrels, are not).
        private static readonly HashSet<string> Props = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "GuckSack", "GuckSack_small", "Charred_altar_bellfragment",
        };

        // Locations people look for: name, icon and whether they are on by default. The icon says why you go there (what
        // the place holds, read from the places' own prefabs and dungeon rooms): cores in Burial Chambers, scrap iron in
        // crypts, black metal in fuling villages; a trophy where the place is about its creature or boss. Everything else
        // is listed too, off and only in F1's advanced settings, with a name made from its prefab.
        private sealed class Known
        {
            public string Name, Icon;
            public string Label; // what the pin says, when not its name (the setting's key)
            public string White; // its white icon (places of other mods; the game's own places have theirs in WhiteIcons)
            public bool On = true, Dungeon, Trader, Advanced;
        }

        // A place's entry: the game's own (Places) or another mod's (ModPlaces).
        private static bool KnownPlace(string name, out Known known) =>
            Places.TryGetValue(name, out known) || ModPlaces.TryGetValue(name, out known);

        // The white icon a place of another mod has in ModPlaces.
        internal static string WhiteIconOf(Thing thing) =>
            thing != null && thing.Kind == Kind.Place && thing.Id.StartsWith("place:", StringComparison.Ordinal) &&
            KnownPlace(thing.Id.Substring(6), out var known) ? known.White : null;

        private static readonly Dictionary<string, Known> Places = new Dictionary<string, Known>(StringComparer.OrdinalIgnoreCase)
        {
            // Dungeons and caves
            ["Crypt"] = new Known { Name = "Burial Chambers", Icon = "SurtlingCore", Dungeon = true },
            ["SunkenCrypt"] = new Known { Name = "Crypt", Icon = "IronScrap", Dungeon = true },
            ["TrollCave"] = new Known { Name = "Troll Cave", Icon = "TrophyFrostTroll", Dungeon = true },
            ["BearCave"] = new Known { Name = "Bear Cave", Icon = "TrophyBjorn", Dungeon = true },
            ["MountainCave"] = new Known { Name = "Frost Cave", Icon = "WolfHairBundle", Dungeon = true },
            ["Mistlands_DvergrTownEntrance"] = new Known { Name = "Infested Mine", Icon = "BlackCore", Dungeon = true },
            ["Hildir_crypt"] = new Known { Name = "Smouldering Tomb", Icon = "TrophySkeletonHildir", Dungeon = true },
            ["Hildir_cave"] = new Known { Name = "Howling Cavern", Icon = "TrophyCultist_Hildir", Dungeon = true },
            ["Hildir_plainsfortress"] = new Known { Name = "Sealed Tower", Icon = "TrophyGoblinBruteBrosBrute", Dungeon = true },
            ["MorkBorg"] = new Known { Name = "Morkhalla", Icon = "AncientGemstonePurple", Dungeon = true },
            ["TheHole"] = new Known { Name = "Winding Tunnels", Icon = "FrostCore", Dungeon = true }, // "$location_thehole"
            ["FortressRuins"] = new Known { Name = "Fortress ruins", Icon = "GemstoneRed", On = false, Dungeon = true },
            ["AshlandRuins"] = new Known { Name = "Ashland ruins", Icon = "Fiddleheadfern", On = false, Dungeon = true },
            ["NorthVillage"] = new Known { Name = "North village", Icon = "KaleSeeds", On = false, Dungeon = true },
            // Boss altars: the pin says only the boss's name; the settings keep "... altar".
            ["Eikthyrnir"] = new Known { Name = "Eikthyr altar", Label = "Eikthyr", Icon = "TrophyEikthyr" },
            ["GDKing"] = new Known { Name = "The Elder altar", Label = "The Elder", Icon = "TrophyTheElder" },
            ["Bonemass"] = new Known { Name = "Bonemass altar", Label = "Bonemass", Icon = "TrophyBonemass" },
            ["Dragonqueen"] = new Known { Name = "Moder altar", Label = "Moder", Icon = "TrophyDragonQueen" },
            ["GoblinKing"] = new Known { Name = "Yagluth altar", Label = "Yagluth", Icon = "TrophyGoblinKing" },
            ["Mistlands_DvergrBossEntrance"] = new Known { Name = "The Queen altar", Label = "The Queen", Icon = "TrophySeekerQueen" },
            ["FaderLocation"] = new Known { Name = "Fader altar", Label = "Fader", Icon = "TrophyFader" },
            ["DN_Bossroom"] = new Known { Name = "Frozen King altar", Label = "Frozen King", Icon = "CrownJewel" }, // drops no trophy
            // Traders: the map's own trader icons (the game shows them itself once found; this pin only where it does not)
            ["Vendor_BlackForest"] = new Known { Name = "Haldor", Icon = "loc:Vendor_BlackForest", Trader = true },
            ["Hildir_camp"] = new Known { Name = "Hildir", Icon = "loc:Hildir_camp", Trader = true },
            ["BogWitch_Camp"] = new Known { Name = "Bog Witch", Icon = "loc:BogWitch_Camp", Trader = true },
            // Places with something to get
            ["GoblinCamp"] = new Known { Name = "Fuling village", Icon = "BlackMetalScrap" },
            ["CharredFortress"] = new Known { Name = "Charred Fortress", Icon = "BellFragment" }, // the Fader's bell fragments
            ["MorgenHole"] = new Known { Name = "Putrid Hole", Icon = "TrophyMorgen" },
            ["PlaceofMystery"] = new Known { Name = "Place of Mystery", Icon = "DyrnwynHiltFragment" },
            // Deep North: three Memorial Coal on its altar call warriors that drop the frost-fire and thunder-blood orbs
            // the Deep North gear needs (2026-11-20).
            ["NorthMemorialPlace"] = new Known { Name = "Memorial altar", Icon = "OrbFrostFire" },
            ["TarPit"] = new Known { Name = "Tar pit", Icon = "Tar" },
            ["DrakeNest"] = new Known { Name = "Drake nest", Icon = "DragonEgg" },
            ["Mistlands_Giant"] = new Known { Name = "Giant remains", Icon = "Softtissue" },
            ["InfestedTree"] = new Known { Name = "Guck tree", Icon = "Guck" },
            ["WoodVillage"] = new Known { Name = "Draugr village", Icon = "TrophyDraugr" },
            ["WoodFarm"] = new Known { Name = "Abandoned farm", Icon = "TrophyBoar", On = false },
            // The stones set out as a ship over a Viking grave, with treasure buried in it (the game calls it ShipSetting).
            ["ShipSetting"] = new Known { Name = "Viking burial" },
            ["Mistlands_Excavation"] = new Known { Name = "Dvergr excavation", Icon = "TrophyDvergr" },
            ["Mistlands_Harbour"] = new Known { Name = "Dvergr harbour", Icon = "TrophyDvergr" },
            ["Mistlands_GuardTower"] = new Known { Name = "Dvergr tower", Icon = "TrophyDvergr", On = false },
            ["Mistlands_Lighthouse"] = new Known { Name = "Dvergr lighthouse", Icon = "TrophyDvergr", On = false },
            ["FireHole"] = new Known { Name = "Fire hole", Icon = "TrophySurtling", On = false },
            ["VoltureNest"] = new Known { Name = "Volture nest", Icon = "VoltureEgg", On = false },
            ["SulfurArch"] = new Known { Name = "Sulfur arch", Icon = "SulfurStone", On = false },
            ["Runestone"] = new Known { Name = "Runestone", On = false, Advanced = true },
        };

        // Things that got a proper name after they were saved in the list under one made from their prefab. The name is
        // the setting's key, and a saved name stays, so the list is overruled here: the new name, its switch on by default.
        private static readonly Dictionary<string, string> Renamed = new Dictionary<string, string>
        {
            [MorkhallaId] = "Morkhalla",
            ["place:TheHole"] = "Winding Tunnels",
            ["place:ShipSetting"] = "Viking burial", // was "Ship setting" (its prefab name) before 1.2.0
        };

        // Location prefabs that are not worth a pin at all: the start, invisible helpers, places the game marks itself
        // (the upgrade station) and places whose own thing has a pin (the Ashlands' flametal, the gold of a petrified
        // gammeltroll, greydwarf nests, charred stone spawners).
        private static readonly Regex Skip = new Regex(
            @"^(StartTemple|Meteorite|Vegvisir_.*Placeholder|AncientUpgradeStation|LeviathanLava|DN_gammeltrollFrac|Greydwarf_camp|CharredStone_Spawner)",
            RegexOptions.IgnoreCase);
        private static readonly Regex Runestone = new Regex(@"^(Runestone|Vegvisir)", RegexOptions.IgnoreCase);

        // The variants of a place are one ("StoneTowerRuins07_sunk", "Mistlands_GuardTower1_ruined_new" and
        // "FrozenShip01_DN" are "StoneTowerRuins", "Mistlands_GuardTower" and "FrozenShip").
        private static readonly Regex Variant = new Regex(@"_(new\d*|ruined|sunk|leet|heath|dvergr|DN)$|[_\d]+$", RegexOptions.IgnoreCase);

        internal static string PlaceGroup(string prefabName)
        {
            if (Whole.Contains(prefabName)) return prefabName;
            var group = prefabName;
            for (var i = 0; i < 8; i++)
            {
                var next = Variant.Replace(group, "");
                if (next == group || next.Length == 0) break;
                group = next;
            }
            return Runestone.IsMatch(group) ? "Runestone" : group;
        }

        // A place's kind, from Places or (for the rest) its name.
        private static void ApplyKnown(Thing thing)
        {
            if (thing.Kind != Kind.Place || !thing.Id.StartsWith("place:", StringComparison.Ordinal)) return;
            var group = thing.Id.Substring(6);
            if (KnownPlace(group, out var known))
            {
                thing.Dungeon = known.Dungeon;
                thing.Trader = known.Trader;
                thing.Advanced = known.Advanced;
            }
            else
            {
                thing.Dungeon = Dungeon.IsMatch(group);
                thing.Advanced = true;
            }
        }

        internal static void Bind(DigiCore.ModuleConfig config)
        {
            _config = config;
            LoadCache();
        }

        private static string CachePath => Path.Combine(Records.Folder, "catalog.txt");
        // v5 (1.1.0): no plain pickups and props, only spawners that can be destroyed, the variants of a place as one.
        private const string CacheHeader = "# Digi's Auto Map Pins v5:";

        // The list saved by the last world load: its switches exist before any world is loaded.
        private static void LoadCache()
        {
            if (!File.Exists(CachePath)) return;
            var c = CultureInfo.InvariantCulture;
            var lines = File.ReadAllLines(CachePath);
            if (lines.Length == 0 || !lines[0].StartsWith(CacheHeader, StringComparison.Ordinal)) return; // an older list: rebuilt at the next world load
            foreach (var line in lines)
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var f = line.Split('\t');
                if (f.Length < 10 || !Enum.TryParse(f[3], out Kind kind) || !int.TryParse(f[4], NumberStyles.Integer, c, out var biomes) ||
                    !float.TryParse(f[6], NumberStyles.Float, c, out var respawn) || !float.TryParse(f[7], NumberStyles.Float, c, out var spacing)) continue;
                var renamed = Renamed.TryGetValue(f[0], out var newName);
                // A place of another mod saved under the name made from its prefab (before ModPlaces had it): its own
                // name, switch default and icon now.
                var mod = !renamed && kind == Kind.Place && f[0].StartsWith("place:", StringComparison.Ordinal) &&
                          ModPlaces.TryGetValue(f[0].Substring(6), out var entry) && entry.Name != NoTags(f[1]) ? entry : null;
                var name = renamed ? newName : mod != null ? mod.Name : NoTags(f[1]);
                var label = renamed ? newName : mod != null ? mod.Label ?? mod.Name : f[2];
                var on = renamed || (mod != null ? mod.On : f[5] == "1");
                var thing = Add(f[0], name, label, kind, (Heightmap.Biome)biomes, on, respawn, spacing, mod != null ? mod.Icon : f[8].Length > 0 ? f[8] : null);
                if (mod == null && ColorUtility.TryParseHtmlString("#" + f[9], out var color))
                {
                    thing.AutoColor = color;
                    thing.ColorKnown = true;
                }
                if (f.Length > 10 && int.TryParse(f[10], NumberStyles.Integer, c, out var learned)) thing.Learned = (Heightmap.Biome)learned;
                ApplyKnown(thing);
                if (thing.Id == SpawnerId) thing.Advanced = true;
            }
            BindAll();
        }

        private static bool _cacheDirty;

        // A thing found in a biome of its own: the list keeps it there.
        internal static void SaveIfChanged()
        {
            if (_cacheDirty) SaveCache();
        }

        private static void SaveCache()
        {
            _cacheDirty = false;
            var c = CultureInfo.InvariantCulture;
            var lines = new List<string> { CacheHeader + " what can be found per biome (rebuilt at every world load): id, name, label, kind, biomes, on, respawn, spacing, icon, colour, biomes found in outside the spawn tables" };
            foreach (var t in ById.Values)
                lines.Add(string.Join("\t", t.Id, t.Name, t.Label, t.Kind.ToString(), ((int)t.Biomes).ToString(c), t.DefaultOn ? "1" : "0",
                    t.RespawnMinutes.ToString(c), t.Spacing.ToString(c), t.IconItem ?? "", ColorUtility.ToHtmlStringRGB(t.AutoColor),
                    ((int)t.Learned).ToString(c)));
            try
            {
                Directory.CreateDirectory(Records.Folder);
                File.WriteAllLines(CachePath, lines);
            }
            catch (IOException e)
            {
                Plugin.Log?.LogWarning("Could not save the catalog: " + e.Message);
            }
        }

        private static void BindAll()
        {
            foreach (var biome in Biomes)
                foreach (var thing in ById.Values.Where(t => (t.Biomes & biome) != 0).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
                    BindThing(biome, thing);
        }

        // Builds the catalog once the world is loaded (spawn tables, prefabs and items all exist then).
        internal static bool TryBuild()
        {
            if (Built) return true;
            var zones = ZoneSystem.instance;
            var scene = ZNetScene.instance;
            var items = ObjectDB.instance;
            if (zones == null || scene == null || items == null || items.m_items.Count == 0 || _config == null) return false;

            _building = true;
            try
            {
                Build(zones, scene);
            }
            finally
            {
                _building = false;
            }
            foreach (var thing in ById.Values) Icons.Prepare(thing);
            BindAll();
            Fresh.Clear();
            Built = true;
            SaveCache();
            Plugin.Log?.LogInfo($"Catalog: {ById.Count} kinds of things over {Biomes.Length} biomes.");
            return true;
        }

        private static bool _building;

        private static void Build(ZoneSystem zones, ZNetScene scene)
        {
            // Switched off spawn entries still tell where a thing belongs (bog iron in the swamp, barley in the plains).
            var unused = new Dictionary<string, Heightmap.Biome>();
            foreach (var vegetation in zones.m_vegetation)
            {
                if (vegetation == null || vegetation.m_prefab == null) continue;
                if (vegetation.m_enable) AddVegetation(vegetation.m_prefab, vegetation.m_biome);
                else unused[vegetation.m_prefab.name] = (unused.TryGetValue(vegetation.m_prefab.name, out var b) ? b : 0) | vegetation.m_biome;
            }
            foreach (var location in zones.m_locations)
                if (location != null && location.m_enable && !string.IsNullOrEmpty(location.m_prefabName)) AddLocation(location.m_prefabName, location.m_biome);

            var chest = Add(ChestId, "Treasure chests", "$piece_chestwood", Kind.Chest, AllBiomes, true, 0f, 3f, "piece_chest_wood");
            if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("randyknapp.mods.epicloot"))
                Add(MapChestId, "Treasure map chests", "Treasure map chest", Kind.Chest, AllBiomes & ~Heightmap.Biome.Ocean, true, 0f, 3f, "piece_chest_wood");
            // Spawners of nothing known (and pins from before spawners were told apart, until they load again).
            Add(SpawnerId, "Spawners", "Spawner", Kind.Spawner, Heightmap.Biome.None, true, 0f, 6f, null).Advanced = true;
            SpawnerDrops.Clear();
            var creatures = CreatureHomes(zones);
            foreach (var name in Builds)
            {
                var piece = scene.GetPrefab(name)?.GetComponent<Piece>();
                if (piece != null)
                    ByPrefab[name.GetStableHashCode()] = Add("built:" + name, Localized(piece.m_name, Pretty(name)), piece.m_name, Kind.Built,
                        Heightmap.Biome.Mistlands, true, 0f, 8f, name);
            }
            foreach (var prefab in scene.m_prefabs)
            {
                if (prefab == null || ByPrefab.ContainsKey(prefab.name.GetStableHashCode())) continue;
                unused.TryGetValue(prefab.name, out var home);
                // Before the Piece check: Plant Everything makes wild pickables buildable (planted ones have a creator).
                var pickable = prefab.GetComponent<Pickable>();
                if (pickable != null)
                {
                    AddPickup(prefab, pickable, home);
                    continue;
                }
                // World chests (TreasureChest_*, loot_chest_*, Morkhalla's, ...) carry a Piece component too, so before the
                // Piece check; the chests you build are named piece_* (and placed ones have a creator, so never pinned).
                if (prefab.GetComponent<Container>() != null && prefab.GetComponent<TombStone>() == null &&
                    prefab.name.IndexOf("Chest", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    !prefab.name.StartsWith("piece_", StringComparison.OrdinalIgnoreCase))
                {
                    ByPrefab[prefab.name.GetStableHashCode()] = chest;
                    continue;
                }
                if (prefab.GetComponent<Piece>() != null) continue;
                if (WildBeehive(prefab))
                {
                    ByPrefab[prefab.name.GetStableHashCode()] =
                        Add("beehive", "Beehive", "$piece_beehive", Kind.Beehive, Heightmap.Biome.Meadows, true, 0f, 15f, "Honey");
                    continue;
                }
                if (prefab.GetComponent<SpawnArea>() != null || prefab.GetComponent<CreatureSpawner>() != null)
                {
                    if (Nest(prefab)) AddSpawner(prefab, home, creatures);
                }
                else if (prefab.GetComponent<MineRock5>() != null || prefab.GetComponent<MineRock>() != null ||
                         Fractured(prefab) != null || Props.Contains(prefab.name))
                    AddDeposit(prefab, home, true);
            }
            SpawnerIcons();
        }

        // Only spawners you can destroy are worth a pin (nests, bone piles, body piles, charred stones): the single
        // creatures' spawn points of camps, villages and ruins are not (the place's own pin says enough).
        private static bool Nest(GameObject prefab) => prefab.GetComponent<SpawnArea>() != null && prefab.GetComponent<Destructible>() != null;

        // What each kind of spawner drops when destroyed (a greydwarf nest an Ancient Seed), per prefab.
        private static readonly Dictionary<int, string> NestDrop = new Dictionary<int, string>();
        private static readonly Dictionary<Thing, HashSet<string>> SpawnerDrops = new Dictionary<Thing, HashSet<string>>();
        private static readonly HashSet<string> NotADrop = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Frostwood", "Grausten" };

        private static string DropOf(GameObject prefab)
        {
            var table = prefab.GetComponent<DropOnDestroyed>()?.m_dropWhenDestroyed;
            if (table?.m_drops == null) return null;
            foreach (var data in table.m_drops)
                if (data.m_item != null && !Plain.Contains(data.m_item.name) && !NotADrop.Contains(data.m_item.name)) return data.m_item.name;
            return null;
        }

        // A spawner's own drop for its pin when it differs from the icon of its kind (the swamp's evil heart, a draugr
        // spawner, drops an Ancient Seed; a draugr body pile does not).
        internal static string NestIcon(int prefabHash, Thing thing) =>
            NestDrop.TryGetValue(prefabHash, out var drop) && drop != thing.IconItem ? drop : null;

        // The icon of a kind of spawner: what all of them drop when destroyed, else the creature's trophy.
        private static void SpawnerIcons()
        {
            foreach (var pair in SpawnerDrops)
                if (pair.Value.Count == 1 && !pair.Value.Contains("")) pair.Key.IconItem = pair.Value.First();
        }

        private static void AddVegetation(GameObject prefab, Heightmap.Biome biome)
        {
            var pickable = prefab.GetComponent<Pickable>();
            if (pickable != null && pickable.m_itemPrefab != null)
            {
                var item = pickable.m_itemPrefab.GetComponent<ItemDrop>();
                if (item == null || Minor.Contains(item.name)) return;
                // Lying flint, stones, branches and the like: listed (some players want them), off by default.
                var plain = Plain.Contains(item.name);
                var thing = Add("plant:" + item.name, Localized(item.m_itemData.m_shared.m_name, Pretty(item.name)), item.m_itemData.m_shared.m_name, Kind.Plant, biome,
                    !plain, pickable.m_respawnTimeMinutes, 12f, item.name);
                ByPrefab[prefab.name.GetStableHashCode()] = thing;
                Named(pickable.m_overrideName, thing);
                return;
            }
            var hive = prefab.GetComponent<Beehive>();
            if (hive != null)
            {
                ByPrefab[prefab.name.GetStableHashCode()] =
                    Add("beehive", "Beehive", string.IsNullOrEmpty(hive.m_name) ? "$piece_beehive" : hive.m_name, Kind.Beehive, biome, true, 0f, 15f, "Honey");
                return;
            }
            if (prefab.GetComponent<SpawnArea>() != null)
            {
                if (Nest(prefab)) AddSpawner(prefab, biome, null);
                return;
            }
            AddDeposit(prefab, biome, false);
        }

        // The wild beehive is no beehive to the game (that is the one you build): it breaks and drops honey and a queen bee.
        private static bool WildBeehive(GameObject prefab)
        {
            var drops = prefab.GetComponent<DropOnDestroyed>()?.m_dropWhenDestroyed?.m_drops;
            return drops != null && drops.Any(drop => drop.m_item != null && drop.m_item.name == "QueenBee");
        }

        // Where each creature lives, from the game's spawn lists.
        private static Dictionary<string, Heightmap.Biome> CreatureHomes(ZoneSystem zones)
        {
            var homes = new Dictionary<string, Heightmap.Biome>();
            var system = zones.m_zoneCtrlPrefab != null ? zones.m_zoneCtrlPrefab.GetComponent<SpawnSystem>() : null;
            if (system?.m_spawnLists == null) return homes;
            foreach (var list in system.m_spawnLists)
            {
                if (list?.m_spawners == null) continue;
                foreach (var data in list.m_spawners)
                    if (data != null && data.m_enabled && data.m_prefab != null)
                        homes[data.m_prefab.name] = (homes.TryGetValue(data.m_prefab.name, out var b) ? b : 0) | (data.m_biome & AllBiomes);
            }
            // A creature spawned (nearly) everywhere says nothing about where its spawners are: those go by where they are found.
            foreach (var name in homes.Keys.ToList())
                if (Biomes.Count(biome => (homes[name] & biome) != 0) > 3) homes[name] = Heightmap.Biome.None;
            return homes;
        }

        // A spawner by what it spawns, a nest or a single creature's spawn point: "Boar spawner", with the boar's trophy;
        // its pin just says "Boar". By the creature's name, so all skeletons (swamp, meadows, no archer) are one. Under
        // the biome of its spawn table, else the biomes the creature lives in, else the biome it is first found in.
        private static void AddSpawner(GameObject prefab, Heightmap.Biome biome, Dictionary<string, Heightmap.Biome> creatures)
        {
            var hash = prefab.name.GetStableHashCode();
            var creature = prefab.GetComponent<SpawnArea>()?.m_prefabs?
                .Select(data => data?.m_prefab).FirstOrDefault(p => p != null && p.GetComponent<Character>() != null)
                ?? prefab.GetComponent<CreatureSpawner>()?.m_creaturePrefab;
            var token = creature != null ? creature.GetComponent<Character>()?.m_name : null;
            if (string.IsNullOrEmpty(token))
            {
                ByPrefab[hash] = Get(SpawnerId);
                return;
            }
            if (biome == Heightmap.Biome.None && creatures != null) creatures.TryGetValue(creature.name, out biome);
            var trophy = creature.GetComponent<CharacterDrop>()?.m_drops?
                .Select(drop => drop?.m_prefab).FirstOrDefault(p => p != null && p.name.StartsWith("Trophy", StringComparison.Ordinal));
            var thing = Add("spawner:" + token.TrimStart('$'), Localized(token, Pretty(creature.name)) + " spawner", token, Kind.Spawner,
                biome, !TameCreatures.Contains(token), 0f, 20f, null);
            if (thing.IconItem == null && trophy != null) thing.IconItem = trophy.name;
            ByPrefab[hash] = thing;
            var drop = DropOf(prefab);
            if (drop != null) NestDrop[hash] = drop;
            if (!SpawnerDrops.TryGetValue(thing, out var drops)) SpawnerDrops[thing] = drops = new HashSet<string>();
            drops.Add(drop ?? "");
        }

        // Records of things this list no longer has: a place under its old name (one variant of a place that is now
        // one) gets the new one; a spawner of a creature that has no spawner you can destroy (a single creature's spawn
        // point) and things not worth a pin any more (stones, crops, props) are dropped.
        internal static void ForgetUnknown()
        {
            var renamed = 0;
            var dropped = new List<Record>();
            foreach (var record in Records.All)
            {
                if (record.IsPortal || ById.ContainsKey(record.Thing)) continue;
                if (record.Thing.StartsWith("place:", StringComparison.Ordinal))
                {
                    var id = "place:" + PlaceGroup(record.Thing.Substring(6));
                    if (ById.ContainsKey(id))
                    {
                        record.Thing = id;
                        renamed++;
                        continue;
                    }
                }
                dropped.Add(record);
            }
            foreach (var record in dropped) Records.Remove(record);
            if (renamed > 0) Records.Dirty = true;
            if (renamed + dropped.Count > 0)
                Plugin.Log?.LogInfo($"Pins of things renamed: {renamed}; of things no longer pinned (single spawn points, stones, props): {dropped.Count} removed.");
        }

        // A pickup that only lies inside locations (fuling totem, surtling core, dragon egg, tar, ...).
        private static void AddPickup(GameObject prefab, Pickable pickable, Heightmap.Biome biome)
        {
            var item = pickable.m_itemPrefab != null ? pickable.m_itemPrefab.GetComponent<ItemDrop>() : null;
            if (item == null || Plain.Contains(item.name) || Minor.Contains(item.name)) return;
            if (biome == Heightmap.Biome.None) Homes.TryGetValue(item.name, out biome);
            var token = item.m_itemData.m_shared.m_name;
            var thing = Add("plant:" + item.name, Localized(token, Pretty(item.name)), token, Kind.Plant,
                biome, true, pickable.m_respawnTimeMinutes, 12f, item.name);
            ByPrefab[prefab.name.GetStableHashCode()] = thing;
            Named(pickable.m_overrideName, thing);
        }

        // A deposit (or a prop worth a pin): by its own name when it has one ("$piece_deposit_copper", "$piece_mudpile";
        // muddy scrap piles and giant helmets both give iron scrap), else by what it gives.
        private static void AddDeposit(GameObject prefab, Heightmap.Biome biome, bool insideLocations)
        {
            var drop = NotableDrop(prefab, out var token, out var fractured);
            if (drop == null || Minor.Contains(drop.name)) return;
            if (insideLocations && biome == Heightmap.Biome.None) Homes.TryGetValue(drop.name, out biome);
            var named = token != null && token.StartsWith("$piece_", StringComparison.Ordinal);
            var thing = Add("deposit:" + (named ? token.Substring(7) : drop.name), Localized(named ? token : drop.m_itemData.m_shared.m_name, Pretty(drop.name)),
                string.IsNullOrEmpty(token) ? drop.m_itemData.m_shared.m_name : token, Kind.Deposit, biome, !Minor.Contains(drop.name), 0f, 10f, drop.name);
            ByPrefab[prefab.name.GetStableHashCode()] = thing;
            if (fractured != null) ByPrefab[fractured.name.GetStableHashCode()] = thing; // keeps the pin while it is mined
        }

        // What a rock turns into when hit, if that is a rock to mine (not just an effect or a dropped item).
        private static GameObject Fractured(GameObject prefab)
        {
            var spawn = prefab.GetComponent<Destructible>()?.m_spawnWhenDestroyed;
            return spawn != null && (spawn.GetComponent<MineRock5>() != null || spawn.GetComponent<MineRock>() != null) ? spawn : null;
        }

        // The first drop of a rock, stump or the like (or of what it turns into when hit) that is not plain wood, stone
        // or flint, and the rock's name.
        private static ItemDrop NotableDrop(GameObject prefab, out string name, out GameObject fractured)
        {
            name = null;
            fractured = Fractured(prefab);
            if (fractured != null) prefab = fractured;
            DropTable table = null;
            var rock5 = prefab.GetComponent<MineRock5>();
            var rock = prefab.GetComponent<MineRock>();
            var destroyed = prefab.GetComponent<DropOnDestroyed>();
            if (rock5 != null) { table = rock5.m_dropItems; name = rock5.m_name; }
            else if (rock != null) { table = rock.m_dropItems; name = rock.m_name; }
            else if (destroyed != null && prefab.GetComponent<Destructible>() != null) table = destroyed.m_dropWhenDestroyed;
            if (table?.m_drops == null) return null;
            foreach (var data in table.m_drops)
            {
                var item = data.m_item != null ? data.m_item.GetComponent<ItemDrop>() : null;
                if (item != null && !Plain.Contains(item.name)) return item;
            }
            return null;
        }

        // The game's own name of a token ("Copper deposit"), or the fallback when there is none.
        private static string Localized(string token, string fallback)
        {
            if (string.IsNullOrEmpty(token) || token[0] != '$' || Localization.instance == null) return fallback;
            var text = NoTags(Localization.instance.Localize(token));
            return string.IsNullOrEmpty(text) || text[0] == '[' ? fallback : text;
        }

        private static void AddLocation(string prefabName, Heightmap.Biome biome)
        {
            if (Skip.IsMatch(prefabName)) return;
            var group = PlaceGroup(prefabName);
            if (!KnownPlace(group, out var known) && !KnownPlace(prefabName, out known))
                known = new Known
                {
                    Name = Pretty(Regex.Replace(group, @"^(Mistlands|Ashlands|AshLands|DeepNorth|DN|Meadows|BlackForest|Swamp|Mountain|Plains)_", "")),
                    On = false,
                };
            var thing = Add("place:" + group, known.Name, known.Label ?? known.Name, Kind.Place, biome, known.On, 0f, 40f, known.Icon);
            ApplyKnown(thing);
            ByLocation[prefabName] = thing;
        }

        // Ids already updated by this world load (a thing from the saved list gets the world's current data once).
        private static readonly HashSet<string> Fresh = new HashSet<string>();

        private static Thing Add(string id, string name, string label, Kind kind, Heightmap.Biome biome, bool on, float respawn,
            float spacing, string icon)
        {
            if (ById.TryGetValue(id, out var thing))
            {
                if (_building && Fresh.Add(id))
                {
                    // Known from the saved list: the world's data wins, the name (the setting's key) stays.
                    thing.Label = label;
                    thing.Kind = kind;
                    thing.Biomes = biome | thing.Learned;
                    thing.DefaultOn = on;
                    thing.RespawnMinutes = respawn;
                    thing.Spacing = spacing;
                    thing.IconItem = icon;
                }
                else thing.Biomes |= biome;
                return thing;
            }
            if (_building) Fresh.Add(id);
            thing = new Thing
            {
                Id = id, Name = name, Label = label, Kind = kind, Biomes = biome, DefaultOn = on, RespawnMinutes = respawn,
                Spacing = spacing, IconItem = icon,
            };
            ById[id] = thing;
            Named(name, thing);
            Named(label, thing);
            return thing;
        }

        private static void Named(string name, Thing thing)
        {
            if (!string.IsNullOrEmpty(name) && !ByName.ContainsKey(name)) ByName[name] = thing;
        }

        // A thing by a name a pin may carry: its label token, its setting name or a pickup's own name ("$item_pickable_bogiron").
        internal static Thing ForName(string name) => !string.IsNullOrEmpty(name) && ByName.TryGetValue(name, out var thing) ? thing : null;

        // Without rich text ("<color=orange>Lord Reto</color>" -> "Lord Reto").
        private static string NoTags(string text) => text == null ? null : Regex.Replace(text, "<[^>]*>", "");

        // "MushroomJotunPuffs" -> "Mushroom jotun puffs", "Crypt_Big" -> "Crypt big".
        private static string Pretty(string name)
        {
            var spaced = Regex.Replace(name.Replace('_', ' '), @"(?<=[a-z])(?=[A-Z])|(?<=[A-Za-z])(?=\d)", " ").Trim();
            spaced = Regex.Replace(spaced, @"\s+", " ");
            return spaced.Length == 0 ? name : char.ToUpperInvariant(spaced[0]) + spaced.Substring(1).ToLowerInvariant();
        }

        internal static Thing ForPrefab(int prefabHash) => ByPrefab.TryGetValue(prefabHash, out var thing) ? thing : null;
        internal static Thing ForLocation(string prefabName) => ByLocation.TryGetValue(prefabName, out var thing) ? thing : null;
        internal static Thing Get(string id) => id != null && ById.TryGetValue(id, out var thing) ? thing : null;

        // --- Settings per biome --------------------------------------------------------------------------------

        // The place of a biome in Biomes (a biome that is none of them counts as the Meadows); a loop, so nothing is boxed.
        private static int IndexOf(Heightmap.Biome biome)
        {
            for (var i = 0; i < Biomes.Length; i++)
                if (Biomes[i] == biome) return i;
            return 0;
        }

        private static void BindThing(Heightmap.Biome biome, Thing thing)
        {
            var index = IndexOf(biome);
            if (thing.Slots[index] != null) return;
            biome = Biomes[index];
            var slot = new Slot();
            var section = Section(biome);
            var name = thing.Name;
            if (!UsedKeys.Add(section + "|" + name))
            {
                name = $"{thing.Name} ({(thing.Kind == Kind.Plant ? "pickup" : thing.Kind.ToString().ToLowerInvariant())})";
                UsedKeys.Add(section + "|" + name);
            }
            // Rarely worth a pin (places with nothing to get, pickups that grow everywhere): only with "Advanced settings".
            var on = Defaults.On(biome, thing);
            var advanced = thing.Advanced || thing.Kind == Kind.Plant && !on;
            var toggle = _config.Bind(section, name, on, new ConfigDescription(
                $"Pin {thing.Name.ToLowerInvariant()} found in the {BiomeName(biome)}.", null,
                new ConfigurationManagerAttributes { CustomDrawer = e => Icons.DrawSwitch(e, thing, slot), IsAdvanced = advanced }));
            var color = _config.Bind(section, name + " color", thing.AutoColor, new ConfigDescription(
                $"Colour of the {thing.Name.ToLowerInvariant()} pin in the {BiomeName(biome)} with the white icon style.", null,
                new ConfigurationManagerAttributes { IsAdvanced = true }));
            if (NewDefaults && Defaults.Changed.TryGetValue(biome + "|" + thing.Id, out var now) && toggle.Value != now)
                toggle.Value = now; // still at the old default: the new one
            if (NewerDefaults && Defaults.ChangedLater.TryGetValue(biome + "|" + thing.Id, out var later) && !toggle.Value && later)
                toggle.Value = later;
            toggle.SettingChanged += (_, __) => Changed?.Invoke();
            var icon = _config.Bind(section, name + " icon", "", new ConfigDescription(
                $"Icon of the {thing.Name.ToLowerInvariant()} pin in the {BiomeName(biome)}: empty = by the icon style, \"game\" = " +
                "its game icon, else a white icon's name. Picked by clicking its icon in F1.", null,
                new ConfigurationManagerAttributes { Browsable = false }));
            color.SettingChanged += (_, __) => Recolored?.Invoke();
            icon.SettingChanged += (_, __) => Changed?.Invoke();
            slot.Toggle = toggle;
            slot.Color = color;
            slot.Icon = icon;
            thing.Slots[index] = slot;
        }

        // Things turn up in biomes their spawn table does not name (at a biome's edge, or inside a location); they get a
        // switch there too, kept in the saved list so it is in F1 from then on.
        internal static bool IsOn(Heightmap.Biome biome, Thing thing)
        {
            if (thing == null) return false;
            var index = IndexOf(biome);
            if (thing.Slots[index] == null)
            {
                BindThing(Biomes[index], thing);
                if ((thing.Biomes & Biomes[index]) == 0)
                {
                    thing.Biomes |= Biomes[index];
                    thing.Learned |= Biomes[index];
                    _cacheDirty = Built;
                }
            }
            return thing.Slots[index].Toggle.Value;
        }

        internal static Color ColorOf(Heightmap.Biome biome, Thing thing)
        {
            var slot = thing?.Slots[IndexOf(biome)];
            return slot != null ? slot.Color.Value : thing != null ? thing.AutoColor : Color.white;
        }

        // The icon picked for a thing in a biome ("" = by the icon style, "game", or a white icon's name).
        internal static string IconChoice(Heightmap.Biome biome, Thing thing) => thing?.Slots[IndexOf(biome)]?.Icon.Value;
    }

    // Where the dungeons, caves, camps and villages of the world stand (all of them, from the world's location list, also
    // the ones not visited yet), so the spawners at them can be left off the map: the place's own pin says enough.
    // The inside of a dungeon is at the same spot, high above, so its spawners count too.
    internal static class Lairs
    {
        private const float Cell = 64f, MaxRadius = 60f;
        private static readonly Dictionary<long, List<Vector4>> Grid = new Dictionary<long, List<Vector4>>(); // x, z, radius
        private static int _count = -1;

        private static long CellOf(float x, float z) => ((long)Mathf.FloorToInt(x / Cell) << 32) ^ (uint)Mathf.FloorToInt(z / Cell);

        internal static void Reset()
        {
            Grid.Clear();
            _count = -1;
        }

        internal static bool Near(Vector3 pos)
        {
            Refresh();
            var cx = Mathf.FloorToInt(pos.x / Cell);
            var cz = Mathf.FloorToInt(pos.z / Cell);
            for (var x = cx - 1; x <= cx + 1; x++)
                for (var z = cz - 1; z <= cz + 1; z++)
                {
                    if (!Grid.TryGetValue(((long)x << 32) ^ (uint)z, out var list)) continue;
                    foreach (var lair in list)
                    {
                        float dx = lair.x - pos.x, dz = lair.y - pos.z;
                        if (dx * dx + dz * dz <= lair.z * lair.z) return true;
                    }
                }
            return false;
        }

        private static void Refresh()
        {
            var zones = ZoneSystem.instance;
            if (zones == null || !Catalog.Built || zones.m_locationInstances.Count == _count) return;
            _count = zones.m_locationInstances.Count;
            Grid.Clear();
            foreach (var instance in zones.m_locationInstances.Values)
            {
                var location = instance.m_location;
                if (location == null || !Catalog.IsLair(Catalog.ForLocation(location.m_prefabName))) continue;
                var radius = Mathf.Clamp(location.m_exteriorRadius + 10f, 25f, MaxRadius);
                var cell = CellOf(instance.m_position.x, instance.m_position.z);
                if (!Grid.TryGetValue(cell, out var list)) Grid[cell] = list = new List<Vector4>();
                list.Add(new Vector4(instance.m_position.x, instance.m_position.z, radius, 0f));
            }
        }
    }

    // Read by Configuration Manager by field name.
    internal sealed class ConfigurationManagerAttributes
    {
        public int? Order;
        public bool? IsAdvanced;
        public bool? Browsable;
        public bool? HideDefaultButton;
        public Action<ConfigEntryBase> CustomDrawer;
    }
}
