using System;
using System.Collections.Generic;
using Setting;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>
    /// Raises diegetic Foley owned by the followed (or otherwise selected) Para without changing
    /// the game's music, UI, voice-over, general ambience, spatial position, or rolloff curves.
    /// </summary>
    internal static class ObservedParaAudioHandler
    {
        private const float MaximumBoostDecibels = 12f;

        private static readonly HashSet<int> ObservedActivityItemIds = new HashSet<int>();
        private static readonly HashSet<ulong> LoggedAudioInstanceIds = new HashSet<ulong>();

        private static ulong _observedCharacterGuid;
        private static bool _enabled = true;

        /// <summary>
        /// Additional gain applied after the game's own channel, unit, user-option, and ducking
        /// calculation. The source remains spatialized and therefore retains native direction and
        /// distance attenuation.
        /// </summary>
        internal static float BoostDecibels { get; set; } = 6f;

        internal static void Tick()
        {
            if (!_enabled)
            {
                return;
            }

            try
            {
                if (GameLoadingManager.State != State.Game)
                {
                    ResetSessionState();
                    return;
                }

                Player player = GetPlayer();
                ulong characterGuid = player?.CameraCurrentCharacterFollowTarget ?? 0UL;
                if (characterGuid == 0UL && player != null)
                {
                    characterGuid = player.GetSelectedCharacterGUID();
                }

                AssetCharacter character = characterGuid != 0UL
                    ? CharacterManager.Instance?.GetCharacterByGUID(characterGuid)
                    : null;
                if (character?.Data == null)
                {
                    ResetObservedCharacter(characterGuid);
                    return;
                }

                if (_observedCharacterGuid != characterGuid)
                {
                    ResetObservedCharacter(characterGuid);
                }

                float boostDecibels = Mathf.Clamp(
                    BoostDecibels, 0f, MaximumBoostDecibels);
                if (boostDecibels <= 0.01f)
                {
                    return;
                }

                CollectRunningActivityItems(character);

                AudioManager audioManager = AudioManager.Instance;
                if (audioManager?.ActiveAudioUnits == null)
                {
                    return;
                }

                float linearGain = Mathf.Pow(10f, boostDecibels / 20f);
                Camera camera = PlayerManager.Instance?
                    .GetHybridPlayer(player.PlayerIndex)?.HybridCamera?.Camera;

                foreach (ActiveAudioUnit active in audioManager.ActiveAudioUnits)
                {
                    string ownership;
                    if (!ShouldBoost(active, characterGuid, out ownership))
                    {
                        continue;
                    }

                    BoostBranch(
                        active.RootBranch,
                        active,
                        character,
                        ownership,
                        linearGain,
                        boostDecibels,
                        camera);
                }
            }
            catch (Exception ex)
            {
                _enabled = false;
                Plugin.Log.LogError(
                    "Observed-Para Foley boost disabled after an exception: " + ex);
            }
        }

        private static bool ShouldBoost(
            ActiveAudioUnit active, ulong characterGuid, out string ownership)
        {
            ownership = null;
            AudioUnit root = active?.RootBranch?.AudioUnit;
            if (root == null || !IsFoley(root, active.AudioChannel))
            {
                return false;
            }

            // UpdateCharacterAnimationsBasedOnCurrentActions passes character.GUID as the native
            // audio instance GUID for footsteps and every other animation-triggered sound.
            if (active.InstanceGUID == characterGuid)
            {
                ownership = "character";
                return true;
            }

            // PlayAudioStateSubRule retains the actual ItemObjectRoot. Boost it only while that
            // exact item participates in the observed Para's running/ending interaction.
            int itemId = active.Item?.InstanceID ?? -1;
            if (itemId >= 0 && ObservedActivityItemIds.Contains(itemId))
            {
                ownership = "running-activity-item";
                return true;
            }

            return false;
        }

        private static bool IsFoley(AudioUnit root, AudioChannel channel)
        {
            if (root.Type == AudioUnitType.Footstep)
            {
                return true;
            }

            string rootName = root.DisplayName;
            string channelPrefix = channel?.PrefixToDetect;
            return (!string.IsNullOrEmpty(rootName)
                    && rootName.StartsWith("Foley", StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(channelPrefix)
                    && channelPrefix.StartsWith("Foley", StringComparison.OrdinalIgnoreCase));
        }

        private static void CollectRunningActivityItems(AssetCharacter character)
        {
            ObservedActivityItemIds.Clear();
            AddItemId(character.Data.CarriedItemInstanceID);

            List<AssetCharacterDataInteraction> queue =
                character.Data.CurrentInteractionsInQueue;
            if (queue == null)
            {
                return;
            }

            foreach (AssetCharacterDataInteraction interaction in queue)
            {
                if (interaction == null || !IsRunningOrEnding(interaction.State))
                {
                    continue;
                }

                AddItemId(interaction.Item?.InstanceID ?? -1);
                AddItemId(interaction.ItemInstanceID);
                AddItemId(interaction.ItemInstanceIDLoaded);
                if (interaction.ItemCreatedInstanceID > 0)
                {
                    AddItemId(interaction.ItemCreatedInstanceID);
                }

                ItemIDAndSlot itemAndSlot;
                if (interaction.TargetItem.IsSome(out itemAndSlot))
                {
                    AddItemId(itemAndSlot.ItemInstanceID);
                }
                if (interaction.UsedItem.IsSome(out itemAndSlot))
                {
                    AddItemId(itemAndSlot.ItemInstanceID);
                }
            }
        }

        private static bool IsRunningOrEnding(AssetCharacterDataInteractionState state)
        {
            return state == AssetCharacterDataInteractionState.Running
                || state == AssetCharacterDataInteractionState.ToBeCanceled
                || state == AssetCharacterDataInteractionState.Cancelling;
        }

        private static void AddItemId(int itemId)
        {
            if (itemId >= 0)
            {
                ObservedActivityItemIds.Add(itemId);
            }
        }

        private static void BoostBranch(
            AudioUnitSolvingBranch branch,
            ActiveAudioUnit root,
            AssetCharacter character,
            string ownership,
            float linearGain,
            float boostDecibels,
            Camera camera)
        {
            if (branch == null)
            {
                return;
            }

            AudioInstance instance = branch.AudioInstance;
            AudioSource source = instance?.AudioSource;
            if (source != null)
            {
                float nativeVolume = source.volume;
                if (!float.IsNaN(nativeVolume) && !float.IsInfinity(nativeVolume)
                    && nativeVolume > 0f)
                {
                    // AudioSource.volume normally tops out at one. Never lower a native value if
                    // this Unity build permits a greater value; otherwise stop at its clean ceiling.
                    float boostedVolume = nativeVolume >= 1f
                        ? nativeVolume
                        : Mathf.Min(1f, nativeVolume * linearGain);
                    source.volume = boostedVolume;

                    if (boostedVolume > nativeVolume + 0.001f
                        && LoggedAudioInstanceIds.Add(instance.UniqueID))
                    {
                        float distance = camera != null && source.spatialBlend > 0f
                            ? Vector3.Distance(
                                camera.transform.position,
                                source.transform.position)
                            : 0f;
                        Plugin.Log.LogInfo(
                            $"[AudioFocus] character={character.GUID}, "
                          + $"name='{LogText(character.Data.FullName)}', "
                          + $"ownership={ownership}, item={root.Item?.InstanceID ?? -1}, "
                          + $"root='{LogText(root.RootBranch.AudioUnit?.DisplayName)}', "
                          + $"leaf='{LogText(branch.AudioUnit?.DisplayName)}', "
                          + $"channel='{LogText(root.AudioChannel?.DisplayName)}', "
                          + $"distance={distance:0.###}, spatialBlend={source.spatialBlend:0.###}, "
                          + $"nativeVolume={nativeVolume:0.###}, "
                          + $"boostedVolume={boostedVolume:0.###}, "
                          + $"configuredBoostDb={boostDecibels:0.###}.");
                    }
                }
            }

            if (branch.Branches == null)
            {
                return;
            }

            foreach (AudioUnitSolvingBranch child in branch.Branches)
            {
                BoostBranch(
                    child,
                    root,
                    character,
                    ownership,
                    linearGain,
                    boostDecibels,
                    camera);
            }
        }

        private static void ResetSessionState()
        {
            _observedCharacterGuid = 0UL;
            ObservedActivityItemIds.Clear();
            LoggedAudioInstanceIds.Clear();
        }

        private static void ResetObservedCharacter(ulong characterGuid)
        {
            _observedCharacterGuid = characterGuid;
            ObservedActivityItemIds.Clear();
            LoggedAudioInstanceIds.Clear();
        }

        private static Player GetPlayer()
        {
            return PlayerManager.Instance?.Players != null
                && PlayerManager.Instance.Players.Count > 0
                    ? PlayerManager.Instance.Players[0]
                    : null;
        }

        private static string LogText(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? "unknown"
                : value.Trim().Replace('\r', ' ').Replace('\n', ' ').Replace('\'', '’');
        }
    }
}
