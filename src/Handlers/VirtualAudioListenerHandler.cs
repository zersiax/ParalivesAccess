using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ParalivesAccess.Speech;
using Setting;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ParalivesAccess.Handlers
{
    /// <summary>
    /// Lets live-mode audio use a close virtual camera without changing the visual camera.
    /// </summary>
    internal static class VirtualAudioListenerHandler
    {
        private const string ProxyName = "ParalivesAccess Virtual Audio Listener";
        private const string WaitForDistanceMethodName =
            "<Update>g__GoThroughBranchesRecursively|4_0";

        private static readonly MethodInfo PlayerManagerInstanceGetter =
            AccessTools.PropertyGetter(typeof(PlayerManager), nameof(PlayerManager.Instance));
        private static readonly FieldInfo HybridPlayer1Field =
            AccessTools.Field(typeof(PlayerManager), nameof(PlayerManager.HybridPlayer1));
        private static readonly FieldInfo HybridCameraField =
            AccessTools.Field(typeof(HybridPlayer), nameof(HybridPlayer.HybridCamera));
        private static readonly FieldInfo CameraField =
            AccessTools.Field(typeof(HybridCamera), nameof(HybridCamera.Camera));
        private static readonly MethodInfo ComponentTransformGetter =
            AccessTools.PropertyGetter(typeof(Component), nameof(Component.transform));
        private static readonly MethodInfo TransformPositionGetter =
            AccessTools.PropertyGetter(typeof(Transform), nameof(Transform.position));

        private static readonly List<NativeListenerState> NativeListeners =
            new List<NativeListenerState>();
        private static readonly List<AudioListener> ListenerScratch =
            new List<AudioListener>();

        private static GameObject _proxyObject;
        private static AudioListener _proxyListener;
        private static HybridPlayer _listenerOwner;
        private static bool _patchesReady;
        private static bool _runtimeActive;
        private static bool _handlerEnabled = true;
        private static bool _requestedEnabled;
        private static bool _failureAnnounced;
        private static string _lastReadinessFailure;
        private static int _playAtReplacementCount;
        private static int _waitReplacementCount;
        private static bool _loggedPlayAtRedirect;
        private static bool _loggedWaitRedirect;
        private static ulong _lastAnchorGuid;
        private static string _lastAnchorKind;
        private static int _lastInsideMixFrame = -1;
        private static int _listenerAuditFramesRemaining;
        private static string _listenerAuditReason;

        private static Vector3 _virtualPosition;
        private static Quaternion _virtualRotation;
        private static float _virtualZoomDistance;
        private static float _virtualZoomRatio;
        private static float _virtualInsideMix;
        private static float _virtualPositionX;
        private static float _virtualPositionZ;

        /// <summary>Configurable physical key used to toggle close audio perspective.</summary>
        internal static Key ToggleKey { get; set; } = Key.F8;

        /// <summary>Requested virtual distance from the followed Para or camera focus.</summary>
        internal static float ReferenceDistance { get; set; } = 3f;

        /// <summary>Initial and current session state of the close audio perspective.</summary>
        internal static bool RequestedEnabled
        {
            get => _requestedEnabled;
            set => _requestedEnabled = value;
        }

        /// <summary>Disables the feature when its configured key conflicts with another mod key.</summary>
        internal static bool HandlerEnabled
        {
            get => _handlerEnabled;
            set
            {
                _handlerEnabled = value;
                if (!value)
                {
                    _requestedEnabled = false;
                    Deactivate("handler-disabled");
                }
            }
        }

        /// <summary>
        /// Installs all three inseparable audio boundaries: the listener-independent PlayAt gate,
        /// the deferred WaitToBeClose gate, and temporary authored camera-variable overrides.
        /// </summary>
        internal static bool ApplyPatches(Harmony harmony)
        {
            _patchesReady = false;
            _playAtReplacementCount = 0;
            _waitReplacementCount = 0;

            try
            {
                MethodInfo playAt = AccessTools.Method(
                    typeof(AudioManager),
                    nameof(AudioManager.PlayAt),
                    new[]
                    {
                        typeof(AudioUnit), typeof(Vector3), typeof(AudioUserActionData),
                        typeof(ItemObjectRoot), typeof(ulong)
                    });
                MethodInfo updateActiveAudio = AccessTools.Method(
                    typeof(UpdateActiveAudioUnits), nameof(UpdateActiveAudioUnits.Update));
                MethodInfo waitForDistance = typeof(UpdateActiveAudioUnits).GetMethod(
                    WaitForDistanceMethodName,
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo statePrefix = AccessTools.Method(
                    typeof(VirtualAudioListenerHandler), nameof(AudioCameraStatePrefix));
                MethodInfo stateFinalizer = AccessTools.Method(
                    typeof(VirtualAudioListenerHandler), nameof(AudioCameraStateFinalizer));
                MethodInfo playAtTranspiler = AccessTools.Method(
                    typeof(VirtualAudioListenerHandler), nameof(PlayAtDistanceTranspiler));
                MethodInfo waitTranspiler = AccessTools.Method(
                    typeof(VirtualAudioListenerHandler), nameof(WaitDistanceTranspiler));

                if (playAt == null || updateActiveAudio == null || waitForDistance == null
                    || statePrefix == null || stateFinalizer == null
                    || playAtTranspiler == null || waitTranspiler == null)
                {
                    Plugin.Log.LogError(
                        "Close audio patch targets are unavailable "
                      + $"(PlayAt={playAt != null}, active update={updateActiveAudio != null}, "
                      + $"WaitToBeClose={waitForDistance != null}, state prefix={statePrefix != null}, "
                      + $"state finalizer={stateFinalizer != null}, "
                      + $"PlayAt transpiler={playAtTranspiler != null}, "
                      + $"wait transpiler={waitTranspiler != null}).");
                    return false;
                }

                harmony.Patch(
                    playAt,
                    prefix: new HarmonyMethod(statePrefix),
                    transpiler: new HarmonyMethod(playAtTranspiler),
                    finalizer: new HarmonyMethod(stateFinalizer));
                harmony.Patch(
                    updateActiveAudio,
                    prefix: new HarmonyMethod(statePrefix),
                    finalizer: new HarmonyMethod(stateFinalizer));
                harmony.Patch(
                    waitForDistance,
                    transpiler: new HarmonyMethod(waitTranspiler));

                Patches playAtPatches = Harmony.GetPatchInfo(playAt);
                Patches updatePatches = Harmony.GetPatchInfo(updateActiveAudio);
                Patches waitPatches = Harmony.GetPatchInfo(waitForDistance);
                bool playAtPatched = HasPatch(playAtPatches?.Prefixes, statePrefix)
                    && HasPatch(playAtPatches?.Transpilers, playAtTranspiler)
                    && HasPatch(playAtPatches?.Finalizers, stateFinalizer);
                bool updatePatched = HasPatch(updatePatches?.Prefixes, statePrefix)
                    && HasPatch(updatePatches?.Finalizers, stateFinalizer);
                bool waitPatched = HasPatch(waitPatches?.Transpilers, waitTranspiler);
                _patchesReady = playAtPatched && updatePatched && waitPatched
                    && _playAtReplacementCount == 1 && _waitReplacementCount == 1;

                Plugin.Log.LogInfo(
                    "Close audio patches: "
                  + $"PlayAt={playAtPatched} ({_playAtReplacementCount} distance replacement), "
                  + $"active state={updatePatched}, "
                  + $"WaitToBeClose={waitPatched} ({_waitReplacementCount} distance replacement), "
                  + $"complete={_patchesReady}.");

                if (!_patchesReady)
                {
                    Plugin.Log.LogError(
                        "Close audio perspective is fail-closed because its complete listener, "
                      + "distance-gate, and authored-mix contract could not be installed.");
                }

                return _patchesReady;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Installing close audio perspective patches threw: " + ex);
                _patchesReady = false;
                return false;
            }
        }

        /// <summary>Handles the toggle and prepares the virtual audio state before game systems.</summary>
        internal static void BeforeGameSystems()
        {
            if (!_handlerEnabled)
            {
                return;
            }

            try
            {
                Keyboard keyboard = Keyboard.current;
                Player player = GetPlayer();
                HybridPlayer hybridPlayer = PlayerManager.Instance?.HybridPlayer1;
                if (keyboard != null && ModInputKey.WasPressed(keyboard, ToggleKey))
                {
                    HandleToggle(player, hybridPlayer);
                }

                MaintainRequestedState(player, hybridPlayer, out _);
            }
            catch (Exception ex)
            {
                _requestedEnabled = false;
                Deactivate("handler-exception");
                _handlerEnabled = false;
                Plugin.Log.LogError("Close audio perspective disabled after an exception: " + ex);
                AnnounceFailureOnce(Loc.Get("virtual_audio_failed"));
            }
        }

        /// <summary>
        /// Refreshes the actual listener pose after character and camera systems have moved. The
        /// authored audio variables remain native outside their narrowly patched consumers.
        /// </summary>
        internal static void AfterGameSystems()
        {
            if (!_handlerEnabled)
            {
                return;
            }

            // A user-off handoff happens in the prefix. Audit after the game's complete update so
            // the next test can distinguish "our method ran" from "Unity really ended the frame
            // with exactly one native listener and no live proxy". TEST 75 only proved the former.
            if (!_runtimeActive)
            {
                RunPendingListenerAudit();
                return;
            }

            try
            {
                Player player = GetPlayer();
                HybridPlayer hybridPlayer = PlayerManager.Instance?.HybridPlayer1;
                VirtualPose pose;
                string reason;
                if (!TryBuildPose(player, hybridPlayer, out pose, out reason))
                {
                    Deactivate("post-system-pose-unavailable");
                    LogReadinessFailure(reason);
                    return;
                }

                if (!NativeListenerSetMatches(hybridPlayer))
                {
                    Deactivate("post-system-native-listener-set-changed");
                    if (!TryActivate(hybridPlayer, player, pose, out reason))
                    {
                        LogReadinessFailure(reason);
                        return;
                    }
                }

                ApplyPose(player, pose);
                SyncListenerAndEffects();
            }
            catch (Exception ex)
            {
                _requestedEnabled = false;
                Deactivate("post-system-exception");
                _handlerEnabled = false;
                Plugin.Log.LogError(
                    "Close audio perspective post-system update disabled after an exception: " + ex);
                AnnounceFailureOnce(Loc.Get("virtual_audio_failed"));
            }
        }

        private static void HandleToggle(Player player, HybridPlayer hybridPlayer)
        {
            string conflict;
            if (!ModInputKey.IsAvailable(hybridPlayer, ToggleKey, out conflict))
            {
                ScreenReader.Speak(conflict);
                return;
            }

            if (_requestedEnabled)
            {
                _requestedEnabled = false;
                Deactivate("user-toggle-off");
                ScreenReader.Speak(Loc.Get("virtual_audio_off"));
                return;
            }

            if (!_patchesReady)
            {
                ScreenReader.Speak(Loc.Get("virtual_audio_patch_unavailable"));
                return;
            }

            _requestedEnabled = true;
            string reason;
            if (!MaintainRequestedState(player, hybridPlayer, out reason))
            {
                _requestedEnabled = false;
                Deactivate("toggle-on-failed");
                Plugin.Log.LogWarning(
                    "Close audio perspective could not start: " + (reason ?? "unknown reason"));
                ScreenReader.Speak(IsEligibleMode(player)
                    ? Loc.Get("virtual_audio_not_ready")
                    : Loc.Get("virtual_audio_live_mode_only"));
                return;
            }

            _failureAnnounced = false;
            ScreenReader.Speak(Loc.Get("virtual_audio_on"));
        }

        private static bool MaintainRequestedState(
            Player player, HybridPlayer hybridPlayer, out string reason)
        {
            reason = null;
            if (!_requestedEnabled || !_patchesReady)
            {
                Deactivate("not-requested-or-patches-incomplete");
                return false;
            }

            VirtualPose pose;
            if (!TryBuildPose(player, hybridPlayer, out pose, out reason))
            {
                Deactivate("live-mode-context-unavailable");
                LogReadinessFailure(reason);
                return false;
            }

            if (_runtimeActive && !NativeListenerSetMatches(hybridPlayer))
            {
                Deactivate("native-listener-set-changed");
            }

            if (!_runtimeActive && !TryActivate(hybridPlayer, player, pose, out reason))
            {
                LogReadinessFailure(reason);
                return false;
            }

            ApplyPose(player, pose);
            _lastReadinessFailure = null;
            return true;
        }

        private static bool TryBuildPose(
            Player player, HybridPlayer hybridPlayer, out VirtualPose pose, out string reason)
        {
            pose = default(VirtualPose);
            reason = null;

            if (GameLoadingManager.State != State.Game || SavedGameManager.Instance == null
                || !SavedGameManager.Instance.IsGameLoaded || player == null
                || player.State != GameStates.LiveMode || !player.IsUsingFreeCamera)
            {
                reason = "not in a loaded live-mode free camera";
                return false;
            }

            StoryboardManager storyboardManager = StoryboardManager.Instance;
            if (storyboardManager != null && storyboardManager.IsCurrentlyPlayingStoryboards)
            {
                reason = "a storyboard owns the camera";
                return false;
            }

            HybridCamera hybridCamera = hybridPlayer?.HybridCamera;
            HybridCameraFree freeCamera = hybridCamera?.FreeCamera;
            Camera camera = hybridCamera?.Camera;
            global::Setting.FreeCamera settings = Settings.Get<global::Setting.FreeCamera>();
            if (camera == null || freeCamera?.LookTarget == null || settings == null
                || !IsFinite(settings.MinZoomDistance) || !IsFinite(settings.MaxZoomDistance)
                || settings.MaxZoomDistance <= settings.MinZoomDistance
                || !IsFinite(ReferenceDistance))
            {
                reason = "camera or zoom settings are not ready";
                return false;
            }

            Vector3 anchor = freeCamera.LookTarget.position;
            string anchorKind = "camera-focus";
            ulong anchorGuid = 0UL;
            bool anchorInside = player.CameraLookTargetRaycastedPlatform != null;

            ulong followedGuid = player.CameraCurrentCharacterFollowTarget;
            if (followedGuid != 0UL)
            {
                CharacterManager characterManager = CharacterManager.Instance;
                AssetCharacter character = characterManager?.GetCharacterByGUID(followedGuid);
                CharacterVisual visual = characterManager?.GetLoadedCharacterVisual(followedGuid);
                if (character?.Data != null
                    && WorldTargeting.IsActiveCharacterVisual(followedGuid, visual))
                {
                    anchor = WorldTargeting.GetCharacterPosition(character)
                        + Vector3.up * settings.LookTargetHeightInGame;
                    anchorKind = "followed-para";
                    anchorGuid = followedGuid;
                    anchorInside = character.Data.OnZoneWallPlatformObject != null;
                }
            }

            Vector3 cameraPosition = camera.transform.position;
            if (!IsFinite(anchor) || !IsFinite(cameraPosition))
            {
                reason = "camera focus has no finite world position";
                return false;
            }

            Vector3 fromAnchorToCamera = cameraPosition - anchor;
            float actualDistance = fromAnchorToCamera.magnitude;
            if (!IsFinite(actualDistance))
            {
                reason = "camera distance is not finite";
                return false;
            }

            float requestedDistance = Mathf.Clamp(
                ReferenceDistance, settings.MinZoomDistance, settings.MaxZoomDistance);
            float effectiveDistance = Mathf.Min(requestedDistance, actualDistance);
            Vector3 virtualPosition = actualDistance > 0.001f
                ? anchor + fromAnchorToCamera / actualDistance * effectiveDistance
                : cameraPosition;
            float audioDistance = Mathf.Clamp(
                effectiveDistance, settings.MinZoomDistance, settings.MaxZoomDistance);

            pose = new VirtualPose
            {
                Position = virtualPosition,
                Rotation = camera.transform.rotation,
                Anchor = anchor,
                AnchorKind = anchorKind,
                AnchorGuid = anchorGuid,
                ZoomDistance = audioDistance,
                ZoomRatio = Mathf.InverseLerp(
                    settings.MinZoomDistance, settings.MaxZoomDistance, audioDistance),
                IsInsideTarget = anchorInside && audioDistance < 4f
            };
            return true;
        }

        private static bool TryActivate(
            HybridPlayer hybridPlayer,
            Player player,
            VirtualPose pose,
            out string reason)
        {
            reason = null;
            if (hybridPlayer == null || hybridPlayer.gameObject == null)
            {
                reason = "local HybridPlayer is unavailable";
                return false;
            }

            ListenerScratch.Clear();
            hybridPlayer.gameObject.GetComponentsInChildren(true, ListenerScratch);
            AudioListener primary = null;
            int activeNativeCount = 0;
            NativeListeners.Clear();
            foreach (AudioListener listener in ListenerScratch)
            {
                if (listener == null || listener == _proxyListener)
                {
                    continue;
                }

                NativeListeners.Add(new NativeListenerState(listener, listener.enabled));
                if (listener.isActiveAndEnabled)
                {
                    activeNativeCount++;
                }
                if (primary == null || (!primary.isActiveAndEnabled && listener.isActiveAndEnabled))
                {
                    primary = listener;
                }
            }

            if (primary == null || activeNativeCount == 0)
            {
                NativeListeners.Clear();
                reason = "the game's native listener is not active";
                return false;
            }
            if (activeNativeCount != 1)
            {
                NativeListeners.Clear();
                reason = $"the game exposed {activeNativeCount} active native listeners";
                return false;
            }

            string unsupportedEffect;
            if (HasUnsupportedListenerEffect(primary, out unsupportedEffect))
            {
                NativeListeners.Clear();
                reason = unsupportedEffect;
                return false;
            }

            EnsureProxy();
            if (_proxyListener == null)
            {
                NativeListeners.Clear();
                reason = "the virtual listener could not be created";
                return false;
            }

            _listenerOwner = hybridPlayer;
            _virtualInsideMix = player.AudioSystemCameraIsInsideHouse;
            _lastInsideMixFrame = -1;
            ApplyPose(player, pose);
            CopyListenerAndEffects(primary);

            foreach (NativeListenerState state in NativeListeners)
            {
                if (state.Listener != null)
                {
                    state.Listener.enabled = false;
                }
            }

            _proxyObject.SetActive(true);
            _proxyListener.enabled = true;
            _runtimeActive = true;
            _listenerAuditFramesRemaining = 0;
            _listenerAuditReason = null;
            _loggedPlayAtRedirect = false;
            _loggedWaitRedirect = false;
            _lastAnchorGuid = pose.AnchorGuid;
            _lastAnchorKind = pose.AnchorKind;

            Plugin.Log.LogInfo(
                $"[VirtualAudio] activated key={ToggleKey}, "
              + $"requestedDistance={ReferenceDistance:0.###}, "
              + $"effectiveDistance={pose.ZoomDistance:0.###}, "
              + $"anchor={pose.AnchorKind}, character={pose.AnchorGuid}, "
              + $"anchorPosition={FormatVector(pose.Anchor)}, "
              + $"listenerPosition={FormatVector(pose.Position)}, "
              + $"visualCameraPosition={FormatVector(hybridPlayer.HybridCamera.Camera.transform.position)}, "
              + $"nativeListeners={NativeListeners.Count}, "
              + $"nativeListener='{GetTransformPath(primary.transform)}', "
              + $"components='{GetComponentInventory(primary.gameObject)}', "
              + $"proxyComponents='{GetComponentInventory(_proxyObject)}'.");
            LogListenerEnvironment("activated");
            return true;
        }

        private static void EnsureProxy()
        {
            if (_proxyObject != null && _proxyListener != null)
            {
                return;
            }

            _proxyObject = new GameObject(ProxyName);
            // AddComponent<AudioListener> defaults to enabled. Make the new root inactive first so
            // creation itself cannot briefly overlap the still-active native listener.
            _proxyObject.SetActive(false);
            _proxyObject.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(_proxyObject);
            _proxyListener = _proxyObject.AddComponent<AudioListener>();
            _proxyListener.enabled = false;
        }

        private static void ApplyPose(Player player, VirtualPose pose)
        {
            _virtualPosition = pose.Position;
            _virtualRotation = pose.Rotation;
            _virtualZoomDistance = pose.ZoomDistance;
            _virtualZoomRatio = pose.ZoomRatio;
            _virtualPositionX = pose.Anchor.x;
            _virtualPositionZ = pose.Anchor.z;

            if (_lastInsideMixFrame != Time.frameCount)
            {
                _virtualInsideMix = Mathf.Lerp(
                    _virtualInsideMix,
                    pose.IsInsideTarget ? 1f : 0f,
                    Time.unscaledDeltaTime * 2f);
                _lastInsideMixFrame = Time.frameCount;
            }

            if (_proxyObject != null)
            {
                _proxyObject.transform.SetPositionAndRotation(_virtualPosition, _virtualRotation);
            }

            if (_runtimeActive
                && (_lastAnchorGuid != pose.AnchorGuid || _lastAnchorKind != pose.AnchorKind))
            {
                Plugin.Log.LogInfo(
                    $"[VirtualAudio] anchor changed {_lastAnchorKind}/{_lastAnchorGuid} -> "
                  + $"{pose.AnchorKind}/{pose.AnchorGuid}, position={FormatVector(pose.Anchor)}, "
                  + $"effectiveDistance={pose.ZoomDistance:0.###}.");
                _lastAnchorGuid = pose.AnchorGuid;
                _lastAnchorKind = pose.AnchorKind;
            }
        }

        private static void SyncListenerAndEffects()
        {
            if (!_runtimeActive || NativeListeners.Count == 0 || _proxyListener == null)
            {
                return;
            }

            AudioListener primary = null;
            foreach (NativeListenerState state in NativeListeners)
            {
                if (state.Listener != null && (primary == null || state.WasEnabled))
                {
                    primary = state.Listener;
                    if (state.WasEnabled)
                    {
                        break;
                    }
                }
            }

            if (primary != null)
            {
                CopyListenerAndEffects(primary);
            }
        }

        private static void CopyListenerAndEffects(AudioListener source)
        {
            _proxyListener.velocityUpdateMode = source.velocityUpdateMode;

            CopyLowPass(
                source.GetComponent<AudioLowPassFilter>(),
                GetProxyComponentForMirror(source.GetComponent<AudioLowPassFilter>()));
            CopyHighPass(
                source.GetComponent<AudioHighPassFilter>(),
                GetProxyComponentForMirror(source.GetComponent<AudioHighPassFilter>()));
            CopyReverb(
                source.GetComponent<AudioReverbFilter>(),
                GetProxyComponentForMirror(source.GetComponent<AudioReverbFilter>()));
            CopyChorus(
                source.GetComponent<AudioChorusFilter>(),
                GetProxyComponentForMirror(source.GetComponent<AudioChorusFilter>()));
            CopyDistortion(
                source.GetComponent<AudioDistortionFilter>(),
                GetProxyComponentForMirror(source.GetComponent<AudioDistortionFilter>()));
            CopyEcho(
                source.GetComponent<AudioEchoFilter>(),
                GetProxyComponentForMirror(source.GetComponent<AudioEchoFilter>()));
        }

        /// <summary>
        /// Returns a matching proxy effect, creating it only when the native listener actually has
        /// that effect. The previous implementation added all six Unity DSP filters and disabled
        /// the absent ones. That was logically redundant and left unnecessary echo/reverb
        /// components on the persistent proxy during TEST 75's audible delay failure.
        /// </summary>
        private static T GetProxyComponentForMirror<T>(T source) where T : Component
        {
            T component = _proxyObject.GetComponent<T>();
            return component != null || source == null
                ? component
                : _proxyObject.AddComponent<T>();
        }

        private static void CopyLowPass(AudioLowPassFilter source, AudioLowPassFilter target)
        {
            if (source == null)
            {
                if (target != null)
                {
                    target.enabled = false;
                }
                return;
            }
            target.customCutoffCurve = source.customCutoffCurve;
            target.cutoffFrequency = source.cutoffFrequency;
            target.lowpassResonanceQ = source.lowpassResonanceQ;
            target.enabled = source.enabled;
        }

        private static void CopyHighPass(AudioHighPassFilter source, AudioHighPassFilter target)
        {
            if (source == null)
            {
                if (target != null)
                {
                    target.enabled = false;
                }
                return;
            }
            target.cutoffFrequency = source.cutoffFrequency;
            target.highpassResonanceQ = source.highpassResonanceQ;
            target.enabled = source.enabled;
        }

        private static void CopyReverb(AudioReverbFilter source, AudioReverbFilter target)
        {
            if (source == null)
            {
                if (target != null)
                {
                    target.enabled = false;
                }
                return;
            }
            target.reverbPreset = source.reverbPreset;
            target.dryLevel = source.dryLevel;
            target.room = source.room;
            target.roomHF = source.roomHF;
            target.decayTime = source.decayTime;
            target.decayHFRatio = source.decayHFRatio;
            target.reflectionsLevel = source.reflectionsLevel;
            target.reflectionsDelay = source.reflectionsDelay;
            target.reverbLevel = source.reverbLevel;
            target.reverbDelay = source.reverbDelay;
            target.diffusion = source.diffusion;
            target.density = source.density;
            target.hfReference = source.hfReference;
            target.roomLF = source.roomLF;
            target.lfReference = source.lfReference;
            target.enabled = source.enabled;
        }

        private static void CopyChorus(AudioChorusFilter source, AudioChorusFilter target)
        {
            if (source == null)
            {
                if (target != null)
                {
                    target.enabled = false;
                }
                return;
            }
            target.dryMix = source.dryMix;
            target.wetMix1 = source.wetMix1;
            target.wetMix2 = source.wetMix2;
            target.wetMix3 = source.wetMix3;
            target.delay = source.delay;
            target.rate = source.rate;
            target.depth = source.depth;
            target.enabled = source.enabled;
        }

        private static void CopyDistortion(
            AudioDistortionFilter source, AudioDistortionFilter target)
        {
            if (source == null)
            {
                if (target != null)
                {
                    target.enabled = false;
                }
                return;
            }
            target.distortionLevel = source.distortionLevel;
            target.enabled = source.enabled;
        }

        private static void CopyEcho(AudioEchoFilter source, AudioEchoFilter target)
        {
            if (source == null)
            {
                if (target != null)
                {
                    target.enabled = false;
                }
                return;
            }
            target.delay = source.delay;
            target.decayRatio = source.decayRatio;
            target.dryMix = source.dryMix;
            target.wetMix = source.wetMix;
            target.enabled = source.enabled;
        }

        private static bool HasUnsupportedListenerEffect(
            AudioListener listener, out string reason)
        {
            reason = null;
            foreach (Component component in listener.gameObject.GetComponents<Component>())
            {
                if (component == null || component is AudioListener
                    || component is AudioLowPassFilter || component is AudioHighPassFilter
                    || component is AudioReverbFilter || component is AudioChorusFilter
                    || component is AudioDistortionFilter || component is AudioEchoFilter
                    || component is AudioSource)
                {
                    continue;
                }

                Type type = component.GetType();
                if (component is AudioReverbZone)
                {
                    reason = "an AudioReverbZone is attached directly to the native listener";
                    return true;
                }

                MethodInfo customFilter = type.GetMethod(
                    "OnAudioFilterRead",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(float[]), typeof(int) },
                    null);
                if (customFilter != null)
                {
                    reason = $"custom listener filter {type.FullName} cannot be mirrored safely";
                    return true;
                }
            }

            return false;
        }

        private static bool NativeListenerSetMatches(HybridPlayer hybridPlayer)
        {
            if (_listenerOwner != hybridPlayer || hybridPlayer == null
                || NativeListeners.Count == 0)
            {
                return false;
            }

            ListenerScratch.Clear();
            hybridPlayer.gameObject.GetComponentsInChildren(true, ListenerScratch);
            int nativeCount = 0;
            bool identityChanged = false;
            bool gameEnabledNativeListener = false;
            foreach (AudioListener listener in ListenerScratch)
            {
                if (listener == null || listener == _proxyListener)
                {
                    continue;
                }

                nativeCount++;
                bool known = false;
                foreach (NativeListenerState state in NativeListeners)
                {
                    if (state.Listener == listener)
                    {
                        known = true;
                        break;
                    }
                }
                if (!known)
                {
                    identityChanged = true;
                }

                if (listener.enabled)
                {
                    gameEnabledNativeListener = true;
                }
            }

            if (nativeCount != NativeListeners.Count)
            {
                identityChanged = true;
            }

            if (gameEnabledNativeListener)
            {
                // A game-side listener switch while the proxy owns output is authoritative. Adopt
                // the current native enabled set before deactivation, so restoration does not
                // resurrect the listener which the game just replaced.
                foreach (NativeListenerState state in NativeListeners)
                {
                    state.WasEnabled = state.Listener != null && state.Listener.enabled;
                }
            }

            // Every native listener is deliberately disabled for the complete handoff. Either a
            // new identity or a game-side re-enable requires one complete restore/reacquire cycle.
            return !identityChanged && !gameEnabledNativeListener;
        }

        private static void Deactivate(string reason)
        {
            bool wasActive = _runtimeActive;
            if (_proxyListener != null)
            {
                _proxyListener.enabled = false;
            }
            if (_proxyObject != null)
            {
                _proxyObject.SetActive(false);
            }

            foreach (NativeListenerState state in NativeListeners)
            {
                if (state.Listener != null)
                {
                    state.Listener.enabled = state.WasEnabled;
                }
            }

            NativeListeners.Clear();
            _listenerOwner = null;
            _runtimeActive = false;
            _lastInsideMixFrame = -1;
            _lastAnchorGuid = 0UL;
            _lastAnchorKind = null;

            if (wasActive)
            {
                Plugin.Log.LogInfo($"[VirtualAudio] deactivated reason={reason ?? "unknown"}.");
                _listenerAuditFramesRemaining = 1;
                _listenerAuditReason = reason ?? "unknown";

                // A user explicitly asking for native audio should leave no persistent proxy DSP
                // object behind. Recreate it cleanly on the next opt-in instead of retaining any
                // Unity filter/reverb tail state from the failed close-perspective session.
                if (string.Equals(reason, "user-toggle-off", StringComparison.Ordinal))
                {
                    DestroyProxy();
                }
            }
        }

        private static void DestroyProxy()
        {
            if (_proxyObject != null)
            {
                UnityEngine.Object.Destroy(_proxyObject);
            }
            _proxyListener = null;
            _proxyObject = null;
        }

        private static void RunPendingListenerAudit()
        {
            if (_listenerAuditFramesRemaining <= 0 || --_listenerAuditFramesRemaining > 0)
            {
                return;
            }

            string reason = _listenerAuditReason;
            _listenerAuditReason = null;
            LogListenerEnvironment("after-deactivate:" + (reason ?? "unknown"));
        }

        /// <summary>
        /// Logs the real active-listener set and nearby authored reverb zones at each handoff.
        /// This is deliberately toggle-bound, not a per-frame probe.
        /// </summary>
        private static void LogListenerEnvironment(string phase)
        {
            try
            {
                var activeListeners = new List<AudioListener>();
                foreach (AudioListener listener in Resources.FindObjectsOfTypeAll<AudioListener>())
                {
                    if (listener != null && listener.isActiveAndEnabled)
                    {
                        activeListeners.Add(listener);
                    }
                }

                var listenerDescriptions = new List<string>();
                foreach (AudioListener listener in activeListeners)
                {
                    listenerDescriptions.Add(
                        $"{GetTransformPath(listener.transform)}@{FormatVector(listener.transform.position)}");
                }

                var nearbyZones = new List<string>();
                if (activeListeners.Count == 1)
                {
                    Vector3 position = activeListeners[0].transform.position;
                    foreach (AudioReverbZone zone in Resources.FindObjectsOfTypeAll<AudioReverbZone>())
                    {
                        if (zone == null || !zone.enabled || !zone.gameObject.activeInHierarchy)
                        {
                            continue;
                        }

                        float distance = Vector3.Distance(position, zone.transform.position);
                        if (distance <= zone.maxDistance)
                        {
                            nearbyZones.Add(
                                $"{GetTransformPath(zone.transform)} preset={zone.reverbPreset} "
                              + $"distance={distance:0.###} range={zone.minDistance:0.###}-{zone.maxDistance:0.###}");
                        }
                    }
                }

                Plugin.Log.LogInfo(
                    $"[VirtualAudio/audit] phase={phase}, activeListeners={activeListeners.Count} "
                  + $"[{string.Join("; ", listenerDescriptions.ToArray())}], "
                  + $"proxyExists={_proxyObject != null}, "
                  + $"proxyActive={(_proxyListener != null && _proxyListener.isActiveAndEnabled)}, "
                  + $"nearbyReverbZones={nearbyZones.Count} "
                  + $"[{string.Join("; ", nearbyZones.ToArray())}].");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Auditing close audio listener state threw: " + ex);
            }
        }

        private static void AudioCameraStatePrefix(ref AudioCameraState __state)
        {
            __state = default(AudioCameraState);
            if (!CanRedirectAudio())
            {
                return;
            }

            Player player = GetPlayer();
            if (player == null)
            {
                return;
            }

            __state = new AudioCameraState(player);
            player.AudioSystemCameraZoomRatio = _virtualZoomRatio;
            player.AudioSystemCameraZoomDistance = _virtualZoomDistance;
            player.AudioSystemCameraIsInsideHouse = _virtualInsideMix;
            player.AudioSystemCameraPositionX = _virtualPositionX;
            player.AudioSystemCameraPositionZ = _virtualPositionZ;
        }

        private static Exception AudioCameraStateFinalizer(
            Exception __exception, AudioCameraState __state)
        {
            __state.Restore();
            return __exception;
        }

        private static IEnumerable<CodeInstruction> PlayAtDistanceTranspiler(
            IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo replacement = AccessTools.Method(
                typeof(VirtualAudioListenerHandler), nameof(GetPlayAtReferencePosition));
            return ReplaceNativeCameraPosition(
                instructions, replacement, count => _playAtReplacementCount = count);
        }

        private static IEnumerable<CodeInstruction> WaitDistanceTranspiler(
            IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo replacement = AccessTools.Method(
                typeof(VirtualAudioListenerHandler), nameof(GetWaitReferencePosition));
            return ReplaceNativeCameraPosition(
                instructions, replacement, count => _waitReplacementCount = count);
        }

        private static IEnumerable<CodeInstruction> ReplaceNativeCameraPosition(
            IEnumerable<CodeInstruction> instructions,
            MethodInfo replacement,
            Action<int> recordCount)
        {
            var codes = new List<CodeInstruction>(instructions);
            int replacements = 0;
            if (replacement != null && PlayerManagerInstanceGetter != null
                && HybridPlayer1Field != null && HybridCameraField != null && CameraField != null
                && ComponentTransformGetter != null && TransformPositionGetter != null)
            {
                for (int i = 5; i < codes.Count; i++)
                {
                    if (codes[i].Calls(TransformPositionGetter)
                        && codes[i - 1].Calls(ComponentTransformGetter)
                        && Equals(codes[i - 2].operand, CameraField)
                        && Equals(codes[i - 3].operand, HybridCameraField)
                        && Equals(codes[i - 4].operand, HybridPlayer1Field)
                        && codes[i - 5].Calls(PlayerManagerInstanceGetter))
                    {
                        // The static replacement consumes the Transform already on the stack and
                        // returns either its native position or the active virtual listener point.
                        codes[i].opcode = OpCodes.Call;
                        codes[i].operand = replacement;
                        replacements++;
                    }
                }
            }

            recordCount(replacements);
            return codes;
        }

        private static bool HasPatch(IEnumerable<Patch> patches, MethodInfo method)
        {
            if (patches == null || method == null)
            {
                return false;
            }

            foreach (Patch patch in patches)
            {
                if (patch.owner == Plugin.PluginGuid && patch.PatchMethod == method)
                {
                    return true;
                }
            }

            return false;
        }

        private static Vector3 GetPlayAtReferencePosition(Transform nativeTransform)
        {
            if (!CanRedirectAudio())
            {
                return nativeTransform != null ? nativeTransform.position : Vector3.zero;
            }

            if (!_loggedPlayAtRedirect)
            {
                _loggedPlayAtRedirect = true;
                Plugin.Log.LogInfo(
                    "[VirtualAudio] PlayAt real-camera distance gate is using the virtual listener.");
            }
            return _virtualPosition;
        }

        private static Vector3 GetWaitReferencePosition(Transform nativeTransform)
        {
            if (!CanRedirectAudio())
            {
                return nativeTransform != null ? nativeTransform.position : Vector3.zero;
            }

            if (!_loggedWaitRedirect)
            {
                _loggedWaitRedirect = true;
                Plugin.Log.LogInfo(
                    "[VirtualAudio] WaitToBeClose distance gate is using the virtual listener.");
            }
            return _virtualPosition;
        }

        private static bool CanRedirectAudio()
        {
            if (!_patchesReady || !_requestedEnabled || !_runtimeActive
                || _proxyListener == null || !_proxyListener.isActiveAndEnabled
                || GameLoadingManager.State != State.Game)
            {
                return false;
            }

            Player player = GetPlayer();
            if (player == null || player.State != GameStates.LiveMode || !player.IsUsingFreeCamera)
            {
                return false;
            }

            StoryboardManager storyboardManager = StoryboardManager.Instance;
            return storyboardManager == null || !storyboardManager.IsCurrentlyPlayingStoryboards;
        }

        private static bool IsEligibleMode(Player player)
        {
            StoryboardManager storyboardManager = StoryboardManager.Instance;
            return GameLoadingManager.State == State.Game
                && SavedGameManager.Instance != null && SavedGameManager.Instance.IsGameLoaded
                && player != null && player.State == GameStates.LiveMode
                && player.IsUsingFreeCamera
                && (storyboardManager == null || !storyboardManager.IsCurrentlyPlayingStoryboards);
        }

        private static Player GetPlayer()
        {
            return PlayerManager.Instance?.Players != null
                && PlayerManager.Instance.Players.Count > 0
                    ? PlayerManager.Instance.Players[0]
                    : null;
        }

        private static void LogReadinessFailure(string reason)
        {
            string current = reason ?? "unknown reason";
            if (_lastReadinessFailure == current)
            {
                return;
            }

            _lastReadinessFailure = current;
            Plugin.Log.LogInfo("[VirtualAudio] waiting: " + current + ".");
        }

        private static void AnnounceFailureOnce(string message)
        {
            if (_failureAnnounced)
            {
                return;
            }

            _failureAnnounced = true;
            ScreenReader.Speak(message);
        }

        private static string GetComponentInventory(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return "unavailable";
            }

            var names = new List<string>();
            foreach (Component component in gameObject.GetComponents<Component>())
            {
                if (component != null)
                {
                    names.Add(component.GetType().FullName);
                }
            }
            return string.Join(", ", names.ToArray());
        }

        private static string GetTransformPath(Transform transform)
        {
            if (transform == null)
            {
                return "unavailable";
            }

            var names = new List<string>();
            Transform current = transform;
            while (current != null && names.Count < 12)
            {
                names.Add(current.name);
                current = current.parent;
            }
            names.Reverse();
            return string.Join("/", names.ToArray());
        }

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x:0.###},{value.y:0.###},{value.z:0.###})";
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private sealed class NativeListenerState
        {
            internal NativeListenerState(AudioListener listener, bool wasEnabled)
            {
                Listener = listener;
                WasEnabled = wasEnabled;
            }

            internal AudioListener Listener { get; }

            internal bool WasEnabled { get; set; }
        }

        private struct VirtualPose
        {
            internal Vector3 Position;
            internal Quaternion Rotation;
            internal Vector3 Anchor;
            internal string AnchorKind;
            internal ulong AnchorGuid;
            internal float ZoomDistance;
            internal float ZoomRatio;
            internal bool IsInsideTarget;
        }

        private struct AudioCameraState
        {
            private readonly Player _player;
            private readonly float _zoomRatio;
            private readonly float _zoomDistance;
            private readonly float _positionX;
            private readonly float _positionZ;
            private readonly float _insideMix;
            private readonly bool _captured;

            internal AudioCameraState(Player player)
            {
                _player = player;
                _zoomRatio = player.AudioSystemCameraZoomRatio;
                _zoomDistance = player.AudioSystemCameraZoomDistance;
                _positionX = player.AudioSystemCameraPositionX;
                _positionZ = player.AudioSystemCameraPositionZ;
                _insideMix = player.AudioSystemCameraIsInsideHouse;
                _captured = true;
            }

            internal void Restore()
            {
                if (!_captured || _player == null)
                {
                    return;
                }

                _player.AudioSystemCameraZoomRatio = _zoomRatio;
                _player.AudioSystemCameraZoomDistance = _zoomDistance;
                _player.AudioSystemCameraPositionX = _positionX;
                _player.AudioSystemCameraPositionZ = _positionZ;
                _player.AudioSystemCameraIsInsideHouse = _insideMix;
            }
        }
    }
}
