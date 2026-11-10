using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ParalivesAccess.Speech;
using Setting;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ParalivesAccess.Handlers
{
    /// <summary>
    /// Makes new and existing floor and supported wall-item previews movable and confirmable while leaving the
    /// game's placement validation, purchase, undo and object registration transaction intact.
    /// </summary>
    public static class ItemPlacementHandler
    {
        /// <summary>Set false to disable accessible item placement.</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>True while a captured preview owns movement, with native modal UI taking priority.</summary>
        public static bool OwnsInput => _trackedInstanceId >= 0
            && HasPlacementForeground(PlayerManager.Instance?.HybridPlayer1);

        internal static bool ApplyPatches(Harmony harmony)
        {
            try
            {
                var method = AccessTools.Method(typeof(StartOffsetItem), "UpdateForPlayer", new[] { typeof(Player) });
                var prefix = AccessTools.Method(typeof(ItemPlacementHandler), nameof(AllowNativeOffset));
                harmony.Patch(method, prefix: new HarmonyMethod(prefix));
                return Harmony.GetPatchInfo(method)?.Prefixes.Any(p => p.owner == Plugin.PluginGuid && p.PatchMethod == prefix) == true;
            }
            catch (Exception ex) { Plugin.Log.LogError("Placement modifier isolation failed: " + ex); return false; }
        }

        // Shift belongs to fine keyboard movement while this preview owns input. Native
        // StartOffsetItem otherwise treats the same modifier as a vertical mouse gesture.
        private static bool AllowNativeOffset(Player player) => !Enabled || !OwnsInput
            || PlayerManager.Instance?.HybridPlayer1?.Player != player
            || player.ItemInPlacement?.Item?.InstanceID != _trackedInstanceId;

        /// <summary>Physical spacing of the accessibility grid, in metres.</summary>
        public static float GridSpacingMetres { get; set; } = 0.5f;

        /// <summary>The most recently verified placed instance, or -1 when none was placed.</summary>
        public static int LastPlacedInstanceId { get; private set; } = -1;

        /// <summary>The owned lot containing <see cref="LastPlacedInstanceId"/>, or zero.</summary>
        public static ulong LastPlacedLotGuid { get; private set; }

        private const float RotationChangeThresholdDegrees = 0.1f;
        private const float FramingWorldStabilityDistance = 0.05f;
        private const int RequiredStableFramingSamples = 2;
        private const int MaxFramingAttempts = 180;

        // Search the unobstructed middle of the viewport before moving toward its edges. These are
        // screen positions, not invented world coordinates: each candidate is accepted only when
        // the same terrain/floor ray used by UpdateMoveItem lands inside the expected lot polygon.
        private static readonly Vector2[] InitialCursorCandidates =
        {
            new Vector2(0.50f, 0.50f),
            new Vector2(0.50f, 0.35f),
            new Vector2(0.50f, 0.65f),
            new Vector2(0.35f, 0.50f),
            new Vector2(0.65f, 0.50f),
            new Vector2(0.35f, 0.35f),
            new Vector2(0.65f, 0.35f),
            new Vector2(0.35f, 0.65f),
            new Vector2(0.65f, 0.65f)
        };

        private enum PlacementState
        {
            None,
            Centering,
            Moving,
            Wall,
            Ceiling,
            Unsupported
        }

        private static PlacementState _state;
        private static int _trackedInstanceId = -1;
        private static bool _trackedWasNew;
        private static ulong _catalogueGuid;
        private static ulong _expectedLotGuid;
        private static string _expectedLotSource;
        private static string _itemName;
        private static int _startingMoney;
        private static int _expectedPrice;

        private static bool _cameraBaselineCaptured;
        private static bool _cameraWasFrozen;
        private static int _framingAttempts;
        private static int _stableFramingSamples;
        private static int _lastFramingCandidateIndex = -1;
        private static Vector3 _lastFramingWorldPoint;
        private static float _framingZoomRatio;
        private static string _anchorSource;
        private static Vector3 _anchorWorldPosition;
        private static ZoneWallObject _anchorRoom;
        private static LotPerimeterZoneObject _expectedLotPerimeter;
        private static List<Vector3> _expectedLotPoints;

        private static Vector2 _cursorTarget;
        private static readonly PlacementPointer Pointer = new PlacementPointer();
        private static PlacementGrid _grid;
        private static Vector3 _requestedPosition;
        private static bool _hasRequestedPosition;
        private static bool _requireExactTarget;
        private static bool _actionAim;
        private static int _coordinateLayer;
        private static PlacementGrid _coordinateGrid;
        private static string _targetFailure;
        private static bool _restoringRoomBoundary;
        private static string _roomBoundaryMessage;
        private static int _validatedFrames;
        private static bool _initialAnnouncementPending;
        private static bool _movementAnnouncementPending;
        private static string _lastStatusSignature;
        private static Quaternion _lastRotation;
        private static int _blockerCheckedFrame = -1;
        private static ItemObjectRoot _blockerCheckedRoot;
        private static ItemObjectRoot _blockingObject;

        /// <summary>
        /// Runs after raw-key instrumentation but before EndMoveItem and UpdateMoveItem.
        /// </summary>
        public static void BeforeGameSystems()
        {
            if (!Enabled || !OwnsInput || PlayerManager.Instance == null || CoordinateEntryHandler.BlocksPlacement)
            {
                return;
            }

            try
            {
                HybridPlayer hybridPlayer = PlayerManager.Instance.HybridPlayer1;
                Player player = hybridPlayer?.Player;
                ItemObjectInPlacement placement = player?.ItemInPlacement;
                if (player == null || placement?.Item == null
                    || placement.Item.InstanceID != _trackedInstanceId)
                {
                    return;
                }

                player.FreezeCameraMovement = true;
                if (placement.FromRotationWidget || placement.IsInOffset || player.ItemInRotation != null) return;
                if (RoofConstructionHandler.Pending(placement))
                {
                    RoofConstructionHandler.TickFit(hybridPlayer, placement);
                    if (_hasRequestedPosition && _actionAim) MaintainWorldCursor(hybridPlayer, placement);
                    return;
                }
                if (NativePlacementAttempt.Before(hybridPlayer, placement)) return;
                if (RoomFitSearch.Before(hybridPlayer, placement)) return;
                if ((_state == PlacementState.Moving || _state == PlacementState.Wall || _state == PlacementState.Ceiling || _state == PlacementState.Unsupported)
                    && PlacementActionsHandler.Handle(hybridPlayer, placement)) return;

                if (_state == PlacementState.Ceiling)
                { CeilingPlacementHandler.Before(hybridPlayer, placement); return; }

                if (_state == PlacementState.Wall)
                {
                    WallItemPlacementHandler.BeforeGameSystems(hybridPlayer, placement);
                    return;
                }

                if (SlotPlacementHandler.Active)
                {
                    SlotPlacementHandler.BeforeGameSystems(hybridPlayer, placement);
                    if (!SlotPlacementHandler.Active) ResumeFloorPlacement(hybridPlayer, placement);
                    return;
                }
                if (SlotPlacementHandler.TryOpen(hybridPlayer, placement)) return;

                if (CoordinateEntryHandler.WasRequested(hybridPlayer))
                {
                    OpenCoordinates(hybridPlayer, placement);
                    return;
                }

                if (_state == PlacementState.Unsupported && !placement.Item.IsCeilingItem
                    && ModInputKey.WasPressed(Keyboard.current, Key.Home))
                {
                    if (!ModInputKey.IsAvailable(hybridPlayer, Key.Home, out var conflict)) Speak(conflict);
                    else BeginTracking(hybridPlayer, placement, centerHouseView: true, keepCameraBaseline: true);
                    return;
                }
                if (_state == PlacementState.Unsupported || hybridPlayer.HasInputFieldFocused)
                {
                    return;
                }

                if (_state == PlacementState.Centering)
                {
                    TryWarpToInitialPoint(hybridPlayer, placement);
                    return;
                }

                if (_state != PlacementState.Moving)
                {
                    return;
                }

                Keyboard keyboard = Keyboard.current;
                if (keyboard == null)
                {
                    return;
                }

                int horizontal = (keyboard.rightArrowKey.wasPressedThisFrame ? 1 : 0)
                    - (keyboard.leftArrowKey.wasPressedThisFrame ? 1 : 0);
                int vertical = (keyboard.upArrowKey.wasPressedThisFrame ? 1 : 0)
                    - (keyboard.downArrowKey.wasPressedThisFrame ? 1 : 0);

                // Wait for native validation of the restored cursor before another action.
                if (_restoringRoomBoundary) return;
                ObserveManualCursor(hybridPlayer, placement);
                bool movedThisFrame = horizontal != 0 || vertical != 0;
                if (movedThisFrame)
                {
                    RoofConstructionHandler.ClearFit(placement);
                    MoveCursor(hybridPlayer, placement, horizontal, vertical,
                        keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed);
                }
                else
                {
                    MaintainWorldCursor(hybridPlayer, placement);
                }

                bool rotatedThisFrame = hybridPlayer.ButtonSmallRotateItemLeft.Down
                    || hybridPlayer.ButtonSmallRotateItemRight.Down
                    || hybridPlayer.ButtonLargeRotateItem.Down;
                if (rotatedThisFrame) RoofConstructionHandler.ClearFit(placement);

                bool enterReleased = keyboard.enterKey.wasReleasedThisFrame
                    || keyboard.numpadEnterKey.wasReleasedThisFrame;
                if (!enterReleased)
                {
                    return;
                }

                if (!string.IsNullOrEmpty(_targetFailure))
                {
                    Speak(_targetFailure);
                    return;
                }

                string actionProblem = RoofConstructionHandler.Problem(hybridPlayer, placement)
                    ?? RoofClearanceReader.Problem(placement.Item)
                    ?? PlacementSupportConsent.FloorProblem(placement)
                    ?? PlacementSupportConsent.Problem(placement)
                    ?? RoomBoundaryConstraint.Problem(_expectedLotGuid, player.GetLotLayer(_expectedLotGuid), placement.Item.CubeTransform.LocalPosition)
                    ?? RoomPlacementGrid.Problem(_expectedLotGuid, player.GetLotLayer(_expectedLotGuid), placement.Item.CubeTransform.LocalPosition)
                    ?? FacingTargetHandler.Problem(hybridPlayer, placement.Item) ?? ItemAlignmentHandler.Problem(hybridPlayer, placement.Item);
                if (actionProblem != null) { Speak(actionProblem); return; }

                if (_requireExactTarget
                    && (!_hasRequestedPosition || !IsFinite(placement.Item.CubeTransform.LocalPosition)
                        || !RefreshGrid() || !_coordinateGrid.SameFrame(_grid)
                        || player.GetLotLayer(_expectedLotGuid) != _coordinateLayer
                        || Vector2.Distance(new Vector2(placement.Item.CubeTransform.LocalPosition.x, placement.Item.CubeTransform.LocalPosition.z),
                            new Vector2(_requestedPosition.x, _requestedPosition.z)) > 0.01f))
                { Speak(Loc.Get("coordinate_mismatch")); return; }

                if (movedThisFrame || rotatedThisFrame || _validatedFrames < 1
                    || placement.CursorTeleported)
                {
                    Plugin.Log.LogInfo(
                        $"[Placement] confirm refused before bridge: id={_trackedInstanceId}, "
                      + $"moved={movedThisFrame}, rotated={rotatedThisFrame}, "
                      + $"validatedFrames={_validatedFrames}, "
                      + $"cursorTeleported={placement.CursorTeleported}.");
                    Speak(Loc.Get("placement_wait"));
                    return;
                }

                if (InputManager.Instance.IsPointerOverUI(player.PlayerIndex))
                {
                    Plugin.Log.LogInfo(
                        $"[Placement] confirm refused before bridge: id={_trackedInstanceId}, "
                      + "pointerOverUI=True.");
                    ReframeCurrentPoint(hybridPlayer, placement);
                    return;
                }

                ItemObjectRoot blocker = IsInsideOwnedLot(placement.Item)
                    ? GetBlockingObject(player, placement.Item)
                    : null;
                if (blocker != null)
                {
                    string blockerName = ItemDescription.GetProductIdentity(blocker) ?? Loc.Get("item_unknown");
                    Plugin.Log.LogInfo(
                        $"[Placement] confirm refused before bridge: id={_trackedInstanceId}, "
                      + $"occupiedBy={blocker.InstanceID}, occupiedName='{blockerName}'.");
                    Speak(Loc.Get("placement_occupied_move", blockerName));
                    return;
                }

                Plugin.Log.LogInfo(
                    $"[Placement] injecting Enter release as ButtonConfirm.Up: "
                  + DescribeDiagnostic(player, placement.Item));
                if (!placement.IsNewItem) ExistingItemEditHandler.QueueConfirm();
                else hybridPlayer.ButtonConfirm.SetValue(0f);
            }
            catch (Exception ex)
            {
                DisableAfterException("prefix", ex);
            }
        }

        /// <summary>
        /// Captures previews and observes placement/transaction results after the game systems.
        /// </summary>
        public static void AfterGameSystems()
        {
            if (!Enabled || PlayerManager.Instance == null)
            {
                return;
            }

            try
            {
                HybridPlayer hybridPlayer = PlayerManager.Instance.HybridPlayer1;
                Player player = hybridPlayer?.Player;
                if (player == null)
                {
                    return;
                }

                ItemObjectInPlacement current = player.ItemInPlacement;
                if (_trackedInstanceId < 0)
                {
                    if (current?.Item != null && (current.IsNewItem || ExistingItemEditHandler.CanTrack(hybridPlayer, current)))
                    {
                        BeginTracking(hybridPlayer, current, centerHouseView: current.IsNewItem, keepCameraBaseline: false);
                    }
                    return;
                }

                if (current?.Item == null || current.Item.InstanceID != _trackedInstanceId
                    || current.IsNewItem != _trackedWasNew)
                {
                    FinishTrackedPreview(hybridPlayer, current);
                    return;
                }
                if (PlacementActionsHandler.Active) return;

                if (_state == PlacementState.Ceiling)
                { CeilingPlacementHandler.After(hybridPlayer, current); return; }

                if (_state == PlacementState.Wall)
                {
                    WallItemPlacementHandler.AfterGameSystems(hybridPlayer, current);
                    return;
                }

                if (SlotPlacementHandler.Active)
                {
                    SlotPlacementHandler.AfterGameSystems(hybridPlayer, current);
                    return;
                }

                if (!OwnsInput) return;

                if (_state == PlacementState.Centering)
                {
                    return;
                }

                if (_state != PlacementState.Moving)
                {
                    return;
                }

                if (CoordinateEntryHandler.BlocksPlacement) return;
                if (!current.CursorTeleported && RestoreRoomBoundary(hybridPlayer, current)) return;
                if (!current.CursorTeleported)
                {
                    _validatedFrames++;
                }

                ObservePlacementState(hybridPlayer, current);
            }
            catch (Exception ex)
            {
                DisableAfterException("postfix", ex);
            }
        }

        private static void BeginTracking(
            HybridPlayer hybridPlayer,
            ItemObjectInPlacement placement,
            bool centerHouseView,
            bool keepCameraBaseline)
        {
            ItemObjectRoot root = placement.Item;
            RoomBoundaryConstraint.Clear();
            PlacementSupportConsent.Begin(placement);
            _restoringRoomBoundary = false; _roomBoundaryMessage = null;
            _trackedInstanceId = root.InstanceID;
            _trackedWasNew = placement.IsNewItem;
            if (!_trackedWasNew) ExistingItemEditHandler.Track(placement);
            _catalogueGuid = root.CatalogueGUID;
            _itemName = ItemDescription.GetProductIdentity(root) ?? Loc.Get("item_unknown");
            _expectedPrice = root.GetPrice();
            _startingMoney = ReadMoney();
            _expectedLotGuid = ResolvePlacementLot(hybridPlayer.Player, out _expectedLotSource);
            if (!_trackedWasNew) { _expectedLotGuid = root.PreviousLotPlacedOnGUID; _expectedLotSource = "existing-item"; }
            _lastRotation = root.transform.rotation;
            _lastStatusSignature = null;
            _validatedFrames = 0;
            _grid = null;
            _hasRequestedPosition = false;
            _requireExactTarget = false;
            _actionAim = false;
            _targetFailure = null;
            _initialAnnouncementPending = false;
            _movementAnnouncementPending = false;
            _framingAttempts = 0;
            _stableFramingSamples = 0;
            _lastFramingCandidateIndex = -1;
            _lastFramingWorldPoint = default(Vector3);
            _framingZoomRatio = 0f;
            _anchorSource = null;
            _anchorRoom = null;
            _expectedLotPerimeter = null;
            _expectedLotPoints = null;
            ResetBlockingObjectCache();

            if (!keepCameraBaseline)
            {
                bool transferred = BuildCatalogHandler.TryTransferCameraBaseline(out _cameraWasFrozen);
                if (!transferred)
                {
                    _cameraWasFrozen = hybridPlayer.Player.FreezeCameraMovement;
                }
                _cameraBaselineCaptured = true;
            }

            hybridPlayer.Player.FreezeCameraMovement = true;

            Plugin.Log.LogInfo(
                $"[Placement] captured id={_trackedInstanceId}, catalog={_catalogueGuid}, "
              + $"name='{_itemName}', price={_expectedPrice}, money={_startingMoney}, "
              + $"expectedLot={_expectedLotGuid}, lotSource={_expectedLotSource}, "
              + $"wall={root.IsWallItem}, "
              + $"ceiling={root.IsCeilingItem}, repeat={!centerHouseView}.");

            if (root.IsWallItem)
            {
                _state = PlacementState.Wall;
                WallItemPlacementHandler.Begin(hybridPlayer, placement, _expectedLotGuid);
                return;
            }

            if (root.IsCeilingItem)
            {
                _state = PlacementState.Ceiling;
                CeilingPlacementHandler.Begin(hybridPlayer, placement, _expectedLotGuid);
                return;
            }

            if (!centerHouseView)
            {
                RoomBoundaryConstraint.Begin(placement, _expectedLotGuid, root.Layer, root.CubeTransform.LocalPosition);
                _state = PlacementState.Moving;
                _validatedFrames = 1;
                _cursorTarget = InputManager.Instance.GetCursorPosition(hybridPlayer.Player.PlayerIndex);
                if (!RefreshGrid()) _targetFailure = Loc.Get("placement_grid_unavailable");
                _lastStatusSignature = BuildStatusSignature(hybridPlayer.Player, root);
                _initialAnnouncementPending = true;
                Plugin.Log.LogInfo(
                    "[Placement] repeat preview state "
                  + DescribeDiagnostic(hybridPlayer.Player, root));
                return;
            }

            if (_expectedLotGuid == 0L)
            {
                _state = PlacementState.Unsupported;
                Speak(Loc.Get("placement_lot_required"));
                return;
            }

            if (!TryCenterOnOwnedLot(hybridPlayer))
            {
                RefuseFraming("setup-rejected", Loc.Get("placement_anchor_failed"));
                return;
            }

            RoomBoundaryConstraint.Begin(placement, _expectedLotGuid, hybridPlayer.Player.GetLotLayer(_expectedLotGuid), _anchorWorldPosition);
            _state = PlacementState.Centering;
            Speak(Loc.Get("placement_framing"));
        }

        private static void ResumeFloorPlacement(HybridPlayer hybrid, ItemObjectInPlacement preview)
        {
            _actionAim = false;
            _requireExactTarget = false;
            _state = PlacementState.Moving;
            _validatedFrames = 0; _hasRequestedPosition = false; _targetFailure = null;
            _cursorTarget = InputManager.Instance.GetCursorPosition(hybrid.Player.PlayerIndex);
            _expectedLotGuid = ResolvePlacementLot(hybrid.Player, out _expectedLotSource);
            if (!RefreshGrid()) _targetFailure = Loc.Get("placement_grid_unavailable");
            _lastStatusSignature = null;
        }

        private static bool TryCenterOnOwnedLot(HybridPlayer hybridPlayer)
        {
            Player player = hybridPlayer?.Player;
            if (player == null)
            {
                return RejectFramingSetup("player-missing");
            }

            if (_expectedLotGuid == 0L)
            {
                return RejectFramingSetup("lot-unresolved");
            }

            if (HouseholdManager.Instance == null)
            {
                return RejectFramingSetup("household-manager-missing");
            }

            if (!HouseholdManager.Instance.OwnsLot(_expectedLotGuid))
            {
                return RejectFramingSetup("lot-not-owned");
            }

            HybridCameraFree freeCamera = hybridPlayer.HybridCamera?.FreeCamera;
            if (freeCamera == null)
            {
                return RejectFramingSetup("free-camera-missing");
            }

            LotPerimeterZoneObject perimeter =
                ZoneManager.Instance?.GetLotPerimeterZoneObjectByGUID(_expectedLotGuid);
            if (perimeter == null || perimeter.Segments == null || perimeter.Segments.Count < 2
                || !IsFinite(perimeter.AveragePosition))
            {
                return RejectFramingSetup("lot-perimeter-unusable");
            }

            _expectedLotPerimeter = perimeter;
            _expectedLotPoints = perimeter.GetPoints();
            if (_expectedLotPoints == null || _expectedLotPoints.Count < 3)
            {
                return RejectFramingSetup("lot-polygon-unusable");
            }

            int currentLayer = player.GetLotLayer(_expectedLotGuid);
            ZoneWallObject containingRoom = null;
            ZoneWallObject largestRoomOnLayer = null;
            ZoneWallObject largestRoom = null;
            float containingArea = float.MaxValue;
            float largestLayerArea = -1f;
            float largestArea = -1f;

            Transform lookTarget = freeCamera.LookTarget;
            bool hasCameraTarget = lookTarget != null && IsFinite(lookTarget.position);
            Vector3 cameraTarget = hasCameraTarget ? lookTarget.position : Vector3.zero;

            List<ZoneObject> zones = ZoneManager.Instance?.GetLotZones(_expectedLotGuid);
            if (zones != null)
            {
                foreach (ZoneObject zone in zones)
                {
                    var room = zone as ZoneWallObject;
                    if (!IsUsableRoom(room))
                    {
                        continue;
                    }

                    float area = float.IsNaN(room.SurfaceArea)
                        || float.IsInfinity(room.SurfaceArea)
                            ? 0f
                            : Mathf.Max(0f, room.SurfaceArea);

                    if (area > largestArea)
                    {
                        largestArea = area;
                        largestRoom = room;
                    }

                    if (room.Layer != currentLayer)
                    {
                        continue;
                    }

                    if (area > largestLayerArea)
                    {
                        largestLayerArea = area;
                        largestRoomOnLayer = room;
                    }

                    // If the camera is already looking into this lot, preserve that room. When it
                    // is outside (the reported road preview), the largest loaded room below is a
                    // deterministic indoor anchor. An empty lot legitimately has no such room and
                    // falls through to its perimeter centre.
                    if (hasCameraTarget && area < containingArea
                        && ZoneGeometry.Contains(room, cameraTarget))
                    {
                        containingArea = area;
                        containingRoom = room;
                    }
                }
            }

            bool cameraOnLot = hasCameraTarget && ZoneGeometry.Contains(perimeter, cameraTarget);
            ZoneWallObject anchorRoom = containingRoom ?? (cameraOnLot ? null : largestRoomOnLayer ?? largestRoom);
            _anchorRoom = anchorRoom;
            if (anchorRoom != null)
            {
                _anchorWorldPosition = containingRoom != null ? cameraTarget : anchorRoom.AveragePosition;
                if (RoomAnchorReader.TryRead(anchorRoom, _anchorWorldPosition, out var interior)) _anchorWorldPosition = interior;
                _anchorSource = containingRoom != null
                    ? "camera-room"
                    : (largestRoomOnLayer != null ? "largest-room-on-layer" : "largest-room");
            }
            else
            {
                _anchorWorldPosition = cameraOnLot ? cameraTarget : perimeter.AveragePosition;
                _anchorSource = cameraOnLot ? "camera-outdoors" : "lot-center";
            }

            global::Setting.FreeCamera cameraSettings = Settings.Get<global::Setting.FreeCamera>();
            float previousZoomRatio = freeCamera.DistanceRatio;
            _framingZoomRatio = cameraSettings != null
                ? cameraSettings.HouseholdChangeZoom
                : 0.3f;
            if (float.IsNaN(_framingZoomRatio) || float.IsInfinity(_framingZoomRatio))
            {
                _framingZoomRatio = 0.3f;
            }
            _framingZoomRatio = Mathf.Clamp01(_framingZoomRatio);

            // ZoomTo deliberately preserves pitch. At close zoom the game may add an extra look-up
            // angle after LookAt, which is why TEST 42 projected the LookTarget below the viewport.
            // Clear only that transient offset; retain the user's normal X-angle and rotation.
            float previousExtraLookUp = freeCamera.CurrentExtraLookUpRatio;
            freeCamera.CurrentExtraLookUpRatio = 0f;
            freeCamera.TargetDistanceSpeed = 0f;
            freeCamera.ZoomTo(_anchorWorldPosition, _framingZoomRatio);

            Plugin.Log.LogInfo(
                $"[Placement] centering id={_trackedInstanceId}, lot={_expectedLotGuid}, "
              + $"lotSource={_expectedLotSource}, anchorSource={_anchorSource}, "
              + $"layer={currentLayer}, area={(anchorRoom != null ? anchorRoom.SurfaceArea : 0f):0.###}, "
              + $"lotArea={perimeter.SurfaceArea:0.###}, lotSegments={perimeter.Segments.Count}, "
              + $"anchor={_anchorWorldPosition}, previousZoomRatio={previousZoomRatio:0.###}, "
              + $"framingZoomRatio={_framingZoomRatio:0.###}, "
              + $"extraLookUp={previousExtraLookUp:0.###}->0, "
              + $"freeCamera={player.IsUsingFreeCamera}, "
              + $"actionBlocked={player.PlayerActionBlockedByUIWindow}, "
              + $"terrainReady={TerrainManager.Instance != null && TerrainManager.Instance.Inited}.");
            return true;
        }

        private static bool RejectFramingSetup(string reason)
        {
            Plugin.Log.LogWarning(
                $"[Placement] framing setup rejected id={_trackedInstanceId}, reason={reason}, "
              + $"lot={_expectedLotGuid}, lotSource={_expectedLotSource}.");
            return false;
        }

        private static bool IsUsableRoom(ZoneWallObject room)
        {
            return room != null && room.Segments != null && room.Segments.Count >= 2
                && IsFinite(room.AveragePosition);
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsNaN(value.y) && !float.IsNaN(value.z)
                && !float.IsInfinity(value.x) && !float.IsInfinity(value.y)
                && !float.IsInfinity(value.z);
        }

        private static void TryWarpToInitialPoint(
            HybridPlayer hybridPlayer,
            ItemObjectInPlacement placement)
        {
            Camera camera = hybridPlayer.HybridCamera?.Camera;
            Mouse mouse = Mouse.current;
            if (camera == null)
            {
                RefuseFraming("camera-missing", Loc.Get("placement_anchor_failed"));
                return;
            }

            if (mouse == null)
            {
                RefuseFraming("mouse-missing", Loc.Get("placement_mouse_missing"));
                return;
            }

            _framingAttempts++;
            if (TryFindInitialOwnedLotPoint(
                hybridPlayer,
                camera,
                out Vector2 screenPoint,
                out Vector3 worldPoint,
                out ColliderType surfaceType,
                out int candidateIndex))
            {
                Vector2 lastXZ = new Vector2(
                    _lastFramingWorldPoint.x,
                    _lastFramingWorldPoint.z);
                Vector2 currentXZ = new Vector2(worldPoint.x, worldPoint.z);
                bool stable = candidateIndex == _lastFramingCandidateIndex
                    && Vector2.Distance(lastXZ, currentXZ) <= FramingWorldStabilityDistance;
                _stableFramingSamples = stable ? _stableFramingSamples + 1 : 1;
                _lastFramingCandidateIndex = candidateIndex;
                _lastFramingWorldPoint = worldPoint;

                if (_stableFramingSamples < RequiredStableFramingSamples)
                {
                    Plugin.Log.LogInfo(
                        $"[Placement] framing candidate id={_trackedInstanceId}, "
                      + $"attempt={_framingAttempts}, candidate={candidateIndex}, "
                      + $"screen=({screenPoint.x:0.0},{screenPoint.y:0.0}), "
                      + $"world={worldPoint}, surface={surfaceType}, "
                      + $"stableSamples={_stableFramingSamples}/{RequiredStableFramingSamples}.");
                    return;
                }

                Vector3 projectedAnchor = camera.WorldToScreenPoint(_anchorWorldPosition);
                HybridCameraFree freeCamera = hybridPlayer.HybridCamera.FreeCamera;
                Plugin.Log.LogInfo(
                    $"[Placement] framing ready id={_trackedInstanceId}, "
                  + $"attempt={_framingAttempts}, candidate={candidateIndex}, "
                  + $"screen=({screenPoint.x:0.0},{screenPoint.y:0.0}), "
                  + $"world={worldPoint}, surface={surfaceType}, "
                  + $"stableSamples={_stableFramingSamples}/{RequiredStableFramingSamples}, "
                  + $"anchorProjected={projectedAnchor}, "
                  + $"distanceRatio={freeCamera.DistanceRatio:0.###}, "
                  + $"targetRatio={_framingZoomRatio:0.###}, "
                  + $"extraLookUp={freeCamera.CurrentExtraLookUpRatio:0.###}.");

                _cursorTarget = screenPoint;
                WarpCursor(placement, _cursorTarget, "initial-owned-lot-ray");
                _state = PlacementState.Moving;
                _initialAnnouncementPending = true;
                return;
            }

            _stableFramingSamples = 0;
            _lastFramingCandidateIndex = -1;
            _lastFramingWorldPoint = default(Vector3);

            if (_framingAttempts == 1 || _framingAttempts % 30 == 0)
            {
                Vector3 projectedAnchor = camera.WorldToScreenPoint(_anchorWorldPosition);
                HybridCameraFree freeCamera = hybridPlayer.HybridCamera.FreeCamera;
                Plugin.Log.LogInfo(
                    $"[Placement] framing wait id={_trackedInstanceId}, "
                  + $"attempt={_framingAttempts}, anchorSource={_anchorSource}, "
                  + $"anchorProjected={projectedAnchor}, "
                  + $"distanceRatio={freeCamera.DistanceRatio:0.###}, "
                  + $"targetRatio={_framingZoomRatio:0.###}, "
                  + $"extraLookUp={freeCamera.CurrentExtraLookUpRatio:0.###}, "
                  + $"freeCamera={hybridPlayer.Player.IsUsingFreeCamera}, "
                  + $"actionBlocked={hybridPlayer.Player.PlayerActionBlockedByUIWindow}, "
                  + $"terrainReady={TerrainManager.Instance != null && TerrainManager.Instance.Inited}; "
                  + "no tested terrain/floor ray is inside the expected lot yet.");
            }

            if (_framingAttempts >= MaxFramingAttempts)
            {
                RefuseFraming("no-owned-lot-ray", Loc.Get("placement_anchor_failed"));
            }
        }

        private static bool TryFindInitialOwnedLotPoint(
            HybridPlayer hybridPlayer,
            Camera camera,
            out Vector2 screenPoint,
            out Vector3 worldPoint,
            out ColliderType surfaceType,
            out int candidateIndex)
        {
            screenPoint = default(Vector2);
            worldPoint = default(Vector3);
            surfaceType = ColliderType.None;
            candidateIndex = -1;

            Rect pixelRect = camera.pixelRect;
            if (pixelRect.width <= 1f || pixelRect.height <= 1f
                || _expectedLotPerimeter == null || _expectedLotPoints == null)
            {
                return false;
            }

            // The camera's focus is not the viewport centre: in the reported room it projects
            // to (960,493), while (960,540) starts the preview on the back edge of the room.
            // Try the actual projected anchor first; keep the existing fallback rays for cases
            // where native camera framing puts that anchor off screen.
            for (int i = 0; i <= InitialCursorCandidates.Length; i++)
            {
                Vector3 candidateScreen;
                if (i == 0)
                {
                    candidateScreen = camera.WorldToScreenPoint(_anchorWorldPosition);
                    if (!IsFinite(candidateScreen) || candidateScreen.z <= 0
                        || candidateScreen.x < pixelRect.xMin || candidateScreen.x > pixelRect.xMax
                        || candidateScreen.y < pixelRect.yMin || candidateScreen.y > pixelRect.yMax) continue;
                }
                else
                {
                    Vector2 normalized = InitialCursorCandidates[i - 1];
                    candidateScreen = new Vector3(pixelRect.xMin + pixelRect.width * normalized.x,
                        pixelRect.yMin + pixelRect.height * normalized.y, 0f);
                }

                RaycastHit hit;
                ColliderType colliderType;
                GameObject ignored;
                (hit, colliderType, ignored) = RaycastSystem.ScreenCastAndGoThroughTerrain(
                    candidateScreen,
                    includeTerrain: true,
                    includeFloor: true,
                    includeWalls: false,
                    includeObjects: false,
                    includeWidgets: false);

                Vector3 candidateWorld;
                if (hit.collider != null
                    && (colliderType == ColliderType.Terrain || colliderType == ColliderType.Floor))
                {
                    candidateWorld = hit.point;
                }
                else
                {
                    candidateWorld = PlaneCastService.GetXZPlanePosition(
                        hybridPlayer.Player.PlayerIndex,
                        candidateScreen,
                        0uL);
                    colliderType = ColliderType.None;
                }

                if (!IsFinite(candidateWorld)
                    || !ZoneGeometry.Contains(_expectedLotPerimeter, candidateWorld)
                    || (_anchorRoom != null && !ZoneGeometry.Contains(_anchorRoom, candidateWorld)))
                {
                    continue;
                }

                screenPoint = new Vector2(candidateScreen.x, candidateScreen.y);
                worldPoint = candidateWorld;
                surfaceType = colliderType;
                candidateIndex = i;
                return true;
            }

            return false;
        }

        private static void RefuseFraming(string reason, string spokenMessage)
        {
            Plugin.Log.LogWarning(
                $"[Placement] framing failed id={_trackedInstanceId}, reason={reason}, "
              + $"attempts={_framingAttempts}, lot={_expectedLotGuid}, "
              + $"anchorSource={_anchorSource}, anchor={_anchorWorldPosition}.");
            _state = PlacementState.Unsupported;
            Speak(spokenMessage);
        }

        private static void MoveCursor(
            HybridPlayer hybridPlayer,
            ItemObjectInPlacement placement,
            int horizontal,
            int vertical,
            bool fine)
        {
            _actionAim = false;
            _requireExactTarget = false;
            if (!RefreshGrid())
            {
                RefuseTarget(Loc.Get("placement_grid_unavailable"));
                return;
            }
            Vector3 actual = placement.Item.CubeTransform.LocalPosition;
            PlacementSupportConsent.Clear();
            Vector3 start = _hasRequestedPosition ? _requestedPosition : actual;
            _grid.Step(start.x, start.z, horizontal, vertical, fine, out double x, out double z);
            // Keep the last native support height. A slope, new surface, item offset or snap can
            // change the result; report that difference after UpdateMoveItem rather than forging it.
            var target = new Vector3((float)x, actual.y, (float)z);
            string boundaryProblem = RoomBoundaryConstraint.Problem(_expectedLotGuid, hybridPlayer.Player.GetLotLayer(_expectedLotGuid), target);
            // A stopped step leaves the last valid point usable. Actual native position is
            // checked again on Enter and at EndMoveItem, so an outside snap cannot be purchased.
            if (boundaryProblem != null) { Speak(boundaryProblem); return; }
            string roomProblem = RoomPlacementGrid.Problem(_expectedLotGuid, hybridPlayer.Player.GetLotLayer(_expectedLotGuid), target);
            if (roomProblem != null) { RefuseTarget(roomProblem); return; }
            if (!TryProjectTarget(hybridPlayer, target, out Vector2 screenPoint))
            {
                RefuseTarget(Loc.Get("placement_target_out_of_view"));
                return;
            }
            _requestedPosition = target;
            _hasRequestedPosition = true;
            _targetFailure = null;
            _cursorTarget = screenPoint;
            WarpCursor(placement, screenPoint, fine ? "grid-fine" : "grid");
            _movementAnnouncementPending = true;
            Plugin.Log.LogInfo($"[PlacementGrid] request id={_trackedInstanceId}, target={target}, "
                + $"spacing={_grid.Spacing}, rotation={_grid.Rotation}, fine={fine}.");
        }

        private static bool RestoreRoomBoundary(HybridPlayer hybrid, ItemObjectInPlacement preview)
        {
            var aim = _hasRequestedPosition ? _requestedPosition : preview.Item.CubeTransform.LocalPosition;
            var cursor = _hasRequestedPosition ? _cursorTarget : (Vector2)InputManager.Instance.GetCursorPosition(hybrid.Player.PlayerIndex);
            bool outside = RoomBoundaryConstraint.ObserveNative(preview, aim, cursor,
                out var restoreAim, out var restoreCursor, out var problem);
            if (outside && string.IsNullOrEmpty(_targetFailure))
            {
                if (_restoringRoomBoundary)
                {
                    // A changed camera/surface can invalidate even a previously verified cursor.
                    // Do not retry every frame or claim that this preview is placeable.
                    _restoringRoomBoundary = false;
                    RefuseTarget(problem);
                    return false;
                }
                _requestedPosition = restoreAim; _hasRequestedPosition = true;
                _cursorTarget = restoreCursor; _requireExactTarget = false; _actionAim = false;
                _restoringRoomBoundary = true; _roomBoundaryMessage = problem;
                _movementAnnouncementPending = false;
                WarpCursor(preview, restoreCursor, "room-boundary-return");
                return true;
            }
            if (_restoringRoomBoundary)
            {
                _restoringRoomBoundary = false;
                if (problem != null) RefuseTarget(problem);
                else Speak(_roomBoundaryMessage);
                _movementAnnouncementPending = false;
            }
            return false;
        }

        private static bool HasPlacementForeground(HybridPlayer hybridPlayer)
        {
            return hybridPlayer?.Player != null && !hybridPlayer.HasInputFieldFocused
                && !hybridPlayer.Player.PlayerActionBlockedByUIWindow
                && !hybridPlayer.IsUsingGamePad
                && hybridPlayer.Player.State == GameStates.BuildMode;
        }

        private static void OpenCoordinates(HybridPlayer hybrid, ItemObjectInPlacement preview)
        {
            if (_state != PlacementState.Moving || !RefreshGrid()) { Speak(Loc.Get("placement_wait")); return; }
            var grid = _grid; int layer = hybrid.Player.GetLotLayer(_expectedLotGuid);
            grid.Coordinates(preview.Item.CubeTransform.LocalPosition.x, preview.Item.CubeTransform.LocalPosition.z, out double column, out double row);
            _validatedFrames = 0;
            CoordinateEntryHandler.Open(hybrid, preview, Loc.Get(RoomPlacementGrid.Selected ? "coordinate_room_hint" : "coordinate_grid_hint", PlacementGridReader.Length(GridSpacingMetres)), column, row,
                () => hybrid.Player.GetLotLayer(_expectedLotGuid) == layer
                    && RoomPlacementGrid.TryRead(_expectedLotGuid, layer, GridSpacingMetres, out var current) && grid.SameFrame(current),
                (x, z) =>
                {
                    grid.World(x, z, out double wx, out double wz);
                    var target = new Vector3((float)wx, preview.Item.CubeTransform.LocalPosition.y, (float)wz);
                    string roomProblem = RoomBoundaryConstraint.Problem(_expectedLotGuid, layer, target)
                        ?? RoomPlacementGrid.Problem(_expectedLotGuid, layer, target);
                    if (roomProblem != null) { RefuseTarget(roomProblem); return; }
                    if (!TryProjectTarget(hybrid, target, out var screen)) { RefuseTarget(Loc.Get("placement_target_out_of_view")); return; }
                    _requestedPosition = target; _hasRequestedPosition = true; _requireExactTarget = true;
                    _actionAim = false;
                    _coordinateGrid = grid; _coordinateLayer = layer; _targetFailure = null; _cursorTarget = screen;
                    WarpCursor(preview, screen, "coordinate-entry"); _movementAnnouncementPending = true;
                });
        }

        private static bool RefreshGrid()
        {
            int layer = PlayerManager.Instance?.HybridPlayer1?.Player?.GetLotLayer(_expectedLotGuid) ?? 0;
            if (!RoomPlacementGrid.TryRead(_expectedLotGuid, layer, GridSpacingMetres, out PlacementGrid current))
            {
                _grid = null;
                _hasRequestedPosition = false;
                return false;
            }
            if (_grid != null && !_grid.SameFrame(current))
            {
                _hasRequestedPosition = false;
                _movementAnnouncementPending = true;
                Speak(Loc.Get("placement_grid_changed"));
            }
            _grid = current;
            return true;
        }

        internal static bool TryProjectTarget(HybridPlayer hybridPlayer, Vector3 target, out Vector2 screenPoint)
        {
            screenPoint = default(Vector2);
            Camera camera = hybridPlayer.HybridCamera?.Camera;
            if (camera == null || !IsFinite(target)) return false;
            Vector3 screen = camera.WorldToScreenPoint(target);
            Rect rect = camera.pixelRect;
            if (!IsFinite(screen) || screen.z <= 0f || rect.width <= 2f || rect.height <= 2f
                || screen.x < rect.xMin + 1f || screen.x > rect.xMax - 1f
                || screen.y < rect.yMin + 1f || screen.y > rect.yMax - 1f) return false;
            screenPoint = new Vector2(screen.x, screen.y);
            return true;
        }

        private static void ReframeCurrentPoint(HybridPlayer hybrid, ItemObjectInPlacement preview)
        {
            Vector3 point = preview.Item.CubeTransform.LocalPosition;
            if (!WorldTargeting.FrameArea(hybrid.Player, preview.Item.LotPlacedOnGUID, preview.Item.Layer, point, "placement-interface-recovery"))
            { Speak(Loc.Get("placement_pointer_over_ui")); return; }
            hybrid.HybridCamera.FreeCamera.CurrentExtraLookUpRatio = 0f;
            hybrid.HybridCamera.FreeCamera.TargetDistanceSpeed = 0f;
            _requestedPosition = point; _hasRequestedPosition = true; _actionAim = true;
            _coordinateGrid = _grid; _coordinateLayer = preview.Item.Layer; _requireExactTarget = true;
            _validatedFrames = 0; _targetFailure = null; _movementAnnouncementPending = true;
            if (TryProjectTarget(hybrid, SlotPlacementHandler.AimPoint(hybrid.Player, preview, point), out var screen))
            { _cursorTarget = screen; WarpCursor(preview, screen, "interface-recovery"); }
            Speak(Loc.Get("placement_view_reframed"));
        }

        private static void RefuseTarget(string message)
        {
            _targetFailure = message;
            Speak(message);
        }

        private static void ObserveManualCursor(HybridPlayer hybridPlayer, ItemObjectInPlacement placement)
        {
            if (!_hasRequestedPosition || placement.CursorTeleported || _validatedFrames < 1) return;
            Vector2 current = InputManager.Instance.GetCursorPosition(hybridPlayer.Player.PlayerIndex);
            if (Pointer.WasMoved(current))
            {
                RoofConstructionHandler.ClearFit(placement);
                _requireExactTarget = false;
                _hasRequestedPosition = false;
                _targetFailure = null;
                _validatedFrames = 0;
                _movementAnnouncementPending = true;
            }
        }

        internal static void MaintainWorldCursor(HybridPlayer hybridPlayer, ItemObjectInPlacement placement)
        {
            if (!_hasRequestedPosition || !string.IsNullOrEmpty(_targetFailure)) return;
            Vector3 aim = _actionAim ? SlotPlacementHandler.AimPoint(hybridPlayer.Player, placement, _requestedPosition) : _requestedPosition;
            if (!TryProjectTarget(hybridPlayer, aim, out Vector2 screenPoint))
            {
                RefuseTarget(Loc.Get("placement_target_out_of_view"));
                return;
            }
            if (Vector2.Distance(screenPoint, _cursorTarget) > 0.5f)
            {
                _cursorTarget = screenPoint;
                WarpCursor(placement, screenPoint, "grid-camera-reprojection");
            }
        }

        /// <summary>Provides a live F3 report while placement, rather than a modal, owns the keyboard.</summary>
        internal static bool TryGetContext(HybridPlayer hybridPlayer, out string report)
        {
            return GetContext(hybridPlayer, false, out report);
        }

        internal static bool TryGetDetailedContext(HybridPlayer hybridPlayer, out string report)
        {
            return GetContext(hybridPlayer, true, out report);
        }

        private static bool GetContext(HybridPlayer hybridPlayer, bool detailed, out string report)
        {
            report = null;
            ItemObjectRoot root = hybridPlayer?.Player?.ItemInPlacement?.Item;
            if (!Enabled || !OwnsInput || root == null || root.InstanceID != _trackedInstanceId) return false;
            if (PlacementActionsHandler.Active) report = PlacementActionsHandler.Describe();
            else if (_state == PlacementState.Wall)
                report = WallItemPlacementHandler.Describe(detailed);
            else if (_state == PlacementState.Ceiling)
                report = CeilingPlacementHandler.Describe(detailed);
            else if (SlotPlacementHandler.Active)
                report = SlotPlacementHandler.Describe(detailed);
            else if (_state == PlacementState.Centering)
                report = Loc.Get("placement_framing");
            else if (_state == PlacementState.Unsupported)
                report = root.IsWallItem ? Loc.Get("placement_unsupported_wall")
                    : root.IsCeilingItem ? Loc.Get("placement_unsupported_ceiling") : Loc.Get("placement_anchor_failed");
            else if (_validatedFrames < 1)
                report = Loc.Get("placement_wait");
            else
            {
                if (!RefreshGrid()) _targetFailure = Loc.Get("placement_grid_unavailable");
                report = Loc.Get(detailed ? (RoomPlacementGrid.Selected ? "placement_room_context" : "placement_grid_context") : "placement_context_short", _itemName, DescribeStatus(hybridPlayer, root, detailed),
                    PlacementGridReader.Dimensions(root),
                    PlacementGridReader.Length(GridSpacingMetres), PlacementGridReader.Length(GridSpacingMetres / 5f));
            }
            var details = new List<string> { report };
            if (!PlacementActionsHandler.Active)
            {
                string roof = RoofConstructionHandler.Describe(root);
                if (!string.IsNullOrWhiteSpace(roof)) details.Add(roof);
                string boundary = RoomBoundaryConstraint.Describe();
                if (!string.IsNullOrWhiteSpace(boundary)) details.Add(boundary);
            }
            if (detailed && (!RoomPlacementGrid.Selected || root.IsWallItem || root.IsCeilingItem))
                details.Add(ItemFootprintReader.Describe(root));
            if (detailed && !root.IsWallItem && !root.IsCeilingItem)
            {
                details.Add(RoomPlacementGrid.Describe(root));
                details.Add(FacingTargetHandler.Describe(hybridPlayer, root));
                details.Add(ItemAlignmentHandler.Describe(hybridPlayer, root));
            }
            details.RemoveAll(string.IsNullOrWhiteSpace);
            report = string.Join(". ", details.Select(part => part.TrimEnd(' ', '.')));
            return true;
        }

        internal static string RequestAlignment(HybridPlayer hybrid, ItemObjectInPlacement preview, Vector3 target)
        {
            if (_state != PlacementState.Moving || !IsFinite(target) || !RefreshGrid()) return Loc.Get("alignment_unavailable");
            string problem = RoomBoundaryConstraint.Problem(_expectedLotGuid, hybrid.Player.GetLotLayer(_expectedLotGuid), target)
                ?? RoomPlacementGrid.Problem(_expectedLotGuid, hybrid.Player.GetLotLayer(_expectedLotGuid), target);
            if (problem != null) return problem;
            if (!TryProjectTarget(hybrid, SlotPlacementHandler.AimPoint(hybrid.Player, preview, target), out var screen))
                return Loc.Get("placement_target_out_of_view");
            SlotPlacementHandler.Clear();
            _requestedPosition = target; _hasRequestedPosition = true; _requireExactTarget = true; _actionAim = true;
            _coordinateGrid = _grid; _coordinateLayer = hybrid.Player.GetLotLayer(_expectedLotGuid);
            _targetFailure = null; _cursorTarget = screen; _movementAnnouncementPending = true;
            WarpCursor(preview, screen, "alignment");
            return null;
        }

        internal static ZoneWallObject CurrentRoom(HybridPlayer hybrid, ItemObjectInPlacement preview)
        {
            ulong lot = _expectedLotGuid; int layer = hybrid.Player.GetLotLayer(lot);
            if (RoomPlacementGrid.Selected && RoomPlacementGrid.Room != null && RoomPlacementGrid.Room.LotPlacedOnGUID == lot
                && RoomPlacementGrid.Room.Layer == layer && RoomLabelHandler.IsCurrent(RoomPlacementGrid.Room)) return RoomPlacementGrid.Room;
            if (_anchorRoom != null && _anchorRoom.Layer == layer && RoomLabelHandler.IsCurrent(_anchorRoom)) return _anchorRoom;
            var rooms = RoomSpatialReader.OrderedRooms(lot, layer);
            return rooms.FirstOrDefault(r => ZoneGeometry.Contains(r, preview.Item.CubeTransform.LocalPosition))
                ?? rooms.FirstOrDefault(r => ZoneGeometry.Contains(r, hybrid.HybridCamera.FreeCamera.LookTarget.position));
        }

        internal static string RequestFitTarget(HybridPlayer hybrid, ItemObjectInPlacement preview, Vector3 support)
        {
            if (hybrid.Player.ItemInPlacement != preview || !IsFinite(support)) return Loc.Get("fit_changed");
            if (!TryProjectTarget(hybrid, SlotPlacementHandler.AimPoint(hybrid.Player, preview, support), out var screen))
                return Loc.Get("placement_target_out_of_view");
            _state = PlacementState.Moving; _requestedPosition = support; _hasRequestedPosition = true;
            _actionAim = true; _requireExactTarget = false; _targetFailure = null; _restoringRoomBoundary = false;
            _cursorTarget = screen; _movementAnnouncementPending = false;
            WarpCursor(preview, screen, "room-fit"); return null;
        }

        internal static ItemObjectRoot BlockingForGroup(Player player, ItemObjectRoot item)
        { ResetBlockingObjectCache(); return GetBlockingObject(player, item); }

        internal static void RejectRoofTarget(string message) => RefuseTarget(message);

        internal static void ReviewAfterAction(HybridPlayer hybrid, ItemObjectInPlacement preview)
        {
            _validatedFrames = 0; _hasRequestedPosition = false; _requireExactTarget = false;
            _targetFailure = null; _movementAnnouncementPending = true;
            if (preview.Item.IsWallItem || preview.Item.IsCeilingItem || SlotPlacementHandler.Active) return;
            if (!RefreshGrid()) { RefuseTarget(Loc.Get("room_grid_changed")); return; }
            _requestedPosition = preview.Item.CubeTransform.LocalPosition;
            _actionAim = true;
            if (!TryProjectTarget(hybrid, SlotPlacementHandler.AimPoint(hybrid.Player, preview, _requestedPosition), out var screen))
            { RefuseTarget(Loc.Get("placement_target_out_of_view")); return; }
            _hasRequestedPosition = true; _cursorTarget = screen; WarpCursor(preview, screen, "placement-action");
        }

        internal static void ResetRoomBoundaryTarget()
        {
            // Changing a constraint does not move, rotate or purchase the preview. Discard an
            // old refused target so disabling the toggle immediately restores ordinary movement.
            _validatedFrames = 0; _hasRequestedPosition = false; _requireExactTarget = false;
            _targetFailure = null; _movementAnnouncementPending = false;
            _restoringRoomBoundary = false; _roomBoundaryMessage = null;
        }

        private static void WarpCursor(ItemObjectInPlacement placement, Vector2 target, string reason)
        {
            Mouse mouse = Mouse.current;
            if (mouse == null)
            {
                _state = PlacementState.Unsupported;
                Speak(Loc.Get("placement_mouse_missing"));
                return;
            }

            Pointer.Expect(target);
            mouse.WarpCursorPosition(target);
            placement.CursorTeleported = true;
            placement.CursorTeleportPosition = new Vector3(target.x, target.y, 0f);
            _validatedFrames = 0;

            Plugin.Log.LogInfo(
                $"[Placement] cursor warp id={_trackedInstanceId}, reason={reason}, "
              + $"screen=({target.x:0.0},{target.y:0.0}).");
        }

        private static Vector3 _lastSpokenPosition;

        private static void ObservePlacementState(
            HybridPlayer hybridPlayer,
            ItemObjectInPlacement placement)
        {
            ItemObjectRoot root = placement.Item;
            string signature = BuildStatusSignature(hybridPlayer.Player, root);
            bool changed = !string.Equals(signature, _lastStatusSignature, StringComparison.Ordinal);
            if (changed)
            {
                _lastStatusSignature = signature;
                Plugin.Log.LogInfo("[Placement] state " + DescribeDiagnostic(hybridPlayer.Player, root));
            }

            if (_validatedFrames < 1)
            {
                return;
            }

            if (_initialAnnouncementPending && !RefreshGrid())
                _targetFailure = Loc.Get("placement_grid_unavailable");

            string status = DescribeStatus(hybridPlayer, root);
            bool announced = false;
            if (_initialAnnouncementPending)
            {
                _initialAnnouncementPending = false;
                Speak(Loc.Get("placement_ready", _itemName, status,
                    PlacementGridReader.Length(GridSpacingMetres), PlacementGridReader.Length(GridSpacingMetres / 5f))
                    + (RoomBoundaryConstraint.Describe() is string boundary ? ". " + boundary : ""));
                announced = true;
            }
            else if (_movementAnnouncementPending)
            {
                _movementAnnouncementPending = false;
                string movement = PlacementGridReader.Movement(_grid, _lastSpokenPosition, root.CubeTransform.LocalPosition);
                if (_hasRequestedPosition && Vector2.Distance(new Vector2(_requestedPosition.x, _requestedPosition.z),
                    new Vector2(root.CubeTransform.LocalPosition.x, root.CubeTransform.LocalPosition.z)) > .1f)
                    status += ". " + Loc.Get("placement_target_difference", PlacementGridReader.Length(Vector2.Distance(
                        new Vector2(_requestedPosition.x, _requestedPosition.z), new Vector2(root.CubeTransform.LocalPosition.x, root.CubeTransform.LocalPosition.z))));
                Speak(string.IsNullOrEmpty(movement) ? status : movement + ". " + status);
                announced = true;
                Plugin.Log.LogInfo($"[PlacementGrid] result id={_trackedInstanceId}, "
                    + $"requested={(_hasRequestedPosition ? _requestedPosition.ToString() : "none")}, "
                    + $"actual={root.CubeTransform.LocalPosition}, rotation={root.transform.eulerAngles}.");
            }

            float rotationDifference = Quaternion.Angle(_lastRotation, root.transform.rotation);
            if (rotationDifference > RotationChangeThresholdDegrees)
            {
                _lastRotation = root.transform.rotation;
                if (!announced)
                {
                    Speak(Loc.Get("placement_rotated", _itemName, status) + ". "
                        + Loc.Get("placement_rotation", root.transform.eulerAngles.y.ToString("0.#")));
                    announced = true;
                }
            }

            if (changed && !announced)
            {
                // State can change because snapping or a relation settles one frame after movement.
                Speak(Loc.Get("placement_status_changed", _itemName, status));
            }
            _lastSpokenPosition = root.CubeTransform.LocalPosition;
        }

        private static void FinishTrackedPreview(
            HybridPlayer hybridPlayer,
            ItemObjectInPlacement current)
        {
            ItemObjectRoot placed = ItemManager.Instance != null
                ? ItemManager.Instance.GetItemByInstanceID(_trackedInstanceId)
                : null;

            bool placedSuccessfully = (_trackedWasNew || ExistingItemEditHandler.WasCommitted) && placed != null
                && placed.LotPlacedOnGUID != 0L
                && HouseholdManager.Instance != null
                && HouseholdManager.Instance.OwnsLot(placed.LotPlacedOnGUID)
                && IsInLotList(placed);

            int finishedId = _trackedInstanceId;
            string finishedName = _itemName;
            int startingMoney = _startingMoney;
            int endingMoney = ReadMoney();
            bool hasNextPreview = current?.Item != null
                && current.IsNewItem
                && current.Item.InstanceID != finishedId;

            if (placedSuccessfully)
            {
                LastPlacedInstanceId = finishedId;
                LastPlacedLotGuid = placed.LotPlacedOnGUID;
                int spent = Math.Max(0, startingMoney - endingMoney);
                string location = ItemDescription.DescribeLocation(
                    placed, null);
                if (_state == PlacementState.Ceiling)
                    location += ". " + CeilingPlacementHandler.Result(placed);
                else if (_state == PlacementState.Wall)
                    location += ". " + WallItemPlacementHandler.DescribeResult(placed);
                else if (SlotPlacementHandler.Active)
                    location += ". " + SlotPlacementHandler.DescribeResult(placed);

                Plugin.Log.LogInfo(
                    $"[Placement] success id={finishedId}, catalog={_catalogueGuid}, "
                  + $"lot={placed.LotPlacedOnGUID}, money={startingMoney}->{endingMoney}, "
                  + $"expectedPrice={_expectedPrice}, nextPreview={hasNextPreview}.");

                if (!_trackedWasNew)
                {
                    Speak(Loc.Get("edit_completed", finishedName, location));
                }
                else if (hasNextPreview)
                {
                    Speak(Loc.Get(
                        "placement_repeat",
                        finishedName,
                        location,
                        FormatService.GetMoney(spent)));
                }
                else
                {
                    Speak(Loc.Get(
                        "placement_placed",
                        finishedName,
                        location,
                        FormatService.GetMoney(spent)));
                }
            }
            else
            {
                Plugin.Log.LogInfo(
                    $"[Placement] cancelled/not-placed id={finishedId}, catalog={_catalogueGuid}, "
                  + $"money={startingMoney}->{endingMoney}, nextPreview={hasNextPreview}.");
                Speak(_trackedWasNew ? Loc.Get("placement_cancelled", finishedName) : ExistingItemEditHandler.DescribeCancelled(placed));
            }

            if (hasNextPreview)
            {
                ClearTracking(restoreCamera: false);
                BeginTracking(
                    hybridPlayer,
                    current,
                    centerHouseView: false,
                    keepCameraBaseline: true);
            }
            else
            {
                ClearTracking(restoreCamera: true);
            }
        }

        private static bool IsInLotList(ItemObjectRoot root)
        {
            List<ItemObjectRoot> items = ItemManager.Instance.GetLotItems(root.LotPlacedOnGUID);
            return items != null && items.Contains(root);
        }

        private static string DescribeStatus(HybridPlayer hybridPlayer, ItemObjectRoot root, bool detailed = false)
        {
            if (RoofConstructionHandler.Pending(hybridPlayer.Player.ItemInPlacement)) return Loc.Get("roof_fitting");
            Player player = hybridPlayer.Player;
            string actionProblem = _targetFailure ?? RoofConstructionHandler.Problem(hybridPlayer, player.ItemInPlacement)
                ?? RoofClearanceReader.Problem(root)
                ?? PlacementSupportConsent.FloorProblem(player.ItemInPlacement)
                ?? PlacementSupportConsent.Problem(player.ItemInPlacement)
                ?? RoomBoundaryConstraint.Problem(_expectedLotGuid, player.GetLotLayer(_expectedLotGuid), root.CubeTransform.LocalPosition)
                ?? RoomPlacementGrid.Problem(_expectedLotGuid, player.GetLotLayer(_expectedLotGuid), root.CubeTransform.LocalPosition)
                ?? FacingTargetHandler.Problem(hybridPlayer, root) ?? ItemAlignmentHandler.Problem(hybridPlayer, root);
            string spatialLocation = PlacementLocationReader.Describe(root);
            string prefix = string.IsNullOrEmpty(spatialLocation) ? "" : spatialLocation + ". ";
            if (actionProblem != null) return prefix + actionProblem;
            if (_requireExactTarget && (!_hasRequestedPosition || !IsFinite(root.CubeTransform.LocalPosition)
                || _coordinateGrid == null || !_coordinateGrid.SameFrame(_grid) || player.GetLotLayer(_expectedLotGuid) != _coordinateLayer
                || Vector2.Distance(new Vector2(root.CubeTransform.LocalPosition.x, root.CubeTransform.LocalPosition.z),
                    new Vector2(_requestedPosition.x, _requestedPosition.z)) > 0.01f))
                return prefix + Loc.Get("coordinate_mismatch");
            string problem = GetPlacementProblem(root);
            bool valid = string.IsNullOrEmpty(problem) && IsInsideOwnedLot(root);
            ItemObjectRoot blocker = valid ? GetBlockingObject(player, root) : null;

            string state;
            if (blocker != null)
            {
                state = Loc.Get(
                    "placement_occupied",
                    ItemDescription.GetProductIdentity(blocker) ?? Loc.Get("item_unknown"));
            }
            else
            {
                state = InputManager.Instance.IsPointerOverUI(player.PlayerIndex) ? Loc.Get("placement_pointer_over_ui") : valid
                    ? Loc.Get("placement_valid")
                    : Loc.Get("placement_invalid", problem ?? TranslationManager.Get("ItemPlacementError_MustStayOnLot"));
            }

            state = prefix + state;
            string support = PlacementSupportConsent.Describe(player.ItemInPlacement);
            if (support != null) state += ". " + support;
            if (!detailed)
                return string.IsNullOrEmpty(_targetFailure) ? state : state + ". " + _targetFailure;
            string location = ItemDescription.DescribeLocation(root, null);
            string result = string.IsNullOrEmpty(location) ? state : state + ", " + location;
            if (_grid != null && root.CubeTransform != null)
            {
                result += ". " + (RoomPlacementGrid.Selected ? Loc.Get("room_grid_coordinate_label") + ": " : "")
                    + PlacementGridReader.Position(_grid, root.CubeTransform.LocalPosition);
                if (_hasRequestedPosition)
                {
                    Vector3 actual = root.CubeTransform.LocalPosition;
                    float distance = Vector2.Distance(new Vector2(actual.x, actual.z),
                        new Vector2(_requestedPosition.x, _requestedPosition.z));
                    if (distance >= 0.01f)
                        result += ". " + Loc.Get("placement_target_difference", PlacementGridReader.Length(distance));
                }
            }
            if (player.HasSnap) result += ". " + Loc.Get("placement_snapped");
            result += ". " + Loc.Get("placement_rotation", root.transform.eulerAngles.y.ToString("0.#"));
            if (!string.IsNullOrEmpty(_targetFailure)) result += ". " + _targetFailure;
            return result;
        }

        private static string GetPlacementProblem(ItemObjectRoot root)
        {
            if (root.IsPositionIllegal)
            {
                return TranslationManager.Get("ItemPlacementError_MustStayOnLot");
            }
            if (root.IsWallItemNotOnWall)
            {
                return TranslationManager.Get("ItemPlacementError_WallItemsMustBeOnWalls");
            }
            if (root.IsCeilingItemNotOnCeiling)
            {
                return TranslationManager.Get("ItemPlacementError_CeilingItemsMustBeOnCeilings");
            }
            if (root.IsHoleItemOnCurvedWall)
            {
                return TranslationManager.Get("ItemPlacementError_HoleItemsCannotBeOnCurvedWalls");
            }
            if (root.IsHoleItemOnFence)
            {
                return TranslationManager.Get("ItemPlacementError_HoleItemsCannotBeOnFences");
            }
            return null;
        }

        private static string DescribeDiagnostic(Player player, ItemObjectRoot root)
        {
            ItemObjectInPlacement placement = player.ItemInPlacement;
            ItemObjectRoot blocker = GetBlockingObject(player, root);
            return $"id={root.InstanceID}, catalog={root.CatalogueGUID}, "
                 + $"position={root.CubeTransform?.LocalPosition}, lot={root.LotPlacedOnGUID}, "
                 + $"layer={root.Layer}, illegal={root.IsPositionIllegal}, "
                 + $"wallOffWall={root.IsWallItemNotOnWall}, "
                 + $"ceilingOffCeiling={root.IsCeilingItemNotOnCeiling}, "
                 + $"holeCurvedWall={root.IsHoleItemOnCurvedWall}, "
                 + $"holeFence={root.IsHoleItemOnFence}, snap={player.HasSnap}, "
                 + $"collider={placement?.LastColliderType}, "
                 + $"occupiedBy={blocker?.InstanceID ?? -1}, "
                 + $"placedOn={root.ItemPlacedOn?.InstanceID ?? -1}, "
                 + $"attachedTo={root.ItemAttachedTo?.InstanceID ?? -1}, "
                 + $"placementSlot={root.PlacementSlotPlacedOn}, chairSlot={root.ChairSlotPlacedOn}, "
                 + $"validatedFrames={_validatedFrames}";
        }

        private static string BuildStatusSignature(Player player, ItemObjectRoot root)
        {
            ItemObjectInPlacement placement = player.ItemInPlacement;
            ItemObjectRoot blocker = GetBlockingObject(player, root);
            return $"ui={InputManager.Instance.IsPointerOverUI(player.PlayerIndex)};lot={root.LotPlacedOnGUID};layer={root.Layer};illegal={root.IsPositionIllegal};"
                 + $"wall={root.IsWallItemNotOnWall};ceiling={root.IsCeilingItemNotOnCeiling};"
                 + $"curved={root.IsHoleItemOnCurvedWall};fence={root.IsHoleItemOnFence};"
                 + $"snap={player.HasSnap};collider={placement?.LastColliderType};"
                 + $"occupied={blocker?.InstanceID ?? -1};"
                 + $"on={root.ItemPlacedOn?.InstanceID ?? -1};"
                 + $"attached={root.ItemAttachedTo?.InstanceID ?? -1};"
                 + $"slot={root.PlacementSlotPlacedOn};chair={root.ChairSlotPlacedOn}";
        }

        private static bool IsInsideOwnedLot(ItemObjectRoot root)
        {
            return root != null && !root.IsPositionIllegal && root.LotPlacedOnGUID != 0L
                && HouseholdManager.Instance != null
                && HouseholdManager.Instance.OwnsLot(root.LotPlacedOnGUID);
        }

        /// <summary>
        /// Finds an existing item occupying the preview's current box. Paralives intentionally
        /// allows ordinary furniture overlap, so this is an accessibility safety check for the
        /// mod's Enter bridge, not a replacement for the game's placement-validity flags.
        /// </summary>
        private static ItemObjectRoot GetBlockingObject(Player player, ItemObjectRoot root)
        {
            if (_blockerCheckedFrame == Time.frameCount && _blockerCheckedRoot == root)
            {
                return _blockingObject;
            }

            _blockerCheckedFrame = Time.frameCount;
            _blockerCheckedRoot = root;
            _blockingObject = null;

            ItemObjectInPlacement placement = player?.ItemInPlacement;
            ItemCubeTransform cube = root?.CubeTransform;
            if (placement?.Item == null || cube == null)
            {
                return null;
            }

            Vector3 size = cube.FinalSize;
            Vector3 halfExtents = new Vector3(
                Mathf.Abs(size.x) * 0.5f,
                Mathf.Abs(size.y) * 0.5f,
                Mathf.Abs(size.z) * 0.5f);
            if (halfExtents.x <= 0f || halfExtents.y <= 0f || halfExtents.z <= 0f)
            {
                return null;
            }

            // This is the same public oriented-box query and 0.1-unit sweep used by
            // ItemObjectPlacementSlot.BoxCastOnPlacementSlot. The preview's colliders are disabled
            // by SpawnItemForPlacement, so the query sees existing items rather than itself.
            Vector3 rotatedSize = root.transform.rotation * size;
            Vector3 center = root.IsWallItem || root.IsCeilingItem ? root.GetWorldBoundingBox().Center
                : cube.LocalPosition + Vector3.up * (rotatedSize.y * 0.5f);
            var hits = RaycastSystem.BoxCastAll(
                center,
                root.transform.up / 2f,
                halfExtents,
                root.transform.rotation,
                0.1f,
                includeTerrain: false,
                includeFloor: false,
                includeWalls: false);

            List<GameObject> hitObjects = hits.Item4;
            if (hitObjects == null)
            {
                return null;
            }

            for (int i = 0; i < hitObjects.Count; i++)
            {
                GameObject hitObject = hitObjects[i];
                ItemObjectRoot candidate = null;
                if (hitObject != null)
                {
                    candidate = hitObject.GetComponent<ItemObjectRoot>();
                    if (candidate == null && hitObject.TryGetComponent(out ItemObject item))
                    {
                        candidate = item.Root;
                    }
                }
                if (candidate == null || candidate == root
                    || RoofConstructionHandler.CanOverlap(root, candidate)
                    || (!RoofConstructionHandler.IsRoof(root) && RoofConstructionHandler.IsRoof(candidate))
                    || ExistingItemEditHandler.MovesWith(placement.Item, candidate)
                    || SlotPlacementHandler.AllowsLinkedOverlap(root, candidate)
                    || IsSemanticPlacementSupport(root, placement, candidate))
                {
                    continue;
                }

                _blockingObject = candidate;
                break;
            }

            return _blockingObject;
        }

        private static bool IsSemanticPlacementSupport(
            ItemObjectRoot root,
            ItemObjectInPlacement placement,
            ItemObjectRoot candidate)
        {
            if (root.ItemPlacedOn == candidate || root.ItemAttachedTo == candidate)
            {
                return true;
            }

            if (placement.LastColliderType == ColliderType.Object
                && placement.LastCollidedObject == candidate.gameObject)
            {
                return true;
            }

            if (root.PlacementSlotPlacedOn == 0L || LotManager.Instance == null
                || root.LotPlacedOnGUID == 0L)
            {
                return false;
            }

            AssetLot lot = LotManager.Instance.GetLotByGUID(root.LotPlacedOnGUID);
            if (lot?.ItemPlacementSlots == null)
            {
                return false;
            }

            foreach (ItemObjectPlacementSlot slot in lot.ItemPlacementSlots)
            {
                if (slot != null && slot.GUID == root.PlacementSlotPlacedOn
                    && slot.ItemOnInstanceID == root.InstanceID
                    && slot.Item?.Root == candidate)
                {
                    return true;
                }
            }

            return false;
        }

        private static void ResetBlockingObjectCache()
        {
            _blockerCheckedFrame = -1;
            _blockerCheckedRoot = null;
            _blockingObject = null;
        }

        internal static string SemanticPlacementProblem(HybridPlayer hybrid, ItemObjectRoot root)
        {
            string actionProblem = CeilingPlacementHandler.Problem(hybrid, root) ?? RoofConstructionHandler.Problem(hybrid, hybrid.Player.ItemInPlacement)
                ?? RoofClearanceReader.Problem(root)
                ?? PlacementSupportConsent.FloorProblem(hybrid.Player.ItemInPlacement)
                ?? PlacementSupportConsent.Problem(hybrid.Player.ItemInPlacement)
                ?? RoomBoundaryConstraint.Problem(root.LotPlacedOnGUID, root.Layer, root.CubeTransform.LocalPosition)
                ?? FacingTargetHandler.Problem(hybrid, root) ?? ItemAlignmentHandler.Problem(hybrid, root);
            if (!root.IsWallItem && !root.IsCeilingItem && !SlotPlacementHandler.Active)
                actionProblem = actionProblem ?? RoomPlacementGrid.Problem(root.LotPlacedOnGUID, root.Layer, root.CubeTransform.LocalPosition);
            if (actionProblem != null) return actionProblem;
            string problem = GetPlacementProblem(root);
            if (problem != null) return Loc.Get("placement_invalid", problem);
            if (!IsInsideOwnedLot(root)) return Loc.Get("placement_lot_required");
            if (InputManager.Instance.IsPointerOverUI(hybrid.Player.PlayerIndex)) return Loc.Get("placement_pointer_over_ui");
            if (hybrid.Player.ItemInPlacement.IsNewItem && hybrid.Player.ItemInPlacement.InventoryItem == null && !MoneyManager.Instance.CanPurchase(root.GetPrice()))
                return Loc.Get("wall_item_unaffordable", FormatService.GetMoney(root.GetPrice()));
            // Recheck at the native purchase boundary, even if an earlier report used this frame's cache.
            ResetBlockingObjectCache();
            var blocker = GetBlockingObject(hybrid.Player, root);
            return blocker == null ? null : Loc.Get("placement_occupied_move", ItemDescription.GetProductIdentity(blocker) ?? Loc.Get("item_unknown"));
        }

        internal static ulong ResolvePlacementLot(Player player, out string source)
        {
            source = "none";
            HouseholdManager households = HouseholdManager.Instance;
            if (player == null || households == null || !households.HasCurrentHousehold)
            {
                return 0uL;
            }

            ulong cameraLot = player.CameraLookingAtLotGUID;
            if (cameraLot != 0L && households.OwnsLot(cameraLot))
            {
                source = "camera";
                return cameraLot;
            }

            List<ulong> ownedLots = households.CurrentHousehold?.Data?.OwnedLots;
            if (ownedLots != null && ownedLots.Count == 1 && ownedLots[0] != 0L)
            {
                source = "sole-owned-lot";
                return ownedLots[0];
            }

            // With several homes, silently following an autonomous Para would move the user's
            // placement target behind their back. The free camera is the player; require it to
            // identify the intended lot instead.
            return 0uL;
        }

        private static int ReadMoney()
        {
            return MoneyManager.Instance != null
                && HouseholdManager.Instance != null
                && HouseholdManager.Instance.HasCurrentHousehold
                    ? MoneyManager.Instance.Money
                    : 0;
        }

        private static void ClearTracking(bool restoreCamera)
        {
            Pointer.Clear();
            _restoringRoomBoundary = false; _roomBoundaryMessage = null;
            RoomBoundaryConstraint.Clear();
            PlacementActionsHandler.Clear(); FacingTargetHandler.Clear(); ItemAlignmentHandler.Clear();
            RoofConstructionHandler.Clear();
            WallItemPlacementHandler.Clear();
            CeilingPlacementHandler.Clear();
            SlotPlacementHandler.Clear();
            ExistingItemEditHandler.Clear();
            CoordinateEntryHandler.Cancel();
            if (_trackedInstanceId >= 0)
            {
                LotBuildInputOwnership.HoldThroughCurrentFrame();
            }

            bool restoreCameraBaseline = restoreCamera && _cameraBaselineCaptured;
            bool cameraWasFrozen = _cameraWasFrozen;

            if (restoreCamera)
            {
                _cameraBaselineCaptured = false;
            }

            _state = PlacementState.None;
            _trackedInstanceId = -1;
            _catalogueGuid = 0uL;
            _expectedLotGuid = 0uL;
            _expectedLotSource = null;
            _itemName = null;
            _startingMoney = 0;
            _expectedPrice = 0;
            _framingAttempts = 0;
            _stableFramingSamples = 0;
            _lastFramingCandidateIndex = -1;
            _lastFramingWorldPoint = default(Vector3);
            _framingZoomRatio = 0f;
            _anchorSource = null;
            _anchorRoom = null;
            _expectedLotPerimeter = null;
            _expectedLotPoints = null;
            _grid = null;
            _hasRequestedPosition = false;
            _targetFailure = null;
            _validatedFrames = 0;
            _initialAnnouncementPending = false;
            _movementAnnouncementPending = false;
            _lastStatusSignature = null;
            ResetBlockingObjectCache();

            if (restoreCameraBaseline)
            {
                try
                {
                    Player player = PlayerManager.Instance?.HybridPlayer1?.Player;
                    if (player != null)
                    {
                        player.FreezeCameraMovement = cameraWasFrozen;
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError("Restoring the placement camera baseline threw: " + ex);
                }
            }
        }

        private static void DisableAfterException(string phase, Exception ex)
        {
            Enabled = false;
            Plugin.Log.LogError($"ItemPlacementHandler disabled after a {phase} exception: {ex}");
            ClearTracking(restoreCamera: true);
            ScreenReader.Speak(Loc.Get("placement_handler_failed"));
        }

        private static void Speak(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            Plugin.Log.LogInfo("[Placement] " + message);
            ScreenReader.Speak(message);
        }
    }
}
