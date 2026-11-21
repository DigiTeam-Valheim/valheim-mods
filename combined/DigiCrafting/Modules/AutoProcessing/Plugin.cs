// Every station that turns raw materials into processed ones: smelter, blast furnace, charcoal kiln, frigid kiln,
// spinning wheel, eitr refinery and windmill. Bigger capacity, adjustable speed, Shift+E to add as much as you carry,
// and automation from nearby chests (every part switchable per station):
// 1. Input (ore, wood, flax, barley, oats, soft tissue, ...) is topped up from chests in range (Input chest range; fuel
//    has its own Fuel chest range, the output the Chest range). Stations near the same
//    chests share it evenly (the shortest queue gets the next item); the windmill has a slider per grain for how much
//    of it is ground (50% oats = half stays oats, half becomes flour).
// 2. Fuel (coal, sap) is kept at what the queued input needs, from chests in range. The frigid kiln has no input: it
//    turns its fuel (ice) into liquid frost for as long as it has some, so its ice is kept full.
// 3. Output goes straight into a chest the way "Store Nearby" (Local Craft From Storage) would: a chest that already
//    holds it, else the nearest chest with room. With no room anywhere it drops at the station as usual. A station
//    that just loaded waits for the chests around it first (ChestWait), so what it made while you were away finds them.
// Sap extractors: the sap they collect goes into a nearby chest the same way, so they never fill up and stop.
// Away from them nothing runs (no game simulates an area nobody is in). The ancient root keeps its sap, though: it
// catches up on the time you were away when it loads, but stops at 50, and the extractors then pull 1 per minute. So
// the sap a root saved up goes into the chest at once, and what the root would have made after it was full is
// counted too (kept on the root as a number, only for roots with an extractor): together exactly what it would have
// made if you had stayed. Taken one stack per round.
// Beehives (what HoneyPlease did, safer): honey into a nearby chest, speed, any biome, no room needed, and the time to
// the next honey on hover. The values are set once per hive; "no room needed" also skips the game's cover raycasts.
// Siege engines, bathtubs and unknown modded stations stay vanilla.
// Chests are only touched when nobody has them open. Carts, ships and other players' private chests are skipped.
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AutoProcessing
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.autoprocessing";
        public const string Name = "Digi's Auto Processing";
        public const string Version = "1.1.1";

        public enum Kind { Furnace, Kiln, SpinningWheel, EitrRefinery, Windmill, FrostKiln }

        private sealed class Settings
        {
            public ConfigEntry<bool> Fill, Fuel, Empty;
            public ConfigEntry<float> Speed;
            // Per input (prefab name): how much of it Fill grinds, in %. An input without an entry is always taken.
            public readonly Dictionary<string, ConfigEntry<int>> Shares = new Dictionary<string, ConfigEntry<int>>();
            // This round: how many more of each limited input may go in (set by PlanShares).
            public readonly Dictionary<string, int> Budget = new Dictionary<string, int>();
        }

        // Beehives and birds' nests work the same way in the game (a "Beehive"); each has its own settings.
        private sealed class HiveSettings
        {
            public ConfigEntry<bool> ToChest, AnyBiome, NoRoomNeeded, Timer;
            public ConfigEntry<float> Range, Speed;
            public ConfigEntry<int> Capacity;
        }

        private static ConfigEntry<bool> _enabled, _bulkAdd, _spread, _sapToChest, _sapAtOnce, _sapWhileAway, _fullPower;
        private static ConfigEntry<float> _range, _inputRange, _fuelRange, _sapSpeed;
        private static ConfigEntry<int> _furnaceOre, _furnaceFuel, _kilnWood, _frostKilnIce, _machineMultiplier, _windmillMultiplier, _honey, _feathers;
        private static HiveSettings _bees, _nests;
        private static readonly Dictionary<Kind, Settings> PerKind = new Dictionary<Kind, Settings>();

        private static readonly Dictionary<Smelter, Kind> Stations = new Dictionary<Smelter, Kind>();
        private static readonly HashSet<Container> Chests = new HashSet<Container>();
        private static readonly List<Smelter> Dead = new List<Smelter>();

        // A station supplied this round, with the chests near it (looked up once, when first needed).
        private sealed class Feed
        {
            public Smelter Station;
            public ZNetView View;
            public Settings Settings;
            public List<Container> Chests, FuelChests; // input chests (Input chest range), fuel chests (Fuel chest range)
            public int Queued => View.GetZDO().GetInt(ZDOVars.s_queued);
            public bool Hungry => Settings.Fill != null && Settings.Fill.Value && Queued < Station.m_maxOre;
        }

        private static readonly List<Feed> Feeds = new List<Feed>();
        private static readonly List<Feed> Hungry = new List<Feed>();
        private static readonly HashSet<SapCollector> SapExtractors = new HashSet<SapCollector>();
        private static readonly AccessTools.FieldRef<SapCollector, ZNetView> ExtractorView = AccessTools.FieldRefAccess<SapCollector, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<SapCollector, ResourceRoot> ExtractorRoot = AccessTools.FieldRefAccess<SapCollector, ResourceRoot>("m_root");
        private static readonly AccessTools.FieldRef<ResourceRoot, ZNetView> RootView = AccessTools.FieldRefAccess<ResourceRoot, ZNetView>("m_nview");
        private const string SapAwayKey = "digi_sap_away"; // sap a root made after it was full, while nobody was there
        private const float SapAwayMax = 5000f;
        private static readonly HashSet<Beehive> Hives = new HashSet<Beehive>();
        private static readonly AccessTools.FieldRef<Beehive, ZNetView> HiveView = AccessTools.FieldRefAccess<Beehive, ZNetView>("m_nview");
        // Extractors and hives whose output found no chest: tried again after a while, not every round.
        private static readonly Dictionary<Object, float> RetryAt = new Dictionary<Object, float>();
        private const float RetryDelay = 30f;
        private const string HoneyPleaseGuid = "TastyChickenLegs.HoneyPlease";

        private static readonly AccessTools.FieldRef<Smelter, ZNetView> StationView = AccessTools.FieldRefAccess<Smelter, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<Smelter, EffectList> ProduceEffects = AccessTools.FieldRefAccess<Smelter, EffectList>("m_produceEffects");
        private static readonly AccessTools.FieldRef<Smelter, float> AddedOreTime = AccessTools.FieldRefAccess<Smelter, float>("m_addedOreTime");
        private static readonly System.Action<Smelter, bool> SetAnimation =
            AccessTools.MethodDelegate<System.Action<Smelter, bool>>(AccessTools.Method(typeof(Smelter), "SetAnimation"));
        private static readonly AccessTools.FieldRef<Container, ZNetView> ChestView = AccessTools.FieldRefAccess<Container, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<Container, Piece> ChestPiece = AccessTools.FieldRefAccess<Container, Piece>("m_piece");

        private float _timer;
        private static BepInEx.Logging.ManualLogSource _log;
        private static float _nextDropLog;


        private void Awake()
        {
            StoreRules.BindRules(Config); // the shared Chest rules (StoreRules)
            _log = Logger;
            var migrated = MigrateConfig();
            _enabled = Config.Bind("1 - General", "Automation enabled", true,
                "Stations fill themselves and store what they make, using the chests near them. Off = only the capacity, speed and Shift+E changes.");
            _range = Config.Bind("1 - General", "Chest range (m)", 10f,
                new ConfigDescription("Output: what the stations make goes into chests up to this many meters away (also the sap " +
                    "extractors' sap).", new AcceptableValueRange<float>(1f, 30f)));
            _inputRange = Config.Bind("1 - General", "Input chest range (m)", 10f,
                new ConfigDescription("Input (ore, wood, flax, grain, soft tissue, ...) is taken from chests up to this many meters away.",
                    new AcceptableValueRange<float>(1f, 30f)));
            _fuelRange = Config.Bind("1 - General", "Fuel chest range (m)", 10f,
                new ConfigDescription("Fuel (coal, sap, ice) is taken from chests up to this many meters away.", new AcceptableValueRange<float>(1f, 30f)));
            // Input and fuel had the one Chest range before (2026-11-19): they start from it, so nothing changes until moved.
            if (FirstTime("input and fuel chest ranges"))
            {
                _inputRange.Value = _range.Value;
                _fuelRange.Value = _range.Value;
            }
            _bulkAdd = Config.Bind("1 - General", "Shift+E adds max", true,
                "Hold Shift while adding something (ore, coal, barley, ...) to put in as much as fits from your inventory.");
            _spread = Config.Bind("1 - General", "Spread input evenly", true,
                "Stations near the same chests share what is in them: the station with the shortest queue gets the next " +
                "item, so they all work at once (many windmills, say) instead of the first one filling up while the " +
                "others wait. Off = each station fills up in turn.");

            _furnaceOre = Config.Bind("2 - Capacity", "Furnace max ore", 50,
                new ConfigDescription("How much ore a smelter or blast furnace holds (normal 10). Needs a restart.", new AcceptableValueRange<int>(1, 500)));
            _furnaceFuel = Config.Bind("2 - Capacity", "Furnace max fuel", 100,
                new ConfigDescription("How much coal a smelter or blast furnace holds (normal 20). Needs a restart.", new AcceptableValueRange<int>(1, 500)));
            _kilnWood = Config.Bind("2 - Capacity", "Kiln max wood", 100,
                new ConfigDescription("How much wood a charcoal kiln holds (normal 25). Needs a restart.", new AcceptableValueRange<int>(1, 500)));
            _frostKilnIce = Config.Bind("2 - Capacity", "Frigid kiln max ice", 100,
                new ConfigDescription("How much ice a frigid kiln holds (normal 25). Needs a restart.", new AcceptableValueRange<int>(5, 500)));
            _machineMultiplier = Config.Bind("2 - Capacity", "Spinning wheel and refinery multiplier", 2,
                new ConfigDescription("How much a spinning wheel and eitr refinery hold, times this (1 = normal). Needs a restart.", new AcceptableValueRange<int>(1, 10)));
            _windmillMultiplier = Config.Bind("2 - Capacity", "Windmill multiplier", 2,
                new ConfigDescription("How much grain a windmill holds, times this (1 = normal 50). Needs a restart.", new AcceptableValueRange<int>(1, 10)));
            _honey = Config.Bind("2 - Capacity", "Beehive max honey", 50,
                new ConfigDescription("How much honey a beehive holds before you collect it (normal 4). Needs a restart.", new AcceptableValueRange<int>(1, 500)));
            _feathers = Config.Bind("2 - Capacity", "Bird nest max feathers", 50,
                new ConfigDescription("How many feathers a birds' nest holds before you collect them (normal 4). Needs a restart.", new AcceptableValueRange<int>(1, 500)));

            PerKind[Kind.Furnace] = Bind("3 - Furnaces", 1f, true, true, true, "Ore and scrap", "Coal", "metal");
            PerKind[Kind.Kiln] = Bind("4 - Charcoal kiln", 2f, false, null, true, "Wood", null, "coal");
            PerKind[Kind.FrostKiln] = Bind("4 - Frigid kiln", 1f, null, true, true, null, "Ice", "liquid frost");
            PerKind[Kind.SpinningWheel] = Bind("5 - Spinning wheel", 1f, true, null, true, "Flax", null, "linen thread");
            PerKind[Kind.EitrRefinery] = Bind("6 - Eitr refinery", 1f, true, true, true, "Soft tissue", "Sap", "eitr");
            PerKind[Kind.Windmill] = Bind("7 - Windmill", 1f, true, null, true, "Grain (barley, oat seeds, oats)", null, "flour");
            BindShare(Kind.Windmill, "7 - Windmill", "Barley into flour (%)", "Barley", "barley", "barley flour", "keep it all to plant", 100);
            // Oats: half the seeds stay for planting, and most oats stay oats (oat milk, oatmeal) unless set otherwise.
            BindShare(Kind.Windmill, "7 - Windmill", "Oat seeds into oats (%)", "OatSeeds", "oat seeds", "oats (and the oat flour made from them)",
                "keep them all to plant", 50);
            BindShare(Kind.Windmill, "7 - Windmill", "Oats into flour (%)", "Oat", "oats", "oat flour", "keep them all, e.g. for oat milk and oatmeal", 10);
            _fullPower = Config.Bind("7 - Windmill", "Ignore wind and cover", false,
                "Windmills make flour at full power, whatever the wind and whatever stands around them. Off = the game's rule: less in a light wind and when they are sheltered. The blades keep following the real wind either way.");
            _sapSpeed = Config.Bind("8 - Sap extractor", "Speed multiplier", 1f, new ConfigDescription(
                "How fast sap extractors fill: 2 = twice as fast, 1 = normal. The root runs dry that much faster too. Needs a restart.",
                new AcceptableValueRange<float>(0.25f, 10f)));
            _sapToChest = Config.Bind("8 - Sap extractor", "Output to chest", true,
                "The sap goes into a nearby chest: one that already has sap, otherwise the nearest one with room. Off = you collect it by hand.");
            _sapAtOnce = Config.Bind("8 - Sap extractor", "Take root sap at once", true,
                "When you get there, the sap an ancient root saved up while you were away goes into the chest right away " +
                "(a stack per few seconds), instead of the extractor pulling 1 per minute. Needs Output to chest.");
            _sapWhileAway = Config.Bind("8 - Sap extractor", "Count sap while away", true,
                "A root stops at 50 sap (about 5.5 hours). On: what it would have made after that while you were away is " +
                "counted too and goes into the chest when you get there, as if you had stayed (about 9 sap per hour per " +
                "root). Only for roots with an extractor.");
            _bees = BindHives("9 - Beehives", "beehive", "beehives", "honey", "honey", _honey);
            _nests = BindHives("9 - Bird nests", "birds' nest", "birds' nests", "feathers", "feather", _feathers);
            if (migrated) TakeWindmillSettings();
            PatchModule(typeof(Plugin));
        }

        private HiveSettings BindHives(string section, string one, string many, string product, string single, ConfigEntry<int> capacity) => new HiveSettings
        {
            Capacity = capacity,
            ToChest = Config.Bind(section, "Output to chest", true,
                $"Puts the {product} into a nearby chest: one that already has {product}, otherwise the nearest one with room. Off = you collect it by hand."),
            Range = Config.Bind(section, "Chest range (m)", 20f, new ConfigDescription(
                $"Chests up to this many meters from a {one} are used ({many} often stand a bit away from the house).", new AcceptableValueRange<float>(1f, 40f))),
            Speed = Config.Bind(section, "Speed multiplier", 2f, new ConfigDescription(
                $"How fast {many} make {product}: 2 = twice as fast (one every 10 minutes instead of 20), 1 = normal. Needs a restart.", new AcceptableValueRange<float>(0.25f, 10f))),
            AnyBiome = Config.Bind(section, "Works in any biome", true,
                $"{char.ToUpper(many[0]) + many.Substring(1)} work in every biome (normally only in the Meadows, Black Forest and Plains). Needs a restart."),
            NoRoomNeeded = Config.Bind(section, "No room needed", true,
                $"{char.ToUpper(many[0]) + many.Substring(1)} also work when they are built in or closed in (normally they need open space). Lighter too: the game no longer checks the space around each one every 10 seconds. Needs a restart."),
            Timer = Config.Bind(section, "Show time to next", true,
                $"Aiming at a {one} shows when the next {single} is ready."),
        };

        // A birds' nest is the game's beehive piece that makes feathers; anything else (also modded hives) counts as a beehive.
        private static HiveSettings For(Beehive hive) =>
            hive.name.IndexOf("birdnest", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
            hive.m_honeyItem?.m_itemData?.m_shared?.m_name == "$item_feathers" ? _nests : _bees;

        // fuel null: the station burns no fuel, so there is no fuel setting. fill null: it has no input (the frigid kiln
        // makes its product from its fuel alone), so there is no fill setting and its fuel is kept full.
        private Settings Bind(string section, float speed, bool? fill, bool? fuel, bool empty, string input, string fuelName, string output) => new Settings
        {
            Speed = Config.Bind(section, "Speed multiplier", speed,
                new ConfigDescription("How fast this station works: 2 = twice as fast, 1 = normal. Needs a restart.", new AcceptableValueRange<float>(0.25f, 10f))),
            Fill = fill.HasValue ? Config.Bind(section, "Fill from chests", fill.Value, $"{input} is topped up from nearby chests."
                + (section.Contains("Charcoal") ? " Off by default: it would also burn your building wood." : "")) : null,
            Fuel = fuel.HasValue ? Config.Bind(section, "Fuel from chests", fuel.Value, input == null
                ? $"{fuelName} is added from nearby chests, keeping the station full."
                : $"{fuelName} is added from nearby chests, as much as the queued {input.ToLowerInvariant()} needs.") : null,
            Empty = Config.Bind(section, "Output to chest", empty,
                $"The {output} goes into a nearby chest: one that already has it, otherwise the nearest one with room. Off = it drops on the ground."),
        };

        // A share's default was 100 before Crafting and Automation 1.0.14: a lower one is set once for everyone still at 100.
        private void BindShare(Kind kind, string section, string key, string prefab, string what, string product, string none, int share)
        {
            var entry = PerKind[kind].Shares[prefab] = Config.Bind(section, key, share, new ConfigDescription(
                $"How much of the {what} in the chests near it the windmill turns into {product}. 100 = all of it, 50 = " +
                $"until there is as much {product} as {what} left, 0 = none ({none}). What the windmills already hold " +
                "counts too. Adding by hand and Shift+E are not affected.", new AcceptableValueRange<int>(0, 100)));
            if (share != 100 && FirstTime(prefab + " share " + share) && entry.Value == 100) entry.Value = share;
        }

        private static bool InputAllowed(Settings settings, string prefab) =>
            !settings.Budget.TryGetValue(prefab, out var left) || left > 0;

        // For each limited input of a station kind: how many more may go in this round, so that what has been ground
        // (in the queues, and everything made from it in the chests) stays at the share of the whole. Counted over the
        // chests near every such station this game supplies, and their queues.
        private static void PlanShares(Settings settings)
        {
            settings.Budget.Clear();
            Smelter sample = null;
            var chests = new HashSet<Container>();
            foreach (var feed in Feeds)
            {
                if (feed.Settings != settings) continue;
                sample = sample ?? feed.Station;
                feed.Chests = feed.Chests ?? UsableChests(feed.Station.transform.position, _inputRange.Value); // the input chests
                foreach (var chest in feed.Chests) chests.Add(chest);
            }
            if (sample == null) return;
            foreach (var share in settings.Shares)
            {
                if (share.Value.Value >= 100) continue;
                var input = share.Key;
                var made = MadeFrom(sample, input);
                int left = 0, ground = 0;
                foreach (var chest in chests)
                    foreach (var item in chest.GetInventory().GetAllItems())
                    {
                        var name = StoreRules.NameOf(item.m_dropPrefab);
                        if (name == input) left += item.m_stack;
                        else if (name != null && made.Contains(name)) ground += item.m_stack;
                    }
                foreach (var feed in Feeds)
                {
                    if (feed.Settings != settings) continue;
                    var zdo = feed.View.GetZDO();
                    var queued = zdo.GetInt(ZDOVars.s_queued);
                    for (var i = 0; i < queued; i++)
                    {
                        var name = zdo.GetString("item" + i);
                        if (name == input || made.Contains(name)) ground++;
                    }
                }
                settings.Budget[input] = Mathf.Max(0, Mathf.FloorToInt(share.Value.Value / 100f * (left + ground)) - ground);
            }
        }

        // Everything the station makes from this input, step after step (oat seeds: oats, then oat flour).
        private static HashSet<string> MadeFrom(Smelter station, string input)
        {
            var made = new HashSet<string>();
            var next = new Queue<string>();
            next.Enqueue(input);
            while (next.Count > 0)
            {
                var from = next.Dequeue();
                foreach (var c in station.m_conversion)
                    if (c.m_from != null && c.m_to != null && c.m_from.gameObject.name == from && made.Add(c.m_to.gameObject.name))
                        next.Enqueue(c.m_to.gameObject.name);
            }
            made.Remove(input);
            return made;
        }

        // Settings from Auto Smelter (same entries) and Auto Windmill (windmill entries) are carried over once.
        private bool MigrateConfig()
        {
            if (System.IO.File.Exists(Config.ConfigFilePath)) return false;
            var smelter = System.IO.Path.Combine(Paths.ConfigPath, "pieter.valheim.autosmelter.cfg");
            if (System.IO.File.Exists(smelter))
            {
                System.IO.File.Copy(smelter, Config.ConfigFilePath);
                Config.Reload();
            }
            return true;
        }

        private void TakeWindmillSettings()
        {
            var file = System.IO.Path.Combine(Paths.ConfigPath, "pieter.valheim.autowindmill.cfg");
            if (!System.IO.File.Exists(file)) return;
            var old = new ConfigFile(file, false) { SaveOnConfigSet = false };
            var w = PerKind[Kind.Windmill];
            _windmillMultiplier.Value = old.Bind("Windmill", "Capacity multiplier", 2).Value;
            w.Speed.Value = old.Bind("Windmill", "Speed multiplier", 1f).Value;
            w.Fill.Value = old.Bind("General", "Fill from chests", true).Value;
            w.Empty.Value = old.Bind("General", "Store flour in chest", true).Value;
        }

        // --- Station setup -------------------------------------------------------------------------------------

        private static Kind? Classify(Smelter s)
        {
            if (s.m_windmill != null || s.GetComponentInParent<Windmill>() != null) return Kind.Windmill;
            if (s.GetComponentInParent<SiegeMachine>() != null) return null;
            var name = Utils.GetPrefabName(s.transform.root.gameObject).ToLowerInvariant();
            if (name == "charcoal_kiln") return Kind.Kiln;
            if (name == "piece_frostkiln") return Kind.FrostKiln;
            if (name == "piece_spinningwheel") return Kind.SpinningWheel;
            if (name == "eitrrefinery") return Kind.EitrRefinery;
            if (name == "smelter" || name == "blastfurnace") return Kind.Furnace;
            if (name.Contains("bathtub") || name.Contains("kiln") || name.StartsWith("dev")) return null;
            // Other fuel-burning furnaces (e.g. from mods) count as furnaces.
            return s.m_fuelItem != null && s.m_maxFuel > 0 && s.m_maxOre > 0 ? Kind.Furnace : (Kind?)null;
        }

        [HarmonyPatch(typeof(Smelter), "Awake")]
        [HarmonyPostfix]
        private static void SmelterAwake(Smelter __instance)
        {
            var kind = Classify(__instance);
            if (kind == null) return;
            Stations[__instance] = kind.Value;
            switch (kind.Value)
            {
                case Kind.Furnace:
                    __instance.m_maxOre = _furnaceOre.Value;
                    __instance.m_maxFuel = _furnaceFuel.Value;
                    break;
                case Kind.Kiln:
                    __instance.m_maxOre = _kilnWood.Value;
                    break;
                case Kind.FrostKiln:
                    if (__instance.m_maxFuel > 0) __instance.m_maxFuel = _frostKilnIce.Value;
                    break;
                case Kind.Windmill:
                    __instance.m_maxOre *= _windmillMultiplier.Value;
                    break;
                default:
                    __instance.m_maxOre *= _machineMultiplier.Value;
                    if (__instance.m_fuelItem != null && __instance.m_maxFuel > 0) __instance.m_maxFuel *= _machineMultiplier.Value;
                    break;
            }
            var speed = PerKind[kind.Value].Speed.Value;
            if (speed > 0f && __instance.m_secPerProduct > 0f) __instance.m_secPerProduct /= speed;
        }

        // HoneyPlease (its job is in 9 - Beehives now) installed too: it goes off, ours runs.
        private void Start() => SwitchOffReplaced(HoneyPleaseGuid, "HoneyPlease");

        [HarmonyPatch(typeof(Beehive), "Awake")]
        [HarmonyPostfix]
        private static void BeehiveAwake(Beehive __instance)
        {
            Hives.Add(__instance);
            var settings = For(__instance);
            __instance.m_maxHoney = settings.Capacity.Value;
            if (settings.Speed.Value > 0f) __instance.m_secPerUnit /= settings.Speed.Value;
            if (settings.AnyBiome.Value) __instance.m_biome = (Heightmap.Biome)~0;
            if (settings.NoRoomNeeded.Value) __instance.m_maxCover = 0f; // 0: the game skips its cover check entirely
        }

        // Extract drops what the hive or nest holds as whole stacks. The game drops one item per unit in a tower going
        // up, which scatters badly with a bigger capacity (and is one physics object each).
        [HarmonyPatch(typeof(Beehive), "RPC_Extract")]
        [HarmonyPrefix]
        private static bool Extract(Beehive __instance)
        {
            if (__instance.m_honeyItem == null || __instance.m_spawnPoint == null) return true;
            var view = HiveView(__instance);
            if (view == null || !view.IsValid() || !view.IsOwner()) return true;
            var zdo = view.GetZDO();
            var level = zdo.GetInt(ZDOVars.s_level);
            if (level <= 0) return false;
            __instance.m_spawnEffect.Create(__instance.m_spawnPoint.position, Quaternion.identity);
            var total = level * Game.instance.ScaleDrops(__instance.m_honeyItem.m_itemData, 1);
            var perStack = Mathf.Max(1, __instance.m_honeyItem.m_itemData.m_shared.m_maxStackSize);
            for (var i = 0; total > 0; i++)
            {
                var stack = Mathf.Min(total, perStack);
                var offset = Random.insideUnitCircle * 0.3f;
                var position = __instance.m_spawnPoint.position + new Vector3(offset.x, 0.25f * i, offset.y);
                Object.Instantiate(__instance.m_honeyItem, position, Quaternion.identity).GetComponent<ItemDrop>()?.SetStack(stack);
                total -= stack;
            }
            zdo.Set(ZDOVars.s_level, 0);
            return false;
        }

        // The game's text keeps its [E] line; the time to the next honey (or feather, for a nest) goes below it.
        [HarmonyPatch(typeof(Beehive), nameof(Beehive.GetHoverText))]
        [HarmonyPostfix]
        private static void BeehiveHover(Beehive __instance, ref string __result)
        {
            if (!For(__instance).Timer.Value || string.IsNullOrEmpty(__result) || ZNet.instance == null) return;
            var view = HiveView(__instance);
            if (view == null || !view.IsValid() || !PrivateArea.CheckAccess(__instance.transform.position, 0f, false)) return;
            var zdo = view.GetZDO();
            var product = __instance.m_honeyItem != null
                ? Localization.instance.Localize(__instance.m_honeyItem.m_itemData.m_shared.m_name).ToLowerInvariant()
                : "output";
            if (zdo.GetInt(ZDOVars.s_level) >= __instance.m_maxHoney)
            {
                __result += $"\n<color=orange>Full: empty it to keep making {product}</color>";
                return;
            }
            // The game adds the passed time every 10 seconds while the bees work; much older means they are not
            // working here (wrong biome, no room), so there is no time to show.
            var now = ZNet.instance.GetTime().Ticks;
            var since = (now - zdo.GetLong(ZDOVars.s_lastTime, now)) / (double)System.TimeSpan.TicksPerSecond;
            if (since < 0 || since > 30) return;
            var left = __instance.m_secPerUnit - zdo.GetFloat(ZDOVars.s_product) - since;
            __result += $"\nNext {product} in <color=yellow>{FormatTime(System.Math.Max(0, left))}</color>";
        }

        private static string FormatTime(double seconds)
        {
            var t = System.TimeSpan.FromSeconds(seconds);
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }

        [HarmonyPatch(typeof(SapCollector), "Awake")]
        [HarmonyPostfix]
        private static void ExtractorAwake(SapCollector __instance)
        {
            SapExtractors.Add(__instance);
            // The time per sap, set once per extractor as it loads (like the beehives' speed): the game's own filling and
            // "Count sap while away" both go by it.
            if (_sapSpeed.Value > 0f && _sapSpeed.Value != 1f) __instance.m_secPerUnit /= _sapSpeed.Value;
        }

        // Before a root catches up on the time since its last update (it then stops at its maximum): keep what would have
        // gone over the maximum, for roots that have an extractor.
        [HarmonyPatch(typeof(ResourceRoot), "UpdateTick")]
        [HarmonyPrefix]
        private static void RootCatchUp(ResourceRoot __instance)
        {
            if (!_enabled.Value || !_sapWhileAway.Value) return;
            var view = RootView(__instance);
            if (view == null || !view.IsValid() || !view.IsOwner() || !HasExtractor(__instance)) return;
            var zdo = view.GetZDO();
            var now = ZNet.instance.GetTime().Ticks;
            var seconds = (now - zdo.GetLong(ZDOVars.s_lastTime, now)) / (double)System.TimeSpan.TicksPerSecond;
            if (seconds <= 0.0) return;
            var over = __instance.GetLevel() + (float)(__instance.m_regenPerSec * seconds) - __instance.m_maxLevel;
            if (over > 0f) zdo.Set(SapAwayKey, Mathf.Min(SapAwayMax, zdo.GetFloat(SapAwayKey) + over));
        }

        // An extractor on this root: one that already found it, or one standing on it (before its first update).
        private static bool HasExtractor(ResourceRoot root)
        {
            foreach (var extractor in SapExtractors)
            {
                if (extractor == null) continue;
                var found = ExtractorRoot(extractor);
                if (found == root) return true;
                if (found == null && extractor.m_mustConnectTo != null &&
                    (extractor.transform.position - root.transform.position).sqrMagnitude < 30f * 30f) return true;
            }
            return false;
        }

        [HarmonyPatch(typeof(Container), "Awake")]
        [HarmonyPostfix]
        private static void ChestAwake(Container __instance) => Chests.Add(__instance);

        // --- Shift+E: add as much as possible ------------------------------------------------------------------

        private static bool ShiftHeld() => ZInput.GetKey(KeyCode.LeftShift) || ZInput.GetKey(KeyCode.RightShift);

        [HarmonyPatch(typeof(Smelter), "OnAddOre")]
        [HarmonyPrefix]
        private static bool AddOre(Smelter __instance, Humanoid user, ItemDrop.ItemData item, ref bool __result)
        {
            if (!_bulkAdd.Value || user == null || !ShiftHeld() || !Stations.ContainsKey(__instance)) return true;
            var view = StationView(__instance);
            var inv = user.GetInventory();
            if (item == null)
                item = __instance.m_conversion.Where(c => c.m_from != null)
                    .Select(c => inv.GetItem(c.m_from.m_itemData.m_shared.m_name)).FirstOrDefault(i => i != null);
            if (item == null || item.m_dropPrefab == null ||
                !__instance.m_conversion.Any(c => c.m_from != null && c.m_from.gameObject.name == item.m_dropPrefab.name))
                return true; // vanilla shows the right message
            var room = __instance.m_maxOre - view.GetZDO().GetInt(ZDOVars.s_queued);
            var have = inv.CountItems(item.m_shared.m_name);
            var amount = Mathf.Min(room, have);
            if (amount <= 0) return true;
            var prefab = item.m_dropPrefab.name;
            var cheated = item.m_cheated;
            inv.RemoveItem(item.m_shared.m_name, amount);
            for (var i = 0; i < amount; i++) view.InvokeRPC("RPC_AddOre", prefab, cheated);
            AddedOreTime(__instance) = Time.time;
            if (__instance.m_addOreAnimationDuration > 0f) SetAnimation(__instance, true);
            user.Message(MessageHud.MessageType.Center, $"$msg_added {item.m_shared.m_name} x{amount}");
            __result = true;
            return false;
        }

        [HarmonyPatch(typeof(Smelter), "OnAddFuel")]
        [HarmonyPrefix]
        private static bool AddFuel(Smelter __instance, Humanoid user, ItemDrop.ItemData item, ref bool __result)
        {
            if (!_bulkAdd.Value || user == null || !ShiftHeld() || __instance.m_fuelItem == null || !Stations.ContainsKey(__instance)) return true;
            var fuelName = __instance.m_fuelItem.m_itemData.m_shared.m_name;
            if (item != null && item.m_shared.m_name != fuelName) return true;
            var view = StationView(__instance);
            var room = __instance.m_maxFuel - Mathf.CeilToInt(view.GetZDO().GetFloat(ZDOVars.s_fuel));
            var amount = Mathf.Min(room, user.GetInventory().CountItems(fuelName));
            if (amount <= 0) return true;
            user.GetInventory().RemoveItem(fuelName, amount);
            for (var i = 0; i < amount; i++) view.InvokeRPC("RPC_AddFuel");
            user.Message(MessageHud.MessageType.Center, $"$msg_added {fuelName} x{amount}");
            __result = true;
            return false;
        }

        [HarmonyPatch(typeof(Smelter), "OnHoverAddOre")]
        [HarmonyPostfix]
        private static void HoverOre(Smelter __instance, ref string __result) => __result = Hint(__instance, __result);

        [HarmonyPatch(typeof(Smelter), "OnHoverAddFuel")]
        [HarmonyPostfix]
        private static void HoverFuel(Smelter __instance, ref string __result) => __result = Hint(__instance, __result);

        private static string Hint(Smelter s, string text) =>
            !_bulkAdd.Value || string.IsNullOrEmpty(text) || !Stations.ContainsKey(s) ? text
                : text + Localization.instance.Localize("\n[<color=yellow><b>Shift + $KEY_Use</b></color>] Add max");

        // --- Automation ----------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(Smelter), "UpdateSmelter")]
        [HarmonyPrefix]
        private static bool WaitForChests(Smelter __instance)
        {
            if (!_enabled.Value || !Stations.TryGetValue(__instance, out var kind) || !PerKind[kind].Empty.Value) return true;
            var view = StationView(__instance);
            if (view == null || !view.IsValid() || !view.IsOwner()) return true;
            return !ChestWait.ShouldWait(__instance, _range.Value);
        }

        // A station without a windmill works at full power: the game's update does not see the windmill while it runs.
        // Only the production changes; the blades, their sound and what they can hit still follow the real wind.
        private static Windmill _setAside;

        [HarmonyPatch(typeof(Smelter), "UpdateSmelter")]
        [HarmonyPrefix]
        private static void SetWindAside(Smelter __instance)
        {
            if (!_fullPower.Value || __instance.m_windmill == null) return;
            _setAside = __instance.m_windmill;
            __instance.m_windmill = null;
        }

        [HarmonyPatch(typeof(Smelter), "UpdateSmelter")]
        [HarmonyFinalizer]
        private static void PutWindBack(Smelter __instance)
        {
            if (_setAside == null) return;
            __instance.m_windmill = _setAside;
            _setAside = null;
        }

        // Output: the station drops its product here (only on the game that controls it). Store it in a chest instead.
        [HarmonyPatch(typeof(Smelter), "Spawn")]
        [HarmonyPrefix]
        private static bool Spawn(Smelter __instance, string ore, int stack)
        {
            if (!_enabled.Value || stack <= 0 || !Stations.TryGetValue(__instance, out var kind) || !PerKind[kind].Empty.Value) return true;
            try
            {
                // As the game finds it: a conversion without a source (the frigid kiln's) matches any ore name.
                var conversion = __instance.m_conversion.FirstOrDefault(c => c.m_from == null || c.m_from.gameObject.name == ore);
                if (conversion?.m_to == null) return true;
                var prefab = conversion.m_to.gameObject;
                var chest = StoreRules.PickOutputChest(UsableChests(__instance.transform.position), prefab, stack);
                if (chest == null)
                {
                    // no room (or no chest with it, under "Only store into chests that already have it"): drop it like vanilla
                    if (Time.time >= _nextDropLog)
                    {
                        _nextDropLog = Time.time + 60f;
                        _log.LogInfo($"No chest {(StoreRules.OnlyMatching ? "that already has" : "with room for")} {stack} {prefab.name} within {_range.Value} m of the station at {__instance.transform.position}: it drops.");
                    }
                    return true;
                }
                chest.GetInventory().AddItem(prefab, stack);
                ProduceEffects(__instance)?.Create(__instance.transform.position, __instance.transform.rotation);
                return false;
            }
            catch (System.Exception)
            {
                return true;
            }
        }

        private void Update()
        {
            if (!_enabled.Value || Player.m_localPlayer == null || ZNet.instance == null) return;
            _timer -= Time.deltaTime;
            if (_timer > 0f) return;
            _timer = 2f;
            // Ultimate Builder's creative world: its stations stand still, nothing is taken from chests or filled there.
            if (DigiCore.CreativeWorld.Active) return;
            // One save per changed chest for the whole round (StoreRules.HoldSaves).
            StoreRules.HoldSaves();
            try
            {
                Chests.RemoveWhere(c => c == null);
                Dead.Clear();
                Feeds.Clear();
                foreach (var pair in Stations)
                {
                    var station = pair.Key;
                    if (station == null) { Dead.Add(station); continue; }
                    var settings = PerKind[pair.Value];
                    if ((settings.Fill == null || !settings.Fill.Value) && (settings.Fuel == null || !settings.Fuel.Value)) continue;
                    var view = StationView(station);
                    if (view == null || !view.IsValid() || !view.IsOwner()) continue;
                    Feeds.Add(new Feed { Station = station, View = view, Settings = settings });
                }
                foreach (var dead in Dead) Stations.Remove(dead);
                FillStations();
                foreach (var feed in Feeds)
                {
                    try
                    {
                        AddFuel(feed);
                    }
                    catch (System.Exception e)
                    {
                        Logger.LogWarning($"Station at {feed.Station.transform.position}: {e.Message}");
                    }
                }
                if (_sapToChest.Value) StoreSap();
                if ((_bees.ToChest.Value || _nests.ToChest.Value)) StoreHoney();
                if (RetryAt.Count > 0)
                    foreach (var gone in RetryAt.Keys.Where(k => k == null).ToList()) RetryAt.Remove(gone);
            }
            finally
            {
                StoreRules.SaveHeld();
            }
        }

        private static bool Waiting(Object source) => RetryAt.TryGetValue(source, out var at) && Time.time < at;

        // Sap extractors (on the game that controls them): move their collected sap into a chest, as "Extract" would
        // drop it, and empty them so they keep collecting.
        private void StoreSap()
        {
            SapExtractors.RemoveWhere(e => e == null);
            foreach (var extractor in SapExtractors)
            {
                var view = ExtractorView(extractor);
                if (view == null || !view.IsValid() || !view.IsOwner() || extractor.m_spawnItem == null) continue;
                var zdo = view.GetZDO();
                var level = zdo.GetInt(ZDOVars.s_level);
                if (level <= 0) continue;
                try
                {
                    var prefab = extractor.m_spawnItem.gameObject;
                    var amount = level * Game.instance.ScaleDrops(extractor.m_spawnItem.m_itemData, 1);
                    if (Waiting(extractor)) continue;
                    var chest = StoreRules.PickOutputChest(UsableChests(extractor.transform.position), prefab, amount);
                    if (chest == null || !chest.GetInventory().AddItem(prefab, amount))
                    {
                        RetryAt[extractor] = Time.time + RetryDelay; // no room: it waits in the extractor
                        continue;
                    }
                    RetryAt.Remove(extractor);
                    zdo.Set(ZDOVars.s_level, 0);
                    view.InvokeRPC(ZNetView.Everybody, "RPC_UpdateEffects");
                }
                catch (System.Exception e)
                {
                    Logger.LogWarning($"Sap extractor at {extractor.transform.position}: {e.Message}");
                }
            }
            if (_sapAtOnce.Value)
                foreach (var extractor in SapExtractors) TakeRootSap(extractor);
        }

        // The sap a root saved up (and what it made after it was full while you were away) into a chest, a stack at a time.
        private void TakeRootSap(SapCollector extractor)
        {
            var view = extractor != null ? ExtractorView(extractor) : null;
            var root = view != null && view.IsValid() && view.IsOwner() ? ExtractorRoot(extractor) : null;
            var rootView = root != null ? RootView(root) : null;
            if (rootView == null || !rootView.IsValid() || extractor.m_spawnItem == null || Waiting(extractor)) return;
            try
            {
                var rootZdo = rootView.GetZDO();
                var stock = Mathf.Max(0, Mathf.FloorToInt(root.GetLevel() - 0.01f));
                var away = rootView.IsOwner() ? Mathf.FloorToInt(rootZdo.GetFloat(SapAwayKey)) : 0;
                var item = extractor.m_spawnItem.m_itemData;
                var take = Mathf.Min(stock + away, Mathf.Max(1, item.m_shared.m_maxStackSize));
                if (take <= 0) return;
                var amount = take * Game.instance.ScaleDrops(item, 1);
                var prefab = extractor.m_spawnItem.gameObject;
                var chest = StoreRules.PickOutputChest(UsableChests(extractor.transform.position), prefab, amount);
                if (chest == null || !chest.GetInventory().AddItem(prefab, amount))
                {
                    RetryAt[extractor] = Time.time + RetryDelay; // no room: it stays on the root
                    return;
                }
                var fromRoot = Mathf.Min(stock, take);
                if (fromRoot > 0) root.Drain(fromRoot);
                if (take > fromRoot) rootZdo.Set(SapAwayKey, Mathf.Max(0f, rootZdo.GetFloat(SapAwayKey) - (take - fromRoot)));
            }
            catch (System.Exception e)
            {
                Logger.LogWarning($"Ancient root at {root.transform.position}: {e.Message}");
            }
        }

        // Beehives and birds' nests (on the game that controls them): their honey or feathers into a chest, as
        // "Extract" would drop them.
        private void StoreHoney()
        {
            Hives.RemoveWhere(h => h == null);
            foreach (var hive in Hives)
            {
                var settings = For(hive);
                if (!settings.ToChest.Value) continue;
                var view = HiveView(hive);
                if (view == null || !view.IsValid() || !view.IsOwner() || hive.m_honeyItem == null) continue;
                var zdo = view.GetZDO();
                var level = zdo.GetInt(ZDOVars.s_level);
                if (level <= 0 || Waiting(hive)) continue;
                try
                {
                    var prefab = hive.m_honeyItem.gameObject;
                    var amount = level * Game.instance.ScaleDrops(hive.m_honeyItem.m_itemData, 1);
                    var chest = StoreRules.PickOutputChest(UsableChests(hive.transform.position, settings.Range.Value), prefab, amount);
                    if (chest == null || !chest.GetInventory().AddItem(prefab, amount))
                    {
                        RetryAt[hive] = Time.time + RetryDelay; // no room: it waits in the hive
                        continue;
                    }
                    RetryAt.Remove(hive);
                    zdo.Set(ZDOVars.s_level, 0);
                }
                catch (System.Exception e)
                {
                    Logger.LogWarning($"Beehive at {hive.transform.position}: {e.Message}");
                }
            }
        }

        // Input, one item at a time. With "Spread input evenly" the station with the shortest queue gets the next item,
        // so stations that share chests all run at once; otherwise each station fills up in turn, as before. A station
        // leaves the round once it is full or its chests have nothing it takes.
        private void FillStations()
        {
            foreach (var settings in PerKind.Values)
                if (settings.Shares.Count > 0) PlanShares(settings);
            Hungry.Clear();
            foreach (var feed in Feeds)
                if (feed.Hungry) Hungry.Add(feed);
            while (Hungry.Count > 0)
            {
                var next = Hungry[0];
                if (_spread.Value)
                {
                    var least = next.Queued;
                    foreach (var feed in Hungry)
                    {
                        var queued = feed.Queued;
                        if (queued < least) { next = feed; least = queued; }
                    }
                }
                try
                {
                    if (!next.Hungry || !TakeInput(next)) Hungry.Remove(next);
                }
                catch (System.Exception e)
                {
                    Hungry.Remove(next);
                    Logger.LogWarning($"Station at {next.Station.transform.position}: {e.Message}");
                }
            }
        }

        // RPC_AddOre runs right away on the controlling game, so the queue size is current after each item.
        private static bool TakeInput(Feed feed)
        {
            var station = feed.Station;
            feed.Chests = feed.Chests ?? UsableChests(station.transform.position, _inputRange.Value);
            var raw = StoreRules.InputNames(station.m_conversion, c => c.m_from);
            if (!StoreRules.TakeOne(feed.Chests, i => i.m_dropPrefab != null && raw.Contains(StoreRules.NameOf(i.m_dropPrefab)) &&
                    InputAllowed(feed.Settings, StoreRules.NameOf(i.m_dropPrefab)), out var taken))
                return false;
            feed.View.InvokeRPC("RPC_AddOre", taken.m_dropPrefab.name, taken.m_cheated);
            if (feed.Settings.Budget.TryGetValue(taken.m_dropPrefab.name, out var left)) feed.Settings.Budget[taken.m_dropPrefab.name] = left - 1;
            return true;
        }

        private static void AddFuel(Feed feed)
        {
            var station = feed.Station;
            var settings = feed.Settings;
            if (settings.Fuel == null || !settings.Fuel.Value || station.m_fuelItem == null || station.m_maxFuel <= 0) return;
            // Enough fuel for everything queued, never more than fits. A station without input (the frigid kiln)
            // makes its product from the fuel itself: kept full.
            var zdo = feed.View.GetZDO();
            var needed = station.m_maxOre <= 0 ? station.m_maxFuel
                : Mathf.Min(station.m_maxFuel, zdo.GetInt(ZDOVars.s_queued) * Mathf.Max(1, station.m_fuelPerProduct));
            var fuel = zdo.GetFloat(ZDOVars.s_fuel);
            if (fuel + 1f > needed) return;
            feed.FuelChests = feed.FuelChests ?? UsableChests(station.transform.position, _fuelRange.Value);
            var fuelName = station.m_fuelItem.m_itemData.m_shared.m_name;
            while (fuel + 1f <= needed && StoreRules.TakeOne(feed.FuelChests, i => i.m_shared.m_name == fuelName, out _))
            {
                feed.View.InvokeRPC("RPC_AddFuel");
                fuel += 1f;
            }
        }

        // Chests in range, nearest first, that this game may change right now. Chests controlled by another player are
        // claimed when nobody has them open, and used from the next round on.
        private static List<Container> UsableChests(Vector3 at) => UsableChests(at, _range.Value);

        private static List<Container> UsableChests(Vector3 at, float range)
        {
            var me = Game.instance.GetPlayerProfile().GetPlayerID();
            var result = new List<Container>();
            var dists = new Dictionary<Container, float>();
            foreach (var chest in Chests)
            {
                if (chest == null || StoreRules.CartShipOrGrave(chest)) continue;
                var piece = ChestPiece(chest);
                if (piece == null) continue; // only built chests
                var dist = Vector3.Distance(at, chest.transform.position);
                if (dist > range) continue;
                if (chest.m_privacy == Container.PrivacySetting.Private && piece.GetCreator() != me) continue;
                if (chest.m_privacy == Container.PrivacySetting.Group) continue;
                var view = ChestView(chest);
                if (view == null || !view.IsValid() || chest.GetInventory() == null) continue;
                if (view.GetZDO().GetInt(ZDOVars.s_inUse) == 1 || chest.IsInUse()) continue;
                if (!view.IsOwner())
                {
                    view.ClaimOwnership();
                    continue;
                }
                result.Add(chest);
                dists[chest] = dist;
            }
            return result.OrderBy(c => dists[c]).ToList(); // no tuples: Valheim's runtime lacks System.ValueTuple
        }
    }
}
