using System;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;
using FlyMod.Core;
using FlyMod.UI;

namespace FlyMod.Features
{
    // Memory and framerate levers.
    //
    // Why this exists: the player log shows "Unloading N unused Assets"
    // only at scene transitions. In a persistent hub, avatars stream in and
    // out constantly, and the assets of players who already left stay
    // resident until the next zone change - which is why RAM climbs into
    // the multi-GB range while standing in one busy city. Resources
    // .UnloadUnusedAssets is the engine's own way to reclaim exactly that,
    // just never called while you stay put.
    //
    // It is NOT free: an unload pass measured ~275ms in the log, which is a
    // visible hitch, so it's manual-by-default and rate-limited when auto.
    //
    // The graphics knobs are plain QualitySettings values, applied live and
    // restorable - nothing is patched or permanently altered.
    internal class PerformanceController
    {
        private const float AutoFreeCheckIntervalSeconds = 10f;
        private const float MinSecondsBetweenAutoFrees = 90f;

        private readonly ManualLogSource _log;
        private readonly SystemStatsController _systemStats;

        public bool AutoFreeEnabled;
        public float AutoFreeThresholdMB = 6000f;
        public string LastFreeResult = "not run yet";

        private float _timeSinceLastCheck;
        private float _secondsSinceLastAutoFree = float.MaxValue;

        // Captured the first time anything is changed so "Reset" can put
        // the game's own values back rather than guessing at defaults.
        private bool _capturedOriginals;
        private int _originalTextureLimit;
        private float _originalShadowDistance;
        private float _originalLodBias;

        public PerformanceController(ManualLogSource log, SystemStatsController systemStats)
        {
            _log = log;
            _systemStats = systemStats;
        }

        // Automatic freeing is off for good: in both freezes looked at (30 Sep
        // and 1 Oct) it had run shortly before, it froze the game for
        // 300-500 ms each time and freed nothing while in a crowded room.
        // Only the manual button remains.
        public void Tick(float deltaTime)
        {
            _secondsSinceLastAutoFree += deltaTime;
            if (!AutoFreeEnabled)
                return;

            _timeSinceLastCheck += deltaTime;
            if (_timeSinceLastCheck < AutoFreeCheckIntervalSeconds)
                return;
            _timeSinceLastCheck = 0f;

            if (_secondsSinceLastAutoFree < MinSecondsBetweenAutoFrees)
                return;

            float currentMB = SystemStatsController.BytesToMB(_systemStats.WorkingSetBytes());
            if (currentMB < AutoFreeThresholdMB)
                return;

            _secondsSinceLastAutoFree = 0f;
            float freed = FreeUnusedMemory();
            // Nothing to free (the RAM is in use, not leftovers): stop paying
            // the hitch every 90 s and wait 10 minutes before trying again.
            if (freed < MinUsefulFreeMB)
                _secondsSinceLastAutoFree = -(NothingFreedBackoffSeconds - MinSecondsBetweenAutoFrees);
        }

        private const float MinUsefulFreeMB = 100f;
        private const float NothingFreedBackoffSeconds = 600f;

        public float FreeUnusedMemory()
        {
            long beforeBytes = _systemStats.WorkingSetBytes();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            // UnloadUnusedAssets runs its own garbage collection and finishes
            // over the next frames, so the result is measured when it's done
            // (no extra GC.Collect or waiting on finalizers - both can stall
            // the main thread).
            LastFreeResult = "freeing...";
            AsyncOperation unload = Resources.UnloadUnusedAssets();
            unload.completed += _ =>
            {
                stopwatch.Stop();
                long afterBytes = _systemStats.WorkingSetBytes();
                float freedMB = SystemStatsController.BytesToMB(beforeBytes - afterBytes);
                LastFreeResult = freedMB > 0f
                    ? "freed " + freedMB.ToString("0") + " MB in " + stopwatch.ElapsedMilliseconds + " ms"
                    : "nothing to free (" + stopwatch.ElapsedMilliseconds + " ms)";
                _log.LogInfo("[performance] UnloadUnusedAssets: " + LastFreeResult +
                    " | before=" + SystemStatsController.BytesToMB(beforeBytes).ToString("0") + "MB" +
                    " after=" + SystemStatsController.BytesToMB(afterBytes).ToString("0") + "MB");
                Toasts.Show(LastFreeResult);
            };
            return 0f;
        }

        // 0 = full resolution, 1 = half, 2 = quarter. Dropping a step is the
        // single biggest RAM/VRAM saving available without touching the
        // game's own content.
        public int TextureQualityLimit
        {
            get => ReadTextureLimit();
            set
            {
                CaptureOriginalsOnce();
                WriteTextureLimit(Mathf.Clamp(value, 0, 3));
            }
        }

        public float ShadowDistance
        {
            get => QualitySettings.shadowDistance;
            set
            {
                CaptureOriginalsOnce();
                QualitySettings.shadowDistance = value;
            }
        }

        public float LodBias
        {
            get => QualitySettings.lodBias;
            set
            {
                CaptureOriginalsOnce();
                QualitySettings.lodBias = value;
            }
        }

        public void ResetGraphicsToGameDefaults()
        {
            if (!_capturedOriginals)
                return;
            WriteTextureLimit(_originalTextureLimit);
            QualitySettings.shadowDistance = _originalShadowDistance;
            QualitySettings.lodBias = _originalLodBias;
            _capturedOriginals = false;
            _log.LogInfo("[performance] restored the game's original quality settings");
        }

        // Only graphics the player actually changed are saved and put back
        // next session; untouched ones stay whatever the game chooses.
        public bool GraphicsChangedByPlayer => _capturedOriginals;

        private void CaptureOriginalsOnce()
        {
            if (_capturedOriginals)
                return;
            _originalTextureLimit = ReadTextureLimit();
            _originalShadowDistance = QualitySettings.shadowDistance;
            _originalLodBias = QualitySettings.lodBias;
            _capturedOriginals = true;
        }

        // Unity 6 renamed masterTextureLimit to globalTextureMipmapLimit and
        // the old name may be compiled out, so this goes through reflection
        // rather than binding to whichever name this build happens to have.
        private static PropertyInfo _textureLimitProperty;

        private static PropertyInfo TextureLimitProperty
        {
            get
            {
                if (_textureLimitProperty != null)
                    return _textureLimitProperty;
                _textureLimitProperty =
                    typeof(QualitySettings).GetProperty("globalTextureMipmapLimit", BindingFlags.Public | BindingFlags.Static) ??
                    typeof(QualitySettings).GetProperty("masterTextureLimit", BindingFlags.Public | BindingFlags.Static);
                return _textureLimitProperty;
            }
        }

        private static int ReadTextureLimit()
        {
            try
            {
                return TextureLimitProperty != null ? (int)TextureLimitProperty.GetValue(null) : 0;
            }
            catch
            {
                return 0;
            }
        }

        private void WriteTextureLimit(int value)
        {
            try
            {
                TextureLimitProperty?.SetValue(null, value);
            }
            catch (Exception exception)
            {
                _log.LogWarning("[performance] could not set texture limit: " + exception.Message);
            }
        }
    }
}
