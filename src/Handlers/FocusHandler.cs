using System;
using HarmonyLib;
using ParalivesAccess.Speech;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>
    /// Announces UI elements as they gain focus or mouse hover.
    /// </summary>
    /// <remarks>
    /// Hooks the game's existing focus system rather than building a parallel one. See
    /// docs/game-api.md section 4: Focusable/FocusableGroup already implement spatial navigation
    /// for gamepad support, so this handler only has to voice what the game already tracks.
    ///
    /// WHY THE PROPERTY SETTER AND NOT OnFocusGained:
    /// Focusable.OnFocusGained is protected virtual and ParaButton overrides it. A Harmony patch
    /// on the base method would not intercept calls that dispatch to the override, so ParaButton —
    /// the only direct subclass, and the one that matters — would be missed. The IsFocused setter
    /// is non-virtual and every focus change routes through it, so patching it catches all
    /// subclasses uniformly.
    ///
    /// The setter is entered on every assignment, INCLUDING redundant ones that assign the value it
    /// already holds. The prefix therefore compares the incoming value against the current one and
    /// flags only a genuine false -> true transition, mirroring the game's own
    /// `if (_isFocused != value)` guard.
    ///
    /// That comparison is not optional. An earlier version captured only the incoming value, so any
    /// re-assignment of `true` spoke again. UICharacterCreatorGenderItem.Refresh ends with
    /// `Button.IsFocused = base.IsOn;` and runs every frame, which made the character creator
    /// announce the selected gender ("M", "F", "NB") after every other announcement, drowning
    /// everything else out. The game itself was unaffected because its setter no-ops.
    ///
    /// Worth knowing: that same line shows the game also uses IsFocused as a VISUAL SELECTED state
    /// on some controls, not only as navigation focus.
    ///
    /// Known gap: Focusable.SetIsFocusedNoCallback writes the backing field directly and bypasses
    /// the setter. That is deliberate on the game's side ("NoCallback"), so it is left alone.
    /// </remarks>
    public static class FocusHandler
    {
        private static UIWindow _transitionHoverWindow;
        private static int _transitionHoverThroughFrame = -1;

        /// <summary>True to announce mouse hover in addition to focus.</summary>
        public static bool AnnounceHover { get; set; } = true;

        /// <summary>True to announce focus changes.</summary>
        public static bool AnnounceFocus { get; set; } = true;

        /// <summary>
        /// True to log every announcement at Info level so it appears in LogOutput.log.
        /// </summary>
        /// <remarks>
        /// These lines used LogDebug, which BepInEx does not write to disk at the default level, so
        /// no test could show what was actually spoken. That gap blocked diagnosing the character
        /// creator, where announcements were reported as being swallowed by a repeated "f". Set
        /// false once label quality is settled.
        /// </remarks>
        public static bool LogAnnouncements { get; set; } = true;

        /// <summary>Applies the Harmony patches for focus and hover announcements.</summary>
        /// <param name="harmony">Harmony instance owned by the plugin.</param>
        /// <returns>True when both patches were verified as applied.</returns>
        public static bool ApplyPatches(Harmony harmony)
        {
            bool focusOk = TryPatchSetter(
                harmony,
                propertyName: "IsFocused",
                prefixName: nameof(CaptureFocusTransition),
                postfixName: nameof(IsFocusedPostfix));

            bool hoverOk = TryPatchSetter(
                harmony,
                propertyName: "IsMouseHovered",
                prefixName: nameof(CaptureHoverTransition),
                postfixName: nameof(IsMouseHoveredPostfix));

            return focusOk && hoverOk;
        }

        private static bool TryPatchSetter(Harmony harmony, string propertyName, string prefixName, string postfixName)
        {
            try
            {
                var setter = AccessTools.PropertySetter(typeof(Focusable), propertyName);
                if (setter == null)
                {
                    Plugin.Log.LogError(
                        $"Could not find Focusable.{propertyName} setter. "
                      + "The game's UI code may have changed - re-check docs/game-api.md section 4.");
                    return false;
                }

                harmony.Patch(
                    setter,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(FocusHandler), prefixName)),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(FocusHandler), postfixName)));

                // Do not assume the patch took: SRE is unavailable on this runtime.
                bool applied = new System.Collections.Generic.List<System.Reflection.MethodBase>(
                    harmony.GetPatchedMethods()).Contains(setter);

                Plugin.Log.LogInfo($"Patched Focusable.{propertyName} setter, applied={applied}.");
                return applied;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Patching Focusable.{propertyName} threw: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Harmony prefix on the IsFocused setter: flags a genuine off -> on transition.
        /// </summary>
        /// <param name="__instance">The Focusable being assigned to.</param>
        /// <param name="value">The incoming value.</param>
        /// <param name="__state">True when this assignment actually turns focus on.</param>
        public static void CaptureFocusTransition(Focusable __instance, bool value, out bool __state)
        {
            // Read BEFORE the setter runs, so this is the previous state. Comparing here is what
            // stops per-frame re-assignments of an already-focused element from speaking.
            __state = value && __instance != null && !__instance.IsFocused;
        }

        /// <summary>
        /// Harmony prefix on the IsMouseHovered setter: flags a genuine off -> on transition.
        /// </summary>
        /// <param name="__instance">The Focusable being assigned to.</param>
        /// <param name="value">The incoming value.</param>
        /// <param name="__state">True when this assignment actually turns hover on.</param>
        public static void CaptureHoverTransition(Focusable __instance, bool value, out bool __state)
        {
            __state = value && __instance != null && !__instance.IsMouseHovered;
            if (__state) ParalivesAccess.Probes.CreatorUiDiagnostics.Focus(__instance, "hover entered");
        }

        /// <summary>Harmony postfix on Focusable.IsFocused.</summary>
        /// <param name="__instance">The Focusable whose focus state changed.</param>
        /// <param name="__state">True when focus genuinely turned on.</param>
        public static void IsFocusedPostfix(Focusable __instance, bool __state)
        {
            if (!__state || __instance == null)
            {
                return;
            }

            // Save rows are lazily initialized only while visible. Reveal the destination before
            // reading it; its label and activation still resolve from the model if presentation
            // needs another frame to catch up.
            ParalivesAccess.Probes.CreatorUiDiagnostics.Focus(__instance, "focus gained");
            SaveGameListHandler.OnFocusGained(__instance);
            SettingsHandler.OnFocusGained(__instance);
            HouseholdLibraryHandler.OnFocusGained(__instance);

            if (!AnnounceFocus || FocusNavigator.ShouldSkip(__instance))
            {
                return;
            }

            if (!AgreesWithModCursor(__instance))
            {
                return;
            }

            Announce(__instance, "focus");
        }

        /// <summary>
        /// True when a focus gain reflects where the user actually is.
        /// </summary>
        /// <remarks>
        /// The game writes IsFocused for two different reasons, and only one of them is navigation.
        /// UICharacterCreatorGenderItem.Refresh runs every frame and assigns
        /// `Button.IsFocused = base.IsOn`, using it as a *visual selected* highlight
        /// (docs/game-api.md sections 27, 28).
        ///
        /// That is harmless while the game owns navigation, because the pinned element is normally
        /// already true and the setter no-ops. Since v0.15 the mod owns arrow navigation and calls
        /// FocusableGroup.SetFocused, which clears every other element - including the pinned
        /// gender. Refresh then re-pins it on the very next frame, and that IS a genuine
        /// false -> true transition. Measured: every real move was followed by a phantom
        /// "F, selected", so moving to Voice or Age announced the destination and then immediately
        /// announced the gender again.
        ///
        /// The mod's own cursor is the authority on where the user is, so a focus gain that
        /// disagrees with it is the game talking about something else and is not spoken. Twins of
        /// the cursor count as agreement: a duplicate registration is the same control, and which
        /// twin holds the flag is an implementation detail the user must never hear about.
        /// This exception is restricted to the creator's spatial navigation. Ordinary logical
        /// panels retain distinct controls at coincident positions and require exact identity.
        ///
        /// Deliberately scoped to the group the mod is driving. When the game drives a panel
        /// (UICharacterOutfits) or nothing is being driven, every focus gain is announced exactly as
        /// before - this must not silence the screens that already work.
        /// </remarks>
        private static bool AgreesWithModCursor(Focusable focusable)
        {
            FocusableGroup driving = MenuNavigationHandler.CursorGroup;
            Focusable cursor = MenuNavigationHandler.Cursor;

            // Persistent HUD focus is meaningful after deliberate panel entry. Pointer hover
            // remains a separate audible path; automatic Game Bar seeding is not a user move.
            if (focusable.GetComponentInParent<UIGameBar>() != null
                && (!MenuNavigationHandler.HasUiPanelSelection
                    || !ReferenceEquals(GetGroup(focusable), driving)))
                return false;

            if (driving == null || cursor == null)
            {
                return true;
            }

            // A different panel's focus change is not the mod's business.
            if (!ReferenceEquals(GetGroup(focusable), driving))
            {
                return true;
            }

            return ReferenceEquals(focusable, cursor)
                || (LogicalFocusNavigator.UsesSpatialNavigation(driving)
                    && FocusNavigator.AreCoincident(focusable, cursor));
        }

        /// <summary>Harmony postfix on Focusable.IsMouseHovered.</summary>
        /// <param name="__instance">The Focusable whose hover state changed.</param>
        /// <param name="__state">True when hover genuinely turned on.</param>
        public static void IsMouseHoveredPostfix(Focusable __instance, bool __state)
        {
            if (!AnnounceHover || !__state || __instance == null
                || FocusNavigator.ShouldSkip(__instance)
                || SaveGameListHandler.ShouldSuppressScrollHover(__instance)
                || SettingsHandler.ShouldSuppressScrollHover(__instance)
                || HouseholdLibraryHandler.ShouldSuppressScrollHover(__instance)
                || ShouldSuppressWindowHoverForTransition(__instance)
                || IsOwnedByHiddenWindow(__instance))
            {
                return;
            }

            Announce(__instance, "hover");
        }

        /// <summary>
        /// Suppresses hover from one disappearing native window across a short, exact transition.
        /// </summary>
        internal static void SuppressWindowHoverForTransition(UIWindow window)
        {
            if (window == null)
            {
                return;
            }

            _transitionHoverWindow = window;
            _transitionHoverThroughFrame = Time.frameCount + 2;
        }

        private static bool ShouldSuppressWindowHoverForTransition(Focusable focusable)
        {
            if (_transitionHoverWindow == null
                || Time.frameCount > _transitionHoverThroughFrame)
            {
                _transitionHoverWindow = null;
                _transitionHoverThroughFrame = -1;
                return false;
            }

            UIWindow owner = focusable?.GetComponentInParent<UIWindow>();
            if (!ReferenceEquals(owner, _transitionHoverWindow))
            {
                return false;
            }

            Plugin.Log.LogInfo(
                $"[Hover] Suppressed transition hover from {owner.GetType().Name}.");
            return true;
        }

        /// <summary>
        /// True when a retained pooled control belongs to a window whose presentation is closed.
        /// </summary>
        private static bool IsOwnedByHiddenWindow(Focusable focusable)
        {
            try
            {
                UIWindow owner = focusable?.GetComponentInParent<UIWindow>();
                return owner != null && !owner.IsVisible;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Checking hover window visibility threw: " + ex);
                return false;
            }
        }

        private static UnityEngine.GameObject _lastSelected;

        /// <summary>
        /// Marks an EventSystem selection as already announced, so <see cref="Tick"/> stays quiet
        /// about it.
        /// </summary>
        /// <remarks>
        /// For the case where the MOD moved the selection deliberately and has already said
        /// something better. `TMP_InputField.Select()` changes the selection synchronously, and Tick
        /// runs later in the same frame, so without this NameEntryHandler's "Editing First name"
        /// would be followed immediately by a bare "First Name, blah" - two utterances for one
        /// action.
        ///
        /// Only suppresses the ONE selection passed in. Any later move - including the user
        /// arrowing to a different field - is a real change and is still announced.
        /// </remarks>
        /// <param name="selected">The object the mod just selected.</param>
        public static void SuppressSelectionAnnouncement(UnityEngine.GameObject selected)
        {
            _lastSelected = selected;
        }

        /// <summary>
        /// Announces movement between controls that live in Unity's EventSystem rather than the
        /// game's focus system. Called once per frame from the game loop hook.
        /// </summary>
        /// <remarks>
        /// THE SECOND NAVIGATION SYSTEM. game-api.md section 4 records that the game runs an
        /// `InputSystemUIInputModule` driving `EventSystem.SetSelectedGameObject`, entirely separate
        /// from `Focusable`/`FocusableGroup`. Everything this class patches is blind to it.
        ///
        /// Section 17 said whether those controls can be navigated OUTSIDE the Options inspector was
        /// prefab-serialized and probe-only. TEST 33 was that probe, and the answer is yes: in
        /// `UICharacterName` the arrow keys walked `InputFieldFirstName` to `InputFieldLastName` and
        /// back. **The movement already worked - nothing announced it.** The game also supplies its
        /// own Tab / Shift+Tab / Space chain through `UpdateKeyboardShortcuts`, gated on the same
        /// `HasInputFieldFocused`, so there are two working routes and this covers both.
        ///
        /// Scoped to exactly the controls the focus system cannot see: an active `TMP_InputField` or
        /// `Toggle` with NO `Focusable` on the same object. That is the same pair
        /// `HybridPlayer.IsCursorInsideInputField` tests (section 17), so the announcement covers
        /// precisely the set that puts the mod into typing mode - and the `Focusable` exclusion means
        /// this can never double-announce against IsFocusedPostfix.
        /// </remarks>
        public static void Tick()
        {
            if (!AnnounceFocus)
            {
                return;
            }

            // Runs inside the game's loop; contain it rather than let it escape into game code.
            try
            {
                UnityEngine.EventSystems.EventSystem eventSystem =
                    UnityEngine.EventSystems.EventSystem.current;

                UnityEngine.GameObject selected =
                    eventSystem != null ? eventSystem.currentSelectedGameObject : null;

                if (ReferenceEquals(selected, _lastSelected))
                {
                    return;
                }

                _lastSelected = selected;

                if (selected == null || !selected.activeInHierarchy
                    || WindowHandler.IsSceneTransitionActive)
                {
                    // Deselection is not announced: it is the normal end of an edit, and the handler
                    // that owns the screen re-announces where the user now is.
                    return;
                }

                bool isTextOrToggle = selected.GetComponent<TMPro.TMP_InputField>() != null
                                   || selected.GetComponent<UnityEngine.UI.Toggle>() != null;

                if (!isTextOrToggle || selected.GetComponent<Focusable>() != null)
                {
                    return;
                }

                string label = UiLabelReader.GetLabelOrFallback(selected.transform);
                if (string.IsNullOrEmpty(label))
                {
                    return;
                }

                if (LogAnnouncements)
                {
                    Plugin.Log.LogInfo($"[Speak/selected] '{label}'  <- {selected.name}");
                }

                ScreenReader.Speak(label);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("FocusHandler.Tick threw: " + ex);
            }
        }

        private static System.Reflection.FieldInfo _groupField;
        private static bool _groupFieldResolved;

        /// <summary>The FocusableGroup a Focusable registered with, or null.</summary>
        private static FocusableGroup GetGroup(Focusable focusable)
        {
            if (!_groupFieldResolved)
            {
                _groupFieldResolved = true;
                _groupField = AccessTools.Field(typeof(Focusable), "_focusableGroup");
            }

            return _groupField?.GetValue(focusable) as FocusableGroup;
        }

        /// <summary>Names the FocusableGroup a Focusable is registered with, for diagnostics.</summary>
        private static string DescribeGroup(Focusable focusable)
        {
            FocusableGroup group = GetGroup(focusable);
            return group != null ? group.name : "(none)";
        }

        /// <summary>Lists the nearest ancestors, nearest first, for label-context diagnostics.</summary>
        private static string DescribeAncestors(Focusable focusable)
        {
            var sb = new System.Text.StringBuilder();
            UnityEngine.Transform parent = focusable.transform.parent;

            for (int depth = 0; depth < 3 && parent != null; depth++)
            {
                if (sb.Length > 0)
                {
                    sb.Append('/');
                }

                sb.Append(parent.name);
                parent = parent.parent;
            }

            return sb.Length > 0 ? sb.ToString() : "(root)";
        }

        private static void Announce(Focusable focusable, string reason)
        {
            // This runs inside the game's own call stack. An exception escaping here would land in
            // game code, so it is contained and logged rather than allowed to propagate.
            try
            {
                if (focusable == null || FocusNavigator.ShouldSkip(focusable)
                    || WindowHandler.IsSceneTransitionActive || IsOwnedByHiddenWindow(focusable))
                {
                    return;
                }

                string label = UiLabelReader.GetLabelOrFallback(focusable);
                if (string.IsNullOrEmpty(label))
                {
                    return;
                }

                if (ScreenReader.SpeakIfChanged(label))
                {
                    if (LogAnnouncements)
                    {
                        // The owning group identifies WHICH driver moved focus - the decisive fact
                        // when one arrow press produces two announcements. The ancestor chain shows
                        // where an uninformative label like "M" or "..." could borrow context from.
                        Plugin.Log.LogInfo(
                            $"[Speak/{reason}] '{label}'  <- {focusable.name}"
                          + $"  group={DescribeGroup(focusable)}  under={DescribeAncestors(focusable)}");
                    }
                    else
                    {
                        Plugin.Log.LogDebug($"[{reason}] {label}");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"FocusHandler.Announce ({reason}) threw: {ex}");
            }
        }
    }
}
