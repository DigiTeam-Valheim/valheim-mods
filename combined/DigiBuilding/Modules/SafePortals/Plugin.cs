// Safe Portals: enemies no longer go for your portals. In vanilla every portal is a priority target, so enemies go
// for it before other buildings. Monsters only go for buildings flagged as a target (priority or random); every portal
// a player can build (wood, stone and portals from other mods) loses both flags. A portal can still be hit by accident,
// e.g. by an area attack aimed at you. (Keeping spawns away from portals: Spawn-Free Range.)
// A switch, off by default, lets stone portals carry the Sacrificial Blood, which goes through no portal in vanilla.
// Lag: the loaded portals are kept in a list as they load, for switching back; nothing runs per frame.
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;

namespace SafePortals
{
    public class Plugin : DigiCore.DigiModule
    {
        private const string SacrificialBlood = "$item_frozenking_drop";
        private static ConfigEntry<bool> _notTargeted, _stoneBlood;
        private static readonly List<TeleportWorld> Portals = new List<TeleportWorld>();

        private void Awake()
        {
            _notTargeted = Config.Bind("General", "Enemies ignore portals", true,
                "In vanilla every portal is a priority target: enemies go for it before your other buildings. On: " +
                "enemies no longer choose portals to attack (they still go for you and your other buildings). A portal " +
                "can still be hit by accident, e.g. by an area attack.");
            _notTargeted.SettingChanged += OnTargetsChanged;
            _stoneBlood = Config.Bind("General", "Stone portals allow Sacrificial Blood", false,
                "The Sacrificial Blood goes through no portal at all in vanilla, so it has to be carried by boat or on foot. On: stone portals let it through.");
            PatchModule(typeof(Plugin));
            foreach (var portal in FindObjectsByType<TeleportWorld>(UnityEngine.FindObjectsSortMode.None)) Add(portal);
        }

        private void OnDestroy()
        {
            _notTargeted.SettingChanged -= OnTargetsChanged;
            foreach (var portal in Portals)
                if (portal != null) Restore(portal);
            Portals.Clear();
        }

        [HarmonyPatch(typeof(TeleportWorld), "Awake")]
        [HarmonyPostfix]
        private static void PortalLoaded(TeleportWorld __instance) => Add(__instance);

        // The game keeps items of tool tier 1000 and up from every portal, and the blood is the only one. First, so Adventure
        // Backpacks still gets its turn (it only looks in your backpacks after a yes); its check of a backpack comes here too.
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.IsTeleportable))]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.First)]
        private static void StoneBlood(Inventory __instance, bool allowAllItems, ref bool __result)
        {
            if (__result || !allowAllItems || !_stoneBlood.Value) return;
            foreach (var item in __instance.GetAllItems())
                if (item.m_shared.m_toolTier >= 1000 && item.m_shared.m_name != SacrificialBlood) return;
            __result = true;
        }

        private static void Add(TeleportWorld portal)
        {
            if (portal == null || portal.GetComponent<Piece>() == null) return;
            Portals.RemoveAll(p => p == null);
            if (Portals.Contains(portal)) return;
            Portals.Add(portal);
            ApplyTargets(portal);
        }

        private static void OnTargetsChanged(object sender, System.EventArgs e)
        {
            foreach (var portal in Portals)
                if (portal != null) ApplyTargets(portal);
        }

        private static void ApplyTargets(TeleportWorld portal)
        {
            if (!_notTargeted.Value)
            {
                Restore(portal);
                return;
            }
            var piece = portal.GetComponent<Piece>();
            piece.m_primaryTarget = false;
            piece.m_randomTarget = false;
        }

        // Back to the values of the portal's own prefab.
        private static void Restore(TeleportWorld portal)
        {
            var piece = portal.GetComponent<Piece>();
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(Utils.GetPrefabName(portal.gameObject)) : null;
            var original = prefab != null ? prefab.GetComponent<Piece>() : null;
            if (piece == null || original == null) return;
            piece.m_primaryTarget = original.m_primaryTarget;
            piece.m_randomTarget = original.m_randomTarget;
        }
    }
}
