// Keeps the Rested buff when you log out: the game saves your food but not your status effects, so Rested was gone
// after every relog. When the game saves your character, the time left on Rested (and its comfort level) goes into the
// character's own save data; the next time you log in, Rested comes back with exactly that time left. After dying you
// lose it as normal (only the first spawn after logging in restores it).
// Light: nothing runs while you play; only when the character is saved and when you log in.
// Also: resting turns into Rested faster (RestSpeed.cs).
using System.Globalization;
using HarmonyLib;

namespace KeepRested
{
    public class Plugin : DigiCore.DigiModule
    {
        private const string Key = "digi_keeprested"; // "<time left>;<duration>;<comfort level>"

        private static readonly AccessTools.FieldRef<Game, bool> FirstSpawn = AccessTools.FieldRefAccess<Game, bool>("m_firstSpawn");
        private static readonly AccessTools.FieldRef<Player, int> ComfortLevel = AccessTools.FieldRefAccess<Player, int>("m_comfortLevel");
        private static readonly AccessTools.FieldRef<StatusEffect, float> EffectTime = AccessTools.FieldRefAccess<StatusEffect, float>("m_time");

        private void Awake()
        {
            RestSpeed.Bind(Config);
            PatchModule(typeof(Plugin));
            PatchModule(typeof(RestSpeed));
        }

        // Saving the character: remember Rested (or forget it when it is gone).
        [HarmonyPatch(typeof(Player), nameof(Player.Save))]
        [HarmonyPrefix]
        private static void Save(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            var rested = __instance.GetSEMan().GetStatusEffect(SEMan.s_statusEffectRested);
            var left = rested != null ? rested.GetRemaningTime() : 0f;
            if (left > 1f && !__instance.IsDead())
                __instance.m_customData[Key] = string.Join(";",
                    left.ToString("0.#", CultureInfo.InvariantCulture),
                    rested.m_ttl.ToString("0.#", CultureInfo.InvariantCulture),
                    __instance.GetComfortLevel().ToString(CultureInfo.InvariantCulture));
            else
                __instance.m_customData.Remove(Key);
        }

        // Logging in: the game has just loaded the character (with our note) and spawned it.
        [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        [HarmonyPostfix]
        private static void Spawned(Player __instance)
        {
            if (__instance != Player.m_localPlayer || Game.instance == null || !__instance.m_customData.TryGetValue(Key, out var note)) return;
            __instance.m_customData.Remove(Key);
            if (!FirstSpawn(Game.instance)) return; // a respawn after dying: Rested is lost, as normal
            var parts = note.Split(';');
            if (parts.Length < 3 ||
                !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var left) ||
                !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) ||
                !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var comfort) ||
                left <= 0f) return;
            // The game shows "Rested (Comfort: x)" and sets the duration from the current comfort level, which is not
            // known yet right after spawning: use the saved one, then put the time left back.
            var current = ComfortLevel(__instance);
            ComfortLevel(__instance) = comfort;
            var se = __instance.GetSEMan().AddStatusEffect(SEMan.s_statusEffectRested, true);
            ComfortLevel(__instance) = current;
            if (se == null) return;
            se.m_ttl = System.Math.Max(duration, left);
            EffectTime(se) = se.m_ttl - left;
        }
    }
}
