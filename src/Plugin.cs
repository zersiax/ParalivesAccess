using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using ParalivesAccess.Handlers;
using ParalivesAccess.Probes;
using ParalivesAccess.Speech;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ParalivesAccess
{
    /// <summary>
    /// BepInEx entry point for ParalivesAccess.
    /// </summary>
    /// <remarks>
    /// IMPORTANT ARCHITECTURE NOTE â€” per-frame work must ride the game's loop, not a MonoBehaviour.
    ///
    /// Unity does not tick MonoBehaviours attached to BepInEx's ManagerObject in this game. Verified:
    /// Awake runs (AddComponent calls it synchronously) and reports activeSelf=True,
    /// activeInHierarchy=True, hideFlags=None, enabled=True, isActiveAndEnabled=True â€” yet Start and
    /// Update never fire, with no exception in the BepInEx log or Player.log. The cause is UNKNOWN
    /// and deliberately not guessed at. It is not a global player-loop issue: the game's own
    /// SystemManager : MonoBehaviour ticks normally via private void Update().
    ///
    /// So we hook SystemManager.Update, the game's real per-frame driver for its ~400
    /// ParaSystemBase systems. See docs/game-api.md sections 9a and 10a.
    /// </remarks>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        /// <summary>Unique BepInEx plugin identifier.</summary>
        public const string PluginGuid = "com.florianbeijers.paralivesaccess";

        /// <summary>Human-readable plugin name shown in the BepInEx log.</summary>
        public const string PluginName = "ParalivesAccess";

        /// <summary>Plugin version.</summary>
        public const string PluginVersion = "0.39.37";

        /// <summary>Shared logger so handlers and probes log under the plugin's name.</summary>
        public static ManualLogSource Log { get; private set; }

        private static bool _announcedStartup;
        private static bool _probeDone;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo($"{PluginName} {PluginVersion} loading...");

            Loc.Initialize();

            ContextDeliveryProbe.Enabled = Config.Bind(
                "Diagnostics", "ContextDeliveryAudit", true,
                "Traces F3 keyboard events and native speech submission metadata to diagnose silent context reports.").Value;
            ContextDeliveryProbe.Install();
            ContextKeyInput.Install();

            BuildCatalogHandler.OpenKey = Config.Bind(
                "Keyboard",
                "BuildCatalogKey",
                Key.F6,
                "Opens the accessible browser over the live build catalogue.").Value;
            LotObjectHandler.OpenKey = Config.Bind(
                "Keyboard",
                "LotObjectKey",
                Key.F7,
                "Opens the camera-first current-lot explorer, including tutorial targets.").Value;
            ContextActionHandler.ActionKey = Config.Bind(
                "Keyboard",
                "ContextActionKey",
                Key.F4,
                "Acts on an announced transient context, such as a full Together Bar.").Value;
            VirtualAudioListenerHandler.ToggleKey = Config.Bind(
                "Keyboard",
                "CloseAudioPerspectiveKey",
                Key.F8,
                "Toggles a close virtual audio listener in live mode without changing the visual camera.").Value;
            VirtualAudioListenerHandler.ReferenceDistance = Mathf.Clamp(
                Config.Bind(
                    "Audio",
                    "CloseAudioPerspectiveDistance",
                    3f,
                    "Distance in world units used by the close audio perspective. Range: 0.5 to 8.").Value,
                0.5f,
                8f);
            VirtualAudioListenerHandler.RequestedEnabled = Config.Bind(
                "Audio",
                "CloseAudioPerspectiveOnStartup",
                false,
                "Starts close audio perspective automatically when a loaded game enters live mode.").Value;
            Log.LogInfo(
                $"Close audio perspective: key={VirtualAudioListenerHandler.ToggleKey}, "
              + $"distance={VirtualAudioListenerHandler.ReferenceDistance:0.###}, "
              + $"startup={VirtualAudioListenerHandler.RequestedEnabled}.");
            ObservedParaAudioHandler.BoostDecibels = Mathf.Clamp(
                Config.Bind(
                    "Audio",
                    "ObservedParaFoleyBoostDecibels",
                    6f,
                    "Additional gain for footsteps and activity Foley owned by the followed or "
                  + "selected Para. Does not affect voices, UI, music, or general ambience. "
                  + "Range: 0 (disabled) to 12 dB.").Value,
                0f,
                12f);
            Log.LogInfo(
                $"Observed-Para Foley boost: "
              + $"{ObservedParaAudioHandler.BoostDecibels:0.###} dB.");

            if (BuildCatalogHandler.OpenKey == LotObjectHandler.OpenKey)
            {
                LotObjectHandler.Enabled = false;
                Log.LogError(
                    $"BuildCatalogKey and LotObjectKey are both {BuildCatalogHandler.OpenKey}. "
                    + "The current-lot explorer is disabled until the keys differ.");
            }

            if (ContextActionHandler.ActionKey == Key.F3
                || ContextActionHandler.ActionKey == BuildCatalogHandler.OpenKey
                || ContextActionHandler.ActionKey == LotObjectHandler.OpenKey)
            {
                ContextActionHandler.Enabled = false;
                Log.LogError(
                    $"ContextActionKey {ContextActionHandler.ActionKey} conflicts with another "
                  + "accessibility command. Contextual actions are disabled until the keys differ.");
            }

            if (VirtualAudioListenerHandler.ToggleKey == Key.F3
                || VirtualAudioListenerHandler.ToggleKey == ContextActionHandler.ActionKey
                || VirtualAudioListenerHandler.ToggleKey == BuildCatalogHandler.OpenKey
                || VirtualAudioListenerHandler.ToggleKey == LotObjectHandler.OpenKey)
            {
                VirtualAudioListenerHandler.HandlerEnabled = false;
                Log.LogError(
                    $"CloseAudioPerspectiveKey {VirtualAudioListenerHandler.ToggleKey} conflicts "
                  + "with another accessibility command. Close audio perspective is disabled "
                  + "until the keys differ.");
            }

            bool speechOk = ScreenReader.Initialize();

            Harmony harmony = new Harmony(PluginGuid);
            HookGameLoop(harmony);

            bool focusOk = FocusHandler.ApplyPatches(harmony);
            if (!CreatorUiDiagnostics.ApplyPatches(harmony))
                Log.LogWarning("Creator exit/library diagnostics did not all apply.");
            if (!RelationshipLocationHandler.ApplyPatches(harmony))
                Log.LogWarning("Relationship Locate announcements did not apply.");
            if (!SettingsHandler.ApplyPatches(harmony))
                Log.LogWarning("Settings accessibility hooks did not all apply.");
            if (!CreatorHouseholdHandler.ApplyPatches(harmony))
                Log.LogWarning("Creator household Delete buttons may still require hover.");
            if (!focusOk)
            {
                Log.LogError("Focus/hover patches did not apply. UI announcements will not work.");
            }

            if (!SaveGameListHandler.ApplyPatches(harmony))
            {
                Log.LogWarning(
                    "Off-screen save rows will be named from their model but cannot be safely "
                  + "activated until the game's visual initialization reaches them.");
            }

            // Navigation still works without this patch; only panel switching on game-driven
            // windows would double-navigate, so a failure here is not fatal.
            bool navigationPatchesOk = MenuNavigationHandler.ApplyPatches(harmony);
            if (!navigationPatchesOk)
            {
                Log.LogWarning(
                    "UIManager.NavigateMenu patch did not apply. Switching to a nested panel on a "
                  + "game-driven window will move focus twice per arrow press.");

                // The new slice depends on both halves: actual catalogue Focusable activation uses
                // the ClickUp guard, and placement must suppress UIManager.NavigateMenu while its
                // synthetic ButtonConfirm.Up reaches EndMoveItem. Partial operation is unsafe.
                DisableLotBuildHandlers(
                    "their navigation and activation safety patches are incomplete");
            }

            if (!WindowHandler.ApplyPatches(harmony))
            {
                Log.LogWarning("Screen changes will not be announced; the F3 context key still works.");
            }

            if (!StateReportingHandler.ApplyPatches(harmony))
            {
                Log.LogWarning(
                    "Player interaction lifecycle, notification, and request-indicator reporting are unavailable.");
            }

            if (!SceneNarrationHandler.ApplyPatches(harmony))
            {
                Log.LogWarning(
                    "Some followed-Para scene descriptions are unavailable; see the patch report above.");
            }

            if (!VirtualAudioListenerHandler.ApplyPatches(harmony))
            {
                Log.LogWarning(
                    "Close audio perspective is unavailable; native listener and audio distance behavior remain unchanged.");
            }

            Log.LogInfo($"{PluginName} {PluginVersion} loaded. Speech={speechOk}, focus patches={focusOk}.");
        }

        private void HookGameLoop(Harmony harmony)
        {
            try
            {
                MethodInfo target = AccessTools.Method(typeof(SystemManager), "Update");
                if (target == null)
                {
                    Log.LogError("Could not find SystemManager.Update - the game loop hook is unavailable.");
                    DisableLotBuildHandlers("the game-loop target is unavailable");
                    return;
                }

                MethodInfo prefix = AccessTools.Method(typeof(Plugin), nameof(BeforeGameSystems));

                MethodInfo postfix = AccessTools.Method(typeof(Plugin), nameof(OnGameLoopTick));
                if (prefix == null || postfix == null)
                {
                    Log.LogError(
                        "Could not resolve the SystemManager.Update hook methods "
                      + $"(input prefix={prefix != null}, tick postfix={postfix != null}).");
                    DisableLotBuildHandlers("the game-loop hook methods could not be resolved");
                    return;
                }

                harmony.Patch(
                    target,
                    prefix: new HarmonyMethod(prefix),
                    postfix: new HarmonyMethod(postfix));

                Patches patches = Harmony.GetPatchInfo(target);
                bool prefixApplied = HasPatch(patches != null ? patches.Prefixes : null, prefix);
                bool postfixApplied = HasPatch(patches != null ? patches.Postfixes : null, postfix);

                Log.LogInfo(
                    "Hooked SystemManager.Update, "
                  + $"inputPrefixApplied={prefixApplied}, tickPostfixApplied={postfixApplied}.");

                if (!prefixApplied)
                {
                    Log.LogWarning(
                        "Raw key instrumentation did not apply. Physical keypresses will not be "
                      + "available to disambiguate missing actions.");
                    DisableLotBuildHandlers(
                        "the pre-system camera, key-trace, and placement bridge hook is unavailable");
                }

                if (!postfixApplied)
                {
                    Log.LogError("The game loop tick postfix did not apply. Per-frame mod features will not run.");
                    DisableLotBuildHandlers("the post-system state-observation hook is unavailable");
                }
            }
            catch (Exception ex)
            {
                Log.LogError("Hooking SystemManager.Update threw: " + ex);
                DisableLotBuildHandlers("installing the game-loop hook threw an exception");
            }
        }

        private static void DisableLotBuildHandlers(string reason)
        {
            TutorialHandler.Enabled = false;
            ContextActionHandler.Enabled = false;
            OccupationHandler.Enabled = false;
            BuildCatalogHandler.Enabled = false;
            ItemPlacementHandler.Enabled = false;
            LotObjectHandler.Enabled = false;
            Log.LogError($"Tutorial/occupation/lot/build/item handlers disabled because {reason}.");
        }

        /// <summary>
        /// Prefix on the game's driver. Raw instrumentation is deliberately first, followed by the
        /// exclusive camera guards and the narrow placement-confirm bridge.
        /// </summary>
        private static void BeforeGameSystems()
        {
            ContextDeliveryProbe.BeforeGameSystems();
            InputTrace.BeforeGameSystems();
            ContextKeyInput.BeforeGameSystems();
            AnnouncementCoordinator.BeforeGameSystems();
            VirtualAudioListenerHandler.BeforeGameSystems();
            SettingsHandler.Tick();
            WindowHandler.BeforeGameSystems();
            if (SettingsHandler.IsCapturingBindingInput)
            {
                MenuNavigationHandler.BeforeGameSystems();
                return;
            }
            ActivityQueueSummaryHandler.BeforeGameSystems();
            OccupationHandler.BeforeGameSystems();
            MenuNavigationHandler.BeforeGameSystems();
            TutorialHandler.BeforeGameSystems();
            BuildCatalogHandler.BeforeGameSystems();
            LotObjectHandler.BeforeGameSystems();
            ItemPlacementHandler.BeforeGameSystems();
        }

        private static bool HasPatch(IEnumerable<Patch> patches, MethodInfo method)
        {
            if (patches == null || method == null)
            {
                return false;
            }

            foreach (Patch patch in patches)
            {
                if (patch.owner == PluginGuid && patch.PatchMethod == method)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Postfix on the game's per-frame driver. Handles mod systems and one-shot startup work.
        /// </summary>
        /// <remarks>
        /// Kept cheap: once both one-shot flags are set this returns immediately. Uses unscaled
        /// time because the game pauses Time.timeScale in menus.
        /// </remarks>
        private static void OnGameLoopTick()
        {
            SettingsHandler.Tick();
            if (SettingsHandler.IsCapturingBindingInput)
            {
                // The native rebinding prompt still needs its deferred text announced. All mod
                // shortcuts stand down, including the frame on which the new binding completes.
                MenuNavigationHandler.Tick();
                WindowHandler.Tick();
                FocusHandler.Tick();
                AnnouncementCoordinator.Tick();
                return;
            }
            // InputTrace runs in the matching PREFIX, before the game's systems. Every action below
            // therefore has its physical key evidence earlier in the log.

            // Captures a preview only after CreateItemForPlacementEvent has run, and verifies the
            // transaction only after EndMoveItem has updated the lot list and balance.
            ItemPlacementHandler.AfterGameSystems();

            // The close listener is prepared before audio systems for gate/mix evaluation, then
            // moved once more here so the audible pose reflects this frame's character movement.
            VirtualAudioListenerHandler.AfterGameSystems();

            // The catalogue and object browsers own explicit function keys and ordered lists.
            // They run before general navigation so opening one suppresses every older handler on
            // the same frame rather than one frame later.
            // A transient action is announced without moving focus. Only the user's explicit F4
            // request enters its compact choice panel. It runs before tutorial observation so the
            // tutorial describes the coalesced conversation mode rather than one card panel.
            OccupationHandler.Tick();
            ContextActionHandler.Tick();
            TutorialHandler.Tick();
            BuildCatalogHandler.Tick();
            LotObjectHandler.Tick();

            // Supplies keyboard navigation where the game provides none. Runs before the probe so
            // the probe observes the resulting state. It still runs under exclusive ownership so
            // it can discard any half-finished Enter gesture, but returns before navigating.
            MenuNavigationHandler.Tick();

            // F3 is the global on-demand context/repeat key. It must remain available while a
            // mod-owned browser has exclusive arrow/Enter ownership; TEST 51 showed that opening
            // F7 before pressing F3 otherwise made a just-missed tutorial instruction impossible
            // to recover. The handler only observes state and reacts to F3, so it does not contend
            // with the owning browser's controls.
            WindowHandler.Tick();

            // The selected Para's running and queued activities follow the main F3 context
            // sentence. This observer also gives one quiet snapshot after loading or selection;
            // it never turns autonomous queue mutations into narration.
            ActivityQueueSummaryHandler.Tick();

            // Follows only interactions created through the game's explicit player-input method,
            // and reads notifications when the model releases them for actual display. Autonomous
            // simulation remains quiet.
            StateReportingHandler.Tick();

            // A follow begins with one readable, source-backed camera frame. The game's own
            // UpdateFreeCamera remains the sole continuous tracker and manual camera movement
            // retains its native authority to cancel following.
            CameraFollowHandler.Tick();
            CameraZoomHandler.Tick();

            // Adds a restrained descriptive layer around the followed/selected Para: sourced
            // emotion changes, conversation turns, stable area changes, and curated activity
            // phases. Visual Together-card events are patched at their exact display boundary.
            SceneNarrationHandler.Tick();

            // Native audio has now completed its channel, user-option, ducking, and spatial setup.
            // Lift only Foley owned by the followed/selected Para or their running activity item;
            // spatial position and distance attenuation remain native.
            ObservedParaAudioHandler.Tick();

            if (!LotBuildInputOwnership.OwnsInput)
            {
                // Reports what opening a character-creator category revealed, and moves focus into it.
                // Runs after the navigation handler so the panel list it selects from is current.
                CharacterCreatorHandler.Tick();

                // Name Details holds no Focusables the game's system can reach, so the mod supplies the
                // ordering. Runs before the adjust keys so typing mode wins any contest for a keypress.
                NameEntryHandler.Tick();

                // The town map's lots are ~80 one-item FocusableGroups (game-api.md section 21), so the
                // mod supplies a list rather than letting panel cycling loose on them.
                TownMapLotHandler.Tick();

                // '7' and '8' change the value of the focused control. Runs after navigation so the
                // cursor it reads is this frame's.
                ValueAdjustHandler.Tick();

                // Announces movement through Unity's EventSystem - the second navigation system, which
                // the Focusable patches are blind to (game-api.md sections 4 and 17).
                FocusHandler.Tick();

                // Announces model-backed changes to tutorial guidance, the tracked goal and active
                // wants. Its first observation is silent because the load/window path owns that one.
                ObjectiveHandler.Tick();

                // Runs every frame while the navigation diagnostic is enabled; it self-throttles.
                MenuNavigationProbe.Tick();

                // Answers where the character creator's actual content lives. Runs after the navigation
                // handler so it can report whether that frame's panel list saw it. Self-throttling: it
                // only logs when the open category path changes, and at most MaxDumps times per session.
                CharacterCreatorContentProbe.Tick();
            }

            // This is deliberately the final recurring speech handler. Important world-state
            // events wait for physical input to settle and then queue behind every foreground
            // utterance produced above, rather than being vulnerable to a later same-frame focus
            // or objective announcement.
            AnnouncementCoordinator.Tick();

            if (_announcedStartup && _probeDone)
            {
                return;
            }

            try
            {
                if (!_announcedStartup)
                {
                    _announcedStartup = true;

                    string message = ScreenReader.IsAvailable
                        ? Loc.Get("mod_loaded")
                        : Loc.Get("speech_unavailable");

                    Log.LogInfo("Startup announcement: " + message);
                    ScreenReader.Speak(message);
                }

                if (!_probeDone)
                {
                    _probeDone = true;
                    InputBindingProbe.DumpIfMissing();
                }
            }
            catch (Exception ex)
            {
                // Never let a mod exception escape into the game's loop.
                _announcedStartup = true;
                _probeDone = true;
                Log.LogError("Startup tick threw: " + ex);
            }
        }

        /// <summary>
        /// Diagnostic only. This must NOT tear down speech.
        /// </summary>
        /// <remarks>
        /// The plugin's host GameObject is destroyed shortly after load in this game. Shutting
        /// Prism down here killed speech before the first frame: Awake logged
        /// "Prism ready. Backend='NVDA'" and Speech=True, yet by the first SystemManager.Update
        /// tick IsAvailable was false and every announcement was dropped.
        ///
        /// This also explains the earlier unexplained finding that Start and Update never ran on
        /// BepInEx-attached MonoBehaviours - the object does not survive to be ticked. Awake's
        /// diagnostics reported it healthy only because it had not been destroyed yet.
        ///
        /// The mod is unaffected otherwise: Harmony patches and static state outlive the object.
        /// Prism is left running deliberately; the OS reclaims it at process exit. If explicit
        /// shutdown is ever needed, hook the game's QuitApplicationEvent system instead.
        /// </remarks>
        private void OnDestroy()
        {
            Log.LogWarning(
                "Plugin host GameObject was destroyed. This is expected in this game and is why "
              + "MonoBehaviour Update never runs. Speech is deliberately NOT shut down here.");
        }
    }
}
