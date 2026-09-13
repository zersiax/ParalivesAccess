using System;
using System.Collections.Generic;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>
    /// Moves focus within a <c>FocusableGroup</c>, skipping duplicate registrations.
    /// </summary>
    /// <remarks>
    /// WHY THIS EXISTS - see docs/game-api.md section 28.
    ///
    /// UICharacterCreator.UpdateGenders builds its row with GetOrReplaceItemAtIndex and then pools
    /// only items at or after the final index, so an item swapped out at a LOWER index is never
    /// reclaimed and stays registered. Measured at runtime: the gender row has four controls and
    /// EIGHT focusables, in exact coordinate pairs.
    ///
    /// That breaks the game's own FocusableGroup.GetFocusableInDirection, which scores candidates as
    /// distance * angleCoefficient and excludes only the source itself. A twin at the SAME position
    /// is a different object at distance zero: (candidate - source).normalized is Vector2.zero,
    /// Vector2.Angle returns 0 so the angle gate passes for EVERY direction, and a score of 0 beats
    /// every real candidate. Focus can never leave the pair, in any direction. Age sits 50px above
    /// and Voice 40px below and simply never win.
    ///
    /// This is a GAME bug that hits gamepad players identically, so it cannot be fixed by driving
    /// the game's navigation differently - the defect is inside the search itself.
    ///
    /// DESIGN: this mirrors the game's algorithm exactly - same Settings.UI tuning values, same
    /// angle gate, same distance weighting, same wrap-around retry - and differs in ONE respect: it
    /// navigates over a list deduplicated by screen position. On a screen with no duplicates it
    /// therefore behaves identically to the game, so the mod does not present two different
    /// navigation models depending on which bug a screen happens to carry. Since v0.39.35 this
    /// spatial algorithm is the creator fallback; ordinary panels use LogicalFocusNavigator.
    ///
    /// It deliberately does NOT call FocusableGroup.UnregisterFocusable to delete the duplicates.
    /// The game writes through whichever instance GetOrReplaceItemAtIndex currently returns, and
    /// which twin is live is not observable from outside; keeping the wrong one would leave a
    /// control whose click acts on a discarded item - a silent failure, the exact kind a blind user
    /// cannot detect. Choosing which twin to FOCUS is reversible; deleting a registration is not.
    /// </remarks>
    public static class FocusNavigator
    {
        private static readonly HashSet<string> InertActions = new HashSet<string>
        {
            // Both bodies only start visual transform animations. See game-api.md section 29.
            "AnimationPulsePosition.Play",
            "AnimationPulseRotation.Play"
        };

        /// <summary>Screen-space radius within which two focusables count as the same control.</summary>
        /// <remarks>
        /// The measured pairs are pixel-identical, so this only has to beat floating-point noise.
        /// Kept tight deliberately: a loose value would merge genuinely adjacent controls, and the
        /// gender items sit only ~30px apart.
        /// </remarks>
        private const float CoincidentRadius = 2f;

        // CountFocusables runs once per visible group per frame. Reusing a per-thread buffer keeps
        // positional deduplication from creating a stream of short-lived lists in the game loop.
        [ThreadStatic]
        private static List<Focusable> _countBuffer;

        /// <summary>
        /// True when two retained focusables occupy the same navigation position.
        /// </summary>
        /// <remarks>
        /// Duplicate registrations are pixel-identical (game-api.md section 28), so this identifies
        /// the twins of a control the mod is focused on - needed by the announcer, which must treat
        /// a twin as "the user is here" rather than as a move somewhere else. Position alone is not
        /// proof of semantic identity: a parent RectTransform and its first child can share a pivot.
        /// Source-proven structural focusables are therefore removed by <see cref="ShouldSkip"/>
        /// before navigation or focus agreement uses this positional test.
        /// </remarks>
        public static bool AreCoincident(Focusable a, Focusable b)
        {
            if (a == null || b == null)
            {
                return false;
            }

            Vector2 delta = (Vector2)a.transform.position - (Vector2)b.transform.position;
            return delta.sqrMagnitude < CoincidentRadius * CoincidentRadius;
        }

        /// <summary>
        /// Picks the element a directional press should move to. Has NO side effects.
        /// </summary>
        /// <remarks>
        /// Deliberately does not apply the move. <c>FocusableGroup.SetFocused</c> writes IsFocused,
        /// the mod patches that setter to announce, and the announcer decides what to speak by
        /// comparing against <c>MenuNavigationHandler.Cursor</c> - so the cursor MUST already name
        /// the destination before the setter runs. When this method applied the move itself, the
        /// cursor was still the previous element while the announcement fired, every mod-driven move
        /// was judged a disagreement, and the main menu went completely silent after its first
        /// announcement while still navigating correctly.
        ///
        /// Computing here and applying in the caller keeps that ordering impossible to get wrong,
        /// and it is the cleaner split anyway: the navigator decides, the handler commits.
        /// </remarks>
        /// <param name="group">The group being navigated, for its direction-folding helper.</param>
        /// <param name="focusables">The group's registered focusables, read by the caller.</param>
        /// <param name="source">Where focus currently is, or null to seed a default.</param>
        /// <param name="direction">A four-way direction, screen axes, y up.</param>
        /// <returns>The element to focus, or null when nothing lies that way.</returns>
        public static Focusable FindTarget(
            FocusableGroup group, List<Focusable> focusables, Focusable source, Vector2 direction)
        {
            return FindTarget(group, focusables, source, direction, allowWrap: true);
        }

        /// <summary>
        /// Picks a directional target without wrapping to the far side of the panel.
        /// </summary>
        /// <remarks>
        /// Creator sub-panels contain several visually adjacent but semantically separate rows.
        /// Native wrap turns an Up boundary into a move to a lower child control (for example,
        /// Casual -> Show outfits), which makes repeated Up/Down oscillate. The creator's logical
        /// navigator uses this boundary-preserving form after applying its typed row rules. Other
        /// screens retain the game's wrapping behaviour through <see cref="FindTarget"/>.
        /// </remarks>
        internal static Focusable FindTargetWithoutWrap(
            FocusableGroup group, List<Focusable> focusables, Focusable source, Vector2 direction)
        {
            return FindTarget(group, focusables, source, direction, allowWrap: false);
        }

        private static Focusable FindTarget(
            FocusableGroup group,
            List<Focusable> focusables,
            Focusable source,
            Vector2 direction,
            bool allowWrap)
        {
            if (group == null || focusables == null || focusables.Count == 0)
            {
                return null;
            }

            List<Focusable> candidates = Deduplicate(focusables);
            if (candidates.Count == 0)
            {
                return null;
            }

            // Nothing focused yet: seed rather than move, mirroring the game's own
            // FocusableGroup.Navigate, which calls FocusDefaultFocusable and returns without moving.
            Focusable resolved = ResolveSource(candidates, source);
            if (resolved == null)
            {
                return TopLeftMost(candidates);
            }

            Focusable next = FindInDirection(candidates, resolved, direction, wrap: false);
            if (next == null && allowWrap)
            {
                next = FindInDirection(candidates, resolved, direction, wrap: true);
            }

            return ReferenceEquals(next, resolved) ? null : next;
        }

        /// <summary>
        /// Keeps one focusable per screen position, dropping inactive and duplicate registrations.
        /// </summary>
        /// <remarks>
        /// Order is preserved so navigation stays stable between frames. The FIRST registration at a
        /// position wins - an arbitrary but consistent choice, and the two are visually identical by
        /// definition, so the user cannot perceive which was kept. Only non-shoulder focusables are
        /// considered, matching the isShoulderButton:false navigation the mod drives.
        /// </remarks>
        private static List<Focusable> Deduplicate(List<Focusable> focusables)
        {
            return FilterDistinctReachable(focusables, includeShoulderButtons: false);
        }

        /// <summary>
        /// Returns the distinct controls a user can reach across both navigation tracks.
        /// </summary>
        /// <remarks>
        /// Panel and window totals must use the same position rule as directional navigation or a
        /// duplicated registry advertises controls the user can never visit. Shoulder controls are
        /// retained because the game's shoulder navigation reaches them independently; coincident
        /// entries are only merged within the same track.
        /// </remarks>
        /// <param name="focusables">The group's raw registration list.</param>
        /// <returns>Active, non-subsumed controls with one entry per position and track.</returns>
        internal static List<Focusable> GetDistinctReachable(List<Focusable> focusables)
        {
            return FilterDistinctReachable(focusables, includeShoulderButtons: true);
        }

        /// <summary>Counts the controls returned by <see cref="GetDistinctReachable"/>.</summary>
        internal static int CountDistinctReachable(List<Focusable> focusables)
        {
            if (focusables == null)
            {
                return 0;
            }

            List<Focusable> kept = _countBuffer ?? (_countBuffer = new List<Focusable>());
            kept.Clear();

            try
            {
                AddDistinctReachable(focusables, includeShoulderButtons: true, kept);
                return kept.Count;
            }
            finally
            {
                // Do not keep pooled/destroyed Unity objects alive between calls.
                kept.Clear();
            }
        }

        private static List<Focusable> FilterDistinctReachable(
            List<Focusable> focusables, bool includeShoulderButtons)
        {
            if (focusables == null)
            {
                return new List<Focusable>();
            }

            var kept = new List<Focusable>(focusables.Count);
            AddDistinctReachable(focusables, includeShoulderButtons, kept);
            return kept;
        }

        private static void AddDistinctReachable(
            List<Focusable> focusables, bool includeShoulderButtons, List<Focusable> kept)
        {
            foreach (Focusable f in focusables)
            {
                if (f == null || (!includeShoulderButtons && f.IsShoulderButton)
                    || !f.gameObject.activeInHierarchy || ShouldSkip(f))
                {
                    continue;
                }

                Vector2 pos = f.transform.position;
                bool duplicate = false;

                for (int i = 0; i < kept.Count; i++)
                {
                    // Shoulder and ordinary controls are separate tracks in FocusableGroup. If
                    // both deliberately share a position, both remain reachable through their own
                    // input and therefore both belong in the user-facing total.
                    if (kept[i].IsShoulderButton == f.IsShoulderButton
                        && ((Vector2)kept[i].transform.position - pos).sqrMagnitude
                        < CoincidentRadius * CoincidentRadius)
                    {
                        duplicate = true;
                        break;
                    }
                }

                if (!duplicate)
                {
                    kept.Add(f);
                }
            }
        }

        /// <summary>
        /// True when a focusable is presentation-only or its state/action is covered elsewhere.
        /// </summary>
        /// <remarks>
        /// Every reason here is deliberately narrow. A spacer/fill segment is source-proven
        /// presentation with no independent meaning. Child gauges are subsumed by a semantic parent
        /// stop; a thought-bubble item and its Sprite are a transient event presented through
        /// StateReportingHandler; and an actionless UIInteractionsList is pooling structure whose
        /// header context is presented by WindowHandler while its inner rows retain the actions.
        /// These remain separately justified rather than inferred from an object name.
        ///
        /// Measured in TEST 31: one personality stat costs six focusables — the
        /// `UIProfileTraitStatsItem` row, three `UIProfileTraitStatsItemJauge` segments, a
        /// `UIProfileTraitStatsLimitBar` (an entirely EMPTY class, so inert on the other test too)
        /// and a `Warning` icon. Four stats spend 24 focusables expressing four numbers, and twelve
        /// of those announced as `unlabeled Jauge(Clone)`.
        ///
        /// The test is STRUCTURAL — "is a descendant of a stat row" — not a list of type names.
        /// That is deliberate: `Warning`'s component type is prefab-serialized and unidentified, and
        /// a rule keyed on descent covers it without a guess about what it is. It also cannot
        /// over-reach, because it only ever applies below a `UIProfileTraitStatsItem`.
        ///
        /// HARD PREREQUISITE: the gauges are the only way to set a stat, so this skip is only safe
        /// while `ValueAdjustHandler` gives the row a working adjust. The two ship together. If the
        /// adjust is ever removed, this must be removed with it.
        /// </remarks>
        /// <param name="focusable">The candidate.</param>
        /// <returns>True when it should not be a navigation stop.</returns>
        public static bool ShouldSkip(Focusable focusable)
        {
            // External game API on pooled objects; a diagnostic-shaped test must never break
            // navigation, and returning false simply keeps the element reachable.
            try
            {
                if (SettingsHandler.ShouldSkipSurface(focusable)) return true;

                // A UILoadGameItem owns the actual load operation through
                // OnListItemClicked -> OnClicked. Its child Content button is wired to that exact
                // same OnClicked method and appeared as a second stop offset by its pivot. Preserve
                // nested edit/delete/autosave groups: only a same-group child whose entire click
                // event delegates to the outer row is a duplicate.
                if (SaveGameListHandler.IsDuplicateRowAction(focusable))
                {
                    return true;
                }

                // UINeedsItem.ReliefPanel is the visual hover target for the one active
                // "being relieved" indicator. UINeedsItem.Update consults it only to suppress the
                // row tooltip while that marker is hovered; it has no need action of its own. The
                // owning row already appends the same IsRelieving state. TEST 70 measured the lone
                // active marker at the Sleep row's Y coordinate, but spatial Right/Left navigation
                // could select it from every other need because it was the only horizontal
                // candidate. Match the public field by identity, and preserve it if a future prefab
                // gives that exact marker a real component or serialized click action.
                UINeedsItem needItem = focusable.GetComponentInParent<UINeedsItem>();
                if (needItem != null && ReferenceEquals(needItem.ReliefPanel, focusable)
                    && !HasDirectAction(focusable))
                {
                    return true;
                }

                var row = focusable.GetComponentInParent<UIProfileTraitStatsItem>();
                if (row != null && row.gameObject != focusable.gameObject)
                {
                    return true;
                }

                // User-approved after TEST 28. This is evidence-based rather than name-based: an
                // unlabeled focusable is skipped only when every serialized click target has a
                // source-read visual-only body and no runtime ButtonAction can add behaviour.
                if (HasOnlyInertVisualActions(focusable))
                {
                    return true;
                }

                // UIThoughtBubblesItem inherits UIListItemBase, so its inherited OnClick makes it
                // look actionable to HasDirectAction. Source and TEST 55 agree that it has no
                // OnListItemClicked implementation and ParentUIList is null: both the item and its
                // child Sprite are transient presentation, not controls. The model-backed bubble
                // event is announced by StateReportingHandler instead.
                UIThoughtBubblesItem bubble =
                    focusable.GetComponentInParent<UIThoughtBubblesItem>();
                if (bubble != null)
                {
                    return true;
                }

                // In the creator, each personality category is a pooled UIListItemBase only so
                // UIProfile can place its section. The Stats/Simple/Mix classes do not override
                // OnListItemClicked; their descendants own adjustment, trait selection and
                // dropdown/toggle actions. TEST 72 showed these shells as extra stops such as
                // "Vibe, Overjoyed" between Randomize Vibe and the real "Vibe: Overjoyed" item,
                // allowing spatial Left/Right to oscillate across unrelated sections. Remove only
                // the three source-proven shell types, only under UICharacterCreator, and preserve
                // any prefab which gains a direct action.
                if ((focusable is UIProfileTraitStats
                        || focusable is UIProfileTraitSimple
                        || focusable is UIProfileTraitMix)
                    && focusable.GetComponentInParent<UICharacterCreator>() != null
                    && !HasDirectAction(focusable))
                {
                    return true;
                }

                // UIInteractions owns an OUTER UIList of UIInteractionsList panels, and each panel
                // owns an INNER UIList of UIInteractionsListItem rows. UIList requires its pooled
                // model to derive from UIListItemBase, which makes the outer panel a Focusable even
                // though UIInteractionsList does not override OnListItemClicked. The real rows do:
                // a group row calls UIInteractions.AddList and an interaction row queues the action
                // (UIList.cs:33-58; UIInteractions.cs:251-271;
                // UIInteractionsListItem.cs:213-296). TEST 57 then measured the consequence of
                // treating the panel pivot as another row: the panel and first action shared the
                // same transform.position, so positional deduplication discarded the action.
                //
                // Keep the rule typed to this source-proven structural class. Other containers can
                // have their own operation and remain untouched. The runtime wiring guard is also
                // load-bearing: prefab UnityEvents are invisible to decompilation, so a future or
                // patched UIInteractionsList with a ButtonAction or persistent click is retained.
                if (focusable is UIInteractionsList && !HasDirectAction(focusable))
                {
                    return true;
                }

                // UIStoryCards owns an outer list of UIStoryCardOffer layout rows, and every offer
                // owns the actual clickable UIStoryCard rows. UIStoryCardOffer does not override
                // OnListItemClicked; TEST 71 recorded Enter firing on the aggregate offer label and
                // producing no state change. Its pivot also coincides with the middle real card, so
                // positional deduplication kept the inert container and discarded that choice.
                // Preserve any future prefab-wired action, exactly as for UIInteractionsList.
                if (focusable is UIStoryCardOffer && !HasDirectAction(focusable))
                {
                    return true;
                }

                // UIStoryboardsImageAndTextItem is presentation, not a choice. Its Init method
                // activates either Label or Image, and it adds no click behaviour. When the image
                // branch is active, the inactive prefab Label retains the sample caption "Text";
                // the generic inactive-text fallback exposed that caption as a phantom control at
                // tutorial completion in TEST 75. Keep real active authored text available (and
                // independently narrated at Init), but remove image-only/placeholder rows.
                UIStoryboardsImageAndTextItem storyboardItem =
                    focusable.GetComponentInParent<UIStoryboardsImageAndTextItem>();
                if (storyboardItem != null && !HasDirectAction(focusable)
                    && (storyboardItem.Label == null
                        || !storyboardItem.Label.gameObject.activeInHierarchy
                        || string.Equals(
                            storyboardItem.Label.text?.Trim(),
                            "Text",
                            StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }

                // UITownMapCharacterIcon is a FocusableGroup which visually owns one Para icon and
                // a child ParaButton wired to Clicked(). TEST 69 measured both the presentation
                // surface (the Para name) and that real button (unlabeled) as separate stops. The
                // class itself contains only thumbnail/position/tooltip presentation; Clicked is
                // the operation and lives on the child. Remove an actionless surface only while a
                // descendant still carries that exact serialized action, leaving one reachable
                // "Center camera on <name>" control rather than two overlapping meanings.
                if (IsTownMapCharacterPresentation(focusable))
                {
                    return true;
                }

                // Both Wants prefabs expose an extra exact UIListItemBase in addition to their
                // typed want rows. The base class has no operation beyond its ButtonClick event;
                // neither owner adds one in code, and a persistent/runtime component action keeps
                // the element below. Outside selection mode this exact, actionless base item is
                // therefore pooling/presentation structure, not another want.
                if (IsActionlessWantsBaseItem(focusable))
                {
                    return true;
                }

                // UIGoals exposes the actual checkbox through ShowCompletedLifeGoalsToggle, but
                // its prefab also registers the adjacent caption as a Focusable. TEST 68 measured
                // both as separate stops. Remove only a non-action sibling whose live caption is
                // exactly the semantic name of that source-proven toggle.
                if (IsDuplicateGoalsFilterLabel(focusable))
                {
                    return true;
                }

                // The classes below either contain presentation-only geometry or duplicate a
                // semantic child/parent stop. Keep any instance with serialized/runtime component
                // wiring: a prefab patch can therefore turn it into a real control without the mod
                // silently swallowing the new action.
                if (HasDirectAction(focusable))
                {
                    return false;
                }

                if (focusable is UINeedsItemFillIcon || focusable is UICharactersSpacer)
                {
                    return true;
                }

                return false;
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"ShouldSkip threw: {ex}");
                return false;
            }
        }

        private static bool IsTownMapCharacterPresentation(Focusable focusable)
        {
            UITownMapCharacterIcon icon =
                focusable.GetComponentInParent<UITownMapCharacterIcon>();
            if (icon == null || HasDirectAction(focusable))
            {
                return false;
            }

            ParaButton[] buttons = icon.GetComponentsInChildren<ParaButton>(includeInactive: false);
            foreach (ParaButton button in buttons)
            {
                if (button?.ButtonClick == null)
                {
                    continue;
                }

                int count = button.ButtonClick.GetPersistentEventCount();
                for (int i = 0; i < count; i++)
                {
                    if (ReferenceEquals(button.ButtonClick.GetPersistentTarget(i), icon)
                        && string.Equals(
                            button.ButtonClick.GetPersistentMethodName(i),
                            nameof(UITownMapCharacterIcon.Clicked),
                            StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsActionlessWantsBaseItem(Focusable focusable)
        {
            var item = focusable as UIListItemBase;
            if (item == null || item.GetType() != typeof(UIListItemBase)
                || HasDirectAction(focusable)
                || item.ParentUIList?.InSelectionMode == true)
            {
                return false;
            }

            return item.GetComponentInParent<UIWants2>() != null
                || item.GetComponentInParent<UIOfferedWants>() != null;
        }

        private static bool IsDuplicateGoalsFilterLabel(Focusable focusable)
        {
            UIGoals goals = focusable.GetComponentInParent<UIGoals>();
            ParaToggle toggle = goals?.ShowCompletedLifeGoalsToggle;
            if (toggle == null
                || ReferenceEquals(focusable.gameObject, toggle.gameObject)
                || focusable.transform.parent != toggle.transform.parent
                || HasDirectAction(focusable))
            {
                return false;
            }

            string label = UiLabelReader.GetLabel(focusable);
            return !string.IsNullOrWhiteSpace(label)
                && string.Equals(
                    label.Trim(),
                    Loc.Get("action_show_completed_life_goals"),
                    StringComparison.CurrentCultureIgnoreCase);
        }

        private static bool HasOnlyInertVisualActions(Focusable focusable)
        {
            if (focusable.GetComponent<ButtonAction>() != null
                || !string.IsNullOrEmpty(UiLabelReader.GetLabel(focusable)))
            {
                return false;
            }

            ParaButton button = focusable.GetComponent<ParaButton>();
            if (button?.ButtonClick == null)
            {
                return false;
            }

            int count = button.ButtonClick.GetPersistentEventCount();
            if (count == 0)
            {
                return false;
            }

            for (int i = 0; i < count; i++)
            {
                UnityEngine.Object target = button.ButtonClick.GetPersistentTarget(i);
                string method = button.ButtonClick.GetPersistentMethodName(i);
                if (target == null || string.IsNullOrEmpty(method)
                    || !IsInertAction(target.GetType(), method))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsInertAction(Type type, string method)
        {
            for (Type current = type;
                current != null && current != typeof(MonoBehaviour);
                current = current.BaseType)
            {
                if (InertActions.Contains(current.Name + "." + method))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasDirectAction(Focusable focusable)
        {
            if (focusable.GetComponent<ButtonAction>() != null)
            {
                return true;
            }

            ParaButton button = focusable as ParaButton;
            return button?.ButtonClick != null
                && button.ButtonClick.GetPersistentEventCount() > 0;
        }

        /// <summary>
        /// Maps the reported focus onto the deduplicated list.
        /// </summary>
        /// <remarks>
        /// The element the game reports as focused may be the twin that deduplication dropped. Since
        /// the twins share a position, the kept one at that position is the same control as far as
        /// the user is concerned, and navigating from it is correct.
        /// </remarks>
        private static Focusable ResolveSource(List<Focusable> candidates, Focusable source)
        {
            if (source == null)
            {
                return null;
            }

            Vector2 pos = source.transform.position;

            foreach (Focusable f in candidates)
            {
                if (ReferenceEquals(f, source))
                {
                    return f;
                }

                if (((Vector2)f.transform.position - pos).sqrMagnitude
                    < CoincidentRadius * CoincidentRadius)
                {
                    return f;
                }
            }

            return null;
        }

        /// <summary>
        /// The game's own direction search, over a list with no coincident entries.
        /// </summary>
        /// <remarks>
        /// Scoring is copied from FocusableGroup.GetFocusableInDirection so behaviour matches the
        /// game on healthy screens: reject anything outside Settings.UI.NavigationMaxAngle, then
        /// score by distance weighted by how well the angle matches, and take the lowest. The tuning
        /// values are READ from the game's settings rather than hardcoded, so they follow patches.
        ///
        /// The zero-distance guard is the one addition. It is what the game lacks.
        /// </remarks>
        private static Focusable FindInDirection(
            List<Focusable> candidates, Focusable source, Vector2 direction, bool wrap)
        {
            Vector2 origin = source.transform.position;

            if (wrap)
            {
                // Same trick the game uses: shove the origin past the far edge so the nearest match
                // is the element that visually wraps around.
                float offset = Settings.Instance.UI.WrapExtraPositionOffset;
                Bounds bounds = Extent(candidates);

                if (direction.x > 0f) origin.x = bounds.min.x - offset;
                else if (direction.x < 0f) origin.x = bounds.max.x + offset;
                else if (direction.y > 0f) origin.y = bounds.min.y - offset;
                else if (direction.y < 0f) origin.y = bounds.max.y + offset;
            }

            float maxAngle = Settings.Instance.UI.NavigationMaxAngle;
            float minCoefficient = Settings.Instance.UI.NavigationMinMatchingAngleCoeficient;

            float best = float.MaxValue;
            Focusable result = null;

            foreach (Focusable f in candidates)
            {
                if (ReferenceEquals(f, source))
                {
                    continue;
                }

                Vector2 delta = (Vector2)f.transform.position - origin;

                // THE FIX. Vector2.zero.normalized is Vector2.zero, and Vector2.Angle(dir, zero)
                // returns 0, so a coincident candidate would pass the angle gate for every direction
                // and score 0 - beating every real candidate. Deduplication should already have
                // removed these; this is the belt to that braces.
                if (delta.sqrMagnitude < CoincidentRadius * CoincidentRadius)
                {
                    continue;
                }

                float angle = Vector2.Angle(direction, delta.normalized);
                if (angle >= maxAngle)
                {
                    continue;
                }

                float coefficient = Mathf.Lerp(minCoefficient, 1f, Mathf.Max(0.01f, angle / 89.99f));
                float score = delta.magnitude * coefficient;

                if (score < best)
                {
                    best = score;
                    result = f;
                }
            }

            return result;
        }

        /// <summary>Bounding box of the candidates, for wrap-around.</summary>
        private static Bounds Extent(List<Focusable> candidates)
        {
            var bounds = new Bounds(candidates[0].transform.position, Vector3.zero);

            for (int i = 1; i < candidates.Count; i++)
            {
                bounds.Encapsulate(candidates[i].transform.position);
            }

            return bounds;
        }

        /// <summary>
        /// The element nearest the top-left of the group, matching the game's default-focus rule.
        /// </summary>
        /// <remarks>
        /// FocusableGroup.SetDefaultFocused prefers a focusable flagged IsFirstFocused and otherwise
        /// takes the one closest to (leftmost x, topmost y). IsFirstFocused is serialized in the
        /// prefab and cannot be read from decompiled source (lesson 10), so only the geometric half
        /// is reproduced here - it is a seed, and the first arrow press corrects it.
        /// </remarks>
        private static Focusable TopLeftMost(List<Focusable> candidates)
        {
            Bounds bounds = Extent(candidates);
            var corner = new Vector2(bounds.min.x, bounds.max.y);

            Focusable best = null;
            float bestDistance = float.MaxValue;

            foreach (Focusable f in candidates)
            {
                float d = ((Vector2)f.transform.position - corner).sqrMagnitude;
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = f;
                }
            }

            return best;
        }

        /// <summary>
        /// Reads the four-way direction the user pressed this frame, or zero when none.
        /// </summary>
        /// <remarks>
        /// Mirrors FocusableGroup.NavigateMenuArrows exactly, including its priority order, so the
        /// mod reacts to the same inputs on the same frames the game would have. The analogue stick
        /// is folded to four directions by the game's own public GetFourDirectionsInputVector2.
        /// </remarks>
        /// <param name="group">Any group, used only for the game's direction-folding helper.</param>
        /// <param name="hybridPlayer">The player whose input to read.</param>
        /// <returns>A unit direction in screen axes, or <c>Vector2.zero</c>.</returns>
        public static Vector2 ReadDirection(FocusableGroup group, HybridPlayer hybridPlayer)
        {
            if (hybridPlayer.ButtonDPadLeft.IntervalPressed) return new Vector2(-1f, 0f);
            if (hybridPlayer.ButtonDPadRight.IntervalPressed) return new Vector2(1f, 0f);
            if (hybridPlayer.ButtonDPadDown.IntervalPressed) return new Vector2(0f, -1f);
            if (hybridPlayer.ButtonDPadUp.IntervalPressed) return new Vector2(0f, 1f);

            if (hybridPlayer.CameraMovement.IntervalPressed
                && hybridPlayer.CameraMovement.Value != Vector2.zero)
            {
                return group.GetFourDirectionsInputVector2(hybridPlayer.CameraMovement.Value.normalized);
            }

            return Vector2.zero;
        }
    }
}
