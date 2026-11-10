using System;
using System.Collections.Generic;
using System.Linq;
using ParalivesAccess.Speech;
using Setting;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ParalivesAccess.Handlers
{
    /// <summary>A selected room side, with direction established by its native room-facing mesh.</summary>
    internal sealed class RoomExtensionTarget
    {
        internal ZoneWallObject Room;
        internal WallSpanTarget Span;
        internal int Ordinal;
        internal ulong Lot;
        internal int Layer;

        internal bool IsLive => Room != null && RoomSpatialReader.IsUsableRoom(Room, Lot)
            && Room.Layer == Layer && Span.IsLive(Lot, Layer)
            && Walls.All(w => w.NodeA != null && w.NodeB != null
                && WallFaceFrame.Finite(w.NodeA.Position) && WallFaceFrame.Finite(w.NodeB.Position));

        internal List<SegmentWallObject> Walls => Span.Members.Select(m => m.Face.WallSection.Wall).Distinct().ToList();

        internal string SideName => Loc.Get("extension_wall_direction", Ordinal, RoomSpatialReader.LotSide(Lot, Span.Frame));

        internal string Name()
        {
            if (!IsLive) return Loc.Get("extension_changed");
            return Loc.Get("extension_side", SideName, PlacementGridReader.Length(Width), OppositeRoom() is ZoneWallObject other
                ? Loc.Get("extension_other_room", RoomLabelHandler.Name(other))
                : Loc.Get("extension_outside"));
        }

        internal ZoneWallObject OppositeRoom() => RoomSpatialReader.OrderedRooms(Lot, Layer)
            .FirstOrDefault(r => r != Room && r.Segments.Any(e => Walls.Contains(e.Segment as SegmentWallObject)));

        // Mesh extents can include wall thickness and corner mitres. Construction must use the
        // actual endpoint nodes of the chosen span, never offset mesh-face corners.
        internal void Endpoints(out Vector3 a, out Vector3 b)
        {
            var points = Walls.SelectMany(w => new[] { w.NodeA.Position, w.NodeB.Position })
                .OrderBy(p => Span.Frame.Along(p)).ToList();
            a = points.First(); b = points.Last();
        }

        internal float Width { get { Endpoints(out var a, out var b); return Vector3.Distance(new Vector3(a.x, 0, a.z), new Vector3(b.x, 0, b.z)); } }

        internal string Prepare(double depth, out RoomExtensionReview review)
        {
            review = null;
            if (!IsLive) return Loc.Get("extension_changed");
            if (OppositeRoom() != null) return Loc.Get("extension_room_blocked");
            if (!PlacementGrid.Finite(depth) || depth <= 0) return Loc.Get("extension_depth_invalid");
            if (!Settings.Get<Gameplay>().UsesWallGrid) return Loc.Get("wall_grid_disabled");
            var data = LotManager.Instance.GetLotByGUID(Lot)?.GetGridData();
            if (data == null || !PlacementGridReader.TryRead(Lot, data.CellDimension, out var grid)) return Loc.Get("wall_grid_unavailable");
            Endpoints(out var a, out var b);
            grid.Coordinates(a.x, a.z, out double ac, out double ar);
            grid.Coordinates(b.x, b.z, out double bc, out double br);
            if (Math.Abs(ac - bc) * grid.Spacing > 0.002 && Math.Abs(ar - br) * grid.Spacing > 0.002)
                return Loc.Get("extension_grid_required");
            double roundedDepth = Math.Max(1, Math.Round(depth / grid.Spacing, MidpointRounding.AwayFromZero)) * grid.Spacing;
            var opposite = b - Span.Frame.Normal * (float)roundedDepth;
            if (!RectangleWallPlan.TryCreate(grid, a.x, a.z, opposite.x, opposite.z, out var plan))
                return Loc.Get("extension_grid_required");
            int height = Walls[0].Height;
            if (!SharedWallBoundary.Find(plan, Lot, Layer, height, out var shared) || shared == null
                || shared.Walls.Count != Walls.Count || shared.Walls.Any(w => !Walls.Contains(w)))
                return Loc.Get("extension_shared_unsupported");
            var proposed = new RoomExtensionReview { Target = this, Plan = plan, Shared = shared,
                Width = Width, Depth = (float)roundedDepth, Height = height,
                RoomNumber = RoomSpatialReader.OrderedRooms(Lot, Layer).IndexOf(Room) + 1 };
            string problem = proposed.Problem();
            if (problem != null) return problem;
            review = proposed;
            return null;
        }
    }

    /// <summary>Checks proposed space without creating walls or changing any saved geometry.</summary>
    internal sealed class RoomExtensionReview
    {
        internal RoomExtensionTarget Target;
        internal RectangleWallPlan Plan;
        internal SharedWallBoundary Shared;
        internal float Width, Depth;
        internal int Height, RoomNumber;

        internal string Description => Loc.Get("extension_review", RoomLabelHandler.Name(Target.Room, RoomNumber), Target.SideName,
            PlacementGridReader.Length(Width), PlacementGridReader.Length(Depth));

        // Re-read at the native confirmation boundary as well as when the user chooses depth.
        // Previously confirmed edges on this plan's boundary are allowed; crossing its interior
        // is not. This continues to work while native room/mesh refresh reconstructs room objects.
        internal string Problem()
        {
            ulong lot = Target.Lot; int layer = Target.Layer;
            if (!BuildModeRefreshManager.IsZoneStateRefreshed || ZoneManager.Instance.DirtyWallZones.Count != 0)
                return Loc.Get("extension_updating");
            if (!Shared.IsLive()) return Loc.Get("extension_changed");
            var data = LotManager.Instance.GetLotByGUID(lot)?.GetGridData();
            if (data == null || !PlacementGridReader.TryRead(lot, data.CellDimension, out var grid) || !Plan.Grid.SameFrame(grid))
                return Loc.Get("extension_changed");
            for (int i = 0; i < 4; i++)
            {
                Plan.Point(i, out double x, out double z);
                var inside = ZoneManager.Instance.GetNodeCursorLotPerimeterOfHousehold(new Vector3((float)x, 0, (float)z), lot);
                if (!inside.Item1 || inside.Item2 != lot) return Loc.Get("extension_lot_blocked");
            }
            var perimeter = ZoneManager.Instance.GetLotPerimeterZoneObjectByGUID(lot)?.GetPoints();
            if (perimeter == null || perimeter.Count < 3) return Loc.Get("extension_changed");
            for (int i = 0; i < perimeter.Count; i++)
            {
                var a = perimeter[i]; var b = perimeter[(i + 1) % perimeter.Count];
                if (Plan.CrossesInterior(a.x, a.z, b.x, b.z)) return Loc.Get("extension_lot_blocked");
            }
            Plan.Grid.World((Plan.MinColumn + Plan.MaxColumn) / 2, (Plan.MinRow + Plan.MaxRow) / 2, out double cx, out double cz);
            var centre = new Vector3((float)cx, 0, (float)cz);
            if (RoomSpatialReader.OrderedRooms(lot, layer).Any(r => ZoneGeometry.Contains(r, centre)))
                return Loc.Get("extension_room_blocked");
            foreach (var wall in SegmentManager.Instance.GetLotSegments(lot).OfType<SegmentWallObject>())
            {
                if (wall == null || !wall.gameObject.activeInHierarchy || wall.Layer != layer || wall.PlacementPlayer != -1) continue;
                if (wall.SegmentPoints == null || wall.SegmentPoints.Count < 2) return Loc.Get("extension_changed");
                for (int i = 1; i < wall.SegmentPoints.Count; i++)
                {
                    var a = wall.SegmentPoints[i - 1]; var b = wall.SegmentPoints[i];
                    if (Plan.CrossesInterior(a.x, a.z, b.x, b.z)) return Loc.Get("extension_wall_blocked");
                }
            }
            // Existing furniture may remain inside a new room, but an extension must not draw
            // a new wall through its bounding box. Objects mounted on the shared wall stay there.
            foreach (var item in ItemManager.Instance.GetLotItems(lot))
            {
                if (item == null || !item.gameObject.activeInHierarchy || item.Layer != layer || item.IsWallItem || item.IsCeilingItem) continue;
                if (!ItemFootprintReader.Extents(item, grid, out double x1, out double z1, out double x2, out double z2))
                    return Loc.Get("extension_changed");
                for (int edge = 0; edge < 4; edge++)
                {
                    Plan.Point(edge, out double ax, out double az); Plan.Point(edge + 1, out double bx, out double bz);
                    if (Shared.Walls.Any(w => ConstructionGeometry.Overlaps(ax, az, bx, bz,
                        w.NodeA.Position.x, w.NodeA.Position.z, w.NodeB.Position.x, w.NodeB.Position.z))) continue;
                    grid.Coordinates(ax, az, out double ac, out double ar); grid.Coordinates(bx, bz, out double bc, out double br);
                    const double tolerance = 0.0001;
                    if (Math.Max(Math.Min(ac, bc), x1) < Math.Min(Math.Max(ac, bc), x2) - tolerance
                        && Math.Min(ar, br) > z1 + tolerance && Math.Max(ar, br) < z2 - tolerance
                        || Math.Max(Math.Min(ar, br), z1) < Math.Min(Math.Max(ar, br), z2) - tolerance
                        && Math.Min(ac, bc) > x1 + tolerance && Math.Max(ac, bc) < x2 - tolerance)
                        return Loc.Get("extension_item_blocked", ItemDescription.GetName(item) ?? Loc.Get("item_unknown"));
                }
            }
            return null;
        }
    }

    internal static class RoomExtensionReader
    {
        internal static List<RoomExtensionTarget> Read(ZoneWallObject room)
        {
            var result = new List<RoomExtensionTarget>();
            if (room == null || !RoomSpatialReader.IsUsableRoom(room, room.LotPlacedOnGUID)) return result;
            var faces = RoomSpatialReader.Faces(room);
            foreach (var face in faces)
            {
                if (result.Any(t => t.Span.Members.Any(m => m.Face == face.Face))) continue;
                var span = WallSpanReader.Build(faces, face, room.LotPlacedOnGUID, room.Layer);
                if (span == null) continue;
                result.Add(new RoomExtensionTarget { Room = room, Span = span, Ordinal = result.Count + 1,
                    Lot = room.LotPlacedOnGUID, Layer = room.Layer });
            }
            return result;
        }

        internal static void OpenDepth(HybridPlayer hybrid, RoomExtensionTarget target)
        {
            if (!target.IsLive || !StructureEditingHandler.Context(hybrid.Player, target.Lot, target.Layer))
            { ScreenReader.Speak(Loc.Get("extension_changed")); return; }
            if (target.OppositeRoom() != null) { ScreenReader.Speak(Loc.Get("extension_room_blocked")); return; }
            var unit = MeasurementUnitService.GetCurrentUnit();
            if (unit == null || !PlacementGrid.Finite(unit.MetricMultiplier) || unit.MetricMultiplier <= 0)
            { ScreenReader.Speak(Loc.Get("room_size_unavailable")); return; }
            float multiplier = unit.MetricMultiplier;
            CoordinateEntryHandler.OpenDepth(hybrid,
                Loc.Get("extension_depth_hint", target.Name(), unit.ShortName), target.Width * multiplier, 4 * multiplier,
                () => target.IsLive && hybrid.Player.GetLotLayer(target.Lot) == target.Layer
                    && ItemPlacementHandler.ResolvePlacementLot(hybrid.Player, out _) == target.Lot
                    && MeasurementUnitService.GetCurrentUnit() == unit && unit.MetricMultiplier == multiplier,
                value =>
                {
                    if (!StructureEditingHandler.Context(hybrid.Player, target.Lot, target.Layer))
                    { ScreenReader.Speak(Loc.Get("extension_changed")); return; }
                    string problem = target.Prepare(value / multiplier, out var review);
                    if (problem != null) { ScreenReader.Speak(problem); return; }
                    ScreenReader.Speak(WallConstructionHandler.StartExtension(hybrid, review));
                }, Loc.Get("extension_title"), Loc.Get("extension_depth_instructions"));
        }

        internal static bool SelectWallTool(HybridPlayer hybrid)
        {
            var catalog = UI.GetOrNull<UIBuildModeCatalog>(hybrid.Player.PlayerIndex);
            var tags = Settings.Get<BuildModeCatalogTags>();
            var root = tags.GetTagByGUID(tags.WallModeTag);
            if (catalog == null || root == null) return false;
            var candidates = new List<BuildModeCatalogTag>();
            var pending = new Queue<BuildModeCatalogTag>(); var seen = new HashSet<ulong>(); pending.Enqueue(root);
            while (pending.Count > 0)
            {
                var tag = pending.Dequeue();
                if (tag == null || !seen.Add(tag.GUID)) continue;
                if (tag.ChangesState && tag.SetGameState == GameStates.PlaceSegment && tag.SetSegmentType == SegmentTypes.Wall
                    && tag.SetWallHeightType == WallHeightType.FullWall) candidates.Add(tag);
                if (tag.ChildTags != null) foreach (var child in tag.ChildTags) pending.Enqueue(tags.GetTagByGUID(child.GUID));
            }
            var selected = candidates.Contains(root) ? root : candidates.Count == 1 ? candidates[0] : null;
            if (selected == null) return false;
            catalog.SetModeFromShortcut(GameStates.PlaceSegment, SegmentTypes.Wall, selected.GUID);
            catalog.ChangePlayerState(selected.GUID);
            if (hybrid.Player.WallPlacementMode != WallPlacementMode.Straight)
            {
                var mode = catalog.Modes?.FirstOrDefault(m => m.TagMode == BuildModeTagModes.WallPlacement);
                int index = (int)WallPlacementMode.Straight;
                if (mode?.Buttons == null || index < 0 || index >= mode.Buttons.Length) return false;
                mode.OnClick(index);
            }
            return hybrid.Player.State == GameStates.PlaceSegment && hybrid.Player.CurrentSegmentType == SegmentTypes.Wall
                && hybrid.Player.CurrentWallHeightType == WallHeightType.FullWall && hybrid.Player.WallPlacementMode == WallPlacementMode.Straight;
        }
    }
}
