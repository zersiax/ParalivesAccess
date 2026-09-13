using System;
using Setting;

namespace ParalivesAccess.Handlers
{
    /// <summary>
    /// Resolves a body-diagram handle to the character and axes it edits.
    /// </summary>
    /// <remarks>
    /// THE REAL BODY EDITOR. TEST 38 reported sliders as failing; the log showed the opposite of what
    /// that suggested. `UIClassicSliderItem` was targeted correctly and announces fine
    /// ("Height, 3 of 100"), but `UICharacterCreatorBodyPartDiagram` holds **one** slider row and
    /// **44 handles** - the handles are what the user actually navigates onto, and they carry the
    /// body controls: Chest Size, Height, Muscles, Bottom Position, Stomach Size, Shoulder Depth.
    ///
    /// A handle is a 2D DRAG TARGET, not a slider. `BodySlider.XAxis` and `.YAxis` are each an array
    /// of deformation-slider GUIDs, and a handle may have one axis or both.
    ///
    /// All of it is reachable without Reflection, and the index mapping is exact:
    /// `RefreshHandlesList` does `HandlesList.GetItemAtIndex(i).Init(BodyPartDiagram.Handles[i], ..., i)`,
    /// so `HandleIndex` indexes `Handles` directly with no filtering - verified rather than assumed,
    /// because a filtered list would have made this silently edit the wrong body part.
    ///
    /// INVERSION DELIBERATELY IGNORED. `IsXAxisInverted` / `IsYAxisInverted` exist to map a screen
    /// position onto a value in `OnDrag`; writing values directly never goes through screen space, so
    /// applying them would invert the result. "Increase" therefore means the value rises, which is
    /// the only meaning available to someone who cannot see which way the handle moved.
    /// </remarks>
    public static class BodyDiagramHandle
    {
        /// <summary>One editable axis of a handle.</summary>
        public sealed class Axis
        {
            /// <summary>Every deformation slider this axis drives. All share one value.</summary>
            public UlongAndGuid[] Sliders;

            /// <summary>Spoken name for the axis.</summary>
            public string Name;
        }

        /// <summary>The character and axes behind a focused handle.</summary>
        public sealed class Resolved
        {
            /// <summary>The character being edited.</summary>
            public AssetCharacter Character;

            /// <summary>The source-owned body slider represented by this handle.</summary>
            public BodySlider BodySlider;

            /// <summary>The handle's own name, e.g. "Chest Size".</summary>
            public string Name;

            /// <summary>The primary axis, adjusted with '-' and '='. Never null.</summary>
            public Axis Primary;

            /// <summary>The secondary axis, adjusted with '9' and '0', or null for a 1D handle.</summary>
            public Axis Secondary;
        }

        /// <summary>
        /// Resolves a handle, or returns null when it cannot be read.
        /// </summary>
        /// <remarks>
        /// A handle with only a Y axis still reports it as PRIMARY, so '-' and '=' always work on
        /// whatever single axis a handle has. The second key pair only ever appears on a genuinely
        /// two-dimensional handle, which keeps 1D handles as simple as the personality stats.
        /// </remarks>
        /// <param name="item">The focused handle.</param>
        /// <returns>The resolved handle, or null.</returns>
        public static Resolved Resolve(UICharacterCreatorDiagramHandleItem item)
        {
            // External game API across several settings lookups; must degrade, never throw.
            try
            {
                if (item == null)
                {
                    return null;
                }

                var diagram = item.GetComponentInParent<UICharacterCreatorBodyPartDiagram>();
                if (diagram == null || diagram.BodyPartDiagram == null || diagram.BodyPartDiagram.Handles == null)
                {
                    return null;
                }

                DiagramHandle[] handles = diagram.BodyPartDiagram.Handles;
                if (item.HandleIndex < 0 || item.HandleIndex >= handles.Length)
                {
                    return null;
                }

                BodySlider bodySlider = Settings.Get<BodySliders>()
                    .GetBodySliderByGUID(handles[item.HandleIndex].BodySlider);

                if (bodySlider == null)
                {
                    return null;
                }

                AssetCharacter character = GetSelectedCharacter();
                if (character == null)
                {
                    return null;
                }

                var resolved = new Resolved
                {
                    Character = character,
                    BodySlider = bodySlider,
                    Name = NameOf(bodySlider),
                };

                bool hasX = bodySlider.XAxis != null && bodySlider.XAxis.Length != 0;
                bool hasY = bodySlider.YAxis != null && bodySlider.YAxis.Length != 0;

                if (hasX)
                {
                    resolved.Primary = new Axis { Sliders = bodySlider.XAxis, Name = Loc.Get("axis_horizontal") };
                    if (hasY)
                    {
                        resolved.Secondary = new Axis { Sliders = bodySlider.YAxis, Name = Loc.Get("axis_vertical") };
                    }
                }
                else if (hasY)
                {
                    resolved.Primary = new Axis { Sliders = bodySlider.YAxis, Name = Loc.Get("axis_vertical") };
                }

                return resolved.Primary == null ? null : resolved;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Resolving a body diagram handle threw: {ex}");
                return null;
            }
        }

