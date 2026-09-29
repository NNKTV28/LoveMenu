using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using VWW.Clients.Curio.Avatar;
using VWW.Clients.Curio.ClientJS;
using VWW.Clients.Curio.ClientUI.Elements;
using VWW.CoreLibs.Shared;

using FlyMod.Core;

namespace FlyMod.Features
{
    // Diagnostics for two open questions:
    //
    //  1. What actually happens when the popup's own X is clicked - so the
    //     auto-hide can trigger the real close path instead of bluntly
    //     setting Visible = false (which left an empty frame, and was the
    //     prime suspect for movement getting stuck).
    //
    //  2. Why movement gets stuck. CamControl.DisableAvatarMovement is only
    //     set true when the camera switches to FlyCam or DOMCamera - NOT by
    //     modal popups - so "can't move" may be the game's own free-camera
    //     mode left on, which this makes visible instead of guessable.
    internal class UiDebugController
    {
        private const float ScanIntervalSeconds = 1f;

        private readonly ManualLogSource _log;
        private readonly HashSet<GUIWebView> _hookedWebViews = new HashSet<GUIWebView>();
        private readonly HashSet<GUIBaseWindow> _seenWindows = new HashSet<GUIBaseWindow>();
        private float _timeSinceLastScan;

        private bool _lastMovementDisabled;
        private bool _lastInputDisabled;
        private string _lastCameraType = "";
        private bool _hasLoggedInitialState;

        public bool MovementDisabled { get; private set; }
        public bool InputDisabled { get; private set; }
        public string CameraType { get; private set; } = "—";

        public UiDebugController(ManualLogSource log)
        {
            _log = log;
        }

        public void Tick(float deltaTime)
        {
            ReadMovementState();
            LogMovementStateIfChanged();

            _timeSinceLastScan += deltaTime;
            if (_timeSinceLastScan < ScanIntervalSeconds)
                return;
            _timeSinceLastScan = 0f;
            HookAnyNewWebViews();
            LogAnyNewWindows();
        }

        // The promo popups turned out not to be GUIWebView instances at all
        // (only one webview exists, the shared WidgetManager host), so they
        // must be GUIBaseWindow instances created elsewhere. Logging every
        // window as it first appears identifies them by name, which is what
        // an auto-hide should actually target.
        private void LogAnyNewWindows()
        {
            foreach (GUIBaseWindow window in UnityEngine.Object.FindObjectsOfType<GUIBaseWindow>())
            {
                try
                {
                    if (window == null || !_seenWindows.Add(window))
                        continue;
                    DebugLog.Detail("[window] name=" + window.Name + " go=" + window.gameObject.name +
                        " visible=" + window.Visible + " layer=" + window.LayerName + " isHUD=" + window.IsHUD);

                    string windowName = window.Name;
                    window.OnShow += (sender, args) => DebugLog.Detail("[window] OnShow " + windowName);
                    window.OnHide += (sender, args) => DebugLog.Detail("[window] OnHide " + windowName +
                        " | InputDisabled=" + (Singleton<KeybindManager>.Current?.InputDisabled ?? false));
                }
                catch (Exception exception)
                {
                    _log.LogWarning("[window] hook failed: " + exception.Message);
                }
            }
        }

        private void ReadMovementState()
        {
            try
            {
                MovementDisabled = CamControl.DisableAvatarMovement;
                CameraType = CamControl.Self != null ? CamControl.Self.CameraType.ToString() : "—";
                InputDisabled = Singleton<KeybindManager>.Current?.InputDisabled ?? false;
            }
            catch
            {
                // best effort - these are read every frame, never worth throwing over
            }
        }

        private void LogMovementStateIfChanged()
        {
            bool changed = !_hasLoggedInitialState || MovementDisabled != _lastMovementDisabled ||
                InputDisabled != _lastInputDisabled || CameraType != _lastCameraType;
            if (!changed)
                return;

            _hasLoggedInitialState = true;
            _lastMovementDisabled = MovementDisabled;
            _lastInputDisabled = InputDisabled;
            _lastCameraType = CameraType;
            DebugLog.Detail("[movement state] DisableAvatarMovement=" + MovementDisabled +
                " InputDisabled=" + InputDisabled + " CameraType=" + CameraType);
        }

        private void HookAnyNewWebViews()
        {
            foreach (GUIWebView webView in UnityEngine.Object.FindObjectsOfType<GUIWebView>())
            {
                try
                {
                    if (webView == null || !_hookedWebViews.Add(webView))
                        continue;

                    string label = DescribeWebView(webView);
                    DebugLog.Detail("[popup] found webview: " + label);

                    webView.OnCloseRequested += (sender, args) =>
                        DebugLog.Detail("[popup] OnCloseRequested fired for " + label +
                            " | DisableAvatarMovement=" + CamControl.DisableAvatarMovement +
                            " InputDisabled=" + (Singleton<KeybindManager>.Current?.InputDisabled ?? false));

                    GUIBaseWindow window = webView.Window;
                    if (window == null)
                        continue;

                    window.OnHide += (sender, args) =>
                        DebugLog.Detail("[popup] Window.OnHide fired for " + label +
                            " | DisableAvatarMovement=" + CamControl.DisableAvatarMovement +
                            " InputDisabled=" + (Singleton<KeybindManager>.Current?.InputDisabled ?? false));
                    window.OnShow += (sender, args) =>
                        DebugLog.Detail("[popup] Window.OnShow fired for " + label);
                }
                catch (Exception exception)
                {
                    _log.LogWarning("[popup] hook failed: " + exception.Message);
                }
            }
        }

        private static string DescribeWebView(GUIWebView webView)
        {
            string url = string.IsNullOrEmpty(webView.CurrentUrl) ? "(no url)" : webView.CurrentUrl;
            if (url.Length > 90)
                url = url.Substring(0, 90) + "...";
            string windowName = webView.Window != null ? webView.Window.Name : "(no window)";
            return webView.GetType().Name + " url=" + url + " window=" + windowName + " go=" + webView.gameObject.name;
        }
    }
}
