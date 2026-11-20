// Cape Feather Fall: a switch per vanilla cape that gives feather fall while it is worn (the game's "SlowFall" effect:
// you fall slowly and take no fall damage), as the Feather Cape does and Epic Loot's Feather Fall enchantment (legs only)
// gives. Only the Feather Cape has it in vanilla, so it has no switch here. All switches are off by default, except the
// Cape of the Caller (the Deep North mage set's cape, with the Robes of the Caller): on since Combat and Loot 1.0.12, and
// switched on once for players who already had it off from before.
// The game rebuilds the worn equipment's effects on every equipment change; this adds feather fall to that list, so it
// goes away by itself when the cape comes off or its switch is turned off.
// Light: runs only when the worn equipment changes.
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;

namespace CapeFeatherFall
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.capefeatherfall";
        public const string Name = "Digi's Cape Feather Fall";
        public const string Version = "1.0.0";

        // Prefab name and the name in F1, in the order the capes come in the game.
        private static readonly string[][] Capes =
        {
            new[] { "CapeDeerHide", "Deer Hide Cape" }, new[] { "CapeTrollHide", "Troll Hide Cape" },
            new[] { "CapeLinen", "Linen Cape" }, new[] { "CapeWolf", "Wolf Fur Cape" }, new[] { "CapeLox", "Lox Cape" },
            new[] { "CapeAsksvin", "Asksvin Cloak" }, new[] { "CapeAsh", "Ashen Cape" },
            new[] { "CapeDeepNorth", "Moose Hide Cape" }, new[] { "CapeDeepNorthMage", "Cape of the Caller" },
            new[] { "CapeOdin", "Cape of Oden" },
        };

        private const string OnByDefault = "CapeDeepNorthMage";

        private static readonly Dictionary<string, ConfigEntry<bool>> Switches = new Dictionary<string, ConfigEntry<bool>>();
        private static readonly int SlowFall = "SlowFall".GetStableHashCode();
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> Shoulder =
            AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_shoulderItem");
        private static readonly AccessTools.FieldRef<Humanoid, HashSet<StatusEffect>> WornEffects =
            AccessTools.FieldRefAccess<Humanoid, HashSet<StatusEffect>>("m_equipmentStatusEffects");
        private static readonly System.Reflection.MethodInfo UpdateWornEffects = AccessTools.Method(typeof(Humanoid), "UpdateEquipmentStatusEffects");

        private void Awake()
        {
            Switches.Clear();
            foreach (var cape in Capes)
            {
                var on = cape[0] == OnByDefault;
                var entry = Config.Bind("Capes", cape[1], on,
                    $"Wearing the {cape[1]} gives feather fall: you fall slowly and take no fall damage, as with the Feather Cape.");
                if (on && FirstTime(cape[0] + " on")) entry.Value = true;
                entry.SettingChanged += (s, e) => Refresh();
                Switches[cape[0]] = entry;
            }
            PatchModule(typeof(Plugin));
            Refresh();
        }

        private void OnDestroy()
        {
            Switches.Clear();
            Refresh();
        }

        // The game's own update of the worn equipment's effects, so a changed switch counts at once.
        private static void Refresh()
        {
            if (Player.m_localPlayer != null) UpdateWornEffects?.Invoke(Player.m_localPlayer, null);
        }

        [HarmonyPatch(typeof(Humanoid), "UpdateEquipmentStatusEffects")]
        [HarmonyPostfix]
        private static void AddFeatherFall(Humanoid __instance)
        {
            if (!(__instance is Player)) return;
            var cape = Shoulder(__instance)?.m_dropPrefab;
            if (cape == null || !Switches.TryGetValue(cape.name, out var on) || !on.Value) return;
            var effect = ObjectDB.instance != null ? ObjectDB.instance.GetStatusEffect(SlowFall) : null;
            var worn = WornEffects(__instance);
            if (effect == null || worn == null || worn.Contains(effect)) return;
            worn.Add(effect);
            __instance.GetSEMan().AddStatusEffect(effect);
        }
    }
}
