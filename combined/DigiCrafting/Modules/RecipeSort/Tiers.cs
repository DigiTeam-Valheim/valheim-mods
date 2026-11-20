// Recipe Sort and Search: the biome of every item, worked out once per world from the game's own data, so items from
// other mods get one too. Raw materials take the earliest biome they come from: creatures of the spawn lists and what
// they drop, ores, trees, rocks and plants of the world's vegetation, boss drops, and what traders sell after a boss.
// Made items take the latest biome of their ingredients (recipes, smelters, cooking stations, fermenters, planted
// seeds, sap extractors and beehives); where an item can be had several ways, the earliest counts.
// Order: Meadows, Black Forest, Swamp, Ocean, Mountains, Plains, Mistlands, Ashlands, Deep North. Unknown sorts last.
using System.Collections.Generic;
using UnityEngine;

namespace RecipeSort
{
    internal static class Tiers
    {
        internal const int Unknown = 99;

        private static readonly Heightmap.Biome[] Order =
        {
            Heightmap.Biome.Meadows, Heightmap.Biome.BlackForest, Heightmap.Biome.Swamp, Heightmap.Biome.Ocean,
            Heightmap.Biome.Mountain, Heightmap.Biome.Plains, Heightmap.Biome.Mistlands, Heightmap.Biome.AshLands,
            Heightmap.Biome.DeepNorth,
        };
        private static readonly string[] Tokens =
        {
            "$biome_meadows", "$biome_blackforest", "$biome_swamp", "$biome_ocean", "$biome_mountain", "$biome_plains",
            "$biome_mistlands", "$biome_ashlands", "$biome_deepnorth",
        };
        private static readonly string[] English =
        {
            "Meadows", "Black Forest", "Swamp", "Ocean", "Mountains", "Plains", "Mistlands", "Ashlands", "Deep North",
        };

        // The boss of each biome, by the key its defeat sets.
        private static readonly Dictionary<string, int> BossKeys = new Dictionary<string, int>
        {
            { "defeated_eikthyr", 0 }, { "defeated_gdking", 1 }, { "defeated_bonemass", 2 }, { "defeated_dragon", 4 },
            { "defeated_goblinking", 5 }, { "defeated_queen", 6 }, { "defeated_fader", 7 },
            { "defeated_frozenking", 8 }, { "defeated_frozenking_p3", 8 }, // Kall Fimbulbringer (Deep North), first and last phase
        };

        private static readonly Dictionary<string, int> Tier = new Dictionary<string, int>(); // item prefab name -> tier
        private static readonly HashSet<string> MeadBases = new HashSet<string>();
        private static readonly Dictionary<string, string> Sources = new Dictionary<string, string>(); // where each tier came from
        private static string _from = "";
        private static ObjectDB _builtFor;
        private static int _builtRecipes = -1;

        internal static int Of(ItemDrop item)
        {
            if (item == null) return Unknown;
            Ensure();
            return Tier.TryGetValue(item.gameObject.name, out var tier) ? tier : Unknown;
        }

        // What a fermenter turns into a mead: listed with the meads.
        internal static bool IsMeadBase(ItemDrop item)
        {
            if (item == null) return false;
            Ensure();
            return MeadBases.Contains(item.gameObject.name);
        }

        internal static string Source(ItemDrop item)
        {
            if (item == null) return "";
            Ensure();
            return Sources.TryGetValue(item.gameObject.name, out var from) ? from : "";
        }

        internal static string Name(int tier) =>
            tier >= 0 && tier < Order.Length ? Localization.instance.Localize(Tokens[tier]) : "";

        internal static string EnglishName(int tier) => tier >= 0 && tier < Order.Length ? English[tier] : "";

        internal static void Forget()
        {
            Tier.Clear();
            MeadBases.Clear();
            Sources.Clear();
            _builtFor = null;
            _builtRecipes = -1;
        }

        // Rebuilt for a new world, and when a mod adds recipes later.
        private static void Ensure()
        {
            var db = ObjectDB.instance;
            if (db == null || (db == _builtFor && db.m_recipes.Count == _builtRecipes)) return;
            if (ZNetScene.instance == null || ZoneSystem.instance == null) return;
            _builtFor = db;
            _builtRecipes = db.m_recipes.Count;
            Build(db);
        }

        private static int TierOf(Heightmap.Biome biome)
        {
            for (var i = 0; i < Order.Length; i++)
                if ((biome & Order[i]) != 0) return i;
            return Unknown;
        }

