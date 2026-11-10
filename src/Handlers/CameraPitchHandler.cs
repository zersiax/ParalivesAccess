using ParalivesAccess.Speech;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ParalivesAccess.Handlers
{
    internal static class CameraPitchHandler
    {
        private static HybridCameraFree _camera;
        private static float _previousRatio = .5f, _previousExtra;
        private static int _consumedFrame = -1;
        internal static bool ConsumedThisFrame => _consumedFrame == Time.frameCount;
        internal static void BeforeGameSystems()
        {
            var keys = Keyboard.current;
            if (keys == null || !keys.f11Key.wasPressedThisFrame || !keys.ctrlKey.isPressed
                || keys.shiftKey.isPressed || keys.altKey.isPressed || keys.leftMetaKey.isPressed || keys.rightMetaKey.isPressed) return;
            var h = PlayerManager.Instance?.HybridPlayer1;
            if (!CanUse(h)) return;
            _consumedFrame = Time.frameCount;
            ScreenReader.Speak(ModInputKey.IsAvailable(h, Key.F11, out string conflict) ? Toggle(h) : conflict);
        }
        private static bool CanUse(HybridPlayer h) => GameLoadingManager.State == State.Game && Application.isFocused
            && h?.Player?.State == GameStates.BuildMode && h.Player.IsUsingFreeCamera && !h.HasInputFieldFocused
            && !h.Player.PlayerActionBlockedByUIWindow && !h.IsUsingGamePad && !SettingsHandler.IsCapturingBindingInput
            && !CoordinateEntryHandler.BlocksPlacement && h.HybridCamera?.FreeCamera != null;
        internal static string Toggle(HybridPlayer h)
        {
            if (!CanUse(h)) return Loc.Get("camera_pitch_unavailable");
            var c = h.HybridCamera.FreeCamera;
            if (c == _camera && c.XAngleRatio >= .999f && c.CurrentExtraLookUpRatio >= -.001f)
            { c.XAngleRatio = _previousRatio; c.CurrentExtraLookUpRatio = _previousExtra; return Loc.Get("camera_pitch_restored"); }
            _camera = c; _previousRatio = c.XAngleRatio; _previousExtra = c.CurrentExtraLookUpRatio;
            c.XAngleRatio = 1f; c.CurrentExtraLookUpRatio = 0f;
            return Loc.Get("camera_pitch_top");
        }
    }
}
