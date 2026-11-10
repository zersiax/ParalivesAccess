using System;
using System.Collections.Generic;
using ParalivesAccess.Speech;

namespace ParalivesAccess.Handlers
{
    /// <summary>
    /// A mod-owned ordered list of controls that the game does not expose as <c>Focusable</c>s.
    /// </summary>
    /// <remarks>
    /// WHY THIS EXISTS
    ///
    /// The game's focus system only reaches `Focusable`s, and whole screens are built from controls
    /// that are not. `UICharacterName` is the clearest case: four `TMP_InputField`s and five Unity
    /// `Toggle`s, none of them focusable, which is exactly why that window reports ONE focusable
    /// (`ButtonClose`) and why names cannot be typed at all (game-api.md section 17).
    ///
    /// There is no way to make those controls focusable from outside the prefab, so the ordering has
    /// to be the mod's. This is the same trade already made for arrow navigation in FocusNavigator:
    /// the mod owns the ordering, the game keeps ownership of state and actions.
    ///
    /// DELIBERATELY GENERIC. This is the seed of the guided flow's controller
    /// (docs/character-creator-flow.md, phase 3): a flow step is exactly "walk these N controls,
    /// describe each, act on Enter". Building it once here avoids writing the same thing twice.
    ///
    /// Entries are re-described on every announcement rather than cached, because the game rewrites
    /// this UI continuously - toggling "has first name" hides the first-name field on the next frame.
    /// </remarks>
    public sealed class VirtualPanel
    {
        /// <summary>One control in the panel.</summary>
        public interface IEntry
        {
            /// <summary>Spoken description, including the current value. Never cached.</summary>
            string Describe();

            /// <summary>False when the game is currently hiding this control.</summary>
            bool IsAvailable { get; }

            /// <summary>Invoked on Enter.</summary>
            void Activate();
        }

        private readonly List<IEntry> _entries = new List<IEntry>();
        private int _index;

        /// <summary>The panel's spoken name.</summary>
        public string Name => NameReader?.Invoke() ?? _name;
        private readonly string _name;
        internal Func<string> NameReader { get; set; }

        /// <summary>Creates an empty panel.</summary>
        /// <param name="name">The panel's spoken name.</param>
        public VirtualPanel(string name)
        {
            _name = name;
        }

        /// <summary>Adds a control to the end of the panel.</summary>
        public void Add(IEntry entry)
        {
            if (entry != null)
            {
                _entries.Add(entry);
            }
        }

        /// <summary>The entry the cursor is on, or null when nothing is available.</summary>
        public IEntry Current
        {
            get
            {
                return _index >= 0 && _index < _entries.Count && _entries[_index].IsAvailable
                    ? _entries[_index]
                    : null;
            }
        }

        /// <summary>Number of controls currently available.</summary>
        public int AvailableCount
        {
            get
            {
                int count = 0;
                foreach (IEntry entry in _entries)
                {
                    if (entry.IsAvailable)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>
        /// Puts the cursor on the first available control and announces the panel.
        /// </summary>
        public void Enter()
        {
            Enter(null, announce: true);
        }

        /// <summary>
        /// Puts the cursor on a preferred available control, falling back to the first, and
        /// announces the panel.
        /// </summary>
        /// <param name="preferred">The entry to focus initially, or null for the first entry.</param>
        public void Enter(IEntry preferred)
        {
            Enter(preferred, announce: true);
        }

        /// <summary>
        /// Seeds the cursor without speaking. Used when a surrounding tutorial announcement owns
        /// the description of the newly opened panel.
        /// </summary>
        public void EnterSilently()
        {
            Enter(null, announce: false);
        }

        /// <summary>
        /// Seeds a preferred available control without speaking, falling back to the first.
        /// </summary>
        /// <param name="preferred">The entry to restore, or null for the first entry.</param>
        public void EnterSilently(IEntry preferred)
        {
            Enter(preferred, announce: false);
        }

        private void Enter(IEntry preferred, bool announce)
        {
            _index = -1;

            if (preferred != null)
            {
                int preferredIndex = _entries.IndexOf(preferred);
                if (preferredIndex >= 0 && preferred.IsAvailable)
                {
                    _index = preferredIndex;
                }
            }

            if (_index < 0 && !Step(1, wrap: false))
            {
                if (announce)
                {
                    ScreenReader.Speak(Loc.Get("panel_none"));
                }
                return;
            }

            if (!announce)
            {
                return;
            }

            int count = AvailableCount;
            string message = count == 1
                ? Loc.Get("panel_selected_one", Name)
                : Loc.Get("panel_selected", Name, count);

            Announce(Loc.Get("panel_with_focus", message, Describe()));
        }

        /// <summary>Moves the cursor and announces the new control.</summary>
        /// <param name="direction">+1 for next, -1 for previous.</param>
        public void Move(int direction)
        {
            if (!Step(direction, wrap: true))
            {
                return;
            }

            Announce(Describe());
        }

        /// <summary>Activates the current control.</summary>
        public void Activate()
        {
            IEntry entry = Current;
            if (entry == null)
            {
                return;
            }

            entry.Activate();
        }

        /// <summary>Re-announces the current control, e.g. after its value changed.</summary>
        public void AnnounceCurrent()
        {
            if (Current != null)
            {
                Announce(Describe());
            }
        }

        /// <summary>
        /// Moves the cursor to the next available entry in a direction.
        /// </summary>
        /// <remarks>
        /// Unavailable entries are stepped over rather than removed, so the panel's order stays
        /// stable while the game shows and hides controls underneath it. Bounded by the entry count
        /// so a panel where nothing is available cannot spin.
        /// </remarks>
        private bool Step(int direction, bool wrap)
        {
            if (_entries.Count == 0)
            {
                return false;
            }

            int index = _index;
            for (int i = 0; i < _entries.Count; i++)
            {
                index += direction;

                if (index < 0 || index >= _entries.Count)
                {
                    if (!wrap)
                    {
                        // Scanning from outside the list: start at the appropriate end.
                        index = direction > 0 ? 0 : _entries.Count - 1;
                    }
                    else
                    {
                        index = (index + _entries.Count) % _entries.Count;
                    }
                }

                if (_entries[index].IsAvailable)
                {
                    _index = index;
                    return true;
                }
            }

            return false;
        }

        private string Describe()
        {
            IEntry entry = Current;
            return entry == null ? null : entry.Describe();
        }

        private static void Announce(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            Plugin.Log.LogInfo("[Virtual] " + message);
            ScreenReader.Speak(message);
        }
    }
}
