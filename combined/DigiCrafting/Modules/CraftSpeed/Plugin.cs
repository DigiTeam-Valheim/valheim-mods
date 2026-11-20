// Craft Speed: how fast crafting goes at a station, and on its own how fast recycling goes in Recycle Tab's Recycle tab.
// 100% = the game's own time (2 s for one item, 6 s for a Shift batch, longer for upgrading at some stations; the
// crafting skill still shortens it as normal); 200% = twice as fast. Only the crafting timer runs faster: costs, results
// and skill XP are unchanged.
// Light: one multiplication per frame, and only while something is being crafted.
using System;
using BepInEx.Configuration;
using HarmonyLib;

namespace CraftSpeed
{
    public class Plugin : DigiCore.DigiModule
    {
        private static ConfigEntry<int> _craft, _recycle;
        private static Func<bool> _recycling;
        private static bool _looked;

        private void Awake()
        {
            _craft = Config.Bind("General", "Crafting speed (%)", 100,
                new ConfigDescription("How fast crafting and upgrading items goes. 100 = normal (2 seconds for one item, 6 for a " +
                                      "Shift batch, before your crafting skill makes it shorter); 200 = twice as fast, 50 = half as fast.",
                    new AcceptableValueRange<int>(10, 1000)));
            _recycle = Config.Bind("General", "Recycling speed (%)", 100,
                new ConfigDescription("How fast recycling goes in the Recycle tab (the Recycle Tab sub-mod of Crafting and " +
                                      "Automation). 100 = normal (as long as crafting the item); 200 = twice as fast.",
                    new AcceptableValueRange<int>(10, 1000)));
            PatchModule(typeof(Plugin));
        }

        // Recycle Tab's "the Recycle tab is open", looked up by name: it can be in another plugin (Hexium's single mods).
        private static bool Recycling()
        {
            if (!_looked)
            {
                _looked = true;
                var getter = AccessTools.PropertyGetter(AccessTools.TypeByName("RecycleTab.Plugin"), "Active");
                if (getter != null && getter.IsStatic && getter.ReturnType == typeof(bool))
                    _recycling = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), getter);
            }
            return _recycling != null && _recycling();
        }

        // The game adds the frame time to the crafting timer; a faster speed adds more.
        [HarmonyPatch(typeof(InventoryGui), "UpdateRecipe")]
        [HarmonyPrefix]
        private static void Faster(InventoryGui __instance, ref float dt)
        {
            if (__instance.m_craftTimer < 0f) return;
            var percent = Recycling() ? _recycle.Value : _craft.Value;
            if (percent != 100) dt *= percent / 100f;
        }
    }
}
