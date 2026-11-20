// Ovens that run themselves from nearby chests. Every couple of seconds, for each oven (any cooking station that burns
// fuel) this game controls:
// 1. Finished food goes into a chest the way "Store Nearby" (Local Craft From Storage) would: a chest that already
//    holds that food, else the nearest chest with room (burnt leftovers too). Same Cooking XP and bonus-yield chance as
//    taking it out by hand.
// 2. Empty slots are filled with anything the oven can bake, taken from chests in range.
// 3. While something is baking, wood (the oven's fuel) is kept full from chests in range (the stone oven holds 10, the
//    frost foundry 20). Fuel only burns while something uncooked is inside, so what is left after baking stays there.
//    (It used to keep only 3: the frost foundry then took one piece at a time and its level kept rising and falling.)
// The frost foundry (Deep North) is one too: it finishes gold gear, with liquid frost as its fuel.
// Shift+E on the fuel puts in as much as fits from your inventory, then from the chests around you (the game adds one per
// press): as far as Craft From Chests reaches (its range, 50 m if it is not there), since you fill it by hand. While Craft
// From Chests runs it does Shift+E itself, for everything that takes fuel.
// Stone ovens have a speed multiplier: time passes faster for them, for the food and the wood alike.
// The chest range is measured from the oven's edge to the chest's edge (the frost foundry is big: from its centre a
// chest right beside it was more than 4 m away).
// Chests are only touched when nobody has them open; the game then takes control of the chest like opening it would,
// so its contents stay in sync for everyone. Carts, ships and private chests of other players are skipped.
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AutoOven
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.autooven";
        public const string Name = "Digi's Auto Oven";
        public const string Version = "1.2.2";

        private const int NotDone = 0, Done = 1;
        private const string StoneOven = "piece_oven", FrostFoundry = "piece_FrostFoundry";

        private static ConfigEntry<bool> _enabled, _fill, _fuel, _fuelFull, _empty, _xp, _bulkFuel;
        private static ConfigEntry<float> _range, _inputRange, _fuelRange, _ovenSpeed, _foundrySpeed;
        private static ConfigEntry<int> _fuelLevel;

        private static readonly HashSet<CookingStation> Ovens = new HashSet<CookingStation>();
        private static readonly HashSet<Container> Chests = new HashSet<Container>();

        private static readonly AccessTools.FieldRef<CookingStation, ZNetView> OvenView = AccessTools.FieldRefAccess<CookingStation, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<Container, ZNetView> ChestView = AccessTools.FieldRefAccess<Container, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<Container, Piece> ChestPiece = AccessTools.FieldRefAccess<Container, Piece>("m_piece");

        private float _timer;

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
            _enabled = Config.Bind("General", "Enabled", true, "Ovens fill, fuel and empty themselves using the chests near them.");
            _range = Config.Bind("General", "Chest range (m)", 10f,
                new ConfigDescription("Output: baked food (and the frost foundry's gear) goes into chests up to this many meters from the oven (edge to edge).",
                    new AcceptableValueRange<float>(1f, 30f)));
            _inputRange = Config.Bind("General", "Input chest range (m)", 10f,
                new ConfigDescription("What the oven bakes (dough, uncooked pies, unforged gear) is taken from chests up to this many " +
                    "meters away (edge to edge).", new AcceptableValueRange<float>(1f, 30f)));
            _fuelRange = Config.Bind("General", "Fuel chest range (m)", 10f,
                new ConfigDescription("Fuel (wood; liquid frost for the frost foundry) is taken from chests up to this many meters away " +
                    "(edge to edge).", new AcceptableValueRange<float>(1f, 30f)));
            // Input and fuel had the one Chest range before (2026-11-19): they start from it, so nothing changes until moved.
            if (FirstTime("input and fuel chest ranges"))
            {
                _inputRange.Value = _range.Value;
                _fuelRange.Value = _range.Value;
            }
            _fill = Config.Bind("General", "Fill from chests", true, "Empty oven slots are filled with anything the oven can bake (bread dough, uncooked pies, ...) from nearby chests.");
            _fuel = Config.Bind("General", "Add fuel from chests", true,
                "While something is baking, the oven takes its fuel from nearby chests (wood; liquid frost for the frost foundry).");
            _fuelFull = Config.Bind("General", "Keep the fuel full", true,
                "While something is baking, the oven is kept full of fuel (stone oven 10 wood, frost foundry 20 liquid frost). " +
                "Fuel only burns while something is baking, so nothing is wasted. Off: only Fuel to keep.");
            _fuelLevel = Config.Bind("General", "Fuel to keep", 3,
                new ConfigDescription("With Keep the fuel full off: how much fuel the oven keeps in stock while baking.", new AcceptableValueRange<int>(1, 20)));
            _empty = Config.Bind("General", "Empty into chest", true,
                "Baked food (and the frost foundry's finished gear) goes into a nearby chest: one that already has it, otherwise the nearest one with room.");
            _xp = Config.Bind("General", "Cooking XP", true, "You get Cooking XP and the chance of bonus food, as if you took the food out by hand.");
            _bulkFuel = Config.Bind("General", "Shift+E adds max fuel", true,
                "Hold Shift while adding fuel (wood to an oven, liquid frost to the frost foundry) to fill it up: from your inventory first, " +
                "then from the chests around you, as far as Craft From Chests reaches (50 m without it).");
            _ovenSpeed = Config.Bind("General", "Stone oven speed multiplier", 1f, new ConfigDescription(
                "How fast stone ovens bake: 2 = twice as fast, 1 = normal. Works right away. The wood burns that much faster too, so baking costs the same wood.",
                new AcceptableValueRange<float>(0.25f, 10f)));
            _foundrySpeed = Config.Bind("General", "Frost foundry speed multiplier", 1f, new ConfigDescription(
                "How fast the frost foundry forges: 2 = twice as fast, 1 = normal. Works right away. Its liquid frost burns that much " +
                "faster too, so forging costs the same.", new AcceptableValueRange<float>(0.25f, 10f)));
            PatchModule(typeof(Plugin));
        }

        [HarmonyPatch(typeof(CookingStation), "Awake")]
        [HarmonyPostfix]
        private static void OvenAwake(CookingStation __instance)
        {
            if (__instance.m_useFuel) Ovens.Add(__instance);
        }

        [HarmonyPatch(typeof(CookingStation), "GetDeltaTime")]
        [HarmonyPostfix]
        private static void OvenSpeed(CookingStation __instance, ref float __result)
        {
            if (_ovenSpeed.Value != 1f && __instance.name.StartsWith(StoneOven, System.StringComparison.Ordinal)) __result *= _ovenSpeed.Value;
            else if (_foundrySpeed.Value != 1f && __instance.name.StartsWith(FrostFoundry, System.StringComparison.Ordinal)) __result *= _foundrySpeed.Value;
        }

        [HarmonyPatch(typeof(Container), "Awake")]
        [HarmonyPostfix]
        private static void ChestAwake(Container __instance) => Chests.Add(__instance);

        // --- Shift+E: as much fuel as fits ---------------------------------------------------------------------

        private static bool ShiftHeld() => ZInput.GetKey(KeyCode.LeftShift) || ZInput.GetKey(KeyCode.RightShift);

        [HarmonyPatch(typeof(CookingStation), "OnAddFuelSwitch")]
        [HarmonyPrefix]
        private static bool AddFuel(CookingStation __instance, Humanoid user, ItemDrop.ItemData item, ref bool __result)
        {
            if (!_bulkFuel.Value || user == null || !ShiftHeld() || !__instance.m_useFuel || __instance.m_fuelItem == null) return true;
            if (CraftFromChestsRuns()) return true; // it fills up itself, with its own chest rules
            var fuelName = __instance.m_fuelItem.m_itemData.m_shared.m_name;
            if (item != null && item.m_shared.m_name != fuelName) return true;
            var view = OvenView(__instance);
            if (view == null || !view.IsValid()) return true;
            var room = __instance.m_maxFuel - Mathf.CeilToInt(view.GetZDO().GetFloat(ZDOVars.s_fuel));
            if (room <= 0) return true; // full: the game's own message
            var carried = Mathf.Min(room, user.GetInventory().CountItems(fuelName));
            var chests = room > carried && user == Player.m_localPlayer ? UsableChests(__instance, user.transform.position, HandRange(), _fuelRange.Value) : new List<Container>();
            if (carried <= 1 && chests.Count == 0) return true; // one or none and no chests: the game's own add and messages
            if (carried > 0) user.GetInventory().RemoveItem(fuelName, carried);
            var fromChests = 0;
            while (carried + fromChests < room && StoreRules.TakeOne(chests, i => i.m_shared.m_name == fuelName, out _)) fromChests++;
            var amount = carried + fromChests;
            if (amount == 0) return true; // none anywhere: the game's own "you have none" message
            for (var i = 0; i < amount; i++) view.InvokeRPC("RPC_AddFuel");
            user.Message(MessageHud.MessageType.Center, $"$msg_added {fuelName} x{amount}" + (fromChests > 0 ? $" ({fromChests} from chests)" : ""));
            __result = true;
            return false;
        }

        [HarmonyPatch(typeof(CookingStation), "OnHoverFuelSwitch")]
        [HarmonyPostfix]
        private static void HoverFuel(CookingStation __instance, ref string __result)
        {
            if (_bulkFuel.Value && __instance.m_useFuel && !string.IsNullOrEmpty(__result))
                __result += Localization.instance.Localize("\n[<color=yellow><b>Shift + $KEY_Use</b></color>] Add max");
        }

        private void Update()
        {
            if (!_enabled.Value || Player.m_localPlayer == null || ZNet.instance == null) return;
            _timer -= Time.deltaTime;
            if (_timer > 0f) return;
            _timer = 2f;
            // Ultimate Builder's creative world: its stations stand still, nothing is taken from chests or filled there.
            if (DigiCore.CreativeWorld.Active) return;
            // One save per changed chest for the whole round (StoreRules.HoldSaves).
            StoreRules.HoldSaves();
            try
            {
                Ovens.RemoveWhere(o => o == null);
                Chests.RemoveWhere(c => c == null);
                foreach (var oven in Ovens)
                {
                    var view = OvenView(oven);
                    if (view == null || !view.IsValid() || !view.IsOwner()) continue;
                    try
                    {
                        Run(oven, view.GetZDO(), view);
                    }
                    catch (System.Exception e)
                    {
                        Logger.LogWarning($"Oven at {oven.transform.position}: {e.Message}");
                    }
                }
            }
            finally
            {
                StoreRules.SaveHeld();
            }
        }

        private static void Run(CookingStation oven, ZDO zdo, ZNetView view)
        {
            // Output, input and fuel each from their own range (looked up when needed).
            var chests = UsableChests(oven, _range.Value);
            List<Container> input = null, fuelChests = null;
            var slots = oven.m_slots.Length;

            // 1. Finished food out.
            if (_empty.Value)
            {
                for (var i = 0; i < slots; i++)
                {
                    var item = zdo.GetString("slot" + i);
                    if (item == "" || zdo.GetInt("slotstatus" + i) == NotDone) continue;
                    var prefab = ObjectDB.instance.GetItemPrefab(item);
                    if (prefab == null) continue;
                    var burnt = oven.m_overCookedItem != null && item == oven.m_overCookedItem.name;
                    var player = Player.m_localPlayer;
                    var amount = 1;
                    if (_xp.Value && !burnt && oven.m_canGiveBonusYield &&
                        Random.value < player.GetSkillFactor(oven.m_skill) * InventoryGui.instance.m_craftBonusChance)
                        amount += InventoryGui.instance.m_craftBonusAmount;
                    var target = StoreRules.PickOutputChest(chests, prefab, amount) ?? StoreRules.PickOutputChest(chests, prefab, 1);
                    if (target == null) break; // no room anywhere: leave it in the oven (no XP until it is taken out)
                    if (!target.GetInventory().CanAddItem(prefab, amount)) amount = 1;
                    target.GetInventory().AddItem(prefab, amount);
                    ClearSlot(zdo, view, i);
                    if (_xp.Value && !burnt && oven.m_skill != Skills.SkillType.None) player.RaiseSkill(oven.m_skill, 0.6f);
                }
            }

            // 2. Empty slots filled.
            if (_fill.Value)
            {
                for (var i = 0; i < slots; i++)
                {
                    if (zdo.GetString("slot" + i) != "") continue;
                    input = input ?? UsableChests(oven, _inputRange.Value);
                    if (!StoreRules.TakeOne(input, item => oven.m_conversion.Any(c => c.m_from != null && c.m_from.name == item.m_dropPrefab?.name), out var taken))
                        break;
                    view.InvokeRPC("RPC_AddItem", taken.m_dropPrefab.name, taken.m_cheated);
                    if (_xp.Value && oven.m_skill != Skills.SkillType.None) Player.m_localPlayer.RaiseSkill(oven.m_skill, 0.4f);
                }
            }

            // 3. Fuel while something is baking (slots fill through an RPC, so count the ZDO after it ran).
            if (_fuel.Value && oven.m_fuelItem != null && Baking(zdo, slots))
            {
                var fuelName = oven.m_fuelItem.m_itemData.m_shared.m_name;
                var fuel = zdo.GetFloat(ZDOVars.s_fuel);
                // A piece only while a whole one fits (RPC_AddFuel itself does not stop at the max).
                var want = _fuelFull.Value ? oven.m_maxFuel : Mathf.Min(_fuelLevel.Value, oven.m_maxFuel);
                fuelChests = fuelChests ?? UsableChests(oven, _fuelRange.Value);
                while (fuel <= want - 1 && StoreRules.TakeOne(fuelChests, item => item.m_shared.m_name == fuelName, out _))
                {
                    view.InvokeRPC("RPC_AddFuel");
                    fuel += 1f;
                }
            }
        }

        private static bool Baking(ZDO zdo, int slots)
        {
            for (var i = 0; i < slots; i++)
                if (zdo.GetString("slot" + i) != "" && zdo.GetInt("slotstatus" + i) == NotDone)
                    return true;
            return false;
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
        // Craft From Chests (same combined mod, reached by name: Auto Oven is also a Hexium single without it).
        private static System.Reflection.FieldInfo _cfcRunning, _cfcRange;
        private static bool _cfcResolved;

        private static void ResolveCraftFromChests()
        {
            if (_cfcResolved) return;
            _cfcResolved = true;
            var plugin = AccessTools.TypeByName("CraftFromChests.Plugin");
            if (plugin == null) return;
            _cfcRunning = AccessTools.Field(plugin, "Running");
            _cfcRange = AccessTools.Field(plugin, "Range");
        }

        private static bool CraftFromChestsRuns()
        {
            ResolveCraftFromChests();
            return _cfcRunning != null && (bool)_cfcRunning.GetValue(null);
        }

        // How far filling by hand reaches: Craft From Chests' range (it took over Local Craft From Storage's), else 50 m.
        private static float HandRange()
        {
            ResolveCraftFromChests();
            return _cfcRange?.GetValue(null) is ConfigEntry<float> range ? range.Value : 50f;
        }

        private static List<Container> UsableChests(CookingStation oven, float range) => UsableChests(oven, null, 0f, range);

        // Chests in the oven's range (edge to edge), and with a player position also every chest within handRange of the
        // player (then nearest to the player first).
        private static List<Container> UsableChests(CookingStation oven, Vector3? player, float handRange, float range)
        {
            var me = Game.instance.GetPlayerProfile().GetPlayerID();
            var at = oven.transform.position;
            var edge = BoundsOf(oven);
            var reach = range + 10f; // a chest's centre is never further than this from the oven when its edge is in range
            var result = new List<Container>();
            var dists = new Dictionary<Container, float>();
            foreach (var chest in Chests)
            {
                if (chest == null) continue; // destroyed or unloaded since the last clean-up
                var byHand = player.HasValue && (chest.transform.position - player.Value).sqrMagnitude <= handRange * handRange;
                if (!byHand && (chest.transform.position - at).sqrMagnitude > reach * reach) continue;
                if (chest.m_wagon != null || chest.GetComponentInParent<Ship>() != null || chest.GetComponent<TombStone>() != null) continue;
                var piece = ChestPiece(chest);
                if (piece == null) continue; // only built chests
                var dist = byHand ? Vector3.Distance(player.Value, chest.transform.position) : Gap(edge, BoundsOf(chest));
                if (!byHand && dist > range) continue;
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
                dists[chest] = dist;
            }
            return result.OrderBy(c => dists[c]).ToList(); // no tuples: Valheim's runtime lacks System.ValueTuple
        }

        // The space a piece takes up (its solid colliders), and the gap between two such boxes (0 when they touch).
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
