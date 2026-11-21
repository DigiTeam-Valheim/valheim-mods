// Craft From Chests: crafting, upgrading, building and filling fires and stations use what lies in the chests around you,
// as if it were in your bag (the bag pays first, then the nearest chests). Our own replacement for Local Craft From
// Storage Prime, which it stands in for: if that mod is installed too, it is switched off (DigiCore.OtherMods).
// - Counting: while the game checks whether you have the materials (recipe list, craft button, build menu, the piece's
//   panel, its red "missing" numbers), your bag's count of an item includes the chests (DigiStorage.Chests keeps their
//   totals; a count is a lookup). Adventure Backpacks still adds your backpack the same way.
// - Paying: the game's own payment takes the items from your bag; what your bag lacks comes out of the chests, nearest
//   first. In multiplayer only chests that are ours can pay (see DigiStorage.Chests): the check right before paying counts
//   only those, in the same frame, so nothing is ever made without being paid for.
// - Filling: fires, torches, braziers, cooking stations, smelters, kilns, furnaces, windmills, spinning wheels, eitr
//   refineries and fermenters take their fuel or input from the chests when your bag has none; Shift fills a
//   smelter-like station completely.
// - Chest window: a "Crafting" checkbox (where the title was); unticked, nothing takes from that chest (also not the auto
//   stations).
// - The crafting window and the build panel show what bag and chests hold together as a green "+24".
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using DigiStorage;
using UnityEngine;

namespace CraftFromChests
{
    public class Plugin : DigiCore.DigiModule
    {
        internal const string LcfspGuid = "LuminairPrime.valheim.localcraftfromstorageprime";
        private const string LcfspConfig = "LuminairPrime.valheim.localcraftfromstorageprime.cfg";

        internal static ConfigEntry<float> Range;
        internal static ConfigEntry<bool> Personal, CartsShips, Crafting, Building, Stations, ShiftFill, ShowCounts, ShowCheckbox;

        internal static readonly ChestRules Rules = new ChestRules { BlockFlag = DigiCore.ChestFlags.NoTake };
        internal static readonly Chests.Area Area = new Chests.Area(Rules);
        internal static bool Running;
        internal static BepInEx.Logging.ManualLogSource Log;

        private void Awake()
        {
            StoreRules.BindRules(Config); // the shared Chest rules (StoreRules)
            Log = Logger;
            const string chests = "1 - Chests", use = "2 - Use chests for", show = "3 - Show";
            Range = Config.Bind(chests, "Range (m)", 50f, new ConfigDescription(
                "How far from you a chest may be.", new AcceptableValueRange<float>(5f, 100f)));
            Personal = Config.Bind(chests, "Personal chests", false,
                "Also your own personal (private) chests. Other players' personal chests are never used.");
            CartsShips = Config.Bind(chests, "Carts and ships", false, "Also what lies in carts and in ships' storage.");
            Crafting = Config.Bind(use, "Crafting", true, "Crafting and upgrading at the stations.");
            Building = Config.Bind(use, "Building", true, "Placing pieces with the hammer, hoe and cultivator.");
            Stations = Config.Bind(use, "Fires and stations", true,
                "Fuel for fires, torches, braziers and cooking stations; ore, wood, flax, barley, sap and the like for smelters, " +
                "kilns, furnaces, windmills, spinning wheels and eitr refineries; mead base for fermenters; what ovens, cooking stations " +
                "and the frost foundry cook or forge (dough, meat, unforged gear). Only when your bag has none.");
            ShiftFill = Config.Bind(use, "Shift fills up", true,
                "Shift + E on anything that takes fuel (fires, hearths, ovens, the frost foundry, smelters, kilns, ...) or ore and " +
                "the like (smelters, kilns, windmills, spinning wheels, eitr refineries) fills it completely: from your bag first, " +
                "then the chests in range around you.");
            ShowCounts = Config.Bind(show, "Chest counts", true,
                "The crafting window and the build panel show what your bag and the chests hold together, as a green +number " +
                "above the amount needed.");
            ShowCheckbox = Config.Bind(show, "Take from it checkbox", true,
                "The \"Crafting\" checkbox in chest windows (where the chest's title was): untick it and nothing takes from that " +
                "chest (crafting, building, filling, the auto stations). Store Nearby still fills it.");

            var old = Path.Combine(Paths.ConfigPath, LcfspConfig);
            if (File.Exists(old) && FirstTime("from local craft from storage")) FromLocalCraft(old);
        }

