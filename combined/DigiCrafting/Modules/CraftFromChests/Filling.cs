// Filling fires and stations from the chests when your bag has none of what they take:
// - Fires, torches, braziers, hearths (Fireplace.Interact) and cooking stations' fuel (CookingStation.OnAddFuelSwitch):
//   the game's own code runs, with your bag counting the chests that are ours and the item taken from them. With Shift
//   (setting) they are filled up: from the bag first, then the chests in range (around you, like crafting).
// - Smelter-like stations (smelter, kilns, furnaces, windmill, spinning wheel, eitr refinery): their input and their fuel;
//   the first thing the station takes, in its own order. With Shift (setting) the station is filled completely, from
//   the bag first and then the chests.
// - Ovens, cooking stations and the frost foundry: what they cook or forge, when you use one with nothing for it in
//   your bag (Local Craft From Storage only did their fuel).
// - Fermenters: mead base from the chests when an empty one is used.
// Multiplayer: a chest that is not ours yet is asked for, with a short message; the next press takes from it.
using System.Collections.Generic;
using DigiStorage;
using HarmonyLib;
using UnityEngine;

namespace CraftFromChests
{
    internal static class Filling
    {
        internal static int Depth; // inside a fill: the bag counts and pays from the chests that are ours
        private static readonly Stack<bool> Fills = new Stack<bool>();

        private static bool On(Humanoid user) => Plugin.Stations.Value && user != null && user == Player.m_localPlayer;

        private static bool Shift =>
            Plugin.ShiftFill.Value && (ZInput.GetKey(KeyCode.LeftShift, false) || ZInput.GetKey(KeyCode.RightShift, false));

        private static string Named(string token) => Localization.instance != null ? Localization.instance.Localize(token) : token;

        // Begins a fill when the bag has none of this item but a chest of ours has (or asks for one that is not ours yet).
        private static bool Begin(Humanoid user, ItemDrop fuel)
        {
            if (!On(user) || fuel == null) return false;
            var name = fuel.m_itemData.m_shared.m_name;
            if (Plugin.Own(user.GetInventory(), name) > 0) return false; // the game's own way, from the bag
            var area = Plugin.Here();
            if (Chests.CountReady(area, name) > 0)
            {
                Depth++;
                Linking.Depth++;
                Linking.ReadyOnly = true;
                return true;
            }
            if (Chests.Count(area, name) > 0 && Chests.Ask(area, name, 1))
                Plugin.Say($"Getting {Named(name)} from a chest: try again");
            return false;
        }

        private static void End()
        {
            if (Fills.Count == 0 || !Fills.Pop()) return;
            Depth--;
            Linking.Depth--;
            Linking.ReadyOnly = false;
        }

        // Shift: puts in as much as fits, from the bag first, then the chests (nearest first). True when it is done (or asked
        // for a chest that is not ours yet): result is then what the game's own add returns.
        private static bool FillUp(Humanoid user, ItemDrop fuel, int room, System.Action<int> add, ref bool result)
        {
            if (fuel == null || room <= 0) return false;
            var name = fuel.m_itemData.m_shared.m_name;
            var bag = user.GetInventory();
            var fromBag = Mathf.Min(Plugin.Own(bag, name), room);
            if (fromBag > 0) bag.RemoveItem(name, fromBag);
            var area = Plugin.Here();
            var fromChests = room > fromBag ? Chests.Take(area, name, room - fromBag) : 0;
            var added = fromBag + fromChests;
            if (added == 0)
            {
                if (Chests.Count(area, name) == 0 || !Chests.Ask(area, name, 1)) return false; // the game says you have none
                Plugin.Say($"Getting {Named(name)} from a chest: try again");
                result = false;
                return true;
            }
            add(added);
            user.Message(MessageHud.MessageType.Center, "$msg_added " + name + " x" + added + (fromChests > 0 ? $" ({fromChests} from chests)" : ""));
            result = true;
            return true;
        }

        // ---- the game's own code, with the chests as part of the bag (Shift: filled up)

        [HarmonyPatch(typeof(Fireplace), nameof(Fireplace.Interact))]
        [HarmonyPrefix]
        private static bool FireStart(Fireplace __instance, Humanoid user, bool hold, bool alt, ref bool __result)
        {
            var refuel = __instance.m_canRefill && !__instance.m_infiniteFuel && __instance.m_fuelItem != null;
            if (refuel && !hold && On(user) && Plugin.ShiftFill.Value && (alt || Shift) && __instance.m_nview != null && __instance.m_nview.IsValid())
            {
                Fills.Push(false);
                var room = Mathf.FloorToInt(__instance.m_maxFuel) - Mathf.CeilToInt(__instance.m_nview.GetZDO().GetFloat(ZDOVars.s_fuel));
                var view = __instance.m_nview;
                if (FillUp(user, __instance.m_fuelItem, room, n => { for (var i = 0; i < n; i++) view.InvokeRPC("RPC_AddFuel"); }, ref __result)) return false;
                return true;
            }
            Fills.Push(refuel && Begin(user, __instance.m_fuelItem));
            return true;
        }

