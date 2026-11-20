// Monster loot drops the moment the creature dies instead of when the corpse disappears, and corpses stay as long as
// you like. Pickups are shown stacked in one small list ("Leather scraps x7") instead of one message per item.
// Light: uses the corpse's own removal timer (just rescheduled) and marks its loot as dropped, so there is no extra
// per-corpse bookkeeping, no scanning of the scene, and no chance of loot dropping twice.
using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace InstantLoot
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.instantloot";
        public const string Name = "Digi's Instant Loot";
        public const string Version = "1.0.3";

        internal static ConfigEntry<bool> Enabled, FeedEnabled, FeedOutline;
        private static ConfigEntry<float> _dropDelay, _corpseTime, _pickupDelay, _pickupRange;
        internal static ConfigEntry<PickupFeed.Corner> FeedCorner;
        internal static ConfigEntry<int> FeedX, FeedY, FeedWidth, FeedLines, FeedFontSize;
        internal static ConfigEntry<float> FeedOpacity, FeedLinger;
        internal static ConfigEntry<Color> FeedTextColor, FeedAmountColor, FeedOutlineColor;

        private static readonly Action<Ragdoll, Vector3> SpawnLoot =
            AccessTools.MethodDelegate<Action<Ragdoll, Vector3>>(AccessTools.Method(typeof(Ragdoll), "SpawnLoot"));
        private static readonly AccessTools.FieldRef<Ragdoll, ZNetView> RagdollView = AccessTools.FieldRefAccess<Ragdoll, ZNetView>("m_nview");

        private static bool _spawningLoot;
        private static readonly List<ItemDrop> Waiting = new List<ItemDrop>();
        private static readonly List<float> WaitUntil = new List<float>();
        private static float _vanillaRange = -1f;

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
            Enabled = Config.Bind("1 - Loot", "Enabled", true, "Monsters drop their loot the moment they die, instead of when the body fades.");
            _dropDelay = Config.Bind("1 - Loot", "Drop delay (s)", 0.05f,
                new ConfigDescription("Seconds between a creature's death and its loot dropping.", new AcceptableValueRange<float>(0f, 5f)));
            _corpseTime = Config.Bind("1 - Loot", "Corpse time (s)", 60f,
                new ConfigDescription("How long dead creatures stay before they fade, in seconds.", new AcceptableValueRange<float>(1f, 600f)));
            _pickupDelay = Config.Bind("2 - Pickup", "Auto-pickup delay (s)", 1f,
                new ConfigDescription("Seconds before monster loot can be picked up automatically (lets it land first). 0 = right away.",
                    new AcceptableValueRange<float>(0f, 10f)));
            _pickupRange = Config.Bind("2 - Pickup", "Auto-pickup range (m)", 2.5f,
                new ConfigDescription("How close items must be to be picked up automatically, in meters. Normal is 2.", new AcceptableValueRange<float>(1f, 10f)));
            FeedEnabled = Config.Bind("3 - Pickup list", "Enabled", true, "Show pickups stacked in a small list (e.g. \"Leather scraps x7\") instead of one message per item.");
            FeedCorner = Config.Bind("3 - Pickup list", "Corner", PickupFeed.Corner.BottomRight, "Which corner of the screen the list is in.");
            FeedX = Config.Bind("3 - Pickup list", "Offset X", 20, "Distance from the left or right edge of the screen, in pixels.");
            FeedY = Config.Bind("3 - Pickup list", "Offset Y", 20, "Distance from the top or bottom edge of the screen, in pixels.");
            FeedWidth = Config.Bind("3 - Pickup list", "Width", 250, new ConfigDescription("Width of the list, in pixels.", new AcceptableValueRange<int>(100, 800)));
            FeedLines = Config.Bind("3 - Pickup list", "Max lines", 5, new ConfigDescription("How many pickups are shown at once.", new AcceptableValueRange<int>(1, 20)));
            FeedFontSize = Config.Bind("3 - Pickup list", "Font size", 15, new ConfigDescription("Text size.", new AcceptableValueRange<int>(8, 40)));
            FeedOpacity = Config.Bind("3 - Pickup list", "Background opacity", 0f, new ConfigDescription("Background behind the list: 0 = none, 1 = solid black.", new AcceptableValueRange<float>(0f, 1f)));
            FeedLinger = Config.Bind("3 - Pickup list", "Seconds shown", 8f, new ConfigDescription("How long each pickup stays in the list, in seconds.", new AcceptableValueRange<float>(1f, 30f)));
            FeedOutline = Config.Bind("3 - Pickup list", "Text outline", true, "Dark edge around the text so it stays readable on bright backgrounds.");
            FeedTextColor = Config.Bind("3 - Pickup list", "Text color", Color.white, "Color of the item names.");
            FeedAmountColor = Config.Bind("3 - Pickup list", "Amount color", new Color(0.95f, 0.9f, 0.75f), "Color of the amounts (x7).");
            FeedOutlineColor = Config.Bind("3 - Pickup list", "Outline color", Color.black, "Color of the text edge (fully transparent = no edge).");
            _pickupRange.SettingChanged += (_, __) => ApplyRange(Player.m_localPlayer);
            Enabled.SettingChanged += (_, __) => ApplyRange(Player.m_localPlayer);
            if (gameObject.GetComponent<PickupFeed>() == null) gameObject.AddComponent<PickupFeed>();
            PatchModule(typeof(Plugin));
        }

        // --- Loot and corpses ----------------------------------------------------------------------------------

        [HarmonyPatch(typeof(Ragdoll), "Awake")]
        [HarmonyPostfix]
        private static void RagdollAwake(Ragdoll __instance)
        {
            if (!Enabled.Value) return;
            __instance.CancelInvoke("DestroyNow");
            __instance.InvokeRepeating("DestroyNow", CorpseLeft(RagdollView(__instance)), 1f);
            __instance.StartCoroutine(DropLoot(__instance));
        }

        // The corpse's time counts from the death, kept on the corpse itself. Awake runs again every time the corpse
        // loads (you walk away and come back, or log in again), and the game starts its timer over each time: with a
        // longer corpse time, a corpse where you often pass could stay for tens of minutes. A corpse whose time ran out
        // while it was not loaded goes right away.
        private const string DiedKey = "digi_corpse_died";

        private static float CorpseLeft(ZNetView view)
        {
            var zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (zdo == null || ZNet.instance == null) return _corpseTime.Value;
            var now = ZNet.instance.GetTimeSeconds();
            var died = zdo.GetLong(DiedKey) / 1000.0;
            if (died <= 0.0 || died > now)
            {
                if (view.IsOwner()) zdo.Set(DiedKey, (long)(now * 1000.0));
                return _corpseTime.Value;
            }
            return Mathf.Max(0.5f, _corpseTime.Value - (float)(now - died));
        }

        // Epic Loot marks the corpse with these and rolls its magic items whenever the corpse drops its loot.
        private const string EpicLootName = "characterName", EpicLootLuckyRolls = "el-lky-rolls";

        private static IEnumerator DropLoot(Ragdoll ragdoll)
        {
            // Always wait at least one frame: the corpse gets its loot list (and Epic Loot its mark) right after Awake.
            if (_dropDelay.Value > 0f) yield return new WaitForSeconds(_dropDelay.Value);
            else yield return null;
            var view = RagdollView(ragdoll);
            if (view == null || !view.IsValid() || !view.IsOwner()) yield break; // the owner's game drops it
            var zdo = view.GetZDO();
            var epicLoot = !string.IsNullOrEmpty(zdo.GetString(EpicLootName));
            if (zdo.GetInt(ZDOVars.s_drops) <= 0 && !epicLoot) yield break;
            var center = ragdoll.m_lootSpawnJoint != null ? ragdoll.m_lootSpawnJoint.transform.position : ragdoll.GetAverageBodyPosition();
            _spawningLoot = true;
            try
            {
                SpawnLoot(ragdoll, center);
            }
            finally
            {
                _spawningLoot = false;
            }
            // The corpse's own removal later finds nothing left to drop: no normal loot, and no second Epic Loot roll.
            zdo.Set(ZDOVars.s_drops, 0);
            if (epicLoot)
            {
                zdo.Set(EpicLootName, "");
                zdo.Set(EpicLootLuckyRolls, 0);
            }
        }

        [HarmonyPatch(typeof(ItemDrop), "Awake")]
        [HarmonyPostfix]
        private static void ItemAwake(ItemDrop __instance)
        {
            if (!_spawningLoot || _pickupDelay.Value <= 0f || !__instance.m_autoPickup) return;
            __instance.m_autoPickup = false;
            Waiting.Add(__instance);
            WaitUntil.Add(Time.time + _pickupDelay.Value);
        }

        private void Update()
        {
            for (var i = Waiting.Count - 1; i >= 0; i--)
            {
                if (Waiting[i] != null && Time.time < WaitUntil[i]) continue;
                if (Waiting[i] != null) Waiting[i].m_autoPickup = true;
                Waiting.RemoveAt(i);
                WaitUntil.RemoveAt(i);
            }
        }

        // --- Pickup range --------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        [HarmonyPostfix]
        private static void Spawned(Player __instance)
        {
            if (__instance == Player.m_localPlayer) ApplyRange(__instance);
        }

        private static void ApplyRange(Player player)
        {
            if (player == null) return;
            if (_vanillaRange < 0f) _vanillaRange = player.m_autoPickupRange;
            player.m_autoPickupRange = Enabled.Value ? _pickupRange.Value : _vanillaRange;
        }

        // --- Pickup list ---------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(Character), nameof(Character.ShowPickupMessage))]
        [HarmonyPrefix]
        private static bool PickupMessage(Character __instance, ItemDrop.ItemData item, int amount)
        {
            if (!FeedEnabled.Value || __instance != Player.m_localPlayer || PickupFeed.Instance == null) return true;
            PickupFeed.Instance.Add(item, amount);
            return false;
        }
    }

    // A small stacked list of recent pickups, drawn only while it has lines.
    public class PickupFeed : MonoBehaviour
    {
        public enum Corner { BottomRight, BottomLeft, TopRight, TopLeft }

        private class Line
        {
            public string Name, AmountText;
            public int Amount;
            public Sprite Icon;
            public float Age;
        }

        public static PickupFeed Instance { get; private set; }
        private readonly List<Line> _lines = new List<Line>();
        private GUIStyle _name, _amount;
        private int _styleSize = -1;
        private Texture2D _pixel;

        private void Awake()
        {
            Instance = this;
            _pixel = new Texture2D(1, 1);
            _pixel.SetPixel(0, 0, Color.white);
            _pixel.Apply();
        }

        public void Add(ItemDrop.ItemData item, int amount)
        {
            if (item?.m_shared == null || amount <= 0) return;
            var name = Localization.instance != null ? Localization.instance.Localize(item.m_shared.m_name) : item.m_shared.m_name;
            foreach (var line in _lines)
            {
                if (line.Name != name) continue;
                line.Amount += amount;
                line.AmountText = "x" + line.Amount;
                line.Age = 0f;
                _lines.Remove(line);
                _lines.Add(line);
                return;
            }
            var icons = item.m_shared.m_icons;
            _lines.Add(new Line
            {
                Name = name,
                Amount = amount,
                AmountText = "x" + amount,
                Icon = icons != null && icons.Length > 0 ? icons[Mathf.Clamp(item.m_variant, 0, icons.Length - 1)] : null,
            });
            while (_lines.Count > Plugin.FeedLines.Value) _lines.RemoveAt(0);
        }

        private void Update()
        {
            if (_lines.Count == 0) return;
            var linger = Plugin.FeedLinger.Value;
            for (var i = _lines.Count - 1; i >= 0; i--)
            {
                _lines[i].Age += Time.unscaledDeltaTime;
                if (_lines[i].Age >= linger) _lines.RemoveAt(i);
            }
        }

        private void OnGUI()
        {
            if (_lines.Count == 0 || !Plugin.FeedEnabled.Value || Player.m_localPlayer == null || Hud.IsUserHidden()) return;
            var size = Plugin.FeedFontSize.Value;
            if (_name == null || _styleSize != size)
            {
                _styleSize = size;
                _name = new GUIStyle(GUI.skin.label) { fontSize = size, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip, wordWrap = false };
                _amount = new GUIStyle(_name) { alignment = TextAnchor.MiddleRight, clipping = TextClipping.Overflow };
            }
            var lineHeight = size + 9f;
            var width = (float)Plugin.FeedWidth.Value;
            var height = Plugin.FeedLines.Value * lineHeight + 8f;
            var corner = Plugin.FeedCorner.Value;
            var right = corner == Corner.BottomRight || corner == Corner.TopRight;
            var bottom = corner == Corner.BottomRight || corner == Corner.BottomLeft;
            var x = right ? Screen.width - width - Plugin.FeedX.Value : Plugin.FeedX.Value;
            var y = bottom ? Screen.height - height - Plugin.FeedY.Value : Plugin.FeedY.Value;
            var oldColor = GUI.color;

            if (Plugin.FeedOpacity.Value > 0f)
            {
                GUI.color = new Color(0.04f, 0.05f, 0.06f, Plugin.FeedOpacity.Value);
                GUI.DrawTexture(new Rect(x, y, width, height), _pixel);
            }

            var linger = Plugin.FeedLinger.Value;
            var rowY = bottom ? y + height - 4f - lineHeight : y + 4f;
            for (var i = _lines.Count - 1; i >= 0; i--)
            {
                var line = _lines[i];
                var fade = Mathf.Clamp01((linger - line.Age) / 0.8f);
                var rowX = x + 6f;
                if (line.Icon != null && line.Icon.texture != null)
                {
                    var tex = line.Icon.texture;
                    var r = line.Icon.textureRect;
                    GUI.color = new Color(1f, 1f, 1f, fade);
                    GUI.DrawTextureWithTexCoords(new Rect(rowX, rowY + 3f, lineHeight - 6f, lineHeight - 6f), tex,
                        new Rect(r.x / tex.width, r.y / tex.height, r.width / tex.width, r.height / tex.height));
                    rowX += lineHeight;
                }
                var amountWidth = size * 3.4f + 16f;
                Label(new Rect(rowX, rowY, x + width - rowX - amountWidth - 6f, lineHeight), line.Name, _name, Plugin.FeedTextColor.Value, fade);
                Label(new Rect(x + width - amountWidth - 6f, rowY, amountWidth, lineHeight), line.AmountText, _amount, Plugin.FeedAmountColor.Value, fade);
                rowY += bottom ? -lineHeight : lineHeight;
            }
            GUI.color = oldColor;
        }

        private static void Label(Rect rect, string text, GUIStyle style, Color color, float fade)
        {
            GUI.color = Color.white;
            if (Plugin.FeedOutline.Value)
            {
                var outline = Plugin.FeedOutlineColor.Value;
                outline.a *= fade;
                style.normal.textColor = outline;
                GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text, style);
                GUI.Label(new Rect(rect.x - 1f, rect.y - 1f, rect.width, rect.height), text, style);
            }
            color.a *= fade;
            style.normal.textColor = color;
            GUI.Label(rect, text, style);
        }
    }
}
