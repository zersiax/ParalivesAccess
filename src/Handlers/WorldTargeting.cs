using System;
using ParalivesAccess.Speech;
using Setting;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>
    /// Shared camera-only framing and camera-relative descriptions for persistent world targets.
    /// </summary>
    /// <remarks>
    /// Moving the free camera is navigation, not locomotion. These helpers never select, teleport,
    /// or otherwise move a Para. Interaction handlers remain responsible for validating and
    /// opening the game's native action menu after a target has been framed.
    /// </remarks>
    internal static class WorldTargeting
    {
        private const float ObjectZoomRatio = 0.1f;

        /// <summary>Returns an item's world position using the same root point as interaction handoff.</summary>
        internal static Vector3 GetItemPosition(ItemObjectRoot root)
        {
            return root?.CubeTransform != null
                ? root.CubeTransform.LocalPosition
                : (root != null ? root.transform.position : Vector3.zero);
        }

        /// <summary>True only for the active visible character visual with the requested GUID.</summary>
        internal static bool IsActiveCharacterVisual(
            ulong characterGuid, CharacterVisual visual)
        {
            return characterGuid != 0UL && visual != null
                && visual.CharacterGUID == characterGuid
                && visual.gameObject != null && visual.gameObject.activeInHierarchy
                && visual.IsVisible;
        }

        /// <summary>
        /// Returns the position the game's character selector can currently hit. Saved character
        /// coordinates are retained only as the fallback when no live visual is available.
        /// </summary>
        internal static Vector3 GetCharacterPosition(AssetCharacter character)
        {
            CharacterVisual visual;
            CharacterManager manager = CharacterManager.Instance;
            if (character != null && manager?.LoadedCharacterVisuals != null
                && manager.LoadedCharacterVisuals.TryGetValue(character.GUID, out visual)
                && IsActiveCharacterVisual(character.GUID, visual)
                && IsFinite(visual.transform.position))
            {
                return visual.transform.position;
            }

            return character?.Data != null ? character.Data.Position : Vector3.zero;
        }

        /// <summary>True when a captured item still resolves to the exact same live instance.</summary>
        internal static bool IsSameLiveItem(ItemObjectRoot root, int instanceId, ulong lotGuid = 0UL)
        {
            if (root == null || root.gameObject == null || !root.gameObject.activeInHierarchy
                || root.InstanceID != instanceId || ItemManager.Instance == null
                || ItemManager.Instance.GetItemByInstanceID(instanceId) != root)
            {
                return false;
            }

            if (lotGuid != 0UL && root.LotPlacedOnGUID != lotGuid)
            {
                return false;
            }

            return true;
        }

        /// <summary>Frames a live item without moving or selecting any Para.</summary>
        internal static bool FrameItem(ItemObjectRoot root, Player player, string source)
        {
            if (root == null)
            {
                return false;
            }

            return FramePosition(
                player,
                root.LotPlacedOnGUID,
                root.Layer,
                GetItemPosition(root),
                ObjectZoomRatio,
                source,
                "item=" + root.InstanceID);
        }

        /// <summary>Frames a Para without changing character selection.</summary>
        internal static bool FrameCharacter(AssetCharacter character, Player player, ulong lotGuid)
        {
            if (character?.Data == null)
            {
                return false;
            }

            int layer = character.Data.OnZoneWallObject != null
                ? character.Data.OnZoneWallObject.Layer
                : 0;
            return FramePosition(
                player,
                lotGuid,
                layer,
                GetCharacterPosition(character),
                ObjectZoomRatio,
                "para",
                "character=" + character.GUID);
        }

        /// <summary>Frames a room or outdoor-area anchor without changing character state.</summary>
        internal static bool FrameArea(
            Player player, ulong lotGuid, int layer, Vector3 position, string source)
        {
            global::Setting.FreeCamera settings = Settings.Get<global::Setting.FreeCamera>();
            float ratio = settings != null ? settings.HouseholdChangeZoom : 0.3f;
            if (float.IsNaN(ratio) || float.IsInfinity(ratio))
            {
                ratio = 0.3f;
            }

            return FramePosition(
                player,
                lotGuid,
                layer,
                position,
                Mathf.Clamp01(ratio),
                source,
                "area");
        }

        /// <summary>Describes a point by distance and direction from the free-camera focus.</summary>
        internal static string DescribeFromCamera(Vector3 position)
        {
            try
            {
                Transform lookTarget = PlayerManager.Instance?.HybridPlayer1?
                    .HybridCamera?.FreeCamera?.LookTarget;
                if (lookTarget == null || !IsFinite(position) || !IsFinite(lookTarget.position))
                {
                    return null;
                }

                Vector3 delta = position - lookTarget.position;
                Vector3 horizontal = new Vector3(delta.x, 0f, delta.z);
                float distance = horizontal.magnitude;
                if (distance < 0.1f)
                {
                    return Loc.Get("explorer_at_camera_focus");
                }

                Vector3 forward = lookTarget.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude < 0.001f)
                {
                    forward = Vector3.forward;
                }
                forward.Normalize();

                Vector3 right = lookTarget.right;
                right.y = 0f;
                if (right.sqrMagnitude < 0.001f)
                {
                    right = Vector3.right;
                }
                right.Normalize();

                Vector3 direction = horizontal.normalized;
                float angle = Mathf.Atan2(
                    Vector3.Dot(direction, right),
                    Vector3.Dot(direction, forward)) * Mathf.Rad2Deg;
                string directionText = DirectionForAngle(angle);
                return Loc.Get(
                    "explorer_camera_relation",
                    directionText,
                    ScreenReader.NormalizeForOutput(
                        MeasurementUnitService.FormatLength(distance)));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Describing a world target from the camera threw: " + ex);
                return null;
            }
        }

        private static bool FramePosition(
            Player player,
            ulong lotGuid,
            int layer,
            Vector3 position,
            float zoomRatio,
            string source,
            string identity)
        {
            try
            {
                HybridPlayer hybridPlayer = PlayerManager.Instance?.HybridPlayer1;
                HybridCameraFree freeCamera = hybridPlayer?.HybridCamera?.FreeCamera;
                if (player == null || freeCamera == null || !IsFinite(position))
                {
                    return false;
                }

                if (lotGuid != 0UL)
                {
                    PlayerManager.Instance.SetPlayerCameraLayer(player, layer, lotGuid);
                }

                float previousRatio = freeCamera.DistanceRatio;
                ulong previousFollowTarget = player.CameraCurrentCharacterFollowTarget;
                freeCamera.ZoomTo(position, zoomRatio);
                Plugin.Log.LogInfo(
                    $"[ExplorerCamera] source={source}, {identity}, lot={lotGuid}, layer={layer}, "
                  + $"position={position}, previousRatio={previousRatio:0.###}, "
                  + $"targetRatio={zoomRatio:0.###}, selectedCharacters="
                  + $"{player.SelectedCharactersGUID?.Count ?? 0}, previousFollowTarget="
                  + $"{previousFollowTarget}, followTarget="
                  + $"{player.CameraCurrentCharacterFollowTarget}.");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Framing a world target threw: " + ex);
                return false;
            }
        }

        private static string DirectionForAngle(float angle)
        {
            if (angle >= -22.5f && angle < 22.5f)
            {
                return Loc.Get("direction_ahead");
            }
            if (angle >= 22.5f && angle < 67.5f)
            {
                return Loc.Get("direction_ahead_right");
            }
            if (angle >= 67.5f && angle < 112.5f)
            {
                return Loc.Get("direction_right");
            }
            if (angle >= 112.5f && angle < 157.5f)
            {
                return Loc.Get("direction_behind_right");
            }
            if (angle >= 157.5f || angle < -157.5f)
            {
                return Loc.Get("direction_behind");
            }
            if (angle >= -157.5f && angle < -112.5f)
            {
                return Loc.Get("direction_behind_left");
            }
            if (angle >= -112.5f && angle < -67.5f)
            {
                return Loc.Get("direction_left");
            }

            return Loc.Get("direction_ahead_left");
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x)
                && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
                && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }
    }
}
