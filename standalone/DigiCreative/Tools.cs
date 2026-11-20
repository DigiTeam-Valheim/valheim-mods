// The creative world's own hammer tools (Wood Swap, Build Materials): copies of the hammer's Repair tool made into normal
// menu pieces, like Roof Swap, at the end of the hammer's Building tab. Clicking with one goes to its own action instead
// of placing anything. They are in the hammer only while the creative world is played (the hammer's list is the game's
// own and lives on into the next world), and known without the game's "new piece" message for each.
using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using DigiShared;
using UnityEngine;

namespace DigiCreative
{
    internal class Tools : MonoBehaviour
    {
        internal static Func<bool> InCreative = () => false;
        internal static BepInEx.Logging.ManualLogSource Log;
        private const string Prefix = "piece_digi_";

        private sealed class Tool
        {
            public string Id, Name, Description, Icon;
            public Func<bool> On;
            public Action<Player> Click;
            public GameObject Prefab;
        }

        private static readonly List<Tool> All = new List<Tool>();
        private static GameObject _holder;
        private static ObjectDB _syncedDb;
        private static int _synced;
        private static bool _failed;

        // Icon: the prefab whose icon the tool shows (a piece or an item).
        internal static void Add(string id, string name, string description, string icon, Func<bool> on, Action<Player> click) =>
            All.Add(new Tool { Id = Prefix + id, Name = name, Description = description, Icon = icon, On = on, Click = click });

