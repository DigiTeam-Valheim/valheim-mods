// Glowing pickables (thistle, yellow mushrooms, Mistlands Jotun Puffs and Magecap, ...) each carry a real light, and a field of them adds dozens of
// lights the GPU has to draw. This switches those lights off: the plants still look glowing (that is their material),
// they just no longer light up their surroundings. Which pickables is configurable; changes apply right away.
using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace PickableLights
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.pickablelights";
        public const string Name = "Digi's Pickable Lights";
        public const string Version = "1.1.0";

        // Default list. The old default (before the Mistlands plants were added) is replaced by it once.
        private const string Defaults = "Pickable_Thistle, Pickable_Mushroom_yellow, Pickable_Mushroom_JotunPuffs, Pickable_Mushroom_Magecap";
        private const string OldDefaults = "Pickable_Thistle, Pickable_Mushroom_yellow";

        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<string> _pickables;
        private static HashSet<string> _names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<Pickable> Loaded = new HashSet<Pickable>();


        private void Awake()
        {
            _enabled = Config.Bind("General", "Lights off", true,
                "Glowing pickables (thistle, yellow mushrooms, Jotun Puffs, Magecap) still glow, but no longer light up their surroundings. Better FPS where many of them grow.");
            _pickables = Config.Bind("General", "Pickables", Defaults,
                "Which pickables lose their light, by internal name (the one the spawn console command uses), comma-separated. " +
                "Others that glow: Pickable_Mushroom_blue (frost caves), Pickable_RoyalJelly, Pickable_GlowWorm.");
            if (_pickables.Value.Replace(" ", "") == OldDefaults.Replace(" ", "")) _pickables.Value = Defaults;
            _pickables.SettingChanged += (_, __) => { ParseNames(); ApplyAll(); };
            _enabled.SettingChanged += (_, __) => ApplyAll();
            ParseNames();
            PatchModule(typeof(Plugin));
        }

        private static void ParseNames() =>
            _names = new HashSet<string>((_pickables.Value ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0),
                StringComparer.OrdinalIgnoreCase);

        [HarmonyPatch(typeof(Pickable), "Awake")]
        [HarmonyPostfix]
        private static void PickableAwake(Pickable __instance)
        {
            Loaded.Add(__instance);
            Apply(__instance);
        }

        private static void ApplyAll()
        {
            Loaded.RemoveWhere(p => p == null);
            foreach (var pickable in Loaded) Apply(pickable);
        }

        private static void Apply(Pickable pickable)
        {
            if (pickable == null) return;
            var off = _enabled.Value && _names.Contains(Utils.GetPrefabName(pickable.gameObject));
            foreach (var light in pickable.GetComponentsInChildren<Light>(true))
            {
                // The game's light manager turns lights back on by distance, so it is paused together with the light.
                var lod = light.GetComponent<LightLod>();
                if (off)
                {
                    if (lod != null) lod.enabled = false;
                    light.enabled = false;
                }
                else if (!light.enabled || (lod != null && !lod.enabled))
                {
                    light.enabled = true;
                    if (lod != null) lod.enabled = true;
                }
            }
        }
    }
}
