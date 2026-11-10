using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>Describes live room contents in a frame explicitly chosen by the user.</summary>
    internal static class RoomSpatialReader
    {
        private sealed class Reference
        {
            internal WallFaceTarget Face;
            internal WallFaceFrame Frame;
        }
        private static readonly Dictionary<ZoneWallObject, Reference> References = new Dictionary<ZoneWallObject, Reference>();

        internal static bool IsUsableRoom(ZoneWallObject room, ulong lot) => room != null && room.gameObject != null
            && room.gameObject.activeInHierarchy && !room.IsGlobal && room.LotPlacedOnGUID == lot
            && room.Segments != null && room.Segments.Count >= 2 && PlacementGrid.Finite(room.AveragePosition.x);

        internal static int CompareRooms(ZoneWallObject a, ZoneWallObject b)
        {
            // Native room IDs increase at creation and are restored from the save. Position
            // ordering inserted a new left-hand room before its older, possibly named neighbor.
            // Count every room on the floor, including named rooms, in native creation order.
            return a.InstanceID.CompareTo(b.InstanceID);
        }

        internal static List<ZoneWallObject> OrderedRooms(ulong lot, int layer)
        {
            var rooms = ZoneManager.Instance.GetLotZones(lot).OfType<ZoneWallObject>()
                .Where(r => IsUsableRoom(r, lot) && r.Layer == layer).ToList();
            rooms.Sort(CompareRooms); return rooms;
        }

        internal static bool TryReference(ZoneWallObject room, out WallFaceFrame frame)
        {
            frame = null;
            return room != null && References.TryGetValue(room, out var reference)
                && reference.Face.TryRead(room.LotPlacedOnGUID, room.Layer, out frame) && reference.Frame.Same(frame);
        }

        internal static List<WallFaceTarget> Faces(ZoneWallObject room) => room == null ? new List<WallFaceTarget>()
            : WallFaceReader.Read(room.LotPlacedOnGUID, room.Layer).FirstOrDefault(g => g.Room == room)?.Faces ?? new List<WallFaceTarget>();

        internal static string DescribeFace(WallFaceTarget face, int index)
        {
            var room = face.Room;
            if (room == null || !face.TryRead(room.LotPlacedOnGUID, room.Layer, out var frame)) return Loc.Get("room_reference_changed");
            string address = PlacementGridReader.TryRead(room.LotPlacedOnGUID, ItemPlacementHandler.GridSpacingMetres, out var grid)
                ? PlacementGridReader.Position(grid, frame.Origin) + ", " + PlacementGridReader.Position(grid, frame.Point(frame.Width, 0))
                : Loc.Get("placement_grid_unavailable");
            return Loc.Get("room_reference_face", index, PlacementGridReader.Length(frame.Width), address)
                + ". " + DescribeSide(face, frame);
        }

        internal static string DescribeSide(WallFaceTarget face, WallFaceFrame frame = null)
        {
            if (face?.Face?.WallSection?.Wall == null || (frame == null && !face.TryRead(face.Face.WallSection.Wall.LotPlacedOnGUID,
                face.Face.WallSection.Wall.Layer, out frame))) return Loc.Get("room_wall_direction_unavailable");
            var wall = face.Face.WallSection.Wall;
            string side = LotSide(wall.LotPlacedOnGUID, frame);
            if (face.Room != null) return Loc.Get("wall_side_room", side, RoomLabelHandler.Name(face.Room));
            var inside = WallFaceReader.Read(wall.LotPlacedOnGUID, wall.Layer).Where(g => g.Room != null)
                .SelectMany(g => g.Faces).FirstOrDefault(f => f.WallId == face.WallId && f.SectionId == face.SectionId);
            if (inside != null && inside.TryRead(wall.LotPlacedOnGUID, wall.Layer, out var interior))
                return Loc.Get("wall_side_exterior", Loc.Get("wall_side_room", LotSide(wall.LotPlacedOnGUID, interior), RoomLabelHandler.Name(inside.Room)));
            return Loc.Get("wall_side_outside", side);
        }

        internal static string SetReference(ZoneWallObject room, WallFaceTarget face)
        {
            if (room == null || face?.Room != room || !face.TryRead(room.LotPlacedOnGUID, room.Layer, out var frame))
                return Loc.Get("room_reference_changed");
            foreach (var stale in References.Keys.Where(r => r == null || !r.gameObject.activeInHierarchy).ToList()) References.Remove(stale);
            References[room] = new Reference { Face = face, Frame = frame };
            return Loc.Get("room_reference_set");
        }

        internal static string Side(WallFaceFrame reference, WallFaceFrame face)
        {
            float along = Vector3.Dot(face.Normal, reference.Right), depth = Vector3.Dot(face.Normal, reference.Normal);
            if (depth > 0.99f) return Loc.Get("room_front_wall");
            if (depth < -0.99f) return Loc.Get("room_back_wall");
            if (along > 0.99f) return Loc.Get("room_left_wall");
            if (along < -0.99f) return Loc.Get("room_right_wall");
            return Loc.Get("room_angled_wall");
        }

        // Extension choices use the same fixed lot directions as J and ordinary build arrows.
        // A separately chosen advanced front-wall reference must not silently turn these labels.
        internal static string LotSide(ulong lot, WallFaceFrame face)
        {
            if (face == null || !PlacementGridReader.TryRead(lot, 1, out var grid))
                return Loc.Get("room_wall_direction_unavailable");
            double angle = grid.Rotation * Math.PI / 180;
            double inwardX = face.Normal.x * Math.Cos(angle) + face.Normal.z * Math.Sin(angle);
            double inwardZ = -face.Normal.x * Math.Sin(angle) + face.Normal.z * Math.Cos(angle);
            if (inwardZ > 0.99) return Loc.Get("room_front_wall");
            if (inwardZ < -0.99) return Loc.Get("room_back_wall");
            if (inwardX > 0.99) return Loc.Get("room_left_wall");
            if (inwardX < -0.99) return Loc.Get("room_right_wall");
            return Loc.Get(inwardZ > 0
                ? (inwardX > 0 ? "room_front_left_wall" : "room_front_right_wall")
                : (inwardX > 0 ? "room_back_left_wall" : "room_back_right_wall"));
        }

        // Use the same room/lot frame as the position report, independent of the camera.
        internal static string Facing(ItemObjectRoot item, WallFaceFrame frame)
        {
            if (item == null || frame == null) return Loc.Get("room_facing_unavailable");
            var forward = item.transform.TransformDirection(new Vector3(0, 0, 1));
            if (!WallFaceFrame.Finite(forward)) return Loc.Get("room_facing_unavailable");
            double x = Vector3.Dot(forward, frame.Right), z = Vector3.Dot(forward, frame.Normal);
            if (x * x + z * z < .000001) return Loc.Get("room_facing_unavailable");
            const double cardinal = .414213562373095; // Half of an eight-direction sector.
            if (Math.Abs(x) <= Math.Abs(z) * cardinal)
                return Loc.Get("room_facing_wall", Loc.Get(z >= 0 ? "room_back_wall" : "room_front_wall"));
            if (Math.Abs(z) <= Math.Abs(x) * cardinal)
                return Loc.Get("room_facing_wall", Loc.Get(x >= 0 ? "room_right_wall" : "room_left_wall"));
            return Loc.Get("room_facing_corner", Loc.Get(z >= 0 ? "room_back" : "room_front"),
                Loc.Get(x >= 0 ? "room_right" : "room_left"));
        }

        internal static string Region(float fraction, string first, string middle, string last) =>
            Loc.Get(fraction < 1f / 3f ? first : fraction > 2f / 3f ? last : middle);

        internal static string Describe(ZoneWallObject room)
        {
            if (room == null || !References.TryGetValue(room, out var reference)) return Loc.Get("room_reference_required");
            if (!reference.Face.TryRead(room.LotPlacedOnGUID, room.Layer, out var frame) || !reference.Frame.Same(frame))
            { References.Remove(room); return Loc.Get("room_reference_changed"); }
            return DescribeContents(room, frame, false);
        }

        // J uses a common lot frame and short positions. The existing F7 reference-wall report
        // keeps its precise distances; both paths use the same native item membership/attachments.
        internal static string DescribeContents(ZoneWallObject room, WallFaceFrame frame, bool concise)
        {
            var faces = Faces(room);
            var spans = new List<WallSpanTarget>();
            foreach (var face in faces)
            {
                if (spans.Any(s => s.Members.Any(m => m.Face == face.Face))) continue;
                var span = WallSpanReader.Build(faces, face, room.LotPlacedOnGUID, room.Layer);
                if (span == null) return Loc.Get("room_reference_changed");
                spans.Add(span);
            }
            var frames = spans.Select(s => s.Frame).ToList();
            if (room.Segments == null || room.Segments.Any(e => e.Segment?.SegmentPoints == null)) return Loc.Get("placement_geometry_unavailable");
            var points = room.Segments.SelectMany(e => e.Segment.SegmentPoints).ToList();
            if (points == null || points.Count < 3 || points.Any(p => !WallFaceFrame.Finite(p))) return Loc.Get("placement_geometry_unavailable");
            float minX = points.Min(p => frame.Along(p)), maxX = points.Max(p => frame.Along(p));
            float minZ = points.Min(p => Vector3.Dot(p - frame.Origin, frame.Normal)), maxZ = points.Max(p => Vector3.Dot(p - frame.Origin, frame.Normal));
            if (maxX - minX < 0.01f || maxZ - minZ < 0.01f) return Loc.Get("placement_geometry_unavailable");
            var descriptions = new List<string>();
            foreach (var item in ItemManager.Instance.GetLotItems(room.LotPlacedOnGUID).OrderBy(i => i != null ? i.InstanceID : -1))
            {
                if (item == null || !item.isActiveAndEnabled || item.CubeTransform == null || item.Layer != room.Layer
                    || item.LotPlacedOnGUID != room.LotPlacedOnGUID || PlayerManager.Instance?.HybridPlayer1?.Player?.ItemInPlacement?.Item == item) continue;
                int wallIndex = spans.FindIndex(s => s.Members.Any(f => f.Face.WallSection == item.WallSectionPlacedOn
                    && (item.CenterToWallThickness || item.WallSideOn == f.Face.WallSide)));
                bool inside = item.FloorPlacedOn?.WallZone == room || ZoneGeometry.Contains(room, item.CubeTransform.LocalPosition);
                if (wallIndex < 0 && (!inside || item.WallSectionPlacedOn != null)) continue;
                var corners = ItemFootprintReader.Corners(item);
                if (corners.Count == 0) continue;
                Vector3 center = item.GetWorldBoundingBox().Center;
                string location;
                if (wallIndex >= 0)
                {
                    var wall = frames[wallIndex]; float along = wall.Along(center);
                    string position = Math.Abs(along - wall.Width / 2) <= 0.01f ? Loc.Get("room_centred")
                        : Loc.Get("room_wall_distance", PlacementGridReader.Length(along), PlacementGridReader.Length(wall.Width));
                    location = concise ? Loc.Get("layout_on_wall", Side(frame, wall))
                        : Loc.Get("room_on_wall", position, Side(frame, wall), wallIndex + 1);
                }
                else
                {
                    string horizontal = Region((frame.Along(center) - minX) / (maxX - minX), "room_left", "room_centre", "room_right");
                    string depth = Region((Vector3.Dot(center - frame.Origin, frame.Normal) - minZ) / (maxZ - minZ), "room_front", "room_middle", "room_back");
                    location = Loc.Get(concise ? "layout_region" : "room_region", depth, horizontal);
                    float nearest = 0.101f; int nearWall = -1;
                    for (int i = 0; i < frames.Count; i++)
                    {
                        var wall = frames[i];
                        float gap = corners.Min(p => Vector3.Dot(p - wall.Origin, wall.Normal));
                        if (gap >= -0.01f && gap < nearest && corners.Max(wall.Along) >= 0 && corners.Min(wall.Along) <= wall.Width)
                        { nearest = Math.Max(0, gap); nearWall = i; }
                    }
                    if (nearWall >= 0) location += ", " + (concise ? Loc.Get("layout_against_wall", Side(frame, frames[nearWall]))
                        : Loc.Get("room_near_wall", Side(frame, frames[nearWall]), nearWall + 1, PlacementGridReader.Length(nearest)));
                }
                if (item.ItemPlacedOn != null) location += ", " + Loc.Get("room_on_item", ItemDescription.GetProductIdentity(item.ItemPlacedOn));
                if (item.ItemAttachedTo != null) location += ", " + Loc.Get("room_attached_item", ItemDescription.GetProductIdentity(item.ItemAttachedTo));
                location += ", " + Facing(item, frame);
                descriptions.Add(Loc.Get("room_item", ItemDescription.GetProductIdentity(item) ?? Loc.Get("item_unknown"), location));
            }
            bool partial = room.Segments.Any(e => e.Segment is SegmentWallObject w && (w.IsCurved
                || w.WallSections.Any(s => !s.IsFence && !s.DisabledByUser && !s.DisabledButVisible
                    && !faces.Any(f => f.Face.WallSection == s))));
            if (concise)
            {
                string report = Loc.Get("layout_contents", descriptions.Count);
                if (partial) report += ". " + Loc.Get("layout_contents_partial");
                return report + (descriptions.Count > 0 ? ". " + string.Join(". ", descriptions) : "");
            }
            string walls = string.Join(". ", frames.Select((f, i) => Loc.Get("room_wall_size", Side(frame, f), i + 1, PlacementGridReader.Length(f.Width))));
            return Loc.Get("room_summary", descriptions.Count, PlacementGridReader.Length(maxX - minX), PlacementGridReader.Length(maxZ - minZ))
                + (partial ? ". " + Loc.Get("layout_contents_partial") : "") + ". " + walls + ". " + string.Join(". ", descriptions);
        }
    }
}
