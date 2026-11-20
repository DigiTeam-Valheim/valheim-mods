// The player's side: the new hoe pieces, sizes (size key + mouse wheel), square mode, the Raise/Lower step, the outline.
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using DigiShared;
using HarmonyLib;
using UnityEngine;

namespace LevelStrength
{
    internal static class Tools
    {
        private const string Prefix = "digi_terrain_";

        // id, label, description, kind, step sign, default size
        private sealed class Tool
        {
            public string Id, Label, Description;
            public OpKind Kind;
            public float Sign, Size;
        }

        private static readonly Tool[] All =
        {
            new Tool { Id = "smooth", Label = "Smooth", Kind = OpKind.Smooth, Size = 4f,
                Description = "Evens out bumps and sudden steps but keeps the ground's shape; every click a bit more." },
            new Tool { Id = "slope", Label = "Slope", Kind = OpKind.Slope, Size = 4f,
                Description = "Turns the ground under it into one even slope that follows its overall incline: one click makes a step a ramp." },
            new Tool { Id = "raise", Label = "Raise", Kind = OpKind.Raise, Sign = 1f, Size = 2f,
                Description = "Raises the ground by a small step ({step} + mouse wheel sets the step)." },
            new Tool { Id = "lower", Label = "Lower", Kind = OpKind.Raise, Sign = -1f, Size = 2f,
                Description = "Lowers the ground by a small step ({step} + mouse wheel sets the step)." },
            new Tool { Id = "reset", Label = "Reset", Kind = OpKind.Reset, Size = 3f,
                Description = "Puts the ground back to how the world made it: height, paths, paving and cultivated soil." },
        };

        private static readonly AccessTools.FieldRef<Player, HashSet<string>> KnownRecipes = AccessTools.FieldRefAccess<Player, HashSet<string>>("m_knownRecipes");
        private static readonly AccessTools.FieldRef<BuildUi, List<Piece>> MenuScratch = AccessTools.FieldRefAccess<BuildUi, List<Piece>>("m_tempPieces");
        private static readonly AccessTools.FieldRef<Player, GameObject> Ghost = AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost");
        private static readonly MethodInfo UpdateKnown = AccessTools.Method(typeof(Player), "UpdateKnownRecipesList");
        private static readonly MethodInfo UpdateAvailable = AccessTools.Method(typeof(Player), "UpdateAvailablePiecesList");

        private static readonly List<GameObject> Pieces = new List<GameObject>();
        private static readonly Dictionary<string, float> Sizes = new Dictionary<string, float>(); // piece prefab name -> size
        private static GameObject _holder;
        private static ObjectDB _syncedDb;
        private static IconSheet _icons;
        private static bool _square, _active, _stepTool, _readingWheel, _failed;
        private static LineRenderer _line;
        private static GameObject _hiddenGhost;
        private static readonly List<Renderer> HiddenParticles = new List<Renderer>();

        // --- Every frame -----------------------------------------------------------------------------------------------

        internal static void Tick()
        {
            if (_failed) return;
            try
            {
                Sync();
                Steer();
            }
            catch (System.Exception e)
            {
                _failed = true; // once in the log, not every frame
                Plugin.Log?.LogError("Terrain tools stopped: " + e);
                HideOutline();
            }
        }

