using System;
using UnityEngine;
using FlyMod.Inputs;

namespace FlyMod.UI
{
    // First-run tour, shown once a few seconds after the player's avatar
    // first appears. Five short steps in a card over a dimmed screen:
    //  1. welcome   2. try the menu key   3. pick an accent + overlay (live)
    //  4. handy tricks   5. done - open the menu
    // Replayable from Credits. Animations are plain eased values driven by
    // Time.unscaledTime: backdrop fade, card slide-up, step slide, a pulsing
    // logo, a key that "presses" itself, and progress dots that grow.
    internal class WelcomeScreen
    {
        private const int StepCount = 5;
        private const float ShowDelaySeconds = 3f;

        public bool Seen;
        public bool Visible { get; private set; }

        private readonly Theme _theme;
        private readonly Keybinds _keybinds;
        private readonly MiniOverlay _overlay;
        private readonly Func<string> _playerName;
        private readonly Func<bool> _gameReady;
        private readonly Action _openMenu;

        private int _step;
        private int _stepDirection = 1;
        private int? _pendingStep;
        private bool _pendingClose, _pendingOpenMenu;
        private float _openedAt, _stepChangedAt, _readySince = -1f, _menuKeyTriedAt = -1f;
        private readonly float[] _dotWidths = new float[StepCount];

        public WelcomeScreen(Theme theme, Keybinds keybinds, MiniOverlay overlay, Func<string> playerName, Func<bool> gameReady, Action openMenu)
        {
            _theme = theme;
            _keybinds = keybinds;
            _overlay = overlay;
            _playerName = playerName;
            _gameReady = gameReady;
            _openMenu = openMenu;
        }

        public void Tick()
        {
            if (Seen || Visible)
                return;
            if (!_gameReady())
            {
                _readySince = -1f;
                return;
            }
            if (_readySince < 0f)
                _readySince = Time.unscaledTime;
            else if (Time.unscaledTime - _readySince > ShowDelaySeconds)
                Show();
        }

        public void Show()
        {
            Visible = true;
            _step = 0;
            _stepDirection = 1;
            _openedAt = _stepChangedAt = Time.unscaledTime;
            _menuKeyTriedAt = -1f;
            MenuUI.WelcomeBlocksMouse = true;
        }

        private void Close(bool openMenu)
        {
            Visible = false;
            Seen = true;
            MenuUI.WelcomeBlocksMouse = false;
            if (openMenu)
                _openMenu();
        }

        // The plugin hands the menu key here while the tour is up. On the
        // "try it" step that's the whole point; elsewhere it is swallowed so
        // the menu doesn't open underneath the tour.
        public bool TakeMenuKey()
        {
            if (!Visible)
                return false;
            if (_step == 1 && _menuKeyTriedAt < 0f)
                _menuKeyTriedAt = Time.unscaledTime;
            return true;
        }

        private void GoTo(int step)
        {
            step = Mathf.Clamp(step, 0, StepCount - 1);
            if (step == _step)
                return;
            _pendingStep = step;
        }

        private static float Ease(float t)
        {
            t = Mathf.Clamp01(t);
            return 1f - Mathf.Pow(1f - t, 3f);
        }

        public void Draw(MenuStyles s)
        {
            if (!Visible)
                return;

            HandleKeys();
            float now = Time.unscaledTime;
            float openT = Ease((now - _openedAt) / 0.35f);
            float stepT = Ease((now - _stepChangedAt) / 0.28f);
            Color previousColor = GUI.color;

            // Backdrop
            GUI.color = new Color(0f, 0f, 0f, 0.6f * openT);
            if (Event.current.type == EventType.Repaint)
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);

            // Card
            float width = Mathf.Min(s.S(560), Screen.width - s.S(40));
            float height = Mathf.Min(s.S(440), Screen.height - s.S(40));
            var card = new Rect((Screen.width - width) / 2f, (Screen.height - height) / 2f + (1f - openT) * s.S(30), width, height);
            GUI.color = new Color(1f, 1f, 1f, openT);
            GUI.Box(card, GUIContent.none, s.Card);

