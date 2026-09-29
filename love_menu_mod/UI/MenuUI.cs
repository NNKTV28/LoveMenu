using System;
using System.Collections.Generic;
using UnityEngine;
using FlyMod.Core;
using FlyMod.Features;
using FlyMod.Inputs;

namespace FlyMod.UI
{
    // The Love Menu window (approved 1.3 design): a sidebar for navigation,
    // a header with the page title, search and close, and a scrolling page
    // of cards laid out in columns. It reads and writes state on the
    // controllers it's given and holds no feature state of its own.
    //
    // Clicks that change what's drawn (switching pages, toggles that show or
    // hide rows) are queued with Later() and applied after the window has
    // finished drawing, so IMGUI's layout and repaint passes always see the
    // same controls.
    internal class MenuUI
    {
        public const string Version = "1.3.1";

        private enum Section { Home, Movement, Camera, Teleports, Performance, Crashes, Settings, Credits }

        private static readonly (Section Section, string Title, string Subtitle)[] Pages =
        {
            (Section.Home, "Home", "What is on right now, and how the game is running."),
            (Section.Movement, "Movement", "Fly, speed and staying on your feet."),
            (Section.Camera, "Camera", "Field of view, zoom and clean screenshots."),
            (Section.Teleports, "Teleports", "Find things in the room, save spots, reach friends."),
            (Section.Performance, "Performance", "Keep busy rooms smooth."),
            (Section.Crashes, "Crashes", "What the menu does to keep the game running."),
            (Section.Settings, "Settings", "Keys, look and size of the menu."),
            (Section.Credits, "Credits", "Who made this and where to get help."),
        };

        // What the header search can jump to.
        private static readonly (string Label, Section Section)[] SearchIndex =
        {
            ("Fly", Section.Movement), ("Fly speed", Section.Movement), ("Unstick me", Section.Movement),
            ("Movement speed", Section.Movement), ("Knockback immunity", Section.Movement), ("Lock body rotation", Section.Movement),
            ("Field of view (FOV)", Section.Camera), ("Zoom-out limit", Section.Camera), ("Screenshot mode", Section.Camera),
            ("Find objects and keys", Section.Teleports), ("Waypoints", Section.Teleports), ("Teleport to a friend", Section.Teleports),
            ("Free memory", Section.Performance), ("Auto-free RAM", Section.Performance), ("Hide player wings", Section.Performance),
            ("Texture resolution", Section.Performance), ("Shadow distance", Section.Performance), ("LOD bias", Section.Performance),
            ("Crash dumps", Section.Crashes), ("DynamicBones guard", Section.Crashes),
            ("Keys and hotkeys", Section.Settings), ("Hide promo popups", Section.Settings), ("Accent color", Section.Settings),
            ("Background opacity", Section.Settings), ("Menu size", Section.Settings), ("Mini overlay", Section.Settings),
            ("Reset all settings", Section.Settings), ("Check for updates", Section.Home), ("Version", Section.Credits),
        };

        // Matches the room objects players look for most.
        private static readonly string[] QuickSearchTerms = { "key", "letter", "potion", "pet" };

        public bool Open { get; private set; }
        public static bool IsMouseOverOpenMenu { get; private set; }
        public Rect WindowRect => _windowRect;

        public readonly Theme Theme = new Theme();
        public readonly MenuStyles Styles = new MenuStyles();
        public string DismissedUpdateVersion = "";

        private readonly PlayerContext _playerContext;
        private readonly Keybinds _keybinds;
        private readonly FeatureHotkeys _hotkeys;
        private readonly FlyController _flyController;
        private readonly SpeedBoostController _speedBoostController;
        private readonly TeleportController _teleportController;
        private readonly KnockbackImmunityController _knockbackImmunityController;
        private readonly BodyRotationLockController _bodyRotationLockController;
        private readonly CameraController _cameraController;
        private readonly CrashWorkaroundController _crashWorkaroundController;
        private readonly SystemStatsController _systemStatsController;
        private readonly PromoPopupController _promoPopupController;
        private readonly PerformanceController _performanceController;
        private readonly WingsHiderController _wingsHiderController;
        private readonly MiniOverlay _overlay;
        private readonly UpdateChecker _updateChecker;
        private readonly string _crashDumpFolder;
        private readonly Action _onMenuClosed;
        private readonly Action _onResetAllSettings;

        private MenuStyles S => Styles;
        private Section _section = Section.Home;
        private Rect _windowRect = new Rect(60, 60, 1040, 660);
        private bool _windowPlaced;
        private readonly Dictionary<Section, Vector2> _scroll = new Dictionary<Section, Vector2>();
        private readonly List<Action> _deferred = new List<Action>();

        private string _searchQuery = "";
        private Rect _searchFieldRect;
        private readonly List<(Rect Rect, Section Section)> _searchResultRects = new List<(Rect, Section)>();
        private const string SearchControlName = "LoveMenuSearch";

        private string _newWaypointName = "";
        private Vector2 _collectiblesScroll, _waypointsScroll;
        private float _resetConfirmUntil;

        public MenuUI(PlayerContext playerContext, Keybinds keybinds, FeatureHotkeys hotkeys, FlyController flyController,
            SpeedBoostController speedBoostController, TeleportController teleportController,
            KnockbackImmunityController knockbackImmunityController, BodyRotationLockController bodyRotationLockController,
            CameraController cameraController, CrashWorkaroundController crashWorkaroundController,
            SystemStatsController systemStatsController, PromoPopupController promoPopupController,
            PerformanceController performanceController, WingsHiderController wingsHiderController,
            MiniOverlay overlay, UpdateChecker updateChecker, string crashDumpFolder,
            Action onMenuClosed, Action onResetAllSettings)
        {
            _playerContext = playerContext;
            _keybinds = keybinds;
            _hotkeys = hotkeys;
            _flyController = flyController;
            _speedBoostController = speedBoostController;
            _teleportController = teleportController;
            _knockbackImmunityController = knockbackImmunityController;
            _bodyRotationLockController = bodyRotationLockController;
            _cameraController = cameraController;
            _crashWorkaroundController = crashWorkaroundController;
            _systemStatsController = systemStatsController;
            _promoPopupController = promoPopupController;
            _performanceController = performanceController;
            _wingsHiderController = wingsHiderController;
            _overlay = overlay;
            _updateChecker = updateChecker;
            _crashDumpFolder = crashDumpFolder;
            _onMenuClosed = onMenuClosed;
            _onResetAllSettings = onResetAllSettings;
        }

        public void SetOpen(bool open)
        {
            if (open == Open)
                return;
            Open = open;
            if (!open)
            {
                _searchQuery = "";
                _onMenuClosed?.Invoke();
            }
        }

        public void ToggleOpen() => SetOpen(!Open);

        // True while one of the menu's own text boxes has keyboard focus, so
        // feature hotkeys don't fire while the player types a search or a
        // waypoint name.
        public bool IsTyping => Open && GUIUtility.keyboardControl != 0;

        private void Later(Action action) => _deferred.Add(action);

        // --- window ----------------------------------------------------------

        public void Draw()
        {
            if (!Open)
            {
                IsMouseOverOpenMenu = false;
                return;
            }
            Styles.Rebuild(Theme);

            float width = Mathf.Min(S.S(1040), Screen.width - 20f);
            float height = Mathf.Min(S.S(660), Screen.height - 20f);
            if (!_windowPlaced)
            {
                _windowRect.x = S.S(40);
                _windowRect.y = S.S(40);
                _windowPlaced = true;
            }
            _windowRect.width = width;
            _windowRect.height = height;

            GUISkin skin = GUI.skin;
            GUIStyle previousBar = skin.verticalScrollbar, previousThumb = skin.verticalScrollbarThumb;
            skin.verticalScrollbar = S.VerticalScrollbar;
            skin.verticalScrollbarThumb = S.VerticalThumb;
            try
            {
                _windowRect = GUI.Window(831201, _windowRect, DrawWindow, GUIContent.none, S.Window);
            }
            finally
            {
                skin.verticalScrollbar = previousBar;
                skin.verticalScrollbarThumb = previousThumb;
            }

            _windowRect.x = Mathf.Clamp(_windowRect.x, S.S(200) - _windowRect.width, Screen.width - S.S(200));
            _windowRect.y = Mathf.Clamp(_windowRect.y, 0f, Screen.height - S.S(74));
            IsMouseOverOpenMenu = _windowRect.Contains(Event.current.mousePosition);

            if (_deferred.Count > 0)
            {
                var actions = _deferred.ToArray();
                _deferred.Clear();
                foreach (Action action in actions)
                    action();
            }
        }