        private static void Steer()
        {
            var player = Player.m_localPlayer;
            var piece = player != null && player.InPlaceMode() && !Hud.IsPieceSelectionVisible() && !Typing() ? player.GetSelectedPiece() : null;
            var top = piece != null ? piece.GetComponent<TerrainOp>() : null;
            _active = top != null;
            _stepTool = _active && ToolOf(piece)?.Kind == OpKind.Raise;
            if (!_active)
            {
                HideOutline();
                return;
            }
            var tool = ToolOf(piece);
            if (Plugin.SquareKey.Value.IsDown())
            {
                _square = !_square;
                Say(player, _square ? "Square" : "Round");
            }
            _readingWheel = true;
            var wheel = ZInput.GetMouseScrollWheel();
            _readingWheel = false;
            if (wheel != 0f && UnityInput.Current.GetKey(Plugin.RadiusKey.Value))
            {
                // Square mode counts whole meters: one notch is at least one meter there.
                var step = _square ? Mathf.Max(1f, Plugin.RadiusStep.Value) : Plugin.RadiusStep.Value;
                var size = Mathf.Clamp(UsedSize(piece, top) + Mathf.Sign(wheel) * step, _square ? 1f : 0.5f, MaxSize(top));
                Sizes[piece.gameObject.name] = size;
                Say(player, $"{Localization.instance.Localize(piece.m_name)}: {UsedSize(piece, top):0.#} m{(_square ? " (square)" : "")}");
            }
            else if (wheel != 0f && tool != null && tool.Kind == OpKind.Raise && UnityInput.Current.GetKey(Plugin.StepKey.Value))
            {
                var step = Mathf.Clamp(Mathf.Round((Plugin.RaiseStep.Value + Mathf.Sign(wheel) * 0.05f) * 100f) / 100f, 0.05f, 2f);
                Plugin.RaiseStep.Value = step;
                Say(player, $"Step: {step:0.##} m");
            }
            Outline(player, piece, top);
        }

        private static bool Typing() =>
            (Chat.instance != null && Chat.instance.HasFocus()) || Console.IsVisible() || TextInput.IsVisible() || Minimap.InTextInput() ||
            (Hud.instance != null && Hud.instance.m_buildUi != null && Hud.instance.m_buildUi.SearchFieldFocused); // the build menu's search box

        private static void Say(Player player, string text) => player.Message(MessageHud.MessageType.Center, text);

        private static Tool ToolOf(Piece piece)
        {
            var name = piece != null ? piece.gameObject.name : null;
            if (name == null || !name.StartsWith(Prefix, System.StringComparison.Ordinal)) return null;
            var id = name.Substring(Prefix.Length);
            return All.FirstOrDefault(t => t.Id == id);
        }

        // The size of a tool: what the player chose, else the piece's own (its largest radius).
        private static float SizeOf(Piece piece, TerrainOp top) =>
            Sizes.TryGetValue(piece.gameObject.name, out var size) ? Mathf.Min(size, MaxSize(top)) : top.m_settings.GetRadius();

        private static float MaxSize(TerrainOp top) => Mathf.Max(Plugin.MaxRadius.Value, top.m_settings.GetRadius());

        // Square mode works in whole meters (the grid's), so the game's own square tools and ours reach the same vertices.
        private static float UsedSize(Piece piece, TerrainOp top)
        {
            var size = SizeOf(piece, top);
            return _square ? Mathf.Max(1f, Mathf.Round(size)) : size;
        }

        // The mouse wheel goes to the size (or step) only while its key is held: no camera zoom or piece turning then.
        [HarmonyPatch(typeof(ZInput), "Internal_GetMouseScrollWheel")]
        [HarmonyPostfix]
        private static void WheelForSize(ref float __result)
        {
            if (!_active || _readingWheel || __result == 0f) return;
            if (UnityInput.Current.GetKey(Plugin.RadiusKey.Value) || (_stepTool && UnityInput.Current.GetKey(Plugin.StepKey.Value))) __result = 0f;
        }

        // --- A click ---------------------------------------------------------------------------------------------------

        // What a click with this piece does, or null when it is the game's plain click (own size, round).
        internal static OpData ClickWith(Piece piece, Vector3 pos)
        {
            if (!Plugin.ToolsOn) return null;
            var top = piece.GetComponent<TerrainOp>();
            if (top == null) return null;
            var tool = ToolOf(piece);
            var own = top.m_settings.GetRadius();
            var size = UsedSize(piece, top);
            if (tool == null && !_square && Mathf.Abs(size - own) < 0.01f) return null;
            var op = new OpData
            {
                Kind = tool?.Kind ?? OpKind.Piece,
                Id = Random.Range(int.MinValue, int.MaxValue),
                Square = _square,
                Radius = size,
                BaseRadius = own,
            };
            if (tool == null) return op;
            switch (tool.Kind)
            {
                case OpKind.Raise:
                    op.Delta = tool.Sign * Plugin.RaiseStep.Value;
                    break;
                case OpKind.Smooth:
                    op.Strength = Plugin.SmoothStrength.Value / 100f;
                    break;
                case OpKind.Slope:
                    Ops.FitPlane(op, pos);
                    break;
            }
            return op;
        }

