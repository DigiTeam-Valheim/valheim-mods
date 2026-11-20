// Wood Swap (was Roof Swap): one hammer tool per wood type (Wood, Log, Darkwood, Wood Iron, Ashwood, Scalewood, Timber,
// Decorated Timber), at the end of the hammer's Building tab. Click a wooden building piece with one: it becomes the
// same shape in that wood, on exactly the same snap points (the shape table and the fitting are shared with Ultimate
// Builder: DigiShared/WoodShapes.cs). Thatch roofs to shingles and back are part of it, so it does all Roof Swap did.
// Like Roof Swap: the new pieces cost their normal recipe, from the inventory first and then from nearby chests (the
// whole cost is checked first; if anything is short nothing changes and the message says what); the old pieces drop
// their materials like a removal with the hammer; the normal rules apply (the new piece unlocked, its station in range,
// wards, no-build zones; in no-cost mode, such as Ultimate Builder's creative world, it is free and needs nothing
// unlocked). Hold the whole-building key to swap every piece of the clicked piece's wood in that building.
// The tools are copies of the hammer's Repair tool made into normal menu pieces (their clicks are taken over), with the
// icons of Ultimate Builder (make_creative_icons.py; the sheet is embedded here too). They are in the hammer while this
// sub-mod runs; switched off, they leave it. Ultimate Builder leaves its own Wood Swap tools out while this one is on.
// The first time, Roof Swap's settings and its on/off switch carry over.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using DigiShared;
using HarmonyLib;
using UnityEngine;

