using System;
using System.Runtime.InteropServices;

namespace FlyMod.Core
{
    // Clicking inside the BepInEx console window starts a text selection
    // ("Seleccionar"/"Select" in the title). While it is active, Windows
    // blocks every write to that console - and the game writes its Unity log
    // there from the main thread, so the whole game freezes until the
    // selection ends. Confirmed from a hang dump: main thread parked in
    // KERNELBASE!WriteFile under Mono's logging. Turning QuickEdit off means
    // a click can no longer start that selection.
    internal static class ConsoleQuickEditGuard
    {
        private const int StdInputHandle = -10;
        private const uint EnableQuickEditMode = 0x0040;
        private const uint EnableExtendedFlags = 0x0080;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int handleId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr consoleHandle, out uint mode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode(IntPtr consoleHandle, uint mode);

        public static void Apply()
        {
            try
            {
                IntPtr inputHandle = GetStdHandle(StdInputHandle);
                if (!GetConsoleMode(inputHandle, out uint mode))
                    return; // no console window attached - nothing to freeze

                if ((mode & EnableQuickEditMode) == 0)
                    return;

                uint newMode = (mode & ~EnableQuickEditMode) | EnableExtendedFlags;
                if (SetConsoleMode(inputHandle, newMode))
                    DebugLog.Info("Console QuickEdit disabled - clicking the BepInEx console can no longer freeze the game");
            }
            catch (Exception exception)
            {
                DebugLog.Warn("Could not disable console QuickEdit: " + exception.Message);
            }
        }
    }
}
