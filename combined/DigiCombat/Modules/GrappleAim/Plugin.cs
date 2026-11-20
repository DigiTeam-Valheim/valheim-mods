// While the grappling hook is in your hands, the crosshair turns red when the hook would not catch where you aim:
// too far away, a surface it can't hold on to, or further than its rope reaches (60 m: the rope lets go at once).
// Mods that change the shot are followed too: Better Archery (left-hand start, arrow velocity and aim direction while
// arrows are equipped), Epic Loot's enchantments (Grapple Flight Time: a longer flight; Projectile Speed: a faster
// shot; both also lengthen the rope), and any other speed change is learned from your real shots.
// Better Archery speeds up bows (the hook counts as one) only while arrows or bolts are equipped; "Hook always fast"
// gives the hook that speed with an empty quiver too, so its reach no longer depends on what is in your quiver. The hook's flight is followed the way the game flies it (speed,
// drop, flight time, from where it leaves your hand), with a handful of raycasts per frame and only while the hook
// is equipped. Works for any hook-like weapon whose projectile grapples, including modded ones. The hook's tooltip also
// shows its range.
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace GrappleAim
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.grappleaim";
        public const string Name = "Digi's Grapple Aim";
        public const string Version = "1.3.0";

        private static ConfigEntry<bool> _enabled, _onlyWhenLoaded, _colorInRange, _tooltipRange, _logShots, _noFreeAmmo, _alwaysFast,
            _onePerClick, _pullAtOnce, _keepPulling;
        private static readonly AccessTools.FieldRef<Character, bool> SecondaryHold = AccessTools.FieldRefAccess<Character, bool>("m_secondaryAttackHold");
        private static readonly AccessTools.FieldRef<GrapplingPoint, float> LastDistance = AccessTools.FieldRefAccess<GrapplingPoint, float>("m_lastDist");
        private static readonly AccessTools.FieldRef<GrapplingPoint, float> NotCloserTime = AccessTools.FieldRefAccess<GrapplingPoint, float>("m_breakingTime");
        private static readonly AccessTools.FieldRef<GrapplingPoint, Character> Hooked = AccessTools.FieldRefAccess<GrapplingPoint, Character>("m_character");
        private static bool _hookCaught;
        internal static BepInEx.Logging.ManualLogSource Log;
        private Harmony _harmony;
        private static ConfigEntry<Color> _outOfRangeColor, _inRangeColor;


        private void Awake()
        {
            _enabled = Config.Bind("Crosshair", "Enabled", true, "Turn the crosshair red when the grappling hook would not catch where you aim.");
            _outOfRangeColor = Config.Bind("Crosshair", "Out of range color", new Color(1f, 0.15f, 0.15f, 0.8f),
                "Crosshair color when the hook would miss: too far away, or a surface it can't hold on to.");
            _colorInRange = Config.Bind("Crosshair", "Color when in range", false, "Also give the crosshair its own color when the hook would catch. Off = the normal crosshair.");
            _inRangeColor = Config.Bind("Crosshair", "In range color", new Color(0.4f, 1f, 0.4f, 0.8f), "Crosshair color when the hook would catch (if turned on above).");
            _onlyWhenLoaded = Config.Bind("Crosshair", "Only when loaded", false, "Only color the crosshair while the hook is loaded and ready to fire.");
            _tooltipRange = Config.Bind("Tooltip", "Show range", true, "Show the hook's range (how far it reaches when aimed straight ahead) in its tooltip.");
            _noFreeAmmo = Config.Bind("Better Archery", "No free ammo from the hook", true,
                "Better Archery counts the grappling hook as a bow, so its retrievable arrows drop your equipped arrow or bolt where the hook lands, " +
                "although the hook used none (free ammo). On: Better Archery leaves hook shots alone; arrows and bolts work as before.");
            _alwaysFast = Config.Bind("Better Archery", "Hook always fast", true,
                "Better Archery makes bows (the grappling hook counts as one) shoot at its arrow velocity, but only while arrows or bolts " +
                "are equipped: with an empty quiver the hook flew much slower and less far. On: the hook always flies at that speed. " +
                "Off: as Better Archery does it.");
            _onePerClick = Config.Bind("Middle click", "One shot per click", true,
                "The game fires the hook's middle-click shot again and again while the button is held (it needs no reload), so one " +
                "click often shot twice and cost twice the stamina. On: one shot per press, however long you hold it.");
            _pullAtOnce = Config.Bind("Middle click", "Pull at once", true,
                "A middle-click hook hangs you on the rope, and only pulls you in when you jump while in the air (from the ground: " +
                "jump twice). On: it pulls you in as soon as it catches, like the left-click shot.");
            _keepPulling = Config.Bind("Pull", "Keep pulling", true,
                "The game stops the pull (you hang on the rope and must jump to go on) once it has gone half a second without " +
                "bringing you closer, but it counts every frame in which the physics did not move you, even in full flight, and " +
                "never starts that count again: on longer flights it stopped you halfway. On: the count starts again whenever you " +
                "get closer, so it only stops when you are really stuck.");
            _logShots = Config.Bind("Testing", "Log shots", false,
                "Write every hook shot to the log: what the crosshair said, and where the hook really caught (or that it missed).");
            Log = Logger;
            // Own patch ID per load, so a hot reload (ScriptEngine) never removes the new copy's patches with the old one's.
            _harmony = PatchModule(typeof(Plugin));
            var retrieve = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("ishid4.mods.betterarchery")
                ? AccessTools.TypeByName("BetterArchery.RetrieveArrows") : null; // only when Better Archery is installed
            var setup = retrieve != null ? AccessTools.Method(retrieve, "Prefix") : null;
            if (setup != null) _harmony.Patch(setup, prefix: new HarmonyMethod(typeof(Plugin), nameof(NoArrowForHooks)));
        }

        // Lets the mod be reloaded while the game runs (ScriptEngine).
        private void OnDestroy() => _harmony?.UnpatchSelf();

        private static int _mask;
        private static readonly RaycastHit[] Hits = new RaycastHit[32];

        // Same layers the game's projectiles collide with.
        private static int Mask => _mask != 0 ? _mask : _mask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece",
            "piece_nonsolid", "terrain", "character", "character_net", "character_ghost", "hitbox", "character_noenv", "vehicle");

        private static readonly Color VanillaCrosshair = new Color(1f, 1f, 1f, 0.5f);
        private static bool _colored;

        // Every frame after the game's HUD (which may skip its crosshair update, e.g. while a hook pulls you).
        private void LateUpdate()
        {
            var hud = Hud.instance;
            var player = Player.m_localPlayer;
            if (hud == null || hud.m_crosshair == null) return;
            var attack = player?.GetCurrentWeapon()?.m_shared?.m_attack;
            var projectile = _enabled.Value && player != null ? HookProjectile(attack) : null;
            if (projectile == null || (_onlyWhenLoaded.Value && attack.m_requiresReload && !player.IsWeaponLoaded()))
            {
                if (_colored) hud.m_crosshair.color = VanillaCrosshair;
                _colored = false;
                return;
            }
            RefreshFactors(player, false);
            _predicted = WouldCatch(player, attack, projectile, out _predictedDistance);
            if (!_predicted) hud.m_crosshair.color = _outOfRangeColor.Value;
            else if (_colorInRange.Value) hud.m_crosshair.color = _inRangeColor.Value;
            else if (_colored && hud.m_crosshair.color == _outOfRangeColor.Value) hud.m_crosshair.color = VanillaCrosshair;
            _colored = !_predicted || _colorInRange.Value;
        }

        // --- Epic Loot ---------------------------------------------------------------------------------------------

        // Epic Loot's public API for an enchantment's total (0.01 = per cent to a share); null without Epic Loot.
        private static System.Reflection.MethodInfo _epicLootValue;
        private static bool _epicLootLooked;
        // The equipped hook's factors, read twice a second (not every frame): flight time and shot speed.
        private static float _flightFactor = 1f, _speedFactor = 1f, _factorsAt = -1f;
        private static ItemDrop.ItemData _factorsItem;

        // An enchantment's share (0.4 = +40%): with a player, everything it has equipped; without, the item's own.
        private static float EpicLoot(Player player, ItemDrop.ItemData item, string effect)
        {
            if (!_epicLootLooked)
            {
                _epicLootLooked = true;
                if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("randyknapp.mods.epicloot"))
                    _epicLootValue = AccessTools.Method("EpicLoot.API:GetTotalActiveMagicEffectValue",
                        new[] { typeof(Player), typeof(ItemDrop.ItemData), typeof(string), typeof(float) });
            }
            if (_epicLootValue == null || item == null) return 0f;
            try
            {
                return Mathf.Max(0f, (float)_epicLootValue.Invoke(null, new object[] { player, item, effect, 0.01f }));
            }
            catch (System.Exception e)
            {
                _epicLootValue = null; // a different Epic Loot: the hook is predicted without its enchantments
                Log?.LogWarning("Epic Loot's enchantments could not be read: " + e.Message);
                return 0f;
            }
        }

        // Grapple Flight Time multiplies the hook's flight time, Projectile Speed its speed, and the rope grows by both
        // (Epic Loot's GrapplingHook and ModifyProjectileSpeed).
        private static void Enchanted(Player player, ItemDrop.ItemData item, out float flight, out float speed)
        {
            flight = 1f + EpicLoot(player, item, "ModifyGrappleFlightTime");
            speed = 1f + EpicLoot(player, item, "ModifyProjectileSpeed");
        }

        private static void RefreshFactors(Player player, bool now)
        {
            var item = player.GetCurrentWeapon();
            if (!now && item == _factorsItem && Time.time - _factorsAt < 0.5f) return;
            _factorsItem = item;
            _factorsAt = Time.time;
            Enchanted(player, item, out _flightFactor, out _speedFactor);
        }

        private static bool _predicted;
        private static float _predictedDistance;
        private static Vector3 _simSpawn, _simVelocity;
        private static float _simGravity;

        // The projectile of a weapon whose shot grapples, or null.
        private static Projectile HookProjectile(Attack attack)
        {
            var projectile = attack?.m_attackProjectile != null ? attack.m_attackProjectile.GetComponent<Projectile>() : null;
            return projectile != null && projectile.m_spawnOnHit != null && projectile.m_spawnOnHit.GetComponent<GrapplingPoint>() != null ? projectile : null;
        }

        // Better Archery's retrievable arrows (a prefix on Projectile.Setup): skipped for grappling hook shots. Its result
        // is set to true so the game's own Setup still runs.
        private static bool NoArrowForHooks(ref Projectile __0, ref bool __result)
        {
            if (!_noFreeAmmo.Value || __0 == null || __0.m_spawnOnHit == null || __0.m_spawnOnHit.GetComponent<GrapplingPoint>() == null) return true;
            __result = true;
            return false;
        }

        // "Hook always fast": with an empty quiver, the hook gets Better Archery's arrow velocity too (with ammo Better
        // Archery sets it itself). Before Better Archery's and Epic Loot's own changes, so Epic Loot's speed comes on top.
        [HarmonyPatch(typeof(Attack), "FireProjectileBurst")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        private static void HookSpeed(Attack __instance)
        {
            var character = AttackCharacter(__instance);
            if (!_alwaysFast.Value || character == null || character != Player.m_localPlayer || HookProjectile(__instance) == null) return;
            var player = Player.m_localPlayer;
            if (player.GetAmmoItem() == null && BetterArcheryApplies(player)) __instance.m_projectileVel = _archeryVelocity.Value;
        }

        // --- Tooltip ---------------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip), typeof(ItemDrop.ItemData), typeof(int), typeof(bool), typeof(float), typeof(int), typeof(bool))]
        [HarmonyPostfix]
        private static void Tooltip(ItemDrop.ItemData item, ref string __result)
        {
            if (!_tooltipRange.Value || item?.m_shared == null || __result == null) return;
            var attack = item.m_shared.m_attack;
            var projectile = HookProjectile(attack);
            if (projectile == null) return;
            // The hook in your hands counts everything you wear (Epic Loot); one in a chest or your bag only itself.
            var player = Player.m_localPlayer;
            Enchanted(player != null && player.GetCurrentWeapon() == item ? player : null, item, out var flight, out var speed);
            __result += $"\nRange: <color=orange>{Range(item, attack, projectile, flight, speed):0} m</color>";
        }

        // How far the hook gets in its flight time when fired straight ahead (it drops a little on the way).
        // With Better Archery's arrow improvements (and arrows equipped) a bow-type weapon flies at its arrow velocity.
        private static float Range(ItemDrop.ItemData item, Attack attack, Projectile projectile, float flight, float speedFactor)
        {
            const float step = 0.02f;
            var position = Vector3.zero;
            var speed = attack.m_projectileVel;
            var player = Player.m_localPlayer;
            if (player != null && item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow && (player.GetAmmoItem() != null || _alwaysFast.Value))
            {
                BetterArcheryApplies(player); // reads its settings once
                if (_archeryEnabled != null && _archeryVelocity != null && _archeryEnabled.Value) speed = _archeryVelocity.Value;
            }
            speed *= speedFactor;
            if (player != null && _measuredItem == item.m_shared.m_name && _measuredFactor > 0.2f && _measuredFactor < 5f) speed *= _measuredFactor;
            var velocity = Vector3.forward * speed;
            var time = (projectile.m_ttl > 0f ? projectile.m_ttl : 4f) * flight;
            for (var t = 0f; t < time; t += step)
            {
                velocity += Vector3.down * (projectile.m_gravity * step);
                if (projectile.m_drag > 0f) velocity -= velocity.normalized * (velocity.sqrMagnitude * projectile.m_drag * step);
                position += velocity * step;
            }
            var rope = projectile.m_spawnOnHit != null ? projectile.m_spawnOnHit.GetComponent<GrapplingPoint>() : null;
            var ropeLength = rope != null ? rope.m_maxLength * flight * speedFactor : 0f;
            return ropeLength > 0f ? Mathf.Min(position.magnitude, ropeLength) : position.magnitude; // the rope lets go beyond its length
        }

        // --- Crosshair -------------------------------------------------------------------------------------------

        // Flies the hook exactly the way Projectile.FixedUpdate does: physics steps of Time.fixedDeltaTime, the same
        // flight-time countdown, and the same collision rays (the first one from your body when the hook asks for it,
        // later ones from half a step back). hitDistance: how far from the hand it catches.
        private static bool WouldCatch(Player player, Attack attack, Projectile projectile, out float hitDistance)
        {
            hitDistance = 0f;
            Launch(player, attack, true, out var spawn, out var velocity);
            _simSpawn = spawn;
            _simVelocity = velocity;
            _simGravity = projectile.m_gravity;

            var dt = Time.fixedDeltaTime > 0f ? Time.fixedDeltaTime : 0.02f;
            var ttl = projectile.m_ttl * _flightFactor;
            var position = spawn;
            var haveStart = projectile.m_doOwnerRaytest;
            var startPoint = player.GetCenterPoint();
            startPoint.y = spawn.y;
            for (var i = 0; i < 2000; i++)
            {
                var from = haveStart ? startPoint : position;
                velocity += Vector3.down * (projectile.m_gravity * dt);
                if (projectile.m_drag > 0f) velocity -= velocity.normalized * (velocity.sqrMagnitude * projectile.m_drag * dt);
                position += velocity * dt;
                var segment = position - from;
                if (!haveStart) from -= segment.normalized * (segment.magnitude * 0.5f);
                haveStart = false;
                var length = segment.magnitude * 1.5f;
                if (length > 0.0001f)
                {
                    var count = Physics.RaycastNonAlloc(from, segment.normalized, Hits, length, Mask);
                    var nearest = -1;
                    for (var h = 0; h < count; h++)
                    {
                        if (Hits[h].collider == null || !Hittable(player, projectile, Hits[h].collider)) continue;
                        if (nearest < 0 || Hits[h].distance < Hits[nearest].distance) nearest = h;
                    }
                    if (nearest >= 0)
                    {
                        hitDistance = Vector3.Distance(spawn, Hits[nearest].point);
                        // The rope lets go at once when the hook point is further from your feet than its max length
                        // (60 m, longer with Epic Loot's enchantments) - which happens when other mods make the hook fly
                        // further than vanilla.
                        var rope = projectile.m_spawnOnHit.GetComponent<GrapplingPoint>();
                        var ropeLength = rope != null ? rope.m_maxLength * _flightFactor * _speedFactor : 0f;
                        if (ropeLength > 0f && Vector3.Distance(player.transform.position, Hits[nearest].point) > ropeLength)
                            return false;
                        return Catches(projectile, Hits[nearest].collider);
                    }
                }
                if (ttl > 0f)
                {
                    ttl -= dt;
                    if (ttl <= 0f) break;
                }
            }
            return false; // flew its whole flight time without touching anything
        }

        // Where and how fast the hook leaves your hand, like Attack.GetProjectileSpawnPoint and FireProjectileBurst, plus
        // what Better Archery changes for every bow-type weapon (the hook counts as one) when its arrow improvements are
        // on: the shot starts at your left hand, and while you have arrows equipped it flies at its arrow velocity with
        // its aim direction added (not re-normalised, so the speed varies a little with the angle, like the real shot).
        // measured: also apply the speed factor learned from your last real shot (e.g. Epic Loot's projectile speed).
        private static string _measuredItem;
        private static float _measuredFactor = 1f;
        // Where the left hand really was when the last hook left it, relative to you and your aim (x right, y up,
        // z forward): the shooting animation raises the hand, so the resting hand would predict from the wrong spot.
        private static Vector3 _handOffset;
        private static bool _haveHandOffset;

        private static void Launch(Player player, Attack attack, bool measured, out Vector3 spawn, out Vector3 velocity)
        {
            var look = player.GetLookDir();
            var flat = Vector3.ProjectOnPlane(look, Vector3.up);
            var forward = flat.sqrMagnitude > 0.0001f ? flat.normalized : player.transform.forward; // you turn to face your aim when firing
            var right = Vector3.Cross(Vector3.up, forward);
            var origin = player.transform.position;
            if (!string.IsNullOrEmpty(attack.m_attackOriginJoint) && player.GetVisual() != null)
            {
                var joint = Utils.FindChild(player.GetVisual().transform, attack.m_attackOriginJoint);
                if (joint != null) origin = joint.position;
            }
            spawn = origin + Vector3.up * attack.m_attackHeight + forward * attack.m_attackRange + right * attack.m_attackOffset;
            var aim = look;
            var speed = attack.m_projectileVel;
            if (BetterArcheryApplies(player))
            {
                var hand = VisEquipment(player)?.m_leftHand;
                if (measured && _haveHandOffset)
                    spawn = player.transform.position + right * _handOffset.x + Vector3.up * _handOffset.y + forward * _handOffset.z;
                else if (hand != null) spawn = hand.position;
                if (aim.y <= -0.8f) spawn = origin + Vector3.up * attack.m_attackHeight + forward * 0.7f + right * attack.m_attackOffset;
                var ammo = player.GetAmmoItem();
                if (ammo != null || _alwaysFast.Value) speed = _archeryVelocity.Value; // with ammo, or always ("Hook always fast")
                if (ammo != null && ammo.m_shared.m_attack.m_attackProjectile != null && _archeryAim != null) aim += _archeryAim.Value;
            }
            if (attack.m_launchAngle != 0f) aim = Quaternion.AngleAxis(attack.m_launchAngle, Vector3.Cross(Vector3.up, aim)) * aim;
            speed *= _speedFactor; // Epic Loot's Projectile Speed (the learned factor below then only covers other mods)
            var item = player.GetCurrentWeapon();
            if (measured && item != null && _measuredItem == item.m_shared.m_name && _measuredFactor > 0.2f && _measuredFactor < 5f)
                speed *= _measuredFactor;
            velocity = aim * speed;
        }

        private static readonly AccessTools.FieldRef<Humanoid, VisEquipment> VisEquipment = AccessTools.FieldRefAccess<Humanoid, VisEquipment>("m_visEquipment");
        private static readonly AccessTools.FieldRef<Attack, Humanoid> AttackCharacter = AccessTools.FieldRefAccess<Attack, Humanoid>("m_character");
        private static readonly AccessTools.FieldRef<Humanoid, Attack> CurrentAttack = AccessTools.FieldRefAccess<Humanoid, Attack>("m_currentAttack");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> LeftItem = AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_leftItem");
        private static ConfigEntry<bool> _archeryEnabled;
        private static ConfigEntry<float> _archeryVelocity;
        private static ConfigEntry<Vector3> _archeryAim;
        private static bool _archeryLooked;

        private static bool BetterArcheryApplies(Player player)
        {
            if (!_archeryLooked)
            {
                _archeryLooked = true;
                if (BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue("ishid4.mods.betterarchery", out var info) && info.Instance != null)
                {
                    info.Instance.Config.TryGetEntry(new ConfigDefinition("Arrow Improvements", "Enable Arrow Improvements"), out _archeryEnabled);
                    info.Instance.Config.TryGetEntry(new ConfigDefinition("Arrow Improvements", "Set Arrow Velocity"), out _archeryVelocity);
                    info.Instance.Config.TryGetEntry(new ConfigDefinition("Arrow Improvements", "Set Aim Direction"), out _archeryAim);
                }
            }
            if (_archeryEnabled == null || _archeryVelocity == null || !_archeryEnabled.Value) return false;
            var left = LeftItem(player);
            return left != null && left.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow;
        }

        // Projectile.IsValidTarget: the hook flies through you, and through creatures and players it may not hit.
        private static bool Hittable(Player player, Projectile projectile, Collider collider)
        {
            var character = Projectile.FindHitObject(collider)?.GetComponent<Character>();
            if (character == null) return true;
            if (character == player) return projectile.m_hitOwner;
            if (projectile.m_hitFriendly) return true;
            var enemy = BaseAI.IsEnemy(player, character) || (character.GetBaseAI() != null && character.GetBaseAI().IsAggravatable());
            return enemy || player.IsPVPEnabled();
        }

        // --- Shot log (testing) ------------------------------------------------------------------------------------

        private sealed class Shot
        {
            public Projectile Projectile;
            public Vector3 Start;
            public float Time, PredictedDistance;
            public bool Predicted, Done, AtClick, HaveClick;
            public float AtClickDistance;
        }

        private static readonly List<Shot> Shots = new List<Shot>();
        private static bool _predictedAtClick, _haveClick;
        private static float _predictedDistanceAtClick;

        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.StartAttack))]
        [HarmonyPrefix]
        private static void Clicked(Humanoid __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            _predictedAtClick = _predicted;
            _predictedDistanceAtClick = _predictedDistance;
            _haveClick = true;
        }

        [HarmonyPatch(typeof(Projectile), nameof(Projectile.Setup))]
        [HarmonyPostfix]
        private static void Fired(Projectile __instance, Character owner, Vector3 velocity)
        {
            if (owner == null || owner != Player.m_localPlayer || __instance.m_spawnOnHit == null || __instance.m_spawnOnHit.GetComponent<GrapplingPoint>() == null) return;
            var player = (Player)owner;
            var item = player.GetCurrentWeapon();
            var attack = item?.m_shared?.m_attack;
            if (attack != null)
            {
                if (BetterArcheryApplies(player) && player.GetLookDir().y > -0.8f)
                {
                    var look = player.GetLookDir();
                    var flat = Vector3.ProjectOnPlane(look, Vector3.up);
                    var forward = flat.sqrMagnitude > 0.0001f ? flat.normalized : player.transform.forward;
                    var right = Vector3.Cross(Vector3.up, forward);
                    var d = __instance.transform.position - player.transform.position;
                    _handOffset = new Vector3(Vector3.Dot(d, right), d.y, Vector3.Dot(d, forward));
                    _haveHandOffset = true;
                }
                RefreshFactors(player, true);
                Launch(player, attack, false, out var modelSpawn, out var modelVelocity);
                if (modelVelocity.sqrMagnitude > 0.01f)
                {
                    _measuredItem = item.m_shared.m_name;
                    _measuredFactor = velocity.magnitude / modelVelocity.magnitude;
                }
                if (_logShots.Value)
                    Log.LogInfo($"[Shot] model at fire time: from {modelSpawn} (off by {Vector3.Distance(modelSpawn, __instance.transform.position):0.00} m), " +
                                $"dir off by {Vector3.Angle(modelVelocity, velocity):0.0} deg, speed {modelVelocity.magnitude:0.0} vs real {velocity.magnitude:0.0}");
            }
            if (!_logShots.Value) return;
            Log.LogInfo($"[Shot] FIRED {Utils.GetPrefabName(__instance.gameObject)} gravity {__instance.m_gravity}, ttl {__instance.m_ttl}: " +
                        $"from {__instance.transform.position} speed {velocity.magnitude:0.0} dir {velocity.normalized}. " +
                        $"Crosshair used: from {_simSpawn} (off by {Vector3.Distance(_simSpawn, __instance.transform.position):0.00} m) " +
                        $"speed {_simVelocity.magnitude:0.0} dir {_simVelocity.normalized} (off by {Vector3.Angle(_simVelocity, velocity):0.0} deg), gravity {_simGravity}. " +
                        $"Look dir now {((Player)owner).GetLookDir()}, fixed step {Time.fixedDeltaTime}");
            var current = CurrentAttack(player);
            Log.LogInfo($"[Shot] attack in use: height {current?.m_attackHeight}, range {current?.m_attackRange}, offset {current?.m_attackOffset}, " +
                        $"launch angle {current?.m_launchAngle}, origin joint '{current?.m_attackOriginJoint}', vel {current?.m_projectileVel}, accuracy {current?.m_projectileAccuracy}, " +
                        $"item attack: height {attack?.m_attackHeight}, range {attack?.m_attackRange}, angle {attack?.m_launchAngle}. Player at {player.transform.position}, " +
                        $"forward {player.transform.forward}, up {player.transform.up}, crouching {player.IsCrouching()}");
            Shots.Add(new Shot
            {
                Projectile = __instance, Start = __instance.transform.position, Time = UnityEngine.Time.time,
                Predicted = _predicted, PredictedDistance = _predictedDistance,
                AtClick = _predictedAtClick, AtClickDistance = _predictedDistanceAtClick, HaveClick = _haveClick,
            });
            _haveClick = false;
        }

        // --- Middle click ------------------------------------------------------------------------------------------------

        // The game fires a held secondary attack again whenever the last one ends; the hook's middle-click shot needs no
        // reload, so holding the button a moment too long shot twice. Only for the hook, and not for a looping attack
        // (letting go of the button is what ends those).
        [HarmonyPatch(typeof(Player), "PlayerAttackInput")]
        [HarmonyPrefix]
        private static void OneShotPerClick(Player __instance)
        {
            if (!_onePerClick.Value || __instance != Player.m_localPlayer || !SecondaryHold(__instance)) return;
            var weapon = __instance.GetCurrentWeapon();
            var secondary = weapon?.m_shared.m_secondaryAttack;
            if (secondary == null || secondary.m_loopingAttack || HookProjectile(secondary) == null) return;
            SecondaryHold(__instance) = false; // the press itself still fires (the game queues it); holding does not repeat it
        }

        // A hook point made by a shot that caught something (not the one the game adds at the end of a pull).
        [HarmonyPatch(typeof(Projectile), "SpawnOnHit")]
        [HarmonyPrefix]
        private static void HookHitting() => _hookCaught = true;

        [HarmonyPatch(typeof(Projectile), "SpawnOnHit")]
        [HarmonyFinalizer]
        private static void HookHit() => _hookCaught = false;

        // The middle-click hook hangs you on the rope (the game's "repell"); jumping in the air makes it pull (Pull()).
        // This pulls at once, the same way the game's jump does.
        [HarmonyPatch(typeof(GrapplingPoint), nameof(GrapplingPoint.Activate))]
        [HarmonyPostfix]
        private static void PullAtOnce(GrapplingPoint __instance, Character character)
        {
            if (!_pullAtOnce.Value || !_hookCaught || !__instance.m_secondary || character == null || character != Player.m_localPlayer) return;
            __instance.Pull();
        }

        // GrapplingPoint.Update adds every frame whose distance is not smaller than the last one to m_breakingTime and stops
        // the pull past m_breakEarlyTime; frames without a physics step count too, and the sum never goes back to 0. Here it
        // does whenever the pull brought you closer, so only a real standstill (half a second) stops it.
        [HarmonyPatch(typeof(GrapplingPoint), "Update")]
        [HarmonyPrefix]
        private static void KeepPulling(GrapplingPoint __instance)
        {
            if (!_keepPulling.Value || __instance.m_secondary) return;
            var who = Hooked(__instance);
            if (who == null || who != Player.m_localPlayer) return;
            if (Vector3.Distance(who.transform.position, __instance.transform.position) < LastDistance(__instance) - 0.001f)
                NotCloserTime(__instance) = 0f;
        }

        [HarmonyPatch(typeof(GrapplingPoint), nameof(GrapplingPoint.Activate))]
        [HarmonyPostfix]
        private static void Caught(GrapplingPoint __instance, Character character)
        {
            if (!_logShots.Value || character == null || character != Player.m_localPlayer) return;
            for (var i = Shots.Count - 1; i >= 0; i--)
            {
                var shot = Shots[i];
                if (shot.Done) continue;
                shot.Done = true;
                var feet = Player.m_localPlayer != null ? Vector3.Distance(Player.m_localPlayer.transform.position, __instance.transform.position) : 0f;
                Log.LogInfo($"[Shot] CAUGHT at {Vector3.Distance(shot.Start, __instance.transform.position):0.0} m after {UnityEngine.Time.time - shot.Time:0.00} s " +
                            $"(rope {feet:0.0} m of max {__instance.m_maxLength:0}{(feet > __instance.m_maxLength ? ": TOO LONG, lets go" : "")}). " +
                            Said(shot));
                return;
            }
        }

        private static string Said(Shot shot) =>
            $"Crosshair said: {(shot.Predicted ? $"catch at {shot.PredictedDistance:0.0} m" : "MISS (red)")}" +
            (shot.HaveClick ? $"; when you clicked: {(shot.AtClick ? $"catch at {shot.AtClickDistance:0.0} m" : "MISS (red)")}" : "");

        private void Update()
        {
            for (var i = Shots.Count - 1; i >= 0; i--)
            {
                var shot = Shots[i];
                if (!shot.Done && shot.Projectile != null) continue;
                if (!shot.Done)
                    Log.LogInfo("[Shot] MISSED (flew its full time). " + Said(shot));
                Shots.RemoveAt(i);
            }
        }

        // The same conditions Projectile.OnHit and SpawnOnHit use before the hook point appears.
        private static bool Catches(Projectile projectile, Collider collider)
        {
            var hit = Projectile.FindHitObject(collider);
            var terrain = collider.GetComponent<Heightmap>() != null;
            if (terrain && !projectile.m_spawnOnTerrain) return false;
            if (hit != null && hit.GetComponent<Character>() != null && !projectile.m_spawnOnCharacters) return false;
            if (hit != null && hit.GetComponent<WearNTear>() != null && !projectile.m_spawnOnWearNTear) return false;
            if (projectile.m_groundHitOnly && !terrain) return false;
            if (projectile.m_staticHitOnly && (collider.attachedRigidbody != null || (hit != null && hit.GetComponent<IDestructible>() != null))) return false;
            return hit == null || projectile.m_spawnOnHit.GetComponent<GrapplingPoint>().CanAttach(hit);
        }
    }
}
