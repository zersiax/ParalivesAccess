using System;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ParalivesAccess.Probes
{
    /// <summary>
    /// Diagnostic probe that dumps the game's real input bindings to a text file.
    /// </summary>
    /// <remarks>
    /// Why this exists: <c>Setting.KeyBindings</c> only stores user rebind *overrides*. The actual
    /// default bindings live in a serialized Unity <see cref="InputActionAsset"/>, which decompiled
    /// source cannot reveal. Assigning mod keys without this data would be guesswork, so this probe
    /// reads the bindings at runtime instead.
    ///
    /// This is a plain static class, not a MonoBehaviour: <see cref="Plugin"/> drives it from its own
    /// Update loop. See the design note on <see cref="Plugin"/> for why.
    ///
    /// Timing note: callers must poll on <see cref="Time.unscaledTime"/>, never WaitForSeconds —
    /// Paralives pauses <see cref="Time.timeScale"/> in menus, which silently stalled an earlier build.
    ///
    /// This is a temporary analysis tool, not a shipping feature. Delete it once
    /// docs/game-api.md sections 2 and 3 are filled in.
    /// </remarks>
    public static class InputBindingProbe
    {
        private static readonly string OutputPath =
            Path.Combine(Paths.BepInExRootPath, "ParalivesAccess-InputDump.txt");

        /// <summary>
        /// Writes the binding dump only if it does not already exist.
        /// </summary>
        /// <remarks>
        /// Bindings change when the game updates or the user rebinds. Regenerating automatically
        /// when the file is absent means deleting the dump is all it takes to refresh it, without
        /// re-dumping on every launch.
        /// </remarks>
        public static void DumpIfMissing()
        {
            try
            {
                if (File.Exists(OutputPath))
                {
                    return;
                }

                string blocker;
                if (!IsReady(out blocker))
                {
                    Plugin.Log.LogInfo("InputBindingProbe: not ready yet (" + blocker + "), skipping dump.");
                    return;
                }

                Plugin.Log.LogInfo("InputBindingProbe: no dump on disk, regenerating.");
                Dump();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("InputBindingProbe.DumpIfMissing threw: " + ex);
            }
        }

        /// <summary>Checks whether the input chain is reachable, reporting the first missing link.</summary>
        /// <param name="blocker">Describes what is not yet available, or null when ready.</param>
        /// <returns>True when bindings can be read.</returns>
        public static bool IsReady(out string blocker)
        {
            blocker = null;
            try
            {
                PlayerManager manager = PlayerManager.Instance;
                if (manager == null)
                {
                    blocker = "PlayerManager.Instance is null";
                    return false;
                }

                if (manager.HybridPlayer1 == null)
                {
                    blocker = "PlayerManager.Instance.HybridPlayer1 is null";
                    return false;
                }

                PlayerInput playerInput = manager.HybridPlayer1.PlayerInput;
                if (playerInput == null)
                {
                    blocker = "HybridPlayer1.PlayerInput is null (assigned in HybridPlayer.Start)";
                    return false;
                }

                if (playerInput.actions == null)
                {
                    blocker = "PlayerInput.actions is null";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                blocker = "readiness check threw: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>Writes the full binding dump to disk. Safe to call when not fully ready.</summary>
        public static void Dump()
        {
            StringBuilder sb = new StringBuilder();

            try
            {
                sb.AppendLine("ParalivesAccess - Input binding dump");
                sb.AppendLine("Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("Unity: " + Application.unityVersion);
                sb.AppendLine("Game version: " + Application.version);
                sb.AppendLine("timeScale at dump: " + Time.timeScale);
                sb.AppendLine();

                AppendDevices(sb);

                string blocker;
                if (!IsReady(out blocker))
                {
                    sb.AppendLine("=== INCOMPLETE DUMP ===");
                    sb.AppendLine("Input chain was not reachable. Blocked on: " + blocker);
                    sb.AppendLine("Action maps and bindings are MISSING from this dump.");
                    WriteFile(sb.ToString());
                    return;
                }

                HybridPlayer hybridPlayer = PlayerManager.Instance.HybridPlayer1;
                PlayerInput playerInput = hybridPlayer.PlayerInput;
                InputActionAsset actions = playerInput.actions;

                sb.AppendLine("=== CURRENT STATE ===");
                sb.AppendLine("currentControlScheme : " + playerInput.currentControlScheme);
                sb.AppendLine("IsUsingGamePad       : " + hybridPlayer.IsUsingGamePad);
                sb.AppendLine("actions.name         : " + actions.name);
                sb.AppendLine();

                AppendControlSchemes(sb, actions);
                AppendActionMaps(sb, actions);
                AppendHybridPlayerInputFields(sb, hybridPlayer);
            }
            catch (Exception ex)
            {
                // Reading the Input System is an external-API call; a failure here must be loud,
                // not silent, or we would draw conclusions from an incomplete dump.
                string message = "InputBindingProbe FAILED mid-dump: " + ex;
                sb.AppendLine();
                sb.AppendLine(message);
                Plugin.Log.LogError(message);
            }

            WriteFile(sb.ToString());
        }

        private static void AppendDevices(StringBuilder sb)
        {
            sb.AppendLine("=== CONNECTED DEVICES ===");
            try
            {
                foreach (InputDevice device in InputSystem.devices)
                {
                    sb.AppendLine($"  {device.displayName}  [layout={device.layout}, added={device.added}]");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("  device enumeration threw: " + ex.Message);
            }
            sb.AppendLine();
        }

        private static void AppendControlSchemes(StringBuilder sb, InputActionAsset actions)
        {
            sb.AppendLine("=== CONTROL SCHEMES ===");
            foreach (InputControlScheme scheme in actions.controlSchemes)
            {
                sb.AppendLine($"  {scheme.name}  (bindingGroup='{scheme.bindingGroup}')");
                foreach (InputControlScheme.DeviceRequirement requirement in scheme.deviceRequirements)
                {
                    sb.AppendLine($"      requires: {requirement.controlPath}  optional={requirement.isOptional}");
                }
            }
            sb.AppendLine();
        }

        private static void AppendActionMaps(StringBuilder sb, InputActionAsset actions)
        {
            sb.AppendLine("=== ACTION MAPS AND BINDINGS ===");
            sb.AppendLine("(effectivePath is what actually fires: overridePath if the user rebound it, else path)");
            sb.AppendLine();

            foreach (InputActionMap map in actions.actionMaps)
            {
                sb.AppendLine($"--- MAP: {map.name}  (enabled={map.enabled}, actions={map.actions.Count}) ---");

                foreach (InputAction action in map.actions)
                {
                    sb.AppendLine($"  ACTION: {action.name}   [type={action.type}]");

                    string display = SafeDisplayString(action);
                    if (!string.IsNullOrEmpty(display))
                    {
                        sb.AppendLine($"    display: {display}");
                    }

                    for (int i = 0; i < action.bindings.Count; i++)
                    {
                        InputBinding binding = action.bindings[i];

                        string kind = binding.isComposite
                            ? "COMPOSITE"
                            : (binding.isPartOfComposite ? "  part" : "binding");

                        sb.AppendLine($"    [{i,2}] {kind}  name='{binding.name}'  groups='{binding.groups}'");
                        sb.AppendLine($"         path         : {binding.path}");

                        if (!string.IsNullOrEmpty(binding.overridePath))
                        {
                            sb.AppendLine($"         overridePath : {binding.overridePath}   <-- USER REBOUND");
                        }

                        sb.AppendLine($"         effectivePath: {binding.effectivePath}");

                        if (!string.IsNullOrEmpty(binding.interactions))
                        {
                            sb.AppendLine($"         interactions : {binding.interactions}");
                        }
                    }

                    sb.AppendLine();
                }

                sb.AppendLine();
            }
        }

        private static string SafeDisplayString(InputAction action)
        {
            try
            {
                return action.GetBindingDisplayString();
            }
            catch (Exception ex)
            {
                return "<display string threw: " + ex.GetType().Name + ">";
            }
        }

        /// <summary>
        /// Dumps HybridPlayer's InputBase fields and the InputName each maps to.
        /// </summary>
        /// <remarks>
        /// Mirrors HybridPlayer.Start, which sets InputName = fieldName.Replace("Button", "").
        /// This is what correlates a code-side field such as ButtonDPadLeft to an action named DPadLeft.
        /// </remarks>
        private static void AppendHybridPlayerInputFields(StringBuilder sb, HybridPlayer hybridPlayer)
        {
            sb.AppendLine("=== HybridPlayer INPUT FIELDS (field -> InputName) ===");

            try
            {
                FieldInfo[] fields = hybridPlayer.GetType().GetFields();
                foreach (FieldInfo field in fields)
                {
                    if (!typeof(InputBase).IsAssignableFrom(field.FieldType))
                    {
                        continue;
                    }

                    InputBase input = field.GetValue(hybridPlayer) as InputBase;
                    string inputName = input == null ? "<null>" : input.InputName;
                    sb.AppendLine($"  {field.Name,-45} -> {inputName}   [{field.FieldType.Name}]");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("  Reflection over HybridPlayer fields failed: " + ex);
                Plugin.Log.LogError("InputBindingProbe reflection failed: " + ex);
            }

            sb.AppendLine();
        }

        private static void WriteFile(string contents)
        {
            try
            {
                File.WriteAllText(OutputPath, contents, Encoding.UTF8);
                Plugin.Log.LogInfo("InputBindingProbe wrote dump to: " + OutputPath);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("InputBindingProbe could not write " + OutputPath + ": " + ex);
                // Fall back to the BepInEx log so the data is not lost entirely.
                Plugin.Log.LogInfo(contents);
            }
        }
    }
}
