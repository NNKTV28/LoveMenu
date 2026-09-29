using System.Collections.Generic;
using UnityEngine;

namespace FlyMod.UI
{
    // Builds every GUIStyle and texture the menu draws with. Rebuild() only
    // does work when the accent, opacity or scale actually changed: an
    // earlier version created textures every frame and never destroyed
    // them, which exhausted D3D11's resource table and crashed the game.
    //
    // Rounded shapes are small 9-sliced textures (GUIStyle.border keeps the
    // corners crisp at any size); sizes are multiplied by the menu scale
    // here rather than through GUI.matrix, which would blur the text.
    internal class MenuStyles
    {
        public GUIStyle Window, Sidebar;
        public GUIStyle Brand, BrandMeta, Title, Subtitle, SectionLabel;
        public GUIStyle CardTitle, Body, BodyStrong, Description, Small, StatValue, StatValueSmall, StatUnit;
        public GUIStyle Card, ListRow, ListRowFirst, Banner, Toast, OverlayPill, Dropdown, DropdownRow;
        public GUIStyle NavItem, NavItemActive;
        public GUIStyle Primary, Secondary, Go, Chip, ChipActive, Danger, IconButton;
        public GUIStyle Keycap, KeycapEmpty, KeycapListening;
        public GUIStyle SegmentContainer, SegmentOn, SegmentOff;
        public GUIStyle TextField, SearchField;
        public GUIStyle BadgeWarning, BadgeSuccess;
        public GUIStyle VerticalScrollbar, VerticalThumb;
        public GUIStyle Swatch, SwatchSelected;
        // White 9-sliced shapes for widgets to tint with GUI.color.
        public GUIStyle PillShape, SmallPillShape, FillShape;

        // White shapes, tinted with GUI.color by the widgets that draw them.
        public Texture2D Pill { get; private set; }
        public Texture2D Circle { get; private set; }
        public Texture2D RoundedFill { get; private set; }

        public float Scale { get; private set; } = 1f;

        private static Font _font;
        private readonly List<Texture2D> _ownedTextures = new List<Texture2D>();
        private bool _hasBuilt;
        private int _lastIndex;
        private float _lastAlpha;
        private float _lastScale;

        public float S(float designPixels) => designPixels * Scale;
        private int Si(float designPixels) => Mathf.Max(1, Mathf.RoundToInt(designPixels * Scale));