            // Skip, top right (not on the last step, which has its own buttons)
            if (_step < StepCount - 1)
            {
                var skipStyle = new GUIStyle(s.Small) { alignment = TextAnchor.MiddleRight, normal = { textColor = Theme.TextSecondary }, hover = { textColor = Theme.Text } };
                if (GUI.Button(new Rect(card.xMax - s.S(120), card.y + s.S(14), s.S(100), s.S(24)), "Skip tour", skipStyle))
                    _pendingClose = true;
            }

            // Step content, sliding in from the side it came from
            float padding = s.S(36);
            float footerHeight = s.S(64);
            var content = new Rect(card.x + padding + (1f - stepT) * s.S(28) * _stepDirection, card.y + s.S(40),
                card.width - padding * 2f, card.height - s.S(40) - footerHeight);
            GUI.color = new Color(1f, 1f, 1f, openT * stepT);
            GUILayout.BeginArea(content);
            DrawStep(s, content.width, now);
            GUILayout.EndArea();

            // Footer: progress dots, back / next
            GUI.color = new Color(1f, 1f, 1f, openT);
            var footer = new Rect(card.x + padding, card.yMax - footerHeight, card.width - padding * 2f, footerHeight - s.S(20));
            DrawFooter(s, footer);

            GUI.color = previousColor;
            ApplyPending();
        }

