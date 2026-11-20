// Chest Snapping: chests, rugs, fermenters and furniture have no snap points in the game, so they never line up and Q / E
// (the game's keys that pick which snap point of the piece you hold goes onto the aimed spot) do nothing while placing one.
// - Chests (any piece with a container at its root: wood, reinforced, personal, black metal and Grausten chests,
//   barrels, the wardrobe, modded chests) get fourteen snap points: the eight corners of their box, the middle of each
//   bottom edge and the middle of their top and bottom. A chest snaps flush next to, on top of or below another chest
//   (two of different sizes centred on each other).
// - Rugs (the game's and Nordic Rugs', not the wall versions) and fermenters get nine on their bottom: the corners, the
//   middle of each edge and the middle. Rugs lie flush side by side, fermenters stand flush side by side.
// - Furniture (chairs, stools, benches, thrones: any piece you can sit on, not ships or hot tubs; tables, not crafting
//   tables; beds: the simple bed, the dragon bed, the Ashwood bed, other mods' beds) gets nine on its bottom too, from its
//   visible model: furniture stands flush side by side (a chair against a table, two benches in a row, beds in a row).
//   The hot tub counts as furniture too (its own switch), though it has eight snap points of its own around its base
//   (0.5 and 1.5 m out from its middle, so they never put it on the floor grid): it gets one more at its middle, which
//   goes onto the tile's corner, edge middle (the middle of the seam between two floors) or middle, at any turn.
// - The 4 x 4 floors (Grausten and stone) get snap points at the middle of each edge and in the middle, on top and
//   below: they snap to other floors every 2 m, like four 2 x 2 tiles, and count as four tiles below.
// - The hanging brazier (and any piece that hangs from a ceiling) gets one point at the top of its chain: it snaps under
//   a floor tile by the 3 x 3 zones too, its chain onto the tile's corner, edge middle or dead centre (no flush edges).
// - A sconce gets one point on its back plate. Aimed at a standing beam (a pole, a beam stood up, a pillar: upright,
//   thin, with snap points at both ends), it goes flush against the side aimed at, centred on it, turned to face out
//   from it, at a height in 25 cm steps from the beam's foot. Elsewhere it is placed as the game does.
// - Standing lights (the lava lantern, the standing brazier and the blue one, the iron fire pit) get nine points on their
//   base like rugs: flush side by side and onto floor tiles by the 3 x 3 zones; the lantern and the braziers aimed at a
//   beam go on top of it: centred on a standing beam's top, or on a lying beam's top, centred across it and along it
//   flush with an end or in the middle.
// - Machines (anything that works like a smelter: the smelter, the blast furnace, the charcoal kiln, the eitr refinery,
//   the windmill, the spinning wheel, the Frost kiln, other mods' ones; and the Frost Foundry) get nine points on their
//   base, sized from their colliders (the windmill from its stone base, not its blade arms): flush side by side and onto
//   floor tiles by the 3 x 3 zones. The Frost Foundry keeps its own four corner points too (3.5 x 5.5 m: they never put
//   it on the 2 m floor grid). The oven needs nothing: its own points are its 2 x 2 m base, so it already fits a tile.
// - Portals (the wood and stone portals, other mods' ones: anything with a teleport frame) get nine points on their base,
//   sized from their colliders, like machines: flush side by side and onto floor tiles by the 3 x 3 zones. Wider than a
//   tile, a portal goes with its middle onto the seam between two floor pieces or onto a tile's middle. Should a portal
//   have snap points of its own, they stay (ours come beside them, like the Frost Foundry's).
// - Item stands: the standing one snaps like the lanterns (nine points on its base: onto floor tiles by the 3 x 3 zones,
//   flush side by side, on top of beams). The wall one gets one point on its back plate: aimed at a wall (a flat
//   upright piece, 0.9 m or more each way, with snap points around it) its back goes flush onto the face aimed at,
//   facing out, by the 3 x 3 zones of that wall (a corner, flush with an edge and centred along it, or the middle; a
//   wall with snap points every 2 m or more counts as tiles between them); aimed at a standing beam, like a sconce.
// - The cartography table snaps like furniture (it is no crafting table for this).
// - Each kind snaps flush to its own kind (when one is within reach), and onto floor tiles by where you aim: the tile
//   under the crosshair (any flat piece with a rectangle of snap points on top: floors of any size; a floor with snap
//   points every 2 m or more, like the 4 x 4 floors, is split into tiles between them) is split into 3 x 3 zones. Aim near a corner of the tile and the piece's matching corner goes into it (the piece
//   lying over the tile from there); near the middle of an edge and the piece lies flush with that edge, centred along
//   it; in the middle and it is centred on the tile. So a wardrobe goes flush against the tile edge at the wall, a
//   fermenter into the middle of a tile, a rug into a tile's corner. With Q / E a chosen point of the piece snaps onto
//   the nearest corner, edge middle or middle of a tile instead. The floors themselves are not changed: they snap to
//   each other exactly as before. Walls and floors never snap onto these pieces; a setting lets them all snap to any
//   piece's snap points.
// The points are added to each piece in the world (and to the placement ghost) the first time the game asks for its
// snap points: sized from its collider; a Nordic rug from its woven area: its size in Nordic Rugs' catalog, less the
// see-through border of its picture (2-7% on each side, read once from the picture in Nordic Rugs' file; its collider
// is only 90% of the rug, and its tassels hang over the line like a real rug's); another rug from its visible cloth;
// the Grausten chest as measured; furniture from its model (the low-detail copies left out).
// Pieces that already have snap points are left alone (but for the 4 x 4 floors, the hot tub, the Frost Foundry and portals). Switched off, or a setting changed, the
// points are taken away again (and given anew where still wanted).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ChestSnapping
{
    public class Plugin : DigiCore.DigiModule
    {
        public const string Guid = "digi.valheim.chestsnapping";
        public const string Name = "Digi's Chest Snapping";
        public const string Version = "1.4.1";

        // The kinds of piece that get snap points (0: none). Our points' names start with the kind's word (the game shows
        // the name when Q / E picks one).
        private const int None = 0, Chest = 1, Rug = 2, Fermenter = 3, Furniture = 4, Hanging = 5, Sconce = 6, Light = 7, Machine = 8, Portal = 9, Stand = 10;
        private static readonly string[] Marks = { "", "Chest ", "Rug ", "Fermenter ", "Furniture ", "Hanging ", "Sconce ", "Light ", "Machine ", "Portal ", "Item stand " };
        private static readonly HashSet<string> Lights = new HashSet<string> { "piece_Lavalantern", "piece_brazierfloor01", "piece_brazierfloor02", "fire_pit_iron" };
        private static readonly HashSet<string> OnBeams = new HashSet<string> { "piece_Lavalantern", "piece_brazierfloor01", "piece_brazierfloor02" }; // also on top of beams
        private const string FloorMark = "Floor "; // our extra points on the 4 x 4 floors
        private const string HotTub = "piece_bathtub", FrostFoundry = "piece_FrostFoundry";
        private static readonly HashSet<string> BigFloors = new HashSet<string> { "Piece_grausten_floor_4x4", "stone_floor" };
        private const float MinTile = 1.9f; // a floor's snap points split it into tiles only when every tile is this big
        private const float SconceStep = 0.25f; // a sconce's height on a beam, in steps from the beam's foot

        private static ConfigEntry<bool> _otherPieces, _floorTiles, _rugs, _fermenters, _seats, _tables, _beds, _hotTub, _machines, _portals, _bigFloors, _hanging, _sconces, _lights;
        private static ConfigEntry<bool> _itemStands, _mapTable;
        private static readonly Dictionary<int, int> Kinds = new Dictionary<int, int>();             // piece -> kind
        private static readonly Dictionary<int, Vector3[]> Tiles = new Dictionary<int, Vector3[]>(); // piece -> its targets
        private static readonly Dictionary<int, float> Unders = new Dictionary<int, float>();        // piece -> its underside (own space)
        private static readonly Dictionary<int, Beam> Beams = new Dictionary<int, Beam>();          // piece -> standing beam or not
        private static readonly Dictionary<int, Wall> Walls = new Dictionary<int, Wall>();          // piece -> wall or not
        private static readonly Dictionary<string, StandShape> StandShapes = new Dictionary<string, StandShape>(); // wall item stands by prefab

        // What the placement ray hit this frame (a sconce goes onto the beam you aim at).
        private static Piece _hitPiece;
        private static Vector3 _hitPoint;
        private static int _hitFrame = -1;

        private class Beam
        {
            public Bounds Box; // its visible model, own space
            public int Axis;   // its own long axis (0 x, 1 y, 2 z)
            public int Up;     // its own axis that points up (or down)
            public bool Standing => Axis == Up;
        }
        // A wall: a flat upright piece. Its own axes: Normal (its thickness), Across (level, along the wall), Up; the lines its
        // snap points lie on along Across and Up (tiles between them, as on floors).
        private class Wall
        {
            public Bounds Box; // its visible model, own space
            public int Normal, Across, Up;
            public List<float> LinesAcross, LinesUp;
        }

        // A wall item stand: Front is its own direction out from the wall (the side its item hangs on), Wide its level axis
        // across, Box its visible model.
        private class StandShape
        {
            public Vector3 Front;
            public int Wide;
            public Bounds Box;
        }

        private static readonly HashSet<Transform> Roots = new HashSet<Transform>();
        private static Transform _tile; // the target handed to the game when a piece snaps onto a floor tile
        private static readonly HashSet<int> Seen = new HashSet<int>();
        private static readonly FieldInfo AllPieces = AccessTools.Field(typeof(Piece), "s_allPieces");
        private static readonly AccessTools.FieldRef<Player, List<Transform>> GhostPoints = AccessTools.FieldRefAccess<Player, List<Transform>>("m_tempSnapPoints1");
        private static readonly AccessTools.FieldRef<Player, List<Transform>> NearPoints = AccessTools.FieldRefAccess<Player, List<Transform>>("m_tempSnapPoints2");
        private static readonly AccessTools.FieldRef<Player, int> ManualPoint = AccessTools.FieldRefAccess<Player, int>("m_manualSnapPoint");

        private void Awake()
        {
            _otherPieces = Config.Bind("Chest Snapping", "Snap to other pieces", false,
                "Chests, rugs, fermenters and furniture also snap to the snap points of walls, beams and other pieces, and " +
                "those to them. Off: each snaps to its own kind and to floor tiles only, so a chest near a wall stays where you aim.");
            _floorTiles = Config.Bind("Chest Snapping", "Snap to floor tiles", true,
                "Chests, rugs, fermenters and furniture snap onto floor tiles (and other flat pieces): flush with a tile's " +
                "edge and centred along it, or right in the middle of the tile, so they do not hang over onto the next tile. " +
                "The floors themselves snap as before.");
            _rugs = Config.Bind("Chest Snapping", "Rugs", true,
                "Rugs (the game's and Nordic Rugs', not the wall versions) get snap points too: flush side by side, and onto " +
                "floor tiles.");
            _fermenters = Config.Bind("Chest Snapping", "Fermenters", true,
                "Fermenters get snap points too: flush side by side, and onto floor tiles (in the middle of a tile).");
            _seats = Config.Bind("Chest Snapping", "Chairs benches and thrones", true,
                "Everything you can sit on (stools, chairs, benches, thrones, also other mods' seats; not ships or the hot tub) " +
                "gets snap points too: flush side by side and against tables, and onto floor tiles.");
            _tables = Config.Bind("Chest Snapping", "Tables", true,
                "Tables (not crafting tables like the workbench or the cartography table) get snap points too: flush side by " +
                "side and against chairs and benches, and onto floor tiles.");
            _beds = Config.Bind("Chest Snapping", "Beds", true,
                "Beds (the simple bed, the dragon bed, the Ashwood bed, also other mods' beds) get snap points too: flush side " +
                "by side and against other furniture, and onto floor tiles (a bed wider than a tile goes with its middle onto " +
                "the seam between two floor pieces or onto a tile's middle).");
            _hotTub = Config.Bind("Chest Snapping", "Hot tub", true,
                "The hot tub snaps onto floor tiles by its middle, at any turn: onto a corner where floors meet, the middle of the " +
                "seam between two floor pieces, or a tile's middle, by the 3 x 3 zone its middle is over (turned along the floor, " +
                "its stove then lies on a seam). Its own eight points around its base stay for Q / E.");
            _machines = Config.Bind("Chest Snapping", "Machines", true,
                "Smelters, the blast furnace, charcoal kilns, the eitr refinery, the windmill, the spinning wheel, the Frost kiln " +
                "and the Frost Foundry (also other mods' machines that work like a smelter) get snap points too: flush side by " +
                "side and onto floor tiles by the 3 x 3 zones (one wider than a tile goes with its middle onto the seam between " +
                "two floor pieces or onto a tile's middle). The oven needs nothing: its own points already fit a floor tile.");
            _portals = Config.Bind("Chest Snapping", "Portals", true,
                "The wood and stone portals (also other mods' portals) get snap points on their base: flush side by side and " +
                "onto floor tiles by the 3 x 3 zones. A portal is wider than a floor tile, so across it its middle goes onto the " +
                "seam between two floor pieces (aim near the seam) or onto a tile's middle; front to back it lies flush with " +
                "the tile's edge or centred on it.");
            _bigFloors = Config.Bind("Chest Snapping", "4x4 floors as four 2x2 tiles", true,
                "The 4 x 4 floors (Grausten and stone) get snap points in the middle of each edge and in the middle: other " +
                "floors, walls and beams snap to them every 2 m, as if they were four 2 x 2 floors, and chests, rugs and " +
                "furniture see four tiles of 3 x 3 zones on them (6 x 6 in all).");
            _hanging = Config.Bind("Chest Snapping", "Hanging braziers", true,
                "The hanging brazier (and other pieces that hang from a ceiling) snaps under floor tiles by where you aim: to a " +
                "corner, the middle of an edge or the dead centre of the tile (on a 4 x 4 floor every metre).");
            _sconces = Config.Bind("Chest Snapping", "Sconces on beams", true,
                "A sconce aimed at a standing beam, pole or pillar goes flush against the side you aim at, centred on it and " +
                "facing out from it, its height in 25 cm steps from the beam's foot.");
            _lights = Config.Bind("Chest Snapping", "Lanterns braziers and fire pit", true,
                "The lava lantern, the standing braziers (also the blue one) and the iron fire pit snap onto floor tiles by the " +
                "3 x 3 zones and flush side by side; the lantern and the braziers also onto beams: centred on top of a standing " +
                "beam or post, or on top of a lying beam (at an end or in the middle, by where you aim).");
            _itemStands = Config.Bind("Chest Snapping", "Item stands", true,
                "Item stands snap too. The standing one like the lanterns: onto floor tiles by the 3 x 3 zones, flush side by " +
                "side, and on top of beams. The wall one onto walls by where you aim: into a corner of the wall, flush with an " +
                "edge and centred along it, or in the middle, facing out (a wall with snap points every 2 m counts as tiles); " +
                "onto a standing beam like a sconce.");
            _mapTable = Config.Bind("Chest Snapping", "Cartography table", true,
                "The cartography table snaps like furniture: flush side by side and against tables and benches, and onto floor tiles.");
            _itemStands.SettingChanged += Changed;
            _mapTable.SettingChanged += Changed;
            _rugs.SettingChanged += Changed;
            _fermenters.SettingChanged += Changed;
            _seats.SettingChanged += Changed;
            _tables.SettingChanged += Changed;
            _beds.SettingChanged += Changed;
            _hotTub.SettingChanged += Changed;
            _machines.SettingChanged += Changed;
            _portals.SettingChanged += Changed;
            _bigFloors.SettingChanged += Changed;
            _hanging.SettingChanged += Changed;
            _sconces.SettingChanged += Changed;
            _lights.SettingChanged += Changed;
            Seen.Clear();
            PatchModule(typeof(Plugin));
        }

        // A kind switched on or off: the points go and come back as the settings now say.
        private static void Changed(object sender, EventArgs e) => Reset();

        // Switched off: the pieces in the world lose the points again.
        private void OnDestroy()
        {
            _itemStands.SettingChanged -= Changed;
            _mapTable.SettingChanged -= Changed;
            _rugs.SettingChanged -= Changed;
            _fermenters.SettingChanged -= Changed;
            _seats.SettingChanged -= Changed;
            _tables.SettingChanged -= Changed;
            _beds.SettingChanged -= Changed;
            _hotTub.SettingChanged -= Changed;
            _machines.SettingChanged -= Changed;
            _portals.SettingChanged -= Changed;
            _bigFloors.SettingChanged -= Changed;
            _hanging.SettingChanged -= Changed;
            _sconces.SettingChanged -= Changed;
            _lights.SettingChanged -= Changed;
            Reset();
            _woven = null;
            if (_tile != null) Destroy(_tile.parent != null ? _tile.parent.gameObject : _tile.gameObject);
            _tile = null;
        }

        private static void Reset()
        {
            Seen.Clear();
            Kinds.Clear();
            Tiles.Clear();
            Unders.Clear();
            Beams.Clear();
            Walls.Clear();
            StandShapes.Clear();
            _hitPiece = null;
            if (!(AllPieces?.GetValue(null) is List<Piece> pieces)) return;
            foreach (var piece in pieces)
            {
                if (piece == null) continue;
                for (var i = piece.transform.childCount - 1; i >= 0; i--)
                {
                    var child = piece.transform.GetChild(i);
                    if (!child.CompareTag("snappoint") || !Ours(child.name)) continue;
                    child.SetParent(null, false); // gone at once: the piece may be read again before the end of the frame
                    Destroy(child.gameObject);
                }
            }
        }

        private static bool Ours(string name)
        {
            if (name.StartsWith(FloorMark, StringComparison.Ordinal)) return true;
            for (var k = 1; k < Marks.Length; k++)
                if (name.StartsWith(Marks[k], StringComparison.Ordinal)) return true;
            return false;
        }

        private static int KindOf(Transform root)
        {
            if (root == null || root.GetComponent<Piece>() == null) return None;
            if (root.GetComponent<Container>() != null) return Chest;
            if (_fermenters.Value && root.GetComponentInChildren<global::Fermenter>() != null) return Fermenter;
            // An item stand: the standing one (horizontal) works like the lanterns, the wall one (vertical) has a kind of its own.
            var stand = root.GetComponentInChildren<ItemStand>();
            if (_itemStands.Value && stand != null && !OwnPoints(root) && !Moves(root))
                return stand.m_orientationType == ItemStand.Orientation.Horizontal ? Light : Stand;
            // The cartography table: like furniture (IsFurniture leaves it out, as no crafting table is furniture).
            if (_mapTable.Value && root.GetComponentInChildren<MapTable>() != null && !OwnPoints(root) && !Moves(root)) return Furniture;
            var name = Utils.GetPrefabName(root.gameObject);
            if (_hanging.Value && (name == "piece_brazierceiling01" || root.GetComponent<Piece>().m_inCeilingOnly) && !OwnPoints(root)) return Hanging;
            if (_sconces.Value && name == "piece_walltorch" && !OwnPoints(root)) return Sconce;
            if (_lights.Value && Lights.Contains(name) && !OwnPoints(root)) return Light;
            if (_rugs.Value)
            {
                if (name.StartsWith("rug_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("nr_rug_", StringComparison.OrdinalIgnoreCase))
                    if (name.IndexOf("_wall", StringComparison.OrdinalIgnoreCase) < 0) return Rug;
            }
            // The hot tub: it has a fire (so it is no furniture below) and eight snap points of its own around its base, which
            // never put it on the floor grid (they are 0.5 and 1.5 m out from its middle): it gets its middle as well.
            if (_hotTub.Value && name == HotTub) return Furniture;
            // A machine: works like a smelter (and has no snap points of its own, and does not move: not the battering ram),
            // or the Frost Foundry (it keeps its own).
            if (_machines.Value && (name == FrostFoundry || (root.GetComponentInChildren<Smelter>() != null && !OwnPoints(root) && !Moves(root))))
                return Machine;
            // A portal (anything with a teleport frame: the wood and stone portals, other mods' ones). Should one have snap
            // points of its own, it keeps them and gets ours beside them.
            if (_portals.Value && root.GetComponentInChildren<TeleportWorld>() != null && !Moves(root)) return Portal;
            if ((_seats.Value || _tables.Value || _beds.Value) && IsFurniture(root, name)) return Furniture;
            return None;
        }

        // A seat (any piece with a chair in it), a bed (any piece with a bed in it) or a table (by its name), but nothing
        // that moves (ships, carts), crafts (the workbench, the Galdr table, the food preparation table), is a map (the
        // cartography table), burns (the hot tub) or has snap points of its own.
        private static bool IsFurniture(Transform root, string name)
        {
            if (Moves(root)) return false;
            if (root.GetComponentInChildren<CraftingStation>() != null || root.GetComponentInChildren<StationExtension>() != null ||
                root.GetComponentInChildren<MapTable>() != null || root.GetComponentInChildren<Fireplace>() != null) return false;
            if (OwnPoints(root)) return false;
            if (_seats.Value && root.GetComponentInChildren<Chair>() != null) return true;
            if (_beds.Value && root.GetComponentInChildren<Bed>() != null) return true;
            if (!_tables.Value) return false;
            // A word of the name starts with "table" (piece_table_round, piece_blackmarble_table; not a stable).
            foreach (var part in name.Split('_', ' '))
                if (part.StartsWith("table", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Where a chest stands: its bottom middle point (or its own position).
        private static float BaseY(Transform root)
        {
            if (root == null) return float.MaxValue;
            var bottom = root.Find(Marks[Chest] + CentreName(Chest));
            return bottom != null ? bottom.position.y : root.position.y;
        }

        // Ships, carts, the battering ram: anything with a body that moves.
        private static bool Moves(Transform root) =>
            root.GetComponent<Rigidbody>() != null || root.GetComponentInChildren<Ship>() != null || root.GetComponentInChildren<Vagon>() != null;

        // The piece has snap points of its own (not ours).
        private static bool OwnPoints(Transform root)
        {
            for (var i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
                if (child.CompareTag("snappoint") && !Ours(child.name)) return true;
            }
            return false;
        }

        // Kept per piece: this runs for every point near the piece held, every frame.
        private static int Kind(Transform root)
        {
            if (root == null) return None;
            var id = root.GetInstanceID();
            if (Kinds.TryGetValue(id, out var kind)) return kind;
            if (Kinds.Count > 100000) Kinds.Clear();
            return Kinds[id] = KindOf(root);
        }

        // Every time the game reads a piece's snap points (the ghost's and those of the pieces near it): a chest, rug or
        // fermenter gets its points the first time.
        [HarmonyPatch(typeof(Piece), nameof(Piece.GetSnapPoints), typeof(List<Transform>))]
        [HarmonyPrefix]
        private static void BeforeSnapPoints(Piece __instance)
        {
            if (__instance == null) return;
            if (Seen.Count > 200000) Seen.Clear(); // a piece is never given its points twice anyway
            if (!Seen.Add(__instance.GetInstanceID())) return;
            if (!__instance.gameObject.scene.IsValid()) return; // not the prefabs themselves
            try
            {
                if (_bigFloors.Value && BigFloors.Contains(Utils.GetPrefabName(__instance.gameObject)))
                {
                    AddFloorPoints(__instance.transform);
                    return;
                }
                var kind = Kind(__instance.transform);
                if (kind != None) AddPoints(__instance.transform, kind);
            }
            catch (Exception e) { BepInEx.Logging.Logger.CreateLogSource(Name).LogWarning($"No snap points for {__instance.name}: {e.Message}"); }
        }

        // A 4 x 4 floor: to its corners (on top and below) come the middle of each edge and the middle, at the same heights.
        private static void AddFloorPoints(Transform root)
        {
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            float top = float.MinValue, bottom = float.MaxValue;
            for (var i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
                if (!child.CompareTag("snappoint")) continue;
                if (Ours(child.name)) return; // has them already
                var p = child.localPosition;
                minX = Mathf.Min(minX, p.x);
                maxX = Mathf.Max(maxX, p.x);
                minZ = Mathf.Min(minZ, p.z);
                maxZ = Mathf.Max(maxZ, p.z);
                top = Mathf.Max(top, p.y);
                bottom = Mathf.Min(bottom, p.y);
            }
            if (maxX - minX < 2f * MinTile || maxZ - minZ < 2f * MinTile) return;
            float midX = (minX + maxX) * 0.5f, midZ = (minZ + maxZ) * 0.5f;
            foreach (var y in bottom < top - 0.05f ? new[] { top, bottom } : new[] { top })
            {
                var side = y == top && bottom < top - 0.05f ? " (top)" : y == top ? "" : " (bottom)";
                Add(root, FloorMark + "centre" + side, new Vector3(midX, y, midZ));
                Add(root, FloorMark + "back edge" + side, new Vector3(midX, y, minZ));
                Add(root, FloorMark + "front edge" + side, new Vector3(midX, y, maxZ));
                Add(root, FloorMark + "left edge" + side, new Vector3(minX, y, midZ));
                Add(root, FloorMark + "right edge" + side, new Vector3(maxX, y, midZ));
            }
        }

        private static void AddPoints(Transform root, int kind)
        {
            var prefab = Utils.GetPrefabName(root.gameObject);
            var tub = kind == Furniture && prefab == HotTub;
            var keepOwn = tub || (kind == Machine && prefab == FrostFoundry) || kind == Portal; // ours beside the piece's own points
            for (var i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
                if (child.CompareTag("snappoint") && (!keepOwn || Ours(child.name))) return; // has its own (or ours already)
            }
            Bounds box;
            if (!(kind == Rug ? ClothBox(root, out box) : kind == Furniture || kind == Light || kind == Stand ? ModelBox(root, out box) || LocalBox(root, out box) : LocalBox(root, out box))) return;
            Vector3 lo = box.min, hi = box.max;
            float midX = (lo.x + hi.x) * 0.5f, midZ = (lo.z + hi.z) * 0.5f;
            var mark = Marks[kind];
            // A hanging piece hangs from the top of its colliders (its chain: where the game puts it against a ceiling); a
            // sconce is held by its back plate (its back is its -x side: it leans out along +x).
            if (kind == Hanging)
            {
                Add(root, mark + ChainTop, new Vector3(midX, hi.y, midZ));
                return;
            }
            if (kind == Sconce)
            {
                Add(root, mark + BackPlate, new Vector3(lo.x, (lo.y + hi.y) * 0.5f, midZ));
                return;
            }
            // A wall item stand: its thinner level axis points out from the wall, to the side its item hangs on (the attach
            // point); its back plate is the middle of the other side.
            if (kind == Stand)
            {
                var thin = box.size.x <= box.size.z ? 0 : 2;
                var attach = root.GetComponentInChildren<ItemStand>()?.m_attachOther;
                var sign = attach == null || root.InverseTransformPoint(attach.position)[thin] >= box.center[thin] ? 1f : -1f;
                var front = Vector3.zero;
                front[thin] = sign;
                StandShapes[prefab] = new StandShape { Front = front, Wide = thin == 0 ? 2 : 0, Box = box };
                var back = box.center;
                back[thin] -= sign * box.extents[thin];
                Add(root, mark + BackPlate, back);
                return;
            }
            // The names the game shows when Q / E picks a point: plain words. Front is the piece's forward (+z), which faces
            // you when you place it unturned. A chest has points on its top too, so its names say which.
            // The hot tub: only the middle of its round tub (its own eight points stay for Q / E).
            if (tub)
            {
                Add(root, mark + CentreName(kind), new Vector3(0f, lo.y, 0f));
                return;
            }
            var bottom = kind == Chest ? " (bottom)" : "";
            Add(root, mark + CentreName(kind), new Vector3(midX, lo.y, midZ));
            Add(root, mark + "back edge", new Vector3(midX, lo.y, lo.z));
            Add(root, mark + "front edge", new Vector3(midX, lo.y, hi.z));
            Add(root, mark + "left edge", new Vector3(lo.x, lo.y, midZ));
            Add(root, mark + "right edge", new Vector3(hi.x, lo.y, midZ));
            if (kind == Chest) Add(root, mark + "centre (top)", new Vector3(midX, hi.y, midZ));
            foreach (var y in kind == Chest ? new[] { lo.y, hi.y } : new[] { lo.y })
                foreach (var z in new[] { hi.z, lo.z })
                    foreach (var x in new[] { lo.x, hi.x })
                        Add(root, $"{mark}{(z == hi.z ? "front" : "back")} {(x == lo.x ? "left" : "right")} corner{(kind != Chest ? "" : y == lo.y ? bottom : " (top)")}",
                            new Vector3(x, y, z));
        }

        // The point a hanging piece hangs from, and the one a sconce is held by.
        private const string ChainTop = "chain top", BackPlate = "back plate";

        // The point a piece stands on (the middle of its base), the one the floor zones place.
        private static string CentreName(int kind) => kind == Chest ? "centre (bottom)" : "centre";

        private static void Add(Transform root, string name, Vector3 local)
        {
            var point = new GameObject(name);
            point.tag = "snappoint";
            point.SetActive(false); // a marker only, like the game's own
            point.transform.SetParent(root, false);
            point.transform.localPosition = local;
        }

        // Chests whose collider does not match what you see, measured from their model's vertices: the Grausten chest's
        // stone base is 1.696 m wide and 1.182 m deep (its collider only 1.31 by 1.13) and its closed lid rises to 1.171 m
        // (the collider to 0.72), so it snapped 20 cm into its neighbour. Its base is what touches the next chest (the
        // skulls on its sides reach 1 cm further, which left a small gap between the bases). The wardrobe likewise: its
        // model is 1.676 m wide, 1.106 m deep and 2.678 m high (widest at its base), its collider only 1.35 x 0.85 x 2.10,
        // so it stuck out 16 cm past a tile edge.
        private static readonly Dictionary<string, Bounds> Measured = new Dictionary<string, Bounds>
        {
            { "piece_chest_grausten", new Bounds(new Vector3(0f, 0.5855f, 0f), new Vector3(1.696f, 1.171f, 1.182f)) },
            { "piece_chest_warderobe", new Bounds(new Vector3(0.001f, 1.339f, -0.003f), new Vector3(1.676f, 2.678f, 1.106f)) },
            // The lava lantern is drawn by an animated (skinned) mesh that ModelBox cannot read: its model, 38 x 38 cm, 82 cm high.
            { "piece_Lavalantern", new Bounds(new Vector3(0f, 0.405f, 0f), new Vector3(0.384f, 0.82f, 0.38f)) },
            // The dragon bed's model is wider on one side (the blanket hangs over it, -1.47 m against +1.32 m): the frame,
            // the same on both sides, so the bed itself is what centres and stands flush.
            { "piece_bed02", new Bounds(new Vector3(0f, 0.8655f, 0.114f), new Vector3(2.646f, 1.749f, 3.488f)) },
            // The windmill stands on its stone base, 3.7 m square (its blade arms reach out 3.5 m above it).
            { "windmill", new Bounds(new Vector3(0f, 2.5f, 0f), new Vector3(3.7f, 5f, 3.7f)) },
            // The Frost Foundry: its own four corner points (3.5 x 5.5 m, its base 25 cm below its middle).
            { FrostFoundry, new Bounds(new Vector3(0f, 0.425f, 0f), new Vector3(3.5f, 1.35f, 5.5f)) },
            // The hot tub: its round tub (3.29 m across, centred on the piece), not the stove sticking out at the back.
            { HotTub, new Bounds(new Vector3(0f, 1.12f, 0f), new Vector3(3.294f, 2.24f, 3.294f)) },
        };

        // The piece's box in its own space, from its solid colliders (the lid's and body's), or as measured.
        private static bool LocalBox(Transform root, out Bounds box)
        {
            if (Measured.TryGetValue(Utils.GetPrefabName(root.gameObject), out box)) return true;
            box = default;
            var found = false;
            var corners = new Vector3[8];
            foreach (var collider in root.GetComponentsInChildren<Collider>(false))
            {
                if (collider.isTrigger || !collider.enabled) continue;
                Vector3 center, size;
                if (collider is BoxCollider b) { center = b.center; size = b.size; }
                else if (collider is MeshCollider m && m.sharedMesh != null) { center = m.sharedMesh.bounds.center; size = m.sharedMesh.bounds.size; }
                else if (collider is CapsuleCollider c)
                {
                    center = c.center;
                    size = new Vector3(c.radius * 2f, c.radius * 2f, c.radius * 2f);
                    size[c.direction] = Mathf.Max(c.height, c.radius * 2f);
                }
                else if (collider is SphereCollider s) { center = s.center; size = Vector3.one * s.radius * 2f; }
                else continue;
                var n = 0;
                for (var sx = -1; sx <= 1; sx += 2)
                    for (var sy = -1; sy <= 1; sy += 2)
                        for (var sz = -1; sz <= 1; sz += 2)
                            corners[n++] = center + Vector3.Scale(size * 0.5f, new Vector3(sx, sy, sz));
                foreach (var corner in corners)
                {
                    var local = root.InverseTransformPoint(collider.transform.TransformPoint(corner));
                    if (!found) { box = new Bounds(local, Vector3.zero); found = true; }
                    else box.Encapsulate(local);
                }
            }
            return found && box.size.x > 0.05f && box.size.z > 0.05f;
        }

        // Furniture's box from its visible model (what touches the next chair or table), in the piece's own space: every
        // mesh shown now, without the low-detail copies of a level-of-detail group.
        private static bool ModelBox(Transform root, out Bounds box)
        {
            if (Measured.TryGetValue(Utils.GetPrefabName(root.gameObject), out box)) return true;
            box = default;
            var found = false;
            var lowDetail = new HashSet<Renderer>();
            foreach (var group in root.GetComponentsInChildren<LODGroup>(false))
            {
                var lods = group.GetLODs();
                for (var l = 1; l < lods.Length; l++)
                    foreach (var r in lods[l].renderers)
                        if (r != null) lowDetail.Add(r);
            }
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(false))
            {
                var mesh = filter.sharedMesh;
                var renderer = filter.GetComponent<MeshRenderer>();
                if (mesh == null || renderer == null || !renderer.enabled || lowDetail.Contains(renderer)) continue;
                var b = mesh.bounds;
                for (var sx = -1; sx <= 1; sx += 2)
                    for (var sy = -1; sy <= 1; sy += 2)
                        for (var sz = -1; sz <= 1; sz += 2)
                        {
                            var local = root.InverseTransformPoint(filter.transform.TransformPoint(b.center + Vector3.Scale(b.extents, new Vector3(sx, sy, sz))));
                            if (!found) { box = new Bounds(local, Vector3.zero); found = true; }
                            else box.Encapsulate(local);
                        }
            }
            return found && box.size.x > 0.05f && box.size.z > 0.05f;
        }

        // Nordic Rugs: the woven size of every rug (width x length, without the tassels), from its catalog.
        private static Dictionary<string, Vector2> _woven;

        private static bool WovenSize(string prefab, out Vector2 size)
        {
            if (_woven == null)
            {
                _woven = new Dictionary<string, Vector2>();
                try
                {
                    var type = AccessTools.TypeByName("NordicRugs.RugCatalog");
                    if (type != null && AccessTools.Field(type, "All")?.GetValue(null) is IEnumerable all)
                        foreach (var rug in all)
                        {
                            var t = Traverse.Create(rug);
                            _woven[t.Property("PrefabName").GetValue<string>()] =
                                new Vector2(t.Property("Width").GetValue<float>(), t.Property("Length").GetValue<float>());
                        }
                }
                catch (Exception e)
                {
                    BepInEx.Logging.Logger.CreateLogSource(Name).LogWarning("Nordic Rugs sizes not read: " + e.Message);
                }
            }
            return _woven.TryGetValue(BaseRug(prefab), out size);
        }

        // Rug Lift's large versions (name_x2, name_x3) are the same rug scaled: their own scale does the rest.
        private static string BaseRug(string prefab)
        {
            var cut = prefab.LastIndexOf("_x", StringComparison.Ordinal);
            return cut > 0 && int.TryParse(prefab.Substring(cut + 2), out _) ? prefab.Substring(0, cut) : prefab;
        }

        // The woven part of a Nordic rug's picture (u, v from 0 to 1): the columns and rows that are at least 80% solid
        // (the border around it is see-through, the tassels too thin to count). Read once per rug from the picture in
        // Nordic Rugs' own file, since the game's copy cannot be read back.
        private static readonly Dictionary<string, Rect> WovenAreas = new Dictionary<string, Rect>();

        private static Rect WovenArea(string id)
        {
            if (WovenAreas.TryGetValue(id, out var area)) return area;
            area = new Rect(0f, 0f, 1f, 1f);
            Texture2D picture = null;
            try
            {
                var assembly = AccessTools.TypeByName("NordicRugs.RugCatalog")?.Assembly;
                using (var stream = assembly?.GetManifestResourceStream("NordicRugs.Textures." + id + ".png"))
                {
                    if (stream != null)
                    {
                        var bytes = new byte[stream.Length];
                        var read = 0;
                        while (read < bytes.Length)
                        {
                            var n = stream.Read(bytes, read, bytes.Length - read);
                            if (n <= 0) break;
                            read += n;
                        }
                        picture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        if (DigiShared.IconSheet.LoadPng(picture, bytes))
                        {
                            int w = picture.width, h = picture.height;
                            var pixels = picture.GetPixels32();
                            var columns = new int[w];
                            var rows = new int[h];
                            for (var y = 0; y < h; y++)
                                for (var x = 0; x < w; x++)
                                    if (pixels[y * w + x].a > 127)
                                    {
                                        columns[x]++;
                                        rows[y]++;
                                    }
                            int u0 = 0, u1 = w - 1, v0 = 0, v1 = h - 1;
                            while (u0 < w - 1 && columns[u0] < 0.8f * h) u0++;
                            while (u1 > u0 && columns[u1] < 0.8f * h) u1--;
                            while (v0 < h - 1 && rows[v0] < 0.8f * w) v0++; // the picture's first row is v = 0
                            while (v1 > v0 && rows[v1] < 0.8f * w) v1--;
                            if (u1 > u0 && v1 > v0)
                                area = Rect.MinMaxRect((float)u0 / w, (float)v0 / h, (float)(u1 + 1) / w, (float)(v1 + 1) / h);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                BepInEx.Logging.Logger.CreateLogSource(Name).LogWarning($"Woven area of {id} not read: {e.Message}");
            }
            finally
            {
                if (picture != null) UnityEngine.Object.Destroy(picture);
            }
            WovenAreas[id] = area;
            return area;
        }

        // A rug's box: a Nordic rug's woven size, centred on its middle; another rug's visible cloth (the meshes shown now,
        // not the low-detail copies). Lying on the placement point.
        private static bool ClothBox(Transform root, out Bounds box)
        {
            var prefab = Utils.GetPrefabName(root.gameObject);
            if (WovenSize(prefab, out var woven))
            {
                // The mesh spans the catalog size with the picture stretched over it (u across, v along); the woven part of
                // the picture is a little smaller.
                var area = WovenArea(BaseRug(prefab).Substring("nr_rug_".Length));
                box = new Bounds(new Vector3((area.center.x - 0.5f) * woven.x, 0f, (area.center.y - 0.5f) * woven.y),
                    new Vector3(area.width * woven.x, 0f, area.height * woven.y));
                return box.size.x > 0.05f && box.size.z > 0.05f;
            }
            box = default;
            var found = false;
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(false))
            {
                var mesh = filter.sharedMesh;
                if (mesh == null || filter.name.IndexOf("LOD", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                var renderer = filter.GetComponent<Renderer>();
                if (renderer == null || !renderer.enabled) continue;
                var b = mesh.bounds;
                for (var sx = -1; sx <= 1; sx += 2)
                    for (var sz = -1; sz <= 1; sz += 2)
                    {
                        var local = root.InverseTransformPoint(filter.transform.TransformPoint(b.center + Vector3.Scale(b.extents, new Vector3(sx, 0f, sz))));
                        local.y = 0f; // a rug lies on the point it is placed on (Rug Lift only raises its picture a little)
                        if (!found) { box = new Bounds(local, Vector3.zero); found = true; }
                        else box.Encapsulate(local);
                    }
            }
            if (!found) return LocalBox(root, out box);
            return box.size.x > 0.05f && box.size.z > 0.05f;
        }

        // The game snaps to the nearest pair of points within 0.5 m (a point of the piece held and one of a piece near it).
        // This picks again where needed: a chest, rug or fermenter snaps onto its own kind and onto floor tiles (unless it
        // may snap to anything), and anything else never onto them.
        [HarmonyPatch(typeof(Player), "FindClosestSnapPoints")]
        [HarmonyPostfix]
        private static void ChooseSnap(Player __instance, Transform ghost, float maxSnapDistance, ref Transform a, ref Transform b, ref bool __result)
        {
            if (ghost == null) return;
            var kind = Kind(ghost);
            if (kind == None)
            {
                if (!__result || _otherPieces.Value || b == null || Kind(b.parent) == None) return;
                Pick(__instance, maxSnapDistance, None, ref a, ref b, ref __result);
                return;
            }
            // A standing light aimed at a beam: on top of it.
            if (kind == Light && ManualPoint(__instance) < 0 && (OnBeams.Contains(Utils.GetPrefabName(ghost.gameObject)) || ghost.GetComponentInChildren<ItemStand>() != null) &&
                OnTop(ghost, GhostPoints(__instance), out var foot, out var onTop))
            {
                Target(onTop);
                a = foot;
                b = _tile;
                __result = true;
                return;
            }
            // A sconce: onto the standing beam aimed at, else as the game has it (no snapping, unless it may snap to anything).
            // A wall item stand: onto the wall aimed at, else onto a standing beam, else the same.
            if (kind == Sconce || kind == Stand)
            {
                Transform mount = null;
                var at = Vector3.zero;
                if ((kind == Stand && OnWall(ghost, GhostPoints(__instance), out mount, out at)) || OnBeam(ghost, GhostPoints(__instance), kind, out mount, out at))
                {
                    Target(at);
                    a = mount;
                    b = _tile;
                    __result = true;
                }
                else if (!_otherPieces.Value)
                {
                    a = null;
                    b = null;
                    __result = false;
                }
                return;
            }
            if (_otherPieces.Value && !_floorTiles.Value) return; // the game's own choice, onto anything
            Pick(__instance, maxSnapDistance, kind, ref a, ref b, ref __result);
            // A chest onto another chest's lid that pokes up through a floor (a shelf over the lower row, the chest taller
            // than the walls holding the shelf): still right above that chest, but standing on the floor, where the lower
            // chest stands below the floor.
            if (__result && kind == Chest && a != null && b != null && b.name.EndsWith("(top)", StringComparison.Ordinal) && _floorTiles.Value &&
                ManualPoint(__instance) < 0 && Zone(ghost, GhostPoints(__instance), kind, out _, out var shelfTo) &&
                BaseY(b.parent) < shelfTo.y - 0.1f)
            {
                Target(new Vector3(b.position.x, shelfTo.y, b.position.z));
                b = _tile;
                return;
            }
            // Not next to its own kind: onto the tile under the crosshair (a hanging piece: above it), by the zone aimed at.
            if (_floorTiles.Value && ManualPoint(__instance) < 0 && !__result &&
                Zone(ghost, GhostPoints(__instance), kind, out var from, out var to))
            {
                Target(to);
                a = from;
                b = _tile;
                __result = true;
            }
        }

        // The tile under the piece's bottom middle (which follows the crosshair), split into 3 x 3 zones: in each direction
        // the piece's low edge goes onto the tile's low edge, its high edge onto the tile's high edge, or it is centred.
        // Only for a piece turned along the floor (or a quarter turn from it). A hanging piece: the tile above its top
        // middle (its chain), which goes onto the tile's underside at the corner, edge middle or middle of the zone aimed at.
        private static bool Zone(Transform ghost, List<Transform> points, int kind, out Transform from, out Vector3 to)
        {
            from = null;
            to = Vector3.zero;
            var hanging = kind == Hanging;
            // The hot tub: its middle goes onto the tile's corner (where four floors meet), edge middle (the middle of the
            // seam between two floor pieces) or middle, like a hanging brazier's chain; it is round, so at any turn (turned
            // along the floor, its stove then lies on a seam).
            var tub = !hanging && Utils.GetPrefabName(ghost.gameObject) == HotTub;
            var onPoint = hanging || tub;
            var want = Marks[kind] + (hanging ? ChainTop : CentreName(kind));
            foreach (var point in points)
            {
                if (point == null) continue;
                if (point.name == want)
                {
                    from = point;
                    break;
                }
            }
            if (from == null) return false;
            // The piece's half size, from its own corner points (a hanging piece goes onto the points themselves).
            float halfX = 0f, halfZ = 0f;
            var middle = from.localPosition;
            foreach (var point in points)
            {
                if (point == null || onPoint || !point.name.StartsWith(Marks[kind], StringComparison.Ordinal)) continue; // ours only
                halfX = Mathf.Max(halfX, Mathf.Abs(point.localPosition.x - middle.x) * ghost.lossyScale.x);
                halfZ = Mathf.Max(halfZ, Mathf.Abs(point.localPosition.z - middle.z) * ghost.lossyScale.z);
            }
            var aim = from.position;
            // The tile it is over: the one whose square holds the aimed point (at about its surface height). A big floor can
            // hold several tiles (nine targets each).
            Transform floor = null;
            Vector3[] tiles = null;
            Vector3 local = default;
            var c = 0;
            var surface = 0f;
            foreach (var root in Roots)
            {
                var targets = TileTargets(root);
                var p = root.InverseTransformPoint(aim);
                for (var t = 0; t + 9 <= targets.Length; t += 9)
                {
                    surface = hanging ? Under(root) : targets[t + 4].y;
                    if (Mathf.Abs(p.y - surface) > 0.35f) continue;
                    if (p.x < targets[t + 5].x - 0.01f || p.x > targets[t + 8].x + 0.01f || p.z < targets[t + 5].z - 0.01f || p.z > targets[t + 8].z + 0.01f) continue;
                    floor = root;
                    tiles = targets;
                    local = p;
                    c = t;
                    break;
                }
                if (floor != null) break;
            }
            if (floor == null) return false;
            // Turned along the floor: within 3 degrees of a quarter turn (a hanging piece hangs the same at any turn, and a
            // round one, the hot tub, covers the same circle: its middle snaps at any turn).
            var yaw = (Quaternion.Inverse(floor.rotation) * ghost.rotation).eulerAngles.y;
            var quarter = Mathf.Round(yaw / 90f);
            if (!onPoint && Mathf.Abs(yaw - quarter * 90f) > 3f) return false;
            if (((int)quarter & 1) == 1)
            {
                var swap = halfX;
                halfX = halfZ;
                halfZ = swap;
            }
            // A tile's corners are its targets 5..8 (minX/minZ, minX/maxZ, maxX/minZ, maxX/maxZ).
            float minX = tiles[c + 5].x, maxX = tiles[c + 8].x, minZ = tiles[c + 5].z, maxZ = tiles[c + 8].z;
            // The piece reaches 2 cm past the tile's edge: the floor's planks stick out about a centimetre past its snap
            // points (neighbouring tiles overlap), which would show as a thin line along the rug or the chest's base.
            var over = onPoint ? 0f : 0.02f;
            var x = Aligned(local.x, minX, maxX, halfX, over);
            var z = Aligned(local.z, minZ, maxZ, halfZ, over);
            to = floor.TransformPoint(new Vector3(x, surface, z));
            return true;
        }

        // A floor's underside in its own space (the bottom of its colliders), from TileTargets.
        private static float Under(Transform root)
        {
            TileTargets(root);
            return Unders.TryGetValue(root.GetInstanceID(), out var under) ? under : 0f;
        }

        // Before the snap: what the placement ray hit (the piece and the spot), for a sconce.
        [HarmonyPatch(typeof(Player), "PieceRayTest")]
        [HarmonyPostfix]
        private static void RayHit(bool __result, ref Vector3 point, ref Piece piece)
        {
            _hitPiece = __result ? piece : null;
            _hitPoint = point;
            _hitFrame = Time.frameCount;
        }

        // A sconce aimed at a standing beam (a pole, an upright beam, a pillar): its back plate flush against the side of the
        // beam aimed at, centred on it, facing straight out from it; its height in 25 cm steps from the beam's foot.
        private static bool OnBeam(Transform ghost, List<Transform> points, int kind, out Transform mount, out Vector3 at)
        {
            mount = null;
            at = Vector3.zero;
            if (_hitFrame != Time.frameCount || _hitPiece == null) return false;
            var face = Quaternion.identity;
            if (kind == Stand && !Facing(ghost, out face)) return false;
            foreach (var point in points)
                if (point != null && point.name == Marks[kind] + BackPlate)
                {
                    mount = point;
                    break;
                }
            if (mount == null) return false;
            var root = _hitPiece.transform;
            var beam = Bar(root);
            if (beam == null || !beam.Standing) return false;
            var box = beam.Box;
            var p = root.InverseTransformPoint(_hitPoint);
            // The side aimed at: of the two level axes, the one the hit is furthest out along (for its half thickness).
            int i = (beam.Axis + 1) % 3, j = (beam.Axis + 2) % 3;
            var di = (p[i] - box.center[i]) / Mathf.Max(box.extents[i], 0.01f);
            var dj = (p[j] - box.center[j]) / Mathf.Max(box.extents[j], 0.01f);
            var k = Mathf.Abs(di) >= Mathf.Abs(dj) ? i : j;
            var sign = (k == i ? di : dj) >= 0f ? 1f : -1f;
            var outward = Vector3.zero;
            outward[k] = sign;
            var normal = root.TransformDirection(outward);
            normal.y = 0f;
            if (normal.sqrMagnitude < 0.01f) return false;
            normal.Normalize();
            var side = box.center;
            side[k] += sign * box.extents[k];
            var onFace = root.TransformPoint(side);
            // The height: in steps from the beam's foot, within the beam.
            float bottom = float.MaxValue, top = float.MinValue;
            for (var sx = -1; sx <= 1; sx += 2)
                for (var sy = -1; sy <= 1; sy += 2)
                    for (var sz = -1; sz <= 1; sz += 2)
                    {
                        var y = root.TransformPoint(box.center + Vector3.Scale(box.extents, new Vector3(sx, sy, sz))).y;
                        bottom = Mathf.Min(bottom, y);
                        top = Mathf.Max(top, y);
                    }
            var height = bottom + Mathf.Round((_hitPoint.y - bottom) / SconceStep) * SconceStep;
            height = Mathf.Clamp(height, bottom, top);
            // Facing out: the sconce leans out along its own +x, an item stand along its Front.
            ghost.rotation = Quaternion.LookRotation(normal) * (kind == Stand ? face : Quaternion.Euler(0f, -90f, 0f));
            at = new Vector3(onFace.x, height, onFace.z);
            return true;
        }

        // The turn that brings a wall item stand's Front onto +z (put after a turn facing out from the wall).
        private static bool Facing(Transform ghost, out Quaternion turn)
        {
            turn = Quaternion.identity;
            if (!StandShapes.TryGetValue(Utils.GetPrefabName(ghost.gameObject), out var shape)) return false;
            turn = Quaternion.Inverse(Quaternion.LookRotation(shape.Front));
            return true;
        }

        // A wall item stand aimed at a wall: its back plate flush on the face aimed at, facing straight out, placed by the
        // 3 x 3 zones of the wall tile aimed at (like a chest on a floor tile, Aligned: across the wall and up and down).
        private static bool OnWall(Transform ghost, List<Transform> points, out Transform back, out Vector3 at)
        {
            back = null;
            at = Vector3.zero;
            if (_hitFrame != Time.frameCount || _hitPiece == null) return false;
            foreach (var point in points)
                if (point != null && point.name == Marks[Stand] + BackPlate)
                {
                    back = point;
                    break;
                }
            if (back == null || !StandShapes.TryGetValue(Utils.GetPrefabName(ghost.gameObject), out var shape)) return false;
            var root = _hitPiece.transform;
            var wall = WallOf(root);
            if (wall == null) return false;
            var p = root.InverseTransformPoint(_hitPoint);
            int n = wall.Normal, u = wall.Across, v = wall.Up;
            var sign = p[n] >= wall.Box.center[n] ? 1f : -1f;
            var outward = Vector3.zero;
            outward[n] = sign;
            var normal = root.TransformDirection(outward);
            normal.y = 0f;
            if (normal.sqrMagnitude < 0.01f) return false;
            normal.Normalize();
            if (!Span(wall.LinesAcross, p[u], out var lowU, out var highU) || !Span(wall.LinesUp, p[v], out var lowV, out var highV)) return false;
            ghost.rotation = Quaternion.LookRotation(normal) * Quaternion.Inverse(Quaternion.LookRotation(shape.Front));
            // The stand's half width and height, in the wall's own units.
            var scale = ghost.lossyScale;
            var wallScale = root.lossyScale;
            var halfW = shape.Box.extents[shape.Wide] * Mathf.Abs(scale[shape.Wide]) / Mathf.Max(0.01f, Mathf.Abs(wallScale[u]));
            var halfH = shape.Box.extents.y * Mathf.Abs(scale.y) / Mathf.Max(0.01f, Mathf.Abs(wallScale[v]));
            var local = wall.Box.center;
            local[n] += sign * wall.Box.extents[n];
            local[u] = Aligned(p[u], lowU, highU, halfW, 0f);
            local[v] = Aligned(p[v], lowV, highV, halfH, 0f);
            at = root.TransformPoint(local);
            return true;
        }

        // The tile (between two lines) a value lies in; beyond the outer lines the outer tile.
        private static bool Span(List<float> lines, float value, out float low, out float high)
        {
            low = high = 0f;
            if (lines.Count < 2) return false;
            var i = 0;
            while (i + 2 < lines.Count && value > lines[i + 1]) i++;
            low = lines[i];
            high = lines[i + 1];
            return high - low >= 0.9f;
        }

        // A wall: upright (one of its own axes within 11 degrees of straight up), at least 0.9 m high and wide, thinner than
        // that (at most 1.05 m), with snap points spanning 0.9 m or more both ways. Not a wall item stand's own kind (Kind None).
        private static Wall WallOf(Transform root)
        {
            var id = root.GetInstanceID();
            if (Walls.TryGetValue(id, out var wall)) return wall;
            if (Walls.Count > 50000) Walls.Clear();
            wall = null;
            if (Kind(root) == None && ModelBox(root, out var box))
            {
                var up = -1;
                for (var k = 0; k < 3; k++)
                    if (Mathf.Abs(Direction(root, k).y) >= 0.98f) up = k;
                if (up >= 0)
                {
                    var scale = root.lossyScale;
                    var size = new Vector3(box.size.x * Mathf.Abs(scale.x), box.size.y * Mathf.Abs(scale.y), box.size.z * Mathf.Abs(scale.z));
                    int i = (up + 1) % 3, j = (up + 2) % 3;
                    var n = size[i] <= size[j] ? i : j;
                    var u = n == i ? j : i;
                    if (size[up] >= 0.9f && size[u] >= 0.9f && size[n] <= 1.05f && size[n] < 0.8f * Mathf.Min(size[up], size[u]))
                    {
                        var across = new List<float>();
                        var high = new List<float>();
                        for (var c = 0; c < root.childCount; c++)
                        {
                            var child = root.GetChild(c);
                            if (!child.CompareTag("snappoint")) continue;
                            across.Add(child.localPosition[u]);
                            high.Add(child.localPosition[up]);
                        }
                        if (across.Count >= 2)
                        {
                            var linesU = Lines(across);
                            var linesV = Lines(high);
                            if (linesU[linesU.Count - 1] - linesU[0] >= 0.9f && linesV[linesV.Count - 1] - linesV[0] >= 0.9f)
                                wall = new Wall { Box = box, Normal = n, Across = u, Up = up, LinesAcross = linesU, LinesUp = linesV };
                        }
                    }
                }
            }
            Walls[id] = wall;
            return wall;
        }

        // A beam: at least 0.9 m long, at most 1.05 m thick each way and at least 1.5 times as long as thick, with snap
        // points at both ends, set straight (one of its own axes within 11 degrees of straight up): standing (poles, beams
        // stood up, pillars, narrow wall pieces) or lying level (beams, half walls). Not banners or torches (no snap
        // points), nor slanted beams. Its box is its visible model.
        private static Beam Bar(Transform root)
        {
            var id = root.GetInstanceID();
            if (Beams.TryGetValue(id, out var beam)) return beam;
            if (Beams.Count > 50000) Beams.Clear();
            beam = null;
            if (Kind(root) == None && ModelBox(root, out var box))
            {
                var scale = root.lossyScale;
                var size = new Vector3(box.size.x * Mathf.Abs(scale.x), box.size.y * Mathf.Abs(scale.y), box.size.z * Mathf.Abs(scale.z));
                var axis = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
                float length = size[axis], a = size[(axis + 1) % 3], b = size[(axis + 2) % 3];
                var up = -1;
                for (var k = 0; k < 3; k++)
                    if (Mathf.Abs(Direction(root, k).y) >= 0.98f) up = k;
                bool high = false, low = false;
                for (var c = 0; c < root.childCount; c++)
                {
                    var child = root.GetChild(c);
                    if (!child.CompareTag("snappoint")) continue;
                    var along = child.localPosition[axis] - box.center[axis];
                    if (along > box.extents[axis] * 0.6f) high = true;
                    if (along < -box.extents[axis] * 0.6f) low = true;
                }
                if (up >= 0 && length >= 0.9f && a <= 1.05f && b <= 1.05f && length >= 1.5f * Mathf.Max(a, b) && high && low)
                    beam = new Beam { Box = box, Axis = axis, Up = up };
            }
            Beams[id] = beam;
            return beam;
        }

        private static Vector3 Direction(Transform root, int axis) => axis == 0 ? root.right : axis == 1 ? root.up : root.forward;

        // A standing light aimed at a beam: on top of it. A standing beam: centred on its top (aimed at its upper half
        // metre); a lying beam: centred across its top, along it by the third aimed at (flush with an end, or in the middle).
        private static bool OnTop(Transform ghost, List<Transform> points, out Transform from, out Vector3 to)
        {
            from = null;
            to = Vector3.zero;
            if (_hitFrame != Time.frameCount || _hitPiece == null) return false;
            foreach (var point in points)
                if (point != null && point.name == Marks[Light] + CentreName(Light))
                {
                    from = point;
                    break;
                }
            if (from == null) return false;
            var root = _hitPiece.transform;
            var bar = Bar(root);
            if (bar == null) return false;
            var box = bar.Box;
            var up = bar.Up;
            // its top: the end of its upright axis that is higher in the world
            var sign = Direction(root, up).y >= 0f ? 1f : -1f;
            var face = box.center;
            face[up] += sign * box.extents[up];
            if (bar.Standing)
            {
                to = root.TransformPoint(face);
                return _hitPoint.y >= to.y - 0.5f;
            }
            // the light's own half size, from its points
            var half = 0f;
            foreach (var point in points)
            {
                if (point == null) continue;
                var d = point.localPosition - from.localPosition;
                half = Mathf.Max(half, Mathf.Max(Mathf.Abs(d.x) * ghost.lossyScale.x, Mathf.Abs(d.z) * ghost.lossyScale.z));
            }
            half /= Mathf.Max(0.01f, Mathf.Abs(root.lossyScale[bar.Axis]));
            var p = root.InverseTransformPoint(_hitPoint);
            var along = bar.Axis;
            face[along] = Aligned(p[along], box.center[along] - box.extents[along], box.center[along] + box.extents[along], half, 0f);
            to = root.TransformPoint(face);
            return true;
        }

        // In one direction: aimed in the low third of the tile -> the piece's low edge on the tile's low edge; high third ->
        // high edge on high edge; else centred. A piece bigger than the tile: its middle on the low or high edge, or centred.
        private static float Aligned(float aimed, float low, float high, float half, float over)
        {
            var size = high - low;
            // Bigger than the tile (a large rug over several floor pieces): its middle goes onto the tile's edge aimed near
            // (the seam between two floor pieces) or onto the tile's middle, so it can be centred in a room of any width.
            if (2f * half > size + 0.05f)
            {
                if (aimed < low + size / 3f) return low;
                if (aimed > high - size / 3f) return high;
                return (low + high) * 0.5f;
            }
            if (aimed < low + size / 3f) return low + half - over;
            if (aimed > high - size / 3f) return high - half + over;
            return (low + high) * 0.5f;
        }

        // The target handed to the game (it reads the target's parent, the piece it belongs to, so it needs one).
        private static void Target(Vector3 at)
        {
            if (_tile == null)
            {
                var holder = new GameObject("DigiChestSnapping");
                DontDestroyOnLoad(holder);
                _tile = new GameObject("Floor tile").transform;
                _tile.SetParent(holder.transform, false);
            }
            _tile.position = at;
        }

        private static void Pick(Player player, float maxDistance, int kind, ref Transform a, ref Transform b, ref bool result)
        {
            var mine = GhostPoints(player);
            var near = NearPoints(player);
            var manual = ManualPoint(player);
            Transform bestA = null, bestB = null;
            var best = float.MaxValue;
            var onTile = false;
            var tilePoint = Vector3.zero;
            Roots.Clear();
            foreach (var target in near)
            {
                if (target == null) continue;
                var root = target.parent;
                var targetKind = Kind(root);
                if (kind != None && targetKind == None && _floorTiles.Value && root != null) Roots.Add(root);
                // (a hanging piece, a sconce or a wall item stand has one point: onto another of its kind it would stand in the same spot)
                var allowed = kind == None ? targetKind == None
                    : (targetKind == kind && kind != Hanging && kind != Sconce && kind != Stand) || (targetKind == None && _otherPieces.Value);
                if (!allowed) continue;
                var at = target.position;
                for (var i = 0; i < mine.Count; i++)
                {
                    if ((manual >= 0 && i != manual) || mine[i] == null) continue;
                    var distance = Vector3.Distance(mine[i].position, at);
                    if (distance > maxDistance || distance >= best) continue;
                    best = distance;
                    bestA = mine[i];
                    bestB = target;
                }
            }
            // With a point picked by Q / E: onto the nearest corner, edge middle or middle of a floor tile.
            foreach (var root in Roots)
            {
                if (manual < 0) break; // without Q / E: by the zone aimed at (Zone)
                var targets = TileTargets(root);
                if (targets.Length == 0) continue;
                foreach (var local in targets)
                {
                    var at = root.TransformPoint(kind == Hanging ? new Vector3(local.x, Under(root), local.z) : local);
                    for (var i = 0; i < mine.Count; i++)
                    {
                        if ((manual >= 0 && i != manual) || mine[i] == null) continue;
                        var distance = Vector3.Distance(mine[i].position, at);
                        if (distance > maxDistance || distance >= best) continue;
                        best = distance;
                        bestA = mine[i];
                        onTile = true;
                        tilePoint = at;
                    }
                }
            }
            if (onTile)
            {
                Target(tilePoint);
                bestB = _tile;
            }
            a = bestA;
            b = bestB;
            result = bestA != null;
        }

        // A flat piece with a rectangle of snap points on top (a floor tile): its corners, the middle of each edge and its
        // middle, on its top surface (the top of its collider: a wood floor's points are 10 cm below its planks' top). With
        // snap points in between that split it into tiles of 1.9 m or more (a 4 x 4 floor's middle points), each such tile
        // the same: nine targets per tile.
        private static Vector3[] TileTargets(Transform root)
        {
            var id = root.GetInstanceID();
            if (Tiles.TryGetValue(id, out var targets)) return targets;
            if (Tiles.Count > 50000)
            {
                Tiles.Clear();
                Unders.Clear();
            }
            targets = new Vector3[0];
            float top = float.MinValue, lowest = float.MaxValue;
            for (var i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
                if (!child.CompareTag("snappoint")) continue;
                top = Mathf.Max(top, child.localPosition.y);
                lowest = Mathf.Min(lowest, child.localPosition.y);
            }
            var xs = new List<float>();
            var zs = new List<float>();
            var count = 0;
            for (var i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
                if (!child.CompareTag("snappoint") || child.localPosition.y < top - 0.05f) continue;
                xs.Add(child.localPosition.x);
                zs.Add(child.localPosition.z);
                count++;
            }
            var linesX = Lines(xs);
            var linesZ = Lines(zs);
            if (count >= 4 && linesX[linesX.Count - 1] - linesX[0] >= 0.9f && linesZ[linesZ.Count - 1] - linesZ[0] >= 0.9f)
            {
                var solid = LocalBox(root, out var box);
                var surface = solid ? Mathf.Max(top, box.max.y) : top;
                Unders[id] = solid ? box.min.y : lowest; // where a hanging piece hangs from: the bottom of its colliders
                targets = new Vector3[9 * (linesX.Count - 1) * (linesZ.Count - 1)];
                var t = 0;
                for (var ix = 0; ix + 1 < linesX.Count; ix++)
                    for (var iz = 0; iz + 1 < linesZ.Count; iz++)
                    {
                        float minX = linesX[ix], maxX = linesX[ix + 1], minZ = linesZ[iz], maxZ = linesZ[iz + 1];
                        float midX = (minX + maxX) * 0.5f, midZ = (minZ + maxZ) * 0.5f;
                        targets[t++] = new Vector3(midX, surface, minZ);
                        targets[t++] = new Vector3(midX, surface, maxZ);
                        targets[t++] = new Vector3(minX, surface, midZ);
                        targets[t++] = new Vector3(maxX, surface, midZ);
                        targets[t++] = new Vector3(midX, surface, midZ);
                        targets[t++] = new Vector3(minX, surface, minZ);
                        targets[t++] = new Vector3(minX, surface, maxZ);
                        targets[t++] = new Vector3(maxX, surface, minZ);
                        targets[t++] = new Vector3(maxX, surface, maxZ);
                    }
            }
            Tiles[id] = targets;
            return targets;
        }

        // The lines a floor's top snap points lie on in one direction, low to high (within 5 cm counts as one): all of
        // them when every gap between them is a tile of 1.9 m or more, else only the outer two.
        private static List<float> Lines(List<float> values)
        {
            var lines = new List<float>();
            if (values.Count == 0)
            {
                lines.Add(0f);
                return lines;
            }
            values.Sort();
            foreach (var v in values)
                if (lines.Count == 0 || v - lines[lines.Count - 1] > 0.05f) lines.Add(v);
            for (var i = 1; i < lines.Count; i++)
                if (lines[i] - lines[i - 1] < MinTile)
                    return new List<float> { lines[0], lines[lines.Count - 1] };
            if (lines.Count == 1) lines.Add(lines[0]);
            return lines;
        }
    }
}
