using System;
using BepInEx.Logging;
using UnityEngine;
using VWW.Clients.Curio.ClientJS;
using VWW.Clients.Curio.ClientUI.Elements;
using VWW.CoreLibs.Shared;

using FlyMod.Core;

namespace FlyMod.Features
{
    // Hides the promotional popups the game shows right after login.
    //
    // They're GUIBaseWindow instances named "WebScreen" (confirmed from the
    // window log). The events widget lives in a different window named
    // "WidgetManager", so it isn't affected.
    //
    // "WebScreen" is a generic name though - the shop/help dialogs the
    // player opens on purpose are very likely WebScreens too. Hiding every
    // one of them would slam those shut the moment they were opened, so
    // this only acts during a short window after the avatar first exists:
    // the login popups appear on their own, anything opened deliberately
    // comes later and is left alone.
    //
    // An earlier version matched GUIWebView by URL and hid that instead.
    // That was wrong: there is only ONE GUIWebView, a shared host the
    // events widget and promo pages both load into, so hiding it blanked
    // the shared host permanently - empty black boxes, broken events
    // widget, and a dead frame that could hold InputDisabled true and block
    // movement. Webviews are never touched now.
    internal class PromoPopupController
    {
        private const float ScanIntervalSeconds = 0.5f;
        private const float AutoHideWindowSeconds = 45f;
        private const float RestoreInputWindowSeconds = 75f;

        private static readonly string[] PromoWindowNames = { "WebScreen" };
        private static readonly string[] NeverTouchNameFragments = { "chat", "widget", "hud", "main", "menu", "profile", "avatar" };

        private readonly ManualLogSource _log;
        public bool Enabled;

        private float _timeSinceLastScan;
        private float _secondsSincePlayerReady = -1f;
        private bool _hidAPromoWindow;

        public PromoPopupController(ManualLogSource log)
        {
            _log = log;
        }

        public void Tick(float deltaTime, bool playerReady, bool typingInChat)
        {
            if (!playerReady || !Enabled)
                return;
            if (_secondsSincePlayerReady < 0f)
                _secondsSincePlayerReady = 0f;
            _secondsSincePlayerReady += deltaTime;

            if (_secondsSincePlayerReady > RestoreInputWindowSeconds)
                return;

            _timeSinceLastScan += deltaTime;
            if (_timeSinceLastScan < ScanIntervalSeconds)
                return;
            _timeSinceLastScan = 0f;

            if (_secondsSincePlayerReady <= AutoHideWindowSeconds)
                HideMatchingWindows();
            RestoreInputIfPopupLeftItDisabled(typingInChat);
        }

        // The popup's own JS calls KeybindManager.DisableInput() while it
        // loads and EnableInput() when its close button runs. Hiding the
        // window means that close handler never runs, so input stays
        // disabled and the player can't move, jump, sit or crouch. This
        // calls the game's own EnableInput() to undo it - skipped while
        // typing in chat, where input is disabled legitimately.
        private void RestoreInputIfPopupLeftItDisabled(bool typingInChat)
        {
            if (!_hidAPromoWindow || typingInChat)
                return;
            try
            {
                KeybindManager keybindManager = Singleton<KeybindManager>.Current;
                if (keybindManager == null || !keybindManager.InputDisabled)
                    return;
                keybindManager.EnableInput();
                DebugLog.Detail("[popup] re-enabled input left disabled by a hidden promo popup");
            }
            catch (Exception exception)
            {
                _log.LogWarning("[popup] EnableInput failed: " + exception.Message);
            }
        }

        private void HideMatchingWindows()
        {
            foreach (GUIBaseWindow window in UnityEngine.Object.FindObjectsOfType<GUIBaseWindow>())
            {
                try
                {
                    if (window == null || !window.Visible || !IsPromoWindow(window.Name))
                        continue;

                    window.Visible = false;
                    _hidAPromoWindow = true;
                    DebugLog.Detail("[popup] hid promo window: " + window.Name +
                        " | InputDisabled=" + (Singleton<KeybindManager>.Current?.InputDisabled ?? false));
                }
                catch
                {
                    // best effort - one uncooperative window shouldn't stop the scan
                }
            }
        }

        private static bool IsPromoWindow(string windowName)
        {
            if (string.IsNullOrEmpty(windowName))
                return false;
            foreach (string fragment in NeverTouchNameFragments)
                if (windowName.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;
            foreach (string name in PromoWindowNames)
                if (string.Equals(windowName, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }
}
