// The modpack's settings, applied once instead of on every update. A mod manager (r2modman, Thunderstore, Hexium) copies
// a pack's config folder over the profile's on EVERY update of the pack, which threw away every setting a player had
// changed (Epic Loot, Star Level System, ours). So the pack (Digi Modpack) ships its settings files in
// BepInEx/config/DigiModpack/seed/ and this puts each into BepInEx/config once:
// - not there yet: copied;
// - there, but made in this game session (a fresh install: a mod that loaded before this one just wrote its defaults):
//   copied over, and that mod's settings are read again at once (switches and values that apply live do so; the rest,
//   e.g. Epic Loot's data files, from the next start);
// - there from before (the player's own): never touched again;
// - a later pack changes a file: it replaces the player's copy only when that copy is still exactly what the pack put
//   there; changed by the player (or rewritten by its mod), it stays.
// What was done is kept in BepInEx/config/DigiModpack/seeded.txt (path, tab, the hash of what was put there or "own").
// Without the pack (no seed folder) nothing happens. Runs once per game, before this plugin's modules start.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;

namespace DigiWorld
{
    internal static class PackSeed
    {
        private const string Own = "own", RanKey = "digi.modpack.seeded";

        internal static void Run(ManualLogSource log)
        {
            if (AppDomain.CurrentDomain.GetData(RanKey) != null) return;
            AppDomain.CurrentDomain.SetData(RanKey, true);
            try
            {
                Seed(log, Paths.ConfigPath);
            }
            catch (Exception e)
            {
                log?.LogWarning("Modpack settings not applied: " + e.Message);
            }
        }

        // config: the BepInEx config folder (a test passes its own).
        internal static void Seed(ManualLogSource log, string config)
        {
            var root = Path.Combine(config, "DigiModpack");
            var seed = Path.Combine(root, "seed");
            if (!Directory.Exists(seed)) return;
            var listPath = Path.Combine(root, "seeded.txt");
            var list = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(listPath))
                foreach (var line in File.ReadAllLines(listPath))
                {
                    var tab = line.LastIndexOf('\t');
                    if (tab > 0) list[line.Substring(0, tab)] = line.Substring(tab + 1).Trim();
                }
            var started = Process.GetCurrentProcess().StartTime.ToUniversalTime();
            int applied = 0, kept = 0, reloaded = 0;
            foreach (var file in Directory.GetFiles(seed, "*", SearchOption.AllDirectories))
            {
                var rel = file.Substring(seed.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var target = Path.Combine(config, rel);
                list.TryGetValue(rel, out var known);
                if (known == Own) continue;
                var seedHash = Hash(file);
                var fresh = false;
                if (known == null)
                {
                    if (File.Exists(target))
                    {
                        if (File.GetCreationTimeUtc(target) < started)
                        {
                            list[rel] = Own; // the player's own from before: theirs
                            kept++;
                            continue;
                        }
                        fresh = true; // made a moment ago by a mod that loaded first, with its defaults
                    }
                }
                else
                {
                    if (known == seedHash) continue; // the pack's file is unchanged
                    if (File.Exists(target) && Hash(target) != known)
                    {
                        list[rel] = Own; // changed since the pack put it there
                        kept++;
                        continue;
                    }
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
                list[rel] = seedHash;
                applied++;
                if (fresh && Reloader(target)) reloaded++;
            }
            File.WriteAllLines(listPath, list.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key + "\t" + p.Value));
            if (applied > 0 || kept > 0)
                log?.LogInfo($"Modpack settings: {applied} files applied ({reloaded} read again at once), {kept} kept as the player's own.");
        }

        // A mod that loaded before this one reads its settings file again (BepInEx raises SettingChanged for each). A test
        // outside the game swaps it (BepInEx's plugin loader needs the game).
        internal static Func<string, bool> Reloader = Reload;

        private static bool Reload(string target)
        {
            var full = Path.GetFullPath(target);
            foreach (var info in Chainloader.PluginInfos.Values)
            {
                var plugin = info?.Instance;
                if (plugin == null || plugin.Config == null || string.IsNullOrEmpty(plugin.Config.ConfigFilePath)) continue;
                if (!string.Equals(Path.GetFullPath(plugin.Config.ConfigFilePath), full, StringComparison.OrdinalIgnoreCase)) continue;
                plugin.Config.Reload();
                return true;
            }
            return false;
        }

        private static string Hash(string path)
        {
            using (var sha = SHA1.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }
    }
}
