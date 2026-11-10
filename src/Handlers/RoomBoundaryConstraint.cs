using System;
using System.Linq;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>Optional room containment, independent of the chosen movement grid.</summary>
    internal static class RoomBoundaryConstraint
    {
        internal static bool Available { get; set; }
        // Remember the toggle for this game session; each new preview captures its own room.
        internal static bool Enabled { get; set; }
        private static ItemObjectInPlacement _preview;
        private static ZoneWallObject _room;
        private static ulong _lot;
        private static int _layer, _roomId;
        private static Vector3 _start;
        private static Vector3[] _boundary;
        private static string _name;
        private static bool _pending;
        private static bool _hasNativeSample;
        private static Vector3 _lastInside, _lastAim;
        private static Vector2 _lastCursor;

        private static bool Updating => !BuildModeRefreshManager.IsZoneStateRefreshed
            || ZoneManager.Instance == null || ZoneManager.Instance.DirtyWallZones.Count > 0;

        internal static void Clear()
        {
            _preview = null; _room = null; _boundary = null; _name = null; _pending = false;
            _hasNativeSample = false;
        }

        internal static void Begin(ItemObjectInPlacement preview, ulong lot, int layer, Vector3 start)
        {
            Clear();
            if (!Available || !Enabled || preview?.Item == null || preview.Item.IsWallItem
                || preview.Item.IsCeilingItem || preview.Item.IsLotMovementItem) return;
            _preview = preview; _lot = lot; _layer = layer; _start = start; _pending = true;
            ResolveRoom();
        }

        private static void ResolveRoom()
        {
            if (!_pending || Updating) return;
            var rooms = RoomSpatialReader.OrderedRooms(_lot, _layer);
            int index = rooms.FindIndex(room => ZoneGeometry.Contains(room, _start));
            _pending = false;
            if (index < 0) return; // A new outdoor preview has no room to confine it to.
            _room = rooms[index]; _roomId = _room.InstanceID;
            _name = RoomLabelHandler.Name(_room, index + 1);
            _boundary = ReadBoundary(_room);
        }

        private static Vector3[] ReadBoundary(ZoneWallObject room)
        {
            if (!SpatialOutline.TryRead(room, new PlacementGrid(0, 0, 0, 1), out _)) return null;
            return room.Segments.SelectMany(e => e.Segment.SegmentPoints).ToArray();
        }

        internal static string Toggle(ItemObjectInPlacement preview)
        {
            if (Enabled) { Enabled = false; Clear(); return Loc.Get("room_stay_disabled"); }
            if (!Available) return Loc.Get("room_stay_unavailable");
            if (Updating) return Loc.Get("layout_updating");
            var item = preview?.Item;
            if (item?.CubeTransform == null || item.IsWallItem || item.IsCeilingItem || item.IsLotMovementItem)
                return Loc.Get("room_stay_no_room");
            Enabled = true;
            Begin(preview, item.LotPlacedOnGUID, item.Layer, item.CubeTransform.LocalPosition);
            if (_room == null || _boundary == null)
            { Enabled = false; Clear(); return Loc.Get("room_stay_no_room"); }
            return Loc.Get("room_stay_enabled", _name);
        }

        internal static string Option()
        {
            ResolveRoom();
            return !Enabled ? Loc.Get("room_stay_off") : _room != null
                ? Loc.Get("room_stay_on", _name) : Loc.Get("room_stay_on_next");
        }

        internal static string Describe()
        {
            if (!Enabled || _preview == null) return null;
            ResolveRoom();
            var item = _preview.Item;
            if (item?.CubeTransform != null)
            {
                string problem = Problem(item.LotPlacedOnGUID, item.Layer, item.CubeTransform.LocalPosition);
                if (problem != null) return problem;
            }
            return _pending ? Loc.Get("layout_updating") : _room != null
                ? Loc.Get("room_stay_enabled", _name) : Loc.Get("room_stay_outdoors");
        }

        internal static string Problem(ulong lot, int layer, Vector3 point)
        {
            if (!Enabled || _preview == null) return null;
            ResolveRoom();
            if (_pending) return Loc.Get("layout_updating");
            if (_room == null) return null;
            if (Updating) return Loc.Get("layout_updating");
            if (lot != _lot || layer != _layer || _room.InstanceID != _roomId
                || !RoomSpatialReader.IsUsableRoom(_room, _lot) || _room.Layer != _layer
                || !ZoneManager.Instance.GetLotZones(_lot).Contains(_room)) return Loc.Get("room_stay_changed");
            var current = ReadBoundary(_room);
            if (_boundary == null || current == null || current.Length != _boundary.Length
                || current.Where((p, i) => Vector3.Distance(p, _boundary[i]) > 0.002f).Any())
                return Loc.Get("room_stay_changed");
            return ZoneGeometry.Contains(_room, point) ? null : Loc.Get("room_stay_edge", _name);
        }

        // Save the cursor input that actually produced an indoor result, not just a proposed
        // world point. Projecting that result again can introduce the same rounding error.
        internal static bool ObserveNative(ItemObjectInPlacement preview, Vector3 aim, Vector2 cursor,
            out Vector3 restoreAim, out Vector2 restoreCursor, out string problem)
        {
            restoreAim = default(Vector3); restoreCursor = default(Vector2); problem = null;
            if (!Enabled || preview != _preview || preview?.Item?.CubeTransform == null) return false;
            ResolveRoom();
            if (_room == null) return false;
            var item = preview.Item;
            problem = Problem(item.LotPlacedOnGUID, item.Layer, item.CubeTransform.LocalPosition);
            if (problem == null)
            {
                _lastInside = item.CubeTransform.LocalPosition; _lastAim = aim; _lastCursor = cursor;
                _hasNativeSample = true;
                return false;
            }
            // Never restore against stale geometry, a different lot/floor, or a deleted room.
            if (!_hasNativeSample || Problem(item.LotPlacedOnGUID, item.Layer, _lastInside) != null) return false;
            restoreAim = _lastAim; restoreCursor = _lastCursor;
            return true;
        }

        // EndMoveItem sees the actual native result. A snap or a physical mouse move must not
        // turn an indoor keyboard request into an unnoticed outdoor purchase/move.
        internal static bool AllowsNativeCommit(Player player)
        {
            if (!Enabled || _preview == null || player != PlayerManager.Instance?.HybridPlayer1?.Player
                || player.ItemInPlacement != _preview) return true;
            ResolveRoom();
            if (_room == null && !_pending) return true;
            var item = _preview.Item;
            return item?.CubeTransform != null && player.GetLotLayer(_lot) == _layer
                && Problem(item.LotPlacedOnGUID, item.Layer, item.CubeTransform.LocalPosition) == null;
        }
    }
}
