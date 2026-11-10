using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>Keyboard placement requires an explicit choice before accepting an object as support.</summary>
    internal static class PlacementSupportConsent
    {
        private static ItemObjectInPlacement _preview;
        private static ItemObjectRoot _owner;
        private static int _ownerId;
        private static Vector3 _position;
        private static Vector3 _ownerPosition, _ownerSize;
        private static Quaternion _ownerRotation;
        internal static void Clear() { _preview = null; _owner = null; }
        internal static string FloorProblem(ItemObjectInPlacement preview)
        {
            var root = preview?.Item;
            if (root == null || root.IsWallItem || root.IsCeilingItem || root.IsLotMovementItem || RoofConstructionHandler.IsRoof(root)) return null;
            Vector3 position = root.CubeTransform.LocalPosition;
            if (!WallFaceFrame.Finite(position)) return Loc.Get("placement_below_floor");
            var hits = RaycastSystem.CastAllGetAll(new Vector3(position.x, 500f, position.z), Vector3.down, 1000f,
                includeTerrain: true, includeFloor: true, includeWalls: false, includeObjects: false, includeInvisibleFloors: true);
            foreach (var hit in hits)
            {
                if (hit.gameObject == null) continue;
                if (hit.colliderType == ColliderType.Floor)
                {
                    var floor = hit.gameObject.GetComponent<ZoneWallPlatformRenderer>()?.WallZonePlatform;
                    if (floor?.WallZone == null || floor.DisabledByUser || floor.DisabledButVisible
                        || floor.WallZone.LotPlacedOnGUID != root.LotPlacedOnGUID
                        || floor.WallZone.Layer + (floor.IsCeiling ? 1 : 0) != root.Layer) continue;
                }
                else
                {
                    if (hit.colliderType != ColliderType.Terrain
                        || LotManager.Instance.GetLotByGUID(root.LotPlacedOnGUID)?.Data.LayerOffset != root.Layer) continue;
                    var terrain = RaycastSystem.Cast(new Vector3(position.x, 500f, position.z), Vector3.down, 1000f,
                        includeTerrain: true, includeFloor: false, includeWalls: false, includeObjects: false);
                    if (terrain.hit.collider == null || RaycastSystem.CheckIfTerrainHole(terrain.hit)) continue;
                }
                // Check the actual pivot against real support, including any native offset.
                // Subtracting ItemOffset here would incorrectly approve a buried object.
                if (position.y < hit.point.y - .05f) return Loc.Get("placement_below_floor");
            }
            return null;
        }
        internal static void Begin(ItemObjectInPlacement preview)
        {
            Clear();
            if (preview?.Item != null && !preview.IsNewItem && preview.Item.ItemPlacedOn != null) Capture(preview, preview.Item.ItemPlacedOn);
        }
        private static void Capture(ItemObjectInPlacement preview, ItemObjectRoot owner)
        {
            _preview = preview; _owner = owner; _ownerId = owner.InstanceID;
            _position = preview.Item.CubeTransform.LocalPosition;
            _ownerPosition = owner.CubeTransform.LocalPosition;
            _ownerSize = owner.CubeTransform.FinalSize;
            _ownerRotation = owner.transform.rotation;
        }
        internal static ItemObjectRoot Current(ItemObjectInPlacement preview)
        {
            if (preview?.Item == null || preview.Item.IsWallItem || preview.Item.IsCeilingItem || RoofConstructionHandler.IsRoof(preview.Item)
                || preview.LastColliderType != ColliderType.Object || preview.LastCollidedObject == null) return null;
            var root = preview.LastCollidedObject.GetComponent<ItemObjectRoot>();
            return root != null && root != preview.Item && root.isActiveAndEnabled && root.LotPlacedOnGUID == preview.Item.LotPlacedOnGUID
                && root.Layer == preview.Item.Layer && ItemManager.Instance.GetItemByInstanceID(root.InstanceID) == root ? root : null;
        }
        internal static string Describe(ItemObjectInPlacement preview)
        {
            var owner = Current(preview);
            if (owner == null) return null;
            float y = preview.Item.CubeTransform.LocalPosition.y - WallHeightManager.Instance.GetFloorWorldY(preview.Item.LotPlacedOnGUID, preview.Item.Layer);
            return Loc.Get("placement_support", ItemDescription.GetProductIdentity(owner) ?? Loc.Get("item_unknown"), PlacementGridReader.Length(y));
        }
        internal static string Problem(ItemObjectInPlacement preview)
        {
            if (SlotPlacementHandler.Active) return null;
            var owner = Current(preview);
            if (owner == null) return null;
            if (preview == _preview && owner == _owner && owner.InstanceID == _ownerId
                && Vector3.Distance(owner.CubeTransform.LocalPosition, _ownerPosition) <= .001f
                && Vector3.Distance(owner.CubeTransform.FinalSize, _ownerSize) <= .001f
                && Quaternion.Angle(owner.transform.rotation, _ownerRotation) <= .1f
                && Vector3.Distance(preview.Item.CubeTransform.LocalPosition, _position) <= .02f) return null;
            return Describe(preview) + ". " + Loc.Get("placement_support_review");
        }
        internal static string Approve(HybridPlayer hybrid, ItemObjectInPlacement preview)
        {
            var owner = Current(preview);
            if (owner == null || hybrid?.Player?.ItemInPlacement != preview || !hybrid.Player.PlayerActionsAreAllowed
                || preview.Item.IsPositionIllegal) return Loc.Get("placement_support_changed");
            Capture(preview, owner);
            Plugin.Log.LogInfo($"[Support] approved item={preview.Item.InstanceID}, owner={owner.InstanceID}, point={_position}.");
            return Describe(preview) + ". " + Loc.Get("placement_support_approved");
        }
    }
}
