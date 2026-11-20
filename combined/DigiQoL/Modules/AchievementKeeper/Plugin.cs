// Keeps Steam achievements and stats working in a modded game. Valheim blocks achievements when the game is modded,
// when cheats were used, when the world has cheat modifiers or when you carry a "cheated" item. Each of those can be
// ignored separately. Stats keep counting normally (kills, crafts, ...) instead of being marked as cheated.
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AchievementKeeper
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.achievementkeeper";
        public const string Name = "Digi's Achievement Keeper";
        public const string Version = "1.0.2";

        private static ConfigEntry<bool> _modded, _cheats, _items, _hideStats;
        private static readonly List<string> ToRemove = new List<string>();
        private static readonly AccessTools.FieldRef<Dictionary<string, Terminal.ConsoleCommand>> Commands =
            AccessTools.StaticFieldRefAccess<Dictionary<string, Terminal.ConsoleCommand>>(AccessTools.Field(typeof(Terminal), "commands"));
        private float _timer;

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
            _modded = Config.Bind("General", "Allow in modded games", true, "Steam achievements can unlock while you play with mods (Valheim normally blocks them in modded games).");
            _cheats = Config.Bind("General", "Ignore cheat flags", true,
                "Achievements and stats keep counting even after console cheats (devcommands), on worlds with cheat world modifiers, or with spawned items.");
            _items = Config.Bind("General", "Clear cheated mark on items", true,
                "Items made with cheats (spawned, or dropped by spawned creatures) lose their hidden \"cheated\" mark.");
            _hideStats = Config.Bind("General", "Hide cheat stats", false,
                "Your character's stats page shows 0 cheats and no cheat commands.");
            PatchModule(typeof(Plugin));
        }

        // Console commands: the game asks to type "confirmcheats" before a cheat command until it sees the cheat flag.
        // With the flag ignored it would ask forever and never run one, so inside a command the game counts as cheated
        // (that confirmation is about losing achievements, which this mod prevents anyway).
        private static int _inCommand;

        [HarmonyPatch(typeof(Terminal.ConsoleCommand), nameof(Terminal.ConsoleCommand.RunAction))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void CommandStart() => _inCommand++;

        [HarmonyPatch(typeof(Terminal.ConsoleCommand), nameof(Terminal.ConsoleCommand.RunAction))]
        [HarmonyFinalizer]
        private static void CommandEnd()
        {
            if (_inCommand > 0) _inCommand--;
        }

        [HarmonyPatch(typeof(Achievements), nameof(Achievements.IsCheatedAtAll))]
        [HarmonyPrefix]
        private static bool IsCheatedAtAll(ref bool __result)
        {
            if (_inCommand > 0 && _cheats.Value)
            {
                __result = true;
                return false;
            }
            if (_cheats.Value && _modded.Value)
            {
                __result = false;
                return false;
            }
            if (!_cheats.Value && !_modded.Value) return true;
            // One of the two: vanilla check without the ignored part.
            var cheated = !_cheats.Value && (
                (Game.instance != null && Game.instance.GetPlayerProfile().m_usedCheats) ||
                Achievements.IsWorldCheated() ||
                (Player.m_localPlayer != null && Player.m_localPlayer.GetInventory().AnyCheatedItem()));
            __result = cheated || (!_modded.Value && Game.isModded);
            return false;
        }

        [HarmonyPatch(typeof(PlayerProfile), nameof(PlayerProfile.s_bypassCheatChecks), MethodType.Getter)]
        [HarmonyPostfix]
        private static void BypassCheatChecks(ref bool __result)
        {
            if (_cheats.Value) __result = true;
        }

        [HarmonyPatch(typeof(PlayerProfile), nameof(PlayerProfile.IncrementStat))]
        [HarmonyPrefix]
        private static bool IncrementStat(PlayerStatType stat) => !(_hideStats.Value && stat == PlayerStatType.Cheats);

        // Every few seconds: clear the used-cheats flag, item marks and (optionally) the cheat stats. Cheap and only local.
        private void Update()
        {
            _timer += Time.unscaledDeltaTime;
            if (_timer < 3f) return;
            _timer = 0f;
            var profile = Game.instance != null ? Game.instance.GetPlayerProfile() : null;
            if (profile == null) return;
            if (_cheats.Value) profile.m_usedCheats = false;
            if (_hideStats.Value) ClearCheatStats(profile);
            if (_items.Value && Player.m_localPlayer != null)
                foreach (var item in Player.m_localPlayer.GetInventory().GetAllItems())
                    if (item.m_cheated) item.m_cheated = false;
        }

        private static void ClearCheatStats(PlayerProfile profile)
        {
            var commands = Commands();
            foreach (var stats in profile.m_playerStats)
            {
                if (stats == null) continue;
                if (stats.m_stats != null) stats.m_stats[PlayerStatType.Cheats] = 0f;
                var known = stats.m_knownCommands;
                if (known == null || known.Count == 0) continue;
                ToRemove.Clear();
                foreach (var key in known.Keys)
                    if (key == "devcommands" || (commands != null && commands.TryGetValue(key, out var cmd) && cmd != null && cmd.IsCheat))
                        ToRemove.Add(key);
                foreach (var key in ToRemove) known.Remove(key);
            }
        }
    }
}
