// Some items from other mods come with a collider that has no shape: a MeshCollider whose mesh is missing (HoneyPlus'
// Honey Cooked Meat, for one). Nothing can touch such an item: it falls through floors (the game keeps lifting it back
// up, so it seems to respawn) and the player's pickup never finds it. When such an item appears, its shapeless
// collider is replaced by a box of the item's own size. Each item kind is checked once; the rest is a lookup.
// Equipment Slots' quick slot 2 key is V, and so is the game's auto pickup switch: every use of that slot also switched
// auto pickup on or off (the game keeps that through a relog, only a restart turns it back on), so now and then nothing
// was picked up any more. A quick slot key now only uses its slot; auto pickup then needs its own key in the game's
// controls. Light: one button check per frame, the quick slot keys only when the auto pickup key was pressed.
using System.Collections.Generic;
using System.Linq;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ItemPickupFix
{
    public class Plugin : DigiCore.DigiModule
    {
        private static readonly Dictionary<string, bool> Broken = new Dictionary<string, bool>();
        private static BepInEx.Logging.ManualLogSource _log;
        private const string EquipmentSlotsGuid = "cdev.equipmentslots";
        private static ConfigEntry<bool> _quickSlots;
        private static List<ConfigEntryBase> _quickKeys; // Equipment Slots' [QuickSlots] keys, read live

        private void Awake()
        {
            _log = Logger;
            _quickSlots = Config.Bind("General", "Quick slot keys leave auto pickup alone", true,
                "Equipment Slots' quick slot 2 key is V, the game's auto pickup switch too: each use of that slot also switched " +
                "auto pickup on or off (it stayed off through a relog, until a restart). On: a quick slot key only uses its slot. " +
                "To switch auto pickup yourself, give it its own key in the game's controls settings.");
            PatchModule(typeof(Plugin));
            foreach (var drop in FindObjectsByType<ItemDrop>(FindObjectsSortMode.None)) Check(drop);
        }

        [HarmonyPatch(typeof(ItemDrop), "Awake")]
        [HarmonyPostfix]
        private static void Spawned(ItemDrop __instance) => Check(__instance);

        // Before the player reads its keys: a quick slot key pressed now takes the auto pickup press away.
        [HarmonyPatch(typeof(Player), "Update")]
        [HarmonyPrefix]
        private static void QuickSlotKey(Player __instance)
        {
            if (!_quickSlots.Value || __instance != Player.m_localPlayer || !ZInput.GetButtonDown("AutoPickup")) return;
            foreach (var entry in QuickKeys())
            {
                if (!(entry.BoxedValue is KeyboardShortcut key) || key.MainKey == KeyCode.None) continue;
                if (!ZInput.GetKeyDown(key.MainKey, false) || !key.Modifiers.All(m => ZInput.GetKey(m, false))) continue;
                ZInput.ResetButtonStatus("AutoPickup");
                return;
            }
        }

        private static List<ConfigEntryBase> QuickKeys()
        {
            if (_quickKeys != null) return _quickKeys;
            _quickKeys = new List<ConfigEntryBase>();
            if (Chainloader.PluginInfos.TryGetValue(EquipmentSlotsGuid, out var info) && info.Instance != null)
                foreach (var pair in info.Instance.Config)
                    if (pair.Key.Section == "QuickSlots" && pair.Value.SettingType == typeof(KeyboardShortcut)) _quickKeys.Add(pair.Value);
            return _quickKeys;
        }

        private static void Check(ItemDrop drop)
        {
            if (drop == null) return;
            var name = Utils.GetPrefabName(drop.gameObject);
            if (!Broken.TryGetValue(name, out var broken))
            {
                broken = IsBroken(drop.gameObject);
                Broken[name] = broken;
                if (broken)
                    _log.LogInfo($"{name} ({drop.m_itemData?.m_shared?.m_name}) has a collider without a shape: it gets a box, so it can be " +
                                 "picked up and stops falling through floors.");
            }
            if (broken) Fix(drop.gameObject);
        }

        private static bool IsBroken(GameObject item)
        {
            foreach (var collider in item.GetComponentsInChildren<MeshCollider>(true))
                if (!collider.isTrigger && collider.sharedMesh == null) return true;
            return false;
        }

        private static void Fix(GameObject item)
        {
            foreach (var collider in item.GetComponentsInChildren<MeshCollider>(true))
            {
                if (collider.isTrigger || collider.sharedMesh != null) continue;
                var bounds = VisualBounds(item, collider.transform);
                var box = collider.gameObject.AddComponent<BoxCollider>();
                box.center = bounds.center;
                box.size = Vector3.Max(bounds.size, Vector3.one * 0.05f);
                box.sharedMaterial = collider.sharedMaterial;
                Object.Destroy(collider);
            }
        }

        // The item's visible meshes, measured in the collider's own space; a small cube if it has none.
        private static Bounds VisualBounds(GameObject item, Transform space)
        {
            var found = false;
            var bounds = new Bounds();
            foreach (var filter in item.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = filter.sharedMesh;
                if (mesh == null) continue;
                var local = mesh.bounds;
                for (var i = 0; i < 8; i++)
                {
                    var corner = local.center + Vector3.Scale(local.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var point = space.InverseTransformPoint(filter.transform.TransformPoint(corner));
                    if (!found)
                    {
                        bounds = new Bounds(point, Vector3.zero);
                        found = true;
                    }
                    else bounds.Encapsulate(point);
                }
            }
            return found ? bounds : new Bounds(Vector3.zero, Vector3.one * 0.1f);
        }
    }
}
