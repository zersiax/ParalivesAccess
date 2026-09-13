using System;
using System.Collections.Generic;
using ParalivesAccess.Speech;
using Setting;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ParalivesAccess.Handlers
{
    /// <summary>
    /// Explores the currently loaded lot from the free camera, frames persistent world targets,
    /// and hands actionable objects and Paras to the game's native interaction menu.
    /// </summary>
    /// <remarks>
    /// The hierarchy is floor -&gt; unnamed room/outdoor area -&gt; objects and Paras. Complementary
    /// name, action and essential-facility finders operate on the same persistent instance graph.
    /// No operation in this handler moves a Para; the selected Para is consulted only when the
    /// native interaction system needs an actor.
    /// </remarks>
    public static class LotObjectHandler
    {
        private const int DirectListThreshold = 24;

        /// <summary>Set false to disable the current-lot explorer.</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>Configurable key that opens and closes the current-lot explorer.</summary>
        public static Key OpenKey { get; set; } = Key.F7;

        /// <summary>True while this handler owns arrows, Backspace, and Enter.</summary>
        public static bool OwnsInput => _panel != null;

        private static VirtualPanel _panel;
        private static readonly Stack<ViewState> _history = new Stack<ViewState>();
        private static ExplorerModel _model;
        private static ulong _lotGuid;
        private static string _lotSource;
        private static string _tutorialPhaseAtOpen;
        private static Func<bool> _panelContextAvailable;
        private static bool _isForeignLot;
        private static bool _showAllForeignLotObjects;
        private static bool _enterPressed;
        private static bool _enterWithShift;
        private static VirtualPanel.IEntry _enterEntry;
        private static bool _cameraBaselineCaptured;
        private static bool _cameraWasFrozen;

        /// <summary>
        /// Prefix-side camera guard. The explorer's arrows must never also pan the free camera.
        /// </summary>
        public static void BeforeGameSystems()
        {
            if (!Enabled || !OwnsInput || PlayerManager.Instance == null)
            {
                return;
            }

            HybridPlayer hybridPlayer = PlayerManager.Instance.HybridPlayer1;
            if (hybridPlayer?.Player != null)
            {
                hybridPlayer.Player.FreezeCameraMovement = true;
            }
        }

        /// <summary>Called once per frame from the game-loop postfix.</summary>
        public static void Tick()
        {
            if (!Enabled)
            {
                return;
            }

            try
            {
                HybridPlayer hybridPlayer = PlayerManager.Instance?.HybridPlayer1;
                Keyboard keyboard = Keyboard.current;
                if (hybridPlayer?.Player == null || keyboard == null)
                {
                    return;
                }

                if (!OwnsInput && LotBuildInputOwnership.OwnsInput)
                {
                    return;
                }

                if (hybridPlayer.HasInputFieldFocused)
                {
                    if (_panel != null)
                    {
                        Close(announce: true);
                    }
                    return;
                }

                if (ModInputKey.WasPressed(keyboard, OpenKey))
                {
                    if (_panel != null)
                    {
                        Close(announce: true);
                    }
                    else
                    {
                        Open(hybridPlayer);
                    }
                    return;
                }

                if (_panel == null)
                {
                    return;
                }

                // A tutorial transition can replace the available world targets in one frame.
                // Never let a marker/household/Storyteller list from the previous lesson survive.
                if (!string.Equals(
                    _tutorialPhaseAtOpen,
                    TutorialHandler.CurrentPhaseId,
                    StringComparison.Ordinal))
                {
                    Close(announce: false);
                    return;
                }

                if (!IsContextStillValid(hybridPlayer))
                {
                    Close(announce: true);
                    return;
                }

                // An object or Para command page is meaningful only while that exact live target
                // remains in the explorer model. Its individual entries all becoming unavailable
                // used to leave a panel in which every arrow and Enter press was silent.
                if (_panelContextAvailable != null && !_panelContextAvailable())
                {
                    ReturnFromUnavailableTarget();
                    return;
                }

                if (keyboard.backspaceKey.wasPressedThisFrame)
                {
                    GoBack();
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

                HandleEnter(keyboard);
            }
            catch (Exception ex)
            {
                Enabled = false;
                Close(announce: false);
                Plugin.Log.LogError("LotObjectHandler disabled after an exception: " + ex);
                ScreenReader.Speak(Loc.Get("explorer_handler_failed"));
            }
        }

        private static void Open(HybridPlayer hybridPlayer)
        {
            string conflict;
            if (!ModInputKey.IsAvailable(hybridPlayer, OpenKey, out conflict))
            {
                Speak(conflict);
                return;
            }

            Player player = hybridPlayer.Player;
            if (player.State != GameStates.LiveMode)
            {
                Speak(Loc.Get("explorer_live_required"));
                return;
            }
            if (player.PlayerActionBlockedByUIWindow)
            {
                Speak(Loc.Get("explorer_ui_blocked"));
                return;
            }
            if (IsTownMapBlockingExplorer(hybridPlayer))
            {
                Speak(Loc.Get("explorer_town_map"));
                return;
            }
            string source;
            ulong lotGuid = ResolveCurrentLot(player, out source);
            AssetLot lot = GetLoadedLot(lotGuid);
            if (lotGuid == 0UL || lot == null)
            {
                Speak(Loc.Get("explorer_lot_required"));
                return;
            }

            ExplorerModel model = ExplorerModel.Build(lotGuid, player);
            if (model == null)
            {
                Speak(Loc.Get("explorer_lot_required"));
                return;
            }

            CaptureCameraBaseline(hybridPlayer);
            _lotGuid = lotGuid;
            _lotSource = source;
            _tutorialPhaseAtOpen = TutorialHandler.CurrentPhaseId;
            _isForeignLot = !player.IsInIntro && !IsOwnedLot(lotGuid);
            _showAllForeignLotObjects = false;
            _model = model;
            _history.Clear();
            _enterPressed = false;
            LotBuildInputOwnership.HoldThroughCurrentFrame();

            Plugin.Log.LogInfo(
                $"[Explorer] open lot={lotGuid}, source={source}, floors={model.Floors.Count}, "
              + $"rooms={model.RoomCount}, objects={model.Objects.Count}, "
              + $"interactable={model.CountObjects(FindMode.Interactable)}, "
              + "facilities=deferred, "
              + $"paras={model.Paras.Count}, "
              + $"foreign={_isForeignLot}, view="
              + $"{(UseActionOnlyView ? "actionable-only" : "all")}, "
              + $"ordinaryVisualFallback={model.OrdinaryVisualParaCount}, "
              + $"introVisualFallback={model.IntroVisualParaCount}, "
              + $"travelDestinationFallback={model.TravelDestinationParaCount}, "
              + $"cameraLayer={player.GetLotLayer(lotGuid)}.");
            ShowRoot(player);
        }

        private static void ShowRoot(Player player, bool preferViewToggle = false)
        {
            var panel = NewPanel(Loc.Get("explorer_panel"));
            VirtualPanel.IEntry preferred = null;
            int currentLayer = player.GetLotLayer(_lotGuid);
            var paraFinder = new ParaFinderEntry();
            ForeignLotViewToggleEntry viewToggle = null;

            if (player.IsInSelectHouseholdTutorial)
            {
                var households = new TutorialHouseholdFinderEntry();
                panel.Add(households);
                preferred = households;
            }
            else if (player.IsInSelectStoryteller)
            {
                var storytellers = new TutorialStorytellerFinderEntry();
                panel.Add(storytellers);
                preferred = storytellers;
            }
            else if (TutorialHandler.IsMultiselectStep)
            {
                // The conductor is not a member of the current lot model in the train scene.
                // Expose the exact game-authored tutorial character through the same live visual
                // registry used by the game's drag-selection implementation.
                var conductor = new TutorialConductorFinderEntry();
                panel.Add(conductor);
                preferred = conductor;
            }

            // Preserve F7's original fast action-browser role. The broader map hierarchy is
            // additive: when the selected Para currently has targets, this remains the first and
            // initially focused route. With no actionable targets, the camera's current floor is
            // still the useful starting point.
            var quickActions = new FinderEntry(FindMode.Interactable);
            panel.Add(quickActions);
            if (!player.IsInIntro && !player.IsInSelectHouseholdTutorial
                && !player.IsInSelectStoryteller && GetPrimarySelectedPara() != null)
            {
                // F7 describes the lot under the CAMERA, which can legitimately differ from the
                // selected Para's lot. Keep the fast action browser first, but put a direct route
                // back to the selected Para immediately beside it.
                var locator = new SelectedParaLocatorEntry();
                panel.Add(locator);
                ulong selectedParaLot = GetParaLot(GetPrimarySelectedPara());
                if (selectedParaLot != 0UL && selectedParaLot != _lotGuid)
                {
                    preferred = locator;
                }
            }
            if (_isForeignLot)
            {
                viewToggle = new ForeignLotViewToggleEntry();
                panel.Add(viewToggle);
                if (preferViewToggle)
                {
                    preferred = viewToggle;
                }
            }
            if (preferred == null && _model.CountObjects(FindMode.Interactable) > 0)
            {
                preferred = quickActions;
            }

            foreach (FloorModel floor in _model.Floors)
            {
                var entry = new FloorEntry(floor);
                panel.Add(entry);
                if (preferred == null && floor.Layer == currentLayer)
                {
                    preferred = entry;
                }
            }

            if (!UseActionOnlyView)
            {
                panel.Add(new FinderEntry(FindMode.All));
            }
            panel.Add(new FinderEntry(FindMode.Facilities));
            panel.Add(paraFinder);
            AddClearSelectionCommand(panel);
            SetPanel(panel, preferred, pushCurrent: false);
        }

        private static void OpenFloor(FloorModel floor)
        {
            if (floor == null)
            {
                return;
            }

            Player player = GetPlayer();
            if (player != null)
            {
                PlayerManager.Instance.SetPlayerCameraLayer(player, floor.Layer, _lotGuid);
            }

            var areas = new List<AreaModel>();
            foreach (AreaModel area in floor.Areas)
            {
                if (IsAreaVisible(area))
                {
                    areas.Add(area);
                }
            }
            Vector3 cameraPosition = GetCameraPosition();
            AreaModel current = _model.FindArea(floor.Layer, cameraPosition, null);
            areas.Sort((a, b) => CompareAreaFromCamera(a, b, current, cameraPosition));

            var panel = NewPanel(Loc.Get("explorer_floor", floor.Layer));
            VirtualPanel.IEntry preferred = null;
            foreach (AreaModel area in areas)
            {
                var entry = new AreaEntry(area);
                panel.Add(entry);
                if (area == current)
                {
                    preferred = entry;
                }
            }

            SetPanel(panel, preferred, pushCurrent: true);
        }

        private static void OpenArea(AreaModel area)
        {
            if (area == null || !area.IsAvailable)
            {
                Speak(Loc.Get("explorer_target_gone"));
                return;
            }

            Player player = GetPlayer();
            string areaName = area.GetName();
            if (!ShouldRetainCameraFollow(player, "area-selection", areaName))
            {
                if (WorldTargeting.FrameArea(
                    player, _lotGuid, area.Layer, area.GetAnchor(), "area"))
                {
                    Speak(Loc.Get("explorer_framed", areaName));
                }
                else
                {
                    Speak(Loc.Get("explorer_frame_failed"));
                }
            }

            var objects = new List<ObjectModel>();
            foreach (ObjectModel item in area.Objects)
            {
                if (ShouldIncludeObject(item)
                    && _model.FindAreaForItem(item.Root) == area)
                {
                    objects.Add(item);
                }
            }
            objects.Sort(CompareObjectsFromCamera);

            var paras = new List<ParaModel>();
            foreach (ParaModel para in area.Paras)
            {
                if (ShouldIncludePara(para)
                    && _model.FindAreaForPara(para.Character) == area)
                {
                    paras.Add(para);
                }
            }
            paras.Sort(CompareParasFromCamera);

            if (objects.Count == 0 && paras.Count == 0)
            {
                Speak(area.Describe(UseActionOnlyView));
                return;
            }

            var panel = NewPanel(areaName);
            foreach (ObjectModel item in objects)
            {
                panel.Add(new ObjectEntry(item));
            }
            foreach (ParaModel para in paras)
            {
                panel.Add(new ParaEntry(para));
            }
            SetPanel(panel, null, pushCurrent: true);
        }

        private static void OpenFinder(FindMode mode)
        {
            if (mode == FindMode.Facilities)
            {
                OpenFacilityFinder();
                return;
            }

            List<ObjectModel> matches = _model.GetObjects(mode);
            if (matches.Count == 0)
            {
                Speak(Loc.Get("explorer_no_objects"));
                return;
            }

            matches.Sort(CompareObjectsByName);
            string name = GetFinderName(mode, matches.Count);
            bool openActionDirectly = mode == FindMode.Interactable;
            if (matches.Count <= DirectListThreshold)
            {
                ShowObjectList(
                    name, matches, pushCurrent: true, openActionDirectly: openActionDirectly);
                return;
            }

            var groups = new Dictionary<string, List<ObjectModel>>();
            foreach (ObjectModel item in matches)
            {
                string key = GetNameGroup(item.Name);
                List<ObjectModel> group;
                if (!groups.TryGetValue(key, out group))
                {
                    group = new List<ObjectModel>();
                    groups.Add(key, group);
                }
                group.Add(item);
            }

            var keys = new List<string>(groups.Keys);
            keys.Sort(StringComparer.CurrentCultureIgnoreCase);
            var panel = NewPanel(name);
            VirtualPanel.IEntry preferred = null;
            int preferredInstanceId = GetPreferredObjectInstanceId();
            foreach (string key in keys)
            {
                var entry = new NameGroupEntry(
                    name, key, groups[key], openActionDirectly);
                panel.Add(entry);
                if (groups[key].Exists(item => item.InstanceId == preferredInstanceId))
                {
                    preferred = entry;
                }
            }
            SetPanel(panel, preferred, pushCurrent: true);
        }

        private static void OpenFacilityFinder()
        {
            List<FacilityChoice> matches = _model.GetFacilityChoices();
            matches.RemoveAll(choice => choice == null || !choice.IsAvailable);
            if (matches.Count == 0)
            {
                Speak(Loc.Get("explorer_no_objects"));
                return;
            }

            matches.Sort(CompareFacilityChoicesByName);
            string name = GetFinderName(FindMode.Facilities, matches.Count);
            Plugin.Log.LogInfo(
                $"[Explorer/facilities] open entries={matches.Count}, "
              + $"view={(UseActionOnlyView ? "actionable-only" : "all")}.");
            if (matches.Count <= DirectListThreshold)
            {
                ShowFacilityList(name, matches, pushCurrent: true);
                return;
            }

            var groups = new Dictionary<string, List<FacilityChoice>>();
            foreach (FacilityChoice choice in matches)
            {
                string key = GetNameGroup(choice.SortName);
                List<FacilityChoice> group;
                if (!groups.TryGetValue(key, out group))
                {
                    group = new List<FacilityChoice>();
                    groups.Add(key, group);
                }
                group.Add(choice);
            }

            var keys = new List<string>(groups.Keys);
            keys.Sort(StringComparer.CurrentCultureIgnoreCase);
            var panel = NewPanel(name);
            VirtualPanel.IEntry preferred = null;
            int preferredInstanceId = GetPreferredObjectInstanceId();
            foreach (string key in keys)
            {
                var entry = new FacilityNameGroupEntry(name, key, groups[key]);
                panel.Add(entry);
                if (groups[key].Exists(
                    choice => choice.ContainsInstance(preferredInstanceId)))
                {
                    preferred = entry;
                }
            }
            SetPanel(panel, preferred, pushCurrent: true);
        }

        private static void ShowObjectList(
            string name,
            List<ObjectModel> objects,
            bool pushCurrent,
            bool openActionDirectly = false)
        {
            var panel = NewPanel(name);
            VirtualPanel.IEntry preferred = null;
            int preferredInstanceId = GetPreferredObjectInstanceId();
            foreach (ObjectModel item in objects)
            {
                var entry = new ObjectEntry(item, openActionDirectly);
                panel.Add(entry);
                if (item.InstanceId == preferredInstanceId)
                {
                    preferred = entry;
                }
            }
            SetPanel(panel, preferred, pushCurrent);
        }

        private static void ShowFacilityList(
            string name,
            List<FacilityChoice> choices,
            bool pushCurrent)
        {
            var panel = NewPanel(name);
            VirtualPanel.IEntry preferred = null;
            int preferredInstanceId = GetPreferredObjectInstanceId();
            foreach (FacilityChoice choice in choices)
            {
                var entry = new FacilityEntry(choice);
                panel.Add(entry);
                if (choice.ContainsInstance(preferredInstanceId))
                {
                    preferred = entry;
                }
            }
            SetPanel(panel, preferred, pushCurrent);
        }

        private static void OpenParaFinder()
        {
            var paras = new List<ParaModel>();
            foreach (ParaModel para in _model.Paras)
            {
                if (ShouldIncludePara(para))
                {
                    paras.Add(para);
                }
            }
            paras.Sort(CompareParasByName);
            if (paras.Count == 0)
            {
                Speak(Loc.Get("explorer_no_paras"));
                return;
            }

            var panel = NewPanel(Loc.Get("explorer_find_paras", paras.Count));
            VirtualPanel.IEntry preferred = null;
            foreach (ParaModel para in paras)
            {
                var entry = new ParaEntry(para);
                panel.Add(entry);
                if (TutorialHandler.IsMultiselectStep
                    && para.Resolve()?.IsPartOfTutorialHousehold == true)
                {
                    preferred = entry;
                }
            }
            SetPanel(panel, preferred, pushCurrent: true);
        }

        private static void OpenTutorialConductorFinder()
        {
            TutorialConductorScan scan;
            List<TutorialConductorChoice> choices =
                GetTutorialConductorChoices(out scan);
            LogTutorialConductorScan("open", scan);
            if (choices.Count == 0)
            {
                Speak(Loc.Get(scan.IsVisualRegistryLoading
                    ? "explorer_tutorial_conductor_loading"
                    : "explorer_tutorial_conductor_unavailable"));
                return;
            }

            var panel = NewPanel(Loc.Get(
                "explorer_tutorial_conductors", choices.Count));
            foreach (TutorialConductorChoice choice in choices)
            {
                panel.Add(new TutorialConductorChoiceEntry(choice));
            }
            SetPanel(panel, null, pushCurrent: true);
        }

        private static int GetPreferredObjectInstanceId()
        {
            int tutorialTarget;
            string ignored;
            return TutorialHandler.TryGetPreferredWorldTarget(out tutorialTarget, out ignored)
                ? tutorialTarget
                : ItemPlacementHandler.LastPlacedInstanceId;
        }

        private static bool IsSelectablePara(AssetCharacter character)
        {
            if (character?.Data == null || character.Data.IsUnselectable || character.Data.IsDead
                || HouseholdManager.Instance?.CurrentHousehold?.Data?.Characters == null)
            {
                return false;
            }

            if (HouseholdManager.Instance.CurrentHousehold.Data.Characters.Contains(character.GUID))
            {
                return true;
            }

            Player player = GetPlayer();
            return player != null && (!player.IsInIntro
                || (TutorialHandler.IsMultiselectStep && character.IsPartOfTutorialHousehold));
        }

        private static bool IsCurrentHouseholdPara(AssetCharacter character)
        {
            List<ulong> household =
                HouseholdManager.Instance?.CurrentHousehold?.Data?.Characters;
            return character != null && household != null
                && household.Contains(character.GUID);
        }

        private static bool CanChangeParaSelection(ParaModel para)
        {
            return CanChangeParaSelection(para?.Resolve());
        }

        private static bool CanChangeParaSelection(AssetCharacter character)
        {
            Player player = GetPlayer();
            if (player == null || player.SelectedCharactersGUID == null
                || character == null || CharacterManager.Instance == null
                || player.State != GameStates.LiveMode
                || player.IsInSelectHouseholdTutorial || player.IsInSelectStoryteller
                || !IsSelectablePara(character))
            {
                return false;
            }

            try
            {
                return player.CanChangeCharacterSelection();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Checking Para-selection availability threw: " + ex);
                return false;
            }
        }

        private static bool CanSelectOnlyPara(ParaModel para)
        {
            AssetCharacter character = para?.Resolve();
            Player player = GetPlayer();
            return CanChangeParaSelection(para) && IsCurrentHouseholdPara(character)
                && player?.SelectedCharactersGUID != null
                && (player.SelectedCharactersGUID.Count != 1
                    || player.SelectedCharactersGUID[0] != character.GUID);
        }

        private static bool CanToggleParaSelection(ParaModel para)
        {
            return CanToggleParaSelection(para?.Resolve());
        }

        private static bool CanToggleParaSelection(AssetCharacter character)
        {
            if (!CanChangeParaSelection(character))
            {
                return false;
            }

            Player player = GetPlayer();
            if (!player.SelectedCharactersGUID.Contains(character.GUID))
            {
                return true;
            }

            if (!IsCurrentHouseholdPara(character))
            {
                return player.SelectedCharactersGUID.Count > 1;
            }

            int selectedHouseholdMembers = 0;
            foreach (ulong guid in player.SelectedCharactersGUID)
            {
                AssetCharacter selected = AssetManager.Instance?.GetCharacter(guid);
                if (IsCurrentHouseholdPara(selected))
                {
                    selectedHouseholdMembers++;
                }
            }
            return selectedHouseholdMembers > 1;
        }

        private static string DescribeToggleParaSelection(ParaModel para)
        {
            return DescribeToggleParaSelection(para?.Resolve());
        }

        private static string DescribeToggleParaSelection(AssetCharacter character)
        {
            Player player = GetPlayer();
            if (character == null || player?.SelectedCharactersGUID == null)
            {
                return null;
            }

            return player.SelectedCharactersGUID.Contains(character.GUID)
                ? Loc.Get("explorer_remove_from_selection")
                : Loc.Get("explorer_add_to_selection");
        }

        private static void SelectOnlyPara(ParaModel para)
        {
            Player player = GetPlayer();
            AssetCharacter character = para?.Resolve();
            if (!CanSelectOnlyPara(para) || player == null || character == null)
            {
                Speak(Loc.Get("explorer_action_unavailable"));
                return;
            }

            CharacterManager.Instance.SelectCharacter(character.GUID, player.PlayerIndex);
            bool selectedOnly = player.SelectedCharactersGUID.Count == 1
                && player.SelectedCharactersGUID[0] == character.GUID;
            Plugin.Log.LogInfo(
                $"[Explorer] select-only character={character.GUID}, accepted={selectedOnly}.");
            Speak(selectedOnly
                ? Loc.Get("explorer_para_selected_only", para.GetDisplayName())
                : Loc.Get("explorer_action_unavailable"));
        }

        private static void ToggleParaSelection(ParaModel para)
        {
            ToggleParaSelection(
                para?.Resolve(), para?.GetDisplayName(), "lot-model");
        }

        private static void ToggleParaSelection(
            AssetCharacter character, string displayName, string source)
        {
            Player player = GetPlayer();
            if (player == null || character == null
                || !CanToggleParaSelection(character))
            {
                Speak(Loc.Get("explorer_action_unavailable"));
                return;
            }

            bool wasSelected = player.SelectedCharactersGUID.Contains(character.GUID);
            if (wasSelected)
            {
                CharacterManager.Instance.UnselectCharacter(character.GUID, player.PlayerIndex);
            }
            else
            {
                CharacterManager.Instance.AddSelectedCharacter(character.GUID, player.PlayerIndex);
            }

            bool isSelected = player.SelectedCharactersGUID.Contains(character.GUID);
            bool accepted = isSelected != wasSelected;
            int count = player.SelectedCharactersGUID.Count;
            Plugin.Log.LogInfo(
                $"[Explorer] Para selection source={source}, character={character.GUID}, "
              + $"before={wasSelected}, after={isSelected}, accepted={accepted}, "
              + $"selectedCount={count}.");
            string name = !string.IsNullOrWhiteSpace(displayName)
                ? displayName
                : character.Data?.FullName;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = Loc.Get("focus_unlabeled");
            }
            Speak(accepted
                ? Loc.Get(
                    isSelected ? "explorer_para_selected" : "explorer_para_unselected",
                    name.Trim(), count)
                : Loc.Get("explorer_action_unavailable"));
        }

        private static void HandleEnter(Keyboard keyboard)
        {
            bool pressed = keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame;
            bool released = keyboard.enterKey.wasReleasedThisFrame || keyboard.numpadEnterKey.wasReleasedThisFrame;
            if (pressed)
            {
                _enterPressed = true;
                _enterWithShift = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
                _enterEntry = _panel.Current;
            }
            if (!released || !_enterPressed) return;

            VirtualPanel.IEntry entry = _enterEntry;
            _enterPressed = false;
            _enterEntry = null;
            if (_panel.Current == null || !ReferenceEquals(entry, _panel.Current))
            {
                Speak(Loc.Get("explorer_target_gone"));
                return;
            }
            if (_enterWithShift)
            {
                if (entry is IParaSelectionEntry paraEntry) paraEntry.ToggleSelection();
                else Speak(Loc.Get("explorer_selection_shortcut_requires_para"));
            }
            else
            {
                _panel.Activate();
            }
        }

        private static AssetCharacter GetParaRetainedWhenClearing()
        {
            Player player = GetPlayer();
            List<ulong> household = HouseholdManager.Instance?.CurrentHousehold?.Data?.Characters;
            if (player?.SelectedCharactersGUID == null || household == null || AssetManager.Instance == null)
                return null;
            foreach (ulong guid in player.SelectedCharactersGUID)
            {
                if (household.Contains(guid))
                {
                    AssetCharacter character = AssetManager.Instance.GetCharacter(guid);
                    if (character?.Data != null && !character.Data.IsDead) return character;
                }
            }
            foreach (ulong guid in household)
            {
                AssetCharacter character = AssetManager.Instance.GetCharacter(guid);
                if (character?.Data != null && !character.Data.IsDead) return character;
            }
            return null;
        }

        private static void AddClearSelectionCommand(VirtualPanel panel)
        {
            panel.Add(new CommandEntry(
                () => CanChangeParaSelection(GetParaRetainedWhenClearing()),
                () => Loc.Get("explorer_clear_selection", GetParaRetainedWhenClearing()?.Data?.FullName),
                ClearSelectedGroup));
        }

        private static void ClearSelectedGroup()
        {
            Player player = GetPlayer();
            AssetCharacter retained = GetParaRetainedWhenClearing();
            if (!CanChangeParaSelection(retained))
            {
                Speak(Loc.Get("explorer_action_unavailable"));
                return;
            }
            // UpdateSelectedCharacterOnStart restores saved household selections every time the
            // list is empty. Native select-only dissolves the group without triggering that restore.
            int before = player.SelectedCharactersGUID.Count;
            CharacterManager.Instance.SelectCharacter(retained.GUID, player.PlayerIndex);
            bool accepted = player.SelectedCharactersGUID.Count == 1
                && player.SelectedCharactersGUID[0] == retained.GUID;
            Plugin.Log.LogInfo($"[Explorer] clear selected group before={before}, "
                + $"after={player.SelectedCharactersGUID.Count}, retained={retained.GUID}, accepted={accepted}.");
            Speak(accepted ? Loc.Get("explorer_selection_cleared", retained.Data.FullName)
                : Loc.Get("explorer_action_unavailable"));
        }

        private static List<TutorialConductorChoice> GetTutorialConductorChoices(
            out TutorialConductorScan scan)
        {
            var choices = new List<TutorialConductorChoice>();
            scan = new TutorialConductorScan();

            CharacterManager characterManager = CharacterManager.Instance;
            Dictionary<ulong, CharacterVisual> visuals =
                characterManager?.LoadedCharacterVisuals;
            scan.CharacterManagerAvailable = characterManager != null;
            scan.RegistryAvailable = visuals != null;
            if (visuals == null)
            {
                return choices;
            }

            scan.RegistryEntries = visuals.Count;
            var seen = new HashSet<ulong>();
            foreach (KeyValuePair<ulong, CharacterVisual> entry in visuals)
            {
                CharacterVisual visual = entry.Value;
                if (visual == null)
                {
                    scan.NullVisuals++;
                    continue;
                }
                if (visual.gameObject == null || !visual.gameObject.activeInHierarchy)
                {
                    scan.InactiveVisuals++;
                    continue;
                }
                if (!visual.IsVisible)
                {
                    scan.HiddenVisuals++;
                    continue;
                }
                if (entry.Key == 0UL || visual.CharacterGUID != entry.Key)
                {
                    scan.GuidMismatches++;
                    continue;
                }

                scan.ActiveVisuals++;
                AssetCharacter character = characterManager.GetCharacterByGUID(entry.Key);
                if (character?.Data == null)
                {
                    scan.MissingCharacters++;
                    continue;
                }
                if (character.Data.IsUnselectable)
                {
                    scan.UnselectableCharacters++;
                    continue;
                }
                if (!character.IsPartOfTutorialHousehold)
                {
                    scan.NotTutorialConductors++;
                    continue;
                }
                if (IsCurrentHouseholdPara(character))
                {
                    scan.CurrentHouseholdCharacters++;
                    continue;
                }
                if (!seen.Add(character.GUID))
                {
                    scan.DuplicateCharacters++;
                    continue;
                }

                choices.Add(new TutorialConductorChoice(visual));
                scan.AcceptedConductors++;
            }

            choices.Sort((a, b) => string.Compare(
                a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return choices;
        }

        private static void LogTutorialConductorScan(
            string source, TutorialConductorScan scan)
        {
            if (scan == null)
            {
                return;
            }

            Plugin.Log.LogInfo(
                $"[Explorer] tutorial conductor visual scan source={source}, "
              + $"phase={TutorialHandler.CurrentPhaseId}, "
              + $"characterManager={scan.CharacterManagerAvailable}, "
              + $"registry={scan.RegistryAvailable}, candidates={scan.RegistryEntries}, "
              + $"active={scan.ActiveVisuals}, conductors={scan.AcceptedConductors}, "
              + $"rejected={scan.RejectedEntries}, null={scan.NullVisuals}, "
              + $"inactive={scan.InactiveVisuals}, hidden={scan.HiddenVisuals}, "
              + $"guidMismatch={scan.GuidMismatches}, "
              + $"missingCharacter={scan.MissingCharacters}, "
              + $"unselectable={scan.UnselectableCharacters}, "
              + $"notTutorialConductor={scan.NotTutorialConductors}, "
              + $"currentHousehold={scan.CurrentHouseholdCharacters}, "
              + $"duplicate={scan.DuplicateCharacters}.");
        }

        private static List<HouseholdChoice> GetTutorialHouseholdChoices(
            out HouseholdChoiceScan scan)
        {
            var choices = new List<HouseholdChoice>();
            scan = new HouseholdChoiceScan();

            CharacterManager characterManager = CharacterManager.Instance;
            HouseholdManager householdManager = HouseholdManager.Instance;
            Dictionary<ulong, CharacterVisual> visuals = characterManager?.LoadedCharacterVisuals;
            scan.CharacterManagerAvailable = characterManager != null;
            scan.HouseholdManagerAvailable = householdManager != null;
            scan.RegistryAvailable = visuals != null;
            if (visuals == null)
            {
                return choices;
            }

            scan.RegistryEntries = visuals.Count;
            var seen = new HashSet<ulong>();
            foreach (KeyValuePair<ulong, CharacterVisual> entry in visuals)
            {
                CharacterVisual visual = entry.Value;
                if (visual == null)
                {
                    scan.NullVisuals++;
                    continue;
                }
                if (visual.gameObject == null || !visual.gameObject.activeInHierarchy)
                {
                    scan.InactiveVisuals++;
                    continue;
                }
                if (!visual.IsVisible)
                {
                    scan.HiddenVisuals++;
                    continue;
                }
                if (entry.Key == 0UL || visual.CharacterGUID != entry.Key)
                {
                    scan.GuidMismatches++;
                    continue;
                }

                scan.ActiveVisuals++;
                AssetCharacter character = characterManager.GetCharacterByGUID(entry.Key);
                if (character?.Data == null)
                {
                    scan.MissingCharacters++;
                    continue;
                }
                if (character.Data.IsUnselectable)
                {
                    scan.UnselectableCharacters++;
                    continue;
                }

                AssetHousehold household =
                    householdManager?.GetHouseholdOfCharacter(character);
                if (household?.Data == null)
                {
                    scan.MissingHouseholds++;
                    continue;
                }
                if (!seen.Add(household.GUID))
                {
                    scan.DuplicateHouseholds++;
                    continue;
                }

                choices.Add(new HouseholdChoice(household, visual));
                scan.AcceptedHouseholds++;
            }

            choices.Sort((a, b) => string.Compare(
                a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return choices;
        }

        /// <summary>
        /// Counts the same live, visual-backed choices used by the explorer so tutorial guidance
        /// cannot fall back to the ordinary lot-Para collection.
        /// </summary>
        internal static int GetTutorialHouseholdChoiceCount(out bool visualRegistryLoading)
        {
            HouseholdChoiceScan scan;
            int count = GetTutorialHouseholdChoices(out scan).Count;
            visualRegistryLoading = scan.IsVisualRegistryLoading;
            return count;
        }

        private static HouseholdChoice GetVisualBackedHouseholdChoice(AssetCharacter character)
        {
            CharacterManager characterManager = CharacterManager.Instance;
            CharacterVisual visual;
            if (character?.Data == null || character.Data.IsUnselectable
                || characterManager?.LoadedCharacterVisuals == null
                || !characterManager.LoadedCharacterVisuals.TryGetValue(
                    character.GUID, out visual)
                || !IsActiveCharacterVisual(character.GUID, visual))
            {
                return null;
            }

            AssetHousehold household = HouseholdManager.Instance?
                .GetHouseholdOfCharacter(character);
            return household?.Data != null
                ? new HouseholdChoice(household, visual)
                : null;
        }

        private static bool IsActiveCharacterVisual(
            ulong characterGuid, CharacterVisual visual)
        {
            return WorldTargeting.IsActiveCharacterVisual(characterGuid, visual);
        }

        private static void LogTutorialHouseholdScan(
            string source, HouseholdChoiceScan scan)
        {
            if (scan == null)
            {
                return;
            }

            Plugin.Log.LogInfo(
                $"[Explorer] tutorial household visual scan source={source}, "
              + $"characterManager={scan.CharacterManagerAvailable}, "
              + $"householdManager={scan.HouseholdManagerAvailable}, "
              + $"registry={scan.RegistryAvailable}, loading={scan.IsVisualRegistryLoading}, "
              + $"candidates={scan.RegistryEntries}, active={scan.ActiveVisuals}, "
              + $"households={scan.AcceptedHouseholds}, rejected={scan.RejectedEntries}, "
              + $"null={scan.NullVisuals}, inactive={scan.InactiveVisuals}, "
              + $"hidden={scan.HiddenVisuals}, guidMismatch={scan.GuidMismatches}, "
              + $"missingCharacter={scan.MissingCharacters}, "
              + $"unselectable={scan.UnselectableCharacters}, "
              + $"missingHousehold={scan.MissingHouseholds}, "
              + $"duplicateHousehold={scan.DuplicateHouseholds}.");
        }

        private static ObjectModel GetCreateHouseholdTarget()
        {
            NewGame newGame = Settings.Get<NewGame>();
            if (_model == null || newGame == null)
            {
                return null;
            }

            foreach (ObjectModel item in _model.Objects)
            {
                ItemObjectRoot root = item.Resolve();
                if (newGame.ItemToCreateNewHouseholdInTutorial != 0UL && root != null
                    && root.CatalogueGUID == newGame.ItemToCreateNewHouseholdInTutorial)
                {
                    return item;
                }
            }
            return null;
        }

        private static void OpenTutorialHouseholds()
        {
            HouseholdChoiceScan scan;
            List<HouseholdChoice> choices = GetTutorialHouseholdChoices(out scan);
            bool canCreate = IsTutorialHouseholdCreationAvailable();
            LogTutorialHouseholdScan("open", scan);
            if (choices.Count == 0 && !canCreate)
            {
                Speak(Loc.Get(scan.IsVisualRegistryLoading
                    ? "tutorial_household_targets_loading"
                    : "tutorial_household_targets_unavailable"));
                return;
            }

            var panel = NewPanel(Loc.Get(
                "explorer_tutorial_households", choices.Count + (canCreate ? 1 : 0)));
            foreach (HouseholdChoice choice in choices)
            {
                panel.Add(new HouseholdChoiceEntry(choice));
            }
            if (canCreate)
            {
                panel.Add(new CreateHouseholdNativeEntry());
            }
            SetPanel(panel, null, pushCurrent: true);
        }

        /// <summary>
        /// True when the tutorial's actual Create New Household window command is available.
        /// </summary>
        /// <remarks>
        /// The world also contains an ItemToCreateNewHouseholdInTutorial target, but TEST 71
        /// measured the native button while that item was absent from the loaded lot model. The
        /// screen command is the authoritative route: UITrainSelectStartingHousehold implements it
        /// by calling HouseholdManager.CreateNewHousehold with its owning player index.
        /// </remarks>
        internal static bool IsTutorialHouseholdCreationAvailable()
        {
            Player player = GetPlayer();
            UITrainSelectStartingHousehold window = player != null
                ? UI.GetOrNull<UITrainSelectStartingHousehold>(player.PlayerIndex)
                : null;
            return player?.IsInSelectHouseholdTutorial == true
                && window != null && window.IsVisible
                && !window.IsInfoPanelVisible()
                && !player.PlayerActionBlockedByUIWindow;
        }

        private static void CreateTutorialHouseholdFromWindow()
        {
            Player player = GetPlayer();
            UITrainSelectStartingHousehold window = player != null
                ? UI.GetOrNull<UITrainSelectStartingHousehold>(player.PlayerIndex)
                : null;
            if (!IsTutorialHouseholdCreationAvailable() || window == null)
            {
                Speak(Loc.Get("explorer_action_unavailable"));
                return;
            }

            Close(announce: false);
            Plugin.Log.LogInfo(
                "[Explorer] invoking UITrainSelectStartingHousehold's native Create New Household command.");
            window.OnButtonCreateNewHousehold();
        }

        private static bool CanReviewHousehold(HouseholdChoice choice)
        {
            Player player = GetPlayer();
            return choice?.ResolveHousehold()?.Data != null
                && player?.IsInSelectHouseholdTutorial == true
                && !player.PlayerActionBlockedByUIWindow
                && (StoryboardManager.Instance == null
                    || !StoryboardManager.Instance.IsCurrentyPlayingStoryboardHidingUI());
        }

        private static void ReviewHousehold(HouseholdChoice choice)
        {
            Player player = GetPlayer();
            AssetHousehold household = choice?.ResolveHousehold();
            if (!CanReviewHousehold(choice) || player == null || household?.Data == null)
            {
                Speak(Loc.Get("explorer_action_unavailable"));
                return;
            }

            UITrainSelectStartingHousehold window =
                UI.Get<UITrainSelectStartingHousehold>(player.PlayerIndex);
            if (window == null)
            {
                Speak(Loc.Get("explorer_action_unavailable"));
                return;
            }

            ulong householdGuid = household.GUID;
            Close(announce: false);
            Plugin.Log.LogInfo(
                $"[Explorer] reviewing tutorial household {householdGuid} through native UI.");
            window.RefreshAndShowInfoPanel(household);
        }

        private static bool IsCreateHouseholdTarget(ItemObjectRoot root)
        {
            NewGame newGame = Settings.Get<NewGame>();
            return root != null && newGame != null
                && newGame.ItemToCreateNewHouseholdInTutorial != 0UL
                && GetPlayer()?.IsInSelectHouseholdTutorial == true
                && root.CatalogueGUID == newGame.ItemToCreateNewHouseholdInTutorial;
        }

        private static void CreateTutorialHousehold(ObjectModel item)
        {
            Player player = GetPlayer();
            ItemObjectRoot root = item?.Resolve();
            if (player == null || HouseholdManager.Instance == null
                || !IsCreateHouseholdTarget(root) || player.PlayerActionBlockedByUIWindow)
            {
                Speak(Loc.Get("explorer_action_unavailable"));
                return;
            }

            int instanceId = root.InstanceID;
            Close(announce: false);
            Plugin.Log.LogInfo(
                $"[Explorer] invoking native tutorial household creation from instance={instanceId}.");
            HouseholdManager.Instance.CreateNewHousehold(player.PlayerIndex);
        }

        private static List<StorytellerChoice> GetTutorialStorytellerChoices()
        {
            var choices = new List<StorytellerChoice>();
            Storytellers setting = Settings.Get<Storytellers>();
            if (setting?.AllStorytellers == null)
            {
                return choices;
            }

            foreach (Storyteller storyteller in setting.AllStorytellers)
            {
                if (storyteller == null)
                {
                    continue;
                }

                ObjectModel objectRepresentation = null;
                foreach (ObjectModel item in _model.Objects)
                {
                    ItemObjectRoot root = item.Resolve();
                    if (storyteller.PhysicalItem != 0UL && root != null
                        && root.CatalogueGUID == storyteller.PhysicalItem)
                    {
                        objectRepresentation = item;
                        break;
                    }
                }

                ulong characterRepresentationGuid = 0UL;
                if (objectRepresentation == null)
                {
                    CharacterManager manager = CharacterManager.Instance;
                    if (manager?.LoadedCharacterVisuals != null)
                    {
                        foreach (KeyValuePair<ulong, CharacterVisual> entry
                            in manager.LoadedCharacterVisuals)
                        {
                            AssetCharacter character = manager.GetCharacterByGUID(entry.Key);
                            if (storyteller.PhysicalPremadeCharacter != 0UL
                                && character?.Data != null
                                && character.PremadeGUID == storyteller.PhysicalPremadeCharacter
                                && IsActiveCharacterVisual(entry.Key, entry.Value))
                            {
                                characterRepresentationGuid = entry.Key;
                                break;
                            }
                        }
                    }
                }

                // UIStorytellerSelection.Refresh iterates this same AllStorytellers array, and
                // SetCurrentStoryteller is the exact operation UpdateSelect invokes after clicking
                // a physical representation. A representation is useful spatial context but is not
                // an availability requirement for the actual three choices.
                choices.Add(new StorytellerChoice(
                    storyteller, objectRepresentation, characterRepresentationGuid));
            }

            choices.Sort((a, b) => string.Compare(
                a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            Plugin.Log.LogInfo(
                $"[Explorer] tutorial Storyteller choices source=AllStorytellers, "
              + $"count={choices.Count}, represented={choices.FindAll(c => c.HasRepresentation).Count}.");
            return choices;
        }

        private static void OpenTutorialStorytellers()
        {
            List<StorytellerChoice> choices = GetTutorialStorytellerChoices();
            if (choices.Count == 0)
            {
                Speak(Loc.Get("tutorial_storyteller_targets_loading"));
                return;
            }

            var panel = NewPanel(Loc.Get("explorer_tutorial_storytellers", choices.Count));
            foreach (StorytellerChoice choice in choices)
            {
                panel.Add(new StorytellerChoiceEntry(choice));
            }
            SetPanel(panel, null, pushCurrent: true);
        }

        private static Storyteller FindStoryteller(ItemObjectRoot root)
        {
            Storytellers setting = Settings.Get<Storytellers>();
            if (root == null || setting?.AllStorytellers == null)
            {
                return null;
            }

            foreach (Storyteller storyteller in setting.AllStorytellers)
            {
                if (storyteller != null && storyteller.PhysicalItem != 0UL
                    && storyteller.PhysicalItem == root.CatalogueGUID)
                {
                    return storyteller;
                }
            }
            return null;
        }

        private static Storyteller FindStoryteller(AssetCharacter character)
        {
            Storytellers setting = Settings.Get<Storytellers>();
            if (character == null || setting?.AllStorytellers == null)
            {
                return null;
            }

            foreach (Storyteller storyteller in setting.AllStorytellers)
            {
                if (storyteller != null && storyteller.PhysicalPremadeCharacter != 0UL
                    && storyteller.PhysicalPremadeCharacter == character.PremadeGUID)
                {
                    return storyteller;
                }
            }
            return null;
        }

        private static string DescribeStoryteller(Storyteller storyteller)
        {
            if (storyteller == null)
            {
                return null;
            }

            string name = GameText("Storyteller_" + storyteller.DisplayName);
            string description = GameText(
                "StorytellerDescription_" + storyteller.DisplayName);
            return string.IsNullOrEmpty(description)
                ? name
                : Loc.Get("tutorial_storyteller_choice", name, description);
        }

        private static void SelectStoryteller(Storyteller storyteller)
        {
            Player player = GetPlayer();
            if (storyteller == null || player?.IsInSelectStoryteller != true)
            {
                Speak(Loc.Get("explorer_action_unavailable"));
                return;
            }

            UIStorytellerSelection window =
                UI.Get<UIStorytellerSelection>(player.PlayerIndex);
            if (window == null)
            {
                Speak(Loc.Get("explorer_action_unavailable"));
                return;
            }

            ulong guid = storyteller.GUID;
            Close(announce: false);
            Plugin.Log.LogInfo(
                $"[Explorer] selecting tutorial Storyteller {guid} through native UI.");
            window.SetCurrentStoryteller(storyteller, isCustomStoryteller: false);
        }

        private static string GameText(string key)
        {
            string text = TranslationManager.Get(key);
            return string.IsNullOrWhiteSpace(text) ? key : text.Trim();
        }

        private static void OpenObjectCommands(
            ObjectModel item, string nameOverride = null)
        {
            ItemObjectRoot root = item?.Resolve();
            if (root == null)
            {
                Speak(Loc.Get("explorer_target_gone"));
                return;
            }

            string name = !string.IsNullOrWhiteSpace(nameOverride)
                ? nameOverride.Trim()
                : item.GetDisplayName();
            Player player = GetPlayer();
            if (!ShouldRetainCameraFollow(player, "object-selection", name))
            {
                if (WorldTargeting.FrameItem(root, player, "object-selection"))
                {
                    Speak(Loc.Get("explorer_framed", name));
                }
                else
                {
                    Speak(Loc.Get("explorer_frame_failed"));
                }
            }

            var panel = NewPanel(Loc.Get("explorer_object_commands", name));
            Storyteller storyteller = GetPlayer()?.IsInSelectStoryteller == true
                ? FindStoryteller(root)
                : null;
            if (IsCreateHouseholdTarget(root))
            {
                panel.Add(new CommandEntry(
                    () => IsCreateHouseholdTarget(item.Resolve()),
                    () => Loc.Get("explorer_create_household_command"),
                    () => CreateTutorialHousehold(item)));
            }
            else if (storyteller != null)
            {
                Storyteller captured = storyteller;
                panel.Add(new CommandEntry(
                    () => GetPlayer()?.IsInSelectStoryteller == true
                        && FindStoryteller(item.Resolve()) == captured,
                    () => Loc.Get(
                        "explorer_select_storyteller_command", DescribeStoryteller(captured)),
                    () => SelectStoryteller(captured)));
            }
            else
            {
                panel.Add(new CommandEntry(
                    () => item.IsAvailable,
                    () => DescribeItemActionCommand(item),
                    () => OpenItemInteraction(item)));
            }
            panel.Add(new CommandEntry(
                () => item.IsAvailable,
                () => Loc.Get("explorer_frame_object"),
                () => FrameObject(item, name)));
            SetPanel(
                panel, null, pushCurrent: true,
                contextAvailable: () => item.IsAvailable);
        }

        private static void OpenParaCommands(ParaModel para)
        {
            AssetCharacter character = para?.Resolve();
            if (character == null)
            {
                Speak(Loc.Get("explorer_target_gone"));
                return;
            }

            string name = para.GetDisplayName();
            Player player = GetPlayer();
            if (!ShouldRetainCameraFollow(player, "para-selection", name))
            {
                if (WorldTargeting.FrameCharacter(character, player, _lotGuid))
                {
                    Speak(Loc.Get("explorer_framed", name));
                }
                else
                {
                    Speak(Loc.Get("explorer_frame_failed"));
                }
            }

            var panel = NewPanel(Loc.Get("explorer_para_commands", name));
            if (GetPlayer()?.IsInSelectHouseholdTutorial != true
                && GetPlayer()?.IsInSelectStoryteller != true
                && IsSelectablePara(character))
            {
                panel.Add(new CommandEntry(
                    () => CanSelectOnlyPara(para),
                    () => Loc.Get("explorer_select_only_para"),
                    () => SelectOnlyPara(para)));
                panel.Add(new CommandEntry(
                    () => CanToggleParaSelection(para),
                    () => DescribeToggleParaSelection(para),
                    () => ToggleParaSelection(para)));
            }

            if (GetPlayer()?.IsInSelectHouseholdTutorial == true)
            {
                HouseholdChoice choice = GetVisualBackedHouseholdChoice(character);
                if (choice != null)
                {
                    panel.Add(new CommandEntry(
                        () => CanReviewHousehold(choice),
                        () => Loc.Get(
                            "explorer_review_household_command", choice.Name),
                        () => ReviewHousehold(choice)));
                }
            }
            else if (GetPlayer()?.IsInSelectStoryteller == true)
            {
                Storyteller storyteller = FindStoryteller(character);
                if (storyteller != null)
                {
                    Storyteller captured = storyteller;
                    panel.Add(new CommandEntry(
                        () => GetPlayer()?.IsInSelectStoryteller == true
                            && FindStoryteller(para.Resolve()) == captured,
                        () => Loc.Get(
                            "explorer_select_storyteller_command",
                            DescribeStoryteller(captured)),
                        () => SelectStoryteller(captured)));
                }
            }
            else
            {
                panel.Add(new CommandEntry(
                    () => para.IsAvailable,
                    () => DescribeParaActionCommand(para),
                    () => OpenParaInteraction(para)));
            }
            panel.Add(new CommandEntry(
                () => para.IsAvailable,
                () => Loc.Get("explorer_follow_para"),
                () => FollowPara(para)));
            panel.Add(new CommandEntry(
                () => para.IsAvailable,
                () => Loc.Get("explorer_frame_para"),
                () => FramePara(para)));
            AddClearSelectionCommand(panel);
            SetPanel(
                panel, null, pushCurrent: true,
                contextAvailable: () => para.IsAvailable);
        }

        private static void OpenTutorialConductorCommands(
            TutorialConductorChoice choice)
        {
            AssetCharacter character = choice?.ResolveCharacter();
            if (character == null)
            {
                Speak(Loc.Get("explorer_target_gone"));
                return;
            }

            string name = choice.Name;
            Player player = GetPlayer();
            if (!ShouldRetainCameraFollow(player, "tutorial-conductor-selection", name))
            {
                if (WorldTargeting.FrameCharacter(character, player, _lotGuid))
                {
                    Speak(Loc.Get("explorer_framed", name));
                }
                else
                {
                    Speak(Loc.Get("explorer_frame_failed"));
                }
            }

            var panel = NewPanel(Loc.Get("explorer_para_commands", name));
            var selection = new CommandEntry(
                () => CanToggleParaSelection(choice?.ResolveCharacter()),
                () => DescribeToggleParaSelection(choice?.ResolveCharacter()),
                () => ToggleParaSelection(
                    choice?.ResolveCharacter(), choice?.Name, "tutorial-conductor"));
            panel.Add(selection);
            AddClearSelectionCommand(panel);
            panel.Add(new CommandEntry(
                () => choice?.IsAvailable == true,
                () => Loc.Get("explorer_frame_para"),
                () => FrameTutorialConductor(choice)));
            SetPanel(panel, selection, pushCurrent: true);
        }

        private static void FrameTutorialConductor(TutorialConductorChoice choice)
        {
            AssetCharacter character = choice?.ResolveCharacter();
            if (character == null)
            {
                Speak(Loc.Get("explorer_target_gone"));
                return;
            }

            Player player = GetPlayer();
            ulong previousFollow = player?.CameraCurrentCharacterFollowTarget ?? 0UL;
            string previousFollowName = GetParaName(previousFollow);
            bool framed = WorldTargeting.FrameCharacter(character, player, _lotGuid);
            SpeakFrameResult(
                framed, choice.Name, player, previousFollow, previousFollowName);
        }

        private static void OpenHouseholdCommands(HouseholdChoice choice)
        {
            AssetCharacter character = choice?.ResolveCharacter();
            if (character == null)
            {
                Speak(Loc.Get("explorer_target_gone"));
                return;
            }

            FrameHouseholdRepresentation(choice, explicitCommand: false);
            var panel = NewPanel(Loc.Get("explorer_household_commands", choice.Name));
            panel.Add(new CommandEntry(
                () => CanReviewHousehold(choice),
                () => Loc.Get("explorer_review_household_command", choice.Name),
                () => ReviewHousehold(choice)));
            panel.Add(new CommandEntry(
                () => choice?.IsAvailable == true,
                () => Loc.Get("explorer_frame_household"),
                () => FrameHouseholdRepresentation(choice, explicitCommand: true)));
            SetPanel(panel, null, pushCurrent: true);
        }

        private static void FrameHouseholdRepresentation(
            HouseholdChoice choice, bool explicitCommand)
        {
            AssetCharacter character = choice?.ResolveCharacter();
            if (character == null)
            {
                Speak(Loc.Get("explorer_target_gone"));
                return;
            }

            Player player = GetPlayer();
            if (!explicitCommand && ShouldRetainCameraFollow(
                player, "household-selection", choice.RepresentativeName))
            {
                return;
            }

            ulong previousFollow = player?.CameraCurrentCharacterFollowTarget ?? 0UL;
            string previousFollowName = GetParaName(previousFollow);
            bool framed = WorldTargeting.FrameCharacter(character, player, _lotGuid);
            SpeakFrameResult(
                framed, choice.RepresentativeName, player,
                previousFollow, previousFollowName);
        }

        private static string DescribeItemActionCommand(ObjectModel item)
        {
            string blocker = GetItemActionBlocker(item?.Resolve(), GetPlayer());
            return Loc.Get(
                "objects_description",
                Loc.Get("explorer_open_actions"),
                blocker == null
                    ? Loc.Get("explorer_actions_available")
                    : Loc.Get("explorer_actions_unavailable"));
        }

        private static string DescribeParaActionCommand(ParaModel para)
        {
            InteractionGroup ignored;
            string blocker = GetParaActionBlocker(para?.Resolve(), GetPlayer(), out ignored);
            return Loc.Get(
                "objects_description",
                Loc.Get("explorer_open_actions"),
                blocker == null
                    ? Loc.Get("explorer_actions_available")
                    : Loc.Get("explorer_actions_unavailable"));
        }

        private static void FrameObject(
            ObjectModel item, string nameOverride = null)
        {
            ItemObjectRoot root = item?.Resolve();
            if (root == null)
            {
                Speak(Loc.Get("explorer_target_gone"));
                return;
            }

            Player player = GetPlayer();
            ulong previousFollow = player?.CameraCurrentCharacterFollowTarget ?? 0UL;
            string previousFollowName = GetParaName(previousFollow);
            bool framed = WorldTargeting.FrameItem(root, player, "object-command");
            SpeakFrameResult(
                framed,
                !string.IsNullOrWhiteSpace(nameOverride)
                    ? nameOverride.Trim()
                    : item.GetDisplayName(),
                player,
                previousFollow, previousFollowName);
        }

        private static void FramePara(ParaModel para)
        {
            AssetCharacter character = para?.Resolve();
            if (character == null)
            {
                Speak(Loc.Get("explorer_target_gone"));
                return;
            }

            Player player = GetPlayer();
            ulong previousFollow = player?.CameraCurrentCharacterFollowTarget ?? 0UL;
            string previousFollowName = GetParaName(previousFollow);
            bool framed = WorldTargeting.FrameCharacter(character, player, _lotGuid);
            SpeakFrameResult(
                framed, para.GetDisplayName(), player,
                previousFollow, previousFollowName);
        }

        private static bool ShouldRetainCameraFollow(
            Player player, string source, string requestedTarget)
        {
            ulong followedGuid = player?.CameraCurrentCharacterFollowTarget ?? 0UL;
            if (followedGuid == 0UL)
            {
                return false;
            }

            Plugin.Log.LogInfo(
                $"[ExplorerCamera] implicit frame skipped source={source ?? "unknown"}, "
              + $"requestedTarget='{requestedTarget ?? "unknown"}', "
              + $"retainedFollow={followedGuid}, retainedName='{GetParaName(followedGuid)}'.");
            return true;
        }

        private static void SpeakFrameResult(
            bool framed,
            string targetName,
            Player player,
            ulong previousFollow,
            string previousFollowName)
        {
            if (!framed)
            {
                Speak(Loc.Get("explorer_frame_failed"));
                return;
            }

            if (previousFollow != 0UL
                && (player == null || player.CameraCurrentCharacterFollowTarget == 0UL))
            {
                Speak(Loc.Get(
                    "explorer_framed_stopped_following",
                    targetName,
                    previousFollowName));
                return;
            }

            Speak(Loc.Get("explorer_framed", targetName));
        }

        private static string GetParaName(ulong characterGuid)
        {
            if (characterGuid == 0UL)
            {
                return Loc.Get("state_unknown_para");
            }

            AssetCharacter character = CharacterManager.Instance?
                .GetCharacterByGUID(characterGuid);
            return character?.Data != null
                && !string.IsNullOrWhiteSpace(character.Data.FullName)
                    ? character.Data.FullName.Trim()
                    : Loc.Get("state_unknown_para");
        }

        private static void FollowPara(ParaModel para)
        {
            AssetCharacter character = para?.Resolve();
            Player player = GetPlayer();
            if (character == null || player == null)
            {
                Speak(Loc.Get("explorer_target_gone"));
                return;
            }

            player.CameraCurrentCharacterFollowTarget = character.GUID;
            CameraFollowHandler.FrameFollowStart(player, character, "explorer-para");
            string name = para.GetDisplayName();
            Close(announce: false);
            Speak(Loc.Get("explorer_following", name));
        }

        private static AssetCharacter GetPrimarySelectedPara()
        {
            Player player = GetPlayer();
            ulong guid = player?.SelectedCharactersGUID != null
                && player.SelectedCharactersGUID.Count > 0
                    ? player.SelectedCharactersGUID[0]
                    : 0UL;
            return guid == 0UL
                ? null
                : CharacterManager.Instance?.GetCharacterByGUID(guid);
        }

        private static ulong GetParaLot(AssetCharacter character)
        {
            if (character?.Data == null || CharacterManager.Instance == null)
            {
                return 0UL;
            }

            ulong lotGuid = CharacterManager.Instance.GetLotForCharacter(character);
            return lotGuid != 0UL || character.Data.OnZoneWallObject == null
                ? lotGuid
                : character.Data.OnZoneWallObject.LotPlacedOnGUID;
        }

        private static string DescribeSelectedParaLocator()
        {
            AssetCharacter character = GetPrimarySelectedPara();
            if (character?.Data == null)
            {
                return Loc.Get("explorer_locate_selected_para_unavailable");
            }

            string name = string.IsNullOrWhiteSpace(character.Data.FullName)
                ? Loc.Get("state_unknown_para")
                : character.Data.FullName.Trim();
            Player player = GetPlayer();
            return player?.CameraCurrentCharacterFollowTarget == character.GUID
                ? Loc.Get("explorer_selected_para_already_followed", name)
                : Loc.Get("explorer_locate_selected_para", name);
        }

        private static void LocateSelectedPara()
        {
            Player player = GetPlayer();
            AssetCharacter character = GetPrimarySelectedPara();
            if (player == null || character?.Data == null)
            {
                Speak(Loc.Get("explorer_locate_selected_para_unavailable"));
                return;
            }

            ulong lotGuid = GetParaLot(character);

            player.CameraCurrentCharacterFollowTarget = character.GUID;
            bool framed = CameraFollowHandler.FrameFollowStart(
                player, character, "explorer-selected-para");
            if (!framed)
            {
                framed = WorldTargeting.FrameCharacter(character, player, lotGuid);
            }

            string name = string.IsNullOrWhiteSpace(character.Data.FullName)
                ? Loc.Get("state_unknown_para")
                : character.Data.FullName.Trim();
            Plugin.Log.LogInfo(
                $"[ExplorerCamera] selected-para locator character={character.GUID}, "
              + $"lot={lotGuid}, framed={framed}, followTarget="
              + $"{player.CameraCurrentCharacterFollowTarget}.");
            Close(announce: false);
            Speak(framed
                ? Loc.Get("explorer_following", name)
                : Loc.Get("explorer_follow_started_frame_pending", name));
        }

        private static void OpenItemInteraction(ObjectModel item)
        {
            ItemObjectRoot root = item?.Resolve();
            Player player = GetPlayer();
            string blocker = GetItemActionBlocker(root, player);
            if (blocker != null)
            {
                Speak(blocker);
                return;
            }

            InteractionGroup group = InteractionManager.Instance.GetItemInteractionGroup(root);
            UIInteractions interactions = NativeInteractionMenu.GetOrCreate(
                player.PlayerIndex, "explorer-item");
            if (interactions == null)
            {
                Speak(Loc.Get("objects_open_failed"));
                return;
            }

            Vector3 position = WorldTargeting.GetItemPosition(root);
            int instanceId = root.InstanceID;
            ulong lotGuid = root.LotPlacedOnGUID;
            Plugin.Log.LogInfo(
                $"[InteractionOpen] phase=request, source=explorer-item, instance={instanceId}, "
              + $"group={(group != null ? group.GUID.ToString() : "null")}, lot={lotGuid}, "
              + $"selectedCharacter={player.GetSelectedCharacterGUID()}, worldPosition={position}.");

            Close(announce: false);
            interactions.Show(group, position, instanceId, 0UL, root, lotGuid);
            Plugin.Log.LogInfo(
                $"[InteractionOpen] phase=shown, source=explorer-item, instance={instanceId}, "
              + $"group={(group != null ? group.GUID.ToString() : "null")}, lot={lotGuid}.");
        }

        private static void OpenParaInteraction(ParaModel para)
        {
            AssetCharacter character = para?.Resolve();
            Player player = GetPlayer();
            InteractionGroup group;
            string blocker = GetParaActionBlocker(character, player, out group);
            if (blocker != null)
            {
                Speak(blocker);
                return;
            }

            UIInteractions interactions = NativeInteractionMenu.GetOrCreate(
                player.PlayerIndex, "explorer-para");
            if (interactions == null)
            {
                Speak(Loc.Get("objects_open_failed"));
                return;
            }

            Vector3 position = WorldTargeting.GetCharacterPosition(character);
            ulong interactionLotGuid = GetCharacterInteractionLotGuid(character);
            ulong characterGuid = character.GUID;
            Plugin.Log.LogInfo(
                $"[InteractionOpen] phase=request, source=explorer-para, character={characterGuid}, "
              + $"group={group.GUID}, lot={interactionLotGuid}, selectedCharacter="
              + $"{player.GetSelectedCharacterGUID()}, worldPosition={position}.");

            Close(announce: false);
            interactions.Show(group, position, -1, characterGuid, null, interactionLotGuid);
            Plugin.Log.LogInfo(
                $"[InteractionOpen] phase=shown, source=explorer-para, character={characterGuid}, "
              + $"group={group.GUID}, lot={interactionLotGuid}.");
        }

        private static ulong GetCharacterInteractionLotGuid(AssetCharacter character)
        {
            ulong lotGuid = LotManager.Instance != null
                ? LotManager.Instance.GetLotFromPosition(
                    WorldTargeting.GetCharacterPosition(character))
                : 0UL;
            return lotGuid != 0UL ? lotGuid : _lotGuid;
        }

        private static string GetItemActionBlocker(ItemObjectRoot root, Player player)
        {
            try
            {
                if (root == null || player == null || _model == null
                    || !_model.ContainsExactItem(root))
                {
                    return Loc.Get("explorer_target_gone");
                }
                if (!TutorialHandler.CanUseGoHereMarkerAction(root.InstanceID))
                {
                    return Loc.Get("explorer_action_unavailable");
                }
                if (player.SelectedCharactersGUID == null
                    || player.SelectedCharactersGUID.Count == 0)
                {
                    return Loc.Get("explorer_selected_para_required");
                }
                if (player.PlayerActionBlockedByUIWindow)
                {
                    return Loc.Get("explorer_ui_blocked");
                }
                if (InteractionManager.Instance == null)
                {
                    return Loc.Get("explorer_action_unavailable");
                }
                if (player.IsInIntro && NeedsTutorialManager.Instance != null
                    && !NeedsTutorialManager.Instance.CanClickOnItem(root))
                {
                    return Loc.Get("explorer_action_unavailable");
                }

                AssetCharacter selected = GetSelectedCharacter(player);
                if (!player.IsInIntro && selected?.Data != null
                    && selected.Data.CurrentOccupationIndex != -1 && selected.IsVisualLoaded)
                {
                    return TranslationManager.Get("UIPrompt_CantDoInteractionsBecauseWorking");
                }

                return InteractionManager.Instance.ItemHasInteractions(root, player.PlayerIndex)
                    ? null
                    : Loc.Get("explorer_action_unavailable");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Checking an explorer item action threw: " + ex);
                return Loc.Get("explorer_action_unavailable");
            }
        }

        private static string GetParaActionBlocker(
            AssetCharacter target, Player player, out InteractionGroup group)
        {
            group = null;
            try
            {
                if (target?.Data == null || player == null || _model == null
                    || !_model.ContainsExactPara(target) || target.Data.IsDead)
                {
                    return Loc.Get("explorer_target_gone");
                }
                if (player.IsInIntro)
                {
                    return Loc.Get("explorer_action_unavailable");
                }
                if (player.SelectedCharactersGUID == null
                    || player.SelectedCharactersGUID.Count == 0)
                {
                    return Loc.Get("explorer_selected_para_required");
                }
                if (player.PlayerActionBlockedByUIWindow)
                {
                    return Loc.Get("explorer_ui_blocked");
                }

                Interactions settings = Settings.Get<Interactions>();
                ulong groupGuid = InteractionManager.Instance.GetInteractionGroupGUIDFor(
                    settings, player.SelectedCharactersGUID, target.GUID);
                group = settings.GetInteractionGroupByGUID(groupGuid);
                if (group == null)
                {
                    return Loc.Get("explorer_action_unavailable");
                }

                foreach (ulong selectedGuid in player.SelectedCharactersGUID)
                {
                    if (InteractionManager.Instance.CanShowInteractionGroupInInteractionList(
                        group,
                        null,
                        player.PlayerIndex,
                        selectedGuid,
                        target.GUID,
                        GetCharacterInteractionLotGuid(target)))
                    {
                        return null;
                    }
                }

                group = null;
                return Loc.Get("explorer_action_unavailable");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Checking an explorer Para action threw: " + ex);
                group = null;
                return Loc.Get("explorer_action_unavailable");
            }
        }

        private static ulong ResolveCurrentLot(Player player, out string source)
        {
            source = "none";
            LotManager lots = LotManager.Instance;
            if (lots == null || player == null)
            {
                return 0UL;
            }

            AssetLot special = lots.CurrentSpecialLot;
            if (special != null && GetLoadedLot(special.GUID) != null)
            {
                source = "special-lot";
                return special.GUID;
            }

            Transform lookTarget = PlayerManager.Instance?.HybridPlayer1?
                .HybridCamera?.FreeCamera?.LookTarget;
            LotPerimeterZoneObject perimeter;
            if (lookTarget != null && ZoneManager.Instance != null
                && ZoneManager.Instance.TryGetLotPerimeterZoneAtPosition(
                    lookTarget.position, out perimeter))
            {
                ulong guid = perimeter.RealLotGUID;
                if (GetLoadedLot(guid) != null)
                {
                    source = "camera-position";
                    return guid;
                }
            }

            if (player.CameraLookingAtLotGUID != 0UL)
            {
                ulong guid = ResolveEffectiveLot(player.CameraLookingAtLotGUID);
                if (GetLoadedLot(guid) != null)
                {
                    source = "camera-owned-lot";
                    return guid;
                }
            }

            AssetCharacter followed = player.CameraCurrentCharacterFollowTarget != 0UL
                ? CharacterManager.Instance?
                    .GetCharacterByGUID(player.CameraCurrentCharacterFollowTarget)
                : null;
            if (followed?.Data?.OnLotPerimeterZone != null)
            {
                ulong guid = followed.Data.OnLotPerimeterZone.RealLotGUID;
                if (GetLoadedLot(guid) != null)
                {
                    source = "followed-para-lot";
                    return guid;
                }
            }

            AssetCharacter selected = GetSelectedCharacter(player);
            if (selected?.Data?.OnLotPerimeterZone != null)
            {
                ulong guid = selected.Data.OnLotPerimeterZone.RealLotGUID;
                if (GetLoadedLot(guid) != null)
                {
                    source = "selected-para-lot";
                    return guid;
                }
            }

            // NearFrontSegment can put an arrived actor on town terrain immediately outside the
            // target polygon. Use the exact retained L transaction only after every ordinary
            // camera/Para lot source has failed, and before the unrelated sole-owned fallback.
            ulong travelLotGuid;
            ulong travelActorGuid;
            string travelDiagnostic;
            if (TownMapLotHandler.TryGetFollowedTravelDestination(
                player, out travelLotGuid, out travelActorGuid, out travelDiagnostic))
            {
                ulong guid = ResolveEffectiveLot(travelLotGuid);
                if (GetLoadedLot(guid) != null)
                {
                    Plugin.Log.LogInfo("[Explorer/resolve] " + travelDiagnostic + ".");
                    source = "followed-travel-destination";
                    return guid;
                }
            }
            else if (!string.IsNullOrEmpty(travelDiagnostic))
            {
                Plugin.Log.LogInfo("[Explorer/resolve] " + travelDiagnostic + ".");
            }

            List<ulong> owned = HouseholdManager.Instance?.CurrentHousehold?.Data?.OwnedLots;
            if (owned != null && owned.Count == 1)
            {
                ulong guid = ResolveEffectiveLot(owned[0]);
                if (GetLoadedLot(guid) != null)
                {
                    source = "sole-owned-lot";
                    return guid;
                }
            }

            return 0UL;
        }

        /// <summary>
        /// Describes the stable room/outdoor area occupied by a Para using the same model and
        /// ordering as F7. This is intentionally called only after a followed Para has remained in
        /// a new area for several frames; rebuilding the explorer model every frame would be both
        /// wasteful and prone to narrating doorway-boundary flicker.
        /// </summary>
        internal static bool TryDescribeParaAreaForNarration(
            AssetCharacter character, out string areaName, out string keyContents)
        {
            areaName = null;
            keyContents = null;
            if (character?.Data == null)
            {
                return false;
            }

            try
            {
                ulong lotGuid = 0UL;
                if (character.Data.OnLotPerimeterZone != null)
                {
                    lotGuid = character.Data.OnLotPerimeterZone.RealLotGUID;
                }
                else if (character.Data.OnZoneWallObject != null)
                {
                    lotGuid = character.Data.OnZoneWallObject.LotPlacedOnGUID;
                }

                lotGuid = ResolveEffectiveLot(lotGuid);
                Player player = GetPlayer();
                ExplorerModel model = ExplorerModel.Build(lotGuid, player);
                AreaModel area = model?.FindAreaForPara(character);
                if (area == null)
                {
                    return false;
                }

                areaName = area.GetName();
                keyContents = area.DescribeKeyContents();
                return !string.IsNullOrWhiteSpace(areaName);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Describing a followed Para's area threw: " + ex);
                return false;
            }
        }

        private static ulong ResolveEffectiveLot(ulong guid)
        {
            LotPerimeterZoneObject perimeter = ZoneManager.Instance?
                .GetLotPerimeterZoneObjectByGUID(guid);
            return perimeter != null ? perimeter.RealLotGUID : guid;
        }

        private static AssetLot GetLoadedLot(ulong guid)
        {
            if (guid == 0UL)
            {
                return null;
            }

            AssetLot lot = LotManager.Instance?.GetLotByGUID(guid);
            if (lot == null && LotManager.Instance?.CurrentSpecialLot?.GUID == guid)
            {
                lot = LotManager.Instance.CurrentSpecialLot;
            }
            return lot;
        }

        private static bool IsContextStillValid(HybridPlayer hybridPlayer)
        {
            Player player = hybridPlayer?.Player;
            return player != null
                && player.State == GameStates.LiveMode
                && player.ItemInPlacement == null
                && !player.PlayerActionBlockedByUIWindow
                && !IsTownMapBlockingExplorer(hybridPlayer)
                && _lotGuid != 0UL
                && GetLoadedLot(_lotGuid) != null
                && _model != null;
        }

        private static bool IsTownMapBlockingExplorer(HybridPlayer hybridPlayer)
        {
            Player player = hybridPlayer?.Player;
            return hybridPlayer != null && hybridPlayer.IsInTownMap()
                && player?.IsInSelectHouseholdTutorial != true;
        }

        private static AssetCharacter GetSelectedCharacter(Player player)
        {
            if (player?.SelectedCharactersGUID == null
                || player.SelectedCharactersGUID.Count == 0 || AssetManager.Instance == null)
            {
                return null;
            }
            return AssetManager.Instance.GetCharacter(player.SelectedCharactersGUID[0]);
        }

        private static Player GetPlayer()
        {
            return PlayerManager.Instance?.HybridPlayer1?.Player;
        }

        private static bool UseActionOnlyView =>
            _isForeignLot && !_showAllForeignLotObjects;

        private static bool IsOwnedLot(ulong lotGuid)
        {
            HouseholdManager households = HouseholdManager.Instance;
            if (households == null)
            {
                return false;
            }
            if (households.OwnsLot(lotGuid))
            {
                return true;
            }

            // An owned perimeter can resolve to its proxy premade lot when loaded. Ownership is
            // stored on the perimeter GUID, while the explorer intentionally models the effective
            // loaded asset, so compare both forms before calling that loaded view foreign.
            List<ulong> owned = households.CurrentHousehold?.Data?.OwnedLots;
            if (owned == null)
            {
                return false;
            }
            foreach (ulong ownedGuid in owned)
            {
                if (ResolveEffectiveLot(ownedGuid) == lotGuid)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool ShouldIncludeObject(ObjectModel item)
        {
            if (item == null)
            {
                return false;
            }
            return !UseActionOnlyView
                ? item.IsAvailable
                : item.HasCurrentActionMenu(GetPlayer());
        }

        private static bool ShouldIncludePara(ParaModel para)
        {
            if (para == null)
            {
                return false;
            }
            if (!UseActionOnlyView)
            {
                return para.IsAvailable;
            }
            return para.HasCurrentActionMenu(GetPlayer());
        }

        private static bool IsAreaVisible(AreaModel area)
        {
            return area != null && area.IsAvailable
                && (!UseActionOnlyView
                    || area.GetVisibleObjectCount(actionOnly: true) > 0
                    || area.GetVisibleParaCount(actionOnly: true) > 0);
        }

        private static int CountVisibleParas()
        {
            int count = 0;
            if (_model == null)
            {
                return count;
            }
            foreach (ParaModel para in _model.Paras)
            {
                if (ShouldIncludePara(para))
                {
                    count++;
                }
            }
            return count;
        }

        private static Vector3 GetCameraPosition()
        {
            Transform lookTarget = PlayerManager.Instance?.HybridPlayer1?
                .HybridCamera?.FreeCamera?.LookTarget;
            return lookTarget != null ? lookTarget.position : Vector3.zero;
        }

        private static VirtualPanel NewPanel(string name)
        {
            return new VirtualPanel(Loc.Get("panel_summary", name, Loc.Get("explorer_controls")));
        }

        private static void SetPanel(
            VirtualPanel panel,
            VirtualPanel.IEntry preferred,
            bool pushCurrent,
            Func<bool> contextAvailable = null)
        {
            if (panel == null)
            {
                return;
            }
            if (pushCurrent && _panel != null)
            {
                _history.Push(new ViewState(
                    _panel, _panel.Current, _panelContextAvailable));
            }
            _panel = panel;
            _panelContextAvailable = contextAvailable;
            _enterPressed = false;
            _panel.Enter(preferred);
        }

        private static void ReturnFromUnavailableTarget()
        {
            LotBuildInputOwnership.HoldThroughCurrentFrame();
            _enterPressed = false;

            while (_history.Count > 0)
            {
                ViewState state = _history.Pop();
                if (state.ContextAvailable != null && !state.ContextAvailable())
                {
                    continue;
                }

                _panel = state.Panel;
                _panelContextAvailable = state.ContextAvailable;
                _panel.EnterSilently(state.Preferred);
                if (_panel.AvailableCount > 0)
                {
                    Speak(Loc.Get("explorer_target_gone_returned"));
                    return;
                }
            }

            Close(announce: false);
            Speak(Loc.Get("explorer_target_gone"));
        }

        private static void GoBack()
        {
            LotBuildInputOwnership.HoldThroughCurrentFrame();
            _enterPressed = false;
            if (_history.Count == 0)
            {
                Close(announce: true);
                return;
            }

            ViewState state = _history.Pop();
            _panel = state.Panel;
            _panelContextAvailable = state.ContextAvailable;
            _panel.Enter(state.Preferred);
        }

        private static void CaptureCameraBaseline(HybridPlayer hybridPlayer)
        {
            _cameraWasFrozen = hybridPlayer.Player.FreezeCameraMovement;
            _cameraBaselineCaptured = true;
            hybridPlayer.Player.FreezeCameraMovement = true;
        }

        private static void RestoreCamera()
        {
            if (!_cameraBaselineCaptured)
            {
                return;
            }

            bool cameraWasFrozen = _cameraWasFrozen;
            _cameraBaselineCaptured = false;
            try
            {
                Player player = GetPlayer();
                if (player != null)
                {
                    player.FreezeCameraMovement = cameraWasFrozen;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Restoring the explorer camera baseline threw: " + ex);
            }
        }

        private static void Close(bool announce)
        {
            LotBuildInputOwnership.HoldThroughCurrentFrame();
            _panel = null;
            _history.Clear();
            _model = null;
            _lotGuid = 0UL;
            _lotSource = null;
            _tutorialPhaseAtOpen = null;
            _panelContextAvailable = null;
            _isForeignLot = false;
            _showAllForeignLotObjects = false;
            _enterPressed = false;
            RestoreCamera();
            if (announce)
            {
                Speak(Loc.Get("explorer_closed"));
            }
        }

        private static void Speak(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }
            Plugin.Log.LogInfo("[Explorer] " + message);
            ScreenReader.Speak(message);
        }

        private static void AddPart(List<string> parts, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add(value.Trim());
            }
        }

        private static string Count(string singularKey, string pluralKey, int value)
        {
            return value == 1 ? Loc.Get(singularKey) : Loc.Get(pluralKey, value);
        }

        private static string GetFinderName(FindMode mode, int count)
        {
            switch (mode)
            {
                case FindMode.Interactable:
                    int tutorialTarget;
                    string ignored;
                    return TutorialHandler.TryGetPreferredWorldTarget(
                        out tutorialTarget, out ignored)
                            ? Loc.Get("explorer_find_actions_tutorial", count)
                            : Loc.Get("explorer_find_actions", count);
                case FindMode.Facilities:
                    return Loc.Get("explorer_find_facilities", count);
                default:
                    return Loc.Get("explorer_find_all", count);
            }
        }

        private static string GetNameGroup(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "#";
            }
            char first = char.ToUpper(name.Trim()[0], System.Globalization.CultureInfo.CurrentCulture);
            return char.IsLetter(first) ? first.ToString() : "#";
        }

        private static int CompareAreaFromCamera(
            AreaModel a, AreaModel b, AreaModel current, Vector3 camera)
        {
            if (a == current && b != current)
            {
                return -1;
            }
            if (b == current && a != current)
            {
                return 1;
            }
            int distance = Vector3.Distance(a.GetAnchor(), camera)
                .CompareTo(Vector3.Distance(b.GetAnchor(), camera));
            return distance != 0 ? distance : a.SortKey.CompareTo(b.SortKey);
        }

        private static int CompareObjectsFromCamera(ObjectModel a, ObjectModel b)
        {
            Vector3 camera = GetCameraPosition();
            int distance = Vector3.Distance(a.Position, camera)
                .CompareTo(Vector3.Distance(b.Position, camera));
            return distance != 0 ? distance : CompareObjectsByName(a, b);
        }

        private static int CompareObjectsByName(ObjectModel a, ObjectModel b)
        {
            int name = string.Compare(
                a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            if (name != 0)
            {
                return name;
            }
            int path = string.Compare(
                a.GetPath(), b.GetPath(), StringComparison.CurrentCultureIgnoreCase);
            return path != 0 ? path : a.InstanceId.CompareTo(b.InstanceId);
        }

        private static int CompareFacilityChoicesByName(
            FacilityChoice a, FacilityChoice b)
        {
            int name = string.Compare(
                a?.SortName, b?.SortName, StringComparison.CurrentCultureIgnoreCase);
            if (name != 0)
            {
                return name;
            }

            ObjectModel first = a?.ResolveTarget(requireActionMenu: false);
            ObjectModel second = b?.ResolveTarget(requireActionMenu: false);
            if (first == null || second == null)
            {
                return first == second ? 0 : first == null ? 1 : -1;
            }
            return CompareObjectsByName(first, second);
        }

        private static int CompareParasFromCamera(ParaModel a, ParaModel b)
        {
            Vector3 camera = GetCameraPosition();
            int distance = Vector3.Distance(a.Position, camera)
                .CompareTo(Vector3.Distance(b.Position, camera));
            return distance != 0 ? distance : CompareParasByName(a, b);
        }

        private static int CompareParasByName(ParaModel a, ParaModel b)
        {
            int name = string.Compare(
                a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            return name != 0 ? name : a.Guid.CompareTo(b.Guid);
        }

        private enum FindMode
        {
            All,
            Interactable,
            Facilities
        }

        private sealed class ViewState
        {
            internal ViewState(
                VirtualPanel panel,
                VirtualPanel.IEntry preferred,
                Func<bool> contextAvailable)
            {
                Panel = panel;
                Preferred = preferred;
                ContextAvailable = contextAvailable;
            }

            internal VirtualPanel Panel { get; }
            internal VirtualPanel.IEntry Preferred { get; }
            internal Func<bool> ContextAvailable { get; }
        }

        private sealed class FloorEntry : VirtualPanel.IEntry
        {
            private readonly FloorModel _floor;

            internal FloorEntry(FloorModel floor)
            {
                _floor = floor;
            }

            public bool IsAvailable => _floor != null
                && (!UseActionOnlyView
                    || _floor.GetVisibleAreaCount(actionOnly: true) > 0);

            public string Describe()
            {
                return Loc.Get(
                    "explorer_floor_entry",
                    Loc.Get("explorer_floor", _floor.Layer),
                    Count(
                        "explorer_area_count_one", "explorer_area_count_many",
                        _floor.GetVisibleAreaCount(UseActionOnlyView)),
                    Count(
                        "explorer_object_count_one", "explorer_object_count_many",
                        _floor.GetVisibleObjectCount(UseActionOnlyView)),
                    Count(
                        "explorer_para_count_one", "explorer_para_count_many",
                        _floor.GetVisibleParaCount(UseActionOnlyView)));
            }

            public void Activate()
            {
                OpenFloor(_floor);
            }
        }

        private sealed class AreaEntry : VirtualPanel.IEntry
        {
            private readonly AreaModel _area;

            internal AreaEntry(AreaModel area)
            {
                _area = area;
            }

            public bool IsAvailable => IsAreaVisible(_area);

            public string Describe()
            {
                return _area?.Describe(UseActionOnlyView);
            }

            public void Activate()
            {
                OpenArea(_area);
            }
        }

        private sealed class ForeignLotViewToggleEntry : VirtualPanel.IEntry
        {
            public bool IsAvailable => _model != null && _isForeignLot;

            public string Describe()
            {
                return Loc.Get(UseActionOnlyView
                    ? "explorer_view_actionable"
                    : "explorer_view_all");
            }

            public void Activate()
            {
                Player player = GetPlayer();
                if (player == null || _model == null)
                {
                    Speak(Loc.Get("explorer_lot_required"));
                    return;
                }

                _showAllForeignLotObjects = !_showAllForeignLotObjects;
                _history.Clear();
                Plugin.Log.LogInfo(
                    "[Explorer] foreign-lot view changed to "
                  + (UseActionOnlyView ? "actionable-only." : "all objects."));
                ShowRoot(player, preferViewToggle: true);
            }
        }

        private sealed class FinderEntry : VirtualPanel.IEntry
        {
            private readonly FindMode _mode;

            internal FinderEntry(FindMode mode)
            {
                _mode = mode;
            }

            public bool IsAvailable => _model != null;

            public string Describe()
            {
                int count = _mode == FindMode.Facilities
                    ? _model.CountAvailableFacilityChoices()
                    : _model.CountObjects(_mode);
                return GetFinderName(_mode, count);
            }

            public void Activate()
            {
                OpenFinder(_mode);
            }
        }

        private sealed class ParaFinderEntry : VirtualPanel.IEntry
        {
            public bool IsAvailable => _model != null;

            public string Describe()
            {
                return Loc.Get("explorer_find_paras", CountVisibleParas());
            }

            public void Activate()
            {
                OpenParaFinder();
            }
        }

        private sealed class SelectedParaLocatorEntry : VirtualPanel.IEntry
        {
            public bool IsAvailable => GetPrimarySelectedPara() != null;

            public string Describe()
            {
                return DescribeSelectedParaLocator();
            }

            public void Activate()
            {
                LocateSelectedPara();
            }
        }

        private sealed class TutorialConductorFinderEntry : VirtualPanel.IEntry
        {
            public bool IsAvailable => _model != null;

            public string Describe()
            {
                TutorialConductorScan scan;
                int count = GetTutorialConductorChoices(out scan).Count;
                LogTutorialConductorScan("finder", scan);
                return count == 0
                    ? Loc.Get(scan.IsVisualRegistryLoading
                        ? "explorer_tutorial_conductor_loading"
                        : "explorer_tutorial_conductor_unavailable")
                    : Loc.Get("explorer_tutorial_conductors", count);
            }

            public void Activate()
            {
                OpenTutorialConductorFinder();
            }
        }

        private sealed class TutorialHouseholdFinderEntry : VirtualPanel.IEntry
        {
            public bool IsAvailable => _model != null;

            public string Describe()
            {
                HouseholdChoiceScan scan;
                int count = GetTutorialHouseholdChoices(out scan).Count
                    + (IsTutorialHouseholdCreationAvailable() ? 1 : 0);
                LogTutorialHouseholdScan("finder", scan);
                return count == 0
                    ? Loc.Get(scan.IsVisualRegistryLoading
                        ? "tutorial_household_targets_loading"
                        : "tutorial_household_targets_unavailable")
                    : Loc.Get("explorer_tutorial_households", count);
            }

            public void Activate()
            {
                OpenTutorialHouseholds();
            }
        }

        private sealed class TutorialStorytellerFinderEntry : VirtualPanel.IEntry
        {
            public bool IsAvailable => _model != null;

            public string Describe()
            {
                int count = GetTutorialStorytellerChoices().Count;
                return count == 0
                    ? Loc.Get("tutorial_storyteller_targets_loading")
                    : Loc.Get("explorer_tutorial_storytellers", count);
            }

            public void Activate()
            {
                OpenTutorialStorytellers();
            }
        }

        private sealed class TutorialConductorChoice
        {
            private readonly ulong _characterGuid;

            internal TutorialConductorChoice(CharacterVisual visual)
            {
                _characterGuid = visual.CharacterGUID;
            }

            internal bool IsAvailable => ResolveCharacter() != null;
            internal string Name
            {
                get
                {
                    string name = ResolveCharacter()?.Data?.FullName;
                    return string.IsNullOrWhiteSpace(name)
                        ? Loc.Get("focus_unlabeled")
                        : name.Trim();
                }
            }

            internal AssetCharacter ResolveCharacter()
            {
                CharacterVisual visual = ResolveVisual();
                AssetCharacter character = visual != null
                    ? CharacterManager.Instance?.GetCharacterByGUID(_characterGuid)
                    : null;
                return character?.Data != null && !character.Data.IsUnselectable
                    && character.IsPartOfTutorialHousehold
                    && !IsCurrentHouseholdPara(character)
                        ? character
                        : null;
            }

            internal string Describe()
            {
                AssetCharacter character = ResolveCharacter();
                CharacterVisual visual = ResolveVisual();
                if (character == null || visual == null)
                {
                    return null;
                }

                var parts = new List<string>
                {
                    Name,
                    Loc.Get("explorer_tutorial_conductor_target")
                };
                AddPart(parts,
                    WorldTargeting.DescribeFromCamera(visual.transform.position));
                Player player = GetPlayer();
                if (player?.SelectedCharactersGUID != null)
                {
                    string selectionState =
                        player.SelectedCharactersGUID.Contains(character.GUID)
                            ? Loc.Get("explorer_selected")
                            : Loc.Get("explorer_not_selected");
                    parts.Add(Loc.Get(
                        "explorer_para_selection_state", selectionState));
                }

                Plugin.Log.LogInfo(
                    $"[Explorer] focus type=tutorial-conductor, "
                  + $"character={character.GUID}, name='{Name}', "
                  + $"position={visual.transform.position}.");
                return string.Join(
                    Loc.Get("lot_detail_separator"), parts.ToArray());
            }

            private CharacterVisual ResolveVisual()
            {
                CharacterVisual visual;
                CharacterManager manager = CharacterManager.Instance;
                return TutorialHandler.IsMultiselectStep
                    && manager?.LoadedCharacterVisuals != null
                    && manager.LoadedCharacterVisuals.TryGetValue(
                        _characterGuid, out visual)
                    && IsActiveCharacterVisual(_characterGuid, visual)
                        ? visual
                        : null;
            }
        }

        private sealed class TutorialConductorScan
        {
            internal bool CharacterManagerAvailable { get; set; }
            internal bool RegistryAvailable { get; set; }
            internal int RegistryEntries { get; set; }
            internal int ActiveVisuals { get; set; }
            internal int AcceptedConductors { get; set; }
            internal int NullVisuals { get; set; }
            internal int InactiveVisuals { get; set; }
            internal int HiddenVisuals { get; set; }
            internal int GuidMismatches { get; set; }
            internal int MissingCharacters { get; set; }
            internal int UnselectableCharacters { get; set; }
            internal int NotTutorialConductors { get; set; }
            internal int CurrentHouseholdCharacters { get; set; }
            internal int DuplicateCharacters { get; set; }

            internal bool IsVisualRegistryLoading =>
                !RegistryAvailable || RegistryEntries == 0;

            internal int RejectedEntries => NullVisuals + InactiveVisuals
                + HiddenVisuals + GuidMismatches + MissingCharacters
                + UnselectableCharacters + NotTutorialConductors
                + CurrentHouseholdCharacters + DuplicateCharacters;
        }

        private interface IParaSelectionEntry : VirtualPanel.IEntry
        {
            void ToggleSelection();
        }

        private sealed class TutorialConductorChoiceEntry : IParaSelectionEntry
        {
            private readonly TutorialConductorChoice _choice;

            internal TutorialConductorChoiceEntry(TutorialConductorChoice choice)
            {
                _choice = choice;
            }

            public bool IsAvailable => _choice?.IsAvailable == true;

            public string Describe()
            {
                return IsAvailable ? _choice.Describe() : null;
            }

            public void Activate()
            {
                OpenTutorialConductorCommands(_choice);
            }

            public void ToggleSelection()
            {
                ToggleParaSelection(_choice?.ResolveCharacter(), _choice?.Name, "tutorial-shift-enter");
            }
        }

        private sealed class HouseholdChoice
        {
            private readonly AssetHousehold _household;
            private readonly ulong _householdGuid;
            private readonly ulong _characterGuid;

            internal HouseholdChoice(AssetHousehold household, CharacterVisual visual)
            {
                _household = household;
                _householdGuid = household.GUID;
                _characterGuid = visual.CharacterGUID;
            }

            internal bool IsAvailable => ResolveHousehold() != null;
            internal string RepresentativeName
            {
                get
                {
                    string name = ResolveCharacter()?.Data?.FullName;
                    return string.IsNullOrWhiteSpace(name)
                        ? Loc.Get("focus_unlabeled")
                        : name.Trim();
                }
            }
            internal string Name
            {
                get
                {
                    AssetHousehold household = ResolveHousehold() ?? _household;
                    string name = household?.Data?.GetHouseholdName();
                    return string.IsNullOrWhiteSpace(name)
                        ? Loc.Get("focus_unlabeled")
                        : name.Trim();
                }
            }

            internal AssetCharacter ResolveCharacter()
            {
                AssetCharacter character = ResolveSelectableCharacter();
                AssetHousehold household = character != null
                    ? HouseholdManager.Instance?.GetHouseholdOfCharacter(character)
                    : null;
                return household?.Data != null && household.GUID == _householdGuid
                    ? character
                    : null;
            }

            internal AssetHousehold ResolveHousehold()
            {
                AssetCharacter character = ResolveSelectableCharacter();
                AssetHousehold household = character != null
                    ? HouseholdManager.Instance?.GetHouseholdOfCharacter(character)
                    : null;
                return household?.Data != null && household.GUID == _householdGuid
                    ? household
                    : null;
            }

            internal string Describe()
            {
                AssetHousehold household = ResolveHousehold();
                AssetCharacter character = ResolveCharacter();
                CharacterVisual visual = ResolveVisual();
                if (household?.Data == null || character?.Data == null || visual == null)
                {
                    return null;
                }

                string biography = household.Data.Biography;
                if (!string.IsNullOrEmpty(biography)
                    && biography.StartsWith("HouseholdBio_", StringComparison.Ordinal))
                {
                    biography = TranslationManager.Get(biography);
                }

                string identity = string.IsNullOrWhiteSpace(biography)
                    ? Name
                    : Loc.Get("tutorial_household_choice", Name, biography.Trim());
                string cameraRelation =
                    WorldTargeting.DescribeFromCamera(visual.transform.position);
                string representation = string.IsNullOrWhiteSpace(cameraRelation)
                    ? Loc.Get("explorer_household_representation_name", RepresentativeName)
                    : Loc.Get(
                        "explorer_household_representation",
                        RepresentativeName,
                        cameraRelation);

                return Loc.Get(
                    "objects_description",
                    identity,
                    representation);
            }

            private CharacterVisual ResolveVisual()
            {
                CharacterVisual visual;
                CharacterManager manager = CharacterManager.Instance;
                return manager?.LoadedCharacterVisuals != null
                    && manager.LoadedCharacterVisuals.TryGetValue(_characterGuid, out visual)
                    && IsActiveCharacterVisual(_characterGuid, visual)
                        ? visual
                        : null;
            }

            private AssetCharacter ResolveSelectableCharacter()
            {
                CharacterVisual visual = ResolveVisual();
                AssetCharacter character = visual != null
                    ? CharacterManager.Instance?.GetCharacterByGUID(_characterGuid)
                    : null;
                return character?.Data != null && !character.Data.IsUnselectable
                    ? character
                    : null;
            }
        }

        private sealed class HouseholdChoiceScan
        {
            internal bool CharacterManagerAvailable { get; set; }
            internal bool HouseholdManagerAvailable { get; set; }
            internal bool RegistryAvailable { get; set; }
            internal int RegistryEntries { get; set; }
            internal int ActiveVisuals { get; set; }
            internal int AcceptedHouseholds { get; set; }
            internal int NullVisuals { get; set; }
            internal int InactiveVisuals { get; set; }
            internal int HiddenVisuals { get; set; }
            internal int GuidMismatches { get; set; }
            internal int MissingCharacters { get; set; }
            internal int UnselectableCharacters { get; set; }
            internal int MissingHouseholds { get; set; }
            internal int DuplicateHouseholds { get; set; }

            internal bool IsVisualRegistryLoading => !RegistryAvailable || RegistryEntries == 0;

            internal int RejectedEntries => NullVisuals + InactiveVisuals + HiddenVisuals
                + GuidMismatches + MissingCharacters + UnselectableCharacters
                + MissingHouseholds + DuplicateHouseholds;
        }

        private sealed class HouseholdChoiceEntry : VirtualPanel.IEntry
        {
            private readonly HouseholdChoice _choice;

            internal HouseholdChoiceEntry(HouseholdChoice choice)
            {
                _choice = choice;
            }

            public bool IsAvailable => _choice?.IsAvailable == true;

            public string Describe()
            {
                return IsAvailable ? _choice.Describe() : null;
            }

            public void Activate()
            {
                OpenHouseholdCommands(_choice);
            }
        }

        private sealed class CreateHouseholdNativeEntry : VirtualPanel.IEntry
        {
            public bool IsAvailable => IsTutorialHouseholdCreationAvailable();

            public string Describe()
            {
                return Loc.Get("explorer_create_household_command");
            }

            public void Activate()
            {
                CreateTutorialHouseholdFromWindow();
            }
        }

        private sealed class StorytellerChoice
        {
            internal StorytellerChoice(
                Storyteller storyteller,
                ObjectModel objectRepresentation,
                ulong characterRepresentationGuid)
            {
                Storyteller = storyteller;
                ObjectRepresentation = objectRepresentation;
                CharacterRepresentationGuid = characterRepresentationGuid;
            }

            internal Storyteller Storyteller { get; }
            internal ObjectModel ObjectRepresentation { get; }
            internal ulong CharacterRepresentationGuid { get; }
            internal string Name => GameText("Storyteller_" + Storyteller.DisplayName);
            internal bool HasRepresentation => ObjectRepresentation?.IsAvailable == true
                || ResolveCharacterVisual() != null;
            internal bool IsAvailable => Storyteller != null
                && GetPlayer()?.IsInSelectStoryteller == true;

            internal string Describe()
            {
                string identity = DescribeStoryteller(Storyteller);
                if (ObjectRepresentation?.IsAvailable == true)
                {
                    return Loc.Get(
                        "objects_description",
                        identity,
                        Loc.Get(
                            "explorer_storyteller_object_representation",
                            ObjectRepresentation.GetDisplayName(),
                            ObjectRepresentation.GetPath(),
                            WorldTargeting.DescribeFromCamera(ObjectRepresentation.Position)));
                }

                CharacterVisual visual = ResolveCharacterVisual();
                AssetCharacter character = visual != null
                    ? CharacterManager.Instance?.GetCharacterByGUID(CharacterRepresentationGuid)
                    : null;
                return character?.Data == null
                    ? identity
                    : Loc.Get(
                        "objects_description",
                        identity,
                        Loc.Get(
                            "explorer_storyteller_para_representation",
                            character.Data.FullName,
                            Loc.Get("explorer_loaded_world_representation"),
                            WorldTargeting.DescribeFromCamera(visual.transform.position)));
            }

            private CharacterVisual ResolveCharacterVisual()
            {
                CharacterVisual visual;
                CharacterManager manager = CharacterManager.Instance;
                return CharacterRepresentationGuid != 0UL
                    && manager?.LoadedCharacterVisuals != null
                    && manager.LoadedCharacterVisuals.TryGetValue(
                        CharacterRepresentationGuid, out visual)
                    && IsActiveCharacterVisual(CharacterRepresentationGuid, visual)
                        ? visual
                        : null;
            }
        }

        private sealed class StorytellerChoiceEntry : VirtualPanel.IEntry
        {
            private readonly StorytellerChoice _choice;

            internal StorytellerChoiceEntry(StorytellerChoice choice)
            {
                _choice = choice;
            }

            public bool IsAvailable => _choice?.IsAvailable == true;

            public string Describe()
            {
                return IsAvailable ? _choice.Describe() : null;
            }

            public void Activate()
            {
                SelectStoryteller(_choice?.Storyteller);
            }
        }

        private sealed class NameGroupEntry : VirtualPanel.IEntry
        {
            private readonly string _finderName;
            private readonly string _key;
            private readonly List<ObjectModel> _objects;
            private readonly bool _openActionDirectly;

            internal NameGroupEntry(
                string finderName,
                string key,
                List<ObjectModel> objects,
                bool openActionDirectly)
            {
                _finderName = finderName;
                _key = key;
                _objects = objects;
                _openActionDirectly = openActionDirectly;
            }

            public bool IsAvailable => _objects != null && _objects.Count > 0;

            public string Describe()
            {
                string key = _key == "#" ? Loc.Get("explorer_name_other") : _key;
                return Loc.Get("explorer_name_group", key, _objects.Count);
            }

            public void Activate()
            {
                string key = _key == "#" ? Loc.Get("explorer_name_other") : _key;
                ShowObjectList(
                    Loc.Get("panel_summary", _finderName, key),
                    _objects,
                    pushCurrent: true,
                    openActionDirectly: _openActionDirectly);
            }
        }

        private sealed class FacilityNameGroupEntry : VirtualPanel.IEntry
        {
            private readonly string _finderName;
            private readonly string _key;
            private readonly List<FacilityChoice> _choices;

            internal FacilityNameGroupEntry(
                string finderName,
                string key,
                List<FacilityChoice> choices)
            {
                _finderName = finderName;
                _key = key;
                _choices = choices;
            }

            public bool IsAvailable => _choices != null
                && _choices.Exists(choice => choice?.IsAvailable == true);

            public string Describe()
            {
                string key = _key == "#" ? Loc.Get("explorer_name_other") : _key;
                int count = _choices?.FindAll(
                    choice => choice?.IsAvailable == true).Count ?? 0;
                return Loc.Get("explorer_facility_name_group", key, count);
            }

            public void Activate()
            {
                string key = _key == "#" ? Loc.Get("explorer_name_other") : _key;
                var available = _choices.FindAll(
                    choice => choice?.IsAvailable == true);
                ShowFacilityList(
                    Loc.Get("panel_summary", _finderName, key),
                    available,
                    pushCurrent: true);
            }
        }

        private sealed class FacilityEntry : VirtualPanel.IEntry
        {
            private readonly FacilityChoice _choice;

            internal FacilityEntry(FacilityChoice choice)
            {
                _choice = choice;
            }

            public bool IsAvailable => _choice?.IsAvailable == true;

            public string Describe()
            {
                if (_choice == null)
                {
                    return null;
                }
                if (_choice.IsShopProduct)
                {
                    return _choice.DescribeShopProduct();
                }

                ObjectModel target = _choice.ResolveTarget(requireActionMenu: false);
                return target == null ? null : new ObjectEntry(target).Describe();
            }

            public void Activate()
            {
                ObjectModel target = _choice?.ResolveTarget(
                    requireActionMenu: UseActionOnlyView);
                if (target == null)
                {
                    Speak(Loc.Get("explorer_target_gone"));
                    return;
                }

                OpenObjectCommands(
                    target,
                    _choice.IsShopProduct ? _choice.ProductLabel : null);
            }
        }

        private sealed class ObjectEntry : VirtualPanel.IEntry
        {
            private readonly ObjectModel _item;
            private readonly bool _openActionDirectly;

            internal ObjectEntry(ObjectModel item, bool openActionDirectly = false)
            {
                _item = item;
                _openActionDirectly = openActionDirectly;
            }

            public bool IsAvailable => ShouldIncludeObject(_item);

            public string Describe()
            {
                ItemObjectRoot root = _item?.Resolve();
                if (root == null)
                {
                    return null;
                }

                var parts = new List<string>();
                int tutorialTarget;
                string tutorialDescription;
                if (TutorialHandler.TryGetPreferredWorldTarget(
                    out tutorialTarget, out tutorialDescription)
                    && tutorialTarget == _item.InstanceId)
                {
                    AddPart(parts, Loc.Get("explorer_tutorial_target", tutorialDescription));
                }
                parts.Add(_item.GetDisplayName());
                string functions = LotReadinessReader.DescribeFacilityKinds(_item.Facilities);
                if (!string.IsNullOrEmpty(functions))
                {
                    parts.Add(Loc.Get("explorer_object_function", functions));
                }
                AddPart(parts, _item.GetPath());
                AddPart(parts, ItemDescription.DescribePlacementRelation(root));
                AddPart(parts, WorldTargeting.DescribeFromCamera(_item.Position));
                Storyteller storyteller = GetPlayer()?.IsInSelectStoryteller == true
                    ? FindStoryteller(root)
                    : null;
                if (IsCreateHouseholdTarget(root))
                {
                    parts.Add(Loc.Get("explorer_create_household_available"));
                }
                else if (storyteller != null)
                {
                    parts.Add(Loc.Get("explorer_storyteller_choice_available"));
                }
                else
                {
                    parts.Add(_item.HasCurrentActionMenu(GetPlayer())
                        ? Loc.Get("explorer_actions_available")
                        : Loc.Get("explorer_actions_unavailable"));
                }

                string description = string.Join(
                    Loc.Get("lot_detail_separator"), parts.ToArray());
                Plugin.Log.LogInfo(
                    $"[Explorer] focus type=item, instance={_item.InstanceId}, lot={_lotGuid}, "
                  + $"path='{_item.GetPath()}', name='{_item.Name}', "
                  + $"category='{_item.Category}', "
                  + $"facilities={_item.Facilities}, position={_item.Position}.");
                return description;
            }

            public void Activate()
            {
                if (!_openActionDirectly)
                {
                    OpenObjectCommands(_item);
                    return;
                }

                ItemObjectRoot root = _item?.Resolve();
                if (root == null)
                {
                    Speak(Loc.Get("explorer_target_gone"));
                    return;
                }

                // Keep an existing follow camera on its observed Para. Fixed-shot ZoomTo is useful
                // only when no follow is active because the game clears follow inside ZoomTo.
                Player player = GetPlayer();
                if (!ShouldRetainCameraFollow(
                    player, "quick-action", _item.GetDisplayName()))
                {
                    WorldTargeting.FrameItem(root, player, "quick-action");
                }
                OpenItemInteraction(_item);
            }
        }

        private sealed class ParaEntry : IParaSelectionEntry
        {
            private readonly ParaModel _para;

            internal ParaEntry(ParaModel para)
            {
                _para = para;
            }

            public bool IsAvailable => ShouldIncludePara(_para);

            public void ToggleSelection()
            {
                ToggleParaSelection(_para);
            }

            public string Describe()
            {
                AssetCharacter character = _para?.Resolve();
                if (character == null)
                {
                    return null;
                }

                var parts = new List<string> { _para.GetDisplayName() };
                AddPart(parts, _para.GetPath());
                AddPart(parts, WorldTargeting.DescribeFromCamera(_para.Position));
                Player player = GetPlayer();
                if (player?.IsInSelectHouseholdTutorial != true
                    && player?.IsInSelectStoryteller != true
                    && IsSelectablePara(character)
                    && player?.SelectedCharactersGUID != null)
                {
                    string selectionState = player.SelectedCharactersGUID.Contains(character.GUID)
                        ? Loc.Get("explorer_selected")
                        : Loc.Get("explorer_not_selected");
                    parts.Add(Loc.Get("explorer_para_selection_state", selectionState));
                }
                if (GetPlayer()?.IsInSelectHouseholdTutorial == true
                    && GetVisualBackedHouseholdChoice(character) != null)
                {
                    parts.Add(Loc.Get("explorer_household_choice_available"));
                }
                else if (GetPlayer()?.IsInSelectStoryteller == true
                    && FindStoryteller(character) != null)
                {
                    parts.Add(Loc.Get("explorer_storyteller_choice_available"));
                }
                else
                {
                    parts.Add(_para.HasCurrentActionMenu(GetPlayer())
                        ? Loc.Get("explorer_actions_available")
                        : Loc.Get("explorer_actions_unavailable"));
                }
                Plugin.Log.LogInfo(
                    $"[Explorer] focus type=para, character={_para.Guid}, lot={_lotGuid}, "
                  + $"path='{_para.GetPath()}', name='{_para.Name}', "
                  + $"position={_para.Position}.");
                return string.Join(Loc.Get("lot_detail_separator"), parts.ToArray());
            }

            public void Activate()
            {
                OpenParaCommands(_para);
            }
        }

        private sealed class CommandEntry : VirtualPanel.IEntry
        {
            private readonly Func<bool> _isAvailable;
            private readonly Func<string> _describe;
            private readonly Action _activate;

            internal CommandEntry(
                Func<bool> isAvailable, Func<string> describe, Action activate)
            {
                _isAvailable = isAvailable;
                _describe = describe;
                _activate = activate;
            }

            public bool IsAvailable => _isAvailable != null && _isAvailable();

            public string Describe()
            {
                return _describe?.Invoke();
            }

            public void Activate()
            {
                _activate?.Invoke();
            }
        }

        /// <summary>
        /// One facility-list choice. Ordinary facilities retain one physical target; exact
        /// PurchaseItem equivalents share one product choice while every source object remains in
        /// the explorer model and therefore in the explicit all-object view.
        /// </summary>
        private sealed class FacilityChoice
        {
            private readonly List<ObjectModel> _targets = new List<ObjectModel>();
            private readonly string _productSignature;

            private FacilityChoice(
                ObjectModel target,
                bool isShopProduct,
                string productSignature,
                string productLabel)
            {
                if (target != null)
                {
                    _targets.Add(target);
                }
                IsShopProduct = isShopProduct;
                _productSignature = productSignature;
                ProductLabel = productLabel;
            }

            internal bool IsShopProduct { get; }
            internal string ProductLabel { get; }
            internal string SortName => IsShopProduct
                ? ProductLabel
                : _targets.Count > 0 ? _targets[0].Name : string.Empty;
            internal bool IsAvailable => FindFirstAvailable(UseActionOnlyView) != null;

            internal static FacilityChoice ForPhysicalFacility(ObjectModel target)
            {
                return new FacilityChoice(target, false, null, null);
            }

            internal static FacilityChoice ForShopProduct(
                ObjectModel target, string signature, string label)
            {
                return new FacilityChoice(target, true, signature, label);
            }

            internal bool TryAddEquivalentProduct(
                ObjectModel target, string signature)
            {
                if (!IsShopProduct || target == null || string.IsNullOrEmpty(signature)
                    || !string.Equals(
                        _productSignature, signature, StringComparison.Ordinal))
                {
                    return false;
                }

                _targets.Add(target);
                return true;
            }

            internal bool ContainsInstance(int instanceId)
            {
                return instanceId >= 0
                    && _targets.Exists(target => target?.InstanceId == instanceId);
            }

            internal ObjectModel ResolveTarget(bool requireActionMenu)
            {
                var candidates = new List<ObjectModel>();
                foreach (ObjectModel target in _targets)
                {
                    if (IsCurrentFacilityTarget(target))
                    {
                        candidates.Add(target);
                    }
                }
                candidates.Sort(CompareObjectsFromCamera);

                Player player = GetPlayer();
                foreach (ObjectModel target in candidates)
                {
                    if (!requireActionMenu || target.HasCurrentActionMenu(player))
                    {
                        return target;
                    }
                }
                return null;
            }

            internal string DescribeShopProduct()
            {
                ObjectModel target = ResolveTarget(UseActionOnlyView)
                    ?? ResolveTarget(requireActionMenu: false);
                ItemObjectRoot root = target?.Resolve();
                if (root == null)
                {
                    return null;
                }

                var parts = new List<string>();
                AddPart(parts, ProductLabel);
                int copies = CountCurrentProductCopies();
                if (copies > 1)
                {
                    parts.Add(Loc.Get("shop_product_identical_displays", copies));
                }

                string functions = LotReadinessReader.DescribeFacilityKinds(target.Facilities);
                if (!string.IsNullOrEmpty(functions))
                {
                    parts.Add(Loc.Get("explorer_object_function", functions));
                }
                AddPart(parts, target.GetPath());
                AddPart(parts, WorldTargeting.DescribeFromCamera(target.Position));
                parts.Add(target.HasCurrentActionMenu(GetPlayer())
                    ? Loc.Get("explorer_actions_available")
                    : Loc.Get("explorer_actions_unavailable"));

                Plugin.Log.LogInfo(
                    $"[Explorer/facility] type=shop-product, label='{ProductLabel}', "
                  + $"catalogue={root.CatalogueGUID}, skin={root.CurrentSkinGUID}, "
                  + $"displays={copies}, target={target.InstanceId}, path='{target.GetPath()}'.");
                return string.Join(Loc.Get("lot_detail_separator"), parts.ToArray());
            }

            private ObjectModel FindFirstAvailable(bool requireActionMenu)
            {
                Player player = GetPlayer();
                foreach (ObjectModel target in _targets)
                {
                    if (IsCurrentFacilityTarget(target)
                        && (!requireActionMenu || target.HasCurrentActionMenu(player)))
                    {
                        return target;
                    }
                }
                return null;
            }

            private bool IsCurrentFacilityTarget(ObjectModel target)
            {
                ItemObjectRoot root = target?.Resolve();
                if (root == null)
                {
                    return false;
                }
                if (IsShopProduct)
                {
                    return root.IsForSale;
                }
                return target.Facilities != LotReadinessReader.FacilityKind.None;
            }

            private int CountCurrentProductCopies()
            {
                int count = 0;
                foreach (ObjectModel target in _targets)
                {
                    ItemObjectRoot root = target?.Resolve();
                    if (root != null && root.IsForSale)
                    {
                        count++;
                    }
                }
                return count;
            }
        }

        private sealed class ExplorerModel
        {
            private readonly ulong _lotGuid;
            private readonly AssetLot _lot;
            private readonly LotPerimeterZoneObject _perimeter;
            private List<FacilityChoice> _facilityChoices;
            private readonly List<ulong> _facilitySelection = new List<ulong>();

            private ExplorerModel(
                ulong lotGuid, AssetLot lot, LotPerimeterZoneObject perimeter)
            {
                _lotGuid = lotGuid;
                _lot = lot;
                _perimeter = perimeter;
            }

            internal List<FloorModel> Floors { get; } = new List<FloorModel>();
            internal List<ObjectModel> Objects { get; } = new List<ObjectModel>();
            internal List<ParaModel> Paras { get; } = new List<ParaModel>();
            internal ulong LotGuid => _lotGuid;
            internal int RoomCount { get; private set; }
            internal int OrdinaryVisualParaCount { get; private set; }
            internal int IntroVisualParaCount { get; private set; }
            internal int TravelDestinationParaCount { get; private set; }

            internal static ExplorerModel Build(ulong lotGuid, Player player)
            {
                AssetLot lot = GetLoadedLot(lotGuid);
                if (lot == null)
                {
                    return null;
                }

                LotPerimeterZoneObject perimeter = ZoneManager.Instance?
                    .GetLotPerimeterZoneObjectByGUID(lotGuid);
                var model = new ExplorerModel(lotGuid, lot, perimeter);

                List<ZoneObject> zones = ZoneManager.Instance?.GetLotZones(lotGuid);
                if (zones != null)
                {
                    foreach (ZoneObject zone in zones)
                    {
                        ZoneWallObject room = zone as ZoneWallObject;
                        if (!IsUsableRoom(room, lotGuid))
                        {
                            continue;
                        }
                        FloorModel floor = model.GetOrCreateFloor(room.Layer);
                        floor.Areas.Add(new AreaModel(model, floor, room));
                        model.RoomCount++;
                    }
                }

                foreach (FloorModel floor in model.Floors)
                {
                    floor.AssignRoomOrdinals();
                }

                List<ItemObjectRoot> roots = ItemManager.Instance?.GetLotItems(lotGuid);
                if (roots != null)
                {
                    ItemObjectRoot preview = player?.ItemInPlacement?.Item;
                    foreach (ItemObjectRoot root in roots)
                    {
                        if (root == null || root.gameObject == null
                            || !root.gameObject.activeInHierarchy
                            || root.LotPlacedOnGUID != lotGuid || root == preview)
                        {
                            continue;
                        }

                        var item = new ObjectModel(model, root);
                        model.Objects.Add(item);
                        FloorModel floor = model.GetOrCreateFloor(root.Layer);
                        AreaModel area = model.FindRoomForItem(root)
                            ?? floor.GetOrCreateOutdoor();
                        area.Objects.Add(item);
                        item.Area = area;
                    }
                }

                CharacterManager characterManager = CharacterManager.Instance;
                var addedParaGuids = new HashSet<ulong>();
                if (characterManager?.Characters != null)
                {
                    foreach (AssetCharacter character in characterManager.Characters)
                    {
                        bool isTravelDestination =
                            TownMapLotHandler.IsCharacterAtFollowedTravelDestination(
                                player, character, lotGuid);
                        if (!IsUsablePara(character)
                            || (!model.IsCharacterOnLot(character) && !isTravelDestination))
                        {
                            continue;
                        }

                        model.AddPara(
                            character,
                            isIntroVisual: false,
                            isTravelDestination: isTravelDestination);
                        addedParaGuids.Add(character.GUID);
                    }
                }

                // The native world selector targets active CharacterVisual instances. Saved
                // Position/OnLotPerimeterZone can lag behind that presentation, so union in every
                // visual whose transform is inside this exact ordinary lot perimeter.
                if (characterManager?.LoadedCharacterVisuals != null)
                {
                    foreach (KeyValuePair<ulong, CharacterVisual> entry in
                        characterManager.LoadedCharacterVisuals)
                    {
                        if (addedParaGuids.Contains(entry.Key))
                        {
                            continue;
                        }

                        AssetCharacter character = characterManager.GetCharacterByGUID(entry.Key);
                        if (!IsUsableVisualPara(character)
                            || !model.IsOrdinaryVisualPara(character, entry.Value))
                        {
                            continue;
                        }

                        model.AddPara(
                            character, isIntroVisual: false,
                            isOrdinaryVisualFallback: true);
                        addedParaGuids.Add(character.GUID);
                    }
                }

                // UpdateDrawSelectionBox does not seed its candidates from Characters. It walks
                // LoadedCharacterVisuals and resolves each GUID, which is significant on the train:
                // the temporary special-lot membership data is incomplete even though the actors
                // are loaded and selectable. Keep this as a separate intro-only union because the
                // train has no ordinary perimeter in which to validate a visual position.
                if (model.CanUseIntroVisualParas(player)
                    && characterManager?.LoadedCharacterVisuals != null)
                {
                    foreach (KeyValuePair<ulong, CharacterVisual> entry in
                        characterManager.LoadedCharacterVisuals)
                    {
                        if (addedParaGuids.Contains(entry.Key))
                        {
                            continue;
                        }

                        AssetCharacter character = characterManager.GetCharacterByGUID(entry.Key);
                        if (!IsUsableVisualPara(character)
                            || !model.IsIntroVisualPara(character, player, entry.Value))
                        {
                            continue;
                        }

                        model.AddPara(character, isIntroVisual: true);
                        addedParaGuids.Add(character.GUID);
                    }
                }

                int currentLayer = player != null ? player.GetLotLayer(lotGuid) : 0;
                FloorModel currentFloor = model.GetOrCreateFloor(currentLayer);
                if (currentFloor.Areas.Count == 0 || currentLayer == 0)
                {
                    currentFloor.GetOrCreateOutdoor();
                }

                model.Floors.Sort((a, b) => a.Layer.CompareTo(b.Layer));
                foreach (FloorModel floor in model.Floors)
                {
                    floor.AssignRoomOrdinals();
                    floor.Areas.Sort((a, b) => a.SortKey.CompareTo(b.SortKey));
                }
                model.AssignDuplicateOrdinals();
                model.AssignParaOrdinals();
                return model;
            }

            internal List<ObjectModel> GetObjects(FindMode mode)
            {
                var result = new List<ObjectModel>();
                foreach (ObjectModel item in Objects)
                {
                    if (!item.IsAvailable)
                    {
                        continue;
                    }
                    bool needsActionCheck = UseActionOnlyView
                        || mode == FindMode.Interactable;
                    bool hasActionMenu = !needsActionCheck
                        || item.HasCurrentActionMenu(GetPlayer());
                    if (UseActionOnlyView && !hasActionMenu)
                    {
                        continue;
                    }
                    if (mode == FindMode.Facilities
                        && item.Facilities == LotReadinessReader.FacilityKind.None)
                    {
                        continue;
                    }
                    if (mode == FindMode.Interactable && !hasActionMenu)
                    {
                        int tutorialTarget;
                        string ignored;
                        if (!TutorialHandler.TryGetPreferredWorldTarget(
                            out tutorialTarget, out ignored)
                            || item.InstanceId != tutorialTarget)
                        {
                            continue;
                        }
                    }
                    result.Add(item);
                }
                return result;
            }

            internal int CountObjects(FindMode mode)
            {
                return GetObjects(mode).Count;
            }

            internal List<FacilityChoice> GetFacilityChoices()
            {
                Player player = GetPlayer();
                if (_facilityChoices == null || !FacilitySelectionMatches(player))
                {
                    BuildFacilityChoices(player);
                }
                return new List<FacilityChoice>(_facilityChoices);
            }

            internal int CountAvailableFacilityChoices()
            {
                int count = 0;
                foreach (FacilityChoice choice in GetFacilityChoices())
                {
                    if (choice?.IsAvailable == true)
                    {
                        count++;
                    }
                }
                return count;
            }

            private void BuildFacilityChoices(Player player)
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var choices = new List<FacilityChoice>();
                var groupedProducts = new Dictionary<string, FacilityChoice>(
                    StringComparer.Ordinal);
                int physicalTargets = 0;
                int shopDisplays = 0;
                int shopProductEntries = 0;

                foreach (ObjectModel item in Objects)
                {
                    ItemObjectRoot root = item?.Resolve();
                    if (root == null)
                    {
                        continue;
                    }

                    LotReadinessReader.FacilityKind kinds = item.Facilities;
                    if (kinds == LotReadinessReader.FacilityKind.None)
                    {
                        continue;
                    }
                    physicalTargets++;

                    bool pureItemPurchase = kinds
                        == LotReadinessReader.FacilityKind.ShopPurchase
                        && root.IsForSale;
                    if (!pureItemPurchase)
                    {
                        choices.Add(FacilityChoice.ForPhysicalFacility(item));
                        continue;
                    }

                    shopDisplays++;
                    string signature = GetPurchaseProductSignature(root);
                    FacilityChoice product;
                    if (!string.IsNullOrEmpty(signature)
                        && groupedProducts.TryGetValue(signature, out product)
                        && product.TryAddEquivalentProduct(item, signature))
                    {
                        continue;
                    }

                    string label = ItemDescription.GetShopProductLabel(root)
                        ?? item.Name;
                    product = FacilityChoice.ForShopProduct(item, signature, label);
                    choices.Add(product);
                    shopProductEntries++;
                    if (!string.IsNullOrEmpty(signature))
                    {
                        groupedProducts[signature] = product;
                    }
                }

                _facilityChoices = choices;
                CaptureFacilitySelection(player);
                stopwatch.Stop();
                Plugin.Log.LogInfo(
                    $"[Explorer/facilities] snapshot lot={_lotGuid}, "
                  + $"objectsScanned={Objects.Count}, physicalTargets={physicalTargets}, "
                  + $"entries={choices.Count}, shopDisplays={shopDisplays}, "
                  + $"shopProducts={shopProductEntries}, "
                  + $"collapsed={shopDisplays - shopProductEntries}, "
                  + $"elapsedMs={stopwatch.ElapsedMilliseconds}.");
            }

            private bool FacilitySelectionMatches(Player player)
            {
                List<ulong> selected = player?.SelectedCharactersGUID;
                if (selected == null || selected.Count != _facilitySelection.Count)
                {
                    return selected == null && _facilitySelection.Count == 0;
                }
                for (int i = 0; i < selected.Count; i++)
                {
                    if (selected[i] != _facilitySelection[i])
                    {
                        return false;
                    }
                }
                return true;
            }

            private void CaptureFacilitySelection(Player player)
            {
                _facilitySelection.Clear();
                if (player?.SelectedCharactersGUID == null)
                {
                    return;
                }
                _facilitySelection.AddRange(player.SelectedCharactersGUID);
            }

            /// <summary>
            /// Creates a canonical copy of everything PurchaseItem transfers except the physical
            /// display's identity and placement. Complex slotted/roped objects remain separate so
            /// grouping can never hide different contained or connected items.
            /// </summary>
            private static string GetPurchaseProductSignature(ItemObjectRoot root)
            {
                AssetLotDataItem data = ItemManager.Instance?.RootToAsset(root);
                if (data == null
                    || HasItems(data.ItemPlacementSlots)
                    || HasItems(data.ItemChairSlots)
                    || HasItems(data.ItemBedSlot)
                    || data.ItemRopeAInstanceID != 0
                    || data.ItemRopeBInstanceID != 0
                    || data.ItemRopeGUID != 0UL)
                {
                    return null;
                }

                ulong forSaleState = Settings.Get<ItemStates>()?.ItemStateForSale ?? 0UL;
                if (data.ItemStates != null && forSaleState != 0UL)
                {
                    data.ItemStates.RemoveAll(
                        state => state != null && state.GUID == forSaleState);
                }
                data.ItemStates?.Sort(CompareStateValues);
                data.ItemStatesTargets?.Sort(CompareStateValues);
                data.ItemMeshes?.Sort(CompareMeshes);
                data.ItemPictures?.Sort(ComparePictures);
                data.ItemTexts?.Sort(CompareTexts);
                data.ItemSlotGUIDs?.Sort(CompareItemSlots);

                data.InstanceID = 0;
                data.Position = Vector3.zero;
                data.Rotation = Quaternion.identity;
                data.Layer = 0;
                data.PlacedOn = new AssetLotDataItemPlacement();
                data.SegmentSnappedToID = 0;
                data.PlacementSlotPlacedOnGUID = 0UL;
                data.ChairSlotPlacedOnGUID = 0UL;
                data.LotLayerGUID = 0UL;
                return JsonUtility.ToJson(data);
            }

            private static bool HasItems<T>(List<T> items)
            {
                return items != null && items.Count > 0;
            }

            private static int CompareStateValues(
                AssetLotDataStateValues a, AssetLotDataStateValues b)
            {
                int guid = CompareNullableGuid(a?.GUID, b?.GUID, a, b);
                return guid != 0 ? guid : (a?.Value ?? 0f).CompareTo(b?.Value ?? 0f);
            }

            private static int CompareMeshes(AssetLotDataMeshes a, AssetLotDataMeshes b)
            {
                int guid = CompareNullableGuid(a?.GUID, b?.GUID, a, b);
                if (guid != 0)
                {
                    return guid;
                }
                return (a?.MeshGUID ?? 0UL).CompareTo(b?.MeshGUID ?? 0UL);
            }

            private static int ComparePictures(
                AssetLotDataPictures a, AssetLotDataPictures b)
            {
                int guid = CompareNullableGuid(a?.GUID, b?.GUID, a, b);
                if (guid != 0)
                {
                    return guid;
                }
                return (a?.PictureGUID ?? 0UL).CompareTo(b?.PictureGUID ?? 0UL);
            }

            private static int CompareTexts(AssetLotDataText a, AssetLotDataText b)
            {
                int guid = CompareNullableGuid(a?.GUID, b?.GUID, a, b);
                if (guid != 0)
                {
                    return guid;
                }
                return string.Compare(a?.Text, b?.Text, StringComparison.Ordinal);
            }

            private static int CompareItemSlots(
                AssetLotDataItemSlots a, AssetLotDataItemSlots b)
            {
                int key = CompareNullableGuid(a?.Key, b?.Key, a, b);
                return key != 0 ? key : (a?.Value ?? 0UL).CompareTo(b?.Value ?? 0UL);
            }

            private static int CompareNullableGuid<T>(
                ulong? aGuid, ulong? bGuid, T a, T b)
                where T : class
            {
                if (a == null || b == null)
                {
                    return ReferenceEquals(a, b) ? 0 : a == null ? -1 : 1;
                }
                return aGuid.GetValueOrDefault().CompareTo(bGuid.GetValueOrDefault());
            }

            internal bool ContainsExactItem(ItemObjectRoot root)
            {
                if (root == null || root.LotPlacedOnGUID != _lotGuid
                    || !WorldTargeting.IsSameLiveItem(root, root.InstanceID, _lotGuid))
                {
                    return false;
                }
                List<ItemObjectRoot> roots = ItemManager.Instance?.GetLotItems(_lotGuid);
                return roots != null && roots.Contains(root);
            }

            internal bool ContainsExactPara(AssetCharacter character)
            {
                return character != null && IsUsableVisualPara(character)
                    && ((character.IsVisualLoaded && IsCharacterOnLot(character))
                        || IsOrdinaryVisualPara(character)
                        || IsIntroVisualPara(character, GetPlayer())
                        || TownMapLotHandler.IsCharacterAtFollowedTravelDestination(
                            GetPlayer(), character, _lotGuid))
                    && AssetManager.Instance?.GetCharacter(character.GUID) == character;
            }

            internal AreaModel FindAreaForItem(ItemObjectRoot root)
            {
                return root == null
                    ? null
                    : FindRoomForItem(root) ?? GetOrCreateFloor(root.Layer).GetOrCreateOutdoor();
            }

            internal AreaModel FindAreaForPara(AssetCharacter character)
            {
                if (character?.Data == null)
                {
                    return null;
                }
                int layer = character.Data.OnZoneWallObject != null
                    ? character.Data.OnZoneWallObject.Layer
                    : 0;
                return FindRoomForPara(character)
                    ?? GetOrCreateFloor(layer).GetOrCreateOutdoor();
            }

            internal AreaModel FindArea(
                int layer, Vector3 position, ZoneWallObject preferredRoom)
            {
                FloorModel floor = FindFloor(layer);
                if (floor == null)
                {
                    return null;
                }

                if (preferredRoom != null)
                {
                    foreach (AreaModel area in floor.Areas)
                    {
                        if (area.Room == preferredRoom)
                        {
                            return area;
                        }
                    }
                }

                AreaModel best = null;
                foreach (AreaModel area in floor.Areas)
                {
                    if (area.Room == null || !area.IsAvailable)
                    {
                        continue;
                    }
                    try
                    {
                        if (MathService.ZoneObjectContainsPoint(area.Room, position)
                            && (best == null
                                || area.Room.SurfaceArea < best.Room.SurfaceArea))
                        {
                            best = area;
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogError(
                            $"Checking room {area.Room.InstanceID} membership threw: {ex}");
                    }
                }

                return best ?? floor.GetOrCreateOutdoor();
            }

            internal Vector3 GetOutdoorAnchor(FloorModel floor)
            {
                Vector3 total = Vector3.zero;
                int count = 0;
                AreaModel outdoors = floor.FindOutdoor();
                if (outdoors != null)
                {
                    foreach (ObjectModel item in outdoors.Objects)
                    {
                        if (item.IsAvailable)
                        {
                            total += item.Position;
                            count++;
                        }
                    }
                    foreach (ParaModel para in outdoors.Paras)
                    {
                        if (para.IsAvailable)
                        {
                            total += para.Position;
                            count++;
                        }
                    }
                }

                if (count > 0)
                {
                    Vector3 average = total / count;
                    average.y = _perimeter != null
                        ? _perimeter.AveragePosition.y
                        : average.y;
                    return average;
                }
                return _perimeter != null ? _perimeter.AveragePosition : GetCameraPosition();
            }

            private FloorModel GetOrCreateFloor(int layer)
            {
                FloorModel floor = FindFloor(layer);
                if (floor == null)
                {
                    floor = new FloorModel(this, layer);
                    Floors.Add(floor);
                }
                return floor;
            }

            private FloorModel FindFloor(int layer)
            {
                foreach (FloorModel floor in Floors)
                {
                    if (floor.Layer == layer)
                    {
                        return floor;
                    }
                }
                return null;
            }

            private AreaModel FindRoomForItem(ItemObjectRoot root)
            {
                ZoneWallObject preferred = root.FloorPlacedOn?.WallZone;
                AreaModel area = FindArea(
                    root.Layer, WorldTargeting.GetItemPosition(root), preferred);
                return area?.Room != null ? area : null;
            }

            private AreaModel FindRoomForPara(AssetCharacter character)
            {
                int layer = character.Data.OnZoneWallObject != null
                    ? character.Data.OnZoneWallObject.Layer
                    : 0;
                AreaModel area = FindArea(
                    layer,
                    WorldTargeting.GetCharacterPosition(character),
                    character.Data.OnZoneWallObject);
                return area?.Room != null ? area : null;
            }

            private bool IsCharacterOnLot(AssetCharacter character)
            {
                if (character?.Data == null)
                {
                    return false;
                }
                if (character.Data.OnLotPerimeterZone != null
                    && character.Data.OnLotPerimeterZone.RealLotGUID == _lotGuid)
                {
                    return true;
                }
                if (_perimeter != null)
                {
                    try
                    {
                        return MathService.ZoneObjectContainsPoint(
                            _perimeter, character.Data.Position);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogError("Checking a Para's lot membership threw: " + ex);
                    }
                }
                return false;
            }

            private bool IsOrdinaryVisualPara(AssetCharacter character)
            {
                CharacterVisual visual;
                CharacterManager manager = CharacterManager.Instance;
                return manager?.LoadedCharacterVisuals != null
                    && character != null
                    && manager.LoadedCharacterVisuals.TryGetValue(character.GUID, out visual)
                    && IsOrdinaryVisualPara(character, visual);
            }

            private bool IsOrdinaryVisualPara(
                AssetCharacter character, CharacterVisual visual)
            {
                if (!IsUsableVisualPara(character)
                    || !IsActiveCharacterVisual(character.GUID, visual)
                    || ZoneManager.Instance == null)
                {
                    return false;
                }

                try
                {
                    LotPerimeterZoneObject perimeter;
                    return ZoneManager.Instance.TryGetLotPerimeterZoneAtPosition(
                            visual.transform.position, out perimeter)
                        && perimeter != null && perimeter.RealLotGUID == _lotGuid;
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError(
                        "Checking a live Para visual's lot membership threw: " + ex);
                    return false;
                }
            }

            /// <summary>
            /// Admits the visually present, game-selectable Paras on the train intro's special lot.
            /// </summary>
            /// <remarks>
            /// The train is a verified special-lot exception: its current-household Paras and
            /// conductor have active entries in
            /// <c>CharacterManager.LoadedCharacterVisuals</c>, but their saved
            /// <c>OnLotPerimeterZone</c> does not identify the temporary special lot. The game's own
            /// <c>UpdateDrawSelectionBox</c> handles that scene by iterating this visual registry and
            /// accepting current-household members plus <c>IsPartOfTutorialHousehold</c> while in
            /// the intro. Mirroring that predicate makes the generic Paras category truthful
            /// without admitting arbitrary actors from another lot.
            /// </remarks>
            private bool IsIntroVisualPara(AssetCharacter character, Player player)
            {
                CharacterVisual visual;
                CharacterManager manager = CharacterManager.Instance;
                return manager?.LoadedCharacterVisuals != null
                    && character != null
                    && manager.LoadedCharacterVisuals.TryGetValue(character.GUID, out visual)
                    && IsIntroVisualPara(character, player, visual);
            }

            private bool IsIntroVisualPara(
                AssetCharacter character, Player player, CharacterVisual visual)
            {
                if (!CanUseIntroVisualParas(player) || character?.Data == null)
                {
                    return false;
                }

                bool isCurrentHouseholdMember = IsCurrentHouseholdPara(character);
                bool isTutorialConductor = TutorialHandler.IsMultiselectStep
                    && character.IsPartOfTutorialHousehold;
                if (!isCurrentHouseholdMember && !isTutorialConductor)
                {
                    return false;
                }

                return IsActiveCharacterVisual(character.GUID, visual);
            }

            private bool CanUseIntroVisualParas(Player player)
            {
                AssetLot specialLot = LotManager.Instance?.CurrentSpecialLot;
                return player?.IsInIntro == true && specialLot != null
                    && specialLot.GUID == _lotGuid;
            }

            private void AddPara(
                AssetCharacter character,
                bool isIntroVisual,
                bool isOrdinaryVisualFallback = false,
                bool isTravelDestination = false)
            {
                if (isIntroVisual)
                {
                    IntroVisualParaCount++;
                }
                if (isOrdinaryVisualFallback)
                {
                    OrdinaryVisualParaCount++;
                }
                if (isTravelDestination)
                {
                    TravelDestinationParaCount++;
                }

                int layer = character.Data.OnZoneWallObject != null
                    ? character.Data.OnZoneWallObject.Layer
                    : 0;
                var para = new ParaModel(this, character);
                Paras.Add(para);
                FloorModel floor = GetOrCreateFloor(layer);
                AreaModel area = FindRoomForPara(character)
                    ?? floor.GetOrCreateOutdoor();
                area.Paras.Add(para);
                para.Area = area;
            }

            private void AssignDuplicateOrdinals()
            {
                var sorted = new List<ObjectModel>(Objects);
                sorted.Sort(CompareObjectsByName);
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (ObjectModel item in sorted)
                {
                    int count;
                    counts.TryGetValue(item.DuplicateIdentity, out count);
                    counts[item.DuplicateIdentity] = count + 1;
                }
                var positions = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (ObjectModel item in sorted)
                {
                    int ordinal;
                    positions.TryGetValue(item.DuplicateIdentity, out ordinal);
                    ordinal++;
                    positions[item.DuplicateIdentity] = ordinal;
                    item.DuplicateOrdinal = ordinal;
                    item.DuplicateCount = counts[item.DuplicateIdentity];
                }
            }

            private void AssignParaOrdinals()
            {
                var sorted = new List<ParaModel>(Paras);
                sorted.Sort(CompareParasByName);
                var counts = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
                foreach (ParaModel para in sorted)
                {
                    int count;
                    counts.TryGetValue(para.Name, out count);
                    counts[para.Name] = count + 1;
                }
                var positions = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
                foreach (ParaModel para in sorted)
                {
                    int ordinal;
                    positions.TryGetValue(para.Name, out ordinal);
                    ordinal++;
                    positions[para.Name] = ordinal;
                    para.DuplicateOrdinal = ordinal;
                    para.DuplicateCount = counts[para.Name];
                }
            }

            private static bool IsUsableRoom(ZoneWallObject room, ulong lotGuid)
            {
                return room != null && room.gameObject != null
                    && room.gameObject.activeInHierarchy && !room.IsGlobal
                    && room.LotPlacedOnGUID == lotGuid && room.Segments != null
                    && room.Segments.Count >= 2
                    && !float.IsNaN(room.AveragePosition.x)
                    && !float.IsInfinity(room.AveragePosition.x);
            }

            private static bool IsUsablePara(AssetCharacter character)
            {
                return IsUsableVisualPara(character) && character.IsVisualLoaded;
            }

            private static bool IsUsableVisualPara(AssetCharacter character)
            {
                return character?.Data != null
                    && !character.IsDummy && !character.DoNotLoadVisual
                    && !character.Data.IsDeadOrTakenAway;
            }
        }

        private sealed class FloorModel
        {
            private readonly ExplorerModel _model;

            internal FloorModel(ExplorerModel model, int layer)
            {
                _model = model;
                Layer = layer;
            }

            internal int Layer { get; }
            internal List<AreaModel> Areas { get; } = new List<AreaModel>();

            internal int GetVisibleAreaCount(bool actionOnly)
            {
                int count = 0;
                foreach (AreaModel area in Areas)
                {
                    if (area.IsAvailable
                        && (!actionOnly
                            || area.GetVisibleObjectCount(actionOnly: true) > 0
                            || area.GetVisibleParaCount(actionOnly: true) > 0))
                    {
                        count++;
                    }
                }
                return count;
            }

            internal int GetVisibleObjectCount(bool actionOnly)
            {
                int count = 0;
                foreach (AreaModel area in Areas)
                {
                    count += area.GetVisibleObjectCount(actionOnly);
                }
                return count;
            }

            internal int GetVisibleParaCount(bool actionOnly)
            {
                int count = 0;
                foreach (AreaModel area in Areas)
                {
                    count += area.GetVisibleParaCount(actionOnly);
                }
                return count;
            }

            internal AreaModel GetOrCreateOutdoor()
            {
                AreaModel outdoors = FindOutdoor();
                if (outdoors == null)
                {
                    outdoors = new AreaModel(_model, this, null);
                    Areas.Add(outdoors);
                }
                return outdoors;
            }

            internal AreaModel FindOutdoor()
            {
                foreach (AreaModel area in Areas)
                {
                    if (area.Room == null)
                    {
                        return area;
                    }
                }
                return null;
            }

            internal void AssignRoomOrdinals()
            {
                var rooms = new List<AreaModel>();
                foreach (AreaModel area in Areas)
                {
                    if (area.Room != null)
                    {
                        rooms.Add(area);
                    }
                }
                rooms.Sort((a, b) =>
                {
                    int x = a.Room.AveragePosition.x.CompareTo(b.Room.AveragePosition.x);
                    if (x != 0)
                    {
                        return x;
                    }
                    int z = a.Room.AveragePosition.z.CompareTo(b.Room.AveragePosition.z);
                    return z != 0 ? z : a.Room.InstanceID.CompareTo(b.Room.InstanceID);
                });
                for (int i = 0; i < rooms.Count; i++)
                {
                    rooms[i].RoomOrdinal = i + 1;
                    rooms[i].RoomCount = rooms.Count;
                }
            }
        }

        private sealed class AreaModel
        {
            private readonly ExplorerModel _model;
            private readonly FloorModel _floor;

            internal AreaModel(
                ExplorerModel model, FloorModel floor, ZoneWallObject room)
            {
                _model = model;
                _floor = floor;
                Room = room;
            }

            internal ZoneWallObject Room { get; }
            internal int Layer => _floor.Layer;
            internal int RoomOrdinal { get; set; }
            internal int RoomCount { get; set; }
            internal int SortKey => Room != null ? RoomOrdinal : int.MaxValue;
            internal List<ObjectModel> Objects { get; } = new List<ObjectModel>();
            internal List<ParaModel> Paras { get; } = new List<ParaModel>();
            internal bool IsAvailable => Room == null
                || (Room.gameObject != null && Room.gameObject.activeInHierarchy
                    && Room.LotPlacedOnGUID == _model.LotGuid);

            internal string GetName()
            {
                return Room == null
                    ? Loc.Get("explorer_outdoors")
                    : Loc.Get("explorer_unnamed_room", RoomOrdinal, RoomCount);
            }

            internal Vector3 GetAnchor()
            {
                return Room != null ? Room.AveragePosition : _model.GetOutdoorAnchor(_floor);
            }

            internal int GetVisibleObjectCount(bool actionOnly)
            {
                int count = 0;
                foreach (ObjectModel item in Objects)
                {
                    ItemObjectRoot root = item?.Root;
                    if (root == null || _model.FindAreaForItem(root) != this
                        || (actionOnly && !item.HasCurrentActionMenu(GetPlayer())))
                    {
                        continue;
                    }
                    count++;
                }
                return count;
            }

            internal int GetVisibleParaCount(bool actionOnly)
            {
                int count = 0;
                foreach (ParaModel para in Paras)
                {
                    AssetCharacter character = para?.Character;
                    if (character == null || _model.FindAreaForPara(character) != this)
                    {
                        continue;
                    }
                    if (actionOnly)
                    {
                        if (!para.HasCurrentActionMenu(GetPlayer()))
                        {
                            continue;
                        }
                    }
                    count++;
                }
                return count;
            }

            internal string Describe(bool actionOnly)
            {
                var parts = new List<string>
                {
                    Loc.Get(
                        "explorer_area_entry",
                        GetName(),
                        Count(
                            "explorer_object_count_one", "explorer_object_count_many",
                            GetVisibleObjectCount(actionOnly)),
                        Count(
                            "explorer_para_count_one", "explorer_para_count_many",
                            GetVisibleParaCount(actionOnly)))
                };
                if (Room != null)
                {
                    AddPart(parts, Loc.Get(
                        "explorer_area_surface",
                        ScreenReader.NormalizeForOutput(
                            MeasurementUnitService.FormatSurfaceArea(Room.SurfaceArea))));
                }

                string contents = DescribeKeyContents(actionOnly);
                if (!string.IsNullOrEmpty(contents))
                {
                    parts.Add(Loc.Get("explorer_area_contents", contents));
                }
                AddPart(parts, WorldTargeting.DescribeFromCamera(GetAnchor()));
                return string.Join(Loc.Get("lot_detail_separator"), parts.ToArray());
            }

            internal string DescribeKeyContents(bool actionOnly = false)
            {
                var values = new List<string>();
                LotReadinessReader.FacilityKind facilities =
                    LotReadinessReader.FacilityKind.None;
                foreach (ObjectModel item in Objects)
                {
                    if (!actionOnly || ShouldIncludeObject(item))
                    {
                        facilities |= item.Facilities;
                    }
                }
                string facilityText = LotReadinessReader.DescribeFacilityKinds(facilities);
                AddPart(values, facilityText);

                var names = new List<string>();
                foreach (ObjectModel item in Objects)
                {
                    if (actionOnly && !ShouldIncludeObject(item))
                    {
                        continue;
                    }
                    if (names.Count >= 3)
                    {
                        break;
                    }
                    string usefulName = !string.IsNullOrEmpty(item.Category)
                        ? item.Category
                        : item.Name;
                    if (!string.IsNullOrEmpty(usefulName) && !names.Contains(usefulName))
                    {
                        names.Add(usefulName);
                    }
                }
                if (values.Count == 0 && names.Count > 0)
                {
                    values.AddRange(names);
                }
                return values.Count == 0
                    ? null
                    : string.Join(Loc.Get("list_separator"), values.ToArray());
            }
        }

        private sealed class ObjectModel
        {
            private readonly ExplorerModel _model;
            private readonly ItemObjectRoot _root;
            private readonly ulong _catalogueGuid;
            private readonly ulong _skinGuid;
            private readonly LotReadinessReader.FacilityKind _physicalFacilities;
            private int _actionabilityFrame = -1;
            private bool _hasCurrentActionMenu;
            private int _civicFacilityFrame = -1;
            private LotReadinessReader.FacilityKind _civicFacilities;

            internal ObjectModel(ExplorerModel model, ItemObjectRoot root)
            {
                _model = model;
                _root = root;
                _catalogueGuid = root.CatalogueGUID;
                _skinGuid = root.CurrentSkinGUID;
                InstanceId = root.InstanceID;
                Name = ItemDescription.GetProductIdentity(root) ?? Loc.Get("item_unknown");
                Category = ItemDescription.GetCategory(root);
                _physicalFacilities = LotReadinessReader.ClassifyFacility(root);
            }

            internal int InstanceId { get; }
            internal string Name { get; }
            internal string Category { get; }
            internal LotReadinessReader.FacilityKind Facilities
            {
                get
                {
                    int frame = Time.frameCount;
                    if (_civicFacilityFrame != frame)
                    {
                        _civicFacilityFrame = frame;
                        _civicFacilities = LotReadinessReader.ClassifyCivicFacility(
                            Resolve(), GetPlayer());
                    }
                    return _physicalFacilities | _civicFacilities;
                }
            }
            internal AreaModel Area { get; set; }
            internal int DuplicateOrdinal { get; set; }
            internal int DuplicateCount { get; set; }
            internal string DuplicateIdentity => _catalogueGuid != 0UL
                ? _catalogueGuid.ToString() + "/" + _skinGuid.ToString()
                : "instance/" + InstanceId.ToString();
            internal ItemObjectRoot Root => Resolve();
            internal bool IsAvailable => Resolve() != null;
            internal Vector3 Position
            {
                get
                {
                    ItemObjectRoot root = Resolve();
                    return root != null ? WorldTargeting.GetItemPosition(root) : Vector3.zero;
                }
            }

            internal ItemObjectRoot Resolve()
            {
                return _root != null && _root.InstanceID == InstanceId
                    && _model.ContainsExactItem(_root)
                        ? _root
                        : null;
            }

            /// <summary>
            /// Reads native menu availability at most once per frame. A large community lot can
            /// contain more than a thousand objects, and one panel description asks for several
            /// overlapping counts. The frame boundary deliberately invalidates the answer so a
            /// later state or Para-selection change is never treated as permanent capability.
            /// </summary>
            internal bool HasCurrentActionMenu(Player player)
            {
                int frame = Time.frameCount;
                if (_actionabilityFrame != frame)
                {
                    _actionabilityFrame = frame;
                    _hasCurrentActionMenu =
                        GetItemActionBlocker(Resolve(), player) == null;
                }
                return _hasCurrentActionMenu;
            }

            internal string GetDisplayName()
            {
                string productName = DuplicateCount > 1
                    ? Loc.Get("objects_copy", Name, DuplicateOrdinal, DuplicateCount)
                    : Name;
                return string.IsNullOrEmpty(Category)
                    ? productName
                    : Loc.Get("label_with_detail", productName, Category);
            }

            internal string GetPath()
            {
                ItemObjectRoot root = Resolve();
                AreaModel area = root != null ? _model.FindAreaForItem(root) : Area;
                int layer = root != null ? root.Layer : Area.Layer;
                return Loc.Get(
                    "explorer_path",
                    Loc.Get("explorer_floor", layer),
                    area?.GetName() ?? Loc.Get("explorer_outdoors"));
            }
        }

        private sealed class ParaModel
        {
            private readonly ExplorerModel _model;
            private readonly AssetCharacter _character;
            private int _actionabilityFrame = -1;
            private bool _hasCurrentActionMenu;

            internal ParaModel(ExplorerModel model, AssetCharacter character)
            {
                _model = model;
                _character = character;
                Guid = character.GUID;
                Name = string.IsNullOrWhiteSpace(character.Data.FullName)
                    ? Loc.Get("focus_unlabeled")
                    : character.Data.FullName.Trim();
            }

            internal ulong Guid { get; }
            internal string Name { get; }
            internal AreaModel Area { get; set; }
            internal int DuplicateOrdinal { get; set; }
            internal int DuplicateCount { get; set; }
            internal AssetCharacter Character => Resolve();
            internal bool IsAvailable => Resolve() != null;
            internal Vector3 Position
            {
                get
                {
                    AssetCharacter character = Resolve();
                    return WorldTargeting.GetCharacterPosition(character);
                }
            }

            internal AssetCharacter Resolve()
            {
                return _character != null && _character.GUID == Guid
                    && _model.ContainsExactPara(_character)
                        ? _character
                        : null;
            }

            internal bool HasCurrentActionMenu(Player player)
            {
                int frame = Time.frameCount;
                if (_actionabilityFrame != frame)
                {
                    _actionabilityFrame = frame;
                    InteractionGroup ignored;
                    _hasCurrentActionMenu =
                        GetParaActionBlocker(Resolve(), player, out ignored) == null;
                }
                return _hasCurrentActionMenu;
            }

            internal string GetDisplayName()
            {
                return DuplicateCount > 1
                    ? Loc.Get("objects_copy", Name, DuplicateOrdinal, DuplicateCount)
                    : Name;
            }

            internal string GetPath()
            {
                AssetCharacter character = Resolve();
                AreaModel area = character != null
                    ? _model.FindAreaForPara(character)
                    : Area;
                int layer = character?.Data?.OnZoneWallObject != null
                    ? character.Data.OnZoneWallObject.Layer
                    : 0;
                return Loc.Get(
                    "explorer_path",
                    Loc.Get("explorer_floor", layer),
                    area?.GetName() ?? Loc.Get("explorer_outdoors"));
            }
        }
    }
}
