using System.Linq;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>Finds a real floor point inside a room, away from its boundary.</summary>
    internal static class RoomAnchorReader
    {
        internal static bool TryRead(ZoneWallObject room, Vector3 preferred, out Vector3 point)
        {
            point = preferred;
            if (room == null || !BuildModeRefreshManager.IsZoneStateRefreshed) return false;
            var vertices = room.Segments?.Where(e => e.Segment?.SegmentPoints != null).SelectMany(e => e.Segment.SegmentPoints).ToList();
            if (vertices == null || vertices.Count < 3) return false;
            if (TryPoint(room, preferred, out point)) return true;
            float minX = vertices.Min(v => v.x), maxX = vertices.Max(v => v.x);
            float minZ = vertices.Min(v => v.z), maxZ = vertices.Max(v => v.z);
            foreach (float z in new[] { .5f, .25f, .75f, .125f, .875f })
                foreach (float x in new[] { .5f, .25f, .75f, .125f, .875f })
                    if (TryPoint(room, new Vector3(Mathf.Lerp(minX, maxX, x), preferred.y, Mathf.Lerp(minZ, maxZ, z)), out point)) return true;
            return false;
        }
        internal static bool TryPoint(ZoneWallObject room, Vector3 position, out Vector3 result)
            => TryPoint(room, position, .15f, out result);

        internal static bool TryPoint(ZoneWallObject room, Vector3 position, float margin, out Vector3 result)
        {
            result = position;
            if (!WallFaceFrame.Finite(position) || !ZoneGeometry.Contains(room, position)) return false;
            foreach (var delta in new[] { Vector3.right, Vector3.left, Vector3.forward, Vector3.back })
                if (margin > 0 && !ZoneGeometry.Contains(room, position + delta * margin)) return false;
            var ray = RaycastSystem.CastAllGetAll(new Vector3(position.x, 500f, position.z), Vector3.down, 1000f,
                includeTerrain: true, includeFloor: true, includeWalls: false, includeObjects: false, includeInvisibleFloors: true);
            float y = float.NegativeInfinity;
            foreach (var hit in ray)
            {
                if (hit.gameObject == null) continue;
                Vector3 p = hit.point;
                if (hit.colliderType == ColliderType.Floor)
                {
                    var floor = hit.gameObject.GetComponent<ZoneWallPlatformRenderer>()?.WallZonePlatform;
                    if (floor?.WallZone != room || floor.IsCeiling || floor.DisabledByUser || floor.DisabledButVisible) continue;
                }
                else
                {
                    if (hit.colliderType != ColliderType.Terrain || room.Layer != LotManager.Instance.GetLotByGUID(room.LotPlacedOnGUID)?.Data.LayerOffset) continue;
                    var terrain = RaycastSystem.Cast(new Vector3(position.x, 500f, position.z), Vector3.down, 1000f,
                        includeTerrain: true, includeFloor: false, includeWalls: false, includeObjects: false);
                    if (terrain.hit.collider == null || RaycastSystem.CheckIfTerrainHole(terrain.hit)) continue;
                }
                if (p.y <= y) continue;
                y = p.y; result = p;
            }
            return !float.IsNegativeInfinity(y);
        }
    }
}
