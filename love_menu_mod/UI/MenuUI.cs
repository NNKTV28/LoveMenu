using System;
using System.Collections.Generic;
using UnityEngine;
using FlyMod.Core;
using FlyMod.Features;
using FlyMod.Inputs;

namespace FlyMod.UI
{
    // Owns the IMGUI window and every Draw* method; reads/writes state on
    // the controllers it's given rather than holding feature state itself.
    // Dashboard layout (like a web admin panel): a fixed-width left sidebar
    // for navigation, a top bar with the current page title, and a
    // card-based scrollable content area filling the rest.
    internal class MenuUI
    {
        private enum Section { Home, Movement, Teleports, Performance, Crashes, Credits, Settings }
        private Section _activeSection = Section.Home;

        public bool Open { get; private set; }
        private Rect _windowRect = new Rect(40, 40, 860, 460);

        // Read by ScrollWheelBlockPatch so scrolling over an open menu
        // scrolls menu content instead of zooming the game camera - IMGUI
        // itself doesn't consume input the rest of the game reads, so the
        // camera's own Input polling needs to be zeroed directly.
        public static bool IsMouseOverOpenMenu { get; private set; }

        public readonly Theme Theme = new Theme();
        private readonly MenuStyles _styles = new MenuStyles();

        private readonly PlayerContext _playerContext;
        private readonly Keybinds _keybinds;
        private readonly FlyController _flyController;
        private readonly SpeedBoostController _speedBoostController;
        private readonly TeleportController _teleportController;
        private readonly KnockbackImmunityController _knockbackImmunityController;
        private readonly BodyRotationLockController _bodyRotationLockController;
        private readonly CrashWorkaroundController _crashWorkaroundController;
        private readonly SystemStatsController _systemStatsController;
        private readonly PromoPopupController _promoPopupController;
        private readonly UiDebugController _uiDebugController;
        private readonly PerformanceController _performanceController;
        private readonly WingsHiderController _wingsHiderController;
        private readonly string _crashDumpFolder;
        private readonly Action _onMenuClosed;

        private string _newWaypointNameInput = "";
        private Vector2 _waypointsScrollPosition;
        private Vector2 _movementScrollPosition;
        private Vector2 _settingsScrollPosition;
        private Vector2 _homeScrollPosition;

        private const float SidebarWidth = 210f;
        private const float TopBarHeight = 52f;
        private const float ContentPadding = 20f;
        private const string Version = "v1.2.0";

        // Drag-to-resize from the bottom-right corner, like a normal window.
        private const float MinWindowWidth = 760f;
        private const float MinWindowHeight = 420f;
        private const float MaxWindowWidth = 1400f;
        private const float MaxWindowHeight = 900f;
        private const float CornerGrabSize = 22f;
        private const float EdgeGrabThickness = 8f;
        private Vector2 _resizeStartMouseScreen;
        private Vector2 _resizeStartWindowSize;
        private bool _resizingWidth, _resizingHeight;

        // Set once per Draw() pass - every section reads these instead of
        // _windowRect directly, since the sidebar eats into the width and
        // the top bar into the height actually available for content.
        private float _contentWidth;

        private const float MinContentHeight = 300f;
        private const float ListRowHeight = 62f;

        // Lists size themselves to how much they actually contain, capped
        // so a 40-result search scrolls internally instead of stretching
        // the window past the screen. Deliberately independent of
        // _windowRect.height - deriving it from the window is what made the
        // window grow every frame.
        private static float ListHeightFor(int itemCount, float maxHeight)
        {
            if (itemCount <= 0)
                return 90f;
            return Mathf.Clamp(itemCount * ListRowHeight + 8f, 90f, maxHeight);
        }

        private readonly (Section Section, string Label, Func<Texture2D> Icon)[] _navItems;
        private readonly float[] _navIconScale;

        // Page-switch fade-in: content alpha ramps 0->1 over a few frames
        // whenever the active section changes.
        private const float SectionFadeSeconds = 0.15f;
        private Section _lastDrawnSection = Section.Home;
        private float _sectionFadeElapsed = SectionFadeSeconds;

        public MenuUI(PlayerContext playerContext, Keybinds keybinds, FlyController flyController,
            SpeedBoostController speedBoostController, TeleportController teleportController,
            KnockbackImmunityController knockbackImmunityController, BodyRotationLockController bodyRotationLockController,
            CrashWorkaroundController crashWorkaroundController, SystemStatsController systemStatsController,
            PromoPopupController promoPopupController, UiDebugController uiDebugController,
            PerformanceController performanceController, WingsHiderController wingsHiderController,
            string crashDumpFolder, Action onMenuClosed)
        {
            _playerContext = playerContext;
            _keybinds = keybinds;
            _flyController = flyController;
            _speedBoostController = speedBoostController;
            _teleportController = teleportController;
            _knockbackImmunityController = knockbackImmunityController;
            _bodyRotationLockController = bodyRotationLockController;
            _crashWorkaroundController = crashWorkaroundController;
            _systemStatsController = systemStatsController;
            _promoPopupController = promoPopupController;
            _uiDebugController = uiDebugController;
            _performanceController = performanceController;
            _wingsHiderController = wingsHiderController;
            _crashDumpFolder = crashDumpFolder;
            _onMenuClosed = onMenuClosed;

            _navItems = new (Section, string, Func<Texture2D>)[]
            {
                (Section.Home, "Home", () => Icons.Home),
                (Section.Movement, "Movement", () => Icons.Movement),
                (Section.Teleports, "Teleports", () => Icons.Teleport),
                (Section.Performance, "Performance", () => Icons.Movement),
                (Section.Crashes, "Crashes", () => Icons.Warning),
                (Section.Credits, "Credits", () => Icons.Info),
                (Section.Settings, "Settings", () => Icons.Settings),
            };
            _navIconScale = new float[_navItems.Length];
            for (int i = 0; i < _navIconScale.Length; i++)
                _navIconScale[i] = 1f;
        }

        public void ToggleOpen()
        {
            Open = !Open;
            if (!Open)
                _onMenuClosed?.Invoke();
        }

