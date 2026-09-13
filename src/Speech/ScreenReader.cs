using System;
using System.Text;
using System.Text.RegularExpressions;
using ParalivesAccess.Probes;

namespace ParalivesAccess.Speech
{
    /// <summary>
    /// Managed front end for screen reader output via Prism.
    /// </summary>
    /// <remarks>
    /// This is the only type the rest of the mod should use to speak. It owns the native
    /// context and backend handles and guarantees that a failure to speak is logged rather
    /// than swallowed — silence is indistinguishable from success for a blind user, so a
    /// dropped announcement must never pass unnoticed.
    ///
    /// All native interaction is wrapped in try/catch, which is justified here per the project's
    /// error-handling rule: these are external calls into a native library.
    /// </remarks>
    public static class ScreenReader
    {
        private static IntPtr _context = IntPtr.Zero;
        private static IntPtr _backend = IntPtr.Zero;
        private static bool _useOutput;
        private static string _lastSpoken;

        /// <summary>True when Prism is initialised and speech is available.</summary>
        public static bool IsAvailable => _backend != IntPtr.Zero;

        /// <summary>Name of the active Prism backend, or null when unavailable.</summary>
        public static string BackendName { get; private set; }

        /// <summary>
        /// Strips TextMeshPro rich-text tags, which the game embeds in UI strings.
        /// </summary>
        /// <remarks>
        /// Required because <c>TranslatedText.Refresh</c> rewrites '$' into
        /// &lt;sprite name="Money"&gt; and wraps &lt;link=...&gt; in colour and underline tags.
        /// Speaking those verbatim would read markup aloud.
        /// </remarks>
        private static readonly Regex RichTextTags = new Regex("<[^>]+>", RegexOptions.Compiled);

