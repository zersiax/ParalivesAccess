using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ParalivesAccess.Speech;
using Setting;

namespace ParalivesAccess.Handlers
{
    /// <summary>Speaks the current lot after the relationships screen's native Locate action.</summary>
    internal static class RelationshipLocationHandler
    {
        internal static bool ApplyPatches(Harmony harmony)
        {
            try
            {
                MethodInfo method = AccessTools.Method(typeof(UIRelationships), "OnClickFindParafolk");
                if (method == null) throw new MissingMethodException("UIRelationships.OnClickFindParafolk");
                harmony.Patch(method, postfix: new HarmonyMethod(typeof(RelationshipLocationHandler), nameof(Located)));
                bool applied = new List<MethodBase>(harmony.GetPatchedMethods()).Contains(method);
                Plugin.Log.LogInfo("[Locate] Patched UIRelationships.OnClickFindParafolk, applied=" + applied);
                return applied;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Relationship location hook failed: " + ex);
                return false;
            }
        }

        private static void Located(UIRelationships __instance)
        {
            try
            {
                AssetCharacter character = AssetManager.Instance?.GetCharacter(__instance.RelationshipWithCharacter);
                string message = Describe(character);
                Plugin.Log.LogInfo("[Locate] " + message);
                ScreenReader.Speak(message);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Reading the located Para's current lot failed: " + ex);
                ScreenReader.Speak(Loc.Get("relationship_location_error"));
            }
        }

        internal static string Describe(AssetCharacter character)
        {
            AssetCharacterData data = character?.Data;
            string name = ScreenReader.NormalizeForOutput(data?.FullName);
            if (string.IsNullOrWhiteSpace(name)) name = Loc.Get("state_unknown_para");
            if (data == null || ZoneManager.Instance == null)
                return Loc.Get("relationship_location_unknown", name);

            if (data.TownAutonomyDestinationState == TownAutonomyDestinationState.OutOfTown)
                return Loc.Get("relationship_location_out_of_town", name);

            LotPerimeterZoneObject lot;
            if (character.IsInImpostorLot && data.TownAutonomyDestinationState == TownAutonomyDestinationState.Idle)
            {
                // AutonomyManager.SetTownAutonomyWalkDone marks arrival inside an impostor
                // before setting Idle. Its target then identifies the occupied lot.
                lot = data.CurrentTownAutonomyTargetLot != 0
                    ? ZoneManager.Instance.GetLotPerimeterZoneObjectByGUID(data.CurrentTownAutonomyTargetLot) : null;
            }
            else
            {
                // Locate frames Data.Position. Query that same position with the native zone
                // API, avoiding stale NPC zone references and destination/last-visited fallbacks.
                ZoneManager.Instance.TryGetLotPerimeterZoneAtPosition(data.Position, out lot);
            }

            string location = ScreenReader.NormalizeForOutput(lot?.DisplayName);
            if (string.IsNullOrWhiteSpace(location))
                location = ScreenReader.NormalizeForOutput(lot?.UIWidget?.ToolTip?.TextToShow);
            return string.IsNullOrWhiteSpace(location)
                ? Loc.Get("relationship_location_unknown", name)
                : Loc.Get("relationship_location", name, location);
        }
    }
}
