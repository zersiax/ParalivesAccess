using System;
using System.Collections.Generic;
using ParalivesAccess.Speech;
using Setting;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ParalivesAccess.Handlers
{
    /// <summary>
    /// Gives the selected Para's complete interaction state at deliberate, low-frequency
    /// boundaries: once after a loaded/selected Para settles, and as a supplement to F3 context.
    /// </summary>
    /// <remarks>
    /// This deliberately does not observe queue mutations. Autonomous interactions share the same
    /// model as player-requested interactions, so transition narration here would turn ordinary
    /// simulation into a continuous activity feed. StateReportingHandler remains the owner of
    /// player-requested lifecycle events; this handler only takes compact snapshots of the current
    /// authoritative model.
    /// </remarks>
    internal static class ActivityQueueSummaryHandler
    {
        private const float LoadSettleSeconds = 2f;
        private const float SelectionSettleSeconds = 0.65f;

        private static bool _contextRequested;
        private static int _contextRequestFrame = -1;
        private static bool _wasInGame;
        private static ulong _selectedCharacterGuid;
        private static ulong _pendingSnapshotGuid;
        private static float _snapshotReadyAt;

        /// <summary>Captures the same unmodified F3 edge as WindowHandler.</summary>
        internal static void BeforeGameSystems()
        {
            try
            {
                if (!ContextKeyInput.ContextPressed)
                {
                    return;
                }

                _contextRequested = true;
                _contextRequestFrame = Time.frameCount;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError(
                    "ActivityQueueSummaryHandler.BeforeGameSystems threw: " + ex);
            }
        }

        /// <summary>
        /// Runs immediately after WindowHandler so an F3 activity snapshot queues behind the main
        /// context sentence instead of replacing it.
        /// </summary>
        internal static void Tick()
        {
            bool contextRequested = _contextRequested;
            int contextRequestFrame = _contextRequestFrame;
            _contextRequested = false;
            _contextRequestFrame = -1;

            try
            {
                Player player = GetPlayer();
                ObserveLoadAndSelection(player);

                if (contextRequested)
                {
                    SpeakOnDemand(player, contextRequestFrame);
                    return;
                }

                SpeakPendingSnapshot(player);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("ActivityQueueSummaryHandler.Tick threw: " + ex);
            }
        }

        private static void ObserveLoadAndSelection(Player player)
        {
            if (GameLoadingManager.State != State.Game || player == null)
            {
                ResetForLoading();
                return;
            }

            ulong selectedGuid = player.GetSelectedCharacterGUID();
            if (!_wasInGame)
            {
                _wasInGame = true;
                _selectedCharacterGuid = selectedGuid;
                ScheduleSnapshot(selectedGuid, LoadSettleSeconds, "game-loaded");
                return;
            }

            if (selectedGuid == _selectedCharacterGuid)
            {
                return;
            }

            _selectedCharacterGuid = selectedGuid;
            ScheduleSnapshot(selectedGuid, SelectionSettleSeconds, "selection-changed");
        }

        private static void ScheduleSnapshot(ulong characterGuid, float delay, string reason)
        {
            _pendingSnapshotGuid = characterGuid;
            _snapshotReadyAt = Time.unscaledTime + delay;
            Plugin.Log.LogInfo(
                $"[ActivitySummary] scheduled reason={reason}, character={characterGuid}, "
              + $"readyAt={_snapshotReadyAt:0.00}.");
        }

        private static void SpeakOnDemand(Player player, int requestFrame)
        {
            if (GameLoadingManager.State != State.Game || player == null)
            {
                // WindowHandler owns the F3 unavailable response outside a playable game.
                return;
            }

            // A deliberate report supersedes an initial/selection snapshot which may be waiting
            // for input to settle in the important-event coordinator.
            AnnouncementCoordinator.DiscardPending("activity-summary");

            string summary;
            ulong characterGuid;
            if (!TryBuildSummary(player, includeNoSelection: true, out summary, out characterGuid))
            {
                return;
            }

            Plugin.Log.LogInfo(
                $"[ActivitySummary] F3 request from frame {requestFrame}, "
              + $"character={characterGuid}: {summary}");
            ScreenReader.Speak(summary, interrupt: false, diagnosticSource: "F3-activities");

            // The deliberate F3 report satisfies a still-pending initial/selection snapshot. Do
            // not repeat the same information a fraction of a second later.
            if (characterGuid != 0UL && characterGuid == _pendingSnapshotGuid
                && CharacterManager.Instance?.GetCharacterByGUID(characterGuid)?.Data != null)
            {
                _pendingSnapshotGuid = 0UL;
            }
        }

        private static void SpeakPendingSnapshot(Player player)
        {
            if (_pendingSnapshotGuid == 0UL || Time.unscaledTime < _snapshotReadyAt
                || player == null || player.GetSelectedCharacterGUID() != _pendingSnapshotGuid)
            {
                return;
            }

            string summary;
            ulong characterGuid;
            if (!TryBuildSummary(player, includeNoSelection: false, out summary, out characterGuid))
            {
                // Character data can arrive after GameLoadingManager enters Game. Retain the
                // request and try again without producing a misleading idle snapshot.
                _snapshotReadyAt = Time.unscaledTime + SelectionSettleSeconds;
                return;
            }

            _pendingSnapshotGuid = 0UL;
            Plugin.Log.LogInfo(
                $"[ActivitySummary] initial/selection snapshot character={characterGuid}: {summary}");
            AnnouncementCoordinator.PublishLatestImportant("activity-summary", summary);
        }

        private static bool TryBuildSummary(
            Player player, bool includeNoSelection, out string summary, out ulong characterGuid)
        {
            summary = null;
            characterGuid = player?.GetSelectedCharacterGUID() ?? 0UL;
            if (characterGuid == 0UL)
            {
                if (includeNoSelection)
                {
                    summary = Loc.Get("activity_summary_no_selection");
                    return true;
                }
                return false;
            }

            AssetCharacter character = CharacterManager.Instance?
                .GetCharacterByGUID(characterGuid);
            if (character?.Data == null)
            {
                if (includeNoSelection)
                {
                    summary = Loc.Get("activity_summary_unavailable");
                    return true;
                }
                return false;
            }

            string subject = string.IsNullOrWhiteSpace(character.Data.FullName)
                ? Loc.Get("state_selected_para")
                : ScreenReader.NormalizeForOutput(character.Data.FullName.Trim());
            var running = new List<string>();
            var queued = new List<string>();
            var ending = new List<string>();
            var runningSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queuedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var endingSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int unresolved = 0;

            List<AssetCharacterDataInteraction> interactions =
                character.Data.CurrentInteractionsInQueue;
            if (interactions != null)
            {
                foreach (AssetCharacterDataInteraction interaction in interactions)
                {
                    if (interaction == null)
                    {
                        continue;
                    }

                    bool isEnding = interaction.IsCancelling
                        || interaction.State == AssetCharacterDataInteractionState.ToBeCanceled
                        || interaction.State == AssetCharacterDataInteractionState.Cancelling;
                    bool isRunning = interaction.State
                        == AssetCharacterDataInteractionState.Running;
                    bool isQueued = interaction.State
                            == AssetCharacterDataInteractionState.Pending
                        || interaction.State
                            == AssetCharacterDataInteractionState.ToBeStarted;
                    if (!isEnding && !isRunning && !isQueued)
                    {
                        // A natural completion can occupy ToBeDeleted for one frame. It is no
                        // longer current or queued and must not create an "unknown activity".
                        continue;
                    }

                    string name = GetInteractionName(interaction.InteractionSettingGUID);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        // Never expose a raw setting GUID as if it were a useful activity name.
                        unresolved++;
                        continue;
                    }

                    if (isEnding)
                    {
                        AddDistinct(ending, endingSet, name);
                    }
                    else if (isRunning)
                    {
                        AddDistinct(running, runningSet, name);
                    }
                    else if (isQueued)
                    {
                        AddDistinct(queued, queuedSet, name);
                    }
                    // ToBeDeleted is already terminal. Its player-requested lifecycle is reported
                    // by StateReportingHandler; including it here would call a completed action
                    // "ending" during the one-frame deletion boundary.
                }
            }

            if (running.Count == 0 && queued.Count == 0 && ending.Count == 0)
            {
                summary = unresolved == 0
                    ? Loc.Get("activity_summary_idle", subject)
                    : Loc.Get("activity_summary_names_unavailable", subject);
                return true;
            }

            var parts = new List<string>();
            if (running.Count > 0)
            {
                parts.Add(Loc.Get("activity_summary_running", JoinNames(running)));
            }
            if (queued.Count > 0)
            {
                parts.Add(Loc.Get("activity_summary_queued", JoinNames(queued)));
            }
            if (ending.Count > 0)
            {
                parts.Add(Loc.Get("activity_summary_ending", JoinNames(ending)));
            }
            if (unresolved > 0)
            {
                parts.Add(Loc.Get("activity_summary_some_names_unavailable"));
            }

            summary = Loc.Get(
                "activity_summary",
                subject,
                string.Join(Loc.Get("list_separator"), parts.ToArray()));
            return true;
        }

        private static string GetInteractionName(ulong interactionGuid)
        {
            InteractionUnit interaction = Settings.Get<Interactions>()?
                .GetInteractionByGUID(interactionGuid);
            if (interaction == null)
            {
                return null;
            }

            string translated = TranslationManager.Get(interaction.TranslationKey);
            string name = string.IsNullOrWhiteSpace(translated)
                || translated == interaction.TranslationKey
                    ? interaction.DisplayName
                    : translated;
            return string.IsNullOrWhiteSpace(name)
                ? null
                : ScreenReader.NormalizeForOutput(name.Trim());
        }

        private static void AddDistinct(
            List<string> names, HashSet<string> seen, string name)
        {
            if (seen.Add(name))
            {
                names.Add(name);
            }
        }

        private static string JoinNames(List<string> names)
        {
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

        private static Player GetPlayer()
        {
            return PlayerManager.Instance?.HybridPlayer1?.Player;
        }

        private static void ResetForLoading()
        {
            _wasInGame = false;
            _selectedCharacterGuid = 0UL;
            _pendingSnapshotGuid = 0UL;
            _snapshotReadyAt = 0f;
        }
    }
}
