using System;
using System.Collections.Generic;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>Traverses native lists and control hierarchies without screen-coordinate scoring.</summary>
    internal static class LogicalFocusNavigator
    {
        private sealed class Entry
        {
            internal Focusable Control;
            internal List<int> Path;
            internal int Registration;
        }

        // Creator diagrams and their dedicated row navigation retain the spatial fallback.
        internal static bool UsesSpatialNavigation(FocusableGroup group) =>
            group != null && (group is UICharacterCreatorContextualList
                || group.GetComponentInParent<UICharacterCreator>() != null);

        internal static Focusable FindTarget(FocusableGroup group, List<Focusable> registered,
            Focusable source, Vector2 direction)
        {
            if (UsesSpatialNavigation(group))
                return FocusNavigator.FindTarget(group, registered, source, direction);
            if (group == null || direction == Vector2.zero) return null;
            List<Focusable> ordered = GetOrdered(registered);
            if (ordered.Count == 0) return null;
            int index = ordered.IndexOf(source);
            if (index < 0)
            {
                foreach (Focusable control in ordered)
                    if (control.IsFirstFocused) return control;
                return ordered[0];
            }
            bool forward = Math.Abs(direction.y) > Math.Abs(direction.x)
                ? direction.y < 0f : direction.x > 0f;
            int next = index + (forward ? 1 : -1);
            return next >= 0 && next < ordered.Count ? ordered[next] : null;
        }

        internal static Focusable GetEntry(FocusableGroup group, List<Focusable> registered)
        {
            if (group is UICharacterSubMenuBar)
            {
                foreach (Focusable control in GetOrdered(registered))
                    if (control is ParaToggle toggle && toggle.IsOn) return control;
            }
            if (group is UIRelationships relationships && relationships.UIListOtherCharacters != null)
            {
                Focusable first = null;
                foreach (UIListItemBase row in relationships.UIListOtherCharacters.CurrentItems)
                {
                    if (!IsReachable(row) || registered == null || !registered.Contains(row)) continue;
                    if (row.IsOn) return row;
                    if (first == null) first = row;
                }
                return first;
            }
            return null;
        }

        internal static List<Focusable> GetReachable(FocusableGroup group, List<Focusable> registered)
        {
            if (UsesSpatialNavigation(group)) return FocusNavigator.GetDistinctReachable(registered);
            return Collect(registered, includeShoulders: true);
        }

        internal static int CountReachable(FocusableGroup group, List<Focusable> registered)
        {
            if (UsesSpatialNavigation(group)) return FocusNavigator.CountDistinctReachable(registered);
            // Called for visible panels every frame; counting needs neither sorting nor paths.
            int count = 0;
            if (registered == null) return count;
            for (int i = 0; i < registered.Count; i++)
                if (IsReachable(registered[i]) && registered.IndexOf(registered[i]) == i) count++;
            return count;
        }

        private static List<Focusable> Collect(List<Focusable> registered, bool includeShoulders)
        {
            var result = new List<Focusable>();
            if (registered == null) return result;
            foreach (Focusable control in registered)
                if (IsReachable(control) && (includeShoulders || !control.IsShoulderButton)
                    && !result.Contains(control)) result.Add(control);
            return result;
        }

        private static bool IsReachable(Focusable control)
        {
            if (control == null || !control.gameObject.activeInHierarchy || FocusNavigator.ShouldSkip(control)) return false;
            for (Transform node = control.transform; node != null; node = node.parent)
            {
                UIListItemBase row = node.GetComponent<UIListItemBase>();
                if (row != null && row.ParentUIList != null && !row.ParentUIList.CurrentItems.Contains(row))
                    return false;
            }
            return true;
        }

        internal static List<Focusable> GetOrdered(List<Focusable> registered)
        {
            var entries = new List<Entry>();
            foreach (Focusable control in Collect(registered, includeShoulders: false))
                entries.Add(new Entry { Control = control, Path = GetPath(control.transform), Registration = entries.Count });
            entries.Sort((a, b) =>
            {
                int shared = Math.Min(a.Path.Count, b.Path.Count);
                for (int i = 0; i < shared; i++)
                {
                    int order = a.Path[i].CompareTo(b.Path[i]);
                    if (order != 0) return order;
                }
                int length = a.Path.Count.CompareTo(b.Path.Count);
                return length != 0 ? length : a.Registration.CompareTo(b.Registration);
            });
            var ordered = new List<Focusable>(entries.Count);
            foreach (Entry entry in entries) ordered.Add(entry.Control);
            return ordered;
        }

        private static List<int> GetPath(Transform leaf)
        {
            var ancestors = new List<Transform>();
            for (Transform node = leaf; node != null; node = node.parent) ancestors.Add(node);
            ancestors.Reverse();
            var path = new List<int>(ancestors.Count * 2);
            foreach (Transform node in ancestors)
            {
                int anchor = node.GetSiblingIndex();
                int itemIndex = -1;
                UIListItemBase row = node.GetComponent<UIListItemBase>();
                if (row != null && row.ParentUIList != null)
                {
                    List<UIListItemBase> items = row.ParentUIList.CurrentItems;
                    itemIndex = items.IndexOf(row);
                    // Pool replacement moves a row to the last Transform sibling even when it
                    // replaces the first logical item (UIList.CreateItemWithoutInsert). Keep the
                    // whole list at its first sibling, then order its rows by CurrentItems.
                    foreach (UIListItemBase item in items)
                        if (item != null && item.transform.parent == node.parent)
                            anchor = Math.Min(anchor, item.transform.GetSiblingIndex());
                }
                path.Add(anchor);
                path.Add(itemIndex);
            }
            return path;
        }
    }
}