        private float SidebarWidth => S.S(220);
        private float HeaderHeight => S.S(74);

        private void DrawWindow(int windowId)
        {
            HandleSearchResultClicks();

            float width = _windowRect.width;
            float height = _windowRect.height;
            float mainX = SidebarWidth;
            float mainWidth = width - SidebarWidth;

            GUILayout.BeginArea(new Rect(0, 0, SidebarWidth, height), S.Sidebar);
            DrawSidebar();
            GUILayout.EndArea();

            GUILayout.BeginArea(new Rect(mainX + S.S(24), S.S(20), mainWidth - S.S(48), HeaderHeight - S.S(20)));
            DrawHeader(mainX + S.S(24), S.S(20));
            GUILayout.EndArea();
            FillRect(new Rect(mainX, HeaderHeight, mainWidth, 1), Theme.Divider);

            float contentX = mainX + S.S(24);
            float contentWidth = mainWidth - S.S(48) - S.S(12); // room for the scrollbar
            GUILayout.BeginArea(new Rect(contentX, HeaderHeight + 1, mainWidth - S.S(30), height - HeaderHeight - 2));
            _scroll.TryGetValue(_section, out Vector2 scroll);
            scroll = GUILayout.BeginScrollView(scroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUIStyle.none);
            GUILayout.Space(S.S(18));
            DrawSection(contentWidth);
            GUILayout.Space(S.S(12));
            GUILayout.EndScrollView();
            _scroll[_section] = scroll;
            GUILayout.EndArea();

            FillRect(new Rect(SidebarWidth - 1, 0, 1, height), Theme.Divider);
            DrawSearchResults();
            Toasts.Draw(S, new Rect(0, 0, width, height));

            // Dragging by the title area of the header; search and close
            // sit to the right of it and keep their own clicks.
            GUI.DragWindow(new Rect(mainX, 0, mainWidth - S.S(320), HeaderHeight));
        }

        private void FillRect(Rect rect, Color color)
        {
            if (Event.current.type != EventType.Repaint)
                return;
            Color previous = GUI.color;
            GUI.color = color * previous;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
        }

        // --- sidebar ---------------------------------------------------------

        private void DrawSidebar()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Space(S.S(8));
            Rect logo = GUILayoutUtility.GetRect(S.S(34), S.S(34), GUILayout.Width(S.S(34)), GUILayout.Height(S.S(34)));
            if (Event.current.type == EventType.Repaint)
            {
                Color previous = GUI.color;
                GUI.color = Theme.Accent * previous;
                S.FillShape.Draw(logo, false, false, false, false);
                GUI.color = previous;
                Widgets.DrawIcon(new Rect(logo.x + S.S(8), logo.y + S.S(8), S.S(18), S.S(18)), Icons.Heart, Color.white);
            }
            GUILayout.Space(S.S(10));
            GUILayout.BeginVertical();
            GUILayout.Space(S.S(1));
            GUILayout.Label("LOVE MENU", S.Brand);
            GUILayout.Label("v" + Version + " · by NNKtv28", S.BrandMeta);
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            GUILayout.Space(S.S(18));

            NavItem(Section.Home, Icons.Home);
            NavItem(Section.Movement, Icons.Movement);
            NavItem(Section.Camera, Icons.Camera);
            NavItem(Section.Teleports, Icons.Teleport);
            NavItem(Section.Performance, Icons.Gauge);
            NavItem(Section.Crashes, Icons.Warning);
            NavItem(Section.Settings, Icons.Settings);
            NavItem(Section.Credits, Icons.Info);

            GUILayout.FlexibleSpace();
            Widgets.Divider(S);
            GUILayout.Space(S.S(12));
            GUILayout.BeginHorizontal();
            GUILayout.Space(S.S(8));
            string name = _playerContext.PlayerName.Length > 0 ? _playerContext.PlayerName : "Loading...";
            DrawInitialBadge(name, S.S(30), S.BodyStrong);
            GUILayout.Space(S.S(10));
            GUILayout.BeginVertical();
            GUILayout.Label(name, S.BodyStrong);
            GUILayout.BeginHorizontal();
            GUILayout.Label(FeatureHotkeys.KeyName(_keybinds.MenuKey), MiniKeycap());
            GUILayout.Space(S.S(5));
            GUILayout.Label("to close", S.Small);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }

        private GUIStyle _miniKeycap;
        private float _miniKeycapScale;

        private GUIStyle MiniKeycap()
        {
            if (_miniKeycap == null || !Mathf.Approximately(_miniKeycapScale, S.Scale))
            {
                _miniKeycap = new GUIStyle(S.KeycapEmpty) { fixedHeight = S.S(18), fontSize = Mathf.RoundToInt(S.S(11)), fontStyle = FontStyle.Bold };
                _miniKeycap.normal.textColor = Theme.Text;
                _miniKeycap.padding = new RectOffset(Mathf.RoundToInt(S.S(6)), Mathf.RoundToInt(S.S(6)), 0, 0);
                _miniKeycapScale = S.Scale;
            }
            return _miniKeycap;
        }

        private void DrawInitialBadge(string name, float size, GUIStyle textStyle)
        {
            Rect rect = GUILayoutUtility.GetRect(size, size, GUILayout.Width(size), GUILayout.Height(size));
            if (Event.current.type != EventType.Repaint)
                return;
            Color previous = GUI.color;
            GUI.color = Theme.Selected * previous;
            GUI.DrawTexture(rect, S.Circle);
            GUI.color = previous;
            var centered = new GUIStyle(textStyle) { alignment = TextAnchor.MiddleCenter, normal = { textColor = Theme.AccentTextSoft } };
            GUI.Label(rect, name.Length > 0 ? name.Substring(0, 1).ToUpperInvariant() : "?", centered);
        }

        private void NavItem(Section section, Texture2D icon)
        {
            bool active = _section == section;
            if (GUILayout.Button(TitleOf(section), active ? S.NavItemActive : S.NavItem, GUILayout.ExpandWidth(true)))
                Later(() => OpenSection(section));
            Rect row = GUILayoutUtility.GetLastRect();
            float iconSize = S.S(18);
            Widgets.DrawIcon(new Rect(row.x + S.S(14), row.center.y - iconSize / 2f, iconSize, iconSize), icon,
                active ? Theme.TextStrong : Theme.TextSecondary);
            GUILayout.Space(S.S(2));
        }

        private void OpenSection(Section section)
        {
            _section = section;
            _searchQuery = "";
            // Friends come and go; show who is here now rather than a stale list.
            if (section == Section.Teleports)
                _teleportController.RefreshNearbyFriends();
        }

        private static string TitleOf(Section section)
        {
            foreach (var page in Pages)
                if (page.Section == section)
                    return page.Title;
            return "";
        }

        private static string SubtitleOf(Section section)
        {
            foreach (var page in Pages)
                if (page.Section == section)
                    return page.Subtitle;
            return "";
        }

        // --- header and search -----------------------------------------------

