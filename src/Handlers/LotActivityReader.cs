using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ParalivesAccess.Speech;
using Setting;

namespace ParalivesAccess.Handlers
{
    /// <summary>Reads native lot hours and the exterior building's menu without opening it.</summary>
    internal static class LotActivityReader
    {
        private sealed class Offering
        {
            internal bool Enabled;
            internal readonly SortedSet<string> Reasons = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        }

        internal static string Describe(AssetLot lot)
        {
            if (lot?.Data == null) return null;
            try
            {
                var parts = new List<string>();
                LotType lotType = Settings.Get<LotTypes>()?.GetLotTypeByGUID(lot.Data.LotTypeGUID);
                if (lotType != null && (lotType.OpeningHoursStart != 0f || lotType.OpeningHoursEnd != 1440f))
                {
                    bool hour24 = Settings.Get<GeneralOptions>().Hour24Mode;
                    parts.Add(Loc.Get("lot_activity_hours",
                        FormatService.GetTimeOfDay(lotType.OpeningHoursStart, hour24),
                        FormatService.GetTimeOfDay(lotType.OpeningHoursEnd, hour24),
                        Loc.Get(lotType.IsOpen() ? "lot_activity_open" : "lot_activity_closed")));
                }

                Player player = PlayerManager.Instance?.HybridPlayer1?.Player;
                if (player == null || player.State != GameStates.LiveMode || player.IsInIntro
                    || player.SelectedCharactersGUID == null || player.SelectedCharactersGUID.Count == 0
                    || InteractionManager.Instance == null || CharacterManager.Instance == null
                    || ItemManager.Instance == null)
                    return Join(parts);

                var offerings = new SortedDictionary<string, SortedDictionary<string, Offering>>(StringComparer.CurrentCultureIgnoreCase);
                Interactions settings = Settings.Get<Interactions>();
                if (settings == null) return Join(parts);
                ulong[] selected = player.SelectedCharactersGUID.ToArray();
                if (lot.ImpostorObject != null)
                {
                    ItemObjectRoot root = lot.ImpostorObject.GetComponent<ItemObjectRoot>();
                    if (root != null && root.IsImpostorForLot == lot.GUID)
                        ReadItem(root, 0UL, player, selected, settings, offerings);
                }
                // Loaded interior objects are not a native lot-preview surface. Their
                // individual menus remain available through current-lot exploration.

                var groups = new List<string>();
                foreach (var group in offerings)
                {
                    var actions = new List<string>();
                    foreach (var action in group.Value)
                        actions.Add(action.Value.Enabled ? action.Key : Loc.Get("lot_activity_blocked", action.Key,
                            action.Value.Reasons.Count == 0 ? Loc.Get("lot_activity_unavailable")
                                : string.Join(Loc.Get("list_separator"), action.Value.Reasons)));
                    groups.Add(Loc.Get("lot_activity_group", group.Key, string.Join(Loc.Get("list_separator"), actions)));
                }
                if (groups.Count > 0) parts.Add(Loc.Get("lot_activity_offerings", Join(groups)));
                return Join(parts);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Reading native lot activity information threw: " + ex);
                return Loc.Get("lot_activity_error");
            }
        }

        private static void ReadItem(ItemObjectRoot root, ulong expectedLot, Player player, ulong[] selected,
            Interactions settings, SortedDictionary<string, SortedDictionary<string, Offering>> offerings)
        {
            if (root == null || !WorldTargeting.IsSameLiveItem(root, root.InstanceID, expectedLot)
                || root == player.ItemInPlacement?.Item
                || !InteractionManager.Instance.ItemHasInteractions(root, player.PlayerIndex)) return;
            InteractionGroup group = InteractionManager.Instance.GetItemInteractionGroup(root);
            string heading = ReadTranslation(group?.TranslationKey);
            if (group?.DisplayNameIsSkinnable == true && root.CurrentSkinGUID != 0UL)
            {
                string skinKey = Settings.Get<Skins>()?.GetSkinByGUID(root.CurrentSkinGUID)
                    ?.GetProperty(group.SkinTagGUID)?.StringValue;
                heading = ReadTranslation(skinKey) ?? heading;
            }
            if (group?.DisplayNameIsImpostorLotType == true)
            {
                AssetLot target = LotManager.Instance?.GetLotByGUID(root.IsImpostorForLot);
                heading = ReadTranslation(Settings.Get<LotTypes>()?.GetLotTypeByGUID(target?.Data?.LotTypeGUID ?? 0UL)?.TranslationKey);
            }
            heading = heading ?? Loc.Get("lot_activity_objects");
            ReadGroup(group, root, root.LotPlacedOnGUID, heading, player, selected, settings, offerings, new HashSet<ulong>());

            // Include global rows only from this exact exterior building's native menu.
            if (settings.GlobalInteractions != null)
                foreach (UlongAndGuid global in settings.GlobalInteractions)
                    if (global != null)
                        ReadInteraction(new InteractionGroupItem { Type = InteractionItemType.Interaction, Interaction = global.Value },
                            root, root.LotPlacedOnGUID, Loc.Get("lot_activity_objects"), player, selected, settings, offerings);
        }

