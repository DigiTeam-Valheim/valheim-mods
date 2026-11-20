// Build Materials: a hammer tool (Building tab, at the end). Click any piece of a building and a window opens with every
// material the whole building costs (every piece connected to the one you clicked), what you have in your inventory and
// the chests nearby (green: enough, red: not) and what is missing; sortable by any column and with a search box, like
// the Cookbook. Below that how many of each piece. Handy to rebuild a building somewhere else, or to see what it took.
// The window and the building search are shared with Ultimate Builder (DigiShared/MaterialsList.cs, Windows.cs,
// Buildings.cs); Ultimate Builder leaves its own Build Materials tool out while this one is on, so the hammer shows one.
// The tool is a copy of the hammer's Repair tool made into a normal menu piece (its click is taken over), with the icon
// of make_creative_icons.py. It is in the hammer while this sub-mod runs; switched off, it leaves it.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using DigiShared;
using HarmonyLib;
using UnityEngine;

namespace BuildMaterials
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.buildmaterials";
        public const string Name = "Digi's Build Materials";
        public const string Version = "1.0.0";
        private const string ToolName = "piece_buildmaterials_tool";

        private static ConfigEntry<float> _chestRange;
        private static BepInEx.Logging.ManualLogSource _log;
        private static Plugin _instance;
        private static GameObject _tool, _holder;
        private static ObjectDB _syncedDb;
        private static bool _busy, _failed;

        private static readonly AccessTools.FieldRef<Player, HashSet<string>> KnownRecipes = AccessTools.FieldRefAccess<Player, HashSet<string>>("m_knownRecipes");
        private static readonly AccessTools.FieldRef<BuildUi, List<Piece>> MenuScratch = AccessTools.FieldRefAccess<BuildUi, List<Piece>>("m_tempPieces");
        private static readonly MethodInfo UpdateKnown = AccessTools.Method(typeof(Player), "UpdateKnownRecipesList");
        private static readonly MethodInfo UpdateAvailable = AccessTools.Method(typeof(Player), "UpdateAvailablePiecesList");

        private void Awake()
        {
            _instance = this;
            _log = Logger;
            _chestRange = Config.Bind("Build Materials", "Chest range (m)", 50f, new ConfigDescription(
                "Chests up to this far away count for Have (with your inventory): player-built ones you may open. 0: only your inventory.",
                new AcceptableValueRange<float>(0f, 100f)));
            MaterialsList.ChestRange = _chestRange.Value;
            _chestRange.SettingChanged += (_, __) => MaterialsList.ChestRange = _chestRange.Value;
            _syncedDb = null;
            PatchModule(typeof(Plugin));
            Windows.Patch(ModuleHarmony);
            Windows.Use(gameObject);
        }

        private void Update()
        {
            if (_failed) return;
            try { Sync(); }
            catch (Exception e)
            {
                _failed = true; // once in the log, not every frame
                Logger.LogError("Adding the Build Materials tool to the hammer failed: " + e);
            }
        }

        private void OnDestroy()
        {
            Windows.Close();
            Windows.Release(gameObject);
            HammerTable()?.m_pieces.RemoveAll(p => p != null && p.name == ToolName);
            Refresh(Player.m_localPlayer);
            if (_holder != null) Destroy(_holder);
            _tool = null;
            _syncedDb = null;
            if (_instance == this) _instance = null;
        }

        // --- The tool in the hammer --------------------------------------------------------------------------------

        private static PieceTable HammerTable()
        {
            var hammer = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab("Hammer") : null;
            var drop = hammer != null ? hammer.GetComponent<ItemDrop>() : null;
            return drop != null ? drop.m_itemData?.m_shared?.m_buildPieces : null;
        }

        // Once per world (the hammer's list belongs to the game and lives on), when the player is there: else the game
        // would greet the tool as a new piece.
        private static void Sync()
        {
            var player = Player.m_localPlayer;
            if (ObjectDB.instance == null || ObjectDB.instance == _syncedDb || ZNetScene.instance == null || player == null) return;
            var table = HammerTable();
            if (table == null) return;
            _syncedDb = ObjectDB.instance;
            table.m_pieces.RemoveAll(p => p != null && p.name == ToolName);
            if (!MakeTool(table)) return;
            table.m_pieces.Add(_tool);
            KnownRecipes(player).Add(_tool.GetComponent<Piece>().m_name);
            Refresh(player);
        }

        // The game refreshes the hammer's list only when it learns a new piece; a menu update that failed leaves pieces
        // in its scratch list: both are put right here.
        private static void Refresh(Player player)
        {
            if (player == null) return;
            UpdateKnown.Invoke(player, null);
            UpdateAvailable.Invoke(player, null);
            var buildUi = Hud.instance != null ? Hud.instance.m_buildUi : null;
            if (buildUi != null) MenuScratch(buildUi).Clear();
        }

        private static bool MakeTool(PieceTable table)
        {
            if (_tool != null) return true;
            var repair = table.m_pieces.FirstOrDefault(p => p != null && p.GetComponent<Piece>() != null && p.GetComponent<Piece>().m_repairPiece);
            if (repair == null)
            {
                _log?.LogError("The hammer's repair tool was not found, no Build Materials tool.");
                return false;
            }
            if (_holder == null)
            {
                _holder = new GameObject("DigiBuilding_BuildMaterials");
                _holder.SetActive(false); // the copy never wakes up
                DontDestroyOnLoad(_holder);
            }
            _tool = Instantiate(repair, _holder.transform, false);
            _tool.name = ToolName;
            var piece = _tool.GetComponent<Piece>();
            piece.m_repairPiece = false;
            piece.m_removePiece = false;
            piece.m_name = "Build Materials";
            piece.m_description = "Click a building piece: every material the whole building connected to it costs, what you have " +
                                  "(inventory and nearby chests) and what is missing.";
            piece.m_category = Piece.PieceCategory.BuildingWorkbench;
            piece.m_resources = new Piece.Requirement[0];
            piece.m_craftingStation = null;
            var fallback = ZNetScene.instance.GetPrefab("piece_chest_wood")?.GetComponent<Piece>()?.m_icon;
            var icons = new IconSheet(typeof(Plugin).Assembly, "WoodSwap.CreativeIcons", w => _log?.LogWarning(w));
            piece.m_icon = icons.For("buildmaterials", null) ?? fallback;
            return true;
        }

        // --- Clicking ----------------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(Player), nameof(Player.TryPlacePiece))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool TryPlacePrefix(Player __instance, Piece piece, ref bool __result)
        {
            if (piece == null || piece.gameObject.name != ToolName) return true;
            __result = false; // never place the tool itself
            if (__instance == Player.m_localPlayer && _instance != null && !_busy && !Windows.IsOpen)
                _instance.StartCoroutine(Count(__instance));
            return false;
        }

        private static IEnumerator Count(Player player)
        {
            var start = player.GetHoveringPiece();
            if (start == null || !start.IsPlacedByPlayer())
            {
                player.Message(MessageHud.MessageType.Center, "Aim at a piece of the building");
                yield break;
            }
            _busy = true;
            try
            {
                var found = new List<Piece>();
                var steps = 0;
                foreach (var piece in Buildings.Connected(start, found))
                    if (++steps % 150 == 0) yield return null;
                var names = found.Where(p => p != null).Select(p => Utils.GetPrefabName(p.gameObject)).ToList();
                var what = Localization.instance.Localize(start.m_name);
                MaterialsList.Show(names.Count == 1 ? $"1 piece: {what}" : $"{MaterialsList.Number(names.Count)} pieces, connected to the {what} you clicked", names);
            }
            finally { _busy = false; }
        }
    }
}
