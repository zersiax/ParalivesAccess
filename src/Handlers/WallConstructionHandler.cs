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
    /// <summary>Keyboard navigation over the native straight-wall cursor and transaction.</summary>
    public static class WallConstructionHandler
    {
        /// <summary>Disabled if either the game loop or native completion observer is unavailable.</summary>
        public static bool Enabled { get; set; } = true;
        /// <summary>Starts a guided rectangle while the native wall tool has no preview.</summary>
        public static Key RectangleKey { get; set; } = Key.R;
        /// <summary>True only while the supported native tool has the foreground.</summary>
        public static bool OwnsInput => Enabled && _player != null
            && Foreground(PlayerManager.Instance?.HybridPlayer1);

        private enum Guide { None, FirstCorner, SecondCorner, ReviewingSharedEdge, StartingEdge, ReviewingEdge, CheckingRoom }
        private static Guide _guide;
        private static Player _player;
        private static bool _cameraWasFrozen;
        private static ulong _lot;
        private static int _layer, _stable, _observedId = -1, _pendingId = -1, _roomPasses;
        private static bool _pendingStart, _accepted, _announce, _hasTarget;
        private static bool _quietIdleReturn;
        private static bool _syntheticConfirm, _nativeConfirmBlocked;
        private static bool _needsAnchor, _unresolvedLotReported;
        private static bool _exactCoordinate;
        private static float _roomDeadline;
        private static int _observedPrice;
        private static int _acceptedPrice;
        private static Vector3 _target, _actual, _firstCorner, _observedA, _observedB;
        private static Vector3 _lastSpokenPosition;
        private static bool _hasSpokenPosition;
        private static bool _guideInputBlocked;
        private static Vector2 _screen;
        private static readonly PlacementPointer Pointer = new PlacementPointer();
        private static bool _interfaceReframed;
        private static PlacementGrid _grid;
        private static RectangleWallPlan _plan;
        private static SharedWallBoundary _shared;
        private static RoomExtensionReview _extension;
        private static string _extensionProblem;
        private static int _reusedEdges;
        private static readonly HashSet<int> PreviousRooms = new HashSet<int>();
        private static string _failure;

        /// <summary>Observes successful native commits, including segments split during completion.</summary>
        internal static bool ApplyPatches(Harmony harmony)
        {
            try
            {
                var target = AccessTools.Method(typeof(EndCreateSegment), "CompletePlaceSegment",
                    new[] { typeof(Player), typeof(int), typeof(SegmentObject) });
                var prefix = AccessTools.Method(typeof(WallConstructionHandler), nameof(BeforeComplete));
                var postfix = AccessTools.Method(typeof(WallConstructionHandler), nameof(AfterComplete));
                if (target == null || prefix == null || postfix == null) return false;
                harmony.Patch(target, new HarmonyMethod(prefix), new HarmonyMethod(postfix));
                var patches = Harmony.GetPatchInfo(target);
                return patches != null
                    && patches.Prefixes.Any(p => p.owner == Plugin.PluginGuid && p.PatchMethod == prefix)
                    && patches.Postfixes.Any(p => p.owner == Plugin.PluginGuid && p.PatchMethod == postfix)
                    && InstallGuard(harmony, typeof(StartCreateSegment), nameof(BeforeNativeStart), true)
                    && InstallGuard(harmony, typeof(CheckToReplaceSegment), nameof(BeforeNativeReplacement), false)
                    && InstallGuard(harmony, typeof(EndCreateSegment), nameof(BeforeNativeEnd), true);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Wall completion observer could not be installed: " + ex);
                return false;
            }
        }

        private static bool InstallGuard(Harmony harmony, Type system, string name, bool cleanup)
        {
            var target = AccessTools.Method(system, "UpdateForPlayer", new[] { typeof(Player) });
            var prefix = AccessTools.Method(typeof(WallConstructionHandler), name);
            if (target == null || prefix == null) return false;
            var finalizer = cleanup ? AccessTools.Method(typeof(WallConstructionHandler), nameof(AfterNativeConfirm)) : null;
            if (cleanup && finalizer == null) return false;
            harmony.Patch(target, prefix: new HarmonyMethod(prefix), finalizer: cleanup ? new HarmonyMethod(finalizer) : null);
            var patches = Harmony.GetPatchInfo(target);
            return patches?.Prefixes.Any(p => p.owner == Plugin.PluginGuid && p.PatchMethod == prefix) == true
                && (!cleanup || patches.Finalizers.Any(p => p.owner == Plugin.PluginGuid && p.PatchMethod == finalizer));
        }

        // UpdateNodeCursor runs inside the game loop before these systems. Check again here:
        // its cursor may have changed since our prefix, while EndCreateSegment still has the old endpoint.
        private static bool BeforeNativeStart(Player player)
        {
            if (!_pendingStart || player != _player) return true;
            return AllowNativeConfirm() && InjectConfirm();
        }

        private static bool BeforeNativeReplacement(Player player)
        {
            if (_pendingId < 0 || player != _player) return true;
            try
            {
                _nativeConfirmBlocked = !AllowNativeConfirm();
                if (!_nativeConfirmBlocked && ReplacesExistingWall(player.SegmentInPlacement))
                {
                    _nativeConfirmBlocked = true;
                    Refuse("wall_replacement_unsupported");
                }
                if (!_nativeConfirmBlocked) _nativeConfirmBlocked = !InjectConfirm();
                return !_nativeConfirmBlocked;
            }
            catch (Exception ex) { Fail(ex); return false; }
        }

        private static bool BeforeNativeEnd(Player player) => _pendingId < 0 || player != _player
            || (!_nativeConfirmBlocked && _syntheticConfirm && AllowNativeConfirm());

        private static bool InjectConfirm()
        {
            var button = PlayerManager.Instance.HybridPlayer1.ButtonConfirm;
            if (_syntheticConfirm) return true;
            if (button.Down || button.Up || button.Pressed) return false;
            _syntheticConfirm = true;
            button.SetValue(1f);
            return true;
        }

        // Run as a finalizer even if the native method throws. No synthetic press/up survives into
        // item/platform widgets or LateUpdate. Physical mouse input is never replaced by this bridge.
        private static void AfterNativeConfirm(Player player)
        {
            if (!_syntheticConfirm || player != _player) return;
            var button = PlayerManager.Instance.HybridPlayer1.ButtonConfirm;
            _syntheticConfirm = false;
            button.SetValue(0f);
            button.Update();
        }

        private static bool ReplacesExistingWall(SegmentObject preview)
        {
            foreach (var existing in SegmentManager.Instance.GetSegmentsInOwnLots())
            {
                if (existing == null || existing == preview || !existing.gameObject.activeInHierarchy
                    || existing.PlacementPlayer != -1 || existing.IsCurved || existing.Layer != preview.Layer
                    || existing.NodeA == null || existing.NodeB == null) continue;
                // CheckToReplaceSegment acts on coincident endpoints before native end validation.
                // Refuse positive collinear overlap, including subsegments, for this construction-only scope.
                Vector3 a = preview.NodeA.Position, b = preview.NodeB.Position;
                Vector3 c = existing.NodeA.Position, d = existing.NodeB.Position;
                if (ConstructionGeometry.Overlaps(a.x, a.z, b.x, b.z, c.x, c.z, d.x, d.z)) return true;
            }
            return false;
        }

        private static bool AllowNativeConfirm()
        {
            try { return Enabled && Foreground(PlayerManager.Instance?.HybridPlayer1) && Ready(PlayerManager.Instance.HybridPlayer1); }
            catch (Exception ex) { Fail(ex); return false; }
        }

        private static void BeforeComplete(Player player, out int __state)
        {
            __state = player == _player && _pendingId >= 0
                && player.SegmentInPlacement?.InstanceID == _pendingId ? _pendingId : -1;
        }

        private static void AfterComplete(Player player, int price, int __state)
        {
            if (__state < 0 || player != _player || __state != _pendingId) return;
            _accepted = true;
            _acceptedPrice = price;
        }

        /// <summary>Moves the cursor before native systems, confirming only previously observed geometry.</summary>
        public static void BeforeGameSystems()
        {
            if (CoordinateEntryHandler.BlocksPlacement) return;
            try
            {
                var hybrid = PlayerManager.Instance?.HybridPlayer1;
                if (Enabled && TryExitToExplorer(hybrid)) return;
                if (!Enabled || !Foreground(hybrid)) { Release(!YieldToCatalog(hybrid)); return; }
                if (_player != hybrid.Player)
                {
                    if (_player != null) Release();
                    Capture(hybrid);
                }
                if (_player == null) return;
                _player.FreezeCameraMovement = true;
                var keyboard = Keyboard.current;
                if (keyboard == null) return;
                if (keyboard.rightArrowKey.wasPressedThisFrame || keyboard.leftArrowKey.wasPressedThisFrame
                    || keyboard.upArrowKey.wasPressedThisFrame || keyboard.downArrowKey.wasPressedThisFrame
                    || keyboard.enterKey.wasReleasedThisFrame || keyboard.numpadEnterKey.wasReleasedThisFrame
                    || keyboard.homeKey.wasPressedThisFrame || hybrid.ButtonConfirm.Down
                    || hybrid.ButtonCancel.Down || hybrid.ButtonUndo.Down)
                    _quietIdleReturn = false;
                if (hybrid.ButtonCancel.Down || hybrid.ButtonUndo.Down
                    || hybrid.ForceTriggerCancelEscapeBackWithRightClick)
                {
                    StopGuide();
                    _hasTarget = false; _stable = 0; _announce = true;
                    return; // Native cancellation/undo retains its own input and transaction.
                }
                if (!RefreshGrid()) return;
                if (CoordinateEntryHandler.WasRequested(hybrid)) { OpenCoordinates(hybrid); return; }
                if (ModInputKey.WasPressed(keyboard, Key.F11))
                {
                    if (!ModInputKey.IsAvailable(hybrid, Key.F11, out string conflict)) Speak(conflict);
                    else Speak(DescribeDetailed() + ". " + PlacementGridReader.DescribeBounds(_lot, _grid)
                        + ". " + BuildCameraHandler.DescribeCamera(hybrid, false) + ". " + Loc.Get("wall_grid_context",
                        PlacementGridReader.Length((float)_grid.Spacing), PlacementGridReader.Length(
                            (float)_grid.Spacing * Settings.Get<WallPlacementGrid>().ShiftCellDimensionMultiplier)));
                    return;
                }
                if (hybrid.ButtonConfirm.Down)
                {
                    StopGuide(); // A physical click belongs to native free placement, not the guide.
                    _hasTarget = false; _stable = 0; _needsAnchor = false;
                    return;
                }
                if (keyboard.homeKey.wasPressedThisFrame && _hasTarget)
                {
                    if (!ModInputKey.IsAvailable(hybrid, Key.Home, out string conflict)) Speak(conflict);
                    else
                    {
                        var camera = hybrid.HybridCamera?.FreeCamera;
                        if (camera != null) { camera.CurrentExtraLookUpRatio = 0f; camera.TargetDistanceSpeed = 0f; }
                        if (!WorldTargeting.FrameArea(_player, _lot, _layer, _target, "wall"))
                            Speak(Loc.Get("wall_frame_failed"));
                        _stable = 0;
                    }
                    return;
                }
                if (ModInputKey.WasPressed(keyboard, RectangleKey))
                {
                    if (!ModInputKey.IsAvailable(hybrid, RectangleKey, out string conflict)) Speak(conflict);
                    else if (_player.SegmentInPlacement != null || _player.StartNewSegment
                        || _player.StartNewSegmentNextFrame || _guide != Guide.None)
                        Speak(Loc.Get("wall_rectangle_busy"));
                    else if (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed)
                    { _guide = Guide.FirstCorner; Speak(Loc.Get("wall_rectangle_first")); }
                    else OpenRoomSize(hybrid);
                    return;
                }
                if (_stable >= 2 && _hasTarget && Pointer.WasMoved(
                    InputManager.Instance.GetCursorPosition(_player.PlayerIndex)))
                {
                    StopGuide(); _hasTarget = false; _stable = 0; _announce = true;
                    return; // Let native cursor observation catch up before adopting the new point.
                }
                if (!_hasTarget && ReadCursor(out Vector3 current))
                {
                    if (_needsAnchor && !IsOnExpectedLot(current))
                    {
                        if (!AnchorOnLot(hybrid)) return;
                    }
                    else SetTarget(current);
                    _needsAnchor = false;
                }
                int dx = (keyboard.rightArrowKey.wasPressedThisFrame ? 1 : 0)
                    - (keyboard.leftArrowKey.wasPressedThisFrame ? 1 : 0);
                int dz = (keyboard.upArrowKey.wasPressedThisFrame ? 1 : 0)
                    - (keyboard.downArrowKey.wasPressedThisFrame ? 1 : 0);
                bool shift = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
                bool modifiers = shift || InputManager.ControlOrCommand || InputManager.Alt;
                bool fixedGuide = _guide == Guide.StartingEdge || _guide == Guide.ReviewingEdge || _guide == Guide.ReviewingSharedEdge;
                if (fixedGuide && (modifiers || dx != 0 || dz != 0))
                {
                    // A modifier alone can begin a read-only shortcut such as Shift+J. Keep
                    // construction blocked, but warn only about an actual move/confirm attempt.
                    bool attemptedEdit = dx != 0 || dz != 0 || keyboard.enterKey.wasReleasedThisFrame
                        || keyboard.numpadEnterKey.wasReleasedThisFrame;
                    if (attemptedEdit && !_guideInputBlocked) Speak(Loc.Get("wall_rectangle_locked"));
                    if (attemptedEdit) _guideInputBlocked = true;
                    _stable = 0; return;
                }
                _guideInputBlocked = false;
                if (!fixedGuide && _hasTarget && (dx != 0 || dz != 0))
                {
                    double step = shift ? Settings.Get<WallPlacementGrid>().ShiftCellDimensionMultiplier : 1;
                    _grid.Step(_target.x, _target.z, dx, dz, step, out double x, out double z);
                    SetTarget(new Vector3((float)x, _target.y, (float)z));
                }
                if (!Warp(hybrid)) return;
                bool enter = keyboard.enterKey.wasReleasedThisFrame || keyboard.numpadEnterKey.wasReleasedThisFrame;
                if (_guide == Guide.StartingEdge)
                {
                    PrepareEdge(hybrid);
                    return;
                }
                if (!enter || _guide == Guide.CheckingRoom) return;
                if (modifiers && _guide != Guide.None) { Speak(Loc.Get("wall_rectangle_locked")); return; }
                if (InputManager.ControlOrCommand || InputManager.Alt)
                { Speak(Loc.Get("wall_release_modifiers")); return; }
                if (!Ready(hybrid))
                {
                    Speak(_extensionProblem ?? Loc.Get(_failure ?? (_guide == Guide.ReviewingEdge && !SameXZ(_actual, _target)
                        ? "wall_rectangle_endpoint" : "wall_wait")));
                    return;
                }
                if (_guide == Guide.FirstCorner)
                {
                    _firstCorner = _actual;
                    _guide = Guide.SecondCorner;
                    Speak(Loc.Get("wall_rectangle_second", Position(_actual)));
                }
                else if (_guide == Guide.SecondCorner)
                {
                    if (!RectangleWallPlan.TryCreate(_grid, _firstCorner.x, _firstCorner.z,
                        _actual.x, _actual.z, out _plan)) { Speak(Loc.Get("wall_rectangle_invalid")); return; }
                    BeginPlan();
                }
                else if (_guide == Guide.ReviewingSharedEdge)
                {
                    if (_shared == null || !_shared.IsLive()) { Speak(Loc.Get("wall_shared_changed")); StopGuide(); return; }
                    _plan.AcceptEdge(); _reusedEdges = 1; _guide = Guide.StartingEdge; SetCorner(1);
                    Speak(Loc.Get("wall_shared_accepted"));
                }
                else Confirm(hybrid);
            }
            catch (Exception ex) { Fail(ex); }
        }

        /// <summary>Reads the native cursor and preview after endpoint updates and records completed walls.</summary>
        public static void AfterGameSystems()
        {
            if (CoordinateEntryHandler.BlocksPlacement) return;
            if (_player == null) return;
            try
            {
                var hybrid = PlayerManager.Instance?.HybridPlayer1;
                if (!Enabled || !Foreground(hybrid) || _player != hybrid.Player) { Release(); return; }
                if (_pendingId >= 0)
                {
                    if (_accepted)
                    {
                        Speak(Loc.Get("wall_placed", _acceptedPrice));
                        if (_plan != null)
                        {
                            _plan.AcceptEdge();
                            if (_plan.Complete) { _guide = Guide.CheckingRoom; _roomPasses = 0; _roomDeadline = Time.unscaledTime + 5f; }
                            else { _guide = Guide.StartingEdge; SetCorner(_plan.CompletedEdges); }
                        }
                    }
                    else { Speak(Loc.Get(_failure ?? "wall_refused")); StopGuide(); }
                    _pendingId = -1; _accepted = false; _stable = 0; _announce = true;
                }
                if (_pendingStart)
                {
                    _pendingStart = false;
                    if (_player.SegmentInPlacement == null) { Speak(Loc.Get("wall_start_refused")); StopGuide(); }
                    _stable = 0; _announce = true;
                }
                if (_guide == Guide.CheckingRoom) { CheckRoom(); return; }
                if (!ReadCursor(out Vector3 actual)) { _stable = 0; return; }
                var segment = _player.SegmentInPlacement;
                int id = segment != null ? segment.InstanceID : -1;
                Vector3 a = segment?.NodeA != null ? segment.NodeA.Position : Vector3.zero;
                Vector3 b = segment?.NodeB != null ? segment.NodeB.Position : Vector3.zero;
                int price = segment != null ? segment.GetPrice() : 0;
                bool changed = id != _observedId || Vector3.Distance(actual, _actual) > 0.002f
                    || Vector3.Distance(a, _observedA) > 0.002f || Vector3.Distance(b, _observedB) > 0.002f
                    || price != _observedPrice;
                if (changed) { _stable = 0; _announce = true; }
                _observedId = id; _actual = actual; _observedA = a; _observedB = b;
                _observedPrice = price;
                bool cursorArrived = _hasTarget && Vector2.Distance(
                    InputManager.Instance.GetCursorPosition(_player.PlayerIndex), _screen) <= 2f;
                _stable = cursorArrived && !_player.StartNewSegment && !_player.StartNewSegmentNextFrame
                    ? _stable + 1 : 0;
                if (_guide == Guide.ReviewingEdge && segment == null) StopGuide();
                if (_quietIdleReturn && _guide == Guide.None && segment == null)
                {
                    // Automatic cursor settling after a form or completed guide must not
                    // interrupt its outcome with an idle wall-start prompt. A fresh edit
                    // resumes ordinary feedback; explicit F3/F11 remain available throughout.
                    _announce = false;
                    _lastSpokenPosition = _actual; _hasSpokenPosition = true;
                }
                if (_announce && _stable >= 2 && _guide != Guide.StartingEdge)
                {
                    _announce = false;
                    string movement = _hasSpokenPosition && _guide == Guide.None
                        ? PlacementGridReader.Movement(_grid, _lastSpokenPosition, _actual) : null;
                    Speak(string.IsNullOrEmpty(movement) ? Describe() : movement + ". " + Describe());
                    _lastSpokenPosition = _actual; _hasSpokenPosition = true;
                }
            }
            catch (Exception ex) { Fail(ex); }
        }

        private static bool Foreground(HybridPlayer hybrid)
        {
            var p = hybrid?.Player;
            return p != null && p.State == GameStates.PlaceSegment && p.CurrentSegmentType == SegmentTypes.Wall
                && p.CurrentWallHeightType == WallHeightType.FullWall && p.WallPlacementMode == WallPlacementMode.Straight
                && (p.SegmentInPlacement == null || !p.SegmentInPlacement.IsCurved)
                && !p.CatalogueOpened && p.PlayerActionsAreAllowed && !p.PlayerActionBlockedByUIWindow
                && !hybrid.HasInputFieldFocused && !hybrid.IsUsingGamePad
                && !BuildCatalogHandler.OwnsInput && !TutorialHandler.OwnsInput
                && !ContextActionHandler.OwnsInput && !OccupationHandler.OwnsInput && !LotObjectHandler.OwnsInput
                && !YieldToCatalog(hybrid);
        }

        private static bool YieldToCatalog(HybridPlayer hybrid) => _guide == Guide.None
            && hybrid?.Player != null && hybrid.Player.SegmentInPlacement == null
            && ModInputKey.WasPressed(Keyboard.current, BuildCatalogHandler.OpenKey);

        private static bool TryExitToExplorer(HybridPlayer hybrid)
        {
            if (_guide != Guide.None || !Foreground(hybrid) || hybrid.Player.SegmentInPlacement != null
                || hybrid.Player.StartNewSegment || hybrid.Player.StartNewSegmentNextFrame
                || !ModInputKey.WasPressed(Keyboard.current, LotObjectHandler.OpenKey)) return false;
            if (!ModInputKey.IsAvailable(hybrid, LotObjectHandler.OpenKey, out string conflict))
            { Speak(conflict); return true; }
            var mode = UI.GetOrNull<UIBuildModeModes>(hybrid.Player.PlayerIndex);
            if (mode == null) { Speak(Loc.Get("extension_tool_unavailable")); return true; }
            // Same native Close control as the tool banner; no wall preview exists to cancel.
            mode.CloseClicked();
            Release(false); // F7 must remain available to the explorer on this frame.
            return true;
        }

        private static void Capture(HybridPlayer hybrid, bool announce = true)
        {
            _lot = ItemPlacementHandler.ResolvePlacementLot(hybrid.Player, out _);
            if (_lot == 0)
            {
                if (!_unresolvedLotReported) Speak(Loc.Get("wall_lot_required"));
                _unresolvedLotReported = true;
                return;
            }
            _unresolvedLotReported = false;
            _player = hybrid.Player;
            _layer = _player.GetLotLayer(_lot);
            _cameraWasFrozen = _player.FreezeCameraMovement;
            _observedId = -1;
            _stable = 0; _hasTarget = false; _announce = true;
            _hasSpokenPosition = false; _guideInputBlocked = false;
            _needsAnchor = _player.SegmentInPlacement == null;
            if (announce) Speak(Loc.Get("wall_ready", RectangleKey));
        }

        internal static string StartExtension(HybridPlayer hybrid, RoomExtensionReview review)
        {
            if (!Enabled || !StructureEditingHandler.Context(hybrid.Player, review.Target.Lot, review.Target.Layer)
                || !review.Target.IsLive) return Loc.Get("extension_changed");
            string problem = review.Problem();
            if (problem != null) return problem;
            if (!RoomExtensionReader.SelectWallTool(hybrid)) return Loc.Get("extension_tool_unavailable");
            if (_player != null) Release(false);
            Capture(hybrid, false);
            if (_player == null || !Foreground(hybrid) || !RefreshGrid() || !_grid.SameFrame(review.Plan.Grid)
                || _player.CurrentFullWallHeight != review.Height)
            { Release(); return Loc.Get("extension_tool_unavailable"); }
            _extension = review; _extensionProblem = null;
            _plan = review.Plan; _shared = review.Shared; _reusedEdges = 0;
            PreviousRooms.Clear();
            foreach (var zone in ZoneManager.Instance.GetLotZones(_lot)) PreviousRooms.Add(zone.InstanceID);
            _firstCorner = new Vector3(0, WallHeightManager.Instance.GetFloorWorldY(_lot, _layer), 0);
            _plan.StartAt(_shared.Edge); _guide = Guide.ReviewingSharedEdge;
            SetCorner(0);
            if (!WorldTargeting.FrameArea(_player, _lot, _layer, _target, "room-extension"))
            { Release(); return Loc.Get("wall_frame_failed"); }
            _needsAnchor = false;
            return review.Description;
        }

        private static bool IsOnExpectedLot(Vector3 position)
        {
            var found = ZoneManager.Instance.GetNodeCursorLotPerimeterOfHousehold(position, _lot);
            return found.Item1 && found.Item2 == _lot;
        }

        private static bool AnchorOnLot(HybridPlayer hybrid)
        {
            var camera = hybrid.HybridCamera?.FreeCamera;
            var perimeter = ZoneManager.Instance.GetLotPerimeterZoneObjectByGUID(_lot);
            if (camera == null || perimeter == null) return Refuse("wall_anchor_unavailable");
            Vector3 point = camera.LookTarget != null && IsOnExpectedLot(camera.LookTarget.position)
                ? camera.LookTarget.position : perimeter.AveragePosition;
            _grid.Coordinates(point.x, point.z, out double c, out double r);
            _grid.World(Math.Round(c), Math.Round(r), out double x, out double z);
            point = new Vector3((float)x, WallHeightManager.Instance.GetFloorWorldY(_lot, _layer), (float)z);
            if (!IsOnExpectedLot(point)) return Refuse("wall_anchor_unavailable");
            camera.CurrentExtraLookUpRatio = 0f; camera.TargetDistanceSpeed = 0f;
            if (!WorldTargeting.FrameArea(_player, _lot, _layer, point, "wall-entry")) return Refuse("wall_frame_failed");
            SetTarget(point);
            return true;
        }

        private static bool RefreshGrid()
        {
            var data = LotManager.Instance?.GetLotByGUID(_lot)?.GetGridData();
            if (!Settings.Get<Gameplay>().UsesWallGrid)
            { StopGuide(); return Refuse("wall_grid_disabled"); }
            if (ItemPlacementHandler.ResolvePlacementLot(_player, out _) != _lot
                || _player.GetLotLayer(_lot) != _layer || data == null
                || !PlacementGridReader.TryRead(_lot, data.CellDimension, out PlacementGrid grid))
            { StopGuide(); return Refuse("wall_grid_unavailable"); }
            if (_grid != null && !_grid.SameFrame(grid))
            {
                StopGuide(); _hasTarget = false; _stable = 0;
                Speak(Loc.Get("placement_grid_changed"));
            }
            _grid = grid;
            return true;
        }

        private static void OpenRoomSize(HybridPlayer hybrid)
        {
            // The native camera focus (or lot centre) supplies the anchor, never a guessed
            // coordinate measured from the bounding rectangle's potentially off-lot origin.
            if (!AnchorOnLot(hybrid)) return;
            _quietIdleReturn = true;
            var grid = _grid; var center = _target; ulong lot = _lot; int layer = _layer;
            var unit = MeasurementUnitService.GetCurrentUnit();
            if (unit == null || !PlacementGrid.Finite(unit.MetricMultiplier) || unit.MetricMultiplier <= 0)
            { Speak(Loc.Get("room_size_unavailable")); return; }
            float multiplier = unit.MetricMultiplier;
            CoordinateEntryHandler.Open(hybrid, null, Loc.Get("room_size_hint", unit.ShortName),
                4 * multiplier, 4 * multiplier,
                () => _player == hybrid.Player && _player.SegmentInPlacement == null
                    && !_player.StartNewSegment && !_player.StartNewSegmentNextFrame
                    && _lot == lot && _layer == layer && _guide == Guide.None
                    && ItemPlacementHandler.ResolvePlacementLot(_player, out _) == lot && _player.GetLotLayer(lot) == layer
                    && Settings.Get<Gameplay>().UsesWallGrid
                    && PlacementGridReader.TryRead(lot, (float)grid.Spacing, out var current) && grid.SameFrame(current)
                    && MeasurementUnitService.GetCurrentUnit() == unit && unit.MetricMultiplier == multiplier,
                (width, depth) =>
                {
                    if (!RectangleWallPlan.TryCreateSized(grid, center.x, center.z, width / multiplier, depth / multiplier, out _plan))
                    { Speak(Loc.Get("room_size_invalid")); return; }
                    for (int i = 0; i < 4; i++)
                    {
                        _plan.Point(i, out double x, out double z);
                        if (!IsOnExpectedLot(new Vector3((float)x, center.y, (float)z)))
                        { _plan = null; Speak(Loc.Get("room_size_outside")); return; }
                    }
                    _firstCorner = center;
                    BeginPlan();
                }, RectangleKey, Loc.Get("room_size_title"), Loc.Get("room_size_instructions"), false);
        }

        private static void BeginPlan()
        {
            if (!SharedWallBoundary.Find(_plan, _lot, _layer, _player.CurrentFullWallHeight, out _shared))
            { _plan = null; Speak(Loc.Get("wall_shared_unsupported")); return; }
            _quietIdleReturn = false;
            _reusedEdges = 0;
            PreviousRooms.Clear();
            foreach (var zone in ZoneManager.Instance.GetLotZones(_lot)) PreviousRooms.Add(zone.InstanceID);
            _guide = _shared == null ? Guide.StartingEdge : Guide.ReviewingSharedEdge;
            if (_shared != null) _plan.StartAt(_shared.Edge);
            SetCorner(0);
            Speak(Loc.Get("wall_rectangle_plan", PlacementGridReader.Length((float)_plan.Width),
                PlacementGridReader.Length((float)_plan.Depth)));
            if (_shared != null) Speak(Loc.Get("wall_shared_review", _shared.Walls.Count, Position(_target)));
        }

        private static void OpenCoordinates(HybridPlayer hybrid)
        {
            if (_guide == Guide.StartingEdge || _guide == Guide.ReviewingEdge || _guide == Guide.ReviewingSharedEdge || _guide == Guide.CheckingRoom)
            { Speak(Loc.Get("wall_rectangle_locked")); return; }
            if (!_hasTarget || _grid == null) { Speak(Loc.Get("wall_wait")); return; }
            var grid = _grid; var segment = _player.SegmentInPlacement; int id = segment != null ? segment.InstanceID : -1;
            _grid.Coordinates(_target.x, _target.z, out double x, out double z);
            _stable = 0;
            CoordinateEntryHandler.Open(hybrid, null, Loc.Get("wall_coordinate_hint", PlacementGridReader.Length((float)grid.Spacing)), x, z,
                () => _player == hybrid.Player && _player.SegmentInPlacement == segment && (segment == null || segment.InstanceID == id)
                    && ItemPlacementHandler.ResolvePlacementLot(_player, out _) == _lot && _player.GetLotLayer(_lot) == _layer
                    && PlacementGridReader.TryRead(_lot, (float)grid.Spacing, out var current) && grid.SameFrame(current)
                    && Settings.Get<Gameplay>().UsesWallGrid,
                (column, row) =>
                {
                    grid.World(column, row, out double wx, out double wz);
                    var point = new Vector3((float)wx, _target.y, (float)wz);
                    if (!IsOnExpectedLot(point)) { Refuse("wall_outside_lot"); return; }
                    SetTarget(point); _exactCoordinate = true;
                });
        }

        private static bool ReadCursor(out Vector3 position)
        {
            position = default(Vector3);
            if (_player?.NodeCursor == null) return false;
            var widget = WidgetManager.Instance?.GetWidget(_player.PlayerIndex, Settings.Get<Widgets>().WidgetNodeCursor.GUID);
            if (widget == null) return false;
            position = widget.Transform.position;
            return PlacementGrid.Finite(position.x) && PlacementGrid.Finite(position.y) && PlacementGrid.Finite(position.z);
        }

        private static void SetTarget(Vector3 target)
        {
            _exactCoordinate = false;
            _target = target; _hasTarget = true; _stable = 0; _failure = null; _announce = true;
            Pointer.Clear(); _interfaceReframed = false;
        }

        private static void SetCorner(int corner)
        {
            _plan.Point(corner, out double x, out double z);
            SetTarget(new Vector3((float)x, _firstCorner.y, (float)z));
        }

        private static bool Warp(HybridPlayer hybrid)
        {
            if (!_hasTarget) return false; // Still waiting for the native cursor observation.
            if (Mouse.current == null) return Refuse("placement_mouse_missing");
            if (!ItemPlacementHandler.TryProjectTarget(hybrid, _target, out Vector2 screen))
                return Refuse("placement_target_out_of_view");
            if (Vector2.Distance(screen, _screen) > 1f) _stable = 0;
            _screen = screen;
            if (InputManager.Instance.IsPointerOverUI(_player.PlayerIndex)
                && Vector2.Distance(InputManager.Instance.GetCursorPosition(_player.PlayerIndex), screen) <= 2f)
            {
                _stable = 0;
                if (!_interfaceReframed)
                {
                    _interfaceReframed = true;
                    if (WorldTargeting.FrameArea(_player, _lot, _layer, _target, "wall-clear-interface"))
                    { Speak(Loc.Get("wall_view_reframed")); return false; }
                }
                return Refuse("wall_pointer_over_ui");
            }
            Pointer.Expect(screen);
            Mouse.current.WarpCursorPosition(screen);
            _failure = null;
            return true;
        }

        private static bool Ready(HybridPlayer hybrid)
        {
            _extensionProblem = _extension?.Problem();
            if (_extensionProblem != null) return false;
            if (_shared != null && !_shared.IsLive()) { _failure = "wall_shared_changed"; return false; }
            if (_stable < 2 || _grid == null || _player.StartNewSegment || _player.StartNewSegmentNextFrame
                || !ReadCursor(out Vector3 actual) || Vector3.Distance(actual, _actual) > 0.002f
                || Vector2.Distance(InputManager.Instance.GetCursorPosition(_player.PlayerIndex), _screen) > 2f
                || InputManager.Instance.IsPointerOverUI(_player.PlayerIndex) || _player.NodeCursor.PositionIllegal)
                return false;
            if (_exactCoordinate && !SameXZ(actual, _target)) { _failure = "wall_coordinate_mismatch"; return false; }
            var segment = _player.SegmentInPlacement;
            if (segment == null) return _observedId == -1 && _player.CurrentHoveredFloorLayer == _layer
                && IsOnExpectedLot(actual);
            if (segment.InstanceID != _observedId || segment.LotPlacedOnGUID != _lot || segment.Layer != _layer
                || segment.NodeA == null || segment.NodeB == null || segment.IsCurved || segment.GetPrice() != _observedPrice
                || !SameXZ(segment.NodeB.Position, actual)
                || !SameXZ(segment.NodeA.Position, _observedA) || !SameXZ(segment.NodeB.Position, _observedB)) return false;
            if (_plan == null) return true;
            _plan.Point(_plan.CompletedEdges, out double ax, out double az);
            _plan.Point(_plan.CompletedEdges + 1, out double bx, out double bz);
            return SameXZ(segment.NodeA.Position, new Vector3((float)ax, 0, (float)az))
                && SameXZ(segment.NodeB.Position, new Vector3((float)bx, 0, (float)bz));
        }

        private static void PrepareEdge(HybridPlayer hybrid)
        {
            if (_player.StartNewSegment || _player.StartNewSegmentNextFrame || _pendingStart) return;
            var segment = _player.SegmentInPlacement;
            if (segment != null)
            {
                _plan.Point(_plan.CompletedEdges, out double x, out double z);
                if (segment.LotPlacedOnGUID != _lot || segment.Layer != _layer || segment.NodeA == null
                    || !SameXZ(segment.NodeA.Position, new Vector3((float)x, 0, (float)z)))
                { Speak(Loc.Get("wall_rectangle_snap")); StopGuide(); return; }
                _guide = Guide.ReviewingEdge;
                SetCorner(_plan.CompletedEdges + 1);
            }
            else if (Ready(hybrid))
            {
                if (!SameXZ(_actual, _target)) { Speak(Loc.Get("wall_rectangle_snap")); StopGuide(); return; }
                Confirm(hybrid); // Starts a native preview; purchase still requires a later Enter.
            }
        }

        private static void Confirm(HybridPlayer hybrid)
        {
            var segment = _player.SegmentInPlacement;
            _pendingStart = segment == null;
            _pendingId = segment != null ? segment.InstanceID : -1;
            _accepted = false;
            _nativeConfirmBlocked = false;
            Plugin.Log.LogInfo($"[Walls] queued native confirm: lot={_lot}, layer={_layer}, segment={_pendingId}, guide={_guide}.");
        }

        private static bool SameXZ(Vector3 a, Vector3 b) => Math.Abs(a.x - b.x) <= 0.02f && Math.Abs(a.z - b.z) <= 0.02f;

        private static bool Refuse(string key)
        {
            if (_failure != key) Speak(Loc.Get(key));
            _failure = key; _stable = 0;
            return false;
        }

        private static string Position(Vector3 point)
        {
            if (_grid == null) return Loc.Get("wall_grid_unavailable");
            _grid.Coordinates(point.x, point.z, out double c, out double r);
            return Loc.Get("wall_position", c.ToString("0.##"), r.ToString("0.##"), _layer);
        }

        private static string DescribeDetailed()
        {
            if (_failure != null) return Loc.Get(_failure);
            var segment = _player.SegmentInPlacement;
            string result = Position(_actual);
            if (_hasTarget && !SameXZ(_actual, _target)) result += ". " + Loc.Get("wall_snap", Position(_target));
            if (segment != null)
                result += ". " + Loc.Get("wall_preview", Position(segment.NodeA.Position),
                    PlacementGridReader.Length(segment.Length), segment.GetPrice());
            if (_player.NodeCursor?.PositionIllegal == true) result += ". " + Loc.Get("wall_illegal");
            if (!IsOnExpectedLot(_actual)) result += ". " + Loc.Get("wall_outside_lot");
            if (_guide == Guide.ReviewingEdge) result += ". " + Loc.Get("wall_rectangle_edge", _plan.CompletedEdges + 1);
            if (_guide == Guide.ReviewingSharedEdge) result += ". " + Loc.Get("wall_shared_review", _shared.Walls.Count, Position(_target));
            if (_guide == Guide.StartingEdge) result += ". " + Loc.Get("wall_rectangle_preparing", _plan.CompletedEdges + 1);
            if (_guide == Guide.FirstCorner) result += ". " + Loc.Get("wall_rectangle_first");
            if (_guide == Guide.SecondCorner) result += ". " + Loc.Get("wall_rectangle_second", Position(_firstCorner));
            string angles = DescribeAngles();
            if (angles != null) result += ". " + angles;
            return result;
        }

        // UpdateAngleWidget displays the absolute SignedAngle of each native marker. Read
        // those same live values on demand; do not invent angles from the rectangle plan.
        private static string DescribeAngles()
        {
            if (_player?.SegmentInPlacement == null) return null;
            var profile = Settings.Get<Widgets>().WidgetAngleCurve;
            var widgets = profile == null ? null : WidgetManager.Instance?.GetWidgets(_player.PlayerIndex, profile.GUID);
            if (widgets == null) return null;
            var seen = new HashSet<int>();
            var labels = new List<string>();
            foreach (var widget in widgets)
            {
                if (!(widget is NodeAngleWidget angle)) continue;
                int value = Math.Abs(angle.SignedAngle);
                if (value > 0 && seen.Add(value))
                    labels.Add(ScreenReader.NormalizeForOutput(FormatService.GetAngle(value)));
            }
            return labels.Count == 0 ? null : Loc.Get("wall_angle_markers", string.Join(", ", labels));
        }

        private static string Describe()
        {
            string extensionProblem = _extension?.Problem();
            if (extensionProblem != null) return extensionProblem;
            if (_failure != null) return Loc.Get(_failure);
            if (_player.NodeCursor?.PositionIllegal == true) return Loc.Get("wall_illegal");
            if (!IsOnExpectedLot(_actual)) return Loc.Get("wall_outside_lot");
            // Manual corner work is an explicit advanced choice and still needs its addresses.
            if (_guide == Guide.FirstCorner || _guide == Guide.SecondCorner) return DescribeDetailed();
            if (_guide == Guide.ReviewingSharedEdge) return _extension?.Description ?? Loc.Get("wall_shared_review", _shared.Walls.Count, Position(_target));
            var segment = _player.SegmentInPlacement;
            string result = segment == null ? Loc.Get("wall_idle", RectangleKey)
                : Loc.Get("wall_preview_short", PlacementGridReader.Length(segment.Length), segment.GetPrice());
            if (_plan != null && _extension == null)
                result = Loc.Get("room_size_result", PlacementGridReader.Length((float)_plan.Width),
                    PlacementGridReader.Length((float)_plan.Depth)) + ". " + result;
            if (_guide == Guide.ReviewingEdge) result += ". " + (_extension != null
                ? Loc.Get("extension_wall_edge", _plan.CompletedEdges) : Loc.Get("wall_rectangle_edge", _plan.CompletedEdges + 1));
            else if (_guide == Guide.StartingEdge) result = _extension != null
                ? Loc.Get("extension_wall_preparing", _plan.CompletedEdges) : Loc.Get("wall_rectangle_preparing", _plan.CompletedEdges + 1);
            return result;
        }

        /// <summary>Provides the current native cursor, preview dimensions and guide stage for F3.</summary>
        internal static bool TryGetContext(HybridPlayer hybrid, out string report)
        {
            report = null;
            if (!OwnsInput || hybrid?.Player != _player) return false;
            report = Describe();
            if (_guide == Guide.None && _player.SegmentInPlacement == null)
                report += ". " + BuildCameraHandler.DescribeCamera(hybrid, false);
            return true;
        }

        private static void CheckRoom()
        {
            _roomPasses++;
            // DirtyZones is delivered on the following system pass. Do not inspect stale zones.
            if (_roomPasses < 3 || !BuildModeRefreshManager.IsZoneStateRefreshed
                || ZoneManager.Instance.DirtyWallZones.Count != 0)
            {
                if (Time.unscaledTime < _roomDeadline) return;
                Speak(Loc.Get("wall_room_unverified")); StopGuide(false); return;
            }
            foreach (var zone in ZoneManager.Instance.GetLotZones(_lot))
            {
                if (!(zone is ZoneWallObject) || zone.Layer != _layer || PreviousRooms.Contains(zone.InstanceID)
                    || zone.Segments == null || zone.Segments.Count < 4
                    || Math.Abs(zone.SurfaceArea - _plan.Width * _plan.Depth) > 0.05) continue;
                bool matches = true;
                foreach (var edge in zone.Segments)
                {
                    var wall = edge.Segment;
                    if (wall == null || wall.IsCurved || wall.NodeA == null || wall.NodeB == null
                        || !_plan.IsBoundaryEdge(wall.NodeA.Position.x, wall.NodeA.Position.z,
                            wall.NodeB.Position.x, wall.NodeB.Position.z)) { matches = false; break; }
                }
                if (!matches) continue;
                Speak(Loc.Get("wall_room_verified", ScreenReader.NormalizeForOutput(MeasurementUnitService.FormatSurfaceArea(zone.SurfaceArea))));
                StopGuide(false); return;
            }
            Speak(Loc.Get("wall_room_unverified")); StopGuide(false);
        }

        private static void StopGuide(bool announce = true)
        {
            if (_guide != Guide.None && announce) Speak(Loc.Get("wall_rectangle_stopped", Math.Max(0, (_plan?.CompletedEdges ?? 0) - _reusedEdges)));
            if (_guide == Guide.CheckingRoom) _quietIdleReturn = true;
            _guide = Guide.None; _plan = null; _shared = null; _reusedEdges = 0; PreviousRooms.Clear();
            _extension = null; _extensionProblem = null; _guideInputBlocked = false;
        }

        private static void Release(bool holdFrame = true)
        {
            Pointer.Clear(); _interfaceReframed = false;
            if (_player == null) { _unresolvedLotReported = false; return; }
            AfterNativeConfirm(_player);
            StopGuide();
            _player.FreezeCameraMovement = _cameraWasFrozen;
            _player = null; _grid = null; _hasTarget = false; _failure = null;
            _pendingId = -1; _pendingStart = false; _accepted = false; _stable = 0;
            _quietIdleReturn = false;
            if (holdFrame) LotBuildInputOwnership.HoldThroughCurrentFrame();
        }

        private static void Fail(Exception ex)
        {
            Enabled = false;
            Release();
            Plugin.Log.LogError("Wall construction disabled after an exception: " + ex);
            Speak(Loc.Get("wall_handler_failed"));
        }

        private static void Speak(string message)
        {
            if (_player != null) Plugin.Log.LogInfo($"[Walls] {message} | lot={_lot}, layer={_layer}, "
                + $"guide={_guide}, segment={_player.SegmentInPlacement?.InstanceID}, requested={_target}, "
                + $"actual={_actual}, screen={_screen}, stable={_stable}, failure={_failure}.");
            ScreenReader.Speak(message);
        }
    }
}
