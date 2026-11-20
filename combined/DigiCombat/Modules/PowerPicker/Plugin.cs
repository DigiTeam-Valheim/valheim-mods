// Power Picker: a key (P) opens a window with every forsaken power: its icon, name and what it does (the boss stone's
// own text). Powers whose boss trophy hangs on its stone can be chosen right there, the same way the stone does it
// (Player.SetGuardianPower), so there is no need to travel to the start each time. Powers whose trophy is not hung yet
// are greyed out. With Passive Powers on, the chosen power is on at once and stays on.
// Which trophies hang: the boss stones are read from the saved world (the host knows every one). Where the stones are not
// known (a player far from them on a server), the stones this character has used count instead.
using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using DigiShared;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace PowerPicker
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.powerpicker";
        public const string Name = "Digi's Power Picker";
        public const string Version = "1.0.0";

        private const float Width = 900f, Height = 780f, RowHeight = 104f;
        private static readonly string[] Order = { "GP_Eikthyr", "GP_TheElder", "GP_Bonemass", "GP_Moder", "GP_Yagluth", "GP_Queen", "GP_Fader" };
        private static readonly int ItemKey = "item".GetStableHashCode();
        private static readonly AccessTools.FieldRef<ZDOMan, Dictionary<ZDOID, ZDO>> AllZdos = AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZDOID, ZDO>>("m_objectsByID");

        private static ConfigEntry<KeyCode> _key;
        private static Windows.Window _window;

        // One forsaken power: its stone(s) and trophy.
        private sealed class Power
        {
            public StatusEffect Effect;
            public string Trophy;
            public readonly HashSet<int> Stones = new HashSet<int>();
        }

        private void Awake()
        {
            _key = Config.Bind("General", "Key", KeyCode.P,
                "Opens and closes the window with every forsaken power: choose one whose boss trophy hangs on its stone, without going there.");
            PatchModule(typeof(Plugin));
            Windows.Patch(ModuleHarmony);
            Windows.Use(gameObject);
        }

        private void OnDestroy()
        {
            if (IsOpen) Windows.Close();
            Windows.Release(gameObject);
            _window = null;
        }

        private static bool IsOpen => Windows.IsOpen && Windows.Current == _window;

        private void Update()
        {
            var player = Player.m_localPlayer;
            if (player == null || !UnityInput.Current.GetKeyDown(_key.Value) || Typing()) return;
            if (IsOpen)
            {
                Windows.Close();
                return;
            }
            if (player.IsDead() || Menu.IsVisible() || Minimap.IsOpen() || StoreGui.IsVisible() || InventoryGui.IsVisible() ||
                Hud.IsPieceSelectionVisible()) return;
            Open();
        }

        private static bool Typing() =>
            (Chat.instance != null && Chat.instance.HasFocus()) || Console.IsVisible() || TextInput.IsVisible() || Minimap.InTextInput() ||
            Windows.Searching || (Hud.instance != null && Hud.instance.m_buildUi != null && Hud.instance.m_buildUi.SearchFieldFocused); // the build menu's search box

        // --- What there is ----------------------------------------------------------------------------------------------

        // Every forsaken power with the stone prefabs that give it (an item stand whose power is set) and their trophy.
        private static List<Power> Powers()
        {
            var byName = new Dictionary<string, Power>();
            if (ZNetScene.instance != null)
                foreach (var prefab in ZNetScene.instance.m_prefabs)
                {
                    var stand = prefab != null ? prefab.GetComponentInChildren<ItemStand>(true) : null;
                    if (stand == null || stand.m_guardianPower == null) continue;
                    var effect = ObjectDB.instance != null ? ObjectDB.instance.GetStatusEffect(stand.m_guardianPower.NameHash()) : null;
                    if (effect == null) effect = stand.m_guardianPower;
                    if (!byName.TryGetValue(effect.name, out var power)) byName[effect.name] = power = new Power { Effect = effect };
                    power.Stones.Add(prefab.name.GetStableHashCode());
                    if (power.Trophy == null && stand.m_supportedItems != null && stand.m_supportedItems.Count > 0 && stand.m_supportedItems[0] != null)
                        power.Trophy = stand.m_supportedItems[0].m_itemData.m_shared.m_name;
                }
            return byName.Values
                .OrderBy(p => Array.IndexOf(Order, p.Effect.name) is var i && i >= 0 ? i : Order.Length)
                .ThenBy(p => p.Effect.name)
                .ToList();
        }

        // The powers whose trophy hangs on a stone in the saved world; null when no stone is known here at all.
        private static HashSet<string> Hung(List<Power> powers)
        {
            if (ZDOMan.instance == null) return null;
            var stones = new Dictionary<int, Power>();
            foreach (var p in powers)
                foreach (var hash in p.Stones)
                    stones[hash] = p;
            var hung = new HashSet<string>();
            var anyStone = false;
            foreach (var zdo in AllZdos(ZDOMan.instance).Values)
            {
                if (zdo == null || !stones.TryGetValue(zdo.GetPrefab(), out var power)) continue;
                anyStone = true;
                if (zdo.GetInt(ItemKey) != 0) hung.Add(power.Effect.name); // the hung item's hash (ItemStand.HaveAttachment)
            }
            return anyStone ? hung : null;
        }

        // --- The window -------------------------------------------------------------------------------------------------

        private static void Open()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;
            var powers = Powers();
            var hung = Hung(powers);
            var current = player.GetGuardianPowerName();
            var w = Windows.Open("Forsaken Powers",
                hung != null ? "Choose any power whose trophy hangs on its boss stone. The others are greyed out."
                             : "Boss stones not loaded here: the stones you have used before count.",
                Width, Height, $"{_key.Value}: open and close      Esc: close", RowHeight);
            if (w == null) return;
            _window = w;
            var y = 0f;
            var index = 0;
            foreach (var power in powers)
            {
                var name = power.Effect.name;
                bool ready = hung != null ? hung.Contains(name) : player.HaveUniqueKey(name);
                bool active = name == current;
                Row(w, power, y, index++, ready, active);
                y += RowHeight;
            }
            if (powers.Count == 0)
            {
                var none = Windows.Text(w.Content, "No forsaken powers found in this world.", 20f, Windows.Dim, TextAlignmentOptions.Left);
                Windows.Place(none.rectTransform, 14f, 10f, w.RowWidth - 28f, 34f);
                y = 54f;
            }
            Windows.Done(w, y);
        }

        private static void Row(Windows.Window w, Power power, float y, int index, bool ready, bool active)
        {
            var width = w.RowWidth;
            var row = Windows.Rect("Power", w.Content);
            Windows.Place(row, 0f, y, width, RowHeight);
            row.gameObject.AddComponent<UnityEngine.UI.Image>().color = active ? new Color(1f, 0.72f, 0.36f, 0.14f) : index % 2 == 0 ? Windows.RowEven : Windows.RowOdd;

            var icon = Windows.Picture(row, power.Effect.m_icon, 14f, 14f, 76f, 76f);
            if (!ready) icon.color = new Color(1f, 1f, 1f, 0.3f);

            var title = Localization.instance.Localize(power.Effect.m_name);
            var nameColor = active ? Windows.Gold : ready ? Windows.Body : Windows.Dim;
            Windows.Place(Windows.Text(row, title, 24f, nameColor, TextAlignmentOptions.Left).rectTransform, 104f, 8f, width - 104f - 190f, 32f);

            var trophy = power.Trophy != null ? Localization.instance.Localize(power.Trophy) : "its trophy";
            var state = active ? "Your power now" : ready ? "Its trophy hangs on the stone: click Choose" : $"Not yet: hang the {trophy} on its stone";
            Windows.Place(Windows.Text(row, state, 17f, active ? Windows.Gold : Windows.Dim, TextAlignmentOptions.Left).rectTransform,
                104f, 38f, width - 104f - 190f, 24f);

            // Only the bonuses: the game's tooltip is the description, an empty line, then one line per bonus.
            var full = power.Effect.GetTooltipString() ?? "";
            var flavor = power.Effect.m_tooltip ?? "";
            if (flavor.Length > 0 && full.StartsWith(flavor, StringComparison.Ordinal)) full = full.Substring(flavor.Length);
            var stats = Localization.instance.Localize(full).Replace("\r", "");
            stats = string.Join("     ", stats.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0));
            if (stats.Length == 0) stats = Localization.instance.Localize(flavor);
            var statText = Windows.Text(row, stats, 16f, ready ? Windows.Body : Windows.Dim, TextAlignmentOptions.TopLeft);
            statText.textWrappingMode = TextWrappingModes.Normal;
            Windows.Place(statText.rectTransform, 104f, 62f, width - 104f - 190f, RowHeight - 64f);

            if (ready && !active)
                Windows.Button(row, "Choose", width - 14f - 160f, (RowHeight - 44f) / 2f, 160f, 44f, () => Choose(power));
        }

        // The same as using the boss stone (ItemStand.DelayedPowerActivation): the power, and the stone counted as used.
        private static void Choose(Power power)
        {
            var player = Player.m_localPlayer;
            if (player == null) return;
            player.SetGuardianPower(power.Effect.name);
            Game.instance?.IncrementPlayerStat(PlayerStatType.SetGuardianPower);
            // On at once when Passive Powers runs (it checks every few seconds anyway). Found by name: in the Hexium single
            // it lives in another assembly, or is not installed.
            try { AccessTools.Method(AccessTools.TypeByName("PassivePowers.Plugin"), "Refresh")?.Invoke(null, null); }
            catch { }
            player.Message(MessageHud.MessageType.TopLeft, "Forsaken power: " + Localization.instance.Localize(power.Effect.m_name));
            Open(); // redrawn with the new choice
        }
    }
}
