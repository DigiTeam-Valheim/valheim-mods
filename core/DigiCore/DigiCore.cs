// Shared core of the combined DigiTeam mods. A combined mod is one BepInEx plugin with one settings file that runs a
// set of sub-mods (modules). F1 shows a master switch per sub-mod at the top ("0 - Sub-mods"); below it every sub-mod's
// own settings, grouped under its name. Switching a sub-mod off removes all its patches right away; switching it on
// starts it again. Each module was a separate plugin before: the first time it starts, its settings are copied from
// its old settings file (digi.valheim.<mod>.cfg), and it does not start while the old standalone plugin is installed
// (the main menu then shows one message, "Remove these mods", naming every separate mod to remove).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace DigiCore
{
    // One sub-mod of a combined mod.
    public sealed class ModuleInfo
    {
        public readonly Type Type;
        public readonly string Title, OldGuid, Description;
        public readonly string[] Requires;
        public readonly bool DefaultOn, OffOnce;
        public readonly string FormerTitle;

        // offOnce: went from on to off by default, so it is switched off once for those who have it on from the old default.
        // formerTitle: the sub-mod's name before it was renamed: its on/off switch carries over once.
        public ModuleInfo(Type type, string title, string oldGuid, string description, string[] requires = null, bool defaultOn = true,
            bool offOnce = false, string formerTitle = null)
        {
            OffOnce = offOnce;
            FormerTitle = formerTitle;
            Type = type;
            Title = title;
            OldGuid = oldGuid;
            Description = description;
            Requires = requires ?? new string[0];
            DefaultOn = defaultOn;
        }
    }

    public abstract class DigiPlugin : BaseUnityPlugin
    {
        public const string ModulesSection = "0 - Sub-mods";

        protected abstract ModuleInfo[] Modules { get; }

        // A single sub-mod packaged as its own plugin (the Hexium packages, made by make_hexium_singles.py): its plugin ID
        // is the sub-mod's own (OldGuid), so a combined mod with the same sub-mod leaves its copy off; it always runs (no
        // switch); its settings sections have no sub-mod name in front, like the separate mods had, so settings carry
        // over both ways. The combined mods leave this false.
        protected virtual bool Standalone => false;
        internal bool IsStandalone => Standalone;

        // Set while a module component is being added, so its Awake binds to the right settings file and section.
        internal static DigiPlugin StartingHost;
        internal static ModuleInfo StartingModule;

        private static readonly Dictionary<string, string> RequirementNames = new Dictionary<string, string>
        {
            { "randyknapp.mods.epicloot", "Epic Loot" }, { "com.jotunn.jotunn", "Jotunn" }, { "ValheimLegends", "Valheim Legends" },
            { "vapok.mods.adventurebackpacks", "Adventure Backpacks" }, { "MidnightsFX.StarLevelSystem", "Star Level System" },
            { "sosal.nordicrugs", "Nordic Rugs" }, { "LuminairPrime.valheim.localcraftfromstorageprime", "Local Craft From Storage Prime" },
        };

        // The package name on Thunderstore (Hexium for a single): "Digi's Epic Loot Add-ons" -> Epic_Loot_Addons; a single
        // as make_hexium_singles names it ("Spawn-Free Range" -> Spawn_Free_Range).
        protected virtual string PackageName
        {
            get
            {
                var title = Info.Metadata.Name.Replace("Digi's ", "");
                if (Standalone) return System.Text.RegularExpressions.Regex.Replace(title, "[^A-Za-z0-9]+", "_").Trim('_');
                return string.Join("_", new string(title.Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray())
                    .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            }
        }

        private readonly Dictionary<ModuleInfo, DigiModule> _running = new Dictionary<ModuleInfo, DigiModule>();
        private readonly Dictionary<ModuleInfo, ConfigEntry<bool>> _switches = new Dictionary<ModuleInfo, ConfigEntry<bool>>();
        private HashSet<string> _migrated;
        private string MigratedFile => Path.Combine(Paths.ConfigPath, Info.Metadata.GUID + ".migrated.txt");

        protected virtual void Awake()
        {
            Config.SaveOnConfigSet = true;
            Config.Save(); // the file exists from the start (old per-mod migration code then leaves it alone)
            _migrated = File.Exists(MigratedFile)
                ? new HashSet<string>(File.ReadAllLines(MigratedFile).Select(l => l.Trim()).Where(l => l.Length > 0))
                : new HashSet<string>();
            var firstRun = !_migrated.Contains("#switches");

            if (Standalone)
            {
                foreach (var module in Modules)
                {
                    var needs = module.Requires.Where(r => !Chainloader.PluginInfos.ContainsKey(r)).ToList();
                    if (needs.Count > 0)
                        Logger.LogWarning($"{module.Title}: not started, needs " +
                                          string.Join(" and ", needs.Select(r => RequirementNames.TryGetValue(r, out var n) ? n : r)) + ".");
                    else StartModule(module);
                }
                return;
            }

            foreach (var module in Modules)
            {
                var missing = module.Requires.Where(r => !Chainloader.PluginInfos.ContainsKey(r)).ToList();
                var note = module.Requires.Length == 0 ? "" :
                    $" Needs {string.Join(" and ", module.Requires.Select(r => RequirementNames.TryGetValue(r, out var n) ? n : r))}.";
                var entry = Config.Bind(ModulesSection, module.Title, module.DefaultOn, $"{module.Description}{note}");
                // Upgrading from the separate mods: a sub-mod you used before starts switched on.
                if (firstRun && !module.DefaultOn && File.Exists(OldConfigPath(module))) entry.Value = true;
                // Renamed: the old switch (still in the file, BepInEx keeps unknown entries) carries over once.
                if (module.FormerTitle != null && !_migrated.Contains("#renamed:" + module.FormerTitle))
                {
                    if (File.Exists(Config.ConfigFilePath) &&
                        ReadCfg(Config.ConfigFilePath).TryGetValue(ModulesSection + "\n" + module.FormerTitle, out var was) &&
                        bool.TryParse(was, out var wasOn))
                        entry.Value = wasOn;
                    Remember("#renamed:" + module.FormerTitle);
                }
                if (module.OffOnce && !_migrated.Contains("#off:" + module.OldGuid))
                {
                    entry.Value = false;
                    Remember("#off:" + module.OldGuid);
                }
                _switches[module] = entry;
                var captured = module;
                entry.SettingChanged += (_, __) => Apply(captured);
                if (missing.Count > 0) Logger.LogInfo($"{module.Title}: off, needs {string.Join(", ", missing)}.");
            }
            if (firstRun) Remember("#switches");
            foreach (var module in Modules) Apply(module);
        }

        private void Start()
        {
            // BepInEx loads plugins in ID order, so in Awake only the separate mods loaded before this one were known.
            // Now all are: a sub-mod whose separate mod loaded later stops here, and copies its settings again once
            // the separate mod is gone.
            foreach (var pair in _running.ToList())
            {
                if (pair.Value == null || pair.Key.OldGuid == Info.Metadata.GUID || !Chainloader.PluginInfos.ContainsKey(pair.Key.OldGuid)) continue;
                if (!IsOurSingle(pair.Key.OldGuid))
                {
                    OtherMods.SwitchOff(pair.Key.OldGuid, null, Logger); // the separate mod goes off, ours keeps running
                    continue;
                }
                pair.Value.Teardown();
                _running.Remove(pair.Key);
                Forget(pair.Key.OldGuid);
                LeaveToSingle(pair.Key);
            }
            SettingOrder.Apply(Config);
            // No update check of our own (parked in mods/core/parked/UpdateCheck.cs): Hexium's moderators reject mods that ask
            // for updates themselves and say so at the main menu; the mod manager shows updates (2026-11-19).
            // Sub-mods can still report a mod they replace (their Start), so the check runs once the menu is there.
            // One copy shows it. Mods built from an older DigiCore wait for the old "shown" flag before showing their
            // own (longer) message: setting it now leaves the message to this simpler one.
            if (!Application.isBatchMode && AppDomain.CurrentDomain.GetData(PopupOwnerKey) == null)
            {
                AppDomain.CurrentDomain.SetData(PopupOwnerKey, true);
                AppDomain.CurrentDomain.SetData(DuplicatesShownKey, true);
                StartCoroutine(ShowDuplicates());
            }
        }

        // Mods that older copies of this code (combined mods not updated yet) left on, keeping their own copy off: lines
        // "<combined mod>|<mod>", in the AppDomain. Newer copies switch such a mod off instead (OtherMods). The main menu
        // shows both kinds once, in one message.
        private const string DuplicatesKey = "digi.valheim.duplicates", DuplicatesShownKey = "digi.valheim.duplicates.shown",
            PopupOwnerKey = "digi.valheim.duplicates.popup2";

        private static List<string> Duplicates
        {
            get
            {
                var list = AppDomain.CurrentDomain.GetData(DuplicatesKey) as List<string>;
                if (list == null)
                {
                    list = new List<string>();
                    AppDomain.CurrentDomain.SetData(DuplicatesKey, list);
                }
                return list;
            }
        }

        // One of our own single-mod packages (Hexium): installing it next to the bundle is fine; the single runs and this
        // copy stays off, without a message.
        private void LeaveToSingle(ModuleInfo module) =>
            Logger.LogInfo($"{module.Title}: runs as its own mod ({module.OldGuid}), so the copy in {Info.Metadata.Name} stays off.");

        // A plugin made from this same DigiCore (a standalone single), whichever DLL it is in: checked by type name.
        private static bool IsOurSingle(string guid)
        {
            if (!Chainloader.PluginInfos.TryGetValue(guid, out var info) || info.Instance == null) return false;
            for (var t = info.Instance.GetType(); t != null; t = t.BaseType)
                if (t.FullName == "DigiCore.DigiPlugin") return true;
            return false;
        }

        private System.Collections.IEnumerator ShowDuplicates()
        {
            while (FejdStartup.instance == null || !UnifiedPopup.IsAvailable()) yield return new WaitForSeconds(0.5f);
            yield return new WaitForSeconds(1f); // the other combined mods have added theirs by now
            OtherMods.SweepAgain(); // patches a switched-off mod made after we switched it off (in its own Start)
            var off = OtherMods.Names();
            // From older copies: each mod once, by name, unless a newer copy switched it off anyway.
            var remove = Duplicates.Select(d => d.Substring(d.IndexOf('|') + 1)).Where(n => !off.Contains(n)).Distinct().OrderBy(n => n).ToList();
            if (off.Count == 0 && remove.Count == 0) yield break;
            var text = "";
            if (off.Count > 0)
                text = "<b>" + string.Join("\n", off) + "</b>\n\nDigi's mods do the same, so these are switched off. You can remove them " +
                       "in your mod manager. (To use one of them instead, switch Digi's version off in F1 and restart.)";
            if (remove.Count > 0)
                text += (text.Length > 0 ? "\n\n" : "") + "<b>" + string.Join("\n", remove) + "</b>\n\nRemove these in your mod manager: " +
                        "until then, the older Digi mod that does the same stays off (or update it).";
            UnifiedPopup.Push(new WarningPopup(off.Count > 0 ? "Mods switched off" : "Remove these mods", text,
                () => UnifiedPopup.Pop(), localizeText: false));
        }

        private static string OldConfigPath(ModuleInfo module) => Path.Combine(Paths.ConfigPath, module.OldGuid + ".cfg");

        // True only the first time this key is asked for in this game (kept in the migrated file): for a setting that a new
        // version changes once for everyone, also for players who already have it in their settings file.
        internal bool FirstTime(string key)
        {
            if (_migrated == null || _migrated.Contains(key)) return false;
            Remember(key);
            return true;
        }

        private void Remember(string key)
        {
            if (!_migrated.Add(key)) return;
            try { File.AppendAllText(MigratedFile, key + Environment.NewLine); }
            catch (Exception e) { Logger.LogWarning($"Could not write {MigratedFile}: {e.Message}"); }
        }

        private void Forget(string key)
        {
            if (!_migrated.Remove(key)) return;
            try { File.WriteAllLines(MigratedFile, _migrated.ToArray()); }
            catch (Exception e) { Logger.LogWarning($"Could not write {MigratedFile}: {e.Message}"); }
        }

        // Starts or stops a module to match its switch.
        private void Apply(ModuleInfo module)
        {
            var on = _switches[module].Value;
            _running.TryGetValue(module, out var current);
            if (on && current == null)
            {
                if (module.Requires.Any(r => !Chainloader.PluginInfos.ContainsKey(r))) return;
                // (a package of one mod may use the mod's own ID as the plugin's: that is this plugin, not a duplicate)
                if (module.OldGuid != Info.Metadata.GUID && Chainloader.PluginInfos.ContainsKey(module.OldGuid))
                {
                    if (IsOurSingle(module.OldGuid))
                    {
                        LeaveToSingle(module);
                        return;
                    }
                    OtherMods.SwitchOff(module.OldGuid, null, Logger); // the separate mod goes off, ours runs
                }
                StartModule(module);
            }
            else if (!on && current != null)
            {
                current.Teardown();
                _running.Remove(module);
                Logger.LogInfo($"{module.Title}: switched off.");
            }
        }

        private void StartModule(ModuleInfo module)
        {
            StartingHost = this;
            StartingModule = module;
            try
            {
                var component = (DigiModule)gameObject.AddComponent(module.Type);
                component.Host = this;
                component.Info2 = module;
                _running[module] = component;
                MigrateOldSettings(module, component);
            }
            catch (Exception e)
            {
                Logger.LogError($"{module.Title} could not start: {e}");
            }
            finally
            {
                StartingHost = null;
                StartingModule = null;
            }
            SettingOrder.Apply(Config);
        }

        // Copies the module's settings from its old settings file, once.
        private void MigrateOldSettings(ModuleInfo module, DigiModule component)
        {
            if (_migrated.Contains(module.OldGuid) || module.OldGuid == Info.Metadata.GUID) return; // standalone: its own file
            var path = OldConfigPath(module);
            if (File.Exists(path) && component.ConfigOrNull != null)
            {
                var old = ReadCfg(path);
                var copied = 0;
                foreach (var bound in component.ConfigOrNull.Entries)
                {
                    if (!old.TryGetValue(bound.Section + "\n" + bound.Key, out var raw)) continue;
                    try
                    {
                        bound.Entry.SetSerializedValue(raw);
                        copied++;
                    }
                    catch (Exception e)
                    {
                        Logger.LogWarning($"{module.Title}: could not copy '{bound.Key}': {e.Message}");
                    }
                }
                Logger.LogInfo($"{module.Title}: copied {copied} setting(s) from {Path.GetFileName(path)}.");
            }
            Remember(module.OldGuid);
        }

        // BepInEx .cfg: [Section] lines and "Key = Value" lines; comments start with #.
        private static Dictionary<string, string> ReadCfg(string path)
        {
            var result = new Dictionary<string, string>();
            var section = "";
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2);
                    continue;
                }
                var eq = line.IndexOf(" = ", StringComparison.Ordinal);
                if (eq <= 0) continue;
                result[section + "\n" + line.Substring(0, eq).Trim()] = line.Substring(eq + 3);
            }
            return result;
        }

        protected virtual void OnDestroy()
        {
            foreach (var module in _running.Values.ToList()) module?.Teardown();
            _running.Clear();
        }

        // For modules that need to know whether another module of the same combined mod is running.
        public bool IsRunning(Type moduleType) => _running.Any(p => p.Key.Type == moduleType && p.Value != null);
    }

    // Base of every sub-mod: gives the module code the same Config / Logger it had as a separate plugin.
    public abstract class DigiModule : MonoBehaviour
    {
        internal DigiPlugin Host;
        internal ModuleInfo Info2;

        // For a sub-mod that does the job of another mod: call from Start. If that mod is installed too it is switched
        // off (OtherMods) and named in the main menu message; true when it was installed.
        protected bool SwitchOffReplaced(string guid, string name) => OtherMods.SwitchOff(guid, name, Logger);
        private ModuleConfig _config;
        private ManualLogSource _logger;
        private Harmony _harmony;

        internal ModuleConfig ConfigOrNull => _config;

        // True only the first time in this game (per sub-mod and key): to change a setting once for everyone.
        protected bool FirstTime(string key) =>
            (Host ?? DigiPlugin.StartingHost)?.FirstTime("#once:" + (Info2 ?? DigiPlugin.StartingModule)?.Title + ":" + key) ?? false;

        protected ModuleConfig Config =>
            _config ?? (_config = new ModuleConfig((Host ?? DigiPlugin.StartingHost).Config,
                (Host ?? DigiPlugin.StartingHost).IsStandalone ? null : (Info2 ?? DigiPlugin.StartingModule).Title));

        protected ManualLogSource Logger =>
            _logger ?? (_logger = BepInEx.Logging.Logger.CreateLogSource("Digi's " + (Info2 ?? DigiPlugin.StartingModule)?.Title));

        // The module's own Harmony instance: everything it patches is removed again when it is switched off.
        protected Harmony ModuleHarmony => _harmony ?? (_harmony = new Harmony(GetType().Namespace?.ToLowerInvariant() + "." + Guid.NewGuid().ToString("N")));

        // Same as Harmony.CreateAndPatchAll(type): the patches declared in that one class.
        protected Harmony PatchModule(Type type)
        {
            ModuleHarmony.PatchAll(type);
            return ModuleHarmony;
        }

        // Same as patching the whole assembly when the module was its own plugin: every class in its namespace.
        protected Harmony PatchModuleNamespace()
        {
            var ns = GetType().Namespace;
            foreach (var type in AccessTools.GetTypesFromAssembly(GetType().Assembly))
                if (type.Namespace == ns || (type.Namespace != null && ns != null && type.Namespace.StartsWith(ns + ".")))
                    ModuleHarmony.CreateClassProcessor(type).Patch();
            return ModuleHarmony;
        }

        internal void Teardown()
        {
            try { _harmony?.UnpatchSelf(); }
            catch (Exception e) { Logger.LogWarning($"Unpatch: {e.Message}"); }
            Destroy(this);
        }
    }

    // A module's view of the shared settings file: its sections get the module's name in front.
    public sealed class ModuleConfig
    {
        public sealed class Bound
        {
            public string Section, Key;
            public ConfigEntryBase Entry;
        }

        private readonly ConfigFile _file;
        private readonly string _prefix;
        internal readonly List<Bound> Entries = new List<Bound>();

        internal ModuleConfig(ConfigFile file, string title)
        {
            _file = file;
            _prefix = title == null ? "" : title + " - "; // null: a standalone sub-mod, sections as they are
        }

        public ConfigFile File => _file;
        public string ConfigFilePath => _file.ConfigFilePath;
        public void Reload() { }
        public void Save() => _file.Save();

        public ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, ConfigDescription configDescription = null)
        {
            // BepInEx refuses these characters in names and throws, which would stop the whole sub-mod: leave them out.
            section = Clean(section);
            key = Clean(key);
            var entry = _file.Bind(_prefix + section, key, defaultValue, configDescription);
            if (!Entries.Any(b => b.Section == section && b.Key == key)) Entries.Add(new Bound { Section = section, Key = key, Entry = entry });
            return entry;
        }

        private static string Clean(string name)
        {
            if (name.IndexOfAny(Invalid) < 0) return name;
            var parts = name.Split(Invalid, System.StringSplitOptions.None);
            return string.Join("", parts).Trim();
        }

        private static readonly char[] Invalid = { '=', '\n', '\t', '\\', '"', '\'', '[', ']' };

        public ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, string description) =>
            Bind(section, key, defaultValue, new ConfigDescription(description));

        public ConfigEntry<T> Bind<T>(ConfigDefinition definition, T defaultValue, ConfigDescription configDescription = null) =>
            Bind(definition.Section, definition.Key, defaultValue, configDescription);

        public bool TryGetEntry<T>(ConfigDefinition definition, out ConfigEntry<T> entry) =>
            _file.TryGetEntry(new ConfigDefinition(_prefix + definition.Section, definition.Key), out entry);
    }
}
