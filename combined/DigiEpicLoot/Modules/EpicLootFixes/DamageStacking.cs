// Epic Loot Fixes, damage stacking: Epic Loot lets only one damage enchantment on an item: Fire, Frost, Lightning,
// Poison, Spirit, Blunt, Slash, Pierce, +% damage, +% physical, +% elemental and low-health damage all exclude each
// other. On: they can all roll together, even the same one twice. More damage lines then compete for every slot, so
// Double Magic Shot (staffs) and Triple Shot (bows, crossbows) get twice their selection weight, which keeps their
// odds where they were. Set by the host, the same for everyone.
// Also the cost of the extra shots: when Double Magic Shot (2 projectiles, 66% damage each) or Triple Shot (3 arrows,
// 40% each) fires, Epic Loot charges more eitr or stamina ("CostScale" 2: 2x in total since Epic Loot 0.15, before that
// 2x on top, 3x). With the fix CostScale is 0, which both versions read as no extra, so it costs what a normal shot
// costs, which makes these rare enchantments an upgrade instead of a trade. Triple Shot still uses 3 arrows (Epic Loot
// takes those separately).
// Applied to Epic Loot's loaded effect definitions, and again after every reload (file change, the host's sync); off
// puts the loaded values back. Idempotent, so definitions the host already changed are not changed twice.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using EpicLoot;
using HarmonyLib;
using UnityEngine;

namespace EpicLootFixes
{
    internal static class DamageStacking
    {
        private static readonly HashSet<string> Damage = new HashSet<string>
        {
            "ModifyDamage", "ModifyPhysicalDamage", "ModifyElementalDamage", "ModifyDamageLowHealth",
            "AddBluntDamage", "AddSlashingDamage", "AddPiercingDamage",
            "AddFireDamage", "AddFrostDamage", "AddLightningDamage", "AddPoisonDamage", "AddSpiritDamage",
        };
        private static readonly HashSet<string> Shots = new HashSet<string> { "DoubleMagicShot", "TripleBowShot" };
        private const float ShotWeight = 4f; // twice Epic Loot's 2

        private sealed class Loaded
        {
            public List<string> Exclusive;
            public bool ExclusiveSelf;
            public float Weight;
            public float? CostScale;
        }

        private static ConfigEntry<bool> _stack, _costFix;
        private const float FixedCostScale = 0f; // just the normal cost (0.15: total max(1, x); before: 1 + x)
        private static readonly ConditionalWeakTable<MagicItemEffectDefinition, Loaded> Originals = new ConditionalWeakTable<MagicItemEffectDefinition, Loaded>();

        internal static void Bind(DigiCore.ModuleConfig config)
        {
            _stack = config.Bind("Damage stacking", "Damage effects stack", true, new ConfigDescription(
                "On: a weapon can roll several damage enchantments together (fire and frost and +% damage, even the same " +
                "one twice); Epic Loot normally allows only one. Double Magic Shot and Triple Shot then get twice their " +
                "chance, so their odds stay as before. Off: Epic Loot's own rules. Only changes new rolls (drops, " +
                "enchanting, augmenting). Set by the host, the same for everyone.", null,
                new ConfigurationManagerAttributes { IsAdminOnly = true }));
            _costFix = config.Bind("Double and Triple Shot", "Normal cost", true, new ConfigDescription(
                "When Double Magic Shot (staffs) or Triple Shot (bows, crossbows) fires its extra projectiles, Epic Loot " +
                "charges 2x the eitr or stamina. On: just the normal cost, so these rare enchantments are an upgrade, not a " +
                "trade. Triple Shot still uses 3 arrows. Set by the host, the same for everyone.",
                null, new ConfigurationManagerAttributes { IsAdminOnly = true }));
            _stack.SettingChanged += OnChanged;
            _costFix.SettingChanged += OnChanged;
        }

        internal static void Start() => Apply(_stack.Value, _costFix.Value);

        internal static void Stop()
        {
            _stack.SettingChanged -= OnChanged;
            _costFix.SettingChanged -= OnChanged;
            Apply(false, false);
        }

        private static void OnChanged(object sender, EventArgs e) => Apply(_stack.Value, _costFix.Value);

        // Epic Loot (re)loaded its effects: from its file, or from the host.
        [HarmonyPatch(typeof(MagicItemEffectDefinitions), nameof(MagicItemEffectDefinitions.Initialize))]
        [HarmonyPostfix]
        private static void Reloaded() => Apply(_stack.Value, _costFix.Value);

        private static void Apply(bool on, bool costFix)
        {
            foreach (var def in MagicItemEffectDefinitions.AllDefinitions.Values)
            {
                if (def == null || def.Type == null) continue;
                var damage = Damage.Contains(def.Type);
                if (!damage && !Shots.Contains(def.Type)) continue;
                if (def.Requirements == null) def.Requirements = new MagicItemEffectRequirements();
                var loaded = Originals.GetValue(def, d => new Loaded
                {
                    Exclusive = d.Requirements.ExclusiveEffectTypes != null ? new List<string>(d.Requirements.ExclusiveEffectTypes) : new List<string>(),
                    ExclusiveSelf = d.Requirements.ExclusiveSelf,
                    Weight = d.SelectionWeight,
                    CostScale = d.Config != null && d.Config.TryGetValue("CostScale", out var scale) ? scale : (float?)null,
                });
                if (damage)
                {
                    def.Requirements.ExclusiveEffectTypes = on ? loaded.Exclusive.Where(t => !Damage.Contains(t)).ToList() : new List<string>(loaded.Exclusive);
                    def.Requirements.ExclusiveSelf = !on && loaded.ExclusiveSelf;
                }
                else def.SelectionWeight = on ? Mathf.Max(loaded.Weight, ShotWeight) : loaded.Weight;
                if (Shots.Contains(def.Type))
                {
                    if (def.Config == null) def.Config = new Dictionary<string, float>();
                    if (costFix) def.Config["CostScale"] = FixedCostScale;
                    else if (loaded.CostScale.HasValue) def.Config["CostScale"] = loaded.CostScale.Value;
                    else def.Config.Remove("CostScale");
                }
            }
        }
    }
}
