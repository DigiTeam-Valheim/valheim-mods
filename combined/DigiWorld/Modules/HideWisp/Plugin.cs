// Your Wisplight (Demister) keeps clearing the mist, but its wisp is no longer drawn: no floating ball, no glow and no light
// on your surroundings. Only for you: other players still see it. The mist is pushed away by the wisp's force field
// (Demister + ParticleSystemForceField), which stays on; only its renderers and lights are switched off.
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace HideWisp
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.hidewisp";
        public const string Name = "Digi's Hide Wisp";
        public const string Version = "1.0.0";

        private static GameObject _hidden;
        private static readonly List<Renderer> HiddenRenderers = new List<Renderer>();
        private static readonly List<Behaviour> HiddenLights = new List<Behaviour>();

        private void Awake() => PatchModule(typeof(Plugin));

        // The wisp is made (and moved) by the Wisplight's status effect; a new one comes after every re-equip.
        [HarmonyPatch(typeof(SE_Demister), nameof(SE_Demister.UpdateStatusEffect))]
        [HarmonyPostfix]
        private static void AfterUpdate(SE_Demister __instance)
        {
            var ball = __instance.m_ballInstance;
            if (ball == null || ball == _hidden || __instance.m_character == null || __instance.m_character != Player.m_localPlayer) return;
            Show();
            _hidden = ball;
            foreach (var r in ball.GetComponentsInChildren<Renderer>(true))
                if (r.enabled)
                {
                    r.enabled = false;
                    HiddenRenderers.Add(r);
                }
            // The game's light manager turns lights back on by distance, so it is paused before its light.
            foreach (var lod in ball.GetComponentsInChildren<LightLod>(true))
                if (lod.enabled)
                {
                    lod.enabled = false;
                    HiddenLights.Add(lod);
                }
            foreach (var light in ball.GetComponentsInChildren<Light>(true))
                if (light.enabled)
                {
                    light.enabled = false;
                    HiddenLights.Add(light);
                }
        }

        private static void Show()
        {
            foreach (var r in HiddenRenderers)
                if (r != null) r.enabled = true;
            // Lights before their light managers (added the other way round).
            for (var i = HiddenLights.Count - 1; i >= 0; i--)
                if (HiddenLights[i] != null) HiddenLights[i].enabled = true;
            HiddenRenderers.Clear();
            HiddenLights.Clear();
            _hidden = null;
        }

        private void OnDestroy() => Show();
    }
}
