// Boss-driven star progression on top of StarLevelSystem (SLS):
// - Normal creatures: fixed chance per star. Each defeated boss raises the star cap of the biomes before it
//   (one boss behind) up to 5 stars, then two more bosses raise the chances. The tables are handed to SLS through
//   its boss-conditional level hook (ConditionalScaleSystem.GetConditionalLevelupChance), which SLS uses for both
//   the roll and the biome's max level. Biomes without progression use DefaultCreatureLevelUpChance in LevelSettings.yaml.
// - Bosses: 0 stars until defeated once, then their own fixed table; the cap rises two bosses behind (4 stars, then
//   5 at three), and four and five bosses behind raise the chances of high stars (5 stars 10%, then 20%).
// - Damage: SLS gives +50% per star; 4 and 5 stars get +175% / +200% instead of +200% / +250%, and creatures in the
//   Ocean get no extra damage at all (boats). Health and size stay as SLS sets them.
// - Trophies: normal creatures get a per-star trophy chance; boss trophies are multiplied per star on top of
//   Epic Loot's one-per-player count.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace BossStarsAfterDefeat
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.bossstarsafterdefeat";
        public const string Name = "Digi's Boss Stars After Defeat";
        public const string Version = "2.0.5";
        private const string StarLevelSystemGuid = "MidnightsFX.StarLevelSystem";
        private const string EpicLootGuid = "randyknapp.mods.epicloot";

        // Boss keys in progression order (same as SLS's vanilla order).
        private static readonly string[] BossKeys =
        {
            "defeated_eikthyr", "defeated_gdking", "defeated_bonemass", "defeated_dragon",
            "defeated_goblinking", "defeated_queen", "defeated_fader", "defeated_frozenking_p3"
        };

        // Biome -> index of its own boss in BossKeys. The Ocean follows the Swamp.
        private static readonly Dictionary<Heightmap.Biome, int> BiomeIndex = new Dictionary<Heightmap.Biome, int>
        {
            { Heightmap.Biome.Meadows, 0 }, { Heightmap.Biome.BlackForest, 1 }, { Heightmap.Biome.Swamp, 2 },
            { Heightmap.Biome.Ocean, 2 }, { Heightmap.Biome.Mountain, 3 }, { Heightmap.Biome.Plains, 4 },
            { Heightmap.Biome.Mistlands, 5 }, { Heightmap.Biome.AshLands, 6 }, { Heightmap.Biome.DeepNorth, 7 }
        };

        // SLS tables: key = level (1 = 0 stars), value = % chance to roll past that level, i.e. the chance of at
        // least (key) stars. The last key is the cap. Exact per-star chances are in the comments.
        private static readonly SortedDictionary<int, float>[] CreatureTables =
        {
            null, // stage 0: LevelSettings.yaml default, 15 / 7.5 / 3.75
            Table(37.5f, 17.5f, 7.5f, 2.5f, 0f),                // 4 stars: 20 / 10 / 5 / 2.5
            Table(45f, 25f, 12.5f, 5f, 1.25f, 0f),             // 5 stars: 20 / 12.5 / 7.5 / 3.75 / 1.25
            Table(55f, 32.5f, 17.5f, 7.5f, 2.5f, 0f),          // boost 1: 22.5 / 15 / 10 / 5 / 2.5
            Table(66.25f, 41.25f, 23.75f, 11.25f, 3.75f, 0f)   // boost 2: 25 / 17.5 / 12.5 / 7.5 / 3.75
        };

        private static readonly SortedDictionary<int, float>[] BossTables =
        {
            Table(35f, 15f, 5f, 0f),                   // 3 stars: 20 / 10 / 5
            Table(43.75f, 23.75f, 11.25f, 3.75f, 0f),  // 4 stars: 20 / 12.5 / 7.5 / 3.75
            Table(52.5f, 32.5f, 17.5f, 7.5f, 2.5f, 0f), // 5 stars: 20 / 15 / 10 / 5 / 2.5
            Table(67.5f, 47.5f, 32.5f, 20f, 10f, 0f),   // boost 1: 20 / 15 / 12.5 / 10 / 10
            Table(80f, 65f, 50f, 35f, 20f, 0f)          // boost 2: 15 / 15 / 15 / 15 / 20
        };

        private static readonly string[] StageNames = { "base (3*)", "4*", "5*", "5* boost 1", "5* boost 2" };

        // Damage factor per star (index = stars) for normal creatures, replacing SLS's 1 + 0.5 per star.
        private static readonly float[] DamageFactors = { 1f, 1.5f, 2f, 2.5f, 2.75f, 3f };

        private static ConfigEntry<bool> _progression, _damage, _bossTrophies, _logTrophyRolls, _logStars, _trophiesAsBefore;
        private static ConfigEntry<float> _bossTrophyMultiplier;
        private static ConfigEntry<string> _trophyChances;
        private static float[] _trophyChanceTable;
        private static BepInEx.Logging.ManualLogSource _log;

        private static readonly AccessTools.FieldRef<Attack, Humanoid> AttackCharacter =
            AccessTools.FieldRefAccess<Attack, Humanoid>("m_character");

        private static ConfigEntry<float> _slsDamagePerStar;
        private static ConfigEntry<bool> _scaleAllLoot;
        private static ConfigEntry<string> _slsLootType;
        private static ConfigEntry<float> _slsLootScale, _slsChanceBase, _slsChancePerLevel;

        [ThreadStatic] private static Character _rolling;
        private static int _loggedHighest = int.MinValue;

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
            _progression = Config.Bind("Stars", "Boss progression", true,
                "Each boss you defeat allows more stars on creatures in earlier biomes (up to 5), and lets earlier bosses spawn with stars. Off = only the Star Level System settings decide.");
            _damage = Config.Bind("Stars", "Per-star damage", true,
                "Starred creatures hit harder: +50% per star up to 3 stars, +175% at 4 and +200% at 5 stars (not at sea). Off = Star Level System's own damage setting.");
            _logStars = Config.Bind("Stars", "Log star progression", false,
                "Write star levels and boss star rolls to the log file (for troubleshooting).");
            _trophyChances = Config.Bind("Trophies", "Trophy chance per star (%)", "10, 15, 20, 25, 37.5, 50",
                "Trophy drop chance of normal creatures at 0, 1, 2, ... stars, comma-separated. Creatures that normally drop their trophy more often keep their own chance. Stars past the end of the list use the last value.");
            _bossTrophies = Config.Bind("Trophies", "Multiply boss trophies per star", true,
                "Starred bosses drop more trophies.");
            _bossTrophyMultiplier = Config.Bind("Trophies", "Boss trophy multiplier per star", 1.5f,
                new ConfigDescription("Each boss star multiplies the trophies by this: 1.5 gives x1, x1.5, x2.25, x3.4, ... at 0, 1, 2, 3 stars (fractions are rounded up or down at random).", new AcceptableValueRange<float>(1f, 2f)));
            _logTrophyRolls = Config.Bind("Trophies", "Log trophy rolls", false,
                "Write every trophy drop roll to the log file (for troubleshooting).");
            _trophiesAsBefore = Config.Bind("Trophies", "Trophies as before Valheim 1.0.20", true,
                "On: stars no longer raise the trophy chance, as before Valheim 1.0.20 (the chance per star above still " +
                "applies). With Star Level System's ScaleAllLootByLevel, starred creatures then drop more trophies at " +
                "once instead. Off: the 1.0.20 way: the chance grows with stars, always 1 trophy.");
            _trophyChanceTable = ParseChances(_trophyChances.Value);
            _trophyChances.SettingChanged += (s, e) => _trophyChanceTable = ParseChances(_trophyChances.Value);

            var valConfig = AccessTools.TypeByName("StarLevelSystem.common.ValConfig");
            _slsDamagePerStar = AccessTools.Field(valConfig, "EnemyDamageLevelMultiplier")?.GetValue(null) as ConfigEntry<float>;
            _scaleAllLoot = AccessTools.Field(valConfig, "ScaleAllLootByLevel")?.GetValue(null) as ConfigEntry<bool>;
            _slsLootType = AccessTools.Field(valConfig, "LootDropCalculationType")?.GetValue(null) as ConfigEntry<string>;
            _slsLootScale = AccessTools.Field(valConfig, "PerLevelLootScale")?.GetValue(null) as ConfigEntry<float>;
            _slsChanceBase = AccessTools.Field(valConfig, "ChanceBaseChancePerLevel")?.GetValue(null) as ConfigEntry<float>;
            _slsChancePerLevel = AccessTools.Field(valConfig, "PerLevelLootChanceScale")?.GetValue(null) as ConfigEntry<float>;

            var harmony = ModuleHarmony;
            var selection = AccessTools.TypeByName("StarLevelSystem.modules.LevelSystem.LevelSelection");
            var conditional = AccessTools.TypeByName("StarLevelSystem.modules.LevelSystem.ConditionalScaleSystem");
            var maxLevel = AccessTools.Method(selection, "GetMaxCreatureLevel");
            var determine = selection?.GetMethods(AccessTools.all).FirstOrDefault(m =>
                m.Name == "DetermineLevel" && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(Character));
            var chance = selection?.GetMethods(AccessTools.all).FirstOrDefault(m =>
                m.Name == "DetermineLevelupChance" && m.GetParameters().Length == 6);
            var conditionalChance = AccessTools.Method(conditional, "GetConditionalLevelupChance");

            if (maxLevel == null || determine == null || chance == null || conditionalChance == null || _slsDamagePerStar == null)
            {
                Logger.LogError($"Star progression disabled, StarLevelSystem internals not found (max {maxLevel != null}, " +
                                $"roll {determine != null}, chance {chance != null}, conditional {conditionalChance != null}, " +
                                $"damage {_slsDamagePerStar != null}).");
            }
            else
            {
                harmony.Patch(maxLevel, postfix: Hook(nameof(MaxLevelPostfix)));
                harmony.Patch(determine, prefix: Hook(nameof(DeterminePrefix)), postfix: Hook(nameof(DeterminePostfix)),
                    finalizer: Hook(nameof(DetermineFinalizer)));
                harmony.Patch(chance, postfix: Hook(nameof(BossChancePostfix)));
                harmony.Patch(conditionalChance, postfix: Hook(nameof(ConditionalChancePostfix)));
                harmony.Patch(AccessTools.Method(typeof(Attack), "GetLevelDamageFactor"),
                    postfix: new HarmonyMethod(typeof(Plugin), nameof(DamageFactorPostfix)) { priority = Priority.Last });
            }

            // Runs after Epic Loot's postfix (default priority), which sets the per-player boss trophy count.
            harmony.Patch(AccessTools.Method(typeof(CharacterDrop), "GenerateDropList"),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(DropListPrefix)),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(DropListPostfix)) { priority = Priority.Last });
            Logger.LogInfo("Boss star progression, per-star damage and per-star trophies active.");
        }

        private static HarmonyMethod Hook(string name) =>
            new HarmonyMethod(typeof(Plugin).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static));

        private static SortedDictionary<int, float> Table(params float[] chances)
        {
            var table = new SortedDictionary<int, float>();
            for (var i = 0; i < chances.Length; i++) table[i + 1] = chances[i];
            return table;
        }

        private static float[] ParseChances(string text)
        {
            var list = new List<float>();
            foreach (var part in (text ?? "").Split(','))
                if (float.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    list.Add(Mathf.Clamp(v, 0f, 100f));
            return list.Count > 0 ? list.ToArray() : new[] { 0f };
        }

        // --- Progression ---------------------------------------------------------------------------------------

        // Index of the latest defeated boss in BossKeys, -1 when none.
        private static int HighestDefeated()
        {
            if (ZoneSystem.instance == null) return -1;
            for (var i = BossKeys.Length - 1; i >= 0; i--)
                if (ZoneSystem.instance.GetGlobalKey(BossKeys[i])) return i;
            return -1;
        }

        private static int BiomeStage(Heightmap.Biome biome, int highest) =>
            BiomeIndex.TryGetValue(biome, out var index) ? Mathf.Clamp(highest - index, 0, CreatureTables.Length - 1) : 0;

        private static int BossStage(Character boss, int highest)
        {
            var index = Array.IndexOf(BossKeys, boss.m_defeatSetGlobalKey);
            return index < 0 ? 0 : Mathf.Clamp(highest - index - 1, 0, BossTables.Length - 1);
        }

        // Normal creatures: SLS asks for the boss-conditional table of a biome; it uses it for the roll and the cap.
        private static void ConditionalChancePostfix(Heightmap.Biome biome, ref SortedDictionary<int, float> __result)
        {
            if (!_progression.Value) return;
            var highest = HighestDefeated();
            LogStages(highest);
            var stage = BiomeStage(biome, highest);
            if (stage > 0) __result = CreatureTables[stage];
        }

        private static void MaxLevelPostfix(Character character, ref int __result)
        {
            if (__result <= 1 || character == null || !character.IsBoss()) return; // 1 = pinned boss phase
            var key = character.m_defeatSetGlobalKey;
            if (string.IsNullOrEmpty(key) || ZoneSystem.instance == null) return;
            if (!ZoneSystem.instance.GetGlobalKey(key))
            {
                __result = 1; // not defeated yet: no stars
                return;
            }
            if (_progression.Value) __result = BossTables[BossStage(character, HighestDefeated())].Keys.Last();
        }

        [ThreadStatic] private static string _bossTableUsed;

        private static void DeterminePrefix(Character character)
        {
            _rolling = character;
            _bossTableUsed = null;
            _creatureTableUsed = null;
        }

        private static void DeterminePostfix(Character character, ZDO cZDO, Heightmap.Biome biome, int leveloverride, bool allowRoll, int __result)
        {
            if (!_logStars.Value || character == null || !_progression.Value) return;
            if (!character.IsBoss())
            {
                RecordCreatureRoll(biome, cZDO, leveloverride, allowRoll, __result);
                return;
            }
            var stage = BossStage(character, HighestDefeated());
            _log.LogInfo($"Star roll: boss {Utils.GetPrefabName(character.gameObject)} level {__result} ({Math.Max(0, __result - 1)}*), " +
                         $"table {StageNames[stage]}, defeated before: {ZoneSystem.instance?.GetGlobalKey(character.m_defeatSetGlobalKey)}, " +
                         $"saved level {cZDO?.GetInt(ZDOVars.s_level, 0)}, override {leveloverride}, roll allowed {allowRoll}, " +
                         $"boss table used: {_bossTableUsed ?? "no (no new roll)"}");
        }

        private static Exception DetermineFinalizer(Exception __exception)
        {
            _rolling = null;
            return __exception;
        }

        // Bosses: replace SLS's boss/biome chances with the boss table for this boss's stage.
        // Normal creatures: only note which table SLS picked, for the roll summary.
        private static void BossChancePostfix(bool isBoss, ref SortedDictionary<int, float> __result)
        {
            if (!isBoss)
            {
                var index = Array.IndexOf(CreatureTables, __result);
                _creatureTableUsed = index > 0 ? StageNames[index]
                    : __result != null && __result.TryGetValue(1, out var first) ? $"default (1+ stars {first}%)" : "none";
                return;
            }
            if (!_progression.Value || _rolling == null || !_rolling.IsBoss()) return;
            var stage = BossStage(_rolling, HighestDefeated());
            __result = BossTables[stage];
            _bossTableUsed = $"{StageNames[stage]} (1+ stars {__result[1]}%)";
        }

        // Normal creature rolls are summed per biome and table and written once a minute.
        [ThreadStatic] private static string _creatureTableUsed;
        private static readonly Dictionary<string, int[]> CreatureRolls = new Dictionary<string, int[]>();
        private static float _nextSummary;

        private static void RecordCreatureRoll(Heightmap.Biome biome, ZDO zdo, int leveloverride, bool allowRoll, int level)
        {
            var fresh = allowRoll && leveloverride <= 0 && zdo != null && zdo.GetInt(ZDOVars.s_level, 0) <= 0 && _creatureTableUsed != null;
            if (fresh)
            {
                var key = $"{biome} [{_creatureTableUsed}]";
                if (!CreatureRolls.TryGetValue(key, out var counts)) CreatureRolls[key] = counts = new int[7];
                counts[Mathf.Clamp(level - 1, 0, 6)]++;
            }
            _creatureTableUsed = null;

            if (Time.time < _nextSummary || CreatureRolls.Count == 0) return;
            _nextSummary = Time.time + 60f;
            foreach (var pair in CreatureRolls)
            {
                var total = pair.Value.Sum();
                var stars = string.Join(", ", Enumerable.Range(0, 7).Where(i => pair.Value[i] > 0)
                    .Select(i => $"{i}* {pair.Value[i]} ({100f * pair.Value[i] / total:0}%)"));
                _log.LogInfo($"Star rolls: {pair.Key}: {total} new creatures: {stars}");
            }
            CreatureRolls.Clear();
        }

        private static void LogStages(int highest)
        {
            if (highest == _loggedHighest || !_logStars.Value) return;
            _loggedHighest = highest;
            var parts = new[] { Heightmap.Biome.Meadows, Heightmap.Biome.BlackForest, Heightmap.Biome.Swamp, Heightmap.Biome.Ocean,
                    Heightmap.Biome.Mountain, Heightmap.Biome.Plains, Heightmap.Biome.Mistlands, Heightmap.Biome.AshLands, Heightmap.Biome.DeepNorth }
                .Select(b => $"{b} {StageNames[BiomeStage(b, highest)]}");
            _log.LogInfo($"Star stages (latest boss: {(highest >= 0 ? BossKeys[highest] : "none")}): {string.Join(", ", parts)}");
        }

        // --- Damage --------------------------------------------------------------------------------------------

        private static void DamageFactorPostfix(Attack __instance, ref float __result)
        {
            if (!_damage.Value || _slsDamagePerStar == null) return;
            var character = AttackCharacter(__instance);
            if (character == null || character.IsPlayer() || character.IsBoss()) return;
            var stars = character.GetLevel() - 1;
            if (stars <= 0) return;

            var sls = 1f + stars * _slsDamagePerStar.Value;
            if (sls <= 0f) return;
            var target = Heightmap.FindBiome(character.transform.position) == Heightmap.Biome.Ocean
                ? 1f
                : stars < DamageFactors.Length ? DamageFactors[stars] : DamageFactors[DamageFactors.Length - 1] + 0.25f * (stars - DamageFactors.Length + 1);
            __result = __result / sls * target;
        }

        // --- Trophies ------------------------------------------------------------------------------------------

        // "Trophies as before Valheim 1.0.20": 1.0.20 made trophies level-multiplied "chance only" drops. This puts this
        // creature's trophies (its own copy of the drop table) back as they were: no level multiplier, so the star level
        // does not raise the chance, and Star Level System's ScaleAllLootByLevel raises the amount again.
        private static void DropListPrefix(CharacterDrop __instance)
        {
            if (ChanceOnlyField == null || !_trophiesAsBefore.Value || __instance.m_drops == null) return;
            foreach (var entry in __instance.m_drops)
            {
                if (entry == null || !entry.m_prefab || !ChanceOnly(entry)) continue;
                var item = entry.m_prefab.GetComponent<ItemDrop>();
                if (item?.m_itemData?.m_shared?.m_itemType != ItemDrop.ItemData.ItemType.Trophy) continue;
                entry.m_levelMultiplier = false;
                ChanceOnlyField.SetValue(entry, false);
            }
        }

        private static void DropListPostfix(CharacterDrop __instance, List<KeyValuePair<GameObject, int>> __result)
        {
            if (__result == null) return;
            var character = __instance.GetComponent<Character>();
            if (character == null) return;
            var stars = Mathf.Clamp(character.GetLevel() - 1, 0, 10);

            if (!character.IsBoss())
            {
                ExtraTrophyRoll(__instance, __result, stars);
                return;
            }
            if (stars == 0 || !_bossTrophies.Value) return;

            var factor = Mathf.Pow(_bossTrophyMultiplier.Value, stars);
            for (var i = 0; i < __result.Count; i++)
            {
                var item = __result[i].Key ? __result[i].Key.GetComponent<ItemDrop>() : null;
                if (item?.m_itemData?.m_shared?.m_itemType != ItemDrop.ItemData.ItemType.Trophy) continue;
                var exact = __result[i].Value * factor;
                var count = Mathf.FloorToInt(exact);
                if (UnityEngine.Random.value < exact - count) count++;
                count = Mathf.Max(__result[i].Value, count);
                LogRoll($"boss {Utils.GetPrefabName(__instance.gameObject)} {stars}*: {__result[i].Key.name} {__result[i].Value} x {factor:0.##} = {exact:0.##} -> {count}");
                __result[i] = new KeyValuePair<GameObject, int>(__result[i].Key, count);
            }
        }

        // Normal creatures: when the vanilla roll missed a trophy, roll again so the total chance becomes
        // max(vanilla, per-star chance). Other drops keep their chance.
        private static void ExtraTrophyRoll(CharacterDrop drop, List<KeyValuePair<GameObject, int>> result, int stars)
        {
            var who = $"{Utils.GetPrefabName(drop.gameObject)} {stars}*";
            var table = _trophyChanceTable;
            var floor = table[Mathf.Min(stars, table.Length - 1)] / 100f;
            if (drop.m_drops == null)
            {
                LogRoll($"{who}: no drop table");
                return;
            }

            var sawTrophy = false;
            foreach (var entry in drop.m_drops)
            {
                var item = entry?.m_prefab ? entry.m_prefab.GetComponent<ItemDrop>() : null;
                if (item?.m_itemData?.m_shared?.m_itemType != ItemDrop.ItemData.ItemType.Trophy) continue;
                sawTrophy = true;
                var name = entry.m_prefab.name;
                var line = $"{who}: {name} vanilla {entry.m_chance:P1}";

                var existing = result.FindIndex(r => r.Key == entry.m_prefab);
                if (existing >= 0)
                {
                    LogRoll($"{line} -> vanilla roll HIT, dropping {result[existing].Value}");
                    continue;
                }
                // The game raises the chance of a level-multiplied drop per star. Since Valheim 1.0.20 trophies are
                // such drops, so the top-up starts from that raised chance.
                var chance = entry.m_levelMultiplier ? Mathf.Min(1f, entry.m_chance * ChanceFactor(stars + 1)) : entry.m_chance;
                if (chance <= 0f || chance >= 1f || floor <= 0f)
                {
                    LogRoll($"{line} -> vanilla roll missed, no extra roll (chance {chance:P1}, target {floor:P1})");
                    continue;
                }

                var total = Mathf.Min(1f, floor);
                if (total <= chance)
                {
                    LogRoll($"{line} -> vanilla roll missed, vanilla chance {chance:P1} already >= target {total:P1}");
                    continue;
                }
                var topUp = (total - chance) / (1f - chance);
                var roll = UnityEngine.Random.value;
                if (roll >= topUp)
                {
                    LogRoll($"{line} -> vanilla missed, extra roll {roll:0.000} vs {topUp:0.000} (total {total:P1}) MISSED");
                    continue;
                }

                var amount = UnityEngine.Random.Range(entry.m_amountMin, entry.m_amountMax + 1);
                // Like the game: the amount grows per star, except for drops marked "chance only" (trophies since 1.0.20).
                if ((entry.m_levelMultiplier || (_scaleAllLoot != null && _scaleAllLoot.Value)) && !ChanceOnly(entry))
                    amount *= Mathf.RoundToInt(LevelFactor(stars + 1));
                amount = Mathf.Max(1, amount);
                result.Add(new KeyValuePair<GameObject, int>(entry.m_prefab, amount));
                LogRoll($"{line} -> vanilla missed, extra roll {roll:0.000} vs {topUp:0.000} (total {total:P1}) HIT, dropping {amount}");
            }
            if (!sawTrophy) LogRoll($"{who}: has no trophy in its drop table");
        }

        // What a level-multiplied drop gets at this creature level. Star Level System replaces the game's 2^(level-1)
        // with its own loot style (its LootSystem settings); its distance bonuses are left out.
        private static float LevelFactor(int level)
        {
            if (level <= 1) return 1f;
            if (_slsLootType == null || _slsLootScale == null) return Mathf.Pow(2f, level - 1);
            var factor = _slsLootType.Value == "Exponential"
                ? Mathf.Pow(1f + _slsLootScale.Value, level - 1)
                : level * _slsLootScale.Value; // PerLevel and ChancePerLevel
            return Mathf.Max(1f, Mathf.Round(factor));
        }

        private static float ChanceFactor(int level)
        {
            if (_slsLootType != null && _slsLootType.Value == "ChancePerLevel" && _slsChanceBase != null && _slsChancePerLevel != null)
                return _slsChanceBase.Value + _slsChancePerLevel.Value * level;
            return LevelFactor(level);
        }

        // CharacterDrop.Drop.m_levelMultiplierNotAmount (Valheim 1.0.20+): the star level raises only the chance. Read by
        // name, so the mod still loads on an older game.
        private static readonly System.Reflection.FieldInfo ChanceOnlyField =
            AccessTools.Field(typeof(CharacterDrop.Drop), "m_levelMultiplierNotAmount");

        private static bool ChanceOnly(CharacterDrop.Drop entry) =>
            ChanceOnlyField != null && (bool)ChanceOnlyField.GetValue(entry);

        private static void LogRoll(string message)
        {
            if (_logTrophyRolls.Value) _log.LogInfo("Trophy roll: " + message);
        }
    }
}
