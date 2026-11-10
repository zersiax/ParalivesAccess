using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ParalivesAccess.Speech;
using Setting;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ParalivesAccess.Handlers
{
    /// <summary>A read-only lot address, independent of placement and character locomotion.</summary>
    internal static class LotCursorHandler
    {
        internal static bool Enabled;
        internal static Key OpenKey = Key.N;
        private static Player _player;
        private static ulong _lot;
        private static int _layer, _holdFrame = -1;
        private static bool _cameraWasFrozen, _enterDown;
        private static PlacementGrid _grid;
        private static Vector3 _position;
        private static UIInteractions _menu;
        private static Player _heldPlayer;
        internal static bool Active => _player != null;
        internal static bool OwnsInput => Active && _menu?.IsVisible != true && Foreground(PlayerManager.Instance?.HybridPlayer1);

        internal static bool ApplyPatches(Harmony harmony)
        {
            try
            {
                foreach (var hook in new[] {
                    (Target: AccessTools.DeclaredMethod(typeof(Back), "Update"), Prefix: nameof(AllowBack)),
                    (Target: AccessTools.DeclaredMethod(typeof(UpdateSelect), "UpdateForPlayer", new[] { typeof(Player) }), Prefix: nameof(AllowNative)) })
                {
                    if (hook.Target == null) return false;
                    var prefix = AccessTools.Method(typeof(LotCursorHandler), hook.Prefix);
                    harmony.Patch(hook.Target, prefix: new HarmonyMethod(prefix));
                    if (Harmony.GetPatchInfo(hook.Target)?.Prefixes.Any(p => p.owner == Plugin.PluginGuid && p.PatchMethod == prefix) != true) return false;
                }
                return true;
            }
            catch (Exception ex) { Plugin.Log.LogError("Lot cursor input hooks failed: " + ex); return false; }
        }

        // Escape closes the cursor. Physical world clicks first release it, then keep their native path.
        private static bool AllowNative(Player player) => !BlocksBack(player);
        private static bool AllowBack() => !BlocksBack(PlayerManager.Instance?.HybridPlayer1?.Player);

        private static bool Foreground(HybridPlayer hybrid)
        {
            var p = hybrid?.Player;
            return p != null && GameLoadingManager.State == State.Game && Application.isFocused
                && !SettingsHandler.IsCapturingBindingInput && !hybrid.HasInputFieldFocused && !hybrid.IsUsingGamePad
                && (p.State == GameStates.BuildMode || p.State == GameStates.LiveMode)
                && !p.PlayerActionBlockedByUIWindow && p.PlayerActionsAreAllowed && !hybrid.IsInTownMap()
                && p.ItemInPlacement == null && p.ItemInResize == null && p.ItemInScale == null
                && p.SegmentInPlacement == null && p.PlatformInPlacement == null
                && !CoordinateEntryHandler.BlocksPlacement && !BuildCatalogHandler.OwnsInput
                && !LotObjectHandler.OwnsInput && !TutorialHandler.OwnsInput
                && !ContextActionHandler.OwnsInput && !OccupationHandler.OwnsInput
                && !TownMapLotHandler.OwnsInput;
        }

        internal static void BeforeGameSystems()
        {
            try
            {
                var hybrid = PlayerManager.Instance?.HybridPlayer1;
                var escapeKeys = Keyboard.current;
                if (_heldPlayer != null)
                {
                    if (escapeKeys != null && (escapeKeys.escapeKey.isPressed || escapeKeys.escapeKey.wasReleasedThisFrame
                        || escapeKeys.backspaceKey.isPressed || escapeKeys.backspaceKey.wasReleasedThisFrame))
                    { _holdFrame = Time.frameCount; LotBuildInputOwnership.HoldThroughCurrentFrame(); }
                    else _heldPlayer = null;
                }
                if (!Enabled || hybrid?.Player == null) { Close(false); return; }
                if (Active)
                {
                    if (hybrid.Player != _player || GameLoadingManager.State != State.Game
                        || (_player.State != GameStates.LiveMode && _player.State != GameStates.BuildMode))
                    { Close(false); return; }
                    // A native action menu is temporary. Remember the exact address while it owns input.
                    if (_menu != null)
                    {
                        _enterDown = false;
                        if (_menu.IsVisible) return;
                        // Back hides the native menu before its focus/block flags and the
                        // closing key have drained. That gesture must not also close the cursor.
                        var closingKeys = Keyboard.current;
                        if (hybrid.HasInputFieldFocused || _player.PlayerActionBlockedByUIWindow
                            || (closingKeys != null && (closingKeys.escapeKey.isPressed || closingKeys.escapeKey.wasReleasedThisFrame
                                || closingKeys.enterKey.isPressed || closingKeys.enterKey.wasReleasedThisFrame
                                || closingKeys.numpadEnterKey.isPressed || closingKeys.numpadEnterKey.wasReleasedThisFrame))) return;
                        _menu = null;
                        LotBuildInputOwnership.HoldThroughCurrentFrame();
                        return;
                    }
                    if (hybrid.IsInTownMap() || !PlacementGridReader.TryRead(_lot, ItemPlacementHandler.GridSpacingMetres, out var current)
                        || !_grid.SameFrame(current)) { Close(false); return; }
                    if (_player.GetLotLayer(_lot) != _layer)
                    { _layer = _player.GetLotLayer(_lot); _enterDown = false; if (Foreground(hybrid)) Speak(Describe()); }
                    if (!CoordinateEntryHandler.BlocksPlacement && !Foreground(hybrid)) { Close(false); return; }
                    if (CoordinateEntryHandler.BlocksPlacement) return;
                    var keys = Keyboard.current;
                    if (keys != null && (keys.escapeKey.wasPressedThisFrame || keys.backspaceKey.wasPressedThisFrame))
                    { _heldPlayer = _player; Close(true); _holdFrame = Time.frameCount; return; }
                    if (ModInputKey.WasPressed(keys, BuildCatalogHandler.OpenKey)
                        || ModInputKey.WasPressed(keys, LotObjectHandler.OpenKey)
                        || ModInputKey.WasPressed(keys, LotObjectHandler.ParaListKey)
                        || ModInputKey.WasPressed(keys, ParaSwitchHandler.SwitchKey))
                    { Close(false, holdFrame: false); return; }
                    if (hybrid.ButtonConfirm.Down || hybrid.ButtonCancel.Down)
                    { Close(false); return; }
                    _player.FreezeCameraMovement = true;
                }
                var keyboard = Keyboard.current;
                if (keyboard == null || keyboard.ctrlKey.isPressed || keyboard.altKey.isPressed
                    || keyboard.leftMetaKey.isPressed || keyboard.rightMetaKey.isPressed) return;
                if (ModInputKey.WasPressed(keyboard, OpenKey) && !keyboard.shiftKey.isPressed)
                {
                    if (Active) Close(true);
                    else if ((Foreground(hybrid) && !LotBuildInputOwnership.OwnsInput)
                        || (LotObjectHandler.OwnsInput && !hybrid.HasInputFieldFocused && !hybrid.Player.PlayerActionBlockedByUIWindow))
                    {
                        if (!ModInputKey.IsAvailable(hybrid, OpenKey, out string conflict)) Speak(conflict);
                        else
                        {
                            LotObjectHandler.CloseForParaSwitch();
                            if (Foreground(hybrid)) Open(hybrid);
                        }
                    }
                    return;
                }
                if (!OwnsInput || CoordinateEntryHandler.BlocksPlacement) return;
                if (keyboard.escapeKey.wasPressedThisFrame || keyboard.backspaceKey.wasPressedThisFrame)
                { _heldPlayer = _player; Close(true); _holdFrame = Time.frameCount; return; }
                if (ModInputKey.WasPressed(keyboard, Key.F10)) { OpenCoordinates(hybrid); return; }
                if (ModInputKey.WasPressed(keyboard, Key.F11))
                {
                    Speak(ModInputKey.IsAvailable(hybrid, Key.F11, out string conflict)
                        ? LotNearbyReader.Describe(_lot, _layer, _position) : conflict);
                    return;
                }
                int x = (keyboard.rightArrowKey.wasPressedThisFrame ? 1 : 0) - (keyboard.leftArrowKey.wasPressedThisFrame ? 1 : 0);
                int z = (keyboard.upArrowKey.wasPressedThisFrame ? 1 : 0) - (keyboard.downArrowKey.wasPressedThisFrame ? 1 : 0);
                if (x != 0 || z != 0)
                {
                    _enterDown = false;
                    _grid.Step(_position.x, _position.z, x, z, keyboard.shiftKey.isPressed, out double wx, out double wz);
                    Move(hybrid, new Vector3((float)wx, _position.y, (float)wz)); return;
                }
                if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)
                    _enterDown = !keyboard.shiftKey.isPressed;
                if (keyboard.enterKey.wasReleasedThisFrame || keyboard.numpadEnterKey.wasReleasedThisFrame)
                {
                    bool requested = _enterDown; _enterDown = false;
                    if (requested && !keyboard.shiftKey.isPressed) OpenGroundMenu(hybrid);
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("Lot cursor failed: " + ex); Close(false); Speak(Loc.Get("cursor_unavailable")); }
        }

        internal static bool BlocksBack(Player player) => player == _player && OwnsInput
            || player == _heldPlayer && _holdFrame == Time.frameCount;

        private static void Open(HybridPlayer hybrid)
        {
            ulong lot = LotObjectHandler.ResolveCurrentLot(hybrid.Player, out _);
            var perimeter = ZoneManager.Instance?.GetLotPerimeterZoneObjectByGUID(lot);
            if (perimeter == null || !PlacementGridReader.TryRead(lot, ItemPlacementHandler.GridSpacingMetres, out var grid))
            { Speak(Loc.Get("cursor_lot_required")); return; }
            Vector3 position = hybrid.HybridCamera.FreeCamera.LookTarget.position;
            if (!ZoneGeometry.Contains(perimeter, position))
            {
                var actor = AssetManager.Instance.GetCharacter(hybrid.Player.GetSelectedCharacterGUID());
                position = actor?.Data != null ? WorldTargeting.GetCharacterPosition(actor) : perimeter.AveragePosition;
            }
            if (!ZoneGeometry.Contains(perimeter, position)) position = perimeter.AveragePosition;
            if (!ZoneGeometry.Contains(perimeter, position)) { Speak(Loc.Get("cursor_lot_required")); return; }
            _player = hybrid.Player; _lot = lot; _layer = _player.GetLotLayer(lot); _grid = grid; _position = position;
            _cameraWasFrozen = _player.FreezeCameraMovement; _player.FreezeCameraMovement = true; _enterDown = false;
            Frame(); LotBuildInputOwnership.HoldThroughCurrentFrame();
            Speak(Loc.Get("cursor_open", OpenKey, PlacementGridReader.Length((float)_grid.Spacing)) + ". " + Describe());
        }

        private static void Move(HybridPlayer hybrid, Vector3 point)
        {
            if (!ZoneGeometry.Contains(ZoneManager.Instance.GetLotPerimeterZoneObjectByGUID(_lot), point))
            { Speak(Loc.Get("cursor_edge")); return; }
            _position = point; Frame(); Speak(Describe());
        }

        private static void Frame()
        {
            var sample = Read();
            _position.y = sample.HasGround ? sample.Position.y : WallHeightManager.Instance.GetFloorWorldY(_lot, _layer);
            WorldTargeting.FrameArea(_player, _lot, _layer, _position, "lot-cursor");
        }

        private static void OpenCoordinates(HybridPlayer hybrid)
        {
            _grid.Coordinates(_position.x, _position.z, out double c, out double r);
            var owner = _player; ulong lot = _lot; int layer = _layer;
            CoordinateEntryHandler.Open(hybrid, null, PlacementGridReader.DescribeBounds(_lot, _grid), c, r,
                () => Active && _player == owner && _lot == lot && _layer == layer && owner.GetLotLayer(lot) == layer,
                (column, row) => { _grid.World(column, row, out double x, out double z); Move(hybrid, new Vector3((float)x, _position.y, (float)z)); },
                title: Loc.Get("cursor_coordinate_title"), instructions: Loc.Get("cursor_coordinate_hint"), announceReview: false);
        }

        private sealed class Sample
        {
            internal bool HasGround;
            internal Vector3 Position;
            internal ZoneWallPlatformObject Floor;
            internal readonly List<ItemObjectRoot> Objects = new List<ItemObjectRoot>();
        }

        private static Sample Read()
        {
            var result = new Sample();
            if (!Active || !BuildModeRefreshManager.IsZoneStateRefreshed || ZoneManager.Instance.DirtyWallZones.Count > 0) return result;
            // Probe the addressed vertical column, rather than a camera ray which may hit a nearer room.
            var hits = RaycastSystem.CastAllGetAll(new Vector3(_position.x, 500f, _position.z), Vector3.down, 1000f,
                includeTerrain: true, includeFloor: true, includeWalls: false, includeObjects: true,
                includeInvisibleObjects: true, includeInvisibleFloors: true);
            float highest = float.NegativeInfinity;
            foreach (var hit in hits)
            {
                if (hit.gameObject == null || !hit.gameObject.activeInHierarchy) continue;
                Vector3 point = hit.point;
                if (hit.colliderType == ColliderType.Object)
                {
                    var item = hit.gameObject.GetComponent<ItemObjectRoot>();
                    if (item != null && item.LotPlacedOnGUID == _lot && item.Layer == _layer && !result.Objects.Contains(item)) result.Objects.Add(item);
                }
                else if (hit.colliderType == ColliderType.Floor)
                {
                    var floor = hit.gameObject.GetComponent<ZoneWallPlatformRenderer>()?.WallZonePlatform;
                    if (floor?.WallZone == null || floor.IsCeiling || floor.DisabledByUser || floor.DisabledButVisible
                        || floor.WallZone.LotPlacedOnGUID != _lot || floor.WallZone.Layer != _layer || point.y <= highest) continue;
                    highest = point.y; result.Position = point; result.Floor = floor; result.HasGround = true;
                }
                else if (hit.colliderType == ColliderType.Terrain
                    && _layer == LotManager.Instance.GetLotByGUID(_lot)?.Data.LayerOffset && point.y > highest)
                {
                    var terrainHit = RaycastSystem.Cast(new Vector3(_position.x, 500f, _position.z), Vector3.down, 1000f,
                        includeTerrain: true, includeFloor: false, includeWalls: false, includeObjects: false);
                    if (terrainHit.hit.collider == null || RaycastSystem.CheckIfTerrainHole(terrainHit.hit)) continue;
                    highest = point.y; result.Position = point; result.Floor = null; result.HasGround = true;
                }
            }
            result.Objects.Sort((a, b) => a.InstanceID.CompareTo(b.InstanceID));
            return result;
        }

        internal static bool TryGetContext(out string report)
        { report = OwnsInput ? Describe() : null; return report != null; }

        private static string Describe()
        {
            if (!BuildModeRefreshManager.IsZoneStateRefreshed || ZoneManager.Instance.DirtyWallZones.Count > 0) return Loc.Get("layout_updating");
            var sample = Read();
            string support = !sample.HasGround ? Loc.Get("cursor_no_ground") : sample.Floor != null
                ? Loc.Get("cursor_floor", CatalogEntryReader.SurfaceName(sample.Floor.SurfaceGUID)) : Loc.Get("cursor_terrain");
            string objects = sample.Objects.Count == 0 ? Loc.Get("cursor_no_objects")
                : Loc.Get("cursor_objects", string.Join("; ", sample.Objects.Select(i => ItemDescription.GetProductIdentity(i) ?? Loc.Get("item_unknown"))));
            return Loc.Get("cursor_report", _layer, PlacementGridReader.Position(_grid, _position),
                PlacementLocationReader.Describe(_lot, _layer, _position), support, objects);
        }

        private static void OpenGroundMenu(HybridPlayer hybrid)
        {
            if (_player.State != GameStates.LiveMode) { Speak(Loc.Get("lots_visit_live_required")); return; }
            if (_player.GetSelectedCharacterGUID() == 0) { Speak(Loc.Get("lots_visit_para_required")); return; }
            var actor = AssetManager.Instance.GetCharacter(_player.GetSelectedCharacterGUID());
            if (actor?.Data == null || actor.Data.CurrentOccupationIndex != -1 || _player.IsInIntro)
            { Speak(Loc.Get("cursor_actions_unavailable")); return; }
            var sample = Read();
            if (!sample.HasGround) { Speak(Loc.Get("cursor_no_ground")); return; }
            var settings = Settings.Get<Interactions>();
            var group = settings?.GetInteractionGroupByGUID(settings.FloorInteractions);
            if (group == null || !_player.SelectedCharactersGUID.Any(g => InteractionManager.Instance.CanShowInteractionGroupInInteractionList(group, null, _player.PlayerIndex, g, 0UL, _lot)))
            { Speak(Loc.Get("cursor_actions_unavailable")); return; }
            _menu = NativeInteractionMenu.GetOrCreate(_player.PlayerIndex, "lot-cursor-ground");
            if (_menu == null) { Speak(Loc.Get("cursor_actions_unavailable")); return; }
            Plugin.Log.LogInfo($"[LotCursor] ground menu lot={_lot}, layer={_layer}, point={sample.Position}, floor={sample.Floor?.InstanceID}, actor={actor.GUID}.");
            _menu.Show(group, sample.Position, -1, 0UL, null, _lot);
            LotBuildInputOwnership.HoldThroughCurrentFrame();
        }

        private static void Close(bool announce, bool holdFrame = true)
        {
            if (!Active) return;
            _player.FreezeCameraMovement = _cameraWasFrozen;
            _player = null; _menu = null; _enterDown = false; _grid = null;
            if (holdFrame) LotBuildInputOwnership.HoldThroughCurrentFrame();
            if (announce) Speak(Loc.Get("cursor_closed"));
        }
        private static void Speak(string text) { Plugin.Log.LogInfo("[LotCursor] " + text); ScreenReader.Speak(text); }
    }
}
