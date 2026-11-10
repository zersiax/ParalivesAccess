using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>Reads the present lot and rooms without changing selection, camera or construction.</summary>
    internal static class LotLayoutReader
    {
        internal static string Describe(HybridPlayer hybrid, bool wholeLot)
        {
            var player = hybrid?.Player;
            if (player == null) return Loc.Get("layout_unavailable");
            ulong lot = player.State == GameStates.LiveMode ? LotObjectHandler.ResolveCurrentLot(player, out _)
                : ItemPlacementHandler.ResolvePlacementLot(player, out _);
            var zones = ZoneManager.Instance;
            var perimeter = zones?.GetLotPerimeterZoneObjectByGUID(lot);
            var data = LotManager.Instance?.GetLotByGUID(lot)?.GetGridData();
            if (lot == 0 || data == null || perimeter == null
                || !PlacementGridReader.TryRead(lot, data.CellDimension, out var grid)
                || !SpatialOutline.TryRead(perimeter, grid, out var outline)) return Loc.Get("layout_unavailable");
            if (!BuildModeRefreshManager.IsZoneStateRefreshed || zones.DirtyWallZones.Count != 0)
                return Loc.Get("layout_updating");

            int layer = player.GetLotLayer(lot);
            var currentRooms = RoomSpatialReader.OrderedRooms(lot, layer);
            var focus = hybrid.HybridCamera?.FreeCamera?.LookTarget;
            int roomIndex = focus != null && ZoneGeometry.Contains(perimeter, focus.position)
                ? currentRooms.FindIndex(r => ZoneGeometry.Contains(r, focus.position)) : -1;
            var report = new List<string>();
            if (!wholeLot && roomIndex >= 0)
            {
                var room = currentRooms[roomIndex];
                report.Add(Loc.Get("layout_room_focus", RoomLabelHandler.Name(room, roomIndex + 1), layer));
                report.Add(DescribeRoom(room, roomIndex, currentRooms, grid, outline));
                double angle = grid.Rotation * Math.PI / 180;
                var frame = new WallFaceFrame { Origin = Vector3.zero,
                    Right = new Vector3((float)Math.Cos(angle), 0, (float)Math.Sin(angle)),
                    Normal = new Vector3((float)-Math.Sin(angle), 0, (float)Math.Cos(angle)) };
                report.Add(RoomSpatialReader.DescribeContents(room, frame, true));
                return string.Join(". ", report.Where(s => !string.IsNullOrWhiteSpace(s)));
            }

            report.Add(Loc.Get("layout_lot", outline.Shape(), outline.Size()));
            grid.World((outline.Left + outline.Right) / 2 / grid.Spacing, (outline.Front + outline.Back) / 2 / grid.Spacing,
                out double centreX, out double centreZ);
            if (!ZoneGeometry.Contains(perimeter, new Vector3((float)centreX, 0, (float)centreZ)))
                report.Add(Loc.Get("layout_centre_outside"));
            var allRooms = zones.GetLotZones(lot).OfType<ZoneWallObject>()
                .Where(r => RoomSpatialReader.IsUsableRoom(r, lot)).ToList();
            int objects = ItemManager.Instance.GetLotItems(lot).Count(i => i != null && i.isActiveAndEnabled
                && i.LotPlacedOnGUID == lot && player.ItemInPlacement?.Item != i);
            if (allRooms.Count == 0) report.Add(Loc.Get("layout_no_rooms", objects));
            else
            {
                report.Add(Loc.Get("layout_room_count", allRooms.Count));
                foreach (int floor in allRooms.Select(r => r.Layer).Distinct().OrderBy(n => n))
                {
                    report.Add(Loc.Get("layout_floor", floor));
                    var rooms = RoomSpatialReader.OrderedRooms(lot, floor);
                    for (int i = 0; i < rooms.Count; i++) report.Add(DescribeRoom(rooms[i], i, rooms, grid, outline));
                }
            }
            var enclosed = new HashSet<SegmentObject>(allRooms.SelectMany(r => r.Segments).Select(e => e.Segment));
            int unfinished = SegmentManager.Instance.GetLotSegments(lot).OfType<SegmentWallObject>().Count(w =>
                w != null && w.gameObject.activeInHierarchy && w.LotPlacedOnGUID == lot && w.PlacementPlayer == -1
                && w.ReplacingSegment == null && !enclosed.Contains(w) && w.WallSections.Any(IsVisibleWall));
            if (unfinished > 0) report.Add(Loc.Get("layout_unenclosed_walls", unfinished));
            return string.Join(". ", report);
        }

        private static bool IsVisibleWall(SegmentWallSectionObject section) => section != null
            && section.gameObject.activeInHierarchy && !section.IsFence && !section.DisabledByUser && !section.DisabledButVisible;

        internal static string DescribeRoom(ZoneWallObject room, int index, List<ZoneWallObject> rooms,
            PlacementGrid grid, SpatialOutline lotOutline)
        {
            string name = RoomLabelHandler.Name(room, index + 1);
            if (!SpatialOutline.TryRead(room, grid, out var outline)) return Loc.Get("layout_room_unavailable", name);
            var middle = new SpatialOutline.Point((outline.Left + outline.Right) / 2, (outline.Front + outline.Back) / 2);
            var parts = new List<string> { Loc.Get("layout_room", name, lotOutline.Region(middle), outline.Shape(), outline.Size()) };
            for (int i = 0; i < rooms.Count; i++)
            {
                var other = rooms[i];
                if (other == room || other.Layer != room.Layer) continue;
                var relations = new HashSet<string>();
                foreach (var edge in room.Segments)
                {
                    // Shared identity proves a boundary. Coincident endpoints/proximity do not.
                    if (!(edge.Segment is SegmentWallObject wall) || wall.PlacementPlayer != -1
                        || !wall.gameObject.activeInHierarchy || wall.ReplacingSegment != null
                        || !SegmentManager.Instance.GetLotSegments(room.LotPlacedOnGUID).Contains(wall)
                        || !other.Segments.Any(e => e.Segment == wall && e.FromAToB != edge.FromAToB)) continue;
                    relations.Add(wall.WallSections.Any(IsVisibleWall)
                        ? Loc.Get("layout_shared_wall", outline.Side(edge), RoomLabelHandler.Name(other, i + 1))
                        : Loc.Get("layout_shared_boundary", RoomLabelHandler.Name(other, i + 1)));
                }
                parts.AddRange(relations.OrderBy(s => s, StringComparer.Ordinal));
            }
            if (parts.Count == 1) parts.Add(Loc.Get("layout_room_detached"));
            return string.Join(". ", parts);
        }
    }
}
