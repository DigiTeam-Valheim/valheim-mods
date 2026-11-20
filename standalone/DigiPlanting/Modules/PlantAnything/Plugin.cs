// Plant Anything: the cultivator plants what grows wild. Berry bushes, mushrooms and flowers (planted with what they
// give), four saplings the game has no seed for (ancient, ygga, autumn birch, ashwood), extra flora (small trees,
// bushes, shrubs, vines, an Ashlands fern), debris (branches, stones, flint; off by default) and any prefab from the
// "Extra plantables" list. Growing rules for the cultivator's crops and saplings: any biome, cultivated ground, sunlight,
// space, shelter from cold and heat, grow times, spacing, enemies; plants indoors. Planted bushes, mushrooms and flowers
// regrow in a time you set; a picked flower or mushroom you planted stays as a small sprout while it regrows; wild
// stones, flint and branches can regrow too (off by default). Hover text shows how long until a plant is ready.
// The cultivator's remove key takes away what you planted (seeds back, a setting).
// Replaces Plant Everything (Advize, GPL; written anew, nothing taken from it): Plant Everything, if installed too, is switched
// off (DigiCore.OtherMods); takes over its settings once, and makes its four saplings under the same names, so ones planted with it
// keep growing; plants it placed "anywhere" keep floating.
// Light: everything is set on the prefabs once (and again when a setting changes); while playing only the hover texts,
// picking and the growth checks of crops are touched.
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using UnityEngine;

