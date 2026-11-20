// Portal List: walk into a portal (or E on it) and pick where to go from a list of every portal in the world, no
// matching tags needed. Ctrl+E (a setting; with a controller the game's alt button + Use) on a portal goes straight back
// to the portal you used last (your newest trip, either end, other than this portal). A movement key pressed while the
// list is open closes it, so you can step out of a portal you walked into (the key held while walking in does not
// count); the search box only takes keys after a click, and then no key reaches the game.
// Light mode (a setting, for a portal room): portals work as in the game (walk in = by
// name, E renames), Shift+E (or the back key) opens the list only to look up and edit portals; clicking a row opens its editor. The list shows each portal's icon, name in its colour, biome and distance; a search box; sorting
// by most recently used, by name (A-Z) or by distance; favourites (a star) at the top; the portal you stand at on top
// ("you are here"). Every row has an Edit button that opens a small editor under it: the portal's name (up to 32
// letters), a colour and an icon, which every player sees (kept on the portal itself).
// A portal far away is changed through the server; others' portals only by whoever built them, or the host.
// Favourites and recent trips are your own, per world, kept with your character, by the portal's place (the game
// numbers every object anew each time a world loads, so an object's ID does not last past a restart).
// Multiplayer: every portal is private until its builder shares it (Private / Shared on your own rows): the list shows
// you your own portals, shared ones and unowned ones; the server sends each player only those. The game's name pairing
// is held to the same rule on the server: a private portal pairs only with portals of the same builder.
// With Auto Map Pins (our map mod, which pins every portal): the editor has "Map pin" Shown / Hidden, and portals whose
// pin is hidden (switched off there, or deleted on the map with a right-click) sit in a "Hidden" section at the bottom of
// the list, closed until clicked; favourites stay at the top. Auto Map Pins keeps that per world and character.
// The window is built once; sorting, stars and edits redraw only the list, in place, keeping where it is scrolled to.
// The game's tag pairing still works: the portal a tag links to is marked in the list. With a portal list a portal may
// lead anywhere, so portals glow as ready even without a matching tag (a setting).
// In multiplayer the server holds every portal, so the list comes from the server (it needs this mod too); without it,
// the list has the portals your own game knows. Going through checks what the game checks: ores and other
// non-teleportable items (unless the portal you stand at lets them through), and the world's no-portal rules.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using BepInEx.Configuration;
using DigiShared;
using HarmonyLib;
using Splatform;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PortalList
{
    public class Plugin : DigiCore.DigiModule
    {
        private const string AskRpc = "Digi_PortalList_Ask", ListRpc = "Digi_PortalList", EditRpc = "Digi_PortalList_Edit";
        private const string ColorKey = "digi_portal_color", IconKey = "digi_portal_icon", SharedKey = "digi_portal_shared";
        private const int NameLimit = 32, EditName = 0, EditColor = 1, EditIcon = 2, EditShared = 3;
        private const float Width = 900f, Height = 760f, ListTop = 160f, RowHeight = 50f, HeaderHeight = 34f, Cell = 46f;

        public enum SortBy { Recent, Name, Nearest, Biome }

        // The biomes in the order players meet them (the ocean last).
        private static readonly Heightmap.Biome[] BiomeOrder =
        {
            Heightmap.Biome.Meadows, Heightmap.Biome.BlackForest, Heightmap.Biome.Swamp, Heightmap.Biome.Mountain, Heightmap.Biome.Plains,
            Heightmap.Biome.Mistlands, Heightmap.Biome.AshLands, Heightmap.Biome.DeepNorth, Heightmap.Biome.Ocean,
        };

        private static ConfigEntry<bool> _onEnter, _glow, _zToA, _light, _moveCloses;
        private static ConfigEntry<SortBy> _sort;
        private static ConfigEntry<KeyboardShortcut> _backKey;

        // The colours a portal can have (0: none, the list's normal text colour).
        private static readonly Color[] Colors =
        {
            new Color(0.92f, 0.9f, 0.86f), new Color(0.95f, 0.33f, 0.3f), new Color(1f, 0.6f, 0.2f), new Color(1f, 0.88f, 0.3f),
            new Color(0.45f, 0.85f, 0.35f), new Color(0.3f, 0.85f, 0.8f), new Color(0.4f, 0.6f, 1f), new Color(0.72f, 0.48f, 1f),
            new Color(1f, 0.5f, 0.8f), new Color(0.6f, 0.6f, 0.6f),
            // added later (kept on portals by number, so only ever at the end): darker and in-between shades
            new Color(0.75f, 0.18f, 0.16f), new Color(0.62f, 0.4f, 0.2f), new Color(0.85f, 0.68f, 0.2f), new Color(0.2f, 0.58f, 0.25f),
            new Color(0.72f, 0.95f, 0.3f), new Color(0.12f, 0.58f, 0.56f), new Color(0.58f, 0.84f, 1f), new Color(0.22f, 0.36f, 0.82f),
            new Color(0.5f, 0.28f, 0.75f), new Color(0.9f, 0.3f, 0.72f), new Color(1f, 1f, 1f),
        };

        // The order the editor shows them in: by shade, each light colour next to its dark one.
        private static readonly int[] ColorOrder = { 0, 1, 10, 2, 11, 12, 3, 14, 4, 13, 5, 15, 16, 6, 17, 7, 18, 8, 19, 9, 20 };

        // The icons a portal can have (0: the portal's own): item pictures, "piece:" a building piece's picture, "pin:" a map
        // pin's, "loc:" a map location's (the traders). Kept on the portal by number, so new ones only ever go at the end.
        // Names that do not exist are left out.
        private static readonly string[] IconItems =
        {
            "", "Hammer", "Cultivator", "PickaxeIron", "AxeIron", "FishingRod", "Coins", "SwordIron", "BowFineWood", "Honey",
            "Tankard", "Torch", "TrophyEikthyr", "TrophyTheElder", "TrophyBonemass", "TrophyDragonQueen", "TrophyGoblinKing",
            "TrophySeekerQueen", "TrophyFader", "CopperOre", "TinOre", "IronScrap", "SilverOre", "BlackMetalScrap", "Tar",
            "Wood", "Stone", "Raspberry", "Mushroom",
            // home
            "pin:Icon1", "pin:Bed", "piece:bed", "piece:piece_bed02", "piece:hearth", "piece:fire_pit", "pin:Icon0",
            "piece:piece_chest_wood", "piece:piece_workbench", "piece:forge", "piece:piece_cauldron", "piece:piece_table",
            "piece:piece_chair", "piece:wood_door", "piece:piece_banner01", "piece:piece_cartographytable", "piece:piece_oven",
            "piece:fermenter", "piece:smelter", "piece:windmill", "piece:piece_beehive", "pin:Icon4", "pin:Icon2",
            // traders and places (the map's own icons), more items
            "loc:Vendor_BlackForest", "loc:Hildir_camp", "loc:BogWitch_Camp", "loc:StartTemple", "SurtlingCore",
            // the Deep North boss (the Frozen King drops no trophy): its Crown Jewel and the Sacrificial Blood
            "CrownJewel", "FrozenKingDrop",
            // more materials
            "Sap", "Eitr", "YggdrasilWood", "BlackMarble", "Carapace", "Softtissue", "BlackCore", "Flametal", "FlametalNew",
            "Grausten", "CharredBone", "Obsidian", "Crystal", "FreezeGland", "Thunderstone", "DragonEgg", "Chitin", "Feathers",
            "WolfPelt", "LoxPelt",
            // the Deep North: its creatures' trophies and its materials
            "TrophyMoose", "TrophyJotunWarrior", "TrophyJotunWitch", "TrophyElaking", "TrophyBarka", "TrophySeal", "TrophyFrostTroll",
            "Frostwood", "Ice", "Lingonberry", "MooseHide", "SealHide", "NornThread", "ElakingHairBundle", "BloodGoldKey",
        };

        // Items shown right after the boss trophies (the last one is the Fader's) instead of where they stand in the list.
        private static readonly string[] AfterTrophies = { "CrownJewel", "FrozenKingDrop" };

        // Not offered in the editor any more (too many to pick from), but kept in the list: a portal that has one keeps it.
        private static readonly HashSet<string> Hidden = new HashSet<string>
        {
            "Hammer", "Coins", "BowFineWood", "Honey", "Torch", "Wood", "Stone", "Raspberry", "Mushroom",
            "piece:piece_bed02", "piece:fire_pit", "piece:piece_table", "piece:piece_chair", "piece:wood_door", "piece:piece_banner01",
            "piece:piece_oven", "piece:windmill", "FrozenKingDrop",
            "YggdrasilWood", "BlackMarble", "Carapace", "Softtissue", "BlackCore", "Grausten", "CharredBone", "Obsidian", "Crystal",
            "FreezeGland", "Thunderstone", "Chitin", "Feathers", "WolfPelt", "LoxPelt",
            "TrophyMoose", "TrophyJotunWarrior", "TrophyJotunWitch", "TrophyElaking", "TrophyBarka", "TrophySeal", "TrophyFrostTroll",
            "MooseHide", "SealHide", "NornThread", "ElakingHairBundle", "BloodGoldKey",
        };

        // Offered in the editor: not hidden (or the portal's own), and flametal only once (the game may have two).
        private static bool Offered(int i, int current)
        {
            if (i == current || i == 0) return true;
            var key = IconItems[i];
            if (Hidden.Contains(key)) return false;
            return key != "Flametal" || ItemIcon("FlametalNew") == null;
        }

        // The order the editor shows them in: the portal's own first, then the home ones, then the rest.
        private static IEnumerable<int> IconOrder()
        {
            yield return 0;
            for (var i = 1; i < IconItems.Length; i++)
                if (IconItems[i].StartsWith("loc:", StringComparison.Ordinal)) yield return i;
            for (var i = 1; i < IconItems.Length; i++)
                if (IconItems[i].StartsWith("pin:", StringComparison.Ordinal) || IconItems[i].StartsWith("piece:", StringComparison.Ordinal)) yield return i;
            for (var i = 1; i < IconItems.Length; i++)
            {
                if (IconItems[i].IndexOf(':') >= 0 || Array.IndexOf(AfterTrophies, IconItems[i]) >= 0) continue;
                yield return i;
                if (IconItems[i] == "TrophyFader")
                    foreach (var boss in AfterTrophies) yield return Array.IndexOf(IconItems, boss);
            }
        }

        private sealed class Entry
        {
            public ZDOID Id;
            public string Name, Prefab;
            public Vector3 Position;
            public Quaternion Rotation;
            public int Color, Icon;
            public long Creator;
            public bool Shared;
        }

        private static List<Entry> _entries = new List<Entry>();
        private static bool _fromServer, _asking;
        private static float _askedAt;
        private static TeleportWorld _source;
        private static Windows.Window _window;
        private static TMP_InputField _searchBox;
        private static TextMeshProUGUI _subtitle; // "You are at: ...", kept up to date
        private static readonly List<KeyValuePair<SortBy, Button>> SortButtons = new List<KeyValuePair<SortBy, Button>>();
        private static string _search = "";
        private static ZDOID _editing = ZDOID.None; // the row whose editor is open
        private static List<ZDOID> _frozenFavourites, _frozenRest, _frozenHidden; // the order while an editor is open
        private static bool _hiddenOpen;             // the Hidden section is open (until the game closes)
        private static readonly Dictionary<ZDOID, int> PinStates = new Dictionary<ZDOID, int>(); // this layout's map pin states
        private static string _startName;            // that portal as it was when Edit was clicked (Reset)
        private static int _startColor, _startIcon;
        private static Entry _typing;                // a name typed but not saved yet, saved half a second after the last key
        private static string _typed;
        private static float _typedAt;

        private static BepInEx.Logging.ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            _light = Config.Bind("General", "Light mode", false,
                "Portals work as in the game: walk in and you go to the portal with the same name, E renames it. Shift+E opens " +
                "the list to look up your portals and edit them (name, colour, icon), also far away ones; clicking a portal " +
                "there opens its editor and takes you nowhere. For a portal room with a portal per place. Off: the full list " +
                "(walk in or E to pick where to go, Ctrl+E back to the portal you used last).");
            _backKey = Config.Bind("General", "Back to last portal key", new KeyboardShortcut(KeyCode.E, KeyCode.LeftControl),
                "Looking at a portal, this key takes you straight back to the portal you used last (in light mode it opens the " +
                "list). E alone still opens the list. Ctrl, Shift and Alt count on either side of the keyboard. With a " +
                "controller: the game's alt button + Use.");
            _moveCloses = Config.Bind("General", "Moving closes the list", true,
                "A movement key (W, A, S, D or the controller's stick) pressed while the list is open closes it and you walk " +
                "on, out of the portal you walked into. The key you were holding when you walked in does not count. The " +
                "search box only takes keys after you click it; while you type there no key reaches the game.");
            _onEnter = Config.Bind("General", "Open the list when walking into a portal", true,
                "Walking into a portal opens the list of every portal in the world: click one to go there. Off: the game's " +
                "tag pairing as before; E on a portal still opens the list.");
            _glow = Config.Bind("General", "Portals glow without a matching tag", true,
                "With the list any portal can lead anywhere, so portals light up as ready even when no other portal has the same name.");
            _sort = Config.Bind("General", "Sort", SortBy.Recent,
                "How the list is sorted (the buttons in the list change it): Recent = the portals you went to last first, " +
                "Name = A to Z, Nearest = closest to the portal you stand at first, Biome = by biome in the order you meet them " +
                "(A to Z within each). Favourites always come first.");
            _zToA = Config.Bind("General", "Name sorting Z to A", false,
                "The name sorting from Z to A instead of A to Z (clicking the name sort button again switches it).");
            PatchModule(typeof(Plugin));
            Windows.Patch(ModuleHarmony);
            Windows.Use(gameObject);
            RegisterRpcs();
        }

        private void OnDestroy()
        {
            if (IsOpen) Windows.Close();
            Windows.Release(gameObject);
            _window = null;
            _source = null;
        }

        private static bool IsOpen => Windows.IsOpen && Windows.Current == _window;

        // --- the game's portal --------------------------------------------------------------------------------------

        // Walking into a portal: the list instead of the tag partner.
        [HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.Teleport))]
        [HarmonyPrefix]
        private static bool Entered(TeleportWorld __instance, Player player)
        {
            if (_light.Value || !_onEnter.Value || player == null || player != Player.m_localPlayer || player.IsTeleporting()) return true;
            Open(__instance);
            return false;
        }

        // E: the list. Ctrl+E (the back key): back to the portal you used last. Renaming is done in the list (Edit on the
        // portal's row). Light mode: E renames as in the game (with room for 32 letters), Shift+E opens the list (as since
        // 1.0.6; light mode has no going back, so the back key opens it too).
        // The game calls this for E with alt = Shift (or the controller's alt button) held: on a keyboard Shift+E is now
        // just E. The back key is read here (E with its modifiers) and in Update (a back key other than E never reaches
        // Interact); whichever comes first in a frame does it.
        [HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.Interact))]
        [HarmonyPrefix]
        private static bool Interacted(TeleportWorld __instance, Humanoid human, bool hold, bool alt, ref bool __result)
        {
            if (hold) return true;
            __result = true;
            if (human != Player.m_localPlayer || _backFrame == Time.frameCount) return false;
            if (_light.Value && alt)
            {
                Open(__instance);
                return false;
            }
            if (BackKeyDown() || alt && ZInput.IsGamepadActive())
            {
                Back(__instance);
                return false;
            }
            if (_light.Value)
            {
                if (!PrivateArea.CheckAccess(__instance.transform.position)) human.Message(MessageHud.MessageType.Center, "$piece_noaccess");
                else TextInput.instance.RequestText(__instance, "$piece_portal_tag", NameLimit);
                return false;
            }
            Open(__instance);
            return false;
        }

        private static int _backFrame = -1; // the frame the back key was handled in (Update or Interact, not both)

        private static void Back(TeleportWorld portal)
        {
            _backFrame = Time.frameCount;
            UndoCrouch();
            if (_light.Value) Open(portal);
            else GoBack(portal);
        }

        // Ctrl is also the game's crouch key, and crouch toggles the moment it goes down (Player.SetControls), before E
        // is pressed: Ctrl+E left the player crouching. The crouch state from before the back key's modifiers went down is
        // remembered every frame (Update) and put back when the back key is used. A crouch key other than Ctrl: no change.
        private static bool _crouchBefore;

        private static void RememberCrouch()
        {
            var player = Player.m_localPlayer;
            if (player == null || _backKey.Value.Modifiers.Any(HeldEitherSide)) return;
            _crouchBefore = player.m_crouchToggled;
        }

        private static void UndoCrouch()
        {
            var player = Player.m_localPlayer;
            if (player != null && _backKey.Value.Modifiers.Any() && player.m_crouchToggled != _crouchBefore) player.SetCrouch(_crouchBefore);
        }

        private static bool HeldEitherSide(KeyCode key) => ZInput.GetKey(key, false) || ZInput.GetKey(OtherSide(key), false);

        // The back key pressed while looking at a portal (Update: any key, not only E).
        private static void BackFromHover()
        {
            var player = Player.m_localPlayer;
            if (player == null || !player.TakeInput()) return; // chat, console, a menu or the inventory open
            var hover = player.GetHoverObject();
            var portal = hover != null ? hover.GetComponentInParent<TeleportWorld>() : null;
            if (portal != null) Back(portal);
        }

        // Its main key pressed now and its modifiers held, other keys ignored: BepInEx's IsDown() fails while any other key
        // is held too (W while walking up to the portal). Read through the game's ZInput, which gives nothing while typing
        // in a box (Windows). Ctrl, Shift and Alt count on either side.
        private static bool BackKeyDown()
        {
            var key = _backKey.Value;
            if (key.MainKey == KeyCode.None || !ZInput.GetKeyDown(key.MainKey, false)) return false;
            foreach (var modifier in key.Modifiers)
                if (!ZInput.GetKey(modifier, false) && !ZInput.GetKey(OtherSide(modifier), false)) return false;
            return true;
        }

        private static KeyCode OtherSide(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.LeftControl: return KeyCode.RightControl;
                case KeyCode.RightControl: return KeyCode.LeftControl;
                case KeyCode.LeftShift: return KeyCode.RightShift;
                case KeyCode.RightShift: return KeyCode.LeftShift;
                case KeyCode.LeftAlt: return KeyCode.RightAlt;
                case KeyCode.RightAlt: return KeyCode.LeftAlt;
                default: return key;
            }
        }

        // The back key for the hover text ("Ctrl + E"); with a controller the game's alt button + Use. Null: no back key.
        private static string BackKeyText()
        {
            if (ZInput.IsGamepadActive()) return Localization.instance.Localize("$KEY_AltPlace + $KEY_Use");
            var key = _backKey.Value;
            if (key.MainKey == KeyCode.None) return null;
            var parts = key.Modifiers.Select(KeyName).ToList();
            parts.Add(KeyName(key.MainKey));
            return string.Join(" + ", parts);
        }

        private static string KeyName(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.LeftControl: case KeyCode.RightControl: return "Ctrl";
                case KeyCode.LeftShift: case KeyCode.RightShift: return "Shift";
                case KeyCode.LeftAlt: case KeyCode.RightAlt: return "Alt";
                case KeyCode.Return: return "Enter";
                default: return key.ToString();
            }
        }

        // A new press of a movement key (W, A, S, D, or the stick pushed out of the middle). One held since before the list
        // opened (the key you walked into the portal with) is no new press. While typing in a box the game's keys answer
        // "not pressed" (Windows), so letters typed there never close the list.
        private static float _stickWas;

        private static bool MovePressed(float stick)
        {
            if (Windows.Searching) return false;
            return ZInput.GetButtonDown("Forward") || ZInput.GetButtonDown("Backward") || ZInput.GetButtonDown("Left") ||
                   ZInput.GetButtonDown("Right") || stick > 0.5f && _stickWas <= 0.5f;
        }

        // The game's "[E] Set tag" line becomes our two keys.
        [HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.GetHoverText))]
        [HarmonyPostfix]
        private static void Hover(TeleportWorld __instance, ref string __result)
        {
            var backKey = BackKeyText();
            if (_light.Value) // the game's "[E] Set tag" stays
            {
                __result += "\n" + Localization.instance.Localize("[<color=yellow><b>$KEY_AltPlace + $KEY_Use</b></color>] ") + "Portal list";
                return;
            }
            var setTag = Localization.instance.Localize("[<color=yellow><b>$KEY_Use</b></color>] $piece_portal_settag");
            var keys = Localization.instance.Localize("[<color=yellow><b>$KEY_Use</b></color>] ") + "Portal list" + (_onEnter.Value ? "  (or walk in)" : "");
            var back = BackName(__instance);
            if (back != null && backKey != null) keys += "\n[<color=yellow><b>" + backKey + "</b></color>] Back to " + back;
            var at = __result.IndexOf(setTag, StringComparison.Ordinal);
            __result = at >= 0 ? __result.Substring(0, at) + keys + __result.Substring(at + setTag.Length) : __result + "\n" + keys;
        }

        // Ready-looking without a tag partner: the glow on, the swirl on when a player who may go through is near.
        [HarmonyPatch(typeof(TeleportWorld), "UpdatePortal")]
        [HarmonyPostfix]
        private static void Glow(TeleportWorld __instance)
        {
            if (_light.Value || !_glow.Value || __instance.m_nview == null || !__instance.m_nview.IsValid() || __instance.m_proximityRoot == null) return;
            __instance.m_hadTarget = true;
            var near = Player.GetClosestPlayer(__instance.m_proximityRoot.position, __instance.m_activationRange);
            __instance.m_target_found.SetActive(near != null && near.IsTeleportable(__instance.m_allowAllItems));
        }

        // --- the portals: from the server, or the ones this game knows ----------------------------------------------

        [HarmonyPatch(typeof(Game), "Start")]
        [HarmonyPostfix]
        private static void RegisterRpcs()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null) return;
            var functions = AccessTools.Field(typeof(ZRoutedRpc), "m_functions")?.GetValue(rpc) as IDictionary;
            foreach (var name in new[] { AskRpc, ListRpc, EditRpc }) functions?.Remove(name.GetStableHashCode());
            rpc.Register<ZPackage>(AskRpc, OnAsk);
            rpc.Register<ZPackage>(ListRpc, OnList);
            rpc.Register<ZPackage>(EditRpc, OnEdit);
        }

        // The server: the portals that player may see (their own, shared and unowned ones), sent to them.
        private static void OnAsk(long sender, ZPackage ask)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            var who = ask.ReadLong();
            var list = Known().Where(e => Visible(e, who)).ToList();
            var package = new ZPackage();
            package.Write(list.Count);
            foreach (var e in list)
            {
                package.Write(e.Id);
                package.Write(e.Name ?? "");
                package.Write(e.Prefab ?? "");
                package.Write(e.Position);
                package.Write(e.Rotation);
                package.Write(e.Color);
                package.Write(e.Icon);
                package.Write(e.Creator);
                package.Write(e.Shared);
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, ListRpc, package);
        }

        private static void OnList(long sender, ZPackage package)
        {
            var list = new List<Entry>();
            var count = package.ReadInt();
            for (var i = 0; i < count; i++)
                list.Add(new Entry
                {
                    Id = package.ReadZDOID(), Name = package.ReadString(), Prefab = package.ReadString(), Position = package.ReadVector3(),
                    Rotation = package.ReadQuaternion(), Color = package.ReadInt(), Icon = package.ReadInt(), Creator = package.ReadLong(),
                    Shared = package.ReadBool(),
                });
            _entries = list;
            _fromServer = true;
            _asking = false;
            if (IsOpen) FillList();
            if (_goingBack != null) FinishGoBack(list);
        }

        // The portals this game knows: on the server (and in single player) every one; elsewhere those sent to it.
        private static List<Entry> Known()
        {
            var list = new List<Entry>();
            var seen = new HashSet<ZDOID>();
            if (ZDOMan.instance != null)
                foreach (var zdo in ZDOMan.instance.GetPortalList())
                    if (zdo != null && seen.Add(zdo.m_uid)) list.Add(FromZdo(zdo));
            foreach (var portal in FindObjectsByType<TeleportWorld>(FindObjectsSortMode.None))
            {
                var zdo = portal.m_nview != null && portal.m_nview.IsValid() ? portal.m_nview.GetZDO() : null;
                if (zdo != null && seen.Add(zdo.m_uid)) list.Add(FromZdo(zdo));
            }
            return list;
        }

        private static Entry FromZdo(ZDO zdo)
        {
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
            return new Entry
            {
                Id = zdo.m_uid, Name = zdo.GetString(ZDOVars.s_tag), Prefab = prefab != null ? prefab.name : "", Position = zdo.GetPosition(),
                Rotation = zdo.GetRotation(), Color = zdo.GetInt(ColorKey), Icon = zdo.GetInt(IconKey), Creator = zdo.GetLong(ZDOVars.s_creator),
                Shared = zdo.GetInt(SharedKey) == 1,
            };
        }

        // --- private and shared -----------------------------------------------------------------------------------

        private static long Me => Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerID() : 0L;

        // Who may see (and so go to) a portal: its builder, everyone once shared, everyone when it has no builder.
        private static bool Visible(Entry e, long who) => e.Creator == 0L || e.Shared || e.Creator == who;

        private static bool MayShare(Entry e) => Me != 0L && e.Creator == Me;

        private static bool Open(ZDO zdo) => zdo.GetLong(ZDOVars.s_creator) == 0L || zdo.GetInt(SharedKey) == 1;

        // The game's name pairing (on the server) under the same rule: a private portal only with its builder's own.
        private static bool MayPair(ZDO a, ZDO b) =>
            a == null || b == null || a.GetLong(ZDOVars.s_creator) == b.GetLong(ZDOVars.s_creator) || (Open(a) && Open(b));

        [HarmonyPatch(typeof(Game), "FindRandomUnconnectedPortal")]
        [HarmonyPrefix]
        private static bool PairAllowed(Game __instance, List<ZDO> portals, ZDO skip, string tag, ref ZDO __result)
        {
            var list = new List<ZDO>();
            foreach (var portal in portals)
                if (portal != skip && portal.GetString(ZDOVars.s_tag) == tag && portal.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal) == ZDOID.None &&
                    !__instance.IsCurrentlyConnectingPortal(portal) && MayPair(skip, portal))
                    list.Add(portal);
            __result = list.Count == 0 ? null : list[UnityEngine.Random.Range(0, list.Count)];
            return false;
        }

        // Pairs the rule no longer allows (a portal made private) are undone before the game pairs portals again.
        [HarmonyPatch(typeof(Game), nameof(Game.ConnectPortals))]
        [HarmonyPrefix]
        private static void UndoPairs(Game __instance)
        {
            if (ZDOMan.instance == null) return;
            foreach (var zdo in ZDOMan.instance.GetPortalList())
            {
                var id = zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal);
                if (id.IsNone()) continue;
                var other = ZDOMan.instance.GetZDO(id);
                if (other != null && !MayPair(zdo, other)) __instance.SetConnection(zdo, ZDOID.None);
            }
        }

        // --- changing a portal: the one here through the game, one far away on the server ---------------------------

        private static TeleportWorld Loaded(ZDOID id)
        {
            foreach (var portal in FindObjectsByType<TeleportWorld>(FindObjectsSortMode.None))
                if (portal.m_nview != null && portal.m_nview.IsValid() && portal.m_nview.GetZDO().m_uid == id) return portal;
            return null;
        }

        // May you change it: a portal near you that no ward of others keeps you from, one you built, or any as the host.
        private static bool MayEdit(Entry e)
        {
            var player = Player.m_localPlayer;
            if (player == null) return false;
            if (ZNet.instance != null && ZNet.instance.IsServer()) return true;
            if (e.Creator == 0L || e.Creator == player.GetPlayerID()) return true;
            var portal = Loaded(e.Id);
            return portal != null && PrivateArea.CheckAccess(portal.transform.position, 0f, false);
        }

        private static void Change(Entry e, int what, string text, int value)
        {
            if (e == null || !MayEdit(e)) return;
            if (what == EditShared && !MayShare(e)) return;
            if (what == EditName) e.Name = text;
            else if (what == EditColor) e.Color = value;
            else if (what == EditShared) e.Shared = value == 1;
            else e.Icon = value;
            var portal = Loaded(e.Id);
            if (portal != null || ZNet.instance == null || ZNet.instance.IsServer())
                Apply(e.Id, what, text, value, portal);
            else if (ZRoutedRpc.instance != null)
            {
                var package = new ZPackage();
                package.Write(e.Id);
                package.Write(what);
                package.Write(text ?? "");
                package.Write(value);
                ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), EditRpc, package);
            }
        }

        // A name typed in an editor: kept now (when typing paused, the box was left, or the list is drawn anew).
        private static void SaveTyped()
        {
            var e = _typing;
            _typing = null;
            if (e == null) return;
            var text = (_typed ?? "").Trim();
            if (text != (e.Name ?? "")) Change(e, EditName, text, 0);
        }

        // The server, for a player whose game does not have that portal loaded.
        private static void OnEdit(long sender, ZPackage package)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            var id = package.ReadZDOID();
            var what = package.ReadInt();
            var text = package.ReadString();
            var value = package.ReadInt();
            Apply(id, what, text, value, Loaded(id));
        }

        private static void Apply(ZDOID id, int what, string text, int value, TeleportWorld portal)
        {
            if (what == EditName && portal != null)
            {
                portal.SetText(text); // the game's own rename (it also lets go of the old tag partner)
                return;
            }
            ZDO zdo;
            if (portal != null)
            {
                if (!portal.m_nview.IsOwner()) portal.m_nview.ClaimOwnership();
                zdo = portal.m_nview.GetZDO();
            }
            else
            {
                zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(id) : null;
                if (zdo == null) return;
                if (!zdo.IsOwner()) zdo.SetOwner(ZDOMan.GetSessionID());
            }
            if (what == EditName)
            {
                zdo.Set(ZDOVars.s_tag, text ?? "");
                zdo.Set(ZDOVars.s_tagauthor, PlatformManager.DistributionPlatform.LocalUser.PlatformUserID.ToString());
            }
            else zdo.Set(what == EditColor ? ColorKey : what == EditShared ? SharedKey : IconKey, value);
        }

        // --- your favourites and trips (per world, kept with your character) ---------------------------------------
        // Kept by the portal's place: ZDO.Load gives every object a new ID (ZDOID.m_loadID) whenever a world loads, so the
        // IDs saved before (up to Portal List 1.0.8) pointed at nothing, or at another object, after a restart. A portal
        // never moves and its place is saved exactly, so the place (to 10 cm) names it across restarts and servers.

        private static readonly HashSet<string> Favourites = new HashSet<string>();
        private static readonly Dictionary<string, long> Trips = new Dictionary<string, long>(); // portal place -> when you last went there
        private static string _loadedKey;

        private static string PlaceKey(Vector3 p) =>
            p.x.ToString("0.0", CultureInfo.InvariantCulture) + "," + p.y.ToString("0.0", CultureInfo.InvariantCulture) + "," +
            p.z.ToString("0.0", CultureInfo.InvariantCulture);

        private static string KeyOf(Entry e) => e != null ? PlaceKey(e.Position) : null;

        private static string DataKey => ZNet.instance != null ? "digi.portallist." + ZNet.instance.GetWorldUID().ToString(CultureInfo.InvariantCulture) : null;

        private static void LoadMine()
        {
            var player = Player.m_localPlayer;
            var key = DataKey;
            if (player == null || key == null || key + player.GetPlayerID() == _loadedKey) return;
            _loadedKey = key + player.GetPlayerID();
            Favourites.Clear();
            Trips.Clear();
            if (!player.m_customData.TryGetValue(key, out var text) || string.IsNullOrEmpty(text)) return;
            // "p:<place>;" a favourite, "r:<place>:<ticks>;" a trip. The old "f:" and "t:" entries (by ID) are dropped: their
            // IDs were from an earlier session.
            foreach (var part in text.Split(';'))
            {
                var bits = part.Split(':');
                if (bits.Length < 2 || bits[1].Length == 0) continue;
                if (bits[0] == "p") Favourites.Add(bits[1]);
                else if (bits[0] == "r" && bits.Length > 2 && long.TryParse(bits[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var when)) Trips[bits[1]] = when;
            }
        }

        private static void SaveMine()
        {
            var player = Player.m_localPlayer;
            var key = DataKey;
            if (player == null || key == null) return;
            var text = new StringBuilder();
            foreach (var f in Favourites) text.Append("p:").Append(f).Append(';');
            foreach (var t in Trips.OrderByDescending(t => t.Value).Take(200))
                text.Append("r:").Append(t.Key).Append(':').Append(t.Value.ToString(CultureInfo.InvariantCulture)).Append(';');
            player.m_customData[key] = text.ToString();
        }

        // --- the window: built once; the list redrawn in place --------------------------------------------------------

        private static void Open(TeleportWorld source)
        {
            var player = Player.m_localPlayer;
            if (player == null || source == null || player.IsDead() || InventoryGui.IsVisible() || Minimap.IsOpen()) return;
            if (IsOpen && _source == source) return;
            LoadMine();
            _pinsLooked = false;
            _source = source;
            _editing = ZDOID.None;
            _entries = Known();
            _fromServer = ZNet.instance != null && ZNet.instance.IsServer();
            if (!_fromServer && ZRoutedRpc.instance != null)
            {
                _asking = true;
                _askedAt = Time.unscaledTime;
                var ask = new ZPackage();
                ask.Write(Me);
                ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), AskRpc, ask);
            }
            OpenWindow();
        }

        private void Update()
        {
            RememberCrouch();
            var stick = ZInput.GetJoyLeftStick().magnitude;
            if (IsOpen)
            {
                if (_moveCloses.Value && MovePressed(stick)) Windows.Close(); // the character walks on from the next frame
            }
            else if (!Windows.IsOpen && _backFrame != Time.frameCount && BackKeyDown()) BackFromHover();
            _stickWas = stick;
            if (_typing != null && (!IsOpen || Time.unscaledTime - _typedAt > 0.5f)) SaveTyped();
            if (_asking && Time.unscaledTime - _askedAt > 3f)
            {
                _asking = false; // no answer: the server does not have this mod
                if (IsOpen) FillList();
                if (_goingBack != null) FinishGoBack(Known());
            }
        }

        private static void OpenWindow()
        {
            var closes = _moveCloses.Value ? " Esc or walking closes." : " Esc closes.";
            var w = Windows.Open("Portals", "", Width, Height, _light.Value
                    ? "Light mode: click a portal to edit it; portals take you where the same name is, as in the game." + closes
                    : "Click a portal to go there. Private: only you see it; Shared: everyone can go there." + closes,
                RowHeight * 2f, null, ListTop);
            if (w == null) return;
            _window = w;
            _subtitle = Windows.Text(w.Panel, "", 20f, Windows.Dim, TextAlignmentOptions.Left);
            Windows.Place(_subtitle.rectTransform, Windows.Pad, 62f, Width - 2f * Windows.Pad, 30f);
            UpdateSubtitle();
            Rows.Clear(); // the old window's rows went with it
            Extras.Clear();
            SortButtons.Clear();
            var x = Windows.Pad;
            foreach (var sort in new[] { SortBy.Recent, SortBy.Name, SortBy.Nearest, SortBy.Biome })
            {
                var label = sort == SortBy.Recent ? "Recent" : sort == SortBy.Name ? "A - Z" : sort == SortBy.Nearest ? "Nearest" : "Biome";
                var button = Windows.Button(w.Panel, label, x, 104f, 104f, 40f, () =>
                {
                    _sortClickFrame = Time.frameCount;
                    _sort.Value = sort;
                    _editing = ZDOID.None; // a new sorting closes an open editor (which holds the order still)
                    UpdateSortButtons();
                    FillList(false);
                });
                Instant(button);
                if (sort == SortBy.Name) button.gameObject.AddComponent<SortAgain>(); // the chosen one clicked again: Z - A
                SortButtons.Add(new KeyValuePair<SortBy, Button>(sort, button));
                x += 110f;
            }
            UpdateSortButtons();
            _searchBox = Windows.SearchBox(w.Panel, Width - Windows.Pad - 300f, 105f, 300f, 38f, "Search a name or biome...");
            _searchBox.SetTextWithoutNotify(_search);
            _searchBox.onValueChanged.AddListener(text =>
            {
                _search = text ?? "";
                FillList(false);
            });
            _searchBox.onSelect.AddListener(_ => { if (_window != null) _window.Search = _searchBox; });
            w.Search = _searchBox;
            FillList(false);
        }

        // "You are at: <name>" (a name being typed for this portal shows at once), and a warning about what you carry.
        private static void UpdateSubtitle(string typing = null)
        {
            if (_subtitle == null) return;
            var text = "You are at: " + Display(typing ?? SourceEntry()?.Name);
            var player = Player.m_localPlayer;
            if (player != null && !player.IsTeleportable(_source != null && _source.m_allowAllItems))
                text += "  <color=#ff7060>- you carry something that cannot go through portals</color>";
            _subtitle.text = text;
        }

        private static void UpdateSortButtons()
        {
            foreach (var kv in SortButtons)
            {
                if (kv.Value == null) continue;
                kv.Value.interactable = kv.Key != _sort.Value; // the game's own tabs: the chosen one looks chosen
                if (kv.Key == SortBy.Name) Windows.SetLabel(kv.Value, _zToA.Value ? "Z - A" : "A - Z");
            }
        }

        // The frame a sort button was clicked (choosing a sorting is not also a second click on it).
        private static int _sortClickFrame = -1;

        // The name sort button clicked while it is the chosen sorting (the game's button ignores clicks then): A - Z and
        // Z - A switch.
        private sealed class SortAgain : MonoBehaviour, IPointerClickHandler
        {
            public void OnPointerClick(PointerEventData data)
            {
                if (_sort.Value != SortBy.Name || Time.frameCount == _sortClickFrame) return;
                _zToA.Value = !_zToA.Value;
                _editing = ZDOID.None;
                UpdateSortButtons();
                FillList(false);
            }
        }

        // A button that changes its look at once (no colour fade, which can show it bright for a few frames).
        private static void Instant(Selectable button)
        {
            if (button == null) return;
            var colors = button.colors;
            colors.fadeDuration = 0f;
            button.colors = colors;
        }

        // --- the list: every row built once, then only laid out and updated --------------------------------------------

        private sealed class RowParts
        {
            public Entry E;
            public RectTransform Rect;
            public Image Background, Stripe, Icon, Kind;
            public RowHover Hover;
            public TextMeshProUGUI Star, Name, Biome, Far;
            public Button Edit, Share;
        }

        private static readonly Dictionary<ZDOID, RowParts> Rows = new Dictionary<ZDOID, RowParts>();
        private static readonly List<GameObject> Extras = new List<GameObject>(); // headings, notes, the editor: drawn anew

        // The list laid out again: the same rows moved and updated, the headings and the editor drawn anew; it stays
        // scrolled where it was (unless asked not to: a new sorting or search starts at the top).
        private static void FillList(bool keepScroll = true)
        {
            var w = _window;
            if (w == null || w.Content == null) return;
            SaveTyped(); // a name being typed is kept before its box is drawn anew
            var scrolled = w.Content.anchoredPosition.y;
            foreach (var old in Extras)
                if (old != null)
                {
                    old.SetActive(false);
                    Destroy(old);
                }
            Extras.Clear();
            if (w.Search != _searchBox) w.Search = _searchBox; // an editor's name box went with it
            var height = Layout(w);
            UpdateSubtitle();
            w.Content.sizeDelta = new Vector2(0f, height);
            var max = Mathf.Max(0f, height - w.View.rect.height);
            w.Content.anchoredPosition = new Vector2(0f, keepScroll ? Mathf.Clamp(scrolled, 0f, max) : 0f);
            w.Gliding = false;
        }

        private static Entry SourceEntry()
        {
            var zdo = _source != null && _source.m_nview != null && _source.m_nview.IsValid() ? _source.m_nview.GetZDO() : null;
            if (zdo == null) return null;
            return _entries.FirstOrDefault(e => e.Id == zdo.m_uid) ?? FromZdo(zdo);
        }

        private static string Display(string name) => string.IsNullOrWhiteSpace(name) ? "(no name)" : name.RemoveRichTextTags();

        private static Color ColorOf(int index) => index > 0 && index < Colors.Length ? Colors[index] : Windows.Body;

        private static Sprite IconOf(Entry e)
        {
            if (e.Icon > 0 && e.Icon < IconItems.Length)
            {
                var sprite = IconSprite(IconItems[e.Icon]);
                if (sprite != null) return sprite;
            }
            return PortalIcon(e.Prefab);
        }

        private static Sprite PortalIcon(string prefabName)
        {
            var prefab = ZNetScene.instance != null && !string.IsNullOrEmpty(prefabName) ? ZNetScene.instance.GetPrefab(prefabName) : null;
            var piece = prefab != null ? prefab.GetComponent<Piece>() : null;
            return piece != null ? piece.m_icon : null;
        }

        private static Sprite IconSprite(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (key.StartsWith("piece:", StringComparison.Ordinal)) return PortalIcon(key.Substring(6));
            if (key.StartsWith("loc:", StringComparison.Ordinal)) return Minimap.instance != null ? Minimap.instance.GetLocationIcon(key.Substring(4)) : null;
            if (key.StartsWith("pin:", StringComparison.Ordinal))
                return Minimap.instance != null && Enum.TryParse(key.Substring(4), out Minimap.PinType pin) ? Minimap.instance.GetSprite(pin) : null;
            return ItemIcon(key);
        }

        private static Sprite ItemIcon(string item)
        {
            if (string.IsNullOrEmpty(item) || ObjectDB.instance == null) return null;
            var prefab = ObjectDB.instance.GetItemPrefab(item);
            var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            return drop != null && drop.m_itemData.m_shared.m_icons != null && drop.m_itemData.m_shared.m_icons.Length > 0 ? drop.m_itemData.GetIcon() : null;
        }

        private static string Biome(Vector3 p)
        {
            if (WorldGenerator.instance == null) return "";
            var biome = WorldGenerator.instance.GetBiome(p.x, p.z);
            return Localization.instance.Localize("$biome_" + biome.ToString().ToLowerInvariant());
        }

        private static int BiomeRank(Vector3 p)
        {
            if (WorldGenerator.instance == null) return 99;
            var at = Array.IndexOf(BiomeOrder, WorldGenerator.instance.GetBiome(p.x, p.z));
            return at < 0 ? 98 : at;
        }

        private static string Distance(float meters) => meters >= 1000f ? (meters / 1000f).ToString("0.0", CultureInfo.InvariantCulture) + " km" : Mathf.RoundToInt(meters) + " m";

        // --- Auto Map Pins: a portal's map pin ------------------------------------------------------------------

        // Auto Map Pins' PortalPins, found by reflection (neither mod needs the other): Hidden(place) is 1 when its pin of
        // the portal there is hidden, 0 when not, -1 when unknown (not installed, switched off, no world);
        // SetHidden(place, hidden) hides or shows it.
        private static Func<Vector3, int> _pinHidden;
        private static Func<Vector3, bool, bool> _setPinHidden;
        private static bool _pinsLooked;

        private static void FindMapPins()
        {
            if (_pinsLooked) return;
            _pinsLooked = true;
            _pinHidden = null;
            _setPinHidden = null;
            try
            {
                // The newest copy: a hot reload loads Auto Map Pins anew next to the old one, whose pins are gone.
                Type type = null;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var found = assembly.GetType("AutoMapPins.PortalPins", false);
                    if (found != null) type = found;
                }
                if (type == null) return;
                _pinHidden = AccessTools.MethodDelegate<Func<Vector3, int>>(AccessTools.Method(type, "Hidden"));
                _setPinHidden = AccessTools.MethodDelegate<Func<Vector3, bool, bool>>(AccessTools.Method(type, "SetHidden"));
            }
            catch (Exception e)
            {
                _pinHidden = null;
                _setPinHidden = null;
                Log?.LogWarning("Auto Map Pins found, but not its portal pins: " + e.Message);
            }
        }

        private static int PinState(Entry e)
        {
            FindMapPins();
            if (_pinHidden == null || e == null) return -1;
            try
            {
                return _pinHidden(e.Position);
            }
            catch
            {
                return -1;
            }
        }

        private static void SetPinHidden(Entry e, bool hidden)
        {
            FindMapPins();
            if (_setPinHidden == null || e == null) return;
            try
            {
                _setPinHidden(e.Position, hidden);
            }
            catch (Exception ex)
            {
                Log?.LogWarning("Could not change the portal's map pin: " + ex.Message);
            }
        }

        private static bool PinHidden(Entry e) => e != null && PinStates.TryGetValue(e.Id, out var state) && state == 1;

        private static float Layout(Windows.Window w)
        {
            var here = SourceEntry();
            var from = _source != null ? _source.transform.position : Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;
            var hereZdo = _source != null && _source.m_nview != null && _source.m_nview.IsValid() ? _source.m_nview.GetZDO() : null;
            var linked = hereZdo != null ? hereZdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal) : ZDOID.None;
            var search = (_search ?? "").Trim();
            var me = Me;
            var rows = _entries.Where(e => here == null || e.Id != here.Id).Where(e => Visible(e, me)).Where(e => search.Length == 0 ||
                Display(e.Name).IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                Biome(e.Position).IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
            IEnumerable<Entry> sorted;
            switch (_sort.Value)
            {
                case SortBy.Name:
                    var byName = rows.OrderBy(e => string.IsNullOrWhiteSpace(e.Name) ? 1 : 0); // portals without a name last either way
                    sorted = _zToA.Value ? byName.ThenByDescending(e => Display(e.Name), StringComparer.CurrentCultureIgnoreCase)
                        : byName.ThenBy(e => Display(e.Name), StringComparer.CurrentCultureIgnoreCase);
                    break;
                case SortBy.Nearest:
                    sorted = rows.OrderBy(e => (e.Position - from).sqrMagnitude);
                    break;
                case SortBy.Biome:
                    sorted = rows.OrderBy(e => BiomeRank(e.Position)).ThenBy(e => string.IsNullOrWhiteSpace(e.Name) ? 1 : 0)
                        .ThenBy(e => Display(e.Name), StringComparer.CurrentCultureIgnoreCase);
                    break;
                default:
                    sorted = rows.OrderByDescending(e => Trips.TryGetValue(KeyOf(e), out var t) ? t : 0L).ThenBy(e => Display(e.Name), StringComparer.CurrentCultureIgnoreCase);
                    break;
            }
            var list = sorted.ToList();
            PinStates.Clear();
            foreach (var e in list) PinStates[e.Id] = PinState(e);
            if (here != null) PinStates[here.Id] = PinState(here);
            var favourites = list.Where(e => Favourites.Contains(KeyOf(e))).ToList();
            var rest = list.Where(e => !Favourites.Contains(KeyOf(e)) && !PinHidden(e)).ToList();
            var hidden = list.Where(e => !Favourites.Contains(KeyOf(e)) && PinHidden(e)).ToList();
            // While an editor is open nothing moves (a rename under A - Z, a star): the order it had when it opened stays,
            // and the list sorts again once it is closed.
            if (_editing == ZDOID.None) _frozenFavourites = _frozenRest = _frozenHidden = null;
            else if (_frozenFavourites == null)
            {
                _frozenFavourites = favourites.Select(e => e.Id).ToList();
                _frozenRest = rest.Select(e => e.Id).ToList();
                _frozenHidden = hidden.Select(e => e.Id).ToList();
            }
            else
            {
                var byId = list.ToDictionary(e => e.Id);
                var placed = new HashSet<ZDOID>();
                List<Entry> Keep(List<ZDOID> order, List<Entry> fresh)
                {
                    var kept = new List<Entry>();
                    foreach (var id in order)
                        if (byId.TryGetValue(id, out var e) && placed.Add(id)) kept.Add(e);
                    return kept;
                }
                var newFavourites = Keep(_frozenFavourites, favourites);
                var newRest = Keep(_frozenRest, rest);
                var newHidden = Keep(_frozenHidden, hidden);
                foreach (var e in favourites) if (placed.Add(e.Id)) newFavourites.Add(e); // came in while editing (a search)
                foreach (var e in rest) if (placed.Add(e.Id)) newRest.Add(e);
                foreach (var e in hidden) if (placed.Add(e.Id)) newHidden.Add(e);
                favourites = newFavourites;
                rest = newRest;
                hidden = newHidden;
            }
            var shown = new HashSet<ZDOID>();
            var y = 0f;
            if (_asking) y = Note(w, "Asking the server for every portal...", y);
            else if (!_fromServer) y = Note(w, "The server does not have Portal List: these are the portals your game knows (those you were near).", y);
            var index = 0;
            if (here != null) y = Place(w, here, from, false, true, y, index++, shown);
            if (list.Count == 0)
                y = Note(w, search.Length > 0 ? $"No portal matches \"{search}\"." : "There is no other portal yet.", y + 4f);
            if (favourites.Count > 0)
            {
                y = Header(w, "Favourites", y + 4f);
                foreach (var e in favourites) y = Place(w, e, from, e.Id == linked, false, y, index++, shown);
            }
            if (rest.Count > 0 && _sort.Value == SortBy.Biome)
            {
                // a heading per biome
                var lastRank = -1;
                foreach (var e in rest)
                {
                    var rank = BiomeRank(e.Position);
                    if (rank != lastRank)
                    {
                        y = Header(w, Biome(e.Position), y + 4f);
                        lastRank = rank;
                    }
                    y = Place(w, e, from, e.Id == linked, false, y, index++, shown);
                }
            }
            else if (rest.Count > 0)
            {
                y = Header(w, _sort.Value == SortBy.Recent ? "All portals, last visited first" : _sort.Value == SortBy.Name ? (_zToA.Value ? "All portals, Z - A" : "All portals, A - Z") :
                    "All portals, nearest first", y + 4f);
                foreach (var e in rest) y = Place(w, e, from, e.Id == linked, false, y, index++, shown);
            }
            if (hidden.Count > 0)
            {
                // Open while searching, or while one of them is being edited (its pin switched off just now).
                var open = _hiddenOpen || search.Length > 0 || hidden.Any(e => e.Id == _editing);
                y = HiddenHeader(w, hidden.Count, open, y + 4f);
                if (open) foreach (var e in hidden) y = Place(w, e, from, e.Id == linked, false, y, index++, shown);
            }
            // rows not shown now (searched away, or gone from the list) are hidden; those of portals gone are dropped
            var known = new HashSet<ZDOID>(_entries.Select(e => e.Id));
            if (here != null) known.Add(here.Id);
            foreach (var id in Rows.Keys.ToList())
            {
                var parts = Rows[id];
                if (parts.Rect == null || !known.Contains(id))
                {
                    if (parts.Rect != null) Destroy(parts.Rect.gameObject);
                    Rows.Remove(id);
                }
                else if (!shown.Contains(id)) parts.Rect.gameObject.SetActive(false);
            }
            return y + 6f;
        }

        private static float Note(Windows.Window w, string text, float y)
        {
            var t = Windows.Text(w.Content, text, 19f, Windows.Dim, TextAlignmentOptions.Left);
            Windows.Place(t.rectTransform, 14f, y + 6f, w.RowWidth - 28f, 30f);
            Extras.Add(t.gameObject);
            return y + 40f;
        }

        private static float Header(Windows.Window w, string text, float y)
        {
            var t = Windows.Text(w.Content, text, 21f, Windows.Gold, TextAlignmentOptions.Left);
            Windows.Place(t.rectTransform, 14f, y + 4f, w.RowWidth - 28f, HeaderHeight - 4f);
            Extras.Add(t.gameObject);
            return y + HeaderHeight;
        }

        // "Hidden (3)": click to open or close the section.
        private static float HiddenHeader(Windows.Window w, int count, bool open, float y)
        {
            // The game's font has no arrow signs: the heading says it in words. Heading and note both open and close it.
            var t = Windows.Text(w.Content, "Hidden (" + count + ")", 21f, Windows.Gold, TextAlignmentOptions.Left);
            Windows.Place(t.rectTransform, 14f, y + 4f, 150f, HeaderHeight - 4f);
            Clickable(t);
            var note = Windows.Text(w.Content, (open ? "click to close" : "click to open") +
                "  \u00b7  their pins are hidden from your map (Edit, Map pin, shows one again)", 16f, Windows.Dim, TextAlignmentOptions.Left);
            Windows.Place(note.rectTransform, 170f, y + 4f, w.RowWidth - 184f, HeaderHeight - 4f);
            Clickable(note);
            return y + HeaderHeight;
        }

        private static void Clickable(TMP_Text text)
        {
            text.raycastTarget = true;
            var button = text.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(() =>
            {
                _hiddenOpen = !_hiddenOpen;
                FillList();
            });
            Extras.Add(text.gameObject);
        }

        // A portal's row where it now goes: made the first time, after that only moved and brought up to date.
        private static float Place(Windows.Window w, Entry e, Vector3 from, bool linked, bool isHere, float y, int index, HashSet<ZDOID> shown)
        {
            if (!Rows.TryGetValue(e.Id, out var parts) || parts.Rect == null) Rows[e.Id] = parts = MakeRow(w, e.Id, isHere);
            parts.E = e;
            shown.Add(e.Id);
            var width = w.RowWidth;
            Windows.Place(parts.Rect, 0f, y, width, RowHeight);
            if (!parts.Rect.gameObject.activeSelf) parts.Rect.gameObject.SetActive(true);
            var normal = isHere ? new Color(1f, 0.72f, 0.36f, 0.09f) : index % 2 == 0 ? Windows.RowEven : Windows.RowOdd;
            parts.Background.color = normal;
            if (parts.Hover != null) parts.Hover.Normal = normal;
            parts.Stripe.color = e.Color > 0 ? ColorOf(e.Color) : new Color(0f, 0f, 0f, 0f);
            if (parts.Star != null)
            {
                var fav = Favourites.Contains(KeyOf(e));
                parts.Star.text = fav ? "\u2605" : "\u2606";
                parts.Star.color = fav ? Windows.Gold : Windows.Dim;
            }
            parts.Icon.sprite = IconOf(e);
            parts.Icon.color = parts.Icon.sprite != null ? Color.white : new Color(0f, 0f, 0f, 0.3f);
            parts.Kind.sprite = PortalIcon(e.Prefab);
            parts.Kind.color = parts.Kind.sprite != null ? Color.white : new Color(0f, 0f, 0f, 0f);
            parts.Name.text = Display(e.Name);
            parts.Name.color = ColorOf(e.Color);
            parts.Biome.text = Biome(e.Position) + (linked ? "  \u00b7  linked by name" : "") + (PinHidden(e) && Favourites.Contains(KeyOf(e)) ? "  \u00b7  no pin" : "");
            parts.Far.text = isHere ? "you are here" : Distance(Vector3.Distance(from, e.Position));
            var mine = MayShare(e);
            if (parts.Share.gameObject.activeSelf != mine) parts.Share.gameObject.SetActive(mine);
            if (mine)
            {
                Windows.SetLabel(parts.Share, e.Shared ? "Shared" : "Private");
                foreach (var label in parts.Share.GetComponentsInChildren<TMP_Text>(true))
                    label.color = e.Shared ? new Color(0.55f, 0.9f, 0.45f) : Windows.Gold;
            }
            var open = _editing == e.Id;
            Windows.SetLabel(parts.Edit, open ? "Close" : "Edit");
            parts.Edit.interactable = MayEdit(e);
            y += RowHeight;
            return open ? Editor(w, e, parts.Name, y) : y;
        }

        // Opens the editor under this portal's row (or closes it when it is open).
        private static void ToggleEditor(Entry e)
        {
            if (e == null) return;
            SaveTyped();
            _editing = _editing == e.Id ? ZDOID.None : e.Id;
            if (_editing == e.Id)
            {
                _startName = e.Name ?? "";
                _startColor = e.Color;
                _startIcon = e.Icon;
            }
            FillList();
        }

        private static RowParts MakeRow(Windows.Window w, ZDOID id, bool isHere)
        {
            var width = w.RowWidth;
            var parts = new RowParts();
            var row = parts.Rect = Windows.Rect("Portal", w.Content);
            Windows.Place(row, 0f, 0f, width, RowHeight);
            // The row's own colour from the start, and no button colour fade: a button tints from white, which showed
            // every row bright white for a few frames.
            parts.Background = row.gameObject.AddComponent<Image>();
            parts.Background.color = Windows.RowEven;
            var travel = row.gameObject.AddComponent<Button>();
            travel.transition = Selectable.Transition.None;
            travel.targetGraphic = parts.Background;
            if (!isHere)
            {
                travel.onClick.AddListener(() =>
                {
                    if (_light.Value) ToggleEditor(parts.E); // light mode: the list only looks up and edits
                    else Travel(parts.E);
                });
                parts.Hover = row.gameObject.AddComponent<RowHover>();
                parts.Hover.Image = parts.Background;
                parts.Hover.Over = new Color(1f, 0.72f, 0.36f, 0.16f);
            }
            var stripe = Windows.Rect("Colour", row);
            Windows.Place(stripe, 0f, 0f, 5f, RowHeight);
            parts.Stripe = stripe.gameObject.AddComponent<Image>();
            parts.Stripe.raycastTarget = false;
            // the favourite star (also on the portal you stand at: from any other portal it is then at the top)
            {
                parts.Star = Windows.Text(row, "\u2606", 28f, Windows.Dim, TextAlignmentOptions.Center);
                Windows.Place(parts.Star.rectTransform, 10f, 0f, 40f, RowHeight);
                parts.Star.raycastTarget = true;
                var starButton = parts.Star.gameObject.AddComponent<Button>();
                starButton.transition = Selectable.Transition.None;
                starButton.onClick.AddListener(() =>
                {
                    var place = KeyOf(parts.E);
                    if (!Favourites.Remove(place)) Favourites.Add(place);
                    SaveMine();
                    FillList();
                });
            }
            parts.Icon = Windows.Picture(row, null, 56f, 7f, 36f, 36f);

            parts.Name = Windows.Text(row, "", 23f, Windows.Body, TextAlignmentOptions.Left);
            Windows.Place(parts.Name.rectTransform, 104f, 0f, 276f, RowHeight);
            parts.Biome = Windows.Text(row, "", 18f, Windows.Dim, TextAlignmentOptions.Left);
            Windows.Place(parts.Biome.rectTransform, 388f, 0f, 140f, RowHeight);
            // the portal's own kind (stone, wood, ...) in a small column of its own, on every row
            parts.Kind = Windows.Picture(row, null, 534f, 11f, 28f, 28f);
            parts.Far = Windows.Text(row, "", isHere ? 17f : 18f, isHere ? Windows.Gold : Windows.Dim, TextAlignmentOptions.Right);
            parts.Far.overflowMode = TextOverflowModes.Overflow;
            Windows.Place(parts.Far.rectTransform, width - 290f, 0f, 116f, RowHeight);
            // your own portals: Private (only you see it) or Shared (everyone sees it and can go there)
            parts.Share = Windows.Button(row, "Private", width - 168f, 8f, 78f, RowHeight - 16f, () =>
            {
                Change(parts.E, EditShared, null, parts.E.Shared ? 0 : 1);
                FillList();
            });
            Instant(parts.Share);
            parts.Edit = Windows.Button(row, "Edit", width - 86f, 8f, 80f, RowHeight - 16f, () => ToggleEditor(parts.E));
            Instant(parts.Edit);
            return parts;
        }

        // The editor under a row: its name, colour and icon. Every change is kept at once.
        private static float Editor(Windows.Window w, Entry e, TextMeshProUGUI rowName, float y)
        {
            var width = w.RowWidth;
            var panel = Windows.Rect("Editor", w.Content);
            Extras.Add(panel.gameObject);
            panel.gameObject.AddComponent<Image>().color = new Color(1f, 0.72f, 0.36f, 0.06f);
            var top = 10f;
            Windows.Place(Windows.Text(panel, "Name", 19f, Windows.Gold, TextAlignmentOptions.Left).rectTransform, 14f, top, 84f, 38f);
            var box = Windows.SearchBox(panel, 100f, top, 440f, 38f, "Name this portal...");
            Instant(box);
            box.characterLimit = NameLimit;
            box.SetTextWithoutNotify(e.Name ?? "");
            box.onSelect.AddListener(_ => { if (_window != null) _window.Search = box; });
            // Saved as you type, half a second after the last key (and at once when the box is left); the row's name
            // changes along in place (a redraw here would swallow the click that left the box, e.g. on a colour).
            box.onValueChanged.AddListener(text =>
            {
                _typing = e;
                _typed = text ?? "";
                _typedAt = Time.unscaledTime;
                if (rowName != null) rowName.text = Display(_typed.Trim());
                var hereNow = SourceEntry();
                if (hereNow != null && hereNow.Id == e.Id) UpdateSubtitle(_typed.Trim());
            });
            box.onEndEdit.AddListener(_ =>
            {
                SaveTyped();
                if (_window != null && _window.Search == box) _window.Search = _searchBox;
            });
            var reset = Windows.Button(panel, "Reset", 550f, top, 96f, 38f, () =>
            {
                _typing = null;
                if ((e.Name ?? "") != _startName) Change(e, EditName, _startName, 0);
                if (e.Color != _startColor) Change(e, EditColor, null, _startColor);
                if (e.Icon != _startIcon) Change(e, EditIcon, null, _startIcon);
                FillList();
            });
            Instant(reset);
            Windows.Place(Windows.Text(panel, "Saved as you type.", 16f, Windows.Dim,
                TextAlignmentOptions.Left).rectTransform, 658f, top, width - 672f, 38f);
            top += 50f;

            Windows.Place(Windows.Text(panel, "Colour", 19f, Windows.Gold, TextAlignmentOptions.Left).rectTransform, 14f, top, 84f, Cell);
            var perLine = Mathf.Max(1, Mathf.FloorToInt((width - 100f - 14f) / (Cell + 6f)));
            var n = 0;
            foreach (var i in ColorOrder)
            {
                if (i >= Colors.Length) continue;
                var value = i;
                var swatch = Windows.Rect("Colour " + i, panel);
                Windows.Place(swatch, 100f + n % perLine * (Cell + 6f), top + n / perLine * (Cell + 6f), Cell, Cell);
                n++;
                swatch.gameObject.AddComponent<Image>().color = i == 0 ? new Color(0f, 0f, 0f, 0.35f) : Colors[i];
                var button = swatch.gameObject.AddComponent<Button>();
                button.transition = Selectable.Transition.None;
                button.onClick.AddListener(() =>
                {
                    Change(e, EditColor, null, value);
                    FillList();
                });
                if (i == 0) Windows.Text(swatch, "none", 14f, Windows.Body, TextAlignmentOptions.Center).rectTransform.sizeDelta = new Vector2(Cell, Cell);
                if (i == e.Color) Outline(swatch);
            }
            top += Mathf.Ceil(n / (float)perLine) * (Cell + 6f) + 6f;

            Windows.Place(Windows.Text(panel, "Icon", 19f, Windows.Gold, TextAlignmentOptions.Left).rectTransform, 14f, top, 84f, Cell);
            var perRow = Mathf.Max(1, Mathf.FloorToInt((width - 100f - 14f) / (Cell + 6f)));
            var count = 0;
            foreach (var i in IconOrder())
            {
                if (!Offered(i, e.Icon)) continue;
                var sprite = i == 0 ? PortalIcon(e.Prefab) : IconSprite(IconItems[i]);
                if (i > 0 && sprite == null) continue;
                var value = i;
                var cell = Windows.Rect("Icon " + i, panel);
                Windows.Place(cell, 100f + count % perRow * (Cell + 6f), top + count / perRow * (Cell + 6f), Cell, Cell);
                cell.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.35f);
                var button = cell.gameObject.AddComponent<Button>();
                button.transition = Selectable.Transition.None;
                button.onClick.AddListener(() =>
                {
                    Change(e, EditIcon, null, value);
                    FillList();
                });
                Windows.Picture(cell, sprite, 5f, 5f, Cell - 10f, Cell - 10f);
                if (i == e.Icon) Outline(cell);
                count++;
            }
            top += Mathf.Ceil(count / (float)perRow) * (Cell + 6f) + 6f;

            // Auto Map Pins' pin of this portal, on your map only.
            var pin = PinStates.TryGetValue(e.Id, out var state) ? state : PinState(e);
            if (pin >= 0)
            {
                Windows.Place(Windows.Text(panel, "Map pin", 19f, Windows.Gold, TextAlignmentOptions.Left).rectTransform, 14f, top, 84f, 38f);
                var toggle = Windows.Button(panel, pin == 1 ? "Hidden" : "Shown", 100f, top, 110f, 38f, () =>
                {
                    SetPinHidden(e, pin != 1);
                    FillList();
                });
                Instant(toggle);
                foreach (var label in toggle.GetComponentsInChildren<TMP_Text>(true))
                    label.color = pin == 1 ? Windows.Dim : new Color(0.55f, 0.9f, 0.45f);
                Windows.Place(Windows.Text(panel, pin == 1
                        ? "Not on your map (Auto Map Pins). Click to show its pin again."
                        : "On your map (Auto Map Pins). Hidden, it goes to the Hidden section at the bottom of the list.",
                    16f, Windows.Dim, TextAlignmentOptions.Left).rectTransform, 222f, top, width - 236f, 38f);
                top += 50f;
            }
            Windows.Place(panel, 0f, y, width, top);
            return y + top + 4f;
        }

        // A row lights up under the mouse (set straight, no fade).
        private sealed class RowHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
        {
            public Image Image;
            public Color Normal, Over;

            public void OnPointerEnter(PointerEventData data)
            {
                if (Image != null) Image.color = Over;
            }

            public void OnPointerExit(PointerEventData data)
            {
                if (Image != null) Image.color = Normal;
            }
        }

        private static void Outline(RectTransform rect)
        {
            var outline = rect.gameObject.AddComponent<Outline>();
            outline.effectColor = Windows.Gold;
            outline.effectDistance = new Vector2(3f, -3f);
        }

        // --- Ctrl+E (the back key): back to the portal you used last ---------------------------------------------------

        private static TeleportWorld _goingBack; // waiting for the server's list (a client may not know a far portal)

        // The place of the portal you stand at (its ZDO's: the same as in the list).
        private static string Here(TeleportWorld portal)
        {
            var zdo = portal != null && portal.m_nview != null && portal.m_nview.IsValid() ? portal.m_nview.GetZDO() : null;
            return zdo != null ? PlaceKey(zdo.GetPosition()) : null;
        }

        // Your newest trip other than this portal (a trip marks both ends, so after A -> B this is A, seen from B).
        private static string NewestTrip(string here)
        {
            string best = null;
            var when = long.MinValue;
            foreach (var t in Trips)
                if (t.Key != here && t.Value > when)
                {
                    best = t.Key;
                    when = t.Value;
                }
            return best;
        }

        // The newest trip's portal among these that still stands and that you may go to.
        private static Entry LastUsed(List<Entry> entries, string here)
        {
            var me = Me;
            Entry best = null;
            var when = long.MinValue;
            foreach (var e in entries)
                if (KeyOf(e) != here && Visible(e, me) && Trips.TryGetValue(KeyOf(e), out var t) && t > when)
                {
                    best = e;
                    when = t;
                }
            return best;
        }

        // For the hover text: the name of the portal the back key goes to, or null when there is none.
        private static string BackName(TeleportWorld portal)
        {
            LoadMine();
            var place = NewestTrip(Here(portal));
            if (place == null) return null;
            var known = _entries.FirstOrDefault(e => KeyOf(e) == place);
            if (known != null) return Display(known.Name);
            if (ZDOMan.instance != null) // the game's own list of portals (no search of the scene: this runs while hovering)
                foreach (var zdo in ZDOMan.instance.GetPortalList())
                    if (zdo != null && PlaceKey(zdo.GetPosition()) == place) return Display(zdo.GetString(ZDOVars.s_tag));
            return "the last portal";
        }

        private static void GoBack(TeleportWorld source)
        {
            var player = Player.m_localPlayer;
            if (player == null || source == null || player.IsDead() || player.IsTeleporting() || InventoryGui.IsVisible() || Minimap.IsOpen()) return;
            LoadMine();
            var here = Here(source);
            var newest = NewestTrip(here);
            if (newest == null)
            {
                player.Message(MessageHud.MessageType.Center, "No portal used yet: E opens the list");
                return;
            }
            _source = source;
            var known = Known();
            var server = ZNet.instance == null || ZNet.instance.IsServer() || ZRoutedRpc.instance == null;
            // This game knows that portal (or knows every portal): go at once. A client asks the server first, since the
            // portal may be far away (or gone, or made private).
            if (server || known.Any(e => KeyOf(e) == newest && Visible(e, Me)))
            {
                FinishGoBack(known);
                return;
            }
            _goingBack = source;
            _asking = true;
            _askedAt = Time.unscaledTime;
            var ask = new ZPackage();
            ask.Write(Me);
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), AskRpc, ask);
        }

        private static void FinishGoBack(List<Entry> entries)
        {
            var source = _goingBack ?? _source;
            _goingBack = null;
            var player = Player.m_localPlayer;
            if (player == null || source == null || player.IsTeleporting() || Vector3.Distance(player.transform.position, source.transform.position) > 10f) return;
            _source = source;
            var target = LastUsed(entries, Here(source));
            if (target != null) Travel(target);
            else player.Message(MessageHud.MessageType.Center, "That portal is gone: E opens the list");
        }

        // --- going there --------------------------------------------------------------------------------------------

        private static void Travel(Entry e)
        {
            var player = Player.m_localPlayer;
            if (player == null || e == null) return;
            if (ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoPortals))
            {
                player.Message(MessageHud.MessageType.Center, "$msg_blocked");
                return;
            }
            if (ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoBossPortals) && (RandEventSystem.instance.GetBossEvent() != null ||
                (ZoneSystem.instance.GetGlobalKey(GlobalKeys.activeBosses, out float bosses) && bosses > 0f)))
            {
                player.Message(MessageHud.MessageType.Center, "$msg_blockedbyboss");
                return;
            }
            var allowAll = _source != null && _source.m_allowAllItems;
            if (!player.IsTeleportable(allowAll))
            {
                player.Message(MessageHud.MessageType.Center, "$msg_noteleport");
                return;
            }
            SaveTyped();
            var exit = _source != null ? _source.m_exitDistance : 1f;
            var forward = e.Rotation * Vector3.forward;
            var here = SourceEntry();
            Windows.Close();
            Trips[KeyOf(e)] = DateTime.UtcNow.Ticks;
            if (here != null) Trips[KeyOf(here)] = DateTime.UtcNow.Ticks - 1;
            SaveMine();
            player.TeleportTo(e.Position + forward * exit + Vector3.up, e.Rotation, true);
            Game.instance.IncrementPlayerStat(PlayerStatType.PortalsUsed);
        }
    }
}
