// Auto Map Pins, the white icon style: white pictograms from game-icons.net (CC BY 3.0, by Lorc, Delapouite and the
// other game-icons.net authors; credited on the mod's page), tinted per thing like the map's own pins. They are packed
// into one sheet compiled into the mod (WhiteIcons.png and its list of names WhiteIcons.txt, made by
// make_map_icons.py from the names in this file and in ModPlaces.cs). A thing gets its icon by its id (or its entry in
// ModPlaces), else by a word in its id, else by
// its kind; a thing without one keeps a white copy of its game icon. Every icon of the sheet (also the extra ones
// below, used by nothing) can be picked per thing in F1.
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AutoMapPins
{
    internal static class WhiteIcons
    {
        internal const string Portal = "lorc/magic-portal", OpenChest = "skoll/open-chest", MapList = "delapouite/checklist";

        private static readonly Dictionary<string, string> ById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Deposits
            ["deposit:deposit_copper"] = "faithtoken/ore", ["deposit:TinOre"] = "faithtoken/ore",
            ["deposit:deposit_silver"] = "faithtoken/ore", ["deposit:deposit_silvervein"] = "faithtoken/ore",
            ["deposit:FlametalOreNew"] = "faithtoken/ore", ["deposit:deposit_goldvein"] = "delapouite/gold-nuggets",
            ["deposit:Obsidian"] = "faithtoken/minerals", ["deposit:ProustitePowder"] = "faithtoken/minerals",
            ["deposit:mudpile"] = "delapouite/stone-pile", ["deposit:giant_helmet"] = "delapouite/viking-helmet",
            ["deposit:giant_sword"] = "delapouite/ancient-sword", ["deposit:giant_bone"] = "lorc/broken-bone",
            ["deposit:giant_brain"] = "lorc/brain", ["deposit:Ice"] = "lorc/snowflake-2", ["deposit:Chitin"] = "lorc/sea-serpent",
            ["deposit:Frostwood"] = "delapouite/log", ["deposit:Guck"] = "lorc/dripping-goo", ["deposit:BellFragment"] = "lorc/ringing-bell",
            ["deposit:Coal"] = "delapouite/coal-wagon",
            // Pickups
            ["plant:Raspberry"] = "delapouite/raspberry", ["plant:Blueberries"] = "lorc/elderberry", ["plant:Cloudberry"] = "delapouite/strawberry",
            ["plant:Lingonberry"] = "delapouite/berries-bowl", ["plant:Vineberry"] = "lorc/grapes", ["plant:Mushroom"] = "lorc/mushroom",
            ["plant:MushroomYellow"] = "lorc/spotted-mushroom", ["plant:MushroomBlue"] = "delapouite/mushrooms",
            ["plant:MushroomMagecap"] = "lorc/super-mushroom", ["plant:MushroomJotunPuffs"] = "delapouite/mushrooms-cluster",
            ["plant:MushroomSmokePuff"] = "delapouite/grass-mushroom", ["plant:Thistle"] = "lorc/thorny-vine",
            ["plant:Dandelion"] = "delapouite/dandelion-flower", ["plant:Flax"] = "delapouite/flax", ["plant:Barley"] = "lorc/wheat",
            ["plant:Carrot"] = "delapouite/carrot", ["plant:Turnip"] = "delapouite/beet", ["plant:Kale"] = "delapouite/cabbage",
            ["plant:Poteitr"] = "delapouite/potato", ["plant:Crystal"] = "lorc/crystal-growth", ["plant:Obsidian"] = "faithtoken/minerals",
            ["plant:TinOre"] = "faithtoken/ore", ["plant:IronOre"] = "faithtoken/ore", ["plant:SulfurStone"] = "lorc/crystal-cluster",
            ["plant:DragonEgg"] = "lorc/dinosaur-egg", ["plant:VoltureEgg"] = "delapouite/nest-eggs",
            ["plant:SurtlingCore"] = "delapouite/fire-gem", ["plant:MoltenCore"] = "sbed/lava", ["plant:BlackCore"] = "delapouite/planet-core",
            ["plant:FrostCore"] = "lorc/frostfire", ["plant:Tar"] = "lorc/boiling-bubbles", ["plant:RoyalJelly"] = "delapouite/jelly",
            ["plant:GoblinTotem"] = "delapouite/totem", ["plant:Coins"] = "delapouite/coins-pile", ["plant:Entrails"] = "lorc/meat",
            ["plant:WolfHairBundle"] = "delapouite/hair-strands", ["plant:Charredskull"] = "lorc/broken-skull",
            ["plant:Pot_Shard_Green"] = "delapouite/broken-pottery", ["plant:Fiddleheadfern"] = "delapouite/fern",
            ["plant:GlowWorm"] = "delapouite/worms", ["plant:Lantern"] = "lorc/lantern", ["plant:Tankard_dvergr"] = "lorc/beer-stein",
            ["plant:FishingRod"] = "delapouite/fishing-pole", ["plant:FaderEmber"] = "lorc/burning-embers", ["plant:Wisp"] = "lorc/spark-spirit",
            ["plant:Wood"] = "delapouite/wood-stick", ["plant:Frostwood"] = "delapouite/log", ["plant:Snowball"] = "lorc/snowflake-1",
            ["plant:Stone"] = "delapouite/stone-pile", ["plant:StoneRock"] = "lorc/rock", ["plant:Flint"] = "lorc/rock", ["plant:Grausten"] = "lorc/rock",
            // Others
            ["beehive"] = "delapouite/beehive", ["built:piece_sapcollector"] = "delapouite/tap", ["chest"] = "lorc/locked-chest", ["treasuremap"] = "lorc/locked-chest", ["spawner"] = "lorc/skull-crossed-bones",
            // Places
            ["place:Crypt"] = "delapouite/crypt-entrance", ["place:SunkenCrypt"] = "delapouite/crypt-entrance", ["place:MorkBorg"] = "delapouite/crypt-entrance",
            ["place:TrollCave"] = "delapouite/cave-entrance", ["place:BearCave"] = "delapouite/cave-entrance",
            ["place:MountainCave"] = "delapouite/mountain-cave", ["place:Hildir_cave"] = "delapouite/mountain-cave",
            ["place:Mistlands_DvergrTownEntrance"] = "delapouite/gold-mine", ["place:MorgenHole"] = "delapouite/underground-cave",
            ["place:Hildir_crypt"] = "lorc/tombstone", ["place:Hildir_plainsfortress"] = "lorc/stone-tower",
            ["place:CharredFortress"] = "lorc/locked-fortress", ["place:PlaceofMystery"] = "delapouite/crystal-shrine",
            ["place:Eikthyrnir"] = "caro-asercion/deer-head", ["place:GDKing"] = "lorc/evil-tree", ["place:Bonemass"] = "lorc/goo-skull",
            ["place:Dragonqueen"] = "lorc/dragon-head", ["place:GoblinKing"] = "lorc/crowned-skull",
            ["place:Mistlands_DvergrBossEntrance"] = "carl-olsen/spider-face", ["place:FaderLocation"] = "delapouite/spiked-dragon-head",
            ["place:Vendor_BlackForest"] = "delapouite/coins", ["place:Hildir_camp"] = "delapouite/shopping-bag",
            ["place:BogWitch_Camp"] = "cathelineau/witch-face", ["place:GoblinCamp"] = "delapouite/goblin-camp",
            ["place:WoodVillage"] = "delapouite/village", ["place:WoodFarm"] = "delapouite/house", ["place:TarPit"] = "lorc/boiling-bubbles",
            ["place:DrakeNest"] = "delapouite/nest-eggs", ["place:InfestedTree"] = "delapouite/tree-roots",
            ["place:Mistlands_Giant"] = "delapouite/t-rex-skull", ["place:Runestone"] = "lorc/rune-stone",
            ["place:Mistlands_Harbour"] = "delapouite/harbor-dock", ["place:Mistlands_Lighthouse"] = "delapouite/lighthouse",
            ["place:Dolmen"] = "delapouite/dolmen", ["place:Waymarker"] = "delapouite/obelisk",
            ["place:DN_Bossroom"] = "lorc/crown", ["place:TheHole"] = "delapouite/underground-cave",
            ["place:Mistlands_GuardTower"] = "lorc/stone-tower", ["place:FrozenShip"] = "delapouite/ship-wreck",
            ["place:ShipSetting"] = "delapouite/grave-flowers",
            ["place:VoltureNest"] = "delapouite/nest-eggs", ["place:SulfurArch"] = "lorc/crystal-cluster", ["place:FireHole"] = "lorc/campfire",
        };

        // A word in the id (in this order) when the id itself has no icon.
        private static readonly string[,] ByWord =
        {
            { "Dvergr", "kier-heyl/dwarf-helmet" }, { "Mistlands_", "kier-heyl/dwarf-helmet" }, { "Tower", "lorc/stone-tower" },
            { "Fortress", "lorc/locked-fortress" }, { "Ruin", "delapouite/ancient-ruins" }, { "Cave", "delapouite/cave-entrance" },
            { "Crypt", "delapouite/crypt-entrance" }, { "Hole", "delapouite/underground-cave" }, { "Ship", "delapouite/ship-wreck" },
            { "Well", "delapouite/well" }, { "Grave", "lorc/tombstone" }, { "Camp", "delapouite/forest-camp" },
            { "Village", "delapouite/village" }, { "House", "delapouite/hut" }, { "Hut", "delapouite/hut" }, { "Cabin", "delapouite/hut" },
            { "Stone", "delapouite/dolmen" }, { "Henge", "delapouite/dolmen" }, { "Statue", "delapouite/obelisk" },
            { "Nest", "delapouite/nest-eggs" }, { "Seed", "delapouite/plant-seed" }, { "Berr", "delapouite/berries-bowl" },
            { "Mushroom", "lorc/mushroom" }, { "Gemstone", "lorc/gems" }, { "Dyrnwyn", "lorc/shattered-sword" },
            { "Ore", "faithtoken/ore" }, { "Rock", "lorc/rock" },
        };

        // Not given to anything by default, only there to be picked in F1: creatures for spawners, markers.
        private static readonly string[] Extras =
        {
            "caro-asercion/boar", "lorc/wolf-head", "skoll/troll", "delapouite/rock-golem", "delapouite/golem-head",
            "skoll/skeleton", "lorc/ghost", "delapouite/goblin-head", "delapouite/ogre", "lorc/snake", "lorc/bat-wing",
            "lorc/wasp-sting", "delapouite/lynx-head", "lorc/hound", "lorc/crown", "delapouite/star-flag",
            "delapouite/flag-objective", "delapouite/viking-longhouse",
            "lorc/campfire", "lorc/battle-axe", "lorc/anvil", "delapouite/bow-arrow", "delapouite/thor-hammer",
            "lorc/checked-shield", "lorc/standing-potion", "delapouite/key-lock", "delapouite/camping-tent",
            "lorc/crossed-swords", "lorc/treasure-map", "lorc/archery-target", "lorc/anchor",
            // graves and farms (Viking burial, Burial Chambers, abandoned farms), and the list button of the map
            "lorc/coffin", "lorc/tombstone", "lorc/spade", "delapouite/drakkar", "delapouite/viking-shield", "delapouite/barn",
            "delapouite/farmer", "delapouite/farm-tractor", MapList,
        };

        private static string ForKind(Kind kind)
        {
            switch (kind)
            {
                case Kind.Plant: return "lorc/sprout";
                case Kind.Deposit: return "lorc/rock";
                case Kind.Place: return "delapouite/position-marker";
                case Kind.Spawner: return "lorc/skull-crossed-bones";
                case Kind.Chest: return "lorc/locked-chest";
                case Kind.Beehive: return "delapouite/beehive";
                default: return null;
            }
        }

        internal static string NameFor(Thing thing)
        {
            if (thing == null) return null;
            if (ById.TryGetValue(thing.Id, out var icon)) return icon;
            icon = Catalog.WhiteIconOf(thing); // a place of another mod (ModPlaces.cs)
            if (icon != null) return icon;
            for (var i = 0; i < ByWord.GetLength(0); i++)
                if (thing.Id.IndexOf(ByWord[i, 0], StringComparison.OrdinalIgnoreCase) >= 0) return ByWord[i, 1];
            return ForKind(thing.Kind);
        }

        // --- The sheet ------------------------------------------------------------------------------------------

        private const int Cell = 64;
        private static Dictionary<string, Sprite> _sprites;
        private static readonly List<string> SheetOrder = new List<string>();

        // Every icon of the sheet, in its order (for the picker in F1).
        internal static IList<string> All
        {
            get
            {
                if (_sprites == null) Load();
                return SheetOrder;
            }
        }

        // ImageConversion.LoadImage, found at runtime: its module targets a newer .NET Standard than this mod compiles against.
        private static readonly System.Reflection.MethodInfo LoadImage = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule")
            ?.GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]) });

        internal static Sprite For(Thing thing) => Get(NameFor(thing));

        internal static Sprite Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_sprites == null) Load();
            return _sprites.TryGetValue(name, out var sprite) ? sprite : null;
        }

        private static void Load()
        {
            _sprites = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var assembly = typeof(WhiteIcons).Assembly;
                byte[] image;
                string[] names;
                using (var stream = assembly.GetManifestResourceStream("AutoMapPins.WhiteIcons.png"))
                using (var memory = new MemoryStream())
                {
                    if (stream == null) return;
                    stream.CopyTo(memory);
                    image = memory.ToArray();
                }
                using (var stream = assembly.GetManifestResourceStream("AutoMapPins.WhiteIcons.txt"))
                {
                    if (stream == null) return;
                    using (var reader = new StreamReader(stream)) names = reader.ReadToEnd().Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                }
                var sheet = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (LoadImage == null || !(LoadImage.Invoke(null, new object[] { sheet, image }) is bool loaded) || !loaded) return;
                // One texture per icon, so the small sizes of the map (mipmaps) do not bleed into the neighbours.
                var columns = sheet.width / Cell;
                for (var i = 0; i < names.Length; i++)
                {
                    var x = i % columns * Cell;
                    var y = sheet.height - (i / columns + 1) * Cell;
                    if (y < 0) break;
                    var texture = new Texture2D(Cell, Cell, TextureFormat.RGBA32, true) { name = names[i], filterMode = FilterMode.Trilinear };
                    texture.SetPixels(sheet.GetPixels(x, y, Cell, Cell));
                    texture.Apply(true, true);
                    var sprite = Sprite.Create(texture, new Rect(0f, 0f, Cell, Cell), new Vector2(0.5f, 0.5f), 100f);
                    sprite.name = names[i];
                    _sprites[names[i]] = sprite;
                    SheetOrder.Add(names[i]);
                }
                UnityEngine.Object.Destroy(sheet);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("Could not load the white icons: " + e.Message);
            }
        }
    }
}