        private void Start()
        {
            SwitchOffReplaced(LcfspGuid, "Local Craft From Storage Prime"); // installed too: it goes off, ours runs
            Chests.Use();
            PatchModule(typeof(Linking));
            PatchModule(typeof(Paying));
            PatchModule(typeof(Filling));
            PatchModule(typeof(ChestCounts));
            PatchModule(typeof(TakeCheckbox));
            ShowCheckbox.SettingChanged += TakeCheckbox.SettingChanged;
            Running = true;
        }

        private void OnDestroy()
        {
            if (!Running) return;
            Running = false;
            ShowCheckbox.SettingChanged -= TakeCheckbox.SettingChanged;
            Chests.Release();
            ChestCounts.Clear();
            TakeCheckbox.Clear();
            Linking.Reset();
            Paying.Reset();
        }

        // The chests around you now (the list is refreshed at most once a second, or when you move).
        internal static Chests.Area Here()
        {
            Rules.Range = Range.Value;
            Rules.Personal = Personal.Value;
            Rules.CartsShips = CartsShips.Value;
            var player = Player.m_localPlayer;
            if (player != null) Chests.Near(Area, player.transform.position);
            return Area;
        }

        // What the bag itself holds, as Inventory.CountItems counts it, but never with anything added by other mods
        // (Adventure Backpacks) or by us.
        internal static int Own(Inventory inventory, string name, int quality = -1, bool matchWorldLevel = true)
        {
            var count = 0;
            if (inventory == null) return 0;
            foreach (var item in inventory.GetAllItems())
                if (item.m_shared.m_name == name && (quality < 0 || item.m_quality == quality) && (!matchWorldLevel || item.m_worldLevel >= Game.m_worldLevel))
                    count += item.m_stack;
            return count;
        }

        // A message that takes the place of the game's own "missing" message shown in the same frame.
        private static string _say;
        private static int _sayFrame = -1;

        internal static void Say(string text)
        {
            _say = text;
            _sayFrame = Time.frameCount;
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, text);
        }

        internal static bool Replaces(ref string msg)
        {
            if (_sayFrame != Time.frameCount || _say == null || msg == null || msg == _say) return false;
            if (!msg.StartsWith("$msg_missingrequirement") && !msg.StartsWith("$msg_outof") && !msg.StartsWith("$msg_donthaveany") &&
                !msg.StartsWith("$msg_noprocessableitems")) return false;
            msg = _say;
            return true;
        }

        // ---- Local Craft From Storage Prime's settings, once ----

        private void FromLocalCraft(string path)
        {
            var values = new Dictionary<string, string>();
            var section = "";
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith("[") && line.EndsWith("]")) section = line.Substring(1, line.Length - 2);
                else if (!line.StartsWith("#") && line.Contains(" = "))
                {
                    var eq = line.IndexOf(" = ", System.StringComparison.Ordinal);
                    values[section + "|" + line.Substring(0, eq).Trim()] = line.Substring(eq + 3).Trim();
                }
            }
            bool On(string key, bool fallback) => values.TryGetValue(key, out var v) ? v.Equals("true", System.StringComparison.OrdinalIgnoreCase) : fallback;
            var all = On("01 - General|Enabled", true);
            Crafting.Value = all && On("02 - Craft from Storage|Enabled", true);
            Building.Value = all && On("03 - Build from Storage|Enabled", true);
            Stations.Value = all && (On("04 - Refuel from Storage|Fireplace-class Fuel", true) || On("04 - Refuel from Storage|Smelter-class Fuel", true) ||
                                     On("04 - Refuel from Storage|Smelter-class Ore", true));
            ShowCounts.Value = On("99 - Diagnostics|ShowMaterialQuantities", true);
            if (values.TryGetValue("02 - Craft from Storage|Distance", out var d) &&
                float.TryParse(d, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var range))
                Range.Value = Mathf.Clamp(range, 5f, 100f);
            if (values.TryGetValue("01 - General|ContainerTypes", out var types))
            {
                var t = types.ToLowerInvariant();
                Personal.Value = t.Contains("personal") || t.Contains("piece_chest_private");
                CartsShips.Value = t.Contains("carts") || t.Contains("ships") || t.Contains("cart") || t.Contains("karve") || t.Contains("vikingship");
            }
            Logger.LogInfo($"Took over Local Craft From Storage's settings once: range {Range.Value:0} m, crafting {Crafting.Value}, building {Building.Value}, " +
                           $"fires and stations {Stations.Value}, personal chests {Personal.Value}, carts and ships {CartsShips.Value}.");
        }
    }
}
