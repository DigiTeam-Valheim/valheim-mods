// HUD Grid: the bottom-left of the HUD tidied. Better Archery's quiver slots, Valheim Legends' ability icons and CDEV
// Equipment Slots' quick slots, each drawn by its own mod in its own place and style, go into one grid beside the health
// bar: arrows on top, abilities in the middle, potions at the bottom, columns lined up, every slot in a hotbar-like look
// (key small top left, count at the bottom; no ability names). By default the slots are like the game's food squares
// left of the health bar (their size, each row level with one of them, as far apart): food, bar and grid as one block.
// "Use slot size" switches to a size of its own, the grid standing on the bottom of the health panel. Whatever is not
// there leaves no gap. Without any of those mods it does nothing. The layout is in Grid.cs; switched off, everything
// goes back at once. Light: about ten rectangles a frame, no patches.
using BepInEx.Configuration;

namespace HudGrid
{
    public class Plugin : DigiCore.DigiModule
    {
        private static ConfigEntry<float> _slotSize;
        private static ConfigEntry<bool> _useSlotSize;

        private void Awake()
        {
            _useSlotSize = Config.Bind("General", "Use slot size", false,
                "Off (default): the slots as big as the food squares left of the health bar, each row level with one of them " +
                "and as far apart as they are: food, health bar and grid as one block. On: every slot as big as Slot size, the " +
                "grid standing on the bottom of the health panel.");
            _slotSize = Config.Bind("General", "Slot size", 56f, new ConfigDescription(
                "With Use slot size on: how big each slot of the grid is, in the HUD's pixels (the game's hotbar slots are about " +
                "64). The game's HUD scale applies on top. Works right away.", new AcceptableValueRange<float>(32f, 96f)));
            Grid.SlotSize = () => _slotSize.Value;
            Grid.LikeFood = () => !_useSlotSize.Value;
        }

        // After every Update: those mods put their parts back in place each frame.
        private void LateUpdate() => Grid.Apply();

        private void OnDestroy()
        {
            Grid.Restore();
            Grid.SlotSize = () => 56f;
            Grid.LikeFood = () => true;
        }
    }
}
