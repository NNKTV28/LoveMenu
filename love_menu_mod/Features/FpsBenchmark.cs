using System;
using System.Collections.Generic;
using UnityEngine;
using FlyMod.UI;

namespace FlyMod.Features
{
    // Measures the frame rate over 30 seconds: the average, and the "1%
    // low" (the average of the slowest 1% of frames - what stutters feel
    // like). Run it once with a setting on and once with it off, standing
    // in the same spot, to see what actually helps.
    internal static class FpsBenchmark
    {
        public class Result
        {
            public string Label;
            public float AverageFps;
            public float OnePercentLowFps;
            public DateTime When;
        }

        public const float DurationSeconds = 30f;

        public static readonly List<Result> Results = new List<Result>();
        public static bool Running { get; private set; }
        public static float Progress => Running ? Mathf.Clamp01(_elapsed / DurationSeconds) : 0f;

        private static readonly List<float> Frames = new List<float>(4096);
        private static float _elapsed;
        private static string _label;

        public static void Start(string label)
        {
            Frames.Clear();
            _elapsed = 0f;
            _label = label;
            Running = true;
            Toasts.Show("Benchmark started - stay put for " + DurationSeconds.ToString("0") + " s");
        }

        public static void Cancel() => Running = false;

        public static void Tick(float unscaledDeltaTime)
        {
            if (!Running || unscaledDeltaTime <= 0f)
                return;
            // The first half second after starting often includes the click itself.
            _elapsed += unscaledDeltaTime;
            if (_elapsed > 0.5f)
                Frames.Add(unscaledDeltaTime);
            if (_elapsed < DurationSeconds)
                return;

            Running = false;
            if (Frames.Count < 10)
                return;
            float total = 0f;
            foreach (float frame in Frames)
                total += frame;
            var sorted = new List<float>(Frames);
            sorted.Sort((a, b) => b.CompareTo(a));
            int slowCount = Mathf.Max(1, sorted.Count / 100);
            float slowTotal = 0f;
            for (int i = 0; i < slowCount; i++)
                slowTotal += sorted[i];

            var result = new Result
            {
                Label = _label,
                AverageFps = Frames.Count / total,
                OnePercentLowFps = slowCount / slowTotal,
                When = DateTime.Now,
            };
            Results.Insert(0, result);
            if (Results.Count > 8)
                Results.RemoveAt(Results.Count - 1);
            Toasts.Show("Benchmark: " + result.AverageFps.ToString("0") + " FPS average, " + result.OnePercentLowFps.ToString("0") + " FPS 1% low");
        }
    }
}
