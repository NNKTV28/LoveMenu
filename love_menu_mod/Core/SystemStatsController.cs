using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;

namespace FlyMod.Core
{
    // Cheap, per-frame-safe system stats for the Home dashboard. FPS is
    // smoothed with a simple exponential average so it doesn't jitter every
    // single frame; everything else is a direct, allocation-light read.
    // FPS/RAM are also sampled into small rolling history buffers (every
    // SampleIntervalSeconds, not every frame) so the dashboard can draw a
    // graph of recent history, not just an instantaneous number.
    internal class SystemStatsController
    {
        private const float SampleIntervalSeconds = 0.25f;
        private const int MaxHistorySamples = 120; // 30 seconds at the sample rate above

        private readonly Process _process = Process.GetCurrentProcess();
        private readonly IntPtr _processHandle;
        private readonly float _startRealtime = Time.realtimeSinceStartup;
        private float _timeSinceLastSample;

        public SystemStatsController()
        {
            _processHandle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, _process.Id);
        }

        public void Shutdown()
        {
            if (_processHandle != IntPtr.Zero)
                CloseHandle(_processHandle);
        }

        public float FramesPerSecond { get; private set; }
        public float FrameTimeMs { get; private set; }

        public readonly List<float> FpsHistory = new List<float>();
        public readonly List<float> RamHistoryMB = new List<float>();

        public void Tick()
        {
            float deltaTime = Time.unscaledDeltaTime;
            if (deltaTime <= 0f)
                return;

            float instantFps = 1f / deltaTime;
            FramesPerSecond = FramesPerSecond <= 0f ? instantFps : Mathf.Lerp(FramesPerSecond, instantFps, 0.1f);
            FrameTimeMs = deltaTime * 1000f;

            _timeSinceLastSample += deltaTime;
            if (_timeSinceLastSample < SampleIntervalSeconds)
                return;
            _timeSinceLastSample = 0f;

            AppendCapped(FpsHistory, FramesPerSecond);
            AppendCapped(RamHistoryMB, BytesToMB(WorkingSetBytes()));
        }

        private static void AppendCapped(List<float> history, float value)
        {
            history.Add(value);
            if (history.Count > MaxHistorySamples)
                history.RemoveAt(0);
        }

        // System.Diagnostics.Process.WorkingSet64 read back as 0 when tested
        // from inside BepInEx's Mono runtime - likely the process handle
        // Process.GetCurrentProcess() opens here doesn't carry the access
        // right that property needs internally. GetProcessMemoryInfo via a
        // handle we open ourselves with explicit query+read rights is the
        // lower-level primitive real crash/monitoring tools use instead,
        // and reads correctly regardless of that wrapper's quirks.
        public long WorkingSetBytes()
        {
            if (_processHandle == IntPtr.Zero)
                return 0;
            var counters = new ProcessMemoryCounters { cb = Marshal.SizeOf(typeof(ProcessMemoryCounters)) };
            return GetProcessMemoryInfo(_processHandle, out counters, counters.cb) ? (long)counters.WorkingSetSize : 0;
        }

        // GetAllocatedMemoryForGraphicsDriver only returns real data in a
        // Development Build - the game's shipped player build has profiler
        // instrumentation stripped, so this reliably reads back 0 here.
        // HasVramReading tells the UI to show "n/a" instead of a false 0.
        public bool HasVramReading { get; private set; } = true;

        public long GraphicsMemoryBytes()
        {
            long bytes = UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver();
            HasVramReading = bytes > 0;
            return bytes;
        }

        public long ManagedMemoryBytes() => GC.GetTotalMemory(false);

        private const uint ProcessQueryInformation = 0x0400;
        private const uint ProcessVmRead = 0x0010;

        [StructLayout(LayoutKind.Sequential, Size = 40)]
        private struct ProcessMemoryCounters
        {
            public int cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetProcessMemoryInfo(IntPtr hProcess, out ProcessMemoryCounters counters, int size);

        public string GameUptime()
        {
            TimeSpan elapsed = TimeSpan.FromSeconds(Time.realtimeSinceStartup - _startRealtime);
            return elapsed.Hours > 0 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"m\:ss");
        }

        public static float BytesToMB(long bytes) => bytes / 1024f / 1024f;
    }
}