namespace PlantAnything
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.plantanything";
        public const string Name = "Digi's Plant Anything";
        public const string Version = "1.0.0";
        internal const string PlantEverythingGuid = "advize.PlantEverything";
        private const string PlantEverythingConfig = "advize.PlantEverything.cfg";

        internal static BepInEx.Logging.ManualLogSource Log;
        internal static ConfigEntry<int> RegrowMinutes, Cost, DebrisRegrow, CropMinutes, TreeMinutes;
        internal static ConfigEntry<float> CropSpacing, TreeSpacing;
        internal static ConfigEntry<bool> Flora, Debris, PickedVisible, SeedsBack, ShowTime, AnyBiome, NeedCultivated, NeedSun, NeedSpace,
            NeedShelter, EnemiesAttack, Indoors;
        internal static ConfigEntry<string> Extra;
        internal static bool Running { get; private set; }

        private void Awake()
        {
            Log = Logger;
            const string plants = "Plants", growing = "Growing", extra = "Extra";
            Cost = Config.Bind(plants, "Seeds per bush or flower", 5, new ConfigDescription(
                "What planting a berry bush, mushroom or flower costs, in what it gives (5 raspberries for a raspberry bush; fiddlehead " +
                "3 times as much).", new AcceptableValueRange<int>(1, 50)));
            RegrowMinutes = Config.Bind(plants, "Regrow time (min)", 0, new ConfigDescription(
                "How long berry bushes, mushrooms and flowers take to grow back after picking, wild ones too (in-game minutes). " +
                "0 = the game's own (berries 300, mushrooms and flowers 240).", new AcceptableValueRange<int>(0, 1440)));
            PickedVisible = Config.Bind(plants, "Picked plants stay visible", true,
                "A flower or mushroom you planted stays as a small sprout after picking (the game hides it), so you see where it " +
                "grows back.");
            ShowTime = Config.Bind(plants, "Show growth time", true,
                "Looking at a crop or sapling shows how long until it is grown, at a picked bush or flower how long until it is back.");
            SeedsBack = Config.Bind(plants, "Seeds back when removing", true,
                "Removing something you planted with the cultivator's remove key gives back what it cost.");
            Flora = Config.Bind(plants, "Extra flora", true,
                "Small trees, bushes, shrubs, vines and an Ashlands fern can be planted too (for looks; they cost wood or a seed).");
            Debris = Config.Bind(plants, "Plant debris", false, "Branches, stones and flint can be planted too.");
            DebrisRegrow = Config.Bind(plants, "Debris regrow time (min)", 0, new ConfigDescription(
                "Stones, flint and branches lying around (wild ones too) come back this long after you pick them up (in-game " +
                "minutes). 0 = off: picked up is gone, as in the game.", new AcceptableValueRange<int>(0, 1440)));

            AnyBiome = Config.Bind(growing, "Any biome", false,
                "Crops and saplings grow in every biome (also the four extra saplings, which otherwise keep to the biomes of their trees).");
            NeedCultivated = Config.Bind(growing, "Crops need cultivated ground", true, "Off: crops can be planted on any ground.");
            NeedSun = Config.Bind(growing, "Crops need sunlight", true, "Off: crops also grow under a roof.");
            NeedSpace = Config.Bind(growing, "Crops need space", true, "Off: crops grow however close other things stand.");
            NeedShelter = Config.Bind(growing, "Plants need shelter from cold and heat", true,
                "Off: crops and saplings also grow in the cold of the mountains and the heat of the Ashlands without a shelter.");
            EnemiesAttack = Config.Bind(growing, "Enemies attack crops", true, "Off: creatures leave your crops alone.");
            Indoors = Config.Bind(growing, "Plant indoors", false,
                "Saplings, bushes and flora can be planted on floors and indoors, and grow there (they then never fall; trees " +
                "grown from them neither).");
            CropMinutes = Config.Bind(growing, "Crop grow time (min)", 0, new ConfigDescription(
                "How long crops take to grow (in-game minutes, each a bit different, up to 10 % more). 0 = the game's own (about 70 to 80).",
                new AcceptableValueRange<int>(0, 600)));
            CropSpacing = Config.Bind(growing, "Crop spacing (m)", 0f, new ConfigDescription(
                "How much room a crop needs around it. 0 = the game's own (0.5).", new AcceptableValueRange<float>(0f, 2f)));
            TreeMinutes = Config.Bind(growing, "Sapling grow time (min)", 0, new ConfigDescription(
                "How long saplings take to grow into trees (in-game minutes). 0 = the game's own (about 50 to 80).",
                new AcceptableValueRange<int>(0, 600)));
            TreeSpacing = Config.Bind(growing, "Sapling spacing (m)", 0f, new ConfigDescription(
                "How much room a sapling needs around it. 0 = the game's own (2 or more).", new AcceptableValueRange<float>(0f, 5f)));

            Extra = Config.Bind(extra, "Extra plantables", "", new ConfigDescription(
                "Any other prefab to plant with the cultivator, also from other mods: Prefab:Item:Amount, separated by commas " +
                "(\"Pickable_Tar:Tar:5, Pickable_DragonEgg:DragonEgg:1\"). Wrong names are left out (see the log).", null,
                new ConfigurationManagerAttributes { IsAdvanced = true }));

            var old = Path.Combine(Paths.ConfigPath, PlantEverythingConfig);
            if (File.Exists(old) && FirstTime("from plant everything")) FromPlantEverything(old);
        }

        private void Start()
        {
            SwitchOffReplaced(PlantEverythingGuid, "Plant Everything"); // installed too: it goes off, ours runs
            PatchModule(typeof(Plantables));
            PatchModule(typeof(Growing));
            PatchModule(typeof(Picking));
            Running = true;
            foreach (var entry in new ConfigEntryBase[]
                     {
                         RegrowMinutes, Cost, DebrisRegrow, CropMinutes, TreeMinutes, CropSpacing, TreeSpacing, Flora, Debris, SeedsBack,
                         AnyBiome, NeedCultivated, NeedShelter, EnemiesAttack, Indoors, Extra,
                     })
                Hook(entry);
            Plantables.Apply(true); // switched on with a world loaded
        }

        private void OnDestroy()
        {
            if (!Running) return;
            Running = false;
            Plantables.Undo();
            Picking.Clear();
        }

        // Every setting that changes the prefabs applies at once (also to the plants already loaded).
        private static void Hook(ConfigEntryBase entry)
        {
            switch (entry)
            {
                case ConfigEntry<int> i: i.SettingChanged += Changed; break;
                case ConfigEntry<float> f: f.SettingChanged += Changed; break;
                case ConfigEntry<bool> b: b.SettingChanged += Changed; break;
                case ConfigEntry<string> s: s.SettingChanged += Changed; break;
            }
        }

        private static void Changed(object sender, System.EventArgs e)
        {
            if (Running) Plantables.Apply(true);
        }

        // ---- Plant Everything's settings, once ----

        private void FromPlantEverything(string path)
        {
            var cfg = new Dictionary<string, string>();
            var section = "";
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith("[") && line.EndsWith("]")) section = line.Substring(1, line.Length - 2);
                else if (line.Length > 0 && line[0] != '#' && line.Contains("="))
                {
                    var at = line.IndexOf('=');
                    cfg[section + "." + line.Substring(0, at).Trim()] = line.Substring(at + 1).Trim();
                }
            }
            bool Flag(string key, bool fallback) => cfg.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : fallback;
            float Number(string key, float fallback) =>
                cfg.TryGetValue(key, out var v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : fallback;
            // the regrow time most of its plants have, when not the game's
            var times = new Dictionary<int, int>();
            foreach (var key in new[]
                     {
                         "Berries.RaspberryRespawnTime", "Berries.BlueberryRespawnTime", "Berries.CloudberryRespawnTime", "Berries.LingonberryRespawnTime",
                         "Mushrooms.MushroomRespawnTime", "Mushrooms.YellowMushroomRespawnTime", "Mushrooms.BlueMushroomRespawnTime",
                         "Mushrooms.SmokepuffRespawnTime", "Flowers.ThistleRespawnTime", "Flowers.DandelionRespawnTime", "Flowers.FiddleheadRespawnTime",
                     })
            {
                var minutes = (int)Number(key, -1f);
                if (minutes > 0) times[minutes] = (times.TryGetValue(minutes, out var n) ? n : 0) + 1;
            }
            var common = 0;
            var most = 0;
            foreach (var pair in times)
                if (pair.Value > most) { most = pair.Value; common = pair.Key; }
            if (common > 0 && common != 240 && common != 300) RegrowMinutes.Value = Mathf.Clamp(common, 0, 1440);
            AnyBiome.Value = !Flag("Difficulty.EnforceBiomesVanilla", true);
            NeedShelter.Value = Flag("Difficulty.PlantsRequireShielding", true);
            Indoors.Value = Flag("Difficulty.PlaceAnywhere", false);
            SeedsBack.Value = Flag("Difficulty.RecoverResources", SeedsBack.Value);
            Flora.Value = Flag("General.EnableMiscFlora", true);
            Debris.Value = Flag("General.EnableDebris", false);
            PickedVisible.Value = Flag("General.ShowPickableSpawners", true);
            EnemiesAttack.Value = Flag("Crops.EnemiesTargetCrops", true);
            NeedSun.Value = Flag("Crops.CropsRequireSunlight", true);
            NeedSpace.Value = Flag("Crops.CropsRequireGrowthSpace", true);
            if (Flag("Crops.EnableCropOverrides", false))
            {
                NeedCultivated.Value = Flag("Crops.CropsRequireCultivation", true);
                var grow = Number("Crops.CropGrowTimeMin", 4000f);
                if (!Mathf.Approximately(grow, 4000f)) CropMinutes.Value = Mathf.Clamp(Mathf.RoundToInt(grow / 60f), 1, 600);
                var radius = Number("Crops.CropGrowRadius", 0.5f);
                if (!Mathf.Approximately(radius, 0.5f)) CropSpacing.Value = Mathf.Clamp(radius, 0.05f, 2f);
            }
            // saplings: the smallest spacing it had set (most players set them all the same)
            var spacing = float.MaxValue;
            foreach (var tree in new[] { "Birch", "Oak", "Ancient", "Beech", "Pine", "Fir", "Ygga", "AutumnBirch", "Ashwood", "Timberwood" })
                spacing = Mathf.Min(spacing, Number("Saplings." + tree + "GrowRadius", float.MaxValue));
            if (spacing < 2f) TreeSpacing.Value = Mathf.Clamp(spacing, 0.1f, 5f);
            Logger.LogInfo("Took over Plant Everything's settings (once).");
        }
    }

    // Read by Configuration Manager by field name.
    internal sealed class ConfigurationManagerAttributes
    {
        public bool? IsAdvanced;
    }
}
