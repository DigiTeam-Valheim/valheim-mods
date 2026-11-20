// Schematics: a hammer tool (Building tab, at the end) for your normal worlds: a click opens the list of the schematics
// you saved with Ultimate Builder (in its creative world), each with its picture, piece count, size and date, and a
// Materials button: every material the schematic costs, what you have in your inventory and the chests nearby (green:
// enough, red: not; the chests within Build Materials' chest range) and what is missing; Back returns to the list.
// Place schematics (a setting, off by default): the list gets a Place button. The building then follows your aim as a
// ghost and snaps onto the buildings around it (DigiShared/SchematicPlacer.cs, as in Ultimate Builder); a click builds
// all of it at once and pays every material up front, from your inventory first and then the nearest chests (Build
// Materials' chest range; a chest someone has open is skipped). As when building by hand: only pieces you have unlocked,
// the crafting stations they need within range of where you stand (unless the world needs no workbench), not in someone
// else's ward or a no-build place; a world without build costs (or the game's no-cost mode) charges nothing. A piece
// already standing in the same place is left out and not paid. Nothing is placed or paid when any of it fails, and the
// message says why.
// The list and the files are shared with Ultimate Builder (DigiShared/SchematicLibrary.cs); in Ultimate Builder's
// creative world its own Schematics tool (which also places them, for free) is used and this one stays out of the hammer.
// The tool is a copy of the hammer's Repair tool made into a normal menu piece (its click is taken over), with the icon
// of make_creative_icons.py. It is in the hammer while this sub-mod runs; switched off, it leaves it.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using DigiShared;
using HarmonyLib;
using UnityEngine;

