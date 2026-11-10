using System;

namespace ParalivesAccess.Handlers
{
    /// <summary>Support-scope checks for new walls; native validation remains authoritative.</summary>
    internal static class ConstructionGeometry
    {
        /// <summary>True for positive collinear overlap, excluding an ordinary shared endpoint.</summary>
        internal static bool Overlaps(double ax, double az, double bx, double bz,
            double cx, double cz, double dx, double dz)
        {
            double vx = bx - ax, vz = bz - az;
            double length = Math.Sqrt(vx * vx + vz * vz);
            if (length < 0.001) return false;
            vx /= length; vz /= length;
            const double tolerance = 0.002; // World metres, independent of grid spacing.
            if (Math.Abs((cx - ax) * vz - (cz - az) * vx) > tolerance
                || Math.Abs((dx - ax) * vz - (dz - az) * vx) > tolerance) return false;
            double c = (cx - ax) * vx + (cz - az) * vz;
            double d = (dx - ax) * vx + (dz - az) * vz;
            return Math.Min(length, Math.Max(c, d)) - Math.Max(0, Math.Min(c, d)) > tolerance;
        }
    }
}