        // --- The outline -----------------------------------------------------------------------------------------------

        // Exactly the vertices the tool changes: within the size of the vertex nearest to the aim, round or square.
        private static void Outline(Player player, Piece piece, TerrainOp top)
        {
            var ghost = Ghost(player);
            if (ghost == null || !ghost.activeInHierarchy)
            {
                HideOutline();
                return;
            }
            var size = UsedSize(piece, top);
            if (!Plugin.SteadyOutline.Value)
            {
                HideOutline();
                ScaleParticles(ghost, size / Mathf.Max(0.01f, top.m_settings.GetRadius()));
                return;
            }
            HideParticles(ghost);
            if (_line == null) _line = NewLine();
            if (_line == null) return;
            var square = _square;
            var centre = ghost.transform.position;
            centre.x = Mathf.Floor(centre.x + 0.5f);
            centre.z = Mathf.Floor(centre.z + 0.5f);
            var sampler = new Sampler();
            var points = new List<Vector3>();
            if (square)
            {
                var half = size + 0.02f;
                var steps = Mathf.Max(4, Mathf.CeilToInt(half * 2f));
                var corners = new[]
                {
                    new Vector2(-half, -half), new Vector2(half, -half), new Vector2(half, half), new Vector2(-half, half),
                };
                for (var side = 0; side < 4; side++)
                {
                    var from = corners[side];
                    var to = corners[(side + 1) % 4];
                    for (var i = 0; i < steps; i++)
                    {
                        var p = Vector2.Lerp(from, to, i / (float)steps);
                        points.Add(OnGround(sampler, centre.x + p.x, centre.z + p.y, centre.y));
                    }
                }
            }
            else
            {
                var steps = Mathf.Clamp(Mathf.CeilToInt(size * 12f), 32, 240);
                for (var i = 0; i < steps; i++)
                {
                    var angle = i * Mathf.PI * 2f / steps;
                    points.Add(OnGround(sampler, centre.x + Mathf.Cos(angle) * size, centre.z + Mathf.Sin(angle) * size, centre.y));
                }
            }
            _line.positionCount = points.Count;
            _line.SetPositions(points.ToArray());
            _line.enabled = true;
        }

        private static Vector3 OnGround(Sampler sampler, float x, float z, float fallback)
        {
            var h = sampler.Height(new Vector3(x, 0f, z));
            return new Vector3(x, (float.IsNaN(h) ? fallback : h) + 0.08f, z);
        }

        private static LineRenderer NewLine()
        {
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
            if (shader == null) return null;
            var go = new GameObject("Digi terrain tool outline");
            Object.DontDestroyOnLoad(go);
            var line = go.AddComponent<LineRenderer>();
            line.material = new Material(shader);
            line.startColor = line.endColor = new Color(1f, 0.92f, 0.7f, 0.9f);
            line.startWidth = line.endWidth = 0.07f;
            line.loop = true;
            line.useWorldSpace = true;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            return line;
        }

        private static void HideOutline()
        {
            if (_line != null) _line.enabled = false;
        }

        // The game's pulsing circle (particles on the ghost) while the outline shows.
        private static void HideParticles(GameObject ghost)
        {
            if (_hiddenGhost == ghost) return;
            ShowParticles();
            _hiddenGhost = ghost;
            foreach (var renderer in ghost.GetComponentsInChildren<ParticleSystemRenderer>(true))
            {
                if (!renderer.enabled) continue;
                renderer.enabled = false;
                HiddenParticles.Add(renderer);
            }
        }

        private static void ShowParticles()
        {
            foreach (var renderer in HiddenParticles)
                if (renderer != null) renderer.enabled = true;
            HiddenParticles.Clear();
            _hiddenGhost = null;
        }

        // Without the outline: the game's circle grows and shrinks with the size.
        private static void ScaleParticles(GameObject ghost, float factor)
        {
            if (_hiddenGhost == ghost) ShowParticles();
            var only = ghost.transform.Find("_GhostOnly");
            if (only == null) return;
            foreach (var system in only.GetComponentsInChildren<ParticleSystem>(true))
                system.transform.localScale = Vector3.one * factor;
        }

        // --- The new pieces in the hoe ---------------------------------------------------------------------------------

