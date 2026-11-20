// Plant Anything, what the cultivator plants and the growing rules.
// Wild things (berry bushes, mushrooms, flowers, flora, debris, extra plantables) are planted from a copy of their
// prefab that has a Piece: the copy carries the game's name, so a planted bush is saved as the game's own bush and loads
// back as one, without a Piece. The wild ones in the world are left as they are (Plant Everything puts a Piece on the
// game's prefabs, which makes every wild bush, mushroom and flower in the world a building piece the game keeps track of).
// The four extra saplings are copies of the birch sapling under the names Plant Everything gave them (registered as
// prefabs, so they load), growing into their trees and tinted like them.
// Every change to the game's prefabs is undone exactly (Undo) when a setting changes or the sub-mod is switched off.
using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PlantAnything
{
    internal static class Plantables
    {
        private enum Group { Berry, Mushroom, Flower, Flora, Debris, Extra }

        private sealed class Def
        {
            public string Prefab, Name;
            public Group Group;
            public Heightmap.Biome Biome;
            public string[] Items;   // its cost (empty: what the pickable gives, "Seeds per bush or flower" of it)
            public int[] Amounts;
            public int CostScale = 1;
            public bool Grounded = true;
            public string[] Grown;   // saplings: the trees it grows into
            public Color Tint = Color.white;
            public float MinScale, MaxScale;
        }

        private static Def Pick(string prefab, string name, Group group, Heightmap.Biome biome, int scale = 1) =>
            new Def { Prefab = prefab, Name = name, Group = group, Biome = biome, Items = new string[0], Amounts = new int[0], CostScale = scale };

        private static Def Item(string prefab, string name, Group group, string item, int amount, bool grounded = true) =>
            new Def { Prefab = prefab, Name = name, Group = group, Items = new[] { item }, Amounts = new[] { amount }, Grounded = grounded };

        private const Heightmap.Biome Any = Heightmap.Biome.None;

        private static readonly Def[] Wild =
        {
            Pick("RaspberryBush", "Raspberry bush", Group.Berry, Heightmap.Biome.Meadows),
            Pick("BlueberryBush", "Blueberry bush", Group.Berry, Heightmap.Biome.BlackForest),
            Pick("CloudberryBush", "Cloudberry bush", Group.Berry, Heightmap.Biome.Plains),
            Pick("LingonberryBush", "Lingonberry bush", Group.Berry, Heightmap.Biome.DeepNorth),
            Pick("Pickable_Mushroom", "Mushrooms", Group.Mushroom, Any),
            Pick("Pickable_Mushroom_yellow", "Yellow mushrooms", Group.Mushroom, Any),
            Pick("Pickable_Mushroom_blue", "Blue mushrooms", Group.Mushroom, Any),
            Pick("Pickable_SmokePuff", "Smoke puffs", Group.Mushroom, Heightmap.Biome.AshLands),
            Pick("Pickable_Thistle", "Thistle", Group.Flower, Heightmap.Biome.BlackForest),
            Pick("Pickable_Dandelion", "Dandelion", Group.Flower, Heightmap.Biome.Meadows),
            Pick("Pickable_Fiddlehead", "Fiddlehead", Group.Flower, Heightmap.Biome.AshLands, 3),
            Item("Beech_small1", "Small beech", Group.Flora, "BeechSeeds", 1),
            Item("FirTree_small", "Small fir", Group.Flora, "FirCone", 1),
            Item("FirTree_small_dead", "Small dead fir", Group.Flora, "FirCone", 1),
            Item("Bush01", "Bush", Group.Flora, "Wood", 2),
            Item("Bush01_heath", "Heath bush", Group.Flora, "Wood", 2),
            Item("Bush02_en", "Plains bush", Group.Flora, "Wood", 3),
            Item("shrub_2", "Shrub", Group.Flora, "Wood", 2),
            Item("shrub_2_heath", "Heath shrub", Group.Flora, "Wood", 2),
            new Def { Prefab = "YggaShoot_small1", Name = "Small Ygga shoot", Group = Group.Flora, Items = new[] { "YggdrasilWood", "Wood" }, Amounts = new[] { 1, 2 } },
            Item("vines", "Vines", Group.Flora, "Wood", 2, false),
            Item("FernAshlands", "Ashlands fern", Group.Flora, "Wood", 2),
            Item("Pickable_Branch", "Branch", Group.Debris, "Wood", 5),
            Item("Pickable_Stone", "Stone", Group.Debris, "Stone", 1),
            Item("Pickable_Flint", "Flint", Group.Debris, "Flint", 1),
        };

        // Native biomes as Plant Everything had them (when "Any biome" is off), scales of their trees.
        private static readonly Def[] Saplings =
        {
            new Def { Prefab = "Ancient_Sapling", Name = "Ancient sapling", Items = new[] { "AncientSeed" }, Amounts = new[] { 1 }, Grown = new[] { "SwampTree1" },
                Biome = (Heightmap.Biome)27, Tint = new Color(0.62f, 0.66f, 0.42f), MinScale = 0.5f, MaxScale = 2f },
            new Def { Prefab = "Ygga_Sapling", Name = "Ygga sapling", Items = new[] { "Sap" }, Amounts = new[] { 1 }, Grown = new[] { "YggaShoot1", "YggaShoot2", "YggaShoot3" },
                Biome = (Heightmap.Biome)537, Tint = new Color(0.9f, 0.95f, 0.62f), MinScale = 0.5f, MaxScale = 1f },
            new Def { Prefab = "Autumn_Birch_Sapling", Name = "Autumn birch sapling", Items = new[] { "BirchSeeds" }, Amounts = new[] { 1 }, Grown = new[] { "Birch1_aut", "Birch2_aut" },
                Biome = (Heightmap.Biome)25, Tint = new Color(1f, 0.62f, 0.28f), MinScale = 0.5f, MaxScale = 2f },
            new Def { Prefab = "Ashwood_Sapling", Name = "Ashwood sapling", Items = new[] { "BeechSeeds", "SulfurStone" }, Amounts = new[] { 1, 1 },
                Grown = new[] { "AshlandsTree3", "AshlandsTree4", "AshlandsTree5", "AshlandsTree6_big" },
                Biome = (Heightmap.Biome)57, Tint = new Color(0.95f, 0.42f, 0.3f), MinScale = 0.7f, MaxScale = 1.3f },
        };

        // Planted pickables that regrow with "Regrow time" (wild ones too); the vine's berries count as berries.
        private static readonly string[] RegrowToo = { "VineAsh" };

        private static readonly HashSet<string> Flowers = new HashSet<string>();
        private static readonly HashSet<string> DebrisNames = new HashSet<string> { "Pickable_Branch", "Pickable_Stone", "Pickable_Flint" };
        // Every prefab name planted from the cultivator that its remove key may take away again, with what it cost.
        private static readonly Dictionary<string, Piece.Requirement[]> Costs = new Dictionary<string, Piece.Requirement[]>();
        private static readonly List<Action> Undos = new List<Action>();
        private static readonly List<GameObject> Copies = new List<GameObject>();
        private static GameObject _holder;
        private static EffectList _placeEffect;

        internal static bool IsFlower(string name) => Flowers.Contains(name);
        internal static bool IsDebris(string name) => DebrisNames.Contains(name);
        internal static bool Removable(string name, out Piece.Requirement[] cost) => Costs.TryGetValue(name, out cost);
        internal static EffectList PlaceEffect => _placeEffect;

        static Plantables()
        {
            foreach (var def in Wild)
                if (def.Group == Group.Mushroom || def.Group == Group.Flower) Flowers.Add(def.Prefab);
        }

        // A parent that is never active: copies under it never wake up (no ZDO, no Awake), but what is placed from them does.
        private static Transform Holder()
        {
            if (_holder == null)
            {
                _holder = new GameObject("DigiPlantAnything");
                _holder.SetActive(false);
                Object.DontDestroyOnLoad(_holder);
            }
            return _holder.transform;
        }

        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        [HarmonyPostfix]
        private static void SceneReady() => Apply(false);

        [HarmonyPatch(typeof(ObjectDB), "Awake")]
        [HarmonyPostfix]
        private static void ItemsReady() => Apply(false);

        private static PieceTable Cultivator(ObjectDB items)
        {
            var prefab = items.GetItemPrefab("Cultivator");
            return prefab != null ? prefab.GetComponent<ItemDrop>()?.m_itemData.m_shared.m_buildPieces : null;
        }

        // Everything at once, from the game's own values: what was changed before is undone first.
        internal static void Apply(bool live)
        {
            if (!Plugin.Running) return;
            var scene = ZNetScene.instance;
            var items = ObjectDB.instance;
            if (scene == null || items == null || items.m_items.Count == 0) return;
            var tool = Cultivator(items);
            if (tool == null) return;
            Undo();
            try
            {
                Build(scene, items, tool);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("Plant Anything could not set up its plants: " + e);
            }
            if (live)
            {
                UpdateLoaded();
                Player.m_localPlayer?.UpdateAvailablePiecesList();
            }
        }

        private static void Build(ZNetScene scene, ObjectDB items, PieceTable tool)
        {
            Costs.Clear();
            var model = FirstPlant(tool);
            _placeEffect = model?.m_placeEffect;
            Words();
            // the game's own regrow times first, so the copies made after them have them too
            if (Plugin.RegrowMinutes.Value > 0)
            {
                foreach (var def in Wild)
                    if (def.Group == Group.Berry || def.Group == Group.Mushroom || def.Group == Group.Flower) Regrow(scene.GetPrefab(def.Prefab), Plugin.RegrowMinutes.Value);
                foreach (var name in RegrowToo) Regrow(scene.GetPrefab(name), Plugin.RegrowMinutes.Value);
            }
            if (Plugin.DebrisRegrow.Value > 0)
                foreach (var name in DebrisNames) Regrow(scene.GetPrefab(name), Plugin.DebrisRegrow.Value);

            foreach (var def in Wild)
            {
                if (def.Group == Group.Flora && !Plugin.Flora.Value || def.Group == Group.Debris && !Plugin.Debris.Value) continue;
                var original = scene.GetPrefab(def.Prefab);
                if (original == null) continue;
                var cost = CostOf(def, original, items);
                if (cost == null) continue;
                Add(tool, MakeCopy(original, def, cost, model));
            }
            foreach (var def in Saplings)
            {
                var sapling = MakeSapling(scene, items, def, model);
                if (sapling != null) Add(tool, sapling);
            }
            Extras(scene, items, tool, model);
            Rules(tool);
            var canRemove = tool.m_canRemovePieces;
            tool.m_canRemovePieces = true; // the remove key takes away what you planted
            Undos.Add(() => tool.m_canRemovePieces = canRemove);
        }

        // Undo every change to the game's prefabs, in reverse order, and drop the copies.
        internal static void Undo()
        {
            for (var i = Undos.Count - 1; i >= 0; i--)
            {
                try { Undos[i](); }
                catch (Exception e) { Plugin.Log?.LogWarning("Plant Anything could not undo a change: " + e.Message); }
            }
            Undos.Clear();
            foreach (var copy in Copies)
                if (copy != null) Object.Destroy(copy);
            Copies.Clear();
        }

        private static void Add(PieceTable tool, GameObject prefab)
        {
            if (prefab == null || tool.m_pieces.Contains(prefab)) return;
            tool.m_pieces.Add(prefab);
            Undos.Add(() => tool.m_pieces.Remove(prefab));
        }

        private static Piece FirstPlant(PieceTable tool)
        {
            foreach (var go in tool.m_pieces)
                if (go != null && go.GetComponent<Plant>() != null && go.GetComponent<Piece>() != null) return go.GetComponent<Piece>();
            return null;
        }

        private static void Regrow(GameObject prefab, int minutes)
        {
            var pickable = prefab != null ? prefab.GetComponent<Pickable>() : null;
            if (pickable == null) return;
            var old = pickable.m_respawnTimeMinutes;
            pickable.m_respawnTimeMinutes = minutes;
            Undos.Add(() => pickable.m_respawnTimeMinutes = old);
        }

        // ---- names ----

        private static string Token(string prefab) => "digi_pa_" + prefab.ToLowerInvariant();

        private static void Words()
        {
            var words = Localization.instance;
            if (words == null) return;
            foreach (var def in Wild)
            {
                words.AddWord(Token(def.Prefab), def.Name);
                words.AddWord(Token(def.Prefab) + "_desc", def.Group == Group.Flora ? "For looks." :
                    def.Group == Group.Debris ? "Picked up, it comes back after a while." : "Grows back after picking.");
            }
            foreach (var def in Saplings)
            {
                words.AddWord(Token(def.Prefab), def.Name);
                words.AddWord(Token(def.Prefab) + "_desc", "Grows into a tree.");
            }
        }

        // ---- costs ----

        private static Piece.Requirement[] CostOf(Def def, GameObject original, ObjectDB items)
        {
            var list = new List<Piece.Requirement>();
            if (def.Items.Length == 0)
            {
                var gives = original.GetComponent<Pickable>()?.m_itemPrefab;
                var drop = gives != null ? gives.GetComponent<ItemDrop>() : null;
                if (drop == null) return null;
                list.Add(new Piece.Requirement { m_resItem = drop, m_amount = Plugin.Cost.Value * def.CostScale, m_recover = Plugin.SeedsBack.Value });
            }
            else
                for (var i = 0; i < def.Items.Length; i++)
                {
                    var drop = items.GetItemPrefab(def.Items[i])?.GetComponent<ItemDrop>();
                    if (drop == null) return null;
                    list.Add(new Piece.Requirement { m_resItem = drop, m_amount = def.Amounts[i], m_recover = Plugin.SeedsBack.Value });
                }
            return list.ToArray();
        }

        // ---- copies to plant from ----

        private static GameObject MakeCopy(GameObject original, Def def, Piece.Requirement[] cost, Piece model)
        {
            if (original.GetComponent<ZNetView>() == null) return null; // nothing that would not be saved
            var copy = Object.Instantiate(original, Holder());
            copy.name = original.name; // saved under the game's name: loads back as the game's own
            Copies.Add(copy);
            var piece = copy.GetComponent<Piece>() ?? copy.AddComponent<Piece>();
            piece.m_name = "$" + Token(def.Prefab);
            piece.m_description = "$" + Token(def.Prefab) + "_desc";
            piece.m_resources = cost;
            piece.m_icon = cost[0].m_resItem.m_itemData.GetIcon();
            piece.m_groundOnly = def.Grounded && !Plugin.Indoors.Value;
            // Placed with its foot on the aimed point, as the game places crops and as it stands in the wild. A piece that is
            // not marked so is placed by its colliders: the game takes the collider point nearest the aim, skipping triggers,
            // disabled colliders and hollow (non-convex) mesh colliders. A mushroom has none it counts, so the "nearest point"
            // stayed (0, 0, 0) and the ghost went to twice the aimed point, 50 m up (outside the world from 5 km out: no
            // grid shown, every spot "blocked"). Vines stay as they are: they are placed against a wall.
            piece.m_clipEverything = def.Grounded;
            piece.m_cultivatedGroundOnly = false;
            piece.m_onlyInBiome = Plugin.AnyBiome.Value ? Any : def.Biome;
            piece.m_canBeRemoved = true;
            piece.m_primaryTarget = piece.m_randomTarget = false;
            if (model != null)
            {
                piece.m_category = model.m_category;
                piece.m_placeEffect = model.m_placeEffect;
            }
            Costs[def.Prefab] = cost;
            return copy;
        }

        // The four extra saplings: made once from the birch sapling, registered with every new scene, set up each time.
        private static GameObject MakeSapling(ZNetScene scene, ObjectDB items, Def def, Piece model)
        {
            var hash = def.Prefab.GetStableHashCode();
            var sapling = Holder().Find(def.Prefab)?.gameObject;
            if (sapling == null)
            {
                if (scene.m_namedPrefabs.ContainsKey(hash)) return null; // another mod has it
                var birch = scene.GetPrefab("Birch_Sapling");
                if (birch == null) return null;
                sapling = Object.Instantiate(birch, Holder());
                sapling.name = def.Prefab;
                Tint(sapling, def.Tint);
            }
            if (!scene.m_namedPrefabs.ContainsKey(hash))
            {
                scene.m_prefabs.Add(sapling);
                scene.m_namedPrefabs[hash] = sapling;
            }
            var plant = sapling.GetComponent<Plant>();
            var piece = sapling.GetComponent<Piece>();
            if (plant == null || piece == null) return null;
            var grown = new List<GameObject>();
            foreach (var name in def.Grown)
            {
                var tree = scene.GetPrefab(name);
                if (tree != null) grown.Add(tree);
            }
            if (grown.Count == 0) return null;
            var cost = CostOf(def, sapling, items);
            if (cost == null) return null;
            foreach (var req in cost) req.m_recover = false; // a sapling gives its seed back only as a tree
            plant.m_grownPrefabs = grown.ToArray();
            plant.m_name = piece.m_name = "$" + Token(def.Prefab);
            piece.m_description = "$" + Token(def.Prefab) + "_desc";
            piece.m_resources = cost;
            piece.m_icon = cost[0].m_resItem.m_itemData.GetIcon();
            plant.m_minScale = def.MinScale;
            plant.m_maxScale = def.MaxScale;
            plant.m_biome = def.Biome;
            piece.m_onlyInBiome = def.Biome;
            if (model != null) piece.m_category = model.m_category;
            Costs[def.Prefab] = cost;
            return sapling;
        }

        private static void Tint(GameObject go, Color tint)
        {
            foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.sharedMaterials;
                for (var i = 0; i < materials.Length; i++)
                {
                    if (materials[i] == null || !materials[i].HasProperty("_Color")) continue;
                    var copy = new Material(materials[i]);
                    copy.color = copy.color * tint;
                    materials[i] = copy;
                }
                renderer.sharedMaterials = materials;
            }
        }

        // ---- extra plantables ----

        private static void Extras(ZNetScene scene, ObjectDB items, PieceTable tool, Piece model)
        {
            var text = Plugin.Extra.Value ?? "";
            foreach (var entry in text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = entry.Trim().Split(':');
                if (parts.Length < 2) continue;
                var name = parts[0].Trim();
                var prefab = scene.GetPrefab(name);
                var item = items.GetItemPrefab(parts[1].Trim())?.GetComponent<ItemDrop>();
                var amount = parts.Length > 2 && int.TryParse(parts[2].Trim(), out var n) ? Mathf.Max(1, n) : 1;
                if (prefab == null || item == null)
                {
                    Plugin.Log?.LogWarning($"Extra plantables: \"{entry.Trim()}\" left out ({(prefab == null ? "no prefab " + name : "no item " + parts[1].Trim())}).");
                    continue;
                }
                var cost = new[] { new Piece.Requirement { m_resItem = item, m_amount = amount, m_recover = Plugin.SeedsBack.Value } };
                if (prefab.GetComponent<Piece>() != null)
                {
                    Add(tool, prefab); // a piece already (of the game or another mod): as it is
                    continue;
                }
                Localization.instance?.AddWord(Token(name), name);
                Localization.instance?.AddWord(Token(name) + "_desc", "");
                Add(tool, MakeCopy(prefab, new Def { Prefab = name, Name = name, Group = Group.Extra, Biome = Any, Items = new string[0], Amounts = new int[0] }, cost, model));
            }
        }

        // ---- growing rules ----

        private static readonly Heightmap.Biome AllBiomes = Heightmap.Biome.Meadows | Heightmap.Biome.Swamp | Heightmap.Biome.Mountain |
                                                           Heightmap.Biome.BlackForest | Heightmap.Biome.Plains | Heightmap.Biome.AshLands |
                                                           Heightmap.Biome.DeepNorth | Heightmap.Biome.Ocean | Heightmap.Biome.Mistlands;

        internal static bool IsTree(Plant plant)
        {
            if (plant == null || plant.m_grownPrefabs == null) return false;
            foreach (var grown in plant.m_grownPrefabs)
                if (grown != null && grown.GetComponent<TreeBase>() != null) return true;
            return false;
        }

        // On every plant of the cultivator (the game's crops and saplings, other mods' too, the four extra saplings). Only
        // what a setting changes from the game's own is set, so other mods' plants keep their own values otherwise.
        private static void Rules(PieceTable tool)
        {
            foreach (var go in tool.m_pieces)
            {
                var plant = go != null ? go.GetComponent<Plant>() : null;
                var piece = go != null ? go.GetComponent<Piece>() : null;
                if (plant == null || piece == null) continue;
                var tree = IsTree(plant);
                var vine = plant.m_attachDistance > 0f;
                Remember(plant, piece);
                if (Plugin.AnyBiome.Value)
                {
                    plant.m_biome = AllBiomes;
                    piece.m_onlyInBiome = Any;
                }
                if (!Plugin.NeedShelter.Value) plant.m_tolerateCold = plant.m_tolerateHeat = true;
                if (Plugin.Indoors.Value && tree)
                {
                    piece.m_groundOnly = false;
                    plant.m_destroyIfCantGrow = false;
                }
                if (tree)
                {
                    if (Plugin.TreeMinutes.Value > 0) SetTime(plant, Plugin.TreeMinutes.Value);
                    if (Plugin.TreeSpacing.Value > 0f) plant.m_growRadius = Plugin.TreeSpacing.Value;
                    Costs[go.name] = piece.m_resources;
                    continue;
                }
                if (!Plugin.NeedCultivated.Value) plant.m_needCultivatedGround = piece.m_cultivatedGroundOnly = false;
                if (!Plugin.EnemiesAttack.Value) piece.m_primaryTarget = piece.m_randomTarget = false;
                if (!vine)
                {
                    if (Plugin.CropMinutes.Value > 0) SetTime(plant, Plugin.CropMinutes.Value);
                    if (Plugin.CropSpacing.Value > 0f) plant.m_growRadius = Plugin.CropSpacing.Value;
                }
                Costs[go.name] = piece.m_resources;
            }
        }

        private static void SetTime(Plant plant, int minutes)
        {
            plant.m_growTime = minutes * 60f;
            plant.m_growTimeMax = minutes * 66f;
        }

        // What the rules may change on a plant and its piece, put back by Undo.
        private static void Remember(Plant plant, Piece piece)
        {
            var biome = plant.m_biome;
            var only = piece.m_onlyInBiome;
            var cold = plant.m_tolerateCold;
            var heat = plant.m_tolerateHeat;
            var ground = piece.m_groundOnly;
            var destroy = plant.m_destroyIfCantGrow;
            var time = plant.m_growTime;
            var timeMax = plant.m_growTimeMax;
            var radius = plant.m_growRadius;
            var cultivated = plant.m_needCultivatedGround;
            var cultivatedOnly = piece.m_cultivatedGroundOnly;
            var primary = piece.m_primaryTarget;
            var random = piece.m_randomTarget;
            Undos.Add(() =>
            {
                plant.m_biome = biome;
                piece.m_onlyInBiome = only;
                plant.m_tolerateCold = cold;
                plant.m_tolerateHeat = heat;
                piece.m_groundOnly = ground;
                plant.m_destroyIfCantGrow = destroy;
                plant.m_growTime = time;
                plant.m_growTimeMax = timeMax;
                plant.m_growRadius = radius;
                plant.m_needCultivatedGround = cultivated;
                piece.m_cultivatedGroundOnly = cultivatedOnly;
                piece.m_primaryTarget = primary;
                piece.m_randomTarget = random;
            });
        }

        // ---- what is loaded already (a setting changed while playing) ----

        // Loaded plants and pickables take their prefab's new values (a pickable that now regrows starts its regrow check).
        private static void UpdateLoaded()
        {
            var scene = ZNetScene.instance;
            if (scene == null) return;
            foreach (var pickable in Object.FindObjectsByType<Pickable>(FindObjectsSortMode.None))
            {
                var prefab = scene.GetPrefab(Utils.GetPrefabName(pickable.gameObject))?.GetComponent<Pickable>();
                if (prefab == null || Mathf.Approximately(prefab.m_respawnTimeMinutes, pickable.m_respawnTimeMinutes)) continue;
                var started = pickable.m_respawnTimeMinutes > 0f;
                pickable.m_respawnTimeMinutes = prefab.m_respawnTimeMinutes;
                if (!started && pickable.m_respawnTimeMinutes > 0f) pickable.InvokeRepeating("UpdateRespawn", UnityEngine.Random.Range(1f, 5f), 60f);
            }
            foreach (var plant in Object.FindObjectsByType<Plant>(FindObjectsSortMode.None))
            {
                var prefab = scene.GetPrefab(Utils.GetPrefabName(plant.gameObject))?.GetComponent<Plant>();
                if (prefab == null) continue;
                plant.m_biome = prefab.m_biome;
                plant.m_tolerateCold = prefab.m_tolerateCold;
                plant.m_tolerateHeat = prefab.m_tolerateHeat;
                plant.m_destroyIfCantGrow = prefab.m_destroyIfCantGrow;
                plant.m_growTime = prefab.m_growTime;
                plant.m_growTimeMax = prefab.m_growTimeMax;
                plant.m_growRadius = prefab.m_growRadius;
                plant.m_needCultivatedGround = prefab.m_needCultivatedGround;
            }
        }
    }
}
