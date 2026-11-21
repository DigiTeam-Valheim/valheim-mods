// Digi's Ultimate Builder (Thunderstore: Ultimate_Builder; until 1.4.0 "Creative World", the ID stays
// digi.valheim.creativeworld so settings carry over): a building test world. Only in worlds with the set name
// ("Creative" by default; make a NEW world with that name):
// - Terrain: flat ground in a big circle around the middle of the world (1500 m radius by default), all Meadows, easing
//   back into the normal land over the edge. No trees, rocks, bushes or grass there, and no locations except the spawn
//   temple. The ground is worked out live from the world generator, so nothing is stored in the save.
// - Player: every building piece unlocked and free, no workbench needed (the game's own no-cost mode, also free
//   crafting), double-tap jump to fly (Space up, Left Ctrl down, run = faster), no damage, tools keep full durability,
//   flying passes through the ground and buildings (also the camera), fly speed sliders, infinite stamina, nothing drops and no debris when you remove or break things, a hammer if you have none, and every piece in the hammer menu in full colour (the game greys out what you lack the
//   materials for, even in no-cost mode).
// - Creatures: a switch for enemies and one for animals (deer, boars, fish, birds, ...): while on, they do not spawn.
// - Mirror building (Mirror.cs): N sets a mirror line, Alt+N a second one; placing and removing is mirrored (with two
//   lines into all four quarters), Shift+N mirrors a building.
// - Wood Swap (WoodSwap.cs): a hammer tool per wood type; click a wooden piece and it becomes that wood, same shape on
//   the same snap points (Shift: the whole building's pieces of that wood; mirror lines are followed).
// - Build Materials (BuildMaterials.cs): a hammer tool; click a piece and a window lists every material the building
//   connected to it costs, with the totals (and how many of each piece).
// - Schematics (Schematics.cs): Save Schematic saves a building to a file with a picture; Schematics lists them (Place,
//   Mirrored, Materials, Delete) and pastes one where you aim, shown as a ghost first (wheel turns it); Undo last paste.
// - Remove Building (RemoveBuilding.cs): click a building twice (within 5 s) to remove every connected piece.
// - Stability meter (DigiShared/StabilityMeter.cs): below the crosshair while the hammer is out, how stable the aimed
//   piece will end up, red "Will collapse" when it will come down (off while Building Tweaks' Stability Meter is on).
// - Free fuel: fires, torches, braziers, candles, the hot tub, ovens, smelters and shield generators stay full.
// - Stations stand still: smelters, kilns, the windmill, the spinning wheel, ovens, the frost foundry, cooking stations
//   and fermenters make nothing and burn no fuel (their clock stands still). Crafting and Automation's stations take
//   nothing from chests here either (it asks this mod's _active).
// - Build distance: a slider for how far from you pieces can be placed and removed (the game's aiming ray stops at 50 m).
// - No raids: no raid or other event starts.
// - Building stability (on by default): off, nothing falls down for lack of support (truly creative); on, buildings need
//   support as anywhere, to test whether they would stand.
// - Iron inside timber (IronCores.cs, off by default): timber beams and poles get Wood Iron pieces inside them.
// - The tools' icons (Icons.cs, make_creative_icons.py): one style, a gold pictogram and a label.
// - Weather: the time stands still (noon by default, like the game's tod command; a slider for any time of the day or
//   night, and Page Up / Page Down for an hour later / earlier), no rain (clear weather), no fog. Switched off, the time
//   runs and the weather changes again.
// Other worlds and the main menu are left alone. All of it only applies while you play the creative world.
using System;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using DigiShared;
using UnityEngine;

