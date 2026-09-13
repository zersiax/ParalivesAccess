using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using ParalivesAccess.Handlers;
using UnityEngine;

namespace ParalivesAccess.Probes
{
    /// <summary>
    /// Debug-gated diagnostic that answers one question: when a character-creator category is
    /// opened, where does its content live and why has the mod never seen it? Reads and logs only.
    /// </summary>
    /// <remarks>
    /// WHY THIS EXISTS
    ///
    /// TEST 28 accounted for all 33 focusables in the UICharacterCreator group and not one of them
    /// belongs to the creator's actual content. The words ContextualList, Equipment, Preset, Swatch
    /// and Decal appear nowhere in a 620-line log, and the panel count in the creator was 4 of 4.
    ///
    /// Source says the content is real and reachable in principle:
    ///  - UICharacterCreator.UICharacterCreatorContextualList is a public field on the window.
    ///  - UICharacterCreatorContextualList : UIContainer : FocusableGroup.
    ///  - Everything it builds - UICharacterCreatorContextualListHeader, ...Equipments, ...Presets,
    ///    UICharacterCreatorGlobalSwatches, UICharacterCreatorDecalPositions, UIEquipmentItem,
    ///    UIPresetItem - is a UIListItemBase, so ParaToggle -> ParaButton -> Focusable.
    ///  - MenuNavigationHandler.BuildCandidates accepts every active FocusableGroup under a visible
    ///    window that holds at least one Focusable.
    ///
    /// So it should have been listed as a panel and it was not. Source cannot separate the two
    /// candidate explanations, so this probe measures both:
    ///
    ///  1. The list was empty or hidden. RefreshListContent sets
    ///     IsVisible = CharacterCreatorCurrentTagGUIDs.Length != 0, and BuildCandidates passes
    ///     includeInactive: false.
    ///  2. It is populated but its Focusables register somewhere else. Focusable.OnEnable binds to
    ///     GetComponentInParent&lt;FocusableGroup&gt;() - the NEAREST ancestor - and which object that
    ///     is depends on the prefab hierarchy, which decompilation cannot show (lesson 10).
    ///
    /// The decisive measurement is the registration map below: Focusable._focusableGroup is the
    /// group an element actually joined, so it is read directly instead of being inferred from the
    /// hierarchy.
    ///
    /// SIDE-EFFECT HAZARD, deliberately avoided. The obvious change signal,
    /// Player.GetCurrentCharacterCreatorTagsHash(), CLEARS Player.ResetCreatorTagHash as a side
    /// effect (Player.cs:529-534). Calling it here would consume the reset flag that
    /// UICharacterCreatorContextualList.RefreshListContent depends on and could suppress the game's
    /// own rebuild. The signature below is built from the same fields by pure reads instead.
    ///
    /// Reflection is used for the game's private focusable list and protected group back-reference.
    /// That is acceptable here: this is diagnostic mod-internal code, and the try-catch around
    /// reflective and external calls keeps diagnostics from interrupting the game.
    /// </remarks>
    public static class CharacterCreatorContentProbe
    {
        /// <summary>Set true to re-enable this probe. Off by default since TEST 29 answered it.</summary>
        /// <remarks>
        /// Kept rather than deleted: it is the only tool that can answer "what is actually in this
        /// category and which group owns it", and the creator has several category shapes still
        /// unexercised (global swatches, presets/sliders, limb selection). Off by default because it
        /// costs eight multi-line dumps per session and its original question - game-api.md section
        /// 31 - is closed.
        /// </remarks>
        public static bool Enabled { get; set; }

        /// <summary>
        /// Frames to wait after the tag path changes before dumping.
        /// </summary>
        /// <remarks>
        /// The mod ticks from a postfix on SystemManager.Update, so it observes an arbitrary point
        /// inside Unity's Update phase. The content is not settled until two later stages have run:
        /// UICharacterCreatorContextualList.Update rebuilds the UIList, and UIContainer.LateUpdate
        /// applies Content.SetActive - which is what fires Focusable.OnEnable and therefore what
        /// performs the registration this probe is measuring. Sampling on the change frame would
        /// reliably report an empty list and answer the wrong question.
        /// </remarks>
        private const int SettleFrames = 3;

