using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FlyMod.Core
{
    // When the game freezes, the minidump shows the main thread waiting on a
    // C# lock, but the C# part of every stack is unnamed there. This names
    // it while the process is still alive: each thread is paused for a
    // moment to read where its stack is, then its stack memory is scanned for
    // return addresses, and Mono (the game's C# runtime) says which method
    // each one belongs to. A raw scan can list a few stale methods too, but
    // the top entries are where the thread is stuck.
    //
    // Only runs after a freeze has been detected, from the watchdog thread.
    internal static class HangStacks
    {
        private const string Mono = "mono-2.0-bdwgc.dll";

        [DllImport(Mono)] private static extern IntPtr mono_get_root_domain();
        [DllImport(Mono)] private static extern IntPtr mono_jit_info_table_find(IntPtr domain, IntPtr address);
        [DllImport(Mono)] private static extern IntPtr mono_jit_info_get_method(IntPtr jitInfo);
        [DllImport(Mono)] private static extern IntPtr mono_method_full_name(IntPtr method, bool signature);

        [DllImport("kernel32.dll")] private static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);
        [DllImport("kernel32.dll")] private static extern uint SuspendThread(IntPtr thread);
        [DllImport("kernel32.dll")] private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll")] private static extern bool GetThreadContext(IntPtr thread, IntPtr context);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll")] private static extern UIntPtr VirtualQuery(IntPtr address, out MemoryInfo info, UIntPtr length);

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryInfo
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public IntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }

        private const uint ThreadSuspendResume = 0x0002, ThreadGetContext = 0x0008, ThreadQueryInformation = 0x0040;
        private const int ContextSize = 1232;            // x64 CONTEXT
        private const int ContextFlagsOffset = 0x30;
        private const int RspOffset = 0x98;
        private const int RipOffset = 0xF8;
        private const uint ContextControl = 0x00100001;
        private const int MaxScanBytes = 256 * 1024;
        private const int MaxFramesPerThread = 25;

        public static uint MainThreadId;

        // Call from the main thread at startup.
        public static void RememberMainThread() => MainThreadId = GetCurrentThreadId();

        public static string Describe()
        {
            // First only read registers (threads paused as briefly as
            // possible, nothing allocated meanwhile); names come after.
            var spots = new List<(uint Id, IntPtr Rip, IntPtr Rsp)>();
            uint self = GetCurrentThreadId();
            IntPtr raw = Marshal.AllocHGlobal(ContextSize + 16);
            try
            {
                IntPtr context = new IntPtr((raw.ToInt64() + 15) & ~15L);
                foreach (ProcessThread thread in Process.GetCurrentProcess().Threads)
                {
                    uint id = (uint)thread.Id;
                    if (id == self)
                        continue;
                    IntPtr handle = OpenThread(ThreadSuspendResume | ThreadGetContext | ThreadQueryInformation, false, id);
                    if (handle == IntPtr.Zero)
                        continue;
                    try
                    {
                        if (SuspendThread(handle) == uint.MaxValue)
                            continue;
                        Marshal.WriteInt32(context, ContextFlagsOffset, unchecked((int)ContextControl));
                        bool ok = GetThreadContext(handle, context);
                        ResumeThread(handle);
                        if (ok)
                            spots.Add((id, Marshal.ReadIntPtr(context, RipOffset), Marshal.ReadIntPtr(context, RspOffset)));
                    }
                    finally
                    {
                        CloseHandle(handle);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(raw);
            }

            IntPtr domain = mono_get_root_domain();
            var text = new StringBuilder();
            // Main thread first, then only threads running C# code.
            spots.Sort((a, b) => (b.Id == MainThreadId).CompareTo(a.Id == MainThreadId));
            foreach (var spot in spots)
            {
                List<string> methods = ScanStack(domain, spot.Rip, spot.Rsp);
                if (methods.Count == 0 && spot.Id != MainThreadId)
                    continue;
                text.AppendLine((spot.Id == MainThreadId ? "MAIN THREAD " : "thread ") + spot.Id + ":");
                if (methods.Count == 0)
                    text.AppendLine("    (no C# methods found - stuck in native code)");
                foreach (string method in methods)
                    text.AppendLine("    " + method);
            }
            return text.ToString();
        }

        private static List<string> ScanStack(IntPtr domain, IntPtr rip, IntPtr rsp)
        {
            var methods = new List<string>();
            string last = null;
            void Add(IntPtr address)
            {
                IntPtr info = mono_jit_info_table_find(domain, address);
                if (info == IntPtr.Zero)
                    return;
                IntPtr method = mono_jit_info_get_method(info);
                if (method == IntPtr.Zero)
                    return;
                string name = Marshal.PtrToStringAnsi(mono_method_full_name(method, false));
                if (string.IsNullOrEmpty(name) || name == last)
                    return;
                last = name;
                methods.Add(name);
            }

            Add(rip);
            if (VirtualQuery(rsp, out MemoryInfo region, (UIntPtr)Marshal.SizeOf(typeof(MemoryInfo))) == UIntPtr.Zero)
                return methods;
            long end = region.BaseAddress.ToInt64() + region.RegionSize.ToInt64();
            end = Math.Min(end, rsp.ToInt64() + MaxScanBytes);
            for (long at = rsp.ToInt64(); at + 8 <= end && methods.Count < MaxFramesPerThread; at += 8)
                Add(Marshal.ReadIntPtr(new IntPtr(at)));
            return methods;
        }
    }
}
