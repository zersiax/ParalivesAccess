using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>A continuous straight face assembled from live native sections without merging game objects.</summary>
    internal sealed class WallSpanTarget
    {
        internal readonly List<WallFaceTarget> Members = new List<WallFaceTarget>();
        internal readonly List<WallFaceFrame> Frames = new List<WallFaceFrame>();
        internal WallFaceFrame Frame;

        internal bool IsLive(ulong lot, int layer)
        {
            if (Members.Count == 0) return false;
            var group = WallFaceReader.Read(lot, layer).FirstOrDefault(g => g.Room == Members[0].Room);
            var current = group == null ? null : WallSpanReader.Build(group.Faces, Members[0], lot, layer);
            return current != null && Frame.Same(current.Frame) && Members.Count == current.Members.Count
                && Members.Select((m, i) => current.Members.Any(n => n.Face == m.Face
                    && Frames[i].Same(current.Frames[current.Members.IndexOf(n)]))).All(match => match);
        }

        internal WallFaceTarget At(float along) => Members.FirstOrDefault(m =>
        {
            int i = Members.IndexOf(m); float start = Frame.Along(Frames[i].Origin);
            return along >= start - 0.01f && along <= start + Frames[i].Width + 0.01f;
        });

        internal bool Contains(SegmentWallSectionObject section) => Members.Any(m => m.Face.WallSection == section);
        internal bool ContainsRenderer(GameObject renderer) => Members.Any(m => m.Face.gameObject == renderer);
    }

    internal static class WallSpanReader
    {
        internal static WallSpanTarget Build(IReadOnlyList<WallFaceTarget> faces, WallFaceTarget seed, ulong lot, int layer)
        {
            if (seed == null || !seed.TryRead(lot, layer, out var basis)) return null;
            var span = new WallSpanTarget(); span.Members.Add(seed); span.Frames.Add(basis);
            bool added;
            do
            {
                added = false;
                foreach (var candidate in faces)
                {
                    if (candidate.Room != seed.Room || span.Members.Any(m => m.Face == candidate.Face)
                        || !candidate.TryRead(lot, layer, out var frame) || !Collinear(basis, frame)) continue;
                    for (int i = 0; i < span.Members.Count; i++)
                    {
                        var a = span.Members[i].Face.WallSection.Wall; var b = candidate.Face.WallSection.Wall;
                        bool connected = a.NodeA != null && (a.NodeA == b.NodeA || a.NodeA == b.NodeB)
                            || a.NodeB != null && (a.NodeB == b.NodeA || a.NodeB == b.NodeB);
                        if (!connected || a.Cluster != b.Cluster || !Touches(span.Frames[i], frame)) continue;
                        span.Members.Add(candidate); span.Frames.Add(frame); added = true; break;
                    }
                }
            } while (added);
            float start = span.Frames.Min(f => basis.Along(f.Origin));
            float end = span.Frames.Max(f => basis.Along(f.Origin) + f.Width);
            span.Frame = new WallFaceFrame { Origin = basis.Point(start, 0), Right = basis.Right,
                Normal = basis.Normal, Width = end - start, Height = basis.Height };
            return span;
        }

        internal static bool Collinear(WallFaceFrame a, WallFaceFrame b) => Vector3.Distance(a.Normal, b.Normal) < 0.001f
            && Math.Abs(a.Origin.y - b.Origin.y) < 0.002f && Math.Abs(a.Height - b.Height) < 0.002f
            && Math.Abs(Vector3.Dot(b.Origin - a.Origin, a.Normal)) < 0.002f;

        private static bool Touches(WallFaceFrame a, WallFaceFrame b) =>
            Vector3.Distance(a.Point(a.Width, 0), b.Origin) < 0.01f || Vector3.Distance(b.Point(b.Width, 0), a.Origin) < 0.01f;

        internal static bool Division(float width, double index, double count, out float along)
        {
            along = 0;
            if (!PlacementGrid.Finite(index) || !PlacementGrid.Finite(count) || count < 1 || count > 100
                || index < 1 || index > count || index != Math.Floor(index) || count != Math.Floor(count)) return false;
            along = (float)(width * index / (count + 1)); return true;
        }
    }
}
