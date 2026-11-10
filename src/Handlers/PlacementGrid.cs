using System;

namespace ParalivesAccess.Handlers
{
    /// <summary>
    /// A mod-owned coordinate frame over continuous world X/Z positions. It describes requested
    /// points, never native occupancy, snapping, or permission to place an item.
    /// </summary>
    internal sealed class PlacementGrid
    {
        internal double OriginX { get; }
        internal double OriginZ { get; }
        internal double Rotation { get; }
        internal double Spacing { get; }
        private readonly double _cos;
        private readonly double _sin;

        internal PlacementGrid(double originX, double originZ, double rotation, double spacing)
        {
            if (!Finite(originX) || !Finite(originZ) || !Finite(rotation)
                || !Finite(spacing) || spacing <= 0)
                throw new ArgumentOutOfRangeException(nameof(spacing), "Invalid placement grid frame.");
            OriginX = originX;
            OriginZ = originZ;
            Rotation = rotation;
            Spacing = spacing;
            _cos = Math.Cos(rotation * Math.PI / 180);
            _sin = Math.Sin(rotation * Math.PI / 180);
        }

        internal void Coordinates(double x, double z, out double column, out double row)
        {
            double dx = x - OriginX;
            double dz = z - OriginZ;
            column = (dx * _cos + dz * _sin) / Spacing;
            row = (-dx * _sin + dz * _cos) / Spacing;
        }

        internal void World(double column, double row, out double x, out double z)
        {
            x = OriginX + Spacing * (column * _cos - row * _sin);
            z = OriginZ + Spacing * (column * _sin + row * _cos);
        }

        /// <summary>
        /// Moves to the next grid line in each requested direction, preserving the untouched axis.
        /// An off-grid starting position reaches the next line first; subsequent steps are uniform.
        /// Fine movement uses fifths of the same grid, without changing its origin or addresses.
        /// </summary>
        internal void Step(double x, double z, int horizontal, int vertical, bool fine,
            out double targetX, out double targetZ)
        {
            Step(x, z, horizontal, vertical, fine ? 0.2 : 1, out targetX, out targetZ);
        }

        /// <summary>Uses an explicit subdivision for a native construction grid.</summary>
        internal void Step(double x, double z, int horizontal, int vertical, double increment,
            out double targetX, out double targetZ)
        {
            if (!Finite(increment) || increment <= 0) throw new ArgumentOutOfRangeException(nameof(increment));
            Coordinates(x, z, out double column, out double row);
            // Unity returns world positions as floats. At large coordinates their roundoff is
            // larger than a fixed grid-coordinate epsilon, even though our frame math is double.
            // Bound the two input components' float rounding error in subdivision coordinates.
            double epsilon = Math.Min(0.25, Math.Max(0.00001,
                (Math.Abs(x) + Math.Abs(z)) / 16777216.0 / Spacing / increment));
            column = NextLine(column, horizontal, increment, epsilon);
            row = NextLine(row, vertical, increment, epsilon);
            World(column, row, out targetX, out targetZ);
        }

        private static double NextLine(double value, int direction, double increment, double epsilon)
        {
            if (direction > 0) return (Math.Floor(value / increment + epsilon) + 1) * increment;
            if (direction < 0) return (Math.Ceiling(value / increment - epsilon) - 1) * increment;
            return value;
        }

        internal bool SameFrame(PlacementGrid other)
        {
            return other != null && Math.Abs(OriginX - other.OriginX) < 0.0001
                && Math.Abs(OriginZ - other.OriginZ) < 0.0001
                && Math.Abs(Rotation - other.Rotation) < 0.0001
                && Math.Abs(Spacing - other.Spacing) < 0.0001;
        }

        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
