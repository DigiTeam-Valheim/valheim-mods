// Shows why a tamed animal is or is not breeding, under its hover text. Valheim's Procreation checks every few seconds:
// tamed, fed, not alerted, fewer than m_maxCreatures adults + offspring of its kind within m_totalCheckRange, and a
// ready partner within m_partnerCheckRange; three (or more) such checks make it pregnant, and the offspring is born
// after m_pregnancyDuration. Display only; breeding itself is unchanged.
using System;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace BreedingStatus
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.breedingstatus";
        public const string Name = "Digi's Breeding Status";
        public const string Version = "1.0.2";

        private static ConfigEntry<bool> _enabled;

        private static readonly AccessTools.FieldRef<Tameable, ZNetView> TameView = AccessTools.FieldRefAccess<Tameable, ZNetView>("m_nview");

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
            _enabled = Config.Bind("General", "Show breeding status", true,
                "Show why a tamed animal is or is not breeding when you hover over it.");
            PatchModule(typeof(Plugin));
        }

        [HarmonyPatch(typeof(Tameable), nameof(Tameable.GetHoverText))]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void HoverPostfix(Tameable __instance, ref string __result)
        {
            if (!_enabled.Value || __instance == null || string.IsNullOrEmpty(__result)) return;
            try
            {
                var status = Status(__instance);
                if (status != null) __result += status;
            }
            catch (Exception)
            {
                // Never break the hover text.
            }
        }

        private static string Status(Tameable tameable)
        {
            if (!tameable.IsTamed()) return null;
            var procreation = tameable.GetComponent<Procreation>();
            var view = TameView(tameable);
            var zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (procreation == null || zdo == null || ZNet.instance == null || ZNetScene.instance == null) return null;

            var now = ZNet.instance.GetTime();
            var pos = tameable.transform.position;
            var lines = new System.Text.StringBuilder();

            var pregnant = zdo.GetLong(ZDOVars.s_pregnant, 0L);
            if (pregnant != 0L)
            {
                var left = procreation.m_pregnancyDuration - (now - new DateTime(pregnant)).TotalSeconds;
                lines.Append(left > 0 ? $"\n<color=#ff9ed8>Pregnant: offspring in {Math.Ceiling(left)}s</color>"
                                      : "\n<color=#ff9ed8>Pregnant: offspring at the next check</color>");
                return lines.ToString();
            }

            var love = zdo.GetInt(ZDOVars.s_lovePoints, 0);
            lines.Append($"\n<color=#ff9ed8>Breeding: love {love}/{procreation.m_requiredLovePoints}</color>");

            var fedLeft = tameable.m_fedDuration - (now - new DateTime(zdo.GetLong(ZDOVars.s_tameLastFeeding, 0L))).TotalSeconds;
            lines.Append(fedLeft > 0 ? Ok($"Fed ({Duration(fedLeft)} left)") : Bad("Hungry: feed it to breed"));

            var ai = tameable.GetComponent<BaseAI>();
            if (ai != null && ai.IsAlerted()) lines.Append(Bad("Alerted: no breeding during danger"));

            var me = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
            var offspring = procreation.m_offspring != null ? ZNetScene.instance.GetPrefab(Utils.GetPrefabName(procreation.m_offspring)) : null;
            if (me != null)
            {
                var crowd = SpawnSystem.GetNrOfInstances(me, pos, procreation.m_totalCheckRange) +
                            (offspring != null && offspring != me ? SpawnSystem.GetNrOfInstances(offspring, pos, procreation.m_totalCheckRange) : 0);
                var room = $"{crowd}/{procreation.m_maxCreatures} within {procreation.m_totalCheckRange:0} m (incl. young)";
                lines.Append(crowd >= procreation.m_maxCreatures ? Bad("Too crowded: " + room) : Ok("Space: " + room));

                if (procreation.m_noPartnerOffspring == null)
                {
                    var partnerPrefab = procreation.m_seperatePartner != null ? procreation.m_seperatePartner : me;
                    var ready = SpawnSystem.GetNrOfInstances(partnerPrefab, pos, procreation.m_partnerCheckRange, false, true);
                    var others = procreation.m_seperatePartner != null ? ready : ready - (procreation.ReadyForProcreation() ? 1 : 0);
                    lines.Append(others > 0
                        ? Ok($"Partner within {procreation.m_partnerCheckRange:0} m")
                        : Bad($"No ready partner within {procreation.m_partnerCheckRange:0} m"));
                }
            }
            return lines.ToString();
        }

        private static string Ok(string text) => $"\n<color=#8fe08f>+ {text}</color>";

        private static string Bad(string text) => $"\n<color=#ff7070>- {text}</color>";

        private static string Duration(double seconds) =>
            seconds >= 60 ? $"{Math.Floor(seconds / 60)} min" : $"{Math.Ceiling(seconds)}s";
    }
}
