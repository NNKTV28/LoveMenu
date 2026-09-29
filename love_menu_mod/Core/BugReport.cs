using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using BepInEx;
using UnityEngine;

namespace FlyMod.Core
{
    // One zip with everything needed to look into a crash or bug, saved to
    // the desktop: the menu's and game's logs, collected errors, crash dumps,
    // settings and a short system summary. Nothing is sent anywhere - the
    // player shares the zip themselves. (The launcher's --report adds the
    // Windows crash entries, which only it can read.)
    internal static class BugReport
    {
        public static string LastPath { get; private set; }

        public static string Create()
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string path = Path.Combine(desktop, "LoveMenu-report-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm") + ".zip");
            string bepInEx = Paths.BepInExRootPath;
            string unityLogs = Path.GetDirectoryName(Application.consoleLogPath) ?? "";

            using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                Add(zip, Path.Combine(bepInEx, "LogOutput.log"), "bepinex/LogOutput.log");
                Add(zip, Path.Combine(bepInEx, "config", "local.flymod.cfg"), "bepinex/local.flymod.cfg");
                foreach (string name in new[] { "errors.log", "errors-previous.log", "errors-summary.txt", "graphics.txt", "graphics-last.txt" })
                    Add(zip, Path.Combine(bepInEx, "LoveMenu", name), "lovemenu/" + name);
                string dumps = Path.Combine(Paths.GameRootPath, "BepInEx", "CrashDumps");
                if (Directory.Exists(dumps))
                    foreach (string file in Directory.GetFiles(dumps))
                        Add(zip, file, "crashdumps/" + Path.GetFileName(file));
                foreach (string name in new[] { "Player.log", "Player-prev.log" })
                    Add(zip, Path.Combine(unityLogs, name), "unity/" + name);

                ZipArchiveEntry info = zip.CreateEntry("system.txt");
                using (var writer = new StreamWriter(info.Open(), Encoding.UTF8))
                    writer.Write(SystemSummary());
            }
            LastPath = path;
            DebugLog.Info("Crash report saved to " + path);
            return path;
        }

        // Copies even files the game still has open.
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
            catch (Exception exception)
            {
                DebugLog.Detail("Crash report skipped " + source + ": " + exception.Message);
            }
        }

        private static string SystemSummary()
        {
            var text = new StringBuilder();
            text.AppendLine("Love Menu " + FlyMod.UI.MenuUI.Version + " report, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            text.AppendLine("OS: " + SystemInfo.operatingSystem);
            text.AppendLine("CPU: " + SystemInfo.processorType + " (" + SystemInfo.processorCount + " threads)");
            text.AppendLine("RAM: " + SystemInfo.systemMemorySize + " MB");
            text.AppendLine("GPU: " + SystemInfo.graphicsDeviceName + " · " + SystemInfo.graphicsDeviceVersion);
            text.AppendLine("GPU driver/vendor: " + SystemInfo.graphicsDeviceVendor + " · VRAM " + SystemInfo.graphicsMemorySize + " MB");
            text.AppendLine("Graphics API running: " + SystemInfo.graphicsDeviceType + " (" + FlyMod.Features.GraphicsApiSetting.RunningThreading + ")");
            text.AppendLine("Screen: " + Screen.width + "x" + Screen.height + " @ " + Screen.currentResolution.refreshRateRatio.value.ToString("0") + " Hz");
            text.AppendLine("Quality level: " + QualitySettings.GetQualityLevel() + " · shadows " + QualitySettings.shadows + " · texture limit " + QualitySettings.globalTextureMipmapLimit);
            text.AppendLine("Game running for: " + TimeSpan.FromSeconds(Time.realtimeSinceStartup).ToString(@"hh\:mm\:ss"));
            text.AppendLine("Errors this session: " + ErrorLogger.Total + " (" + ErrorLogger.Distinct + " distinct)");
            return text.ToString();
        }
    }
}
