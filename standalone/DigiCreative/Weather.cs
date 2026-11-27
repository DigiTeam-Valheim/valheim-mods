// Weather in the creative world: one of the game's own weathers (EnvMan's environments: Clear, Rain, ThunderStorm,
// SnowStorm, Misty, the Ashlands' and Mistlands' skies, twilight, ...), picked from a list in F1 and held while you build;
// "Natural" lets the game change it as anywhere. It replaced "No rain" (on = Clear, off = Natural), whose choice carries
// over once. The list comes from the game (EnvMan.m_environments, mods' weathers included), so F1 shows it once a world
// is loaded; a weather that is not there (a mod removed) counts as Natural. Held with EnvMan.SetForceEnvironment, the
// console's "env" command; the light changes with it (the time of day has its own settings).
using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace DigiCreative
{
    internal static class Weather
    {
        // Read by Configuration Manager by name.
        internal sealed class ConfigurationManagerAttributes
        {
            public Action<ConfigEntryBase> CustomDrawer;
            public bool? HideDefaultButton;
            public bool? Browsable;
        }

        internal static ConfigEntry<string> Choice;
        private const string Natural = "Natural (the game's own weather)";
        private static bool _open;
        private static Vector2 _scroll;

        internal static void Bind(ConfigFile config, string section)
        {
            // Before 1.6.11: "No rain" (on = always Clear). Its choice once, when Weather is new in the file.
            var file = config.ConfigFilePath;
            var known = System.IO.File.Exists(file) && System.IO.File.ReadAllText(file).Contains("\nWeather = ");
            var noRain = config.Bind(section, "No rain", true, new ConfigDescription("Replaced by Weather.", null,
                new ConfigurationManagerAttributes { Browsable = false }));
            Choice = config.Bind(section, "Weather", "Clear", new ConfigDescription(
                "The weather in the creative world: click to pick one of the game's weathers (Clear, rain, thunderstorm, snow, " +
                "mist, the Ashlands' and Mistlands' skies, twilight, ...), held while you build. Natural: the weather changes " +
                "as anywhere. The list is there once a world is loaded.", null,
                new ConfigurationManagerAttributes { CustomDrawer = Draw }));
            if (!known && !noRain.Value) Choice.Value = "";
        }

        // The weather to hold now: a name the game knows, or null (Natural).
        internal static string Wanted(EnvMan env)
        {
            var name = Choice?.Value;
            if (string.IsNullOrEmpty(name) || env == null) return null;
            foreach (var setup in env.m_environments)
                if (setup != null && setup.m_name == name) return name;
            return null;
        }

        private static List<string> Names()
        {
            var env = EnvMan.instance;
            if (env == null) return new List<string>();
            return env.m_environments.Where(e => e != null && !string.IsNullOrEmpty(e.m_name)).Select(e => e.m_name)
                .Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // F1: the weather now as a button; clicked, the list (Natural first) to pick from.
        private static void Draw(ConfigEntryBase entry)
        {
            var current = Choice.Value;
            GUILayout.BeginVertical();
            if (GUILayout.Button(string.IsNullOrEmpty(current) ? Natural : current, GUILayout.ExpandWidth(true))) _open = !_open;
            if (_open)
            {
                var names = Names();
                if (names.Count == 0) GUILayout.Label("The list of weathers is there once a world is loaded.");
                _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(Mathf.Min(300f, 26f * (names.Count + 1))));
                if (GUILayout.Button(string.IsNullOrEmpty(current) ? "> " + Natural : Natural))
                {
                    Choice.Value = "";
                    _open = false;
                }
                foreach (var name in names)
                    if (GUILayout.Button(name == current ? "> " + name : name))
                    {
                        Choice.Value = name;
                        _open = false;
                    }
                GUILayout.EndScrollView();
            }
            GUILayout.EndVertical();
        }
    }
}
