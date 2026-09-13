using System;
using System.Collections.Generic;
using System.Text;
using ParalivesAccess.Speech;
using Setting;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>
    /// Reports what opening a character-creator category actually revealed, and moves focus into it.
    /// </summary>
    /// <remarks>
    /// WHY THIS EXISTS - measured in TEST 29 (2026-08-10).
    ///
    /// Pressing Enter on a category produced NO audible feedback at all, which is the exact failure
    /// mode this project treats as worst: silence is indistinguishable from "nothing happened".
    /// Every mechanism was working; nothing connected the press to its result.
    ///
    ///  - The content genuinely appears. With Tattoos open, UICharacterCreatorContextualList held 22
    ///    focusables and the mod's own panel list already contained it.
    ///  - But it never becomes the active panel. ResolveActiveGroup defaults to the FULLEST panel,
    ///    and UICharacterCreator holds 33 against the contextual list's 22, so focus stayed put.
    ///  - And nothing announced that the panel count had changed, so there was no cue to press the
    ///    panel key - which the user had no way to know was the route in.
    ///
    /// A category is a door, and this makes walking through it audible. The user chose this over a
    /// pure announcement (2026-08-10): it mirrors what a sighted player does - click a category, look
    /// at what appeared - and ';' still returns to the toolbar.
    ///
    /// THREE OUTCOMES, because a category does not always reveal the same kind of thing:
    ///  1. A leaf tag with items fills the contextual list      -> move focus there.
    ///  2. Personality opens a SEPARATE container (measured at 47 focusables), reached through the
    ///     public UICharacterCreator.UICharacterCreatorPersonality field -> move focus there.
    ///  3. A branch tag reveals SUBCATEGORIES in the toolbar rather than content. Its contextual list
    ///     is legitimately empty (game-api.md section 31), the new buttons sit right beside the user,
    ///     and moving focus would take them away from them -> announce the count, do not move.
    ///
    /// Every value read here is public game state and every read is pure. In particular this must
    /// never call Player.GetCurrentCharacterCreatorTagsHash(), which clears ResetCreatorTagHash as a
    /// side effect and would suppress the game's own list rebuild (game-api.md section 31).
    /// </remarks>
    public static class CharacterCreatorHandler
    {
        /// <summary>Set false to disable the category announcements entirely.</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>
        /// Frames to wait after the tag path changes before reading the result.
        /// </summary>
        /// <remarks>
        /// The same settle the probe needs, for the same reason. The mod ticks from a postfix on
        /// SystemManager.Update, while the list is rebuilt in
        /// UICharacterCreatorContextualList.Update and the focusables only register when
        /// UIContainer.LateUpdate calls Content.SetActive (game-api.md section 32). Reading on the
        /// change frame would reliably see an empty panel and announce the wrong outcome.
        /// </remarks>
        private const int SettleFrames = 3;

        /// <summary>Screen-space tolerance for controls that visually form one horizontal row.</summary>
        private const float CreatorRowTolerance = 12f;

        private static string _lastSignature;
        private static int _framesUntilReport = -1;
        private static readonly Dictionary<string, Focusable> LastFocusByCreatorRow =
            new Dictionary<string, Focusable>();

        private sealed class CreatorNavigationRow
        {
            public string Key;
            public string SectionKey;
            public List<Focusable> Items;
            public Focusable Preferred;
            public bool IsLandmark;
        }

        /// <summary>
        /// Applies the creator's visible list hierarchy before the general spatial navigator gets a
        /// chance to cross from one unrelated section to another.
        /// </summary>
        /// <remarks>
        /// The main category row is horizontal, while each revealed child category is a vertical
        /// list beside its parent. Pure nearest-neighbour geometry therefore makes a second Right
        /// press from a leaf such as Skin Details jump diagonally back to Personality, and can then
        /// jump again to the top-bar Cancel button. Those controls are close on screen but are not
        /// siblings in the creator's model.
        ///
        /// This method handles only source-proven creator structures. A handled boundary deliberately
        /// returns no target instead of wrapping to an unrelated control on the far side of the
        /// window.
        /// </remarks>
        internal static bool TryNavigateLogical(
            FocusableGroup group,
            IList<Focusable> groupFocusables,
            Focusable source,
            Vector2 direction,
            out Focusable target,
            out FocusableGroup destinationGroup)
        {
            target = null;
            destinationGroup = group;
            if (group == null || source == null || direction == Vector2.zero)
            {
                return false;
            }

            try
            {
                Player player = PlayerManager.Instance?.HybridPlayer1?.Player;
                UICharacterCreator creator = player != null && UI.Instance != null
                    ? UI.GetOrNull<UICharacterCreator>(player.PlayerIndex)
                    : null;
                if (creator == null || !creator.IsVisible)
                {
                    return false;
                }

                if (group is UICharacters)
                    return CreatorHouseholdHandler.TryNavigate(
                        group, groupFocusables, source, direction, out target);

                if (ReferenceEquals(group, creator))
                {
                    // Personality is a separate focus group. Down from its tab must mirror the
                    // existing Up-to-parent edge rather than sending focus into the name fields.
                    if (direction.y < 0f && Mathf.Abs(direction.y) > Mathf.Abs(direction.x)
                        && ReferenceEquals(source, GetCurrentTopLevelTab(creator))
                        && creator.UICharacterCreatorPersonality != null
                        && creator.UICharacterCreatorPersonality.IsVisible)
                    {
                        destinationGroup = creator.UICharacterCreatorPersonality;
                        target = GetPersonalityEntry(creator,
                            destinationGroup.GetComponentsInChildren<Focusable>(false));
                        return true;
                    }
                    return TryNavigateMainCreator(
                        creator, groupFocusables, source, direction, out target);
                }

                if (ReferenceEquals(group, creator.UICharacterOutfits))
                {
                    bool handled = TryNavigateOutfits(
                        creator.UICharacterOutfits, groupFocusables, source, direction, out target);
                    return ReturnToTopLevelTabAtUpperBoundary(
                        creator, handled, direction, ref target, ref destinationGroup);
                }

                if (ReferenceEquals(group, creator.UICharacterCreatorContextualList))
                {
                    bool handled = TryNavigateContextualList(
                        creator.UICharacterCreatorContextualList,
                        groupFocusables,
                        source,
                        direction,
                        out target);
                    return ReturnToTopLevelTabAtUpperBoundary(
                        creator, handled, direction, ref target, ref destinationGroup);
                }

                if (ReferenceEquals(group, creator.UICharacterCreatorPersonality))
                {
                    bool handled = TryNavigatePersonality(
                        creator, groupFocusables, source, direction, out target);
                    return ReturnToTopLevelTabAtUpperBoundary(
                        creator, handled, direction, ref target, ref destinationGroup);
                }

                UIProfilePickPersonalityTrait traitPicker = UI.Instance != null
                    ? UI.GetOrNull<UIProfilePickPersonalityTrait>(player.PlayerIndex)
                    : null;
                if (ReferenceEquals(group, traitPicker))
                {
                    return TryNavigatePersonalityPicker(
                        traitPicker, source, direction, out target);
                }

                UICharacterCreatorBodyPartDiagram bodyDiagram = UI.Instance != null
                    ? UI.GetOrNull<UICharacterCreatorBodyPartDiagram>(player.PlayerIndex)
                    : null;
                if (ReferenceEquals(group, bodyDiagram))
                {
                    // The body diagram's individual handles are already represented by the
                    // game's focus registry. Preserve that geometry while removing far-edge wrap.
                    target = FocusNavigator.FindTargetWithoutWrap(
                        group,
                        ToFocusableList(groupFocusables),
                        source,
                        direction);
                    return ReturnToTopLevelTabAtUpperBoundary(
                        creator, true, direction, ref target, ref destinationGroup);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Character-creator logical navigation threw: " + ex);
            }

            return false;
        }

        /// <summary>
        /// Turns Up at the top of a creator content panel into the missing parent edge: the current
        /// top-level creator tab.
        /// </summary>
        /// <remarks>
        /// The content areas are separate <see cref="FocusableGroup"/> instances even though the
        /// game draws them as children of the Appearance, Clothing and Personality tabs. Its native
        /// directional lookup cannot cross that group boundary. A null result from one of the
        /// source-aware navigators above means the user reached the upper row, so this is a model
        /// handoff rather than a geometric guess. Trait-choice popups deliberately do not use it:
        /// those are modal pickers, not tab content.
        /// </remarks>
        private static bool ReturnToTopLevelTabAtUpperBoundary(
            UICharacterCreator creator,
            bool handled,
            Vector2 direction,
            ref Focusable target,
            ref FocusableGroup destinationGroup)
        {
            if (!handled || target != null || direction.y <= 0f
                || Mathf.Abs(direction.y) < Mathf.Abs(direction.x))
            {
                return handled;
            }

            Focusable currentTab = GetCurrentTopLevelTab(creator);
            if (currentTab != null)
            {
                target = currentTab;
                destinationGroup = creator;
            }
            return handled;
        }

        /// <summary>Returns the game-selected main creator tab, falling back to the first tab.</summary>
        internal static Focusable GetCurrentTopLevelTab(UICharacterCreator creator)
        {
            CategoryList[] categories = creator?.GenericUI?.CategoryLists;
            return categories != null && categories.Length > 0
                ? GetSelectedOrFirst(categories[0]?.UIList)
                : null;
        }

        private static bool TryNavigateMainCreator(
            UICharacterCreator creator,
            IList<Focusable> groupFocusables,
            Focusable source,
            Vector2 direction,
            out Focusable target)
        {
            target = null;

            UIList sourceList = (source as UIListItemBase)?.ParentUIList;
            if (sourceList == null)
            {
                UIListItemBase owner = source.GetComponentInParent<UIListItemBase>();
                sourceList = owner?.ParentUIList;
                if (owner != null)
                {
                    source = owner;
                }
            }

            CategoryList[] categories = creator.GenericUI?.CategoryLists;
            List<Focusable> topBar = GetActionRow(
                groupFocusables, creator,
                "Cancel", "ValidateAndExitCharacterCreator");
            List<Focusable> nameRow = GetActionRow(
                groupFocusables, creator,
                "RandomizeFirstName", "RandomizeLastName", "ToggleEditNameDetails");
            string topBarKey = RowKey(creator, "top-bar");
            string nameRowKey = RowKey(creator, "name-actions");
            int level = FindCategoryLevel(categories, sourceList);
            if (level >= 0)
            {
                RememberListPosition(sourceList, source);
                bool horizontal = Mathf.Abs(direction.x) >= Mathf.Abs(direction.y);
                if (level == 0 && horizontal)
                {
                    target = MoveWithinList(sourceList, source, direction.x > 0f ? 1 : -1);
                    return true;
                }

                if (level == 0)
                {
                    if (direction.y > 0f)
                    {
                        target = GetRememberedOrFirst(topBar, topBarKey, FirstOrNull(topBar));
                    }
                    else if (IsCurrentCategory(source))
                    {
                        target = categories != null && categories.Length > 1
                            ? GetRememberedOrSelectedOrFirst(categories[1]?.UIList)
                            : null;
                        if (target == null)
                        {
                            target = GetRememberedOrFirst(
                                nameRow, nameRowKey, FirstOrNull(nameRow));
                        }
                    }
                    // A non-current main tab does not own the currently visible child list. Enter
                    // selects it; Down must not silently enter another tab's children.
                    return true;
                }

                if (!horizontal)
                {
                    target = MoveWithinList(sourceList, source, direction.y < 0f ? 1 : -1);
                    if (target == null)
                    {
                        target = direction.y > 0f
                            ? GetRememberedOrSelectedOrFirst(categories[level - 1]?.UIList)
                            : GetRememberedOrFirst(
                                nameRow, nameRowKey, FirstOrNull(nameRow));
                    }
                    return true;
                }

                // Child categories are vertical lists. Left used to jump from any middle item to
                // the parent row at the top of the creator; Up at the first item already provides
                // that escape. Right enters a deeper list only from the item that actually owns it.
                if (direction.x > 0f && IsCurrentCategory(source)
                    && categories != null && level + 1 < categories.Length)
                {
                    target = GetRememberedOrSelectedOrFirst(categories[level + 1]?.UIList);
                }
                return true;
            }

            if (Contains(topBar, source))
            {
                RememberRowPosition(topBarKey, topBar, source);
                if (Mathf.Abs(direction.x) >= Mathf.Abs(direction.y))
                {
                    target = MoveWithinRow(topBar, source, direction.x > 0f ? 1 : -1);
                }
                else if (direction.y < 0f)
                {
                    target = GetRememberedOrSelectedOrFirst(
                        categories != null && categories.Length > 0
                            ? categories[0]?.UIList
                            : null);
                }
                return true;
            }

            if (Contains(nameRow, source))
            {
                RememberRowPosition(nameRowKey, nameRow, source);
                if (Mathf.Abs(direction.x) >= Mathf.Abs(direction.y))
                {
                    target = MoveWithinRow(nameRow, source, direction.x > 0f ? 1 : -1);
                }
                else
                {
                    target = direction.y > 0f
                        ? GetDeepestRememberedOrSelected(categories)
                        : GetRememberedOrSelectedOrFirst(creator.LifeStageItemList);
                }
                return true;
            }

            // The demographic controls are independent horizontal rows. Remember the user's
            // position in each row so M -> Voice -> Gender returns to M rather than snapping to the
            // selected F entry. This also makes the name/life-stage boundary reciprocal.
            if (ReferenceEquals(sourceList, creator.LifeStageItemList))
            {
                RememberListPosition(sourceList, source);
                target = NavigateCreatorRow(
                    sourceList, source, direction,
                    GetRememberedOrFirst(nameRow, nameRowKey, FirstOrNull(nameRow)),
                    GetRememberedOrSelectedOrFirst(creator.GenderList));
                return true;
            }
            if (ReferenceEquals(sourceList, creator.GenderList))
            {
                RememberListPosition(sourceList, source);
                target = NavigateCreatorRow(
                    sourceList, source, direction,
                    GetRememberedOrSelectedOrFirst(creator.LifeStageItemList),
                    GetRememberedOrSelectedOrFirst(creator.VoiceItemList));
                return true;
            }
            if (ReferenceEquals(sourceList, creator.VoiceItemList))
            {
                RememberListPosition(sourceList, source);
                target = NavigateCreatorRow(
                    sourceList, source, direction,
                    GetRememberedOrSelectedOrFirst(creator.GenderList),
                    null);
                return true;
            }

            return false;
        }

        private static bool TryNavigateOutfits(
            UICharacterOutfits outfits,
            IList<Focusable> groupFocusables,
            Focusable source,
            Vector2 direction,
            out Focusable target)
        {
            target = null;
            if (outfits == null)
            {
                return false;
            }

            string prefix = "creator-outfits:" + outfits.GetInstanceID() + ":";
            var rows = new List<CreatorNavigationRow>();

            AddNavigationRow(
                rows,
                prefix + "randomize",
                prefix + "randomize",
                GetActionRow(groupFocusables, outfits, "RandomizeOutfit"),
                null,
                isLandmark: true);

            List<Focusable> types = GetActiveItems(outfits.OutfitTypeList);
            AddNavigationRow(
                rows,
                prefix + "types",
                prefix + "types",
                types,
                GetSelectedOrFirst(outfits.OutfitTypeList),
                isLandmark: false);

            var toggleButtons = new List<Focusable>();
            foreach (Focusable typeFocusable in types)
            {
                UICharacterOutfitType type = typeFocusable as UICharacterOutfitType;
                if (type?.ShowOptionsButton != null
                    && type.ShowOptionsButton.activeInHierarchy)
                {
                    AddFocusablesWithin(
                        toggleButtons, groupFocusables, type.ShowOptionsButton);
                }
            }
            AddNavigationRow(
                rows,
                prefix + "show-options",
                prefix + "show-options",
                toggleButtons,
                FirstOrNull(toggleButtons),
                isLandmark: false);

            List<Focusable> options = GetActiveItems(outfits.OutfitOptionsList);
            AddFocusablesWithin(options, groupFocusables, outfits.AddButton);
            Focusable selectedOption = null;
            foreach (Focusable option in options)
            {
                UICharacterOutfitOptions typed = option as UICharacterOutfitOptions;
                if (typed != null && typed.OutfitGUID == outfits.CurrentOutfit)
                {
                    selectedOption = option;
                    break;
                }
            }
            AddNavigationRow(
                rows,
                prefix + "options",
                prefix + "options",
                options,
                selectedOption,
                isLandmark: false);

            return NavigateRows(rows, source, direction, out target);
        }

        private static bool TryNavigatePersonality(
            UICharacterCreator creator,
            IList<Focusable> groupFocusables,
            Focusable source,
            Vector2 direction,
            out Focusable target)
        {
            target = null;
            UIProfile profile = creator?.UIProfile;
            if (profile?.UIList?.CurrentItems == null)
            {
                return false;
            }

            source = NormalizePersonalitySource(source);
            string prefix = "creator-personality:" + profile.GetInstanceID() + ":";
            var rows = new List<CreatorNavigationRow>();

            AddNavigationRow(
                rows,
                prefix + "all-randomize",
                prefix + "all",
                GetActionRow(
                    groupFocusables, profile, nameof(UIProfile.RandomizePersonality)),
                null,
                isLandmark: true);

            foreach (UIListItemBase outer in profile.UIList.CurrentItems)
            {
                if (outer == null || !outer.gameObject.activeInHierarchy)
                {
                    continue;
                }

                string section = prefix + outer.GetInstanceID();
                UIProfileTraitStats stats = outer as UIProfileTraitStats;
                if (stats != null)
                {
                    // The stat values are visually above their Randomize Stats button. Keeping
                    // them as separate rows makes Down follow the actual authored reading order.
                    AddNavigationRow(
                        rows,
                        section + ":values",
                        section,
                        GetActiveItems(stats.UIListStatItems),
                        null,
                        isLandmark: false);
                    AddNavigationRow(
                        rows,
                        section + ":randomize",
                        section,
                        GetActionRowWithin(
                            groupFocusables,
                            profile,
                            stats.gameObject,
                            nameof(UIProfile.RandomizePersonalityStats)),
                        null,
                        isLandmark: false);
                    continue;
                }

                UIProfileTraitSimple simple = outer as UIProfileTraitSimple;
                if (simple != null)
                {
                    // The section shell merely repeats the category and chosen value. Its
                    // Randomize button and semantic trait item are the two real controls.
                    List<Focusable> row = GetActionRowWithin(
                        groupFocusables,
                        profile,
                        simple.gameObject,
                        nameof(UIProfile.RandomizePersonalitySimple));
                    List<Focusable> traitChoices = GetActiveItems(simple.UIListTraits);
                    row.AddRange(traitChoices);
                    AddNavigationRow(
                        rows,
                        section + ":choice",
                        section,
                        row,
                        FirstOrNull(traitChoices),
                        isLandmark: false);
                    continue;
                }

                UIProfileTraitMix mix = outer as UIProfileTraitMix;
                if (mix != null)
                {
                    AddNavigationRow(
                        rows,
                        section + ":randomize",
                        section,
                        GetActionRowWithin(
                            groupFocusables,
                            profile,
                            mix.gameObject,
                            nameof(UIProfile.RandomizePersonalityMix)),
                        null,
                        isLandmark: false);
                    rows.AddRange(BuildPositionRows(
                        GetActiveItems(mix.UIListTraits),
                        section + ":choice-row:",
                        section));
                }
            }

            return NavigateRows(rows, source, direction, out target);
        }

        /// <summary>Returns the authored start of Personality rather than an arbitrary leaf.</summary>
        internal static Focusable GetPersonalityEntry(
            UICharacterCreator creator, IList<Focusable> focusables)
        {
            UIProfile profile = creator?.UIProfile;
            if (profile == null)
                return null;
            Focusable randomize = FirstOrNull(GetActionRow(
                focusables, profile, nameof(UIProfile.RandomizePersonality)));
            if (randomize != null)
                return randomize;
            if (profile.UIList?.CurrentItems != null)
            {
                foreach (UIListItemBase section in profile.UIList.CurrentItems)
                {
                    if (section is UIProfileTraitStats stats && stats.gameObject.activeInHierarchy)
                        return GetRememberedOrSelectedOrFirst(stats.UIListStatItems);
                }
            }
            return null;
        }

        private static bool TryNavigatePersonalityPicker(
            UIProfilePickPersonalityTrait picker,
            Focusable source,
            Vector2 direction,
            out Focusable target)
        {
            target = null;
            if (picker?.UIListTraits == null)
            {
                return false;
            }

            UIProfilePickPersonalityTraitItem item =
                source?.GetComponentInParent<UIProfilePickPersonalityTraitItem>();
            if (item != null)
            {
                source = item;
            }

            string prefix = "creator-personality-picker:"
                + picker.GetInstanceID() + ":";
            List<CreatorNavigationRow> rows = BuildPositionRows(
                GetActiveItems(picker.UIListTraits),
                prefix + "row:",
                prefix + "choices");
            return NavigateRows(rows, source, direction, out target);
        }

        private static Focusable NormalizePersonalitySource(Focusable source)
        {
            if (source == null)
            {
                return null;
            }

            UIProfileTraitStatsItem stat =
                source.GetComponentInParent<UIProfileTraitStatsItem>();
            if (stat != null)
            {
                return stat;
            }

            UIProfileTraitSimpleItem simpleItem =
                source.GetComponentInParent<UIProfileTraitSimpleItem>();
            if (simpleItem != null)
            {
                return simpleItem;
            }

            UIProfileTraitMixItem mixItem =
                source.GetComponentInParent<UIProfileTraitMixItem>();
            if (mixItem != null)
            {
                return mixItem;
            }

            UIProfileTraitStats stats = source as UIProfileTraitStats;
            if (stats != null)
            {
                return GetRememberedOrSelectedOrFirst(stats.UIListStatItems) ?? source;
            }

            UIProfileTraitSimple simple = source as UIProfileTraitSimple;
            if (simple != null)
            {
                return GetRememberedOrSelectedOrFirst(simple.UIListTraits) ?? source;
            }

            UIProfileTraitMix mix = source as UIProfileTraitMix;
            if (mix != null)
            {
                return GetRememberedOrSelectedOrFirst(mix.UIListTraits) ?? source;
            }

            return source;
        }

        private static bool TryNavigateContextualList(
            UICharacterCreatorContextualList contextual,
            IList<Focusable> groupFocusables,
            Focusable source,
            Vector2 direction,
            out Focusable target)
        {
            target = null;
            if (contextual?.UIList?.CurrentItems == null)
            {
                return false;
            }

            // Do not collapse coincident controls before assigning them to their authored
            // contextual section. A section shell and its actionable child can legitimately
            // occupy the same coordinates; global de-duplication would discard the child before
            // we know which semantic row it belongs to. Rows de-duplicate locally below.
            List<Focusable> active = FilterActive(ToFocusableList(groupFocusables));
            var assigned = new HashSet<Focusable>();
            var rows = new List<CreatorNavigationRow>();
            string prefix = "creator-context:" + contextual.GetInstanceID() + ":";

            foreach (UIListItemBase outer in contextual.UIList.CurrentItems)
            {
                if (outer == null || !outer.gameObject.activeInHierarchy)
                {
                    continue;
                }

                string section = prefix + outer.GetInstanceID();
                var direct = new List<Focusable>();
                var descendants = new List<Focusable>();
                foreach (Focusable candidate in active)
                {
                    if (candidate == null || assigned.Contains(candidate))
                    {
                        continue;
                    }

                    UIListItemBase nearestOwner =
                        candidate.GetComponentInParent<UIListItemBase>();
                    if (ReferenceEquals(candidate, outer)
                        || ReferenceEquals(nearestOwner, outer))
                    {
                        direct.Add(candidate);
                        assigned.Add(candidate);
                    }
                    else if (candidate.transform.IsChildOf(outer.transform))
                    {
                        descendants.Add(candidate);
                        assigned.Add(candidate);
                    }
                }

                AddNavigationRow(
                    rows,
                    section + ":landmark",
                    section,
                    direct,
                    outer as Focusable,
                    isLandmark: true);

                List<CreatorNavigationRow> descendantRows = BuildPositionRows(
                    descendants, section + ":row:", section);
                rows.AddRange(descendantRows);
            }

            var unowned = new List<Focusable>();
            foreach (Focusable candidate in active)
            {
                if (!assigned.Contains(candidate))
                {
                    unowned.Add(candidate);
                }
            }
            rows.AddRange(BuildPositionRows(
                unowned, prefix + "unowned-row:", prefix + "unowned"));

            return NavigateRows(rows, source, direction, out target);
        }

        private static bool NavigateRows(
            List<CreatorNavigationRow> rows,
            Focusable source,
            Vector2 direction,
            out Focusable target)
        {
            target = null;
            int sourceRowIndex = -1;
            int sourceItemIndex = -1;
            // Prefer object identity. Coincident registrations are a fallback for a pooled twin,
            // not evidence that two controls in different semantic rows are interchangeable (a
            // section container can share a pivot with its first child).
            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                List<Focusable> items = rows[rowIndex].Items;
                for (int itemIndex = 0; itemIndex < items.Count; itemIndex++)
                {
                    if (ReferenceEquals(items[itemIndex], source))
                    {
                        sourceRowIndex = rowIndex;
                        sourceItemIndex = itemIndex;
                        break;
                    }
                }
                if (sourceRowIndex >= 0)
                {
                    break;
                }
            }

            if (sourceRowIndex < 0)
            {
                for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
                {
                    List<Focusable> items = rows[rowIndex].Items;
                    for (int itemIndex = 0; itemIndex < items.Count; itemIndex++)
                    {
                        if (FocusNavigator.AreCoincident(items[itemIndex], source))
                        {
                            sourceRowIndex = rowIndex;
                            sourceItemIndex = itemIndex;
                            source = items[itemIndex];
                            break;
                        }
                    }
                    if (sourceRowIndex >= 0)
                    {
                        break;
                    }
                }
            }

            if (sourceRowIndex < 0)
            {
                return false;
            }

            CreatorNavigationRow sourceRow = rows[sourceRowIndex];
            RememberRowPosition(sourceRow.Key, sourceRow.Items, source);
            bool horizontal = Mathf.Abs(direction.x) >= Mathf.Abs(direction.y);
            if (horizontal)
            {
                int nextItem = sourceItemIndex + (direction.x > 0f ? 1 : -1);
                if (nextItem >= 0 && nextItem < sourceRow.Items.Count)
                {
                    target = sourceRow.Items[nextItem];
                }
                return true;
            }

            int nextRowIndex = sourceRowIndex + (direction.y > 0f ? -1 : 1);
            if (nextRowIndex < 0 || nextRowIndex >= rows.Count)
            {
                return true;
            }

            CreatorNavigationRow nextRow = rows[nextRowIndex];
            target = GetRememberedOrFirst(
                nextRow.Items, nextRow.Key, preferred: null);
            if (target == null)
            {
                bool enteringNewSection = !string.Equals(
                    sourceRow.SectionKey, nextRow.SectionKey, StringComparison.Ordinal);
                if (sourceRow.IsLandmark || nextRow.IsLandmark || enteringNewSection)
                {
                    target = ResolveRowMember(nextRow.Items, nextRow.Preferred)
                        ?? FirstOrNull(nextRow.Items);
                }
                else
                {
                    target = ClosestByHorizontalPosition(nextRow.Items, source);
                }
            }
            return true;
        }

        private static List<CreatorNavigationRow> BuildPositionRows(
            List<Focusable> candidates,
            string keyPrefix,
            string sectionKey)
        {
            List<Focusable> prepared = PrepareRow(candidates);
            prepared.Sort((a, b) => b.transform.position.y.CompareTo(a.transform.position.y));
            var grouped = new List<List<Focusable>>();
            var anchors = new List<float>();
            foreach (Focusable candidate in prepared)
            {
                int groupIndex = -1;
                for (int i = 0; i < anchors.Count; i++)
                {
                    if (Mathf.Abs(candidate.transform.position.y - anchors[i])
                        <= CreatorRowTolerance)
                    {
                        groupIndex = i;
                        break;
                    }
                }

                if (groupIndex < 0)
                {
                    groupIndex = grouped.Count;
                    grouped.Add(new List<Focusable>());
                    anchors.Add(candidate.transform.position.y);
                }
                grouped[groupIndex].Add(candidate);
            }

            var result = new List<CreatorNavigationRow>();
            for (int i = 0; i < grouped.Count; i++)
            {
                AddNavigationRow(
                    result,
                    keyPrefix + i,
                    sectionKey,
                    grouped[i],
                    null,
                    isLandmark: false);
            }
            return result;
        }

        private static void AddNavigationRow(
            List<CreatorNavigationRow> rows,
            string key,
            string sectionKey,
            List<Focusable> candidates,
            Focusable preferred,
            bool isLandmark)
        {
            List<Focusable> items = PrepareRow(candidates);
            if (items.Count == 0)
            {
                return;
            }
            items.Sort((a, b) => a.transform.position.x.CompareTo(b.transform.position.x));
            rows.Add(new CreatorNavigationRow
            {
                Key = key,
                SectionKey = sectionKey,
                Items = items,
                Preferred = ResolveRowMember(items, preferred) ?? items[0],
                IsLandmark = isLandmark
            });
        }

        private static List<Focusable> PrepareRow(List<Focusable> candidates)
        {
            return FocusNavigator.GetDistinctReachable(FilterActive(candidates));
        }

        private static List<Focusable> FilterActive(List<Focusable> candidates)
        {
            if (candidates == null)
            {
                return new List<Focusable>();
            }

            var active = new List<Focusable>();
            foreach (Focusable candidate in candidates)
            {
                if (candidate != null && !candidate.IsShoulderButton
                    && candidate.gameObject.activeInHierarchy
                    && !FocusNavigator.ShouldSkip(candidate))
                {
                    active.Add(candidate);
                }
            }
            return active;
        }

        private static void AddFocusablesWithin(
            List<Focusable> destination,
            IList<Focusable> candidates,
            GameObject root)
        {
            if (destination == null || candidates == null || root == null
                || !root.activeInHierarchy)
            {
                return;
            }

            foreach (Focusable candidate in candidates)
            {
                if (candidate != null
                    && (ReferenceEquals(candidate.gameObject, root)
                        || candidate.transform.IsChildOf(root.transform)))
                {
                    destination.Add(candidate);
                }
            }
        }

        private static List<Focusable> ToFocusableList(IList<Focusable> source)
        {
            var result = new List<Focusable>();
            if (source == null)
            {
                return result;
            }
            for (int i = 0; i < source.Count; i++)
            {
                result.Add(source[i]);
            }
            return result;
        }

        private static Focusable ClosestByHorizontalPosition(
            List<Focusable> row, Focusable source)
        {
            if (row == null || row.Count == 0 || source == null)
            {
                return FirstOrNull(row);
            }

            float x = source.transform.position.x;
            Focusable best = null;
            float bestDistance = float.MaxValue;
            foreach (Focusable candidate in row)
            {
                float distance = Mathf.Abs(candidate.transform.position.x - x);
                if (distance < bestDistance)
                {
                    best = candidate;
                    bestDistance = distance;
                }
            }
            return best;
        }

        private static bool SameNavigationControl(Focusable a, Focusable b)
        {
            return a != null && b != null
                && (ReferenceEquals(a, b) || FocusNavigator.AreCoincident(a, b));
        }

        private static string RowKey(UnityEngine.Object owner, string name)
        {
            return "creator:" + (owner != null ? owner.GetInstanceID() : 0) + ":" + name;
        }

        private static string ListRowKey(UIList list)
        {
            return RowKey(list, "list");
        }

        private static void RememberListPosition(UIList list, Focusable source)
        {
            if (list != null)
            {
                RememberRowPosition(ListRowKey(list), GetActiveItems(list), source);
            }
        }

        private static void RememberRowPosition(
            string key, List<Focusable> row, Focusable source)
        {
            Focusable member = ResolveRowMember(row, source);
            if (member != null)
            {
                LastFocusByCreatorRow[key] = member;
            }
        }

        private static Focusable GetRememberedOrSelectedOrFirst(UIList list)
        {
            List<Focusable> row = GetActiveItems(list);
            return GetRememberedOrFirst(
                row, ListRowKey(list), GetSelectedOrFirst(list));
        }

        private static Focusable GetRememberedOrFirst(
            List<Focusable> row, string key, Focusable preferred)
        {
            if (row == null || row.Count == 0)
            {
                return null;
            }

            if (LastFocusByCreatorRow.TryGetValue(key, out Focusable remembered))
            {
                Focusable live = ResolveRowMember(row, remembered);
                if (live != null)
                {
                    return live;
                }
                LastFocusByCreatorRow.Remove(key);
            }
            return ResolveRowMember(row, preferred);
        }

        private static Focusable ResolveRowMember(
            List<Focusable> row, Focusable candidate)
        {
            if (row == null || candidate == null)
            {
                return null;
            }
            foreach (Focusable member in row)
            {
                if (SameNavigationControl(member, candidate))
                {
                    return member;
                }
            }
            return null;
        }

        private static bool IsCurrentCategory(Focusable source)
        {
            UIGenericCategoriesButton category =
                source?.GetComponentInParent<UIGenericCategoriesButton>();
            return category?.Data?.IsOn == true;
        }

        private static Focusable GetDeepestRememberedOrSelected(CategoryList[] categories)
        {
            if (categories == null)
            {
                return null;
            }
            for (int i = categories.Length - 1; i >= 0; i--)
            {
                Focusable item = GetRememberedOrSelectedOrFirst(categories[i]?.UIList);
                if (item != null)
                {
                    return item;
                }
            }
            return null;
        }

        /// <summary>True when Enter must call a public creator callback instead of a shell click.</summary>
        internal static bool IsSemanticActivationTarget(Focusable source)
        {
            return source != null
                && (source.GetComponentInParent<UICharacterCreatorGenderItem>() != null
                    || source.GetComponentInParent<UIProfilePickPersonalityTraitItem>() != null
                    || source.GetComponentInParent<UIProfileTraitSimpleItem>() != null
                    || source.GetComponentInParent<UIProfileTraitMixItem>() != null);
        }

        /// <summary>
        /// Invokes the exact native control used by the pointer-visible creator choice.
        /// </summary>
        /// <param name="source">The focused presentation row or one of its nested buttons.</param>
        /// <param name="result">A concise state change to announce, when the callback commits one.</param>
        /// <returns>True when this was a recognized creator control, including a locked one.</returns>
        internal static bool TryActivateSemantic(Focusable source, out string result)
        {
            result = null;
            if (source == null)
            {
                return false;
            }

            try
            {
                UICharacterCreatorGenderItem gender =
                    source.GetComponentInParent<UICharacterCreatorGenderItem>();
                if (gender != null)
                {
                    ParaButton nativeButton = gender.Button;
                    if (nativeButton == null || !nativeButton.enabled
                        || !nativeButton.Interactable
                        || !nativeButton.gameObject.activeInHierarchy)
                    {
                        result = Loc.Get("adjust_locked");
                        return true;
                    }

                    // The row and this button are coincident registrations. The row is the useful
                    // spoken target, but only the child button owns the prefab's OnClickGender
                    // listener and configured click sound. Run a complete native button gesture
                    // while leaving the row as the pending state target; UpdateGenders will then
                    // change its label to "selected" and the standard result observer will say it.
                    MenuNavigationHandler.PressFocusable(nativeButton);
                    MenuNavigationHandler.ReleaseFocusable(nativeButton);
                    return true;
                }

                UIProfilePickPersonalityTraitItem picker =
                    source.GetComponentInParent<UIProfilePickPersonalityTraitItem>();
                if (picker != null)
                {
                    string name = ReadTranslated(picker.TraitLabel);
                    if (picker.ClickAction == null)
                    {
                        result = Loc.Get("creator_choice_unavailable");
                        return true;
                    }

                    picker.OnClickItem();
                    result = string.IsNullOrWhiteSpace(name)
                        ? Loc.Get("creator_choice_selected")
                        : Loc.Get("creator_choice_selected_named", name);
                    return true;
                }

                UIProfileTraitSimpleItem simple =
                    source.GetComponentInParent<UIProfileTraitSimpleItem>();
                if (simple != null)
                {
                    ParaButton activeButton = simple.ButtonPickOne != null
                        && simple.ButtonPickOne.gameObject.activeInHierarchy
                            ? simple.ButtonPickOne
                            : simple.ButtonWithTrait;
                    if (activeButton == null || !activeButton.enabled || !activeButton.Interactable)
                    {
                        result = Loc.Get("adjust_locked");
                        return true;
                    }

                    simple.OnTraitItemClicked();
                    return true;
                }

                UIProfileTraitMixItem mix =
                    source.GetComponentInParent<UIProfileTraitMixItem>();
                if (mix == null)
                {
                    return false;
                }

                UIProfile profile = mix.UIProfileTraitMix?.UIProfile;
                if (profile == null || !profile.AllowEditingPersonality
                    || mix.PersonalityTrait == null)
                {
                    result = Loc.Get("adjust_locked");
                    return true;
                }

                if (mix.Toggle != null && mix.Toggle.gameObject.activeInHierarchy)
                {
                    mix.OnToggleClicked();
                    // The ordinary settled activation observer will see GetMixedTraitLabel change
                    // from selected to not selected (or vice versa) on the next frame. Do not queue
                    // a second paraphrase of that same result.
                    return true;
                }

                if (mix.Dropdown != null && mix.Dropdown.activeInHierarchy)
                {
                    mix.OnDropdownClicked();
                    return true;
                }

                result = Loc.Get("creator_choice_unavailable");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Activating a character-creator choice threw: " + ex);
                result = Loc.Get("creator_choice_unavailable");
                return true;
            }
        }

        private static int FindCategoryLevel(CategoryList[] categories, UIList list)
        {
            if (categories == null || list == null)
            {
                return -1;
            }

            for (int i = 0; i < categories.Length; i++)
            {
                if (ReferenceEquals(categories[i]?.UIList, list))
                {
                    return i;
                }
            }
            return -1;
        }

        private static Focusable NavigateCreatorRow(
            UIList row,
            Focusable source,
            Vector2 direction,
            Focusable above,
            Focusable below)
        {
            if (Mathf.Abs(direction.x) >= Mathf.Abs(direction.y))
            {
                return MoveWithinList(row, source, direction.x > 0f ? 1 : -1);
            }
            return direction.y > 0f ? above : below;
        }

        private static List<Focusable> GetActionRow(
            IList<Focusable> focusables,
            UnityEngine.Object actionTarget,
            params string[] methodNames)
        {
            var result = new List<Focusable>();
            if (focusables == null || actionTarget == null)
            {
                return result;
            }

            foreach (Focusable focusable in focusables)
            {
                if (focusable == null || !focusable.gameObject.activeInHierarchy
                    || FocusNavigator.ShouldSkip(focusable)
                    || !HasAction(focusable, actionTarget, methodNames))
                {
                    continue;
                }
                result.Add(focusable);
            }

            result.Sort((a, b) => a.transform.position.x.CompareTo(b.transform.position.x));
            return FocusNavigator.GetDistinctReachable(result);
        }

        private static List<Focusable> GetActionRowWithin(
            IList<Focusable> focusables,
            UnityEngine.Object actionTarget,
            GameObject root,
            params string[] methodNames)
        {
            List<Focusable> actions = GetActionRow(
                focusables, actionTarget, methodNames);
            if (root == null)
            {
                return new List<Focusable>();
            }

            actions.RemoveAll(action => action == null
                || (!ReferenceEquals(action.gameObject, root)
                    && !action.transform.IsChildOf(root.transform)));
            return actions;
        }

        private static bool HasAction(
            Focusable focusable,
            UnityEngine.Object actionTarget,
            string[] methodNames)
        {
            ParaButton button = focusable?.GetComponent<ParaButton>();
            if (button?.ButtonClick == null)
            {
                return false;
            }

            int count = button.ButtonClick.GetPersistentEventCount();
            for (int i = 0; i < count; i++)
            {
                if (!ReferenceEquals(button.ButtonClick.GetPersistentTarget(i), actionTarget))
                {
                    continue;
                }

                string method = button.ButtonClick.GetPersistentMethodName(i);
                foreach (string expected in methodNames)
                {
                    if (string.Equals(method, expected, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool Contains(List<Focusable> row, Focusable source)
        {
            return row != null && row.Exists(item => ReferenceEquals(item, source)
                || source.transform.IsChildOf(item.transform));
        }

        private static Focusable MoveWithinRow(
            List<Focusable> row, Focusable source, int delta)
        {
            int index = row.FindIndex(item => ReferenceEquals(item, source)
                || source.transform.IsChildOf(item.transform));
            int next = index + delta;
            return index >= 0 && next >= 0 && next < row.Count ? row[next] : null;
        }

        private static Focusable FirstOrNull(List<Focusable> row)
        {
            return row != null && row.Count > 0 ? row[0] : null;
        }

        private static Focusable MoveWithinList(UIList list, Focusable source, int delta)
        {
            List<Focusable> items = GetActiveItems(list);
            int index = items.FindIndex(item => ReferenceEquals(item, source)
                || source.transform.IsChildOf(item.transform));
            if (index < 0)
            {
                return null;
            }

            int next = index + delta;
            return next >= 0 && next < items.Count ? items[next] : null;
        }

        private static Focusable GetSelectedOrFirst(UIList list)
        {
            List<Focusable> items = GetActiveItems(list);
            foreach (Focusable item in items)
            {
                UIGenericCategoriesButton category =
                    item.GetComponent<UIGenericCategoriesButton>();
                if (category?.Data?.IsOn == true)
                {
                    return item;
                }

                ParaToggle toggle = item.GetComponent<ParaToggle>();
                if (toggle?.IsOn == true)
                {
                    return item;
                }
            }
            return items.Count > 0 ? items[0] : null;
        }

        private static List<Focusable> GetActiveItems(UIList list)
        {
            var result = new List<Focusable>();
            if (list?.CurrentItems == null)
            {
                return result;
            }

            foreach (UIListItemBase item in list.CurrentItems)
            {
                var focusable = item as Focusable;
                if (focusable != null && focusable.gameObject.activeInHierarchy
                    && !FocusNavigator.ShouldSkip(focusable))
                {
                    result.Add(focusable);
                }
            }
            return result;
        }

        private static string ReadTranslated(TranslatedText translated)
        {
            string text = translated?.Text?.GetParsedText();
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }


        /// <summary>Called once per frame from the game loop hook.</summary>
        public static void Tick()
        {
            if (!Enabled)
            {
                return;
            }

            try
            {
                HybridPlayer hybridPlayer = PlayerManager.Instance != null
                    ? PlayerManager.Instance.HybridPlayer1
                    : null;

                Player player = hybridPlayer != null ? hybridPlayer.Player : null;
                if (player == null)
                {
                    return;
                }

                UICharacterCreator creator = UI.Instance != null
                    ? UI.GetOrNull<UICharacterCreator>(player.PlayerIndex)
                    : null;

                if (creator == null || !creator.IsVisible)
                {
                    // Leaving the creator resets the baseline so re-entering does not replay the
                    // last category as though the user had just opened it. Row memories contain
                    // pooled Unity controls and must not carry into the next creator session.
                    _lastSignature = null;
                    _framesUntilReport = -1;
                    LastFocusByCreatorRow.Clear();
                    return;
                }

                string signature = BuildSignature(player);

                if (signature != _lastSignature)
                {
                    bool isFirstObservation = _lastSignature == null;
                    _lastSignature = signature;

                    // Entering the creator auto-selects the first main tag (RefreshMainSectionTags),
                    // which is a change the user did not make. Seeding silently keeps the mod from
                    // announcing a category nobody opened.
                    _framesUntilReport = isFirstObservation ? -1 : SettleFrames;
                    return;
                }

                if (_framesUntilReport < 0 || _framesUntilReport-- > 0)
                {
                    return;
                }

                Report(player, creator);
            }
            catch (Exception ex)
            {
                // Runs inside the game's loop; contain it rather than let it escape into game code.
                Enabled = false;
                Plugin.Log.LogError("CharacterCreatorHandler disabled after an exception: " + ex);
            }
        }

        /// <summary>
        /// Builds a pure-read change signal from the open tag path and the selected character.
        /// </summary>
        private static string BuildSignature(Player player)
        {
            var sb = new StringBuilder();
            AppendGuids(sb, player.CharacterCreatorTags);
            sb.Append('|');
            AppendGuids(sb, player.CharacterCreatorCurrentTagGUIDs);
            sb.Append('|').Append(player.GetSelectedCharacterGUID());
            return sb.ToString();
        }

        private static void AppendGuids(StringBuilder sb, ulong[] guids)
        {
            if (guids == null)
            {
                sb.Append("(null)");
                return;
            }

            for (int i = 0; i < guids.Length; i++)
            {
                sb.Append(guids[i]).Append(',');
            }
        }

        private static void Report(Player player, UICharacterCreator creator)
        {
            string categoryName = GetOpenCategoryName(player);
            if (string.IsNullOrEmpty(categoryName))
            {
                // No name means no tag is open - there is nothing to report.
                return;
            }

            // 1. The contextual list, which holds equipment, sliders, swatches and decal placement.
            UICharacterCreatorContextualList list = creator.UICharacterCreatorContextualList;
            if (list != null && MenuNavigationHandler.SelectPanel(list, categoryName))
            {
                Plugin.Log.LogInfo($"[Creator] '{categoryName}' opened its contextual list.");
                return;
            }

            // 2. Personality has its own container rather than contextual-list content.
            UIContainer personality = creator.UICharacterCreatorPersonality;
            if (personality != null && personality.IsVisible
                && MenuNavigationHandler.SelectPanel(personality, categoryName))
            {
                Plugin.Log.LogInfo($"[Creator] '{categoryName}' opened its own panel.");
                return;
            }

            // 3. A branch tag: the children appeared in the toolbar beside the user, so say how many
            // and leave focus where it is.
            int children = CountChildren(player);
            if (children > 0)
            {
                string message = children == 1
                    ? Loc.Get("creator_subcategories_one", categoryName)
                    : Loc.Get("creator_subcategories", categoryName, children);

                Plugin.Log.LogInfo("[Creator] " + message);
                ScreenReader.Speak(message);
                return;
            }

            // Nothing was revealed anywhere. Say so rather than leave the press silent - a category
            // that opens onto nothing is information, and silence would read as a broken mod.
            string empty = Loc.Get("creator_category_empty", categoryName);
            Plugin.Log.LogInfo("[Creator] " + empty);
            ScreenReader.Speak(empty);
        }

        /// <summary>
        /// Gives a character-creator panel the name of the category it belongs to, or null when the
        /// group is not one of them.
        /// </summary>
        /// <remarks>
        /// TEST 30 measured the two routes in disagreeing: opening a category announced "Personality,
        /// 47 items" while cycling to the same panel with the panel key announced
        /// "UICharacterCreatorPersonality, 47 items, panel 4 of 4". Same panel, two names, one of
        /// them a raw class name. This makes the panel key use the same words as the auto-open.
        /// </remarks>
        /// <param name="group">The panel being announced.</param>
        /// <returns>The open category's name, or null to fall back to the object name.</returns>
        public static string GetPanelDisplayName(FocusableGroup group)
        {
            if (group == null)
            {
                return null;
            }

            // External game API: this runs inside an announcement and must never throw.
            try
            {
                HybridPlayer hybridPlayer = PlayerManager.Instance != null
                    ? PlayerManager.Instance.HybridPlayer1
                    : null;

                Player player = hybridPlayer != null ? hybridPlayer.Player : null;
                if (player == null || UI.Instance == null)
                {
                    return null;
                }

                UICharacterCreator creator = UI.GetOrNull<UICharacterCreator>(player.PlayerIndex);
                if (creator == null || !creator.IsVisible)
                {
                    return null;
                }

                // Matched by identity against the window's own public fields rather than by object
                // name, so a renamed prefab object degrades to the old behaviour instead of
                // mislabelling a different panel.
                bool isCreatorPanel =
                    ReferenceEquals(group, creator.UICharacterCreatorContextualList)
                    || ReferenceEquals(group, creator.UICharacterCreatorPersonality);

                return isCreatorPanel ? GetOpenCategoryName(player) : null;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Resolving a character-creator panel name threw: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Names the category the game currently has open, in the player's language.
        /// </summary>
        /// <remarks>
        /// Read from CharacterCreatorCurrentTagGUIDs - the tag that actually drives the contextual
        /// list - rather than from the button the user pressed. Re-clicking an open category
        /// COLLAPSES it to its parent (OnTagItemClicked:505-509), so the pressed button and the open
        /// category are genuinely different things and only the game's own state knows which.
        ///
        /// CharacterCreatorTag.TranslationKey is the game's own property ("CharacterCreatorTag_" +
        /// DisplayName), the same key UIGenericCategoriesButton uses, so the spoken name matches the
        /// toolbar button exactly.
        /// </remarks>
        private static string GetOpenCategoryName(Player player)
        {
            // External game API: a settings lookup or an unknown GUID must never break a tick.
            try
            {
                ulong[] current = player.CharacterCreatorCurrentTagGUIDs;
                if (current == null || current.Length == 0 || current[0] == 0UL)
                {
                    return null;
                }

                Setting.CharacterCreatorTag tag =
                    Settings.Get<Setting.CharacterCreatorTags>().GetTagByGUID(current[0]);

                if (tag == null)
                {
                    return null;
                }

                string translated = TranslationManager.Get(tag.TranslationKey);

                // A missing translation echoes the key back; the raw DisplayName reads better than
                // "CharacterCreatorTag_Tattoos".
                return string.IsNullOrEmpty(translated) || translated == tag.TranslationKey
                    ? tag.DisplayName
                    : translated.Trim();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Reading the open character-creator tag threw: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Counts the subcategories the open tag exposes in the toolbar.
        /// </summary>
        private static int CountChildren(Player player)
        {
            // External game API: an unknown GUID must degrade to "no children", never throw.
            try
            {
                ulong[] current = player.CharacterCreatorCurrentTagGUIDs;
                if (current == null || current.Length == 0 || current[0] == 0UL)
                {
                    return 0;
                }

                Setting.CharacterCreatorTag tag =
                    Settings.Get<Setting.CharacterCreatorTags>().GetTagByGUID(current[0]);

                return tag?.ChildrenTagsInCharacterCreatorUI?.Length ?? 0;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Counting character-creator subcategories threw: {ex}");
                return 0;
            }
        }
    }
}
