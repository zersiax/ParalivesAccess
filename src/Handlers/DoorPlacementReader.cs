using Setting;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>Reads native floor height for doorway guidance; UpdateItemDoor remains authoritative.</summary>
    internal static class DoorPlacementReader
    {
        internal static bool IsDoor(ItemObjectRoot item) => item != null && item.IsWallItem && item.GetComponent<ItemObjectDoor>() != null;

        internal static bool TryFloor(WallFaceTarget face, out float floor)
        {
            floor = 0;
            var wall = face?.Face?.WallSection?.Wall;
            if (wall == null || WallHeightManager.Instance == null) return false;
            floor = WallHeightManager.Instance.GetFloorHeight(wall.LotPlacedOnGUID, wall.Cluster, wall.Layer) * Settings.Get<Wall>().StepHeight;
            return PlacementGrid.Finite(floor);
        }

        internal static string Describe(ItemObjectRoot item, WallFaceTarget face)
        {
            if (!IsDoor(item)) return "";
            if (!TryFloor(face, out float floor)) return Loc.Get("door_floor_unknown");
            float gap = item.CubeTransform.LocalPosition.y - item.CubeTransform.FinalSize.y / 2 - floor;
            if (!PlacementGrid.Finite(gap)) return Loc.Get("door_floor_unknown");
            return Loc.Get("door_threshold", PlacementGridReader.Length(gap), item.SnapsToBottomOfWall
                ? Loc.Get("door_native_height") : Loc.Get("door_manual_height"));
        }
    }
}
