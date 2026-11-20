// Plant Anything, picking and removing:
// - A picked flower or mushroom you planted stays as a small sprout (a third of its size) while it regrows; the game
//   hides it. Wild ones are left as the game has them.
// - Debris that regrows ("Debris regrow time"): picked up, a stone, flint or branch is hidden (no collider either) until
//   it is back; the game would remove it.
// - The hover text of a picked bush, mushroom or flower says when it is back.
// - The cultivator's remove key takes away a plant someone planted (crops, saplings, bushes, flowers, flora: never wild
//   ones): an unpicked one gives what it holds, and with "Seeds back when removing" its cost comes back too.
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace PlantAnything
{
    internal static class Picking
    {
        private const float Sprout = 0.35f;
        // the size a sprouting plant had before it shrank (by the object that shrank)
        private static readonly Dictionary<Transform, Vector3> Sizes = new Dictionary<Transform, Vector3>();

        internal static void Clear() => Sizes.Clear();

        private static bool Planted(ZNetView nview)
        {
            var zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            return zdo != null && zdo.GetLong(ZDOVars.s_creator) != 0L;
        }

        [HarmonyPatch(typeof(Pickable), "Awake")]
        [HarmonyPostfix]
        private static void Woke(Pickable __instance) => Look(__instance);

        [HarmonyPatch(typeof(Pickable), nameof(Pickable.SetPicked))]
        [HarmonyPostfix]
        private static void Picked(Pickable __instance) => Look(__instance);

        // How a pickable looks picked or not: a planted flower as a sprout, regrowing debris hidden.
        private static void Look(Pickable pickable)
        {
            if (!Plugin.Running || pickable == null) return;
            var name = Utils.GetPrefabName(pickable.gameObject);
            if (Plantables.IsDebris(name))
            {
                if (pickable.m_respawnTimeMinutes > 0f && pickable.m_hideWhenPicked == null) Show(pickable.gameObject, !pickable.m_picked);
                return;
            }
            if (!Plugin.PickedVisible.Value || !Plantables.IsFlower(name) || pickable.m_hideWhenPicked == null || !Planted(pickable.m_nview)) return;
            var visual = pickable.m_hideWhenPicked.transform;
            if (pickable.m_picked && pickable.m_enabled != 0)
            {
                if (!Sizes.ContainsKey(visual)) Sizes[visual] = visual.localScale;
                pickable.m_hideWhenPicked.SetActive(true);
                visual.localScale = Sizes[visual] * Sprout;
            }
            else if (Sizes.TryGetValue(visual, out var size))
            {
                visual.localScale = size;
                Sizes.Remove(visual);
            }
        }

        private static void Show(GameObject go, bool shown)
        {
            foreach (var renderer in go.GetComponentsInChildren<Renderer>(true)) renderer.enabled = shown;
            foreach (var collider in go.GetComponentsInChildren<Collider>(true)) collider.enabled = shown;
        }

        // "Back in 12 min" on a picked bush, mushroom or flower (the game shows nothing on it).
        [HarmonyPatch(typeof(Pickable), nameof(Pickable.GetHoverText))]
        [HarmonyPostfix]
        private static void Hover(Pickable __instance, ref string __result)
        {
            if (!Plugin.Running || !Plugin.ShowTime.Value || !__instance.m_picked || __instance.m_respawnTimeMinutes <= 0f ||
                __instance.m_enabled == 0 || __instance.m_nview == null || !__instance.m_nview.IsValid() || ZNet.instance == null) return;
            var at = __instance.m_nview.GetZDO().GetLong(ZDOVars.s_pickedTime);
            if (at <= 1L) return;
            var left = __instance.m_respawnTimeMinutes * 60.0 - (ZNet.instance.GetTime() - new System.DateTime(at)).TotalSeconds;
            if (left <= 0) return;
            __result = Localization.instance.Localize(__instance.GetHoverName()) + $"\n<color=#ffcf70>Back in {Growing.Duration(left)}</color>";
        }

        // ---- the cultivator's remove key ----

        private static int _mask;
        private static readonly RaycastHit[] Hits = new RaycastHit[16];

        [HarmonyPatch(typeof(Player), "RemovePiece")]
        [HarmonyPrefix]
        private static bool Remove(Player __instance, ref bool __result)
        {
            if (!Plugin.Running) return true;
            var tool = __instance.GetRightItem();
            if (tool == null || tool.m_shared.m_name != "$item_cultivator") return true;
            __result = false;
            var eye = GameCamera.instance != null ? GameCamera.instance.transform : null;
            if (eye == null) return false;
            // Bushes, mushrooms and flowers are on the "item" layer, which the game's remove ray does not see: an own ray,
            // the nearest thing hit that someone planted (a dropped item in front of it does not get in the way).
            if (_mask == 0) _mask = LayerMask.GetMask("item", "piece", "piece_nonsolid", "Default", "Default_small", "static_solid");
            var n = Physics.RaycastNonAlloc(eye.position, eye.forward, Hits, 50f, _mask);
            ZNetView nview = null;
            Piece.Requirement[] cost = null;
            var nearest = float.MaxValue;
            for (var i = 0; i < n; i++)
            {
                if (Hits[i].distance >= nearest || Vector3.Distance(Hits[i].point, __instance.m_eye.position) >= __instance.m_maxPlaceDistance) continue;
                var view = Hits[i].collider.GetComponentInParent<ZNetView>();
                if (view == null || !view.IsValid() || !Planted(view) || !Plantables.Removable(Utils.GetPrefabName(view.gameObject), out var its)) continue;
                nview = view;
                cost = its;
                nearest = Hits[i].distance;
            }
            if (nview == null) return false; // only what someone planted
            var go = nview.gameObject;
            if (!PrivateArea.CheckAccess(go.transform.position))
            {
                __instance.Message(MessageHud.MessageType.Center, "$msg_privatezone");
                return false;
            }
            var pickable = go.GetComponent<Pickable>();
            if (pickable != null && !pickable.m_picked && pickable.m_enabled != 0) nview.InvokeRPC("RPC_Pick", 0); // what it holds
            var at = go.transform.position + Vector3.up * 0.5f;
            if (Plugin.SeedsBack.Value && cost != null)
                foreach (var req in cost)
                {
                    if (req.m_resItem == null || req.m_amount <= 0) continue;
                    var drop = Object.Instantiate(req.m_resItem.gameObject, at, Quaternion.identity).GetComponent<ItemDrop>();
                    drop?.SetStack(req.m_amount);
                }
            Plantables.PlaceEffect?.Create(go.transform.position, go.transform.rotation);
            nview.ClaimOwnership();
            ZNetScene.instance.Destroy(go);
            __instance.FaceLookDirection();
            __instance.m_zanim.SetTrigger(tool.m_shared.m_attack.m_attackAnimation);
            __result = true;
            return false;
        }
    }
}
