using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ParalivesAccess.Speech;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ParalivesAccess.Handlers
{
    /// <summary>Room-relative ceiling positioning through the game's underside ray and native transaction.</summary>
    internal static class CeilingPlacementHandler
    {
        internal static bool Enabled;
        private static Player _player;
        private static ItemObjectInPlacement _preview;
        private static List<ZoneWallObject> _rooms = new List<ZoneWallObject>();
        private static ZoneWallObject _room;
        private static ZoneWallPlatformObject _support;
        private static PlacementGrid _grid;
        private static Vector3 _reference, _target, _supportPoint, _observed, _size;
        private static Quaternion _rotation;
        private static Vector2 _screen;
        private static ulong _lot;
        private static int _layer, _index, _stable, _submittedFrame = -1;
        private static bool _choosing, _hasScreen, _announce;
        private static string _failure, _lastReport;
        private static InputButton _injected;
        internal static bool Active => _preview != null;

        internal static bool ApplyPatches(Harmony harmony)
        {
            try {
                var move=AccessTools.Method(typeof(UpdateMoveItem),"UpdateForPlayer",new[]{typeof(Player)});
                var end=AccessTools.Method(typeof(EndMoveItem),"UpdateForPlayer",new[]{typeof(Player)});
                var allowMove=AccessTools.Method(typeof(CeilingPlacementHandler),nameof(AllowMove));
                var allowEnd=AccessTools.Method(typeof(CeilingPlacementHandler),nameof(AllowEnd));
                harmony.Patch(move,prefix:new HarmonyMethod(allowMove));harmony.Patch(end,prefix:new HarmonyMethod(allowEnd));
                return Harmony.GetPatchInfo(move)?.Prefixes.Any(p=>p.owner==Plugin.PluginGuid&&p.PatchMethod==allowMove)==true
                    &&Harmony.GetPatchInfo(end)?.Prefixes.Any(p=>p.owner==Plugin.PluginGuid&&p.PatchMethod==allowEnd)==true;
            }catch(Exception ex){Plugin.Log.LogError("Ceiling input hooks failed: "+ex);return false;}
        }
        private static bool NativeOwns(Player player)=>!Enabled||!ItemPlacementHandler.Enabled||player.ItemInPlacement!=_preview
            ||player.State!=GameStates.BuildMode||PlayerManager.Instance?.HybridPlayer1?.IsUsingGamePad==true;
        private static bool AllowMove(Player player)
        {
            if(player!=_player||!Active||NativeOwns(player))return true;
            if(_choosing)return false;
            var h=PlayerManager.Instance?.HybridPlayer1;
            if(player.ItemInPlacement==_preview&&Context(h)&&!h.HasInputFieldFocused&&_hasScreen) {
                Project(h);
                // Preserve fractional screen coordinates at the actual native movement boundary.
                // An integral mouse position otherwise makes a settled ceiling target wobble.
                _preview.CursorTeleported=true;_preview.CursorTeleportPosition=new Vector3(_screen.x,_screen.y,0);
            }
            return true;
        }
        private static bool AllowEnd(Player player)=>player!=_player||!Active||NativeOwns(player)||NativePlacementAttempt.IsSubmitting(player)||_submittedFrame==Time.frameCount;

        internal static void Begin(HybridPlayer h,ItemObjectInPlacement preview,ulong lot)
        {
            Clear();_player=h.Player;_preview=preview;_lot=lot;_layer=_player.GetLotLayer(lot);
            if(!Enabled){Speak(Loc.Get("ceiling_unavailable"));return;}
            OpenRooms(h);
            if(!preview.IsNewItem) {
                var current=_rooms.FirstOrDefault(r=>ZoneGeometry.Contains(r,preview.Item.CubeTransform.LocalPosition));
                if(current!=null)Select(h,current,preview.Item.CubeTransform.LocalPosition);
            }
        }
        internal static void Clear(){if(_injected!=null){_injected.Update();_injected=null;}_player=null;_preview=null;_room=null;_support=null;_grid=null;_rooms.Clear();_stable=0;_hasScreen=false;_failure=null;_lastReport=null;_submittedFrame=-1;}
        private static bool Context(HybridPlayer h)=>h?.Player==_player&&_player?.ItemInPlacement==_preview
            &&_player.State==GameStates.BuildMode&&_player.PlayerActionsAreAllowed&&!_player.PlayerActionBlockedByUIWindow&&!h.IsUsingGamePad
            &&_lot!=0&&HouseholdManager.Instance.OwnsLot(_lot)&&_player.GetLotLayer(_lot)==_layer;
        private static bool RoomCurrent()=>_room!=null&&RoomLabelHandler.IsCurrent(_room)&&_room.LotPlacedOnGUID==_lot&&_room.Layer==_layer
            &&BuildModeRefreshManager.IsZoneStateRefreshed;
        internal static bool TrySupport(ZoneWallObject room,Vector3 point,out ZoneWallPlatformObject platform,out Vector3 support,out Vector3 floor)
        {
            platform=null;support=default(Vector3);floor=default(Vector3);
            if(!RoomLabelHandler.IsCurrent(room)||!BuildModeRefreshManager.IsZoneStateRefreshed
                ||!RoomAnchorReader.TryPoint(room,point,0,out floor))return false;
            float nearest=float.PositiveInfinity;
            foreach(var hit in RaycastSystem.CastAllGetAll(floor+Vector3.up*.05f,Vector3.up,100f,
                includeTerrain:false,includeFloor:true,includeWalls:false,includeObjects:false,includeInvisibleFloors:true)) {
                var candidate=hit.gameObject?.GetComponent<ZoneWallPlatformRenderer>()?.WallZonePlatform;
                if(hit.colliderType!=ColliderType.Floor||candidate?.WallZone==null||candidate.DisabledByUser||candidate.DisabledButVisible
                    ||candidate.WallZone.LotPlacedOnGUID!=room.LotPlacedOnGUID||candidate.WallZone.Layer-(candidate.IsCeiling?0:1)!=room.Layer)continue;
                Vector3 p=hit.point;if(p.y<=floor.y+.1f||p.y>=nearest)continue;
                nearest=p.y;platform=candidate;support=p;
            }
            return platform!=null;
        }
        internal static void OpenRooms(HybridPlayer h)
        {
            _layer=_player.GetLotLayer(_lot);
            _choosing=true;_hasScreen=false;_stable=0;_rooms=RoomSpatialReader.OrderedRooms(_lot,_layer)
                .Where(r=>TryAnchor(r,out _)).ToList();
            var camera=h.HybridCamera?.FreeCamera?.LookTarget?.position??Vector3.zero;
            _index=Math.Max(0,_rooms.FindIndex(r=>ZoneGeometry.Contains(r,camera)));Speak(Describe());
        }
        private static bool TryAnchor(ZoneWallObject room,out Vector3 point)
        {
            if(RoomAnchorReader.TryRead(room,room.AveragePosition,out point)&&TrySupport(room,point,out _,out _,out _))return true;
            foreach(var candidate in RoomCandidateReader.Read(room,.5f,128))
                if(RoomAnchorReader.TryPoint(room,candidate,out point)&&TrySupport(room,point,out _,out _,out _))return true;
            point=default(Vector3);return false;
        }
        private static void Select(HybridPlayer h,ZoneWallObject room,Vector3 preferred)
        {
            if(!TryAnchor(room,out _reference)||!PlacementGridReader.TryRead(_lot,ItemPlacementHandler.GridSpacingMetres,out var lotGrid))
            {Speak(Loc.Get("ceiling_no_support"));return;}
            _room=room;_grid=new PlacementGrid(_reference.x,_reference.z,lotGrid.Rotation,ItemPlacementHandler.GridSpacingMetres);
            var point=ZoneGeometry.Contains(room,preferred)?preferred:_reference;
            if(!TrySupport(room,point,out _support,out _supportPoint,out _)){Speak(Loc.Get("ceiling_no_support"));return;}
            _target=new Vector3(point.x,_supportPoint.y,point.z);_choosing=false;_failure=null;_hasScreen=false;_stable=0;_announce=true;
            WorldTargeting.FrameArea(_player,_lot,_layer,_reference,"ceiling-room");Project(h);Speak(Loc.Get("ceiling_controls",RoomLabelHandler.Name(room),
                PlacementGridReader.Length((float)_grid.Spacing),PlacementGridReader.Length((float)_grid.Spacing/5)));
        }
        internal static void Centre(HybridPlayer h){if(_room==null){OpenRooms(h);return;}SetTarget(h,_reference);}
        private static void SetTarget(HybridPlayer h,Vector3 target)
        {
            if(!RoomCurrent()||!ZoneGeometry.Contains(_room,target)){Speak(Loc.Get("ceiling_outside"));return;}
            if(!TrySupport(_room,target,out var support,out var point,out _)){Speak(Loc.Get("ceiling_no_support"));return;}
            _target=new Vector3(target.x,point.y,target.z);_support=support;_supportPoint=point;_failure=null;_stable=0;_hasScreen=false;_announce=true;Project(h);
        }
        private static void Project(HybridPlayer h)
        {
            if(_choosing||!RoomCurrent())return;
            _player.HasSnap=false;_preview.Item.UsesCeilingRaycast=true;
            var offset=_preview.Item.ItemOffset!=null?_preview.Item.transform.rotation*_preview.Item.ItemOffset.Offset:Vector3.zero;
            var aim=new Vector3(_target.x,_supportPoint.y+offset.y,_target.z);
            if(!ItemPlacementHandler.TryProjectTarget(h,aim,out var screen)){_failure=Loc.Get("ceiling_out_of_view");_hasScreen=false;return;}
            if(Mouse.current==null){_failure=Loc.Get("placement_mouse_missing");_hasScreen=false;return;}
            if(!_hasScreen||Vector2.Distance(screen,_screen)>.25f) {
                if(!_hasScreen)_stable=0;
                _screen=screen;_hasScreen=true;Mouse.current.WarpCursorPosition(screen);
                _preview.CursorTeleported=true;_preview.CursorTeleportPosition=new Vector3(screen.x,screen.y,0);
            }
        }
        internal static void Before(HybridPlayer h,ItemObjectInPlacement preview)
        {
            if(preview==_preview&&h.Player==_player&&!h.IsUsingGamePad&&_player.State==GameStates.BuildMode&&_player.GetLotLayer(_lot)!=_layer)
            { OpenRooms(h); return; }
            if(preview!=_preview||!Context(h)||h.HasInputFieldFocused)return;
            var k=Keyboard.current;if(k==null||h.ButtonCancel.Down||h.ButtonUndo.Down||h.ButtonRedo.Down)return;
            bool enter=k.enterKey.wasReleasedThisFrame||k.numpadEnterKey.wasReleasedThisFrame;
            int x=(k.rightArrowKey.wasPressedThisFrame?1:0)-(k.leftArrowKey.wasPressedThisFrame?1:0);
            int z=(k.upArrowKey.wasPressedThisFrame?1:0)-(k.downArrowKey.wasPressedThisFrame?1:0);
            if(_choosing) {
                int move=x!=0?x:-z;if(move!=0&&_rooms.Count>0){_index=(_index+move+_rooms.Count)%_rooms.Count;Speak(Describe());}
                else if(enter&&_rooms.Count>0&&!k.ctrlKey.isPressed&&!k.shiftKey.isPressed&&!k.altKey.isPressed)Select(h,_rooms[_index],_rooms[_index].AveragePosition);
                return;
            }
            if(!RoomCurrent()){_failure=Loc.Get("ceiling_changed");if(enter)Speak(_failure);return;}
            if(CoordinateEntryHandler.WasRequested(h)){OpenCoordinates(h);return;}
            if(ModInputKey.WasPressed(k,Key.Home)||ModInputKey.WasPressed(k,Key.End)) {
                var key=ModInputKey.WasPressed(k,Key.Home)?Key.Home:Key.End;
                if(!ModInputKey.IsAvailable(h,key,out var conflict)){Speak(conflict);return;}
                if(key==Key.Home)WorldTargeting.FrameArea(_player,_lot,_layer,_reference,"ceiling-reframe");Centre(h);return;
            }
            if(x!=0||z!=0) {
                _grid.Step(_target.x,_target.z,x,z,k.shiftKey.isPressed,out double wx,out double wz);SetTarget(h,new Vector3((float)wx,_target.y,(float)wz));return;
            }
            if(h.ButtonLargeRotateItem.Down||h.ButtonSmallRotateItemLeft.Down||h.ButtonSmallRotateItemRight.Down){_stable=0;_announce=true;}
            Project(h);
            if(!enter)return;
            if(k.ctrlKey.isPressed||k.shiftKey.isPressed||k.altKey.isPressed){Speak(Loc.Get("slot_release_modifiers"));return;}
            string problem=Ready(h);if(problem!=null){Plugin.Log.LogInfo($"[Ceiling] confirm refused: {problem}; stable={_stable}, screen={_hasScreen}, actual={preview.Item.CubeTransform.LocalPosition}, observed={_observed}, cursorPending={preview.CursorTeleported}, rotate={h.ButtonLargeRotateItem.Down}/{h.ButtonSmallRotateItemLeft.Down}/{h.ButtonSmallRotateItemRight.Down}.");Speak(problem);return;}
            _submittedFrame=Time.frameCount;_injected=h.ButtonConfirm;_injected.SetValue(0f);
            Plugin.Log.LogInfo($"[Ceiling] request item={preview.Item.InstanceID}, room={_room.InstanceID}, support={_support.InstanceID}, actual={preview.Item.CubeTransform.LocalPosition}.");
        }
        private static void OpenCoordinates(HybridPlayer h)
        {
            var room=_room;var grid=_grid;float height=_supportPoint.y;
            grid.Coordinates(_target.x,_target.z,out double column,out double row);
            CoordinateEntryHandler.Open(h,_preview,Loc.Get("ceiling_coordinate_hint",PlacementGridReader.Length((float)grid.Spacing)),column,row,
                ()=>h.Player==_player&&_player.ItemInPlacement==_preview&&_player.State==GameStates.BuildMode&&!h.IsUsingGamePad
                    &&_player.GetLotLayer(_lot)==_layer&&HouseholdManager.Instance.OwnsLot(_lot)
                    &&RoomCurrent()&&_room==room&&_grid==grid&&TrySupport(room,_target,out var current,out var point,out _)
                    &&Math.Abs(point.y-height)<.005f,
                (c,r)=>{grid.World(c,r,out double x,out double z);SetTarget(h,new Vector3((float)x,_target.y,(float)z));});
        }
        internal static void AcceptActual(HybridPlayer h,ItemObjectInPlacement preview)
        {if(preview==_preview&&!_choosing&&RoomCurrent())SetTarget(h,preview.Item.CubeTransform.LocalPosition);}
        internal static string Problem(HybridPlayer h,ItemObjectRoot root)
        {
            if(root!=_preview?.Item)return null;
            if(!Enabled||!Context(h)||_choosing||!RoomCurrent()||_failure!=null)return _failure??Loc.Get("ceiling_changed");
            if(!TrySupport(_room,_target,out var support,out var point,out var floor)||Math.Abs(point.y-_supportPoint.y)>.005f)return Loc.Get("ceiling_changed");
            var renderer=_preview.LastCollidedObject?.GetComponent<ZoneWallPlatformRenderer>();
            var nativeSupport=renderer?.WallZonePlatform;
            // A ceiling and the floor directly above can share a plane. The game's actual
            // underside selection is authoritative when both support the reviewed room/height.
            if(_preview.LastColliderType!=ColliderType.Floor||nativeSupport?.WallZone==null||!renderer.IsUnderside
                ||nativeSupport.DisabledByUser||nativeSupport.DisabledButVisible||nativeSupport.WallZone.LotPlacedOnGUID!=_lot
                ||nativeSupport.WallZone.Layer-(nativeSupport.IsCeiling?0:1)!=_layer
                ||Math.Abs(nativeSupport.transform.position.y-_supportPoint.y)>.05f
                ||root.LotPlacedOnGUID!=_lot||root.Layer!=_layer||root.IsCeilingItemNotOnCeiling)return Loc.Get("ceiling_wrong_support");
            var position=root.CubeTransform.LocalPosition;
            if(Vector2.Distance(new Vector2(position.x,position.z),new Vector2(_target.x,_target.z))>.015f)return Loc.Get("ceiling_mismatch");
            var offset=root.ItemOffset!=null?root.transform.rotation*root.ItemOffset.Offset:Vector3.zero;
            if(Math.Abs(position.y-(_supportPoint.y+offset.y-.03f))>.04f)return Loc.Get("ceiling_wrong_support");
            var corners=ItemFootprintReader.Corners(root);if(corners.Count==0)return Loc.Get("placement_geometry_unavailable");
            if(corners.Min(p=>p.y)<floor.y+.05f||corners.Max(p=>p.y)>_supportPoint.y+.05f)return Loc.Get("ceiling_height_invalid");
            foreach(var corner in corners.Select(p=>new Vector3(p.x,0,p.z)).Distinct())
                if(!ZoneGeometry.Contains(_room,corner)||!TrySupport(_room,corner,out _,out var edge,out _)||Math.Abs(edge.y-_supportPoint.y)>.05f)return Loc.Get("ceiling_outside");
            return null;
        }
        private static string Ready(HybridPlayer h)
        {
            if(_failure!=null)return _failure;
            if(_choosing||!RoomCurrent())return Loc.Get("ceiling_changed");
            if(CoordinateEntryHandler.BlocksPlacement||!_hasScreen||_stable<3||h.HasInputFieldFocused
                ||_preview.IsInOffset||_preview.FromRotationWidget||_player.ItemInRotation!=null)return Loc.Get("placement_wait");
            return ItemPlacementHandler.SemanticPlacementProblem(h,_preview.Item);
        }
        internal static void After(HybridPlayer h,ItemObjectInPlacement preview)
        {
            if(_injected!=null){_injected.Update();_injected=null;}
            if(preview==_preview&&h.IsUsingGamePad){_choosing=true;_hasScreen=false;_stable=0;return;}
            if(preview!=_preview||!Context(h)||_choosing||CoordinateEntryHandler.BlocksPlacement||PlacementActionsHandler.Active)return;
            var actual=preview.Item.CubeTransform.LocalPosition;
            var offset=preview.Item.ItemOffset!=null?preview.Item.transform.rotation*preview.Item.ItemOffset.Offset:Vector3.zero;
            bool reached=Vector2.Distance(new Vector2(actual.x,actual.z),new Vector2(_target.x,_target.z))<=.015f
                &&Math.Abs(actual.y-(_supportPoint.y+offset.y-.03f))<=.04f;
            bool same=reached&&Vector3.Distance(_size,preview.Item.CubeTransform.FinalSize)<.001f
                &&Quaternion.Angle(_rotation,preview.Item.transform.rotation)<.1f;
            if((!same||preview.CursorTeleported)&&Time.frameCount%30==0)Plugin.Log.LogInfo($"[Ceiling] settling positionDelta={Vector3.Distance(_observed,preview.Item.CubeTransform.LocalPosition):F6}, sizeDelta={Vector3.Distance(_size,preview.Item.CubeTransform.FinalSize):F6}, rotationDelta={Quaternion.Angle(_rotation,preview.Item.transform.rotation):F6}, cursorPending={preview.CursorTeleported}.");
            _stable=same&&!preview.CursorTeleported?_stable+1:0;_observed=preview.Item.CubeTransform.LocalPosition;_size=preview.Item.CubeTransform.FinalSize;_rotation=preview.Item.transform.rotation;
            if(_stable<3)return;string report=Describe();if(_announce||report!=_lastReport){_announce=false;_lastReport=report;Speak(report);}
        }
        internal static string Describe(bool detailed=false)
        {
            if(_preview==null)return Loc.Get("ceiling_unavailable");
            if(_choosing)return _rooms.Count==0?Loc.Get("ceiling_no_rooms"):Loc.Get("ceiling_room_choice",RoomLabelHandler.Name(_rooms[_index]),_index+1,_rooms.Count);
            string problem=Ready(PlayerManager.Instance?.HybridPlayer1);
            string position=PlacementGridReader.Position(_grid,_preview.Item.CubeTransform.LocalPosition);
            return (ItemDescription.GetProductIdentity(_preview.Item)??Loc.Get("item_unknown"))+". "+Loc.Get("ceiling_status",RoomLabelHandler.Name(_room),position,Height(_preview.Item,_supportPoint.y,_reference.y),problem??Loc.Get("placement_valid"))
                +(detailed?". "+PlacementGridReader.Dimensions(_preview.Item):"");
        }
        private static string Height(ItemObjectRoot root,float support,float floor)
        {
            var corners=ItemFootprintReader.Corners(root);float bottom=corners.Count>0?corners.Min(p=>p.y):root.CubeTransform.LocalPosition.y;
            return Loc.Get("ceiling_height",PlacementGridReader.Length(support-floor),PlacementGridReader.Length(bottom-floor));
        }
        internal static string Result(ItemObjectRoot root)=>root?.FloorPlacedOn?.WallZone!=null&&!root.IsCeilingItemNotOnCeiling
            &&root.FloorPlacedOn.WallZone.Layer-(root.FloorPlacedOn.IsCeiling?0:1)==root.Layer
            ?Loc.Get("ceiling_attached")+". "+Height(root,root.FloorPlacedOn.transform.position.y,WallHeightManager.Instance.GetFloorWorldY(root.LotPlacedOnGUID,root.Layer))
            :Loc.Get("ceiling_wrong_support");
        private static void Speak(string text)=>ScreenReader.Speak(text);
    }
}
