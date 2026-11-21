// Small fixes for Epic Loot (0.15.1), each switchable:
// - Lucky Loot message: the Lucky Loot shard multiplies a creature's drops now and then, but only says so in the log.
//   The game that rolls it tells every player within the shard's range (100 m), who then see one short line in the top
//   left corner: "Lucky Loot 10x: Boar (+3 magic rolls)" (the big message in the middle was the default before).
// - Lucky Loot on birds: Epic Loot only rolls Lucky Loot for creatures; birds (crows, seagulls) drop their feathers
//   like a destroyed object, so they never got it. Now they roll with the same chance and multiplier.
// - Lucky Loot and trophies: Epic Loot never multiplies trophies. "SecondChance" (default): a lucky kill that did not
//   drop its trophy rolls it once more at its chance x the Lucky multiplier (at most 100%), and a lucky kill's trophies
//   count double ("Trophy amount on lucky kills"); the chance and amount are the ones the creature really has (stars,
//   Boss Stars After Defeat's per-star chance and amount); bosses' trophies are left alone. "Multiply": x the Lucky
//   multiplier like the other loot, never a boss's trophy.
// - Augment choices: no new choice that is the same as keeping the effect: see AugmentChoices.cs.
// - Reduce Eitr Cost: staff tooltips and the cast check know about the shard: see EitrCost.cs.
// - Darting Thoughts limit: its max eitr penalty stops at a set limit: see DartingThoughtsCap.cs.
// - Sort effects: tooltips show the magic effects by stars, then by name: see SortedEffects.cs.
// - Extra stars: Double Magic Shot (and any effect set) shows at least 2 stars: see ExtraStars.cs.
// - Sockets on worn gear: the socket shortcut also works in CDEV Equipment Slots' panel: see WornSockets.cs.
// Epic Loot 0.15 fixed two things this did before itself: the eitr cost in enchanted staffs' tooltips, and skipping
// chests whose contents did not change when they load.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace EpicLootFixes
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.epiclootfixes";
        public const string Name = "Digi's Epic Loot Fixes";
        public const string Version = "1.4.1";
        private const string EpicLootGuid = "randyknapp.mods.epicloot";

        public enum TrophyRule { Never, SecondChance, Multiply }

        private static ConfigEntry<bool> _luckyMessage, _luckyBirds;
        private static ConfigEntry<TrophyRule> _luckyTrophies;
        private static ConfigEntry<int> _luckyTrophyAmount;
        internal static BepInEx.Logging.ManualLogSource Log;
        private static ConfigEntry<MessageHud.MessageType> _luckyPosition;

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
            _luckyMessage = Config.Bind("Lucky Loot", "Show Lucky Loot message", true,
                "Show a short line when the Lucky Loot shard multiplies a creature's drops (e.g. \"Lucky Loot 10x: Boar (+3 magic rolls)\").");
            _luckyPosition = Config.Bind("Lucky Loot", "Message position", MessageHud.MessageType.TopLeft,
                "TopLeft: small line in the top left corner, like picked-up items. Center: big message in the middle of the screen.");
            // The big message in the middle was the default before and filled the screen on every lucky kill: once, to the corner.
            if (FirstTime("lucky message top left") && _luckyPosition.Value == MessageHud.MessageType.Center)
                _luckyPosition.Value = MessageHud.MessageType.TopLeft;
            _luckyBirds = Config.Bind("Lucky Loot", "Lucky Loot on birds", true,
                "Birds (crows, seagulls) can get Lucky Loot too, with the same chance and drop multiplier as creatures. " +
                "(Epic Loot itself only does creatures.)");
            _luckyTrophies = Config.Bind("Lucky Loot", "Trophies", TrophyRule.SecondChance,
                "What Lucky Loot does with trophies. Never: Epic Loot's own rule, trophies are never multiplied. " +
                "SecondChance: a lucky kill that did not drop its trophy rolls it once more, at its drop chance times the " +
                "Lucky multiplier (at most 100%), and a lucky kill's trophies are multiplied by the setting below (2 = double); " +
                "a boss's trophy (always dropped) stays as it is. The chance and amount include stars and Boss Stars After " +
                "Defeat (Combat and Loot). Multiply: trophies are multiplied by the Lucky multiplier like the other loot, " +
                "except a boss's trophy. Decided by the game that rolls the drops, like Lucky Loot itself.");
            _luckyTrophyAmount = Config.Bind("Lucky Loot", "Trophy amount on lucky kills (x)", 2,
                new ConfigDescription("SecondChance only: a lucky kill's trophies (dropped normally or by the second roll) are " +
                                      "multiplied by this. 1 = the normal amount. Never a boss's trophy.",
                                      new AcceptableValueRange<int>(1, 10)));
            Log = Logger;
            DamageStacking.Bind(Config);
            SummonLimit.Bind(Config);
            AugmentChoices.Bind(Config);
            EitrCost.Bind(Config);
            DartingThoughtsCap.Bind(Config);
            SortedEffects.Bind(Config);
            ExtraStars.Bind(Config);
            LuckyTotal.Bind(Config);
            WornSockets.Bind(Config);
            var harmony = PatchModule(typeof(Plugin));
            PatchModule(typeof(DamageStacking));
            PatchModule(typeof(SummonLimit));
            PatchModule(typeof(AugmentChoices));
            PatchModule(typeof(EitrCost));
            DartingThoughtsCap.Patch(harmony, Logger);
            SortedEffects.Patch(harmony, Logger);
            ExtraStars.Patch(harmony, Logger);
            LuckyTotal.Patch(harmony, Logger);
            WornSockets.Patch(harmony, Logger);
            DamageStacking.Start();
            if (LuckyPostfix != null && PendingOwner != null && PendingRolls != null && PendingStamp != null)
            {
                harmony.Patch(LuckyPostfix, new HarmonyMethod(typeof(Plugin), nameof(BeforeLucky)), new HarmonyMethod(typeof(Plugin), nameof(AfterLucky)));
                // Below Priority.Last (0): after Epic Loot's Lucky Loot and Boss Stars After Defeat's trophy top-up.
                harmony.Patch(AccessTools.Method(typeof(CharacterDrop), "GenerateDropList"),
                    postfix: new HarmonyMethod(typeof(Plugin), nameof(LateTrophies)) { priority = -100 });
            }
            else
                Logger.LogWarning("Lucky Loot message and trophies are off: this Epic Loot version has no Lucky Loot shard code we know.");
            RegisterRpc(); // switched on in a running game (at the start there is no game yet: done in Game.Start)
        }

        private void OnDestroy() => DamageStacking.Stop();

        // --- Lucky Loot message ----------------------------------------------------------------------------------

        private const string LuckyRpc = "Digi_LuckyLoot";
        private const float LuckyRange = 100f; // Epic Loot's own range for the shard
        private static readonly Type Lucky = AccessTools.TypeByName("EpicLoot.MagicItemEffects.Shards.LuckyLoot");
        private static readonly System.Reflection.MethodInfo LuckyPostfix =
            AccessTools.Method(AccessTools.TypeByName("EpicLoot.MagicItemEffects.Shards.LuckyLoot+LuckyLoot_CharacterDrop_GenerateDropList_Patch"), "Postfix");
        private static readonly System.Reflection.FieldInfo PendingOwner = Lucky == null ? null : AccessTools.Field(Lucky, "_pendingOwner");
        private static readonly System.Reflection.FieldInfo PendingRolls = Lucky == null ? null : AccessTools.Field(Lucky, "_pendingRolls");
        private static readonly System.Reflection.FieldInfo PendingStamp = Lucky == null ? null : AccessTools.Field(Lucky, "_pendingStamp");
        private static readonly System.Reflection.MethodInfo BestNearbyValue = Lucky == null ? null : AccessTools.Method(Lucky, "BestNearbyValue");
        private static readonly System.Reflection.MethodInfo Multiplier = Lucky == null ? null : AccessTools.Method(Lucky, "Multiplier");

        private static readonly AccessTools.FieldRef<CharacterDrop, Character> CreatureOf = AccessTools.FieldRefAccess<CharacterDrop, Character>("m_character");

        private sealed class LuckyBefore
        {
            public object Owner;
            public float Stamp;
        }

        // Epic Loot's own Lucky Loot postfix on CharacterDrop.GenerateDropList: a proc leaves this creature as "pending".
        private static void BeforeLucky(out LuckyBefore __state)
        {
            __state = new LuckyBefore { Owner = PendingOwner.GetValue(null), Stamp = (float)PendingStamp.GetValue(null) };
        }

        private static void AfterLucky(CharacterDrop __0, LuckyBefore __state)
        {
            if (__0 == null || CreatureOf(__0) == null) return;
            var owner = PendingOwner.GetValue(null) as CharacterDrop;
            var stamp = (float)PendingStamp.GetValue(null);
            if (owner != __0 || (ReferenceEquals(__state.Owner, owner) && stamp == __state.Stamp)) return; // no new proc
            var creature = CreatureOf(__0);
            var position = creature.transform.position;
            var multiplier = 0;
            if (BestNearbyValue != null && Multiplier != null)
                multiplier = (int)Multiplier.Invoke(null, new object[] { (float)BestNearbyValue.Invoke(null, new object[] { position }) });
            if (multiplier > 1)
            {
                _luckyDrop = __0;   // the trophies are done in LateTrophies, after every other drop-list postfix
                _luckyMultiplier = multiplier;
            }
            SendLucky(position, creature.m_name ?? "", multiplier, (int)PendingRolls.GetValue(null));
        }

        // --- Lucky Loot and trophies -----------------------------------------------------------------------------
        // Runs last on CharacterDrop.GenerateDropList, so the list already holds the game's roll and Boss Stars After
        // Defeat's (Combat and Loot) per-star trophy top-up. The second roll uses the trophy's real chance (the game's,
        // raised per star, or Boss Stars' per-star chance, whichever is higher) and gives the amount the creature would
        // have dropped (more per star when that applies), so a lucky kill never gives fewer trophies than a normal one.

        private static CharacterDrop _luckyDrop;
        private static int _luckyMultiplier;

        private static void LateTrophies(CharacterDrop __instance, List<KeyValuePair<GameObject, int>> __result)
        {
            if (_luckyDrop == null || _luckyDrop != __instance) return;
            var multiplier = _luckyMultiplier;
            _luckyDrop = null;
            var creature = CreatureOf(__instance);
            if (__result == null || creature == null || __instance.m_drops == null) return;
            var rule = _luckyTrophies.Value;
            if (rule == TrophyRule.Never) return;
            var level = Mathf.Max(1, creature.GetLevel());
            foreach (var drop in __instance.m_drops)
            {
                if (drop == null || drop.m_prefab == null || drop.m_onePerPlayer || !IsTrophy(drop.m_prefab)) continue;
                var prefab = drop.m_prefab;
                var at = __result.FindIndex(p => p.Key == prefab);
                if (rule == TrophyRule.Multiply)
                {
                    if (at < 0 || creature.IsBoss()) continue;
                    __result[at] = new KeyValuePair<GameObject, int>(prefab, Mathf.Min(100, __result[at].Value * multiplier));
                    Log?.LogInfo($"Lucky Loot: {creature.m_name} trophy x{multiplier}.");
                    continue;
                }
                if (creature.IsBoss()) continue; // a boss's trophy always drops: nothing to roll, not doubled either
                var how = "dropped anyway";
                if (at < 0)
                {
                    var chance = drop.m_levelMultiplier ? drop.m_chance * ChanceFactor(level) : drop.m_chance;
                    chance = Mathf.Max(chance, BossStarsChance(level - 1));
                    if (chance <= 0f || chance >= 1f) continue; // never drops (or would have dropped)
                    var second = Mathf.Min(1f, chance * multiplier);
                    if (UnityEngine.Random.value >= second) continue;
                    var amount = UnityEngine.Random.Range(drop.m_amountMin, drop.m_amountMax + 1);
                    if ((drop.m_levelMultiplier || SlsBool("ScaleAllLootByLevel")) && !drop.m_levelMultiplierNotAmount)
                        amount *= Mathf.RoundToInt(AmountFactor(level));
                    __result.Add(new KeyValuePair<GameObject, int>(prefab, Mathf.Clamp(amount, 1, 100)));
                    at = __result.Count - 1;
                    how = $"second chance {chance * 100f:0.#}% x {multiplier} = {second * 100f:0}%";
                }
                var times = Mathf.Clamp(_luckyTrophyAmount.Value, 1, 10);
                if (times > 1) __result[at] = new KeyValuePair<GameObject, int>(prefab, Mathf.Min(100, __result[at].Value * times));
                Log?.LogInfo($"Lucky Loot second chance: {creature.m_name} dropped {__result[at].Value} trophy ({how}, x{times}).");
            }
        }

        private static bool IsTrophy(GameObject prefab)
        {
            var item = prefab.GetComponent<ItemDrop>();
            return item != null && item.m_itemData?.m_shared != null && item.m_itemData.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Trophy;
        }

        // Boss Stars After Defeat's trophy chance for a normal creature with this many stars, while its drop-list postfix
        // is attached (the sub-mod is on); 0 otherwise. Its table is "Trophy chance per star (%)", the last value repeats.
        private static readonly Type BossStars = AccessTools.TypeByName("BossStarsAfterDefeat.Plugin");
        private static readonly System.Reflection.FieldInfo BossStarsTable = BossStars == null ? null : AccessTools.Field(BossStars, "_trophyChanceTable");

        private static float BossStarsChance(int stars)
        {
            if (BossStarsTable == null || !(BossStarsTable.GetValue(null) is float[] table) || table.Length == 0) return 0f;
            var info = Harmony.GetPatchInfo(AccessTools.Method(typeof(CharacterDrop), "GenerateDropList"));
            if (info == null || !info.Postfixes.Any(p => p.PatchMethod?.DeclaringType == BossStars)) return 0f;
            return table[Mathf.Clamp(stars, 0, table.Length - 1)] / 100f;
        }

        // Star Level System's loot settings (it replaces the game's x2 per star): read by name, like Boss Stars does.
        private static readonly Type SlsConfig = AccessTools.TypeByName("StarLevelSystem.common.ValConfig");

        private static object SlsValue(string name) =>
            SlsConfig == null ? null : (AccessTools.Field(SlsConfig, name)?.GetValue(null) as ConfigEntryBase)?.BoxedValue;

        private static bool SlsBool(string name) => SlsValue(name) is bool b && b;

        // What a level-multiplied drop's amount gets at this level: the game doubles it per star; Star Level System
        // PerLevel (and ChancePerLevel) = level x its loot scale, Exponential = (1 + scale)^(level - 1).
        private static float AmountFactor(int level)
        {
            if (level <= 1) return 1f;
            if (!(SlsValue("LootDropCalculationType") is string style) || !(SlsValue("PerLevelLootScale") is float scale))
                return Mathf.Pow(2f, level - 1);
            var factor = style == "Exponential" ? Mathf.Pow(1f + scale, level - 1) : level * scale;
            return Mathf.Max(1f, Mathf.Round(factor));
        }

        // What a level-multiplied drop's chance gets at this level (Star Level System's ChancePerLevel has its own rule).
        private static float ChanceFactor(int level)
        {
            if (SlsValue("LootDropCalculationType") is string style && style == "ChancePerLevel" &&
                SlsValue("ChanceBaseChancePerLevel") is float chanceBase && SlsValue("PerLevelLootChanceScale") is float perLevel)
                return chanceBase + perLevel * level;
            return AmountFactor(level);
        }

        // Tells every game a Lucky Loot proc happened here; players within the shard's range see the message.
        private static void SendLucky(Vector3 position, string name, int multiplier, int rolls)
        {
            if (ZRoutedRpc.instance == null) return;
            var package = new ZPackage();
            package.Write(position);
            package.Write(name);
            package.Write(multiplier);
            package.Write(rolls);
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, LuckyRpc, package);
        }

        // --- Lucky Loot on birds ---------------------------------------------------------------------------------

        private static int _birdMultiplier;

        // A bird (crow, seagull) drops its feathers here. Roll Lucky Loot like Epic Loot does for creatures: chance =
        // the best Lucky Loot value of the players within 100 m, in percent; the drops are multiplied by its multiplier.
        [HarmonyPatch(typeof(DropOnDestroyed), "OnDestroyed")]
        [HarmonyPrefix]
        private static void BirdDropsBefore(DropOnDestroyed __instance)
        {
            _birdMultiplier = 0;
            if (!_luckyBirds.Value || BestNearbyValue == null || Multiplier == null) return;
            if (__instance.GetComponentInParent<RandomFlyingBird>() == null && __instance.GetComponentInChildren<RandomFlyingBird>() == null) return;
            var position = __instance.transform.position;
            var value = (float)BestNearbyValue.Invoke(null, new object[] { position });
            if (value <= 0f || UnityEngine.Random.value >= value * 0.01f) return;
            var multiplier = (int)Multiplier.Invoke(null, new object[] { value });
            if (multiplier <= 1) return;
            _birdMultiplier = multiplier;
            var name = Utils.GetPrefabName(__instance.gameObject);
            if (name == "Seagal") name = "Seagull";
            Log?.LogInfo($"Lucky Loot proc on bird {name}: {multiplier}x drops (value {value}).");
            SendLucky(position, name, multiplier, 0);
        }

        [HarmonyPatch(typeof(DropOnDestroyed), "OnDestroyed")]
        [HarmonyFinalizer]
        private static Exception BirdDropsAfter(Exception __exception)
        {
            _birdMultiplier = 0;
            return __exception;
        }

        // While a lucky bird drops: every drop is there multiplier times (at most 100 of one item, like Epic Loot).
        [HarmonyPatch(typeof(DropTable), "GetDropList", new[] { typeof(int) })] // the inner one: too big to be inlined
        [HarmonyPostfix]
        private static void MultiplyBirdDrops(ref List<GameObject> __result)
        {
            if (_birdMultiplier <= 1 || __result == null || __result.Count == 0) return;
            var once = new List<GameObject>(__result);
            var copies = Mathf.Min(_birdMultiplier, Mathf.Max(1, 100 / once.Count));
            for (var i = 1; i < copies; i++) __result.AddRange(once);
            _birdMultiplier = 0; // only the bird's own drop list
        }

        // At the game's start; and when this sub-mod is switched on (or loaded again) in a running game, where the name may
        // still be held by an earlier copy's handler: taken over, so this one answers.
        [HarmonyPatch(typeof(Game), "Start")]
        [HarmonyPostfix]
        private static void RegisterRpc()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null) return;
            (AccessTools.Field(typeof(ZRoutedRpc), "m_functions")?.GetValue(rpc) as IDictionary)?.Remove(LuckyRpc.GetStableHashCode());
            rpc.Register<ZPackage>(LuckyRpc, OnLucky);
        }

        private static void OnLucky(long sender, ZPackage package)
        {
            var position = package.ReadVector3();
            var name = package.ReadString();
            var multiplier = package.ReadInt();
            var rolls = package.ReadInt();
            var player = Player.m_localPlayer;
            if (!_luckyMessage.Value || player == null || Vector3.Distance(player.transform.position, position) > LuckyRange) return;
            var creature = Localization.instance != null ? Localization.instance.Localize(name) : name;
            // One short line: "Lucky Loot 10x: Boar (+3 magic rolls)".
            var text = "Lucky Loot" + (multiplier > 1 ? $" {multiplier}x" : "") + ": " + creature +
                       (rolls > 0 ? $" (+{rolls} magic roll{(rolls == 1 ? "" : "s")})" : "");
            player.Message(_luckyPosition.Value, text);
        }
    }
}
