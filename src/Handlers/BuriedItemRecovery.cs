using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>Explicit native inventory/sale transactions for saved objects below real support.</summary>
    internal static class BuriedItemRecovery
    {
        internal static List<ItemObjectRoot> Read(ulong lot)
        {
            var p = PlayerManager.Instance?.HybridPlayer1?.Player;
            var saved = LotManager.Instance?.GetLotByGUID(lot)?.Data?.Items;
            if (p == null || saved == null || !HouseholdManager.Instance.OwnsLot(lot)
                || !BuildModeRefreshManager.IsZoneStateRefreshed) return new List<ItemObjectRoot>();
            return ItemManager.Instance.GetLotItems(lot).Where(i => i != null && i.CubeTransform != null
                && i.LotPlacedOnGUID == lot && i.IsImpostorForLot == 0 && !i.IsLotLayerItem
                && i != p.ItemInPlacement?.Item && i != p.ItemStartingPlacement
                && ItemManager.Instance.GetItemByInstanceID(i.InstanceID) == i
                && saved.Any(s => s.InstanceID == i.InstanceID)
                && !HasAppliedDeletion(p, i)
                && PlacementSupportConsent.FloorProblem(new ItemObjectInPlacement { Item = i }) != null)
                .Select(LinkedGroupRecovery.Top).Where(i => i != null && !HasAppliedDeletion(p, i)
                    && PlacementSupportConsent.FloorProblem(new ItemObjectInPlacement { Item = i }) != null).Distinct()
                .OrderBy(i => i.Layer).ThenBy(i => ItemDescription.GetProductIdentity(i)).ThenBy(i => i.InstanceID).ToList();
        }

        private static bool HasAppliedDeletion(Player player, ItemObjectRoot item)
            => player.UndoRedoStack.OfType<DeleteItemAction>().Any(a => a.Item == item
                && (player.UndoRedo.ActionCursor <= 0 || a.ActionID < player.UndoRedo.ActionCursor));

        internal static string ContextProblem(HybridPlayer hybrid, ItemObjectRoot item)
        {
            var p = hybrid?.Player;
            if (p == null || p.State != GameStates.BuildMode || !p.PlayerActionsAreAllowed || p.PlayerActionBlockedByUIWindow
                || hybrid.HasInputFieldFocused || hybrid.IsUsingGamePad || p.ItemInPlacement != null
                || p.ItemStartingPlacement != null || !p.CanUndoRedo() || !Read(item?.LotPlacedOnGUID ?? 0).Contains(item))
                return Loc.Get("recovery_changed");
            return null;
        }
        internal static string Problem(HybridPlayer hybrid, ItemObjectRoot item)
        {
            string context = ContextProblem(hybrid, item);
            if (context != null) return context;
            // Native deletion recursively handles surface children, but inventory serialization is
            // a single root. Refuse linked groups rather than silently discard their furnishings.
            if (item.CharacterHoldingGUID != 0 || item.ItemPlacedOn != null || item.ItemAttachedTo != null
                || item.PlacementSlotPlacedOn != 0 || item.ChairSlotPlacedOn != 0
                || item.ItemsOnThis.Any(i => i != null) || item.ItemsAttachedToThis.Any(i => i != null)
                || item.ItemRopeGUID != 0)
                return Loc.Get("recovery_linked");
            return null;
        }

        internal static string Describe(ItemObjectRoot item) => Loc.Get("recovery_item",
            ItemDescription.GetProductIdentity(item) ?? Loc.Get("item_unknown"), item.Layer,
            PlacementGridReader.Length(item.CubeTransform.LocalPosition.y
                - WallHeightManager.Instance.GetFloorWorldY(item.LotPlacedOnGUID, item.Layer)))
            + ". " + PlacementLocationReader.Target(item);

        internal static string Submit(HybridPlayer hybrid, ItemObjectRoot item, bool sell)
        {
            string problem = Problem(hybrid, item);
            if (problem != null) return problem;
            var p = hybrid.Player; int action = UndoRedoManager.Instance.GenerateID(p.PlayerIndex);
            if (!sell)
            {
                var data = ItemManager.Instance.RootToAsset(item);
                data.Offset = Vector3.zero;
                InventoryManager.Instance.AddToInventory(data);
                UndoRedoManager.Instance.AddAction(new AddToInventoryAction { Item = data, ActionID = action }, p.PlayerIndex);
            }
            SystemManager.Instance.RegisterMessage(new MessageDeleteItem {
                PlayerIndex = p.PlayerIndex, ItemToDelete = item, GiveMoney = sell,
                UndoRedoActionId = action, PlayAudio = false, WriteMemory = sell
            });
            Plugin.Log.LogInfo($"[Recovery] requested item={item.InstanceID}, lot={item.LotPlacedOnGUID}, sell={sell}, action={action}.");
            return Loc.Get(sell ? "recovery_selling" : "recovery_inventory_requested");
        }

        internal static bool BeginInventoryPlacement(HybridPlayer hybrid, AssetLotDataItem data)
        {
            var p = hybrid?.Player;
            if (p == null || p.State != GameStates.BuildMode || !p.PlayerActionsAreAllowed
                || p.PlayerActionBlockedByUIWindow || hybrid.HasInputFieldFocused || hybrid.IsUsingGamePad
                || p.ItemInPlacement != null || p.ItemStartingPlacement != null || data == null || data.InstanceID < 0
                || !HouseholdManager.Instance.CurrentHousehold.Data.Inventory.Contains(data)) return false;
            // The saved-item branch of UIInventory.OnClickPlace, preserving all native data.
            data.InstanceID = InstanceIDManager.Get();
            ItemManager.Instance.AssetToRoot(data, 0uL, fromLoad: false);
            p.ItemInPlacement.InventoryItem = data;
            return true;
        }
    }

    public static partial class LotObjectHandler
    {
        private static void OpenBuriedItems()
        {
            var items = BuriedItemRecovery.Read(_lotGuid);
            if (items.Count == 0) { Speak(Loc.Get("recovery_empty")); return; }
            var panel = NewPanel(Loc.Get("recovery_panel"));
            foreach (var root in items)
            {
                var item = root;
                panel.Add(new CommandEntry(() => BuriedItemRecovery.Read(_lotGuid).Contains(item),
                    () => BuriedItemRecovery.Describe(item), () => OpenRecoveryChoice(item)));
            }
            SetPanel(panel, null, true);
        }

        private static void OpenSavedInventory()
        {
            var inventory = HouseholdManager.Instance.CurrentHousehold.Data.Inventory;
            var items = inventory.Where(i => i != null && i.InstanceID >= 0).ToList();
            if (items.Count == 0) { Speak(Loc.Get("recovery_inventory_empty")); return; }
            var panel = NewPanel(Loc.Get("recovery_inventory_panel"));
            foreach (var value in items)
            {
                var data = value;
                panel.Add(new CommandEntry(() => inventory.Contains(data), () =>
                    ItemDescription.GetProductIdentity(Settings.Get<global::Setting.Items>().GetItemByGUID(data.CatalogueGUID), data.SkinGUID)
                        ?? Loc.Get("item_unknown"), () => {
                    var hybrid = PlayerManager.Instance.HybridPlayer1;
                    Close(false);
                    if (!BuriedItemRecovery.BeginInventoryPlacement(hybrid, data)) Speak(Loc.Get("recovery_changed"));
                }));
            }
            SetPanel(panel, null, true);
        }

        private static void OpenRecoveryChoice(ItemObjectRoot item)
        {
            bool group = LinkedGroupRecovery.Read(item).Count > 1;
            string problem = group ? LinkedGroupRecovery.Problem(PlayerManager.Instance.HybridPlayer1, item)
                : BuriedItemRecovery.Problem(PlayerManager.Instance.HybridPlayer1, item);
            if (problem != null) { Speak(problem); return; }
            var panel = NewPanel(Loc.Get("recovery_review", BuriedItemRecovery.Describe(item)));
            if (group)
            {
                panel.Add(new CommandEntry(() => LinkedGroupRecovery.Problem(PlayerManager.Instance.HybridPlayer1, item) == null,
                    () => Loc.Get("group_recovery_command", LinkedGroupRecovery.Read(item).Count), () => {
                        var h = PlayerManager.Instance.HybridPlayer1; Close(false); Speak(LinkedGroupRecovery.Begin(h, item));
                    }));
                SetPanel(panel, null, true); return;
            }
            foreach (bool sell in new[] { false, true })
            {
                bool sale = sell;
                panel.Add(new CommandEntry(() => BuriedItemRecovery.Problem(PlayerManager.Instance.HybridPlayer1, item) == null,
                    () => Loc.Get(sale ? "recovery_sell" : "recovery_to_inventory"), () => {
                        var hybrid = PlayerManager.Instance.HybridPlayer1;
                        string result = BuriedItemRecovery.Submit(hybrid, item, sale);
                        Close(false); Speak(result);
                    }));
            }
            SetPanel(panel, null, true);
        }
    }
}
