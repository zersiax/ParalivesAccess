using System;
using System.Collections.Generic;
using System.Text;
using ParalivesAccess.Speech;
using Setting;
using UnityEngine.InputSystem;

namespace ParalivesAccess.Handlers
{
    /// <summary>
    /// Presents the game's live build catalogue as a keyboard-driven category and item browser.
    /// </summary>
    public static class BuildCatalogHandler
    {
        internal static bool TryGetContext(out string report)
        {
            report = _panel == null ? null : _panel.Name + ". " + _panel.Current?.Describe();
            return report != null;
        }
        /// <summary>Set false to disable the accessible build-catalogue browser.</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>Configurable key that opens and closes the catalogue browser.</summary>
        public static Key OpenKey { get; set; } = Key.F6;

        /// <summary>True while the browser, a catalogue refresh, or preview creation owns input.</summary>
        public static bool OwnsInput
        {
            get { return _panel != null || _awaitingRefresh || _waitingForPreview; }
        }

        private static VirtualPanel _panel;
        private static UIBuildModeCatalog _catalog;

        private static bool _awaitingRefresh;
        private static int _activatedCategoryLevel;
        private static string _refreshSignature;
        private static int _stableRefreshObservations;

        private static bool _waitingForPreview;
        private static int _previewWaitPasses;

        private static VirtualPanel.IEntry _pressedEntry;
        private static Focusable _pressedFocusable;

        private static bool _cameraBaselineCaptured;
        private static bool _cameraWasFrozen;
        private static bool _loggedRuntimeFacts;

        /// <summary>
        /// Prefix-side camera guard. The catalogue browser uses arrows, which otherwise also move
        /// the build camera through the game's MoveCamera action.
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

        /// <summary>Called once per frame after the game's systems have updated the live lists.</summary>
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
                    if (OwnsInput)
                    {
                        Close(hybridPlayer, announce: true, restoreCamera: true);
                    }
                    return;
                }

                if (ModInputKey.WasPressed(keyboard, OpenKey))
                {
                    if (OwnsInput)
                    {
                        Close(hybridPlayer, announce: true, restoreCamera: true);
                    }
                    else
                    {
                        Open(hybridPlayer);
                    }

                    return;
                }

                if (!OwnsInput)
                {
                    return;
                }

                if (_waitingForPreview)
                {
                    _previewWaitPasses++;
                    if (_previewWaitPasses >= 1)
                    {
                        Plugin.Log.LogWarning(
                            "[BuildCatalog] Expected an affordable item preview after the next "
                          + "game-system pass, but ItemInPlacement is still null.");
                        _waitingForPreview = false;
                        Speak(Loc.Get("catalog_preview_failed"));
                        EnterItemPanel();
                    }

                    return;
                }

                if (!IsCatalogUsable(hybridPlayer))
                {
                    Close(hybridPlayer, announce: true, restoreCamera: true);
                    return;
                }

                if (_awaitingRefresh)
                {
                    ObserveRefresh(hybridPlayer);
                    return;
                }

