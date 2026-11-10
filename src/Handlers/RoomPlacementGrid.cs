using System;
using System.Linq;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>A deliberately selected room frame; stale selections never fall back to the lot grid.</summary>
    internal static class RoomPlacementGrid
    {
        internal static ZoneWallObject Room { get; private set; }
        internal static bool Selected { get; private set; }
        private static WallFaceFrame _reference;
        private static Vector3[] _boundary;

        internal static void UseLot() { Room = null; Selected = false; _reference = null; _boundary = null; }

        internal static string Select(ZoneWallObject room)
        {
            if (!RoomSpatialReader.TryReference(room, out var frame)) return Loc.Get("room_reference_required");
            var points = Boundary(room);
            if (points == null) return Loc.Get("room_grid_changed");
            Room = room; Selected = true; _reference = frame; _boundary = points;
            return Loc.Get("room_grid_selected");
        }

        private static Vector3[] Boundary(ZoneWallObject room)
        {
            if (room?.Segments == null || room.Segments.Any(e => e.Segment?.SegmentPoints == null)) return null;
            var points = room.Segments.SelectMany(e => e.Segment.SegmentPoints).ToArray();
            return points.Length >= 3 && points.All(WallFaceFrame.Finite) ? points : null;
        }

        internal static bool TryRead(ulong lot, int layer, float spacing, out PlacementGrid grid)
        {
            grid = null;
            if (!PlacementGrid.Finite(spacing) || spacing <= 0) return false;
            if (!Selected) return PlacementGridReader.TryRead(lot, spacing, out grid);
            if (Room == null || Room.LotPlacedOnGUID != lot || Room.Layer != layer
                || !RoomSpatialReader.TryReference(Room, out var frame) || !_reference.Same(frame)) return false;
            var points = Boundary(Room);
            if (points == null || points.Length != _boundary.Length
                || points.Where((p, i) => Vector3.Distance(p, _boundary[i]) > 0.002f).Any()) return false;
            // Grid columns follow the chosen face Right; rows follow its inward normal. Origin
            // stays at the explicitly selected section endpoint, including on angled rooms.
            grid = new PlacementGrid(frame.Origin.x, frame.Origin.z,
                Math.Atan2(frame.Right.z, frame.Right.x) * 180 / Math.PI, spacing);
            return true;
        }

        internal static string Problem(ulong lot, int layer, Vector3 point)
        {
            if (!Selected) return null;
            if (!TryRead(lot, layer, ItemPlacementHandler.GridSpacingMetres, out _)) return Loc.Get("room_grid_changed");
            return WallFaceFrame.Finite(point) && ZoneGeometry.Contains(Room, point)
                ? null : Loc.Get("room_grid_outside");
        }

        internal static string Describe(ItemObjectRoot item)
        {
            if (!Selected) return Loc.Get("room_grid_lot");
            if (item == null || !TryRead(item.LotPlacedOnGUID, item.Layer, ItemPlacementHandler.GridSpacingMetres, out var grid))
                return Loc.Get("room_grid_changed");
            string membership = Problem(item.LotPlacedOnGUID, item.Layer, item.CubeTransform.LocalPosition) ?? Loc.Get("room_grid_inside");
            var corners = ItemFootprintReader.Corners(item);
            if (corners.Any(p => !ZoneGeometry.Contains(Room, p))) membership += ". " + Loc.Get("room_grid_bounds_cross");
            return Loc.Get("room_grid_report", PlacementGridReader.Position(grid, item.CubeTransform.LocalPosition), membership)
                + ". " + ItemFootprintReader.Describe(item, grid);
        }
    }
}
