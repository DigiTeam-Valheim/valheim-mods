// Auto Map Pins, places of other mods: names, icons and switches for the locations of More World Locations AIO
// (warpalicious, 190 locations in 140 kinds; its prefab names as of 5.2.4). Without an entry here a mod's location is
// still listed (the catalog takes every location of the world), but under a name made from its prefab, off and only
// in F1's advanced settings. Here: the places worth a trip (traders, trainers, ports, the two dungeons, castles, forts,
// temples, churches, arenas, taverns, forges) are on by default; houses, huts, towers, ruins, shrines and the like are
// named and get an icon, but stay off and in the advanced settings (there are hundreds of them in a world).
// White is the white icon (game-icons.net, in the sheet made by make_map_icons.py, which reads this file too); Icon is
// the game icon style's icon: an item, or the map's own icon of a location ("loc:"), which More World Locations gives
// its traders, trainers and ports: where the map shows that icon, no second pin is added (as for Haldor).
using System;
using System.Collections.Generic;

namespace AutoMapPins
{
    internal static partial class Catalog
    {
        // Locations of one kind that are not one thing: two blacksmiths with their own names.
        private static readonly HashSet<string> Whole = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "MWL_BlackForestBlacksmith1", "MWL_BlackForestBlacksmith2",
        };

        // On by default and in F1 without "Advanced settings".
        private static Known ModWorth(string name, string white, string icon = null) =>
            new Known { Name = name, White = white, Icon = icon };

        // Off by default, only in F1's advanced settings.
        private static Known ModMinor(string name, string white, string icon = null) =>
            new Known { Name = name, White = white, Icon = icon, On = false, Advanced = true };

        // A trader, trainer or port: the pin says its name (the setting says what it is), with the map's own icon.
        private static Known ModTrader(string name, string label, string white, string location) =>
            new Known { Name = name, Label = label, White = white, Icon = "loc:" + location, Trader = true };

        private static Known ModDungeon(string name, string white) =>
            new Known { Name = name, White = white, Dungeon = true };

