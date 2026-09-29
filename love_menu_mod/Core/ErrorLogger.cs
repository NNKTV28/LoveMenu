using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FlyMod.Core
{
    // Collects every error the game logs (the red "[Error : Unity Log]"
    // lines, with their stack traces) into BepInEx\LoveMenu\errors.log, so
    // they can be looked at and patched together instead of one screenshot
    // at a time.
    //
    // The same error often repeats hundreds of times, so errors are grouped
    // by message and first stack line: errors.log gets each distinct error
    // once, in full, the first time it shows up, and errors-summary.txt is
    // rewritten with every distinct error and how often it happened.
    // errors.log is started fresh each session; the previous one is kept
    // as errors-previous.log.
    internal static class ErrorLogger
    {
        private class Group
        {
            public string Message;
            public string FirstStackLine;
            public string Stack;
            public string Type;
            public string Scene;
            public DateTime First;
            public DateTime Last;
            public int Count;
        }

        private struct Pending
        {
            public string Message;
            public string Stack;
            public LogType Type;
            public DateTime When;
        }

        private const float FlushSeconds = 2f;
        private const int MaxGroups = 500;

        private static readonly object Lock = new object();
        private static readonly List<Pending> Queue = new List<Pending>();
        private static readonly Dictionary<string, Group> Groups = new Dictionary<string, Group>();
        private static string _folder, _logPath, _summaryPath;
        private static float _nextFlush;
        private static bool _summaryDirty;

        public static int Distinct => Groups.Count;
        public static int Total { get; private set; }
        public static string Folder => _folder;

        public static void Start(string folder)
        {
            _folder = folder;
            _logPath = Path.Combine(folder, "errors.log");
            _summaryPath = Path.Combine(folder, "errors-summary.txt");
            try
            {
                Directory.CreateDirectory(folder);
                if (File.Exists(_logPath))
                    File.Copy(_logPath, Path.Combine(folder, "errors-previous.log"), overwrite: true);
                File.WriteAllText(_logPath, "Love Menu error log - session started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine + Environment.NewLine);
            }
            catch (Exception exception)
            {
                DebugLog.Warn("Error logger could not start: " + exception.Message);
                return;
            }
            // Threaded: errors from loading threads are caught too. The
            // callback only queues; files are written from the main thread.
            Application.logMessageReceivedThreaded += OnLog;
        }

        private static void OnLog(string message, string stack, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)
                return;
            lock (Lock)
                Queue.Add(new Pending { Message = message ?? "", Stack = stack ?? "", Type = type, When = DateTime.Now });
        }

        public static void Tick()
        {
            if (_logPath == null || Time.unscaledTime < _nextFlush)
                return;
            _nextFlush = Time.unscaledTime + FlushSeconds;

            List<Pending> batch;
            lock (Lock)
            {
                if (Queue.Count == 0 && !_summaryDirty)
                    return;
                batch = new List<Pending>(Queue);
                Queue.Clear();
            }

            var newText = new StringBuilder();
            string scene = SceneManager.GetActiveScene().name;
            foreach (Pending error in batch)
            {
                Total++;
                string firstLine = FirstLine(error.Message);
                string firstStack = FirstGameFrame(error.Stack);
                // Numbers (object IDs, counts) are ignored when grouping, so
                // the same error on different objects counts as one.
                string key = System.Text.RegularExpressions.Regex.Replace(firstLine + "|" + firstStack, @"\d+", "#");
                if (Groups.TryGetValue(key, out Group group))
                {
                    group.Count++;
                    group.Last = error.When;
                }
                else if (Groups.Count < MaxGroups)
                {
                    group = new Group
                    {
                        Message = error.Message.Trim(),
                        FirstStackLine = firstStack,
                        Stack = error.Stack.Trim(),
                        Type = error.Type.ToString(),
                        Scene = scene,
                        First = error.When,
                        Last = error.When,
                        Count = 1,
                    };
                    Groups[key] = group;
                    newText.AppendLine("=== #" + Groups.Count + "  " + error.When.ToString("HH:mm:ss") + "  " + group.Type + "  (scene: " + scene + ")");
                    newText.AppendLine(group.Message);
                    if (group.Stack.Length > 0)
                        newText.AppendLine(group.Stack);
                    newText.AppendLine();
                }
                _summaryDirty = true;
            }

            try
            {
                if (newText.Length > 0)
                    File.AppendAllText(_logPath, newText.ToString());
                if (_summaryDirty)
                {
                    WriteSummary();
                    _summaryDirty = false;
                }
            }
            catch (Exception exception)
            {
                DebugLog.Detail("Error logger could not write: " + exception.Message);
            }
        }

        private static void WriteSummary()
        {
            var text = new StringBuilder();
            text.AppendLine("Love Menu error summary - " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            text.AppendLine(Total + " errors, " + Groups.Count + " distinct. Most frequent first; full details in errors.log.");
            text.AppendLine();
            int number = 0;
            foreach (Group group in Groups.Values.OrderByDescending(g => g.Count))
            {
                number++;
                text.AppendLine(group.Count.ToString().PadLeft(6) + "x  " + FirstLine(group.Message));
                if (group.FirstStackLine.Length > 0)
                    text.AppendLine("         at " + group.FirstStackLine.Trim());
                text.AppendLine("         first " + group.First.ToString("HH:mm:ss") + ", last " + group.Last.ToString("HH:mm:ss") + ", scene " + group.Scene);
            }
            File.WriteAllText(_summaryPath, text.ToString());
        }

        // The first stack line that names game code: native frames
        // ("0x00007ff9... (UnityPlayer)") and Unity's own logging calls are
        // the same for every error, so they tell nothing apart.
        private static string FirstGameFrame(string stack)
        {
            if (string.IsNullOrEmpty(stack))
                return "";
            foreach (string raw in stack.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("0x") || line.StartsWith("UnityEngine.Debug") ||
                    line.StartsWith("UnityEngine.Logger") || line.StartsWith("UnityEngine.StackTraceUtility") ||
                    line.StartsWith("Stack trace", StringComparison.OrdinalIgnoreCase))
                    continue;
                return line;
            }
            return "";
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            int end = text.IndexOfAny(new[] { '\r', '\n' });
            return (end < 0 ? text : text.Substring(0, end)).Trim();
        }

        public static void OpenFolder()
        {
            try
            {
                Directory.CreateDirectory(_folder);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + _folder + "\"");
            }
            catch
            {
                // best effort
            }
        }
    }
}
