using System.Collections.Generic;
using UnityEngine;

namespace FlyMod.UI
{
    // Simple monochrome line icons, drawn procedurally pixel-by-pixel (no
    // image assets to ship). Each is baked once into a white shape-with-alpha
    // texture and cached - tab bar tints them per-state via GUI.color at
    // draw time rather than regenerating textures.
    internal static class Icons
    {
        private const int Size = 32;
        private const float StrokeHalfWidth = 1.1f;

        private static readonly Dictionary<string, Texture2D> Cache = new Dictionary<string, Texture2D>();

        public static Texture2D Home => GetOrBuild("home", BuildHome);
        public static Texture2D Movement => GetOrBuild("movement", BuildMovement);
        public static Texture2D Teleport => GetOrBuild("teleport", BuildTeleport);
        public static Texture2D Settings => GetOrBuild("settings", BuildSettings);
        public static Texture2D Warning => GetOrBuild("warning", BuildWarning);
        public static Texture2D Info => GetOrBuild("info", BuildInfo);

        private static Texture2D GetOrBuild(string key, System.Action<IconCanvas> paint)
        {
            if (Cache.TryGetValue(key, out Texture2D existing))
                return existing;

            var canvas = new IconCanvas(Size);
            paint(canvas);
            Texture2D texture = canvas.ToTexture();
            Cache[key] = texture;
            return texture;
        }

        private static void BuildHome(IconCanvas c)
        {
            c.Line(16, 5, 5, 16);
            c.Line(16, 5, 27, 16);
            c.Line(8, 16, 8, 27);
            c.Line(24, 16, 24, 27);
            c.Line(8, 27, 24, 27);
        }

        private static void BuildMovement(IconCanvas c)
        {
            DrawArrow(c, new Vector2(16, 16), new Vector2(16, 6));
            DrawArrow(c, new Vector2(16, 16), new Vector2(16, 26));
            DrawArrow(c, new Vector2(16, 16), new Vector2(6, 16));
            DrawArrow(c, new Vector2(16, 16), new Vector2(26, 16));
        }

        private static void DrawArrow(IconCanvas c, Vector2 from, Vector2 to)
        {
            c.Line(from.x, from.y, to.x, to.y);
            Vector2 dir = (to - from).normalized;
            Vector2 perp = new Vector2(-dir.y, dir.x);
            Vector2 headBase = to - dir * 4f;
            c.Line(to.x, to.y, headBase.x + perp.x * 3f, headBase.y + perp.y * 3f);
            c.Line(to.x, to.y, headBase.x - perp.x * 3f, headBase.y - perp.y * 3f);
        }

        private static void BuildTeleport(IconCanvas c)
        {
            c.Circle(16, 12, 7);
            c.Circle(16, 12, 2.2f);
            c.Line(11.5f, 17, 16, 27);
            c.Line(20.5f, 17, 16, 27);
        }

        private static void BuildInfo(IconCanvas c)
        {
            c.Circle(16, 16, 10.5f);
            c.Line(16, 14, 16, 23);
            c.Circle(16, 9.5f, 0.7f);
        }

        private static void BuildWarning(IconCanvas c)
        {
            c.Line(16, 5, 4, 26);
            c.Line(16, 5, 28, 26);
            c.Line(4, 26, 28, 26);
            c.Line(16, 12, 16, 19);
            c.Circle(16, 23, 0.6f);
        }

        private static void BuildSettings(IconCanvas c)
        {
            c.Circle(16, 16, 9);
            c.Circle(16, 16, 4);
            for (int toothIndex = 0; toothIndex < 8; toothIndex++)
            {
                float angle = toothIndex * Mathf.PI / 4f;
                Vector2 innerPoint = new Vector2(16 + Mathf.Cos(angle) * 9f, 16 + Mathf.Sin(angle) * 9f);
                Vector2 outerPoint = new Vector2(16 + Mathf.Cos(angle) * 13f, 16 + Mathf.Sin(angle) * 13f);
                c.Line(innerPoint.x, innerPoint.y, outerPoint.x, outerPoint.y);
            }
        }

        // Accumulates stroke coverage per pixel so overlapping lines don't
        // double-darken, then bakes to a texture on demand.
        private class IconCanvas
        {
            private readonly int _size;
            private readonly float[,] _coverage;

            public IconCanvas(int size)
            {
                _size = size;
                _coverage = new float[size, size];
            }

            public void Line(float x1, float y1, float x2, float y2)
            {
                Vector2 a = new Vector2(x1, y1);
                Vector2 b = new Vector2(x2, y2);
                for (int y = 0; y < _size; y++)
                for (int x = 0; x < _size; x++)
                {
                    float distance = DistanceToSegment(new Vector2(x + 0.5f, y + 0.5f), a, b);
                    ApplyCoverage(x, y, distance);
                }
            }

            public void Circle(float centerX, float centerY, float radius)
            {
                Vector2 center = new Vector2(centerX, centerY);
                for (int y = 0; y < _size; y++)
                for (int x = 0; x < _size; x++)
                {
                    float distance = Mathf.Abs(Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center) - radius);
                    ApplyCoverage(x, y, distance);
                }
            }

            private void ApplyCoverage(int x, int y, float distanceFromStroke)
            {
                float coverage = Mathf.Clamp01(1f - (distanceFromStroke - StrokeHalfWidth));
                if (coverage > _coverage[x, y])
                    _coverage[x, y] = coverage;
            }

            private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
            {
                Vector2 ab = b - a;
                float lengthSquared = ab.sqrMagnitude;
                if (lengthSquared < 0.0001f)
                    return Vector2.Distance(p, a);
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / lengthSquared);
                Vector2 projection = a + ab * t;
                return Vector2.Distance(p, projection);
            }

            public Texture2D ToTexture()
            {
                var texture = new Texture2D(_size, _size, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Bilinear,
                };
                for (int y = 0; y < _size; y++)
                for (int x = 0; x < _size; x++)
                    texture.SetPixel(x, _size - 1 - y, new Color(1f, 1f, 1f, _coverage[x, y]));
                texture.Apply();
                return texture;
            }
        }
    }
}
