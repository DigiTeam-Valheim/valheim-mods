// Fades the particle effects that cover creatures in combat, so you can see their attacks coming: the flames, frost
// and poison on a creature, the burst of a hit (blood, sparks, crits), weapon impacts (incl. elemental bursts), and
// projectile and area-attack impacts. Faded, not removed: opacity, brightness and size are sliders, every kind of
// effect has its own switch, and real lights are never changed. Visual only, client-side, never synced.
//
// Light by design (what Dimmer Combat Particle Effects did with a hook on every status effect of every creature, every
// frame, this does from the effect itself):
// - The effect is recognised when the game creates it, by which effect list made it: status effects and weapons share
//   their lists with the game's templates (two lookups), a creature's hit effects are its own (found through the
//   creature the effect is attached to). Only projectile and area-attack impacts need a hook, and only when they hit.
// - Effects other players' games create reach this game as network objects: matched by their template, one lookup.
// - The faded values per effect are worked out once and reused (no new gradients per hit); no lists are allocated.
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace HitEffects
{
    public class Plugin : DigiCore.DigiModule
    {
        [System.Flags]
        private enum Kind { None = 0, Status = 1, CreatureHit = 2, Weapon = 4, Projectile = 8, Area = 16 }

        private static ConfigEntry<int> _opacity, _brightness, _size;
        private static ConfigEntry<bool> _status, _creatureHits, _weapons, _projectiles, _areas, _players, _log;
        private static Plugin _instance;

        // Effect lists shared with the game's templates (status effects, weapons and attacks), per ObjectDB.
        private static ObjectDB _listsOf;
        private static readonly HashSet<EffectList> StatusLists = new HashSet<EffectList>();
        private static readonly HashSet<EffectList> WeaponLists = new HashSet<EffectList>();
        // Network effect templates (prefab hash) and what kind of effect they are, per ZNetScene.
        private static ZNetScene _networkOf;
        private static readonly Dictionary<int, Kind> NetworkEffects = new Dictionary<int, Kind>();
        // Projectile / area-attack impacts being created right now, with their kind.
        private static readonly List<EffectList> Impacts = new List<EffectList>(4);
        private static readonly List<Kind> ImpactKinds = new List<Kind>(4);

        private sealed class Faded
        {
            public int Version, Count;
            public ParticleSystem.MinMaxGradient[] Colors;
            public ParticleSystem.MinMaxCurve[] Size, SizeX, SizeY, SizeZ;
        }

        private static readonly Dictionary<string, Faded> Cache = new Dictionary<string, Faded>();
        private static readonly List<ParticleSystem> Systems = new List<ParticleSystem>(16);
        private static int _version;

        private void Awake()
        {
            _instance = this;
            _opacity = Config.Bind("1 - Strength", "Opacity (%)", 35, new ConfigDescription(
                "How visible the faded effects stay: 100 = normal, 35 = faint (default), 0 = invisible. This is the main " +
                "setting: lower it until you can see an enemy's attack wind-up through the effects. Applies from the next " +
                "effect on.", new AcceptableValueRange<int>(0, 100)));
            _brightness = Config.Bind("1 - Strength", "Brightness (%)", 100, new ConfigDescription(
                "Also darkens the colours: 100 = normal. Leave it at 100 unless an effect still looks bright after lowering " +
                "Opacity (glowing effects add light on top of the picture and partly ignore Opacity). Real lights are never " +
                "changed, so hits still light up dark places.", new AcceptableValueRange<int>(0, 100)));
            _size = Config.Bind("1 - Strength", "Size (%)", 100, new ConfigDescription(
                "Shrinks the effects so they cover less of the creature, instead of (or as well as) fading them: " +
                "100 = normal, 50 = half as big.", new AcceptableValueRange<int>(10, 100)));
            _status = Config.Bind("2 - Which effects", "Status effects on creatures", true,
                "The flames on a burning creature, the frost shell, the poison cloud and other status effect visuals, and the " +
                "small burst they repeat on every damage tick. They last as long as the effect does, so they hide a creature " +
                "the longest: this is most of what this mod does.");
            _creatureHits = Config.Bind("2 - Which effects", "Hit bursts on creatures", true,
                "Blood, sparks and the extra burst of a critical hit or sneak attack that a creature throws off when it is hit.");
            _weapons = Config.Bind("2 - Which effects", "Weapon impacts", true,
                "The burst a weapon makes where it hits, including the fire, frost, lightning, poison and spirit bursts of " +
                "elemental weapons.");
            _projectiles = Config.Bind("2 - Which effects", "Projectile impacts", true,
                "Where an arrow, bolt or staff projectile hits. Water splashes are not changed.");
            _areas = Config.Bind("2 - Which effects", "Area attacks", true,
                "The burst an area attack (atgeir sweep, bomb, ground slam, staff blast) makes on everything it hits.");
            _players = Config.Bind("2 - Which effects", "Also on players", false,
                "Also fade these effects when they are on a player: when you or a friend is hit, burning, poisoned, and so on. " +
                "Off by default, so you still clearly see when you take damage.");
            _log = Config.Bind("3 - Troubleshooting", "Log faded effects", false,
                "Write the name of every effect this fades to the BepInEx log (LogOutput.log). Use it to check whether an " +
                "effect that still bothers you is covered.");
            _opacity.SettingChanged += OnStrengthChanged;
            _brightness.SettingChanged += OnStrengthChanged;
            _size.SettingChanged += OnStrengthChanged;
            PatchModule(typeof(Plugin));
        }

        private static void OnStrengthChanged(object sender, System.EventArgs e) => _version++;

        private void OnDestroy()
        {
            _opacity.SettingChanged -= OnStrengthChanged;
            _brightness.SettingChanged -= OnStrengthChanged;
            _size.SettingChanged -= OnStrengthChanged;
            Impacts.Clear();
            ImpactKinds.Clear();
            if (_instance == this) _instance = null;
        }

        private static bool Fades => _opacity.Value < 100 || _brightness.Value < 100 || _size.Value < 100;

        // --- Recognising the effect ------------------------------------------------------------------------------

        [HarmonyPatch(typeof(EffectList), nameof(EffectList.Create))]
        [HarmonyPostfix]
        private static void Created(EffectList __instance, GameObject[] __result, Vector3 basePos, Transform baseParent,
            ZDOID gamepadEffectsExclusiveToPlayer)
        {
            if (__result == null || __result.Length == 0 || !Fades) return;
            var kind = Classify(__instance, basePos, baseParent, gamepadEffectsExclusiveToPlayer);
            if (kind == Kind.None) return;
            foreach (var effect in __result) Fade(effect, kind);
        }

        private static Kind Classify(EffectList list, Vector3 pos, Transform parent, ZDOID owner)
        {
            for (var i = Impacts.Count - 1; i >= 0; i--)
                if (ReferenceEquals(Impacts[i], list)) return ImpactKinds[i];

            UpdateLists();
            if (WeaponLists.Contains(list))
            {
                if (!_weapons.Value) return Kind.None;
                // Weapon impacts carry the attacker: a wild creature hits players (or tames), and that is your own
                // damage feedback. Players and tamed animals hit the creatures you fight.
                var attacker = Find(owner);
                return attacker != null && !attacker.IsPlayer() && !attacker.IsTamed() && !_players.Value ? Kind.None : Kind.Weapon;
            }
            if (StatusLists.Contains(list))
            {
                if (!_status.Value) return Kind.None;
                var carrier = parent != null ? parent.GetComponentInParent<Character>() : Find(owner) ?? Nearest(pos);
                return carrier != null && carrier.IsPlayer() && !_players.Value ? Kind.None : Kind.Status;
            }
            // A creature's own hit effects are created attached to it.
            if (parent != null && _creatureHits.Value)
            {
                var character = parent.GetComponent<Character>();
                if (character != null && (ReferenceEquals(list, character.m_hitEffects) ||
                                          ReferenceEquals(list, character.m_critHitEffects) ||
                                          ReferenceEquals(list, character.m_backstabHitEffects)))
                    return character.IsPlayer() && !_players.Value ? Kind.None : Kind.CreatureHit;
            }
            return Kind.None;
        }

        private static Character Find(ZDOID id)
        {
            if (id.IsNone() || ZNetScene.instance == null) return null;
            var go = ZNetScene.instance.FindInstance(id);
            return go != null ? go.GetComponent<Character>() : null;
        }

        // Burning and poison ticks are created at the creature's feet without naming it.
        private static Character Nearest(Vector3 pos)
        {
            foreach (var c in Character.GetAllCharacters())
                if (c != null && (c.transform.position - pos).sqrMagnitude < 0.01f) return c;
            return null;
        }

        private static void UpdateLists()
        {
            var db = ObjectDB.instance;
            if (db == null || db == _listsOf) return;
            _listsOf = db;
            StatusLists.Clear();
            WeaponLists.Clear();
            foreach (var se in db.m_StatusEffects)
            {
                if (se == null) continue;
                Add(StatusLists, se.m_startEffects);
                if (se is SE_Stats stats) Add(StatusLists, stats.m_tickEffect);
                else if (se is SE_Burning burning) Add(StatusLists, burning.m_tickEffect);
                else if (se is SE_Poison poison) Add(StatusLists, poison.m_tickEffect);
            }
            foreach (var item in db.m_items)
            {
                var shared = item != null ? item.GetComponent<ItemDrop>()?.m_itemData?.m_shared : null;
                if (shared == null) continue;
                Add(WeaponLists, shared.m_hitEffect);
                Add(WeaponLists, shared.m_attack?.m_hitEffect);
                Add(WeaponLists, shared.m_secondaryAttack?.m_hitEffect);
            }
        }

        private static void Add(HashSet<EffectList> set, EffectList list)
        {
            if (list?.m_effectPrefabs != null && list.m_effectPrefabs.Length > 0) set.Add(list);
        }

        // Projectile and area-attack impacts are made by the hitting object's own list: mark it while it hits.
        [HarmonyPatch(typeof(Projectile), "OnHit")]
        [HarmonyPrefix]
        private static void ProjectileHit(Projectile __instance, out int __state) =>
            __state = Arm(_projectiles.Value ? __instance.m_hitEffects : null, Kind.Projectile);

        [HarmonyPatch(typeof(Projectile), "OnHit")]
        [HarmonyFinalizer]
        private static void ProjectileHitDone(int __state) => Disarm(__state);

        [HarmonyPatch(typeof(Aoe), "OnHit")]
        [HarmonyPrefix]
        private static void AreaHit(Aoe __instance, out int __state) =>
            __state = Arm(_areas.Value ? __instance.m_hitEffects : null, Kind.Area);

        [HarmonyPatch(typeof(Aoe), "OnHit")]
        [HarmonyFinalizer]
        private static void AreaHitDone(int __state) => Disarm(__state);

        private static int Arm(EffectList list, Kind kind)
        {
            var depth = Impacts.Count;
            if (list != null && Fades)
            {
                Impacts.Add(list);
                ImpactKinds.Add(kind);
            }
            return depth;
        }

        private static void Disarm(int depth)
        {
            if (Impacts.Count <= depth) return;
            Impacts.RemoveRange(depth, Impacts.Count - depth);
            ImpactKinds.RemoveRange(depth, ImpactKinds.Count - depth);
        }

        // Effects from other players' games arrive as network objects.
        [HarmonyPatch(typeof(ZNetScene), "CreateObject", typeof(ZDO))]
        [HarmonyPostfix]
        private static void NetworkObject(ZDO zdo, GameObject __result)
        {
            if (__result == null || zdo == null || !Fades) return;
            UpdateNetworkEffects();
            if (!NetworkEffects.TryGetValue(zdo.GetPrefab(), out var kinds)) return;
            var kind = kinds & Enabled();
            if (kind != Kind.None) Fade(__result, kind);
        }

        private static Kind Enabled() =>
            (_status.Value ? Kind.Status : 0) | (_creatureHits.Value ? Kind.CreatureHit : 0) | (_weapons.Value ? Kind.Weapon : 0) |
            (_projectiles.Value ? Kind.Projectile : 0) | (_areas.Value ? Kind.Area : 0);

        private static void UpdateNetworkEffects()
        {
            var scene = ZNetScene.instance;
            if (scene == null || scene == _networkOf || ObjectDB.instance == null) return;
            _networkOf = scene;
            NetworkEffects.Clear();
            foreach (var prefab in scene.m_prefabs)
            {
                if (prefab == null) continue;
                var character = prefab.GetComponent<Character>();
                if (character != null)
                {
                    Note(character.m_hitEffects, Kind.CreatureHit);
                    Note(character.m_critHitEffects, Kind.CreatureHit);
                    Note(character.m_backstabHitEffects, Kind.CreatureHit);
                }
                var projectile = prefab.GetComponent<Projectile>();
                if (projectile != null) Note(projectile.m_hitEffects, Kind.Projectile);
                var aoe = prefab.GetComponent<Aoe>();
                if (aoe != null) Note(aoe.m_hitEffects, Kind.Area);
            }
            UpdateLists();
            foreach (var list in WeaponLists) Note(list, Kind.Weapon);
            foreach (var list in StatusLists) Note(list, Kind.Status);
        }

        // Only templates that are network objects can arrive this way.
        private static void Note(EffectList list, Kind kind)
        {
            if (list?.m_effectPrefabs == null) return;
            foreach (var data in list.m_effectPrefabs)
            {
                var prefab = data?.m_prefab;
                if (prefab == null || prefab.GetComponent<ZNetView>() == null) continue;
                var hash = prefab.name.GetStableHashCode();
                NetworkEffects.TryGetValue(hash, out var known);
                NetworkEffects[hash] = known | kind;
            }
        }

        // --- Fading ----------------------------------------------------------------------------------------------

        private static void Fade(GameObject effect, Kind kind)
        {
            if (effect == null) return;
            effect.GetComponentsInChildren(true, Systems);
            if (Systems.Count == 0) return;
            var key = effect.name;
            if (!Cache.TryGetValue(key, out var faded) || faded.Version != _version || faded.Count != Systems.Count)
                Cache[key] = faded = Work(Systems);
            var color = _opacity.Value < 100 || _brightness.Value < 100;
            var size = _size.Value < 100;
            for (var i = 0; i < Systems.Count; i++)
            {
                var main = Systems[i].main;
                if (color) main.startColor = faded.Colors[i];
                if (!size) continue;
                if (main.startSize3D)
                {
                    main.startSizeX = faded.SizeX[i];
                    main.startSizeY = faded.SizeY[i];
                    main.startSizeZ = faded.SizeZ[i];
                }
                else main.startSize = faded.Size[i];
            }
            Systems.Clear();
            if (_log.Value) _instance?.Logger.LogInfo($"Faded {key} ({kind})");
        }

        // The faded start values of each particle system of this effect, from a fresh copy (the template's values).
        private static Faded Work(List<ParticleSystem> systems)
        {
            var alpha = _opacity.Value / 100f;
            var light = _brightness.Value / 100f;
            var scale = _size.Value / 100f;
            var n = systems.Count;
            var f = new Faded
            {
                Version = _version, Count = n, Colors = new ParticleSystem.MinMaxGradient[n],
                Size = new ParticleSystem.MinMaxCurve[n], SizeX = new ParticleSystem.MinMaxCurve[n],
                SizeY = new ParticleSystem.MinMaxCurve[n], SizeZ = new ParticleSystem.MinMaxCurve[n],
            };
            for (var i = 0; i < n; i++)
            {
                var main = systems[i].main;
                f.Colors[i] = Dim(main.startColor, alpha, light);
                f.Size[i] = Scale(main.startSize, scale);
                f.SizeX[i] = Scale(main.startSizeX, scale);
                f.SizeY[i] = Scale(main.startSizeY, scale);
                f.SizeZ[i] = Scale(main.startSizeZ, scale);
            }
            return f;
        }

        private static ParticleSystem.MinMaxGradient Dim(ParticleSystem.MinMaxGradient g, float alpha, float light)
        {
            switch (g.mode)
            {
                case ParticleSystemGradientMode.Color:
                    return new ParticleSystem.MinMaxGradient(Dim(g.color, alpha, light));
                case ParticleSystemGradientMode.TwoColors:
                    return new ParticleSystem.MinMaxGradient(Dim(g.colorMin, alpha, light), Dim(g.colorMax, alpha, light));
                case ParticleSystemGradientMode.TwoGradients:
                    return new ParticleSystem.MinMaxGradient(Dim(g.gradientMin, alpha, light), Dim(g.gradientMax, alpha, light));
                case ParticleSystemGradientMode.Gradient:
                case ParticleSystemGradientMode.RandomColor:
                    return new ParticleSystem.MinMaxGradient(Dim(g.gradient, alpha, light)) { mode = g.mode };
                default:
                    return g;
            }
        }

        private static Color Dim(Color c, float alpha, float light) => new Color(c.r * light, c.g * light, c.b * light, c.a * alpha);

        private static Gradient Dim(Gradient source, float alpha, float light)
        {
            if (source == null) return null;
            var colors = source.colorKeys;
            var alphas = source.alphaKeys;
            for (var i = 0; i < colors.Length; i++)
            {
                var c = colors[i].color;
                colors[i].color = new Color(c.r * light, c.g * light, c.b * light, c.a);
            }
            for (var i = 0; i < alphas.Length; i++) alphas[i].alpha *= alpha;
            var result = new Gradient { mode = source.mode };
            result.SetKeys(colors, alphas);
            return result;
        }

        private static ParticleSystem.MinMaxCurve Scale(ParticleSystem.MinMaxCurve curve, float factor)
        {
            curve.constantMin *= factor;
            curve.constantMax *= factor;
            curve.curveMultiplier *= factor;
            return curve;
        }
    }
}
