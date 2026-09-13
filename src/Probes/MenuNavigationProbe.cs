using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using ParalivesAccess.Handlers;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;

namespace ParalivesAccess.Probes
{
    /// <summary>
    /// Debug-gated diagnostic that reports why menu navigation does or does not reach the game's
    /// focus system. It only reads and logs; it never speaks and never changes game state.
    /// </summary>
    /// <remarks>
    /// WHY THIS EXISTS
    /// Test 8 found that neither keyboard nor gamepad navigation announces anything. The source
    /// documents how navigation is supposed to reach an announcement:
    ///
    ///   Back.Update -> UIManager.NavigateMenu -> CurrentWindow.NavigateMenuArrows
    ///     -> FocusableGroup.Navigate -> SetDefaultFocused -> Focusable.IsFocused = true
    ///
    /// Every link in that chain is source-verified, so the break has to be in the runtime values
    /// feeding it. This probe measures them rather than guessing:
    ///  - is a gamepad visible to Unity's Input System at all, and is it paired to PlayerInput
    ///  - does CameraMovement / DPad input reach the HybridPlayer input wrappers
    ///  - is UIManager.CurrentWindow null (NavigateMenu early-returns when it is)
    ///  - which windows are visible, do they register for the back button, and do their
    ///    FocusableGroups actually contain Focusables
    ///
    /// LEADING HYPOTHESIS - NOT CONFIRMED, which is the point of this probe.
    /// UIMainMenu may not be registered for the back button, which would leave CurrentWindow null
    /// at the main menu and make UIManager.NavigateMenu return without doing anything - matching
    /// the observed silence on both keyboard and gamepad. Source basis: UIMainMenu.Update sets
    ///   MainPanel.IsVisible = !HybridPlayer1.UIManager.HasVisibleWindowRegisteredForBackButton
    /// which would permanently hide the menu's own panel if the menu registered itself. The
    /// deciding value is UIWindow.RegisterWindowForEscapeButtonEvent, a serialized field whose
    /// value lives in the prefab, so it cannot be read from decompiled source - only measured here.
    ///
    /// Reflection is used for the game's private window and focusable lists. That is acceptable
    /// here: this is diagnostic mod-internal code, and the try-catch around external/reflective
    /// calls keeps diagnostics from interrupting the game.
    /// </remarks>
    public static class MenuNavigationProbe
    {
        /// <summary>Set false to silence the probe without removing it.</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>Seconds between state snapshots, in unscaled time.</summary>
        private const float SnapshotIntervalSeconds = 2f;

        private static float _nextSnapshotTime;
        private static string _lastSnapshot;
        private static string _lastGroupInventory;
        private static GameObject _lastSelectedObject;
        private static bool _dumpedInputModule;

        private static FieldInfo _visibleWindowsField;
        private static FieldInfo _windowsForBackButtonField;
        private static FieldInfo _focusablesField;
        private static bool _reflectionResolved;

        /// <summary>
        /// Called once per frame from the game loop hook. Logs a state snapshot on a timer and
        /// logs navigation input the moment it arrives.
        /// </summary>
        public static void Tick()
        {
            if (!Enabled)
            {
                return;
            }

            try
            {
                HybridPlayer hybridPlayer = GetHybridPlayer();

                // Input is logged every frame it fires. IntervalPressed is set in
                // HybridPlayer.LateUpdate, so the value we read here is the same one Back.Update
                // saw this frame - which is exactly the comparison we need.
                if (hybridPlayer != null)
                {
                    LogInputActivity(hybridPlayer);
                }

                // Unity's EventSystem selection is a SECOND navigation system, independent of
                // Focusable. Tracked every frame because a selection change is an event, not a
                // state we can sample on a timer.
                TrackEventSystemSelection();

                if (Time.unscaledTime < _nextSnapshotTime)
                {
                    return;
                }

                _nextSnapshotTime = Time.unscaledTime + SnapshotIntervalSeconds;

                DumpInputModuleOnce();
                LogGroupInventory();

                string snapshot = BuildSnapshot(hybridPlayer);
                if (snapshot == _lastSnapshot)
                {
                    return;
                }

                _lastSnapshot = snapshot;
                Plugin.Log.LogInfo("[NavProbe] " + snapshot);
            }
            catch (Exception ex)
            {
                // This runs inside the game's own loop; never let it escape.
                Enabled = false;
                Plugin.Log.LogError("MenuNavigationProbe disabled after an exception: " + ex);
            }
        }

