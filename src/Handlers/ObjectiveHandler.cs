using System;
using System.Text;
using ParalivesAccess.Speech;
using Setting;

namespace ParalivesAccess.Handlers
{
    /// <summary>
    /// Announces meaningful changes to tutorial guidance, the tracked goal and active wants.
    /// </summary>
    /// <remarks>
    /// The first observed state is deliberately only a baseline: load/window announcements already
    /// describe what is on screen. Later model-state changes settle for three game frames before
    /// they are read, matching the pooled UI's deferred refresh without polling spoken text that can
    /// contain countdowns. The signature uses identities and statuses, so a ticking timer cannot
    /// produce a stream of announcements.
    /// </remarks>
    internal static class ObjectiveHandler
    {
        private const int SettleFrames = 3;

        private static bool _hasBaseline;
        private static string _committedSignature;
        private static string _pendingSignature;
        private static int _framesRemaining;
        private static string _lastSummary;

        /// <summary>Observes the selected character's objective state once per game frame.</summary>
        internal static void Tick()
        {
            try
            {
                if (GameLoadingManager.State != State.Game
                    || PlayerManager.Instance == null || AssetManager.Instance == null)
                {
                    Reset();
                    return;
                }

                // The train intro has its own ordered state observer. Letting the ordinary
                // goal/want observer run there would announce the same want transition twice.
                if (PlayerManager.Instance.HybridPlayer1?.Player?.IsInIntro == true)
                {
                    Reset();
                    return;
                }

                ulong characterGuid =
                    PlayerManager.Instance.HybridPlayer1.Player.GetSelectedCharacterGUID();
                AssetCharacter character = characterGuid == 0UL
                    ? null
                    : AssetManager.Instance.GetCharacter(characterGuid);
                if (character?.Data == null)
                {
                    Reset();
                    return;
                }

                string current = BuildSignature(character);
                if (!_hasBaseline)
                {
                    _hasBaseline = true;
                    _committedSignature = current;
                    _lastSummary = UiLabelReader.GetCurrentObjectiveSummary(character);
                    return;
                }

                if (current == _committedSignature)
                {
                    _pendingSignature = null;
                    _framesRemaining = 0;
                    return;
                }

                if (current != _pendingSignature)
                {
                    _pendingSignature = current;
                    _framesRemaining = SettleFrames;
                    return;
                }

                if (--_framesRemaining > 0)
                {
                    return;
                }

                _committedSignature = current;
                _pendingSignature = null;

                string summary = UiLabelReader.GetCurrentObjectiveSummary(character);
                if (summary == _lastSummary)
                {
                    return;
                }

                _lastSummary = summary;
                string spoken = string.IsNullOrEmpty(summary)
                    ? Loc.Get("objectives_none")
                    : Loc.Get("objectives_updated", summary);
                Plugin.Log.LogInfo("[Objectives] " + spoken);
                ScreenReader.Speak(spoken);
            }
            catch (Exception ex)
            {
                // This is called from SystemManager.Update; no mod exception may escape into it.
                Plugin.Log.LogError("ObjectiveHandler.Tick threw: " + ex);
            }
        }

        private static string BuildSignature(AssetCharacter character)
        {
            var signature = new StringBuilder();
            signature.Append(character.GUID).Append('|');

            UISkillsInProgressAndUpcomingEvents panel =
                UI.GetOrNull<UISkillsInProgressAndUpcomingEvents>(0);
            signature.Append(panel?.UITutorialOverlayFirstBill?.IsVisible == true ? '1' : '0');
            signature.Append(panel?.UITutorialOverlayStorycard?.IsVisible == true ? '1' : '0');
            signature.Append('|').Append(character.Data.TrackedGoal);

            if (character.Data.TrackedGoal != 0UL && GoalsManager.Instance != null)
            {
                AssetCharacterGoalData goal = GoalsManager.Instance.GetGoalDataFromGoalGUID(
                    character, character.Data.TrackedGoal);
                if (goal != null)
                {
                    signature.Append(':').Append(goal.HasBeenCompleted ? '1' : '0');
                    signature.Append(goal.IsCompletedAndAllRewardsClaimed ? '1' : '0');
                    if (goal.Objectives != null)
                    {
                        foreach (AssetCharacterGoalObjectiveData objective in goal.Objectives)
                        {
                            if (objective == null)
                            {
                                continue;
                            }

                            signature.Append(':').Append(objective.GUID);
                            if (objective.WantData != null)
                            {
                                signature.Append(',').Append((int)objective.WantData.Status);
                            }
                        }
                    }
                }
            }

            Wants wants = Settings.Get<Wants>();
            if (character.Data.Wants != null)
            {
                foreach (AssetCharacterWantData data in character.Data.Wants)
                {
                    if (data == null)
                    {
                        continue;
                    }

                    Want want = wants?.GetWantByGUID(data.WantGUID);
                    if (want == null || want.WantType != WantTypes.Want)
                    {
                        continue;
                    }

                    signature.Append('|').Append(data.WantGUID)
                        .Append(',').Append((int)data.Status)
                        .Append(',').Append(data.CharacterTargetGUID)
                        .Append(',').Append(data.SkillGUID)
                        .Append(',').Append(data.SkinGUID)
                        .Append(',').Append(data.CatalogueGUID);
                }
            }

            return signature.ToString();
        }

        private static void Reset()
        {
            _hasBaseline = false;
            _committedSignature = null;
            _pendingSignature = null;
            _framesRemaining = 0;
            _lastSummary = null;
        }
    }
}