        private static void ReadGroup(InteractionGroup group, ItemObjectRoot root, ulong lotGuid, string heading,
            Player player, ulong[] selected, Interactions settings,
            SortedDictionary<string, SortedDictionary<string, Offering>> offerings, HashSet<ulong> path)
        {
            if (group?.ChildrenInteractionAndGroups == null || !path.Add(group.GUID)) return;
            foreach (InteractionGroupItem child in group.ChildrenInteractionAndGroups)
            {
                if (child == null) continue;
                if (child.Type == InteractionItemType.Interaction)
                    ReadInteraction(child, root, lotGuid, heading, player, selected, settings, offerings);
                else if (child.Type == InteractionItemType.Group)
                {
                    InteractionGroup nested = settings.GetInteractionGroupByGUID(child.Group);
                    if (nested == null || path.Contains(nested.GUID)
                        || !InteractionManager.Instance.CanShowInteractionGroupInInteractionList(nested, root,
                            player.PlayerIndex, selected[0], selected.Length > 1 ? selected[1] : 0UL, lotGuid)) continue;
                    string name = ReadTranslation(nested.TranslationKey);
                    ReadGroup(nested, root, 0UL, name == null ? heading : Loc.Get("lot_activity_subgroup", heading, name),
                        player, selected, settings, offerings, path);
                }
            }
            path.Remove(group.GUID);
        }

        private static void ReadInteraction(InteractionGroupItem child, ItemObjectRoot root, ulong lotGuid, string heading,
            Player player, ulong[] selected, Interactions settings,
            SortedDictionary<string, SortedDictionary<string, Offering>> offerings)
        {
            InteractionUnit interaction = settings.GetInteractionByGUID(child.Interaction);
            if (interaction == null || !CharacterManager.Instance.AnyCharacterHasCharacterRequirement(selected, interaction.CharacterRequirement)) return;
            List<InteractionUsabilityRule> failures = InteractionManager.Instance.GetFaillingInteractionUsabilityRule(
                interaction, root, -1, player.PlayerIndex, selected[0], selected.Length > 1 ? selected[1] : 0UL, lotGuid);
            foreach (InteractionUsabilityRule failure in failures)
                if (failure.DisplayRule == HowToDisplayIfNotMet.Hide) return;
            string name = ReadTranslation(child.IsNestedNameDifferentThanInteractionName ? child.NestedNameTranslationKey : interaction.TranslationKey);
            if (name == null) return;
            SortedDictionary<string, Offering> group;
            if (!offerings.TryGetValue(heading, out group))
                offerings[heading] = group = new SortedDictionary<string, Offering>(StringComparer.CurrentCultureIgnoreCase);
            Offering offering;
            if (!group.TryGetValue(name, out offering)) group[name] = offering = new Offering();
            offering.Enabled |= failures.Count == 0;
            foreach (InteractionUsabilityRule failure in failures)
            {
                string reason = ReadTranslation("InteractionUsabilityRuleTooltip_" + failure.GreyedOutTooltip);
                if (reason != null) offering.Reasons.Add(reason);
            }
        }

        private static string ReadTranslation(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            string text = ScreenReader.NormalizeForOutput(TranslationManager.Get(key));
            return string.IsNullOrWhiteSpace(text) || text == key || Regex.IsMatch(text, @"\{\d+(?:[,:][^}]*)?\}")
                ? null : text;
        }

        private static string Join(List<string> parts) => string.Join(Loc.Get("lot_detail_separator"), parts);
    }
}
