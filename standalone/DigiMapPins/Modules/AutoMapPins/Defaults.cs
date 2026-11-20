// Auto Map Pins, the defaults per biome that differ from a thing's own (made by make_map_defaults.py from tuned
// settings): what is not worth a pin where it is everywhere (berries, mushrooms, common ore, chests) and a few places
// that are. Only defaults: a switch someone has set stays as it is.
using System.Collections.Generic;

namespace AutoMapPins
{
    internal static class Defaults
    {
        private static readonly Dictionary<string, bool> Switches = new Dictionary<string, bool>
        {
            ["AshLands|spawner:enemy_charred_archer"] = false,
            ["AshLands|plant:Charredskull"] = false,
            ["AshLands|spawner:enemy_charred_twitcher"] = false,
            ["AshLands|spawner:enemy_charred_mage"] = false,
            ["AshLands|plant:Fiddleheadfern"] = false,
            ["AshLands|deposit:FlametalOreNew"] = false,
            ["AshLands|plant:MoltenCore"] = false,
            ["AshLands|plant:Pot_Shard_Green"] = false,
            ["AshLands|deposit:ProustitePowder"] = false,
            ["AshLands|place:Runestone"] = false,
            ["AshLands|plant:MushroomSmokePuff"] = false,
            ["AshLands|spawner"] = false,
            ["AshLands|plant:SulfurStone"] = false,
            ["AshLands|chest"] = false,
            ["AshLands|plant:VoltureEgg"] = false,
            ["AshLands|spawner:enemy_volture"] = false,
            ["BlackForest|plant:Blueberries"] = false,
            ["BlackForest|spawner:enemy_boar"] = false,
            ["BlackForest|plant:CarrotSeeds"] = false,
            ["BlackForest|plant:Cloudberry"] = false,
            ["BlackForest|deposit:deposit_copper"] = false,
            ["BlackForest|spawner:enemy_greydwarfbrute"] = false,
            ["BlackForest|spawner:enemy_greydwarfshaman"] = false,
            ["BlackForest|plant:Mushroom"] = false,
            ["BlackForest|place:Runestone"] = false,
            ["BlackForest|spawner:enemy_skeleton"] = false,
            ["BlackForest|spawner"] = false,
            ["BlackForest|plant:SurtlingCore"] = false,
            ["BlackForest|plant:Thistle"] = false,
            ["BlackForest|deposit:TinOre"] = false,
            ["BlackForest|plant:TinOre"] = false,
            ["BlackForest|chest"] = false,
            ["BlackForest|spawner:enemy_troll"] = false,
            ["DeepNorth|place:DN_Bossroom"] = true,
            ["DeepNorth|place:NorthMemorialPlace"] = true, // the memorial altar: the orbs for the Deep North gear
            ["DeepNorth|spawner:enemy_trollfrost"] = false,
            ["DeepNorth|deposit:Ice"] = false,
            ["DeepNorth|plant:Lingonberry"] = false,
            ["DeepNorth|place:Runestone"] = false,
            ["DeepNorth|spawner:enemy_shadowperson"] = false,
            ["DeepNorth|deposit:Frostwood"] = false,
            ["DeepNorth|plant:Frostwood"] = false,
            ["Meadows|place:WoodFarm"] = false,
            ["Meadows|plant:Barley"] = false,
            ["Meadows|spawner:enemy_boar"] = false,
            ["Meadows|plant:Cloudberry"] = false,
            ["Meadows|deposit:deposit_copper"] = false,
            ["Meadows|plant:Dandelion"] = false,
            ["Meadows|spawner:enemy_draugr"] = false,
            ["Meadows|place:WoodVillage"] = false,
            ["Meadows|spawner:enemy_greydwarfbrute"] = false,
            ["Meadows|spawner:enemy_greydwarfshaman"] = false,
            ["Meadows|spawner:enemy_greydwarf"] = false,
            ["Meadows|spawner:enemy_jotun_warrior"] = false,
            ["Meadows|deposit:mudpile"] = false,
            ["Meadows|plant:Mushroom"] = false,
            ["Meadows|plant:Raspberry"] = false,
            ["Meadows|place:Runestone"] = false,
            ["Meadows|spawner:enemy_skeleton"] = false,
            ["Meadows|spawner"] = false,
            ["Meadows|deposit:TinOre"] = false,
            ["Meadows|chest"] = false,
            ["Meadows|plant:TurnipSeeds"] = false,
            ["Mistlands|deposit:giant_helmet"] = false,
            ["Mistlands|deposit:giant_sword"] = false,
            ["Mistlands|plant:Coins"] = false,
            ["Mistlands|spawner:enemy_dvergr_mage"] = false,
            ["Mistlands|spawner:enemy_dvergr"] = false,
            ["Mistlands|plant:MushroomJotunPuffs"] = false,
            ["Mistlands|plant:MushroomMagecap"] = false,
            ["Mistlands|deposit:giant_bone"] = false,
            ["Mistlands|plant:RoyalJelly"] = false,
            ["Mistlands|place:Runestone"] = false,
            ["Mistlands|spawner:enemy_seekerbrute"] = false,
            ["Mistlands|deposit:giant_brain"] = false,
            ["Mistlands|spawner:enemy_tick"] = false,
            ["Mistlands|chest"] = false,
            ["Mountain|plant:Crystal"] = false,
            ["Mountain|spawner:enemy_fenring"] = false,
            ["Mountain|plant:WolfHairBundle"] = false,
            ["Mountain|deposit:Obsidian"] = false,
            ["Mountain|plant:Obsidian"] = false,
            ["Mountain|plant:Raspberry"] = false,
            ["Mountain|place:Runestone"] = false,
            ["Mountain|deposit:deposit_silver"] = false,
            ["Mountain|deposit:deposit_silvervein"] = false,
            ["Mountain|chest"] = false,
            ["Ocean|deposit:Chitin"] = false,
            ["Ocean|deposit:Ice"] = false,
            ["Ocean|chest"] = false,
            ["Plains|plant:Barley"] = false,
            ["Plains|plant:CarrotSeeds"] = false,
            ["Plains|plant:Cloudberry"] = false,
            ["Plains|plant:Flax"] = false,
            ["Plains|spawner:enemy_goblinbrute"] = false,
            ["Plains|spawner:enemy_goblinshaman"] = false,
            ["Plains|spawner:enemy_goblin"] = false,
            ["Plains|deposit:mudpile"] = false,
            ["Plains|place:Runestone"] = false,
            ["Plains|chest"] = false,
            ["Swamp|spawner:enemy_draugrelite"] = false,
            ["Swamp|spawner:enemy_draugr"] = false,
            ["Swamp|deposit:Guck"] = false,
            ["Swamp|place:InfestedTree"] = false,
            ["Swamp|plant:IronOre"] = false,
            ["Swamp|deposit:mudpile"] = false,
            ["Swamp|plant:Mushroom"] = false,
            ["Swamp|place:Runestone"] = false,
            ["Swamp|spawner:enemy_skeleton"] = false,
            ["Swamp|plant:Thistle"] = false,
            ["Swamp|chest"] = false,
            ["Swamp|plant:TurnipSeeds"] = false,
        };

