using System;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>Applies an explicit geometric facing request through the native rotation action.</summary>
    internal static class FacingTargetHandler
    {
        private static ItemObjectRoot _item, _target;
        private static ItemObjectInPlacement _preview;
        private static Vector3 _targetPosition;
        private static ulong _lot;
        private static int _layer;
        private static int _itemId, _targetId;
        internal static void Clear() { _item = _target = null; _preview = null; }

        internal static bool LiveTarget(ItemObjectRoot item, ItemObjectRoot target) => item != null && target != null
            && item != target && target.isActiveAndEnabled && target.CubeTransform != null
            && target.LotPlacedOnGUID == item.LotPlacedOnGUID && target.Layer == item.Layer
            && ItemManager.Instance.GetItemByInstanceID(target.InstanceID) == target
            && ItemManager.Instance.GetLotItems(target.LotPlacedOnGUID).Contains(target)
            && !ExistingItemEditHandler.MovesWith(item, target);

        internal static bool Direction(ItemObjectRoot item, ItemObjectRoot target, out Vector3 direction)
        {
            direction = Vector3.zero;
            if (!LiveTarget(item, target)) return false;
            direction = target.GetWorldBoundingBox().Center - item.GetWorldBoundingBox().Center;
            direction.y = 0;
            return WallFaceFrame.Finite(direction) && direction.magnitude > 0.01f;
        }

        internal static string Face(HybridPlayer hybrid, ItemObjectInPlacement preview, ItemObjectRoot target)
        {
            var item = preview?.Item;
            if (item == null || hybrid?.Player?.ItemInPlacement != preview || !ItemPlacementHandler.OwnsInput
                || !hybrid.Player.PlayerActionsAreAllowed || !HouseholdManager.Instance.OwnsLot(item.LotPlacedOnGUID)
                || hybrid.Player.ItemInRotation != null || preview.FromRotationWidget || preview.IsInOffset
                || item.IsWallItem || item.IsCeilingItem || item.IsLotMovementItem
                || !Direction(item, target, out var direction) || !WallFaceFrame.Finite(item.transform.eulerAngles)
                || Vector3.Dot(item.transform.up, Vector3.up) < 0.999f) return Loc.Get("facing_unavailable");
            if (item.PlacementSlotPlacedOn != 0 || item.ChairSlotPlacedOn != 0 || item.ItemAttachedTo != null)
                return Loc.Get("facing_slot_controls");
            SlotPlacementHandler.Clear();
            Quaternion desired = Quaternion.LookRotation(direction.normalized, Vector3.up);
            if (Quaternion.Angle(item.transform.rotation, desired) > 0.1f)
            {
                // The same native action used by EndRotateItem updates the item, carried children,
                // materials, floor holes and navmesh. New previews do not create an undo purchase.
                var action = new RotateItemAction { Item = item,
                    RotationDifference = Quaternion.Inverse(item.transform.rotation) * desired,
                    GameFrame = Time.frameCount };
                if (!preview.IsNewItem) action.ActionID = UndoRedoManager.Instance.GenerateID(hybrid.Player.PlayerIndex);
                action.Redo();
                if (!preview.IsNewItem) UndoRedoManager.Instance.AddAction(action, hybrid.Player.PlayerIndex);
                hybrid.Player.LastItemRotation = item.transform.rotation;
            }
            _item = item; _target = target; _preview = preview; _lot = item.LotPlacedOnGUID; _layer = item.Layer;
            _itemId = item.InstanceID; _targetId = target.InstanceID;
            _targetPosition = target.GetWorldBoundingBox().Center;
            ItemPlacementHandler.ReviewAfterAction(hybrid, preview);
            Plugin.Log.LogInfo($"[Facing] request item={item.InstanceID}, target={target.InstanceID}, forward={item.transform.forward}.");
            return Loc.Get("facing_requested", ItemDescription.GetProductIdentity(target));
        }

        internal static string Problem(HybridPlayer hybrid, ItemObjectRoot item)
        {
            if (_item == null || item != _item) return null;
            if (hybrid?.Player?.ItemInPlacement != _preview || item.InstanceID != _itemId || _target == null || _target.InstanceID != _targetId
                || item.LotPlacedOnGUID != _lot || item.Layer != _layer
                || !Direction(item, _target, out var direction)
                || Vector3.Distance(_target.GetWorldBoundingBox().Center, _targetPosition) > 0.01f)
                return Loc.Get("facing_changed");
            float angle = Vector3.Angle(item.transform.forward, direction);
            return PlacementGrid.Finite(angle) && angle <= 0.5f ? null : Loc.Get("facing_mismatch", angle.ToString("0.0"));
        }

        internal static string Describe(HybridPlayer hybrid, ItemObjectRoot item)
        {
            if (item != _item || _item == null) return "";
            return Problem(hybrid, item) ?? Loc.Get("facing_verified", ItemDescription.GetProductIdentity(_target));
        }
    }
}
