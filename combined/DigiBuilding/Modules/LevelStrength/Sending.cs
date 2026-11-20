// A click with a hoe or cultivator tool: the game places the piece, whose TerrainOp sends "use piece X here" to the owner
// of every heightmap in reach (RPC_ApplyOperation; the owner uses its own copy of piece X). For a click with a size,
// square mode or one of the new tools, that message gets our data appended (players without this mod read past it and
// do the game's own operation), and the zones the click reaches are worked out from its real size.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace LevelStrength
{
    internal static class Sending
    {
        private const int Magic = 0x44544F31; // "DTO1"
        private const byte Format = 1;

        // While the owner works out a click that came with our data.
        [ThreadStatic] internal static OpData Current;

        private static OpData _placing;
        private static readonly Dictionary<TerrainOp, OpData> Marked = new Dictionary<TerrainOp, OpData>();
        private static bool _unknownLogged;

        private static readonly MethodInfo OnPlaced = AccessTools.Method(typeof(TerrainOp), "OnPlaced");
        private static readonly MethodInfo DoOperation = AccessTools.Method(typeof(TerrainComp), "DoOperation");
        private static readonly MethodInfo Clone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static void Reset()
        {
            _placing = null;
            Current = null;
            Marked.Clear();
        }

        // --- The player who clicks -----------------------------------------------------------------------------------

        [HarmonyPatch(typeof(Player), nameof(Player.PlacePiece))]
        [HarmonyPrefix]
        private static void Placing(Player __instance, Piece piece, Vector3 pos)
        {
            _placing = null;
            if (__instance == Player.m_localPlayer && piece != null) _placing = Tools.ClickWith(piece, pos);
        }

        [HarmonyPatch(typeof(Player), nameof(Player.PlacePiece))]
        [HarmonyFinalizer]
        private static Exception Placed(Exception __exception)
        {
            _placing = null;
            Marked.Clear();
            return __exception;
        }

        // The placed piece wakes up inside PlacePiece: give it the click's size and shape, then do what TerrainOp.Awake
        // does, over every zone the click really reaches (the game measures only from the piece's own radius).
        [HarmonyPatch(typeof(TerrainOp), "Awake")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Waking(TerrainOp __instance)
        {
            var op = _placing;
            if (op == null || TerrainOp.m_forceDisableTerrainOps) return true;
            _placing = null;
            try
            {
                Shape(__instance.m_settings, op);
                Marked[__instance] = op;
                var maps = new List<Heightmap>();
                Heightmap.FindHeightmap(__instance.transform.position, op.Reach + 1f, maps); // paint reaches 1 m further
                foreach (var map in maps)
                {
                    var comp = map.GetAndCreateTerrainCompiler();
                    if (comp != null) comp.ApplyOperation(__instance);
                }
                OnPlaced?.Invoke(__instance, null);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("Terrain tool click failed: " + e);
            }
            UnityEngine.Object.Destroy(__instance.gameObject);
            return false;
        }

        // Size and shape into the operation's settings (the placed copy's own settings, not the piece's).
        private static void Shape(TerrainOp.Settings s, OpData op)
        {
            if (op.Kind == OpKind.Piece)
            {
                var scale = op.BaseRadius > 0f ? op.Radius / op.BaseRadius : 1f;
                s.m_levelRadius *= scale;
                s.m_raiseRadius *= scale;
                s.m_smoothRadius *= scale;
                s.m_paintRadius *= scale;
                if (op.Square) s.m_square = true;
                return;
            }
            s.m_level = false;
            s.m_raise = false;
            s.m_paintCleared = false;
            s.m_smooth = true; // only for the game's bookkeeping (radius, grass): the tool's own work is done in Ops
            s.m_smoothRadius = op.Reach;
            s.m_square = op.Square;
        }

        [HarmonyPatch(typeof(TerrainComp), nameof(TerrainComp.ApplyOperation))]
        [HarmonyPrefix]
        private static bool Send(TerrainComp __instance, TerrainOp modifier)
        {
            if (modifier == null || !Marked.TryGetValue(modifier, out var op)) return true;
            var view = Terrain.View(__instance);
            if (view == null || !view.IsValid()) return true; // the game logs it
            var settings = modifier.m_settings;
            var pkg = new ZPackage();
            pkg.Write(modifier.transform.position);
            pkg.Write(settings.m_rotation);
            if (settings.m_rotation) pkg.Write(modifier.transform.forward);
            settings.Serialize(pkg, modifier.gameObject);
            pkg.Write(Magic);
            pkg.Write(Format);
            pkg.Write((byte)op.Kind);
            pkg.Write(op.Id);
            pkg.Write((byte)((settings.m_level ? 1 : 0) | (settings.m_raise ? 2 : 0) | (settings.m_smooth ? 4 : 0) |
                             (settings.m_paintCleared ? 8 : 0) | (op.Square ? 16 : 0)));
            pkg.Write(settings.m_levelRadius);
            pkg.Write(settings.m_raiseRadius);
            pkg.Write(settings.m_smoothRadius);
            pkg.Write(settings.m_paintRadius);
            pkg.Write(op.Radius);
            pkg.Write(op.A);
            pkg.Write(op.B);
            pkg.Write(op.C);
            pkg.Write(op.Delta);
            pkg.Write(op.Strength);
            view.InvokeRPC("RPC_ApplyOperation", pkg);
            return false;
        }

        // --- The owner of the ground -----------------------------------------------------------------------------------

        [HarmonyPatch(typeof(TerrainComp), "RPC_ApplyOperation")]
        [HarmonyPrefix]
        private static bool Receiving(TerrainComp __instance, ZPackage pkg)
        {
            var view = Terrain.View(__instance);
            if (pkg == null || view == null || !view.IsValid() || !view.IsOwner()) return true;
            var start = pkg.GetPos();
            Vector3 pos, rot;
            OpData op;
            TerrainOp.Settings settings;
            try
            {
                pos = pkg.ReadVector3();
                rot = Vector3.zero;
                if (pkg.ReadBool()) rot = pkg.ReadVector3();
                var hash = pkg.ReadInt();
                if (pkg.Size() - pkg.GetPos() < 4 || pkg.ReadInt() != Magic || pkg.ReadByte() != Format)
                {
                    pkg.SetPos(start);
                    return true; // the game's own click
                }
                op = new OpData { Kind = (OpKind)pkg.ReadByte(), Id = pkg.ReadInt() };
                var flags = pkg.ReadByte();
                var levelRadius = pkg.ReadSingle();
                var raiseRadius = pkg.ReadSingle();
                var smoothRadius = pkg.ReadSingle();
                var paintRadius = pkg.ReadSingle();
                op.Radius = pkg.ReadSingle();
                op.A = pkg.ReadSingle();
                op.B = pkg.ReadSingle();
                op.C = pkg.ReadSingle();
                op.Delta = pkg.ReadSingle();
                op.Strength = Mathf.Clamp01(pkg.ReadSingle());
                op.Square = (flags & 16) != 0;
                settings = SettingsFor(hash, op, flags, levelRadius, raiseRadius, smoothRadius, paintRadius);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("Unreadable terrain tool click: " + e.Message);
                pkg.SetPos(start);
                return true;
            }
            if (settings == null) return false;
            Current = op;
            try
            {
                DoOperation.Invoke(__instance, new object[] { pos, rot, settings });
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("Terrain tool click failed: " + (e.InnerException ?? e));
            }
            finally
            {
                Current = null;
            }
            return false;
        }

        // The click's settings: a copy of the piece's own (game pieces) or plain ones (the new tools), with the click's
        // sizes, kept within this game's (the host's) largest size.
        private static TerrainOp.Settings SettingsFor(int hash, OpData op, byte flags, float levelRadius, float raiseRadius,
            float smoothRadius, float paintRadius)
        {
            var max = Plugin.MaxRadius?.Value ?? 30f;
            if (op.Kind != OpKind.Piece)
            {
                op.Radius = Mathf.Clamp(op.Radius, 0.5f, max);
                op.Delta = Mathf.Clamp(op.Delta, -2f, 2f);
                return new TerrainOp.Settings
                {
                    m_level = false, m_raise = false, m_paintCleared = false, m_smooth = true,
                    m_smoothRadius = op.Reach, m_square = op.Square,
                };
            }
            if (ObjectDB.instance == null || !ObjectDB.instance.TryGetTerrainOp(hash, out var prefab) || prefab == null)
            {
                if (!_unknownLogged) Plugin.Log?.LogWarning("A terrain tool click for a piece this game does not know: ignored.");
                _unknownLogged = true;
                return null;
            }
            var own = prefab.m_settings;
            var s = (TerrainOp.Settings)Clone.Invoke(own, null);
            s.m_level = (flags & 1) != 0;
            s.m_raise = (flags & 2) != 0;
            s.m_smooth = (flags & 4) != 0;
            s.m_paintCleared = (flags & 8) != 0;
            s.m_levelRadius = Mathf.Clamp(levelRadius, 0.1f, Mathf.Max(max, own.m_levelRadius));
            s.m_raiseRadius = Mathf.Clamp(raiseRadius, 0.1f, Mathf.Max(max, own.m_raiseRadius));
            s.m_smoothRadius = Mathf.Clamp(smoothRadius, 0.1f, Mathf.Max(max, own.m_smoothRadius));
            s.m_paintRadius = Mathf.Clamp(paintRadius, 0.1f, Mathf.Max(max, own.m_paintRadius));
            if (op.Square) s.m_square = true;
            return s;
        }
    }
}
