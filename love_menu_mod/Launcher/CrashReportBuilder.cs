using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace LoveMenuLauncher
{
    // "LoveMenu.exe --report": one zip on the desktop with the game's and
    // menu's logs, collected errors, crash dumps, settings, the launcher's
    // archived logs, and the Windows System-log entries that explain a
    // whole-PC crash (unexpected shutdowns, bluescreens, graphics driver
    // resets) from the last 3 days. Nothing is uploaded.
    internal static class CrashReportBuilder
    {
        private static readonly string[] CrashSources =
        {
            "Microsoft-Windows-Kernel-Power", "BugCheck", "EventLog", "nvlddmkm", "amdkmdag", "amdwddmg",
            "igfx", "Display", "Microsoft-Windows-WHEA-Logger", "volmgr",
        };

        public static string Create(string gameDirectory, string unityLogFolder, string launcherLogs)
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string path = Path.Combine(desktop, "LoveMenu-report-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm") + ".zip");
            try
            {
                string bepInEx = Path.Combine(gameDirectory, "BepInEx");
                using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create))
                {
                    Add(zip, Path.Combine(bepInEx, "LogOutput.log"), "bepinex/LogOutput.log");
                    Add(zip, Path.Combine(bepInEx, "config", "local.flymod.cfg"), "bepinex/local.flymod.cfg");
                    AddFolder(zip, Path.Combine(bepInEx, "LoveMenu"), "lovemenu", "*.log", "*.txt");
                    AddFolder(zip, Path.Combine(bepInEx, "CrashDumps"), "crashdumps", "*");
                    Add(zip, Path.Combine(unityLogFolder, "Player.log"), "unity/Player.log");
                    Add(zip, Path.Combine(unityLogFolder, "Player-prev.log"), "unity/Player-prev.log");
                    Add(zip, Path.Combine(launcherLogs, "bepinex_history.log"), "launcher-logs/bepinex_history.log");
                    AddNewest(zip, launcherLogs, "launcher-logs", "unity_*.log", 6);
                    AddFolder(zip, @"C:\Windows\Minidump", "windows-minidump", "*.dmp");

                    ZipArchiveEntry events = zip.CreateEntry("windows-crash-events.txt");
                    using (var writer = new StreamWriter(events.Open(), Encoding.UTF8))
                        writer.Write(WindowsCrashEvents());
                }
                return "Crash report saved to your desktop: " + Path.GetFileName(path) + ". Send that file.";
            }
            catch (Exception exception)
            {
                return "Could not make the crash report: " + exception.Message;
            }
        }

        private static void AddFolder(ZipArchive zip, string folder, string entryFolder, params string[] patterns)
        {
            try
            {
                if (!Directory.Exists(folder))
                    return;
                foreach (string pattern in patterns)
                    foreach (string file in Directory.GetFiles(folder, pattern))
                        Add(zip, file, entryFolder + "/" + Path.GetFileName(file));
            }
            catch
            {
                // e.g. Minidump needs admin rights - skip it
            }
        }

        // The launcher keeps every session's Unity log; the last few are enough.
        private static void AddNewest(ZipArchive zip, string folder, string entryFolder, string pattern, int count)
        {
            try
            {
                if (!Directory.Exists(folder))
                    return;
                string[] files = Directory.GetFiles(folder, pattern);
                Array.Sort(files, (a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
                for (int i = 0; i < files.Length && i < count; i++)
                    Add(zip, files[i], entryFolder + "/" + Path.GetFileName(files[i]));
            }
            catch
            {
                // best effort
            }
        }

        private static void Add(ZipArchive zip, string source, string entryName)
        {
            try
            {
                if (!File.Exists(source))
                    return;
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (Stream output = zip.CreateEntry(entryName).Open())
                    input.CopyTo(output);
            }
            catch
            {
                // locked or unreadable - skip it
            }
        }

        // Newest first; stops at entries older than 3 days.
        private static string WindowsCrashEvents()
        {
            var text = new StringBuilder();
            text.AppendLine("Windows System log, crash-related entries from the last 3 days (newest first)");
            text.AppendLine("Kernel-Power 41 = PC lost power or hard-reset; BugCheck 1001 = bluescreen (stop code inside);");
            text.AppendLine("nvlddmkm / amdkmdag / Display = graphics driver stopped responding; WHEA = hardware error.");
            text.AppendLine();
            DateTime since = DateTime.Now.AddDays(-3);
            try
            {
                using (var log = new EventLog("System"))
                {
                    int found = 0;
                    for (int i = log.Entries.Count - 1; i >= 0 && found < 200; i--)
                    {
                        EventLogEntry entry = log.Entries[i];
                        if (entry.TimeGenerated < since)
                            break;
                        bool crashSource = Array.Exists(CrashSources, s => entry.Source.StartsWith(s, StringComparison.OrdinalIgnoreCase));
                        bool unexpectedShutdown = entry.Source == "EventLog" && (entry.InstanceId & 0xFFFF) == 6008;
                        if (!crashSource || (entry.Source == "EventLog" && !unexpectedShutdown))
                            continue;
                        if (entry.EntryType != EventLogEntryType.Error && entry.EntryType != EventLogEntryType.Warning && !unexpectedShutdown)
                            continue;
                        found++;
                        text.AppendLine(entry.TimeGenerated.ToString("yyyy-MM-dd HH:mm:ss") + "  " + entry.EntryType + "  " +
                            entry.Source + " " + (entry.InstanceId & 0xFFFF));
                        text.AppendLine("  " + (entry.Message ?? "").Replace("\r\n", "\n  ").Trim());
                        text.AppendLine();
                    }
                    if (found == 0)
                        text.AppendLine("None found.");
                }
            }
            catch (Exception exception)
            {
                text.AppendLine("Could not read the Windows event log: " + exception.Message);
            }
            return text.ToString();
        }
    }
}
