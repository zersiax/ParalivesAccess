using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ParalivesAccess.Speech;
using Setting;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>
    /// Reports model-backed gameplay changes which have no reliable focus or window event.
    /// </summary>
    /// <remarks>
    /// Two channels belong here:
    ///  - player-requested interactions, followed by their saved queue GUID and state rather than by
    ///    scraping the pooled interaction-queue prefab;
    ///  - notifications at the moment NotificationManager releases them for actual display.
    ///
    /// Autonomous interactions are deliberately excluded. InteractionManager exposes a dedicated
    /// AddToInteractionQueueOfSelectedCharactersFromPlayerInput entry point, so the observer can be
    /// useful without narrating every autonomous choice in the simulation.
    /// </remarks>
    public static class StateReportingHandler
    {
        public static bool Enabled { get; set; } = true;

        private sealed class TrackedInteraction
        {
            public ulong QueueGuid;
            public ulong ActorGuid;
            public ulong TargetCharacterGuid;
            public int PlayerIndex;
            public string Subject;
            public string Name;
            public bool FollowCameraOnStart;
            public ulong TravelDestinationLotGuid;
            public Vector3 TravelDestinationPosition;
            public bool CameraFollowStarted;
            public bool RequestedInIntro;
            public bool WasEverPresent;
            public bool EverRunning;
            public bool EverStarted;
            public bool CompletedAllActions;
            public bool WasCancelling;
            public bool QueueAnnounced;
            public bool StartAnnounced;
            public bool HasObservedState;
            public AssetCharacterDataInteractionState LastState;
            public int MissingFrames;
            public float RequestedAt;
            public string PendingTutorialSuccessKey;
            public int PendingTutorialSuccessFrame = -1;
            public string PendingNotificationTerminalKey;
            public int PendingNotificationTerminalFrame = -1;
            public bool TerminalSupersededByNotification;
        }

        private static readonly Dictionary<ulong, TrackedInteraction> Tracked =
            new Dictionary<ulong, TrackedInteraction>();

        private static readonly List<ulong> RemovalBuffer = new List<ulong>();

        /// <summary>
        /// Snapshot taken around the game's wrapper-to-child interaction handoff.
        /// </summary>
        /// <remarks>
        /// Commands such as Go home and Ask to leave finish a short player-input wrapper which
        /// injects a second queue entry with a new GUID. Without carrying the observation across
        /// that exact source-backed boundary, the wrapper disappearing is falsely reported as the
        /// command finishing while the Para has only just started moving.
        /// </remarks>
        private sealed class InjectedInteractionHandoffState
        {
            public ulong ParentQueueGuid;
            public ulong ActorGuid;
            public ulong ChildSettingGuid;
            public readonly HashSet<ulong> QueueGuidsBefore = new HashSet<ulong>();
        }

        private sealed class TrackedInvitedVisitor
        {
            public ulong CharacterGuid;
            public ulong RuleGuid;
            public ulong TargetLotGuid;
            public bool Arrived;
        }

        private const string InvitationSuccessNotification = "CharacterInvitedToHome";
        private const string InvitationFailureNotification = "CharacterInvitedToHomeError";

        private static readonly Dictionary<ulong, TrackedInvitedVisitor> InvitedVisitors =
            new Dictionary<ulong, TrackedInvitedVisitor>();

        private static readonly List<ulong> VisitorRemovalBuffer = new List<ulong>();

        // TEST 66 measured the departure notification within the nine-frame interval between the
        // action entering ToBeCanceled and the next physical arrow. Hold only target-character
        // stops, and only across this bounded settlement window. A notification must additionally
        // belong to the shipped CharacterLeaves_* semantic family before it can replace the generic
        // "Talk with stopped" result; unrelated same-Para notifications cannot suppress it.
        private const int NotificationTerminalSettleFrames = 12;

        private static readonly Dictionary<ulong, int> RecentDepartureNotificationTargetFrames =
            new Dictionary<ulong, int>();

        private sealed class TrackedThoughtBubble
        {
            public int LastSeenFrame;
            public bool Announced;
            public string ContentSignature;
        }

        private sealed class RequestBubbleEntry
        {
            public ulong RequestGuid;
            public ulong RequesterGuid;
            public readonly List<ulong> HouseholdCharacterGuids = new List<ulong>();
        }

        private sealed class RequestBubbleModel
        {
            public readonly List<RequestBubbleEntry> Entries = new List<RequestBubbleEntry>();
            public string Signature;
        }

        private static readonly Dictionary<string, TrackedThoughtBubble> ThoughtBubbles =
            new Dictionary<string, TrackedThoughtBubble>();

        private static readonly List<string> ThoughtBubbleRemovalBuffer = new List<string>();

        /// <summary>
        /// The exact nested label shown by the interaction row whose synchronous queue call is in
        /// progress. Some reusable interaction settings have a different object-specific UI name.
        /// </summary>
        private static string _visiblePlayerInteractionName;

        /// <summary>Installs the player-action and displayed-notification observation patches.</summary>
        public static bool ApplyPatches(Harmony harmony)
        {
            try
            {
                MethodInfo queue = AccessTools.Method(
                    typeof(InteractionManager),
                    "AddToInteractionQueueOfSelectedCharactersFromPlayerInput",
                    new[]
                    {
                        typeof(int), typeof(ulong), typeof(InteractionTarget), typeof(ulong),
                        typeof(ulong)
                    });
                MethodInfo pullNotification = AccessTools.Method(
                    typeof(NotificationManager),
                    "PullNotificationToStartDisplaying",
                    new[] { typeof(bool) });
                MethodInfo completeAction = AccessTools.Method(
                    typeof(UpdateCharacterActions),
                    "CompleteTheAction",
                    new[]
                    {
                        typeof(AssetCharacter), typeof(AssetCharacterDataInteraction),
                        typeof(CurrentAction), typeof(ActionUnit)
                    });
                MethodInfo addThoughtBubble = AccessTools.Method(
                    typeof(UIThoughtBubbles),
                    "AddThoughtBubble",
                    new[] { typeof(ulong), typeof(ItemObjectRoot), typeof(ulong) });
                MethodInfo clickedInteraction = AccessTools.Method(
                    typeof(UIInteractionsListItem),
                    nameof(UIInteractionsListItem.ClickedInteraction),
                    new[] { typeof(ulong) });
                MethodInfo injectedInteraction = AccessTools.Method(
                    typeof(CancelAndInjectInteractionProcessor),
                    nameof(CancelAndInjectInteractionProcessor.Process),
                    new[] { typeof(Outcome), typeof(OutcomeData) });

                if (queue == null || pullNotification == null || completeAction == null
                    || addThoughtBubble == null || clickedInteraction == null
                    || injectedInteraction == null)
                {
                    Plugin.Log.LogError(
                        "State reporting patch targets are unavailable "
                      + $"(player interaction={queue != null}, action completion={completeAction != null}, "
                      + $"notification={pullNotification != null}, "
                      + $"thoughtBubble={addThoughtBubble != null}, "
                      + $"visible interaction label={clickedInteraction != null}, "
                      + $"injected interaction handoff={injectedInteraction != null}).");
                    return false;
                }

                harmony.Patch(
                    queue,
                    postfix: new HarmonyMethod(AccessTools.Method(
                        typeof(StateReportingHandler), nameof(PlayerInteractionQueuedPostfix))));
                harmony.Patch(
                    pullNotification,
                    postfix: new HarmonyMethod(AccessTools.Method(
                        typeof(StateReportingHandler), nameof(NotificationPulledPostfix))));
                harmony.Patch(
                    completeAction,
                    postfix: new HarmonyMethod(AccessTools.Method(
                        typeof(StateReportingHandler), nameof(InteractionActionCompletedPostfix))));
                harmony.Patch(
                    addThoughtBubble,
                    postfix: new HarmonyMethod(AccessTools.Method(
                        typeof(StateReportingHandler), nameof(ThoughtBubbleAddedPostfix))));
                harmony.Patch(
                    clickedInteraction,
                    prefix: new HarmonyMethod(AccessTools.Method(
                        typeof(StateReportingHandler), nameof(VisibleInteractionNamePrefix))),
                    finalizer: new HarmonyMethod(AccessTools.Method(
                        typeof(StateReportingHandler), nameof(VisibleInteractionNameFinalizer))));
                harmony.Patch(
                    injectedInteraction,
                    prefix: new HarmonyMethod(AccessTools.Method(
                        typeof(StateReportingHandler), nameof(InjectedInteractionPrefix))),
                    postfix: new HarmonyMethod(AccessTools.Method(
                        typeof(StateReportingHandler), nameof(InjectedInteractionPostfix))));

                var patched = new List<MethodBase>(harmony.GetPatchedMethods());
                bool queueApplied = patched.Contains(queue);
                bool notificationApplied = patched.Contains(pullNotification);
                bool completionApplied = patched.Contains(completeAction);
                bool thoughtBubbleApplied = patched.Contains(addThoughtBubble);
                bool visibleNameApplied = patched.Contains(clickedInteraction);
                bool injectedHandoffApplied = patched.Contains(injectedInteraction);
                Plugin.Log.LogInfo(
                    "Patched state reporting, "
                  + $"playerInteraction={queueApplied}, actionCompletion={completionApplied}, "
                  + $"notification={notificationApplied}, thoughtBubble={thoughtBubbleApplied}, "
                  + $"visibleInteractionLabel={visibleNameApplied}, "
                  + $"injectedInteractionHandoff={injectedHandoffApplied}.");
                return queueApplied && completionApplied && notificationApplied
                    && thoughtBubbleApplied && visibleNameApplied && injectedHandoffApplied;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Patching state reporting threw: " + ex);
                return false;
            }
        }

        /// <summary>
        /// Captures an object-specific nested interaction name only for the duration of the row's
        /// own <see cref="UIInteractionsListItem.ClickedInteraction"/> call.
        /// </summary>
        public static void VisibleInteractionNamePrefix(
            UIInteractionsListItem __instance, out string __state)
        {
            __state = _visiblePlayerInteractionName;
            _visiblePlayerInteractionName = GetVisibleNestedInteractionName(__instance);
        }

        /// <summary>
        /// Restores the previous interaction-name context even when the game's click method throws.
        /// </summary>
        public static Exception VisibleInteractionNameFinalizer(
            Exception __exception, string __state)
        {
            _visiblePlayerInteractionName = __state;
            return __exception;
        }

        /// <summary>
        /// Captures the actor's exact queue before a tracked player command injects its child.
        /// </summary>
        private static void InjectedInteractionPrefix(
            Outcome outcome, OutcomeData data, out InjectedInteractionHandoffState __state)
        {
            __state = null;
            if (!Enabled || data.Interaction == null || outcome?.InjectedInteraction == null
                || outcome.InjectedInteraction.InjectedInteraction == 0UL)
            {
                return;
            }

            try
            {
                TrackedInteraction tracked;
                if (!Tracked.TryGetValue(data.Interaction.GUID, out tracked))
                {
                    return;
                }

                AssetCharacter actor = ResolveCharacter(data.CharacterGUID);
                if (actor?.Data?.CurrentInteractionsInQueue == null)
                {
                    return;
                }

                var state = new InjectedInteractionHandoffState
                {
                    ParentQueueGuid = data.Interaction.GUID,
                    ActorGuid = data.CharacterGUID,
                    ChildSettingGuid = outcome.InjectedInteraction.InjectedInteraction
                };
                foreach (AssetCharacterDataInteraction entry
                    in actor.Data.CurrentInteractionsInQueue)
                {
                    if (entry != null)
                    {
                        state.QueueGuidsBefore.Add(entry.GUID);
                    }
                }

                __state = state;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError(
                    "StateReportingHandler.InjectedInteractionPrefix threw: " + ex);
            }
        }

        /// <summary>
        /// Moves a player-command observation to the one newly injected child queue entry.
        /// </summary>
        private static void InjectedInteractionPostfix(InjectedInteractionHandoffState __state)
        {
            if (!Enabled || __state == null)
            {
                return;
            }

            try
            {
                TrackedInteraction tracked;
                if (!Tracked.TryGetValue(__state.ParentQueueGuid, out tracked))
                {
                    return;
                }

                AssetCharacter actor = ResolveCharacter(__state.ActorGuid);
                if (actor?.Data?.CurrentInteractionsInQueue == null)
                {
                    return;
                }

                AssetCharacterDataInteraction child = null;
                int matchingChildren = 0;
                foreach (AssetCharacterDataInteraction entry
                    in actor.Data.CurrentInteractionsInQueue)
                {
                    if (entry == null
                        || entry.InteractionSettingGUID != __state.ChildSettingGuid
                        || __state.QueueGuidsBefore.Contains(entry.GUID))
                    {
                        continue;
                    }

                    child = entry;
                    matchingChildren++;
                }

                // The native processor may deliberately decline an injection when that setting is
                // already queued, and item-finder failure can also produce no child. Only one exact
                // new entry is proof of a handoff; ambiguity leaves the original lifecycle intact.
                if (matchingChildren != 1 || child == null || Tracked.ContainsKey(child.GUID))
                {
                    Plugin.Log.LogInfo(
                        $"[State/action] wrapper guid={__state.ParentQueueGuid}, "
                      + $"childSetting={__state.ChildSettingGuid}, "
                      + $"newMatchingChildren={matchingChildren}; lifecycle not handed off.");
                    return;
                }

                Tracked.Remove(__state.ParentQueueGuid);
                ulong parentGuid = tracked.QueueGuid;
                tracked.QueueGuid = child.GUID;
                tracked.ActorGuid = __state.ActorGuid;
                tracked.WasEverPresent = false;
                tracked.EverRunning = false;
                tracked.EverStarted = false;
                tracked.CompletedAllActions = false;
                tracked.WasCancelling = false;
                tracked.HasObservedState = false;
                tracked.MissingFrames = 0;
                tracked.PendingTutorialSuccessKey = null;
                tracked.PendingTutorialSuccessFrame = -1;
                tracked.PendingNotificationTerminalKey = null;
                tracked.PendingNotificationTerminalFrame = -1;
                tracked.TerminalSupersededByNotification = false;
                ObserveEntry(tracked, child);
                Tracked[child.GUID] = tracked;

                Plugin.Log.LogInfo(
                    $"[State/action] handed off '{tracked.Name}' lifecycle from wrapper "
                  + $"guid={parentGuid} to injected guid={child.GUID}, "
                  + $"setting={__state.ChildSettingGuid}, actor={__state.ActorGuid}, "
                  + $"state={child.State}.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError(
                    "StateReportingHandler.InjectedInteractionPostfix threw: " + ex);
            }
        }

        /// <summary>
        /// Records the exact queue GUID produced by a native player-input interaction request.
        /// </summary>
        public static void PlayerInteractionQueuedPostfix(
            int playerIndex,
            ulong interactionGUID,
            InteractionTarget target,
            ulong lotGUID,
            ulong __result)
        {
            if (!Enabled || __result == 0)
            {
                return;
            }

            try
            {
                Player player = GetPlayer(playerIndex);
                InteractionUnit setting = Settings.Get<Interactions>()?.GetInteractionByGUID(interactionGUID);
                string name = !string.IsNullOrWhiteSpace(_visiblePlayerInteractionName)
                    ? _visiblePlayerInteractionName
                    : GetInteractionName(setting, interactionGUID);

                ulong actorGuid = 0;
                AssetCharacterDataInteraction entry = null;
                int selectedCount = 0;

                if (player != null && player.SelectedCharactersGUID != null)
                {
                    selectedCount = player.SelectedCharactersGUID.Count;
                    foreach (ulong characterGuid in player.SelectedCharactersGUID)
                    {
                        AssetCharacter character = ResolveCharacter(characterGuid);
                        AssetCharacterDataInteraction candidate = FindEntry(character, __result);
                        if (actorGuid == 0)
                        {
                            actorGuid = characterGuid;
                        }
                        if (candidate != null)
                        {
                            actorGuid = characterGuid;
                            entry = candidate;
                            break;
                        }
                    }
                }

                // A shared queue GUID can be copied to several selected Paras. This first slice
                // follows one concrete matching actor and names that actor; saying "3 Paras" from
                // one actor's state would overclaim group success. Multi-actor aggregation remains
                // a separate runtime-backed refinement.
                string subject = GetSubject(actorGuid);
                ulong travelDestinationLotGuid;
                Vector3 travelDestinationPosition;
                bool followCameraOnStart = TownMapLotHandler.TryClaimTravelCameraFollow(
                    playerIndex,
                    target,
                    lotGUID,
                    out travelDestinationLotGuid,
                    out travelDestinationPosition);
                var tracked = new TrackedInteraction
                {
                    QueueGuid = __result,
                    ActorGuid = actorGuid,
                    TargetCharacterGuid = target.CharacterGUID,
                    PlayerIndex = playerIndex,
                    Subject = subject,
                    Name = name,
                    FollowCameraOnStart = followCameraOnStart,
                    TravelDestinationLotGuid = travelDestinationLotGuid,
                    TravelDestinationPosition = travelDestinationPosition,
                    RequestedInIntro = player?.IsInIntro == true,
                    RequestedAt = Time.unscaledTime
                };

                if (entry != null)
                {
                    ObserveEntry(tracked, entry);
                }

                Tracked[__result] = tracked;
                Plugin.Log.LogInfo(
                    $"[State/action] requested guid={__result}, setting={interactionGUID}, "
                  + $"name='{name}', subject='{subject}', selected={selectedCount}, "
                  + $"present={entry != null}, "
                  + $"state={(entry != null ? entry.State.ToString() : "missing")}, "
                  + $"targetCharacter={tracked.TargetCharacterGuid}, "
                  + $"travelCamera={tracked.FollowCameraOnStart}, targetLot={lotGUID}, "
                  + $"travelDestinationLot={tracked.TravelDestinationLotGuid}, "
                  + $"travelDestination={tracked.TravelDestinationPosition}, "
                  + $"origin={(tracked.RequestedInIntro ? "tutorial" : "ordinary")}, "
                  + "lifecycle=pending-settle.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("StateReportingHandler.PlayerInteractionQueuedPostfix threw: " + ex);
            }
        }

        private static string GetVisibleNestedInteractionName(UIInteractionsListItem item)
        {
            InteractionGroupItem groupItem = item?.InteractionGroupItem;
            if (groupItem == null || groupItem.Type != InteractionItemType.Interaction
                || !groupItem.IsNestedNameDifferentThanInteractionName)
            {
                return null;
            }

            string key = groupItem.NestedNameTranslationKey;
            string translated = string.IsNullOrEmpty(key) ? null : TranslationManager.Get(key);
            string name = !string.IsNullOrWhiteSpace(translated) && translated != key
                ? translated
                : groupItem.DisplayNameOfNestedInteraction;
            return string.IsNullOrWhiteSpace(name)
                ? null
                : ScreenReader.NormalizeForOutput(name.Trim());
        }

        /// <summary>
        /// Records the game's exact final-action completion boundary before the queue entry can be
        /// removed later in the same frame.
        /// </summary>
        public static void InteractionActionCompletedPostfix(
            AssetCharacter characterAsset, AssetCharacterDataInteraction interaction)
        {
            if (!Enabled || characterAsset == null || interaction == null
                || interaction.IsCancelling
                || interaction.State != AssetCharacterDataInteractionState.ToBeDeleted
                || interaction.CurrentActionList == null
                || interaction.CurrentActionList.Count == 0
                || interaction.CurrentActionListIndex < interaction.CurrentActionList.Count)
            {
                return;
            }

            try
            {
                TrackedInteraction tracked;
                if (!Tracked.TryGetValue(interaction.GUID, out tracked)
                    || tracked.ActorGuid != characterAsset.GUID)
                {
                    return;
                }

                tracked.CompletedAllActions = true;
                Plugin.Log.LogInfo(
                    $"[State/action] guid={tracked.QueueGuid}, name='{tracked.Name}', "
                  + "final action completed before queue removal.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError(
                    "StateReportingHandler.InteractionActionCompletedPostfix threw: " + ex);
            }
        }

        /// <summary>Speaks a notification only when the game has actually released it for display.</summary>
        public static void NotificationPulledPostfix(NotificationData __result)
        {
            if (!Enabled || __result == null)
            {
                return;
            }

            try
            {
                Notification notification = Settings.Get<Notifications>()?
                    .GetNotificationByGUID(__result.NotificationGUID);
                if (IsInvitationNotification(notification, success: true))
                {
                    TrackInvitedVisitors(__result);
                }

                string body = DescribeNotification(__result);
                if (string.IsNullOrEmpty(body))
                {
                    return;
                }

                string spoken = Loc.Get("state_notification", body);
                string notificationType = notification != null
                    ? notification.DisplayName
                    : __result.NotificationGUID.ToString();
                bool isDeparture = notification != null
                    && !string.IsNullOrWhiteSpace(notification.DisplayName)
                    && notification.DisplayName.StartsWith(
                        "CharacterLeaves_", StringComparison.Ordinal);

                if (isDeparture && __result.CharacterGUIDs != null)
                {
                    foreach (ulong characterGuid in __result.CharacterGUIDs)
                    {
                        if (characterGuid == 0UL)
                        {
                            continue;
                        }

                        RecentDepartureNotificationTargetFrames[characterGuid] = Time.frameCount;
                        foreach (KeyValuePair<ulong, TrackedInteraction> pair in Tracked)
                        {
                            TrackedInteraction tracked = pair.Value;
                            if (tracked.TargetCharacterGuid == characterGuid
                                && !string.IsNullOrEmpty(tracked.PendingNotificationTerminalKey))
                            {
                                tracked.TerminalSupersededByNotification = true;
                            }
                        }
                    }
                }

                Plugin.Log.LogInfo(
                    $"[State/notification] guid={__result.NotificationGUID}, "
                  + $"type='{notificationType}', departure={isDeparture}, "
                  + $"frame={Time.frameCount}: {spoken}");
                AnnouncementCoordinator.PublishImportant("notification", spoken);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("StateReportingHandler.NotificationPulledPostfix threw: " + ex);
            }
        }

        /// <summary>
        /// Announces the request content represented by the two known world-space indicators.
        /// </summary>
        /// <remarks>
        /// AddThoughtBubble is refreshed every frame while a request remains available. Tracking the
        /// exact model key turns that stream into one event, while the visibility check prevents an
        /// off-camera request from being announced as though its icon were currently present. The
        /// bubble object carries no text, so the report resolves the request GUIDs through the same
        /// GoalsManager collections and methods that produce the icon and its native Together card.
        /// </remarks>
        public static void ThoughtBubbleAddedPostfix(
            UIThoughtBubbles __instance, ulong characterGUID, ItemObjectRoot item, ulong bubbleType)
        {
            if (!Enabled || __instance == null)
            {
                return;
            }

            try
            {
                Goals goals = Settings.Get<Goals>();
                bool available = goals != null && bubbleType == goals.RequestAvailableThoughtBubble;
                bool completed = goals != null && bubbleType == goals.RequestCompletedThoughtBubble;
                if (!available && !completed)
                {
                    return;
                }

                int itemId = item != null ? item.InstanceID : -1;
                string key = characterGUID + ":" + itemId + ":" + bubbleType;
                TrackedThoughtBubble tracked;
                if (!ThoughtBubbles.TryGetValue(key, out tracked))
                {
                    tracked = new TrackedThoughtBubble();
                    ThoughtBubbles[key] = tracked;
                }

                tracked.LastSeenFrame = Time.frameCount;
                if (!IsThoughtBubbleVisible(__instance, characterGUID, item))
                {
                    return;
                }

                string subject = GetThoughtBubbleSubject(characterGUID, item);
                RequestBubbleModel model = available
                    ? GetAvailableRequestBubbleModel(characterGUID)
                    : GetCompletedRequestBubbleModel(characterGUID);
                if (tracked.Announced && tracked.ContentSignature == model.Signature)
                {
                    return;
                }

                string spoken = available
                    ? DescribeAvailableRequestBubble(subject, characterGUID == 0, model)
                    : DescribeCompletedRequestBubble(subject, model);
                tracked.Announced = true;
                tracked.ContentSignature = model.Signature;
                Plugin.Log.LogInfo(
                    $"[State/thought-bubble] target='{subject}', bubble={bubbleType}, "
                  + $"kind={(available ? "available" : "completed")}, "
                  + $"content='{model.Signature}', spoken='{spoken}'.");
                ScreenReader.Speak(spoken);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("StateReportingHandler.ThoughtBubbleAddedPostfix threw: " + ex);
            }
        }

        /// <summary>Observes settled state transitions for the player-requested queue GUIDs.</summary>
        public static void Tick()
        {
            if (!Enabled)
            {
                return;
            }

            try
            {
                PruneThoughtBubbleObservations();

                // Queue objects are replaced wholesale during loading. Dropping the old observation
                // is truthful; interpreting that disappearance as completion would not be.
                if (GameLoadingManager.State != State.Game)
                {
                    if (Tracked.Count > 0)
                    {
                        Plugin.Log.LogInfo(
                            $"[State/action] cleared {Tracked.Count} pending observations during "
                          + $"state {GameLoadingManager.State}.");
                    }
                    Tracked.Clear();
                    InvitedVisitors.Clear();
                    RecentDepartureNotificationTargetFrames.Clear();
                    return;
                }

                ObserveInvitedVisitors();

                if (Tracked.Count == 0)
                {
                    return;
                }

                RemovalBuffer.Clear();
                foreach (KeyValuePair<ulong, TrackedInteraction> pair in Tracked)
                {
                    TrackedInteraction tracked = pair.Value;

                    if (!string.IsNullOrEmpty(tracked.PendingNotificationTerminalKey))
                    {
                        if (tracked.TerminalSupersededByNotification)
                        {
                            Plugin.Log.LogInfo(
                                $"[State/action] guid={tracked.QueueGuid}, name='{tracked.Name}', "
                              + $"result={tracked.PendingNotificationTerminalKey} coalesced with "
                              + $"the target character's displayed notification.");
                            RemovalBuffer.Add(pair.Key);
                            continue;
                        }

                        int heldFrames = Time.frameCount
                            - tracked.PendingNotificationTerminalFrame;
                        if (heldFrames <= NotificationTerminalSettleFrames)
                        {
                            continue;
                        }

                        Plugin.Log.LogInfo(
                            $"[State/action] guid={tracked.QueueGuid}, name='{tracked.Name}', "
                          + $"result={tracked.PendingNotificationTerminalKey} released after "
                          + $"{heldFrames} frames; no matching target notification replaced it.");
                        SpeakTerminal(tracked, tracked.PendingNotificationTerminalKey);
                        RemovalBuffer.Add(pair.Key);
                        continue;
                    }

                    if (!string.IsNullOrEmpty(tracked.PendingTutorialSuccessKey))
                    {
                        if (TutorialHandler.LastStateAnnouncementFrame
                            >= tracked.PendingTutorialSuccessFrame)
                        {
                            Plugin.Log.LogInfo(
                                $"[State/action] guid={tracked.QueueGuid}, name='{tracked.Name}', "
                              + $"result={tracked.PendingTutorialSuccessKey} coalesced with "
                              + "the tutorial's settled state announcement.");
                            RemovalBuffer.Add(pair.Key);
                            continue;
                        }

                        int heldFrames = Time.frameCount - tracked.PendingTutorialSuccessFrame;
                        if (heldFrames <= TutorialHandler.AnnouncementSettleFrames)
                        {
                            continue;
                        }

                        Plugin.Log.LogInfo(
                            $"[State/action] guid={tracked.QueueGuid}, name='{tracked.Name}', "
                          + $"result={tracked.PendingTutorialSuccessKey} released after "
                          + $"{heldFrames} frames; no tutorial state announcement replaced it.");
                        SpeakTerminal(tracked, tracked.PendingTutorialSuccessKey);
                        RemovalBuffer.Add(pair.Key);
                        continue;
                    }

                    AssetCharacter character = ResolveCharacter(tracked.ActorGuid);
                    AssetCharacterDataInteraction entry = FindEntry(character, tracked.QueueGuid);

                    if (entry == null)
                    {
                        tracked.MissingFrames++;
                        if (tracked.MissingFrames < 3)
                        {
                            continue;
                        }

                        if (AnnounceDisappearance(tracked))
                        {
                            RemovalBuffer.Add(pair.Key);
                        }
                        continue;
                    }

                    tracked.MissingFrames = 0;
                    AssetCharacterDataInteractionState before = tracked.LastState;
                    bool hadState = tracked.HasObservedState;
                    ObserveEntry(tracked, entry);

                    if (!hadState || before != entry.State)
                    {
                        Plugin.Log.LogInfo(
                            $"[State/action] guid={tracked.QueueGuid}, name='{tracked.Name}', "
                          + $"state={(hadState ? before.ToString() : "new")}->{entry.State}, "
                          + $"started={entry.HasStarted}, cancelling={entry.IsCancelling}.");
                    }

                    if (IsCancellationState(entry.State) || entry.IsCancelling)
                    {
                        if (!HoldTerminalForTargetNotification(
                            tracked, "state_action_stopped"))
                        {
                            SpeakTerminal(tracked, "state_action_stopped");
                            RemovalBuffer.Add(pair.Key);
                        }
                        continue;
                    }

                    if (entry.State == AssetCharacterDataInteractionState.ToBeDeleted)
                    {
                        string terminalKey = tracked.CompletedAllActions
                            ? "state_action_finished"
                            : (tracked.EverRunning
                                ? "state_action_ended"
                                : "state_action_could_not_start");
                        if (!HoldProvenTutorialSuccess(tracked, terminalKey))
                        {
                            SpeakTerminal(tracked, terminalKey);
                            RemovalBuffer.Add(pair.Key);
                        }
                        continue;
                    }

                    if (entry.State == AssetCharacterDataInteractionState.Running
                        && !tracked.StartAnnounced)
                    {
                        tracked.StartAnnounced = true;
                        tracked.CameraFollowStarted = StartTravelCameraFollow(tracked);
                        Speak(
                            tracked,
                            tracked.CameraFollowStarted
                                ? "state_action_started_following"
                                : "state_action_started");
                        continue;
                    }

                    // Do not say "queued" for an action that starts almost immediately. Half a
                    // second distinguishes a real waiting state without producing two near-simultaneous
                    // announcements for the normal path.
                    if (!tracked.QueueAnnounced && !tracked.StartAnnounced
                        && Time.unscaledTime - tracked.RequestedAt >= 0.5f)
                    {
                        tracked.QueueAnnounced = true;
                        SpeakQueued(tracked, character);
                    }
                }

                foreach (ulong guid in RemovalBuffer)
                {
                    Tracked.Remove(guid);
                }
            }
            catch (Exception ex)
            {
                Enabled = false;
                Tracked.Clear();
                InvitedVisitors.Clear();
                Plugin.Log.LogError("StateReportingHandler disabled after an exception: " + ex);
                ScreenReader.Speak(Loc.Get("state_reporting_failed"));
            }
        }

        /// <summary>
        /// Starts observing only visitors whose accepted invitation has actually reached the
        /// game's displayed-notification channel.
        /// </summary>
        private static void TrackInvitedVisitors(NotificationData data)
        {
            if (data?.CharacterGUIDs == null)
            {
                return;
            }

            foreach (ulong characterGuid in data.CharacterGUIDs)
            {
                AssetCharacter character = ResolveCharacter(characterGuid);
                if (character?.Data == null
                    || character.Data.CurrentTownAutonomyRule == 0UL
                    || character.Data.CurrentTownAutonomyTargetLot == 0UL)
                {
                    Plugin.Log.LogInfo(
                        $"[State/visitor] accepted invitation for character={characterGuid}, "
                      + "but no active town-autonomy visit could be observed.");
                    continue;
                }

                InvitedVisitors[characterGuid] = new TrackedInvitedVisitor
                {
                    CharacterGuid = characterGuid,
                    RuleGuid = character.Data.CurrentTownAutonomyRule,
                    TargetLotGuid = character.Data.CurrentTownAutonomyTargetLot
                };
                Plugin.Log.LogInfo(
                    $"[State/visitor] tracking invited Para '{GetCharacterName(characterGuid)}', "
                  + $"character={characterGuid}, rule={character.Data.CurrentTownAutonomyRule}, "
                  + $"targetLot={character.Data.CurrentTownAutonomyTargetLot}, "
                  + $"state={character.Data.TownAutonomyDestinationState}.");
            }
        }

        /// <summary>Reports arrival and the end of visits accepted through the invite picker.</summary>
        private static void ObserveInvitedVisitors()
        {
            if (InvitedVisitors.Count == 0)
            {
                return;
            }

            VisitorRemovalBuffer.Clear();
            foreach (KeyValuePair<ulong, TrackedInvitedVisitor> pair in InvitedVisitors)
            {
                TrackedInvitedVisitor tracked = pair.Value;
                AssetCharacter character = ResolveCharacter(tracked.CharacterGuid);
                if (character?.Data == null)
                {
                    Plugin.Log.LogInfo(
                        $"[State/visitor] character={tracked.CharacterGuid} is no longer in the "
                      + "character model; dropping the visit observation silently.");
                    VisitorRemovalBuffer.Add(pair.Key);
                    continue;
                }

                bool sameVisit = character.Data.CurrentTownAutonomyRule == tracked.RuleGuid
                    && character.Data.CurrentTownAutonomyTargetLot == tracked.TargetLotGuid;
                TownAutonomyDestinationState state =
                    character.Data.TownAutonomyDestinationState;
                string name = GetCharacterName(tracked.CharacterGuid);

                if (!tracked.Arrived)
                {
                    // InjectTownAutonomyToCharacter sets HeadingToALot. The game's exact walk-done
                    // boundary changes that state to Idle; no position heuristic is required.
                    if (sameVisit && state == TownAutonomyDestinationState.Idle)
                    {
                        tracked.Arrived = true;
                        string arrival = Loc.Get("state_invited_visitor_arrived", name);
                        Plugin.Log.LogInfo(
                            $"[State/visitor] character={tracked.CharacterGuid}, "
                          + $"rule={tracked.RuleGuid}, targetLot={tracked.TargetLotGuid}: {arrival}");
                        AnnouncementCoordinator.PublishImportant("visitor-state", arrival);
                    }
                    else if (!sameVisit || state == TownAutonomyDestinationState.OutOfTown)
                    {
                        string cancelled = Loc.Get("state_invited_visitor_not_arriving", name);
                        Plugin.Log.LogInfo(
                            $"[State/visitor] character={tracked.CharacterGuid} changed before "
                          + $"arrival; currentRule={character.Data.CurrentTownAutonomyRule}, "
                          + $"currentLot={character.Data.CurrentTownAutonomyTargetLot}, "
                          + $"state={state}: {cancelled}");
                        AnnouncementCoordinator.PublishImportant("visitor-state", cancelled);
                        VisitorRemovalBuffer.Add(pair.Key);
                    }
                    continue;
                }

                // HeadingOutOfTown is movement in progress, not a completed departure. Retain the
                // observation until the native walk-done boundary reaches OutOfTown. A direct
                // ClearNPCDestinationAndLotInfo transition instead means the visit itself ended.
                if (state == TownAutonomyDestinationState.HeadingOutOfTown)
                {
                    continue;
                }

                if (state == TownAutonomyDestinationState.OutOfTown)
                {
                    string departed = Loc.Get("state_invited_visitor_left", name);
                    Plugin.Log.LogInfo(
                        $"[State/visitor] character={tracked.CharacterGuid}, state={state}: "
                      + departed);
                    AnnouncementCoordinator.PublishImportant("visitor-state", departed);
                    VisitorRemovalBuffer.Add(pair.Key);
                }
                else if (!sameVisit)
                {
                    string ended = Loc.Get("state_invited_visit_ended", name);
                    Plugin.Log.LogInfo(
                        $"[State/visitor] character={tracked.CharacterGuid} visit changed; "
                      + $"currentRule={character.Data.CurrentTownAutonomyRule}, "
                      + $"currentLot={character.Data.CurrentTownAutonomyTargetLot}, "
                      + $"state={state}: {ended}");
                    AnnouncementCoordinator.PublishImportant("visitor-state", ended);
                    VisitorRemovalBuffer.Add(pair.Key);
                }
            }

            foreach (ulong characterGuid in VisitorRemovalBuffer)
            {
                InvitedVisitors.Remove(characterGuid);
            }
        }

        private static void ObserveEntry(
            TrackedInteraction tracked, AssetCharacterDataInteraction entry)
        {
            tracked.WasEverPresent = true;
            tracked.EverRunning |= entry.State == AssetCharacterDataInteractionState.Running;
            tracked.EverStarted |= entry.HasStarted;
            tracked.CompletedAllActions |= entry.CurrentActionList != null
                && entry.CurrentActionList.Count > 0
                && entry.CurrentActionListIndex >= entry.CurrentActionList.Count
                && !entry.IsCancelling && !IsCancellationState(entry.State);
            tracked.WasCancelling |= entry.IsCancelling || IsCancellationState(entry.State);
            tracked.LastState = entry.State;
            tracked.HasObservedState = true;
        }

        private static bool AnnounceDisappearance(TrackedInteraction tracked)
        {
            string key;
            if (tracked.WasCancelling)
            {
                key = "state_action_stopped";
            }
            else if (tracked.CompletedAllActions)
            {
                key = "state_action_finished";
            }
            else if (tracked.EverRunning)
            {
                key = "state_action_ended";
            }
            else
            {
                key = "state_action_could_not_start";
            }

            Plugin.Log.LogInfo(
                $"[State/action] guid={tracked.QueueGuid}, name='{tracked.Name}', disappeared; "
              + $"present={tracked.WasEverPresent}, running={tracked.EverRunning}, "
              + $"started={tracked.EverStarted}, completed={tracked.CompletedAllActions}, "
              + $"cancelling={tracked.WasCancelling}, result={key}.");

            if (HoldProvenTutorialSuccess(tracked, key))
            {
                return false;
            }

            if (HoldTerminalForTargetNotification(tracked, key))
            {
                return false;
            }

            SpeakTerminal(tracked, key);
            return true;
        }

        /// <summary>
        /// Holds a target-character cancellation briefly so a displayed CharacterLeaves_* event
        /// about that same Para can replace the generic terminal result.
        /// </summary>
        private static bool HoldTerminalForTargetNotification(
            TrackedInteraction tracked, string key)
        {
            if (key != "state_action_stopped" || tracked.TargetCharacterGuid == 0UL)
            {
                return false;
            }

            tracked.PendingNotificationTerminalKey = key;
            tracked.PendingNotificationTerminalFrame = Time.frameCount;

            int recentFrame;
            if (RecentDepartureNotificationTargetFrames.TryGetValue(
                    tracked.TargetCharacterGuid, out recentFrame)
                && Time.frameCount - recentFrame <= NotificationTerminalSettleFrames)
            {
                tracked.TerminalSupersededByNotification = true;
            }

            Plugin.Log.LogInfo(
                $"[State/action] guid={tracked.QueueGuid}, name='{tracked.Name}', result={key} "
              + $"held for target-notification coalescing; "
              + $"target={tracked.TargetCharacterGuid}, "
              + $"alreadyMatched={tracked.TerminalSupersededByNotification}.");
            return true;
        }

        /// <summary>
        /// Gives a proven tutorial success just long enough for the tutorial observer's settled
        /// next-state announcement to replace it. No other lifecycle result is delayed or hidden.
        /// </summary>
        private static bool HoldProvenTutorialSuccess(
            TrackedInteraction tracked, string key)
        {
            if (!tracked.RequestedInIntro || key != "state_action_finished")
            {
                return false;
            }

            tracked.PendingTutorialSuccessKey = key;
            tracked.PendingTutorialSuccessFrame = Time.frameCount;
            Plugin.Log.LogInfo(
                $"[State/action] guid={tracked.QueueGuid}, name='{tracked.Name}', "
              + "holding proven tutorial success for state-announcement coalescing.");
            return true;
        }

        private static void SpeakTerminal(TrackedInteraction tracked, string key)
        {
            if (tracked.FollowCameraOnStart && key != "state_action_finished")
            {
                TownMapLotHandler.NotifyTravelCameraFollowFailed(
                    tracked.PlayerIndex,
                    tracked.QueueGuid,
                    tracked.ActorGuid,
                    key);
            }
            Speak(tracked, key);
        }

        /// <summary>
        /// Uses the same native state as the game's F / Center Camera on Character command, but
        /// only after an L-originated destination action has genuinely started.
        /// </summary>
        private static bool StartTravelCameraFollow(TrackedInteraction tracked)
        {
            if (!tracked.FollowCameraOnStart || tracked.ActorGuid == 0UL)
            {
                return false;
            }

            try
            {
                Player player = GetPlayer(tracked.PlayerIndex);
                AssetCharacter actor = ResolveCharacter(tracked.ActorGuid);
                if (player == null || actor?.Data == null)
                {
                    Plugin.Log.LogInfo(
                        $"[LotTravelCamera] start unavailable player={tracked.PlayerIndex}, "
                      + $"actor={tracked.ActorGuid}.");
                    return false;
                }

                bool newlyFollowing =
                    player.CameraCurrentCharacterFollowTarget != tracked.ActorGuid;
                if (newlyFollowing)
                {
                    player.CameraCurrentCharacterFollowTarget = tracked.ActorGuid;
                }
                CameraFollowHandler.FrameFollowStart(player, actor, "town-travel");
                TownMapLotHandler.NotifyTravelCameraFollowStarted(
                    tracked.PlayerIndex,
                    tracked.QueueGuid,
                    tracked.ActorGuid,
                    tracked.TravelDestinationLotGuid,
                    tracked.TravelDestinationPosition,
                    actor);
                Plugin.Log.LogInfo(
                    $"[LotTravelCamera] following actor={tracked.ActorGuid}, "
                  + $"name='{tracked.Subject}', action='{tracked.Name}', "
                  + $"position={actor.Data.Position}, newlyFollowing={newlyFollowing}.");
                return newlyFollowing;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Starting town travel camera follow threw: " + ex);
                return false;
            }
        }

        /// <summary>
        /// Reports a real wait in the interaction queue, including the currently running action
        /// when the actor model exposes one.
        /// </summary>
        /// <remarks>
        /// This replaces the existing queued utterance; it does not add a second announcement.
        /// TEST 62 left Sit here Running from 19:15 through the following morning, while Dance,
        /// Go here, Use toilet and Apply for a job all remained Pending behind it. Merely saying
        /// "queued" made that correct state sound like five unrelated execution failures.
        /// </remarks>
        private static void SpeakQueued(TrackedInteraction tracked, AssetCharacter character)
        {
            string runningName = GetRunningInteractionName(character, tracked.QueueGuid);
            if (string.IsNullOrWhiteSpace(runningName))
            {
                Speak(tracked, "state_action_queued");
                return;
            }

            bool interrupt = !tracked.RequestedInIntro
                || TutorialHandler.LastStateAnnouncementFrame != Time.frameCount;
            Plugin.Log.LogInfo(
                $"[State/action] output=state_action_queued_behind, name='{tracked.Name}', "
              + $"running='{runningName}', "
              + $"origin={(tracked.RequestedInIntro ? "tutorial" : "ordinary")}, "
              + $"speechMode={(interrupt ? "interrupt" : "queue-after-tutorial")}.");
            ScreenReader.Speak(
                Loc.Get(
                    "state_action_queued_behind",
                    tracked.Subject,
                    tracked.Name,
                    runningName),
                interrupt);
        }

        private static string GetRunningInteractionName(
            AssetCharacter character, ulong excludedQueueGuid)
        {
            if (character?.Data?.CurrentInteractionsInQueue == null)
            {
                return null;
            }

            foreach (AssetCharacterDataInteraction interaction
                in character.Data.CurrentInteractionsInQueue)
            {
                if (interaction == null || interaction.GUID == excludedQueueGuid
                    || interaction.State != AssetCharacterDataInteractionState.Running)
                {
                    continue;
                }

                TrackedInteraction running;
                if (Tracked.TryGetValue(interaction.GUID, out running)
                    && !string.IsNullOrWhiteSpace(running.Name))
                {
                    return running.Name;
                }

                InteractionUnit setting = Settings.Get<Interactions>()
                    ?.GetInteractionByGUID(interaction.InteractionSettingGUID);
                if (setting == null)
                {
                    // A missing setting is not useful speech. Fall back to the short queued
                    // message instead of exposing the raw GUID returned by GetInteractionName.
                    return null;
                }
                string name = GetInteractionName(setting, interaction.InteractionSettingGUID);
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }

            return null;
        }

        private static void Speak(TrackedInteraction tracked, string key)
        {
            bool interrupt = !tracked.RequestedInIntro
                || TutorialHandler.LastStateAnnouncementFrame != Time.frameCount;
            Plugin.Log.LogInfo(
                $"[State/action] output={key}, name='{tracked.Name}', "
              + $"origin={(tracked.RequestedInIntro ? "tutorial" : "ordinary")}, "
              + $"speechMode={(interrupt ? "interrupt" : "queue-after-tutorial")}.");
            ScreenReader.Speak(
                Loc.Get(key, tracked.Subject, tracked.Name), interrupt);
        }

        private static bool IsCancellationState(AssetCharacterDataInteractionState state)
        {
            return state == AssetCharacterDataInteractionState.ToBeCanceled
                || state == AssetCharacterDataInteractionState.Cancelling;
        }

        private static Player GetPlayer(int playerIndex)
        {
            if (PlayerManager.Instance == null || PlayerManager.Instance.Players == null
                || playerIndex < 0 || playerIndex >= PlayerManager.Instance.Players.Count)
            {
                return null;
            }

            return PlayerManager.Instance.Players[playerIndex];
        }

        private static AssetCharacter ResolveCharacter(ulong guid)
        {
            if (guid == 0 || CharacterManager.Instance == null)
            {
                return null;
            }

            return CharacterManager.Instance.GetCharacterByGUID(guid);
        }

        private static AssetCharacterDataInteraction FindEntry(
            AssetCharacter character, ulong queueGuid)
        {
            if (character == null || character.Data == null
                || character.Data.CurrentInteractionsInQueue == null)
            {
                return null;
            }

            foreach (AssetCharacterDataInteraction interaction
                in character.Data.CurrentInteractionsInQueue)
            {
                if (interaction != null && interaction.GUID == queueGuid)
                {
                    return interaction;
                }
            }

            return null;
        }

        private static string GetSubject(ulong actorGuid)
        {
            AssetCharacter actor = ResolveCharacter(actorGuid);
            return actor != null && actor.Data != null && !string.IsNullOrEmpty(actor.Data.FullName)
                ? actor.Data.FullName
                : Loc.Get("state_selected_para");
        }

        private static string GetThoughtBubbleSubject(ulong characterGuid, ItemObjectRoot item)
        {
            if (characterGuid != 0)
            {
                AssetCharacter character = ResolveCharacter(characterGuid);
                if (character != null && character.Data != null
                    && !string.IsNullOrEmpty(character.Data.FullName))
                {
                    return character.Data.FullName;
                }
            }

            if (item != null)
            {
                string name = ItemDescription.GetName(item);
                if (!string.IsNullOrEmpty(name))
                {
                    return name;
                }
            }

            return Loc.Get("item_unknown");
        }

        /// <summary>
        /// Resolves the exact offers represented by an available-request marker. A character marker
        /// represents that requester's entries; a request-board marker represents the full list.
        /// </summary>
        private static RequestBubbleModel GetAvailableRequestBubbleModel(ulong requesterGuid)
        {
            var model = new RequestBubbleModel();
            GoalsManager manager = GoalsManager.Instance;
            if (manager == null || manager.CurrentRequests == null)
            {
                model.Signature = "available:none";
                return model;
            }

            foreach (var current in manager.CurrentRequests)
            {
                if (requesterGuid != 0 && current.requester != requesterGuid)
                {
                    continue;
                }

                model.Entries.Add(new RequestBubbleEntry
                {
                    RequestGuid = current.request,
                    RequesterGuid = current.requester
                });
            }

            model.Signature = BuildRequestBubbleSignature("available", model.Entries);
            return model;
        }

        /// <summary>
        /// Resolves the first completable request the game exposes for each household Para at this
        /// requester, then groups copies of a shared household request into one spoken entry.
        /// </summary>
        private static RequestBubbleModel GetCompletedRequestBubbleModel(ulong requesterGuid)
        {
            var model = new RequestBubbleModel();
            GoalsManager manager = GoalsManager.Instance;
            HouseholdManager householdManager = HouseholdManager.Instance;
            if (requesterGuid == 0 || manager == null || householdManager == null)
            {
                model.Signature = "completed:none";
                return model;
            }

            List<ulong> householdCharacters = householdManager.GetCharactersInCurrentHousehold();
            if (householdCharacters == null)
            {
                model.Signature = "completed:none";
                return model;
            }

            foreach (ulong householdCharacterGuid in householdCharacters)
            {
                AssetCharacter character = ResolveCharacter(householdCharacterGuid);
                if (character == null)
                {
                    continue;
                }

                ulong requestGuid = manager.GetFirstCompletableRequestGivenToCharacterByCharacter(
                    character, requesterGuid);
                if (requestGuid == 0)
                {
                    continue;
                }

                RequestBubbleEntry matching = null;
                foreach (RequestBubbleEntry entry in model.Entries)
                {
                    if (entry.RequestGuid == requestGuid && entry.RequesterGuid == requesterGuid)
                    {
                        matching = entry;
                        break;
                    }
                }

                if (matching == null)
                {
                    matching = new RequestBubbleEntry
                    {
                        RequestGuid = requestGuid,
                        RequesterGuid = requesterGuid
                    };
                    model.Entries.Add(matching);
                }

                matching.HouseholdCharacterGuids.Add(householdCharacterGuid);
            }

            model.Signature = BuildRequestBubbleSignature("completed", model.Entries);
            return model;
        }

        private static string BuildRequestBubbleSignature(
            string kind, List<RequestBubbleEntry> entries)
        {
            var parts = new List<string>();
            foreach (RequestBubbleEntry entry in entries)
            {
                string actors = entry.HouseholdCharacterGuids.Count == 0
                    ? string.Empty
                    : string.Join(",", entry.HouseholdCharacterGuids.ConvertAll(
                        guid => guid.ToString()).ToArray());
                parts.Add(entry.RequestGuid + ":" + entry.RequesterGuid + ":" + actors);
            }

            return kind + ":" + string.Join("|", parts.ToArray());
        }

        private static string DescribeAvailableRequestBubble(
            string subject, bool isRequestBoard, RequestBubbleModel model)
        {
            if (model.Entries.Count == 0)
            {
                return Loc.Get("state_request_available", subject);
            }

            var offers = new List<string>();
            foreach (RequestBubbleEntry entry in model.Entries)
            {
                string title = GetRequestTitle(entry.RequestGuid);
                if (isRequestBoard)
                {
                    offers.Add(Loc.Get(
                        "state_request_offer",
                        GetCharacterName(entry.RequesterGuid),
                        title));
                }
                else
                {
                    offers.Add(title);
                }
            }

            string details = string.Join(Loc.Get("list_separator"), offers.ToArray());
            return offers.Count == 1
                ? Loc.Get("state_request_available_named", subject, details)
                : Loc.Get("state_requests_available_named", subject, offers.Count, details);
        }

        private static string DescribeCompletedRequestBubble(
            string subject, RequestBubbleModel model)
        {
            if (model.Entries.Count == 0)
            {
                return Loc.Get("state_request_completed", subject);
            }

            var requests = new List<string>();
            foreach (RequestBubbleEntry entry in model.Entries)
            {
                var characterNames = new List<string>();
                foreach (ulong characterGuid in entry.HouseholdCharacterGuids)
                {
                    characterNames.Add(GetCharacterName(characterGuid));
                }

                requests.Add(Loc.Get(
                    "state_request_turn_in_entry",
                    JoinNames(characterNames),
                    GetRequestTitle(entry.RequestGuid)));
            }

            string details = string.Join(Loc.Get("list_separator"), requests.ToArray());
            return requests.Count == 1
                ? Loc.Get("state_request_completed_named", subject, details)
                : Loc.Get("state_requests_completed_named", subject, requests.Count, details);
        }

        private static string GetRequestTitle(ulong requestGuid)
        {
            Goal goal = Settings.Get<Goals>()?.GetGoalByGUID(requestGuid);
            if (goal == null || string.IsNullOrEmpty(goal.DisplayName))
            {
                return Loc.Get("state_request_unknown");
            }

            string key = "Goal_" + goal.DisplayName;
            string translated = TranslationManager.Has(key)
                ? TranslationManager.Get(key)
                : null;
            if (string.IsNullOrWhiteSpace(translated) || translated == key)
            {
                return goal.DisplayName;
            }

            string normalized = ScreenReader.NormalizeForOutput(translated);
            return string.IsNullOrEmpty(normalized) ? goal.DisplayName : normalized;
        }

        private static string GetCharacterName(ulong characterGuid)
        {
            AssetCharacter character = ResolveCharacter(characterGuid);
            return character != null && character.Data != null
                && !string.IsNullOrWhiteSpace(character.Data.FullName)
                    ? character.Data.FullName.Trim()
                    : Loc.Get("state_unknown_para");
        }

        private static string JoinNames(List<string> names)
        {
            if (names.Count == 0)
            {
                return Loc.Get("state_unknown_para");
            }
            if (names.Count == 1)
            {
                return names[0];
            }
            if (names.Count == 2)
            {
                return Loc.Get("list_pair", names[0], names[1]);
            }

            string beginning = string.Join(
                Loc.Get("list_comma"), names.GetRange(0, names.Count - 1).ToArray());
            return Loc.Get("list_final_pair", beginning, names[names.Count - 1]);
        }

        /// <summary>Mirrors UIThoughtBubbles.Update's camera and viewport gates.</summary>
        private static bool IsThoughtBubbleVisible(
            UIThoughtBubbles window, ulong characterGuid, ItemObjectRoot item)
        {
            if (window == null || !window.IsVisible || window.PlayerOwnerOrDefault == null
                || window.PlayerOwnerOrDefault.HybridCamera == null)
            {
                return false;
            }

            HybridCamera hybridCamera = window.PlayerOwnerOrDefault.HybridCamera;
            Camera camera = hybridCamera.Camera;
            if (camera == null || hybridCamera.FreeCamera == null
                || hybridCamera.FreeCamera.LookTarget == null)
            {
                return false;
            }

            ThoughtBubbles settings = Settings.Get<ThoughtBubbles>();
            if (settings == null)
            {
                return false;
            }

            Vector3 worldPosition = Vector3.zero;
            if (characterGuid != 0)
            {
                AssetCharacter character = ResolveCharacter(characterGuid);
                if (character != null && character.Data != null
                    && CharacterHeightManager.Instance != null)
                {
                    worldPosition = character.Data.Position
                        + new Vector3(
                            0f,
                            CharacterHeightManager.Instance.GetHeight(characterGuid) + settings.Offset,
                            0f);
                }
            }
            else if (item != null && item.CubeTransform != null)
            {
                worldPosition = item.transform.position
                    + new Vector3(0f, item.CubeTransform.FinalSize.y + settings.Offset, 0f);
            }

            float maximumDistance = Mathf.Lerp(
                settings.VisibleAtDistanceOfCameraLookTarget.x,
                settings.VisibleAtDistanceOfCameraLookTarget.y,
                hybridCamera.FreeCamera.DistanceRatio);
            if (worldPosition != Vector3.zero
                && Vector3.Distance(worldPosition, hybridCamera.FreeCamera.LookTarget.position)
                    > maximumDistance)
            {
                return false;
            }

            Vector3 viewport = camera.WorldToViewportPoint(worldPosition);
            return viewport.z > 0f
                && viewport.x >= 0f && viewport.x <= 1f
                && viewport.y >= 0f && viewport.y <= 1f;
        }

        private static void PruneThoughtBubbleObservations()
        {
            if (ThoughtBubbles.Count == 0)
            {
                return;
            }

            ThoughtBubbleRemovalBuffer.Clear();
            foreach (KeyValuePair<string, TrackedThoughtBubble> pair in ThoughtBubbles)
            {
                // The game removes an unrefreshed bubble after two frames. Use the same boundary,
                // plus one frame for system ordering, before allowing a future occurrence to speak.
                if (Time.frameCount > pair.Value.LastSeenFrame + 3)
                {
                    ThoughtBubbleRemovalBuffer.Add(pair.Key);
                }
            }

            foreach (string key in ThoughtBubbleRemovalBuffer)
            {
                ThoughtBubbles.Remove(key);
            }
        }

        private static string GetInteractionName(InteractionUnit interaction, ulong guid)
        {
            if (interaction == null)
            {
                return guid.ToString();
            }

            string translated = TranslationManager.Get(interaction.TranslationKey);
            return string.IsNullOrEmpty(translated) || translated == interaction.TranslationKey
                ? interaction.DisplayName
                : translated;
        }

        private static string DescribeNotification(NotificationData data)
        {
            Notifications notifications = Settings.Get<Notifications>();
            Notification notification = notifications?.GetNotificationByGUID(data.NotificationGUID);
            if (notification == null)
            {
                return null;
            }

            string[] parameters = NotificationManager.Instance
                .GetTranslationParametersOfNotification(data);
            ulong gender = 0;
            if (data.CharacterGUIDs != null && data.CharacterGUIDs.Length > 0)
            {
                AssetCharacter character = ResolveCharacter(data.CharacterGUIDs[0]);
                if (character != null)
                {
                    gender = PersonalityManager.Instance.GetGenderFromPronouns(character);
                }
            }

            string titleKey = "Notification_" + notification.DisplayName;
            if (notification.CanBeCombined && data.WasCombined
                && notification.CombinedTitleOverride != 0)
            {
                titleKey = TranslationManager.GetKeyOfTranslationItemByGUID(
                    notification.CombinedTitleOverride);
            }

            string title = Translate(titleKey, gender, parameters);

            // PurchaseItemProcessor puts the purchased catalogue identity on this exact
            // notification. Preserve the native title unless its text omits functional detail
            // that the item's visible category or authored museum model provides.
            Items items = Settings.Get<Items>();
            if (items != null
                && data.NotificationGUID == items.ItemPurchaseNotification
                && data.CatalogueItemGUID != 0UL)
            {
                CatalogueItem purchased = items.GetItemByGUID(data.CatalogueItemGUID);
                string nativeItemName = ItemDescription.GetName(purchased, data.SkinGUID);
                string productName = ItemDescription.GetShopProductName(
                    purchased, data.SkinGUID);
                if (!string.IsNullOrEmpty(productName)
                    && !string.Equals(
                        productName, nativeItemName, StringComparison.OrdinalIgnoreCase))
                {
                    title = Loc.Get("state_shop_item_purchased", productName);
                }
            }

            // These two invitation toasts carry the invited Para only as a portrait. Their text is
            // a first-person response ("I'll be there soon" / "I'm unavailable"), so omitting the
            // portrait identity makes the result ambiguous to a screen-reader user.
            if (IsInvitationNotification(notification, success: true)
                || IsInvitationNotification(notification, success: false))
            {
                string characterName = data.CharacterGUIDs != null
                    && data.CharacterGUIDs.Length > 0
                        ? GetCharacterName(data.CharacterGUIDs[0])
                        : null;
                if (!string.IsNullOrEmpty(characterName) && !string.IsNullOrEmpty(title)
                    && title.IndexOf(characterName, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    title = Loc.Get("state_invitation_response", characterName, title);
                }
            }

            // The side notification prefab renders only LabelNotificationText. Subtitle belongs
            // exclusively to the center-of-screen prefab; speaking it for every side toast would
            // expose content the visible event does not present and make a frequent channel noisy.
            if (data.NotificationDisplayType != NotificationDisplayType.CenterOfScreen)
            {
                return title;
            }

            string subtitle = null;
            ulong subtitleGuid = notification.Subtitle;
            if (notification.CanBeCombined && data.WasCombined
                && notification.CombinedSubtitleOverride != 0)
            {
                subtitleGuid = notification.CombinedSubtitleOverride;
            }
            if (subtitleGuid != 0)
            {
                subtitle = Translate(
                    TranslationManager.GetKeyOfTranslationItemByGUID(subtitleGuid),
                    gender,
                    parameters);
            }

            if (string.IsNullOrEmpty(title))
            {
                return subtitle;
            }
            if (string.IsNullOrEmpty(subtitle))
            {
                return title;
            }

            return Loc.Get("state_notification_detail", title.TrimEnd('.', ' '), subtitle);
        }

        private static bool IsInvitationNotification(Notification notification, bool success)
        {
            return notification != null
                && string.Equals(
                    notification.DisplayName,
                    success ? InvitationSuccessNotification : InvitationFailureNotification,
                    StringComparison.Ordinal);
        }

        private static string Translate(string key, ulong gender, string[] parameters)
        {
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }

            return gender == 0
                ? TranslationManager.Get(key, parameters)
                : TranslationManager.Get(key, gender, parameters);
        }
    }
}
