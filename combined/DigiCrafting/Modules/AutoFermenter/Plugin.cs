// Fermenters that run themselves from nearby chests. Every couple of seconds, for each fermenter this game controls:
// 1. When the mead is ready it is tapped into a chest the way "Store Nearby" (Local Craft From Storage) would: a chest
//    that already holds that mead, else the nearest chest with room for the whole batch.
// 2. An empty fermenter is filled with a mead base from a chest in range.
// Chests are only touched when nobody has them open; the game then takes control of the chest like opening it would,
// so its contents stay in sync for everyone. Carts, ships and private chests of other players are skipped.
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AutoFermenter
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.autofermenter";
        public const string Name = "Digi's Auto Fermenter";
        public const string Version = "1.1.2";

        private static ConfigEntry<bool> _enabled, _fill, _empty;
        private static ConfigEntry<float> _range, _speed;

        private static readonly HashSet<Fermenter> Fermenters = new HashSet<Fermenter>();
        private static readonly HashSet<Container> Chests = new HashSet<Container>();

        private static readonly AccessTools.FieldRef<Fermenter, ZNetView> FermenterView = AccessTools.FieldRefAccess<Fermenter, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<Fermenter, EffectList> TapEffects = AccessTools.FieldRefAccess<Fermenter, EffectList>("m_tapEffects");
        private static readonly AccessTools.FieldRef<Container, ZNetView> ChestView = AccessTools.FieldRefAccess<Container, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<Container, Piece> ChestPiece = AccessTools.FieldRefAccess<Container, Piece>("m_piece");

        private float _timer;

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
            _enabled = Config.Bind("General", "Enabled", true, "Fermenters fill and empty themselves using the chests near them.");
            _range = Config.Bind("General", "Chest range (m)", 10f,
                new ConfigDescription("Chests up to this many meters from the fermenter are used.", new AcceptableValueRange<float>(1f, 30f)));
            _fill = Config.Bind("General", "Fill from chests", true, "An empty fermenter takes a mead base from a nearby chest.");
            _empty = Config.Bind("General", "Tap into chest", true, "Finished mead goes into a nearby chest: one that already has that mead, otherwise the nearest one with room.");
            _speed = Config.Bind("General", "Speed multiplier", 1f, new ConfigDescription(
                "How fast fermenters make mead: 2 = twice as fast (the game's 40 minutes become 20), 1 = normal. Needs a restart.",
                new AcceptableValueRange<float>(0.25f, 10f)));
            PatchModule(typeof(Plugin));
        }

        [HarmonyPatch(typeof(Fermenter), "Awake")]
        [HarmonyPostfix]
        private static void FermenterAwake(Fermenter __instance)
        {
            Fermenters.Add(__instance);
            // Its duration, set once per fermenter as it loads (like the beehives' speed): the game's ready check and
            // Fermenter Timer's countdown both go by it.
            if (_speed.Value > 0f && _speed.Value != 1f) __instance.m_fermentationDuration /= _speed.Value;
        }

        [HarmonyPatch(typeof(Container), "Awake")]
        [HarmonyPostfix]
        private static void ChestAwake(Container __instance) => Chests.Add(__instance);

        private void Update()
        {
            if (!_enabled.Value || Player.m_localPlayer == null || ZNet.instance == null) return;
            _timer -= Time.deltaTime;
            if (_timer > 0f) return;
            _timer = 2f;
            // Ultimate Builder's creative world: its stations stand still, nothing is taken from chests or filled there.
            if (DigiCore.CreativeWorld.Active) return;
            // One save per changed chest for the whole round (StoreRules.HoldSaves).
            StoreRules.HoldSaves();
            try
            {
                Fermenters.RemoveWhere(f => f == null);
                Chests.RemoveWhere(c => c == null);
                foreach (var fermenter in Fermenters)
                {
                    var view = FermenterView(fermenter);
                    if (view == null || !view.IsValid() || !view.IsOwner()) continue;
                    try
                    {
                        Run(fermenter, view.GetZDO(), view);
                    }
                    catch (System.Exception e)
                    {
                        Logger.LogWarning($"Fermenter at {fermenter.transform.position}: {e.Message}");
                    }
                }
            }
            finally
            {
                StoreRules.SaveHeld();
            }
        }

        private static void Run(Fermenter fermenter, ZDO zdo, ZNetView view)
        {
            var content = zdo.GetInt(ZDOVars.s_content);
            if (content != 0)
            {
                if (!_empty.Value || !IsReady(fermenter, zdo)) return;
                var conversion = fermenter.m_conversion.FirstOrDefault(c => c.m_from != null && c.m_from.name.GetStableHashCode() == content);
                if (conversion?.m_to == null) return;
                var chest = StoreRules.PickOutputChest(UsableChests(fermenter.transform.position), conversion.m_to.gameObject, conversion.m_producedItems);
                if (chest == null) return; // no room: leave it ready in the barrel
                chest.GetInventory().AddItem(conversion.m_to.gameObject, conversion.m_producedItems);
                zdo.Set(ZDOVars.s_content, 0);
                zdo.Set(ZDOVars.s_startTime, 0L);
                zdo.Set(ZDOVars.s_cheatedQueued, false);
                TapEffects(fermenter)?.Create(fermenter.transform.position, fermenter.transform.rotation);
                return; // refill next round
            }

            if (!_fill.Value) return;
            if (StoreRules.TakeOne(UsableChests(fermenter.transform.position), i => i.m_dropPrefab != null &&
                    fermenter.m_conversion.Any(c => c.m_from != null && c.m_from.name == i.m_dropPrefab.name), out var taken))
                view.InvokeRPC("RPC_AddItem", taken.m_dropPrefab.name.GetStableHashCode(), taken.m_cheated);
        }

        private static bool IsReady(Fermenter fermenter, ZDO zdo)
        {
            var start = zdo.GetLong(ZDOVars.s_startTime, 0L);
            if (start == 0L) return false;
            return (ZNet.instance.GetTime() - new System.DateTime(start)).TotalSeconds > fermenter.m_fermentationDuration;
        }

        // Chests in range, nearest first, that this game may change right now. Chests controlled by another player are
        // claimed when nobody has them open, and used from the next round on.
        private static List<Container> UsableChests(Vector3 at)
        {
            var me = Game.instance.GetPlayerProfile().GetPlayerID();
            var range = _range.Value;
            var result = new List<Container>();
            var dists = new Dictionary<Container, float>();
            foreach (var chest in Chests)
            {
                if (chest.m_wagon != null || chest.GetComponentInParent<Ship>() != null || chest.GetComponent<TombStone>() != null) continue;
                var piece = ChestPiece(chest);
                if (piece == null) continue; // only built chests
                var dist = Vector3.Distance(at, chest.transform.position);
                if (dist > range) continue;
                if (chest.m_privacy == Container.PrivacySetting.Private && piece.GetCreator() != me) continue;
                if (chest.m_privacy == Container.PrivacySetting.Group) continue;
                var view = ChestView(chest);
                if (view == null || !view.IsValid() || chest.GetInventory() == null) continue;
                if (view.GetZDO().GetInt(ZDOVars.s_inUse) == 1 || chest.IsInUse()) continue;
                if (!view.IsOwner())
                {
                    view.ClaimOwnership();
                    continue;
                }
                result.Add(chest);
                dists[chest] = dist;
            }
            return result.OrderBy(c => dists[c]).ToList(); // no tuples: Valheim's runtime lacks System.ValueTuple
        }
    }
}
