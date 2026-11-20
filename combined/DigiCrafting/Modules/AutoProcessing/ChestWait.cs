// A station that just came into the loaded area works off everything it missed in its first update, up to an hour's
// worth at once, and a base loads piece by piece: the chests around it often do not exist yet. Its output then found no
// chest and dropped. So a station waits until the chests in its range are loaded. It loses nothing: its first real update
// still counts the time since its last one. Looked up once per zone per second, and only until a station is ready.
using System.Collections.Generic;
using UnityEngine;

namespace AutoProcessing
{
    internal static class ChestWait
    {
        private static readonly HashSet<Smelter> Ready = new HashSet<Smelter>();
        private static readonly HashSet<int> ChestPrefabs = new HashSet<int>();
        private static readonly Dictionary<Vector2s, List<Vector3>> Missing = new Dictionary<Vector2s, List<Vector3>>();
        private static readonly List<ZDO> Nearby = new List<ZDO>();
        private static float _readAt = float.MinValue;

        internal static bool ShouldWait(Smelter station, float range)
        {
            if (Ready.Contains(station)) return false;
            if (!ChestsLoaded(station.transform.position, range)) return true;
            Ready.RemoveWhere(s => s == null);
            Ready.Add(station);
            return false;
        }

        private static bool ChestsLoaded(Vector3 at, float range)
        {
            if (ZNetScene.instance == null || ZDOMan.instance == null) return true;
            if (ChestPrefabs.Count == 0) ReadChestPrefabs();
            if (Time.time - _readAt >= 1f)
            {
                Missing.Clear();
                _readAt = Time.time;
            }
            var zone = ZoneSystem.GetZone(at);
            if (!Missing.TryGetValue(zone, out var missing))
            {
                Missing[zone] = missing = new List<Vector3>();
                Nearby.Clear();
                ZDOMan.instance.FindSectorObjects(zone, new SimulationDistance(1, 0), Nearby);
                foreach (var zdo in Nearby)
                    if (ChestPrefabs.Contains(zdo.GetPrefab()) && ZNetScene.instance.FindInstance(zdo) == null)
                        missing.Add(zdo.GetPosition());
            }
            foreach (var chest in missing)
                if ((chest - at).sqrMagnitude <= range * range) return false;
            return true;
        }

        // Built chests only, like the ones the stations use.
        private static void ReadChestPrefabs()
        {
            foreach (var prefab in ZNetScene.instance.m_prefabs)
                if (prefab != null && prefab.GetComponent<Container>() != null && prefab.GetComponent<Piece>() != null)
                    ChestPrefabs.Add(prefab.name.GetStableHashCode());
        }
    }
}
