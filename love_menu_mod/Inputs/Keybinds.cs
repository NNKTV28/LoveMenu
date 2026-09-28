using System;
using UnityEngine;

namespace FlyMod.Inputs
{
    // Rebindable keys plus the "waiting for the next keypress" capture flow
    // used by the Settings screen.
    internal class Keybinds
    {
        public KeyCode MenuKey = KeyCode.F7;
        public KeyCode FlyUpKey = KeyCode.Space;
        public KeyCode FlyDownKey = KeyCode.LeftControl;

        public enum RebindTarget { None, Menu, Up, Down }
        public RebindTarget Rebinding { get; private set; } = RebindTarget.None;

        private static readonly KeyCode[] AllKeyCodes = (KeyCode[])Enum.GetValues(typeof(KeyCode));

        public void BeginRebind(RebindTarget target)
        {
            Rebinding = Rebinding == target ? RebindTarget.None : target;
        }

        // Returns true if this frame's input was consumed by a rebind
        // capture (cancelled, just captured, or still waiting) - the caller
        // should skip normal key handling for the frame either way.
        public bool CaptureIfRebinding()
        {
            if (Rebinding == RebindTarget.None)
                return false;

            if (WasCancelPressed())
            {
                Rebinding = RebindTarget.None;
                return true;
            }

            if (TryGetNewlyPressedKey(out KeyCode pressedKey))
            {
                AssignKeyToCurrentTarget(pressedKey);
                Rebinding = RebindTarget.None;
            }
            return true;
        }

        private static bool WasCancelPressed() => Input.GetKeyDown(KeyCode.Escape);

        private static bool TryGetNewlyPressedKey(out KeyCode pressedKey)
        {
            foreach (KeyCode candidateKey in AllKeyCodes)
            {
                if (Input.GetKeyDown(candidateKey))
                {
                    pressedKey = candidateKey;
                    return true;
                }
            }
            pressedKey = KeyCode.None;
            return false;
        }

        private void AssignKeyToCurrentTarget(KeyCode key)
        {
            switch (Rebinding)
            {
                case RebindTarget.Menu: MenuKey = key; break;
                case RebindTarget.Up: FlyUpKey = key; break;
                case RebindTarget.Down: FlyDownKey = key; break;
            }
        }
    }
}
