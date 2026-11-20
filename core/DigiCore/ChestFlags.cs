// Per-chest settings shared by the combined mods, stored on the chest itself (its network data), so every player and
// every mod sees the same. Set with the checkboxes in the chest window.
using UnityEngine;

namespace DigiCore
{
    public static class ChestFlags
    {
        // "Store Nearby" checkbox unticked: the player's Store Nearby button skips this chest. Everything else (the auto
        // stations, crafting and building from chests) uses it as normal.
        public const string NoStore = "digi_nostore";

        // "Take from it" checkbox unticked: crafting, building and filling from chests, the auto stations and Planting
        // Tools' replanting leave this chest's contents alone. Store Nearby still fills it.
        public const string NoTake = "digi_notake";

        public static bool StoreBlocked(Container chest) => Get(chest, NoStore);

        public static bool TakeBlocked(Container chest) => Get(chest, NoTake);

        private static bool Get(Container chest, string key)
        {
            if (chest == null) return false;
            var view = chest.GetComponent<ZNetView>();
            if (view == null && chest.m_rootObjectOverride != null) view = chest.m_rootObjectOverride;
            return view != null && view.IsValid() && view.GetZDO().GetBool(key);
        }
    }
}
