using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Setting;
using ParalivesAccess.Speech;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ParalivesAccess.Handlers
{
    /// <summary>Browses native surface/chair slots and verifies the normal preview before purchase.</summary>
    internal static class SlotPlacementHandler
    {
        internal static bool Enabled { get; set; } = true;
        internal static bool Active => _player != null;
        private static Player _player;
        private static ItemObjectRoot _item;
        private static ulong _lot;
        private static int _layer, _index, _stable, _pendingFrame = -1, _submittedFrame = -1;
        private static List<ItemSlotTarget> _targets = new List<ItemSlotTarget>();
        private static ItemSlotTarget _target;
        private static ItemObjectRoot _ownerFilter;
        private static Vector3 _position, _observedPosition, _observedSize, _observedOffset, _observedMouseOffset;
        private static Quaternion _rotation, _observedRotation;
        private static int _observedPrice;
        private static ulong _observedSkin;
        private static bool _observedSnap, _hasScreen, _announce;
        private static Vector2 _screen;
        private static readonly PlacementPointer Pointer = new PlacementPointer();
        private static string _failure, _lastStatus;
        private static InputButton _injectedButton;

        internal static bool ApplyPatches(Harmony harmony)
        {
            try
            {
                return Install(harmony, typeof(EndMoveItem), nameof(BeforeNativeEnd), nameof(AfterNativeEnd))
                    && Install(harmony, typeof(UpdateMoveItem), nameof(BeforeNativeMove), null);
            }
            catch (Exception ex) { Plugin.Log.LogError("Slot placement hooks could not be installed: " + ex); return false; }
        }

        private static bool Install(Harmony harmony, Type system, string before, string after)
        {
            var method = AccessTools.Method(system, "UpdateForPlayer", new[] { typeof(Player) });
            var prefix = AccessTools.Method(typeof(SlotPlacementHandler), before);
            var cleanup = after == null ? null : AccessTools.Method(typeof(SlotPlacementHandler), after);
            if (method == null || prefix == null || (after != null && cleanup == null)) return false;
            harmony.Patch(method, prefix: new HarmonyMethod(prefix), finalizer: cleanup == null ? null : new HarmonyMethod(cleanup));
            var info = Harmony.GetPatchInfo(method);
            return info?.Prefixes.Any(p => p.owner == Plugin.PluginGuid && p.PatchMethod == prefix) == true
                && (cleanup == null || info.Finalizers.Any(p => p.owner == Plugin.PluginGuid && p.PatchMethod == cleanup));
        }

        internal static bool TryOpen(HybridPlayer hybrid, ItemObjectInPlacement preview)
        {
            if (preview.Item.IsWallItem || preview.Item.IsCeilingItem || preview.Item.IsLotMovementItem
                || !ModInputKey.WasPressed(Keyboard.current, Key.Semicolon)) return false;
            if (!ModInputKey.IsAvailable(hybrid, Key.Semicolon, out string conflict)) Speak(conflict);
            else if (!Enabled) Speak(Loc.Get("slot_unavailable"));
            else
            {
                FacingTargetHandler.Clear(); ItemAlignmentHandler.Clear();
                Clear(); _player = hybrid.Player; _item = preview.Item;
                _lot = ItemPlacementHandler.ResolvePlacementLot(_player, out _); _layer = _player.GetLotLayer(_lot);
                Reload(); Speak(Loc.Get("slot_open") + ". " + Describe());
            }
            return true;
        }

        internal static void Clear()
        {
            _player = null; _item = null; _ownerFilter = null; _targets.Clear(); _target = null;
            Invalidate(); _submittedFrame = -1;
        }

        private static void Invalidate()
        { _stable = 0; _pendingFrame = -1; _hasScreen = false; Pointer.Clear(); _failure = null; _lastStatus = null; _announce = true; }

        private static void Reload()
        { _targets = ItemSlotReader.Read(_item, _lot, _layer).Where(t => ReferenceEquals(_ownerFilter, null) || t.Owner == _ownerFilter).ToList(); _index = 0; _target = null; Invalidate(); }

        internal static void OpenForOwner(HybridPlayer hybrid, ItemObjectInPlacement preview, ItemObjectRoot owner)
        {
            if (!Enabled || !FacingTargetHandler.LiveTarget(preview.Item, owner)) { Speak(Loc.Get("slot_changed")); return; }
            Clear(); _player = hybrid.Player; _item = preview.Item; _ownerFilter = owner;
            _lot = owner.LotPlacedOnGUID; _layer = owner.Layer; Reload(); Speak(Loc.Get("slot_open") + ". " + Describe());
        }

        private static bool Foreground(HybridPlayer hybrid) => Enabled && ItemPlacementHandler.Enabled
            && _player != null && hybrid?.Player == _player && _item != null
            && !_item.IsWallItem && !_item.IsCeilingItem && !_item.IsLotMovementItem
            && _player.ItemInPlacement?.Item == _item
            && _player.State == GameStates.BuildMode && _player.PlayerActionsAreAllowed
            && !hybrid.HasInputFieldFocused && !hybrid.IsUsingGamePad && !_player.PlayerActionBlockedByUIWindow;

        private static bool SameContext() => _lot != 0 && ItemPlacementHandler.ResolvePlacementLot(_player, out _) == _lot
            && _player.GetLotLayer(_lot) == _layer;

        private static bool SameTarget() => _target != null && _target.IsLive(_item, _lot, _layer)
            && Vector3.Distance(_position, _target.Transform.position) < 0.001f
            && Quaternion.Angle(_rotation, _target.Transform.rotation) < 0.1f;

        internal static bool AllowsLinkedOverlap(ItemObjectRoot root, ItemObjectRoot candidate)
        {
            // An authored desk chair can overlap the bounding box of its linked computer.
            // Exempt only the live, occupied counterpart of the currently reviewed native slot.
            if (root != _item || candidate == null || candidate == root || !candidate.isActiveAndEnabled
                || candidate.LotPlacedOnGUID != _lot || candidate.Layer != _layer
                || LotManager.Instance?.GetLotByGUID(_lot)?.LotItems.Contains(candidate) != true
                || _player?.ItemInPlacement?.Item != root || !SameContext() || !SameTarget()
                || !_target.MatchesPreview(_player.ItemInPlacement)) return false;
            if (_target.IsChair)
            {
                foreach (var surface in _target.Owner.ItemPlacementSlots.Values)
                {
                    if (surface == null || surface.ChairSlotLinked != _target.Chair) continue;
                    var other = new ItemSlotTarget(surface);
                    if (other.IsLive(candidate, _lot, _layer) && other.MatchesPlaced(candidate)) return true;
                }
                return false;
            }
            var chair = _target.Surface.ChairSlotLinked;
            if (chair == null) return false;
            var linked = new ItemSlotTarget(chair);
            return linked.IsLive(candidate, _lot, _layer) && linked.MatchesPlaced(candidate);
        }

        private static bool Modified() => Keyboard.current != null
            && (Keyboard.current.leftAltKey.isPressed || Keyboard.current.rightAltKey.isPressed
                || Keyboard.current.leftCtrlKey.isPressed || Keyboard.current.rightCtrlKey.isPressed
                || Keyboard.current.leftShiftKey.isPressed || Keyboard.current.rightShiftKey.isPressed);

        private static bool PhysicalConfirm(HybridPlayer hybrid) => hybrid.ButtonConfirm.Down
            || hybrid.ButtonConfirm.Up || hybrid.ButtonConfirm.Pressed;

        internal static void BeforeGameSystems(HybridPlayer hybrid, ItemObjectInPlacement preview)
        {
            _pendingFrame = -1;
            if (ModInputKey.WasPressed(Keyboard.current, Key.Semicolon))
            {
                if (!ModInputKey.IsAvailable(hybrid, Key.Semicolon, out string conflict)) { Speak(conflict); return; }
                if (_target != null) { _target = null; Invalidate(); Speak(Describe()); }
                else { Clear(); Speak(Loc.Get("slot_closed")); }
                return;
            }
            if (!Foreground(hybrid)) { _target = null; Invalidate(); return; }
            if (!SameContext())
            {
                _lot = ItemPlacementHandler.ResolvePlacementLot(_player, out _); _layer = _player.GetLotLayer(_lot);
                Reload(); Speak(Loc.Get("slot_context_changed") + ". " + Describe()); return;
            }
            var keyboard = Keyboard.current;
            if (keyboard == null) return;
            if (CoordinateEntryHandler.WasRequested(hybrid)) { Speak(Loc.Get("coordinate_leave_slots")); return; }
            if (hybrid.ButtonCancel.Down || hybrid.ButtonUndo.Down || hybrid.ButtonRedo.Down)
            { _target = null; Invalidate(); return; }
            if (preview.FromRotationWidget || preview.IsInOffset || _player.ItemInRotation != null)
            { Invalidate(); return; }
            if (PhysicalConfirm(hybrid) || (_target != null && _hasScreen && _stable > 0 && !preview.CursorTeleported
                && Pointer.WasMoved(InputManager.Instance.GetCursorPosition(_player.PlayerIndex))))
            {
                bool wasTargeted = _target != null;
                if (wasTargeted) Plugin.Log.LogInfo($"[Slots] pointer handoff item={_item.InstanceID}, frame={Time.frameCount}, physicalConfirm={PhysicalConfirm(hybrid)}, legacy={InputManager.Instance.GetCursorPosition(_player.PlayerIndex)}, device={Mouse.current?.position.ReadValue()}, target={_screen}.");
                _target = null; Invalidate();
                if (wasTargeted) Speak(Loc.Get("slot_manual"));
                return;
            }
            int x = (keyboard.rightArrowKey.wasPressedThisFrame ? 1 : 0) - (keyboard.leftArrowKey.wasPressedThisFrame ? 1 : 0);
            int y = (keyboard.upArrowKey.wasPressedThisFrame ? 1 : 0) - (keyboard.downArrowKey.wasPressedThisFrame ? 1 : 0);
            int move = x != 0 ? x : -y;
            bool enter = keyboard.enterKey.wasReleasedThisFrame || keyboard.numpadEnterKey.wasReleasedThisFrame;
            if (move != 0)
            {
                _target = null; Invalidate();
                if (_targets.Count > 0) _index = (_index + move + _targets.Count) % _targets.Count;
                Speak(Describe()); return;
            }
            if (enter && Modified()) { Invalidate(); Speak(Loc.Get("slot_release_modifiers")); return; }
            if (_target == null)
            {
                if (!enter) return;
                if (_targets.Count == 0) { Reload(); Speak(Describe()); return; }
                var target = _targets[_index];
                if (!target.IsLive(_item, _lot, _layer)) { Changed(); return; }
                if (!target.AvailableFor(_item)) { Speak(Loc.Get("slot_occupied")); return; }
                string boundary = RoomBoundaryConstraint.Problem(_lot, _layer, target.Transform.position);
                if (boundary != null) { Speak(boundary); return; }
                _target = target; _position = target.Transform.position; _rotation = target.Transform.rotation;
                Invalidate(); Frame(); Project(hybrid, preview); Speak(Loc.Get("slot_targeted")); return;
            }
            if (!SameTarget()) { Changed(); return; }
            if (ModInputKey.WasPressed(keyboard, Key.Home))
            {
                if (!ModInputKey.IsAvailable(hybrid, Key.Home, out string conflict)) { Speak(conflict); return; }
                Invalidate(); Frame(); Project(hybrid, preview); return;
            }
            Project(hybrid, preview);
            if (!enter) return;
            string problem = Ready(hybrid, preview);
            if (problem != null) Speak(problem); else _pendingFrame = Time.frameCount;
        }

        private static void Changed() { Reload(); Speak(Loc.Get("slot_changed") + ". " + Describe()); }

        private static void Frame()
        {
            if (!WorldTargeting.FrameArea(_player, _lot, _layer, _position, "item-slot")) Speak(Loc.Get("explorer_frame_failed"));
        }

        internal static Vector3 AimPoint(Player player, ItemObjectInPlacement preview, Vector3 point)
        {
            // Invert the non-wall ray offset in UpdateMoveItem (game-api §106). This only aims the
            // cursor. Native tag filtering, proximity, occupancy and snap order still choose a slot.
            Vector3 offset = preview.Item.ItemOffset != null ? preview.Item.ItemOffset.Offset : Vector3.zero;
            if (player.HasSnap) offset -= preview.LocalMouseOffset;
            return point + preview.Item.transform.rotation * offset;
        }

        private static void Project(HybridPlayer hybrid, ItemObjectInPlacement preview)
        {
            Vector2 screen = default(Vector2);
            string failure = RoomBoundaryConstraint.Problem(_lot, _layer, _position)
                ?? (Mouse.current == null ? Loc.Get("placement_mouse_missing")
                : !ItemPlacementHandler.TryProjectTarget(hybrid, AimPoint(_player, preview, _position), out screen)
                    ? Loc.Get("slot_out_of_view") : null);
            if (failure != null)
            {
                _stable = 0; _hasScreen = false;
                if (failure != _failure) Speak(failure);
                _failure = failure; return;
            }
            _failure = null;
            if (!_hasScreen || Vector2.Distance(screen, _screen) > 0.25f)
            {
                _stable = 0; _announce = true; _hasScreen = true;
                Pointer.Expect(screen);
                Mouse.current.WarpCursorPosition(screen);
            }
            _screen = screen;
        }

        private static void BeforeNativeMove(Player player)
        {
            try
            {
                var hybrid = PlayerManager.Instance?.HybridPlayer1;
                if (player != _player || !Foreground(hybrid) || !_hasScreen || _failure != null
                    || !SameContext() || !SameTarget() || PhysicalConfirm(hybrid)
                    || player.ItemInPlacement.FromRotationWidget || player.ItemInPlacement.IsInOffset || player.ItemInRotation != null
                    || hybrid.ButtonCancel.Down || hybrid.ButtonUndo.Down || hybrid.ButtonRedo.Down) return;
                player.ItemInPlacement.CursorTeleportPosition = new Vector3(_screen.x, _screen.y, 0f);
                player.ItemInPlacement.CursorTeleported = true;
            }
            catch (Exception ex) { Fail(ex); }
        }

        internal static void AfterGameSystems(HybridPlayer hybrid, ItemObjectInPlacement preview)
        {
            if (!Foreground(hybrid)) { _target = null; Invalidate(); return; }
            if (_submittedFrame == Time.frameCount)
            { _submittedFrame = -1; _stable = 0; _announce = true; Speak(Loc.Get("slot_not_placed")); return; }
            if (_target == null || !_hasScreen || _failure != null || preview.CursorTeleported) return;
            _stable = SameObservation(preview) ? _stable + 1 : 1;
            _observedPosition = _item.CubeTransform.LocalPosition; _observedSize = _item.CubeTransform.FinalSize;
            _observedRotation = _item.transform.rotation; _observedPrice = _item.GetPrice(); _observedSkin = _item.CurrentSkinGUID;
            _observedOffset = _item.ItemOffset != null ? _item.ItemOffset.Offset : Vector3.zero;
            _observedMouseOffset = preview.LocalMouseOffset; _observedSnap = _player.HasSnap;
            if (_stable < 2) return;
            string status = Ready(hybrid, preview) ?? Loc.Get("slot_ready");
            if (_announce || status != _lastStatus)
            {
                _announce = false; _lastStatus = status; Speak(Describe());
                Plugin.Log.LogInfo($"[Slots] item={_item.InstanceID}, owner={_target.OwnerId}, slot={_target.Guid}, chair={_target.IsChair}, "
                    + $"surfaceRef={_item.PlacementSlotPlacedOn}, chairRef={_item.ChairSlotPlacedOn}, occupant={_target.Occupant}, "
                    + $"target={_position}, actual={_observedPosition}, status={status}.");
            }
        }

        private static bool SameObservation(ItemObjectInPlacement preview) => _stable > 0
            && Vector3.Distance(_observedPosition, _item.CubeTransform.LocalPosition) < 0.001f
            && Vector3.Distance(_observedSize, _item.CubeTransform.FinalSize) < 0.001f
            && Quaternion.Angle(_observedRotation, _item.transform.rotation) < 0.1f && _observedPrice == _item.GetPrice()
            && _observedSkin == _item.CurrentSkinGUID && _observedSnap == _player.HasSnap
            && Vector3.Distance(_observedOffset, _item.ItemOffset != null ? _item.ItemOffset.Offset : Vector3.zero) < 0.001f
            && Vector3.Distance(_observedMouseOffset, preview.LocalMouseOffset) < 0.001f;

        private static string Ready(HybridPlayer hybrid, ItemObjectInPlacement preview)
        {
            if (!Foreground(hybrid) || !SameContext() || !SameTarget()) return Loc.Get("slot_changed");
            if (!_target.AvailableFor(_item)) return Loc.Get("slot_occupied");
            if (_failure != null) return _failure;
            if (Modified()) return Loc.Get("slot_release_modifiers");
            if (!_hasScreen || _stable < 2 || preview.CursorTeleported || !SameObservation(preview)
                || preview.FromRotationWidget || preview.IsInOffset || _player.ItemInRotation != null
                || !BuildModeRefreshManager.IsZoneStateRefreshed || ZoneManager.Instance.DirtyWallZones.Count != 0)
                return Loc.Get("placement_wait");
            if (_item.LotPlacedOnGUID != _lot || _item.Layer != _layer || !_target.MatchesPreview(preview))
                return Loc.Get("slot_wrong_target");
            if (!ItemSlotTarget.Finite(_item.CubeTransform.LocalPosition) || !ItemSlotTarget.Finite(_item.CubeTransform.FinalSize)
                || !ItemSlotTarget.Finite(_item.transform.eulerAngles)
                || _item.CubeTransform.FinalSize.x <= 0 || _item.CubeTransform.FinalSize.y <= 0 || _item.CubeTransform.FinalSize.z <= 0
                || Vector3.Distance(_item.CubeTransform.LocalPosition, _position
                    + (_item.ItemOffset != null ? _item.transform.rotation * _item.ItemOffset.Offset : Vector3.zero)) > 0.01f
                || Quaternion.Angle(_rotation, _item.transform.rotation) > 0.1f)
                return Loc.Get("slot_alignment_changed");
            return ItemPlacementHandler.SemanticPlacementProblem(hybrid, _item);
        }

        private static bool NativeRayMatches(ItemObjectInPlacement preview)
        {
            Vector3 offset = AimPoint(_player, preview, Vector3.zero);
            var screen = new Vector3(_screen.x, _screen.y, 0f);
            var floor = RaycastSystem.ScreenCastAndGoThroughTerrain(screen, includeTerrain: true, includeFloor: true, rayOffset: -offset);
            if (floor.type != ColliderType.Floor && floor.type != ColliderType.Terrain) return false;
            Vector3 point = floor.hit.point;
            ItemObjectRoot support = null;
            foreach (var hit in RaycastSystem.ScreenCastAll(screen, includeObjects: true, rayOffset: -offset,
                distance: floor.gameObject != null ? floor.hit.distance : 1000f))
            {
                if (hit.type != ColliderType.Object || hit.gameObject == null || hit.gameObject == _item.gameObject) continue;
                var root = hit.gameObject.GetComponent<ItemObjectRoot>();
                if (root != null && root.ItemCanBeStackedOn && (root.SurfaceCanBeNotFlat || Vector3.Dot(hit.hit.normal, Vector3.up) > 0.9f)
                    && (_item.ItemCanStackOnOther || root.AnyItemCanBeStackedOn))
                { support = root; point = hit.hit.point; break; }
            }
            return (_target.IsChair || support == _target.Owner)
                && Vector3.Distance(point, _position) < Settings.Get<SnappingAndAlignment>().SnapDistanceWithPlacementSlots;
        }

        private static void BeforeNativeEnd(Player player)
        {
            if (player != _player || _pendingFrame != Time.frameCount) return;
            _pendingFrame = -1;
            try
            {
                var hybrid = PlayerManager.Instance.HybridPlayer1;
                var preview = player.ItemInPlacement;
                if (PhysicalConfirm(hybrid) || hybrid.ButtonCancel.Down || hybrid.ButtonUndo.Down || hybrid.ButtonRedo.Down) return;
                string problem = Ready(hybrid, preview);
                if (problem != null) { Speak(problem); return; }
                if (!ItemPlacementHandler.TryProjectTarget(hybrid, AimPoint(player, preview, _position), out var screen)
                    || Vector2.Distance(screen, _screen) > 0.25f || !NativeRayMatches(preview))
                { Speak(Loc.Get("slot_wrong_target")); return; }
                _injectedButton = hybrid.ButtonConfirm; _injectedButton.SetValue(0f); _submittedFrame = Time.frameCount;
            }
            catch (Exception ex) { AfterNativeEnd(player); Fail(ex); }
        }

        private static void AfterNativeEnd(Player player)
        {
            if (_injectedButton == null) return;
            var button = _injectedButton; _injectedButton = null; button.Update();
        }

        internal static string Describe()
            => Describe(false);

        internal static string Describe(bool detailed)
        {
            if (!Enabled) return Loc.Get("slot_unavailable");
            if (_targets.Count == 0) return Loc.Get("slot_none");
            string report = Loc.Get("slot_entry", _index + 1, _targets.Count, _targets[_index].Describe(_item, _lot, _layer, detailed));
            if (_target == null) return report;
            return report + (detailed ? ". " + Loc.Get("slot_preview", PlacementGridReader.Dimensions(_item), FormatService.GetMoney(_item.GetPrice())) : "")
                + ". " + (Ready(PlayerManager.Instance.HybridPlayer1, _player.ItemInPlacement) ?? Loc.Get("slot_ready"));
        }

        internal static string DescribeResult(ItemObjectRoot placed)
        {
            if (placed == null || _target == null || !SameTarget() || placed.LotPlacedOnGUID != _lot || placed.Layer != _layer
                || placed.CubeTransform == null || !ItemSlotTarget.Finite(placed.CubeTransform.LocalPosition)
                || !ItemSlotTarget.Finite(placed.transform.eulerAngles)
                || !_target.MatchesPlaced(placed) || Quaternion.Angle(_rotation, placed.transform.rotation) > 0.1f)
                return Loc.Get("slot_result_unverified");
            Plugin.Log.LogInfo($"[Slots] verified item={placed.InstanceID}, owner={_target.OwnerId}, slot={_target.Guid}, chair={_target.IsChair}.");
            return Loc.Get(_target.IsChair ? "slot_chair_placed" : "slot_surface_placed", _target.Name) + ". " + _target.DescribeLinks(_lot);
        }

        private static void Fail(Exception ex)
        { Enabled = false; _target = null; Invalidate(); Plugin.Log.LogError("Slot placement stopped: " + ex); Speak(Loc.Get("slot_unavailable")); }

        private static void Speak(string message) { Plugin.Log.LogInfo("[Slots] " + message); ScreenReader.Speak(message); }
    }
}
