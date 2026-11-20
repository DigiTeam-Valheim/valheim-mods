// Set Scanner: a window (key J, also from the inventory) with Epic Loot's sets and how many pieces of each you have:
// worn, in your inventory, in the backpack you wear (Adventure Backpacks) and in the player-built chests, carts and ships
// near you that you may open. Per set every piece: found ones in their rarity's colour with where they are (the best
// copy, "+2 more" when you have copies), missing ones dimmed with the kind of item they come on. Every set has its own
// switch under "Sets" (all on): every set that is on is listed, the ones you have pieces of first (most pieces first),
// then the rest (0 / 4), so you see what each set is made of. Under each set its bonuses: the value for the pieces you
// have, gold while you wear enough. A search box finds a set by its name or a piece's name; a star (as in Portal List)
// marks a favourite set, listed first under its own heading (kept with your character, for every world). While the
// search box has the keyboard no key reaches the game or other mods.
// A piece in a chest or the backpack has a Take button that moves it to your inventory (only from a chest this PC owns
// on the network and nobody has open, so the change saves like a normal one); nothing else is ever moved.
// A second tab, Enchantments: a search box over Epic Loot's enchantments (those your items nearby have are listed
// with how many items have them; a search also finds the others); click one to see every item nearby with it, its value
// and where it is, with the same Take button (to etch it out at the enchanting table, for example). J reopens the tab
// used last. The window is DigiShared's (the Cookbook look), compiled into this mod.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using DigiShared;
using EpicLoot;
using EpicLoot.LegendarySystem;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SetScanner
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.setscanner";
        public const string Name = "Digi's Set Scanner";
        public const string Version = "1.1.0";
        private const float Width = 960f, Height = 840f, HeaderHeight = 46f, PieceHeight = 38f, BonusHeight = 32f, Gap = 14f,
            ButtonWidth = 84f;

        // Read by Configuration Manager by name: the set's own name in F1 (the setting's key stays the set's ID).
        private sealed class ConfigurationManagerAttributes
        {
            public string DispName;
            public int? Order;
        }

        private sealed class Copy
        {
            public ItemDrop.ItemData Item;
            public ItemRarity Rarity;
            public int Rank; // 0 worn, 1 inventory, 2 backpack, 3 chest
            public float Distance;
            public string Chest;
            public Inventory Source;    // where it is (backpack or chest), for Take
            public Container Container; // the chest, if in one
        }

        private sealed class SetPiece
        {
            public string ID, Name, Hint;
            public readonly List<Copy> Copies = new List<Copy>();

            public Copy Best => Copies.OrderByDescending(c => c.Rarity).ThenBy(c => c.Rank).ThenBy(c => c.Distance).FirstOrDefault();
        }

        private sealed class FoundSet
        {
            public LegendarySetInfo Set;
            public string Name;
            public int Full;
            public readonly List<SetPiece> Pieces = new List<SetPiece>();

            public int Owned => Pieces.Count(p => p.Copies.Count > 0);
            public int Worn => Pieces.Count(p => p.Copies.Any(c => c.Rank == 0));
        }

        // An item with a given enchantment, and its value there.
        private sealed class Hit
        {
            public Copy Copy;
            public float Value;
        }

        private sealed class EnchantRow
        {
            public string Type, Name;
            public int Count, Stars;
        }

        // Epic Loot's rarity stars of an enchantment (MagicEffectRarity: 1-3, 0 for a common one), drawn the way its tooltips
        // do: its own mark (★ when its fonts have it) in its colour per tier.
        private static readonly string[] StarColors = { "", "#8FD8FF", "#C79BFF", "#FFD24A" };
        private static char _starMark;

        private static string StarText(int stars)
        {
            if (stars <= 0) return "";
            if (_starMark == default(char))
            {
                _starMark = '★';
                try
                {
                    AccessTools.Method(typeof(MagicEffectRarity), "ResolveGlyph")?.Invoke(null, null);
                    if (AccessTools.Field(typeof(MagicEffectRarity), "_tierGlyph")?.GetValue(null) is char c) _starMark = c;
                }
                catch { }
            }
            return $"<color={StarColors[Math.Min(stars, 3)]}>{new string(_starMark, Math.Min(stars, 3))}</color>";
        }

        private enum Tab { Sets, Enchantments }

        private static Tab _tab = Tab.Sets;
        private static string _search = "", _setSearch = "", _selected;

        // Your favourite sets (a star in the set's header): listed first. Kept with your character, for every world.
        private const string FavouritesKey = "digi.setscanner.favourites";
        private static readonly HashSet<string> Favourites = new HashSet<string>();
        private static long _favouritesOf;
        private static List<Copy> _all = new List<Copy>();
        private static int _chests;
        private const float ListTop = 158f;

        private static ConfigEntry<KeyCode> _key;
        private static ConfigEntry<float> _range;
        private static readonly Dictionary<string, ConfigEntry<bool>> Switches = new Dictionary<string, ConfigEntry<bool>>();
        private static readonly HashSet<Container> Containers = new HashSet<Container>();
        private static Dictionary<string, string> _english;
        private static Plugin _instance;
        private static BepInEx.Logging.ManualLogSource _log;
        private static MethodInfo _checkAccess, _backpackInventory;
        private static Windows.Window _window;
        private static bool _openWhenInventoryHides;
        private static float _openDeadline;

        private void Awake()
        {
            _instance = this;
            _log = Logger;
            _key = Config.Bind("1 - General", "Key", KeyCode.J, "Key that opens and closes the set scanner (also with the inventory open).");
            _range = Config.Bind("1 - General", "Chest range (m)", 50f, new ConfigDescription(
                "Player-built chests, carts and ships up to this far away are scanned (those you may open). 0: only your inventory and backpack.",
                new AcceptableValueRange<float>(0f, 100f)));
            _checkAccess = AccessTools.Method(typeof(Container), "CheckAccess", new[] { typeof(long) });
            var backpacks = AccessTools.TypeByName("AdventureBackpacks.API.ABAPI");
            _backpackInventory = backpacks == null ? null : AccessTools.Method(backpacks, "GetEquippedBackpackInventory", new[] { typeof(Player) });
            BindSets();
            Containers.Clear();
            foreach (var chest in FindObjectsByType<Container>(FindObjectsSortMode.None)) Containers.Add(chest); // those loaded before a live switch-on
            PatchModule(typeof(Plugin));
            Windows.Patch(ModuleHarmony);
            Windows.Use(gameObject);
        }

        private void OnDestroy()
        {
            if (IsOpen) Windows.Close();
            Windows.Release(gameObject);
            _window = null;
            _openWhenInventoryHides = false;
            Switches.Clear();
            Containers.Clear();
            if (_instance == this) _instance = null;
        }

        [HarmonyPatch(typeof(Container), "Awake")]
        [HarmonyPostfix]
        private static void ChestLoaded(Container __instance) => Containers.Add(__instance);

        private static bool IsOpen => Windows.IsOpen && Windows.Current == _window;

        private void Update()
        {
            if (_openWhenInventoryHides)
            {
                if (!InventoryGui.IsVisible())
                {
                    _openWhenInventoryHides = false;
                    Open();
                }
                else if (Time.unscaledTime > _openDeadline) _openWhenInventoryHides = false;
                return;
            }
            var player = Player.m_localPlayer;
            if (player == null || !UnityInput.Current.GetKeyDown(_key.Value) || Typing()) return;
            if (IsOpen)
            {
                Windows.Close();
                return;
            }
            if (player.IsDead() || Menu.IsVisible() || Minimap.IsOpen() || StoreGui.IsVisible() || Hud.IsPieceSelectionVisible()) return;
            if (InventoryGui.IsVisible())
            {
                // The window closes itself while the inventory shows: open it once the inventory has gone.
                InventoryGui.instance.Hide();
                _openWhenInventoryHides = true;
                _openDeadline = Time.unscaledTime + 1f;
                return;
            }
            Open();
        }

        private static bool Typing()
        {
            if ((Chat.instance != null && Chat.instance.HasFocus()) || Console.IsVisible() || TextInput.IsVisible() || Minimap.InTextInput()) return true;
            // Any other mod's text box (a search field) that has the keyboard.
            var events = UnityEngine.EventSystems.EventSystem.current;
            var selected = events != null ? events.currentSelectedGameObject : null;
            if (selected == null) return false;
            var tmp = selected.GetComponent<TMP_InputField>();
            if (tmp != null && tmp.isFocused) return true;
            var field = selected.GetComponent<InputField>();
            return field != null && field.isFocused;
        }

        // --- Settings: a switch per set -----------------------------------------------------------------------------

        // Epic Loot has loaded its sets by the time this sub-mod starts; a server can send other ones later, so every scan
        // binds the sets that are new. F1 shows each switch under the set's English name, sorted by it.
        private static void BindSets()
        {
            if (_instance == null) return;
            var fresh = UniqueLegendaryHelper.AllSets.Values
                .Where(s => s != null && !string.IsNullOrEmpty(s.ID) && !Switches.ContainsKey(s.ID))
                .OrderBy(EnglishName, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var set in fresh)
            {
                var name = EnglishName(set);
                Switches[set.ID] = _instance.Config.Bind("2 - Sets", Spaced(set.ID), true, new ConfigDescription(
                    $"List {name} in the set scanner.", null,
                    new ConfigurationManagerAttributes { DispName = name, Order = 1000 - Switches.Count }));
            }
        }

        private static bool Shown(string setID) => !Switches.TryGetValue(setID, out var entry) || entry.Value;

        private static string EnglishName(LegendarySetInfo set) => English(set.Name) ?? Spaced(set.ID);

        private static string Spaced(string id) => Regex.Replace(id, "(?<=[a-z])(?=[A-Z])", " ");

        // Epic Loot's English texts, from its DLL: the game's own language may not be loaded yet when the settings are made.
        private static string English(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            if (_english == null)
            {
                _english = new Dictionary<string, string>();
                try
                {
                    using (var stream = typeof(UniqueLegendaryHelper).Assembly.GetManifestResourceStream("EpicLoot.localizations.English.json"))
                    using (var reader = new System.IO.StreamReader(stream))
                        foreach (Match m in Regex.Matches(reader.ReadToEnd(), "\"(mod_epicloot_[A-Za-z0-9_]+)\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\""))
                            _english[m.Groups[1].Value] = Regex.Unescape(m.Groups[2].Value);
                }
                catch (Exception e)
                {
                    _log?.LogWarning("Epic Loot's English set names were not found, the switches use the set IDs: " + e.Message);
                }
            }
            return _english.TryGetValue(token.TrimStart('$'), out var text) ? text : null;
        }

        private static string Localize(string token, string fallback)
        {
            if (string.IsNullOrEmpty(token)) return fallback;
            var text = Localization.instance != null ? Localization.instance.Localize(token) : token;
            if (string.IsNullOrEmpty(text) || text.StartsWith("[") || text.StartsWith("$")) return English(token) ?? fallback;
            return text;
        }

        // --- The scan ----------------------------------------------------------------------------------------------

        // Everything in your inventory, the backpack you wear and the chests in range, with where it is.
        private static List<Copy> ScanAll(Player player, out int chests)
        {
            var all = new List<Copy>();
            foreach (var item in player.GetInventory().GetAllItems())
                all.Add(NewCopy(item, item.m_equipped ? 0 : 1, 0f, null, null, null));
            var backpack = BackpackInventory(player);
            if (backpack != null)
                foreach (var item in backpack.GetAllItems().ToList())
                    all.Add(NewCopy(item, 2, 0f, null, backpack, null));

            chests = 0;
            var range = _range.Value;
            if (range > 0f)
            {
                var origin = player.transform.position;
                Containers.RemoveWhere(c => c == null);
                foreach (var chest in Containers)
                {
                    var distance = Vector3.Distance(chest.transform.position, origin);
                    if (distance > range || !Usable(chest, player)) continue;
                    chests++;
                    var name = Localize(chest.m_name, "Chest");
                    var inventory = chest.GetInventory();
                    foreach (var item in inventory.GetAllItems())
                        all.Add(NewCopy(item, 3, distance, name, inventory, chest));
                }
            }
            all.RemoveAll(c => c == null);
            return all;
        }

        private static Copy NewCopy(ItemDrop.ItemData item, int rank, float distance, string chest, Inventory source, Container container)
        {
            if (item?.m_shared == null) return null;
            return new Copy
            {
                Item = item, Rarity = item.IsMagic(out var magic) ? magic.Rarity : ItemRarity.Magic, Rank = rank, Distance = distance,
                Chest = chest, Source = source, Container = container,
            };
        }

        private static List<FoundSet> Sets(List<Copy> all)
        {
            BindSets();
            var sets = new Dictionary<string, FoundSet>();
            foreach (var set in UniqueLegendaryHelper.AllSets.Values)
            {
                if (set == null || string.IsNullOrEmpty(set.ID) || !Shown(set.ID)) continue;
                var found = new FoundSet { Set = set, Name = Localize(set.Name, Spaced(set.ID)), Full = UniqueLegendaryHelper.GetFullSetCount(set) };
                foreach (var id in (set.LegendaryIDs ?? new List<string>()).Where(x => !string.IsNullOrEmpty(x)).Distinct())
                {
                    UniqueLegendaryHelper.TryGetLegendaryInfo(id, out var info);
                    found.Pieces.Add(new SetPiece { ID = id, Name = info != null ? Localize(info.Name, Spaced(id)) : Spaced(id), Hint = Hint(info) });
                }
                sets[set.ID] = found;
            }
            foreach (var copy in all) Add(sets, copy);
            return sets.Values
                .OrderByDescending(s => s.Owned).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static void Add(Dictionary<string, FoundSet> sets, Copy copy)
        {
            var item = copy.Item;
            if (!item.IsMagic(out var magic) || magic.IsUnidentified || string.IsNullOrEmpty(magic.SetID)) return;
            if (!sets.TryGetValue(magic.SetID, out var set)) return; // not a set, or switched off
            var piece = string.IsNullOrEmpty(magic.LegendaryID) ? null : set.Pieces.FirstOrDefault(p => p.ID == magic.LegendaryID);
            if (piece == null)
            {
                // A piece the set's list does not name (an older item, or one without a piece ID): a row of its own.
                piece = new SetPiece { ID = magic.LegendaryID ?? "", Name = Localize(item.m_shared.m_name, "Set piece"), Hint = "" };
                set.Pieces.Add(piece);
            }
            piece.Copies.Add(copy);
        }

        // Every enchantment on the identified magic items found, with the items that have it.
        private static Dictionary<string, List<Hit>> Enchantments(List<Copy> all)
        {
            var result = new Dictionary<string, List<Hit>>();
            foreach (var copy in all)
            {
                if (!copy.Item.IsMagic(out var magic) || magic.IsUnidentified || magic.Effects == null) continue;
                foreach (var effect in magic.Effects)
                {
                    if (effect == null || string.IsNullOrEmpty(effect.EffectType)) continue;
                    if (!result.TryGetValue(effect.EffectType, out var list)) result[effect.EffectType] = list = new List<Hit>();
                    list.Add(new Hit { Copy = copy, Value = effect.EffectValue });
                }
            }
            return result;
        }

        private static string EffectName(string type)
        {
            try
            {
                if (MagicItemEffectDefinitions.TryGet(type, out var definition))
                {
                    var name = definition.GetName();
                    if (!string.IsNullOrEmpty(name)) return name;
                }
            }
            catch
            {
                // the type's own name below
            }
            return Spaced(type);
        }

        private static Inventory BackpackInventory(Player player)
        {
            if (_backpackInventory == null) return null;
            try
            {
                return _backpackInventory.Invoke(null, new object[] { player }) as Inventory;
            }
            catch
            {
                return null;
            }
        }

        // Player-built chests (carts and ships too) this player may open; no tombstones, dungeon chests or backpacks.
        private static bool Usable(Container chest, Player player)
        {
            if (!chest.isActiveAndEnabled || chest.GetInventory() == null) return false;
            var piece = chest.GetComponentInParent<Piece>();
            if (piece == null || !piece.IsPlacedByPlayer()) return false;
            return _checkAccess == null || (bool)_checkAccess.Invoke(chest, new object[] { player.GetPlayerID() });
        }

        // What kind of item a missing piece comes on, from Epic Loot's requirements: "helmet", "tower shield", "axe, two-handed".
        private static string Hint(LegendaryInfo info)
        {
            var req = info?.Requirements;
            if (req == null) return "";
            var types = req.AllowedItemTypes ?? new List<string>();
            var skills = req.AllowedSkillTypes ?? new List<Skills.SkillType>();
            if (skills.Count > 0)
                return string.Join(" or ", skills.Select(SkillWord).Distinct()) + (types.Any(t => t.StartsWith("TwoHanded")) ? ", two-handed" : "");
            return string.Join(" or ", types.Select(t => TypeWord(t, req)).Distinct());
        }

        private static string TypeWord(string type, MagicItemEffectRequirements req)
        {
            switch (type)
            {
                case "Helmet": return "helmet";
                case "Chest": return "chest armour";
                case "Legs": return "leg armour";
                case "Shoulder": return "cape";
                case "Shield": return req.ItemHasNoParryPower == true ? "tower shield" : "shield";
                case "Bow": return "bow";
                case "TwoHandedWeapon":
                case "TwoHandedWeaponLeft": return "two-handed weapon";
                case "OneHandedWeapon": return "one-handed weapon";
                case "Torch": return "torch";
                case "Utility": return "utility item";
                case "Trinket": return "trinket";
                default: return "weapon";
            }
        }

        private static string SkillWord(Skills.SkillType skill)
        {
            switch (skill)
            {
                case Skills.SkillType.Swords: return "sword";
                case Skills.SkillType.Knives: return "knife";
                case Skills.SkillType.Clubs: return "club";
                case Skills.SkillType.Polearms: return "polearm";
                case Skills.SkillType.Spears: return "spear";
                case Skills.SkillType.Axes: return "axe";
                case Skills.SkillType.Bows: return "bow";
                case Skills.SkillType.Crossbows: return "crossbow";
                case Skills.SkillType.ElementalMagic: return "elemental staff";
                case Skills.SkillType.BloodMagic: return "blood magic staff";
                case Skills.SkillType.Unarmed: return "fists";
                case Skills.SkillType.Pickaxes: return "pickaxe";
                case Skills.SkillType.Blocking: return "shield";
                default: return skill.ToString().ToLowerInvariant();
            }
        }

        // --- The window --------------------------------------------------------------------------------------------

        private static string S(int n) => n == 1 ? "" : "s";

        private static string Scanned() => _range.Value > 0f
            ? $"Inventory, backpack and {_chests} chest{S(_chests)} within {_range.Value:0} m"
            : "Inventory and backpack";

        private static void Open()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;
            try
            {
                _all = ScanAll(player, out _chests);
            }
            catch (Exception e)
            {
                _log?.LogError("Scanning your items failed: " + e);
                return;
            }
            OpenWindow();
        }

        // The window for the current tab: Sets / Enchantments buttons and, for enchantments, the search box above the list.
        private static void OpenWindow()
        {
            string title, subtitle, footer;
            if (_tab == Tab.Sets)
            {
                var sets = Sets(_all);
                var pieces = sets.Sum(s => s.Owned);
                var withPieces = sets.Count(s => s.Owned > 0);
                title = "Set Scanner";
                subtitle = $"{Scanned()}: {pieces} set piece{S(pieces)} of {withPieces} set{S(withPieces)}";
                footer = $"{_key.Value} or Esc closes. The star keeps a set on top; each set can be switched off in F1.";
            }
            else
            {
                var found = Enchantments(_all);
                var items = found.Values.SelectMany(list => list.Select(h => h.Copy.Item)).Distinct().Count();
                title = "Enchant Scanner";
                subtitle = $"{Scanned()}: {found.Count} enchantment{S(found.Count)} on {items} magic item{S(items)}";
                footer = $"{_key.Value} or Esc closes. Search an enchantment, click it to see the items that have it.";
            }
            var w = Windows.Open(title, subtitle, Width, Height, footer, PieceHeight * 2f, null, ListTop);
            if (w == null) return;
            _window = w;
            var setsTab = Windows.Button(w.Panel, "Sets", Windows.Pad, 104f, 170f, 40f, () => SwitchTab(Tab.Sets));
            var enchantsTab = Windows.Button(w.Panel, "Enchantments", Windows.Pad + 180f, 104f, 220f, 40f, () => SwitchTab(Tab.Enchantments));
            setsTab.interactable = _tab != Tab.Sets; // the game's own tabs: the open one cannot be clicked
            enchantsTab.interactable = _tab != Tab.Enchantments;
            // A search box on each tab (each keeps its own text). While it has the keyboard no key reaches the game or
            // other mods (DigiShared's windows), and J does not close the window.
            var onSets = _tab == Tab.Sets;
            var box = Windows.SearchBox(w.Panel, Width - Windows.Pad - 380f, 105f, 380f, 38f, onSets ? "Search a set or piece..." : "Search an enchantment...");
            box.SetTextWithoutNotify(onSets ? _setSearch : _search);
            box.onValueChanged.AddListener(text =>
            {
                if (onSets) _setSearch = text ?? "";
                else
                {
                    _search = text ?? "";
                    _selected = null;
                }
                FillList();
            });
            w.Search = box;
            FillList();
        }

        private static void SwitchTab(Tab tab)
        {
            if (_tab == tab) return;
            _tab = tab;
            _selected = null;
            OpenWindow();
        }

        // The list part again (typing in the search box, picking an enchantment, a star), the rest of the window stays.
        // A star keeps the list scrolled where it was.
        private static void FillList(bool keepScroll = false)
        {
            var w = _window;
            if (w == null || w.Content == null) return;
            var scroll = w.Content.anchoredPosition.y;
            for (var i = w.Content.childCount - 1; i >= 0; i--) Destroy(w.Content.GetChild(i).gameObject);
            var height = _tab == Tab.Sets ? DrawSets(w) : DrawEnchantments(w);
            Windows.Done(w, height);
            if (keepScroll && w.View != null)
                w.Content.anchoredPosition = new Vector2(0f, Mathf.Clamp(scroll, 0f, Mathf.Max(0f, height - w.View.rect.height)));
        }

        // Favourites first (under their own heading), then the rest; a search keeps the sets whose name or a piece's name
        // has it.
        private static float DrawSets(Windows.Window w)
        {
            var sets = Sets(_all);
            var y = 0f;
            if (sets.Count == 0)
            {
                var none = Windows.Text(w.Content, "Every set is switched off in F1 (Set Scanner - 2 - Sets).",
                    20f, Windows.Dim, TextAlignmentOptions.Left);
                Windows.Place(none.rectTransform, 14f, 10f, w.RowWidth - 28f, 34f);
                return 54f;
            }
            LoadFavourites();
            var search = (_setSearch ?? "").Trim();
            if (search.Length > 0)
                sets = sets.Where(s => Has(s.Name, search) || s.Pieces.Any(p => Has(p.Name, search))).ToList();
            if (sets.Count == 0)
            {
                var none = Windows.Text(w.Content, $"No set or piece matches \"{search}\".", 20f, Windows.Dim, TextAlignmentOptions.Left);
                Windows.Place(none.rectTransform, 14f, 10f, w.RowWidth - 28f, 34f);
                return 54f;
            }
            var favourites = sets.Where(s => Favourites.Contains(s.Set.ID)).ToList();
            var rest = sets.Where(s => !Favourites.Contains(s.Set.ID)).ToList();
            if (favourites.Count > 0)
            {
                y = Heading(w, "Favourites", y);
                foreach (var set in favourites) y = DrawSet(w, set, y) + Gap;
                if (rest.Count > 0) y = Heading(w, "All sets", y);
            }
            foreach (var set in rest)
                y = DrawSet(w, set, y) + Gap;
            return y;
        }

        private static bool Has(string text, string search) =>
            !string.IsNullOrEmpty(text) && text.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0;

        private static float Heading(Windows.Window w, string text, float y)
        {
            var t = Windows.Text(w.Content, text, 21f, Windows.Gold, TextAlignmentOptions.Left);
            Windows.Place(t.rectTransform, 14f, y + 2f, w.RowWidth - 28f, 34f);
            return y + 40f;
        }

        // --- Favourites (kept with your character) ------------------------------------------------------------------

        private static void LoadFavourites()
        {
            var player = Player.m_localPlayer;
            if (player == null || player.GetPlayerID() == _favouritesOf) return;
            _favouritesOf = player.GetPlayerID();
            Favourites.Clear();
            if (player.m_customData.TryGetValue(FavouritesKey, out var text) && !string.IsNullOrEmpty(text))
                foreach (var id in text.Split(';'))
                    if (id.Length > 0) Favourites.Add(id);
        }

        private static void ToggleFavourite(string setID)
        {
            var player = Player.m_localPlayer;
            if (player == null || string.IsNullOrEmpty(setID)) return;
            LoadFavourites();
            if (!Favourites.Remove(setID)) Favourites.Add(setID);
            player.m_customData[FavouritesKey] = string.Join(";", Favourites.OrderBy(id => id, StringComparer.Ordinal));
            FillList(true);
        }

        // Without a pick: the enchantments (with a search, every one whose name matches; without, those your items have).
        // With one picked: the items that have it, highest value first.
        private static float DrawEnchantments(Windows.Window w)
        {
            var found = Enchantments(_all);
            var width = w.RowWidth;
            var y = 0f;
            if (_selected != null)
            {
                found.TryGetValue(_selected, out var hits);
                hits = hits ?? new List<Hit>();
                MagicItemEffectDefinitions.TryGet(_selected, out var definition);
                var header = Windows.Rect("Enchantment", w.Content);
                Windows.Place(header, 0f, y, width, HeaderHeight);
                header.gameObject.AddComponent<Image>().color = new Color(1f, 0.72f, 0.36f, 0.12f);
                Windows.Place(Windows.Text(header, EffectName(_selected), 25f, Windows.Gold, TextAlignmentOptions.Left).rectTransform,
                    14f, 0f, width - 330f, HeaderHeight);
                Windows.Place(Windows.Text(header, $"{hits.Count} item{S(hits.Count)}", 20f, Windows.Body, TextAlignmentOptions.Right).rectTransform,
                    width - 320f, 0f, 170f, HeaderHeight);
                Windows.Button(header, "Back", width - 12f - 130f, 4f, 130f, HeaderHeight - 8f, () =>
                {
                    _selected = null;
                    FillList();
                });
                y += HeaderHeight;
                if (hits.Count == 0)
                {
                    var none = Windows.Text(w.Content, "None of your items nearby has it.", 20f, Windows.Dim, TextAlignmentOptions.Left);
                    Windows.Place(none.rectTransform, 14f, y + 8f, width - 28f, 34f);
                    return y + 50f;
                }
                var index = 0;
                foreach (var hit in hits.OrderByDescending(h => h.Value).ThenBy(h => h.Copy.Rank).ThenBy(h => h.Copy.Distance))
                {
                    DrawHit(w, definition, hit, y, index++);
                    y += PieceHeight;
                }
                return y;
            }

            var search = (_search ?? "").Trim();
            var rows = new List<EnchantRow>();
            foreach (var definition in MagicItemEffectDefinitions.AllDefinitions.Values)
            {
                if (definition == null || string.IsNullOrEmpty(definition.Type)) continue;
                var name = EffectName(definition.Type);
                var count = found.TryGetValue(definition.Type, out var hits) ? hits.Count : 0;
                var shown = search.Length == 0
                    ? count > 0
                    : name.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                      definition.Type.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
                if (shown) rows.Add(new EnchantRow { Type = definition.Type, Name = name, Count = count, Stars = MagicEffectRarity.GetTier(definition) });
            }
            if (rows.Count == 0)
            {
                var text = search.Length == 0 ? "None of your items nearby has an enchantment." : $"No enchantment matches \"{search}\".";
                var none = Windows.Text(w.Content, text, 20f, Windows.Dim, TextAlignmentOptions.Left);
                Windows.Place(none.rectTransform, 14f, 10f, width - 28f, 34f);
                return 54f;
            }
            var i = 0;
            // The rarest first (3 stars, 2, 1, then the rest), each group A-Z.
            foreach (var row in rows.OrderByDescending(r => r.Stars).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                DrawEnchantRow(w, row, y, i++);
                y += PieceHeight;
            }
            return y;
        }

        // One enchantment: its name and how many of your items nearby have it; a click shows them.
        private static void DrawEnchantRow(Windows.Window w, EnchantRow row, float y, int index)
        {
            var width = w.RowWidth;
            var rect = Windows.Rect("Enchant", w.Content);
            Windows.Place(rect, 0f, y, width, PieceHeight);
            var image = rect.gameObject.AddComponent<Image>();
            image.color = index % 2 == 0 ? new Color(1f, 1f, 1f, 0.05f) : new Color(0f, 0f, 0f, 0.16f);
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            var colors = button.colors;
            colors.normalColor = new Color(0.5f, 0.5f, 0.5f, 0.5f);
            colors.highlightedColor = new Color(1f, 0.9f, 0.7f, 1f);
            colors.pressedColor = new Color(0.8f, 0.8f, 0.8f, 0.8f);
            colors.selectedColor = colors.normalColor;
            colors.colorMultiplier = 2f; // hovered rows light up
            button.colors = colors;
            var type = row.Type;
            button.onClick.AddListener(() =>
            {
                _selected = type;
                FillList();
            });
            var color = row.Count > 0 ? Windows.Body : Windows.Dim;
            Windows.Place(Windows.Text(rect, row.Name, 20f, color, TextAlignmentOptions.Left).rectTransform, 30f, 0f, width * 0.6f, PieceHeight);
            if (row.Stars > 0)
                Windows.Place(Windows.Text(rect, StarText(row.Stars), 20f, Windows.Body, TextAlignmentOptions.Right).rectTransform,
                    width * 0.6f, 0f, width * 0.4f - 16f - 150f, PieceHeight);
            var note = row.Count > 0 ? $"{row.Count} item{S(row.Count)}  >" : "none nearby";
            Windows.Place(Windows.Text(rect, note, 18f, row.Count > 0 ? Windows.Gold : Windows.Dim, TextAlignmentOptions.Right).rectTransform,
                width * 0.6f, 0f, width * 0.4f - 16f, PieceHeight);
        }

        // One item with the picked enchantment: its name in its rarity's colour, the enchantment's value there, where it is.
        private static void DrawHit(Windows.Window w, MagicItemEffectDefinition definition, Hit hit, float y, int index)
        {
            var width = w.RowWidth;
            var copy = hit.Copy;
            var row = Windows.Rect("Item", w.Content);
            Windows.Place(row, 0f, y, width, PieceHeight);
            row.gameObject.AddComponent<Image>().color = index % 2 == 0 ? Windows.RowEven : Windows.RowOdd;
            Windows.Picture(row, copy.Item.GetIcon(), 30f, 3f, 32f, 32f);
            var nameWidth = width * 0.36f;
            var name = Localize(copy.Item.m_shared.m_name, "Item");
            try
            {
                if (copy.Item.IsMagic(out var magic) && !string.IsNullOrEmpty(magic.DisplayName)) name = Localize(magic.DisplayName, name);
            }
            catch
            {
                // the plain name
            }
            Windows.Place(Windows.Text(row, name, 19f, global::EpicLoot.EpicLoot.GetRarityColorARGB(copy.Rarity), TextAlignmentOptions.Left).rectTransform,
                76f, 0f, nameWidth - 76f, PieceHeight);
            var value = definition != null ? Localize(MagicItem.GetEffectText(definition, hit.Value), "") : hit.Value.ToString("0.#");
            Windows.Place(Windows.Text(row, value, 18f, Windows.Gold, TextAlignmentOptions.Left).rectTransform,
                nameWidth + 8f, 0f, width * 0.30f, PieceHeight);
            var buttonLeft = width - 12f - ButtonWidth;
            var where = Localize(global::EpicLoot.EpicLoot.GetRarityDisplayName(copy.Rarity), copy.Rarity.ToString()) + ", " + Where(copy);
            Windows.Place(Windows.Text(row, where, 18f, Windows.Body, TextAlignmentOptions.Right).rectTransform,
                nameWidth + width * 0.30f + 16f, 0f, buttonLeft - 12f - (nameWidth + width * 0.30f + 16f), PieceHeight);
            if (copy.Source != null) Windows.Button(row, "Take", buttonLeft, 4f, ButtonWidth, PieceHeight - 8f, () => Take(copy, name));
        }

        private static float DrawSet(Windows.Window w, FoundSet set, float y)
        {
            var width = w.RowWidth;
            var header = Windows.Rect("Set", w.Content);
            Windows.Place(header, 0f, y, width, HeaderHeight);
            header.gameObject.AddComponent<Image>().color = new Color(1f, 0.72f, 0.36f, 0.12f);
            var complete = set.Full > 0 && set.Owned >= set.Full;
            var color = complete ? Windows.Gold : Windows.Body;
            // The favourite star, as in Portal List: click to mark or unmark.
            var favourite = Favourites.Contains(set.Set.ID);
            var star = Windows.Text(header, favourite ? "★" : "☆", 28f, favourite ? Windows.Gold : Windows.Dim, TextAlignmentOptions.Center);
            Windows.Place(star.rectTransform, 6f, 0f, 40f, HeaderHeight);
            star.raycastTarget = true;
            var starButton = star.gameObject.AddComponent<Button>();
            starButton.transition = Selectable.Transition.None;
            var id = set.Set.ID;
            starButton.onClick.AddListener(() => ToggleFavourite(id));
            Windows.Place(Windows.Text(header, set.Name, 25f, color, TextAlignmentOptions.Left).rectTransform, 50f, 0f, width - 366f, HeaderHeight);
            var note = complete ? "complete" : set.Worn > 0 ? $"{set.Worn} worn" : "";
            if (complete && set.Worn > 0) note += $", {set.Worn} worn";
            Windows.Place(Windows.Text(header, note, 18f, Windows.Dim, TextAlignmentOptions.Right).rectTransform, width - 310f, 0f, 180f, HeaderHeight);
            Windows.Place(Windows.Text(header, $"{set.Owned} / {set.Full}", 25f, color, TextAlignmentOptions.Right).rectTransform, width - 124f, 0f, 110f, HeaderHeight);
            y += HeaderHeight;
            var index = 0;
            foreach (var piece in set.Pieces)
            {
                DrawPiece(w, piece, y, index++);
                y += PieceHeight;
            }
            return DrawBonuses(w, set, y);
        }

        private static void DrawPiece(Windows.Window w, SetPiece piece, float y, int index)
        {
            var width = w.RowWidth;
            var row = Windows.Rect("Piece", w.Content);
            Windows.Place(row, 0f, y, width, PieceHeight);
            row.gameObject.AddComponent<Image>().color = index % 2 == 0 ? Windows.RowEven : Windows.RowOdd;
            var best = piece.Best;
            var icon = Windows.Picture(row, best != null ? best.Item.GetIcon() : null, 30f, 3f, 32f, 32f);
            if (best == null) icon.color = new Color(0f, 0f, 0f, 0.35f);
            var nameWidth = width * 0.42f;
            var nameColor = best != null ? global::EpicLoot.EpicLoot.GetRarityColorARGB(best.Rarity) : Windows.Dim;
            Windows.Place(Windows.Text(row, piece.Name, 20f, nameColor, TextAlignmentOptions.Left).rectTransform, 76f, 0f, nameWidth - 76f, PieceHeight);
            string text;
            if (best == null) text = string.IsNullOrEmpty(piece.Hint) ? "missing" : "missing: " + piece.Hint;
            else
            {
                text = Localize(global::EpicLoot.EpicLoot.GetRarityDisplayName(best.Rarity), best.Rarity.ToString()) + ", " + Where(best);
                if (piece.Copies.Count > 1) text += $"  <color=#9a948c>(+{piece.Copies.Count - 1} more)</color>";
            }
            var buttonLeft = width - 12f - ButtonWidth;
            Windows.Place(Windows.Text(row, text, 18f, best != null ? Windows.Body : Windows.Dim, TextAlignmentOptions.Right).rectTransform,
                nameWidth, 0f, buttonLeft - 12f - nameWidth, PieceHeight);
            // In a chest or the backpack: one click moves it into your inventory.
            if (best != null && best.Source != null)
                Windows.Button(row, "Take", buttonLeft, 4f, ButtonWidth, PieceHeight - 8f, () => Take(best, piece.Name));
        }

        // The set's bonuses, as Epic Loot words them, with the value for the pieces you have (their best rarities, like
        // Epic Loot counts worn ones); without enough pieces, the value per rarity. Gold: active now (you wear enough
        // pieces). White: you have enough pieces. Dim: not yet.
        private static float DrawBonuses(Windows.Window w, FoundSet set, float y)
        {
            var bonuses = set.Set?.SetBonuses?.Where(b => b?.Effect != null).OrderBy(b => b.Count).ToList();
            if (bonuses == null || bonuses.Count == 0) return y;
            var width = w.RowWidth;
            var owned = set.Pieces.Where(p => p.Copies.Count > 0).Select(p => p.Best.Rarity).OrderByDescending(r => r).ToList();
            var worn = set.Pieces.Where(p => p.Copies.Any(c => c.Rank == 0))
                .Select(p => p.Copies.Where(c => c.Rank == 0).Max(c => c.Rarity)).OrderByDescending(r => r).ToList();
            var rarities = UniqueLegendaryHelper.GetRarities(set.Set);
            const float labelLeft = 30f, labelWidth = 110f, tagWidth = 90f;
            var textLeft = labelLeft + labelWidth;
            var textWidth = width - textLeft - tagWidth - 24f;
            foreach (var bonus in bonuses)
            {
                if (!MagicItemEffectDefinitions.TryGet(bonus.Effect.Type, out var definition)) continue;
                var count = Math.Max(1, bonus.Count);
                var active = worn.Count >= count;
                var reachable = owned.Count >= count;
                var tiers = active ? new List<ItemRarity> { worn[count - 1] }
                    : reachable ? new List<ItemRarity> { owned[count - 1] } : rarities;
                string text;
                try
                {
                    text = SetDescriptions.DescribeSetBonus(definition, bonus, tiers);
                    if (Localization.instance != null) text = Localization.instance.Localize(text);
                }
                catch (Exception e)
                {
                    _log?.LogWarning($"Could not describe a set bonus of {set.Name}: {e.Message}");
                    continue;
                }
                var color = active ? Windows.Gold : reachable ? Windows.Body : Windows.Dim;
                var row = Windows.Rect("Bonus", w.Content);
                var body = Windows.Text(row, text, 18f, color, TextAlignmentOptions.TopLeft);
                body.textWrappingMode = TextWrappingModes.Normal;
                body.overflowMode = TextOverflowModes.Overflow;
                var height = Mathf.Max(BonusHeight, body.GetPreferredValues(text, textWidth, 0f).y + 12f);
                Windows.Place(row, 0f, y, width, height);
                row.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.22f);
                Windows.Place(body.rectTransform, textLeft, 6f, textWidth, height - 6f);
                var label = Windows.Text(row, $"{count} pieces", 18f, color, TextAlignmentOptions.TopLeft);
                Windows.Place(label.rectTransform, labelLeft, 6f, labelWidth, 26f);
                var tag = active ? "active" : reachable ? "found" : "";
                if (tag.Length > 0)
                    Windows.Place(Windows.Text(row, tag, 17f, color, TextAlignmentOptions.TopRight).rectTransform, width - 12f - tagWidth, 6f, tagWidth, 26f);
                y += height;
            }
            return y;
        }

        // Moves the piece into your inventory: from the backpack, or from a chest this PC owns that nobody has open.
        private static void Take(Copy copy, string name)
        {
            var player = Player.m_localPlayer;
            if (player == null || copy?.Source == null || copy.Item == null) return;
            if (copy.Rank == 3 && copy.Container == null)
            {
                Say(player, "That chest is gone.");
                Refresh();
                return;
            }
            if (!copy.Source.ContainsItem(copy.Item))
            {
                Say(player, $"{name} is not there any more.");
                Refresh();
                return;
            }
            if (copy.Container != null && (!copy.Container.IsOwner() || copy.Container.IsInUse()))
            {
                Say(player, $"Another player is using that {copy.Chest.ToLowerInvariant()}: take it from there yourself.");
                return;
            }
            var inventory = player.GetInventory();
            if (!inventory.CanAddItem(copy.Item))
            {
                Say(player, "Your inventory is full.");
                return;
            }
            inventory.MoveItemToThis(copy.Source, copy.Item);
            Say(player, copy.Source.ContainsItem(copy.Item) ? $"Could not take {name}." : $"{name} is in your inventory.");
            Refresh();
        }

        private static void Say(Player player, string text) => player.Message(MessageHud.MessageType.Center, text);

        // The window again after a change, at the same scroll position.
        private static void Refresh()
        {
            var scroll = IsOpen && _window.Content != null ? _window.Content.anchoredPosition.y : 0f;
            Open();
            if (!IsOpen || _window.Content == null || _window.View == null) return;
            Canvas.ForceUpdateCanvases();
            var range = Mathf.Max(0f, _window.Content.rect.height - _window.View.rect.height);
            _window.Content.anchoredPosition = new Vector2(_window.Content.anchoredPosition.x, Mathf.Clamp(scroll, 0f, range));
        }

        private static string Where(Copy copy)
        {
            switch (copy.Rank)
            {
                case 0: return "worn";
                case 1: return "inventory";
                case 2: return "backpack";
                default: return $"{copy.Chest}, {copy.Distance:0} m";
            }
        }
    }
}
