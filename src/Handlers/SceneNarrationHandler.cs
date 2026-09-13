using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using ParalivesAccess.Speech;
using Setting;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>
    /// Turns authoritative visual simulation changes into a restrained description stream for the
    /// followed (or, when nothing is followed, selected) Para.
    /// </summary>
    /// <remarks>
    /// This is deliberately not a frame-by-frame animation narrator. It reports only stable scene
    /// boundaries: an explained visual/audio popup, a sourced emotion change, a conversation turn,
    /// a room/floor transition, or a curated activity phase. Unknown internal animation names are
    /// logged so later descriptions can be based on evidence rather than spoken as developer text.
    /// Background descriptions queue behind current screen-reader output instead of interrupting
    /// menus and control feedback.
    /// </remarks>
    public static class SceneNarrationHandler
    {
        public static bool Enabled { get; set; } = true;

        private const float PollIntervalSeconds = 0.2f;
        private const float AreaSettleSeconds = 0.35f;
        private const float StairSequenceGapSeconds = 3f;
        private const float StairSequenceDistanceSquared = 100f;

        private struct TogetherBarUpdateState
        {
            public bool WasFull;
            public SocialGroup Group;
        }

        private sealed class EmotionContribution
        {
            public ulong StatusEffectGuid;
            public ulong BrainLogicGuid;
            public ulong TogetherCardGuid;
            public ulong OtherCharacterGuid;
            public ulong StoryCardGuid;
            public float Value;
            public string Reason;

            public bool IsFromTogetherCard => TogetherCardGuid != 0UL;

            public bool Matches(EmotionContribution other)
            {
                return other != null
                    && StatusEffectGuid == other.StatusEffectGuid
                    && BrainLogicGuid == other.BrainLogicGuid
                    && TogetherCardGuid == other.TogetherCardGuid
                    && OtherCharacterGuid == other.OtherCharacterGuid
                    && StoryCardGuid == other.StoryCardGuid
                    && Mathf.Approximately(Value, other.Value);
            }
        }

        private sealed class EmotionSnapshot
        {
            public int Value;
            public readonly List<EmotionContribution> Contributions =
                new List<EmotionContribution>();
        }

        private sealed class ActivityObservation
        {
            public ulong ActionGuid;
            public ulong AnimationGuid;
        }

        private struct AreaIdentity : IEquatable<AreaIdentity>
        {
            public ulong LotGuid;
            public int RoomInstanceId;
            public int Layer;

            public bool Equals(AreaIdentity other)
            {
                return LotGuid == other.LotGuid
                    && RoomInstanceId == other.RoomInstanceId
                    && Layer == other.Layer;
            }

            public override bool Equals(object obj)
            {
                return obj is AreaIdentity && Equals((AreaIdentity)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = LotGuid.GetHashCode();
                    hash = (hash * 397) ^ RoomInstanceId;
                    return (hash * 397) ^ Layer;
                }
            }
        }

        private static readonly Dictionary<ulong, EmotionSnapshot> EmotionSnapshots =
            new Dictionary<ulong, EmotionSnapshot>();

        private static readonly Dictionary<ulong, ActivityObservation> Activities =
            new Dictionary<ulong, ActivityObservation>();

        private static readonly List<ulong> ActivityRemovalBuffer = new List<ulong>();

        // ProcessOutcomes returns the same result objects that UIResults later reveals one at a
        // time. Keep those exact references so the card can be reported once as a coherent result
        // while unrelated UIResultItem popups retain their existing standalone announcement.
        private static readonly List<OutcomeResultData> AggregatedTogetherCardResults =
            new List<OutcomeResultData>();

        // UIStoryboardImageAndTextItem.Init is called once per visible text, per frame. Remember
        // the last frame for each exact authored element so a continuous caption is announced once,
        // while the same element may still be announced if a later storyboard shows it again.
        private static readonly Dictionary<string, int> TutorialStoryboardTextFrames =
            new Dictionary<string, int>();

        private static FieldInfo _togetherBarIsFullField;
        private static FieldInfo _togetherBarGroupField;
        private static ulong _observedCharacterGuid;
        private static float _nextPollTime;
        private static bool _hasBaseline;

        private static ulong _conversationGroupGuid;
        private static ulong _conversationTalkerGuid;
        private static ulong _conversationListenerGuid;
        private static ulong _conversationTagGuid;
        private static ulong _lastCardGroupGuid;
        private static float _lastCardTime;

        private static AreaIdentity _stableArea;
        private static AreaIdentity _candidateArea;
        private static float _candidateAreaSince;
        private static bool _candidateBeganWhileMoving;

        private static bool _hasStairSequence;
        private static ulong _lastStairCharacterGuid;
        private static ulong _lastStairInteractionGuid;
        private static bool _lastStairWasDown;
        private static bool _lastStairWasIndoors;
        private static float _lastStairTime;
        private static Vector3 _lastStairPosition;

        /// <summary>Installs exact visual-event and locomotion-boundary patches.</summary>
        public static bool ApplyPatches(Harmony harmony)
        {
            try
            {
                MethodInfo togetherBarUpdate = AccessTools.Method(
                    typeof(UICharactersTogetherBar), "Update", Type.EmptyTypes);
                MethodInfo showResult = AccessTools.Method(
                    typeof(UIResultItem), nameof(UIResultItem.ShowResult),
                    new[] { typeof(OutcomeResultData), typeof(int) });
                MethodInfo processOutcomes = AccessTools.Method(
                    typeof(TogetherManager), nameof(TogetherManager.ProcessOutcomes),
                    new[]
                    {
                        typeof(bool), typeof(ulong), typeof(ulong), typeof(SocialGroup),
                        typeof(ulong), typeof(List<ulong>), typeof(ulong), typeof(ulong),
                        typeof(ulong)
                    });
                MethodInfo pathNodeStart = AccessTools.Method(
                    typeof(UpdateCharacterLocomotion), "OnPathNodeStart",
                    new[]
                    {
                        typeof(AssetCharacter), typeof(CharacterVisual),
                        typeof(AssetCharacterDataInteraction), typeof(CurrentAction)
                    });
                MethodInfo storyboardTextInit = AccessTools.Method(
                    typeof(UIStoryboardsImageAndTextItem),
                    nameof(UIStoryboardsImageAndTextItem.Init),
                    new[] { typeof(StoryboardElement) });

                _togetherBarIsFullField = AccessTools.Field(
                    typeof(UICharactersTogetherBar), "_isFull");
                _togetherBarGroupField = AccessTools.Field(
                    typeof(UICharactersTogetherBar), "_socialGroup");

                if (togetherBarUpdate != null && _togetherBarIsFullField != null
                    && _togetherBarGroupField != null)
                {
                    harmony.Patch(
                        togetherBarUpdate,
                        prefix: new HarmonyMethod(AccessTools.Method(
                            typeof(SceneNarrationHandler), nameof(TogetherBarUpdatePrefix))),
                        postfix: new HarmonyMethod(AccessTools.Method(
                            typeof(SceneNarrationHandler), nameof(TogetherBarUpdatePostfix))));
                }
                if (showResult != null)
                {
                    harmony.Patch(
                        showResult,
                        postfix: new HarmonyMethod(AccessTools.Method(
                            typeof(SceneNarrationHandler), nameof(ResultShownPostfix))));
                }
                if (processOutcomes != null)
                {
                    harmony.Patch(
                        processOutcomes,
                        postfix: new HarmonyMethod(AccessTools.Method(
                            typeof(SceneNarrationHandler), nameof(TogetherCardProcessedPostfix))));
                }
                if (pathNodeStart != null)
                {
                    harmony.Patch(
                        pathNodeStart,
                        postfix: new HarmonyMethod(AccessTools.Method(
                            typeof(SceneNarrationHandler), nameof(PathNodeStartedPostfix))));
                }
                if (storyboardTextInit != null)
                {
                    harmony.Patch(
                        storyboardTextInit,
                        postfix: new HarmonyMethod(AccessTools.Method(
                            typeof(SceneNarrationHandler), nameof(StoryboardTextShownPostfix))));
                }

                var patched = new List<MethodBase>(harmony.GetPatchedMethods());
                bool barApplied = togetherBarUpdate != null && patched.Contains(togetherBarUpdate);
                bool resultApplied = showResult != null && patched.Contains(showResult);
                bool cardApplied = processOutcomes != null && patched.Contains(processOutcomes);
                bool pathApplied = pathNodeStart != null && patched.Contains(pathNodeStart);
                bool storyboardTextApplied = storyboardTextInit != null
                    && patched.Contains(storyboardTextInit);
                Plugin.Log.LogInfo(
                    "Patched scene narration, "
                  + $"togetherBar={barApplied}, resultPopups={resultApplied}, "
                  + $"conversationCards={cardApplied}, pathLinks={pathApplied}, "
                  + $"tutorialStoryboardText={storyboardTextApplied}.");
                return barApplied && resultApplied && cardApplied && pathApplied;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Patching scene narration threw: " + ex);
                return false;
            }
        }

        private static void TogetherBarUpdatePrefix(
            UICharactersTogetherBar __instance, out TogetherBarUpdateState __state)
        {
            __state = default(TogetherBarUpdateState);
            if (__instance == null || _togetherBarIsFullField == null
                || _togetherBarGroupField == null)
            {
                return;
            }

            try
            {
                __state.WasFull = (bool)_togetherBarIsFullField.GetValue(__instance);
                __state.Group = _togetherBarGroupField.GetValue(__instance) as SocialGroup;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Capturing Together Bar state threw: " + ex);
            }
        }

        /// <summary>Explains the exact false-to-true transition which plays the native pop.</summary>
        private static void TogetherBarUpdatePostfix(
            UICharactersTogetherBar __instance, TogetherBarUpdateState __state)
        {
            if (!Enabled || __instance == null || _togetherBarIsFullField == null)
            {
                return;
            }

            try
            {
                bool isFull = (bool)_togetherBarIsFullField.GetValue(__instance);
                if (__state.WasFull || !isFull || !ShouldReportGroup(__state.Group))
                {
                    return;
                }

                SpeakBackground(
                    "together-bar-ready",
                    Loc.Get("scene_together_bar_ready", ContextActionHandler.ActionKey));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Explaining the Together Bar sound threw: " + ex);
            }
        }

        /// <summary>
        /// Reports the localized topic and its committed typed outcomes as one ordered result.
        /// </summary>
        public static void TogetherCardProcessedPostfix(
            bool isSuccess,
            ulong cardGUID,
            ulong characterUsingCardGUID,
            SocialGroup socialGroup,
            ulong targetGUID,
            List<ulong> charactersFromCard,
            ulong skinGUID,
            ulong initiativeReplyGUID,
            ulong requestGUID,
            TogetherCardOutcomeData __result)
        {
            if (!Enabled || !ShouldReportGroup(socialGroup))
            {
                return;
            }

            try
            {
                TogetherCard card = Settings.Get<Together>()?.GetCardByGUID(cardGUID);
                string cardName = GetTogetherCardName(card, skinGUID, requestGUID);
                string topic = null;
                if (!string.IsNullOrWhiteSpace(cardName))
                {
                    string actor = GetCharacterName(characterUsingCardGUID);
                    string target = targetGUID != 0UL ? GetCharacterName(targetGUID) : null;
                    topic = characterUsingCardGUID == 0UL
                        ? Loc.Get("scene_story_topic", cardName)
                        : (!string.IsNullOrEmpty(target)
                            ? Loc.Get("scene_conversation_topic_target", actor, cardName, target)
                            : Loc.Get("scene_conversation_topic", actor, cardName));

                    if (initiativeReplyGUID != 0UL)
                    {
                        TogetherCard replyCard = Settings.Get<Together>()?
                            .GetCardByGUID(initiativeReplyGUID);
                        string replyName = GetTogetherCardName(replyCard, 0UL, 0UL);
                        if (!string.IsNullOrWhiteSpace(replyName))
                        {
                            string reply = !string.IsNullOrEmpty(target)
                                ? Loc.Get("scene_conversation_reply", target, replyName)
                                : Loc.Get("scene_conversation_reply_chosen", replyName);
                            topic = Loc.Get(
                                "state_notification_detail", topic.TrimEnd('.', ' '), reply);
                        }
                    }

                    string visualReaction = DescribeConversationTag(socialGroup);
                    if (!string.IsNullOrEmpty(visualReaction))
                    {
                        topic = Loc.Get(
                            "state_notification_detail", topic.TrimEnd('.', ' '),
                            visualReaction);
                    }
                }

                string outcomeSummary = DescribeTogetherCardOutcomes(__result, socialGroup);
                string spoken = !string.IsNullOrWhiteSpace(topic)
                    ? (string.IsNullOrWhiteSpace(outcomeSummary)
                        ? topic
                        : Loc.Get(
                            "scene_conversation_with_results", topic.TrimEnd('.', ' '),
                            outcomeSummary))
                    : (string.IsNullOrWhiteSpace(outcomeSummary)
                        ? null
                        : Loc.Get("scene_conversation_results", outcomeSummary));
                if (string.IsNullOrWhiteSpace(spoken))
                {
                    return;
                }

                _lastCardGroupGuid = socialGroup.GUID;
                _lastCardTime = Time.unscaledTime;
                if (!string.IsNullOrWhiteSpace(outcomeSummary))
                {
                    Plugin.Log.LogInfo(
                        "[Scene/conversation-result] "
                      + ScreenReader.NormalizeForOutput(spoken));
                    AnnouncementCoordinator.PublishImportant(
                        "conversation-result", spoken);
                }
                else
                {
                    SpeakBackground("conversation-card", spoken);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Describing a conversation card threw: " + ex);
            }
        }

        /// <summary>
        /// Announces game-authored text at the exact boundary where a tutorial storyboard renders it.
        /// </summary>
        public static void StoryboardTextShownPostfix(StoryboardElement storyboardElement)
        {
            if (!Enabled || storyboardElement == null
                || storyboardElement.Type != StoryboardElementType.Text
                || !TutorialHandler.IsActive || string.IsNullOrWhiteSpace(storyboardElement.Text))
            {
                return;
            }

            try
            {
                string translationKey = "Storyboards_" + storyboardElement.Text;
                string spoken = TranslationManager.Get(translationKey);
                if (string.IsNullOrWhiteSpace(spoken) || spoken == translationKey)
                {
                    spoken = storyboardElement.Text;
                }
                spoken = ScreenReader.NormalizeForOutput(spoken?.Trim());
                if (string.IsNullOrWhiteSpace(spoken))
                {
                    return;
                }

                string identity = storyboardElement.GUID + "\n" + spoken;
                int frame = Time.frameCount;
                int previousFrame;
                TutorialStoryboardTextFrames.TryGetValue(identity, out previousFrame);
                TutorialStoryboardTextFrames[identity] = frame;
                if (previousFrame > 0 && frame >= previousFrame && frame - previousFrame <= 1)
                {
                    return;
                }

                string announcement = Loc.Get("tutorial_announcement", spoken);
                Plugin.Log.LogInfo(
                    $"[Scene/tutorial-text] guid={storyboardElement.GUID}, text='"
                  + announcement.Replace('\r', ' ').Replace('\n', ' ') + "'.");
                AnnouncementCoordinator.PublishImportant(
                    "tutorial-description", announcement);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Describing tutorial storyboard text threw: " + ex);
            }
        }

        /// <summary>
        /// Speaks a standalone visual result unless its exact object was already included in the
        /// authoritative Together-card summary.
        /// </summary>
        public static void ResultShownPostfix(OutcomeResultData resultData)
        {
            if (!Enabled || resultData == null)
            {
                return;
            }

            try
            {
                if (RemoveAggregatedTogetherCardResult(resultData))
                {
                    Plugin.Log.LogInfo(
                        $"[Scene/conversation-result] Popup already included in card summary, "
                      + $"type={resultData.ResultType}, character={resultData.CharacterGUID}.");
                    return;
                }

                string spoken = DescribeOutcome(resultData);
                if (!string.IsNullOrWhiteSpace(spoken))
                {
                    SpeakBackground("conversation-result", spoken);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Describing a visual result popup threw: " + ex);
            }
        }

        /// <summary>Reports structural movement which the path itself identifies unambiguously.</summary>
        public static void PathNodeStartedPostfix(AssetCharacter characterAsset)
        {
            if (!Enabled || characterAsset?.Data?.PathfindingData == null
                || !IsObservedCharacter(characterAsset.GUID))
            {
                return;
            }

            try
            {
                CharacterPathfindingData path = characterAsset.Data.PathfindingData;
                if (path.CurrentNodesSegmentIndex < 0
                    || path.CurrentNodesSegmentIndex >= path.PathNodesSegments.Count)
                {
                    return;
                }

                PathNodesSegment segment = path.PathNodesSegments[path.CurrentNodesSegmentIndex];
                if (segment?.Nodes == null || path.CurrentPoint < 0
                    || path.CurrentPoint >= segment.Nodes.Count)
                {
                    return;
                }

                PathNode node = segment.Nodes[path.CurrentPoint];
                if (node?.Link == null)
                {
                    return;
                }

                string subject = GetCharacterName(characterAsset.GUID);
                string spoken = null;
                if (node.Link.NavMeshLinkType == NavMeshLinkType.Stairs)
                {
                    bool goingDown;
                    if (path.CurrentStepSize
                        == CharacterPathfindingData.StepSize.GoingDownStairs)
                    {
                        goingDown = true;
                    }
                    else if (path.CurrentStepSize
                        == CharacterPathfindingData.StepSize.GoingUpStairs)
                    {
                        goingDown = false;
                    }
                    else
                    {
                        return;
                    }

                    AreaIdentity area = GetAreaIdentity(characterAsset);
                    bool indoors = area.RoomInstanceId != 0;
                    Vector3 delta = node.Position - _lastStairPosition;
                    float horizontalDistanceSquared = delta.x * delta.x + delta.z * delta.z;
                    bool sameSequence = _hasStairSequence
                        && _lastStairCharacterGuid == characterAsset.GUID
                        && _lastStairInteractionGuid == path.InteractionGUID
                        && _lastStairWasDown == goingDown
                        && _lastStairWasIndoors == indoors
                        && Time.unscaledTime - _lastStairTime <= StairSequenceGapSeconds
                        && horizontalDistanceSquared <= StairSequenceDistanceSquared;

                    _hasStairSequence = true;
                    _lastStairCharacterGuid = characterAsset.GUID;
                    _lastStairInteractionGuid = path.InteractionGUID;
                    _lastStairWasDown = goingDown;
                    _lastStairWasIndoors = indoors;
                    _lastStairTime = Time.unscaledTime;
                    _lastStairPosition = node.Position;

                    if (sameSequence)
                    {
                        Plugin.Log.LogInfo(
                            $"[Scene/path-link] Coalesced adjacent stair link for "
                          + $"character={characterAsset.GUID}, interaction={path.InteractionGUID}, "
                          + $"direction={(goingDown ? "down" : "up")}, indoors={indoors}, "
                          + $"distance={Mathf.Sqrt(horizontalDistanceSquared):0.0}.");
                        return;
                    }

                    // "Upstairs/downstairs" implies a building floor. Outdoor stair links use
                    // spatial wording, read from the character's authoritative room association.
                    spoken = goingDown
                        ? (indoors
                            ? Loc.Get("scene_goes_downstairs", subject)
                            : Loc.Get("scene_descends_stairs", subject))
                        : (indoors
                            ? Loc.Get("scene_goes_upstairs", subject)
                            : Loc.Get("scene_ascends_stairs", subject));
                }
                else if (node.Link.NavMeshLinkType == NavMeshLinkType.Ladder)
                {
                    spoken = path.PreviousPoint.y > node.Position.y
                        ? Loc.Get("scene_climbs_down", subject)
                        : Loc.Get("scene_climbs_up", subject);
                }

                if (!string.IsNullOrEmpty(spoken))
                {
                    SpeakBackground("path-link", spoken);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Describing a followed Para's path transition threw: " + ex);
            }
        }

        /// <summary>Polls only low-frequency model state not exposed by a reliable event method.</summary>
        public static void Tick()
        {
            if (!Enabled)
            {
                return;
            }

            try
            {
                if (GameLoadingManager.State != State.Game)
                {
                    ResetContinuousState();
                    return;
                }

                AssetCharacter character = GetObservedCharacter();
                if (character?.Data == null)
                {
                    ResetContinuousState();
                    return;
                }

                if (_observedCharacterGuid != character.GUID)
                {
                    ResetContinuousState();
                    _observedCharacterGuid = character.GUID;
                }

                if (Time.unscaledTime < _nextPollTime)
                {
                    return;
                }
                _nextPollTime = Time.unscaledTime + PollIntervalSeconds;

                if (!_hasBaseline)
                {
                    CaptureBaseline(character);
                    _hasBaseline = true;
                    return;
                }

                ObserveEmotions(character);
                ObserveConversation(character);
                ObserveActivities(character, speakChanges: true);
                ObserveArea(character);
            }
            catch (Exception ex)
            {
                Enabled = false;
                ResetContinuousState();
                Plugin.Log.LogError("Scene narration disabled after an exception: " + ex);
                ScreenReader.Speak(Loc.Get("scene_narration_failed"));
            }
        }

        private static void CaptureBaseline(AssetCharacter character)
        {
            CaptureEmotionSnapshots(character);
            CaptureConversation(character);
            ObserveActivities(character, speakChanges: false);
            _stableArea = GetAreaIdentity(character);
            _candidateArea = _stableArea;
            _candidateAreaSince = Time.unscaledTime;
            _candidateBeganWhileMoving = false;
            Plugin.Log.LogInfo(
                $"[Scene] baseline character={character.GUID}, "
              + $"name='{GetCharacterName(character.GUID)}', follow="
              + $"{GetPlayer()?.CameraCurrentCharacterFollowTarget ?? 0UL}, "
              + $"room={_stableArea.RoomInstanceId}, layer={_stableArea.Layer}, "
              + $"conversation={_conversationGroupGuid}.");
        }

        private static void ResetContinuousState()
        {
            _observedCharacterGuid = 0UL;
            _nextPollTime = 0f;
            _hasBaseline = false;
            EmotionSnapshots.Clear();
            Activities.Clear();
            _conversationGroupGuid = 0UL;
            _conversationTalkerGuid = 0UL;
            _conversationListenerGuid = 0UL;
            _conversationTagGuid = 0UL;
            _lastCardGroupGuid = 0UL;
            _lastCardTime = 0f;
            _stableArea = default(AreaIdentity);
            _candidateArea = default(AreaIdentity);
            _candidateAreaSince = 0f;
            _candidateBeganWhileMoving = false;
            _hasStairSequence = false;
            _lastStairCharacterGuid = 0UL;
            _lastStairInteractionGuid = 0UL;
            _lastStairWasDown = false;
            _lastStairWasIndoors = false;
            _lastStairTime = 0f;
            _lastStairPosition = default(Vector3);
        }

        private static void CaptureEmotionSnapshots(AssetCharacter character)
        {
            EmotionSnapshots.Clear();
            Emotions setting = Settings.Get<Emotions>();
            if (setting?.AllEmotions == null)
            {
                return;
            }

            foreach (Emotion emotion in setting.AllEmotions)
            {
                if (emotion != null)
                {
                    EmotionSnapshots[emotion.GUID] = GetEmotionSnapshot(character, emotion);
                }
            }
        }

        private static void ObserveEmotions(AssetCharacter character)
        {
            Emotions setting = Settings.Get<Emotions>();
            if (setting?.AllEmotions == null)
            {
                return;
            }

            var messages = new List<string>();
            foreach (Emotion emotion in setting.AllEmotions)
            {
                if (emotion == null)
                {
                    continue;
                }

                EmotionSnapshot before;
                if (!EmotionSnapshots.TryGetValue(emotion.GUID, out before))
                {
                    before = new EmotionSnapshot();
                }
                EmotionSnapshot after = GetEmotionSnapshot(character, emotion);
                EmotionSnapshots[emotion.GUID] = after;
                if (before.Value == after.Value)
                {
                    continue;
                }

                List<EmotionContribution> added = FindAddedContributions(
                    before.Contributions, after.Contributions);
                List<EmotionContribution> removed = FindAddedContributions(
                    after.Contributions, before.Contributions);
                bool onlyTogetherCardAdditions = added.Count > 0 && removed.Count == 0
                    && AllFromTogetherCard(added);
                string emotionName = TranslateSettingName("Emotion_", emotion.DisplayName);
                bool reasonEnded;
                string reason = GetEmotionChangeReason(
                    added, removed, after.Value > before.Value, out reasonEnded);

                Plugin.Log.LogInfo(
                    $"[Scene/emotion] character={character.GUID}, emotion={emotion.GUID}, "
                  + $"name='{emotionName}', value={before.Value}->{after.Value}, "
                  + $"reason='{reason}', reasonEnded={reasonEnded}, "
                  + $"cardSummaryWillReport={onlyTogetherCardAdditions}.");

                // Together-card changes are included in the authoritative ProcessOutcomes summary.
                // Reporting the same delta from polling would turn one change into duplicate speech.
                // Brain/story/status effects have no equivalent summary and remain audible.
                if (onlyTogetherCardAdditions)
                {
                    continue;
                }

                string subject = GetCharacterName(character.GUID);
                if (after.Value > before.Value)
                {
                    messages.Add(string.IsNullOrEmpty(reason)
                        ? Loc.Get(
                            "scene_emotion_increased", subject, emotionName,
                            before.Value, after.Value)
                        : Loc.Get(
                            reasonEnded
                                ? "scene_emotion_increased_ended_reason"
                                : "scene_emotion_increased_reason",
                            subject, emotionName,
                            before.Value, after.Value, reason));
                }
                else if (after.Value == 0)
                {
                    messages.Add(string.IsNullOrEmpty(reason)
                        ? Loc.Get("scene_emotion_ended", subject, emotionName)
                        : Loc.Get(
                            reasonEnded
                                ? "scene_emotion_ended_reason"
                                : "scene_emotion_ended_cause",
                            subject, emotionName, reason));
                }
                else
                {
                    messages.Add(string.IsNullOrEmpty(reason)
                        ? Loc.Get(
                            "scene_emotion_decreased", subject, emotionName,
                            before.Value, after.Value)
                        : Loc.Get(
                            reasonEnded
                                ? "scene_emotion_decreased_reason"
                                : "scene_emotion_decreased_cause",
                            subject, emotionName,
                            before.Value, after.Value, reason));
                }
            }

            if (messages.Count > 0)
            {
                string spoken = string.Join(" ", messages.ToArray());
                Plugin.Log.LogInfo("[Scene/emotion-change] " + spoken);
                AnnouncementCoordinator.PublishImportant("emotion-change", spoken);
            }
        }

        private static EmotionSnapshot GetEmotionSnapshot(
            AssetCharacter character, Emotion emotion)
        {
            var snapshot = new EmotionSnapshot
            {
                Value = EmotionManager.Instance.GetEmotionValue(character, emotion.GUID)
            };

            List<StatusEffectReturnValues> values = StatusEffectManager.Instance
                .GetStatusEffectLogicsAndDurationsByEffectType(
                    StatusEffectEffectTypes.Emotion, emotion.GUID, character);
            foreach (StatusEffectReturnValues value in values)
            {
                snapshot.Contributions.Add(new EmotionContribution
                {
                    StatusEffectGuid = value.StatusEffect,
                    BrainLogicGuid = value.BrainLogic,
                    TogetherCardGuid = value.TogetherCard,
                    OtherCharacterGuid = value.OtherCharacter,
                    StoryCardGuid = value.StoryCard,
                    Value = value.Value,
                    Reason = DescribeEmotionReason(value)
                });
            }
            return snapshot;
        }

        private static List<EmotionContribution> FindAddedContributions(
            List<EmotionContribution> oldValues, List<EmotionContribution> newValues)
        {
            var result = new List<EmotionContribution>();
            var used = new bool[oldValues.Count];
            foreach (EmotionContribution value in newValues)
            {
                bool matched = false;
                for (int i = 0; i < oldValues.Count; i++)
                {
                    if (!used[i] && value.Matches(oldValues[i]))
                    {
                        used[i] = true;
                        matched = true;
                        break;
                    }
                }
                if (!matched)
                {
                    result.Add(value);
                }
            }
            return result;
        }

        private static bool AllFromTogetherCard(List<EmotionContribution> values)
        {
            foreach (EmotionContribution value in values)
            {
                if (!value.IsFromTogetherCard)
                {
                    return false;
                }
            }
            return true;
        }

        private static string GetFirstReason(List<EmotionContribution> values)
        {
            foreach (EmotionContribution value in values)
            {
                if (!string.IsNullOrWhiteSpace(value.Reason))
                {
                    return value.Reason;
                }
            }
            return null;
        }

        private static string GetEmotionChangeReason(
            List<EmotionContribution> added,
            List<EmotionContribution> removed,
            bool increased,
            out bool reasonEnded)
        {
            // A contribution can itself be negative. Pick provenance by the direction in which
            // adding or removing that value moves the total, rather than assuming every increase
            // is an addition and every decrease an expiry.
            if (increased)
            {
                string reason = GetFirstReasonWithSign(added, positive: true);
                if (!string.IsNullOrWhiteSpace(reason))
                {
                    reasonEnded = false;
                    return reason;
                }

                reason = GetFirstReasonWithSign(removed, positive: false);
                if (!string.IsNullOrWhiteSpace(reason))
                {
                    reasonEnded = true;
                    return reason;
                }
            }
            else
            {
                string reason = GetFirstReasonWithSign(removed, positive: true);
                if (!string.IsNullOrWhiteSpace(reason))
                {
                    reasonEnded = true;
                    return reason;
                }

                reason = GetFirstReasonWithSign(added, positive: false);
                if (!string.IsNullOrWhiteSpace(reason))
                {
                    reasonEnded = false;
                    return reason;
                }
            }

            string fallback = GetFirstReason(increased ? added : removed);
            reasonEnded = !increased;
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                return fallback;
            }

            fallback = GetFirstReason(increased ? removed : added);
            reasonEnded = increased;
            return fallback;
        }

        private static string GetFirstReasonWithSign(
            List<EmotionContribution> values, bool positive)
        {
            foreach (EmotionContribution value in values)
            {
                bool signMatches = positive ? value.Value > 0f : value.Value < 0f;
                if (signMatches && !string.IsNullOrWhiteSpace(value.Reason))
                {
                    return value.Reason;
                }
            }
            return null;
        }

        private static string DescribeEmotionReason(StatusEffectReturnValues value)
        {
            if (value.BrainLogic != 0UL)
            {
                BrainTriggerAndLogic logic = Settings.Get<BrainLogic>()
                    ?.GetBrainTriggerAndLogicByGUID(value.BrainLogic);
                string reason = logic != null
                    ? TranslateSettingName("BrainLogic_", logic.DisplayName)
                    : null;
                if (!string.IsNullOrWhiteSpace(reason))
                {
                    return reason;
                }
            }

            if (value.TogetherCard != 0UL)
            {
                TogetherCard card = Settings.Get<Together>()?.GetCardByGUID(value.TogetherCard);
                string reason = card != null
                    ? TranslateSettingName("TogetherCard_", card.DisplayName)
                    : null;
                if (!string.IsNullOrWhiteSpace(reason) && value.OtherCharacter != 0UL)
                {
                    return Loc.Get(
                        "scene_reason_with_para", reason,
                        GetCharacterName(value.OtherCharacter));
                }
                if (!string.IsNullOrWhiteSpace(reason))
                {
                    return reason;
                }
            }

            if (value.StoryCard != 0UL)
            {
                StoryCard story = Settings.Get<StoryCards>()?.GetStoryCardByGUID(value.StoryCard);
                string reason = story != null
                    ? TranslateSettingName("StoryCard_", story.DisplayName)
                    : null;
                if (!string.IsNullOrWhiteSpace(reason))
                {
                    return reason;
                }
            }

            StatusEffect status = Settings.Get<StatusEffects>()
                ?.GetStatusEffectByGUID(value.StatusEffect);
            return status != null
                ? TranslateSettingName("StatusEffect_", status.DisplayName)
                : null;
        }

        private static void CaptureConversation(AssetCharacter character)
        {
            SocialGroup group = FindConversationGroup(character.GUID);
            _conversationGroupGuid = group?.GUID ?? 0UL;
            _conversationTalkerGuid = group?.TalkerCharacter ?? 0UL;
            _conversationListenerGuid = group?.MainListenerCharacter ?? 0UL;
            _conversationTagGuid = group?.CurrentConversationTag ?? 0UL;
        }

        private static void ObserveConversation(AssetCharacter character)
        {
            SocialGroup group = FindConversationGroup(character.GUID);
            if (group == null)
            {
                _conversationGroupGuid = 0UL;
                _conversationTalkerGuid = 0UL;
                _conversationListenerGuid = 0UL;
                _conversationTagGuid = 0UL;
                return;
            }

            if (_conversationGroupGuid != group.GUID)
            {
                _conversationGroupGuid = group.GUID;
                _conversationTalkerGuid = group.TalkerCharacter;
                _conversationListenerGuid = group.MainListenerCharacter;
                _conversationTagGuid = group.CurrentConversationTag;

                CameraFollowHandler.LogViewportSnapshot(
                    "conversation-started:" + group.GUID,
                    character,
                    group);

                string others = JoinOtherCharacterNames(group, character.GUID);
                string speaker = GetCharacterName(group.TalkerCharacter);
                string spoken = Loc.Get(
                    "scene_conversation_started", GetCharacterName(character.GUID), others,
                    speaker);
                SpeakBackground("conversation-started", spoken);
                return;
            }

            bool speakerChanged = _conversationTalkerGuid != group.TalkerCharacter
                && group.TalkerCharacter != 0UL;
            _conversationTalkerGuid = group.TalkerCharacter;
            _conversationListenerGuid = group.MainListenerCharacter;
            _conversationTagGuid = group.CurrentConversationTag;
            bool wasJustSetByReportedCard = _lastCardGroupGuid == group.GUID
                && Time.unscaledTime - _lastCardTime < 0.75f;
            if (speakerChanged && !wasJustSetByReportedCard)
            {
                SpeakBackground(
                    "conversation-speaker",
                    Loc.Get(
                        "scene_conversation_speaker",
                        GetCharacterName(group.TalkerCharacter)));
            }
        }

        private static SocialGroup FindConversationGroup(ulong characterGuid)
        {
            if (characterGuid == 0UL || SavedGameManager.Instance?.CurrentSavedGame?.Data?
                .SocialGroupData?.SocialGroups == null)
            {
                return null;
            }

            foreach (SocialGroup group in SavedGameManager.Instance.CurrentSavedGame.Data
                .SocialGroupData.SocialGroups)
            {
                if (group?.CharactersInGroup != null
                    && group.CharactersInGroup.Count > 1
                    && group.CurrentConversationTag != 0UL
                    && group.CharactersInGroup.Contains(characterGuid))
                {
                    return group;
                }
            }
            return null;
        }

        private static string JoinOtherCharacterNames(SocialGroup group, ulong observedGuid)
        {
            var names = new List<string>();
            if (group?.CharactersInGroup != null)
            {
                foreach (ulong guid in group.CharactersInGroup)
                {
                    if (guid != observedGuid)
                    {
                        names.Add(GetCharacterName(guid));
                    }
                }
            }
            return names.Count == 0
                ? Loc.Get("state_unknown_para")
                : string.Join(Loc.Get("list_separator"), names.ToArray());
        }

        private static void ObserveActivities(AssetCharacter character, bool speakChanges)
        {
            ActivityRemovalBuffer.Clear();
            foreach (ulong guid in Activities.Keys)
            {
                ActivityRemovalBuffer.Add(guid);
            }

            List<AssetCharacterDataInteraction> queue = character.Data.CurrentInteractionsInQueue;
            if (queue == null)
            {
                Activities.Clear();
                return;
            }

            foreach (AssetCharacterDataInteraction interaction in queue)
            {
                if (interaction == null
                    || interaction.State != AssetCharacterDataInteractionState.Running
                    || interaction.CurrentActionList == null
                    || interaction.CurrentActionListIndex < 0
                    || interaction.CurrentActionListIndex >= interaction.CurrentActionList.Count)
                {
                    continue;
                }

                CurrentAction current = interaction.CurrentActionList[
                    interaction.CurrentActionListIndex];
                ActionUnit action = Settings.Get<Actions>()?.GetActionByGUID(
                    current.ActionSettingGUID);
                AnimationUnit animation = interaction.AnimationContainerPlayingData?
                    .CurrentlyPlayingAnimation;
                ulong animationGuid = animation?.GUID ?? 0UL;

                ActivityObservation before;
                bool existed = Activities.TryGetValue(interaction.GUID, out before);
                bool actionChanged = !existed
                    || before.ActionGuid != current.ActionSettingGUID;
                bool changed = actionChanged
                    || before.AnimationGuid != animationGuid;
                Activities[interaction.GUID] = new ActivityObservation
                {
                    ActionGuid = current.ActionSettingGUID,
                    AnimationGuid = animationGuid
                };
                ActivityRemovalBuffer.Remove(interaction.GUID);
                if (!changed)
                {
                    continue;
                }

                InteractionUnit interactionSetting = Settings.Get<Interactions>()
                    ?.GetInteractionByGUID(interaction.InteractionSettingGUID);
                Plugin.Log.LogInfo(
                    $"[Scene/activity] character={character.GUID}, queue={interaction.GUID}, "
                  + $"interaction={interaction.InteractionSettingGUID} "
                  + $"'{GetInteractionName(interactionSetting)}', "
                  + $"action={current.ActionSettingGUID} '{action?.DisplayName}', "
                  + $"animation={animationGuid} '{animation?.DisplayName}', "
                  + $"locomotion={interaction.IsLocomotionRunning}.");

                CameraFollowHandler.LogViewportSnapshot(
                    $"activity:{interaction.InteractionSettingGUID}/"
                  + $"{current.ActionSettingGUID}/{animationGuid}",
                    character,
                    FindConversationGroup(character.GUID));

                if (speakChanges && actionChanged && action != null)
                {
                    string phase = DescribeActivityPhase(
                        GetCharacterName(character.GUID), action.DisplayName);
                    if (!string.IsNullOrEmpty(phase))
                    {
                        SpeakBackground("activity-phase", phase);
                    }
                }
            }

            foreach (ulong guid in ActivityRemovalBuffer)
            {
                Activities.Remove(guid);
            }
        }

        private static string DescribeActivityPhase(string subject, string actionName)
        {
            switch (actionName)
            {
                case "GetPhone":
                    return Loc.Get("scene_takes_out_phone", subject);
                case "PlayingOnPhoneOneHour":
                    return Loc.Get("scene_plays_phone_game", subject);
                case "TypingOnPhone30Minutes":
                    return Loc.Get("scene_types_on_phone", subject);
                case "PutAwayPhone":
                    return Loc.Get("scene_puts_away_phone", subject);
                default:
                    return null;
            }
        }

        private static void ObserveArea(AssetCharacter character)
        {
            AreaIdentity current = GetAreaIdentity(character);
            if (!current.Equals(_candidateArea))
            {
                _candidateArea = current;
                _candidateAreaSince = Time.unscaledTime;
                _candidateBeganWhileMoving = IsMoving(character);
                return;
            }

            if (current.Equals(_stableArea)
                || Time.unscaledTime - _candidateAreaSince < AreaSettleSeconds)
            {
                return;
            }

            AreaIdentity before = _stableArea;
            _stableArea = current;
            if (!_candidateBeganWhileMoving || before.LotGuid == 0UL
                || current.LotGuid == 0UL || before.LotGuid != current.LotGuid)
            {
                return;
            }

            string subject = GetCharacterName(character.GUID);
            string spoken;
            if (current.RoomInstanceId == 0)
            {
                spoken = Loc.Get("scene_steps_outdoors", subject);
            }
            else
            {
                string areaName;
                string contents;
                bool described = LotObjectHandler.TryDescribeParaAreaForNarration(
                    character, out areaName, out contents);
                if (!described)
                {
                    areaName = Loc.Get("scene_room_on_floor", current.Layer);
                }
                else
                {
                    areaName = Loc.Get(
                        "explorer_path", Loc.Get("explorer_floor", current.Layer), areaName);
                }
                spoken = string.IsNullOrWhiteSpace(contents)
                    ? Loc.Get("scene_enters_area", subject, areaName)
                    : Loc.Get("scene_enters_area_contents", subject, areaName, contents);
            }

            Plugin.Log.LogInfo(
                $"[Scene/area] character={character.GUID}, lot={before.LotGuid}, "
              + $"room={before.RoomInstanceId}->{current.RoomInstanceId}, "
              + $"layer={before.Layer}->{current.Layer}, spoken='{spoken}'.");
            SpeakBackground("area-change", spoken);
        }

        private static AreaIdentity GetAreaIdentity(AssetCharacter character)
        {
            ZoneWallObject room = character?.Data?.OnZoneWallObject;
            ulong lotGuid = character?.Data?.OnLotPerimeterZone?.RealLotGUID ?? 0UL;
            if (lotGuid == 0UL && room != null)
            {
                lotGuid = room.LotPlacedOnGUID;
            }
            return new AreaIdentity
            {
                LotGuid = lotGuid,
                RoomInstanceId = room?.InstanceID ?? 0,
                Layer = room?.Layer ?? 0
            };
        }

        private static bool IsMoving(AssetCharacter character)
        {
            if (character?.Data?.PathfindingData != null)
            {
                return true;
            }
            if (character?.Data?.CurrentInteractionsInQueue == null)
            {
                return false;
            }
            foreach (AssetCharacterDataInteraction interaction
                in character.Data.CurrentInteractionsInQueue)
            {
                if (interaction?.IsLocomotionRunning == true)
                {
                    return true;
                }
            }
            return false;
        }

        private static string DescribeConversationTag(SocialGroup group)
        {
            if (group == null || group.CurrentConversationTag == 0UL)
            {
                return null;
            }

            Together together = Settings.Get<Together>();
            if (together == null || group.CurrentConversationTag == together.DefaultConversationTag)
            {
                return null;
            }

            ConversationTag tag = together.GetConversationTagByGUID(
                group.CurrentConversationTag);
            string talker = GetCharacterName(group.TalkerCharacter);
            string listener = GetCharacterName(group.MainListenerCharacter);
            switch (tag?.DisplayName)
            {
                case "TellJokeSuccess":
                    return Loc.Get("scene_tag_joke_success", talker, listener);
                case "TellJokeFail":
                    return Loc.Get("scene_tag_joke_failed", talker, listener);
                case "FlirtSuccess":
                    return Loc.Get("scene_tag_flirt_success", talker, listener);
                case "FlirtFail":
                    return Loc.Get("scene_tag_flirt_failed", talker, listener);
                case "AskQuestion":
                    return Loc.Get("scene_tag_question", talker, listener);
                case "AwkwardDiscussion":
                    return Loc.Get("scene_tag_awkward");
                case "AngryDiscussion":
                    return Loc.Get("scene_tag_angry");
                case "MeanDiscussion":
                    return Loc.Get("scene_tag_mean");
                case "NeutralDiscussion":
                    return Loc.Get("scene_tag_neutral");
                case "YellDiscussion":
                    return Loc.Get("scene_tag_yelling", talker);
                case "Introduction":
                    return Loc.Get("scene_tag_introduction", talker, listener);
                case "ComplainDiscussion":
                    return Loc.Get("scene_tag_complaining", talker);
                case "MischieviousDiscussion":
                    return Loc.Get("scene_tag_mischievous");
                case "MotivationalDiscussion":
                    return Loc.Get("scene_tag_motivational");
                case "PraiseDiscussion":
                    return Loc.Get("scene_tag_praise", talker, listener);
                case "FunnyDiscussion":
                    return Loc.Get("scene_tag_funny");
                case "ShareTipsDiscussion":
                    return Loc.Get("scene_tag_tips", talker, listener);
                case "StartledDiscussion":
                    return Loc.Get("scene_tag_startled", listener);
                case "BragDiscussion":
                    return Loc.Get("scene_tag_bragging", talker);
                case "SadDiscussion":
                    return Loc.Get("scene_tag_sad");
                case "MeaningfulDiscussion":
                    return Loc.Get("scene_tag_meaningful");
                case "FlirtyDiscussion":
                    return Loc.Get("scene_tag_flirty");
                default:
                    return null;
            }
        }

        private static string DescribeTogetherCardOutcomes(
            TogetherCardOutcomeData outcome, SocialGroup socialGroup)
        {
            AggregatedTogetherCardResults.Clear();
            if (outcome?.PerCharacterOutcomes == null
                || socialGroup?.CharactersInGroup == null)
            {
                return null;
            }

            var characterSummaries = new List<string>();
            var seenCharacters = new HashSet<ulong>();
            List<ulong> householdCharacters =
                HouseholdManager.Instance?.CurrentHousehold?.Data?.Characters;

            // UICharacters renders household members first and NPCs second, retaining the social
            // group's order within each section. Match that instead of Dictionary enumeration.
            for (int section = 0; section < 2; section++)
            {
                bool wantHouseholdMembers = section == 0;
                foreach (ulong characterGuid in socialGroup.CharactersInGroup)
                {
                    bool isHouseholdMember = householdCharacters != null
                        && householdCharacters.Contains(characterGuid);
                    if (isHouseholdMember != wantHouseholdMembers
                        || !seenCharacters.Add(characterGuid))
                    {
                        continue;
                    }

                    List<OutcomeResultData> results;
                    if (!outcome.PerCharacterOutcomes.TryGetValue(characterGuid, out results)
                        || results == null)
                    {
                        continue;
                    }

                    var details = new List<string>();
                    foreach (OutcomeResultData result in results)
                    {
                        if (result == null)
                        {
                            continue;
                        }

                        string detail = DescribeOutcome(result, detailOnly: true);
                        if (string.IsNullOrWhiteSpace(detail))
                        {
                            continue;
                        }

                        details.Add(detail.TrimEnd('.', ' '));
                        AggregatedTogetherCardResults.Add(result);
                    }

                    if (details.Count > 0)
                    {
                        characterSummaries.Add(Loc.Get(
                            "scene_result_group", GetCharacterName(characterGuid),
                            string.Join("; ", details.ToArray())));
                    }
                }
            }

            return characterSummaries.Count > 0
                ? string.Join(". ", characterSummaries.ToArray())
                : null;
        }

        private static bool RemoveAggregatedTogetherCardResult(OutcomeResultData result)
        {
            for (int i = 0; i < AggregatedTogetherCardResults.Count; i++)
            {
                if (ReferenceEquals(AggregatedTogetherCardResults[i], result))
                {
                    AggregatedTogetherCardResults.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        private static string DescribeOutcome(
            OutcomeResultData data, bool detailOnly = false)
        {
            string subject = data.CharacterGUID != 0UL
                ? GetCharacterName(data.CharacterGUID)
                : Loc.Get("scene_conversation_result");
            switch (data.ResultType)
            {
                case OutcomeResultDataTypes.Emotion:
                    Emotion emotion = Settings.Get<Emotions>()
                        ?.GetEmotionByGUID(data.EmotionGUID);
                    string emotionName = emotion != null
                        ? TranslateSettingName("Emotion_", emotion.DisplayName)
                        : Loc.Get("focus_unlabeled");
                    string emotionValue = FormatSigned(data.EmotionValue);
                    return Loc.Get(
                        detailOnly ? "scene_result_emotion_detail" : "scene_result_emotion",
                        detailOnly ? emotionName : subject,
                        detailOnly ? emotionValue : emotionName,
                        emotionValue);

                case OutcomeResultDataTypes.GetToKnow:
                    string learned = ScreenReader.NormalizeForOutput(data.GetToKnowData);
                    return Loc.Get(
                        detailOnly ? "scene_result_learned_detail" : "scene_result_learned",
                        detailOnly ? learned : subject,
                        learned);

                case OutcomeResultDataTypes.RelationshipLabel:
                    RelationshipLabel label = Settings.Get<RelationshipLabels>()
                        ?.GetLabelByGUID(data.RelationshipLabelGUID);
                    string labelName = label != null
                        ? TranslationManager.Get(
                            "RelationshipLabel_" + label.DisplayName,
                            PersonalityManager.Instance.GetGenderFromPronouns(
                                ResolveCharacter(data.CharacterGUID)))
                        : Loc.Get("focus_unlabeled");
                    string relationshipLevels = FormatSigned(data.RelationshipLabelLevels);
                    return Loc.Get(
                        detailOnly
                            ? "scene_result_relationship_detail"
                            : "scene_result_relationship",
                        detailOnly ? labelName : subject,
                        detailOnly ? relationshipLevels : labelName,
                        relationshipLevels);

                case OutcomeResultDataTypes.SkillExperience:
                    Skill skill = Settings.Get<Skills>()?.GetSkillByGUID(data.SkillGUID);
                    string skillName = skill != null
                        ? TranslateSettingName("SkillName_", skill.DisplayName)
                        : Loc.Get("focus_unlabeled");
                    string skillExperience = FormatSigned(data.SkillExperienceGained);
                    return Loc.Get(
                        detailOnly ? "scene_result_skill_detail" : "scene_result_skill",
                        detailOnly ? skillName : subject,
                        detailOnly ? skillExperience : skillName,
                        skillExperience);

                case OutcomeResultDataTypes.StatusEffect:
                    StatusEffect status = Settings.Get<StatusEffects>()
                        ?.GetStatusEffectByGUID(data.StatusEffectGUID);
                    string statusName = status != null
                        ? TranslateSettingName("StatusEffect_", status.DisplayName)
                        : Loc.Get("focus_unlabeled");
                    return Loc.Get(
                        detailOnly ? "scene_result_status_detail" : "scene_result_status",
                        detailOnly ? statusName : subject,
                        statusName);

                case OutcomeResultDataTypes.SuccessChance:
                    return Loc.Get(data.IsSuccess
                        ? (detailOnly
                            ? "scene_result_success_detail"
                            : "scene_result_success")
                        : (detailOnly
                            ? "scene_result_failure_detail"
                            : "scene_result_failure"));

                case OutcomeResultDataTypes.NeedRelieved:
                    Need need = Settings.Get<Needs>()?.GetNeedByGUID(data.NeedGUID);
                    string needName = need != null
                        ? TranslateSettingName("Need_", need.DisplayName)
                        : Loc.Get("focus_unlabeled");
                    return Loc.Get(
                        detailOnly ? "scene_result_need_detail" : "scene_result_need",
                        detailOnly ? needName : subject,
                        needName);

                case OutcomeResultDataTypes.WantGained:
                    Want want = Settings.Get<Wants>()?.GetWantByGUID(data.WantGUID);
                    string wantName = want != null
                        ? TranslateSettingName("Want_", want.DisplayName)
                        : Loc.Get("focus_unlabeled");
                    return Loc.Get(
                        detailOnly ? "scene_result_want_detail" : "scene_result_want",
                        detailOnly ? wantName : subject,
                        wantName);

                case OutcomeResultDataTypes.MoneyChanged:
                    string money = ScreenReader.NormalizeForOutput(
                        FormatService.GetMoney(data.MoneyAmount));
                    return Loc.Get(
                        detailOnly ? "scene_result_money_detail" : "scene_result_money",
                        detailOnly ? money : subject,
                        money);

                case OutcomeResultDataTypes.ItemAdded:
                case OutcomeResultDataTypes.ItemRemoved:
                    CatalogueItem item = Settings.Get<Items>()?.GetItemByGUID(data.ItemGUID);
                    string itemName = item != null
                        ? ItemDescription.GetName(item, data.ItemSkinGUID)
                        : null;
                    return Loc.Get(
                        data.ResultType == OutcomeResultDataTypes.ItemAdded
                            ? (detailOnly
                                ? "scene_result_item_added_detail"
                                : "scene_result_item_added")
                            : (detailOnly
                                ? "scene_result_item_removed_detail"
                                : "scene_result_item_removed"),
                        detailOnly
                            ? itemName ?? Loc.Get("item_unknown")
                            : subject,
                        itemName ?? Loc.Get("item_unknown"));

                default:
                    return null;
            }
        }

        private static string GetTogetherCardName(
            TogetherCard card, ulong skinGuid, ulong requestGuid)
        {
            if (card == null)
            {
                return null;
            }

            string key = "TogetherCard_" + card.DisplayName;
            string translated;
            if (card.InjectRequestIntoCard && requestGuid != 0UL)
            {
                Goal goal = Settings.Get<Goals>()?.GetGoalByGUID(requestGuid);
                string request = goal != null
                    ? TranslateSettingName("Goal_", goal.DisplayName)
                    : null;
                translated = string.IsNullOrEmpty(request)
                    ? TranslationManager.Get(key)
                    : TranslationManager.Get(key, request);
            }
            else if (card.InjectSkinIntoCard && skinGuid != 0UL)
            {
                Skins skins = Settings.Get<Skins>();
                Skin skin = skins?.GetSkinByGUID(skinGuid);
                SkinProperty property = skin?.GetProperty(
                    skins.SkinSelectionUITitlePropertyTag);
                string skinName = property != null
                    ? TranslateSettingName("Skin_", property.StringValue)
                    : null;
                translated = string.IsNullOrEmpty(skinName)
                    ? TranslationManager.Get(key)
                    : TranslationManager.Get(key, skinName);
            }
            else
            {
                translated = TranslationManager.Get(key);
            }
            return string.IsNullOrWhiteSpace(translated) || translated == key
                ? card.DisplayName
                : ScreenReader.NormalizeForOutput(translated.Trim());
        }

        private static string GetInteractionName(InteractionUnit interaction)
        {
            if (interaction == null)
            {
                return null;
            }
            string translated = TranslationManager.Get(interaction.TranslationKey);
            return string.IsNullOrWhiteSpace(translated)
                || translated == interaction.TranslationKey
                    ? interaction.DisplayName
                    : ScreenReader.NormalizeForOutput(translated);
        }

        private static string TranslateSettingName(string prefix, string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName))
            {
                return null;
            }
            string key = prefix + displayName;
            string translated = TranslationManager.Get(key);
            return string.IsNullOrWhiteSpace(translated) || translated == key
                ? displayName
                : ScreenReader.NormalizeForOutput(translated.Trim());
        }

        private static string FormatSigned(float value)
        {
            string number = value.ToString("0.##", CultureInfo.CurrentCulture);
            return value > 0f ? "+" + number : number;
        }

        private static bool ShouldReportGroup(SocialGroup group)
        {
            if (group?.CharactersInGroup == null)
            {
                return false;
            }
            AssetCharacter observed = GetObservedCharacter();
            return observed != null && group.CharactersInGroup.Contains(observed.GUID);
        }

        private static bool IsObservedCharacter(ulong guid)
        {
            AssetCharacter observed = GetObservedCharacter();
            return observed != null && observed.GUID == guid;
        }

        private static AssetCharacter GetObservedCharacter()
        {
            Player player = GetPlayer();
            if (player == null || CharacterManager.Instance == null)
            {
                return null;
            }

            ulong guid = player.CameraCurrentCharacterFollowTarget;
            if (guid == 0UL)
            {
                guid = player.GetSelectedCharacterGUID();
            }
            return ResolveCharacter(guid);
        }

        private static Player GetPlayer()
        {
            return PlayerManager.Instance?.Players != null
                && PlayerManager.Instance.Players.Count > 0
                    ? PlayerManager.Instance.Players[0]
                    : null;
        }

        private static AssetCharacter ResolveCharacter(ulong guid)
        {
            return guid != 0UL
                ? CharacterManager.Instance?.GetCharacterByGUID(guid)
                : null;
        }

        private static string GetCharacterName(ulong guid)
        {
            AssetCharacter character = ResolveCharacter(guid);
            return character?.Data != null && !string.IsNullOrWhiteSpace(character.Data.FullName)
                ? character.Data.FullName.Trim()
                : Loc.Get("state_unknown_para");
        }

        private static void SpeakBackground(string kind, string text)
        {
            string clean = ScreenReader.NormalizeForOutput(text);
            if (string.IsNullOrWhiteSpace(clean))
            {
                return;
            }
            Plugin.Log.LogInfo($"[Scene/{kind}] {clean}");
            ScreenReader.Speak(clean, interrupt: false);
        }
    }
}
