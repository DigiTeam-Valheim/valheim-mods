// Planting Tools: plant a whole grid of crops, saplings or berry bushes with one click of the cultivator, without the
// game slowing down. Replaces Plant Easily (Advize), which placed a 50 x 50 grid two plants per frame (about a minute),
// kept 2,500 copies of the plant as preview objects (each checked again only 20 per frame), refused the next click
// until the last grid was done, and took seeds, stamina and durability once per plant (each seed taken makes the game go
// through the whole inventory). Here:
// - the preview is drawn straight to the graphics card (Preview.cs), the grid checked in one go (Grid.cs);
// - a click pays once for the whole grid (seeds from what you carry, stamina, cultivator wear) and hands the plants to a
//   queue that plants a few milliseconds' worth per frame (Planter.cs); the next grid can be clicked at once;
// - only your own click makes a grid (Player.TryPlacePiece): other mods that plant one at a time are left alone.
// Ctrl + arrow keys or Ctrl + mouse wheel set the grid size, Alt + mouse wheel the spacing (shown in the count line
// meanwhile); F8 turns the grid on or off, F10 the lining up with a field
// already there, F7 Fill field (every free spot of the cultivated patch under the cursor). Plant Easily's settings are
// copied once. Plant Easily, if installed too, is switched off.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace PlantingTools
{
    // A key held with another key: either side of the keyboard counts.
    public enum Modifier { Ctrl, Shift, Alt }

    public class Plugin : DigiCore.DigiModule
    {
        private const string PlantEasilyGuid = "advize.PlantEasily";
        private const string PlantEasilyConfig = "advize.PlantEasily.cfg";

        internal static BepInEx.Logging.ManualLogSource Log;
        private static bool _running;

        private static ConfigEntry<bool> _gridOn, _snapOn, _fillOn, _worldAligned, _tighter, _onlyWhereItGrows, _allOrNothing, _stamina, _durability,
            _randomRotation, _preview, _count;
        private static ConfigEntry<int> _rows, _columns, _maxPlants;
        private static ConfigEntry<float> _extraCrop, _extraSapling, _pickableSpacing, _scatter, _tilt, _budget, _fillReach;
        private static ConfigEntry<KeyCode> _toggleKey, _snapKey, _fillKey, _moreColumns, _fewerColumns, _moreRows, _fewerRows;
        private static ConfigEntry<Modifier> _sizeModifier, _wheelModifier, _spacingModifier;
        private static float _spacingShownUntil; // the count line shows the spacing a moment after it changed

        internal static bool GridOn => _gridOn.Value;
        internal static bool SnapOn => _snapOn.Value;
        internal static bool FillOn => _fillOn.Value;
        internal static float FillReach => _fillReach.Value;
        internal static int Rows => _rows.Value;
        internal static int Columns => _columns.Value;
        internal static int MaxPlants => _maxPlants.Value;
        internal static bool WorldAligned => _worldAligned.Value;
        internal static bool ShowPreview => _preview.Value;

        private static TextMeshProUGUI _label;
        private static GameObject _labelRoot;

        // one click of yours: the grid's plants, paid for and queued at the end of the frame
        private static List<Vector3> _clicked;
        private static Kind _clickedKind;
        private static Quaternion _clickedRotation;
        private static int _rotationBefore;
        private static bool _keepRotation;

        private void Awake()
        {
            _instance = this;
            Log = Logger;
            _gridOn = Config.Bind("General", "Grid on", true,
                "Plant a whole grid with each click of the cultivator. The toggle key switches it while you play.");
            _toggleKey = Config.Bind("General", "Toggle key", KeyCode.F8, "Turns the grid on or off.");
            _snapOn = Config.Bind("General", "Line up with fields", true,
                "Aiming next to plants of the same kind (planted, grown or still being planted), the grid continues their " +
                "field: same lines and spacing, starting on the free spot next to them nearest your aim, and growing away from " +
                "them. The snap key switches it while you play.");
            _snapKey = Config.Bind("General", "Snap key", KeyCode.F10, "Turns lining up with fields on or off.");
            _fillOn = Config.Bind("General", "Fill field", false,
                "Instead of rows x columns, the grid is every free spot of the cultivated patch under your aim (up to the most " +
                "plants per click): one click plants a whole field. The fill key switches it while you play.");
            _fillKey = Config.Bind("General", "Fill key", KeyCode.F7, "Turns Fill field on or off.");
            _fillReach = Config.Bind("General", "Fill reach (m)", 40f, new ConfigDescription(
                "Fill field plants no further than this from your aim.", new AcceptableValueRange<float>(5f, 200f)));

            _rows = Config.Bind("Grid", "Rows", 2, new ConfigDescription("Rows of plants (away from you).", new AcceptableValueRange<int>(1, 200)));
            _columns = Config.Bind("Grid", "Columns", 2, new ConfigDescription("Plants per row (to the right).", new AcceptableValueRange<int>(1, 200)));
            _maxPlants = Config.Bind("Grid", "Most plants per click", 10000, new ConfigDescription(
                "A grid never has more plants than this, whatever its size.", new AcceptableValueRange<int>(2, 40000)));
            _worldAligned = Config.Bind("Grid", "Line up with the world", true,
                "Rows run north and columns east (turned with the mouse wheel like any piece). Off: rows run away from you.");
            _tighter = Config.Bind("Grid", "Tighter spacing", false,
                "Crops stand closer together: just enough room to grow (1.1 x the room a plant needs, plus its own width) " +
                "instead of twice the room. Saplings keep their spacing.");
            _extraCrop = Config.Bind("Grid", "Extra crop spacing (m)", 0f, new ConfigDescription(
                "Added to the space between crops; below 0 packs them closer (they may then not grow).", new AcceptableValueRange<float>(-1f, 5f)));
            _extraSapling = Config.Bind("Grid", "Extra sapling spacing (m)", 0f, new ConfigDescription(
                "Added to the space between tree saplings.", new AcceptableValueRange<float>(-2f, 10f)));
            _pickableSpacing = Config.Bind("Grid", "Bush spacing (m)", 1f, new ConfigDescription(
                "Space between pickables (bushes, mushrooms, stones) that have no spacing of their own in Bush spacing.", new AcceptableValueRange<float>(0.25f, 10f)));

            _onlyWhereItGrows = Config.Bind("Planting", "Only where it can grow", true,
                "Nothing is planted where the plant would not grow (no cultivated ground, wrong biome, no room, a roof above, " +
                "too hot or cold); the grid shows those spots red. Off: such spots are planted too.");
            _allOrNothing = Config.Bind("Planting", "All or nothing", false,
                "A click plants nothing if any spot of the grid cannot be planted or has no seeds.");
            _stamina = Config.Bind("Planting", "Use stamina", true, "Every plant of a grid costs the stamina one plant costs.");
            _durability = Config.Bind("Planting", "Use cultivator durability", true, "Every plant of a grid wears the cultivator like one plant.");
            _randomRotation = Config.Bind("Planting", "Random rotation", true, "Each plant of a grid is turned a random way, so a field looks less even.");
            _scatter = Config.Bind("Planting", "Scatter (m)", 0f, new ConfigDescription(
                "Each plant is moved up to this far off its spot, for a less even look. Keep it small (0.01 to 0.05).", new AcceptableValueRange<float>(0f, 0.5f)));
            _tilt = Config.Bind("Planting", "Tilt (degrees)", 0f, new ConfigDescription(
                "Each plant leans up to this far, for a less even look (1 to 3).", new AcceptableValueRange<float>(0f, 15f)));
            _budget = Config.Bind("Planting", "Planting time per frame (ms)", 4f, new ConfigDescription(
                "How long each frame may spend creating plants of a grid. Higher plants faster, lower keeps the frame rate " +
                "smoother while a big grid is planted.", new AcceptableValueRange<float>(0.5f, 50f)));

            _sizeModifier = Config.Bind("Keys", "Size keys modifier", Modifier.Ctrl, "Hold this (left or right) with the arrow keys below to change the grid size.");
            _moreColumns = Config.Bind("Keys", "More columns", KeyCode.RightArrow, "With the modifier: one plant more per row.");
            _fewerColumns = Config.Bind("Keys", "Fewer columns", KeyCode.LeftArrow, "With the modifier: one plant less per row.");
            _moreRows = Config.Bind("Keys", "More rows", KeyCode.UpArrow, "With the modifier: one row more.");
            _fewerRows = Config.Bind("Keys", "Fewer rows", KeyCode.DownArrow, "With the modifier: one row less.");
            _wheelModifier = Config.Bind("Keys", "Mouse wheel modifier", Modifier.Ctrl,
                "Hold this and turn the mouse wheel to make the grid bigger or smaller (rows and columns together).");
            _spacingModifier = Config.Bind("Keys", "Spacing wheel modifier", Modifier.Alt,
                "Hold this and turn the mouse wheel to put the plants further apart or closer together (crops 0.1 m a step, " +
                "saplings 0.25 m, bushes 0.1 m; kept in the spacing settings).");

            _preview = Config.Bind("Display", "Show the grid", true, "See-through plants show where the grid goes and which spots work.");
            _count = Config.Bind("Display", "Show the count", true, "A line below the crosshair: how many plants the click plants, and why spots are left out.");
            Harvest.Bind(Config);
            Seeds.Bind(Config);

            _tighter.SettingChanged += (s, e) => Respace();
            _extraCrop.SettingChanged += (s, e) => Respace();
            _extraSapling.SettingChanged += (s, e) => Respace();
            _pickableSpacing.SettingChanged += (s, e) => Respace();

            var old = Path.Combine(Paths.ConfigPath, PlantEasilyConfig);
            if (File.Exists(old) && FirstTime("from plant easily")) FromPlantEasily(old);
            // Planting Tools first ran inside World and Performance (a test build): its settings there carry over once
            var world = Path.Combine(Paths.ConfigPath, "digi.valheim.world.cfg");
            if (File.Exists(world) && FirstTime("from world and performance")) FromWorld(world);
        }

        private void Start()
        {
            SwitchOffReplaced(PlantEasilyGuid, "Plant Easily"); // installed too: it goes off, ours runs
            PatchModule(typeof(Plugin));
            PatchModule(typeof(Harvest));
            PatchModule(typeof(Seeds));
            Seeds.Known();
            _running = true;
        }

        private void OnDestroy()
        {
            if (_running) Planter.Finish();
            _running = false;
            Grid.Clear();
            Preview.Clear();
            if (_labelRoot != null) Destroy(_labelRoot);
            _labelRoot = null;
            _label = null;
        }

        private void Update()
        {
            if (!_running) return;
            Planter.Work(_budget.Value);
            Harvest.RememberCrouch();
            var player = Player.m_localPlayer;
            if (player == null || (Chat.instance != null && Chat.instance.HasFocus()) || global::Console.IsVisible() || TextInput.IsVisible() ||
                Menu.IsVisible() || InventoryGui.IsVisible() || (Hud.instance != null && Hud.instance.m_buildUi != null && Hud.instance.m_buildUi.SearchFieldFocused)) return; // also the build menu's search box
            if (Grid.Kind == null || !player.InPlaceMode()) return;
            if (ZInput.GetKeyDown(_toggleKey.Value))
            {
                _gridOn.Value = !_gridOn.Value;
                player.Message(MessageHud.MessageType.TopLeft, "Planting grid " + (_gridOn.Value ? "on" : "off"));
            }
            if (ZInput.GetKeyDown(_snapKey.Value))
            {
                _snapOn.Value = !_snapOn.Value;
                player.Message(MessageHud.MessageType.TopLeft, "Line up with fields " + (_snapOn.Value ? "on" : "off"));
            }
            if (ZInput.GetKeyDown(_fillKey.Value))
            {
                _fillOn.Value = !_fillOn.Value;
                player.Message(MessageHud.MessageType.TopLeft, "Fill field " + (_fillOn.Value ? "on" : "off"));
            }
            if (Held(_sizeModifier.Value))
            {
                if (ZInput.GetKeyDown(_moreColumns.Value)) _columns.Value++;
                if (ZInput.GetKeyDown(_fewerColumns.Value)) _columns.Value--;
                if (ZInput.GetKeyDown(_moreRows.Value)) _rows.Value++;
                if (ZInput.GetKeyDown(_fewerRows.Value)) _rows.Value--;
            }
        }

        private void LateUpdate()
        {
            if (!_running) return;
            if (_clicked != null) PlantClicked();
            var player = Player.m_localPlayer;
            if (Grid.Active && (player == null || !player.InPlaceMode() || player.m_placementGhost == null || !player.m_placementGhost.activeInHierarchy))
                Grid.Active = false; // the cultivator was put away
            Preview.Draw();
            ShowCount();
        }

        // ---- the grid follows the game's ghost ----

        // Crops get a random turn from the game each time the ghost is made and after each placement; a grid that turns
        // with it would point another way after every click, so the turn you chose is kept (as Plant Easily did).
        private static int _turnBefore;

        [HarmonyPatch(typeof(Player), "SetupPlacementGhost")]
        [HarmonyPrefix]
        private static void BeforeGhostMade(Player __instance) => _turnBefore = __instance.m_placeRotation;

        [HarmonyPatch(typeof(Player), "SetupPlacementGhost")]
        [HarmonyPostfix]
        private static void GhostMade(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            if (__instance.m_buildPieces != null)
            {
                BindBushes(__instance.m_buildPieces);
                Harvest.Learn(__instance.m_buildPieces);
            }
            Grid.Kind = __instance.m_buildPieces != null ? Kind.Of(__instance.m_buildPieces.GetSelectedPrefab()) : null;
            if (Grid.Kind != null)
            {
                Preview.Build(__instance.m_placementGhost);
                if (_gridOn.Value) __instance.m_placeRotation = _turnBefore;
            }
            else Preview.Clear();
        }

        [HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
        [HarmonyPostfix]
        private static void GhostMoved(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            Grid.Update(__instance, __instance.m_placementGhost);
        }

        // ---- the click ----

        [HarmonyPatch(typeof(Player), "TryPlacePiece")]
        [HarmonyPrefix]
        private static bool BeforeClick(Player __instance, Piece piece, ref bool __result)
        {
            if (__instance != Player.m_localPlayer || !Grid.Active || Grid.Kind == null || piece == null || piece.gameObject != Grid.Kind.Prefab) return true;
            _turnBefore = __instance.m_placeRotation;
            // a spot still waiting in the planting queue is never planted twice
            if (Grid.Count > 0 && (Grid.Spots[0] == Spot.Taken || _onlyWhereItGrows.Value && Grid.Bad(Grid.Spots[0])))
                return Refuse(__instance, Grid.Spots[0], ref __result);
            if (_allOrNothing.Value)
                for (var i = 0; i < Grid.Count; i++)
                {
                    var spot = Grid.Spots[i];
                    if (spot != Spot.Ok && !(spot == Spot.NoSeeds && __instance.m_noPlacementCost)) return Refuse(__instance, spot, ref __result);
                }
            return true;
        }

        [HarmonyPatch(typeof(Player), "TryPlacePiece")]
        [HarmonyPostfix]
        private static void AfterClick(Player __instance, Piece piece, bool __result)
        {
            if (__instance != Player.m_localPlayer || !Grid.Active || Grid.Kind == null || piece == null || piece.gameObject != Grid.Kind.Prefab) return;
            __instance.m_placeRotation = _turnBefore; // the game turned it randomly after placing
            if (!__result) return;
            var cells = new List<Vector3>();
            for (var i = 1; i < Grid.Count; i++)
            {
                var spot = Grid.Spots[i];
                if (spot == Spot.Ok || spot == Spot.NoSeeds || (!_onlyWhereItGrows.Value && Grid.Bad(spot) && spot != Spot.Invalid && spot != Spot.Blocked))
                    cells.Add(Grid.Positions[i]);
            }
            _clicked = cells;
            _clickedKind = Grid.Kind;
            _clickedRotation = Grid.Rotation;
        }

        // At the end of the frame (after the game paid for the first plant): pay for the rest at once and queue them.
        private static void PlantClicked()
        {
            var cells = _clicked;
            var kind = _clickedKind;
            _clicked = null;
            var player = Player.m_localPlayer;
            if (player == null || kind == null || cells.Count == 0) return;
            var paid = ExtraPlantsPaid(player, kind); // after the first plant was paid: one more than before
            var free = paid == int.MaxValue;
            var n = free ? cells.Count : Mathf.Min(cells.Count, paid + 1);
            if (n > 0 && !free) n = Pay(player, kind, n);
            if (n < cells.Count) player.Message(MessageHud.MessageType.Center, $"Seeds for {n + 1} of {cells.Count + 1} plants");
            if (n <= 0) return;
            if (_stamina.Value) player.UseStamina(player.GetBuildStamina() * n);
            var tool = player.GetRightItem();
            if (_durability.Value && tool != null && tool.m_shared.m_useDurability)
                tool.m_durability = Mathf.Max(0f, tool.m_durability - player.GetPlaceDurability(tool) * Game.m_durabilityRate * n);
            Game.instance.IncrementPlayerStat(PlayerStatType.Builds, n);
            Planter.Owner(player);
            var scatter = _scatter.Value;
            var tilt = _tilt.Value;
            for (var i = 0; i < n; i++)
            {
                var pos = cells[i];
                if (scatter > 0f)
                {
                    pos.x += UnityEngine.Random.Range(-scatter, scatter);
                    pos.z += UnityEngine.Random.Range(-scatter, scatter);
                }
                var rot = _randomRotation.Value ? Quaternion.Euler(0f, 22.5f * UnityEngine.Random.Range(0, 16), 0f) : _clickedRotation;
                if (tilt > 0f) rot *= Quaternion.Euler(UnityEngine.Random.Range(-tilt, tilt), 0f, UnityEngine.Random.Range(-tilt, tilt));
                Planter.Add(kind.Prefab, pos, rot);
            }
        }

        private static bool Refuse(Player player, Spot spot, ref bool result)
        {
            player.Message(MessageHud.MessageType.Center, Message(spot));
            result = false;
            return false;
        }

        private static string Message(Spot spot)
        {
            switch (spot)
            {
                case Spot.NoSeeds: return "$msg_missingrequirement";
                case Spot.NotCultivated: return "$piece_plant_notcultivated";
                case Spot.WrongBiome: return "$piece_plant_wrongbiome";
                case Spot.NoSpace: case Spot.Taken: return "$piece_plant_nospace";
                case Spot.NoSun: return "$piece_plant_nosun";
                case Spot.TooHot: return "$piece_plant_toohot";
                case Spot.TooCold: return "$piece_plant_toocold";
                case Spot.Blocked: return "$msg_privatezone";
                default: return "$msg_invalidplacement";
            }
        }

        // ---- the mouse wheel with a modifier sizes or spaces the grid instead of turning the plant ----

        [HarmonyPatch(typeof(Player), "UpdatePlacement")]
        [HarmonyPrefix]
        private static void BeforePlacementInput(Player __instance)
        {
            _keepRotation = false;
            if (__instance != Player.m_localPlayer || Grid.Kind == null || !__instance.InPlaceMode()) return;
            var spacing = Held(_spacingModifier.Value);
            var size = !spacing && (Held(_wheelModifier.Value) || Held(_sizeModifier.Value));
            if (!spacing && !size) return;
            _keepRotation = true;
            _rotationBefore = __instance.m_placeRotation;
            var wheel = ZInput.GetMouseScrollWheel();
            if (wheel == 0f) return;
            var step = wheel > 0f ? 1 : -1;
            if (spacing)
            {
                Space(Grid.Kind, step);
                return;
            }
            _rows.Value += step;
            _columns.Value += step;
        }

        // One wheel step wider or closer: a bush's own spacing, else the extra spacing of saplings or crops.
        private static void Space(Kind kind, int step)
        {
            if (kind.Plant == null)
            {
                var bush = Bush(kind.Name, kind.Piece);
                bush.Value = Mathf.Round((bush.Value + 0.1f * step) * 100f) / 100f;
            }
            else if (kind.Tree) _extraSapling.Value = Mathf.Round((_extraSapling.Value + 0.25f * step) * 100f) / 100f;
            else _extraCrop.Value = Mathf.Round((_extraCrop.Value + 0.1f * step) * 100f) / 100f;
            Respace();
            _spacingShownUntil = Time.unscaledTime + 3f;
        }

        [HarmonyPatch(typeof(Player), "UpdatePlacement")]
        [HarmonyPostfix]
        private static void AfterPlacementInput(Player __instance)
        {
            if (!_keepRotation || __instance != Player.m_localPlayer) return;
            __instance.m_placeRotation = _rotationBefore;
            __instance.m_scrollCurrAmount = 0f;
        }

        // ---- leaving the world with plants still queued: plant them before the game saves ----

        [HarmonyPatch(typeof(Game), "Shutdown")]
        [HarmonyPrefix]
        private static void BeforeShutdown(Game __instance)
        {
            if (!__instance.m_shuttingDown && Planter.Left > 0) Planter.Finish();
        }

        // ---- spacing and seeds ----

        // Plant Easily's spacing: saplings 2.2 x the room they need; crops twice that room, or with "tighter spacing" 1.1 x
        // plus their own width; pickables their harvest radius or the bush spacing.
        internal static float SpacingFor(Kind kind)
        {
            if (kind.Plant == null) return Bush(kind.Name, kind.Piece).Value;
            if (kind.Tree) return Mathf.Max(0.1f, kind.GrowRadius * 2.2f + _extraSapling.Value);
            var spacing = _tighter.Value ? kind.GrowRadius * 1.1f + kind.CapsuleRadius() : kind.GrowRadius * 2f;
            return Mathf.Max(0.1f, spacing + _extraCrop.Value);
        }

        // A modifier counts on either side of the keyboard.
        internal static bool Held(Modifier key)
        {
            switch (key)
            {
                case Modifier.Shift: return ZInput.GetKey(KeyCode.LeftShift) || ZInput.GetKey(KeyCode.RightShift);
                case Modifier.Alt: return ZInput.GetKey(KeyCode.LeftAlt) || ZInput.GetKey(KeyCode.RightAlt);
                default: return ZInput.GetKey(KeyCode.LeftControl) || ZInput.GetKey(KeyCode.RightControl);
            }
        }

        // Plant Easily's keys ("RightControl", "LeftShift", ...) as one of the three.
        internal static void Mod(Dictionary<string, string> cfg, string key, ConfigEntry<Modifier> entry)
        {
            if (!cfg.TryGetValue(key, out var v)) return;
            v = v.Trim();
            if (v.EndsWith("Control")) entry.Value = Modifier.Ctrl;
            else if (v.EndsWith("Shift")) entry.Value = Modifier.Shift;
            else if (v.EndsWith("Alt")) entry.Value = Modifier.Alt;
        }

        private static void Respace()
        {
            if (Grid.Kind != null) Grid.Kind.Spacing = SpacingFor(Grid.Kind);
        }

        // ---- spacing per bush (Plant Easily's defaults; its own values copied once) ----

        private static readonly Dictionary<string, ConfigEntry<float>> Bushes = new Dictionary<string, ConfigEntry<float>>();
        private static readonly Dictionary<string, float> BushDefaults = new Dictionary<string, float>
        {
            { "Pickable_Dandelion", 0.75f }, { "Pickable_Fiddlehead", 1f }, { "Pickable_Mushroom", 0.5f }, { "Pickable_Mushroom_blue", 0.5f },
            { "Pickable_Mushroom_yellow", 0.5f }, { "Pickable_SmokePuff", 1f }, { "Pickable_Thistle", 0.75f }, { "BlueberryBush", 1.5f },
            { "RaspberryBush", 1.5f }, { "CloudberryBush", 1f }, { "LingonberryBush", 1.5f },
        };
        private static Plugin _instance;

        private static ConfigEntry<float> Bush(string prefab, Piece piece)
        {
            if (Bushes.TryGetValue(prefab, out var entry)) return entry;
            var start = BushDefaults.TryGetValue(prefab, out var d) ? d : piece != null && piece.m_harvestRadius > 0f ? piece.m_harvestRadius : _pickableSpacing.Value;
            var name = piece != null && Localization.instance != null ? Localization.instance.Localize(piece.m_name) : prefab;
            entry = _instance.Config.Bind("Bush spacing", prefab, start, new ConfigDescription(
                $"Space between {name} when planted with the grid (m).", new AcceptableValueRange<float>(0.25f, 10f)));
            entry.SettingChanged += (s, e) => Respace();
            Bushes[prefab] = entry;
            return entry;
        }

        private static void BindBushes(PieceTable tool)
        {
            foreach (var go in tool.m_pieces)
                if (go != null && go.GetComponent<Pickable>() != null && go.GetComponent<Plant>() == null && !Bushes.ContainsKey(go.name))
                    Bush(go.name, go.GetComponent<Piece>());
        }

        // How many plants after the first one the seeds you carry pay for (the first one the game pays itself).
        internal static int ExtraPlantsPaid(Player player, Kind kind)
        {
            if (player.m_noPlacementCost || ZoneSystem.instance.GetGlobalKey(kind.Piece.FreeBuildKey())) return int.MaxValue;
            var most = int.MaxValue;
            foreach (var req in kind.Piece.m_resources)
            {
                if (req.m_resItem == null || req.m_amount <= 0) continue;
                most = Mathf.Min(most, Seeds.Available(player, req.m_resItem.m_itemData.m_shared.m_name) / req.m_amount);
            }
            return most == int.MaxValue ? int.MaxValue : Mathf.Max(0, most - 1);
        }

        // Pays for n plants (bag first, then nearby chests); returns how many it could pay for.
        internal static int Pay(Player player, Kind kind, int n)
        {
            foreach (var req in kind.Piece.m_resources)
            {
                if (req.m_resItem == null || req.m_amount <= 0) continue;
                var got = Seeds.Take(player, req.m_resItem.m_itemData.m_shared.m_name, req.m_amount * n);
                n = Mathf.Min(n, got / req.m_amount);
            }
            return n;
        }

        // ---- the count below the crosshair ----

        private static void ShowCount()
        {
            var show = _count.Value && (Grid.Active || Planter.Left > 0) && Player.m_localPlayer != null && Player.m_localPlayer.InPlaceMode();
            if (!show)
            {
                if (_labelRoot != null && _labelRoot.activeSelf) _labelRoot.SetActive(false);
                return;
            }
            if (_label == null && !MakeLabel()) return;
            if (!_labelRoot.activeSelf) _labelRoot.SetActive(true);
            var t = Grid.Tally;
            var parts = new List<string>();
            if (Grid.Active)
            {
                var plants = t[(int)Spot.Ok];
                var shape = Grid.Fill ? "Fill field" : $"{Grid.Rows} x {Grid.Columns}";
                if (Grid.Snapped) shape += ", lined up";
                if (Grid.Kind != null && (Time.unscaledTime < _spacingShownUntil || Held(_spacingModifier.Value)))
                    shape += $", {Grid.Kind.Spacing.ToString("0.0#", CultureInfo.InvariantCulture)} m apart";
                parts.Add($"<color=#B8FFB8>{shape}: {plants:N0} to plant</color>");
                if (Grid.Note.Length > 0) parts.Add($"<color=#FFD94D>{Grid.Note}</color>");
                if (t[(int)Spot.NoSeeds] > 0) parts.Add($"<color=#FFD94D>{t[(int)Spot.NoSeeds]:N0} no seeds</color>");
                Add(parts, t[(int)Spot.Taken], "being planted");
                Add(parts, t[(int)Spot.NotCultivated], "not cultivated");
                Add(parts, t[(int)Spot.NoSpace], "no room");
                Add(parts, t[(int)Spot.NoSun], "no sun");
                Add(parts, t[(int)Spot.WrongBiome], "wrong biome");
                Add(parts, t[(int)Spot.TooHot] + t[(int)Spot.TooCold], "too hot or cold");
                Add(parts, t[(int)Spot.Blocked] + t[(int)Spot.Invalid], "blocked");
            }
            var text = string.Join("  ", parts);
            if (Planter.Left > 0) text += (text.Length > 0 ? "\n" : "") + $"<color=#DDDDDD>planting: {Planter.Left:N0} left</color>";
            if (_label.text != text) _label.text = text;
        }

        private static void Add(List<string> parts, int n, string what)
        {
            if (n > 0) parts.Add($"<color=#FF8C80>{n:N0} {what}</color>");
        }

        private static bool MakeLabel()
        {
            var hud = Hud.instance;
            if (hud == null || hud.m_crosshair == null) return false;
            var cross = hud.m_crosshair.rectTransform;
            _labelRoot = new GameObject("DigiPlantingCount", typeof(RectTransform));
            var rect = (RectTransform)_labelRoot.transform;
            rect.SetParent(cross.parent, false);
            rect.anchorMin = cross.anchorMin;
            rect.anchorMax = cross.anchorMax;
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = cross.anchoredPosition + new Vector2(0f, -60f);
            rect.sizeDelta = new Vector2(900f, 50f);
            _label = _labelRoot.AddComponent<TextMeshProUGUI>();
            if (hud.m_hoverName != null)
            {
                _label.font = hud.m_hoverName.font;
                _label.fontSharedMaterial = hud.m_hoverName.fontSharedMaterial;
            }
            _label.fontSize = 16f;
            _label.alignment = TextAlignmentOptions.Top;
            _label.textWrappingMode = TextWrappingModes.NoWrap;
            _label.overflowMode = TextOverflowModes.Overflow;
            _label.raycastTarget = false;
            _label.richText = true;
            return true;
        }

        // ---- Plant Easily's settings, once ----

        private void FromPlantEasily(string path)
        {
            try
            {
                var cfg = new Dictionary<string, string>();
                var section = "";
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]")) { section = line.Substring(1, line.Length - 2); continue; }
                    var eq = line.IndexOf('=');
                    if (eq > 0) cfg[section + "." + line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
                Int(cfg, "General.Rows", _rows);
                Int(cfg, "General.Columns", _columns);
                Bool(cfg, "General.ModActive", _gridOn);
                Bool(cfg, "General.SnapActive", _snapOn);
                Key(cfg, "Controls.EnableSnappingKey", _snapKey);
                foreach (var pair in cfg)
                {
                    const string head = "Pickables.", tail = " GridSpacing";
                    if (!pair.Key.StartsWith(head) || !pair.Key.EndsWith(tail)) continue;
                    var prefab = pair.Key.Substring(head.Length, pair.Key.Length - head.Length - tail.Length);
                    if (float.TryParse(pair.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) Bush(prefab, null).Value = f;
                }
                Bool(cfg, "General.RandomizeRotation", _randomRotation);
                if (cfg.TryGetValue("General.EnableScatter", out var scatterOn) && scatterOn.Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    Float(cfg, "General.PositionScatterRadius", _scatter);
                    Float(cfg, "General.RotationScatterAngle", _tilt);
                }
                Bool(cfg, "Grid.GloballyAlignGridDirections", _worldAligned);
                Bool(cfg, "Grid.MinimizeGridSpacing", _tighter);
                Float(cfg, "Grid.ExtraCropSpacing", _extraCrop);
                Float(cfg, "Grid.ExtraSaplingSpacing", _extraSapling);
                Float(cfg, "Pickables.DefaultGridSpacing", _pickableSpacing);
                Bool(cfg, "Difficulty.PreventInvalidPlanting", _onlyWhereItGrows);
                Bool(cfg, "Difficulty.PreventPartialPlanting", _allOrNothing);
                Bool(cfg, "Difficulty.UseStamina", _stamina);
                Bool(cfg, "Difficulty.UseDurability", _durability);
                Key(cfg, "Controls.EnableModKey", _toggleKey);
                Mod(cfg, "Controls.KeyboardModifierKey", _sizeModifier);
                Harvest.Copy(cfg, Bool, Float, Mod);
                Key(cfg, "Controls.IncreaseXKey", _moreColumns);
                Key(cfg, "Controls.DecreaseXKey", _fewerColumns);
                Key(cfg, "Controls.IncreaseYKey", _moreRows);
                Key(cfg, "Controls.DecreaseYKey", _fewerRows);
                Logger.LogInfo($"Copied Plant Easily's settings (grid {_rows.Value} x {_columns.Value}).");
            }
            catch (Exception e)
            {
                Logger.LogWarning("Could not read Plant Easily's settings: " + e.Message);
            }
        }

        private void FromWorld(string path)
        {
            try
            {
                var old = new Dictionary<string, string>();
                var section = "";
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]")) { section = line.Substring(1, line.Length - 2); continue; }
                    var eq = line.IndexOf(" = ", StringComparison.Ordinal);
                    // ("Replant key" was the replant on/off toggle there, F6; now it is the key held while picking)
                    var name = eq > 0 ? line.Substring(0, eq).Trim() : "";
                    if (eq > 0 && section.StartsWith("Planting Tools - ") && name != "Replant key") old[section + "\n" + name] = line.Substring(eq + 3);
                }
                if (old.Count == 0) return;
                var copied = 0;
                foreach (var pair in Config.File)
                {
                    if (!old.TryGetValue(pair.Key.Section + "\n" + pair.Key.Key, out var value)) continue;
                    if (pair.Value.SettingType == typeof(Modifier)) // a key there ("RightControl"), one of three here
                    {
                        if (value.EndsWith("Control")) value = nameof(Modifier.Ctrl);
                        else if (value.EndsWith("Shift")) value = nameof(Modifier.Shift);
                        else if (value.EndsWith("Alt")) value = nameof(Modifier.Alt);
                        else continue;
                    }
                    pair.Value.SetSerializedValue(value);
                    copied++;
                }
                Logger.LogInfo($"Copied {copied} setting(s) from World and Performance.");
            }
            catch (Exception e)
            {
                Logger.LogWarning("Could not read the settings from World and Performance: " + e.Message);
            }
        }

        private static void Int(Dictionary<string, string> cfg, string key, ConfigEntry<int> entry)
        {
            if (cfg.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) entry.Value = n;
        }

        private static void Float(Dictionary<string, string> cfg, string key, ConfigEntry<float> entry)
        {
            if (cfg.TryGetValue(key, out var v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) entry.Value = f;
        }

        private static void Bool(Dictionary<string, string> cfg, string key, ConfigEntry<bool> entry)
        {
            if (cfg.TryGetValue(key, out var v) && bool.TryParse(v, out var b)) entry.Value = b;
        }

        private static void Key(Dictionary<string, string> cfg, string key, ConfigEntry<KeyCode> entry)
        {
            // Plant Easily stores shortcuts ("RightControl", "F8", or "A + LeftShift"): only a single key carries over
            if (cfg.TryGetValue(key, out var v) && !v.Contains("+") && Enum.TryParse(v.Trim(), out KeyCode k) && k != KeyCode.None) entry.Value = k;
        }
    }
}
