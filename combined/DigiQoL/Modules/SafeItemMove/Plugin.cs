// Safety net for inventory drag and drop. When a stack is dropped onto a slot holding a different item, Valheim
// removes the dragged stack first, moves the other item away, and only then adds the dragged stack to the slot. If
// anything interferes in between (a full inventory, another mod refusing the slot), the dragged stack is gone without
// an error. This counts the involved items before and after every drop and gives back anything that vanished, then
// logs what happened. Drops into or out of world containers are only logged, since those can finish later over the
// network (MultiUserChest) and must not be duplicated.
// Also no phantom items: Valheim leaves the container panel showing the last chest after the inventory closes, and
// only brings it up to date from the frame after the inventory opens again. So the first frame of the opening shows
// that old chest's items (in the backpack panel, or in the next chest you open). Right after the inventory opens, the
// panel now shows the right contents. Light: one grid update per opening.
// Also no accidental Place stacks: in the game, holding E on a chest opens it, puts every stack you carry of an item the
// chest already holds into it, and closes it again. That hold does nothing now (setting), and the chest's hover text no
// longer says "(Hold to stack)". The Place stacks button in the chest window still works.
// Place stacks (the chest window's button, and the hold if it is switched back on) leaves your hotbar alone (setting):
// the game only skipped equipped items, so a spare tool or a stack on the hotbar went into the chest. Those items are
// set aside for the moment Place stacks runs and put back right after (the way Equipment Slots keeps its slots).
using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SafeItemMove
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.safeitemmove";
        public const string Name = "Digi's Safe Item Move";
        public const string Version = "1.0.3";

        private static ManualLogSource _log;
        private static readonly AccessTools.FieldRef<InventoryGui, Container> CurrentContainer =
            AccessTools.FieldRefAccess<InventoryGui, Container>("m_currentContainer");
        private static readonly AccessTools.FieldRef<InventoryGui, ItemDrop.ItemData> DragItem =
            AccessTools.FieldRefAccess<InventoryGui, ItemDrop.ItemData>("m_dragItem");

        private sealed class Snapshot
        {
            public Inventory From, To;
            public readonly Dictionary<string, int> Counts = new Dictionary<string, int>();
            public readonly Dictionary<string, ItemDrop.ItemData> Samples = new Dictionary<string, ItemDrop.ItemData>();
            public bool Container;
            public string Description;
        }

        // The plugin ID changed from pieter.valheim.* to digi.valheim.*: carry the old settings file over once.
        private void MigrateConfig()
        {
            var old = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, Guid.Replace("digi.valheim.", "pieter.valheim.") + ".cfg");
            if (System.IO.File.Exists(Config.ConfigFilePath) || !System.IO.File.Exists(old)) return;
            System.IO.File.Copy(old, Config.ConfigFilePath);
            Config.Reload();
        }


        private static BepInEx.Configuration.ConfigEntry<bool> _holdToStack, _keepHotbar;
        private static readonly Stack<List<ItemDrop.ItemData>> SetAside = new Stack<List<ItemDrop.ItemData>>();

        private void Awake()
        {
            MigrateConfig();
            _holdToStack = Config.Bind("General", "Hold E to stack", false,
                "Holding E on a chest puts every stack you carry of an item the chest already holds into it, and closes the " +
                "chest again (the game's own hold). Off: holding E just opens the chest. The Place stacks button still works.");
            _keepHotbar = Config.Bind("General", "Place stacks keeps the hotbar", true,
                "Place stacks in a chest window never takes what lies on your hotbar (the top row of your inventory).");
            _log = Logger;
            PatchModule(typeof(Plugin));
            Logger.LogInfo("Watching inventory drag and drop for vanished items.");
        }

        // After every opening (also the second Show that opens a backpack or swaps in a chest): the panel shows the
        // container that is open now, or hides until the game shows it (a chest this game does not own yet).
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Show))]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Shown(InventoryGui __instance)
        {
            try
            {
                var panel = __instance.m_container;
                var grid = __instance.ContainerGrid;
                if (panel == null || grid == null) return;
                var container = CurrentContainer(__instance);
                var inventory = container != null && container.IsOwner() ? container.GetInventory() : null;
                if (inventory == null)
                {
                    panel.gameObject.SetActive(false);
                    return;
                }
                grid.UpdateInventory(inventory, null, DragItem(__instance));
                if (__instance.m_containerName != null && Localization.instance != null)
                    __instance.m_containerName.text = Localization.instance.Localize(inventory.GetName());
            }
            catch (Exception e)
            {
                _log?.LogWarning("Could not refresh the container panel: " + e.Message); // the game refreshes it a frame later anyway
            }
        }

        [HarmonyPatch(typeof(InventoryGrid), nameof(InventoryGrid.DropItem))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void Before(InventoryGrid __instance, Inventory fromInventory, ItemDrop.ItemData item, int amount, Vector2i pos, out Snapshot __state)
        {
            __state = null;
            var to = __instance?.GetInventory();
            if (to == null || fromInventory == null || item?.m_shared == null) return;
            try
            {
                var target = to.GetItemAt(pos.x, pos.y);
                var s = new Snapshot { From = fromInventory, To = to };
                foreach (var it in new[] { item, target })
                {
                    if (it?.m_shared == null || s.Counts.ContainsKey(it.m_shared.m_name)) continue;
                    s.Counts[it.m_shared.m_name] = Count(s, it.m_shared.m_name);
                    s.Samples[it.m_shared.m_name] = it.Clone();
                }
                var container = InventoryGui.instance != null ? CurrentContainer(InventoryGui.instance) : null;
                var containerInv = container != null ? container.GetInventory() : null;
                s.Container = containerInv != null && (fromInventory == containerInv || to == containerInv);
                s.Description = $"{item.m_shared.m_name} x{amount} from ({item.m_gridPos.x},{item.m_gridPos.y}) " +
                                $"to ({pos.x},{pos.y})" + (target != null ? $" holding {target.m_shared.m_name} x{target.m_stack}" : "");
                __state = s;
            }
            catch (Exception e)
            {
                _log.LogWarning("Could not snapshot a drop: " + e.Message);
            }
        }

        [HarmonyPatch(typeof(InventoryGrid), nameof(InventoryGrid.DropItem))]
        [HarmonyFinalizer]
        [HarmonyPriority(Priority.Last)]
        private static Exception After(Exception __exception, Snapshot __state)
        {
            if (__state == null) return __exception;
            try
            {
                foreach (var pair in __state.Counts)
                {
                    var missing = pair.Value - Count(__state, pair.Key);
                    if (missing <= 0) continue;
                    var name = pair.Key;
                    if (__state.Container)
                    {
                        _log.LogWarning($"{name} x{missing} not back yet after moving {__state.Description} (container, left alone)");
                        continue;
                    }
                    _log.LogWarning($"{name} x{missing} vanished while moving {__state.Description}; giving it back.");
                    GiveBack(__state, __state.Samples[pair.Key], missing);
                }
            }
            catch (Exception e)
            {
                _log.LogWarning("Could not check a drop: " + e.Message);
            }
            return __exception;
        }

        private static int Count(Snapshot s, string name)
        {
            var total = Sum(s.From, name);
            if (s.To != s.From) total += Sum(s.To, name);
            return total;
        }

        private static int Sum(Inventory inventory, string name)
        {
            var total = 0;
            foreach (var it in inventory.GetAllItems())
                if (it?.m_shared != null && it.m_shared.m_name == name) total += it.m_stack;
            return total;
        }

        private static void GiveBack(Snapshot s, ItemDrop.ItemData sample, int missing)
        {
            var perStack = Math.Max(1, sample.m_shared.m_maxStackSize);
            while (missing > 0)
            {
                var copy = sample.Clone();
                copy.m_stack = Math.Min(perStack, missing);
                missing -= copy.m_stack;
                if (s.From.AddItem(copy) || (s.To != s.From && s.To.AddItem(copy))) continue;

                var player = Player.m_localPlayer;
                if (player == null) continue;
                var where = player.transform.position + player.transform.forward + Vector3.up;
                ItemDrop.DropItem(copy, copy.m_stack, where, Quaternion.identity);
                _log.LogWarning($"No room for {copy.m_shared.m_name} x{copy.m_stack}; dropped it at your feet.");
            }
        }

        // The game's hold on a chest: after the window opened, holding E places stacks, then closes it. Its state is set to
        // "done" right after the window opens, so neither happens.
        [HarmonyPatch(typeof(InventoryGui), "UpdateContainer")]
        [HarmonyPostfix]
        private static void NoHoldToStack(InventoryGui __instance)
        {
            if (_holdToStack != null && !_holdToStack.Value && __instance.m_containerHoldState == 0) __instance.m_containerHoldState = 2;
        }

        [HarmonyPatch(typeof(Inventory), nameof(Inventory.StackAll))]
        [HarmonyPrefix]
        private static void HotbarAside(Inventory fromInventory)
        {
            List<ItemDrop.ItemData> aside = null;
            var player = Player.m_localPlayer;
            if (_keepHotbar != null && _keepHotbar.Value && player != null && fromInventory != null && fromInventory == player.m_inventory)
            {
                var items = fromInventory.m_inventory;
                for (var i = items.Count - 1; i >= 0; i--)
                {
                    if (items[i] == null || items[i].m_gridPos.y != 0) continue;
                    (aside ?? (aside = new List<ItemDrop.ItemData>())).Add(items[i]);
                    items.RemoveAt(i);
                }
            }
            SetAside.Push(aside);
        }

        [HarmonyPatch(typeof(Inventory), nameof(Inventory.StackAll))]
        [HarmonyFinalizer]
        private static void HotbarBack(Inventory fromInventory)
        {
            var aside = SetAside.Count > 0 ? SetAside.Pop() : null;
            if (aside == null || fromInventory == null) return;
            fromInventory.m_inventory.AddRange(aside);
            fromInventory.Changed();
        }

        [HarmonyPatch(typeof(Container), nameof(Container.GetHoverText))]
        [HarmonyPostfix]
        private static void NoHoldHint(ref string __result)
        {
            if (_holdToStack == null || _holdToStack.Value || string.IsNullOrEmpty(__result) || Localization.instance == null) return;
            var hint = Localization.instance.Localize("$msg_stackall_hover");
            if (!string.IsNullOrEmpty(hint)) __result = __result.Replace(" " + hint, "").Replace(hint, "");
        }
    }
}