        public void Rebuild(Theme theme)
        {
            float scale = theme.Scale;
            bool unchanged = _hasBuilt && theme.Index == _lastIndex &&
                Mathf.Approximately(theme.Alpha, _lastAlpha) && Mathf.Approximately(scale, _lastScale);
            if (unchanged)
                return;

            DestroyOwnedTextures();
            Scale = scale;
            if (_font == null)
                _font = Font.CreateDynamicFontFromOSFont(new[] { "Segoe UI", "Tahoma", "Arial" }, 16);

            Color accent = theme.Accent;
            Color windowFill = Theme.WindowBackground;
            windowFill.a = theme.Alpha;
            Color sidebarFill = Theme.SidebarBackground;
            sidebarFill.a = theme.Alpha;

            Pill = Rounded(Color.white, 11, null);
            Circle = Rounded(Color.white, 8, null);
            RoundedFill = Rounded(Color.white, 6, null);
            PillShape = Box(Pill, 11);
            SmallPillShape = Box(Rounded(Color.white, 2, null), 2);
            FillShape = Box(RoundedFill, 6);

            Window = Box(Rounded(windowFill, 14, Theme.CardBorder), 14);
            Sidebar = Box(Rounded(sidebarFill, 14, null, roundLeft: true, roundRight: false), 14);
            Sidebar.padding = Pad(12, 12, 18, 14);

            Brand = Text(15, Theme.TextStrong, bold: true);
            BrandMeta = Text(12, Theme.TextSecondary);
            Title = Text(20, Theme.TextStrong, bold: true);
            Subtitle = Text(13, Theme.TextSecondary);
            SectionLabel = Text(12, Theme.TextSecondary, bold: true);
            CardTitle = Text(14, Theme.Text, bold: true);
            Body = Text(13, Theme.Text);
            BodyStrong = Text(13, Theme.Text, bold: true);
            Description = Text(12.5f, Theme.TextSecondary, wrap: true);
            Small = Text(12, Theme.TextSecondary);
            StatValue = Text(22, Theme.TextStrong, bold: true);
            StatValueSmall = Text(16, Theme.Text, bold: true);
            StatUnit = Text(13, Theme.TextSecondary);

            Card = Box(Rounded(Theme.CardBackground, 10, Theme.CardBorder), 10);
            Card.padding = Pad(18, 18, 16, 16);
            ListRow = Box(Solid(Hex(0x19161f)), 0);
            ListRow.padding = Pad(12, 12, 8, 8);
            ListRowFirst = ListRow;
            Banner = Box(Rounded(Hex(0x221c33), 10, Hex(0x4b3f6a)), 10);
            Banner.padding = Pad(16, 14, 12, 12);
            Toast = Box(Rounded(Theme.Selected, 9, Hex(0x4b3f6a)), 9);
            Toast.padding = Pad(14, 14, 10, 10);
            Toast.font = _font;
            Toast.fontSize = Si(13);
            Toast.normal.textColor = Theme.TextStrong;
            OverlayPill = Box(Rounded(new Color(0.063f, 0.055f, 0.086f, 0.82f), 7, null), 7);
            OverlayPill.padding = Pad(10, 10, 5, 5);
            OverlayPill.font = _font;
            OverlayPill.fontSize = Si(12);
            OverlayPill.fontStyle = FontStyle.Bold;
            OverlayPill.normal.textColor = Theme.TextStrong;
            Dropdown = Box(Rounded(Hex(0x1f1b2a), 9, Theme.ControlBorder), 9);
            Dropdown.padding = Pad(4, 4, 4, 4);
            DropdownRow = Button(null, Theme.Text, 13, hover: Theme.Selected, radius: 6);
            DropdownRow.alignment = TextAnchor.MiddleLeft;
            DropdownRow.padding = Pad(10, 10, 0, 0);
            DropdownRow.fixedHeight = S(34);

            NavItem = Button(null, Theme.TextSecondary, 13.5f, hover: Theme.Hover, radius: 8);
            NavItem.alignment = TextAnchor.MiddleLeft;
            NavItem.padding = Pad(44, 12, 0, 0);
            NavItem.fixedHeight = S(40);
            NavItemActive = Button(Theme.Selected, Theme.TextStrong, 13.5f, hover: Theme.Selected, radius: 8, bold: true);
            NavItemActive.alignment = TextAnchor.MiddleLeft;
            NavItemActive.padding = Pad(44, 12, 0, 0);
            NavItemActive.fixedHeight = S(40);

            Primary = Button(accent, Color.white, 13, hover: Brighten(accent, 0.08f), radius: 8, bold: true);
            Primary.fixedHeight = S(36);
            Primary.padding = Pad(16, 16, 0, 0);
            Secondary = Button(Theme.SecondaryButton, Theme.Text, 13, hover: Theme.Selected, radius: 8, border: Theme.ControlBorder);
            Secondary.fixedHeight = S(34);
            Secondary.padding = Pad(14, 14, 0, 0);
            Go = Button(Theme.Selected, Theme.AccentTextSoft, 12.5f, hover: Hex(0x352d48), radius: 6);
            Go.fixedHeight = S(28);
            Go.padding = Pad(12, 12, 0, 0);
            Chip = Button(null, Theme.TextSoft, 12.5f, hover: Theme.Hover, radius: 14, border: Theme.ControlBorder);
            Chip.fixedHeight = S(28);
            Chip.padding = Pad(12, 12, 0, 0);
            ChipActive = Button(accent, Color.white, 12.5f, hover: Brighten(accent, 0.08f), radius: 14, bold: true);
            ChipActive.fixedHeight = S(28);
            ChipActive.padding = Pad(12, 12, 0, 0);
            Danger = Button(null, Theme.DangerText, 13, hover: Hex(0x2a1419), radius: 8, border: Theme.DangerBorder);
            Danger.fixedHeight = S(34);
            Danger.padding = Pad(14, 14, 0, 0);
            IconButton = Button(Theme.SecondaryButton, Theme.TextSoft, 13, hover: Theme.Selected, radius: 8);
            IconButton.fixedWidth = S(32);
            IconButton.fixedHeight = S(32);

            Keycap = Button(Theme.InputBackground, Theme.Text, 12.5f, hover: Theme.Hover, radius: 7, border: Theme.ControlBorder, bold: true);
            Keycap.fixedHeight = S(30);
            Keycap.padding = Pad(10, 10, 0, 0);
            KeycapEmpty = Button(null, Theme.TextSecondary, 11.5f, hover: Theme.Hover, radius: 6, border: Theme.ControlBorder);
            KeycapEmpty.fixedHeight = S(26);
            KeycapEmpty.padding = Pad(9, 9, 0, 0);
            KeycapListening = Button(Theme.Selected, Theme.AccentTextSoft, 12.5f, hover: Theme.Selected, radius: 7, border: accent, bold: true);
            KeycapListening.fixedHeight = S(30);
            KeycapListening.padding = Pad(10, 10, 0, 0);

            SegmentContainer = Box(Rounded(Theme.InputBackground, 9, null), 9);
            SegmentContainer.padding = Pad(4, 4, 4, 4);
            SegmentOn = Button(accent, Color.white, 12.5f, hover: accent, radius: 6, bold: true);
            SegmentOn.fixedHeight = S(32);
            SegmentOff = Button(null, Theme.TextSoft, 12.5f, hover: Theme.Hover, radius: 6, bold: true);
            SegmentOff.fixedHeight = S(32);

            TextField = new GUIStyle(GUI.skin.textField)
            {
                font = _font,
                fontSize = Si(13),
                normal = { background = Rounded(Theme.InputBackground, 8, Theme.ControlBorder), textColor = Theme.Text },
                hover = { background = Rounded(Theme.InputBackground, 8, Hex(0x4b4560)), textColor = Theme.Text },
                focused = { background = Rounded(Theme.InputBackground, 8, accent), textColor = Theme.Text },
                border = Border(8),
                padding = Pad(12, 12, 0, 0),
                alignment = TextAnchor.MiddleLeft,
                fixedHeight = S(36),
            };

            // The header search box leaves room for its magnifier icon.
            SearchField = new GUIStyle(TextField) { padding = Pad(34, 12, 0, 0) };

            BadgeWarning = Badge(Theme.WarningBackground, Theme.WarningText);
            BadgeSuccess = Badge(Theme.SuccessBackground, Theme.SuccessText);

            // Named like the built-in styles: IMGUI finds a scrollbar's thumb
            // and buttons by appending "thumb"/"upbutton" to the bar's name.
            VerticalScrollbar = new GUIStyle { name = "verticalscrollbar", fixedWidth = S(8), margin = Pad(4, 0, 0, 0) };
            VerticalThumb = Box(Rounded(Hex(0x34304a), 4, null), 4);
            VerticalThumb.name = "verticalscrollbarthumb";
            VerticalThumb.fixedWidth = S(8);

            Swatch = new GUIStyle { fixedWidth = S(30), fixedHeight = S(30), margin = Pad(0, 8, 0, 0) };
            SwatchSelected = Swatch;

            _lastIndex = theme.Index;
            _lastAlpha = theme.Alpha;
            _lastScale = scale;
            _hasBuilt = true;
        }

