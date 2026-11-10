using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ParalivesAccess.Speech;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ParalivesAccess.Handlers
{
    internal static class RoomFitSearch
    {
        internal static bool Enabled;
        private static Player _player;
        private static ItemObjectInPlacement _preview;
        private static ZoneWallObject _room;
        private static IEnumerator<Vector3> _points;
        private static Vector3 _size;
        private static Quaternion _rotation;
        private static int _observedFrame,_tested;
        private static float _deadline;
        private static bool _waiting;
        private static Vector3 _observedPosition;
        private static Quaternion _observedRotation;
        private static int _stable;
        private static RoomAccessPlan _access;
        private static string _lastAccessProblem;
        private static int _startFrame;
        internal static bool Active=>_preview!=null;
        internal static bool ApplyPatches(Harmony harmony)
        {
            try {
                var method=AccessTools.Method(typeof(EndMoveItem),"UpdateForPlayer",new[]{typeof(Player)});
                var prefix=AccessTools.Method(typeof(RoomFitSearch),nameof(AllowEnd));harmony.Patch(method,prefix:new HarmonyMethod(prefix));
                return Harmony.GetPatchInfo(method)?.Prefixes.Any(p=>p.owner==Plugin.PluginGuid&&p.PatchMethod==prefix)==true;
            }catch(Exception ex){Plugin.Log.LogError("Room fit isolation failed: "+ex);return false;}
        }
        private static bool AllowEnd(Player player)=>player!=_player||!Active;
        internal static string Start(HybridPlayer h,ItemObjectInPlacement preview,ZoneWallObject room)
        {
            if(!Enabled||h?.Player?.ItemInPlacement!=preview||room==null||!RoomLabelHandler.IsCurrent(room)
                ||preview.Item.IsWallItem||preview.Item.IsCeilingItem||RoofConstructionHandler.IsRoof(preview.Item)
                ||preview.Item.IsLotMovementItem||room.LotPlacedOnGUID!=ItemPlacementHandler.ResolvePlacementLot(h.Player,out _)
                ||h.Player.GetLotLayer(room.LotPlacedOnGUID)!=room.Layer||!HouseholdManager.Instance.OwnsLot(room.LotPlacedOnGUID))return Loc.Get("fit_unavailable");
            Cancel();NativePlacementAttempt.Cancel();_preview=preview;_player=h.Player;_room=room;_tested=0;_waiting=false;_startFrame=Time.frameCount;_lastAccessProblem=null;
            _size=preview.Item.CubeTransform.FinalSize;_rotation=preview.Item.transform.rotation;_deadline=Time.realtimeSinceStartup+30;
            _points=RoomCandidateReader.Read(room,.5f,512).GetEnumerator();
            SlotPlacementHandler.Clear();FacingTargetHandler.Clear();ItemAlignmentHandler.Clear();
            if(RoomPlacementGrid.Selected&&RoomPlacementGrid.Room!=room)RoomPlacementGrid.UseLot();
            RoomBoundaryConstraint.Begin(preview,room.LotPlacedOnGUID,room.Layer,room.AveragePosition);
            WorldTargeting.FrameArea(_player,room.LotPlacedOnGUID,room.Layer,room.AveragePosition,"fit-room");
            return Loc.Get("fit_searching",RoomLabelHandler.Name(room));
        }
        internal static bool Before(HybridPlayer h,ItemObjectInPlacement preview)
        {
            if(!Active)return false;var k=Keyboard.current;
            if(!Valid(h,preview)||k==null){Cancel();return false;}
            if(k.escapeKey.wasPressedThisFrame||k.f11Key.wasPressedThisFrame||k.f10Key.wasPressedThisFrame
                ||k.leftArrowKey.wasPressedThisFrame||k.rightArrowKey.wasPressedThisFrame||k.upArrowKey.wasPressedThisFrame||k.downArrowKey.wasPressedThisFrame
                ||CameraPitchHandler.ConsumedThisFrame||h.ButtonCancel.Down||h.ButtonUndo.Down||h.ButtonRedo.Down
                ||h.ButtonLargeRotateItem.Down||h.ButtonSmallRotateItemLeft.Down||h.ButtonSmallRotateItemRight.Down){Cancel();return false;}
            ItemPlacementHandler.MaintainWorldCursor(h,preview);
            if(k.enterKey.wasReleasedThisFrame||k.numpadEnterKey.wasReleasedThisFrame)Speak(Loc.Get("fit_wait"));return true;
        }
        private static bool Valid(HybridPlayer h,ItemObjectInPlacement preview)=>h?.Player==_player&&preview==_preview
            &&_player.State==GameStates.BuildMode&&!_player.PlayerActionBlockedByUIWindow&&!h.HasInputFieldFocused&&!h.IsUsingGamePad
            &&RoomLabelHandler.IsCurrent(_room)&&BuildModeRefreshManager.IsZoneStateRefreshed&&_player.GetLotLayer(_room.LotPlacedOnGUID)==_room.Layer
            &&Vector3.Distance(preview.Item.CubeTransform.FinalSize,_size)<.001f;
        internal static void Tick()
        {
            if(!Active)return;var h=PlayerManager.Instance?.HybridPlayer1;
            if(!Valid(h,h?.Player?.ItemInPlacement)){Cancel();Speak(Loc.Get("fit_changed"));return;}
            if(Time.realtimeSinceStartup>_deadline){Fail();return;}
            try {
                if(_access==null) {
                    if(Time.frameCount<_startFrame+3||PathfindingManager.Instance.CurrentNavMeshState!=PathfindingManager.NavMeshState.Idle)return;
                    _access=RoomAccessPlan.Build(_room,_preview.Item,out var accessProblem);
                    if(_access==null){Cancel();Speak(accessProblem);return;}
                    Plugin.Log.LogInfo($"[RoomFit] access graph nodes={_access.NodeCount}, doors={_access.DoorCount}, targets={_access.TargetCount}.");
                }
                if(!_access.Current){Cancel();Speak(Loc.Get("fit_access_changed"));return;}
                if(_waiting){
                    if(Time.frameCount<=_observedFrame+2||_preview.CursorTeleported)return;var root=_preview.Item;
                    bool stable=Vector3.Distance(root.CubeTransform.LocalPosition,_observedPosition)<.002f&&Quaternion.Angle(root.transform.rotation,_observedRotation)<.1f;
                    _observedPosition=root.CubeTransform.LocalPosition;_observedRotation=root.transform.rotation;
                    _stable=stable?_stable+1:0;if(_stable<4&&Time.frameCount<_observedFrame+24)return;_waiting=false;
                    string problem=ItemPlacementHandler.SemanticPlacementProblem(h,root);var members=LinkedGroupRecovery.Read(root);if(members.Count==0)members.Add(root);
                    if(_stable>=4&&problem==null&&root.Layer==_room.Layer&&members.All(i=>Fits(_room,i))
                        &&members.All(i=>RoofClearanceReader.Problem(i)==null&&ItemPlacementHandler.BlockingForGroup(_player,i)==null)){
                        string access=_access.Problem(members);
                        if(access!=null){_lastAccessProblem=access;Plugin.Log.LogInfo($"[RoomFit] access rejected point={root.CubeTransform.LocalPosition}: {access}");}
                        else {
                        var room=_room;int tested=_tested;Cancel();ItemPlacementHandler.ReviewAfterAction(h,h.Player.ItemInPlacement);
                        Speak(Loc.Get("fit_found",PlacementLocationReader.CompactTarget(root),Mathf.RoundToInt(root.transform.eulerAngles.y),Loc.Get("fit_access_checked")));
                        Plugin.Log.LogInfo($"[RoomFit] found root={root.InstanceID}, room={room.InstanceID}, point={root.CubeTransform.LocalPosition}, tested={tested}.");return;
                        }
                    }
                }
                for(int n=0;n<8;n++){
                    if(!_points.MoveNext()){Fail();return;}
                    if(!RoomAnchorReader.TryPoint(_room,_points.Current,out var support))continue;
                    if(ItemPlacementHandler.RequestFitTarget(h,_preview,support)!=null)continue;
                    _tested++;_observedFrame=Time.frameCount;_stable=0;_observedPosition=_preview.Item.CubeTransform.LocalPosition;_observedRotation=_preview.Item.transform.rotation;_waiting=true;return;
                }
            }catch(Exception ex){Cancel();Plugin.Log.LogError("Room fit failed: "+ex);Speak(Loc.Get("fit_unavailable"));}
        }
        internal static bool Fits(ZoneWallObject room,ItemObjectRoot item)
        {
            var s=item.CubeTransform.FinalSize;var c=item.GetWorldBoundingBox().Center;var q=item.transform.rotation;
            var corners=new[]{new Vector3(-s.x,0,-s.z),new Vector3(s.x,0,-s.z),new Vector3(s.x,0,s.z),new Vector3(-s.x,0,s.z)}.Select(p=>c+q*(p*.5f)).ToArray();
            for(int i=0;i<4;i++){
                var a=corners[i];var b=corners[(i+1)%4];int samples=Math.Min(128,Math.Max(1,Mathf.CeilToInt(Vector3.Distance(a,b)/.1f)));
                for(int n=0;n<=samples;n++)if(!ZoneGeometry.Contains(room,Vector3.Lerp(a,b,(float)n/samples)))return false;
            }return true;
        }
        private static void Fail(){string name=RoomLabelHandler.Name(_room);string reason=_lastAccessProblem;Cancel();ItemPlacementHandler.ResetRoomBoundaryTarget();Speak(Loc.Get("fit_none",name)+(reason==null?"":". "+reason));}
        internal static void Cancel(){_points?.Dispose();_points=null;_preview=null;_player=null;_room=null;_access=null;}
        private static void Speak(string text)=>ScreenReader.Speak(text);
    }
}
