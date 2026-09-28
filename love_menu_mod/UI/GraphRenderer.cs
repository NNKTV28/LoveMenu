using System.Collections.Generic;
using UnityEngine;

namespace FlyMod.UI
{
    // Draws a simple line-sparkline from recent sample history. Deliberately
    // allocates zero textures - each segment is just Texture2D.whiteTexture
    // stretched and rotated into place via GUI.matrix, tinted with GUI.color.
    // (MenuStyles used to leak a new Texture2D every frame the menu was open
    // - this graph exists specifically to show that kind of regression, so
    // it can't itself repeat the mistake.)
    internal static class GraphRenderer
    {
        public static void DrawLineGraph(Rect area, IReadOnlyList<float> samples, Color lineColor, float minValue, float maxValue)
        {
            Color previousColor = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.06f);
            GUI.DrawTexture(area, Texture2D.whiteTexture);
            GUI.color = previousColor;

            DrawGridLines(area);

            if (samples == null || samples.Count < 2)
                return;

            float range = Mathf.Max(0.0001f, maxValue - minValue);
            int count = samples.Count;
            Vector2 previousPoint = default;

            for (int i = 0; i < count; i++)
            {
                float t = (float)i / (count - 1);
                float x = area.x + t * area.width;
                float normalized = Mathf.Clamp01((samples[i] - minValue) / range);
                float y = area.y + area.height - normalized * area.height;
                var point = new Vector2(x, y);

                // A soft column down to the baseline under each point -
                // cheap stand-in for a filled area chart, which reads far
                // better than a bare 2px line on a dark card.
                GUI.color = new Color(lineColor.r, lineColor.g, lineColor.b, 0.12f);
                GUI.DrawTexture(new Rect(x, y, Mathf.Max(1f, area.width / count + 1f), area.yMax - y), Texture2D.whiteTexture);
                GUI.color = Color.white;

                if (i > 0)
                    DrawLineSegment(previousPoint, point, lineColor, 2f);
                previousPoint = point;
            }
        }

        private static void DrawGridLines(Rect area)
        {
            Color previousColor = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.05f);
            for (int i = 1; i < 4; i++)
            {
                float y = area.y + area.height * i / 4f;
                GUI.DrawTexture(new Rect(area.x, y, area.width, 1f), Texture2D.whiteTexture);
            }
            GUI.color = previousColor;
        }

        private static void DrawLineSegment(Vector2 a, Vector2 b, Color color, float thickness)
        {
            Vector2 delta = b - a;
            float length = delta.magnitude;
            if (length < 0.001f)
                return;
            float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;

            Color previousColor = GUI.color;
            Matrix4x4 previousMatrix = GUI.matrix;

            GUI.color = color;
            GUIUtility.RotateAroundPivot(angle, a);
            GUI.DrawTexture(new Rect(a.x, a.y - thickness / 2f, length, thickness), Texture2D.whiteTexture);

            GUI.matrix = previousMatrix;
            GUI.color = previousColor;
        }
    }
}
