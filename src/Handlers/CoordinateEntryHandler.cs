using System;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using ParalivesAccess.Speech;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ParalivesAccess.Handlers
{
    /// <summary>Uses a native single-field form to review two placement coordinates without committing.</summary>
    internal static class CoordinateEntryHandler
    {
        internal static bool Enabled { get; set; } = true;
        private static UIForm _form;
        private static UIFormItemData _data;
        private static Player _player;
        private static ItemObjectInPlacement _preview;
        private static GameStates _state;
        private static Func<bool> _valid;
        private static Action<double, double> _apply;
        private static Action<string> _applyText;
        private static string _submitted;
        private static string _hint, _instructions, _title;
        private static string _cancelKey;
        private static bool _announceReview, _singleValue;
        private static double _depthWidth;
        private static bool _wasBlocking, _submitRequested;
        private static int _closedFrame = -1;
        private static readonly System.Reflection.FieldInfo FormItems = AccessTools.Field(typeof(UIForm), "_currentFormItems");
        internal static bool BlocksPlacement => _form != null || _closedFrame == Time.frameCount;

        internal static bool ApplyPatches(Harmony harmony)
        {
            if (FormItems == null) return false;
            try
            {
                var prefix = AccessTools.Method(typeof(CoordinateEntryHandler), nameof(AllowNativePlacement));
                foreach (var type in new[] { typeof(UpdateMoveItem), typeof(EndMoveItem), typeof(CancelMoveItem),
                    typeof(StartCreateSegment), typeof(CheckToReplaceSegment), typeof(EndCreateSegment), typeof(UpdateCreateSegment), typeof(CancelCreateSegment) })
                {
                    var method = AccessTools.Method(type, "UpdateForPlayer", new[] { typeof(Player) });
                    harmony.Patch(method, prefix: new HarmonyMethod(prefix));
                    if (Harmony.GetPatchInfo(method)?.Prefixes.Any(p => p.PatchMethod == prefix && p.owner == Plugin.PluginGuid) != true) return false;
                }
                return true;
            }
            catch (Exception ex) { Plugin.Log.LogError("Coordinate input isolation hooks failed: " + ex); return false; }
        }

        private static bool AllowNativePlacement(Player player) => player != _player || !BlocksPlacement;

        internal static bool WasRequested(HybridPlayer hybrid)
        {
            if (!ModInputKey.WasPressed(Keyboard.current, Key.F10)) return false;
            return true; // Open checks the binding; consume a conflicting key without another placement action.
        }

        internal static bool TryParse(string text, out double first, out double second)
        {
            first = second = 0;
            var parts = (text ?? "").Split(';');
            return parts.Length == 2 && Number(parts[0], out first) && Number(parts[1], out second);
        }

        private static bool Number(string text, out double value)
        {
            const NumberStyles style = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite;
            return (double.TryParse(text, style, CultureInfo.CurrentCulture, out value)
                || double.TryParse(text, style, CultureInfo.InvariantCulture, out value)) && PlacementGrid.Finite(value)
                && Math.Abs(value) <= 1000000; // Bound conversion to Unity world floats; never accept NaN/infinity/exponents.
        }

        internal static void Open(HybridPlayer hybrid, ItemObjectInPlacement preview, string hint,
            double first, double second, Func<bool> valid, Action<double, double> apply,
            Key openingKey = Key.F10, string title = null, string instructions = null, bool announceReview = true,
            bool singleValue = false)
        {
            if (openingKey != Key.None && !ModInputKey.IsAvailable(hybrid, openingKey, out string conflict)) { Speak(conflict); return; }
            if (!Enabled) { Speak(Loc.Get("coordinate_unavailable")); return; }
            var form = UI.Get<UIForm>(hybrid.Player.PlayerIndex);
            if (form.IsVisible || BlocksPlacement) { Speak(Loc.Get("coordinate_busy")); return; }
            _form = form; _preview = preview; _player = hybrid.Player; _valid = valid; _apply = apply; _submitted = null;
            _cancelKey = "coordinate_cancelled";
            _submitRequested = false;
            _applyText = null;
            _state = _player.State;
            _hint = hint; _title = title ?? Loc.Get("coordinate_title");
            _instructions = instructions ?? Loc.Get("coordinate_hint"); _announceReview = announceReview;
            _singleValue = singleValue; _depthWidth = 0;
            _wasBlocking = form.BlockPlayerActionWhenShown; form.BlockPlayerActionWhenShown = true;
            _data = new UIFormItemData { LabelKey = "", InputFieldText = first.ToString("0.###", CultureInfo.CurrentCulture)
                + (singleValue ? "" : "; " + second.ToString("0.###", CultureInfo.CurrentCulture)), CharacterLimit = 64,
                SetAction = (index, text) => { if (OwnsForm()) _submitted = text; } };
            form.Show("", _data);
            form.LabelTitle.Text.text = _title;
            foreach (var row in form.UIList.CurrentItems.OfType<UIFormItem>()) row.Label.Text.text = hint;
            // The settled window announcement owns the instructions, after native text focus.
        }

        internal static void OpenDepth(HybridPlayer hybrid, string hint, double width, double depth,
            Func<bool> valid, Action<double> apply, string title, string instructions)
        {
            Open(hybrid, null, hint, depth, 0, valid, (value, unused) => apply(value), Key.None,
                title, instructions, false, true);
            // Open can refuse a busy shared form. Do not alter another request's parser.
            if (_form != null && _valid == valid && _singleValue && PlacementGrid.Finite(width) && width > 0)
                _depthWidth = width;
        }

        internal static bool TryParseDepth(string text, double width, out double depth, out bool widthMismatch)
        {
            widthMismatch = false;
            if (Number(text, out depth)) return true;
            if (width <= 0 || !TryParse(text, out double requestedWidth, out depth)) return false;
            // Accept a width copied from the native two-decimal displayed measurement.
            if (Math.Abs(requestedWidth - width) <= .0051) return true;
            widthMismatch = true;
            return false;
        }

        /// <summary>Uses the same native form ownership and close-frame guards for a room label.</summary>
        internal static void OpenText(HybridPlayer hybrid, string title, string hint, string instructions,
            string initial, Func<bool> valid, Action<string> apply, ItemObjectInPlacement preview = null,
            string cancelKey = "room_label_cancelled")
        {
            Open(hybrid, preview, hint, 0, 0, valid, null, Key.None, title, instructions, false, true);
            if (_form == null || _valid != valid) return;
            _applyText = apply;
            _cancelKey = cancelKey;
            _data.InputFieldText = initial ?? "";
            _data.CharacterLimit = 64;
            foreach (var row in _form.UIList.CurrentItems.OfType<UIFormItem>()) row.InputField.text = _data.InputFieldText;
        }

        internal static string DescribeForm(UIWindow window)
        {
            if (window != _form || !OwnsForm() || !_form.IsVisible) return null;
            var row = _form.UIList.CurrentItems.OfType<UIFormItem>().FirstOrDefault();
            return Loc.Get("build_form_context", _title, _hint, row?.InputField?.text ?? _data.InputFieldText, _instructions);
        }

        private static bool OwnsForm() => _form != null && FormItems?.GetValue(_form) is UIFormItemData[] items
            && items.Length == 1 && ReferenceEquals(items[0], _data);

        internal static void Tick()
        {
            if (_form == null) return;
            try
            {
                if (!OwnsForm()) { Release(false); Speak(Loc.Get("coordinate_changed")); return; }
                if (_player?.ItemInPlacement != _preview || _player.State != _state || !_valid())
                { Release(true); Speak(Loc.Get("coordinate_changed")); return; }
                var keyboard = Keyboard.current;
                if (keyboard != null)
                {
                    // UIFormItem checks legacy Input.GetKeyDown during TMP end-edit. Retain
                    // intent from the input system we actually observe, and use the native OK
                    // callback on release if that path did not submit. Never infer Enter from a
                    // disappearing form, and never turn Escape into confirmation.
                    if (keyboard.escapeKey.isPressed || keyboard.escapeKey.wasReleasedThisFrame)
                    {
                        _submitRequested = false;
                        _submitted = null;
                        // Native Back skips windows while an item is a cancelable action.
                        // We own this form; close it here, keeping placement blocked through
                        // the whole Escape press/release so the preview is not cancelled too.
                        // Otherwise native Back must close the window itself: closing it early
                        // would let the same press fall through to the pause menu.
                        if (_form.IsVisible && _player.IsDoingCancelableAction) _form.Back();
                    }
                    else if (_form.IsVisible && (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame))
                        _submitRequested = true;
                    if (_form.IsVisible && _submitRequested && _submitted == null
                        && (keyboard.enterKey.wasReleasedThisFrame || keyboard.numpadEnterKey.wasReleasedThisFrame))
                    {
                        Plugin.Log.LogInfo("[BuildForm] Completing owned Enter through native OK callback.");
                        _form.ConfirmPressed();
                    }
                }
                if (_form.IsVisible) return;
                // A form's Enter or Escape belongs to text editing, including its release frame.
                if (keyboard != null && (keyboard.enterKey.isPressed || keyboard.numpadEnterKey.isPressed
                    || keyboard.enterKey.wasReleasedThisFrame || keyboard.numpadEnterKey.wasReleasedThisFrame
                    || keyboard.escapeKey.isPressed || keyboard.escapeKey.wasReleasedThisFrame)) return;
                var hybrid = PlayerManager.Instance?.HybridPlayer1;
                if (hybrid?.Player != _player || hybrid.HasInputFieldFocused || _player.PlayerActionBlockedByUIWindow) return;
                string text = _submitted; var apply = _apply; var applyText = _applyText; string cancelKey = _cancelKey;
                bool announceReview = _announceReview, singleValue = _singleValue;
                double depthWidth = _depthWidth;
                Release(false);
                double first = 0, second = 0; bool widthMismatch = false;
                if (text == null) Speak(Loc.Get(cancelKey));
                else if (applyText != null) applyText(text);
                else if (!(singleValue ? TryParseDepth(text, depthWidth, out first, out widthMismatch) : TryParse(text, out first, out second)))
                    Speak(widthMismatch ? Loc.Get("extension_width_fixed", depthWidth.ToString("0.###", CultureInfo.CurrentCulture))
                        : Loc.Get(singleValue ? "extension_depth_invalid" : "coordinate_invalid"));
                else
                {
                    Plugin.Log.LogInfo($"[BuildForm] Applying numeric request: first={first}, second={second}, single={singleValue}.");
                    apply(first, second);
                    if (announceReview) Speak(Loc.Get("coordinate_review"));
                }
            }
            catch (Exception ex) { Release(false); Plugin.Log.LogError("Coordinate entry failed: " + ex); Speak(Loc.Get("coordinate_unavailable")); }
        }

        internal static void Cancel() { if (_form != null) Release(OwnsForm()); }

        private static void Release(bool hide)
        {
            var form = _form;
            if (form != null)
            {
                if (hide && OwnsForm() && form.IsVisible) form.Hide();
                // Show can reuse this shared form for another workflow. Always undo our flag
                // override, but never close or submit a form whose data no longer belongs to us.
                form.BlockPlayerActionWhenShown = _wasBlocking;
            }
            _form = null; _data = null; _preview = null; _valid = null; _apply = null; _applyText = null; _submitted = null;
            _submitRequested = false;
            _closedFrame = Time.frameCount;
            LotBuildInputOwnership.HoldThroughCurrentFrame();
        }

        private static void Speak(string text)
        {
            Plugin.Log.LogInfo("[BuildForm] " + text);
            ScreenReader.Speak(text);
        }
    }
}