        /// <summary>Maximum dumps per session, so browsing categories cannot flood the log.</summary>
        private const int MaxDumps = 8;

        /// <summary>Maximum focusables described individually; totals are always exact.</summary>
        private const int MaxDetailLines = 24;

        private static string _lastSignature;
        private static int _framesUntilDump = -1;
        private static int _dumpCount;
        private static bool _reportedCap;

        private static FieldInfo _focusablesField;
        private static FieldInfo _focusableGroupField;
        private static bool _reflectionResolved;

        /// <summary>
        /// Called once per frame from the game loop hook. Dumps a settled snapshot whenever the
        /// open category path changes.
        /// </summary>
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

                // Not in the creator: reset so re-entering dumps a fresh baseline.
                if (creator == null || !creator.IsVisible)
                {
                    _lastSignature = null;
                    _framesUntilDump = -1;
                    return;
                }

                string signature = BuildSignature(player);
                if (signature != _lastSignature)
                {
                    _lastSignature = signature;
                    _framesUntilDump = SettleFrames;
                    return;
                }

                if (_framesUntilDump < 0)
                {
                    return;
                }

                if (_framesUntilDump-- > 0)
                {
                    return;
                }

                if (_dumpCount >= MaxDumps)
                {
                    if (!_reportedCap)
                    {
                        _reportedCap = true;
                        Plugin.Log.LogInfo(
                            $"[CCProbe] Dump cap of {MaxDumps} reached; further category changes are "
                          + "not logged. Restart the game to capture more.");
                    }

                    return;
                }

