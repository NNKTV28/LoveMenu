using BepInEx.Logging;

namespace FlyMod.Core
{
    // Tiny shared logging accessor so any controller can log without
    // threading a ManualLogSource through every constructor.
    internal static class DebugLog
    {
        private static ManualLogSource _log;

        public static void Init(ManualLogSource log) => _log = log;
        public static void Info(string message) => _log?.LogInfo(message);
        public static void Warn(string message) => _log?.LogWarning(message);
    }
}
