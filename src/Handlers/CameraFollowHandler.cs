using System;
using System.Collections.Generic;
using System.Globalization;
using ParalivesAccess.Speech;
using Setting;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>
    /// Gives every newly followed Para a readable initial camera frame, then leaves the game's
    /// continuous follow system completely in charge.
    /// </summary>
    internal static class CameraFollowHandler
    {
        // UIGoals and UIRelationships use 0.1 when framing a loaded NPC. It is close enough to
        // read the Para while retaining substantially more scene context than ZoomTo's 0.0375
        // default.
        private const float FollowZoomRatio = 0.1f;

        private static Player _observedPlayer;
        private static ulong _observedTargetGuid;
        private static bool _enabled = true;
        private static bool _viewportDiagnosticsEnabled = true;

        /// <summary>
        /// Observes native follow starts (F, portrait actions, and other game-owned entry points).
        /// Accessibility-owned entry points call <see cref="FrameFollowStart"/> directly as well,
        /// because they may deliberately restart framing for an already-followed Para after the
        /// town map has zoomed back out.
        /// </summary>
        internal static void Tick()
        {
            if (!_enabled)
            {
                return;
            }

            try
            {
                Player player = GetPlayer();
                if (player == null || GameLoadingManager.State != State.Game
                    || player.State != GameStates.LiveMode || !player.IsUsingFreeCamera)
                {
                    _observedPlayer = player;
                    _observedTargetGuid = 0UL;
                    return;
                }

                if (!ReferenceEquals(_observedPlayer, player))
                {
                    _observedPlayer = player;
                    _observedTargetGuid = 0UL;
                }

                ulong targetGuid = player.CameraCurrentCharacterFollowTarget;
                if (targetGuid == 0UL)
                {
                    _observedTargetGuid = 0UL;
                    return;
                }
                if (targetGuid == _observedTargetGuid)
                {
                    return;
                }

                AssetCharacter character = CharacterManager.Instance?
                    .GetCharacterByGUID(targetGuid);
                if (character?.Data == null)
                {
                    // Loading can expose the GUID before its character model. Keep this transition
                    // pending so a later frame can still supply the initial framing.
                    return;
                }

                if (FrameFollowStart(player, character, "native-transition"))
                {
                    // UpdateKeyboardShortcuts maps the game's CenterCameraOnCharacter action to
                    // this exact state transition (default keyboard binding: F). Portrait follow
                    // controls use it too. Confirm the result once, after the same one-shot framing
                    // has succeeded; accessibility-owned F7 routes call FrameFollowStart directly
                    // and already provide their own confirmation, so they cannot duplicate this.
                    ScreenReader.Speak(Loc.Get(
                        "explorer_following", GetCharacterName(character)));
                }
            }
            catch (Exception ex)
            {
                _enabled = false;
                Plugin.Log.LogError("Initial camera-follow framing disabled after an exception: " + ex);
            }
        }

        /// <summary>
        /// Centers and, only when necessary, zooms in for a newly requested follow. This method
        /// does not set or reassert follow state; callers must already have set the exact target.
        /// </summary>
        internal static bool FrameFollowStart(
            Player player, AssetCharacter character, string source)
        {
            try
            {
                if (player == null || character?.Data == null || character.GUID == 0UL
                    || player.CameraCurrentCharacterFollowTarget != character.GUID
                    || GameLoadingManager.State != State.Game
                    || player.State != GameStates.LiveMode || !player.IsUsingFreeCamera
                    || TerrainManager.Instance == null || !TerrainManager.Instance.Inited
                    || (StoryboardManager.Instance != null
                        && StoryboardManager.Instance.CurrentlyPlayingStoryboardLocksCameraMovement))
                {
                    return false;
                }

                HybridPlayer hybridPlayer = PlayerManager.Instance?
                    .GetHybridPlayer(player.PlayerIndex);
                HybridCameraFree freeCamera = hybridPlayer?.HybridCamera?.FreeCamera;
                global::Setting.FreeCamera settings = Settings.Get<global::Setting.FreeCamera>();
                if (freeCamera?.LookTarget == null || settings == null
                    || !IsFinite(settings.MinZoomDistance)
                    || !IsFinite(settings.MaxZoomDistance)
                    || settings.MaxZoomDistance <= settings.MinZoomDistance)
                {
                    return false;
                }

                Vector3 characterPosition = WorldTargeting.GetCharacterPosition(character);
                if (!IsFinite(characterPosition))
                {
                    return false;
                }

                float lookTargetHeight = IsFinite(settings.LookTargetHeightInGame)
                    ? settings.LookTargetHeightInGame
                    : 0f;
                Vector3 previousLookTarget = freeCamera.LookTarget.position;
                Vector3 nextLookTarget = characterPosition
                    + new Vector3(0f, lookTargetHeight, 0f);
                float previousDistance = freeCamera.Distance;
                float previousRatio = freeCamera.DistanceRatio;
                float previousTargetDistance = freeCamera.TargetDistance;
                float previousExtraLookUp = freeCamera.CurrentExtraLookUpRatio;
                float desiredDistance = Mathf.Lerp(
                    settings.MinZoomDistance,
                    settings.MaxZoomDistance,
                    FollowZoomRatio);

                // Match UpdateFreeCamera's own immediate layer choice. Its normal per-frame follow
                // path will continue maintaining this as the Para changes floors or lots.
                if (character.Data.IsOnTerrain)
                {
                    PlayerManager.Instance.SetPlayerCameraLayer(player, 0, 0UL);
                }
                else if (character.Data.OnZoneWallObject != null)
                {
                    PlayerManager.Instance.SetPlayerCameraLayer(
                        player,
                        character.Data.OnZoneWallObject.Layer,
                        character.Data.OnZoneWallObject.LotPlacedOnGUID);
                }

                // Snap the focus point so zooming in cannot briefly magnify the old location. Only
                // TargetDistance is narrowed; Distance still uses the game's normal smooth damping.
                freeCamera.LookTarget.position = nextLookTarget;
                freeCamera.CurrentExtraLookUpRatio = 0f;
                freeCamera.TargetDistanceSpeed = 0f;

                float nextTargetDistance = previousTargetDistance;
                if (!IsFinite(nextTargetDistance)
                    || nextTargetDistance < settings.MinZoomDistance)
                {
                    nextTargetDistance = desiredDistance;
                }
                else if (nextTargetDistance > desiredDistance)
                {
                    nextTargetDistance = desiredDistance;
                }
                freeCamera.TargetDistance = nextTargetDistance;

                _observedPlayer = player;
                _observedTargetGuid = character.GUID;

                Plugin.Log.LogInfo(
                    $"[CameraFollow] source={source ?? "unknown"}, target={character.GUID}, "
                  + $"name='{GetCharacterName(character)}', position={characterPosition}, "
                  + $"lookTarget={previousLookTarget}->{nextLookTarget}, "
                  + $"distance={previousDistance:0.###}, ratio={previousRatio:0.###}, "
                  + $"targetDistance={previousTargetDistance:0.###}->{nextTargetDistance:0.###}, "
                  + $"desiredRatio={FollowZoomRatio:0.###}, "
                  + $"extraLookUp={previousExtraLookUp:0.###}->0.");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError(
                    $"Initial camera-follow framing threw for source={source ?? "unknown"}, "
                  + $"target={character?.GUID ?? 0UL}: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Records camera, projection, renderer, and ray-obstruction evidence at a semantic scene
        /// boundary. This is deliberately log-only: the measurements decide what later visual
        /// descriptions can truthfully say, but are not useful speech themselves.
        /// </summary>
        internal static void LogViewportSnapshot(
            string reason, AssetCharacter primary, SocialGroup conversationGroup = null)
        {
            if (!_viewportDiagnosticsEnabled)
            {
                return;
            }

            try
            {
                Player player = GetPlayer();
                HybridPlayer hybridPlayer = player != null
                    ? PlayerManager.Instance?.GetHybridPlayer(player.PlayerIndex)
                    : null;
                HybridCamera hybridCamera = hybridPlayer?.HybridCamera;
                HybridCameraFree freeCamera = hybridCamera?.FreeCamera;
                Camera camera = hybridCamera?.Camera;
                ulong followedGuid = player?.CameraCurrentCharacterFollowTarget ?? 0UL;
                ulong selectedGuid = player?.SelectedCharactersGUID != null
                    && player.SelectedCharactersGUID.Count > 0
                        ? player.SelectedCharactersGUID[0]
                        : 0UL;
                string safeReason = LogText(reason);

                if (camera == null || freeCamera == null)
                {
                    Plugin.Log.LogInfo(
                        $"[CameraView] reason='{safeReason}', cameraReady=False, "
                      + $"follow={followedGuid}, selected={selectedGuid}, "
                      + $"primary={primary?.GUID ?? 0UL}, "
                      + $"conversation={conversationGroup?.GUID ?? 0UL}.");
                    return;
                }

                Plugin.Log.LogInfo(
                    $"[CameraView] reason='{safeReason}', cameraReady=True, "
                  + $"follow={followedGuid}, selected={selectedGuid}, "
                  + $"primary={primary?.GUID ?? 0UL}, "
                  + $"conversation={conversationGroup?.GUID ?? 0UL}, "
                  + $"cameraPosition={FormatVector(camera.transform.position)}, "
                  + $"cameraForward={FormatVector(camera.transform.forward)}, "
                  + $"freeCameraPosition={FormatVector(freeCamera.CameraTransform != null ? freeCamera.CameraTransform.position : Vector3.zero)}, "
                  + $"lookTarget={FormatVector(freeCamera.LookTarget != null ? freeCamera.LookTarget.position : Vector3.zero)}, "
                  + $"distance={FormatFloat(freeCamera.Distance)}, "
                  + $"distanceRatio={FormatFloat(freeCamera.DistanceRatio)}, "
                  + $"targetDistance={FormatFloat(freeCamera.TargetDistance)}, "
                  + $"xAngleRatio={FormatFloat(freeCamera.XAngleRatio)}, "
                  + $"extraLookUp={FormatFloat(freeCamera.CurrentExtraLookUpRatio)}, "
                  + $"fieldOfView={FormatFloat(camera.fieldOfView)}.");

                var logged = new HashSet<ulong>();
                if (primary != null && primary.GUID != 0UL)
                {
                    logged.Add(primary.GUID);
                    LogViewportSubject(camera, primary, "primary");
                }

                if (conversationGroup?.CharactersInGroup != null)
                {
                    foreach (ulong guid in conversationGroup.CharactersInGroup)
                    {
                        if (guid == 0UL || !logged.Add(guid))
                        {
                            continue;
                        }

                        AssetCharacter participant = CharacterManager.Instance?
                            .GetCharacterByGUID(guid);
                        LogViewportSubject(camera, participant, "conversation-participant", guid);
                    }
                }
            }
            catch (Exception ex)
            {
                _viewportDiagnosticsEnabled = false;
                Plugin.Log.LogError(
                    "Camera viewport diagnostics disabled after an exception: " + ex);
            }
        }

        private static void LogViewportSubject(
            Camera camera, AssetCharacter character, string role, ulong requestedGuid = 0UL)
        {
            ulong guid = character?.GUID ?? requestedGuid;
            try
            {
                CharacterVisual visual = guid != 0UL
                    ? CharacterManager.Instance?.GetLoadedCharacterVisual(guid)
                    : null;
                Renderer renderer = visual?.Renderer;
                Vector3 dataPosition = character?.Data?.Position ?? Vector3.zero;
                Vector3 visualPosition = visual != null
                    ? visual.transform.position
                    : dataPosition;
                bool visualActive = visual != null && visual.gameObject != null
                    && visual.gameObject.activeInHierarchy;
                bool visualVisible = visual?.IsVisible == true;
                bool rendererReady = renderer != null;
                bool rendererVisible = renderer?.isVisible == true;
                bool rendererEnabled = renderer?.enabled == true;
                Bounds bounds = rendererReady
                    ? renderer.bounds
                    : new Bounds(visualPosition, Vector3.zero);
                Vector3 viewport = camera.WorldToViewportPoint(bounds.center);
                bool inViewport = viewport.z > 0f
                    && viewport.x >= 0f && viewport.x <= 1f
                    && viewport.y >= 0f && viewport.y <= 1f;
                bool inSafeFrame = viewport.z > 0f
                    && viewport.x >= 0.08f && viewport.x <= 0.92f
                    && viewport.y >= 0.08f && viewport.y <= 0.92f;
                Vector3 topViewport = camera.WorldToViewportPoint(
                    bounds.center + Vector3.up * bounds.extents.y);
                Vector3 bottomViewport = camera.WorldToViewportPoint(
                    bounds.center - Vector3.up * bounds.extents.y);
                float projectedHeight = topViewport.z > 0f && bottomViewport.z > 0f
                    ? Mathf.Abs(topViewport.y - bottomViewport.y)
                    : 0f;
                string obstruction = FindPotentialObstruction(camera, visual, bounds.center);

                Plugin.Log.LogInfo(
                    $"[CameraView/subject] role={role ?? "unknown"}, guid={guid}, "
                  + $"name='{LogText(GetCharacterName(character))}', "
                  + $"dataPosition={FormatVector(dataPosition)}, "
                  + $"visualPosition={FormatVector(visualPosition)}, "
                  + $"dataVisualDelta={FormatFloat(Vector3.Distance(dataPosition, visualPosition))}, "
                  + $"visualLoaded={visual != null}, visualActive={visualActive}, "
                  + $"visualVisible={visualVisible}, rendererReady={rendererReady}, "
                  + $"rendererEnabled={rendererEnabled}, rendererVisible={rendererVisible}, "
                  + $"boundsCenter={FormatVector(bounds.center)}, boundsExtents={FormatVector(bounds.extents)}, "
                  + $"viewport={FormatVector(viewport)}, inViewport={inViewport}, "
                  + $"inSafeFrame={inSafeFrame}, projectedHeight={FormatFloat(projectedHeight)}, "
                  + $"potentialObstruction='{LogText(obstruction)}'.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning(
                    $"[CameraView/subject] role={role ?? "unknown"}, guid={guid}, "
                  + $"measurement failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static string FindPotentialObstruction(
            Camera camera, CharacterVisual targetVisual, Vector3 targetPosition)
        {
            if (camera == null || !IsFinite(targetPosition))
            {
                return "not measured";
            }

            Vector3 origin = camera.transform.position;
            Vector3 delta = targetPosition - origin;
            float distance = delta.magnitude;
            if (!IsFinite(origin) || !IsFinite(distance) || distance <= 0.1f)
            {
                return "none";
            }

            float rayDistance = Mathf.Max(0f, distance - 0.05f);
            RaycastHit[] hits = Physics.RaycastAll(
                new Ray(origin, delta / distance),
                rayDistance,
                ~0,
                QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));
            foreach (RaycastHit hit in hits)
            {
                Collider collider = hit.collider;
                if (collider == null)
                {
                    continue;
                }

                Transform hitTransform = collider.transform;
                if (targetVisual != null
                    && (hitTransform == targetVisual.transform
                        || hitTransform.IsChildOf(targetVisual.transform)))
                {
                    continue;
                }
                if (hitTransform == camera.transform
                    || hitTransform.IsChildOf(camera.transform))
                {
                    continue;
                }

                CharacterVisual characterVisual = collider.GetComponentInParent<CharacterVisual>();
                if (characterVisual != null)
                {
                    AssetCharacter character = CharacterManager.Instance?
                        .GetCharacterByGUID(characterVisual.CharacterGUID);
                    return string.Format(
                        CultureInfo.InvariantCulture,
                        "character {0} '{1}', collider={2}, layer={3}, distance={4:0.###}",
                        characterVisual.CharacterGUID,
                        GetCharacterName(character),
                        collider.GetType().Name,
                        GetLayerName(collider.gameObject.layer),
                        hit.distance);
                }

                ItemObjectRoot item = collider.GetComponentInParent<ItemObjectRoot>();
                if (item != null)
                {
                    return string.Format(
                        CultureInfo.InvariantCulture,
                        "item {0} '{1}', collider={2}, layer={3}, distance={4:0.###}",
                        item.InstanceID,
                        ItemDescription.GetName(item) ?? "unknown",
                        collider.GetType().Name,
                        GetLayerName(collider.gameObject.layer),
                        hit.distance);
                }

                return string.Format(
                    CultureInfo.InvariantCulture,
                    "object '{0}', collider={1}, layer={2}, distance={3:0.###}",
                    collider.gameObject != null ? collider.gameObject.name : "unknown",
                    collider.GetType().Name,
                    GetLayerName(collider.gameObject.layer),
                    hit.distance);
            }

            return "none";
        }

        private static string GetLayerName(int layer)
        {
            string name = LayerMask.LayerToName(layer);
            return string.IsNullOrWhiteSpace(name)
                ? layer.ToString(CultureInfo.InvariantCulture)
                : name;
        }

        private static string FormatFloat(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string FormatVector(Vector3 value)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "({0:0.###},{1:0.###},{2:0.###})",
                value.x,
                value.y,
                value.z);
        }

        private static string LogText(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? "unknown"
                : value.Trim().Replace('\r', ' ').Replace('\n', ' ').Replace('\'', '’');
        }

        private static Player GetPlayer()
        {
            return PlayerManager.Instance?.Players != null
                && PlayerManager.Instance.Players.Count > 0
                    ? PlayerManager.Instance.Players[0]
                    : null;
        }

        private static string GetCharacterName(AssetCharacter character)
        {
            return character?.Data != null
                && !string.IsNullOrWhiteSpace(character.Data.FullName)
                    ? character.Data.FullName.Trim()
                    : "unknown";
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }
    }
}
