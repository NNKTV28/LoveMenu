using System.Collections.Generic;
using HarmonyLib;
using Tenkoku.Effects;
using UnityEngine;
using UnityEngine.Rendering;
using VWW.Clients.Curio;
using VWW.Clients.Curio.Scene;

namespace FlyMod.Features
{
    // Rendering costs the game has no setting for, found in its code:
    //
    // - Mirrors (MirrorReflection) draw the whole room again from the
    //   mirror's side, every frame, with shadows.
    // - Rooms with reflection probes (ReflectionProbeUpdater) re-render every
    //   probe - six views of the room each - once a minute, a short hitch.
    // - Lamps and spotlights cast shadows whenever the quality level is above
    //   the lowest. A shadow-casting lamp draws the scene six more times.
    // - The sky (Tenkoku) adds sun shafts, sky blur and a temporal
    //   reprojection pass on top of the game's own post-processing.
    // - Every avatar and NPC is drawn however far away it is. They are all
    //   on the "Skin" layer, so a per-layer cull distance stops drawing
    //   (and shadowing) the ones beyond it.
    //
    // Each is a switch on the Rendering page, off by default.
    internal class RenderTweaks
    {
        public static bool CheaperMirrors;
        public static bool NoProbeRefresh;
        public bool LampShadowsOff;
        public bool SkyEffectsOff;
        public bool AvatarDistance;
        public const float AvatarDrawDistance = 50f;

        private const float ScanSeconds = 2f;
        private float _nextScan;
        private readonly Dictionary<Light, LightShadows> _lampShadows = new Dictionary<Light, LightShadows>();
        private readonly List<Behaviour> _skyEffectsTurnedOff = new List<Behaviour>();
        private Camera _culledCamera;
        private int _skinLayer = -1;

        public void Tick()
        {
            if (Time.unscaledTime < _nextScan)
                return;
            _nextScan = Time.unscaledTime + ScanSeconds;
            ApplyLampShadows();
            ApplySkyEffects();
            ApplyAvatarDistance();
        }

        // Switches take effect straight away instead of on the next scan.
        public void ApplyNow() => _nextScan = 0f;

        // --- lamp shadows ----------------------------------------------------

        private void ApplyLampShadows()
        {
            if (!LampShadowsOff)
            {
                foreach (var pair in _lampShadows)
                    if (pair.Key != null)
                        pair.Key.shadows = pair.Value;
                _lampShadows.Clear();
                return;
            }
            // Rescanned, because lights load later and the server can
            // switch a light's shadows back on.
            foreach (Light light in Object.FindObjectsOfType<Light>())
            {
                if (light.type == LightType.Directional || light.shadows == LightShadows.None)
                    continue;
                if (!_lampShadows.ContainsKey(light))
                    _lampShadows[light] = light.shadows;
                light.shadows = LightShadows.None;
            }
        }

        // --- sky effects -----------------------------------------------------

        private void ApplySkyEffects()
        {
            if (!SkyEffectsOff)
            {
                foreach (Behaviour effect in _skyEffectsTurnedOff)
                    if (effect != null)
                        effect.enabled = true;
                _skyEffectsTurnedOff.Clear();
                return;
            }
            Camera camera = Camera.main;
            if (camera == null)
                return;
            TurnOff(camera.GetComponent<TenkokuSunShafts>());
            TurnOff(camera.GetComponent<TenkokuSkyBlur>());
            TurnOff(camera.GetComponent<Tenkoku_TemporalReprojection>());
        }

        private void TurnOff(Behaviour effect)
        {
            if (effect == null || !effect.enabled)
                return;
            effect.enabled = false;
            if (!_skyEffectsTurnedOff.Contains(effect))
                _skyEffectsTurnedOff.Add(effect);
        }

        // --- avatar draw distance -------------------------------------------

        private void ApplyAvatarDistance()
        {
            if (_skinLayer < 0)
                _skinLayer = LayerMask.NameToLayer("Skin");
            if (_skinLayer < 0)
                return;

            Camera camera = Camera.main;
            if (_culledCamera != null && (_culledCamera != camera || !AvatarDistance))
            {
                SetSkinCullDistance(_culledCamera, 0f);
                _culledCamera = null;
            }
            if (!AvatarDistance || camera == null)
                return;
            SetSkinCullDistance(camera, AvatarDrawDistance);
            camera.layerCullSpherical = true;
            _culledCamera = camera;
        }

        private void SetSkinCullDistance(Camera camera, float distance)
        {
            float[] distances = camera.layerCullDistances;
            if (distances == null || distances.Length < 32)
                distances = new float[32];
            if (Mathf.Approximately(distances[_skinLayer], distance))
                return;
            distances[_skinLayer] = distance;
            camera.layerCullDistances = distances;
        }
    }

    // Draw each mirror every other frame (the other frame shows the last
    // picture) and without shadows in the reflection.
    [HarmonyPatch(typeof(MirrorReflection), nameof(MirrorReflection.OnWillRenderObject))]
    internal static class CheaperMirrorsPatch
    {
        static bool Prefix(out ShadowQuality __state)
        {
            __state = QualitySettings.shadows;
            if (!RenderTweaks.CheaperMirrors)
                return true;
            if ((Time.frameCount & 1) == 1)
                return false;
            QualitySettings.shadows = ShadowQuality.Disable;
            return true;
        }

        static void Postfix(ShadowQuality __state)
        {
            if (QualitySettings.shadows != __state)
                QualitySettings.shadows = __state;
        }
    }

    // Rooms with decals (DynamicDecals) redraw the scene one to three more
    // times with replacement shaders - for every camera that renders,
    // including mirror, water-reflection and probe cameras. This keeps those
    // passes for the main camera only; decals seen in reflections may look
    // slightly off.
    [HarmonyPatch]
    internal static class DecalsMainCameraOnlyPatch
    {
        public static bool Enabled;

        static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("LlockhamIndustries.Decals.CameraData"), "Update",
                new[] { typeof(Camera), AccessTools.TypeByName("LlockhamIndustries.Decals.DynamicDecals") });

        static bool Prepare() => TargetMethod() != null;

        static bool Prefix(Camera Camera) =>
            !Enabled || Camera == null || Camera.cameraType != CameraType.Game || Camera == Camera.main;
    }

    // Reflection probes are still drawn once when the room loads; this only
    // stops the once-a-minute redraw.
    [HarmonyPatch(typeof(ReflectionProbeUpdater), "UpdateProbes")]
    internal static class NoProbeRefreshPatch
    {
        static bool Prefix() => !RenderTweaks.NoProbeRefresh;
    }
}