        // A spawn entry's biome. Entries for every biome (the Deep North's list) take it from the list's name. Entries that
        // need a boss down (seekers in the Meadows after the Queen) count as the biome after that boss.
        private static int SpawnTier(SpawnSystem.SpawnData data, string list)
        {
            var tier = (data.m_biome & Heightmap.Biome.All) == Heightmap.Biome.All ? TierOfName(list) : TierOf(data.m_biome);
            if (tier < Unknown && !string.IsNullOrEmpty(data.m_requiredGlobalKey) && BossKeys.TryGetValue(data.m_requiredGlobalKey, out var boss))
                tier = Mathf.Max(tier, Mathf.Min(boss + 1, Order.Length - 1));
            return tier;
        }

        private static readonly string[] NameHints = { "meadow", "forest", "swamp", "ocean", "mountain", "plains", "mistland", "ashland", "deepnorth" };

        private static int TierOfName(string name)
        {
            var lower = (name ?? "").ToLowerInvariant().Replace(" ", "").Replace("_", "");
            for (var i = NameHints.Length - 1; i >= 0; i--)
                if (lower.Contains(NameHints[i])) return i;
            return Unknown;
        }

        // Loot of villages, dungeons and chests (the world's locations are not loaded to look inside them).
        private static readonly Dictionary<string, int> LocationLoot = new Dictionary<string, int>
        {
            { "Amber", 1 }, { "AmberPearl", 1 }, { "Ruby", 1 }, { "SurtlingCore", 1 }, { "MushroomYellow", 1 },
            { "WolfClaw", 4 }, { "WolfHairBundle", 4 }, { "DragonEgg", 4 }, { "Fish4_cave", 4 }, { "OnionSeeds", 4 },
            { "Barley", 5 }, { "Flax", 5 }, { "ChickenEgg", 5 }, { "Wisp", 6 }, { "BlackCore", 6 }, { "DvergrKeyFragment", 6 },
            { "GemstoneBlue", 7 }, { "GemstoneGreen", 7 }, { "GemstoneRed", 7 }, { "FlametalOreNew", 7 }, { "MoltenCore", 7 },
            { "VineberrySeeds", 7 }, { "Fiddleheadfern", 7 }, { "TrophySurtling", 2 }, { "TrophyCultist", 4 },
            { "TrophyGrowth", 5 }, { "ChickenMeat", 5 }, { "Gold", 8 }, { "FrostCore", 8 }, { "OrbFrostFire", 8 },
            { "OrbThunderBlood", 8 }, { "SpiceDeepNorth", 8 },
            // Treasure chests, pickables inside locations, traders without a boss key and creatures no spawn list has
            // (checked against the game of 2026-10-28): what these make follows from them.
            { "AxeHead1", 0 }, { "AxeHead2", 0 }, { "FishingBait", 1 }, { "CuredSquirrelHamstring", 2 },
            { "PowderedDragonEgg", 2 }, { "PungentPebbles", 2 }, { "FreshSeaweed", 2 }, { "SpiceOceans", 3 },
            { "FireworksRocket_White", 5 }, { "Hook", 6 }, { "BellFragment", 7 }, { "DyrnwynBladeFragment", 7 },
            { "DyrnwynHiltFragment", 7 }, { "DyrnwynTipFragment", 7 }, { "FaderEmber", 7 }, { "MoleClaws", 8 },
            { "OozeMork", 8 }, { "TrophyBlob_Morkhalla", 8 }, { "Poteitr", 8 }, { "OatSeeds", 8 }, { "Oat", 8 },
        };

        private static bool Lower(GameObject item, int tier)
        {
            if (item == null || tier >= Unknown || item.GetComponent<ItemDrop>() == null) return false;
            var name = item.name;
            if (Tier.TryGetValue(name, out var current) && current <= tier) return false;
            Tier[name] = tier;
            Sources[name] = _from;
            return true;
        }

        private static int Known(ItemDrop item) =>
            item != null && Tier.TryGetValue(item.gameObject.name, out var tier) ? tier : Unknown;

        private static void Drops(DropTable table, int tier)
        {
            if (table?.m_drops == null) return;
            foreach (var drop in table.m_drops) Lower(drop.m_item, tier);
        }

