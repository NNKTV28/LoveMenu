using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Logging;
using UnityEngine;

namespace FlyMod.Core
{
    // Player.log alone misses two things:
    //  1. It's only flushed to disk periodically and gets overwritten by
    //     the very next launch unless something archives it first - any
    //     managed exception is recorded here immediately, separately.
    //  2. A HANG (main thread stops responding, nothing actually faults)
    //     leaves no trace at all - Player.log just stops, no Windows Error
    //     Reporting entry, nothing. A background timer (NOT a Unity
    //     coroutine - those don't run if the main thread is stuck) watches
    //     for the main thread going quiet and writes a real minidump via
    //     dbghelp.dll the moment that's detected, so there's an actual
    //     crash dump to look at next time instead of a log that just ends.
    internal class CrashDumpController
    {
        private const double HangThresholdSeconds = 10.0;

        private ManualLogSource _logger;
        private string _dumpFolder;
        private Timer _watchdogTimer;
        private DateTime _lastHeartbeatUtc;
        private volatile bool _dumpWritten;

        public string DumpFolder => _dumpFolder;

        public void Init(ManualLogSource logger, string dumpFolder)
        {
            _logger = logger;
            _dumpFolder = dumpFolder;
            Directory.CreateDirectory(_dumpFolder);

            _lastHeartbeatUtc = DateTime.UtcNow;
            Application.logMessageReceivedThreaded += OnLogMessage;
            _watchdogTimer = new Timer(CheckForHang, null, 1000, 1000);
        }

        public void Shutdown()
        {
            Application.logMessageReceivedThreaded -= OnLogMessage;
            _watchdogTimer?.Dispose();
        }

        // Called from Update() every frame - if this stops advancing for
        // HangThresholdSeconds, the main thread is stuck.
        public void Heartbeat()
        {
            _lastHeartbeatUtc = DateTime.UtcNow;
        }

        private void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Exception && type != LogType.Error)
                return;
            try
            {
                string path = Path.Combine(_dumpFolder, "exceptions.log");
                string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + condition +
                    (string.IsNullOrEmpty(stackTrace) ? "" : "\n" + stackTrace) + "\n\n";
                File.AppendAllText(path, line);
            }
            catch
            {
                // best effort - a missing exception log entry isn't fatal
            }
        }

        private void CheckForHang(object state)
        {
            if (_dumpWritten)
                return;

            double secondsSinceHeartbeat = (DateTime.UtcNow - _lastHeartbeatUtc).TotalSeconds;
            if (secondsSinceHeartbeat < HangThresholdSeconds)
                return;

            _dumpWritten = true; // one dump per session - a stuck process won't recover to reset this anyway
            WriteMiniDump();
        }

        private void WriteMiniDump()
        {
            string path = Path.Combine(_dumpFolder, "hang_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".dmp");
            try
            {
                using (var fileStream = new FileStream(path, FileMode.Create))
                {
                    Process process = Process.GetCurrentProcess();
                    bool wrote = MiniDumpWriteDump(process.Handle, (uint)process.Id, fileStream.SafeFileHandle,
                        MiniDumpType.Normal | MiniDumpType.WithThreadInfo, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    _logger?.LogWarning("Hang detected (no heartbeat for " + HangThresholdSeconds + "s) - minidump " +
                        (wrote ? "written to " + path : "FAILED to write"));
                }
            }
            catch (Exception exception)
            {
                _logger?.LogWarning("Hang detected, but writing minidump failed: " + exception.Message);
            }
        }

        [Flags]
        private enum MiniDumpType : uint
        {
            Normal = 0x00000000,
            WithThreadInfo = 0x00001000,
        }

        [DllImport("dbghelp.dll", SetLastError = true)]
        private static extern bool MiniDumpWriteDump(IntPtr hProcess, uint processId, SafeHandle hFile,
            MiniDumpType dumpType, IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);
    }
}
