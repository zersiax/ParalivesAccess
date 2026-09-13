using System;
using ParalivesAccess.Speech;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>Reports the native camera zoom after a completed user zoom gesture.</summary>
    internal static class CameraZoomHandler
    {
        private static Player _pendingPlayer;
        private static float _pendingRatio;

        /// <summary>Captures distance at the real free-camera consumer without changing input.</summary>
        internal static float BeforeCameraUpdate(Player player)
        {
            try
            {
                HybridPlayer hybrid = player != null
                    ? PlayerManager.Instance?.GetHybridPlayer(player.PlayerIndex) : null;
                if (GameLoadingManager.State != State.Game || player == null
                    || player.State != GameStates.LiveMode || !player.IsUsingFreeCamera
                    || hybrid == null || hybrid.HasInputFieldFocused || hybrid.Zoom.Value == 0f)
                    return float.NaN;
                return hybrid.HybridCamera.FreeCamera.TargetDistance;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Capturing native camera zoom threw: " + ex);
                return float.NaN;
            }
        }

        /// <summary>Retains only a zoom change actually made by UpdateFreeCamera.</summary>
        internal static void AfterCameraUpdate(Player player, float previousDistance)
        {
            if (float.IsNaN(previousDistance))
                return;
            try
            {
                HybridPlayer hybrid = PlayerManager.Instance?.GetHybridPlayer(player.PlayerIndex);
                if (hybrid == null
                    || Mathf.Approximately(previousDistance, hybrid.HybridCamera.FreeCamera.TargetDistance))
                    return;
                _pendingPlayer = player;
                // UpdateFreeCamera already calculated this ratio from its clamped target.
                _pendingRatio = player.AudioSystemCameraZoomRatio;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Observing native camera zoom threw: " + ex);
            }
        }

        /// <summary>Speaks the final value once after keyboard, wheel, or controller zoom stops.</summary>
        internal static void Tick()
        {
            if (_pendingPlayer == null)
                return;
            try
            {
                HybridPlayer hybrid = PlayerManager.Instance?.HybridPlayer1;
                if (GameLoadingManager.State != State.Game
                    || !ReferenceEquals(hybrid?.Player, _pendingPlayer)
                    || _pendingPlayer.State != GameStates.LiveMode
                    || WindowHandler.IsSceneTransitionActive)
                {
                    _pendingPlayer = null;
                    return;
                }
                if (hybrid.Zoom.Value != 0f)
                    return;
                _pendingPlayer = null;
                int zoomPercent = Mathf.RoundToInt(100f * (1f - _pendingRatio));
                string message = Loc.Get("camera_zoom_value", zoomPercent);
                Plugin.Log.LogInfo("[Camera/zoom] " + message);
                ScreenReader.Speak(message);
            }
            catch (Exception ex)
            {
                _pendingPlayer = null;
                Plugin.Log.LogError("Reporting native camera zoom threw: " + ex);
            }
        }
    }
}
