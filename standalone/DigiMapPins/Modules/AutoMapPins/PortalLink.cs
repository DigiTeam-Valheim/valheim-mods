// Auto Map Pins and portals: a portal pin shows the portal's own icon (wood or stone portal). With Portal List (our own
// portal mod) installed, the name under the pin is in the colour picked for that portal there, and a portal Portal List
// keeps private (built by someone else and not shared) gets no pin, as it is not in your list either. Portal List keeps
// both on the portal ("digi_portal_color" as a number into its own colours, read here once by reflection;
// "digi_portal_shared").
using System;
using HarmonyLib;
using UnityEngine;

namespace AutoMapPins
{
    internal static class PortalLink
    {
        private const string ColorKey = "digi_portal_color", SharedKey = "digi_portal_shared";
        private static bool _tried, _installed;
        private static Color[] _colors;

        private static bool Installed()
        {
            if (_tried) return _installed;
            _tried = true;
            try
            {
                _installed = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("digi.valheim.portallist");
                if (!_installed) return false;
                var type = AccessTools.TypeByName("PortalList.Plugin");
                _colors = type != null ? AccessTools.Field(type, "Colors")?.GetValue(null) as Color[] : null;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("Could not read Portal List's colours: " + e.Message);
            }
            return _installed;
        }

        // The portal's look and privacy now: its prefab, Portal List's colour (-1: none) and whether Portal List keeps it
        // private from you. True when any of it changed.
        internal static bool Update(Record record)
        {
            var zdo = record.Members.Count > 0 && ZDOMan.instance != null ? ZDOMan.instance.GetZDO(record.Members[0]) : null;
            if (zdo == null) return false;
            var prefab = zdo.GetPrefab();
            var linked = Installed();
            var color = linked ? zdo.GetInt(ColorKey) : -1;
            var creator = zdo.GetLong(ZDOVars.s_creator);
            var me = Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerID() : 0L;
            var hidden = linked && creator != 0L && me != 0L && creator != me && zdo.GetInt(SharedKey) == 0;
            if (prefab == record.PortalPrefab && color == record.PortalColorIndex && hidden == record.PortalPrivate) return false;
            record.PortalPrefab = prefab;
            record.PortalColorIndex = color;
            record.PortalPrivate = hidden;
            return true;
        }

        // The colour picked for the portal in Portal List, or null.
        internal static Color? ColorOf(Record record)
        {
            var index = record.PortalColorIndex;
            if (index <= 0 || !Installed() || _colors == null || index >= _colors.Length) return null;
            return _colors[index];
        }

        // The portal's own icon (the piece: wood portal, stone portal, ...).
        internal static Sprite OwnIcon(Record record)
        {
            var prefab = record.PortalPrefab != 0 && ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(record.PortalPrefab) : null;
            var piece = prefab != null ? prefab.GetComponent<Piece>() : null;
            return piece != null ? piece.m_icon : null;
        }
    }

    // For Portal List ("Map pin" in its editor and its Hidden section), found there by reflection so that neither mod
    // needs the other. A portal's pin is hidden when you deleted it on the map (right-click) or switched it off in Portal
    // List; it stays hidden, per world and character, until shown again there (or "Restore deleted pins").
    public static class PortalPins
    {
        // 1 = hidden, 0 = not hidden (pinned, or pinned once the portal loads or is on your explored map), -1 = not known
        // (no world loaded, or Auto Map Pins switched off).
        public static int Hidden(Vector3 pos)
        {
            if (Records.Key == null) return -1;
            var record = Records.Closest(pos, 3f, Catalog.PortalId);
            return record != null && record.Deleted ? 1 : 0;
        }

        // Hides or shows the pin of the portal at pos; false when that cannot be done now (see Hidden).
        public static bool SetHidden(Vector3 pos, bool hidden)
        {
            if (Records.Key == null) return false;
            var record = Records.Closest(pos, 3f, Catalog.PortalId);
            if (record == null)
            {
                if (!hidden) return true;
                // Not pinned yet (far away, not on your explored map): the pin is made hidden, and the portal finds it
                // by its place once it loads.
                var portal = ZDOMan.instance?.GetPortalList().Find(z => z != null && Utils.DistanceXZ(z.GetPosition(), pos) < 1f);
                record = new Record
                {
                    Thing = Catalog.PortalId,
                    Biome = WorldGenerator.instance != null ? WorldGenerator.instance.GetBiome(pos) : Heightmap.Biome.Meadows,
                    Pos = portal != null ? portal.GetPosition() : pos,
                    Label = portal != null ? portal.GetString(ZDOVars.s_tag) : null,
                    Deleted = true,
                };
                if (portal != null) record.Members.Add(portal.m_uid);
                Records.Add(record);
            }
            else if (record.Deleted == hidden) return true;
            else record.Deleted = hidden;
            Records.Dirty = true;
            MapPins.Refresh(record);
            return true;
        }
    }
}
