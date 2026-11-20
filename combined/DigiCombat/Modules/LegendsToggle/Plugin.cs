// A live master switch for Valheim Legends, set by the host and synced to every player through Jotunn: flip it in
// Configuration Manager (F1) and Legends turns on or off for everyone, immediately, without a restart.
// Off means every gameplay hook of Legends is skipped (abilities and their keys, class passives, damage/stamina/carry
// changes, the class altar, the ability bar), and Legends' status effects are removed from your character. What keeps
// your data intact stays active: its skills, saving/loading your class, its prefabs and its config sync. So switching
// back on restores everything as it was.
// Also keeps skills of mods that are not installed (e.g. Legends removed) instead of letting Valheim delete them,
// gives the Enchanter's Zone Charge a buff in the Deep North (Legends has none there), and makes the Monk's Chi Blast
// need the 5 chi its message asks for (Legends checked for 1, and the bolt's own hit gives 1 back, so with any chi it
// could be fired every second, without end).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Utils;
using UnityEngine;

namespace LegendsToggle
{
    [SynchronizationMode(AdminOnlyStrictness.IfOnServer)]
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.legendstoggle";
        public const string Name = "Digi's Legends Toggle";
        public const string Version = "2.0.4";
        private const string LegendsGuid = "ValheimLegends";
        private const string LegendsHarmonyId = "valheim.torann.valheimlegends";

        // Legends hooks that keep data and registration working; they always run.
        private static readonly HashSet<string> AlwaysOn = new HashSet<string>
        {
            "ZNet.Awake", "ZNet.RPC_PeerInfo", "ZNet.OnDestroy", "ZNetScene.Awake", "ObjectDB.Awake", "ObjectDB.CopyOtherDB",
            "ObjectDB.GetStatusEffect", "Hud.Awake", "PlayerProfile.SavePlayerToDisk", "PlayerProfile.LoadPlayerFromDisk",
            "PlayerProfile.LoadPlayerData", "Skills.IsSkillValid", "Skills.GetSkillDef", "Skills.CheatRaiseSkill",
            "Player.RaiseSkill", "Player.OnSpawned", "Player.OnRespawn", "Menu.OnQuit", "Menu.OnLogout", "Terminal.TryRunCommand",
        };

        private static ConfigEntry<bool> _enabled, _deepNorthZone, _chiBlast;
        private static Assembly _legends;
        private static BepInEx.Logging.ManualLogSource _log;
        private float _sweepTimer;

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
            _enabled = Config.Bind("Valheim Legends", "Enabled", true,
                new ConfigDescription("Valheim Legends on or off for everyone, right away (no restart). Only the host can change it. Your class and Legends skills are kept while it is off.",
                    null, new ConfigurationManagerAttributes { IsAdminOnly = true }));
            _enabled.SettingChanged += (_, __) => OnSwitched();
            _deepNorthZone = Config.Bind("Valheim Legends", "Zone Charge in the Deep North", true,
                "The Enchanter's Zone Charge has a buff for every biome but the Deep North: there it only cost stamina and " +
                "started its cooldown. On: in the Deep North it gives the Mountain buff (less frost damage, stamina back " +
                "every 5 seconds), so its shock works there too.");
            _chiBlast = Config.Bind("Valheim Legends", "Chi Blast needs 5 chi", true,
                new ConfigDescription("The Monk's Chi Blast says it costs 5 chi, but Legends only checked for 1, and the bolt's hit " +
                    "gives 1 chi back: with any chi it could be fired every second without end. On: it needs 5 chi. It still uses " +
                    "all your chi, and its damage still grows with it. Surge (blocking + the same key) still needs 1. Only the host can change it.",
                    null, new ConfigurationManagerAttributes { IsAdminOnly = true }));

