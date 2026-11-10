using ParalivesAccess.Speech;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ParalivesAccess.Handlers
{
    /// <summary>Explicit, one-shot native confirmation of an unchanged reviewed preview.</summary>
    internal static class NativePlacementAttempt
    {
        private static Player _player, _submittingPlayer;
        private static ItemObjectInPlacement _preview;
        private static Vector3 _position, _size, _offset;
        private static Quaternion _rotation;
        private static GameObject _support;
        private static int _layer, _viewLayer, _frame, _submittedFrame = -1, _price;
        private static ulong _lot;
        private static InputButton _button;
        internal static bool IsSubmitting(Player player) => player == _submittingPlayer && _submittedFrame == Time.frameCount;

        internal static string Problem(HybridPlayer h, ItemObjectInPlacement preview)
        {
            var p = h?.Player; var root = preview?.Item;
            if (p == null || p.ItemInPlacement != preview || root == null || p.State != GameStates.BuildMode
                || !p.PlayerActionsAreAllowed || p.PlayerActionBlockedByUIWindow || h.HasInputFieldFocused || h.IsUsingGamePad
                || preview.FromRotationWidget || preview.IsInOffset || p.ItemInRotation != null || CoordinateEntryHandler.BlocksPlacement)
                return Loc.Get("native_attempt_unavailable");
            if (!WallFaceFrame.Finite(root.CubeTransform.LocalPosition) || !WallFaceFrame.Finite(root.CubeTransform.FinalSize)
                || !HouseholdManager.Instance.OwnsLot(root.LotPlacedOnGUID)) return Loc.Get("placement_lot_required");
            if (root.IsPositionIllegal || root.IsWallItemNotOnWall || root.IsCeilingItemNotOnCeiling || root.IsHoleItemOnCurvedWall || root.IsHoleItemOnFence)
                return Loc.Get("native_attempt_illegal");
            string floor = PlacementSupportConsent.FloorProblem(preview);
            if (floor != null) return floor;
            if (InputManager.Instance.IsPointerOverUI(p.PlayerIndex)) return Loc.Get("placement_pointer_over_ui");
            if (preview.IsNewItem && preview.InventoryItem == null && !MoneyManager.Instance.CanPurchase(root.GetPrice()))
                return Loc.Get("wall_item_unaffordable", FormatService.GetMoney(root.GetPrice()));
            return null;
        }

        internal static string Arm(HybridPlayer h, ItemObjectInPlacement preview)
        {
            string problem = Problem(h, preview); if (problem != null) return problem;
            RoomFitSearch.Cancel();
            SlotPlacementHandler.Clear(); ItemPlacementHandler.ReviewAfterAction(h, preview);
            FacingTargetHandler.Clear(); ItemAlignmentHandler.Clear();
            _player = h.Player; _preview = preview; var root = preview.Item;
            _position = root.CubeTransform.LocalPosition; _rotation = root.transform.rotation; _size = root.CubeTransform.FinalSize;
            _offset = root.ItemOffset?.Offset ?? Vector3.zero; _support = preview.LastCollidedObject;
            _lot = root.LotPlacedOnGUID; _layer = root.Layer; _viewLayer = _player.GetLotLayer(_lot); _price = root.GetPrice(); _frame = Time.frameCount;
            return Loc.Get("native_attempt_armed", PlacementLocationReader.Target(root), PlacementGridReader.Dimensions(root));
        }

        private static bool Same(HybridPlayer h, ItemObjectInPlacement preview) => h?.Player == _player && preview == _preview
            && preview?.Item != null && preview.Item.LotPlacedOnGUID == _lot && preview.Item.Layer == _layer
            && _player.GetLotLayer(_lot) == _viewLayer && preview.Item.GetPrice() == _price
            && Vector3.Distance(preview.Item.CubeTransform.LocalPosition, _position) < .01f
            && Vector3.Distance(preview.Item.CubeTransform.FinalSize, _size) < .001f
            && Vector3.Distance(preview.Item.ItemOffset?.Offset ?? Vector3.zero, _offset) < .001f
            && Quaternion.Angle(preview.Item.transform.rotation, _rotation) < .1f && preview.LastCollidedObject == _support;

        internal static bool Before(HybridPlayer h, ItemObjectInPlacement preview)
        {
            if (_preview == null) return false;
            var k = Keyboard.current;
            bool enter = k != null && (k.enterKey.wasReleasedThisFrame || k.numpadEnterKey.wasReleasedThisFrame);
            if (!Same(h, preview) || CameraPitchHandler.ConsumedThisFrame)
            { Clear(); Speak(Loc.Get("native_attempt_changed")); return enter; }
            if (h.ButtonCancel.Down || h.ButtonUndo.Down || h.ButtonRedo.Down || k == null)
            { Clear(); return false; }
            if (k.leftArrowKey.wasPressedThisFrame || k.rightArrowKey.wasPressedThisFrame || k.upArrowKey.wasPressedThisFrame
                || k.downArrowKey.wasPressedThisFrame || k.f10Key.wasPressedThisFrame || k.f11Key.wasPressedThisFrame
                || h.ButtonSmallRotateItemLeft.Down || h.ButtonSmallRotateItemRight.Down || h.ButtonLargeRotateItem.Down)
            { Clear(); return false; }
            ItemPlacementHandler.MaintainWorldCursor(h,preview);
            if (!enter) return true;
            if (k.ctrlKey.isPressed || k.shiftKey.isPressed || k.altKey.isPressed) { Speak(Loc.Get("slot_release_modifiers")); return true; }
            string problem = Problem(h, preview);
            if (problem != null) { Clear(); Speak(problem); return true; }
            if (preview.CursorTeleported || Time.frameCount <= _frame + 1) { Speak(Loc.Get("placement_wait")); return true; }
            _submittingPlayer = h.Player; _submittedFrame = Time.frameCount;
            _button = h.ButtonConfirm; _button.SetValue(0f);
            Plugin.Log.LogInfo($"[NativePlacement] one-shot request root={preview.Item.InstanceID}, lot={_lot}, layer={_layer}, point={_position}.");
            return true;
        }

        internal static void AfterGameSystems()
        {
            if (_button != null)
            {
                _button.Update(); _button = null;
                bool placed = _player?.ItemInPlacement != _preview;
                Clear(); if (!placed) Speak(Loc.Get("native_attempt_not_placed")); return;
            }
            if (_preview != null)
            {
                var h = PlayerManager.Instance?.HybridPlayer1;
                if (!Same(h, h?.Player?.ItemInPlacement) || h.HasInputFieldFocused || h.Player.PlayerActionBlockedByUIWindow
                    || h.Player.State != GameStates.BuildMode) Clear();
            }
        }
        internal static void Cancel() => Clear();
        private static void Clear() { _player = null; _preview = null; }
        private static void Speak(string text) => ScreenReader.Speak(text);
    }
}
