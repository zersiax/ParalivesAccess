using UnityEngine;
using UnityEngine.InputSystem;

namespace ParalivesAccess.Handlers
{
    /// <summary>Separates asynchronous cursor warps from deliberate pointer movement.</summary>
    internal sealed class PlacementPointer
    {
        private Vector2 _target;
        private bool _expected, _arrived;

        internal void Clear() { _expected = _arrived = false; }
        internal void Expect(Vector2 target)
        {
            if (_expected && Vector2.Distance(target, _target) <= .25f) return;
            _target = target; _expected = true; _arrived = false;
        }

        internal bool WasMoved(Vector2 reported)
        {
            if (!_expected || Mouse.current == null) return false;
            // Native placement reads legacy Input.mousePosition, while warps use the
            // Input System. Either stream can report the previous position temporarily.
            // Wait for arrival, then hand over only when both streams leave the target.
            if (Vector2.Distance(reported, _target) <= 2f
                || Vector2.Distance(Mouse.current.position.ReadValue(), _target) <= 2f)
            { _arrived = true; return false; }
            return _arrived;
        }
    }
}
