using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>A face-relative frame read from a native straight wall mesh without openings.</summary>
    internal sealed class WallFaceFrame
    {
        internal Vector3 Origin, Right, Normal;
        internal float Width, Height;
        internal Vector3 Point(float along, float height) => Origin + Right * along + Vector3.up * height;
        internal float Along(Vector3 point) => Vector3.Dot(point - Origin, Right);
        internal float AboveBottom(Vector3 point) => point.y - Origin.y;
        internal bool Same(WallFaceFrame other) => other != null
            && Vector3.Distance(Origin, other.Origin) < 0.002f && Vector3.Distance(Normal, other.Normal) < 0.001f
            && Math.Abs(Width - other.Width) < 0.002f && Math.Abs(Height - other.Height) < 0.002f;

        internal static bool TryCreate(Vector3[] vertices, Vector3 normal, out WallFaceFrame frame)
        {
            frame = null;
            if (vertices == null || vertices.Length < 4 || !Finite(normal) || normal.magnitude < 0.99f
                || Math.Abs(normal.normalized.y) > 0.001f) return false;
            normal = normal.normalized;
            Vector3 right = Vector3.Cross(Vector3.up, normal).normalized;
            float min = float.PositiveInfinity, max = float.NegativeInfinity;
            float bottom = float.PositiveInfinity, top = float.NegativeInfinity;
            foreach (var point in vertices)
            {
                if (!Finite(point) || Math.Abs(Vector3.Dot(point - vertices[0], normal)) > 0.002f) return false;
                float along = Vector3.Dot(point - vertices[0], right);
                min = Math.Min(min, along); max = Math.Max(max, along);
                bottom = Math.Min(bottom, point.y); top = Math.Max(top, point.y);
            }
            if (max - min < 0.01f || top - bottom < 0.01f) return false;
            // Only rectangular faces are offered. A bounding rectangle of a sloping or irregular
            // face would promise positions which are not on that wall.
            int corners = 0;
            foreach (var point in vertices)
            {
                float along = Vector3.Dot(point - vertices[0], right);
                if (Math.Min(Math.Abs(along - min), Math.Abs(along - max)) > 0.002f
                    || Math.Min(Math.Abs(point.y - bottom), Math.Abs(point.y - top)) > 0.002f) return false;
                corners |= 1 << ((Math.Abs(along - min) < 0.002f ? 0 : 1) + (Math.Abs(point.y - bottom) < 0.002f ? 0 : 2));
            }
            if (corners != 15) return false;
            frame = new WallFaceFrame { Origin = vertices[0] + right * min + Vector3.up * (bottom - vertices[0].y),
                Right = right, Normal = normal, Width = max - min, Height = top - bottom };
            return true;
        }

        internal static bool Finite(Vector3 v) => PlacementGrid.Finite(v.x) && PlacementGrid.Finite(v.y) && PlacementGrid.Finite(v.z);
    }

    /// <summary>Live native room/wall-face identities; coordinates are derived only when requested.</summary>
    internal sealed class WallFaceTarget
    {
        internal ZoneWallObject Room;
        internal SegmentWallSectionRenderer Face;
        internal int RoomId, WallId, SectionId;

        internal bool TryRead(ulong lot, int layer, out WallFaceFrame frame)
        {
            frame = null;
            var section = Face != null ? Face.WallSection : null;
            var wall = section != null ? section.Wall : null;
            if (!WallFaceReader.Usable(wall, lot, layer) || wall.InstanceID != WallId
                || section.InstanceID != SectionId || !wall.WallSections.Contains(section)
                || section.IsFence || section.DisabledByUser || section.DisabledButVisible
                || !section.gameObject.activeInHierarchy || !Face.gameObject.activeInHierarchy
                || (Face != section.SideA && Face != section.SideB)) return false;
            if (RoomId >= 0 && Room == null) return false;
            if (Room != null && (Room.InstanceID != RoomId || !Room.gameObject.activeInHierarchy
                || Room.LotPlacedOnGUID != lot || Room.Layer != layer || !ZoneManager.Instance.GetLotZones(lot).Contains(Room)
                || !Room.Segments.Any(e => e.Segment == wall && (e.FromAToB ? section.SideA : section.SideB) == Face))) return false;
            var mesh = Face.Mesh;
            if (mesh == null || mesh.normals == null || mesh.normals.Length == 0) return false;
            Vector3 normal = Face.transform.TransformDirection(mesh.normals[0]).normalized;
            if (mesh.normals.Any(n => Vector3.Dot(normal, Face.transform.TransformDirection(n).normalized) < 0.999f)) return false;
            return WallFaceFrame.TryCreate(mesh.vertices.Select(Face.transform.TransformPoint).ToArray(), normal, out frame);
        }
    }

    internal sealed class WallFaceGroup
    {
        internal ZoneWallObject Room;
        internal readonly List<WallFaceTarget> Faces = new List<WallFaceTarget>();
    }

    /// <summary>Enumerates room-facing sides and remaining lot sides without inventing room names.</summary>
    internal static class WallFaceReader
    {
        internal static bool Usable(SegmentWallObject wall, ulong lot, int layer) => wall != null
            && wall.gameObject.activeInHierarchy && wall.LotPlacedOnGUID == lot && wall.Layer == layer
            && wall.PlacementPlayer == -1 && !wall.IsCurved && wall.ReplacingSegment == null
            && SegmentManager.Instance.GetLotSegments(lot).Contains(wall);

        internal static List<WallFaceGroup> Read(ulong lot, int layer)
        {
            var result = new List<WallFaceGroup>();
            var assigned = new HashSet<SegmentWallSectionRenderer>();
            foreach (var room in ZoneManager.Instance.GetLotZones(lot).OfType<ZoneWallObject>().OrderBy(r => r.InstanceID))
            {
                if (!room.gameObject.activeInHierarchy || room.LotPlacedOnGUID != lot || room.Layer != layer) continue;
                var group = new WallFaceGroup { Room = room };
                foreach (var edge in room.Segments)
                    if (edge.Segment is SegmentWallObject wall && Usable(wall, lot, layer))
                        foreach (var section in wall.WallSections.OrderBy(s => s.StartHeight))
                            Add(group, edge.FromAToB ? section.SideA : section.SideB, lot, layer, assigned);
                if (group.Faces.Count > 0) result.Add(group);
            }
            var outside = new WallFaceGroup();
            foreach (var wall in SegmentManager.Instance.GetLotSegments(lot).OfType<SegmentWallObject>().OrderBy(w => w.InstanceID))
                if (Usable(wall, lot, layer))
                    foreach (var section in wall.WallSections.OrderBy(s => s.StartHeight))
                    {
                        Add(outside, section.SideA, lot, layer, assigned);
                        Add(outside, section.SideB, lot, layer, assigned);
                    }
            if (outside.Faces.Count > 0) result.Add(outside);
            return result;
        }

        private static void Add(WallFaceGroup group, SegmentWallSectionRenderer face, ulong lot, int layer,
            HashSet<SegmentWallSectionRenderer> assigned)
        {
            if (face == null || assigned.Contains(face)) return;
            var target = new WallFaceTarget { Room = group.Room, RoomId = group.Room != null ? group.Room.InstanceID : -1,
                Face = face, SectionId = face.WallSection.InstanceID, WallId = face.WallSection.Wall.InstanceID };
            if (!target.TryRead(lot, layer, out _)) return;
            group.Faces.Add(target); assigned.Add(face);
        }
    }
}
