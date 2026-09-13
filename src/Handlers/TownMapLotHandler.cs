using System;
using System.Collections.Generic;
using ParalivesAccess.Speech;
using Setting;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ParalivesAccess.Handlers
{
    /// <summary>
    /// Lists the town's lots on the L key, so a lot can be inspected, visited and bought without
    /// a mouse.
    /// </summary>
    /// <remarks>
    /// THE PROGRESS BLOCKER AFTER THE CHARACTER CREATOR. Confirming a household leaves the game
    /// demanding *"Purchase a House - click on house icons to see lot info"*, and live mode is
    /// unreachable until a lot is owned. Everything queued behind that - interactions, needs, skills -
    /// cannot even be tested.
    ///
    /// WHY A DEDICATED LIST RATHER THAN PANEL NAVIGATION. §21: every lot icon is a
    /// `UITownMapLotIcon : UIContainer : FocusableGroup` holding exactly one focusable, so the town
    /// map presents as ~80 identical single-item panels. Panel cycling is the wrong interaction here,
    /// and the game's own three collections are the documented read path instead.
    ///
    /// A map icon's `Clicked()` opens only `UILotInfo`; it never exposes travel. For an unvisited
    /// visitable lot, the game instead attaches `GROUP_LotImpostor` to the lot asset's
    /// `ImpostorObject`, and a physical world click passes that `ItemObjectRoot` to
    /// `UIInteractions.Show`. Its native Visit row performs the load and travel. Once the lot is
    /// already loaded, the corresponding native route is the ordinary floor interaction menu at a
    /// pathfindable point in that lot. This handler exposes those two game-owned menus and retains
    /// Lot Info as a separate command; it does not reproduce either action.
    ///
    /// L is a town explorer, not one eighty-row mixed list. Its first level is built from exact game
    /// roles (visitable lot type, household owner, live for-sale marker), plus browse-by-lot-type and
    /// all-lots fallbacks. Categories deliberately overlap: a household can also be visitable, just
    /// as F7's action/facility/all finders can expose the same object for different purposes.
    /// ALL LOTS remains cheapest first (user decision, 2026-08-11); owned and non-purchasable lots
    /// remain included there.
    /// </remarks>
    public static class TownMapLotHandler
    {
        private const float TravelArrivalRadius = 5f;
        private const float TravelDepartureRadius = 20f;

        /// <summary>Set false to disable the lot list entirely.</summary>
        public static bool Enabled { get; set; } = true;

        private static VirtualPanel _panel;
        private static readonly Stack<ViewState> _history = new Stack<ViewState>();
        private static PendingTravelMenu _pendingTravelMenu;
        private static ActiveTravelFollow _activeTravelFollow;
        private static bool _nativeWindowSuspensionLogged;
        private static bool _nativeWindowLookupFailureLogged;

        private sealed class ViewState
        {
            public VirtualPanel Panel;
            public VirtualPanel.IEntry Preferred;
        }

        /// <summary>
        /// The native interaction menu most recently opened from an L destination command.
        /// </summary>
        /// <remarks>
        /// Opening the menu is not travel, so this is only a claim token. The state reporter
        /// consumes it when an action from this exact bound menu is queued, and camera follow still
        /// waits until that queue entry reaches Running.
        /// </remarks>
        private sealed class PendingTravelMenu
        {
            public UIInteractions Menu;
            public int PlayerIndex;
            public ulong LotGuid;
            public ulong RootGroupGuid;
            public int ItemInstanceId;
            public Vector3 WorldPosition;
        }

        /// <summary>
        /// The destination which produced the game's current character-follow state.
        /// </summary>
        /// <remarks>
        /// `FindRandomPositionInLot(... NearFrontSegment)` deliberately finishes beside a lot's
        /// front segment. On open lots that point can be on town terrain just outside the perimeter,
        /// leaving both `OnLotPerimeterZone` and the camera raycast lot empty. Retaining the exact
        /// claimed L transaction gives F7 a truthful narrow fallback without treating every
        /// followed character or every outdoor position as belonging to the owned home.
        /// </remarks>
        private sealed class ActiveTravelFollow
        {
            public int PlayerIndex;
            public ulong QueueGuid;
            public ulong ActorGuid;
            public ulong LotGuid;
            public Vector3 DestinationPosition;
            public AssetCharacter Actor;
            public bool HasArrived;
        }

        private enum LotSort
        {
            Name,
            Value
        }

        private enum TravelRoute
        {
            None,
            VisitImpostor,
            GoHere
        }

        /// <summary>True while this handler owns the keyboard.</summary>
        public static bool OwnsInput
        {
            get
            {
                if (_panel == null)
                {
                    return false;
                }

                UIWindow ignored;
                return !TryGetNativeForegroundWindow(null, out ignored);
            }
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

                MaintainActiveTravelFollow(hybridPlayer?.Player);

                if (hybridPlayer == null || hybridPlayer.HasInputFieldFocused)
                {
                    return;
                }

                Keyboard keyboard = Keyboard.current;
                if (keyboard == null)
                {
                    return;
                }

                // The L browser is an accessibility overlay, not a game modal. A real native
                // CurrentWindow must take precedence while it is visible. TEST 76 left the town
                // browser open behind UIEscapeMenu and UIPrompt; because OwnsInput remained true,
                // arrows continued alternating Go here / View lot information instead of moving
                // through the visible prompt. Suspend rather than discard the browser so closing
                // the temporary modal returns the user to the exact town-map context they had.
                UIWindow nativeForeground;
                if (TryGetNativeForegroundWindow(hybridPlayer, out nativeForeground))
                {
                    if (_panel != null && !_nativeWindowSuspensionLogged)
                    {
                        _nativeWindowSuspensionLogged = true;
                        Plugin.Log.LogInfo(
                            "[TownMap] input suspended behind native foreground window "
                          + $"'{nativeForeground?.GetType().Name ?? "unknown"}'.");
                    }
                    return;
                }

                if (_nativeWindowSuspensionLogged)
                {
                    _nativeWindowSuspensionLogged = false;
                    Plugin.Log.LogInfo("[TownMap] input resumed after native foreground window closed.");
                }

                if (keyboard.lKey.wasPressedThisFrame)
                {
                    Toggle(hybridPlayer);
                    return;
                }

                if (_panel == null)
                {
                    return;
                }

                // The list is a snapshot, and the town map is not: a lot can be bought, sold or
                // scrolled out of the icon pool underneath it. Closing on a state change is safer
                // than navigating a stale list.
                if (!IsTownMapUsable(hybridPlayer))
                {
                    Close();
                    return;
                }

                HandleInput(hybridPlayer);
            }
            catch (Exception ex)
            {
                // Runs inside the game's loop; contain it rather than let it escape into game code.
                Enabled = false;
                Plugin.Log.LogError("TownMapLotHandler disabled after an exception: " + ex);
            }
        }

        private static void Toggle(HybridPlayer hybridPlayer)
        {
            if (_panel != null)
            {
                Close();
                return;
            }

            if (!IsTownMapUsable(hybridPlayer))
            {
                ScreenReader.Speak(Loc.Get("lots_unavailable"));
                return;
            }

            _history.Clear();
            _panel = Build(hybridPlayer);
            if (_panel == null)
            {
                ScreenReader.Speak(Loc.Get("lots_none"));
                return;
            }

            _panel.Enter();
            ScreenReader.Speak(Loc.Get("lots_details_hint"), interrupt: false);
        }

        /// <summary>F3 reads the actual L-browser selection's name/address, tooltip and description.</summary>
        internal static bool TryGetContext(out string panelName, out string description, out bool isLot)
        {
            panelName = null;
            description = null;
            isLot = false;
            HybridPlayer player = PlayerManager.Instance?.HybridPlayer1;
            if (!Enabled || !OwnsInput || player == null || !IsTownMapUsable(player)) return false;
            panelName = _panel.Name;
            LotEntry lot = _panel.Current as LotEntry;
            // The commands page belongs to the lot selected on the preceding list page.
            if (lot == null)
                foreach (ViewState state in _history)
                    if (state.Preferred is LotEntry previous) { lot = previous; break; }
            isLot = lot != null;
            description = lot != null
                ? (lot.IsAvailable ? lot.DescribeDetails() : Loc.Get("lots_target_gone"))
                : _panel.Current?.Describe();
            return true;
        }

        private static void Close()
        {
            ClearPanelOwnership();
            ScreenReader.Speak(Loc.Get("lots_closed"));
        }

        private static void ClearPanelOwnership()
        {
            _panel = null;
            _history.Clear();
            _nativeWindowSuspensionLogged = false;
        }

        /// <summary>
        /// Resolves the same native foreground priority as Back.Update: common UI first, then the
        /// local player's UI manager. UITownMap itself is the background that the L browser
        /// augments, so it is not a competing foreground window.
        /// </summary>
        private static bool TryGetNativeForegroundWindow(
            HybridPlayer hybridPlayer, out UIWindow foreground)
        {
            foreground = null;
            try
            {
                UIManager common = HybridReferences.Instance?.UIManagerCommon;
                UIWindow candidate = common?.CurrentWindow;
                if (candidate != null && candidate.IsVisible && !(candidate is UITownMap))
                {
                    foreground = candidate;
                    _nativeWindowLookupFailureLogged = false;
                    return true;
                }

                HybridPlayer player = hybridPlayer
                    ?? PlayerManager.Instance?.HybridPlayer1;
                candidate = player?.UIManager?.CurrentWindow;
                if (candidate != null && candidate.IsVisible && !(candidate is UITownMap))
                {
                    foreground = candidate;
                    _nativeWindowLookupFailureLogged = false;
                    return true;
                }

                _nativeWindowLookupFailureLogged = false;
                return false;
            }
            catch (Exception ex)
            {
                // Fail closed: uncertainty about native foreground ownership must never let the
                // invisible virtual list steal input from a visible game modal.
                if (!_nativeWindowLookupFailureLogged)
                {
                    _nativeWindowLookupFailureLogged = true;
                    Plugin.Log.LogError(
                        "Reading native foreground ownership for the town map threw: " + ex);
                }
                return true;
            }
        }

        /// <summary>
        /// True when the town map is showing and its icons can be trusted.
        /// </summary>
        /// <remarks>
        /// `IsInTownMap` is the game's own predicate (§21) and is a CAMERA STATE, not a window - it is
        /// true when there is no household yet, during a fast travel, or whenever the camera is zoomed
        /// past `CameraRatioToShowTownMap`. That first case is exactly the post-creator "buy a house"
        /// situation this exists for.
        /// </remarks>
        private static bool IsTownMapUsable(HybridPlayer hybridPlayer)
        {
            // External game API: the predicate touches camera and household state.
            try
            {
                return hybridPlayer.IsInTownMap();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Reading IsInTownMap threw: " + ex);
                return false;
            }
        }

        /// <summary>
        /// Builds the town explorer's role-based root, or the direct purchase list during the
        /// initial household flow.
        /// </summary>
        /// <remarks>
        /// `TownMapIcons` is a POOL, not a live list (§21): `CreateLotIcon` reuses entries with
        /// `!IsVisible` and `UpdateLotsIcons` prunes only `!IsValid`. Both filters are applied, or
        /// dead pool slots would be counted and announced as lots.
        ///
        /// Price comes from `LotManager.GetLotValue(lotGUID, forceUseCached: true)` - a number, so it
        /// can be sorted. The cached form is used deliberately: the uncached path recomputes a lot's
        /// value, which is not something a list build should trigger ~80 times.
        /// </remarks>
        private static VirtualPanel Build(HybridPlayer hybridPlayer)
        {
            UITownMap map = UI.Instance != null && hybridPlayer.Player != null
                ? UI.GetOrNull<UITownMap>(hybridPlayer.Player.PlayerIndex)
                : null;

            if (map == null || map.TownMapIcons == null)
            {
                return null;
            }

            var entries = new List<LotEntry>();

            foreach (UITownMapLotIcon icon in map.TownMapIcons)
            {
                if (icon == null || !icon.IsValid || !icon.IsVisible)
                {
                    continue;
                }

                entries.Add(new LotEntry(icon, LotManager.Instance.GetLotValue(icon.LotGUID, true)));
            }

            if (entries.Count == 0)
            {
                return null;
            }

            if (IsInitialLotPurchase())
            {
                List<LotEntry> sale = entries.FindAll(entry => entry.IsForSale);
                return BuildLotPanel(
                    sale.Count > 0 ? Loc.Get("lots_for_sale_category") : Loc.Get("lots_all_category"),
                    sale.Count > 0 ? sale : entries,
                    LotSort.Value);
            }

            List<LotEntry> visitable = entries.FindAll(entry => entry.CanBeVisited);
            List<LotEntry> households = entries.FindAll(entry => entry.HasHousehold);
            List<LotEntry> forSale = entries.FindAll(entry => entry.IsForSale);
            var lotTypes = new HashSet<ulong>();
            foreach (LotEntry entry in entries)
            {
                lotTypes.Add(entry.LotTypeGuid);
            }
            Plugin.Log.LogInfo(
                $"[TownMap] root lots={entries.Count}, visitable={visitable.Count}, "
              + $"households={households.Count}, forSale={forSale.Count}, "
              + $"lotTypes={lotTypes.Count}.");

            var root = new VirtualPanel(Loc.Get("lots_panel"));
            root.Add(new LotCollectionEntry(
                Loc.Get("lots_visitable_category"), visitable, LotSort.Name));
            root.Add(new LotCollectionEntry(
                Loc.Get("lots_households_category"), households, LotSort.Name));
            root.Add(new LotCollectionEntry(
                Loc.Get("lots_for_sale_category"), forSale, LotSort.Value));
            root.Add(new LotTypesEntry(entries));
            root.Add(new LotCollectionEntry(
                Loc.Get("lots_all_category"), entries, LotSort.Value));
            return root;
        }

        private static VirtualPanel BuildLotPanel(
            string name, List<LotEntry> source, LotSort sort)
        {
            var entries = new List<LotEntry>(source);
            if (sort == LotSort.Value)
            {
                entries.Sort((a, b) =>
                {
                    int value = a.Value.CompareTo(b.Value);
                    return value != 0 ? value : CompareLotNames(a, b);
                });
            }
            else
            {
                entries.Sort(CompareLotNames);
            }

            var panel = new VirtualPanel(name);
            foreach (LotEntry entry in entries)
            {
                panel.Add(entry);
            }
            return panel;
        }

        private static int CompareLotNames(LotEntry a, LotEntry b)
        {
            return StringComparer.CurrentCultureIgnoreCase.Compare(
                a?.SortName ?? string.Empty,
                b?.SortName ?? string.Empty);
        }

        private static void OpenLotCollection(
            string name, List<LotEntry> entries, LotSort sort)
        {
            if (entries == null || entries.Count == 0)
            {
                ScreenReader.Speak(Loc.Get("lots_none_in_category"));
                return;
            }

            PushPanel(BuildLotPanel(name, entries, sort));
        }

        private static void OpenLotTypes(List<LotEntry> entries)
        {
            var groups = new Dictionary<ulong, List<LotEntry>>();
            var names = new Dictionary<ulong, string>();
            foreach (LotEntry entry in entries)
            {
                if (entry == null || !entry.IsAvailable)
                {
                    continue;
                }

                List<LotEntry> group;
                if (!groups.TryGetValue(entry.LotTypeGuid, out group))
                {
                    group = new List<LotEntry>();
                    groups.Add(entry.LotTypeGuid, group);
                    names[entry.LotTypeGuid] = entry.LotTypeName;
                }
                group.Add(entry);
            }

            var typeGuids = new List<ulong>(groups.Keys);
            typeGuids.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(
                names[a], names[b]));
            var panel = new VirtualPanel(Loc.Get("lots_types_category"));
            foreach (ulong typeGuid in typeGuids)
            {
                panel.Add(new LotCollectionEntry(
                    names[typeGuid], groups[typeGuid], LotSort.Name));
            }
            PushPanel(panel);
        }

        private static void PushPanel(VirtualPanel next)
        {
            if (next == null)
            {
                return;
            }

            if (_panel != null)
            {
                _history.Push(new ViewState
                {
                    Panel = _panel,
                    Preferred = _panel.Current
                });
            }
            _panel = next;
            _panel.Enter();
        }

        private static void GoBack()
        {
            if (_history.Count == 0)
            {
                Close();
                return;
            }

            ViewState previous = _history.Pop();
            _panel = previous.Panel;
            _panel.Enter(previous.Preferred);
        }

        private static void HandleInput(HybridPlayer hybridPlayer)
        {
            // The window itself is a FocusableGroup, and ReadDirection only needs one for the game's
            // own four-direction folding helper.
            UIWindow window = UI.GetOrNull<UITownMap>(hybridPlayer.Player.PlayerIndex);
            Vector2 direction = window != null
                ? FocusNavigator.ReadDirection(window, hybridPlayer)
                : Vector2.zero;

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && keyboard.backspaceKey.wasPressedThisFrame)
            {
                GoBack();
                return;
            }

            if (direction.y < 0f || direction.x > 0f)
            {
                _panel.Move(1);
                return;
            }

            if (direction.y > 0f || direction.x < 0f)
            {
                _panel.Move(-1);
                return;
            }

            if (keyboard != null
                && (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame))
            {
                _panel.Activate();
            }
        }

        /// <summary>
        /// Opens the commands that genuinely exist for a lot without replacing Lot Info.
        /// </summary>
        private static void OpenLotCommands(LotEntry lot)
        {
            if (lot == null || !lot.IsAvailable)
            {
                ScreenReader.Speak(Loc.Get("lots_target_gone"));
                return;
            }

            TravelRoute route = lot.ResolveTravelRoute(null, null);

            // Preserve the established tutorial/home-purchase happy path: in that state the game
            // requires UILotInfo and there is no useful actor with which to travel. Ordinary play
            // gets an explicit command page so travel never replaces buy/switch/inspect.
            if (route == TravelRoute.None || IsInitialLotPurchase())
            {
                lot.OpenInfo();
                return;
            }

            var commands = new VirtualPanel(Loc.Get("lots_actions_panel", lot.GetShortLabel()));
            commands.Add(new TravelEntry(lot));
            commands.Add(new LotInfoEntry(lot));
            PushPanel(commands);
        }

        private static bool IsInitialLotPurchase()
        {
            try
            {
                HouseholdManager households = HouseholdManager.Instance;
                return households == null || !households.HasCurrentHousehold
                    || households.CurrentHousehold == null
                    || households.CurrentHousehold.IsTemporaryHousehold
                    || households.CurrentHousehold.JustCameOutOfTheIntro;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Checking initial lot-purchase state threw: " + ex);
                return false;
            }
        }

        private static void OpenTravelActions(LotEntry entry)
        {
            try
            {
                HybridPlayer hybridPlayer = PlayerManager.Instance?.HybridPlayer1;
                Player player = hybridPlayer?.Player;
                if (player == null || player.State != GameStates.LiveMode)
                {
                    ScreenReader.Speak(Loc.Get("lots_visit_live_required"));
                    return;
                }
                if (player.SelectedCharactersGUID == null
                    || player.SelectedCharactersGUID.Count == 0)
                {
                    ScreenReader.Speak(Loc.Get("lots_visit_para_required"));
                    return;
                }
                if (player.PlayerActionBlockedByUIWindow)
                {
                    ScreenReader.Speak(Loc.Get("lots_visit_ui_blocked"));
                    return;
                }

                ItemObjectRoot impostor = null;
                AssetLot lot = null;
                TravelRoute route = entry != null
                    ? entry.ResolveTravelRoute(value => impostor = value, value => lot = value)
                    : TravelRoute.None;
                if (route == TravelRoute.None || lot == null)
                {
                    ScreenReader.Speak(Loc.Get("lots_visit_unavailable"));
                    return;
                }

                if (route == TravelRoute.VisitImpostor)
                {
                    InteractionGroup group = InteractionManager.Instance
                        ?.GetItemInteractionGroup(impostor);
                    if (group == null || !InteractionManager.Instance.ItemHasInteractions(
                        impostor, player.PlayerIndex))
                    {
                        Plugin.Log.LogInfo(
                            $"[LotTravel] lot={lot.GUID}, route=impostor, instance="
                          + $"{impostor?.InstanceID ?? -1}, native menu unavailable.");
                        ScreenReader.Speak(Loc.Get("lots_visit_unavailable"));
                        return;
                    }

                    UIInteractions menu = NativeInteractionMenu.GetOrCreate(
                        player.PlayerIndex, "town-map-lot-impostor");
                    if (menu == null)
                    {
                        ScreenReader.Speak(Loc.Get("objects_open_failed"));
                        return;
                    }

                    Vector3 position = WorldTargeting.GetItemPosition(impostor);
                    Plugin.Log.LogInfo(
                        $"[LotTravel] lot={lot.GUID}, route=impostor, instance={impostor.InstanceID}, "
                      + $"group={group.GUID}, selectedCharacter={player.GetSelectedCharacterGUID()}.");
                    ClearPanelOwnership();
                    menu.Show(
                        group,
                        position,
                        impostor.InstanceID,
                        0UL,
                        impostor,
                        impostor.LotPlacedOnGUID);
                    ArmTravelCameraFollow(
                        menu,
                        player.PlayerIndex,
                        lot.GUID,
                        group.GUID,
                        impostor.InstanceID,
                        position);
                    return;
                }

                Interactions settings = Settings.Get<Interactions>();
                InteractionGroup floorGroup = settings?.GetInteractionGroupByGUID(
                    settings.FloorInteractions);
                if (floorGroup == null || PathfindingManager.Instance == null)
                {
                    ScreenReader.Speak(Loc.Get("lots_visit_unavailable"));
                    return;
                }

                var destination = PathfindingManager.Instance.FindRandomPositionInLot(
                    lot, PathfindingManager.FindRandomPositionValidPositions.NearFrontSegment);
                if (destination.result != PathfindingManager.FindRandomPositionInLotResult.Success)
                {
                    Plugin.Log.LogInfo(
                        $"[LotTravel] lot={lot.GUID}, route=go-here, target={destination.result}.");
                    ScreenReader.Speak(Loc.Get("lots_visit_unavailable"));
                    return;
                }

                bool hasFloorActions = false;
                foreach (ulong characterGuid in player.SelectedCharactersGUID)
                {
                    if (InteractionManager.Instance.CanShowInteractionGroupInInteractionList(
                        floorGroup, null, player.PlayerIndex, characterGuid, 0UL, lot.GUID))
                    {
                        hasFloorActions = true;
                        break;
                    }
                }
                if (!hasFloorActions)
                {
                    ScreenReader.Speak(Loc.Get("lots_visit_unavailable"));
                    return;
                }

                UIInteractions floorMenu = NativeInteractionMenu.GetOrCreate(
                    player.PlayerIndex, "town-map-lot-floor");
                if (floorMenu == null)
                {
                    ScreenReader.Speak(Loc.Get("objects_open_failed"));
                    return;
                }

                Plugin.Log.LogInfo(
                    $"[LotTravel] lot={lot.GUID}, route=go-here, group={floorGroup.GUID}, "
                  + $"selectedCharacter={player.GetSelectedCharacterGUID()}, "
                  + $"worldPosition={destination.position}.");
                ClearPanelOwnership();
                floorMenu.Show(floorGroup, destination.position, -1, 0UL, null, lot.GUID);
                ArmTravelCameraFollow(
                    floorMenu,
                    player.PlayerIndex,
                    lot.GUID,
                    floorGroup.GUID,
                    -1,
                    destination.position);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Opening native lot travel actions threw: " + ex);
                ScreenReader.Speak(Loc.Get("lots_visit_unavailable"));
            }
        }

        private static void ArmTravelCameraFollow(
            UIInteractions menu,
            int playerIndex,
            ulong lotGuid,
            ulong rootGroupGuid,
            int itemInstanceId,
            Vector3 worldPosition)
        {
            _pendingTravelMenu = new PendingTravelMenu
            {
                Menu = menu,
                PlayerIndex = playerIndex,
                LotGuid = lotGuid,
                RootGroupGuid = rootGroupGuid,
                ItemInstanceId = itemInstanceId,
                WorldPosition = worldPosition
            };
            Plugin.Log.LogInfo(
                $"[LotTravelCamera] armed player={playerIndex}, lot={lotGuid}, "
              + $"group={rootGroupGuid}, item={itemInstanceId}, position={worldPosition}.");
        }

        /// <summary>
        /// Claims camera follow only for an action chosen from the exact native menu opened by L.
        /// </summary>
        internal static bool TryClaimTravelCameraFollow(
            int playerIndex,
            InteractionTarget target,
            ulong lotGuid,
            out ulong destinationLotGuid,
            out Vector3 destinationPosition)
        {
            destinationLotGuid = 0UL;
            destinationPosition = default(Vector3);
            PendingTravelMenu pending = _pendingTravelMenu;
            if (pending == null)
            {
                return false;
            }

            try
            {
                UIInteractions current = UI.Instance != null
                    ? UI.GetOrNull<UIInteractions>(playerIndex)
                    : null;
                bool menuMatches = playerIndex == pending.PlayerIndex
                    && ReferenceEquals(current, pending.Menu)
                    && pending.Menu != null
                    && pending.Menu.IsVisible
                    && pending.Menu.CurrentRootInteractionGroup != null
                    && pending.Menu.CurrentRootInteractionGroup.GUID == pending.RootGroupGuid
                    && pending.Menu.ClickedItemInstanceID == pending.ItemInstanceId;
                bool lotMatches = lotGuid != 0UL && lotGuid == pending.LotGuid;
                bool positionMatches = IsFinite(target.WorldPosition)
                    && (target.WorldPosition - pending.WorldPosition).sqrMagnitude <= 0.25f;
                bool claimed = menuMatches && (lotMatches || positionMatches);
                if (claimed)
                {
                    destinationLotGuid = pending.LotGuid;
                    destinationPosition = IsFinite(target.WorldPosition)
                        ? target.WorldPosition
                        : pending.WorldPosition;

                    // The map icon can receive one final pointer-enter while the native
                    // interaction menu is closing. In TEST 76 that stale "For Sale, +1" cut off
                    // the more important "Go here started" result. This exact claimed-travel
                    // boundary is narrow enough to suppress only the disappearing map window.
                    SuppressTownMapTransitionHover(playerIndex);
                }

                Plugin.Log.LogInfo(
                    $"[LotTravelCamera] claim player={playerIndex}, expectedLot={pending.LotGuid}, "
                  + $"queuedLot={lotGuid}, menuMatches={menuMatches}, lotMatches={lotMatches}, "
                  + $"positionMatches={positionMatches}, claimed={claimed}, "
                  + $"destination={destinationPosition}.");
                _pendingTravelMenu = null;
                return claimed;
            }
            catch (Exception ex)
            {
                _pendingTravelMenu = null;
                Plugin.Log.LogError("Claiming town travel camera follow threw: " + ex);
                return false;
            }
        }

        private static void SuppressTownMapTransitionHover(int playerIndex)
        {
            try
            {
                FocusHandler.SuppressWindowHoverForTransition(
                    UI.GetOrNull<UITownMap>(playerIndex));
            }
            catch (Exception ex)
            {
                // Hover cleanup is ancillary; it must never invalidate a correctly claimed travel
                // action or prevent the follow camera from acquiring its destination.
                Plugin.Log.LogError(
                    "Preparing town-map transition hover suppression threw: " + ex);
            }
        }

        /// <summary>
        /// Retains the exact claimed destination once its concrete queue entry reaches Running.
        /// </summary>
        internal static void NotifyTravelCameraFollowStarted(
            int playerIndex,
            ulong queueGuid,
            ulong actorGuid,
            ulong lotGuid,
            Vector3 destinationPosition,
            AssetCharacter actor)
        {
            if (actorGuid == 0UL || lotGuid == 0UL || actor?.Data == null
                || !IsFinite(destinationPosition))
            {
                return;
            }

            _activeTravelFollow = new ActiveTravelFollow
            {
                PlayerIndex = playerIndex,
                QueueGuid = queueGuid,
                ActorGuid = actorGuid,
                LotGuid = lotGuid,
                DestinationPosition = destinationPosition,
                Actor = actor,
                HasArrived = IsWithinHorizontalRadius(
                    actor.Data.Position, destinationPosition, TravelArrivalRadius)
            };

            Plugin.Log.LogInfo(
                $"[LotTravelContext] active player={playerIndex}, queue={queueGuid}, "
              + $"actor={actorGuid}, lot={lotGuid}, destination={destinationPosition}, "
              + $"actorPosition={actor.Data.Position}, arrived={_activeTravelFollow.HasArrived}.");
        }

        /// <summary>Invalidates a claimed destination when its action stops or fails.</summary>
        internal static void NotifyTravelCameraFollowFailed(
            int playerIndex, ulong queueGuid, ulong actorGuid, string result)
        {
            ActiveTravelFollow active = _activeTravelFollow;
            if (active != null && active.PlayerIndex == playerIndex
                && active.QueueGuid == queueGuid && active.ActorGuid == actorGuid)
            {
                ClearActiveTravelFollow("action-" + (result ?? "failed"));
            }
        }

        /// <summary>
        /// Resolves the destination only while the same live actor is still the game's camera
        /// follow target and has reached the claimed world point.
        /// </summary>
        internal static bool TryGetFollowedTravelDestination(
            Player player,
            out ulong lotGuid,
            out ulong actorGuid,
            out string diagnostic)
        {
            lotGuid = 0UL;
            actorGuid = 0UL;
            diagnostic = null;

            ActiveTravelFollow before = _activeTravelFollow;
            MaintainActiveTravelFollow(player);
            ActiveTravelFollow active = _activeTravelFollow;
            if (active == null)
            {
                if (before != null)
                {
                    diagnostic = "the retained L destination was invalidated before F7 resolution";
                }
                return false;
            }

            AssetCharacter actor = active.Actor;
            float distance = HorizontalDistance(
                actor.Data.Position, active.DestinationPosition);
            if (!active.HasArrived)
            {
                diagnostic = $"retained L destination waiting for actor {active.ActorGuid}: "
                    + $"distance={distance:0.0}m, lot={active.LotGuid}";
                return false;
            }

            AssetLot lot = LotManager.Instance?.GetLotByGUID(active.LotGuid);
            if (lot == null && LotManager.Instance?.CurrentSpecialLot?.GUID != active.LotGuid)
            {
                diagnostic = $"retained L destination lot {active.LotGuid} is unavailable";
                return false;
            }

            lotGuid = active.LotGuid;
            actorGuid = active.ActorGuid;
            diagnostic = $"retained L destination matched actor {actorGuid}: "
                + $"distance={distance:0.0}m, lot={lotGuid}";
            return true;
        }

        /// <summary>
        /// Extends F7's Para membership only to the actor proven by the active L destination.
        /// </summary>
        internal static bool IsCharacterAtFollowedTravelDestination(
            Player player, AssetCharacter character, ulong lotGuid)
        {
            ulong destinationLot;
            ulong actorGuid;
            string ignored;
            return character != null
                && TryGetFollowedTravelDestination(
                    player, out destinationLot, out actorGuid, out ignored)
                && destinationLot == lotGuid && actorGuid == character.GUID;
        }

        private static void MaintainActiveTravelFollow(Player player)
        {
            ActiveTravelFollow active = _activeTravelFollow;
            if (active == null)
            {
                return;
            }

            if (GameLoadingManager.State != State.Game || player == null
                || player.State != GameStates.LiveMode || player.PlayerIndex != active.PlayerIndex)
            {
                ClearActiveTravelFollow("game-context-changed");
                return;
            }

            if (player.CameraCurrentCharacterFollowTarget != active.ActorGuid)
            {
                ClearActiveTravelFollow("camera-follow-changed");
                return;
            }

            AssetCharacter actor = CharacterManager.Instance?
                .GetCharacterByGUID(active.ActorGuid);
            if (actor?.Data == null || !ReferenceEquals(actor, active.Actor))
            {
                ClearActiveTravelFollow("actor-changed");
                return;
            }

            float distance = HorizontalDistance(
                actor.Data.Position, active.DestinationPosition);
            if (!active.HasArrived && distance <= TravelArrivalRadius)
            {
                active.HasArrived = true;
                Plugin.Log.LogInfo(
                    $"[LotTravelContext] arrived actor={active.ActorGuid}, lot={active.LotGuid}, "
                  + $"distance={distance:0.0}m, position={actor.Data.Position}.");
            }
            else if (active.HasArrived && distance > TravelDepartureRadius)
            {
                ClearActiveTravelFollow($"actor-left-destination-{distance:0.0}m");
            }
        }

        private static void ClearActiveTravelFollow(string reason)
        {
            ActiveTravelFollow active = _activeTravelFollow;
            if (active == null)
            {
                return;
            }

            Plugin.Log.LogInfo(
                $"[LotTravelContext] cleared player={active.PlayerIndex}, queue={active.QueueGuid}, "
              + $"actor={active.ActorGuid}, lot={active.LotGuid}, reason={reason}.");
            _activeTravelFollow = null;
        }

        private static bool IsWithinHorizontalRadius(
            Vector3 left, Vector3 right, float radius)
        {
            return HorizontalDistance(left, right) <= radius;
        }

        private static float HorizontalDistance(Vector3 left, Vector3 right)
        {
            float x = left.x - right.x;
            float z = left.z - right.z;
            return Mathf.Sqrt(x * x + z * z);
        }

        /// <summary>Discards an unclaimed L handoff when its native menu closes.</summary>
        internal static void NotifyInteractionMenuClosing(UIInteractions menu)
        {
            if (_pendingTravelMenu != null && ReferenceEquals(_pendingTravelMenu.Menu, menu))
            {
                Plugin.Log.LogInfo("[LotTravelCamera] native menu closed without a claim.");
                _pendingTravelMenu = null;
            }
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x)
                && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
                && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }

        /// <summary>One lot in the list.</summary>
        private sealed class LotEntry : VirtualPanel.IEntry
        {
            private readonly UITownMapLotIcon _icon;

            public LotEntry(UITownMapLotIcon icon, int value)
            {
                _icon = icon;
                Value = value;
                ReadClassification();
            }

            /// <summary>The lot's value, used only for sorting.</summary>
            public int Value { get; }

            internal bool IsForSale { get; private set; }

            internal bool HasHousehold { get; private set; }

            internal bool CanBeVisited { get; private set; }

            internal ulong LotTypeGuid { get; private set; }

            internal string LotTypeName { get; private set; }

            internal string SortName { get; private set; }

            public bool IsAvailable => _icon != null && _icon.IsValid && _icon.IsVisible;

            private void ReadClassification()
            {
                LotTypeName = Loc.Get("lots_unknown_type");
                SortName = GetShortLabel();
                try
                {
                    LotPerimeterZoneObject perimeter = ZoneManager.Instance
                        ?.GetLotPerimeterZoneObjectByGUID(_icon.LotGUID);
                    ulong householdGuid = perimeter?.HouseholdOwnerGUID ?? 0UL;
                    HasHousehold = householdGuid != 0UL;

                    string address = !string.IsNullOrWhiteSpace(perimeter?.DisplayName)
                        ? perimeter.DisplayName.Trim()
                        : null;
                    string householdName = null;
                    if (householdGuid != 0UL && AssetManager.Instance != null)
                    {
                        AssetHousehold household = AssetManager.Instance.GetAsset(householdGuid)
                            as AssetHousehold;
                        householdName = household?.Data?.GetHouseholdName();
                    }

                    AssetLot lot = LotManager.Instance?.GetLotByGUID(_icon.LotGUID);
                    LotTypes lotTypes = Settings.Get<LotTypes>();
                    LotType lotType = lot?.Data != null
                        ? lotTypes?.GetLotTypeByGUID(lot.Data.LotTypeGUID)
                        : null;
                    if (lotType != null)
                    {
                        LotTypeGuid = lotType.GUID;
                        CanBeVisited = lotType.CanBeVisitedByPlayer;
                        string localized = TranslationManager.Get(lotType.TranslationKey);
                        LotTypeName = string.IsNullOrWhiteSpace(localized)
                            ? lotType.DisplayName
                            : localized.Trim();
                    }

                    bool visibleForSale = _icon.LabelForSale != null
                        && _icon.LabelForSale.activeSelf;
                    IsForSale = visibleForSale || (!HasHousehold && lotType != null
                        && (lotType.CanPurchase || lotTypes.CanBuyAllLotTypes));

                    if (!string.IsNullOrWhiteSpace(householdName))
                    {
                        SortName = householdName.Trim();
                    }
                    else if (!string.IsNullOrWhiteSpace(address))
                    {
                        SortName = address;
                    }
                    else if (!string.IsNullOrWhiteSpace(LotTypeName))
                    {
                        SortName = LotTypeName;
                    }
                }
                catch (Exception ex)
                {
                    // A category is an accelerator, never authority. The exact icon remains in
                    // All lots even if one optional classification source is changing.
                    Plugin.Log.LogError("Classifying a town-map lot threw: " + ex);
                }
            }

            /// <summary>
            /// Reads the concise native tooltip; F3 adds identity and available native details.
            /// </summary>
            /// <remarks>
            /// §21: `UITownMapLotIcon.LateUpdate` writes `ToolTip.TextToShow` itself - lot type plus
            /// price when purchasable, lot type plus household name when owned, lot type alone
            /// otherwise. So the label is already localized, already formatted through
            /// `FormatService.GetMoney`, and already says whether a lot is someone's home.
            ///
            /// **Never cached** - it is reassigned every frame and the money value moves.
            /// `LabelForSale.activeSelf` is appended because "for sale" is the distinction that
            /// matters when the game is refusing to continue until something is bought.
            /// F3 adds identity, nonempty authored text, hours and native exterior building options
            /// (game-api.md section 96). These never participate in the list's sort key.
            /// </remarks>
            public string Describe()
            {
                return Describe(includeDetails: false);
            }

            internal string DescribeDetails()
            {
                return Describe(includeDetails: true);
            }

            private string Describe(bool includeDetails)
            {
                // External game API on a pooled icon: must degrade, never throw mid-announcement.
                try
                {
                    string text = _icon.ToolTip != null ? _icon.ToolTip.TextToShow : null;
                    if (string.IsNullOrWhiteSpace(text)) text = Loc.Get("lots_unnamed");

                    bool forSale = _icon.LabelForSale != null && _icon.LabelForSale.activeSelf;
                    string gameLabel = forSale
                        ? Loc.Get("lots_for_sale", text.Trim())
                        : text.Trim();
                    return LotReadinessReader.DescribeTownMapLot(_icon, gameLabel, includeDetails);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError("Reading a lot icon label threw: " + ex);
                    return null;
                }
            }

            internal string GetShortLabel()
            {
                string label = _icon?.ToolTip != null ? _icon.ToolTip.TextToShow : null;
                string gameLabel = string.IsNullOrWhiteSpace(label)
                    ? Loc.Get("lots_unnamed")
                    : label.Trim();
                return gameLabel;
            }

            internal TravelRoute ResolveTravelRoute(
                Action<ItemObjectRoot> receiveImpostor,
                Action<AssetLot> receiveLot)
            {
                try
                {
                    if (!IsAvailable || LotManager.Instance == null)
                    {
                        return TravelRoute.None;
                    }

                    AssetLot lot = LotManager.Instance.GetLotByGUID(_icon.LotGUID);
                    if (lot?.Data == null)
                    {
                        return TravelRoute.None;
                    }

                    LotType lotType = Settings.Get<LotTypes>()
                        ?.GetLotTypeByGUID(lot.Data.LotTypeGUID);
                    if (lotType?.CanBeVisitedByPlayer != true)
                    {
                        return TravelRoute.None;
                    }

                    receiveLot?.Invoke(lot);
                    ItemObjectRoot impostor = lot.ImpostorObject != null
                        ? lot.ImpostorObject.GetComponent<ItemObjectRoot>()
                        : null;
                    if (impostor != null && impostor.IsImpostorForLot == lot.GUID
                        && impostor.gameObject.activeInHierarchy)
                    {
                        receiveImpostor?.Invoke(impostor);
                        return TravelRoute.VisitImpostor;
                    }

                    return TravelRoute.GoHere;
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError("Resolving a lot's native travel route threw: " + ex);
                    return TravelRoute.None;
                }
            }

            internal void OpenInfo()
            {
                if (!IsAvailable)
                {
                    ScreenReader.Speak(Loc.Get("lots_target_gone"));
                    return;
                }

                ClearPanelOwnership();
                _icon.Clicked();
            }

            /// <summary>Opens travel/info commands when both legitimately exist.</summary>
            public void Activate()
            {
                OpenLotCommands(this);
            }
        }

        /// <summary>A role or lot-type category whose members remain exact lot entries.</summary>
        private sealed class LotCollectionEntry : VirtualPanel.IEntry
        {
            private readonly string _name;
            private readonly List<LotEntry> _entries;
            private readonly LotSort _sort;

            internal LotCollectionEntry(string name, List<LotEntry> entries, LotSort sort)
            {
                _name = name;
                _entries = entries ?? new List<LotEntry>();
                _sort = sort;
            }

            public bool IsAvailable => CountAvailable() > 0;

            public string Describe()
            {
                int count = CountAvailable();
                return count == 1
                    ? Loc.Get("lots_category_one", _name)
                    : Loc.Get("lots_category_many", _name, count);
            }

            public void Activate()
            {
                OpenLotCollection(_name, AvailableEntries(), _sort);
            }

            private int CountAvailable()
            {
                int count = 0;
                foreach (LotEntry entry in _entries)
                {
                    if (entry != null && entry.IsAvailable)
                    {
                        count++;
                    }
                }
                return count;
            }

            private List<LotEntry> AvailableEntries()
            {
                return _entries.FindAll(entry => entry != null && entry.IsAvailable);
            }
        }

        /// <summary>Second route over the same lots, grouped by the game's localized LotType.</summary>
        private sealed class LotTypesEntry : VirtualPanel.IEntry
        {
            private readonly List<LotEntry> _entries;

            internal LotTypesEntry(List<LotEntry> entries)
            {
                _entries = entries ?? new List<LotEntry>();
            }

            public bool IsAvailable => CountTypes() > 0;

            public string Describe()
            {
                int count = CountTypes();
                return count == 1
                    ? Loc.Get("lots_types_one")
                    : Loc.Get("lots_types_many", count);
            }

            public void Activate()
            {
                OpenLotTypes(_entries);
            }

            private int CountTypes()
            {
                var types = new HashSet<ulong>();
                foreach (LotEntry entry in _entries)
                {
                    if (entry != null && entry.IsAvailable)
                    {
                        types.Add(entry.LotTypeGuid);
                    }
                }
                return types.Count;
            }
        }

        private sealed class TravelEntry : VirtualPanel.IEntry
        {
            private readonly LotEntry _lot;

            internal TravelEntry(LotEntry lot)
            {
                _lot = lot;
            }

            public bool IsAvailable => _lot != null && _lot.IsAvailable
                && _lot.ResolveTravelRoute(null, null) != TravelRoute.None;

            public string Describe()
            {
                return _lot.ResolveTravelRoute(null, null) == TravelRoute.VisitImpostor
                    ? Loc.Get("lots_visit_command")
                    : Loc.Get("lots_go_here_command");
            }

            public void Activate()
            {
                OpenTravelActions(_lot);
            }
        }

        private sealed class LotInfoEntry : VirtualPanel.IEntry
        {
            private readonly LotEntry _lot;

            internal LotInfoEntry(LotEntry lot)
            {
                _lot = lot;
            }

            public bool IsAvailable => _lot != null && _lot.IsAvailable;

            public string Describe()
            {
                return Loc.Get("lots_info_command");
            }

            public void Activate()
            {
                _lot.OpenInfo();
            }
        }
    }
}
