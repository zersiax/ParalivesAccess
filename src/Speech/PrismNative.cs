using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ParalivesAccess.Speech
{
    /// <summary>
    /// Raw P/Invoke declarations for the Prism speech library (prism.dll).
    /// </summary>
    /// <remarks>
    /// Every signature here is transcribed from <c>include/prism.h</c> of the
    /// prism-windows-x64 v0.17.3 release. Do NOT change anything in this file without
    /// re-reading that header — a wrong signature corrupts memory instead of failing loudly,
    /// and the user cannot see that happen.
    ///
    /// Three details from the header that are easy to get wrong and were verified explicitly:
    ///
    /// 1. CALLING CONVENTION IS CDECL. The header does `#define PRISM_CALL __cdecl` on _WIN32.
    ///    .NET's DllImport default is StdCall; using it would misalign the stack.
    ///
    /// 2. STRINGS ARE UTF-8, not ANSI and not UTF-16. Parameters are `const char *`, and the
    ///    error enum contains PRISM_ERROR_INVALID_UTF8. CharSet.Ansi would silently mangle any
    ///    non-ASCII text because it uses the system codepage. All strings therefore cross the
    ///    boundary as explicit null-terminated UTF-8 byte arrays.
    ///    .NET Framework 4.7.2 has no Marshal.PtrToStringUTF8, so <see cref="Utf8PtrToString"/>
    ///    does the reverse conversion by hand.
    ///
    /// 3. C `bool` (from stdbool.h) IS ONE BYTE. .NET marshals bool as a 4-byte Win32 BOOL by
    ///    default, so every bool is explicitly MarshalAs(UnmanagedType.U1), and the bool field
    ///    inside PrismConfig is declared as a raw byte.
    ///
    /// Export names were confirmed present in prism.dll via LoadLibrary/GetProcAddress before
    /// this file was written. x64 cdecl exports are undecorated, so the names match the header.
    /// </remarks>
    internal static class PrismNative
    {
        /// <summary>
        /// Native library name. Resolved by Mono from the game root (next to Paralives.exe).
        /// </summary>
        private const string Dll = "prism";

        /// <summary>Value of PRISM_CONFIG_VERSION in prism.h at the version we target.</summary>
        internal const byte ConfigVersion = 3;

        #region Enums

        /// <summary>Mirrors <c>PrismError</c>. PRISM_OK is guaranteed 0 by a static assert in the header.</summary>
        internal enum PrismError
        {
            Ok = 0,
            NotInitialized,
            InvalidParam,
            NotImplemented,
            NoVoices,
            VoiceNotFound,
            SpeakFailure,
            MemoryFailure,
            RangeOutOfBounds,
            Internal,
            NotSpeaking,
            NotPaused,
            AlreadyPaused,
            InvalidUtf8,
            InvalidOperation,
            AlreadyInitialized,
            BackendNotAvailable,
            Unknown,
            InvalidAudioFormat,
            InternalBackendLimitExceeded,
            BackendEnteredUndefinedState,
            LibraryLoadFailed,
            LibraryInvalid,
            IncompatibleAbi,
            Count
        }

        /// <summary>Mirrors <c>PrismLogLevel</c>.</summary>
        internal enum PrismLogLevel
        {
            Trace = 0,
            Debug,
            Info,
            Warn,
            Error,
            None
        }

        /// <summary>
        /// Backend capability bits from <c>PrismBackendFeature</c>. Only the ones we use.
        /// </summary>
        [Flags]
        internal enum PrismBackendFeature : ulong
        {
            None = 0,
            IsSupportedAtRuntime = 1UL << 0,
            SupportsSpeak = 1UL << 2,
            SupportsBraille = 1UL << 4,
            SupportsOutput = 1UL << 5,
            SupportsIsSpeaking = 1UL << 6,
            SupportsStop = 1UL << 7
        }

        #endregion

        #region Structs

        /// <summary>
        /// Mirrors <c>PrismConfig</c> from prism.h.
        /// </summary>
        /// <remarks>
        /// Field order and types must match the header exactly. On x64 the compiler pads the
        /// leading uint8_t out to the 8-byte pointer alignment; LayoutKind.Sequential with default
        /// packing reproduces that. <c>availability_auto_power_manage</c> is a C bool (1 byte) and
        /// is deliberately declared as a byte rather than a marshalled bool.
        /// </remarks>
        [StructLayout(LayoutKind.Sequential)]
        internal struct PrismConfig
        {
            /// <summary>uint8_t version — must be <see cref="ConfigVersion"/>.</summary>
            public byte Version;

            /// <summary>PrismRegistry* — null means use the built-in registry.</summary>
            public IntPtr Registry;

            /// <summary>PrismAvailabilityCallback — null unless we subscribe to availability changes.</summary>
            public IntPtr AvailabilityCallback;

            /// <summary>void* userdata passed back to the availability callback.</summary>
            public IntPtr AvailabilityUserdata;

            /// <summary>uint32_t availability_poll_interval_ms.</summary>
            public uint AvailabilityPollIntervalMs;

            /// <summary>uint32_t availability_debounce_samples.</summary>
            public uint AvailabilityDebounceSamples;

            /// <summary>uint32_t availability_backoff_max_ms.</summary>
            public uint AvailabilityBackoffMaxMs;

            /// <summary>C bool availability_auto_power_manage (1 byte).</summary>
            public byte AvailabilityAutoPowerManage;
        }

        #endregion

        #region Lifecycle

        /// <summary>Returns a PrismConfig prefilled with library defaults.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern PrismConfig prism_config_init();

        /// <summary>Creates a Prism context. Returns null on failure.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr prism_init(ref PrismConfig cfg);

        /// <summary>Destroys a Prism context.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void prism_shutdown(IntPtr ctx);

        #endregion

        #region Registry

        /// <summary>
        /// Allocates the best available backend. Returns null when no backend is available.
        /// </summary>
        /// <remarks>
        /// We use <c>create_best</c> rather than <c>acquire_best</c> because the header marks the
        /// create variants PRISM_MALLOC, making ownership unambiguous: we allocated it, so we free
        /// it with <see cref="prism_backend_free"/>. The acquire variants carry no such annotation
        /// and their ownership semantics are not documented — so we avoid guessing.
        /// </remarks>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr prism_registry_create_best(IntPtr ctx);

        /// <summary>Number of registered backends.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern UIntPtr prism_registry_count(IntPtr ctx);

        /// <summary>Backend id at an index.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern ulong prism_registry_id_at(IntPtr ctx, UIntPtr index);

        /// <summary>Backend name for an id. Returns a UTF-8 string owned by Prism.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr prism_registry_name(IntPtr ctx, ulong id);

        #endregion

        #region Backend

        /// <summary>Frees a backend allocated by a create* function.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void prism_backend_free(IntPtr backend);

        /// <summary>Backend name. Returns a UTF-8 string owned by Prism — do not free.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr prism_backend_name(IntPtr backend);

        /// <summary>Capability bitmask; see <see cref="PrismBackendFeature"/>.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern ulong prism_backend_get_features(IntPtr backend);

        /// <summary>Initialises the backend. Must succeed before speaking.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern PrismError prism_backend_initialize(IntPtr backend);

        /// <summary>Speaks text. <paramref name="text"/> must be null-terminated UTF-8.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern PrismError prism_backend_speak(
            IntPtr backend,
            byte[] text,
            [MarshalAs(UnmanagedType.U1)] bool interrupt);

        /// <summary>
        /// Speech and braille together, where the backend supports it.
        /// <paramref name="text"/> must be null-terminated UTF-8.
        /// </summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern PrismError prism_backend_output(
            IntPtr backend,
            byte[] text,
            [MarshalAs(UnmanagedType.U1)] bool interrupt);

        /// <summary>Braille only. <paramref name="text"/> must be null-terminated UTF-8.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern PrismError prism_backend_braille(IntPtr backend, byte[] text);

        /// <summary>Stops current speech.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern PrismError prism_backend_stop(IntPtr backend);

        /// <summary>Queries whether the backend is currently speaking.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern PrismError prism_backend_is_speaking(
            IntPtr backend,
            [MarshalAs(UnmanagedType.U1)] out bool speaking);

        #endregion

        #region Diagnostics

        /// <summary>Human-readable name for an error code. UTF-8, owned by Prism.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr prism_error_string(PrismError error);

        /// <summary>Sets the library log level, returning the previous one.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern PrismLogLevel prism_set_log_level(PrismLogLevel level);

        #endregion

        #region Marshalling helpers

        /// <summary>
        /// Converts a managed string to a null-terminated UTF-8 byte array for native calls.
        /// </summary>
        /// <param name="value">Text to convert. Null is treated as empty.</param>
        /// <returns>UTF-8 bytes with an explicit trailing null.</returns>
        internal static byte[] ToUtf8(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return new byte[] { 0 };
            }

            int byteCount = Encoding.UTF8.GetByteCount(value);
            byte[] buffer = new byte[byteCount + 1];
            Encoding.UTF8.GetBytes(value, 0, value.Length, buffer, 0);
            buffer[byteCount] = 0;
            return buffer;
        }

        /// <summary>
        /// Reads a null-terminated UTF-8 string from native memory.
        /// </summary>
        /// <remarks>
        /// Hand-rolled because .NET Framework 4.7.2 lacks Marshal.PtrToStringUTF8, and
        /// Marshal.PtrToStringAnsi would decode using the system ANSI codepage, corrupting any
        /// non-ASCII text.
        /// </remarks>
        /// <param name="ptr">Pointer to UTF-8 bytes owned by native code.</param>
        /// <returns>The decoded string, or null when <paramref name="ptr"/> is null.</returns>
        internal static string Utf8PtrToString(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero)
            {
                return null;
            }

            int length = 0;
            while (Marshal.ReadByte(ptr, length) != 0)
            {
                length++;
            }

            if (length == 0)
            {
                return string.Empty;
            }

            byte[] buffer = new byte[length];
            Marshal.Copy(ptr, buffer, 0, length);
            return Encoding.UTF8.GetString(buffer);
        }

        /// <summary>Returns Prism's description of an error code, falling back to the enum name.</summary>
        internal static string DescribeError(PrismError error)
        {
            try
            {
                string described = Utf8PtrToString(prism_error_string(error));
                return string.IsNullOrEmpty(described) ? error.ToString() : described;
            }
            catch (Exception)
            {
                return error.ToString();
            }
        }

        #endregion
    }
}
