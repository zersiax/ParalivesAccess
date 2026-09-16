using System.Collections.Generic;
using Setting;

namespace ParalivesAccess.Handlers
{
    /// <summary>Finds loaded stops from the same catalogue references as the native travel map.</summary>
    internal static class TransitStopReader
    {
        internal static bool IsStop(ItemObjectRoot root)
        {
            if (root == null) return false;
            FastTravelMethod[] methods = Settings.Get<FastTravelMethods>()?.AllFastTravelMethods;
            if (methods == null) return false;
            InteractionGroup group = InteractionManager.Instance?.GetItemInteractionGroup(root);
            foreach (FastTravelMethod method in methods)
            {
                if (method == null || !method.NeedsMapSelectionBasedOnItem
                    || method.CatalogueItems == null) continue;
                foreach (UlongAndGuid item in method.CatalogueItems)
                {
                    if (item == null || item.Value == 0UL) continue;
                    if (item.Value == root.CatalogueGUID) return true;
                    // The stop shelter and its numbered sign share a native interaction group;
                    // only the sign is used as a map destination.
                    CatalogueItem catalogue = Settings.Get<Items>()?.GetItemByGUID(item.Value);
                    if (group != null && catalogue != null && catalogue.OverrideInteractionGroup
                        && catalogue.Interaction != 0UL && catalogue.Interaction == group.GUID)
                        return true;
                }
            }
            return false;
        }

        internal static List<ItemObjectRoot> GetStops(Player player)
        {
            var result = new List<ItemObjectRoot>();
            List<ItemObjectRoot> items = ItemManager.Instance?.AllItems;
            if (items == null || player?.IsInIntro == true) return result;
            // Native instance lookup prunes destroyed entries from AllItems. Retain references
            // before validating identities so its cleanup cannot invalidate this enumeration.
            foreach (ItemObjectRoot root in items.ToArray())
                if (root != null && root != player?.ItemInPlacement?.Item
                    && root.gameObject != null && root.gameObject.activeInHierarchy && IsStop(root)
                    && WorldTargeting.IsSameLiveItem(root, root.InstanceID))
                    result.Add(root);
            return result;
        }

        internal static string GetLabel(ItemObjectRoot root)
        {
            string name = ItemDescription.GetProductIdentity(root) ?? Loc.Get("transit_stop");
            var text = root?.GetChildren<ItemObjectText>();
            if (text != null && text.Count > 0 && text[0] is ItemObjectText sign
                && !string.IsNullOrWhiteSpace(sign.Text))
                return Loc.Get("transit_stop_label", name, sign.Text.Trim());
            return name;
        }
    }
}