        // --- style builders --------------------------------------------------

        private GUIStyle Text(float size, Color color, bool bold = false, bool wrap = false) => new GUIStyle(GUI.skin.label)
        {
            font = _font,
            fontSize = Si(size),
            fontStyle = bold ? FontStyle.Bold : FontStyle.Normal,
            normal = { textColor = color },
            wordWrap = wrap,
            padding = Pad(0, 0, 0, 0),
            margin = Pad(0, 0, 0, 0),
        };

        private GUIStyle Box(Texture2D background, int radius) => new GUIStyle
        {
            normal = { background = background },
            border = Border(radius),
            margin = Pad(0, 0, 0, 0),
        };

        private GUIStyle Button(Color? fill, Color textColor, float size, Color hover, int radius, Color? border = null, bool bold = false)
        {
            Texture2D normal = fill.HasValue ? Rounded(fill.Value, radius, border) : (border.HasValue ? Rounded(new Color(0, 0, 0, 0), radius, border) : null);
            return new GUIStyle
            {
                font = _font,
                fontSize = Si(size),
                fontStyle = bold ? FontStyle.Bold : FontStyle.Normal,
                alignment = TextAnchor.MiddleCenter,
                normal = { background = normal, textColor = textColor },
                hover = { background = Rounded(hover, radius, border), textColor = textColor },
                active = { background = Rounded(Darken(hover, 0.04f), radius, border), textColor = textColor },
                border = Border(radius),
                margin = Pad(0, 0, 0, 0),
                clipping = TextClipping.Clip,
            };
        }

