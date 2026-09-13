using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using ParalivesAccess.Speech;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace ParalivesAccess.Handlers
{
    /// <summary>Bridges player-facing native inspectors and slider value editors into keyboard navigation.</summary>
    internal static class SettingsHandler
    {
        private static FieldInfo _rebindingDataField;
        private static FieldInfo _bindingWarningField;
        private static bool _wasRebinding;
        private static int _bindingInputFrame = -1;
        private static TMP_InputField _editing;
        private static Focusable _editingTarget;
        private static bool _editEnded;
        private static bool _editConfirmed;
        private static int _editStartedFrame;
        private static int _editReleasedFrame = -1;
        private static string _editOriginalText;
        private static bool _editRejected;
        private static UIFieldGameObject _pendingValue;
        private static TMP_InputField _pendingInput;
        private static string _valueBefore;
        private static UIInspector _scrolledInspector;
        private static float _scrollHoverUntil;

        /// <summary>Includes the completion frame so the newly assigned key cannot activate UI.</summary>
        internal static bool IsCapturingBindingInput =>
            _wasRebinding || _bindingInputFrame == Time.frameCount;

        /// <summary>Suppresses a submit/release echo while native numeric editing starts or ends.</summary>
        internal static bool OwnsEditInput => _editing != null || _editReleasedFrame == Time.frameCount;

        /// <summary>Hooks field creation, native edit completion, and option refresh.</summary>
        internal static bool ApplyPatches(Harmony harmony)
        {
            try
            {
                _rebindingDataField = AccessTools.Field(typeof(KeyRebindingManager),
                    "KeyRebindingManagerDataByPlayer");
                _bindingWarningField = AccessTools.Field(typeof(UIFieldKeyBinding), "_imageWarningDuplicateBinding");
                if (_rebindingDataField == null)
                    throw new MissingFieldException(typeof(KeyRebindingManager).Name,
                        "KeyRebindingManagerDataByPlayer");
                Patch(harmony, AccessTools.Method(typeof(UIField), "Init",
                    new[] { typeof(UIInspectorComponentVisual) }), nameof(FieldInitialized));
                Patch(harmony, AccessTools.Method(typeof(UIFieldGameObject), "OnEndEdit"),
                    nameof(FieldEditEnded));
                Patch(harmony, AccessTools.Method(typeof(UIOptions), "Update"),
                    nameof(OptionsRefreshed));
                Patch(harmony, AccessTools.Method(typeof(UIDecalPlacement), "Update"),
                    nameof(InspectorRefreshed));
                Patch(harmony, AccessTools.Method(typeof(UIStorytellerSelection), "Update"),
                    nameof(InspectorRefreshed));
                MethodInfo classicCommit = AccessTools.Method(typeof(UIClassicSliderItem), "OnInputValueSet");
                if (classicCommit == null) throw new MissingMethodException("UIClassicSliderItem.OnInputValueSet");
                harmony.Patch(classicCommit,
                    prefix: new HarmonyMethod(typeof(SettingsHandler), nameof(ClassicEditStarting)),
                    postfix: new HarmonyMethod(typeof(SettingsHandler), nameof(ClassicEditEnded)));
                if (!new List<MethodBase>(harmony.GetPatchedMethods()).Contains(classicCommit))
                    throw new InvalidOperationException("Classic slider input patch was not applied.");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Settings accessibility hooks failed: " + ex);
                return false;
            }
        }

        private static void Patch(Harmony harmony, MethodInfo target, string postfix)
        {
            if (target == null) throw new MissingMethodException(postfix);
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(SettingsHandler), postfix));
            if (!new List<MethodBase>(harmony.GetPatchedMethods()).Contains(target))
                throw new InvalidOperationException("Settings patch was not applied: " + target.Name);
            Plugin.Log.LogInfo("[Settings] Patched " + target.DeclaringType.Name + "." + target.Name);
        }

        private static UIWindow GetOwner(UIFieldGameObject row)
        {
            if (row?.UIField?.ComponentVisual == null) return null;
            UIInspector inspector = row.UIField.ComponentVisual.UIInspector;
            UIWindow owner = inspector != null ? inspector.GetComponentInParent<UIWindow>() : null;
            if (owner is UIOptions options && ReferenceEquals(options.UIInspector, inspector)) return owner;
            if (owner is UIDecalPlacement decal && ReferenceEquals(decal.Inspector, inspector)) return owner;
            if (owner is UIStorytellerSelection storyteller
                && ReferenceEquals(storyteller.CustomStorytellerInspector, inspector)) return owner;
            return null;
        }

        internal static UIFieldGameObject GetRow(Component source)
        {
            UIFieldGameObject row = source != null ? source.GetComponentInParent<UIFieldGameObject>() : null;
            return GetOwner(row) != null ? row : null;
        }

        private static void FieldInitialized(UIField __instance)
        {
            try
            {
                UIFieldGameObject row = __instance.UIFieldGameObject;
                if (GetOwner(row) == null) return;
                // UIField.RefreshTransformSiblingOrderAndVisibility disables Selectable navigation.
                // A native Focusable on the existing row supplies a stop without changing that rule.
                if (__instance is UIFieldInput && __instance.FieldType == typeof(Vector2))
                {
                    if (row.InputField1.GetComponent<Focusable>() == null) row.InputField1.gameObject.AddComponent<Focusable>();
                    if (row.InputField2.GetComponent<Focusable>() == null) row.InputField2.gameObject.AddComponent<Focusable>();
                }
                else if (__instance is UIFieldSlider || __instance is UIFieldInput)
                {
                    if (row.GetComponent<Focusable>() == null) row.gameObject.AddComponent<Focusable>();
                }
                string fieldName = __instance.ObjectHierarchy.Count > 0 ? __instance.FieldName : "header";
                Plugin.Log.LogInfo($"[Settings] row {fieldName}: {__instance.GetType().Name}, "
                    + $"slider={row.Slider != null}, numeric={row.InputField1 != null}, "
                    + $"action={row.ButtonAction != null}, toggle={row.ParaToggle1ForPlayersVersion != null}, "
                    + $"endEditListeners={row.InputField1?.onEndEdit.GetPersistentEventCount() ?? 0}, "
                    + $"sliderListeners={row.Slider?.onValueChanged.GetPersistentEventCount() ?? 0}, "
                    + $"actionListeners={row.ButtonAction?.ButtonClick?.GetPersistentEventCount() ?? 0}.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Settings row registration failed: " + ex);
                ScreenReader.Speak(Loc.Get("settings_error"));
            }
        }

        /// <summary>Reads the displayed row label and value from the supported player inspectors.</summary>
        internal static string GetLabel(Component source)
        {
            UIFieldGameObject row = GetRow(source);
            if (row == null) return null;
            UIField field = row.UIField;
            string name = row.LabelFieldName?.Text?.text;
            if (string.IsNullOrWhiteSpace(name)) return null;
            string result;
            if (field is UIFieldSlider slider)
            {
                result = Loc.Get(source is TMP_InputField ? "settings_numeric" : "settings_slider",
                    name, row.InputField1.text, slider.MinValue.ToString(CultureInfo.InvariantCulture),
                    slider.MaxValue.ToString(CultureInfo.InvariantCulture));
            }
            else if (field is UIFieldToggle && field.FieldType == typeof(bool))
            {
                bool on = row.ParaToggle1ForPlayersVersion != null
                    ? row.ParaToggle1ForPlayersVersion.IsOn : row.Toggle1.isOn;
                result = Loc.Get("settings_toggle", name, Loc.Get(on ? "settings_on" : "settings_off"));
            }
            else if (field is UIFieldKeyBinding && row.ButtonAction != null
                && source.gameObject != row.ButtonAction.gameObject)
            {
                result = Loc.Get("settings_reset_binding", name, row.ButtonActionLabel.text);
            }
            else if (row.ButtonAction != null && row.ButtonAction.gameObject.activeInHierarchy)
            {
                string value = row.ButtonActionLabel != null ? row.ButtonActionLabel.text : "";
                result = Loc.Get(field is UIFieldKeyBinding ? "settings_binding" : "settings_choice",
                    name, string.IsNullOrWhiteSpace(value) ? Loc.Get("settings_unassigned") : value);
            }
            else if (field is UIFieldInput && row.InputField1 != null)
            {
                TMP_InputField input = GetValueInput(row, source);
                result = Loc.Get("field_value", GetInputName(row, input), input.text);
            }
            else return null;
            if (field is UIFieldKeyBinding
                && _bindingWarningField?.GetValue(field) is GameObject warning && warning.activeInHierarchy)
                result = Loc.Get("settings_binding_conflict", result);
            return field.HasDisableInspectorEditionAttribute
                ? Loc.Get("settings_read_only", result) : result;
        }

        private static bool IsOptions(FocusableGroup group)
        {
            return group is UIOptions options && options.IsVisible && options.UIInspector != null;
        }

        /// <summary>Excludes inert headers and row shells while retaining their native controls.</summary>
        internal static bool ShouldSkipSurface(Focusable control)
        {
            UIFieldGameObject row = GetRow(control);
            if (row == null) return false;
            if (row.UIField is UIFieldHeader) return true;
            if (row.UIField is UIFieldInput && row.UIField.FieldType == typeof(Vector2))
                return control.gameObject == row.gameObject;
            return control.gameObject == row.gameObject
                && !(row.UIField is UIFieldSlider) && !(row.UIField is UIFieldInput)
                && ((row.ButtonAction != null && row.ButtonAction.gameObject != row.gameObject
                        && row.ButtonAction.gameObject.activeInHierarchy)
                    || (row.ParaToggle1ForPlayersVersion != null
                        && row.ParaToggle1ForPlayersVersion.gameObject != row.gameObject
                        && row.ParaToggle1ForPlayersVersion.gameObject.activeInHierarchy));
        }

        private static int Section(Focusable control)
        {
            if (control is UIOptionsCategoryItem) return 0;
            return GetRow(control) != null ? 1 : 2;
        }

        private static List<Focusable> OrderedControls(FocusableGroup group, List<Focusable> registered)
        {
            var result = new List<Focusable>();
            if (!IsOptions(group) || registered == null) return result;
            foreach (Focusable control in registered)
            {
                if (control != null && control.gameObject.activeInHierarchy && !control.IsShoulderButton
                    && !result.Contains(control) && !FocusNavigator.ShouldSkip(control)) result.Add(control);
            }
            result.Sort((a, b) => Section(a) != Section(b)
                ? Section(a).CompareTo(Section(b)) : CompareHierarchy(a.transform, b.transform));
            return result;
        }

        // UIField writes sibling indices from InspectorFieldOrder, including conditional rows.
        // Comparing that authored hierarchy avoids coincident layout positions during a rebuild.
        private static int CompareHierarchy(Transform a, Transform b)
        {
            var pathA = new List<int>();
            var pathB = new List<int>();
            for (Transform node = a; node != null; node = node.parent) pathA.Add(node.GetSiblingIndex());
            for (Transform node = b; node != null; node = node.parent) pathB.Add(node.GetSiblingIndex());
            pathA.Reverse(); pathB.Reverse();
            for (int i = 0; i < Math.Min(pathA.Count, pathB.Count); i++)
                if (pathA[i] != pathB[i]) return pathA[i].CompareTo(pathB[i]);
            return pathA.Count.CompareTo(pathB.Count);
        }

        internal static Focusable GetEntry(FocusableGroup group, List<Focusable> registered)
        {
            List<Focusable> controls = OrderedControls(group, registered);
            Focusable current = group != null ? group.GetFocused(false) : null;
            if (current != null && controls.Contains(current)) return current;
            return controls.Count > 0 ? controls[0] : null;
        }

        internal static string GetPanelLabel(FocusableGroup group)
        {
            return IsOptions(group) ? Loc.Get("settings_panel") : null;
        }

        private static string GetValuesLabel(UIOptions options)
        {
            if (options?.UIListOptionCategories?.CurrentItems != null)
                foreach (UIListItemBase item in options.UIListOptionCategories.CurrentItems)
                    if (item is UIOptionsCategoryItem category && category.IsOn)
                        return Loc.Get("settings_category_values", category.TranslatedTextCategoryName.Text.text);
            return Loc.Get("settings_values");
        }

        /// <summary>Moves between the native categories, values and window actions in one group.</summary>
        internal static bool TrySwitchSection(FocusableGroup group, List<Focusable> registered,
            Focusable source, int direction, out Focusable target, out string label)
        {
            target = null;
            label = null;
            if (!IsOptions(group)) return false;
            List<Focusable> controls = OrderedControls(group, registered);
            int current = source != null ? Section(source) : 0;
            for (int offset = 1; offset <= 3; offset++)
            {
                int section = (current + (direction > 0 ? offset : -offset) + 3) % 3;
                target = controls.Find(c => Section(c) == section);
                if (target == null) continue;
                if (section == 0)
                    target = controls.Find(c => c is UIOptionsCategoryItem category && category.IsOn) ?? target;
                label = section == 0 ? Loc.Get("settings_categories")
                    : section == 1 ? GetValuesLabel((UIOptions)group) : Loc.Get("settings_actions");
                break;
            }
            return true;
        }

        /// <summary>Keeps keyboard-focused rows inside the native inspector's viewport.</summary>
        internal static void OnFocusGained(Focusable target)
        {
            UIFieldGameObject row = GetRow(target);
            ScrollRect scroll = row?.UIField.ComponentVisual.UIInspector.ScrollRect;
            RectTransform viewport = scroll?.viewport;
            if (scroll == null || !scroll.vertical || scroll.content == null
                || viewport == null || row.RectTransform == null) return;
            Canvas.ForceUpdateCanvases();
            Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(viewport, row.RectTransform);
            float shift = bounds.max.y > viewport.rect.yMax ? viewport.rect.yMax - bounds.max.y
                : bounds.min.y < viewport.rect.yMin ? viewport.rect.yMin - bounds.min.y : 0f;
            if (Mathf.Abs(shift) < 0.5f) return;
            scroll.StopMovement();
            Vector2 position = scroll.content.anchoredPosition;
            position.y += shift;
            scroll.content.anchoredPosition = position;
            _scrolledInspector = row.UIField.ComponentVisual.UIInspector;
            // As in save-list scrolling, Unity may dispatch pointer-enter after the move frame.
            _scrollHoverUntil = Time.unscaledTime + 0.15f;
            Canvas.ForceUpdateCanvases();
        }

        internal static bool ShouldSuppressScrollHover(Focusable target)
        {
            UIFieldGameObject row = GetRow(target);
            return row != null && ReferenceEquals(row.UIField.ComponentVisual.UIInspector, _scrolledInspector)
                && Time.unscaledTime < _scrollHoverUntil
                && (Mouse.current == null || Mouse.current.delta.ReadValue().sqrMagnitude == 0f);
        }

        /// <summary>Uses authored row order; left/right changes a slider through its native OnMove.</summary>
        internal static bool TryNavigate(FocusableGroup group, List<Focusable> registered,
            Focusable source, Vector2 direction, out Focusable target)
        {
            target = null;
            UIFieldGameObject sourceRow = GetRow(source);
            if (direction.x != 0 && sourceRow?.UIField is UIFieldSlider
                && GetOwner(sourceRow)?.IsVisible == true
                && ReferenceEquals(source.GetComponentInParent<FocusableGroup>(), group))
            {
                AdjustSlider(sourceRow, direction.x > 0);
                target = source;
                return true;
            }
            if (!IsOptions(group)) return false;
            List<Focusable> controls = OrderedControls(group, registered);
            if (controls.Count == 0) return true;
            int index = controls.IndexOf(source);
            if (index < 0) { target = controls[0]; return true; }
            UIFieldGameObject row = GetRow(source);
            if (direction.x > 0 && source is UIOptionsCategoryItem)
            {
                target = controls.Find(c => GetRow(c) != null) ?? source;
                return true;
            }
            if (direction.x != 0 && row?.UIField is UIFieldSlider)
            {
                AdjustSlider(row, direction.x > 0);
                target = source;
                return true;
            }
            // Horizontal movement stays inside the row (e.g. binding and its Reset button).
            int step = direction.y != 0 ? (direction.y < 0 ? 1 : -1) : (direction.x > 0 ? 1 : -1);
            int next = index + step;
            if (next >= 0 && next < controls.Count
                && (direction.y != 0 || (row != null && ReferenceEquals(row, GetRow(controls[next])))))
                target = controls[next];
            else if (direction.x < 0 && row != null)
                target = controls.Find(c => c is UIOptionsCategoryItem category && category.IsOn) ?? source;
            else target = source;
            return true;
        }

        private static void AdjustSlider(UIFieldGameObject row, bool increase)
        {
            Slider slider = row.Slider;
            if (row.UIField.HasDisableInspectorEditionAttribute || slider == null || !slider.IsInteractable())
            {
                ScreenReader.Speak(Loc.Get("settings_read_only", GetLabel(row)));
                return;
            }
            _valueBefore = row.InputField1.text;
            _pendingValue = row;
            _pendingInput = row.InputField1;
            // Delegate clamping, whole-number handling, step size, and value-changed callbacks.
            // OnMove has no dependency on EventSystem selection. UIField deliberately sets Mode.None.
            bool vertical = slider.direction == Slider.Direction.BottomToTop || slider.direction == Slider.Direction.TopToBottom;
            bool reverse = slider.direction == Slider.Direction.RightToLeft || slider.direction == Slider.Direction.TopToBottom;
            bool forward = increase != reverse;
            MoveDirection move = vertical ? (forward ? MoveDirection.Up : MoveDirection.Down)
                : (forward ? MoveDirection.Right : MoveDirection.Left);
            slider.OnMove(new AxisEventData(EventSystem.current) { moveDir = move });
        }

        /// <summary>Uses the same native slider route for Paramaker's existing 7/8 keys.</summary>
        internal static bool TryAdjustValue(Focusable target, bool increase)
        {
            UIFieldGameObject row = GetRow(target);
            if (!(row?.UIField is UIFieldSlider) || GetOwner(row)?.IsVisible != true
                || !target.gameObject.activeInHierarchy) return false;
            AdjustSlider(row, increase);
            return true;
        }

        internal static bool IsValueEntryTarget(Focusable target)
        {
            if (target is UIClassicSliderItem classic) return classic.InputField != null;
            UIFieldGameObject row = GetRow(target);
            return row != null && (row.gameObject == target.gameObject
                    || target.GetComponent<TMP_InputField>() == row.InputField1
                    || (row.InputField2 != null && target.GetComponent<TMP_InputField>() == row.InputField2))
                && (row.UIField is UIFieldSlider || row.UIField is UIFieldInput);
        }

        private static TMP_InputField GetValueInput(UIFieldGameObject row, Component source)
        {
            TMP_InputField input = source != null ? source.GetComponent<TMP_InputField>() : null;
            return input == row.InputField2 && input != null ? input : row.InputField1;
        }

        private static string GetInputName(UIFieldGameObject row, TMP_InputField input)
        {
            string name = row.LabelFieldName.Text.text;
            return row.UIField is UIFieldInput && row.UIField.FieldType == typeof(Vector2)
                ? Loc.Get(input == row.InputField2 ? "settings_axis_y" : "settings_axis_x", name) : name;
        }

        /// <summary>Enter uses the game's Select/ActivateInputField recipe on the existing input.</summary>
        internal static void ActivateValueEntry(Focusable target)
        {
            UIFieldGameObject row = GetRow(target);
            UIClassicSliderItem classic = target as UIClassicSliderItem;
            TMP_InputField input = classic != null ? classic.InputField : row != null ? GetValueInput(row, target) : null;
            if (input == null || !input.gameObject.activeInHierarchy || input.readOnly || !input.IsInteractable())
            {
                ScreenReader.Speak(Loc.Get("settings_read_only", GetLabel(target)));
                return;
            }
            _editing = input;
            _editingTarget = target;
            _editEnded = false;
            _editConfirmed = false;
            _editRejected = false;
            _editOriginalText = input.text;
            _editStartedFrame = Time.frameCount;
            if (classic != null)
            {
                // Keep the prefab's callbacks. The observer calls the public numeric commit only
                // if its Harmony postfix has not already observed a native commit in this edit.
                // This also covers absent/disabled prefab wiring without adding a second undo.
                bool wired = false;
                for (int i = 0; i < input.onEndEdit.GetPersistentEventCount(); i++)
                    wired |= ReferenceEquals(input.onEndEdit.GetPersistentTarget(i), classic)
                        && input.onEndEdit.GetPersistentMethodName(i) == nameof(UIClassicSliderItem.OnInputValueSet);
                input.onEndEdit.RemoveListener(CommitClassicInput);
                input.onEndEdit.AddListener(CommitClassicInput);
                Plugin.Log.LogInfo("[Settings] Classic slider native end-edit listener present=" + wired);
            }
            input.Select();
            input.ActivateInputField();
            input.caretPosition = input.text.Length;
            Plugin.Log.LogInfo("[Settings] Editing " + (classic != null ? "body slider" : row.UIField.FieldName));
            ScreenReader.Speak(Loc.Get("value_entry_started", input.text));
        }

        private static void CommitClassicInput(string value)
        {
            if (_editingTarget is UIClassicSliderItem classic
                && ReferenceEquals(_editing, classic.InputField) && !_editEnded)
                classic.OnInputValueSet();
        }

        private static bool ClassicEditStarting(UIClassicSliderItem __instance)
        {
            if (_editing == null || !ReferenceEquals(_editing, __instance.InputField)) return true;
            if (_editEnded) return false;
            // Native OnInputValueSet uses float.Parse and accepts 0..100. A blank or malformed
            // edit must not throw out of Unity's event dispatch; no character write occurs here.
            if (float.TryParse(_editing.text, NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.CurrentCulture, out float value) && value >= 0f && value <= 100f) return true;
            _editRejected = true;
            _editing.text = _editOriginalText;
            ScreenReader.Speak(Loc.Get("slider_invalid_value"));
            return false;
        }

        private static void ClassicEditEnded(UIClassicSliderItem __instance)
        {
            if (_editing != null && ReferenceEquals(__instance.InputField, _editing)) _editEnded = true;
        }

        private static void FieldEditEnded(UIFieldGameObject __instance)
        {
            if (_editing != null && (ReferenceEquals(__instance.InputField1, _editing)
                || ReferenceEquals(__instance.InputField2, _editing))) _editEnded = true;
        }

        private static void OptionsRefreshed(UIOptions __instance)
        {
            InspectorRefreshed(__instance);
        }

        private static void InspectorRefreshed(UIWindow __instance)
        {
            if (_pendingValue == null || !ReferenceEquals(GetOwner(_pendingValue), __instance)) return;
            UIFieldGameObject row = _pendingValue;
            TMP_InputField input = _pendingInput;
            _pendingValue = null;
            _pendingInput = null;
            if (!__instance.IsVisible || !row.gameObject.activeInHierarchy
                || input == null || WindowHandler.IsSceneTransitionActive) return;
            string message = Loc.Get("field_value", GetInputName(row, input), input.text);
            if (input.text == _valueBefore) message = Loc.Get("settings_unchanged", message);
            Plugin.Log.LogInfo("[Settings] " + message);
            ScreenReader.Speak(message);
        }

        /// <summary>Observes native editing/rebinding before and after the game consumes input.</summary>
        internal static void Tick()
        {
            try
            {
                var data = _rebindingDataField?.GetValue(null) as Dictionary<int, KeyRebindingManagerPlayerData>;
                bool rebinding = false;
                int playerIndex = PlayerManager.Instance?.HybridPlayer1?.Player?.PlayerIndex ?? -1;
                if (data != null && data.TryGetValue(playerIndex, out KeyRebindingManagerPlayerData playerData))
                    rebinding = playerData.RebindOperation != null;
                if (rebinding || _wasRebinding) _bindingInputFrame = Time.frameCount;
                _wasRebinding = rebinding;

                if (_editing == null) return;
                _editConfirmed |= _editing.isFocused;
                if (!_editEnded && _editing.gameObject.activeInHierarchy
                    && (!_editConfirmed || _editing.isFocused))
                {
                    // ActivateInputField is deferred to TMP's LateUpdate. Only diagnose an actual
                    // activation failure after its existing Name Details grace period has elapsed.
                    if (_editConfirmed || Time.frameCount - _editStartedFrame <= 20) return;
                    Plugin.Log.LogWarning("[Settings] Numeric input activation did not complete.");
                    _editRejected = true;
                    ScreenReader.Speak(Loc.Get("settings_error"));
                }
                TMP_InputField input = _editing;
                Focusable target = _editingTarget;
                input.onEndEdit.RemoveListener(CommitClassicInput);
                _editing = null;
                _editingTarget = null;
                _editReleasedFrame = Time.frameCount;
                if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject == input.gameObject)
                    EventSystem.current.SetSelectedGameObject(null);
                if (target != null && target.gameObject.activeInHierarchy && !WindowHandler.IsSceneTransitionActive)
                {
                    if (target is UIClassicSliderItem classic)
                    {
                        if (!_editRejected) ValueAdjustHandler.ReportSliderValue(classic);
                        return;
                    }
                    UIFieldGameObject row = GetRow(target);
                    _pendingValue = row;
                    _pendingInput = input;
                    _valueBefore = null;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Settings input observation failed: " + ex);
            }
        }
    }
}
