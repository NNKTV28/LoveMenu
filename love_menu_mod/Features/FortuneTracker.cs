using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using FlyMod.Core;
using FlyMod.UI;

namespace FlyMod.Features
{
    // Kiss of Fortune and the other fortune balls. Clicking a ball starts a
    // world script whose start-up data says how long until the next free
    // opening (TIMER_REMAIN, seconds), what a paid one costs and how many
    // are left today. This remembers that per ball, counts down, shows it on
    // Home and the Fortune page, and says when the free opening is ready.
    // Only reads; the ball itself (timer, prize) is decided by the server.
    //
    // The timer is only known after the ball has been clicked once; it is
    // saved, so it keeps counting across restarts.
    internal class FortuneTracker
    {
        public class Ball
        {
            public string Key;          // the ball's paid-opening listing ID
            public string Name = "Fortune ball";
            public string Scene = "";
            public Vector3 Position;
            public bool HasPosition;
            public DateTime FreeAtUtc;
            public int Cost;
            public int LeftToday = -1;
            public bool Notified;

            public TimeSpan Remaining
            {
                get
                {
                    TimeSpan left = FreeAtUtc - DateTime.UtcNow;
                    return left < TimeSpan.Zero ? TimeSpan.Zero : left;
                }
            }

            public bool FreeNow => Remaining == TimeSpan.Zero;
        }

        public static readonly FortuneTracker Instance = new FortuneTracker();
        private static readonly Regex DomId = new Regex(@"^\[DOM[A-Za-z]*:\s*(\d+)\]\s*(.*)$");

        public readonly List<Ball> Balls = new List<Ball>();
        public bool NotifyWhenFree = true;
        public bool Changed;            // tells the settings to save
        private float _nextCheck;
        private readonly List<(Ball Ball, long ObjectId)> _toLocate = new List<(Ball, long)>();

        // Soonest free opening, for Home.
        public Ball Next
        {
            get
            {
                Ball next = null;
                foreach (Ball ball in Balls)
                    if (next == null || ball.FreeAtUtc < next.FreeAtUtc)
                        next = ball;
                return next;
            }
        }

        // From WorldScriptMessagePatch, for every script the server starts.
        public void OnScriptEvent(object scriptEvent)
        {
            if (scriptEvent == null || scriptEvent.GetType().Name.IndexOf("Add", StringComparison.OrdinalIgnoreCase) < 0)
                return;
            try
            {
                var traverse = HarmonyLib.Traverse.Create(scriptEvent);
                string context = traverse.Property("Context").GetValue()?.ToString();
                if (string.IsNullOrEmpty(context) || context.IndexOf("TIMER_REMAIN", StringComparison.Ordinal) < 0 ||
                    context.IndexOf("ROLL_LISTING_ID", StringComparison.Ordinal) < 0)
                    return;
                JObject data = JObject.Parse(context);
                string key = (string)data["ROLL_LISTING_ID"];
                if (string.IsNullOrEmpty(key))
                    return;

                Ball ball = Balls.Find(b => b.Key == key);
                if (ball == null)
                {
                    ball = new Ball { Key = key };
                    Balls.Add(ball);
                }
                double seconds = data["TIMER_REMAIN"]?.Type == JTokenType.Integer || data["TIMER_REMAIN"]?.Type == JTokenType.Float
                    ? (double)data["TIMER_REMAIN"] : 0;
                ball.FreeAtUtc = DateTime.UtcNow.AddSeconds(Math.Max(0, seconds));
                ball.Cost = data["ROLL_COST"]?.Type == JTokenType.Integer ? (int)data["ROLL_COST"] : ball.Cost;
                ball.LeftToday = data["ROLL_DAY_LIMIT"]?.Type == JTokenType.Integer ? (int)data["ROLL_DAY_LIMIT"] : ball.LeftToday;
                ball.Scene = SceneManager.GetActiveScene().name;
                ball.Notified = seconds <= 0;
                Changed = true;

                if (long.TryParse(traverse.Property("ObjectID").GetValue()?.ToString(), out long objectId))
                    _toLocate.Add((ball, objectId));
                DebugLog.Info("Fortune ball " + key + ": free in " + FormatRemaining(ball.Remaining));
            }
            catch (Exception exception)
            {
                DebugLog.Detail("Fortune tracker could not read a ball: " + exception.Message);
            }
        }

