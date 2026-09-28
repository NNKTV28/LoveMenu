using System;
using UnityEngine;
using UnityEngine.EventSystems;
using VWW.Clients.Curio.Avatar;

namespace FlyMod.Core
{
    // Resolves and caches the local avatar, derives a display name from it,
    // and detects whether a chat/text input box currently has focus - WASD
    // and other movement keys double as normal typing while one does, so
    // every movement feature needs to know this before reading input.
    internal class PlayerContext
    {
        public AvControl Avatar { get; private set; }
        public string PlayerName { get; private set; } = "—";
        public bool TypingInChat { get; private set; }

        // Set by any controller that deliberately moves the avatar's
        // position (waypoint/friend teleport, unstick, landing after fly).
        // KnockbackImmunity reads and clears this each frame so it doesn't
        // mistake our own teleports for an external knockback to revert.
        public bool WasTeleportedByUsThisFrame { get; private set; }

        public void MarkIntentionalTeleport() => WasTeleportedByUsThisFrame = true;

        public void UpdateForThisFrame()
        {
            ResolveAvatarAndNameIfNeeded();
            TypingInChat = IsChatInputFieldFocused();
        }

        // Called once, after every feature has had a chance to read the
        // flag for this frame.
        public void ClearIntentionalTeleportFlag() => WasTeleportedByUsThisFrame = false;

        private void ResolveAvatarAndNameIfNeeded()
        {
            if (Avatar != null)
                return;

            Avatar = UnityEngine.Object.FindObjectOfType<AvControl>();
            if (Avatar == null)
                return;

            bool nameNotYetResolved = PlayerName == "—" || string.IsNullOrEmpty(PlayerName);
            if (nameNotYetResolved)
                PlayerName = string.IsNullOrEmpty(Avatar.transform.root.name) ? "Player" : Avatar.transform.root.name;
        }

        private static bool IsChatInputFieldFocused()
        {
            GameObject focusedGameObject = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            if (focusedGameObject == null)
                return false;

            foreach (Component component in focusedGameObject.GetComponents<Component>())
                if (component != null && component.GetType().Name.IndexOf("InputField", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }
    }
}
