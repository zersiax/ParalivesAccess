using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using ParalivesAccess.Speech;
using Setting;
using TMPro;
using Unity.Mathematics;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>
    /// Extracts a spoken label from a UI element.
    /// </summary>
    /// <remarks>
    /// Paralives buttons carry no label field of their own — <c>ParaButton</c> only adds audio
    /// and UnityEvents. The visible text lives in child TMP_Text components, usually driven by a
    /// <c>TranslatedText</c>. Reading <c>TMP_Text.text</c> gives the already-localised string, so
    /// no Reflection is needed and the mod never re-translates game content itself.
    /// </remarks>
    public static class UiLabelReader
    {
        private const int MaxTextComponents = 8;

        private static bool _tooltipReflectionResolved;
        private static FieldInfo _simpleTranslationParametersField;
        private static FieldInfo _simpleBaseParametersField;
        private static FieldInfo _simpleHasBaseParametersField;
        private static FieldInfo _imageTranslationParametersField;
        private static FieldInfo _videoTranslationParametersField;

        private static bool _needReflectionResolved;
        private static FieldInfo _needField;
        private static FieldInfo _needCharacterField;
        private static FieldInfo _needMaxField;

        private static bool _emotionReflectionResolved;
        private static FieldInfo _emotionEffectsAndReasonsField;

        private static bool _interactionCharacterCardReflectionResolved;
        private static FieldInfo _interactionCharacterCardCharacterField;

        private static bool _townMapCharacterReflectionResolved;
        private static FieldInfo _townMapCharacterField;

        private static bool _classicSliderReflectionResolved;
        private static FieldInfo _classicSliderBodySliderField;

        private static bool _characterThumbnailReflectionResolved;
        private static FieldInfo _characterThumbnailCharacterField;

        /// <summary>
        /// Builds a spoken label for a UI element.
        /// </summary>
        /// <param name="source">The component whose element should be described.</param>
        /// <returns>Human-readable text, or null when nothing usable was found.</returns>
        public static string GetLabel(Component source)
        {
            if (source == null)
            {
                return null;
            }

            string searchLabel = ContextMenuSearchHandler.GetLabel(source);
            if (!string.IsNullOrWhiteSpace(searchLabel)) return searchLabel;

            string settingsLabel = SettingsHandler.GetLabel(source);
            if (!string.IsNullOrWhiteSpace(settingsLabel)) return settingsLabel;

            string stampLabel = GetMailboxStampLabel(source);
            if (stampLabel != null) return stampLabel;

            UIStorytellerSelectionItem storyteller = source.GetComponentInParent<UIStorytellerSelectionItem>();
            if (storyteller?.Storyteller != null)
            {
                // UIStorytellerSelectionItem.Init supplies this exact name key. Its portrait
                // contains speech-bubble text, which must not identify the choice itself.
                string name = TranslationManager.Get(storyteller.IsCustomStoryteller
                    ? "UIStoryteller_CustomStoryteller" : "Storyteller_" + storyteller.Storyteller.DisplayName);
                if (storyteller.IsCustomStoryteller) return name;
                // The review screen uses this description for the same authored storyteller.
                string descriptionKey = "StorytellerDescription_" + storyteller.Storyteller.DisplayName;
                string description = ScreenReader.NormalizeForOutput(TranslationManager.Get(descriptionKey));
                return string.IsNullOrWhiteSpace(description) || description == descriptionKey
                    ? name : Loc.Get("tutorial_storyteller_choice", name, description);
            }

            // Authoritative even when null: off-screen UILoadGameItem rows retain believable
            // prefab sample text until the game's staggered visual Init reaches them. The live
            // cached model, not those captions, identifies a save.
            if (SaveGameListHandler.TryGetLabel(source, out string savedGameLabel))
            {
                return savedGameLabel;
            }

            UIStoryCard storyCard = source.GetComponent<UIStoryCard>();
            if (storyCard != null)
            {
                // UIStoryCard.Init refreshes these fields even when mouse hover hides the
                // description object. Read the current card/pack surface, never pooled siblings.
                bool isCard = storyCard.PanelIsCard != null && storyCard.PanelIsCard.activeSelf;
                bool isPack = storyCard.PanelIsPack != null && storyCard.PanelIsPack.activeSelf;
                if (!isCard && !isPack)
                    return null;
                var parts = new List<string>();
                AddUnique(parts, ScreenReader.NormalizeForOutput(
                    (isCard ? storyCard.CardName : storyCard.LabelPackName)?.Text?.text));
                AddUnique(parts, ScreenReader.NormalizeForOutput(storyCard.LabelDescription?.Text?.text));
                return parts.Count > 0 ? string.Join(". ", parts) : null;
            }

            // These two windows contain several pooled child systems. A generic hierarchy scrape
            // can join an inactive request, a tutorial placeholder and a numeric badge into one
            // plausible-looking sentence. Their live summaries are authoritative; an empty summary
            // deliberately stays empty instead of falling through to stale descendants.
            if (source is UIThoughts thoughts)
            {
                return GetThoughtsSummary(thoughts);
            }

            if (source is UISkillsInProgressAndUpcomingEvents progressAndEvents)
            {
                return GetProgressAndEventsSummary(progressAndEvents);
            }

            if (source is UICharacters characters)
            {
                string summary = GetHouseholdSummary(characters);
                return PlayerManager.Instance?.GetHybridPlayer(characters.PlayerOwnerIndex)?.Player?.State == GameStates.CharacterCreator
                    ? Loc.Get("panel_summary", summary, Loc.Get("creator_household_controls")) : summary;
            }

            if (source is UIOfferedWants offeredWants)
            {
                return GetOfferedWantsSummary(offeredWants);
            }

            if (source is UIOfferedRequests offeredRequests)
            {
                return GetOfferedRequestsSummary(offeredRequests);
            }

            if (source is UILotInfo lotInfo)
            {
                return LotReadinessReader.DescribeLotInfo(lotInfo);
            }

            // Known item types carry their own data object. Reading it beats scraping child text:
            // a character card holds eight unrelated TMP_Texts (skills, cards, ages, blurbs) whose
            // concatenation is unusable, while AssetCharacterData.FullName is the actual answer.
            string typed = GetTypedLabel(source);
            if (!string.IsNullOrEmpty(typed))
            {
                return typed;
            }

            // Active text is authoritative. Pooled UI slots retain their previous occupant's text
            // while inactive, and TEST 43 showed that including it immediately leaks expired offers,
            // placeholder timers and unrelated tutorial copy. Some controls genuinely keep their
            // only caption inactive, so retain the old behaviour strictly as a last resort.
            string active = CollectText(source, includeInactive: false);
            if (!string.IsNullOrEmpty(active))
            {
                return active;
            }

            // An active caption that is present but unusable (most importantly `{0}`) is evidence
            // that this control has current presentation. Do not let a plausible inactive pooled
            // value win merely because the current one was rejected.
            return HasAnyActiveText(source)
                ? null
                : CollectText(source, includeInactive: true);
        }

        private static bool HasAnyActiveText(Component source)
        {
            TMP_Text[] texts = source.GetComponentsInChildren<TMP_Text>(includeInactive: false);
            foreach (TMP_Text text in texts)
            {
                if (text != null && !string.IsNullOrWhiteSpace(text.text))
                {
                    return true;
                }
            }

            return false;
        }

        private static string CollectText(Component source, bool includeInactive)
        {
            List<string> parts = new List<string>();
            TMP_Text[] texts = source.GetComponentsInChildren<TMP_Text>(includeInactive);

            int taken = 0;
            foreach (TMP_Text text in texts)
            {
                if (taken >= MaxTextComponents)
                {
                    break;
                }

                if (text == null)
                {
                    continue;
                }

                string value = text.text;
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                string trimmed = value.Trim();
                if (trimmed.Length == 0 || ContainsUnresolvedPlaceholder(trimmed)
                    || parts.Contains(trimmed))
                {
                    continue;
                }

                parts.Add(trimmed);
                taken++;
            }

            if (parts.Count == 0)
            {
                return null;
            }

            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < parts.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(parts[i]);
            }

            return builder.ToString();
        }

        private static bool ContainsUnresolvedPlaceholder(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            for (int i = 0; i + 2 < text.Length; i++)
            {
                if (text[i] != '{' || !char.IsDigit(text[i + 1]))
                {
                    continue;
                }

                int end = i + 2;
                while (end < text.Length && char.IsDigit(text[end]))
                {
                    end++;
                }

                if (end < text.Length && text[end] == '}')
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// True when text carries at least one letter or digit, i.e. it is a name and not a glyph.
        /// </summary>
        /// <remarks>
        /// Deliberately Unicode-aware via char.IsLetterOrDigit rather than an ASCII test: the game
        /// ships Chinese, Japanese and Korean translations, whose labels are letters with no ASCII
        /// in them at all. An ASCII check would classify every CJK label as a glyph and silently
        /// reroute those users to tooltips.
        /// </remarks>
        /// <param name="text">The candidate label.</param>
        /// <returns>True when the text is worth speaking as a name.</returns>
        private static bool IsSpeakable(string text)
        {
            foreach (char c in text)
            {
                if (char.IsLetterOrDigit(c))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Appends the semantic state when the element genuinely tracks selection.
        /// </summary>
        /// <remarks>
        /// This is the answer to "I don't know if we properly confirmed our choices". Nearly every
        /// selectable thing in this UI derives from ParaToggle, which carries a public IsOn:
        ///   Focusable -> ParaButton -> ParaToggle -> UIListItemBase -> (most item types)
        ///
        /// But IsOn is NOT universally maintained. UIListItemBase.OnClick overrides ParaToggle.OnClick
        /// and never calls base, so for list items IsOn is only click-maintained when the prefab sets
        /// InSelectionMode. Announcing it regardless says "not selected" about the very item that IS
        /// selected - measured on colour swatches, where UISwatchItem never touches IsOn at all and
        /// selection lives in UISwatchList.SelectedSwatchGUID.
        ///
        /// So this follows the ordered rule in docs/game-api.md section 16, all public game API, and
        /// stays SILENT when the control does not track selection. Silence is right there: a list
        /// with no selection yet and a list that does not use IsOn are indistinguishable, and both
        /// self-correct the moment something is actually selected.
        ///
        /// Where selection IS tracked, both states are spoken rather than only the positive one -
        /// silence would otherwise be ambiguous with an unhandled control.
        /// </remarks>
        /// <param name="source">The element being described.</param>
        /// <param name="label">The label built so far.</param>
        /// <returns>The label, with selection state appended when the control tracks it.</returns>
        private static string AppendToggleState(Component source, string label)
        {
            // Inspector rows already include their live on/off state, rather than selection state.
            if (SettingsHandler.GetRow(source) != null) return label;
            UIListItemBase creatorRow = source.GetComponentInParent<UIListItemBase>();
            UICharacterCreator rowOwner = source.GetComponentInParent<UICharacterCreator>();
            if (rowOwner != null && creatorRow?.ParentUIList != null)
            {
                if (ReferenceEquals(creatorRow.ParentUIList, rowOwner.VoiceItemList))
                    label = Loc.Get("creator_voice_choice", label);
                else if (ReferenceEquals(creatorRow.ParentUIList, rowOwner.LifeStageItemList))
                    label = Loc.Get("creator_life_stage_choice", label);
                else if (ReferenceEquals(creatorRow.ParentUIList, rowOwner.GenderList)
                    && creatorRow is UICharacterCreatorGenderItem genderRow
                    && genderRow.Label != null && genderRow.Label.text != "...")
                    label = Loc.Get("creator_gender_choice", label);
            }

            // UIInteractionQueueSocialGroupCharacterCardItem never writes ParaToggle.IsOn.
            // Its selected colour is driven directly from Player.SelectedCharactersGUID, so the
            // typed label below owns this state and the generic toggle path must not append a
            // stale "not selected" after it.
            if (source.GetComponentInParent<UIInteractionQueueSocialGroupCharacterCardItem>() != null)
            {
                return label;
            }

            // GetMixedTraitLabel reads the live PersonalityManager model because the coincident
            // outer row and nested checkbox do not share one reliable ParaToggle.IsOn value.
            if (source.GetComponentInParent<UIProfileTraitMixItem>() != null)
            {
                return label;
            }

            // ButtonMailbox is a button, not a persistent selection. Its prefab toggle can remain
            // IsOn after UIMailbox.Back has already hidden the window (TEST 75), yielding the false
            // announcement "Mailbox, selected". The actual UIWindow visibility is authoritative.
            UIGameBar gameBar = source.GetComponentInParent<UIGameBar>();
            if (gameBar != null && IsWithin(source, gameBar.ButtonMailbox))
            {
                try
                {
                    UIMailbox mailbox = UI.GetOrNull<UIMailbox>(gameBar.PlayerOwnerIndex);
                    return mailbox != null && mailbox.IsVisible
                        ? Loc.Get("label_with_state", label, Loc.Get("state_open"))
                        : label;
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogError("Reading authoritative mailbox visibility threw: " + ex);
                    return label;
                }
            }

            var toggle = source.GetComponent<ParaToggle>();
            if (toggle == null)
            {
                return label;
            }

            // "Selected" is technically true for a tab, but it does not answer the useful
            // question: which mutually exclusive panel is actually open? Keep that vocabulary
            // scoped to source-proven panel selectors; ordinary checkboxes, choices and
            // multi-select rows retain selected/not selected below.
            if (TryGetExclusivePanelTabState(source, out bool isCurrentPanel))
            {
                return isCurrentPanel
                    ? Loc.Get("label_with_state", label, Loc.Get("state_current_panel"))
                    : label;
            }

            bool? selected = GetSelectionState(toggle);
            if (!selected.HasValue)
            {
                return label;
            }

            string state = selected.Value ? Loc.Get("state_selected") : Loc.Get("state_not_selected");
            return Loc.Get("label_with_state", label, state);
        }

        /// <summary>
        /// Identifies controls whose state denotes the one currently displayed panel rather than
        /// an ordinary selected value.
        /// </summary>
        /// <param name="source">The focused UI component.</param>
        /// <param name="isCurrent">True when this control owns the currently displayed panel.</param>
        /// <returns>True only for a source-proven exclusive panel selector.</returns>
        internal static bool TryGetExclusivePanelTabState(Component source, out bool isCurrent)
        {
            isCurrent = false;
            if (source == null)
            {
                return false;
            }

            // External game UI can be between pooled-list refresh stages. A failed semantic read
            // falls back to the established selected/not-selected vocabulary rather than breaking
            // the caller's announcement.
            try
            {
                ParaToggle toggle = source.GetComponent<ParaToggle>();
                if (toggle == null)
                {
                    return false;
                }

                // UICharacterSubMenuBar.Update derives each toggle directly from its corresponding
                // UIWindow.IsVisible, then opens Thoughts when no sibling window is visible.
                UICharacterSubMenuBar characterBar =
                    source.GetComponentInParent<UICharacterSubMenuBar>();
                if (characterBar != null && IsCharacterInformationTab(toggle, characterBar))
                {
                    isCurrent = toggle.IsOn;
                    return true;
                }

                // UIOptions.Update writes IsOn from its one _currentCategoryGUID on every frame;
                // choosing a row replaces the inspector contents through SetCurrentCategory.
                UIOptionsCategoryItem optionsCategory =
                    source.GetComponent<UIOptionsCategoryItem>();
                if (optionsCategory != null
                    && TryGetSingleOnListPanelState(optionsCategory, out isCurrent))
                {
                    return true;
                }

                // Collectibles does not maintain ParaToggle.IsOn. Its public SelectedCategoryIndex
                // chooses the one category whose items Refresh places in ListCollectibleItems, so
                // use that model index rather than the selected background image.
                UICollectiblesCategoryItem collectiblesCategory =
                    source.GetComponent<UICollectiblesCategoryItem>();
                if (collectiblesCategory != null
                    && TryGetCollectiblesPanelState(collectiblesCategory, out isCurrent))
                {
                    return true;
                }

                // Outfit type is a real tab: UICharacterOutfits.SelectedOutfitType changes the one
                // CurrentOutfitType whose options UICharacterOutfitType.RefreshList exposes. Require
                // one and only one active sibling so a pooled/transitional list cannot be described
                // as exclusive when its own state disagrees.
                UICharacterOutfitType outfitType = source.GetComponent<UICharacterOutfitType>();
                if (outfitType != null
                    && TryGetSingleOnListPanelState(outfitType, out isCurrent))
                {
                    return true;
                }

                // Character-creator categories form a selected path. Only the level containing the
                // one CharacterCreatorCurrentTagGUIDs match is a panel-tab set; ancestors remain
                // "selected" because they describe the expanded path, not the displayed content.
                UIGenericCategoriesButton category =
                    source.GetComponent<UIGenericCategoriesButton>();
                if (category != null
                    && TryGetCreatorCategoryPanelState(category, out isCurrent))
                {
                    return true;
                }

                // UIGenericSectionMenu builds a row of grouped toggles whose actions replace one
                // visible section (UIModEditor is the shipped consumer). Unlike an arbitrary radio
                // group, this type's only purpose is panel selection.
                UIGenericSectionMenu sectionMenu =
                    source.GetComponentInParent<UIGenericSectionMenu>();
                if (sectionMenu != null && sectionMenu.ParaToggleGroup != null
                    && ReferenceEquals(toggle.ParaToggleGroup, sectionMenu.ParaToggleGroup)
                    && TryGetSingleOnGroupedPanelState(
                        toggle, sectionMenu.ParaToggleGroup, out isCurrent))
                {
                    return true;
                }

                // General authored form: a grouped ParaToggle whose own ButtonOpenUIWindow targets
                // a UIWindowGroup. Both pieces are required. ParaToggleGroup proves only one toggle
                // can be on; UIWindowGroup proves the action controls mutually exclusive windows.
                ButtonOpenUIWindow windowOpener = source.GetComponent<ButtonOpenUIWindow>();
                if (windowOpener?.UIWindowPrefab?.UIWindowGroup != null
                    && toggle.ParaToggleGroup != null
                    && TryGetSingleOnGroupedPanelState(
                        toggle, toggle.ParaToggleGroup, out isCurrent))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Reading exclusive panel-tab state threw: {ex}");
            }

            return false;
        }

        private static bool IsCharacterInformationTab(
            ParaToggle toggle, UICharacterSubMenuBar bar)
        {
            return ReferenceEquals(toggle, bar.ButtonThoughts)
                || ReferenceEquals(toggle, bar.ButtonProfile)
                || ReferenceEquals(toggle, bar.ButtonSkills)
                || ReferenceEquals(toggle, bar.ButtonSocial)
                || ReferenceEquals(toggle, bar.ButtonOccupation)
                || ReferenceEquals(toggle, bar.ButtonInventory)
                || ReferenceEquals(toggle, bar.ButtonMemories)
                || ReferenceEquals(toggle, bar.ButtonGoals);
        }

        private static bool TryGetSingleOnListPanelState<T>(T tab, out bool isCurrent)
            where T : UIListItemBase
        {
            isCurrent = false;
            UIList list = tab.ParentUIList;
            if (list?.CurrentItems == null)
            {
                return false;
            }

            bool foundTab = false;
            int currentCount = 0;
            foreach (UIListItemBase item in list.CurrentItems)
            {
                T sibling = item as T;
                if (sibling == null || !sibling.gameObject.activeInHierarchy)
                {
                    continue;
                }

                foundTab |= ReferenceEquals(sibling, tab);
                if (sibling.IsOn)
                {
                    currentCount++;
                }
            }

            if (!foundTab || currentCount != 1)
            {
                return false;
            }

            isCurrent = tab.IsOn;
            return true;
        }

        private static bool TryGetCollectiblesPanelState(
            UICollectiblesCategoryItem tab, out bool isCurrent)
        {
            isCurrent = false;
            UICollectibles collectibles = tab.GetComponentInParent<UICollectibles>();
            UIList list = collectibles?.ListCategories;
            if (list?.CurrentItems == null)
            {
                return false;
            }

            int index = list.CurrentItems.IndexOf(tab);
            if (index < 0 || collectibles.SelectedCategoryIndex < 0
                || collectibles.SelectedCategoryIndex >= list.CurrentItems.Count)
            {
                return false;
            }

            isCurrent = index == collectibles.SelectedCategoryIndex;
            return true;
        }

        private static bool TryGetSingleOnGroupedPanelState(
            ParaToggle tab, ParaToggleGroup group, out bool isCurrent)
        {
            isCurrent = false;
            if (tab == null || group == null)
            {
                return false;
            }

            bool foundTab = false;
            int currentCount = 0;
            foreach (ParaToggle sibling in group.GetComponentsInChildren<ParaToggle>(false))
            {
                if (sibling == null || !sibling.gameObject.activeInHierarchy
                    || !ReferenceEquals(sibling.ParaToggleGroup, group))
                {
                    continue;
                }

                foundTab |= ReferenceEquals(sibling, tab);
                if (sibling.IsOn)
                {
                    currentCount++;
                }
            }

            if (!foundTab || currentCount != 1)
            {
                return false;
            }

            isCurrent = tab.IsOn;
            return true;
        }

        private static bool TryGetCreatorCategoryPanelState(
            UIGenericCategoriesButton button, out bool isCurrent)
        {
            isCurrent = false;
            UIGenericCategoriesData data = button.Data;
            UIList list = button.ParentUIList;
            UICharacterCreator creator = button.GetComponentInParent<UICharacterCreator>();
            Player player = creator?.PlayerOwner?.Player;
            ulong[] currentTags = player?.CharacterCreatorCurrentTagGUIDs;

            if (data == null || !data.IsCharacterCreatorTag || list?.CurrentItems == null
                || currentTags == null || currentTags.Length != 1)
            {
                return false;
            }

            bool foundButton = false;
            int currentCount = 0;
            foreach (UIListItemBase item in list.CurrentItems)
            {
                UIGenericCategoriesButton sibling = item as UIGenericCategoriesButton;
                if (sibling == null || !sibling.gameObject.activeInHierarchy
                    || sibling.Data == null || !sibling.Data.IsCharacterCreatorTag)
                {
                    continue;
                }

                foundButton |= ReferenceEquals(sibling, button);
                if (ContainsGuid(currentTags, sibling.Data.Tag))
                {
                    currentCount++;
                }
            }

            // UpdateCharacterHighlightZone can temporarily publish several current tags. In that
            // state the source does not prove that this list represents one exclusive panel, so
            // retain ordinary selection language until it settles back to one match.
            if (!foundButton || currentCount != 1)
            {
                return false;
            }

            isCurrent = ContainsGuid(currentTags, data.Tag);
            return true;
        }

        private static bool ContainsGuid(ulong[] guids, ulong candidate)
        {
            foreach (ulong guid in guids)
            {
                if (guid == candidate)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Decides whether a toggle's selection state is meaningful, and what it is.
        /// </summary>
        /// <remarks>
        /// Implements docs/game-api.md section 16 in order:
        ///  1. UISwatchItem        - IsOn is never written; read UISwatchList.SelectedSwatchGUID.
        ///  2. Not a list item     - a real checkbox or radio button; IsOn is authoritative.
        ///  3. ParentUIList null   - the item is inert on click; nothing to report.
        ///  4. InSelectionMode     - the list maintains IsOn on click; authoritative.
        ///  5. HasSelectedItem()   - some item in the list is on, so the list does use IsOn.
        ///  6. Otherwise           - unknown; stay silent.
        /// </remarks>
        /// <param name="toggle">The toggle component on the focused element.</param>
        /// <returns>The selection state, or null when the control does not track selection.</returns>
        private static bool? GetSelectionState(ParaToggle toggle)
        {
            // External game API: pooled items can be mid-rebuild, and these are game types whose
            // shape can change between patches.
            try
            {
                if (toggle is UISwatchItem swatch)
                {
                    var list = swatch.ParentUIList as UISwatchList;
                    if (list != null && swatch.Swatch != null)
                    {
                        return list.SelectedSwatchGUID == swatch.Swatch.GUID;
                    }

                    // The swatch's own indicator, which UISwatchList drives from the same GUID.
                    return swatch.SelectedImage != null ? swatch.SelectedImage.activeSelf : (bool?)null;
                }

                // Same shape as the swatch case: IsOn is never written for outfit entries. Init
                // drives SelectedImage.SetActive(isOn) without touching base.IsOn, and OnClick
                // overrides ParaToggle.OnClick without calling base, so the generic list rules below
                // would fall through to silence on a control that genuinely does track selection.
                // The panel holds the answer as UICharacterOutfits.CurrentOutfit.
                if (toggle is UICharacterOutfitOptions outfitOption)
                {
                    UICharacterOutfits outfits = outfitOption.UICharacterOutfits;
                    return outfits == null ? (bool?)null : outfits.CurrentOutfit == outfitOption.OutfitGUID;
                }

                var item = toggle as UIListItemBase;
                if (item == null)
                {
                    return toggle.IsOn;
                }

                UIList parent = item.ParentUIList;
                if (parent == null)
                {
                    return null;
                }

                if (parent.InSelectionMode)
                {
                    return toggle.IsOn;
                }

                return parent.HasSelectedItem() ? toggle.IsOn : (bool?)null;
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"Reading selection state threw: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Reads a label from the element's underlying game data, for item types that carry it.
        /// </summary>
        /// <remarks>
        /// "Read, never recompute": UICharacterItem holds the AssetCharacter it displays, so the
        /// name comes from AssetCharacterData.FullName rather than from whatever text happens to be
        /// laid out inside the card. Scraping produced
        /// "+1, Kraft Singles, Cooking Buddy +1, Music, 20, +1 learning speed for XYZ skill, ..." -
        /// eight unrelated child texts joined into something unusable.
        ///
        /// Extend this per item type as each one is confirmed in the decompiled source. Item types
        /// that have not been checked fall through to the text scrape rather than being guessed at.
        /// </remarks>
        /// <param name="source">The component whose element should be described.</param>
        /// <returns>A label from game data, or null when this type is not handled.</returns>
        private static string GetTypedLabel(Component source)
        {
            string creatorDelete = CreatorHouseholdHandler.GetDeleteLabel(source);
            if (!string.IsNullOrEmpty(creatorDelete)) return creatorDelete;
            string tutorialAction = GetTutorialActionLabel(source);
            if (!string.IsNullOrEmpty(tutorialAction))
            {
                return tutorialAction;
            }

            var lotInfo = source.GetComponentInParent<UILotInfo>();
            if (lotInfo != null && lotInfo.LotThumbnail != null
                && ReferenceEquals(lotInfo.LotThumbnail.gameObject, source.gameObject))
            {
                // Source only assigns this Image's sprite; there is no textual alternative for the
                // visual preview. Keep the stop reportable without pretending to describe it.
                return Loc.Get("lot_preview_image");
            }

            var activeWant = source.GetComponent<UIActiveWant2Item>();
            if (activeWant != null)
            {
                return GetActiveWantLabel(activeWant);
            }

            var offeredWant = source.GetComponent<UIOfferedWantsItem>();
            if (offeredWant != null)
            {
                string name = ReadActiveText(offeredWant.WantNameLabel);
                string reason = offeredWant.Tooltip != null
                    ? ReadOpenerText(offeredWant.Tooltip, offeredWant)
                    : null;
                return string.IsNullOrEmpty(reason) || string.Equals(name, reason)
                    ? name
                    : Loc.Get("offered_want_with_reason", name, reason);
            }

            // UIMailbox.Back is exactly Hide(); TEST 75 confirms this control closed the window.
            // Its prefab nevertheless carries the literal sample caption "x", which otherwise
            // wins before the action-wiring fallback and conceals the useful operation.
            if (source.GetComponentInParent<UIMailbox>() != null
                && string.Equals(source.gameObject.name, "ButtonClose", StringComparison.Ordinal))
            {
                return Loc.Get("action_close");
            }

            // The same portrait item appears beside a previewed interaction and inside a queued
            // social group. Resolve it before the broader queue ancestor so the represented Para,
            // not the queued activity, names this distinct native selection control.
            var interactionCharacterCard =
                source.GetComponentInParent<UIInteractionQueueSocialGroupCharacterCardItem>();
            if (interactionCharacterCard != null)
            {
                return GetInteractionCharacterCardLabel(interactionCharacterCard);
            }

            var interactionItem = source.GetComponent<UIInteractionsListItem>();
            if (interactionItem != null)
            {
                return GetInteractionItemLabel(interactionItem);
            }

            var interactionList = source.GetComponent<UIInteractionsList>();
            if (interactionList != null)
            {
                return GetInteractionListLabel(interactionList);
            }

            var queueItem = source.GetComponentInParent<UIInteractionQueueItem>();
            if (queueItem != null)
            {
                return GetInteractionQueueItemLabel(queueItem);
            }

            var needItem = source.GetComponent<UINeedsItem>();
            if (needItem != null)
            {
                return GetNeedLabel(needItem);
            }

            var parentNeed = source.GetComponentInParent<UINeedsItem>();
            if (parentNeed != null && IsWithin(source, parentNeed.NeedAbilityButton))
            {
                string needName = GetNeedName(parentNeed);
                return string.IsNullOrEmpty(needName)
                    ? Loc.Get("need_offers")
                    : Loc.Get("need_offers_for", needName);
            }

            var emotion = source.GetComponentInParent<UIEmotionsItem2>();
            if (emotion != null && (ReferenceEquals(source, emotion)
                || IsWithin(source, emotion.EmotionSpriteObject)))
            {
                return GetEmotionLabel(emotion);
            }

            var togetherBar = source.GetComponentInParent<UICharactersTogetherBar>();
            if (togetherBar != null)
            {
                return GetTogetherBarLabel(togetherBar, source);
            }

            var togetherCard = source.GetComponentInParent<UITogetherCard>();
            if (togetherCard != null)
            {
                return GetTogetherCardLabel(togetherCard);
            }

            var characterListItem = source.GetComponentInParent<UICharacterListItem>();
            if (characterListItem != null
                && IsWithin(source, characterListItem.MainEmotionObject))
            {
                return GetCharacterMainEmotionLabel(characterListItem);
            }
            if (characterListItem != null && ReferenceEquals(source, characterListItem))
            {
                return GetCharacterListItemLabel(characterListItem);
            }

            var thoughtBubble = source.GetComponentInParent<UIThoughtBubblesItem>();
            if (thoughtBubble != null)
            {
                // The child Sprite is a second focusable over the same icon. Give it the same
                // semantic label even when a future prefab action causes the navigator to retain it.
                return GetThoughtBubbleLabel(thoughtBubble);
            }

            string characterTab = GetCharacterTabLabel(source);
            if (!string.IsNullOrEmpty(characterTab))
            {
                return characterTab;
            }

            string gameBar = GetGameBarLabel(source);
            if (!string.IsNullOrEmpty(gameBar))
            {
                return gameBar;
            }

            string personalityLevel = GetPersonalityLevelLabel(source);
            if (!string.IsNullOrEmpty(personalityLevel))
            {
                return personalityLevel;
            }

            var progressPanel = source.GetComponentInParent<UISkillsInProgressAndUpcomingEvents>();
            string progressShortcut = GetProgressShortcutLabel(progressPanel, source);
            if (!string.IsNullOrEmpty(progressShortcut))
            {
                return progressShortcut;
            }

            string tutorial = GetProgressTutorialLabel(progressPanel, source);
            if (!string.IsNullOrEmpty(tutorial))
            {
                return tutorial;
            }

            var newspaperArticle = source.GetComponent<UINewspaperArticleItem>();
            if (newspaperArticle != null)
            {
                return GetNewspaperArticleLabel(newspaperArticle);
            }

            // UINewspaperArticleItem is itself focusable, but its action is a separate child
            // Focusable. Label both from the article's public references so the hidden pooled
            // "claimed or expired" text can never leak into an available offer.
            var parentNewspaperArticle = source.GetComponentInParent<UINewspaperArticleItem>();
            if (parentNewspaperArticle != null
                && parentNewspaperArticle.ButtonAction == source.gameObject)
            {
                return GetNewspaperArticleLabel(parentNewspaperArticle);
            }

            var goal = source.GetComponent<UIGoalItem>();
            if (goal != null)
            {
                return GetGoalLabel(goal);
            }

            var goalHeader = source.GetComponent<UIGoalItemHeader>();
            if (goalHeader != null)
            {
                return ReadActiveTranslatedText(goalHeader.LineLabel);
            }

            var goalLine = source.GetComponent<UIGoalItemLine>();
            if (goalLine != null)
            {
                return ReadActiveTranslatedText(goalLine.LineLabel);
            }

            var offeredRequest = source.GetComponent<UIGoalOfferedLine>();
            if (offeredRequest != null)
            {
                return GetOfferedRequestLineLabel(offeredRequest);
            }

            // The visual job card contains the native OnClickOnItem application callback; there is
            // no separate Accept button. Its prefab also registers coincident focusables, so the
            // keyboard adapter resolves the semantic card explicitly. Name the action and exact
            // application-points gate before the generic child-text scrape joins the position,
            // company, rank, salary and decorative "Apply!" caption without explaining ownership.
            var occupationOffer = source.GetComponent<UIOccupationOfferItem>();
            if (occupationOffer != null)
            {
                return GetOccupationOfferLabel(occupationOffer);
            }

            var characterItem = source.GetComponent<UICharacterItem>();
            if (characterItem != null)
            {
                // External game API: pooled list items can be mid-rebuild with no character attached.
                try
                {
                    AssetCharacter character = characterItem.Character;
                    string fullName = character?.Data?.FullName;
                    return string.IsNullOrEmpty(fullName) ? null : fullName.Trim();
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogError($"Reading UICharacterItem.Character threw: {ex}");
                    return null;
                }
            }

            var smallCharacter = source.GetComponent<SmallCharacterThumbnailListItem>();
            if (smallCharacter != null)
            {
                return GetSmallCharacterThumbnailLabel(smallCharacter);
            }

            var outfitType = source.GetComponent<UICharacterOutfitType>();
            if (outfitType != null)
            {
                return GetOutfitTypeLabel(outfitType);
            }

            var buildItem = source.GetComponent<UIBuildModeItem>();
            if (buildItem != null)
            {
                return GetBuildModeItemLabel(buildItem);
            }

            var skinChoice = source.GetComponent<UIGenericSkinChoice>();
            if (skinChoice != null)
            {
                return GetSkinChoiceLabel(skinChoice);
            }

            var skinChoiceButton = source.GetComponent<UIGenericSkinChoiceButton>();
            if (skinChoiceButton != null)
            {
                return ReadCleanText(skinChoiceButton.Label);
            }

            var swatch = source.GetComponent<UISwatchItem>();
            if (swatch != null)
            {
                return GetSwatchLabel(swatch);
            }

            var category = source.GetComponent<UIGenericCategoriesButton>();
            if (category != null)
            {
                return GetCategoryLabel(category);
            }

            var outfitOption = source.GetComponent<UICharacterOutfitOptions>();
            if (outfitOption != null)
            {
                return GetOutfitOptionLabel(outfitOption);
            }

            var decalSection = source.GetComponent<UICharacterCreatorDecalPositionsListItem>();
            if (decalSection != null)
            {
                return GetDecalSectionLabel(decalSection);
            }

            // The two layout containers inside the contextual list. Both are UIListItemBase, so they
            // are focusable themselves while also holding the controls the user actually wants, and
            // the generic scrape turns them into a wall of joined-up child names. Named as landmarks
            // instead of skipped: they say what follows and how much of it there is.
            var equipmentGrid = source.GetComponent<UICharacterCreatorContextualListEquipments>();
            if (equipmentGrid != null)
            {
                return CountLabel("group_items", equipmentGrid.UIList);
            }

            var presetGrid = source.GetComponent<UICharacterCreatorContextualListPresets>();
            if (presetGrid != null)
            {
                return CountLabel("group_sliders", presetGrid.UIList);
            }

            var decalPositions = source.GetComponent<UICharacterCreatorDecalPositions>();
            if (decalPositions != null)
            {
                return CountLabel("group_body_sections", decalPositions.ListSections);
            }

            string visibleTrait = GetVisibleTraitDescriptionLabel(source);
            if (!string.IsNullOrEmpty(visibleTrait))
            {
                return visibleTrait;
            }

            var simpleTrait = source.GetComponentInParent<UIProfileTraitSimpleItem>();
            if (simpleTrait != null)
            {
                return GetSimpleTraitLabel(simpleTrait);
            }

            var mixedTrait = source.GetComponentInParent<UIProfileTraitMixItem>();
            if (mixedTrait != null)
            {
                return GetMixedTraitLabel(mixedTrait);
            }

            var statItem = source.GetComponent<UIProfileTraitStatsItem>();
            if (statItem != null)
            {
                return GetStatLabel(statItem);
            }

            var slider = source.GetComponent<UIClassicSliderItem>();
            if (slider != null)
            {
                return GetSliderLabel(slider);
            }

            var handle = source.GetComponent<UICharacterCreatorDiagramHandleItem>();
            if (handle != null)
            {
                return GetHandleLabel(handle);
            }

            string randomize = GetPersonalityRandomizeLabel(source);
            if (!string.IsNullOrEmpty(randomize))
            {
                return randomize;
            }

            var inputField = source.GetComponent<TMP_InputField>();
            if (inputField != null)
            {
                return GetInputFieldLabel(inputField);
            }

            // Two icon-only controls in the outfits panel. Both are reached by identity against a
            // public field rather than by GameObject name, so a renamed prefab object degrades to
            // the old "unlabeled" behaviour instead of mislabelling a different control.
            var outfitTypeItem = source.GetComponentInParent<UICharacterOutfitType>();
            if (outfitTypeItem != null && outfitTypeItem.ShowOptionsButton == source.gameObject)
            {
                return GetShowOptionsLabel(outfitTypeItem);
            }

            var outfitsPanel = source.GetComponentInParent<UICharacterOutfits>();
            if (outfitsPanel != null && outfitsPanel.AddButton == source.gameObject)
            {
                return Loc.Get("outfit_add");
            }

            return null;
        }

        /// <summary>
        /// Keeps a tutorial overlay's action attached to the question or explanation it answers.
        /// TEST 53 reached the needs overlay's lone OK control after navigating other panels; an
        /// isolated "OK" no longer conveyed what accepting it meant.
        /// </summary>
        private static string GetTutorialActionLabel(Component source)
        {
            if (!(source is Focusable))
            {
                return null;
            }

            UIThoughts thoughts = source.GetComponentInParent<UIThoughts>();
            UIContainer tutorial = null;
            if (thoughts?.UIWants2?.TutorialOverlay?.IsVisible == true
                && IsWithin(source, thoughts.UIWants2.TutorialOverlay.gameObject))
            {
                tutorial = thoughts.UIWants2.TutorialOverlay;
            }
            else if (thoughts?.UINeeds?.NeedsTutorial?.IsVisible == true
                && IsWithin(source, thoughts.UINeeds.NeedsTutorial.gameObject))
            {
                tutorial = thoughts.UINeeds.NeedsTutorial;
            }

            if (tutorial == null)
            {
                return null;
            }

            string action = CollectText(source, includeInactive: false);
            if (string.IsNullOrEmpty(action))
            {
                action = Loc.Get("action_continue_tutorial");
            }
            string instruction = ReadTutorialInstruction(tutorial);
            return string.IsNullOrEmpty(instruction)
                ? action
                : Loc.Get("tutorial_control_context", action, instruction);
        }

        private static string GetActiveWantLabel(UIActiveWant2Item item)
        {
            try
            {
                if (item.HasNoWant != null && item.HasNoWant.activeInHierarchy)
                {
                    UIWants2 wants = item.GetComponentInParent<UIWants2>();
                    UIList list = wants?.ActiveWantsList;
                    if (list?.CurrentItems != null)
                    {
                        int index = list.CurrentItems.IndexOf(item);
                        if (index >= 0)
                        {
                            return Loc.Get(
                                "want_empty_slot", index + 1, list.CurrentItems.Count);
                        }
                    }
                }
                return GetTooltipText(item);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Reading UIActiveWant2Item tooltip threw: {ex}");
                return null;
            }
        }

        private static string GetInteractionItemLabel(UIInteractionsListItem item)
        {
            try
            {
                string name = ReadActiveTranslatedText(item.LabelInteractionUnitName);
                if (string.IsNullOrEmpty(name))
                {
                    return null;
                }

                if (item.InteractionGroupItem != null
                    && item.InteractionGroupItem.Type == InteractionItemType.Group)
                {
                    // The visual child-arrow is carried by ImageHasChildInteractionsIndicator.
                    // Render that semantic state instead of relying on punctuation alone.
                    name = Loc.Get(
                        "interaction_submenu_label",
                        TrimInteractionGroupEllipsis(name));
                }

                var details = new List<string>();

                UIInteractionSuccessRate success = item.UIInteractionSuccessRate;
                string successRate = success != null && success.gameObject.activeInHierarchy
                    ? ReadActiveText(success.LabelSuccessRate)
                    : null;
                if (!string.IsNullOrEmpty(successRate))
                {
                    details.Add(Loc.Get("interaction_success", successRate));
                }

                string cost = item.CostPanel != null && item.CostPanel.activeInHierarchy
                    ? ReadActiveText(item.CostLabel)
                    : null;
                if (!string.IsNullOrEmpty(cost))
                {
                    details.Add(Loc.Get("interaction_cost", cost));
                }

                string cooldown = item.CooldownPanel != null && item.CooldownPanel.activeInHierarchy
                    ? ReadActiveText(item.CooldownLabel)
                    : null;
                if (!string.IsNullOrEmpty(cooldown))
                {
                    details.Add(Loc.Get("interaction_cooldown", cooldown));
                }

                if (!item.Interactable && item.Tooltip != null
                    && !string.IsNullOrWhiteSpace(item.Tooltip.TextToShow))
                {
                    details.Add(Loc.Get("interaction_unavailable", item.Tooltip.TextToShow.Trim()));
                }

                return details.Count == 0
                    ? name
                    : Loc.Get("interaction_with_details", name,
                        string.Join(Loc.Get("list_separator"), details.ToArray()));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Reading UIInteractionsListItem state threw: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Names the Para portrait selector shown for multi-Para interaction and queue contexts.
        /// </summary>
        private static string GetInteractionCharacterCardLabel(
            UIInteractionQueueSocialGroupCharacterCardItem card)
        {
            ResolveInteractionCharacterCardReflection();
            if (_interactionCharacterCardCharacterField == null)
            {
                return null;
            }

            AssetCharacter character;
            try
            {
                character = _interactionCharacterCardCharacterField.GetValue(card) as AssetCharacter;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError(
                    "Reading UIInteractionQueueSocialGroupCharacterCardItem._character threw: " + ex);
                return null;
            }

            if (character?.Data == null)
            {
                return null;
            }

            string characterName = character.Data.FullName;
            if (string.IsNullOrWhiteSpace(characterName))
            {
                characterName = character.Data.ShortName;
            }
            if (string.IsNullOrWhiteSpace(characterName))
            {
                return null;
            }

            string label = characterName.Trim();
            Player player = PlayerManager.Instance?.HybridPlayer1?.Player;
            if (player?.SelectedCharactersGUID != null)
            {
                string state = player.SelectedCharactersGUID.Contains(character.GUID)
                    ? Loc.Get("state_selected")
                    : Loc.Get("state_not_selected");
                label = Loc.Get("label_with_state", label, state);
            }

            // UIInteractions.Update supplies the exact CanCharacterDoInteraction result and the
            // card renders it solely through this X overlay. Queue-only cards leave it inactive.
            if (card.XThumbnailOverlay != null && card.XThumbnailOverlay.activeSelf)
            {
                label = Loc.Get(
                    "label_with_state",
                    label,
                    Loc.Get("interaction_character_cannot_perform"));
            }

            return label;
        }

        private static void ResolveInteractionCharacterCardReflection()
        {
            if (_interactionCharacterCardReflectionResolved)
            {
                return;
            }

            _interactionCharacterCardReflectionResolved = true;
            _interactionCharacterCardCharacterField = AccessTools.Field(
                typeof(UIInteractionQueueSocialGroupCharacterCardItem), "_character");
            if (_interactionCharacterCardCharacterField == null)
            {
                Plugin.Log.LogError(
                    "Could not resolve UIInteractionQueueSocialGroupCharacterCardItem._character. "
                  + "Multi-Para interaction portrait controls will retain their generic fallback.");
            }
        }

        private static string TrimInteractionGroupEllipsis(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            string result = value.TrimEnd();
            if (result.EndsWith("...", StringComparison.Ordinal))
            {
                return result.Substring(0, result.Length - 3).TrimEnd();
            }
            if (result.EndsWith("…", StringComparison.Ordinal))
            {
                return result.Substring(0, result.Length - 1).TrimEnd();
            }
            return result;
        }

        private static string GetTogetherBarLabel(
            UICharactersTogetherBar bar, Component source)
        {
            try
            {
                if (bar.ButtonBarFull != null && bar.ButtonBarFull.activeInHierarchy)
                {
                    if (IsWithin(source, bar.ButtonBarFull))
                    {
                        return Loc.Get("action_open_together_choices");
                    }

                    // The outer list item selects the participants; its active child opens the
                    // topics. They overlap visually but are distinct actions and must never inherit
                    // the same label merely because they share this ancestor.
                    if (ReferenceEquals(source, bar))
                    {
                        return Loc.Get("action_select_together_group");
                    }
                    return Loc.Get("together_bar_ready");
                }

                int percent = bar.TogetherBarFill != null
                    ? Mathf.RoundToInt(bar.TogetherBarFill.fillAmount * 100f)
                    : 0;
                return Loc.Get("together_bar_filling", percent);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Reading UICharactersTogetherBar state threw: " + ex);
                return null;
            }
        }

        /// <summary>
        /// Names an interaction-queue row by the action Enter performs, not merely its state.
        /// </summary>
        private static string GetInteractionQueueItemLabel(UIInteractionQueueItem item)
        {
            try
            {
                AssetCharacterDataInteraction data = item.InteractionSaveData;
                string name = item.TooltipInteractionName != null
                    ? ReadOpenerText(item.TooltipInteractionName, item)
                    : null;
                if (string.IsNullOrWhiteSpace(name) || data == null)
                {
                    return name;
                }

                bool inTutorial = PlayerManager.Instance?.HybridPlayer1?.Player?.IsInIntro == true;
                if (inTutorial)
                {
                    return Loc.Get("queue_tutorial_locked", name);
                }

                switch (data.State)
                {
                    case AssetCharacterDataInteractionState.ToBeCanceled:
                    case AssetCharacterDataInteractionState.Cancelling:
                    case AssetCharacterDataInteractionState.ToBeDeleted:
                        return Loc.Get("queue_cancelling", name);
                    case AssetCharacterDataInteractionState.Running:
                        return Loc.Get("queue_cancel_running", name);
                    default:
                        return Loc.Get("queue_cancel_queued", name);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Reading UIInteractionQueueItem state threw: " + ex);
                return null;
            }
        }

        private static string GetTogetherCardLabel(UITogetherCard card)
        {
            try
            {
                string name = ReadActiveTranslatedText(card.Label);
                var parts = new List<string>();
                AddUnique(parts, name);

                string chance = card.SuccessChanceContainer != null
                    && card.SuccessChanceContainer.activeInHierarchy
                        ? ReadActiveText(card.SuccessChanceLabel)
                        : null;
                if (!string.IsNullOrEmpty(chance))
                {
                    AddUnique(parts, Loc.Get("together_card_chance", chance));
                }

                // These are the only requirements the native card itself exposes. Other
                // eligibility rules are useful diagnostics, but speaking those hidden authoring
                // conditions would turn every topic into an implementation dump.
                string trait = ReadActiveTranslatedText(card.LabelPersonalityTrait);
                if (!string.IsNullOrEmpty(trait))
                {
                    AddUnique(parts, Loc.Get("together_card_trait_requirement", trait));
                }

                if (card.ImageInventoryItem != null
                    && card.ImageInventoryItem.gameObject.activeInHierarchy)
                {
                    string itemName = GetTogetherCardInventoryRequirement(card);
                    if (!string.IsNullOrEmpty(itemName))
                    {
                        AddUnique(parts,
                            Loc.Get("together_card_inventory_requirement", itemName));
                    }
                }

                if (card.RelationshipLabelContainer != null
                    && card.RelationshipLabelContainer.activeInHierarchy)
                {
                    string relationship = ReadActiveText(card.RelationshipLabelText);
                    if (!string.IsNullOrEmpty(relationship))
                    {
                        AddUnique(parts,
                            Loc.Get("together_card_relationship_preview", relationship));
                    }
                }

                if (card.ChangeVariantButton != null
                    && card.ChangeVariantButton.activeInHierarchy)
                {
                    AddUnique(parts, Loc.Get("together_card_variants_available"));
                }

                return parts.Count == 0
                    ? name
                    : string.Join(Loc.Get("list_separator"), parts.ToArray());
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Reading UITogetherCard state threw: " + ex);
                return null;
            }
        }

        private static string GetTogetherCardInventoryRequirement(UITogetherCard card)
        {
            TogetherCard data = Settings.Get<Together>()?.GetCardByGUID(card.CardGUID);
            List<ContextRequirement> requirements = data != null
                ? TogetherManager.Instance?.GetAllCardRequirements(
                    data, onlyGetMultipleRequirements: false)
                : null;
            if (requirements == null)
            {
                return null;
            }

            ulong itemGuid = 0UL;
            foreach (ContextRequirement requirement in requirements)
            {
                if (requirement == null || !requirement.MustBeTrue
                    || requirement.Type != ContextRequirementType.HasItemInInventory
                    || requirement.CatalogueItem == 0UL)
                {
                    continue;
                }

                // UITogetherCard.Init overwrites its displayed inventory icon for every matching
                // requirement, so the final match—not the first—is the authoritative visual.
                itemGuid = requirement.CatalogueItem;
            }

            CatalogueItem item = itemGuid != 0UL
                ? Settings.Get<Items>()?.GetItemByGUID(itemGuid)
                : null;
            return item != null ? ItemDescription.GetName(item, 0UL) : null;
        }

        private static string GetCharacterListItemLabel(UICharacterListItem item)
        {
            try
            {
                AssetCharacter character = CharacterManager.Instance?
                    .GetCharacterByGUID(item.CharacterGUID);
                string name = character?.Data?.FullName;
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = ReadActiveText(item.LabelName);
                }
                if (string.IsNullOrWhiteSpace(name))
                {
                    return null;
                }

                string context = item.MainEmotionObject != null
                    && item.MainEmotionObject.activeInHierarchy
                        ? GetCharacterConversationContext(character)
                        : null;
                if (string.IsNullOrWhiteSpace(context))
                {
                    context = name.Trim();
                }

                string chance = item.SuccessChanceContainer != null
                    && item.SuccessChanceContainer.activeInHierarchy
                        ? ReadActiveText(item.SuccessChanceLabel)
                        : null;
                return string.IsNullOrEmpty(chance)
                    ? context
                    : Loc.Get("character_together_chance", context, chance);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Reading UICharacterListItem state threw: " + ex);
                return null;
            }
        }

        private static string GetCharacterMainEmotionLabel(UICharacterListItem item)
        {
            try
            {
                AssetCharacter character = CharacterManager.Instance?
                    .GetCharacterByGUID(item.CharacterGUID);
                if (character?.Data == null)
                {
                    return GetCharacterListItemLabel(item);
                }
                return GetCharacterConversationContext(character);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Reading a character card's main emotion threw: " + ex);
                return GetCharacterListItemLabel(item);
            }
        }

        /// <summary>
        /// Names one conversation participant and the main emotion the native Together view shows.
        /// </summary>
        internal static string GetCharacterConversationContext(ulong characterGuid)
        {
            try
            {
                return GetCharacterConversationContext(CharacterManager.Instance?
                    .GetCharacterByGUID(characterGuid));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError(
                    $"Reading conversation context for character {characterGuid} threw: {ex}");
                return null;
            }
        }

        private static string GetCharacterConversationContext(AssetCharacter character)
        {
            if (character?.Data == null)
            {
                return null;
            }

            string characterName = character.Data.FullName;
            if (string.IsNullOrWhiteSpace(characterName))
            {
                characterName = character.Data.ShortName;
            }
            if (string.IsNullOrWhiteSpace(characterName))
            {
                return null;
            }

            EmotionManager manager = EmotionManager.Instance;
            if (manager == null)
            {
                return characterName.Trim();
            }

            ulong emotionGuid = manager.GetMainEmotion(character);
            Emotion emotion = Settings.Get<Emotions>()?.GetEmotionByGUID(emotionGuid);
            string emotionName = emotion != null
                ? TranslationManager.Get("Emotion_" + emotion.DisplayName)
                : null;
            return string.IsNullOrWhiteSpace(emotionName)
                || emotionName == "Emotion_" + emotion?.DisplayName
                    ? characterName.Trim()
                    : Loc.Get("character_main_emotion",
                        characterName.Trim(), emotionName.Trim());
        }

        private static string GetInteractionListLabel(UIInteractionsList list)
        {
            try
            {
                string target = null;
                if (list.HeaderForItem != null && list.HeaderForItem.activeInHierarchy)
                {
                    target = ReadActiveTranslatedText(list.LabelParentInteractionUnitName);
                    UIInteractions window = list.ListDepth == 0
                        ? list.GetComponentInParent<UIInteractions>()
                        : null;
                    ItemObjectRoot clicked = window != null
                        ? ItemManager.Instance?.GetItemByInstanceID(
                            window.ClickedItemInstanceID)
                        : null;
                    if (clicked?.IsForSale == true)
                    {
                        string product = ItemDescription.GetShopProductLabel(clicked);
                        if (!string.IsNullOrEmpty(product))
                        {
                            target = product;
                        }
                    }
                    string hours = ReadActiveTranslatedText(list.LabelLotVisitHours);
                    if (!string.IsNullOrEmpty(hours))
                    {
                        target = string.IsNullOrEmpty(target)
                            ? hours
                            : Loc.Get("label_with_detail", target, hours);
                    }
                }
                else if (list.HeaderForCharacter != null && list.HeaderForCharacter.activeInHierarchy)
                {
                    target = ReadActiveText(list.LabelCharacterName);
                    string lifeStage = ReadActiveTranslatedText(list.LabelCharacterLifeStage);
                    if (!string.IsNullOrEmpty(lifeStage))
                    {
                        target = string.IsNullOrEmpty(target)
                            ? lifeStage
                            : Loc.Get("label_with_detail", target, lifeStage);
                    }
                }

                int count = 0;
                if (list.UIListInteractionItems?.CurrentItems != null)
                {
                    foreach (UIListItemBase entry in list.UIListInteractionItems.CurrentItems)
                    {
                        if (entry != null && entry.gameObject.activeInHierarchy)
                        {
                            count++;
                        }
                    }
                }

                string countLabel = count == 1
                    ? Loc.Get("interaction_list_one")
                    : Loc.Get("interaction_list_many", count);
                return string.IsNullOrEmpty(target)
                    ? countLabel
                    : Loc.Get("label_with_detail", target, countLabel);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Reading UIInteractionsList state threw: {ex}");
                return null;
            }
        }

        private static string GetNeedLabel(UINeedsItem item)
        {
            try
            {
                ResolveNeedReflection();
                var need = _needField?.GetValue(item) as Need;
                var character = _needCharacterField?.GetValue(item) as AssetCharacter;
                if (need == null || character == null || NeedManager.Instance == null)
                {
                    return null;
                }

                string name = ResolveTranslation(need.TranslationKey) ?? need.DisplayName;
                if (string.IsNullOrEmpty(name))
                {
                    return null;
                }

                var visibleValue = NeedManager.Instance.GetNeedValueForUI(need.GUID, character);
                float max = _needMaxField != null
                    ? (float)_needMaxField.GetValue(item)
                    : NeedManager.Instance.GetNeedCap(need.GUID, character);
                bool relieving = NeedManager.Instance.IsRelieving(need.GUID, character);

                // Mirror UINeedsItem.InitializeNeed's presentation rounding. This describes the
                // number of filled segments the sighted UI actually draws; the underlying float is
                // never exposed with extra precision.
                float displayed = !need.ActivatedByDefault
                    ? Mathf.Floor(visibleValue.Item1)
                    : ((!relieving || visibleValue.Item1 <= 1f)
                        ? Mathf.Ceil(visibleValue.Item1)
                        : Mathf.Floor(visibleValue.Item1));
                int cap = Mathf.Max(0, Mathf.FloorToInt(max));
                int filled = Mathf.Clamp(Mathf.RoundToInt(displayed), 0, cap);

                var states = new List<string>();
                if (visibleValue.Item2
                    && visibleValue.Item3 == StatusEffectMaskingTypes.DisableAndGrayOut)
                {
                    states.Add(Loc.Get("need_ignored"));
                }
                if (NeedManager.Instance.IsNeedCritical(need.GUID, character))
                {
                    states.Add(Loc.Get("need_critical"));
                }
                if (relieving)
                {
                    states.Add(Loc.Get("need_relieving"));
                }

                return states.Count == 0
                    ? Loc.Get("need_value", name, filled, cap)
                    : Loc.Get("need_value_state", name, filled, cap,
                        string.Join(Loc.Get("list_separator"), states.ToArray()));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Reading UINeedsItem state threw: {ex}");
                return null;
            }
        }

        private static string GetNeedName(UINeedsItem item)
        {
            try
            {
                ResolveNeedReflection();
                var need = _needField?.GetValue(item) as Need;
                return need == null
                    ? null
                    : (ResolveTranslation(need.TranslationKey) ?? need.DisplayName);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Reading UINeedsItem need name threw: {ex}");
                return null;
            }
        }

        private static void ResolveNeedReflection()
        {
            if (_needReflectionResolved)
            {
                return;
            }

            _needReflectionResolved = true;
            _needField = AccessTools.Field(typeof(UINeedsItem), "_need");
            _needCharacterField = AccessTools.Field(typeof(UINeedsItem), "_character");
            _needMaxField = AccessTools.Field(typeof(UINeedsItem), "_max");

            if (_needField == null || _needCharacterField == null || _needMaxField == null)
            {
                Plugin.Log.LogError(
                    "Could not resolve UINeedsItem._need/_character/_max. Need rows will retain "
                  + "their tooltip label but cannot report the visible value.");
            }
        }

        private static string GetEmotionLabel(UIEmotionsItem2 item)
        {
            try
            {
                string name = ReadActiveTranslatedText(item.Label);
                if (string.IsNullOrEmpty(name))
                {
                    return null;
                }

                string value = ReadActiveText(item.ValueLabel);
                string label = string.IsNullOrEmpty(value)
                    ? name
                    : Loc.Get("emotion_value", name, value);

                string breakdown = string.IsNullOrEmpty(value)
                    ? null
                    : GetEmotionBreakdown(item);
                if (!string.IsNullOrEmpty(breakdown))
                {
                    label = Loc.Get("emotion_with_breakdown", label, breakdown);
                }

                return item.WantsAvailableNotification != null
                    && item.WantsAvailableNotification.activeInHierarchy
                        ? Loc.Get("emotion_reaction_available", label)
                        : label;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Reading UIEmotionsItem2 state threw: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Includes the same sourced contribution rows as the game's numeric breakdown tooltip.
        /// This makes an existing value such as Angry 3 explainable even when it was already
        /// present when the save loaded and therefore had no proactive change event to announce.
        /// </summary>
        private static string GetEmotionBreakdown(UIEmotionsItem2 item)
        {
            if (!_emotionReflectionResolved)
            {
                _emotionReflectionResolved = true;
                _emotionEffectsAndReasonsField = AccessTools.Field(
                    typeof(UIEmotionsItem2), "_effectsAndReasons");
                if (_emotionEffectsAndReasonsField == null)
                {
                    Plugin.Log.LogError(
                        "Could not resolve UIEmotionsItem2._effectsAndReasons. Emotion rows "
                      + "will report totals without their native breakdown reasons.");
                }
            }

            if (_emotionEffectsAndReasonsField == null)
            {
                return null;
            }

            var reasons = _emotionEffectsAndReasonsField.GetValue(item)
                as ValueTuple<float, string>[];
            if (reasons == null || reasons.Length == 0)
            {
                return null;
            }

            var entries = new List<string>();
            foreach (ValueTuple<float, string> reason in reasons)
            {
                string source = ScreenReader.NormalizeForOutput(reason.Item2);
                if (string.IsNullOrWhiteSpace(source))
                {
                    continue;
                }

                int amount = Mathf.RoundToInt(reason.Item1);
                string signedAmount = amount > 0 ? "+" + amount : amount.ToString();
                string entry = Loc.Get("emotion_breakdown_entry", signedAmount, source);
                if (!entries.Contains(entry))
                {
                    entries.Add(entry);
                }
            }

            return entries.Count == 0
                ? null
                : string.Join(Loc.Get("list_separator"), entries.ToArray());
        }

        private static string GetThoughtBubbleLabel(UIThoughtBubblesItem item)
        {
            try
            {
                Sprite current = item.ThoughtBubbleImage != null
                    ? item.ThoughtBubbleImage.sprite
                    : null;
                if (current == null || AssetManager.Instance == null)
                {
                    return Loc.Get("thought_bubble");
                }

                ThoughtBubbles settings = Settings.Get<ThoughtBubbles>();
                ThoughtBubble match = null;
                if (settings?.AllThoughtBubbles != null)
                {
                    foreach (ThoughtBubble bubble in settings.AllThoughtBubbles)
                    {
                        if (bubble != null
                            && AssetManager.Instance.GetSprite(bubble.Sprite) == current)
                        {
                            match = bubble;
                            break;
                        }
                    }
                }

                if (match == null)
                {
                    return Loc.Get("thought_bubble");
                }

                Goals goals = Settings.Get<Goals>();
                if (goals != null && match.GUID == goals.RequestAvailableThoughtBubble)
                {
                    return Loc.Get("thought_request_available");
                }
                if (goals != null && match.GUID == goals.RequestCompletedThoughtBubble)
                {
                    return Loc.Get("thought_request_completed");
                }

                return Loc.Get("thought_bubble");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Reading UIThoughtBubblesItem sprite mapping threw: {ex}");
                return Loc.Get("thought_bubble");
            }
        }

        private static bool IsWithin(Component source, GameObject root)
        {
            return source != null && root != null
                && (source.gameObject == root || source.transform.IsChildOf(root.transform));
        }

        /// <summary>Names the eight icon-only character-information tabs from their public fields.</summary>
        private static string GetCharacterTabLabel(Component source)
        {
            try
            {
                UICharacterSubMenuBar bar = source.GetComponentInParent<UICharacterSubMenuBar>();
                if (bar == null)
                {
                    return null;
                }

                if (source.gameObject == bar.ButtonThoughts?.gameObject)
                {
                    return Loc.Get("tab_thoughts");
                }
                if (source.gameObject == bar.ButtonProfile?.gameObject)
                {
                    return WithNotification(
                        Loc.Get("tab_profile"), bar.ProfileRedNotificationNumber,
                        bar.ProfileRedNotificationNumberLabel);
                }
                if (source.gameObject == bar.ButtonSkills?.gameObject)
                {
                    return Loc.Get("tab_skills");
                }
                if (source.gameObject == bar.ButtonSocial?.gameObject)
                {
                    return Loc.Get("tab_relationships");
                }
                if (source.gameObject == bar.ButtonOccupation?.gameObject)
                {
                    return WithNotification(
                        Loc.Get("tab_occupations"), bar.OccupationsRedNotificationNumber,
                        bar.OccupationsRedNotificationNumberLabel);
                }
                if (source.gameObject == bar.ButtonInventory?.gameObject)
                {
                    return Loc.Get("tab_inventory");
                }
                if (source.gameObject == bar.ButtonMemories?.gameObject)
                {
                    return Loc.Get("tab_memories");
                }
                if (source.gameObject == bar.ButtonGoals?.gameObject)
                {
                    return WithNotification(
                        Loc.Get("tab_goals"), bar.GoalsRedNotificationNumber,
                        bar.GoalsRedNotificationNumberLabel);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Reading UICharacterSubMenuBar tab identity threw: {ex}");
            }

            return null;
        }

        private static string WithNotification(string label, GameObject indicator, TMP_Text countLabel)
        {
            if (indicator == null || !indicator.activeInHierarchy)
            {
                return label;
            }

            string count = ReadActiveText(countLabel);
            int numeric;
            if (int.TryParse(count, out numeric) && numeric == 1)
            {
                return Loc.Get("notification_one", label);
            }

            return string.IsNullOrEmpty(count)
                ? Loc.Get("notification_present", label)
                : Loc.Get("notification_many", label, count);
        }

        private static string GetGameBarLabel(Component source)
        {
            try
            {
                UIGameBar bar = source.GetComponentInParent<UIGameBar>();
                if (bar == null)
                {
                    return null;
                }

                if (IsWithin(source, bar.ButtonMoney))
                {
                    string balance = ReadActiveText(bar.LabelMoney);
                    return string.IsNullOrEmpty(balance)
                        ? Loc.Get("money")
                        : Loc.Get("money_balance", balance);
                }

                if (IsWithin(source, bar.ButtonMailbox))
                {
                    string count = bar.MailboxNotification != null
                        && bar.MailboxNotification.activeInHierarchy
                            ? ReadActiveText(bar.LabelMailboxNotificationCount)
                            : null;
                    int numeric;
                    if (int.TryParse(count, out numeric) && numeric == 1)
                    {
                        return Loc.Get("mailbox_unread_one");
                    }

                    return string.IsNullOrEmpty(count)
                        ? Loc.Get("mailbox")
                        : Loc.Get("mailbox_unread_many", count);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Reading UIGameBar semantic label threw: {ex}");
            }

            return null;
        }

        private static string GetPersonalityLevelLabel(Component source)
        {
            try
            {
                UIProfilePersonalityLevel level =
                    source.GetComponentInParent<UIProfilePersonalityLevel>();
                if (level == null)
                {
                    return null;
                }

                if (IsWithin(source, level.ButtonLevelUp))
                {
                    string action = ReadActiveTranslatedText(level.ButtonLevelLabel);
                    return string.IsNullOrEmpty(action) ? Loc.Get("profile_level_up") : action;
                }

                string number = ReadActiveText(level.LabelCurrentLevel);
                string experience = level.LabelExperience == null
                    ? null
                    : ReadOpenerText(level.LabelExperience, source);
                if (string.IsNullOrEmpty(number))
                {
                    return experience;
                }

                return string.IsNullOrEmpty(experience)
                    ? Loc.Get("profile_level_number", number)
                    : Loc.Get("profile_level", number, experience);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Reading UIProfilePersonalityLevel state threw: {ex}");
                return null;
            }
        }

        private static string GetProgressTutorialLabel(
            UISkillsInProgressAndUpcomingEvents panel, Component source)
        {
            if (panel == null || source == null)
            {
                return null;
            }

            try
            {
                UIContainer tutorial = null;
                if (panel.UITutorialOverlayFirstBill != null
                    && panel.UITutorialOverlayFirstBill.IsVisible
                    && IsWithin(source, panel.UITutorialOverlayFirstBill.gameObject))
                {
                    tutorial = panel.UITutorialOverlayFirstBill;
                }
                else if (panel.UITutorialOverlayStorycard != null
                    && panel.UITutorialOverlayStorycard.IsVisible
                    && IsWithin(source, panel.UITutorialOverlayStorycard.gameObject))
                {
                    tutorial = panel.UITutorialOverlayStorycard;
                }

                string instruction = ReadTutorialInstruction(tutorial);
                return string.IsNullOrEmpty(instruction)
                    ? null
                    : Loc.Get("objective_guidance", instruction);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Reading progress tutorial overlay threw: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Names the compact HUD's newspaper and mailbox shortcuts from the exact counters that
        /// <see cref="UISkillsInProgressAndUpcomingEvents.Update"/> writes into them.
        /// </summary>
        /// <remarks>
        /// TEST 56 exposed both controls as a bare "1". The number was valid, but scraping its
        /// child label discarded the identity supplied by the owning field. Keep this deliberately
        /// compact: identity plus unread count fixes the ambiguity without adding instructions.
        /// </remarks>
        private static string GetProgressShortcutLabel(
            UISkillsInProgressAndUpcomingEvents panel, Component source)
        {
            if (panel == null || source == null)
            {
                return null;
            }

            try
            {
                if (IsWithin(source, panel.ButtonNewspaper))
                {
                    string count = ReadActiveText(panel.LabelNewspaperNotificationCount);
                    int numeric;
                    if (int.TryParse(count, out numeric))
                    {
                        if (numeric == 1)
                        {
                            return Loc.Get("newspaper_unread_one");
                        }
                        if (numeric > 1)
                        {
                            return Loc.Get("newspaper_unread_many", count);
                        }
                    }

                    return Loc.Get("newspaper");
                }

                if (IsWithin(source, panel.ButtonMailbox))
                {
                    string count = ReadActiveText(panel.LabelMailboxNotificationCount);
                    int numeric;
                    if (int.TryParse(count, out numeric))
                    {
                        if (numeric == 1)
                        {
                            return Loc.Get("mailbox_unread_one");
                        }
                        if (numeric > 1)
                        {
                            return Loc.Get("mailbox_unread_many", count);
                        }
                    }

                    return Loc.Get("mailbox");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Reading progress shortcut identity threw: {ex}");
            }

            return null;
        }

        private static string GetThoughtsSummary(UIThoughts thoughts)
        {
            try
            {
                var parts = new List<string>();
                AddGuidance(parts, thoughts.UIWants2?.TutorialOverlay);
                AddGuidance(parts, thoughts.UINeeds?.NeedsTutorial);
                AddGuidance(parts, thoughts.UIEmotions2?.Overlay);

                string wants = GetActiveWantsSummary(GetSelectedCharacter());
                AddUnique(parts, wants);

                return FormatPanelSummary(Loc.Get("panel_thoughts"), parts);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Building UIThoughts summary threw: {ex}");
                return Loc.Get("panel_thoughts");
            }
        }

        private static string GetProgressAndEventsSummary(
            UISkillsInProgressAndUpcomingEvents panel)
        {
            try
            {
                var parts = new List<string>();
                AddGuidance(parts, panel.UITutorialOverlayFirstBill);
                AddGuidance(parts, panel.UITutorialOverlayStorycard);

                bool hasRenderedGoal = panel.UIGoalItem != null
                    && panel.UIGoalItem.gameObject.activeInHierarchy;
                if (hasRenderedGoal)
                {
                    AddUnique(parts, GetGoalLabel(panel.UIGoalItem));
                }
                else
                {
                    // This HUD registers during loading, while its Update deliberately declines to
                    // initialize the card until State.Game. The selected character model already
                    // has the tracked goal, so use it rather than losing the objective at load.
                    AddUnique(parts, GetTrackedGoalSummary(GetSelectedCharacter()));
                }

                var upcoming = new List<string>();
                if (panel.UpcomingEventsItemList?.CurrentItems != null)
                {
                    foreach (UIListItemBase entry in panel.UpcomingEventsItemList.CurrentItems)
                    {
                        var item = entry as UIUpcomingEventsItem;
                        if (item == null || !item.gameObject.activeInHierarchy)
                        {
                            continue;
                        }

                        string label = ReadActiveTranslatedText(item.Label);
                        string action = item.ButtonObject != null && item.ButtonObject.activeInHierarchy
                            ? ReadActiveTranslatedText(item.LabelGoToWork)
                            : null;
                        AddUnique(upcoming, string.IsNullOrEmpty(action)
                            ? label
                            : (string.IsNullOrEmpty(label)
                                ? action
                                : Loc.Get("label_with_detail", label, action)));
                    }
                }
                if (upcoming.Count > 0)
                {
                    AddUnique(parts, Loc.Get("upcoming_events_summary",
                        string.Join(Loc.Get("list_separator"), upcoming.ToArray())));
                }

                if (panel.ButtonDailyStoryCards != null
                    && panel.ButtonDailyStoryCards.IsVisible)
                {
                    AddUnique(parts, Loc.Get("daily_story_cards_ready"));
                }

                return FormatPanelSummary(Loc.Get("panel_progress_events"), parts);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Building progress and events summary threw: {ex}");
                return Loc.Get("panel_progress_events");
            }
        }

        private static string GetHouseholdSummary(UICharacters characters)
        {
            try
            {
                var parts = new List<string>();
                AddGuidance(parts, characters.TutorialOverlayGoToBuildMode);

                var names = new List<string>();
                if (characters.List?.CurrentItems != null)
                {
                    foreach (UIListItemBase entry in characters.List.CurrentItems)
                    {
                        var character = entry as UICharacterItem;
                        if (character == null || !character.gameObject.activeInHierarchy)
                        {
                            continue;
                        }

                        AddUnique(names, GetTypedLabel(character));
                    }
                }

                if (names.Count > 0)
                {
                    AddUnique(parts, Loc.Get("household_members",
                        string.Join(Loc.Get("list_separator"), names.ToArray())));
                }

                return FormatPanelSummary(Loc.Get("panel_household"), parts);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Building UICharacters summary threw: {ex}");
                return Loc.Get("panel_household");
            }
        }

        private static string GetOfferedWantsSummary(UIOfferedWants offered)
        {
            try
            {
                var parts = new List<string>();
                AddGuidance(parts, offered.TutorialOverlay);

                var choices = new List<string>();
                if (offered.OfferedWantsList?.CurrentItems != null)
                {
                    foreach (UIListItemBase entry in offered.OfferedWantsList.CurrentItems)
                    {
                        var item = entry as UIOfferedWantsItem;
                        if (item != null && item.gameObject.activeInHierarchy)
                        {
                            AddUnique(choices, ReadActiveText(item.WantNameLabel));
                        }
                    }
                }

                if (choices.Count > 0)
                {
                    AddUnique(parts, Loc.Get("offered_wants_summary",
                        string.Join(Loc.Get("list_separator"), choices.ToArray())));
                }

                return FormatPanelSummary(Loc.Get("panel_offered_wants"), parts);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Building UIOfferedWants summary threw: {ex}");
                return Loc.Get("panel_offered_wants");
            }
        }

        /// <summary>
        /// Gives the request-board window its native localized identity and a concise offer index.
        /// Individual request cards retain their full objective descriptions while navigating.
        /// </summary>
        private static string GetOfferedRequestsSummary(UIOfferedRequests offered)
        {
            string header = ResolveTranslation("UIOfferedRequests_Header")
                ?? Loc.Get("panel_offered_requests");

            try
            {
                var requests = new List<string>();
                if (offered.RequestList?.CurrentItems != null)
                {
                    foreach (UIListItemBase entry in offered.RequestList.CurrentItems)
                    {
                        var request = entry as UIGoalItem;
                        if (request == null || !request.gameObject.activeInHierarchy)
                        {
                            continue;
                        }

                        string title = ReadActiveTranslatedText(request.Header?.LineLabel);
                        string offeredBy = request.GoalOffered?.ThumbnailTooltip == null
                            ? null
                            : ReadOpenerText(request.GoalOffered.ThumbnailTooltip,
                                request.GoalOffered);
                        string label = string.IsNullOrEmpty(offeredBy)
                            ? title
                            : (string.IsNullOrEmpty(title)
                                ? offeredBy
                                : Loc.Get("label_with_detail", title, offeredBy));
                        if (!string.IsNullOrEmpty(label))
                        {
                            requests.Add(label);
                        }
                    }
                }

                if (requests.Count > 0)
                {
                    string joined = string.Join(
                        Loc.Get("list_separator"), requests.ToArray());
                    string summary = requests.Count == 1
                        ? Loc.Get("offered_requests_one", joined)
                        : Loc.Get("offered_requests_many", requests.Count, joined);
                    return Loc.Get("panel_summary", header, summary);
                }

                string empty = offered.HasNoRequests != null
                    && offered.HasNoRequests.activeInHierarchy
                        ? ResolveTranslation("UIOfferedRequests_NoMoreRequests")
                        : null;
                return string.IsNullOrEmpty(empty)
                    ? header
                    : Loc.Get("panel_summary", header, empty);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Building UIOfferedRequests summary threw: {ex}");
                return header;
            }
        }

        /// <summary>Builds the current tutorial, tracked-goal and active-wants summary.</summary>
        internal static string GetCurrentObjectiveSummary(AssetCharacter character)
        {
            try
            {
                var parts = new List<string>();
                UISkillsInProgressAndUpcomingEvents panel =
                    UI.GetOrNull<UISkillsInProgressAndUpcomingEvents>(0);
                if (panel != null)
                {
                    AddGuidance(parts, panel.UITutorialOverlayFirstBill);
                    AddGuidance(parts, panel.UITutorialOverlayStorycard);
                }

                // Objective-change speech is model-backed. The rendered card is ideal for a window
                // landmark, but can still be one UI refresh behind the status that triggered this
                // observer; rebuilding from the same settings/data used by the card avoids losing
                // a completion announcement when that race occurs.
                AddUnique(parts, GetTrackedGoalSummary(character));
                AddUnique(parts, GetActiveWantsSummary(character));

                return parts.Count == 0
                    ? null
                    : string.Join(Loc.Get("list_separator"), parts.ToArray());
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Building current objective summary threw: {ex}");
                return null;
            }
        }

        private static string GetTrackedGoalSummary(AssetCharacter character)
        {
            if (character?.Data == null || character.Data.TrackedGoal == 0UL)
            {
                return null;
            }

            Goals goals = Settings.Get<Goals>();
            Goal goal = goals?.GetGoalByGUID(character.Data.TrackedGoal);
            if (goal == null)
            {
                return null;
            }

            AssetCharacterGoalData savedGoal = GoalsManager.Instance.GetGoalDataFromGoalGUID(
                character, character.Data.TrackedGoal);
            AssetCharacter target = savedGoal != null && savedGoal.TargetCharacter != 0UL
                ? CharacterManager.Instance.GetCharacterByGUID(savedGoal.TargetCharacter)
                : null;
            string title = target == null
                ? ResolveTranslation("Goal_" + goal.DisplayName)
                : ResolveTranslation("Goal_" + goal.DisplayName, target.Data.FullName);
            title = title ?? goal.DisplayName;
            var objectives = new List<string>();
            if (goal.Objectives != null)
            {
                foreach (GoalObjective objective in goal.Objectives)
                {
                    if (objective == null)
                    {
                        continue;
                    }

                    AssetCharacterGoalObjectiveData data =
                        GoalsManager.Instance.GetGoalObjectiveData(
                            character, character.Data.TrackedGoal, objective.GUID);
                    string label = GetGoalObjectiveLabel(character, objective, data);
                    if (!string.IsNullOrEmpty(label))
                    {
                        bool complete = objective.ObjectiveType == GoalObjectiveType.Want
                            ? data?.WantData?.Status == AssetCharacterWantStatus.Completed
                            : data != null
                                && GoalsManager.Instance.GetGoalObjectiveProgress(objective, data) >= 1f;
                        AddUnique(objectives, complete
                            ? Loc.Get("objective_completed", label)
                            : label);
                    }
                }
            }

            if (objectives.Count == 0)
            {
                return title;
            }

            string joined = string.Join(Loc.Get("list_separator"), objectives.ToArray());
            return string.IsNullOrEmpty(title) ? joined : Loc.Get("goal_summary", title, joined);
        }

        private static string GetGoalObjectiveLabel(
            AssetCharacter character, GoalObjective objective, AssetCharacterGoalObjectiveData data)
        {
            if (objective == null)
            {
                return null;
            }

            switch (objective.ObjectiveType)
            {
                case GoalObjectiveType.Want:
                    string[] parameters = new string[0];
                    ulong targetGuid = data?.WantData?.CharacterTargetGUID ?? 0UL;
                    if (targetGuid != 0UL)
                    {
                        AssetCharacter target = CharacterManager.Instance.GetCharacterByGUID(targetGuid);
                        if (target != null)
                        {
                            parameters = new[] { character.Data.FullName, target.Data.FullName };
                        }
                    }
                    return ResolveTranslation(
                        objective.TranslationKey, objective.DisplayNameParameters, parameters);

                case GoalObjectiveType.Money:
                    return ResolveTranslation(objective.TranslationKey,
                        objective.MoneyAmount.ToString());

                case GoalObjectiveType.Item:
                case GoalObjectiveType.SkinnedItem:
                    CatalogueItem item = Settings.Get<Items>().GetItemByGUID(objective.ItemGUID);
                    string itemName = objective.UseObjectiveDisplayNameAsDescription
                        ? ResolveTranslation(objective.TranslationKey)
                        : ResolveTranslation(ItemManager.Instance.GetSkinnedDisplayName(
                            item, objective.SkinGUID));
                    string key = objective.ItemAmount > 1
                        ? "UIGoals_ObjectiveIsMultipleItems"
                        : "UIGoals_ObjectiveIsItem";
                    return ResolveTranslation(key, objective.ItemAmount.ToString(), itemName);

                default:
                    return ResolveTranslation(objective.TranslationKey);
            }
        }

        private static string GetActiveWantsSummary(AssetCharacter character)
        {
            if (character?.Data?.Wants == null || WantsManager.Instance == null)
            {
                return null;
            }

            Wants wants = Settings.Get<Wants>();
            var names = new List<string>();
            foreach (AssetCharacterWantData data in character.Data.Wants)
            {
                if (data == null)
                {
                    continue;
                }

                Want want = wants?.GetWantByGUID(data.WantGUID);
                if (want == null || want.WantType != WantTypes.Want
                    || data.Status != AssetCharacterWantStatus.Active)
                {
                    continue;
                }

                string name = WantsManager.Instance.GetWantFullName(want, data);
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                string remaining = GetVisibleWantTimeRemaining(want, data);
                AddUnique(names, string.IsNullOrEmpty(remaining)
                    ? name
                    : Loc.Get("label_with_detail", name,
                        Loc.Get("objective_time_remaining", remaining)));
            }

            return names.Count == 0
                ? null
                : Loc.Get("active_wants_summary",
                    string.Join(Loc.Get("list_separator"), names.ToArray()));
        }

        /// <summary>
        /// Mirrors only the countdown a failable want's native time-limit bar and tooltip render.
        /// Requests and ordinary goal objectives deliberately have no inferred deadline.
        /// </summary>
        private static string GetVisibleWantTimeRemaining(
            Want want, AssetCharacterWantData data)
        {
            if (!want.CanBeFailed || want.FailRequirement == null
                || want.FailRequirement.Type != ContextRequirementType.TimeSince)
            {
                return null;
            }

            double remainingMinutes = want.FailRequirement.MinutesValue
                - (ParaTime.TotalMinutes - data.Timestamp);
            return FormatService.GetElapsedTimeFromMinutes(
                (float)Math.Max(0d, remainingMinutes));
        }

        private static AssetCharacter GetSelectedCharacter()
        {
            if (PlayerManager.Instance == null || AssetManager.Instance == null)
            {
                return null;
            }

            ulong guid = PlayerManager.Instance.HybridPlayer1.Player.GetSelectedCharacterGUID();
            return guid == 0UL ? null : AssetManager.Instance.GetCharacter(guid);
        }

        private static void AddGuidance(List<string> parts, UIContainer tutorial)
        {
            string instruction = ReadTutorialInstruction(tutorial);
            if (!string.IsNullOrEmpty(instruction))
            {
                AddUnique(parts, Loc.Get("objective_guidance", instruction));
            }
        }

        internal static string ReadTutorialInstruction(UIContainer tutorial)
        {
            if (tutorial == null || !tutorial.IsVisible || !tutorial.gameObject.activeInHierarchy)
            {
                return null;
            }

            var parts = new List<string>();
            TMP_Text[] labels = tutorial.GetComponentsInChildren<TMP_Text>(includeInactive: false);
            foreach (TMP_Text label in labels)
            {
                // Button captions are actions, not tutorial prose. Keeping them out of the body
                // avoids "OK. Tutorial: description; OK" when the action is contextualized above.
                Focusable action = label?.GetComponentInParent<Focusable>();
                if (action != null && action.transform.IsChildOf(tutorial.transform))
                {
                    continue;
                }

                string text = label == null ? null : label.text;
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                string trimmed = text.Trim();
                if (ContainsUnresolvedPlaceholder(trimmed) || !ContainsLetter(trimmed)
                    || IsPrefabPlaceholder(trimmed))
                {
                    continue;
                }

                AddUnique(parts, trimmed);
            }

            return parts.Count == 0
                ? null
                : string.Join(Loc.Get("list_separator"), parts.ToArray());
        }

        private static bool ContainsLetter(string value)
        {
            foreach (char c in value)
            {
                if (char.IsLetter(c))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsPrefabPlaceholder(string value)
        {
            string normalized = value.Trim().ToUpperInvariant();
            return normalized == "TITLE" || normalized == "LABEL"
                || normalized == "TEXT" || normalized == "DESCRIPTION";
        }

        private static void AddUnique(List<string> parts, string value)
        {
            if (!string.IsNullOrWhiteSpace(value) && !parts.Contains(value.Trim()))
            {
                parts.Add(value.Trim());
            }
        }

        private static string FormatPanelSummary(string panelName, List<string> parts)
        {
            return parts.Count == 0
                ? panelName
                : Loc.Get("panel_summary", panelName,
                    string.Join(Loc.Get("list_separator"), parts.ToArray()));
        }

        /// <summary>
        /// Describes a newspaper article from the exact state UINewspaperArticleItem currently
        /// exposes. Its claimed/expired label is pooled and retains stale text while inactive, so
        /// the generic include-inactive scrape is not authoritative for this item type.
        /// </summary>
        private static string GetNewspaperArticleLabel(UINewspaperArticleItem article)
        {
            try
            {
                string title = ReadActiveText(article.LabelTitle);
                string status = ReadActiveTranslatedText(article.TranslatedTextClaimedOrExpired);
                if (!string.IsNullOrEmpty(status))
                {
                    return string.IsNullOrEmpty(title)
                        ? status
                        : Loc.Get("newspaper_article_status", title, status);
                }

                string action = article.ButtonAction != null
                    && article.ButtonAction.activeInHierarchy
                        ? ReadActiveTranslatedText(article.TranslatedTextButtonAction)
                        : null;
                if (!string.IsNullOrEmpty(action))
                {
                    return string.IsNullOrEmpty(title)
                        ? action
                        : Loc.Get("newspaper_article_action", title, action);
                }

                return title;
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"Reading UINewspaperArticleItem state threw: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Gives the fixed tracked-goal card one concise landmark label: the game's localized goal
        /// title followed by the localized labels of its live objective rows. This deliberately
        /// excludes hover-only controls and unrelated pooled descendants.
        /// </summary>
        private static string GetGoalLabel(UIGoalItem goal)
        {
            try
            {
                string title = ReadActiveTranslatedText(goal.Header?.LineLabel);
                List<string> objectives = new List<string>();

                if (goal.GoalItemLines?.CurrentItems != null)
                {
                    foreach (UIListItemBase entry in goal.GoalItemLines.CurrentItems)
                    {
                        var line = entry as UIGoalItemLine;
                        if (line == null || !line.gameObject.activeInHierarchy)
                        {
                            continue;
                        }

                        string objective = ReadActiveTranslatedText(line.LineLabel);
                        if (!string.IsNullOrEmpty(objective) && !objectives.Contains(objective))
                        {
                            objectives.Add(objective);
                        }
                    }
                }

                if (objectives.Count == 0)
                {
                    return title;
                }

                string joined = string.Join("; ", objectives.ToArray());
                return string.IsNullOrEmpty(title)
                    ? joined
                    : Loc.Get("goal_summary", title, joined);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"Reading UIGoalItem state threw: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Describes the final line of an offered request from its live description, requester
        /// tooltip and, when the prefab wiring confirms it, its accept action.
        /// </summary>
        private static string GetOfferedRequestLineLabel(UIGoalOfferedLine offered)
        {
            try
            {
                var parts = new List<string>();

                // UIGoalItem.OnRequestAccepted is prefab-wired rather than called in source. The
                // normal action reader verifies that wiring before it is allowed to say "Accept".
                AddUnique(parts, GetActionLabel(offered));
                if (offered.ThumbnailTooltip != null)
                {
                    AddUnique(parts, ReadOpenerText(offered.ThumbnailTooltip, offered));
                }
                AddUnique(parts, ReadActiveTranslatedText(offered.Description));

                return parts.Count == 0
                    ? null
                    : string.Join(Loc.Get("list_separator"), parts.ToArray());
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Reading UIGoalOfferedLine state threw: {ex}");
                return null;
            }
        }

        private static string ReadActiveTranslatedText(TranslatedText translated)
        {
            return translated == null || !translated.gameObject.activeInHierarchy
                ? null
                : ReadActiveText(translated.Text);
        }

        private static string ReadActiveText(TMP_Text text)
        {
            if (text == null || !text.gameObject.activeInHierarchy
                || string.IsNullOrEmpty(text.text))
            {
                return null;
            }

            string trimmed = text.text.Trim();
            return trimmed.Length == 0 || ContainsUnresolvedPlaceholder(trimmed)
                || trimmed == "99:99:99"
                    ? null
                    : trimmed;
        }

        /// <summary>Describes one native job-offer row as the action it performs.</summary>
        /// <remarks>
        /// <c>UIOccupationOfferItem.OnClickOnItem</c> has exactly two branches. It shows the game's
        /// own not-qualified floating text when the same application-points calculation used here
        /// is below the offer's requirement; otherwise it enters the native schedule-selection
        /// flow. Position, company, starting rank and salary are the fields the row renders.
        /// </remarks>
        private static string GetOccupationOfferLabel(UIOccupationOfferItem item)
        {
            try
            {
                OccupationOffer offer = item.Offer;
                UIOccupationOffers ui = item.UIOccupationOffers;
                AssetCharacter character = ui?.Character;
                if (offer == null || character == null)
                {
                    return null;
                }

                Occupations occupations = Settings.Get<Occupations>();
                Occupation occupation = occupations?.GetOccupationByGUID(offer.Occupation);
                if (occupation == null)
                {
                    return null;
                }

                string position = ReadActiveTranslatedText(item.LabelPosition);
                if (string.IsNullOrWhiteSpace(position))
                {
                    ulong gender = PersonalityManager.Instance != null
                        ? PersonalityManager.Instance.GetGenderFromPronouns(character)
                        : 0UL;
                    position = gender == 0UL
                        ? TranslationManager.Get("OccupationName_" + occupation.DisplayName)
                        : TranslationManager.Get(
                            "OccupationName_" + occupation.DisplayName, gender);
                }
                position = ScreenReader.NormalizeForOutput(position?.Trim());
                if (string.IsNullOrWhiteSpace(position))
                {
                    return null;
                }

                Company company = occupations.GetCompanyByGUID(occupation.Company);
                string companyName = ReadActiveTranslatedText(item.LabelCompany);
                if (string.IsNullOrWhiteSpace(companyName) && company != null)
                {
                    companyName = TranslationManager.Get(
                        "CompanyName_" + company.DisplayName);
                }
                companyName = ScreenReader.NormalizeForOutput(companyName?.Trim());

                var parts = new List<string>();
                AddUnique(parts, string.IsNullOrWhiteSpace(companyName)
                    ? Loc.Get("job_offer_apply", position)
                    : Loc.Get("job_offer_apply_at", position, companyName));
                AddUnique(parts, Loc.Get("job_offer_rank", offer.StartingRank));

                int salary = OccupationsManager.Instance.GetStartingSalary(
                    character, occupation, offer.StartingRank);
                string salaryText = ScreenReader.NormalizeForOutput(
                    FormatService.GetMoney(salary));
                AddUnique(parts, Loc.Get("job_offer_salary", salaryText));

                int applicationPoints =
                    OccupationsManager.Instance.GetCharacterTotalApplicationPoints(
                        character, occupation, offer);
                AddUnique(parts, applicationPoints < offer.RequiredApplicationPoints
                    ? Loc.Get(
                        "job_offer_not_qualified",
                        applicationPoints,
                        offer.RequiredApplicationPoints)
                    : (offer.RequiredApplicationPoints > 0
                        ? Loc.Get(
                            "job_offer_qualified_points",
                            applicationPoints,
                            offer.RequiredApplicationPoints)
                        : Loc.Get("job_offer_qualified")));

                return string.Join(Loc.Get("list_comma"), parts.ToArray());
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError(
                    "Reading UIOccupationOfferItem typed label threw: " + ex);
                return null;
            }
        }

        /// <summary>
        /// Reads a furniture or surface catalogue entry from its current native type and data.
        /// </summary>
        /// <remarks>
        /// Ordinary entries leave their plain tooltip untouched while the pooled slot is reused.
        /// The game's own visible-label path is ItemData + ItemSkin through GetSkinnedDisplayName;
        /// affordability is otherwise represented only by colour (game-api.md sections 25 and 37).
        /// InitSurface does not populate furniture fields, so the native type takes precedence.
        /// Other build tools return null and retain the existing type-specific/fallback paths.
        /// </remarks>
        private static string GetBuildModeItemLabel(UIBuildModeItem item)
        {
            try
            {
                var surface = CatalogEntryReader.SurfaceOf(item);
                if (surface != null) return Loc.Get("catalog_surface", CatalogEntryReader.SurfaceName(surface.GUID));
                if (!CatalogEntryReader.IsOrdinaryItem(item)) return null;
                CatalogueItem data = item.ItemData;
                if (data == null)
                {
                    return null;
                }

                string name = ItemDescription.GetProductIdentity(data, item.ItemSkin);
                if (string.IsNullOrEmpty(name))
                {
                    return null;
                }

                string price = FormatService.GetMoney(item.Price);
                bool affordable = MoneyManager.Instance != null
                    && MoneyManager.Instance.CanPurchase(item.Price);

                return affordable
                    ? Loc.Get("catalog_item_affordable", name, price)
                    : Loc.Get("catalog_item_unaffordable", name, price);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"Reading UIBuildModeItem typed label threw: {ex}");
                return null;
            }
        }

        /// <summary>Reads one visible recipe/variant row without speaking TMP rich-text tags.</summary>
        private static string GetSkinChoiceLabel(UIGenericSkinChoice item)
        {
            try
            {
                if (item.KnownSkin == null || !item.KnownSkin.activeInHierarchy)
                {
                    return Loc.Get("skin_choice_unknown");
                }

                string name = ReadCleanText(item.NameLabel?.Text);
                string level = item.LevelLabelPanel != null
                    && item.LevelLabelPanel.activeInHierarchy
                        ? ReadCleanText(item.LevelLabel?.Text)
                        : null;
                string label = string.IsNullOrWhiteSpace(name)
                    ? Loc.Get("skin_choice_unknown")
                    : name;
                if (!string.IsNullOrWhiteSpace(level))
                {
                    label = Loc.Get("label_with_detail", label, level);
                }

                if (!item.Interactable)
                {
                    string reason = ReadOpenerText(item.Tooltip, item);
                    if (!string.IsNullOrWhiteSpace(reason))
                    {
                        label = Loc.Get("skin_choice_unavailable", label, reason);
                    }
                }
                return label;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Reading a generic skin choice threw: " + ex);
                return null;
            }
        }

        /// <summary>Names a visual color swatch by position and its authored palette colors.</summary>
        private static string GetSwatchLabel(UISwatchItem item)
        {
            try
            {
                UISwatchList list = item.ParentUIList as UISwatchList;
                int index = 0;
                int total = 0;
                var colors = new List<string>();
                if (list?.CurrentItems != null)
                {
                    foreach (UIListItemBase sibling in list.CurrentItems)
                    {
                        var swatchSibling = sibling as UISwatchItem;
                        if (swatchSibling == null || !swatchSibling.gameObject.activeInHierarchy)
                        {
                            continue;
                        }
                        total++;
                        if (ReferenceEquals(swatchSibling, item))
                        {
                            index = total;
                        }
                    }
                }

                // UISwatchThumbnail.UpdateThumbnailColors does not necessarily render every
                // backing SwatchColor. It first applies SwatchGroup.ColorsVisibility, then stops
                // when the active thumbnail has no more Image slots. The previous label ignored
                // both rules, which made a one-chip skin tone announce several hidden shader
                // channels. Mirror the native presentation exactly; genuinely multi-colour chips
                // still retain every colour the player can see.
                SwatchGroup swatchGroup = null;
                if (item.Swatch != null && SwatchManager.SwatchSettings != null)
                {
                    swatchGroup = SwatchManager.SwatchSettings
                        .GetSwatchGroupAndIndexByGUID(item.Swatch.SwatchGroup).Item1;
                }
                int thumbnailSlots = GetVisibleSwatchThumbnailSlots(item);
                bool patternThumbnail = item.UISwatchThumbnailPrefab?.ThumbnailPattern != null
                    && item.UISwatchThumbnailPrefab.ThumbnailPattern.activeInHierarchy;
                int visibleColorCount = 0;
                if (item.Swatch?.SwatchColors != null)
                {
                    for (int colorIndex = 0;
                        colorIndex < item.Swatch.SwatchColors.Length;
                        colorIndex++)
                    {
                        ColorsVisibility flag = (ColorsVisibility)(1 << colorIndex);
                        // The native pattern branch uses its authored NbColors directly and
                        // returns before applying the ordinary thumbnail's group mask.
                        if (!patternThumbnail && swatchGroup != null
                            && !swatchGroup.ColorsVisibility.HasFlag(flag))
                        {
                            continue;
                        }
                        if (thumbnailSlots > 0 && visibleColorCount >= thumbnailSlots)
                        {
                            break;
                        }

                        visibleColorCount++;
                        SwatchColor swatchColor = item.Swatch.SwatchColors[colorIndex];
                        if (swatchColor == null)
                        {
                            continue;
                        }

                        string colorName = null;
                        if (swatchColor.PaletteColor != 0UL)
                        {
                            PaletteColor palette = SwatchManager.SwatchSettings?
                                .GetPaletteColorByGUID(swatchColor.PaletteColor);
                            if (palette != null)
                            {
                                colorName = ResolveTranslation(palette.TranslationKey)
                                    ?? palette.DisplayName;
                            }
                        }
                        if (string.IsNullOrWhiteSpace(colorName))
                        {
                            Color visibleColor = swatchColor.GetColor();
                            colorName = swatchGroup != null
                                && string.Equals(
                                    swatchGroup.DisplayName,
                                    "Skin",
                                    StringComparison.OrdinalIgnoreCase)
                                ? DescribeSkinTone(visibleColor)
                                : DescribeApproximateColor(visibleColor);
                        }
                        AddUnique(colors, colorName);
                    }
                }

                string label = index > 0 && total > 0
                    ? (colors.Count > 0
                        ? Loc.Get(
                            "swatch_position_colors", index, total,
                            string.Join(Loc.Get("list_separator"), colors.ToArray()))
                        : Loc.Get("swatch_position", index, total))
                    : (colors.Count > 0
                        ? Loc.Get(
                            "swatch_colors",
                            string.Join(Loc.Get("list_separator"), colors.ToArray()))
                        : Loc.Get("swatch_generic"));

                return item.IsDefault
                    ? Loc.Get("swatch_default", label)
                    : label;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Reading a color swatch threw: " + ex);
                return null;
            }
        }

        /// <summary>
        /// Counts the colour regions the native swatch thumbnail currently displays.
        /// </summary>
        /// <remarks>
        /// UISwatchThumbnail selects one public thumbnail GameObject, then calls
        /// GetComponentsInChildren&lt;Image&gt; on that exact object and only assigns colours while an
        /// Image slot remains. Reading the same active object avoids guessing from the enum: Auto
        /// can change shape at runtime, and Hair has its own prefab layout.
        /// </remarks>
        private static int GetVisibleSwatchThumbnailSlots(UISwatchItem item)
        {
            UISwatchThumbnail thumbnail = item?.UISwatchThumbnailPrefab;
            if (thumbnail == null)
            {
                return 0;
            }

            if (thumbnail.ThumbnailPattern != null
                && thumbnail.ThumbnailPattern.activeInHierarchy
                && item.Swatch != null)
            {
                PatternItem pattern = Settings.Get<Patterns>()?
                    .GetPatternByGUID(item.Swatch.Pattern);
                return pattern != null ? pattern.NbColors : 0;
            }

            GameObject[] candidates =
            {
                thumbnail.ThumbnailHair,
                thumbnail.ThumbnailOneColor,
                thumbnail.ThumbnailTwoColors,
                thumbnail.ThumbnailThreeColors,
                thumbnail.ThumbnailFourColors
            };
            foreach (GameObject candidate in candidates)
            {
                if (candidate != null && candidate.activeInHierarchy)
                {
                    return candidate
                        .GetComponentsInChildren<UnityEngine.UI.Image>(includeInactive: false)
                        .Length;
                }
            }
            return 0;
        }

        private static string DescribeApproximateColor(Color color)
        {
            if (color.a < 0.05f)
            {
                return Loc.Get("color_transparent");
            }

            Color.RGBToHSV(color, out float hue, out float saturation, out float value);
            if (value < 0.12f)
            {
                return Loc.Get("color_black");
            }
            if (saturation < 0.12f)
            {
                if (value < 0.35f) return Loc.Get("color_dark_gray");
                if (value < 0.7f) return Loc.Get("color_gray");
                if (value < 0.92f) return Loc.Get("color_light_gray");
                return Loc.Get("color_white");
            }

            float degrees = hue * 360f;
            string basic;
            if ((degrees >= 15f && degrees < 48f) && value < 0.68f)
            {
                basic = Loc.Get("color_brown");
            }
            else if (degrees < 15f || degrees >= 345f) basic = Loc.Get("color_red");
            else if (degrees < 48f) basic = Loc.Get("color_orange");
            else if (degrees < 70f) basic = Loc.Get("color_yellow");
            else if (degrees < 165f) basic = Loc.Get("color_green");
            else if (degrees < 195f) basic = Loc.Get("color_cyan");
            else if (degrees < 255f) basic = Loc.Get("color_blue");
            else if (degrees < 290f) basic = Loc.Get("color_purple");
            else basic = Loc.Get("color_pink");

            if (value < 0.38f)
            {
                return Loc.Get("color_dark", basic);
            }
            if (value > 0.82f && saturation < 0.65f)
            {
                return Loc.Get("color_light", basic);
            }
            return basic;
        }

        /// <summary>
        /// Describes an authored skin chip by perceptual depth and visible undertone.
        /// </summary>
        /// <remarks>
        /// The installed Skin group uses direct RGB values rather than PaletteColor names. Broad
        /// HSV names collapsed three visibly different chips to "light orange" in TEST 72. CIE L*
        /// supplies stable perceptual depth bands, while the same visible RGB supplies a compact
        /// undertone. This is used only for the source-identified Skin swatch group; object, hair
        /// and clothing colours retain the generic colour vocabulary.
        /// </remarks>
        private static string DescribeSkinTone(Color color)
        {
            if (color.a < 0.05f)
            {
                return Loc.Get("color_transparent");
            }

            float lightness = GetCieLightness(color);
            string depth;
            if (lightness >= 83f) depth = Loc.Get("skin_depth_pale");
            else if (lightness >= 78f) depth = Loc.Get("skin_depth_very_light");
            else if (lightness >= 72f) depth = Loc.Get("skin_depth_light");
            else if (lightness >= 56f) depth = Loc.Get("skin_depth_medium_light");
            else if (lightness >= 44f) depth = Loc.Get("skin_depth_medium");
            else if (lightness >= 36f) depth = Loc.Get("skin_depth_medium_dark");
            else if (lightness >= 25f) depth = Loc.Get("skin_depth_dark");
            else if (lightness >= 14f) depth = Loc.Get("skin_depth_deep");
            else if (lightness >= 9f) depth = Loc.Get("skin_depth_very_deep");
            else depth = Loc.Get("skin_depth_deepest");

            Color.RGBToHSV(color, out float hue, out float saturation, out _);
            float degrees = hue * 360f;
            string undertone;
            if (saturation < 0.10f)
            {
                undertone = Loc.Get("skin_undertone_neutral");
            }
            else if (degrees < 8f || degrees >= 352f)
            {
                undertone = Loc.Get("skin_undertone_rosy");
            }
            else if (degrees < 17f)
            {
                undertone = Loc.Get("skin_undertone_reddish");
            }
            else if (degrees < 24f)
            {
                undertone = Loc.Get("skin_undertone_peach");
            }
            else if (degrees < 48f)
            {
                undertone = Loc.Get("skin_undertone_golden");
            }
            else
            {
                undertone = DescribeApproximateColor(color);
            }

            if (saturation < 0.20f)
            {
                return Loc.Get("skin_tone_muted", depth, undertone);
            }
            if (saturation > 0.52f)
            {
                return Loc.Get("skin_tone_rich", depth, undertone);
            }
            return Loc.Get("skin_tone", depth, undertone);
        }

        private static float GetCieLightness(Color color)
        {
            float red = SrgbToLinear(Mathf.Clamp01(color.r));
            float green = SrgbToLinear(Mathf.Clamp01(color.g));
            float blue = SrgbToLinear(Mathf.Clamp01(color.b));
            float luminance = 0.2126f * red + 0.7152f * green + 0.0722f * blue;
            float transformed = luminance > 216f / 24389f
                ? Mathf.Pow(luminance, 1f / 3f)
                : ((24389f / 27f) * luminance + 16f) / 116f;
            return 116f * transformed - 16f;
        }

        private static float SrgbToLinear(float channel)
        {
            return channel <= 0.04045f
                ? channel / 12.92f
                : Mathf.Pow((channel + 0.055f) / 1.055f, 2.4f);
        }

        private static string GetSimpleTraitLabel(UIProfileTraitSimpleItem item)
        {
            PersonalityTrait chosen = item.IsInUpgradeUI && item.TemporaryChosenTrait != null
                ? item.TemporaryChosenTrait
                : item.PersonalityTrait;
            string category = NameTraitCategory(item.UIProfileTraitSimple?.PersonalityTraitCategory);
            string value = chosen == null
                ? Loc.Get("creator_pick_one")
                : ResolveTranslation("Trait_" + chosen.DisplayName) ?? chosen.DisplayName;

            if (string.IsNullOrWhiteSpace(category))
            {
                return value;
            }
            return Loc.Get("creator_trait_value", category, value);
        }

        private static string GetMixedTraitLabel(UIProfileTraitMixItem item)
        {
            PersonalityTrait trait = item.PersonalityTrait;
            if (trait == null)
            {
                return null;
            }

            string name = ResolveTranslation("Trait_" + trait.DisplayName)
                ?? trait.DisplayName;
            if (item.Toggle != null && item.Toggle.gameObject.activeInHierarchy)
            {
                UIProfile profile = item.UIProfileTraitMix?.UIProfile;
                bool selected = profile?.Character != null
                    && PersonalityManager.Instance.CharacterHasTrait(
                        profile.Character, trait.GUID);
                return Loc.Get(
                    "label_with_state", name,
                    Loc.Get(selected ? "state_selected" : "state_not_selected"));
            }

            string selectedValue = item.Dropdown != null && item.Dropdown.activeInHierarchy
                ? ReadActiveTranslatedText(item.DropdownLabelSelectedItem)
                : null;
            return string.IsNullOrWhiteSpace(selectedValue)
                ? name
                : Loc.Get("creator_trait_value", name, selectedValue);
        }

        private static string ReadCleanText(TMP_Text text)
        {
            if (text == null || !text.gameObject.activeInHierarchy)
            {
                return null;
            }

            return ScreenReader.NormalizeForOutput(text.text);
        }

        /// <summary>
        /// Describes a body/face slider as a value, e.g. "Height, 1.76 metres, 50 of 100".
        /// </summary>
        /// <remarks>
        /// Reported from play: *"things like height don't appear to respond to keyboard input yet."*
        /// Correct, and it covers far more than height — sliders are most of the body and face
        /// editor.
        ///
        /// §14 mapped the shape: `UIClassicSliderItem : UIListItemBase` is a focusable ROW holding a
        /// `Slider` that is **not** focusable, so the row is reachable and the value is not. The
        /// generic scrape would join the row's label to its input field and read something like
        /// "Height 50", with no indication of the range.
        ///
        /// Value is read from the character, not from the UI: `RefreshItemVisual` drives both the
        /// slider and the input field from
        /// `CharacterDeformationSliderManager.GetCharacterSliderValue` every LateUpdate, so the
        /// character's data is the source and the widgets are the copy. Read, never recompute.
        ///
        /// The 0..1 value is reported over 100 because that is the scale the game itself shows in the
        /// input field (`Mathf.Round(value * 100f)`).
        /// </remarks>
        /// <param name="item">The slider row to describe.</param>
        /// <returns>A value label, or null to fall through.</returns>
        private static string GetSliderLabel(UIClassicSliderItem item)
        {
            // External game API on pooled rows: a row can be mid-rebuild with no character attached.
            try
            {
                Setting.DeformationSlider slider = item.DeformationSlider;
                if (slider == null || item.AssetCharacter == null)
                {
                    return null;
                }

                string name = ResolveTranslation(slider.TranslationKey)
                           ?? FormatService.AddSpacesBetweenUpperCamelCaseWords(
                                  slider.DisplayName, onlyCapitalizeFirstLetter: true);

                if (string.IsNullOrEmpty(name))
                {
                    return null;
                }

                float value = CharacterDeformationSliderManager.Instance
                    .GetCharacterSliderValue(item.AssetCharacter, slider.GUID);

                if (IsHeightSlider(item))
                {
                    return GetHeightValueLabel(
                        item.AssetCharacter, name, Mathf.RoundToInt(value * 100f));
                }

                return Loc.Get("slider_value", name, Mathf.RoundToInt(value * 100f));
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"Reading a body slider threw: {ex}");
                return null;
            }
        }

        private static string GetMailboxStampLabel(Component source)
        {
            UIMailbox mailbox = source.GetComponentInParent<UIMailbox>();
            if (mailbox?.Stamp == null || mailbox.SelectedLetter == null
                || (source.transform != mailbox.Stamp && !source.transform.IsChildOf(mailbox.Stamp))) return null;
            if (mailbox.SelectedLetter.StampHasCollected) return Loc.Get("mailbox_stamp_collected");
            // OnClickStamp refuses stamps already in this household's collection. Reading this
            // state has no side effects (UpdateStamps can assign a stamp and must not be called).
            bool owned = HouseholdManager.Instance.CurrentHousehold.Data.Collectibles.Contains(mailbox.SelectedLetter.Stamp);
            return Loc.Get(owned ? "mailbox_stamp_owned" : "action_collect_stamp");
        }

        /// <summary>
        /// Formats the source-owned metric height together with the editable percentage.
        /// </summary>
        /// <remarks>
        /// Both the classic height row and the diagram's Height handle edit the same body slider.
        /// Keeping the formatter here prevents the two keyboard paths from drifting into different
        /// units or wording. When CharacterHeight is still being recomputed, the truthful fallback
        /// remains the game's 0..100 slider value.
        /// </remarks>
        internal static string GetHeightValueLabel(
            AssetCharacter character, string name, int percent)
        {
            float height = character?.Visual?.Data?.CharacterHeight ?? 0f;
            if (height > 0f && !float.IsNaN(height) && !float.IsInfinity(height))
            {
                string metric = ScreenReader.NormalizeForOutput(
                    MeasurementUnitService.FormatLength(height, "0.00"));
                if (!string.IsNullOrWhiteSpace(metric))
                {
                    return Loc.Get("slider_height_value", name, metric, percent);
                }
            }

            return Loc.Get("slider_value", name, percent);
        }

        /// <summary>
        /// Identifies the height slider through UIClassicSliderItem's exact BodySlider reference.
        /// </summary>
        /// <remarks>
        /// DeformationSlider.GUID and BodySlider.GUID belong to different setting tables, so
        /// comparing the public deformation GUID to HeightSliderGUID would be a plausible but
        /// incorrect shortcut. UIClassicSliderItem.OnStartDrag uses its private `_bodySlider` for
        /// this same identity check; reflection reads that source-owned association.
        /// </remarks>
        internal static bool IsHeightSlider(UIClassicSliderItem item)
        {
            if (item == null)
            {
                return false;
            }

            if (!_classicSliderReflectionResolved)
            {
                _classicSliderReflectionResolved = true;
                _classicSliderBodySliderField = AccessTools.Field(
                    typeof(UIClassicSliderItem), "_bodySlider");
                if (_classicSliderBodySliderField == null)
                {
                    Plugin.Log.LogError(
                        "Could not resolve UIClassicSliderItem._bodySlider. Height will retain "
                      + "its percentage label until the game field is remapped.");
                }
            }

            try
            {
                var bodySlider = _classicSliderBodySliderField?.GetValue(item) as BodySlider;
                CharacterCreatorCamera settings = Settings.Get<CharacterCreatorCamera>();
                return bodySlider != null && settings != null
                    && bodySlider.GUID == settings.HeightSliderGUID;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Reading the body slider behind a classic slider threw: " + ex);
                return false;
            }
        }

        /// <summary>
        /// Describes a body-diagram handle with its current value(s).
        /// </summary>
        /// <remarks>
        /// The tooltip already gave the NAME ("Chest Size") — `Init` sets
        /// `Tooltip.TranslationKeyOfTextToShow = bodySlider.TranslationKey` — which is why these
        /// announced sensibly while being completely un-editable. What was missing is the value, and
        /// on a two-dimensional handle there are two of them.
        /// </remarks>
        /// <param name="item">The handle to describe.</param>
        /// <returns>A value label, or null to fall through to the tooltip.</returns>
        private static string GetHandleLabel(UICharacterCreatorDiagramHandleItem item)
        {
            BodyDiagramHandle.Resolved handle = BodyDiagramHandle.Resolve(item);
            if (handle == null)
            {
                return null;
            }

            // External game API: reading a value can fail on a character mid-rebuild.
            try
            {
                int primary = BodyDiagramHandle.ReadPercent(handle, handle.Primary);

                if (handle.Secondary == null)
                {
                    return Loc.Get("handle_one_axis", handle.Name, primary);
                }

                int secondary = BodyDiagramHandle.ReadPercent(handle, handle.Secondary);
                return Loc.Get(
                    "handle_two_axis",
                    handle.Name, handle.Primary.Name, primary, handle.Secondary.Name, secondary);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"Reading a body diagram handle value threw: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Names a personality randomize button by the section it randomizes.
        /// </summary>
        /// <remarks>
        /// TEST 34 measured these announcing **"Label"** — a prefab placeholder caption, the same
        /// shape as the `"x"` on category buttons (§25). It is a real word, so v0.13's glyph rule
        /// correctly declines to treat it as a placeholder and the tooltip is never consulted.
        ///
        /// Rather than widen that heuristic or match on the GameObject name (lesson 15), this runs in
        /// `GetTypedLabel` — **before** the text scrape — and is evidence-based in two independent
        /// steps:
        ///  1. **Is it a randomize button?** Read `ParaButton.ButtonClick`'s persistent listeners
        ///     (§29) and require a `UIProfile.RandomizePersonality*` target. Nothing else is named.
        ///  2. **Which section?** Read the owning panel's category. This is where the wiring alone is
        ///     not enough: three of the four methods take the panel as an ARGUMENT, and
        ///     `UnityEventBase` exposes no getter for arguments (§29's structural limit). Walking up
        ///     to the panel sidesteps that entirely and is identity-based.
        ///
        /// Note the inconsistent game field names, verified rather than assumed:
        /// `UIProfileTraitStats.PersonalityTraitCategory` and
        /// `UIProfileTraitSimple.PersonalityTraitCategory`, but `UIProfileTraitMix.PersonalityCategory`.
        /// </remarks>
        /// <param name="source">The element being described.</param>
        /// <returns>A label, or null when this is not a personality randomize button.</returns>
        private static string GetPersonalityRandomizeLabel(Component source)
        {
            // External game API plus Unity serialization: must degrade, never throw.
            try
            {
                var button = source.GetComponent<ParaButton>();
                if (button == null || button.ButtonClick == null)
                {
                    return null;
                }

                bool isRandomize = false;
                int count = button.ButtonClick.GetPersistentEventCount();

                for (int i = 0; i < count && !isRandomize; i++)
                {
                    string method = button.ButtonClick.GetPersistentMethodName(i);
                    isRandomize = button.ButtonClick.GetPersistentTarget(i) is UIProfile
                               && !string.IsNullOrEmpty(method)
                               && method.StartsWith("RandomizePersonality", System.StringComparison.Ordinal);
                }

                if (!isRandomize)
                {
                    return null;
                }

                string category = ResolveRandomizeCategory(source);

                return string.IsNullOrEmpty(category)
                    ? Loc.Get("randomize_personality")
                    : Loc.Get("randomize_section", category);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"Reading a personality randomize button threw: {ex}");
                return null;
            }
        }

        /// <summary>Names the trait category of whichever personality panel owns this button.</summary>
        private static string ResolveRandomizeCategory(Component source)
        {
            var stats = source.GetComponentInParent<UIProfileTraitStats>();
            if (stats != null)
            {
                return NameTraitCategory(stats.PersonalityTraitCategory);
            }

            var simple = source.GetComponentInParent<UIProfileTraitSimple>();
            if (simple != null)
            {
                return NameTraitCategory(simple.PersonalityTraitCategory);
            }

            var mix = source.GetComponentInParent<UIProfileTraitMix>();
            return mix != null ? NameTraitCategory(mix.PersonalityCategory) : null;
        }

        /// <summary>
        /// Resolves a trait category to its localized name.
        /// </summary>
        /// <remarks>
        /// The key is the game's own: `CheckHouseholdValidity` builds
        /// `TranslationManager.Get("TraitCategory_" + category.DisplayName)` when it names a missing
        /// category in an error, so the mod says the same words the game's own message would.
        /// </remarks>
        private static string NameTraitCategory(Setting.PersonalityTraitCategory category)
        {
            if (category == null || string.IsNullOrEmpty(category.DisplayName))
            {
                return null;
            }

            return ResolveTranslation("TraitCategory_" + category.DisplayName) ?? category.DisplayName;
        }

        /// <summary>
        /// Joins a creator trait's visible name to the exact description assigned to its tooltip.
        /// </summary>
        /// <remarks>
        /// `UIProfilePickPersonalityTraitItem.Refresh` assigns
        /// `TraitDescription_&lt;DisplayName&gt;` to its Tooltip, while
        /// `UIProfileTraitSimpleEvolutionItem.Refresh` assigns the trait's
        /// `DescriptionTranslationKey` and `DescriptionParameters`. The generic label path stops at
        /// the visible name, so keyboard focus never reveals the same effect text a pointer hover
        /// does. Read the configured opener through the shared tooltip translator instead of
        /// exposing hidden setting data.
        ///
        /// Advanced and read-only trait displays intentionally fall through: their source provides
        /// no description tooltip, so adding one there would disclose information the visual UI
        /// does not present.
        /// </remarks>
        private static string GetVisibleTraitDescriptionLabel(Component source)
        {
            try
            {
                TranslatedText label = null;
                TooltipOpenerSimple tooltip = null;

                UIProfilePickPersonalityTraitItem picker =
                    source.GetComponentInParent<UIProfilePickPersonalityTraitItem>();
                if (picker != null)
                {
                    label = picker.TraitLabel;
                    tooltip = picker.Tooltip;
                }
                else
                {
                    UIProfileTraitSimpleEvolutionItem evolution =
                        source.GetComponentInParent<UIProfileTraitSimpleEvolutionItem>();
                    if (evolution != null)
                    {
                        label = evolution.LabelTraitName;
                        tooltip = evolution.Tooltip;
                    }
                }

                if (tooltip == null)
                {
                    return null;
                }

                string name = ReadActiveTranslatedText(label);
                string description = ReadOpenerText(tooltip, source);
                if (string.IsNullOrEmpty(name))
                {
                    // The locked evolution slot has no visible name, but its tooltip explicitly
                    // contains the game's translated "Locked" message.
                    return description;
                }
                if (string.IsNullOrEmpty(description)
                    || string.Equals(name, description, StringComparison.Ordinal))
                {
                    return name;
                }

                return Loc.Get("trait_with_description", name, description);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Reading a visible personality trait tooltip threw: " + ex);
                return null;
            }
        }

        /// <summary>
        /// Describes a text field as label plus contents, e.g. "First Name, Blah" or "First Name, empty".
        /// </summary>
        /// <remarks>
        /// TEST 34 measured the same control announcing two different ways depending on how it was
        /// reached: `Last name, empty` through the mod's own panel, but `'Last Name, ​'` when Unity's
        /// EventSystem selected it — a trailing **zero-width space**, so the user heard "Last Name,"
        /// and then nothing.
        ///
        /// The cause is the generic scrape: `TMP_InputField` keeps its placeholder and its text as
        /// two separate child `TMP_Text`s, and the scrape includes inactive children, so it collected
        /// both. Reading the two fields by name instead makes every route to the control agree.
        ///
        /// `placeholder` is a `Graphic` on TMP_InputField, so it is cast rather than assumed.
        /// </remarks>
        /// <param name="field">The input field to describe.</param>
        /// <returns>A label, or null to fall through.</returns>
        private static string GetInputFieldLabel(TMP_InputField field)
        {
            // External game API: a prefab without a placeholder must degrade, not throw.
            try
            {
                var placeholder = field.placeholder as TMP_Text;
                string name = placeholder != null ? placeholder.text : null;

                if (!string.IsNullOrEmpty(name))
                {
                    name = name.Trim();
                }

                string value = field.text;
                bool empty = !IsSpeakable(value ?? string.Empty);

                if (string.IsNullOrEmpty(name))
                {
                    // No placeholder to name it with; the caller's other sources may do better.
                    return empty ? null : value.Trim();
                }

                return empty
                    ? Loc.Get("field_empty", name)
                    : Loc.Get("field_value", name, value.Trim());
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"Reading a text field threw: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Describes one personality stat as a single value, e.g. "Charisma, 2 of 3, 7 Points Left".
        /// </summary>
        /// <remarks>
        /// Measured in TEST 31: this row announced as "Physique, -, +" — the generic scrape joining
        /// the stat name to decorative child text — and the user reasonably read that as a label
        /// with increment and decrement buttons. **There are no such buttons.** The control is a
        /// vertical gauge of `UIProfileTraitStatsItemJauge` segments, one focusable per point, and
        /// the whole block costs SIX focusables per stat (row, 3 gauges, a limit bar, a warning
        /// icon) to express one number. Four stats therefore spend 24 focusables on four values.
        ///
        /// Identity comes through the child gauges, not this row: `_personalityTraitGUID` is private
        /// here, but every `UIProfileTraitStatsItemJauge` exposes public `PersonalityTrait` and
        /// `UIProfileTraitStats`, so no Reflection is needed.
        ///
        /// The maximum is COUNTED from the live gauges rather than read from the category.
        /// `UIProfileTraitStatsItem.Refresh` picks `MaxPointsPerStat` or `MaxPointsPerStatExtended`
        /// depending on the character's lifestage and whether another trait in the category is
        /// already at the cap; counting the segments it actually built reproduces that branch
        /// without duplicating it. The limit bar is excluded — it is a separate type.
        ///
        /// Points-left is READ from the game's own label and only while that label is active.
        /// `UIProfileTraitStats.Refresh` shows it solely when `IsFirstTimeInCharacterCreator`, so
        /// this says exactly what a sighted player is shown and stays quiet otherwise.
        /// </remarks>
        /// <param name="item">The stat row to describe.</param>
        /// <returns>A single-value label, or null to fall through.</returns>
        private static string GetStatLabel(UIProfileTraitStatsItem item)
        {
            // External game API on pooled items: a row can be mid-rebuild with no gauges yet.
            try
            {
                UIProfileTraitStatsItemJauge sample = null;
                int max = 0;

                UIList gauges = item.JaugeList;
                if (gauges == null || gauges.CurrentItems == null)
                {
                    return null;
                }

                foreach (UIListItemBase entry in gauges.CurrentItems)
                {
                    var gauge = entry as UIProfileTraitStatsItemJauge;
                    if (gauge == null)
                    {
                        continue;
                    }

                    if (sample == null)
                    {
                        sample = gauge;
                    }

                    max++;
                }

                if (sample == null || sample.PersonalityTrait == null || sample.UIProfileTraitStats == null)
                {
                    return null;
                }

                UIProfile profile = sample.UIProfileTraitStats.UIProfile;
                if (profile == null || profile.Character == null)
                {
                    return null;
                }

                string name = ResolveTranslation("Trait_" + sample.PersonalityTrait.DisplayName)
                           ?? sample.PersonalityTrait.DisplayName;

                int value = PersonalityManager.Instance.GetCharacterTraitPoints(
                    profile.Character, sample.PersonalityTrait.GUID);

                string pointsLeft = ReadPointsLeft(sample.UIProfileTraitStats);

                return string.IsNullOrEmpty(pointsLeft)
                    ? Loc.Get("stat_value", name, value, max)
                    : Loc.Get("stat_value_with_points", name, value, max, pointsLeft);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"Reading a personality stat threw: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Reads the category's "points left" label, but only while the game is showing it.
        /// </summary>
        private static string ReadPointsLeft(UIProfileTraitStats stats)
        {
            TranslatedText label = stats.LabelPointsLeft;
            if (label == null || label.Text == null || !label.gameObject.activeInHierarchy)
            {
                return null;
            }

            string text = label.Text.text;
            return string.IsNullOrEmpty(text) ? null : text.Trim();
        }

        /// <summary>
        /// Describes a layout container by what it holds, e.g. "Tattoo items, 11".
        /// </summary>
        /// <remarks>
        /// The count comes from the container's own UIList.CurrentItems, which pooling removes from
        /// (game-api.md section 15), so it is a live total rather than a pool size.
        ///
        /// Returns null on an empty list so the container falls through to the generic reader rather
        /// than announcing "0" - an empty grid should not be a landmark at all.
        /// </remarks>
        /// <param name="key">The Loc key describing what the container holds.</param>
        /// <param name="list">The container's item list.</param>
        /// <returns>A count label, or null to fall through.</returns>
        private static string CountLabel(string key, UIList list)
        {
            // External game API: a container can be mid-rebuild with no list attached.
            try
            {
                int count = list?.CurrentItems?.Count ?? 0;
                return count > 0 ? Loc.Get(key, count) : null;
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"Counting a contextual list container threw: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Names a tattoo/decal body-section button, e.g. "Torso".
        /// </summary>
        /// <remarks>
        /// Measured in TEST 29: all seven of these announced "unlabeled SectionButton(Clone)". They
        /// are the body-placement selectors for tattoos, so being unable to tell them apart makes
        /// the whole decal category unusable.
        ///
        /// They carry no text and no tooltip - the control is an icon of a body diagram
        /// (EquipmentDecalSections.CategoryIconGUID). But the item exposes its identity directly:
        /// UICharacterCreatorDecalPositions.Init assigns the public DecalGUID per item
        /// (UICharacterCreatorDecalPositions.cs:29), and Decals.GetDecalSectionByGUID resolves it.
        ///
        /// Speaking DisplayName is not inventing a name. The shipped values are already
        /// player-quality English - "Right Arm", "Left Arm", "Face", "Torso", "Right Leg",
        /// "Left Leg", "Back" - and they are the game's own words for exactly the distinction the
        /// icon draws. This is the same call already made for UIEquipmentItem, where the game itself
        /// displays Equipment.DisplayName.
        ///
        /// EquipmentDecalSections carries no TranslationKey and no [TranslatedText] attribute, so
        /// there is no localized form to prefer; non-English players get English here, exactly as
        /// they do for equipment names.
        ///
        /// Selection state needs nothing extra: SetToggles drives IsOn through SetIsOnNoCallback
        /// (:51-56), so section 16's rules already report it correctly.
        /// </remarks>
        /// <param name="item">The section button to describe.</param>
        /// <returns>The section name, or null to fall through.</returns>
        private static string GetDecalSectionLabel(UICharacterCreatorDecalPositionsListItem item)
        {
            // External game API: pooled items can be mid-rebuild with no GUID assigned.
            try
            {
                if (item.DecalGUID == 0UL)
                {
                    return null;
                }

                Setting.EquipmentDecalSections section =
                    Settings.Get<Setting.Decals>().GetDecalSectionByGUID(item.DecalGUID);

                return string.IsNullOrEmpty(section?.DisplayName) ? null : section.DisplayName.Trim();
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"Reading the decal section name threw: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Reads a category button's name from its own data. Used by the character creator's
        /// left-hand toolbars and by the build-mode catalog.
        /// </summary>
        /// <remarks>
        /// Measured: these announce as **"x"**. The prefab ships a leftover "x" caption on an
        /// icon-only button, and "x" is a letter, so the glyph rule in GetLabelOrFallback correctly
        /// does not treat it as a placeholder - a one-character label can be perfectly real, which is
        /// exactly what the neighbouring "M" / "F" gender items are. Widening that heuristic would
        /// have silenced legitimate labels, so this reads the data instead.
        ///
        /// UIGenericCategoriesButton.Init composes the key itself (game-api.md section 25):
        ///     ItemTag_&lt;DisplayName&gt;, or CharacterCreatorTag_&lt;DisplayName&gt; when IsCharacterCreatorTag
        /// and Data is public, so the same key is rebuilt here from the same fields.
        ///
        /// Reading Data also SIDESTEPS the two-opener hazard this class carries: it holds both a
        /// TooltipOpenerSimple and a TooltipOpenerWithVideo, only the enabled one is ever assigned,
        /// the other is never cleared, and the button is pooled - so the stale opener holds the key
        /// of whatever category last used that slot. Data is always current.
        /// </remarks>
        /// <param name="button">The category button to describe.</param>
        /// <returns>The localized category name, or null to fall through.</returns>
        private static string GetCategoryLabel(UIGenericCategoriesButton button)
        {
            // External game API: pooled buttons can be mid-rebuild with no data attached.
            try
            {
                UIGenericCategoriesData data = button.Data;
                if (data == null || string.IsNullOrEmpty(data.DisplayName))
                {
                    return null;
                }

                string prefix = data.IsCharacterCreatorTag ? "CharacterCreatorTag_" : "ItemTag_";
                return ResolveTranslation(prefix + data.DisplayName);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"Reading UIGenericCategoriesButton.Data threw: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Reads the label of an outfit-type tab in the Outfits panel.
        /// </summary>
        /// <remarks>
        /// TEST 21 measured every one of these announcing "..." . The label is not where the generic
        /// reader looks: UICharacterOutfitType.Init assigns it to a NAMED FIELD,
        ///     OutfitTypeLabelTooltip.TranslationKeyOfTextToShow = "OutfitType_" + outfitType.DisplayName;
        /// while the item's own child text is the nested ShowOptionsButton, whose caption really is
        /// the "…" overflow glyph. So the scrape was reading a genuine, useless label.
        ///
        /// The first entry in the list is special and MUST NOT be labelled from that field:
        /// UICharacterOutfits.RefreshUI calls Init(character, null, ...) for the "all outfits" tab
        /// (UICharacterOutfits.cs:99-100), and Init only assigns the tooltip key when outfitType is
        /// non-null. These are pooled UIList items (game-api.md section 15), so on that entry the
        /// field still holds the key of whatever outfit type last occupied the slot - a real,
        /// plausible, WRONG name, which is the one failure a blind user cannot detect.
        ///
        /// The game gives that entry no text of any kind: CharacterOutfits exposes only
        /// AllOutfitsIcon, with no matching name or translation key. A sighted player gets an icon
        /// and nothing else, so per the Playability principle no name is invented here. It falls
        /// through to "unlabeled ..." instead - audibly wrong, and therefore reportable.
        ///
        /// _outfitType is private, so Reflection is the only way to ask the authoritative question
        /// "does this entry have an outfit type at all". Positional tests (index 0 of the list) were
        /// rejected: they encode a construction order rather than the state being asked about.
        /// </remarks>
        /// <param name="item">The outfit-type tab to describe.</param>
        /// <returns>The localized outfit type name, or null to fall through.</returns>
        private static string GetOutfitTypeLabel(UICharacterOutfitType item)
        {
            // External game API plus Reflection: a renamed field must degrade to "unlabeled", never throw.
            try
            {
                if (_outfitTypeField == null)
                {
                    _outfitTypeField = AccessTools.Field(typeof(UICharacterOutfitType), "_outfitType");
                    if (_outfitTypeField == null)
                    {
                        Plugin.Log.LogWarning(
                            "UICharacterOutfitType._outfitType not found; outfit tabs stay unlabeled "
                          + "rather than risk speaking a stale pooled name.");
                        return null;
                    }
                }

                // The "all outfits" tab. Its tooltip key is stale, and the game has no name for it.
                if (_outfitTypeField.GetValue(item) == null)
                {
                    return null;
                }

                TooltipOpenerSimple opener = item.OutfitTypeLabelTooltip;
                return opener == null ? null : ResolveTranslation(opener.TranslationKeyOfTextToShow);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"Reading UICharacterOutfitType label threw: {ex}");
                return null;
            }
        }

        private static FieldInfo _outfitTypeField;

        /// <summary>
        /// Describes one outfit inside an outfit type, e.g. "Casual 2 of 4".
        /// </summary>
        /// <remarks>
        /// These carry no text at all - the control is a rendered thumbnail of the character wearing
        /// the outfit. A sighted player tells them apart by looking at them and gets no name either,
        /// so no name is invented here. What IS discrete and checkable is the position in the list
        /// and which one is currently worn, which is the same shape as the slot vocabulary in
        /// game-api.md section 26.
        ///
        /// The type name and ordinal are the game's own vocabulary, not the mod's:
        /// UICharacterOutfitType.RefreshList composes exactly
        ///     TranslationManager.Get("OutfitType_" + DisplayName) + " " + ordinal
        /// and passes it to Init as tooltipText (UICharacterOutfitType.cs:71-72).
        ///
        /// But Init only STORES that string outside the character creator:
        ///     if (!UICharacterOutfits.InCharacterCreator) { Tooltip.TextToShow = tooltipText; }
        /// (UICharacterOutfitOptions.cs:24-27). These are pooled UIList items, so inside the creator
        /// the field holds the label of whatever outfit last occupied the slot - real, plausible and
        /// wrong. So the read mirrors the game's own write condition exactly: read the tooltip where
        /// the game assigns it, and rebuild from live data where it does not.
        ///
        /// Position comes from ParentUIList.CurrentItems rather than the item's own Index property.
        /// Both are correct after a refresh, but DeactivateAndPoolItemAtIndex removes from
        /// CurrentItems immediately (UIList.cs:142) while Index is only rewritten on the next Init,
        /// so RemoveOutfit leaves a window where Index is stale by one. CurrentItems is live by
        /// definition, and taking position and total from the same list keeps them consistent.
        /// </remarks>
        /// <param name="item">The outfit entry to describe.</param>
        /// <returns>A positional label, or null to fall through.</returns>
        private static string GetOutfitOptionLabel(UICharacterOutfitOptions item)
        {
            // External game API: pooled items can be mid-rebuild with no panel or list attached.
            try
            {
                UICharacterOutfits outfits = item.UICharacterOutfits;
                if (outfits == null)
                {
                    return null;
                }

                // Where the game assigns the tooltip itself, that string wins - it is the label a
                // sighted player gets on this exact control, already localised and already ordinal-
                // correct across mixed types in the "all outfits" list.
                if (!outfits.InCharacterCreator && item.Tooltip != null)
                {
                    string assigned = item.Tooltip.TextToShow;
                    if (!string.IsNullOrEmpty(assigned))
                    {
                        return assigned.Trim();
                    }
                }

                UIList list = item.ParentUIList;
                if (list == null)
                {
                    return null;
                }

                int position = list.CurrentItems.IndexOf(item);
                if (position < 0)
                {
                    return null;
                }

                int total = list.CurrentItems.Count;
                string typeName = GetOutfitTypeName(outfits.CurrentOutfitType);

                return string.IsNullOrEmpty(typeName)
                    ? Loc.Get("outfit_option_untyped", position + 1, total)
                    : Loc.Get("outfit_option", typeName, position + 1, total);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"Reading UICharacterOutfitOptions label threw: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Resolves an outfit type GUID to its localized name.
        /// </summary>
        /// <remarks>
        /// OutfitType exposes the translation key as a public property (TranslationKey =>
        /// "OutfitType_" + DisplayName), so the key is read from the game rather than rebuilt here.
        /// </remarks>
        /// <param name="outfitTypeGuid">The outfit type to name.</param>
        /// <returns>The localized name, or null when the type is unknown.</returns>
        private static string GetOutfitTypeName(ulong outfitTypeGuid)
        {
            if (outfitTypeGuid == 0UL)
            {
                return null;
            }

            Setting.OutfitType type = Settings.Get<Setting.CharacterOutfits>()
                .GetOutfitTypeByGUID(outfitTypeGuid);

            return type == null ? null : ResolveTranslation(type.TranslationKey);
        }

        /// <summary>
        /// Names the outfit type tab's "..." button by what it will do next.
        /// </summary>
        /// <remarks>
        /// Its visible caption really is "...", so the glyph rule in GetLabelOrFallback would look
        /// for a tooltip - and this control has none, which is why it announced "...".
        ///
        /// ToggleShowOptions flips UICharacterOutfits.ShowOptions, and RefreshUI applies it as
        /// OutfitContainer.gameObject.SetActive(ShowOptions) (UICharacterOutfits.cs:87-90), so the
        /// button shows or hides the list of outfits for the selected type. ShowOptions is a public
        /// property, so the label states the action the NEXT press performs rather than a static
        /// name - the same information the icon's state gives a sighted player.
        /// </remarks>
        /// <param name="item">The outfit type tab owning the button.</param>
        /// <returns>The action label, or null when the panel cannot be reached.</returns>
        private static string GetShowOptionsLabel(UICharacterOutfitType item)
        {
            // External game API: the tab's panel reference can be missing mid-rebuild.
            try
            {
                UICharacterOutfits outfits = item.UICharacterOutfits;
                if (outfits == null)
                {
                    return null;
                }

                return outfits.ShowOptions ? Loc.Get("outfit_hide") : Loc.Get("outfit_show");
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"Reading UICharacterOutfits.ShowOptions threw: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Reads the element's own tooltip text, resolved through the game's translations.
        /// </summary>
        /// <remarks>
        /// TooltipOpenerBase is declared [RequireComponent(typeof(Focusable))], so an opener always
        /// sits on the same GameObject as the Focusable - no searching required.
        ///
        /// An element can carry MORE THAN ONE opener and enable exactly one of them. Reading with
        /// GetComponent returns whichever sits first in component order, which is not necessarily
        /// the enabled one, and the disabled sibling is often stale:
        ///
        ///   UINeedsItem              - two openers, identical text. Harmless.
        ///   UISkillItem              - Tooltip.TextToShow is always assigned, SimpleTooltip.TextToShow
        ///                              only while the character is NOT levelling that skill.
        ///   UIGenericCategoriesButton - only the enabled opener is assigned a key and the other is
        ///                              never cleared. These are pooled UIList items, so the disabled
        ///                              one holds the key of the PREVIOUS category in that slot.
        ///
        /// The last case is the dangerous one: it speaks a real, plausible category name that is
        /// simply the wrong one, which a blind user has no way to detect. So enabled openers win.
        /// See docs/game-api.md sections 20, 22 and 25.
        /// </remarks>
        /// <param name="source">The component whose tooltip should be read.</param>
        /// <returns>The tooltip text, or null when the element has none this reader understands.</returns>
        private static string GetTooltipText(Component source)
        {
            TooltipOpenerBase[] openers = source.GetComponents<TooltipOpenerBase>();
            if (openers == null || openers.Length == 0)
            {
                return null;
            }

            foreach (TooltipOpenerBase opener in openers)
            {
                if (opener == null || !opener.enabled)
                {
                    continue;
                }

                string text = ReadOpenerText(opener, source);
                if (!string.IsNullOrEmpty(text))
                {
                    return text;
                }
            }

            // No enabled opener produced text. With a single opener there is no sibling to have
            // gone stale against, so the pre-fix behaviour is kept rather than dropping a label
            // that may be the element's only one. With several, staying silent is correct: the
            // fallback path says "unlabeled <name>", which is audibly wrong rather than quietly
            // wrong, and can therefore be reported.
            if (openers.Length == 1)
            {
                return ReadOpenerText(openers[0], source);
            }

            Plugin.Log.LogInfo(
                $"[Label] '{source.gameObject.name}' has {openers.Length} tooltip openers but none "
              + "enabled with usable text - staying silent rather than risking a stale sibling.");
            return null;
        }

        /// <summary>
        /// Reads the text out of one tooltip opener, resolved through the game's translations.
        /// </summary>
        /// <remarks>
        /// TooltipOpenerSimple exposes both TranslationKeyOfTextToShow and a literal TextToShow;
        /// OnShowTooltip prefers the key and falls back to the literal, so this mirrors that order.
        /// TooltipOpenerNumericBreakdown derives from Simple and is caught by the same branch.
        ///
        /// Other subclasses carry differently-shaped data (TooltipOpenerWithPage points at a page
        /// GUID, TooltipOpenerBuildModeCatalogItem carries typed CatalogueItem/Surface/Fence data).
        /// Those are logged rather than guessed at, so the remaining shapes can be added from
        /// evidence - see docs/game-api.md section 18 for the full map.
        /// </remarks>
        /// <param name="opener">The opener to read.</param>
        /// <param name="source">The owning component, for diagnostics.</param>
        /// <returns>The opener's text, or null when its shape is not handled.</returns>
        private static string ReadOpenerText(TooltipOpenerBase opener, Component source)
        {
            string key = null;
            string literal = null;
            string[] parameters = null;
            bool hasBaseParameters = false;
            float4 baseParameters = default(float4);

            if (opener is TooltipOpenerSimple simple)
            {
                key = simple.TranslationKeyOfTextToShow;
                literal = simple.TextToShow;
                ResolveTooltipReflection();
                parameters = _simpleTranslationParametersField?.GetValue(simple) as string[];
                hasBaseParameters = _simpleHasBaseParametersField != null
                    && (bool)_simpleHasBaseParametersField.GetValue(simple);
                if (hasBaseParameters && _simpleBaseParametersField != null)
                {
                    baseParameters = (float4)_simpleBaseParametersField.GetValue(simple);
                }
            }
            else if (opener is TooltipOpenerWithImage withImage)
            {
                key = withImage.TranslationKeyOfTextToShow;
                ResolveTooltipReflection();
                parameters = _imageTranslationParametersField?.GetValue(withImage) as string[];
            }
            else if (opener is TooltipOpenerWithVideo withVideo)
            {
                // Build-mode tools (Straight, Curved, Draw, Replace, SingleWall, WholeRoom) use this.
                key = withVideo.TranslationKeyOfTextToShow;
                ResolveTooltipReflection();
                parameters = _videoTranslationParametersField?.GetValue(withVideo) as string[];
            }
            else
            {
                Plugin.Log.LogInfo(
                    $"[Label] Unhandled tooltip opener '{opener.GetType().Name}' on "
                  + $"'{source.gameObject.name}' - element stays unlabeled. Add its text field once "
                  + "its shape is known.");
                return null;
            }

            string resolved = hasBaseParameters
                ? ResolveTranslation(key, baseParameters, parameters)
                : ResolveTranslation(key, parameters ?? new string[0]);
            if (!string.IsNullOrEmpty(key))
            {
                return resolved;
            }

            if (string.IsNullOrWhiteSpace(literal))
            {
                return null;
            }

            string trimmed = literal.Trim();
            return ContainsUnresolvedPlaceholder(trimmed) || trimmed == "99:99:99"
                ? null
                : trimmed;
        }

        private static void ResolveTooltipReflection()
        {
            if (_tooltipReflectionResolved)
            {
                return;
            }

            _tooltipReflectionResolved = true;
            _simpleTranslationParametersField =
                AccessTools.Field(typeof(TooltipOpenerSimple), "_translationParameters");
            _simpleBaseParametersField =
                AccessTools.Field(typeof(TooltipOpenerSimple), "_baseParameters");
            _simpleHasBaseParametersField =
                AccessTools.Field(typeof(TooltipOpenerSimple), "_hasBaseParameters");
            _imageTranslationParametersField =
                AccessTools.Field(typeof(TooltipOpenerWithImage), "_translationParameters");
            _videoTranslationParametersField =
                AccessTools.Field(typeof(TooltipOpenerWithVideo), "_translationParameters");

            if (_simpleTranslationParametersField == null
                || _simpleBaseParametersField == null
                || _simpleHasBaseParametersField == null
                || _imageTranslationParametersField == null
                || _videoTranslationParametersField == null)
            {
                Plugin.Log.LogError(
                    "Could not resolve one or more tooltip parameter fields. Parameterized "
                  + "tooltips will stay unlabeled rather than speak unresolved placeholders.");
            }
        }

        private static string ResolveTranslation(string key)
        {
            return ResolveTranslation(key, new string[0]);
        }

        private static string ResolveTranslation(string key, params string[] parameters)
        {
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }

            // External game API: a missing key or a changed signature must not break announcements.
            try
            {
                if (!TranslationManager.Has(key))
                {
                    return null;
                }

                string text = TranslationManager.Get(key, parameters);

                // A missing translation commonly echoes the key back; speaking a raw key is worse
                // than falling through to the next source.
                if (string.IsNullOrEmpty(text) || text == key
                    || ContainsUnresolvedPlaceholder(text))
                {
                    return null;
                }

                return text.Trim();
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"TranslationManager.Get('{key}') threw: {ex}");
                return null;
            }
        }

        private static string ResolveTranslation(
            string key, float4 baseParameters, params string[] extraParameters)
        {
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }

            try
            {
                if (!TranslationManager.Has(key))
                {
                    return null;
                }

                string text = TranslationManager.Get(key, baseParameters, extraParameters);
                if (string.IsNullOrEmpty(text) || text == key
                    || ContainsUnresolvedPlaceholder(text))
                {
                    return null;
                }

                return text.Trim();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"TranslationManager.Get('{key}', float4, ...) threw: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Maps a button's wired click target onto a spoken label, keyed "TypeName.MethodName".
        /// </summary>
        /// <remarks>
        /// Every entry here is a method whose BODY has been read, and the label states what that
        /// body actually does. Adding an entry from a method's name alone defeats the point of
        /// reading the wiring in the first place.
        ///
        ///   UICharacterCreator.ValidateAndExitCharacterCreator - validates the household, sets
        ///     WasSavedOnce, plays the confirm sound and fades out to LeaveCharacterCreator.
        ///   UICharacterCreator.Cancel - opens a yes/no prompt, then discards the household or
        ///     restores every character's previous data and leaves.
        ///   UICharacterOutfits.AddOutfit - adds an outfit of the current type and selects it.
        ///   UITrainHouseholdInfoPanel.Back - the whole body is StopStoryboard(); Hide();, so the
        ///     button closes the panel and does nothing else. Mapped from the wiring TEST 28's
        ///     diagnostic reported for ButtonClose.
        /// </remarks>
        private static readonly Dictionary<string, string> ActionLabels =
            new Dictionary<string, string>
            {
                { "UICharacterCreator.ValidateAndExitCharacterCreator", "action_confirm_and_exit" },
                { "UICharacterCreator.Cancel", "action_cancel_and_exit" },
                { "UICharacterOutfits.AddOutfit", "outfit_add" },
                { "UITrainHouseholdInfoPanel.Back", "action_close" },
                { "UITrainSelectStartingHousehold.OnButtonCreateNewHousehold", "action_create_household" },
                { "UITrainHouseholdInfoPanel.OnClickEditInParamaker", "action_edit_household" },
                { "UITrainHouseholdInfoPanel.OnClickPlayNow", "action_play_household" },
                { "UINeeds.AdvanceTutorial", "action_continue_tutorial" },
                { "UIWants2.ProgressTutorial", "action_continue_tutorial" },
                { "UICharacters.SelectAll", "action_select_all_household" },
                { "UICharacterItem.OnCameraFollowTargetCancelButtonClicked", "action_stop_camera_follow" },
                { "UICharactersTogetherBar.OpenTogetherChoices", "action_open_together_choices" },
                { "UICharactersTogetherBar.SelectAllCharactersInGroup", "action_select_together_group" },
                { "UIStorytellerSelection.OnFinishSelectStoryteller", "action_confirm_storyteller" },
                { "UIStorytellerSelection.CreateCustomStorytellerBasedOnThis", "action_customize_storyteller" },
                { "UIMailbox.OnClickStamp", "action_collect_stamp" },
                // The native invite picker gathers every selected row, invokes its invitation
                // callback, then closes. This names its otherwise icon-only validation control.
                { "UISelectCharacters.OnValidateClicked", "action_invite_selected_paras" },
                { "UIGoalItem.OnRequestAccepted", "action_accept_request" },
                // A job card is the application control; a qualified press always continues to
                // schedule selection. The pin action only moves the camera to that company's lot.
                { "UIOccupationOfferItem.OnClickOnItem", "action_apply_for_job" },
                { "UIOccupationOfferItem.OnClickPinCompany", "action_show_job_location" },
                { "UIOccupationOffers.Hide", "action_close" },
                // The same schedule window handles first-time enrollment and later schedule
                // changes. ContextualActionLabel distinguishes those modes from its live model.
                { "UIOccupationsChangeSchedule.OnClickChange", "action_confirm_work_schedule" },
                // Measured in TEST 36. This is the Name Details close button, and it only became
                // visible once v0.23.0 keyed the wiring throttle on name AND wiring - several
                // distinct objects are called ButtonClose, and the old name-only key reported
                // whichever was seen first and hid the rest. UICharacterName.Hide is UIContainer's
                // Hide(), i.e. IsVisible = false. This is the target v0.18.0 aimed at and missed.
                { "UICharacterName.Hide", "action_close" },
                // Both targets only delegate to UIContainer.Hide (directly or through the inherited
                // method), so their icon-only close controls have the same unambiguous action.
                { "UICharacterProfile.Hide", "action_close" },
                { "UIOfferedWants.Hide", "action_close" },
                { "UIGenericSkinChoices.Hide", "action_close" },
                { "UIInventory.Hide", "action_close_inventory" },
                { "UILotInfo.Hide", "action_close_lot_details" },
                { "UIOccupationsChangeSchedule.OnClickQuit", "action_close_work_schedule" },
                { "UIStorytellerSelection.Back", "action_back_storyteller_selection" },
                { "UIDeveloperTools.OnButtonChangeCurrentDeveloperTool", "action_choose_developer_tool" },
                // UIGoals.OnShow reads and displays the saved completed-goal filter; the toggle
                // method flips that exact value. Its close control directly hides this window.
                { "UIGoals.Hide", "action_close_goals" },
                { "UIGoals.ToggleShowLifeGoals", "action_show_completed_life_goals" },
                // Museum donations are a two-window flow. These controls commit the selected
                // eligible UIInventory rows or close both UIInventory and UIDonateToMuseum.
                { "UIDonateToMuseum.ClickedDonate", "action_donate_selected_items" },
                { "UIDonateToMuseum.CloseClicked", "action_close_museum_donations" },
                // These entries came from TEST 69's live prefab wiring, and each target method's
                // body has been read. ContextualActionLabel adds live state or owning-row context
                // where the static action alone would be ambiguous.
                { "UILoadGameItem.ToggleShowAutoSaves", "action_toggle_autosaves" },
                { "UIOptions.ResetAllSettingsInCurrentCategory", "settings_reset_category" },
                { "UIOptions.Hide", "action_close" },
                { "UIPremadeHouseholdSelection.Hide", "action_close" },
                { "UIPremadeHouseholdInfo.Hide", "action_close" },
                { "UITownMapCharacterIcon.Clicked", "action_center_camera_on_para" },
                { "UIInteractionsList.DeleteItem", "action_sell_item" },
                { "UIBuildModeModes.CloseClicked", "action_return_to_build_mode" },
                { "UIBuildModeCatalog.OnIncrementHalfWallHeight", "action_adjust_half_wall_height" },
                { "UIGoalItemLine.OnClickHelp", "action_goal_help" },
                { "UIGoalItemLine.OnClickShowOnMap", "action_goal_show_on_map" },
                { "UINewHousehold.ClickOnMoreHouseholdOptions", "action_more_household_options" },
                { "UICharacters.CloseTogetherResults", "action_close_conversation_choices" },
                // Activates and selects the root interaction list's TMP input field.
                { "UIInteractions.ShowSearchField", "interaction_search" },
            };

        private static readonly HashSet<string> _reportedWiring = new HashSet<string>();

        /// <summary>
        /// Names an icon-only button from the click action the prefab wired to it.
        /// </summary>
        /// <remarks>
        /// Measured in TEST 27: ButtonCancel and ButtonValidate - the two controls that confirm or
        /// discard the whole character - have no child text AND no tooltip opener at all (the log
        /// carries zero "[Label] ... openers" lines), so every existing label source is exhausted
        /// and they announce "unlabeled ButtonValidate".
        ///
        /// Their behaviour is NOT readable from decompiled source: UICharacterCreator has no field
        /// referencing either object, and neither Cancel() nor ValidateAndExitCharacterCreator() is
        /// called anywhere in code. Both are public methods on a UI class that nothing invokes,
        /// which in this codebase means a UnityEvent wired in the prefab - and prefab wiring is
        /// invisible to decompilation (lesson 10).
        ///
        /// Naming them from their GameObject names would be exactly the plausible-but-wrong guess a
        /// blind user cannot detect, on the highest-stakes pair of buttons in the mod. So the wiring
        /// is READ instead. ParaButton.ButtonClick is a public UnityEvent, and UnityEventBase
        /// exposes GetPersistentEventCount / GetPersistentTarget / GetPersistentMethodName - all
        /// three verified present on this game's UnityEngine.CoreModule.dll before this was written.
        /// That turns a serialized value into a readable one.
        ///
        /// Safety properties, in order of importance:
        ///  - A button is named ONLY when its wiring matches an entry that was added deliberately.
        ///    An unknown action returns null and the caller says "unlabeled &lt;name&gt;" exactly as
        ///    before, so this can add labels but can never change one to something wrong.
        ///  - Several listeners resolving to DIFFERENT labels is ambiguous, and stays silent.
        ///  - Unrecognised wiring is logged once per object name, so a single run maps the rest of
        ///    the unlabeled icon buttons in the game (ButtonClose, ButtonMoreOptions,
        ///    ButtonTrackUntrackGoal ...) without another round of guessing.
        ///
        /// This reads PERSISTENT listeners only. Listeners added at runtime via AddListener are
        /// invisible here, which is correct: those come from code that decompilation can already
        /// show, and it is the prefab-wired ones that are otherwise unknowable.
        /// </remarks>
        /// <param name="source">The element being described.</param>
        /// <returns>A label for the wired action, or null when it is unknown or ambiguous.</returns>
        private static string GetActionLabel(Component source)
        {
            // External game API plus Unity serialization: a changed event shape must degrade to
            // "unlabeled", never throw inside a focus announcement.
            try
            {
                var button = source.GetComponent<ParaButton>();
                if (button == null || button.ButtonClick == null)
                {
                    return null;
                }

                int count = button.ButtonClick.GetPersistentEventCount();
                if (count == 0)
                {
                    return null;
                }

                var wiring = new List<string>();
                var matches = new HashSet<string>();

                for (int i = 0; i < count; i++)
                {
                    UnityEngine.Object target = button.ButtonClick.GetPersistentTarget(i);
                    string method = button.ButtonClick.GetPersistentMethodName(i);
                    if (target == null || string.IsNullOrEmpty(method))
                    {
                        continue;
                    }

                    wiring.Add(target.GetType().Name + "." + method);

                    string key = LookupActionKey(target.GetType(), method);
                    if (key != null)
                    {
                        matches.Add(key);
                    }
                }

                if (matches.Count == 1)
                {
                    foreach (string key in matches)
                    {
                        return GetContextualActionLabel(source, key) ?? Loc.Get(key);
                    }
                }

                ReportWiring(source, wiring, matches.Count);
                return null;
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"Reading ParaButton.ButtonClick wiring threw: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Adds state or owning-row context to a source-proven wired action.
        /// </summary>
        /// <remarks>
        /// This is called only after the ordinary text and tooltip paths were empty and after the
        /// button's persistent wiring resolved to exactly one known semantic action. It therefore
        /// cannot override game-authored copy, and a changed prefab falls back to the existing
        /// reportable "unlabeled" behaviour rather than being guessed from an object name.
        /// </remarks>
        private static string GetContextualActionLabel(Component source, string key)
        {
            if (key == "action_toggle_autosaves")
            {
                UILoadGameItem row = source.GetComponentInParent<UILoadGameItem>();
                if (row?.AutoSaveContainer != null)
                {
                    return Loc.Get(row.AutoSaveContainer.activeSelf
                        ? "action_hide_autosaves"
                        : "action_show_autosaves");
                }
            }

            if (key == "action_center_camera_on_para")
            {
                UITownMapCharacterIcon icon =
                    source.GetComponentInParent<UITownMapCharacterIcon>();
                string name = GetTownMapCharacterName(icon);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return Loc.Get("action_center_camera_on_named_para", name.Trim());
                }
            }

            if (key == "action_apply_for_job")
            {
                UIOccupationOfferItem offer =
                    source.GetComponentInParent<UIOccupationOfferItem>();
                string label = GetOccupationOfferLabel(offer);
                if (!string.IsNullOrWhiteSpace(label))
                {
                    return label;
                }
            }

            if (key == "action_show_job_location")
            {
                UIOccupationOfferItem offer =
                    source.GetComponentInParent<UIOccupationOfferItem>();
                string company = offer == null
                    ? null
                    : ReadActiveTranslatedText(offer.LabelCompany);
                if (!string.IsNullOrWhiteSpace(company))
                {
                    return Loc.Get("action_show_named_job_location", company.Trim());
                }
            }

            if (key == "action_confirm_work_schedule")
            {
                UIOccupationsChangeSchedule schedule =
                    source.GetComponentInParent<UIOccupationsChangeSchedule>();
                if (schedule?.LabelButtonValidate != null)
                {
                    string authored = ReadActiveTranslatedText(
                        schedule.LabelButtonValidate);
                    if (!string.IsNullOrWhiteSpace(authored))
                    {
                        return authored;
                    }
                }
            }

            if (key == "action_goal_help" || key == "action_goal_show_on_map")
            {
                UIGoalItemLine line = source.GetComponentInParent<UIGoalItemLine>();
                string objective = line == null
                    ? null
                    : ReadActiveTranslatedText(line.LineLabel);
                if (!string.IsNullOrWhiteSpace(objective))
                {
                    return Loc.Get(
                        key == "action_goal_help"
                            ? "action_goal_help_for"
                            : "action_goal_show_on_map_for",
                        objective.Trim());
                }
            }

            if (key == "action_close_conversation_choices")
            {
                UICharacters characters = source.GetComponentInParent<UICharacters>();
                if (characters?.IsShowingOutcomes == true)
                {
                    return Loc.Get("action_close_conversation_results");
                }
            }

            if (key == "action_adjust_half_wall_height")
            {
                // The body takes a signed increment. The prefab exposes the direction only through
                // its visual arrow and matching object name, so require BOTH that exact verified
                // wiring (the key above) and the exact directional name before disambiguating.
                // Neither the name nor the method alone is allowed to label another control.
                if (string.Equals(source.gameObject.name, "ButtonUp", StringComparison.Ordinal))
                {
                    return Loc.Get("action_increase_half_wall_height");
                }
                if (string.Equals(source.gameObject.name, "ButtonDown", StringComparison.Ordinal))
                {
                    return Loc.Get("action_decrease_half_wall_height");
                }
            }

            return null;
        }

        private static string GetSmallCharacterThumbnailLabel(
            SmallCharacterThumbnailListItem item)
        {
            try
            {
                if (!_characterThumbnailReflectionResolved)
                {
                    _characterThumbnailReflectionResolved = true;
                    _characterThumbnailCharacterField = AccessTools.Field(
                        typeof(CharacterThumbnail), "_character");
                    if (_characterThumbnailCharacterField == null)
                    {
                        Plugin.Log.LogError(
                            "Could not resolve CharacterThumbnail._character. Small Para "
                          + "portraits will fall back to their tooltip labels.");
                    }
                }

                AssetCharacter character = _characterThumbnailCharacterField?
                    .GetValue(item.Thumbnail) as AssetCharacter;
                string name = character?.Data?.FullName;
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = item.Tooltip?.TextToShow;
                }
                return string.IsNullOrWhiteSpace(name)
                    ? null
                    : Loc.Get("character_thumbnail", name.Trim());
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Reading a small character thumbnail threw: " + ex);
                return null;
            }
        }

        private static string GetTownMapCharacterName(UITownMapCharacterIcon icon)
        {
            if (icon == null)
            {
                return null;
            }

            ResolveTownMapCharacterReflection();
            if (_townMapCharacterField != null)
            {
                try
                {
                    AssetCharacter character = _townMapCharacterField.GetValue(icon) as AssetCharacter;
                    string fullName = character?.Data?.FullName;
                    if (!string.IsNullOrWhiteSpace(fullName))
                    {
                        return fullName;
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError(
                        "Reading UITownMapCharacterIcon._character threw: " + ex);
                }
            }

            // Update writes the same ordinary player-facing name into this tooltip. It is a safe
            // settling fallback if reflection becomes unavailable in a later game build.
            string tooltip = icon.Tooltip?.TextToShow;
            return string.IsNullOrWhiteSpace(tooltip) ? null : tooltip;
        }

        private static void ResolveTownMapCharacterReflection()
        {
            if (_townMapCharacterReflectionResolved)
            {
                return;
            }

            _townMapCharacterReflectionResolved = true;
            _townMapCharacterField = AccessTools.Field(
                typeof(UITownMapCharacterIcon), "_character");
            if (_townMapCharacterField == null)
            {
                Plugin.Log.LogError(
                    "Could not resolve UITownMapCharacterIcon._character. Town-map character "
                  + "buttons will retain their current tooltip name when available.");
            }
        }

        /// <summary>
        /// Looks up a wired method, walking up the target's base types.
        /// </summary>
        /// <remarks>
        /// A persistent target's runtime type can be a subclass of the type that declares the
        /// wired method, so an exact type-name match alone would silently miss it. The walk stops
        /// at MonoBehaviour, below which nothing game-specific can be declared.
        /// </remarks>
        /// <param name="type">The runtime type of the wired target.</param>
        /// <param name="method">The wired method name.</param>
        /// <returns>The Loc key for this action, or null when it is not mapped.</returns>
        private static string LookupActionKey(System.Type type, string method)
        {
            for (System.Type t = type; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
            {
                string key;
                if (ActionLabels.TryGetValue(t.Name + "." + method, out key))
                {
                    return key;
                }
            }

            return null;
        }

        /// <summary>
        /// Logs the wiring of a button this reader could not name, once per object name.
        /// </summary>
        /// <remarks>
        /// Throttled because this runs on every focus gain of an unlabeled button; unthrottled it
        /// would flood the log while the user arrows around a toolbar.
        ///
        /// KEYED ON NAME **AND** WIRING, which is a fix. Throttling by name alone assumed object
        /// names were unique, and they are not: TEST 28 reported
        /// `ButtonClose -> UITrainHouseholdInfoPanel.Back` while TEST 29 reported
        /// `ButtonClose -> UIBuildModeModes.CloseClicked`. Both were true - the game has several
        /// distinct objects called ButtonClose - but the name-keyed set logged whichever was seen
        /// first and silently hid every other one. That sent a label to the wrong control: the
        /// mapping shipped in v0.18.0 was correct for the panel it named and did nothing for the
        /// ButtonClose the user actually meets, on UICharacterName.
        ///
        /// The set stays small and bounded: one entry per distinct (control, behaviour) pair.
        /// </remarks>
        /// <param name="source">The element being described.</param>
        /// <param name="wiring">The "Type.Method" entries found on the button.</param>
        /// <param name="matchCount">How many distinct known actions matched.</param>
        private static void ReportWiring(Component source, List<string> wiring, int matchCount)
        {
            if (wiring.Count == 0)
            {
                return;
            }

            string name = source.gameObject.name;
            if (!_reportedWiring.Add(name + " -> " + string.Join(", ", wiring.ToArray())))
            {
                return;
            }

            string joined = string.Join(", ", wiring.ToArray());

            if (matchCount > 1)
            {
                Plugin.Log.LogInfo(
                    $"[Label] '{name}' is wired to several known actions ({joined}) - staying "
                  + "silent rather than picking one.");
                return;
            }

            Plugin.Log.LogInfo(
                $"[Label] '{name}' has no text and no tooltip. Its ButtonClick is wired to "
              + $"{joined}. Read that method, then add it to ActionLabels to give it a name.");
        }

        /// <summary>
        /// Builds a label, falling back to the GameObject name when no text is present.
        /// </summary>
        /// <remarks>
        /// The GameObject name is a developer-facing identifier, not user-facing copy. It is used
        /// only so that an unlabeled control is still reachable and reportable during testing
        /// rather than being silent — silence would be indistinguishable from a broken mod.
        /// Unlabeled elements found this way should be logged and given real labels over time.
        /// </remarks>
        /// <param name="source">The component whose element should be described.</param>
        /// <returns>Text suitable for speaking; never null for a non-null source.</returns>
        public static string GetLabelOrFallback(Component source)
        {
            if (source == null)
            {
                return null;
            }

            string label = GetLabel(source);

            // A caption with no letters and no digits is a glyph, not a name. The game uses "..."
            // as visible copy on real controls and gives them a proper tooltip alongside it:
            //   UICharacterCreatorGenderItem.Refresh, when gender == null, is the advanced-
            //   personality button and sets BOTH Label.text = "..." and
            //   Tooltip.TranslationKeyOfTextToShow = "UICharacterCreator_AdvancePersonality".
            // Scraping child text first therefore spoke "dot dot dot" while the real name sat
            // unread one field away. Preferring the tooltip for glyph-only captions fixes the whole
            // class at once instead of one item type at a time.
            //
            // The outfit type tab's own "..." control has NO tooltip, which is why the rule below
            // was not enough for it; it is named from its action by GetShowOptionsLabel instead.
            //
            // The scraped text is still kept as the fallback when neither resolves, because a
            // glyph the user can report beats silence.
            if (!string.IsNullOrEmpty(label) && !IsSpeakable(label))
            {
                string glyphTooltip = GetTooltipText(source);
                if (!string.IsNullOrEmpty(glyphTooltip))
                {
                    return AppendToggleState(source, glyphTooltip);
                }

                string glyphAction = GetActionLabel(source);
                if (!string.IsNullOrEmpty(glyphAction))
                {
                    return AppendToggleState(source, glyphAction);
                }
            }

            if (!string.IsNullOrEmpty(label))
            {
                return AppendToggleState(source, label);
            }

            // No text of its own - an icon button. The game's own tooltip is the label a sighted
            // player gets for exactly these controls, so read that rather than inventing one.
            string tooltip = GetTooltipText(source);
            if (!string.IsNullOrEmpty(tooltip))
            {
                return AppendToggleState(source, tooltip);
            }

            // Still nothing, so the game gives a sighted player only an icon. The last source that
            // is still the GAME's own answer rather than the mod's is what the button is wired to
            // do. Deliberately last: it can only ever replace the developer-facing "unlabeled
            // ButtonValidate" fallback, so no control that already speaks can regress through it.
            string action = GetActionLabel(source);
            if (!string.IsNullOrEmpty(action))
            {
                return AppendToggleState(source, action);
            }

            string objectName = source.gameObject != null ? source.gameObject.name : null;
            if (string.IsNullOrEmpty(objectName))
            {
                return Loc.Get("focus_unlabeled");
            }

            return AppendToggleState(source, Loc.Get("focus_unlabeled") + " " + objectName);
        }
    }
}
