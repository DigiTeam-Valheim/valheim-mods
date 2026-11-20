// Planting Tools, the planting queue. A click pays for the whole grid at once and hands its plants to this queue, which
// creates them a few milliseconds' worth per frame ("Planting time per frame"), so the game keeps running smoothly and
// you can click the next grid straight away: queued plants count as standing where they will grow, so nothing is
// planted twice. No effect or sound per plant (one swing per click, like the game); the Farming skill rises per plant.
// When you leave the world with plants still queued, they are all planted before the game saves.
using System.Collections.Generic;
using Splatform;
using UnityEngine;

namespace PlantingTools
{
    internal static class Planter
    {
        private struct Job
        {
            public GameObject Prefab;
            public Vector3 Pos;
            public Quaternion Rot;
        }

        private static readonly Queue<Job> Jobs = new Queue<Job>();
        // queued plants by 1 m square, so the grid can tell quickly which spots are taken
        private static readonly Dictionary<long, List<Job>> Squares = new Dictionary<long, List<Job>>();
        private static long _creator;
        private static PlatformUserID _platformUser;
        private static Skills.SkillType _skill;

        public static int Left => Jobs.Count;
        public static int Planted;      // goes up with every plant created
        public static int Queued;       // goes up with every plant queued (the grid checks again at once)
        public static float LastFrameMs;

        public static void Add(GameObject prefab, Vector3 pos, Quaternion rot)
        {
            var job = new Job { Prefab = prefab, Pos = pos, Rot = rot };
            Jobs.Enqueue(job);
            Queued++;
            var key = Key(pos);
            if (!Squares.TryGetValue(key, out var list)) Squares[key] = list = new List<Job>();
            list.Add(job);
        }

        public static void Owner(Player player)
        {
            _creator = player.GetPlayerID();
            _platformUser = PlatformManager.DistributionPlatform.LocalUser.PlatformUserID;
            _skill = player.m_buildPieces != null ? player.m_buildPieces.m_skill : Skills.SkillType.Farming;
        }

        // Each frame: plant until this frame's time is used up.
        public static void Work(float budgetMs)
        {
            if (Jobs.Count == 0) return;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (Jobs.Count > 0 && watch.Elapsed.TotalMilliseconds < budgetMs) PlantOne();
            LastFrameMs = (float)watch.Elapsed.TotalMilliseconds;
        }

        public static void Finish()
        {
            while (Jobs.Count > 0) PlantOne();
        }

        public static bool Near(Vector3 p, float distance)
        {
            var d2 = distance * distance;
            var cx = Mathf.FloorToInt(p.x);
            var cz = Mathf.FloorToInt(p.z);
            var reach = Mathf.CeilToInt(distance);
            for (var x = cx - reach; x <= cx + reach; x++)
                for (var z = cz - reach; z <= cz + reach; z++)
                {
                    if (!Squares.TryGetValue(((long)x << 32) ^ (uint)z, out var list)) continue;
                    foreach (var q in list)
                    {
                        var dx = q.Pos.x - p.x;
                        var dz = q.Pos.z - p.z;
                        if (dx * dx + dz * dz < d2) return true;
                    }
                }
            return false;
        }

        // The queued plant of this prefab nearest "from" within "radius" of "center" (a grid can continue a field that is
        // still being planted).
        public static bool Nearest(Vector3 center, float radius, GameObject prefab, Vector3 from, bool notAtFrom, out Vector3 found, out float bestD2)
        {
            found = Vector3.zero;
            bestD2 = float.MaxValue;
            var r2 = radius * radius;
            var cx = Mathf.FloorToInt(center.x);
            var cz = Mathf.FloorToInt(center.z);
            var reach = Mathf.CeilToInt(radius);
            for (var x = cx - reach; x <= cx + reach; x++)
                for (var z = cz - reach; z <= cz + reach; z++)
                {
                    if (!Squares.TryGetValue(((long)x << 32) ^ (uint)z, out var list)) continue;
                    foreach (var q in list)
                    {
                        if (q.Prefab != prefab) continue;
                        float cdx = q.Pos.x - center.x, cdz = q.Pos.z - center.z;
                        if (cdx * cdx + cdz * cdz > r2) continue;
                        float dx = q.Pos.x - from.x, dz = q.Pos.z - from.z;
                        var d2 = dx * dx + dz * dz;
                        if ((notAtFrom && d2 < 0.0025f) || d2 >= bestD2) continue;
                        bestD2 = d2;
                        found = q.Pos;
                    }
                }
            return bestD2 < float.MaxValue;
        }

        // The queued plants, for the preview.
        public static void CopyQueued(List<Matrix4x4> into, Vector3 scale)
        {
            into.Clear();
            foreach (var job in Jobs) into.Add(Matrix4x4.TRS(job.Pos, job.Rot, scale));
        }

        private static void PlantOne()
        {
            var job = Jobs.Dequeue();
            var key = Key(job.Pos);
            if (Squares.TryGetValue(key, out var list))
            {
                for (var i = 0; i < list.Count; i++)
                    if (list[i].Pos == job.Pos && list[i].Prefab == job.Prefab) { list.RemoveAt(i); break; }
                if (list.Count == 0) Squares.Remove(key);
            }
            if (job.Prefab == null) return;
            TerrainModifier.SetTriggerOnPlaced(true);
            var go = Object.Instantiate(job.Prefab, job.Pos, job.Rot);
            TerrainModifier.SetTriggerOnPlaced(false);
            var piece = go.GetComponent<Piece>();
            if (piece != null) piece.SetCreator(_creator, _platformUser);
            var player = Player.m_localPlayer;
            if (player != null && _skill != Skills.SkillType.None) player.RaiseSkill(_skill);
            Planted++;
        }

        private static long Key(Vector3 p) => ((long)Mathf.FloorToInt(p.x) << 32) ^ (uint)Mathf.FloorToInt(p.z);
    }
}
