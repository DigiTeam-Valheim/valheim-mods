// Where a place's or a dungeon's spawners stood, worked out the way the game built them: the same blueprint, the same
// seed and the game's own RandomSpawn/RandomObject rolls (ZoneSystem.SpawnLocation and DungeonGenerator.PlaceRoom,
// step for step), so a spawner that is missing now is one that was destroyed, and one the roll left out never existed.
// The rolls change the shared blueprint for a moment; it is put back exactly as the game does after its own roll, and
// UnityEngine.Random's state is restored, so the game never notices.
using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace SpawnerRestore
{
    internal sealed class Ruin
    {
        public int Prefab;
        public Vector3 Pos;
        public Quaternion Rot;
        public Vector3 Scale = Vector3.one;
        public bool Recorded; // saved when it was destroyed (else worked out from a place or dungeon)
        public ZDO RecordedIn; // the zone's control object holding that record
    }

    internal static class Rolls
    {
        private static readonly Func<ZoneSystem, int, ZoneSystem.ZoneLocation> GetLocation =
            AccessTools.MethodDelegate<Func<ZoneSystem, int, ZoneSystem.ZoneLocation>>(AccessTools.Method(typeof(ZoneSystem), "GetLocation", new[] { typeof(int) }));

        // The spawners a place got when it was built. Its marker (LocationProxy) keeps the place, its seed and its turn.
        internal static List<Ruin> AtPlace(ZDO proxy, HashSet<int> spawners)
        {
            var result = new List<Ruin>();
            var hash = proxy.GetInt(ZDOVars.s_location);
            if (hash == 0 || ZoneSystem.instance == null) return result;
            var location = GetLocation(ZoneSystem.instance, hash);
            if (location == null || !location.m_prefab.IsValid) return result;
            Place(location, proxy.GetInt(ZDOVars.s_seed), proxy.GetPosition(), proxy.GetRotation(), spawners, result);
            return result;
        }

        // ZoneSystem.SpawnLocation, the part that decides which networked objects are made.
        private static void Place(ZoneSystem.ZoneLocation location, int seed, Vector3 pos, Quaternion rot, HashSet<int> spawners, List<Ruin> result)
        {
            var random = UnityEngine.Random.state;
            location.m_prefab.Load();
            try
            {
                var asset = location.m_prefab.Asset;
                if (asset == null) return;
                var views = Utils.GetEnabledComponentsInChildren<ZNetView>(asset);
                var objects = Utils.GetEnabledComponentsInChildren<RandomObject>(asset);
                var spawns = Utils.GetEnabledComponentsInChildren<RandomSpawn>(asset);
                foreach (var s in spawns) s.Prepare();
                var component = asset.GetComponent<Location>();
                var biome = component != null ? component.m_biome : Heightmap.Biome.None;
                Vector3 interior = Vector3.zero, generatorPos = Vector3.zero;
                if (component != null && component.m_interiorTransform && component.m_generator)
                {
                    interior = component.m_interiorTransform.localPosition;
                    generatorPos = component.m_generator.transform.localPosition;
                }
                var savedPos = asset.transform.position;
                var savedRot = asset.transform.rotation;
                asset.transform.position = Vector3.zero;
                asset.transform.rotation = Quaternion.identity;
                var custom = component != null && component.m_useCustomInteriorTransform && component.m_interiorTransform && component.m_generator;
                Vector3 genLocal = Vector3.zero, interiorLocal = Vector3.zero;
                var interiorRot = Quaternion.identity;
                try
                {
                    if (custom)
                    {
                        genLocal = component.m_generator.transform.localPosition;
                        interiorLocal = component.m_interiorTransform.localPosition;
                        interiorRot = component.m_interiorTransform.localRotation;
                        var zonePos = ZoneSystem.GetZonePos(ZoneSystem.GetZone(pos));
                        component.m_generator.transform.localPosition = Vector3.zero;
                        var offset = zonePos + interior + generatorPos - pos;
                        Vector3 local = (Matrix4x4.Rotate(Quaternion.Inverse(rot)) * Matrix4x4.Translate(offset)).GetColumn(3);
                        local.y = component.m_interiorTransform.localPosition.y;
                        component.m_interiorTransform.localPosition = local;
                        component.m_interiorTransform.localRotation = Quaternion.Inverse(rot);
                    }
                    UnityEngine.Random.InitState(seed);
                    foreach (var s in spawns) s.Randomize(pos + rot * s.gameObject.transform.position, component);
                    foreach (var o in objects) o.Randomize(pos + rot * o.gameObject.transform.position, component);
                    foreach (var view in views)
                    {
                        if (!view.gameObject.activeSelf) continue;
                        var prefab = Utils.GetPrefabName(view.gameObject).GetStableHashCode();
                        if (!spawners.Contains(prefab)) continue;
                        result.Add(new Ruin
                        {
                            Prefab = prefab, Pos = pos + rot * view.gameObject.transform.position, Rot = rot * view.gameObject.transform.rotation,
                            Scale = view.gameObject.transform.localScale,
                        });
                    }
                }
                finally
                {
                    foreach (var s in spawns) s.Reset();
                    foreach (var o in objects) o.Reset();
                    foreach (var view in views) view.gameObject.SetActive(true);
                    asset.transform.position = savedPos;
                    asset.transform.rotation = savedRot;
                    if (custom)
                    {
                        component.m_generator.transform.localPosition = genLocal;
                        component.m_interiorTransform.localPosition = interiorLocal;
                        component.m_interiorTransform.localRotation = interiorRot;
                    }
                    if (component != null) component.m_biome = biome;
                }
            }
            finally
            {
                location.m_prefab.Release();
                UnityEngine.Random.state = random;
            }
        }

        // The spawners a dungeon's rooms got. The rooms (which, where, turned how) are saved with the dungeon; each room's
        // roll is seeded by its position, as DungeonGenerator.PlaceRoom does. Needs the dungeon loaded (its seed and
        // settings are the live generator's).
        internal static List<Ruin> InDungeon(DungeonGenerator dungeon, ZDO zdo, HashSet<int> spawners) =>
            InDungeon(dungeon, dungeon.transform.position, dungeon.GetSeed(), zdo, spawners);

        // The same with the generator's settings, place and seed given (settings: the live one, or its prefab).
        internal static List<Ruin> InDungeon(DungeonGenerator settings, Vector3 at, int seed, ZDO zdo, HashSet<int> spawners)
        {
            var result = new List<Ruin>();
            if (DungeonDB.instance == null) return result;
            foreach (var room in Rooms(zdo))
            {
                var data = DungeonDB.instance.GetRoom(room.Hash);
                if (data == null || !data.m_prefab.IsValid) continue;
                Room(settings, at, seed, data, room.Pos, room.Rot, spawners, result);
            }
            return result;
        }

        private struct PlacedRoom
        {
            public int Hash;
            public Vector3 Pos;
            public Quaternion Rot;
        }

        // DungeonGenerator.Load: the new format (one byte array) or the old one (a value per room).
        private static List<PlacedRoom> Rooms(ZDO zdo)
        {
            var list = new List<PlacedRoom>();
            var bytes = zdo.GetByteArray(ZDOVars.s_roomData);
            if (bytes != null)
            {
                using (var reader = new BinaryReader(new MemoryStream(bytes)))
                {
                    var count = reader.ReadInt32();
                    for (var i = 0; i < count; i++)
                        list.Add(new PlacedRoom { Hash = reader.ReadInt32(), Pos = reader.ReadVector3(), Rot = reader.ReadQuaternion() });
                }
                return list;
            }
            var old = zdo.GetInt(ZDOVars.s_rooms);
            for (var i = 0; i < old; i++)
            {
                var key = "room" + i;
                list.Add(new PlacedRoom { Hash = zdo.GetInt(key), Pos = zdo.GetVec3(key + "_pos", Vector3.zero), Rot = zdo.GetQuaternion(key + "_rot", Quaternion.identity) });
            }
            return list;
        }

        // DungeonGenerator.PlaceRoom, the part that decides which networked objects are made.
        private static void Room(DungeonGenerator dungeon, Vector3 dungeonPos, int dungeonSeed, DungeonDB.RoomData data, Vector3 pos, Quaternion rot,
            HashSet<int> spawners, List<Ruin> result)
        {
            var random = UnityEngine.Random.state;
            data.m_prefab.Load();
            try
            {
                var asset = data.m_prefab.Asset;
                if (asset == null) return;
                var room = asset.GetComponent<Room>();
                if (room == null) return;
                var views = Utils.GetEnabledComponentsInChildren<ZNetView>(asset);
                var objects = Utils.GetEnabledComponentsInChildren<RandomObject>(asset);
                var spawns = Utils.GetEnabledComponentsInChildren<RandomSpawn>(asset);
                foreach (var s in spawns) s.Prepare();
                var at = dungeon.m_useCustomInteriorTransform ? pos - dungeonPos : pos;
                var seed = (int)at.x * 4271 + (int)at.y * 9187 + (int)at.z * 2134;
                if (dungeon.m_addBaseSeedToRandomSpawn) seed += dungeonSeed;
                var origin = room.transform.position;
                var inverse = Quaternion.Inverse(room.transform.rotation);
                try
                {
                    UnityEngine.Random.InitState(seed);
                    foreach (var s in spawns) s.Randomize(pos + rot * (inverse * (s.gameObject.transform.position - origin)), null, dungeon);
                    foreach (var o in objects) o.Randomize(pos + rot * (inverse * (o.gameObject.transform.position - origin)), null, dungeon);
                    foreach (var view in views)
                    {
                        if (!view.gameObject.activeSelf) continue;
                        var prefab = Utils.GetPrefabName(view.gameObject).GetStableHashCode();
                        if (!spawners.Contains(prefab)) continue;
                        result.Add(new Ruin
                        {
                            Prefab = prefab, Pos = pos + rot * (inverse * (view.gameObject.transform.position - origin)),
                            Rot = rot * (inverse * view.gameObject.transform.rotation), Scale = view.gameObject.transform.localScale,
                        });
                    }
                }
                finally
                {
                    foreach (var s in spawns) s.Reset();
                    foreach (var o in objects) o.Reset();
                    foreach (var view in views) view.gameObject.SetActive(true);
                }
            }
            finally
            {
                data.m_prefab.Release();
                UnityEngine.Random.state = random;
            }
        }
    }
}
