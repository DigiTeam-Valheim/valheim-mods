// The chest choice of the auto stations (and Planting Tools' replanting): output goes into a chest that already holds that
// item (nearest first), and only otherwise into the nearest chest with room; input is taken nearest first. A chest whose
// "Take from it" checkbox is unticked (ChestFlags.NoTake) is never emptied by them. There are no marker slots any more
// (Local Craft From Storage's wood-arrow markers are gone with it): every slot is a normal slot.
// Two chest rules, off by default (Tigertron on Discord, coming from Lazy Vikings, 2026-11-21), in the shared section
// "Craft From Chests - Chest rules" ("Chest rules" in a Hexium single; bound by every sub-mod that uses them, on the
// plugin's own file, so there is one switch per mod, shown with Craft From Chests on the wiki and in F1):
// - Leave 1 in each chest: nothing takes the last one of an item from a chest (here TakeOne for the stations;
//   DigiStorage.Chests for crafting, building and filling by hand), so a chest kept for one item stays that item's chest.
// - Only store into chests that already have it: station output never goes into a chest without that item; with none,
//   each station does what it does when every chest is full (waits, or drops like the game).
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

    private static BepInEx.Configuration.ConfigEntry<bool> _leaveOne, _onlyMatching;

    internal static bool LeaveOne => _leaveOne != null && _leaveOne.Value;

    internal static bool OnlyMatching => _onlyMatching != null && _onlyMatching.Value;

    // Binding the same entry again returns the one already bound (BepInEx), so every sub-mod may call this.
    internal static void BindRules(DigiCore.ModuleConfig config)
    {
        var file = config.File;
        var section = config.Standalone ? "Chest rules" : "Craft From Chests - Chest rules";
        _leaveOne = file.Bind(section, "Leave 1 in each chest", false,
            "Crafting, building, filling by hand and the automated stations never take the last one of an item from a chest: a " +
            "chest kept for one item (wood, iron, a mead) keeps one, so it stays that item's chest. Off: all of it can be used.");
        _onlyMatching = file.Bind(section, "Only store into chests that already have it", false,
            "The automated stations put what they make only into a chest that already holds that item. Off: when no chest has " +
            "it, it goes into the nearest chest with room. With no chest for it, fermenters, ovens, cooking stations, beehives " +
            "and sap extractors keep it (food only drops just before it burns); smelters, kilns and the like drop it as in the game.");
    }

    // The last one of this item in this chest stays there ("Leave 1 in each chest").
    internal static bool KeepsLast(Inventory inv, ItemDrop.ItemData item) =>
        LeaveOne && item?.m_shared != null && inv.CountItems(item.m_shared.m_name, -1, false) <= 1;

    // Chest for the output, chests nearest first: one that already holds this item, else (unless "Only store into chests
    // that already have it") the nearest with room.
    internal static Container PickOutputChest(List<Container> nearestFirst, GameObject prefab, int amount)
    {
        var shared = prefab.GetComponent<ItemDrop>()?.m_itemData.m_shared.m_name;
        var usable = nearestFirst.Where(c => c.GetInventory().CanAddItem(prefab, amount)).ToList();
        var matching = usable.FirstOrDefault(c => c.GetInventory().GetAllItems().Any(i => i?.m_shared != null && i.m_shared.m_name == shared));
        return matching ?? (OnlyMatching ? null : usable.FirstOrDefault());
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

    // ---- cheap checks for the stations' rounds ----
    // Auto Fermenter took 70-170 ms every 2 s in a base of 4000 pieces (Lag Meter, 2026-11-20): every round compared
    // each item in every chest in range with each conversion by Unity's .name, which asks the engine and makes a new
    // string on every call, and walked each chest's parents for a ship. Prefab names never change and a chest never
    // moves onto a ship: both are worked out once.
    private static readonly Dictionary<GameObject, string> Names = new Dictionary<GameObject, string>();

    internal static string NameOf(GameObject prefab)
    {
        if (prefab == null) return null;
        if (!Names.TryGetValue(prefab, out var name))
        {
            if (Names.Count > 20000) Names.Clear(); // only prefabs land here; a guard against anything else
            Names[prefab] = name = prefab.name;
        }
        return name;
    }

    internal static string NameOf(Component prefab) => prefab == null ? null : NameOf(prefab.gameObject);

    // A chest on a cart or a ship, or a grave: never used by the stations.
    private static readonly Dictionary<Container, bool> Mobile = new Dictionary<Container, bool>();
    private static int _mobilePruneAt = 512;

    internal static bool CartShipOrGrave(Container chest)
    {
        if (Mobile.TryGetValue(chest, out var result)) return result;
        if (Mobile.Count >= _mobilePruneAt)
        {
            foreach (var gone in Mobile.Keys.Where(c => c == null).ToList()) Mobile.Remove(gone);
            _mobilePruneAt = Math.Max(512, Mobile.Count * 2);
        }
        result = chest.m_wagon != null || chest.GetComponentInParent<Ship>() != null || chest.GetComponent<TombStone>() != null;
        Mobile[chest] = result;
        return result;
    }

    // What a piece bumps into, in world space (measure: the caller's own, from its solid colliders; DigiCore is built into
    // mods without the physics module). Measured again only when the piece moved: GetComponentsInChildren and
    // collider.bounds per chest per station per round added up.
    private sealed class Box
    {
        public Vector3 At;
        public Bounds Bounds;
    }

    private static readonly Dictionary<Component, Box> Boxes = new Dictionary<Component, Box>();
    private static int _boxPruneAt = 512;

    internal static Bounds SolidBounds(Component owner, Func<Component, Bounds> measure)
    {
        var at = owner.transform.position;
        if (Boxes.TryGetValue(owner, out var box) && box.At == at) return box.Bounds;
        if (box == null && Boxes.Count >= _boxPruneAt)
        {
            foreach (var gone in Boxes.Keys.Where(c => c == null).ToList()) Boxes.Remove(gone);
            _boxPruneAt = Math.Max(512, Boxes.Count * 2);
        }
        var bounds = measure(owner);
        Boxes[owner] = new Box { At = at, Bounds = bounds };
        return bounds;
    }

    // The prefab names a station takes in (its conversion list), made once per list.
    private sealed class InputSet
    {
        public int Count;
        public HashSet<string> Names;
    }

    private static readonly Dictionary<System.Collections.IList, InputSet> InputSets = new Dictionary<System.Collections.IList, InputSet>();

    internal static HashSet<string> InputNames<T>(List<T> conversions, Func<T, Component> from)
    {
        if (conversions == null) return new HashSet<string>();
        if (!InputSets.TryGetValue(conversions, out var set) || set.Count != conversions.Count)
        {
            if (InputSets.Count > 2000) InputSets.Clear(); // one per station that ever loaded
            var names = new HashSet<string>();
            foreach (var c in conversions)
            {
                var source = c != null ? from(c) : null;
                if (source != null) names.Add(NameOf(source));
            }
            InputSets[conversions] = set = new InputSet { Count = conversions.Count, Names = names };
        }
        return set.Names;
    }

    // Takes one matching item from the chests (nearest first), skipping chests that may not be emptied.
    internal static bool TakeOne(List<Container> nearestFirst, Func<ItemDrop.ItemData, bool> match, out ItemDrop.ItemData taken)
    {
        foreach (var chest in nearestFirst)
        {
            if (!CanWithdraw(chest)) continue;
            var inv = chest.GetInventory();
            var item = inv.GetAllItems().FirstOrDefault(i => match(i) && !KeepsLast(inv, i));
            if (item == null) continue;
            taken = item.Clone();
            inv.RemoveOneItem(item);
            return true;
        }
        taken = null;
        return false;
    }
}
