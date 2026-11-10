using System;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace ParalivesAccess.Handlers
{
    /// <summary>
    /// Reads configurable physical keys and verifies them against the player's live game bindings.
    /// </summary>
    internal static class ModInputKey
    {
        internal static bool WasPressed(Keyboard keyboard, Key key)
        {
            if (key == Key.F11 && CameraPitchHandler.ConsumedThisFrame) return false;
            KeyControl control = GetControl(keyboard, key);
            return control != null && control.wasPressedThisFrame;
        }

        internal static bool IsAvailable(HybridPlayer hybridPlayer, Key key, out string conflict)
        {
            conflict = null;

            try
            {
                Keyboard keyboard = Keyboard.current;
                KeyControl control = GetControl(keyboard, key);
                if (control == null)
                {
                    conflict = Loc.Get("hotkey_missing", key);
                    return false;
                }

                PlayerInput playerInput = hybridPlayer != null ? hybridPlayer.PlayerInput : null;
                InputActionAsset actions = playerInput != null ? playerInput.actions : null;
                if (actions == null)
                {
                    conflict = Loc.Get("hotkey_unverified", key);
                    return false;
                }

                foreach (InputActionMap map in actions.actionMaps)
                {
                    if (!map.enabled)
                    {
                        continue;
                    }

                    foreach (InputAction action in map.actions)
                    {
                        if (!action.enabled)
                        {
                            continue;
                        }

                        foreach (InputBinding binding in action.bindings)
                        {
                            string path = binding.effectivePath;
                            if (!string.IsNullOrEmpty(path) && InputControlPath.Matches(path, control))
                            {
                                conflict = Loc.Get("hotkey_conflict", key, action.name);
                                return false;
                            }
                        }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Checking live binding for {key} threw: {ex}");
                conflict = Loc.Get("hotkey_unverified", key);
                return false;
            }
        }

        private static KeyControl GetControl(Keyboard keyboard, Key key)
        {
            if (keyboard == null || key == Key.None)
            {
                return null;
            }

            try
            {
                return keyboard[key];
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
    }
}
