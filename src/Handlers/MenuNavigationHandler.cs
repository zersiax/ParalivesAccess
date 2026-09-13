using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ParalivesAccess.Speech;
using Setting;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace ParalivesAccess.Handlers
{
    /// <summary>
    /// Supplies keyboard menu navigation, panel switching and activation on the screens where the
    /// game provides none, by driving the game's own <c>FocusableGroup</c> API.
    /// </summary>
    /// <remarks>
    /// WHY THIS IS NEEDED - all measured, see docs/game-api.md section 4 and project_status.md
    /// TESTS 9-11.
    ///
    /// The game's chain is
    ///   Back.Update -> UIManager.NavigateMenu -> CurrentWindow.NavigateMenuArrows -> FocusableGroup
    /// and it works for keyboard as well as gamepad wherever it is reachable. It is unreachable
    /// almost everywhere, for two independent reasons:
    ///  1. NavigateMenu early-returns on CurrentWindow == null, and CurrentWindow only ever holds
    ///     windows registered for the back button. UIMainMenu is not one (measured).
    ///  2. The game navigates exactly ONE group - the window's own. Focusables register with
    ///     GetComponentInParent&lt;FocusableGroup&gt;(), the NEAREST ancestor, and UIContainer is itself
    ///     a FocusableGroup, so nested panels are unreachable by design. UIMainMenu reports 0
    ///     focusables while its buttons sit on a nested UIContainer 'MainPanel' (8, measured), and
    ///     UIOptions owns both category and settings focusables; UIInspector is not a focus group.
    ///
    /// Three additions. Navigation and activation are additive; only the suppression below changes
    /// game behaviour, and it does so narrowly.
    ///  - Navigation: the game's own NavigateMenuArrows/Shoulders, called on the active panel.
    ///  - Activation: Enter, using the same ClickDown/ClickUp the game uses for the gamepad.
    ///    NavigateMenuDoClick is gamepad-gated, so keyboard users get no activation without this.
    ///  - Panel switching: ';' and ''' move between the panels on the current screen. Nothing is
    ///    hidden or skipped - every group holding a Focusable is reachable.
    /// </remarks>
    public static class MenuNavigationHandler
    {
        /// <summary>Set false to disable mod-supplied navigation entirely.</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>The panel the user selected with the panel keys, if any.</summary>
        private static FocusableGroup _selectedGroup;

        /// <summary>
        /// True when <see cref="_selectedGroup"/> was chosen by the user rather than defaulted to.
        /// </summary>
        /// <remarks>
        /// Without this, an automatic fallback becomes permanent. Measured in TEST 13: opening
        /// Settings hides MainPanel (UIMainMenu.Update sets
        /// MainPanel.IsVisible = !HasVisibleWindowRegisteredForBackButton), so the only candidate
        /// left is UIGameBar and the selection moves there. Closing Settings brings MainPanel back,
        /// but UIGameBar is still a valid candidate, so the selection stuck to the overlay bar and
        /// the user had to press a panel key on every return to the main menu. A defaulted
        /// selection is now re-derived every frame; only an explicit choice persists.
        /// </remarks>
        private static bool _selectionIsExplicit;

        /// <summary>
        /// True when the tutorial observer guided focus to a newly revealed ordinary control.
        /// </summary>
        /// <remarks>
        /// Kept separate from <see cref="_selectionIsExplicit"/> so a tutorial transition may
        /// release only its own focus without undoing a panel the user deliberately selected.
        /// </remarks>
        private static bool _selectionIsTutorialDirected;

        /// <summary>
        /// True when a newly opened foreground window deliberately took navigation focus.
        /// </summary>
        /// <remarks>
        /// This is separate from a user selection and from tutorial guidance. A prompt or other
        /// foreground window must temporarily displace a panel behind it, then restore that panel
        /// when the foreground window closes.
        /// </remarks>
        private static bool _selectionIsForegroundDirected;

        private sealed class SuspendedSelection
        {
            public FocusableGroup Group;
            public bool IsExplicit;
            public bool IsTutorialDirected;
            public bool IsForegroundDirected;
        }

        private static readonly Stack<SuspendedSelection> _suspendedSelections =
            new Stack<SuspendedSelection>();

        /// <summary>The panel actually being driven, read by the suppression prefix.</summary>
        private static FocusableGroup _activeGroup;

        /// <summary>The panel currently being navigated, or null. Read-only view for reporting.</summary>
        public static FocusableGroup ActivePanel => _activeGroup;

        /// <summary>
        /// True while either the user or the tutorial has deliberately handed arrows to a panel.
        /// </summary>
        internal static bool HasUiPanelSelection =>
            (_selectionIsExplicit || _selectionIsTutorialDirected || _selectionIsForegroundDirected)
            && _selectedGroup != null;

        private static readonly List<FocusableGroup> _candidates = new List<FocusableGroup>();
        private static readonly List<UIWindow> _windowBuffer = new List<UIWindow>();

        /// <summary>
        /// The panels the mod currently considers reachable, in panel-key order. Read-only.
        /// </summary>
        /// <remarks>
        /// A live view over the candidate list rather than a copy, so reading it costs nothing and
        /// cannot go stale. Exposed for diagnostics: "is this group in the panel list" is a question
        /// a probe must be able to ask directly, instead of re-deriving BuildCandidates' rules and
        /// risking a diagnostic that disagrees with the code it is measuring.
        /// </remarks>
        public static IList<FocusableGroup> Panels { get; } = _candidates.AsReadOnly();

        private static FieldInfo _visibleWindowsField;
        private static FieldInfo _focusablesField;
        private static FieldInfo _isHoveredField;
        private static FieldInfo _justEnabledField;
        private static FieldInfo _inputVectorSmoothValueField;
        private static bool _reflectionResolved;

        private static FocusableGroup _lastLoggedTarget;
        private static bool _loggedInputFieldBlock;

        /// <summary>
        /// A physical arrow-down edge captured before game systems can smooth its shared camera
        /// vector back to zero.
        /// </summary>
        private static Vector2 _pendingPhysicalArrowDirection;

        /// <summary>
        /// The game's CameraMovement interval reports the same physical arrow edge again on a later
        /// frame. Suppress exactly that echo; later intervals from a held key remain repeat input.
        /// </summary>
        private static bool _suppressNextPhysicalArrowInterval;

        /// <summary>
        /// True from a physical arrow edge until the shared CameraMovement smoothing tail reaches
        /// neutral. Only UpdateFreeCamera sees a neutral vector; UI navigation retains the action.
        /// </summary>
        private static bool _suppressArrowCameraMovement;

        /// <summary>A panel-key edge captured before game systems can consume it.</summary>
        private static int _pendingPanelDirection;

        private sealed class PendingActivationOutcome
        {
            public Focusable Target;
            public FocusableGroup Group;
            public UIWindow Window;
            public HashSet<FocusableGroup> VisibleGroupsBefore;
            public string LabelBefore;
            public int FramesRemaining;
            public int InteractionSubmenuDepth = -1;
            public ulong InteractionSubmenuGroupGuid;
            public UIGenericSkinChoices SkinChoices;
        }

        /// <summary>
        /// A short, bounded settle observation for an Enter activation. It reports an actual
        /// changed label, a newly revealed child panel, or the source-proven case where the user
        /// activated the exclusive tab which remains the current panel. Other unchanged clicks
        /// remain quiet.
        /// </summary>
        private static PendingActivationOutcome _pendingActivationOutcome;

        /// <summary>
        /// Applies the patch that stops the game navigating a window while the mod is driving a
        /// different panel on that same screen.
        /// </summary>
        /// <param name="harmony">Harmony instance owned by the plugin.</param>
        /// <returns>True when the patch was verified as applied.</returns>
        public static bool ApplyPatches(Harmony harmony)
        {
            try
            {
                // The ClickUp patch needs _isHoveredField, and it can run before the first Tick.
                ResolveReflection();

                MethodInfo target = AccessTools.Method(typeof(UIManager), "NavigateMenu");
                if (target == null)
                {
                    Plugin.Log.LogError(
                        "Could not find UIManager.NavigateMenu. Panel switching will double-navigate "
                      + "on game-driven windows - re-check docs/game-api.md section 4.");
                    return false;
                }

                harmony.Patch(
                    target,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(MenuNavigationHandler), nameof(NavigateMenuPrefix))));

                bool applied = new List<MethodBase>(harmony.GetPatchedMethods()).Contains(target);
                Plugin.Log.LogInfo($"Patched UIManager.NavigateMenu, applied={applied}.");

                MethodInfo clickUp = AccessTools.Method(typeof(Focusable), "ClickUp");
                if (clickUp == null)
                {
                    Plugin.Log.LogError(
                        "Could not find Focusable.ClickUp. Enter will silently fail to activate any "
                      + "element the mouse has passed over - re-check docs/game-api.md section 4.");
                    return false;
                }

                harmony.Patch(
                    clickUp,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(MenuNavigationHandler), nameof(ClickUpPrefix))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(MenuNavigationHandler), nameof(ClickUpPostfix))));

                bool clickUpApplied = new List<MethodBase>(harmony.GetPatchedMethods()).Contains(clickUp);
                Plugin.Log.LogInfo($"Patched Focusable.ClickUp, applied={clickUpApplied}.");

                // Arrow keys remain in CameraMovement because native menus consume that action,
                // but no longer drive the free camera. Only UpdateFreeCamera sees the neutralized
                // vector, including the post-release smoothing tail. Native UI, accessible build
                // placement and gamepad input retain their existing paths.
                MethodInfo freeCamera = AccessTools.Method(typeof(UpdateFreeCamera), "UpdateForPlayer");
                if (freeCamera == null)
                {
                    Plugin.Log.LogError(
                        "Could not find UpdateFreeCamera.UpdateForPlayer. Arrow keys may still move "
                      + "the free camera.");
                }
                else
                {
                    harmony.Patch(
                        freeCamera,
                        prefix: new HarmonyMethod(AccessTools.Method(
                            typeof(MenuNavigationHandler), nameof(FreeCameraPrefix))),
                        postfix: new HarmonyMethod(AccessTools.Method(
                            typeof(MenuNavigationHandler), nameof(FreeCameraPostfix))));

                    Plugin.Log.LogInfo(
                        "Patched UpdateFreeCamera.UpdateForPlayer, applied="
                      + $"{new List<MethodBase>(harmony.GetPatchedMethods()).Contains(freeCamera)}.");
                }

                // Diagnostic: confirms an activation actually reached the game. OnClick is overridden
                // at TWO levels and neither override calls base, so one patch is not enough:
                //   Focusable.OnClick -> ParaButton -> ParaToggle -> UIListItemBase
                // TEST 21 measured the consequence. Patching only ParaButton.OnClick made [Activate]
                // structurally blind to every UIListItemBase - character cards, swatches, catalog
                // items, skill rows, category buttons, outfit tabs - because UIListItemBase.OnClick
                // returns without ever reaching ParaButton's body. Two Enter presses looked like
                // failed activations when the truth was that the mod simply could not see them.
                // 'F, selected' still logged only because that focusable is the gender item's CHILD
                // ParaButton rather than the UICharacterCreatorGenderItem itself.
                PatchOnClick(harmony, typeof(ParaButton));
                PatchOnClick(harmony, typeof(UIListItemBase));

                return applied && clickUpApplied;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Patching UIManager.NavigateMenu threw: " + ex);
                return false;
            }
        }

        public struct CameraMovementState
        {
            /// <summary>Native zoom distance before the free-camera consumer, or NaN.</summary>
            public float ZoomDistance;
            public bool Applied;
            public InputVector2 Input;
            public Vector2 Value;
            public Vector2 SmoothValue;
        }

        /// <summary>
        /// Removes arrow-originated values only while the free-camera consumer runs.
        /// </summary>
        public static void FreeCameraPrefix(Player player, out CameraMovementState __state)
        {
            __state = default(CameraMovementState);
            __state.ZoomDistance = CameraZoomHandler.BeforeCameraUpdate(player);

            Keyboard keyboard = Keyboard.current;
            if (!Enabled || player == null || keyboard == null)
            {
                return;
            }

            bool arrowHeld = keyboard.leftArrowKey.isPressed
                || keyboard.rightArrowKey.isPressed
                || keyboard.upArrowKey.isPressed
                || keyboard.downArrowKey.isPressed;
            if (arrowHeld || _pendingPhysicalArrowDirection != Vector2.zero)
            {
                _suppressArrowCameraMovement = true;
            }

            // CameraMovement is one composite. If the remaining camera keys are deliberately held,
            // camera control wins rather than pretending the mod can separate two simultaneous
            // binding contributions. A later arrow-only edge starts suppression again.
            bool wasdHeld = keyboard.wKey.isPressed || keyboard.aKey.isPressed
                || keyboard.sKey.isPressed || keyboard.dKey.isPressed;
            if (wasdHeld)
            {
                _suppressArrowCameraMovement = false;
                return;
            }

            HybridPlayer hybridPlayer = PlayerManager.Instance != null
                ? PlayerManager.Instance.GetHybridPlayer(player.PlayerIndex)
                : null;
            InputVector2 input = hybridPlayer?.CameraMovement;
            if (!_suppressArrowCameraMovement || input == null)
            {
                return;
            }

            if (!arrowHeld && _pendingPhysicalArrowDirection == Vector2.zero
                && input.Value.sqrMagnitude <= 0.0001f
                && input.SmoothValue.sqrMagnitude <= 0.0001f)
            {
                _suppressArrowCameraMovement = false;
                return;
            }

            ResolveReflection();
            if (_inputVectorSmoothValueField == null)
            {
                return;
            }

            __state.Applied = true;
            __state.Input = input;
            __state.Value = input.Value;
            __state.SmoothValue = input.SmoothValue;
            input.SetValue(Vector2.zero);
            _inputVectorSmoothValueField.SetValue(input, Vector2.zero);
        }

        /// <summary>
        /// Captures the initial physical arrow edge before the game's systems consume and smooth the
        /// shared CameraMovement action.
        /// </summary>
        public static void BeforeGameSystems()
        {
            try
            {
                if (SettingsHandler.IsCapturingBindingInput)
                {
                    _pendingPhysicalArrowDirection = Vector2.zero;
                    _pendingPanelDirection = 0;
                    _suppressNextPhysicalArrowInterval = false;
                    return;
                }
                Keyboard keyboard = Keyboard.current;
                if (keyboard == null)
                {
                    return;
                }

                if (keyboard.leftArrowKey.wasPressedThisFrame)
                {
                    _pendingPhysicalArrowDirection = new Vector2(-1f, 0f);
                    _suppressNextPhysicalArrowInterval = true;
                    _suppressArrowCameraMovement = true;
                }
                else if (keyboard.rightArrowKey.wasPressedThisFrame)
                {
                    _pendingPhysicalArrowDirection = new Vector2(1f, 0f);
                    _suppressNextPhysicalArrowInterval = true;
                    _suppressArrowCameraMovement = true;
                }
                else if (keyboard.downArrowKey.wasPressedThisFrame)
                {
                    _pendingPhysicalArrowDirection = new Vector2(0f, -1f);
                    _suppressNextPhysicalArrowInterval = true;
                    _suppressArrowCameraMovement = true;
                }
                else if (keyboard.upArrowKey.wasPressedThisFrame)
                {
                    _pendingPhysicalArrowDirection = new Vector2(0f, 1f);
                    _suppressNextPhysicalArrowInterval = true;
                    _suppressArrowCameraMovement = true;
                }

                if (keyboard.quoteKey.wasPressedThisFrame)
                {
                    _pendingPanelDirection = 1;
                }
                else if (keyboard.semicolonKey.wasPressedThisFrame)
                {
                    _pendingPanelDirection = -1;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("MenuNavigationHandler.BeforeGameSystems threw: " + ex);
            }
        }

        /// <summary>Restores the shared action after the free-camera consumer has returned.</summary>
        public static void FreeCameraPostfix(Player player, CameraMovementState __state)
        {
            CameraZoomHandler.AfterCameraUpdate(player, __state.ZoomDistance);
            if (__state.Applied && __state.Input != null)
            {
                __state.Input.SetValue(__state.Value);
                _inputVectorSmoothValueField?.SetValue(
                    __state.Input, __state.SmoothValue);
            }
        }

        /// <summary>
        /// Harmony prefix on <c>UIManager.NavigateMenu</c>.
        /// </summary>
        /// <param name="__instance">The manager about to navigate.</param>
        /// <returns>False to skip the game's navigation for this frame.</returns>
        /// <remarks>
        /// WORKAROUND: the game and the mod would otherwise drive two different groups from the
        /// same arrow press, moving focus twice and speaking twice. There is no game API to hand
        /// navigation over, because the game never anticipated a second driver. Approved by the
        /// user (2026-08-08) for sibling panels, then extended with approval on 2026-08-15 for a
        /// deliberately selected CurrentWindow. The latter is required for game windows such as
        /// UIOfferedWants which register as CurrentWindow but do not move from keyboard input.
        /// Unselected CurrentWindows still run untouched, so the game's normal gamepad route keeps
        /// priority until the user or tutorial explicitly hands that panel to the mod.
        /// </remarks>
        public static bool NavigateMenuPrefix(UIManager __instance)
        {
            try
            {
                return !ShouldSuppressGameNavigation(__instance);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("NavigateMenuPrefix threw, letting the game navigate: " + ex);
                return true;
            }
        }

        /// <summary>True only while this handler is inside its own Enter activation.</summary>
        private static bool _modInitiatedActivation;

        /// <summary>
        /// Harmony prefix on <c>Focusable.ClickUp</c>. Makes the element look hovered for the
        /// duration of a mod-initiated activation.
        /// </summary>
        /// <param name="__instance">The element being activated.</param>
        /// <param name="__state">True when this call changed the field and must restore it.</param>
        /// <remarks>
        /// WORKAROUND: Focusable.ClickUp only raises OnClick when
        /// `!IsClickCanceled &amp;&amp; (_isHovered || (_justEnabled &amp;&amp; IsPressed))`. `_justEnabled` is set in
        /// OnEnable and cleared by ANY write to IsMouseHovered - including hover EXIT - so every
        /// element the mouse has passed over since it appeared becomes impossible to activate from
        /// the keyboard unless the cursor still happens to rest on it. Measured in TEST 14:
        /// `Enter on 'Settings': _isHovered=False, _justEnabled=False, OnClick expected to fire=False`.
        ///
        /// The clean path is the one already used - ClickDown() then ClickUp(), exactly what the
        /// game's own NavigateMenuDoClick does for the gamepad. It is blocked by that guard, and
        /// there is no way around it from outside the class: `_isHovered` has no public setter
        /// other than IsMouseHovered (which fires OnHoverGained/OnHoverLost, changing visuals,
        /// tooltips and announcements), and OnClick() is protected virtual.
        ///
        /// So the private field is written directly and restored immediately, which fires no
        /// events and leaves no state behind. Scoped to _modInitiatedActivation, so clicks the game
        /// or the mouse initiate are completely untouched. Approved by the user (2026-08-09).
        ///
        /// This is a game bug, not a mod-only concern - gamepad users hit it too. Worth reporting
        /// upstream; a fix there would let this patch be deleted.
        /// </remarks>
        public static void ClickUpPrefix(Focusable __instance, out bool __state)
        {
            __state = false;

            if (!_modInitiatedActivation || _isHoveredField == null || _justEnabledField == null || __instance == null)
            {
                return;
            }

            // A cancelled click is a decision the game made; never force one it deliberately dropped.
            if (__instance.IsClickCanceled)
            {
                return;
            }

            bool hovered = _isHoveredField.GetValue(__instance) is bool h && h;
            bool justEnabled = _justEnabledField.GetValue(__instance) is bool j && j;

            // Mirror the game's own guard exactly and step in ONLY when it would swallow the click.
            // Intervening unconditionally would both touch state needlessly and make the log claim
            // the workaround was "needed" on clicks that would have worked anyway.
            if (hovered || (justEnabled && __instance.IsPressed))
            {
                return;
            }

            _isHoveredField.SetValue(__instance, true);
            __state = true;
        }

        /// <summary>Harmony postfix on <c>Focusable.ClickUp</c>. Restores the hover field.</summary>
        /// <param name="__instance">The element that was activated.</param>
        /// <param name="__state">True when the prefix changed the field.</param>
        public static void ClickUpPostfix(Focusable __instance, bool __state)
        {
            if (__state && _isHoveredField != null && __instance != null)
            {
                _isHoveredField.SetValue(__instance, false);

                // Logged so the workaround proves itself in ordinary play. Pair this line with the
                // [Activate] line that follows: together they show a click that the game's hover
                // guard would otherwise have swallowed.
                Plugin.Log.LogInfo(
                    $"[Nav] Enter needed the hover workaround on '{UiLabelReader.GetLabelOrFallback(__instance)}'.");
            }
        }

        private static Focusable _cursor;
        private static FocusableGroup _cursorGroup;
        private static FocusableGroup _inventoriedGroup;

        /// <summary>The element the mod considers focused, or null when it is not driving.</summary>
        /// <remarks>
        /// Since v0.15 the mod owns arrow navigation, so this - not
        /// <c>FocusableGroup.GetFocused</c> - is the answer to "where is the user". The game's
        /// answer is unreliable by design: it reports the first element holding IsFocused, and
        /// UICharacterCreatorGenderItem.Refresh pins the selected gender there every frame as a
        /// *visual* selected state (game-api.md sections 27, 28).
        /// </remarks>
        public static Focusable Cursor => _cursor;

        /// <summary>The group <see cref="Cursor"/> belongs to, or null.</summary>
        public static FocusableGroup CursorGroup => _cursorGroup;

        /// <summary>
        /// Identifies one focusable unambiguously: name, two ancestors, and screen position.
        /// </summary>
        /// <remarks>
        /// Names are useless here on their own - every pooled ticket item is literally
        /// "Item(Clone)". The parent chain separates the Age row from Gender from Voice, and the
        /// position is what FocusableGroup.GetFocusableInDirection actually navigates by, so it is
        /// the only way to reason about why a direction resolves the way it does.
        /// </remarks>
        private static string Identify(Focusable f)
        {
            string path = f.name;
            UnityEngine.Transform p = f.transform.parent;

            for (int d = 0; d < 2 && p != null; d++)
            {
                path = p.name + "/" + path;
                p = p.parent;
            }

            UnityEngine.Vector3 pos = f.transform.position;
            return $"'{path}'@({pos.x:0},{pos.y:0}){(f.gameObject.activeInHierarchy ? string.Empty : "[inactive]")}";
        }

        /// <summary>
        /// Dumps a group's complete focusable list once, the first time it becomes the active panel.
        /// </summary>
        /// <remarks>
        /// TEST 24 showed navigation reaching only 2 of 33 focusables and bouncing between them.
        /// Deciding whether that is duplicate registrations, stacked positions, or a genuine angle
        /// failure needs the whole list with positions - and it is static per screen, so it is
        /// logged once per group rather than per frame.
        /// </remarks>
        private static void DumpGroupInventory(FocusableGroup group)
        {
            if (group == null || ReferenceEquals(group, _inventoriedGroup))
            {
                return;
            }

            _inventoriedGroup = group;

            try
            {
                List<Focusable> list = GetFocusables(group);
                if (list == null || list.Count == 0)
                {
                    return;
                }

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[NavProbe/inventory] '{group.name}' has {list.Count} focusables:");

                for (int i = 0; i < list.Count; i++)
                {
                    Focusable f = list[i];
                    sb.AppendLine(f == null
                        ? $"    {i}: (null)"
                        : $"    {i}: {Identify(f)} focused={f.IsFocused}");
                }

                Plugin.Log.LogInfo(sb.ToString());
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"DumpGroupInventory threw: {ex}");
            }
        }

        /// <summary>
        /// Reads a group's private focusable list, or null when Reflection could not resolve it.
        /// </summary>
        /// <param name="group">The group to read.</param>
        /// <returns>The game's own list instance - do not mutate it.</returns>
        private static List<Focusable> GetFocusables(FocusableGroup group)
        {
            ResolveReflection();
            return _focusablesField?.GetValue(group) as List<Focusable>;
        }

        /// <summary>
        /// Restores the "exactly one focused element" invariant before asking the game to navigate.
        /// </summary>
        /// <remarks>
        /// THE FIX for game-api.md section 27. The game breaks its own invariant:
        /// UICharacterCreatorGenderItem.Refresh runs every frame and assigns
        /// `Button.IsFocused = base.IsOn`, using IsFocused as a *selected* highlight. The selected
        /// gender is therefore pinned focused forever, so two elements are focused at once.
        ///
        /// FocusableGroup.GetFocused returns the FIRST focused entry, and Navigate steers from it.
        /// Measured consequence: navigation steers from the pinned gender instead of the user's
        /// position, its `focusableInDirection.IsFocused = true` lands on an item that was already
        /// true so nothing announces, and focus oscillates between two gender items forever - the
        /// same for all four directions. Age, Voice and the name fields are unreachable as a result.
        ///
        /// The repair uses the game's OWN public FocusableGroup.SetFocused, which assigns
        /// `IsFocused = (focusable == newFocused)` across the group. Nothing is reimplemented: the
        /// mod only says which element is focused before asking the game to navigate from it.
        ///
        /// Deliberately a no-op unless the invariant is ALREADY broken. When the game's state is
        /// correct the mod does not touch it, so the blast radius is exactly the screens carrying
        /// the bug. Playability principle: work with the game, correct it only where it is wrong.
        ///
        /// Known and accepted cost: on a broken group this writes IsFocused = false to the pinned
        /// gender item for one frame, and the game's own Refresh restores it on the next. That is a
        /// one-frame flicker of a highlight the game rewrites every frame anyway.
        /// </remarks>
        /// <param name="group">The group about to be navigated.</param>
        private static void NormalizeFocus(FocusableGroup group)
        {
            try
            {
                List<Focusable> list = GetFocusables(group);
                if (list == null)
                {
                    return;
                }

                int focusedCount = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    Focusable f = list[i];
                    if (f != null && !f.IsShoulderButton && f.IsFocused)
                    {
                        focusedCount++;
                    }
                }

                if (focusedCount <= 1)
                {
                    // The game's own state is consistent. Leave it alone.
                    return;
                }

                // Prefer the element the mod last drove focus to. GetFocused cannot be used to
                // recover it - returning the wrong element is the entire bug being fixed here.
                Focusable target = IsCursorUsable(group, list) ? _cursor : group.GetFocused(false);
                if (target != null && FocusNavigator.ShouldSkip(target))
                {
                    target = FindLeafFocusable(group, list);
                }
                if (target == null)
                {
                    return;
                }

                group.SetFocused(target);

                Plugin.Log.LogInfo(
                    $"[Nav] '{group.name}': {focusedCount} elements were focused at once "
                  + $"(game-api.md 27). Reset to '{target.name}' before navigating.");
            }
            catch (Exception ex)
            {
                // External game API on pooled objects; never break navigation over a repair.
                Plugin.Log.LogError($"NormalizeFocus threw: {ex}");
            }
        }

        /// <summary>
        /// True when the tracked cursor still refers to a live element of this group.
        /// </summary>
        /// <remarks>
        /// A panel switch makes the cursor belong to a different group, and UIList pooling can
        /// deactivate or unregister the element behind it (game-api.md section 15), so membership is
        /// re-checked rather than assumed. Failing this test falls back to the game's answer, which
        /// is merely imprecise rather than wrong - it re-seeds on the next press.
        /// </remarks>
        private static bool IsCursorUsable(FocusableGroup group, List<Focusable> list)
        {
            // Unity's overloaded == also catches a destroyed object, not just a null reference.
            if (_cursor == null || !ReferenceEquals(_cursorGroup, group))
            {
                return false;
            }

            return _cursor.gameObject.activeInHierarchy && list.Contains(_cursor)
                && !FocusNavigator.ShouldSkip(_cursor);
        }

        /// <summary>
        /// Lists which entries of a group hold <c>IsFocused</c>, in the group's own list order.
        /// </summary>
        /// <remarks>
        /// This is the ONE question the decompiled source cannot answer, so it is measured instead
        /// of guessed at.
        ///
        /// Grepping every non-literal IsFocused assignment in the game returns exactly two writers:
        ///   FocusableGroup.SetFocused        - IsFocused = (focusable == newFocused), true focus
        ///   UICharacterCreatorGenderItem.Refresh:38 - Button.IsFocused = base.IsOn, EVERY FRAME
        /// The second uses IsFocused as a visual SELECTED state, so in the character creator the
        /// selected gender is pinned focused no matter where the user actually is.
        ///
        /// FocusableGroup.GetFocused returns the FIRST list entry with IsFocused true, and Navigate
        /// takes its direction from that. If more than one entry is true, navigation steers from the
        /// wrong element, and `focusableInDirection.IsFocused = true` can be a no-op on an item that
        /// was already true - which changes GetFocused's answer with NO false->true transition.
        /// That is silent to FocusHandler and invisible to the "moved nothing" check, which is
        /// exactly the signature TEST 22 recorded.
        ///
        /// Printing the count plus only the focused entries keeps this to one short line per
        /// navigation interval even in a 33-focusable group.
        /// </remarks>
        /// <param name="group">The group to describe.</param>
        /// <returns>A compact description of the group's focus state.</returns>
        private static string DescribeFocusState(FocusableGroup group)
        {
            try
            {
                // Shares the FieldInfo the panel scanner uses. ResolveReflection is idempotent, so
                // calling it here removes any dependence on which method happened to run first; if
                // resolution failed it has already logged why, so this stays quiet about it.
                ResolveReflection();

                Focusable current = group.GetFocused(false);
                string head = $"'{group.name}' getFocused="
                            + $"{(current != null ? "'" + current.name + "'" : "(null)")}";

                var list = _focusablesField?.GetValue(group) as List<Focusable>;
                if (list == null)
                {
                    return head + " focusables=(unreadable)";
                }

                var flagged = new List<string>();
                // Every pooled ticket item is named "Item(Clone)", so the name alone cannot tell
                // two focusables apart - which is exactly what TEST 24 needed and could not get.
                // The ancestor path and screen position identify them.
                for (int i = 0; i < list.Count; i++)
                {
                    Focusable f = list[i];
                    if (f != null && f.IsFocused)
                    {
                        flagged.Add($"{i}:{Identify(f)}{(f.IsShoulderButton ? "(shoulder)" : string.Empty)}");
                    }
                }

                return head + $" count={list.Count} isFocusedTrue=[{string.Join(", ", flagged.ToArray())}]";
            }
            catch (Exception ex)
            {
                return $"(focus state unreadable: {ex.GetType().Name})";
            }
        }

        /// <summary>
        /// True on the single frame a directional press should move focus one step.
        /// </summary>
        /// <remarks>
        /// Reads the game's own <c>IntervalPressed</c> flags rather than raw stick values, because
        /// that is exactly what <c>FocusableGroup.NavigateMenuArrows</c> reads. Any other definition
        /// would make the diagnostics disagree with the code they are measuring.
        /// </remarks>
        /// <param name="hybridPlayer">The player whose input to test.</param>
        /// <returns>True when the game considers this frame a navigation step.</returns>
        private static bool IsDirectionalInterval(HybridPlayer hybridPlayer)
        {
            bool dpad = hybridPlayer.ButtonDPadUp.IntervalPressed
                || hybridPlayer.ButtonDPadDown.IntervalPressed
                || hybridPlayer.ButtonDPadLeft.IntervalPressed
                || hybridPlayer.ButtonDPadRight.IntervalPressed;
            if (dpad)
            {
                return true;
            }

            // InputVector2 can leave IntervalPressed set on the zero-valued release frame.
            // Quantizing zero through the game's angle helper produces a phantom Up (TEST 77).
            if (!hybridPlayer.CameraMovement.IntervalPressed
                || hybridPlayer.CameraMovement.Value == Vector2.zero)
            {
                return false;
            }

            // CameraMovement combines WASD and arrows. In ordinary live mode an explicit panel
            // selection claims the arrows, not every camera key: WASD must remain available for
            // camera movement and must not silently move UI focus at the same time.
            Player player = hybridPlayer.Player;
            if (HasUiPanelSelection && GameLoadingManager.State == State.Game
                && player != null && player.IsUsingFreeCamera)
            {
                Keyboard keyboard = Keyboard.current;
                return keyboard != null
                    && (keyboard.leftArrowKey.isPressed || keyboard.rightArrowKey.isPressed
                        || keyboard.upArrowKey.isPressed || keyboard.downArrowKey.isPressed);
            }

            return true;
        }

        /// <summary>
        /// Names the early return that swallowed a directional press. Diagnostic only.
        /// </summary>
        /// <remarks>
        /// Silent only when no navigation interval occurred, so this cannot flood: at most one line
        /// per repeat interval, and only while a direction is actually held. It exists because a
        /// press that vanishes with no log is indistinguishable from a broken mod, which is what
        /// TEST 21 ran into.
        /// </remarks>
        /// <param name="directional">Whether this frame was a real navigation step.</param>
        /// <param name="reason">Which return path was taken.</param>
        private static void TraceNavPath(bool directional, string reason)
        {
            if (directional)
            {
                Plugin.Log.LogInfo($"[Nav] Directional press not driven by the mod: {reason}.");
            }
        }

        /// <summary>
        /// Attaches the activation diagnostic to one <c>OnClick</c> override.
        /// </summary>
        /// <remarks>
        /// Declared on the given type only. A type that does not redeclare OnClick is skipped rather
        /// than double-patching an inherited method, which would log one activation twice.
        /// </remarks>
        /// <param name="harmony">The patcher.</param>
        /// <param name="declaringType">The class whose OnClick override should be patched.</param>
        private static void PatchOnClick(Harmony harmony, Type declaringType)
        {
            MethodInfo onClick = AccessTools.DeclaredMethod(declaringType, "OnClick");
            if (onClick == null)
            {
                Plugin.Log.LogWarning(
                    $"Could not find {declaringType.Name}.OnClick; activations on that type cannot "
                  + "be confirmed in the log.");
                return;
            }

            harmony.Patch(
                onClick,
                postfix: new HarmonyMethod(AccessTools.Method(typeof(MenuNavigationHandler), nameof(OnClickPostfix))));

            Plugin.Log.LogInfo(
                $"Patched {declaringType.Name}.OnClick, applied="
              + $"{new List<MethodBase>(harmony.GetPatchedMethods()).Contains(onClick)}.");
        }

        /// <summary>
        /// Harmony postfix on the <c>OnClick</c> overrides. Diagnostic only.
        /// </summary>
        /// <param name="__instance">The button that was activated.</param>
        /// <remarks>
        /// This is the only proof that an activation actually reached the game. ParaButton overrides
        /// the protected virtual Focusable.OnClick, so the override is patched directly - a patch on
        /// the base method would miss every real button.
        /// </remarks>
        public static void OnClickPostfix(ParaButton __instance)
        {
            if (__instance == null)
            {
                return;
            }

            // "OnClick fired" is not the same as "the thing happened", and conflating them is how a
            // blind user ends up unsure whether a choice registered. UIListItemBase.OnClick only
            // reaches OnListItemClicked() when the list is NOT in selection mode, or when a second
            // click lands within 0.2s of the first (Time.unscaledTime - _lastClickTime <= 0.2f).
            // In selection mode a single click just moves the selection. Naming which of the two
            // happened is the whole point of this line.
            string note = string.Empty;
            try
            {
                if (__instance is UIListItemBase item)
                {
                    UIList parent = item.ParentUIList;
                    if (parent == null)
                    {
                        note = " (inert: ParentUIList is null, the click does nothing)";
                    }
                    else if (parent.InSelectionMode)
                    {
                        note = " (selection-mode list: a single click only moves the selection; "
                             + "activating needs a second click within 0.2s)";
                    }
                }
            }
            catch (Exception ex)
            {
                // External game API on a pooled item; a diagnostic must never break activation.
                Plugin.Log.LogError($"Classifying the click target threw: {ex}");
            }

            Plugin.Log.LogInfo(
                $"[Activate] OnClick fired on '{UiLabelReader.GetLabelOrFallback(__instance)}'{note}.");
        }

        private static bool ShouldSuppressGameNavigation(UIManager manager)
        {
            if (SettingsHandler.IsCapturingBindingInput) return true;
            // The catalogue, placement and lot-object handlers deliberately own the same
            // directional/confirm inputs as UIManager.NavigateMenu. Placement is the critical
            // case: its synthetic ButtonConfirm.Up must reach EndMoveItem without also releasing a
            // focused catalogue control.
            if (LotBuildInputOwnership.OwnsInput)
            {
                return true;
            }

            if (!Enabled || manager == null || _activeGroup == null)
            {
                return false;
            }

            UIWindow current = manager.CurrentWindow;
            if (current == null)
            {
                // The game would do nothing anyway; never claim a suppression we do not need.
                return false;
            }

            // A deliberate selection of CurrentWindow is still mod-driven. TEST 48 showed that
            // UIOfferedWants is registered as the game's CurrentWindow but its keyboard path moved
            // nowhere for fifteen directional presses. Once the user (or tutorial) chooses that
            // panel, suppress the inert game route and let the same FocusNavigator used by every
            // sibling panel drive it. Default, unchosen windows remain entirely game-driven.
            return HasUiPanelSelection || !ReferenceEquals(_activeGroup, current);
        }

        /// <summary>Called once per frame from the game loop hook.</summary>
        public static void Tick()
        {
            if (!Enabled)
            {
                _pendingPhysicalArrowDirection = Vector2.zero;
                _pendingPanelDirection = 0;
                _suppressNextPhysicalArrowInterval = false;
                _suppressArrowCameraMovement = false;
                _pendingActivationOutcome = null;
                return;
            }

            Vector2 capturedPhysicalDirection = _pendingPhysicalArrowDirection;
            _pendingPhysicalArrowDirection = Vector2.zero;
            int capturedPanelDirection = _pendingPanelDirection;
            _pendingPanelDirection = 0;

            try
            {
                HybridPlayer hybridPlayer = PlayerManager.Instance != null
                    ? PlayerManager.Instance.HybridPlayer1
                    : null;

                if (hybridPlayer == null)
                {
                    return;
                }

                if (WindowHandler.IsSceneTransitionActive)
                {
                    _pressedFocusable = null;
                    _pressedOccupationOffer = false;
                    _pressedCreatorSemantic = false;
                    _pendingActivationOutcome = null;
                    _suppressNextPhysicalArrowInterval = false;
                    return;
                }

                // TEST 21 left one question unanswered: in UICharacterCreator (33 focusables) 13
                // directional presses moved nothing AND neither existing diagnostic fired. Three
                // candidate causes were traced through source and all three were ruled out, so this
                // traces the PATH instead of guessing at a fourth. Every early return below is
                // named, so one run says exactly where a press is lost.
                //
                // Gated on a real navigation interval, not on the raw stick value: InputVector2 sets
                // IntervalPressed true for a single frame per repeat interval, and the game's own
                // FocusableGroup.NavigateMenuArrows reads that same flag. Logging raw magnitude
                // would emit every frame a key is held and drown the answer.
                bool interval = IsDirectionalInterval(hybridPlayer);
                Keyboard keyboard = Keyboard.current;
                bool physicalArrowHeld = keyboard != null
                    && (keyboard.leftArrowKey.isPressed || keyboard.rightArrowKey.isPressed
                        || keyboard.upArrowKey.isPressed || keyboard.downArrowKey.isPressed);

                // TEST 55 measured two focus moves from one short press: the pre-system physical
                // edge moved once, then CameraMovement.IntervalPressed repeated that same edge on a
                // later frame. Consume one such interval while the physical key is still held. The
                // flag clears after that one interval, preserving the game's later key-repeat pace.
                if (capturedPhysicalDirection == Vector2.zero
                    && interval && _suppressNextPhysicalArrowInterval && physicalArrowHeld)
                {
                    interval = false;
                    _suppressNextPhysicalArrowInterval = false;
                    Plugin.Log.LogInfo(
                        "[Nav] Suppressed the delayed CameraMovement interval for the already handled physical arrow edge.");
                }
                else if (capturedPhysicalDirection == Vector2.zero && !physicalArrowHeld)
                {
                    _suppressNextPhysicalArrowInterval = false;
                }

                bool directional = capturedPhysicalDirection != Vector2.zero || interval;

                // A modal panel the mod owns is up. Two handlers acting on one keypress is how the
                // Name Details window nearly closed itself on the press that opened it (TEST 33);
                // see NameEntryHandler.OwnsInput.
                if (SettingsHandler.IsCapturingBindingInput || SettingsHandler.OwnsEditInput
                    || NameEntryHandler.OwnsInput
                    || TownMapLotHandler.OwnsInput
                    || LotBuildInputOwnership.OwnsInput)
                {
                    // Any press we were holding belongs to a gesture we are no longer part of.
                    // Keeping it would let a later release complete against a stale element.
                    _pressedFocusable = null;
                    _pressedOccupationOffer = false;
                    _pressedCreatorSemantic = false;
                    TraceNavPath(directional, "an exclusive accessibility handler owns the input");
                    return;
                }

                // The user is typing - every mod key must stay out of the way.
                if (hybridPlayer.HasInputFieldFocused)
                {
                    _pressedFocusable = null;
                    _pressedOccupationOffer = false;
                    _pressedCreatorSemantic = false;
                    // Searchable native dropdowns deliberately auto-select their search input.
                    // Physical Up/Down leaves that exact field; typing and caret keys stay native.
                    Vector2 searchDirection = capturedPhysicalDirection;
                    if (searchDirection == Vector2.zero && interval && keyboard != null)
                    {
                        if (keyboard.downArrowKey.isPressed) searchDirection = new Vector2(0f, -1f);
                        else if (keyboard.upArrowKey.isPressed) searchDirection = new Vector2(0f, 1f);
                    }
                    UIWindow searchWindow = ResolveGameDrivenWindow(hybridPlayer);
                    if (ContextMenuSearchHandler.TryLeaveSearch(searchWindow, searchDirection,
                        out Focusable searchResult))
                    {
                        if (searchResult != null)
                        {
                            FocusForegroundWindow(searchWindow);
                            _activeGroup = searchWindow;
                            bool announce = FocusHandler.AnnounceFocus;
                            FocusHandler.AnnounceFocus = false;
                            try
                            {
                                _cursor = searchResult;
                                _cursorGroup = searchWindow;
                                searchWindow.SetFocused(searchResult);
                            }
                            finally { FocusHandler.AnnounceFocus = announce; }
                            // The first result may already hold silent foreground focus. Speak
                            // explicitly so Down does not skip it or depend on a focus-change event.
                            ScreenReader.Speak(UiLabelReader.GetLabelOrFallback(searchResult));
                        }
                        return;
                    }
                    TraceNavPath(directional, "input field has focus");
                    // Logged because this silently disables ALL navigation, which is
                    // indistinguishable from a broken mod. The character creator has first/last
                    // name TMP_InputFields, so it is a live suspect whenever a panel there stops
                    // responding to the arrow keys.
                    if (!_loggedInputFieldBlock)
                    {
                        _loggedInputFieldBlock = true;
                        Plugin.Log.LogInfo(
                            "[Nav] Navigation suspended: an input field has focus (HasInputFieldFocused).");
                    }

                    return;
                }

                _loggedInputFieldBlock = false;

                UIWindow gameDrivenWindow = ResolveGameDrivenWindow(hybridPlayer);
                BuildCandidates(hybridPlayer, gameDrivenWindow);

                if (IsBlockingCommonWindow(gameDrivenWindow))
                    FocusForegroundWindow(gameDrivenWindow);

                HandlePanelSwitching(capturedPanelDirection);

                ObservePendingActivationOutcome(gameDrivenWindow);

                FocusableGroup target = ResolveActiveGroup(gameDrivenWindow);
                _activeGroup = target;
                LogTargetChange(target, gameDrivenWindow);

                HandleActivation(target);

                if (target == null)
                {
                    TraceNavPath(directional, "no active group resolved");
                    return;
                }

                if (ReferenceEquals(target, gameDrivenWindow) && !HasUiPanelSelection)
                {
                    // The game drives this panel itself.
                    TraceNavPath(directional, $"'{target.name}' is the game-driven window");
                    return;
                }

                // A game-driven window is up, so a modal UI already owns the screen and the arrow
                // keys are free. Otherwise fall back to the gameplay guard.
                if (gameDrivenWindow == null && !ShouldSupplyNavigation(hybridPlayer))
                {
                    TraceNavPath(directional, "gameplay owns the arrow keys (ShouldSupplyNavigation false)");
                    return;
                }

                if (directional)
                {
                    DumpGroupInventory(target);
                    NormalizeFocus(target);

                    // Dedicated screen navigation runs first. Other panels follow native list
                    // order and control hierarchy, not distances between their moving visuals.
                    Vector2 direction = capturedPhysicalDirection != Vector2.zero
                        ? capturedPhysicalDirection
                        : FocusNavigator.ReadDirection(target, hybridPlayer);
                    if (direction != Vector2.zero)
                    {
                        Focusable source = IsCursorUsable(target, GetFocusables(target))
                            ? _cursor
                            : target.GetFocused(false);
                        if (source != null && FocusNavigator.ShouldSkip(source))
                        {
                            source = null;
                        }

                        if (ContextMenuSearchHandler.TryReturnToSearch(target, source, direction))
                            return;

                        Focusable moved;
                        FocusableGroup destinationGroup;
                        bool logicalHandled = CharacterCreatorHandler.TryNavigateLogical(
                            target,
                            GetFocusables(target),
                            source,
                            direction,
                            out moved,
                            out destinationGroup);
                        if (!logicalHandled)
                        {
                            logicalHandled = HouseholdLibraryHandler.TryNavigate(target, GetFocusables(target),
                                source, direction, out moved);
                            destinationGroup = target;
                        }
                        if (!logicalHandled)
                        {
                            logicalHandled = SettingsHandler.TryNavigate(target, GetFocusables(target),
                                source, direction, out moved);
                            destinationGroup = target;
                        }
                        if (!logicalHandled)
                        {
                            moved = LogicalFocusNavigator.FindTarget(
                                target, GetFocusables(target), source, direction);
                            destinationGroup = target;
                        }

                        if (moved != null)
                        {
                            FocusableGroup committedGroup = destinationGroup ?? target;
                            if (!ReferenceEquals(committedGroup, target))
                            {
                                // The creator draws its tabs and content as one screen, but registers
                                // them as separate focus groups. An authored Up edge may therefore
                                // cross panels. Treat that user gesture like an explicit panel choice
                                // so ResolveActiveGroup cannot pull focus straight back into the
                                // fuller content panel on the next frame.
                                _selectedGroup = committedGroup;
                                _selectionIsExplicit = true;
                                _selectionIsTutorialDirected = false;
                                _selectionIsForegroundDirected = false;
                                _suspendedSelections.Clear();
                                Plugin.Log.LogInfo(
                                    $"[Nav] Directional creator handoff from '{target.name}' "
                                  + $"to '{committedGroup.name}'.");
                            }

                            // ORDER IS LOAD-BEARING. SetFocused writes IsFocused, the mod patches
                            // that setter to announce, and FocusHandler.AgreesWithModCursor speaks
                            // only what matches the cursor. Publishing the cursor first is what
                            // makes the destination announceable; committing first left the cursor
                            // pointing at the previous element and silenced the whole main menu.
                            CommitArrowFocus(committedGroup, moved);
                        }
                        else
                        {
                            Plugin.Log.LogInfo(
                                $"[Nav] '{target.name}': nothing lies in direction {direction} from "
                              + $"'{(source != null ? source.name : "(nothing focused)")}'.");
                        }
                    }
                }

                // Shoulder buttons stay the game's: they navigate a separate focusable set
                // (IsShoulderButton true) that the duplicate-registration bug has not been seen on.
                target.NavigateMenuShoulders(hybridPlayer);

                if (directional)
                {
                    // FocusNavigator already names the "nothing in that direction" case with the
                    // direction vector and the element it searched from, which is strictly more
                    // informative than the old before/after identity test - and that test could not
                    // survive the duplicate-registration bug anyway, because the twins made the two
                    // samples differ while the user had not moved (game-api.md sections 27, 28).
                    Plugin.Log.LogInfo($"[NavProbe/focus] after:  {DescribeFocusState(target)}");
                }
            }
            catch (Exception ex)
            {
                // Runs inside the game's loop; contain it rather than let it escape into game code.
                Enabled = false;
                Plugin.Log.LogError("MenuNavigationHandler disabled after an exception: " + ex);
            }
        }

        /// <summary>
        /// Returns the window the game is already navigating this frame, or null.
        /// </summary>
        /// <remarks>
        /// Reproduces the manager choice in Back.Update: the common manager wins when it has a
        /// CurrentWindow, otherwise the per-player manager is used.
        /// </remarks>
        private static UIWindow ResolveGameDrivenWindow(HybridPlayer hybridPlayer)
        {
            UIManager common = HybridReferences.Instance != null
                ? HybridReferences.Instance.UIManagerCommon
                : null;

            if (common != null && common.CurrentWindow != null)
            {
                return common.CurrentWindow;
            }

            return hybridPlayer.UIManager != null ? hybridPlayer.UIManager.CurrentWindow : null;
        }

        /// <summary>
        /// True when the arrow keys are free to mean "move focus" rather than "move the camera".
        /// </summary>
        /// <remarks>
        /// In live mode the arrow keys and WASD share CameraMovement, and a sighted player gets no
        /// menu focus there either. Navigating without an explicit panel choice would move focus
        /// and the camera on the same press. Both signals used here are the game's own:
        ///  - GameLoadingManager.State: outside State.Game there is no gameplay camera to disturb.
        ///  - Player.PlayerActionBlockedByUIWindow: set by the game's own
        ///    CheckIfPlayerActionsAreBlockedByUIWindow system from
        ///    UIManager.HasWindowBlockingPlayerActions().
        ///
        /// Semicolon/apostrophe are the explicit focus-the-UI keys. Once either chooses a panel,
        /// physical arrows navigate it; WASD remains camera input.
        /// </remarks>
        private static bool ShouldSupplyNavigation(HybridPlayer hybridPlayer)
        {
            if (GameLoadingManager.State != State.Game)
            {
                return true;
            }

            Player player = hybridPlayer.Player;
            if (player == null)
            {
                return false;
            }

            // A semicolon/apostrophe press, tutorial-directed focus, or a newly opened foreground
            // window is an unambiguous request to operate UI. This used to be honored only during
            // the intro, even though the panel selection itself persisted in ordinary live mode.
            // The log therefore said "Panel switched" and then rejected every arrow with
            // ShouldSupplyNavigation false. The camera prefix above prevents those same physical
            // arrow presses from also moving the world.
            if (HasUiPanelSelection)
            {
                return true;
            }

            // MIRRORS UpdateFreeCamera's OWN ENTRY GUARD (UpdateFreeCamera.cs:9), rather than
            // guessing which screens are "menus". That system is the thing that would otherwise
            // consume the arrow keys, and it returns immediately when either test below holds - so
            // on those frames nothing else is claiming the input and the mod may use it.
            //
            // IsUsingFreeCamera is the game's own property and is simply
            // !(State == GameStates.CharacterCreator) (Player.cs:447-449).
            //
            // TEST 30 measured what its absence cost. In the Personality panel EVERY arrow press was
            // rejected with "gameplay owns the arrow keys", so a 47-item panel was announced and then
            // could not be navigated at all. The same panel worked minutes earlier only because
            // UICharacterOutfits happened to be open with RegisterWindowForEscapeButtonEvent=True,
            // which made CurrentWindow non-null and skipped this guard entirely. Navigation inside
            // the character creator was therefore contingent on whether an unrelated window happened
            // to be registered for the back button.
            //
            // Known simplification: the game's guard is
            // (PlayerActionBlockedByUIWindow && !StoryboardManager.IsCurrentlyPlayingStoryboards).
            // The storyboard term is deliberately not mirrored - that branch has never been observed
            // here, and the existing behaviour has shipped since v0.11. Revisit if a storyboard is
            // ever seen fighting the mod for the arrow keys.
            return !player.IsUsingFreeCamera || player.PlayerActionBlockedByUIWindow;
        }

        private static bool IsBlockingCommonWindow(UIWindow window)
        {
            return window != null && window.IsVisible
                && ReferenceEquals(HybridReferences.Instance?.UIManagerCommon?.CurrentWindow, window)
                && (window.BlockPlayerActionWhenShown || window.HideOtherInterfacesWhenShown);
        }

        /// <summary>Rebuilds reachable panels from the native window managers.</summary>
        /// <remarks>
        /// Local windows retain front-to-back ordering: creator content spans sibling windows.
        /// Common windows have input priority in Back.cs. A common window whose live flags block
        /// actions or hide other interfaces excludes background panels until it closes. The v0.39.23
        /// settings log measures this ownership for UIContextMenuWidget dropdowns.
        /// </remarks>
        private static void BuildCandidates(HybridPlayer hybridPlayer, UIWindow gameDrivenWindow)
        {
            _candidates.Clear();
            _windowBuffer.Clear();

            UIManager manager = hybridPlayer.UIManager;
            if (manager == null)
            {
                return;
            }

            ResolveReflection();

            var visibleWindows = _visibleWindowsField?.GetValue(manager) as IEnumerable;
            if (visibleWindows == null)
            {
                return;
            }

            foreach (object item in visibleWindows)
            {
                var window = item as UIWindow;
                if (window != null && window.IsVisible)
                {
                    _windowBuffer.Add(window);
                }
            }

            _windowBuffer.Sort(CompareBySiblingIndex);

            // The screen the user is on comes first; everything else keeps z-order behind it.
            // Common dropdowns belong to the common manager, absent from the player's list.
            // Back.cs gives that manager input priority; a blocking popup excludes its background.
            if (gameDrivenWindow != null && gameDrivenWindow.IsVisible)
            {
                if (IsBlockingCommonWindow(gameDrivenWindow)) _windowBuffer.Clear();
                else _windowBuffer.Remove(gameDrivenWindow);
                _windowBuffer.Insert(0, gameDrivenWindow);
            }

            foreach (UIWindow window in _windowBuffer)
            {
                foreach (FocusableGroup group in window.GetComponentsInChildren<FocusableGroup>(false))
                {
                    if (CountFocusables(group) > 0 && !_candidates.Contains(group))
                    {
                        _candidates.Add(group);
                    }
                }
            }
        }

        /// <summary>
        /// Orders windows front-to-back, so panel switching walks the screen from the layer nearest
        /// the user towards the background.
        /// </summary>
        /// <remarks>
        /// Descending, not ascending. Measured in TEST 14: with the escape menu open there were 82
        /// panels, and ascending sibling index put UITownMap (sibling=0, 80 one-item lot icons)
        /// ahead of UIEscapeMenu (17) and UIOptions (18). Pressing the panel key from Settings
        /// therefore dived straight into the map behind everything else. Front-to-back puts the
        /// panels the user is looking at first and the deep background last, without excluding
        /// anything.
        /// </remarks>
        private static int CompareBySiblingIndex(UIWindow a, UIWindow b)
        {
            return b.transform.GetSiblingIndex().CompareTo(a.transform.GetSiblingIndex());
        }

        /// <summary>
        /// Returns the panel to drive: the user's selection when it is still on screen, otherwise a
        /// default.
        /// </summary>
        /// <remarks>
        /// The default prefers the game-driven window's own panel so that screens the game already
        /// handles keep behaving exactly as before, and the user steps into nested panels
        /// deliberately. Failing that it takes the fullest panel.
        ///
        /// WHY FULLEST AND NOT TOPMOST: measured wrong in TEST 11. UIGameBar reported
        /// siblingIndex=9, above UIMainMenu, so topmost-by-z-order navigated the three overlay
        /// buttons instead of Continue/New Game/Load Game. UIGameBar is a thin overlay visible in
        /// every state, so being drawn on top does not make it the screen the user came to use.
        /// Element count is a better proxy for "the main content panel", but it is still a
        /// heuristic - no game API says which panel should be navigated. A wrong default now costs
        /// one panel keypress rather than blocking the screen.
        /// </remarks>
        private static FocusableGroup ResolveActiveGroup(UIWindow gameDrivenWindow)
        {
            if (_candidates.Count == 0)
            {
                // Preserve the return selection while a common window populates its controls.
                if (IsBlockingCommonWindow(gameDrivenWindow)) return null;
                _selectedGroup = null;
                _selectionIsExplicit = false;
                _selectionIsTutorialDirected = false;
                _selectionIsForegroundDirected = false;
                _suspendedSelections.Clear();
                return null;
            }

            if ((_selectionIsExplicit || _selectionIsTutorialDirected
                    || _selectionIsForegroundDirected)
                && _selectedGroup != null && _candidates.Contains(_selectedGroup))
            {
                return _selectedGroup;
            }

            // A foreground window closed. Restore the panel it displaced, including whether that
            // panel belonged to the user or to an earlier nested foreground window (for example,
            // lot information -> purchase prompt -> lot information).
            if (_selectionIsForegroundDirected)
            {
                while (_suspendedSelections.Count > 0)
                {
                    SuspendedSelection suspended = _suspendedSelections.Pop();
                    if (suspended.Group == null || !_candidates.Contains(suspended.Group))
                    {
                        continue;
                    }

                    _selectedGroup = suspended.Group;
                    _selectionIsExplicit = suspended.IsExplicit;
                    _selectionIsTutorialDirected = suspended.IsTutorialDirected;
                    _selectionIsForegroundDirected = suspended.IsForegroundDirected;
                    Plugin.Log.LogInfo(
                        $"[Nav] Foreground window closed; restored panel '{_selectedGroup.name}'.");
                    return _selectedGroup;
                }
            }

            // The panel the user picked is gone; fall back to a default rather than stranding them.
            _selectionIsExplicit = false;
            _selectionIsTutorialDirected = false;
            _selectionIsForegroundDirected = false;

            if (gameDrivenWindow != null && _candidates.Contains(gameDrivenWindow))
            {
                _selectedGroup = gameDrivenWindow;
                return gameDrivenWindow;
            }

            FocusableGroup fullest = _candidates[0];
            int bestCount = CountFocusables(fullest);

            for (int i = 1; i < _candidates.Count; i++)
            {
                int count = CountFocusables(_candidates[i]);
                if (count > bestCount)
                {
                    bestCount = count;
                    fullest = _candidates[i];
                }
            }

            _selectedGroup = fullest;
            return fullest;
        }

        /// <summary>
        /// Moves between panels on ';' and '''.
        /// </summary>
        /// <remarks>
        /// Both keys are unbound in the game (game-api.md section 3) and Tab is not available -
        /// the game uses it. Announcement is a single utterance naming the panel, its size and the
        /// element that took focus, so the panel name is not cut off by the focus announcement
        /// that FocusDefaultFocusable would otherwise trigger.
        /// </remarks>
        private static void HandlePanelSwitching(int direction)
        {
            if (direction == 0)
            {
                return;
            }

            bool next = direction > 0;

            Focusable sectionTarget;
            string sectionLabel;
            if (_selectedGroup != null && _candidates.Contains(_selectedGroup)
                && (HouseholdLibraryHandler.TrySwitchSection(_selectedGroup, GetFocusables(_selectedGroup),
                    _cursorGroup == _selectedGroup ? _cursor : _selectedGroup.GetFocused(false),
                    direction, out sectionTarget, out sectionLabel)
                || SettingsHandler.TrySwitchSection(_selectedGroup, GetFocusables(_selectedGroup),
                    _cursorGroup == _selectedGroup ? _cursor : _selectedGroup.GetFocused(false),
                    direction, out sectionTarget, out sectionLabel)))
            {
                if (sectionTarget == null) { ScreenReader.Speak(Loc.Get("panel_none")); return; }
                bool announce = FocusHandler.AnnounceFocus;
                FocusHandler.AnnounceFocus = false;
                try
                {
                    _cursor = sectionTarget;
                    _cursorGroup = _selectedGroup;
                    _selectedGroup.SetFocused(sectionTarget);
                }
                finally { FocusHandler.AnnounceFocus = announce; }
                ScreenReader.Speak(Loc.Get("panel_with_focus", sectionLabel,
                    UiLabelReader.GetLabelOrFallback(sectionTarget)));
                return;
            }

            if (_candidates.Count == 0)
            {
                ScreenReader.Speak(Loc.Get("panel_none"));
                return;
            }

            if (_candidates.Count == 1)
            {
                ScreenReader.Speak(Loc.Get("panel_only_one"));
                return;
            }

            int index = _selectedGroup != null ? _candidates.IndexOf(_selectedGroup) : -1;
            if (index < 0)
            {
                index = 0;
            }
            else
            {
                index = (index + (next ? 1 : -1) + _candidates.Count) % _candidates.Count;
            }

            _selectedGroup = _candidates[index];
            _selectionIsExplicit = true;
            _selectionIsTutorialDirected = false;
            _selectionIsForegroundDirected = false;
            _suspendedSelections.Clear();
            FocusPanelAndAnnounce(_selectedGroup, index, null);
        }

        /// <summary>
        /// Focuses a panel and speaks it as one utterance.
        /// </summary>
        /// <param name="group">The panel to focus.</param>
        /// <param name="index">Its position in the candidate list, or -1 to omit the position.</param>
        /// <param name="displayName">A spoken name, or null to clean up the object name.</param>
        private static void FocusPanelAndAnnounce(FocusableGroup group, int index, string displayName)
        {
            // Focus first with the focus announcement muted, so the panel name and the element that
            // took focus can be spoken as one utterance instead of interrupting each other.
            bool previousAnnounceFocus = FocusHandler.AnnounceFocus;
            FocusHandler.AnnounceFocus = false;
            try
            {
                SeedFocus(group);
            }
            finally
            {
                FocusHandler.AnnounceFocus = previousAnnounceFocus;
            }

            int count = CountFocusables(group);

            // Both routes into a panel must call it the same thing. Falling straight through to the
            // object name made the panel key announce "UICharacterCreatorPersonality" for the panel
            // that auto-open had just called "Personality" (TEST 30).
            string name = displayName;
            if (string.IsNullOrEmpty(name))
            {
                name = GetPanelDisplayName(group);
            }

            string message = count == 1
                ? Loc.Get("panel_selected_one", name)
                : Loc.Get("panel_selected", name, count);

            // Position matters when several panels share a name, as the town map's lot icons do.
            // Omitted for an auto-opened panel: the user did not walk a list to get there, so an
            // index would describe a journey they did not take.
            if (index >= 0)
            {
                message = Loc.Get("panel_position", message, index + 1, _candidates.Count);
            }

            List<Focusable> focusables = GetFocusables(group);
            Focusable focused = focusables != null && IsCursorUsable(group, focusables)
                ? _cursor
                : group.GetFocused(false);
            if (focused != null && FocusNavigator.ShouldSkip(focused))
            {
                focused = null;
            }
            if (focused != null)
            {
                message = Loc.Get("panel_with_focus", message, UiLabelReader.GetLabelOrFallback(focused));
            }

            Plugin.Log.LogInfo("[Nav] Panel switched: " + message);
            ScreenReader.Speak(message);
        }

        /// <summary>
        /// Places focus somewhere useful in a panel and publishes the mod's cursor.
        /// </summary>
        /// <remarks>
        /// The game's own FocusableGroup.FocusDefaultFocusable is used wherever it lands on a real
        /// control, so behaviour matches the game on every panel that already worked. It is
        /// deliberately NOT trusted blindly for two measured reasons:
        ///
        ///  - It is a no-op when anything in the group already holds IsFocused, and the stale
        ///    registration described on CountFocusables can leave an INACTIVE element focused. The
        ///    user would then be told about a panel while focus sat on something invisible.
        ///  - Its fallback rule is leftmost/topmost, which in the contextual list is a layout
        ///    CONTAINER. Measured in TEST 29: UICharacterCreatorDecalPositions and
        ///    UICharacterCreatorContextualListEquipments are UIListItemBase, so they are focusable
        ///    themselves while also holding the 7 section buttons and 11 equipment items. Landing on
        ///    one announces every child joined together - "Test, TattooMmeCrow, TattooCitrus, ..." -
        ///    before the user reaches a single real item.
        ///
        /// So the seed prefers a focusable that contains no OTHER registered focusable of the same
        /// group. That is a structural test, not a name match: it asks whether this element is a leaf
        /// of the panel. Containers stay reachable by arrow key; they are simply not where the user
        /// is dropped. If every candidate is a container, the game's answer is kept unchanged.
        ///
        /// Publishing the cursor matters: since v0.15 the mod's cursor, not GetFocused, is the
        /// authority on where the user is, and FocusHandler gates announcements on it.
        /// </remarks>
        /// <param name="group">The panel to seed focus in.</param>
        private static void SeedFocus(FocusableGroup group)
        {
            // The game's own seed runs FIRST and unconditionally. It also covers SHOULDER buttons,
            // which are a separate focusable set the mod never navigates - FocusableGroup.SetFocused
            // only touches entries whose IsShoulderButton matches its target, so seeding a leaf
            // alone would silently stop seeding them. This keeps that half identical to before.
            group.FocusDefaultFocusable();

            Focusable target = group.GetFocused(false);
            List<Focusable> list = GetFocusables(group);

            // The compact live-mode panel contains a fixed tracked-goal card plus its controls.
            // Its geometric default is the pin button ("Untrack Goal"), which hides exactly the
            // information the user opened the panel to hear. The card is a real registered
            // Focusable and carries the title/objectives, so use it as this panel's landmark.
            Focusable semanticSeed = FindSemanticSeed(group, list);
            if (semanticSeed != null)
            {
                group.SetFocused(semanticSeed);
                target = semanticSeed;
            }

            // A NO-OP UNLESS THE GAME'S ANSWER IS ACTUALLY BAD - same principle as NormalizeFocus.
            // v0.19.0 overrode the game on every panel and in _focusables order, which discards the
            // game's leftmost/topmost rule everywhere to fix two panels. Measured cost in TEST 30:
            // switching to the character creator started seeding 'Cancel and exit' (index 0) instead
            // of the gender item, so the default landing spot became the discard button.
            //
            // Two answers are genuinely bad and are the only ones replaced:
            //  - an INACTIVE element, which FocusDefaultFocusable will happily leave in place because
            //    it no-ops whenever anything already holds IsFocused. The stale registration
            //    described on CountFocusables produces exactly this.
            //  - a layout CONTAINER, whose label is every child joined together.
            // THREE bad answers, not two. The subsumed test was added to FindLeafFocusable,
            // ContainsAnother and CountFocusables but missed HERE, and TEST 33 caught it: the panel
            // announced "Personality, 27 items, unlabeled Jauge(Clone)" because a gauge is active
            // and contains nothing, so it passed the container test unchallenged. That is the
            // reason all focus-selection paths must apply the same eligibility rules.
            else if (target == null
                || !target.gameObject.activeInHierarchy
                || FocusNavigator.ShouldSkip(target)
                || ContainsAnother(list, target))
            {
                Focusable leaf = FindLeafFocusable(group, list);
                if (leaf != null)
                {
                    group.SetFocused(leaf);
                    target = leaf;
                }
            }

            _cursor = target;
            _cursorGroup = target != null ? group : null;
        }

        internal static void CommitArrowFocus(FocusableGroup group, Focusable target)
        {
            // Two different controls may both say "Unknown Information". A real keyboard move
            // must remain audible; automatic repeated assignments still use normal deduplication.
            if (!ReferenceEquals(_cursor, target) || !ReferenceEquals(_cursorGroup, group))
                ScreenReader.ResetLastSpoken();
            _cursor = target;
            _cursorGroup = group;
            group.SetFocused(target);
        }

        private static Focusable FindSemanticSeed(FocusableGroup group, List<Focusable> list)
        {
            Focusable householdEntry = CreatorHouseholdHandler.GetEntry(group, list);
            if (householdEntry != null) return householdEntry;
            Focusable libraryEntry = HouseholdLibraryHandler.GetEntry(group, list);
            if (libraryEntry != null) return libraryEntry;
            Focusable settingsEntry = SettingsHandler.GetEntry(group, list);
            if (settingsEntry != null) return settingsEntry;
            Focusable logicalEntry = LogicalFocusNavigator.GetEntry(group, list);
            if (logicalEntry != null) return logicalEntry;
            var creator = group as UICharacterCreator;
            Focusable currentCreatorTab = CharacterCreatorHandler.GetCurrentTopLevelTab(creator);
            if (currentCreatorTab != null && list != null && list.Contains(currentCreatorTab))
            {
                return currentCreatorTab;
            }

            UICharacterCreator owner = group.GetComponentInParent<UICharacterCreator>();
            if (owner != null && ReferenceEquals(group, owner.UICharacterCreatorPersonality))
            {
                return CharacterCreatorHandler.GetPersonalityEntry(owner, list);
            }

            var skills = group as UISkillsInProgressAndUpcomingEvents;
            UIGoalItem goal = skills?.UIGoalItem;
            if (goal != null && goal.gameObject.activeInHierarchy
                && list != null && list.Contains(goal))
            {
                return goal;
            }

            return null;
        }

        /// <summary>
        /// True when another active focusable of the group sits underneath this one.
        /// </summary>
        /// <remarks>
        /// The structural test for "this is a layout container, not a control". Measured in TEST 29
        /// and 30: UICharacterCreatorDecalPositions and UICharacterCreatorContextualListEquipments
        /// are UIListItemBase, so they are focusable in their own right while also holding the seven
        /// section buttons and eleven equipment items. Focusing one announces every child joined
        /// together - "Test, TattooMmeCrow, TattooCitrus, ..." - before a single real item.
        /// </remarks>
        private static bool ContainsAnother(List<Focusable> list, Focusable candidate)
        {
            if (list == null || candidate == null)
            {
                return false;
            }

            for (int i = 0; i < list.Count; i++)
            {
                Focusable other = list[i];
                if (other == null || ReferenceEquals(other, candidate)
                    || !other.gameObject.activeInHierarchy
                    || FocusNavigator.ShouldSkip(other))
                {
                    continue;
                }

                if (other.transform.IsChildOf(candidate.transform))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Picks the active leaf focusable nearest the group's top-left corner.
        /// </summary>
        /// <remarks>
        /// Ordered by position rather than by registration order, so it agrees with the game's own
        /// FocusableGroup.SetDefaultFocused rule (leftmost, then topmost) wherever both apply. Only
        /// the container exclusion is the mod's.
        /// </remarks>
        /// <param name="group">The group being seeded.</param>
        /// <param name="list">Its registered focusables.</param>
        /// <returns>A leaf focusable, or null when none qualifies.</returns>
        private static Focusable FindLeafFocusable(FocusableGroup group, List<Focusable> list)
        {
            if (list == null)
            {
                return null;
            }

            Focusable best = null;
            float bestScore = float.MaxValue;

            for (int i = 0; i < list.Count; i++)
            {
                Focusable candidate = list[i];
                if (candidate == null || candidate.IsShoulderButton
                    || !candidate.gameObject.activeInHierarchy
                    || FocusNavigator.ShouldSkip(candidate)
                    || ContainsAnother(list, candidate))
                {
                    continue;
                }

                // Leftmost wins, topmost breaks the tie - the same ordering SetDefaultFocused uses.
                Vector3 pos = candidate.transform.position;
                float score = pos.x - pos.y * 0.001f;

                if (score < bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            return best;
        }

        /// <summary>
        /// Strips Unity's instantiation suffix. The rest of the name is a developer identifier and
        /// is spoken as-is: it is the only name these panels have, and inventing friendlier ones
        /// would mean guessing at what the panel contains.
        /// </summary>
        private static string CleanPanelName(string name)
        {
            const string cloneSuffix = "(Clone)";
            return name != null && name.EndsWith(cloneSuffix, StringComparison.Ordinal)
                ? name.Substring(0, name.Length - cloneSuffix.Length).Trim()
                : name;
        }

        /// <summary>Returns the same spoken panel name for switching and context reports.</summary>
        internal static string GetPanelDisplayName(FocusableGroup group)
        {
            if (group == null)
            {
                return null;
            }

            string name = CharacterCreatorHandler.GetPanelDisplayName(group);
            if (string.IsNullOrEmpty(name))
            {
                name = GetSemanticPanelName(group);
            }

            return string.IsNullOrEmpty(name) ? CleanPanelName(group.name) : name;
        }

        /// <summary>Names recurring HUD groups by the player-facing system they contain.</summary>
        private static string GetSemanticPanelName(FocusableGroup group)
        {
            if (group is UIPremadeHouseholdSelection) return Loc.Get("library_panel");
            string settingsName = SettingsHandler.GetPanelLabel(group);
            if (settingsName != null) return settingsName;
            if (group.GetComponentInParent<UIStoryCards>() != null)
            {
                return Loc.Get("panel_story_cards");
            }
            if (group is UIWants2) return Loc.Get("panel_active_wants");
            if (group is UINeeds) return Loc.Get("panel_needs");
            if (group is UIEmotions2) return Loc.Get("panel_emotions");
            if (group is UIThoughtBubbles) return Loc.Get("panel_thought_bubbles");
            if (group is UICharacterSubMenuBar) return Loc.Get("panel_character_tabs");
            if (group is UITime) return Loc.Get("panel_time_controls");
            if (group is UIGameBar) return Loc.Get("panel_game_controls");
            if (group is UICharacters) return Loc.Get("panel_household");
            if (group is UIInteractions) return Loc.Get("panel_interactions");
            if (group is UIOfferedWants) return Loc.Get("panel_offered_wants");
            if (group is UIThoughts) return Loc.Get("panel_thoughts");
            if (group is UISkillsInProgressAndUpcomingEvents)
            {
                return Loc.Get("panel_progress_events");
            }

            return null;
        }

        /// <summary>
        /// Activates the focused element on Enter, mirroring the game's gamepad activation.
        /// </summary>
        /// <remarks>
        /// Enter is unbound in the game's InputActionAsset (game-api.md section 3), so this adds a
        /// binding rather than overriding one. ClickDown on press and ClickUp on release matches
        /// FocusableGroup.NavigateMenuDoClick exactly.
        ///
        /// KNOWN RISK, measured not assumed: Focusable.ClickUp only raises OnClick when
        /// `_isHovered || (_justEnabled &amp;&amp; IsPressed)`. `_justEnabled` is set in OnEnable and
        /// cleared by ANY write to IsMouseHovered, so once the mouse has passed over an element it
        /// can only be activated while the cursor is still on it. TEST 11 confirmed activation
        /// works in practice (`_justEnabled=True` throughout), but the hazard is real, so every
        /// attempt still logs the deciding values rather than failing silently.
        ///
        /// PRESS AND RELEASE ARE ONE GESTURE, and must be paired. An Enter press and its release land
        /// on DIFFERENT frames, so anything that changes which handler owns the input between them
        /// splits the gesture in half.
        ///
        /// Measured in TEST 36, reported from play as "Enter on Close bounces back to First Name and
        /// the window never closes": pressing Enter on the mod's Close entry hid the Name Details
        /// window, which flipped `NameEntryHandler.OwnsInput` to false — so on the NEXT frame this
        /// handler was live again and delivered the orphaned ClickUp to whatever the character
        /// creator still had focused, namely `ButtonEditNameDetails`. That is wired to
        /// `ToggleEditNameDetails`, which re-`Show()`s the window it had just closed. The panel then
        /// rebuilt and reseeded to its first entry, producing the bounce.
        ///
        /// The orphaned release is not harmless in general: `ClickUp` raises OnClick whenever
        /// `_isHovered` is true, and the mod's own hover workaround forces exactly that for
        /// mod-initiated activation. So a stray release genuinely activates whatever is focused.
        ///
        /// Recording the element that was pressed also fixes a second latent bug: the release used to
        /// re-read `GetFocused`, so a focus change mid-gesture would have clicked the wrong control.
        /// </remarks>
        private static void HandleActivation(FocusableGroup group)
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null || group == null)
            {
                return;
            }

            bool pressed = keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame;
            bool released = keyboard.enterKey.wasReleasedThisFrame || keyboard.numpadEnterKey.wasReleasedThisFrame;

            if (!pressed && !released)
            {
                return;
            }

            if (pressed)
            {
                _pressedOccupationOffer = false;
                _pressedCreatorSemantic = false;
                List<Focusable> list = GetFocusables(group);
                Focusable gameFocused = group.GetFocused(false);
                Focusable focused = list != null && IsCursorUsable(group, list)
                    ? _cursor
                    : gameFocused;

                // UICharacterCreatorGenderItem writes its selected state into IsFocused every
                // frame. GetFocused therefore returns gender even after navigation and speech have
                // moved elsewhere. Activation must use the same cursor authority as navigation.
                if (focused != null && gameFocused != null
                    && !ReferenceEquals(focused, gameFocused))
                {
                    Plugin.Log.LogInfo(
                        $"[Nav] Enter follows cursor '{UiLabelReader.GetLabelOrFallback(focused)}' "
                      + $"instead of game-pinned '{UiLabelReader.GetLabelOrFallback(gameFocused)}'.");
                }

                focused = ResolveActivationTarget(focused);
                if (focused != null && !FocusNavigator.ShouldSkip(focused))
                {
                    if (SettingsHandler.IsValueEntryTarget(focused))
                    {
                        _pressedFocusable = focused;
                    }
                    else if (TryHandleReadOnlyActivation(focused))
                    {
                        _pressedFocusable = null;
                    }
                    else if (OccupationHandler.IsOfferActivationTarget(focused))
                    {
                        // The offer prefab registers another Focusable at the exact card position.
                        // ClickUp can therefore fire on presentation without ever reaching the
                        // UIOccupationOfferItem callback. Pair this Enter normally, then invoke that
                        // public native callback once on release.
                        _pressedFocusable = focused;
                        _pressedOccupationOffer = true;
                        Plugin.Log.LogInfo(
                            $"[Nav] Enter armed native job application for "
                          + $"'{UiLabelReader.GetLabelOrFallback(focused)}'.");
                    }
                    else if (CharacterCreatorHandler.IsSemanticActivationTarget(focused))
                    {
                        // These creator rows share coordinates with their nested pointer control.
                        // A generic ClickUp reaches the presentation shell but not the public native
                        // callback that actually opens or commits the choice.
                        _pressedFocusable = focused;
                        _pressedCreatorSemantic = true;
                        Plugin.Log.LogInfo(
                            $"[Nav] Enter armed native creator choice for "
                          + $"'{UiLabelReader.GetLabelOrFallback(focused)}'.");
                    }
                    else
                    {
                        PressFocusable(focused);
                        _pressedFocusable = focused;
                    }
                }
            }

            if (!released)
            {
                return;
            }

            // Deliberately NOT an else-if. A fast tap can put wasPressedThisFrame and
            // wasReleasedThisFrame on the SAME frame, and returning after the press would leave the
            // click half-finished - the element pressed but never activated, with the stale press
            // then completing against whatever the next release happened to be.
            //
            // RELEASE only ever completes a press THIS handler made. An orphaned release - one whose
            // press another owner handled, or that arrived after the pressed element went away - is
            // dropped rather than delivered to whatever is focused now.
            Focusable target = _pressedFocusable;
            bool occupationOffer = _pressedOccupationOffer;
            bool creatorSemantic = _pressedCreatorSemantic;
            _pressedFocusable = null;
            _pressedOccupationOffer = false;
            _pressedCreatorSemantic = false;

            // Unity's overloaded == also catches a destroyed object, not just a null reference.
            if (target == null || !target.gameObject.activeInHierarchy)
            {
                return;
            }

            string labelBefore = UiLabelReader.GetLabelOrFallback(target);
            FocusableGroup targetGroup = target.GetComponentInParent<FocusableGroup>();
            UIWindow targetWindow = target.GetComponentInParent<UIWindow>();
            HashSet<FocusableGroup> visibleGroupsBefore =
                CaptureVisibleFocusableGroups(targetWindow);
            int interactionSubmenuDepth = -1;
            ulong interactionSubmenuGroupGuid = 0UL;
            UIGenericSkinChoices skinChoices =
                target.GetComponentInParent<UIGenericSkinChoice>() != null
                    ? target.GetComponentInParent<UIGenericSkinChoices>()
                    : null;
            var interactionItem = target as UIInteractionsListItem;
            if (interactionItem?.InteractionGroupItem != null
                && interactionItem.InteractionGroupItem.Type == InteractionItemType.Group)
            {
                UIInteractionsList owningList =
                    target.GetComponentInParent<UIInteractionsList>();
                if (owningList != null)
                {
                    interactionSubmenuDepth = owningList.ListDepth + 1;
                    interactionSubmenuGroupGuid =
                        interactionItem.InteractionGroupItem.Group;
                }
            }

            if (SettingsHandler.IsValueEntryTarget(target))
            {
                SettingsHandler.ActivateValueEntry(target);
                return;
            }
            if (occupationOffer)
            {
                OccupationHandler.TryActivateOffer(target);
            }
            else if (creatorSemantic)
            {
                string result;
                CharacterCreatorHandler.TryActivateSemantic(target, out result);
                if (!string.IsNullOrWhiteSpace(result))
                {
                    AnnouncementCoordinator.PublishLatestImportant(
                        "creator-choice", result);
                }
            }
            else
            {
                ReleaseFocusable(target);
            }

            _pendingActivationOutcome = new PendingActivationOutcome
            {
                Target = target,
                Group = targetGroup,
                Window = targetWindow,
                VisibleGroupsBefore = visibleGroupsBefore,
                LabelBefore = labelBefore,
                FramesRemaining = 3,
                InteractionSubmenuDepth = interactionSubmenuDepth,
                InteractionSubmenuGroupGuid = interactionSubmenuGroupGuid,
                SkinChoices = skinChoices
            };
        }

        private static bool TryHandleReadOnlyActivation(Focusable target)
        {
            if (target == null || HasDirectActivation(target))
            {
                return false;
            }

            UIEmotionsItem2 emotion = target.GetComponent<UIEmotionsItem2>();
            if (emotion != null
                && (emotion.WantsAvailableNotification == null
                    || !emotion.WantsAvailableNotification.activeInHierarchy))
            {
                SpeakReadOnlyResult(target, "emotion_no_reaction");
                return true;
            }

            if (target.GetComponent<UIGoalItemLine>() != null)
            {
                SpeakReadOnlyResult(target, "goal_objective_read_only");
                return true;
            }

            if (target.GetComponent<UIGoalItem>() != null)
            {
                SpeakReadOnlyResult(target, "goal_summary_read_only");
                return true;
            }

            return false;
        }

        private static bool HasDirectActivation(Focusable target)
        {
            if (target.GetComponent<ButtonAction>() != null)
            {
                return true;
            }

            ParaButton button = target.GetComponent<ParaButton>();
            return button?.ButtonClick != null
                && button.ButtonClick.GetPersistentEventCount() > 0;
        }

        private static void SpeakReadOnlyResult(Focusable target, string key)
        {
            string message = Loc.Get(key);
            Plugin.Log.LogInfo(
                $"[Nav] Enter handled as read-only on {target.GetType().Name} "
              + $"'{UiLabelReader.GetLabelOrFallback(target)}': {message}");
            ScreenReader.Speak(message);
        }

        /// <summary>
        /// Reports a concrete result of the last Enter press after the game has had time to update.
        /// </summary>
        /// <remarks>
        /// This is intentionally change-driven. It does not announce that Enter exists or repeat a
        /// control's unchanged label. TEST 55 supplied both measured cases: Pause changed from not
        /// selected to selected without speech, while opening a Story Card pack revealed a different
        /// three-item child panel but left arrows on the old one-item pack row.
        /// </remarks>
        private static void ObservePendingActivationOutcome(UIWindow gameDrivenWindow)
        {
            PendingActivationOutcome pending = _pendingActivationOutcome;
            if (pending == null)
            {
                return;
            }

            // A newly opened modal has its own window/focus announcement. Do not describe a stale
            // control behind it as though that were the result the user is now looking at.
            if (pending.Window != null
                && (!pending.Window.IsVisible
                    || (gameDrivenWindow != null
                        && !ReferenceEquals(gameDrivenWindow, pending.Window))))
            {
                _pendingActivationOutcome = null;
                return;
            }

            // UIInteractions submenus do not create a new FocusableGroup. AddList places another
            // UIInteractionsList inside the same window/group, so the generic "new child panel"
            // observer cannot see it. Handle the source-proven Group path before the parent row's
            // IsOn change can be mistaken for the activation result.
            if (pending.InteractionSubmenuDepth >= 0)
            {
                if (FocusOpenedInteractionSubmenu(pending))
                {
                    _pendingActivationOutcome = null;
                    return;
                }

                pending.FramesRemaining--;
                if (pending.FramesRemaining > 0)
                {
                    return;
                }

                Plugin.Log.LogInfo(
                    $"[State/activation] '{pending.LabelBefore}' expected interaction submenu "
                  + $"depth={pending.InteractionSubmenuDepth}, group="
                  + $"{pending.InteractionSubmenuGroupGuid}, but no choices registered.");
                ScreenReader.Speak(Loc.Get(
                    "interaction_submenu_empty", pending.LabelBefore));
                _pendingActivationOutcome = null;
                return;
            }

            // Recipe and other generic skin choices reveal their next actions inside the same
            // UIWindow and FocusableGroup. There is no new panel for the generic observer to see.
            if (pending.SkinChoices != null)
            {
                if (FocusOpenedSkinChoiceDetails(pending))
                {
                    _pendingActivationOutcome = null;
                    return;
                }

                pending.FramesRemaining--;
                if (pending.FramesRemaining > 0)
                {
                    return;
                }

                Plugin.Log.LogInfo(
                    $"[State/activation] '{pending.LabelBefore}' did not expose an available "
                  + "skin-choice action after settling.");
                ScreenReader.Speak(Loc.Get(
                    "skin_choice_no_actions", pending.LabelBefore));
                _pendingActivationOutcome = null;
                return;
            }

            if (pending.Window != null && pending.Group != null)
            {
                FocusableGroup revealed = FindNewForegroundGroup(
                    pending.Window, pending.VisibleGroupsBefore);
                if (revealed != null && !ReferenceEquals(revealed, pending.Group)
                    && _candidates.Contains(revealed)
                    && CountFocusables(revealed) > CountFocusables(pending.Group))
                {
                    _selectedGroup = revealed;
                    _selectionIsExplicit = false;
                    _selectionIsTutorialDirected = false;
                    _selectionIsForegroundDirected = true;
                    FocusPanelAndAnnounce(revealed, -1, null);
                    Plugin.Log.LogInfo(
                        $"[State/activation] '{pending.LabelBefore}' revealed panel "
                      + $"'{GetPanelDisplayName(revealed)}' with {CountFocusables(revealed)} items.");
                    _pendingActivationOutcome = null;
                    return;
                }
            }

            if (pending.Target != null && pending.Target.gameObject.activeInHierarchy
                && pending.Group != null && _candidates.Contains(pending.Group))
            {
                string labelAfter = UiLabelReader.GetLabelOrFallback(pending.Target);
                if (!string.IsNullOrEmpty(labelAfter)
                    && !string.Equals(pending.LabelBefore, labelAfter, StringComparison.Ordinal))
                {
                    Plugin.Log.LogInfo(
                        $"[State/activation] '{pending.LabelBefore}' -> '{labelAfter}'.");
                    ScreenReader.Speak(labelAfter);
                    _pendingActivationOutcome = null;
                    return;
                }
            }

            pending.FramesRemaining--;
            if (pending.FramesRemaining > 0)
            {
                return;
            }

            // A grouped panel tab can intentionally ignore a press on the already-open panel.
            // ParaButton still plays its generic click sound first, which otherwise sounds like an
            // undisclosed popup. Confirm only this typed semantic no-op after the normal settlement
            // checks have proved that no label or panel changed. Ordinary unchanged controls stay
            // silent, preserving the change-driven rule above.
            if (pending.Target != null && pending.Target.gameObject.activeInHierarchy
                && UiLabelReader.TryGetExclusivePanelTabState(
                    pending.Target, out bool remainsCurrentPanel)
                && remainsCurrentPanel)
            {
                string panelName = UiLabelReader.GetLabel(pending.Target);
                string message = string.IsNullOrEmpty(panelName)
                    ? Loc.Get("panel_still_current")
                    : Loc.Get("panel_still_current_named", panelName);
                Plugin.Log.LogInfo(
                    $"[State/activation] '{pending.LabelBefore}' remained the current panel.");
                ScreenReader.Speak(message);
                _pendingActivationOutcome = null;
                return;
            }

            Plugin.Log.LogInfo(
                $"[State/activation] '{pending.LabelBefore}' produced no changed label or newly "
              + "revealed panel after settling.");
            _pendingActivationOutcome = null;
        }

        /// <summary>
        /// Focuses the first row of the exact nested interaction list opened by a Group row.
        /// </summary>
        /// <remarks>
        /// `UIInteractionsListItem.OnListItemClicked` calls `UIInteractions.AddList` for a Group,
        /// and every row in that new list still registers directly with the one UIInteractions
        /// window. There is therefore no window or panel transition for the ordinary observers to
        /// announce. The outer `UIInteractionLists` model retains depth and group identity, so the
        /// new list can be selected without guessing from coordinates or English labels.
        /// </remarks>
        private static bool FocusOpenedInteractionSubmenu(PendingActivationOutcome pending)
        {
            var interactions = pending?.Window as UIInteractions;
            if (interactions?.UIInteractionLists?.CurrentItems == null
                || pending.Group == null)
            {
                return false;
            }

            UIInteractionsList submenu = null;
            foreach (UIListItemBase item in interactions.UIInteractionLists.CurrentItems)
            {
                var candidate = item as UIInteractionsList;
                if (candidate != null && candidate.gameObject.activeInHierarchy
                    && candidate.ListDepth == pending.InteractionSubmenuDepth
                    && candidate.InteractionGroup != null
                    && candidate.InteractionGroup.GUID
                        == pending.InteractionSubmenuGroupGuid)
                {
                    submenu = candidate;
                    break;
                }
            }

            if (submenu?.UIListInteractionItems?.CurrentItems == null)
            {
                return false;
            }

            var rawRows = new List<Focusable>();
            foreach (UIListItemBase item in submenu.UIListInteractionItems.CurrentItems)
            {
                var row = item as Focusable;
                if (row != null && row.gameObject.activeInHierarchy
                    && !FocusNavigator.ShouldSkip(row))
                {
                    rawRows.Add(row);
                }
            }

            List<Focusable> rows = FocusNavigator.GetDistinctReachable(rawRows);
            if (rows.Count == 0)
            {
                return false;
            }

            Focusable first = rows[0];
            List<Focusable> windowFocusables = GetFocusables(pending.Group);
            if (windowFocusables == null || !windowFocusables.Contains(first))
            {
                // UIList can activate its pooled row one stage before Focusable.OnEnable registers
                // it with the window. Wait for the next settlement frame rather than announcing a
                // destination which the arrow navigator cannot yet reach.
                return false;
            }

            bool previousAnnounceFocus = FocusHandler.AnnounceFocus;
            FocusHandler.AnnounceFocus = false;
            try
            {
                _cursor = first;
                _cursorGroup = pending.Group;
                pending.Group.SetFocused(first);
            }
            finally
            {
                FocusHandler.AnnounceFocus = previousAnnounceFocus;
            }

            string firstLabel = UiLabelReader.GetLabelOrFallback(first);
            string message = rows.Count == 1
                ? Loc.Get(
                    "interaction_submenu_opened_one",
                    pending.LabelBefore,
                    firstLabel)
                : Loc.Get(
                    "interaction_submenu_opened_many",
                    pending.LabelBefore,
                    rows.Count,
                    firstLabel);
            Plugin.Log.LogInfo(
                $"[State/activation] '{pending.LabelBefore}' opened interaction submenu "
              + $"depth={pending.InteractionSubmenuDepth}, group="
              + $"{pending.InteractionSubmenuGroupGuid}, choices={rows.Count}, "
              + $"focused='{firstLabel}'.");
            ScreenReader.Speak(message);
            return true;
        }

        /// <summary>Focuses the first action revealed in a generic recipe/variant side panel.</summary>
        private static bool FocusOpenedSkinChoiceDetails(PendingActivationOutcome pending)
        {
            UIGenericSkinChoices window = pending?.SkinChoices;
            if (window == null || !window.IsVisible || window.SidePanel == null
                || !window.SidePanel.activeInHierarchy
                || window.ListSkinSelection?.CurrentItems == null
                || pending.Group == null)
            {
                return false;
            }

            var rawActions = new List<Focusable>();
            foreach (UIListItemBase item in window.ListSkinSelection.CurrentItems)
            {
                var action = item as UIGenericSkinChoiceButton;
                if (action != null && action.gameObject.activeInHierarchy
                    && action.Interactable && !FocusNavigator.ShouldSkip(action))
                {
                    rawActions.Add(action);
                }
            }

            List<Focusable> actions = FocusNavigator.GetDistinctReachable(rawActions);
            if (actions.Count == 0)
            {
                return false;
            }

            Focusable first = actions[0];
            List<Focusable> windowFocusables = GetFocusables(pending.Group);
            if (windowFocusables == null || !windowFocusables.Contains(first))
            {
                // The side panel can become active one frame before its pooled rows register.
                return false;
            }

            bool previousAnnounceFocus = FocusHandler.AnnounceFocus;
            FocusHandler.AnnounceFocus = false;
            try
            {
                _cursor = first;
                _cursorGroup = pending.Group;
                pending.Group.SetFocused(first);
            }
            finally
            {
                FocusHandler.AnnounceFocus = previousAnnounceFocus;
            }

            string selected = ScreenReader.NormalizeForOutput(
                window.SelectedName?.Text?.GetParsedText());
            string chance = window.SuccessChanceContainer != null
                && window.SuccessChanceContainer.activeInHierarchy
                    ? ScreenReader.NormalizeForOutput(
                        window.SuccessChanceLabel?.GetParsedText())
                    : null;
            string firstLabel = UiLabelReader.GetLabelOrFallback(first);

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(selected))
            {
                parts.Add(Loc.Get("skin_choice_selected", selected));
            }
            if (!string.IsNullOrWhiteSpace(chance))
            {
                parts.Add(Loc.Get("skin_choice_chance", chance));
            }
            parts.Add(actions.Count == 1
                ? Loc.Get("skin_choice_action_one", firstLabel)
                : Loc.Get("skin_choice_actions", actions.Count, firstLabel));

            string message = string.Join(" ", parts.ToArray());
            Plugin.Log.LogInfo(
                $"[State/activation] '{pending.LabelBefore}' exposed {actions.Count} "
              + $"side-panel action(s); focused '{firstLabel}'.");
            ScreenReader.Speak(message);
            return true;
        }

        /// <summary>
        /// Captures the active, populated panels which already existed before an activation. A
        /// sibling HUD panel is not an activation result merely because it is larger than the
        /// panel containing the clicked control.
        /// </summary>
        private static HashSet<FocusableGroup> CaptureVisibleFocusableGroups(UIWindow window)
        {
            var result = new HashSet<FocusableGroup>();
            if (window == null)
            {
                return result;
            }

            foreach (FocusableGroup group in
                window.GetComponentsInChildren<FocusableGroup>(false))
            {
                if (group != null && group.gameObject.activeInHierarchy
                    && CountFocusables(group) > 0)
                {
                    result.Add(group);
                }
            }

            return result;
        }

        /// <summary>
        /// Returns the largest active child panel which became populated after activation.
        /// Pre-existing siblings are deliberately excluded even if they are the window's largest
        /// focus group.
        /// </summary>
        private static FocusableGroup FindNewForegroundGroup(
            UIWindow window, HashSet<FocusableGroup> visibleGroupsBefore)
        {
            if (window == null)
            {
                return null;
            }

            FocusableGroup best = null;
            int bestCount = 0;
            foreach (FocusableGroup group in
                window.GetComponentsInChildren<FocusableGroup>(false))
            {
                if (group == null || !group.gameObject.activeInHierarchy
                    || visibleGroupsBefore?.Contains(group) == true)
                {
                    continue;
                }

                int count = CountFocusables(group);
                if (count > bestCount)
                {
                    best = group;
                    bestCount = count;
                }
            }

            return best;
        }

        /// <summary>
        /// Maps a focusable presentation row to the child control that owns its actual action.
        /// UINewspaperArticleItem inherits a clickable list-item base but does not implement an
        /// article action on that parent; ButtonAction is the source-wired target.
        /// </summary>
        private static Focusable ResolveActivationTarget(Focusable target)
        {
            if (target == null)
            {
                return null;
            }

            var article = target.GetComponent<UINewspaperArticleItem>();
            GameObject actionObject = article?.ButtonAction;
            if (actionObject == null || !actionObject.activeInHierarchy)
            {
                return target;
            }

            Focusable action = actionObject.GetComponent<Focusable>();
            if (action == null || !action.gameObject.activeInHierarchy)
            {
                return target;
            }

            Plugin.Log.LogInfo(
                $"[Nav] Redirecting newspaper article activation from "
              + $"'{UiLabelReader.GetLabelOrFallback(target)}' to its active action button.");
            return action;
        }

        /// <summary>
        /// Begins a keyboard activation on a specific game focusable using the game's own
        /// <c>ClickDown</c> path.
        /// </summary>
        /// <param name="target">The live focusable to press.</param>
        public static void PressFocusable(Focusable target)
        {
            if (target == null || !target.gameObject.activeInHierarchy)
            {
                return;
            }

            LogActivationPrediction(target);
            ParalivesAccess.Probes.CreatorUiDiagnostics.Activation(target, "press");
            target.ClickDown();
        }

        /// <summary>
        /// Completes a keyboard activation on a specific game focusable using the game's own
        /// <c>ClickUp</c> path and the already-approved hover-guard patch.
        /// </summary>
        /// <param name="target">The focusable previously passed to <see cref="PressFocusable"/>.</param>
        public static void ReleaseFocusable(Focusable target)
        {
            if (target == null || !target.gameObject.activeInHierarchy)
            {
                return;
            }

            _modInitiatedActivation = true;
            try
            {
                ParalivesAccess.Probes.CreatorUiDiagnostics.Activation(target, "release");
                target.ClickUp(PointerEventData.InputButton.Left);
            }
            finally
            {
                _modInitiatedActivation = false;
            }
        }

        /// <summary>
        /// Cancels and completes a pending keyboard press so a handler can close without leaving
        /// the game focusable in its pressed state or firing its action.
        /// </summary>
        /// <param name="target">The focusable whose press should be cancelled.</param>
        public static void CancelFocusablePress(Focusable target)
        {
            if (target == null || !target.gameObject.activeInHierarchy)
            {
                return;
            }

            target.IsClickCanceled = true;
            ReleaseFocusable(target);
        }

        /// <summary>The element this handler pressed, awaiting its matching release.</summary>
        private static Focusable _pressedFocusable;
        private static bool _pressedOccupationOffer;
        private static bool _pressedCreatorSemantic;

        private static void LogTargetChange(FocusableGroup target, UIWindow gameDrivenWindow)
        {
            if (target == _lastLoggedTarget)
            {
                return;
            }

            _lastLoggedTarget = target;

            if (target == null)
            {
                Plugin.Log.LogInfo("[Nav] No navigable panel found.");
                return;
            }

            Plugin.Log.LogInfo(
                $"[Nav] Active panel '{target.name}' ({target.GetType().Name}), "
              + $"focusables={CountFocusables(target)}, of {_candidates.Count} on screen, "
              + $"drivenBy={(ReferenceEquals(target, gameDrivenWindow) && !HasUiPanelSelection ? "game" : "mod")}.");
        }

        private static void LogActivationPrediction(Focusable focused)
        {
            ResolveReflection();

            object hovered = _isHoveredField?.GetValue(focused);
            object justEnabled = _justEnabledField?.GetValue(focused);
            bool willFire = (hovered as bool? ?? false) || (justEnabled as bool? ?? false);

            Plugin.Log.LogInfo(
                $"[Nav] Enter on '{UiLabelReader.GetLabelOrFallback(focused)}': "
              + $"_isHovered={hovered}, _justEnabled={justEnabled}, "
              + $"OnClick expected to fire={willFire}.");
        }

        /// <summary>
        /// Counts the distinct focusables of a group that the user can actually reach.
        /// </summary>
        /// <remarks>
        /// ACTIVE AND POSITION-DEDUPLICATED, and that is a fix rather than a refinement. The
        /// registry is not trustworthy on its own: `UICharacterCreatorContextualListEquipments` and
        /// `UICharacterCreatorContextualListPresets` each declare a private OnDisable that never
        /// calls base, which HIDES the private Focusable.OnDisable - the only caller of
        /// UnregisterFocusable. So those two types register on enable and never unregister
        /// (game-api.md section 31).
        ///
        /// Measured in TEST 29: after leaving the Tattoos category the contextual list still held
        /// one focusable - an inactive GridUIEquipments(Clone) still carrying the tattoo list's
        /// run-on label. Counting it kept offering a ghost panel that read out the previous
        /// category's contents. Filtering on activeInHierarchy removes that stale entry.
        ///
        /// TEST 40 found the other failure mode: the body diagram registered one slider plus 22
        /// coincident handle pairs. Directional navigation correctly reduced those 45 registrations
        /// to 23 stops, but this count still announced 45. Reusing FocusNavigator's positional rule
        /// makes panel selection, spoken totals and actual navigation describe the same controls.
        /// v0.39.35 keeps that rule in the creator; ordinary logical panels count actual control
        /// identities and current native list membership, independently of screen position.
        /// </remarks>
        /// <param name="group">The group to measure.</param>
        /// <returns>The number of distinct reachable controls, or 0 when unreadable.</returns>
        private static int CountFocusables(FocusableGroup group)
        {
            // UIStoryCards keeps every hand's UIStoryCardOffer active and changes only its public
            // UIContainer.IsVisible flag. GetComponentsInChildren therefore finds hidden hands and
            // their still-clickable cards. TEST 71 used the panel key to enter one of those hidden
            // hands and selected Big House while a different offer was actually on screen. Scope
            // this visibility rule to that source-proven structure; other UIContainer panels keep
            // their existing treatment.
            UIStoryCardOffer storyOffer = group != null
                ? group.GetComponentInParent<UIStoryCardOffer>()
                : null;
            if (storyOffer?.UIContainer != null && !storyOffer.UIContainer.IsVisible)
            {
                return 0;
            }

            List<Focusable> list = GetFocusables(group);
            return LogicalFocusNavigator.CountReachable(group, list);
        }

        /// <summary>
        /// Makes a group the active panel and announces it, as though the user had pressed a panel
        /// key. Used when the game reveals content the user asked for.
        /// </summary>
        /// <remarks>
        /// Reuses the panel-switch path deliberately rather than adding a second way to move focus:
        /// that path already mutes the focus announcement, seeds focus and speaks one combined
        /// utterance, and it is the behaviour TEST 21 and TEST 28 verified.
        ///
        /// The selection is marked EXPLICIT so the fullest-panel default cannot immediately pull the
        /// user back out. In the character creator the window itself holds 33 focusables against the
        /// contextual list's 22, so a defaulted selection would undo this on the very next frame.
        /// </remarks>
        /// <param name="group">The panel to move to.</param>
        /// <param name="displayName">A spoken name for it, or null to use the object name.</param>
        /// <returns>True when the panel was selected.</returns>
        public static bool SelectPanel(FocusableGroup group, string displayName)
        {
            if (group == null || CountFocusables(group) == 0)
            {
                return false;
            }

            _selectedGroup = group;
            _selectionIsExplicit = true;
            _selectionIsTutorialDirected = false;
            _selectionIsForegroundDirected = false;
            _suspendedSelections.Clear();

            FocusPanelAndAnnounce(group, -1, displayName);
            return true;
        }

        /// <summary>
        /// Silently moves navigation into a newly opened foreground window.
        /// </summary>
        /// <remarks>
        /// Window speech already names the popup and its choices, so a second focus utterance would
        /// be noise. The focus move itself is essential: TEST 54 showed a purchase prompt open while
        /// arrows and Enter continued operating a town-map lot icon behind it.
        /// </remarks>
        /// <param name="forceReseed">
        /// True only when the same composite window has opened a new presentation and its semantic
        /// starting point must replace retained native child focus.
        /// </param>
        internal static bool FocusForegroundWindow(UIWindow window, bool forceReseed = false)
        {
            if (window == null || !window.IsVisible)
            {
                return false;
            }

            FocusableGroup group = FindForegroundGroup(window);
            if (group == null)
            {
                return false;
            }

            bool alreadySelected = ReferenceEquals(_selectedGroup, group);
            if (!forceReseed && alreadySelected
                && (_selectionIsExplicit || _selectionIsTutorialDirected
                    || _selectionIsForegroundDirected))
            {
                return true;
            }

            if (!alreadySelected && _selectedGroup != null
                && (_selectionIsExplicit || _selectionIsTutorialDirected
                    || _selectionIsForegroundDirected))
            {
                _suspendedSelections.Push(new SuspendedSelection
                {
                    Group = _selectedGroup,
                    IsExplicit = _selectionIsExplicit,
                    IsTutorialDirected = _selectionIsTutorialDirected,
                    IsForegroundDirected = _selectionIsForegroundDirected
                });
            }

            _selectedGroup = group;
            _selectionIsExplicit = false;
            _selectionIsTutorialDirected = false;
            _selectionIsForegroundDirected = true;

            bool previousAnnounceFocus = FocusHandler.AnnounceFocus;
            FocusHandler.AnnounceFocus = false;
            try
            {
                SeedFocus(group);
            }
            finally
            {
                FocusHandler.AnnounceFocus = previousAnnounceFocus;
            }

            Plugin.Log.LogInfo(
                $"[Nav] Foreground focus moved to '{group.name}' for {window.GetType().Name}.");
            return true;
        }

        private static FocusableGroup FindForegroundGroup(UIWindow window)
        {
            if (CountFocusables(window) > 0)
            {
                return window;
            }

            FocusableGroup best = null;
            int bestCount = 0;
            foreach (FocusableGroup group in window.GetComponentsInChildren<FocusableGroup>(false))
            {
                if (group == null || !group.gameObject.activeInHierarchy)
                {
                    continue;
                }

                int count = CountFocusables(group);
                if (count > bestCount)
                {
                    best = group;
                    bestCount = count;
                }
            }

            return best;
        }

        /// <summary>
        /// Silently guides the shared cursor to a panel revealed by the tutorial.
        /// </summary>
        /// <param name="group">The ordinary game panel to navigate.</param>
        /// <returns>True when the panel contains a reachable control and was selected.</returns>
        internal static bool FocusForTutorial(FocusableGroup group)
        {
            if (group == null || !group.gameObject.activeInHierarchy
                || CountFocusables(group) == 0)
            {
                return false;
            }

            _selectedGroup = group;
            _selectionIsExplicit = false;
            _selectionIsTutorialDirected = true;
            _selectionIsForegroundDirected = false;

            bool previousAnnounceFocus = FocusHandler.AnnounceFocus;
            FocusHandler.AnnounceFocus = false;
            try
            {
                SeedFocus(group);
            }
            finally
            {
                FocusHandler.AnnounceFocus = previousAnnounceFocus;
            }

            Plugin.Log.LogInfo(
                $"[Nav] Tutorial guided focus to panel '{group.name}'.");
            return true;
        }

        /// <summary>
        /// Silently guides the shared cursor to an exact ordinary game control.
        /// </summary>
        /// <param name="target">The control the current tutorial sentence describes.</param>
        /// <returns>True when its owning panel accepted focus.</returns>
        internal static bool FocusForTutorial(Focusable target)
        {
            if (target == null || !target.gameObject.activeInHierarchy
                || FocusNavigator.ShouldSkip(target))
            {
                return false;
            }

            FocusableGroup group = target.GetComponentInParent<FocusableGroup>();
            if (group == null)
            {
                return false;
            }
            List<Focusable> focusables = GetFocusables(group);
            if (focusables == null || !focusables.Contains(target))
            {
                return false;
            }

            _selectedGroup = group;
            _selectionIsExplicit = false;
            _selectionIsTutorialDirected = true;
            _selectionIsForegroundDirected = false;

            bool previousAnnounceFocus = FocusHandler.AnnounceFocus;
            FocusHandler.AnnounceFocus = false;
            try
            {
                _cursor = target;
                _cursorGroup = group;
                group.SetFocused(target);
            }
            finally
            {
                FocusHandler.AnnounceFocus = previousAnnounceFocus;
            }

            Plugin.Log.LogInfo(
                $"[Nav] Tutorial guided focus to '{UiLabelReader.GetLabelOrFallback(target)}' "
              + $"in panel '{group.name}'.");
            return true;
        }

        /// <summary>
        /// Releases focus installed by <see cref="FocusForTutorial(FocusableGroup)"/> without
        /// changing a panel the user selected with the panel keys.
        /// </summary>
        internal static void ClearTutorialFocus()
        {
            if (!_selectionIsTutorialDirected)
            {
                return;
            }

            _selectionIsTutorialDirected = false;
            if (!_selectionIsExplicit && !_selectionIsForegroundDirected)
            {
                _selectedGroup = null;
            }
        }

        private static void ResolveReflection()
        {
            if (_reflectionResolved)
            {
                return;
            }

            _reflectionResolved = true;

            _visibleWindowsField = AccessTools.Field(typeof(UIManager), "_visibleWindows");
            _focusablesField = AccessTools.Field(typeof(FocusableGroup), "_focusables");
            _isHoveredField = AccessTools.Field(typeof(Focusable), "_isHovered");
            _justEnabledField = AccessTools.Field(typeof(Focusable), "_justEnabled");
            _inputVectorSmoothValueField = AccessTools.Field(
                typeof(InputVector2), "<SmoothValue>k__BackingField");

            if (_visibleWindowsField == null || _focusablesField == null)
            {
                Plugin.Log.LogError(
                    "MenuNavigationHandler could not resolve the game's private UI fields "
                  + $"(_visibleWindows={_visibleWindowsField != null}, "
                  + $"_focusables={_focusablesField != null}). "
                  + "Navigation will not work - re-check docs/game-api.md section 4.");
            }
            if (_inputVectorSmoothValueField == null)
            {
                Plugin.Log.LogError(
                    "Could not resolve InputVector2.SmoothValue. Arrow camera suppression will "
                  + "not run; re-check the current game input wrapper.");
            }
        }
    }
}
