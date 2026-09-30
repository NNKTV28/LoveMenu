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
        public static Texture2D Camera => GetOrBuild("camera", BuildCamera);
        public static Texture2D Gauge => GetOrBuild("gauge", BuildGauge);
        public static Texture2D Search => GetOrBuild("search", BuildSearch);
        public static Texture2D Close => GetOrBuild("close", BuildClose);
        public static Texture2D Heart => GetOrBuild("heart", BuildHeart);
        public static Texture2D Download => GetOrBuild("download", BuildDownload);
        public static Texture2D Eye => GetOrBuild("eye", BuildEye);
        public static Texture2D Bug => GetOrBuild("bug", BuildBug);
        public static Texture2D Arrow => GetOrBuild("arrow", BuildArrow);
        public static Texture2D Map => GetOrBuild("map", BuildMap);

        public static Texture2D People => GetOrBuild("people", BuildPeople);

        // Two heads and shoulders.
        private static void BuildPeople(IconCanvas c)
        {
            c.Circle(12, 11, 4f);
            c.Line(4, 26, 6, 19);
            c.Line(6, 19, 18, 19);
            c.Line(18, 19, 20, 26);
            c.Circle(22, 9, 3.5f);
            c.Line(22, 16, 27, 16);
            c.Line(27, 16, 29, 23);
        }

        // Pointing up: a chevron with a short tail.
        private static void BuildArrow(IconCanvas c)
        {
            c.Line(16, 4, 6, 26);
            c.Line(16, 4, 26, 26);
            c.Line(6, 26, 16, 20);
            c.Line(26, 26, 16, 20);
        }

        // A folded map.
        private static void BuildMap(IconCanvas c)
        {
            c.Line(4, 8, 12, 5);
            c.Line(12, 5, 20, 8);
            c.Line(20, 8, 28, 5);
            c.Line(4, 27, 12, 24);
            c.Line(12, 24, 20, 27);
            c.Line(20, 27, 28, 24);
            c.Line(4, 8, 4, 27);
            c.Line(12, 5, 12, 24);
            c.Line(20, 8, 20, 27);
            c.Line(28, 5, 28, 24);
        }

        // Round body, head, three legs each side, two feelers.
        private static void BuildBug(IconCanvas c)
        {
            c.Circle(16, 18, 7f);
            c.Circle(16, 9, 3f);
            c.Line(16, 12, 16, 25);
            c.Line(9, 14, 4, 11);
            c.Line(23, 14, 28, 11);
            c.Line(9, 19, 4, 19);
            c.Line(23, 19, 28, 19);
            c.Line(10, 23, 5, 27);
            c.Line(22, 23, 27, 27);
            c.Line(14, 6, 11, 3);
            c.Line(18, 6, 21, 3);
        }

        private static void BuildCamera(IconCanvas c)
        {
            c.Line(5, 11, 11, 11);
            c.Line(11, 11, 13, 7);
            c.Line(13, 7, 19, 7);
            c.Line(19, 7, 21, 11);
            c.Line(21, 11, 27, 11);
            c.Line(27, 11, 27, 25);
            c.Line(27, 25, 5, 25);
            c.Line(5, 25, 5, 11);
            c.Circle(16, 17.5f, 4.5f);
        }

        private static void BuildGauge(IconCanvas c)
        {
            for (int segment = 0; segment < 12; segment++)
            {
                float a1 = Mathf.PI + segment * Mathf.PI / 12f;
                float a2 = Mathf.PI + (segment + 1) * Mathf.PI / 12f;
                c.Line(16 + Mathf.Cos(a1) * 11f, 22 + Mathf.Sin(a1) * 11f, 16 + Mathf.Cos(a2) * 11f, 22 + Mathf.Sin(a2) * 11f);
            }
            c.Line(16, 22, 22, 14);
            c.Line(4, 22, 7, 22);
            c.Line(25, 22, 28, 22);
        }

        private static void BuildSearch(IconCanvas c)
        {
            c.Circle(14, 14, 8);
            c.Line(20, 20, 27, 27);
        }

        private static void BuildClose(IconCanvas c)
        {
            c.Line(8, 8, 24, 24);
            c.Line(24, 8, 8, 24);
        }

        private static void BuildHeart(IconCanvas c)
        {
            for (int step = 0; step <= 24; step++)
            {
                float t1 = step / 24f * Mathf.PI * 2f;
                float t2 = (step + 1) / 24f * Mathf.PI * 2f;
                c.Line(HeartX(t1), HeartY(t1), HeartX(t2), HeartY(t2));
            }
        }

        private static float HeartX(float t) => 16f + 0.72f * 16f * Mathf.Pow(Mathf.Sin(t), 3f);
        private static float HeartY(float t) => 16f - 0.72f * (13f * Mathf.Cos(t) - 5f * Mathf.Cos(2f * t) - 2f * Mathf.Cos(3f * t) - Mathf.Cos(4f * t)) + 1f;

        private static void BuildEye(IconCanvas c)
        {
            const int segments = 12;
            for (int segment = 0; segment < segments; segment++)
            {
                float x1 = 4f + 24f * segment / segments;
                float x2 = 4f + 24f * (segment + 1) / segments;
                float h1 = 8f * Mathf.Sin(Mathf.PI * segment / segments);
                float h2 = 8f * Mathf.Sin(Mathf.PI * (segment + 1) / segments);
                c.Line(x1, 16 - h1, x2, 16 - h2);
                c.Line(x1, 16 + h1, x2, 16 + h2);
            }
            c.Circle(16, 16, 4f);
        }

        private static void BuildDownload(IconCanvas c)
        {
            c.Line(16, 5, 16, 20);
            c.Line(10, 14, 16, 20);
            c.Line(22, 14, 16, 20);
            c.Line(7, 27, 25, 27);
        }

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
