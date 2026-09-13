using System;
using System.Collections.Generic;
using ParalivesAccess.Speech;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace ParalivesAccess.Handlers
{
    /// <summary>
    /// Makes the Name Details window usable: walks its fields and toggles, and types into them.
    /// </summary>
    /// <remarks>
    /// THE LARGEST REMAINING GAP IN THE CHARACTER CREATOR, and a Confirm blocker.
    /// `CheckHouseholdValidity` refuses to finish a household when a character has `HasFirstName`
    /// with an empty `FirstName`, so a name that cannot be typed is a household that cannot be
    /// finished (game-api.md section 31).
    ///
    /// `UICharacterName` holds four `TMP_InputField`s and five Unity `Toggle`s and **not one of them
    /// is a `Focusable`** - which is exactly why the window reports a single focusable,
    /// `ButtonClose`. No amount of focus navigation can reach them, so the ordering has to be the
    /// mod's; see VirtualPanel.
    ///
    /// TYPING MODE IS THE GAME'S OWN SWITCH, not a mod invention.
    /// `HybridPlayer.IsCursorInsideInputField()` returns true when
    /// `EventSystem.current.currentSelectedGameObject` carries a `TMP_InputField` or a `Toggle`. So
    /// section 17's recipe - Select() then ActivateInputField() - flips `HasInputFieldFocused` as a
    /// side effect, and everything already stands down: this handler, MenuNavigationHandler.Tick,
    /// ValueAdjustHandler, and the game's own UpdateFreeCamera, UpdateKeyboardShortcuts,
    /// UpdateUndoRedo and UpdateGameUI. Leaving the field clears it again. No new suppression logic
    /// is needed anywhere.
    ///
    /// That same clause is a trap for the toggles: selecting a `Toggle` would ALSO suppress
    /// navigation, so toggles are driven without Select().
    /// </remarks>
    public static class NameEntryHandler
    {
        /// <summary>Set false to disable the Name Details panel entirely.</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>
        /// True while this handler owns the keyboard, so other handlers must not act on it.
        /// </summary>
        /// <remarks>
        /// TEST 33 measured both handlers acting on ONE Enter press: MenuNavigationHandler ran
        /// ClickDown on the still-focused `ButtonEditNameDetails` while this handler activated the
        /// first field. `ButtonEditNameDetails` is wired to `ToggleEditNameDetails`, whose body is
        /// "if visible Hide() else Show()" - so the release half of that press would have CLOSED the
        /// window the user had just opened. It survived only by timing: selecting the field set
        /// `HasInputFieldFocused` before the release frame, and MenuNavigationHandler returns early
        /// on that flag, so the matching ClickUp never ran. It also left `Focusable.IsPressed` true
        /// with no click to clear it.
        ///
        /// Relying on that timing is not a design. Name Details is a modal window - while it is up,
        /// this handler owns navigation and activation, and Escape closes it through the game's own
        /// back button (it is registered for it).
        /// </remarks>
        public static bool OwnsInput
        {
            get { return _panel != null && _window != null && _window.IsVisible; }
        }

        private static VirtualPanel _panel;
        private static UICharacterName _window;
        private static bool _wasTyping;
        private static FieldEntry _editing;

        /// <summary>
        /// Frames to wait after the window appears before reading its controls.
        /// </summary>
        /// <remarks>
        /// The same settle every other handler here needs, for the reason in game-api.md section 32:
        /// the mod ticks from a postfix on SystemManager.Update, while `UIContainer.LateUpdate`
        /// applies `Content.SetActive` and `UICharacterName.UpdateUIForPlayer` sets each field's
        /// active state. Reading on the first visible frame would decide which fields are available
        /// from state the game has not written yet, and silently drop whichever ones were still
        /// inactive.
        /// </remarks>
        private const int SettleFrames = 3;

        private static int _framesUntilBuild = -1;

        /// <summary>Whether the active field has been observed genuinely in edit mode.</summary>
        private static bool _editConfirmed;

        private static int _editFrames;

        /// <summary>
        /// Frames to allow for <c>ActivateInputField</c> to take effect before giving up on it.
        /// </summary>
        /// <remarks>
        /// §17: activation is deferred to the field's own LateUpdate, so `isFocused` lags by a frame
        /// or more. Generous, because the cost of being too short is cancelling an edit the user just
        /// started, while the cost of being too long is a brief delay before a failed activation is
        /// released.
        /// </remarks>
        private const int EditActivationGraceFrames = 20;

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

                Player player = hybridPlayer != null ? hybridPlayer.Player : null;
                if (player == null || UI.Instance == null)
                {
                    Reset();
                    return;
                }

                UICharacterCreator creator = UI.GetOrNull<UICharacterCreator>(player.PlayerIndex);
                UICharacterName window = creator != null ? creator.UICharacterName : null;

                if (window == null || !window.IsVisible)
                {
                    Reset();
                    return;
                }

                if (!ReferenceEquals(window, _window))
                {
                    _window = window;
                    _panel = null;
                    _framesUntilBuild = SettleFrames;
                    return;
                }

                if (_framesUntilBuild >= 0)
                {
                    if (_framesUntilBuild-- > 0)
                    {
                        return;
                    }

                    _panel = Build(window);
                    _panel.Enter();
                    return;
                }

                if (_panel == null)
                {
                    return;
                }

                ReleaseFinishedEdit();

                // The game owns the keyboard while a field is active. Watch the flag fall so the
                // edit can be committed and the field re-announced with what was typed.
                if (hybridPlayer.HasInputFieldFocused)
                {
                    _wasTyping = true;
                    return;
                }

                if (_wasTyping)
                {
                    _wasTyping = false;
                    CommitEdit();
                    _panel.AnnounceCurrent();
                    return;
                }

                HandleInput(hybridPlayer);
            }
            catch (Exception ex)
            {
                // Runs inside the game's loop; contain it rather than let it escape into game code.
                Enabled = false;
                Plugin.Log.LogError("NameEntryHandler disabled after an exception: " + ex);
            }
        }

        private static void Reset()
        {
            _window = null;
            _panel = null;
            _wasTyping = false;
            _editing = null;
            _editConfirmed = false;
            _editFrames = 0;
            _framesUntilBuild = -1;
        }

        /// <summary>
        /// Clears the EventSystem selection once an edit has finished, so typing mode actually ends.
        /// </summary>
        /// <remarks>
        /// THE BUG THAT KILLED THE WHOLE PANEL, reported from play: *"when in an edit field, nothing
        /// gets you out except clicking elsewhere"*, and *"the close button doesn't close Name
        /// Details"*. **Those were one bug, not two.**
        ///
        /// `TMP_InputField.DeactivateInputField(bool clearSelection = false)` (§17) ends the edit but
        /// leaves the field EventSystem-SELECTED, and
        /// `HybridPlayer.IsCursorInsideInputField()` only asks whether the selected object *has* a
        /// `TMP_InputField` — not whether it is still being edited. So after pressing Enter the flag
        /// latched on permanently, `Tick` returned at its `HasInputFieldFocused` guard on every
        /// later frame, and `HandleInput` never ran again. Movement, activation and Close were all
        /// dead; only a mouse click elsewhere moved the selection and freed it.
        ///
        /// `isFocused` is the authoritative "is this field actually being edited" test, and it is the
        /// game's own — `UICharacterName.UpdateUIForPlayer` guards every field refresh with it.
        ///
        /// The grace period matters: `ActivateInputField` only sets `m_ShouldActivateNextUpdate`, so
        /// `isFocused` is still false for a frame or so afterwards (§17). Releasing on that would
        /// cancel the edit the instant it began. So the release waits until editing has been
        /// OBSERVED true — and, if it never becomes true, until the grace frames run out, because a
        /// failed activation must not strand the user either.
        /// </remarks>
        private static void ReleaseFinishedEdit()
        {
            if (_editing == null)
            {
                return;
            }

            // External game API on a pooled UI object; never let this break the panel.
            try
            {
                if (_editing.IsEditing)
                {
                    _editConfirmed = true;
                    return;
                }

                if (!_editConfirmed && ++_editFrames <= EditActivationGraceFrames)
                {
                    return;
                }

                _editing.Release();
                _editConfirmed = false;
                _editFrames = 0;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Releasing a finished edit threw: " + ex);
                _editing = null;
            }
        }

        /// <summary>
        /// Applies a finished text edit when the prefab did not wire the field to do it.
        /// </summary>
        /// <remarks>
        /// Only fires when NEITHER `onEndEdit` NOR `onValueChanged` carries a persistent listener.
        /// Checking one alone is not enough: the prefab could apply the name per keystroke through
        /// `onValueChanged` and leave `onEndEdit` empty, and a mod that only inspected `onEndEdit`
        /// would then call the handler a second time. That is near-invisible - the assignment is
        /// idempotent, so the NAME would be right - but every call also does
        /// `UndoRedoManager.Instance.AddAction(...)`, so one edit would need two presses of Ctrl+Z.
        /// </remarks>
        private static void CommitEdit()
        {
            FieldEntry field = _editing;
            _editing = null;
            _editConfirmed = false;
            _editFrames = 0;

            if (field == null || _window == null)
            {
                return;
            }

            // External game API: a changed handler signature must not break leaving a field.
            try
            {
                if (field.GameAppliesTheEdit)
                {
                    return;
                }

                field.InvokeHandler(_window);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Committing a name edit threw: " + ex);
            }
        }

        /// <summary>
        /// Reads movement and activation, using the game's own repeat flags.
        /// </summary>
        /// <remarks>
        /// `IntervalPressed` rather than raw key state, matching FocusableGroup.NavigateMenuArrows,
        /// so held keys repeat at the game's cadence instead of the mod inventing a second one.
        /// </remarks>
        private static void HandleInput(HybridPlayer hybridPlayer)
        {
            // LEFT AND RIGHT MOVE TOO. This is a flat vertical list with nothing horizontal in it, so
            // there is no second axis for them to mean. TEST 34 measured the cost of ignoring them:
            // MenuNavigationHandler refused 66 directional presses to this handler while the panel
            // announced far fewer moves, because every left/right press fell through both branches
            // and did nothing at all - a silent dead key, which reads exactly like a broken mod.
            // The window is itself a FocusableGroup (UIWindow : UIContainer : FocusableGroup), and
            // ReadDirection only needs one for the game's own GetFourDirectionsInputVector2 helper -
            // so the analogue stick folds to four directions exactly as it does everywhere else.
            Vector2 direction = FocusNavigator.ReadDirection(_window, hybridPlayer);

            if (direction.y < 0f || direction.x > 0f)
            {
                _panel.Move(1);
                return;
            }

            if (direction.y > 0f || direction.x < 0f)
            {
                _panel.Move(-1);
                return;
            }

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null
                && (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame))
            {
                _panel.Activate();
            }
        }

        /// <summary>
        /// Builds the panel in reading order: the name fields, then the options that govern them.
        /// </summary>
        /// <remarks>
        /// Fields come before their toggles deliberately. The toggles GATE the fields -
        /// `UpdateUIForPlayer` does `LabelFirstName.gameObject.SetActive(HasFirstName)` - so a user
        /// who finds an expected field missing meets its toggle immediately afterwards.
        /// </remarks>
        private static VirtualPanel Build(UICharacterName window)
        {
            var panel = new VirtualPanel(Loc.Get("name_panel"));

            panel.Add(new FieldEntry(window.LabelFirstName, "name_first", window));
            panel.Add(new FieldEntry(window.LabelLastName, "name_last", window));
            panel.Add(new FieldEntry(window.LabelMiddleName, "name_middle", window));
            panel.Add(new FieldEntry(window.LabelNickname, "name_nickname", window));

            panel.Add(new ToggleEntry(window.ToggleHasFirstName, "name_has_first", window));
            panel.Add(new ToggleEntry(window.ToggleHasLastName, "name_has_last", window));
            panel.Add(new ToggleEntry(window.ToggleHasMiddleName, "name_has_middle", window));
            panel.Add(new ToggleEntry(window.ToggleHasNickName, "name_has_nickname", window));
            panel.Add(new ToggleEntry(window.ToggleInvertFirstAndLastName, "name_invert", window));

            // Escape already closes this window - it is registered for the back button and
            // `Back.Update` drives `UIManager.PressBackButton()`. The entry exists anyway so the way
            // out is discoverable by walking the panel, rather than only by knowing a convention.
            panel.Add(new CloseEntry(window));

            return panel;
        }

        /// <summary>The window's own close action, as the last entry in the panel.</summary>
        private sealed class CloseEntry : VirtualPanel.IEntry
        {
            private readonly UICharacterName _window;

            public CloseEntry(UICharacterName window)
            {
                _window = window;
            }

            public bool IsAvailable => _window != null;

            public string Describe()
            {
                return Loc.Get("action_close");
            }

            /// <summary>
            /// Calls the game's own `Back()`, whose body is `Hide()` - the same thing Escape does.
            /// </summary>
            public void Activate()
            {
                _window.Back();
            }
        }

        /// <summary>
        /// Decides whether setting a control's value already runs the game's handler.
        /// </summary>
        /// <remarks>
        /// MEASURED, NOT ASSUMED. Whether `OnFirstNameChanged` / `OnToggleHasFirstNameClicked` are
        /// wired to the control's `onEndEdit` / `onValueChanged` lives in the prefab and is invisible
        /// to decompilation (lesson 10). But lesson 14 applies: `UnityEventBase` hands the wiring
        /// over at runtime, and section 29 already proved those APIs exist on this game's
        /// UnityEngine.CoreModule.dll.
        ///
        /// So: if the control's own event carries persistent listeners, setting the value invokes the
        /// game handler and the mod must NOT call it again. If it carries none, the mod calls the
        /// public handler itself.
        ///
        /// Getting this wrong is nearly invisible, which is why it is measured. Both handlers assign
        /// idempotently, so the DATA would be correct either way - but each call also does
        /// `UndoRedoManager.Instance.AddAction(...)`, so a double invoke means one edit needs two
        /// presses of Ctrl+Z. Logged once per control so a single run documents the wiring.
        /// </remarks>
        private static bool EventIsWired(UnityEventBase evt, string what)
        {
            // Reflection-adjacent Unity API on game objects; must degrade, never throw.
            try
            {
                if (evt == null)
                {
                    return false;
                }

                int count = evt.GetPersistentEventCount();
                bool wired = false;

                for (int i = 0; i < count && !wired; i++)
                {
                    wired = evt.GetPersistentTarget(i) != null
                         && !string.IsNullOrEmpty(evt.GetPersistentMethodName(i));
                }

                if (_reportedWiring.Add(what))
                {
                    Plugin.Log.LogInfo(
                        $"[Label] '{what}' event has {count} persistent listener(s), wired={wired}. "
                      + (wired
                            ? "Setting the value invokes the game's handler; the mod will not call it again."
                            : "The mod will call the game's public handler directly."));
                }

                return wired;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Reading the '{what}' event wiring threw: {ex}");
                return false;
            }
        }

        private static readonly HashSet<string> _reportedWiring = new HashSet<string>();

        /// <summary>One of the four name text fields.</summary>
        private sealed class FieldEntry : VirtualPanel.IEntry
        {
            private readonly TMP_InputField _field;
            private readonly string _labelKey;
            private readonly UICharacterName _window;

            public FieldEntry(TMP_InputField field, string labelKey, UICharacterName window)
            {
                _field = field;
                _labelKey = labelKey;
                _window = window;
            }

            /// <summary>
            /// The game hides a field when its "has" toggle is off, and a hidden control is not one a
            /// sighted player can reach either.
            /// </summary>
            public bool IsAvailable => _field != null && _field.gameObject.activeInHierarchy;

            /// <summary>
            /// Describes the field through the shared reader, so every route to it agrees.
            /// </summary>
            /// <remarks>
            /// ONE SOURCE OF TRUTH, deliberately. This used to compose its own string from a mod Loc
            /// key, which meant the panel said "Last name, empty" while the EventSystem announcement
            /// said "Last Name, ..." for the very same control - two answers depending on how the
            /// user arrived. Both now run `UiLabelReader.GetLabelOrFallback`, whose typed branch
            /// reads the field's own placeholder and contents.
            ///
            /// Using the game's placeholder rather than a mod-authored name is also the
            /// "read, never recompute" call: the placeholder IS the label printed on screen.
            /// </remarks>
            public string Describe()
            {
                string shared = UiLabelReader.GetLabelOrFallback(_field);
                if (!string.IsNullOrEmpty(shared))
                {
                    return shared;
                }

                // The prefab gave the field no placeholder to name it with; fall back to the mod's
                // own wording rather than announcing nothing.
                string value = _field.text;
                return string.IsNullOrEmpty(value)
                    ? Loc.Get("name_field_empty", Loc.Get(_labelKey))
                    : Loc.Get("name_field", Loc.Get(_labelKey), value);
            }

            /// <summary>
            /// Starts typing, using the recipe in game-api.md section 17.
            /// </summary>
            /// <remarks>
            /// Select() moves EventSystem selection onto the field, which is what flips
            /// `HasInputFieldFocused` and makes every other system stand down.
            /// ActivateInputField() opens the caret. The caret is placed at the END so typing appends
            /// to an existing name rather than silently replacing it.
            ///
            /// Leaving is the field's own business: TMP_InputField handles Enter and Escape and
            /// fires onEndEdit, at which point the game's OnFirstNameChanged runs and the handler's
            /// Tick sees HasInputFieldFocused fall and re-announces.
            /// </remarks>
            public void Activate()
            {
                ScreenReader.Speak(Loc.Get("name_editing", Loc.Get(_labelKey)));

                // Measured BEFORE selecting, so the log records the wiring even if the recipe below
                // fails. Both events are checked - see CommitEdit for why one is not enough.
                GameAppliesTheEdit =
                    EventIsWired(_field.onEndEdit, _labelKey + ".onEndEdit")
                    | EventIsWired(_field.onValueChanged, _labelKey + ".onValueChanged");

                _editing = this;
                _editConfirmed = false;
                _editFrames = 0;

                // lineType is prefab-serialized. If a name field were MultiLine, Enter would insert a
                // newline instead of finishing the edit, and the isFocused release below would never
                // trigger - so it is logged once rather than assumed to be SingleLine.
                if (_reportedWiring.Add(_labelKey + ".lineType"))
                {
                    Plugin.Log.LogInfo($"[Label] '{_labelKey}' lineType={_field.lineType}.");
                }

                _field.Select();
                _field.ActivateInputField();
                _field.caretPosition = _field.text != null ? _field.text.Length : 0;

                // Select() moves the EventSystem selection synchronously and FocusHandler.Tick runs
                // later in this same frame, so without this the "Editing ..." line above would be
                // followed immediately by a bare re-reading of the same field.
                FocusHandler.SuppressSelectionAnnouncement(_field.gameObject);
            }

            /// <summary>True when the prefab already applies this field's edit to the character.</summary>
            public bool GameAppliesTheEdit { get; private set; }

            /// <summary>True while the field is genuinely in edit mode, per the game's own test.</summary>
            public bool IsEditing
            {
                get { return _field != null && _field.isFocused; }
            }

            /// <summary>
            /// Ends the edit AND clears the EventSystem selection, which is what actually drops
            /// <c>HasInputFieldFocused</c>.
            /// </summary>
            /// <remarks>
            /// `DeactivateInputField()` alone is not enough - its `clearSelection` parameter defaults
            /// to false (§17), which is precisely why the flag latched. The selection is cleared
            /// explicitly, and only if it is still this field, so a selection the user has already
            /// moved elsewhere is never stolen.
            /// </remarks>
            public void Release()
            {
                _field.DeactivateInputField();

                UnityEngine.EventSystems.EventSystem eventSystem =
                    UnityEngine.EventSystems.EventSystem.current;

                if (eventSystem != null
                    && ReferenceEquals(eventSystem.currentSelectedGameObject, _field.gameObject))
                {
                    eventSystem.SetSelectedGameObject(null);
                }
            }

            /// <summary>Calls the public handler matching this field.</summary>
            /// <remarks>
            /// Each handler reads the field's own `.text`, registers an undo action and calls
            /// `UICharacterCreator.SetCurrentCharacterName`, so invoking it is the whole commit.
            /// </remarks>
            public void InvokeHandler(UICharacterName window)
            {
                if (ReferenceEquals(_field, window.LabelFirstName)) window.OnFirstNameChanged();
                else if (ReferenceEquals(_field, window.LabelLastName)) window.OnLastNameChanged();
                else if (ReferenceEquals(_field, window.LabelMiddleName)) window.OnMiddleNameChanged();
                else if (ReferenceEquals(_field, window.LabelNickname)) window.OnNickNameChanged();
            }
        }

        /// <summary>One of the five name option toggles.</summary>
        private sealed class ToggleEntry : VirtualPanel.IEntry
        {
            private readonly Toggle _toggle;
            private readonly string _labelKey;
            private readonly UICharacterName _window;

            public ToggleEntry(Toggle toggle, string labelKey, UICharacterName window)
            {
                _toggle = toggle;
                _labelKey = labelKey;
                _window = window;
            }

            public bool IsAvailable => _toggle != null && _toggle.gameObject.activeInHierarchy;

            public string Describe()
            {
                string state = _toggle.isOn
                    ? Loc.Get("state_selected")
                    : Loc.Get("state_not_selected");

                return Loc.Get("label_with_state", Loc.Get(_labelKey), state);
            }

            /// <summary>
            /// Flips the toggle WITHOUT selecting it.
            /// </summary>
            /// <remarks>
            /// `IsCursorInsideInputField` treats a selected `Toggle` exactly like a selected input
            /// field, so calling Select() here would suppress all navigation until something else
            /// took selection - stranding the user on the toggle they just pressed.
            ///
            /// Setting `isOn` raises `onValueChanged`. Whether that already runs the game's handler
            /// is prefab state, so it is measured rather than assumed; see EventIsWired.
            /// </remarks>
            public void Activate()
            {
                // Measured before the write, so the log records the wiring even if the flip throws.
                bool wired = EventIsWired(_toggle.onValueChanged, _labelKey + ".onValueChanged");

                _toggle.isOn = !_toggle.isOn;

                if (!wired)
                {
                    InvokeHandler();
                }

                // The game rebuilds the window from UpdateUIForPlayer, so the field this toggle
                // governs appears or disappears on a later frame. Announcing the toggle's own new
                // state immediately is still correct and is what the user pressed for.
                ScreenReader.Speak(Describe());
            }

            /// <summary>Calls the public handler matching this toggle.</summary>
            private void InvokeHandler()
            {
                if (ReferenceEquals(_toggle, _window.ToggleHasFirstName)) _window.OnToggleHasFirstNameClicked();
                else if (ReferenceEquals(_toggle, _window.ToggleHasLastName)) _window.OnToggleHasLastNameClicked();
                else if (ReferenceEquals(_toggle, _window.ToggleHasMiddleName)) _window.OnToggleHasMiddleNameClicked();
                else if (ReferenceEquals(_toggle, _window.ToggleHasNickName)) _window.OnToggleHasNickNameClicked();
                else if (ReferenceEquals(_toggle, _window.ToggleInvertFirstAndLastName)) _window.OnToggleInvertNamesClicked();
            }
        }
    }
}
