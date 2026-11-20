// Ultimate Builder's creative world (DigiCreative, a package of its own): whether this game is in it right now. Read
// from that mod by reflection (its _active, set when a world starts), so nothing here needs it installed. Used by the
// automation sub-mods: in the creative world stations stand still and nothing is taken from chests.
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace DigiCore
{
    internal static class CreativeWorld
    {
        private const string Guid = "digi.valheim.creativeworld";
        private static FieldInfo _active;
        private static bool _looked;

        internal static bool Active
        {
            get
            {
                if (!_looked)
                {
                    // Looked up once, after every plugin has loaded (the first call comes from an Update).
                    _looked = true;
                    if (Chainloader.PluginInfos.TryGetValue(Guid, out var info) && info.Instance != null)
                        _active = AccessTools.Field(info.Instance.GetType(), "_active");
                }
                return _active != null && (bool)_active.GetValue(null);
            }
        }
    }
}
