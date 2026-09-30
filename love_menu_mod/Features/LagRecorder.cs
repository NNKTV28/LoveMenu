using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using VWW.Clients.Curio.Scene.Loaders;
using FlyMod.Core;

namespace FlyMod.Features
{
    // Lag spike recorder, for "the game lags" and "chat lags" reports: every
    // frame that takes much longer than usual is written down together with
    // what happened in it - objects (avatars, outfits, props) finishing
    // loading, a garbage collection, chat messages arriving, Love Menu's own
    // memory clean-up - so the cause shows up in numbers instead of guesses.
    // Saved to BepInEx\LoveMenu\lag.log; a summary shows in the Bug fixes tab.
    internal class LagRecorder
    {
        public class Spike
        {
            public DateTime When;
            public float Ms;
            public int ObjectsLoaded;
            public int Collections;
            public int ChatMessages;
            public string Scene;
            public string MenuWork = "";        // "collectionLogger 42 ms" when a menu feature took long

            public string Cause
            {
                get
                {
                    var causes = new List<string>();
                    if (MenuWork.Length > 0) causes.Add("Love Menu: " + MenuWork);
                    if (ObjectsLoaded > 0) causes.Add(ObjectsLoaded + " object(s) loaded");
                    if (Collections > 0) causes.Add("garbage collection");
                    if (ChatMessages > 0) causes.Add(ChatMessages + " chat message(s)");
                    return causes.Count > 0 ? string.Join(", ", causes.ToArray()) : "the game's own work or rendering (not the menu)";
                }
            }
        }

        public static readonly LagRecorder Instance = new LagRecorder();

        public bool Enabled;
        public string Folder;
        public readonly List<Spike> Recent = new List<Spike>();
        public int Total { get; private set; }
        public int WithLoading { get; private set; }
        public int WithGc { get; private set; }
        public int WithChat { get; private set; }
        public int Unknown { get; private set; }

        private const float MinSpikeMs = 50f;         // under 20 FPS for that frame
        private const float SpikeFactor = 2.5f;       // and 2.5x the usual frame
        private static int _objectsLoaded, _chatMessages;
        private int _lastCollections = -1;
        private float _typicalMs = 16f;

        // Time each menu feature takes, per frame, so a hitch caused by the
        // menu itself is named instead of hiding in "unknown".
        private const double MenuReportMs = 3.0;
        private static readonly Dictionary<string, double> MenuCosts = new Dictionary<string, double>();
        private static readonly double TicksToMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        public int WithMenu { get; private set; }

        public static long Begin() => System.Diagnostics.Stopwatch.GetTimestamp();

        public static void End(string feature, long started)
        {
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * TicksToMs;
            if (ms < MenuReportMs)
                return;
            MenuCosts.TryGetValue(feature, out double total);
            MenuCosts[feature] = total + ms;
        }

        public static void CountObjectLoaded() => Interlocked.Increment(ref _objectsLoaded);
        public static void CountChatMessage() => Interlocked.Increment(ref _chatMessages);

        // Called once per frame; looks at the frame that just ended.
        public void Tick()
        {
            float ms = Time.unscaledDeltaTime * 1000f;
            int objects = Interlocked.Exchange(ref _objectsLoaded, 0);
            int chat = Interlocked.Exchange(ref _chatMessages, 0);
            int collections = GC.CollectionCount(0);
            int gcDelta = _lastCollections < 0 ? 0 : collections - _lastCollections;
            _lastCollections = collections;
            string menuWork = "";
            if (MenuCosts.Count > 0)
            {
                var parts = new List<string>();
                foreach (var cost in MenuCosts)
                    parts.Add(cost.Key + " " + cost.Value.ToString("0") + " ms");
                menuWork = string.Join(", ", parts.ToArray());
                MenuCosts.Clear();
            }
            if (!Enabled || ms <= 0f)
                return;

            bool spike = ms >= MinSpikeMs && ms >= _typicalMs * SpikeFactor;
            if (!spike)
            {
                _typicalMs = Mathf.Lerp(_typicalMs, ms, 0.02f);
                return;
            }
            var entry = new Spike
            {
                When = DateTime.Now,
                Ms = ms,
                ObjectsLoaded = objects,
                Collections = gcDelta,
                ChatMessages = chat,
                Scene = SceneManager.GetActiveScene().name,
                MenuWork = menuWork,
            };
            Total++;
            if (menuWork.Length > 0) WithMenu++;
            if (objects > 0) WithLoading++;
            if (gcDelta > 0) WithGc++;
            if (chat > 0) WithChat++;
            if (objects == 0 && gcDelta == 0 && chat == 0 && menuWork.Length == 0) Unknown++;
            Recent.Insert(0, entry);
            if (Recent.Count > 30)
                Recent.RemoveAt(Recent.Count - 1);
            Write(entry);
        }

        public void Reset()
        {
            Recent.Clear();
            Total = WithLoading = WithGc = WithChat = Unknown = WithMenu = 0;
        }

        private void Write(Spike spike)
        {
            if (Folder == null)
                return;
            try
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(Path.Combine(Folder, "lag.log"),
                    spike.When.ToString("yyyy-MM-dd HH:mm:ss") + "\t" + spike.Ms.ToString("0") + " ms\t" + spike.Scene + "\t" + spike.Cause +
                    "\t(usual frame " + _typicalMs.ToString("0") + " ms)" + Environment.NewLine);
            }
            catch
            {
                // best effort
            }
        }
    }

    [HarmonyPatch(typeof(DOMLoader), nameof(DOMLoader.PostLoadObject))]
    internal static class LagRecorderLoadPatch
    {
        static void Postfix() => LagRecorder.CountObjectLoaded();
    }
}