                _dumpCount++;
                Dump(player, creator);
            }
            catch (Exception ex)
            {
                // This runs inside the game's own loop; never let it escape.
                Enabled = false;
                Plugin.Log.LogError("CharacterCreatorContentProbe disabled after an exception: " + ex);
            }
        }

        /// <summary>
        /// Builds a pure-read change signal from the open tag path and the selected character.
        /// </summary>
        /// <remarks>
        /// These are the same fields Player.GetCurrentCharacterCreatorTagsHash() reads, but that
        /// method clears ResetCreatorTagHash and must not be called from a diagnostic.
        /// </remarks>
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
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append(guids[i]);
            }
        }

        private static void Dump(Player player, UICharacterCreator creator)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[CCProbe] ===== dump {_dumpCount} of {MaxDumps}, frame {Time.frameCount} =====");

            DescribeTagPath(sb, player);
            DescribeCreator(sb, creator);

            UICharacterCreatorContextualList list = creator.UICharacterCreatorContextualList;
            if (list == null)
            {
                sb.AppendLine(
                    "    contextualList: THE FIELD IS NULL. UICharacterCreator.UICharacterCreatorContextualList "
                  + "is unassigned on this instance, so the content has no object at all.");
                Plugin.Log.LogInfo(sb.ToString());
                return;
            }

            DescribeContextualList(sb, list);
            DescribeAncestry(sb, list);
            DescribeItems(sb, list);
            DescribeRegistrations(sb, list);
            DescribeOwnFocusables(sb, list);

            Plugin.Log.LogInfo(sb.ToString());
        }

        /// <summary>
        /// Prints the 4-level tag path and the current tag set, named rather than as raw GUIDs.
        /// </summary>
        /// <remarks>
        /// Player.CharacterCreatorTags is a ulong[4] holding the currently-OPEN path, one entry per
        /// tree level, and CharacterCreatorCurrentTagGUIDs is what RefreshListContent actually
        /// builds from - it is the empty/non-empty test behind IsVisible. Printing both separates
        /// "the user never opened anything" from "a category is open but produced no content".
        /// </remarks>
        private static void DescribeTagPath(StringBuilder sb, Player player)
        {
            Setting.CharacterCreatorTags settings = null;

            // External game API: a settings lookup must never break the dump.
            try
            {
                settings = Settings.Get<Setting.CharacterCreatorTags>();
            }
            catch (Exception ex)
            {
                sb.AppendLine($"    tagSettings: unreadable ({ex.GetType().Name})");
            }

            sb.AppendLine($"    CharacterCreatorTags (open path, one per level): {NameGuids(settings, player.CharacterCreatorTags)}");
            sb.AppendLine($"    CharacterCreatorCurrentTagGUIDs (drives the list): {NameGuids(settings, player.CharacterCreatorCurrentTagGUIDs)}");
            sb.AppendLine($"    selectedCharacterGUID={player.GetSelectedCharacterGUID()} state={player.State}");
        }

        private static string NameGuids(Setting.CharacterCreatorTags settings, ulong[] guids)
        {
            if (guids == null)
            {
                return "(null)";
            }

            if (guids.Length == 0)
            {
                return "(empty)";
            }

            var sb = new StringBuilder();
            for (int i = 0; i < guids.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(i).Append(':');

                if (guids[i] == 0UL)
                {
                    sb.Append("(none)");
                    continue;
                }

                sb.Append(NameTag(settings, guids[i]));
            }

            return sb.ToString();
        }

        private static string NameTag(Setting.CharacterCreatorTags settings, ulong guid)
        {
            if (settings == null)
            {
                return guid.ToString();
            }

            // External game API: an unknown GUID must degrade to the number, never throw.
            try
            {
                Setting.CharacterCreatorTag tag = settings.GetTagByGUID(guid);
                if (tag == null)
                {
                    return guid + "(unknown tag)";
                }

                int children = tag.ChildrenTagsInCharacterCreatorUI != null
                    ? tag.ChildrenTagsInCharacterCreatorUI.Length
                    : 0;

                return $"'{tag.DisplayName}'({guid}, children={children}, showChildItems={tag.ShowChildCategoryItems}, "
                     + $"isGlobal={tag.IsGlobal}, showLimbs={tag.ShowLimbSelection})";
            }
            catch (Exception ex)
            {
                return $"{guid}(unreadable: {ex.GetType().Name})";
            }
        }

        private static void DescribeCreator(StringBuilder sb, UICharacterCreator creator)
        {
            sb.AppendLine(
                $"    window '{creator.name}' isVisible={creator.IsVisible} "
              + $"activeInHierarchy={creator.gameObject.activeInHierarchy} "
              + $"focusables={CountFocusables(creator)} "
              + $"sibling={creator.transform.GetSiblingIndex()}");
        }

        /// <summary>
        /// Reports the contextual list's own visibility and focusable count.
        /// </summary>
        /// <remarks>
        /// UIContainer.IsVisible does NOT deactivate the container's own GameObject - it toggles
        /// Content, which UIContainer resolves as transform.GetChild(0) and applies in LateUpdate.
        /// So the container stays visible to GetComponentsInChildren&lt;FocusableGroup&gt;(false) while
        /// its children go inactive and unregister. Both states are printed because they answer
        /// different halves of the question: an active container with zero focusables means
        /// BuildCandidates saw it and skipped it on the count test, while an inactive container
        /// means BuildCandidates never saw it at all.
        /// </remarks>
        private static void DescribeContextualList(StringBuilder sb, UICharacterCreatorContextualList list)
        {
            bool isGroup = list is FocusableGroup;

            sb.AppendLine(
                $"    contextualList '{list.name}' type={list.GetType().Name} isFocusableGroup={isGroup}");
            sb.AppendLine(
                $"      isVisible={list.IsVisible} activeSelf={list.gameObject.activeSelf} "
              + $"activeInHierarchy={list.gameObject.activeInHierarchy} "
              + $"focusables={(isGroup ? CountFocusables(list).ToString() : "n/a")}");

            // External game API: Content lazily resolves transform.GetChild(0) and throws if the
            // prefab has no children.
            try
            {
                GameObject content = list.Content;
                sb.AppendLine(
                    content == null
                        ? "      content=(null)"
                        : $"      content='{content.name}' activeSelf={content.activeSelf} "
                        + $"activeInHierarchy={content.activeInHierarchy}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"      content=(unreadable: {ex.GetType().Name})");
            }

            sb.AppendLine(
                $"      equipmentGrid={Describe(list.EquipmentGrid)} "
              + $"globalSwatches={Describe(list.ActiveCharacterCreatorGlobalSwatches)} "
              + $"decalPositions={Describe(list.UIDecalPositions)}");

            bool inPanels = IsInPanelList(list);
            sb.AppendLine(
                $"      SEEN BY BuildCandidates={inPanels} "
              + $"(mod panel list holds {MenuNavigationHandler.Panels.Count} groups this frame)");
        }

        private static string Describe(Component component)
        {
            if (component == null)
            {
                return "(null)";
            }

            return $"'{component.name}'(active={component.gameObject.activeInHierarchy})";
        }

        private static bool IsInPanelList(FocusableGroup group)
        {
            System.Collections.Generic.IList<FocusableGroup> panels = MenuNavigationHandler.Panels;
            for (int i = 0; i < panels.Count; i++)
            {
                if (ReferenceEquals(panels[i], group))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Walks from the contextual list to the scene root, naming every FocusableGroup and
        /// UIWindow on the way.
        /// </summary>
        /// <remarks>
        /// This is the fact decompilation cannot supply. BuildCandidates enumerates
        /// window.GetComponentsInChildren&lt;FocusableGroup&gt;(false) over the visible windows, so the
        /// contextual list is only reachable if it is a DESCENDANT of one of them. The public field
        /// UICharacterCreator.UICharacterCreatorContextualList proves a reference exists; it proves
        /// nothing about where the object sits in the hierarchy.
        ///
        /// The same walk also shows what Focusable.OnEnable would find: its
        /// GetComponentInParent&lt;FocusableGroup&gt;() stops at the first group above the element.
        /// </remarks>
        private static void DescribeAncestry(StringBuilder sb, UICharacterCreatorContextualList list)
        {
            sb.AppendLine("      ancestry (nearest first):");

            Transform t = list.transform.parent;
            int depth = 0;

            while (t != null && depth < 12)
            {
                var group = t.GetComponent<FocusableGroup>();
                var window = t.GetComponent<UIWindow>();

                sb.AppendLine(
                    $"        {depth}: '{t.name}' active={t.gameObject.activeInHierarchy}"
                  + (group != null ? $" [FocusableGroup {group.GetType().Name}, focusables={CountFocusables(group)}]" : string.Empty)
                  + (window != null ? $" [UIWindow isVisible={window.IsVisible}]" : string.Empty));

                t = t.parent;
                depth++;
            }

            if (t != null)
            {
                sb.AppendLine("        ... (chain truncated at 12)");
            }
        }

        /// <summary>
        /// Lists what the contextual list's UIList currently holds, including nested lists.
        /// </summary>
        /// <remarks>
        /// RefreshListContent builds a two-level structure: the outer UIList holds headers, the
        /// global swatch block, decal positions and the grid containers, and each grid container
        /// owns its OWN UIList of the actual items (UICharacterCreatorContextualList.cs:182, :231).
        /// The presets container is only a local variable in that method, so nested lists are found
        /// by reflecting over each item's public UIList fields rather than by naming types - that
        /// covers Equipments, Presets, DecalPositions.ListSections and GlobalSwatches.SwatchList
        /// without assuming which shapes are present.
        ///
        /// CurrentItems is a true total, not a pool size: DeactivateAndPoolItemAtIndex removes from
        /// it immediately (game-api.md section 15).
        /// </remarks>
        private static void DescribeItems(StringBuilder sb, UICharacterCreatorContextualList list)
        {
            UIList uiList = list.UIList;
            if (uiList == null)
            {
                sb.AppendLine("      UIList field is NULL - nothing can have been built.");
                return;
            }

            List<UIListItemBase> items = uiList.CurrentItems;
            int count = items != null ? items.Count : -1;
            sb.AppendLine($"      UIList.CurrentItems={count} inSelectionMode={uiList.InSelectionMode}");

            if (items == null)
            {
                return;
            }

            for (int i = 0; i < items.Count && i < MaxDetailLines; i++)
            {
                UIListItemBase item = items[i];
                if (item == null)
                {
                    sb.AppendLine($"        {i}: (null)");
                    continue;
                }

                sb.AppendLine(
                    $"        {i}: {item.GetType().Name} '{item.name}' "
                  + $"active={item.gameObject.activeInHierarchy} "
                  + $"registeredWith={NameGroup(GetRegisteredGroup(item))}"
                  + DescribeNestedLists(item));
            }

            if (items.Count > MaxDetailLines)
            {
                sb.AppendLine($"        ... {items.Count - MaxDetailLines} more");
            }
        }

        /// <summary>
        /// Reports every public UIList field on a list item, with its live item count.
        /// </summary>
        private static string DescribeNestedLists(UIListItemBase item)
        {
            var sb = new StringBuilder();

            // Mod-internal reflection over game types: a renamed field must degrade to no detail.
            try
            {
                FieldInfo[] fields = item.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
                foreach (FieldInfo field in fields)
                {
                    if (!typeof(UIList).IsAssignableFrom(field.FieldType))
                    {
                        continue;
                    }

                    var nested = field.GetValue(item) as UIList;
                    sb.Append(" | ").Append(field.Name).Append('=');
                    sb.Append(nested == null
                        ? "(null)"
                        : $"{nested.CurrentItems.Count} items");
                }
            }
            catch (Exception ex)
            {
                sb.Append(" | nested lists unreadable: ").Append(ex.GetType().Name);
            }

            return sb.ToString();
        }

        /// <summary>
        /// THE DECISIVE MEASUREMENT: which FocusableGroup each Focusable under the contextual list
        /// actually joined.
        /// </summary>
        /// <remarks>
        /// Focusable.OnEnable stores its group in the protected field _focusableGroup, so that field
        /// is the ground truth for "where did this register" - strictly better than re-deriving it
        /// from the hierarchy, because it also reflects the state at the moment OnEnable ran rather
        /// than the state now.
        ///
        /// Inactive children are included on purpose: an element that unregistered when its
        /// GameObject was disabled still holds the group it last belonged to, which distinguishes
        /// "never registered anywhere" from "registered, then pooled out".
        /// </remarks>
        private static void DescribeRegistrations(StringBuilder sb, UICharacterCreatorContextualList list)
        {
            ResolveReflection();

            if (_focusableGroupField == null)
            {
                sb.AppendLine("      registration map: unavailable (Focusable._focusableGroup not found).");
                return;
            }

            Focusable[] focusables = list.GetComponentsInChildren<Focusable>(includeInactive: true);
            sb.AppendLine($"      Focusables under the contextual list (incl. inactive): {focusables.Length}");

            var groups = new List<FocusableGroup>();
            var totals = new List<int>();
            var actives = new List<int>();
            int noGroup = 0;

            foreach (Focusable focusable in focusables)
            {
                if (focusable == null)
                {
                    continue;
                }

                FocusableGroup group = GetRegisteredGroup(focusable);
                if (group == null)
                {
                    noGroup++;
                    continue;
                }

                int index = -1;
                for (int i = 0; i < groups.Count; i++)
                {
                    if (ReferenceEquals(groups[i], group))
                    {
                        index = i;
                        break;
                    }
                }

                if (index < 0)
                {
                    groups.Add(group);
                    totals.Add(0);
                    actives.Add(0);
                    index = groups.Count - 1;
                }

                totals[index] = totals[index] + 1;
                if (focusable.gameObject.activeInHierarchy)
                {
                    actives[index] = actives[index] + 1;
                }
            }

            if (noGroup > 0)
            {
                sb.AppendLine($"        {noGroup} focusable(s) hold no group reference at all.");
            }

            for (int i = 0; i < groups.Count; i++)
            {
                FocusableGroup group = groups[i];
                sb.AppendLine(
                    $"        {totals[i]} ({actives[i]} active) -> {NameGroup(group)} "
                  + $"groupFocusables={CountFocusables(group)} "
                  + $"groupActive={group.gameObject.activeInHierarchy} "
                  + $"inModPanelList={IsInPanelList(group)}");
            }

            if (groups.Count == 0)
            {
                sb.AppendLine("        (no focusable under the contextual list has ever registered)");
            }
        }

        /// <summary>
        /// Dumps the contextual list group's own registered focusables, with the labels the mod
        /// would speak for them.
        /// </summary>
        /// <remarks>
        /// Same shape as MenuNavigationHandler's [NavProbe/inventory] block, so the two can be read
        /// side by side. The labels matter as much as the count: they say in advance whether the
        /// equipment items and sliders would announce anything usable once they are reachable, which
        /// is the next question after this one.
        /// </remarks>
        private static void DescribeOwnFocusables(StringBuilder sb, UICharacterCreatorContextualList list)
        {
            List<Focusable> registered = GetFocusables(list);
            if (registered == null)
            {
                sb.AppendLine("      own focusable list: unreadable.");
                return;
            }

            sb.AppendLine($"      contextual list group holds {registered.Count} registered focusables:");

            for (int i = 0; i < registered.Count && i < MaxDetailLines; i++)
            {
                Focusable focusable = registered[i];
                if (focusable == null)
                {
                    sb.AppendLine($"        {i}: (null)");
                    continue;
                }

                Vector3 pos = focusable.transform.position;
                sb.AppendLine(
                    $"        {i}: {focusable.GetType().Name} '{focusable.name}'@({pos.x:0},{pos.y:0}) "
                  + $"active={focusable.gameObject.activeInHierarchy} focused={focusable.IsFocused} "
                  + $"label='{UiLabelReader.GetLabelOrFallback(focusable)}'");
            }

            if (registered.Count > MaxDetailLines)
            {
                sb.AppendLine($"        ... {registered.Count - MaxDetailLines} more");
            }
        }

        private static FocusableGroup GetRegisteredGroup(Focusable focusable)
        {
            ResolveReflection();

            if (_focusableGroupField == null || focusable == null)
            {
                return null;
            }

            // Reflection on a game type: a renamed field must degrade to "unknown", never throw.
            try
            {
                return _focusableGroupField.GetValue(focusable) as FocusableGroup;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string NameGroup(FocusableGroup group)
        {
            return group == null ? "(none)" : $"{group.GetType().Name} '{group.name}'";
        }

        private static List<Focusable> GetFocusables(FocusableGroup group)
        {
            ResolveReflection();
            return _focusablesField?.GetValue(group) as List<Focusable>;
        }

        private static int CountFocusables(FocusableGroup group)
        {
            List<Focusable> list = GetFocusables(group);
            return list?.Count ?? -1;
        }

        private static void ResolveReflection()
        {
            if (_reflectionResolved)
            {
                return;
            }

            _reflectionResolved = true;

            _focusablesField = AccessTools.Field(typeof(FocusableGroup), "_focusables");
            _focusableGroupField = AccessTools.Field(typeof(Focusable), "_focusableGroup");

            if (_focusablesField == null || _focusableGroupField == null)
            {
                Plugin.Log.LogWarning(
                    "CharacterCreatorContentProbe could not resolve the game's private focus fields "
                  + $"(_focusables={_focusablesField != null}, _focusableGroup={_focusableGroupField != null}). "
                  + "The dump will be incomplete - re-check docs/game-api.md section 4.");
            }
        }
    }
}
