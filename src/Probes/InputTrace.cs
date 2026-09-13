using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace ParalivesAccess.Probes
{
    /// <summary>
    /// Logs physical keyboard events before the game's systems and the mod's handlers can act on
    /// them.
    /// </summary>
    /// <remarks>
    /// TEST 40 contained neither an undo action nor the old <c>ButtonUndo.Down</c> diagnostic. That
    /// left two materially different cases indistinguishable: Ctrl+Z was never physically pressed,
    /// or Unity saw the keys but the game's action did not fire.
    ///
    /// This probe runs from a PREFIX on <c>SystemManager.Update</c>. The game's
    /// <c>UpdateUndoRedo</c> system runs inside that method, while every mod handler runs from its
    /// postfix, so an <c>[Input/raw]</c> line is guaranteed to precede either kind of action in the
    /// log. The line also reports the current <c>HybridPlayer</c> input-wrapper state, making
    /// physical input and the game's interpretation independently visible.
    ///
    /// Privacy is deliberate. Outside text entry every key is useful diagnostic data, including a
    /// user-rebound action. While a TMP input field is selected, ordinary printable keys are not
    /// logged; only navigation/control keys and Ctrl-modified commands are retained. A typed name
    /// must never be reconstructed from the diagnostic log.
    /// </remarks>
    public static class InputTrace
    {
        /// <summary>Set false to silence raw input instrumentation without removing its hook.</summary>
        public static bool Enabled { get; set; } = true;

        private static readonly HashSet<Key> LoggedPressedKeys = new HashSet<Key>();
        private static int _lastFrame = -1;

        /// <summary>
        /// Harmony prefix on <c>SystemManager.Update</c>. Reads only; never consumes or changes input.
        /// </summary>
        public static void BeforeGameSystems()
        {
            if (!Enabled || _lastFrame == Time.frameCount)
            {
                return;
            }

            _lastFrame = Time.frameCount;

            try
            {
                Keyboard keyboard = Keyboard.current;
                if (keyboard == null)
                {
                    return;
                }

                bool textEntry = IsTextEntrySelected();
                var pressed = new List<string>();
                var released = new List<string>();

                foreach (KeyControl key in keyboard.allKeys)
                {
                    if (key.wasPressedThisFrame && ShouldLogPress(key, keyboard, textEntry))
                    {
                        pressed.Add(DescribePress(key, keyboard));
                        LoggedPressedKeys.Add(key.keyCode);
                    }

                    if (key.wasReleasedThisFrame && ShouldLogRelease(key, textEntry))
                    {
                        released.Add(key.keyCode.ToString());
                    }

                    if (key.wasReleasedThisFrame)
                    {
                        LoggedPressedKeys.Remove(key.keyCode);
                    }
                }

                if (pressed.Count == 0 && released.Count == 0)
                {
                    return;
                }

                HybridPlayer hybridPlayer = PlayerManager.Instance != null
                    ? PlayerManager.Instance.HybridPlayer1
                    : null;

                bool controlActive = IsControlActive(keyboard);
                bool physicalUndo = controlActive && keyboard.zKey.wasPressedThisFrame;
                bool physicalRedo = controlActive && keyboard.yKey.wasPressedThisFrame;

                var message = new StringBuilder();
                message.Append("[Input/raw] frame=").Append(Time.frameCount);

                if (pressed.Count > 0)
                {
                    message.Append(" pressed=[").Append(string.Join(", ", pressed.ToArray())).Append(']');
                }

                if (released.Count > 0)
                {
                    message.Append(" released=[").Append(string.Join(", ", released.ToArray())).Append(']');
                }

                message.Append(" | textEntry=").Append(textEntry);
                message.Append(" gameInput=[")
                    .Append(DescribeActiveGameInput(hybridPlayer, textEntry))
                    .Append(']');

                Plugin.Log.LogInfo(message.ToString());

                if (physicalUndo || physicalRedo
                    || (hybridPlayer != null
                        && (hybridPlayer.ButtonUndo.Down || hybridPlayer.ButtonRedo.Down)))
                {
                    LogUndoEvidence(hybridPlayer, physicalUndo, physicalRedo);
                }
            }
            catch (Exception ex)
            {
                // This is a Harmony prefix on the game's central loop. Disable the diagnostic rather
                // than ever allowing an instrumentation failure to enter game code.
                Enabled = false;
                Plugin.Log.LogError("InputTrace disabled after an exception: " + ex);
            }
        }

        private static bool IsTextEntrySelected()
        {
            EventSystem eventSystem = EventSystem.current;
            GameObject selected = eventSystem != null ? eventSystem.currentSelectedGameObject : null;

            return selected != null
                && selected.activeInHierarchy
                && selected.GetComponent<TMP_InputField>() != null;
        }

        private static bool ShouldLogPress(KeyControl key, Keyboard keyboard, bool textEntry)
        {
            if (!textEntry)
            {
                return true;
            }

            Key code = key.keyCode;
            if (IsModifier(code) || IsControlActive(keyboard))
            {
                return true;
            }

            // Keys that can leave or navigate a field, or invoke a non-text command. Printable
            // punctuation, numbers and letters deliberately do not appear here.
            switch (code)
            {
                case Key.Enter:
                case Key.NumpadEnter:
                case Key.Escape:
                case Key.Tab:
                case Key.UpArrow:
                case Key.DownArrow:
                case Key.LeftArrow:
                case Key.RightArrow:
                case Key.Home:
                case Key.End:
                case Key.PageUp:
                case Key.PageDown:
                case Key.F1:
                case Key.F2:
                case Key.F3:
                case Key.F4:
                case Key.F5:
                case Key.F6:
                case Key.F7:
                case Key.F8:
                case Key.F9:
                case Key.F10:
                case Key.F11:
                case Key.F12:
                    return true;
                default:
                    return false;
            }
        }

        private static bool ShouldLogRelease(KeyControl key, bool textEntry)
        {
            return !textEntry || LoggedPressedKeys.Contains(key.keyCode) || IsModifier(key.keyCode);
        }

        private static string DescribePress(KeyControl key, Keyboard keyboard)
        {
            Key code = key.keyCode;
            if (IsModifier(code))
            {
                return code.ToString();
            }

            var parts = new List<string>();
            if (IsControlActive(keyboard))
            {
                parts.Add("Ctrl");
            }

            if (IsAltActive(keyboard))
            {
                parts.Add("Alt");
            }

            if (IsShiftActive(keyboard))
            {
                parts.Add("Shift");
            }

            if (IsMetaActive(keyboard))
            {
                parts.Add("Meta");
            }

            parts.Add(code.ToString());
            return string.Join("+", parts.ToArray());
        }

        private static string DescribeActiveGameInput(HybridPlayer hybridPlayer, bool textEntry)
        {
            if (hybridPlayer == null)
            {
                return "no HybridPlayer";
            }

            // Any game action can be user-rebound to a printable key. Reporting even the action
            // name could therefore reveal a typed character when an allowed navigation key occurs
            // on the same frame. The separate Ctrl+Z/Y diagnostic remains available below.
            if (textEntry)
            {
                return "suppressed for text privacy";
            }

            var active = new List<string>();
            AddDown(active, "Undo", hybridPlayer.ButtonUndo);
            AddDown(active, "Redo", hybridPlayer.ButtonRedo);
            AddDown(active, "Confirm", hybridPlayer.ButtonConfirm);
            AddDown(active, "Cancel", hybridPlayer.ButtonCancel);

            AddDown(active, "RotateItemLeft", hybridPlayer.ButtonSmallRotateItemLeft);
            AddDown(active, "RotateItemRight", hybridPlayer.ButtonSmallRotateItemRight);
            AddDown(active, "RotateItemLarge", hybridPlayer.ButtonLargeRotateItem);
            AddInterval(active, "DPadLeft", hybridPlayer.ButtonDPadLeft);
            AddInterval(active, "DPadRight", hybridPlayer.ButtonDPadRight);
            AddInterval(active, "DPadDown", hybridPlayer.ButtonDPadDown);
            AddInterval(active, "DPadUp", hybridPlayer.ButtonDPadUp);
            AddInterval(active, "LeftShoulder", hybridPlayer.ButtonLeftShoulder);
            AddInterval(active, "RightShoulder", hybridPlayer.ButtonRightShoulder);

            if (hybridPlayer.CameraMovement.IntervalPressed)
            {
                active.Add("CameraMovement.Interval=" + hybridPlayer.CameraMovement.Value);
            }

            return active.Count > 0 ? string.Join(", ", active.ToArray()) : "none";
        }

        private static void AddDown(List<string> active, string name, InputButton input)
        {
            if (input != null && input.Down)
            {
                active.Add(name + ".Down");
            }
        }

        private static void AddInterval(List<string> active, string name, InputButton input)
        {
            if (input != null && input.IntervalPressed)
            {
                active.Add(name + ".Interval");
            }
        }

        private static void LogUndoEvidence(
            HybridPlayer hybridPlayer, bool physicalUndo, bool physicalRedo)
        {
            var message = new StringBuilder();
            message.Append("[Input/undo] frame=").Append(Time.frameCount)
                .Append(" physicalUndo=").Append(physicalUndo)
                .Append(" physicalRedo=").Append(physicalRedo);

            if (hybridPlayer == null)
            {
                message.Append(" | HybridPlayer=(null)");
                Plugin.Log.LogInfo(message.ToString());
                return;
            }

            message.Append(" | undoDown=").Append(hybridPlayer.ButtonUndo.Down)
                .Append(" undoHeld=").Append(hybridPlayer.ButtonUndo.Pressed)
                .Append(" redoDown=").Append(hybridPlayer.ButtonRedo.Down)
                .Append(" redoHeld=").Append(hybridPlayer.ButtonRedo.Pressed)
                .Append(" hasInputFieldFocused=").Append(hybridPlayer.HasInputFieldFocused);

            Player player = hybridPlayer.Player;
            if (player == null)
            {
                message.Append(" player=(null)");
                Plugin.Log.LogInfo(message.ToString());
                return;
            }

            try
            {
                bool undo = physicalUndo || hybridPlayer.ButtonUndo.Down;
                UIForm form = UI.Instance != null ? UI.GetOrNull<UIForm>(player.PlayerIndex) : null;

                int animatedItems = ItemManager.Instance != null
                    ? ItemManager.Instance.AnimatedItems.Count
                    : -1;

                int animatedSegments = SegmentManager.Instance != null
                    ? SegmentManager.Instance.AnimatedSegments.Count
                    : -1;

                string wallRequests = WallMeshCreationManager.Instance != null
                    ? WallMeshCreationManager.Instance.HasRequests.ToString()
                    : "?";

                int targetAction = UndoRedoManager.Instance != null
                    ? UndoRedoManager.Instance.GetTargetActionID(player.PlayerIndex, undo)
                    : (undo ? int.MinValue : int.MaxValue);

                bool hasTarget = undo ? targetAction != int.MinValue : targetAction != int.MaxValue;

                message.Append(" | state=").Append(player.State)
                    .Append(" actionsAllowed=").Append(player.PlayerActionsAreAllowed)
                    .Append(" canUndoRedo=").Append(player.CanUndoRedo())
                    .Append(" animatedItems=").Append(animatedItems)
                    .Append(" animatedSegments=").Append(animatedSegments)
                    .Append(" wallRequests=").Append(wallRequests)
                    .Append(" uiFormVisible=").Append(form != null && form.IsVisible)
                    .Append(" hasTarget=").Append(hasTarget)
                    .Append(" targetActionID=").Append(hasTarget ? targetAction.ToString() : "none");
            }
            catch (Exception ex)
            {
                // These are external game reads. Keep the raw evidence even if one optional gate
                // cannot be inspected in a particular loading state.
                message.Append(" | gateReadFailed=")
                    .Append(ex.GetType().Name)
                    .Append(": ")
                    .Append(ex.Message);
            }

            Plugin.Log.LogInfo(message.ToString());
        }

        private static bool IsModifier(Key key)
        {
            switch (key)
            {
                case Key.LeftCtrl:
                case Key.RightCtrl:
                case Key.LeftShift:
                case Key.RightShift:
                case Key.LeftAlt:
                case Key.RightAlt:
                case Key.LeftMeta:
                case Key.RightMeta:
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsControlActive(Keyboard keyboard)
        {
            return IsDownThisFrame(keyboard.leftCtrlKey) || IsDownThisFrame(keyboard.rightCtrlKey);
        }

        private static bool IsShiftActive(Keyboard keyboard)
        {
            return IsDownThisFrame(keyboard.leftShiftKey) || IsDownThisFrame(keyboard.rightShiftKey);
        }

        private static bool IsAltActive(Keyboard keyboard)
        {
            return IsDownThisFrame(keyboard.leftAltKey) || IsDownThisFrame(keyboard.rightAltKey);
        }

        private static bool IsMetaActive(Keyboard keyboard)
        {
            return IsDownThisFrame(keyboard.leftMetaKey) || IsDownThisFrame(keyboard.rightMetaKey);
        }

        private static bool IsDownThisFrame(KeyControl key)
        {
            return key != null && (key.isPressed || key.wasPressedThisFrame);
        }
    }
}
