using UnityEngine;

namespace FlyMod.UI
{
    internal class Theme
    {
        public float Alpha = 0.96f;
        public int Index; // accent color index
        public float UIScale = 1f;

        public static readonly string[] AccentNames = { "Blue", "Violet", "Cyan", "Red", "Green" };

        private static readonly Color[] Accents =
        {
            new Color(0.30f, 0.55f, 0.95f), // Blue
            new Color(0.62f, 0.42f, 0.95f), // Violet
            new Color(0.25f, 0.80f, 0.85f), // Cyan
            new Color(0.90f, 0.35f, 0.35f), // Red
            new Color(0.35f, 0.80f, 0.45f), // Green
        };

        // Background transparency quick-presets, next to the fine slider.
        public const float BackgroundGame = 0.55f;
        public const float BackgroundDimmed = 0.85f;
        public const float BackgroundSolid = 1f;

        public Color Accent => Accents[Mathf.Clamp(Index, 0, Accents.Length - 1)];
    }
}
