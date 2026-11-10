using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ParalivesAccess.Speech;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ParalivesAccess.Handlers
{
    /// <summary>Chooses native wall faces and drives a new wall item's normal placement preview.</summary>
    internal static class WallItemPlacementHandler
    {
        internal static bool Enabled { get; set; } = true;
        private enum Stage { Rooms, Faces, Position }
        private static Stage _stage;
        private static Player _player;
        private static ItemObjectRoot _item;
        private static ulong _lot;
        private static int _layer, _roomIndex, _faceIndex, _stable, _pendingFrame = -1;
        private static List<WallFaceGroup> _groups = new List<WallFaceGroup>();
        private static WallFaceTarget _face;
        private static WallFaceFrame _frame;
        private static WallSpanTarget _span;
        private static float _along, _height;
        private static Vector2 _screen;
        private static readonly PlacementPointer Pointer = new PlacementPointer();
        private static Vector3 _observedPosition, _observedSize, _observedNormal;
        private static Quaternion _observedRotation;
        private static GameObject _observedSupport;
        private static int _observedPrice;
        private static int _announcedPrice;
        private static Vector3 _announcedSize;
        private static bool _hasScreen, _announce;
        private static string _failure, _lastStatus;
        private static InputButton _injectedButton;
        private static int _submittedFrame = -1;
        private const float AlignmentTolerance = 0.01f;

        internal static bool ApplyPatches(Harmony harmony)
        {
            try
            {
                return Install(harmony, typeof(EndMoveItem), nameof(BeforeNativeEnd), nameof(AfterNativeEnd))
                    && Install(harmony, typeof(UpdateMoveItem), nameof(BeforeNativeMove), null);
            }
            catch (Exception ex) { Plugin.Log.LogError("Wall-item input bridge could not be installed: " + ex); return false; }
        }

        private static bool Install(Harmony harmony, Type system, string prefixName, string cleanupName)
        {
            var method = AccessTools.Method(system, "UpdateForPlayer", new[] { typeof(Player) });
            var prefix = AccessTools.Method(typeof(WallItemPlacementHandler), prefixName);
            var cleanup = cleanupName == null ? null : AccessTools.Method(typeof(WallItemPlacementHandler), cleanupName);
            if (method == null || prefix == null || (cleanupName != null && cleanup == null)) return false;
            harmony.Patch(method, prefix: new HarmonyMethod(prefix), finalizer: cleanup == null ? null : new HarmonyMethod(cleanup));
            var info = Harmony.GetPatchInfo(method);
            return info?.Prefixes.Any(p => p.owner == Plugin.PluginGuid && p.PatchMethod == prefix) == true
                && (cleanup == null || info.Finalizers.Any(p => p.owner == Plugin.PluginGuid && p.PatchMethod == cleanup));
        }

        internal static void Begin(HybridPlayer hybrid, ItemObjectInPlacement preview, ulong lot)
        {
            Clear();
            _player = hybrid.Player; _item = preview.Item; _lot = lot; _layer = _player.GetLotLayer(lot);
            _announcedPrice = _item.GetPrice(); _announcedSize = _item.CubeTransform.FinalSize;
            if (!Enabled) { Speak(Loc.Get("wall_item_unavailable")); return; }
            Reload();
            Speak(Loc.Get("wall_item_open", ItemDescription.GetProductIdentity(_item)) + ". " + Describe());
        }

        internal static void Clear()
        {
            _player = null; _item = null; _face = null; _frame = null; _span = null; _groups.Clear();
            _stable = 0; _pendingFrame = _submittedFrame = -1; _hasScreen = false; _failure = null; _lastStatus = null;
        }

        private static bool Foreground(HybridPlayer hybrid) => Enabled && ItemPlacementHandler.Enabled
            && hybrid?.Player == _player && _player != null && _item != null
            && _item.IsWallItem
            && _player.ItemInPlacement?.Item == _item
            && _player.State == GameStates.BuildMode && _player.PlayerActionsAreAllowed
            && !hybrid.HasInputFieldFocused && !hybrid.IsUsingGamePad && !_player.PlayerActionBlockedByUIWindow;

        private static bool SameContext() => ItemPlacementHandler.ResolvePlacementLot(_player, out _) == _lot
            && _player.GetLotLayer(_lot) == _layer;

        private static void Reload()
        {
            _groups = _lot == 0 ? new List<WallFaceGroup>() : WallFaceReader.Read(_lot, _layer);
            _roomIndex = _faceIndex = 0; _stage = Stage.Rooms; _face = null; _frame = null; _span = null;
            Invalidate();
        }

        private static void Invalidate()
        {
            _stable = 0; _pendingFrame = -1; _hasScreen = false; Pointer.Clear(); _failure = null;
            _lastStatus = null; _announce = true;
        }

        internal static void BeforeGameSystems(HybridPlayer hybrid, ItemObjectInPlacement preview)
        {
            _pendingFrame = -1;
            if (!Foreground(hybrid)) { Invalidate(); return; }
            if (!SameContext())
            {
                _lot = ItemPlacementHandler.ResolvePlacementLot(_player, out _); _layer = _player.GetLotLayer(_lot);
                Reload(); Speak(Loc.Get("wall_item_context_changed") + ". " + Describe()); return;
            }
            var keyboard = Keyboard.current;
            if (keyboard == null) return;
            if (CoordinateEntryHandler.WasRequested(hybrid))
            {
                if (_stage != Stage.Position || _face == null) { Speak(Loc.Get("coordinate_choose_face")); return; }
                var selected = _face; var frame = _frame; var span = _span;
                Invalidate();
                CoordinateEntryHandler.Open(hybrid, preview, Loc.Get("coordinate_wall_hint"), _along, _height,
                    () => SameContext() && _face == selected && _span == span && FrameIsLive() && frame.Same(_frame),
                    (along, height) =>
                    {
                        if (RequiredFloorHeight(out float nativeHeight) && Math.Abs(height - nativeHeight) > AlignmentTolerance)
                        { Speak(Loc.Get("wall_item_floor_height", PlacementGridReader.Length(nativeHeight))); return; }
                        _along = (float)along; _height = (float)height; Invalidate(); Project(hybrid, preview);
                    });
                return;
            }
            if (hybrid.ButtonCancel.Down || hybrid.ButtonUndo.Down || hybrid.ButtonRedo.Down) { Invalidate(); return; }
            if (hybrid.ButtonConfirm.Down || hybrid.ButtonConfirm.Up || hybrid.ButtonConfirm.Pressed
                || (_stage == Stage.Position && _hasScreen && _stable > 0 && !preview.CursorTeleported
                    && Pointer.WasMoved(InputManager.Instance.GetCursorPosition(_player.PlayerIndex))))
            {
                if (_stage == Stage.Position) { _stage = Stage.Faces; Invalidate(); Speak(Loc.Get("wall_item_manual")); }
                return;
            }
            if (ModInputKey.WasPressed(keyboard, Key.Semicolon))
            {
                if (!ModInputKey.IsAvailable(hybrid, Key.Semicolon, out string conflict)) { Speak(conflict); return; }
                if (_stage == Stage.Position) { _stage = Stage.Faces; Invalidate(); }
                else Reload();
                Speak(Describe()); return;
            }
            int x = (keyboard.rightArrowKey.wasPressedThisFrame ? 1 : 0) - (keyboard.leftArrowKey.wasPressedThisFrame ? 1 : 0);
            int y = (keyboard.upArrowKey.wasPressedThisFrame ? 1 : 0) - (keyboard.downArrowKey.wasPressedThisFrame ? 1 : 0);
            bool enter = keyboard.enterKey.wasReleasedThisFrame || keyboard.numpadEnterKey.wasReleasedThisFrame;
            if (_stage != Stage.Position)
            {
                int count = _stage == Stage.Rooms ? _groups.Count : Group?.Faces.Count ?? 0;
                int move = x != 0 ? x : -y;
                if (count > 0 && move != 0)
                {
                    if (_stage == Stage.Rooms) _roomIndex = (_roomIndex + move + count) % count;
                    else _faceIndex = (_faceIndex + move + count) % count;
                    Speak(Describe()); return;
                }
                if (!enter) return;
                if (count == 0) { Reload(); Speak(Describe()); return; }
                if (_stage == Stage.Rooms) { _stage = Stage.Faces; _faceIndex = 0; Speak(Describe()); return; }
                _face = Group.Faces[_faceIndex];
                _span = null;
                if (!_face.TryRead(_lot, _layer, out _frame)) { TargetsChanged(); return; }
                _along = _frame.Width / 2f; _height = _frame.Height / 2f;
                    if ((_item.SnapsToBottomOfWall || DoorPlacementReader.IsDoor(_item)) && DoorPlacementReader.TryFloor(_face, out float floor))
                    _height = floor + _item.CubeTransform.FinalSize.y / 2 - _frame.Origin.y;
                _stage = Stage.Position; Invalidate();
                FrameCamera(hybrid); Project(hybrid, preview);
                Speak(Loc.Get(RequiredFloorHeight(out _) ? "wall_item_position_controls_fixed" : "wall_item_position_controls")); return;
            }
            if (!FrameIsLive()) { TargetsChanged(); return; }
            if (ModInputKey.WasPressed(keyboard, Key.Home))
            {
                if (!ModInputKey.IsAvailable(hybrid, Key.Home, out string conflict)) { Speak(conflict); return; }
                Invalidate(); FrameCamera(hybrid); Project(hybrid, preview); return;
            }
            if (ModInputKey.WasPressed(keyboard, Key.End))
            {
                if (!ModInputKey.IsAvailable(hybrid, Key.End, out string conflict)) { Speak(conflict); return; }
                _along = _frame.Width / 2f; Invalidate(); Project(hybrid, preview); return;
            }
            if (x != 0 || y != 0)
            {
                float step = ItemPlacementHandler.GridSpacingMetres;
                if (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed) step /= 5f;
                _along += x * step;
                if (RequiredFloorHeight(out float nativeHeight))
                { _height = nativeHeight; if (y != 0) Speak(Loc.Get("wall_item_floor_height", PlacementGridReader.Length(nativeHeight))); }
                else _height += y * step;
                Invalidate(); Project(hybrid, preview); return;
            }
            Project(hybrid, preview);
            if (!enter) return;
            string problem = Ready(hybrid, preview);
            if (problem != null) Speak(problem);
            else _pendingFrame = Time.frameCount;
        }

        private static WallFaceGroup Group => _roomIndex >= 0 && _roomIndex < _groups.Count ? _groups[_roomIndex] : null;

        private static bool FrameIsLive() => _face != null && (_span != null ? _span.IsLive(_lot, _layer)
            : _face.TryRead(_lot, _layer, out var live) && _frame.Same(live));

        private static bool MatchesSection(SegmentWallSectionObject section) => _span != null
            ? _span.Contains(section) : section == _face.Face.WallSection;

        private static bool RequiredFloorHeight(out float height)
        {
            height = 0;
            if (_item?.SnapsToBottomOfWall != true || _frame == null || !DoorPlacementReader.TryFloor(_face, out var floor)) return false;
            height = floor + _item.CubeTransform.FinalSize.y / 2f - _frame.Origin.y;
            return PlacementGrid.Finite(height);
        }

        internal static string CentreWholeWall(HybridPlayer hybrid, ItemObjectInPlacement preview)
            => JoinWall(hybrid, preview, centre: true);

        private static string JoinWall(HybridPlayer hybrid, ItemObjectInPlacement preview, bool centre)
        {
            if (!Foreground(hybrid) || !SameContext() || _stage != Stage.Position || !FrameIsLive()) return Loc.Get("coordinate_choose_face");
            var group = WallFaceReader.Read(_lot, _layer).FirstOrDefault(g => g.Room == _face.Room);
            var span = group == null ? null : WallSpanReader.Build(group.Faces, _face, _lot, _layer);
            if (span == null) return Loc.Get("wall_item_targets_changed");
            Vector3 previous = _frame.Point(_along, _height);
            _span = span; _frame = span.Frame; _along = centre ? _frame.Width / 2 : _frame.Along(previous); _height = previous.y - _frame.Origin.y;
            _face = _span.At(_along); Invalidate(); FrameCamera(hybrid); Project(hybrid, preview);
            return Loc.Get("wall_span_selected", _span.Members.Count, PlacementGridReader.Length(_frame.Width));
        }

        internal static void OpenDivisions(HybridPlayer hybrid, ItemObjectInPlacement preview)
        {
            if (!Foreground(hybrid) || _stage != Stage.Position || !FrameIsLive()) { Speak(Loc.Get("coordinate_choose_face")); return; }
            if (_span == null) Speak(JoinWall(hybrid, preview, centre: false));
            if (_span == null) return;
            var span = _span; var frame = _frame;
            Invalidate();
            CoordinateEntryHandler.Open(hybrid, preview, Loc.Get("wall_span_division_hint"), 1, 3,
                () => SameContext() && _span == span && FrameIsLive() && frame.Same(_frame),
                (index, count) =>
                {
                    if (!WallSpanReader.Division(frame.Width, index, count, out float along))
                    { Speak(Loc.Get("wall_span_division_invalid")); return; }
                    _along = along; _face = _span.At(along); Invalidate(); Project(hybrid, preview);
                });
        }

        internal static string AlignDoorFloor(HybridPlayer hybrid, ItemObjectInPlacement preview)
        {
            if (!Foreground(hybrid) || !SameContext() || _stage != Stage.Position || !FrameIsLive()) return Loc.Get("coordinate_choose_face");
            if (!DoorPlacementReader.IsDoor(_item) || !DoorPlacementReader.TryFloor(_face, out float floor)) return Loc.Get("door_floor_unknown");
            _height = floor + _item.CubeTransform.FinalSize.y / 2 - _frame.Origin.y;
            Invalidate(); Project(hybrid, preview); return Loc.Get("door_floor_requested");
        }

        private static void TargetsChanged()
        {
            Reload(); Speak(Loc.Get("wall_item_targets_changed") + ". " + Describe());
        }

        private static void FrameCamera(HybridPlayer hybrid)
        {
            Vector3 point = AimPoint(_player.ItemInPlacement, _frame, _along, _height);
            if (!WorldTargeting.FrameArea(_player, _lot, _layer, point, "wall-item"))
            { Speak(Loc.Get("explorer_frame_failed")); return; }
            var camera = hybrid.HybridCamera.FreeCamera;
            // UpdateFreeCamera positions the camera behind LookTarget.forward. The chosen face's
            // outward mesh normal therefore defines the view without naming a compass direction.
            camera.LookTarget.rotation = Quaternion.LookRotation(-_frame.Normal, Vector3.up);
            camera.XAngleRatio = 0f;
            var settings = global::Settings.Get<global::Setting.FreeCamera>();
            if (settings != null && PlacementGrid.Finite(settings.MinZoomDistance) && PlacementGrid.Finite(settings.MaxZoomDistance)
                && settings.MinZoomDistance > 0f && settings.MaxZoomDistance >= settings.MinZoomDistance)
            {
                camera.TargetDistance = Mathf.Clamp(3f, settings.MinZoomDistance, settings.MaxZoomDistance);
                float ratio = (camera.TargetDistance - settings.MinZoomDistance) / Mathf.Max(.001f, settings.MaxZoomDistance - settings.MinZoomDistance);
                float minimumAngle = settings.StartMinAngle + (settings.MinLookAngleByDistance?.Evaluate(ratio) ?? 0f)
                    * (settings.EndMinAngle - settings.StartMinAngle);
                camera.XAngleRatio = Mathf.Clamp01((70f - minimumAngle) / Mathf.Max(.001f, settings.MaxAngle - minimumAngle));
            }
            camera.TargetDistanceSpeed = 0f; camera.CurrentExtraLookUpRatio = 0f;
        }

        internal static Vector3 AimPoint(ItemObjectInPlacement preview, WallFaceFrame frame, float along, float height)
        {
            var root = preview.Item;
            Vector3 mouseOffset = preview.LocalMouseOffset; mouseOffset.z = 0f;
            Vector3 offset = root.ItemOffset != null ? root.ItemOffset.Offset : Vector3.zero;
            // Invert the straight-wall cursor offsets in UpdateMoveItem: its second ray shifts its
            // origin by -(rotated item offset + half depth); final in-plane position adds the mouse
            // offset and WallOffset. This chooses an aim only. Native snapping/attachment still run,
            // and the resulting centre must be measured before confirmation (game-api §105).
            Vector3 aim = frame.Point(along, height) + root.transform.rotation
                * (offset + Vector3.forward * (root.CubeTransform.FinalSize.z / 2f) - mouseOffset - root.WallOffset);
            // UpdateItemDoor fixes Y for every SnapsToBottomOfWall item, including sinks.
            // Its WallOffset can put the inverse centre ray below the floor. Aim at the wall's
            // interior instead; native floor anchoring still determines the observed final Y.
            if (root.SnapsToBottomOfWall)
                // Leave room above the first slanted ray before native rotation aligns a
                // deep fixture, and below it after the depth offset turns toward the camera.
                // Doors still need the header above their temporary preview opening.
                aim.y = frame.Origin.y + (DoorPlacementReader.IsDoor(root) ? frame.Height - .05f : frame.Height * .75f);
            return aim;
        }

        private static void Project(HybridPlayer hybrid, ItemObjectInPlacement preview)
        {
            string failure = null;
            Vector2 screen = default(Vector2);
            Vector3 size = preview.Item.CubeTransform.FinalSize;
            if (!WallFaceFrame.Finite(size) || size.x <= 0f || size.y <= 0f || size.z <= 0f
                || _along < size.x / 2f || _along > _frame.Width - size.x / 2f
                || _height < size.y / 2f || _height > _frame.Height - size.y / 2f)
                failure = Loc.Get("wall_item_outside");
            else if (Mouse.current == null) failure = Loc.Get("placement_mouse_missing");
            else if (!ItemPlacementHandler.TryProjectTarget(hybrid, AimPoint(preview, _frame, _along, _height), out screen))
                failure = Loc.Get("wall_item_out_of_view");
            if (failure != null)
            {
                _stable = 0; _hasScreen = false;
                if (failure != _failure) Speak(failure);
                _failure = failure; return;
            }
            _failure = null;
            if (!_hasScreen || Vector2.Distance(screen, _screen) > 0.25f)
            {
                _screen = screen; _hasScreen = true; _stable = 0; _announce = true;
                Pointer.Expect(screen);
                Mouse.current.WarpCursorPosition(screen);
            }
            _screen = screen; // Keep the precise native handoff current even below the OS-warp threshold.
        }

        private static void BeforeNativeMove(Player player)
        {
            try
            {
                var hybrid = PlayerManager.Instance?.HybridPlayer1;
                if (player != _player || !Foreground(hybrid) || CoordinateEntryHandler.BlocksPlacement || _stage != Stage.Position || !_hasScreen
                    || PlacementActionsHandler.Active || _failure != null || !SameContext() || !FrameIsLive()) return;
                // The native handoff preserves fractional pixels every movement pass. A rounded OS
                // cursor must not move an accurately centred preview on the following idle frame.
                player.ItemInPlacement.CursorTeleportPosition = new Vector3(_screen.x, _screen.y, 0f);
                player.ItemInPlacement.CursorTeleported = true;
            }
            catch (Exception ex) { Fail(ex); }
        }

        internal static void AfterGameSystems(HybridPlayer hybrid, ItemObjectInPlacement preview)
        {
            if (!Foreground(hybrid)) { Invalidate(); return; }
            if (_submittedFrame == Time.frameCount)
            {
                _submittedFrame = -1; _stable = 0; _announce = true;
                Speak(Loc.Get("wall_item_not_placed")); return;
            }
            if (_stage != Stage.Position || !_hasScreen || _failure != null || preview.CursorTeleported) return;
            bool same = SameObservation(preview);
            _stable = same ? _stable + 1 : 1;
            _observedPosition = _item.CubeTransform.LocalPosition; _observedSize = _item.CubeTransform.FinalSize;
            _observedRotation = _item.transform.rotation; _observedSupport = preview.LastCollidedObject;
            _observedNormal = preview.LastCollidedNormal; _observedPrice = _item.GetPrice();
            if (_stable < 2) return;
            string status = Ready(hybrid, preview) ?? Loc.Get("wall_item_ready");
            bool priceChanged = _announcedPrice != _item.GetPrice();
            bool sizeChanged = Vector3.Distance(_announcedSize, _item.CubeTransform.FinalSize) > .001f;
            if (_announce || status != _lastStatus || priceChanged || sizeChanged)
            {
                _announce = false; _lastStatus = status;
                string report = DescribeMovement() + ". " + status;
                if (priceChanged) report += ". " + Loc.Get("wall_item_price_changed", FormatService.GetMoney(_item.GetPrice()));
                if (sizeChanged) report += ". " + PlacementGridReader.Dimensions(_item);
                _announcedPrice = _item.GetPrice(); _announcedSize = _item.CubeTransform.FinalSize;
                Speak(report);
                Plugin.Log.LogInfo($"[WallItem] preview={_item.InstanceID}, wall={_face.WallId}, section={_face.SectionId}, "
                    + $"side={_face.Face.WallSide}, target=({_along},{_height}), actual={_observedPosition}, status={status}.");
            }
        }

        internal static void AcceptActual(HybridPlayer hybrid, ItemObjectInPlacement preview)
        {
            if (preview?.Item != _item || _stage != Stage.Position || !FrameIsLive()) return;
            _along = _frame.Along(_item.CubeTransform.LocalPosition);
            _height = _frame.AboveBottom(_item.CubeTransform.LocalPosition);
            Invalidate(); Project(hybrid, preview);
        }

        private static bool SameObservation(ItemObjectInPlacement preview) => _stable > 0
            && Vector3.Distance(_observedPosition, _item.CubeTransform.LocalPosition) < 0.001f
            && Vector3.Distance(_observedSize, _item.CubeTransform.FinalSize) < 0.001f
            && Quaternion.Angle(_observedRotation, _item.transform.rotation) < 0.1f
            && _observedSupport == preview.LastCollidedObject
            && Vector3.Distance(_observedNormal, preview.LastCollidedNormal) < 0.001f
            && _observedPrice == _item.GetPrice();

        private static string Ready(HybridPlayer hybrid, ItemObjectInPlacement preview)
        {
            if (!Foreground(hybrid) || !SameContext() || _stage != Stage.Position || _face == null
                || !FrameIsLive()) return Loc.Get("wall_item_targets_changed");
            if (CoordinateEntryHandler.BlocksPlacement) return Loc.Get("placement_wait");
            if (_failure != null) return _failure;
            if (!_hasScreen || _stable < 2 || preview.CursorTeleported || !SameObservation(preview)
                || preview.FromRotationWidget || preview.IsInOffset || _player.ItemInRotation != null
                || !BuildModeRefreshManager.IsZoneStateRefreshed || ZoneManager.Instance.DirtyWallZones.Count != 0)
                return Loc.Get("placement_wait");
            var section = preview.LastCollidedObject != null ? preview.LastCollidedObject.GetComponent<SegmentWallSectionObject>() : null;
            if (preview.LastColliderType != ColliderType.Wall || section == null || !MatchesSection(section)
                || Vector3.Dot(preview.LastCollidedNormal.normalized, _frame.Normal) < 0.99f
                || (_item.WallSectionPlacedOn != null && _item.WallSectionPlacedOn != section)
                || _item.LotPlacedOnGUID != _lot || _item.Layer != _layer)
                return Loc.Get("wall_item_wrong_face");
            if (Math.Abs(_frame.Along(_item.CubeTransform.LocalPosition) - _along) > AlignmentTolerance
                || Math.Abs(_frame.AboveBottom(_item.CubeTransform.LocalPosition) - _height) > AlignmentTolerance)
                return Loc.Get("wall_item_alignment_changed");
            return ItemPlacementHandler.SemanticPlacementProblem(hybrid, _item);
        }

        private static void BeforeNativeEnd(Player player)
        {
            if (player != _player || _pendingFrame != Time.frameCount) return;
            _pendingFrame = -1;
            try
            {
                var hybrid = PlayerManager.Instance.HybridPlayer1;
                var preview = player.ItemInPlacement;
                if (hybrid.ButtonConfirm.Down || hybrid.ButtonConfirm.Up || hybrid.ButtonConfirm.Pressed) return;
                string problem = Ready(hybrid, preview);
                if (problem != null) { Speak(problem); return; }
                Vector3 offset = _item.ItemOffset != null ? _item.transform.rotation * _item.ItemOffset.Offset : Vector3.zero;
                // Recheck the exact first and second native wall rays at the purchase boundary.
                var first = RaycastSystem.ScreenCastAndGoThroughTerrain(new Vector3(_screen.x, _screen.y, 0),
                    includeTerrain: true, includeFloor: false, includeWalls: true, includeObjects: false, rayOffset: -offset);
                var second = RaycastSystem.ScreenCast(new Vector3(_screen.x, _screen.y, 0), includeTerrain: false, includeFloor: false, includeWalls: true, includeObjects: false,
                    rayOffset: -(offset + _item.transform.rotation * Vector3.forward * (_item.CubeTransform.FinalSize.z / 2f)));
                if (first.type != ColliderType.Wall || second.type != ColliderType.Wall
                    || !(_span != null ? _span.ContainsRenderer(second.gameObject) : second.gameObject == _face.Face.gameObject)
                    || Vector3.Dot(second.hit.normal.normalized, _frame.Normal) < 0.99f)
                { Speak(Loc.Get("wall_item_wrong_face")); return; }
                _injectedButton = hybrid.ButtonConfirm;
                _injectedButton.SetValue(0f);
                _submittedFrame = Time.frameCount;
            }
            catch (Exception ex) { AfterNativeEnd(player); Fail(ex); }
        }

        private static void AfterNativeEnd(Player player)
        {
            if (_injectedButton == null) return;
            var button = _injectedButton; _injectedButton = null;
            button.Update(); // Clear this Up without firing a second OnUp callback.
        }

        internal static string Describe(bool detailed = false)
        {
            if (!Enabled) return Loc.Get("wall_item_unavailable");
            if (_stage == Stage.Position)
            {
                var point = _item.CubeTransform.LocalPosition;
                string position = detailed ? DescribePosition() + ". " + Address(_frame) : DescribeMovement();
                return position + ". " + (Ready(PlayerManager.Instance.HybridPlayer1, _player.ItemInPlacement) ?? Loc.Get("wall_item_ready"));
            }
            var group = Group;
            if (group == null) return Loc.Get("wall_item_no_faces");
            string room = group.Room == null ? Loc.Get("wall_item_outside_group")
                : Loc.Get("wall_item_room", RoomLabelHandler.Name(group.Room),
                    ScreenReader.NormalizeForOutput(MeasurementUnitService.FormatSurfaceArea(group.Room.SurfaceArea)), _layer);
            if (_stage == Stage.Rooms) return Loc.Get("wall_item_group", room, group.Faces.Count);
            var face = group.Faces[_faceIndex];
            if (!face.TryRead(_lot, _layer, out var frame)) return Loc.Get("wall_item_targets_changed");
            return Loc.Get("wall_item_face", _faceIndex + 1, group.Faces.Count, room,
                PlacementGridReader.Length(frame.Width), PlacementGridReader.Length(frame.Height)) + ". "
                + RoomSpatialReader.DescribeSide(face, frame) + (detailed ? ". " + Address(frame) : "");
        }

        private static string Address(WallFaceFrame frame)
        {
            if (!PlacementGridReader.TryRead(_lot, ItemPlacementHandler.GridSpacingMetres, out var grid))
                return Loc.Get("wall_item_address_unavailable");
            return Loc.Get("wall_item_address", PlacementGridReader.Position(grid, frame.Origin),
                PlacementGridReader.Position(grid, frame.Point(frame.Width, 0f)), PlacementGridReader.Length((float)grid.Spacing));
        }

        private static string DescribePosition()
        {
            var point = _item.CubeTransform.LocalPosition;
            string position = (_span != null ? Loc.Get("wall_span_selected", _span.Members.Count, PlacementGridReader.Length(_frame.Width)) + ". " : "")
                + RoomSpatialReader.DescribeSide(_face, _frame) + ". " + Loc.Get("wall_item_position", _faceIndex + 1,
                PlacementGridReader.Length(_frame.Along(point)), PlacementGridReader.Length(_frame.Width),
                PlacementGridReader.Length(_frame.AboveBottom(point)), PlacementGridReader.Length(_along),
                PlacementGridReader.Length(_height), FormatService.GetMoney(_item.GetPrice()), PlacementGridReader.Dimensions(_item))
                + RequestedDifference(point);
            return string.Join(". ", new[] { position, DoorPlacementReader.Describe(_item, _face),
                RequiredFloorHeight(out float floorHeight) ? Loc.Get("wall_item_floor_height", PlacementGridReader.Length(floorHeight)) : null }
                .Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part.TrimEnd(' ', '.')));
        }

        private static string DescribeMovement()
        {
            var point = _item.CubeTransform.LocalPosition;
            string side = _face.Room != null ? Loc.Get("wall_item_room_side", RoomLabelHandler.Name(_face.Room), RoomSpatialReader.LotSide(_lot, _frame))
                : RoomSpatialReader.DescribeSide(_face, _frame);
            return Loc.Get("wall_item_move", side, PlacementGridReader.Length(_frame.Along(point)),
                PlacementGridReader.Length(_frame.AboveBottom(point))) + RequestedDifference(point);
        }

        private static string RequestedDifference(Vector3 point) =>
            Math.Abs(_frame.Along(point) - _along) > AlignmentTolerance || Math.Abs(_frame.AboveBottom(point) - _height) > AlignmentTolerance
                ? ". " + Loc.Get("wall_item_requested", PlacementGridReader.Length(_along), PlacementGridReader.Length(_height)) : "";

        internal static string DescribeResult(ItemObjectRoot placed)
        {
            if (placed == null || placed.CubeTransform == null || !WallFaceFrame.Finite(placed.CubeTransform.LocalPosition)
                || _stage != Stage.Position || !FrameIsLive() || !MatchesSection(placed.WallSectionPlacedOn)
                || placed.LotPlacedOnGUID != _lot || placed.Layer != _layer
                || Math.Abs(_frame.Along(placed.CubeTransform.LocalPosition) - _along) > AlignmentTolerance
                || Math.Abs(_frame.AboveBottom(placed.CubeTransform.LocalPosition) - _height) > AlignmentTolerance)
                return Loc.Get("wall_item_result_unverified");
            Plugin.Log.LogInfo($"[WallItem] attached item={placed.InstanceID}, section={placed.WallSectionPlacedOn.InstanceID}, members={_span?.Members.Count ?? 1}, target=({_along},{_height}).");
            return Loc.Get(_span != null ? "wall_span_result" : "wall_item_result", _span?.Members.Count ?? _faceIndex + 1,
                PlacementGridReader.Length(_along), PlacementGridReader.Length(_height)) + ". " + RoomSpatialReader.DescribeSide(_face, _frame);
        }

        private static void Fail(Exception ex)
        {
            Enabled = false; Invalidate();
            Plugin.Log.LogError("Wall-item placement stopped: " + ex);
            Speak(Loc.Get("wall_item_unavailable"));
        }

        private static void Speak(string message) => ScreenReader.Speak(message);
    }
}
