using System.Collections.Generic;
using CameraFX;
using UnityEngine;
using VWW.Clients.Curio.GUI;
using FlyMod.Core;
using GameSettings = VWW.Clients.Curio.Settings;

namespace FlyMod.Features
{
    // Camera tab. Everything here goes through the game's own switches:
    //  - FOV: CameraFXManager.CameraFOV / CameraFOVControl (what world
    //    scripts use), and ResetFOV() to hand control back.
    //  - Zoom-out limit: the game's own CameraMaxDistance preference, which
    //    it clamps to 10-70 and saves itself.
    //  - Screenshot mode: GUIManager.HidingUI, the game's Hide UI toggle
    //    (HUD, chat and the world canvas with name tags), plus our own menu
    //    and overlay, and optionally the player's avatar.
    internal class CameraController
    {
        public const float DefaultFov = 65f;
        public const int MinZoomDistance = 10;
        public const int MaxZoomDistance = 70;

        private readonly PlayerContext _playerContext;
        private bool _fovOverrideActive;
        private readonly List<Renderer> _hiddenAvatarRenderers = new List<Renderer>();

        public float Fov = DefaultFov;
        public bool HideOwnAvatarInScreenshots;
        public bool ScreenshotMode { get; private set; }

        public CameraController(PlayerContext playerContext)
        {
            _playerContext = playerContext;
        }

        public bool FovChanged => Mathf.Abs(Fov - DefaultFov) > 0.5f;

        public void Tick()
        {
            CameraFXManager cameraEffects = CameraFXManager.Instance;
            if (cameraEffects == null)
                return;

            if (FovChanged)
            {
                cameraEffects.CameraFOVControl = true;
                cameraEffects.CameraFOV = Fov;
                _fovOverrideActive = true;
            }
            else if (_fovOverrideActive)
            {
                cameraEffects.ResetFOV();
                _fovOverrideActive = false;
            }
        }

        public void ResetFov() => Fov = DefaultFov;

        public int ZoomDistance
        {
            get => GameSettings.Default != null ? GameSettings.Default.CameraMaxDistance : MinZoomDistance;
            set
            {
                if (GameSettings.Default != null)
                    GameSettings.Default.CameraMaxDistance = Mathf.Clamp(value, MinZoomDistance, MaxZoomDistance);
            }
        }

        public void SetScreenshotMode(bool enable)
        {
            if (enable == ScreenshotMode)
                return;
            ScreenshotMode = enable;

            if (GUIManager.Instance != null)
                GUIManager.Instance.HidingUI = enable;

            if (enable && HideOwnAvatarInScreenshots)
                HideAvatar();
            else
                RestoreAvatar();
        }

        private void HideAvatar()
        {
            _hiddenAvatarRenderers.Clear();
            if (_playerContext.Avatar == null)
                return;
            foreach (Renderer renderer in _playerContext.Avatar.transform.root.GetComponentsInChildren<Renderer>())
            {
                if (renderer == null || !renderer.enabled)
                    continue;
                renderer.enabled = false;
                _hiddenAvatarRenderers.Add(renderer);
            }
        }

        private void RestoreAvatar()
        {
            foreach (Renderer renderer in _hiddenAvatarRenderers)
                if (renderer != null)
                    renderer.enabled = true;
            _hiddenAvatarRenderers.Clear();
        }

        public void Shutdown()
        {
            SetScreenshotMode(false);
            if (_fovOverrideActive && CameraFXManager.Instance != null)
                CameraFXManager.Instance.ResetFOV();
        }
    }
}