        [HarmonyPatch(typeof(Fireplace), nameof(Fireplace.Interact))]
        [HarmonyFinalizer]
        private static void FireDone() => End();

        [HarmonyPatch(typeof(CookingStation), "OnAddFuelSwitch")]
        [HarmonyPrefix]
        private static bool CookFuelStart(CookingStation __instance, Humanoid user, ItemDrop.ItemData item, ref bool __result)
        {
            if (item == null && On(user) && Shift && __instance.m_useFuel && __instance.m_fuelItem != null)
            {
                Fills.Push(false);
                var room = __instance.m_maxFuel - Mathf.CeilToInt(__instance.GetFuel());
                var view = __instance.m_nview;
                if (FillUp(user, __instance.m_fuelItem, room, n => { for (var i = 0; i < n; i++) view.InvokeRPC("RPC_AddFuel"); }, ref __result)) return false;
                return true;
            }
            Fills.Push(item == null && Begin(user, __instance.m_fuelItem));
            return true;
        }

        [HarmonyPatch(typeof(CookingStation), "OnAddFuelSwitch")]
        [HarmonyFinalizer]
        private static void CookFuelDone() => End();

        // ---- smelter-like stations

        [HarmonyPatch(typeof(Smelter), "OnAddFuel")]
        [HarmonyPrefix]
        private static bool SmelterFuel(Smelter __instance, Humanoid user, ItemDrop.ItemData item, ref bool __result)
        {
            if (!On(user) || item != null || __instance.m_fuelItem == null) return true;
            var name = __instance.m_fuelItem.m_itemData.m_shared.m_name;
            var bag = user.GetInventory();
            var own = Plugin.Own(bag, name);
            var shift = Shift;
            if (own > 0 && !shift) return true; // the game's own: one from the bag
            var room = __instance.m_maxFuel - Mathf.CeilToInt(__instance.GetFuel());
            if (room <= 0) return true; // the game says it is full
            var want = shift ? room : 1;
            var fromBag = Mathf.Min(own, want);
            if (fromBag > 0) bag.RemoveItem(name, fromBag);
            var area = Plugin.Here();
            var added = fromBag + (want > fromBag ? Chests.Take(area, name, want - fromBag) : 0);
            if (added == 0)
            {
                if (Chests.Count(area, name) == 0 || !Chests.Ask(area, name, 1)) return true; // the game says you have none
                Plugin.Say($"Getting {Named(name)} from a chest: try again");
                __result = false;
                return false;
            }
            for (var i = 0; i < added; i++) __instance.m_nview.InvokeRPC("RPC_AddFuel");
            user.Message(MessageHud.MessageType.Center, "$msg_added " + name + (added > 1 ? " x" + added : ""));
            __result = true;
            return false;
        }

        [HarmonyPatch(typeof(Smelter), "OnAddOre")]
        [HarmonyPrefix]
        private static bool SmelterInput(Smelter __instance, Humanoid user, ItemDrop.ItemData item, ref bool __result)
        {
            if (!On(user) || item != null) return true;
            var bag = user.GetInventory();
            var shift = Shift;
            if (!shift && __instance.FindCookableItem(bag) != null) return true; // the game's own: one from the bag
            var room = __instance.m_maxOre - __instance.GetQueueSize();
            if (room <= 0) return true; // the game says it is full
            var want = shift ? room : 1;
            var added = 0;
            string last = null;
            while (added < want) // the bag first, in the station's own order
            {
                var it = __instance.FindCookableItem(bag);
                if (it == null || it.m_dropPrefab == null || !__instance.IsItemAllowed(it.m_dropPrefab.name)) break;
                if (!bag.RemoveItem(it, 1)) break;
                __instance.m_nview.InvokeRPC("RPC_AddOre", it.m_dropPrefab.name, it.m_cheated);
                added++;
                last = it.m_shared.m_name;
            }
            var area = Plugin.Here();
            foreach (var conversion in __instance.m_conversion)
            {
                if (added >= want) break;
                var from = conversion?.m_from;
                if (from == null || !__instance.IsItemAllowed(from.gameObject.name)) continue;
                var got = Chests.Take(area, from.m_itemData.m_shared.m_name, want - added);
                for (var i = 0; i < got; i++) __instance.m_nview.InvokeRPC("RPC_AddOre", from.gameObject.name, false);
                if (got <= 0) continue;
                added += got;
                last = from.m_itemData.m_shared.m_name;
            }
            if (added == 0)
            {
                foreach (var conversion in __instance.m_conversion)
                {
                    var from = conversion?.m_from;
                    if (from == null || Chests.Count(area, from.m_itemData.m_shared.m_name) == 0) continue;
                    if (!Chests.Ask(area, from.m_itemData.m_shared.m_name, 1)) break;
                    Plugin.Say($"Getting {Named(from.m_itemData.m_shared.m_name)} from a chest: try again");
                    __result = false;
                    return false;
                }
                return true; // the game says there is nothing to add
            }
            user.Message(MessageHud.MessageType.Center, "$msg_added " + last + (added > 1 ? " x" + added : ""));
            __instance.m_addedOreTime = Time.time;
            if (__instance.m_addOreAnimationDuration > 0f) __instance.SetAnimation(true);
            __result = true;
            return false;
        }

