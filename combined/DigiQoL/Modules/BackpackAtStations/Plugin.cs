// Adventure Backpacks only auto-opens the backpack when the inventory is opened with the inventory key.
// Crafting stations (forge, workbench, HoneyPlus apiary, ...) open the inventory through InventoryGui.Show(null, 3),
// which bypasses that, so the backpack panel stays closed. This opens it there too, using Adventure Backpacks'
// own OpenBackpack and its "Open with Inventory" setting, then restores the station's active UI group.
// Adventure Backpacks 2.4 and newer do this themselves (their setting "Open with Crafting Station"); then this does
// nothing, so that setting alone decides.
using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace BackpackAtStations
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.backpackatstations";
        public const string Name = "Digi's Backpack At Stations";
        public const string Version = "1.0.4";
        private const string AdventureBackpacksGuid = "vapok.mods.adventurebackpacks";

        private static FieldInfo _backpackIsOpen;
        private static MethodInfo _openBackpack;
        private static MethodInfo _canOpenBackpack;
        private static ConfigEntry<bool> _openWithInventory;
        private static MethodInfo _setActiveGroup;
        private static bool _opening;

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
            var registry = AccessTools.TypeByName("AdventureBackpacks.Configuration.ConfigRegistry");
            if (registry != null && AccessTools.Field(registry, "OpenWithCraftingStation") != null)
            {
                Logger.LogInfo("Adventure Backpacks opens the backpack at crafting stations itself (its setting \"Open with Crafting Station\"); nothing to do.");
                return;
            }
            _backpackIsOpen = AccessTools.Field(AccessTools.TypeByName("AdventureBackpacks.Patches.InventoryGuiPatches"), "BackpackIsOpen");
            var extensions = AccessTools.TypeByName("AdventureBackpacks.Extensions.PlayerExtensions");
            _openBackpack = AccessTools.Method(extensions, "OpenBackpack", new[] { typeof(Player), typeof(InventoryGui) });
            _canOpenBackpack = AccessTools.Method(extensions, "CanOpenBackpack", new[] { typeof(Player) });
            _openWithInventory = AccessTools.Field(AccessTools.TypeByName("AdventureBackpacks.Configuration.ConfigRegistry"), "OpenWithInventory")
                ?.GetValue(null) as ConfigEntry<bool>;
            _setActiveGroup = AccessTools.Method(typeof(InventoryGui), "SetActiveGroup", new[] { typeof(int), typeof(bool) });

            if (_backpackIsOpen == null || _openBackpack == null || _canOpenBackpack == null || _openWithInventory == null)
            {
                Logger.LogError("Disabled, Adventure Backpacks' backpack functions were not found.");
                return;
            }
            ModuleHarmony.PatchAll(typeof(ShowPatch));
            Logger.LogInfo("Backpack now also opens at crafting stations.");
        }

        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Show), typeof(Container), typeof(int))]
        private static class ShowPatch
        {
            private static void Postfix(InventoryGui __instance, Container container, int activeGroup)
            {
                // Only the station path: no container, crafting group active.
                if (_opening || container != null || activeGroup != 3) return;
                var player = Player.m_localPlayer;
                if (player == null || player.GetCurrentCraftingStation() == null || !_openWithInventory.Value) return;
                if ((bool)_backpackIsOpen.GetValue(null) || !(bool)_canOpenBackpack.Invoke(null, new object[] { player })) return;

                _opening = true;
                try
                {
                    _openBackpack.Invoke(null, new object[] { player, __instance });
                    _setActiveGroup?.Invoke(__instance, new object[] { activeGroup, false });
                }
                catch (Exception e)
                {
                    BepInEx.Logging.Logger.CreateLogSource(Name).LogWarning("Could not open backpack at station: " + e.Message);
                }
                finally
                {
                    _opening = false;
                }
            }
        }
    }
}
