using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace ParalivesAccess.Speech
{
    /// <summary>
    /// Delivers important world-state events after foreground input has settled and retains the
    /// newest event for explicit repetition.
    /// </summary>
    /// <remarks>
    /// Focus and control speech must remain interrupting: delayed focus makes keyboard navigation
    /// unusable. Notifications and resolved gameplay outcomes have the opposite requirement. A
    /// sighted player can return to their retained visual, while an utterance interrupted by the
    /// next arrow otherwise disappears completely. Important events therefore wait for a short
    /// physical-input quiet period, queue behind the foreground utterance, and remain available to
    /// the Shift+F3 repeat command.
    /// </remarks>
    public static class AnnouncementCoordinator
    {
        private const float MinimumEventDelaySeconds = 0.2f;
        private const float InputQuietSeconds = 0.35f;
        private const float ForegroundSpeechQuietSeconds = 0.35f;

        private sealed class PendingAnnouncement
        {
            public string Kind;
            public string Text;
            public float ReadyAt;
        }

        private static readonly List<PendingAnnouncement> Pending =
            new List<PendingAnnouncement>();

        private static float _lastPhysicalInputTime = float.NegativeInfinity;
        private static float _lastForegroundSpeechTime = float.NegativeInfinity;
        private static string _lastImportantEvent;
        private static bool _tutorialDeliveryProtected;

        /// <summary>Queues automatic speech behind tutorial guidance until the user acts again.</summary>
        internal static bool ResolveInterrupt(bool requested)
        {
            if (!_tutorialDeliveryProtected || !requested)
                return requested;
            // Pointer callbacks can precede SystemManager.Update. Observe real input here too,
            // so mouse navigation keeps the same immediate authority as keyboard input.
            BeforeGameSystems();
            return !_tutorialDeliveryProtected || GameLoadingManager.State != State.Game;
        }

        /// <summary>
        /// Records successful interrupting speech so retained events wait behind automatic window,
        /// focus, action, and popup announcements as well as physical input.
        /// </summary>
        internal static void NoteForegroundSpeech()
        {
            _lastForegroundSpeechTime = Time.unscaledTime;
        }

        /// <summary>
        /// Captures keyboard and pointer activity before game systems can turn it into focus speech.
        /// </summary>
        public static void BeforeGameSystems()
        {
            try
            {
                bool active = Handlers.ContextKeyInput.ContextPressed
                    || Handlers.ContextKeyInput.RepeatPressed;
                Keyboard keyboard = Keyboard.current;
                if (keyboard != null)
                {
                    // A modifier may legitimately remain latched for an entire accessible
                    // keyboard session (TEST 68 held Alt from frame 4 onward). Count physical
                    // edges here, not the persistent isPressed state; Tick separately blocks a
                    // held non-modifier so key repeat still cannot release an event mid-navigation.
                    foreach (KeyControl key in keyboard.allKeys)
                    {
                        if (key.wasPressedThisFrame || key.wasReleasedThisFrame)
                        {
                            active = true;
                            break;
                        }
                    }
                }

                Mouse mouse = Mouse.current;
                if (mouse != null)
                {
                    active |= mouse.leftButton.wasPressedThisFrame
                        || mouse.rightButton.wasPressedThisFrame
                        || mouse.middleButton.wasPressedThisFrame
                        || mouse.leftButton.wasReleasedThisFrame
                        || mouse.rightButton.wasReleasedThisFrame
                        || mouse.middleButton.wasReleasedThisFrame
                        || mouse.delta.ReadValue().sqrMagnitude > 0.01f
                        || mouse.scroll.ReadValue().sqrMagnitude > 0.01f;
                }

                Gamepad gamepad = Gamepad.current;
                if (gamepad != null)
                {
                    active |= gamepad.leftStick.ReadValue().sqrMagnitude > 0.01f
                        || gamepad.rightStick.ReadValue().sqrMagnitude > 0.01f;
                    foreach (InputControl control in gamepad.allControls)
                    {
                        if (control is ButtonControl button
                            && (button.wasPressedThisFrame || button.wasReleasedThisFrame))
                        {
                            active = true;
                            break;
                        }
                    }
                }

                if (active)
                {
                    _lastPhysicalInputTime = Time.unscaledTime;
                    _tutorialDeliveryProtected = false;
                }
            }
            catch (Exception ex)
            {
                // Input observation is an external API boundary. Keep already queued events rather
                // than allowing an input-device failure to enter the game's central update call.
                Plugin.Log.LogError("AnnouncementCoordinator input observation threw: " + ex);
            }
        }

        /// <summary>
        /// Retains and schedules an important event without interrupting foreground navigation.
        /// </summary>
        public static void PublishImportant(string kind, string text)
        {
            Publish(kind, text, replacePendingKind: false);
        }

        /// <summary>
        /// Retains and schedules the newest state snapshot of one kind, replacing an older pending
        /// snapshot which would already be stale when delivered.
        /// </summary>
        public static void PublishLatestImportant(string kind, string text)
        {
            Publish(kind, text, replacePendingKind: true);
        }

        private static void Publish(string kind, string text, bool replacePendingKind)
        {
            string clean = ScreenReader.NormalizeForOutput(text);
            if (string.IsNullOrWhiteSpace(clean))
            {
                return;
            }

            string cleanKind = string.IsNullOrWhiteSpace(kind) ? "event" : kind;
            _lastImportantEvent = clean;

            bool refreshed = false;
            for (int i = Pending.Count - 1; i >= 0; i--)
            {
                PendingAnnouncement existing = Pending[i];
                if (existing.Kind != cleanKind)
                {
                    continue;
                }

                if (existing.Text == clean && !refreshed)
                {
                    existing.ReadyAt = Time.unscaledTime + MinimumEventDelaySeconds;
                    refreshed = true;
                    if (!replacePendingKind)
                    {
                        Plugin.Log.LogInfo(
                            $"[Announcement] Refreshed pending important event kind={cleanKind}, text='{clean}'.");
                        return;
                    }
                    continue;
                }

                if (replacePendingKind)
                {
                    Pending.RemoveAt(i);
                }
            }

            if (refreshed)
            {
                Plugin.Log.LogInfo(
                    $"[Announcement] Refreshed the latest event and removed older pending "
                  + $"events of kind={cleanKind}, text='{clean}'.");
                return;
            }

            Pending.Add(new PendingAnnouncement
            {
                Kind = cleanKind,
                Text = clean,
                ReadyAt = Time.unscaledTime + MinimumEventDelaySeconds
            });
            Plugin.Log.LogInfo(
                $"[Announcement] Retained important event kind={cleanKind}, pending={Pending.Count}, "
              + $"text='{clean}'.");
        }

        /// <summary>Discards undelivered events of one kind without erasing repeat history.</summary>
        public static void DiscardPending(string kind)
        {
            if (string.IsNullOrWhiteSpace(kind))
            {
                return;
            }

            int removed = 0;
            for (int i = Pending.Count - 1; i >= 0; i--)
            {
                if (Pending[i].Kind == kind)
                {
                    Pending.RemoveAt(i);
                    removed++;
                }
            }

            if (removed > 0)
            {
                Plugin.Log.LogInfo(
                    $"[Announcement] Discarded {removed} pending event(s) of kind={kind}.");
            }
        }

        /// <summary>
        /// Repeats the newest retained important event immediately and removes a matching pending
        /// delivery so one explicit repeat cannot produce an echo later.
        /// </summary>
        /// <returns>True when an event existed and was submitted to the screen reader.</returns>
        public static bool RepeatLastImportant()
        {
            if (GameLoadingManager.State != State.Game)
            {
                ClearForNonGameState();
                return false;
            }

            if (string.IsNullOrWhiteSpace(_lastImportantEvent))
            {
                return false;
            }

            int discarded = Pending.Count;
            Pending.Clear();

            Plugin.Log.LogInfo(
                $"[Announcement] Repeating last important event and discarding {discarded} "
              + $"older pending event(s): {_lastImportantEvent}");
            ScreenReader.Speak(_lastImportantEvent);
            return true;
        }

        /// <summary>Releases settled important events behind current foreground speech.</summary>
        public static void Tick()
        {
            if (GameLoadingManager.State != State.Game)
            {
                ClearForNonGameState();
                return;
            }

            if (Pending.Count == 0)
            {
                return;
            }

            float now = Time.unscaledTime;
            if (now - _lastPhysicalInputTime < InputQuietSeconds)
            {
                return;
            }

            // Handler ordering only protects an event from speech produced earlier in the SAME
            // frame. TEST 75's tutorial completion was queued successfully, then an action result
            // and transition windows interrupted it on following frames. The central speech
            // boundary records those successful interrupts, so the event is released only after
            // the complete foreground burst has actually gone quiet.
            if (now - _lastForegroundSpeechTime < ForegroundSpeechQuietSeconds)
            {
                return;
            }

            if (HasForegroundInputHeld())
            {
                return;
            }

            PendingAnnouncement next = Pending[0];
            if (now < next.ReadyAt)
            {
                return;
            }

            Pending.RemoveAt(0);
            Plugin.Log.LogInfo(
                $"[Announcement] Delivering important event kind={next.Kind}, "
              + $"remaining={Pending.Count}, speechMode=queue-after-foreground: {next.Text}");
            if (ScreenReader.Speak(next.Text, interrupt: false) && next.Kind == "tutorial")
            {
                // Submission is not completion. Later automatic windows and actions must queue,
                // not cancel this utterance after it has already left Pending (TEST 77).
                _tutorialDeliveryProtected = true;
            }
        }

        private static bool HasForegroundInputHeld()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                foreach (KeyControl key in keyboard.allKeys)
                {
                    if (key.isPressed && !IsModifier(key.keyCode))
                    {
                        return true;
                    }
                }
            }

            Mouse mouse = Mouse.current;
            return mouse != null
                && (mouse.leftButton.isPressed
                    || mouse.rightButton.isPressed
                    || mouse.middleButton.isPressed);
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

        private static void ClearForNonGameState()
        {
            _tutorialDeliveryProtected = false;
            if (Pending.Count == 0 && string.IsNullOrWhiteSpace(_lastImportantEvent))
            {
                return;
            }

            Plugin.Log.LogInfo(
                $"[Announcement] Cleared {Pending.Count} pending event(s) and repeat history "
              + $"during state {GameLoadingManager.State}.");
            Pending.Clear();
            _lastImportantEvent = null;
            _lastForegroundSpeechTime = float.NegativeInfinity;
        }
    }
}
