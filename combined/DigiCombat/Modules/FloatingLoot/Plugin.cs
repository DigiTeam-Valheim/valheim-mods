// Dropped items float on water instead of sinking, using the same floating behaviour as wood.
// Kept light: items that appear far above any water (mountains, inland) do not get it, since each floating item costs
// a little physics work every frame. Excluded items keep sinking.
using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FloatingLoot
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.floatingloot";
        public const string Name = "Digi's Floating Loot";
        public const string Version = "1.0.1";

        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<string> _excluded;
        private static ConfigEntry<float> _maxHeight;
        private static HashSet<string> _excludedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static Floating _template;

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
            _enabled = Config.Bind("General", "Enabled", true, "Dropped items float on water instead of sinking (items dropped after you turn it on).");
            _excluded = Config.Bind("General", "Excluded items", "",
                "Items that should still sink, comma-separated, by their internal name (the one the spawn console command uses), e.g. \"Coins, IronScrap\".");
            _maxHeight = Config.Bind("General", "Max height above water (m)", 50f,
                new ConfigDescription("Items that drop higher than this above sea level do not float (they will not reach water anyway; saves performance). 0 = all items float.", new AcceptableValueRange<float>(0f, 500f)));
            _excluded.SettingChanged += (_, __) => ParseExcluded();
            ParseExcluded();
            PatchModule(typeof(Plugin));
        }

        private static void ParseExcluded() =>
            _excludedSet = new HashSet<string>((_excluded.Value ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0),
                StringComparer.OrdinalIgnoreCase);

        [HarmonyPatch(typeof(ItemDrop), "Awake")]
        [HarmonyPrefix]
        private static void ItemAwake(ItemDrop __instance)
        {
            if (!_enabled.Value) return;
            var go = __instance.gameObject;
            if (go.GetComponent<Floating>() != null || go.GetComponent<IWaterInteractable>() != null) return;
            if (go.GetComponent<Rigidbody>() == null || go.GetComponent<ZNetView>() == null || go.GetComponentInChildren<Collider>() == null) return;
            if (_excludedSet.Count > 0 && _excludedSet.Contains(Utils.GetPrefabName(go))) return;
            // Measured against sea level (all oceans, lakes and rivers share it), so items on a dock or shore qualify.
            if (_maxHeight.Value > 0f && ZoneSystem.instance != null &&
                go.transform.position.y - ZoneSystem.instance.m_waterLevel > _maxHeight.Value) return;

            var floating = go.AddComponent<Floating>();
            var template = Template();
            if (template == null) return;
            floating.m_waterLevelOffset = template.m_waterLevelOffset;
            floating.m_forceDistance = template.m_forceDistance;
            floating.m_force = template.m_force;
            floating.m_balanceForceFraction = template.m_balanceForceFraction;
            floating.m_damping = template.m_damping;
            floating.m_impactEffects = template.m_impactEffects;
        }

        // Wood's floating settings, or any item's that floats in vanilla.
        private static Floating Template()
        {
            if (_template != null || ObjectDB.instance == null) return _template;
            var wood = ObjectDB.instance.GetItemPrefab("Wood");
            if (wood != null && wood.TryGetComponent(out _template)) return _template;
            foreach (var item in ObjectDB.instance.m_items)
                if (item != null && item.TryGetComponent(out _template)) return _template;
            return null;
        }
    }
}
