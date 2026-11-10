using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ParalivesAccess.Speech;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>Starts native existing-item moves and observes their commit/cancel lifecycle.</summary>
    internal static class ExistingItemEditHandler
    {
        internal static bool Enabled { get; set; } = true;
        private static ItemObjectInPlacement _tracked;
        private static bool _committed, _cancelled;
        private static int _confirmFrame = -1;
        private static InputButton _injected;

        internal static bool ApplyPatches(Harmony harmony)
        {
            try
            {
                foreach (var type in new[] { typeof(EndMoveItem), typeof(CancelMoveItem) })
                {
                    var method = AccessTools.Method(type, "UpdateForPlayer", new[] { typeof(Player) });
                    var prefix = AccessTools.Method(typeof(ExistingItemEditHandler), type == typeof(EndMoveItem) ? nameof(BeforeEnd) : nameof(Capture));
                    var postfix = AccessTools.Method(typeof(ExistingItemEditHandler), type == typeof(EndMoveItem) ? nameof(AfterEnd) : nameof(AfterCancel));
                    var cleanup = AccessTools.Method(typeof(ExistingItemEditHandler), nameof(Cleanup));
                    harmony.Patch(method, prefix: new HarmonyMethod(prefix), postfix: new HarmonyMethod(postfix), finalizer: new HarmonyMethod(cleanup));
                    var info = Harmony.GetPatchInfo(method);
                    if (info == null || !info.Prefixes.Any(p => p.PatchMethod == prefix && p.owner == Plugin.PluginGuid)
                        || !info.Postfixes.Any(p => p.PatchMethod == postfix && p.owner == Plugin.PluginGuid)
                        || !info.Finalizers.Any(p => p.PatchMethod == cleanup && p.owner == Plugin.PluginGuid)) return false;
                }
                return true;
            }
            catch (Exception ex) { Plugin.Log.LogError("Existing-item lifecycle hooks failed: " + ex); return false; }
        }

        internal static bool CanBegin(HybridPlayer hybrid, ItemObjectRoot item) => Enabled && ItemPlacementHandler.Enabled
            && hybrid?.Player != null && hybrid.Player.State == GameStates.BuildMode && hybrid.Player.PlayerActionsAreAllowed
            && !hybrid.HasInputFieldFocused && !hybrid.IsUsingGamePad && !hybrid.Player.PlayerActionBlockedByUIWindow
            && hybrid.Player.ItemInPlacement == null && hybrid.Player.ItemStartingPlacement == null && hybrid.Player.ItemInRotation == null
            && item != null && item.isActiveAndEnabled && item.CubeTransform != null && !item.IsLotMovementItem && (!item.IsCeilingItem || CeilingPlacementHandler.Enabled)
            && HouseholdManager.Instance != null && HouseholdManager.Instance.OwnsLot(item.LotPlacedOnGUID)
            && ItemManager.Instance.GetItemByInstanceID(item.InstanceID) == item
            && ItemManager.Instance.GetLotItems(item.LotPlacedOnGUID).Contains(item);

        internal static bool Begin(HybridPlayer hybrid, ItemObjectRoot item)
        {
            if (!CanBegin(hybrid, item)) { ScreenReader.Speak(UnavailableReason(hybrid, item)); return false; }
            if (!WorldTargeting.FrameItem(item, hybrid.Player, "edit-existing"))
            { ScreenReader.Speak(Loc.Get("explorer_frame_failed")); return false; }
            // Same public handoff as UpdateSelectedItemUI.OnMoveClicked. StartMoveItem captures the
            // original action, disables colliders, detaches supports and creates the actual preview.
            hybrid.Player.ItemStartingPlacement = item;
            item.MovedItemFromUISelected = true;
            ScreenReader.Speak(Loc.Get("edit_started", ItemDescription.GetProductIdentity(item)));
            return true;
        }

        internal static string UnavailableReason(HybridPlayer hybrid, ItemObjectRoot item)
        {
            var player = hybrid?.Player;
            if (!Enabled || !ItemPlacementHandler.Enabled) return Loc.Get("edit_disabled");
            if (player == null || player.State != GameStates.BuildMode) return Loc.Get("edit_build_required");
            if (player.ItemInPlacement != null || player.ItemStartingPlacement != null) return Loc.Get("edit_finish_placement");
            if (hybrid.HasInputFieldFocused || player.PlayerActionBlockedByUIWindow) return Loc.Get("edit_close_window");
            if (player.ItemInRotation != null) return Loc.Get("edit_finish_rotation");
            if (hybrid.IsUsingGamePad) return Loc.Get("edit_keyboard_required");
            if (item == null || !item.isActiveAndEnabled || item.CubeTransform == null
                || ItemManager.Instance?.GetItemByInstanceID(item.InstanceID) != item
                || !ItemManager.Instance.GetLotItems(item.LotPlacedOnGUID).Contains(item)) return Loc.Get("edit_target_gone");
            if (item.IsLotMovementItem) return Loc.Get("edit_type_unsupported");
            if (item.IsCeilingItem && !CeilingPlacementHandler.Enabled) return Loc.Get("edit_ceiling_disabled");
            if (HouseholdManager.Instance?.OwnsLot(item.LotPlacedOnGUID) != true) return Loc.Get("edit_owned_lot");
            return Loc.Get("edit_unavailable");
        }

        internal static bool CanTrack(HybridPlayer hybrid, ItemObjectInPlacement preview) => Enabled
            && hybrid?.Player?.State == GameStates.BuildMode && preview?.Item != null && !preview.IsNewItem
            && preview.OriginalMoveItemAction != null && (!preview.Item.IsCeilingItem || CeilingPlacementHandler.Enabled) && !preview.Item.IsLotMovementItem
            && HouseholdManager.Instance?.OwnsLot(preview.Item.PreviousLotPlacedOnGUID) == true;

        internal static void Track(ItemObjectInPlacement preview)
        { _tracked = preview; _committed = _cancelled = false; _confirmFrame = -1; }

        internal static void Clear() { _tracked = null; _confirmFrame = -1; _committed = _cancelled = false; }
        internal static bool WasCommitted => _committed;
        internal static void QueueConfirm() { _confirmFrame = Time.frameCount; }

        private static void Capture(Player player, out ItemObjectInPlacement __state)
        { __state = player.ItemInPlacement == _tracked ? _tracked : null; }

        private static void BeforeEnd(Player player, out ItemObjectInPlacement __state)
        {
            Capture(player, out __state);
            if (__state == null || _confirmFrame != Time.frameCount) return;
            _confirmFrame = -1;
            var hybrid = PlayerManager.Instance.HybridPlayer1;
            if (hybrid?.Player != player || !ItemPlacementHandler.OwnsInput || CoordinateEntryHandler.BlocksPlacement
                || hybrid.ButtonCancel.Down || hybrid.ButtonUndo.Down || hybrid.ButtonRedo.Down
                || hybrid.ButtonConfirm.Down || hybrid.ButtonConfirm.Up || hybrid.ButtonConfirm.Pressed) return;
            string problem = ItemPlacementHandler.SemanticPlacementProblem(hybrid, __state.Item);
            if (problem != null) { ScreenReader.Speak(problem); return; }
            try { _injected = hybrid.ButtonConfirm; _injected.SetValue(0f); }
            catch (Exception ex) { Cleanup(); Plugin.Log.LogError("Existing-item confirmation failed: " + ex); ScreenReader.Speak(Loc.Get("edit_unverified")); }
        }

        private static void AfterEnd(Player player, ItemObjectInPlacement __state)
        {
            if (__state != null && player.ItemInPlacement != __state) _committed = true;
            LinkedGroupRecovery.AfterEnd(player, __state, _committed);
        }

        private static void AfterCancel(Player player, ItemObjectInPlacement __state)
        {
            if (__state != null && player.ItemInPlacement != __state) _cancelled = true;
            LinkedGroupRecovery.AfterCancel(player, __state);
        }

        private static void Cleanup()
        { if (_injected == null) return; var button = _injected; _injected = null; button.Update(); }

        internal static string DescribeCancelled(ItemObjectRoot item)
        {
            var old = _tracked?.OriginalMoveItemAction;
            bool restored = _cancelled && item != null && old != null
                && Vector3.Distance(item.CubeTransform.LocalPosition, _tracked.OriginalPosition) < 0.01f
                && Quaternion.Angle(item.transform.rotation, _tracked.OriginalRotation) < 0.1f
                && item.LotPlacedOnGUID == item.PreviousLotPlacedOnGUID
                && item.ItemPlacedOn == old.OldItemPlacedOn && item.ItemAttachedTo == old.OldItemAttachedTo
                && item.WallSectionPlacedOn == old.OldWall && item.FloorPlacedOn == old.OldFloorPlacedOn
                && item.PlacementSlotPlacedOn == old.OldPlacementSlot && item.ChairSlotPlacedOn == old.OldChairSlot
                && (item.ItemPlacedOn == null || item.ItemPlacedOn.ItemsOnThis.Contains(item))
                && (item.ItemAttachedTo == null || item.ItemAttachedTo.ItemsAttachedToThis.Contains(item))
                && (item.WallSectionPlacedOn == null || item.WallSectionPlacedOn.ItemsOnThis.Contains(item))
                && (item.FloorPlacedOn == null || item.FloorPlacedOn.ItemsOnThis.Contains(item))
                && (old.OldPlacementSlot == 0 || (item.ItemPlacedOn != null
                    && item.ItemPlacedOn.ItemPlacementSlots.TryGetValue(old.OldPlacementSlot, out var surface)
                    && surface != null && surface.Item?.Root == item.ItemPlacedOn && surface.ItemOnInstanceID == item.InstanceID))
                && (old.OldChairSlot == 0 || (item.ItemAttachedTo != null
                    && item.ItemAttachedTo.ItemChairSlots.TryGetValue(old.OldChairSlot, out var chair)
                    && chair != null && chair.Item?.Root == item.ItemAttachedTo && chair.ItemInSlotInstanceID == item.InstanceID));
            return Loc.Get(restored ? "edit_cancelled" : "edit_unverified");
        }

        internal static bool MovesWith(ItemObjectRoot root, ItemObjectRoot candidate)
        {
            var visited = new HashSet<ItemObjectRoot>();
            var pending = new Stack<ItemObjectRoot>(); pending.Push(root);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (current == null || !visited.Add(current)) continue;
                if (current == candidate) return true;
                foreach (var child in current.ItemsOnThis) pending.Push(child);
                foreach (var child in current.ItemsAttachedToThis) pending.Push(child);
            }
            return false;
        }
    }
}
