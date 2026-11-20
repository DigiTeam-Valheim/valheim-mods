// Farm Performance, harvesting. A big field picked at once (Plant Easily's Shift+E picks every crop around you in the
// same frame) nearly froze the game: every crop played its pick effect (and, at a good Farming skill, its bonus effect
// and "+1" text) and threw each harvested item into the world as its own physics object with its own network object,
// which you then picked up again a moment later.
// Now, when you pick a crop yourself, what it gives goes straight into your inventory, with the game's usual pickup
// message ("Barley x24"); what does not fit falls out as before. And per frame only the first few picks play their
// effects, bonus effects and bonus texts; the bonus yield itself is never lost. A crop another player picks (yours on the
// network, theirs to pick up) still drops its items as always.
// Every item added to your inventory makes the game go through your whole inventory and rebuild your known recipes and
// buildable pieces (and other mods react too); a field's worth of items in one frame did that a hundred times. While you
// interact (one Shift+E is one interact) your inventory's "changed" is held and run once at the end, and the pickup
// messages are added up into one per kind of item.
using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FarmPerformance
{
    internal static class Harvest
    {
        private static ConfigEntry<bool> _direct;
        private static ConfigEntry<int> _effectsPerFrame;
        private static readonly EffectList NoEffects = new EffectList();
        private static int _frame = -1, _picks, _interacts;
        private static bool _localPick, _muteText;
        private static Pickable _pickSwapped, _bonusSwapped;
        private static EffectList _pickSaved, _bonusSaved;

        // One interact of yours: the inventory's changes held, the pickup messages added up.
        private sealed class Picked
        {
            public ItemDrop.ItemData Item;
            public int Amount;
        }

        private static int _batch;
        private static bool _changeHeld;
        private static readonly Dictionary<string, Picked> Messages = new Dictionary<string, Picked>();
        private static readonly MethodInfo InventoryChanged = AccessTools.Method(typeof(Inventory), "Changed", new[] { typeof(bool), typeof(bool) });

        internal static void Bind(DigiCore.ModuleConfig config)
        {
            _direct = config.Bind("Harvest", "Harvest straight into inventory", true,
                "What you pick from a crop (and berries, mushrooms, ...) goes straight into your inventory, instead of each item " +
                "flying out as its own physics object that you then pick up. Saves a lot when harvesting a whole field at once " +
                "(Plant Easily's Shift+E). What does not fit falls out as before.");
            _effectsPerFrame = config.Bind("Harvest", "Pick effects per frame", 6, new ConfigDescription(
                "Picking many plants at once: only this many per frame play their pick effect, bonus effect and bonus \"+1\" " +
                "text; the rest are picked without them (the bonus yield is still given). 0: no pick effects at all.",
                new AcceptableValueRange<int>(0, 100)));
        }

        [HarmonyPatch(typeof(Player), "Interact", new[] { typeof(GameObject), typeof(bool), typeof(bool) })]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)] // before Plant Easily's prefix, which picks the whole field
        private static void BeginBatch(Player __instance)
        {
            if (__instance == Player.m_localPlayer && InventoryChanged != null) _batch++;
        }

        [HarmonyPatch(typeof(Player), "Interact", new[] { typeof(GameObject), typeof(bool), typeof(bool) })]
        [HarmonyFinalizer]
        private static Exception EndBatch(Player __instance, Exception __exception)
        {
            if (__instance != Player.m_localPlayer || _batch == 0) return __exception;
            if (--_batch > 0) return __exception;
            try
            {
                if (_changeHeld)
                {
                    _changeHeld = false;
                    InventoryChanged.Invoke(__instance.GetInventory(), new object[] { false, false });
                }
                foreach (var picked in Messages.Values) __instance.ShowPickupMessage(picked.Item, picked.Amount);
            }
            catch (Exception e)
            {
                // The game goes on; the inventory catches up at its next change.
                UnityEngine.Debug.LogWarning("[Farm Performance] Updating the inventory after a harvest failed: " + e.Message);
            }
            finally
            {
                Messages.Clear();
            }
            return __exception;
        }

        [HarmonyPatch(typeof(Inventory), "Changed", new[] { typeof(bool), typeof(bool) })]
        [HarmonyPrefix]
        private static bool HoldChange(Inventory __instance)
        {
            if (_batch == 0 || Player.m_localPlayer == null || __instance != Player.m_localPlayer.GetInventory()) return true;
            _changeHeld = true;
            return false;
        }

        private static void NewFrame()
        {
            if (_frame == Time.frameCount) return;
            _frame = Time.frameCount;
            _picks = 0;
            _interacts = 0;
        }

        // Interact: the bonus effect and "+1" text past the frame's limit are skipped (the bonus amount is not).
        [HarmonyPatch(typeof(Pickable), nameof(Pickable.Interact))]
        [HarmonyPrefix]
        private static void BeforeInteract(Pickable __instance)
        {
            NewFrame();
            if (++_interacts <= _effectsPerFrame.Value || _bonusSwapped != null) return;
            _bonusSwapped = __instance;
            _bonusSaved = __instance.m_bonusEffect;
            __instance.m_bonusEffect = NoEffects;
            _muteText = true;
        }

        [HarmonyPatch(typeof(Pickable), nameof(Pickable.Interact))]
        [HarmonyFinalizer]
        private static Exception AfterInteract(Exception __exception)
        {
            if (_bonusSwapped != null) _bonusSwapped.m_bonusEffect = _bonusSaved;
            _bonusSwapped = null;
            _bonusSaved = null;
            _muteText = false;
            return __exception;
        }

        [HarmonyPatch(typeof(DamageText), nameof(DamageText.ShowText), new[] { typeof(DamageText.TextType), typeof(Vector3), typeof(string), typeof(bool) })]
        [HarmonyPrefix]
        private static bool BonusText() => !_muteText;

        // The pick itself (run where the crop is owned, so for your own crops on your PC): the pick effect past the
        // frame's limit is skipped; picked by you, the items go to your inventory (Drop below).
        [HarmonyPatch(typeof(Pickable), "RPC_Pick")]
        [HarmonyPrefix]
        private static void BeforePick(Pickable __instance, long __0)
        {
            NewFrame();
            _localPick = _direct.Value && Player.m_localPlayer != null && __0 == ZDOMan.GetSessionID();
            if (++_picks <= _effectsPerFrame.Value || _pickSwapped != null) return;
            _pickSwapped = __instance;
            _pickSaved = __instance.m_pickEffector;
            __instance.m_pickEffector = NoEffects;
        }

        [HarmonyPatch(typeof(Pickable), "RPC_Pick")]
        [HarmonyFinalizer]
        private static Exception AfterPick(Exception __exception)
        {
            if (_pickSwapped != null) _pickSwapped.m_pickEffector = _pickSaved;
            _pickSwapped = null;
            _pickSaved = null;
            _localPick = false;
            return __exception;
        }

        // Each item the pick gives: into your inventory when it fits, as the game's own auto-pickup would put it there.
        [HarmonyPatch(typeof(Pickable), "Drop")]
        [HarmonyPrefix]
        private static bool BeforeDrop(GameObject __0, int __2)
        {
            if (!_localPick || __0 == null) return true;
            var player = Player.m_localPlayer;
            var drop = __0.GetComponent<ItemDrop>();
            if (player == null || drop == null) return true;
            var stack = Mathf.Max(1, __2);
            var item = drop.m_itemData.Clone();
            item.m_dropPrefab = __0;
            item.m_stack = stack;
            item.m_worldLevel = (byte)Game.m_worldLevel;
            var inventory = player.GetInventory();
            if (!inventory.CanAddItem(item, stack) || !inventory.AddItem(item)) return true;
            if (_batch > 0)
            {
                var key = item.m_shared.m_name + "/" + item.m_quality;
                if (Messages.TryGetValue(key, out var picked)) picked.Amount += stack;
                else Messages[key] = new Picked { Item = item, Amount = stack };
            }
            else player.ShowPickupMessage(item, stack);
            return false;
        }
    }
}
