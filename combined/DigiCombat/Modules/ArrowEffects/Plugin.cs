// Fire/poison/frost arrows carry particles, a light and (poison) a glow sphere on both the arrow item (also the one
// nocked on the bow) and the flying projectile; they can hide what you aim at. This scales those effects per arrow
// element with a slider. Ammo is classified by its damage type, so modded arrows and bolts are included. The prefabs
// are scaled from stored originals, so changing a slider re-applies cleanly. Visual only, never synced.
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ArrowEffects
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.arroweffects";
        public const string Name = "Digi's Arrow Effects";
        public const string Version = "1.0.2";

        private enum Element { Fire, Poison, Frost }

        private static readonly Dictionary<Element, ConfigEntry<int>> Strength = new Dictionary<Element, ConfigEntry<int>>();
        private static readonly Dictionary<GameObject, Element> Prefabs = new Dictionary<GameObject, Element>();

        private sealed class Original
        {
            public float Size, Rate, Distance;
            public int MaxParticles;
            public bool Emission;
            public float Intensity, Range;
            public Vector3 Scale;
        }

        private static readonly Dictionary<Component, Original> Originals = new Dictionary<Component, Original>();
        private static BepInEx.Logging.ManualLogSource _log;

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
            _log = Logger;
            foreach (Element e in new[] { Element.Fire, Element.Poison, Element.Frost })
            {
                var entry = Config.Bind("Arrow effects", $"{e} arrows (%)", 100,
                    new ConfigDescription($"How strong the effects of {e.ToString().ToLower()} arrows are (flames or clouds, light, glow): " +
                                          "on your bow, in flight and lying on the ground. 100 = normal, 0 = none. Lower = better FPS.",
                        new AcceptableValueRange<int>(0, 100)));
                entry.SettingChanged += (_, __) => ApplyAll();
                Strength[e] = entry;
            }
            ModuleHarmony.PatchAll(typeof(Plugin));
        }

        [HarmonyPatch(typeof(ObjectDB), "Awake")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void ObjectDBAwake(ObjectDB __instance) => Collect(__instance);

        [HarmonyPatch(typeof(ObjectDB), "CopyOtherDB")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void ObjectDBCopy(ObjectDB __instance) => Collect(__instance);

        private static void Collect(ObjectDB db)
        {
            if (db?.m_items == null) return;
            var before = Prefabs.Count;
            foreach (var go in db.m_items)
            {
                var shared = go ? go.GetComponent<ItemDrop>()?.m_itemData?.m_shared : null;
                if (shared == null || shared.m_itemType != ItemDrop.ItemData.ItemType.Ammo) continue;
                var d = shared.m_damages;
                Element element;
                if (d.m_poison > 0f && d.m_poison >= d.m_fire && d.m_poison >= d.m_frost) element = Element.Poison;
                else if (d.m_fire > 0f && d.m_fire >= d.m_frost) element = Element.Fire;
                else if (d.m_frost > 0f) element = Element.Frost;
                else continue;

                Prefabs[go] = element;
                var projectile = shared.m_attack?.m_attackProjectile;
                if (projectile) Prefabs[projectile] = element;
            }
            if (Prefabs.Count != before)
                _log.LogInfo($"Arrow effects: {Prefabs.Count} arrow and projectile prefabs found.");
            ApplyAll();
        }

        private static void ApplyAll()
        {
            foreach (var pair in Prefabs)
                if (pair.Key) Apply(pair.Key, Strength[pair.Value].Value / 100f);
        }

        private static void Apply(GameObject prefab, float k)
        {
            foreach (var ps in prefab.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = ps.main;
                var emission = ps.emission;
                if (!Originals.TryGetValue(ps, out var o))
                {
                    o = new Original
                    {
                        Size = main.startSizeMultiplier, MaxParticles = main.maxParticles, Emission = emission.enabled,
                        Rate = emission.rateOverTimeMultiplier, Distance = emission.rateOverDistanceMultiplier,
                    };
                    Originals[ps] = o;
                }
                main.startSizeMultiplier = o.Size * k;
                main.maxParticles = Mathf.Max(k > 0f ? 1 : 0, Mathf.RoundToInt(o.MaxParticles * k));
                emission.rateOverTimeMultiplier = o.Rate * k;
                emission.rateOverDistanceMultiplier = o.Distance * k;
                emission.enabled = o.Emission && k > 0f;
            }

            foreach (var light in prefab.GetComponentsInChildren<Light>(true))
            {
                if (!Originals.TryGetValue(light, out var o))
                {
                    o = new Original { Intensity = light.intensity, Range = light.range };
                    Originals[light] = o;
                }
                light.intensity = o.Intensity * k;
                light.range = o.Range * Mathf.Max(k, 0.01f);
                light.enabled = k > 0f;
            }

            // Glow meshes such as the poison "Sphere"; the arrow model itself is left alone.
            foreach (var renderer in prefab.GetComponentsInChildren<MeshRenderer>(true))
            {
                var t = renderer.transform;
                if (t.name != "Sphere") continue;
                if (!Originals.TryGetValue(t, out var o))
                {
                    o = new Original { Scale = t.localScale };
                    Originals[t] = o;
                }
                t.localScale = o.Scale * k;
                renderer.enabled = k > 0f;
            }
        }
    }
}
