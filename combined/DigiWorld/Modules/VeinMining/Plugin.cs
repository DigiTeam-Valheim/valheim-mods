// Hold a key (default Left Alt) while mining an ore deposit with a pickaxe: when the piece you are mining breaks, the
// rest of the deposit breaks with it. Every extra piece is broken by an ordinary hit through the game's own damage
// code, so mods that change rock drops (Star Level System, Epic Loot, ...) work exactly as when mining by hand; this
// mod changes no game code. You get Pickaxe XP for every piece as if you mined it yourself; wearing down the pickaxe
// for the whole vein is optional. Only the player who mines needs the mod.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace VeinMining
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.veinmining";
        public const string Name = "Digi's Vein Mining";
        public const string Version = "1.0.1";

        public enum Trigger { WhenFirstPieceBreaks, OnFirstHit }
        public enum Rocks { OreDepositsOnly, AllRocks }

        private static readonly string[] ControllerButtons =
        {
            "None", "JoyLStick", "JoyRStick", "JoyLBumper", "JoyRBumper", "JoyLTrigger", "JoyRTrigger",
            "JoyDPadUp", "JoyDPadDown", "JoyDPadLeft", "JoyDPadRight", "JoyButtonX", "JoyButtonY",
        };

        private static ConfigEntry<bool> _enabled, _alwaysOn, _xpWholeVein, _durabilityWholeVein, _dropsNearYou, _effectsEveryPiece, _showMessage;
        private static ConfigEntry<KeyCode> _key;
        private static ConfigEntry<string> _controllerButton, _extraOres;
        private static ConfigEntry<Trigger> _trigger;
        private static ConfigEntry<Rocks> _rocks;
        private static ConfigEntry<int> _maxPieces;
        private static ConfigEntry<float> _maxDistance, _xpMultiplier;


        private void Awake()
        {
            _enabled = Config.Bind("1 - Vein mining", "Enabled", true, "Turn vein mining on or off.");
            _key = Config.Bind("1 - Vein mining", "Key", KeyCode.LeftAlt, "Hold this key while mining with a pickaxe to mine the whole deposit.");
            _controllerButton = Config.Bind("1 - Vein mining", "Controller button", "JoyLStick",
                new ConfigDescription("Hold this controller button while mining to mine the whole deposit (JoyLStick = press the left stick). None = keyboard only.",
                    new AcceptableValueList<string>(ControllerButtons)));
            _alwaysOn = Config.Bind("1 - Vein mining", "Always on", false, "Mine whole deposits without holding the key.");
            _trigger = Config.Bind("1 - Vein mining", "When the deposit breaks", Trigger.WhenFirstPieceBreaks,
                "WhenFirstPieceBreaks: mine one piece as usual, and when it breaks the rest of the deposit breaks with it. " +
                "OnFirstHit: the whole deposit breaks on your first hit.");
            _rocks = Config.Bind("1 - Vein mining", "Works on", Rocks.OreDepositsOnly,
                "OreDepositsOnly: copper, tin, iron, silver, obsidian, black marble and other ore deposits. " +
                "AllRocks: every rock you can mine with a pickaxe, big stones too.");
            _extraOres = Config.Bind("1 - Vein mining", "Also counts as ore", "Obsidian, SoftTissue, BlackMarble",
                "Item names (comma separated) that make a rock an ore deposit. Items with \"Ore\" or \"Scrap\" in their name always count.");
            _maxPieces = Config.Bind("1 - Vein mining", "Max pieces", 200,
                new ConfigDescription("Most extra pieces that break at once.", new AcceptableValueRange<int>(1, 1000)));
            _maxDistance = Config.Bind("1 - Vein mining", "Max distance (m)", 30f,
                new ConfigDescription("Only pieces up to this many meters from the piece you hit break with it.", new AcceptableValueRange<float>(1f, 100f)));

            _xpWholeVein = Config.Bind("2 - Skill and durability", "XP for the whole deposit", true,
                "Get Pickaxe XP for every piece, as if you had mined each one yourself. Off = XP only for the hits you made.");
            _xpMultiplier = Config.Bind("2 - Skill and durability", "XP multiplier", 1f,
                new ConfigDescription("Multiplies the XP for the extra pieces: 1 = the same as mining them by hand.", new AcceptableValueRange<float>(0f, 5f)));
            _durabilityWholeVein = Config.Bind("2 - Skill and durability", "Durability for the whole deposit", false,
                "Your pickaxe wears down as if you had mined every piece yourself. Off = only the hits you made wear it down.");

            _dropsNearYou = Config.Bind("3 - Drops and effects", "Drops near you", true,
                "Everything the deposit drops lands at your feet instead of all over the deposit. Works on deposits your game " +
                "looks after: when you play alone or host, or when you were the first player near the deposit.");
            _effectsEveryPiece = Config.Bind("3 - Drops and effects", "Hit effects for every piece", false,
                "Hit sounds and damage numbers for every extra piece. Off = only the break effects, quieter and smoother " +
                "(on deposits your game looks after, as above).");
            _showMessage = Config.Bind("3 - Drops and effects", "Show message", true, "Show \"Vein mined: 23 pieces\" in the top left corner.");
            _extraOres.SettingChanged += (_, __) => OreCache.Clear();
            _rocks.SettingChanged += (_, __) => OreCache.Clear();

            PatchModuleNamespace();
        }

        // --- One mineable piece of a deposit ---------------------------------------------------------------------

        private sealed class Area
        {
            public int Index;
            public float Health;
            public Vector3 Center;
        }

        // What the prefix saw before the player's own hit landed (health, damage per swing), for the postfix.
        private sealed class VeinStart
        {
            internal List<Area> Areas;
            internal int HitIndex;
            internal float PerSwing, HitHealth;
        }

        private static readonly AccessTools.FieldRef<MineRock5, ZNetView> Mr5View = AccessTools.FieldRefAccess<MineRock5, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<MineRock, ZNetView> MrView = AccessTools.FieldRefAccess<MineRock, ZNetView>("m_nview");
        private static readonly FieldInfo Mr5Areas = AccessTools.Field(typeof(MineRock5), "m_hitAreas");
        private static readonly Type Mr5AreaType = AccessTools.Inner(typeof(MineRock5), "HitArea");
        private static readonly FieldInfo Mr5AreaHealth = AccessTools.Field(Mr5AreaType, "m_health");
        private static readonly FieldInfo Mr5AreaCollider = AccessTools.Field(Mr5AreaType, "m_collider");
        private static readonly MethodInfo Mr5LoadHealth = AccessTools.Method(typeof(MineRock5), "LoadHealth");
        private static readonly MethodInfo Mr5CheckSupport = AccessTools.Method(typeof(MineRock5), "CheckSupport");
        private static readonly AccessTools.FieldRef<MineRock, Collider[]> MrAreas = AccessTools.FieldRefAccess<MineRock, Collider[]>("m_hitAreas");

        private static List<Area> ReadAreas(MineRock5 rock, Collider hitCollider, out int hitIndex)
        {
            hitIndex = -1;
            Mr5LoadHealth.Invoke(rock, null);
            var list = Mr5Areas.GetValue(rock) as IList;
            if (list == null) return null;
            var areas = new List<Area>(list.Count);
            for (var i = 0; i < list.Count; i++)
            {
                var collider = (Collider)Mr5AreaCollider.GetValue(list[i]);
                if (collider == hitCollider) hitIndex = i;
                areas.Add(new Area
                {
                    Index = i,
                    Health = (float)Mr5AreaHealth.GetValue(list[i]),
                    Center = collider != null ? collider.bounds.center : rock.transform.position,
                });
            }
            return areas;
        }

        private static List<Area> ReadAreas(MineRock rock, Collider hitCollider, out int hitIndex)
        {
            hitIndex = -1;
            var colliders = MrAreas(rock);
            var zdo = MrView(rock)?.GetZDO();
            if (colliders == null || zdo == null) return null;
            var full = rock.GetHealth();
            var areas = new List<Area>(colliders.Length);
            for (var i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] == hitCollider) hitIndex = i;
                areas.Add(new Area
                {
                    Index = i,
                    Health = zdo.GetFloat("Health" + i, full),
                    Center = colliders[i] != null ? colliders[i].bounds.center : rock.transform.position,
                });
            }
            return areas;
        }

        // --- Deciding whether this hit starts a vein ---------------------------------------------------------------

        private static bool KeyHeld()
        {
            if (_alwaysOn.Value) return true;
            if (UnityInput.Current.GetKey(_key.Value)) return true;
            var button = _controllerButton.Value;
            return !string.IsNullOrEmpty(button) && button != "None" && ZInput.GetButton(button);
        }

        private static readonly Dictionary<string, bool> OreCache = new Dictionary<string, bool>();

        private static bool WorksOn(GameObject rock, DropTable drops)
        {
            if (_rocks.Value == Rocks.AllRocks) return true;
            var name = Utils.GetPrefabName(rock);
            if (OreCache.TryGetValue(name, out var ore)) return ore;
            var extra = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in _extraOres.Value.Split(',')) if (part.Trim().Length > 0) extra.Add(part.Trim());
            if (drops?.m_drops != null)
                foreach (var drop in drops.m_drops)
                {
                    if (drop.m_item == null) continue;
                    var item = drop.m_item.name;
                    if (item.Contains("Ore") || item.Contains("Scrap") || extra.Contains(item)) ore = true;
                }
            OreCache[name] = ore;
            return ore;
        }

        private static VeinStart Begin(HitData hit, HitData.DamageModifiers modifiers, int minToolTier, List<Area> areas, int hitIndex)
        {
            if (areas == null || hitIndex < 0) return null;
            var hitHealth = areas[hitIndex].Health;
            if (hitHealth <= 0f) return null;
            var test = hit.Clone();
            test.ApplyResistance(modifiers, out _);
            var perSwing = test.GetTotalDamage();
            if (perSwing <= 0f || !test.CheckToolTier(minToolTier)) return null;
            // Normal mode: only the hit that breaks the piece you are mining sets off the rest.
            if (_trigger.Value == Trigger.WhenFirstPieceBreaks && hitHealth - perSwing > 0f) return null;
            var start = new VeinStart { Areas = areas, HitIndex = hitIndex, PerSwing = perSwing, HitHealth = hitHealth };
            _collectOrigin = areas[hitIndex].Center;
            _collectRadius = _maxDistance.Value + 3f;
            _collectUntil = Time.time + 3f;
            return start;
        }

        private static bool Eligible(HitData hit, ZNetView view)
        {
            if (!_enabled.Value || hit == null || view == null || !view.IsValid()) return false;
            if (hit.m_hitCollider == null || hit.m_radius > 0f || hit.m_skill != Skills.SkillType.Pickaxes) return false;
            var player = Player.m_localPlayer;
            return player != null && hit.GetAttacker() == player && KeyHeld();
        }

        // --- Breaking the rest of the deposit ----------------------------------------------------------------------

        private static bool _veinHits; // our own extra hits are being handled right now (on this game)

        private static void Finish(MonoBehaviour rock, ZNetView view, string rpc, HitData hit, VeinStart start)
        {
            if (!view.IsValid()) return; // the hit broke the last piece
            var origin = start.Areas[start.HitIndex].Center;
            var maxDistance = _maxDistance.Value;
            var targets = new List<Area>();
            foreach (var area in start.Areas)
            {
                if (area.Health <= 0f) continue;
                if (area.Index == start.HitIndex && _trigger.Value == Trigger.WhenFirstPieceBreaks) continue; // broken by the player's hit
                if (Vector3.Distance(area.Center, origin) <= maxDistance) targets.Add(area);
            }
            if (targets.Count == 0)
            {
                _collectUntil = 0f;
                return;
            }
            targets.Sort((a, b) => Vector3.Distance(a.Center, origin).CompareTo(Vector3.Distance(b.Center, origin)));
            var limit = _maxPieces.Value + (_trigger.Value == Trigger.OnFirstHit ? 1 : 0);
            if (targets.Count > limit) targets.RemoveRange(limit, targets.Count - limit);

            var swings = 0;
            var broken = 0;
            _veinHits = view.IsOwner();
            try
            {
                foreach (var area in targets)
                {
                    if (!view.IsValid()) break;
                    // An ordinary hit from the player, strong enough to break the piece in one go.
                    var veinHit = hit.Clone();
                    veinHit.m_point = area.Center;
                    veinHit.m_radius = 0f;
                    veinHit.m_pushForce = 0f;
                    veinHit.m_damage.Modify(area.Health / start.PerSwing * 1.05f + 0.01f);
                    view.InvokeRPC(rpc, veinHit, area.Index);
                    var hits = Mathf.Max(1, Mathf.CeilToInt(area.Health / start.PerSwing - 0.001f));
                    if (area.Index == start.HitIndex) hits -= 1; // the player's own hit already counted
                    swings += hits;
                    if (area.Index != start.HitIndex) broken++;
                }
            }
            finally
            {
                _veinHits = false;
            }
            // Support checks were skipped while the pieces broke; do the one that matters now.
            if (rock is MineRock5 && view.IsValid() && view.IsOwner()) Mr5CheckSupport.Invoke(rock, null);

            var player = Player.m_localPlayer;
            if (player == null) return;
            if (_xpWholeVein.Value && _xpMultiplier.Value > 0f && swings > 0)
            {
                // One raise per swing, as when mining by hand (a single big raise would lose XP at a level up).
                var amount = hit.m_skillRaiseAmount * _xpMultiplier.Value;
                for (var i = 0; i < Mathf.Min(swings, 20000); i++) player.RaiseSkill(hit.m_skill, amount);
            }
            if (_durabilityWholeVein.Value && swings > 0)
            {
                var tool = player.GetCurrentWeapon();
                if (tool != null && tool.m_shared.m_useDurability)
                    tool.m_durability = Mathf.Max(0f, tool.m_durability - tool.m_shared.m_useDurabilityDrain * Game.m_durabilityRate * swings);
            }
            if (_showMessage.Value && broken > 0)
                player.Message(MessageHud.MessageType.TopLeft, $"Vein mined: {broken + 1} pieces");
        }

        // --- Drops near you -----------------------------------------------------------------------------------------

        // Drops created on this game during a vein (and for a few seconds after, for mods that spawn drops a bit later)
        // are moved to the player. Which items drop is left entirely to the game and other mods.
        private static float _collectUntil;
        private static Vector3 _collectOrigin;
        private static float _collectRadius;

        [HarmonyPatch(typeof(ItemDrop), "Awake")]
        private static class GatherDrops
        {
            private static void Postfix(ItemDrop __instance)
            {
                if (_collectUntil <= 0f || Time.time > _collectUntil || !_dropsNearYou.Value) return;
                var player = Player.m_localPlayer;
                if (player == null || (__instance.transform.position - _collectOrigin).sqrMagnitude > _collectRadius * _collectRadius) return;
                var view = __instance.GetComponent<ZNetView>();
                if (view == null || !view.IsValid() || !view.IsOwner()) return;
                var offset = UnityEngine.Random.insideUnitCircle * 0.5f;
                var position = player.transform.position + new Vector3(offset.x, 0.7f, offset.y);
                __instance.transform.position = position;
                var body = __instance.GetComponent<Rigidbody>();
                if (body == null) return;
                body.position = position;
                body.linearVelocity = Vector3.zero;
            }
        }

        // --- Patches -------------------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(MineRock5), nameof(MineRock5.Damage))]
        private static class MineRock5Damage
        {
            private static void Prefix(MineRock5 __instance, HitData hit, out VeinStart __state)
            {
                __state = null;
                var view = Mr5View(__instance);
                if (!Eligible(hit, view) || !WorksOn(__instance.gameObject, __instance.m_dropItems)) return;
                var areas = ReadAreas(__instance, hit.m_hitCollider, out var hitIndex);
                __state = Begin(hit, __instance.m_damageModifiers, __instance.m_minToolTier, areas, hitIndex);
            }

            private static void Postfix(MineRock5 __instance, HitData hit, VeinStart __state)
            {
                if (__state != null) Finish(__instance, Mr5View(__instance), "RPC_Damage", hit, __state);
            }
        }

        [HarmonyPatch(typeof(MineRock), nameof(MineRock.Damage))]
        private static class MineRockDamage
        {
            private static void Prefix(MineRock __instance, HitData hit, out VeinStart __state)
            {
                __state = null;
                var view = MrView(__instance);
                if (!Eligible(hit, view) || !WorksOn(__instance.gameObject, __instance.m_dropItems)) return;
                var areas = ReadAreas(__instance, hit.m_hitCollider, out var hitIndex);
                __state = Begin(hit, __instance.m_damageModifiers, __instance.m_minToolTier, areas, hitIndex);
            }

            private static void Postfix(MineRock __instance, HitData hit, VeinStart __state)
            {
                if (__state != null) Finish(__instance, MrView(__instance), "Hit", hit, __state);
            }
        }

        // While our extra pieces break on this game: no hit sound/sparks per piece (the break effect still plays) ...
        private static readonly EffectList NoEffect = new EffectList();

        [HarmonyPatch(typeof(MineRock5), "DamageArea")]
        private static class QuietMineRock5
        {
            private static void Prefix(MineRock5 __instance, out EffectList __state)
            {
                __state = null;
                if (!_veinHits || _effectsEveryPiece.Value) return;
                __state = __instance.m_hitEffect;
                __instance.m_hitEffect = NoEffect;
            }

            private static void Postfix(MineRock5 __instance, EffectList __state)
            {
                if (__state != null) __instance.m_hitEffect = __state;
            }
        }

        [HarmonyPatch(typeof(MineRock), "RPC_Hit")]
        private static class QuietMineRock
        {
            private static void Prefix(MineRock __instance, out EffectList __state)
            {
                __state = null;
                if (!_veinHits || _effectsEveryPiece.Value) return;
                __state = __instance.m_hitEffect;
                __instance.m_hitEffect = NoEffect;
            }

            private static void Postfix(MineRock __instance, EffectList __state)
            {
                if (__state != null) __instance.m_hitEffect = __state;
            }
        }

        // ... no damage numbers per piece ...
        [HarmonyPatch]
        private static class QuietDamageText
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var method in typeof(DamageText).GetMethods(BindingFlags.Instance | BindingFlags.Public))
                    if (method.Name == nameof(DamageText.ShowText)) yield return method;
            }

            private static bool Prefix() => !_veinHits || _effectsEveryPiece.Value;
        }

        // ... and the "does this piece still rest on something" check runs once at the end instead of after every piece.
        [HarmonyPatch(typeof(MineRock5), "CheckSupport")]
        private static class OneSupportCheck
        {
            private static bool Prefix() => !_veinHits;
        }
    }
}
