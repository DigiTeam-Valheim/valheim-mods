// Press a key (default Keypad +) to repair every damaged building piece around you. An English version of
// "EasyRepairArea" by Fernasz (MIT licence), with the same behaviour: free, no hammer or workbench needed.
// Only pieces that were actually damaged are counted; the key does nothing while typing in chat or the console.
// Spawners (breakable objects that keep spawning creatures: Greydwarf nests, body piles, the Ashlands totems; the game
// never heals them) can be healed by the same key and made unbreakable (no damage from anything), with a switch per
// biome for each; all off by default. A spawner counts for the biome it stands in.
using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RepairNearby
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.repairnearby";
        public const string Name = "Digi's Repair Nearby";
        public const string Version = "1.1.0";

        private ConfigEntry<bool> _enabled;
        private ConfigEntry<float> _radius;
        private ConfigEntry<KeyCode> _key;
        // The biomes in game order, with the spawners the game itself puts there (mods add more, in any biome).
        private static readonly Heightmap.Biome[] Biomes =
        {
            Heightmap.Biome.Meadows, Heightmap.Biome.BlackForest, Heightmap.Biome.Swamp, Heightmap.Biome.Mountain,
            Heightmap.Biome.Plains, Heightmap.Biome.Mistlands, Heightmap.Biome.AshLands, Heightmap.Biome.DeepNorth,
        };
        private static readonly string[] BiomeNames = { "Meadows", "Black Forest", "Swamp", "Mountains", "Plains", "Mistlands", "Ashlands", "Deep North" };
        private static readonly string[] Examples = { "", "Greydwarf nests, bone piles", "body piles, bone piles", "", "", "", "the spawn totems", "" };
        private static readonly ConfigEntry<bool>[] _repair = new ConfigEntry<bool>[Biomes.Length], _protect = new ConfigEntry<bool>[Biomes.Length];
        private static readonly int Health = "health".GetStableHashCode();
        private float _cooldown;

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
            _enabled = Config.Bind("General", "Enabled", true, "Turn the repair key on or off.");
            _radius = Config.Bind("General", "Radius (m)", 100f,
                new ConfigDescription("Building pieces up to this many meters from you are repaired.", new AcceptableValueRange<float>(1f, 200f)));
            _key = Config.Bind("General", "Key", KeyCode.KeypadPlus, "Key that repairs all damaged building pieces around you.");
            for (var i = 0; i < Biomes.Length; i++)
            {
                var which = $"Spawners standing in the {BiomeNames[i]}" + (Examples[i].Length > 0 ? $" ({Examples[i]}, and ones from mods)" : " (ones from mods)");
                _repair[i] = Config.Bind("Repair spawners", BiomeNames[i], false,
                    $"{which}: the key also heals damaged ones within the radius back to full health. The game never heals " +
                    "spawners: every stray hit while farming stays until they break.");
                _protect[i] = Config.Bind("Protect spawners", BiomeNames[i], false,
                    $"{which} take no damage at all: not from your hits or splash damage, not from creatures' arrows or " +
                    "other players, so farming next to one can never break it.");
            }
            CarryOverOldSwitches();
            PatchModule(typeof(Plugin));
        }

        private void Update()
        {
            if (!_enabled.Value) return;
            if (_cooldown > 0f)
            {
                _cooldown -= Time.unscaledDeltaTime;
                return;
            }
            var player = Player.m_localPlayer;
            if (player == null || Game.IsPaused() || Typing() || !UnityInput.Current.GetKeyDown(_key.Value)) return;
            _cooldown = 0.5f;
            Repair(player);
        }

        private static bool Typing() =>
            (Chat.instance != null && Chat.instance.HasFocus()) || Console.IsVisible() || TextInput.IsVisible() || Menu.IsVisible() ||
            (Hud.instance != null && Hud.instance.m_buildUi != null && Hud.instance.m_buildUi.SearchFieldFocused); // the build menu's search box

        private void Repair(Player player)
        {
            var center = player.transform.position;
            var max = _radius.Value * _radius.Value;
            var repaired = 0;
            foreach (var piece in FindObjectsOfType<WearNTear>())
            {
                if (piece == null || (piece.transform.position - center).sqrMagnitude > max) continue;
                if (piece.Repair()) repaired++; // false when it was not damaged
            }
            var totems = AnyOn(_repair) ? HealSpawners(center, max) : 0;
            var what = repaired > 0 ? $"{repaired} building pieces" : "";
            if (totems > 0) what += (what.Length > 0 ? " and " : "") + $"{totems} spawners";
            player.Message(MessageHud.MessageType.TopLeft,
                what.Length > 0 ? $"Repaired {what} within {_radius.Value:0} m" : $"Nothing to repair within {_radius.Value:0} m");
            Logger.LogInfo($"Repaired {repaired} pieces and {totems} spawners within {_radius.Value} m");
        }

        // Until 1.0.3 there were two switches, described for the Ashlands but working on every spawner: one that was on
        // turns that switch on for every biome, so nothing changes for whoever used it.
        private void CarryOverOldSwitches()
        {
            var file = Config.File;
            var orphans = AccessTools.Property(typeof(ConfigFile), "OrphanedEntries")?.GetValue(file, null) as IDictionary<ConfigDefinition, string>;
            if (orphans == null) return;
            var changed = false;
            foreach (var old in new List<ConfigDefinition>(orphans.Keys))
            {
                if (!old.Section.EndsWith("Spawn totems")) continue;
                var on = string.Equals(orphans[old].Trim(), "true", StringComparison.OrdinalIgnoreCase);
                var switches = old.Key == "Repair spawn totems" ? _repair : old.Key == "Protect spawn totems" ? _protect : null;
                if (on && switches != null)
                    foreach (var s in switches) s.Value = true;
                orphans.Remove(old);
                changed = true;
            }
            if (changed) file.Save();
        }

        private static bool AnyOn(ConfigEntry<bool>[] switches)
        {
            foreach (var s in switches)
                if (s != null && s.Value) return true;
            return false;
        }

        // A spawner: a breakable object (Destructible) that spawns creatures (some keep the spawner on a child).
        private static bool IsSpawner(Component c) => c != null && c.GetComponentInChildren<SpawnArea>(true) != null;

        // Is the switch on for the biome this spawner stands in? Never "Ocean": one at the shore counts for the land behind it.
        private static bool On(ConfigEntry<bool>[] switches, Component c)
        {
            if (!AnyOn(switches) || !IsSpawner(c) || WorldGenerator.instance == null) return false;
            var p = c.transform.position;
            var i = Array.IndexOf(Biomes, WorldGenerator.instance.GetBiome(p.x, p.z, -1000f));
            return i >= 0 && switches[i] != null && switches[i].Value;
        }

        // Full health the way the game counts it (more on higher world levels).
        private static int HealSpawners(Vector3 center, float max)
        {
            var healed = 0;
            var full = 0f;
            foreach (var spawner in FindObjectsOfType<Destructible>())
            {
                if (spawner == null || (spawner.transform.position - center).sqrMagnitude > max || !On(_repair, spawner)) continue;
                var view = spawner.GetComponent<ZNetView>();
                if (view == null || !view.IsValid()) continue;
                full = spawner.m_health + Game.m_worldLevel * spawner.m_health * (Game.instance != null ? Game.instance.m_worldLevelMineHPMultiplier : 0f);
                var zdo = view.GetZDO();
                var now = zdo.GetFloat(Health, full);
                if (now <= 0f || now >= full - 0.01f) continue;
                if (!view.IsOwner()) view.ClaimOwnership();
                zdo.Set(Health, full);
                healed++;
            }
            return healed;
        }

        // Every hit goes through Damage on the hitter's side and RPC_Damage on the spawner's owner: both are stopped, so
        // splash damage without an attacker, creatures and players without this mod cannot break it either.
        [HarmonyPatch(typeof(Destructible), nameof(Destructible.Damage))]
        [HarmonyPrefix]
        private static bool ProtectSpawner(Destructible __instance) => !On(_protect, __instance);

        [HarmonyPatch(typeof(Destructible), "RPC_Damage")]
        [HarmonyPrefix]
        private static bool ProtectSpawnerOwner(Destructible __instance) => !On(_protect, __instance);
    }
}