        // Switches whose default changed in this version: set once for players who still have the old default.
        internal static readonly Dictionary<string, bool> Changed = new Dictionary<string, bool>
        {
            // 1.1.0: the dungeons and the places worth going back to are on again (their icons say what they hold, and
            // zoomed out they no longer fill the map); greydwarf nests show their Ancient Seed.
            ["BlackForest|place:Crypt"] = true,
            ["Swamp|place:SunkenCrypt"] = true,
            ["BlackForest|place:TrollCave"] = true,
            ["Mountain|place:MountainCave"] = true,
            ["Mountain|place:Hildir_cave"] = true,
            ["Plains|place:GoblinCamp"] = true,
            ["Mountain|place:DrakeNest"] = true,
            ["Mistlands|place:Mistlands_Giant"] = true,
            ["BlackForest|spawner:enemy_greydwarf"] = true,
            ["Swamp|place:FireHole"] = false,
        };

        // 1.2.5: the same, once more for what changed since (a switch still at the old default gets the new one).
        internal static readonly Dictionary<string, bool> ChangedLater = new Dictionary<string, bool>
        {
            ["DeepNorth|place:NorthMemorialPlace"] = true, // was off as "North memorial place"
        };

        internal static bool On(Heightmap.Biome biome, Thing thing) =>
            Switches.TryGetValue(biome + "|" + thing.Id, out var on) ? on : thing.DefaultOn;
    }
}
