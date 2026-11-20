// Auto Map Pins: pins what you come across on the map by itself. Settings per biome, below General and Portals: every
// kind of thing that can be found in that biome with its own switch and, in F1, the icon it gets (the same thing in two
// biomes has a switch in each); things rarely worth a pin only with F1's "Advanced settings". Portals are pinned as soon
// as they load, named after their tag, and follow it; their pins have their own size, colour and icon. Two icon styles:
// the game's own icons (what a place holds or is about, the item a thing gives, a trophy) or white ones in a colour per
// thing; per thing the colour and the icon can be chosen in F1. Names under the pins are switched, and the pin size
// set, per kind of thing (ore, plants, chests, spawners, dungeons, other places, boss altars, sap extractors, traders).
// The large map shows pins by zoom: zoomed out only portals, boss altars and traders, halfway the dungeons and places
// too, zoomed in everything; names one step later. Pins of the same thing that would overlap are one pin with a count;
// pointing at a pin tells what it is and how it is; buttons on the map hide or show a kind of pin (see MapView.cs), and
// the last one opens a list of every kind of pin to switch each one on or off (PinList.cs: the switches of F1, all
// biomes at once).
// Pins of things that do not come back (mined out ore, destroyed spawners) go away; picked berries and the like stay,
// faded until they have regrown; looted chests are faded; a pin you delete stays deleted.
// Replaces Cartur's Map Pins: its pins are taken over once per world and character (see CarturImport.cs).
// The parts: Catalog.cs (what can be found, settings), Tracker.cs (discovery and upkeep), Records.cs (saved per world
// and character), MapPins.cs (on the map), MapView.cs (zoom, merging, pointing, filters), Icons.cs.
// Light: a dictionary lookup when an object loads, a check of the loaded candidates and nearby pins once a second, and
// the colour, size and zoom level of this mod's pins when the map redraws its pins.
using BepInEx.Configuration;
using DigiShared;
using UnityEngine;