            var harmony = ModuleHarmony;
            harmony.PatchAll(typeof(KeepSkills));
            if (Chainloader.PluginInfos.TryGetValue(LegendsGuid, out var info) && info.Instance != null)
            {
                _legends = info.Instance.GetType().Assembly;
                GateLegends(harmony);
                GuardDamageHook(harmony);
                DeepNorthZoneCharge(harmony);
                ChiBlastCost(harmony);
                var mountain = AccessTools.Method(_legends.GetType("ValheimLegends.SE_BiomeMountain"), "OnDamaged");
                if (mountain != null) harmony.Patch(mountain, prefix: new HarmonyMethod(typeof(Plugin), nameof(MountainResist)));
            }
            else Logger.LogWarning("Valheim Legends is not installed; the switch does nothing.");
        }

        // Zone Charge picks its buff with Player.GetCurrentBiome(), one check per biome, and the Deep North (newer than
        // the mod) matches none ("Biome invalid." in the log). Its checks go through ZoneBiome instead, which answers
        // Mountain in the Deep North.
        private static int _biomeChecks;

        private void DeepNorthZoneCharge(Harmony harmony)
        {
            var input = AccessTools.Method(_legends.GetType("ValheimLegends.Class_Enchanter"), "Process_Input");
            if (input == null)
            {
                Logger.LogWarning("Legends' Enchanter not found: Zone Charge stays without a buff in the Deep North.");
                return;
            }
            _biomeChecks = 0;
            harmony.Patch(input, transpiler: new HarmonyMethod(typeof(Plugin), nameof(ZoneChargeBiome)));
            Logger.LogInfo($"Zone Charge in the Deep North gives the Mountain buff ({_biomeChecks} biome checks).");
        }

        private static IEnumerable<CodeInstruction> ZoneChargeBiome(IEnumerable<CodeInstruction> instructions)
        {
            var getBiome = AccessTools.Method(typeof(Player), nameof(Player.GetCurrentBiome));
            var zoneBiome = AccessTools.Method(typeof(Plugin), nameof(ZoneBiome));
            foreach (var code in instructions)
            {
                if (code.Calls(getBiome))
                {
                    code.opcode = OpCodes.Call;
                    code.operand = zoneBiome;
                    _biomeChecks++;
                }
                yield return code;
            }
        }

        // Chi Blast (Monk, ability 3 while unarmed): Class_Monk.Process_Input checks "hitCount >= 1" (chi), then fires a bolt
        // that uses all chi (its damage grows with it) and sets it to 0. The bolt's hit counts as an unarmed hit, which
        // gives 1 chi: so it could be fired again a second later, forever. Legends' own message says the cost is
        // VL_Utility.GetPsiBoltCost (5). The same check also lets Surge (blocking + that key) start: Surge keeps 1.
        private static int _chiChecks, _chiCost = 5;

        private void ChiBlastCost(Harmony harmony)
        {
            var input = AccessTools.Method(_legends.GetType("ValheimLegends.Class_Monk"), "Process_Input");
            var cost = AccessTools.Property(_legends.GetType("ValheimLegends.VL_Utility"), "GetPsiBoltCost");
            if (cost != null && cost.GetValue(null) is float c && c >= 1f) _chiCost = Mathf.RoundToInt(c);
            if (input == null)
            {
                Logger.LogWarning("Legends' Monk not found: Chi Blast keeps its check for 1 chi.");
                return;
            }
            _chiChecks = 0;
            harmony.Patch(input, transpiler: new HarmonyMethod(typeof(Plugin), nameof(ChiBlastCheck)));
            if (_chiChecks == 1) Logger.LogInfo($"Chi Blast needs {_chiCost} chi.");
            else Logger.LogWarning($"Chi Blast: Legends' chi check not found ({_chiChecks}); it keeps needing 1 chi.");
        }

        // The first "SE_Monk.hitCount >= 1" of the method: its 1 becomes ChiBlastNeeds().
        private static IEnumerable<CodeInstruction> ChiBlastCheck(IEnumerable<CodeInstruction> instructions)
        {
            var hitCount = AccessTools.Field(_legends.GetType("ValheimLegends.SE_Monk"), "hitCount");
            var needs = AccessTools.Method(typeof(Plugin), nameof(ChiBlastNeeds));
            var codes = instructions.ToList();
            for (var i = 0; i + 1 < codes.Count && _chiChecks == 0; i++)
            {
                if (hitCount == null || !codes[i].LoadsField(hitCount) || codes[i + 1].opcode != OpCodes.Ldc_I4_1) continue;
                codes[i + 1].opcode = OpCodes.Call; // labels stay on the instruction
                codes[i + 1].operand = needs;
                _chiChecks++;
            }
            return codes;
        }

        private static int ChiBlastNeeds()
        {
            var player = Player.m_localPlayer;
            return _chiBlast.Value && player != null && !player.IsBlocking() ? _chiCost : 1;
        }

        // Zone Charge's Mountain buff on the Enchanter (and charmed creatures) sets its frost "resistance" to 1 + 0.1 x
        // Abjuration level, the Meadows heal formula copied: frost damage was multiplied by that (x3 at level 20) instead
        // of lowered. Players nearby get it right (0.8 - 0.002 x their average skill). Above 1 it becomes 0.8, what the
        // Enchanter gets from the other biomes' buffs.
        private static void MountainResist(ref float ___resistModifier)
        {
            if (___resistModifier > 1f) ___resistModifier = 0.8f;
        }

        private static Heightmap.Biome ZoneBiome(Player player)
        {
            var biome = player.GetCurrentBiome();
            return biome == Heightmap.Biome.DeepNorth && _deepNorthZone.Value ? Heightmap.Biome.Mountain : biome;
        }

        // Legends' damage hook reads hit.GetAttacker().IsPlayer() for a creature charmed by the Enchanter without checking
        // for an attacker. Damage without one (Ashlands ground fire, burning) then threw, and the throw cancelled the
        // damage: charmed creatures took no such damage and the log filled up. With no attacker the rest of that hook
        // does nothing, so its right answer is "let the damage through".
        private void GuardDamageHook(Harmony harmony)
        {
            var hook = _legends.GetType("ValheimLegends.ValheimLegends+VL_Damage_Patch");
            var prefix = hook != null ? AccessTools.Method(hook, "Prefix") : null;
            if (prefix == null || prefix.ReturnType != typeof(bool)) return;
            harmony.Patch(prefix, finalizer: new HarmonyMethod(typeof(Plugin), nameof(DamageHookFailed)));
        }

        private static Exception DamageHookFailed(Exception __exception, ref bool __result, ref HitData hit)
        {
            if (!(__exception is NullReferenceException) || hit == null || hit.GetAttacker() != null) return __exception;
            __result = true;
            return null;
        }

        // Puts a gate in front of each gameplay patch method of Legends. Done at startup, before any of them first runs.
        private void GateLegends(Harmony harmony)
        {
            var gateBool = new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(GateBool)));
            var gateVoid = new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(GateVoid)));
            int gated = 0, kept = 0;
            foreach (var original in Harmony.GetAllPatchedMethods().ToList())
            {
                var info = Harmony.GetPatchInfo(original);
                if (info == null) continue;
                var key = original.DeclaringType?.Name + "." + original.Name;
                foreach (var patch in info.Prefixes.Concat(info.Postfixes).Where(p => p.owner == LegendsHarmonyId))
                {
                    if (AlwaysOn.Contains(key)) { kept++; continue; }
                    try
                    {
                        harmony.Patch(patch.PatchMethod, prefix: patch.PatchMethod.ReturnType == typeof(bool) ? gateBool : gateVoid);
                        gated++;
                    }
                    catch (Exception e)
                    {
                        Logger.LogWarning($"Could not gate Legends hook on {key}: {e.Message}");
                    }
                }
            }
            Logger.LogInfo($"Legends switch ready: {gated} gameplay hooks gated, {kept} data hooks always on. Legends is {(_enabled.Value ? "ON" : "OFF")}.");
        }

        // Off: skip the Legends prefix and let the game's own method run.
        private static bool GateBool(ref bool __result)
        {
            if (_enabled.Value) return true;
            __result = true;
            return false;
        }

        private static bool GateVoid() => _enabled.Value;

        private void OnSwitched()
        {
            _log.LogInfo("Valheim Legends switched " + (_enabled.Value ? "ON" : "OFF"));
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Valheim Legends " + (_enabled.Value ? "ON" : "OFF"));
            if (!_enabled.Value) RemoveAbilityBar();
            _sweepTimer = 0f;
        }

        private static void RemoveAbilityBar()
        {
            var field = _legends?.GetType("ValheimLegends.ValheimLegends")?.GetField("abilitiesStatus", BindingFlags.Static | BindingFlags.Public);
            if (!(field?.GetValue(null) is List<RectTransform> bar)) return;
            foreach (var rect in bar)
                if (rect != null) Destroy(rect.gameObject);
            field.SetValue(null, null); // Legends rebuilds it when switched back on
        }

        // While off, clear Legends' status effects (class passives, buffs, cooldowns) from your character.
        private void Update()
        {
            if (_legends == null || _enabled.Value || Player.m_localPlayer == null) return;
            _sweepTimer -= Time.deltaTime;
            if (_sweepTimer > 0f) return;
            _sweepTimer = 0.5f;
            var seman = Player.m_localPlayer.GetSEMan();
            foreach (var se in seman.GetStatusEffects().ToList())
                if (se != null && se.GetType().Assembly == _legends)
                    seman.RemoveStatusEffect(se.NameHash(), true);
        }

        // --- Keep skills of mods that are not installed --------------------------------------------------------------

        private static class KeepSkills
        {
            // Plain class, not a tuple: Valheim's runtime has no System.ValueTuple.
            private sealed class KeptSkill
            {
                public int Type;
                public float Level, Acc;
            }

            private sealed class Kept
            {
                public readonly List<KeptSkill> Skills = new List<KeptSkill>();
            }

            private static readonly ConditionalWeakTable<Skills, Kept> KeptSkills = new ConditionalWeakTable<Skills, Kept>();
            private static readonly Func<Skills.SkillType, bool> IsSkillValid =
                AccessTools.MethodDelegate<Func<Skills.SkillType, bool>>(AccessTools.Method(typeof(Skills), "IsSkillValid"));

            [HarmonyPatch(typeof(Skills), nameof(Skills.Load))]
            [HarmonyPrefix]
            private static void Load(Skills __instance, ZPackage pkg)
            {
                var kept = KeptSkills.GetOrCreateValue(__instance);
                kept.Skills.Clear();
                var start = pkg.GetPos();
                try
                {
                    var version = pkg.ReadInt();
                    var count = pkg.ReadInt();
                    for (var i = 0; i < count; i++)
                    {
                        var type = pkg.ReadInt();
                        var level = pkg.ReadSingle();
                        var acc = version >= 2 ? pkg.ReadSingle() : 0f;
                        if (!IsSkillValid((Skills.SkillType)type)) kept.Skills.Add(new KeptSkill { Type = type, Level = level, Acc = acc });
                    }
                }
                finally
                {
                    pkg.SetPos(start);
                }
            }

            // Vanilla Save plus the kept skills (same format; skipped if the skill is known again).
            [HarmonyPatch(typeof(Skills), nameof(Skills.Save))]
            [HarmonyPrefix]
            private static bool Save(Skills __instance, ZPackage pkg, Dictionary<Skills.SkillType, Skills.Skill> ___m_skillData)
            {
                if (!KeptSkills.TryGetValue(__instance, out var kept) || kept.Skills.Count == 0) return true;
                var extra = kept.Skills.FindAll(s => !___m_skillData.ContainsKey((Skills.SkillType)s.Type));
                pkg.Write(2);
                pkg.Write(___m_skillData.Count + extra.Count);
                foreach (var skill in ___m_skillData.Values)
                {
                    pkg.Write((int)skill.m_info.m_skill);
                    pkg.Write(skill.m_level);
                    pkg.Write(skill.m_accumulator);
                }
                foreach (var s in extra)
                {
                    pkg.Write(s.Type);
                    pkg.Write(s.Level);
                    pkg.Write(s.Acc);
                }
                return false;
            }
        }
    }
}
