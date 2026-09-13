using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using ParalivesAccess.Speech;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ParalivesAccess.Handlers
{
    /// <summary>
    /// Announces screen changes, and reports the current context on demand.
    /// </summary>
    /// <remarks>
    /// WHY: through TESTS 9-18 the mod could say what element had focus but never what screen the
    /// user was on. User feedback after TEST 18: "I went through most character panels but I wasn't
    /// getting the impression we were getting anywhere; I don't know if we properly confirmed our
    /// choices, and what the game wants us to do next." Element-level speech alone does not orient
    /// anyone - a sighted player reads the screen's title and layout continuously and for free.
    ///
    /// Two additions:
    ///  - Screen changes are spoken as they happen, hooked on UIManager.RegisterActiveWindow, the
    ///    game's own record of a window becoming active (game-api.md section 4).
    ///  - F3 reports the current context on demand, without waiting for something to change. F3 is
    ///    verified unbound (game-api.md section 3). Its press is captured before game systems run,
    ///    so a transition cannot consume the evidence before this handler sees it.
    ///
    /// Window names are developer type names (UICharacterOutfits). They are cleaned into readable
    /// words ("Character Outfits") but NOT renamed: inventing friendlier names would mean guessing
    /// at what each screen contains, and a wrong name is worse than a clumsy one. Screens whose
    /// spoken name reads badly should be mapped through Loc from real listening.
    /// </remarks>
    public static class WindowHandler
    {
        /// <summary>True to announce screen changes as they happen.</summary>
        public static bool AnnounceWindows { get; set; } = true;

        private static FieldInfo _focusablesField;
        private static bool _reflectionResolved;

        private sealed class PendingWindow
        {
            public UIWindow Window;
            public int FramesRemaining;
        }

        private sealed class PendingPrompt
        {
            public UIPrompt Prompt;
            public UIPromptData[] Buttons;
            public int FramesRemaining;
        }

        private static readonly List<PendingWindow> _pendingInformationalWindows =
            new List<PendingWindow>();
        private static PendingPrompt _pendingPrompt;

        /// <summary>Last observed <c>player.ShowUI</c>; null until the first frame with a player.</summary>
        private static bool? _lastShowUi;

        /// <summary>An F3 press captured in the pre-system input phase and awaiting a response.</summary>
        private static bool _contextRequested;

        /// <summary>The frame on which the current context request was captured, for diagnostics.</summary>
        private static int _contextRequestFrame = -1;

        /// <summary>A Shift+F3 request to repeat the newest important gameplay event.</summary>
        private static bool _importantEventRequested;

        /// <summary>The frame on which the current important-event request was captured.</summary>
        private static int _importantEventRequestFrame = -1;

        /// <summary>The last physical Escape edge, used only to attribute a matching window close.</summary>
        private static int _escapePressedFrame = -1;

        public struct EscapeCloseState
        {
            public bool Announce;
            public int Frame;
            public string WindowName;
        }

        /// <summary>Applies the screen-change patch.</summary>
        /// <param name="harmony">Harmony instance owned by the plugin.</param>
        /// <returns>True when the patch was verified as applied.</returns>
        public static bool ApplyPatches(Harmony harmony)
        {
            try
            {
                MethodInfo target = AccessTools.Method(typeof(UIManager), "RegisterActiveWindow");
                if (target == null)
                {
                    Plugin.Log.LogError(
                        "Could not find UIManager.RegisterActiveWindow. Screen changes will not be "
                      + "announced - re-check docs/game-api.md section 4.");
                    return false;
                }

                harmony.Patch(
                    target,
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(WindowHandler), nameof(RegisterActiveWindowPostfix))));

                bool applied = new List<MethodBase>(harmony.GetPatchedMethods()).Contains(target);
                Plugin.Log.LogInfo($"Patched UIManager.RegisterActiveWindow, applied={applied}.");

                MethodInfo unregister = AccessTools.Method(
                    typeof(UIManager), "UnregisterActiveWindow", new[] { typeof(UIWindow) });
                if (unregister == null)
                {
                    Plugin.Log.LogError(
                        "Could not find UIManager.UnregisterActiveWindow. Escape-triggered window "
                      + "closures will remain unannounced.");
                }
                else
                {
                    harmony.Patch(
                        unregister,
                        prefix: new HarmonyMethod(AccessTools.Method(
                            typeof(WindowHandler), nameof(UnregisterActiveWindowPrefix))),
                        postfix: new HarmonyMethod(AccessTools.Method(
                            typeof(WindowHandler), nameof(UnregisterActiveWindowPostfix))));
                    Plugin.Log.LogInfo(
                        "Patched UIManager.UnregisterActiveWindow, applied="
                      + $"{new List<MethodBase>(harmony.GetPatchedMethods()).Contains(unregister)}.");
                }

                // A modal that explains why an action was refused is the one window whose CONTENT
                // must be read, not just its name. See PromptPostfix.
                MethodInfo prompt = AccessTools.Method(
                    typeof(UIPrompt),
                    "Show",
                    new[] { typeof(string), typeof(string), typeof(UIPromptData[]) });

                if (prompt == null)
                {
                    Plugin.Log.LogError(
                        "Could not find UIPrompt.Show(string, string, UIPromptData[]). Prompts will "
                      + "announce their name but not their message, so a refused action stays "
                      + "unexplained - re-check docs/game-api.md.");
                    return applied;
                }

                harmony.Patch(
                    prompt,
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(WindowHandler), nameof(PromptPostfix))));

                Plugin.Log.LogInfo(
                    "Patched UIPrompt.Show, applied="
                  + $"{new List<MethodBase>(harmony.GetPatchedMethods()).Contains(prompt)}.");

                // The THIRD announcement channel, and the one that carries refusals the mod was
                // silently dropping. See FloatingTextPostfix.
                MethodInfo floating = AccessTools.Method(
                    typeof(UIFloatingText),
                    "Show",
                    new[] { typeof(ulong), typeof(string), typeof(Vector3) });

                if (floating == null)
                {
                    Plugin.Log.LogError(
                        "Could not find the private UIFloatingText.Show(ulong, string, Vector3). "
                      + "On-screen feedback such as 'no more points' will stay unspoken.");
                    return applied;
                }

                harmony.Patch(
                    floating,
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(WindowHandler), nameof(FloatingTextPostfix))));

                Plugin.Log.LogInfo(
                    "Patched UIFloatingText.Show, applied="
                  + $"{new List<MethodBase>(harmony.GetPatchedMethods()).Contains(floating)}.");

                // Undo and redo change game state with no feedback of any kind. See UndoRedoPostfix.
                MethodInfo undo = AccessTools.Method(typeof(UndoRedoManager), "UndoRedoActions");
                if (undo == null)
                {
                    Plugin.Log.LogError(
                        "Could not find UndoRedoManager.UndoRedoActions. Undo and redo will stay "
                      + "silent.");
                    return applied;
                }

                harmony.Patch(
                    undo,
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(WindowHandler), nameof(UndoRedoPostfix))));

                Plugin.Log.LogInfo(
                    "Patched UndoRedoManager.UndoRedoActions, applied="
                  + $"{new List<MethodBase>(harmony.GetPatchedMethods()).Contains(undo)}.");

                return applied;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Patching UIManager.RegisterActiveWindow threw: " + ex);
                return false;
            }
        }

        /// <summary>
        /// Harmony postfix on <c>UIPrompt.Show</c>. Speaks the prompt's title, message and choices.
        /// </summary>
        /// <param name="__instance">The prompt being shown.</param>
        /// <param name="buttons">The choices it was built with.</param>
        /// <remarks>
        /// THE WORST FAILURE THE MOD CURRENTLY HAS, and it is silent by construction.
        ///
        /// UICharacterCreator.CheckHouseholdValidity raises a prompt and then RETURNS FALSE, so
        /// ValidateAndExitCharacterCreator does nothing at all. Pressing Confirm with a character
        /// missing a name therefore produced no speech, no movement and no error - indistinguishable
        /// from a broken mod, and the user has no way to discover the reason. Three of the four
        /// completion rules end this way (missing name, unspent personality points, missing required
        /// life stage).
        ///
        /// The window announcement already names the prompt, but a name alone is useless here: every
        /// one of those refusals opens the same UIPrompt and differs ONLY in its message.
        ///
        /// Read from the resolved TMP_Text rather than the key arguments. TranslatedText.Refresh
        /// writes the final localized, parameter-substituted string into Text.text, and callers are
        /// inconsistent about what they pass - CheckHouseholdValidity hands Show an ALREADY
        /// translated message, while ShowErrorOk hands it the raw key "UIPrompt_Error" as a title.
        /// Reading the rendered text is the only thing true for both. ScreenReader.Speak strips the
        /// TMP rich-text markup Refresh injects.
        ///
        /// TEST 44 corrected the timing assumption: this postfix is on the inner Show call, while
        /// callers such as UILotInfo.BuyClicked call AddTitleParameters/AddMessageParameters only
        /// after ShowYesNo returns. Speaking here produced literal `{0}`. The postfix now snapshots
        /// the choices and schedules one settled read from Tick; RegisterActiveWindow deliberately
        /// leaves UIPrompt to this channel so the final prompt is not announced twice.
        ///
        /// The button labels come from the UIPromptData argument, which carries the translation key
        /// the game itself used - so "OK", "Yes"/"No" and "Save and quit" are named from the game's
        /// own vocabulary rather than from the mod's guess at what a prompt offers.
        /// </remarks>
        public static void PromptPostfix(UIPrompt __instance, UIPromptData[] buttons)
        {
            if (!AnnounceWindows || __instance == null)
            {
                return;
            }

            // Runs inside the game's call stack; an escaping exception would land in game code.
            try
            {
                // Buttons are created before UIPrompt.Show registers the window, and the outer Show
                // call has now made its content active. Move the shared cursor now so arrows and
                // Enter cannot continue operating the panel behind this modal while its text settles.
                MenuNavigationHandler.FocusForegroundWindow(__instance);

                _pendingPrompt = new PendingPrompt
                {
                    Prompt = __instance,
                    Buttons = buttons == null ? null : (UIPromptData[])buttons.Clone(),
                    FramesRemaining = InformationalSettleFrames
                };
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("WindowHandler.PromptPostfix threw: " + ex);
            }
        }

        /// <summary>
        /// Harmony postfix on the private <c>UIFloatingText.Show</c>. Speaks transient on-screen text.
        /// </summary>
        /// <param name="text">The message, already localized by the caller.</param>
        /// <remarks>
        /// A THIRD announcement channel, distinct from window changes and from `UIPrompt`, and the
        /// one that carries refusals.
        ///
        /// TEST 33 measured the gap. Pressing '=' on a stat with no budget left produced
        /// `[Adjust] +1 -> Creativity, 0 of 3, 0 Points Left` **twice** - two identical utterances,
        /// indistinguishable from a key that did not register. The game DID explain itself:
        /// `UIProfileTraitStatsItemJauge.OnClickJauge:104` calls
        /// `UI.Get&lt;UIFloatingText&gt;(0).ShowAtCenterOfScreen(..., "UIProfileError_NoMorePoints")`.
        /// The mod simply had no reader for that channel.
        ///
        /// **This corrects a claim in the v0.22.0 plan**, which asserted the refusal was a `UIPrompt`
        /// that Phase 0 already spoke. It never was.
        ///
        /// Patched on the PRIVATE Show(ulong, string, Vector3) rather than the three public entry
        /// points, because `ShowAtWorldPosition` and `ShowAtScreenPosition` both delegate to
        /// `ShowAtCenterOfScreen`, which delegates here - so one patch covers every caller and
        /// cannot double-announce.
        ///
        /// The `text` argument is used rather than the rendered item: callers pass an
        /// already-translated string, and reading the argument avoids depending on when the item's
        /// own label is populated.
        /// </remarks>
        public static void FloatingTextPostfix(string text)
        {
            if (!AnnounceWindows || string.IsNullOrEmpty(text))
            {
                return;
            }

            // Runs inside the game's call stack; an escaping exception would land in game code.
            try
            {
                Plugin.Log.LogInfo("[Floating] " + text);
                ScreenReader.Speak(text);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("WindowHandler.FloatingTextPostfix threw: " + ex);
            }
        }

        /// <summary>
        /// Harmony postfix on <c>UndoRedoManager.UndoRedoActions</c>. Announces that state changed.
        /// </summary>
        /// <param name="undo">True for an undo, false for a redo.</param>
        /// <remarks>
        /// Reported from play: *"if ctrl+z did anything I wasn't made aware."* Correct — the game
        /// gives undo no feedback at all: no prompt, no floating text, no notification. A sighted
        /// player sees the UI change; a blind one gets nothing, which makes the whole feature
        /// unusable and, worse, makes an accidental Ctrl+Z silently destructive.
        ///
        /// It announces only THAT something was undone, deliberately. `UndoRedoAction` carries
        /// `ActionID`, `PlayerIndex`, `MoneyChange` and `GameFrame` and **no description of any
        /// kind**, so naming what changed would mean the mod maintaining its own table of ~40 action
        /// subclasses and guessing at wording for each. Announcing the fact is true; announcing a
        /// guessed description would be exactly the plausible-but-wrong failure a blind user cannot
        /// detect.
        /// </remarks>
        public static void UndoRedoPostfix(bool undo)
        {
            if (!AnnounceWindows)
            {
                return;
            }

            // Runs inside the game's call stack; an escaping exception would land in game code.
            try
            {
                string message = Loc.Get(undo ? "undo_done" : "redo_done");
                Plugin.Log.LogInfo("[UndoRedo] " + message);
                ScreenReader.Speak(message);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("WindowHandler.UndoRedoPostfix threw: " + ex);
            }
        }

        /// <summary>
        /// Trims a trailing sentence terminator so joining two fragments cannot double it.
        /// </summary>
        /// <remarks>
        /// The Loc templates add ". " between parts, but the game's own strings often already end in
        /// a full stop. TEST 34 measured the result:
        /// "Error. Blah h is missing one or more personality stats.. Choices: Ok".
        ///
        /// Trimming here rather than changing the template keeps the separator in the localisable
        /// string, where a translator can change it.
        /// </remarks>
        private static string Sentence(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            return text.TrimEnd('.', ' ', '。');
        }

        /// <summary>Reads the rendered text out of a TranslatedText, or null when it has none.</summary>
        private static string ReadTranslatedText(TranslatedText label)
        {
            if (label == null || label.Text == null)
            {
                return null;
            }

            string text = label.Text.text;
            return string.IsNullOrEmpty(text) ? null : text.Trim();
        }

        /// <summary>Names the prompt's buttons, using the translation keys the game passed in.</summary>
        private static string DescribeChoices(UIPromptData[] buttons)
        {
            if (buttons == null || buttons.Length == 0)
            {
                return null;
            }

            var parts = new List<string>();
            foreach (UIPromptData button in buttons)
            {
                if (button == null || string.IsNullOrEmpty(button.ButtonText))
                {
                    continue;
                }

                string text = TranslationManager.Get(button.ButtonText);

                // A missing translation echoes the key back; the key is still better than silence
                // about what the choices are, but the resolved form is preferred.
                parts.Add(string.IsNullOrEmpty(text) ? button.ButtonText : text.Trim());
            }

            return parts.Count == 0 ? null : string.Join(", ", parts.ToArray());
        }

        /// <summary>Snapshots the current window when a recent physical Escape is closing it.</summary>
        public static void UnregisterActiveWindowPrefix(
            UIManager __instance, UIWindow window, out EscapeCloseState __state)
        {
            __state = default(EscapeCloseState);
            try
            {
                TownMapLotHandler.NotifyInteractionMenuClosing(window as UIInteractions);

                int age = Time.frameCount - _escapePressedFrame;
                if (!AnnounceWindows || __instance == null || window == null
                    || age < 0 || age > 1
                    || !ReferenceEquals(__instance.CurrentWindow, window))
                {
                    return;
                }

                __state.Announce = true;
                __state.Frame = _escapePressedFrame;
                __state.WindowName = DescribeWindowName(window);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("WindowHandler.UnregisterActiveWindowPrefix threw: " + ex);
            }
        }

        /// <summary>
        /// Reports the concrete result of Escape only when it removed the manager's current window.
        /// </summary>
        public static void UnregisterActiveWindowPostfix(EscapeCloseState __state)
        {
            if (!__state.Announce)
            {
                return;
            }

            try
            {
                _escapePressedFrame = -1;
                string message = Loc.Get("window_closed", __state.WindowName);
                Plugin.Log.LogInfo(
                    $"[Window] Escape from frame {__state.Frame} -> {message}");
                ScreenReader.Speak(message);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("WindowHandler.UnregisterActiveWindowPostfix threw: " + ex);
            }
        }

        /// <summary>Harmony postfix on <c>UIManager.RegisterActiveWindow</c>.</summary>
        /// <param name="window">The window that just became active.</param>
        /// <remarks>
        /// Postfix, not prefix: the window must already be in the manager's lists before its size
        /// is read.
        /// </remarks>
        public static void RegisterActiveWindowPostfix(UIWindow window)
        {
            if (!AnnounceWindows || window == null)
            {
                return;
            }

            // Runs inside the game's call stack; an escaping exception would land in game code.
            try
            {
                // UIFloatingText is a pooled transport for transient messages, not a second
                // informational window. Its private Show postfix receives and speaks the current,
                // authoritative text exactly once; reading the pooled hierarchy three frames later
                // duplicates that message and can include stale items from earlier notifications.
                if (window is UIFloatingText || window is UIPrompt)
                {
                    return;
                }

                // Save rows already expose a complete, package-backed label (slot, household,
                // play time and timestamp). Their delayed generic tooltip repeats only the
                // secondary "Stats / Last played" copy and registers as a separate zero-control
                // window, so it interrupts the row that was just focused. Scope this to the
                // the active load-game screen rather than suppressing UITooltipGenericSimple
                // globally: generic tooltips on other screens can still contain otherwise
                // unreachable help. The opener-parent route is preferred, but TEST 62 showed the
                // serialized Stats opener is not retained on this singleton at registration time;
                // UILoadGame's own visible state is therefore the reliable ownership boundary.
                var genericTooltip = window as UITooltipGenericSimple;
                bool loadGameTooltip = genericTooltip?.TooltipOpener != null
                    && genericTooltip.TooltipOpener.GetComponentInParent<UILoadGame>() != null;
                if (!loadGameTooltip && genericTooltip != null && UI.Instance != null)
                {
                    UILoadGame loadGame = UI.GetOrNull<UILoadGame>(genericTooltip.PlayerOwnerIndex);
                    loadGameTooltip = loadGame != null && loadGame.IsVisible;
                }
                if (loadGameTooltip)
                {
                    Plugin.Log.LogInfo(
                        "[Window] Save-row generic tooltip is redundant with its semantic row; "
                      + "registration text not announced.");
                    return;
                }

                // The F6 catalogue browser already owns the exact typed row and includes the
                // authored product name, localized catalogue name, price and affordability. The
                // hover tooltip can register for a stale pointer target after keyboard focus has
                // moved (TEST 67 announced Pika/Gertrude over an Abi row), so it is neither useful
                // nor authoritative while that browser owns input. Outside F6 it remains audible.
                if (window is UITooltipBuildModeCatalogItem
                    && BuildCatalogHandler.OwnsInput)
                {
                    Plugin.Log.LogInfo(
                        "[Window] Build-catalogue tooltip is owned by the semantic F6 row; "
                      + "registration text not announced.");
                    return;
                }

                // This persistent HUD rewrites a pooled UIList every frame from the selected
                // characters' live queues. Registration is not a queue-content event: the v0.35.0
                // tutorial run captured its prefab's "Lorem ipsum" placeholder before Update had
                // supplied real rows. A model-backed lifecycle observer will own queue changes;
                // never scrape the prefab at window-registration time.
                if (window is UIInteractionQueue)
                {
                    Plugin.Log.LogInfo(
                        "[Window] Interaction Queue is a live HUD; registration text not announced.");
                    return;
                }

                // UI.Get creates this singleton before Show supplies its target and rebuilds its
                // pooled rows. TEST 48 heard the prefab's stale "8 items" during that gap, while
                // TEST 55 showed that leaving the registration silent also left focus on the
                // household behind the now-open menu. Settle first, then announce and focus the
                // target-bound rows through the ordinary pending-window path.
                if (window is UIInteractions)
                {
                    Plugin.Log.LogInfo(
                        "[Window] Interactions registered before target binding; awaiting settled rows.");
                    ScheduleInformationalWindow(window);
                    return;
                }

                // Registration is never a notification-content event. The singleton retains pooled
                // entries across saves and can re-register after State.Game, which TEST 48 measured
                // four times as old skill-up/departure announcements inside a new tutorial. Live
                // notifications arrive through their transient message route instead.
                if (window is UINotifications)
                {
                    Plugin.Log.LogInfo(
                        "[Window] Notifications is a retained live HUD; registration text not announced.");
                    return;
                }

                // These are short-lived world indicators, not a navigable window. Their inherited
                // UIListItemBase click is inert; StateReportingHandler announces the known,
                // model-backed request event when it is actually visible instead.
                if (window is UIThoughtBubbles)
                {
                    Plugin.Log.LogInfo(
                        "[Window] Thought Bubbles is a transient indicator HUD; registration text not announced.");
                    return;
                }

                // These windows are persistent live-HUD structure. Conversation-card mode
                // temporarily hides and re-registers them after every resolved card; TEST 66
                // measured those redundant registrations interrupting the typed outcome burst.
                // TEST 75 measured the same problem from UIGameBar during the very first camera
                // lesson and again after choosing the conductor. The bar's controls remain fully
                // reachable through panel navigation; registering the always-present HUD is not a
                // new screen or state event.
                // Their explicit tab/row focus remains fully readable, so registration itself
                // carries no state change and must stay silent.
                if (window is UICharacterSubMenuBar || window is UIThoughts
                    || window is UIGameBar)
                {
                    Plugin.Log.LogInfo(
                        $"[Window] {window.GetType().Name} is persistent live HUD; "
                      + "registration text not announced.");
                    return;
                }

                // UICharacters re-registers when the native Together-card composition replaces
                // the ordinary household layout. ContextActionHandler already owns that boundary
                // with one coalesced Conversation choices/target announcement. Saying Household a
                // few frames later both misnames the active composition and cuts its first topic.
                UICharacters characters = window as UICharacters;
                if (characters != null
                    && (characters.IsTogetherCardsMode || characters.IsShowingOutcomes))
                {
                    Plugin.Log.LogInfo(
                        "[Window] UICharacters is in Together-card/outcome mode; "
                      + "the semantic conversation panel owns its announcement.");
                    return;
                }

                // A qualified job offer always opens this exact second-stage window. Its two
                // schedule controls are raw TMP dropdowns outside FocusableGroup navigation, so
                // OccupationHandler presents one coherent accessible panel after the same bounded
                // settle used here. Let that owner make the single opening announcement.
                if (OccupationHandler.ManagesWindow(window))
                {
                    Plugin.Log.LogInfo(
                        "[Window] Work schedule is owned by the occupation accessibility flow.");
                    return;
                }

                if (TutorialHandler.ManagesWindow(window))
                {
                    Plugin.Log.LogInfo(
                        $"[Window] {window.GetType().Name} is owned by the tutorial state reader.");
                    return;
                }

                // Registration precedes row binding on more than just informational windows.
                // TEST 55 measured UIOccupations claiming 13 pooled items before settling to one
                // real Close control. Defer every ordinary window by the same three-frame boundary;
                // this is short enough to precede human input and makes both counts and focus refer
                // to the final visible state.
                ScheduleInformationalWindow(window);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("WindowHandler.RegisterActiveWindowPostfix threw: " + ex);
            }
        }

        /// <summary>
        /// Frames to wait before reading a focusable-less window's text.
        /// </summary>
        /// <remarks>
        /// `RegisterActiveWindow` fires when the window registers, which is before its content is
        /// written - the same settle every handler here needs for the reason in game-api.md
        /// section 32. Reading immediately would see an empty tutorial and suppress it as noise.
        /// </remarks>
        private const int InformationalSettleFrames = 3;

        /// <summary>
        /// Reads a window that has nothing to navigate, or drops it if it has nothing to say.
        /// </summary>
        /// <remarks>
        /// TWO PROBLEMS, ONE RULE.
        ///
        /// The game's tutorials - `UIGoHereTutorial`, `UIClickOnFoodToEatItTutorial`,
        /// `UIMultiselectTutorial`, `UICameraTutorial`, `UIPickStorytellerTutorial`,
        /// `UITogetherCardsClickTutorial` - are all plain `UIWindow`s carrying **no focusables**.
        /// Their instruction lives in prefab text, so the mod announced "Go Here Tutorial, 0 items"
        /// and never read the sentence saying what the game wants. That is the same shape as the
        /// `UIPrompt` gap: window NAMES were being read, window CONTENT was not.
        ///
        /// Meanwhile TEST 21 recorded the opposite complaint - `Transition Fade`, `Loading Screen`
        /// and `Notifications` announcing themselves with 0 items as pure noise - and noted a 0-item
        /// window as "a safe suppression candidate".
        ///
        /// Both are answered by asking what the window actually says. Text present: read it, because
        /// there is no other way to reach it. Text absent: stay silent, because the name alone told
        /// the user nothing anyway.
        /// </remarks>
        private static void ScheduleInformationalWindow(UIWindow window)
        {
            foreach (PendingWindow pending in _pendingInformationalWindows)
            {
                if (ReferenceEquals(pending.Window, window))
                {
                    pending.FramesRemaining = InformationalSettleFrames;
                    return;
                }
            }

            _pendingInformationalWindows.Add(new PendingWindow
            {
                Window = window,
                FramesRemaining = InformationalSettleFrames
            });
        }

        /// <summary>True while native loading or a scene fade covers the interactive UI.</summary>
        internal static bool IsSceneTransitionActive
        {
            get
            {
                // A real prompt above a loading screen still requires an answer. Follow native
                // foreground ownership instead of allowing the background transition to mask it.
                UIWindow common = HybridReferences.Instance?.UIManagerCommon?.CurrentWindow;
                UIWindow local = PlayerManager.Instance?.HybridPlayer1?.UIManager?.CurrentWindow;
                if ((common is UIPrompt && common.IsVisible)
                    || (local is UIPrompt && local.IsVisible))
                    return false;
                if (GameLoadingManager.State == State.Loading)
                    return true;
                if (UI.Instance == null)
                    return false;
                return UI.GetOrNull<UITransitionFade>(-1)?.IsVisible == true
                    || UI.GetOrNull<UITransitionFade>(0)?.IsVisible == true
                    || UI.GetOrNull<UILoadingScreen>(-1)?.IsVisible == true
                    || UI.GetOrNull<UILoadingScreen>(0)?.IsVisible == true;
            }
        }

        private static bool IsOwnedCreatorOutfits(UIWindow window)
        {
            if (!(window is UICharacterOutfits) || UI.Instance == null)
                return false;
            UICharacterCreator creator = UI.GetOrNull<UICharacterCreator>(window.PlayerOwnerIndex);
            return creator != null && creator.IsVisible
                && ReferenceEquals(creator.UICharacterOutfits, window);
        }

        private static void ReadPendingInformationals()
        {
            if (_pendingInformationalWindows.Count == 0)
            {
                return;
            }

            var ready = new List<UIWindow>();
            for (int i = _pendingInformationalWindows.Count - 1; i >= 0; i--)
            {
                PendingWindow pending = _pendingInformationalWindows[i];
                if (pending.Window == null)
                {
                    _pendingInformationalWindows.RemoveAt(i);
                    continue;
                }

                if (pending.FramesRemaining-- > 0)
                {
                    continue;
                }

                // Iterating backwards makes removal safe. Insert at the front to preserve the
                // windows' registration order when several become ready on the same frame.
                ready.Insert(0, pending.Window);
                _pendingInformationalWindows.RemoveAt(i);
            }

            bool interrupt = true;
            foreach (UIWindow window in ready)
            {
                // Tab switches also call Outfits.Show; ownership lasts for the creator session.
                if (IsOwnedCreatorOutfits(window))
                {
                    Plugin.Log.LogInfo(
                        "[Window] Creator-owned Outfits registration kept current creator focus.");
                    continue;
                }

                string message = DescribeSettledWindow(window);
                if (string.IsNullOrEmpty(message))
                {
                    continue;
                }

                ScreenReader.Speak(message, interrupt);
                interrupt = false;
            }
        }

        private static string DescribeSettledWindow(UIWindow window)
        {
            if (window == null || !window.IsVisible)
            {
                return null;
            }

            // Re-check delayed windows at delivery time. The mode can change during the three-frame
            // settlement window after registration.
            if (window is UICharacterSubMenuBar || window is UIThoughts
                || window is UIGameBar)
            {
                return null;
            }
            UICharacters conversationCharacters = window as UICharacters;
            if (conversationCharacters != null
                && (conversationCharacters.IsTogetherCardsMode
                    || conversationCharacters.IsShowingOutcomes
                    || conversationCharacters.PlayerOwner?.Player?.IsInIntro == true))
            {
                return null;
            }
            if (OccupationHandler.ManagesWindow(window))
            {
                return null;
            }

            // External game API on a window that may have been hidden again in the meantime.
            try
            {
                string name = DescribeWindowName(window);

                // A window can register before its focusables do. Once it has settled, treat it as
                // the ordinary navigable window it became instead of scraping every descendant as
                // informational copy. TEST 44's Build Mode debris was exactly this race.
                int count = CountFocusables(window);
                // UICharacterCreator is a composite screen whose active Outfits child is the
                // native CurrentWindow. It deliberately carries none of the generic modal flags,
                // so merely suppressing the sibling Outfits registration did not claim input:
                // TEST 75 still opened on Casual and the first Up remained native-owned. A fresh
                // creator registration is the exact structural boundary at which its selected
                // top-level tab must be reseeded and made authoritative.
                if (window is UICharacterCreator)
                {
                    MenuNavigationHandler.FocusForegroundWindow(window, forceReseed: true);
                }
                else if (ShouldTakeForegroundFocus(window))
                {
                    MenuNavigationHandler.FocusForegroundWindow(window);
                }

                // UIInteractionsList is a structural, pooled list panel. Its useful target/header
                // context is no longer a navigation stop, so present that context together with the
                // real row that foreground focus selected. This is one state announcement, not a
                // tutorial instruction or an enumeration of the whole menu.
                if (window is UIInteractions interactions)
                {
                    string interactionSummary = DescribeInteractionWindow(interactions, name, count);
                    Plugin.Log.LogInfo(
                        $"[Window] {interactionSummary}  (type={window.GetType().Name})");
                    return interactionSummary;
                }

                // Foreground seeding is intentionally silent because the window announcement owns
                // this boundary. For Load Game, the seeded row is essential identity rather than
                // redundant focus chatter, so include it in that same single announcement.
                if (window is UILoadGame loadGame)
                {
                    string navigable = count == 1
                        ? Loc.Get("window_opened_one", name)
                        : Loc.Get("window_opened", name, count);
                    Focusable focused = ReferenceEquals(
                        MenuNavigationHandler.CursorGroup, loadGame)
                            ? MenuNavigationHandler.Cursor
                            : loadGame.GetFocused(false);
                    string saveLabel;
                    string summary = focused != null
                        && SaveGameListHandler.TryGetLabel(focused, out saveLabel)
                        && !string.IsNullOrWhiteSpace(saveLabel)
                            ? Loc.Get("panel_with_focus", navigable, saveLabel)
                            : navigable;
                    Plugin.Log.LogInfo(
                        $"[Window] {summary}  (type={window.GetType().Name})");
                    return summary;
                }

                // The user's requested landing point is the creator's main category row, not the
                // simultaneously opened Outfits content. Naming that one seeded tab in the opening
                // utterance makes the orientation usable without separately speaking focus and
                // interrupting the window announcement.
                if (window is UICharacterCreator creator)
                {
                    string navigable = count == 1
                        ? Loc.Get("window_opened_one", name)
                        : Loc.Get("window_opened", name, count);
                    Focusable focused = ReferenceEquals(
                        MenuNavigationHandler.CursorGroup, creator)
                            ? MenuNavigationHandler.Cursor
                            : creator.GetFocused(false);
                    string focusLabel = focused != null && !FocusNavigator.ShouldSkip(focused)
                        ? UiLabelReader.GetLabelOrFallback(focused)
                        : null;
                    string summary = string.IsNullOrEmpty(focusLabel)
                        ? navigable
                        : Loc.Get("panel_with_focus", navigable, focusLabel);
                    Plugin.Log.LogInfo(
                        $"[Window] {summary}  (type={window.GetType().Name})");
                    return summary;
                }

                // This singleton is used only by the two invite-phone outcomes. Its UIList rows
                // visibly identify the eligible Paras and OnValidateClicked acts on the selected
                // subset. Say that purpose, the real row count, and the silently seeded control in
                // one opening announcement so the first Down press cannot skip an unknown choice.
                if (window is UISelectCharacters characterPicker)
                {
                    int available = characterPicker.CharacterList?.CurrentItems?.Count ?? 0;
                    string pickerSummary = available == 0
                        ? Loc.Get("invite_picker_none")
                        : (available == 1
                            ? Loc.Get("invite_picker_one")
                            : Loc.Get("invite_picker_many", available));
                    Focusable cursor = MenuNavigationHandler.Cursor;
                    Focusable focused = cursor != null
                        && cursor.gameObject.activeInHierarchy
                        && cursor.GetComponentInParent<UISelectCharacters>() == characterPicker
                            ? cursor
                            : characterPicker.GetFocused(false);
                    string focusLabel = focused != null && !FocusNavigator.ShouldSkip(focused)
                        ? UiLabelReader.GetLabelOrFallback(focused)
                        : null;
                    string summary = string.IsNullOrEmpty(focusLabel)
                        ? pickerSummary
                        : Loc.Get("panel_with_focus", pickerSummary, focusLabel);
                    Plugin.Log.LogInfo(
                        $"[Window] {summary}  (type={window.GetType().Name})");
                    return summary;
                }

                // There is no distinct Accept control in this screen. Each
                // UIOccupationOfferItem is the apply action itself, so name that model explicitly
                // and include the first live offer instead of presenting the cards as generic
                // informational text.
                if (window is UIOccupationOffers occupationOffers)
                {
                    string summary = DescribeOccupationOffers(occupationOffers);
                    Plugin.Log.LogInfo(
                        $"[Window] {summary}  (type={window.GetType().Name})");
                    return summary;
                }

                if (count > 0 && !HasSemanticWindowSummary(window))
                {
                    string navigable = count == 1
                        ? Loc.Get("window_opened_one", name)
                        : Loc.Get("window_opened", name, count);
                    Plugin.Log.LogInfo($"[Window] {navigable}  (type={window.GetType().Name})");
                    return navigable;
                }

                string text = UiLabelReader.GetLabel(window);

                if (string.IsNullOrEmpty(text))
                {
                    // Nothing to say. The bare name was the noise TEST 21 asked to be rid of.
                    Plugin.Log.LogInfo($"[Window] '{name}' has no focusables and no text; not announced.");
                    return null;
                }

                string message = HasSemanticWindowSummary(window)
                    ? text
                    : Loc.Get("window_informational", name, text);
                Plugin.Log.LogInfo($"[Window] {message}  (type={window.GetType().Name})");
                return message;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Reading an informational window threw: " + ex);
                return null;
            }
        }

        /// <summary>Describes an interaction target and the real control selected on entry.</summary>
        private static string DescribeInteractionWindow(
            UIInteractions window, string windowName, int reachableCount)
        {
            UIInteractionsList root = null;
            UIList lists = window.UIInteractionLists;
            if (lists?.CurrentItems != null)
            {
                foreach (UIListItemBase item in lists.CurrentItems)
                {
                    var list = item as UIInteractionsList;
                    if (list != null && list.ListDepth == 0 && list.gameObject.activeInHierarchy)
                    {
                        root = list;
                        break;
                    }
                }
            }

            string context = root != null ? UiLabelReader.GetLabel(root) : null;

            Focusable focused = null;
            Focusable cursor = MenuNavigationHandler.Cursor;
            if (cursor != null && MenuNavigationHandler.CursorGroup != null
                && ReferenceEquals(MenuNavigationHandler.CursorGroup, window)
                && cursor.gameObject.activeInHierarchy)
            {
                focused = cursor;
            }
            else
            {
                focused = window.GetFocused(false);
            }

            if (focused != null && FocusNavigator.ShouldSkip(focused))
            {
                focused = null;
            }

            string choice = focused != null ? UiLabelReader.GetLabelOrFallback(focused) : null;
            if (!string.IsNullOrEmpty(context)
                && string.Equals(context, choice, StringComparison.Ordinal))
            {
                choice = null;
            }

            string detail;
            if (!string.IsNullOrEmpty(context) && !string.IsNullOrEmpty(choice))
            {
                detail = Loc.Get("window_informational", context, choice);
            }
            else
            {
                detail = !string.IsNullOrEmpty(context) ? context : choice;
            }

            if (!string.IsNullOrEmpty(detail))
            {
                return Loc.Get("window_informational", windowName, detail);
            }

            return reachableCount == 1
                ? Loc.Get("window_opened_one", windowName)
                : Loc.Get("window_opened", windowName, reachableCount);
        }

        /// <summary>Describes the live job-card list and its apply ownership.</summary>
        private static string DescribeOccupationOffers(UIOccupationOffers window)
        {
            string paraName = window?.Character?.Data?.FullName;
            paraName = string.IsNullOrWhiteSpace(paraName)
                ? Loc.Get("job_offers_unknown_para")
                : ScreenReader.NormalizeForOutput(paraName.Trim());

            int count = 0;
            string first = null;
            if (window?.UIListOffers?.CurrentItems != null)
            {
                foreach (UIListItemBase item in window.UIListOffers.CurrentItems)
                {
                    var offer = item as UIOccupationOfferItem;
                    if (offer == null || !offer.gameObject.activeInHierarchy)
                    {
                        continue;
                    }

                    count++;
                    if (first == null)
                    {
                        first = UiLabelReader.GetLabel(offer);
                    }
                }
            }

            string summary = count == 0
                ? Loc.Get("job_offers_none", paraName)
                : (count == 1
                    ? Loc.Get("job_offers_one", paraName)
                    : Loc.Get("job_offers_many", paraName, count));
            return string.IsNullOrWhiteSpace(first)
                ? summary
                : Loc.Get("job_offers_first", summary, first);
        }

        private static void ReadPendingPrompt()
        {
            if (_pendingPrompt == null || _pendingPrompt.FramesRemaining-- > 0)
            {
                return;
            }

            PendingPrompt pending = _pendingPrompt;
            _pendingPrompt = null;

            try
            {
                UIPrompt prompt = pending.Prompt;
                if (prompt == null || !prompt.IsVisible)
                {
                    return;
                }

                string title = ReadTranslatedText(prompt.LabelTitle);
                string message = ReadTranslatedText(prompt.LabelMessage);

                // An unresolved formatted fragment is never usable speech. The normal path has
                // applied all parameters by now; this guard makes a changed caller fail audibly as
                // a generic prompt instead of leaking `{0}` again.
                if (ContainsNumericPlaceholder(title))
                {
                    Plugin.Log.LogWarning("[Prompt] Suppressed an unresolved title after settling.");
                    title = null;
                }
                if (ContainsNumericPlaceholder(message))
                {
                    Plugin.Log.LogWarning("[Prompt] Suppressed an unresolved message after settling.");
                    message = null;
                }

                string spoken = string.IsNullOrEmpty(title)
                    ? message
                    : (string.IsNullOrEmpty(message)
                        ? title
                        : Loc.Get("prompt_body", Sentence(title), message));
                if (string.IsNullOrEmpty(spoken))
                {
                    spoken = Loc.Get("prompt_opened");
                }

                string choices = DescribeChoices(pending.Buttons);
                if (!string.IsNullOrEmpty(choices))
                {
                    spoken = Loc.Get("prompt_with_choices", Sentence(spoken), choices);
                }

                Plugin.Log.LogInfo("[Prompt] " + spoken);
                MenuNavigationHandler.FocusForegroundWindow(prompt);
                ScreenReader.Speak(spoken);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Reading a settled prompt threw: " + ex);
            }
        }

        /// <summary>
        /// True when the game itself says a window interrupts ordinary play strongly enough that
        /// keyboard navigation should enter it as it opens.
        /// </summary>
        private static bool ShouldTakeForegroundFocus(UIWindow window)
        {
            return window != null
                && (window.RegisterWindowForEscapeButtonEvent
                    || window.BlockPlayerActionWhenShown
                    || window.HideOtherInterfacesWhenShown
                    || window.PauseTimeWhenShown);
        }

        private static bool ContainsNumericPlaceholder(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            for (int i = 0; i + 2 < text.Length; i++)
            {
                if (text[i] != '{' || !char.IsDigit(text[i + 1]))
                {
                    continue;
                }

                int end = i + 2;
                while (end < text.Length && char.IsDigit(text[end]))
                {
                    end++;
                }

                if (end < text.Length && text[end] == '}')
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Captures F3 before the game's systems run. Every captured press is consumed by
        /// <see cref="Tick"/> as either a report or an explicit reason a report is unavailable.
        /// </summary>
        public static void BeforeGameSystems()
        {
            try
            {
                if (SettingsHandler.IsCapturingBindingInput) return;
                Keyboard keyboard = Keyboard.current;
                if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
                {
                    _escapePressedFrame = Time.frameCount;
                }

                if (!ContextKeyInput.ContextPressed && !ContextKeyInput.RepeatPressed)
                {
                    return;
                }

                if (ContextKeyInput.RepeatPressed)
                {
                    _importantEventRequested = true;
                    _importantEventRequestFrame = Time.frameCount;
                    Plugin.Log.LogInfo(
                        $"[Context] Shift+F3 captured before game systems at frame "
                      + $"{_importantEventRequestFrame}; state={GameLoadingManager.State}.");
                }

                if (!ContextKeyInput.ContextPressed) return;
                _contextRequested = true;
                _contextRequestFrame = Time.frameCount;
                Plugin.Log.LogInfo(
                    $"[Context] F3 captured before game systems at frame {_contextRequestFrame}; "
                  + $"state={GameLoadingManager.State}.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("WindowHandler.BeforeGameSystems threw: " + ex);
            }
        }

        /// <summary>
        /// Called once per frame from the game loop hook. Watches HUD visibility, settles new
        /// windows and answers a captured F3 request.
        /// </summary>
        public static void Tick()
        {
            bool contextRequested = _contextRequested;
            int requestFrame = _contextRequestFrame;
            _contextRequested = false;
            _contextRequestFrame = -1;
            bool importantEventRequested = _importantEventRequested;
            int importantEventRequestFrame = _importantEventRequestFrame;
            _importantEventRequested = false;
            _importantEventRequestFrame = -1;

            try
            {
                if (IsSceneTransitionActive)
                {
                    // Delivery rechecks visibility after loading, so only destination windows
                    // survive. An old prompt belongs to the scene being left.
                    _pendingPrompt = null;
                    if (contextRequested || importantEventRequested)
                        ScreenReader.Speak(Loc.Get("context_unavailable_transition"));
                    return;
                }

                ReadPendingInformationals();
                ReadPendingPrompt();

                HybridPlayer hybridPlayer = PlayerManager.Instance != null
                    ? PlayerManager.Instance.HybridPlayer1
                    : null;

                if (hybridPlayer != null)
                {
                    AnnounceHudVisibility(hybridPlayer);
                }

                if (importantEventRequested)
                {
                    bool repeated = AnnouncementCoordinator.RepeatLastImportant();
                    Plugin.Log.LogInfo(
                        $"[Context] Shift+F3 request from frame {importantEventRequestFrame} -> "
                      + $"important event available={repeated}.");
                    if (!repeated)
                    {
                        ScreenReader.Speak(Loc.Get("important_event_none"));
                    }
                }

                if (!contextRequested)
                {
                    return;
                }

                if (hybridPlayer == null)
                {
                    string unavailable = Loc.Get(
                        "context_unavailable_no_player", CleanName(GameLoadingManager.State.ToString()));
                    Plugin.Log.LogInfo(
                        $"[Context] F3 request from frame {requestFrame} -> unavailable: no local "
                      + $"player; state={GameLoadingManager.State}.");
                    ScreenReader.Speak(unavailable);
                    return;
                }

                string report = BuildContextReport(hybridPlayer);
                Plugin.Log.LogInfo(
                    $"[Context] F3 request from frame {requestFrame} -> report: {report}");
                ScreenReader.Speak(report, interrupt: true, diagnosticSource: "F3-context");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("WindowHandler.Tick threw: " + ex);

                if (contextRequested)
                {
                    // A request must never disappear into an exception. The log retains the exact
                    // exception; speech gives the user the concise runtime reason.
                    ScreenReader.Speak(Loc.Get("context_unavailable_transition"));
                }
            }
        }

        /// <summary>
        /// Speaks when the whole HUD is hidden or restored.
        /// </summary>
        /// <remarks>
        /// Ctrl+H (ToggleUI) wipes every game window in one keystroke, and it is a bare shortcut
        /// with no confirmation. A sighted player sees the UI vanish; without this the mod simply
        /// goes quiet, which is indistinguishable from the mod having broken - the exact ambiguity
        /// a screen reader user cannot resolve.
        ///
        /// player.ShowUI is the game's own flag, read by UpdateGameUI to gate every window
        /// (game-api.md section 19), so this reports the real cause rather than inferring it from
        /// windows disappearing.
        ///
        /// Polled rather than patched: ShowUI is a public FIELD, not a property, so it has no setter
        /// to hook. The comparison against the previous value keeps this to one announcement per
        /// change - lesson 7, applied before it can bite.
        /// </remarks>
        /// <param name="hybridPlayer">The local player.</param>
        private static void AnnounceHudVisibility(HybridPlayer hybridPlayer)
        {
            Player player = hybridPlayer.Player;
            if (player == null)
            {
                return;
            }

            bool showUi = player.ShowUI;

            // First observation seeds the baseline silently; only real changes are spoken.
            if (!_lastShowUi.HasValue)
            {
                _lastShowUi = showUi;
                return;
            }

            if (_lastShowUi.Value == showUi)
            {
                return;
            }

            _lastShowUi = showUi;

            string message = showUi ? Loc.Get("hud_shown") : Loc.Get("hud_hidden");
            Plugin.Log.LogInfo($"[Window] player.ShowUI -> {showUi}. {message}");
            ScreenReader.Speak(message);
        }

        /// <summary>
        /// Describes where the user currently is: screen, panel and focused element.
        /// </summary>
        /// <remarks>
        /// Every part is read from the game rather than tracked in parallel, so the report cannot
        /// drift: UIManager.CurrentWindow for the screen, the navigation handler's active panel,
        /// and the navigation handler's cursor for the element. The cursor is authoritative within
        /// its active group because some game controls use IsFocused as selected-state styling.
        /// </remarks>
        private static string BuildContextReport(HybridPlayer hybridPlayer)
        {
            string contextualPanel;
            string contextualElement;
            bool isLot;
            if (TownMapLotHandler.TryGetContext(out contextualPanel, out contextualElement, out isLot))
            {
                // The explicit lot report is already the requested context. Category pages
                // still need the ordinary screen/panel orientation when no lot is selected.
                if (isLot && !string.IsNullOrWhiteSpace(contextualElement)) return contextualElement;
                return Loc.Get("context_report", Loc.Get("lots_panel"), contextualPanel,
                    string.IsNullOrWhiteSpace(contextualElement) ? Loc.Get("context_no_focus") : contextualElement);
            }
            if (OccupationHandler.TryGetContext(
                out contextualPanel, out contextualElement))
            {
                UIOccupationsChangeSchedule schedule =
                    UI.GetOrNull<UIOccupationsChangeSchedule>(0);
                string contextualScreen = schedule != null
                    ? DescribeWindowName(schedule)
                    : Loc.Get("context_no_window");
                return Loc.Get(
                    "context_report", contextualScreen,
                    string.IsNullOrEmpty(contextualPanel)
                        ? Loc.Get("context_no_panel")
                        : contextualPanel,
                    string.IsNullOrEmpty(contextualElement)
                        ? Loc.Get("context_no_focus")
                        : contextualElement);
            }

            if (ContextActionHandler.TryGetContext(
                out contextualPanel, out contextualElement))
            {
                UICharacters characters = UI.GetOrNull<UICharacters>(0);
                string contextualScreen = characters != null
                    ? DescribeWindowName(characters)
                    : Loc.Get("context_no_window");
                string contextualReport = Loc.Get(
                    "context_report", contextualScreen,
                    string.IsNullOrEmpty(contextualPanel)
                        ? Loc.Get("context_no_panel")
                        : contextualPanel,
                    string.IsNullOrEmpty(contextualElement)
                        ? Loc.Get("context_no_focus")
                        : contextualElement);
                string contextualTutorial = TutorialHandler.GetCurrentGuidance();
                return string.IsNullOrEmpty(contextualTutorial)
                    ? contextualReport
                    : Loc.Get("context_with_tutorial",
                        contextualReport, contextualTutorial);
            }

            FocusableGroup panel = MenuNavigationHandler.ActivePanel;

            // The screen is derived from the panel the user is actually on, NOT from
            // UIManager.CurrentWindow. CurrentWindow only ever holds windows registered for the
            // back button, so in the character creator it is permanently UICharacterOutfits and F3
            // answered "Outfits" no matter which panel the user had moved to. Walking up from the
            // active panel gives the window that actually owns it.
            UIWindow owner = panel != null ? panel.GetComponentInParent<UIWindow>() : null;
            if (owner == null && hybridPlayer.UIManager != null)
            {
                owner = hybridPlayer.UIManager.CurrentWindow;
            }

            string screen = owner != null ? DescribeWindowName(owner) : Loc.Get("context_no_window");
            string panelName = panel != null
                ? MenuNavigationHandler.GetPanelDisplayName(panel)
                : Loc.Get("context_no_panel");

            Focusable cursor = MenuNavigationHandler.Cursor;
            Focusable focused = panel != null
                && cursor != null
                && ReferenceEquals(MenuNavigationHandler.CursorGroup, panel)
                && cursor.gameObject.activeInHierarchy
                    ? cursor
                    : (panel != null ? panel.GetFocused(false) : null);
            if (focused != null && FocusNavigator.ShouldSkip(focused))
            {
                focused = null;
            }
            string element = focused != null
                ? UiLabelReader.GetLabelOrFallback(focused)
                : Loc.Get("context_no_focus");

            string lotDetails = owner is UILotInfo lotWindow
                ? LotReadinessReader.DescribeLotInfo(lotWindow, includeReadiness: false)
                : null;
            if (owner is UIPremadeHouseholdSelection)
                element = Loc.Get("label_with_detail", element, Loc.Get("library_navigation_hint"));

            string report = !string.IsNullOrWhiteSpace(lotDetails)
                ? lotDetails
                : Loc.Get("context_report", screen, panelName, element);
            string tutorial = TutorialHandler.GetCurrentGuidance();
            return string.IsNullOrEmpty(tutorial)
                ? report
                : Loc.Get("context_with_tutorial", report, tutorial);
        }

        /// <summary>Turns a window's type name into something speakable.</summary>
        private static string DescribeWindowName(UIWindow window)
        {
            if (window is UIThoughts)
            {
                return Loc.Get("panel_thoughts");
            }
            if (window is UISkillsInProgressAndUpcomingEvents)
            {
                return Loc.Get("panel_progress_events");
            }
            if (window is UICharacters)
            {
                return Loc.Get("panel_household");
            }
            if (window is UIOfferedWants)
            {
                return Loc.Get("panel_offered_wants");
            }
            if (window is UIOfferedRequests)
            {
                return Loc.Get("panel_offered_requests");
            }
            if (window is UILotInfo)
            {
                return Loc.Get("panel_lot_info");
            }
            if (window is UISelectCharacters)
            {
                return Loc.Get("invite_picker");
            }
            if (window is UIOccupationOffers)
            {
                return Loc.Get("panel_job_offers");
            }
            if (window is UIOccupationsChangeSchedule)
            {
                return Loc.Get("occupation_schedule_panel");
            }

            return CleanName(window.GetType().Name);
        }

        private static bool HasSemanticWindowSummary(UIWindow window)
        {
            return window is UIThoughts || window is UISkillsInProgressAndUpcomingEvents
                || window is UICharacters || window is UIOfferedWants
                || window is UIOfferedRequests || window is UILotInfo;
        }

        /// <summary>
        /// Strips Unity and project prefixes/suffixes and splits CamelCase into words.
        /// </summary>
        /// <remarks>
        /// "UICharacterOutfits" becomes "Character Outfits". This is presentation of the only name
        /// these screens have, not a rename - see the class remarks.
        /// </remarks>
        private static string CleanName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            if (name.EndsWith("(Clone)", StringComparison.Ordinal))
            {
                name = name.Substring(0, name.Length - "(Clone)".Length);
            }

            if (name.StartsWith("UI", StringComparison.Ordinal) && name.Length > 2)
            {
                name = name.Substring(2);
            }

            var sb = new StringBuilder(name.Length + 8);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];

                // Split before a capital that starts a new word, so runs like "UIList" stay intact.
                if (i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1]))
                {
                    sb.Append(' ');
                }

                sb.Append(c);
            }

            return sb.ToString().Trim();
        }

        private static int CountFocusables(FocusableGroup group)
        {
            if (!_reflectionResolved)
            {
                _reflectionResolved = true;
                _focusablesField = AccessTools.Field(typeof(FocusableGroup), "_focusables");
            }

            var list = _focusablesField?.GetValue(group) as List<Focusable>;
            return LogicalFocusNavigator.CountReachable(group, list);
        }
    }
}
