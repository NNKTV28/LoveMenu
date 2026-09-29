using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using VWW.Clients.Curio.ClientJS;

namespace FlyMod.Core
{
    // Developer tool: saves the world scripts the game downloads and runs on
    // this PC (quests, quizzes, interactive objects - they aren't part of the
    // game's own code), plus the messages the server sends to them. Used to
    // see how a world feature works, for example whether a quiz's answers
    // are sent to the client or stay on the server.
    //
    // Off by default and never saved: it has to be switched on each session
    // (Crashes > Capture world scripts). Files go to BepInEx\LoveMenu\captured-scripts.
    internal static class WorldScriptCapture
    {
        private static bool _enabled;
        public static bool Enabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                DebugLog.Info("World script capture " + (value ? "ON" : "OFF") + " (script hook " +
                    (ScriptHookAttached ? "attached" : "NOT attached") + ", message hook " +
                    (MessageHookAttached ? "attached" : "NOT attached") + "), saving to " + Folder);
            }
        }
        public static bool ScriptHookAttached, MessageHookAttached;
        public static int ScriptsSeen, MessagesSeen;
        public static string Folder;
        public static int ScriptsSaved { get; private set; }
        public static int MessagesSaved { get; private set; }

        private static readonly Regex UnsafeFileChars = new Regex(@"[^A-Za-z0-9._-]+");

        public static void SaveScript(string code, string source)
        {
            ScriptsSeen++;
            if (!Enabled || string.IsNullOrEmpty(code) || Folder == null)
                return;
            try
            {
                Directory.CreateDirectory(Folder);
                string name = UnsafeFileChars.Replace(source ?? "script", "_").Trim('_');
                if (name.Length > 120)
                    name = name.Substring(name.Length - 120);
                if (!name.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                    name += ".js";
                File.WriteAllText(Path.Combine(Folder, name), "// source: " + source + Environment.NewLine + code);
                ScriptsSaved++;
            }
            catch (Exception exception)
            {
                DebugLog.Warn("Script capture failed: " + exception.Message);
            }
        }

        public static void SaveMessage(object scriptEvent)
        {
            MessagesSeen++;
            if (!Enabled || scriptEvent == null || Folder == null)
                return;
            // Every event is kept (script adds carry the script's address,
            // UI messages the quiz window's contents), not only "messages".
            string kind = scriptEvent.GetType().Name;
            try
            {
                // Dump every readable property: the event types differ
                // (script add, UI message, message, remove).
                var text = new System.Text.StringBuilder();
                text.AppendLine(DateTime.Now.ToString("HH:mm:ss") + "  " + kind);
                foreach (PropertyInfo property in scriptEvent.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    object value = null;
                    try { value = property.GetValue(scriptEvent, null); } catch { }
                    string shown = Describe(value);
                    if (value is System.Collections.IEnumerable list && !(value is string))
                    {
                        var items = new System.Collections.Generic.List<string>();
                        foreach (object item in list)
                            items.Add(Describe(item));
                        shown = "[" + string.Join(", ", items.ToArray()) + "]";
                    }
                    text.AppendLine("  " + property.Name + " = " + shown);
                }
                Directory.CreateDirectory(Folder);
                File.AppendAllText(Path.Combine(Folder, "messages.log"), text.ToString());
                MessagesSaved++;
            }
            catch (Exception exception)
            {
                DebugLog.Warn("Message capture failed: " + exception.Message);
            }
        }

        private static string Describe(object value)
        {
            // Message data arrives as a string or a JSON object (JToken), and
            // both print as their JSON text.
            return value == null ? "null" : value.ToString();
        }
    }

    // Every world script the game runs goes through the script host's
    // RunScript(script, source, identifier), inherited by JSClientBridge.
    [HarmonyPatch]
    internal static class WorldScriptRunPatch
    {
        private static MethodBase Target() =>
            AccessTools.Method(typeof(JSClientBridge), "RunScript", new[] { typeof(string), typeof(string), typeof(string) });

        // Skipped (instead of failing every other patch) if a game update renames it.
        private static bool Prepare() => WorldScriptCapture.ScriptHookAttached = Target() != null;

        private static MethodBase TargetMethod() => Target();

        private static void Prefix(string script, string source) => WorldScriptCapture.SaveScript(script, source);
    }

    // Server messages to running world scripts.
    [HarmonyPatch(typeof(JSClientBridgeManager), "HandleScriptEvent")]
    internal static class WorldScriptMessagePatch
    {
        private static bool Prepare() =>
            WorldScriptCapture.MessageHookAttached = AccessTools.Method(typeof(JSClientBridgeManager), "HandleScriptEvent") != null;

        private static void Prefix(object scriptEvent)
        {
            WorldScriptCapture.SaveMessage(scriptEvent);
            FlyMod.Features.QuizHelper.OnScriptEvent(scriptEvent);
        }
    }
}
