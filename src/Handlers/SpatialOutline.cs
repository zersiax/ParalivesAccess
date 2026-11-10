using System;
using System.Collections.Generic;
using System.Linq;
using ParalivesAccess.Speech;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>Descriptive extents and shape of a native boundary, never placement validation.</summary>
    internal sealed class SpatialOutline
    {
        internal struct Point
        {
            internal double X, Y;
            internal Point(double x, double y) { X = x; Y = y; }
        }

        internal readonly List<Point> Points;
        internal readonly double Left, Right, Front, Back;
        internal double Width => Right - Left;
        internal double Depth => Back - Front;
        private readonly bool _curved;
        private readonly double _rotation;

        private SpatialOutline(List<Point> points, bool curved, double rotation)
        {
            Points = points; _curved = curved; _rotation = rotation;
            Left = points.Min(p => p.X); Right = points.Max(p => p.X);
            Front = points.Min(p => p.Y); Back = points.Max(p => p.Y);
        }

        internal static bool TryRead(ZoneObject zone, PlacementGrid grid, out SpatialOutline outline)
        {
            outline = null;
            if (grid == null || zone?.Segments == null || zone.Segments.Count < 2) return false;
            var points = new List<Point>();
            bool curved = false;
            foreach (var edge in zone.Segments)
            {
                var segment = edge.Segment;
                if (segment?.SegmentPoints == null || segment.SegmentPoints.Count < 2) return false;
                curved |= segment.IsCurved;
                var sequence = edge.FromAToB ? segment.SegmentPoints : segment.SegmentPoints.AsEnumerable().Reverse();
                bool first = true;
                foreach (var p in sequence)
                {
                    if (!PlacementGrid.Finite(p.x) || !PlacementGrid.Finite(p.z)) return false;
                    grid.Coordinates(p.x, p.z, out double x, out double y);
                    var point = new Point(x * grid.Spacing, y * grid.Spacing);
                    if (first && points.Count > 0 && Distance(points[points.Count - 1], point) > 0.02) return false;
                    first = false;
                    if (points.Count == 0 || Distance(points[points.Count - 1], point) > 0.001) points.Add(point);
                }
            }
            if (points.Count < 4 || Distance(points[0], points[points.Count - 1]) > 0.02) return false;
            points.RemoveAt(points.Count - 1);
            // Native splits do not turn a rectangular room into a six-sided room.
            bool removed = true;
            while (removed && points.Count > 3)
            {
                removed = false;
                for (int i = 0; i < points.Count; i++)
                {
                    var a = points[(i + points.Count - 1) % points.Count];
                    var b = points[i]; var c = points[(i + 1) % points.Count];
                    if (DistanceToSegment(b, a, c) > 0.002) continue;
                    points.RemoveAt(i); removed = true; break;
                }
            }
            var candidate = new SpatialOutline(points, curved, grid.Rotation);
            if (candidate.Width < 0.01 || candidate.Depth < 0.01 || Math.Abs(candidate.SignedArea()) < 0.0001) return false;
            outline = candidate; return true;
        }

        private double SignedArea()
        {
            double area = 0;
            for (int i = 0; i < Points.Count; i++)
            {
                var a = Points[i]; var b = Points[(i + 1) % Points.Count];
                area += a.X * b.Y - b.X * a.Y;
            }
            return area / 2;
        }

        internal string Shape()
        {
            if (_curved) return Loc.Get("layout_shape_curved");
            bool orthogonal = true;
            var inward = new List<Point>();
            double winding = Math.Sign(SignedArea());
            for (int i = 0; i < Points.Count; i++)
            {
                var a = Points[(i + Points.Count - 1) % Points.Count];
                var b = Points[i]; var c = Points[(i + 1) % Points.Count];
                double ax = b.X - a.X, ay = b.Y - a.Y, bx = c.X - b.X, by = c.Y - b.Y;
                if (Math.Abs(ax * bx + ay * by) > 0.002 * Distance(a, b) * Distance(b, c)) orthogonal = false;
                if ((ax * by - ay * bx) * winding < -0.0001) inward.Add(b);
            }
            if (orthogonal && Points.Count == 4)
            {
                double a = Distance(Points[0], Points[1]), b = Distance(Points[1], Points[2]);
                return Loc.Get(Math.Abs(a - b) < 0.02 ? "layout_shape_square" : "layout_shape_rectangle");
            }
            if (orthogonal && Points.Count == 6 && inward.Count == 1)
            {
                // The only absent corner of the enclosing rectangle identifies the L's recess
                // when the outline follows our common lot axes. Rotated Ls keep an honest label.
                var corners = new[] { new Point(Left, Front), new Point(Right, Front), new Point(Left, Back), new Point(Right, Back) };
                var missing = corners.Where(c => !Points.Any(p => Distance(c, p) < 0.02)).ToList();
                return missing.Count == 1 ? Loc.Get("layout_shape_l_recess", Region(missing[0])) : Loc.Get("layout_shape_l");
            }
            return inward.Count > 0 ? Loc.Get("layout_shape_indented", Points.Count,
                string.Join(", ", inward.Select(Region).Distinct())) : Loc.Get("layout_shape_polygon", Points.Count);
        }

        internal string Size() => Loc.Get("layout_size", Length(Width), Length(Depth));

        internal string Side(ZoneSegment edge)
        {
            if (edge.Segment.IsCurved) return Loc.Get("layout_curved_wall");
            var points = edge.Segment.SegmentPoints;
            if (points == null || points.Count < 2) return Loc.Get("room_angled_wall");
            // Work in the same axes as the outline. The directed edge and polygon winding give
            // its inward normal even on a recess, where a bounding-centre test would be wrong.
            var a = edge.FromAToB ? points[0] : points[points.Count - 1];
            var b = edge.FromAToB ? points[points.Count - 1] : points[0];
            double dx = b.x - a.x, dz = b.z - a.z;
            double radians = _rotation * Math.PI / 180;
            double x = dx * Math.Cos(radians) + dz * Math.Sin(radians);
            double y = -dx * Math.Sin(radians) + dz * Math.Cos(radians);
            double length = Math.Sqrt(x * x + y * y);
            if (length < 0.001) return Loc.Get("room_angled_wall");
            double inwardX = -y / length * Math.Sign(SignedArea()), inwardY = x / length * Math.Sign(SignedArea());
            return Loc.Get(inwardY > 0.99 ? "room_front_wall" : inwardY < -0.99 ? "room_back_wall"
                : inwardX > 0.99 ? "room_left_wall" : inwardX < -0.99 ? "room_right_wall" : "room_angled_wall");
        }

        internal string Region(Point p)
        {
            string horizontal = RoomSpatialReader.Region((float)((p.X - Left) / Width), "room_left", "room_centre", "room_right");
            string depth = RoomSpatialReader.Region((float)((p.Y - Front) / Depth), "room_front", "room_middle", "room_back");
            return Loc.Get("layout_region", depth, horizontal);
        }

        internal string DescribePosition(PlacementGrid grid, Vector3 position)
        {
            grid.Coordinates(position.x, position.z, out double x, out double y);
            var p = new Point(x * grid.Spacing, y * grid.Spacing);
            double dx = p.X - (Left + Right) / 2, dy = p.Y - (Front + Back) / 2;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            // The centre neighbourhood scales down for small lots; it never extends beyond 2 m.
            string report = distance <= Math.Min(2, Math.Min(Width, Depth) * 0.05)
                ? Loc.Get("layout_near_centre") : Loc.Get("layout_from_centre", Length(distance, distance < 1 ? "0.#" : "0"), Direction(dx, dy));
            double edgeDistance = double.PositiveInfinity;
            for (int i = 0; i < Points.Count; i++)
                edgeDistance = Math.Min(edgeDistance, DistanceToSegment(p, Points[i], Points[(i + 1) % Points.Count]));
            if (edgeDistance <= 2) report += ", " + Loc.Get("layout_near_boundary", Length(edgeDistance));
            return report;
        }

        internal static string Direction(double x, double y)
        {
            string[] keys = { "layout_right", "layout_back_right", "layout_back", "layout_back_left",
                "layout_left", "layout_front_left", "layout_front", "layout_front_right" };
            int sector = ((int)Math.Round(Math.Atan2(y, x) * 4 / Math.PI) + 8) % 8;
            return Loc.Get(keys[sector]);
        }

        internal static string Length(double metres, string format = "0.#") =>
            ScreenReader.NormalizeForOutput(MeasurementUnitService.FormatLength((float)metres, format));

        private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        private static double DistanceToSegment(Point p, Point a, Point b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, length2 = dx * dx + dy * dy;
            double t = length2 > 0 ? Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / length2)) : 0;
            return Distance(p, new Point(a.X + t * dx, a.Y + t * dy));
        }
    }
}
