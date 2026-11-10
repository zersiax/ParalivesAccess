using System;
using System.Linq;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>Short room/lot location for the actual preview pivot, using the same names and axes as J.</summary>
    internal static class PlacementLocationReader
    {
        internal static string Describe(ItemObjectRoot item)
        {
            if (item?.CubeTransform == null) return null;
            ulong lot = item.LotPlacedOnGUID;
            Vector3 point = item.CubeTransform.LocalPosition;
            return Describe(lot, item.Layer, point);
        }

        internal static string Target(ItemObjectRoot target, ItemObjectRoot from = null)
        {
            if (target?.CubeTransform == null) return Loc.Get("build_location_unavailable");
            Vector3 point = target.CubeTransform.LocalPosition;
            string report = Loc.Get("target_location", Describe(target), target.Layer);
            if (!PlacementGridReader.TryRead(target.LotPlacedOnGUID, ItemPlacementHandler.GridSpacingMetres, out var grid)) return report;
            report += ". " + PlacementGridReader.Position(grid, point);
            if (from?.CubeTransform == null || from.LotPlacedOnGUID != target.LotPlacedOnGUID || from.Layer != target.Layer) return report;
            grid.Coordinates(point.x, point.z, out double x, out double z);
            grid.Coordinates(from.CubeTransform.LocalPosition.x, from.CubeTransform.LocalPosition.z, out double a, out double b);
            double dx = (x - a) * grid.Spacing, dz = (z - b) * grid.Spacing;
            string horizontal = Math.Abs(dx) < .05 ? "" : Loc.Get(dx > 0 ? "target_right" : "target_left", PlacementGridReader.Length((float)Math.Abs(dx)));
            string depth = Math.Abs(dz) < .05 ? "" : Loc.Get(dz > 0 ? "target_back" : "target_front", PlacementGridReader.Length((float)Math.Abs(dz)));
            return report + ". " + (horizontal.Length + depth.Length == 0 ? Loc.Get("target_same_position")
                : Loc.Get("target_relative", string.Join(", ", new[] { horizontal, depth }.Where(s => s.Length > 0))));
        }

        internal static string Describe(ulong lot, int layer, Vector3 point)
            => Describe(lot, layer, point, false);

        internal static string CompactTarget(ItemObjectRoot item)
        {
            if (item?.CubeTransform == null) return Loc.Get("build_location_unavailable");
            string result = Loc.Get("target_location", Describe(item.LotPlacedOnGUID, item.Layer, item.CubeTransform.LocalPosition, true), item.Layer);
            return PlacementGridReader.TryRead(item.LotPlacedOnGUID, ItemPlacementHandler.GridSpacingMetres, out var grid)
                ? result + ". " + PlacementGridReader.Position(grid, item.CubeTransform.LocalPosition) : result;
        }

        internal static string AreaName(ulong lot, int layer, Vector3 point)
        {
            var room = RoomSpatialReader.OrderedRooms(lot, layer).FirstOrDefault(r => ZoneGeometry.Contains(r, point));
            return room == null ? Loc.Get("explorer_outdoors") : RoomLabelHandler.Name(room);
        }

        internal static string Brief(ulong lot, int layer, Vector3 point) => Describe(lot, layer, point, true);

        private static string Describe(ulong lot, int layer, Vector3 point, bool brief)
        {
            if (!BuildModeRefreshManager.IsZoneStateRefreshed || ZoneManager.Instance?.DirtyWallZones.Count > 0)
                return Loc.Get("layout_updating");
            var perimeter = ZoneManager.Instance?.GetLotPerimeterZoneObjectByGUID(lot);
            if (perimeter == null || !PlacementGridReader.TryRead(lot, 1, out var grid)) return null;
            if (!ZoneGeometry.Contains(perimeter, point)) return Loc.Get("build_outside_lot");
            var rooms = RoomSpatialReader.OrderedRooms(lot, layer);
            for (int i = 0; i < rooms.Count; i++)
            {
                if (!ZoneGeometry.Contains(rooms[i], point)) continue;
                string name = RoomLabelHandler.Name(rooms[i], i + 1);
                if (!SpatialOutline.TryRead(rooms[i], grid, out var roomOutline)) return name;
                grid.Coordinates(point.x, point.z, out double x, out double z);
                string region = roomOutline.Region(new SpatialOutline.Point(x * grid.Spacing, z * grid.Spacing));
                if (brief && region == Loc.Get("layout_region", Loc.Get("room_middle"), Loc.Get("room_centre")))
                    region = Loc.Get("room_centre");
                return Loc.Get(brief ? "placement_room_location_brief" : "placement_room_location", name, region);
            }
            return SpatialOutline.TryRead(perimeter, grid, out var outline)
                ? Loc.Get("placement_outdoor_location", outline.DescribePosition(grid, point))
                : Loc.Get("build_camera_outdoors");
        }
    }
}
