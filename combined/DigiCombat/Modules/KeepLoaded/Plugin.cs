// Crossbows and the grappling hook forget they were loaded the moment you put them away, so you always reload after
// switching. This keeps a loaded crossbow / hook loaded while it is unequipped: reload in advance, switch to another
// weapon, switch back and it is ready to fire. The loaded mark is stored on the item itself (survives relogging).
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace KeepLoaded
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.keeploaded";
        public const string Name = "Digi's Keep Loaded";
        public const string Version = "1.0.2";
        private const string LoadedKey = "pieter_keeploaded";

        private static ConfigEntry<bool> _crossbows;
        private static ConfigEntry<bool> _grapplingHook;

        private static readonly AccessTools.FieldRef<Player, ItemDrop.ItemData> WeaponLoaded =
            AccessTools.FieldRefAccess<Player, ItemDrop.ItemData>("m_weaponLoaded");
        private static readonly System.Action<Player, ItemDrop.ItemData> SetWeaponLoaded =
            AccessTools.MethodDelegate<System.Action<Player, ItemDrop.ItemData>>(AccessTools.Method(typeof(Player), "SetWeaponLoaded"));

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
            _crossbows = Config.Bind("General", "Crossbows stay loaded", true,
                "A loaded crossbow stays loaded when you switch to another weapon and back.");
            _grapplingHook = Config.Bind("General", "Grappling hook stays loaded", true,
                "A loaded grappling hook stays loaded when you switch to another item and back.");
            PatchModule(typeof(Plugin));
        }

        private static bool Applies(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null || !item.m_shared.m_attack.m_requiresReload) return false;
            if (item.m_shared.m_skillType == Skills.SkillType.Crossbows) return _crossbows.Value;
            var prefab = item.m_dropPrefab != null ? item.m_dropPrefab.name : "";
            if (prefab.Contains("Grappling") || item.m_shared.m_name.ToLowerInvariant().Contains("grappl"))
                return _grapplingHook.Value;
            return false;
        }

        // Unequipping clears the loaded state; remember it on the item first.
        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.UnequipItem))]
        [HarmonyPrefix]
        private static void Unequip(Humanoid __instance, ItemDrop.ItemData item)
        {
            if (!(__instance is Player player) || player != Player.m_localPlayer || item == null) return;
            if (WeaponLoaded(player) == item && Applies(item) && player.IsItemEquiped(item))
                item.m_customData[LoadedKey] = "1";
        }

        // Runs every frame for the equipped weapon; vanilla queues a reload when it is not loaded. A remembered load
        // is restored instead, so no reload starts.
        [HarmonyPatch(typeof(Player), "UpdateWeaponLoading")]
        [HarmonyPrefix]
        private static void UpdateWeaponLoading(Player __instance, ItemDrop.ItemData weapon)
        {
            if (weapon == null || WeaponLoaded(__instance) == weapon || !weapon.m_customData.ContainsKey(LoadedKey)) return;
            weapon.m_customData.Remove(LoadedKey);
            if (Applies(weapon)) SetWeaponLoaded(__instance, weapon);
        }
    }
}
