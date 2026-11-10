using System;
using System.Collections.Generic;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>Projects the rotated native box onto a coordinate frame; this is not a clearance test.</summary>
    internal static class ItemFootprintReader
    {
        internal static List<Vector3> Corners(ItemObjectRoot item)
        {
            var result = new List<Vector3>();
            if (item?.CubeTransform == null) return result;
            Vector3 size = item.CubeTransform.FinalSize, center = item.GetWorldBoundingBox().Center;
            if (!WallFaceFrame.Finite(size) || !WallFaceFrame.Finite(center) || !WallFaceFrame.Finite(item.transform.eulerAngles)
                || size.x <= 0 || size.y <= 0 || size.z <= 0) return result;
            // BoundingBox.min/max are formed from rotation * size, not from all eight corners.
            // Use its native centre and the actual rotated box, rather than that diagonal's X/Z.
            for (int x = -1; x <= 1; x += 2)
                for (int y = -1; y <= 1; y += 2)
                    for (int z = -1; z <= 1; z += 2)
                        result.Add(center + item.transform.rotation * new Vector3(x * size.x / 2, y * size.y / 2, z * size.z / 2));
            return result;
        }

        internal static bool Extents(ItemObjectRoot item, PlacementGrid grid, out double minX, out double minZ, out double maxX, out double maxZ)
        {
            minX = minZ = double.PositiveInfinity; maxX = maxZ = double.NegativeInfinity;
            var corners = Corners(item);
            if (grid == null || corners.Count == 0) return false;
            foreach (var point in corners)
            {
                grid.Coordinates(point.x, point.z, out double x, out double z);
                minX = Math.Min(minX, x); minZ = Math.Min(minZ, z); maxX = Math.Max(maxX, x); maxZ = Math.Max(maxZ, z);
            }
            return true;
        }

        internal static string Describe(ItemObjectRoot item)
        {
            return item != null && PlacementGridReader.TryRead(item.LotPlacedOnGUID, ItemPlacementHandler.GridSpacingMetres, out var grid)
                ? Describe(item, grid) : Loc.Get("placement_geometry_unavailable");
        }

        internal static string Describe(ItemObjectRoot item, PlacementGrid grid)
        {
            if (!Extents(item, grid, out double x1, out double z1, out double x2, out double z2)) return Loc.Get("placement_geometry_unavailable");
            // Numerical boundary tolerance only, not geometric clearance or padding.
            double left = Math.Floor(x1 + 0.00001), right = Math.Ceiling(x2 - 0.00001) - 1;
            double front = Math.Floor(z1 + 0.00001), back = Math.Ceiling(z2 - 0.00001) - 1;
            return Loc.Get("footprint_report", Math.Max(1, right - left + 1), Math.Max(1, back - front + 1),
                x1.ToString("0.##"), x2.ToString("0.##"), z1.ToString("0.##"), z2.ToString("0.##"), PlacementGridReader.Length((float)grid.Spacing));
        }
    }
}
