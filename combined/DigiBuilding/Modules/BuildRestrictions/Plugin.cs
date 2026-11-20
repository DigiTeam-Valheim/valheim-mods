// Choose which building restrictions apply. Each rule can be switched off on its own; settings are controlled by the
// server admin/host and synced to everyone (Jotunn), so all players follow the same rules.
// The check runs once per frame while a piece is being placed, nothing else.
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Utils;
using UnityEngine;

namespace BuildRestrictions
{
    [SynchronizationMode(AdminOnlyStrictness.IfOnServer)]
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.buildrestrictions";
        public const string Name = "Digi's Build Restrictions";
        public const string Version = "1.0.1";

        private static ConfigEntry<bool> _all, _invalid, _blockedByPlayer, _noBuildZone, _space, _teleport,
            _missingExtensionStation, _missingStation, _biome, _cultivated, _dirt, _dungeon;
        private static CraftingStation _anyStation;

        private static readonly AccessTools.FieldRef<Player, GameObject> Ghost = AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost");
        private static readonly AccessTools.FieldRef<Player, Player.PlacementStatus> Status =
            AccessTools.FieldRefAccess<Player, Player.PlacementStatus>("m_placementStatus");
        private static readonly System.Action<Player, bool> SetGhostValid =
            AccessTools.MethodDelegate<System.Action<Player, bool>>(AccessTools.Method(typeof(Player), "SetPlacementGhostValid"));

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
            _all = Rule("Ignore all restrictions", false, "Turns every rule below off at once.");
            _invalid = Rule("Ignore invalid placement", true, "Place pieces where the game says \"Invalid placement\", e.g. a campfire on a wooden floor, ground-only pieces on floors, or pieces on steep slopes.");
            _blockedByPlayer = Rule("Ignore player in the way", false, "Build even when a player is standing where the piece goes.");
            _noBuildZone = Rule("Ignore no-build zones", false, "Build in protected places like the trader's camp and boss altars.");
            _space = Rule("Ignore space needed", false, "Build pieces that need free space around them (like beds and portals) in tight spots.");
            _teleport = Rule("Ignore teleport areas", false, "Place pieces that are normally only allowed in special teleport areas anywhere (rarely matters).");
            _missingExtensionStation = Rule("Ignore extension without station", false, "Build station upgrades (chopping block, tanning rack, forge bellows, ...) without their crafting station nearby.");
            _missingStation = Rule("Ignore missing workbench", false, "Build without a workbench (or the other station a piece needs) nearby.");
            _biome = Rule("Ignore biome restrictions", false, "Build pieces that are limited to certain biomes anywhere.");
            _cultivated = Rule("Ignore cultivated ground", false, "Plant crops and saplings without cultivated ground.");
            _dirt = Rule("Ignore dirt requirement", false, "Place pieces that need dirt or grass on any ground.");
            _dungeon = Rule("Ignore dungeon restrictions", true, "Build inside dungeons, crypts and other places where building is normally blocked.");
            PatchModule(typeof(Plugin));
        }

        private ConfigEntry<bool> Rule(string name, bool value, string description) =>
            Config.Bind("Restrictions", name, value,
                new ConfigDescription(description + " Set by the host, the same for everyone.", null, new ConfigurationManagerAttributes { IsAdminOnly = true }));

        private static bool On(ConfigEntry<bool> rule) => _all.Value || rule.Value;

        private static bool Ignored(Player.PlacementStatus status)
        {
            switch (status)
            {
                case Player.PlacementStatus.Invalid: return On(_invalid);
                case Player.PlacementStatus.BlockedbyPlayer: return On(_blockedByPlayer);
                case Player.PlacementStatus.NoBuildZone: return On(_noBuildZone);
                case Player.PlacementStatus.MoreSpace: return On(_space);
                case Player.PlacementStatus.NoTeleportArea: return On(_teleport);
                case Player.PlacementStatus.ExtensionMissingStation: return On(_missingExtensionStation);
                case Player.PlacementStatus.WrongBiome: return On(_biome);
                case Player.PlacementStatus.NeedCultivated: return On(_cultivated);
                case Player.PlacementStatus.NeedDirt: return On(_dirt);
                case Player.PlacementStatus.NotInDungeon: return On(_dungeon);
                default: return false; // valid, ward-protected areas and the rest stay vanilla
            }
        }

        [HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
        [HarmonyPostfix]
        private static void UpdatePlacementGhost(Player __instance)
        {
            if (Ghost(__instance) == null) return;
            var status = Status(__instance);
            if (status == Player.PlacementStatus.Valid || !Ignored(status)) return;
            Status(__instance) = Player.PlacementStatus.Valid;
            SetGhostValid(__instance, true);
        }

        [HarmonyPatch(typeof(Location), nameof(Location.IsInsideNoBuildLocation))]
        [HarmonyPostfix]
        private static void NoBuildLocation(ref bool __result)
        {
            if (__result && On(_noBuildZone)) __result = false;
        }

        // "Is the required station near?" answers yes with a placeholder station. It is inactive, so the game never
        // registers it anywhere; callers only check that a station was found.
        [HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.HaveBuildStationInRange))]
        [HarmonyPostfix]
        private static void HaveStation(ref CraftingStation __result)
        {
            if (__result != null || !On(_missingStation)) return;
            if (_anyStation == null)
            {
                var go = new GameObject("BuildRestrictions_AnyStation");
                go.SetActive(false);
                Object.DontDestroyOnLoad(go);
                _anyStation = go.AddComponent<CraftingStation>();
            }
            __result = _anyStation;
        }
    }
}
