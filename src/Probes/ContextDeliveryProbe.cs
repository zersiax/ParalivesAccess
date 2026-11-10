using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace ParalivesAccess.Probes
{
    /// <summary>Traces F3 from keyboard events to native speech submission without changing either.</summary>
    internal static class ContextDeliveryProbe
    {
        internal static bool Enabled { get; set; }

        // A bounded diagnostic window, not a delay or a speech/input scheduling rule.
        private const float ObservationSeconds = 15f;
        private static readonly Dictionary<int, bool> F3ByDevice = new Dictionary<int, bool>();
        private static bool _installed;
        private static int _request;
        private static int _eventSequence;
        private static int _sampledEventSequence;
        private static int _speechSequence;
        private static float _observeUntil = -1f;

        private static bool Observing => Enabled && _request != 0 && Time.unscaledTime <= _observeUntil;

        internal static void Install()
        {
            if (!Enabled || _installed || Keyboard.current == null) return;
            try
            {
                InputSystem.onEvent += OnInputEvent;
                _installed = true;
                Plugin.Log.LogInfo("[ContextDelivery] F3 event and speech audit enabled.");
            }
            catch (Exception ex)
            {
                Enabled = false;
                Plugin.Log.LogError("Context delivery audit installation failed: " + ex);
            }
        }

        private static void Arm(string source)
        {
            _request++;
            _observeUntil = Time.unscaledTime + ObservationSeconds;
            Plugin.Log.LogInfo($"[ContextDelivery] request={_request}, source={source}, frame={Time.frameCount}, focused={Application.isFocused}.");
        }

        private static void OnInputEvent(InputEventPtr inputEvent, InputDevice device)
        {
            if (!Enabled || !(device is Keyboard keyboard)
                || (!inputEvent.IsA<StateEvent>() && !inputEvent.IsA<DeltaStateEvent>())) return;
            try
            {
                float value;
                if (!keyboard.f3Key.ReadValueFromEvent(inputEvent, out value)) return;
                bool down = keyboard.f3Key.IsValueConsideredPressed(value);
                bool previous;
                if (!F3ByDevice.TryGetValue(device.deviceId, out previous))
                    previous = keyboard.f3Key.isPressed;
                F3ByDevice[device.deviceId] = down;
                if (down == previous) return;
                if (down) Arm("keyboard-event");
                _eventSequence++;
                Plugin.Log.LogInfo($"[ContextDelivery] request={_request}, event={_eventSequence}, "
                    + $"F3={(down ? "down" : "up")}, device={device.deviceId}, "
                    + $"eventTime={inputEvent.time:0.000000}, frame={Time.frameCount}, handled={inputEvent.handled}.");
            }
            catch (Exception ex)
            {
                Enabled = false;
                Plugin.Log.LogError("Context delivery keyboard audit disabled: " + ex);
            }
        }

        internal static void BeforeGameSystems()
        {
            if (!Enabled) return;
            try
            {
                Keyboard keyboard = Keyboard.current;
                if (keyboard == null) return;
                bool pressed = keyboard.f3Key.wasPressedThisFrame;
                bool released = keyboard.f3Key.wasReleasedThisFrame;
                if (_sampledEventSequence == _eventSequence && !pressed && !released) return;
                if (pressed && !Observing) Arm("frame-sample-without-event");
                _sampledEventSequence = _eventSequence;
                HybridPlayer hybrid = PlayerManager.Instance?.HybridPlayer1;
                Plugin.Log.LogInfo($"[ContextDelivery] request={_request}, phase=before-systems, "
                    + $"frame={Time.frameCount}, event={_eventSequence}, device={keyboard.deviceId}, "
                    + $"pressed={pressed}, released={released}, held={keyboard.f3Key.isPressed}, "
                    + $"shift={keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed}, "
                    + $"focused={Application.isFocused}, state={GameLoadingManager.State}, "
                    + $"textEntry={hybrid?.HasInputFieldFocused}, rebinding={Handlers.SettingsHandler.IsCapturingBindingInput}.");
            }
            catch (Exception ex)
            {
                Enabled = false;
                Plugin.Log.LogError("Context delivery frame audit disabled: " + ex);
            }
        }

        internal static int BeginOutput(string source, string text, bool requestedInterrupt,
            bool effectiveInterrupt, string backend, bool useOutput)
        {
            if (!Observing) return 0;
            int sequence = ++_speechSequence;
            // Log metadata only. The normal context report already records its public game text;
            // subsequent text-entry/focus output must not be copied into this diagnostic window.
            Plugin.Log.LogInfo($"[ContextDelivery] request={_request}, output={sequence}, phase=submit, "
                + $"source={source ?? "other"}, frame={Time.frameCount}, time={Time.unscaledTime:0.000000}, "
                + $"characters={text?.Length ?? 0}, requestedInterrupt={requestedInterrupt}, "
                + $"effectiveInterrupt={effectiveInterrupt}, backend={backend}, api={(useOutput ? "output" : "speak")}.");
            return sequence;
        }

        internal static void EndOutput(int sequence, string result)
        {
            if (sequence == 0) return;
            Plugin.Log.LogInfo($"[ContextDelivery] request={_request}, output={sequence}, phase=returned, "
                + $"frame={Time.frameCount}, time={Time.unscaledTime:0.000000}, nativeResult={result}. "
                + "A successful submission is not proof of audible playback.");
        }

        internal static void NoteStop()
        {
            if (Observing)
                Plugin.Log.LogInfo($"[ContextDelivery] request={_request}, phase=stop-request, frame={Time.frameCount}.");
        }
    }
}