        public void Draw()
        {
            if (!Open)
            {
                IsMouseOverOpenMenu = false;
                return;
            }
            _styles.Rebuild(Theme);

            // Width is user-controlled (resize handles), so it's pinned.
            // Height is left free so the window grows to fit its content -
            // but the result is clamped before being stored, which is what
            // stops the runaway growth this had before: content sized off
            // the window height made the window taller, which made the
            // content taller again. Sections now size themselves off their
            // own content, never off _windowRect.height.
            _styles.Window.fixedWidth = _windowRect.width;
            _styles.Window.fixedHeight = 0f;

            Rect resultRect = GUILayout.Window(831201, _windowRect, DrawWindow, "", _styles.Window);
            _windowRect.x = resultRect.x;
            _windowRect.y = resultRect.y;
            _windowRect.height = Mathf.Clamp(resultRect.height, MinWindowHeight, MaxAutoWindowHeight);
            ClampWindowToScreen();
            IsMouseOverOpenMenu = _windowRect.Contains(Event.current.mousePosition);
        }

        // Never let the window be dragged fully out of reach, while still
        // allowing it to hang off an edge. Clamping x/y to [0, screen -
        // size] used to snap it hard to the top-left whenever the window
        // was wider than the game window, which made it look like every
        // drag was being undone. Keeping a strip of it on screen is enough.
        private const float MinVisibleEdge = 220f;

        private static float MaxAutoWindowHeight => Mathf.Min(MaxWindowHeight, Screen.height - 60f);

        private void ClampWindowToScreen()
        {
            _windowRect.x = Mathf.Clamp(_windowRect.x, MinVisibleEdge - _windowRect.width, Screen.width - MinVisibleEdge);
            _windowRect.y = Mathf.Clamp(_windowRect.y, 0f, Mathf.Max(0f, Screen.height - TopBarHeight));
        }

        // Grabbable resize zones along the whole right edge, whole bottom
        // edge, and a bigger bottom-right corner (which resizes both axes
        // at once) - like a native OS window border, not just one tiny
        // pixel-perfect corner. Standard IMGUI hot-control pattern so the
        // drag keeps tracking even if the mouse outruns the strip mid-drag.
        // _windowRect is mutated here for next frame; GUILayout.Window
        // re-lays-out content against whatever width/height we set since
        // every section sizes itself off _contentWidth rather than letting
        // content auto-size the window.
        private void DrawResizeHandles()
        {
            var cornerRect = new Rect(_windowRect.width - CornerGrabSize, _windowRect.height - CornerGrabSize, CornerGrabSize, CornerGrabSize);
            var rightEdgeRect = new Rect(_windowRect.width - EdgeGrabThickness, 0, EdgeGrabThickness, _windowRect.height - CornerGrabSize);
            var bottomEdgeRect = new Rect(0, _windowRect.height - EdgeGrabThickness, _windowRect.width - CornerGrabSize, EdgeGrabThickness);

            int controlId = GUIUtility.GetControlID(FocusType.Passive);
            Event e = Event.current;

            switch (e.GetTypeForControl(controlId))
            {
                case EventType.MouseDown:
                    if (e.button != 0)
                        break;
                    bool inCorner = cornerRect.Contains(e.mousePosition);
                    bool inRightEdge = rightEdgeRect.Contains(e.mousePosition);
                    bool inBottomEdge = bottomEdgeRect.Contains(e.mousePosition);
                    if (inCorner || inRightEdge || inBottomEdge)
                    {
                        GUIUtility.hotControl = controlId;
                        _resizingWidth = inCorner || inRightEdge;
                        _resizingHeight = inCorner || inBottomEdge;
                        _resizeStartMouseScreen = GUIUtility.GUIToScreenPoint(e.mousePosition);
                        _resizeStartWindowSize = new Vector2(_windowRect.width, _windowRect.height);
                        e.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == controlId)
                    {
                        Vector2 delta = GUIUtility.GUIToScreenPoint(e.mousePosition) - _resizeStartMouseScreen;
                        if (_resizingWidth)
                            _windowRect.width = Mathf.Clamp(_resizeStartWindowSize.x + delta.x, MinWindowWidth, MaxWindowWidth);
                        if (_resizingHeight)
                            _windowRect.height = Mathf.Clamp(_resizeStartWindowSize.y + delta.y, MinWindowHeight, MaxWindowHeight);
                        e.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == controlId)
                    {
                        GUIUtility.hotControl = 0;
                        e.Use();
                    }
                    break;
            }

            bool isActive = GUIUtility.hotControl == controlId;
            bool isHoveringEdge = rightEdgeRect.Contains(e.mousePosition) || bottomEdgeRect.Contains(e.mousePosition);
            bool isHoveringCorner = cornerRect.Contains(e.mousePosition);

            Color previousColor = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, isActive || isHoveringEdge ? 0.3f : 0.1f);
            GUI.DrawTexture(rightEdgeRect, Texture2D.whiteTexture);
            GUI.DrawTexture(bottomEdgeRect, Texture2D.whiteTexture);

            GUI.color = new Color(1f, 1f, 1f, isActive || isHoveringCorner ? 0.6f : 0.3f);
            for (int i = 0; i < 3; i++)
            {
                float offset = i * 6f;
                GUI.DrawTexture(new Rect(cornerRect.xMax - 3f - offset, cornerRect.yMax - 3f, 3f, 3f + offset), Texture2D.whiteTexture);
            }
            GUI.color = previousColor;
        }

        private void DrawWindow(int windowId)
        {
            _contentWidth = _windowRect.width - SidebarWidth - ContentPadding * 2f;

            GUILayout.BeginHorizontal();

            DrawSidebar();

            GUILayout.BeginVertical();
            DrawTopBar();
            DrawContentArea();
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();

            GUI.DragWindow(new Rect(SidebarWidth, 0, _windowRect.width - SidebarWidth, TopBarHeight));
            DrawWindowBorder();
            DrawResizeHandles();
        }

