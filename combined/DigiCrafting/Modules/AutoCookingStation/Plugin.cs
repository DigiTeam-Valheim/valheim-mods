// Cooking stations (iron and wooden, a switch for each) that run themselves from nearby chests. Every couple of seconds,
// for each one this game controls:
// 1. Cooked food goes into a chest the way "Store Nearby" (Local Craft From Storage) would: a chest that already holds
//    that food, else the nearest chest with room (burnt coal too). Same Cooking XP and bonus-yield chance as taking it
//    off by hand. With no room anywhere it waits on the station until just before it would burn, then drops beside it.
// 2. Empty slots are filled from chests in range, with the items switched on per item, while a fire burns under it.
// 3. Food does not burn while you are away: the game adds up all the time since you left when you come back, which
//    burned whatever was on the station. Cooking now pauses until you are back.
// The input and output have their own range, measured from the station's edge to the chest's edge, so 1 m is what it
// looks like. The fire under the station is not fed here. Chests are only touched when nobody has them open; the game
// then takes control of the chest like opening it would. Carts, ships and private chests of other players are skipped.
using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AutoCookingStation
{
    public class Plugin : DigiCore.DigiModule
    {
        private const string IronPrefab = "piece_cookingstation_iron", WoodenPrefab = "piece_cookingstation";
        private const int NotDone = 0, Burnt = 2;
        private const float PauseAfter = 5f;      // seconds between two cooking updates that mean nobody was here
        private const float LastMoment = 6f;      // food with no room drops this many seconds before it would burn

        // Everything the game's iron station cooks (the wooden one cooks 9 of these), so the switches are in F1 from the
        // main menu on. An item another mod adds to a station gets its switch when a station is first supplied.
        private static readonly string[][] KnownItems =
        {
            new[] { "RawMeat", "Boar Meat" }, new[] { "NeckTail", "Neck Tail" }, new[] { "DeerMeat", "Deer Meat" },
            new[] { "FishRaw", "Raw Fish" }, new[] { "Honey", "Honey" }, new[] { "ChickenMeat", "Chicken Meat" },
            new[] { "BjornMeat", "Bear Meat" }, new[] { "WolfMeat", "Wolf Meat" }, new[] { "HareMeat", "Hare Meat" },
            new[] { "LoxMeat", "Lox Meat" }, new[] { "SerpentMeat", "Serpent Meat" }, new[] { "BugMeat", "Seeker Meat" },
            new[] { "BoneMawSerpentMeat", "Bonemaw Meat" }, new[] { "VoltureMeat", "Volture Meat" },
            new[] { "AsksvinMeat", "Asksvin Tail" }, new[] { "MooseMeat", "Moose Meat" },
        };

        private static ConfigEntry<bool> _iron, _wooden, _fill, _empty, _xp, _pauseAway;
        private static ConfigEntry<float> _inputRange, _outputRange, _speed;
        private readonly Dictionary<string, ConfigEntry<bool>> _items = new Dictionary<string, ConfigEntry<bool>>();

        private static readonly Dictionary<CookingStation, ConfigEntry<bool>> Stations = new Dictionary<CookingStation, ConfigEntry<bool>>(); // with its switch
        private static readonly List<CookingStation> Dead = new List<CookingStation>();
        private static readonly HashSet<Container> Chests = new HashSet<Container>();

        private static readonly AccessTools.FieldRef<CookingStation, ZNetView> StationView = AccessTools.FieldRefAccess<CookingStation, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<Container, ZNetView> ChestView = AccessTools.FieldRefAccess<Container, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<Container, Piece> ChestPiece = AccessTools.FieldRefAccess<Container, Piece>("m_piece");
        private static readonly Func<CookingStation, bool> FireLit =
            AccessTools.MethodDelegate<Func<CookingStation, bool>>(AccessTools.Method(typeof(CookingStation), "IsFireLit"));

        private float _timer;

        private void Awake()
        {
            _iron = Config.Bind("General", "Iron cooking station", true, "Iron cooking stations run themselves. Off = they work as in the normal game.");
            _wooden = Config.Bind("General", "Wooden cooking station", true, "Wooden cooking stations run themselves. Off = they work as in the normal game.");
            _fill = Config.Bind("General", "Fill from chests", true,
                "Empty slots are filled from the chests within the input range, with the items switched on below, while a fire burns under the station.");
            _inputRange = Config.Bind("General", "Input chest range (m)", 1f, new ConfigDescription(
                "Chests up to this far from the station are used for what goes on it, from the station's edge to the chest's edge. " +
                "Small by default, so it only takes from a chest you put right next to it, not from all your stored meat.",
                new AcceptableValueRange<float>(0f, 30f)));
            _empty = Config.Bind("General", "Empty into chest", true,
                "Cooked food goes into a chest within the output range: one that already has it, otherwise the nearest one with room. " +
                "With no room anywhere it drops beside the station just before it would burn. Off = you take it off by hand.");
            _outputRange = Config.Bind("General", "Output chest range (m)", 10f, new ConfigDescription(
                "Chests up to this far from the station can receive the cooked food, from the station's edge to the chest's edge.",
                new AcceptableValueRange<float>(0f, 30f)));
            _xp = Config.Bind("General", "Cooking XP", true, "You get Cooking XP and the chance of bonus food, as if you did it by hand.");
            _pauseAway = Config.Bind("General", "No burning while away", true,
                "When you come back, the game adds up all the time since you left, which burns whatever was on the station. On = cooking pauses while you are away.");
            foreach (var item in KnownItems)
                _items[item[0]] = BindItem(item[1]);

            _speed = Config.Bind("General", "Speed multiplier", 1f, new ConfigDescription(
                "How fast the cooking stations switched on above cook: 2 = twice as fast, 1 = normal. Food burns that much sooner " +
                "too once it is done. Works right away.", new AcceptableValueRange<float>(0.25f, 10f)));
            PatchModule(typeof(Plugin));
            Stations.Clear();
            Chests.Clear();
            foreach (var station in FindObjectsByType<CookingStation>(FindObjectsSortMode.None)) StationAwake(station);
            foreach (var chest in FindObjectsByType<Container>(FindObjectsSortMode.None)) ChestAwake(chest);
        }

        private ConfigEntry<bool> BindItem(string name) => Config.Bind("Items", name, true,
            $"The station takes {name} from the input chests. Off = it stays in the chest (you can still put it on by hand).");

        [HarmonyPatch(typeof(CookingStation), "Awake")]
        [HarmonyPostfix]
        private static void StationAwake(CookingStation __instance)
        {
            var prefab = Utils.GetPrefabName(__instance.gameObject);
            if (prefab == IronPrefab) Stations[__instance] = _iron;
            else if (prefab == WoodenPrefab) Stations[__instance] = _wooden;
        }

        [HarmonyPatch(typeof(Container), "Awake")]
        [HarmonyPostfix]
        private static void ChestAwake(Container __instance) => Chests.Add(__instance);

        [HarmonyPatch(typeof(CookingStation), "GetDeltaTime")]
        [HarmonyPostfix]
        private static void PauseWhileAway(CookingStation __instance, ref float __result)
        {
            if (!Stations.TryGetValue(__instance, out var on) || !on.Value) return;
            if (__result > PauseAfter && _pauseAway.Value) __result = 1f;
            // The cooking time passes faster (speed), after the pause above.
            if (_speed.Value != 1f) __result *= _speed.Value;
        }

        private void Update()
        {
            if (Player.m_localPlayer == null || ZNet.instance == null) return;
            _timer -= Time.deltaTime;
            if (_timer > 0f) return;
            _timer = 2f;
            // Ultimate Builder's creative world: its stations stand still, nothing is taken from chests or filled there.
            if (DigiCore.CreativeWorld.Active) return;
            // One save per changed chest for the whole round (StoreRules.HoldSaves).
            StoreRules.HoldSaves();
            try
            {
                Chests.RemoveWhere(c => c == null);
                Dead.Clear();
                foreach (var pair in Stations)
                {
                    var station = pair.Key;
                    if (station == null) { Dead.Add(station); continue; }
                    if (!pair.Value.Value) continue;
                    var view = StationView(station);
                    if (view == null || !view.IsValid() || !view.IsOwner()) continue;
                    try
                    {
                        Run(station, view);
                    }
                    catch (Exception e)
                    {
                        Logger.LogWarning($"Cooking station at {station.transform.position}: {e.Message}");
                    }
                }
                foreach (var dead in Dead) Stations.Remove(dead);
            }
            finally
            {
                StoreRules.SaveHeld();
            }
        }

        private void Run(CookingStation station, ZNetView view)
        {
            var zdo = view.GetZDO();
            var slots = station.m_slots.Length;

            // 1. Cooked food out.
            if (_empty.Value)
            {
                List<Container> chests = null;
                for (var i = 0; i < slots; i++)
                {
                    var item = zdo.GetString("slot" + i);
                    var status = zdo.GetInt("slotstatus" + i);
                    if (item == "" || status == NotDone) continue;
                    var prefab = ObjectDB.instance.GetItemPrefab(item);
                    if (prefab == null) continue;
                    chests = chests ?? UsableChests(station, _outputRange.Value);
                    var burnt = status == Burnt;
                    var player = Player.m_localPlayer;
                    var amount = 1;
                    if (_xp.Value && !burnt && station.m_canGiveBonusYield &&
                        UnityEngine.Random.value < player.GetSkillFactor(station.m_skill) * InventoryGui.instance.m_craftBonusChance)
                        amount += InventoryGui.instance.m_craftBonusAmount;
                    var target = StoreRules.PickOutputChest(chests, prefab, amount) ?? StoreRules.PickOutputChest(chests, prefab, 1);
                    if (target != null)
                    {
                        if (!target.GetInventory().CanAddItem(prefab, amount)) amount = 1;
                        target.GetInventory().AddItem(prefab, amount);
                    }
                    else
                    {
                        // No room: coal is safe where it is, food waits until it is about to burn.
                        if (burnt || !AboutToBurn(station, zdo, i, item)) continue;
                        for (var n = 0; n < amount; n++) Drop(station, prefab);
                    }
                    ClearSlot(zdo, view, i);
                    if (_xp.Value && !burnt && station.m_skill != Skills.SkillType.None) player.RaiseSkill(station.m_skill, 0.6f);
                }
            }

            // 2. Empty slots filled (only over a fire: nothing would cook, and it would only sit there).
            if (_fill.Value && Enumerable.Range(0, slots).Any(i => zdo.GetString("slot" + i) == "") && FireLit(station))
            {
                var chests = UsableChests(station, _inputRange.Value);
                for (var i = 0; i < slots; i++)
                {
                    if (zdo.GetString("slot" + i) != "") continue;
                    if (!StoreRules.TakeOne(chests, item => item.m_dropPrefab != null && CanCook(station, item.m_dropPrefab.name), out var taken))
                        break;
                    view.InvokeRPC("RPC_AddItem", taken.m_dropPrefab.name, taken.m_cheated);
                    if (_xp.Value && station.m_skill != Skills.SkillType.None) Player.m_localPlayer.RaiseSkill(station.m_skill, 0.4f);
                }
            }
        }

        private bool CanCook(CookingStation station, string prefab)
        {
            foreach (var conversion in station.m_conversion)
            {
                if (conversion?.m_from == null || conversion.m_from.name != prefab) continue;
                if (!_items.TryGetValue(prefab, out var entry))
                    _items[prefab] = entry = BindItem(Localization.instance.Localize(conversion.m_from.m_itemData.m_shared.m_name));
                return entry.Value;
            }
            return false;
        }

        private static bool AboutToBurn(CookingStation station, ZDO zdo, int slot, string cooked)
        {
            if (!station.m_canOvercookItems) return false;
            var conversion = station.m_conversion.FirstOrDefault(c => c?.m_to != null && c.m_to.name == cooked);
            return conversion == null || zdo.GetFloat("slot" + slot) > conversion.m_cookTime * 2f - LastMoment;
        }

        private static void Drop(CookingStation station, GameObject prefab)
        {
            var at = station.transform.position + Vector3.up + UnityEngine.Random.insideUnitSphere * 0.3f;
            ItemDrop.OnCreateNew(Instantiate(prefab, at, Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f)));
        }

        private static void ClearSlot(ZDO zdo, ZNetView view, int i)
        {
            zdo.Set("slot" + i, "");
            zdo.Set("slot" + i, 0f);
            zdo.Set("slotstatus" + i, NotDone);
            zdo.Set(ZDOVars.s_cheatedQueued + i, false);
            view.InvokeRPC(ZNetView.Everybody, "RPC_SetSlotVisual", i, "");
        }

        // Chests in range, nearest first, that this game may change right now. Chests controlled by another player are
        // claimed when nobody has them open, and used from the next round on.
        private static List<Container> UsableChests(CookingStation station, float range)
        {
            var me = Game.instance.GetPlayerProfile().GetPlayerID();
            var at = station.transform.position;
            var edge = BoundsOf(station);
            var reach = range + 10f; // a chest's centre is never further than this from the station when its edge is in range
            var result = new List<Container>();
            var gaps = new Dictionary<Container, float>();
            foreach (var chest in Chests)
            {
                if ((chest.transform.position - at).sqrMagnitude > reach * reach) continue;
                if (chest.m_wagon != null || chest.GetComponentInParent<Ship>() != null || chest.GetComponent<TombStone>() != null) continue;
                var piece = ChestPiece(chest);
                if (piece == null) continue; // only built chests
                var gap = Gap(edge, BoundsOf(chest));
                if (gap > range) continue;
                if (chest.m_privacy == Container.PrivacySetting.Private && piece.GetCreator() != me) continue;
                if (chest.m_privacy == Container.PrivacySetting.Group) continue;
                var view = ChestView(chest);
                if (view == null || !view.IsValid() || chest.GetInventory() == null) continue;
                if (view.GetZDO().GetInt(ZDOVars.s_inUse) == 1 || chest.IsInUse()) continue;
                if (!view.IsOwner())
                {
                    view.ClaimOwnership();
                    continue;
                }
                result.Add(chest);
                gaps[chest] = gap;
            }
            return result.OrderBy(c => gaps[c]).ToList(); // no tuples: Valheim's runtime lacks System.ValueTuple
        }

        // What the player bumps into, in world space; only a point when the object has no solid collider.
        private static Bounds BoundsOf(Component owner)
        {
            var bounds = new Bounds(owner.transform.position, Vector3.zero);
            var first = true;
            foreach (var collider in owner.GetComponentsInChildren<Collider>())
            {
                if (collider.isTrigger || !collider.enabled) continue;
                if (first) bounds = collider.bounds;
                else bounds.Encapsulate(collider.bounds);
                first = false;
            }
            return bounds;
        }

        private static float Gap(Bounds a, Bounds b)
        {
            var x = Mathf.Max(0f, Mathf.Max(a.min.x - b.max.x, b.min.x - a.max.x));
            var y = Mathf.Max(0f, Mathf.Max(a.min.y - b.max.y, b.min.y - a.max.y));
            var z = Mathf.Max(0f, Mathf.Max(a.min.z - b.max.z, b.min.z - a.max.z));
            return Mathf.Sqrt(x * x + y * y + z * z);
        }
    }
}