        public void Tick()
        {
            if (Time.unscaledTime < _nextCheck)
                return;
            _nextCheck = Time.unscaledTime + 1f;

            if (_toLocate.Count > 0)
            {
                // Server objects are named "[DOMRenderable: 244] Kiss of Fortune".
                foreach (Transform transform in UnityEngine.Object.FindObjectsByType<Transform>(UnityEngine.FindObjectsSortMode.None))
                {
                    Match match = DomId.Match(transform.name);
                    if (!match.Success)
                        continue;
                    foreach (var pending in _toLocate)
                    {
                        if (match.Groups[1].Value != pending.ObjectId.ToString(CultureInfo.InvariantCulture))
                            continue;
                        pending.Ball.Position = transform.position;
                        pending.Ball.HasPosition = true;
                        string name = match.Groups[2].Value.Trim();
                        if (name.Length > 0)
                            pending.Ball.Name = name;
                        Changed = true;
                    }
                }
                _toLocate.Clear();
            }

            foreach (Ball ball in Balls)
            {
                if (ball.Notified || !ball.FreeNow)
                    continue;
                ball.Notified = true;
                Changed = true;
                if (NotifyWhenFree)
                    Toasts.Show(ball.Name + ": free opening ready");
            }
        }

        public bool InThisRoom(Ball ball) => ball.HasPosition && ball.Scene == SceneManager.GetActiveScene().name;

        public static string FormatRemaining(TimeSpan left) =>
            left == TimeSpan.Zero ? "free now" : ((int)left.TotalHours).ToString("00") + ":" + left.Minutes.ToString("00") + ":" + left.Seconds.ToString("00");

        // "key|name|scene|x,y,z|freeAtTicks|cost|left|notified;..."
        public string Encode()
        {
            var parts = new List<string>();
            foreach (Ball ball in Balls)
                parts.Add(string.Join("|", new[]
                {
                    ball.Key, Clean(ball.Name), Clean(ball.Scene),
                    ball.HasPosition ? ball.Position.x.ToString("0.##", CultureInfo.InvariantCulture) + "," +
                        ball.Position.y.ToString("0.##", CultureInfo.InvariantCulture) + "," +
                        ball.Position.z.ToString("0.##", CultureInfo.InvariantCulture) : "",
                    ball.FreeAtUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                    ball.Cost.ToString(CultureInfo.InvariantCulture),
                    ball.LeftToday.ToString(CultureInfo.InvariantCulture),
                    ball.Notified ? "1" : "0",
                }));
            return string.Join(";", parts.ToArray());
        }

        public void Decode(string text)
        {
            Balls.Clear();
            if (string.IsNullOrEmpty(text))
                return;
            foreach (string entry in text.Split(';'))
            {
                string[] f = entry.Split('|');
                if (f.Length < 8 || f[0].Length == 0)
                    continue;
                var ball = new Ball { Key = f[0], Name = f[1], Scene = f[2], Notified = f[7] == "1" };
                string[] xyz = f[3].Split(',');
                if (xyz.Length == 3 && float.TryParse(xyz[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                    float.TryParse(xyz[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                    float.TryParse(xyz[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                {
                    ball.Position = new Vector3(x, y, z);
                    ball.HasPosition = true;
                }
                if (long.TryParse(f[4], out long ticks))
                    ball.FreeAtUtc = new DateTime(ticks, DateTimeKind.Utc);
                int.TryParse(f[5], out ball.Cost);
                if (int.TryParse(f[6], out int left))
                    ball.LeftToday = left;
                Balls.Add(ball);
            }
        }

        public void Forget(Ball ball)
        {
            Balls.Remove(ball);
            Changed = true;
        }

        private static string Clean(string text) => (text ?? "").Replace("|", "/").Replace(";", ",");
    }
}