        // A visible outline around the whole window - without it, the
        // window has no edge distinguishing it from the game world behind
        // it, and the resize strips (near-invisible until hovered) are the
        // only other hint of where its boundary actually is.
        private void DrawWindowBorder()
        {
            Color previousColor = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.14f);
            float w = _windowRect.width;
            float h = _windowRect.height;
            GUI.DrawTexture(new Rect(0, 0, w, 1), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(0, h - 1, w, 1), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(0, 0, 1, h), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(w - 1, 0, 1, h), Texture2D.whiteTexture);
            GUI.color = previousColor;
        }

        private void DrawContentArea()
        {

            UpdateSectionFade();
            Color previousColor = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(_sectionFadeElapsed / SectionFadeSeconds));

            GUILayout.BeginVertical(GUILayout.Width(_contentWidth + ContentPadding * 2f), GUILayout.MinHeight(MinContentHeight));
            GUILayout.Space(ContentPadding);
            GUILayout.BeginHorizontal();
            GUILayout.Space(ContentPadding);
            GUILayout.BeginVertical(GUILayout.Width(_contentWidth));
            DrawActiveSection();
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            GUI.color = previousColor;
        }

        private void UpdateSectionFade()
        {
            if (_activeSection != _lastDrawnSection)
            {
                _lastDrawnSection = _activeSection;
                _sectionFadeElapsed = 0f;
            }
            else if (_sectionFadeElapsed < SectionFadeSeconds && Event.current.type == EventType.Repaint)
            {
                _sectionFadeElapsed += Time.unscaledDeltaTime;
            }
        }

        // --- sidebar --------------------------------------------------------

        private void DrawSidebar()
        {
            GUILayout.BeginVertical(_styles.Sidebar, GUILayout.Width(SidebarWidth), GUILayout.ExpandHeight(true));

            GUILayout.BeginHorizontal();
            GUILayout.Space(16);
            Rect dotRect = GUILayoutUtility.GetRect(9, 9, GUILayout.Width(9), GUILayout.Height(9));
            dotRect.y += 4;
            Color prev = GUI.color;
            GUI.color = Theme.Accent;
            GUI.DrawTexture(dotRect, Texture2D.whiteTexture);
            GUI.color = prev;
            GUILayout.Space(8);
            GUILayout.BeginVertical();
            GUILayout.Label("LOVE MENU", _styles.Header);
            GUILayout.Label(Version + "  ·  NNKtv28", _styles.Meta);
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();

            GUILayout.Space(18);

            for (int i = 0; i < _navItems.Length; i++)
                DrawSidebarNavItem(i);

            GUILayout.FlexibleSpace();

            Divider();
            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            GUILayout.Space(16);
            GUILayout.Label(_playerContext.PlayerName, _styles.Description);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Space(16);
            GUILayout.Label(_keybinds.MenuKey + "  close menu", _styles.Meta);
            GUILayout.EndHorizontal();
            GUILayout.Space(10);

            GUILayout.EndVertical();
        }

        private void DrawSidebarNavItem(int index)
        {
            bool isActive = _activeSection == _navItems[index].Section;
            GUIStyle style = isActive ? _styles.SidebarItemActive : _styles.SidebarItem;

            if (GUILayout.Button(GUIContent.none, style, GUILayout.Height(40), GUILayout.ExpandWidth(true)))
                _activeSection = _navItems[index].Section;

            Rect rowRect = GUILayoutUtility.GetLastRect();
            UpdateNavIconScale(index, isActive);

            if (isActive)
            {
                Color prev = GUI.color;
                GUI.color = Theme.Accent;
                GUI.DrawTexture(new Rect(rowRect.x, rowRect.y, 3, rowRect.height), Texture2D.whiteTexture);
                GUI.color = prev;
            }

            float iconSize = 18f * _navIconScale[index];
            Rect iconRect = new Rect(rowRect.x + 16f, rowRect.y + (rowRect.height - iconSize) / 2f, iconSize, iconSize);
            Color iconColor = isActive ? Color.white : style.normal.textColor;
            Color previousColor = GUI.color;
            GUI.color = iconColor;
            GUI.DrawTexture(iconRect, _navItems[index].Icon());
            GUI.color = previousColor;

            var labelRect = new Rect(rowRect.x + 44f, rowRect.y, rowRect.width - 50f, rowRect.height);
            GUIStyle labelStyle = new GUIStyle(style) { normal = { background = null, textColor = iconColor } };
            GUI.Label(labelRect, _navItems[index].Label, labelStyle);
        }

        private void UpdateNavIconScale(int index, bool isActive)
        {
            if (Event.current.type != EventType.Repaint)
                return;
            float target = isActive ? 1.1f : 1f;
            _navIconScale[index] = Mathf.MoveTowards(_navIconScale[index], target, Time.unscaledDeltaTime * 4f);
        }

        // --- top bar ----------------------------------------------------

        private void DrawTopBar()
        {
            GUILayout.BeginHorizontal(_styles.TopBar, GUILayout.Height(TopBarHeight), GUILayout.Width(_windowRect.width - SidebarWidth));
            GUILayout.Label(CurrentSectionLabel().ToUpperInvariant(), _styles.Header);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private string CurrentSectionLabel()
        {
            foreach (var navItem in _navItems)
                if (navItem.Section == _activeSection)
                    return navItem.Label;
            return "";
        }

        private void DrawActiveSection()
        {
            switch (_activeSection)
            {
                case Section.Home: DrawHomeSection(); break;
                case Section.Movement: DrawMovementSection(); break;
                case Section.Teleports: DrawTeleportsSection(); break;
                case Section.Performance: DrawPerformanceSection(); break;
                case Section.Crashes: DrawCrashesSection(); break;
                case Section.Credits: DrawCreditsSection(); break;
                case Section.Settings: DrawSettingsSection(); break;
            }
        }

        // Small helpers used by every page for the shared card/row look.

        private void BeginCard() => GUILayout.BeginVertical(_styles.Card);
        private void EndCard() => GUILayout.EndVertical();

        private void CardHeaderRow(string title, string description, bool on, Action onToggle)
        {
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical();
            GUILayout.Label(title, _styles.Label);
            if (!string.IsNullOrEmpty(description))
                GUILayout.Label(description, _styles.Description);
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(on ? "ON" : "OFF", on ? _styles.ToggleOn : _styles.ToggleOff, GUILayout.Width(60)))
                onToggle();
            GUILayout.EndHorizontal();
        }

        private void Divider() => GUILayout.Box("", _styles.Divider, GUILayout.ExpandWidth(true));

        private void DrawEmptyState(string message)
        {
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.Label(message, _styles.Description);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.FlexibleSpace();
        }

        // --- Home: status dashboard -----------------------------------------

        private void DrawHomeSection()
        {
            _homeScrollPosition = GUILayout.BeginScrollView(_homeScrollPosition, GUILayout.ExpandHeight(true));

            GUILayout.BeginHorizontal();
            DrawStatusCard("Fly", _flyController.Flying ? "Enabled" : "Disabled", _flyController.Flying);
            DrawStatusCard("Movement Speed", _speedBoostController.Enabled ? _speedBoostController.Multiplier.ToString("0.0") + "x" : "Disabled", _speedBoostController.Enabled);
            DrawStatusCard("Knockback Immunity", _knockbackImmunityController.Enabled ? "Enabled" : "Disabled", _knockbackImmunityController.Enabled);
            GUILayout.EndHorizontal();

            GUILayout.Space(14);
            GUILayout.Label("SYSTEM", _styles.CardTitle);
            GUILayout.Space(6);

            float graphCardWidth = (_contentWidth - 8f) / 2f;
            GUILayout.BeginHorizontal();
            DrawGraphCard("Frames Per Second", _systemStatsController.FramesPerSecond.ToString("0"),
                _systemStatsController.FpsHistory, Theme.Accent, graphCardWidth);
            DrawGraphCard("RAM (MB)", SystemStatsController.BytesToMB(_systemStatsController.WorkingSetBytes()).ToString("0"),
                _systemStatsController.RamHistoryMB, Theme.Accent, graphCardWidth);
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            DrawStatusCard("Frame Time", _systemStatsController.FrameTimeMs.ToString("0.0") + " ms", false);
            long vramBytes = _systemStatsController.GraphicsMemoryBytes();
            DrawStatusCard("VRAM", _systemStatsController.HasVramReading ? SystemStatsController.BytesToMB(vramBytes).ToString("0") + " MB" : "n/a", false);
            DrawStatusCard("Managed Heap", SystemStatsController.BytesToMB(_systemStatsController.ManagedMemoryBytes()).ToString("0") + " MB", false);
            DrawStatusCard("Game Uptime", _systemStatsController.GameUptime(), false);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            DrawStatusCard("Position", PositionText(), false);
            DrawStatusCard("Scene", UnityEngine.SceneManagement.SceneManager.GetActiveScene().name, false);
            DrawStatusCard("Camera Mode", _uiDebugController.CameraType, false);
            DrawStatusCard("Movement Blocked",
                _uiDebugController.MovementDisabled ? "YES (camera)" : _uiDebugController.InputDisabled ? "YES (input)" : "No",
                _uiDebugController.MovementDisabled || _uiDebugController.InputDisabled);
            GUILayout.EndHorizontal();

            GUILayout.EndScrollView();
        }

        // Pads above and below the actual sample range so the line sits in
        // the middle of the graph. Anchoring the range at zero pinned a
        // steady 160 FPS or 3.6 GB RAM line flat against the top edge with
        // dead space underneath, and hid all the small variation that's
        // the entire point of plotting it.
        private static (float min, float max) AutoRange(List<float> samples)
        {
            if (samples.Count == 0)
                return (0f, 1f);

            float min = samples[0];
            float max = samples[0];
            foreach (float sample in samples)
            {
                if (sample < min) min = sample;
                if (sample > max) max = sample;
            }

            float span = max - min;
            if (span < 0.0001f)
                return (min - 1f, max + 1f);

            float padding = span * 0.25f;
            return (min - padding, max + padding);
        }

        private void DrawGraphCard(string title, string currentValueText, List<float> samples, Color lineColor, float cardWidth)
        {
            (float minValue, float maxValue) = AutoRange(samples);

            GUILayout.BeginVertical(_styles.Card, GUILayout.Width(cardWidth));
            GUILayout.BeginHorizontal();
            GUILayout.Label(title.ToUpperInvariant(), _styles.CardTitle);
            GUILayout.FlexibleSpace();
            GUILayout.Label(currentValueText, _styles.CardValue);
            GUILayout.EndHorizontal();
            GUILayout.Space(6);

            Rect graphRect = GUILayoutUtility.GetRect(10, 74, GUILayout.ExpandWidth(true));
            GraphRenderer.DrawLineGraph(graphRect, samples, lineColor, minValue, maxValue);

            GUILayout.BeginHorizontal();
            GUILayout.Label(samples.Count == 0 ? "" : minValue.ToString("0"), _styles.Meta);
            GUILayout.FlexibleSpace();
            GUILayout.Label(samples.Count == 0 ? "collecting..." : maxValue.ToString("0"), _styles.Meta);
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }

        private string PositionText()
        {
            if (_playerContext.Avatar == null)
                return "—";
            Vector3 position = _playerContext.Avatar.transform.position;
            return position.x.ToString("0.0") + ", " + position.y.ToString("0.0") + ", " + position.z.ToString("0.0");
        }

        private const float StatusCardHeight = 62f;

        private void DrawStatusCard(string title, string value, bool active)
        {
            // Fixed height and non-wrapping title keep every card in a row
            // the same size - a title that wrapped to two lines used to make
            // its card taller and leave the whole row ragged.
            GUILayout.BeginVertical(_styles.Card, GUILayout.Width((_contentWidth - 24f) / 4f), GUILayout.Height(StatusCardHeight));
            GUILayout.BeginHorizontal();
            GUILayout.Label(title.ToUpperInvariant(), _styles.CardTitle);
            GUILayout.FlexibleSpace();
            if (active)
            {
                Rect dot = GUILayoutUtility.GetRect(7, 7, GUILayout.Width(7), GUILayout.Height(7));
                Color prev = GUI.color;
                GUI.color = Theme.Accent;
                GUI.DrawTexture(dot, Texture2D.whiteTexture);
                GUI.color = prev;
            }
            GUILayout.EndHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.Label(value, _styles.CardValue);
            GUILayout.EndVertical();
        }

        // --- Movement -----------------------------------------------------

        private void DrawMovementSection()
        {
            _movementScrollPosition = GUILayout.BeginScrollView(_movementScrollPosition, GUILayout.ExpandHeight(true));

            BeginCard();
            CardHeaderRow("Fly", _keybinds.FlyUpKey + " = up  ·  " + _keybinds.FlyDownKey + " = down  ·  WASD move",
                _flyController.Flying, _flyController.Toggle);
            GUILayout.Space(6);
            GUILayout.Label("Speed   " + _flyController.Speed.ToString("0.0"), _styles.Description);
            _flyController.Speed = GUILayout.HorizontalSlider(_flyController.Speed, 1f, 25f);
            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Unstick (pop up)", _styles.ToggleOff))
                _flyController.Unstick();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            EndCard();

            BeginCard();
            CardHeaderRow("Lock Body Rotation", "Stops right-click camera drag from also turning your avatar",
                _bodyRotationLockController.Enabled, () => _bodyRotationLockController.SetEnabled(!_bodyRotationLockController.Enabled));
            EndCard();

            BeginCard();
            CardHeaderRow("Movement Speed", "Multiplies normal walk speed",
                _speedBoostController.Enabled, () => _speedBoostController.SetEnabled(!_speedBoostController.Enabled));
            GUILayout.Space(6);
            GUILayout.Label("Multiplier   " + _speedBoostController.Multiplier.ToString("0.0") + "x", _styles.Description);
            float newMultiplier = GUILayout.HorizontalSlider(_speedBoostController.Multiplier, 1f, 4f);
            if (!Mathf.Approximately(newMultiplier, _speedBoostController.Multiplier))
            {
                _speedBoostController.Multiplier = newMultiplier;
                if (_speedBoostController.Enabled)
                    _speedBoostController.ApplyMultiplier();
            }
            EndCard();

            BeginCard();
            CardHeaderRow("Knockback Immunity", "Reverts sudden shoves (snowballs, etc.)",
                _knockbackImmunityController.Enabled, () => _knockbackImmunityController.Enabled = !_knockbackImmunityController.Enabled);
            EndCard();

            GUILayout.EndScrollView();
        }

        // --- Teleports ----------------------------------------------------

        // Waypoints and Friends share the top row; Quest Objects sits
        // underneath across the full width, where long object names
        // actually fit.
        private void DrawTeleportsSection()
        {
            float halfWidth = _contentWidth / 2f - 8f;
            float topRowHeight = 190f;
            _teleportColumnWidth = 0f; // quest cards span the full content width now

            GUILayout.BeginHorizontal(GUILayout.Height(topRowHeight));

            GUILayout.BeginVertical(GUILayout.Width(halfWidth));
            DrawWaypointsSubsection(ListHeightFor(_teleportController.Waypoints.Count, topRowHeight - 58f));
            GUILayout.EndVertical();

            GUILayout.Space(16);

            GUILayout.BeginVertical();
            DrawTeleportToFriendSubsection(ListHeightFor(_teleportController.NearbyFriends.Count, topRowHeight - 84f));
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();

            GUILayout.Space(10);
            DrawCollectiblesSubsection(ListHeightFor(_teleportController.NearbyCollectibles.Count, 360f));
        }

        private float _teleportColumnWidth;

        // Lists only ever need to scroll vertically - leaving the
        // horizontal bar enabled added a stray scrollbar along the bottom
        // of every list and stole a row of height.
        private Vector2 BeginVerticalOnlyScrollView(Vector2 scrollPosition, float height)
        {
            return GUILayout.BeginScrollView(scrollPosition, false, false,
                GUIStyle.none, GUI.skin.verticalScrollbar, GUI.skin.scrollView, GUILayout.Height(height));
        }

        private Vector2 _collectiblesScrollPosition;

        // Matches the quest types the tracker actually lists (bottles,
        // letters, potions, pets) - one click instead of retyping.
        private static readonly string[] QuestSearchQuickTerms = { "bottle", "letter", "potion", "pet" };

        private void DrawCollectiblesSubsection(float listHeight)
        {
            GUILayout.Label("Quest Objects", _styles.Label);
            GUILayout.Label("Finds objects in this room by name - bottles, letters, anything.", _styles.Description);
            GUILayout.Space(6);

            GUILayout.BeginHorizontal();
            _teleportController.CollectibleSearchTerm = GUILayout.TextField(_teleportController.CollectibleSearchTerm, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("Find", _styles.ToggleOn, GUILayout.Width(60)))
                _teleportController.RefreshNearbyCollectibles();
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(_teleportController.StrictNames ? "Strict: ON" : "Strict: OFF",
                _teleportController.StrictNames ? _styles.ToggleOn : _styles.ToggleOff, GUILayout.Width(90)))
            {
                _teleportController.StrictNames = !_teleportController.StrictNames;
                _teleportController.RefreshNearbyCollectibles();
            }
            foreach (string quickTerm in QuestSearchQuickTerms)
            {
                bool isCurrent = string.Equals(_teleportController.CollectibleSearchTerm, quickTerm, StringComparison.OrdinalIgnoreCase);
                if (GUILayout.Button(quickTerm, isCurrent ? _styles.ToggleOn : _styles.Chip))
                {
                    _teleportController.CollectibleSearchTerm = quickTerm;
                    _teleportController.RefreshNearbyCollectibles();
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(6);

            _collectiblesScrollPosition = BeginVerticalOnlyScrollView(_collectiblesScrollPosition, listHeight);
            if (_teleportController.NearbyCollectibles.Count == 0)
            {
                DrawEmptyState(string.IsNullOrEmpty(_teleportController.CollectibleSearchStatus)
                    ? "Type what to look for, then Find."
                    : _teleportController.CollectibleSearchStatus);
            }
            else
            {
                foreach (var (objectName, objectPosition) in _teleportController.NearbyCollectibles)
                    DrawCollectibleCard(objectName, objectPosition);
            }
            GUILayout.EndScrollView();

            if (_teleportController.NearbyCollectibles.Count > 0)
                GUILayout.Label(_teleportController.CollectibleSearchStatus, _styles.Meta);
        }

        private void DrawCollectibleCard(string objectName, Vector3 objectPosition)
        {
            // The text block gets an explicit width so a long object name
            // can't expand the row and shove the Go button out of view.
            float rightColumnWidth = Mathf.Max(120f, _contentWidth - _teleportColumnWidth - 60f);
            float textWidth = Mathf.Max(60f, rightColumnWidth - 110f);

            BeginCard();
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width(textWidth));
            GUILayout.Label(objectName, _styles.CardValue);
            GUILayout.Label(_teleportController.DistanceFromPlayer(objectPosition).ToString("0") + "m away", _styles.Description);
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Go", _styles.ToggleOn, GUILayout.Width(44)))
                _teleportController.GoToPosition(objectPosition);
            GUILayout.EndHorizontal();
            EndCard();
        }

        private void DrawWaypointsSubsection(float listHeight)
        {
            GUILayout.Label("Waypoints", _styles.Label);
            GUILayout.BeginHorizontal();
            _newWaypointNameInput = GUILayout.TextField(_newWaypointNameInput, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("Save spot", _styles.ToggleOff, GUILayout.Width(110)))
            {
                _teleportController.SaveWaypoint(_newWaypointNameInput);
                _newWaypointNameInput = "";
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(6);

            _waypointsScrollPosition = BeginVerticalOnlyScrollView(_waypointsScrollPosition, listHeight);
            if (_teleportController.Waypoints.Count == 0)
                DrawEmptyState("No waypoints saved yet.");
            for (int waypointIndex = _teleportController.Waypoints.Count - 1; waypointIndex >= 0; waypointIndex--)
                DrawWaypointCard(waypointIndex);
            GUILayout.EndScrollView();
        }

        private void DrawWaypointCard(int waypointIndex)
        {
            Waypoint waypoint = _teleportController.Waypoints[waypointIndex];
            BeginCard();
            GUILayout.BeginHorizontal();
            GUILayout.Label(waypoint.Name, _styles.Label);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Go", _styles.ToggleOn, GUILayout.Width(40)))
                _teleportController.GoToPosition(waypoint.Position);
            if (GUILayout.Button("×", _styles.ToggleOff, GUILayout.Width(28)))
                _teleportController.Waypoints.RemoveAt(waypointIndex);
            GUILayout.EndHorizontal();
            GUILayout.Label(
                "X " + waypoint.Position.x.ToString("0.0") +
                "   Y " + waypoint.Position.y.ToString("0.0") +
                "   Z " + waypoint.Position.z.ToString("0.0"), _styles.Description);
            EndCard();
        }

        private void DrawTeleportToFriendSubsection(float listHeight)
        {
            GUILayout.Label("Teleport to Friend", _styles.Label);
            GUILayout.Label("Only friends currently visible in this room.", _styles.Description);
            GUILayout.Space(6);

            GUILayout.BeginVertical(GUILayout.Height(listHeight));
            if (_teleportController.NearbyFriends.Count == 0)
            {
                DrawEmptyState(string.IsNullOrEmpty(_teleportController.FriendSearchStatus)
                    ? "No visible friends yet."
                    : _teleportController.FriendSearchStatus);
            }
            else
            {
                foreach (var (friendName, friendPosition) in _teleportController.NearbyFriends)
                    DrawNearbyFriendCard(friendName, friendPosition);
            }
            GUILayout.EndVertical();

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Refresh", _styles.ToggleOff, GUILayout.Width(110)))
                _teleportController.RefreshNearbyFriends();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private void DrawNearbyFriendCard(string friendName, Vector3 friendPosition)
        {
            BeginCard();
            GUILayout.BeginHorizontal();
            Rect dot = GUILayoutUtility.GetRect(7, 7, GUILayout.Width(7), GUILayout.Height(7));
            dot.y += 3;
            Color prev = GUI.color;
            GUI.color = Theme.Accent;
            GUI.DrawTexture(dot, Texture2D.whiteTexture);
            GUI.color = prev;
            GUILayout.Space(4);
            GUILayout.Label(friendName, _styles.Label);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Teleport", _styles.ToggleOn, GUILayout.Width(80)))
                _teleportController.GoToFriend(friendPosition);
            GUILayout.EndHorizontal();
            EndCard();
        }

        // --- Performance --------------------------------------------------

        private Vector2 _performanceScrollPosition;

        private void DrawPerformanceSection()
        {
            _performanceScrollPosition = GUILayout.BeginScrollView(_performanceScrollPosition, GUILayout.ExpandHeight(true));

            BeginCard();
            GUILayout.Label("Memory", _styles.Label);
            GUILayout.Label(
                "Unity only unloads unused assets when the scene changes. In a busy hub, avatars " +
                "stream in and out constantly and the assets of players who already left stay in " +
                "memory until you change zone - which is why RAM climbs into the multi-GB range " +
                "while standing still. This runs the engine's own unload pass on demand.",
                _styles.Description);
            GUILayout.Space(4);
            GUILayout.Label("Costs a brief freeze (~0.3s) while it runs.", _styles.Meta);
            GUILayout.Space(8);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Free Unused Memory Now", _styles.ToggleOn, GUILayout.Width(200)))
                _performanceController.FreeUnusedMemory();
            GUILayout.Space(10);
            GUILayout.Label(_performanceController.LastFreeResult, _styles.Description);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Space(10);
            CardHeaderRow("Auto-free when RAM is high",
                "Runs the same pass automatically, at most once every 90s",
                _performanceController.AutoFreeEnabled,
                () => _performanceController.AutoFreeEnabled = !_performanceController.AutoFreeEnabled);
            GUILayout.Label("Trigger above   " + _performanceController.AutoFreeThresholdMB.ToString("0") + " MB", _styles.Description);
            _performanceController.AutoFreeThresholdMB = GUILayout.HorizontalSlider(_performanceController.AutoFreeThresholdMB, 2000f, 12000f);
            EndCard();

            BeginCard();
            CardHeaderRow("Hide Player Wings",
                "Wing attachments are multi-LOD skinned meshes worn by many players at once - " +
                "hiding them is the cheapest win in a crowded room",
                _wingsHiderController.Enabled, () => _wingsHiderController.Enabled = !_wingsHiderController.Enabled);
            if (_wingsHiderController.Enabled)
                GUILayout.Label(_wingsHiderController.HiddenCount + " wing renderer(s) hidden - toggling off restores them", _styles.Meta);
            EndCard();

            BeginCard();
            GUILayout.Label("Graphics", _styles.Label);
            GUILayout.Label("Live Unity quality settings - lower costs less memory and gives more frames.", _styles.Description);
            GUILayout.Space(8);

            string[] textureQualityNames = { "Full", "Half", "Quarter", "Eighth" };
            int currentTextureLimit = Mathf.Clamp(_performanceController.TextureQualityLimit, 0, textureQualityNames.Length - 1);
            GUILayout.Label("Texture Resolution   " + textureQualityNames[currentTextureLimit], _styles.Description);
            GUILayout.BeginHorizontal();
            for (int i = 0; i < textureQualityNames.Length; i++)
            {
                if (GUILayout.Button(textureQualityNames[i], i == currentTextureLimit ? _styles.ToggleOn : _styles.ToggleOff))
                    _performanceController.TextureQualityLimit = i;
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("Biggest single RAM/VRAM saving available. Takes effect as textures reload.", _styles.Meta);

            GUILayout.Space(10);
            GUILayout.Label("Shadow Distance   " + _performanceController.ShadowDistance.ToString("0"), _styles.Description);
            _performanceController.ShadowDistance = GUILayout.HorizontalSlider(_performanceController.ShadowDistance, 0f, 200f);

            GUILayout.Space(6);
            GUILayout.Label("LOD Bias   " + _performanceController.LodBias.ToString("0.00") +
                "   (lower = simpler models sooner)", _styles.Description);
            _performanceController.LodBias = GUILayout.HorizontalSlider(_performanceController.LodBias, 0.3f, 2f);

            GUILayout.Space(10);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Reset to game defaults", _styles.ToggleOff, GUILayout.Width(200)))
                _performanceController.ResetGraphicsToGameDefaults();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            EndCard();

            GUILayout.EndScrollView();
        }

        // --- Crashes ------------------------------------------------------

        private void DrawCrashesSection()
        {
            DrawDiagnosticsCard();

            bool nothingToShow = _crashWorkaroundController.Workarounds.Count == 0 &&
                _crashWorkaroundController.ConfirmedFixes.Count == 0;
            if (nothingToShow)
            {
                BeginCard();
                GUILayout.Label("No confirmed crash causes yet.", _styles.Label);
                GUILayout.Space(6);
                GUILayout.Label(
                    "LoveMenuLauncher.exe now saves every session's logs permanently to its own " +
                    "Logs\\ folder (bepinex_history.log, plus archived Unity player logs) instead " +
                    "of overwriting them each relaunch. Next time the game actually crashes or " +
                    "hangs, that history has what's needed to find the real cause - a toggle gets " +
                    "added here once one's confirmed.",
                    _styles.Description);
                EndCard();
                return;
            }

            foreach (CrashWorkaround workaround in _crashWorkaroundController.Workarounds)
                DrawCrashWorkaroundCard(workaround);

            foreach (ConfirmedCrashFix fix in _crashWorkaroundController.ConfirmedFixes)
                DrawConfirmedFixCard(fix);
        }

        private void DrawDiagnosticsCard()
        {
            BeginCard();
            GUILayout.Label("Diagnostics", _styles.Label);
            GUILayout.Label(
                "Watches for the main thread going quiet for 10s and writes a real .dmp crash " +
                "dump the moment that happens - useful for hangs, which otherwise leave no trace " +
                "at all (no exception, no Windows crash report). Exceptions are also logged here " +
                "immediately, separately from Player.log.",
                _styles.Description);
            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Open Dumps Folder", _styles.ToggleOff, GUILayout.Width(160)))
                OpenCrashDumpFolder();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            EndCard();
        }

        private void OpenCrashDumpFolder()
        {
            try
            {
                System.IO.Directory.CreateDirectory(_crashDumpFolder);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + _crashDumpFolder + "\"");
            }
            catch
            {
                // best effort - not worth surfacing a failure to open a folder
            }
        }

        private void DrawConfirmedFixCard(ConfirmedCrashFix fix)
        {
            BeginCard();

            GUILayout.BeginHorizontal();
            Rect dotRect = GUILayoutUtility.GetRect(9, 9, GUILayout.Width(9), GUILayout.Height(9));
            dotRect.y += 3;
            Color prev = GUI.color;
            GUI.color = Theme.Accent;
            GUI.DrawTexture(dotRect, Texture2D.whiteTexture);
            GUI.color = prev;
            GUILayout.Space(6);
            GUILayout.Label("FIXED", new GUIStyle(_styles.Description) { normal = { textColor = Theme.Accent }, fontStyle = FontStyle.Bold });
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            GUILayout.Label(fix.Name, _styles.Label);
            GUILayout.Label(fix.Description, _styles.Description);

            EndCard();
        }

        private void DrawCrashWorkaroundCard(CrashWorkaround workaround)
        {
            GUILayout.BeginVertical(_styles.WarningCard);

            GUILayout.BeginHorizontal();
            Rect iconRect = GUILayoutUtility.GetRect(16, 16, GUILayout.Width(16), GUILayout.Height(16));
            Color prev = GUI.color;
            GUI.color = new Color(1f, 0.7f, 0.3f);
            GUI.DrawTexture(iconRect, Icons.Warning);
            GUI.color = prev;
            GUILayout.Space(6);
            GUILayout.Label("EXPERIMENTAL", new GUIStyle(_styles.Description) { normal = { textColor = new Color(1f, 0.7f, 0.3f) }, fontStyle = FontStyle.Bold });
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            GUILayout.Label(workaround.Name, _styles.Label);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(workaround.Enabled ? "ON" : "OFF", workaround.Enabled ? _styles.ToggleOn : _styles.ToggleOff, GUILayout.Width(60)))
                _crashWorkaroundController.SetEnabled(workaround, !workaround.Enabled);
            GUILayout.EndHorizontal();
            GUILayout.Label(workaround.Description, _styles.Description);
            GUILayout.Space(4);
            GUILayout.Label("Disable it if it causes unexpected behavior.", _styles.Meta);

            GUILayout.EndVertical();
        }

        // --- Credits --------------------------------------------------

        private void DrawCreditsSection()
        {
            BeginCard();
            GUILayout.Label("LOVE MENU", _styles.Header);
            GUILayout.Label("Version " + Version, _styles.Description);
            EndCard();

            BeginCard();
            GUILayout.Label("AUTHOR", _styles.Description);
            GUILayout.Label("NNKtv28", _styles.Label);
            if (GUILayout.Button("nikicoding.com", _styles.ToggleOff, GUILayout.Width(160)))
                Application.OpenURL("https://nikicoding.com");
            EndCard();

            BeginCard();
            GUILayout.Label("MOD LOADER", _styles.Description);
            GUILayout.Label("BepInEx 5", _styles.Label);
            GUILayout.Label("HarmonyLib", _styles.Label);
            EndCard();

            BeginCard();
            GUILayout.Label("BUILT WITH REFERENCE TO", _styles.Description);
            GUILayout.Label("Assembly-CSharp.dll, VWW.CoreLibs.* - read via reflection for real\nclass/field/method names, nothing copied or included.", _styles.Label);
            GUILayout.Space(4);
            GUILayout.Label("ILSpy - decompiled to find the real send/event code paths.", _styles.Label);
            GUILayout.Space(4);
            GUILayout.Label("lcmem - this project's own live memory-reading toolkit, used for\ninvestigation before this became a proper BepInEx plugin.", _styles.Label);
            EndCard();
        }

        // --- Settings ---------------------------------------------------

        private void DrawSettingsSection()
        {
            _settingsScrollPosition = GUILayout.BeginScrollView(_settingsScrollPosition, GUILayout.ExpandHeight(true));
            GUILayout.BeginHorizontal();

            GUILayout.BeginVertical(GUILayout.Width(_contentWidth / 2f - 12f));
            DrawAppearanceSubsection();
            GUILayout.EndVertical();

            GUILayout.BeginVertical();
            DrawKeybindsSubsection();
            DrawPopupsSubsection();
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();
            GUILayout.EndScrollView();
        }

        private void DrawPopupsSubsection()
        {
            BeginCard();
            CardHeaderRow("Hide Promo Popups", "Closes the sale / promotions popups for the first 45s after spawning - shop and help stay openable",
                _promoPopupController.Enabled, () => _promoPopupController.Enabled = !_promoPopupController.Enabled);
            EndCard();
        }

        private void DrawAppearanceSubsection()
        {
            BeginCard();
            GUILayout.Label("Appearance", _styles.Label);

            GUILayout.Space(8);
            GUILayout.Label("Accent Color", _styles.Description);
            DrawAccentSwatches();

            GUILayout.Space(10);
            GUILayout.Label("Background", _styles.Description);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Game", NearlyEqual(Theme.Alpha, Theme.BackgroundGame) ? _styles.ToggleOn : _styles.ToggleOff))
                Theme.Alpha = Theme.BackgroundGame;
            if (GUILayout.Button("Dimmed", NearlyEqual(Theme.Alpha, Theme.BackgroundDimmed) ? _styles.ToggleOn : _styles.ToggleOff))
                Theme.Alpha = Theme.BackgroundDimmed;
            if (GUILayout.Button("Solid", NearlyEqual(Theme.Alpha, Theme.BackgroundSolid) ? _styles.ToggleOn : _styles.ToggleOff))
                Theme.Alpha = Theme.BackgroundSolid;
            GUILayout.EndHorizontal();
            GUILayout.Label("Fine transparency   " + Theme.Alpha.ToString("0.00"), _styles.Description);
            Theme.Alpha = GUILayout.HorizontalSlider(Theme.Alpha, 0.4f, 1f);

            GUILayout.Space(10);
            GUILayout.Label("UI Scale   " + Theme.UIScale.ToString("0.00") + "x", _styles.Description);
            Theme.UIScale = GUILayout.HorizontalSlider(Theme.UIScale, 0.85f, 1.3f);
            EndCard();
        }

        private static bool NearlyEqual(float a, float b) => Mathf.Abs(a - b) < 0.01f;

        private void DrawAccentSwatches()
        {
            GUILayout.BeginHorizontal();
            for (int i = 0; i < Theme.AccentNames.Length; i++)
                DrawAccentSwatch(i);
            GUILayout.EndHorizontal();
        }

        private void DrawAccentSwatch(int accentIndex)
        {
            bool isSelected = Theme.Index == accentIndex;
            if (GUILayout.Button(isSelected ? "●" : "○", GUILayout.Width(36), GUILayout.Height(28)))
                Theme.Index = accentIndex;
        }

        private void DrawKeybindsSubsection()
        {
            BeginCard();
            GUILayout.Label("Keybinds", _styles.Label);
            GUILayout.Space(6);
            DrawKeybindRow("Menu", Keybinds.RebindTarget.Menu, _keybinds.MenuKey);
            DrawKeybindRow("Fly up", Keybinds.RebindTarget.Up, _keybinds.FlyUpKey);
            DrawKeybindRow("Fly down", Keybinds.RebindTarget.Down, _keybinds.FlyDownKey);
            if (_keybinds.Rebinding != Keybinds.RebindTarget.None)
                GUILayout.Label("Press any key... (Esc to cancel)", _styles.Description);
            EndCard();
        }

        private void DrawKeybindRow(string label, Keybinds.RebindTarget target, KeyCode currentKey)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _styles.Label, GUILayout.Width(80));
            GUILayout.FlexibleSpace();
            bool isWaitingForKeyPress = _keybinds.Rebinding == target;
            string buttonLabel = isWaitingForKeyPress ? "..." : currentKey.ToString();
            if (GUILayout.Button(buttonLabel, isWaitingForKeyPress ? _styles.ToggleOn : _styles.Keycap, GUILayout.Width(100)))
                _keybinds.BeginRebind(target);
            GUILayout.EndHorizontal();
            GUILayout.Space(4);
        }
    }
}
