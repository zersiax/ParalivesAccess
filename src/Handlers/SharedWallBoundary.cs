using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>Reviews one fully existing rectangle side without buying or replacing it.</summary>
    internal sealed class SharedWallBoundary
    {
        internal int Edge;
        internal readonly List<SegmentWallObject> Walls = new List<SegmentWallObject>();
        private object[] _snapshot;
        private ulong _lot;
        private int _layer;

        internal static bool Find(RectangleWallPlan plan, ulong lot, int layer, int height, out SharedWallBoundary shared)
        {
            shared = null;
            for (int edge = 0; edge < 4; edge++)
            {
                plan.Point(edge, out double ax, out double az); plan.Point(edge + 1, out double bx, out double bz);
                double length = Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
                var intervals = new List<(double start, double end, SegmentWallObject wall)>();
                foreach (var wall in SegmentManager.Instance.GetLotSegments(lot).OfType<SegmentWallObject>())
                {
                    if (!WallFaceReader.Usable(wall, lot, layer) || wall.NodeA == null || wall.NodeB == null) continue;
                    var a = wall.NodeA.Position; var b = wall.NodeB.Position;
                    if (!ConstructionGeometry.Overlaps(ax, az, bx, bz, a.x, a.z, b.x, b.z)) continue;
                    if (wall.Height != height || wall.WallSections.Count != 1 || wall.WallSections[0].IsFence
                        || wall.WallSections[0].DisabledByUser || wall.WallSections[0].DisabledButVisible
                        || !wall.WallSections[0].gameObject.activeInHierarchy || wall.WallSections[0].StartHeight != 0
                        || wall.WallSections[0].EndHeight != height) return false;
                    double t1 = ((a.x - ax) * (bx - ax) + (a.z - az) * (bz - az)) / length;
                    double t2 = ((b.x - ax) * (bx - ax) + (b.z - az) * (bz - az)) / length;
                    intervals.Add((Math.Min(t1, t2), Math.Max(t1, t2), wall));
                }
                if (intervals.Count == 0) continue;
                // Reusing a partial wall would require native splitting before the other edges exist.
                // Keep this guide limited to one complete existing side with actual endpoint nodes.
                if (shared != null) return false;
                intervals.Sort((a, b) => a.start.CompareTo(b.start));
                double end = 0; SegmentWallObject previous = null;
                foreach (var interval in intervals)
                {
                    if (Math.Abs(interval.start - end) > 0.002 || interval.end > length + 0.002) return false;
                    var w = interval.wall;
                    if (previous != null && (previous.Cluster != w.Cluster || (previous.NodeA != w.NodeA && previous.NodeA != w.NodeB
                        && previous.NodeB != w.NodeA && previous.NodeB != w.NodeB))) return false;
                    end = interval.end; previous = w;
                }
                if (Math.Abs(end - length) > 0.002) return false;
                shared = new SharedWallBoundary { Edge = edge, _lot = lot, _layer = layer };
                shared.Walls.AddRange(intervals.Select(i => i.wall)); shared._snapshot = shared.Capture();
            }
            return true;
        }

        private object[] Capture()
        {
            var values = new List<object>();
            foreach (var wall in Walls)
            {
                if (!WallFaceReader.Usable(wall, _lot, _layer) || wall.NodeA == null || wall.NodeB == null) return new object[0];
                values.AddRange(new object[] { wall, wall.InstanceID, wall.Height, wall.Cluster, wall.NodeA, wall.NodeB, wall.NodeA.Position, wall.NodeB.Position });
                foreach (var s in wall.WallSections) values.AddRange(new object[] { s, s.InstanceID, s.StartHeight, s.EndHeight, s.IsFence, s.DisabledByUser, s.DisabledButVisible, s.gameObject.activeInHierarchy });
            }
            return values.ToArray();
        }

        internal bool IsLive() => _snapshot.Length > 0 && _snapshot.SequenceEqual(Capture());
    }
}
