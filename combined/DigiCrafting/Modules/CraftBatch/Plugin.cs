// Vanilla Shift+Craft makes 5 at once (InventoryGui.m_multiCraftAmount). This makes Ctrl+Shift+Craft make 20: the game
// uses that one number for the button label, the cost check, the payment, the output and the skill XP, so everything
// scales together (Local Craft From Storage reads it too). The amount is locked in when crafting starts, so letting go
// of Ctrl during the crafting animation does not change it.
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace CraftBatch
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.craftbatch";
        public const string Name = "Digi's Craft Batch";
        public const string Version = "1.0.3";

        private static ConfigEntry<int> _shiftAmount;
        private static ConfigEntry<int> _bigAmount;
        private static ConfigEntry<KeyCode> _bigKey;

        private static readonly AccessTools.FieldRef<InventoryGui, float> CraftTimer = AccessTools.FieldRefAccess<InventoryGui, float>("m_craftTimer");

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
            _shiftAmount = Config.Bind("Crafting", "Shift amount", 5,
                new ConfigDescription("How many Shift+Craft makes at once (normal 5).", new AcceptableValueRange<int>(1, 100)));
            _bigAmount = Config.Bind("Crafting", "Ctrl+Shift amount", 20,
                new ConfigDescription("How many Ctrl+Shift+Craft makes at once.", new AcceptableValueRange<int>(1, 100)));
            _bigKey = Config.Bind("Crafting", "Big batch key", KeyCode.LeftControl,
                "Hold this key together with Shift when you press Craft to make the big batch (Right Ctrl works too when this is Left Ctrl).");
            PatchModule(typeof(Plugin));
        }

        private static bool BigHeld()
        {
            var input = UnityInput.Current;
            return input.GetKey(_bigKey.Value) || (_bigKey.Value == KeyCode.LeftControl && input.GetKey(KeyCode.RightControl));
        }

        private static void SetAmount(InventoryGui gui) =>
            gui.m_multiCraftAmount = BigHeld() ? _bigAmount.Value : _shiftAmount.Value;

        // Label and cost preview follow the keys, but only while nothing is being crafted.
        [HarmonyPatch(typeof(InventoryGui), "UpdateRecipe")]
        [HarmonyPrefix]
        private static void UpdateRecipe(InventoryGui __instance)
        {
            if (CraftTimer(__instance) < 0f) SetAmount(__instance);
        }

        // Lock in the amount when crafting starts.
        [HarmonyPatch(typeof(InventoryGui), "OnCraftPressed")]
        [HarmonyPrefix]
        private static void OnCraftPressed(InventoryGui __instance) => SetAmount(__instance);

        // Big batches (and Local Craft From Storage's "have/need" counts like 511/200) are wider than the material
        // amount field, so the last digit was cut off. The text now shrinks to fit when, and only when, it is too wide.
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.SetupRequirement))]
        [HarmonyPostfix]
        private static void FitAmount(Transform elementRoot)
        {
            var amount = elementRoot != null ? elementRoot.Find("res_amount")?.GetComponent<TMPro.TMP_Text>() : null;
            if (amount == null || amount.enableAutoSizing) return;
            amount.fontSizeMax = amount.fontSize;
            amount.fontSizeMin = amount.fontSize * 0.5f;
            amount.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
            amount.enableAutoSizing = true;
        }
    }
}
