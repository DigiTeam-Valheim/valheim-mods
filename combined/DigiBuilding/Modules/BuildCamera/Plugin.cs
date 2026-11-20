// Build Camera: with a building tool in hand (hammer, hoe, cultivator, or another mod's) at a workbench, H lets the camera
// leave your character and fly around (WASD, Space up, Ctrl down, Shift faster; on a gamepad LB + LT and the sticks).
// Building, removing, rotating and every other build tool work from where the camera is. The character stands still;
// taking damage, putting the tool away, dying or teleporting ends it.
// While flying, the camera stays within "Camera range" of the nearest crafting station (Epic Loot's build distance
// enchantment makes that range bigger), and a station counts as near when the camera is that close to it. Without a
// workbench it only starts when building needs none (Epic Loot's Free Build, the world's no-workbench setting, the
// no-cost cheat, or a mod like Build Restrictions saying any station is fine); it then stays within that range of you.
// Extras: a spotlight on the camera (L), mist cleared around it, picking up things near it, and the materials of pieces
// you remove landing at your feet. Replaces Atze's Build Camera (Nexus only): its settings are copied once; installed too, it is switched off.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace BuildCamera
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.buildcamera";
        public const string Name = "Digi's Build Camera";
        public const string Version = "1.0.0";

        private const string AtzeGuid = "AtzesBuildCamMod";
        private const string AtzeConfig = "Atze's Build Camera.cfg";

        private static ConfigEntry<KeyboardShortcut> _key, _lightKey;
        private static ConfigEntry<float> _speed, _range, _depth, _lightAngle, _lightDistance, _lightBrightness, _mistRange,
            _pickDistance, _autoRadius;
        private static ConfigEntry<bool> _toFeet, _light, _mist, _pickUp, _autoPickup;

        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> RightItem = AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_rightItem");
        private static readonly AccessTools.FieldRef<Player, GameObject> Hovering = AccessTools.FieldRefAccess<Player, GameObject>("m_hovering");
        private static readonly AccessTools.FieldRef<Player, Character> HoveringCreature = AccessTools.FieldRefAccess<Player, Character>("m_hoveringCreature");
        private static readonly AccessTools.FieldRef<Player, int> InteractMask = AccessTools.FieldRefAccess<Player, int>("m_interactMask");
        private static readonly FieldInfo AutoPickupOn = AccessTools.Field(typeof(Player), "m_enableAutoPickup");
        private static readonly FieldInfo AllStations = AccessTools.Field(typeof(CraftingStation), "m_allStations");
        private static readonly FieldInfo Listener = AccessTools.Field(typeof(GameCamera), "m_listner");

        // Epic Loot, when installed: its effect values (by reflection, it is a soft dependency).
        private static MethodInfo _elValue, _elHas;
        private static bool _elChecked;

        private static BepInEx.Logging.ManualLogSource _log;
        private static bool _running;

        // The flight
        internal static bool Active;
        private static Player _player;
        private static float _savedReach;
        private static float _yaw, _pitch;
        private static GameObject _gadget;
        private static Light _lamp;
        private static bool _lampOn = true;
        private static bool _removing, _probing;
        private static float _nextPickup, _nextCheck;
        private static bool _free;
        private static float _factor = 1f;
        private static readonly Collider[] Found = new Collider[64];

        private void Awake()
        {
            _log = Logger;
            _key = Config.Bind("General", "Camera key", new KeyboardShortcut(KeyCode.H),
                "With a building tool in hand (hammer, hoe, cultivator) at a workbench: the camera leaves your character and " +
                "flies (WASD, Space up, Ctrl down, Shift faster). Press again to come back. On a gamepad: LB + LT.");
            _speed = Config.Bind("General", "Speed", 1.5f, new ConfigDescription(
                "How fast the camera flies (times your walking speed; with Shift times your running speed).",
                new AcceptableValueRange<float>(0.1f, 5f)));
            _range = Config.Bind("General", "Camera range (m)", 100f, new ConfigDescription(
                "How far the camera may fly from the nearest crafting station (or from you, where building needs no " +
                "workbench). A station counts as near for building while the camera is this close to it. Epic Loot's build " +
                "distance enchantment makes it bigger by the same percentage.", new AcceptableValueRange<float>(10f, 300f)));
            _depth = Config.Bind("General", "Depth below ground (m)", 2f, new ConfigDescription(
                "How far the camera may go under the ground (negative: it stays that high above it).",
                new AcceptableValueRange<float>(-5f, 5f)));
            _toFeet = Config.Bind("General", "Materials to your feet", true,
                "Pieces you remove with the camera drop their materials at your feet instead of where they stood.");
            _light = Config.Bind("Light", "Light", true, "A spotlight on the camera while it flies.");
            _lightKey = Config.Bind("Light", "Light key", new KeyboardShortcut(KeyCode.L), "Switches the light on and off while flying.");
            _lightAngle = Config.Bind("Light", "Light angle", 90f, new ConfigDescription("How wide the light shines, in degrees.",
                new AcceptableValueRange<float>(15f, 135f)));
            _lightDistance = Config.Bind("Light", "Light distance (m)", 40f, new ConfigDescription("How far the light reaches.",
                new AcceptableValueRange<float>(5f, 150f)));
            _lightBrightness = Config.Bind("Light", "Light brightness", 2f, new ConfigDescription("How bright the light is.",
                new AcceptableValueRange<float>(0.1f, 5f)));
            _mist = Config.Bind("Mist", "Clear mist", true, "Clears the Mistlands mist around the camera while it flies.");
            _mistRange = Config.Bind("Mist", "Mist clearing range (m)", 100f, new ConfigDescription("How far around the camera the mist clears.",
                new AcceptableValueRange<float>(5f, 150f)));
            _pickUp = Config.Bind("Pick up", "Pick up near the camera", true,
                "Use (E) picks up items and plants you aim at near the camera, not only near you.");
            _pickDistance = Config.Bind("Pick up", "Pick up distance (m)", 10f, new ConfigDescription(
                "How far from the camera you can pick things up.", new AcceptableValueRange<float>(1f, 25f)));
            _autoPickup = Config.Bind("Pick up", "Auto pickup around the camera", true,
                "Items lying near the camera are picked up by themselves (when your own auto pickup is on).");
            _autoRadius = Config.Bind("Pick up", "Auto pickup radius (m)", 5f, new ConfigDescription(
                "How close to the camera items are picked up by themselves.", new AcceptableValueRange<float>(1f, 10f)));
            _light.SettingChanged += (s, e) => Gadget(Active);
            _mist.SettingChanged += (s, e) => Gadget(Active);

            var atze = Path.Combine(Paths.ConfigPath, AtzeConfig);
            if (File.Exists(atze) && FirstTime("from atzes build camera")) FromAtze(atze);
        }

        private void Start()
        {
            SwitchOffReplaced(AtzeGuid, "Atze's Build Camera"); // installed too: it goes off, ours runs
            PatchModule(typeof(Plugin));
            _running = true;
        }

        private void OnDestroy()
        {
            Stop(null);
            _running = false;
        }

        // Atze's settings, once: key, speed, range (its workbench range x its multiplier), light, mist, pick up.
        private void FromAtze(string path)
        {
            try
            {
                var values = new Dictionary<string, string>();
                var section = "";
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.StartsWith("[") && line.EndsWith("]")) { section = line.Substring(1, line.Length - 2); continue; }
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var eq = line.IndexOf('=');
                    if (eq > 0) values[section + "/" + line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
                string Get(string key) => values.TryGetValue(key, out var v) ? v : null;
                void Num(ConfigEntry<float> entry, string key)
                {
                    if (float.TryParse(Get(key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f))
                        entry.Value = f;
                }
                void Flag(ConfigEntry<bool> entry, string key)
                {
                    if (bool.TryParse(Get(key), out var b)) entry.Value = b;
                }
                var key = Get("Hotkeys/Switch_Build_Camera");
                if (!string.IsNullOrEmpty(key))
                {
                    var shortcut = KeyboardShortcut.Deserialize(key);
                    if (shortcut.MainKey != KeyCode.None) _key.Value = shortcut;
                }
                Num(_speed, "Camera/Camera_Speed_Multiplier");
                Num(_depth, "Camera/Camera_Ground_Dive_Limiter");
                if (float.TryParse(Get("General/Build_Range_Workbench"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var bench) &&
                    float.TryParse(Get("Camera/Camera_Range_Multiplier"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var times))
                    _range.Value = bench * times;
                Flag(_toFeet, "General/Drop_Resources_Near_Player");
                Flag(_light, "BuildLight/Switch_Build_Light");
                Num(_lightAngle, "BuildLight/FOV_Build_Light");
                Num(_lightDistance, "BuildLight/Distance_Build_Light");
                Num(_lightBrightness, "BuildLight/Intensity_Build_Light");
                Flag(_mist, "BuildDemist/Switch_Build_Demist");
                Num(_mistRange, "BuildDemist/Range_Build_Demist");
                // (Atze's names are crossed: Range_Auto_PickUp is the switch, Switch_Auto_PickUp the radius.)
                Flag(_pickUp, "PickUp/Switch_Interactibility_PickUp");
                Num(_pickDistance, "PickUp/Distance_To_PickUp");
                Flag(_autoPickup, "PickUp/Range_Auto_PickUp");
                Num(_autoRadius, "PickUp/Switch_Auto_PickUp");
                Logger.LogInfo("Copied the settings of Atze's Build Camera (key " + _key.Value + ", range " + _range.Value + " m).");
            }
            catch (Exception e)
            {
                Logger.LogWarning("Could not read Atze's Build Camera settings: " + e.Message);
            }
        }

        // --- Switching on and off ----------------------------------------------------------------------------------------

        private void Update()
        {
            if (!_running) return;
            var player = Player.m_localPlayer;
            if (Active)
            {
                var why = WhyStop(player);
                if (why != null) { Stop(why); return; }
                if (!Typing() && _lightKey.Value.IsDown() && _light.Value)
                {
                    _lampOn = !_lampOn;
                    if (_lamp != null) _lamp.enabled = _lampOn;
                }
                AutoPickup(player);
            }
            if (player == null || Typing() || Menu.IsVisible() || Console.IsVisible() || InventoryGui.IsVisible() || Minimap.IsOpen()) return;
            var pad = ZInput.IsGamepadActive() &&
                      ((ZInput.GetButtonDown("JoyLBumper") && ZInput.GetButton("JoyLTrigger")) || (ZInput.GetButtonDown("JoyLTrigger") && ZInput.GetButton("JoyLBumper")));
            if (!_key.Value.IsDown() && !pad) return;
            if (Active) Stop("$hud_off");
            else TryStart(player);
        }

        internal static bool TryStart(Player player)
        {
            if (player == null || GameCamera.instance == null || Active) return false;
            if (!HoldsBuildTool(player) || Busy(player)) return false;
            Refresh(player, true);
            if (!_free && !AtStation(player.transform.position))
            {
                player.Message(MessageHud.MessageType.Center, "$msg_missingstation");
                return false;
            }
            _player = player;
            _savedReach = player.m_maxPlaceDistance;
            player.m_maxPlaceDistance = Mathf.Max(_savedReach, 1000f); // reach counts from the camera now (the ray is 50 m)
            var euler = GameCamera.instance.transform.rotation.eulerAngles;
            _pitch = euler.x > 180f ? euler.x - 360f : euler.x;
            _yaw = euler.y;
            Active = true;
            _lampOn = true;
            Gadget(true);
            player.Message(MessageHud.MessageType.TopLeft, "Build camera on");
            _log?.LogInfo("Build camera on at " + GameCamera.instance.transform.position.ToString("F1") + (_free ? " (free build)" : ""));
            return true;
        }

        internal static void Stop(string why)
        {
            if (!Active) return;
            Active = false;
            if (_player != null) _player.m_maxPlaceDistance = _savedReach;
            _player = null;
            Gadget(false);
            if (string.IsNullOrEmpty(why)) return;
            var player = Player.m_localPlayer;
            if (player != null) player.Message(MessageHud.MessageType.TopLeft, why == "$hud_off" ? "Build camera off" : "Build camera off: " + why);
            _log?.LogInfo("Build camera off" + (why == "$hud_off" ? "" : ": " + why));
        }

        private static string WhyStop(Player player)
        {
            if (player == null || player != _player) return "";
            if (player.IsDead()) return "you died";
            if (player.IsTeleporting()) return "teleporting";
            if (!HoldsBuildTool(player)) return "no building tool in hand";
            if (Busy(player)) return "busy";
            return null;
        }

        private static bool Busy(Player player) => player.IsDead() || player.IsTeleporting() || player.IsSleeping() || player.InCutscene() || player.IsAttached();

        private static bool HoldsBuildTool(Player player)
        {
            var item = RightItem(player);
            return item != null && item.m_shared.m_buildPieces != null;
        }

        private static bool Typing() =>
            (Chat.instance != null && Chat.instance.HasFocus()) || Console.IsVisible() || TextInput.IsVisible() || Minimap.InTextInput() ||
            (Hud.instance != null && Hud.instance.m_buildUi != null && Hud.instance.m_buildUi.SearchFieldFocused); // the build menu's search box

        // The spotlight and the mist clearing, children of the camera.
        private static void Gadget(bool on)
        {
            if (_gadget != null) UnityEngine.Object.Destroy(_gadget);
            _gadget = null;
            _lamp = null;
            if (!on || GameCamera.instance == null || (!_light.Value && !_mist.Value)) return;
            _gadget = new GameObject("DigiBuildCamera");
            _gadget.transform.SetParent(GameCamera.instance.transform, false);
            if (_light.Value)
            {
                _lamp = _gadget.AddComponent<Light>();
                _lamp.type = LightType.Spot;
                _lamp.spotAngle = _lightAngle.Value;
                _lamp.range = _lightDistance.Value;
                _lamp.intensity = _lightBrightness.Value;
                _lamp.enabled = _lampOn;
            }
            if (_mist.Value)
            {
                var mist = new GameObject("DigiBuildCameraDemister");
                mist.SetActive(false); // the force field first: the Demister reads it in Awake
                mist.transform.SetParent(_gadget.transform, false);
                mist.transform.localPosition = new Vector3(0f, 0f, 1.5f);
                var field = mist.AddComponent<ParticleSystemForceField>();
                field.shape = ParticleSystemForceFieldShape.Sphere;
                field.startRange = _mistRange.Value;
                field.endRange = _mistRange.Value + 5f;
                field.gravityFocus = 0.5f;
                mist.AddComponent<Demister>();
                mist.SetActive(true);
            }
        }

        // --- Where the camera may be -------------------------------------------------------------------------------------

        private static List<CraftingStation> Stations() => AllStations?.GetValue(null) as List<CraftingStation>;

        private static float Flat(Vector3 v) => new Vector2(v.x, v.z).magnitude;

        // Standing in a crafting station's building area (the game's own range).
        private static bool AtStation(Vector3 pos)
        {
            var stations = Stations();
            if (stations == null) return false;
            foreach (var s in stations)
                if (s != null && Flat(s.transform.position - pos) < s.GetStationBuildRange()) return true;
            return false;
        }

        // Free build, and Epic Loot's build distance, looked up twice a second.
        private static void Refresh(Player player, bool now = false)
        {
            if (!now && Time.time < _nextCheck) return;
            _nextCheck = Time.time + 0.5f;
            _free = FreeBuild(player);
            _factor = 1f + Mathf.Max(0f, EpicLootValue(player, "ModifyBuildDistance", 0.01f));
        }

        internal static float Range(Player player) => _range.Value * _factor;

        private static bool FreeBuild(Player player)
        {
            if (player.NoCostCheat()) return true;
            if (ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoWorkbench)) return true;
            if (EpicLootHas(player, "FreeBuild")) return true;
            // Another mod (Build Restrictions' missing station switch) answering "yes" for any station.
            _probing = true;
            try { return CraftingStation.HaveBuildStationInRange("digi_buildcamera_probe", player.transform.position) != null; }
            finally { _probing = false; }
        }

        private static void FindEpicLoot()
        {
            if (_elChecked) return;
            _elChecked = true;
            var type = AccessTools.TypeByName("EpicLoot.PlayerExtensions");
            if (type == null) return;
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var p = m.GetParameters();
                if (m.Name == "GetTotalActiveMagicEffectValue" && p.Length == 4 && p[0].ParameterType == typeof(Player) && p[1].ParameterType == typeof(string)) _elValue = m;
                if (m.Name == "HasActiveMagicEffect" && p.Length == 2 && p[0].ParameterType == typeof(Player) && p[1].ParameterType == typeof(string)) _elHas = m;
            }
            _log?.LogInfo("Epic Loot: build distance " + (_elValue != null ? "found" : "not found") + ", free build " + (_elHas != null ? "found" : "not found"));
        }

        private static float EpicLootValue(Player player, string effect, float scale)
        {
            FindEpicLoot();
            if (_elValue == null) return 0f;
            try { return (float)_elValue.Invoke(null, new object[] { player, effect, scale, null }); }
            catch { return 0f; }
        }

        private static bool EpicLootHas(Player player, string effect)
        {
            FindEpicLoot();
            if (_elHas == null) return false;
            try { return (bool)_elHas.Invoke(null, new object[] { player, effect }); }
            catch { return false; }
        }

        // Keeps the camera within range of the nearest station (or of you in free build) and not too deep in the ground.
        // False when there is nothing to stay near.
        private static bool Leash(Transform cam, Player player)
        {
            var pos = cam.position;
            var range = Range(player);
            var best = float.MaxValue;
            var anchor = Vector3.zero;
            var stations = Stations();
            if (stations != null)
                foreach (var s in stations)
                {
                    if (s == null) continue;
                    var d = Vector3.Distance(s.transform.position, pos);
                    if (d < best) { best = d; anchor = s.transform.position; }
                }
            if (_free)
            {
                var d = Vector3.Distance(player.transform.position, pos);
                if (d < best) { best = d; anchor = player.transform.position; }
            }
            if (best == float.MaxValue) return false;
            if (best > range) pos = anchor + (pos - anchor).normalized * range;
            if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(pos, out var ground) && pos.y < ground - _depth.Value)
                pos.y = ground - _depth.Value;
            cam.position = pos;
            return true;
        }

        // --- The flight ------------------------------------------------------------------------------------------------

        private static bool InputFree() =>
            !Typing() && !Menu.IsVisible() && !InventoryGui.IsVisible() && !StoreGui.IsVisible() && !Minimap.IsOpen() &&
            !Hud.IsPieceSelectionVisible() && !Hud.InRadial() && !Game.IsPaused();

        [HarmonyPatch(typeof(GameCamera), "UpdateCamera")]
        [HarmonyPrefix]
        private static bool Fly(GameCamera __instance, float dt)
        {
            if (!Active) return true;
            var player = _player;
            if (player == null) { Stop(""); return true; }
            Refresh(player);
            var cam = __instance.transform;
            if (InputFree())
            {
                if (ZInput.IsMouseActive())
                {
                    var delta = ZInput.GetMouseDelta() * PlayerController.m_mouseSens;
                    _yaw += delta.x;
                    _pitch = Mathf.Clamp(_pitch - (PlayerController.m_invertMouse ? -delta.y : delta.y), -89f, 89f);
                }
                if (ZInput.IsGamepadActive() && !(player.InPlaceMode() && ZInput.GetButton("JoyRotate")))
                {
                    var turn = 110f * dt * PlayerController.m_gamepadSens;
                    _yaw += ZInput.GetJoyRightStickX() * turn * (PlayerController.m_invertCameraX ? -1f : 1f);
                    _pitch = Mathf.Clamp(_pitch + ZInput.GetJoyRightStickY() * turn * (PlayerController.m_invertCameraY ? -1f : 1f), -89f, 89f);
                }
                cam.rotation = Quaternion.Euler(0f, _yaw, 0f) * Quaternion.Euler(_pitch, 0f, 0f);

                var move = Vector3.zero;
                if (ZInput.GetButton("Forward")) move += cam.forward;
                if (ZInput.GetButton("Backward")) move -= cam.forward;
                if (ZInput.GetButton("Right")) move += cam.right;
                if (ZInput.GetButton("Left")) move -= cam.right;
                if (move.sqrMagnitude > 0f) move.Normalize();
                move += cam.right * ZInput.GetJoyLeftStickX() - cam.forward * ZInput.GetJoyLeftStickY();
                if (ZInput.GetButton("Jump") || ZInput.GetButton("JoyJump")) move += Vector3.up;
                if (ZInput.GetButton("Crouch") || ZInput.GetButton("JoyCrouch")) move -= Vector3.up;
                var fast = ZInput.GetButton("Run") || ZInput.GetButton("JoyRun");
                cam.position += move * (dt * (fast ? player.m_runSpeed : player.m_walkSpeed) * _speed.Value);
            }
            if (!Leash(cam, player)) { Stop("$msg_missingstation"); return true; }
            return false;
        }

        // Sounds are heard where the camera is.
        [HarmonyPatch(typeof(GameCamera), "UpdateListner")]
        [HarmonyPostfix]
        private static void Ears()
        {
            if (Active && Listener?.GetValue(GameCamera.instance) is Component ears) ears.transform.localPosition = Vector3.zero;
        }

        // The character stands still and does not look around while the camera flies.
        [HarmonyPatch(typeof(PlayerController), "TakeInput")]
        [HarmonyPostfix]
        private static void StandStill(ref bool __result)
        {
            if (Active) __result = false;
        }

        // The gamepad's crouch and hide buttons move the camera instead.
        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetButtonDown), new[] { typeof(string) })]
        [HarmonyPostfix]
        private static void PadDown(string name, ref bool __result)
        {
            if (__result && Active && (name == "JoyHide" || name == "JoyCrouch")) __result = false;
        }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetButton), new[] { typeof(string) })]
        [HarmonyPostfix]
        private static void PadHeld(string name, ref bool __result)
        {
            if (__result && Active && name == "JoyHide") __result = false;
        }

        [HarmonyPatch(typeof(Player), "OnDamaged")]
        [HarmonyPostfix]
        private static void Hurt(Player __instance)
        {
            if (Active && __instance == _player) Stop("under attack!");
        }

        // --- Building from the camera ------------------------------------------------------------------------------------

        // The game asks whether the station a piece needs is near the player; while flying, near the camera counts too.
        [HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.HaveBuildStationInRange))]
        [HarmonyPostfix]
        private static void StationNearCamera(string name, Vector3 point, ref CraftingStation __result)
        {
            if (__result != null || !Active || _probing || _player == null || GameCamera.instance == null) return;
            if (Flat(point - _player.transform.position) > 1f) return; // a question about somewhere else
            var stations = Stations();
            if (stations == null) return;
            var cam = GameCamera.instance.transform.position;
            var range = Range(_player);
            foreach (var s in stations)
                if (s != null && s.m_name == name && Flat(s.transform.position - cam) < range)
                {
                    __result = s;
                    return;
                }
        }

        [HarmonyPatch(typeof(Player), "RemovePiece")]
        [HarmonyPrefix]
        private static void Removing() => _removing = Active && _toFeet.Value;

        [HarmonyPatch(typeof(Player), "RemovePiece")]
        [HarmonyFinalizer]
        private static void Removed() => _removing = false;

        // The materials of a removed piece drop at your feet: the piece moves there for the moment it drops them.
        [HarmonyPatch(typeof(Piece), nameof(Piece.DropResources))]
        [HarmonyPrefix]
        private static void DropHere(Piece __instance, out Vector3 __state)
        {
            __state = new Vector3(float.NaN, 0f, 0f);
            if (!_removing || _player == null || Vector3.Distance(__instance.transform.position, _player.transform.position) < 3f) return;
            __state = __instance.transform.position;
            __instance.transform.position = _player.transform.position + _player.transform.forward * 0.5f;
        }

        [HarmonyPatch(typeof(Piece), nameof(Piece.DropResources))]
        [HarmonyFinalizer]
        private static void DropDone(Piece __instance, Vector3 __state)
        {
            if (!float.IsNaN(__state.x) && __instance != null) __instance.transform.position = __state;
        }

        // --- Picking up near the camera ----------------------------------------------------------------------------------

        // With a building tool out the game hovers nothing; while flying, items and plants in front of the camera.
        [HarmonyPatch(typeof(Player), "UpdateHover")]
        [HarmonyPostfix]
        private static void HoverFromCamera(Player __instance)
        {
            if (!Active || !_pickUp.Value || __instance != _player || GameCamera.instance == null) return;
            var cam = GameCamera.instance.transform;
            if (!Physics.Raycast(cam.position, cam.forward, out var hit, _pickDistance.Value + 5f, InteractMask(__instance))) return;
            var go = hit.collider.gameObject;
            if (go.GetComponentInParent<Pickable>() == null && go.GetComponentInParent<ItemDrop>() == null) return;
            var hoverable = go.GetComponentInParent<Hoverable>();
            if (hoverable == null || hit.distance > _pickDistance.Value + hoverable.GetHoverOffset()) return;
            Hovering(__instance) = hit.collider.GetComponent<Hoverable>() != null || hit.collider.attachedRigidbody == null
                ? hit.collider.gameObject
                : hit.collider.attachedRigidbody.gameObject;
            HoveringCreature(__instance) = null;
        }

        private static void AutoPickup(Player player)
        {
            if (!_autoPickup.Value || Time.time < _nextPickup || GameCamera.instance == null) return;
            _nextPickup = Time.time + 0.25f;
            if (AutoPickupOn != null && !(bool)AutoPickupOn.GetValue(null)) return;
            var count = Physics.OverlapSphereNonAlloc(GameCamera.instance.transform.position, _autoRadius.Value, Found, LayerMask.GetMask("item"));
            for (var i = 0; i < count; i++)
            {
                var body = Found[i].attachedRigidbody;
                var drop = body != null ? body.GetComponent<ItemDrop>() : null;
                if (drop == null || !drop.m_autoPickup || drop.IsPiece()) continue;
                if (!drop.CanPickup())
                {
                    drop.RequestOwn();
                    continue;
                }
                drop.Load();
                var inventory = player.GetInventory();
                if (inventory.CanAddItem(drop.m_itemData) && drop.m_itemData.GetWeight() + inventory.GetTotalWeight() <= player.GetMaxCarryWeight())
                    player.Pickup(drop.gameObject);
            }
        }
    }
}
