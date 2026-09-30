using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using FlyMod.Core;
using FlyMod.UI;

namespace FlyMod.Features
{
    // Quiz helper. World quizzes (the LoveAngeles Quiz and others) are a
    // world script, "QuizManager", that the server sends to this PC with all
    // questions, options and the index of the right one in its start-up data
    // (Context.DATA.questions[].correctIndex). The script shuffles them and
    // checks answers locally. This reads that same data when the quiz script
    // arrives and shows a panel with each question's right answer; the
    // player still reads the question and clicks the answer themselves.
    //
    // Answers are shown as text, so the script's shuffling doesn't matter.
    internal static class QuizHelper
    {
        public class Entry
        {
            public string Question;
            public string Answer;
        }

        public static bool Enabled = true;
        public static bool Visible;      // the answers list panel (opened from the Teleports card)
        public static bool Active;       // a quiz script is running right now
        public static string Title = "";
        public static readonly List<Entry> Entries = new List<Entry>();
        private static string _scope;
        private static Vector2 _scroll;
        private static Rect _panelRect;

        // Lets the game-input patches ignore clicks and scrolling on the panel.
        public static bool IsMouseOverPanel(Vector2 mouse) => Visible && _panelRect.Contains(mouse);

        // Called for every server event to world scripts (WorldScriptMessagePatch).
        public static void OnScriptEvent(object scriptEvent)
        {
            if (scriptEvent == null)
                return;
            try
            {
                string kind = scriptEvent.GetType().Name;
                var traverse = HarmonyLib.Traverse.Create(scriptEvent);
                string scope = traverse.Property("Scope").GetValue()?.ToString();

                if (kind.IndexOf("Remove", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (scope != null && scope == _scope)
                    {
                        Visible = false;
                        Active = false;
                    }
                    return;
                }
                if (kind.IndexOf("Add", StringComparison.OrdinalIgnoreCase) < 0)
                    return;

                string name = traverse.Property("Name").GetValue() as string ?? "";
                if (name.IndexOf("Quiz", StringComparison.OrdinalIgnoreCase) < 0)
                    return;
                string context = traverse.Property("Context").GetValue()?.ToString();
                if (string.IsNullOrEmpty(context))
                    return;

                if (Load(context))
                {
                    _scope = scope;
                    Active = true;
                    DebugLog.Info("Quiz helper: loaded " + Entries.Count + " answers for \"" + Title + "\"");
                }
            }
            catch (Exception exception)
            {
                DebugLog.Warn("Quiz helper could not read the quiz: " + exception.Message);
            }
        }

        private static bool Load(string context)
        {
            JObject data = JObject.Parse(context)["DATA"] as JObject;
            if (!(data?["questions"] is JArray questions) || questions.Count == 0)
                return false;

            Entries.Clear();
            Title = (string)data["title"] ?? "Quiz";
            foreach (JToken question in questions)
            {
                var options = question["options"] as JArray;
                int correct = question["correctIndex"]?.Type == JTokenType.Integer ? (int)question["correctIndex"] : -1;
                string answer = options != null && correct >= 0 && correct < options.Count ? (string)options[correct] : "?";
                Entries.Add(new Entry { Question = (string)question["text"] ?? "", Answer = answer ?? "?" });
            }
            _scroll = Vector2.zero;
            return true;
        }

        // --- marking the right answer in the game's quiz window -----------------
        // The quiz draws the question in one text element and each option as
        // a button labelled "<color=#FFA500>1.</color> Find the exit". While
        // a quiz runs, find the text showing a known question, then recolour
        // the button whose option is that question's answer and add a tick.
        // New buttons appear for every question, so this repeats a few times
        // a second. Matching is by text, so it needs the quiz in English.

        private const string Mark = "  <color=#4ADE80><b>✓</b></color>";
        private static readonly System.Text.RegularExpressions.Regex Tags = new System.Text.RegularExpressions.Regex("<.*?>");
        private static readonly System.Text.RegularExpressions.Regex NumberedOption = new System.Text.RegularExpressions.Regex(@"^\s*\d+\.\s*(.+?)\s*$");
        private static readonly Type TextMeshProType = HarmonyLib.AccessTools.TypeByName("TMPro.TMP_Text");
        private static readonly System.Reflection.PropertyInfo TextMeshProText = TextMeshProType?.GetProperty("text");
        private static float _nextMarkTime;

        public static void Tick()
        {
            if (!Enabled || !Active || Entries.Count == 0 || Time.unscaledTime < _nextMarkTime)
                return;
            _nextMarkTime = Time.unscaledTime + 0.2f;
            try
            {
                MarkCurrentAnswer();
            }
            catch (Exception exception)
            {
                DebugLog.Detail("Quiz helper could not mark the answer: " + exception.Message);
            }
        }

        private static void MarkCurrentAnswer()
        {
            var texts = new List<(Component Component, string Text)>();
            foreach (UnityEngine.UI.Text text in UnityEngine.Object.FindObjectsByType<UnityEngine.UI.Text>(UnityEngine.FindObjectsSortMode.None))
                texts.Add((text, text.text ?? ""));
            if (TextMeshProType != null && TextMeshProText != null)
                foreach (Component text in UnityEngine.Object.FindObjectsByType(TextMeshProType, UnityEngine.FindObjectsSortMode.None))
                    texts.Add((text, TextMeshProText.GetValue(text, null) as string ?? ""));

            Entry current = null;
            foreach (var text in texts)
            {
                string plain = Tags.Replace(text.Text, "").Trim();
                current = Entries.Find(entry => string.Equals(entry.Question.Trim(), plain, StringComparison.OrdinalIgnoreCase));
                if (current != null)
                    break;
            }
            if (current == null)
                return;

            foreach (var text in texts)
            {
                if (text.Text.Contains("✓"))
                    continue;
                var option = NumberedOption.Match(Tags.Replace(text.Text, ""));
                if (!option.Success || !string.Equals(option.Groups[1].Value, current.Answer.Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;
                int at = text.Text.LastIndexOf(current.Answer, StringComparison.OrdinalIgnoreCase);
                string marked = at >= 0
                    ? text.Text.Substring(0, at) + "<color=#4ADE80>" + text.Text.Substring(at, current.Answer.Length) + "</color>" + text.Text.Substring(at + current.Answer.Length) + Mark
                    : text.Text + Mark;
                SetText(text.Component, marked);
            }
        }

        private static void SetText(Component component, string value)
        {
            if (component is UnityEngine.UI.Text uiText)
                uiText.text = value;
            else
                TextMeshProText?.SetValue(component, value, null);
        }

        public static void Draw(MenuStyles s)
        {
            if (!Visible || Entries.Count == 0)
            {
                _panelRect = Rect.zero;
                MenuUI.UpdateQuizPanelHover(false);
                return;
            }

            float width = Mathf.Min(s.S(400), Screen.width * 0.3f);
            float height = Mathf.Min(s.S(560), Screen.height - s.S(160));
            _panelRect = new Rect(Screen.width - width - s.S(16), s.S(110), width, height);
            MenuUI.UpdateQuizPanelHover(_panelRect.Contains(Event.current.mousePosition));
            GUI.Box(_panelRect, GUIContent.none, s.Card);

            GUILayout.BeginArea(new Rect(_panelRect.x + s.S(16), _panelRect.y + s.S(14), width - s.S(32), height - s.S(28)));
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical();
            GUILayout.Label("Quiz answers", s.CardTitle);
            GUILayout.Label(Title + " · " + Entries.Count + " questions", s.Small);
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("×", s.Go, GUILayout.Width(s.S(28))))
                Visible = false;
            GUILayout.EndHorizontal();
            GUILayout.Space(s.S(10));

            _scroll = GUILayout.BeginScrollView(_scroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUIStyle.none);
            var answerStyle = new GUIStyle(s.BodyStrong) { wordWrap = true, normal = { textColor = Theme.SuccessText } };
            float textWidth = width - s.S(48);
            for (int i = 0; i < Entries.Count; i++)
            {
                GUILayout.Label((i + 1) + ". " + Entries[i].Question, s.Description, GUILayout.Width(textWidth));
                GUILayout.Space(s.S(2));
                GUILayout.Label("→ " + Entries[i].Answer, answerStyle, GUILayout.Width(textWidth));
                GUILayout.Space(s.S(10));
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}
