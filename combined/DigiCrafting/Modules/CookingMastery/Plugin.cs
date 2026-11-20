// Your Cooking skill makes all food better: more health, stamina, eitr and healing per tick, and optionally longer
// lasting, scaling up to the set factors at Cooking 100. Also:
// - Cooking XP multiplier, and crafting food trains Cooking at any station (cauldron, workbench, ...) and from the
//   inventory (vanilla trains the station's skill, or nothing).
// - Food tooltips show the bonus: "40 +10" when crafting, the boosted value on food you own.
// - Optional: food keeps full strength until it runs out (no decay).
// How: while the game reads a food's stats (eating, the per-frame food update) the item's stats are boosted, and they
// are always restored exactly afterwards, so stats can never creep up. No allocations per frame.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace CookingMastery
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.cookingmastery";
        public const string Name = "Digi's Cooking Mastery";
        public const string Version = "1.0.1";

        private static ConfigEntry<bool> _enabled, _noDecay, _craftTrainsCooking, _tooltips, _logXp;
        private static ConfigEntry<float> _health, _stamina, _regen, _eitr, _duration, _xp;
        private static BepInEx.Logging.ManualLogSource _log;

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
            _log = Logger;
            _enabled = Config.Bind("1 - General", "Enabled", true, "Food gets stronger the higher your Cooking skill is.");
            _noDecay = Config.Bind("1 - General", "No stat decay", false,
                "Food keeps its full health, stamina and eitr until it runs out (normally it weakens over time).");
            _health = Factor("Health", 1.5f);
            _stamina = Factor("Stamina", 1.5f);
            _eitr = Factor("Eitr", 1.5f);
            _regen = Factor("Healing per tick", 1.5f);
            _duration = Config.Bind("2 - Bonus at Cooking 100", "Duration", 1.5f,
                new ConfigDescription("Food duration multiplier at Cooking 100 (1 = vanilla). Lower skill gives part of it.",
                    new AcceptableValueRange<float>(1f, 3f)));
            _xp = Config.Bind("3 - Skill", "Cooking XP multiplier", 5f,
                new ConfigDescription("All Cooking XP times this (5 = five times as fast).", new AcceptableValueRange<float>(0.1f, 100f)));
            _craftTrainsCooking = Config.Bind("3 - Skill", "Crafting food trains Cooking", true,
                "Making food at any station (cauldron, workbench, ...) or in your inventory gives Cooking XP.");
            _tooltips = Config.Bind("4 - Display", "Show bonus in tooltips", true, "Food tooltips show the extra you get from your Cooking skill, e.g. \"40 +10\".");
            _logXp = Config.Bind("5 - Debug", "Log Cooking XP", false, "Write every Cooking XP gain to the log file (for troubleshooting).");
            PatchModuleNamespace();
        }

        private ConfigEntry<float> Factor(string name, float value) =>
            Config.Bind("2 - Bonus at Cooking 100", name, value,
                new ConfigDescription($"{name} multiplier at Cooking 100 (1 = vanilla). Lower skill gives part of it.",
                    new AcceptableValueRange<float>(1f, 5f)));

        // --- Bonus factors -------------------------------------------------------------------------------------

        private struct Factors
        {
            public float Health, Stamina, Regen, Eitr, Duration;
            public bool Any => Health != 1f || Stamina != 1f || Regen != 1f || Eitr != 1f || Duration != 1f;
        }

        private static Factors Current()
        {
            var player = Player.m_localPlayer;
            var skill = !_enabled.Value || player == null ? 0f : Mathf.Clamp01(player.GetSkillLevel(Skills.SkillType.Cooking) / 100f);
            return new Factors
            {
                Health = 1f + skill * (_health.Value - 1f),
                Stamina = 1f + skill * (_stamina.Value - 1f),
                Regen = 1f + skill * (_regen.Value - 1f),
                Eitr = 1f + skill * (_eitr.Value - 1f),
                Duration = 1f + skill * (_duration.Value - 1f),
            };
        }

        private static bool IsFood(ItemDrop.ItemData.SharedData s) =>
            s != null && (s.m_food > 0f || s.m_foodStamina > 0f || s.m_foodEitr > 0f);

        private static float Boost(float value, float factor) => Mathf.Round(value * factor * 10f) / 10f;

        // --- Temporary boost of shared item stats, always restored exactly ---------------------------------------

        private sealed class Original
        {
            public float Food, Stamina, Eitr, Regen;
        }

        // Shared data currently boosted; nested calls (EatFood -> UpdateFood) never boost twice.
        private static readonly Dictionary<ItemDrop.ItemData.SharedData, Original> Boosted = new Dictionary<ItemDrop.ItemData.SharedData, Original>();
        private static readonly Stack<Original> Pool = new Stack<Original>();
        // One list per nesting level, reused, so the per-frame food update allocates nothing.
        private static readonly List<List<ItemDrop.ItemData.SharedData>> Lists = new List<List<ItemDrop.ItemData.SharedData>>();
        private static int _depth;

        private static List<ItemDrop.ItemData.SharedData> Rent()
        {
            if (_depth == Lists.Count) Lists.Add(new List<ItemDrop.ItemData.SharedData>());
            return Lists[_depth++];
        }

        private static void BoostOne(ItemDrop.ItemData.SharedData s, Factors f, List<ItemDrop.ItemData.SharedData> owned)
        {
            if (!IsFood(s) || Boosted.ContainsKey(s)) return;
            var o = Pool.Count > 0 ? Pool.Pop() : new Original();
            o.Food = s.m_food;
            o.Stamina = s.m_foodStamina;
            o.Eitr = s.m_foodEitr;
            o.Regen = s.m_foodRegen;
            Boosted[s] = o;
            s.m_food = Boost(s.m_food, f.Health);
            s.m_foodStamina = Boost(s.m_foodStamina, f.Stamina);
            s.m_foodEitr = Boost(s.m_foodEitr, f.Eitr);
            s.m_foodRegen = Boost(s.m_foodRegen, f.Regen);
            owned.Add(s);
        }

        private static void RestoreAll(bool rented)
        {
            if (!rented) return;
            var owned = Lists[--_depth];
            foreach (var s in owned)
            {
                if (!Boosted.TryGetValue(s, out var o)) continue;
                s.m_food = o.Food;
                s.m_foodStamina = o.Stamina;
                s.m_foodEitr = o.Eitr;
                s.m_foodRegen = o.Regen;
                Boosted.Remove(s);
                Pool.Push(o);
            }
            owned.Clear();
        }

        private static float OriginalFood(ItemDrop.ItemData.SharedData s) => Boosted.TryGetValue(s, out var o) ? o.Food : s.m_food;
        private static float OriginalStamina(ItemDrop.ItemData.SharedData s) => Boosted.TryGetValue(s, out var o) ? o.Stamina : s.m_foodStamina;
        private static float OriginalEitr(ItemDrop.ItemData.SharedData s) => Boosted.TryGetValue(s, out var o) ? o.Eitr : s.m_foodEitr;
        private static float OriginalRegen(ItemDrop.ItemData.SharedData s) => Boosted.TryGetValue(s, out var o) ? o.Regen : s.m_foodRegen;

        // --- Eating and the food update ------------------------------------------------------------------------

        [HarmonyPatch(typeof(Player), "EatFood")]
        private static class EatFoodPatch
        {
            private static void Prefix(Player __instance, ItemDrop.ItemData item, out bool __state)
            {
                __state = false;
                if (__instance != Player.m_localPlayer || item == null) return;
                var f = Current();
                if (!f.Any) return;
                __state = true;
                BoostOne(item.m_shared, f, Rent());
            }

            private static void Postfix(Player __instance, ItemDrop.ItemData item, bool __result, List<Player.Food> ___m_foods)
            {
                if (!__result || __instance != Player.m_localPlayer || item?.m_shared == null) return;
                var duration = Current().Duration;
                if (duration == 1f) return;
                foreach (var food in ___m_foods)
                    if (food.m_item?.m_shared?.m_name == item.m_shared.m_name && food.m_time > 0f)
                        food.m_time *= duration;
            }

            private static Exception Finalizer(Exception __exception, bool __state)
            {
                RestoreAll(__state);
                return __exception;
            }
        }

        [HarmonyPatch(typeof(Player), "UpdateFood")]
        private static class UpdateFoodPatch
        {
            private static void Prefix(Player __instance, List<Player.Food> ___m_foods, out bool __state)
            {
                __state = false;
                if (__instance != Player.m_localPlayer || ___m_foods == null || ___m_foods.Count == 0) return;
                var f = Current();
                if (!f.Any) return;
                __state = true;
                var owned = Rent();
                foreach (var food in ___m_foods) BoostOne(food?.m_item?.m_shared, f, owned);
            }

            private static Exception Finalizer(Exception __exception, bool __state)
            {
                RestoreAll(__state);
                return __exception;
            }
        }

        // No decay: replace the decaying food part with full-strength values, keeping other mods' additions.
        [HarmonyPatch(typeof(Player), "GetTotalFoodValue")]
        private static class TotalFoodPatch
        {
            private static void Postfix(Player __instance, List<Player.Food> ___m_foods, ref float hp, ref float stamina, ref float eitr)
            {
                if (!_noDecay.Value || __instance != Player.m_localPlayer || ___m_foods == null || ___m_foods.Count == 0) return;
                var f = Current();
                foreach (var food in ___m_foods)
                {
                    var shared = food?.m_item?.m_shared;
                    if (shared == null) continue;
                    hp += Boost(OriginalFood(shared), f.Health) - food.m_health;
                    stamina += Boost(OriginalStamina(shared), f.Stamina) - food.m_stamina;
                    eitr += Boost(OriginalEitr(shared), f.Eitr) - food.m_eitr;
                }
            }
        }

        // --- Skill: XP multiplier, crafting food trains Cooking -----------------------------------------------

        private static readonly AccessTools.FieldRef<InventoryGui, Recipe> CraftRecipe = AccessTools.FieldRefAccess<InventoryGui, Recipe>("m_craftRecipe");
        private static readonly AccessTools.FieldRef<InventoryGui, bool> MultiCrafting = AccessTools.FieldRefAccess<InventoryGui, bool>("m_multiCrafting");
        private static bool _craftingFood, _cookingRaised;

        [HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
        private static class DoCraftingPatch
        {
            private static void Prefix(InventoryGui __instance, Player player, out int __state)
            {
                __state = -1;
                _craftingFood = _cookingRaised = false;
                if (!_craftTrainsCooking.Value) return;
                var item = CraftRecipe(__instance)?.m_item?.m_itemData;
                if (player == null || player != Player.m_localPlayer || !IsFood(item?.m_shared)) return;
                _craftingFood = true;
                __state = player.GetInventory().CountItems(item.m_shared.m_name);
            }

            // Crafting without a station raises no skill in vanilla; grant Cooking if the craft went through.
            private static void Postfix(InventoryGui __instance, Player player, int __state)
            {
                if (!_craftingFood || _cookingRaised || __state < 0 || player == null) return;
                var item = CraftRecipe(__instance)?.m_item?.m_itemData;
                if (item == null || player.GetInventory().CountItems(item.m_shared.m_name) <= __state) return;
                player.RaiseSkill(Skills.SkillType.Cooking, MultiCrafting(__instance) ? __instance.m_multiCraftAmount : 1);
            }

            private static Exception Finalizer(Exception __exception)
            {
                _craftingFood = _cookingRaised = false;
                return __exception;
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.RaiseSkill))]
        private static class RaiseSkillPatch
        {
            private static void Prefix(Player __instance, ref Skills.SkillType skill, ref float value)
            {
                if (__instance != Player.m_localPlayer) return;
                if (_craftingFood)
                {
                    skill = Skills.SkillType.Cooking;
                    _cookingRaised = true;
                }
                if (skill != Skills.SkillType.Cooking) return;
                value *= _xp.Value;
                if (_logXp.Value) _log.LogInfo($"[XP] Cooking +{value:0.##} (crafting food: {_craftingFood})");
            }
        }

        // --- Tooltips ------------------------------------------------------------------------------------------

        private static readonly Regex HealthLine = new Regex(@"(\$item_food_health: <color=#ff8080ff>)([^<]*)(</color>)", RegexOptions.Compiled);
        private static readonly Regex StaminaLine = new Regex(@"(\$item_food_stamina: <color=#ffff80ff>)([^<]*)(</color>)", RegexOptions.Compiled);
        private static readonly Regex EitrLine = new Regex(@"(\$item_food_eitr: <color=#9090ffff>)([^<]*)(</color>)", RegexOptions.Compiled);
        private static readonly Regex RegenLine = new Regex(@"(\$item_food_regen: <color=orange>)([^<]*)( hp/tick</color>)", RegexOptions.Compiled);
        private static readonly Regex DurationLine = new Regex(@"(\$item_food_duration: <color=orange>)([^<]*)(</color>)", RegexOptions.Compiled);

        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip),
            typeof(ItemDrop.ItemData), typeof(int), typeof(bool), typeof(float), typeof(int), typeof(bool))]
        private static class TooltipPatch
        {
            private static void Postfix(ItemDrop.ItemData item, bool crafting, bool appending, ref string __result)
            {
                if (!_tooltips.Value || appending || string.IsNullOrEmpty(__result) || Player.m_localPlayer == null) return;
                var s = PrintedFood(item?.m_shared);
                if (!IsFood(s)) return;
                var f = Current();
                if (!f.Any) return;
                __result = Line(__result, HealthLine, OriginalFood(s), f.Health, crafting, "");
                __result = Line(__result, StaminaLine, OriginalStamina(s), f.Stamina, crafting, "");
                __result = Line(__result, EitrLine, OriginalEitr(s), f.Eitr, crafting, "");
                __result = Line(__result, RegenLine, OriginalRegen(s), f.Regen, crafting, " hp/tick");
                if (s.m_foodBurnTime > 0f && f.Duration != 1f)
                {
                    var burn = s.m_foodBurnTime;
                    var text = crafting
                        ? ItemDrop.ItemData.GetDurationString(burn) + "</color>" + Bonus(ItemDrop.ItemData.GetDurationString(burn * f.Duration - burn))
                        : ItemDrop.ItemData.GetDurationString(burn * f.Duration) + "</color>";
                    __result = DurationLine.Replace(__result, m => m.Groups[1].Value + text, 1);
                }
            }

            // The food whose stats the game printed: on raw food (unbaked pie, dough) that is the cooked food it turns
            // into. The raw item's own food values are leftovers nothing uses (unbaked poteitr 110 eitr, baked 100).
            private static ItemDrop.ItemData.SharedData PrintedFood(ItemDrop.ItemData.SharedData s)
            {
                if (s == null || s.m_itemType == ItemDrop.ItemData.ItemType.Consumable) return s;
                return s.m_appendToolTip != null ? s.m_appendToolTip.m_itemData?.m_shared : null;
            }

            private static string Number(float v) => v.ToString("0.#", CultureInfo.InvariantCulture);
            private static string Bonus(string text) => " <color=#80ff80ff>+" + text + "</color>";

            private static string Line(string tooltip, Regex line, float value, float factor, bool crafting, string unit)
            {
                if (value <= 0f || factor == 1f) return tooltip;
                var boosted = Boost(value, factor);
                var text = crafting
                    ? Number(value) + unit + "</color>" + Bonus(Number(boosted - value))
                    : Number(boosted) + unit + "</color>";
                return line.Replace(tooltip, m => m.Groups[1].Value + text, 1);
            }
        }
    }
}
