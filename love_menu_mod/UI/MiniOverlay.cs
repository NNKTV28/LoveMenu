using System.Collections.Generic;
using UnityEngine;
using FlyMod.Core;

namespace FlyMod.UI
{
    // A small readout that stays on screen with the menu closed: FPS, RAM
    // and which features are on, in the corner the player picks.
    internal class MiniOverlay
    {
        public enum Corner { TopLeft, TopRight, BottomLeft, BottomRight }

        public bool Enabled = true;
        public Corner Position = Corner.TopRight;
        public bool ShowFps = true;
        public bool ShowRam = true;
        public bool ShowActiveFeatures;

        private readonly SystemStatsController _systemStats;
        private readonly System.Func<IEnumerable<string>> _activeFeatureNames;

        public MiniOverlay(SystemStatsController systemStats, System.Func<IEnumerable<string>> activeFeatureNames)
        {
            _systemStats = systemStats;
            _activeFeatureNames = activeFeatureNames;
        }

        public string Text()
        {
            var parts = new List<string>();
            if (ShowFps)
                parts.Add(_systemStats.FramesPerSecond.ToString("0") + " FPS");
            if (ShowRam)
                parts.Add((SystemStatsController.BytesToMB(_systemStats.WorkingSetBytes()) / 1024f).ToString("0.0") + " GB");
            if (ShowActiveFeatures)
            {
                var active = new List<string>(_activeFeatureNames());
                if (active.Count > 0)
                    parts.Add(string.Join(" · ", active.ToArray()));
            }
            return string.Join("   ·   ", parts.ToArray());
        }

        public void Draw(MenuStyles styles)
        {
            if (!Enabled || Event.current.type != EventType.Repaint)
                return;
            string text = Text();
            if (text.Length == 0)
                return;

            var content = new GUIContent(text);
            Vector2 size = styles.OverlayPill.CalcSize(content);
            float margin = styles.S(10);
            float x = Position == Corner.TopLeft || Position == Corner.BottomLeft ? margin : Screen.width - size.x - margin;
            float y = Position == Corner.TopLeft || Position == Corner.TopRight ? margin : Screen.height - size.y - margin;
            GUI.Label(new Rect(x, y, size.x, size.y), content, styles.OverlayPill);
        }
    }
}
