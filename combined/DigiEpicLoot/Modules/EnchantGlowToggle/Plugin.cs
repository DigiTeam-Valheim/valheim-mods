// Epic Loot enchantments and legendaries change how worn gear looks: an equip effect (e.g. the Glowing enchantment's
// GlowingAura) and, for some legendaries, a texture replacement (e.g. the blue Heimdall/Ragnar Padded Cuirass).
// CDEV.EquipmentSlots' Appearance tab only changes the worn model, so both stay. This hides them for a slot (helmet,
// chest, legs, cape) that shows an appearance. The hidden slots are shared on the player's network data, so other
// players with this mod see the same look. Visual only: the enchantments keep working.
// Epic Loot never shows the enchantment effects of a unique legendary (it only looks at the legendary's own effect,
// which most legendaries do not have), so e.g. Glowing on a legendary set piece stayed invisible; this falls back to
// the enchantment effect. Epic Loot also only builds these player auras from the local inventory, so other players
// never saw them; the worn auras are shared on the player's network data and attached on other players' screens.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using EpicLoot;
using HarmonyLib;
using UnityEngine;

namespace EnchantGlowToggle
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.enchantglowtoggle";
        public const string Name = "Digi's Enchant Glow Toggle";
        public const string Version = "1.3.4";
        private const string EpicLootGuid = "randyknapp.mods.epicloot";
        private const string EquipmentSlotsGuid = "cdev.equipmentslots";
        private const string LookKey = "CDEV_EquipmentSlots_look"; // "helmet|chest|legs|cape" prefab names, "-" = hidden, "name#2" = colour 2
        private const string GlowFx = "GlowingAura";
        private static readonly int HiddenKey = "pieter_enchantglowtoggle_hidden".GetStableHashCode();
        private static readonly int FxKey = "pieter_enchantglowtoggle_fx".GetStableHashCode();
        private static ConfigEntry<bool> _legendaryFallback, _shareAuras;
        [ThreadStatic] private static bool _publishing;
        private static readonly Dictionary<Player, Dictionary<string, GameObject>> RemoteFx = new Dictionary<Player, Dictionary<string, GameObject>>();

        private enum Match { SameItem, AnyAppearance, Off }

        private static ConfigEntry<Match> _appearance;
        private static ConfigEntry<bool> _hideGlowing, _hideAll, _hideTextures, _effectsBehind, _texturesBehind;
        private static MethodInfo _refreshFx;

        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> Helmet = AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_helmetItem");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> Chest = AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_chestItem");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> Legs = AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_legItem");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> Cape = AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_shoulderItem");
        private static readonly AccessTools.FieldRef<Humanoid, VisEquipment> Vis = AccessTools.FieldRefAccess<Humanoid, VisEquipment>("m_visEquipment");
        private static readonly string[] VisHashFields = { "m_currentHelmetItemHash", "m_currentChestItemHash", "m_currentLegItemHash", "m_currentShoulderItemHash" };

        // Player whose equipment Epic Loot is dressing right now (its lookups only get the item).
        [ThreadStatic] private static Player _dressing;
        private static readonly Dictionary<Player, int> KnownMasks = new Dictionary<Player, int>();
        private float _nextCheck;

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
            _appearance = Config.Bind("Appearance tab", "When to hide", Match.SameItem,
                "When a choice in the Appearance tab hides an item's enchanted look. SameItem = only when you pick the same item as its appearance. AnyAppearance = whenever the slot has an appearance. Off = never. Other players with this mod see it the same way.");
            _effectsBehind = Config.Bind("Appearance tab", "Hide glow and effects", true,
                "Hide the item's enchantment effects (like the Glowing aura) when the rule above applies.");
            _texturesBehind = Config.Bind("Appearance tab", "Hide legendary texture", true,
                "Hide the item's legendary look (like the blue Heimdall armor) when the rule above applies.");
            _hideGlowing = Config.Bind("Always", "Hide Glowing aura", false,
                "Never show the light and sparkles of the Glowing enchantment, on anyone. Only on your screen.");
            _hideAll = Config.Bind("Always", "Hide all enchantment effects", false,
                "Never show any effect of Epic Loot enchantments or legendary items, on anyone. Only on your screen.");
            _hideTextures = Config.Bind("Always", "Hide legendary textures", false,
                "Never show the special look of Epic Loot legendary items, on anyone. Only on your screen.");
            _legendaryFallback = Config.Bind("Effects", "Show enchant effects on legendaries", true,
                "Legendary items that have no effect of their own show the effect of their enchantments (like Glowing). Epic Loot normally shows nothing for them.");
            _shareAuras = Config.Bind("Effects", "Show auras on other players", true,
                "See the enchantment auras (like Glowing) of other players who have this mod. Epic Loot alone only shows your own.");
            _legendaryFallback.SettingChanged += (_, __) => RefreshAll();
            _log = Logger;

            var visPatch = AccessTools.TypeByName("EpicLoot.VisEquipment_Patch");
            var fxLookup = visPatch == null ? null : AccessTools.Method(visPatch, "GetEquipFxName");
            var textures = visPatch == null ? null : AccessTools.Method(visPatch, "SetTextureOverrides");
            _refreshFx = visPatch == null ? null : AccessTools.Method(visPatch, "RefreshPlayerFx");
            if (fxLookup == null || textures == null || _refreshFx == null)
            {
                Logger.LogError("Disabled, Epic Loot's equipment visuals were not found.");
                return;
            }

            var harmony = ModuleHarmony;
            harmony.Patch(fxLookup, postfix: Hook(nameof(EquipFxPostfix)));
            harmony.Patch(textures, prefix: Hook(nameof(TexturePrefix)));
            harmony.Patch(_refreshFx, prefix: Hook(nameof(DressPlayerPrefix)), finalizer: Hook(nameof(DressFinalizer)));
            foreach (var name in new[] { "AttachItem_Postfix", "AttachArmor_Postfix" })
            {
                var m = AccessTools.Method(visPatch, name);
                if (m != null) harmony.Patch(m, prefix: Hook(nameof(DressVisPrefix)), finalizer: Hook(nameof(DressFinalizer)));
            }

            _appearance.SettingChanged += (_, __) => RefreshAll();
            _effectsBehind.SettingChanged += (_, __) => RefreshAll();
            _texturesBehind.SettingChanged += (_, __) => RefreshAll();
            _hideGlowing.SettingChanged += (_, __) => RefreshAll();
            _hideAll.SettingChanged += (_, __) => RefreshAll();
            _hideTextures.SettingChanged += (_, __) => RefreshAll();
            Logger.LogInfo("Enchantment looks hide behind Appearance tab choices (shared with other players).");
        }

        private static HarmonyMethod Hook(string name) =>
            new HarmonyMethod(typeof(Plugin).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static));

        // --- Which slots are hidden -----------------------------------------------------------------------------

        private static int SlotBit(ItemDrop.ItemData item)
        {
            switch (item?.m_shared?.m_itemType)
            {
                case ItemDrop.ItemData.ItemType.Helmet: return 1;
                case ItemDrop.ItemData.ItemType.Chest: return 2;
                case ItemDrop.ItemData.ItemType.Legs: return 4;
                case ItemDrop.ItemData.ItemType.Shoulder: return 8;
                default: return 0;
            }
        }

        // The local player's hidden slots, from its Appearance tab choices: bits 0-3 effects, bits 4-7 textures.
        private static int LocalMask(Player player)
        {
            if (_appearance.Value == Match.Off || player?.m_customData == null) return 0;
            if (!player.m_customData.TryGetValue(LookKey, out var looks) || string.IsNullOrEmpty(looks)) return 0;
            var parts = looks.Split('|');
            var worn = new[] { Helmet(player), Chest(player), Legs(player), Cape(player) };
            var mask = 0;
            for (var i = 0; i < worn.Length && i < parts.Length; i++)
            {
                if (parts[i].Length == 0 || worn[i] == null) continue;
                // CDEV 0.1.22+ saves a colour after the name ("CapeLinen#2"); the colour does not make it another item.
                var look = parts[i];
                var colour = look.LastIndexOf('#');
                if (colour > 0) look = look.Substring(0, colour);
                // "-" = the slot is hidden in the Style tab: nothing of the item is drawn, so neither is its glow.
                if (_appearance.Value == Match.AnyAppearance || look == "-" ||
                    (worn[i].m_dropPrefab != null && worn[i].m_dropPrefab.name == look))
                    mask |= 1 << i;
            }
            return (_effectsBehind.Value ? mask : 0) | (_texturesBehind.Value ? mask << 4 : 0);
        }

        private static int MaskOf(Player player)
        {
            if (player == null) return 0;
            if (player == Player.m_localPlayer) return LocalMask(player);
            var view = player.GetComponent<ZNetView>();
            var zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            return zdo?.GetInt(HiddenKey, 0) ?? 0;
        }

        private static bool Hidden(Player player, ItemDrop.ItemData item, bool texture)
        {
            if (player == null && item != null && Player.m_localPlayer != null && Player.m_localPlayer.IsItemEquiped(item))
                player = Player.m_localPlayer;
            var bit = SlotBit(item) << (texture ? 4 : 0);
            return bit != 0 && (MaskOf(player) & bit) != 0;
        }

        // --- Epic Loot hooks ------------------------------------------------------------------------------------

        private static void DressPlayerPrefix(Player player) => _dressing = player;

        private static void DressVisPrefix(VisEquipment __0) => _dressing = __0 != null ? __0.GetComponent<Player>() : null;

        private static Exception DressFinalizer(Exception __exception)
        {
            _dressing = null;
            return __exception;
        }

        private static void EquipFxPostfix(ItemDrop.ItemData equippedItem, ref string __result, ref FxAttachMode mode)
        {
            if (equippedItem == null) return;
            if (string.IsNullOrEmpty(__result) && _legendaryFallback.Value && equippedItem.IsMagic(out var magic) &&
                magic.IsUniqueLegendary() && string.IsNullOrEmpty(magic.GetLegendaryInfo()?.EquipFx))
            {
                var fx = magic.GetFirstEquipEffect(out var fxMode);
                if (!string.IsNullOrEmpty(fx))
                {
                    __result = fx;
                    mode = fxMode;
                }
            }
            if (string.IsNullOrEmpty(__result)) return;
            // Viewer preferences ("Always") apply on this screen only, not to what is shared with others.
            var viewerHides = !_publishing && (_hideAll.Value || (_hideGlowing.Value && __result == GlowFx));
            if (viewerHides || Hidden(_dressing, equippedItem, false))
                __result = null;
        }

        // --- Auras for other players -------------------------------------------------------------------------------

        // The player-attached auras the local player shows, as decided by Epic Loot (with the rules above).
        private static string LocalAuras(Player player)
        {
            var names = new SortedSet<string>(StringComparer.Ordinal);
            _publishing = true;
            try
            {
                foreach (var item in player.GetInventory().GetEquippedItems())
                {
                    if (item == null || !item.IsMagic()) continue;
                    var name = VisEquipment_Patch.GetEquipFxName(item, out var mode);
                    if (mode == FxAttachMode.Player && !string.IsNullOrEmpty(name)) names.Add(name);
                }
            }
            finally
            {
                _publishing = false;
            }
            return string.Join(",", names);
        }

        // Diagnostics: per worn magic item, what Epic Loot decides, and whether the aura object really is on the player.
        private static string _lastDiagnostics;
        private static BepInEx.Logging.ManualLogSource _log;

        private static void LogDiagnostics(Player player, string auras)
        {
            var lines = new System.Text.StringBuilder();
            foreach (var item in player.GetInventory().GetEquippedItems())
            {
                if (item == null || !item.IsMagic(out var magic)) continue;
                var first = magic.GetFirstEquipEffect(out var firstMode);
                var chosen = VisEquipment_Patch.GetEquipFxName(item, out var chosenMode);
                lines.Append($"\n  {item.m_shared.m_name}: legendary={magic.IsUniqueLegendary()} " +
                             $"legendaryFx='{magic.GetLegendaryInfo()?.EquipFx}' firstEffectFx='{first}'({firstMode}) " +
                             $"shown='{chosen}'({chosenMode}) effects=[{string.Join(",", magic.Effects.Select(e => e.EffectType))}]");
            }
            var attached = string.Join(",", auras.Split(',').Where(n => n.Length > 0)
                .Select(n => $"{n}:{(player.transform.Find(n) != null ? "attached" : "MISSING")}"));
            var text = $"Your auras: [{auras}] on character: [{attached}]{lines}";
            if (text == _lastDiagnostics) return;
            _lastDiagnostics = text;
            _log?.LogInfo(text);
        }

        private static void UpdateRemoteAuras(Player player, string shared)
        {
            if (!RemoteFx.TryGetValue(player, out var attached)) RemoteFx[player] = attached = new Dictionary<string, GameObject>();
            var wanted = new HashSet<string>(
                _shareAuras.Value && !_hideAll.Value && !string.IsNullOrEmpty(shared)
                    ? shared.Split(',').Where(n => n.Length > 0 && !(_hideGlowing.Value && n == GlowFx))
                    : Enumerable.Empty<string>());

            foreach (var name in attached.Keys.ToList())
            {
                if (wanted.Contains(name) && attached[name] != null) continue;
                if (attached[name] != null) UnityEngine.Object.Destroy(attached[name]);
                attached.Remove(name);
            }
            foreach (var name in wanted)
            {
                if (attached.ContainsKey(name)) continue;
                var asset = global::EpicLoot.EpicLoot.LoadAsset<GameObject>(name);
                if (asset == null) continue;
                var previous = ZNetView.m_forceDisableInit;
                ZNetView.m_forceDisableInit = true;
                try
                {
                    var fx = UnityEngine.Object.Instantiate(asset, player.transform, false);
                    fx.name = name;
                    attached[name] = fx;
                }
                finally
                {
                    ZNetView.m_forceDisableInit = previous;
                }
            }
        }

        // SetTextureOverrides(VisEquipment, List<GameObject>, string itemID, ItemData equippedItem)
        private static bool TexturePrefix(VisEquipment __0, ItemDrop.ItemData __3)
        {
            if (_hideTextures.Value) return false;
            var player = __0 != null ? __0.GetComponent<Player>() : null;
            return !Hidden(player, __3, true);
        }

        // --- Sharing and refreshing -----------------------------------------------------------------------------

        private void Update()
        {
            if (Time.time < _nextCheck || Player.m_localPlayer == null) return;
            _nextCheck = Time.time + 0.5f;

            var local = Player.m_localPlayer;
            var mine = LocalMask(local);
            var view = local.GetComponent<ZNetView>();
            var zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (zdo != null && view.IsOwner() && zdo.GetInt(HiddenKey, 0) != mine) zdo.Set(HiddenKey, mine);
            if (zdo != null && view.IsOwner())
            {
                var auras = LocalAuras(local);
                if (zdo.GetString(FxKey, "") != auras) zdo.Set(FxKey, auras);
                LogDiagnostics(local, auras);
            }

            foreach (var player in Player.GetAllPlayers())
            {
                if (player == null) continue;
                if (player != local)
                {
                    var pview = player.GetComponent<ZNetView>();
                    var pzdo = pview != null && pview.IsValid() ? pview.GetZDO() : null;
                    UpdateRemoteAuras(player, pzdo?.GetString(FxKey, "") ?? "");
                }
                var mask = MaskOf(player);
                if (KnownMasks.TryGetValue(player, out var known) && known == mask) continue;
                var first = !KnownMasks.ContainsKey(player);
                KnownMasks[player] = mask;
                if (!first || mask != 0) Redress(player);
            }
            if (KnownMasks.Count > 32)
            {
                var gone = new List<Player>();
                foreach (var p in KnownMasks.Keys) if (p == null) gone.Add(p);
                foreach (var p in gone) KnownMasks.Remove(p);
            }
            foreach (var p in RemoteFx.Keys.Where(p => p == null).ToList()) RemoteFx.Remove(p);
        }

        private static void RefreshAll()
        {
            foreach (var player in Player.GetAllPlayers()) Redress(player);
        }

        // Makes the game re-attach the armor models (so textures are applied again) and rebuild Epic Loot's effects.
        private static void Redress(Player player)
        {
            if (player == null) return;
            try
            {
                var vis = Vis(player);
                if (vis != null)
                    foreach (var field in VisHashFields) Traverse.Create(vis).Field(field).SetValue(-1);
                _refreshFx.Invoke(null, new object[] { player });
            }
            catch (Exception)
            {
                // Looks update on the next equipment change instead.
            }
        }
    }
}
