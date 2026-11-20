// Valheim autosaves the world every 30 minutes, wherever you are, and on a big world that freezes the game for a
// moment. This keeps the interval but waits for a quiet moment to save:
// - right away while a menu, the inventory or the map is open, while sitting or in bed, or after standing still
//   for a few seconds with no creature chasing you;
// - never in the middle of a fight, while running or sailing - but at most a few minutes late, so no progress is
//   ever at risk.
// The host decides when the world is saved; every player's game also autosaves its character on the same timer.
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SmartSave
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.smartsave";
        public const string Name = "Digi's Smart Save";
        public const string Version = "1.0.1";

        private static ConfigEntry<bool> _enabled, _message;
        private static ConfigEntry<int> _interval, _maxDelay;
        private static ConfigEntry<float> _stillSeconds;
        private static float _stillTime;


        private void Awake()
        {
            _enabled = Config.Bind("General", "Wait for a quiet moment", true,
                "When the autosave is due, wait until you are not fighting, running or sailing. Off = save on the dot, like normal.");
            _interval = Config.Bind("General", "Save every (minutes)", 30,
                new ConfigDescription("How often the world is saved. Normal is 30.", new AcceptableValueRange<int>(5, 120)));
            _maxDelay = Config.Bind("General", "Wait at most (minutes)", 5,
                new ConfigDescription("If no quiet moment comes, save anyway after this long, so no progress is at risk.", new AcceptableValueRange<int>(1, 30)));
            _stillSeconds = Config.Bind("General", "Standing still counts after (s)", 3f,
                new ConfigDescription("Standing still this long (with no creature chasing you) counts as a quiet moment.", new AcceptableValueRange<float>(1f, 30f)));
            _message = Config.Bind("General", "Show save message", true, "Show \"Autosaving...\" in the top left corner when the autosave starts.");
            PatchModule(typeof(Plugin));
        }

        // Keeps track of how long the player has stood still.
        private void Update()
        {
            var player = Player.m_localPlayer;
            if (player == null) { _stillTime = 0f; return; }
            var still = player.GetVelocity().sqrMagnitude < 0.04f && !player.InAttack() && player.GetControlledShip() == null;
            _stillTime = still ? _stillTime + Time.unscaledDeltaTime : 0f;
        }

        // Replaces the game's autosave timer (Game.UpdateSaving): same saving, chosen moment.
        [HarmonyPatch(typeof(Game), "UpdateSaving")]
        [HarmonyPrefix]
        private static bool UpdateSaving(Game __instance, float dt)
        {
            Game.m_saveInterval = _interval.Value * 60f;
            if (!_enabled.Value) return true; // vanilla, with the chosen interval
            if (SaveSystem.HasSessionFlag(SaveSystemSessionFlags.DontSaveCharacter | SaveSystemSessionFlags.DontSaveWorld)) return false;

            __instance.m_saveTimer += dt;
            var overdue = __instance.m_saveTimer - Game.m_saveInterval;
            if (overdue <= 0f) return false;
            if (!QuietMoment() && overdue < _maxDelay.Value * 60f) return false;

            if (ZNet.instance == null) return false;
            if (!ZNet.instance.EnoughDiskSpaceAvailable(out _))
            {
                __instance.m_saveTimer -= 300f; // like vanilla: try again in 5 minutes
                return false;
            }
            if (_message.Value) Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, "Autosaving...");
            __instance.SavePlayerProfile(true);
            ZNet.instance.Save(false, true, true); // resets the save timer
            return false;
        }

        private static bool QuietMoment()
        {
            var player = Player.m_localPlayer;
            if (player == null) return true; // nobody playing on this game: save now
            if (BeingChased(player)) return false;
            if (InventoryGui.IsVisible() || Menu.IsVisible() || StoreGui.IsVisible() ||
                (Minimap.instance != null && Minimap.instance.m_mode == Minimap.MapMode.Large)) return true;
            if (player.IsSitting() || player.InBed() || (player.IsAttached() && player.GetControlledShip() == null)) return true;
            return _stillTime >= _stillSeconds.Value;
        }

        private static bool BeingChased(Player player)
        {
            foreach (var ai in BaseAI.BaseAIInstances)
                if (ai != null && ai.GetTargetCreature() == player) return true;
            return false;
        }
    }
}
