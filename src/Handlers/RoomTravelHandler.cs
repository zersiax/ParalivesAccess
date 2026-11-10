using System;
using System.Collections.Generic;
using System.Linq;
using ParalivesAccess.Speech;
using Setting;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    internal static class RoomTravelHandler
    {
        // Canonical Go here asset in installed 0.1.9; resolve/validate it before every request.
        private const ulong GoHere = 6154493702141508290UL;
        private static ZoneWallObject _room;
        private static Player _player;
        private static ulong[] _actors;
        private static IEnumerator<Vector3> _points;
        private static float _deadline;
        internal static bool Active => _room != null;

        internal static void Start(ZoneWallObject room)
        {
            var h = PlayerManager.Instance?.HybridPlayer1;
            if (!Available(h, room)) { Speak(Loc.Get("room_travel_unavailable")); return; }
            Clear(); _room = room; _player = h.Player; _actors = _player.SelectedCharactersGUID.ToArray();
            _points = RoomCandidateReader.Read(room, .5f, 512).GetEnumerator();
            _deadline = Time.realtimeSinceStartup + 15;
            Speak(Loc.Get("room_travel_search", RoomLabelHandler.Name(room)));
        }

        private static bool Available(HybridPlayer h, ZoneWallObject room) => h?.Player != null
            && GameLoadingManager.State == State.Game && h.Player.State == GameStates.LiveMode
            && h.Player.PlayerActionsAreAllowed && !h.Player.PlayerActionBlockedByUIWindow && !h.HasInputFieldFocused
            && !h.IsUsingGamePad && !h.Player.IsInIntro && h.Player.SelectedCharactersGUID?.Count > 0
            && RoomLabelHandler.IsCurrent(room) && BuildModeRefreshManager.IsZoneStateRefreshed
            && Settings.Get<Interactions>().GetInteractionByGUID(GoHere) != null;

        internal static void Tick()
        {
            if (!Active) return;
            var h = PlayerManager.Instance?.HybridPlayer1;
            if (Time.realtimeSinceStartup > _deadline) { Clear(); Speak(Loc.Get("room_travel_unavailable")); return; }
            if (h?.Player != _player || !Available(h, _room) || !_actors.SequenceEqual(_player.SelectedCharactersGUID))
            { Clear(); Speak(Loc.Get("room_travel_changed")); return; }
            try
            {
                var actors = _actors.Select(g => AssetManager.Instance.GetCharacter(g)).ToArray();
                if (actors.Any(a => a?.Data == null || a.Data.IsDead || a.Data.CurrentOccupationIndex != -1))
                { Clear(); Speak(Loc.Get("room_travel_unavailable")); return; }
                if (PathfindingManager.Instance.CurrentNavMeshState != PathfindingManager.NavMeshState.Idle) return;
                for (int n = 0; n < 6; n++)
                {
                    if (!_points.MoveNext()) { string name = RoomLabelHandler.Name(_room); Clear(); Speak(Loc.Get("room_travel_no_path", name)); return; }
                    var candidate = _points.Current;
                    var hit = PathfindingManager.Instance.GetClosestPositionOnNavMeshSmallRadius(candidate);
                    if (!hit.found || Vector3.Distance(hit.position, candidate) > .35f
                        || !ZoneGeometry.Contains(_room, hit.position)
                        || actors.Any(a => !PathfindingManager.CanCharacterFindPath(a, WorldTargeting.GetCharacterPosition(a), hit.position))) continue;
                    var group = Settings.Get<Interactions>().GetInteractionGroupByGUID(Settings.Get<Interactions>().FloorInteractions);
                    if (group == null || actors.Any(a => !InteractionManager.Instance.CanShowInteractionGroupInInteractionList(group, null, _player.PlayerIndex, a.GUID, 0, _room.LotPlacedOnGUID)))
                    { Clear(); Speak(Loc.Get("room_travel_unavailable")); return; }
                    ulong lot = _room.LotPlacedOnGUID; string roomName = RoomLabelHandler.Name(_room);
                    ulong action = InteractionManager.Instance.AddToInteractionQueueOfSelectedCharactersFromPlayerInput(_player.PlayerIndex,
                        GoHere, new InteractionTarget { WorldPosition = hit.position, ItemInstanceID = -1 }, lot);
                    Plugin.Log.LogInfo($"[RoomTravel] action={action}, lot={lot}, room={_room.InstanceID}, target={hit.position}, actors={string.Join(",", _actors)}.");
                    Clear(); Speak(Loc.Get("room_travel_requested", roomName)); return;
                }
            }
            catch (Exception ex) { Clear(); Plugin.Log.LogError("Room travel failed: " + ex); Speak(Loc.Get("room_travel_unavailable")); }
        }
        private static void Clear() { _points?.Dispose(); _points = null; _room = null; _player = null; _actors = null; }
        private static void Speak(string text) => ScreenReader.Speak(text);
    }
}
