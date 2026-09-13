using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;

namespace ParalivesAccess.Handlers
{
    /// <summary>Retains F3 down events, including taps released before the game samples the keyboard.</summary>
    internal static class ContextKeyInput
    {
        private static readonly Dictionary<int, bool> DownByDevice = new Dictionary<int, bool>();
        private static bool _installed;
        private static bool _pendingContext;
        private static bool _pendingRepeat;
        private static int _sampleFrame = -1;

        internal static bool ContextPressed { get; private set; }
        internal static bool RepeatPressed { get; private set; }

        internal static void Install()
        {
            if (_installed) return;
            try
            {
                InputSystem.onEvent += OnInputEvent;
                _installed = true;
                Plugin.Log.LogInfo("[ContextInput] F3 keyboard-event capture installed.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Installing F3 keyboard-event capture threw: " + ex);
            }
        }

        private static void OnInputEvent(InputEventPtr inputEvent, InputDevice device)
        {
            if (!(device is Keyboard keyboard)
                || (!inputEvent.IsA<StateEvent>() && !inputEvent.IsA<DeltaStateEvent>())) return;
            try
            {
                float value;
                if (!keyboard.f3Key.ReadValueFromEvent(inputEvent, out value)) return;
                bool down = keyboard.f3Key.IsValueConsideredPressed(value);
                bool previous;
                if (!DownByDevice.TryGetValue(device.deviceId, out previous))
                    previous = keyboard.f3Key.isPressed;
                DownByDevice[device.deviceId] = down;
                if (!down || previous || inputEvent.handled || !Application.isFocused
                    || SettingsHandler.IsCapturingBindingInput) return;

                // Resolve modifiers at the down event: Shift can also be released in this update.
                bool repeat = ReadPressed(keyboard.leftShiftKey, inputEvent)
                    || ReadPressed(keyboard.rightShiftKey, inputEvent);
                if (repeat) _pendingRepeat = true;
                else _pendingContext = true;
                Plugin.Log.LogInfo($"[ContextInput] F3 down retained, repeat={repeat}, "
                    + $"device={device.deviceId}, frame={Time.frameCount}.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Capturing F3 keyboard event threw: " + ex);
            }
        }

        private static bool ReadPressed(ButtonControl key, InputEventPtr inputEvent)
        {
            float value;
            return key.ReadValueFromEvent(inputEvent, out value)
                ? key.IsValueConsideredPressed(value) : key.isPressed;
        }

        /// <summary>Publishes one shared snapshot for context and activity consumers per game frame.</summary>
        internal static void BeforeGameSystems()
        {
            if (_sampleFrame == Time.frameCount) return;
            _sampleFrame = Time.frameCount;
            ContextPressed = _pendingContext;
            RepeatPressed = _pendingRepeat;
            _pendingContext = false;
            _pendingRepeat = false;
            if (!Application.isFocused || SettingsHandler.IsCapturingBindingInput)
            {
                ContextPressed = false;
                RepeatPressed = false;
            }
        }
    }
}
