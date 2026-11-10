using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ParalivesAccess.Speech;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ParalivesAccess.Handlers
{
    /// <summary>F11 placement commands share the current preview and never confirm placement themselves.</summary>
    internal static class PlacementActionsHandler
    {
        internal static bool Enabled { get; set; }
        internal static bool Active => _preview != null;
        private static ItemObjectInPlacement _preview;
        private static Player _player;
        private static readonly List<(Func<string> Name, Action Run)> Entries = new List<(Func<string>, Action)>();
        private static int _index, _holdFrame = -1;

        internal static bool ApplyPatches(Harmony harmony)
        {
            RoomBoundaryConstraint.Available = false;
            try
            {
                foreach (var type in new[] { typeof(UpdateMoveItem), typeof(EndMoveItem) })
                {
                    var method = AccessTools.Method(type, "UpdateForPlayer", new[] { typeof(Player) });
                    var prefix = AccessTools.Method(typeof(PlacementActionsHandler), nameof(AllowPlacement));
                    harmony.Patch(method, prefix: new HarmonyMethod(prefix));
                    if (Harmony.GetPatchInfo(method)?.Prefixes.Any(p => p.owner == Plugin.PluginGuid && p.PatchMethod == prefix) != true) return false;
                }
                var end = AccessTools.Method(typeof(EndMoveItem), "UpdateForPlayer", new[] { typeof(Player) });
                var boundary = AccessTools.Method(typeof(PlacementActionsHandler), nameof(AllowRoomBoundaryCommit));
                harmony.Patch(end, prefix: new HarmonyMethod(boundary));
                RoomBoundaryConstraint.Available = Harmony.GetPatchInfo(end)?.Prefixes.Any(
                    p => p.owner == Plugin.PluginGuid && p.PatchMethod == boundary) == true;
                if (!RoomBoundaryConstraint.Available) return false;
                return true;
            }
            catch (Exception ex) { Plugin.Log.LogError("Placement command isolation failed: " + ex); return false; }
        }

        private static bool AllowPlacement(Player player) => player != _player || (!Active && _holdFrame != Time.frameCount);
        private static bool AllowRoomBoundaryCommit(Player player) => NativePlacementAttempt.IsSubmitting(player)
            || (RoofConstructionHandler.AllowsNativeCommit(player) && RoomBoundaryConstraint.AllowsNativeCommit(player));
        internal static void Tick()
        {
            if (!Active) return;
            var hybrid = PlayerManager.Instance?.HybridPlayer1;
            if (hybrid?.Player != _player || _player.ItemInPlacement != _preview || _player.State != GameStates.BuildMode
                || !_player.PlayerActionsAreAllowed || hybrid.HasInputFieldFocused || hybrid.IsUsingGamePad
                || _player.PlayerActionBlockedByUIWindow || _player.ItemInRotation != null || _preview.IsInOffset)
                Clear();
        }
        internal static void Clear() { _preview = null; Entries.Clear(); _index = 0; _holdFrame = Time.frameCount; }
        private static void Add(string key, Action action) => Entries.Add((() => Loc.Get(key), action));
        private static void ResetEntries() { Entries.Clear(); _index = 0; }

        internal static bool Handle(HybridPlayer hybrid, ItemObjectInPlacement preview)
        {
            bool pressed = ModInputKey.WasPressed(Keyboard.current, Key.F11);
            if (!Active && !pressed) return false;
            if (Active && (_preview != preview || hybrid.Player != _player || hybrid.ButtonCancel.Down
                || hybrid.ButtonUndo.Down || hybrid.ButtonRedo.Down || hybrid.ButtonConfirm.Down || hybrid.ButtonConfirm.Up
                || hybrid.ButtonConfirm.Pressed || hybrid.ButtonSmallRotateItemLeft.Down || hybrid.ButtonSmallRotateItemRight.Down
                || hybrid.ButtonLargeRotateItem.Down)) { Clear(); return true; }
            if (pressed)
            {
                if (!ModInputKey.IsAvailable(hybrid, Key.F11, out string conflict)) { Speak(conflict); return true; }
                if (!Enabled) { Speak(Loc.Get("placement_actions_unavailable")); return true; }
                if (Active) { Clear(); Speak(Loc.Get("placement_actions_closed")); return true; }
                if (preview.Item.IsLotMovementItem) { Speak(Loc.Get("facing_unavailable")); return true; }
                _preview = preview; _player = hybrid.Player; Root(hybrid, preview); Speak(Describe()); return true;
            }
            var keyboard = Keyboard.current;
            int move = (keyboard.rightArrowKey.wasPressedThisFrame || keyboard.downArrowKey.wasPressedThisFrame ? 1 : 0)
                - (keyboard.leftArrowKey.wasPressedThisFrame || keyboard.upArrowKey.wasPressedThisFrame ? 1 : 0);
            if (move != 0 && Entries.Count > 0) { _index = (_index + move + Entries.Count) % Entries.Count; Speak(Entries[_index].Name()); return true; }
            if ((keyboard.enterKey.wasReleasedThisFrame || keyboard.numpadEnterKey.wasReleasedThisFrame) && Entries.Count > 0)
            {
                if (keyboard.leftAltKey.isPressed || keyboard.rightAltKey.isPressed || keyboard.leftCtrlKey.isPressed
                    || keyboard.rightCtrlKey.isPressed || keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed)
                { Speak(Loc.Get("slot_release_modifiers")); return true; }
                Entries[_index].Run();
            }
            return true;
        }

        private static void Root(HybridPlayer hybrid, ItemObjectInPlacement preview)
        {
            ResetEntries();
            if (RoofConstructionHandler.IsRoof(preview.Item))
            {
                if (preview.IsNewItem)
                {
                    Add("roof_dimensions", () => { Clear(); RoofConstructionHandler.OpenDimensions(hybrid, preview.Item, preview); });
                    Add("roof_fit_room", () => RoofRooms(hybrid, preview));
                }
            }
            else if (preview.Item.IsWallItem)
            {
                Add("wall_span_centre", () => { Clear(); Speak(WallItemPlacementHandler.CentreWholeWall(hybrid, preview)); });
                Add("wall_span_divide", () => { Clear(); WallItemPlacementHandler.OpenDivisions(hybrid, preview); });
                Add("door_align_floor", () => { Clear(); Speak(WallItemPlacementHandler.AlignDoorFloor(hybrid, preview)); });
            }
            else if (preview.Item.IsCeilingItem)
            {
                Add("ceiling_choose_room", () => { Clear(); CeilingPlacementHandler.OpenRooms(hybrid); });
                Add("ceiling_centre", () => { Clear(); CeilingPlacementHandler.Centre(hybrid); });
            }
            else
            {
                if (PlacementSupportConsent.Current(preview) != null)
                    Add("placement_support_allow", () => { Clear(); Speak(PlacementSupportConsent.Approve(hybrid, preview)); });
                Entries.Add((() => RoomBoundaryConstraint.Option(), () =>
                {
                    Clear();
                    Speak(RoomBoundaryConstraint.Toggle(preview));
                    ItemPlacementHandler.ResetRoomBoundaryTarget();
                }));
                Add("facing_choose", () => Targets(hybrid, preview));
                Add("facing_clear", () => { Clear(); FacingTargetHandler.Clear(); Speak(Loc.Get("facing_cleared")); });
                Add("room_grid_use_lot", () => { Clear(); RoomPlacementGrid.UseLot(); ItemPlacementHandler.ReviewAfterAction(hybrid, preview); Speak(Loc.Get("room_grid_lot")); });
                Add("room_grid_choose", () => Rooms(hybrid, preview));
                Add("alignment_walls", () => Walls(hybrid, preview));
                Add("alignment_clear", () => { Clear(); ItemAlignmentHandler.Clear(); ItemPlacementHandler.ReviewAfterAction(hybrid, preview); Speak(Loc.Get("alignment_cleared")); });
            }
            if (!preview.Item.IsWallItem && !preview.Item.IsCeilingItem && !RoofConstructionHandler.IsRoof(preview.Item))
            {
                Add("fit_find_current", () => {
                    var room = ItemPlacementHandler.CurrentRoom(hybrid, preview);
                    if (room == null) { FitRooms(hybrid, preview); return; }
                    Clear(); Speak(RoomFitSearch.Start(hybrid, preview, room));
                });
                Add("fit_choose_room", () => FitRooms(hybrid, preview));
            }
            Add("placement_accept_actual", () => { Clear(); ItemPlacementHandler.ReviewAfterAction(hybrid, preview); WallItemPlacementHandler.AcceptActual(hybrid, preview); CeilingPlacementHandler.AcceptActual(hybrid, preview); Speak(Loc.Get("placement_actual_accepted", PlacementLocationReader.Target(preview.Item))); });
            Add("native_attempt_command", () => { Clear(); Speak(NativePlacementAttempt.Arm(hybrid, preview)); });
            Add("camera_pitch_toggle", () => Speak(CameraPitchHandler.Toggle(hybrid)));
            Add("placement_details", () =>
            {
                Clear();
                if (ItemPlacementHandler.TryGetDetailedContext(hybrid, out string report)) Speak(report);
                else Speak(Loc.Get("placement_wait"));
            });
            Add("placement_actions_close", () => { Clear(); Speak(Loc.Get("placement_actions_closed")); });
        }

        private static void FitRooms(HybridPlayer hybrid, ItemObjectInPlacement preview)
        {
            ResetEntries(); ulong lot = ItemPlacementHandler.ResolvePlacementLot(hybrid.Player, out _);
            foreach (var room in RoomSpatialReader.OrderedRooms(lot, hybrid.Player.GetLotLayer(lot)))
            {
                var target = room;
                Entries.Add((() => Loc.Get("fit_room_choice", RoomLabelHandler.Name(target)), () => {
                    Clear(); Speak(RoomFitSearch.Start(hybrid, preview, target));
                }));
            }
            Add("placement_actions_back", () => { Root(hybrid, preview); Speak(Describe()); }); Speak(Describe());
        }

        private static void RoofRooms(HybridPlayer hybrid, ItemObjectInPlacement preview)
        {
            ResetEntries();
            ulong lot = ItemPlacementHandler.ResolvePlacementLot(hybrid.Player, out _);
            foreach (var room in ZoneManager.Instance.GetLotZones(lot).OfType<ZoneWallObject>()
                .Where(r => RoomSpatialReader.IsUsableRoom(r, lot)).OrderBy(r => r.Layer).ThenBy(r => r.InstanceID))
            {
                var captured = room;
                Entries.Add((() => Loc.Get("roof_room_choice", RoomLabelHandler.Name(captured), captured.Layer),
                    () => { Clear(); RoofConstructionHandler.Fit(hybrid, preview, captured); }));
            }
            bool empty = Entries.Count == 0;
            Add("placement_actions_back", () => { Root(hybrid, preview); Speak(Describe()); });
            Speak(empty ? Loc.Get("roof_fit_unavailable") : Describe());
        }

        private static void Targets(HybridPlayer hybrid, ItemObjectInPlacement preview)
        {
            ResetEntries();
            foreach (var target in ItemManager.Instance.GetLotItems(preview.Item.LotPlacedOnGUID)
                .Where(t => FacingTargetHandler.LiveTarget(preview.Item, t)).OrderBy(t => t.InstanceID))
            {
                var captured = target; int ordinal = Entries.Count + 1; int instanceId = target.InstanceID;
                Entries.Add((() => !FacingTargetHandler.LiveTarget(preview.Item, captured) || captured.InstanceID != instanceId
                    ? Loc.Get("facing_changed") : Loc.Get("facing_target", ordinal, ItemDescription.GetProductIdentity(captured),
                    PlacementLocationReader.Target(captured, preview.Item)),
                    () => TargetCommands(hybrid, preview, captured, instanceId)));
            }
            bool empty = Entries.Count == 0;
            Add("placement_actions_back", () => { Root(hybrid, preview); Speak(Describe()); });
            Speak(empty ? Loc.Get("facing_no_targets") : Describe());
        }

        private static void TargetCommands(HybridPlayer hybrid, ItemObjectInPlacement preview, ItemObjectRoot target, int instanceId)
        {
            if (!FacingTargetHandler.LiveTarget(preview.Item, target) || target.InstanceID != instanceId) { Speak(Loc.Get("facing_changed")); return; }
            ResetEntries();
            if (ItemSlotReader.Read(preview.Item, target.LotPlacedOnGUID, target.Layer).Any(s => s.Owner == target))
                Add("facing_prefer_slots", () => RunForTarget(preview, target, instanceId, () => { FacingTargetHandler.Clear(); ItemAlignmentHandler.Clear(); SlotPlacementHandler.OpenForOwner(hybrid, preview, target); }));
            Add("facing_geometric", () => RunForTarget(preview, target, instanceId, () => Speak(FacingTargetHandler.Face(hybrid, preview, target))));
            Add("alignment_measure", () => RunForTarget(preview, target, instanceId, () => Speak(ItemAlignmentHandler.Measure(preview.Item, target))));
            Add("alignment_commands", () => Alignments(hybrid, preview, target, instanceId));
            Add("placement_actions_back", () => Targets(hybrid, preview)); Speak(Describe());
        }

        private static void RunForTarget(ItemObjectInPlacement preview, ItemObjectRoot target, int instanceId, Action action)
        {
            Clear();
            if (!FacingTargetHandler.LiveTarget(preview.Item, target) || target.InstanceID != instanceId) { Speak(Loc.Get("facing_changed")); return; }
            action();
        }

        private static void Alignments(HybridPlayer hybrid, ItemObjectInPlacement preview, ItemObjectRoot target, int id)
        {
            ResetEntries();
            if (ItemAlignmentHandler.TargetWall(target) != null)
                Add("alignment_wall_centre", () => RunForTarget(preview, target, id, () => Speak(ItemAlignmentHandler.Align(hybrid, preview, target, false, 0, true))));
            foreach (bool row in new[] { false, true })
                foreach (int edge in new[] { 0, -1, 1 })
                {
                    bool r = row; int e = edge;
                    Entries.Add((() => Loc.Get("alignment_axis_command", Loc.Get(r ? "alignment_rows" : "alignment_columns"),
                        Loc.Get(e == 0 ? "alignment_centres" : e < 0 ? "alignment_minimum" : "alignment_maximum")),
                        () => RunForTarget(preview, target, id, () => Speak(ItemAlignmentHandler.Align(hybrid, preview, target, r, e)))));
                }
            Add("placement_actions_back", () => TargetCommands(hybrid, preview, target, id)); Speak(Describe());
        }

        private static void Walls(HybridPlayer hybrid, ItemObjectInPlacement preview)
        {
            ResetEntries();
            foreach (var group in WallFaceReader.Read(preview.Item.LotPlacedOnGUID, preview.Item.Layer))
                foreach (var face in group.Faces)
                {
                    var captured = face; int ordinal = Entries.Count + 1;
                    Entries.Add((() => RoomSpatialReader.DescribeFace(captured, ordinal) + ". " + ItemAlignmentHandler.WallGap(preview.Item, captured),
                        () => WallCommands(hybrid, preview, captured)));
                }
            Add("placement_actions_back", () => { Root(hybrid, preview); Speak(Describe()); }); Speak(Describe());
        }

        private static void WallCommands(HybridPlayer hybrid, ItemObjectInPlacement preview, WallFaceTarget face)
        {
            ResetEntries();
            Add("alignment_measure", () => Speak(ItemAlignmentHandler.WallGap(preview.Item, face)));
            Add("alignment_wall_touch", () => { Clear(); Speak(ItemAlignmentHandler.SetWallGap(hybrid, preview, face, 0)); });
            Add("alignment_wall_distance", () =>
            {
                Clear();
                if (!face.TryRead(preview.Item.LotPlacedOnGUID, preview.Item.Layer, out var frame)) { Speak(Loc.Get("alignment_changed")); return; }
                CoordinateEntryHandler.Open(hybrid, preview, Loc.Get("alignment_distance_hint"), 0.1, 0,
                    () => face.TryRead(preview.Item.LotPlacedOnGUID, preview.Item.Layer, out var now) && frame.Same(now),
                    (gap, reserved) => Speak(reserved == 0 ? ItemAlignmentHandler.SetWallGap(hybrid, preview, face, gap) : Loc.Get("coordinate_invalid")));
            });
            Add("placement_actions_back", () => Walls(hybrid, preview)); Speak(Describe());
        }

        private static void Rooms(HybridPlayer hybrid, ItemObjectInPlacement preview)
        {
            ResetEntries();
            var lot = ItemPlacementHandler.ResolvePlacementLot(hybrid.Player, out _); int layer = hybrid.Player.GetLotLayer(lot);
            foreach (var room in RoomSpatialReader.OrderedRooms(lot, layer))
            {
                var captured = room; int ordinal = Entries.Count + 1;
                Entries.Add((() => Loc.Get("room_grid_choice", RoomLabelHandler.Name(captured, ordinal), RoomSpatialReader.TryReference(captured, out _)
                    ? Loc.Get("room_grid_reference_ready") : Loc.Get("room_reference_required")),
                    () => { Clear(); SlotPlacementHandler.Clear(); Speak(RoomPlacementGrid.Select(captured)); ItemPlacementHandler.ReviewAfterAction(hybrid, preview); }));
            }
            Add("placement_actions_back", () => { Root(hybrid, preview); Speak(Describe()); }); Speak(Describe());
        }

        internal static string Describe() => Loc.Get("placement_actions_open") + ". "
            + (Entries.Count > 0 ? Entries[_index].Name() : Loc.Get("placement_actions_close"));
        private static void Speak(string text) => ScreenReader.Speak(text);
    }
}
