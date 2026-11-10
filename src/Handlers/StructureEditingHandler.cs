using System;
using System.Collections.Generic;
using System.Linq;
using ParalivesAccess.Speech;
using Setting;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>Reviewed native wall and whole-room platform operations exposed by the lot explorer.</summary>
    internal static class StructureEditingHandler
    {
        internal sealed class Review
        {
            internal string Description;
            internal Player Player;
            internal ulong Lot;
            internal int Layer;
            internal object[] Snapshot;
            internal Func<string> Execute;
            private bool _used;

            internal string Apply()
            {
                if (_used) return Loc.Get("structure_changed");
                _used = true;
                if (!Context(Player, Lot, Layer) || !Snapshot.SequenceEqual(Capture(Lot, Layer))) return Loc.Get("structure_changed");
                try
                {
                    string result = Execute();
                    Plugin.Log.LogInfo($"[Structure] lot={Lot}, layer={Layer}: {Description}; result={result}");
                    return result;
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError("Native structure operation failed; no automatic retry: " + ex);
                    return Loc.Get("structure_unverified");
                }
            }
        }

        internal static bool Context(Player player, ulong lot, int layer)
        {
            var h = PlayerManager.Instance?.HybridPlayer1;
            return player != null && h?.Player == player && player.State == GameStates.BuildMode
                && player.PlayerActionsAreAllowed && !player.PlayerActionBlockedByUIWindow && !h.HasInputFieldFocused && !h.IsUsingGamePad
                && !h.ButtonConfirm.Down && !h.ButtonConfirm.Up && !h.ButtonConfirm.Pressed
                && !h.ButtonCancel.Down && !h.ButtonUndo.Down && !h.ButtonRedo.Down
                && !InputManager.ControlOrCommand && !InputManager.Alt && !InputManager.Shift
                && player.ItemInPlacement == null && player.ItemStartingPlacement == null && player.ItemInRotation == null
                && player.ItemInResize == null && player.ItemInScale == null && player.LotInMovement == 0
                && player.SegmentInPlacement == null && player.PlatformInPlacement == null
                && !NativeGesture(player)
                && !player.StartNewSegment && !player.StartNewSegmentNextFrame
                && HouseholdManager.Instance.OwnsLot(lot) && ItemPlacementHandler.ResolvePlacementLot(player, out _) == lot
                && player.GetLotLayer(lot) == layer && BuildModeRefreshManager.IsZoneStateRefreshed
                && ZoneManager.Instance.DirtyWallZones.Count == 0;
        }

        private static bool NativeGesture(Player p)
        {
            var widgets = Settings.Get<Widgets>();
            return WidgetManager.Instance?.GetWidget(p.PlayerIndex, widgets.WidgetSegmentMovePreview.GUID) != null
                || WidgetManager.Instance?.GetWidget(p.PlayerIndex, widgets.WidgetSegmentRaisePreview.GUID) != null
                || WidgetManager.Instance?.GetWidget(p.PlayerIndex, widgets.WidgetPlatformPreview.GUID) != null;
        }

        internal static List<SegmentWallObject> Walls(ulong lot, int layer, ZoneWallObject room = null) =>
            SegmentManager.Instance.GetLotSegments(lot).OfType<SegmentWallObject>()
                .Where(w => WallFaceReader.Usable(w, lot, layer) && w.NodeA != null && w.NodeB != null
                    && (room == null || room.Segments.Any(e => e.Segment == w))).OrderBy(w => w.InstanceID).ToList();

        internal static List<ZoneWallPlatformObject> Floors(ulong lot, int layer, ZoneWallObject room = null) =>
            RoomSpatialReader.OrderedRooms(lot, layer).Where(r => room == null || r == room)
                .SelectMany(r => r.Platforms).Where(p => p != null && !p.IsCeiling
                    && (p.gameObject.activeInHierarchy || p.DisabledByUser) && p.WallZone != null)
                .OrderBy(p => RoomSpatialReader.OrderedRooms(lot, layer).IndexOf(p.WallZone))
                .ThenBy(p => p.Height).ThenBy(p => p.InstanceID).ToList();

        internal static string DescribeWall(SegmentWallObject wall, int ordinal)
        {
            if (wall == null || !Walls(wall.LotPlacedOnGUID, wall.Layer).Contains(wall)) return Loc.Get("structure_changed");
            string a = Loc.Get("wall_grid_unavailable"), b = a;
            if (ConstructionGrid(wall.LotPlacedOnGUID, out var grid))
            { a = PlacementGridReader.Position(grid, wall.NodeA.Position); b = PlacementGridReader.Position(grid, wall.NodeB.Position); }
            var rooms = RoomSpatialReader.OrderedRooms(wall.LotPlacedOnGUID, wall.Layer);
            var names = rooms.Select((r, i) => new { r, i }).Where(x => x.r.Segments.Any(e => e.Segment == wall))
                .Select(x =>
                {
                    var face = WallFaceReader.Read(wall.LotPlacedOnGUID, wall.Layer).FirstOrDefault(g => g.Room == x.r)
                        ?.Faces.FirstOrDefault(f => f.WallId == wall.InstanceID);
                    return face != null && face.TryRead(wall.LotPlacedOnGUID, wall.Layer, out var frame)
                        ? Loc.Get("wall_side_room", RoomSpatialReader.LotSide(wall.LotPlacedOnGUID, frame), RoomLabelHandler.Name(x.r, x.i + 1))
                        : RoomLabelHandler.Name(x.r, x.i + 1);
                }).ToArray();
            return Loc.Get("structure_wall", ordinal, a, b, names.Length == 0 ? Loc.Get("structure_no_adjacent_rooms") : string.Join(", ", names),
                wall.WallSections.Sum(s => s.ItemsOnThis.Count(i => i != null && i.isActiveAndEnabled)));
        }

        internal static bool ConstructionGrid(ulong lot, out PlacementGrid grid)
        {
            grid = null;
            var data = LotManager.Instance?.GetLotByGUID(lot)?.GetGridData();
            return data != null && PlacementGridReader.TryRead(lot, data.CellDimension, out grid);
        }

        internal static string DescribeFloor(ZoneWallPlatformObject floor, int ordinal)
        {
            var room = floor?.WallZone;
            if (room == null || !Floors(room.LotPlacedOnGUID, room.Layer).Contains(floor)) return Loc.Get("structure_changed");
            int baseHeight = WallHeightManager.Instance.GetFloorHeight(room.LotPlacedOnGUID, room.Cluster, room.Layer);
            return Loc.Get("structure_floor", ordinal, RoomLabelHandler.Name(room),
                floor.Height, PlacementGridReader.Length(floor.Height * Settings.Get<Wall>().StepHeight),
                PlacementGridReader.Length((baseHeight + floor.Height) * Settings.Get<Wall>().StepHeight + (room.IsSpecialLot ? room.Position.y : 0)),
                ScreenReader.NormalizeForOutput(MeasurementUnitService.FormatSurfaceArea(room.SurfaceArea)), floor.ItemsOnThis.Count(i => i != null && i.isActiveAndEnabled),
                Loc.Get(floor.DisabledByUser || floor.DisabledButVisible ? "structure_floor_removed" : "structure_floor_present"));
        }

        internal static string Boundary(ZoneWallObject room)
        {
            if (room == null || !RoomSpatialReader.OrderedRooms(room.LotPlacedOnGUID, room.Layer).Contains(room)
                || !ConstructionGrid(room.LotPlacedOnGUID, out var grid)) return Loc.Get("structure_changed");
            var points = new List<string>();
            foreach (var edge in room.Segments)
            {
                if (edge.Segment == null || edge.Segment.SegmentPoints == null) return Loc.Get("structure_changed");
                var samples = edge.FromAToB ? edge.Segment.SegmentPoints.AsEnumerable() : edge.Segment.SegmentPoints.AsEnumerable().Reverse();
                foreach (var point in samples)
                {
                    string position = PlacementGridReader.Position(grid, point);
                    if (points.Count == 0 || points[points.Count - 1] != position) points.Add(position);
                }
            }
            return Loc.Get("structure_boundary", string.Join("; ", points));
        }

        // Snapshot only during review and confirmation. Include both sides' rooms/platforms and item
        // identities: native deletion may merge rooms, keep a separator, or move supported items.
        internal static object[] Capture(ulong lot, int layer)
        {
            var values = new List<object>();
            if (ConstructionGrid(lot, out var grid)) values.AddRange(new object[] { grid.OriginX, grid.OriginZ, grid.Rotation, grid.Spacing });
            foreach (var wall in SegmentManager.Instance.GetLotSegments(lot).OfType<SegmentWallObject>().Where(w => w != null && w.Layer == layer).OrderBy(w => w.InstanceID))
            {
                values.AddRange(new object[] { wall, wall.InstanceID, wall.gameObject.activeInHierarchy, wall.PlacementPlayer, wall.IsCurved,
                    wall.ReplacingSegment, wall.Cluster, wall.Height, wall.Thickness, wall.NodeA, wall.NodeB, wall.NodeA?.Position, wall.NodeB?.Position });
                if (wall.SegmentPoints != null) values.AddRange(wall.SegmentPoints.Cast<object>());
                foreach (var section in wall.WallSections)
                {
                    values.AddRange(new object[] { section, section.InstanceID, section.StartHeight, section.EndHeight, section.IsFence,
                        section.DisabledByUser, section.DisabledButVisible, section.gameObject.activeInHierarchy, MoneyManager.GetWallSectionCost(section),
                        section.SurfaceSideAGUID, section.SurfaceSideBGUID });
                    if (section.ColorsSideA != null) values.AddRange(section.ColorsSideA.Cast<object>());
                    if (section.ColorsSideB != null) values.AddRange(section.ColorsSideB.Cast<object>());
                    foreach (var item in section.ItemsOnThis) { values.Add(item); values.Add(item != null ? item.InstanceID : -1); }
                }
            }
            foreach (var room in RoomSpatialReader.OrderedRooms(lot, layer))
            {
                values.AddRange(new object[] { room, room.InstanceID, room.Cluster, room.SurfaceArea, room.AveragePosition, room.IsSpecialLot, room.Position,
                    WallHeightManager.Instance.GetFloorHeight(lot, room.Cluster, layer), WallHeightManager.Instance.GetWallHeight(lot, room.Cluster, layer),
                    MoneyManager.GetPlatformCost(room.SurfaceArea), Settings.Get<Wall>().StepHeight, Settings.Get<Wall>().MinHeightSteps });
                foreach (var edge in room.Segments) { values.Add(edge.Segment); values.Add(edge.FromAToB); }
                foreach (var floor in room.Platforms)
                {
                    values.AddRange(new object[] { floor, floor.InstanceID, floor.Height, floor.gameObject.activeInHierarchy,
                        floor.IsCeiling, floor.DisabledByUser, floor.DisabledButVisible, floor.SurfaceGUID, floor.FloorOrientation });
                    if (floor.Colors != null) values.AddRange(floor.Colors.Cast<object>());
                    foreach (var item in floor.ItemsOnThis) { values.Add(item); values.Add(item != null ? item.InstanceID : -1); }
                }
            }
            foreach (var item in ItemManager.Instance.GetLotItems(lot).Where(i => i != null && i.Layer == layer).OrderBy(i => i.InstanceID))
                values.AddRange(new object[] { item, item.InstanceID, item.isActiveAndEnabled, item.CubeTransform?.LocalPosition,
                    item.CubeTransform?.FinalSize, item.transform.rotation, item.FloorPlacedOn, item.WallSectionPlacedOn, item.ItemPlacedOn, item.ItemAttachedTo });
            return values.ToArray();
        }

        private static Review Create(Player p, ulong lot, int layer, string description, Func<string> execute) => !Context(p, lot, layer) ? null :
            new Review { Player = p, Lot = lot, Layer = layer, Description = description, Snapshot = Capture(lot, layer), Execute = execute };

        internal static Review WallReview(Player player, SegmentWallObject wall, int sectionIndex, bool restore, int ordinal = 1)
        {
            if (wall == null || !Walls(wall.LotPlacedOnGUID, wall.Layer).Contains(wall) || sectionIndex < -1 || sectionIndex >= wall.WallSections.Count) return null;
            var section = sectionIndex < 0 ? null : wall.WallSections[sectionIndex];
            if (restore && (section == null || !section.DisabledByUser)) return null;
            if (!restore && section != null && (section.DisabledByUser || section.DisabledButVisible || !section.gameObject.activeInHierarchy)) return null;
            int price = section != null ? MoneyManager.GetWallSectionCost(section) : wall.WallSections.Where(s => !s.DisabledByUser && s.gameObject.activeInHierarchy).Sum(MoneyManager.GetWallSectionCost);
            string description = Loc.Get(restore ? "structure_restore_wall_review" : "structure_delete_wall_review", DescribeWall(wall, ordinal),
                sectionIndex < 0 ? Loc.Get("structure_whole_wall") : Loc.Get("structure_section", sectionIndex + 1), price);
            return Create(player, wall.LotPlacedOnGUID, wall.Layer, description, () =>
            {
                if (restore)
                {
                    var system = SystemManager.Instance.SystemsToUpdate.OfType<RestoreWallSectionEvent>().FirstOrDefault();
                    if (system == null) return Loc.Get("structure_unavailable");
                    if (!MoneyManager.Instance.CanPurchase(price)) return Loc.Get("structure_funds");
                    int before = player.SegmentSectionSelectedIndex;
                    try { player.SegmentSectionSelectedIndex = sectionIndex; system.UpdateMessage(new MessageRestoreWallSection { PlayerIndex = player.PlayerIndex, SegmentToRestore = wall }); }
                    finally { player.SegmentSectionSelectedIndex = before; }
                    return Loc.Get(!section.DisabledByUser && !section.DisabledButVisible ? "structure_wall_restored" : "structure_unverified");
                }
                var deletion = SystemManager.Instance.SystemsToUpdate.OfType<DeleteSegmentEvent>().FirstOrDefault();
                if (deletion == null) return Loc.Get("structure_unavailable");
                deletion.UpdateMessage(new MessageDeleteSegment { PlayerIndex = player.PlayerIndex, SegmentToDelete = wall, SegmentSectionIndex = sectionIndex });
                if (!wall.gameObject.activeInHierarchy) return Loc.Get("structure_wall_deleted");
                if (section != null ? section.DisabledByUser : wall.WallSections.All(s => s.DisabledByUser)) return Loc.Get("structure_separator_retained");
                return Loc.Get("structure_unverified");
            });
        }

        internal static Review FloorReview(Player player, ZoneWallPlatformObject floor, int delta, bool additional, int ordinal = 1)
        {
            var room = floor?.WallZone;
            if (room == null || !Floors(room.LotPlacedOnGUID, room.Layer).Contains(floor) || floor.DisabledByUser || floor.DisabledButVisible
                || !floor.gameObject.activeInHierarchy || (delta != 1 && delta != -1) || (additional && delta != 1)) return null;
            int height = floor.Height + delta;
            int wallHeight = WallHeightManager.Instance.GetWallHeight(room.LotPlacedOnGUID, room.Cluster, room.Layer);
            // Native drag bounds are floor elevation through wall top minus one native step. Height
            // is relative to that elevation (EndNewPlatform/EndMovePlatform), including upper floors.
            if (wallHeight == Settings.Get<Wall>().MinHeightSteps || height < 1 || height >= wallHeight
                || room.Platforms.Any(p => p != floor && p.gameObject.activeInHierarchy && !p.IsCeiling && p.Height == height)) return null;
            bool create = additional || floor.Height == 0;
            int price = create ? MoneyManager.GetPlatformCost(room.SurfaceArea) : 0;
            return Create(player, room.LotPlacedOnGUID, room.Layer,
                Loc.Get(create ? "structure_floor_create_review" : "structure_floor_move_review", DescribeFloor(floor, ordinal), height,
                    PlacementGridReader.Length(height * Settings.Get<Wall>().StepHeight), price), () =>
                {
                    if (create && !MoneyManager.Instance.CanPurchase(price)) return Loc.Get("structure_funds");
                    var before = room.Platforms.ToArray();
                    ZoneManager.Instance.ChangePlatformHeightOrCreateNew(player, floor, height, create, false);
                    bool observed = create ? room.Platforms.Any(p => !before.Contains(p) && p.Height == height && p.gameObject.activeInHierarchy && !p.IsCeiling)
                        : floor.Height == height && floor.gameObject.activeInHierarchy;
                    return Loc.Get(observed ? "structure_floor_changed" : "structure_unverified");
                });
        }

        internal static Review RestoreFloorReview(Player player, ZoneWallPlatformObject floor, int ordinal = 1)
        {
            var room = floor?.WallZone;
            if (room == null || floor.Height != 0 || !floor.DisabledByUser || !Floors(room.LotPlacedOnGUID, room.Layer).Contains(floor)) return null;
            return Create(player, room.LotPlacedOnGUID, room.Layer, Loc.Get("structure_floor_restore_review", DescribeFloor(floor, ordinal)), () =>
            {
                var system = SystemManager.Instance.SystemsToUpdate.OfType<RestoreFloorEvent>().FirstOrDefault();
                if (system == null) return Loc.Get("structure_unavailable");
                system.UpdateMessage(new MessageRestoreFloor { PlayerIndex = player.PlayerIndex, Platform = floor });
                return Loc.Get(floor.gameObject.activeInHierarchy && !floor.DisabledByUser && !floor.DisabledButVisible ? "structure_floor_changed" : "structure_unverified");
            });
        }

        internal static Review RemoveFloorReview(Player player, ZoneWallPlatformObject floor, int ordinal = 1)
        {
            var room = floor?.WallZone;
            if (room == null || floor.Height <= 0 || floor.DisabledByUser || floor.DisabledButVisible || !floor.gameObject.activeInHierarchy
                || !Floors(room.LotPlacedOnGUID, room.Layer).Contains(floor)) return null;
            return Create(player, room.LotPlacedOnGUID, room.Layer,
                Loc.Get("structure_floor_remove_review", DescribeFloor(floor, ordinal), MoneyManager.GetPlatformCost(room.SurfaceArea)), () =>
            {
                var system = SystemManager.Instance.SystemsToUpdate.OfType<DeleteFloorEvent>().FirstOrDefault();
                if (system == null) return Loc.Get("structure_unavailable");
                system.UpdateMessage(new MessageDeleteFloor { PlayerIndex = player.PlayerIndex, Platform = floor });
                return Loc.Get(!floor.gameObject.activeInHierarchy && floor.DisabledByUser ? "structure_floor_changed" : "structure_unverified");
            });
        }
    }
}
