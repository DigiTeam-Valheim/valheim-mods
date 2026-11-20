// The chest choice of the auto stations (and Planting Tools' replanting): output goes into a chest that already holds that
// item (nearest first), and only otherwise into the nearest chest with room; input is taken nearest first. A chest whose
// "Take from it" checkbox is unticked (ChestFlags.NoTake) is never emptied by them. There are no marker slots any more
// (Local Craft From Storage's wood-arrow markers are gone with it): every slot is a normal slot.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

internal static class StoreRules
{
    internal static bool CanDeposit(Inventory inv) => true;

    internal static bool CanWithdraw(Inventory inv) => true;

    internal static bool CanWithdraw(Container chest) => !DigiCore.ChestFlags.TakeBlocked(chest);

    internal static bool IsProtected(Inventory inv, ItemDrop.ItemData item) => false;

    internal static bool IsProtectedSlot(Inventory inv, int x, int y) => false;

    // For many slot checks on one chest: null = no slot is protected.
    internal static Func<int, int, bool> ProtectedSlots(Inventory inv) => null;

    // Chest for the output, chests nearest first: one that already holds this item, else the nearest with room.
    internal static Container PickOutputChest(List<Container> nearestFirst, GameObject prefab, int amount)
    {
        var shared = prefab.GetComponent<ItemDrop>()?.m_itemData.m_shared.m_name;
        var usable = nearestFirst.Where(c => c.GetInventory().CanAddItem(prefab, amount)).ToList();
        var matching = usable.FirstOrDefault(c => c.GetInventory().GetAllItems().Any(i => i?.m_shared != null && i.m_shared.m_name == shared));
        return matching ?? usable.FirstOrDefault();
    }

    // ---- one save per chest per round ----
    // The game saves a chest's whole contents after every change (Container.OnContainerChanged -> Save: every item
    // serialized). Taking one by one (50 ore = 50 removals) saved the chest 50 times: up to 1.1 s in a single frame when
    // several stations refilled at once (Lag Meter, 2026-11-18). Between HoldSaves and SaveHeld those saves wait, and each
    // chest that changed is saved once at the end (in the same frame: nothing reads the chest in between).
    private static int _holdSaves;
    private static readonly HashSet<Container> HeldSaves = new HashSet<Container>();
    private static System.Reflection.MethodInfo _save;
    private static bool _savePatched;

    internal static void HoldSaves()
    {
        if (!_savePatched)
        {
            _savePatched = true;
            _save = HarmonyLib.AccessTools.Method(typeof(Container), "Save");
            // One patch per mod that compiles this file in, each with its own count (it does nothing outside a round).
            if (_save != null)
                new HarmonyLib.Harmony("digi.storerules.saves." + typeof(StoreRules).Assembly.GetName().Name)
                    .Patch(_save, prefix: new HarmonyLib.HarmonyMethod(typeof(StoreRules), nameof(SaveLater)));
        }
        _holdSaves++;
    }

    internal static void SaveHeld()
    {
        if (--_holdSaves > 0) return;
        _holdSaves = 0;
        if (HeldSaves.Count == 0) return;
        var chests = HeldSaves.ToList();
        HeldSaves.Clear();
        foreach (var chest in chests)
        {
            if (chest == null || _save == null) continue;
            try { _save.Invoke(chest, null); }
            catch (Exception e) { Debug.LogWarning("[Digi] chest save failed: " + (e.InnerException ?? e).Message); }
        }
    }

    private static bool SaveLater(Container __instance)
    {
        if (_holdSaves <= 0 || __instance == null) return true;
        HeldSaves.Add(__instance);
        return false;
    }

    // Takes one matching item from the chests (nearest first), skipping chests that may not be emptied.
    internal static bool TakeOne(List<Container> nearestFirst, Func<ItemDrop.ItemData, bool> match, out ItemDrop.ItemData taken)
    {
        foreach (var chest in nearestFirst)
        {
            if (!CanWithdraw(chest)) continue;
            var inv = chest.GetInventory();
            var item = inv.GetAllItems().FirstOrDefault(match);
            if (item == null) continue;
            taken = item.Clone();
            inv.RemoveOneItem(item);
            return true;
        }
        taken = null;
        return false;
    }
}
