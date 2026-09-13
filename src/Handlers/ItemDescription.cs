using System;
using System.Collections.Generic;
using ParalivesAccess.Speech;
using Setting;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>
    /// Builds live, localized descriptions from the game's item and placement data.
    /// </summary>
    internal static class ItemDescription
    {
        internal static string GetName(CatalogueItem item, ulong skinGuid)
        {
            if (item == null || ItemManager.Instance == null)
            {
                return null;
            }

            try
            {
                string semanticName = GetSemanticInteractionName(item);
                if (!string.IsNullOrEmpty(semanticName))
                {
                    return semanticName;
                }

                string key = ItemManager.Instance.GetSkinnedDisplayName(item, skinGuid);
                string name = string.IsNullOrEmpty(key) ? null : TranslationManager.Get(key);
                return string.IsNullOrEmpty(name) || name == key ? null : name.Trim();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Reading catalogue item name threw: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Returns the authored product name which the native build-mode tooltip renders beneath
        /// the localized catalogue/collection name.
        /// </summary>
        /// <remarks>
        /// Despite the field name, <c>PatreonName</c> contains the submitted product title (for
        /// example, “The Burnadette” or “One Happy Mouse Fridge”), not merely a creator credit.
        /// It is deliberately raw text in the game and is not passed through TranslationManager.
        /// </remarks>
        internal static string GetAuthoredProductName(CatalogueItem item)
        {
            string name = item?.PatreonName;
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            name = ScreenReader.NormalizeForOutput(name.Trim());
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

        /// <summary>
        /// Returns a compact product-first identity followed by the native localized catalogue
        /// name. Items without an authored product title retain their previous name unchanged.
        /// </summary>
        internal static string GetProductIdentity(CatalogueItem item, ulong skinGuid)
        {
            string catalogueName = GetName(item, skinGuid);
            string productName = GetAuthoredProductName(item);
            if (string.IsNullOrEmpty(productName))
            {
                return catalogueName;
            }
            if (string.IsNullOrEmpty(catalogueName)
                || string.Equals(
                    productName, catalogueName, StringComparison.CurrentCultureIgnoreCase))
            {
                return productName;
            }

            return Loc.Get("item_product_identity", productName, catalogueName);
        }

        /// <summary>Resolves the compact product identity for a placed object.</summary>
        internal static string GetProductIdentity(ItemObjectRoot root)
        {
            if (root == null)
            {
                return null;
            }

            try
            {
                CatalogueItem item = Settings.Get<Items>()?.GetItemByGUID(root.CatalogueGUID);
                return GetProductIdentity(item, root.CurrentSkinGUID);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError(
                    $"Resolving placed item {root.InstanceID} product identity threw: {ex}");
                return GetName(root);
            }
        }

        /// <summary>
        /// Returns the same localized final catalogue category shown by the game's build-mode
        /// tooltip. Collection names such as Iconomi remain intact while their purpose becomes
        /// clear.
        /// </summary>
        internal static string GetCategory(CatalogueItem item)
        {
            if (item == null)
            {
                return null;
            }

            try
            {
                BuildModeCatalogTags tags = Settings.Get<BuildModeCatalogTags>();
                string category = tags?.GetTagsText(item.Tag, getLastTagOnly: true);
                return string.IsNullOrWhiteSpace(category)
                    ? null
                    : ScreenReader.NormalizeForOutput(category.Trim());
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Reading catalogue item category threw: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Describes a for-sale world item with the functional detail a sighted player gets from
        /// its model or thumbnail.
        /// </summary>
        internal static string GetShopProductName(ItemObjectRoot root)
        {
            if (root == null)
            {
                return null;
            }

            CatalogueItem item = Settings.Get<Items>()?.GetItemByGUID(root.CatalogueGUID);
            if (item == null)
            {
                return GetName(root);
            }

            return GetShopProductName(item, root.CurrentSkinGUID);
        }

        /// <summary>
        /// Describes a shop product when the game retains its catalogue identity but no world root,
        /// as it does in the purchase-completion notification.
        /// </summary>
        internal static string GetShopProductName(CatalogueItem item, ulong skinGuid)
        {
            if (item == null)
            {
                return null;
            }

            string nativeName = GetName(item, skinGuid);
            string productIdentity = GetProductIdentity(item, skinGuid);
            string category = GetCategory(item);
            if (!string.IsNullOrEmpty(category))
            {
                return string.IsNullOrEmpty(productIdentity)
                    ? category
                    : Loc.Get("label_with_detail", productIdentity, category);
            }

            string visibleEquivalent = GetMuseumProductVisualEquivalent(item, nativeName);
            if (string.IsNullOrEmpty(visibleEquivalent))
            {
                return productIdentity;
            }

            string authoredName = GetAuthoredProductName(item);
            return string.IsNullOrEmpty(authoredName)
                || string.Equals(
                    authoredName, visibleEquivalent,
                    StringComparison.CurrentCultureIgnoreCase)
                        ? visibleEquivalent
                        : Loc.Get("item_product_identity", authoredName, visibleEquivalent);
        }

        /// <summary>Returns the functional shop-product name followed by the native price.</summary>
        internal static string GetShopProductLabel(ItemObjectRoot root)
        {
            string name = GetShopProductName(root);
            CatalogueItem item = root != null
                ? Settings.Get<Items>()?.GetItemByGUID(root.CatalogueGUID)
                : null;
            if (string.IsNullOrEmpty(name) || item == null)
            {
                return name;
            }

            string price = ScreenReader.NormalizeForOutput(
                FormatService.GetMoney(item.GetPrice()));
            return string.IsNullOrEmpty(price)
                ? name
                : Loc.Get("shop_product_with_price", name, price);
        }

        /// <summary>
        /// Supplies the product noun omitted by the shipped museum translations and empty tags.
        /// </summary>
        /// <remarks>
        /// WORKAROUND: the native live/build tooltip has only localized name, final catalogue tag,
        /// price and a thumbnail. The museum merchandise has no catalogue tag, while thirteen
        /// different products share the text “Turichimi” and “Hector” omits that it is a plush.
        /// The stable catalogue identifiers encode the model-visible nouns for the whole authored
        /// TownMuseum family. Keep this family-scoped instead of exposing arbitrary developer names
        /// from other games or user mods.
        /// </remarks>
        private static string GetMuseumProductVisualEquivalent(
            CatalogueItem item, string nativeName)
        {
            const string prefix = "TownMuseum";
            string identifier = item?.DisplayName;
            if (string.IsNullOrEmpty(identifier)
                || !identifier.StartsWith(prefix, StringComparison.Ordinal))
            {
                return null;
            }

            string product = identifier.Substring(prefix.Length);
            string subject;
            string kindKey;
            if (TrySplitProduct(product, "Plush", out subject))
            {
                kindKey = "shop_product_kind_plush";
            }
            else if (TrySplitProduct(product, "Mug", out subject))
            {
                kindKey = "shop_product_kind_mug";
            }
            else if (TrySplitProduct(product, "Poster", out subject))
            {
                kindKey = "shop_product_kind_poster";
            }
            else if (TrySplitProduct(product, "Tote", out subject))
            {
                kindKey = "shop_product_kind_tote";
            }
            else if (product.StartsWith("Puzzle", StringComparison.Ordinal))
            {
                subject = product.Substring("Puzzle".Length);
                bool boxed = subject.EndsWith("Box", StringComparison.Ordinal);
                if (boxed)
                {
                    subject = subject.Substring(0, subject.Length - "Box".Length);
                }
                kindKey = boxed
                    ? "shop_product_kind_puzzle_box"
                    : "shop_product_kind_puzzle";
            }
            else if (product.StartsWith("SnowGlobe", StringComparison.Ordinal))
            {
                subject = product.Substring("SnowGlobe".Length);
                kindKey = "shop_product_kind_snow_globe";
            }
            else
            {
                return null;
            }

            string subjectName = FormatMuseumProductSubject(subject, nativeName);
            string kind = Loc.Get(kindKey);
            return string.IsNullOrEmpty(subjectName) || string.IsNullOrEmpty(kind)
                ? null
                : Loc.Get("shop_product_named_kind", subjectName, kind);
        }

        private static bool TrySplitProduct(
            string product, string prefix, out string subject)
        {
            subject = null;
            if (string.IsNullOrEmpty(product)
                || !product.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            subject = product.Substring(prefix.Length);
            return true;
        }

        private static string FormatMuseumProductSubject(
            string subject, string nativeName)
        {
            if (string.IsNullOrEmpty(subject))
            {
                return nativeName;
            }
            if (string.Equals(subject, "ILOVEPRLV", StringComparison.Ordinal))
            {
                return Loc.Get("shop_product_i_love_prlv");
            }

            return FormatService.AddSpacesBetweenUpperCamelCaseWords(
                subject, onlyCapitalizeFirstLetter: false);
        }

        /// <summary>
        /// Corrects the shipped request board's placeholder item name from the semantic action it
        /// actually exposes. Other catalogue items keep their ordinary localized item names.
        /// </summary>
        /// <remarks>
        /// The board is stored as CityBulletinRequestBoard but its English Item_ translation is
        /// "Chiti". Its override interaction group contains the exact OpenOfferedRequests action
        /// and has the localized title "Request board". Reading that graph makes the F7 object
        /// route discoverable without depending on either the placeholder or a hard-coded GUID.
        /// </remarks>
        private static string GetSemanticInteractionName(CatalogueItem item)
        {
            if (item == null || !item.OverrideInteractionGroup || item.Interaction == 0UL)
            {
                return null;
            }

            Interactions interactions = Settings.Get<Interactions>();
            InteractionGroup group = interactions?.GetInteractionGroupByGUID(item.Interaction);
            if (group == null || !ContainsInteraction(
                interactions, group, "OpenOfferedRequests", new HashSet<ulong>()))
            {
                return null;
            }

            string key = group.TranslationKey;
            string translated = string.IsNullOrEmpty(key) ? null : TranslationManager.Get(key);
            return string.IsNullOrEmpty(translated) || translated == key
                ? null
                : translated.Trim();
        }

        private static bool ContainsInteraction(
            Interactions interactions, InteractionGroup group, string displayName,
            HashSet<ulong> visitedGroups)
        {
            if (interactions == null || group == null || group.ChildrenInteractionAndGroups == null
                || !visitedGroups.Add(group.GUID))
            {
                return false;
            }

            foreach (InteractionGroupItem child in group.ChildrenInteractionAndGroups)
            {
                if (child == null)
                {
                    continue;
                }

                if (child.Type == InteractionItemType.Interaction)
                {
                    InteractionUnit interaction =
                        interactions.GetInteractionByGUID(child.Interaction);
                    if (interaction != null
                        && string.Equals(interaction.DisplayName, displayName,
                            StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                else if (child.Type == InteractionItemType.Group)
                {
                    InteractionGroup nested =
                        interactions.GetInteractionGroupByGUID(child.Group);
                    if (ContainsInteraction(
                        interactions, nested, displayName, visitedGroups))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        internal static string GetName(ItemObjectRoot root)
        {
            if (root == null)
            {
                return null;
            }

            try
            {
                CatalogueItem item = Settings.Get<Items>().GetItemByGUID(root.CatalogueGUID);
                return GetName(item, root.CurrentSkinGUID);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Resolving placed item {root.InstanceID} name threw: {ex}");
                return null;
            }
        }

        /// <summary>Resolves a placed item's native localized catalogue category.</summary>
        internal static string GetCategory(ItemObjectRoot root)
        {
            if (root == null)
            {
                return null;
            }

            try
            {
                CatalogueItem item = Settings.Get<Items>().GetItemByGUID(root.CatalogueGUID);
                return GetCategory(item);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError(
                    $"Resolving placed item {root.InstanceID} category threw: {ex}");
                return null;
            }
        }

        internal static string DescribeLocation(ItemObjectRoot root, Vector3? referencePosition)
        {
            if (root == null)
            {
                return null;
            }

            string relation = GetRelation(root);
            string layer = Loc.Get("item_floor_layer", root.Layer);

            if (referencePosition.HasValue && root.CubeTransform != null)
            {
                float distance = Vector3.Distance(referencePosition.Value, root.CubeTransform.LocalPosition);
                string formatted = ScreenReader.NormalizeForOutput(
                    MeasurementUnitService.FormatLength(distance));
                return string.IsNullOrEmpty(relation)
                    ? Loc.Get("item_location_distance", layer, formatted)
                    : Loc.Get("item_location_relation_distance", layer, relation, formatted);
            }

            return string.IsNullOrEmpty(relation)
                ? layer
                : Loc.Get("item_location_relation", layer, relation);
        }

        /// <summary>
        /// Describes only the item's placement relationship, for callers that already supply a
        /// floor/room path and must not repeat the layer.
        /// </summary>
        internal static string DescribePlacementRelation(ItemObjectRoot root)
        {
            return root == null ? null : GetRelation(root);
        }

        private static string GetRelation(ItemObjectRoot root)
        {
            ItemObjectRoot parent = root.ItemPlacedOn;
            if (parent != null)
            {
                return Loc.Get("item_on_parent", GetName(parent) ?? Loc.Get("item_unknown"));
            }

            parent = root.ItemAttachedTo;
            if (parent != null)
            {
                return Loc.Get("item_attached_parent", GetName(parent) ?? Loc.Get("item_unknown"));
            }

            if (root.FloorPlacedOn != null)
            {
                return Loc.Get("item_on_floor");
            }

            if (root.PlacedOnTerrain)
            {
                return Loc.Get("item_on_terrain");
            }

            return null;
        }
    }
}
