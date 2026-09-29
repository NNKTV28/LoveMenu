using UnityEngine;

namespace FlyMod.UI
{
    // Short confirmation messages ("Fly on", "Freed 1,204 MB"). Only the
    // newest one shows; each fades after a couple of seconds. Drawn inside
    // the menu when it is open, near the top of the screen otherwise, so a
    // hotkey toggle still gets feedback with the menu closed.
    internal static class Toasts
    {
        private const float VisibleSeconds = 1.8f;
        private const float FadeSeconds = 0.3f;

        private static string _message = "";
        private static float _shownAt = -100f;

        public static void Show(string message)
        {
            _message = message;
            _shownAt = Time.unscaledTime;
        }

        public static void Draw(MenuStyles styles, Rect? menuRect)
        {
            float age = Time.unscaledTime - _shownAt;
            if (string.IsNullOrEmpty(_message) || age > VisibleSeconds + FadeSeconds)
                return;

            var content = new GUIContent(_message);
            Vector2 textSize = styles.Toast.CalcSize(content);
            float dotSpace = styles.S(18);
            float width = textSize.x + dotSpace;
            float height = textSize.y;

            Rect rect;
            if (menuRect.HasValue)
            {
                Rect menu = menuRect.Value;
                rect = new Rect(menu.xMax - width - styles.S(20), menu.yMax - height - styles.S(18), width, height);
            }
            else
            {
                rect = new Rect((Screen.width - width) / 2f, styles.S(72), width, height);
            }

            Color previous = GUI.color;
            float alpha = age < VisibleSeconds ? 1f : 1f - (age - VisibleSeconds) / FadeSeconds;
            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.Box(rect, GUIContent.none, styles.Toast);
            float dot = styles.S(8);
            GUI.color = new Color(Theme.AccentText.r, Theme.AccentText.g, Theme.AccentText.b, alpha);
            GUI.DrawTexture(new Rect(rect.x + styles.Toast.padding.left, rect.center.y - dot / 2f, dot, dot), styles.Circle);
            GUI.color = new Color(1f, 1f, 1f, alpha);
            var labelStyle = new GUIStyle(styles.Toast) { normal = { background = null, textColor = Theme.TextStrong } };
            labelStyle.padding.left += Mathf.RoundToInt(dotSpace);
            GUI.Label(rect, content, labelStyle);
            GUI.color = previous;
        }
    }
}
