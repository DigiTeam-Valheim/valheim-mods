// Protects buildings from rain and water wear, each part switchable.
// Vanilla: an uncovered piece that is wet (rain, or standing in water) loses 5 health a minute until it is at 50%.
// Instead of replacing the game's whole wear routine, this changes only the one "is it wet?" check that decides rain
// and water wear (inside WearNTear.UpdateWear). Everything else stays vanilla: support, snow, ash, lava, events, and
// wet wood still does not catch fire.
// - Rain: protect everything, only listed pieces, or everything except listed pieces; optionally allow some damage.
// - Water: pieces standing in water or just above it (docks, piers) do not rot. Only pieces that can rot are
//   affected anyway (wood); stone and metal never rot in vanilla.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NoWeatherDamage
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.noweatherdamage";
        public const string Name = "Digi's No Weather Damage";
        public const string Version = "1.0.1";

        public enum Filter { All, OnlyListed, AllExceptListed }

        private static ConfigEntry<bool> _rain, _water;
        private static ConfigEntry<int> _rainMaxDamage;
        private static ConfigEntry<Filter> _filter;
        private static ConfigEntry<string> _filterList;
        private static ConfigEntry<float> _waterline;
        private static string[] _filterWords = new string[0];

        private static readonly AccessTools.FieldRef<WearNTear, bool> RainWet = AccessTools.FieldRefAccess<WearNTear, bool>("m_rainWet");
        private static readonly Func<WearNTear, bool> UnderWater =
            AccessTools.MethodDelegate<Func<WearNTear, bool>>(AccessTools.Method(typeof(WearNTear), "IsUnderWater"));

        // The plugin ID changed from pieter.valheim.* to digi.valheim.*: carry the old settings file over once.
        private void MigrateConfig()
        {
            var old = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, Guid.Replace("digi.valheim.", "pieter.valheim.") + ".cfg");
            if (System.IO.File.Exists(Config.ConfigFilePath) || !System.IO.File.Exists(old)) return;
            System.IO.File.Copy(old, Config.ConfigFilePath);
            Config.Reload();
        }


        private void Awake()
        {
            MigrateConfig();
            _rain = Config.Bind("Rain", "Protect from rain", true, "Buildings without a roof do not lose health in the rain.");
            _rainMaxDamage = Config.Bind("Rain", "Max rain damage (%)", 0,
                new ConfigDescription("How much health rain may still take from a piece: 0 = none, 50 = normal (rain normally stops at half health).", new AcceptableValueRange<int>(0, 50)));
            _filter = Config.Bind("Rain", "Which pieces", Filter.All,
                "All = protect every piece. OnlyListed = only pieces that match the list below. AllExceptListed = every piece except those.");
            _filterList = Config.Bind("Rain", "Piece list", "",
                "Words matched against piece names, comma-separated, e.g. \"wood_floor, roof\".");
            _water = Config.Bind("Water", "Protect from water", true,
                "Pieces standing in water or just above it (docks, piers, boathouses) do not rot. Ships are not affected.");
            _waterline = Config.Bind("Water", "Waterline height (m)", 2.5f,
                new ConfigDescription("How far above the water a piece still counts as \"at the water\", in meters.", new AcceptableValueRange<float>(0f, 10f)));
            _filterList.SettingChanged += (_, __) => ParseList();
            ParseList();
            PatchModule(typeof(Plugin));
        }

        private static void ParseList() =>
            _filterWords = (_filterList.Value ?? "").Split(',').Select(w => w.Trim().ToLowerInvariant()).Where(w => w.Length > 0).ToArray();

        // Replace the IsWet() call inside UpdateWear with WetForWear(this).
        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.UpdateWear))]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> UpdateWear(IEnumerable<CodeInstruction> instructions)
        {
            var isWet = AccessTools.Method(typeof(WearNTear), nameof(WearNTear.IsWet));
            var replacement = AccessTools.Method(typeof(Plugin), nameof(WetForWear));
            var replaced = 0;
            foreach (var code in instructions)
            {
                if ((code.opcode == OpCodes.Call || code.opcode == OpCodes.Callvirt) && code.operand as MethodInfo == isWet)
                {
                    replaced++;
                    yield return new CodeInstruction(OpCodes.Call, replacement).WithLabels(code.labels).WithBlocks(code.blocks);
                    continue;
                }
                yield return code;
            }
            if (replaced == 0) Debug.LogWarning("[No Weather Damage] Rain/water check not found in WearNTear.UpdateWear; the mod does nothing.");
        }

        public static bool WetForWear(WearNTear piece)
        {
            var rain = RainWet(piece);
            var underWater = UnderWater(piece);
            if (!rain && !underWater) return false;
            if (piece.GetComponentInParent<Ship>() != null) return true; // ships stay vanilla

            var waterProtected = _water.Value && (underWater || AtWaterline(piece));
            if (waterProtected) return false;
            if (underWater) return true; // in water, but water protection is off for this piece
            return !RainProtected(piece);
        }

        private static bool AtWaterline(WearNTear piece)
        {
            var pos = piece.transform.position;
            WaterVolume volume = null;
            var level = Floating.GetWaterLevel(pos, ref volume);
            return level > -1000f && pos.y <= level + _waterline.Value;
        }

        private static bool RainProtected(WearNTear piece)
        {
            if (!_rain.Value || !Matches(piece)) return false;
            var max = _rainMaxDamage.Value;
            // Still wet (damage allowed) until the piece has lost the allowed share of its health.
            return max <= 0 || piece.GetHealthPercentage() <= 1f - max / 100f;
        }

        private static bool Matches(WearNTear piece)
        {
            if (_filter.Value == Filter.All) return true;
            var name = piece.gameObject.name.ToLowerInvariant();
            var listed = false;
            foreach (var word in _filterWords)
                if (name.Contains(word)) { listed = true; break; }
            return _filter.Value == Filter.OnlyListed ? listed : !listed;
        }
    }
}
