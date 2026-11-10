using System;
using System.Collections.Generic;
using System.Linq;
using Setting;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>A native slot identified by its owning instance as well as its slot GUID.</summary>
    internal sealed class ItemSlotTarget
    {
        internal readonly ItemObjectRoot Owner;
        internal readonly int OwnerId;
        internal readonly ulong Guid;
        internal readonly ItemObjectPlacementSlot Surface;
        internal readonly ItemObjectChairSlot Chair;
        internal bool IsChair => Chair != null;
        internal Transform Transform => IsChair ? Chair.transform : Surface.transform;
        internal int Occupant => IsChair ? Chair.ItemInSlotInstanceID : Surface.ItemOnInstanceID;

        internal ItemSlotTarget(ItemObjectPlacementSlot slot)
        { Surface = slot; Owner = slot.Item.Root; OwnerId = Owner.InstanceID; Guid = slot.GUID; }

        internal ItemSlotTarget(ItemObjectChairSlot slot)
        { Chair = slot; Owner = slot.Item.Root; OwnerId = Owner.InstanceID; Guid = slot.GUID; }

        internal bool IsLive(ItemObjectRoot item, ulong lotGuid, int layer)
        {
            var lot = LotManager.Instance?.GetLotByGUID(lotGuid);
            if (item == null || Owner == null || Owner == item || Owner.InstanceID != OwnerId || Guid == 0
                || ExistingItemEditHandler.MovesWith(item, Owner)
                || !Owner.isActiveAndEnabled || Owner.LotPlacedOnGUID != lotGuid || Owner.Layer != layer
                || lot?.LotItems == null || !lot.LotItems.Contains(Owner)) return false;
            var tags = Settings.Get<Items>().GetItemByGUID(item.CatalogueGUID)?.Tag;
            if (tags == null) return false;
            if (IsChair)
                return Chair.GUID == Guid && Chair.isActiveAndEnabled && Chair.Item?.Root == Owner
                    && lot.ItemChairSlots.Contains(Chair) && Owner.ItemChairSlots.TryGetValue(Guid, out var chair) && chair == Chair
                    && Chair.ChairTags != null && Settings.Get<BuildModeCatalogTags>().CompareTagsToTags(Chair.ChairTags.ToArray(), tags)
                    && Finite(Transform.position) && Finite(Transform.eulerAngles);
            return Surface != null && Surface.GUID == Guid && Surface.isActiveAndEnabled && Surface.Item?.Root == Owner
                && !Surface.PlacementSlotForAnimation && lot.ItemPlacementSlots.Contains(Surface)
                && Owner.ItemPlacementSlots.TryGetValue(Guid, out var surface) && surface == Surface
                && Surface.CompareValidTags(tags) && Finite(Transform.position) && Finite(Transform.eulerAngles);
        }

        internal bool AvailableFor(ItemObjectRoot item) => Occupant == -1 || Occupant == item.InstanceID;

        internal bool MatchesPreview(ItemObjectInPlacement preview)
        {
            var item = preview.Item;
            if (Occupant != item.InstanceID) return false;
            if (IsChair)
                return item.ChairSlotPlacedOn == Guid && item.PlacementSlotPlacedOn == 0
                    && item.ItemAttachedTo == Owner && Owner.ItemsAttachedToThis.Contains(item)
                    && preview.LastColliderType == ColliderType.None && preview.LastCollidedObject == null;
            // EndMoveItem takes the final supporting object from this native ray observation.
            return item.PlacementSlotPlacedOn == Guid && item.ChairSlotPlacedOn == 0 && item.ItemAttachedTo == null
                && preview.LastColliderType == ColliderType.Object && preview.LastCollidedObject == Owner.gameObject;
        }

        internal bool MatchesPlaced(ItemObjectRoot item) => Occupant == item.InstanceID
            && (IsChair
                ? item.ChairSlotPlacedOn == Guid && item.PlacementSlotPlacedOn == 0
                    && item.ItemAttachedTo == Owner && Owner.ItemsAttachedToThis.Contains(item)
                : item.PlacementSlotPlacedOn == Guid && item.ChairSlotPlacedOn == 0
                    && item.ItemPlacedOn == Owner && Owner.ItemsOnThis.Contains(item));

        internal string Name => ItemDescription.GetProductIdentity(Owner) ?? Loc.Get("item_unknown");

        internal string Describe(ItemObjectRoot item, ulong lot, int layer)
            => Describe(item, lot, layer, false);

        internal string Describe(ItemObjectRoot item, ulong lot, int layer, bool detailed)
        {
            if (!IsLive(item, lot, layer)) return Loc.Get("slot_changed");
            string address = PlacementGridReader.TryRead(lot, ItemPlacementHandler.GridSpacingMetres, out var grid)
                ? PlacementGridReader.Position(grid, Transform.position) : Loc.Get("placement_grid_unavailable");
            string location = Loc.Get("target_location", PlacementLocationReader.Brief(lot, layer, Transform.position), layer);
            string report = Loc.Get(IsChair ? "slot_chair" : "slot_surface", detailed ? Name : ItemDescription.GetBriefIdentity(Owner),
                location, address + ". " + (AvailableFor(item) ? Loc.Get("slot_available") : Loc.Get("slot_occupied")));
            return !detailed ? report : report + ". " + Loc.Get("slot_owner_details", Name, PlacementLocationReader.Target(Owner, item))
                + ". " + DescribeLinks(lot);
        }

        internal string DescribeLinks(ulong lotGuid)
        {
            var lot = LotManager.Instance?.GetLotByGUID(lotGuid);
            if (lot == null) return Loc.Get("slot_link_unverified");
            if (!IsChair)
            {
                var linked = Surface.ChairSlotLinked;
                if (linked == null) return Loc.Get("slot_no_chair_link");
                if (!linked.isActiveAndEnabled || linked.Item?.Root != Owner || !lot.ItemChairSlots.Contains(linked)
                    || !Owner.ItemChairSlots.TryGetValue(linked.GUID, out var registered) || registered != linked)
                    return Loc.Get("slot_link_unverified");
                if (linked.ItemInSlotInstanceID == -1) return Loc.Get("slot_link_empty");
                var occupant = lot.LotItems.FirstOrDefault(i => i != null && i.InstanceID == linked.ItemInSlotInstanceID);
                return occupant != null && occupant.isActiveAndEnabled && occupant.LotPlacedOnGUID == lotGuid
                    && occupant.Layer == Owner.Layer && new ItemSlotTarget(linked).MatchesPlaced(occupant)
                    ? Loc.Get("slot_link_chair", ItemDescription.GetProductIdentity(occupant)) : Loc.Get("slot_link_unverified");
            }
            var names = new List<string>();
            bool linkedSurface = false, unverified = false;
            foreach (var surface in Owner.ItemPlacementSlots.Values)
            {
                if (surface == null || surface.ChairSlotLinked != Chair) continue;
                linkedSurface = true;
                if (!surface.isActiveAndEnabled || surface.Item?.Root != Owner || !lot.ItemPlacementSlots.Contains(surface)
                    || !Owner.ItemPlacementSlots.TryGetValue(surface.GUID, out var registered) || registered != surface)
                { unverified = true; continue; }
                if (surface.ItemOnInstanceID == -1) continue;
                var occupant = lot.LotItems.FirstOrDefault(i => i != null && i.InstanceID == surface.ItemOnInstanceID);
                if (occupant != null && occupant.isActiveAndEnabled && occupant.LotPlacedOnGUID == lotGuid
                    && occupant.Layer == Owner.Layer && new ItemSlotTarget(surface).MatchesPlaced(occupant))
                    names.Add(ItemDescription.GetProductIdentity(occupant) ?? Loc.Get("item_unknown"));
                else unverified = true;
            }
            string result = names.Count > 0 ? Loc.Get("slot_link_items", string.Join(", ", names))
                : Loc.Get(linkedSurface ? "slot_link_surfaces_empty" : "slot_no_surface_link");
            return unverified ? result + ". " + Loc.Get("slot_link_unverified") : result;
        }

        internal static bool Finite(Vector3 point) => PlacementGrid.Finite(point.x)
            && PlacementGrid.Finite(point.y) && PlacementGrid.Finite(point.z);
    }

    /// <summary>Enumerates authored placement targets using the game's tag compatibility rules.</summary>
    internal static class ItemSlotReader
    {
        internal static List<ItemSlotTarget> Read(ItemObjectRoot item, ulong lotGuid, int layer)
        {
            var result = new List<ItemSlotTarget>();
            var lot = LotManager.Instance?.GetLotByGUID(lotGuid);
            if (item == null || item.IsWallItem || item.IsCeilingItem || item.IsLotMovementItem || lot == null) return result;
            foreach (var slot in lot.ItemPlacementSlots)
                if (slot != null && slot.Item?.Root != null)
                {
                    var target = new ItemSlotTarget(slot);
                    if (target.IsLive(item, lotGuid, layer) && !result.Any(t => t.Surface == slot)) result.Add(target);
                }
            foreach (var slot in lot.ItemChairSlots)
                if (slot != null && slot.Item?.Root != null)
                {
                    var target = new ItemSlotTarget(slot);
                    if (target.IsLive(item, lotGuid, layer) && !result.Any(t => t.Chair == slot)) result.Add(target);
                }
            return result.OrderBy(t => t.OwnerId).ThenBy(t => t.IsChair).ThenBy(t => t.Guid).ToList();
        }
    }
}
