using System;
using System.Collections.Generic;
using System.Globalization;

namespace ParalivesAccess.Handlers
{
    /// <summary>Metre dimensions and a roof-aligned enclosing rectangle; not a room classification.</summary>
    internal static class RoofGeometry
    {
        internal struct Dimensions { internal double Width, Depth, Height; }
        internal struct Point
        {
            internal double X, Z;
            internal Point(double x, double z) { X = x; Z = z; }
        }
        internal struct Plan { internal double X, Z, Width, Depth; }
        internal static bool Finite(double n) => !double.IsNaN(n) && !double.IsInfinity(n);
        internal static bool TryParse(string text, out Dimensions size)
        {
            size = default(Dimensions);
            var parts = (text ?? "").Split(';');
            if (parts.Length != 3) return false;
            var values = new double[3];
            const NumberStyles style = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint
                | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite;
            for (int i = 0; i < 3; i++)
                if (!(double.TryParse(parts[i], style, CultureInfo.CurrentCulture, out values[i])
                    || double.TryParse(parts[i], style, CultureInfo.InvariantCulture, out values[i]))
                    || !Finite(values[i]) || values[i] <= 0 || values[i] > 1000) return false;
            size = new Dimensions { Width = values[0], Depth = values[1], Height = values[2] };
            return true;
        }

        internal static bool ValidAxis(double metres, double scale, double current, double min, double max, bool resizable)
        {
            if (!Finite(metres) || metres <= 0 || metres > 1000 || !Finite(scale) || scale <= 0
                || !Finite(current) || !Finite(min) || min < 0 || double.IsNaN(max) || max < min) return false;
            double local = metres / scale;
            return Finite(local) && local >= min - 0.00001 && local <= max + 0.00001
                && (resizable || Math.Abs(local - current) < 0.00001);
        }

        internal static bool TryPlan(IReadOnlyList<Point> points, double yaw, double overhang, out Plan plan)
        {
            plan = default(Plan);
            if (points == null || points.Count < 3 || !Finite(yaw) || !Finite(overhang) || overhang < 0 || overhang > 10)
                return false;
            double cos = Math.Cos(yaw * Math.PI / 180), sin = Math.Sin(yaw * Math.PI / 180);
            double x1 = double.PositiveInfinity, z1 = x1, x2 = double.NegativeInfinity, z2 = x2, area = 0;
            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i]; var next = points[(i + 1) % points.Count];
                if (!Finite(p.X) || !Finite(p.Z)) return false;
                double x = cos * p.X - sin * p.Z, z = sin * p.X + cos * p.Z;
                x1 = Math.Min(x1, x); x2 = Math.Max(x2, x); z1 = Math.Min(z1, z); z2 = Math.Max(z2, z);
                area += p.X * next.Z - next.X * p.Z;
            }
            double width = x2 - x1 + overhang * 2, depth = z2 - z1 + overhang * 2;
            if (!Finite(area) || Math.Abs(area) < 0.0001 || !Finite(width) || !Finite(depth)
                || width <= 0 || depth <= 0 || width > 1000 || depth > 1000) return false;
            double mx = (x1 + x2) / 2, mz = (z1 + z2) / 2;
            plan = new Plan { X = cos * mx + sin * mz, Z = -sin * mx + cos * mz, Width = width, Depth = depth };
            return Finite(plan.X) && Finite(plan.Z);
        }
    }
}
