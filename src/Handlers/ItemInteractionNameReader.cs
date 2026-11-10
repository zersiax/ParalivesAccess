using System;
using ParalivesAccess.Speech;
using Setting;

namespace ParalivesAccess.Handlers
{
    /// <summary>Reads the native interaction menu's object heading without opening a menu.</summary>
    internal static class ItemInteractionNameReader
    {
        internal static string GetName(CatalogueItem item, ulong skinGuid)
        {
            try
            {
                return ReadGroupName(GetCatalogueGroup(item), skinGuid, null);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Reading catalogue interaction heading threw: {ex}");
                return null;
            }
        }

        internal static string GetName(ItemObjectRoot root)
        {
            if (root == null) return null;
            try
            {
                // The native resolver includes spawner overrides, which a catalogue record lacks.
                return ReadGroupName(InteractionManager.Instance?.GetItemInteractionGroup(root),
                    root.CurrentSkinGUID, root);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Reading placed item interaction heading threw: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Mirrors InteractionManager.GetItemInteractionGroup for an item not yet instantiated.
        /// Explicit overrides are terminal, even when their target is absent. Tag and parent
        /// traversal retain native ordering; a missing tag is skipped with its ancestry.
        /// </summary>
        internal static InteractionGroup GetCatalogueGroup(CatalogueItem item)
        {
            if (item == null) return null;
            Interactions interactions = Settings.Get<Interactions>();
            if (interactions == null) return null;
            if (item.OverrideInteractionGroup)
                return interactions.GetInteractionGroupByGUID(item.Interaction);

            BuildModeCatalogTags tags = Settings.Get<BuildModeCatalogTags>();
            if (tags == null || item.Tag == null) return null;
            foreach (UlongAndGuid reference in item.Tag)
            {
                if (reference == null) continue;
                BuildModeCatalogTag tag = tags.GetTagByGUID(reference.Value);
                if (tag == null) continue;
                InteractionGroup group = interactions.GetInteractionGroupByGUID(tag.InteractionGroup);
                if (group != null) return group;
                foreach (ulong parentGuid in tags.GetParentTags(reference.Value))
                {
                    BuildModeCatalogTag parent = tags.GetTagByGUID(parentGuid);
                    if (parent == null) continue;
                    group = interactions.GetInteractionGroupByGUID(parent.InteractionGroup);
                    if (group != null) return group;
                }
            }
            return null;
        }

        private static string ReadGroupName(InteractionGroup group, ulong skinGuid, ItemObjectRoot root)
        {
            if (group == null) return null;
            // Same heading sources and precedence as UIInteractions.Show. Incomplete skin/lot
            // data falls back to the localized group rather than a pooled UI label or raw key.
            if (group.DisplayNameIsSkinnable && skinGuid != 0UL)
            {
                string key = Settings.Get<Skins>()?.GetSkinByGUID(skinGuid)
                    ?.GetProperty(group.SkinTagGUID)?.StringValue;
                string name = ReadTranslation(key);
                if (name != null) return name;
            }
            else if (group.DisplayNameIsImpostorLotType && root != null)
            {
                AssetLot lot = LotManager.Instance?.GetLotByGUID(root.IsImpostorForLot);
                LotType type = lot?.Data == null ? null
                    : Settings.Get<LotTypes>()?.GetLotTypeByGUID(lot.Data.LotTypeGUID);
                string name = ReadTranslation(type?.TranslationKey);
                if (name != null) return name;
            }
            return ReadTranslation(group.TranslationKey);
        }

        private static string ReadTranslation(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            string value = TranslationManager.Get(key);
            if (string.IsNullOrWhiteSpace(value) || value == key) return null;
            value = ScreenReader.NormalizeForOutput(value.Trim());
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }
}