        // Names differ from the game's own places (a setting of the same name in a biome would be shared): "Swamp ruin",
        // "Swamp well", "Stone circle", "Stone tower", "Wood house" are the game's.
        private static readonly Dictionary<string, Known> ModPlaces = new Dictionary<string, Known>(StringComparer.OrdinalIgnoreCase)
        {
            // Traders, trainers and ports
            ["MWL_PlainsTavern"] = ModTrader("Torvin (tavern trader)", "Torvin", "lorc/beer-stein", "MWL_PlainsTavern1"),
            ["MWL_OceanTavern"] = ModTrader("Bjorn (sea tavern trader)", "Bjorn", "lorc/beer-stein", "MWL_OceanTavern1"),
            ["MWL_PlainsCamp"] = ModTrader("Ragnir (camp trader)", "Ragnir", "delapouite/camping-tent", "MWL_PlainsCamp1"),
            ["MWL_BlackForestBlacksmith1"] = ModTrader("Volund (blacksmith)", "Volund", "lorc/anvil", "MWL_BlackForestBlacksmith1"),
            ["MWL_BlackForestBlacksmith2"] = ModTrader("Sindri (blacksmith)", "Sindri", "lorc/anvil", "MWL_BlackForestBlacksmith2"),
            ["MWL_MountainsBlacksmith"] = ModTrader("Thorgrim (blacksmith)", "Thorgrim", "lorc/anvil", "MWL_MountainsBlacksmith1"),
            ["MWL_MistlandsBlacksmith"] = ModTrader("Dvalin (blacksmith)", "Dvalin", "lorc/anvil", "MWL_MistlandsBlacksmith1"),
            ["MWL_MeadowsTrainer"] = ModTrader("Eirik (trainer)", "Eirik", "delapouite/teacher", "MWL_MeadowsTrainer1"),
            ["MWL_SwampTrainer"] = ModTrader("Grimhild (trainer)", "Grimhild", "delapouite/teacher", "MWL_SwampTrainer1"),
            ["MWL_PlainsTrainer"] = ModTrader("Asmund (trainer)", "Asmund", "delapouite/teacher", "MWL_PlainsTrainer1"),
            ["MWL_MistTrainer"] = ModTrader("Sigrun (trainer)", "Sigrun", "delapouite/teacher", "MWL_MistTrainer1"),
            ["MWL_Port"] = ModTrader("Port", "Port", "lorc/anchor", "MWL_Port1"),
            // Dungeons
            ["BFD_Exterior"] = ModDungeon("Underground Ruins", "delapouite/underground-cave"),
            ["CD_Exterior"] = ModDungeon("Forbidden Catacombs", "delapouite/crypt-entrance"),
            // Meadows
            ["MWL_RuinsChurch"] = ModWorth("Ruined church", "delapouite/church"),
            ["MWL_RuinsArena"] = ModWorth("Ruined arena", "lorc/crossed-swords"),
            ["MWL_MeadowsTavern"] = ModWorth("Meadow tavern", "delapouite/tavern-sign"),
            ["MWL_Ruins"] = ModMinor("Old ruins", "delapouite/ancient-ruins"),
            ["MWL_RuinsWell"] = ModMinor("Ruined well", "delapouite/well"),
            ["MWL_MaypoleHut"] = ModMinor("Maypole hut", "delapouite/hut"),
            ["MWL_DeerShrine"] = ModMinor("Deer shrine", "lorc/stone-tablet"),
            ["MWL_MeadowsBarn"] = ModMinor("Meadow barn", "delapouite/barn"),
            ["MWL_VikingSacrifice"] = ModMinor("Sacrifice site", "lorc/sacrificial-dagger"),
            ["MWL_MeadowsHouse"] = ModMinor("Meadow house", "delapouite/house"),
            ["MWL_SmallMeadowHouse"] = ModMinor("Small meadow house", "delapouite/hut"),
            ["MWL_MeadowsRuin"] = ModMinor("Meadow ruin", "delapouite/ancient-ruins"),
            ["MWL_MeadowsTomb"] = ModMinor("Meadow tomb", "lorc/coffin"),
            ["MWL_MeadowsTower"] = ModMinor("Meadow tower", "delapouite/watchtower"),
            ["MWL_OakHut"] = ModMinor("Oak hut", "delapouite/hut"),
            ["MWL_SmallHouse"] = ModMinor("Small house", "delapouite/hut"),
            ["MWL_MeadowsFarm"] = ModMinor("Meadow farm", "delapouite/farmer"),
            ["MWL_MeadowsLighthouse"] = ModMinor("Lighthouse", "delapouite/lighthouse"),
            ["MWL_MeadowsSawmill"] = ModMinor("Sawmill", "delapouite/wood-pile"),
            ["MWL_MeadowsWall"] = ModMinor("Old wall", "delapouite/stone-wall"),
            ["MWL_StoneShrine"] = ModMinor("Stone shrine", "lorc/stone-tablet"),
            ["MWL_StoreHouseStone"] = ModMinor("Stone storehouse", "delapouite/stone-stack"),
            // Black Forest
            ["MWL_RuinsCastle"] = ModWorth("Ruined castle", "delapouite/castle"),
            ["MWL_Tavern"] = ModWorth("Forest tavern", "delapouite/tavern-sign"),
            ["MWL_ForestForge"] = ModWorth("Forest forge", "lorc/anvil"),
            ["MWL_ForestGreatHouse"] = ModWorth("Great house", "delapouite/viking-longhouse"),
            ["MWL_RuinsTower"] = ModMinor("Ruined tower", "lorc/stone-tower"),
            ["MWL_SupplyBarn"] = ModMinor("Supply barn", "delapouite/barn"),
            ["MWL_WoodTower"] = ModMinor("Wooden tower", "delapouite/watchtower"),
            ["MWL_ForestHouse"] = ModMinor("Forest house", "delapouite/house"),
            ["MWL_CrookedHut"] = ModMinor("Crooked hut", "delapouite/hut"),
            ["MWL_ForestRuin"] = ModMinor("Forest ruin", "delapouite/ancient-ruins"),
            ["MWL_ForestTower"] = ModMinor("Forest tower", "lorc/stone-tower"),
            ["MWL_MassGrave"] = ModMinor("Mass grave", "lorc/tombstone"),
            ["MWL_StoneFormation"] = ModMinor("Stone formation", "delapouite/dolmen"),
            ["MWL_GuardTower"] = ModMinor("Guard tower", "delapouite/watchtower"),
            ["MWL_RootRuins"] = ModMinor("Root ruins", "delapouite/tree-roots"),
            ["MWL_RootsTower"] = ModMinor("Root tower", "delapouite/tree-roots"),
            ["MWL_RuinedRootTower"] = ModMinor("Ruined root tower", "delapouite/tree-roots"),
            ["MWL_ForestSkull"] = ModMinor("Giant skull", "lorc/skull-crack"),
            ["MWL_ForestPillar"] = ModMinor("Forest pillar", "delapouite/obelisk"),
            ["MWL_CoastTower"] = ModMinor("Coast tower", "lorc/stone-tower"),
            ["MWL_ForestGrove"] = ModMinor("Forest grove", "lorc/pine-tree"),
            ["MWL_RockShrine"] = ModMinor("Rock shrine", "lorc/stone-tablet"),
            ["MWL_ForestCamp"] = ModMinor("Forest camp", "delapouite/forest-camp"),
            ["MWL_RuinedTower"] = ModMinor("Old tower", "lorc/stone-tower"),
            ["MWL_StoneTower"] = ModMinor("Tall stone tower", "lorc/stone-tower"),
            ["MWL_StoneTowerRuins"] = ModMinor("Collapsed stone tower", "lorc/stone-tower"),
            // Swamp
            ["MWL_SwampCastle"] = ModWorth("Swamp castle", "delapouite/castle"),
            ["MWL_SwampStakeFort"] = ModWorth("Stake fort", "delapouite/medieval-gate"),
            ["MWL_FortBakkarhalt"] = ModWorth("Fort Bakkarhalt", "lorc/locked-fortress"),
            ["MWL_Belmont"] = ModWorth("Belmont", "delapouite/castle"),
            ["MWL_SwampTemple"] = ModWorth("Swamp temple", "delapouite/greek-temple"),
            ["MWL_SwampSanctuary"] = ModWorth("Swamp sanctuary", "delapouite/temple-gate"),
            ["MWL_SwampComplex"] = ModWorth("Swamp complex", "delapouite/medieval-barracks"),
            ["MWL_GuckPit"] = ModMinor("Guck pit", "lorc/dripping-goo", "Guck"),
            ["MWL_SwampAltar"] = ModMinor("Swamp altar", "lorc/sacrificial-dagger"),
            ["MWL_SwampGrave"] = ModMinor("Swamp grave", "lorc/tombstone"),
            ["MWL_SwampHouse"] = ModMinor("Swamp house", "delapouite/house"),
            ["MWL_SwampRuin"] = ModMinor("Sunken ruin", "delapouite/ancient-ruins"),
            ["MWL_SwampTower"] = ModMinor("Swamp tower", "lorc/stone-tower"),
            ["MWL_SwampWell"] = ModMinor("Old swamp well", "delapouite/well"),
            ["MWL_AbandonedHouse"] = ModMinor("Abandoned house", "delapouite/house"),
            ["MWL_Treehouse"] = ModMinor("Treehouse", "delapouite/hut"),
            ["MWL_SwampTreeHut"] = ModMinor("Tree hut", "delapouite/hut"),
            ["MWL_Shipyard"] = ModMinor("Shipyard", "delapouite/drakkar"),
            ["MWL_SwampCourtyard"] = ModMinor("Swamp courtyard", "delapouite/ancient-ruins"),
            ["MWL_SwampBrokenTower"] = ModMinor("Broken tower", "lorc/stone-tower"),
            ["MWL_StoneCircle"] = ModMinor("Swamp stone circle", "delapouite/dolmen"),
            ["MWL_SwampTempleSmall"] = ModMinor("Small swamp temple", "delapouite/greek-temple"),
            ["MWL_SwampShrine"] = ModMinor("Swamp shrine", "lorc/stone-tablet"),
            ["MWL_CastleCorner"] = ModMinor("Castle corner", "delapouite/castle"),
            ["MWL_TreeTowers"] = ModMinor("Tree towers", "delapouite/watchtower"),
            ["MWL_StoneBeacon"] = ModMinor("Stone beacon", "lorc/campfire"),
            // Mountains
            ["MWL_StoneCastle"] = ModWorth("Stone castle", "delapouite/castle"),
            ["MWL_StoneFort"] = ModWorth("Stone fort", "lorc/locked-fortress"),
            ["MWL_StoneHall"] = ModWorth("Stone hall", "delapouite/viking-longhouse"),
            ["MWL_StoneTavern"] = ModWorth("Mountain tavern", "delapouite/tavern-sign"),
            ["MWL_MountainsTower"] = ModMinor("Mountain tower", "lorc/stone-tower"),
            ["MWL_MountainsPagota"] = ModMinor("Pagoda", "delapouite/pagoda"),
            ["MWL_MountainsHogan"] = ModMinor("Mountain hogan", "delapouite/hut"),
            ["MWL_WoodBarn"] = ModMinor("Wooden barn", "delapouite/barn"),
            ["MWL_WoodFarm"] = ModMinor("Mountain farm", "delapouite/farmer"),
            ["MWL_WoodHouse"] = ModMinor("Mountain wood house", "delapouite/house"),
            ["MWL_TempleShrine"] = ModMinor("Temple shrine", "delapouite/greek-temple"),
            ["MWL_StoneAltar"] = ModMinor("Stone altar", "lorc/sacrificial-dagger"),
            ["MWL_MountainDvergrShrine"] = ModMinor("Dvergr shrine", "kier-heyl/dwarf-helmet"),
            ["MWL_MountainOverlook"] = ModMinor("Overlook", "delapouite/binoculars"),
            ["MWL_MountainCultShrine"] = ModMinor("Cult shrine", "lorc/candle-holder"),
            ["MWL_MountainShrine"] = ModMinor("Mountain shrine", "lorc/stone-tablet"),
            ["MWL_MountainsAedicule"] = ModMinor("Aedicule", "delapouite/stone-bust"),
            // Plains
            ["MWL_GoblinFort"] = ModWorth("Fuling fort", "lorc/locked-fortress", "BlackMetalScrap"),
            ["MWL_FulingVillage"] = ModWorth("Large fuling village", "delapouite/goblin-camp", "BlackMetalScrap"),
            ["MWL_FulingTemple"] = ModWorth("Fuling temple", "delapouite/greek-temple", "GoblinTotem"),
            ["MWL_PlainsArena"] = ModWorth("Plains arena", "lorc/crossed-swords"),
            ["MWL_FulingTarPit"] = ModWorth("Fuling tar pit", "lorc/boiling-bubbles", "Tar"),
            ["MWL_FulingRock"] = ModMinor("Fuling rock", "lorc/rock"),
            ["MWL_PlainsPillar"] = ModMinor("Plains pillar", "delapouite/obelisk"),
            ["MWL_FulingTempleBroken"] = ModMinor("Broken fuling temple", "delapouite/greek-temple"),
            ["MWL_PlainsOracle"] = ModMinor("Oracle", "lorc/crystal-ball"),
            ["MWL_FulingWall"] = ModMinor("Fuling wall", "delapouite/stone-wall"),
            ["MWL_FulingTower"] = ModMinor("Fuling tower", "lorc/stone-tower"),
            ["MWL_RockGarden"] = ModMinor("Rock garden", "delapouite/stone-stack"),
            ["MWL_MinddripHallow"] = ModMinor("Minddrip Hallow", "lorc/drop"),
            ["MWL_StoneTopHouse"] = ModMinor("Stone top house", "delapouite/house"),
            ["MWL_FulingStoneHouse"] = ModMinor("Fuling stone house", "delapouite/house"),
            ["MWL_FulingStoneOpen"] = ModMinor("Fuling stone ruin", "delapouite/ancient-ruins"),
            // Mistlands
            ["MWL_MistFort"] = ModWorth("Mist fort", "lorc/locked-fortress"),
            ["MWL_SecretRoom"] = ModMinor("Secret room", "delapouite/key-lock"),
            ["MWL_MistWorkshop"] = ModMinor("Dvergr workshop", "lorc/hammer-nails"),
            ["MWL_MistTower"] = ModMinor("Mist tower", "lorc/stone-tower"),
            ["MWL_MistWall"] = ModMinor("Mist wall", "delapouite/stone-wall"),
            ["MWL_MistHut"] = ModMinor("Mist hut", "delapouite/hut"),
            ["MWL_DvergrEitrSingularity"] = ModMinor("Eitr singularity", "lorc/vortex"),
            ["MWL_DvergrHouse"] = ModMinor("Dvergr house", "kier-heyl/dwarf-helmet"),
            ["MWL_DvergrHouseWood"] = ModMinor("Wooden dvergr house", "kier-heyl/dwarf-helmet"),
            ["MWL_MarbleJail"] = ModMinor("Marble jail", "delapouite/prisoner"),
            ["MWL_MarbleHome"] = ModMinor("Marble home", "delapouite/house"),
            ["MWL_MarbleCliffAltar"] = ModMinor("Marble cliff altar", "lorc/sacrificial-dagger"),
            ["MWL_MistPond"] = ModMinor("Mist pond", "lorc/drop"),
            ["MWL_MarbleCage"] = ModMinor("Marble cage", "delapouite/cage"),
            ["MWL_DvergrKnowledgeExtractor"] = ModMinor("Knowledge extractor", "lorc/brain"),
            ["MWL_AncientShrine"] = ModMinor("Ancient shrine", "lorc/stone-tablet"),
            ["MWL_MistShrine"] = ModMinor("Mist shrine", "lorc/stone-tablet"),
            // Ashlands
            ["MWL_AshlandsFort"] = ModWorth("Ashlands fort", "lorc/locked-fortress"),
            ["MWL_AshTower"] = ModMinor("Ash tower", "lorc/stone-tower"),
            ["MWL_AshWallPost"] = ModMinor("Ash wall post", "delapouite/stone-wall"),
        };
    }
}
