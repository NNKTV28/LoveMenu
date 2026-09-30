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
        public const string Version = "1.7.1";

        // Saved by number, so new pages go at the end.
        private enum Section { Home, Movement, Camera, Teleports, Performance, Crashes, Settings, Credits, Rendering, Fortune, Glitches, People }

        private static readonly (Section Section, string Title, string Subtitle)[] Pages =
        {
            (Section.Home, "Home", "What is on right now, and how the game is running."),
            (Section.Movement, "Movement", "Fly, speed and staying on your feet."),
            (Section.Camera, "Camera", "Field of view, zoom and clean screenshots."),
            (Section.Teleports, "Teleports", "Find things in the room, save spots, reach friends."),
            (Section.People, "People", "Who is in the room, and the chat log with mention alerts."),
            (Section.Performance, "Performance", "Keep busy rooms smooth."),
            (Section.Rendering, "Rendering", "Cheaper drawing and smoother loading of people."),
            (Section.Crashes, "Crashes", "What the menu does to keep the game running."),
            (Section.Glitches, "Bug fixes", "Fixes for the game's own bugs, and tools to track them down."),
            (Section.Settings, "Settings", "Keys, look and size of the menu."),
            (Section.Credits, "Credits", "Who made this and where to get help."),
        };

        // What the header search can jump to.
        private static readonly (string Label, Section Section)[] SearchIndex =
        {
            ("Fly", Section.Movement), ("Fly speed", Section.Movement), ("Unstick me", Section.Movement),
            ("Movement speed", Section.Movement), ("Knockback immunity", Section.Movement), ("Lock body rotation", Section.Movement),
            ("Field of view (FOV)", Section.Camera), ("Zoom-out limit", Section.Camera), ("Screenshot mode", Section.Camera),
            ("Find objects (seashells, letters, flowers, keys)", Section.Teleports), ("List object names", Section.Teleports), ("Go to next item", Section.Teleports), ("Go to safe (Key Hunter)", Section.Teleports), ("NPCs / quest givers", Section.Teleports), ("Quest giver (Diego)", Section.Teleports), ("Quiz helper / quiz answers", Section.Teleports), ("Collected today", Section.Teleports), ("Waypoints", Section.Teleports), ("Teleport to a friend", Section.Teleports),
            ("Free memory", Section.Performance), ("Auto-free RAM", Section.Performance), ("Hide player wings", Section.Performance),
            ("Texture resolution", Section.Performance), ("Shadow distance", Section.Performance), ("LOD bias", Section.Performance),
            ("Performance profile (Quality, Balanced, Potato)", Section.Rendering), ("FPS benchmark", Section.Rendering),
            ("Friend arrival notices", Section.Teleports), ("Make crash report", Section.Crashes), ("Bug fixes / glitches", Section.Glitches), ("Lag spike recorder", Section.Glitches), ("People in this room", Section.People), ("Chat log / mention alerts", Section.People), ("Minimap", Section.Teleports), ("Back (undo teleport)", Section.Teleports), ("Name tags distance", Section.Rendering), ("Auto crowd mode", Section.Rendering),
            ("Show people (clan, friends)", Section.Rendering), ("Graphics API (DirectX 12, Vulkan)", Section.Rendering), ("Multithreaded rendering", Section.Rendering),
            ("Smooth loading", Section.Rendering),("Lamp shadows", Section.Rendering), ("Avatar draw distance", Section.Rendering),
            ("Mirrors", Section.Rendering), ("Reflections", Section.Rendering), ("Decals", Section.Rendering), ("Sky effects", Section.Rendering),
            ("Kiss of Fortune timer", Section.Home), ("Kiss of Fortune notice", Section.Settings),
            ("Crash dumps", Section.Crashes), ("DynamicBones settings fix (hair / cloth physics)", Section.Glitches), ("Load fixes (missing objects, lights)", Section.Glitches), ("Game errors log", Section.Crashes),
            ("Keys and hotkeys", Section.Settings), ("Hide promo popups", Section.Settings), ("Accent color", Section.Settings),
            ("Background opacity", Section.Settings), ("Menu size", Section.Settings), ("Mini overlay", Section.Settings),
            ("Reset all settings", Section.Settings), ("Check for updates", Section.Home), ("Version", Section.Credits),
        };

        // The pickups of the current collect quests. Seashell pickups are
        // server objects called "Shell_..."; the flower pickups' name isn't
        // confirmed yet, so that button tries the likely flower words at once
        // (use List names in the flower quest area to find the real one).
        private static readonly (string Label, string Terms)[] QuickSearches =
        {
            // Pickups are "Shell_01 9", "Shell_03 2", "Shell_02 Ref 1"; plain
            // "shell" also found the "Seashell Beach Signpost".
            ("seashells", "Shell_0"),
            ("letters", "letter"),
            // FX_GiftboxAura is the glow the game puts around pickups; found
            // next to the flowers. No "rose": Rose_Podium_Book matched it.
            ("flowers", "GiftboxAura|flower|blossom|bloom|petal|hibiscus|plumeria|orchid|lotus|lily|tulip|daisy|sunflower"),
            ("keys", "key"),
        };

        public bool Open { get; private set; }
        // Read by the game-input patches: while true, the game ignores the
        // mouse wheel and treats clicks as clicks on its own UI.
        public static bool IsMouseOverOpenMenu => _mouseOverWindow || WelcomeBlocksMouse || _mouseOverQuizPanel || Minimap.MouseOver;
        private static bool _mouseOverQuizPanel;
        public static void UpdateQuizPanelHover(bool over) => _mouseOverQuizPanel = over;
        private static bool _mouseOverWindow;
        public static bool WelcomeBlocksMouse;

        // Read by MenuTypingBlockPatch so the game stops reading movement
        // keys while a menu text box has focus.
        public static bool TypingInMenu { get; private set; }
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
        public Action OnShowWelcome;
        public CollectionLogger Collections;

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
                GUIUtility.keyboardControl = 0;
                TypingInMenu = false;
                _onMenuClosed?.Invoke();
            }
        }

        public void ToggleOpen() => SetOpen(!Open);

        // True while one of the menu's own text boxes has keyboard focus, so
        // feature hotkeys don't fire while the player types a search or a
        // waypoint name.
        public bool IsTyping => TypingInMenu;

        // Remembered between sessions.
        public int SectionIndex
        {
            get => (int)_section;
            // A saved page that no longer exists (Fortune moved to Home) opens Home.
            set => _section = Array.Exists(Pages, page => (int)page.Section == value) ? (Section)value : Section.Home;
        }

        public Vector2 WindowPosition
        {
            get => new Vector2(_windowRect.x, _windowRect.y);
            set
            {
                if (value.x < -9000f)
                    return; // never placed yet
                _windowRect.x = value.x;
                _windowRect.y = value.y;
                _windowPlaced = true;
            }
        }

        public bool WindowPlaced => _windowPlaced;

        private void Later(Action action) => _deferred.Add(action);

        // --- window ----------------------------------------------------------

        public void Draw()
        {
            if (!Open)
            {
                _mouseOverWindow = false;
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

            // Always leave part of the header on screen - the whole header is
            // the drag handle, so the window can always be pulled back.
            _windowRect.x = Mathf.Clamp(_windowRect.x, -(SidebarWidth - S.S(40)), Screen.width - S.S(300));
            _windowRect.y = Mathf.Clamp(_windowRect.y, 0f, Screen.height - HeaderHeight);
            _mouseOverWindow = _windowRect.Contains(Event.current.mousePosition);
            TypingInMenu = GUIUtility.keyboardControl != 0;

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

            // The whole header, logo included, drags the window. Search and
            // close were drawn first, so they still get their own clicks.
            GUI.DragWindow(new Rect(0, 0, width, HeaderHeight));
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
            NavItem(Section.People, Icons.People);
            NavItem(Section.Performance, Icons.Gauge);
            NavItem(Section.Rendering, Icons.Eye);
            NavItem(Section.Crashes, Icons.Warning);
            NavItem(Section.Glitches, Icons.Bug);
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
            {
                _teleportController.RefreshNearbyFriends();
                _teleportController.RefreshNearbyNpcs();
            }
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
                // Long labels are cut short so they never run under the page name.
                string where = TitleOf(results[i].Section);
                float whereWidth = whereStyle.CalcSize(new GUIContent(where)).x + S.S(16);
                if (Event.current.type == EventType.Repaint)
                {
                    S.DropdownRow.Draw(row, GUIContent.none, hover, false, false, false);
                    var labelStyle = new GUIStyle(S.DropdownRow) { wordWrap = false };
                    labelStyle.normal.background = labelStyle.hover.background = null;
                    labelStyle.Draw(new Rect(row.x, row.y, row.width - whereWidth, row.height),
                        new GUIContent(Ellipsize(results[i].Label, labelStyle, row.width - whereWidth - labelStyle.padding.horizontal)),
                        hover, false, false, false);
                }
                GUI.Label(new Rect(row.x, row.y, row.width - S.S(10), row.height), where, whereStyle);
                _searchResultRects.Add((row, results[i].Section));
            }
        }

        // "Find objects (seashells, letters, flowers, keys)" -> "Find objects (seashells, lett…"
        private static string Ellipsize(string text, GUIStyle style, float width)
        {
            if (style.CalcSize(new GUIContent(text)).x <= width)
                return text;
            int length = text.Length;
            while (length > 1 && style.CalcSize(new GUIContent(text.Substring(0, length).TrimEnd() + "…")).x > width)
                length--;
            return text.Substring(0, length).TrimEnd() + "…";
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
            // The badge sits beside the title when both fit, otherwise on its
            // own line under it - side by side they used to push the card
            // wider than its column.
            bool badgeBeside = badge != null &&
                heading.CalcSize(new GUIContent(title)).x + S.S(8) + badge.CalcSize(new GUIContent(badgeText)).x <= textWidth;
            float blockHeight = LineHeight(heading);
            if (badge != null)
                blockHeight = badgeBeside ? Mathf.Max(blockHeight, LineHeight(badge)) : blockHeight + S.S(4) + LineHeight(badge);
            if (!string.IsNullOrEmpty(description))
                blockHeight += S.S(3) + TextHeight(S.Description, description, textWidth);
            float switchHeight = S.S(24);
            float rowHeight = Mathf.Max(blockHeight, switchHeight);

            GUILayout.BeginHorizontal();
            BeginCentered(rowHeight, blockHeight);
            GUILayout.BeginVertical(GUILayout.Width(textWidth));
            if (badge != null && badgeBeside)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(title, heading);
                GUILayout.Space(S.S(8));
                GUILayout.Label(badgeText, badge);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }
            else if (badge != null)
            {
                GUILayout.Label(title, heading);
                GUILayout.Space(S.S(4));
                GUILayout.BeginHorizontal();
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
                case Section.Rendering: DrawRendering(width); break;
                case Section.Glitches: DrawGlitches(width); break;
                case Section.People: DrawPeople(width); break;
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
            GUILayout.Space(Gap);
            GUILayout.BeginHorizontal();
            QuickToggle(third, "home-quiz", "Quiz helper", QuizHelper.Enabled, QuizHelper.Active ? "On · quiz running" : "On", ToggleQuizHelper);
            GUILayout.Space(Gap);
            FortuneHomeCard(third * 2f + Gap);
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

        private void ToggleQuizHelper()
        {
            QuizHelper.Enabled = !QuizHelper.Enabled;
            if (!QuizHelper.Enabled)
                QuizHelper.Visible = false;
            Toggle("Quiz helper", QuizHelper.Enabled);
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

        // Home: the soonest free fortune-ball opening, same height as a quick toggle.
        private void FortuneHomeCard(float width)
        {
            FortuneTracker.Ball next = FortuneTracker.Instance.Next;
            float blockHeight = LineHeight(S.CardTitle) + S.S(3) + LineHeight(S.Small);
            GUILayout.BeginVertical(S.Card, GUILayout.Width(width));
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical();
            GUILayout.Label(next == null ? "Kiss of Fortune" : next.Name, S.CardTitle);
            GUILayout.Space(S.S(3));
            string status = next == null ? "Click a fortune ball once to start its timer"
                : next.FreeNow ? "Free opening ready" : "Free in " + FortuneTracker.FormatRemaining(next.Remaining);
            GUILayout.Label(status, new GUIStyle(S.Small) { normal = { textColor = next != null && next.FreeNow ? Theme.SuccessText : Theme.TextSecondary } });
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            // Go to the ball when it's in this room.
            if (next != null && FortuneTracker.Instance.InThisRoom(next))
            {
                BeginCentered(blockHeight, S.Secondary.fixedHeight);
                Vector3 ball = next.Position;
                if (Button("Go", S.Secondary))
                    Later(() => _teleportController.GoToNpc(ball));
                EndCentered();
            }
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

        private static readonly string[] CornerShortNames = { "Top left", "Top right", "Bottom left", "Bottom right" };

        // Minimap settings and the Back button, above the teleport cards.
        private void DrawMapCard(float inner)
        {
            Minimap map = Minimap.Instance;
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width(inner * 0.5f));
            GUILayout.Label("Back", S.CardTitle);
            GUILayout.Space(S.S(3));
            GUILayout.Label("Returns you to where your last teleport started in this room.", S.Description, GUILayout.Width(inner * 0.5f));
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            int steps = _teleportController.BackSteps;
            GUI.enabled = steps > 0;
            if (Button(steps > 0 ? "Back (" + steps + ")" : "Back", S.Primary))
                Later(() => Toasts.Show(_teleportController.GoBack()));
            GUI.enabled = true;
            GUILayout.Space(S.S(10));
            BeginCentered(S.Primary.fixedHeight, HotkeyStyle("back").fixedHeight);
            HotkeyButton("back");
            EndCentered();
            GUILayout.EndHorizontal();
            GUILayout.Space(S.S(14));
            Widgets.Divider(S);
            GUILayout.Space(S.S(14));

            ToggleRow(inner, "minimap", "Minimap",
                "A map of the room in a screen corner: green friends, purple clan, grey players, yellow NPCs. Hover a dot for the name, click the map to teleport there.",
                map.Enabled, () => { map.Enabled = !map.Enabled; Toggle("Minimap", map.Enabled); }, hotkeyId: "minimap");
            if (!map.Enabled)
                return;
            GUILayout.Space(S.S(12));
            GUILayout.Label("Corner", S.Small);
            GUILayout.Space(S.S(4));
            Segmented(CornerShortNames, (int)map.Position, index => map.Position = (Minimap.Corner)index, inner);
            GUILayout.Space(S.S(10));
            GUILayout.Label("Range", S.Small);
            GUILayout.Space(S.S(4));
            Segmented(Minimap.RangeNames, map.RangeIndex, index => map.RangeIndex = index, inner);
            GUILayout.Space(S.S(12));
            ToggleRow(inner, "map-picture", "Map picture", "A top-down view of the room behind the dots. Off saves a little FPS.",
                map.ShowPicture, () => map.ShowPicture = !map.ShowPicture, titleStyle: S.BodyStrong);
            GUILayout.Space(S.S(10));
            ToggleRow(inner, "map-click", "Click to teleport", "Click a dot or any spot on the map to go there.",
                map.ClickToTeleport, () => map.ClickToTeleport = !map.ClickToTeleport, titleStyle: S.BodyStrong);
        }

        private void DrawTeleports(float width)
        {
            Card(width, DrawMapCard);

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
                foreach (var quick in QuickSearches)
                {
                    bool current = string.Equals(_teleportController.CollectibleSearchTerm, quick.Terms, StringComparison.OrdinalIgnoreCase);
                    string picked = quick.Terms;
                    if (Button(quick.Label, current ? S.ChipActive : S.Chip))
                        Later(() => { _teleportController.CollectibleSearchTerm = picked; _teleportController.RefreshNearbyCollectibles(); });
                    GUILayout.Space(S.S(6));
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Space(S.S(8));
                GUILayout.BeginHorizontal();
                GUILayout.Label("Don't know what it's called?", S.Small);
                GUILayout.Space(S.S(8));
                if (Button("List names", new GUIStyle(S.Go) { fixedHeight = S.S(24) }))
                {
                    string names = _teleportController.DescribeRoomObjectNames();
                    DebugLog.Info("Room object names (List names):\n" + names);
                    GUIUtility.systemCopyBuffer = names;
                    Toasts.Show("Object names copied and written to the log");
                }
                GUILayout.Space(S.S(6));
                if (Button("Near me", new GUIStyle(S.Go) { fixedHeight = S.S(24) }))
                {
                    string names = _teleportController.DescribeObjectsNearPlayer(4f);
                    DebugLog.Info("Objects within 4 m (Near me):\n" + names);
                    GUIUtility.systemCopyBuffer = names;
                    Toasts.Show("Names of everything within 4 m copied");
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Space(S.S(10));
                ToggleRow(inner, "strict", "Exact names only", "Off also matches names that only contain the word.",
                    _teleportController.StrictNames,
                    () => { _teleportController.StrictNames = !_teleportController.StrictNames; _teleportController.RefreshNearbyCollectibles(); },
                    titleStyle: S.BodyStrong);
                GUILayout.Space(S.S(12));

                // Go to next: nearest match not visited yet. You pick it up
                // yourself, then press again (or use its hotkey).
                GUILayout.BeginHorizontal();
                if (Button("Go to next", S.Primary))
                    Later(() => Toasts.Show(_teleportController.GoToNextCollectible()));
                GUILayout.Space(S.S(10));
                BeginCentered(S.Primary.fixedHeight, HotkeyStyle("nextpickup").fixedHeight);
                HotkeyButton("nextpickup");
                EndCentered();
                // Key Hunter: carry up to 5 keys, then deposit them at the safe.
                if (_teleportController.SearchingForKeys)
                {
                    GUILayout.Space(S.S(14));
                    BeginCentered(S.Primary.fixedHeight, S.Secondary.fixedHeight);
                    if (Button("Go to safe", S.Secondary))
                        Later(() => Toasts.Show(_teleportController.GoToSafe()));
                    EndCentered();
                    GUILayout.Space(S.S(10));
                    BeginCentered(S.Primary.fixedHeight, HotkeyStyle("gotosafe").fixedHeight);
                    HotkeyButton("gotosafe");
                    EndCentered();
                }
                GUILayout.FlexibleSpace();
                // Picked-up items vanish from the room, so what's still found
                // is what's left.
                int left = _teleportController.NearbyCollectibles.Count;
                CenteredLabel(left == 0 ? "" : left + " left here",
                    new GUIStyle(S.BodyStrong) { normal = { textColor = Theme.AccentText } }, S.Primary.fixedHeight);
                GUILayout.EndHorizontal();
                GUILayout.Space(S.S(10));

                // Back to the NPC that gives and takes the collect quests.
                GUILayout.BeginHorizontal();
                if (Button("Quest giver", S.Secondary))
                    Later(() => Toasts.Show(_teleportController.GoToQuestGiver()));
                GUILayout.Space(S.S(10));
                BeginCentered(S.Secondary.fixedHeight, HotkeyStyle("questgiver").fixedHeight);
                HotkeyButton("questgiver");
                EndCentered();
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
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
                        string key = item.Key;
                        if (Button("Go", S.Go))
                        {
                            _teleportController.MarkCollectibleVisited(key);
                            _teleportController.GoToPosition(target);
                        }
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
                    FriendNotifier notifier = FriendNotifier.Instance;
                    ToggleRow(inner, "friend-arrive", "Tell me when friends arrive",
                        "A notice when a friend shows up in your room, and who is here when you arrive.",
                        notifier.Enabled, () => { notifier.Enabled = !notifier.Enabled; Toggle("Friend notices", notifier.Enabled); },
                        titleStyle: S.BodyStrong);
                    if (notifier.Enabled)
                    {
                        GUILayout.Space(S.S(10));
                        ToggleRow(inner, "friend-leave", "Also when they leave", "",
                            notifier.NotifyLeaving, () => notifier.NotifyLeaving = !notifier.NotifyLeaving, titleStyle: S.BodyStrong);
                    }
                    GUILayout.Space(S.S(14));
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
                Card(column, DrawNpcsCard);
                if (Collections != null)
                    Card(column, DrawCollectedCard);
                Card(column, DrawQuizCard);
            });
        }

        // Quest givers and other server-run characters, for getting back to
        // the NPC that hands out (and takes back) a collect quest.
        private Vector2 _npcScroll;

        private void DrawNpcsCard(float inner)
        {
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width(inner - S.S(100)));
            GUILayout.Label("NPCs in this room", S.CardTitle);
            GUILayout.Space(S.S(3));
            GUILayout.Label("Quest givers and other characters. Go takes you next to them.", S.Description, GUILayout.Width(inner - S.S(100)));
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            if (Button("Refresh", S.Secondary))
                Later(_teleportController.RefreshNearbyNpcs);
            GUILayout.EndHorizontal();
            GUILayout.Space(S.S(12));

            var npcs = _teleportController.NearbyNpcs;
            if (npcs.Count == 0)
            {
                EmptyState(string.IsNullOrEmpty(_teleportController.NpcSearchStatus)
                    ? "Press Refresh to list the NPCs here." : _teleportController.NpcSearchStatus);
                return;
            }
            float listHeight = Mathf.Min(npcs.Count * S.S(46), S.S(230));
            _npcScroll = GUILayout.BeginScrollView(_npcScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUIStyle.none, GUILayout.Height(listHeight));
            ListRows(npcs, (npc, index) =>
            {
                CenteredLabel(npc.Name, new GUIStyle(S.Body) { wordWrap = false, clipping = TextClipping.Clip }, S.Go.fixedHeight, GUILayout.Width(inner - S.S(130)));
                GUILayout.FlexibleSpace();
                CenteredLabel(_teleportController.DistanceFromPlayer(npc.Position).ToString("0") + " m", S.Small, S.Go.fixedHeight);
                GUILayout.Space(S.S(10));
                Vector3 target = npc.Position;
                if (Button("Go", S.Go))
                    _teleportController.GoToNpc(target);
            });
            GUILayout.EndScrollView();
        }

        private void DrawQuizCard(float inner)
        {
            ToggleRow(inner, "quiz", "Quiz helper",
                "During a world quiz, the right answer's button turns green with a ✓. You still click it yourself.",
                QuizHelper.Enabled, ToggleQuizHelper);
            if (QuizHelper.Entries.Count > 0 && !QuizHelper.Visible)
            {
                GUILayout.Space(S.S(10));
                GUILayout.BeginHorizontal();
                if (Button("Show all answers", S.Secondary))
                    Later(() => QuizHelper.Visible = true);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }
        }

        // What the player picked up today, from CollectionLogger.
        private void DrawCollectedCard(float inner)
        {
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width(inner - S.S(100)));
            GUILayout.Label("Collected today", S.CardTitle);
            GUILayout.Space(S.S(3));
            GUILayout.Label("Quest items you pick up (shells, letters, keys, flowers) are counted here and in collected.log.", S.Description, GUILayout.Width(inner - S.S(100)));
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            if (Button("Open log", S.Secondary))
                Collections.OpenLogFolder();
            GUILayout.EndHorizontal();
            GUILayout.Space(S.S(12));

            if (Collections.TodayTotal == 0)
            {
                EmptyState("Nothing yet today. Pick something up and it shows here.");
                return;
            }

            // Chips wrap onto new rows instead of widening the card.
            float rowWidth = 0f;
            GUILayout.BeginHorizontal();
            foreach (var type in Collections.TodayCounts)
            {
                string chip = type.Value + " " + type.Key;
                float chipWidth = S.ChipActive.CalcSize(new GUIContent(chip)).x + S.S(6);
                if (rowWidth > 0f && rowWidth + chipWidth > inner)
                {
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                    GUILayout.Space(S.S(6));
                    GUILayout.BeginHorizontal();
                    rowWidth = 0f;
                }
                GUILayout.Label(chip, S.ChipActive);
                GUILayout.Space(S.S(6));
                rowWidth += chipWidth;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(S.S(10));

            int shown = 0;
            foreach (CollectionLogger.Entry entry in Collections.Recent)
            {
                if (shown++ == 5)
                    break;
                GUILayout.BeginHorizontal();
                GUILayout.Label(entry.When.ToString("HH:mm"), S.Small, GUILayout.Width(S.S(44)));
                GUILayout.Label(entry.Name, new GUIStyle(S.Body) { wordWrap = false, clipping = TextClipping.Clip }, GUILayout.Width(inner - S.S(50)));
                GUILayout.EndHorizontal();
                GUILayout.Space(S.S(4));
            }
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

        // People ----------------------------------------------------------------

        private string _peopleSearch = "";
        private string _chatSearch = "";
        private Vector2 _peopleScroll, _chatScroll;

        private void DrawPeople(float width)
        {
            RoomScan.Instance.Want();
            Columns(width, column => Card(column, inner =>
            {
                PeopleFilter filter = PeopleFilter.Instance;
                GUILayout.Label("People in this room", S.CardTitle);
                GUILayout.Space(S.S(3));
                GUILayout.Label(RoomScan.Instance.PlayerCount + " players here, nearest first. Go takes you next to them. " +
                    "Only: show just the people you pick - everyone else, with their name tags, disappears from your screen.",
                    S.Description, GUILayout.Width(inner));
                GUILayout.Space(S.S(10));
                if (filter.Current == PeopleFilter.Mode.Picked)
                {
                    GUILayout.BeginHorizontal();
                    CenteredLabel("Showing only " + filter.Picked.Count + " picked " + (filter.Picked.Count == 1 ? "person" : "people"),
                        new GUIStyle(S.BodyStrong) { normal = { textColor = Theme.AccentText } }, S.Secondary.fixedHeight);
                    GUILayout.FlexibleSpace();
                    if (Button("Show everyone", S.Secondary))
                        Later(() => { filter.ClearPicks(); Toasts.Show("Showing everyone"); });
                    GUILayout.EndHorizontal();
                    GUILayout.Space(S.S(10));
                }
                _peopleSearch = GUILayout.TextField(_peopleSearch, S.TextField, GUILayout.Width(inner));
                GUILayout.Space(S.S(10));

                Vector3 me = _playerContext.Avatar != null ? _playerContext.Avatar.transform.position : Vector3.zero;
                var people = RoomScan.Instance.Entries.FindAll(e =>
                    (e.Kind == RoomScan.Kind.Player || e.Kind == RoomScan.Kind.Friend || e.Kind == RoomScan.Kind.Clan) &&
                    (_peopleSearch.Trim().Length == 0 ||
                     e.Name.IndexOf(_peopleSearch.Trim(), StringComparison.OrdinalIgnoreCase) >= 0 ||
                     e.Clan.IndexOf(_peopleSearch.Trim(), StringComparison.OrdinalIgnoreCase) >= 0));
                people.Sort((a, b) => (a.Position - me).sqrMagnitude.CompareTo((b.Position - me).sqrMagnitude));
                if (people.Count == 0)
                {
                    EmptyState(_peopleSearch.Trim().Length > 0 ? "Nobody here matches." : "Nobody else here yet.");
                    return;
                }
                _peopleScroll = GUILayout.BeginScrollView(_peopleScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUIStyle.none,
                    GUILayout.Height(Mathf.Min(people.Count * S.S(46), S.S(420))));
                ListRows(people, (person, index) =>
                {
                    Color dot = person.Kind == RoomScan.Kind.Friend ? new Color(0.29f, 0.87f, 0.5f)
                        : person.Kind == RoomScan.Kind.Clan ? Theme.AccentText : Theme.TextSecondary;
                    BeginCentered(S.Go.fixedHeight, S.S(8));
                    Widgets.Dot(S, S.S(8), dot);
                    EndCentered();
                    GUILayout.Space(S.S(10));
                    GUILayout.BeginVertical(GUILayout.Width(inner - S.S(220)));
                    GUILayout.Label(person.Name, new GUIStyle(S.Body) { wordWrap = false, clipping = TextClipping.Clip });
                    if (person.Clan.Length > 0)
                        GUILayout.Label(person.Clan, new GUIStyle(S.Small) { wordWrap = false, clipping = TextClipping.Clip });
                    GUILayout.EndVertical();
                    GUILayout.FlexibleSpace();
                    CenteredLabel((person.Position - me).magnitude.ToString("0") + " m", S.Small, S.Go.fixedHeight);
                    GUILayout.Space(S.S(8));
                    Guid owner = person.Owner;
                    bool picked = filter.Picked.Contains(owner);
                    if (Button(picked ? "✓ Only" : "Only", picked ? S.Primary : S.Secondary, GUILayout.Width(S.S(64))))
                        Later(() =>
                        {
                            filter.TogglePick(owner);
                            Toasts.Show(filter.Picked.Count == 0 ? "Showing everyone" : "Showing only " + filter.Picked.Count + " picked");
                        });
                    GUILayout.Space(S.S(6));
                    Vector3 target = person.Position;
                    if (Button("Go", S.Go))
                        _teleportController.GoToFriend(target);
                });
                GUILayout.EndScrollView();
            }), column => Card(column, inner =>
            {
                ChatLog chat = ChatLog.Instance;
                GUILayout.BeginHorizontal();
                GUILayout.BeginVertical(GUILayout.Width(inner - S.S(110)));
                GUILayout.Label("Chat log", S.CardTitle);
                GUILayout.Space(S.S(3));
                GUILayout.Label("Everything your chat receives, saved per day in BepInEx\\LoveMenu\\chat. Stays on this PC.",
                    S.Description, GUILayout.Width(inner - S.S(110)));
                GUILayout.EndVertical();
                GUILayout.FlexibleSpace();
                if (Button("Open folder", S.Secondary))
                    chat.OpenFolder();
                GUILayout.EndHorizontal();
                GUILayout.Space(S.S(12));
                ToggleRow(inner, "chat-save", "Save chat to a file", "Off: kept only in this list until you close the game.",
                    chat.SaveToFile, () => chat.SaveToFile = !chat.SaveToFile, titleStyle: S.BodyStrong);
                GUILayout.Space(S.S(10));
                ToggleRow(inner, "chat-mention", "Mention alerts",
                    "A notice when a message has your name (" + (_playerContext.PlayerName.Length > 0 ? _playerContext.PlayerName : "your name") +
                    ") or one of your words, and the taskbar button flashes if the game is in the background.",
                    chat.MentionAlerts, () => chat.MentionAlerts = !chat.MentionAlerts, titleStyle: S.BodyStrong);
                if (chat.MentionAlerts)
                {
                    GUILayout.Space(S.S(8));
                    GUILayout.Label("Also alert on these words (comma separated)", S.Small);
                    GUILayout.Space(S.S(4));
                    chat.ExtraWords = GUILayout.TextField(chat.ExtraWords, S.TextField, GUILayout.Width(inner));
                }
                GUILayout.Space(S.S(14));
                Widgets.Divider(S);
                GUILayout.Space(S.S(12));
                GUILayout.Label("Search the chat", S.Small);
                GUILayout.Space(S.S(4));
                _chatSearch = GUILayout.TextField(_chatSearch, S.TextField, GUILayout.Width(inner));
                GUILayout.Space(S.S(8));

                string query = _chatSearch.Trim();
                var lines = new List<ChatLog.Line>();
                for (int i = chat.Recent.Count - 1; i >= 0 && lines.Count < 80; i--)
                {
                    ChatLog.Line line = chat.Recent[i];
                    if (query.Length == 0 || line.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        line.From.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                        lines.Add(line);
                }
                if (lines.Count == 0)
                {
                    EmptyState(chat.Recent.Count == 0 ? "No messages yet this session." : "No message matches.");
                    return;
                }
                _chatScroll = GUILayout.BeginScrollView(_chatScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUIStyle.none,
                    GUILayout.Height(S.S(260)));
                var textStyle = new GUIStyle(S.Small) { wordWrap = true, richText = false };
                foreach (ChatLog.Line line in lines)
                {
                    GUILayout.Label(line.When.ToString("HH:mm") + "  [" + line.Channel + "]  " + line.From + ": " + line.Text,
                        textStyle, GUILayout.Width(inner - S.S(20)));
                    GUILayout.Space(S.S(4));
                }
                GUILayout.EndScrollView();
            }));
        }

        // Bug fixes -------------------------------------------------------------

        private void DrawLagRecorderCard(float inner)
        {
            LagRecorder lag = LagRecorder.Instance;
            ToggleRow(inner, "lag-recorder", "Lag spike recorder",
                "Writes down every hitch (a frame over 50 ms) and what happened in it: objects loading, garbage collection, chat messages, Love Menu's own work. Saved to BepInEx\\LoveMenu\\lag.log.",
                lag.Enabled, () => { lag.Enabled = !lag.Enabled; Toggle("Lag spike recorder", lag.Enabled); }, titleStyle: S.BodyStrong);
            GUILayout.Space(S.S(12));
            if (lag.Total == 0)
                EmptyState(lag.Enabled ? "No hitches yet. Play where it lags." : "Turn it on and play where it lags.");
            else
            {
                GUILayout.Label(lag.Total + " hitches: " + lag.WithLoading + " while objects loaded, " + lag.WithGc + " with garbage collection, " +
                    lag.WithChat + " with chat messages, " + lag.WithMenu + " with Love Menu work, " + lag.Unknown + " the game's own work.",
                    S.BodyStrong, GUILayout.Width(inner));
                GUILayout.Space(S.S(10));
                int shown = 0;
                foreach (LagRecorder.Spike spike in lag.Recent)
                {
                    if (shown++ == 8)
                        break;
                    GUILayout.Label(spike.When.ToString("HH:mm:ss") + "  " + spike.Ms.ToString("0") + " ms  ·  " + spike.Cause, S.Small, GUILayout.Width(inner));
                    GUILayout.Space(S.S(3));
                }
                GUILayout.Space(S.S(8));
                if (Button("Clear", S.Secondary))
                    Later(lag.Reset);
            }
            GUILayout.Space(S.S(14));
            Widgets.Divider(S);
            GUILayout.Space(S.S(14));
            PoseRecorder pose = PoseRecorder.Instance;
            ToggleRow(inner, "pose-recorder", "Pose recorder",
                "For poses and couple dances that end up inside each other: logs where you and the pose point are, and the nearest avatar, when a pose starts and every second during it.",
                pose.Enabled, () => { pose.Enabled = !pose.Enabled; Toggle("Pose recorder", pose.Enabled); }, titleStyle: S.BodyStrong);
            if (pose.LastLine.Length > 0)
            {
                GUILayout.Space(S.S(8));
                GUILayout.Label(pose.LastLine, S.Small, GUILayout.Width(inner));
            }
        }

        private void DrawGlitches(float width)
        {
            Card(width, inner =>
            {
                GUILayout.Label("Fixes", S.CardTitle);
                GUILayout.Space(S.S(3));
                GUILayout.Label("Fixes for bugs in the game itself: things that fail to load, get stuck, look wrong or stop working " +
                    "while you play. Each fix has its own switch; confirmed fixes are on by default, the rest are off until tested.",
                    S.Description, GUILayout.Width(inner));
                GUILayout.Space(S.S(12));
                WorkaroundRows(inner, "glitch-", WorkaroundPage.Glitches);
                GUILayout.Space(S.S(14));
                Widgets.Divider(S);
                GUILayout.Space(S.S(14));
                GUILayout.Label("Seen a glitch that isn't here? Report it with the room and what you did just before, and attach a crash report.",
                    S.Small, GUILayout.Width(inner));
                GUILayout.Space(S.S(10));
                GUILayout.BeginHorizontal();
                if (Button("Report a glitch", S.Primary))
                    Application.OpenURL("https://github.com/NNKTV28/LoveMenu/issues/new/choose");
                GUILayout.Space(S.S(8));
                if (Button("Make crash report", S.Secondary))
                    Later(() =>
                    {
                        try
                        {
                            BugReport.Create();
                            Toasts.Show("Report saved to your desktop - attach it to the glitch report");
                        }
                        catch (Exception exception)
                        {
                            Toasts.Show("Could not make the report: " + exception.Message);
                        }
                    });
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            });

            Card(width, DrawLagRecorderCard);
        }

        // Rendering -------------------------------------------------------------

        private void DrawRendering(float width)
        {
            Columns(width, column => Card(column, inner =>
            {
                int matching = PerformanceProfiles.Matching(_crashWorkaroundController, _performanceController);
                GUILayout.BeginHorizontal();
                GUILayout.Label("Performance profile", S.CardTitle);
                GUILayout.FlexibleSpace();
                if (matching < 0)
                    GUILayout.Label("Custom", S.Small);
                GUILayout.EndHorizontal();
                GUILayout.Space(S.S(3));
                GUILayout.Label("Sets the switches below, Show people and texture resolution in one click.", S.Description, GUILayout.Width(inner));
                GUILayout.Space(S.S(12));
                Segmented(PerformanceProfiles.Names, matching, index =>
                {
                    PerformanceProfiles.Apply((PerformanceProfiles.Profile)index, _crashWorkaroundController, _performanceController);
                    Toasts.Show(PerformanceProfiles.Names[index] + " profile");
                }, inner);
                GUILayout.Space(S.S(10));
                GUILayout.Label(matching >= 0 ? PerformanceProfiles.Summaries[matching] : "Your own mix of settings.",
                    S.Small, GUILayout.Width(inner));
            }), column => Card(column, inner =>
            {
                GUILayout.BeginHorizontal();
                GUILayout.BeginVertical(GUILayout.Width(inner - S.S(130)));
                GUILayout.Label("FPS benchmark", S.CardTitle);
                GUILayout.Space(S.S(3));
                GUILayout.Label("Measures " + FpsBenchmark.DurationSeconds.ToString("0") + " s. Stand still in the same spot, run it with a setting off and on, and compare.",
                    S.Description, GUILayout.Width(inner - S.S(130)));
                GUILayout.EndVertical();
                GUILayout.FlexibleSpace();
                if (FpsBenchmark.Running)
                {
                    if (Button("Stop · " + (FpsBenchmark.Progress * 100f).ToString("0") + "%", S.Secondary))
                        Later(FpsBenchmark.Cancel);
                }
                else if (Button("Run benchmark", S.Primary))
                {
                    int profile = PerformanceProfiles.Matching(_crashWorkaroundController, _performanceController);
                    string label = (profile >= 0 ? PerformanceProfiles.Names[profile] : "Custom") + " · " + GraphicsApiSetting.Running +
                        " · " + PeopleFilter.Names[(int)PeopleFilter.Instance.Current];
                    Later(() => FpsBenchmark.Start(label));
                }
                GUILayout.EndHorizontal();
                GUILayout.Space(S.S(10));
                if (FpsBenchmark.Results.Count == 0)
                    GUILayout.Label("No runs yet.", S.Small);
                foreach (FpsBenchmark.Result result in FpsBenchmark.Results)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(result.When.ToString("HH:mm"), S.Small, GUILayout.Width(S.S(44)));
                    GUILayout.Label(result.Label, new GUIStyle(S.Small) { wordWrap = false, clipping = TextClipping.Clip }, GUILayout.Width(inner - S.S(190)));
                    GUILayout.FlexibleSpace();
                    GUILayout.Label(result.AverageFps.ToString("0") + " avg · " + result.OnePercentLowFps.ToString("0") + " low", S.BodyStrong);
                    GUILayout.EndHorizontal();
                }
            }));

            Card(width, inner =>
            {
                PeopleFilter people = PeopleFilter.Instance;
                GUILayout.Label("Show people", S.CardTitle);
                GUILayout.Space(S.S(3));
                GUILayout.Label("In a packed room most of the frame goes on drawing other avatars. Only show your clan or your friends: " +
                    "everyone else, with their pets and name tags, isn't drawn on your screen. They still see you and chat still works.",
                    S.Description, GUILayout.Width(inner));
                GUILayout.Space(S.S(12));
                Segmented(PeopleFilter.Names, (int)people.Current, index =>
                {
                    people.Current = (PeopleFilter.Mode)index;
                    Toasts.Show("Showing " + PeopleFilter.Names[index].ToLowerInvariant());
                }, inner);
                GUILayout.Space(S.S(10));
                string status = people.Current == PeopleFilter.Mode.Everyone
                    ? "Everyone is shown."
                    : "Hiding " + people.PeopleHidden + " of " + people.PeopleHere + " people here." +
                      (people.Current == PeopleFilter.Mode.Clan
                          ? (people.MyClan.Length > 0 ? " Your clan: " + people.MyClan + "." : " You aren't in a clan, so everyone is hidden.")
                          : people.Current == PeopleFilter.Mode.Picked
                              ? (people.Picked.Count == 0 ? " Pick people with Only in the People tab." : " Showing the " + people.Picked.Count + " you picked in the People tab.")
                              : "");
                GUILayout.Label(status, S.Small, GUILayout.Width(inner));
                GUILayout.Space(S.S(14));
                AutoCrowd crowd = AutoCrowd.Instance;
                ToggleRow(inner, "auto-crowd", "Auto crowd mode",
                    "When " + crowd.CrowdAtLeast + "+ people are here and FPS stays under " + crowd.FpsBelow +
                    ", switch to Friends by itself, and back to everyone when the room empties." + (crowd.Active ? " Active now." : ""),
                    crowd.Enabled, () => { crowd.Enabled = !crowd.Enabled; Toggle("Auto crowd mode", crowd.Enabled); }, titleStyle: S.BodyStrong);
                GUILayout.Space(S.S(14));
                Widgets.Divider(S);
                GUILayout.Space(S.S(14));
                GUILayout.Label("Name tags", S.BodyStrong);
                GUILayout.Space(S.S(3));
                GUILayout.Label("Name, clan and badges above everyone are drawn every frame. Only show the tags of people this close to the camera.",
                    S.Description, GUILayout.Width(inner));
                GUILayout.Space(S.S(10));
                Segmented(NameTagDistance.Names, NameTagDistance.Instance.Choice, index => NameTagDistance.Instance.Choice = index, inner);
            });

            Card(width, inner =>
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Graphics API", S.CardTitle);
                GUILayout.Space(S.S(8));
                GUILayout.Label("EXPERIMENTAL", S.BadgeWarning);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Space(S.S(3));
                GUILayout.Label("Which graphics system the game starts with. The game ships with DirectX 12 and Vulkan built in but uses " +
                    "DirectX 11. Another one may be faster on your PC, or slower. Applies when you next start the game with the Love Menu " +
                    "exe (Steam may ask you to allow the launch options). If the game doesn't get going, the launcher goes back to DirectX 11 by itself.",
                    S.Description, GUILayout.Width(inner));
                GUILayout.Space(S.S(12));
                Segmented(GraphicsApiSetting.Names, (int)GraphicsApiSetting.Chosen,
                    index => GraphicsApiSetting.Choose((GraphicsApiSetting.Api)index, GraphicsApiSetting.NativeGraphicsJobs), inner);
                if (GraphicsApiSetting.Chosen != GraphicsApiSetting.Api.DirectX11)
                {
                    GUILayout.Space(S.S(14));
                    ToggleRow(inner, "gfxjobs", "Multithreaded rendering",
                        "Unity's native graphics jobs: spreads drawing across CPU cores. Only with DirectX 12 or Vulkan.",
                        GraphicsApiSetting.NativeGraphicsJobs,
                        () => GraphicsApiSetting.Choose(GraphicsApiSetting.Chosen, !GraphicsApiSetting.NativeGraphicsJobs), titleStyle: S.BodyStrong);
                }
                GUILayout.Space(S.S(10));
                GUILayout.Label("Running now: " + GraphicsApiSetting.Running + " (" + GraphicsApiSetting.RunningThreading + ")" +
                    (GraphicsApiSetting.RestartNeeded ? " · restart the game to apply your choice" : ""), S.Small, GUILayout.Width(inner));
            });

            Card(width, inner =>
            {
                GUILayout.Label("Rendering switches", S.CardTitle);
                GUILayout.Space(S.S(3));
                GUILayout.Label("Costs the game has no setting for, found in its code. All are off by default and apply straight away.",
                    S.Description, GUILayout.Width(inner));
                GUILayout.Space(S.S(12));
                WorkaroundRows(inner, "rendering-", WorkaroundPage.Rendering);
            });
        }

        // The experimental switches for one page, from the shared list.
        private void WorkaroundRows(float inner, string idPrefix, WorkaroundPage page)
        {
            var workarounds = _crashWorkaroundController.Workarounds;
            bool first = true;
            for (int i = 0; i < workarounds.Count; i++)
            {
                CrashWorkaround workaround = workarounds[i];
                if (workaround.Page != page)
                    continue;
                if (!first)
                    GUILayout.Space(S.S(14));
                first = false;
                ToggleRow(inner, idPrefix + i, workaround.Name, workaround.Description + " Turn it off if anything acts up.",
                    workaround.Enabled, () => _crashWorkaroundController.SetEnabled(workaround, !workaround.Enabled), titleStyle: S.BodyStrong);
            }
            if (first)
                EmptyState("None right now.");
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
                GUILayout.Space(S.S(14));
                Widgets.Divider(S);
                GUILayout.Space(S.S(14));
                ToggleRow(inner, "verbose", "Detailed logging",
                    "Writes extra detail to the log (windows, popups, knockback). Turn it on when reporting a bug, off otherwise.",
                    DebugLog.Verbose, () => { DebugLog.Verbose = !DebugLog.Verbose; Toggle("Detailed logging", DebugLog.Verbose); },
                    titleStyle: S.BodyStrong);
                GUILayout.Space(S.S(14));
                string captured = WorldScriptCapture.Enabled
                    ? " Saved so far: " + WorldScriptCapture.ScriptsSaved + " scripts, " + WorldScriptCapture.MessagesSaved +
                      " messages (seen since start: " + WorldScriptCapture.ScriptsSeen + " / " + WorldScriptCapture.MessagesSeen + ")."
                    : "";
                ToggleRow(inner, "capture", "Capture world scripts",
                    "For developers: saves the world scripts the game downloads (quests, quizzes) and the server's messages to them, to BepInEx\\LoveMenu\\captured-scripts. Off after a restart." + captured,
                    WorldScriptCapture.Enabled, () => { WorldScriptCapture.Enabled = !WorldScriptCapture.Enabled; Toggle("Script capture", WorldScriptCapture.Enabled); },
                    titleStyle: S.BodyStrong);
                GUILayout.Space(S.S(14));
                Widgets.Divider(S);
                GUILayout.Space(S.S(14));
                GUILayout.BeginHorizontal();
                GUILayout.BeginVertical(GUILayout.Width(textWidth));
                GUILayout.Label("Game errors", S.BodyStrong);
                GUILayout.Space(S.S(3));
                GUILayout.Label(ErrorLogger.Total == 0
                        ? "No errors from the game this session. Any that happen are collected in BepInEx\\LoveMenu\\errors.log."
                        : ErrorLogger.Total + " errors this session, " + ErrorLogger.Distinct + " different ones. Collected in errors.log, with a count of each in errors-summary.txt.",
                    S.Description, GUILayout.Width(textWidth));
                GUILayout.EndVertical();
                GUILayout.FlexibleSpace();
                if (Button("Open errors folder", S.Secondary))
                    ErrorLogger.OpenFolder();
                GUILayout.EndHorizontal();
                GUILayout.Space(S.S(14));
                Widgets.Divider(S);
                GUILayout.Space(S.S(14));
                GUILayout.BeginHorizontal();
                GUILayout.BeginVertical(GUILayout.Width(textWidth));
                GUILayout.Label("Crash report", S.BodyStrong);
                GUILayout.Space(S.S(3));
                GUILayout.Label(BugReport.LastPath == null
                        ? "Puts the logs, collected errors, crash dumps, your settings and a short PC summary in one zip on your desktop, ready to send. Nothing is uploaded."
                        : "Saved: " + BugReport.LastPath,
                    S.Description, GUILayout.Width(textWidth));
                GUILayout.EndVertical();
                GUILayout.FlexibleSpace();
                if (Button("Make crash report", S.Secondary))
                    Later(() =>
                    {
                        try
                        {
                            BugReport.Create();
                            Toasts.Show("Crash report saved to your desktop");
                        }
                        catch (Exception exception)
                        {
                            Toasts.Show("Could not make the report: " + exception.Message);
                        }
                    });
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
                WorkaroundRows(inner, "workaround-", WorkaroundPage.Crashes);
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
                GUILayout.Space(S.S(14));
                FortuneTracker fortune = FortuneTracker.Instance;
                ToggleRow(inner, "fortune-notify", "Kiss of Fortune notice",
                    "A notice when a fortune ball's free opening is ready. The countdown is on Home.",
                    fortune.NotifyWhenFree, () => { fortune.NotifyWhenFree = !fortune.NotifyWhenFree; Toggle("Fortune notice", fortune.NotifyWhenFree); },
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
                GUILayout.Space(S.S(8));
                if (Button("Welcome tour", S.Secondary))
                    Later(() => { SetOpen(false); OnShowWelcome?.Invoke(); });
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
