namespace ParalivesAccess.Handlers {
 public static partial class LotDescriptionChecks {
  static string NewSave(string root,string name){string folder=Path.Combine(root,name);Directory.CreateDirectory(folder);SavedGameManager.Instance.CurrentSavedGame=new AssetSavedGame{FilePath=folder};Time.frameCount++;return folder;}
  static void Name(ZoneWallObject room,string text){RoomLabelHandler.Open(room);Check(CoordinateEntryHandler.Valid!=null&&CoordinateEntryHandler.Valid(),"native naming request captures a current room");CoordinateEntryHandler.Submit(text);Time.frameCount++;}
  static void SaveStep(GameSavingRequest request,GameSavingPhase phase,Action native=null){
   request.CurrentPhase=phase;var args=new object[]{request,phase};
   typeof(RoomLabelHandler).GetMethod("BeforeSaveStep",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic).Invoke(null,args);
   native?.Invoke();
   typeof(RoomLabelHandler).GetMethod("AfterSaveStep",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic).Invoke(null,new object[]{request,args[1]});
  }
  static void Fetch(AssetLot asset){asset.Data.Zones=ZoneManager.Instance.Zones.OfType<ZoneWallObject>().Select(r=>new AssetLotDataZone{InstanceID=r.InstanceID,Layer=r.Layer,SegmentInstanceIDs=r.Segments.Select(e=>new AssetSegmentDataZoneSegment{SegmentEntityInstanceID=e.Segment.InstanceID,FromAToB=e.FromAToB}).ToList()}).ToList();}
  static RoomLabelIdentity Identity(int id,string shape,params string[] edges)=>new RoomLabelIdentity{Id=id,Shape=shape,Edges=edges.ToList()};
  public static string RunRoomNames(string root){
   int firstCheck=checks;root=Path.Combine(Path.GetFullPath(root),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
   Check(RoomLabelHandler.ApplyPatches(new Harmony()),"save hooks are checked before naming is enabled");RoomLabelHandler.Enabled=true;
   Reset(74.8,74.8);NewSave(root,"room-order");var original=Room(24,26,32,34);Name(original,"Living Room");
   var addedLeft=Room(20,26,24,34);Time.frameCount++;
   Check(RoomLabelHandler.Name(addedLeft)=="room 2"&&RoomLabelHandler.Name(addedLeft,count:2)=="Unnamed room 2 of 2","new left extension counts the older named room in J and F7 fallback labels");
   Name(addedLeft,"Kitchen");var addedFront=Room(24,20,32,26);Time.frameCount++;
   Check(RoomLabelHandler.Name(addedFront)=="room 3"&&RoomReport(original).Contains("Shares front wall with room 3"),"second extension counts both named rooms and shares the same number in adjoining-room descriptions");
   H.HybridCamera.FreeCamera.LookTarget.position=P(28,23);
   Check(Press().StartsWith("Camera focus in room 3")&&BuildCameraHandler.DescribeCamera(H,false).Contains("room 3"),"J and F3 identify the same newly created room");
   RoomLabelHandler.Clear(original);Time.frameCount++;
   Check(RoomLabelHandler.Name(original)=="room 1"&&RoomLabelHandler.Name(addedFront)=="room 3","clearing a name restores its original ordinal without renumbering newer rooms");
   Name(original,"Lounge");ZoneManager.Instance.Zones.Reverse();Time.frameCount++;
   Check(RoomLabelHandler.Name(addedFront)=="room 3"&&RoomSpatialReader.OrderedRooms(1,0)[0]==original,"rename and native enumeration changes cannot reorder rooms");
   Reset();string mainSave=NewSave(root,"main");var living=Room(10,10,16,14);var kitchen=Room(16,10,20,14);
   Name(living,"Living room");Name(kitchen,"Kitchen");H.HybridCamera.FreeCamera.LookTarget.position=P(12,12);
   Check(Press().StartsWith("Camera focus in Living room")&&Press().Contains("Shares right wall with Kitchen"),"J uses custom room and adjoining-room names");
   Check(BuildCameraHandler.DescribeCamera(H,false).Contains("Living room"),"F3 uses the same custom name");
   Check(PlacementLocationReader.Describe(new ItemObjectRoot{CubeTransform=new ItemCubeTransform{LocalPosition=P(12,12)}}).Contains("In Living room, middle centre"),"placement speech shares the player's room name and actual region");
   Check(RoomLabelHandler.Name(living,1,2)=="Living room","F7 area name uses label without a meaningless number");
   Name(kitchen,"Living room");Check(RoomLabelHandler.Name(living).Contains("room 1, floor layer 0")&&RoomLabelHandler.Name(kitchen).Contains("room 2, floor layer 0"),"duplicate labels are disambiguated");
   Name(kitchen,"Kitchen");Name(living," ");Check(RoomLabelHandler.CustomName(living)=="Living room","blank text cannot erase a name");
   RoomLabelHandler.Open(living);var firstPoint=living.Segments[0].Segment.SegmentPoints[0];living.Segments[0].Segment.SegmentPoints[0]=firstPoint+Vector3.up;
   // Height alone is not room identity; floor layer and horizontal contour are.
   Check(CoordinateEntryHandler.Valid(),"wall point elevation alone does not rename or invalidate the horizontal room");
   living.Segments[0].Segment.SegmentPoints[0]=firstPoint+new Vector3(1,0,0);Check(!CoordinateEntryHandler.Valid(),"changed room contour invalidates an open naming form even with the same ID");living.Segments[0].Segment.SegmentPoints[0]=firstPoint;
   RoomLabelHandler.Open(living);living.gameObject.activeInHierarchy=false;Check(!CoordinateEntryHandler.Valid(),"form invalidates destroyed room");living.gameObject.activeInHierarchy=true;
   RoomLabelHandler.Open(living);var oldSave=SavedGameManager.Instance.CurrentSavedGame;NewSave(root,"other");Check(!CoordinateEntryHandler.Valid(),"form cannot cross save identity");
   Check(RoomLabelHandler.CustomName(living)==null,"same native room IDs in another save cannot acquire names");
   SavedGameManager.Instance.CurrentSavedGame=oldSave;Time.frameCount++;Name(living,"Living room");Name(kitchen,"Kitchen");
   var request=new GameSavingRequest{CurrentLot=LotManager.Instance.Lot};SaveStep(request,GameSavingPhase.Init);
   Check(!File.Exists(Path.Combine(mainSave,RoomLabelFile.FileName)),"naming and save initiation do not write ahead of native assets");
   SaveStep(request,GameSavingPhase.SaveLotFetchData,()=>Fetch(request.CurrentLot));Name(living,"Later edit");
   Check(!File.Exists(Path.Combine(mainSave,RoomLabelFile.FileName)),"fetched snapshot remains memory-only during native writes");
   SaveStep(request,GameSavingPhase.ShowEndFloatingText);
   var disk=RoomLabelFile.Read(mainSave);Check(disk.Lot(1).Find(living.InstanceID).Text=="Living room","saved labels match lot-fetch snapshot, not a later edit");
   var detached=RoomLabelFile.Copy(disk);var special=detached.Lot(ulong.MaxValue);var specialRoom=Identity(999,"shape","900+");special.Rooms.Add(specialRoom);special.Set(specialRoom,"Caf\u00e9 \"study\" \\ nook");
   detached.Lot(1).Labels[0].History[0].Edges.Add("detached");
   Check(!disk.Lot(1).Labels[0].History[0].Edges.Contains("detached"),"save snapshots deep-copy nested contour history");
   string contractFolder=Path.Combine(root,"contract");Directory.CreateDirectory(contractFolder);RoomLabelFile.Write(contractFolder,detached);
   var roundTrip=RoomLabelFile.Read(contractFolder);
   Check(roundTrip.Lots.Count==2&&roundTrip.Lot(ulong.MaxValue).Find(999).Text==special.Find(999).Text,"actual JSON contract preserves multiple lots, full GUID precision, Unicode and escaped names");
   File.WriteAllText(Path.Combine(contractFolder,RoomLabelFile.FileName),"{\"Version\":1}");bool rejected=false;
   try{RoomLabelFile.Read(contractFolder);}catch(System.Runtime.Serialization.SerializationException){rejected=true;}
   Check(rejected&&File.ReadAllText(Path.Combine(contractFolder,RoomLabelFile.FileName))=="{\"Version\":1}","missing collections are rejected without treating truncated metadata as an empty save");
   GameLoadingManager.State=State.Loading;RoomLabelHandler.Tick();GameLoadingManager.State=State.Game;Time.frameCount++;
   Check(RoomLabelHandler.CustomName(living)=="Living room","reloading the same save object discards unsaved label edits");
   string copied=Path.Combine(root,"copy");Directory.CreateDirectory(copied);File.Copy(Path.Combine(mainSave,RoomLabelFile.FileName),Path.Combine(copied,RoomLabelFile.FileName));
   SavedGameManager.Instance.CurrentSavedGame=new AssetSavedGame{FilePath=copied};Time.frameCount++;
   Check(RoomLabelHandler.CustomName(living)=="Living room"&&RoomLabelHandler.CustomName(kitchen)=="Kitchen","native package-copy layout loads labels without depending on save GUID");
   RoomLabelHandler.Clear(living);Check(RoomLabelHandler.CustomName(living)==null,"clear removes the label and its recovery history");
   request=new GameSavingRequest{CurrentLot=LotManager.Instance.Lot};SaveStep(request,GameSavingPhase.Init);SaveStep(request,GameSavingPhase.SaveLotFetchData,()=>Fetch(request.CurrentLot));SaveStep(request,GameSavingPhase.CopySaveAsDefaultTown);
   Check(RoomLabelFile.Read(copied).Lot(1).Find(living.InstanceID)==null,"clear persists by atomic replacement before a native copy");
   SavedGameManager.Instance.CurrentSavedGame=new AssetSavedGame{FilePath=mainSave};Time.frameCount++;
   Check(RoomLabelHandler.CustomName(living)=="Living room","original save retains independent labels after editing a copy");
   Plugin.Log.ExpectedError=true;string broken=NewSave(root,"broken");File.WriteAllText(Path.Combine(broken,RoomLabelFile.FileName),"not json");Time.frameCount++;
   Check(RoomLabelHandler.CustomName(living)==null&&ScreenReader.Said.Last().Contains("could not be read or saved"),"corrupt sidecar produces explicit feedback");
   request=new GameSavingRequest{CurrentLot=LotManager.Instance.Lot};SaveStep(request,GameSavingPhase.Init);SaveStep(request,GameSavingPhase.ShowEndFloatingText);
   Check(File.ReadAllText(Path.Combine(broken,RoomLabelFile.FileName))=="not json","unreadable sidecar is never overwritten");
   string blocked=NewSave(root,"blocked");Name(living,"Unsaved name");Directory.CreateDirectory(Path.Combine(blocked,RoomLabelFile.FileName+".tmp"));
   request=new GameSavingRequest{CurrentLot=LotManager.Instance.Lot};SaveStep(request,GameSavingPhase.Init);SaveStep(request,GameSavingPhase.SaveLotFetchData,()=>Fetch(request.CurrentLot));SaveStep(request,GameSavingPhase.ShowEndFloatingText);
   Check(ScreenReader.Said.Last().Contains("could not be read or saved")&&RoomLabelHandler.CustomName(living)=="Unsaved name","write failure reports the problem and preserves in-memory names");
   var lot=new RoomLabelLot();var originalIdentity=Identity(10,"square","1+","2+","3+","4+");lot.Reconcile(new List<RoomLabelIdentity>{originalIdentity});lot.Set(originalIdentity,"Study");
   lot.Reconcile(new List<RoomLabelIdentity>{originalIdentity,Identity(20,"separate","8+")});Check(lot.Find(10)?.Text=="Study","unrelated room addition preserves name");
   var resize=Identity(10,"larger","1+","2+","30+","40+");lot.Reconcile(new List<RoomLabelIdentity>{resize,Identity(20,"separate","8+")});Check(lot.Find(10)?.Text=="Study","ordinary native room resize keeps its name");
   int historyCount=lot.Find(10).History.Count;
   for(int i=0;i<100;i++)lot.Reconcile(new List<RoomLabelIdentity>{Identity(10,null,"1+","2+","30+","40+"),Identity(20,"separate","8+")});
   Check(lot.Find(10).History.Count==historyCount,"unavailable contours cannot accumulate invented recovery history");
   var splitA=Identity(10,"halfA","1+","99+");var splitB=Identity(30,"halfB","2+","99-");lot.Reconcile(new List<RoomLabelIdentity>{splitA,splitB,Identity(20,"separate","8+")});
   Check(lot.Labels.Single().RoomId==-1,"split cannot give name to whichever result kept the ID");
   lot.Reconcile(new List<RoomLabelIdentity>{splitA,splitB,Identity(20,"separate","8+")});Check(lot.Labels.Single().RoomId==-1,"unassigned split name stays retained across repeated reads");
   lot.Reconcile(new List<RoomLabelIdentity>{Identity(100,"larger","10+"),Identity(20,"separate","8+")});Check(lot.Find(100)?.Text=="Study","undo restores exact historical contour with a rebuilt native ID");
   lot.Reconcile(new List<RoomLabelIdentity>{});Check(lot.Labels.Single().RoomId==-1,"room deletion retains the label for recovery");
   var target=Identity(200,"new room","200+");lot.Reconcile(new List<RoomLabelIdentity>{target});Check(lot.Reassign(lot.Labels.Single().Key,target)&&lot.Find(200).Text=="Study","retained label can be explicitly reassigned");
   lot.Reconcile(new List<RoomLabelIdentity>{originalIdentity});Check(lot.Find(10)==null,"explicit reassignment removes the old identity's automatic claim");
   lot=new RoomLabelLot();var a=Identity(1,"a","1+","2+");var b=Identity(2,"b","3+","4+");lot.Reconcile(new List<RoomLabelIdentity>{a,b});lot.Set(a,"A");lot.Set(b,"B");
   lot.Reconcile(new List<RoomLabelIdentity>{Identity(1,"merged","1+","3+")});Check(lot.Labels.All(l=>l.RoomId<0),"merge retains both names without selecting a winner");
   var cloned=RoomLabelFile.Copy(lot);Check(cloned.Labels.Count==2&&cloned.Labels.All(l=>l.RoomId<0),"unassigned names survive serialization");
   lot.Reconcile(new List<RoomLabelIdentity>{a,b});Check(lot.Find(1)?.Text=="A"&&lot.Find(2)?.Text=="B","undo of merge recovers both exact room contours");
   lot=new RoomLabelLot();a=Identity(1,"small","1+","2+");var big=Identity(1,"big","1+","3+");b=Identity(2,"b","3+");
   lot.Reconcile(new List<RoomLabelIdentity>{a});lot.Set(a,"Living room");lot.Reconcile(new List<RoomLabelIdentity>{big});
   lot.Reconcile(new List<RoomLabelIdentity>{a,b});
   Check(lot.Labels.Single().RoomId<0,"split remains ambiguous even when a half matches an older resize");
   lot=RoomLabelFile.Copy(lot);lot.Reconcile(new List<RoomLabelIdentity>{a,b});
   Check(lot.Labels.Single().RoomId<0,"ambiguous historical contour stays unassigned after serialization and repeated reads");
   lot.Reconcile(new List<RoomLabelIdentity>{big});Check(lot.Find(1)?.Text=="Living room","undo restores the pre-split contour rather than an ambiguous half");
   lot.Reconcile(new List<RoomLabelIdentity>{a});lot.Reconcile(new List<RoomLabelIdentity>{a,b});lot.Set(b,"Study");
   lot.Reconcile(new List<RoomLabelIdentity>{big});lot.Reconcile(new List<RoomLabelIdentity>{big});
   Check(lot.Labels.All(l=>l.RoomId<0),"merged shape from older history cannot select one of two names, including on a later read");
   Plugin.Log.ExpectedError=false;RoomLabelHandler.Enabled=false;return (checks-firstCheck)+" room-label checks passed; evidence "+root;
  }
 }
}
