using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    internal static class LinkedGroupRecovery
    {
        internal static bool Enabled;
        private static Player _player;
        private static ItemObjectRoot _root;
        private static List<ItemObjectRoot> _members;
        private static bool[] _active;
        private static Vector3 _point;
        private static ItemObjectInPlacement _preview;
        private static GroupState _before, _restore;
        private static int _restoreFrame;
        private static GroupVisibilityAction _capture;
        private static int _captureFrame;
        private static int _captureSettled;

        internal static bool ApplyPatches(Harmony harmony)
        {
            try
            {
                var method = AccessTools.Method(typeof(StartMoveItem), "UpdateForPlayer", new[] { typeof(Player) });
                var after = AccessTools.Method(typeof(LinkedGroupRecovery), nameof(AfterStart));
                harmony.Patch(method, postfix: new HarmonyMethod(after));
                bool started = Harmony.GetPatchInfo(method)?.Postfixes.Any(p => p.owner == Plugin.PluginGuid && p.PatchMethod == after) == true;
                var update = AccessTools.Method(typeof(UpdateItemAttachedToItem), "Update", Type.EmptyTypes);
                var preserve = AccessTools.Method(typeof(LinkedGroupRecovery), nameof(PreserveBuriedLinks));
                harmony.Patch(update, prefix: new HarmonyMethod(preserve));
                var surfaces = AccessTools.Method(typeof(UpdateItemOnItem), "Update", Type.EmptyTypes);
                harmony.Patch(surfaces, prefix: new HarmonyMethod(preserve));
                var floor = AccessTools.Method(typeof(UpdateItemOnFloor), "CheckIfItemIsStillOnFloor", new[] { typeof(ItemObjectRoot) });
                var check = AccessTools.Method(typeof(LinkedGroupRecovery), nameof(AllowFloorCleanup));
                harmony.Patch(floor, prefix: new HarmonyMethod(check));
                return started && Harmony.GetPatchInfo(update)?.Prefixes.Any(p => p.owner == Plugin.PluginGuid && p.PatchMethod == preserve) == true
                    && Harmony.GetPatchInfo(surfaces)?.Prefixes.Any(p => p.owner == Plugin.PluginGuid && p.PatchMethod == preserve) == true
                    && Harmony.GetPatchInfo(floor)?.Prefixes.Any(p => p.owner == Plugin.PluginGuid && p.PatchMethod == check) == true;
            }
            catch (Exception ex) { Plugin.Log.LogError("Group recovery hook failed: " + ex); return false; }
        }

        // Native attachment updates delete a chair when its parent is hidden below the floor.
        // Keep only a fully reciprocal, saved owned-lot link pending until its parent is moved.
        // No pose or link is synthesized; normal native updates resume once the parent is active.
        private static void PreserveBuriedLinks()
        {
            var manager = ItemManager.Instance;
            if (manager == null || HouseholdManager.Instance == null || LotManager.Instance == null) return;
            manager.DirtyItemOnItem.RemoveAll(item => {
                var owner = item?.ItemPlacedOn;
                if (owner == null || owner.gameObject.activeInHierarchy || !owner.ItemsOnThis.Contains(item) || !SavedBuried(owner)) return false;
                var saved = LotManager.Instance.GetLotByGUID(owner.LotPlacedOnGUID)?.Data?.Items;
                return item.LotPlacedOnGUID == owner.LotPlacedOnGUID && saved != null
                    && saved.Any(s => s.InstanceID == item.InstanceID && s.PlacedOn.Type == ColliderType.Object && s.PlacedOn.InstanceID == owner.InstanceID);
            });
            manager.DirtyItemAttachedToItem.RemoveAll(item => {
                var owner = item?.ItemAttachedTo;
                if (owner == null || owner.gameObject.activeInHierarchy || owner.LotPlacedOnGUID != item.LotPlacedOnGUID
                    || !HouseholdManager.Instance.OwnsLot(owner.LotPlacedOnGUID) || item.ChairSlotPlacedOn == 0
                    || !owner.ItemsAttachedToThis.Contains(item) || !owner.ItemChairSlots.TryGetValue(item.ChairSlotPlacedOn, out var slot)
                    || slot.ItemInSlotInstanceID != item.InstanceID) return false;
                var saved = LotManager.Instance.GetLotByGUID(owner.LotPlacedOnGUID)?.Data?.Items;
                return saved != null && saved.Any(s => s.InstanceID == owner.InstanceID)
                    && saved.Any(s => s.InstanceID == item.InstanceID && s.ChairSlotPlacedOnGUID == item.ChairSlotPlacedOn)
                    && PlacementSupportConsent.FloorProblem(new ItemObjectInPlacement { Item = owner }) != null;
            });
        }
        private static bool SavedBuried(ItemObjectRoot item) => item != null && HouseholdManager.Instance != null
            && LotManager.Instance != null && HouseholdManager.Instance.OwnsLot(item.LotPlacedOnGUID)
            && LotManager.Instance.GetLotByGUID(item.LotPlacedOnGUID)?.Data?.Items?.Any(s => s.InstanceID == item.InstanceID) == true
            && PlacementSupportConsent.FloorProblem(new ItemObjectInPlacement { Item = item }) != null;
        private static bool AllowFloorCleanup(ItemObjectRoot item) => !SavedBuried(item);

        internal static ItemObjectRoot Top(ItemObjectRoot item)
        {
            var seen = new HashSet<ItemObjectRoot>();
            while (item != null && seen.Add(item))
            {
                var parent = Owner(item);
                if (parent == null) return item;
                if (parent.LotPlacedOnGUID != item.LotPlacedOnGUID) return null;
                item = parent;
            }
            return null;
        }

        private static ItemObjectRoot Owner(ItemObjectRoot item)
        {
            var owner = item.ItemPlacedOn ?? item.ItemAttachedTo;
            if (owner != null || item.ChairSlotPlacedOn == 0) return owner;
            var candidates = ItemManager.Instance.GetLotItems(item.LotPlacedOnGUID).Where(i => i != null && i != item
                && i.ItemsAttachedToThis.Contains(item) && i.ItemChairSlots.TryGetValue(item.ChairSlotPlacedOn, out var slot)
                && slot.ItemInSlotInstanceID == item.InstanceID).Take(2).ToArray();
            if (candidates.Length != 1) return null;
            var saved = LotManager.Instance.GetLotByGUID(item.LotPlacedOnGUID)?.Data?.Items;
            return saved != null && saved.Any(s => s.InstanceID == item.InstanceID && s.ChairSlotPlacedOnGUID == item.ChairSlotPlacedOn)
                && saved.Any(s => s.InstanceID == candidates[0].InstanceID && s.ItemChairSlots.Any(slot => slot.GUID == item.ChairSlotPlacedOn && slot.InstanceID == item.InstanceID)) ? candidates[0] : null;
        }

        internal static List<ItemObjectRoot> Read(ItemObjectRoot root)
        {
            var result = new List<ItemObjectRoot>();
            if (root == null || Top(root) != root) return result;
            var pending = new Queue<ItemObjectRoot>(); pending.Enqueue(root);
            while (pending.Count > 0 && result.Count < 64)
            {
                var item = pending.Dequeue();
                if (item == null || result.Contains(item)) continue;
                if (item.LotPlacedOnGUID != root.LotPlacedOnGUID || item.Layer != root.Layer
                    || ItemManager.Instance.GetItemByInstanceID(item.InstanceID) != item
                    || !ItemManager.Instance.GetLotItems(root.LotPlacedOnGUID).Contains(item)) return new List<ItemObjectRoot>();
                result.Add(item);
                foreach (var child in item.ItemsOnThis.Concat(item.ItemsAttachedToThis))
                {
                    if (child == null || Owner(child) != item) return new List<ItemObjectRoot>();
                    pending.Enqueue(child);
                }
            }
            return pending.Count == 0 ? result : new List<ItemObjectRoot>();
        }

        internal static string Problem(HybridPlayer h, ItemObjectRoot root)
        {
            string context = BuriedItemRecovery.ContextProblem(h, root);
            if (context != null || !Enabled) return context ?? Loc.Get("group_recovery_unavailable");
            var group = Read(root);
            if (group.Count < 2 || group.Any(i => i.CharacterHoldingGUID != 0 || i.ItemRopeGUID != 0
                || i.IsWallItem || i.IsCeilingItem || i.IsLotMovementItem || RoofConstructionHandler.IsRoof(i)))
                return Loc.Get("group_recovery_unavailable");
            foreach (var item in group.Skip(1))
            {
                var owner = Owner(item);
                if (!group.Contains(owner)) return Loc.Get("group_recovery_unavailable");
                if (item.PlacementSlotPlacedOn != 0 && (item.ItemPlacedOn == null
                    || !owner.ItemPlacementSlots.TryGetValue(item.PlacementSlotPlacedOn, out var surface)
                    || (surface.ItemOnInstanceID != item.InstanceID && (surface.ItemOnInstanceID != -1 || !SavedSurfaceLink(owner, item))))) return Loc.Get("group_recovery_unavailable");
                if (item.ChairSlotPlacedOn != 0 && (owner == null
                    || !owner.ItemChairSlots.TryGetValue(item.ChairSlotPlacedOn, out var chair)
                    || chair.ItemInSlotInstanceID != item.InstanceID)) return Loc.Get("group_recovery_unavailable");
            }
            return null;
        }

        private static bool SavedSurfaceLink(ItemObjectRoot owner, ItemObjectRoot item)
        {
            var saved = LotManager.Instance.GetLotByGUID(owner.LotPlacedOnGUID)?.Data?.Items;
            return saved != null && saved.Any(s => s.InstanceID == item.InstanceID && s.PlacementSlotPlacedOnGUID == item.PlacementSlotPlacedOn
                && s.PlacedOn.Type == ColliderType.Object && s.PlacedOn.InstanceID == owner.InstanceID)
                && saved.Any(s => s.InstanceID == owner.InstanceID && s.ItemPlacementSlots.Any(slot => slot.GUID == item.PlacementSlotPlacedOn && slot.InstanceID == item.InstanceID));
        }

        internal static string Begin(HybridPlayer h, ItemObjectRoot root)
        {
            string problem = Problem(h, root); if (problem != null) return problem;
            var rooms = RoomSpatialReader.OrderedRooms(root.LotPlacedOnGUID, root.Layer);
            var room = rooms.FirstOrDefault(r => ZoneGeometry.Contains(r, root.CubeTransform.LocalPosition));
            if (room == null || !RoomAnchorReader.TryRead(room, root.CubeTransform.LocalPosition, out var point))
                return Loc.Get("group_recovery_unavailable");
            _player = h.Player; _root = root; _members = Read(root); _active = _members.Select(i => i.gameObject.activeSelf).ToArray(); _before = new GroupState(_members); _point = point;
            if (!WorldTargeting.FrameArea(_player, root.LotPlacedOnGUID, root.Layer, point, "group-recovery")) { Clear(); return Loc.Get("explorer_frame_failed"); }
            _player.ItemStartingPlacement = root; root.MovedItemFromUISelected = true;
            return Loc.Get("group_recovery_start", _members.Count);
        }

        private static void AfterStart(Player player)
        {
            if (player != _player || _root == null || _preview != null || player.ItemInPlacement?.Item != _root) return;
            _preview = player.ItemInPlacement;
            foreach (var member in _members) member.gameObject.SetActive(true);
            foreach (var member in _members.Where(i => i.PlacementSlotPlacedOn != 0 && i.ItemPlacedOn != null))
            {
                var slot = member.ItemPlacedOn.ItemPlacementSlots[member.PlacementSlotPlacedOn];
                if (slot.ItemOnInstanceID == -1 && SavedSurfaceLink(member.ItemPlacedOn, member)) slot.SetItemToSlot(member);
            }
            var lot = LotManager.Instance.GetLotByGUID(_root.LotPlacedOnGUID);
            if (lot.AttachementItemDatas.Count != 0) { UI.Get<UIBuildModeCancelMoveItem>(player.PlayerIndex).HasClickedCancel = true; return; }
            foreach (var member in _members.Where(i => i.ItemAttachedTo == null && i.ChairSlotPlacedOn != 0))
            {
                var owner = Owner(member);
                if (owner == null) { UI.Get<UIBuildModeCancelMoveItem>(player.PlayerIndex).HasClickedCancel = true; return; }
                if (!lot.AttachementItemDatas.TryGetValue(owner, out var children)) lot.AttachementItemDatas[owner] = children = new List<int>();
                children.Add(member.InstanceID);
            }
            if (lot.AttachementItemDatas.Count > 0) LotManager.Instance.RefreshItemAttachmentData(lot);
            if (_root.ItemOffset != null) _root.ItemOffset.Offset = Vector3.zero; // OriginalOffset was captured by native StartMoveItem.
            player.HasSnap = false;
            var h = PlayerManager.Instance.HybridPlayer1;
            if (ItemPlacementHandler.TryProjectTarget(h, _point, out var screen))
            {
                UnityEngine.InputSystem.Mouse.current.WarpCursorPosition(screen);
                _preview.CursorTeleported = true; _preview.CursorTeleportPosition = new Vector3(screen.x, screen.y, 0);
            }
            else UI.Get<UIBuildModeCancelMoveItem>(player.PlayerIndex).HasClickedCancel = true;
        }

        internal static void AfterEnd(Player player, ItemObjectInPlacement preview, bool committed)
        {
            if (player != _player || preview != _preview || !committed) return;
            var action = new GroupVisibilityAction { Before = _before, After = new GroupState(_members), Members = _members.ToArray(),
                ActionID = preview.OriginalMoveItemAction.ActionID };
            UndoRedoManager.Instance.AddAction(action, player.PlayerIndex);
            _capture = action; _captureFrame = Time.frameCount + 2; _captureSettled = 0;
            Plugin.Log.LogInfo($"[GroupRecovery] committed root={_root.InstanceID}, members={_members.Count}, native action={preview.OriginalMoveItemAction.ActionID}.");
            Clear();
        }

        internal static void AfterCancel(Player player, ItemObjectInPlacement preview)
        {
            if (player != _player || preview != _preview || player.ItemInPlacement == preview) return;
            Schedule(_before);
            Clear();
        }
        private static void Schedule(GroupState state) { state.Restore(); _restore = state; _restoreFrame = Time.frameCount + 2; }
        internal static void Tick()
        {
            if (_restore != null && Time.frameCount >= _restoreFrame) { var state = _restore; _restore = null; state.Restore(); }
            if (_capture != null && Time.frameCount >= _captureFrame)
            {
                _captureSettled = ItemManager.Instance.AnimatedItems.Count == 0 ? _captureSettled + 1 : 0;
                if (_captureSettled >= 4) { _capture.After = new GroupState(_capture.Members); _capture = null; }
            }
        }
        private static void Clear() { _root = null; _player = null; _members = null; _active = null; _preview = null; }
        private sealed class GroupVisibilityAction : UndoRedoAction
        {
            internal GroupState Before, After;
            internal ItemObjectRoot[] Members;
            public override void Undo() { if (_capture == this) _capture = null; Schedule(Before); }
            public override void Redo() => Schedule(After);
        }
        // Native cancellation restores the parent before its deferred child updates run. Preserve
        // the actual captured arrangement through that ordering, including a previously hidden
        // parent's stale slot state. These are transaction snapshots, never candidate positions.
        private sealed class GroupState
        {
            private readonly List<Pose> _poses;
            internal GroupState(IEnumerable<ItemObjectRoot> items) { _poses = items.Select(i => new Pose(i)).ToList(); }
            internal void Restore()
            {
                var items = new HashSet<ItemObjectRoot>(_poses.Select(p => p.Item));
                ItemManager.Instance.DirtyItemOnItem.RemoveAll(items.Contains);
                ItemManager.Instance.DirtyItemAttachedToItem.RemoveAll(items.Contains);
                ItemManager.Instance.DirtyItemOnItemToItemWaitForFrames.RemoveAll(p => items.Contains(p.Root));
                ItemManager.Instance.DirtyItemAttachedToItemWaitForFrames.RemoveAll(p => items.Contains(p.Root));
                foreach (var pose in _poses) pose.Restore();
                ItemManager.Instance.DirtyCubeTransforms.RemoveAll(c => c != null && c.Item.IsRoot() && items.Contains(c.Item.Root));
                ItemManager.Instance.DirtyItemOnItem.RemoveAll(items.Contains);
                ItemManager.Instance.DirtyItemAttachedToItem.RemoveAll(items.Contains);
            }
            private sealed class Pose
            {
                internal readonly ItemObjectRoot Item;
                private readonly Vector3 _position, _transformPosition, _offset, _localRotation;
                private readonly Quaternion _rotation;
                private readonly bool _visible;
                private readonly ItemObjectRoot _on, _attached;
                private readonly Dictionary<ulong,int> _surface, _chairs;
                internal Pose(ItemObjectRoot item)
                {
                    Item=item;_position=item.CubeTransform.LocalPosition;_localRotation=item.CubeTransform.LocalRotation;
                    _rotation=item.transform.localRotation;_transformPosition=item.transform.localPosition;_offset=item.ItemOffset?.Offset??Vector3.zero;_visible=item.gameObject.activeSelf;
                    _on=item.ItemPlacedOn;_attached=item.ItemAttachedTo;
                    _surface=item.ItemPlacementSlots.ToDictionary(p=>p.Key,p=>p.Value.ItemOnInstanceID);
                    _chairs=item.ItemChairSlots.ToDictionary(p=>p.Key,p=>p.Value.ItemInSlotInstanceID);
                }
                internal void Restore()
                {
                    if(Item==null)return;
                    Item.CubeTransform.LocalPosition=_position;Item.CubeTransform.LocalRotation=_localRotation;
                    if(Item.ItemOffset!=null)Item.ItemOffset.Offset=_offset;
                    Item.transform.localPosition=_transformPosition;
                    Item.transform.localRotation=_rotation;Item.ItemPlacedOn=_on;Item.ItemAttachedTo=_attached;
                    foreach(var slot in _surface)if(Item.ItemPlacementSlots.TryGetValue(slot.Key,out var s))s.ItemOnInstanceID=slot.Value;
                    foreach(var slot in _chairs)if(Item.ItemChairSlots.TryGetValue(slot.Key,out var s))s.ItemInSlotInstanceID=slot.Value;
                    Item.gameObject.SetActive(_visible);
                }
            }
        }
    }
}
