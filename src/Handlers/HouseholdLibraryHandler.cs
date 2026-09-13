using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace ParalivesAccess.Handlers
{
    /// <summary>Orders the native household library by filters, household cards and actions.</summary>
    internal static class HouseholdLibraryHandler
    {
        private static UIPremadeHouseholdSelection _lastWindow;
        private static UIPremadeHouseholdSelectionItem _lastRow;
        private static UIPremadeHouseholdSelection _scrolledWindow;
        private static float _scrollHoverUntil;

        private static UIPremadeHouseholdSelection Window(FocusableGroup group)
        {
            var window = group as UIPremadeHouseholdSelection;
            return window != null && window.IsVisible ? window : null;
        }

        private static bool Available(Focusable control, List<Focusable> registered)
        {
            return control != null && registered != null && registered.Contains(control)
                && control.gameObject.activeInHierarchy && !control.IsShoulderButton
                && !FocusNavigator.ShouldSkip(control);
        }

        private static UIPremadeHouseholdSelectionItem Row(Focusable control)
        {
            return control != null ? control.GetComponentInParent<UIPremadeHouseholdSelectionItem>() : null;
        }

        private static int Section(UIPremadeHouseholdSelection window, Focusable control)
        {
            if (Row(control) != null) return 1;
            if (control.gameObject == window.CloseButton) return 2;
            return control == window.OfficialContent || control == window.OwnContent
                || control == window.WorkshopContent
                || (window.HeaderPanel != null && control.transform.IsChildOf(window.HeaderPanel.transform))
                ? 0 : 2;
        }

        private static int CompareHierarchy(Focusable a, Focusable b)
        {
            var pathA = new List<int>();
            var pathB = new List<int>();
            for (Transform node = a.transform; node != null; node = node.parent) pathA.Add(node.GetSiblingIndex());
            for (Transform node = b.transform; node != null; node = node.parent) pathB.Add(node.GetSiblingIndex());
            pathA.Reverse(); pathB.Reverse();
            for (int i = 0; i < Math.Min(pathA.Count, pathB.Count); i++)
                if (pathA[i] != pathB[i]) return pathA[i].CompareTo(pathB[i]);
            return pathA.Count.CompareTo(pathB.Count);
        }

        private static List<Focusable> MainControls(UIPremadeHouseholdSelection window, List<Focusable> registered)
        {
            var filters = new List<Focusable>();
            var actions = new List<Focusable>();
            if (registered != null)
                foreach (Focusable control in registered)
                    if (Available(control, registered) && Row(control) == null)
                    {
                        List<Focusable> section = Section(window, control) == 0 ? filters : actions;
                        if (!section.Contains(control)) section.Add(control);
                    }
            filters.Sort(CompareHierarchy);
            actions.Sort((a, b) => a.gameObject == window.SelectButton ? (b.gameObject == window.SelectButton ? 0 : -1)
                : b.gameObject == window.SelectButton ? 1 : CompareHierarchy(a, b));
            // RefreshList pools cards in native CurrentItems order; positions are not stable during layout.
            if (window.PremadeHouseholdList?.CurrentItems != null)
                foreach (UIListItemBase item in window.PremadeHouseholdList.CurrentItems)
                    if (item is UIPremadeHouseholdSelectionItem && Available(item, registered)
                        && !filters.Contains(item)) filters.Add(item);
            filters.AddRange(actions);
            return filters;
        }

        private static List<Focusable> Details(UIPremadeHouseholdSelectionItem row, List<Focusable> registered)
        {
            var result = new List<Focusable> { row };
            foreach (Focusable control in registered)
                if (control != row && Available(control, registered) && Row(control) == row
                    && !result.Contains(control)) result.Add(control);
            // Keep the real card first, then its live portraits, badges and any native child actions.
            if (result.Count > 2) result.Sort(1, result.Count - 1, Comparer<Focusable>.Create(CompareHierarchy));
            return result;
        }

        internal static Focusable GetEntry(FocusableGroup group, List<Focusable> registered)
        {
            UIPremadeHouseholdSelection window = Window(group);
            if (window == null) return null;
            List<Focusable> controls = MainControls(window, registered);
            return controls.Find(c => c is UIPremadeHouseholdSelectionItem item && item.IsOn)
                ?? controls.Find(c => c is UIPremadeHouseholdSelectionItem)
                ?? (controls.Count > 0 ? controls[0] : null);
        }

        internal static bool TryNavigate(FocusableGroup group, List<Focusable> registered,
            Focusable source, Vector2 direction, out Focusable target)
        {
            target = null;
            UIPremadeHouseholdSelection window = Window(group);
            if (window == null) return false;
            List<Focusable> controls = MainControls(window, registered);
            UIPremadeHouseholdSelectionItem row = Row(source);
            Focusable primary = row != null ? row : source;
            int index = controls.IndexOf(primary);
            if (!Available(source, registered) || index < 0)
            {
                target = GetEntry(group, registered);
                return true;
            }
            if (row != null) { _lastWindow = window; _lastRow = row; }
            if (direction.y != 0)
            {
                int next = index + (direction.y < 0 ? 1 : -1);
                target = next >= 0 && next < controls.Count ? controls[next] : primary;
            }
            else if (row != null && direction.x != 0)
            {
                List<Focusable> details = Details(row, registered);
                int next = details.IndexOf(source) + (direction.x > 0 ? 1 : -1);
                target = next >= 0 && next < details.Count ? details[next] : source;
            }
            else target = source;
            return true;
        }

        internal static bool TrySwitchSection(FocusableGroup group, List<Focusable> registered,
            Focusable source, int direction, out Focusable target, out string label)
        {
            target = null;
            label = null;
            UIPremadeHouseholdSelection window = Window(group);
            if (window == null) return false;
            List<Focusable> controls = MainControls(window, registered);
            UIPremadeHouseholdSelectionItem row = Row(source);
            if (row != null && controls.Contains(row)) { _lastWindow = window; _lastRow = row; }
            int current = source != null ? Section(window, source) : 1;
            for (int offset = 1; offset <= 3; offset++)
            {
                int section = (current + (direction > 0 ? offset : -offset) + 3) % 3;
                target = controls.Find(c => Section(window, c) == section);
                if (target == null) continue;
                if (section == 1)
                    target = _lastWindow == window && _lastRow != null && controls.Contains(_lastRow)
                        ? _lastRow : GetEntry(group, registered);
                label = Loc.Get(section == 0 ? "library_filters" : section == 1 ? "library_households" : "library_actions");
                break;
            }
            return true;
        }

        /// <summary>Reveals keyboard-focused cards through the library's native ScrollRect.</summary>
        internal static void OnFocusGained(Focusable target)
        {
            UIPremadeHouseholdSelectionItem row = Row(target);
            var window = row != null ? row.GetComponentInParent<UIPremadeHouseholdSelection>() : null;
            ScrollRect scroll = window?.ScrollRect;
            RectTransform viewport = scroll?.viewport;
            RectTransform rect = row != null ? row.GetComponent<RectTransform>() : null;
            if (window == null || !window.IsVisible || scroll == null || !scroll.vertical
                || scroll.content == null || viewport == null || rect == null) return;
            Canvas.ForceUpdateCanvases();
            Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(viewport, rect);
            float shift = bounds.max.y > viewport.rect.yMax ? viewport.rect.yMax - bounds.max.y
                : bounds.min.y < viewport.rect.yMin ? viewport.rect.yMin - bounds.min.y : 0f;
            if (Mathf.Abs(shift) < 0.5f) return;
            scroll.StopMovement();
            Vector2 position = scroll.content.anchoredPosition;
            position.y += shift;
            scroll.content.anchoredPosition = position;
            _scrolledWindow = window;
            _scrollHoverUntil = Time.unscaledTime + 0.15f;
            Canvas.ForceUpdateCanvases();
        }

        internal static bool ShouldSuppressScrollHover(Focusable target)
        {
            var window = target != null ? target.GetComponentInParent<UIPremadeHouseholdSelection>() : null;
            return window != null && window == _scrolledWindow && Time.unscaledTime < _scrollHoverUntil
                && (Mouse.current == null || Mouse.current.delta.ReadValue().sqrMagnitude == 0f);
        }
    }
}
