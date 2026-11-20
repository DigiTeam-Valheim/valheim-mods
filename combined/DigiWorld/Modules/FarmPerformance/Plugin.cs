// Big crop fields cost FPS in two ways:
// - Every crop casts a shadow, and shadows are drawn again for each shadow cascade. Crops (plants that grow into
//   something you pick, and those pickables) can stop casting them; saplings that grow into trees keep theirs. Not by
//   "needs cultivated ground": Plant Everything can switch that off for crops.
// - Epic Loot's Green Thumb (a cultivator enchantment) gives every plant it speeds up its own sparkle particle system,
//   a copy of the Magecap's: a field of thousands means thousands of particle systems. Show them on all, a few (1 plant
//   in 10, always the same ones) or none. Found by the child's name, so no reference to Epic Loot is needed.
// Each plant is set up once when it loads; a setting change applies to everything loaded at once.
// Harvesting a whole field at once (items straight into the inventory, fewer pick effects): see Harvest.cs; and a
// Valheim Enhanced Edition list that made every destroyed object slow while its FastUnloadCheck is off: VeeZoneFix.cs;
// a big field growing up all at once (fewer grow effects, no log line per plant): Growth.cs.
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace FarmPerformance
{
    public class Plugin : DigiCore.DigiModule
    {
        public enum Sparkles { All, Few, None }

        private const string SparkleName = "vfx_el_green_thumb"; // Epic Loot's GreenThumb.VfxName
        private const uint FewOutOf = 10;

        private static ConfigEntry<bool> _shadows;
        private static ConfigEntry<Sparkles> _sparkles;
        private static readonly HashSet<string> CropPickables = new HashSet<string>();
        private static readonly HashSet<string> CropPlants = new HashSet<string>();
        private static readonly Dictionary<Renderer, ShadowCastingMode> Unshadowed = new Dictionary<Renderer, ShadowCastingMode>();
        private static bool _cropsKnown;

        private void Awake()
        {
            _shadows = Config.Bind("General", "Crop shadows", false,
                "Crops (barley, flax, oats, carrots, ... growing or ready) cast shadows. Off = they do not, which saves a lot " +
                "of drawing in a big field; trees and saplings keep their shadows.");
            _sparkles = Config.Bind("General", "Green Thumb sparkles", Sparkles.Few,
                "Epic Loot's Green Thumb cultivator gives every plant it speeds up its own sparkle effect. All = every one, " +
                "Few = 1 plant in 10 (enough to see the field is boosted), None = no sparkles. The faster growth is not changed.");
            _shadows.SettingChanged += (s, e) => ApplyAll();
            _sparkles.SettingChanged += (s, e) => ApplyAll();
            Harvest.Bind(Config);
            Growth.Bind(Config);
            PatchModule(typeof(Plugin));
            PatchModule(typeof(Harvest));
            PatchModule(typeof(Growth));
            VeeZoneFix.Patch(ModuleHarmony, Logger);
            ApplyAll();
        }

        private void OnDestroy()
        {
            VeeZoneFix.Unpatch();
            foreach (var pair in Unshadowed)
                if (pair.Key != null) pair.Key.shadowCastingMode = pair.Value;
            Unshadowed.Clear();
            foreach (var plant in FindObjectsByType<Plant>(FindObjectsSortMode.None))
            {
                var sparkle = plant.transform.Find(SparkleName);
                if (sparkle != null) sparkle.gameObject.SetActive(true);
            }
        }

        // After Epic Loot's own postfixes, which add the sparkle when a plant loads or is planted.
        [HarmonyPatch(typeof(Plant), "Awake")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void PlantLoaded(Plant __instance) => Apply(__instance);

        [HarmonyPatch(typeof(Piece), nameof(Piece.SetCreator))]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Planted(Piece __instance)
        {
            var plant = __instance.GetComponent<Plant>();
            if (plant != null) Apply(plant);
        }

        [HarmonyPatch(typeof(Pickable), "Awake")]
        [HarmonyPostfix]
        private static void PickableLoaded(Pickable __instance)
        {
            if (IsCrop(__instance)) SetShadows(__instance.gameObject);
        }

        private static void ApplyAll()
        {
            var gone = new List<Renderer>();
            foreach (var renderer in Unshadowed.Keys)
                if (renderer == null) gone.Add(renderer);
            foreach (var renderer in gone) Unshadowed.Remove(renderer);
            foreach (var plant in FindObjectsByType<Plant>(FindObjectsSortMode.None)) Apply(plant);
            foreach (var pickable in FindObjectsByType<Pickable>(FindObjectsSortMode.None))
                if (IsCrop(pickable)) SetShadows(pickable.gameObject);
        }

        private static void Apply(Plant plant)
        {
            if (plant == null) return;
            if (IsCrop(plant)) SetShadows(plant.gameObject);
            var sparkle = plant.transform.Find(SparkleName);
            if (sparkle == null) return;
            var zdo = _sparkles.Value == Sparkles.Few ? plant.GetComponent<ZNetView>()?.GetZDO() : null;
            var show = _sparkles.Value == Sparkles.All || (zdo != null && zdo.m_uid.ID % FewOutOf == 0);
            if (sparkle.gameObject.activeSelf != show) sparkle.gameObject.SetActive(show);
        }

        // Off: remember each renderer's own setting, so switching back (or unloading this) restores it.
        private static void SetShadows(GameObject crop)
        {
            foreach (var renderer in crop.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is ParticleSystemRenderer) continue;
                if (_shadows.Value)
                {
                    if (Unshadowed.TryGetValue(renderer, out var mode))
                    {
                        renderer.shadowCastingMode = mode;
                        Unshadowed.Remove(renderer);
                    }
                }
                else if (renderer.shadowCastingMode != ShadowCastingMode.Off)
                {
                    Unshadowed[renderer] = renderer.shadowCastingMode;
                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                }
            }
        }

        private static bool IsCrop(Pickable pickable) => Crops() && CropPickables.Contains(Utils.GetPrefabName(pickable.gameObject));

        private static bool IsCrop(Plant plant) => Crops() && CropPlants.Contains(Utils.GetPrefabName(plant.gameObject));

        // Crops: plants that grow into a pickable, and those pickables (read once from the game's prefabs).
        private static bool Crops()
        {
            if (_cropsKnown) return true;
            if (ZNetScene.instance == null) return false;
            _cropsKnown = true;
            foreach (var prefab in ZNetScene.instance.m_prefabs)
            {
                var plant = prefab != null ? prefab.GetComponent<Plant>() : null;
                if (plant == null || plant.m_grownPrefabs == null) continue;
                foreach (var grown in plant.m_grownPrefabs)
                {
                    if (grown == null || grown.GetComponent<Pickable>() == null) continue;
                    CropPickables.Add(grown.name);
                    CropPlants.Add(prefab.name);
                }
            }
            return true;
        }
    }
}
