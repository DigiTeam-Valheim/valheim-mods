// Epic Loot Storage Materials, "Deposit from nearby chests": a second button under the Storage tab's "Deposit All". It
// moves the enchanting materials and shardstones (whatever the Storage can hold) from nearby chests into the table's
// Storage, up to the room its Storage level allows per item; the rest stays in the chests. The chests are the ones the
// table already uses for its costs (the Range setting from you, built chests nobody has open that you may open). The
// deposit is Epic Loot's own: one request to the table's owner, and anything it did not take comes back to you, as with
// Deposit All (TableSend: if the deposit fails, the items go back into their chests). Light: runs only when the button
// is pressed.
using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using EpicLoot_UnityLib;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EpicLootStorageMaterials
{
    internal static class DepositFromChests
    {
        private const string ButtonName = "DepositFromChestsButton";

        private static ConfigEntry<bool> _enabled;
        private static ManualLogSource _log;
        private static readonly FieldInfo StashField = AccessTools.Field(typeof(StashUI), "_stash");

        internal static void Bind(DigiCore.ModuleConfig config)
        {
            _enabled = config.Bind("General", "Deposit from nearby chests button", true,
                "A \"Deposit from nearby chests\" button under the Storage tab's Deposit All: moves enchanting materials and " +
                "shardstones from nearby chests (Range) into the table's Storage, up to the room its Storage level allows. " +
                "The rest stays in the chests.");
        }

        internal static void Patch(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            var onEnable = AccessTools.Method(typeof(StashUI), "OnEnable");
            var refresh = AccessTools.Method(typeof(StashUI), nameof(StashUI.Refresh));
            if (!TableSend.Ready(log) || onEnable == null || refresh == null || StashField == null)
            {
                log.LogInfo("Deposit from nearby chests is off: Epic Loot's table storage was not found.");
                return;
            }
            harmony.Patch(onEnable, postfix: new HarmonyMethod(typeof(DepositFromChests), nameof(AddButton)));
            harmony.Patch(refresh, postfix: new HarmonyMethod(typeof(DepositFromChests), nameof(UpdateButton)));
        }

        // A copy of Deposit All, right under it.
        private static void AddButton(StashUI __instance)
        {
            try
            {
                var main = __instance.MainButton;
                if (main == null) return;
                var parent = main.transform.parent;
                var existing = parent.Find(ButtonName);
                if (existing != null)
                {
                    existing.gameObject.SetActive(_enabled.Value);
                    UpdateButton(__instance);
                    return;
                }
                if (!_enabled.Value) return;
                var copy = UnityEngine.Object.Instantiate(main.gameObject, parent, false);
                copy.name = ButtonName;
                var rect = (RectTransform)copy.transform;
                var source = (RectTransform)main.transform;
                rect.anchoredPosition = source.anchoredPosition - new Vector2(0f, source.rect.height + 6f);
                for (var i = 0; i < copy.transform.childCount; i++)
                {
                    var child = copy.transform.GetChild(i);
                    if (child.name.StartsWith("Hint", StringComparison.Ordinal)) child.gameObject.SetActive(false); // gamepad hint
                }
                foreach (var text in copy.GetComponentsInChildren<TMP_Text>(true))
                    if (text.GetComponent<GamepadGlyph>() == null) text.text = "Deposit from nearby chests";
                var button = copy.GetComponent<Button>();
                button.onClick = new Button.ButtonClickedEvent();
                var ui = __instance;
                button.onClick.AddListener(() => Deposit(ui));
                UpdateButton(__instance);
            }
            catch (Exception e)
            {
                _log?.LogWarning("Could not add the Deposit from nearby chests button: " + e.Message);
            }
        }

        // Usable when the table's Storage may be filled, like Deposit All (which also needs items in your inventory).
        private static void UpdateButton(StashUI __instance)
        {
            var button = __instance.MainButton != null ? __instance.MainButton.transform.parent.Find(ButtonName)?.GetComponent<Button>() : null;
            if (button == null) return;
            var stash = StashField.GetValue(__instance) as EnchantingTableStash;
            button.interactable = stash != null && stash.CanDeposit && stash.HasAccess();
        }

        private static void Deposit(StashUI ui)
        {
            try
            {
                var player = Player.m_localPlayer;
                var stash = StashField.GetValue(ui) as EnchantingTableStash;
                if (player == null || stash == null || !stash.CanDeposit) return;
                if (!stash.HasAccess(true))
                {
                    player.Message(MessageHud.MessageType.Center, "$mod_epicloot_storage_warded");
                    return;
                }
                if (ui.Audio != null && ui.MainActionSFX != null) ui.Audio.PlayOneShot(ui.MainActionSFX);
                var deltas = new Dictionary<string, int>();
                var taken = new List<TableSend.Taken>();
                var moved = 0;
                foreach (var chest in Plugin.ChestsNear(player))
                {
                    var inventory = chest.GetInventory();
                    foreach (var item in new List<ItemDrop.ItemData>(inventory.GetAllItems()))
                    {
                        if (item?.m_shared == null || item.m_stack <= 0 || !StashCatalog.IsStashable(item)) continue;
                        var prefab = StashCatalog.GetPrefabName(item);
                        if (prefab == null) continue;
                        deltas.TryGetValue(prefab, out var pending);
                        var room = stash.GetRoom(prefab);
                        var amount = room == int.MaxValue ? item.m_stack : Mathf.Min(item.m_stack, room - pending);
                        if (amount <= 0) continue;
                        TableSend.Take(inventory, item, amount, taken);
                        deltas[prefab] = pending + amount;
                        moved += amount;
                    }
                }
                if (moved == 0)
                {
                    player.Message(MessageHud.MessageType.Center, "$mod_epicloot_storage_nothingtodeposit");
                    return;
                }
                TableSend.Send(stash, deltas, taken, player); // gives everything back to the chests if it fails
                player.Message(MessageHud.MessageType.TopLeft, Localization.instance.Localize("$mod_epicloot_storage_deposited", moved.ToString()));
                ui.Refresh();
            }
            catch (Exception e)
            {
                _log?.LogWarning("Deposit from nearby chests failed: " + e.Message);
            }
        }
    }
}