        private static PieceTable HoeTable()
        {
            var hoe = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab("Hoe") : null;
            var drop = hoe != null ? hoe.GetComponent<ItemDrop>() : null;
            return drop != null ? drop.m_itemData?.m_shared?.m_buildPieces : null;
        }

        private static bool IsOurs(GameObject prefab) => prefab != null && prefab.name.StartsWith(Prefix, System.StringComparison.Ordinal);

        // Once per world (the hoe's list belongs to the game and lives on), when the player is there: else the game
        // would greet every tool as a new piece.
        private static void Sync()
        {
            var player = Player.m_localPlayer;
            if (ObjectDB.instance == null || ObjectDB.instance == _syncedDb || ZNetScene.instance == null || player == null) return;
            var table = HoeTable();
            if (table == null) return;
            _syncedDb = ObjectDB.instance;
            table.m_pieces.RemoveAll(IsOurs);
            if (!MakePieces(table)) return;
            table.m_pieces.AddRange(Pieces);
            foreach (var piece in Pieces) KnownRecipes(player).Add(piece.GetComponent<Piece>().m_name);
            Refresh(player);
        }

        // The game refreshes the hoe's list only when it learns a new piece (and these are known already), and a menu
        // update that failed leaves pieces in its scratch list: both are put right here.
        private static void Refresh(Player player)
        {
            if (player == null) return;
            UpdateKnown.Invoke(player, null);
            UpdateAvailable.Invoke(player, null);
            var buildUi = Hud.instance != null ? Hud.instance.m_buildUi : null;
            if (buildUi != null) MenuScratch(buildUi).Clear();
        }

        private static bool MakePieces(PieceTable table)
        {
            if (Pieces.Count == All.Length && Pieces.All(p => p != null)) return true;
            Pieces.Clear();
            var template = table.m_pieces.FirstOrDefault(p => p != null && p.name == "mud_road_v2" && p.GetComponent<TerrainOp>() != null)
                           ?? table.m_pieces.FirstOrDefault(p => p != null && p.GetComponent<TerrainOp>() != null && p.GetComponent<Piece>() != null);
            if (template == null)
            {
                Plugin.Log?.LogError("The hoe's Level ground was not found: no new terrain pieces.");
                return false;
            }
            if (_holder == null)
            {
                _holder = new GameObject("DigiBuilding_TerrainTools");
                _holder.SetActive(false); // the copies never wake up
                Object.DontDestroyOnLoad(_holder);
            }
            if (_icons == null) _icons = new IconSheet(typeof(Plugin).Assembly, "WoodSwap.CreativeIcons", w => Plugin.Log?.LogWarning(w));
            foreach (var tool in All)
            {
                var copy = Object.Instantiate(template, _holder.transform, false);
                copy.name = Prefix + tool.Id;
                foreach (var view in copy.GetComponentsInChildren<ZNetView>(true)) Object.DestroyImmediate(view);
                var piece = copy.GetComponent<Piece>();
                piece.m_name = tool.Label;
                piece.m_description = tool.Description.Replace("{step}", Plugin.StepKey.Value.ToString()) +
                                      $" Size: {Plugin.RadiusKey.Value} + mouse wheel. Square or round: {Plugin.SquareKey.Value}.";
                piece.m_icon = _icons.For("terrain_" + tool.Id, null) ?? piece.m_icon;
                var top = copy.GetComponent<TerrainOp>();
                top.m_spawnOnPlaced = null;
                var s = top.m_settings;
                s.m_level = false;
                s.m_raise = false;
                s.m_paintCleared = false;
                s.m_smooth = true;
                s.m_smoothRadius = tool.Size;
                Pieces.Add(copy);
            }
            return true;
        }

        // Switched off: the pieces leave the hoe, the outline goes, the game's circle comes back.
        internal static void Remove()
        {
            HoeTable()?.m_pieces.RemoveAll(IsOurs);
            Refresh(Player.m_localPlayer);
            if (_holder != null) Object.Destroy(_holder);
            _holder = null;
            Pieces.Clear();
            _syncedDb = null;
            ShowParticles();
            if (_line != null) Object.Destroy(_line.gameObject);
            _line = null;
            _active = false;
            _failed = false;
        }
    }
}