        private static void LogInputActivity(HybridPlayer hybridPlayer)
        {
            // These are the exact inputs FocusableGroup.NavigateMenuArrows tests, in its order.
            if (hybridPlayer.ButtonDPadLeft.IntervalPressed) LogInput("DPadLeft");
            if (hybridPlayer.ButtonDPadRight.IntervalPressed) LogInput("DPadRight");
            if (hybridPlayer.ButtonDPadDown.IntervalPressed) LogInput("DPadDown");
            if (hybridPlayer.ButtonDPadUp.IntervalPressed) LogInput("DPadUp");

            if (hybridPlayer.CameraMovement.IntervalPressed)
            {
                LogInput($"CameraMovement value={hybridPlayer.CameraMovement.Value} "
                       + $"magnitude={hybridPlayer.CameraMovement.Value.magnitude:0.00}");
            }

            if (hybridPlayer.ButtonLeftShoulder.IntervalPressed) LogInput("LeftShoulder");
            if (hybridPlayer.ButtonRightShoulder.IntervalPressed) LogInput("RightShoulder");
            if (hybridPlayer.ButtonConfirm.Down) LogInput("Confirm.Down");

            // TEST 36 left one question unanswerable: [UndoRedo] never fired, and nothing in the log
            // could distinguish "Ctrl+Z was never pressed" from "it was pressed and the game swallowed
            // it". UpdateUndoRedo gates on CanUndoRedo(), HasInputFieldFocused and a visible UIForm,
            // so a swallow is entirely possible. Logging the raw button plus the deciding values
            // separates the two in one run instead of another round of theorising.
            if (hybridPlayer.ButtonUndo.Down || hybridPlayer.ButtonRedo.Down)
            {
                Player player = hybridPlayer.Player;
                Plugin.Log.LogInfo(
                    $"[NavProbe] INPUT {(hybridPlayer.ButtonUndo.Down ? "Undo" : "Redo")} pressed | "
                  + $"canUndoRedo={(player != null ? player.CanUndoRedo().ToString() : "(no player)")} "
                  + $"actionsAllowed={(player != null ? player.PlayerActionsAreAllowed.ToString() : "?")} "
                  + $"hasInputFieldFocused={hybridPlayer.HasInputFieldFocused} "
                  + $"state={(player != null ? player.State.ToString() : "?")}");
            }
        }

        private static void LogInput(string what)
        {
            // Naming the window here is what distinguishes "input never arrived" from
            // "input arrived but NavigateMenu had nothing to navigate".
            Plugin.Log.LogInfo(
                $"[NavProbe] INPUT {what} | commonWindow={DescribeCurrentWindow(GetCommonManager())} "
              + $"playerWindow={DescribeCurrentWindow(GetPlayerManagerUi())}");
        }

