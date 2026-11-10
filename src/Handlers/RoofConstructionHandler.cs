using System;
using System.Globalization;
using System.Linq;
using ParalivesAccess.Speech;
using Setting;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>Sizes native roof pieces and aims new previews at an actual room ceiling.</summary>
    internal static class RoofConstructionHandler
    {
        // Native BuildModeCatalogTags: Roofs (not chimneys, decor or surface paint).
        private const ulong RoofTag = 2066270032552373472UL;
        private static ItemObjectInPlacement _pending, _fitted;
        private static ZoneWallObject _room;
        private static ZoneWallPlatformObject _ceiling;
        private static Vector3[] _boundary;
        private static Vector3 _target, _size, _cameraPosition;
        private static Quaternion _rotation;
        private static float _started;
        private static int _stable;
        private static bool _aimed;

        internal static bool IsRoof(ItemObjectRoot root)
        {
            if (root == null) return false;
            var tags = Settings.Get<Items>()?.GetItemByGUID(root.CatalogueGUID)?.Tag;
            return tags != null && tags.Any(t => t.Value == RoofTag
                || Settings.Get<BuildModeCatalogTags>().GetParentTags(t.Value).Contains(RoofTag));
        }
        internal static bool Pending(ItemObjectInPlacement preview) => preview != null && _pending == preview;
        // Roof pieces are combined by overlapping them. EndMoveItem itself does not reject
        // object overlap; retain the furniture guard for other pairs, but allow roof joins.
        internal static bool CanOverlap(ItemObjectRoot first, ItemObjectRoot second) => IsRoof(first) && IsRoof(second);
        internal static string Problem(HybridPlayer hybrid, ItemObjectInPlacement preview)
        {
            if (preview == null || _fitted != preview) return null;
            var points = _room != null ? Boundary(_room) : null;
            var renderer = preview.LastCollidedObject != null ? preview.LastCollidedObject.GetComponent<ZoneWallPlatformRenderer>() : null;
            bool valid = Valid(hybrid, preview.Item, preview) && _room != null && _boundary != null
                && BuildModeRefreshManager.IsZoneStateRefreshed && ZoneManager.Instance.DirtyWallZones.Count == 0
                && ZoneManager.Instance.GetLotZones(_room.LotPlacedOnGUID).Contains(_room)
                && RoomSpatialReader.IsUsableRoom(_room, _room.LotPlacedOnGUID) && Ceiling(_room) == _ceiling
                && points != null && points.Count == _boundary.Length
                && !points.Where((p, i) => Vector3.Distance(p, _boundary[i]) > 0.001f).Any()
                && Math.Abs(_ceiling.transform.position.y + 0.001f - _target.y) < 0.001f
                && preview.Item.Layer == _room.Layer + 1 && hybrid.Player.GetLotLayer(_room.LotPlacedOnGUID) == _room.Layer + 1
                && Math.Abs(preview.Item.CubeTransform.LocalPosition.y - _target.y) < 0.02f
                && preview.LastColliderType == ColliderType.Floor && renderer?.WallZonePlatform == _ceiling;
            return valid ? null : Loc.Get("roof_fit_unavailable");
        }
        internal static bool AllowsNativeCommit(Player player)
        {
            if (Pending(player.ItemInPlacement)) return false;
            var hybrid = PlayerManager.Instance?.HybridPlayer1;
            if (hybrid?.Player != player || !hybrid.ButtonConfirm.Up) return true;
            string problem = Problem(hybrid, player.ItemInPlacement);
            if (problem == null) return true;
            Speak(problem); return false;
        }
        internal static void ClearFit(ItemObjectInPlacement preview) { if (_fitted == preview) Clear(); }
        internal static bool CanSize(ItemObjectRoot root) => root?.CubeTransform != null && IsRoof(root)
            && root.IsResizable && !root.IsWallItem && !root.IsCeilingItem && !root.IsLotMovementItem
            && Vector3.Dot(root.transform.up, Vector3.up) > 0.9999f;
        private static bool Valid(HybridPlayer h, ItemObjectRoot root, ItemObjectInPlacement preview) => CanSize(root)
            && h?.Player != null && GameLoadingManager.State == State.Game && h.Player.State == GameStates.BuildMode
            && h.Player.ItemInPlacement == preview && h.Player.ItemInRotation == null && h.Player.ItemInResize == null
            && !h.IsUsingGamePad && !SettingsHandler.IsCapturingBindingInput && root.isActiveAndEnabled
            && HouseholdManager.Instance?.OwnsLot(preview != null ? ItemPlacementHandler.ResolvePlacementLot(h.Player, out _) : root.LotPlacedOnGUID) == true
            && (preview != null ? preview.Item == root && preview.IsNewItem : ItemManager.Instance.GetItemByInstanceID(root.InstanceID) == root);

        internal static string Describe(ItemObjectRoot root) => !IsRoof(root) ? null
            : Loc.Get("roof_size_report", PlacementGridReader.Length(root.CubeTransform.FinalSize.x),
                PlacementGridReader.Length(root.CubeTransform.FinalSize.z), PlacementGridReader.Length(root.CubeTransform.FinalSize.y), root.Layer);

        internal static void OpenDimensions(HybridPlayer hybrid, ItemObjectRoot root, ItemObjectInPlacement preview = null)
        {
            if (!Valid(hybrid, root, preview) || (preview == null && !ExistingItemEditHandler.CanBegin(hybrid, root)))
            { Speak(Loc.Get("roof_unavailable")); return; }
            Vector3 original = root.CubeTransform.Size, scale = root.CubeTransform.LocalScale;
            var rotation = root.transform.rotation; ulong lot = root.LotPlacedOnGUID; int layer = root.Layer;
            Vector3 physical = root.CubeTransform.FinalSize;
            string initial = physical.x.ToString("0.###", CultureInfo.CurrentCulture) + "; "
                + physical.z.ToString("0.###", CultureInfo.CurrentCulture) + "; " + physical.y.ToString("0.###", CultureInfo.CurrentCulture);
            CoordinateEntryHandler.OpenText(hybrid, Loc.Get("roof_dimensions"), Loc.Get("roof_dimensions_hint"),
                Loc.Get(preview == null ? "roof_resize_instructions" : "roof_size_instructions"), initial,
                () => Valid(hybrid, root, preview) && root.CubeTransform.Size == original && root.CubeTransform.LocalScale == scale
                    && root.transform.rotation == rotation && root.LotPlacedOnGUID == lot && root.Layer == layer,
                text =>
                {
                    if (!RoofGeometry.TryParse(text, out var requested) || !TrySize(root, requested, out var size))
                    { Speak(Loc.Get("roof_invalid_dimensions")); return; }
                    if ((size - original).sqrMagnitude < 0.00000001f) { Speak(Describe(root)); return; }
                    if (preview != null)
                    {
                        SetSize(root, size); ItemPlacementHandler.ReviewAfterAction(hybrid, preview);
                        Speak(Loc.Get("roof_preview_sized", Describe(root)));
                    }
                    else ResizePlaced(hybrid, root, size);
                }, preview, "roof_dimensions_cancelled");
        }

        private static bool TrySize(ItemObjectRoot root, RoofGeometry.Dimensions requested, out Vector3 size)
        {
            size = Vector3.zero;
            var c = root.CubeTransform; var scale = c.LocalScale;
            Vector3 max = root.HasMaxSize ? root.MaxSizes : new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            if (!RoofGeometry.ValidAxis(requested.Width, scale.x, c.Size.x, root.MinSizes.x, max.x, root.ResizableAxes.x)
                || !RoofGeometry.ValidAxis(requested.Height, scale.y, c.Size.y, root.MinSizes.y, max.y, root.ResizableAxes.y)
                || !RoofGeometry.ValidAxis(requested.Depth, scale.z, c.Size.z, root.MinSizes.z, max.z, root.ResizableAxes.z)) return false;
            size = new Vector3((float)(requested.Width / scale.x), (float)(requested.Height / scale.y), (float)(requested.Depth / scale.z));
            return WallFaceFrame.Finite(size);
        }
        private static void SetSize(ItemObjectRoot root, Vector3 size)
        {
            if (Math.Abs(root.CubeTransform.Size.y - size.y) > 0.00001f) root.ManuallyResized = true;
            root.CubeTransform.Size = size;
            root.transform.localPosition = CubeTransformService.GetAnchoredLocalPosition(root.Item, root.CubeTransform);
            new ResizeItemAction { Item = root }.NotifyChanges();
        }

        private static void ResizePlaced(HybridPlayer hybrid, ItemObjectRoot root, Vector3 size)
        {
            if (!Valid(hybrid, root, null) || hybrid.ButtonConfirm.Down || hybrid.ButtonConfirm.Up || hybrid.ButtonConfirm.Pressed)
            { Speak(Loc.Get("roof_unavailable")); return; }
            var p = hybrid.Player; ulong lot = root.LotPlacedOnGUID, previousLot = root.PreviousLotPlacedOnGUID;
            bool manual = root.ManuallyResized; var placed = root.PlacedOnData;
            var resize = new ItemObjectInResize { Item = root, OriginalItemSize = root.CubeTransform.Size,
                OriginalPosition = root.CubeTransform.LocalPosition, ResizeSide = WidgetSide.Up };
            resize.ChairSlots.AddRange(root.ItemChairSlots.Values.Where(s => s != null && s.isActiveAndEnabled));
            resize.PlacementSlots.AddRange(root.ItemPlacementSlots.Values.Where(s => s != null && s.isActiveAndEnabled));
            try
            {
                p.ItemInResize = resize; p.IsDoingCancelableAction = true; root.PlacedOnData = null;
                SetSize(root, size);
                // Run the same synchronous native legality check before the native commit. It
                // reads the changed CubeTransform bounds, not a previous frame's illegal flag.
                new UpdateCheckIfItemsAreInLots().UpdateForPlayer(p);
                if (root.IsPositionIllegal || root.LotPlacedOnGUID != lot)
                { Speak(Loc.Get("roof_resize_outside")); return; }
                hybrid.ButtonConfirm.SetValue(0f);
                try { new EndResizeItem().UpdateForPlayer(p); }
                finally { hybrid.ButtonConfirm.Update(); }
                if (p.ItemInResize != null) { Speak(Loc.Get("roof_unavailable")); return; }
                Speak(Loc.Get("roof_resized", Describe(root)));
            }
            catch (Exception ex) { Plugin.Log.LogError("Native roof resize failed: " + ex); Speak(Loc.Get("roof_unavailable")); }
            finally
            {
                if (p.ItemInResize == resize)
                {
                    // Mirror the native cancel path; the native system also refreshes dirty sizes.
                    hybrid.ForceTriggerCancelEscapeBackWithRightClick = true;
                    new CancelResizeOrScaleItem().UpdateForPlayer(p);
                    root.LotPlacedOnGUID = lot; root.PreviousLotPlacedOnGUID = previousLot;
                    root.ManuallyResized = manual; root.PlacedOnData = placed;
                    new ResizeItemAction { Item = root }.NotifyChanges();
                }
            }
        }

        internal static ZoneWallPlatformObject Ceiling(ZoneWallObject room) => room?.Platforms?
            .Where(p => p != null && p.IsCeiling && !p.DisabledByUser && p.WallZone == room)
            .OrderByDescending(p => p.Height).FirstOrDefault();
        private static System.Collections.Generic.List<Vector3> Boundary(ZoneWallObject room)
        {
            return SpatialOutline.TryRead(room, new PlacementGrid(0, 0, 0, 1), out var outline)
                ? outline.Points.Select(p => new Vector3((float)p.X, 0, (float)p.Y)).ToList() : null;
        }
        internal static void Fit(HybridPlayer hybrid, ItemObjectInPlacement preview, ZoneWallObject room)
        {
            ulong lot = ItemPlacementHandler.ResolvePlacementLot(hybrid.Player, out _);
            var ceiling = Ceiling(room); var points = Boundary(room);
            if (!Valid(hybrid, preview?.Item, preview) || !RoomSpatialReader.IsUsableRoom(room, lot) || ceiling == null
                || !BuildModeRefreshManager.IsZoneStateRefreshed || ZoneManager.Instance.DirtyWallZones.Count > 0
                || points == null || !RoofGeometry.TryPlan(points.Select(p => new RoofGeometry.Point(p.x, p.z)).ToArray(),
                    preview.Item.transform.eulerAngles.y, 0.25, out var plan))
            { Speak(Loc.Get("roof_fit_unavailable")); return; }
            var target = new Vector3((float)plan.X, ceiling.transform.position.y + 0.001f, (float)plan.Z);
            if (!ZoneGeometry.Contains(room, target) || !TrySize(preview.Item, new RoofGeometry.Dimensions
                { Width = plan.Width, Depth = plan.Depth, Height = preview.Item.CubeTransform.FinalSize.y }, out var size))
            { Speak(Loc.Get("roof_fit_unavailable")); return; }
            if (!WorldTargeting.FrameArea(hybrid.Player, lot, room.Layer + 1, target, "roof-room"))
            { Speak(Loc.Get("roof_fit_unavailable")); return; }
            RoomPlacementGrid.UseLot(); RoomBoundaryConstraint.Clear(); FacingTargetHandler.Clear(); ItemAlignmentHandler.Clear(); SlotPlacementHandler.Clear();
            SetSize(preview.Item, size);
            _fitted = null; _pending = preview; _room = room; _ceiling = ceiling; _boundary = points.ToArray(); _target = target;
            _size = size; _rotation = preview.Item.transform.rotation; _started = Time.realtimeSinceStartup; _stable = 0; _aimed = false;
            _cameraPosition = hybrid.HybridCamera.Camera.transform.position;
            Speak(Loc.Get("roof_fitting"));
        }

        internal static void TickFit(HybridPlayer hybrid, ItemObjectInPlacement preview)
        {
            if (_pending == null) return;
            var points = _room != null ? Boundary(_room) : null;
            bool same = _pending == preview && Valid(hybrid, preview?.Item, preview)
                && RoomSpatialReader.IsUsableRoom(_room, ItemPlacementHandler.ResolvePlacementLot(hybrid.Player, out _))
                && Ceiling(_room) == _ceiling && points != null && points.Count == _boundary.Length
                && !points.Where((p, i) => Vector3.Distance(p, _boundary[i]) > 0.001f).Any()
                && Math.Abs(_ceiling.transform.position.y + 0.001f - _target.y) < 0.001f
                && hybrid.Player.GetLotLayer(_room.LotPlacedOnGUID) == _room.Layer + 1
                && preview.Item.CubeTransform.Size == _size && Quaternion.Angle(preview.Item.transform.rotation, _rotation) < 0.01f;
            if (!same || Time.realtimeSinceStartup - _started > 10f)
            { Clear(); ItemPlacementHandler.RejectRoofTarget(Loc.Get("roof_fit_unavailable")); return; }
            if (!_aimed)
            {
                Vector3 camera = hybrid.HybridCamera.Camera.transform.position;
                _stable = Vector3.Distance(camera, _cameraPosition) < 0.001f ? _stable + 1 : 0; _cameraPosition = camera;
                if (_stable < 3) return;
                string problem = ItemPlacementHandler.RequestAlignment(hybrid, preview, _target);
                if (problem != null) return; // The camera can still be completing its framing.
                _aimed = true; _stable = 0; return;
            }
            var renderer = preview.LastCollidedObject != null ? preview.LastCollidedObject.GetComponent<ZoneWallPlatformRenderer>() : null;
            bool atTarget = Vector3.Distance(preview.Item.CubeTransform.LocalPosition, _target) < 0.02f
                && preview.Item.Layer == _room.Layer + 1 && preview.LastColliderType == ColliderType.Floor
                && renderer?.WallZonePlatform == _ceiling && !preview.Item.IsPositionIllegal;
            _stable = atTarget ? _stable + 1 : 0;
            if (_stable < 2) return;
            string name = RoomLabelHandler.Name(_room); _fitted = preview; _pending = null;
            Speak(Loc.Get("roof_fitted", name, Describe(preview.Item)));
        }
        internal static void Clear() { _pending = null; _fitted = null; _room = null; _ceiling = null; _boundary = null; _aimed = false; }
        private static void Speak(string text) { Plugin.Log.LogInfo("[Roof] " + text); ScreenReader.Speak(text); }
    }
}