        // ---- cooking stations, ovens and the frost foundry: what they cook or forge

        // Using one (or its "add" switch) with nothing for it in your bag puts the first thing it takes (in its own order)
        // in from the chests: dough into an oven, an unforged staff into the frost foundry, meat onto a cooking station.
        [HarmonyPatch(typeof(CookingStation), "OnInteract")]
        [HarmonyPrefix]
        private static bool CookFromChest(CookingStation __instance, Humanoid user, ref bool __result)
        {
            if (!On(user) || __instance.HaveDoneItem() || __instance.FindCookableItem(user.GetInventory()) != null) return true;
            if ((__instance.m_requireFire && !__instance.IsFireLit()) || __instance.GetFreeSlot() == -1) return true; // the game's own message
            var area = Plugin.Here();
            foreach (var conversion in __instance.m_conversion)
            {
                var from = conversion?.m_from;
                if (from == null || !__instance.IsItemAllowed(from.gameObject.name)) continue;
                if (Chests.Take(area, from.m_itemData.m_shared.m_name, 1) != 1) continue;
                __instance.m_nview.InvokeRPC("RPC_AddItem", from.gameObject.name, false);
                __result = true;
                return false;
            }
            foreach (var conversion in __instance.m_conversion)
            {
                var from = conversion?.m_from;
                if (from == null || Chests.Count(area, from.m_itemData.m_shared.m_name) == 0) continue;
                if (!Chests.Ask(area, from.m_itemData.m_shared.m_name, 1)) break;
                Plugin.Say($"Getting {Named(from.m_itemData.m_shared.m_name)} from a chest: try again");
                __result = false;
                return false;
            }
            return true; // nothing for it anywhere: the game says so
        }

        // ---- fermenters

        [HarmonyPatch(typeof(Fermenter), nameof(Fermenter.Interact))]
        [HarmonyPrefix]
        private static bool Ferment(Fermenter __instance, Humanoid user, bool hold, ref bool __result)
        {
            if (hold || !On(user) || __instance.GetStatus() != Fermenter.Status.Empty) return true;
            if (!PrivateArea.CheckAccess(__instance.transform.position, 0f, false)) return true;
            __instance.UpdateCover(0f, true);
            if (!__instance.m_hasRoof || __instance.m_exposed || __instance.FindCookableItem(user.GetInventory()) != null) return true;
            var area = Plugin.Here();
            foreach (var conversion in __instance.m_conversion)
            {
                var from = conversion?.m_from;
                if (from == null) continue;
                var name = from.m_itemData.m_shared.m_name;
                if (Chests.Take(area, name, 1) != 1) continue;
                __instance.m_nview.InvokeRPC("RPC_AddItem", from.gameObject.name.GetStableHashCode(), false);
                user.Message(MessageHud.MessageType.Center, "$msg_added " + name);
                __result = true;
                return false;
            }
            foreach (var conversion in __instance.m_conversion)
            {
                var from = conversion?.m_from;
                if (from == null || Chests.Count(area, from.m_itemData.m_shared.m_name) == 0) continue;
                if (!Chests.Ask(area, from.m_itemData.m_shared.m_name, 1)) break;
                Plugin.Say($"Getting {Named(from.m_itemData.m_shared.m_name)} from a chest: try again");
                __result = false;
                return false;
            }
            return true;
        }
    }
}
