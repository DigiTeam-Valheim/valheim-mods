// Build Menu Memory: the game's build menu (Categories, Materials, Recent, Favorites) keeps one open folder for all its
// tabs and goes back to "All" whenever you change tab, and to the first tab whenever you change tool. This remembers
// the open folder of every tab (your own Favorites folders included, by name) and the last tab of every tool (hammer,
// hoe, cultivator), and opens them again. Kept with the character, so it lasts after a restart.
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace BuildMenuMemory
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.buildmenumemory";
        public const string Name = "Digi's Build Menu Memory";
        public const string Version = "1.0.0";

        private const string DataKey = "digi_buildmenumemory";
        private const char Field = '\u001f', Entry = '\u001e';

        private static readonly AccessTools.FieldRef<BuildUi, int> CurrentTab = AccessTools.FieldRefAccess<BuildUi, int>("m_currentPieceList");
        private static readonly AccessTools.FieldRef<BuildUi, int> CurrentTag = AccessTools.FieldRefAccess<BuildUi, int>("m_currentTagId");
        private static readonly AccessTools.FieldRef<BuildUi, PieceTable> CurrentTool = AccessTools.FieldRefAccess<BuildUi, PieceTable>("m_currentBuildTool");
        private static readonly AccessTools.FieldRef<BuildUi, List<BuildUiTagButton>> TagButtons = AccessTools.FieldRefAccess<BuildUi, List<BuildUiTagButton>>("m_tagButtons");
        private static readonly MethodInfo SelectTag = AccessTools.Method(typeof(BuildUi), "OnSelectTag", new[] { typeof(int) });

        // "tool tab" -> open folder's name ("" = All); "tool" -> last tab
        private static readonly Dictionary<string, string> Folders = new Dictionary<string, string>();
        private static readonly Dictionary<string, int> Tabs = new Dictionary<string, int>();
        private static readonly FieldInfo PieceLists = AccessTools.Field(typeof(BuildUi), "m_pieceLists");
        private static BepInEx.Logging.ManualLogSource _log;
        private static Player _loadedFor;
        private static bool _switching, _opening;

        private void Awake()
        {
            _log = Logger;
            PatchModule(typeof(Plugin));
        }

        private void OnDestroy()
        {
            _loadedFor = null;
            _switching = _opening = false;
        }

        // --- Remembering -----------------------------------------------------------------------------------------------

        // A folder the player opens (the game also picks "All" while it changes tab: that is not remembered).
        [HarmonyPatch(typeof(BuildUi), "SetCurrentTag")]
        [HarmonyPostfix]
        private static void FolderOpened(BuildUi __instance)
        {
            if (_switching || !Load()) return;
            var tool = ToolName(__instance);
            if (tool == null) return;
            var tag = CurrentTag(__instance);
            var button = tag == -1 ? null : TagButtons(__instance)?.FirstOrDefault(b => b != null && b.m_tagId == tag);
            Folders[tool + Field + CurrentTab(__instance)] = button != null ? button.m_displayName ?? "" : "";
            Save();
        }

        [HarmonyPatch(typeof(BuildUi), nameof(BuildUi.SelectPieceList))]
        [HarmonyPrefix]
        private static void TabChanging() => _switching = true;

        // A new tab (not the menu reopening on the same one): open the folder that was open there last time.
        [HarmonyPatch(typeof(BuildUi), nameof(BuildUi.SelectPieceList))]
        [HarmonyFinalizer]
        private static void TabChanged(BuildUi __instance, int tabIndex, bool refreshOnly)
        {
            _switching = false;
            if (!Load()) return;
            var tool = ToolName(__instance);
            if (tool == null) return;
            if (!_opening)
            {
                Tabs[tool] = tabIndex; // (while the menu opens, the game picks the tab, not the player)
                Save();
            }
            if (refreshOnly || !Folders.TryGetValue(tool + Field + tabIndex, out var folder) || folder.Length == 0) return;
            var button = TagButtons(__instance)?.FirstOrDefault(b => b != null && b.m_displayName == folder);
            if (button == null || SelectTag == null) return;
            try
            {
                SelectTag.Invoke(__instance, new object[] { button.m_tagId });
            }
            catch (System.Exception e)
            {
                _log?.LogWarning("Could not open the remembered folder: " + (e.InnerException ?? e).Message);
            }
        }

        // Another tool than last time: the game starts on its first tab; go to the one used last with this tool.
        [HarmonyPatch(typeof(BuildUi), nameof(BuildUi.OpenBuildMenu))]
        [HarmonyPrefix]
        private static void Opening(BuildUi __instance, out PieceTable __state)
        {
            __state = CurrentTool(__instance);
            _opening = true;
        }

        [HarmonyPatch(typeof(BuildUi), nameof(BuildUi.OpenBuildMenu))]
        [HarmonyFinalizer]
        private static void OpenDone() => _opening = false;

        [HarmonyPatch(typeof(BuildUi), nameof(BuildUi.OpenBuildMenu))]
        [HarmonyPostfix]
        private static void Opened(BuildUi __instance, PieceTable __state)
        {
            _opening = false;
            var tool = CurrentTool(__instance);
            if (tool == null || tool == __state || !Load()) return;
            var count = (PieceLists?.GetValue(__instance) as System.Collections.IList)?.Count ?? 0;
            if (Tabs.TryGetValue(tool.name, out var tab) && tab != CurrentTab(__instance) && tab >= 0 && tab < count) __instance.SelectPieceList(tab);
        }

        private static string ToolName(BuildUi ui)
        {
            var tool = CurrentTool(ui);
            return tool != null ? tool.name : null;
        }

        // --- Kept with the character -------------------------------------------------------------------------------------

        private static bool Load()
        {
            var player = Player.m_localPlayer;
            if (player == null) return false;
            if (_loadedFor == player) return true;
            _loadedFor = player;
            Folders.Clear();
            Tabs.Clear();
            if (!player.m_customData.TryGetValue(DataKey, out var data) || string.IsNullOrEmpty(data)) return true;
            foreach (var entry in data.Split(Entry))
            {
                var parts = entry.Split(Field);
                if (parts.Length == 2 && int.TryParse(parts[1], out var tab)) Tabs[parts[0]] = tab;
                else if (parts.Length == 3) Folders[parts[0] + Field + parts[1]] = parts[2];
            }
            return true;
        }

        private static void Save()
        {
            var player = Player.m_localPlayer;
            if (player == null || _loadedFor != player) return;
            var text = new StringBuilder();
            foreach (var pair in Tabs) text.Append(pair.Key).Append(Field).Append(pair.Value).Append(Entry);
            foreach (var pair in Folders) text.Append(pair.Key).Append(Field).Append(pair.Value).Append(Entry);
            if (text.Length > 0) text.Length--;
            player.m_customData[DataKey] = text.ToString();
        }
    }
}
