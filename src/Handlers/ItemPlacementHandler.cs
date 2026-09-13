using System;
using System.Collections.Generic;
using ParalivesAccess.Speech;
using Setting;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ParalivesAccess.Handlers
{
    /// <summary>
    /// Makes a new floor-item preview movable and confirmable from the keyboard while leaving the
    /// game's placement validation, purchase, undo and object registration transaction intact.
    /// </summary>
    public static class ItemPlacementHandler
    {
        /// <summary>Set false to disable accessible item placement.</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>True while a captured new-item preview owns movement and Enter.</summary>
        public static bool OwnsInput => _trackedInstanceId >= 0;

        /// <summary>The most recently verified placed instance, or -1 when none was placed.</summary>
        public static int LastPlacedInstanceId { get; private set; } = -1;

        /// <summary>The owned lot containing <see cref="LastPlacedInstanceId"/>, or zero.</summary>
        public static ulong LastPlacedLotGuid { get; private set; }

        // Four percent of the current camera viewport is stable across resolutions while keeping
        // enough precision for ordinary furniture. It changes only the cursor, not game geometry;
        // UpdateMoveItem remains the authority on the resulting world-space position and snapping.
        private const float CursorStepViewportFraction = 0.04f;
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
            Unsupported
        }

        private static PlacementState _state;
        private static int _trackedInstanceId = -1;
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
        private static LotPerimeterZoneObject _expectedLotPerimeter;
        private static List<Vector3> _expectedLotPoints;

        private static Vector2 _cursorTarget;
        private static bool _cursorInitialized;
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
            if (!Enabled || !OwnsInput || PlayerManager.Instance == null)
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

                bool movedThisFrame = horizontal != 0 || vertical != 0;
                if (movedThisFrame)
                {
                    MoveCursor(hybridPlayer, placement, horizontal, vertical);
                }

                bool rotatedThisFrame = hybridPlayer.ButtonSmallRotateItemLeft.Down
                    || hybridPlayer.ButtonSmallRotateItemRight.Down
                    || hybridPlayer.ButtonLargeRotateItem.Down;

                bool enterReleased = keyboard.enterKey.wasReleasedThisFrame
                    || keyboard.numpadEnterKey.wasReleasedThisFrame;
                if (!enterReleased)
                {
                    return;
                }

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
                    Speak(Loc.Get("placement_pointer_over_ui"));
                    return;
                }

                ItemObjectRoot blocker = IsInsideOwnedLot(placement.Item)
                    ? GetBlockingObject(player, placement.Item)
                    : null;
                if (blocker != null)
                {
                    string blockerName = ItemDescription.GetName(blocker) ?? Loc.Get("item_unknown");
                    Plugin.Log.LogInfo(
                        $"[Placement] confirm refused before bridge: id={_trackedInstanceId}, "
                      + $"occupiedBy={blocker.InstanceID}, occupiedName='{blockerName}'.");
                    Speak(Loc.Get("placement_occupied_move", blockerName));
                    return;
                }

                Plugin.Log.LogInfo(
                    $"[Placement] injecting Enter release as ButtonConfirm.Up: "
                  + DescribeDiagnostic(player, placement.Item));
                hybridPlayer.ButtonConfirm.SetValue(0f);
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
                if (!OwnsInput)
                {
                    if (current?.Item != null && current.IsNewItem)
                    {
                        BeginTracking(hybridPlayer, current, centerHouseView: true, keepCameraBaseline: false);
                    }
                    return;
                }

                if (current?.Item == null || current.Item.InstanceID != _trackedInstanceId
                    || !current.IsNewItem)
                {
                    FinishTrackedPreview(hybridPlayer, current);
                    return;
                }

                if (_state == PlacementState.Centering)
                {
                    return;
                }

                if (_state != PlacementState.Moving)
                {
                    return;
                }

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
            _trackedInstanceId = root.InstanceID;
            _catalogueGuid = root.CatalogueGUID;
            _itemName = ItemDescription.GetProductIdentity(root) ?? Loc.Get("item_unknown");
            _expectedPrice = root.GetPrice();
            _startingMoney = ReadMoney();
            _expectedLotGuid = ResolvePlacementLot(hybridPlayer.Player, out _expectedLotSource);
            _lastRotation = root.transform.rotation;
            _lastStatusSignature = null;
            _validatedFrames = 0;
            _cursorInitialized = false;
            _initialAnnouncementPending = false;
            _movementAnnouncementPending = false;
            _framingAttempts = 0;
            _stableFramingSamples = 0;
            _lastFramingCandidateIndex = -1;
            _lastFramingWorldPoint = default(Vector3);
            _framingZoomRatio = 0f;
            _anchorSource = null;
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
                _state = PlacementState.Unsupported;
                Speak(Loc.Get("placement_unsupported_wall"));
                return;
            }

            if (root.IsCeilingItem)
            {
                _state = PlacementState.Unsupported;
                Speak(Loc.Get("placement_unsupported_ceiling"));
                return;
            }

            if (!centerHouseView)
            {
                _state = PlacementState.Moving;
                _validatedFrames = 1;
                _cursorTarget = InputManager.Instance.GetCursorPosition(hybridPlayer.Player.PlayerIndex);
                _cursorInitialized = true;
                _lastStatusSignature = BuildStatusSignature(hybridPlayer.Player, root);
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

            _state = PlacementState.Centering;
            Speak(Loc.Get("placement_framing"));
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
                        && MathService.ZoneObjectContainsPoint(room, cameraTarget))
                    {
                        containingArea = area;
                        containingRoom = room;
                    }
                }
            }

            ZoneWallObject anchorRoom = containingRoom ?? largestRoomOnLayer ?? largestRoom;
            if (anchorRoom != null)
            {
                _anchorWorldPosition = anchorRoom.AveragePosition;
                _anchorSource = containingRoom != null
                    ? "camera-room"
                    : (largestRoomOnLayer != null ? "largest-room-on-layer" : "largest-room");
            }
            else
            {
                _anchorWorldPosition = perimeter.AveragePosition;
                _anchorSource = "lot-center";
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
                _cursorInitialized = true;
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

            for (int i = 0; i < InitialCursorCandidates.Length; i++)
            {
                Vector2 normalized = InitialCursorCandidates[i];
                Vector3 candidateScreen = new Vector3(
                    pixelRect.xMin + pixelRect.width * normalized.x,
                    pixelRect.yMin + pixelRect.height * normalized.y,
                    0f);

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
                    || !MathService.PointCollectionContainsPoint(_expectedLotPoints, candidateWorld))
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
            int vertical)
        {
            if (!_cursorInitialized)
            {
                Vector3 current = InputManager.Instance.GetCursorPosition(hybridPlayer.Player.PlayerIndex);
                _cursorTarget = new Vector2(current.x, current.y);
                _cursorInitialized = true;
            }

            Rect rect = hybridPlayer.CameraRect;
            float minX = rect.xMin * Screen.width;
            float maxX = rect.xMax * Screen.width;
            float minY = rect.yMin * Screen.height;
            float maxY = rect.yMax * Screen.height;
            float viewportWidth = Mathf.Max(1f, maxX - minX);
            float viewportHeight = Mathf.Max(1f, maxY - minY);
            float step = Mathf.Min(viewportWidth, viewportHeight) * CursorStepViewportFraction;

            _cursorTarget.x = Mathf.Clamp(_cursorTarget.x + horizontal * step, minX + 1f, maxX - 1f);
            _cursorTarget.y = Mathf.Clamp(_cursorTarget.y + vertical * step, minY + 1f, maxY - 1f);

            WarpCursor(placement, _cursorTarget, "arrow");
            _movementAnnouncementPending = true;
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

            mouse.WarpCursorPosition(target);
            placement.CursorTeleported = true;
            placement.CursorTeleportPosition = new Vector3(target.x, target.y, 0f);
            _validatedFrames = 0;

            Plugin.Log.LogInfo(
                $"[Placement] cursor warp id={_trackedInstanceId}, reason={reason}, "
              + $"screen=({target.x:0.0},{target.y:0.0}).");
        }

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

            string status = DescribeStatus(hybridPlayer, root);
            bool announced = false;
            if (_initialAnnouncementPending)
            {
                _initialAnnouncementPending = false;
                Speak(Loc.Get("placement_ready", _itemName, status));
                announced = true;
            }
            else if (_movementAnnouncementPending)
            {
                _movementAnnouncementPending = false;
                Speak(Loc.Get("placement_moved", _itemName, status));
                announced = true;
            }

            float rotationDifference = Quaternion.Angle(_lastRotation, root.transform.rotation);
            if (rotationDifference > RotationChangeThresholdDegrees)
            {
                _lastRotation = root.transform.rotation;
                if (!announced)
                {
                    Speak(Loc.Get("placement_rotated", _itemName, status));
                    announced = true;
                }
            }

            if (changed && !announced)
            {
                // State can change because snapping or a relation settles one frame after movement.
                Speak(Loc.Get("placement_status_changed", _itemName, status));
            }
        }

        private static void FinishTrackedPreview(
            HybridPlayer hybridPlayer,
            ItemObjectInPlacement current)
        {
            ItemObjectRoot placed = ItemManager.Instance != null
                ? ItemManager.Instance.GetItemByInstanceID(_trackedInstanceId)
                : null;

            bool placedSuccessfully = placed != null
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
                    placed, GetCameraReferencePosition(hybridPlayer));

                Plugin.Log.LogInfo(
                    $"[Placement] success id={finishedId}, catalog={_catalogueGuid}, "
                  + $"lot={placed.LotPlacedOnGUID}, money={startingMoney}->{endingMoney}, "
                  + $"expectedPrice={_expectedPrice}, nextPreview={hasNextPreview}.");

                if (hasNextPreview)
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
                Speak(Loc.Get("placement_cancelled", finishedName));
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

        private static string DescribeStatus(HybridPlayer hybridPlayer, ItemObjectRoot root)
        {
            Player player = hybridPlayer.Player;
            string problem = GetPlacementProblem(root);
            bool valid = string.IsNullOrEmpty(problem) && IsInsideOwnedLot(root);
            ItemObjectRoot blocker = valid ? GetBlockingObject(player, root) : null;

            string state;
            if (blocker != null)
            {
                state = Loc.Get(
                    "placement_occupied",
                    ItemDescription.GetName(blocker) ?? Loc.Get("item_unknown"));
            }
            else
            {
                state = valid
                    ? Loc.Get("placement_valid")
                    : Loc.Get("placement_invalid", problem ?? TranslationManager.Get("ItemPlacementError_MustStayOnLot"));
            }

            string location = ItemDescription.DescribeLocation(
                root, GetCameraReferencePosition(hybridPlayer));
            return string.IsNullOrEmpty(location) ? state : state + ", " + location;
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
            return $"lot={root.LotPlacedOnGUID};layer={root.Layer};illegal={root.IsPositionIllegal};"
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
            if (placement?.Item != root || cube == null)
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
            Vector3 center = cube.LocalPosition + Vector3.up * (rotatedSize.y * 0.5f);
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

        private static Vector3? GetCameraReferencePosition(HybridPlayer hybridPlayer)
        {
            Transform lookTarget = hybridPlayer?.HybridCamera?.FreeCamera?.LookTarget;
            return lookTarget != null && IsFinite(lookTarget.position)
                ? lookTarget.position
                : (Vector3?)null;
        }

        private static ulong ResolvePlacementLot(Player player, out string source)
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
            _expectedLotPerimeter = null;
            _expectedLotPoints = null;
            _cursorInitialized = false;
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
