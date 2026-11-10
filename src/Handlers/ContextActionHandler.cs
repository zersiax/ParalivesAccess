using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ParalivesAccess.Speech;
using Setting;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ParalivesAccess.Handlers
{
    /// <summary>
    /// Gives transient, time-sensitive game actions one stable keyboard destination without moving
    /// focus merely because the action became available.
    /// </summary>
    /// <remarks>
    /// The first supported context is the Together Bar. The native UI represents every offered
    /// Together Card as its own nested FocusableGroup while the persistent HUD remains visible.
    /// That is faithful to the visual card layout but makes the ordinary panel ring needlessly
    /// expensive. Once the player explicitly asks to respond, this handler presents all cards—and
    /// any subsequent target choices—as one ordered VirtualPanel. The game still owns every card,
    /// validity rule, click action and result.
    /// </remarks>
    public static class ContextActionHandler
    {
        private enum PanelKind
        {
            None,
            ReadyConversations,
            ConversationCards,
            ConversationTargets
        }

        private sealed class ReadyConversation
        {
            public UICharactersTogetherBar Bar;
            public SocialGroup Group;
        }

        public static bool Enabled { get; set; } = true;

        /// <summary>Configurable key that acts on the currently announced context.</summary>
        public static Key ActionKey { get; set; } = Key.F4;

        /// <summary>True while a contextual choice flow owns arrows and Enter.</summary>
        public static bool OwnsInput => _panel != null || _waitingForChoices;

        internal static bool IsConversationPanelOpen =>
            _panel != null && (_panelKind == PanelKind.ConversationCards
                || _panelKind == PanelKind.ConversationTargets);

        /// <summary>Supplies the active virtual location to the global F3 context report.</summary>
        internal static bool TryGetContext(out string panelName, out string element)
        {
            panelName = null;
            element = null;
            if (_panel != null)
            {
                panelName = _panel.Name;
                element = _panel.Current?.Describe();
                return true;
            }
            if (_waitingForChoices)
            {
                panelName = Loc.Get("context_choices_panel");
                element = Loc.Get("context_action_opening");
                return true;
            }
            return false;
        }

        private const int ChoiceSettleFrames = 12;

        private static readonly List<ReadyConversation> ReadyBuffer =
            new List<ReadyConversation>();

        private static FieldInfo _togetherBarGroupField;
        private static FieldInfo _charactersTogetherGroupField;
        private static FieldInfo _charactersTargetCardField;
        private static FieldInfo _charactersTargetGroupField;
        private static FieldInfo _charactersTargetUserField;
        private static FieldInfo _charactersLastCardField;
        private static FieldInfo _cardCharacterField;
        private static FieldInfo _cardSocialGroupField;
        private static FieldInfo _cardForcedCharactersField;
        private static FieldInfo _cardTargetsField;
        private static FieldInfo _cardVariantsField;
        private static FieldInfo _successWheelBigStakesField;
        private static bool _reflectionResolved;

        private static VirtualPanel _panel;
        private static PanelKind _panelKind;
        private static UICharacters _characters;
        private static bool _waitingForChoices;
        private static int _choiceFramesRemaining;
        private static bool _enterPressed;
        private static ulong _activeCardGuid;
        private static ulong _activeCardUserGuid;
        private static ulong _activeCardGroupGuid;
        private static string _activeCardName;
        private static bool _successWheelWasVisible;

        /// <summary>Called once per frame before the general panel navigator.</summary>
        public static void Tick()
        {
            if (!Enabled)
            {
                Reset();
                return;
            }

            try
            {
                HybridPlayer hybridPlayer = PlayerManager.Instance != null
                    ? PlayerManager.Instance.HybridPlayer1
                    : null;
                Keyboard keyboard = Keyboard.current;
                if (hybridPlayer?.Player == null || keyboard == null)
                {
                    Reset();
                    _successWheelWasVisible = false;
                    return;
                }

                // The native wheel owns this short modal boundary. In particular, do not rebuild
                // the card panel after target selection and immediately interrupt the stakes cue.
                if (ObserveSuccessChanceWheel())
                {
                    return;
                }

                if (hybridPlayer.HasInputFieldFocused)
                {
                    _enterPressed = false;
                    return;
                }

                bool actionPressed = ModInputKey.WasPressed(keyboard, ActionKey);
                if (actionPressed)
                {
                    HandleActionKey(hybridPlayer);
                    return;
                }

                if (_waitingForChoices)
                {
                    ObserveOpenedChoices(hybridPlayer.Player);
                    return;
                }

                UICharacters characters = UI.GetOrNull<UICharacters>(0);
                if (_panel == null)
                {
                    // A mouse click, tutorial-era Enter route, or another native path may open the
                    // same choice mode. Coalesce it too, but only after the player/game opened it.
                    if (characters != null && characters.IsTogetherCardsMode)
                    {
                        _characters = characters;
                        BuildPanelForCurrentMode(hybridPlayer.Player);
                    }
                    return;
                }

                if (!SynchronizePanel(hybridPlayer.Player, characters))
                {
                    return;
                }

                HandlePanelInput(keyboard);
            }
            catch (Exception ex)
            {
                Enabled = false;
                Reset();
                Plugin.Log.LogError("ContextActionHandler disabled after an exception: " + ex);
                ScreenReader.Speak(Loc.Get("context_action_failed"));
            }
        }

        private static void HandleActionKey(HybridPlayer hybridPlayer)
        {
            string conflict;
            if (!ModInputKey.IsAvailable(hybridPlayer, ActionKey, out conflict))
            {
                Speak(conflict);
                return;
            }

            if (_panel != null)
            {
                if (_panelKind == PanelKind.ReadyConversations)
                {
                    Reset();
                    Speak(Loc.Get("context_action_closed"));
                    return;
                }
                _panel.AnnounceCurrent();
                return;
            }

            if (_waitingForChoices)
            {
                Speak(Loc.Get("context_action_opening"));
                return;
            }

            if (OccupationHandler.OwnsInput
                || BuildCatalogHandler.OwnsInput || ItemPlacementHandler.OwnsInput
                || WallConstructionHandler.OwnsInput
                || FloorCoveringHandler.OwnsInput
                || LotObjectHandler.OwnsInput || NameEntryHandler.OwnsInput
                || TownMapLotHandler.OwnsInput)
            {
                Speak(Loc.Get("context_action_busy"));
                return;
            }

            UICharacters characters = UI.GetOrNull<UICharacters>(0);
            if (characters != null && characters.IsTogetherCardsMode)
            {
                _characters = characters;
                if (!BuildPanelForCurrentMode(hybridPlayer.Player))
                {
                    Speak(Loc.Get("context_action_opening"));
                }
                return;
            }

            List<ReadyConversation> ready = FindReadyConversations(characters);
            if (ready.Count == 0)
            {
                Speak(Loc.Get("context_action_none"));
                return;
            }

            ReadyConversation preferred = FindSelectedConversation(
                ready, hybridPlayer.Player);
            if (preferred != null || ready.Count == 1)
            {
                OpenConversation(preferred ?? ready[0], characters);
                return;
            }

            var panel = new VirtualPanel(Loc.Get("context_conversations_panel"));
            foreach (ReadyConversation conversation in ready)
            {
                panel.Add(new ReadyConversationEntry(conversation, characters));
            }

            _panel = panel;
            _panelKind = PanelKind.ReadyConversations;
            _characters = characters;
            _enterPressed = false;
            _panel.Enter();
        }

        private static void OpenConversation(
            ReadyConversation conversation, UICharacters characters)
        {
            if (conversation?.Bar == null || !IsReady(conversation.Bar))
            {
                Speak(Loc.Get("context_action_gone"));
                Reset();
                return;
            }

            _panel = null;
            _panelKind = PanelKind.None;
            _characters = characters;
            _enterPressed = false;
            conversation.Bar.OpenTogetherChoices();
            _waitingForChoices = true;
            _choiceFramesRemaining = ChoiceSettleFrames;
            Plugin.Log.LogInfo(
                "[ContextAction] Opened the native Together Card mode from the ready bar; "
              + $"group={conversation.Group?.GUID ?? 0UL}, key={ActionKey}.");
        }

        private static void ObserveOpenedChoices(Player player)
        {
            _characters = UI.GetOrNull<UICharacters>(0);
            if (_characters != null && _characters.IsTogetherCardsMode
                && BuildPanelForCurrentMode(player))
            {
                _waitingForChoices = false;
                _choiceFramesRemaining = 0;
                return;
            }

            _choiceFramesRemaining--;
            if (_choiceFramesRemaining > 0)
            {
                return;
            }

            _waitingForChoices = false;
            Plugin.Log.LogWarning(
                "[ContextAction] Together Card mode did not expose a choice within the bounded settle window.");
            Speak(Loc.Get("context_action_choices_missing"));
        }

        private static bool SynchronizePanel(Player player, UICharacters characters)
        {
            if (_panelKind == PanelKind.ReadyConversations)
            {
                if (_panel.AvailableCount > 0)
                {
                    return true;
                }

                Reset();
                Speak(Loc.Get("context_action_gone"));
                return false;
            }

            if (characters == null || !characters.IsTogetherCardsMode
                || characters.IsShowingOutcomes)
            {
                Reset();
                return false;
            }

            _characters = characters;
            PanelKind expected = characters.IsInTargetCharacterSelectionMode
                ? PanelKind.ConversationTargets
                : PanelKind.ConversationCards;
            if (_panelKind != expected || _panel.AvailableCount == 0)
            {
                if (!BuildPanelForCurrentMode(player))
                {
                    // The game's next Update may still be repopulating a pooled list. Release the
                    // stale virtual cursor now; the auto-open path retries on the next frame.
                    _panel = null;
                    _panelKind = PanelKind.None;
                    _enterPressed = false;
                    return false;
                }
            }

            return true;
        }

        private static bool BuildPanelForCurrentMode(Player player)
        {
            if (_characters == null || !_characters.IsTogetherCardsMode)
            {
                return false;
            }

            return _characters.IsInTargetCharacterSelectionMode
                ? BuildTargetPanel(player)
                : BuildCardPanel(player);
        }

        private static bool BuildCardPanel(Player player)
        {
            var panel = new VirtualPanel(GetConversationPanelName(_characters));
            var seen = new HashSet<int>();
            _activeCardGuid = 0UL;
            _activeCardUserGuid = 0UL;
            _activeCardGroupGuid = 0UL;
            _activeCardName = null;
            foreach (UITogetherCard card in
                _characters.GetComponentsInChildren<UITogetherCard>(includeInactive: false))
            {
                if (card == null || !card.gameObject.activeInHierarchy || card.CardGUID == 0UL
                    || !seen.Add(card.GetInstanceID()))
                {
                    continue;
                }
                panel.Add(new TogetherCardEntry(card));
                if (card.ChangeVariantButton != null
                    && card.ChangeVariantButton.activeInHierarchy)
                {
                    panel.Add(new TogetherCardVariantEntry(card));
                }
                LogCardDiagnostics(card);
            }

            Focusable ok = _characters.TogetherCardsOKButton != null
                ? _characters.TogetherCardsOKButton.GetComponent<Focusable>()
                : null;
            if (ok == null && _characters.TogetherCardsOKButton != null)
            {
                ok = _characters.TogetherCardsOKButton
                    .GetComponentInChildren<Focusable>(includeInactive: false);
            }
            if (ok != null && ok.gameObject.activeInHierarchy)
            {
                panel.Add(new FocusableEntry(ok));
            }

            if (panel.AvailableCount == 0)
            {
                return false;
            }

            _panel = panel;
            _panelKind = PanelKind.ConversationCards;
            _enterPressed = false;
            EnterPanel(player);
            Plugin.Log.LogInfo(
                $"[ContextAction] Coalesced {_panel.AvailableCount} Together Card controls into one panel.");
            return true;
        }

        private static bool BuildTargetPanel(Player player)
        {
            CaptureTargetContext();
            var panel = new VirtualPanel(GetTargetPanelName());
            var seen = new HashSet<ulong>();
            foreach (UICharacterListItem item in
                _characters.GetComponentsInChildren<UICharacterListItem>(includeInactive: false))
            {
                if (item == null || item.CharacterGUID == 0UL
                    || !item.gameObject.activeInHierarchy
                    || (item.GreyedOut != null && item.GreyedOut.activeInHierarchy)
                    || !seen.Add(item.CharacterGUID))
                {
                    continue;
                }
                panel.Add(new CharacterTargetEntry(item));
            }

            if (panel.AvailableCount == 0)
            {
                return false;
            }

            _panel = panel;
            _panelKind = PanelKind.ConversationTargets;
            _enterPressed = false;
            EnterPanel(player);
            Plugin.Log.LogInfo(
                $"[ContextAction] Coalesced {_panel.AvailableCount} conversation targets into one panel.");
            return true;
        }

        private static void EnterPanel(Player player)
        {
            if (player?.IsInIntro == true)
            {
                // TutorialHandler owns the surrounding prompt and choice summary. A second
                // interrupting panel announcement would cut that sentence off.
                _panel.EnterSilently();
            }
            else
            {
                _panel.Enter();
            }
        }

        private static string GetConversationPanelName(UICharacters characters)
        {
            string prompt = characters?.NPCInitiatives != null
                && characters.NPCInitiatives.activeInHierarchy
                    ? characters.NPCInitiativeLabel?.Text?.text
                    : null;
            string panelName = string.IsNullOrWhiteSpace(prompt)
                ? Loc.Get("context_choices_panel")
                : Loc.Get("context_replies_panel", prompt.Trim());
            return AddConversationParticipants(panelName, GetConversationGroupGuid(characters));
        }

        private static string GetTargetPanelName()
        {
            string panelName = string.IsNullOrWhiteSpace(_activeCardName)
                ? Loc.Get("context_targets_panel")
                : Loc.Get("context_targets_for_card", _activeCardName);
            return AddConversationParticipants(panelName, _activeCardGroupGuid);
        }

        private static string AddConversationParticipants(string panelName, ulong groupGuid)
        {
            SocialGroup group = groupGuid != 0UL
                ? SocialGroupManager.Instance?.GetSocialGroupByGUID(groupGuid)
                : null;
            if (group?.CharactersInGroup == null)
            {
                return panelName;
            }

            var participants = new List<string>();
            foreach (ulong guid in group.CharactersInGroup)
            {
                string context = UiLabelReader.GetCharacterConversationContext(guid);
                if (!string.IsNullOrWhiteSpace(context) && !participants.Contains(context))
                {
                    participants.Add(context);
                }
            }

            return participants.Count == 0
                ? panelName
                : Loc.Get("context_panel_with_participants", panelName,
                    string.Join(Loc.Get("list_separator"), participants.ToArray()));
        }

        private static ulong GetConversationGroupGuid(UICharacters characters)
        {
            ResolveReflection();
            if (characters != null && _charactersTogetherGroupField != null)
            {
                object value = _charactersTogetherGroupField.GetValue(characters);
                if (value is ulong guid && guid != 0UL)
                {
                    return guid;
                }
            }

            if (characters != null && _cardSocialGroupField != null)
            {
                foreach (UITogetherCard card in characters
                    .GetComponentsInChildren<UITogetherCard>(includeInactive: false))
                {
                    if (card != null && card.CardGUID != 0UL)
                    {
                        return (ulong)_cardSocialGroupField.GetValue(card);
                    }
                }
            }

            return 0UL;
        }

        private static void HandlePanelInput(Keyboard keyboard)
        {
            if (_panelKind == PanelKind.ReadyConversations
                && keyboard.backspaceKey.wasPressedThisFrame)
            {
                Reset();
                Speak(Loc.Get("context_action_closed"));
                return;
            }

            if (keyboard.downArrowKey.wasPressedThisFrame
                || keyboard.rightArrowKey.wasPressedThisFrame)
            {
                _panel.Move(1);
                return;
            }
            if (keyboard.upArrowKey.wasPressedThisFrame
                || keyboard.leftArrowKey.wasPressedThisFrame)
            {
                _panel.Move(-1);
                return;
            }

            bool pressed = keyboard.enterKey.wasPressedThisFrame
                || keyboard.numpadEnterKey.wasPressedThisFrame;
            bool released = keyboard.enterKey.wasReleasedThisFrame
                || keyboard.numpadEnterKey.wasReleasedThisFrame;
            if (pressed)
            {
                _enterPressed = true;
            }
            if (released && _enterPressed)
            {
                _enterPressed = false;
                _panel.Activate();
            }
        }

        private static List<ReadyConversation> FindReadyConversations(
            UICharacters characters)
        {
            ReadyBuffer.Clear();
            if (characters?.List?.CurrentItems == null)
            {
                return ReadyBuffer;
            }

            ResolveReflection();
            foreach (UIListItemBase item in characters.List.CurrentItems)
            {
                var bar = item as UICharactersTogetherBar;
                if (!IsReady(bar))
                {
                    continue;
                }

                SocialGroup group = _togetherBarGroupField != null
                    ? _togetherBarGroupField.GetValue(bar) as SocialGroup
                    : null;
                ReadyBuffer.Add(new ReadyConversation { Bar = bar, Group = group });
            }

            return ReadyBuffer;
        }

        private static ReadyConversation FindSelectedConversation(
            List<ReadyConversation> ready, Player player)
        {
            if (player?.SelectedCharactersGUID == null)
            {
                return null;
            }

            ReadyConversation match = null;
            foreach (ReadyConversation conversation in ready)
            {
                if (conversation.Group?.CharactersInGroup == null)
                {
                    continue;
                }
                foreach (ulong selected in player.SelectedCharactersGUID)
                {
                    if (!conversation.Group.CharactersInGroup.Contains(selected))
                    {
                        continue;
                    }
                    if (match != null)
                    {
                        // More than one selected conversation: require an explicit panel choice.
                        return null;
                    }
                    match = conversation;
                    break;
                }
            }
            return match;
        }

        private static bool IsReady(UICharactersTogetherBar bar)
        {
            return bar != null && bar.gameObject.activeInHierarchy
                && bar.ButtonBarFull != null && bar.ButtonBarFull.activeInHierarchy;
        }

        private static string DescribeConversation(ReadyConversation conversation)
        {
            var names = new List<string>();
            if (conversation?.Group?.CharactersInGroup != null)
            {
                foreach (ulong guid in conversation.Group.CharactersInGroup)
                {
                    string name = CharacterManager.Instance?
                        .GetCharacterByGUID(guid)?.Data?.FullName;
                    if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name))
                    {
                        names.Add(name);
                    }
                }
            }

            return names.Count == 0
                ? Loc.Get("context_open_conversation")
                : Loc.Get("context_open_conversation_with",
                    string.Join(Loc.Get("list_comma"), names.ToArray()));
        }

        private static void CaptureActiveCardContext(UITogetherCard card)
        {
            ResolveReflection();
            _activeCardGuid = card?.CardGUID ?? 0UL;
            _activeCardName = GetCardTitle(card);
            _activeCardUserGuid = ReadCardGuid(_cardCharacterField, card);
            _activeCardGroupGuid = ReadCardGuid(_cardSocialGroupField, card);
        }

        private static void CaptureTargetContext()
        {
            ResolveReflection();
            if (_characters == null)
            {
                return;
            }

            _activeCardGuid = ReadCharacterGuid(
                _charactersTargetCardField, _characters, _activeCardGuid);
            _activeCardGroupGuid = ReadCharacterGuid(
                _charactersTargetGroupField, _characters, _activeCardGroupGuid);
            _activeCardUserGuid = ReadCharacterGuid(
                _charactersTargetUserField, _characters, _activeCardUserGuid);

            UITogetherCard clicked = _charactersLastCardField?
                .GetValue(_characters) as UITogetherCard;
            if (clicked != null)
            {
                _activeCardName = GetCardTitle(clicked);
            }
            if (string.IsNullOrWhiteSpace(_activeCardName) && _activeCardGuid != 0UL)
            {
                TogetherCard data = Settings.Get<Together>()?
                    .GetCardByGUID(_activeCardGuid);
                if (data != null)
                {
                    string key = "TogetherCard_" + data.DisplayName;
                    string translated = TranslationManager.Get(key);
                    _activeCardName = string.IsNullOrWhiteSpace(translated)
                        || translated == key
                            ? data.DisplayName
                            : translated.Trim();
                }
            }
        }

        private static ulong ReadCharacterGuid(
            FieldInfo field, UICharacters characters, ulong fallback)
        {
            if (field == null || characters == null)
            {
                return fallback;
            }

            object value = field.GetValue(characters);
            return value is ulong guid ? guid : fallback;
        }

        private static ulong ReadCardGuid(FieldInfo field, UITogetherCard card)
        {
            if (field == null || card == null)
            {
                return 0UL;
            }

            object value = field.GetValue(card);
            return value is ulong guid ? guid : 0UL;
        }

        private static string GetCardTitle(UITogetherCard card)
        {
            string title = card?.Label?.Text?.text;
            return string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        }

        private static List<ulong> GetCardGuidList(
            FieldInfo field, UITogetherCard card)
        {
            ResolveReflection();
            return field?.GetValue(card) as List<ulong> ?? new List<ulong>();
        }

        private static List<TogetherCard> GetAvailableVariants(UITogetherCard card)
        {
            ResolveReflection();
            var variants = _cardVariantsField?.GetValue(card) as List<TogetherCard>;
            if (variants != null)
            {
                return variants;
            }

            var available = new List<TogetherCard>();
            Together together = Settings.Get<Together>();
            if (card == null || together == null || TogetherManager.Instance == null)
            {
                return available;
            }

            ulong groupGuid = ReadCardGuid(_cardSocialGroupField, card);
            ulong characterGuid = ReadCardGuid(_cardCharacterField, card);
            foreach (TogetherCard variant in together.GetVariantsForCard(card.CardGUID))
            {
                if (variant != null && TogetherManager.Instance.CanVariantBeChosen(
                    variant, groupGuid, characterGuid))
                {
                    available.Add(variant);
                }
            }
            return available;
        }

        private static string DescribeCard(UITogetherCard card)
        {
            string description = UiLabelReader.GetLabelOrFallback(card);
            List<ulong> forced = GetCardGuidList(_cardForcedCharactersField, card);
            string forcedNames = FormatCharacterNames(forced);
            return string.IsNullOrWhiteSpace(forcedNames)
                ? description
                : Loc.Get("together_card_forced_participants", description, forcedNames);
        }

        private static string DescribeTarget(UICharacterListItem item)
        {
            string description = UiLabelReader.GetLabelOrFallback(item);
            bool hasVisibleChance = item?.SuccessChanceContainer != null
                && item.SuccessChanceContainer.activeInHierarchy;
            if (hasVisibleChance || item == null || _activeCardGuid == 0UL
                || _activeCardGroupGuid == 0UL || TogetherManager.Instance == null)
            {
                return description;
            }

            try
            {
                var chance = TogetherManager.Instance.GetSuccessChanceForSpecificTargetOnCard(
                    _activeCardGroupGuid, _activeCardGuid,
                    _activeCardUserGuid, item.CharacterGUID);
                if (chance.Item1)
                {
                    string percent = Mathf.Min(100f, chance.Item2).ToString("0.#") + "%";
                    return Loc.Get("character_together_chance", description, percent);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError(
                    $"Reading target-specific chance for {item.CharacterGUID} threw: {ex}");
            }

            return description;
        }

        private static void LogCardDiagnostics(UITogetherCard card)
        {
            try
            {
                TogetherCard data = Settings.Get<Together>()?.GetCardByGUID(card.CardGUID);
                if (data == null)
                {
                    Plugin.Log.LogWarning(
                        $"[ContextAction/card] GUID {card.CardGUID} has no TogetherCard data.");
                    return;
                }

                ulong groupGuid = ReadCardGuid(_cardSocialGroupField, card);
                ulong userGuid = ReadCardGuid(_cardCharacterField, card);
                List<ulong> explicitTargets = GetCardGuidList(_cardTargetsField, card);
                var resolvedTargets = new List<ulong>(explicitTargets);
                if (data.TargetsAnotherCharacter && resolvedTargets.Count == 0
                    && TogetherManager.Instance != null)
                {
                    resolvedTargets = TogetherManager.Instance
                        .GetCharactersThatCanBeTargettedForCard(
                            groupGuid, data.GUID, userGuid) ?? new List<ulong>();
                }

                List<ContextRequirement> requirements = TogetherManager.Instance?
                    .GetAllCardRequirements(data, onlyGetMultipleRequirements: false);
                var requirementText = new List<string>();
                if (requirements != null)
                {
                    foreach (ContextRequirement requirement in requirements)
                    {
                        requirementText.Add(DescribeRequirement(requirement));
                    }
                }

                var variantText = new List<string>();
                foreach (TogetherCard variant in GetAvailableVariants(card))
                {
                    if (variant != null)
                    {
                        variantText.Add($"{variant.DisplayName}({variant.GUID})");
                    }
                }

                Together together = Settings.Get<Together>();
                ulong categoryGuid = data.Category != 0UL
                    ? data.Category
                    : together?.DefaultCategory ?? 0UL;
                TogetherCardCategory category = categoryGuid != 0UL
                    ? together?.GetCategoryByGUID(categoryGuid)
                    : null;
                string categoryText = category != null
                    ? $"{category.DisplayName}({categoryGuid})"
                    : categoryGuid.ToString();

                Plugin.Log.LogInfo(
                    $"[ContextAction/card] name='{GetCardTitle(card)}', GUID={data.GUID}, "
                  + $"type={data.CardType}, category={categoryText}, "
                  + $"group={groupGuid}, user={userGuid}, "
                  + $"targetsAnother={data.TargetsAnotherCharacter}, "
                  + $"targets=[{FormatCharacterGuids(resolvedTargets)}], "
                  + $"forced=[{FormatCharacterGuids(GetCardGuidList(_cardForcedCharactersField, card))}], "
                  + $"requirements=[{string.Join("; ", requirementText.ToArray())}], "
                  + $"HasSuccessChance={data.HasSuccessChance}, IsImportant={data.IsImportant}, "
                  + $"variants=[{string.Join("; ", variantText.ToArray())}], "
                  + $"ForceAppearing={data.ForceAppearing}, weightGUID={data.CardWeight}.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError(
                    $"Logging Together Card {card?.CardGUID ?? 0UL} diagnostics threw: {ex}");
            }
        }

        private static string DescribeRequirement(ContextRequirement requirement)
        {
            if (requirement == null)
            {
                return "null";
            }

            var fields = new List<string>
            {
                requirement.Type.ToString(),
                requirement.MustBeTrue ? "must=true" : "must=false",
                $"rule={requirement.Rule}",
                $"value={requirement.Value}"
            };
            AddGuidDiagnostic(fields, "trait", requirement.PersonalityTrait);
            AddGuidDiagnostic(fields, "item", requirement.CatalogueItem);
            AddGuidDiagnostic(fields, "emotion", requirement.Emotion);
            AddGuidDiagnostic(fields, "need", requirement.Need);
            AddGuidDiagnostic(fields, "relationship", requirement.RelationshipLabel);
            AddGuidDiagnostic(fields, "skill", requirement.Skill);
            AddGuidDiagnostic(fields, "status", requirement.StatusEffect);
            AddGuidDiagnostic(fields, "conversationTag", requirement.ConversationTag);
            return string.Join(",", fields.ToArray());
        }

        private static void AddGuidDiagnostic(
            List<string> fields, string label, ulong guid)
        {
            if (guid != 0UL)
            {
                fields.Add($"{label}={guid}");
            }
        }

        private static string FormatCharacterGuids(List<ulong> guids)
        {
            if (guids == null || guids.Count == 0)
            {
                return string.Empty;
            }

            var values = new List<string>();
            foreach (ulong guid in guids)
            {
                string name = CharacterManager.Instance?
                    .GetCharacterByGUID(guid)?.Data?.FullName;
                values.Add(string.IsNullOrWhiteSpace(name)
                    ? guid.ToString()
                    : $"{name.Trim()}({guid})");
            }
            return string.Join(", ", values.ToArray());
        }

        private static string FormatCharacterNames(List<ulong> guids)
        {
            if (guids == null || guids.Count == 0)
            {
                return string.Empty;
            }

            var values = new List<string>();
            foreach (ulong guid in guids)
            {
                string name = CharacterManager.Instance?
                    .GetCharacterByGUID(guid)?.Data?.FullName;
                if (!string.IsNullOrWhiteSpace(name) && !values.Contains(name.Trim()))
                {
                    values.Add(name.Trim());
                }
            }
            return string.Join(Loc.Get("list_comma"), values.ToArray());
        }

        private static bool ObserveSuccessChanceWheel()
        {
            UISuccessChanceWheel wheel = UI.GetOrNull<UISuccessChanceWheel>(0);
            bool visible = wheel != null && wheel.IsVisible;
            if (!visible)
            {
                _successWheelWasVisible = false;
                return false;
            }
            if (_successWheelWasVisible)
            {
                return true;
            }

            _successWheelWasVisible = true;
            ResolveReflection();
            bool bigStakes = _successWheelBigStakesField != null
                && (bool)_successWheelBigStakesField.GetValue(wheel);
            if (bigStakes)
            {
                Speak(Loc.Get("context_high_stakes_check"));
            }
            return true;
        }

        private static void ResolveReflection()
        {
            if (_reflectionResolved)
            {
                return;
            }
            _reflectionResolved = true;
            _togetherBarGroupField = AccessTools.Field(
                typeof(UICharactersTogetherBar), "_socialGroup");
            _charactersTogetherGroupField = AccessTools.Field(
                typeof(UICharacters), "_togetherCardsGroupGUID");
            _charactersTargetCardField = AccessTools.Field(
                typeof(UICharacters), "_targettingCardGUID");
            _charactersTargetGroupField = AccessTools.Field(
                typeof(UICharacters), "_targettingSocialGroupGUID");
            _charactersTargetUserField = AccessTools.Field(
                typeof(UICharacters), "_targettingCharacterUserGUID");
            _charactersLastCardField = AccessTools.Field(
                typeof(UICharacters), "_lastCardClicked");
            _cardCharacterField = AccessTools.Field(
                typeof(UITogetherCard), "_characterGUID");
            _cardSocialGroupField = AccessTools.Field(
                typeof(UITogetherCard), "_socialGroupGUID");
            _cardForcedCharactersField = AccessTools.Field(
                typeof(UITogetherCard), "_charactersForced");
            _cardTargetsField = AccessTools.Field(
                typeof(UITogetherCard), "_targets");
            _cardVariantsField = AccessTools.Field(
                typeof(UITogetherCard), "_variants");
            _successWheelBigStakesField = AccessTools.Field(
                typeof(UISuccessChanceWheel), "_areStakesBig");
            if (_togetherBarGroupField == null)
            {
                Plugin.Log.LogWarning(
                    "ContextActionHandler could not resolve UICharactersTogetherBar._socialGroup; "
                  + "a single ready conversation remains actionable, but multiple bars cannot be named.");
            }
            if (_charactersTogetherGroupField == null || _charactersTargetCardField == null
                || _charactersTargetGroupField == null || _charactersTargetUserField == null
                || _charactersLastCardField == null || _cardCharacterField == null
                || _cardSocialGroupField == null || _cardForcedCharactersField == null
                || _cardTargetsField == null || _cardVariantsField == null
                || _successWheelBigStakesField == null)
            {
                Plugin.Log.LogWarning(
                    "One or more Together-card context fields could not be resolved; visible "
                  + "choices remain actionable, but some participant, target, variant, or "
                  + "high-stakes detail will be omitted.");
            }
        }

        private static void Reset()
        {
            _panel = null;
            _panelKind = PanelKind.None;
            _characters = null;
            _waitingForChoices = false;
            _choiceFramesRemaining = 0;
            _enterPressed = false;
            _activeCardGuid = 0UL;
            _activeCardUserGuid = 0UL;
            _activeCardGroupGuid = 0UL;
            _activeCardName = null;
            ReadyBuffer.Clear();
        }

        private static void Speak(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }
            Plugin.Log.LogInfo("[ContextAction] " + message);
            ScreenReader.Speak(message);
        }

        private sealed class ReadyConversationEntry : VirtualPanel.IEntry
        {
            private readonly ReadyConversation _conversation;
            private readonly UICharacters _characters;

            public ReadyConversationEntry(
                ReadyConversation conversation, UICharacters characters)
            {
                _conversation = conversation;
                _characters = characters;
            }

            public string Describe() => DescribeConversation(_conversation);

            public bool IsAvailable => IsReady(_conversation?.Bar);

            public void Activate()
            {
                OpenConversation(_conversation, _characters);
            }
        }

        private sealed class TogetherCardEntry : VirtualPanel.IEntry
        {
            private readonly UITogetherCard _card;

            public TogetherCardEntry(UITogetherCard card)
            {
                _card = card;
            }

            public string Describe() => DescribeCard(_card);

            public bool IsAvailable => _card != null && _card.gameObject.activeInHierarchy
                && _card.CardGUID != 0UL && _card.Interactable;

            public void Activate()
            {
                CaptureActiveCardContext(_card);
                ActivateFocusable(_card);
            }
        }

        private sealed class TogetherCardVariantEntry : VirtualPanel.IEntry
        {
            private readonly UITogetherCard _card;

            public TogetherCardVariantEntry(UITogetherCard card)
            {
                _card = card;
            }

            public string Describe()
            {
                int count = Math.Max(2, GetAvailableVariants(_card).Count);
                return Loc.Get("context_change_card_variant",
                    GetCardTitle(_card) ?? Loc.Get("focus_unlabeled"), count);
            }

            public bool IsAvailable => _card != null && _card.gameObject.activeInHierarchy
                && _card.ChangeVariantButton != null
                && _card.ChangeVariantButton.activeInHierarchy;

            public void Activate()
            {
                if (!IsAvailable)
                {
                    Speak(Loc.Get("context_action_gone"));
                    return;
                }

                _card.SwitchVariant();
                string title = GetCardTitle(_card) ?? Loc.Get("focus_unlabeled");
                Plugin.Log.LogInfo(
                    $"[ContextAction/card] Switched native card variant to '{title}', "
                  + $"GUID={_card.CardGUID}.");
                Speak(Loc.Get("context_card_variant_changed", title));
            }
        }

        private sealed class CharacterTargetEntry : VirtualPanel.IEntry
        {
            private readonly UICharacterListItem _item;

            public CharacterTargetEntry(UICharacterListItem item)
            {
                _item = item;
            }

            public string Describe() => DescribeTarget(_item);

            public bool IsAvailable => _item != null && _item.CharacterGUID != 0UL
                && _item.gameObject.activeInHierarchy
                && (_item.GreyedOut == null || !_item.GreyedOut.activeInHierarchy);

            public void Activate()
            {
                ActivateFocusable(_item);
            }
        }

        private sealed class FocusableEntry : VirtualPanel.IEntry
        {
            private readonly Focusable _focusable;

            public FocusableEntry(Focusable focusable)
            {
                _focusable = focusable;
            }

            public string Describe() => UiLabelReader.GetLabelOrFallback(_focusable);

            public bool IsAvailable => _focusable != null
                && _focusable.gameObject.activeInHierarchy;

            public void Activate()
            {
                ActivateFocusable(_focusable);
            }
        }

        private static void ActivateFocusable(Focusable target)
        {
            if (target == null || !target.gameObject.activeInHierarchy)
            {
                Speak(Loc.Get("context_action_gone"));
                return;
            }
            Plugin.Log.LogInfo(
                "[ContextAction] Activating native control '"
              + UiLabelReader.GetLabelOrFallback(target) + "'.");
            MenuNavigationHandler.PressFocusable(target);
            MenuNavigationHandler.ReleaseFocusable(target);
        }
    }
}
