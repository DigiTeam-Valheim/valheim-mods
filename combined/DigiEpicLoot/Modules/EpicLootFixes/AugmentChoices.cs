// Epic Loot Fixes, augment choices: augmenting rolls new choices from every effect the slot may have, the replaced one
// included (so a value can be rerolled). For an effect without a value (Glowing, Auto Mead, ...), or with only one
// possible value at this rarity, that choice is the same as keeping it, so it wastes a slot. While the choices are
// rolled, that effect is taken out of the pool; another effect is offered instead. Effects with a range still reroll.
// Light: only runs when the augment choices are rolled.
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using EpicLoot;
using HarmonyLib;
using UnityEngine;

namespace EpicLootFixes
{
    internal static class AugmentChoices
    {
        private static ConfigEntry<bool> _enabled;
        [ThreadStatic] private static string _skip;

        internal static void Bind(DigiCore.ModuleConfig config)
        {
            _enabled = config.Bind("Augment", "No identical augment choice", true,
                "Augmenting an effect without a value (Glowing, Auto Mead, ...) or with one fixed value no longer offers that " +
                "same effect again as a new choice (it would change nothing); another effect is offered instead. Effects " +
                "with a range of values can still be rerolled.");
        }

        [HarmonyPatch(typeof(LootRoller), nameof(LootRoller.RollAugmentEffects))]
        [HarmonyPrefix]
        private static void BeforeRoll(MagicItem magicItem, int effectIndex)
        {
            _skip = null;
            if (!_enabled.Value || magicItem?.Effects == null || effectIndex < 0 || effectIndex >= magicItem.Effects.Count) return;
            var current = magicItem.Effects[effectIndex];
            if (current != null && Unchangeable(current, magicItem.Rarity)) _skip = current.EffectType;
        }

        [HarmonyPatch(typeof(LootRoller), nameof(LootRoller.RollAugmentEffects))]
        [HarmonyFinalizer]
        private static Exception AfterRoll(Exception __exception)
        {
            _skip = null;
            return __exception;
        }

        [HarmonyPatch(typeof(MagicItemEffectDefinitions), nameof(MagicItemEffectDefinitions.GetAvailableEffects))]
        [HarmonyPostfix]
        private static void LeaveOut(List<MagicItemEffectDefinition> __result)
        {
            if (_skip != null) __result?.RemoveAll(x => x != null && x.Type == _skip);
        }

        // No value at this rarity, or a single one that the effect already has: rolling it again changes nothing.
        private static bool Unchangeable(MagicItemEffect effect, ItemRarity rarity)
        {
            if (MagicItemEffectDefinitions.IsValuelessEffect(effect.EffectType)) return true;
            var definition = MagicItemEffectDefinitions.Get(effect.EffectType);
            if (definition?.ValuesPerRarity == null) return false;
            var values = definition.GetValuesForRarity(rarity);
            return values == null || (Mathf.Approximately(values.MinValue, values.MaxValue) && Mathf.Approximately(effect.EffectValue, values.MinValue));
        }
    }
}