        private void DrawHeader(float areaX, float areaY)
        {
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical();
            GUILayout.Label(TitleOf(_section), S.Title);
            GUILayout.Space(S.S(4));
            GUILayout.Label(SubtitleOf(_section), S.Subtitle);
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();

            GUILayout.BeginVertical();
            GUILayout.Space(S.S(1));
            GUI.SetNextControlName(SearchControlName);
            string query = GUILayout.TextField(_searchQuery, S.SearchField, GUILayout.Width(S.S(240)));
            Rect field = GUILayoutUtility.GetLastRect();
            if (Event.current.type == EventType.Repaint)
                _searchFieldRect = new Rect(field.x + areaX, field.y + areaY, field.width, field.height);
            if (query.Length == 0 && GUI.GetNameOfFocusedControl() != SearchControlName && Event.current.type == EventType.Repaint)
            {
                var hint = new GUIStyle(S.SearchField) { normal = { background = null, textColor = Hex(0x8f89a1) } };
                GUI.Label(field, "Search settings", hint);
            }
            Widgets.DrawIcon(new Rect(field.x + S.S(11), field.center.y - S.S(7.5f), S.S(15), S.S(15)), Icons.Search, Hex(0x8f89a1));
            _searchQuery = query;
            GUILayout.EndVertical();

            GUILayout.Space(S.S(12));
            GUILayout.BeginVertical();
            GUILayout.Space(S.S(2));
            if (GUILayout.Button(GUIContent.none, S.IconButton))
                Later(() => SetOpen(false));
            Rect close = GUILayoutUtility.GetLastRect();
            Widgets.DrawIcon(new Rect(close.center.x - S.S(8), close.center.y - S.S(8), S.S(16), S.S(16)), Icons.Close, Theme.TextSoft);
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }

        private List<(string Label, Section Section)> SearchResults()
        {
            var results = new List<(string, Section)>();
            string query = _searchQuery.Trim();
            if (query.Length == 0)
                return results;
            foreach (var entry in SearchIndex)
            {
                if (entry.Label.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    results.Add(entry);
                if (results.Count == 6)
                    break;
            }
            return results;
        }

        // IMGUI hands a click to whichever control was drawn first, and the
        // result list is drawn last so it sits on top. So clicks on it are
        // taken here, before anything else in the window sees them.
        private void HandleSearchResultClicks()
        {
            Event e = Event.current;
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape && _searchQuery.Length > 0)
            {
                _searchQuery = "";
                GUIUtility.keyboardControl = 0;
                e.Use();
                return;
            }
            if (e.type != EventType.MouseDown || _searchQuery.Trim().Length == 0)
                return;

            foreach (var result in _searchResultRects)
            {
                if (!result.Rect.Contains(e.mousePosition))
                    continue;
                Section target = result.Section;
                Later(() => { OpenSection(target); GUIUtility.keyboardControl = 0; });
                e.Use();
                return;
            }
            if (!_searchFieldRect.Contains(e.mousePosition))
                Later(() => { _searchQuery = ""; GUIUtility.keyboardControl = 0; });
        }

        private void DrawSearchResults()
        {
            if (Event.current.type == EventType.Layout)
                return;
            _searchResultRects.Clear();
            if (_searchQuery.Trim().Length == 0)
                return;

            List<(string Label, Section Section)> results = SearchResults();
            float rowHeight = S.S(34);
            float padding = S.S(4);
            float height = results.Count == 0 ? S.S(40) : results.Count * rowHeight + padding * 2f;
            var box = new Rect(_searchFieldRect.x, _searchFieldRect.yMax + S.S(6), _searchFieldRect.width, height);
            GUI.Box(box, GUIContent.none, S.Dropdown);

            if (results.Count == 0)
            {
                GUI.Label(new Rect(box.x + S.S(12), box.y, box.width - S.S(24), box.height), "No setting matches", new GUIStyle(S.Description) { alignment = TextAnchor.MiddleLeft, wordWrap = false });
                return;
            }

            var whereStyle = new GUIStyle(S.Small) { alignment = TextAnchor.MiddleRight };
            for (int i = 0; i < results.Count; i++)
            {
                var row = new Rect(box.x + padding, box.y + padding + i * rowHeight, box.width - padding * 2f, rowHeight);
                bool hover = row.Contains(Event.current.mousePosition);
                if (Event.current.type == EventType.Repaint)
                    S.DropdownRow.Draw(row, new GUIContent(results[i].Label), hover, false, false, false);
                GUI.Label(new Rect(row.x, row.y, row.width - S.S(10), row.height), TitleOf(results[i].Section), whereStyle);
                _searchResultRects.Add((row, results[i].Section));
            }
        }

        // --- layout helpers --------------------------------------------------

        private float Gap => S.S(12);