                HandlePanelInput(hybridPlayer, keyboard);
            }
            catch (Exception ex)
            {
                Enabled = false;
                Close(null, announce: false, restoreCamera: true);
                Plugin.Log.LogError("BuildCatalogHandler disabled after an exception: " + ex);
                ScreenReader.Speak(Loc.Get("catalog_handler_failed"));
            }
        }

        /// <summary>
        /// Transfers the camera-freeze baseline to a newly captured preview without briefly
        /// releasing the camera between the catalogue and placement owners.
        /// </summary>
        internal static bool TryTransferCameraBaseline(out bool cameraWasFrozen)
        {
            cameraWasFrozen = false;
            if (!_waitingForPreview || !_cameraBaselineCaptured)
            {
                return false;
            }

            cameraWasFrozen = _cameraWasFrozen;
            _cameraBaselineCaptured = false;
            _waitingForPreview = false;
            _previewWaitPasses = 0;
            _panel = null;
            _catalog = null;
            _pressedEntry = null;
            _pressedFocusable = null;

            Plugin.Log.LogInfo(
                "[BuildCatalog] Input ownership transferred to the captured placement preview.");
            return true;
        }

        internal static void Open(HybridPlayer hybridPlayer)
        {
            string conflict;
            if (!ModInputKey.IsAvailable(hybridPlayer, OpenKey, out conflict))
            {
                Speak(conflict);
                return;
            }

            UIBuildModeCatalog catalog = UI.Instance != null
                ? UI.GetOrNull<UIBuildModeCatalog>(hybridPlayer.Player.PlayerIndex)
                : null;

            if (catalog == null || !catalog.IsVisible || hybridPlayer.Player.ItemInPlacement != null)
            {
                bool mapZoom = hybridPlayer.Player.State == GameStates.BuildMode
                    && hybridPlayer.Player.ItemInPlacement == null
                    && hybridPlayer.Player.TownMapForFastTravelMethod == 0
                    && hybridPlayer.IsInTownMap(checkForHouseholdStatus: false);
                Speak(Loc.Get(mapZoom ? "catalog_town_map" : "catalog_unavailable"));
                return;
            }

            _catalog = catalog;
            CaptureCameraBaseline(hybridPlayer);
            LogRuntimeFacts(hybridPlayer);

            if (GetBrowsableItems().Count > 0)
            {
                EnterItemPanel();
                return;
            }

            int level = GetDeepestActiveCategoryLevel(0);
            if (level < 0)
            {
                Speak(Loc.Get("catalog_no_content"));
                Close(hybridPlayer, announce: false, restoreCamera: true);
                return;
            }

            EnterCategoryPanel(level);
        }

        private static void HandlePanelInput(HybridPlayer hybridPlayer, Keyboard keyboard)
        {
            if (_panel == null)
            {
                return;
            }

            if (keyboard.downArrowKey.wasPressedThisFrame || keyboard.rightArrowKey.wasPressedThisFrame)
            {
                _panel.Move(1);
                return;
            }

            if (keyboard.upArrowKey.wasPressedThisFrame || keyboard.leftArrowKey.wasPressedThisFrame)
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
                _pressedEntry = _panel.Current;
                if (_pressedEntry is SurfaceEntry && (keyboard.ctrlKey.isPressed || keyboard.altKey.isPressed || keyboard.shiftKey.isPressed))
                { _pressedEntry = null; Speak(Loc.Get("floor_cover_release_modifiers")); return; }
                var selectedItem = _pressedEntry as ItemEntry;
                if (selectedItem != null && !selectedItem.IsAffordable)
                {
                    _pressedEntry = null;
                    _panel.AnnounceCurrent();
                    return;
                }

                var gameEntry = _pressedEntry as GameEntry;
                _pressedFocusable = gameEntry != null ? gameEntry.FocusTarget : null;
                if (_pressedFocusable != null)
                {
                    MenuNavigationHandler.PressFocusable(_pressedFocusable);
                }
            }

            if (!released)
            {
                return;
            }

            VirtualPanel.IEntry entry = _pressedEntry;
            Focusable target = _pressedFocusable;
            _pressedEntry = null;
            _pressedFocusable = null;

            if (entry == null)
            {
                return;
            }

            if (!entry.IsAvailable || (entry is ItemEntry item && !item.IsAffordable)
                || (entry is SurfaceEntry && (keyboard.ctrlKey.isPressed || keyboard.altKey.isPressed || keyboard.shiftKey.isPressed)))
            {
                if (target != null)
                {
                    MenuNavigationHandler.CancelFocusablePress(target);
                }
                if (_panel?.Current != null)
                {
                    _panel.AnnounceCurrent();
                }
                else
                {
                    Speak(Loc.Get("catalog_entry_unavailable"));
                }
                return;
            }

            if (target != null)
            {
                MenuNavigationHandler.ReleaseFocusable(target);
                OnGameEntryActivated(entry as GameEntry);
            }
            else
            {
                entry.Activate();
            }
        }

        private static void OnGameEntryActivated(GameEntry entry)
        {
            if (entry is SurfaceEntry surface)
            {
                var hybrid = PlayerManager.Instance?.HybridPlayer1;
                if (hybrid?.Player == null || hybrid.Player.SelectedSurface != surface.SurfaceGuid
                    || hybrid.Player.SelectedColors == null || hybrid.Player.SelectedColors.Length == 0)
                { Speak(Loc.Get("catalog_surface_not_selected")); return; }
                if (hybrid.Player.State == GameStates.Painting && hybrid.Player.PaintCategory == PaintCategory.Floors)
                {
                    Close(hybrid, announce: false, restoreCamera: true);
                    FloorCoveringHandler.Open(hybrid);
                }
                else Speak(Loc.Get("catalog_surface_selected", CatalogEntryReader.SurfaceName(surface.SurfaceGuid)));
                return;
            }
            if (entry is CategoryEntry category)
            {
                Plugin.Log.LogInfo(
                    $"[BuildCatalog] category level={category.Level}, tag={category.Tag}, "
                  + $"label='{category.Describe()}'.");

                _panel = null;
                _awaitingRefresh = true;
                _activatedCategoryLevel = category.Level;
                _refreshSignature = null;
                _stableRefreshObservations = 0;
                return;
            }

            if (entry is ItemEntry item)
            {
                bool affordable = item.IsAffordable;
                Plugin.Log.LogInfo(
                    $"[BuildCatalog] selected catalogueGUID={item.CatalogueGuid}, "
                  + $"variant={item.VariantIndex}, price={item.Price}, affordable={affordable}.");

                if (!affordable)
                {
                    _panel?.AnnounceCurrent();
                    return;
                }

                _panel = null;
                _waitingForPreview = true;
                _previewWaitPasses = 0;
            }
        }

        private static void ObserveRefresh(HybridPlayer hybridPlayer)
        {
            if (hybridPlayer.Player.BuildModeCatalogueListDirty)
            {
                _refreshSignature = null;
                _stableRefreshObservations = 0;
                return;
            }

            string signature = BuildLiveSignature();
            if (!string.Equals(signature, _refreshSignature, StringComparison.Ordinal))
            {
                _refreshSignature = signature;
                _stableRefreshObservations = 0;
                return;
            }

            _stableRefreshObservations++;
            if (_stableRefreshObservations < 1)
            {
                return;
            }

            _awaitingRefresh = false;
            Plugin.Log.LogInfo(
                $"[BuildCatalog] refresh settled after category level {_activatedCategoryLevel}: "
              + signature);

            int deeper = GetDeepestActiveCategoryLevel(_activatedCategoryLevel + 1);
            if (deeper >= 0)
            {
                EnterCategoryPanel(deeper);
                return;
            }

            if (GetBrowsableItems().Count > 0)
            {
                EnterItemPanel();
                return;
            }

            if (WallConstructionHandler.Enabled && hybridPlayer.Player.State == GameStates.PlaceSegment
                && hybridPlayer.Player.CurrentSegmentType == SegmentTypes.Wall
                && !hybridPlayer.Player.CatalogueOpened)
            {
                Close(hybridPlayer, announce: false, restoreCamera: true);
                return;
            }

            Speak(Loc.Get("catalog_no_content"));
            EnterCategoryPanel(_activatedCategoryLevel);
        }

        private static void EnterCategoryPanel(int level)
        {
            CategoryList source = GetCategoryList(level);
            if (source?.UIList?.CurrentItems == null)
            {
                Speak(Loc.Get("catalog_no_content"));
                return;
            }

            var panel = new VirtualPanel(GetPanelName(level, showingItems: false));
            int count = 0;
            foreach (UIListItemBase listItem in source.UIList.CurrentItems)
            {
                var button = listItem as UIGenericCategoriesButton;
                if (button == null || !button.Interactable || button.Data == null
                    || !button.gameObject.activeInHierarchy)
                {
                    continue;
                }

                panel.Add(new CategoryEntry(button, level));
                count++;
            }

            int itemCount = GetBrowsableItems().Count;
            if (itemCount > 0)
            {
                panel.Add(new BrowseItemsEntry());
            }

            panel.Add(new BackEntry(level, fromItems: false));

            if (count == 0 && itemCount == 0)
            {
                Speak(Loc.Get("catalog_no_content"));
            }

            Plugin.Log.LogInfo(
                $"[BuildCatalog] panel=categories, level={level}, path='{panel.Name}', "
              + $"categories={count}, catalogueRows={itemCount}.");
            _panel = panel;
            _panel.Enter();
        }

        private static void EnterItemPanel()
        {
            List<UIBuildModeItem> items = GetBrowsableItems();
            if (items.Count == 0)
            {
                int level = GetDeepestActiveCategoryLevel(0);
                if (level >= 0)
                {
                    EnterCategoryPanel(level);
                }
                else
                {
                    Speak(Loc.Get("catalog_no_content"));
                }
                return;
            }

            var panel = new VirtualPanel(GetPanelName(-1, showingItems: true));
            foreach (UIBuildModeItem item in items)
            {
                if (CatalogEntryReader.IsOrdinaryItem(item)) panel.Add(new ItemEntry(item));
                else panel.Add(new SurfaceEntry(item));
            }
            panel.Add(new BackEntry(GetDeepestActiveCategoryLevel(0), fromItems: true));

            Plugin.Log.LogInfo(
                $"[BuildCatalog] panel=items, path='{panel.Name}', catalogueRows={items.Count}.");
            _panel = panel;
            _panel.Enter();
        }

        private static void GoBack(int level, bool fromItems)
        {
            if (fromItems)
            {
                int returnLevel = level >= 0 ? level : GetDeepestActiveCategoryLevel(0);
                if (returnLevel >= 0)
                {
                    EnterCategoryPanel(returnLevel);
                    return;
                }
            }
            else if (level > 0)
            {
                EnterCategoryPanel(level - 1);
                return;
            }

            HybridPlayer hybridPlayer = PlayerManager.Instance != null
                ? PlayerManager.Instance.HybridPlayer1
                : null;
            Close(hybridPlayer, announce: true, restoreCamera: true);
        }

        private static bool IsCatalogUsable(HybridPlayer hybridPlayer)
        {
            return _catalog != null
                && _catalog.IsVisible
                && hybridPlayer?.Player != null
                && hybridPlayer.Player.ItemInPlacement == null;
        }

        private static CategoryList GetCategoryList(int level)
        {
            CategoryList[] lists = _catalog?.GenericUI?.CategoryLists;
            if (lists == null || level < 0 || level >= lists.Length)
            {
                return null;
            }

            CategoryList list = lists[level];
            return list != null
                && list.GameObjectToShow != null
                && list.GameObjectToShow.activeSelf
                ? list
                : null;
        }

        private static int GetDeepestActiveCategoryLevel(int minimum)
        {
            CategoryList[] lists = _catalog?.GenericUI?.CategoryLists;
            if (lists == null)
            {
                return -1;
            }

            for (int i = lists.Length - 1; i >= Math.Max(0, minimum); i--)
            {
                CategoryList list = GetCategoryList(i);
                if (list?.UIList?.CurrentItems != null && list.UIList.CurrentItems.Count > 0)
                {
                    return i;
                }
            }

            return -1;
        }

        private static List<UIBuildModeItem> GetBrowsableItems()
        {
            var result = new List<UIBuildModeItem>();
            List<UIListItemBase> current = _catalog?.ItemList?.UIList?.CurrentItems;
            if (current == null)
            {
                return result;
            }

            foreach (UIListItemBase listItem in current)
            {
                var item = listItem as UIBuildModeItem;
                if (item != null && item.Interactable
                    && (CatalogEntryReader.IsOrdinaryItem(item) || CatalogEntryReader.SurfaceOf(item) != null)
                    && item.gameObject.activeInHierarchy)
                {
                    result.Add(item);
                }
            }

            return result;
        }

        private static string GetPanelName(int level, bool showingItems)
        {
            var names = new List<string>();
            CategoryList[] lists = _catalog?.GenericUI?.CategoryLists;
            if (lists != null)
            {
                foreach (CategoryList list in lists)
                {
                    if (list?.GameObjectToShow == null || !list.GameObjectToShow.activeSelf
                        || list.UIList?.CurrentItems == null)
                    {
                        continue;
                    }

                    foreach (UIListItemBase listItem in list.UIList.CurrentItems)
                    {
                        var button = listItem as UIGenericCategoriesButton;
                        if (button != null && button.Data != null && button.Data.IsOn)
                        {
                            string label = UiLabelReader.GetLabel(button);
                            if (!string.IsNullOrEmpty(label))
                            {
                                names.Add(label);
                            }
                            break;
                        }
                    }
                }
            }

            string head = showingItems
                ? Loc.Get("catalog_panel")
                : Loc.Get("catalog_categories_level", level + 1);
            return names.Count == 0 ? head : head + ", " + string.Join(", ", names.ToArray());
        }

        private static string BuildLiveSignature()
        {
            var builder = new StringBuilder();
            CategoryList[] lists = _catalog?.GenericUI?.CategoryLists;
            if (lists != null)
            {
                for (int i = 0; i < lists.Length; i++)
                {
                    CategoryList list = GetCategoryList(i);
                    if (list?.UIList?.CurrentItems == null)
                    {
                        continue;
                    }

                    builder.Append("L").Append(i).Append("=").Append(list.UIList.CurrentItems.Count);
                    foreach (UIListItemBase listItem in list.UIList.CurrentItems)
                    {
                        var button = listItem as UIGenericCategoriesButton;
                        if (button?.Data != null)
                        {
                            builder.Append(":").Append(button.Data.Tag);
                        }
                    }
                    builder.Append(";");
                }
            }

            List<UIBuildModeItem> items = GetBrowsableItems();
            builder.Append("items=").Append(items.Count);
            foreach (UIBuildModeItem item in items)
            {
                if (CatalogEntryReader.IsOrdinaryItem(item)) builder.Append(":item:").Append(item.ItemData.GUID).Append("/").Append(item.VariantIndex);
                else builder.Append(":surface:").Append(CatalogEntryReader.SurfaceOf(item).GUID);
            }
            return builder.ToString();
        }

        private static void CaptureCameraBaseline(HybridPlayer hybridPlayer)
        {
            if (_cameraBaselineCaptured || hybridPlayer?.Player == null)
            {
                return;
            }

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
                HybridPlayer hybridPlayer = PlayerManager.Instance != null
                    ? PlayerManager.Instance.HybridPlayer1
                    : null;
                if (hybridPlayer?.Player != null)
                {
                    hybridPlayer.Player.FreezeCameraMovement = cameraWasFrozen;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Restoring the build-catalogue camera baseline threw: " + ex);
            }
        }

        private static void Close(HybridPlayer hybridPlayer, bool announce, bool restoreCamera)
        {
            LotBuildInputOwnership.HoldThroughCurrentFrame();

            Focusable pressedFocusable = _pressedFocusable;

            _panel = null;
            _catalog = null;
            _awaitingRefresh = false;
            _waitingForPreview = false;
            _pressedEntry = null;
            _pressedFocusable = null;
            _refreshSignature = null;
            _stableRefreshObservations = 0;
            _previewWaitPasses = 0;

            if (pressedFocusable != null)
            {
                try
                {
                    MenuNavigationHandler.CancelFocusablePress(pressedFocusable);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError("Cancelling a build-catalogue press threw: " + ex);
                }
            }

            if (restoreCamera)
            {
                RestoreCamera();
            }

            if (announce)
            {
                Speak(Loc.Get("catalog_closed"));
            }
        }

        private static void LogRuntimeFacts(HybridPlayer hybridPlayer)
        {
            if (_loggedRuntimeFacts)
            {
                return;
            }

            _loggedRuntimeFacts = true;
            CategoryList[] lists = _catalog.GenericUI?.CategoryLists;
            var counts = new List<string>();
            if (lists != null)
            {
                for (int i = 0; i < lists.Length; i++)
                {
                    CategoryList list = lists[i];
                    counts.Add(
                        $"{i}:active={list?.GameObjectToShow?.activeSelf == true},"
                      + $"count={list?.UIList?.CurrentItems?.Count ?? 0}");
                }
            }

            Plugin.Log.LogInfo(
                "[BuildCatalog] runtime facts: "
              + $"visible={_catalog.IsVisible}, currentWindow="
              + $"{ReferenceEquals(_catalog.UIManager?.CurrentWindow, _catalog)}, "
              + $"registerBack={_catalog.RegisterWindowForEscapeButtonEvent}, "
              + $"blocksActions={_catalog.BlockPlayerActionWhenShown}, "
              + $"playerBlocked={hybridPlayer.Player.PlayerActionBlockedByUIWindow}, "
              + $"activeNavGroup='{MenuNavigationHandler.ActivePanel?.name ?? "(none)"}', "
              + $"levels=[{string.Join(", ", counts.ToArray())}], "
              + $"items={_catalog.ItemList?.UIList?.CurrentItems?.Count ?? 0}.");
        }

        private static void Speak(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            Plugin.Log.LogInfo("[BuildCatalog] " + message);
            ScreenReader.Speak(message);
        }

        private abstract class GameEntry : VirtualPanel.IEntry
        {
            public abstract Focusable FocusTarget { get; }
            public abstract bool IsAvailable { get; }
            public abstract string Describe();
            public void Activate() { }
        }

        private sealed class CategoryEntry : GameEntry
        {
            private readonly UIGenericCategoriesButton _button;
            private readonly ulong _tag;

            internal CategoryEntry(UIGenericCategoriesButton button, int level)
            {
                _button = button;
                _tag = button.Data.Tag;
                Level = level;
            }

            internal int Level { get; }
            internal ulong Tag => _tag;
            public override Focusable FocusTarget => _button;
            public override bool IsAvailable => _button != null
                && _button.gameObject.activeInHierarchy
                && _button.Interactable
                && _button.Data != null
                && _button.Data.Tag == _tag;
            public override string Describe() => IsAvailable
                ? UiLabelReader.GetLabelOrFallback(_button)
                : null;
        }

        private sealed class ItemEntry : GameEntry
        {
            private readonly UIBuildModeItem _item;
            private readonly ulong _catalogueGuid;
            private readonly int _variantIndex;

            internal ItemEntry(UIBuildModeItem item)
            {
                _item = item;
                _catalogueGuid = item.ItemData.GUID;
                _variantIndex = item.VariantIndex;
            }

            internal ulong CatalogueGuid => _catalogueGuid;
            internal int VariantIndex => _variantIndex;
            internal int Price => IsAvailable ? _item.Price : 0;
            internal bool IsAffordable => IsAvailable
                && MoneyManager.Instance != null
                && MoneyManager.Instance.CanPurchase(_item.Price);
            public override Focusable FocusTarget => _item;
            public override bool IsAvailable => _item != null
                && _item.gameObject.activeInHierarchy
                && _item.Interactable
                && CatalogEntryReader.IsOrdinaryItem(_item)
                && _item.ItemData.GUID == _catalogueGuid
                && _item.VariantIndex == _variantIndex;
            public override string Describe() => IsAvailable
                ? UiLabelReader.GetLabelOrFallback(_item)
                : null;
        }

        private sealed class SurfaceEntry : GameEntry
        {
            private readonly UIBuildModeItem _item;
            internal ulong SurfaceGuid { get; }
            internal SurfaceEntry(UIBuildModeItem item)
            { _item = item; SurfaceGuid = CatalogEntryReader.SurfaceOf(item).GUID; }
            public override Focusable FocusTarget => _item;
            public override bool IsAvailable => _item != null && _item.gameObject.activeInHierarchy
                && _item.Interactable && CatalogEntryReader.SurfaceOf(_item)?.GUID == SurfaceGuid;
            public override string Describe() => IsAvailable
                ? Loc.Get("catalog_surface", CatalogEntryReader.SurfaceName(SurfaceGuid)) : null;
        }

        private sealed class BrowseItemsEntry : VirtualPanel.IEntry
        {
            public bool IsAvailable => GetBrowsableItems().Count > 0;
            public string Describe() => Loc.Get("catalog_browse_items", GetBrowsableItems().Count);
            public void Activate() => EnterItemPanel();
        }

        private sealed class BackEntry : VirtualPanel.IEntry
        {
            private readonly int _level;
            private readonly bool _fromItems;
            internal BackEntry(int level, bool fromItems)
            {
                _level = level;
                _fromItems = fromItems;
            }
            public bool IsAvailable => true;
            public string Describe() => Loc.Get("catalog_back");
            public void Activate() => GoBack(_level, _fromItems);
        }
    }
}
