// The "Crafting" checkbox in chest windows (where the title was, before Store Nearby's box): unticked, nothing takes from
// that chest (crafting, building, filling, the auto stations, Planting Tools' replanting). Kept on the chest, for every
// player. (It was the bare "Take from it" box until 2026-11-17.)
using DigiStorage;
using HarmonyLib;

namespace CraftFromChests
{
    internal static class TakeCheckbox
    {
        private const string Tooltip =
            "<color=yellow>Ticked</color> (normal): crafting, building, filling fires and stations, the auto stations and " +
            "replanting may take materials from this chest.\n" +
            "<color=yellow>Unticked</color>: they leave it alone.\n\n" +
            "Store Nearby can still put things into it (that is the other box).";

        private static ChestCheckbox _box;

        private static ChestCheckbox Box =>
            _box ?? (_box = new ChestCheckbox(ChestCheckbox.TakeName, DigiCore.ChestFlags.NoTake, "Crafting", "Crafting takes from it", Tooltip));

        internal static void Clear()
        {
            _box?.Destroy();
            _box = null;
        }

        internal static void SettingChanged(object sender, System.EventArgs e)
        {
            if (InventoryGui.instance != null) Box.Refresh(InventoryGui.instance, Plugin.ShowCheckbox.Value);
        }

        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Show))]
        [HarmonyPostfix]
        private static void Shown(InventoryGui __instance) => Box.Refresh(__instance, Plugin.ShowCheckbox.Value);

        [HarmonyPatch(typeof(InventoryGui), "Update")]
        [HarmonyPostfix]
        private static void Updated(InventoryGui __instance)
        {
            if (InventoryGui.IsVisible()) Box.Update(__instance, Plugin.ShowCheckbox.Value);
        }
    }
}
