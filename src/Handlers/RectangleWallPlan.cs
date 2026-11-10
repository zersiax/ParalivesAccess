using System;

namespace ParalivesAccess.Handlers
{
    /// <summary>
    /// Four requested boundary edges in a fixed grid frame. Completion counts native commits;
    /// this plan never constructs geometry, charges money, groups undo, or declares a native room.
    /// </summary>
    internal sealed class RectangleWallPlan
    {
        internal PlacementGrid Grid { get; }
        internal double MinColumn { get; }
        internal double MaxColumn { get; }
        internal double MinRow { get; }
        internal double MaxRow { get; }
        internal double Width => (MaxColumn - MinColumn) * Grid.Spacing;
        internal double Depth => (MaxRow - MinRow) * Grid.Spacing;
        internal int CompletedEdges { get; private set; }
        internal bool Complete => CompletedEdges == 4;
        private int _startEdge;
        internal void StartAt(int edge) { if (CompletedEdges != 0 || edge < 0 || edge > 3) throw new ArgumentOutOfRangeException(nameof(edge)); _startEdge = edge; }

        private RectangleWallPlan(PlacementGrid grid, double c1, double r1, double c2, double r2)
        {
            Grid = grid;
            MinColumn = Math.Min(c1, c2); MaxColumn = Math.Max(c1, c2);
            MinRow = Math.Min(r1, r2); MaxRow = Math.Max(r1, r2);
        }

        internal static bool TryCreate(PlacementGrid grid, double x1, double z1, double x2, double z2,
            out RectangleWallPlan plan)
        {
            plan = null;
            if (grid == null || !PlacementGrid.Finite(x1) || !PlacementGrid.Finite(z1)
                || !PlacementGrid.Finite(x2) || !PlacementGrid.Finite(z2)) return false;
            grid.Coordinates(x1, z1, out double c1, out double r1);
            grid.Coordinates(x2, z2, out double c2, out double r2);
            // A guided rectangle uses full native grid intersections. Native Shift and snapping
            // remain available for individual walls without silently changing a reviewed plan.
            if (!OnLine(c1, grid.Spacing) || !OnLine(r1, grid.Spacing)
                || !OnLine(c2, grid.Spacing) || !OnLine(r2, grid.Spacing)) return false;
            c1 = Math.Round(c1); r1 = Math.Round(r1); c2 = Math.Round(c2); r2 = Math.Round(r2);
            if (c1 == c2 || r1 == r2) return false;
            plan = new RectangleWallPlan(grid, c1, r1, c2, r2);
            return true;
        }

        // Dimensions are rounded to the nearest supported wall-grid size. The caller must
        // announce the resulting Width/Depth and obtain review before starting any wall.
        internal static bool TryCreateSized(PlacementGrid grid, double centerX, double centerZ,
            double width, double depth, out RectangleWallPlan plan)
        {
            plan = null;
            if (grid == null || !PlacementGrid.Finite(centerX) || !PlacementGrid.Finite(centerZ)
                || !PlacementGrid.Finite(width) || !PlacementGrid.Finite(depth) || width <= 0 || depth <= 0)
                return false;
            double columns = Math.Max(1, Math.Round(width / grid.Spacing, MidpointRounding.AwayFromZero));
            double rows = Math.Max(1, Math.Round(depth / grid.Spacing, MidpointRounding.AwayFromZero));
            grid.Coordinates(centerX, centerZ, out double c, out double r);
            double left = Math.Round(c - columns / 2), bottom = Math.Round(r - rows / 2);
            plan = new RectangleWallPlan(grid, left, bottom, left + columns, bottom + rows);
            return true;
        }

        internal void Point(int index, out double x, out double z)
        {
            if (index < 0 || index > 4) throw new ArgumentOutOfRangeException(nameof(index));
            int corner = (index + _startEdge) % 4;
            Grid.World(corner == 1 || corner == 2 ? MaxColumn : MinColumn,
                corner == 2 || corner == 3 ? MaxRow : MinRow, out x, out z);
        }

        // Strict interior intersection, including concave-lot notches and walls whose endpoints
        // both lie outside the proposed room. Shared boundary edges themselves are excluded.
        internal bool CrossesInterior(double ax, double az, double bx, double bz)
        {
            if (!PlacementGrid.Finite(ax) || !PlacementGrid.Finite(az) || !PlacementGrid.Finite(bx) || !PlacementGrid.Finite(bz)) return true;
            Grid.Coordinates(ax, az, out double x, out double z);
            Grid.Coordinates(bx, bz, out double endX, out double endZ);
            double t0 = 0, t1 = 1, epsilon = 0.002 / Grid.Spacing;
            return Clip(x, endX - x, MinColumn + epsilon, MaxColumn - epsilon, ref t0, ref t1)
                && Clip(z, endZ - z, MinRow + epsilon, MaxRow - epsilon, ref t0, ref t1);
        }

        private static bool Clip(double start, double delta, double min, double max, ref double low, ref double high)
        {
            if (Math.Abs(delta) < 1e-12) return start >= min && start <= max;
            double a = (min - start) / delta, b = (max - start) / delta;
            low = Math.Max(low, Math.Min(a, b)); high = Math.Min(high, Math.Max(a, b));
            return high >= low;
        }

        internal void AcceptEdge()
        {
            if (Complete) throw new InvalidOperationException("All rectangle edges are already committed.");
            CompletedEdges++;
        }

        internal bool IsBoundaryEdge(double ax, double az, double bx, double bz)
        {
            if (!PlacementGrid.Finite(ax) || !PlacementGrid.Finite(az)
                || !PlacementGrid.Finite(bx) || !PlacementGrid.Finite(bz)) return false;
            Grid.Coordinates(ax, az, out double ac, out double ar);
            Grid.Coordinates(bx, bz, out double bc, out double br);
            const double tolerance = 0.02; // In grid units; used only to identify the resulting room.
            bool inside = ac >= MinColumn - tolerance && ac <= MaxColumn + tolerance
                && bc >= MinColumn - tolerance && bc <= MaxColumn + tolerance
                && ar >= MinRow - tolerance && ar <= MaxRow + tolerance
                && br >= MinRow - tolerance && br <= MaxRow + tolerance;
            return inside && ((Near(ac, MinColumn) && Near(bc, MinColumn))
                || (Near(ac, MaxColumn) && Near(bc, MaxColumn))
                || (Near(ar, MinRow) && Near(br, MinRow))
                || (Near(ar, MaxRow) && Near(br, MaxRow)));
        }

        private static bool Near(double a, double b) => Math.Abs(a - b) <= 0.02;
        // Native cursor coordinates are floats. Accept two millimetres of projection roundoff,
        // independent of lot rotation, distance from the world origin, or grid spacing.
        private static bool OnLine(double value, double spacing) => Math.Abs(value - Math.Round(value)) * spacing <= 0.002;
    }
}