        internal static void Patch(Harmony harmony) =>
            harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.TryPlacePiece)),
                prefix: new HarmonyMethod(typeof(Tools), nameof(TryPlace)) { priority = Priority.First });

        private void Update()
        {
            if (_failed) return;
            try { Sync(); }
            catch (Exception e)
            {
                _failed = true; // once in the log, not every frame
                Log?.LogError("Adding the creative tools to the hammer failed: " + e);
            }
        }

        private void OnDestroy()
        {
            HammerTable()?.m_pieces.RemoveAll(IsTool);
            if (_holder != null) Destroy(_holder);
        }

        private static bool IsTool(GameObject prefab) => prefab != null && prefab.name.StartsWith(Prefix, StringComparison.Ordinal);

        private static PieceTable HammerTable()
        {
            var hammer = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab("Hammer") : null;
            var drop = hammer != null ? hammer.GetComponent<ItemDrop>() : null;
            return drop != null ? drop.m_itemData?.m_shared?.m_buildPieces : null;
        }

        // Runs every frame: only a bit per tool (wanted or not) is worked out until something changed.
        private static void Sync()
        {
            var want = 0;
            if (InCreative())
                for (var i = 0; i < All.Count; i++)
                    if (All[i].On()) want |= 1 << i;
            if (ObjectDB.instance == null || (ObjectDB.instance == _syncedDb && want == _synced)) return;
            // The icons come from the prefabs; and the player first, else the game greets each tool as a new piece.
            if (want != 0 && (ZNetScene.instance == null || Player.m_localPlayer == null)) return;
            var table = HammerTable();
            if (table == null) return;
            _syncedDb = ObjectDB.instance;
            _synced = want;
            table.m_pieces.RemoveAll(IsTool);
            var player = Player.m_localPlayer;
            for (var i = 0; i < All.Count; i++)
            {
                var tool = All[i];
                if ((want & (1 << i)) == 0 || (tool.Prefab == null && !Make(table, tool))) continue;
                table.m_pieces.Add(tool.Prefab);
                if (player != null) player.m_knownRecipes.Add(tool.Prefab.GetComponent<Piece>().m_name);
            }
            if (player == null) return;
            player.UpdateKnownRecipesList();
            // The game refreshes the hammer's list only when it learns a new piece, and these are known already: without
            // this the list keeps tools that are gone (after a world change or a live reload), which breaks the hammer menu
            // and other mods that read the selected piece.
            player.UpdateAvailablePiecesList();
            // A build menu update that failed on a tool that was gone leaves its list in the menu's scratch list (the game
            // empties that only after a successful update), so every later opening would fail again and add the list once
            // more. Outside an update it is always empty: empty it (only removing the gone ones left thousands of
            // entries after many failed openings, and the next opening made a button for each).
            var buildUi = Hud.instance != null ? Hud.instance.m_buildUi : null;
            if (buildUi != null) buildUi.m_tempPieces.Clear();
        }

        private static bool Make(PieceTable table, Tool tool)
        {
            var repair = table.m_pieces.FirstOrDefault(p => p != null && p.GetComponent<Piece>() != null && p.GetComponent<Piece>().m_repairPiece);
            if (repair == null)
            {
                Log?.LogError("The hammer's repair tool was not found, no " + tool.Name + " tool.");
                return false;
            }
            if (_holder == null)
            {
                _holder = new GameObject("DigiCreative_Tools");
                _holder.SetActive(false); // the copies never wake up
                DontDestroyOnLoad(_holder);
            }
            var prefab = Instantiate(repair, _holder.transform, false);
            prefab.name = tool.Id;
            var piece = prefab.GetComponent<Piece>();
            piece.m_repairPiece = false;
            piece.m_removePiece = false;
            piece.m_name = tool.Name;
            piece.m_description = tool.Description;
            piece.m_category = Piece.PieceCategory.BuildingWorkbench;
            piece.m_canRotate = true; // the scroll wheel turns a schematic
            piece.m_resources = new Piece.Requirement[0];
            piece.m_craftingStation = null;
            // The tool's own icon (Icons.cs), with the prefab's icon inside it where it has room for one (Wood Swap:
            // the wood); the prefab's icon alone if the icon sheet is missing.
            var icon = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(tool.Icon) : null;
            var iconPiece = icon != null ? icon.GetComponent<Piece>() : null;
            var iconItem = icon != null ? icon.GetComponent<ItemDrop>() : null;
            var fallback = iconPiece != null ? iconPiece.m_icon : iconItem != null ? iconItem.m_itemData.GetIcon() : null;
            piece.m_icon = Icons.For(tool.Id.Substring(Prefix.Length), fallback) ?? fallback;
            tool.Prefab = prefab;
            return true;
        }

        private static bool TryPlace(Player __instance, Piece piece, ref bool __result)
        {
            if (piece == null || !IsTool(piece.gameObject)) return true;
            __result = false; // never place the tool itself
            var tool = All.FirstOrDefault(t => t.Id == piece.gameObject.name);
            if (tool == null || __instance != Player.m_localPlayer || !InCreative()) return false;
            try { tool.Click(__instance); }
            catch (Exception e) { Log?.LogError(tool.Name + " failed: " + e); }
            return false;
        }

        // --- Shared by the tools -----------------------------------------------------------------------------------

        internal static Piece PieceOf(string name)
        {
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(name) : null;
            return prefab != null ? prefab.GetComponent<Piece>() : null;
        }

        // The box around a piece's solid colliders, and a whole building: DigiShared/Buildings.cs (also in Building Tweaks).
        internal static Bounds BoundsOf(Piece piece) => Buildings.BoundsOf(piece);

        internal static IEnumerable<Piece> Building(Piece start, List<Piece> found, int max = 20000) => Buildings.Connected(start, found, max);

        // Whether Building Tweaks is here with this sub-mod switched on (its F1 "0 - Sub-mods"): then it brings the same
        // tool to every world and ours stays out of the hammer, so it shows one. Looked up once per sub-mod.
        private static readonly Dictionary<string, ConfigEntry<bool>> BuildingTweaks = new Dictionary<string, ConfigEntry<bool>>();

        internal static bool BuildingTweaksHas(string subMod)
        {
            if (!BuildingTweaks.TryGetValue(subMod, out var entry))
            {
                if (BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue("digi.valheim.building", out var info) && info.Instance != null)
                    info.Instance.Config.TryGetEntry("0 - Sub-mods", subMod, out entry);
                BuildingTweaks[subMod] = entry;
            }
            return entry != null && entry.Value;
        }
    }
}
