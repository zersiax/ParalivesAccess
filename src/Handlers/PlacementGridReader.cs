using System;
using System.Collections.Generic;
using ParalivesAccess.Speech;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>Reads the native lot frame and live preview geometry for the accessibility grid.</summary>
    internal static class PlacementGridReader
    {
        internal static bool TryRead(ulong lotGuid, float spacing, out PlacementGrid grid)
        {
            grid = null;
            AssetLot lot = LotManager.Instance?.GetLotByGUID(lotGuid);
            LotPerimeterZoneObject perimeter = ZoneManager.Instance?.GetLotPerimeterZoneObjectByGUID(lotGuid);
            if (lot == null || perimeter == null || perimeter.Segments == null
                || perimeter.Segments.Count < 3 || !PlacementGrid.Finite(spacing) || spacing <= 0)
                return false;
            AssetLotGridData data = lot.GetGridData();
            if (data == null || !PlacementGrid.Finite(data.OffsetFromOrigin.x)
                || !PlacementGrid.Finite(data.OffsetFromOrigin.y) || !PlacementGrid.Finite(data.Rotation))
                return false;

            var nativeFrame = new PlacementGrid(data.OffsetFromOrigin.x, data.OffsetFromOrigin.y,
                data.Rotation, spacing);
            List<Vector3> points = perimeter.GetPoints();
            if (points == null || points.Count < 3) return false;
            double minColumn = double.PositiveInfinity, minRow = double.PositiveInfinity;
            foreach (Vector3 point in points)
            {
                if (!PlacementGrid.Finite(point.x) || !PlacementGrid.Finite(point.z)) return false;
                nativeFrame.Coordinates(point.x, point.z, out double column, out double row);
                minColumn = Math.Min(minColumn, column);
                minRow = Math.Min(minRow, row);
            }
            // The enclosing rectangle's lower grid intersection is a reproducible address origin.
            // It may be outside a non-rectangular lot; native placement still validates containment.
            nativeFrame.World(Math.Floor(minColumn + 0.00001), Math.Floor(minRow + 0.00001),
                out double x, out double z);
            grid = new PlacementGrid(x, z, data.Rotation, spacing);
            return true;
        }

        internal static string Position(PlacementGrid grid, Vector3 position)
        {
            grid.Coordinates(position.x, position.z, out double column, out double row);
            return Loc.Get("placement_grid_position", Number(column), Number(row));
        }

        internal static bool TryBounds(ulong lot, PlacementGrid grid,
            out double minColumn, out double minRow, out double maxColumn, out double maxRow)
        {
            minColumn = minRow = double.PositiveInfinity;
            maxColumn = maxRow = double.NegativeInfinity;
            var points = ZoneManager.Instance?.GetLotPerimeterZoneObjectByGUID(lot)?.GetPoints();
            if (grid == null || points == null || points.Count < 3) return false;
            foreach (var point in points)
            {
                if (!PlacementGrid.Finite(point.x) || !PlacementGrid.Finite(point.z)) return false;
                grid.Coordinates(point.x, point.z, out double column, out double row);
                minColumn = Math.Min(minColumn, column); maxColumn = Math.Max(maxColumn, column);
                minRow = Math.Min(minRow, row); maxRow = Math.Max(maxRow, row);
            }
            return maxColumn > minColumn && maxRow > minRow;
        }

        internal static string DescribeBounds(ulong lot, PlacementGrid grid)
        {
            return TryBounds(lot, grid, out double x1, out double y1, out double x2, out double y2)
                ? Loc.Get("build_lot_bounds", Number(x1), Number(x2), Number(y1), Number(y2))
                : Loc.Get("build_bounds_unavailable");
        }

        internal static string DescribeRegion(ulong lot, PlacementGrid grid, Vector3 position)
        {
            if (!TryBounds(lot, grid, out double x1, out double y1, out double x2, out double y2)
                || !PlacementGrid.Finite(position.x) || !PlacementGrid.Finite(position.z))
                return Loc.Get("build_location_unavailable");
            grid.Coordinates(position.x, position.z, out double column, out double row);
            double x = (column - x1) / (x2 - x1), y = (row - y1) / (y2 - y1);
            if (x < 0 || y < 0 || x > 1 || y > 1) return Loc.Get("build_outside_lot");
            int horizontal = x < 1.0 / 3 ? 0 : x > 2.0 / 3 ? 2 : 1;
            int vertical = y < 1.0 / 3 ? 0 : y > 2.0 / 3 ? 2 : 1;
            string[] regions = { "build_region_lower_left", "build_region_lower", "build_region_lower_right",
                "build_region_left", "build_region_centre", "build_region_right",
                "build_region_upper_left", "build_region_upper", "build_region_upper_right" };
            return Loc.Get(regions[vertical * 3 + horizontal]);
        }

        internal static string Dimensions(ItemObjectRoot root)
        {
            if (root.CubeTransform == null) return Loc.Get("placement_geometry_unavailable");
            Vector3 size = root.CubeTransform.FinalSize;
            return Loc.Get("placement_dimensions", Length(Math.Abs(size.x)),
                Length(Math.Abs(size.z)), Length(Math.Abs(size.y)));
        }

        internal static string Length(float metres) =>
            ScreenReader.NormalizeForOutput(MeasurementUnitService.FormatLength(metres, "0.00"));

        internal static string Movement(PlacementGrid grid, Vector3 before, Vector3 after)
        {
            if (grid == null) return null;
            grid.Coordinates(before.x, before.z, out double c1, out double r1);
            grid.Coordinates(after.x, after.z, out double c2, out double r2);
            double x = (c2 - c1) * grid.Spacing, z = (r2 - r1) * grid.Spacing;
            var parts = new List<string>();
            if (Math.Abs(x) >= 0.01) parts.Add(Loc.Get(x > 0 ? "build_moved_right" : "build_moved_left", Length((float)Math.Abs(x))));
            if (Math.Abs(z) >= 0.01) parts.Add(Loc.Get(z > 0 ? "build_moved_up" : "build_moved_down", Length((float)Math.Abs(z))));
            return string.Join(". ", parts);
        }

        private static string Number(double value) => (Math.Abs(value) < 0.005 ? 0 : value).ToString("0.##");
    }
}
