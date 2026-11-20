// Build Materials (Creative world): a hammer tool. Click a building piece and the shared Build Materials window opens
// (DigiShared/MaterialsList.cs, also in Building Tweaks): every material the whole building costs (the pieces connected
// to the one you clicked), with what you have in your inventory and nearby chests and what is missing; sortable and
// searchable. The schematic library shows the same window for a saved schematic.
// When Building Tweaks' Build Materials is on (the same tool in every world), this one stays out of the hammer.
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using DigiShared;
using UnityEngine;

namespace DigiCreative
{
    internal class BuildMaterials : MonoBehaviour
    {
        internal static ConfigEntry<bool> On;
        internal static ConfigEntry<float> ChestRange;
        private static BuildMaterials _instance;
        private static bool _busy;

        internal static void Bind(ConfigFile config)
        {
            On = config.Bind("7 - Build Materials", "Build Materials tool", true,
                "A Build Materials tool in the hammer menu (Building tab, at the end). Click a building piece with it: a window " +
                "lists every material the whole building connected to it costs, what you have and what is missing.");
            ChestRange = config.Bind("7 - Build Materials", "Chest range (m)", 50f, new ConfigDescription(
                "Chests up to this far away count for Have (with your inventory): player-built ones you may open. 0: only your inventory.",
                new AcceptableValueRange<float>(0f, 100f)));
            MaterialsList.ChestRange = ChestRange.Value;
            ChestRange.SettingChanged += (_, __) => MaterialsList.ChestRange = ChestRange.Value;
        }

        internal static void Register() =>
            Tools.Add("buildmaterials", "Build Materials",
                "Click a building piece: every material the whole building connected to it costs, what you have (inventory and " +
                "nearby chests) and what is missing.", "piece_chest_wood", () => On.Value && !Tools.BuildingTweaksHas("Build Materials"),
                player =>
                {
                    if (_instance != null && !_busy && !Windows.IsOpen) _instance.StartCoroutine(Count(player));
                });

        private void Awake() => _instance = this;

        private static IEnumerator Count(Player player)
        {
            var start = player.GetHoveringPiece();
            if (start == null || !start.IsPlacedByPlayer())
            {
                player.Message(MessageHud.MessageType.Center, "Aim at a piece of the building");
                yield break;
            }
            _busy = true;
            try
            {
                var found = new List<Piece>();
                var steps = 0;
                foreach (var piece in Buildings.Connected(start, found))
                    if (++steps % 150 == 0) yield return null;
                var names = found.Where(p => p != null).Select(p => Utils.GetPrefabName(p.gameObject)).ToList();
                var what = Localization.instance.Localize(start.m_name);
                Show(names.Count == 1 ? $"1 piece: {what}" : $"{Number(names.Count)} pieces, connected to the {what} you clicked", names);
            }
            finally { _busy = false; }
        }

        // The window for these pieces (prefab names, one per piece); back: opens the window it came from (the library).
        internal static void Show(string subtitle, List<string> prefabs, System.Action back = null) => MaterialsList.Show(subtitle, prefabs, back);

        internal static string Number(int n) => MaterialsList.Number(n);
    }
}
