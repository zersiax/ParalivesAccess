using System;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ParalivesAccess.Handlers
{
    public static partial class LotObjectHandler
    {
        internal static Key ParaListKey { get; private set; } = Key.F7;
        private static bool _directParas;

        internal static void ConfigureParaListKey(Key key)
        {
            ParaListKey = key;
            if (key == Key.None) return;
            if (key < Key.F1 || key > Key.F12 || key == Key.F1 || key == Key.F3
                || key == Key.F10 || key == Key.F11 || key == TimeDateHandler.ReadKey
                || key == Key.F5 || key == Key.F12 || key == BuildCatalogHandler.OpenKey || key == ContextActionHandler.ActionKey
                || key == VirtualAudioListenerHandler.ToggleKey || key == ParaSwitchHandler.SwitchKey)
            {
                ParaListKey = Key.None;
                Plugin.Log.LogError("LotParasKey must be a free function key (Ctrl is required); direct Para shortcut disabled.");
            }
        }

        private static bool HandleQuickNavigation(HybridPlayer hybrid, Keyboard keys)
        {
            if (keys.altKey.isPressed || keys.leftMetaKey.isPressed || keys.rightMetaKey.isPressed) return false;
            if (!keys.ctrlKey.isPressed && keys.shiftKey.isPressed && ModInputKey.WasPressed(keys, OpenKey))
            { Open(hybrid, rooms: true); return true; }
            if (keys.ctrlKey.isPressed && !keys.shiftKey.isPressed && ModInputKey.WasPressed(keys, ParaListKey))
            {
                if (_panel != null && _directParas) Close(announce: true);
                else Open(hybrid, paras: true);
                return true;
            }
            return false;
        }

        private static VirtualPanel.IEntry RoomFinderCommand() => new CommandEntry(
            () => _model != null && _model.RoomCount > 0, () => Loc.Get("quick_rooms"), () => OpenRoomFinder());

        private static void OpenRoomFinder(bool pushCurrent = true, bool travel = false)
        {
            var rooms = _model.Floors.SelectMany(f => f.Areas).Where(a => a.Room != null && a.IsAvailable)
                .OrderBy(a => a.Layer).ThenBy(a => a.GetName(), StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(a => a.Room.InstanceID).ToList();
            if (rooms.Count == 0) { Speak(Loc.Get("quick_rooms_empty")); if (!pushCurrent) Close(false); return; }
            var panel = NewPanel(Loc.Get(travel ? "room_travel_panel" : "quick_rooms_panel"));
            foreach (var area in rooms)
            {
                var target = area;
                panel.Add(new CommandEntry(() => target.IsAvailable,
                    () => Loc.Get("quick_room_entry", target.GetName(), target.Layer), () => {
                        if (!travel) JumpToRoom(target);
                        else { var room = target.Room; Close(false); RoomTravelHandler.Start(room); }
                    }));
            }
            SetPanel(panel, null, pushCurrent);
        }

        private static void JumpToRoom(AreaModel area)
        {
            if (area?.Room == null || !area.IsAvailable || !BuildModeRefreshManager.IsZoneStateRefreshed)
            { Speak(Loc.Get("explorer_target_gone")); return; }
            Vector3 point = area.GetAnchor();
            if (RoomAnchorReader.TryRead(area.Room, point, out var inside)) point = inside;
            else if (!ZoneGeometry.Contains(area.Room, point)) { Speak(Loc.Get("explorer_frame_failed")); return; }
            var player = GetPlayer(); ulong lot = _lotGuid; int layer = area.Layer; string name = area.GetName();
            Close(announce: false);
            Speak(WorldTargeting.FrameArea(player, lot, layer, point, "room-shortcut")
                ? Loc.Get("quick_room_arrived", name, layer) : Loc.Get("explorer_frame_failed"));
        }
    }
}