namespace DigiCreative
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "digi.valheim.creativeworld";
        public const string Name = "Digi's Ultimate Builder";
        public const string Version = "1.6.9";

        private static ConfigEntry<string> _worlds;
        private static ConfigEntry<float> _radius, _edge, _height, _centerX, _centerZ;
        private static ConfigEntry<bool> _fly, _god, _noCost, _tools, _hammer, _noEnemies, _noAnimals, _noTrees, _noGrass, _onlyTemple;
        private static ConfigEntry<bool> _alwaysDay, _noRain, _noFog, _stamina, _noDrops, _through, _noDebris, _freeFuel;
        private static ConfigEntry<bool> _stability, _stabilityValue, _stillStations, _noRaids, _buildStability;
        private static ConfigEntry<float> _buildDistance;
        private static bool _noclip;
        private static int _cameraMask;
        private static float _waterDistance;
        private static ConfigEntry<float> _timeOfDay, _flySpeed, _flyRunSpeed;
        private static ConfigEntry<KeyboardShortcut> _laterKey, _earlierKey;
        private static bool _heldTime, _heldWeather; // this mod stopped the clock / the weather in the current world

        // Read by the world generator, also on its terrain threads: set once when a world starts.
        private static volatile bool _active;
        private static float _r, _rEdge, _edgeLen, _h, _cx, _cz;

        private static bool _gaveHammer;
        private static float _lastJump = -10f, _timer;

        private void Awake()
        {
            _worlds = Config.Bind("1 - World", "World name", "Creative",
                "Worlds with this name are creative worlds (several names: separate with commas). Make a NEW world with this " +
                "name: trees, grass and locations are only left out when an area is generated for the first time.");
            _radius = Config.Bind("2 - Terrain", "Flat radius", 1500f, new ConfigDescription(
                "Radius of the flat area around the centre, in metres.", new AcceptableValueRange<float>(100f, 5000f)));
            _edge = Config.Bind("2 - Terrain", "Edge", 200f, new ConfigDescription(
                "Over how many metres past the flat area the ground eases back into the normal land.", new AcceptableValueRange<float>(20f, 1000f)));
            _height = Config.Bind("2 - Terrain", "Ground height", 6f, new ConfigDescription(
                "Height of the flat ground above the sea, in metres.", new AcceptableValueRange<float>(1f, 100f)));
            _centerX = Config.Bind("2 - Terrain", "Centre X", 0f, "Centre of the flat area (east-west). 0, 0 is the middle of the world, where you spawn.");
            _centerZ = Config.Bind("2 - Terrain", "Centre Z", 0f, "Centre of the flat area (north-south).");
            _noTrees = Config.Bind("2 - Terrain", "No trees and rocks", true, "No trees, rocks, bushes, ore or plants in the flat area and its edge.");
            _noGrass = Config.Bind("2 - Terrain", "No grass", true, "No grass in the flat area and its edge.");
            _onlyTemple = Config.Bind("2 - Terrain", "Only the spawn temple", true, "No locations (ruins, altars, dungeons, traders) in the flat area and its edge, except the spawn temple.");
            _noCost = Config.Bind("3 - Player", "Free building", true, "Every building piece unlocked and free, no workbench needed (the game's no-cost mode; crafting is free too).");
            _fly = Config.Bind("3 - Player", "Fly", true, "Double-tap jump to fly or land. Flying: Space up, Left Ctrl down, run key = faster.");
            _flySpeed = Config.Bind("3 - Player", "Fly speed", 8f, new ConfigDescription(
                "How fast you fly, in metres a second (the game's own is 20).", new AcceptableValueRange<float>(1f, 60f)));
            _flyRunSpeed = Config.Bind("3 - Player", "Fly speed running", 20f, new ConfigDescription(
                "How fast you fly while holding the run key, in metres a second (the game's own is 50; steps of 2.5).",
                new AcceptableValueRange<float>(2.5f, 150f)));
            _through = Config.Bind("3 - Player", "Fly through everything", true,
                "While flying you pass through the ground, buildings and everything else, and the camera does too.");
            _god = Config.Bind("3 - Player", "No damage", true, "You take no damage (also no fall damage).");
            _tools = Config.Bind("3 - Player", "Tools never wear", true, "Hammer, hoe, cultivator and other tools stay at full durability.");
            _stamina = Config.Bind("3 - Player", "Infinite stamina", true, "Running, jumping, swimming and building use no stamina.");
            _noDrops = Config.Bind("3 - Player", "No drops", true, "Removing or breaking a building piece (or anything else) drops nothing.");
            _noDebris = Config.Bind("3 - Player", "No debris", true,
                "A destroyed or removed building piece disappears at once: no loose pieces falling apart, no dust and no " +
                "breaking sound (removing a whole building would make thousands).");
            _hammer = Config.Bind("3 - Player", "Give a hammer", true, "Puts a hammer in your inventory when you arrive without one.");
            _buildDistance = Config.Bind("3 - Player", "Build distance (m)", 20f, new ConfigDescription(
                "How far from you pieces can be placed and removed (the game's own reach is about 5 m; its aiming ray stops at " +
                "50 m). Works right away.", new AcceptableValueRange<float>(5f, 50f)));
            _buildStability = Config.Bind("3 - Player", "Building stability", true,
                "On: buildings need support as anywhere else, so you can test whether a building would stand. Off: nothing " +
                "falls down for lack of support, pieces may float in the air (truly creative). Switched on again, what has no " +
                "support comes down.");
            _freeFuel = Config.Bind("3 - Player", "Free fuel", true,
                "Fires, torches, braziers, candles, the hot tub, ovens, smelters, blast furnaces, the eitr refinery and shield " +
                "generators stay full of fuel: no wood, coal or sap needed, nothing goes out.");
            _stillStations = Config.Bind("3 - Player", "Stations stand still", true,
                "Smelters, kilns (also the Frigid Kiln), the windmill, the spinning wheel, ovens, the frost foundry, cooking " +
                "stations and fermenters make nothing and burn no fuel in the creative world: they are only for building. " +
                "Crafting and Automation's stations take nothing from chests there either. Off: they work as anywhere else.");
            _noEnemies = Config.Bind("3 - Player", "No enemies", true,
                "No enemies spawn in the creative world (greydwarfs, skeletons, trolls, raids, bosses' minions, ...).");
            _noAnimals = Config.Bind("3 - Player", "No animals", true,
                "No animals spawn in the creative world (deer, boars, hares, necks, fish, birds, ...).");
            _noRaids = Config.Bind("3 - Player", "No raids", true,
                "No raid (\"The forest is moving...\", \"The ground is shaking\", ...) or other event starts in the creative world.");
            // Before 1.1.0 one switch, "No creatures", did both: its value carries over once.
            var old = Config.Bind("3 - Player", "No creatures", true, "");
            if (!old.Value) _noEnemies.Value = _noAnimals.Value = false;
            Config.Remove(old.Definition);
            Config.Save();
            _alwaysDay = Config.Bind("4 - Weather", "Always day", true,
                "The time of day stands still at Time of day (noon unless you change it). Off: the time runs as normal.");
            _timeOfDay = Config.Bind("4 - Weather", "Time of day", 0.5f, new ConfigDescription(
                "The time it stands still at: 0 midnight, 0.25 sunrise (6:00), 0.5 noon, 0.75 sunset (18:00); it is night " +
                "before about 0.22 and after about 0.78. Changes at once; Page Up / Page Down move it an hour.",
                new AcceptableValueRange<float>(0f, 1f)));
            _laterKey = Config.Bind("4 - Weather", "Later key", new KeyboardShortcut(KeyCode.PageUp),
                "One hour later (also switches Always day on, so the time stays there).");
            _earlierKey = Config.Bind("4 - Weather", "Earlier key", new KeyboardShortcut(KeyCode.PageDown),
                "One hour earlier (also switches Always day on).");
            _noRain = Config.Bind("4 - Weather", "No rain", true, "Always clear weather: no rain, storms or snow.");
            _noFog = Config.Bind("4 - Weather", "No fog", true, "No fog at all, so you see to the horizon.");
            // Loaded while the game runs (ScriptEngine reads BepInEx\scripts from memory, so no file location; the dev
            // unloader has removed the startup copy's patches and main part): an own patch ID per load, so a reload never
            // removes the next copy's patches; the earlier copy's other parts (mirror lines, tools) go too.
            _live = string.IsNullOrEmpty(typeof(Plugin).Assembly.Location);
            if (_live) RemoveEarlierCopies();
            var harmony = _harmony = Harmony.CreateAndPatchAll(typeof(Plugin), _live ? Guid + ".live" + DateTime.Now.Ticks : Guid);
            Mirror.InCreative = () => _active;
            Mirror.Bind(Config);
            Mirror.Patch(harmony);
            gameObject.AddComponent<Mirror>();
            IronCores.InCreative = () => _active;
            IronCores.Log = Logger;
            IronCores.Bind(Config);
            IronCores.Patch(harmony);
            Tools.InCreative = () => _active;
            Tools.Log = Logger;
            Tools.Patch(harmony);
            Windows.Patch(harmony);
            gameObject.AddComponent<Windows>();
            BuildMaterials.Bind(Config);
            BuildMaterials.Register();
            gameObject.AddComponent<BuildMaterials>();
            WoodSwap.Bind(Config, Logger);
            WoodSwap.Register();
            gameObject.AddComponent<WoodSwap>();
            Schematics.Bind(Config, Logger);
            Schematics.Register();
            gameObject.AddComponent<Schematics>();
            RemoveBuilding.Bind(Config);
            RemoveBuilding.Register();
            gameObject.AddComponent<RemoveBuilding>();
            _stability = Config.Bind("3 - Player", "Stability meter", true,
                "While the hammer is out, a small bar and percentage below the crosshair show how stable the piece you aim at " +
                "will end up (the game takes a while to work support out), red \"Will collapse\" when it will come down. " +
                "With Building Tweaks' Stability Meter switched on, that one shows instead.");
            _stabilityValue = Config.Bind("3 - Player", "Stability meter numbers", false,
                "The stability meter also shows the support numbers behind the percentage: the final support, the most this " +
                "material can have, and the least it needs to stand (below that it collapses).");
            StabilityMeter.Wanted = () => _active && _stability.Value && _buildStability.Value && !Tools.BuildingTweaksHas("Stability Meter");
            StabilityMeter.ShowValue = () => _stabilityValue.Value;
            StabilityMeter.Patch(harmony);
            gameObject.AddComponent<StabilityMeter>();
            gameObject.AddComponent<Tools>();
            // Loaded in the middle of a world: that world counts at once (the world generator started before this copy).
            if (_live && WorldGenerator.instance != null) WorldStarted(WorldGenerator.instance.m_world);
            if (_live) Diagnostics.Patch(harmony, Logger);
        }

        private static bool _live;
        private Harmony _harmony;

        private void OnDestroy() => _harmony?.UnpatchSelf();

        private void RemoveEarlierCopies()
        {
            var manager = BepInEx.Bootstrap.Chainloader.ManagerObject;
            if (manager == null) return;
            foreach (var part in manager.GetComponents<MonoBehaviour>())
                if (part != null && part.GetType().Namespace == typeof(Plugin).Namespace && part.GetType().Assembly != typeof(Plugin).Assembly)
                {
                    Logger.LogInfo("Removed the earlier copy's " + part.GetType().Name);
                    Destroy(part);
                }
        }

        private static bool IsCreative(World world)
        {
            if (world == null || world.m_menu) return false;
            foreach (var name in (_worlds.Value ?? "").Split(','))
                if (name.Trim().Length > 0 && string.Equals(name.Trim(), world.m_name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // --- Terrain ---------------------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.Initialize))]
        [HarmonyPostfix]
        private static void WorldStarted(World world)
        {
            _r = _radius.Value;
            _edgeLen = _edge.Value;
            _rEdge = _r + _edgeLen;
            _h = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel + _height.Value : 30f + _height.Value;
            _cx = _centerX.Value;
            _cz = _centerZ.Value;
            _active = IsCreative(world);
            _gaveHammer = false;
            _heldTime = _heldWeather = false; // a new world has its own clock and weather
            if (_active) BepInEx.Logging.Logger.CreateLogSource(Name).LogInfo($"creative world '{world.m_name}': flat {_r} m around ({_cx}, {_cz}) at {_h} m");
        }

        // 1 inside the flat area, easing to 0 at the end of the edge.
        private static float Weight(float x, float z)
        {
            var dx = x - _cx;
            var dz = z - _cz;
            var d2 = dx * dx + dz * dz;
            if (d2 <= _r * _r) return 1f;
            if (d2 >= _rEdge * _rEdge) return 0f;
            var t = 1f - (Mathf.Sqrt(d2) - _r) / _edgeLen;
            return t * t * (3f - 2f * t);
        }

        // Within the flat area or its edge (plus some metres).
        private static bool Inside(Vector3 p, float extra) => InArea(p.x, p.z, extra);

        private static bool InArea(float x, float z, float extra)
        {
            var dx = x - _cx;
            var dz = z - _cz;
            var r = _rEdge + extra;
            return dx * dx + dz * dz < r * r;
        }

        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetBiome), typeof(float), typeof(float), typeof(float), typeof(bool))]
        [HarmonyPostfix]
        private static void Biome(float wx, float wy, ref Heightmap.Biome __result)
        {
            if (_active && InArea(wx, wy, 0f)) __result = Heightmap.Biome.Meadows;
        }

        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeHeight))]
        [HarmonyPostfix]
        private static void Height(float wx, float wy, ref float __result)
        {
            if (!_active) return;
            var w = Weight(wx, wy);
            if (w > 0f) __result = Mathf.Lerp(__result, _h, w);
        }

        [HarmonyPatch(typeof(ZoneSystem), "PlaceVegetation")]
        [HarmonyPrefix]
        private static bool Vegetation(Vector3 zoneCenterPos) => !(_active && _noTrees.Value && Inside(zoneCenterPos, 46f));

        [HarmonyPatch(typeof(ZoneSystem), "PlaceLocations")]
        [HarmonyPrefix]
        private static bool Locations(ZoneSystem __instance, Vector2s zoneID)
        {
            if (!_active || !_onlyTemple.Value) return true;
            if (!__instance.m_locationInstances.TryGetValue(zoneID, out var instance) || instance.m_placed) return true;
            if (!Inside(instance.m_position, 0f) || instance.m_location?.m_prefabName == "StartTemple") return true;
            instance.m_placed = true; // left out for good
            __instance.m_locationInstances[zoneID] = instance;
            return false;
        }

        [HarmonyPatch(typeof(ClutterSystem), "GetPatchBiomes")]
        [HarmonyPostfix]
        private static void Grass(Vector3 center, ref Heightmap.Biome __result)
        {
            if (_active && _noGrass.Value && Inside(center, 0f)) __result = Heightmap.Biome.None;
        }

        // --- Weather ---------------------------------------------------------------------------------------------------

        // SetEnv rebuilds the fog from the weather every frame; after everyone else (Fog Reducer scales it), set it to none.
        [HarmonyPatch(typeof(EnvMan), "SetEnv")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Fog()
        {
            if (_active && _noFog.Value) RenderSettings.fogDensity = 0f;
        }

        // --- Player ----------------------------------------------------------------------------------------------------

        // The hammer menus colour a piece by "can build", which ignores no-cost mode: with free building, everything can.
        // Last, after Local Craft From Storage, which works out the same answer from the chests nearby.
        [HarmonyPatch(typeof(Player), nameof(Player.HaveRequirements), typeof(Piece), typeof(Player.RequirementMode))]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void CanBuild(Player __instance, Player.RequirementMode mode, ref bool __result)
        {
            if (!__result && _active && mode == Player.RequirementMode.CanBuild && __instance.m_noPlacementCost && __instance == Player.m_localPlayer)
                __result = true;
        }

        // The new build menu's buttons: Local Craft From Storage colours them itself, so set the colour after it.
        [HarmonyPatch(typeof(BuildUiPieceButton), nameof(BuildUiPieceButton.UpdateRequirements))]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void ButtonColour(BuildUiPieceButton __instance)
        {
            var player = Player.m_localPlayer;
            if (_active && player != null && player.m_noPlacementCost && __instance.m_icon != null) __instance.m_icon.color = Color.white;
        }

        // Removing or breaking a piece gives its materials back through DropResources; other objects drop their list.
        [HarmonyPatch(typeof(Piece), nameof(Piece.DropResources))]
        [HarmonyPrefix]
        private static bool PieceDrops() => !(_active && _noDrops.Value);

        // A destroyed piece falls apart into loose physics copies of its meshes (RPC_CreateFragments, for everyone).
        [HarmonyPatch(typeof(WearNTear), "RPC_CreateFragments")]
        [HarmonyPrefix]
        private static bool Fragments() => !(_active && _noDebris.Value);

        // The dust and the breaking sound a piece makes when it goes: left out too (the piece is gone right after).
        private static readonly EffectList NoEffects = new EffectList { m_effectPrefabs = new EffectList.EffectData[0] };

        [HarmonyPatch(typeof(WearNTear), "Destroy", typeof(HitData), typeof(bool))]
        [HarmonyPrefix]
        private static void NoDust(WearNTear __instance)
        {
            if (_active && _noDebris.Value) __instance.m_destroyedEffect = NoEffects;
        }

        // Free fuel: before the game burns fuel, the tank is full again (only where this PC runs the object).
        private static bool FreeFuel(ZNetView view) => _active && _freeFuel.Value && view != null && view.IsValid() && view.IsOwner();

        [HarmonyPatch(typeof(Fireplace), "UpdateFireplace")]
        [HarmonyPrefix]
        private static void FullFire(Fireplace __instance)
        {
            if (__instance.m_infiniteFuel || !FreeFuel(__instance.m_nview)) return;
            var zdo = __instance.m_nview.GetZDO();
            if (zdo.GetFloat(ZDOVars.s_fuel) < __instance.m_maxFuel) zdo.Set(ZDOVars.s_fuel, __instance.m_maxFuel);
        }

        [HarmonyPatch(typeof(Smelter), "UpdateSmelter")]
        [HarmonyPrefix]
        private static void FullSmelter(Smelter __instance)
        {
            if (__instance.m_maxFuel <= 0 || !FreeFuel(__instance.m_nview)) return;
            if (__instance.GetFuel() < __instance.m_maxFuel) __instance.SetFuel(__instance.m_maxFuel);
        }

        [HarmonyPatch(typeof(CookingStation), "UpdateCooking")]
        [HarmonyPrefix]
        private static void FullOven(CookingStation __instance)
        {
            if (!__instance.m_useFuel || __instance.m_maxFuel <= 0 || !FreeFuel(__instance.m_nview)) return;
            if (__instance.GetFuel() < __instance.m_maxFuel) __instance.SetFuel(__instance.m_maxFuel);
        }

        // No raids: a raid (picked by the game or started with the console's event command) starts through SetRandomEvent;
        // in the creative world none does. Stopping one still goes through.
        [HarmonyPatch(typeof(RandEventSystem), "SetRandomEvent")]
        [HarmonyPrefix]
        private static bool NoRaid(RandomEvent ev) => !(_active && _noRaids.Value && ev != null);

        // Building stability off: no piece lacks support, so none takes the "no support" damage that brings it down. The
        // game still works support out (the hammer's colours stay as they are).
        [HarmonyPatch(typeof(WearNTear), "HaveSupport")]
        [HarmonyPostfix]
        private static void AlwaysSupported(ref bool __result)
        {
            if (!__result && _active && !_buildStability.Value) __result = true;
        }

        // Stations stand still: the time that passed for them is 0, so nothing cooks, smelts or ferments and no fuel burns.
        // Last, after Crafting and Automation's speed and pause changes. (With free fuel the Frigid Kiln, which makes liquid
        // frost from its fuel alone, made it without end.)
        private static bool Still => _active && _stillStations.Value;

        [HarmonyPatch(typeof(Smelter), "GetDeltaTime")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void SmelterStill(ref double __result)
        {
            if (Still) __result = 0.0;
        }

        [HarmonyPatch(typeof(CookingStation), "GetDeltaTime")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void CookingStill(ref float __result)
        {
            if (Still) __result = 0f;
        }

        [HarmonyPatch(typeof(Fermenter), "GetFermentationTime")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void FermenterStill(ref double __result)
        {
            if (Still && __result > 0.0) __result = 0.0;
        }

        [HarmonyPatch(typeof(ShieldGenerator), "UpdateShield")]
        [HarmonyPrefix]
        private static void FullShield(ShieldGenerator __instance)
        {
            if (__instance.m_maxFuel <= 0 || !FreeFuel(__instance.m_nview)) return;
            var zdo = __instance.m_nview.GetZDO();
            if (zdo.GetFloat(ZDOVars.s_fuel, __instance.m_defaultFuel) < __instance.m_maxFuel) zdo.Set(ZDOVars.s_fuel, (float)__instance.m_maxFuel);
        }

        [HarmonyPatch(typeof(DropOnDestroyed), "OnDestroyed")]
        [HarmonyPrefix]
        private static bool ObjectDrops() => !(_active && _noDrops.Value);

        // --- Creatures --------------------------------------------------------------------------------------------------

        // Animals: the plant eaters (deer, boars, hares, ...) and what is no creature at all (fish, birds). Enemies: every
        // other creature.
        private static bool Blocked(GameObject prefab)
        {
            if (!_active || prefab == null) return false;
            var character = prefab.GetComponent<Character>();
            var animal = character == null || character.m_faction == Character.Faction.AnimalsVeg;
            return animal ? _noAnimals.Value : _noEnemies.Value;
        }

        // The world's random spawns (also raids).
        [HarmonyPatch(typeof(SpawnSystem), "Spawn")]
        [HarmonyPrefix]
        private static bool RandomSpawn(SpawnSystem.SpawnData critter) => !Blocked(critter?.m_prefab);

        // Fixed spawn points (in camps and dungeons).
        [HarmonyPatch(typeof(CreatureSpawner), "Spawn")]
        [HarmonyPrefix]
        private static bool PointSpawn(CreatureSpawner __instance) => !Blocked(__instance.m_creaturePrefab);

        // Nests and bone piles.
        [HarmonyPatch(typeof(SpawnArea), "SelectWeightedPrefab")]
        [HarmonyPostfix]
        private static void NestSpawn(ref SpawnArea.SpawnData __result)
        {
            if (__result != null && Blocked(__result.m_prefab)) __result = null;
        }

        [HarmonyPatch(typeof(Player), nameof(Player.UseStamina))]
        [HarmonyPrefix]
        private static bool Stamina(Player __instance) => !(_active && _stamina.Value && __instance == Player.m_localPlayer);

        private void Update()
        {
            var player = Player.m_localPlayer;
            Noclip(player);
            if (!_active || player == null || ZNet.instance == null) return;
            if (_fly.Value && Typing() == false && (ZInput.GetButtonDown("Jump") || ZInput.GetButtonDown("JoyJump")))
            {
                if (Time.time - _lastJump < 0.35f)
                {
                    player.ToggleDebugFly();
                    _lastJump = -10f;
                }
                else _lastJump = Time.time;
            }
            // The game multiplies the fly speed by 2.5 while running.
            if (player.IsDebugFlying())
                Character.m_debugFlySpeed = Mathf.Max(1, Mathf.RoundToInt(player.m_run ? _flyRunSpeed.Value / 2.5f : _flySpeed.Value));
            if (!Typing())
            {
                var step = _laterKey.Value.IsDown() ? 1 : _earlierKey.Value.IsDown() ? -1 : 0;
                if (step != 0)
                {
                    var hours = Mathf.Round(_timeOfDay.Value * 24f) + step;
                    hours = (hours % 24f + 24f) % 24f;
                    _timeOfDay.Value = hours / 24f;
                    _alwaysDay.Value = true;
                    _timer = 1f; // right now, not at the next half second
                    player.Message(MessageHud.MessageType.TopLeft, $"Time {hours:00}:00");
                }
            }
            _timer += Time.deltaTime;
            if (_timer < 0.5f) return;
            _timer = 0f;
            if (_noCost.Value && !player.m_noPlacementCost) player.SetNoPlacementCost(true);
            // The reach for placing and removing (Build Camera sets it far out while it flies and puts it back after: left
            // alone meanwhile).
            if (player.m_maxPlaceDistance < 999f && !Mathf.Approximately(player.m_maxPlaceDistance, _buildDistance.Value))
                player.m_maxPlaceDistance = _buildDistance.Value;
            var env = EnvMan.instance;
            if (env != null)
            {
                if (_alwaysDay.Value)
                {
                    env.m_debugTimeOfDay = true;
                    env.m_debugTime = _timeOfDay.Value;
                    _heldTime = true;
                }
                else if (_heldTime)
                {
                    env.m_debugTimeOfDay = false; // the clock runs again
                    _heldTime = false;
                }
                if (_noRain.Value)
                {
                    if (env.m_forceEnv != "Clear") env.SetForceEnvironment("Clear");
                    _heldWeather = true;
                }
                else if (_heldWeather)
                {
                    env.SetForceEnvironment(""); // the weather changes again
                    _heldWeather = false;
                }
            }
            if (_god.Value && !player.InGodMode()) player.SetGodMode(true);
            var inventory = player.GetInventory();
            if (_tools.Value)
                foreach (var item in inventory.GetAllItems())
                    if (item.m_shared.m_useDurability && item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Tool && item.m_durability < item.GetMaxDurability())
                        item.m_durability = item.GetMaxDurability();
            if (_hammer.Value && !_gaveHammer && !player.IsTeleporting() && !player.InIntro())
            {
                _gaveHammer = true;
                if (!inventory.GetAllItems().Any(i => i.m_dropPrefab != null && i.m_dropPrefab.name == "Hammer"))
                {
                    var prefab = ObjectDB.instance.GetItemPrefab("Hammer");
                    if (prefab != null) inventory.AddItem(prefab, 1);
                }
            }
        }

        // Local Craft From Storage blocks the game's colour function and paints the icons itself from the chests nearby, so
        // while the hammer menu is open, set them to full colour every frame, after everyone else.
        private void LateUpdate()
        {
            var player = Player.m_localPlayer;
            var hud = Hud.instance;
            if (!_active || player == null || !player.m_noPlacementCost || hud == null || !Hud.IsPieceSelectionVisible()) return;
            if (hud.m_buildUi != null)
                foreach (var button in hud.m_buildUi.m_pieceButtons)
                    if (button != null && button.m_icon != null && button.m_icon.color != Color.white) button.m_icon.color = Color.white;
            foreach (var icon in hud.m_pieceIcons)
                if (icon?.m_icon != null && icon.m_icon.color != Color.white) icon.m_icon.color = Color.white;
        }

        // While flying: the body collides with nothing, the camera is not blocked and may go below sea level, and the game's
        // under-the-world check (UnderWorldCheck: back on the ground when more than 1 m below it) is skipped. Landing inside
        // the ground puts you back on top.
        [HarmonyPatch(typeof(Character), "UnderWorldCheck")]
        [HarmonyPrefix]
        private static bool UnderWorld(Character __instance) => !(_noclip && __instance == Player.m_localPlayer);

        private static void Noclip(Player player)
        {
            var want = _active && _through.Value && player != null && player.IsDebugFlying();
            if (want == _noclip) return;
            _noclip = want;
            if (player != null && player.m_body != null) player.m_body.detectCollisions = !want;
            var camera = GameCamera.instance;
            if (camera != null)
            {
                if (want)
                {
                    _cameraMask = camera.m_blockCameraMask.value;
                    camera.m_blockCameraMask = 0;
                    _waterDistance = camera.m_minWaterDistance;
                    camera.m_minWaterDistance = -100000f;
                }
                else
                {
                    if (_cameraMask != 0) camera.m_blockCameraMask = _cameraMask;
                    if (_waterDistance > -1000f) camera.m_minWaterDistance = _waterDistance;
                }
            }
            if (want || player == null || ZoneSystem.instance == null) return;
            var pos = player.transform.position;
            var ground = ZoneSystem.instance.GetGroundHeight(pos);
            if (pos.y >= ground - 0.3f) return;
            pos.y = ground + 0.2f;
            player.transform.position = pos;
            if (player.m_body == null) return;
            player.m_body.position = pos;
            player.m_body.linearVelocity = Vector3.zero;
        }

        private static bool Typing() =>
            (Chat.instance != null && Chat.instance.HasFocus()) || global::Console.IsVisible() || TextInput.IsVisible() ||
            Menu.IsVisible() || InventoryGui.IsVisible() || Minimap.IsOpen();
    }
}
