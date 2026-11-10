using System;
using ParalivesAccess.Speech;
using UnityEngine.InputSystem;

namespace ParalivesAccess.Handlers
{
    /// <summary>One explicit description per key press, including during construction previews.</summary>
    internal static class LotDescriptionHandler
    {
        internal static Key DescribeKey = Key.J;

        internal static void Tick()
        {
            try
            {
                var keyboard = Keyboard.current;
                var hybrid = PlayerManager.Instance?.HybridPlayer1;
                var player = hybrid?.Player;
                if (keyboard == null || player == null || GameLoadingManager.State != State.Game
                    || !ModInputKey.WasPressed(keyboard, DescribeKey) || hybrid.HasInputFieldFocused
                    || keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed
                    || keyboard.leftAltKey.isPressed || keyboard.rightAltKey.isPressed
                    || keyboard.leftMetaKey.isPressed || keyboard.rightMetaKey.isPressed
                    || SettingsHandler.IsCapturingBindingInput || CoordinateEntryHandler.BlocksPlacement
                    || player.PlayerActionBlockedByUIWindow || (TownMapLotHandler.Enabled && TownMapLotHandler.OwnsInput)
                    || BuildCatalogHandler.OwnsInput || TutorialHandler.OwnsInput
                    || ContextActionHandler.OwnsInput || OccupationHandler.OwnsInput
                    || (player.State != GameStates.LiveMode && player.State != GameStates.BuildMode
                        && player.State != GameStates.PlaceSegment && player.State != GameStates.Painting)) return;
                string report = ModInputKey.IsAvailable(hybrid, DescribeKey, out string conflict)
                    ? LotLayoutReader.Describe(hybrid, keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed) : conflict;
                Plugin.Log.LogInfo("[LotDescription] " + report);
                ScreenReader.Speak(report);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("LotDescriptionHandler.Tick threw: " + ex);
                ScreenReader.Speak(Loc.Get("layout_unavailable"));
            }
        }
    }
}