        private void HandleKeys()
        {
            Event e = Event.current;
            if (e.type != EventType.KeyDown)
                return;
            if (e.keyCode == KeyCode.Escape)
            {
                _pendingClose = true;
                e.Use();
            }
            else if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter || e.keyCode == KeyCode.RightArrow)
            {
                if (_step == StepCount - 1) { _pendingClose = true; _pendingOpenMenu = true; }
                else GoTo(_step + 1);
                e.Use();
            }
            else if (e.keyCode == KeyCode.LeftArrow)
            {
                GoTo(_step - 1);
                e.Use();
            }
        }

        // Step changes wait until the whole card has been drawn, so IMGUI's
        // layout and repaint passes of one frame see the same step.
        private void ApplyPending()
        {
            if (_pendingClose)
            {
                bool open = _pendingOpenMenu;
                _pendingClose = _pendingOpenMenu = false;
                _pendingStep = null;
                Close(open);
                return;
            }
            if (_pendingStep.HasValue)
            {
                _stepDirection = _pendingStep.Value > _step ? 1 : -1;
                _step = _pendingStep.Value;
                _pendingStep = null;
                _stepChangedAt = Time.unscaledTime;
            }
        }

        // --- steps ---------------------------------------------------------------

        private void DrawStep(MenuStyles s, float width, float now)
        {
            switch (_step)
            {
                case 0: DrawWelcome(s, width, now); break;
                case 1: DrawMenuKey(s, width, now); break;
                case 2: DrawMakeItYours(s, width); break;
                case 3: DrawTricks(s, width); break;
                case 4: DrawDone(s, width, now); break;
            }
        }

        private static GUIStyle Centered(GUIStyle style, bool wrap = true) =>
            new GUIStyle(style) { alignment = TextAnchor.UpperCenter, wordWrap = wrap };

        private void DrawLogo(MenuStyles s, float width, float size, float now)
        {
            float pulse = 1f + 0.06f * Mathf.Sin(now * 3f);
            Rect slot = GUILayoutUtility.GetRect(width, size * 1.15f);
            if (Event.current.type != EventType.Repaint)
                return;
            float drawn = size * pulse;
            var rect = new Rect(slot.center.x - drawn / 2f, slot.center.y - drawn / 2f, drawn, drawn);
            Color previous = GUI.color;
            GUI.color = new Color(_theme.Accent.r, _theme.Accent.g, _theme.Accent.b, previous.a);
            s.FillShape.Draw(rect, false, false, false, false);
            GUI.color = new Color(1f, 1f, 1f, previous.a);
            float icon = drawn * 0.55f;
            GUI.DrawTexture(new Rect(rect.center.x - icon / 2f, rect.center.y - icon / 2f, icon, icon), Icons.Heart);
            GUI.color = previous;
        }

        private void DrawWelcome(MenuStyles s, float width, float now)
        {
            GUILayout.Space(s.S(10));
            DrawLogo(s, width, s.S(72), now);
            GUILayout.Space(s.S(18));
            GUILayout.Label("Welcome to Love Menu", Centered(new GUIStyle(s.Title) { fontSize = Mathf.RoundToInt(s.S(24)) }));
            GUILayout.Space(s.S(10));
            string name = _playerName();
            string hello = string.IsNullOrEmpty(name) ? "Hi!" : "Hi " + name + "!";
            GUILayout.Label(hello + " Here's a quick tour of your new menu. It takes about 30 seconds.",
                Centered(new GUIStyle(s.Body) { normal = { textColor = Theme.TextSoft } }), GUILayout.Width(width));
        }

        private void DrawMenuKey(MenuStyles s, float width, float now)
        {
            string key = FeatureHotkeys.KeyName(_keybinds.MenuKey);
            bool tried = _menuKeyTriedAt >= 0f;

            GUILayout.Label("Open it any time", Centered(s.Title));
            GUILayout.Space(s.S(8));
            GUILayout.Label("Press " + key + " to open or close the menu. Try it now!",
                Centered(new GUIStyle(s.Body) { normal = { textColor = Theme.TextSoft } }), GUILayout.Width(width));
            GUILayout.Space(s.S(22));

            // A big key that presses itself until the player presses the real one.
            Rect slot = GUILayoutUtility.GetRect(width, s.S(96));
            if (Event.current.type == EventType.Repaint)
            {
                float cycle = (now % 1.6f) / 1.6f;
                float press = tried ? 0f : (cycle > 0.8f ? Mathf.Sin((cycle - 0.8f) / 0.2f * Mathf.PI) : 0f);
                float keyWidth = s.S(128), keyHeight = s.S(84);
                var keyRect = new Rect(slot.center.x - keyWidth / 2f, slot.y + press * s.S(6), keyWidth, keyHeight - press * s.S(6));
                var shadow = new Rect(keyRect.x, slot.y + s.S(8), keyWidth, keyHeight);
                Color previous = GUI.color;
                GUI.color = new Color(0.07f, 0.06f, 0.1f, previous.a);
                s.FillShape.Draw(shadow, false, false, false, false);
                Color face = tried ? Theme.SuccessBackground : Theme.Selected;
                GUI.color = new Color(face.r, face.g, face.b, previous.a);
                s.FillShape.Draw(keyRect, false, false, false, false);
                GUI.color = previous;
                var keyText = new GUIStyle(s.Title) { alignment = TextAnchor.MiddleCenter, fontSize = Mathf.RoundToInt(s.S(26)),
                    normal = { textColor = tried ? Theme.SuccessText : Theme.TextStrong } };
                GUI.Label(keyRect, key, keyText);
            }

            GUILayout.Space(s.S(16));
            if (tried)
            {
                GUILayout.Label("✓  That's it! " + key + " opens the menu from anywhere in the game.",
                    Centered(new GUIStyle(s.BodyStrong) { normal = { textColor = Theme.SuccessText } }), GUILayout.Width(width));
            }
            else
            {
                float blink = 0.55f + 0.45f * Mathf.Abs(Mathf.Sin(now * 2f));
                Color previous = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, previous.a * blink);
                GUILayout.Label("Waiting for you to press " + key + "...", Centered(s.Small));
                GUI.color = previous;
            }
            GUILayout.Space(s.S(6));
            GUILayout.Label("You can change the key later in Settings.", Centered(s.Small));
        }

        private void DrawMakeItYours(MenuStyles s, float width)
        {
            GUILayout.Label("Make it yours", Centered(s.Title));
            GUILayout.Space(s.S(8));
            GUILayout.Label("Pick a colour. The menu changes as you click.",
                Centered(new GUIStyle(s.Body) { normal = { textColor = Theme.TextSoft } }), GUILayout.Width(width));
            GUILayout.Space(s.S(20));

            float swatch = s.S(46), gap = s.S(16);
            float rowWidth = Theme.AccentNames.Length * swatch + (Theme.AccentNames.Length - 1) * gap;
            Rect row = GUILayoutUtility.GetRect(width, swatch + s.S(22));
            float x = row.center.x - rowWidth / 2f;
            for (int i = 0; i < Theme.AccentNames.Length; i++)
            {
                var rect = new Rect(x + i * (swatch + gap), row.y, swatch, swatch);
                int index = i;
                if (GUI.Button(rect, GUIContent.none, GUIStyle.none))
                    _theme.Index = index;
                if (Event.current.type == EventType.Repaint)
                {
                    Color previous = GUI.color;
                    bool selected = _theme.Index == i;
                    float grow = selected ? s.S(4) : 0f;
                    var circle = new Rect(rect.x - grow / 2f, rect.y - grow / 2f, rect.width + grow, rect.height + grow);
                    if (selected)
                    {
                        GUI.color = new Color(1f, 1f, 1f, previous.a);
                        GUI.DrawTexture(circle, s.Circle);
                        float inset = s.S(3);
                        circle = new Rect(circle.x + inset, circle.y + inset, circle.width - inset * 2f, circle.height - inset * 2f);
                    }
                    Color accent = Theme.AccentAt(i);
                    GUI.color = new Color(accent.r, accent.g, accent.b, previous.a);
                    GUI.DrawTexture(circle, s.Circle);
                    GUI.color = previous;
                    GUI.Label(new Rect(rect.x - s.S(10), rect.yMax + s.S(4), rect.width + s.S(20), s.S(18)), Theme.AccentNames[i],
                        new GUIStyle(s.Small) { alignment = TextAnchor.UpperCenter });
                }
            }

            GUILayout.Space(s.S(26));
            GUILayout.BeginHorizontal(s.Card);
            GUILayout.BeginVertical(GUILayout.Width(width - s.S(100)));
            GUILayout.Label("Mini overlay", s.CardTitle);
            GUILayout.Space(s.S(3));
            GUILayout.Label("FPS and RAM in the corner of the screen, even with the menu closed.", s.Description, GUILayout.Width(width - s.S(100)));
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            GUILayout.BeginVertical();
            GUILayout.Space(s.S(8));
            if (Widgets.Switch(s, _theme.Accent, "welcome-overlay", _overlay.Enabled))
                _overlay.Enabled = !_overlay.Enabled;
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }

        private void DrawTricks(MenuStyles s, float width)
        {
            GUILayout.Label("Handy tricks", Centered(s.Title));
            GUILayout.Space(s.S(18));
            Trick(s, width, Icons.Search, "Search", "Type \"fov\" or \"wings\" in the search box to jump straight to a setting.");
            Trick(s, width, Icons.Settings, "Hotkeys", "Click + Key next to a switch, then press a key to toggle it without the menu.");
            Trick(s, width, Icons.Camera, "Screenshots", "F9 hides the HUD, name tags and this menu. Press it again to bring them back.");
            Trick(s, width, Icons.Info, "Launcher only", "The menu is on only when you start the game with LoveMenu.exe. Steam starts the normal game.");
        }

        private void Trick(MenuStyles s, float width, Texture2D icon, string title, string text)
        {
            GUILayout.BeginHorizontal();
            Rect bubble = GUILayoutUtility.GetRect(s.S(36), s.S(36), GUILayout.Width(s.S(36)), GUILayout.Height(s.S(36)));
            if (Event.current.type == EventType.Repaint)
            {
                Color previous = GUI.color;
                GUI.color = new Color(Theme.Selected.r, Theme.Selected.g, Theme.Selected.b, previous.a);
                GUI.DrawTexture(bubble, s.Circle);
                GUI.color = new Color(Theme.AccentText.r, Theme.AccentText.g, Theme.AccentText.b, previous.a);
                float size = s.S(18);
                GUI.DrawTexture(new Rect(bubble.center.x - size / 2f, bubble.center.y - size / 2f, size, size), icon);
                GUI.color = previous;
            }
            GUILayout.Space(s.S(14));
            GUILayout.BeginVertical();
            GUILayout.Label(title, s.BodyStrong);
            GUILayout.Space(s.S(2));
            GUILayout.Label(text, s.Description, GUILayout.Width(width - s.S(50)));
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            GUILayout.Space(s.S(12));
        }

        private void DrawDone(MenuStyles s, float width, float now)
        {
            GUILayout.Space(s.S(10));
            DrawLogo(s, width, s.S(64), now);
            GUILayout.Space(s.S(16));
            GUILayout.Label("You're all set", Centered(new GUIStyle(s.Title) { fontSize = Mathf.RoundToInt(s.S(24)) }));
            GUILayout.Space(s.S(10));
            GUILayout.Label("Found a bug or have an idea? Open Credits and press Report a bug. " +
                "You can replay this tour from Credits too.",
                Centered(new GUIStyle(s.Body) { normal = { textColor = Theme.TextSoft } }), GUILayout.Width(width));
        }

        // --- footer ------------------------------------------------------------------

        private void DrawFooter(MenuStyles s, Rect footer)
        {
            // Progress dots: the current one stretches into a pill.
            float dotHeight = s.S(8), gap = s.S(6);
            float x = footer.x;
            float y = footer.center.y - dotHeight / 2f;
            for (int i = 0; i < StepCount; i++)
            {
                float target = i == _step ? s.S(22) : s.S(8);
                if (Event.current.type == EventType.Repaint)
                    _dotWidths[i] = _dotWidths[i] <= 0f ? target : Mathf.Lerp(_dotWidths[i], target, 1f - Mathf.Exp(-Time.unscaledDeltaTime * 14f));
                float dotWidth = _dotWidths[i] <= 0f ? target : _dotWidths[i];
                var dot = new Rect(x, y, dotWidth, dotHeight);
                if (Event.current.type == EventType.Repaint)
                {
                    Color previous = GUI.color;
                    Color color = i == _step ? _theme.Accent : (i < _step ? Theme.AccentText : Theme.ControlBorder);
                    GUI.color = new Color(color.r, color.g, color.b, previous.a);
                    s.SmallPillShape.Draw(dot, false, false, false, false);
                    GUI.color = previous;
                }
                x += dotWidth + gap;
            }

            float buttonHeight = s.Primary.fixedHeight;
            float by = footer.center.y - buttonHeight / 2f;
            if (_step == StepCount - 1)
            {
                float openWidth = s.S(150), closeWidth = s.S(90);
                if (GUI.Button(new Rect(footer.xMax - openWidth, by, openWidth, buttonHeight), "Open the menu", s.Primary))
                {
                    _pendingClose = true;
                    _pendingOpenMenu = true;
                }
                if (GUI.Button(new Rect(footer.xMax - openWidth - s.S(10) - closeWidth, by + (buttonHeight - s.Secondary.fixedHeight) / 2f,
                        closeWidth, s.Secondary.fixedHeight), "Close", s.Secondary))
                    _pendingClose = true;
                return;
            }

            float nextWidth = s.S(110);
            string nextLabel = _step == 0 ? "Start" : "Next";
            if (GUI.Button(new Rect(footer.xMax - nextWidth, by, nextWidth, buttonHeight), nextLabel, s.Primary))
                GoTo(_step + 1);
            if (_step > 0)
            {
                float backWidth = s.S(90);
                if (GUI.Button(new Rect(footer.xMax - nextWidth - s.S(10) - backWidth, by + (buttonHeight - s.Secondary.fixedHeight) / 2f,
                        backWidth, s.Secondary.fixedHeight), "Back", s.Secondary))
                    GoTo(_step - 1);
            }
        }
    }
}
