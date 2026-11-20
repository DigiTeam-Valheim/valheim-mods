// Spawner Restore: a destroyed spawner (Greydwarf nest, body pile, bone pile, the Ashlands totems, spawners from other
// mods) leaves a pale, see-through ghost where it stood (pulsing slowly). Look at it to see the price in coins (by
// biome, each a setting), press E to pay: the game's own spawner comes back at that exact spot, turned and sized the
// same, at full health.
// Which spawners get a ghost:
// - Every spawner destroyed while this mod runs: it is written into its zone's control object (_ZoneCtrl, one per
//   zone, saved with the world and shared with everyone) as it breaks. Vanilla ignores the extra value.
// - Spawners of places and dungeons destroyed before (Rolls.cs): the place's blueprint and the game's own seeded roll
//   tell where every spawner stood, and one that is no longer there was destroyed.
// Loose spawners (nests, piles in the open) destroyed before this mod leave no trace and get no ghost.
// Only near you: the zones around you are looked at once a second, ghosts show within the ghost distance once their
// zone has been loaded for a moment (so nothing flashes while the area streams in). Raid and event spawners are left out.
using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SpawnerRestore
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.spawnerrestore";
        public const string Name = "Digi's Spawner Restore";
        public const string Version = "1.0.0";

        private static readonly Heightmap.Biome[] Biomes =
        {
            Heightmap.Biome.Meadows, Heightmap.Biome.BlackForest, Heightmap.Biome.Swamp, Heightmap.Biome.Mountain,
            Heightmap.Biome.Plains, Heightmap.Biome.Mistlands, Heightmap.Biome.AshLands, Heightmap.Biome.DeepNorth,
        };
        private static readonly string[] BiomeNames = { "Meadows", "Black Forest", "Swamp", "Mountains", "Plains", "Mistlands", "Ashlands", "Deep North" };
        private static readonly int[] DefaultCosts = { 25, 50, 100, 150, 200, 300, 500, 500 };
        private static readonly ConfigEntry<int>[] Costs = new ConfigEntry<int>[Biomes.Length];
        private static ConfigEntry<bool> _ghosts, _oldOnes;
        private static ConfigEntry<float> _distance;
        private static BepInEx.Logging.ManualLogSource _log;

        private static readonly int RuinsKey = "digi_spawner_ruins".GetStableHashCode();
        private static readonly Color Tint = new Color(0.25f, 0.55f, 0.75f); // the glow when no see-through shader is found
        private static readonly HashSet<int> Spawners = new HashSet<int>();  // prefabs that are spawners
        private static readonly HashSet<int> Dungeons = new HashSet<int>();  // prefabs with a dungeon generator
        private static readonly Dictionary<int, string> Names = new Dictionary<int, string>();
        private static int _proxyHash, _zoneCtrlHash;
        private static ZNetScene _scene; // the world the lists and caches belong to

        private static readonly Dictionary<ZDOID, List<Ruin>> PlaceCache = new Dictionary<ZDOID, List<Ruin>>();
        private static readonly Dictionary<ZDOID, List<Ruin>> DungeonCache = new Dictionary<ZDOID, List<Ruin>>();
        private static readonly Dictionary<string, Ghost> Ghosts = new Dictionary<string, Ghost>();
        private static readonly Dictionary<Vector2s, float> ZoneSeen = new Dictionary<Vector2s, float>();
        private static readonly List<ZDO> Near = new List<ZDO>();
        private float _timer;
        private float _nextWarning;

        private sealed class Ghost
        {
            public Ruin Ruin;
            public GameObject Root, Look;
        }

        private void Awake()
        {
            _log = Logger;
            _ghosts = Config.Bind("General", "Ghosts", true,
                "Destroyed spawners show a see-through ghost where they stood. Look at one to see the price, press E to pay: the " +
                "spawner comes back at that spot.");
            _distance = Config.Bind("General", "Ghost distance (m)", 40f,
                new ConfigDescription("Ghosts show within this many meters of you.", new AcceptableValueRange<float>(10f, 100f)));
            _oldOnes = Config.Bind("General", "Spawners destroyed before", true,
                "Spawners of places and dungeons (the Ashlands totems, crypt and burial chamber piles, ...) destroyed before this " +
                "mod also get a ghost: the place's blueprint and the game's own seeded roll tell where each one stood. Loose " +
                "nests and piles destroyed before leave no trace; those destroyed from now on always get one.");
            for (var i = 0; i < Biomes.Length; i++)
                Costs[i] = Config.Bind("Cost", BiomeNames[i], DefaultCosts[i], new ConfigDescription(
                    $"Coins to restore a spawner standing in the {BiomeNames[i]}. 0 = free. Free anyway in no-cost mode.",
                    new AcceptableValueRange<int>(0, 100000)));
            PatchModule(typeof(Plugin));
        }

        private void OnDestroy()
        {
            RemoveGhosts();
            Forget();
        }

        private void Update()
        {
            _timer -= Time.deltaTime;
            if (_timer > 0f) return;
            _timer = 1f;
            try
            {
                Refresh();
            }
            catch (Exception e)
            {
                if (Time.time < _nextWarning) return;
                _nextWarning = Time.time + 60f;
                Logger.LogWarning("Ghosts: " + e);
            }
        }

        private static void Forget()
        {
            Spawners.Clear();
            Dungeons.Clear();
            Names.Clear();
            PlaceCache.Clear();
            DungeonCache.Clear();
            ZoneSeen.Clear();
            Near.Clear();
            _scene = null;
        }

        // --- What is a spawner -------------------------------------------------------------------------------------

        private static void ReadPrefabs()
        {
            Spawners.Clear();
            Dungeons.Clear();
            Names.Clear();
            foreach (var prefab in ZNetScene.instance.m_prefabs)
            {
                if (prefab == null) continue;
                var hash = prefab.name.GetStableHashCode();
                if (prefab.GetComponent<DungeonGenerator>() != null) Dungeons.Add(hash);
                if (prefab.GetComponent<Destructible>() == null || prefab.GetComponentInChildren<SpawnArea>(true) == null) continue;
                if (prefab.name.Contains("_event") || prefab.name.StartsWith("EvilHeart")) continue; // raids and events make those
                Spawners.Add(hash);
            }
            _proxyHash = ZoneSystem.instance.m_locationProxyPrefab.name.GetStableHashCode();
            _zoneCtrlHash = ZoneSystem.instance.m_zoneCtrlPrefab.name.GetStableHashCode();
            _log?.LogInfo($"{Spawners.Count} kinds of spawners, {Dungeons.Count} kinds of dungeons.");
        }

        internal static string NameOf(int prefab)
        {
            if (Names.TryGetValue(prefab, out var name)) return name;
            var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefab) : null;
            if (go == null) return "Spawner";
            var hover = go.GetComponentInChildren<HoverText>(true);
            if (hover != null && !string.IsNullOrEmpty(hover.m_text)) name = Localization.instance.Localize(hover.m_text);
            else
            {
                // Spawner_GreydwarfNest -> Greydwarf nest, BonePileSpawner -> Bone pile
                var raw = go.name.Replace("Spawner_", "").Replace("Spawner", "").Replace("_", " ").Trim();
                var sb = new StringBuilder();
                for (var i = 0; i < raw.Length; i++)
                {
                    var c = raw[i];
                    if (i > 0 && char.IsUpper(c) && raw[i - 1] != ' ') sb.Append(' ');
                    sb.Append(sb.Length > 0 && char.IsUpper(c) ? char.ToLowerInvariant(c) : c);
                }
                name = sb.Length > 0 ? sb.ToString() : "Spawner";
            }
            Names[prefab] = name;
            return name;
        }

        private static bool IsSpawner(Destructible d) =>
            d != null && d.GetComponentInChildren<SpawnArea>(true) != null && !d.name.Contains("_event") && !d.name.StartsWith("EvilHeart");

        // --- Recording: a spawner that breaks is written into its zone -------------------------------------------------

        [HarmonyPatch(typeof(Destructible), nameof(Destructible.Destroy), typeof(HitData))]
        [HarmonyPrefix]
        private static void Breaking(Destructible __instance)
        {
            try
            {
                if (!IsSpawner(__instance)) return;
                var view = __instance.GetComponent<ZNetView>();
                if (view == null || !view.IsValid()) return;
                var t = __instance.transform;
                var ruin = new Ruin { Prefab = view.GetZDO().GetPrefab(), Pos = t.position, Rot = t.rotation, Scale = t.localScale };
                var zone = ZoneCtrl(ZoneSystem.GetZone(ruin.Pos));
                if (zone == null)
                {
                    _log?.LogWarning($"{__instance.name} at {ruin.Pos:F0} broke, but its zone has no control object: no ghost for it.");
                    return;
                }
                var list = Read(zone);
                foreach (var r in list)
                    if (r.Prefab == ruin.Prefab && Vector3.Distance(r.Pos, ruin.Pos) < 0.5f) return; // already known
                list.Add(ruin);
                Write(zone, list);
                _log?.LogInfo($"{__instance.name} at {ruin.Pos:F1} broke: remembered for a ghost.");
            }
            catch (Exception e)
            {
                _log?.LogWarning("Remembering a broken spawner: " + e.Message);
            }
        }

        // The zone's control object (_ZoneCtrl), made for every zone when it is first built.
        private static ZDO ZoneCtrl(Vector2s zone)
        {
            if (ZDOMan.instance == null || ZNet.instance == null || ZoneSystem.instance == null) return null;
            if (_zoneCtrlHash == 0) _zoneCtrlHash = ZoneSystem.instance.m_zoneCtrlPrefab.name.GetStableHashCode();
            var list = new List<ZDO>();
            ZDOMan.instance.FindSectorObjects(zone, ZNet.instance.GetSyncedSimulationDistance(), list);
            foreach (var zdo in list)
                if (zdo != null && zdo.GetPrefab() == _zoneCtrlHash && ZoneSystem.GetZone(zdo.GetPosition()).Equals(zone)) return zdo;
            return null;
        }

        private static List<Ruin> Read(ZDO zone)
        {
            var list = new List<Ruin>();
            var bytes = zone.GetByteArray(RuinsKey);
            if (bytes == null || bytes.Length == 0) return list;
            var pkg = new ZPackage(bytes);
            var version = pkg.ReadInt();
            if (version != 1) return list;
            var count = pkg.ReadInt();
            for (var i = 0; i < count; i++)
                list.Add(new Ruin { Prefab = pkg.ReadInt(), Pos = pkg.ReadVector3(), Rot = pkg.ReadQuaternion(), Scale = pkg.ReadVector3(), Recorded = true, RecordedIn = zone });
            return list;
        }

        private static void Write(ZDO zone, List<Ruin> list)
        {
            if (!zone.IsOwner())
            {
                var view = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zone) : null;
                if (view != null) view.ClaimOwnership();
                else zone.SetOwner(ZDOMan.GetSessionID());
            }
            var pkg = new ZPackage();
            pkg.Write(1);
            pkg.Write(list.Count);
            foreach (var r in list)
            {
                pkg.Write(r.Prefab);
                pkg.Write(r.Pos);
                pkg.Write(r.Rot);
                pkg.Write(r.Scale);
            }
            zone.Set(RuinsKey, pkg.GetArray());
        }

        // --- Ghosts ----------------------------------------------------------------------------------------------------

        private static void Refresh()
        {
            var player = Player.m_localPlayer;
            if (ZNetScene.instance == null || ZoneSystem.instance == null || ZDOMan.instance == null || ZNet.instance == null)
            {
                RemoveGhosts();
                Forget();
                return;
            }
            if (!_ghosts.Value || player == null)
            {
                RemoveGhosts();
                return;
            }
            if (_scene != ZNetScene.instance)
            {
                RemoveGhosts();
                Forget();
                _scene = ZNetScene.instance;
                ReadPrefabs();
            }
            var me = player.transform.position;
            var range = _distance.Value;
            Near.Clear();
            ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(me), ZNet.instance.GetSyncedSimulationDistance(), Near);
            var standing = new List<ZDO>();
            var wanted = new List<Ruin>();
            foreach (var zdo in Near)
            {
                if (zdo == null) continue;
                var prefab = zdo.GetPrefab();
                if (Spawners.Contains(prefab)) standing.Add(zdo);
                else if (prefab == _zoneCtrlHash)
                {
                    if (Utils.DistanceXZ(zdo.GetPosition(), me) < range + 64f) wanted.AddRange(Read(zdo));
                }
                else if (_oldOnes.Value && prefab == _proxyHash)
                {
                    if (Utils.DistanceXZ(zdo.GetPosition(), me) < range + 80f) wanted.AddRange(AtPlace(zdo));
                }
                else if (_oldOnes.Value && Dungeons.Contains(prefab) && Utils.DistanceXZ(zdo.GetPosition(), me) < range + 150f)
                    wanted.AddRange(InDungeon(zdo));
            }

            var show = new Dictionary<string, Ruin>();
            foreach (var ruin in wanted)
            {
                if (Vector3.Distance(me, ruin.Pos) > range) continue;
                var zone = ZoneSystem.GetZone(ruin.Pos);
                if (!ZoneSystem.instance.IsZoneLoaded(ruin.Pos))
                {
                    ZoneSeen.Remove(zone);
                    continue;
                }
                if (!ZoneSeen.TryGetValue(zone, out var since)) ZoneSeen[zone] = since = Time.time;
                if (Time.time - since < 3f) continue; // the area is still coming in
                if (Standing(standing, ruin)) continue;
                var key = Key(ruin);
                if (show.TryGetValue(key, out var known) && known.Recorded) continue; // the saved one: restoring clears it
                show[key] = ruin;
            }

            var gone = new List<string>();
            foreach (var pair in Ghosts)
                if (!show.ContainsKey(pair.Key) || pair.Value.Root == null) gone.Add(pair.Key);
            foreach (var key in gone) RemoveGhost(key);
            foreach (var pair in show)
            {
                if (Ghosts.TryGetValue(pair.Key, out var ghost))
                {
                    ghost.Ruin = pair.Value;
                    var hover = ghost.Root.GetComponentInChildren<RuinHover>();
                    if (hover != null) hover.Ruin = pair.Value;
                    continue;
                }
                ghost = MakeGhost(pair.Value);
                if (ghost != null) Ghosts[pair.Key] = ghost;
            }
        }

        private static string Key(Ruin r) => $"{r.Prefab}:{Mathf.RoundToInt(r.Pos.x)}:{Mathf.RoundToInt(r.Pos.y)}:{Mathf.RoundToInt(r.Pos.z)}";

        private static bool Standing(List<ZDO> standing, Ruin ruin)
        {
            foreach (var zdo in standing)
                if (zdo.GetPrefab() == ruin.Prefab && Vector3.Distance(zdo.GetPosition(), ruin.Pos) < 1.5f) return true;
            return false;
        }

        private static List<Ruin> AtPlace(ZDO proxy)
        {
            if (PlaceCache.TryGetValue(proxy.m_uid, out var list)) return list;
            try
            {
                list = Rolls.AtPlace(proxy, Spawners);
                if (list.Count > 0) _log?.LogInfo($"Place at {proxy.GetPosition():F0}: {list.Count} spawners in its roll.");
            }
            catch (Exception e)
            {
                list = new List<Ruin>();
                _log?.LogWarning($"Place at {proxy.GetPosition():F0}: its roll failed ({e.Message}); no ghosts there.");
            }
            PlaceCache[proxy.m_uid] = list;
            return list;
        }

        private static List<Ruin> InDungeon(ZDO zdo)
        {
            if (DungeonCache.TryGetValue(zdo.m_uid, out var list)) return list;
            var view = ZNetScene.instance.FindInstance(zdo);
            var dungeon = view != null ? view.GetComponent<DungeonGenerator>() : null;
            if (dungeon == null) return new List<Ruin>(); // not loaded yet: asked again later
            try
            {
                list = Rolls.InDungeon(dungeon, zdo, Spawners);
                if (list.Count > 0) _log?.LogInfo($"Dungeon at {zdo.GetPosition():F0}: {list.Count} spawners in its rooms' rolls.");
            }
            catch (Exception e)
            {
                list = new List<Ruin>();
                _log?.LogWarning($"Dungeon at {zdo.GetPosition():F0}: its roll failed ({e.Message}); no ghosts there.");
            }
            DungeonCache[zdo.m_uid] = list;
            return list;
        }

        // Only the looks of the spawner (no scripts, physics, lights, sounds or particles), with a glow, plus a box to look at.
        private static Ghost MakeGhost(Ruin ruin)
        {
            var prefab = ZNetScene.instance.GetPrefab(ruin.Prefab);
            if (prefab == null) return null;
            var root = new GameObject("DigiSpawnerGhost");
            root.transform.position = ruin.Pos;
            GameObject look;
            ZNetView.m_forceDisableInit = true;
            try { look = Instantiate(prefab, ruin.Pos, ruin.Rot); }
            finally { ZNetView.m_forceDisableInit = false; }
            look.transform.SetParent(root.transform, true);
            look.transform.localScale = ruin.Scale;
            var scripts = look.GetComponentsInChildren<MonoBehaviour>(true);
            for (var i = scripts.Length - 1; i >= 0; i--)
                if (scripts[i] != null) DestroyImmediate(scripts[i]);
            foreach (var joint in look.GetComponentsInChildren<Joint>(true)) DestroyImmediate(joint);
            foreach (var body in look.GetComponentsInChildren<Rigidbody>(true)) DestroyImmediate(body);
            foreach (var collider in look.GetComponentsInChildren<Collider>(true)) DestroyImmediate(collider);
            foreach (var light in look.GetComponentsInChildren<Light>(true)) DestroyImmediate(light);
            foreach (var part in look.GetComponentsInChildren<Component>(true))
            {
                if (part == null) continue;
                var type = part.GetType().Name;
                if (type == "AudioSource") DestroyImmediate(part);
                else if (type == "ParticleSystem") part.gameObject.SetActive(false);
            }
            var ghostLayer = LayerMask.NameToLayer("ghost");
            foreach (var t in look.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = ghostLayer;
            SeeThrough(root, look);

            var bounds = new Bounds(ruin.Pos + Vector3.up, Vector3.zero);
            var any = false;
            foreach (var r in look.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled || r is ParticleSystemRenderer) continue;
                if (!any) bounds = r.bounds;
                else bounds.Encapsulate(r.bounds);
                any = true;
            }
            var hover = new GameObject("Hover");
            hover.transform.SetParent(root.transform, false);
            hover.transform.position = bounds.center;
            hover.transform.rotation = Quaternion.identity;
            var layer = LayerMask.NameToLayer("character_ghost");
            hover.layer = layer >= 0 ? layer : LayerMask.NameToLayer("piece_nonsolid");
            var box = hover.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = Vector3.Max(bounds.size, new Vector3(1f, 1f, 1f));
            hover.AddComponent<RuinHover>().Ruin = ruin;
            return new Ghost { Ruin = ruin, Root = root, Look = look };
        }

        // A pale blue, see-through copy of every material (keeping the model's texture), pulsing slowly. Sprites/Default is
        // always in the game; without it the model keeps its own materials with a blue glow.
        private static void SeeThrough(GameObject root, GameObject look)
        {
            var shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                if (MaterialMan.instance != null) MaterialMan.instance.SetValue(look, ShaderProps._EmissionColor, Tint);
                return;
            }
            var pulse = root.AddComponent<GhostPulse>();
            foreach (var r in look.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer) continue;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                var originals = r.sharedMaterials;
                var copies = new Material[originals.Length];
                for (var i = 0; i < originals.Length; i++)
                {
                    var m = new Material(shader) { renderQueue = 3000 };
                    var o = originals[i];
                    if (o != null && o.HasProperty("_MainTex") && o.mainTexture != null)
                    {
                        m.mainTexture = o.mainTexture;
                        m.mainTextureScale = o.mainTextureScale;
                        m.mainTextureOffset = o.mainTextureOffset;
                    }
                    copies[i] = m;
                    pulse.Materials.Add(m);
                }
                r.sharedMaterials = copies;
            }
            pulse.Apply(0f);
        }

        private static void RemoveGhost(string key)
        {
            if (!Ghosts.TryGetValue(key, out var ghost)) return;
            Ghosts.Remove(key);
            // no MaterialMan.UnregisterRenderers here: the see-through look was never registered (the game logged "Can't
            // unregister renderer"), and the glow fallback's MaterialManNotifier unregisters itself when destroyed
            if (ghost.Root != null) Destroy(ghost.Root);
        }

        private static void RemoveGhosts()
        {
            foreach (var key in new List<string>(Ghosts.Keys)) RemoveGhost(key);
        }

        // --- Restoring -------------------------------------------------------------------------------------------------

        internal static int Cost(Ruin ruin)
        {
            if (WorldGenerator.instance == null) return 0;
            var i = Array.IndexOf(Biomes, WorldGenerator.instance.GetBiome(ruin.Pos.x, ruin.Pos.z, -1000f)); // never "Ocean"
            return i >= 0 ? Costs[i].Value : Costs[0].Value;
        }

        private static bool Free(Player player) => player != null && player.NoCostCheat();

        internal static string HoverText(Ruin ruin)
        {
            var player = Player.m_localPlayer;
            var cost = Cost(ruin);
            var price = cost <= 0 || Free(player) ? "free" : $"{cost} coins";
            var text = $"{NameOf(ruin.Prefab)} (destroyed)\n[<color=yellow><b>$KEY_Use</b></color>] Restore ({price})";
            if (cost > 0 && !Free(player) && player != null)
            {
                var have = player.GetInventory().CountItems("$item_coins");
                if (have < cost) text += $"\n<color=#ff6060>You have {have} coins</color>";
            }
            return Localization.instance.Localize(text);
        }

        internal static bool Restore(Ruin ruin, Player player)
        {
            if (player == null || ZNetScene.instance == null) return false;
            var prefab = ZNetScene.instance.GetPrefab(ruin.Prefab);
            if (prefab == null) return false;
            var name = NameOf(ruin.Prefab);
            var cost = Free(player) ? 0 : Cost(ruin);
            if (cost > 0)
            {
                var have = player.GetInventory().CountItems("$item_coins");
                if (have < cost)
                {
                    player.Message(MessageHud.MessageType.Center, $"Restoring the {name.ToLowerInvariant()} costs {cost} coins (you have {have})");
                    return false;
                }
                player.GetInventory().RemoveItem("$item_coins", cost);
            }
            var go = Instantiate(prefab, ruin.Pos, ruin.Rot);
            var view = go.GetComponent<ZNetView>();
            if (view != null && (ruin.Scale - prefab.transform.localScale).sqrMagnitude > 0.0001f) view.SetLocalScale(ruin.Scale);
            var area = go.GetComponentInChildren<SpawnArea>();
            if (area != null) area.m_spawnEffects.Create(ruin.Pos, ruin.Rot);
            if (ruin.Recorded && ruin.RecordedIn != null && ruin.RecordedIn.IsValid())
            {
                var list = Read(ruin.RecordedIn);
                list.RemoveAll(r => r.Prefab == ruin.Prefab && Vector3.Distance(r.Pos, ruin.Pos) < 0.5f);
                Write(ruin.RecordedIn, list);
            }
            RemoveGhost(Key(ruin));
            player.Message(MessageHud.MessageType.Center, cost > 0 ? $"{name} restored (-{cost} coins)" : $"{name} restored");
            _log?.LogInfo($"{prefab.name} restored at {ruin.Pos:F1} for {cost} coins.");
            return true;
        }
    }

    // The ghost's see-through materials: the alpha breathes slowly between faint and clearer; thrown away with the ghost.
    internal class GhostPulse : MonoBehaviour
    {
        internal readonly List<Material> Materials = new List<Material>();
        private static readonly Color Pale = new Color(0.55f, 0.85f, 1f);
        private float _phase;

        private void Awake() => _phase = UnityEngine.Random.value * 6.28f;

        private void Update() => Apply(Time.time);

        internal void Apply(float time)
        {
            var c = Pale;
            c.a = 0.32f + 0.1f * Mathf.Sin(time * 1.6f + _phase);
            foreach (var m in Materials)
                if (m != null) m.color = c;
        }

        private void OnDestroy()
        {
            foreach (var m in Materials)
                if (m != null) Destroy(m);
            Materials.Clear();
        }
    }

    internal class RuinHover : MonoBehaviour, Hoverable, Interactable
    {
        public Ruin Ruin;

        public string GetHoverText() => Ruin != null ? Plugin.HoverText(Ruin) : "";
        public string GetHoverName() => Ruin != null ? Plugin.NameOf(Ruin.Prefab) : "";
        public float GetHoverOffset() => 1f;
        public bool Interact(Humanoid user, bool hold, bool alt) => !hold && Ruin != null && Plugin.Restore(Ruin, user as Player);
        public bool UseItem(Humanoid user, ItemDrop.ItemData item) => false;
    }
}
