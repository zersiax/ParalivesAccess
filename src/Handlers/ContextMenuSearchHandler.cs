using System.Collections.Generic;
using ParalivesAccess.Speech;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ParalivesAccess.Handlers
{
    /// <summary>Announces native popup focus and connects searchable dropdowns to their results.</summary>
    internal static class ContextMenuSearchHandler
    {
        private static TMP_InputField GetSearch(UIContextMenuWidget window)
        {
            TMP_InputField input = window != null ? window.InputFieldSearch : null;
            return window != null && window.IsVisible && window.ShowSearchBox
                && input != null && input.gameObject.activeInHierarchy ? input : null;
        }

        private static List<UIContextMenuWidgetItem> GetResults(UIContextMenuWidget window)
        {
            var results = new List<UIContextMenuWidgetItem>();
            if (window.ItemList?.CurrentItems == null) return results;
            foreach (UIListItemBase item in window.ItemList.CurrentItems)
                if (item is UIContextMenuWidgetItem result && result.gameObject.activeInHierarchy
                    && !FocusNavigator.ShouldSkip(result)) results.Add(result);
            return results;
        }

        internal static string GetLabel(Component source)
        {
            UIContextMenuWidget window = source != null
                ? source.GetComponentInParent<UIContextMenuWidget>() : null;
            TMP_InputField input = GetSearch(window);
            if (input == null || source.gameObject != input.gameObject) return null;
            return string.IsNullOrWhiteSpace(input.text)
                ? Loc.Get("dropdown_search") : Loc.Get("dropdown_search_value", input.text);
        }

        /// <summary>Includes the silently seeded option, or active search, in one opening announcement.</summary>
        internal static string DescribeOpening(UIContextMenuWidget window, string name, int count)
        {
            if (window == null || !window.IsVisible) return null;
            string summary = count == 1
                ? Loc.Get("window_opened_one", name) : Loc.Get("window_opened", name, count);
            string focusLabel = null;
            TMP_InputField input = GetSearch(window);
            if (input != null && EventSystem.current?.currentSelectedGameObject == input.gameObject)
            {
                focusLabel = GetLabel(input);
            }
            else
            {
                Focusable focused = ReferenceEquals(MenuNavigationHandler.CursorGroup, window)
                    && MenuNavigationHandler.Cursor != null
                        ? MenuNavigationHandler.Cursor : window.GetFocused(false);
                if (focused != null && focused.gameObject.activeInHierarchy
                    && !FocusNavigator.ShouldSkip(focused)
                    && focused.GetComponentInParent<UIContextMenuWidget>() == window)
                {
                    UIContextMenuWidgetItem row = focused.GetComponentInParent<UIContextMenuWidgetItem>();
                    // Pooled rows can outlive their menu contents. Read only a current live option.
                    if (row != null && GetResults(window).Contains(row))
                        focusLabel = UiLabelReader.GetLabelOrFallback(focused);
                }
            }
            return string.IsNullOrWhiteSpace(focusLabel)
                ? summary : Loc.Get("panel_with_focus", summary, focusLabel);
        }

        /// <summary>Consumes a vertical search gesture, selecting only a live native result.</summary>
        internal static bool TryLeaveSearch(UIWindow current, Vector2 direction, out Focusable target)
        {
            target = null;
            UIContextMenuWidget window = current as UIContextMenuWidget;
            TMP_InputField input = GetSearch(window);
            EventSystem events = EventSystem.current;
            if (input == null || events == null || events.currentSelectedGameObject != input.gameObject
                || direction.y == 0 || direction.x != 0) return false;

            List<UIContextMenuWidgetItem> results = GetResults(window);
            if (results.Count == 0)
            {
                ScreenReader.Speak(Loc.Get("dropdown_no_results"));
                return true;
            }

            target = direction.y < 0 ? results[0] : results[results.Count - 1];
            // HasInputFieldFocused is refreshed in HybridPlayer.LateUpdate. Clear the native
            // selection and let that refresh happen; the caller consumes this whole gesture.
            events.SetSelectedGameObject(null);
            Plugin.Log.LogInfo("[Dropdown] Left native search for an available option.");
            return true;
        }

        /// <summary>Up from the first option reopens the existing native search field.</summary>
        internal static bool TryReturnToSearch(FocusableGroup group, Focusable source, Vector2 direction)
        {
            UIContextMenuWidget window = group as UIContextMenuWidget;
            TMP_InputField input = GetSearch(window);
            if (input == null || direction.y <= 0 || direction.x != 0) return false;
            List<UIContextMenuWidgetItem> results = GetResults(window);
            if (results.Count == 0 || !ReferenceEquals(source, results[0])) return false;

            input.Select();
            input.ActivateInputField();
            input.caretPosition = input.text.Length;
            Plugin.Log.LogInfo("[Dropdown] Returned from the first option to native search.");
            return true;
        }
    }
}
