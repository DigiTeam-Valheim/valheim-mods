// Nearby chests for Craft From Chests (Crafting and Automation) and Store Nearby (Inventory and QoL): which chests count,
// the chests near a spot, what they hold together, and taking items out of them safely in multiplayer.
// Compiled into the publicized projects only (it reads private members of Container); each project has its own copy.
//
// Fast by design (Local Craft From Storage rebuilt its chest list and loaded every chest again for every single count):
// - Loaded chests are known from Container.Awake: no scene search, ever.
// - The chests near a spot are listed at most once a second, or when you move a metre (wards, privacy and the chest's
//   checkboxes are checked then, not per count).
// - What they hold is added up per item once, and a chest is only read again when its saved data changed (the game's
//   own revision number); a count is then a dictionary lookup.
// - Multiplayer: a chest can only be changed by the client that owns it. A chest that is ours (always in single player,
//   nearly always for the host or the player who stands closest) is used at once. Any other chest is asked from its
//   owner with the game's own open request (the same one opening a chest sends), but without opening its window; while
//   that answer is on its way the action waits (a craft) or asks to be repeated (a build). A chest someone has open is
//   never touched.
using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace DigiStorage
{
    // What a sub-mod allows: range, which kinds of containers, and the chest checkbox it honours.
    public sealed class ChestRules
    {
        public float Range = 50f;
        public bool Personal;      // your own private (personal) chests
        public bool CartsShips;    // carts and ship storage
        public string BlockFlag;   // ZDO bool on the chest: true = this sub-mod leaves the chest alone
    }

    public static class Chests
    {
        private static readonly string[] Forbidden = { "Player_tombstone", "piece_chest_treasure", "piece_gift1", "piece_gift2", "piece_gift3", "incinerator", "Trailership" };

        // ------------------------------------------------------------------ the loaded chests

        private sealed class Facts
        {
            public ZNetView View;
            public Piece Piece;
            public bool Cart, Ship, Built;
        }

        private static readonly List<Container> Loaded = new List<Container>();
        private static readonly Dictionary<Container, Facts> Known = new Dictionary<Container, Facts>();
        private static Harmony _harmony;
        private static int _users, _version;

        // Each sub-mod that uses chests calls Use() when it starts and Release() when it stops: the patches live as long as
        // one of them runs.
        public static void Use()
        {
            if (_users++ > 0) return;
            _harmony = new Harmony("digi.storage." + typeof(Chests).Assembly.GetName().Name + "." + Guid.NewGuid().ToString("N"));
            _harmony.Patch(AccessTools.Method(typeof(Container), "Awake"), postfix: new HarmonyMethod(typeof(Chests), nameof(Awoke)));
            _harmony.Patch(AccessTools.Method(typeof(Container), "RPC_OpenResponse"),
                prefix: new HarmonyMethod(typeof(Chests), nameof(OpenResponse)) { priority = Priority.First });
            foreach (var chest in UnityEngine.Object.FindObjectsByType<Container>(FindObjectsSortMode.None)) Add(chest);
        }

        public static void Release()
        {
            if (_users <= 0 || --_users > 0) return;
            _harmony?.UnpatchSelf();
            _harmony = null;
            Loaded.Clear();
            Known.Clear();
            Pending.Clear();
            Refused.Clear();
            _version++;
        }

        private static void Awoke(Container __instance) => Add(__instance);

        private static void Add(Container chest)
        {
            if (chest == null || chest.m_nview == null || chest.m_nview.GetZDO() == null || Known.ContainsKey(chest)) return;
            var piece = chest.GetComponentInParent<Piece>();
            var name = Utils.GetPrefabName((chest.m_rootObjectOverride != null ? chest.m_rootObjectOverride.gameObject : chest.gameObject));
            var built = piece != null && chest.GetComponent<TombStone>() == null && Array.IndexOf(Forbidden, name) < 0 &&
                        !name.StartsWith("TreasureChest_", StringComparison.OrdinalIgnoreCase) &&
                        !name.StartsWith("shipwreck_", StringComparison.OrdinalIgnoreCase);
            Known[chest] = new Facts
            {
                View = chest.m_nview, Piece = piece, Built = built,
                Cart = chest.m_wagon != null,
                Ship = chest.GetComponentInParent<Ship>() != null,
            };
            Loaded.Add(chest);
            _version++;
        }

        // ------------------------------------------------------------------ which chests

        private static bool Allowed(Container chest, Facts facts, ChestRules rules, long playerId)
        {
            if (!facts.Built || facts.View == null || !facts.View.IsValid()) return false;
            var zdo = facts.View.GetZDO();
            if (facts.Piece.GetCreator() == 0L) return false; // not built by a player (dungeon and village chests)
            if ((facts.Cart || facts.Ship) && !rules.CartsShips) return false;
            if (chest.m_privacy == Container.PrivacySetting.Private && (!rules.Personal || !chest.CheckAccess(playerId))) return false;
            if (chest.m_privacy == Container.PrivacySetting.Group) return false;
            if (!string.IsNullOrEmpty(rules.BlockFlag) && zdo.GetBool(rules.BlockFlag)) return false;
            if (chest.m_checkGuardStone && !PrivateArea.CheckAccess(chest.transform.position, 0f, false)) return false;
            return true;
        }

        // Someone else has it open (only the owner knows m_inUse; others see the saved flag).
        public static bool OpenByOthers(Container chest)
        {
            var view = chest != null ? chest.m_nview : null;
            return view != null && view.IsValid() && !view.IsOwner() && view.GetZDO().GetInt(ZDOVars.s_inUse) == 1;
        }

        // ------------------------------------------------------------------ the chests near a spot

        public sealed class Area
        {
            public readonly ChestRules Rules;
            internal readonly List<Container> List = new List<Container>();
            internal Vector3 Origin;
            internal float Time = -100f;
            internal int Version = -1;
            internal float Range = -1f;
            internal bool Personal, CartsShips;

            // What they hold: per chest the revision it was read at, and the sums.
            internal readonly Dictionary<Container, uint> Read = new Dictionary<Container, uint>();
            internal readonly Dictionary<string, List<Stack>> Sums = new Dictionary<string, List<Stack>>();
            internal int SumsFrame = -1;
            internal bool Dirty = true;
            internal readonly Dictionary<string, int> ReadyMemo = new Dictionary<string, int>();
            internal int ReadyFrame = -1;
            internal bool LeaveOne; // the "Leave 1 in each chest" rule the sums were made with

            public Area(ChestRules rules) { Rules = rules; }
        }

        internal sealed class Stack
        {
            public int Quality, WorldLevel, Amount;
        }

        // Nearest first. Refreshed when the spot moved a metre, after a second, or when chests were built or loaded.
        public static List<Container> Near(Area area, Vector3 origin)
        {
            var now = UnityEngine.Time.unscaledTime;
            var rules = area.Rules;
            if (area.Version == _version && now - area.Time < 1f && (origin - area.Origin).sqrMagnitude < 1f &&
                area.Range == rules.Range && area.Personal == rules.Personal && area.CartsShips == rules.CartsShips)
                return area.List;
            area.Version = _version;
            area.Time = now;
            area.Origin = origin;
            area.Range = rules.Range;
            area.Personal = rules.Personal;
            area.CartsShips = rules.CartsShips;
            area.List.Clear();
            var playerId = Game.instance != null ? Game.instance.GetPlayerProfile().GetPlayerID() : 0L;
            var range2 = rules.Range * rules.Range;
            for (var i = Loaded.Count - 1; i >= 0; i--)
            {
                var chest = Loaded[i];
                if (chest == null)
                {
                    Loaded.RemoveAt(i);
                    _version++;
                    continue;
                }
                if ((chest.transform.position - origin).sqrMagnitude > range2) continue;
                if (!Known.TryGetValue(chest, out var facts) || !Allowed(chest, facts, rules, playerId)) continue;
                area.List.Add(chest);
            }
            if (Known.Count > Loaded.Count + 64) // forget destroyed ones now and then
            {
                var gone = new List<Container>();
                foreach (var chest in Known.Keys)
                    if (chest == null) gone.Add(chest);
                foreach (var chest in gone) Known.Remove(chest);
            }
            area.List.Sort((a, b) => (a.transform.position - origin).sqrMagnitude.CompareTo((b.transform.position - origin).sqrMagnitude));
            area.Dirty = true;
            area.Version = _version;
            return area.List;
        }

        // ------------------------------------------------------------------ what they hold

        // Reads a chest's saved contents now if they changed since the game last did (it checks only once a second).
        public static void Fresh(Container chest)
        {
            if (chest == null || chest.m_inUse || chest.m_nview == null || !chest.m_nview.IsValid()) return;
            if (chest.m_nview.GetZDO().DataRevision != chest.m_lastRevision) chest.Load();
        }

        private static void UpdateSums(Area area)
        {
            if (area.SumsFrame == UnityEngine.Time.frameCount) return;
            area.SumsFrame = UnityEngine.Time.frameCount;
            var dirty = area.Dirty || area.LeaveOne != StoreRules.LeaveOne;
            area.LeaveOne = StoreRules.LeaveOne;
            foreach (var chest in area.List)
            {
                if (chest == null || chest.m_nview == null || !chest.m_nview.IsValid()) continue;
                var revision = chest.m_nview.GetZDO().DataRevision;
                if (area.Read.TryGetValue(chest, out var read) && read == revision) continue;
                Fresh(chest);
                area.Read[chest] = chest.m_nview.GetZDO().DataRevision;
                dirty = true;
            }
            if (!dirty) return;
            area.Dirty = false;
            if (area.Read.Count > area.List.Count)
            {
                var keep = new HashSet<Container>(area.List);
                var gone = new List<Container>();
                foreach (var chest in area.Read.Keys)
                    if (chest == null || !keep.Contains(chest)) gone.Add(chest);
                foreach (var chest in gone) area.Read.Remove(chest);
            }
            foreach (var list in area.Sums.Values) list.Clear();
            foreach (var chest in area.List)
            {
                if (chest == null || OpenByOthers(chest)) continue;
                Kept.Clear();
                foreach (var item in chest.GetInventory().GetAllItems())
                {
                    if (item?.m_shared == null) continue;
                    // "Leave 1 in each chest": the first one of each item in a chest does not count.
                    var amount = area.LeaveOne && Kept.Add(item.m_shared.m_name) ? item.m_stack - 1 : item.m_stack;
                    if (!area.Sums.TryGetValue(item.m_shared.m_name, out var stacks))
                        area.Sums[item.m_shared.m_name] = stacks = new List<Stack>(2);
                    Stack found = null;
                    foreach (var s in stacks)
                        if (s.Quality == item.m_quality && s.WorldLevel == item.m_worldLevel)
                        {
                            found = s;
                            break;
                        }
                    if (found == null) stacks.Add(found = new Stack { Quality = item.m_quality, WorldLevel = item.m_worldLevel });
                    found.Amount += amount;
                }
            }
        }

        private static readonly HashSet<string> Kept = new HashSet<string>();

        // What may be taken from one chest: with "Leave 1 in each chest" (StoreRules) its last one of the item stays.
        private static int Usable(Inventory inventory, string name, int quality, bool matchWorldLevel)
        {
            var have = inventory.CountItems(name, quality, matchWorldLevel);
            if (have <= 0 || !StoreRules.LeaveOne) return have;
            return Mathf.Max(0, Mathf.Min(have, inventory.CountItems(name, -1, false) - 1));
        }

        // Like Inventory.CountItems, for all the chests of the area together.
        public static int Count(Area area, string name, int quality = -1, bool matchWorldLevel = true)
        {
            UpdateSums(area);
            if (name == null || !area.Sums.TryGetValue(name, out var stacks)) return 0;
            var total = 0;
            foreach (var s in stacks)
                if ((quality < 0 || s.Quality == quality) && (!matchWorldLevel || s.WorldLevel >= Game.m_worldLevel))
                    total += s.Amount;
            return total;
        }

        // Counted fresh, only the chests that can be used right now (ours, not open by someone else): for paying.
        // Remembered for the rest of the frame (a recipe list asks for the same items many times), until something is taken.
        public static int CountReady(Area area, string name, int quality = -1, bool matchWorldLevel = true)
        {
            if (name == null) return 0;
            if (area.ReadyFrame != UnityEngine.Time.frameCount)
            {
                area.ReadyFrame = UnityEngine.Time.frameCount;
                area.ReadyMemo.Clear();
            }
            var key = name + "|" + quality + (matchWorldLevel ? "|w" : "|a");
            if (area.ReadyMemo.TryGetValue(key, out var memo)) return memo;
            var total = 0;
            foreach (var chest in area.List)
            {
                if (!Ready(chest)) continue;
                Fresh(chest);
                total += Usable(chest.GetInventory(), name, quality, matchWorldLevel);
            }
            area.ReadyMemo[key] = total;
            return total;
        }

        // Takes up to amount from the ready chests, nearest first; returns how many it took.
        public static int Take(Area area, string name, int amount, int quality = -1, bool matchWorldLevel = true)
        {
            var taken = 0;
            foreach (var chest in area.List)
            {
                if (amount <= 0) break;
                if (!Ready(chest)) continue;
                Fresh(chest);
                var inventory = chest.GetInventory();
                var have = Usable(inventory, name, quality, matchWorldLevel);
                if (have <= 0) continue;
                var n = Mathf.Min(have, amount);
                inventory.RemoveItem(name, n, quality, matchWorldLevel);
                amount -= n;
                taken += n;
            }
            if (taken > 0)
            {
                area.Dirty = true;
                area.ReadyMemo.Clear();
            }
            return taken;
        }

        // Asks the owners of the chests that hold what is still missing (nearest first, until it is covered). Returns true
        // while an answer is still on its way.
        public static bool Ask(Area area, string name, int missing, int quality = -1, bool matchWorldLevel = true)
        {
            missing -= CountReady(area, name, quality, matchWorldLevel); // ours first: no need to ask for what they cover
            foreach (var chest in area.List)
            {
                if (missing <= 0) break;
                if (chest == null || Ready(chest) || OpenByOthers(chest)) continue;
                var have = Usable(chest.GetInventory(), name, quality, matchWorldLevel);
                if (have <= 0) continue;
                Request(chest);
                missing -= have;
            }
            return Waiting();
        }

        // ------------------------------------------------------------------ multiplayer: getting a chest from its owner

        private static readonly Dictionary<Container, float> Pending = new Dictionary<Container, float>();
        private static readonly Dictionary<Container, float> Refused = new Dictionary<Container, float>();
        private const float AnswerTime = 2f;

        public static bool Ready(Container chest) =>
            chest != null && chest.m_nview != null && chest.m_nview.IsValid() && chest.m_nview.IsOwner() &&
            (!chest.m_inUse || InventoryGui.instance == null || InventoryGui.instance.m_currentContainer == chest);

        public static void Request(Container chest)
        {
            if (chest == null || Ready(chest) || chest.m_nview == null || !chest.m_nview.IsValid() || Game.instance == null) return;
            var now = UnityEngine.Time.unscaledTime;
            if (Pending.TryGetValue(chest, out var asked) && now - asked < AnswerTime) return;
            if (Refused.TryGetValue(chest, out var refused) && now - refused < 3f) return;
            Pending[chest] = now;
            chest.m_nview.InvokeRPC("RPC_RequestOpen", Game.instance.GetPlayerProfile().GetPlayerID());
        }

        // An answer is on its way (asked less than two seconds ago, and the chest is not ours yet).
        public static bool Waiting()
        {
            if (Pending.Count == 0) return false;
            var now = UnityEngine.Time.unscaledTime;
            var waiting = false;
            List<Container> done = null;
            foreach (var pair in Pending)
            {
                if (pair.Key != null && now - pair.Value < AnswerTime && !Ready(pair.Key)) waiting = true;
                else (done ?? (done = new List<Container>())).Add(pair.Key);
            }
            if (done != null)
                foreach (var chest in done) Pending.Remove(chest);
            return waiting;
        }

        // Our own request's answer: the chest is ours now (or not), but its window is not opened.
        private static bool OpenResponse(Container __instance, bool granted)
        {
            if (!Pending.ContainsKey(__instance)) return true;
            if (granted) Pending[__instance] = UnityEngine.Time.unscaledTime; // ours as soon as its data arrives
            else
            {
                Pending.Remove(__instance);
                Refused[__instance] = UnityEngine.Time.unscaledTime;
            }
            return false;
        }
    }
}
