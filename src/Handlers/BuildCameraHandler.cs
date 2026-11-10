using System;
using ParalivesAccess.Speech;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ParalivesAccess.Handlers
{
    /// <summary>On-demand camera orientation when no placement tool or browser owns the controls.</summary>
    internal static class BuildCameraHandler
    {
        private static bool CanReport(HybridPlayer hybrid) => GameLoadingManager.State == State.Game
            && hybrid?.Player != null && hybrid.Player.State == GameStates.BuildMode
            && hybrid.Player.ItemInPlacement == null && hybrid.Player.SegmentInPlacement == null
            && !hybrid.HasInputFieldFocused && !hybrid.Player.PlayerActionBlockedByUIWindow
            && !SettingsHandler.IsCapturingBindingInput && !CoordinateEntryHandler.BlocksPlacement
            && !(TownMapLotHandler.Enabled && TownMapLotHandler.OwnsInput)
            && !LotBuildInputOwnership.OwnsInput;

        internal static bool TryGetContext(HybridPlayer hybrid, out string report)
        {
            report = null;
            if (!CanReport(hybrid)) return false;
            report = DescribeCamera(hybrid, false);
            return true;
        }

        internal static void Tick()
        {
            try
            {
                var hybrid = PlayerManager.Instance?.HybridPlayer1;
                if (!CanReport(hybrid) || !ModInputKey.WasPressed(Keyboard.current, Key.F11)) return;
                string report = ModInputKey.IsAvailable(hybrid, Key.F11, out string conflict)
                    ? DescribeCamera(hybrid, true) : conflict;
                Plugin.Log.LogInfo("[BuildCamera] " + report);
                ScreenReader.Speak(report);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("BuildCameraHandler.Tick threw: " + ex);
                ScreenReader.Speak(Loc.Get("build_camera_unavailable"));
            }
        }

        internal static string DescribeCamera(HybridPlayer hybrid, bool detailed)
        {
            var focus = hybrid?.HybridCamera?.FreeCamera?.LookTarget;
            if (focus == null || !PlacementGrid.Finite(focus.position.x) || !PlacementGrid.Finite(focus.position.z))
                return Loc.Get("build_camera_unavailable");
            ulong lot = ItemPlacementHandler.ResolvePlacementLot(hybrid.Player, out _);
            var perimeter = ZoneManager.Instance?.GetLotPerimeterZoneObjectByGUID(lot);
            var data = LotManager.Instance?.GetLotByGUID(lot)?.GetGridData();
            if (lot == 0 || perimeter == null || data == null
                || !PlacementGridReader.TryRead(lot, data.CellDimension, out var grid))
                return Loc.Get("build_camera_lot_unavailable");

            Vector3 position = focus.position;
            int layer = hybrid.Player.GetLotLayer(lot);
            bool inside = ZoneGeometry.Contains(perimeter, position);
            string location = Loc.Get("build_outside_lot");
            if (inside)
            {
                var rooms = RoomSpatialReader.OrderedRooms(lot, layer);
                int index = rooms.FindIndex(room => ZoneGeometry.Contains(room, position));
                string area = index >= 0 ? RoomLabelHandler.Name(rooms[index], index + 1) : Loc.Get("build_camera_outdoors");
                string relative = SpatialOutline.TryRead(perimeter, grid, out var outline)
                    ? outline.DescribePosition(grid, position) : Loc.Get("build_location_unavailable");
                location = Loc.Get("build_camera_area", area, relative);
            }
            string report = Loc.Get("build_camera_location", location, layer);
            if (detailed)
                report += ". " + Loc.Get("build_camera_coordinates", PlacementGridReader.Position(grid, position))
                    + ". " + PlacementGridReader.DescribeBounds(lot, grid)
                    + ". " + Loc.Get("build_camera_grid", PlacementGridReader.Length((float)grid.Spacing));
            return report;
        }
    }
}