        /// <summary>
        /// Initialises Prism and acquires the best available backend.
        /// </summary>
        /// <returns>True when speech is available afterwards.</returns>
        public static bool Initialize()
        {
            if (IsAvailable)
            {
                Plugin.Log.LogWarning("ScreenReader.Initialize called twice; ignoring.");
                return true;
            }

            try
            {
                PrismNative.PrismConfig config = PrismNative.prism_config_init();

                if (config.Version != PrismNative.ConfigVersion)
                {
                    // Not fatal, but a version mismatch means prism.dll is not the build this
                    // wrapper was written against. Say so loudly rather than hoping for the best.
                    Plugin.Log.LogWarning(
                        $"prism.dll reports PrismConfig version {config.Version}, but this wrapper "
                      + $"was written against version {PrismNative.ConfigVersion}. Re-check prism.h.");
                }

                _context = PrismNative.prism_init(ref config);
                if (_context == IntPtr.Zero)
                {
                    Plugin.Log.LogError("prism_init returned null. Speech is unavailable.");
                    return false;
                }

                _backend = PrismNative.prism_registry_create_best(_context);
                if (_backend == IntPtr.Zero)
                {
                    Plugin.Log.LogError(
                        "prism_registry_create_best returned null - no screen reader or TTS backend "
                      + "is available. Is a screen reader running?");
                    LogAvailableBackends();
                    Shutdown();
                    return false;
                }

                PrismNative.PrismError initResult = PrismNative.prism_backend_initialize(_backend);

                // ALREADY_INITIALIZED is a success state, not a failure: prism_registry_create_best
                // returns a backend that is already initialised, and the header defines this code
                // specifically to report a harmless no-op. Treating it as fatal disabled speech
                // entirely on the first real run.
                if (initResult == PrismNative.PrismError.AlreadyInitialized)
                {
                    Plugin.Log.LogInfo("Backend was already initialised by Prism; continuing.");
                }
                else if (initResult != PrismNative.PrismError.Ok)
                {
                    Plugin.Log.LogError(
                        "prism_backend_initialize failed: " + PrismNative.DescribeError(initResult));
                    Shutdown();
                    return false;
                }

                BackendName = PrismNative.Utf8PtrToString(PrismNative.prism_backend_name(_backend));

                ulong features = PrismNative.prism_backend_get_features(_backend);
                bool supportsOutput =
                    (features & (ulong)PrismNative.PrismBackendFeature.SupportsOutput) != 0;
                bool supportsSpeak =
                    (features & (ulong)PrismNative.PrismBackendFeature.SupportsSpeak) != 0;

                // Prefer 'output': it drives speech AND braille where the backend supports both,
                // so braille users are not left out. Fall back to plain speech otherwise.
                _useOutput = supportsOutput;

                if (!supportsOutput && !supportsSpeak)
                {
                    Plugin.Log.LogError(
                        $"Backend '{BackendName}' supports neither output nor speak (features=0x{features:X}). "
                      + "Speech is unavailable.");
                    Shutdown();
                    return false;
                }

                Plugin.Log.LogInfo(
                    $"Prism ready. Backend='{BackendName}', features=0x{features:X}, "
                  + $"using {(_useOutput ? "output (speech+braille)" : "speak (speech only)")}.");

                return true;
            }
            catch (DllNotFoundException ex)
            {
                Plugin.Log.LogError(
                    "prism.dll was not found. It must sit next to Paralives.exe in the game "
                  + "directory. Details: " + ex.Message);
                return false;
            }
            catch (EntryPointNotFoundException ex)
            {
                Plugin.Log.LogError(
                    "A Prism entry point is missing - prism.dll is likely a different version than "
                  + "this wrapper targets (v0.17.3). Details: " + ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Prism initialisation threw: " + ex);
                return false;
            }
        }

        /// <summary>
        /// Speaks text, interrupting anything currently being spoken.
        /// </summary>
        /// <param name="text">Text to speak. Rich-text tags are stripped automatically.</param>
        /// <returns>True when the text was handed to Prism successfully.</returns>
        public static bool Speak(string text)
        {
            return Speak(text, interrupt: true);
        }

        /// <summary>
        /// Speaks text.
        /// </summary>
        /// <param name="text">Text to speak. Rich-text tags are stripped automatically.</param>
        /// <param name="interrupt">True to cut off current speech, false to queue after it.</param>
        /// <returns>True when the text was handed to Prism successfully.</returns>
        public static bool Speak(string text, bool interrupt)
        {
            return Speak(text, interrupt, null);
        }

        /// <summary>Names an on-demand report in the optional delivery audit.</summary>
        internal static bool Speak(string text, bool interrupt, string diagnosticSource)
        {
            if (!IsAvailable)
            {
                Plugin.Log.LogWarning($"Speech unavailable, dropped announcement: \"{text}\"");
                return false;
            }

            string clean = Clean(text);
            if (string.IsNullOrEmpty(clean))
            {
                return false;
            }

            int auditSequence = 0;
            try
            {
                byte[] utf8 = PrismNative.ToUtf8(clean);
                bool requestedInterrupt = interrupt;
                interrupt = AnnouncementCoordinator.ResolveInterrupt(interrupt);
                auditSequence = ContextDeliveryProbe.BeginOutput(diagnosticSource, clean,
                    requestedInterrupt, interrupt, BackendName, _useOutput);

                PrismNative.PrismError result = _useOutput
                    ? PrismNative.prism_backend_output(_backend, utf8, interrupt)
                    : PrismNative.prism_backend_speak(_backend, utf8, interrupt);
                ContextDeliveryProbe.EndOutput(auditSequence, result.ToString());

                if (result != PrismNative.PrismError.Ok)
                {
                    Plugin.Log.LogError(
                        $"Failed to speak \"{clean}\": {PrismNative.DescribeError(result)}");
                    return false;
                }

                if (interrupt)
                {
                    AnnouncementCoordinator.NoteForegroundSpeech();
                }
                _lastSpoken = clean;
                return true;
            }
            catch (Exception ex)
            {
                ContextDeliveryProbe.EndOutput(auditSequence, "exception:" + ex.GetType().Name);
                Plugin.Log.LogError($"Speaking \"{clean}\" threw: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Speaks text only when it differs from the previous announcement.
        /// </summary>
        /// <remarks>
        /// Guards against the game re-reporting the same focus or hover target on consecutive
        /// frames, which would otherwise stutter the screen reader.
        /// </remarks>
        /// <param name="text">Text to speak.</param>
        /// <returns>True when something was actually spoken.</returns>
        public static bool SpeakIfChanged(string text)
        {
            string clean = Clean(text);
            if (string.IsNullOrEmpty(clean) || clean == _lastSpoken)
            {
                return false;
            }

            return Speak(clean);
        }

        /// <summary>Stops any speech currently in progress.</summary>
        public static void Stop()
        {
            ContextDeliveryProbe.NoteStop();
            if (!IsAvailable)
            {
                return;
            }

            try
            {
                PrismNative.PrismError result = PrismNative.prism_backend_stop(_backend);
                if (result != PrismNative.PrismError.Ok
                    && result != PrismNative.PrismError.NotSpeaking)
                {
                    Plugin.Log.LogWarning("prism_backend_stop: " + PrismNative.DescribeError(result));
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("prism_backend_stop threw: " + ex);
            }
        }

        /// <summary>Clears the duplicate-suppression memory used by <see cref="SpeakIfChanged"/>.</summary>
        public static void ResetLastSpoken()
        {
            _lastSpoken = null;
        }

        /// <summary>
        /// Returns the exact normalized text that speech and braille receive.
        /// </summary>
        /// <remarks>
        /// Typed game formatters such as <c>MeasurementUnitService</c> return TextMeshPro markup.
        /// Callers that compose those values into diagnostic descriptions should normalize the
        /// fragment before logging it, so instrumentation matches the actual accessible output.
        /// </remarks>
        internal static string NormalizeForOutput(string text)
        {
            return Clean(text);
        }

        /// <summary>Releases the Prism backend and context. Safe to call more than once.</summary>
        public static void Shutdown()
        {
            try
            {
                if (_backend != IntPtr.Zero)
                {
                    PrismNative.prism_backend_free(_backend);
                    _backend = IntPtr.Zero;
                }

                if (_context != IntPtr.Zero)
                {
                    PrismNative.prism_shutdown(_context);
                    _context = IntPtr.Zero;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Prism shutdown threw: " + ex);
            }
            finally
            {
                BackendName = null;
                _lastSpoken = null;
            }
        }

        /// <summary>Normalises text for speech: strips rich-text markup and collapses whitespace.</summary>
        private static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            string stripped = RichTextTags.Replace(text, " ");
            stripped = stripped.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');

            // Zero-width characters are invisible on screen but are NOT whitespace to char.IsWhiteSpace,
            // so they survive the collapse below and reach the screen reader. TMP uses U+200B as
            // filler in empty input fields - TEST 34 captured 'Last Name, ' with a trailing one.
            // Escapes, not literals: an invisible character pasted into source is unreviewable.
            stripped = stripped
                .Replace("\u200B", string.Empty)   // zero-width space
                .Replace("\u200C", string.Empty)   // zero-width non-joiner
                .Replace("\u200D", string.Empty)   // zero-width joiner
                .Replace("\uFEFF", string.Empty);  // zero-width no-break space / BOM

            // Collapse runs of whitespace left behind by tag removal.
            StringBuilder builder = new StringBuilder(stripped.Length);
            bool lastWasSpace = false;
            foreach (char c in stripped)
            {
                bool isSpace = char.IsWhiteSpace(c);
                if (isSpace && lastWasSpace)
                {
                    continue;
                }

                builder.Append(isSpace ? ' ' : c);
                lastWasSpace = isSpace;
            }

            return builder.ToString().Trim();
        }

        /// <summary>Logs the backends Prism knows about, to help diagnose a failed acquisition.</summary>
        private static void LogAvailableBackends()
        {
            try
            {
                if (_context == IntPtr.Zero)
                {
                    return;
                }

                ulong count = (ulong)PrismNative.prism_registry_count(_context);
                Plugin.Log.LogInfo($"Prism registry contains {count} backend(s):");

                for (ulong i = 0; i < count; i++)
                {
                    ulong id = PrismNative.prism_registry_id_at(_context, (UIntPtr)i);
                    string name = PrismNative.Utf8PtrToString(
                        PrismNative.prism_registry_name(_context, id));
                    Plugin.Log.LogInfo($"  0x{id:X16}  {name}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Could not enumerate Prism backends: " + ex.Message);
            }
        }
    }
}
