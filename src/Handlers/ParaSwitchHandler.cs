using System;
using System.Collections.Generic;
using ParalivesAccess.Speech;
using UnityEngine.InputSystem;

namespace ParalivesAccess.Handlers
{
    /// <summary>Cycles household control and camera follow without navigating the portrait strip.</summary>
    internal static class ParaSwitchHandler
    {
        internal static Key SwitchKey { get; set; } = Key.F9;
        internal static bool Enabled { get; set; } = true;

        internal static void Tick()
        {
            Keyboard keyboard = Keyboard.current;
            HybridPlayer hybrid = PlayerManager.Instance?.HybridPlayer1;
            if (!Enabled || !ModInputKey.WasPressed(keyboard, SwitchKey)
                || hybrid == null || hybrid.HasInputFieldFocused
                || keyboard.ctrlKey.isPressed || keyboard.altKey.isPressed
                || SettingsHandler.IsCapturingBindingInput) return;
            try
            {
                if (!ModInputKey.IsAvailable(hybrid, SwitchKey, out string conflict))
                {
                    ScreenReader.Speak(conflict);
                    return;
                }
                Player player = hybrid.Player;
                UICharacters portraits = UI.GetOrNull<UICharacters>(player.PlayerIndex);
                if (GameLoadingManager.State != State.Game || player.State != GameStates.LiveMode
                    || !player.IsUsingFreeCamera || WindowHandler.IsSceneTransitionActive
                    || player.IsInIntro || player.PlayerActionBlockedByUIWindow
                    || !player.CanChangeCharacterSelection() || player.ItemInPlacement != null
                    || player.TownMapForFastTravelMethod != 0UL
                    || portraits?.IsInTargetCharacterSelectionMode == true
                    || portraits?.IsTogetherCardsMode == true
                    || TutorialHandler.OwnsInput || ContextActionHandler.OwnsInput
                    || OccupationHandler.OwnsInput || ItemPlacementHandler.OwnsInput)
                {
                    ScreenReader.Speak(Loc.Get("para_switch_unavailable"));
                    return;
                }

                var candidates = new List<AssetCharacter>();
                List<ulong> household = HouseholdManager.Instance?.CurrentHousehold?.Data?.Characters;
                if (household != null)
                    foreach (ulong guid in household)
                    {
                        AssetCharacter character = CharacterManager.Instance?.GetCharacterByGUID(guid);
                        if (character?.Data != null && !character.Data.IsDeadOrTakenAway)
                            candidates.Add(character);
                    }
                if (candidates.Count == 0)
                {
                    ScreenReader.Speak(Loc.Get("para_switch_none"));
                    return;
                }
                int current = candidates.FindIndex(c => c.GUID == player.GetSelectedCharacterGUID());
                bool previous = keyboard.shiftKey.isPressed;
                int index = current < 0 ? (previous ? candidates.Count - 1 : 0)
                    : (current + (previous ? -1 : 1) + candidates.Count) % candidates.Count;
                AssetCharacter target = candidates[index];
                // Call the selection operation directly: TrySelect interprets Shift as grouping.
                CharacterManager.Instance.SelectCharacter(target.GUID, player.PlayerIndex);
                if (player.GetSelectedCharacterGUID() != target.GUID
                    || player.SelectedCharactersGUID.Count != 1)
                {
                    ScreenReader.Speak(Loc.Get("para_switch_unavailable"));
                    return;
                }
                LotObjectHandler.CloseForParaSwitch();
                TownMapLotHandler.CloseForParaSwitch();
                player.CameraCurrentCharacterFollowTarget = target.GUID;
                bool framed = CameraFollowHandler.FrameFollowStart(player, target, "household-switch");
                ScreenReader.Speak(Loc.Get(framed ? "para_switch_selected" : "para_switch_selected_pending",
                    target.Data.FullName, index + 1, candidates.Count));
                Plugin.Log.LogInfo($"[ParaSwitch] selected={target.GUID}, index={index + 1}, "
                    + $"count={candidates.Count}, previous={previous}, framed={framed}.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Switching household Para failed: " + ex);
                ScreenReader.Speak(Loc.Get("para_switch_failed"));
            }
        }
    }
}
