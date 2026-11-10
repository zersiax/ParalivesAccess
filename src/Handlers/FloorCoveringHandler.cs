using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ParalivesAccess.Speech;
using Setting;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ParalivesAccess.Handlers
{
    /// <summary>Applies the selected native covering to a reviewed, live floor platform.</summary>
    internal static class FloorCoveringHandler
    {
        internal static bool Enabled { get; set; } = true;
        internal static bool OwnsInput => Enabled && _player != null && Foreground(PlayerManager.Instance?.HybridPlayer1);
        private static readonly MethodInfo PaintPlatform = typeof(UpdatePaintMode).GetMethod("PaintPlatform",
            BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(Player), typeof(int), typeof(bool) }, null);
        private static Player _player;
        private static ulong _lot;
        private static int _layer, _index, _openedFrame;
        private static bool _cameraWasFrozen;
        private static List<ZoneWallPlatformObject> _targets = new List<ZoneWallPlatformObject>();
        private static Review _review;

        private sealed class Review
        {
            internal ZoneWallPlatformObject Target;
            internal ZoneWallObject Room;
            internal int Instance, RoomInstance, Height;
            internal float Area;
            internal ulong Surface, PreviousSurface;
            internal Color[] Colors, PreviousColors;
            internal Vector3[] Boundary;
        }

        internal static void Open(HybridPlayer hybrid)
        {
            try { OpenCore(hybrid); }
            catch (Exception ex) { StopAfterException(ex); }
        }

        private static void OpenCore(HybridPlayer hybrid)
        {
            Close(false);
            if (!Enabled || PaintPlatform == null || !Foreground(hybrid)
                || Settings.Get<Surfaces>().GetSurfaceByGUID(hybrid.Player.SelectedSurface) == null
                || hybrid.Player.SelectedColors == null || hybrid.Player.SelectedColors.Length == 0)
            { Speak(Loc.Get("floor_cover_unavailable")); return; }
            ulong lot = ItemPlacementHandler.ResolvePlacementLot(hybrid.Player, out _);
            if (lot == 0) { Speak(Loc.Get("wall_lot_required")); return; }
            _player = hybrid.Player; _lot = lot; _layer = _player.GetLotLayer(lot);
            _cameraWasFrozen = _player.FreezeCameraMovement;
            _player.FreezeCameraMovement = true;
            _openedFrame = Time.frameCount; _index = 0;
            RefreshTargets();
            Plugin.Log.LogInfo($"[FloorCovering] opened lot={_lot}, layer={_layer}, targets={_targets.Count}, surface={_player.SelectedSurface}.");
            Speak(Loc.Get("floor_cover_open", CatalogEntryReader.SurfaceName(_player.SelectedSurface)) + ". " + Describe());
        }

        internal static void BeforeGameSystems()
        {
            if (_player == null) return;
            try
            {
                var hybrid = PlayerManager.Instance?.HybridPlayer1;
                if (!Enabled || !Foreground(hybrid) || hybrid.Player != _player) { Close(true); return; }
                _player.FreezeCameraMovement = true;
                if (hybrid.ButtonCancel.Down || hybrid.ButtonUndo.Down || hybrid.ButtonRedo.Down
                    || hybrid.ButtonConfirm.Down || ModInputKey.WasPressed(Keyboard.current, BuildCatalogHandler.OpenKey))
                {
                    // Preserve physical native input and let F6 open the catalogue in its normal tick.
                    Close(true, holdFrame: !ModInputKey.WasPressed(Keyboard.current, BuildCatalogHandler.OpenKey));
                }
            }
            catch (Exception ex) { StopAfterException(ex); }
        }

        internal static void Tick()
        {
            if (_player == null) return;
            try
            {
                var hybrid = PlayerManager.Instance?.HybridPlayer1;
                if (!Enabled || !Foreground(hybrid) || hybrid.Player != _player) { Close(true); return; }
                if (ItemPlacementHandler.ResolvePlacementLot(_player, out _) != _lot || _player.GetLotLayer(_lot) != _layer)
                { Speak(Loc.Get("floor_cover_context_changed")); Close(false); return; }
                if (_openedFrame == Time.frameCount) return;
                var keyboard = Keyboard.current;
                if (keyboard == null) return;
                if (RefreshTargets()) { _review = null; Speak(Loc.Get("floor_cover_targets_changed") + ". " + Describe()); return; }
                int movement = (keyboard.downArrowKey.wasPressedThisFrame || keyboard.rightArrowKey.wasPressedThisFrame ? 1 : 0)
                    - (keyboard.upArrowKey.wasPressedThisFrame || keyboard.leftArrowKey.wasPressedThisFrame ? 1 : 0);
                if (movement != 0)
                {
                    if (_targets.Count > 0) _index = (_index + movement + _targets.Count) % _targets.Count;
                    _review = null; Speak(Describe()); return;
                }
                if (keyboard.homeKey.wasPressedThisFrame)
                {
                    if (!ModInputKey.IsAvailable(hybrid, Key.Home, out string conflict)) Speak(conflict);
                    else if (Current != null)
                        Speak(Loc.Get(WorldTargeting.FrameArea(_player, _lot, _layer, Current.WallZone.AveragePosition, "floor-covering")
                            ? "floor_cover_framed" : "explorer_frame_failed"));
                    else Speak(Loc.Get("floor_cover_no_targets"));
                    return;
                }
                if (!(keyboard.enterKey.wasReleasedThisFrame || keyboard.numpadEnterKey.wasReleasedThisFrame)) return;
                if (keyboard.ctrlKey.isPressed || keyboard.altKey.isPressed || keyboard.shiftKey.isPressed)
                { Speak(Loc.Get("floor_cover_release_modifiers")); return; }
                if (_review == null) BeginReview();
                else Apply();
            }
            catch (Exception ex)
            {
                StopAfterException(ex);
            }
        }

        private static bool Foreground(HybridPlayer hybrid)
        {
            var p = hybrid?.Player;
            return p != null && p.State == GameStates.Painting && p.PaintCategory == PaintCategory.Floors
                && p.PlayerActionsAreAllowed && !p.PlayerActionBlockedByUIWindow
                && !hybrid.HasInputFieldFocused && !hybrid.IsUsingGamePad
                && p.ItemInPlacement == null && p.SegmentInPlacement == null && p.PlatformInPlacement == null
                && !BuildCatalogHandler.OwnsInput && !LotObjectHandler.OwnsInput
                && !TutorialHandler.OwnsInput && !ContextActionHandler.OwnsInput && !OccupationHandler.OwnsInput;
        }

        private static ZoneWallPlatformObject Current => _index >= 0 && _index < _targets.Count ? _targets[_index] : null;

        private static bool IsLive(ZoneWallPlatformObject platform)
        {
            if (platform == null || platform.gameObject == null) return false;
            var room = platform.WallZone;
            return room != null && room.gameObject.activeInHierarchy && platform.gameObject.activeInHierarchy
                && !platform.IsCeiling && !platform.DisabledByUser && !platform.DisabledButVisible
                && room.LotPlacedOnGUID == _lot && room.Layer == _layer && room.Segments != null && room.Segments.Count >= 3
                && room.Platforms != null && room.Platforms.Contains(platform) && ZoneManager.Instance.GetLotZones(_lot).Contains(room);
        }

        private static bool RefreshTargets()
        {
            var selected = Current;
            var targets = new List<ZoneWallPlatformObject>();
            foreach (var zone in ZoneManager.Instance.GetLotZones(_lot))
                if (zone is ZoneWallObject room && room.Platforms != null)
                    foreach (var platform in room.Platforms)
                        if (IsLive(platform) && !targets.Contains(platform)) targets.Add(platform);
            targets.Sort((a, b) => a.WallZone.InstanceID != b.WallZone.InstanceID
                ? a.WallZone.InstanceID.CompareTo(b.WallZone.InstanceID)
                : a.Height != b.Height ? a.Height.CompareTo(b.Height) : a.InstanceID.CompareTo(b.InstanceID));
            bool changed = !_targets.SequenceEqual(targets);
            _targets = targets;
            int previous = selected != null ? _targets.IndexOf(selected) : -1;
            _index = previous >= 0 ? previous : 0;
            return changed;
        }

        private static void BeginReview()
        {
            var target = Current;
            if (target == null) { Speak(Loc.Get("floor_cover_no_targets")); return; }
            if (!IsLive(target) || !BuildModeRefreshManager.IsZoneStateRefreshed
                || ZoneManager.Instance.DirtyWallZones.Count != 0 || _player.SelectedColors == null
                || _player.SelectedColors.Length == 0 || Settings.Get<Surfaces>().GetSurfaceByGUID(_player.SelectedSurface) == null)
            { Speak(Loc.Get("floor_cover_not_ready")); return; }
            if (target.SurfaceGUID == _player.SelectedSurface && SameColors(target.Colors, _player.SelectedColors))
            { Speak(Loc.Get("floor_cover_unchanged")); return; }
            _review = new Review { Target = target, Room = target.WallZone, Instance = target.InstanceID,
                RoomInstance = target.WallZone.InstanceID, Height = target.Height, Area = target.WallZone.SurfaceArea,
                Surface = _player.SelectedSurface, Colors = Copy(_player.SelectedColors),
                PreviousSurface = target.SurfaceGUID, PreviousColors = Copy(target.Colors), Boundary = Boundary(target.WallZone) };
            if (_review.Boundary == null) { _review = null; Speak(Loc.Get("floor_cover_not_ready")); return; }
            string area = AreaText(target.WallZone.SurfaceArea).Replace(" square metres", "-square-metre").Replace(" square feet", "-square-foot");
            Speak(Loc.Get("floor_cover_review", CatalogEntryReader.SurfaceName(_review.Surface), TargetName(target), area));
        }

        private static void Apply()
        {
            var r = _review;
            _review = null; // One confirmation can submit at most one native operation, including exceptions.
            if (r == null || !IsLive(r.Target) || Current != r.Target || r.Target.WallZone != r.Room
                || r.Target.InstanceID != r.Instance || r.Room.InstanceID != r.RoomInstance
                || r.Target.Height != r.Height || r.Room.SurfaceArea != r.Area
                || _player.SelectedSurface != r.Surface || !SameColors(_player.SelectedColors, r.Colors)
                || Settings.Get<Surfaces>().GetSurfaceByGUID(r.Surface) == null
                || r.Target.SurfaceGUID != r.PreviousSurface || !SameColors(r.Target.Colors, r.PreviousColors)
                || !SameBoundary(r.Boundary, Boundary(r.Room))
                || !BuildModeRefreshManager.IsZoneStateRefreshed || ZoneManager.Instance.DirtyWallZones.Count != 0)
            { Speak(Loc.Get("floor_cover_review_changed")); return; }
            var system = SystemManager.Instance?.SystemsToUpdate?.OfType<UpdatePaintMode>().FirstOrDefault();
            if (system == null || PaintPlatform == null) { Speak(Loc.Get("floor_cover_unavailable")); return; }
            var previousHover = _player.PlatformHovered;
            bool previousUnderside = _player.PlatformHoveredIsUnderside;
            try
            {
                // PaintPlatform is the complete native floor/ceiling operation: it records undo,
                // updates material state and dirties the save. Its sole target argument is this
                // hover context. Scope it to this invocation and restore the real cursor context.
                _player.PlatformHovered = r.Target;
                _player.PlatformHoveredIsUnderside = false;
                PaintPlatform.Invoke(system, new object[] { _player, _player.PlayerIndex, false });
            }
            finally
            {
                _player.PlatformHovered = previousHover;
                _player.PlatformHoveredIsUnderside = previousUnderside;
            }
            bool applied = IsLive(r.Target) && r.Target.SurfaceGUID == r.Surface && SameColors(r.Target.Colors, r.Colors);
            Plugin.Log.LogInfo($"[FloorCovering] lot={_lot}, room={r.RoomInstance}, platform={r.Instance}, "
                + $"surface={r.PreviousSurface}->{r.Target.SurfaceGUID}, verified={applied}.");
            Speak(Loc.Get(applied ? "floor_cover_applied" : "floor_cover_not_verified", CatalogEntryReader.SurfaceName(r.Surface)));
        }

        private static Color[] Copy(Color[] colors) => colors == null ? null : (Color[])colors.Clone();
        private static Vector3[] Boundary(ZoneWallObject room)
        {
            var points = new List<Vector3>();
            foreach (var edge in room.Segments)
            {
                if (edge.Segment == null || edge.Segment.SegmentPoints == null) return null;
                points.AddRange(edge.Segment.SegmentPoints);
            }
            return points.Count >= 3 ? points.ToArray() : null;
        }
        private static bool SameBoundary(Vector3[] a, Vector3[] b) => a != null && b != null && a.SequenceEqual(b);
        private static bool SameColors(Color[] a, Color[] b) => a == null || b == null ? a == b : a.SequenceEqual(b);

        private static string TargetName(ZoneWallPlatformObject floor)
        {
            string name = RoomLabelHandler.Name(floor.WallZone);
            return floor.WallZone.Platforms.Count(p => IsLive(p)) > 1
                ? Loc.Get("floor_cover_platform", name, PlacementGridReader.Length(floor.Height * Settings.Get<Wall>().StepHeight)) : name;
        }

        private static string AreaText(float area)
        {
            string text = ScreenReader.NormalizeForOutput(MeasurementUnitService.FormatSurfaceArea(area));
            return text.Replace("ft 2", "square feet").Replace("ft²", "square feet").Replace("ft2", "square feet")
                .Replace("m 2", "square metres").Replace("m²", "square metres").Replace("m2", "square metres");
        }

        private static string Describe() => Describe(false);

        private static string Describe(bool detailed)
        {
            var floor = Current;
            if (floor == null || !IsLive(floor)) return Loc.Get("floor_cover_no_targets");
            if (!detailed) return Loc.Get("floor_cover_target_short", TargetName(floor), CatalogEntryReader.SurfaceName(floor.SurfaceGUID));
            string position = Loc.Get("placement_geometry_unavailable");
            if (PlacementGridReader.TryRead(_lot, ItemPlacementHandler.GridSpacingMetres, out var grid))
                position = PlacementGridReader.Position(grid, floor.WallZone.AveragePosition);
            return Loc.Get("floor_cover_target", _index + 1, _targets.Count, _layer,
                AreaText(floor.WallZone.SurfaceArea),
                PlacementGridReader.Length(floor.Height * Settings.Get<Wall>().StepHeight),
                CatalogEntryReader.SurfaceName(floor.SurfaceGUID), position, TargetName(floor));
        }

        internal static bool TryGetContext(HybridPlayer hybrid, out string report)
        {
            report = null;
            if (!OwnsInput || hybrid?.Player != _player) return false;
            report = Loc.Get("floor_cover_context", CatalogEntryReader.SurfaceName(_player.SelectedSurface), Describe(true),
                Loc.Get(_review == null ? "floor_cover_choose" : "floor_cover_confirm"));
            return true;
        }

        private static void Close(bool announce, bool holdFrame = true)
        {
            if (_player == null) return;
            _player.FreezeCameraMovement = _cameraWasFrozen;
            _player = null; _targets.Clear(); _review = null;
            if (holdFrame) LotBuildInputOwnership.HoldThroughCurrentFrame();
            if (announce) Speak(Loc.Get("floor_cover_closed"));
        }

        private static void StopAfterException(Exception ex)
        {
            Plugin.Log.LogError("Floor covering workflow stopped: " + ex);
            Close(false); Speak(Loc.Get("floor_cover_failed"));
        }

        private static void Speak(string message) { ScreenReader.Speak(message); }
    }
}