        private static string BuildSnapshot(HybridPlayer hybridPlayer)
        {
            var sb = new StringBuilder();

            sb.Append("state=").Append(GameLoadingManager.State);
            sb.Append(" players=").Append(PlayerManager.Instance != null ? PlayerManager.Instance.Players.Count : -1);

            sb.Append(" | devices=").Append(DescribeDevices());

            if (hybridPlayer == null)
            {
                sb.Append(" | hybridPlayer=(null)");
                return sb.ToString();
            }

            PlayerInput playerInput = hybridPlayer.PlayerInput;
            if (playerInput == null)
            {
                sb.Append(" | playerInput=(null)");
            }
            else
            {
                sb.Append(" | scheme='").Append(playerInput.currentControlScheme).Append('\'');
                sb.Append(" isUsingGamePad=").Append(hybridPlayer.IsUsingGamePad);
                sb.Append(" pairedDevices=").Append(DescribePairedDevices(playerInput));
                sb.Append(" actionMap=").Append(
                    playerInput.currentActionMap != null
                        ? playerInput.currentActionMap.name + "/enabled=" + playerInput.currentActionMap.enabled
                        : "(null)");
            }

            sb.Append(" || COMMON ").Append(DescribeManager(GetCommonManager()));
            sb.Append(" || PLAYER ").Append(DescribeManager(GetPlayerManagerUi()));

            return sb.ToString();
        }

        private static string DescribeDevices()
        {
            var sb = new StringBuilder();
            foreach (InputDevice device in InputSystem.devices)
            {
                if (sb.Length > 0)
                {
                    sb.Append(',');
                }

                sb.Append(device.name).Append('[').Append(device.layout).Append(']');
            }

            return sb.Length > 0 ? sb.ToString() : "(none)";
        }

        private static string DescribePairedDevices(PlayerInput playerInput)
        {
            var sb = new StringBuilder();
            foreach (InputDevice device in playerInput.devices)
            {
                if (sb.Length > 0)
                {
                    sb.Append(',');
                }

                sb.Append(device.name);
            }

            return sb.Length > 0 ? sb.ToString() : "(none)";
        }

        /// <summary>
        /// Describes one UIManager: its CurrentWindow plus every visible window, whether that
        /// window is registered for the back button, and how many Focusables it holds.
        /// </summary>
        private static string DescribeManager(UIManager manager)
        {
            if (manager == null)
            {
                return "(null manager)";
            }

            var sb = new StringBuilder();
            sb.Append("currentWindow=").Append(DescribeCurrentWindow(manager));

            ResolveReflection();

            var visible = _visibleWindowsField?.GetValue(manager) as IEnumerable;
            var registered = _windowsForBackButtonField?.GetValue(manager) as IEnumerable;

            sb.Append(" visible=[").Append(DescribeWindowList(visible)).Append(']');
            sb.Append(" registeredForBack=[").Append(DescribeWindowList(registered)).Append(']');

            return sb.ToString();
        }

        private static string DescribeWindowList(IEnumerable windows)
        {
            if (windows == null)
            {
                return "(unreadable)";
            }

            var sb = new StringBuilder();
            foreach (object item in windows)
            {
                var window = item as UIWindow;
                if (window == null)
                {
                    continue;
                }

                if (sb.Length > 0)
                {
                    sb.Append(", ");
                }

                // BlockPlayerActionWhenShown / HideOtherInterfacesWhenShown are serialized prefab
                // values, unreadable from decompiled source. They are the candidate signal for
                // telling "modal covering the screen" apart from "another panel of this screen" -
                // the distinction the panel list currently cannot make.
                sb.Append(window.GetType().Name)
                  .Append("(registerForBack=").Append(window.RegisterWindowForEscapeButtonEvent)
                  .Append(", focusables=").Append(CountFocusables(window))
                  .Append(", sibling=").Append(window.transform.GetSiblingIndex())
                  .Append(", blocksActions=").Append(window.BlockPlayerActionWhenShown)
                  .Append(", hidesOthers=").Append(window.HideOtherInterfacesWhenShown)
                  .Append(", pausesTime=").Append(window.PauseTimeWhenShown)
                  .Append(')');
            }

            return sb.Length > 0 ? sb.ToString() : "empty";
        }

        /// <summary>
        /// Counts the distinct reachable controls in a group. Every UIWindow is a FocusableGroup,
        /// so zero means navigation would have nothing to move between even if it ran.
        /// </summary>
        private static string CountFocusables(FocusableGroup group)
        {
            ResolveReflection();

            var list = _focusablesField?.GetValue(group) as List<Focusable>;
            return list != null
                ? LogicalFocusNavigator.CountReachable(group, list).ToString()
                : "?";
        }

