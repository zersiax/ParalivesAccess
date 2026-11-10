using System;
using System.Linq;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>Measured bounding-edge alignment requests, observed through ordinary native placement.</summary>
    internal static class ItemAlignmentHandler
    {
        private static ItemObjectInPlacement _preview;
        private static ItemObjectRoot _target;
        private static int _itemId, _targetId;
        private static Vector3[] _corners;
        private static PlacementGrid _grid;
        private static WallFaceTarget _face;
        private static WallFaceFrame _frame;
        private static Vector3 _position;
        private static Vector3 _size;
        private static Quaternion _rotation;

        internal static void Clear() { _preview = null; _target = null; _face = null; _corners = null; }

        internal static bool Frame(ItemObjectRoot item, out PlacementGrid grid) =>
            RoomPlacementGrid.TryRead(item.LotPlacedOnGUID, item.Layer, ItemPlacementHandler.GridSpacingMetres, out grid);

        internal static bool Bounds(ItemObjectRoot item, Vector3 axis, out float min, out float max)
        {
            min = float.PositiveInfinity; max = float.NegativeInfinity;
            var corners = ItemFootprintReader.Corners(item);
            if (corners.Count != 8 || !WallFaceFrame.Finite(axis) || axis.magnitude < 0.99f) return false;
            foreach (var corner in corners) { float p = Vector3.Dot(corner, axis); min = Math.Min(min, p); max = Math.Max(max, p); }
            return true;
        }

        internal static Vector3 Axis(PlacementGrid grid, bool row)
        {
            grid.World(row ? 0 : 1, row ? 1 : 0, out double x, out double z);
            return new Vector3((float)(x - grid.OriginX), 0, (float)(z - grid.OriginZ)).normalized;
        }

        // A negative signed interval gap means overlap of projections, not collision of meshes.
        internal static float Gap(float a, float b, float c, float d) => Math.Max(c - b, a - d);

        internal static string Measure(ItemObjectRoot item, ItemObjectRoot target)
        {
            if (!FacingTargetHandler.LiveTarget(item, target) || !Frame(item, out var grid)) return Loc.Get("alignment_unavailable");
            var x = Axis(grid, false); var z = Axis(grid, true);
            if (!Bounds(item, x, out float a, out float b) || !Bounds(target, x, out float c, out float d)
                || !Bounds(item, z, out float e, out float f) || !Bounds(target, z, out float g, out float h)) return Loc.Get("alignment_unavailable");
            return Loc.Get("alignment_gaps", ItemDescription.GetProductIdentity(target),
                PlacementGridReader.Length(Gap(a, b, c, d)), PlacementGridReader.Length(Gap(e, f, g, h)),
                Loc.Get(RoomPlacementGrid.Selected ? "room_grid_coordinate_label" : "alignment_lot_axes"));
        }

        internal static WallFaceTarget TargetWall(ItemObjectRoot item)
        {
            if (item?.WallSectionPlacedOn == null || (item.WallSideOn != WallVerticalSectionPartSides.SideA && item.WallSideOn != WallVerticalSectionPartSides.SideB)) return null;
            return WallFaceReader.Read(item.LotPlacedOnGUID, item.Layer).SelectMany(g => g.Faces)
                .FirstOrDefault(f => f.Face.WallSection == item.WallSectionPlacedOn
                    && f.Face == (item.WallSideOn == WallVerticalSectionPartSides.SideA ? item.WallSectionPlacedOn.SideA : item.WallSectionPlacedOn.SideB));
        }

        private static bool CanRequest(HybridPlayer h, ItemObjectInPlacement p) => p?.Item != null
            && h?.Player?.ItemInPlacement == p && ItemPlacementHandler.OwnsInput && h.Player.PlayerActionsAreAllowed
            && !h.HasInputFieldFocused && !h.Player.PlayerActionBlockedByUIWindow && !h.IsUsingGamePad
            && h.Player.ItemInRotation == null && !p.IsInOffset && !p.FromRotationWidget
            && h.Player.ItemInResize == null && h.Player.ItemInScale == null
            && !p.Item.IsWallItem && !p.Item.IsCeilingItem && !p.Item.IsLotMovementItem
            && p.Item.PlacementSlotPlacedOn == 0 && p.Item.ChairSlotPlacedOn == 0 && p.Item.ItemAttachedTo == null
            && HouseholdManager.Instance.OwnsLot(p.Item.LotPlacedOnGUID);

        internal static string Align(HybridPlayer h, ItemObjectInPlacement p, ItemObjectRoot target, bool row, int edge, bool wallAxis = false)
        {
            if (!CanRequest(h, p) || !FacingTargetHandler.LiveTarget(p.Item, target) || !Frame(p.Item, out var grid))
                return Loc.Get("alignment_unavailable");
            var face = wallAxis ? TargetWall(target) : null;
            WallFaceFrame frame = null;
            if (wallAxis && (face == null || !face.TryRead(p.Item.LotPlacedOnGUID, p.Item.Layer, out frame))) return Loc.Get("alignment_unavailable");
            Vector3 axis = wallAxis ? frame.Right : Axis(grid, row);
            if (!Bounds(p.Item, axis, out float a, out float b) || !Bounds(target, axis, out float c, out float d)) return Loc.Get("alignment_unavailable");
            float delta = edge < 0 ? c - a : edge > 0 ? d - b : (c + d - a - b) / 2;
            var position = p.Item.CubeTransform.LocalPosition + axis * delta;
            string problem = ItemPlacementHandler.RequestAlignment(h, p, position);
            if (problem != null) return problem;
            Capture(p, target, grid, face, frame, position);
            return Loc.Get("alignment_requested");
        }

        internal static string WallGap(ItemObjectRoot item, WallFaceTarget face)
        {
            if (face == null || !face.TryRead(item.LotPlacedOnGUID, item.Layer, out var frame)
                || !Bounds(item, frame.Normal, out float min, out _)) return Loc.Get("alignment_unavailable");
            float gap = min - Vector3.Dot(frame.Origin, frame.Normal);
            return Loc.Get("alignment_wall_gap", PlacementGridReader.Length(gap));
        }

        internal static string SetWallGap(HybridPlayer h, ItemObjectInPlacement p, WallFaceTarget face, double gap)
        {
            if (!CanRequest(h, p) || !PlacementGrid.Finite(gap) || gap < 0 || gap > 1000 || !Frame(p.Item, out var grid)
                || face == null || !face.TryRead(p.Item.LotPlacedOnGUID, p.Item.Layer, out var frame)
                || !Bounds(p.Item, frame.Normal, out float min, out _)) return Loc.Get("alignment_unavailable");
            var position = p.Item.CubeTransform.LocalPosition + frame.Normal * ((float)gap + Vector3.Dot(frame.Origin, frame.Normal) - min);
            // A finite wall face is not an infinite plane: projected item extent must lie on this span.
            if (!Bounds(p.Item, frame.Right, out float left, out float right)) return Loc.Get("alignment_unavailable");
            float start = Vector3.Dot(frame.Origin, frame.Right);
            if (left < start - 0.01f || right > start + frame.Width + 0.01f) return Loc.Get("alignment_wall_extent");
            string problem = ItemPlacementHandler.RequestAlignment(h, p, position);
            if (problem != null) return problem;
            Capture(p, null, grid, face, frame, position);
            return Loc.Get("alignment_requested");
        }

        private static void Capture(ItemObjectInPlacement p, ItemObjectRoot target, PlacementGrid grid, WallFaceTarget face, WallFaceFrame frame, Vector3 position)
        {
            _preview = p; _itemId = p.Item.InstanceID; _target = target; _targetId = target != null ? target.InstanceID : -1;
            _corners = target != null ? ItemFootprintReader.Corners(target).ToArray() : null;
            _grid = grid; _face = face; _frame = frame; _position = position;
            _size = p.Item.CubeTransform.FinalSize; _rotation = p.Item.transform.rotation;
        }

        internal static string Problem(HybridPlayer h, ItemObjectRoot item)
        {
            if (_preview == null) return null;
            if (h?.Player?.ItemInPlacement != _preview || _preview.Item != item || item.InstanceID != _itemId
                || !Frame(item, out var grid) || !_grid.SameFrame(grid) || item.CubeTransform == null
                || !WallFaceFrame.Finite(item.CubeTransform.FinalSize) || Vector3.Distance(_size, item.CubeTransform.FinalSize) > 0.002f
                || !PlacementGrid.Finite(Quaternion.Angle(_rotation, item.transform.rotation))
                || Quaternion.Angle(_rotation, item.transform.rotation) > 0.1f) return Loc.Get("alignment_changed");
            if (_targetId >= 0 && (!FacingTargetHandler.LiveTarget(item, _target) || _target.InstanceID != _targetId
                || !SameCorners(_corners, ItemFootprintReader.Corners(_target).ToArray()))) return Loc.Get("alignment_changed");
            if (_face != null && (!_face.TryRead(item.LotPlacedOnGUID, item.Layer, out var frame) || !_frame.Same(frame))) return Loc.Get("alignment_changed");
            var actual = item.CubeTransform.LocalPosition;
            return WallFaceFrame.Finite(actual) && Math.Abs(actual.x - _position.x) <= 0.01f && Math.Abs(actual.z - _position.z) <= 0.01f
                ? null : Loc.Get("alignment_mismatch");
        }

        private static bool SameCorners(Vector3[] a, Vector3[] b) => a != null && b != null && a.Length == 8 && b.Length == 8
            && a.Zip(b, (x, y) => Vector3.Distance(x, y) <= 0.002f).All(same => same);
        internal static string Describe(HybridPlayer h, ItemObjectRoot item) => _preview == null ? "" : Problem(h, item) ?? Loc.Get("alignment_observed");
    }
}
