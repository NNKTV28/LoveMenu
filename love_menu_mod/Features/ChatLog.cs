using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;
using VWW.CoreLibs.ClientAPI.Social;
using FlyMod.Core;
using FlyMod.UI;

namespace FlyMod.Features
{
    // Keeps the chat: every message your game receives (local, channels,
    // private) goes to BepInEx\LoveMenu\chat\<date>.log, and the latest ones
    // can be searched in the menu. Mention alerts: a notice (and a flashing
    // taskbar button when the game is in the background) when a message
    // contains your name or one of your words. Stays on this PC.
    internal class ChatLog
    {
        public class Line
        {
            public DateTime When;
            public string Channel;
            public string From;
            public string Text;
        }

        public static readonly ChatLog Instance = new ChatLog();

        public bool SaveToFile = true;
        public bool MentionAlerts = true;
        public string ExtraWords = "";          // comma separated
        public string Folder;
        public readonly List<Line> Recent = new List<Line>();

        private const int KeepRecent = 300;
        private static readonly Regex Tags = new Regex("<.*?>");
        private readonly object _lock = new object();
        private readonly List<Line> _incoming = new List<Line>();
        private bool _loggedShape;

        // From the patch; may run off the main thread, so only queue.
        public void Receive(ChatChannelMessageEventArgs message)
        {
            if (message == null)
                return;
            try
            {
                var line = new Line
                {
                    When = message.TimeStamp == default ? DateTime.Now : message.TimeStamp.ToLocalTime(),
                    Channel = ChannelName(message),
                    From = ParticipantName(message.Participant),
                    Text = Tags.Replace(message.Message ?? "", "").Trim(),
                };
                if (line.Text.Length == 0)
                    return;
                lock (_lock)
                    _incoming.Add(line);
            }
            catch (Exception exception)
            {
                DebugLog.Detail("Chat log could not read a message: " + exception.Message);
            }
        }

        public void Tick(string myName)
        {
            List<Line> batch;
            lock (_lock)
            {
                if (_incoming.Count == 0)
                    return;
                batch = new List<Line>(_incoming);
                _incoming.Clear();
            }
            var text = new System.Text.StringBuilder();
            foreach (Line line in batch)
            {
                Recent.Add(line);
                text.AppendLine(line.When.ToString("HH:mm:ss") + " [" + line.Channel + "] " + line.From + ": " + line.Text);
                if (MentionAlerts && IsMention(line, myName))
                {
                    Toasts.Show(line.From + " mentioned you: " + (line.Text.Length > 60 ? line.Text.Substring(0, 60) + "..." : line.Text));
                    FlashIfInBackground();
                }
            }
            if (Recent.Count > KeepRecent)
                Recent.RemoveRange(0, Recent.Count - KeepRecent);
            if (SaveToFile && Folder != null)
            {
                try
                {
                    Directory.CreateDirectory(Folder);
                    File.AppendAllText(Path.Combine(Folder, DateTime.Now.ToString("yyyy-MM-dd") + ".log"), text.ToString());
                }
                catch (Exception exception)
                {
                    DebugLog.Detail("Chat log could not write: " + exception.Message);
                }
            }
        }

        private bool IsMention(Line line, string myName)
        {
            if (!string.IsNullOrEmpty(myName) && string.Equals(line.From, myName, StringComparison.OrdinalIgnoreCase))
                return false;           // your own message
            if (!string.IsNullOrEmpty(myName) && line.Text.IndexOf(myName, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            foreach (string raw in ExtraWords.Split(','))
            {
                string word = raw.Trim();
                if (word.Length >= 2 && line.Text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        // The game's chat types come from another library; read their names
        // by property so a renamed field doesn't break the log.
        private string ParticipantName(object participant)
        {
            if (participant == null)
                return "?";
            if (!_loggedShape)
            {
                _loggedShape = true;
                var names = new List<string>();
                foreach (var property in participant.GetType().GetProperties())
                    names.Add(property.Name);
                DebugLog.Detail("Chat participant properties: " + string.Join(", ", names.ToArray()));
            }
            return FirstString(participant, "Name", "DisplayName", "PersonaName", "Nickname", "UserName") ?? participant.ToString();
        }

        private static string ChannelName(ChatChannelMessageEventArgs message)
        {
            if (message.Style == ChatChannelMessageEventArgs.ChatStyle.Local)
                return "Local";
            if (message.Style == ChatChannelMessageEventArgs.ChatStyle.Private)
                return "Private";
            object channel = Traverse.Create(message).Property("Channel").GetValue();
            return (channel != null ? FirstString(channel, "Name", "Title", "DisplayName") : null) ?? message.Style.ToString();
        }

        private static string FirstString(object target, params string[] names)
        {
            var traverse = Traverse.Create(target);
            foreach (string name in names)
            {
                if (!traverse.Property(name).PropertyExists() && !traverse.Field(name).FieldExists())
                    continue;
                string value = (traverse.Property(name).PropertyExists() ? traverse.Property(name).GetValue() : traverse.Field(name).GetValue()) as string;
                if (!string.IsNullOrEmpty(value))
                    return Tags.Replace(value, "").Trim();
            }
            return null;
        }

        public void OpenFolder()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + Folder + "\"");
            }
            catch
            {
                // best effort
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FlashInfo
        {
            public uint Size;
            public IntPtr Window;
            public uint Flags;
            public uint Count;
            public uint Timeout;
        }

        [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FlashInfo info);
        [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();

        private static IntPtr _window;

        private static void FlashIfInBackground()
        {
            if (Application.isFocused)
                return;
            try
            {
                if (_window == IntPtr.Zero)
                    _window = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                var info = new FlashInfo { Window = _window, Flags = 3 | 12, Count = 3 };  // caption + tray, until focused
                info.Size = (uint)Marshal.SizeOf(info);
                FlashWindowEx(ref info);
            }
            catch
            {
                // best effort
            }
        }
    }

    [HarmonyPatch(typeof(SocialManager), nameof(SocialManager.ReceiveChatMessage))]
    internal static class ChatLogPatch
    {
        static void Postfix(ChatChannelMessageEventArgs msg)
        {
            LagRecorder.CountChatMessage();
            ChatLog.Instance.Receive(msg);
        }
    }
}
