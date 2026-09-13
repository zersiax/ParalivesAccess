using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ParalivesAccess.Speech;
using Setting;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ParalivesAccess.Handlers
{
    /// <summary>
    /// Makes the schedule confirmation which follows a job offer usable without a mouse.
    /// </summary>
    /// <remarks>
    /// <c>UIOccupationOfferItem.OnClickOnItem</c> never enrolls immediately. A qualified offer
    /// always calls <c>TryEnrollToOccupationWithoutConflictingSchedule</c>, which opens
    /// <c>UIOccupationsChangeSchedule</c>. That window stores its two choices in raw
    /// <c>TMP_Dropdown</c>s, not Paralives <c>Focusable</c>s, so neither the game's FocusableGroup
    /// navigation nor the mod's ordinary panel navigator can reach them.
    ///
    /// This handler owns only that exact visible window. It presents the live dropdown options as
    /// a VirtualPanel, writes choices through <c>UIParaDropdown.SetValue</c>, and commits through
    /// the window's own <c>OnClickChange</c>. The game remains authoritative for available choices,
    /// schedule conflicts, enrollment, notifications, memory and every resulting occupation value.
    /// </remarks>
    public static class OccupationHandler
    {
        private const int SettleFrames = 3;
        private const int SelectionSettleFrames = 2;

        private enum PanelKind
        {
            None,
            Schedule,
            Days,
            Hours
        }

        private sealed class DelegateEntry : VirtualPanel.IEntry
        {
            private readonly Func<string> _describe;
            private readonly Func<bool> _available;
            private readonly Action _activate;

            public DelegateEntry(
                Func<string> describe, Func<bool> available, Action activate)
            {
                _describe = describe;
                _available = available;
                _activate = activate;
            }

            public string Describe()
            {
                return _describe != null ? _describe() : null;
            }

            public bool IsAvailable => _available == null || _available();

            public void Activate()
            {
                _activate?.Invoke();
            }
        }

        public static bool Enabled { get; set; } = true;

        /// <summary>True while the exact native schedule window owns arrows and Enter.</summary>
        public static bool OwnsInput => Enabled && _window != null && _window.IsVisible;

        /// <summary>
        /// True when the focused component occupies a native job-card root. The runtime prefab
        /// registers two coincident Focusables on that object, so positional deduplication can land
        /// on a presentation component instead of the <see cref="UIOccupationOfferItem"/> which
        /// owns application.
        /// </summary>
        internal static bool IsOfferActivationTarget(Focusable target)
        {
            return Enabled && target != null && target.gameObject.activeInHierarchy
                && target.GetComponent<UIOccupationOfferItem>() != null;
        }

        /// <summary>
        /// Invokes the job card's authoritative application callback exactly once. That method
        /// re-evaluates application points, presents the game's native rejection when short, and
        /// otherwise enters its existing conflict and schedule flow.
        /// </summary>
        internal static bool TryActivateOffer(Focusable target)
        {
            if (!IsOfferActivationTarget(target))
            {
                return false;
            }

            UIOccupationOfferItem item = target.GetComponent<UIOccupationOfferItem>();
            try
            {
                OccupationOffer offer = item.Offer;
                UIOccupationOffers owner = item.UIOccupationOffers;
                AssetCharacter character = owner?.Character;
                Occupations occupations = Settings.Get<Occupations>();
                OccupationsManager manager = OccupationsManager.Instance;
                Occupation occupation = offer != null && occupations != null
                    ? occupations.GetOccupationByGUID(offer.Occupation)
                    : null;
                if (offer == null || character == null || occupation == null || manager == null)
                {
                    Speak(Loc.Get("occupation_offer_unavailable"));
                    return true;
                }

                int points = manager.GetCharacterTotalApplicationPoints(
                    character, occupation, offer);
                int listenerCount = item.ButtonClick != null
                    ? item.ButtonClick.GetPersistentEventCount()
                    : 0;
                Plugin.Log.LogInfo(
                    $"[Occupation] Keyboard Apply invoking native OnClickOnItem; "
                  + $"focusedComponent={target.GetType().Name}, "
                  + $"semanticComponentMatched={ReferenceEquals(target, item)}, "
                  + $"selectionMode={item.ParentUIList?.InSelectionMode}, "
                  + $"persistentListeners={listenerCount}, "
                  + $"character={character.GUID}, occupation={offer.Occupation}, "
                  + $"startingRank={offer.StartingRank}, points={points}/"
                  + $"{offer.RequiredApplicationPoints}, "
                  + $"qualified={points >= offer.RequiredApplicationPoints}.");

                item.OnClickOnItem();
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError(
                    "Invoking UIOccupationOfferItem.OnClickOnItem from Enter threw: " + ex);
                Speak(Loc.Get("occupation_offer_unavailable"));
                return true;
            }
        }

        private static UIOccupationsChangeSchedule _window;
        private static VirtualPanel _panel;
        private static VirtualPanel _schedulePanel;
        private static VirtualPanel.IEntry _daysEntry;
        private static VirtualPanel.IEntry _hoursEntry;
        private static PanelKind _panelKind;
        private static int _settleFrames;
        private static int _selectionSettleFrames;
        private static bool _enterPressed;
        private static string _pendingSelectionLabel;
        private static UIParaDropdown _pendingSelectionDropdown;

        private static bool _reflectionResolved;
        private static FieldInfo _characterField;
        private static FieldInfo _occupationField;
        private static FieldInfo _occupationIndexField;

        /// <summary>
        /// Captures the visible window before game systems consume navigation for the frame.
        /// </summary>
        public static void BeforeGameSystems()
        {
            if (!Enabled)
            {
                return;
            }

            try
            {
                ObserveWindow();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("OccupationHandler.BeforeGameSystems threw: " + ex);
            }
        }

        /// <summary>Called before general menu navigation in the game-loop postfix.</summary>
        public static void Tick()
        {
            if (!Enabled)
            {
                Reset();
                return;
            }

            try
            {
                ObserveWindow();
                if (!OwnsInput)
                {
                    return;
                }

                if (_panel == null)
                {
                    if (_settleFrames-- > 0)
                    {
                        return;
                    }

                    BuildSchedulePanel();
                    return;
                }

                if (_selectionSettleFrames > 0)
                {
                    _selectionSettleFrames--;
                    if (_selectionSettleFrames == 0)
                    {
                        AnnounceSettledSelection();
                    }
                    return;
                }

                Keyboard keyboard = Keyboard.current;
                if (keyboard == null)
                {
                    return;
                }

                if (keyboard.backspaceKey.wasPressedThisFrame)
                {
                    HandleBack();
                    return;
                }

                if (keyboard.downArrowKey.wasPressedThisFrame
                    || keyboard.rightArrowKey.wasPressedThisFrame)
                {
                    _panel.Move(1);
                    return;
                }

                if (keyboard.upArrowKey.wasPressedThisFrame
                    || keyboard.leftArrowKey.wasPressedThisFrame)
                {
                    _panel.Move(-1);
                    return;
                }

                bool pressed = keyboard.enterKey.wasPressedThisFrame
                    || keyboard.numpadEnterKey.wasPressedThisFrame;
                bool released = keyboard.enterKey.wasReleasedThisFrame
                    || keyboard.numpadEnterKey.wasReleasedThisFrame;
                if (pressed)
                {
                    _enterPressed = true;
                }

                if (released && _enterPressed)
                {
                    _enterPressed = false;
                    _panel.Activate();
                }
            }
            catch (Exception ex)
            {
                Enabled = false;
                Reset();
                Plugin.Log.LogError("OccupationHandler disabled after an exception: " + ex);
                Speak(Loc.Get("occupation_handler_failed"));
            }
        }

        /// <summary>Prevents WindowHandler from also announcing this mod-owned flow.</summary>
        internal static bool ManagesWindow(UIWindow window)
        {
            return Enabled && window is UIOccupationsChangeSchedule;
        }

        /// <summary>Supplies this virtual schedule location to the global F3 report.</summary>
        internal static bool TryGetContext(out string panelName, out string element)
        {
            panelName = null;
            element = null;
            if (!OwnsInput)
            {
                return false;
            }

            panelName = _panel != null
                ? _panel.Name
                : Loc.Get("occupation_schedule_panel");
            element = _panel?.Current?.Describe()
                ?? Loc.Get("occupation_schedule_loading");
            return true;
        }

        private static void ObserveWindow()
        {
            UIOccupationsChangeSchedule current =
                UI.GetOrNull<UIOccupationsChangeSchedule>(0);
            if (current == null || !current.IsVisible)
            {
                if (_window != null)
                {
                    Reset();
                }
                return;
            }

            if (ReferenceEquals(current, _window))
            {
                return;
            }

            _window = current;
            _panel = null;
            _schedulePanel = null;
            _daysEntry = null;
            _hoursEntry = null;
            _panelKind = PanelKind.None;
            _settleFrames = SettleFrames;
            _selectionSettleFrames = 0;
            _enterPressed = false;
            _pendingSelectionLabel = null;
            _pendingSelectionDropdown = null;
            Plugin.Log.LogInfo(
                "[Occupation] Schedule window detected; awaiting live dropdown options.");
        }

        private static void BuildSchedulePanel()
        {
            if (_window == null || !_window.IsVisible)
            {
                Reset();
                return;
            }

            string occupationName = GetOccupationName(_window);
            bool isEnrollment = IsEnrollment(_window);
            string panelName = string.IsNullOrWhiteSpace(occupationName)
                ? Loc.Get(isEnrollment
                    ? "occupation_schedule_panel"
                    : "occupation_schedule_change_panel")
                : Loc.Get(isEnrollment
                    ? "occupation_schedule_panel_named"
                    : "occupation_schedule_change_panel_named",
                    occupationName);

            _schedulePanel = new VirtualPanel(panelName);
            _daysEntry = new DelegateEntry(
                () => DescribeDropdown(
                    Loc.Get("occupation_schedule_days"), _window?.DropdownDays),
                () => IsDropdownAvailable(_window?.DropdownDays),
                () => OpenOptions(
                    _window?.DropdownDays,
                    Loc.Get("occupation_schedule_days"),
                    PanelKind.Days,
                    _daysEntry));
            _hoursEntry = new DelegateEntry(
                () => DescribeDropdown(
                    Loc.Get("occupation_schedule_hours"), _window?.DropdownHours),
                () => IsDropdownAvailable(_window?.DropdownHours),
                () => OpenOptions(
                    _window?.DropdownHours,
                    Loc.Get("occupation_schedule_hours"),
                    PanelKind.Hours,
                    _hoursEntry));

            _schedulePanel.Add(_daysEntry);
            _schedulePanel.Add(_hoursEntry);
            _schedulePanel.Add(new DelegateEntry(
                DescribeConflict,
                HasConflict,
                () => Speak(Loc.Get("occupation_schedule_choose_another"))));
            _schedulePanel.Add(new DelegateEntry(
                DescribeConfirm,
                CanConfirm,
                Confirm));
            _schedulePanel.Add(new DelegateEntry(
                () => Loc.Get(IsEnrollment(_window)
                    ? "occupation_schedule_cancel_application"
                    : "occupation_schedule_cancel_changes"),
                () => _window != null && _window.IsVisible,
                Cancel));

            _panel = _schedulePanel;
            _panelKind = PanelKind.Schedule;
            Plugin.Log.LogInfo(
                $"[Occupation] Accessible schedule ready; enrollment={isEnrollment}, "
              + $"occupation='{occupationName}', "
              + $"dayOptions={OptionCount(_window.DropdownDays)}, "
              + $"hourOptions={OptionCount(_window.DropdownHours)}, "
              + $"conflict={HasConflict()}.");
            _panel.Enter();
        }

        private static void OpenOptions(
            UIParaDropdown dropdown,
            string label,
            PanelKind kind,
            VirtualPanel.IEntry returnEntry)
        {
            if (!IsDropdownAvailable(dropdown))
            {
                Speak(Loc.Get("occupation_schedule_choice_unavailable"));
                return;
            }

            TMP_Dropdown native = dropdown.Dropdown;
            if (native.options == null || native.options.Count <= 1)
            {
                Speak(Loc.Get(
                    "occupation_schedule_fixed",
                    label,
                    GetDropdownValue(dropdown)));
                return;
            }

            var options = new VirtualPanel(label);
            VirtualPanel.IEntry selected = null;
            for (int i = 0; i < native.options.Count; i++)
            {
                int index = i;
                var entry = new DelegateEntry(
                    () => DescribeOption(dropdown, index),
                    () => IsOptionAvailable(dropdown, index),
                    () => SelectOption(dropdown, index, label, returnEntry));
                options.Add(entry);
                if (index == native.value)
                {
                    selected = entry;
                }
            }

            _panel = options;
            _panelKind = kind;
            options.Enter(selected);
        }

        private static void SelectOption(
            UIParaDropdown dropdown,
            int index,
            string label,
            VirtualPanel.IEntry returnEntry)
        {
            if (!IsOptionAvailable(dropdown, index))
            {
                Speak(Loc.Get("occupation_schedule_choice_unavailable"));
                return;
            }

            dropdown.SetValue(index);
            Plugin.Log.LogInfo(
                $"[Occupation] {label} selected index={index}, "
              + $"value='{GetDropdownValue(dropdown)}'.");

            _panel = _schedulePanel;
            _panelKind = PanelKind.Schedule;
            _schedulePanel.EnterSilently(returnEntry);
            _pendingSelectionLabel = label;
            _pendingSelectionDropdown = dropdown;
            _selectionSettleFrames = SelectionSettleFrames;
        }

        private static void AnnounceSettledSelection()
        {
            string label = _pendingSelectionLabel;
            UIParaDropdown dropdown = _pendingSelectionDropdown;
            _pendingSelectionLabel = null;
            _pendingSelectionDropdown = null;

            if (_window == null || !_window.IsVisible || dropdown == null)
            {
                return;
            }

            string value = GetDropdownValue(dropdown);
            string conflict = DescribeConflict();
            string message = string.IsNullOrWhiteSpace(conflict)
                ? Loc.Get("occupation_schedule_selected", label, value)
                : Loc.Get("occupation_schedule_selected_conflict", label, value, conflict);
            Speak(message);
        }

        private static void HandleBack()
        {
            _enterPressed = false;
            if (_panelKind == PanelKind.Days || _panelKind == PanelKind.Hours)
            {
                VirtualPanel.IEntry preferred = _panelKind == PanelKind.Days
                    ? _daysEntry
                    : _hoursEntry;
                _panel = _schedulePanel;
                _panelKind = PanelKind.Schedule;
                _schedulePanel.Enter(preferred);
                return;
            }

            Cancel();
        }

        private static void Confirm()
        {
            if (_window == null || !_window.IsVisible)
            {
                Speak(Loc.Get("occupation_schedule_gone"));
                Reset();
                return;
            }

            if (!CanConfirm())
            {
                string conflict = DescribeConflict();
                Speak(string.IsNullOrWhiteSpace(conflict)
                    ? Loc.Get("occupation_schedule_cannot_confirm")
                    : conflict);
                return;
            }

            string occupationName = GetOccupationName(_window);
            bool isEnrollment = IsEnrollment(_window);
            Plugin.Log.LogInfo(
                $"[Occupation] Invoking native schedule confirmation; "
              + $"enrollment={isEnrollment}, occupation='{occupationName}', "
              + $"days='{GetDropdownValue(_window.DropdownDays)}', "
              + $"hours='{GetDropdownValue(_window.DropdownHours)}'.");

            UIOccupationsChangeSchedule current = _window;
            LotBuildInputOwnership.HoldThroughCurrentFrame();
            current.OnClickChange();
            Reset();
        }

        private static void Cancel()
        {
            if (_window == null || !_window.IsVisible)
            {
                Reset();
                return;
            }

            Plugin.Log.LogInfo("[Occupation] Closing schedule without applying changes.");
            UIOccupationsChangeSchedule current = _window;
            LotBuildInputOwnership.HoldThroughCurrentFrame();
            current.OnClickQuit();
            Reset();
        }

        private static string DescribeDropdown(string label, UIParaDropdown dropdown)
        {
            string value = GetDropdownValue(dropdown);
            int count = OptionCount(dropdown);
            return count > 1
                ? Loc.Get("occupation_schedule_choice", label, value, count)
                : Loc.Get("occupation_schedule_fixed", label, value);
        }

        private static string DescribeOption(UIParaDropdown dropdown, int index)
        {
            if (!IsOptionAvailable(dropdown, index))
            {
                return Loc.Get("occupation_schedule_choice_unavailable");
            }

            string value = Clean(dropdown.Dropdown.options[index]?.text)
                ?? Loc.Get("occupation_schedule_unnamed_choice");
            return index == dropdown.Dropdown.value
                ? Loc.Get("label_with_state", value, Loc.Get("state_selected"))
                : value;
        }

        private static string DescribeConfirm()
        {
            string occupationName = GetOccupationName(_window);
            if (!IsEnrollment(_window))
            {
                return Loc.Get("occupation_schedule_save_changes");
            }

            return string.IsNullOrWhiteSpace(occupationName)
                ? Loc.Get("occupation_schedule_accept_job")
                : Loc.Get("occupation_schedule_accept_named_job", occupationName);
        }

        private static string DescribeConflict()
        {
            if (!HasConflict())
            {
                return null;
            }

            TranslatedText text = _window.ConflictMessage;
            string rendered = text?.Text != null && text.Text.gameObject.activeInHierarchy
                ? Clean(text.Text.text)
                : null;
            return rendered ?? Loc.Get("occupation_schedule_conflict");
        }

        private static bool HasConflict()
        {
            return _window?.ConflictMessage != null
                && _window.ConflictMessage.gameObject.activeInHierarchy;
        }

        private static bool CanConfirm()
        {
            return _window?.ButtonValidate != null
                && _window.ButtonValidate.activeInHierarchy
                && !HasConflict();
        }

        private static bool IsDropdownAvailable(UIParaDropdown dropdown)
        {
            return dropdown != null && dropdown.gameObject.activeInHierarchy
                && dropdown.Dropdown != null && dropdown.Dropdown.gameObject.activeInHierarchy
                && dropdown.Dropdown.options != null && dropdown.Dropdown.options.Count > 0;
        }

        private static bool IsOptionAvailable(UIParaDropdown dropdown, int index)
        {
            return IsDropdownAvailable(dropdown)
                && index >= 0 && index < dropdown.Dropdown.options.Count;
        }

        private static int OptionCount(UIParaDropdown dropdown)
        {
            return dropdown?.Dropdown?.options?.Count ?? 0;
        }

        private static string GetDropdownValue(UIParaDropdown dropdown)
        {
            if (!IsDropdownAvailable(dropdown))
            {
                return Loc.Get("occupation_schedule_choice_unavailable");
            }

            int value = dropdown.Dropdown.value;
            if (value < 0 || value >= dropdown.Dropdown.options.Count)
            {
                return Loc.Get("occupation_schedule_choice_unavailable");
            }

            return Clean(dropdown.Dropdown.options[value]?.text)
                ?? Loc.Get("occupation_schedule_unnamed_choice");
        }

        private static string GetOccupationName(UIOccupationsChangeSchedule window)
        {
            if (window == null)
            {
                return null;
            }

            ResolveReflection();
            try
            {
                var occupation = _occupationField?.GetValue(window) as Occupation;
                if (occupation == null || string.IsNullOrWhiteSpace(occupation.DisplayName))
                {
                    return null;
                }

                var character = _characterField?.GetValue(window) as AssetCharacter;
                string key = "OccupationName_" + occupation.DisplayName;
                string translated = character != null && PersonalityManager.Instance != null
                    ? TranslationManager.Get(
                        key,
                        PersonalityManager.Instance.GetGenderFromPronouns(character))
                    : TranslationManager.Get(key);
                return Clean(translated);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError(
                    "Reading UIOccupationsChangeSchedule._occupation threw: " + ex);
                return null;
            }
        }

        private static bool IsEnrollment(UIOccupationsChangeSchedule window)
        {
            if (window == null)
            {
                return true;
            }

            ResolveReflection();
            try
            {
                object value = _occupationIndexField?.GetValue(window);
                return !(value is int) || (int)value == -1;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError(
                    "Reading UIOccupationsChangeSchedule._occupationIndex threw: " + ex);
                return true;
            }
        }

        private static void ResolveReflection()
        {
            if (_reflectionResolved)
            {
                return;
            }

            _reflectionResolved = true;
            _characterField = AccessTools.Field(
                typeof(UIOccupationsChangeSchedule), "_character");
            _occupationField = AccessTools.Field(
                typeof(UIOccupationsChangeSchedule), "_occupation");
            _occupationIndexField = AccessTools.Field(
                typeof(UIOccupationsChangeSchedule), "_occupationIndex");
            if (_characterField == null
                || _occupationField == null
                || _occupationIndexField == null)
            {
                Plugin.Log.LogWarning(
                    "Could not read all occupation schedule context. Schedule choices remain "
                  + "usable, but their panel and confirmation labels may be generic.");
            }
        }

        private static string Clean(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            return ScreenReader.NormalizeForOutput(text.Trim());
        }

        private static void Reset()
        {
            _window = null;
            _panel = null;
            _schedulePanel = null;
            _daysEntry = null;
            _hoursEntry = null;
            _panelKind = PanelKind.None;
            _settleFrames = 0;
            _selectionSettleFrames = 0;
            _enterPressed = false;
            _pendingSelectionLabel = null;
            _pendingSelectionDropdown = null;
        }

        private static void Speak(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            Plugin.Log.LogInfo("[Occupation] " + message);
            ScreenReader.Speak(message);
        }
    }
}
