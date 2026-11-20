// Forsaken powers that are always on. The power you chose at a boss stone (or every power you have unlocked) works as
// a passive effect without a time limit, instead of a power you activate for a few minutes with a long cooldown. The
// host decides which, for everyone.
// The powers stay the game's own status effects; only the copy on your character is made endless. The power key does
// nothing while this runs (activating would hand the power to players nearby too). Switching it off removes the endless
// powers, and the key works as before.
// Light: per frame only the HUD's power slot is touched (two values). The powers are checked when you spawn, choose a
// power at a stone, change a setting, and every few seconds (a boss kill or a trophy can unlock one).
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace PassivePowers
{
    public class Plugin : DigiCore.DigiModule
    {
        public enum PowerChoice { Selected, AllUnlocked }
        public enum UnlockSource { ActivatedStones, DefeatedBosses, PickedUpTrophies }

        private const string PowerPrefix = "GP_";
        private const float CheckInterval = 5f;

        // The game's powers with their boss trophy and the world key the boss kill sets (for the other unlock sources).
        private static readonly Dictionary<string, string[]> Bosses = new Dictionary<string, string[]>
        {
            { "GP_Eikthyr", new[] { "TrophyEikthyr", "defeated_eikthyr" } },
            { "GP_TheElder", new[] { "TrophyTheElder", "defeated_gdking" } },
            { "GP_Bonemass", new[] { "TrophyBonemass", "defeated_bonemass" } },
            { "GP_Moder", new[] { "TrophyDragonQueen", "defeated_dragon" } },
            { "GP_Yagluth", new[] { "TrophyGoblinKing", "defeated_goblinking" } },
            { "GP_Queen", new[] { "TrophySeekerQueen", "defeated_queen" } },
            { "GP_Fader", new[] { "TrophyFader", "defeated_fader" } },
        };

        private static ConfigEntry<PowerChoice> _powers;
        private static ConfigEntry<UnlockSource> _unlockedBy;
        private static ConfigEntry<bool> _showOnHud, _messages;
        private static bool _adding; // Refresh is giving a power: the game's "gained" message stays off
        private static bool _running;

        // The power status effects of the current ObjectDB (a new one is made for every world you load).
        private static ObjectDB _powersOf;
        private static readonly List<StatusEffect> AllPowers = new List<StatusEffect>();
        private static readonly HashSet<int> Wanted = new HashSet<int>();
        private static readonly List<int> ToRemove = new List<int>();
        private float _timer;

        private void Awake()
        {
            var admin = new ConfigurationManagerAttributes { IsAdminOnly = true };
            _powers = Config.Bind("General", "Powers", PowerChoice.Selected, new ConfigDescription(
                "Selected: the power you chose at a boss stone is always on. AllUnlocked: every power you have unlocked " +
                "is always on (see Unlocked by). Set by the host, the same for everyone.", null, admin));
            _unlockedBy = Config.Bind("General", "Unlocked by", UnlockSource.ActivatedStones, new ConfigDescription(
                "With AllUnlocked, which powers count. ActivatedStones: the boss stones this character has used. " +
                "DefeatedBosses: the bosses defeated in this world. PickedUpTrophies: the boss trophies this character " +
                "has picked up. Set by the host, the same for everyone.", null, admin));
            _showOnHud = Config.Bind("General", "Show power icon", false,
                "With Selected: keep showing the chosen power in its slot at the bottom left, always ready. Off = the " +
                "slot stays empty, as with AllUnlocked.");
            _messages = Config.Bind("General", "Show power messages", false,
                "The game's messages when a power is taken away (at death) and given back (when you respawn or log in). " +
                "Off = no messages; the powers work the same.");
            _powers.SettingChanged += OnSettingChanged;
            _unlockedBy.SettingChanged += OnSettingChanged;
            _running = true;
            PatchModule(typeof(Plugin));
            Refresh();
        }

        private void Update()
        {
            if (Player.m_localPlayer == null) return;
            _timer -= Time.deltaTime;
            if (_timer > 0f) return;
            _timer = CheckInterval;
            Refresh();
        }

        private static void OnSettingChanged(object sender, EventArgs e) => Refresh();

        private void OnDestroy()
        {
            _running = false;
            _powers.SettingChanged -= OnSettingChanged;
            _unlockedBy.SettingChanged -= OnSettingChanged;
            var player = Player.m_localPlayer;
            if (player == null) return;
            ToRemove.Clear();
            foreach (var se in player.GetSEMan().GetStatusEffects())
                if (IsEndlessPower(se)) ToRemove.Add(se.NameHash());
            foreach (var hash in ToRemove) player.GetSEMan().RemoveStatusEffect(hash, true);
        }

        // Gives the local player exactly the powers that should be on, endless.
        internal static void Refresh()
        {
            var player = Player.m_localPlayer;
            if (!_running || player == null || ObjectDB.instance == null) return;
            CollectWanted(player);
            var seman = player.GetSEMan();
            ToRemove.Clear();
            foreach (var se in seman.GetStatusEffects())
                if (IsEndlessPower(se) && !Wanted.Contains(se.NameHash())) ToRemove.Add(se.NameHash());
            foreach (var hash in ToRemove) seman.RemoveStatusEffect(hash, true);
            foreach (var hash in Wanted)
            {
                var se = seman.GetStatusEffect(hash);
                if (se == null)
                {
                    _adding = true;
                    try { se = seman.AddStatusEffect(hash); }
                    finally { _adding = false; }
                }
                if (se != null) se.m_ttl = 0f; // 0 = no time limit
            }
        }

        private static bool IsEndlessPower(StatusEffect se) =>
            se != null && se.m_ttl <= 0f && se.name.StartsWith(PowerPrefix, StringComparison.Ordinal);

        private static void CollectWanted(Player player)
        {
            Wanted.Clear();
            var selected = player.GetGuardianPowerName();
            if (!string.IsNullOrEmpty(selected)) Wanted.Add(selected.GetStableHashCode());
            if (_powers.Value != PowerChoice.AllUnlocked) return;
            UpdatePowerList();
            List<string> trophies = null;
            foreach (var power in AllPowers)
                if (IsUnlocked(player, power.name, ref trophies)) Wanted.Add(power.NameHash());
        }

        private static void UpdatePowerList()
        {
            if (_powersOf == ObjectDB.instance) return;
            _powersOf = ObjectDB.instance;
            AllPowers.Clear();
            foreach (var se in _powersOf.m_StatusEffects)
                if (se != null && se.name.StartsWith(PowerPrefix, StringComparison.Ordinal)) AllPowers.Add(se);
        }

        private static bool IsUnlocked(Player player, string power, ref List<string> trophies)
        {
            switch (_unlockedBy.Value)
            {
                case UnlockSource.DefeatedBosses:
                    return Bosses.TryGetValue(power, out var boss) && ZoneSystem.instance != null &&
                           ZoneSystem.instance.GetGlobalKey(boss[1]);
                case UnlockSource.PickedUpTrophies:
                    if (!Bosses.TryGetValue(power, out var trophy)) return false;
                    if (trophies == null) trophies = player.GetTrophies();
                    return trophies.Contains(trophy[0]);
                default: // the game remembers every stone a character has used
                    return player.HaveUniqueKey(power);
            }
        }

        // No "gained" message when this mod gives a power, and no "released" one when an endless power ends (death).
        [HarmonyPatch(typeof(StatusEffect), nameof(StatusEffect.Setup))]
        [HarmonyPrefix]
        private static void QuietStart(StatusEffect __instance)
        {
            if (_adding && !_messages.Value) __instance.m_startMessage = "";
        }

        [HarmonyPatch(typeof(StatusEffect), nameof(StatusEffect.Stop))]
        [HarmonyPrefix]
        private static void QuietStop(StatusEffect __instance)
        {
            if (!_messages.Value && IsEndlessPower(__instance)) __instance.m_stopMessage = "";
        }

        // A new power chosen at a boss stone applies right away.
        [HarmonyPatch(typeof(ItemStand), "DelayedPowerActivation")]
        [HarmonyPostfix]
        private static void PowerChosen() => Refresh();

        [HarmonyPatch(typeof(Player), "OnSpawned")]
        [HarmonyPostfix]
        private static void Spawned(Player __instance)
        {
            if (__instance == Player.m_localPlayer) Refresh();
        }

        private static bool _told;

        // The power key does nothing: the power is already on.
        [HarmonyPatch(typeof(Player), nameof(Player.StartGuardianPower))]
        [HarmonyPrefix]
        private static bool StartPower(Player __instance, ref bool __result)
        {
            // Said once per game: the key may be shared with another mod's key (the user had J for both this and Set
            // Scanner), and then it showed every time that key was used.
            if (__instance == Player.m_localPlayer && !_told)
            {
                _told = true;
                __instance.Message(MessageHud.MessageType.TopLeft, "Your forsaken power is always on.");
            }
            __result = false;
            return false;
        }

        [HarmonyPatch(typeof(Player), nameof(Player.ActivateGuardianPower))]
        [HarmonyPrefix]
        private static bool ActivatePower(ref bool __result)
        {
            __result = false;
            return false;
        }

        // The HUD's power slot: the chosen power, always ready (Selected), or empty.
        [HarmonyPatch(typeof(Player), nameof(Player.GetGuardianPowerHUD))]
        [HarmonyPostfix]
        private static void PowerHud(ref StatusEffect se, ref float cooldown)
        {
            if (_powers.Value == PowerChoice.Selected && _showOnHud.Value) cooldown = 0f;
            else se = null;
        }
    }
}
