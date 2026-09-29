using System;
using UnityEngine;
using UnityEngine.EventSystems;
using VWW.Clients.Curio.Avatar;
using VWW.Clients.Curio;
using VWW.CoreLibs.Shared;

namespace FlyMod.Core
{
    // Resolves and caches the local avatar, reads the player's display name,
    // and detects whether a chat/text input box currently has focus - WASD
    // and other movement keys double as normal typing while one does, so
    // every movement feature needs to know this before reading input.
    internal class PlayerContext
    {
        public AvControl Avatar { get; private set; }
        public string PlayerName { get; private set; } = "";
        private float _nextNameAttemptTime;
        public bool TypingInChat { get; private set; }

        public void UpdateForThisFrame()
        {
            ResolveAvatarIfNeeded();
            ResolveNameIfNeeded();
            TypingInChat = IsChatInputFieldFocused();
        }

        private void ResolveAvatarIfNeeded()
        {
            if (Avatar == null)
                Avatar = UnityEngine.Object.FindObjectOfType<AvControl>();
        }

        // The avatar's GameObject is just called "_Self", so the name comes
        // from the social service, the same call the game's own scripts use
        // (SocialGlobal.GetMyName). Retried until the login has finished.
        private void ResolveNameIfNeeded()
        {
            if (PlayerName.Length > 0 || Time.unscaledTime < _nextNameAttemptTime)
                return;
            _nextNameAttemptTime = Time.unscaledTime + 2f;
            try
            {
                ClientAPI client = Singleton<ClientAPI>.Current;
                string name = client?.SocialManager?.GetInfo(client.ViewManager.PersonaID)?.Name;
                if (!string.IsNullOrEmpty(name))
                    PlayerName = name;
            }
            catch (Exception)
            {
                // not logged in yet - try again shortly
            }
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
