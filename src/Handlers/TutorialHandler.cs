using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using ParalivesAccess.Speech;
using Setting;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ParalivesAccess.Handlers
{
    /// <summary>
    /// Announces the train introduction and points each lesson at the same accessibility control
    /// the player will use after the tutorial.
    /// </summary>
    /// <remarks>
    /// The tutorial is not an accessibility action runner. World targets are exposed by the
    /// current-lot explorer, while ordinary UI lessons use the shared panel navigator. This class
    /// only observes the game's authoritative tutorial state, supplies contextual labels, and
    /// guides focus when the game reveals a required panel. See game-api.md section 44.
    /// </remarks>
    internal static class TutorialHandler
    {
        internal const int AnnouncementSettleFrames = 3;
        private const int FirstMarkerInstanceId = 4761;
        private const int SecondMarkerInstanceId = 5858;
        private const int ThirdMarkerInstanceId = 5859;
        private const int SandwichInstanceId = 6488;

        private static bool _cameraBaselineCaptured;
        private static bool _cameraWasFrozen;
        private static bool _wasInIntro;
        private static string _committedSignature;
        private static string _pendingSignature;
        private static string _guidedFocusSignature;
        private static int _framesRemaining;
        private static int _lastStateAnnouncementFrame = -1;

        private static bool _goHereReflectionResolved;
        private static FieldInfo _goHereStepField;

        /// <summary>
        /// Always false. Tutorial input is owned by the normal explorer or panel navigator.
        /// </summary>
        internal static bool OwnsInput => false;

        /// <summary>Set false when a required game-loop safety patch is unavailable.</summary>
        internal static bool Enabled { get; set; } = true;

        /// <summary>True while the game's train introduction is active.</summary>
        internal static bool IsActive
        {
            get
            {
                Player player = GetPlayer();
                return player != null && player.IsInIntro;
            }
        }

        /// <summary>
        /// The current tutorial phase token. The lot explorer captures this token so a destination
        /// list from an earlier lesson cannot remain open after the game advances.
        /// </summary>
        internal static string CurrentPhaseId
        {
            get
            {
                try
                {
                    Player player = GetPlayer();
                    return player != null && player.IsInIntro
                        ? BuildState(player)?.Id
                        : null;
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError("Reading the current tutorial phase threw: " + ex);
                    return null;
                }
            }
        }

        /// <summary>
        /// Frame on which the observer last announced a settled lesson or tutorial completion.
        /// </summary>
        /// <remarks>
        /// The interaction lifecycle observer uses this only to coalesce a proven successful
        /// terminal result with the next tutorial instruction. Action failures and non-terminal
        /// lifecycle states are never hidden by this signal.
        /// </remarks>
        internal static int LastStateAnnouncementFrame => _lastStateAnnouncementFrame;

        /// <summary>
        /// Freezes the camera only while the shared UI navigator deliberately owns a tutorial
        /// panel. The lot explorer applies its own camera guard while it is open.
        /// </summary>
        internal static void BeforeGameSystems()
        {
            if (!Enabled || PlayerManager.Instance == null)
            {
                return;
            }

            HybridPlayer hybridPlayer = PlayerManager.Instance.HybridPlayer1;
            Player player = hybridPlayer?.Player;
            bool shouldFreeze = player?.IsInIntro == true
                && MenuNavigationHandler.HasUiPanelSelection;
            if (shouldFreeze && player != null)
            {
                CaptureCameraBaseline(hybridPlayer);
                player.FreezeCameraMovement = true;
            }
            else if (_cameraBaselineCaptured)
            {
                RestoreCamera();
            }
        }

        /// <summary>Observes and announces stable tutorial transitions.</summary>
        internal static void Tick()
        {
            if (!Enabled)
            {
                return;
            }

            try
            {
                Player player = GetPlayer();
                bool inIntro = GameLoadingManager.State == State.Game
                    && player != null && player.IsInIntro;

                if (!inIntro)
                {
                    if (_cameraBaselineCaptured)
                    {
                        RestoreCamera();
                    }

                    MenuNavigationHandler.ClearTutorialFocus();
                    if (_wasInIntro && GameLoadingManager.State == State.Game && player != null)
                    {
                        SpeakState(Loc.Get("tutorial_finished"));
                    }

                    ResetObserver();
                    _wasInIntro = false;
                    return;
                }

                _wasInIntro = true;
                Observe(BuildState(player));
                HandleMultiselectShortcut(player);
            }
            catch (Exception ex)
            {
                Enabled = false;
                MenuNavigationHandler.ClearTutorialFocus();
                RestoreCamera();
                Plugin.Log.LogError("TutorialHandler.Tick threw: " + ex);
                ScreenReader.Speak(Loc.Get("tutorial_handler_failed"));
            }
        }

        /// <summary>Current source-backed guidance, used by the F3 context report.</summary>
        internal static string GetCurrentGuidance()
        {
            try
            {
                Player player = GetPlayer();
                if (player == null || !player.IsInIntro)
                {
                    return null;
                }

                return BuildState(player)?.Message;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Building current tutorial guidance threw: " + ex);
                return null;
            }
        }

        /// <summary>
        /// Returns the exact item the current world lesson prefers inside the normal lot explorer.
        /// </summary>
        internal static bool TryGetPreferredWorldTarget(
            out int instanceId, out string description)
        {
            instanceId = -1;
            description = null;

            Player player = GetPlayer();
            if (player == null || !player.IsInIntro)
            {
                return false;
            }

            TutorialState state = BuildState(player);
            if (state == null)
            {
                return false;
            }

            int ordinal;
            if (TryGetActiveMarkerTarget(state, out instanceId, out ordinal))
            {
                description = Loc.Get(
                    "tutorial_marker", ordinal, GameText("UITutorial_GoHere"));
                return true;
            }

            if (state.Id == "needs:WaitingToEat")
            {
                instanceId = SandwichInstanceId;
                description = Loc.Get(
                    "tutorial_food_target", GameText("UITutorial_ClickOnFoodToEatIt"));
                return true;
            }

            return false;
        }

        /// <summary>
        /// Returns the exact blue marker for the currently visible Go Here lesson.
        /// </summary>
        internal static bool TryGetActiveMarkerTarget(out int instanceId)
        {
            instanceId = -1;
            Player player = GetPlayer();
            if (player == null || !player.IsInIntro)
            {
                return false;
            }

            int ignored;
            return TryGetActiveMarkerTarget(BuildState(player), out instanceId, out ignored);
        }

        /// <summary>True for one of the train tutorial's three wagon-marker instances.</summary>
        internal static bool IsGoHereMarker(int instanceId)
        {
            return instanceId == FirstMarkerInstanceId
                || instanceId == SecondMarkerInstanceId
                || instanceId == ThirdMarkerInstanceId;
        }

        /// <summary>
        /// Allows a train marker's native interaction only while that exact marker is the target of
        /// the currently authoritative Go Here lesson. The game leaves future marker roots loaded,
        /// and their shared TutorialGoHere outcome advances the manager without validating either
        /// the marker or the current step. They may still be described and framed by the explicit
        /// all-object view, but must not be surfaced as current actions or handed to the native menu.
        /// </summary>
        internal static bool CanUseGoHereMarkerAction(int instanceId)
        {
            if (!IsGoHereMarker(instanceId))
            {
                return true;
            }

            Player player = GetPlayer();
            if (player == null || !player.IsInIntro)
            {
                return true;
            }

            int activeMarker;
            return TryGetActiveMarkerTarget(out activeMarker)
                && instanceId == activeMarker;
        }

        /// <summary>True while the tutorial asks for the exact multi-Para selection.</summary>
        internal static bool IsMultiselectStep => IsMultiselectVisible();

        /// <summary>
        /// True for tutorial presentation announced by this state observer. WindowHandler must not
        /// also scrape it on registration or duplicate the opening instruction.
        /// </summary>
        internal static bool ManagesWindow(UIWindow window)
        {
            if (window == null)
            {
                return false;
            }

            if (window is UICameraTutorial || window is UIGoHereTutorial
                || window is UIClickOnFoodToEatItTutorial || window is UIMultiselectTutorial
                || window is UIPickStorytellerTutorial || window is UITogetherCardsClickTutorial)
            {
                return true;
            }

            return IsActive && (window is UIThoughts || window is UIOfferedWants);
        }

        private static void Observe(TutorialState state)
        {
            if (state == null || string.IsNullOrEmpty(state.Message))
            {
                return;
            }

            string signature = state.Id + "\n" + state.Message;
            if (signature == _committedSignature)
            {
                _pendingSignature = null;
                _framesRemaining = 0;
                if (_guidedFocusSignature != signature && GuideFocus(state))
                {
                    _guidedFocusSignature = signature;
                }
                return;
            }

            if (signature != _pendingSignature)
            {
                _pendingSignature = signature;
                _framesRemaining = AnnouncementSettleFrames;
                return;
            }

            if (--_framesRemaining > 0)
            {
                return;
            }

            _committedSignature = signature;
            _pendingSignature = null;
            _guidedFocusSignature = GuideFocus(state) ? signature : null;
            SpeakState(Loc.Get("tutorial_announcement", state.Message));
        }

        /// <summary>
        /// Bridges Ctrl+U to the game's own household-selection method during the multiselect
        /// tutorial, then reports the authoritative result.
        /// </summary>
        /// <remarks>
        /// This cannot be left to <c>UpdateKeyboardShortcuts</c>. Its entire household-shortcut
        /// block is guarded by <c>!player.IsInIntro</c>, while this lesson necessarily runs with
        /// <c>IsInIntro == true</c>. The one-Para case hid that contradiction because the sole
        /// household member was already selected; a two-Para household remained at one selected.
        ///
        /// Calling <c>SelectAllCharactersInHousehold</c> is the narrow missing adapter, not a
        /// parallel selection implementation. The method still enforces
        /// <c>CanChangeCharacterSelection</c>, which the tutorial enables through
        /// <c>CanChangeSelectionIntro</c>. Outside this exact visible lesson the mod does nothing.
        /// </remarks>
        private static void HandleMultiselectShortcut(Player player)
        {
            Keyboard keyboard = Keyboard.current;
            if (!IsMultiselectVisible() || keyboard == null
                || !keyboard.uKey.wasPressedThisFrame
                || (!keyboard.leftCtrlKey.isPressed && !keyboard.rightCtrlKey.isPressed))
            {
                return;
            }

            List<ulong> household =
                HouseholdManager.Instance?.CurrentHousehold?.Data?.Characters;
            if (household == null || player?.SelectedCharactersGUID == null)
            {
                return;
            }

            int before = CountSelectedHouseholdMembers(player, household);
            bool canChange = player.CanChangeCharacterSelection();
            CharacterManager.Instance?.SelectAllCharactersInHousehold(player.PlayerIndex);

            int selected = CountSelectedHouseholdMembers(player, household);

            bool accepted = household.Count > 0 && selected == household.Count;
            string spoken = accepted
                ? Loc.Get("tutorial_household_selected", selected)
                : Loc.Get(
                    "tutorial_household_selection_failed", selected, household.Count);
            Plugin.Log.LogInfo(
                $"[Tutorial] bridged select-all household={household.Count}, "
              + $"selected={before}->{selected}, totalSelected={player.SelectedCharactersGUID.Count}, "
              + $"canChange={canChange}, accepted={accepted}.");
            Speak(spoken);
        }

        private static int CountSelectedHouseholdMembers(
            Player player, List<ulong> household)
        {
            if (player?.SelectedCharactersGUID == null || household == null)
            {
                return 0;
            }

            int selected = 0;
            foreach (ulong guid in household)
            {
                if (player.SelectedCharactersGUID.Contains(guid))
                {
                    selected++;
                }
            }
            return selected;
        }

        /// <summary>
        /// Places ordinary UI focus on the control a lesson has just revealed. No action is
        /// performed: arrows and Enter continue through MenuNavigationHandler exactly as elsewhere.
        /// </summary>
        private static bool GuideFocus(TutorialState state)
        {
            if (state == null)
            {
                return true;
            }

            if (state.Id == "household:review")
            {
                UITrainSelectStartingHousehold train =
                    UI.GetOrNull<UITrainSelectStartingHousehold>(0);
                return MenuNavigationHandler.FocusForTutorial(train?.UITrainHouseholdInfoPanel);
            }

            if (state.Id == "storyteller:review")
            {
                return MenuNavigationHandler.FocusForTutorial(
                    UI.GetOrNull<UIStorytellerSelection>(0));
            }

            if (state.Id == "wants:ActiveWants")
            {
                UIThoughts thoughts = UI.GetOrNull<UIThoughts>(0);
                return MenuNavigationHandler.FocusForTutorial(
                    thoughts?.UIWants2?.TutorialOverlay);
            }

            if (state.Id == "needs:NeedDescription")
            {
                UIThoughts thoughts = UI.GetOrNull<UIThoughts>(0);
                return MenuNavigationHandler.FocusForTutorial(
                    thoughts?.UINeeds?.NeedsTutorial);
            }

            if (state.Id == "emotions")
            {
                return MenuNavigationHandler.FocusForTutorial(UI.GetOrNull<UIThoughts>(0));
            }

            if (state.Id.StartsWith("wants:PickAWant:", StringComparison.Ordinal))
            {
                return MenuNavigationHandler.FocusForTutorial(UI.GetOrNull<UIOfferedWants>(0));
            }

            if (state.Id == "together:ready")
            {
                // Availability is announced, but the player may be navigating another persistent
                // HUD panel. F4 is the deliberate transfer into conversation choices.
                MenuNavigationHandler.ClearTutorialFocus();
                return true;
            }

            if (state.Id.StartsWith("together:choices:", StringComparison.Ordinal))
            {
                MenuNavigationHandler.ClearTutorialFocus();
                return ContextActionHandler.IsConversationPanelOpen;
            }

            if (state.Id == "together:target")
            {
                MenuNavigationHandler.ClearTutorialFocus();
                return ContextActionHandler.IsConversationPanelOpen;
            }

            if (state.Id == "together:results")
            {
                return MenuNavigationHandler.FocusForTutorial(UI.GetOrNull<UICharacters>(0));
            }

            // Camera and world lessons must leave the camera/explorer path free. This only removes
            // focus that this observer installed; a panel the user explicitly chose is preserved.
            MenuNavigationHandler.ClearTutorialFocus();
            return true;
        }

        private static TutorialState BuildState(Player player)
        {
            TutorialSteps cameraStep = CameraTutorialManager.Instance.CurrentTutorialStep;
            if (cameraStep != TutorialSteps.None)
            {
                switch (cameraStep)
                {
                    case TutorialSteps.Move:
                        return StateOf("camera:Move", Loc.Get("tutorial_camera_step",
                            GameText("UITutorial_MouseMoveCamera"),
                            Loc.Get("tutorial_camera_move_controls")));
                    case TutorialSteps.Rotate:
                        return StateOf("camera:Rotate", Loc.Get("tutorial_camera_step",
                            GameText("UITutorial_MouseRotateCamera"),
                            Loc.Get("tutorial_camera_rotate_controls")));
                    case TutorialSteps.Zoom:
                        return StateOf("camera:Zoom", Loc.Get("tutorial_camera_step",
                            GameText("UITutorial_MouseZoomCamera"),
                            Loc.Get("tutorial_camera_zoom_controls")));
                }
            }

            if (player.IsInSelectHouseholdTutorial)
            {
                UITrainSelectStartingHousehold train =
                    UI.GetOrNull<UITrainSelectStartingHousehold>(0);
                UITrainHouseholdInfoPanel info = train?.UITrainHouseholdInfoPanel;
                if (info != null && info.IsVisible)
                {
                    string name = CleanGameText(info.HeaderLabel?.text);
                    string biography = CleanGameText(info.BiographyLabel?.text);
                    string details = string.IsNullOrEmpty(biography)
                        ? name
                        : Loc.Get("tutorial_household_choice", name, biography);
                    return StateOf("household:review", Loc.Get(
                        "tutorial_household_review", details,
                        Loc.Get("tutorial_panel_controls")));
                }

                bool visualRegistryLoading;
                int choices = CountLoadedHouseholdChoices(out visualRegistryLoading);
                if (choices > 0)
                {
                    return StateOf("household:list", Loc.Get("tutorial_select_household",
                        GameText("UITrainSelectStartingHousehold_Header"),
                        LotObjectHandler.OpenKey));
                }

                return visualRegistryLoading
                    ? StateOf("household:loading", Loc.Get("tutorial_household_loading",
                        GameText("UITrainSelectStartingHousehold_Header")))
                    : StateOf("household:unavailable", Loc.Get(
                        "tutorial_household_unavailable",
                        GameText("UITrainSelectStartingHousehold_Header")));
            }

            if (player.IsInSelectStoryteller)
            {
                UIStorytellerSelection selection = UI.GetOrNull<UIStorytellerSelection>(0);
                if (selection != null && selection.IsVisible)
                {
                    string summary = CleanGameText(UiLabelReader.GetLabel(selection));
                    return StateOf("storyteller:review", Loc.Get(
                        "tutorial_storyteller_review", summary,
                        Loc.Get("tutorial_panel_controls")));
                }

                int choices = CountLoadedStorytellerChoices();
                return choices == 0
                    ? StateOf("storyteller:loading", Loc.Get("tutorial_storyteller_loading",
                        GameText("UITutorial_PickStoryteller")))
                    : StateOf("storyteller:list", Loc.Get("tutorial_select_storyteller",
                        GameText("UITutorial_PickStoryteller"), LotObjectHandler.OpenKey));
            }

            UICharacters characters = UI.GetOrNull<UICharacters>(0);
            if (characters != null && characters.IsShowingOutcomes)
            {
                return StateOf("together:results", Loc.Get("tutorial_topics_results"));
            }
            if (characters != null && characters.IsTogetherCardsMode
                && characters.IsInTargetCharacterSelectionMode)
            {
                return StateOf("together:target", Loc.Get("tutorial_topic_target",
                    Loc.Get("context_choice_controls")));
            }
            if (characters != null && characters.IsTogetherCardsMode)
            {
                string choices = GetTogetherCardChoices(characters);
                string speaker;
                string prompt;
                string message;
                if (TryGetNpcInitiativeContext(
                    characters, out speaker, out prompt))
                {
                    string context = string.IsNullOrEmpty(speaker)
                        ? Loc.Get("tutorial_npc_prompt", prompt)
                        : Loc.Get("tutorial_npc_prompt_named", speaker, prompt);
                    message = string.IsNullOrEmpty(choices)
                        ? Loc.Get("tutorial_replies_open", context,
                            Loc.Get("context_choice_controls"))
                        : Loc.Get("tutorial_replies_choices", context, choices,
                            Loc.Get("context_choice_controls"));
                }
                else
                {
                    message = string.IsNullOrEmpty(choices)
                        ? Loc.Get("tutorial_topics_open", Loc.Get("context_choice_controls"))
                        : Loc.Get("tutorial_topics_choices", choices,
                            Loc.Get("context_choice_controls"));
                }
                return StateOf("together:choices:" + choices, message);
            }

            if (EmotionsTutorialManager.Instance?.IsInTutorial == true)
            {
                string instruction = JoinGameText(
                    GameText("UIThoughtsEmotions_OverlayTitle"),
                    GameText("UIThoughtsEmotions_OverlayDescription"));
                return StateOf("emotions", Loc.Get("tutorial_ui_step", instruction,
                    Loc.Get("tutorial_panel_controls")));
            }

            WantsTutorialSteps wantsStep = WantsTutorialManager.Instance != null
                ? WantsTutorialManager.Instance.TutorialStep
                : WantsTutorialSteps.Inactive;
            if (wantsStep == WantsTutorialSteps.PickAWant)
            {
                UIOfferedWants offered = UI.GetOrNull<UIOfferedWants>(0);
                string summary = offered != null && offered.IsVisible
                    ? CleanGameText(UiLabelReader.GetLabel(offered))
                    : GameText("UIOfferedWants_TutorialDescription");
                return StateOf("wants:PickAWant:" + summary,
                    Loc.Get("tutorial_ui_step", summary, Loc.Get("tutorial_panel_controls")));
            }
            if (wantsStep == WantsTutorialSteps.ActiveWants)
            {
                UIThoughts thoughts = UI.GetOrNull<UIThoughts>(0);
                string instruction = CleanGameText(UiLabelReader.ReadTutorialInstruction(
                    thoughts?.UIWants2?.TutorialOverlay));
                if (string.IsNullOrEmpty(instruction))
                {
                    instruction = GameText("UIThoughtsWants_OverlayDescription");
                }
                return StateOf("wants:ActiveWants", Loc.Get(
                    "tutorial_ok_continue", instruction));
            }

            if (IsMultiselectVisible())
            {
                return StateOf("multiselect", Loc.Get("tutorial_multiselect",
                    GameText("UITutorial_SelectAllCharacters"), LotObjectHandler.OpenKey));
            }

            UIGoHereTutorial goHere = UI.GetOrNull<UIGoHereTutorial>(0);
            if (goHere != null && goHere.IsVisible)
            {
                string step = GetGoHereStepName();
                return StateOf("go-here:" + step, Loc.Get("tutorial_world_target",
                    GameText("UITutorial_GoHere"), LotObjectHandler.OpenKey));
            }

            NeedsTutorialStep needsStep = NeedsTutorialManager.Instance != null
                ? NeedsTutorialManager.Instance.TutorialStep
                : NeedsTutorialStep.Inactive;
            switch (needsStep)
            {
                case NeedsTutorialStep.NeedDescription:
                    UIThoughts thoughts = UI.GetOrNull<UIThoughts>(0);
                    string instruction = CleanGameText(
                        UiLabelReader.ReadTutorialInstruction(
                            thoughts?.UINeeds?.NeedsTutorial));
                    if (string.IsNullOrEmpty(instruction))
                    {
                        instruction = GameText("UINeeds_TutorialDescription");
                    }
                    return StateOf("needs:NeedDescription", Loc.Get(
                        "tutorial_ok_continue", instruction));
                case NeedsTutorialStep.WaitingToEat:
                    return StateOf("needs:WaitingToEat", Loc.Get("tutorial_world_target",
                        GameText("UITutorial_ClickOnFoodToEatIt"), LotObjectHandler.OpenKey));
                case NeedsTutorialStep.IsEating:
                    return StateOf("needs:IsEating", Loc.Get("tutorial_wait_eating"));
            }

            UITogetherCardsClickTutorial together =
                UI.GetOrNull<UITogetherCardsClickTutorial>(0);
            bool togetherVisible = together != null && together.IsVisible;
            if (togetherVisible || needsStep == NeedsTutorialStep.AfterStoryteller)
            {
                bool ready = FindReadyTogetherBar() != null;
                return StateOf(ready ? "together:ready" : "together:filling",
                    ready
                        ? Loc.Get("tutorial_together_ready",
                            GameText("UITutorial_TogetherBarFillUp"),
                            ContextActionHandler.ActionKey)
                        : Loc.Get("tutorial_together_filling",
                            GameText("UITutorial_TogetherBarFillUp")));
            }

            return StateOf("intro:transition", Loc.Get("tutorial_transition"));
        }

        private static int CountLoadedHouseholdChoices(out bool visualRegistryLoading)
        {
            int choices = LotObjectHandler.GetTutorialHouseholdChoiceCount(
                out visualRegistryLoading);
            return choices
                + (LotObjectHandler.IsTutorialHouseholdCreationAvailable() ? 1 : 0);
        }

        private static int CountLoadedStorytellerChoices()
        {
            Storytellers setting = Settings.Get<Storytellers>();
            if (setting?.AllStorytellers == null)
            {
                return 0;
            }

            int count = 0;
            foreach (Storyteller storyteller in setting.AllStorytellers)
            {
                if (storyteller != null)
                {
                    count++;
                }
            }
            return count;
        }

        private static string GetTogetherCardChoices(UICharacters characters)
        {
            if (characters == null)
            {
                return null;
            }

            var choices = new List<string>();
            foreach (UITogetherCard card in
                characters.GetComponentsInChildren<UITogetherCard>(includeInactive: false))
            {
                string label = CleanGameText(UiLabelReader.GetLabel(card));
                if (!string.IsNullOrEmpty(label) && !choices.Contains(label))
                {
                    choices.Add(label);
                }
            }

            return choices.Count == 0
                ? null
                : string.Join(Loc.Get("list_separator"), choices.ToArray());
        }

        private static UITogetherCard FindFirstTogetherCard()
        {
            UICharacters characters = UI.GetOrNull<UICharacters>(0);
            if (characters == null)
            {
                return null;
            }

            foreach (UITogetherCard card in
                characters.GetComponentsInChildren<UITogetherCard>(includeInactive: false))
            {
                if (card != null && card.gameObject.activeInHierarchy)
                {
                    return card;
                }
            }

            return null;
        }

        private static Focusable FindReadyTogetherBar()
        {
            UICharacters characters = UI.GetOrNull<UICharacters>(0);
            if (characters?.List?.CurrentItems == null)
            {
                return null;
            }

            foreach (UIListItemBase item in characters.List.CurrentItems)
            {
                var bar = item as UICharactersTogetherBar;
                if (bar != null && bar.gameObject.activeInHierarchy
                    && bar.ButtonBarFull != null && bar.ButtonBarFull.activeInHierarchy)
                {
                    // The outer UIListItemBase is registered and labelled like the bar, but TEST
                    // 53 proved its click does not open the choices. The source-wired
                    // OpenTogetherChoices control is the active child represented by
                    // ButtonBarFull; it sits exactly one Right Arrow from the inert outer item.
                    Focusable action = bar.ButtonBarFull.GetComponent<Focusable>();
                    if (action == null)
                    {
                        action = bar.ButtonBarFull
                            .GetComponentInChildren<Focusable>(includeInactive: false);
                    }
                    if (action != null && action.gameObject.activeInHierarchy)
                    {
                        return action;
                    }
                }
            }

            return null;
        }

        private static bool TryGetNpcInitiativeContext(
            UICharacters characters, out string speaker, out string prompt)
        {
            speaker = null;
            prompt = null;
            if (characters?.NPCInitiatives == null
                || !characters.NPCInitiatives.activeInHierarchy)
            {
                return false;
            }

            prompt = CleanGameText(characters.NPCInitiativeLabel?.Text?.text);
            if (string.IsNullOrEmpty(prompt))
            {
                return false;
            }

            Focusable thumbnailOwner = characters.NPCInitiativeThumbnail?
                .GetComponentInParent<Focusable>();
            TooltipOpenerSimple tooltip = thumbnailOwner?
                .GetComponent<TooltipOpenerSimple>();
            speaker = CleanGameText(tooltip?.TextToShow);

            if (string.IsNullOrEmpty(speaker))
            {
                Player player = GetPlayer();
                AssetCharacter selected = player != null
                    ? CharacterManager.Instance?.GetCharacterByGUID(
                        player.GetSelectedCharacterGUID())
                    : null;
                SocialGroupManager socialGroups = SocialGroupManager.Instance;
                ulong groupGuid = selected != null && socialGroups != null
                    ? socialGroups.GetCurrentSocialGroup(selected)
                    : 0UL;
                SocialGroup group = groupGuid != 0UL && socialGroups != null
                    ? socialGroups.GetSocialGroupByGUID(groupGuid)
                    : null;
                AssetCharacter npc = group != null && group.NPCInitiative.Item1
                    ? CharacterManager.Instance?.GetCharacterByGUID(
                        group.NPCInitiative.Item2)
                    : null;
                speaker = CleanGameText(npc?.Data?.FullName);
            }

            return true;
        }

        private static bool IsMultiselectVisible()
        {
            UIMultiselectTutorial window = UI.GetOrNull<UIMultiselectTutorial>(0);
            return window != null && window.IsVisible;
        }

        private static bool TryGetMarkerTarget(out int instanceId, out int ordinal)
        {
            string step = GetGoHereStepName();
            switch (step)
            {
                case "FirstWagon":
                    instanceId = FirstMarkerInstanceId;
                    ordinal = 1;
                    return true;
                case "SecondWagon":
                    instanceId = SecondMarkerInstanceId;
                    ordinal = 2;
                    return true;
                case "ThirdWagon":
                    instanceId = ThirdMarkerInstanceId;
                    ordinal = 3;
                    return true;
                default:
                    instanceId = -1;
                    ordinal = 0;
                    return false;
            }
        }

        private static bool TryGetActiveMarkerTarget(
            TutorialState state, out int instanceId, out int ordinal)
        {
            if (state != null
                && state.Id.StartsWith("go-here:", StringComparison.Ordinal)
                && TryGetMarkerTarget(out instanceId, out ordinal))
            {
                return true;
            }

            instanceId = -1;
            ordinal = 0;
            return false;
        }

        private static string GetGoHereStepName()
        {
            if (!_goHereReflectionResolved)
            {
                _goHereReflectionResolved = true;
                _goHereStepField = typeof(GoHereTutorialManager).GetField(
                    "_currentStep", BindingFlags.Instance | BindingFlags.NonPublic);
                if (_goHereStepField == null)
                {
                    Plugin.Log.LogWarning(
                        "[Tutorial] Could not resolve GoHereTutorialManager._currentStep; "
                      + "the explorer cannot prefer the active marker.");
                }
            }

            object value = _goHereStepField?.GetValue(GoHereTutorialManager.Instance);
            return value != null ? value.ToString() : "Unknown";
        }

        private static TutorialState StateOf(string id, string message)
        {
            return new TutorialState(id, message);
        }

        private static string GameText(string key)
        {
            string text = TranslationManager.Get(key);
            return CleanGameText(string.IsNullOrEmpty(text) ? key : text);
        }

        private static string JoinGameText(params string[] values)
        {
            var parts = new List<string>();
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value) && !parts.Contains(value.Trim()))
                {
                    parts.Add(value.Trim());
                }
            }

            return string.Join(Loc.Get("list_separator"), parts.ToArray());
        }

        /// <summary>Removes TMP markup while preserving the game's localized words.</summary>
        private static string CleanGameText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            value = value.Replace("\\n", " ").Replace('\n', ' ').Replace('\r', ' ');
            var builder = new StringBuilder(value.Length);
            bool inTag = false;
            foreach (char c in value)
            {
                if (c == '<')
                {
                    inTag = true;
                    continue;
                }
                if (c == '>' && inTag)
                {
                    inTag = false;
                    continue;
                }
                if (!inTag)
                {
                    builder.Append(c);
                }
            }

            string cleaned = builder.ToString();
            while (cleaned.Contains("  "))
            {
                cleaned = cleaned.Replace("  ", " ");
            }
            return cleaned.Trim();
        }

        private static Player GetPlayer()
        {
            return PlayerManager.Instance?.HybridPlayer1?.Player;
        }

        private static void CaptureCameraBaseline(HybridPlayer hybridPlayer)
        {
            if (_cameraBaselineCaptured || hybridPlayer?.Player == null)
            {
                return;
            }

            _cameraWasFrozen = hybridPlayer.Player.FreezeCameraMovement;
            _cameraBaselineCaptured = true;
            hybridPlayer.Player.FreezeCameraMovement = true;
        }

        private static void RestoreCamera()
        {
            if (!_cameraBaselineCaptured)
            {
                return;
            }

            bool wasFrozen = _cameraWasFrozen;
            _cameraBaselineCaptured = false;
            try
            {
                Player player = GetPlayer();
                if (player != null)
                {
                    player.FreezeCameraMovement = wasFrozen;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Restoring the tutorial UI camera baseline threw: " + ex);
            }
        }

        private static void ResetObserver()
        {
            _committedSignature = null;
            _pendingSignature = null;
            _guidedFocusSignature = null;
            _framesRemaining = 0;
        }

        private static void Speak(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            Plugin.Log.LogInfo("[Tutorial] " + message);
            ScreenReader.Speak(message);
        }

        private static void SpeakState(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            _lastStateAnnouncementFrame = Time.frameCount;
            // GuideFocus runs immediately before this call. The resulting focus/window/game-bar
            // changes can emit foreground speech later in the same frame, which used to cut the
            // tutorial sentence off. Keep the newest tutorial state and let the coordinator speak
            // it after that foreground burst has settled; Shift+F3 can then repeat it as well.
            Plugin.Log.LogInfo("[Tutorial] " + message);
            AnnouncementCoordinator.PublishLatestImportant("tutorial", message);
        }

        private sealed class TutorialState
        {
            internal TutorialState(string id, string message)
            {
                Id = id;
                Message = message;
            }

            internal string Id { get; }
            internal string Message { get; }
        }
    }
}
