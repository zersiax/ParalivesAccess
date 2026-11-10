using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    internal static class RoomCandidateReader
    {
        internal static IEnumerable<Vector3> Read(ZoneWallObject room, float step, int limit)
        {
            if (room?.Segments == null || limit < 1 || !PlacementGrid.Finite(step) || step <= 0) yield break;
            var vertices = room.Segments.Where(e => e.Segment?.SegmentPoints != null).SelectMany(e => e.Segment.SegmentPoints).ToArray();
            if (vertices.Length < 3) yield break;
            Vector3 centre = room.AveragePosition;
            float minX = vertices.Min(v => v.x), maxX = vertices.Max(v => v.x), minZ = vertices.Min(v => v.z), maxZ = vertices.Max(v => v.z);
            int radius = Math.Min(1000, (int)Math.Ceiling(Math.Max(maxX - minX, maxZ - minZ) / step));
            int count = 0;
            for (int ring = 0; ring <= radius; ring++)
                for (int z = -ring; z <= ring; z++)
                    for (int x = -ring; x <= ring; x++)
                    {
                        if (Math.Max(Math.Abs(x), Math.Abs(z)) != ring) continue;
                        var p = centre + new Vector3(x * step, 0, z * step);
                        if (p.x < minX || p.x > maxX || p.z < minZ || p.z > maxZ || !ZoneGeometry.Contains(room, p)) continue;
                        yield return p;
                        if (++count >= limit) yield break;
                    }
        }
    }
}
