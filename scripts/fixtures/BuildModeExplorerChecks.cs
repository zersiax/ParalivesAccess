namespace ParalivesAccess.Handlers
{
    // Compile the production open/retention gates. Native map and loaded-lot observations are
    // supplied explicitly: map zoom can remain true for several frames while F7 frames an area.
    public static class BuildModeExplorerChecks
    {
        private static ulong _lotGuid = 1;
        private static object _model = new object();
        private static AssetLot _loadedLot = new AssetLot();
        private static AssetLot GetLoadedLot(ulong guid) => _loadedLot;
        private static int closes;
        private static void Close(bool announce) { closes++; }
        private static void Speak(string text) => ScreenReader.Speak(text);
        EXPLORER_METHODS

        public static string Run()
        {
            int checks = 0;
            void Check(bool condition, string description)
            {
                if (!condition) throw new System.Exception(description);
                checks++;
            }
            var hybrid = new HybridPlayer { TownMap = true };
            ZoneManager.Instance=new ZoneManager();LotManager.Instance=new LotManager();
            Check(!IsTownMapBlockingExplorer(hybrid), "build-mode F7 can open at map zoom");
            for (int frame = 0; frame < 30; frame++)
                Check(IsContextStillValid(hybrid), "F7 stays open while the camera is still at map zoom");
            hybrid.TownMap = false;
            Check(IsContextStillValid(hybrid), "F7 stays open after framing an area leaves map zoom");
            hybrid.TownMap = true;
            hybrid.Player.State = GameStates.LiveMode;
            Check(IsTownMapBlockingExplorer(hybrid) && !IsContextStillValid(hybrid),
                "live-mode map still routes to the town explorer");
            hybrid.Player.IsInSelectHouseholdTutorial = true;
            Check(!IsTownMapBlockingExplorer(hybrid) && IsContextStillValid(hybrid),
                "existing household-selection tutorial exception is preserved");
            hybrid.Player.IsInSelectHouseholdTutorial = false;
            hybrid.Player.State = GameStates.BuildMode;
            hybrid.Player.TownMapForFastTravelMethod = 1;
            Check(IsTownMapBlockingExplorer(hybrid) && !IsContextStillValid(hybrid),
                "active fast-travel destination selection still blocks F7");
            hybrid.Player.TownMapForFastTravelMethod = 0;
            hybrid.Player.PlayerActionBlockedByUIWindow = true;
            Check(!IsContextStillValid(hybrid), "modal ownership still closes F7");
            hybrid.Player.PlayerActionBlockedByUIWindow = false;
            hybrid.Player.ItemInPlacement = new object();
            Check(!IsContextStillValid(hybrid), "placement ownership still closes F7");
            hybrid.Player.ItemInPlacement = null;
            _loadedLot = null;
            Check(!IsContextStillValid(hybrid), "unloaded build lot cannot retain an explorer page");
            _loadedLot = new AssetLot();
            hybrid.Player.State = GameStates.CharacterCreator;
            Check(!IsContextStillValid(hybrid), "paramaker is not a build-lot context");
            Check(!IsContextStillValid(null), "missing player cannot retain F7");
            hybrid.Player.State = GameStates.BuildMode;hybrid.TownMap=false;
            ModInputKey.Available=true;ModInputKey.PressedKey=Key.F6;
            Check(HandleBuildShortcut(hybrid,Keyboard.current)&&closes==1&&BuildCatalogHandler.Opens==1,
                "F6 switches directly from F7 to the visible catalogue");
            UI.Catalog.IsVisible=false;hybrid.TownMap=true;ScreenReader.Said.Clear();
            Check(HandleBuildShortcut(hybrid,Keyboard.current)&&closes==1&&BuildCatalogHandler.Opens==1&&ScreenReader.Said.Last().Contains("catalog_town_map"),
                "hidden catalogue explains framing route and keeps F7 open");
            ModInputKey.PressedKey=Key.F11;ScreenReader.Said.Clear();
            Check(HandleBuildShortcut(hybrid,Keyboard.current)&&closes==1&&ScreenReader.Said.Last().Contains("build_camera_coordinates"),
                "F11 speaks camera details inside F7 without closing it");
            ModInputKey.Available=false;ScreenReader.Said.Clear();
            Check(HandleBuildShortcut(hybrid,Keyboard.current)&&ScreenReader.Said.Last()=="binding conflict"&&closes==1,
                "explorer shortcuts respect native binding conflicts");
            hybrid.Player.State=GameStates.LiveMode;ModInputKey.Available=true;
            Check(!HandleBuildShortcut(hybrid,Keyboard.current),"live-mode F11 cannot announce build-camera details");
            ModInputKey.PressedKey=Key.None;
            return checks + " build/map explorer gate checks passed; native framing still requires gameplay verification.";
        }
    }
}
