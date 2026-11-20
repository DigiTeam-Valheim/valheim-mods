// Quiver Toggle: Better Archery hangs the equipped quiver on the character's back (a copy of the quiver model on the
// back-bow attach point, one per character). This hides that model: on your own character, and/or on other players.
// Only what you see changes; the quiver still works and other players still see theirs as they set it.
// Light: set when Better Archery makes the model, and on a setting change; nothing runs per frame.
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace QuiverToggle
{
    public class Plugin : DigiCore.DigiModule
    {
        private static ConfigEntry<bool> _own, _others;
        private static AccessTools.FieldRef<Dictionary<Humanoid, GameObject>> _instances;

        private void Awake()
        {
            _own = Config.Bind("General", "Show your quiver", true, "The quiver on your own character's back. Off = hidden.");
            _others = Config.Bind("General", "Show quivers on other players", true, "The quivers on other players' backs. Off = hidden.");
            _own.SettingChanged += (_, __) => ApplyAll();
            _others.SettingChanged += (_, __) => ApplyAll();

            var patches = AccessTools.TypeByName("BetterArchery.Patches");
            var create = patches != null ? AccessTools.Method(patches, "CreateOrUpdateQuiverVisual") : null;
            var field = patches != null ? AccessTools.Field(patches, "QuiverInstances") : null;
            if (create == null || field == null || field.FieldType != typeof(Dictionary<Humanoid, GameObject>))
            {
                Logger.LogWarning("Better Archery's quiver model was not found (other version?); quivers stay as they are.");
                return;
            }
            _instances = AccessTools.StaticFieldRefAccess<Dictionary<Humanoid, GameObject>>(field);
            ModuleHarmony.Patch(create, postfix: new HarmonyMethod(typeof(Plugin), nameof(Created)));
            ApplyAll();
        }

        private void OnDestroy()
        {
            if (_instances == null) return;
            foreach (var model in _instances().Values)
                if (model != null) model.SetActive(true);
        }

        private static void Created(Humanoid __0) => Apply(__0);

        private static void ApplyAll()
        {
            if (_instances == null) return;
            foreach (var humanoid in new List<Humanoid>(_instances().Keys)) Apply(humanoid);
        }

        private static void Apply(Humanoid humanoid)
        {
            if (humanoid == null || _instances == null || !_instances().TryGetValue(humanoid, out var model) || model == null) return;
            model.SetActive(humanoid == Player.m_localPlayer ? _own.Value : _others.Value);
        }
    }
}