        private GUIStyle Badge(Color fill, Color text)
        {
            GUIStyle badge = Box(Rounded(fill, 5, null), 5);
            badge.font = _font;
            badge.fontSize = Si(11);
            badge.fontStyle = FontStyle.Bold;
            badge.normal.textColor = text;
            badge.padding = Pad(7, 7, 2, 2);
            badge.alignment = TextAnchor.MiddleCenter;
            return badge;
        }

        private RectOffset Pad(float left, float right, float top, float bottom) =>
            new RectOffset(Mathf.RoundToInt(left * Scale), Mathf.RoundToInt(right * Scale), Mathf.RoundToInt(top * Scale), Mathf.RoundToInt(bottom * Scale));

        private RectOffset Border(int radius)
        {
            int slice = Mathf.RoundToInt(radius * Scale) + 1;
            return new RectOffset(slice, slice, slice, slice);
        }

        // --- textures --------------------------------------------------------

        private Texture2D Solid(Color color)
        {
            var texture = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            texture.SetPixel(0, 0, color);
            texture.Apply();
            _ownedTextures.Add(texture);
            return texture;
        }

        // Anti-aliased rounded rectangle, optionally with a 1 px (scaled)
        // border, small enough to 9-slice. roundLeft/roundRight let the
        // sidebar keep square inner corners against the content area.
        private Texture2D Rounded(Color fill, int designRadius, Color? border, bool roundLeft = true, bool roundRight = true)
        {
            int radius = Mathf.Max(1, Mathf.RoundToInt(designRadius * Scale));
            int size = radius * 2 + 3;
            float borderWidth = Mathf.Max(1f, Mathf.Round(Scale));
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                bool leftHalf = x < size / 2f;
                bool rounded = leftHalf ? roundLeft : roundRight;
                float distanceInside = rounded ? DistanceInsideRoundedRect(x + 0.5f, y + 0.5f, size, radius) : DistanceInsideRect(x + 0.5f, y + 0.5f, size);
                float coverage = Mathf.Clamp01(distanceInside + 0.5f);
                Color color = fill;
                if (border.HasValue)
                    color = Color.Lerp(border.Value, fill, Mathf.Clamp01(distanceInside - borderWidth + 0.5f));
                color.a *= coverage;
                pixels[y * size + x] = color;
            }
            texture.SetPixels(pixels);
            texture.Apply();
            _ownedTextures.Add(texture);
            return texture;
        }

        private static float DistanceInsideRoundedRect(float px, float py, int size, int radius)
        {
            float cx = Mathf.Clamp(px, radius, size - radius);
            float cy = Mathf.Clamp(py, radius, size - radius);
            float dx = px - cx, dy = py - cy;
            if (dx != 0f || dy != 0f)
                return radius - Mathf.Sqrt(dx * dx + dy * dy);
            return Mathf.Min(Mathf.Min(px, size - px), Mathf.Min(py, size - py));
        }

        private static float DistanceInsideRect(float px, float py, int size) =>
            Mathf.Min(Mathf.Min(px, size - px), Mathf.Min(py, size - py));

        private void DestroyOwnedTextures()
        {
            foreach (Texture2D texture in _ownedTextures)
                if (texture != null)
                    Object.Destroy(texture);
            _ownedTextures.Clear();
        }

        public static Color Hex(int rgb) => Theme.Hex(rgb);

        private static Color Brighten(Color c, float amount) =>
            new Color(Mathf.Clamp01(c.r + amount), Mathf.Clamp01(c.g + amount), Mathf.Clamp01(c.b + amount), c.a);

        private static Color Darken(Color c, float amount) =>
            new Color(Mathf.Clamp01(c.r - amount), Mathf.Clamp01(c.g - amount), Mathf.Clamp01(c.b - amount), c.a);
    }
}
