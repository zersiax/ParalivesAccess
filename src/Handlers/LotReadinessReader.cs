using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ParalivesAccess.Speech;
using Setting;
using TMPro;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>
    /// Describes a lot's concrete structure, contents and first-day facilities.
    /// </summary>
    /// <remarks>
    /// Paralives exposes no <c>HasHouse</c> or <c>Prefurnished</c> flag. This reader therefore says
    /// only what the effective lot asset proves: walls, zones, layers, placed items, the game's own
    /// bedroom/bathroom counts, and facilities identified by setting GUIDs. See game-api.md
    /// sections 42 and 43.
    /// </remarks>
    internal static class LotReadinessReader
    {
        [Flags]
        internal enum FacilityKind
        {
            None = 0,
            Bed = 1,
            Toilet = 2,
            Shower = 4,
            Fridge = 8,
            Counter = 16,
            Stovetop = 32,
            Oven = 64,
            Microwave = 128,
            ShopPurchase = 256,
            MuseumDonation = 512
        }

        [Flags]
        private enum CivicOutcomeKind
        {
            None = 0,
            PurchaseItem = 1,
            PurchaseFood = 2,
            PurchaseOutfit = 4,
            MuseumDonation = 8
        }

        private sealed class CivicGlobalCandidate
        {
            internal CivicOutcomeKind Kinds { get; set; }
            internal bool PurchaseItemUsesForSaleStateOnly { get; set; }
        }

        private const ulong ShowerGroup = 6711141875056105361UL;
        private const ulong FridgeGroup = 3254832892911657418UL;
        private const ulong BedGroup = 7297545825684127626UL;
        private const ulong ToiletGroup = 8552409619233738241UL;
        private const ulong CounterGroup = 598734596320631140UL;
        private const ulong MicrowaveGroup = 566684092147992618UL;

        private const ulong CookableMicrowaveTag = 6028666573917762814UL;
        private const ulong CookableOvenTag = 3592977563426538460UL;
        private const ulong CookableStovetopTag = 7243011314502853271UL;

        private static bool _lotFieldResolved;
        private static FieldInfo _lotField;
        private static bool _facilitySettingsChecked;
        private static bool _facilitySettingsUsable;
        private static Interactions _civicInteractionsSource;
        private static Actions _civicActionsSource;
        private static bool _civicGlobalIndexReady;
        private static readonly Dictionary<ulong, CivicOutcomeKind> _civicGroupKinds =
            new Dictionary<ulong, CivicOutcomeKind>();
        private static readonly Dictionary<ulong, CivicGlobalCandidate> _civicGlobalKinds =
            new Dictionary<ulong, CivicGlobalCandidate>();

        /// <summary>
        /// Classifies a live item from verified interaction-group and functional-tag GUIDs.
        /// </summary>
        internal static FacilityKind ClassifyFacility(ItemObjectRoot root)
        {
            if (root == null || !ValidateFacilitySettings())
            {
                return FacilityKind.None;
            }

            try
            {
                CatalogueItem item = Settings.Get<Items>().GetItemByGUID(root.CatalogueGUID);
                ulong group = InteractionManager.Instance?.GetItemInteractionGroup(root)?.GUID ?? 0UL;
                FacilityKind result = FacilityKind.None;
                if (group == BedGroup)
                {
                    result |= FacilityKind.Bed;
                }
                else if (group == ToiletGroup)
                {
                    result |= FacilityKind.Toilet;
                }
                else if (group == ShowerGroup)
                {
                    result |= FacilityKind.Shower;
                }
                else if (group == FridgeGroup)
                {
                    result |= FacilityKind.Fridge;
                }
                else if (group == CounterGroup)
                {
                    result |= FacilityKind.Counter;
                }
                else if (group == MicrowaveGroup)
                {
                    result |= FacilityKind.Microwave;
                }

                if (Snapshot.HasTag(item, CookableMicrowaveTag))
                {
                    result |= FacilityKind.Microwave;
                }
                if (Snapshot.HasTag(item, CookableOvenTag))
                {
                    result |= FacilityKind.Oven;
                }
                if (Snapshot.HasTag(item, CookableStovetopTag))
                {
                    result |= FacilityKind.Stovetop;
                }

                return result;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Classifying a live lot facility threw: " + ex);
                return FacilityKind.None;
            }
        }

        /// <summary>
        /// Classifies civic fixtures from the outcomes of their native item interactions.
        /// </summary>
        /// <remarks>
        /// A cash register is primarily an NPC work target, while purchases are attached to the
        /// merchandise or food display being bought. Following the interaction/action graph keeps
        /// the finder tied to that gameplay behavior and also catches skinned names such as
        /// “Purchase snack.” The small set of global interactions carrying either outcome is
        /// indexed once. The exact state-only PurchaseItem row reads its native IsForSale state;
        /// every other global candidate uses the same character-requirement and usability calls
        /// as <c>ItemHasInteractions</c>. Group capability is immutable game setting data and is
        /// cached by group GUID; live global usability is never cached.
        /// </remarks>
        internal static FacilityKind ClassifyCivicFacility(
            ItemObjectRoot root, Player player)
        {
            if (root == null || player == null || InteractionManager.Instance == null)
            {
                return FacilityKind.None;
            }

            try
            {
                Interactions interactions = Settings.Get<Interactions>();
                Actions actions = Settings.Get<Actions>();
                if (interactions == null || actions == null)
                {
                    return FacilityKind.None;
                }

                EnsureCivicIndexes(interactions, actions);

                FacilityKind result = FacilityKind.None;
                InteractionGroup group =
                    InteractionManager.Instance.GetItemInteractionGroup(root);
                if (group != null)
                {
                    result |= ToFacilityKinds(
                        GetCivicGroupKinds(interactions, actions, group.GUID));
                }

                if (_civicGlobalKinds.Count > 0
                    && player.SelectedCharactersGUID != null
                    && player.SelectedCharactersGUID.Count > 0
                    && CharacterManager.Instance != null)
                {
                    // Current ItemHasInteractions/GetGlobalInteractions evaluates global rows for
                    // the primary selected character only. Match that exact menu boundary.
                    ulong characterGuid = player.SelectedCharactersGUID[0];
                    foreach (KeyValuePair<ulong, CivicGlobalCandidate> candidate in
                        _civicGlobalKinds)
                    {
                        CivicOutcomeKind kindsToEvaluate = candidate.Value.Kinds;
                        // The shipped Purchase interaction's only authored local rule is the exact
                        // ItemStateForSale == 1 predicate read by ItemObjectRoot.IsForSale. Only an
                        // interaction proven to have that shape takes this shortcut; another
                        // present or future PurchaseItem interaction retains full evaluation.
                        if (candidate.Value.PurchaseItemUsesForSaleStateOnly)
                        {
                            if (root.IsForSale)
                            {
                                result |= FacilityKind.ShopPurchase;
                            }
                            kindsToEvaluate &= ~CivicOutcomeKind.PurchaseItem;
                        }
                        if (kindsToEvaluate == CivicOutcomeKind.None)
                        {
                            continue;
                        }

                        InteractionUnit interaction =
                            interactions.GetInteractionByGUID(candidate.Key);
                        if (interaction == null)
                        {
                            continue;
                        }
                        if (!CharacterManager.Instance.AnyCharacterHasCharacterRequirement(
                            new[] { characterGuid },
                            interaction.CharacterRequirement))
                        {
                            continue;
                        }

                        List<InteractionUsabilityRule> failures =
                            InteractionManager.Instance
                                .GetFaillingInteractionUsabilityRule(
                                    interaction,
                                    root,
                                    -1,
                                    player.PlayerIndex,
                                    characterGuid,
                                    0UL,
                                    0UL,
                                    false,
                                    null);
                        bool hidden = failures.Count > 0;
                        foreach (InteractionUsabilityRule failure in failures)
                        {
                            if (failure.DisplayRule == HowToDisplayIfNotMet.GreyOut)
                            {
                                hidden = false;
                                break;
                            }
                        }
                        if (!hidden)
                        {
                            result |= ToFacilityKinds(kindsToEvaluate);
                        }
                    }
                }
                return result;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError(
                    "Classifying a live civic facility threw: " + ex);
                return FacilityKind.None;
            }
        }

        private static void EnsureCivicIndexes(
            Interactions interactions, Actions actions)
        {
            bool sourceChanged = !ReferenceEquals(_civicInteractionsSource, interactions)
                || !ReferenceEquals(_civicActionsSource, actions);
            if (sourceChanged)
            {
                _civicInteractionsSource = interactions;
                _civicActionsSource = actions;
                _civicGlobalIndexReady = false;
                _civicGroupKinds.Clear();
                _civicGlobalKinds.Clear();
            }

            if (_civicGlobalIndexReady || interactions.GlobalInteractions == null)
            {
                return;
            }
            _civicGlobalIndexReady = true;
            foreach (UlongAndGuid reference in interactions.GlobalInteractions)
            {
                ulong interactionGuid = reference?.Value ?? 0UL;
                InteractionUnit interaction = interactionGuid != 0UL
                    ? interactions.GetInteractionByGUID(interactionGuid)
                    : null;
                if (interaction == null)
                {
                    continue;
                }
                CivicOutcomeKind kinds = ClassifyCivicAction(
                    actions, interaction.ActionGUID, new HashSet<ulong>());
                if (kinds != CivicOutcomeKind.None)
                {
                    _civicGlobalKinds[interactionGuid] = new CivicGlobalCandidate
                    {
                        Kinds = kinds,
                        PurchaseItemUsesForSaleStateOnly =
                            IsForSaleStateOnlyPurchase(interaction, kinds)
                    };
                }
            }
        }

        private static bool IsForSaleStateOnlyPurchase(
            InteractionUnit interaction, CivicOutcomeKind kinds)
        {
            if (interaction == null
                || (kinds & CivicOutcomeKind.PurchaseItem) == 0
                || interaction.CharacterRequirement != 0UL
                || interaction.InteractionUsabilityRules == null
                || interaction.InteractionUsabilityRules.Length != 1)
            {
                return false;
            }

            InteractionUsabilityRule rule = interaction.InteractionUsabilityRules[0];
            ContextRequirement requirement = rule?.Requirement;
            ulong forSaleState = Settings.Get<ItemStates>()?.ItemStateForSale ?? 0UL;
            return requirement != null
                && forSaleState != 0UL
                && requirement.Type == ContextRequirementType.ItemState
                && requirement.ItemState == forSaleState
                && requirement.Rule == ComparaisonOperator.Equal
                && requirement.Value == 1f;
        }

        private static CivicOutcomeKind GetCivicGroupKinds(
            Interactions interactions, Actions actions, ulong groupGuid)
        {
            CivicOutcomeKind cached;
            if (_civicGroupKinds.TryGetValue(groupGuid, out cached))
            {
                return cached;
            }

            CivicOutcomeKind result = CivicOutcomeKind.None;
            var descendants = new List<ulong>();
            interactions.GetAllChildrenInteractionsOfGroup(descendants, groupGuid);
            foreach (ulong interactionGuid in descendants)
            {
                InteractionUnit interaction =
                    interactions.GetInteractionByGUID(interactionGuid);
                if (interaction != null)
                {
                    result |= ClassifyCivicAction(
                        actions, interaction.ActionGUID, new HashSet<ulong>());
                }
            }
            _civicGroupKinds[groupGuid] = result;
            return result;
        }

        private static CivicOutcomeKind ClassifyCivicAction(
            Actions actions,
            ulong actionGuid,
            HashSet<ulong> visited)
        {
            if (actions == null || actionGuid == 0UL || !visited.Add(actionGuid))
            {
                return CivicOutcomeKind.None;
            }

            ActionUnit action = actions.GetActionByGUID(actionGuid);
            if (action == null)
            {
                return CivicOutcomeKind.None;
            }

            CivicOutcomeKind result = ClassifyCivicOutcomes(action.Outcomes);
            if (action.Items != null)
            {
                foreach (ActionUnitBackpack child in action.Items)
                {
                    if (child != null)
                    {
                        result |= ClassifyCivicAction(
                            actions, child.ActionUnit, visited);
                    }
                }
            }

            result |= ClassifyCivicAction(
                actions, action.ActionSuccess, visited);
            result |= ClassifyCivicAction(
                actions, action.ActionFailure, visited);
            result |= ClassifyCivicAction(
                actions, action.OverrideActionWhenItemNotFound, visited);
            result |= ClassifyCivicAction(
                actions, action.ActionOnCancelIfHasDoneOnPreStart, visited);
            return result;
        }

        private static CivicOutcomeKind ClassifyCivicOutcomes(Outcome[] outcomes)
        {
            CivicOutcomeKind result = CivicOutcomeKind.None;
            if (outcomes == null)
            {
                return result;
            }

            foreach (Outcome outcome in outcomes)
            {
                if (outcome == null)
                {
                    continue;
                }
                if (outcome.Type == OutcomeType.OpenMuseumDonation)
                {
                    result |= CivicOutcomeKind.MuseumDonation;
                }
                else if (outcome.Type == OutcomeType.PurchaseItem)
                {
                    result |= CivicOutcomeKind.PurchaseItem;
                }
                else if (outcome.Type == OutcomeType.AddPurchasedFoodItemToInventory)
                {
                    result |= CivicOutcomeKind.PurchaseFood;
                }
                else if (outcome.Type == OutcomeType.PurchaseOutfit)
                {
                    result |= CivicOutcomeKind.PurchaseOutfit;
                }

                result |= ClassifyCivicOutcomes(outcome.OutcomesSuccess);
                result |= ClassifyCivicOutcomes(outcome.OutcomesFailure);
            }
            return result;
        }

        private static FacilityKind ToFacilityKinds(CivicOutcomeKind kinds)
        {
            FacilityKind result = FacilityKind.None;
            if ((kinds & (CivicOutcomeKind.PurchaseItem
                | CivicOutcomeKind.PurchaseFood
                | CivicOutcomeKind.PurchaseOutfit)) != 0)
            {
                result |= FacilityKind.ShopPurchase;
            }
            if ((kinds & CivicOutcomeKind.MuseumDonation) != 0)
            {
                result |= FacilityKind.MuseumDonation;
            }
            return result;
        }

        /// <summary>Returns localized facility functions for an already classified live item.</summary>
        internal static string DescribeFacilityKinds(FacilityKind kinds)
        {
            var labels = new List<string>();
            AddFacilityLabel(labels, kinds, FacilityKind.Bed, "facility_bed");
            AddFacilityLabel(labels, kinds, FacilityKind.Toilet, "facility_toilet");
            AddFacilityLabel(labels, kinds, FacilityKind.Shower, "facility_shower");
            AddFacilityLabel(labels, kinds, FacilityKind.Fridge, "facility_fridge");
            AddFacilityLabel(labels, kinds, FacilityKind.Counter, "facility_counter");
            AddFacilityLabel(labels, kinds, FacilityKind.Stovetop, "facility_stovetop");
            AddFacilityLabel(labels, kinds, FacilityKind.Oven, "facility_oven");
            AddFacilityLabel(labels, kinds, FacilityKind.Microwave, "facility_microwave");
            AddFacilityLabel(
                labels, kinds, FacilityKind.ShopPurchase, "facility_shop_purchase");
            AddFacilityLabel(
                labels, kinds, FacilityKind.MuseumDonation, "facility_museum_donation");
            return labels.Count == 0
                ? null
                : string.Join(Loc.Get("list_separator"), labels.ToArray());
        }

        private static void AddFacilityLabel(
            List<string> labels, FacilityKind value, FacilityKind flag, string key)
        {
            if ((value & flag) != 0)
            {
                labels.Add(Loc.Get(key));
            }
        }

        /// <summary>Keeps browsing concise; F3 reads the lot's identity and additional native information.</summary>
        public static string DescribeTownMapLot(UITownMapLotIcon icon, string gameLabel, bool includeDetails = false)
        {
            if (icon == null || !includeDetails)
            {
                return gameLabel;
            }

            try
            {
                LotPerimeterZoneObject perimeter = ZoneManager.Instance
                    ?.GetLotPerimeterZoneObjectByGUID(icon.LotGUID);
                var parts = new List<string>();
                AssetLot lot = GetLot(perimeter != null ? perimeter.RealLotGUID : icon.LotGUID);
                AddLotIdentity(parts, perimeter, lot);
                AddUnique(parts, ScreenReader.NormalizeForOutput(gameLabel));
                AddUnique(parts, ScreenReader.NormalizeForOutput(perimeter?.Description));
                AddUnique(parts, LotActivityReader.Describe(lot));
                return string.Join(Loc.Get("lot_detail_separator"), parts.ToArray());
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Reading town-map lot text threw: " + ex);
                return gameLabel;
            }
        }

        /// <summary>Builds the semantic summary for the open lot-information window.</summary>
        public static string DescribeLotInfo(UILotInfo info, bool includeReadiness = true)
        {
            if (info == null)
            {
                return null;
            }

            try
            {
                LotPerimeterZoneObject perimeter = GetPerimeter(info);
                AssetLot lot = GetLot(info.GetLotGUID());
                if (includeReadiness && lot == null)
                {
                    return null;
                }

                var parts = new List<string>();
                if (!includeReadiness) AddLotIdentity(parts, perimeter, lot);
                string description = ScreenReader.NormalizeForOutput(perimeter?.Description);
                AddUnique(parts, description);
                AddUnique(parts, ReadText(info.LotTypeLabel));
                if (includeReadiness) AddUnique(parts, ReadText(info.LotSizeLabel));

                if (info.LotHouseholdContainer != null
                    && info.LotHouseholdContainer.activeInHierarchy)
                {
                    string owner = ReadText(info.LotHouseholdNameLabel);
                    AddUnique(parts, string.IsNullOrEmpty(owner)
                        ? Loc.Get("lot_owned")
                        : Loc.Get("lot_owned_by", owner));
                }
                else if (info.ForSale != null && info.ForSale.activeInHierarchy)
                {
                    AddUnique(parts, Loc.Get("lot_for_sale_status"));
                }

                AddUnique(parts, ReadActiveTranslatedText(info.BuyButtonText));
                AddUnique(parts, ReadActiveTranslatedText(info.MessageText));
                if (!includeReadiness) AddUnique(parts, LotActivityReader.Describe(lot));

                if (includeReadiness)
                {
                    Snapshot snapshot = Snapshot.Create(lot);
                    AddUnique(parts, snapshot.DescribeStructure());
                    AddUnique(parts, snapshot.DescribeFacilities());
                }

                return parts.Count == 0
                    ? Loc.Get("panel_lot_info")
                    : Loc.Get("lot_info_summary",
                        string.Join(Loc.Get("lot_detail_separator"), parts.ToArray()));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Reading UILotInfo readiness threw: " + ex);
                return null;
            }
        }

        private static void AddLotIdentity(List<string> parts, LotPerimeterZoneObject perimeter, AssetLot lot)
        {
            AddUnique(parts, ScreenReader.NormalizeForOutput(perimeter?.DisplayName));
            // Use the instance address, not a proxy/template's potentially different street.
            AddUnique(parts, ScreenReader.NormalizeForOutput(perimeter?.GetAddress()));
            ulong[] companies = lot?.Data?.Companies;
            if (companies == null || companies.Length == 0) return;
            Occupations occupations = Settings.Get<Occupations>();
            foreach (ulong guid in companies)
            {
                Company company = occupations?.GetCompanyByGUID(guid);
                if (string.IsNullOrWhiteSpace(company?.DisplayName)) continue;
                string key = "CompanyName_" + company.DisplayName;
                string name = ScreenReader.NormalizeForOutput(TranslationManager.Get(key));
                if (!string.IsNullOrWhiteSpace(name) && !string.Equals(name, key, StringComparison.Ordinal))
                    AddUnique(parts, name);
            }
        }

        private static AssetLot GetLot(ulong guid)
        {
            if (guid == 0UL)
            {
                return null;
            }

            AssetLot lot = LotManager.Instance?.GetLotByGUID(guid);
            if (lot == null && AssetManager.Instance != null)
            {
                lot = AssetManager.Instance.GetAsset(guid) as AssetLot;
            }

            // Accessing Data is the asset's own synchronous load path and registers the lot with
            // LotManager. Do it once here so every count below reads the same effective asset.
            AssetLotData ignored = lot?.Data;
            return lot;
        }

        private static LotPerimeterZoneObject GetPerimeter(UILotInfo info)
        {
            if (!_lotFieldResolved)
            {
                _lotFieldResolved = true;
                _lotField = AccessTools.Field(typeof(UILotInfo), "_lot");
                if (_lotField == null)
                {
                    Plugin.Log.LogError(
                        "Could not resolve UILotInfo._lot. Lot summaries will omit address and "
                      + "ownership context; re-check game-api.md section 43.");
                }
            }

            return _lotField?.GetValue(info) as LotPerimeterZoneObject;
        }

        private static string ReadText(TMP_Text label)
        {
            if (label == null || !label.gameObject.activeInHierarchy
                || string.IsNullOrWhiteSpace(label.text))
            {
                return null;
            }

            return label.text.Trim();
        }

        private static string ReadActiveTranslatedText(TranslatedText label)
        {
            return label == null || !label.gameObject.activeInHierarchy
                ? null
                : ReadText(label.Text);
        }

        private static void AddUnique(List<string> parts, string value)
        {
            if (!string.IsNullOrWhiteSpace(value) && !parts.Contains(value.Trim()))
            {
                parts.Add(value.Trim());
            }
        }

        private static bool ValidateFacilitySettings()
        {
            if (_facilitySettingsChecked)
            {
                return _facilitySettingsUsable;
            }

            _facilitySettingsChecked = true;
            try
            {
                Interactions interactions = Settings.Get<Interactions>();
                BuildModeCatalogTags tags = Settings.Get<BuildModeCatalogTags>();
                _facilitySettingsUsable = interactions != null && tags != null
                    && interactions.GetInteractionGroupByGUID(ShowerGroup) != null
                    && interactions.GetInteractionGroupByGUID(FridgeGroup) != null
                    && interactions.GetInteractionGroupByGUID(BedGroup) != null
                    && interactions.GetInteractionGroupByGUID(ToiletGroup) != null
                    && interactions.GetInteractionGroupByGUID(CounterGroup) != null
                    && interactions.GetInteractionGroupByGUID(MicrowaveGroup) != null
                    && tags.GetTagByGUID(CookableMicrowaveTag) != null
                    && tags.GetTagByGUID(CookableOvenTag) != null
                    && tags.GetTagByGUID(CookableStovetopTag) != null;

                if (!_facilitySettingsUsable)
                {
                    Plugin.Log.LogWarning(
                        "[LotReadiness] One or more verified facility setting GUIDs are absent. "
                      + "Facility classification is disabled rather than falling back to names.");
                }
            }
            catch (Exception ex)
            {
                _facilitySettingsUsable = false;
                Plugin.Log.LogWarning(
                    "[LotReadiness] Facility setting validation failed; classification disabled: "
                  + ex);
            }

            return _facilitySettingsUsable;
        }

        private sealed class Snapshot
        {
            public int Walls;
            public int Zones;
            public int Layers;
            public int Items;
            public int Bedrooms;
            public int Bathrooms;
            public bool FacilitiesUsable;
            public int Beds;
            public int Toilets;
            public int Showers;
            public int Fridges;
            public int Counters;
            public int Stovetops;
            public int Ovens;
            public int Microwaves;

            public static Snapshot Create(AssetLot lot)
            {
                AssetLotData data = lot.Data;
                var snapshot = new Snapshot
                {
                    Walls = data.Walls?.Count ?? 0,
                    Zones = data.Zones?.Count ?? 0,
                    Bedrooms = LotManager.Instance.GetNbBedroomsInLot(lot),
                    Bathrooms = LotManager.Instance.GetNbBathroomsInLot(lot),
                    FacilitiesUsable = ValidateFacilitySettings()
                };

                var layers = new HashSet<int>();
                if (data.Walls != null)
                {
                    foreach (AssetLotDataWall wall in data.Walls)
                    {
                        if (wall != null)
                        {
                            layers.Add(wall.Layer);
                        }
                    }
                }
                if (data.Zones != null)
                {
                    foreach (AssetLotDataZone zone in data.Zones)
                    {
                        if (zone != null)
                        {
                            layers.Add(zone.Layer);
                        }
                    }
                }

                var serialized = new Dictionary<int, AssetLotDataItem>();
                AddSerialized(serialized, data.Items);
                AddSerialized(serialized, lot.UnloadedItems);

                var liveIds = new HashSet<int>();
                if (lot.LotItems != null)
                {
                    foreach (ItemObjectRoot root in lot.LotItems)
                    {
                        if (root == null || !liveIds.Add(root.InstanceID))
                        {
                            continue;
                        }

                        snapshot.Items++;
                        layers.Add(root.Layer);
                        snapshot.CountLiveFacility(root);
                    }
                }

                foreach (KeyValuePair<int, AssetLotDataItem> pair in serialized)
                {
                    if (liveIds.Contains(pair.Key))
                    {
                        continue;
                    }

                    snapshot.Items++;
                    layers.Add(pair.Value.Layer);
                    snapshot.CountSerializedFacility(pair.Value);
                }

                snapshot.Layers = layers.Count;
                return snapshot;
            }

            public string DescribeStructure()
            {
                if (Walls == 0 && Zones == 0 && Items == 0)
                {
                    return Loc.Get("lot_structure_empty");
                }

                if (Walls == 0 && Zones == 0)
                {
                    return Loc.Get("lot_structure_items_only", Items, Layers, Bedrooms, Bathrooms);
                }

                return Loc.Get("lot_structure_built",
                    Walls, Zones, Layers, Items, Bedrooms, Bathrooms);
            }

            public string DescribeFacilities()
            {
                if (!FacilitiesUsable)
                {
                    return Loc.Get("lot_facilities_unavailable");
                }

                var present = new List<string>();
                var missing = new List<string>();
                AddPresence(present, missing, Beds, "facility_bed");
                AddPresence(present, missing, Toilets, "facility_toilet");
                AddPresence(present, missing, Showers, "facility_shower");
                AddPresence(present, missing, Fridges, "facility_fridge");

                string essential;
                if (present.Count == 0)
                {
                    essential = Loc.Get("lot_facilities_none");
                }
                else if (missing.Count == 0)
                {
                    essential = Loc.Get("lot_facilities_complete",
                        string.Join(Loc.Get("list_separator"), present.ToArray()));
                }
                else
                {
                    essential = Loc.Get("lot_facilities_partial",
                        string.Join(Loc.Get("list_separator"), present.ToArray()),
                        string.Join(Loc.Get("list_separator"), missing.ToArray()));
                }

                var cooking = new List<string>();
                AddIfPresent(cooking, Counters, "facility_counter");
                AddIfPresent(cooking, Stovetops, "facility_stovetop");
                AddIfPresent(cooking, Ovens, "facility_oven");
                AddIfPresent(cooking, Microwaves, "facility_microwave");
                string cookingText = cooking.Count == 0
                    ? Loc.Get("lot_cooking_none")
                    : Loc.Get("lot_cooking_found",
                        string.Join(Loc.Get("list_separator"), cooking.ToArray()));

                return Loc.Get("lot_readiness", essential, cookingText);
            }

            private static void AddSerialized(
                Dictionary<int, AssetLotDataItem> target, List<AssetLotDataItem> source)
            {
                if (source == null)
                {
                    return;
                }

                foreach (AssetLotDataItem item in source)
                {
                    if (item != null)
                    {
                        target[item.InstanceID] = item;
                    }
                }
            }

            private void CountLiveFacility(ItemObjectRoot root)
            {
                if (!FacilitiesUsable)
                {
                    return;
                }

                CatalogueItem item = Settings.Get<Items>().GetItemByGUID(root.CatalogueGUID);
                InteractionGroup group = InteractionManager.Instance
                    ?.GetItemInteractionGroup(root);
                CountFacility(item, group?.GUID ?? ResolveCatalogueGroup(item, 0UL));
            }

            private void CountSerializedFacility(AssetLotDataItem record)
            {
                if (!FacilitiesUsable)
                {
                    return;
                }

                CatalogueItem item = Settings.Get<Items>().GetItemByGUID(record.CatalogueGUID);
                CountFacility(item, ResolveCatalogueGroup(item, record.ItemSpawnerGroupGUID));
            }

            private void CountFacility(CatalogueItem item, ulong group)
            {
                if (group == BedGroup)
                {
                    Beds++;
                }
                else if (group == ToiletGroup)
                {
                    Toilets++;
                }
                else if (group == ShowerGroup)
                {
                    Showers++;
                }
                else if (group == FridgeGroup)
                {
                    Fridges++;
                }
                else if (group == CounterGroup)
                {
                    Counters++;
                }
                else if (group == MicrowaveGroup)
                {
                    Microwaves++;
                }

                if (HasTag(item, CookableMicrowaveTag) && group != MicrowaveGroup)
                {
                    Microwaves++;
                }
                if (HasTag(item, CookableOvenTag))
                {
                    Ovens++;
                }
                if (HasTag(item, CookableStovetopTag))
                {
                    Stovetops++;
                }
            }

            private static ulong ResolveCatalogueGroup(CatalogueItem item, ulong spawnerGroupGuid)
            {
                if (item == null)
                {
                    return 0UL;
                }

                Interactions interactions = Settings.Get<Interactions>();
                if (spawnerGroupGuid != 0UL)
                {
                    ItemSpawnerGroup spawner = Settings.Get<ItemSpawnerGroups>()
                        .GetSpawnerGroupByGUID(spawnerGroupGuid);
                    if (spawner != null && spawner.InteractionGroup != 0UL
                        && interactions.GetInteractionGroupByGUID(spawner.InteractionGroup) != null)
                    {
                        return spawner.InteractionGroup;
                    }
                }

                if (item.OverrideInteractionGroup
                    && interactions.GetInteractionGroupByGUID(item.Interaction) != null)
                {
                    return item.Interaction;
                }

                BuildModeCatalogTags tags = Settings.Get<BuildModeCatalogTags>();
                if (item.Tag == null)
                {
                    return 0UL;
                }

                foreach (UlongAndGuid itemTag in item.Tag)
                {
                    BuildModeCatalogTag tag = tags.GetTagByGUID(itemTag.Value);
                    if (tag != null && tag.InteractionGroup != 0UL
                        && interactions.GetInteractionGroupByGUID(tag.InteractionGroup) != null)
                    {
                        return tag.InteractionGroup;
                    }

                    foreach (ulong parentGuid in tags.GetParentTags(itemTag.Value))
                    {
                        BuildModeCatalogTag parent = tags.GetTagByGUID(parentGuid);
                        if (parent != null && parent.InteractionGroup != 0UL
                            && interactions.GetInteractionGroupByGUID(parent.InteractionGroup) != null)
                        {
                            return parent.InteractionGroup;
                        }
                    }
                }

                return 0UL;
            }

            internal static bool HasTag(CatalogueItem item, ulong tagGuid)
            {
                if (item?.Tag == null)
                {
                    return false;
                }

                foreach (UlongAndGuid tag in item.Tag)
                {
                    if (tag != null && tag.Value == tagGuid)
                    {
                        return true;
                    }
                }

                return false;
            }

            private static void AddPresence(
                List<string> present, List<string> missing, int count, string key)
            {
                if (count > 0)
                {
                    present.Add(Loc.Get(key));
                }
                else
                {
                    missing.Add(Loc.Get(key));
                }
            }

            private static void AddIfPresent(List<string> target, int count, string key)
            {
                if (count > 0)
                {
                    target.Add(Loc.Get(key));
                }
            }
        }
    }
}
