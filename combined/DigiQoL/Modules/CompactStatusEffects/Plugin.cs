// Compact Status Effects: the row of effect icons top right takes less room. Smaller icons (70%) in a tighter row, no
// names under them (the timers and counters stay), and the effects that never change are left out: those of what you
// wear (feather cape, wisp, set bonuses, ...) and the forsaken powers that are always on (Passive Forsaken Powers). What
// comes and goes (rested, shelter, wet, cold, poison, potions, cooldowns) stays. More can be hidden by name.
// The game rebuilds that row from a fresh list each frame: the hidden effects are taken out of the list first
// (Hud.UpdateStatusEffects prefix), then the icons it made are scaled and moved closer together (postfix). Only the HUD
// changes: the effects themselves work as before. Light: a few icons per frame.
using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace CompactStatusEffects
{
    public class Plugin : DigiCore.DigiModule
    {
        private const string PowerPrefix = "GP_";

        private static ConfigEntry<float> _size;
        private static ConfigEntry<bool> _names, _gear, _powers;
        private static ConfigEntry<string> _hideNames;
        private static HashSet<string> _hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<int> Gear = new HashSet<int>();
        private static bool _running;

        private void Awake()
        {
            _size = Config.Bind("General", "Icon size (%)", 70f, new ConfigDescription(
                "How big the effect icons top right are (100 = the game's size); the row gets tighter with them.",
                new AcceptableValueRange<float>(40f, 100f)));
            _names = Config.Bind("General", "Show names", false, "The effect's name under its icon. Off: only the icon and its timer or counter.");
            _gear = Config.Bind("General", "Hide gear effects", true,
                "Effects that come from what you wear are not shown (feather fall from the feather cape, the wisp's light, set bonuses, ...).");
            _powers = Config.Bind("General", "Hide passive forsaken powers", true,
                "Forsaken powers that are always on (Passive Forsaken Powers) are not shown. A power you use still shows while it runs.");
            _hideNames = Config.Bind("General", "Also hide", "",
                "More effects to leave out, by name, separated by commas (as shown under the icon, e.g. \"Shelter, Wet\").");
            _hideNames.SettingChanged += (s, e) => ReadNames();
            ReadNames();
            PatchModule(typeof(Plugin));
            _running = true;
        }

        private void OnDestroy()
        {
            _running = false;
            var hud = Hud.instance;
            if (hud == null) return;
            // back to the game's look; the hidden effects come back as the game rebuilds the row next frame
            for (var i = 0; i < hud.m_statusEffects.Count; i++) Place(hud, hud.m_statusEffects[i], i, 1f, true);
        }

        private static void ReadNames()
        {
            _hidden = new HashSet<string>((_hideNames.Value ?? "").Split(',').Select(n => n.Trim()).Where(n => n.Length > 0),
                StringComparer.OrdinalIgnoreCase);
        }

        [HarmonyPatch(typeof(Hud), "UpdateStatusEffects")]
        [HarmonyPrefix]
        private static void Filter(List<StatusEffect> statusEffects)
        {
            if (!_running || statusEffects == null || statusEffects.Count == 0) return;
            Gear.Clear();
            var player = Player.m_localPlayer;
            if (_gear.Value && player != null)
                foreach (var se in player.m_equipmentStatusEffects)
                    if (se != null) Gear.Add(se.NameHash());
            statusEffects.RemoveAll(Hidden);
        }

        private static bool Hidden(StatusEffect se)
        {
            if (se == null) return false;
            if (_powers.Value && se.m_ttl <= 0f && se.name.StartsWith(PowerPrefix, StringComparison.Ordinal)) return true;
            if (_gear.Value && Gear.Contains(se.NameHash())) return true;
            if (_hidden.Count == 0) return false;
            return _hidden.Contains(se.name) || (Localization.instance != null && _hidden.Contains(Localization.instance.Localize(se.m_name)));
        }

        [HarmonyPatch(typeof(Hud), "UpdateStatusEffects")]
        [HarmonyPostfix]
        private static void Shrink(Hud __instance)
        {
            if (!_running) return;
            var scale = _size.Value / 100f;
            for (var i = 0; i < __instance.m_statusEffects.Count; i++) Place(__instance, __instance.m_statusEffects[i], i, scale, _names.Value);
        }

        // One icon: its size, its spot in the (tighter) row, its name shown or not.
        private static void Place(Hud hud, RectTransform icon, int index, float scale, bool name)
        {
            if (icon == null) return;
            var perRow = Mathf.Max(1, hud.m_effectsPerRow);
            var row = index / perRow;
            var column = index - row * perRow;
            var spacing = hud.m_statusEffectSpacing * scale;
            var size = new Vector3(scale, scale, 1f);
            if (icon.localScale != size) icon.localScale = size;
            var spot = new Vector2(-4f - column * spacing, -row * spacing);
            if (icon.anchoredPosition != spot) icon.anchoredPosition = spot;
            // the name is the first text of the icon (the game writes it there every frame): hidden, not removed
            foreach (var text in icon.GetComponentsInChildren<TMP_Text>(true))
            {
                if (text.name == "TimeText") continue;
                if (text.enabled != name) text.enabled = name;
                break;
            }
        }
    }
}
