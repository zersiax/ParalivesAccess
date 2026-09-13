using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using HarmonyLib;
using ParalivesAccess.Speech;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace ParalivesAccess.Handlers
{
    /// <summary>
    /// Gives the load-game list one stable, model-backed control per saved game.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="UILoadGame"/> creates every <see cref="UILoadGameItem"/> immediately, but
    /// <see cref="UILoadGameItem.Init"/> assigns <c>AssetSavedGame</c> only when the row is on
    /// screen and on one of eight staggered frames. An active off-screen row therefore still
    /// contains the prefab samples "Save name" and "2h05m", while its model property is null.
    /// Activating it reaches the game's delayed fade callback, which hides the window and then
    /// dereferences that null property.
    /// </para>
    /// <para>
    /// The window's private cached list is the source of truth used by its own refresh loop. This
    /// handler maps the public UIList row index into that exact cache, so labels and activation do
    /// not depend on whether presentation has caught up. It does not invent another ordering or
    /// eagerly load all thumbnails.
    /// </para>
    /// </remarks>
    public static class SaveGameListHandler
    {
        private static bool _cacheFieldResolved;
        private static FieldInfo _cachedSavedGamesField;
        private const float ScrollHoverSuppressionSeconds = 0.15f;
        private static float _scrollHoverSuppressionUntil = float.NegativeInfinity;
        private static Vector2 _pointerPositionAtReveal;
        private static bool _pointerPositionKnown;
        private static bool _scrollHoverConsumed;

        private sealed class CachedHouseholdIdentity
        {
            public long LastSaveTime;
            public ulong HouseholdGuid;
            public string Name;
        }

        private static readonly Dictionary<ulong, CachedHouseholdIdentity>
            HouseholdIdentityBySavedGame =
                new Dictionary<ulong, CachedHouseholdIdentity>();

        /// <summary>Installs the guard that attaches the authoritative asset before a row click.</summary>
        public static bool ApplyPatches(Harmony harmony)
        {
            try
            {
                MethodInfo target = AccessTools.Method(
                    typeof(UILoadGameItem), nameof(UILoadGameItem.OnClicked));
                MethodInfo prefix = AccessTools.Method(
                    typeof(SaveGameListHandler), nameof(OnClickedPrefix));

                if (target == null || prefix == null)
                {
                    Plugin.Log.LogError(
                        "Could not resolve UILoadGameItem.OnClicked or its save-list prefix.");
                    return false;
                }

                harmony.Patch(target, prefix: new HarmonyMethod(prefix));

                Patches patches = Harmony.GetPatchInfo(target);
                bool applied = false;
                if (patches != null)
                {
                    foreach (Patch patch in patches.Prefixes)
                    {
                        if (patch.owner == Plugin.PluginGuid && patch.PatchMethod == prefix)
                        {
                            applied = true;
                            break;
                        }
                    }
                }

                Plugin.Log.LogInfo(
                    $"Patched UILoadGameItem.OnClicked save resolution, applied={applied}.");
                return applied;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Patching the load-game list threw: " + ex);
                return false;
            }
        }

        /// <summary>
        /// Prefix on the game's row action. The original method remains responsible for its fade,
        /// click lock and call to SavedGameManager; this only supplies the asset it assumes exists.
        /// </summary>
        public static bool OnClickedPrefix(UILoadGameItem __instance)
        {
            try
            {
                AssetSavedGame savedGame = ResolveSavedGame(__instance, out int index);
                if (savedGame == null)
                {
                    Plugin.Log.LogWarning(
                        $"[SaveList] Refused an unresolved save-row click at index {index}.");
                    ScreenReader.Speak(Loc.Get("save_slot_unavailable"));
                    return false;
                }

                // Off-screen and reused rows can be null OR stale. The cache/index pairing is what
                // UILoadGame.RefreshSavedGamesUIList is currently trying to present, so always make
                // that pairing authoritative at the action boundary.
                if (!ReferenceEquals(__instance.AssetSavedGame, savedGame))
                {
                    __instance.AssetSavedGame = savedGame;
                    Plugin.Log.LogInfo(
                        $"[SaveList] Attached '{savedGame.FileNameNoExtension}' to row {index} "
                      + "before activation.");
                }

                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Preparing a load-game row click threw: " + ex);
                ScreenReader.Speak(Loc.Get("save_slot_unavailable"));
                return false;
            }
        }

        /// <summary>
        /// Handles labels for the outer saved-game row and its same-group presentation surface.
        /// Returning true is significant even when resolution fails: it prevents UiLabelReader
        /// from falling through to the prefab's sample caption.
        /// </summary>
        public static bool TryGetLabel(Component source, out string label)
        {
            label = null;
            if (!TryGetMainRowSurface(source, out UILoadGameItem row, out _))
            {
                return false;
            }

            AssetSavedGame savedGame = ResolveSavedGame(row, out _);
            label = savedGame != null
                ? BuildLabel(savedGame)
                : Loc.Get("save_slot_unavailable");
            return true;
        }

        /// <summary>
        /// True only for a child focusable which invokes the same row action as the outer item.
        /// Nested edit/delete/autosave controls register with their own FocusableGroup and remain.
        /// </summary>
        public static bool IsDuplicateRowAction(Focusable focusable)
        {
            try
            {
                if (!TryGetMainRowSurface(focusable, out UILoadGameItem row, out Focusable surface)
                    || surface == null || ReferenceEquals(surface, row))
                {
                    return false;
                }

                ParaButton button = surface as ParaButton;
                if (button?.ButtonClick == null)
                {
                    return false;
                }

                int count = button.ButtonClick.GetPersistentEventCount();
                if (count == 0)
                {
                    return false;
                }

                bool invokesRow = false;
                for (int i = 0; i < count; i++)
                {
                    UnityEngine.Object target = button.ButtonClick.GetPersistentTarget(i);
                    string method = button.ButtonClick.GetPersistentMethodName(i);

                    if (ReferenceEquals(target, row)
                        && string.Equals(method, nameof(UILoadGameItem.OnClicked),
                            StringComparison.Ordinal))
                    {
                        invokesRow = true;
                        continue;
                    }

                    // Do not discard a future prefab child that acquires another operation.
                    return false;
                }

                return invokesRow;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Classifying a load-game row child threw: " + ex);
                return false;
            }
        }

        /// <summary>Scrolls a newly focused saved-game row into its viewport.</summary>
        public static void OnFocusGained(Focusable focusable)
        {
            try
            {
                if (!TryGetMainRowSurface(focusable, out UILoadGameItem row, out _)
                    || row.RectTransform == null)
                {
                    return;
                }

                ScrollRect scroll = row.RectTransform.GetComponentInParent<ScrollRect>();
                RectTransform content = scroll?.content;
                RectTransform viewport = scroll?.viewport
                    ?? scroll?.GetComponent<RectTransform>();
                if (scroll == null || !scroll.vertical || content == null || viewport == null)
                {
                    return;
                }

                Canvas.ForceUpdateCanvases();
                Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(
                    viewport, row.RectTransform);
                Rect visible = viewport.rect;
                const float padding = 8f;
                float shift = 0f;

                if (bounds.max.y > visible.yMax - padding)
                {
                    // Item is above the viewport: move content down.
                    shift = (visible.yMax - padding) - bounds.max.y;
                }
                else if (bounds.min.y < visible.yMin + padding)
                {
                    // Item is below the viewport: move content up.
                    shift = (visible.yMin + padding) - bounds.min.y;
                }

                if (Mathf.Abs(shift) < 0.5f)
                {
                    return;
                }

                scroll.StopMovement();
                Vector2 position = content.anchoredPosition;
                position.y += shift;
                content.anchoredPosition = position;
                Canvas.ForceUpdateCanvases();

                // Moving content underneath a stationary pointer generates a real Unity
                // OnPointerEnter for whichever row arrives under it. That is not a user mouse
                // action and would interrupt the keyboard-focused row we are about to announce.
                // Arm a brief, save-row-only lease; real pointer movement invalidates it. TEST 76
                // proved Unity dispatches the synthetic pointer-enter on a later frame than the
                // content move, so the previous exact-frame token missed all five observed rows.
                _scrollHoverSuppressionUntil =
                    Time.unscaledTime + ScrollHoverSuppressionSeconds;
                Mouse mouse = Mouse.current;
                _pointerPositionKnown = mouse != null;
                _pointerPositionAtReveal = mouse != null
                    ? mouse.position.ReadValue()
                    : Vector2.zero;
                _scrollHoverConsumed = false;

                ResolveSavedGame(row, out int index);
                Plugin.Log.LogInfo(
                    $"[SaveList] Revealed row {index} in the load-game viewport.");
            }
            catch (Exception ex)
            {
                // Model-backed labels and activation remain correct if visual scrolling fails.
                Plugin.Log.LogError("Revealing a focused load-game row threw: " + ex);
            }
        }

        /// <summary>
        /// Consumes the one save-row hover generated when keyboard focus scrolls list content
        /// beneath an otherwise stationary mouse pointer.
        /// </summary>
        /// <remarks>
        /// This is deliberately narrower than disabling hover globally. It applies only to a main
        /// load-game row, only during the brief lease after a keyboard reveal, only once, and only
        /// while the pointer position remains unchanged. Genuine mouse movement and every non-save
        /// control retain normal hover announcements.
        /// </remarks>
        internal static bool ShouldSuppressScrollHover(Focusable focusable)
        {
            if (_scrollHoverConsumed || Time.unscaledTime > _scrollHoverSuppressionUntil
                || !TryGetMainRowSurface(focusable, out UILoadGameItem row, out _))
            {
                return false;
            }

            Mouse mouse = Mouse.current;
            if (_pointerPositionKnown && mouse != null
                && (mouse.position.ReadValue() - _pointerPositionAtReveal).sqrMagnitude > 0.25f)
            {
                return false;
            }

            _scrollHoverConsumed = true;
            ResolveSavedGame(row, out int index);
            Plugin.Log.LogInfo(
                $"[SaveList] Suppressed scroll-induced hover on row {index}.");
            return true;
        }

        /// <summary>
        /// Finds the main row represented by a component, but only on the row's own navigation
        /// surface. Controls in nested FocusableGroups are independent operations.
        /// </summary>
        private static bool TryGetMainRowSurface(
            Component source, out UILoadGameItem row, out Focusable surface)
        {
            row = null;
            surface = null;
            if (source == null)
            {
                return false;
            }

            // Autosave entries live beneath a main row but own a different saved-game asset.
            // Their Init method has no visibility guard, so leave them to their own rendered label.
            if (source.GetComponentInParent<UILoadGameItemAutoSave>() != null)
            {
                return false;
            }

            row = source as UILoadGameItem ?? source.GetComponentInParent<UILoadGameItem>();
            if (row == null)
            {
                return false;
            }

            if (ReferenceEquals(source, row))
            {
                surface = row;
                return true;
            }

            surface = source as Focusable ?? source.GetComponent<Focusable>();
            if (surface == null)
            {
                row = null;
                return false;
            }

            FocusableGroup rowGroup = row.GetComponentInParent<FocusableGroup>();
            FocusableGroup surfaceGroup = surface.GetComponentInParent<FocusableGroup>();
            if (!ReferenceEquals(rowGroup, surfaceGroup))
            {
                row = null;
                surface = null;
                return false;
            }

            return true;
        }

        /// <summary>Maps a UI row to the exact cached asset UILoadGame assigned to its index.</summary>
        private static AssetSavedGame ResolveSavedGame(UILoadGameItem row, out int index)
        {
            index = -1;
            if (row == null)
            {
                return null;
            }

            UILoadGame window = row.GetComponentInParent<UILoadGame>();
            List<UIListItemBase> items = window?.ItemList?.CurrentItems;
            if (items != null)
            {
                index = items.IndexOf(row);
            }

            ResolveCacheField();
            var cached = _cachedSavedGamesField?.GetValue(window) as List<AssetSavedGame>;
            if (cached != null && index >= 0 && index < cached.Count)
            {
                return cached[index];
            }

            // This fallback supports an already-initialized row if a future game build renames the
            // private cache. A null row never falls back to prefab text or unsafe activation.
            return row.AssetSavedGame;
        }

        private static void ResolveCacheField()
        {
            if (_cacheFieldResolved)
            {
                return;
            }

            _cacheFieldResolved = true;
            _cachedSavedGamesField = AccessTools.Field(
                typeof(UILoadGame), "_cachedSavedGamesAssetList");
            if (_cachedSavedGamesField == null)
            {
                Plugin.Log.LogError(
                    "Could not find UILoadGame._cachedSavedGamesAssetList; "
                  + "off-screen save rows cannot be resolved safely.");
            }
        }

        private static string BuildLabel(AssetSavedGame savedGame)
        {
            string name = savedGame.FileNameNoExtension;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = Loc.Get("save_slot_unnamed");
            }

            string householdName = ResolveHouseholdName(savedGame);
            string playtime = FormatService.GetTime(Mathf.Max(0f, savedGame.Playtime));
            if (savedGame.LastSaveTime <= 0L)
            {
                return string.IsNullOrEmpty(householdName)
                    ? Loc.Get("save_slot_label_no_date", name.Trim(), playtime)
                    : Loc.Get(
                        "save_slot_label_household_no_date",
                        name.Trim(), householdName, playtime);
            }

            string savedAt;
            try
            {
                // GameSavingManager writes DateTime.Now.Ticks, so interpret the value as local time.
                var timestamp = new DateTime(savedGame.LastSaveTime, DateTimeKind.Local);
                savedAt = timestamp.ToString("g", CultureInfo.CurrentCulture);
            }
            catch (ArgumentOutOfRangeException)
            {
                return string.IsNullOrEmpty(householdName)
                    ? Loc.Get("save_slot_label_no_date", name.Trim(), playtime)
                    : Loc.Get(
                        "save_slot_label_household_no_date",
                        name.Trim(), householdName, playtime);
            }

            return string.IsNullOrEmpty(householdName)
                ? Loc.Get("save_slot_label", name.Trim(), playtime, savedAt)
                : Loc.Get(
                    "save_slot_label_household",
                    name.Trim(), householdName, playtime, savedAt);
        }

        /// <summary>
        /// Resolves the household from the selected save package, not from the currently loaded
        /// game or from a global same-GUID lookup. Child packages are read once from their exact
        /// import files so listing saves does not load every saved world's asset graph.
        /// </summary>
        private static string ResolveHouseholdName(AssetSavedGame savedGame)
        {
            try
            {
                AssetSavedGameData data = savedGame?.Data;
                ulong householdGuid = data != null ? data.CurrentHouseholdGUID : 0UL;
                if (savedGame == null)
                {
                    return null;
                }

                CachedHouseholdIdentity cached;
                if (HouseholdIdentityBySavedGame.TryGetValue(savedGame.GUID, out cached)
                    && cached.LastSaveTime == savedGame.LastSaveTime
                    && cached.HouseholdGuid == householdGuid)
                {
                    return cached.Name;
                }

                string householdName = householdGuid == 0UL
                    ? Loc.Get("save_slot_no_current_household")
                    : ReadHouseholdNameFromPackage(savedGame, householdGuid);
                householdName = string.IsNullOrWhiteSpace(householdName)
                    ? null
                    : ScreenReader.NormalizeForOutput(householdName.Trim());

                HouseholdIdentityBySavedGame[savedGame.GUID] = new CachedHouseholdIdentity
                {
                    LastSaveTime = savedGame.LastSaveTime,
                    HouseholdGuid = householdGuid,
                    Name = householdName
                };
                return householdName;
            }
            catch (Exception ex)
            {
                // Save identity remains usable from filename/time if a package is still loading.
                Plugin.Log.LogError("Resolving a save slot household name threw: " + ex);
                return null;
            }
        }

        private static string ReadHouseholdNameFromPackage(
            AssetSavedGame savedGame, ulong householdGuid)
        {
            string householdDirectory = Path.Combine(
                savedGame.FilePath, householdGuid + ".household");
            string householdImport = Path.Combine(
                householdDirectory, householdGuid + ".household.import");
            if (!File.Exists(householdImport))
            {
                return ReadLoadedHouseholdName(savedGame.GUID, householdGuid);
            }

            AssetHouseholdData householdData = JsonUtility.FromJson<AssetHouseholdData>(
                File.ReadAllText(householdImport));
            if (householdData == null)
            {
                return null;
            }
            if (!string.IsNullOrWhiteSpace(householdData.HouseholdName))
            {
                return householdData.HouseholdName;
            }
            if (householdData.Characters == null || householdData.Characters.Count == 0)
            {
                return TranslationManager.Get("Household_New");
            }

            ulong characterGuid = householdData.Characters[0];
            string characterImport = Path.Combine(
                householdDirectory,
                characterGuid + ".character",
                characterGuid + ".character.import");
            if (!File.Exists(characterImport))
            {
                return ReadLoadedHouseholdName(savedGame.GUID, householdGuid);
            }

            AssetCharacterData characterData = JsonUtility.FromJson<AssetCharacterData>(
                File.ReadAllText(characterImport));
            if (characterData == null)
            {
                return null;
            }

            string familyName = !string.IsNullOrEmpty(characterData.LastName)
                ? characterData.LastName
                : characterData.ShortName;
            return TranslationManager.Get("Household_Name", familyName);
        }

        private static string ReadLoadedHouseholdName(
            ulong savedGameGuid, ulong householdGuid)
        {
            AssetManager manager = AssetManager.Instance;
            if (manager == null)
            {
                return null;
            }

            var packaged = manager.GetAssetsOfTypeInPackage<AssetHousehold>(savedGameGuid);
            for (int i = 0; i < packaged.Item2; i++)
            {
                AssetHousehold household = packaged.Item1[i] as AssetHousehold;
                if (household != null && household.GUID == householdGuid
                    && household.ParentPackageAssetGUID == savedGameGuid)
                {
                    return household.Data?.GetHouseholdName();
                }
            }
            return null;
        }
    }
}