        // Everything a world object gives when picked, mined or chopped, also the pieces it breaks into.
        private static void WorldObject(GameObject prefab, int tier, HashSet<GameObject> seen)
        {
            if (prefab == null || !seen.Add(prefab)) return;
            foreach (var pickable in prefab.GetComponentsInChildren<Pickable>(true))
            {
                Lower(pickable.m_itemPrefab, tier);
                Drops(pickable.m_extraDrops, tier);
            }
            foreach (var pickable in prefab.GetComponentsInChildren<PickableItem>(true))
            {
                if (pickable.m_itemPrefab != null) Lower(pickable.m_itemPrefab.gameObject, tier);
                if (pickable.m_randomItemPrefabs != null)
                    foreach (var random in pickable.m_randomItemPrefabs)
                        if (random.m_itemPrefab != null) Lower(random.m_itemPrefab.gameObject, tier);
            }
            foreach (var rock in prefab.GetComponentsInChildren<MineRock>(true)) Drops(rock.m_dropItems, tier);
            foreach (var rock in prefab.GetComponentsInChildren<MineRock5>(true)) Drops(rock.m_dropItems, tier);
            foreach (var drop in prefab.GetComponentsInChildren<DropOnDestroyed>(true)) Drops(drop.m_dropWhenDestroyed, tier);
            foreach (var tree in prefab.GetComponentsInChildren<TreeBase>(true))
            {
                Drops(tree.m_dropWhenDestroyed, tier);
                WorldObject(tree.m_logPrefab, tier, seen);
            }
            foreach (var log in prefab.GetComponentsInChildren<TreeLog>(true))
            {
                Drops(log.m_dropWhenDestroyed, tier);
                WorldObject(log.m_subLogPrefab, tier, seen);
            }
            foreach (var destructible in prefab.GetComponentsInChildren<Destructible>(true))
                WorldObject(destructible.m_spawnWhenDestroyed, tier, seen);
        }

        // The latest biome of what a recipe or piece needs; Unknown while one of them is unknown, unless loose.
        private static int Needs(Piece.Requirement[] resources, bool loose)
        {
            if (resources == null || resources.Length == 0) return Unknown;
            var any = false;
            // Upgrade idols are in many recipes for the upgrade only: they say nothing about the item's biome.
            foreach (var r in resources) if (r?.m_resItem != null && !r.m_upgraderResource && r.m_amount > 0) { any = true; break; }
            var worst = -1;
            foreach (var r in resources)
            {
                if (r?.m_resItem == null || r.m_upgraderResource || (any && r.m_amount <= 0)) continue;
                var tier = Known(r.m_resItem);
                if (tier >= Unknown)
                {
                    if (!loose) return Unknown;
                    continue;
                }
                if (tier > worst) worst = tier;
            }
            return worst < 0 ? Unknown : worst;
        }

        private static void Build(ObjectDB db)
        {
            Tier.Clear();
            MeadBases.Clear();
            Sources.Clear();
            var prefabs = ZNetScene.instance.m_prefabs;

            // Creatures of the spawn lists, and what they drop.
            var spawn = ZoneSystem.instance.m_zoneCtrlPrefab != null ? ZoneSystem.instance.m_zoneCtrlPrefab.GetComponent<SpawnSystem>() : null;
            if (spawn?.m_spawnLists != null)
                foreach (var list in spawn.m_spawnLists)
                {
                    if (list?.m_spawners == null) continue;
                    foreach (var data in list.m_spawners)
                    {
                        if (data == null || !data.m_enabled || data.m_devDisabled || data.m_prefab == null || data.m_spawnChance <= 0f) continue;
                        var tier = SpawnTier(data, list.name);
                        if (tier >= Unknown) continue;
                        _from = $"spawns: {data.m_prefab.name} in {data.m_biome} ({list.name}{(string.IsNullOrEmpty(data.m_requiredGlobalKey) ? "" : ", key " + data.m_requiredGlobalKey)})";
                        Lower(data.m_prefab, tier); // fish are items themselves
                        var drops = data.m_prefab.GetComponent<CharacterDrop>();
                        if (drops?.m_drops != null)
                            foreach (var drop in drops.m_drops) Lower(drop.m_prefab, tier);
                    }
                }

            // Ores, trees, rocks and plants of the world.
            var seen = new HashSet<GameObject>();
            if (ZoneSystem.instance.m_vegetation != null)
                foreach (var veg in ZoneSystem.instance.m_vegetation)
                    if (veg != null && veg.m_enable && veg.m_prefab != null)
                    {
                        seen.Clear();
                        _from = $"world: {veg.m_prefab.name} in {veg.m_biome}";
                        WorldObject(veg.m_prefab, TierOf(veg.m_biome), seen);
                    }

            // Bosses, and what traders sell once a boss is down.
            foreach (var prefab in prefabs)
            {
                if (prefab == null) continue;
                var character = prefab.GetComponent<Character>();
                if (character != null && character.m_boss && BossKeys.TryGetValue(character.m_defeatSetGlobalKey ?? "", out var bossTier))
                {
                    var drops = prefab.GetComponent<CharacterDrop>();
                    _from = "boss " + prefab.name;
                    if (drops?.m_drops != null)
                        foreach (var drop in drops.m_drops) Lower(drop.m_prefab, bossTier);
                }
                var trader = prefab.GetComponent<Trader>();
                _from = "trader " + prefab.name;
                if (trader?.m_items != null)
                    foreach (var trade in trader.m_items)
                        if (trade?.m_prefab != null && BossKeys.TryGetValue(trade.m_requiredGlobalKey ?? "", out var tradeTier))
                            Lower(trade.m_prefab.gameObject, tradeTier);
            }

            _from = "location loot";
            foreach (var loot in LocationLoot)
            {
                var prefab = db.GetItemPrefab(loot.Key);
                if (prefab != null && !Tier.ContainsKey(loot.Key)) Lower(prefab, loot.Value);
            }
            // Deep North gear is cast from moulds found there.
            foreach (var prefab in db.m_items)
                if (prefab != null && prefab.name.StartsWith("Mold", System.StringComparison.Ordinal) && !Tier.ContainsKey(prefab.name)) Lower(prefab, 8);

            // What fermenters take in.
            foreach (var prefab in prefabs)
            {
                var fermenter = prefab != null ? prefab.GetComponent<Fermenter>() : null;
                if (fermenter?.m_conversion == null) continue;
                foreach (var conversion in fermenter.m_conversion)
                    if (conversion?.m_from != null) MeadBases.Add(conversion.m_from.gameObject.name);
            }

            // Made items, until nothing changes; then once more allowing unknown ingredients.
            for (var loose = 0; loose < 2; loose++)
                for (var pass = 0; pass < 20; pass++)
                    if (!Spread(db, prefabs, loose == 1)) break;
        }