        private static string DescribeCurrentWindow(UIManager manager)
        {
            if (manager == null)
            {
                return "(null manager)";
            }

            UIWindow current = manager.CurrentWindow;
            return current != null ? current.GetType().Name : "(null)";
        }

        private static HybridPlayer GetHybridPlayer()
        {
            return PlayerManager.Instance != null ? PlayerManager.Instance.HybridPlayer1 : null;
        }

        private static UIManager GetCommonManager()
        {
            return HybridReferences.Instance != null ? HybridReferences.Instance.UIManagerCommon : null;
        }

        private static UIManager GetPlayerManagerUi()
        {
            HybridPlayer hybridPlayer = GetHybridPlayer();
            return hybridPlayer != null ? hybridPlayer.UIManager : null;
        }

        /// <summary>
        /// Logs changes to Unity's EventSystem selection.
        /// </summary>
        /// <remarks>
        /// The game runs an InputSystemUIInputModule (UIManager.cs casts currentInputModule to it),
        /// which drives Unity's own Selectable navigation through EventSystem.SetSelectedGameObject.
        /// That is entirely separate from Focusable/FocusableGroup and our announcements do not see
        /// it. If the d-pad activates UI while CurrentWindow is null, this is the candidate
        /// explanation - which is why it is measured rather than assumed.
        /// </remarks>
        private static void TrackEventSystemSelection()
        {
            EventSystem eventSystem = EventSystem.current;
            if (eventSystem == null)
            {
                return;
            }

            GameObject selected = eventSystem.currentSelectedGameObject;
            if (selected == _lastSelectedObject)
            {
                return;
            }

            _lastSelectedObject = selected;

            if (selected == null)
            {
                Plugin.Log.LogInfo("[NavProbe] EVENTSYSTEM selection cleared");
                return;
            }

            Plugin.Log.LogInfo(
                $"[NavProbe] EVENTSYSTEM selected '{selected.name}' "
              + $"label='{UiLabelReader.GetLabelOrFallback(selected.transform)}' "
              + $"hasFocusable={selected.GetComponent<Focusable>() != null}");
        }

        /// <summary>
        /// Lists every active FocusableGroup with its Focusable count and labels.
        /// </summary>
        /// <remarks>
        /// UIMainMenu reports focusables=0 while its buttons still announce on hover, so those
        /// Focusables must belong to some other group: Focusable.OnEnable registers with
        /// GetComponentInParent&lt;FocusableGroup&gt;(), the NEAREST ancestor, and UIContainer is
        /// itself a FocusableGroup. Which group actually owns them depends on the prefab hierarchy,
        /// so it is measured here. This decides which object the mod must drive to navigate.
        /// </remarks>
        private static void LogGroupInventory()
        {
            var groups = UnityEngine.Object.FindObjectsOfType<FocusableGroup>();
            var sb = new StringBuilder();

            foreach (FocusableGroup group in groups)
            {
                ResolveReflection();
                var list = _focusablesField?.GetValue(group) as List<Focusable>;
                List<Focusable> reachable = list != null
                    ? LogicalFocusNavigator.GetReachable(group, list)
                    : null;

                // Use the same reachability boundary as panel navigation and spoken counts.
                int count = reachable?.Count ?? -1;

                if (count <= 0)
                {
                    continue;
                }

                sb.Append("\n    ")
                  .Append(group.GetType().Name)
                  .Append(" '").Append(group.name).Append("'")
                  .Append(" isWindow=").Append(group is UIWindow)
                  .Append(" focusables=").Append(count)
                  .Append(" [").Append(DescribeFocusables(reachable)).Append(']');
            }

            string inventory = sb.Length > 0 ? sb.ToString() : " (no group holds any Focusable)";
            if (inventory == _lastGroupInventory)
            {
                return;
            }

            _lastGroupInventory = inventory;
            Plugin.Log.LogInfo("[NavProbe] GROUPS:" + inventory);
        }

