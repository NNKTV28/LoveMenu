using System.Collections.Generic;
using UnityEngine;

namespace FlyMod.UI
{
    // Controls IMGUI doesn't have in the approved look: an animated on/off
    // switch and a slider with a filled track. Both draw with the white
    // shapes from MenuStyles, tinted through GUI.color.
    internal static class Widgets
    {
        private static readonly Dictionary<string, float> SwitchProgress = new Dictionary<string, float>();

        // Returns true on the frame it was clicked.
        public static bool Switch(MenuStyles styles, Color accent, string id, bool on)
        {
            Rect rect = GUILayoutUtility.GetRect(styles.S(44), styles.S(24), GUILayout.Width(styles.S(44)), GUILayout.Height(styles.S(24)));
            bool clicked = GUI.Button(rect, GUIContent.none, GUIStyle.none);

            if (!SwitchProgress.TryGetValue(id, out float progress))
                progress = on ? 1f : 0f;
            if (Event.current.type == EventType.Repaint)
            {
                progress = Mathf.MoveTowards(progress, on ? 1f : 0f, Time.unscaledDeltaTime * 8f);
                SwitchProgress[id] = progress;

                Color previous = GUI.color;
                GUI.color = Color.Lerp(Theme.Track, accent, progress) * previous;
                styles.PillShape.Draw(rect, false, false, false, false);

                float knobSize = styles.S(18);
                float inset = styles.S(3);
                float knobX = Mathf.Lerp(rect.x + inset, rect.xMax - inset - knobSize, progress);
                GUI.color = new Color(1f, 1f, 1f, previous.a);
                GUI.DrawTexture(new Rect(knobX, rect.y + inset, knobSize, knobSize), styles.Circle);
                GUI.color = previous;
            }
            return clicked;
        }


        public static float Slider(MenuStyles styles, Color accent, float value, float min, float max, float step = 0f)
        {
            Rect rect = GUILayoutUtility.GetRect(styles.S(100), styles.S(24), GUILayout.ExpandWidth(true), GUILayout.Height(styles.S(24)));
            int id = GUIUtility.GetControlID(FocusType.Passive);
            Event e = Event.current;

            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (e.button == 0 && rect.Contains(e.mousePosition))
                    {
                        GUIUtility.hotControl = id;
                        value = ValueAt(rect, e.mousePosition.x, min, max, step);
                        GUI.changed = true;
                        e.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id)
                    {
                        value = ValueAt(rect, e.mousePosition.x, min, max, step);
                        GUI.changed = true;
                        e.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id)
                    {
                        GUIUtility.hotControl = 0;
                        e.Use();
                    }
                    break;
                case EventType.Repaint:
                    DrawSlider(styles, accent, rect, Mathf.InverseLerp(min, max, value));
                    break;
            }
            return value;
        }

        private static float ValueAt(Rect rect, float mouseX, float min, float max, float step)
        {
            float t = Mathf.Clamp01((mouseX - rect.x) / Mathf.Max(1f, rect.width));
            float value = Mathf.Lerp(min, max, t);
            if (step > 0f)
                value = Mathf.Round(value / step) * step;
            return Mathf.Clamp(value, min, max);
        }

        private static void DrawSlider(MenuStyles styles, Color accent, Rect rect, float t)
        {
            float trackHeight = styles.S(6);
            float thumbSize = styles.S(16);
            float usable = rect.width - thumbSize;
            float thumbX = rect.x + usable * t;
            Rect track = new Rect(rect.x, rect.center.y - trackHeight / 2f, rect.width, trackHeight);

            Color previous = GUI.color;
            GUI.color = Theme.Track * previous;
            styles.SmallPillShape.Draw(track, false, false, false, false);
            GUI.color = accent * previous;
            styles.SmallPillShape.Draw(new Rect(track.x, track.y, thumbX - rect.x + thumbSize / 2f, trackHeight), false, false, false, false);

            Rect thumb = new Rect(thumbX, rect.center.y - thumbSize / 2f, thumbSize, thumbSize);
            GUI.DrawTexture(thumb, styles.Circle);
            float ring = styles.S(3);
            GUI.color = new Color(1f, 1f, 1f, previous.a);
            GUI.DrawTexture(new Rect(thumb.x + ring, thumb.y + ring, thumbSize - ring * 2f, thumbSize - ring * 2f), styles.Circle);
            GUI.color = previous;
        }

        // An icon texture from Icons, tinted, centred in a layout slot.
        public static void Icon(Texture2D icon, float size, Color color)
        {
            Rect rect = GUILayoutUtility.GetRect(size, size, GUILayout.Width(size), GUILayout.Height(size));
            DrawIcon(rect, icon, color);
        }

        public static void DrawIcon(Rect rect, Texture2D icon, Color color)
        {
            if (Event.current.type != EventType.Repaint)
                return;
            Color previous = GUI.color;
            GUI.color = new Color(color.r, color.g, color.b, color.a * previous.a);
            GUI.DrawTexture(rect, icon);
            GUI.color = previous;
        }

        public static void Dot(MenuStyles styles, float size, Color color)
        {
            Rect rect = GUILayoutUtility.GetRect(size, size, GUILayout.Width(size), GUILayout.Height(size));
            if (Event.current.type != EventType.Repaint)
                return;
            Color previous = GUI.color;
            GUI.color = color * previous;
            GUI.DrawTexture(rect, styles.Circle);
            GUI.color = previous;
        }

        // A horizontal bar filled to t (0..1).
        public static void Bar(MenuStyles styles, float t, Color color)
        {
            Rect rect = GUILayoutUtility.GetRect(styles.S(100), styles.S(8), GUILayout.ExpandWidth(true), GUILayout.Height(styles.S(8)));
            if (Event.current.type != EventType.Repaint)
                return;
            Color previous = GUI.color;
            GUI.color = Theme.CardBorder * previous;
            styles.SmallPillShape.Draw(rect, false, false, false, false);
            GUI.color = color * previous;
            styles.SmallPillShape.Draw(new Rect(rect.x, rect.y, Mathf.Max(styles.S(8), rect.width * Mathf.Clamp01(t)), rect.height), false, false, false, false);
            GUI.color = previous;
        }

        public static void Divider(MenuStyles styles)
        {
            Rect rect = GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true), GUILayout.Height(1));
            if (Event.current.type != EventType.Repaint)
                return;
            Color previous = GUI.color;
            GUI.color = Theme.CardBorder * previous;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
        }
    }
}