namespace WoodSwap
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.woodswap";
        public const string Name = "Digi's Wood Swap";
        public const string Version = "1.0.0";
        private const string Prefix = "piece_woodswap_";
        private const int MostVisited = 20000;

        private static ConfigEntry<KeyboardShortcut> _buildingKey;
        private static ConfigEntry<int> _maxPieces;
        private static ConfigEntry<float> _chestRange;
        private static BepInEx.Logging.ManualLogSource _log;
        private static Plugin _instance;
        private static IconSheet _icons;

        private static readonly List<GameObject> Tools = new List<GameObject>();
        private static GameObject _holder;
        private static ObjectDB _syncedDb;
        private static bool _busy, _failed;

        private static readonly HashSet<Container> Containers = new HashSet<Container>();
        private static readonly MethodInfo CheckAccessMethod = AccessTools.Method(typeof(Container), "CheckAccess", new[] { typeof(long) });
        private static readonly MethodInfo BuildStamina = AccessTools.Method(typeof(Player), "GetBuildStamina");
        private static readonly AccessTools.FieldRef<Player, bool> NoPlacementCost = AccessTools.FieldRefAccess<Player, bool>("m_noPlacementCost");
        private static readonly AccessTools.FieldRef<Player, HashSet<string>> KnownRecipes = AccessTools.FieldRefAccess<Player, HashSet<string>>("m_knownRecipes");
        private static readonly AccessTools.FieldRef<BuildUi, List<Piece>> MenuScratch = AccessTools.FieldRefAccess<BuildUi, List<Piece>>("m_tempPieces");
        private static readonly MethodInfo UpdateKnown = AccessTools.Method(typeof(Player), "UpdateKnownRecipesList");
        private static readonly MethodInfo UpdateAvailable = AccessTools.Method(typeof(Player), "UpdateAvailablePiecesList");
        private static readonly MethodInfo RightItem = AccessTools.Method(typeof(Humanoid), "GetRightItem");

        // A swap to do: the old piece and where its new piece goes.
        private sealed class Job
        {
            public Piece Old;
            public WoodShapes.Fit Fit;
        }

        private void Awake()
        {
            _instance = this;
            _log = Logger;
            _buildingKey = Config.Bind("Wood Swap", "Whole building key", new KeyboardShortcut(KeyCode.LeftShift),
                "Hold this key while clicking with a Wood Swap tool to swap every piece of the clicked piece's wood in that building.");
            _maxPieces = Config.Bind("Wood Swap", "Max pieces per whole-building swap", 300,
                new ConfigDescription("Most pieces one whole-building swap may change (safety limit).", new AcceptableValueRange<int>(10, 2000)));
            _chestRange = Config.Bind("Wood Swap", "Chest range (m)", 50f,
                new ConfigDescription("Missing materials are taken from chests up to this many meters away.", new AcceptableValueRange<float>(0f, 100f)));
            if (FirstTime("from roof swap")) FromRoofSwap();
            _icons = new IconSheet(typeof(Plugin).Assembly, "WoodSwap.CreativeIcons", w => _log?.LogWarning(w));
            _syncedDb = null;
            PatchModule(typeof(Plugin));
        }

        // Roof Swap's settings (this sub-mod was Roof Swap before), once.
        private void FromRoofSwap()
        {
            try
            {
                var path = Path.Combine(BepInEx.Paths.ConfigPath, "digi.valheim.building.cfg");
                if (!File.Exists(path)) return;
                var map = new Dictionary<string, ConfigEntryBase>
                {
                    { "Whole roof key", _buildingKey }, { "Max pieces per whole-roof swap", _maxPieces }, { "Chest range (m)", _chestRange },
                };
                var inSection = false;
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        inSection = line == "[Roof Swap - Roof Swap]";
                        continue;
                    }
                    var eq = line.IndexOf(" = ", StringComparison.Ordinal);
                    if (!inSection || line.StartsWith("#") || eq <= 0) continue;
                    if (map.TryGetValue(line.Substring(0, eq).Trim(), out var entry)) entry.SetSerializedValue(line.Substring(eq + 3));
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning("Roof Swap's settings not carried over: " + e.Message);
            }
        }

        private void Update()
        {
            if (_failed) return;
            try { Sync(); }
            catch (Exception e)
            {
                _failed = true; // once in the log, not every frame
                Logger.LogError("Adding the Wood Swap tools to the hammer failed: " + e);
            }
        }

        private void OnDestroy()
        {
            HammerTable()?.m_pieces.RemoveAll(IsTool);
            Refresh(Player.m_localPlayer);
            if (_holder != null) Destroy(_holder);
            Tools.Clear();
            _syncedDb = null;
            if (_instance == this) _instance = null;
        }

        // --- The tools in the hammer -------------------------------------------------------------------------------

        private static bool IsTool(GameObject prefab) => prefab != null && prefab.name.StartsWith(Prefix, StringComparison.Ordinal);

        private static PieceTable HammerTable()
        {
            var hammer = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab("Hammer") : null;
            var drop = hammer != null ? hammer.GetComponent<ItemDrop>() : null;
            return drop != null ? drop.m_itemData?.m_shared?.m_buildPieces : null;
        }

        // Once per world (the hammer's list belongs to the game and lives on), when the player is there: else the game
        // would greet every tool as a new piece.
        private static void Sync()
        {
            var player = Player.m_localPlayer;
            if (ObjectDB.instance == null || ObjectDB.instance == _syncedDb || ZNetScene.instance == null || player == null) return;
            var table = HammerTable();
            if (table == null) return;
            _syncedDb = ObjectDB.instance;
            table.m_pieces.RemoveAll(IsTool);
            if (!MakeTools(table)) return;
            table.m_pieces.AddRange(Tools);
            foreach (var tool in Tools) KnownRecipes(player).Add(tool.GetComponent<Piece>().m_name);
            Refresh(player);
        }

        // The game refreshes the hammer's list only when it learns a new piece (and these are known already), and a menu
        // update that failed leaves pieces in its scratch list: both are put right here.
        private static void Refresh(Player player)
        {
            if (player == null) return;
            UpdateKnown.Invoke(player, null);
            UpdateAvailable.Invoke(player, null);
            var buildUi = Hud.instance != null ? Hud.instance.m_buildUi : null;
            if (buildUi != null) MenuScratch(buildUi).Clear();
        }

        private static bool MakeTools(PieceTable table)
        {
            if (Tools.Count == WoodShapes.Types.Length && Tools.All(t => t != null)) return true;
            Tools.Clear();
            var repair = table.m_pieces.FirstOrDefault(p => p != null && p.GetComponent<Piece>() != null && p.GetComponent<Piece>().m_repairPiece);
            if (repair == null)
            {
                _log?.LogError("The hammer's repair tool was not found, no Wood Swap tools.");
                return false;
            }
            if (_holder == null)
            {
                _holder = new GameObject("DigiBuilding_WoodSwap");
                _holder.SetActive(false); // the copies never wake up
                DontDestroyOnLoad(_holder);
            }
            for (var i = 0; i < WoodShapes.Types.Length; i++)
            {
                var wood = WoodShapes.Types[i];
                var tool = Instantiate(repair, _holder.transform, false);
                tool.name = Prefix + wood.Replace(" ", "").ToLowerInvariant();
                var piece = tool.GetComponent<Piece>();
                piece.m_repairPiece = false;
                piece.m_removePiece = false;
                piece.m_name = "Wood Swap: " + wood;
                piece.m_description = $"Click a wooden building piece: it becomes {wood}, the same shape on the same snap points. " +
                                      $"Hold {_buildingKey.Value} for every piece of that wood in the building. Costs the new " +
                                      "pieces' materials (from your inventory and nearby chests); the old ones drop theirs.";
                piece.m_category = Piece.PieceCategory.BuildingWorkbench;
                piece.m_resources = new Piece.Requirement[0];
                piece.m_craftingStation = null;
                piece.m_canRotate = true;
                var woodIcon = WoodShapes.PieceOf(WoodShapes.IconPieces[i])?.m_icon;
                piece.m_icon = _icons.For(WoodShapes.ToolId(i), woodIcon) ?? woodIcon;
                Tools.Add(tool);
            }
            return true;
        }

        // --- Clicking ----------------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(Container), "Awake")]
        [HarmonyPostfix]
        private static void ContainerAwake(Container __instance) => Containers.Add(__instance);

        [HarmonyPatch(typeof(Player), nameof(Player.TryPlacePiece))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool TryPlacePrefix(Player __instance, Piece piece, ref bool __result)
        {
            if (piece == null || !IsTool(piece.gameObject)) return true;
            __result = false; // never place the tool itself
            var type = Tools.FindIndex(t => t != null && t.name == piece.gameObject.name);
            if (type < 0 || __instance != Player.m_localPlayer || _instance == null || _busy) return false;
            _instance.StartCoroutine(Swap(__instance, type, _buildingKey.Value.IsPressed()));
            return false;
        }

        private static IEnumerator Swap(Player player, int type, bool building)
        {
            var start = player.GetHoveringPiece();
            if (start == null || !start.IsPlacedByPlayer()) yield break;
            var wood = WoodShapes.Types[type];
            var member = WoodShapes.MemberOf(Utils.GetPrefabName(start.gameObject));
            var what = Localization.instance.Localize(start.m_name);
            if (member == null)
            {
                Say(player, $"Wood Swap: {what} has no other wood types");
                yield break;
            }
            if (member.Type == type && !building)
            {
                Say(player, $"{what} is {wood} already");
                yield break;
            }
            if (member.Row[type].Length == 0 && !building)
            {
                Say(player, $"There is no {wood} version of {what}");
                yield break;
            }
            _busy = true;
            try
            {
                var targets = new List<Piece> { start };
                if (building)
                {
                    var found = new List<Piece>();
                    var steps = 0;
                    foreach (var piece in Building(start, found))
                        if (++steps % 150 == 0) yield return null;
                    targets = found.Where(p => p != null && WoodShapes.MemberOf(Utils.GetPrefabName(p.gameObject))?.Type == member.Type)
                        .Take(_maxPieces.Value).ToList();
                }

                // What goes where; pieces that cannot be changed here or have no such shape in that wood are left.
                var jobs = new List<Job>();
                int none = 0, blocked = 0;
                foreach (var old in targets)
                {
                    var name = Utils.GetPrefabName(old.gameObject);
                    var m = WoodShapes.MemberOf(name);
                    if (m == null || m.Type == type) continue;
                    if (!CanReplace(old)) { blocked++; continue; }
                    var fit = m.Row[type].Length > 0 ? WoodShapes.FitFor(name, m, type, msg => _log?.LogInfo(msg)) : null;
                    if (fit == null) { none++; continue; }
                    jobs.Add(new Job { Old = old, Fit = fit });
                }
                if (jobs.Count == 0)
                {
                    Say(player, blocked > 0 ? "You have no access to change this here" : $"Nothing to change to {wood}");
                    yield break;
                }

                // The rules of building: the new pieces known, their stations in range. In no-cost mode (Ultimate Builder's
                // creative world, the game's own cheat) or with the world modifier "all pieces unlocked" every piece is
                // known, as in the hammer's menu.
                var kinds = jobs.Select(j => j.Fit.Piece).Distinct().ToList();
                var allKnown = NoPlacementCost(player) || ZoneSystem.instance.GetGlobalKey(GlobalKeys.AllPiecesUnlocked);
                var unknown = allKnown ? null : kinds.FirstOrDefault(p => !player.HaveRequirements(p, Player.RequirementMode.IsKnown));
                if (unknown != null)
                {
                    Say(player, $"You have not unlocked {Localization.instance.Localize(unknown.m_name)} yet");
                    yield break;
                }
                if (!NoPlacementCost(player) && !ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoWorkbench))
                {
                    var station = kinds.Select(p => p.m_craftingStation).FirstOrDefault(s =>
                        s != null && !CraftingStation.HaveBuildStationInRange(s.m_name, player.transform.position));
                    if (station != null)
                    {
                        Say(player, $"Needs a {Localization.instance.Localize(station.m_name)} nearby");
                        yield break;
                    }
                }

                // The whole cost first, so a building is either fully paid or not touched at all.
                var cost = new Dictionary<string, int>();
                foreach (var job in jobs)
                {
                    var piece = job.Fit.Piece;
                    if (NoPlacementCost(player) || ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey())) continue;
                    foreach (var req in piece.m_resources)
                    {
                        if (req?.m_resItem == null || req.m_amount <= 0) continue;
                        var item = req.m_resItem.m_itemData.m_shared.m_name;
                        cost.TryGetValue(item, out var n);
                        cost[item] = n + req.m_amount;
                    }
                }
                var missing = cost.Where(c => Available(player, c.Key) < c.Value)
                    .Select(c => $"{Localization.instance.Localize(c.Key)} {Available(player, c.Key)}/{c.Value}").ToList();
                if (missing.Count > 0)
                {
                    Say(player, $"Not enough for {jobs.Count} piece(s): {string.Join(", ", missing)}");
                    yield break;
                }
                foreach (var c in cost) Pay(player, c.Key, c.Value);

                var swapped = 0;
                foreach (var job in jobs)
                {
                    var old = job.Old;
                    if (old == null) continue;
                    var rot = old.transform.rotation;
                    var pos = old.transform.position + rot * job.Fit.Move;
                    old.GetComponent<WearNTear>().Remove(); // drops the old materials, like the hammer's remove
                    player.PlacePiece(job.Fit.Piece, pos, rot * job.Fit.Turn, swapped == 0);
                    if (++swapped % 40 == 0) yield return null;
                }

                player.UseStamina((float)BuildStamina.Invoke(player, null));
                var tool = (ItemDrop.ItemData)RightItem.Invoke(player, null);
                if (tool != null && tool.m_shared.m_useDurability)
                    tool.m_durability -= tool.m_shared.m_useDurabilityDrain * Game.m_durabilityRate;
                var text = swapped == 1 ? $"{what} is {wood} now" : $"{swapped} pieces are {wood} now";
                var notes = new List<string>();
                if (none > 0) notes.Add($"{none} have no {wood} version");
                if (blocked > 0) notes.Add($"{blocked} skipped: no access");
                if (notes.Count > 0) text += " (" + string.Join(", ", notes) + ")";
                player.Message(MessageHud.MessageType.TopLeft, text);
            }
            finally { _busy = false; }
        }

        private static void Say(Player player, string text) => player.Message(MessageHud.MessageType.Center, text);

        private static bool CanReplace(Piece piece)
        {
            if (piece == null || !piece.m_canBeRemoved || !piece.CanBeRemoved() || !piece.IsPlacedByPlayer()) return false;
            if (piece.GetComponent<WearNTear>() == null || piece.GetComponent<ZNetView>() == null) return false;
            var pos = piece.transform.position;
            if (Location.IsInsideNoBuildLocation(pos)) return false;
            return PrivateArea.CheckAccess(pos, 0f, true, false);
        }

        // Every piece touching the start, and every piece touching those: fills found, a step per piece.
        private static IEnumerable<Piece> Building(Piece start, List<Piece> found)
        {
            var seen = new HashSet<Piece> { start };
            found.Add(start);
            var hits = new Collider[128];
            var mask = LayerMask.GetMask("piece", "piece_nonsolid");
            for (var i = 0; i < found.Count && found.Count < MostVisited; i++)
            {
                var piece = found[i];
                if (piece == null) continue;
                var bounds = new Bounds(piece.transform.position, Vector3.zero);
                foreach (var collider in piece.GetComponentsInChildren<Collider>())
                    if (collider.enabled && !collider.isTrigger) bounds.Encapsulate(collider.bounds);
                var count = Physics.OverlapBoxNonAlloc(bounds.center, bounds.extents + Vector3.one * 0.1f, hits, Quaternion.identity, mask);
                for (var h = 0; h < count; h++)
                {
                    var other = hits[h] != null ? hits[h].GetComponentInParent<Piece>() : null;
                    if (other != null && other.IsPlacedByPlayer() && seen.Add(other)) found.Add(other);
                }
                yield return piece;
            }
        }

        // --- Paying ------------------------------------------------------------------------------------------------

        private static IEnumerable<Inventory> ChestInventories(Player player)
        {
            var origin = player.transform.position;
            var maxSqr = _chestRange.Value * _chestRange.Value;
            Containers.RemoveWhere(c => c == null);
            foreach (var chest in Containers.OrderBy(c => (c.transform.position - origin).sqrMagnitude))
            {
                if ((chest.transform.position - origin).sqrMagnitude > maxSqr) break;
                if (!chest.isActiveAndEnabled || chest.GetInventory() == null) continue;
                var piece = chest.GetComponentInParent<Piece>();
                if (piece == null || !piece.IsPlacedByPlayer()) continue;
                if (chest.m_wagon != null || chest.GetComponentInParent<Ship>() != null) continue;
                if (!chest.IsOwner() || chest.IsInUse()) continue;   // only chests this PC may change right now
                if (!(bool)CheckAccessMethod.Invoke(chest, new object[] { player.GetPlayerID() })) continue;
                yield return chest.GetInventory();
            }
        }

        private static int Available(Player player, string name) =>
            player.GetInventory().CountItems(name) + ChestInventories(player).Sum(inv => inv.CountItems(name));

        private static void Pay(Player player, string name, int amount)
        {
            var fromInventory = Mathf.Min(amount, player.GetInventory().CountItems(name));
            if (fromInventory > 0) player.GetInventory().RemoveItem(name, fromInventory);
            var left = amount - fromInventory;
            foreach (var inventory in ChestInventories(player))
            {
                if (left <= 0) break;
                var take = Mathf.Min(left, inventory.CountItems(name));
                if (take <= 0) continue;
                inventory.RemoveItem(name, take);
                left -= take;
            }
        }
    }
}
