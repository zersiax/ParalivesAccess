namespace ParalivesAccess.Handlers {
 public static class StructureExplorerChecks {
  static VirtualPanel _panel;static Stack<VirtualPanel> history=new Stack<VirtualPanel>();static ulong _lotGuid=1;static bool _enterPressed,_enterWithShift;static VirtualPanel.IEntry _enterEntry;
  interface IParaSelectionEntry {void ToggleSelection();}
  static Player GetPlayer()=>PlayerManager.Instance.HybridPlayer1.Player;
  static VirtualPanel NewPanel(string name)=>new VirtualPanel(name);
  static void SetPanel(VirtualPanel panel,VirtualPanel.IEntry preferred,bool pushCurrent,Func<bool> contextAvailable=null){if(pushCurrent&&_panel!=null)history.Push(_panel);_panel=panel;_enterPressed=false;_panel.Enter(preferred);}
  static void Close(bool announce){_panel=null;history.Clear();LotBuildInputOwnership.HoldThroughCurrentFrame();}
  static void GoBack(){_panel=history.Pop();_panel.Enter();}
  static void Speak(string message)=>ScreenReader.Speak(message);
  EXPLORER_MEMBERS
  static int checks;static void Check(bool ok,string name){if(!ok)throw new Exception(name);checks++;}
  static void Press(){Keyboard.current=new Keyboard();Keyboard.current.enterKey.wasPressedThisFrame=true;HandleEnter(Keyboard.current);}
  static void Release(){Keyboard.current=new Keyboard();Keyboard.current.enterKey.wasReleasedThisFrame=true;HandleEnter(Keyboard.current);}
  static void Enter(){Press();Release();}
  static ZoneWallObject Setup(out SegmentWallSectionObject wall){BuildEditingChecks.Reset();SystemManager.Instance=new SystemManager();Close(false);var room=BuildEditingChecks.Room(out wall,out var back);foreach(var edge in room.Segments){edge.Segment.NodeA=new NodeObject{Position=edge.Segment.SegmentPoints[0]};edge.Segment.NodeB=new NodeObject{Position=edge.Segment.SegmentPoints[1]};}GetPlayer().ItemInPlacement=null;return room;}
  public static string Run(){
   var room=Setup(out var wall);var deletion=new DeleteSegmentEvent{Result=m=>m.SegmentToDelete.gameObject.SetActive(false)};SystemManager.Instance.SystemsToUpdate.Add(deletion);
   OpenStructureWalls(room);Check(_panel.Current.Describe().Contains("structure_wall"),"F7 structural wall list uses production description");Enter();Check(_panel.Current.Describe().Contains("structure_delete_wall"),"first Enter selects exact wall commands");Enter();Check(_panel.Current.Describe().Contains("structure_confirm")&&deletion.Calls==0,"second Enter opens review without deletion");Release();Check(deletion.Calls==0,"review opening release cannot become confirmation");Enter();Check(deletion.Calls==1&&deletion.Last.SegmentToDelete==wall.Wall&&_panel==null,"fresh Enter confirms exact wall once and releases explorer");
   room=Setup(out wall);deletion=new DeleteSegmentEvent();SystemManager.Instance.SystemsToUpdate.Add(deletion);OpenStructureWalls(room);Enter();Enter();_panel.Move(1);Enter();Check(deletion.Calls==0&&_panel.Current.Describe().Contains("structure_delete_wall"),"review cancel returns without native operation");
   room=Setup(out wall);deletion=new DeleteSegmentEvent();SystemManager.Instance.SystemsToUpdate.Add(deletion);OpenStructureWalls(room);Press();wall.Wall.InstanceID++;Release();Check(deletion.Calls==0&&ScreenReader.Spoken.Last().Contains("explorer_target_gone"),"reused wall cannot activate between key press and release");
   room=Setup(out wall);var floor=new ZoneWallPlatformObject{WallZone=room};room.Platforms.Add(floor);OpenStructureFloors(room);Enter();Check(_panel.Current.Describe().Contains("structure_floor_raise"),"F7 platform list opens construction commands");Enter();Check(ZoneManager.Instance.PlatformCalls==0&&_panel.Name.Contains("structure_floor_create_review"),"base raise review explains creation");ZoneManager.Instance.PlatformResult=()=>room.Platforms.Add(new ZoneWallPlatformObject{WallZone=room,Height=1});Enter();Check(ZoneManager.Instance.PlatformCalls==1&&ZoneManager.Instance.RequestedCreate&&_panel==null,"F7 confirmed platform command calls native creation");
   room=Setup(out wall);floor=new ZoneWallPlatformObject{WallZone=room};room.Platforms.Add(floor);OpenStructureFloors(room);Enter();Enter();Press();floor.Height=2;Release();Check(ZoneManager.Instance.PlatformCalls==0&&ScreenReader.Spoken.Last().Contains("structure_changed"),"platform changed after review cannot apply original action");
   room=Setup(out wall);deletion=new DeleteSegmentEvent();SystemManager.Instance.SystemsToUpdate.Add(deletion);OpenStructureWalls(room);Enter();Enter();InputManager.Shift=true;Enter();Check(deletion.Calls==0&&ScreenReader.Spoken.Last().Contains("structure_changed"),"modified F7 confirmation refused by shared mutation gate");InputManager.Shift=false;
   room=Setup(out wall);OpenRoomExtension(room);Check(_panel.Name.Contains("extension_choose_wall")&&_panel.Current.Describe().Contains("extension_side"),"F7 room action offers semantic shared-wall choices");Enter();Check(_panel==null&&UI.Form.IsVisible&&CoordinateEntryHandler.DescribeForm(UI.Form).Contains("extension_depth_hint"),"F7 wall selection transfers ownership to the depth form");CoordinateEntryHandler.Cancel();Time.frameCount++;
   room=Setup(out wall);OpenStructureWalls(room);Enter();_panel.Move(1);Check(_panel.Current.Describe().Contains("extension_open"),"existing wall commands also expose room extension");Enter();Check(_panel==null&&UI.Form.IsVisible,"exact existing wall shortcut opens its room-side depth form");CoordinateEntryHandler.Cancel();Time.frameCount++;
   room=Setup(out wall);OpenRoomExtension(room);Press();wall.Wall.InstanceID++;Release();Check(!UI.Form.IsVisible&&ScreenReader.Spoken.Last().Contains("extension_changed"),"stale wall cannot open extension after Enter press");
   return checks+" F7 structural navigation and review-routing checks passed.";
  }
 }
}
