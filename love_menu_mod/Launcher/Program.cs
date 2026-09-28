using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace LoveMenuLauncher
{
    // Standalone launcher: closes any running copy of the game (waiting for
    // a full exit - a relaunch fired too soon after can fail silently,
    // Steam's own session tracking gets confused), installs the embedded
    // plugin, starts the game fresh via Steam so BepInEx's doorstop hook
    // picks the plugin up, then streams BepInEx/LogOutput.log live in this
    // same console window instead of needing a separate log viewer.
    //
    // The plugin only activates when this launcher started the game: it
    // writes BepInEx/LoveMenu.launch just before launching, and the plugin
    // consumes it. A plain Steam launch leaves the menu inactive.
    //
    // Also archives logs persistently: BepInEx/LogOutput.log gets replaced
    // every launch, and Unity's own player log only keeps one prior session
    // (Player-prev.log), so anything not copied out before the next relaunch
    // is gone for good. Everything goes under Logs/ next to this exe,
    // marked with a launcher-initiated-close marker so a deliberate
    // redeploy-and-relaunch doesn't get mistaken for a real crash later.
    internal static class Program
    {
        private const string SteamAppId = "2692440";
        private const string GameProcessName = "Curio";
        private const string PluginResourceName = "FlyMod.dll";
        private const string LaunchMarkerFileName = "LoveMenu.launch";
        private const string BepInExResourceName = "BepInEx.zip";
        private const string BundledBepInExVersion = "5.4.23.5";

        private static string GameDirectory;
        private static string BepInExFolder;
        private static string BepInExLogPath;
        private static readonly string UnityLogFolder =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"..\LocalLow\The Virtual World Web Inc_\Curio");
        private static readonly string UnityCurrentLogPath = Path.Combine(UnityLogFolder, "Player.log");
        private static readonly string UnityPrevLogPath = Path.Combine(UnityLogFolder, "Player-prev.log");

        private static readonly string LogsArchiveFolder =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
        private static readonly string BepInExHistoryPath = Path.Combine(LogsArchiveFolder, "bepinex_history.log");
        private static readonly string SavedGameDirectoryPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LoveMenu.gamedir.txt");

        [STAThread]
        private static void Main(string[] args)
        {
            Console.Title = "Love Menu Launcher";
            PrintHeader();

            if (AnotherLauncherInstanceIsRunning())
            {
                ExitWithMessage("Another Love Menu Launcher window is already running.",
                    "Close that one first, then run this again.");
                return;
            }

            GameDirectory = ResolveGameDirectory(args);
            if (GameDirectory == null)
            {
                ExitWithMessage("Could not find the LoveCraft install.");
                return;
            }
            BepInExFolder = Path.Combine(GameDirectory, "BepInEx");
            BepInExLogPath = Path.Combine(BepInExFolder, "LogOutput.log");
            Console.WriteLine("Game folder: " + GameDirectory);

            // Diagnostic: show which folder would be used, change nothing.
            if (HasArgument(args, "--find-game"))
                return;

            if (!EnsureBepInExInstalled(args))
            {
                ExitWithMessage("BepInEx 5 is required. Nothing was changed in the game folder.");
                return;
            }

            // Install only: set everything up without touching a running game.
            if (HasArgument(args, "--setup-only"))
            {
                if (GameIsRunningFrom(GameDirectory))
                {
                    ExitWithMessage("Close the game first - it keeps the plugin file locked.");
                    return;
                }
                ExitWithMessage(InstallEmbeddedPlugin()
                    ? "Setup complete. Run this exe again without --setup-only to play."
                    : "Could not install the Love Menu plugin.");
                return;
            }

            Directory.CreateDirectory(LogsArchiveFolder);

            ArchiveUnityPlayerLogsBeforeTheyAreOverwritten();
            CloseExistingGameAndWaitForExit();
            if (!InstallEmbeddedPlugin())
            {
                ExitWithMessage("Could not install the Love Menu plugin.");
                return;
            }
            DeleteStaleBepInExLogFile();
            WriteLaunchMarker();
            AppendSessionMarker("NEW SESSION - launching game");
            LaunchGameViaSteam();
            WaitForGameProcessToStart();

            Console.WriteLine();
            Console.WriteLine("Live log (this window updates as the game runs, and is also");
            Console.WriteLine("being saved to Logs\\bepinex_history.log):");
            Console.WriteLine("-------------------------------------------------");
            TailLogForever();
        }

        // BepInEx 5.4.23.5 (x64) ships inside this exe as the official
        // release zip, so a first-time player needs nothing else. Asks
        // before writing into the game folder; --yes skips the question.
        private static bool EnsureBepInExInstalled(string[] args)
        {
            if (File.Exists(Path.Combine(BepInExFolder, "core", "BepInEx.dll")))
                return true;

            Console.WriteLine();
            Console.WriteLine("BepInEx 5 (the mod loader Love Menu needs) is not installed yet.");
            if (!HasArgument(args, "--yes") && !Console.IsInputRedirected)
            {
                Console.Write("Install BepInEx " + BundledBepInExVersion + " into the game folder now? [Y/n] ");
                string answer = (Console.ReadLine() ?? "").Trim();
                if (answer.Length > 0 && !answer.StartsWith("y", StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            try
            {
                int filesWritten = ExtractBundledBepInEx();
                Console.WriteLine("Installed BepInEx " + BundledBepInExVersion + " (" + filesWritten + " files).");
                return File.Exists(Path.Combine(BepInExFolder, "core", "BepInEx.dll"));
            }
            catch (Exception exception)
            {
                Console.WriteLine("BepInEx install failed: " + exception.Message);
                return false;
            }
        }

        private static int ExtractBundledBepInEx()
        {
            string gameRoot = Path.GetFullPath(GameDirectory).TrimEnd('\\') + "\\";
            int filesWritten = 0;
            using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(BepInExResourceName))
            {
                if (resource == null)
                    throw new InvalidOperationException("this launcher was built without BepInEx embedded");
                using (var archive = new ZipArchive(resource, ZipArchiveMode.Read))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        string targetPath = Path.GetFullPath(Path.Combine(gameRoot, entry.FullName));
                        // Never write outside the game folder, whatever the zip says.
                        if (!targetPath.StartsWith(gameRoot, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (entry.Name.Length == 0)
                        {
                            Directory.CreateDirectory(targetPath);
                            continue;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                        entry.ExtractToFile(targetPath, overwrite: true);
                        filesWritten++;
                    }
                }
            }
            Directory.CreateDirectory(Path.Combine(BepInExFolder, "plugins"));
            return filesWritten;
        }

        private static bool GameIsRunningFrom(string directory)
        {
            string target = Path.GetFullPath(directory).TrimEnd('\\');
            foreach (string runningDirectory in RunningGameDirectories())
                if (string.Equals(Path.GetFullPath(runningDirectory).TrimEnd('\\'), target, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        // Compared by our own process name, so a renamed release exe
        // (LoveMenu-1.0.exe) still counts as the same launcher.
        private static bool AnotherLauncherInstanceIsRunning() =>
            Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName).Length > 1;

        private static void ExitWithMessage(params string[] lines)
        {
            foreach (string line in lines)
                Console.WriteLine(line);
            if (Console.IsInputRedirected)
                return;
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey(true);
        }

        // Order: --game-dir argument, saved path, the running game, Steam's
        // own records, common folders on every drive, then ask the player.
        private static string ResolveGameDirectory(string[] args)
        {
            string argumentDirectory = ReadGameDirectoryArgument(args);
            if (argumentDirectory != null)
            {
                string normalized = NormalizeGameDirectory(argumentDirectory);
                if (normalized == null)
                {
                    Console.WriteLine("--game-dir does not point at a LoveCraft install: " + argumentDirectory);
                    return null;
                }
                SaveGameDirectory(normalized);
                Console.WriteLine("Using game folder from --game-dir.");
                return normalized;
            }

            if (HasArgument(args, "--reset-path"))
            {
                try { File.Delete(SavedGameDirectoryPath); } catch { }
                Console.WriteLine("Forgot the saved game folder.");
            }
            else
            {
                string savedDirectory = NormalizeGameDirectory(ReadSavedGameDirectory());
                if (savedDirectory != null)
                    return savedDirectory;
            }

            foreach (var source in AutomaticGameDirectoryCandidates())
            {
                string candidate = NormalizeGameDirectory(source.Path);
                if (candidate == null)
                    continue;
                Console.WriteLine("Found LoveCraft (" + source.Description + ").");
                return candidate;
            }

            string chosenDirectory = AskPlayerForGameDirectory();
            if (chosenDirectory != null)
                SaveGameDirectory(chosenDirectory);
            return chosenDirectory;
        }

        private static string ReadGameDirectoryArgument(string[] args)
        {
            for (int index = 0; index < args.Length; index++)
            {
                if (args[index].Equals("--game-dir", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
                    return args[index + 1];
                if (args[index].StartsWith("--game-dir=", StringComparison.OrdinalIgnoreCase))
                    return args[index].Substring("--game-dir=".Length);
            }
            return null;
        }

        private static bool HasArgument(string[] args, string name)
        {
            foreach (string argument in args)
                if (argument.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static IEnumerable<(string Path, string Description)> AutomaticGameDirectoryCandidates()
        {
            foreach (string runningGamePath in RunningGameDirectories())
                yield return (runningGamePath, "running game");

            foreach (string libraryFolder in FindSteamLibraryFolders())
                yield return (FindGameInSteamLibrary(libraryFolder), "Steam library " + libraryFolder);

            yield return (UninstallRegistryInstallLocation(), "Windows uninstall entry");

            foreach (string commonFolder in CommonInstallFolders())
                yield return (commonFolder, "common folder");
        }

        // Accepts the Application folder, the LoveCraft folder above it, or
        // Curio.exe itself, and returns the folder that holds Curio.exe.
        private static string NormalizeGameDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            path = path.Trim().Trim('"');
            try
            {
                if (File.Exists(path) && Path.GetFileName(path).Equals(GameProcessName + ".exe", StringComparison.OrdinalIgnoreCase))
                    return Path.GetDirectoryName(Path.GetFullPath(path));
                if (IsGameDirectory(path))
                    return Path.GetFullPath(path);
                string applicationFolder = Path.Combine(path, "Application");
                if (IsGameDirectory(applicationFolder))
                    return Path.GetFullPath(applicationFolder);
            }
            catch
            {
                // malformed path - treat as not found
            }
            return null;
        }

        private static string AskPlayerForGameDirectory()
        {
            Console.WriteLine();
            Console.WriteLine("Could not find LoveCraft automatically.");
            while (true)
            {
                Console.WriteLine("Press Enter to browse for Curio.exe, paste the game folder, or type Q to quit.");
                Console.WriteLine(@"  (usually ...\steamapps\common\LoveCraft\Application)");
                Console.Write("> ");
                string typed = (Console.ReadLine() ?? "q").Trim();
                if (typed.Equals("q", StringComparison.OrdinalIgnoreCase))
                    return null;

                string chosen = typed.Length == 0 ? BrowseForGameExecutable() : typed;
                if (chosen == null)
                    continue;

                string normalized = NormalizeGameDirectory(chosen);
                if (normalized != null)
                    return normalized;
                Console.WriteLine("No Curio.exe there: " + chosen);
            }
        }

        private static string BrowseForGameExecutable()
        {
            using (var dialog = new System.Windows.Forms.OpenFileDialog
            {
                Title = "Find Curio.exe (LoveCraft)",
                Filter = "LoveCraft (Curio.exe)|Curio.exe|Programs (*.exe)|*.exe",
                CheckFileExists = true,
            })
            {
                return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.FileName : null;
            }
        }

        private static void SaveGameDirectory(string directory)
        {
            try
            {
                File.WriteAllText(SavedGameDirectoryPath, directory);
            }
            catch
            {
                // not fatal - the player just gets asked again next time
            }
        }

        private static string ReadSavedGameDirectory()
        {
            try
            {
                return File.Exists(SavedGameDirectoryPath) ? File.ReadAllText(SavedGameDirectoryPath).Trim() : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsGameDirectory(string directory) =>
            !string.IsNullOrEmpty(directory) && File.Exists(Path.Combine(directory, GameProcessName + ".exe"));

        private static IEnumerable<string> RunningGameDirectories()
        {
            foreach (Process gameProcess in Process.GetProcessesByName(GameProcessName))
            {
                string directory = null;
                try
                {
                    directory = Path.GetDirectoryName(gameProcess.MainModule.FileName);
                }
                catch
                {
                    // access denied or already exited
                }
                if (directory != null)
                    yield return directory;
            }
        }

        private static IEnumerable<string> FindSteamLibraryFolders()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string steamPath in SteamInstallFolders())
            {
                if (!seen.Add(steamPath))
                    continue;
                yield return steamPath;

                string libraryFoldersFile = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(libraryFoldersFile))
                    continue;
                string libraryFoldersText;
                try { libraryFoldersText = File.ReadAllText(libraryFoldersFile); } catch { continue; }
                foreach (Match match in Regex.Matches(libraryFoldersText, "\"path\"\\s+\"([^\"]+)\""))
                {
                    string libraryFolder = match.Groups[1].Value.Replace(@"\\", @"\");
                    if (seen.Add(libraryFolder))
                        yield return libraryFolder;
                }
            }
        }

        private static IEnumerable<string> SteamInstallFolders()
        {
            string[] registryLocations =
            {
                @"HKEY_CURRENT_USER\Software\Valve\Steam|SteamPath",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam|InstallPath",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam|InstallPath",
            };
            foreach (string location in registryLocations)
            {
                string[] keyAndValue = location.Split('|');
                string steamPath = null;
                try { steamPath = Registry.GetValue(keyAndValue[0], keyAndValue[1], null) as string; } catch { }
                if (!string.IsNullOrEmpty(steamPath))
                    yield return steamPath.Replace('/', '\\').TrimEnd('\\');
            }
        }

        // appmanifest_<id>.acf names the install folder under steamapps/common.
        private static string FindGameInSteamLibrary(string libraryFolder)
        {
            string manifestPath = Path.Combine(libraryFolder, "steamapps", "appmanifest_" + SteamAppId + ".acf");
            if (!File.Exists(manifestPath))
                return null;
            Match installDir;
            try { installDir = Regex.Match(File.ReadAllText(manifestPath), "\"installdir\"\\s+\"([^\"]+)\""); } catch { return null; }
            if (!installDir.Success)
                return null;
            return Path.Combine(libraryFolder, "steamapps", "common", installDir.Groups[1].Value);
        }

        // Steam registers every installed game for Add/Remove Programs.
        private static string UninstallRegistryInstallLocation()
        {
            string[] uninstallKeys =
            {
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App " + SteamAppId,
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Steam App " + SteamAppId,
            };
            foreach (string key in uninstallKeys)
            {
                string installLocation = null;
                try { installLocation = Registry.GetValue(key, "InstallLocation", null) as string; } catch { }
                if (!string.IsNullOrEmpty(installLocation))
                    return installLocation;
            }
            return null;
        }

        // Last resort before asking: the usual Steam library spots on each drive.
        private static IEnumerable<string> CommonInstallFolders()
        {
            string[] librariesOnDrive =
            {
                "SteamLibrary", "Steam", @"Program Files (x86)\Steam", @"Program Files\Steam",
                @"Games\Steam", @"Games\SteamLibrary",
            };
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                    continue;
                foreach (string library in librariesOnDrive)
                    yield return Path.Combine(drive.RootDirectory.FullName, library, "steamapps", "common", "LoveCraft");
            }
        }

        // The plugin ships inside this exe, so one file is the whole install.
        // Only rewritten when it differs - the game must be closed by now,
        // or the old DLL is still locked.
        private static bool InstallEmbeddedPlugin()
        {
            string pluginsFolder = Path.Combine(BepInExFolder, "plugins");
            string targetPath = Path.Combine(pluginsFolder, PluginResourceName);
            try
            {
                byte[] embeddedPlugin;
                using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(PluginResourceName))
                {
                    if (resource == null)
                    {
                        Console.WriteLine("This launcher was built without the plugin embedded.");
                        return false;
                    }
                    using (var buffer = new MemoryStream())
                    {
                        resource.CopyTo(buffer);
                        embeddedPlugin = buffer.ToArray();
                    }
                }

                if (File.Exists(targetPath) && BytesEqual(File.ReadAllBytes(targetPath), embeddedPlugin))
                    return true;

                Directory.CreateDirectory(pluginsFolder);
                File.WriteAllBytes(targetPath, embeddedPlugin);
                Console.WriteLine("Installed plugin -> " + targetPath);
                return true;
            }
            catch (Exception exception)
            {
                Console.WriteLine("Plugin install failed: " + exception.Message);
                return false;
            }
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left.Length != right.Length)
                return false;
            for (int index = 0; index < left.Length; index++)
                if (left[index] != right[index])
                    return false;
            return true;
        }

        private static void WriteLaunchMarker()
        {
            try
            {
                File.WriteAllText(Path.Combine(BepInExFolder, LaunchMarkerFileName), DateTime.UtcNow.ToString("o"));
            }
            catch (Exception exception)
            {
                Console.WriteLine("Could not write launch marker - the menu will stay inactive: " + exception.Message);
            }
        }

        private static void PrintHeader()
        {
            Console.WriteLine("Love Menu Launcher");
            Console.WriteLine("===================");
            Console.WriteLine("by NNKtv28 - https://nikicoding.com");
            Console.WriteLine();
        }

        // Unity keeps only the current and the immediately-previous player
        // log - anything older is destroyed the moment a new session starts.
        // Copy both out, timestamped, before that happens.
        private static void ArchiveUnityPlayerLogsBeforeTheyAreOverwritten()
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            ArchiveUnityLogFile(UnityPrevLogPath, "unity_prev_" + timestamp + ".log");
            ArchiveUnityLogFile(UnityCurrentLogPath, "unity_last_" + timestamp + ".log");
        }

        private static void ArchiveUnityLogFile(string sourcePath, string archiveFileName)
        {
            try
            {
                if (!File.Exists(sourcePath))
                    return;
                File.Copy(sourcePath, Path.Combine(LogsArchiveFolder, archiveFileName), overwrite: true);
                Console.WriteLine("Archived " + Path.GetFileName(sourcePath) + " -> Logs\\" + archiveFileName);
            }
            catch (Exception exception)
            {
                Console.WriteLine("Could not archive " + sourcePath + ": " + exception.Message);
            }
        }

        private static void CloseExistingGameAndWaitForExit()
        {
            Process[] runningGameProcesses = Process.GetProcessesByName(GameProcessName);
            if (runningGameProcesses.Length == 0)
                return;

            Console.WriteLine("Closing the currently running game...");
            AppendSessionMarker("LAUNCHER closing the running game intentionally (redeploy/relaunch, not a crash)");

            foreach (Process gameProcess in runningGameProcesses)
            {
                try
                {
                    gameProcess.Kill();
                    gameProcess.WaitForExit(10000);
                }
                catch
                {
                    // best effort - if it's already gone, that's fine
                }
            }

            // A few extra seconds beyond the process actually exiting -
            // Steam's own session tracking needs a moment to notice too,
            // or the next launch can silently fail.
            Thread.Sleep(3000);
        }

        private static void DeleteStaleBepInExLogFile()
        {
            try
            {
                if (File.Exists(BepInExLogPath))
                    File.Delete(BepInExLogPath);
            }
            catch
            {
                // not fatal - we'll just start tailing from whatever's there
            }
        }

        private static void LaunchGameViaSteam()
        {
            Console.WriteLine("Launching game via Steam...");
            Process.Start(new ProcessStartInfo("steam://run/" + SteamAppId) { UseShellExecute = true });
        }

        private static void WaitForGameProcessToStart()
        {
            Console.Write("Waiting for the game process to appear");
            const int maxAttempts = 60;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (Process.GetProcessesByName(GameProcessName).Length > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("Game is running.");
                    return;
                }
                Console.Write(".");
                Thread.Sleep(2000);
            }
            Console.WriteLine();
            Console.WriteLine("Timed out waiting for the game to start - it may still be loading via Steam.");
        }

        private static void TailLogForever()
        {
            WaitForLogFileToExist();

            using (var sourceStream = new FileStream(BepInExLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sourceReader = new StreamReader(sourceStream))
            using (var historyStream = new FileStream(BepInExHistoryPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            using (var historyWriter = new StreamWriter(historyStream) { AutoFlush = true })
            {
                while (true)
                {
                    string line = sourceReader.ReadLine();
                    if (line == null)
                    {
                        Thread.Sleep(200);
                        continue;
                    }
                    PrintLogLineColored(line);
                    historyWriter.WriteLine(line);
                }
            }
        }

        private static void WaitForLogFileToExist()
        {
            while (!File.Exists(BepInExLogPath))
                Thread.Sleep(500);
        }

        private static void AppendSessionMarker(string message)
        {
            try
            {
                string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] === " + message + " ===";
                File.AppendAllText(BepInExHistoryPath, line + Environment.NewLine);
            }
            catch
            {
                // best effort - a missing marker isn't fatal to launching
            }
        }

        private static void PrintLogLineColored(string line)
        {
            ConsoleColor originalColor = Console.ForegroundColor;
            Console.ForegroundColor = ColorForLogLine(line);
            Console.WriteLine(line);
            Console.ForegroundColor = originalColor;
        }

        private static ConsoleColor ColorForLogLine(string line)
        {
            if (line.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0)
                return ConsoleColor.Red;
            if (line.IndexOf("Warning", StringComparison.OrdinalIgnoreCase) >= 0)
                return ConsoleColor.Yellow;
            if (line.IndexOf("Love Menu", StringComparison.Ordinal) >= 0)
                return ConsoleColor.Cyan;
            return ConsoleColor.Gray;
        }
    }
}
