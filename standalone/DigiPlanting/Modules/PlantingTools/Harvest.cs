// Planting Tools, harvesting: hold the harvest key (Shift) while you pick a crop, berry bush or beehive, and everything
// around it (of the same kind, or everything pickable: a setting) is picked in the same go. With Farm Performance on,
// the whole lot goes straight into your inventory with one inventory update and one message per item.
// Replanting: each crop you pick is planted again on its spot ("Auto replant", on by default; hold the replant key, Ctrl,
// to pick everything around without replanting; with auto replant off it works the other way round: hold Ctrl to
// replant): from your seeds,
// then nearby chests, paid once per kind, planted through the same queue as a grid. Aiming at a crop shows the keys.
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace PlantingTools
{
    internal static class Harvest
    {
        public enum Replant { HarvestedCrop, SelectedCrop }

        private static ConfigEntry<bool> _on, _sameKind, _hints, _auto;
        private static ConfigEntry<Modifier> _key, _replantKey;
        private static ConfigEntry<float> _radius;
        private static ConfigEntry<Replant> _replantWhat;

        private static readonly Collider[] Around = new Collider[4096];
        private static readonly HashSet<Component> Found = new HashSet<Component>();
        private static readonly List<Component> Picks = new List<Component>();
        // crops picked by this interact, waiting to be planted again: by sapling prefab
        private static readonly Dictionary<GameObject, List<Vector3>> ToReplant = new Dictionary<GameObject, List<Vector3>>();
        private static readonly Dictionary<string, GameObject> SaplingOf = new Dictionary<string, GameObject>(); // crop prefab -> sapling prefab
        private static int _depth;
        private static bool _replantNow;

        internal static void Bind(DigiCore.ModuleConfig config)
        {
            _on = config.Bind("Harvest", "Harvest around", true,
                "Hold the harvest key while you pick a crop, berry bush, mushroom or beehive: everything around it is picked " +
                "at once.");
            _key = config.Bind("Harvest", "Harvest key", Modifier.Shift, "Hold this (left or right) while picking to pick everything around you.");
            _radius = config.Bind("Harvest", "Harvest radius (m)", 3f, new ConfigDescription(
                "How far around the thing you pick the rest is picked.", new AcceptableValueRange<float>(0.5f, 20f)));
            _sameKind = config.Bind("Harvest", "Same kind only", false,
                "Only things of the kind you picked are picked around it (only barley when you pick barley). Off: everything " +
                "pickable around it.");
            _auto = config.Bind("Harvest", "Auto replant", true,
                "Every crop you pick is planted again on its spot, with seeds from your bag, then nearby chests (hold the replant " +
                "key to pick without replanting). Off: only when you hold the replant key.");
            _replantKey = config.Bind("Harvest", "Replant key", Modifier.Ctrl,
                "With auto replant on: hold this (left or right) while picking to pick everything around WITHOUT replanting (to clear a field). Off: hold it to " +
                "plant the crop again on its spot (with the harvest key too: everything picked around it). Seeds come from your " +
                "bag, then nearby chests.");
            _replantWhat = config.Bind("Harvest", "Replant with", Replant.HarvestedCrop,
                "HarvestedCrop: the crop you picked. SelectedCrop: the crop chosen in the cultivator (or the picked one when none is).");
            _hints = config.Bind("Display", "Harvest hints", true, "Aiming at a crop, bush or beehive shows the harvest and replant keys.");
        }

        internal static void Copy(Dictionary<string, string> cfg, System.Action<Dictionary<string, string>, string, ConfigEntry<bool>> b,
            System.Action<Dictionary<string, string>, string, ConfigEntry<float>> f, System.Action<Dictionary<string, string>, string, ConfigEntry<Modifier>> k)
        {
            b(cfg, "Harvesting.EnableBulkHarvest", _on);
            f(cfg, "Harvesting.HarvestRadius", _radius);
            k(cfg, "Controls.KeyboardHarvestModifierKey", _key);
            if (cfg.TryGetValue("Harvesting.HarvestStyle", out var style)) _sameKind.Value = style.Trim() == "LikeResources";
            if (cfg.TryGetValue("Harvesting.ReplantStyle", out var what)) _replantWhat.Value = what.Trim() == "SelectedCrop" ? Replant.SelectedCrop : Replant.HarvestedCrop;
        }

        // Which sapling grows into which crop (from the cultivator's plants, so other mods' crops count too).
        internal static void Learn(PieceTable tool)
        {
            foreach (var go in tool.m_pieces)
            {
                var plant = go != null ? go.GetComponent<Plant>() : null;
                if (plant == null) continue;
                foreach (var grown in plant.m_grownPrefabs)
                    if (grown != null && grown.GetComponent<Pickable>() != null && !SaplingOf.ContainsKey(grown.name)) SaplingOf[grown.name] = go;
            }
        }

        private static void LearnOnce()
        {
            if (SaplingOf.Count > 0) return; // the cultivator was not out yet this session
            var cultivator = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab("Cultivator") : null;
            var table = cultivator != null ? cultivator.GetComponent<ItemDrop>()?.m_itemData.m_shared.m_buildPieces : null;
            if (table != null) Learn(table);
        }

        // ---- picking around ----

        // Ctrl is also the game's crouch key, and crouch toggles the moment it goes down, before E: Ctrl+E (picking
        // without replanting) left the player crouching. The crouch state from before Ctrl went down is remembered every
        // frame (Plugin.Update) and put back when a crop or beehive is picked with Ctrl held (as Portal List does).
        private static bool _crouchBefore;

        internal static void RememberCrouch()
        {
            var player = Player.m_localPlayer;
            if (player != null && !Plugin.Held(Modifier.Ctrl)) _crouchBefore = player.m_crouchToggled;
        }

        private static void UndoCrouch(Player player, GameObject go)
        {
            if (!Plugin.Held(Modifier.Ctrl) || (_replantKey.Value != Modifier.Ctrl && _key.Value != Modifier.Ctrl)) return;
            var target = go.GetComponentInParent<Interactable>();
            if (!(target is Pickable) && !(target is Beehive)) return;
            if (player.m_crouchToggled != _crouchBefore) player.SetCrouch(_crouchBefore);
        }

        [HarmonyPatch(typeof(Player), "Interact", new[] { typeof(GameObject), typeof(bool), typeof(bool) })]
        [HarmonyPrefix]
        private static void PickAround(Player __instance, GameObject go, bool hold, bool alt)
        {
            if (__instance != Player.m_localPlayer || go == null) return;
            if (_depth++ == 0) _replantNow = _auto.Value ? !Plugin.Held(_replantKey.Value) : Plugin.Held(_replantKey.Value);
            if (_depth == 1) UndoCrouch(__instance, go);
            if (!_on.Value || __instance.InAttack() || __instance.InDodge() || (hold && Time.time - __instance.m_lastHoverInteractTime < 0.2f)) return;
            // The replant key with auto replant on (Ctrl+E: clearing a field) picks everything around too, without
            // replanting: it picked only the one crop before (2026-11-19).
            var clearing = _auto.Value && Plugin.Held(_replantKey.Value);
            if (!Plugin.Held(_key.Value) && !alt && !clearing) return;
            var first = go.GetComponentInParent<Interactable>() as Component;
            if (!(first is Pickable) && !(first is Beehive)) return;
            var root = first.transform.root;
            var n = Physics.OverlapSphereNonAlloc(root.position, _radius.Value, Around, Grid.SpaceMask | LayerMask.GetMask("item"));
            Found.Clear();
            Picks.Clear();
            for (var i = 0; i < n; i++)
            {
                var c = Around[i];
                Component thing = c.GetComponentInParent<Pickable>();
                if (thing == null) thing = c.GetComponentInParent<Beehive>();
                if (thing == null || thing.transform.root == root || !Found.Add(thing)) continue;
                if (_sameKind.Value && thing.transform.root.name != root.name) continue;
                if (thing is Beehive hive && hive.GetHoneyLevel() < 1) continue;
                Picks.Add(thing);
            }
            foreach (var thing in Picks)
                if (thing != null) ((Interactable)thing).Interact(__instance, hold, alt);
            Picks.Clear();
            Found.Clear();
        }

        // After the whole interact (the first pick and everything around it): pay the replanting once and queue it.
        [HarmonyPatch(typeof(Player), "Interact", new[] { typeof(GameObject), typeof(bool), typeof(bool) })]
        [HarmonyFinalizer]
        private static System.Exception AfterInteract(Player __instance, System.Exception __exception)
        {
            if (__instance != Player.m_localPlayer || _depth == 0) return __exception;
            if (--_depth > 0) return __exception;
            _replantNow = false;
            if (ToReplant.Count == 0) return __exception;
            try { ReplantNow(__instance); }
            finally { ToReplant.Clear(); }
            return __exception;
        }

        // ---- replanting ----

        // (Interact returns whether to play the pick animation, not whether it picked: so look before it runs.)
        [HarmonyPatch(typeof(Pickable), nameof(Pickable.Interact))]
        [HarmonyPrefix]
        private static void Picked(Pickable __instance, Humanoid character)
        {
            if (!_replantNow || character != Player.m_localPlayer || _depth == 0) return;
            if (__instance.m_picked || __instance.m_nview == null || !__instance.m_nview.IsValid() || __instance.m_enabled == 0) return;
            LearnOnce();
            if (!SaplingOf.TryGetValue(Utils.GetPrefabName(__instance.gameObject), out var sapling)) return;
            if (_replantWhat.Value == Replant.SelectedCrop)
            {
                var chosen = Player.m_localPlayer.m_buildPieces?.GetSelectedPrefab();
                var plant = chosen != null ? chosen.GetComponent<Plant>() : null;
                if (plant != null && plant.m_attachDistance <= 0f) sapling = chosen;
            }
            if (!ToReplant.TryGetValue(sapling, out var list)) ToReplant[sapling] = list = new List<Vector3>();
            list.Add(__instance.transform.position);
        }

        private static void ReplantNow(Player player)
        {
            Planter.Owner(player);
            foreach (var pair in ToReplant)
            {
                var kind = Kind.Of(pair.Key);
                if (kind == null) continue;
                var paid = Plugin.ExtraPlantsPaid(player, kind); // what the seeds pay for, minus one
                var n = paid == int.MaxValue ? pair.Value.Count : Mathf.Min(pair.Value.Count, paid + 1);
                if (n > 0 && paid != int.MaxValue) n = Plugin.Pay(player, kind, n);
                if (n <= 0)
                {
                    // Asked for with the key: a clear message; by auto replant: a quiet one (picking without seeds is fine).
                    if (_auto.Value) player.Message(MessageHud.MessageType.TopLeft, "No seeds to replant " + Localization.instance.Localize(kind.Piece.m_name));
                    else player.Message(MessageHud.MessageType.Center, "$msg_missingrequirement");
                    continue;
                }
                Game.instance.IncrementPlayerStat(PlayerStatType.Builds, n);
                for (var i = 0; i < n; i++)
                    Planter.Add(pair.Key, pair.Value[i], Quaternion.Euler(0f, 22.5f * Random.Range(0, 16), 0f));
                if (n < pair.Value.Count) player.Message(MessageHud.MessageType.Center, $"Seeds to replant {n} of {pair.Value.Count}");
            }
        }

        // ---- hints ----

        [HarmonyPatch(typeof(Pickable), nameof(Pickable.GetHoverText))]
        [HarmonyPostfix]
        private static void Hint(Pickable __instance, ref string __result)
        {
            if (!_hints.Value || string.IsNullOrEmpty(__result)) return;
            if (_on.Value) __result += Localization.instance.Localize($"\n[<color=yellow><b>{Name(_key.Value)} + $KEY_Use</b></color>] Harvest around");
            LearnOnce();
            if (!SaplingOf.ContainsKey(Utils.GetPrefabName(__instance.gameObject))) return;
            if (_auto.Value)
            {
                __result += Localization.instance.Localize($"\n[<color=yellow><b>{Name(_replantKey.Value)} + $KEY_Use</b></color>] " +
                                                           (_on.Value ? "Harvest around without replanting" : "Harvest without replanting"));
                return;
            }
            __result +=Localization.instance.Localize($"\n[<color=yellow><b>{Name(_replantKey.Value)} + $KEY_Use</b></color>] Harvest and replant");
            if (_on.Value)
                __result += Localization.instance.Localize($"\n[<color=yellow><b>{Name(_replantKey.Value)} + {Name(_key.Value)} + $KEY_Use</b></color>] Harvest around and replant");
        }

        [HarmonyPatch(typeof(Beehive), nameof(Beehive.GetHoverText))]
        [HarmonyPostfix]
        private static void HiveHint(ref string __result)
        {
            if (!_hints.Value || !_on.Value || string.IsNullOrEmpty(__result)) return;
            __result += Localization.instance.Localize($"\n[<color=yellow><b>{Name(_key.Value)} + $KEY_Use</b></color>] Harvest around");
        }

        private static string Name(Modifier key) => key.ToString();
    }
}
