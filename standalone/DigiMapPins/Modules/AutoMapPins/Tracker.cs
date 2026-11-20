// Auto Map Pins, discovery: objects of the catalog that load (the game spawns every networked object through
// ZNetScene.AddInstance, locations through Location.Awake, portals through TeleportWorld.Awake) wait in a list until you
// come within the discovery radius. Then they join a record: one of the same thing close by (bushes and deposits close
// together share a pin, with a count), or a new one if that thing is switched on for the biome it stands in. Things
// built or planted by players are left out (grown crops and saplings have no creator: plants on cultivated ground or
// in a base are skipped too), except sap extractors, which are pinned as soon as they are placed (and the ones already
// on the explored map when the world loads). Portals are pinned as soon as they load, named after their tag, and
// renamed when it changes.
// Once a second the records around you are checked against what is really there: a deposit, beehive or spawner that
// is gone (mined out, destroyed) loses its pin, so does a picked plant that never grows back; a picked plant that does
// is faded until its regrow time; an emptied chest is faded; a destroyed portal loses its pin.
// When the world loads, every pin is also checked against the world's saved objects (one pass over all of them), so
// what was mined out, destroyed or picked while the mod did not know of it (or before it took over Cartur's pins) loses
// its pin without you going there.
// Nothing inside a dungeon gets a pin (its own pin outside says enough); inside, once no chest has loot left, the
// dungeon's pin is crossed off.
// Portals are also pinned from the game's list of every portal of the world, wherever the map is explored, so you do
// not have to visit them; their pins follow the tag there too.
// The chest of an Epic Loot treasure map has a creator like a build, but is pinned: for the one who bought the map.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace AutoMapPins
{
    internal static class Tracker
    {
        private sealed class Candidate
        {
            public Thing Thing;
            public ZNetView View;
            public Location Location;
        }

        private static readonly List<Candidate> Candidates = new List<Candidate>();
        private static readonly HashSet<TeleportWorld> Portals = new HashSet<TeleportWorld>();
        private static readonly AccessTools.FieldRef<ZNetScene, Dictionary<ZDO, ZNetView>> Instances =
            AccessTools.FieldRefAccess<ZNetScene, Dictionary<ZDO, ZNetView>>("m_instances");
        private static readonly AccessTools.FieldRef<ZDOMan, Dictionary<ZDOID, ZDO>> AllObjects =
            AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZDOID, ZDO>>("m_objectsByID");
        private static readonly AccessTools.FieldRef<List<Location>> AllLocations =
            AccessTools.StaticFieldRefAccess<List<Location>>(AccessTools.Field(typeof(Location), "s_allLocations"));
        private static float _timer, _saveTimer, _portalTimer = 999f;
        private static readonly Func<Minimap, Vector3, bool> Explored =
            AccessTools.MethodDelegate<Func<Minimap, Vector3, bool>>(AccessTools.Method(typeof(Minimap), "IsExplored", new[] { typeof(Vector3) }));
        private static bool _swept;
        // The chests inside the dungeon you are in (no pins, only for crossing the dungeon off), and how long you are in.
        private static readonly List<ZNetView> DungeonChests = new List<ZNetView>();
        private static float _insideFor;
        // Reused each second instead of new lists.
        private static readonly List<Record> Nearby = new List<Record>();
        private static readonly List<ZDOID> Missing = new List<ZDOID>();

        internal static void Reset()
        {
            Candidates.Clear();
            Portals.Clear();
            DungeonChests.Clear();
            _swept = false;
            _portalTimer = 999f;
            _portalsLogged = false;
            _insideFor = 0f;
        }

        // --- What loads ----------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.AddInstance))]
        [HarmonyPostfix]
        private static void ObjectLoaded(ZDO zdo, ZNetView nview)
        {
            if (!Catalog.Built || zdo == null || nview == null) return;
            var thing = Catalog.ForPrefab(zdo.GetPrefab());
            if (thing == null || zdo.GetLong(ZDOVars.s_creator) != 0L && thing.Kind != Kind.Built && !IsMapChest(thing, zdo)) return; // built or planted by a player
            if (Character.InInterior(zdo.GetPosition()))
            {
                if (thing.Kind == Kind.Chest) DungeonChests.Add(nview); // no pin inside a dungeon
                return;
            }
            Candidates.Add(new Candidate { Thing = thing, View = nview });
        }

        // Epic Loot's mark on the chest of a treasure map: one of the world's chests, with the buyer as its creator.
        private static readonly int MapChestMark = "TreasureMapChest.Biome".GetStableHashCode();

        private static bool IsMapChest(Thing thing, ZDO zdo) => thing.Kind == Kind.Chest && !string.IsNullOrEmpty(zdo.GetString(MapChestMark));

        [HarmonyPatch(typeof(Location), "Awake")]
        [HarmonyPostfix]
        private static void LocationLoaded(Location __instance)
        {
            if (!Catalog.Built || __instance == null) return;
            var thing = Catalog.ForLocation(Utils.GetPrefabName(__instance.gameObject));
            if (thing != null) Candidates.Add(new Candidate { Thing = thing, Location = __instance });
        }

        [HarmonyPatch(typeof(TeleportWorld), "Awake")]
        [HarmonyPostfix]
        private static void PortalLoaded(TeleportWorld __instance)
        {
            if (__instance != null) Portals.Add(__instance);
        }

        // Switched on, or the catalog just built: what is loaded already.
        private static void SweepLoaded()
        {
            _swept = true;
            if (ZNetScene.instance != null)
                foreach (var pair in Instances(ZNetScene.instance).ToList()) ObjectLoaded(pair.Key, pair.Value);
            var locations = AllLocations();
            if (locations != null)
                foreach (var location in locations.ToList()) LocationLoaded(location);
            foreach (var portal in UnityEngine.Object.FindObjectsByType<TeleportWorld>(FindObjectsSortMode.None)) PortalLoaded(portal);
        }

        // --- Once a second -------------------------------------------------------------------------------------

        private static int _errors;

        internal static void Tick(float deltaTime)
        {
            _timer += deltaTime;
            if (_timer < 1f) return;
            _timer = 0f;
            try
            {
                Second();
            }
            catch (Exception e)
            {
                if (_errors++ < 3) Plugin.Log?.LogError("Auto Map Pins: " + e);
            }
        }

        private static ZNet _session;

        private static void Second()
        {
            var player = Player.m_localPlayer;
            if (player == null || Minimap.instance == null || ZNet.instance == null || WorldGenerator.instance == null ||
                ZoneSystem.instance == null || ZDOMan.instance == null) return;
            if (ZNet.instance != _session)
            {
                // A world loaded, also the same one again from the main menu: start over as after a restart (the map is
                // new and every object has a new number), so all pins are drawn again.
                if (_session != null)
                {
                    Records.Save();
                    MapPins.Clear();
                    Records.Clear();
                    Reset();
                    Lairs.Reset();
                }
                _session = ZNet.instance;
            }
            if (!Catalog.TryBuild()) return;
            if (!_swept) SweepLoaded();

            var key = Records.CurrentKey();
            if (key == null) return;
            if (key != Records.Key)
            {
                Records.Save();
                MapPins.Clear();
                Records.Load(key);
                Catalog.ForgetUnknown();
                CarturImport.Run();
                ForgetInside();
                UncoverPlaces();
                ScanWorld();
                IdentifySpawners();
                MergeDoubles();
                KnownBuilds();
                KnownMorkhalla();
                PruneGone();
                ClearScan();
                MapPins.RefreshAll();
            }
            if (MapPins.PlacesChanged) MapPins.RefreshPlaces();

            var pos = player.transform.position;
            Discover(pos);
            UpdatePortals();
            _portalTimer += 1f;
            if (_portalTimer >= 5f)
            {
                _portalTimer = 0f;
                KnownPortals();
            }
            Maintain(pos);
            CrossOffLooted(pos);

            _saveTimer += 1f;
            if (_saveTimer >= 20f)
            {
                _saveTimer = 0f;
                Records.Save();
                Catalog.SaveIfChanged();
            }
        }

        private static void Discover(Vector3 pos)
        {
            var radius = Plugin.Radius;
            for (var i = Candidates.Count - 1; i >= 0; i--)
            {
                var candidate = Candidates[i];
                var alive = candidate.Location != null ? (bool)candidate.Location : candidate.View != null && candidate.View.IsValid();
                if (!alive)
                {
                    Candidates.RemoveAt(i);
                    continue;
                }
                // Locations by ground distance (their centre can sit under a hill); objects by real distance, so the
                // chests and spawners of a dungeon (whose inside is high above the world) wait until you are inside.
                var at = candidate.Location != null ? candidate.Location.transform.position : candidate.View.transform.position;
                var distance = candidate.Location != null ? Utils.DistanceXZ(at, pos) : Vector3.Distance(at, pos);
                if (distance > radius) continue;
                Candidates.RemoveAt(i);
                Found(candidate, at);
            }
        }

        private static void Found(Candidate candidate, Vector3 at)
        {
            var thing = candidate.Thing;
            var zdo = candidate.View != null ? candidate.View.GetZDO() : null;
            // Looked at here, not when it loads: a new one gets Epic Loot's mark only after that. Nobody but its buyer can open it.
            if (zdo != null && IsMapChest(thing, zdo))
            {
                if (zdo.GetLong(ZDOVars.s_creator) != Player.m_localPlayer.GetPlayerID()) return;
                thing = Catalog.Get(Catalog.MapChestId) ?? thing;
            }
            var id = zdo != null ? zdo.m_uid : ZDOID.None;
            var known = !id.IsNone() ? Records.OfMember(id) : null;
            if (known != null)
            {
                Identify(known, thing);
                return;
            }

            // One of the same thing close by: it joins that pin (also a pin taken over without its objects).
            var near = Records.Closest(at, thing.Spacing, thing.Id) ??
                       (thing.Kind == Kind.Spawner ? Records.Closest(at, thing.Spacing, Catalog.SpawnerId) : null);
            if (near != null)
            {
                Identify(near, thing);
                near.UnseenFor = 0f;
                if (!id.IsNone())
                {
                    Records.AddMember(near, id);
                    if (near.Members.Count > near.Count)
                    {
                        near.Count = near.Members.Count;
                        MapPins.Refresh(near);
                    }
                }
                return;
            }
            if (!Plugin.AutoPinning) return;
            if (thing.Kind == Kind.Plant && Planted(at)) return;
            var biome = WorldGenerator.instance.GetBiome(at);
            if (!Catalog.IsOn(biome, thing)) return;

            var record = new Record { Thing = thing.Id, Biome = biome, Pos = at };
            if (!id.IsNone()) record.Members.Add(id);
            // A spawner that drops something else than its kind when destroyed shows that (the swamp's evil heart: a seed).
            if (thing.Kind == Kind.Spawner && zdo != null) record.Icon = Catalog.NestIcon(zdo.GetPrefab(), thing);
            // A pickup with a name of its own ("Bog iron", "Meteorite") says that rather than the item it gives.
            var pickable = thing.Kind == Kind.Plant && candidate.View != null ? candidate.View.GetComponent<Pickable>() : null;
            if (pickable != null && !string.IsNullOrEmpty(pickable.m_overrideName)) record.Label = pickable.m_overrideName;
            Records.Add(record);
            MapPins.Refresh(record);
        }

        // Spawner pins that do not know what they spawn yet (taken over from Cartur's, found before spawners were told
        // apart) are named after the nearest spawner at their spot right away: the game hosting the world knows all of
        // its saved objects, also far away (on a server only those near you, the rest are named when they load).
        private static void IdentifySpawners()
        {
            var unknown = Records.All.Where(r => r.Thing == Catalog.SpawnerId && !r.Deleted).ToList();
            if (unknown.Count == 0) return;
            var best = new Dictionary<Record, KeyValuePair<float, ZDO>>();
            foreach (var zdo in ScannedSpawners)
            {
                var thing = Catalog.ForPrefab(zdo.GetPrefab());
                var at = zdo.GetPosition();
                foreach (var record in unknown)
                {
                    var distance = Utils.DistanceXZ(record.Pos, at);
                    if (distance <= thing.Spacing && (!best.TryGetValue(record, out var found) || distance < found.Key))
                        best[record] = new KeyValuePair<float, ZDO>(distance, zdo);
                }
            }
            var doubles = 0;
            foreach (var pair in best)
            {
                // A pin of that spawner already there (taken over twice): this one goes.
                if (Records.ClosestSpawner(pair.Key.Pos, Catalog.ForPrefab(pair.Value.Value.GetPrefab()).Spacing, pair.Key) != null)
                {
                    Gone(pair.Key);
                    doubles++;
                    continue;
                }
                Identify(pair.Key, Catalog.ForPrefab(pair.Value.Value.GetPrefab()));
                Records.AddMember(pair.Key, pair.Value.Value.m_uid);
            }
            Plugin.Log?.LogInfo($"Named {best.Count - doubles} of {unknown.Count} spawner pins after what they spawn" +
                                (doubles > 0 ? $", removed {doubles} doubles." : "."));
        }

        // Sap extractors and the like already standing on the explored map, also far away (the game hosting the world
        // knows all of its saved objects).
        private static void KnownBuilds()
        {
            if (!Plugin.AutoPinning) return;
            var added = 0;
            foreach (var zdo in ScannedBuilds)
            {
                var thing = Catalog.ForPrefab(zdo.GetPrefab());
                if (Records.OfMember(zdo.m_uid) != null) continue;
                var at = zdo.GetPosition();
                if (Explored == null || !Explored(Minimap.instance, at)) continue;
                var near = Records.Closest(at, thing.Spacing, thing.Id);
                if (near != null)
                {
                    Records.AddMember(near, zdo.m_uid);
                    near.Count = Math.Max(near.Count, near.Members.Count);
                    MapPins.Refresh(near);
                    continue;
                }
                var biome = WorldGenerator.instance.GetBiome(at);
                if (!Catalog.IsOn(biome, thing)) continue;
                var record = new Record { Thing = thing.Id, Biome = biome, Pos = at };
                record.Members.Add(zdo.m_uid);
                Records.Add(record);
                MapPins.Refresh(record);
                added++;
            }
            if (added > 0) Plugin.Log?.LogInfo($"Pinned {added} sap extractors already on your map.");
            // The count of a sap extractor pin is how many really stand there (it could grow too big when they were moved).
            foreach (var record in Records.All)
            {
                if (record.Members.Count == 0 || record.Count == record.Members.Count || Catalog.Get(record.Thing)?.Kind != Kind.Built) continue;
                record.Count = record.Members.Count;
                Records.Dirty = true;
                MapPins.Refresh(record);
            }
        }

        // Morkhalla was off by default under another name, so the ones you have been to have no record yet: pinned where
        // your map is explored. A pin you deleted stays deleted (its record is still there).
        private static void KnownMorkhalla()
        {
            if (!Plugin.AutoPinning || Explored == null) return;
            var thing = Catalog.Get(Catalog.MorkhallaId);
            if (thing == null) return;
            var added = 0;
            foreach (var instance in ZoneSystem.instance.m_locationInstances.Values)
            {
                if (instance.m_location == null || Catalog.ForLocation(instance.m_location.m_prefabName) != thing) continue;
                var at = instance.m_position;
                if (!Explored(Minimap.instance, at) || Records.Closest(at, thing.Spacing, thing.Id) != null) continue;
                var biome = WorldGenerator.instance.GetBiome(at);
                if (!Catalog.IsOn(biome, thing)) continue;
                var record = new Record { Thing = thing.Id, Biome = biome, Pos = at };
                Records.Add(record);
                MapPins.Refresh(record);
                added++;
            }
            if (added > 0) Plugin.Log?.LogInfo($"Pinned {added} Morkhalla already on your map.");
        }

        // Pins whose object is no longer in the world: mined out, destroyed, or picked and never growing back. Only where
        // the game hosts the world (it knows all saved objects then; on a server the far ones are not known).
        private static void PruneGone()
        {
            if (!_scanned) return;
            var gone = new List<Record>();
            foreach (var record in Records.All)
            {
                var thing = Catalog.Get(record.Thing);
                // None of a kind anywhere in the world means this mod does not recognise it (a mod changed it): left alone.
                if (thing == null || !Checked(thing) || !ScannedObjects.TryGetValue(KeyOf(thing), out var cells)) continue;
                var reach = Mathf.Max(thing.Spacing, 6f);
                int cx = Mathf.FloorToInt(record.Pos.x / ScanCell), cz = Mathf.FloorToInt(record.Pos.z / ScanCell);
                var found = false;
                for (var x = cx - 1; x <= cx + 1 && !found; x++)
                    for (var z = cz - 1; z <= cz + 1 && !found; z++)
                        if (cells.TryGetValue(((long)x << 32) ^ (uint)z, out var list))
                            foreach (var at in list)
                                if (Utils.DistanceXZ(at, record.Pos) <= reach)
                                {
                                    found = true;
                                    break;
                                }
                if (!found) gone.Add(record);
            }
            foreach (var record in gone) Gone(record);
            if (gone.Count > 0) Plugin.Log?.LogInfo($"Removed {gone.Count} pins of things no longer in the world (mined, destroyed, picked).");
        }

        // What can disappear from the world (places and portals are checked otherwise); spawners of any creature count
        // for each other (a pin may not know its creature yet).
        private static bool Checked(Thing thing) =>
            thing.Kind == Kind.Deposit || thing.Kind == Kind.Beehive || thing.Kind == Kind.Plant || thing.Kind == Kind.Spawner ||
            thing.Kind == Kind.Chest || thing.Kind == Kind.Built;

        private static string KeyOf(Thing thing) =>
            thing.Kind == Kind.Spawner ? Catalog.SpawnerId : thing.Kind == Kind.Deposit ? "deposit" : thing.Id; // any ore there keeps an ore pin

        // --- The world's saved objects, once when a world loads -------------------------------------------------

        private const float ScanCell = 64f;
        private static bool _scanned;
        private static readonly List<ZDO> ScannedSpawners = new List<ZDO>();
        private static readonly List<ZDO> ScannedBuilds = new List<ZDO>();
        // Where the objects of each kind that has pins stand: kind key -> cell -> positions.
        private static readonly Dictionary<string, Dictionary<long, List<Vector3>>> ScannedObjects =
            new Dictionary<string, Dictionary<long, List<Vector3>>>();

        // One pass over every saved object of the world for what the steps after it need: spawners (to name pins that
        // do not know theirs), sap extractors (to pin those already standing) and the objects of every kind that has
        // pins (to find pins whose object is gone). Only where the game hosts the world: it knows all objects then.
        private static void ScanWorld()
        {
            ClearScan();
            if (!ZNet.instance.IsServer()) return;
            var wanted = new HashSet<string>();
            foreach (var record in Records.All)
            {
                var thing = Catalog.Get(record.Thing);
                if (thing != null && Checked(thing)) wanted.Add(KeyOf(thing));
            }
            foreach (var zdo in AllObjects(ZDOMan.instance).Values)
            {
                var thing = Catalog.ForPrefab(zdo.GetPrefab());
                if (thing == null) continue;
                if (IsMapChest(thing, zdo)) thing = Catalog.Get(Catalog.MapChestId) ?? thing;
                if (thing.Kind == Kind.Spawner && thing.Id != Catalog.SpawnerId && zdo.GetLong(ZDOVars.s_creator) == 0L) ScannedSpawners.Add(zdo);
                if (thing.Kind == Kind.Built) ScannedBuilds.Add(zdo);
                if (!Checked(thing)) continue;
                var key = KeyOf(thing);
                if (!wanted.Contains(key)) continue;
                if (thing.Kind == Kind.Plant && thing.RespawnMinutes <= 0f && zdo.GetBool(ZDOVars.s_picked)) continue;
                var at = zdo.GetPosition();
                if (!ScannedObjects.TryGetValue(key, out var cells)) ScannedObjects[key] = cells = new Dictionary<long, List<Vector3>>();
                var cell = ((long)Mathf.FloorToInt(at.x / ScanCell) << 32) ^ (uint)Mathf.FloorToInt(at.z / ScanCell);
                if (!cells.TryGetValue(cell, out var list)) cells[cell] = list = new List<Vector3>();
                list.Add(at);
            }
            _scanned = true;
        }

        private static void ClearScan()
        {
            _scanned = false;
            ScannedSpawners.Clear();
            ScannedBuilds.Clear();
            ScannedObjects.Clear();
        }

        // Pins inside dungeons (from before nothing inside was pinned, or taken over from Cartur's): gone.
        private static void ForgetInside()
        {
            var inside = Records.All.Where(r => !r.IsPortal && Character.InInterior(r.Pos)).ToList();
            foreach (var record in inside) Gone(record);
            if (inside.Count > 0) Plugin.Log?.LogInfo($"Removed {inside.Count} pins inside dungeons.");
        }

        // Places that were marked deleted because a pin of yours stood there (that is how it used to work): not deleted
        // now, they hide while such a pin is there. Once per world and character; the pins removed with "Hand-placed
        // pins" count as standing there.
        private static void UncoverPlaces()
        {
            var marker = Path.Combine(Records.Folder, Records.Key + ".uncovered");
            if (File.Exists(marker)) return;
            var removed = new List<Vector3>();
            var backup = Path.Combine(Records.Folder, Records.Key + ".hand-pins.txt");
            if (File.Exists(backup))
                foreach (var line in File.ReadAllLines(backup))
                {
                    var f = line.Split('\t');
                    if (f.Length > 3 && float.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                        float.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var z)) removed.Add(new Vector3(x, 0f, z));
                }
            var uncovered = 0;
            foreach (var record in Records.All)
            {
                if (!record.Deleted || record.IsPortal || Catalog.Get(record.Thing)?.Kind != Kind.Place) continue;
                if (!MapPins.OtherPinNear(record.Pos, 25f) && !removed.Any(at => Utils.DistanceXZ(at, record.Pos) < 25f)) continue;
                record.Deleted = false;
                Records.Dirty = true;
                uncovered++;
            }
            File.WriteAllText(marker, "1");
            if (uncovered > 0) Plugin.Log?.LogInfo($"{uncovered} places that were hidden by a pin of yours are back.");
        }

        // Inside a dungeon for a few seconds (so all of it has loaded) with no chest left that has loot: its pin is
        // crossed off. Looted chests mostly vanish; the ones that stay are empty.
        private static void CrossOffLooted(Vector3 pos)
        {
            // A dungeon's inside loads with the zone, so its chests are known from outside already; unloaded ones drop out.
            DungeonChests.RemoveAll(view => view == null || !view.IsValid());
            if (!Character.InInterior(pos))
            {
                _insideFor = 0f;
                return;
            }
            _insideFor += 1f;
            if (_insideFor < 5f || !Plugin.CrossOffLooted) return;
            Record dungeon = null;
            var best = 150f;
            foreach (var record in Records.Near(pos, 150f))
            {
                if (record.Checked || record.IsPortal || !Catalog.IsDungeon(Catalog.Get(record.Thing))) continue;
                var distance = Utils.DistanceXZ(record.Pos, pos);
                if (distance < best)
                {
                    best = distance;
                    dungeon = record;
                }
            }
            if (dungeon == null) return;
            // Crossed off only when its chests are known and all empty: no known chest means nothing can be said.
            var chests = 0;
            foreach (var view in DungeonChests)
            {
                var chest = view.GetComponent<Container>();
                if (chest == null || Utils.DistanceXZ(view.transform.position, dungeon.Pos) >= 150f) continue;
                chests++;
                if (chest.GetInventory()?.NrOfItems() > 0) return;
            }
            if (chests == 0) return;
            dungeon.Checked = true;
            Records.Dirty = true;
            MapPins.Refresh(dungeon);
            Plugin.Log?.LogInfo($"Crossed off {Catalog.Get(dungeon.Thing)?.Name}: no loot left inside.");
        }

        // Two pins of the same thing closer together than it shares pins (taken over twice): one pin, with both objects;
        // deleted if either was. Portals: two pins on the same spot (before 1.0.2 every session added one).
        private static void MergeDoubles()
        {
            var merged = 0;
            foreach (var record in Records.All.ToList())
            {
                if (!Records.All.Contains(record)) continue;
                var thing = record.IsPortal ? null : Catalog.Get(record.Thing);
                if (thing == null && !record.IsPortal) continue;
                var twin = record.IsPortal
                    ? Records.Near(record.Pos, 1f, Catalog.PortalId).FirstOrDefault(r => r != record)
                    : Records.Near(record.Pos, thing.Spacing, thing.Id).FirstOrDefault(r => r != record);
                if (twin == null) continue;
                var members = twin.Members.ToList();
                record.Deleted |= twin.Deleted;
                Gone(twin);
                foreach (var id in members) Records.AddMember(record, id);
                MapPins.Refresh(record);
                merged++;
            }
            if (merged > 0) Plugin.Log?.LogInfo($"Merged {merged} double pins.");
        }

        // A spawner pin from before spawners were told apart (taken over from Cartur's, or found earlier) becomes the
        // spawner of what it spawns once that loads.
        private static void Identify(Record record, Thing thing)
        {
            if (thing.Kind != Kind.Spawner || record.Thing != Catalog.SpawnerId || thing.Id == Catalog.SpawnerId) return;
            record.Thing = thing.Id;
            record.Label = null;
            record.Icon = null;
            Records.Dirty = true;
            MapPins.Refresh(record);
        }

        // Grown from a sapling or seed (those get no creator): on cultivated ground or inside a base.
        private static bool Planted(Vector3 at)
        {
            var ground = Heightmap.FindHeightmap(at);
            return (ground != null && ground.IsCultivated(at)) || EffectArea.IsPointInsideArea(at, EffectArea.Type.PlayerBase) != null;
        }

        private static void UpdatePortals()
        {
            Portals.RemoveWhere(p => p == null);
            if (!Plugin.PortalsOn) return;
            foreach (var portal in Portals)
            {
                var view = portal.GetComponent<ZNetView>();
                if (view == null || !view.IsValid()) continue; // also the placement ghost
                var zdo = view.GetZDO();
                var tag = zdo.GetString(ZDOVars.s_tag);
                var record = Records.OfMember(zdo.m_uid);
                if (record == null)
                {
                    // A portal pin without its portal yet (from a cartography table): this is it.
                    record = Records.Near(portal.transform.position, 3f, Catalog.PortalId).FirstOrDefault(r => r.Members.Count == 0);
                    if (record != null) Records.AddMember(record, zdo.m_uid);
                }
                if (record == null)
                {
                    var at = portal.transform.position;
                    record = new Record { Thing = Catalog.PortalId, Biome = WorldGenerator.instance.GetBiome(at), Pos = at, Label = tag };
                    record.Members.Add(zdo.m_uid);
                    Records.Add(record);
                    MapPins.Refresh(record);
                }
                else if ((record.Label ?? "") != tag)
                {
                    record.Label = tag;
                    Records.Dirty = true;
                    MapPins.Refresh(record);
                }
                if (PortalLink.Update(record)) Restyled(record);
            }
        }

        // A portal's look changed (wood or stone, Portal List's icon or colour): its pin again.
        private static void Restyled(Record record)
        {
            MapPins.Refresh(record);
            MapPins.Restyle();
        }

        // Every portal of the world on the explored part of your map, also far away (the game keeps a list of all of them;
        // on a server only of those it sent you). Pins follow the tag; one whose portal is destroyed goes (where the game
        // hosts the world, so it knows that for sure).
        private static bool _portalsLogged;

        private static void KnownPortals()
        {
            if (!Plugin.PortalsOn) return;
            var server = ZNet.instance.IsServer();
            var all = ZDOMan.instance.GetPortalList();
            if (!_portalsLogged)
            {
                _portalsLogged = true;
                Plugin.Log?.LogInfo($"Portals: {all.Count} in the world, {all.Count(z => z != null && Explored != null && Explored(Minimap.instance, z.GetPosition()))} on your explored map.");
            }
            foreach (var zdo in all)
            {
                if (zdo == null) continue;
                var tag = zdo.GetString(ZDOVars.s_tag);
                var record = Records.OfMember(zdo.m_uid);
                if (record == null)
                {
                    var at = zdo.GetPosition();
                    if (Explored == null || !Explored(Minimap.instance, at)) continue;
                    record = Records.Near(at, 3f, Catalog.PortalId).FirstOrDefault(r => r.Members.Count == 0);
                    if (record != null) Records.AddMember(record, zdo.m_uid);
                    else
                    {
                        record = new Record { Thing = Catalog.PortalId, Biome = WorldGenerator.instance.GetBiome(at), Pos = at, Label = tag };
                        record.Members.Add(zdo.m_uid);
                        Records.Add(record);
                        PortalLink.Update(record);
                        MapPins.Refresh(record);
                        continue;
                    }
                }
                if (PortalLink.Update(record)) Restyled(record);
                if ((record.Label ?? "") == tag) continue;
                record.Label = tag;
                Records.Dirty = true;
                MapPins.Refresh(record);
            }
            if (!server) return;
            foreach (var record in Records.All.Where(r => r.IsPortal && r.Members.Count > 0 && r.Members.All(id => ZDOMan.instance.GetZDO(id) == null)).ToList())
                Gone(record);
        }

        // The records around you against what is there now.
        private static void Maintain(Vector3 pos)
        {
            var radius = Plugin.Radius;
            var now = ZNet.instance.GetTime().Ticks;
            Nearby.Clear();
            Nearby.AddRange(Records.Near(pos, radius));
            foreach (var record in Nearby)
            {
                if (!ZoneSystem.instance.IsZoneLoaded(record.Pos)) continue;
                var thing = record.IsPortal ? null : Catalog.Get(record.Thing);
                if (!record.IsPortal && (thing == null || thing.Kind == Kind.Place)) continue;
                if (record.Members.Count == 0)
                {
                    // Taken over without its objects: they join as they load; none within reach for a few seconds = gone.
                    if (Utils.DistanceXZ(record.Pos, pos) > radius * 0.8f) continue;
                    record.UnseenFor += 1f;
                    if (record.UnseenFor >= 5f) Gone(record);
                    continue;
                }
                Check(record, thing, now);
            }
        }

        private static void Check(Record record, Thing thing, long now)
        {
            var missing = Missing;
            missing.Clear();
            var alive = 0;
            var fadedUntil = long.MaxValue;
            var ripe = false;
            var emptied = thing != null && thing.Kind == Kind.Chest;
            foreach (var id in record.Members)
            {
                var zdo = ZDOMan.instance.GetZDO(id);
                if (zdo == null)
                {
                    missing.Add(id);
                    continue;
                }
                if (thing != null && thing.Kind == Kind.Plant)
                {
                    var picked = zdo.GetBool(ZDOVars.s_picked);
                    if (picked && thing.RespawnMinutes <= 0f)
                    {
                        missing.Add(id); // picked, never grows back
                        continue;
                    }
                    if (picked) fadedUntil = Math.Min(fadedUntil, zdo.GetLong(ZDOVars.s_pickedTime) + TimeSpan.FromMinutes(thing.RespawnMinutes).Ticks);
                    else ripe = true;
                }
                if (emptied)
                {
                    var instance = ZNetScene.instance.FindInstance(id);
                    var chest = instance != null ? instance.GetComponent<Container>() : null;
                    if (chest == null || chest.GetInventory() == null || chest.GetInventory().NrOfItems() > 0) emptied = false;
                }
                alive++;
            }

            // Objects missing for a few checks in a row are gone (not just still on their way from the server).
            if (missing.Count > 0)
            {
                record.UnseenFor += 1f;
                if (record.UnseenFor >= 3f)
                {
                    foreach (var id in missing) Records.RemoveMember(record, id);
                    record.UnseenFor = 0f;
                    if (alive == 0)
                    {
                        Gone(record);
                        return;
                    }
                    if (thing != null && record.Count > record.Members.Count)
                    {
                        record.Count = Math.Max(1, record.Members.Count);
                        MapPins.Refresh(record);
                    }
                }
            }
            else record.UnseenFor = 0f;

            // Builds are counted as they stand (each one is there or not; no regrowing, no merging over time).
            if (thing != null && thing.Kind == Kind.Built && missing.Count == 0 && alive > 0 && record.Count != alive)
            {
                record.Count = alive;
                Records.Dirty = true;
                MapPins.Refresh(record);
            }

            if (thing != null && thing.Kind == Kind.Plant && thing.RespawnMinutes > 0f)
            {
                var until = ripe || fadedUntil == long.MaxValue ? 0L : fadedUntil;
                if (until != record.FadedUntil)
                {
                    record.FadedUntil = until;
                    Records.Dirty = true;
                    MapPins.Redraw();
                }
            }
            if (thing != null && thing.Kind == Kind.Chest && alive > 0 && emptied != record.Emptied)
            {
                record.Emptied = emptied;
                Records.Dirty = true;
                MapPins.Refresh(record); // the white style shows an open chest
                MapPins.Redraw();
            }
        }

        private static void Gone(Record record)
        {
            MapPins.Forget(record);
            Records.Remove(record);
        }
    }
}