namespace Schematics
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.schematics";
        public const string Name = "Digi's Schematics";
        public const string Version = "1.0.0";
        private const string ToolName = "piece_schematics_tool";

        private static BepInEx.Logging.ManualLogSource _log;
        private static Plugin _instance;
        private static GameObject _tool, _holder;
        private static ObjectDB _syncedDb;
        private static bool _failed;
        private static ConfigEntry<bool> _place;
        private static ConfigEntry<KeyboardShortcut> _libraryKey, _freeKey;
        private static SchematicPlacer.Host _host;

        private static readonly AccessTools.FieldRef<Player, HashSet<string>> KnownRecipes = AccessTools.FieldRefAccess<Player, HashSet<string>>("m_knownRecipes");
        private static readonly AccessTools.FieldRef<BuildUi, List<Piece>> MenuScratch = AccessTools.FieldRefAccess<BuildUi, List<Piece>>("m_tempPieces");
        private static readonly MethodInfo UpdateKnown = AccessTools.Method(typeof(Player), "UpdateKnownRecipesList");
        private static readonly MethodInfo UpdateAvailable = AccessTools.Method(typeof(Player), "UpdateAvailablePiecesList");

        private void Awake()
        {
            _instance = this;
            _log = Logger;
            SchematicLibrary.Log = Logger;
            _syncedDb = null;
            _place = Config.Bind("General", "Place schematics", false,
                "The list gets a Place button: the building follows your aim as a ghost (it snaps onto the buildings around it) " +
                "and a click builds all of it at once, paying every material from your inventory and the nearby chests. As by " +
                "hand: only pieces you have unlocked, the crafting stations they need within range of where you stand, and not " +
                "in someone else's ward. A piece already standing in the same place is left out and not paid.");
            _libraryKey = Config.Bind("General", "Library key", new KeyboardShortcut(KeyCode.LeftShift),
                "Hold this while clicking with the Schematics tool (a schematic chosen): the list opens again.");
            _freeKey = Config.Bind("General", "Free placement key", new KeyboardShortcut(KeyCode.LeftAlt),
                "Hold this while placing a schematic: it does not snap, and the scroll wheel moves it up and down instead of turning it.");
            _place.SettingChanged += (_, __) =>
            {
                if (!_place.Value) SchematicPlacer.Drop();
            };
            _host = new SchematicPlacer.Host
            {
                Runner = this,
                Allowed = player => _place.Value && ToolSelected(player),
                FreeKey = () => _freeKey.Value,
                HeightStep = () => 0.25f,
                GhostPieces = () => 1500,
                Reach = 50f,
                Check = Check,
                Done = Done,
            };
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
                Logger.LogError("Adding the Schematics tool to the hammer failed: " + e);
            }
        }

        private void LateUpdate() => SchematicPlacer.Tick();

        private void OnDestroy()
        {
            SchematicPlacer.Drop();
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

        // In Ultimate Builder's creative world its own Schematics tool (with Place) is there: this one stays out.
        private static bool UltimateBuilderHasIt()
        {
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue("digi.valheim.creativeworld", out var info) || info.Instance == null) return false;
            var assembly = info.Instance.GetType().Assembly;
            var active = AccessTools.Field(info.Instance.GetType(), "_active")?.GetValue(null) as bool?;
            if (active != true) return false;
            var on = AccessTools.Field(assembly.GetType("DigiCreative.Schematics"), "On")?.GetValue(null) as ConfigEntry<bool>;
            return on == null || on.Value;
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
            if (!UltimateBuilderHasIt() && MakeTool(table))
            {
                table.m_pieces.Add(_tool);
                KnownRecipes(player).Add(_tool.GetComponent<Piece>().m_name);
            }
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
                _log?.LogError("The hammer's repair tool was not found, no Schematics tool.");
                return false;
            }
            if (_holder == null)
            {
                _holder = new GameObject("DigiBuilding_Schematics");
                _holder.SetActive(false); // the copy never wakes up
                DontDestroyOnLoad(_holder);
            }
            _tool = Instantiate(repair, _holder.transform, false);
            _tool.name = ToolName;
            var piece = _tool.GetComponent<Piece>();
            piece.m_repairPiece = false;
            piece.m_removePiece = false;
            piece.m_name = "Schematics";
            piece.m_description = "Click: your saved schematics (made with Ultimate Builder), with their pictures. Materials shows " +
                                  "what one costs, what you have (inventory and nearby chests) and what is missing. With Place " +
                                  "schematics on (F1), Place puts one where you aim and a click builds it, paid from your inventory " +
                                  "and the chests nearby.";
            piece.m_category = Piece.PieceCategory.BuildingWorkbench;
            piece.m_resources = new Piece.Requirement[0];
            piece.m_craftingStation = null;
            var fallback = ZNetScene.instance.GetPrefab("piece_cartographytable")?.GetComponent<Piece>()?.m_icon;
            var icons = new IconSheet(typeof(Plugin).Assembly, "WoodSwap.CreativeIcons", w => _log?.LogWarning(w));
            piece.m_icon = icons.For("schematics", null) ?? fallback;
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
            if (__instance != Player.m_localPlayer || Windows.IsOpen || SchematicPlacer.Pasting) return false;
            if (_place.Value && SchematicPlacer.Chosen && !_libraryKey.Value.IsPressed()) SchematicPlacer.Paste(__instance);
            else OpenLibrary();
            return false;
        }

        private static bool ToolSelected(Player player)
        {
            var selected = SchematicPlacer.Selected(player);
            return selected != null && selected.gameObject.name == ToolName;
        }

        private static void OpenLibrary()
        {
            if (!_place.Value)
            {
                SchematicLibrary.Open("Materials: what a schematic costs, what you have (inventory and nearby chests) and what is missing.",
                    null, (row, s, width) => Windows.Button(row, "Materials", 124f, 70f, 116f, 38f, () => SchematicLibrary.ShowMaterials(s, OpenLibrary)));
                return;
            }
            SchematicLibrary.Open($"Place: follows your aim; wheel turns it, {_freeKey.Value} + wheel: up/down; click builds it (paid). {_libraryKey.Value} + click: this list.",
                null, (row, s, width) =>
                {
                    Windows.Button(row, "Place", 124f, 70f, 116f, 38f, () => Choose(s));
                    Windows.Button(row, "Materials", 248f, 70f, 116f, 38f, () => SchematicLibrary.ShowMaterials(s, OpenLibrary));
                });
        }

        // --- Placing (normal worlds, paid) ---------------------------------------------------------------------------

        private static void Choose(SchematicLibrary.Schematic s)
        {
            var full = SchematicLibrary.Full(s);
            if (full == null || _instance == null) return;
            Windows.Close();
            if (full.Pieces.Count == 0)
            {
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "That schematic has no pieces");
                return;
            }
            SchematicPlacer.Choose(_host, full.Name, full.File, full.Pieces, full.Size, full.Ground);
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, $"{full.Name}: the scroll wheel turns it, a click builds it");
        }

        private static string Local(string name) => Localization.instance != null ? Localization.instance.Localize(name) : name;

        private static string List(List<string> names) =>
            string.Join(", ", names.Take(4)) + (names.Count > 4 ? $" and {names.Count - 4} more" : "");

        private static bool Refuse(Player player, string why)
        {
            player.Message(MessageHud.MessageType.Center, why);
            return false;
        }

        // Everything building it by hand would need, then the materials, taken up front. False: nothing placed or paid.
        private static bool Check(Player player, SchematicPlacer.Plan plan)
        {
            if (plan.Items.Count == 0)
                return Refuse(player, plan.AlreadyThere > 0 ? "All of it stands there already" : "None of its pieces exist in this game (mods missing?)");
            if (SchematicPlacer.NoPlacementCost(player)) return true; // the game's no-cost mode: free, as by hand
            var known = KnownRecipes(player);
            var locked = plan.Items.Select(i => i.Piece).Distinct()
                .Where(p => !p.m_enabled || !known.Contains(p.m_name) || (p.m_dlc.Length > 0 && (DLCMan.instance == null || !DLCMan.instance.IsDLCInstalled(p.m_dlc))))
                .Select(p => Local(p.m_name)).Distinct().ToList();
            if (locked.Count > 0) return Refuse(player, "Not unlocked yet: " + List(locked));
            if (!ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoWorkbench))
            {
                var far = plan.Items.Where(i => i.Piece.m_craftingStation != null).Select(i => i.Piece.m_craftingStation.m_name).Distinct()
                    .Where(station => CraftingStation.HaveBuildStationInRange(station, player.transform.position) == null).Select(Local).ToList();
                if (far.Count > 0) return Refuse(player, "Needs nearby: " + List(far));
            }
            if (plan.Items.Any(i => Location.IsInsideNoBuildLocation(i.Pos))) return Refuse(player, "$msg_nobuildzone");
            if (plan.Items.Any(i => !PrivateArea.CheckAccess(i.Pos, 0f, true, false))) return Refuse(player, "$msg_privatezone");
            return Pay(player, plan);
        }

        // Every material at once: the inventory first, then the nearest chests (claimed first: a chest saves a change
        // only on the game that owns it). A world without build costs charges nothing, as by hand.
        private static bool Pay(Player player, SchematicPlacer.Plan plan)
        {
            var cost = new Dictionary<string, int>();
            foreach (var item in plan.Items)
            {
                if (ZoneSystem.instance.GetGlobalKey(item.Piece.FreeBuildKey()) || item.Piece.m_resources == null) continue;
                foreach (var req in item.Piece.m_resources)
                {
                    if (req?.m_resItem == null || req.m_amount <= 0) continue;
                    var name = req.m_resItem.m_itemData.m_shared.m_name;
                    cost.TryGetValue(name, out var n);
                    cost[name] = n + req.m_amount;
                }
            }
            if (cost.Count == 0) return true;
            var inventory = player.GetInventory();
            var chests = MaterialsList.Chests(player).Where(c => !c.IsInUse()).ToList();
            var missing = new List<string>();
            foreach (var pair in cost)
            {
                var have = inventory.CountItems(pair.Key) + chests.Sum(c => c.GetInventory().CountItems(pair.Key));
                if (have < pair.Value) missing.Add($"{MaterialsList.Number(pair.Value - have)} {Local(pair.Key)}");
            }
            if (missing.Count > 0) return Refuse(player, "Missing " + List(missing) + " (Materials in the list shows all)");
            foreach (var pair in cost)
            {
                var left = pair.Value;
                var mine = Mathf.Min(left, inventory.CountItems(pair.Key));
                if (mine > 0)
                {
                    inventory.RemoveItem(pair.Key, mine);
                    left -= mine;
                }
                foreach (var chest in chests)
                {
                    if (left <= 0) break;
                    var there = chest.GetInventory().CountItems(pair.Key);
                    if (there <= 0) continue;
                    var nview = chest.GetComponent<ZNetView>();
                    if (nview != null && nview.IsValid() && !nview.IsOwner()) nview.ClaimOwnership();
                    var take = Mathf.Min(left, there);
                    chest.GetInventory().RemoveItem(pair.Key, take);
                    left -= take;
                }
            }
            _log?.LogInfo($"Schematic {plan.Name}: paid " + string.Join(", ", cost.Select(c => c.Value + " " + c.Key)));
            return true;
        }

        private static void Done(Player player, SchematicPlacer.Plan plan, List<Piece> made)
        {
            var text = $"Built {plan.Name}: {MaterialsList.Number(made.Count)} pieces";
            var extra = new List<string>();
            if (plan.AlreadyThere > 0) extra.Add($"{MaterialsList.Number(plan.AlreadyThere)} already there");
            var missing = SchematicPlacer.MissingText(plan);
            if (missing.Length > 0) extra.Add(missing);
            if (extra.Count > 0) text += " (" + string.Join("; ", extra) + ")";
            player?.Message(MessageHud.MessageType.TopLeft, text);
        }
    }
}