namespace AutoMapPins
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.automappins";
        public const string Name = "Digi's Auto Map Pins";
        public const string Version = "1.0.0";

        internal enum IconStyle { GameIcons, WhiteIcons }

        // From which zoom of the large map a kind of pin shows (its name one step later).
        internal enum ShowFrom { ZoomedOut, Halfway, ZoomedIn }

        internal static BepInEx.Logging.ManualLogSource Log;
        private static ConfigEntry<bool> _auto, _portals, _portalNames, _restore, _share, _lairSpawners;
        private static ConfigEntry<bool> _zoomLevels, _merge, _hover, _filters, _counts;
        private static ConfigEntry<float> _buttonSize;
        private static ConfigEntry<float> _zoomOut, _zoomIn, _zoomNames;
        private static ConfigEntry<int> _hidden;
        private static ConfigEntry<ShowFrom> _portalLevel;
        // The kinds of pins with their own name switch, size and zoom level.
        internal enum Group { Deposits, Plants, Chests, Spawners, Dungeons, Places, Bosses, Builds, Traders }

        private static readonly string[] GroupTitles =
            { "ore and deposits", "plants and pickups", "chests", "spawners", "dungeons and caves", "other places", "boss altars", "sap extractors", "traders" };
        private static readonly string[] GroupNames =
        {
            "Show the name under pins of ore and other deposits (\"Copper Deposit x3\"). Off: only the icon.",
            "Show the name under pins of berries, mushrooms, pickups (\"Raspberries x4\", \"Fuling Totem\") and beehives.",
            "Show the name under treasure chest pins.",
            "Show what a spawner spawns under its pin (\"Greydwarf\").",
            "Show the name under pins of dungeons and caves: Burial Chambers, crypts, troll caves, frost caves, mines, ...",
            "Show the name under pins of the other places: villages, tar pits, drake nests, giant remains, ...",
            "Show the name under boss altar pins (just the boss: \"Eikthyr\"), also when the other places have no names.",
            "Show the name under sap extractor pins.",
            "Show the name under trader pins (Haldor, Hildir, the Bog Witch; where the map has no icon of its own for them).",
        };
        // Names on the map only where they help by default: places other than dungeons, boss altars and traders.
        private static readonly bool[] GroupNamesOn = { false, false, false, false, false, true, true, false, true };
        // Zoomed out only boss altars and traders (and portals), halfway the dungeons and places, the rest zoomed in.
        private static readonly ShowFrom[] GroupLevels =
        {
            ShowFrom.ZoomedIn, ShowFrom.ZoomedIn, ShowFrom.ZoomedIn, ShowFrom.ZoomedIn, ShowFrom.Halfway, ShowFrom.Halfway,
            ShowFrom.ZoomedOut, ShowFrom.ZoomedIn, ShowFrom.ZoomedOut,
        };
        private static readonly ConfigEntry<bool>[] Names = new ConfigEntry<bool>[GroupTitles.Length];
        private static readonly ConfigEntry<bool>[] Minimaps = new ConfigEntry<bool>[GroupTitles.Length];
        private static readonly ConfigEntry<ShowFrom>[] Levels = new ConfigEntry<ShowFrom>[GroupTitles.Length];
        private static ConfigEntry<bool> _portalMinimap, _crossOff, _preview;
        private static readonly ConfigEntry<float>[] Sizes = new ConfigEntry<float>[GroupTitles.Length];
        private static ConfigEntry<string> _portalIcon;
        private static ConfigEntry<float> _radius, _size, _faded, _portalSize, _traderSize, _nameSize;
        private static ConfigEntry<IconStyle> _style;
        private static ConfigEntry<Color> _portalColor;

        internal static bool AutoPinning => _auto.Value;
        internal static bool Preview => _preview == null || _preview.Value;
        internal static float Radius => _radius.Value;
        internal static string PortalIcon => _portalIcon.Value;
        internal static bool WhiteStyle => _style.Value == IconStyle.WhiteIcons;
        internal static float FadedOpacity => _faded.Value;
        internal static bool PortalsOn => _portals.Value;
        internal static bool PortalNames => _portalNames.Value;
        internal static float PortalSize => _portalSize.Value;
        internal static float TraderSize => _traderSize.Value;
        internal static float NameSize => _nameSize.Value;
        internal static Color PortalColor => _portalColor.Value;
        internal static bool ShareAtTables => _share.Value;
        internal static bool LairSpawners => _lairSpawners.Value;
        internal static bool ZoomLevels => _zoomLevels.Value;
        internal static float ZoomOut => _zoomOut.Value;
        internal static float ZoomIn => _zoomIn.Value;
        internal static float ZoomNames => _zoomNames.Value;
        internal static bool MergePins => _merge.Value;
        internal static bool HoverInfo => _hover.Value;
        internal static bool FilterButtons => _filters.Value;
        internal static bool ShowCounts => _counts.Value;
        internal static float ButtonScale => _buttonSize.Value;

        // The kinds hidden with the filter buttons (a bit per MapView filter); only while the buttons are on.
        internal static int HiddenKinds
        {
            get => _filters.Value ? _hidden.Value : 0;
            set => _hidden.Value = value;
        }

        internal static Group GroupOf(Thing thing)
        {
            switch (thing.Kind)
            {
                case Kind.Deposit: return Group.Deposits;
                case Kind.Plant:
                case Kind.Beehive: return Group.Plants;
                case Kind.Chest: return Group.Chests;
                case Kind.Spawner: return Group.Spawners;
                case Kind.Built: return Group.Builds;
                default:
                    return Catalog.IsBoss(thing) ? Group.Bosses : Catalog.IsTrader(thing) ? Group.Traders
                        : Catalog.IsDungeon(thing) ? Group.Dungeons : Group.Places;
            }
        }

        // Whether pins of this kind of thing have their name under them.
        internal static bool NamesFor(Thing thing) => Names[(int)GroupOf(thing)].Value;

        // Whether pins of this kind of thing show on the minimap too (always on the large map).
        internal static bool OnMinimap(Thing thing) => thing == null || Minimaps[(int)GroupOf(thing)].Value;
        internal static bool PortalsOnMinimap => _portalMinimap.Value;
        internal static bool CrossOffLooted => _crossOff.Value;

        // From which zoom step of the large map (0 zoomed out, 1 halfway, 2 zoomed in) pins of this thing show.
        internal static int LevelFor(Thing thing) => thing == null ? (int)_portalLevel.Value : (int)Levels[(int)GroupOf(thing)].Value;

        // The size of a pin of this thing: "Pin size" times its kind's own size.
        internal static float SizeFor(Thing thing) => thing == null ? _size.Value : _size.Value * Sizes[(int)GroupOf(thing)].Value;
        // Cartur's Map Pins still loaded (this session started with it): this waits, so nothing is pinned twice.
        internal static bool Waiting { get; private set; }

        private void Awake()
        {
            Log = Logger;
            const string general = "01 - General", portals = "02 - Portals";
            _auto = Config.Bind(general, "Auto pinning", true,
                "Pin things as you come across them. Off: no new pins (the ones you have stay, portals keep their own switch).");
            _radius = Config.Bind(general, "Discovery radius (m)", 50f, new ConfigDescription(
                "How close you must come to something before it is pinned. Chests and spawners in a dungeon need you inside.",
                new AcceptableValueRange<float>(10f, 150f)));
            _zoomLevels = Config.Bind(general, "Zoom levels", true,
                "The large map shows pins by how far you zoom in: zoomed out only portals, boss altars and traders, halfway " +
                "the dungeons and other places too, zoomed in everything (each kind can be set below, \"Zoom - ...\"). Names " +
                "show one step later. Off: every pin at every zoom.");
            _zoomOut = Config.Bind(general, "Zoom - zoomed out from", 0.35f, new ConfigDescription(
                "The large map counts as zoomed out from this zoom (the map's own scale: 0.015 fully zoomed in, 1 the whole world).",
                new AcceptableValueRange<float>(0.03f, 1f)));
            _zoomIn = Config.Bind(general, "Zoom - zoomed in below", 0.12f, new ConfigDescription(
                "The large map counts as zoomed in below this zoom (it opens at 0.1 the first time).",
                new AcceptableValueRange<float>(0.02f, 1f)));
            _zoomNames = Config.Bind(general, "Zoom - all names below", 0.05f, new ConfigDescription(
                "Below this zoom the names of the pins that show only zoomed in show too (if their names are on).",
                new AcceptableValueRange<float>(0.015f, 1f)));
            for (var i = 0; i < GroupTitles.Length; i++)
                Levels[i] = Config.Bind(general, "Zoom - " + GroupTitles[i], GroupLevels[i],
                    $"From which zoom the pins of {GroupTitles[i]} show on the large map: ZoomedOut = always, Halfway = from halfway " +
                    "zoomed in, ZoomedIn = only zoomed in. Their names show one step later.");
            _merge = Config.Bind(general, "Merge nearby pins", true,
                "Pins of the same thing that would overlap on the large map are one pin with their count on it, until you zoom in " +
                "far enough for them to stand apart.");
            _counts = Config.Bind(general, "Counts on merged pins", true,
                "Show the number on a pin that stands for several (\"4\" on a silver vein). Off: no numbers on the map.");
            _hover = Config.Bind(general, "Pin info on hover", true,
                "Point at a pin on the large map to see what it is and how it is: regrows in 12 min, looted, emptied, what a " +
                "place holds or a spawner drops, how far away.");
            _filters = Config.Bind(general, "Filter buttons on the map", true,
                "A row of buttons on the large map that hide or show a kind of pin with one click: portals, bosses and traders, " +
                "dungeons, other places, ore, plants, spawners, chests. Off: no buttons and nothing hidden by them.");
            _hidden = Config.Bind(general, "Hidden by the filter buttons", 0, new ConfigDescription(
                "The kinds of pins hidden with the filter buttons on the map (set by clicking them).", null,
                new ConfigurationManagerAttributes { Browsable = false }));
            _buttonSize = Config.Bind(general, "Filter button size", 1f, new ConfigDescription(
                "Size of the filter buttons on the large map, 1 = normal.", new AcceptableValueRange<float>(0.6f, 2f)));
            for (var i = 0; i < GroupTitles.Length; i++)
                Names[i] = Config.Bind(general, "Names - " + GroupTitles[i], GroupNamesOn[i], GroupNames[i]);
            _style = Config.Bind(general, "Icon style", IconStyle.GameIcons,
                "GameIcons: the game's own icon of each thing (what a place holds, the item a thing gives, a trophy). WhiteIcons: " +
                "white icons in a colour per thing, like the map's own pins (each colour is under its biome, Advanced settings).");
            _size = Config.Bind(general, "Pin size", 0.8f, new ConfigDescription(
                "Size of the pins of this mod, 1 = the game's. Each kind below has its own size on top of this.",
                new AcceptableValueRange<float>(0.5f, 2.5f)));
            for (var i = 0; i < GroupTitles.Length; i++)
                Sizes[i] = Config.Bind(general, "Size - " + GroupTitles[i], i == (int)Group.Bosses ? 1.3f : 1f, new ConfigDescription(
                    $"Size of the pins of {GroupTitles[i]}, times \"Pin size\" (0.6 = smaller).", new AcceptableValueRange<float>(0.3f, 2.5f)));
            // Boss altars stand out by size (and a soft shadow behind the head) since 1.1.0: once, from the old default.
            if (FirstTime("boss size 1.1.0") && Mathf.Approximately(Sizes[(int)Group.Bosses].Value, 1f)) Sizes[(int)Group.Bosses].Value = 1.3f;
            for (var i = 0; i < GroupTitles.Length; i++)
                Minimaps[i] = Config.Bind(general, "Minimap - " + GroupTitles[i], true,
                    $"Show the pins of {GroupTitles[i]} on the minimap too. Off: only on the large map.");
            _crossOff = Config.Bind(general, "Cross off looted dungeons", true,
                "Cross off a dungeon's pin (Burial Chambers, crypts, caves, ...) once you are inside and no chest in it has loot left.");
            _faded = Config.Bind(general, "Faded pin opacity", 0.35f, new ConfigDescription(
                "How visible a faded pin is: picked berries, mushrooms and the like until they have regrown, and emptied chests.",
                new AcceptableValueRange<float>(0.05f, 1f)));
            _lairSpawners = Config.Bind(general, "Spawners at dungeons and camps", false,
                "Pin the spawners around dungeons, caves, camps and villages too (the skeletons at Burial Chambers, the draugr " +
                "of a village). Off: only the place's own pin there. Nothing inside a dungeon is ever pinned.");
            _restore = Config.Bind(general, "Restore deleted pins", false,
                "Tick to bring back every pin of this mod you deleted in this world (it unticks itself).");
            Config.Bind(general, "Hand-placed pins", false, new ConfigDescription(
                "Remove every pin you (or a cartography table) placed by hand, after a second click. A copy is kept: " +
                "\"Bring back\" puts them back. Death markers, the bed, boss pins and this mod's pins stay.", null,
                new ConfigurationManagerAttributes { CustomDrawer = HandPins.Draw, HideDefaultButton = true }));
            _preview = Config.Bind(general, "Icon preview on hover", true,
                "In F1, an icon you hold the mouse over for a moment shows bigger (the icon picker and the icons beside each " +
                "switch). Your own icons: PNG files in BepInEx/config/DigiAutoMapPins/Icons show up in the picker.");
            _share = Config.Bind(general, "Share at cartography tables", true,
                "Your pins go onto a cartography table with your map, as normal pins with their name, so everyone gets them. " +
                "Pins you read from a table that this mod knows become its own pins (nothing twice).");
            _portals = Config.Bind(portals, "Pin portals", true,
                "Pin every portal as soon as it loads, also the one you just built. The pin follows its tag and goes when the portal does.");
            _portalNames = Config.Bind(portals, "Portal names", true, "Show the portal's tag under its pin.");
            _portalLevel = Config.Bind(portals, "Portal pins from zoom", ShowFrom.ZoomedOut,
                "From which zoom portal pins show on the large map (see \"Zoom levels\" in General); their names show one step later.");
            _portalSize = Config.Bind(portals, "Portal pin size", 1f, new ConfigDescription("Size of portal pins, 1 = the game's.",
                new AcceptableValueRange<float>(0.5f, 2.5f)));
            if (FirstTime("portal pin size 1.1.0") && Mathf.Approximately(_portalSize.Value, 1.5f)) _portalSize.Value = 1f; // the old default
            _portalMinimap = Config.Bind(portals, "Portal pins on the minimap", true, "Show portal pins on the minimap too. Off: only on the large map.");
            _portalColor = Config.Bind(portals, "Portal color", new Color(0.78f, 0.55f, 1f, 1f),
                "Colour of the soft glow behind portal icons (with Portal List: the colour picked for that portal there, if any), " +
                "or of a white portal icon picked below.");
            _portalIcon = Config.Bind(portals, "Portal icon", "", new ConfigDescription(
                "Icon of portal pins: click it to pick another. Empty = each portal's own icon (wood or stone portal; with Portal " +
                "List the icon picked for that portal there, and its name in the portal's colour).", null,
                new ConfigurationManagerAttributes { CustomDrawer = Icons.DrawPortalIcon }));
            _traderSize = Config.Bind(general, "Trader icon size", 1f, new ConfigDescription(
                "Size of the map's own icons of Haldor, Hildir, the Bog Witch and the start, 1 = a normal pin (the game draws them " +
                "twice as big).", new AcceptableValueRange<float>(0.4f, 2.5f)));
            _nameSize = Config.Bind(general, "Name size", 0.8f, new ConfigDescription(
                "Size of the names under the pins of this mod (and portals), 1 = the game's.", new AcceptableValueRange<float>(0.4f, 1.5f)));

            Subscribe(true);
            Catalog.Changed += MapPins.RefreshAll;
            Catalog.Recolored += MapPins.Restyle;
            // Defaults that changed in this version: once, for switches still at the old default (Defaults.Changed).
            Catalog.NewDefaults = FirstTime("pin defaults 1.1.0");
            Catalog.NewerDefaults = FirstTime("pin defaults 1.2.5");
            Catalog.Bind(Config);
            Waiting = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.jekkle.valheim.carturmappins");
            if (Waiting) Logger.LogWarning("Cartur's Map Pins is loaded: Auto Map Pins does nothing this session (remove Cartur's Map Pins).");
            PatchModule(typeof(Tracker));
            PatchModule(typeof(MapPins));
            Windows.Patch(ModuleHarmony);
            Windows.Use(gameObject);
            var texts = Lang.Check();
            if (texts != null) Logger.LogWarning("Translations: " + texts);
        }

        private void Update()
        {
            if (Waiting) return;
            Tracker.Tick(Time.unscaledDeltaTime); // real time: also while the game is paused (F1 open)
            MapView.Update();
        }

        // After the game's coroutines (it shows a new pin name a second after creating it): names this mod hides stay hidden.
        private void LateUpdate()
        {
            if (!Waiting) MapPins.KeepNamesHidden();
        }

        // Every plugin has started (Tidy Pins' settings exist).
        private void Start()
        {
            if (!Waiting) MapPins.HookTidyPins(ModuleHarmony);
        }

        private void OnDestroy()
        {
            MapPins.UnhookTidyPins();
            Subscribe(false);
            Catalog.Changed -= MapPins.RefreshAll;
            Catalog.Recolored -= MapPins.Restyle;
            Records.Save();
            Catalog.SaveIfChanged();
            MapPins.Clear();
            MapView.Clear();
            Records.Clear();
            Tracker.Reset();
            Lairs.Reset();
            if (PinList.IsOpen) Windows.Close();
            Windows.Release(gameObject);
        }

        // Names, icon style and the portal switches change the pins themselves; sizes, fading, colour, zoom and the
        // filters only their look.
        private static void Subscribe(bool on)
        {
            foreach (var entry in Names) Hook(entry, OnStyleChanged, on);
            Hook(_portalIcon, OnStyleChanged, on);
            Hook(_lairSpawners, OnStyleChanged, on);
            Hook(_style, OnStyleChanged, on);
            Hook(_portals, OnStyleChanged, on);
            Hook(_portalNames, OnStyleChanged, on);
            foreach (var entry in Sizes) Hook(entry, OnLookChanged, on);
            foreach (var entry in Minimaps) Hook(entry, OnLookChanged, on);
            foreach (var entry in Levels) Hook(entry, OnLookChanged, on);
            Hook(_portalMinimap, OnLookChanged, on);
            Hook(_size, OnLookChanged, on);
            Hook(_faded, OnLookChanged, on);
            Hook(_portalSize, OnLookChanged, on);
            Hook(_traderSize, OnLookChanged, on);
            Hook(_nameSize, OnLookChanged, on);
            Hook(_portalColor, OnStyleChanged, on); // also the glow behind the portal icons
            Hook(_zoomLevels, OnLookChanged, on);
            Hook(_zoomOut, OnLookChanged, on);
            Hook(_zoomIn, OnLookChanged, on);
            Hook(_zoomNames, OnLookChanged, on);
            Hook(_merge, OnLookChanged, on);
            Hook(_filters, OnLookChanged, on);
            Hook(_hidden, OnLookChanged, on);
            Hook(_counts, OnLookChanged, on);
            Hook(_buttonSize, OnButtonSize, on);
            Hook(_portalLevel, OnLookChanged, on);
            Hook(_hover, OnHoverChanged, on);
            Hook(_restore, OnRestore, on);
        }

        private static void Hook<T>(ConfigEntry<T> entry, System.EventHandler handler, bool on)
        {
            if (entry == null) return;
            if (on) entry.SettingChanged += handler;
            else entry.SettingChanged -= handler;
        }

        private static void OnStyleChanged(object sender, System.EventArgs e) => MapPins.RefreshAll();

        private static void OnLookChanged(object sender, System.EventArgs e)
        {
            MapView.ButtonsChanged();
            MapPins.Restyle();
        }

        private static void OnHoverChanged(object sender, System.EventArgs e) => MapView.HideTip();

        private static void OnButtonSize(object sender, System.EventArgs e)
        {
            MapView.RebuildBar();
            OnLookChanged(sender, e);
        }

        private static void OnRestore(object sender, System.EventArgs e)
        {
            if (!_restore.Value) return;
            foreach (var record in Records.All) record.Deleted = false;
            Records.Dirty = true;
            MapPins.RefreshAll();
            _restore.Value = false;
        }
    }
}
