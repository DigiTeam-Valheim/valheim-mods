// Nordic Rugs builds its rug meshes only 0.2-1.6 cm above the placement point, so the uneven top of the stone floor
// pokes through them (wood floor is flat and lower, so it does not). This lifts the rug's visual mesh a little, like
// vanilla rugs (whose fabric sits 3-6 cm up). Visual only: placement, collider, comfort and saved position are unchanged,
// and already placed rugs are lifted too.
// It also adds a "(wall)" version of every Nordic rug: the same rug standing upright, to hang on walls. Placed like any
// wall decoration: aim at the wall and turn it with the rotate key until it lies flat against it. A "(wall, sideways)"
// version hangs the same rug turned 90 degrees (landscape), since the rotate key only turns pieces around the vertical.
// Wall rugs line up flat with the wall you aim at (a small gap off its surface), whatever way you face. In the hammer
// all versions of a rug are listed together, rugs in name order.
// Large rugs: a "(2x)" and a "(3x)" version of every Nordic rug, the same rug scaled up flat (a 2 x 3 m rug becomes
// 4 x 6 m and 6 x 9 m; the thickness stays), for 4x and 9x its materials. A scaled prefab: the game places, saves and
// loads it at that size like any piece.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace RugLift
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.ruglift";
        public const string Name = "Digi's Rug Lift";
        public const string Version = "1.4.1";
        private const string NordicRugsGuid = "sosal.nordicrugs";
        private const string MeshName = "NordicRugMesh";
        private const string WallSuffix = "_wall";
        private const string SidewaysSuffix = "_wall_side";
        private static readonly int[] LargeScales = { 2, 3 };

        private static ConfigEntry<float> _lift;
        private static ConfigEntry<bool> _wallRugs;
        private static ConfigEntry<bool> _sidewaysRugs;
        private static ConfigEntry<bool> _largeRugs;
        private static ConfigEntry<float> _wallGap;
        private static readonly Dictionary<string, string> RugNames = new Dictionary<string, string>(); // floor prefab -> name

        // The plugin ID changed from pieter.valheim.* to digi.valheim.*: carry the old settings file over once.
        private void MigrateConfig()
        {
            var old = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, Guid.Replace("digi.valheim.", "pieter.valheim.") + ".cfg");
            if (System.IO.File.Exists(Config.ConfigFilePath) || !System.IO.File.Exists(old)) return;
            System.IO.File.Copy(old, Config.ConfigFilePath);
            Config.Reload();
        }


        private void Awake()
        {
            MigrateConfig();
            _lift = Config.Bind("General", "Lift (m)", 0.03f,
                new ConfigDescription("How far Nordic Rugs float above the floor so stone floors do not poke through, in meters (0.03 = 3 cm, like normal rugs). 0 = off.",
                    new AcceptableValueRange<float>(0f, 0.1f)));
            _wallRugs = Config.Bind("Wall rugs", "Add wall versions", true,
                "Add a \"(wall)\" version of every Nordic rug to the hammer's Furniture tab, to hang on walls. Needs a restart.");
            _sidewaysRugs = Config.Bind("Wall rugs", "Add sideways wall versions", true,
                "Also add a \"(wall, sideways)\" version: the wall rug turned 90 degrees. Needs a restart.");
            _largeRugs = Config.Bind("Large rugs", "Add large versions", true,
                "Add a \"(2x)\" and a \"(3x)\" version of every Nordic rug: the same rug scaled up flat (a 2 x 3 m rug becomes " +
                "4 x 6 m and 6 x 9 m), for 4x and 9x its materials. Needs a restart.");
            _wallGap = Config.Bind("Wall rugs", "Gap from wall (m)", 0.03f,
                new ConfigDescription("How far wall rugs hang from the wall, in meters, so bumpy log walls do not poke through.",
                    new AcceptableValueRange<float>(0f, 0.2f)));
            _lift.SettingChanged += (_, __) =>
            {
                foreach (var piece in UnityEngine.Object.FindObjectsOfType<Piece>()) Apply(piece);
            };
            PatchModule(typeof(Plugin));
            // Subscribed after Nordic Rugs (hard dependency, so it loaded first): its rugs exist when this runs.
            if (_wallRugs.Value || _largeRugs.Value)
            {
                PrefabManager.OnVanillaPrefabsAvailable += AddWallRugs;
                PieceManager.OnPiecesRegistered += SortRugs;
            }
        }

        [HarmonyPatch(typeof(Piece), "Awake")]
        [HarmonyPostfix]
        private static void PieceAwake(Piece __instance) => Apply(__instance);

        private static void Apply(Piece piece)
        {
            if (piece == null || piece.name.Contains(WallSuffix)) return;
            var mesh = piece.transform.Find(MeshName);
            if (mesh == null) return;
            var p = mesh.localPosition;
            mesh.localPosition = new Vector3(p.x, _lift.Value, p.z);
        }

        // --- Wall versions -----------------------------------------------------------------------------------------

        private void AddWallRugs()
        {
            PrefabManager.OnVanillaPrefabsAvailable -= AddWallRugs;
            var catalog = AccessTools.Field(AccessTools.TypeByName("NordicRugs.RugCatalog"), "All")?.GetValue(null) as IEnumerable;
            if (catalog == null)
            {
                Logger.LogError("Wall rugs disabled, the Nordic Rugs catalog was not found.");
                return;
            }

            var added = 0;
            foreach (var rug in catalog)
            {
                try
                {
                    var t = Traverse.Create(rug);
                    var prefabName = t.Property("PrefabName").GetValue<string>();
                    var nameEn = t.Property("NameEn").GetValue<string>();
                    var fur = t.Property("Fur").GetValue<bool>();
                    var recipe = t.Property("Recipe").GetValue<IReadOnlyDictionary<string, int>>();
                    var source = PieceManager.Instance.GetPiece(prefabName);
                    if (source?.PiecePrefab == null) continue;
                    RugNames[prefabName] = nameEn;

                    if (_wallRugs.Value && AddWall(source.PiecePrefab, prefabName + WallSuffix, nameEn + " (wall)", fur, recipe, false)) added++;
                    if (_wallRugs.Value && _sidewaysRugs.Value &&
                        AddWall(source.PiecePrefab, prefabName + SidewaysSuffix, nameEn + " (wall, sideways)", fur, recipe, true)) added++;
                    if (_largeRugs.Value)
                        foreach (var scale in LargeScales)
                            if (AddLarge(source.PiecePrefab, prefabName + LargeSuffix(scale), $"{nameEn} ({scale}x)", fur, recipe, scale)) added++;
                }
                catch (Exception e)
                {
                    Logger.LogWarning("Could not add a wall rug: " + e.Message);
                }
            }
            Logger.LogInfo($"Added {added} wall and large rugs (Furniture).");
        }

        // Hammer list: each rug's floor, wall and sideways versions together, rugs sorted by name, where the rugs were.
        private static void SortRugs()
        {
            var table = PieceManager.Instance.GetPieceTable("Hammer");
            if (table == null || RugNames.Count == 0) return;
            var list = table.m_pieces;
            var first = list.FindIndex(IsRug);
            if (first < 0) return;
            var rugs = list.Where(IsRug).ToList();
            list.RemoveAll(IsRug);
            rugs = rugs.OrderBy(go => RugNames[BaseName(go.name)], StringComparer.OrdinalIgnoreCase)
                .ThenBy(go => go.name.EndsWith(LargeSuffix(2)) ? 1 : go.name.EndsWith(LargeSuffix(3)) ? 2
                    : go.name.EndsWith(SidewaysSuffix) ? 4 : go.name.EndsWith(WallSuffix) ? 3 : 0).ToList();
            list.InsertRange(Math.Min(first, list.Count), rugs);
        }

        private static bool IsRug(GameObject go) => go != null && RugNames.ContainsKey(BaseName(go.name));

        // One of our wall rugs (a Nordic rug's name, nr_rug_..., with _wall or _wall_side after it): only those are turned
        // to the wall. Until 1.4.1 any piece with "_wall" in its name was, so the sconce (piece_walltorch) could not be
        // turned. By name only, so it also holds for a copy loaded into a running game (it never sees the rugs added).
        private static bool IsWallRug(GameObject go)
        {
            var name = Utils.GetPrefabName(go);
            return name.StartsWith("nr_rug_", StringComparison.Ordinal) && (name.EndsWith(WallSuffix) || name.EndsWith(SidewaysSuffix));
        }

        private static string LargeSuffix(int scale) => "_x" + scale;

        private static string BaseName(string name)
        {
            if (name.EndsWith(SidewaysSuffix)) return name.Substring(0, name.Length - SidewaysSuffix.Length);
            if (name.EndsWith(WallSuffix)) return name.Substring(0, name.Length - WallSuffix.Length);
            foreach (var scale in LargeScales)
                if (name.EndsWith(LargeSuffix(scale))) return name.Substring(0, name.Length - LargeSuffix(scale).Length);
            return name;
        }

        // The same rug scaled up flat (length and width; the thickness stays), for scale x scale its materials.
        private static bool AddLarge(GameObject source, string prefabName, string name, bool fur,
            IReadOnlyDictionary<string, int> recipe, int scale)
        {
            var large = PrefabManager.Instance.CreateClonedPrefab(prefabName, source);
            var size = large.transform.localScale;
            large.transform.localScale = new Vector3(size.x * scale, size.y, size.z * scale);
            var config = new PieceConfig
            {
                Name = name,
                Description = fur ? "$nr_fur_description" : "$nr_description",
                PieceTable = "Hammer",
                Category = "Furniture",
                CraftingStation = "piece_workbench",
            };
            foreach (var req in recipe) config.AddRequirement(req.Key, req.Value * scale * scale, true);
            return PieceManager.Instance.AddPiece(new CustomPiece(large, false, config));
        }

        // --- Wall placement: flat against the aimed wall ------------------------------------------------------------

        private static readonly AccessTools.FieldRef<Player, GameObject> Ghost = AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost");
        private static readonly System.Reflection.MethodInfo PieceRayTest = AccessTools.Method(typeof(Player), "PieceRayTest");

        [HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
        [HarmonyPostfix]
        private static void AlignToWall(Player __instance)
        {
            var ghost = Ghost(__instance);
            if (ghost == null || !ghost.activeSelf || !IsWallRug(ghost)) return;
            var args = new object[] { null, null, null, null, null, false };
            if (!(bool)PieceRayTest.Invoke(__instance, args)) return;
            var point = (Vector3)args[0];
            var normal = (Vector3)args[1];
            if (Mathf.Abs(normal.y) > 0.5f) return; // floor or ceiling: leave vanilla placement
            normal = new Vector3(normal.x, 0f, normal.z).normalized;
            // The woven side faces the piece's back (-Z), so the back looks out of the wall.
            ghost.transform.rotation = Quaternion.LookRotation(-normal, Vector3.up);
            ghost.transform.position = point + normal * _wallGap.Value;
        }

        // The rug stands up: its length runs vertically, centered on the aimed point, with the woven side facing the
        // piece's back (-Z) and its back flush with the placement point.
        private static bool AddWall(GameObject source, string prefabName, string name, bool fur,
            IReadOnlyDictionary<string, int> recipe, bool sideways)
        {
            var wall = PrefabManager.Instance.CreateClonedPrefab(prefabName, source);
            MakeUpright(wall, sideways);
            var config = new PieceConfig
            {
                Name = name,
                Description = fur ? "$nr_fur_description" : "$nr_description",
                PieceTable = "Hammer",
                Category = "Furniture",
                CraftingStation = "piece_workbench",
            };
            foreach (var req in recipe) config.AddRequirement(req.Key, req.Value, true);
            return PieceManager.Instance.AddPiece(new CustomPiece(wall, false, config));
        }

        // Sideways: additionally turned 90 degrees around the axis pointing out of the wall.
        private static void MakeUpright(GameObject wall, bool sideways)
        {
            var mesh = wall.transform.Find(MeshName);
            if (mesh != null)
            {
                mesh.localRotation = (sideways ? Quaternion.Euler(0f, 0f, 90f) : Quaternion.identity) * Quaternion.Euler(-90f, 0f, 0f);
                mesh.localPosition = new Vector3(0f, 0f, -0.005f);
            }
            var box = wall.GetComponent<BoxCollider>();
            if (box != null)
            {
                var size = box.size;
                box.size = sideways ? new Vector3(size.z, size.x, size.y) : new Vector3(size.x, size.z, size.y);
                box.center = new Vector3(0f, 0f, -size.y / 2f);
            }
        }
    }
}
