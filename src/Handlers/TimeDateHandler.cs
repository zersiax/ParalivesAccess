using System;
using ParalivesAccess.Speech;
using Setting;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ParalivesAccess.Handlers
{
    /// <summary>Reports the simulation clock without changing the user's current activity.</summary>
    internal static class TimeDateHandler
    {
        internal static Key ReadKey { get; private set; } = Key.F2;

        internal static void ConfigureKey(Key key)
        {
            ReadKey = key;
            if (key == Key.None) return;
            if (key < Key.F1 || key > Key.F12
                || key == Key.F3 || key == Key.F10 || key == Key.F11
                || key == BuildCatalogHandler.OpenKey || key == LotObjectHandler.OpenKey
                || key == ContextActionHandler.ActionKey || key == VirtualAudioListenerHandler.ToggleKey
                || key == ParaSwitchHandler.SwitchKey || key == WallConstructionHandler.RectangleKey
                || key == LotDescriptionHandler.DescribeKey)
            {
                ReadKey = Key.None;
                Plugin.Log.LogError("ReadTimeDateKey must be a function key unused by other accessibility commands; time/date shortcut disabled.");
            }
        }

        internal static void Tick()
        {
            if (!ContextKeyInput.TakeTimeDateRequest() || ReadKey == Key.None
                || !Application.isFocused || SettingsHandler.IsCapturingBindingInput) return;
            try
            {
                HybridPlayer hybrid = PlayerManager.Instance?.HybridPlayer1;
                string report;
                if (GameLoadingManager.State != State.Game
                    || SavedGameManager.Instance?.HasLoadedSavedGame != true || hybrid?.Player == null)
                    report = Loc.Get("time_date_unavailable");
                else report = ModInputKey.IsAvailable(hybrid, ReadKey, out string conflict)
                    ? Describe() ?? Loc.Get("time_date_unavailable") : conflict;

                Plugin.Log.LogInfo("[TimeDate] " + report);
                // Deliberately repeat identical readings while paused. This observer never
                // changes focus, selection, windows, simulation speed or placement ownership.
                ScreenReader.Speak(report);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Reading in-game time and date threw: " + ex);
                ScreenReader.Speak(Loc.Get("time_date_unavailable"));
            }
        }

        internal static string Describe()
        {
            // One snapshot keeps time and date coherent across a midnight boundary. Use the
            // same native formatters as UITime and its full-date tooltip, even when hidden.
            double minutes = ParaTime.TotalMinutes;
            if (double.IsNaN(minutes) || double.IsInfinity(minutes) || minutes < 0d) return null;
            GeneralOptions options = Settings.Get<GeneralOptions>();
            if (options == null) return null;
            string time = ScreenReader.NormalizeForOutput(
                FormatService.GetTimeOfDay(minutes % 1440d, options.Hour24Mode));
            string date = ScreenReader.NormalizeForOutput(
                FormatService.GetDate(minutes, includeDayAndMonth: true, includeYear: true));
            return string.IsNullOrWhiteSpace(time) || string.IsNullOrWhiteSpace(date)
                ? null : Loc.Get("time_date_report", time, date);
        }
    }
}