        private static string DescribeFocusables(List<Focusable> focusables)
        {
            var sb = new StringBuilder();
            int shown = 0;

            foreach (Focusable focusable in focusables)
            {
                if (shown >= 6)
                {
                    sb.Append(", ...");
                    break;
                }

                if (shown > 0)
                {
                    sb.Append(" | ");
                }

                sb.Append(UiLabelReader.GetLabelOrFallback(focusable));
                shown++;
            }

            return sb.ToString();
        }

        /// <summary>
        /// Dumps the EventSystem input module and its navigation actions once.
        /// </summary>
        /// <remarks>
        /// These actions live in a serialized InputActionAsset that the §2 dump did not cover - it
        /// dumped PlayerInput.actions, a different asset. If the UI module has its own Navigate and
        /// Submit bindings, that is a second input path into the UI which the mod must account for.
        /// </remarks>
        private static void DumpInputModuleOnce()
        {
            if (_dumpedInputModule)
            {
                return;
            }

            EventSystem eventSystem = EventSystem.current;
            if (eventSystem == null || eventSystem.currentInputModule == null)
            {
                return;
            }

            _dumpedInputModule = true;

            BaseInputModule module = eventSystem.currentInputModule;
            var sb = new StringBuilder();
            sb.Append("module=").Append(module.GetType().Name);

            var uiModule = module as InputSystemUIInputModule;
            if (uiModule == null)
            {
                Plugin.Log.LogInfo("[NavProbe] INPUTMODULE " + sb + " (not an InputSystemUIInputModule)");
                return;
            }

            sb.Append(" actionsAsset=").Append(uiModule.actionsAsset != null ? uiModule.actionsAsset.name : "(null)");
            sb.Append(DescribeUiAction("move", uiModule.move));
            sb.Append(DescribeUiAction("submit", uiModule.submit));
            sb.Append(DescribeUiAction("cancel", uiModule.cancel));
            sb.Append(DescribeUiAction("point", uiModule.point));
            sb.Append(DescribeUiAction("leftClick", uiModule.leftClick));

            Plugin.Log.LogInfo("[NavProbe] INPUTMODULE " + sb);
        }

        private static string DescribeUiAction(string label, InputActionReference reference)
        {
            if (reference == null || reference.action == null)
            {
                return $"\n    {label}=(none)";
            }

            InputAction action = reference.action;
            var sb = new StringBuilder();
            sb.Append("\n    ").Append(label).Append("='").Append(action.name).Append('\'')
              .Append(" enabled=").Append(action.enabled)
              .Append(" bindings=");

            foreach (InputBinding binding in action.bindings)
            {
                sb.Append(binding.effectivePath).Append(' ');
            }

            return sb.ToString();
        }

        private static void ResolveReflection()
        {
            if (_reflectionResolved)
            {
                return;
            }

            _reflectionResolved = true;

            _visibleWindowsField = AccessTools.Field(typeof(UIManager), "_visibleWindows");
            _windowsForBackButtonField = AccessTools.Field(typeof(UIManager), "_visibleWindowsRegisteredForBackButton");
            _focusablesField = AccessTools.Field(typeof(FocusableGroup), "_focusables");

            if (_visibleWindowsField == null || _windowsForBackButtonField == null || _focusablesField == null)
            {
                Plugin.Log.LogWarning(
                    "NavProbe could not resolve one or more private fields "
                  + $"(_visibleWindows={_visibleWindowsField != null}, "
                  + $"_visibleWindowsRegisteredForBackButton={_windowsForBackButtonField != null}, "
                  + $"_focusables={_focusablesField != null}). "
                  + "The game's UI code may have changed - re-check docs/game-api.md section 4.");
            }
        }
    }
}
