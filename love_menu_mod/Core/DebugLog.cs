using BepInEx.Logging;

namespace FlyMod.Core
{
    // Tiny shared logging accessor so any controller can log without
    // threading a ManualLogSource through every constructor.
    //
    // Detail() is for the chatty lines only useful when chasing a bug
    // (window events, popup handling, wing matching). They stay out of the
    // log unless "Detailed logging" is on in the menu.
    internal static class DebugLog
    {
        private static ManualLogSource _log;

        public static bool Verbose;

        public static void Init(ManualLogSource log) => _log = log;
        public static void Info(string message) => _log?.LogInfo(message);
        public static void Warn(string message) => _log?.LogWarning(message);

        public static void Detail(string message)
        {
            if (Verbose)
                _log?.LogInfo(message);
        }
    }
}