        private void Columns(float width, Action<float> left, Action<float> right)
        {
            float column = (width - Gap) / 2f;
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width(column));
            left(column);
            GUILayout.EndVertical();
            GUILayout.Space(Gap);
            GUILayout.BeginVertical(GUILayout.Width(column));
            right(column);
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }

        private void Card(float width, Action<float> body, GUIStyle style = null)
        {
            GUIStyle cardStyle = style ?? S.Card;
            GUILayout.BeginVertical(cardStyle, GUILayout.Width(width));
            body(width - cardStyle.padding.horizontal);
            GUILayout.EndVertical();
            GUILayout.Space(Gap);
        }

        private void SectionLabel(string text)
        {
            GUILayout.Label(text.ToUpperInvariant(), S.SectionLabel);
            GUILayout.Space(S.S(10));
        }

        private static Color Hex(int rgb) => Theme.Hex(rgb);

        // Vertically centres something itemHeight tall next to a block
        // blockHeight tall. GUILayout.FlexibleSpace can't do this: inside a
        // vertical group it grows to fill all available height, which
        // stretched every card to the height of the page.
        private void BeginCentered(float blockHeight, float itemHeight)
        {
            GUILayout.BeginVertical();
            GUILayout.Space(Mathf.Max(0f, (blockHeight - itemHeight) / 2f));
        }

        private static void EndCentered() => GUILayout.EndVertical();

        private static float LineHeight(GUIStyle style) => style.CalcHeight(new GUIContent("Ag"), 1000f);

        private static float TextHeight(GUIStyle style, string text, float width) => style.CalcHeight(new GUIContent(text), width);

        private void CenteredLabel(string text, GUIStyle style, float rowHeight, params GUILayoutOption[] options)
        {
            BeginCentered(rowHeight, LineHeight(style));
            GUILayout.Label(text, style, options);
            EndCentered();
        }

        // A card row: title (and optional badge) over a wrapped description,
        // an optional hotkey button, and the switch on the right.
        private void ToggleRow(float inner, string id, string title, string description, bool on, Action toggle,
            string hotkeyId = null, GUIStyle badge = null, string badgeText = null, GUIStyle titleStyle = null)
        {
            float switchWidth = S.S(44);
            float hotkeyWidth = hotkeyId != null ? HotkeyButtonWidth(hotkeyId) + S.S(10) : 0f;
            float textWidth = Mathf.Max(S.S(80), inner - switchWidth - S.S(12) - hotkeyWidth);

            GUIStyle heading = titleStyle ?? S.CardTitle;
            float blockHeight = LineHeight(heading);
            if (badge != null)
                blockHeight = Mathf.Max(blockHeight, LineHeight(badge));
            if (!string.IsNullOrEmpty(description))
                blockHeight += S.S(3) + TextHeight(S.Description, description, textWidth);
            float switchHeight = S.S(24);
            float rowHeight = Mathf.Max(blockHeight, switchHeight);

            GUILayout.BeginHorizontal();
            BeginCentered(rowHeight, blockHeight);
            GUILayout.BeginVertical(GUILayout.Width(textWidth));
            if (badge != null)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(title, heading);
                GUILayout.Space(S.S(8));
                GUILayout.Label(badgeText, badge);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }
            else
            {
                GUILayout.Label(title, heading);
            }
            if (!string.IsNullOrEmpty(description))
            {
                GUILayout.Space(S.S(3));
                GUILayout.Label(description, S.Description, GUILayout.Width(textWidth));
            }
            GUILayout.EndVertical();
            EndCentered();
            GUILayout.FlexibleSpace();

            if (hotkeyId != null)
            {
                BeginCentered(rowHeight, HotkeyStyle(hotkeyId).fixedHeight);
                HotkeyButton(hotkeyId);
                EndCentered();
                GUILayout.Space(S.S(10));
            }

            BeginCentered(rowHeight, switchHeight);
            if (Widgets.Switch(S, Theme.Accent, id, on))
                Later(toggle);
            EndCentered();
            GUILayout.EndHorizontal();
        }

        private string HotkeyLabel(string hotkeyId)
        {
            if (_hotkeys.ListeningFor == hotkeyId)
                return "Press a key...";
            FeatureHotkeys.Binding binding = _hotkeys.Find(hotkeyId);
            return binding == null || binding.Key == KeyCode.None ? "+ Key" : FeatureHotkeys.KeyName(binding.Key);
        }

        private GUIStyle HotkeyStyle(string hotkeyId)
        {
            if (_hotkeys.ListeningFor == hotkeyId)
                return S.KeycapListening;
            FeatureHotkeys.Binding binding = _hotkeys.Find(hotkeyId);
            return binding == null || binding.Key == KeyCode.None ? S.KeycapEmpty : S.Keycap;
        }

        private float HotkeyButtonWidth(string hotkeyId) => HotkeyStyle(hotkeyId).CalcSize(new GUIContent(HotkeyLabel(hotkeyId))).x;

        private void HotkeyButton(string hotkeyId)
        {
            FeatureHotkeys.Binding binding = _hotkeys.Find(hotkeyId);
            string tooltip = binding == null ? "" : "Hotkey for " + binding.Label + " (Backspace clears)";
            if (GUILayout.Button(new GUIContent(HotkeyLabel(hotkeyId), tooltip), HotkeyStyle(hotkeyId), GUILayout.Width(HotkeyButtonWidth(hotkeyId))))
                Later(() => _hotkeys.BeginListening(hotkeyId));
        }

        private float SliderRow(string label, string valueText, float value, float min, float max, float step = 0f)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, new GUIStyle(S.Body) { normal = { textColor = Theme.TextSoft }, fontSize = S.Description.fontSize });
            GUILayout.FlexibleSpace();
            GUILayout.Label(valueText, new GUIStyle(S.BodyStrong) { fontSize = S.Description.fontSize });
            GUILayout.EndHorizontal();
            GUILayout.Space(S.S(2));
            return Widgets.Slider(S, Theme.Accent, value, min, max, step);
        }

        private bool Button(string text, GUIStyle style, params GUILayoutOption[] options) =>
            GUILayout.Button(text, style, options);

        private void Segmented(string[] labels, int selected, Action<int> pick, float width)
        {
            GUILayout.BeginHorizontal(S.SegmentContainer, GUILayout.Width(width));
            float each = (width - S.SegmentContainer.padding.horizontal - S.S(4) * (labels.Length - 1)) / labels.Length;
            for (int i = 0; i < labels.Length; i++)
            {
                int index = i;
                if (GUILayout.Button(labels[i], i == selected ? S.SegmentOn : S.SegmentOff, GUILayout.Width(each)))
                    Later(() => pick(index));
                if (i < labels.Length - 1)
                    GUILayout.Space(S.S(4));
            }
            GUILayout.EndHorizontal();
        }

        private void ListRows<T>(IList<T> items, Action<T, int> drawRow)
        {
            for (int i = 0; i < items.Count; i++)
            {
                GUILayout.BeginHorizontal(S.ListRow);
                drawRow(items[i], i);
                GUILayout.EndHorizontal();
                if (i < items.Count - 1)
                    Widgets.Divider(S);
            }
        }

        private void EmptyState(string message)
        {
            GUILayout.Space(S.S(6));
            GUILayout.Label(message, S.Description);
            GUILayout.Space(S.S(6));
        }

        private void Toggle(string name, bool nowOn) => Toasts.Show(name + (nowOn ? " on" : " off"));

        // --- sections --------------------------------------------------------

        private void DrawSection(float width)
        {
            switch (_section)
            {
                case Section.Home: DrawHome(width); break;
                case Section.Movement: DrawMovement(width); break;
                case Section.Camera: DrawCamera(width); break;
                case Section.Teleports: DrawTeleports(width); break;
                case Section.Performance: DrawPerformance(width); break;
                case Section.Crashes: DrawCrashes(width); break;
                case Section.Settings: DrawSettings(width); break;
                case Section.Credits: DrawCredits(width); break;
            }
        }

        // Home ------------------------------------------------------------------

        private void DrawHome(float width)
        {
            if (_updateChecker.UpdateAvailable && _updateChecker.LatestVersion != DismissedUpdateVersion)
                DrawUpdateBanner(width);

            SectionLabel("Quick toggles");
            float third = (width - Gap * 2f) / 3f;
            GUILayout.BeginHorizontal();
            QuickToggle(third, "home-fly", "Fly", _flyController.Flying, "On", () => { _flyController.Toggle(); Toggle("Fly", _flyController.Flying); });
            GUILayout.Space(Gap);
            QuickToggle(third, "home-speed", "Movement speed", _speedBoostController.Enabled, _speedBoostController.Multiplier.ToString("0.0") + "x",
                () => { _speedBoostController.SetEnabled(!_speedBoostController.Enabled); Toggle("Movement speed", _speedBoostController.Enabled); });
            GUILayout.Space(Gap);
            QuickToggle(third, "home-knock", "Knockback immunity", _knockbackImmunityController.Enabled, "On · experimental",
                () => { _knockbackImmunityController.Enabled = !_knockbackImmunityController.Enabled; Toggle("Knockback immunity", _knockbackImmunityController.Enabled); });
            GUILayout.EndHorizontal();
            GUILayout.Space(S.S(18));

            SectionLabel("System");
            Columns(width,
                column => GraphCard(column, "Frames per second", _systemStatsController.FramesPerSecond.ToString("0"), "", _systemStatsController.FpsHistory, Theme.AccentText),
                column => GraphCard(column, "RAM", SystemStatsController.BytesToMB(_systemStatsController.WorkingSetBytes()).ToString("N0"), " MB", _systemStatsController.RamHistoryMB, Theme.RamLine));

            float quarter = (width - Gap * 3f) / 4f;
            GUILayout.BeginHorizontal();
            StatCard(quarter, "Frame time", _systemStatsController.FrameTimeMs.ToString("0.0") + " ms");
            GUILayout.Space(Gap);
            StatCard(quarter, "VRAM", _systemStatsController.HasVramReading
                ? SystemStatsController.BytesToMB(_systemStatsController.GraphicsMemoryBytes()).ToString("N0") + " MB" : "n/a");
            GUILayout.Space(Gap);
            StatCard(quarter, "Managed heap", SystemStatsController.BytesToMB(_systemStatsController.ManagedMemoryBytes()).ToString("N0") + " MB");
            GUILayout.Space(Gap);
            StatCard(quarter, "Game uptime", _systemStatsController.GameUptime());
            GUILayout.EndHorizontal();
        }

        private void DrawUpdateBanner(float width)
        {
            string latest = _updateChecker.LatestVersion;
            float bannerTextHeight = LineHeight(S.BodyStrong) + S.S(2) + LineHeight(S.Description);
            float bannerHeight = Mathf.Max(bannerTextHeight, S.Primary.fixedHeight);
            GUILayout.BeginHorizontal(S.Banner, GUILayout.Width(width));
            BeginCentered(bannerHeight, S.S(20));
            Widgets.Icon(Icons.Download, S.S(20), Theme.AccentText);
            EndCentered();
            GUILayout.Space(S.S(14));
            BeginCentered(bannerHeight, bannerTextHeight);
            GUILayout.Label("Love Menu " + latest + " is available", S.BodyStrong);
            GUILayout.Space(S.S(2));
            GUILayout.Label("You have " + Version + ". Download the new exe and run it to update.", new GUIStyle(S.Description) { wordWrap = false });
            EndCentered();
            GUILayout.FlexibleSpace();
            BeginCentered(bannerHeight, S.Primary.fixedHeight);
            GUILayout.BeginHorizontal();
            if (Button("Download", S.Primary))
                Application.OpenURL(UpdateChecker.ReleasesPage);
            GUILayout.Space(S.S(6));
            if (GUILayout.Button(GUIContent.none, S.IconButton))
                Later(() => DismissedUpdateVersion = latest);
            Rect close = GUILayoutUtility.GetLastRect();
            Widgets.DrawIcon(new Rect(close.center.x - S.S(7), close.center.y - S.S(7), S.S(14), S.S(14)), Icons.Close, Theme.TextSecondary);
            GUILayout.EndHorizontal();
            EndCentered();
            GUILayout.EndHorizontal();
            GUILayout.Space(S.S(18));
        }

        private void QuickToggle(float width, string id, string title, bool on, string onText, Action toggle)
        {
            float blockHeight = LineHeight(S.CardTitle) + S.S(3) + LineHeight(S.Small);
            GUILayout.BeginVertical(S.Card, GUILayout.Width(width));
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical();
            GUILayout.Label(title, S.CardTitle);
            GUILayout.Space(S.S(3));
            GUILayout.Label(on ? onText : "Off", new GUIStyle(S.Small) { normal = { textColor = on ? Theme.AccentText : Theme.TextSecondary } });
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            BeginCentered(blockHeight, S.S(24));
            if (Widgets.Switch(S, Theme.Accent, id, on))
                Later(toggle);
            EndCentered();
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }

        private void GraphCard(float width, string title, string value, string unit, List<float> samples, Color line)
        {
            Card(width, inner =>
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(title, new GUIStyle(S.Body) { normal = { textColor = Theme.TextSecondary } });
                GUILayout.FlexibleSpace();
                GUILayout.Label(value, S.StatValue);
                if (unit.Length > 0)
                    GUILayout.Label(unit, S.StatUnit);
                GUILayout.EndHorizontal();
                GUILayout.Space(S.S(8));
                Rect graph = GUILayoutUtility.GetRect(inner, S.S(70), GUILayout.ExpandWidth(true), GUILayout.Height(S.S(70)));
                (float min, float max) = AutoRange(samples);
                if (Event.current.type == EventType.Repaint)
                    GraphRenderer.DrawLineGraph(graph, samples, line, min, max);
            });
        }

        private void StatCard(float width, string title, string value)
        {
            GUILayout.BeginVertical(S.Card, GUILayout.Width(width));
            GUILayout.Label(title, S.Small);
            GUILayout.Space(S.S(4));
            GUILayout.Label(value, new GUIStyle(S.StatValueSmall) { wordWrap = false, clipping = TextClipping.Clip });
            GUILayout.EndVertical();
        }

        private static (float min, float max) AutoRange(List<float> samples)
        {
            if (samples.Count == 0)
                return (0f, 1f);
            float min = samples[0], max = samples[0];
            foreach (float sample in samples)
            {
                if (sample < min) min = sample;
                if (sample > max) max = sample;
            }
            float span = max - min;
            if (span < 0.0001f)
                return (min - 1f, max + 1f);
            return (min - span * 0.25f, max + span * 0.25f);
        }

        // Movement --------------------------------------------------------------

        private void DrawMovement(float width)
        {
            Columns(width, column =>
            {
                Card(column, inner =>
                {
                    ToggleRow(inner, "fly", "Fly", "Fly freely with WASD. Also gets you out of places you're stuck in.",
                        _flyController.Flying, () => { _flyController.Toggle(); Toggle("Fly", _flyController.Flying); }, hotkeyId: "fly");
                    GUILayout.Space(S.S(12));
                    _flyController.Speed = SliderRow("Fly speed", _flyController.Speed.ToString("0"), _flyController.Speed, 1f, 25f, 1f);
                    GUILayout.Space(S.S(12));
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(FeatureHotkeys.KeyName(_keybinds.FlyUpKey), MiniKeycap());
                    GUILayout.Space(S.S(6));
                    GUILayout.Label("up", S.Small);
                    GUILayout.Space(S.S(14));
                    GUILayout.Label(FeatureHotkeys.KeyName(_keybinds.FlyDownKey), MiniKeycap());
                    GUILayout.Space(S.S(6));
                    GUILayout.Label("down", S.Small);
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                    GUILayout.Space(S.S(12));
                    GUILayout.BeginHorizontal();
                    if (Button("Unstick me", S.Secondary))
                    {
                        _flyController.Unstick();
                        Toasts.Show("Popped up 3 m");
                    }
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                });
            }, column =>
            {
                Card(column, inner =>
                {
                    ToggleRow(inner, "speed", "Movement speed", "Multiplies your normal walk and run speed.",
                        _speedBoostController.Enabled, () => { _speedBoostController.SetEnabled(!_speedBoostController.Enabled); Toggle("Movement speed", _speedBoostController.Enabled); },
                        hotkeyId: "speed");
                    GUILayout.Space(S.S(12));
                    float multiplier = SliderRow("Multiplier", _speedBoostController.Multiplier.ToString("0.0") + "x", _speedBoostController.Multiplier, 1f, 4f, 0.1f);
                    if (!Mathf.Approximately(multiplier, _speedBoostController.Multiplier))
                    {
                        _speedBoostController.Multiplier = multiplier;
                        if (_speedBoostController.Enabled)
                            _speedBoostController.ApplyMultiplier();
                    }
                });
                Card(column, inner => ToggleRow(inner, "knock", "Knockback immunity",
                    "Holds your spot against snowballs and thrown balls while you're standing still.",
                    _knockbackImmunityController.Enabled,
                    () => { _knockbackImmunityController.Enabled = !_knockbackImmunityController.Enabled; Toggle("Knockback immunity", _knockbackImmunityController.Enabled); },
                    hotkeyId: "knockback", badge: S.BadgeWarning, badgeText: "EXPERIMENTAL"));
                Card(column, inner => ToggleRow(inner, "body", "Lock body rotation", "Right-click camera drag no longer turns your avatar.",
                    _bodyRotationLockController.Enabled,
                    () => { _bodyRotationLockController.SetEnabled(!_bodyRotationLockController.Enabled); Toggle("Lock body rotation", _bodyRotationLockController.Enabled); }));
            });
        }

        // Camera ----------------------------------------------------------------

        private void DrawCamera(float width)
        {
            Columns(width, column =>
            {
                Card(column, inner =>
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.BeginVertical(GUILayout.Width(inner - S.S(90)));
                    GUILayout.Label("Field of view", S.CardTitle);
                    GUILayout.Space(S.S(3));
                    GUILayout.Label("How wide the camera sees. The game default is 65.", S.Description, GUILayout.Width(inner - S.S(90)));
                    GUILayout.EndVertical();
                    GUILayout.FlexibleSpace();
                    if (Button("Reset", S.Secondary))
                    {
                        _cameraController.ResetFov();
                        Toasts.Show("Field of view reset to 65");
                    }
                    GUILayout.EndHorizontal();
                    GUILayout.Space(S.S(12));
                    _cameraController.Fov = SliderRow("FOV", _cameraController.Fov.ToString("0") + "°", _cameraController.Fov, 50f, 110f, 1f);
                });
                Card(column, inner =>
                {
                    GUILayout.Label("Zoom-out limit", S.CardTitle);
                    GUILayout.Space(S.S(3));
                    GUILayout.Label("How far the scroll wheel can pull the camera back. Saved by the game itself.", S.Description, GUILayout.Width(inner));
                    GUILayout.Space(S.S(12));
                    int distance = _cameraController.ZoomDistance;
                    float picked = SliderRow("Max distance", distance + " m", distance, CameraController.MinZoomDistance, CameraController.MaxZoomDistance, 1f);
                    if (Mathf.RoundToInt(picked) != distance)
                        _cameraController.ZoomDistance = Mathf.RoundToInt(picked);
                });
            }, column =>
            {
                Card(column, inner =>
                {
                    FeatureHotkeys.Binding binding = _hotkeys.Find("screenshot");
                    string exitHint = binding != null && binding.Key != KeyCode.None
                        ? " Press " + FeatureHotkeys.KeyName(binding.Key) + " or " + FeatureHotkeys.KeyName(_keybinds.MenuKey) + " to bring everything back."
                        : " Press " + FeatureHotkeys.KeyName(_keybinds.MenuKey) + " to bring everything back.";
                    ToggleRow(inner, "shot", "Screenshot mode", "Hides the HUD, chat, name tags and this menu." + exitHint,
                        _cameraController.ScreenshotMode, () => _cameraController.SetScreenshotMode(!_cameraController.ScreenshotMode), hotkeyId: "screenshot");
                    GUILayout.Space(S.S(12));
                    Widgets.Divider(S);
                    GUILayout.Space(S.S(12));
                    ToggleRow(inner, "shot-avatar", "Also hide my avatar", null, _cameraController.HideOwnAvatarInScreenshots,
                        () => _cameraController.HideOwnAvatarInScreenshots = !_cameraController.HideOwnAvatarInScreenshots, titleStyle: S.BodyStrong);
                });
            });
        }

        // Teleports -------------------------------------------------------------

        private void DrawTeleports(float width)
        {
            Columns(width, column => Card(column, inner =>
            {
                GUILayout.Label("Find objects", S.CardTitle);
                GUILayout.Space(S.S(3));
                GUILayout.Label("Search this room by name and jump to what you find.", S.Description, GUILayout.Width(inner));
                GUILayout.Space(S.S(12));

                GUILayout.BeginHorizontal();
                _teleportController.CollectibleSearchTerm = GUILayout.TextField(_teleportController.CollectibleSearchTerm, S.TextField, GUILayout.Width(inner - S.S(80)));
                GUILayout.Space(S.S(8));
                if (Button("Find", S.Primary, GUILayout.Width(S.S(72))))
                    Later(_teleportController.RefreshNearbyCollectibles);
                GUILayout.EndHorizontal();
                GUILayout.Space(S.S(10));

                GUILayout.BeginHorizontal();
                foreach (string term in QuickSearchTerms)
                {
                    bool current = string.Equals(_teleportController.CollectibleSearchTerm, term, StringComparison.OrdinalIgnoreCase);
                    string picked = term;
                    if (Button(term, current ? S.ChipActive : S.Chip))
                        Later(() => { _teleportController.CollectibleSearchTerm = picked; _teleportController.RefreshNearbyCollectibles(); });
                    GUILayout.Space(S.S(6));
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Space(S.S(10));
                ToggleRow(inner, "strict", "Exact names only", "Off also matches names that only contain the word.",
                    _teleportController.StrictNames,
                    () => { _teleportController.StrictNames = !_teleportController.StrictNames; _teleportController.RefreshNearbyCollectibles(); },
                    titleStyle: S.BodyStrong);
                GUILayout.Space(S.S(12));

                var found = _teleportController.NearbyCollectibles;
                if (found.Count == 0)
                {
                    EmptyState(string.IsNullOrEmpty(_teleportController.CollectibleSearchStatus)
                        ? "Type what to look for, then Find." : _teleportController.CollectibleSearchStatus);
                }
                else
                {
                    float listHeight = Mathf.Min(found.Count * S.S(46), S.S(280));
                    _collectiblesScroll = GUILayout.BeginScrollView(_collectiblesScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUIStyle.none, GUILayout.Height(listHeight));
                    ListRows(found, (item, index) =>
                    {
                        CenteredLabel(item.Name, new GUIStyle(S.Body) { wordWrap = false, clipping = TextClipping.Clip }, S.Go.fixedHeight, GUILayout.Width(inner - S.S(150)));
                        GUILayout.FlexibleSpace();
                        CenteredLabel(_teleportController.DistanceFromPlayer(item.Position).ToString("0") + " m", S.Small, S.Go.fixedHeight);
                        GUILayout.Space(S.S(10));
                        Vector3 target = item.Position;
                        if (Button("Go", S.Go))
                            _teleportController.GoToPosition(target);
                    });
                    GUILayout.EndScrollView();
                    GUILayout.Space(S.S(6));
                    GUILayout.Label(_teleportController.CollectibleSearchStatus, S.Small);
                }
            }), column =>
            {
                Card(column, inner =>
                {
                    GUILayout.Label("Waypoints", S.CardTitle);
                    GUILayout.Space(S.S(3));
                    GUILayout.Label("Save spots and come back to them.", S.Description, GUILayout.Width(inner));
                    GUILayout.Space(S.S(12));
                    GUILayout.BeginHorizontal();
                    _newWaypointName = GUILayout.TextField(_newWaypointName, S.TextField, GUILayout.Width(inner - S.S(122)));
                    GUILayout.Space(S.S(8));
                    if (Button("+ Save here", S.Secondary, GUILayout.Width(S.S(114))))
                    {
                        string name = _newWaypointName;
                        Later(() =>
                        {
                            _teleportController.SaveWaypoint(name);
                            _newWaypointName = "";
                            Toasts.Show("Waypoint saved");
                        });
                    }
                    GUILayout.EndHorizontal();
                    GUILayout.Space(S.S(12));

                    var waypoints = _teleportController.Waypoints;
                    if (waypoints.Count == 0)
                    {
                        EmptyState("No waypoints yet. Type a name and save where you stand.");
                    }
                    else
                    {
                        float listHeight = Mathf.Min(waypoints.Count * S.S(46), S.S(220));
                        _waypointsScroll = GUILayout.BeginScrollView(_waypointsScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUIStyle.none, GUILayout.Height(listHeight));
                        ListRows(waypoints, (waypoint, index) =>
                        {
                            CenteredLabel(waypoint.Name, new GUIStyle(S.Body) { wordWrap = false, clipping = TextClipping.Clip }, S.Go.fixedHeight, GUILayout.Width(inner - S.S(120)));
                            GUILayout.FlexibleSpace();
                            Vector3 target = waypoint.Position;
                            if (Button("Go", S.Go))
                                _teleportController.GoToPosition(target);
                            GUILayout.Space(S.S(6));
                            int removeIndex = index;
                            if (Button("×", new GUIStyle(S.Go) { normal = { background = null, textColor = Theme.TextSecondary } }, GUILayout.Width(S.S(28))))
                                Later(() => _teleportController.Waypoints.RemoveAt(removeIndex));
                        });
                        GUILayout.EndScrollView();
                    }
                });
                Card(column, inner =>
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.BeginVertical(GUILayout.Width(inner - S.S(100)));
                    GUILayout.Label("Teleport to a friend", S.CardTitle);
                    GUILayout.Space(S.S(3));
                    GUILayout.Label("Friends currently in this room.", S.Description, GUILayout.Width(inner - S.S(100)));
                    GUILayout.EndVertical();
                    GUILayout.FlexibleSpace();
                    if (Button("Refresh", S.Secondary))
                        Later(_teleportController.RefreshNearbyFriends);
                    GUILayout.EndHorizontal();
                    GUILayout.Space(S.S(12));
                    var friends = _teleportController.NearbyFriends;
                    if (friends.Count == 0)
                    {
                        EmptyState(string.IsNullOrEmpty(_teleportController.FriendSearchStatus)
                            ? "Press Refresh to look for friends here." : _teleportController.FriendSearchStatus);
                        return;
                    }
                    ListRows(friends, (friend, index) =>
                    {
                        BeginCentered(S.S(28), S.S(8));
                        Widgets.Dot(S, S.S(8), Theme.Online);
                        EndCentered();
                        GUILayout.Space(S.S(10));
                        CenteredLabel(friend.Name, S.Body, S.S(28));
                        GUILayout.FlexibleSpace();
                        Vector3 target = friend.Position;
                        if (Button("Teleport", new GUIStyle(S.Primary) { fixedHeight = S.S(28), fontSize = Mathf.RoundToInt(S.S(12.5f)) }))
                            _teleportController.GoToFriend(target);
                    });
                });
            });
        }

        // Performance -----------------------------------------------------------

        private static readonly string[] TextureQualityNames = { "Full", "Half", "Quarter", "Eighth" };

        private void DrawPerformance(float width)
        {
            Columns(width, column => Card(column, inner =>
            {
                GUILayout.Label("Memory", S.CardTitle);
                GUILayout.Space(S.S(3));
                GUILayout.Label("Busy rooms keep assets of players who already left. This frees them without changing zone.", S.Description, GUILayout.Width(inner));
                GUILayout.Space(S.S(12));

                float ramMB = SystemStatsController.BytesToMB(_systemStatsController.WorkingSetBytes());
                float systemMB = Mathf.Max(1f, SystemInfo.systemMemorySize);
                GUILayout.BeginHorizontal();
                GUILayout.Label("Game RAM", new GUIStyle(S.Small) { normal = { textColor = Theme.TextSoft } });
                GUILayout.FlexibleSpace();
                GUILayout.Label(ramMB.ToString("N0") + " MB of " + (systemMB / 1024f).ToString("0") + " GB", new GUIStyle(S.BodyStrong) { fontSize = S.Small.fontSize });
                GUILayout.EndHorizontal();
                GUILayout.Space(S.S(6));
                Widgets.Bar(S, ramMB / systemMB, Theme.RamLine);
                GUILayout.Space(S.S(12));

                GUILayout.BeginHorizontal();
                if (Button("Free memory now", S.Primary))
                {
                    _performanceController.FreeUnusedMemory();
                    Toasts.Show(_performanceController.LastFreeResult);
                }
                GUILayout.Space(S.S(10));
                CenteredLabel("short freeze, about 0.3 s", S.Small, S.Primary.fixedHeight);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                GUILayout.Space(S.S(14));
                Widgets.Divider(S);
                GUILayout.Space(S.S(14));
                ToggleRow(inner, "autofree", "Auto-free when RAM is high", "At most once every 90 s.",
                    _performanceController.AutoFreeEnabled,
                    () => { _performanceController.AutoFreeEnabled = !_performanceController.AutoFreeEnabled; Toggle("Auto-free", _performanceController.AutoFreeEnabled); },
                    titleStyle: S.BodyStrong);
                GUILayout.Space(S.S(12));
                _performanceController.AutoFreeThresholdMB = SliderRow("Trigger above", _performanceController.AutoFreeThresholdMB.ToString("N0") + " MB",
                    _performanceController.AutoFreeThresholdMB, 2000f, 12000f, 500f);
            }), column =>
            {
                Card(column, inner =>
                {
                    ToggleRow(inner, "wings", "Hide player wings", "Cheapest win in a crowded room. Turning it off brings them back.",
                        _wingsHiderController.Enabled,
                        () => { _wingsHiderController.Enabled = !_wingsHiderController.Enabled; Toggle("Hide wings", _wingsHiderController.Enabled); },
                        hotkeyId: "wings");
                    if (_wingsHiderController.Enabled)
                    {
                        GUILayout.Space(S.S(8));
                        GUILayout.Label(_wingsHiderController.HiddenCount + " wing part(s) hidden right now", S.Small);
                    }
                });
                Card(column, inner =>
                {
                    GUILayout.Label("Graphics", S.CardTitle);
                    GUILayout.Space(S.S(3));
                    GUILayout.Label("Texture resolution is the biggest RAM and VRAM saving. Applies as textures reload.", S.Description, GUILayout.Width(inner));
                    GUILayout.Space(S.S(12));
                    int textureLimit = Mathf.Clamp(_performanceController.TextureQualityLimit, 0, TextureQualityNames.Length - 1);
                    Segmented(TextureQualityNames, textureLimit, index => _performanceController.TextureQualityLimit = index, inner);
                    GUILayout.Space(S.S(14));
                    _performanceController.ShadowDistance = SliderRow("Shadow distance", _performanceController.ShadowDistance.ToString("0"),
                        _performanceController.ShadowDistance, 0f, 200f, 5f);
                    GUILayout.Space(S.S(10));
                    _performanceController.LodBias = SliderRow("Model detail (LOD bias)", _performanceController.LodBias.ToString("0.00"),
                        _performanceController.LodBias, 0.3f, 2f, 0.05f);
                    GUILayout.Space(S.S(12));
                    GUILayout.BeginHorizontal();
                    if (Button("Reset to game defaults", S.Secondary))
                    {
                        _performanceController.ResetGraphicsToGameDefaults();
                        Toasts.Show("Graphics reset to game defaults");
                    }
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                });
            });
        }

        // Crashes ---------------------------------------------------------------

        private void DrawCrashes(float width)
        {
            Card(width, inner =>
            {
                const string watchdogText = "If the game stops responding for 10 s, a crash dump is saved so the cause can be found.";
                float textWidth = inner - S.S(210);
                float blockHeight = LineHeight(S.CardTitle) + S.S(3) + TextHeight(S.Description, watchdogText, textWidth);
                GUILayout.BeginHorizontal();
                BeginCentered(blockHeight, S.S(10));
                Widgets.Dot(S, S.S(10), Theme.Online);
                EndCentered();
                GUILayout.Space(S.S(14));
                GUILayout.BeginVertical(GUILayout.Width(textWidth));
                GUILayout.Label("Freeze watchdog is on", S.CardTitle);
                GUILayout.Space(S.S(3));
                GUILayout.Label(watchdogText, S.Description, GUILayout.Width(textWidth));
                GUILayout.EndVertical();
                GUILayout.FlexibleSpace();
                BeginCentered(blockHeight, S.Secondary.fixedHeight);
                if (Button("Open dumps folder", S.Secondary))
                    OpenCrashDumpFolder();
                EndCentered();
                GUILayout.EndHorizontal();
            });

            Columns(width, column => Card(column, inner =>
            {
                GUILayout.Label("Fixed crashes", S.CardTitle);
                GUILayout.Space(S.S(12));
                var fixes = _crashWorkaroundController.ConfirmedFixes;
                if (fixes.Count == 0)
                    EmptyState("No confirmed crash causes yet.");
                for (int i = 0; i < fixes.Count; i++)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("FIXED", S.BadgeSuccess);
                    GUILayout.Space(S.S(8));
                    GUILayout.Label(fixes[i].Name, S.BodyStrong);
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                    GUILayout.Space(S.S(4));
                    GUILayout.Label(fixes[i].Description, S.Description, GUILayout.Width(inner));
                    if (i < fixes.Count - 1)
                        GUILayout.Space(S.S(14));
                }
            }), column => Card(column, inner =>
            {
                GUILayout.Label("Experimental workarounds", S.CardTitle);
                GUILayout.Space(S.S(12));
                var workarounds = _crashWorkaroundController.Workarounds;
                if (workarounds.Count == 0)
                    EmptyState("None right now.");
                for (int i = 0; i < workarounds.Count; i++)
                {
                    CrashWorkaround workaround = workarounds[i];
                    ToggleRow(inner, "workaround-" + i, workaround.Name, workaround.Description + " Turn it off if anything acts up.",
                        workaround.Enabled, () => _crashWorkaroundController.SetEnabled(workaround, !workaround.Enabled), titleStyle: S.BodyStrong);
                    if (i < workarounds.Count - 1)
                        GUILayout.Space(S.S(14));
                }
            }));
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

        // Settings --------------------------------------------------------------

        private void DrawSettings(float width)
        {
            Columns(width, column => Card(column, inner =>
            {
                GUILayout.Label("Keys", S.CardTitle);
                GUILayout.Space(S.S(3));
                GUILayout.Label("Click a key, then press the new one. Esc cancels. Feature keys sit next to each switch.", S.Description, GUILayout.Width(inner));
                GUILayout.Space(S.S(10));
                KeyRow("Open menu", Keybinds.RebindTarget.Menu, _keybinds.MenuKey);
                KeyRow("Fly up", Keybinds.RebindTarget.Up, _keybinds.FlyUpKey);
                KeyRow("Fly down", Keybinds.RebindTarget.Down, _keybinds.FlyDownKey);
                GUILayout.Space(S.S(8));
                Widgets.Divider(S);
                GUILayout.Space(S.S(14));
                ToggleRow(inner, "promo", "Hide promo popups", "Closes sale popups for 45 s after you spawn. Shop and help still open.",
                    _promoPopupController.Enabled,
                    () => { _promoPopupController.Enabled = !_promoPopupController.Enabled; Toggle("Hide promo popups", _promoPopupController.Enabled); },
                    titleStyle: S.BodyStrong);
            }), column => Card(column, inner =>
            {
                GUILayout.Label("Appearance", S.CardTitle);
                GUILayout.Space(S.S(12));
                GUILayout.Label("Accent", new GUIStyle(S.Small) { normal = { textColor = Theme.TextSoft } });
                GUILayout.Space(S.S(6));
                GUILayout.BeginHorizontal();
                for (int i = 0; i < Theme.AccentNames.Length; i++)
                    AccentSwatch(i);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Space(S.S(12));
                Theme.Alpha = SliderRow("Background opacity", Mathf.RoundToInt(Theme.Alpha * 100f) + "%", Theme.Alpha, 0.4f, 1f, 0.01f);
                GUILayout.Space(S.S(14));
                ToggleRow(inner, "autoscale", "Auto size", "Scales the menu to your screen (1080p, 1440p, 4K).", Theme.AutoScale,
                    () => Theme.AutoScale = !Theme.AutoScale, titleStyle: S.BodyStrong);
                if (!Theme.AutoScale)
                {
                    GUILayout.Space(S.S(12));
                    float size = SliderRow("Menu size", Mathf.RoundToInt(Theme.UIScale * 100f) + "%", Theme.UIScale, 0.75f, 2.5f, 0.05f);
                    // Applied after the frame: resizing mid-draw would change every rect IMGUI just laid out.
                    if (!Mathf.Approximately(size, Theme.UIScale))
                        Later(() => Theme.UIScale = size);
                }
                GUILayout.Space(S.S(14));
                Widgets.Divider(S);
                GUILayout.Space(S.S(14));
                GUILayout.BeginHorizontal();
                bool confirming = Time.unscaledTime < _resetConfirmUntil;
                if (Button(confirming ? "Click again to reset everything" : "Reset all settings", S.Danger))
                {
                    if (confirming)
                    {
                        _resetConfirmUntil = 0f;
                        Later(() => { _onResetAllSettings?.Invoke(); Toasts.Show("All settings reset"); });
                    }
                    else
                    {
                        _resetConfirmUntil = Time.unscaledTime + 3f;
                    }
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }));

            Card(width, inner => DrawOverlaySettings(inner));
        }

        private void KeyRow(string label, Keybinds.RebindTarget target, KeyCode key)
        {
            GUILayout.BeginHorizontal();
            CenteredLabel(label, S.Body, S.Keycap.fixedHeight);
            GUILayout.FlexibleSpace();
            bool listening = _keybinds.Rebinding == target;
            if (Button(listening ? "Press a key..." : FeatureHotkeys.KeyName(key), listening ? S.KeycapListening : S.Keycap, GUILayout.MinWidth(S.S(72))))
                Later(() => _keybinds.BeginRebind(target));
            GUILayout.EndHorizontal();
            GUILayout.Space(S.S(4));
        }

        private void AccentSwatch(int index)
        {
            Rect rect = GUILayoutUtility.GetRect(S.S(30), S.S(30), GUILayout.Width(S.S(30)), GUILayout.Height(S.S(30)));
            if (GUI.Button(rect, GUIContent.none, GUIStyle.none))
                Later(() => Theme.Index = index);
            if (Event.current.type == EventType.Repaint)
            {
                Color previous = GUI.color;
                if (Theme.Index == index)
                {
                    GUI.color = Color.white * previous;
                    GUI.DrawTexture(rect, S.Circle);
                    float inset = S.S(2.5f);
                    GUI.color = Theme.AccentAt(index) * previous;
                    GUI.DrawTexture(new Rect(rect.x + inset, rect.y + inset, rect.width - inset * 2f, rect.height - inset * 2f), S.Circle);
                }
                else
                {
                    GUI.color = Theme.AccentAt(index) * previous;
                    GUI.DrawTexture(rect, S.Circle);
                }
                GUI.color = previous;
            }
            GUILayout.Space(S.S(8));
        }

        private static readonly string[] CornerNames = { "Top left", "Top right", "Bottom left", "Bottom right" };

        private void DrawOverlaySettings(float inner)
        {
            float previewWidth = S.S(220);
            float controlsWidth = inner - previewWidth - S.S(24);

            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width(controlsWidth));
            ToggleRow(controlsWidth, "overlay", "Mini overlay", "A small readout that stays on screen, even with the menu closed.",
                _overlay.Enabled, () => { _overlay.Enabled = !_overlay.Enabled; Toggle("Mini overlay", _overlay.Enabled); });
            GUILayout.Space(S.S(12));
            GUILayout.BeginHorizontal();
            OverlayChip("FPS", _overlay.ShowFps, () => _overlay.ShowFps = !_overlay.ShowFps);
            OverlayChip("RAM", _overlay.ShowRam, () => _overlay.ShowRam = !_overlay.ShowRam);
            OverlayChip("Active features", _overlay.ShowActiveFeatures, () => _overlay.ShowActiveFeatures = !_overlay.ShowActiveFeatures);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(S.S(12));
            Segmented(CornerNames, (int)_overlay.Position, index => _overlay.Position = (MiniOverlay.Corner)index, Mathf.Min(controlsWidth, S.S(460)));
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();

            Rect preview = GUILayoutUtility.GetRect(previewWidth, S.S(124), GUILayout.Width(previewWidth), GUILayout.Height(S.S(124)));
            if (Event.current.type == EventType.Repaint)
            {
                Color previous = GUI.color;
                GUI.color = Hex(0x2d3340) * previous;
                S.FillShape.Draw(preview, false, false, false, false);
                GUI.color = previous;
                GUI.Label(new Rect(preview.x + S.S(8), preview.y + S.S(4), preview.width, S.S(20)), "Game view", new GUIStyle(S.Small) { normal = { textColor = Hex(0xcfd4de) } });
                if (_overlay.Enabled)
                {
                    string text = _overlay.Text();
                    if (text.Length == 0)
                        text = "Nothing selected";
                    var content = new GUIContent(text);
                    var pill = new GUIStyle(S.OverlayPill) { fontSize = Mathf.RoundToInt(S.S(10.5f)) };
                    Vector2 size = pill.CalcSize(content);
                    size.x = Mathf.Min(size.x, preview.width - S.S(16));
                    float margin = S.S(8);
                    bool left = _overlay.Position == MiniOverlay.Corner.TopLeft || _overlay.Position == MiniOverlay.Corner.BottomLeft;
                    bool top = _overlay.Position == MiniOverlay.Corner.TopLeft || _overlay.Position == MiniOverlay.Corner.TopRight;
                    float x = left ? preview.x + margin : preview.xMax - size.x - margin;
                    float y = top ? preview.y + S.S(24) : preview.yMax - size.y - margin;
                    GUI.Label(new Rect(x, y, size.x, size.y), content, pill);
                }
            }
            GUILayout.EndHorizontal();
        }

        private void OverlayChip(string label, bool on, Action toggle)
        {
            if (Button(label, on ? S.ChipActive : S.Chip))
                Later(toggle);
            GUILayout.Space(S.S(6));
        }

        // Credits ---------------------------------------------------------------

        private void DrawCredits(float width)
        {
            Card(Mathf.Min(width, S.S(520)), inner =>
            {
                GUILayout.BeginHorizontal();
                DrawInitialBadge("NNKtv28", S.S(44), S.Title);
                GUILayout.Space(S.S(14));
                var nameStyle = new GUIStyle(S.CardTitle) { fontSize = Mathf.RoundToInt(S.S(15)) };
                BeginCentered(S.S(44), LineHeight(nameStyle) + S.S(2) + LineHeight(S.Small));
                GUILayout.Label("NNKtv28", nameStyle);
                GUILayout.Space(S.S(2));
                GUILayout.BeginHorizontal();
                GUILayout.Label("Author ·", S.Small);
                GUILayout.Space(S.S(4));
                if (GUILayout.Button("nikicoding.com", new GUIStyle(S.Small) { normal = { textColor = Theme.AccentText }, hover = { textColor = Theme.AccentTextSoft } }))
                    Application.OpenURL("https://nikicoding.com");
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                EndCentered();
                GUILayout.EndHorizontal();
                GUILayout.Space(S.S(14));

                GUILayout.BeginHorizontal();
                CreditsFact("Version", Version, inner / 2f);
                CreditsFact("Mod loader", "BepInEx 5 · HarmonyLib", inner / 2f);
                GUILayout.EndHorizontal();
                GUILayout.Space(S.S(14));

                GUILayout.BeginHorizontal();
                if (Button("GitHub", S.Secondary))
                    Application.OpenURL("https://github.com/NNKTV28/LoveMenu");
                GUILayout.Space(S.S(8));
                if (Button("Report a bug", S.Secondary))
                    Application.OpenURL("https://github.com/NNKTV28/LoveMenu/issues/new/choose");
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            });
        }

        private void CreditsFact(string label, string value, float width)
        {
            GUILayout.BeginVertical(GUILayout.Width(width));
            GUILayout.Label(label, S.Small);
            GUILayout.Space(S.S(2));
            GUILayout.Label(value, S.Body);
            GUILayout.EndVertical();
        }
    }
}