        private static bool Spread(ObjectDB db, List<GameObject> prefabs, bool loose)
        {
            var changed = false;
            foreach (var recipe in db.m_recipes)
                if (recipe != null && recipe.m_enabled && recipe.m_item != null)
                {
                    _from = "recipe " + recipe.name + (loose ? " (loose)" : "");
                    changed |= Lower(recipe.m_item.gameObject, Needs(recipe.m_resources, loose));
                }
            foreach (var prefab in prefabs)
            {
                if (prefab == null) continue;
                _from = "made in " + prefab.name;
                var smelter = prefab.GetComponent<Smelter>();
                if (smelter?.m_conversion != null)
                    foreach (var c in smelter.m_conversion)
                        if (c?.m_from != null && c.m_to != null) changed |= Lower(c.m_to.gameObject, Known(c.m_from));
                var cooking = prefab.GetComponent<CookingStation>();
                if (cooking?.m_conversion != null)
                    foreach (var c in cooking.m_conversion)
                        if (c?.m_from != null && c.m_to != null) changed |= Lower(c.m_to.gameObject, Known(c.m_from));
                var fermenter = prefab.GetComponent<Fermenter>();
                if (fermenter?.m_conversion != null)
                    foreach (var c in fermenter.m_conversion)
                        if (c?.m_from != null && c.m_to != null) changed |= Lower(c.m_to.gameObject, Known(c.m_from));
                var piece = prefab.GetComponent<Piece>();
                if (piece == null) continue;
                // Planted seeds grow into crops; sap extractors and beehives make their item.
                var plant = prefab.GetComponent<Plant>();
                if (plant?.m_grownPrefabs != null)
                {
                    var seedTier = Needs(piece.m_resources, true);
                    foreach (var grown in plant.m_grownPrefabs)
                    {
                        var pickable = grown != null ? grown.GetComponent<Pickable>() : null;
                        if (pickable == null) continue;
                        changed |= Lower(pickable.m_itemPrefab, seedTier);
                        if (pickable.m_extraDrops?.m_drops != null)
                            foreach (var drop in pickable.m_extraDrops.m_drops) changed |= Lower(drop.m_item, seedTier);
                    }
                }
                var sap = prefab.GetComponent<SapCollector>();
                if (sap?.m_spawnItem != null) changed |= Lower(sap.m_spawnItem.gameObject, Needs(piece.m_resources, loose));
                var hive = prefab.GetComponent<Beehive>();
                if (hive?.m_honeyItem != null) changed |= Lower(hive.m_honeyItem.gameObject, Needs(piece.m_resources, loose));
            }
            return changed;
        }
    }
}
