using System;
using ParalivesAccess.Speech;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ParalivesAccess.Handlers
{
    /// <summary>
    /// Changes the value of the focused character-creator control with the '7' and '8' keys.
    /// </summary>
    /// <remarks>
    /// WHY A SEPARATE ADJUST AXIS
    ///
    /// The user chose "left/right adjusts, up/down moves" for the guided flow (2026-08-10). That
    /// cannot be applied in ordinary panel navigation yet, because the four personality stats sit
    /// SIDE BY SIDE horizontally (measured at x = -821, -695, -569, -443), so left and right already
    /// mean "move to the next stat". Binding them to adjust would make the stats unreachable.
    ///
    /// So the separation of *move* from *adjust* is kept, on its own axis. When the flow lands it can
    /// map left/right to these same two methods.
    ///
    /// The native creator camera also consumes minus/equals through HybridPlayer.Zoom.
    /// Number-row 7/8 are unbound defaults (game-api.md section 3), keeping value adjustment
    /// separate from zoom. Adjustment remains scoped to CharacterCreator.
    ///
    /// WHY THIS EXISTS AT ALL - a live defect found by self-check, never reported from play.
    /// Enter on a personality gauge does nothing. `UIProfileTraitStatsItemJauge : UIListItemBase`
    /// does not override OnListItemClicked, so the mod's activation ends at
    /// `PerformClickActions() -> ParaButton.ButtonClick.Invoke()`, and TEST 31's log proves that
    /// event is empty: the gauge was label-read - which runs GetActionLabel -> ReportWiring - and
    /// produced no [Label] line, and ReportWiring returns early only on an empty listener list. The
    /// gauge's real action hangs off its own Unity `Button.onClick`, raised from
    /// IPointerClickHandler, which the mod's direct ClickDown/ClickUp calls never touch.
    ///
    /// So personality points could not be set at all - and unspent points are one of the four things
    /// `CheckHouseholdValidity` refuses to finish a household over.
    /// </remarks>
    public static class ValueAdjustHandler
    {
        /// <summary>Set false to disable value adjustment entirely.</summary>
        public static bool Enabled { get; set; } = true;

        private static UIClassicSliderItem _pendingHeightSlider;
        private static UICharacterCreatorDiagramHandleItem _pendingHeightHandleItem;
        private static BodyDiagramHandle.Resolved _pendingHeightHandle;
        private static int _pendingHeightReportFrames;

        /// <summary>Called once per frame from the game loop hook.</summary>
        public static void Tick()
        {
            if (!Enabled)
            {
                return;
            }

            try
            {
                HybridPlayer hybridPlayer = PlayerManager.Instance != null
                    ? PlayerManager.Instance.HybridPlayer1
                    : null;
                if (hybridPlayer?.Player == null
                    || hybridPlayer.Player.State != GameStates.CharacterCreator)
                {
                    _pendingHeightSlider = null;
                    _pendingHeightHandleItem = null;
                    _pendingHeightHandle = null;
                    _pendingHeightReportFrames = 0;
                    return;
                }

                ReportSettledHeight();

                Keyboard keyboard = Keyboard.current;
                if (keyboard == null)
                {
                    return;
                }

                bool up = keyboard.digit8Key.wasPressedThisFrame;
                bool down = keyboard.digit7Key.wasPressedThisFrame;

                // SECOND AXIS on '9' and '0'. A body-diagram handle is two-dimensional and one key
                // pair cannot express that (user decision, 2026-08-11: two key pairs, no modes).
                //
                // '9' and '0' sit immediately beside '7' and '8' on the number row, so the four keys
                // form one physical group. The obvious '[' and ']' are NOT free - the game binds both
                // brackets - and the numpad was rejected deliberately: NVDA uses it heavily for the
                // review cursor, so it is the worst place to put mod keys for this user.
                bool secondaryUp = keyboard.digit0Key.wasPressedThisFrame;
                bool secondaryDown = keyboard.digit9Key.wasPressedThisFrame;

                if (!up && !down && !secondaryUp && !secondaryDown)
                {
                    return;
                }

                // The user is typing; every mod key must stay out of the way.
                if (hybridPlayer.HasInputFieldFocused || SettingsHandler.OwnsEditInput)
                {
                    return;
                }

                // The camera tutorial owns its lesson; a stale UI cursor must not adjust a Para
                // or add "Nothing to adjust here" over the tutorial instruction.
                if (CameraTutorialManager.Instance.CurrentTutorialStep != TutorialSteps.None)
                {
                    return;
                }

                // A modal panel the mod owns is up, and nothing in it is adjustable. Without this,
                // pressing '7' while walking Name Details would announce "Nothing to adjust here"
                // over a panel that never has anything to adjust.
                if (NameEntryHandler.OwnsInput || TownMapLotHandler.OwnsInput)
                {
                    return;
                }

                Focusable focused = MenuNavigationHandler.Cursor;
                if (focused == null)
                {
                    return;
                }

                var stat = focused.GetComponent<UIProfileTraitStatsItem>();
                if (stat != null)
                {
                    AdjustStat(stat, up);
                    return;
                }

                var handle = focused.GetComponent<UICharacterCreatorDiagramHandleItem>();
                if (handle != null)
                {
                    AdjustHandle(handle, up || secondaryUp, secondaryUp || secondaryDown);
                    return;
                }

                // The second pair means nothing anywhere else; say so rather than moving the primary
                // axis by surprise.
                if (secondaryUp || secondaryDown)
                {
                    Plugin.Log.LogInfo(
                        $"[Adjust] second-axis key pressed on '{focused.name}', which has one value.");
                    ScreenReader.Speak(Loc.Get("adjust_no_second_axis"));
                    return;
                }

                var slider = focused.GetComponent<UIClassicSliderItem>();
                if (slider != null)
                {
                    AdjustSlider(slider, up);
                    return;
                }

                if (SettingsHandler.TryAdjustValue(focused, up)) return;

                // Nothing here is adjustable. Silence would be indistinguishable from a dead key, so
                // say so once per press rather than leaving the user guessing.
                //
                // LOGGED, not just spoken. TEST 38 could not be diagnosed from its log: sliders were
                // reported as failing, and nothing recorded whether a key had even been pressed or
                // what the cursor was on when it was. An announcement the user hears but the log
                // does not is a blind spot in exactly the situation the log exists for.
                Plugin.Log.LogInfo(
                    $"[Adjust] '{(up ? "8" : "7")}' pressed with nothing adjustable focused: "
                  + $"'{focused.name}' ({focused.GetType().Name}).");

                ScreenReader.Speak(Loc.Get("adjust_not_adjustable"));
            }
            catch (Exception ex)
            {
                // Runs inside the game's loop; contain it rather than let it escape into game code.
                Enabled = false;
                Plugin.Log.LogError("ValueAdjustHandler disabled after an exception: " + ex);
            }
        }

        /// <summary>
        /// Moves a personality stat one point up or down, through the game's own click handler.
        /// </summary>
        /// <remarks>
        /// USES OnClickJauge's OWN SEMANTICS rather than reimplementing them (verified in
        /// UIProfileTraitStatsItemJauge.OnClickJauge):
        ///
        ///  - Clicking the gauge whose PointNumber is ABOVE the current value adds points, clamped
        ///    to both the per-stat cap and the category's remaining budget.
        ///  - Clicking the gauge whose PointNumber EQUALS the current value subtracts exactly one.
        ///    That is also the only route to zero, since there is no gauge numbered 0.
        ///
        /// So increment targets `current + 1` and decrement targets `current`.
        ///
        /// Nothing is clamped here on purpose. When the budget is spent the game raises its own
        /// `UIProfileError_NoMorePoints` floating text and plays the denied sound, and v0.21.0's
        /// prompt reader speaks the game's refusals - so the reason reaches the user in the game's
        /// own words instead of the mod inventing one.
        ///
        /// `PersonalityManager.SetCharacterTraitStatPoints` is public and would be simpler, but it
        /// bypasses the points budget entirely - it would let the user overspend and then be refused
        /// at Confirm with no idea why.
        /// </remarks>
        /// <param name="stat">The stat row to change.</param>
        /// <param name="increase">True to add a point, false to remove one.</param>
        private static void AdjustStat(UIProfileTraitStatsItem stat, bool increase)
        {
            UIList gauges = stat.JaugeList;
            if (gauges == null || gauges.CurrentItems == null)
            {
                return;
            }

            UIProfileTraitStatsItemJauge sample = null;
            foreach (UIListItemBase entry in gauges.CurrentItems)
            {
                sample = entry as UIProfileTraitStatsItemJauge;
                if (sample != null)
                {
                    break;
                }
            }

            if (sample == null || sample.PersonalityTrait == null || sample.UIProfileTraitStats == null)
            {
                return;
            }

            UIProfile profile = sample.UIProfileTraitStats.UIProfile;
            if (profile == null || profile.Character == null)
            {
                return;
            }

            // The same flag the game uses to enable each gauge's Button. Editing is genuinely
            // blocked in some contexts, and saying so beats a keypress that appears to do nothing.
            if (!profile.AllowEditingPersonality)
            {
                ScreenReader.Speak(Loc.Get("adjust_locked"));
                return;
            }

            int current = PersonalityManager.Instance.GetCharacterTraitPoints(
                profile.Character, sample.PersonalityTrait.GUID);

            int target = increase ? current + 1 : current;

            UIProfileTraitStatsItemJauge gauge = FindGauge(gauges, target);
            if (gauge == null)
            {
                // Already at the top of the gauge, or already at zero. The game has no control for
                // this step, so there is nothing to drive.
                ScreenReader.Speak(Loc.Get(increase ? "adjust_at_max" : "adjust_at_min"));
                return;
            }

            gauge.OnClickJauge();

            // Re-read rather than assuming the click applied what was asked: the budget may have
            // clamped it, and announcing the request instead of the result is how a blind user ends
            // up believing a change happened that did not.
            int after = PersonalityManager.Instance.GetCharacterTraitPoints(
                profile.Character, sample.PersonalityTrait.GUID);

            if (after == current)
            {
                // THE GAME REFUSED, and it has already said why - OnClickJauge:104 raises
                // "UIProfileError_NoMorePoints" through UIFloatingText, which the mod now speaks.
                //
                // Reported from play: "if zero points left announcement fired, it was eaten by the
                // re-announcement of focus." Exactly right - ScreenReader.Speak interrupts by
                // default, so re-announcing here talked over the game's own explanation. Staying
                // silent leaves the better message standing.
                Plugin.Log.LogInfo(
                    $"[Adjust] {(increase ? "+" : "-")}1 refused on '{sample.PersonalityTrait.DisplayName}' "
                  + $"(still {after}); leaving the game's own message to speak.");
                return;
            }

            string label = UiLabelReader.GetLabelOrFallback(stat);
            Plugin.Log.LogInfo($"[Adjust] {(increase ? "+" : "-")}1 -> {label}");
            ScreenReader.Speak(label);
        }

        /// <summary>
        /// Moves a body/face slider, using the game's own "value typed into the box" handler.
        /// </summary>
        /// <remarks>
        /// USES THE GAME'S COMPLETE RECIPE rather than reproducing it. `UIClassicSliderItem` already
        /// has a public method for exactly this - a value arriving as a number rather than as a drag:
        ///
        ///   OnInputValueSet()  ->  parses InputField.text, range-checks 0..100,
        ///                          CharacterDeformationSliderManager.SetCharacterSlider(...),
        ///                          UndoRedoManager.AddAction(new SetSliderAction { Difference = ... })
        ///
        /// So the mod writes the number into the field the game reads and calls that. Going straight
        /// to `SetCharacterSlider` would move the slider but **register no undo action**, quietly
        /// breaking Ctrl+Z for every body edit - and that is precisely the kind of damage a blind
        /// user cannot see.
        ///
        /// Frame ordering is safe: `RefreshItemVisual` overwrites `InputField.text` from the
        /// character in LateUpdate, and this handler runs from a postfix on `SystemManager.Update`,
        /// i.e. in the Update phase - the write and the call happen before that overwrite.
        ///
        /// The step is the MOD'S choice, not the game's: the underlying value is continuous 0..1 and
        /// the input field accepts any integer 0..100, so there is no game-defined increment to read.
        /// Five gives twenty stops across the range - coarse enough to cross quickly, fine enough to
        /// place a value deliberately.
        /// </remarks>
        /// <param name="item">The slider row to change.</param>
        /// <param name="increase">True to raise the value, false to lower it.</param>
        private static void AdjustSlider(UIClassicSliderItem item, bool increase)
        {
            Setting.DeformationSlider slider = item.DeformationSlider;
            if (slider == null || item.AssetCharacter == null || item.InputField == null)
            {
                return;
            }

            float current = CharacterDeformationSliderManager.Instance
                .GetCharacterSliderValue(item.AssetCharacter, slider.GUID);

            int currentPercent = Mathf.RoundToInt(current * 100f);
            int target = Mathf.Clamp(currentPercent + (increase ? StepPercent : -StepPercent), 0, 100);

            if (target == currentPercent)
            {
                Plugin.Log.LogInfo($"[Adjust] slider already at {(increase ? "max" : "min")} ({currentPercent}).");
                ScreenReader.Speak(Loc.Get(increase ? "adjust_at_max" : "adjust_at_min"));
                return;
            }

            item.InputField.text = target.ToString(System.Globalization.CultureInfo.InvariantCulture);
            item.OnInputValueSet();

            ReportSliderValue(item);
        }

        /// <summary>Reports native slider results, including height's deferred metric refresh.</summary>
        internal static void ReportSliderValue(UIClassicSliderItem item)
        {
            string label = UiLabelReader.GetLabelOrFallback(item);
            Plugin.Log.LogInfo("[Adjust] " + label);
            if (UiLabelReader.IsHeightSlider(item))
            {
                // SetCharacterSlider resets CharacterHeight to -1 and UpdateCharacterHeight
                // computes the metric measurement asynchronously. Wait for that source-owned
                // value instead of announcing the previous height as the result of this keypress.
                _pendingHeightSlider = item;
                _pendingHeightHandleItem = null;
                _pendingHeightHandle = null;
                _pendingHeightReportFrames = 45;
                return;
            }

            ScreenReader.Speak(label);
        }

        private static void ReportSettledHeight()
        {
            UIClassicSliderItem sliderItem = _pendingHeightSlider;
            UICharacterCreatorDiagramHandleItem handleItem = _pendingHeightHandleItem;
            BodyDiagramHandle.Resolved handle = _pendingHeightHandle;
            if (sliderItem == null && (handleItem == null || handle == null))
            {
                return;
            }

            AssetCharacter character = sliderItem != null ? sliderItem.AssetCharacter : handle.Character;
            bool sourceActive = sliderItem != null
                ? sliderItem.gameObject.activeInHierarchy
                : handleItem.gameObject.activeInHierarchy;
            if (!sourceActive || character?.Visual?.Data == null)
            {
                ClearPendingHeightReport();
                return;
            }

            float height = character.Visual.Data.CharacterHeight;
            _pendingHeightReportFrames--;
            if (height <= 0f && _pendingHeightReportFrames > 0)
            {
                return;
            }

            string label = sliderItem != null
                ? UiLabelReader.GetLabelOrFallback(sliderItem)
                : UiLabelReader.GetHeightValueLabel(
                    character,
                    handle.Name,
                    BodyDiagramHandle.ReadPercent(handle, handle.Primary));
            Plugin.Log.LogInfo(
                $"[Adjust] settled height={height:0.###}, remainingFrames="
              + $"{_pendingHeightReportFrames}: {label}");
            ScreenReader.Speak(label);
            ClearPendingHeightReport();
        }

        private static void ClearPendingHeightReport()
        {
            _pendingHeightSlider = null;
            _pendingHeightHandleItem = null;
            _pendingHeightHandle = null;
            _pendingHeightReportFrames = 0;
        }

        /// <summary>
        /// Moves one axis of a body-diagram handle.
        /// </summary>
        /// <remarks>
        /// These handles are the real body editor - `UICharacterCreatorBodyPartDiagram` holds one
        /// classic slider row and 44 of these - and until now they were reachable, correctly named,
        /// and completely un-editable, because they are 2D drag targets with no click behaviour.
        ///
        /// Writing goes through `BodyDiagramHandle.WritePercent`, which mirrors the game's own
        /// `OnPointerUp`: every deformation slider on the axis is set, and they share one undo
        /// `ActionID` so the whole keypress undoes in a single Ctrl+Z.
        /// </remarks>
        /// <param name="item">The focused handle.</param>
        /// <param name="increase">True to raise the value.</param>
        /// <param name="useSecondary">True when the second key pair was used.</param>
        private static void AdjustHandle(
            UICharacterCreatorDiagramHandleItem item, bool increase, bool useSecondary)
        {
            BodyDiagramHandle.Resolved handle = BodyDiagramHandle.Resolve(item);
            if (handle == null)
            {
                ScreenReader.Speak(Loc.Get("adjust_not_adjustable"));
                return;
            }

            BodyDiagramHandle.Axis axis = useSecondary ? handle.Secondary : handle.Primary;
            if (axis == null)
            {
                Plugin.Log.LogInfo($"[Adjust] '{handle.Name}' has no second axis.");
                ScreenReader.Speak(Loc.Get("adjust_no_second_axis"));
                return;
            }

            int current = BodyDiagramHandle.ReadPercent(handle, axis);
            int target = Mathf.Clamp(current + (increase ? StepPercent : -StepPercent), 0, 100);

            if (target == current)
            {
                Plugin.Log.LogInfo($"[Adjust] '{handle.Name}' {axis.Name} already at {current}.");
                ScreenReader.Speak(Loc.Get(increase ? "adjust_at_max" : "adjust_at_min"));
                return;
            }

            BodyDiagramHandle.WritePercent(handle, axis, target);

            string label = UiLabelReader.GetLabelOrFallback(item);
            Plugin.Log.LogInfo($"[Adjust] '{handle.Name}' {axis.Name} {current} -> {target} : {label}");
            if (BodyDiagramHandle.IsHeight(handle))
            {
                // Height is recomputed after the underlying deformation slider changes. The classic
                // height row already waits for that calculation; diagram-handle edits must use the
                // same source and timing instead of immediately falling back to a bare percentage.
                _pendingHeightSlider = null;
                _pendingHeightHandleItem = item;
                _pendingHeightHandle = handle;
                _pendingHeightReportFrames = 45;
                return;
            }

            ScreenReader.Speak(label);
        }

        /// <summary>
        /// How far one press moves a continuous slider, in percent.
        /// </summary>
        /// <remarks>
        /// A mod choice, not a game value - see AdjustSlider. Invariant culture on the write, because
        /// the field is parsed with float.Parse and a comma decimal separator would break it.
        /// </remarks>
        private const int StepPercent = 5;

        /// <summary>Finds the gauge segment carrying a given point number.</summary>
        private static UIProfileTraitStatsItemJauge FindGauge(UIList gauges, int pointNumber)
        {
            foreach (UIListItemBase entry in gauges.CurrentItems)
            {
                var gauge = entry as UIProfileTraitStatsItemJauge;
                if (gauge != null && gauge.PointNumber == pointNumber)
                {
                    return gauge;
                }
            }

            return null;
        }
    }
}
