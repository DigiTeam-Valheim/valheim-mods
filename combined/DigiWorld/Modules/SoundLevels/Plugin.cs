// Volume sliders for noisy things in the world: the shield generator, whose hum plays nonstop while it runs, and
// portals, which hum while you stand near a connected one.
// The game plays sounds in three ways, and each is scaled where it sets the volume, so a slider change is heard at once:
// - ZSFX (the game's sound player) multiplies its volume by a modifier every frame (carts use it for their wheels).
// - EffectFade (the portal's glow and hum when its target is found) sets its sound to fade x a base volume every
//   frame; that base volume is scaled (the original is kept).
// - A plain AudioSource gets its volume scaled once (the original is kept).
// The shield generator's low-fuel warning loop, created later, is included. One-shot sounds (switching on and off,
// teleporting, hits on the dome) are not.
// Light: work only when a generator or portal loads, when the low-fuel loop starts, and when a slider changes.
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SoundLevels
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.soundlevels";
        public const string Name = "Digi's Sound Levels";
        public const string Version = "1.1.0";

        private enum Kind { None, ShieldGenerator, Portal }

        private sealed class Fade
        {
            public Kind Kind;
            public float Base;
        }

        private static ConfigEntry<int> _shieldGenerator, _portals;
        private static BepInEx.Logging.ManualLogSource _log;
        private static readonly HashSet<Kind> Described = new HashSet<Kind>();

        private static readonly Dictionary<GameObject, Kind> Roots = new Dictionary<GameObject, Kind>();
        private static readonly Dictionary<EffectFade, Fade> Fades = new Dictionary<EffectFade, Fade>();
        private static readonly Dictionary<AudioSource, float> RawVolumes = new Dictionary<AudioSource, float>();
        // The low-fuel loop instances a generator had when they were last scaled (a new array = a new loop).
        private static readonly ConditionalWeakTable<ShieldGenerator, GameObject[]> ScaledLoops = new ConditionalWeakTable<ShieldGenerator, GameObject[]>();
        private static readonly AccessTools.FieldRef<ShieldGenerator, GameObject[]> LowLoops =
            AccessTools.FieldRefAccess<ShieldGenerator, GameObject[]>("m_lowLoopInstances");
        private static readonly AccessTools.FieldRef<EffectFade, float> BaseVolume =
            AccessTools.FieldRefAccess<EffectFade, float>("m_baseVolume");

        private void Awake()
        {
            _log = Logger;
            _shieldGenerator = Config.Bind("Volume", "Shield generator (%)", 20, new ConfigDescription(
                "Volume of the shield generator: its hum and its low-fuel warning loop. 100 = the game's own volume.",
                new AcceptableValueRange<int>(0, 100)));
            _portals = Config.Bind("Volume", "Portals (%)", 30, new ConfigDescription(
                "Volume of the hum a portal makes while you are near and it is connected. 100 = the game's own volume.",
                new AcceptableValueRange<int>(0, 100)));
            _shieldGenerator.SettingChanged += OnChanged;
            _portals.SettingChanged += OnChanged;
            PatchModule(typeof(Plugin));
            // Switched on (or reloaded) in a running world: what is already there.
            foreach (var generator in FindObjectsOfType<ShieldGenerator>()) Track(generator.gameObject, Kind.ShieldGenerator);
            foreach (var portal in FindObjectsOfType<TeleportWorld>()) Track(portal.gameObject, Kind.Portal);
            foreach (var fade in FindObjectsOfType<EffectFade>()) TrackFade(fade);
        }

        private void OnDestroy()
        {
            _shieldGenerator.SettingChanged -= OnChanged;
            _portals.SettingChanged -= OnChanged;
            ApplyAll(true);
            Roots.Clear();
            Fades.Clear();
            RawVolumes.Clear();
        }

        private static float FactorOf(Kind kind)
        {
            if (kind == Kind.ShieldGenerator) return Mathf.Clamp01(_shieldGenerator.Value / 100f);
            if (kind == Kind.Portal) return Mathf.Clamp01(_portals.Value / 100f);
            return 1f;
        }

        private static Kind KindOf(Component component)
        {
            if (component.GetComponentInParent<ShieldGenerator>() != null) return Kind.ShieldGenerator;
            if (component.GetComponentInParent<TeleportWorld>() != null) return Kind.Portal;
            return Kind.None;
        }

        private static void OnChanged(object sender, System.EventArgs e) => ApplyAll(false);

        private static void ApplyAll(bool restore)
        {
            var gone = new List<GameObject>();
            foreach (var pair in Roots)
            {
                if (pair.Key == null) { gone.Add(pair.Key); continue; }
                Scale(pair.Key, restore ? 1f : FactorOf(pair.Value));
                var generator = pair.Value == Kind.ShieldGenerator ? pair.Key.GetComponent<ShieldGenerator>() : null;
                var loops = generator != null ? LowLoops(generator) : null;
                if (loops == null) continue;
                foreach (var loop in loops)
                    if (loop != null) Scale(loop, restore ? 1f : FactorOf(Kind.ShieldGenerator));
            }
            foreach (var key in gone) Roots.Remove(key);
            var goneFades = new List<EffectFade>();
            foreach (var pair in Fades)
            {
                if (pair.Key == null) { goneFades.Add(pair.Key); continue; }
                BaseVolume(pair.Key) = pair.Value.Base * (restore ? 1f : FactorOf(pair.Value.Kind));
            }
            foreach (var key in goneFades) Fades.Remove(key);
        }

        [HarmonyPatch(typeof(ShieldGenerator), "Start")]
        [HarmonyPostfix]
        private static void GeneratorStarted(ShieldGenerator __instance) => Track(__instance.gameObject, Kind.ShieldGenerator);

        [HarmonyPatch(typeof(TeleportWorld), "Awake")]
        [HarmonyPostfix]
        private static void PortalAwake(TeleportWorld __instance) => Track(__instance.gameObject, Kind.Portal);

        // An EffectFade keeps the volume its sound had when it woke up and fades to it; scale that.
        [HarmonyPatch(typeof(EffectFade), "Awake")]
        [HarmonyPostfix]
        private static void FadeAwake(EffectFade __instance) => TrackFade(__instance);

        // The low-fuel loop is created (and removed) while the generator runs.
        [HarmonyPatch(typeof(ShieldGenerator), "UpdateShield")]
        [HarmonyPostfix]
        private static void GeneratorUpdated(ShieldGenerator __instance)
        {
            var loops = LowLoops(__instance);
            if (loops == null || (ScaledLoops.TryGetValue(__instance, out var done) && ReferenceEquals(done, loops))) return;
            ScaledLoops.Remove(__instance);
            ScaledLoops.Add(__instance, loops);
            foreach (var loop in loops)
                if (loop != null) Scale(loop, FactorOf(Kind.ShieldGenerator));
        }

        private static void Track(GameObject root, Kind kind)
        {
            if (root == null || Roots.ContainsKey(root)) return;
            Roots[root] = kind;
            Describe(root, kind);
            Scale(root, FactorOf(kind));
        }

        private static void TrackFade(EffectFade fade)
        {
            if (fade == null || Fades.ContainsKey(fade)) return;
            var kind = KindOf(fade);
            if (kind == Kind.None) return;
            var entry = new Fade { Kind = kind, Base = BaseVolume(fade) };
            Fades[fade] = entry;
            BaseVolume(fade) = entry.Base * FactorOf(kind);
        }

        // ZSFX sounds and plain AudioSources; sounds an EffectFade drives are left to its base volume above.
        private static void Scale(GameObject root, float factor)
        {
            foreach (var sfx in root.GetComponentsInChildren<ZSFX>(true)) sfx.SetVolumeModifier(factor);
            foreach (var source in root.GetComponentsInChildren<AudioSource>(true))
            {
                if (source.GetComponent<ZSFX>() != null || source.GetComponentInParent<EffectFade>() != null) continue;
                if (!RawVolumes.TryGetValue(source, out var volume)) RawVolumes[source] = volume = source.volume;
                source.volume = volume * factor;
            }
        }

        // Once per session and kind: which sounds it has, so the log shows what the slider covers.
        private static void Describe(GameObject root, Kind kind)
        {
            if (!Described.Add(kind)) return;
            var text = new StringBuilder(kind + " sounds:");
            foreach (var source in root.GetComponentsInChildren<AudioSource>(true))
                text.Append($" {source.name} ({(source.clip != null ? source.clip.name : "no clip")}" +
                            $"{(source.loop ? ", loop" : "")}" +
                            $"{(source.GetComponent<ZSFX>() != null ? ", ZSFX" : source.GetComponentInParent<EffectFade>() != null ? ", EffectFade" : ", plain")})");
            _log?.LogInfo(text.ToString());
        }
    }
}
