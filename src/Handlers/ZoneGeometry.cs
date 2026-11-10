using System;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>Horizontal membership in the game's sampled boundary, for mod descriptions and
    /// navigation. Native placement validation and transactions remain authoritative.</summary>
    internal static class ZoneGeometry
    {
        internal static bool Contains(ZoneObject zone, Vector3 point)
        {
            if (zone?.Segments == null || zone.Segments.Count < 2 || !Finite(point)) return false;
            bool inside = false, boundary = false;
            int edges = 0;
            foreach (var entry in zone.Segments)
            {
                var points = entry.Segment?.SegmentPoints;
                if (points == null || points.Count < 2) return false;
                for (int i = 1; i < points.Count; i++)
                {
                    var a = points[i - 1]; var b = points[i];
                    if (!Finite(a) || !Finite(b)) return false;
                    double dx = (double)b.x - a.x, dz = (double)b.z - a.z;
                    double length2 = dx * dx + dz * dz;
                    if (length2 < 1e-12) continue;
                    edges++;
                    double t = Math.Max(0, Math.Min(1,
                        (((double)point.x - a.x) * dx + ((double)point.z - a.z) * dz) / length2));
                    double ex = point.x - (a.x + t * dx), ez = point.z - (a.z + t * dz);
                    boundary |= ex * ex + ez * ez <= 0.000001; // One millimetre on the boundary.
                    // Half-open crossings count a shared vertex once. No fixed ray origin is
                    // assumed to lie outside the lot, unlike MathService.ZoneObjectContainsPoint.
                    if ((a.z > point.z) != (b.z > point.z)
                        && point.x < a.x + dx * ((double)point.z - a.z) / dz)
                        inside = !inside;
                }
            }
            return edges >= 3 && (boundary || inside);
        }

        private static bool Finite(Vector3 p) => !float.IsNaN(p.x) && !float.IsInfinity(p.x)
            && !float.IsNaN(p.z) && !float.IsInfinity(p.z);
    }
}