        /// <summary>True when this handle is the game's configured character-height control.</summary>
        public static bool IsHeight(Resolved handle)
        {
            try
            {
                CharacterCreatorCamera settings = Settings.Get<CharacterCreatorCamera>();
                return handle?.BodySlider != null && settings != null
                    && handle.BodySlider.GUID == settings.HeightSliderGUID;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Identifying a height body handle threw: " + ex);
                return false;
            }
        }

        /// <summary>
        /// Reads an axis's current value, 0..100.
        /// </summary>
        /// <remarks>
        /// The first slider is representative: `OnDrag` writes the SAME computed value to every GUID
        /// in an axis, so they cannot diverge through normal play.
        /// </remarks>
        public static int ReadPercent(Resolved handle, Axis axis)
        {
            float value = CharacterDeformationSliderManager.Instance
                .GetCharacterSliderValue(handle.Character, axis.Sliders[0]);

            return UnityEngine.Mathf.RoundToInt(value * 100f);
        }

        /// <summary>
        /// Writes an axis to a 0..100 value and registers one undo entry for the whole change.
        /// </summary>
        /// <remarks>
        /// Mirrors `OnPointerUp`: every slider on the axis gets its own `SetSliderAction`, and they
        /// SHARE one `ActionID` from `UndoRedoManager.GenerateID`. That shared id is what makes a
        /// multi-slider axis undo as a single step - without it one keypress would need several
        /// presses of Ctrl+Z, which is the same near-invisible defect the name-toggle guard exists
        /// to prevent.
        /// </remarks>
        public static void WritePercent(Resolved handle, Axis axis, int percent)
        {
            int playerIndex = PlayerManager.Instance.HybridPlayer1.Player.PlayerIndex;
            int actionId = UndoRedoManager.Instance.GenerateID(playerIndex);
            float target = percent / 100f;

            foreach (UlongAndGuid slider in axis.Sliders)
            {
                float before = CharacterDeformationSliderManager.Instance
                    .GetCharacterSliderValue(handle.Character, slider);

                CharacterDeformationSliderManager.Instance
                    .SetCharacterSlider(handle.Character, slider, target);

                UndoRedoManager.Instance.AddAction(
                    new SetSliderAction
                    {
                        ActionID = actionId,
                        AssetCharacterGUID = handle.Character.GUID,
                        SliderGUID = slider,
                        Difference = target - before,
                    },
                    playerIndex);
            }
        }

        /// <summary>Names the handle from the game's own body-slider translation key.</summary>
        private static string NameOf(BodySlider bodySlider)
        {
            string translated = TranslationManager.Get(bodySlider.TranslationKey);

            return string.IsNullOrEmpty(translated) || translated == bodySlider.TranslationKey
                ? bodySlider.DisplayName
                : translated.Trim();
        }

        private static AssetCharacter GetSelectedCharacter()
        {
            HybridPlayer hybridPlayer = PlayerManager.Instance != null
                ? PlayerManager.Instance.HybridPlayer1
                : null;

            Player player = hybridPlayer != null ? hybridPlayer.Player : null;
            if (player == null)
            {
                return null;
            }

            ulong guid = player.GetSelectedCharacterGUID();
            return guid == 0UL ? null : AssetManager.Instance.GetCharacter(guid);
        }
    }
}
