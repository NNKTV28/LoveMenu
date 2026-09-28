using System.Collections.Generic;
using UnityEngine;

namespace FlyMod.UI
{
    // Builds the dark/accent GUIStyle set the menu draws with. Rebuild()
    // used to run its full body every OnGUI frame the menu was open,
    // creating ~16 new Texture2D each time with nothing ever destroying the
    // old ones - over a session that's hundreds of thousands of leaked GPU
    // resources, and was the actual cause of a "Resource ID out of range"
    // / d3d11 out-of-memory crash. Now it only rebuilds (and only creates
    // new textures, destroying the previous set first) when the theme
    // values actually changed since the last call.
    internal class MenuStyles
    {
        // Typography tiers: Header (bright, what you're looking at) ->
        // Label (normal control text) -> Description (dimmer but still
        // easily readable, ~65% white) -> Meta (truly secondary, footer/
        // hint text only).
        public GUIStyle Window, Header, Label, Description, Meta, Dim;
        public GUIStyle ToggleOn, ToggleOff;
        public GUIStyle Card, WarningCard, Divider;
        public GUIStyle Sidebar, SidebarItem, SidebarItemActive, TopBar;
        public GUIStyle Chip, ChipRemove, Keycap;
        // Stat-card text. Titles must never wrap - a two-line title made
        // that one card taller than its neighbours and left the whole row
        // ragged, so these clip instead.
        public GUIStyle CardTitle, CardValue;

        public Texture2D AccentTexture { get; private set; }

        private readonly List<Texture2D> _ownedTextures = new List<Texture2D>();
        private bool _hasBuilt;
        private int _lastIndex;
        private float _lastAlpha;
        private float _lastUIScale;

        private Texture2D MakeSolidTexture(Color color)
        {
            var texture = new Texture2D(1, 1);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            _ownedTextures.Add(texture);
            return texture;
        }

        private void DestroyOwnedTextures()
        {
            foreach (Texture2D texture in _ownedTextures)
                if (texture != null)
                    Object.Destroy(texture);
            _ownedTextures.Clear();
        }

        public void Rebuild(Theme theme)
        {
            bool unchanged = _hasBuilt && theme.Index == _lastIndex &&
                Mathf.Approximately(theme.Alpha, _lastAlpha) && Mathf.Approximately(theme.UIScale, _lastUIScale);
            if (unchanged)
                return;

            DestroyOwnedTextures();

            float scale = theme.UIScale;
            Texture2D backgroundTexture = MakeSolidTexture(new Color(0.07f, 0.08f, 0.10f, theme.Alpha));
            Texture2D cardTexture = MakeSolidTexture(new Color(0.115f, 0.125f, 0.155f, 1f));
            Texture2D warningCardTexture = MakeSolidTexture(new Color(0.20f, 0.13f, 0.06f, 1f));
            Texture2D accentTexture = MakeSolidTexture(theme.Accent);
            Texture2D accentTextureHover = MakeSolidTexture(Brighten(theme.Accent, 0.12f));
            Texture2D accentTexturePressed = MakeSolidTexture(Darken(theme.Accent, 0.15f));
            Texture2D offTexture = MakeSolidTexture(new Color(0.17f, 0.17f, 0.20f, 1f));
            Texture2D offTextureHover = MakeSolidTexture(new Color(0.22f, 0.22f, 0.26f, 1f));
            Texture2D offTexturePressed = MakeSolidTexture(new Color(0.12f, 0.12f, 0.14f, 1f));
            AccentTexture = accentTexture;

            Window = new GUIStyle(GUI.skin.window)
            {
                normal = { background = backgroundTexture, textColor = Color.white },
                onNormal = { background = backgroundTexture, textColor = Color.white },
                fontStyle = FontStyle.Bold,
                padding = new RectOffset(0, 0, 22, 0),
            };

            Header = new GUIStyle(GUI.skin.label)
            {
                fontSize = Scaled(16, scale),
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white },
            };
            Label = new GUIStyle(GUI.skin.label)
            {
                fontSize = Scaled(13, scale),
                normal = { textColor = new Color(0.86f, 0.86f, 0.90f) },
            };
            Description = new GUIStyle(Label)
            {
                fontSize = Scaled(11, scale),
                normal = { textColor = new Color(0.66f, 0.66f, 0.72f) },
            };
            Meta = new GUIStyle(Label)
            {
                fontSize = Scaled(10, scale),
                normal = { textColor = new Color(0.42f, 0.42f, 0.48f) },
            };
            Dim = Description; // kept as an alias so older call sites still compile

            CardTitle = new GUIStyle(Description)
            {
                wordWrap = false,
                clipping = TextClipping.Clip,
                normal = { textColor = new Color(0.58f, 0.58f, 0.66f) },
            };
            CardValue = new GUIStyle(Label)
            {
                fontSize = Scaled(15, scale),
                wordWrap = false,
                clipping = TextClipping.Clip,
                normal = { textColor = Color.white },
            };

            ToggleOn = new GUIStyle(GUI.skin.button)
            {
                normal = { background = accentTexture, textColor = Color.white },
                hover = { background = accentTextureHover, textColor = Color.white },
                active = { background = accentTexturePressed, textColor = Color.white },
                fontStyle = FontStyle.Bold,
                fontSize = Scaled(12, scale),
                fixedHeight = Scaled(26, scale),
            };
            ToggleOff = new GUIStyle(GUI.skin.button)
            {
                normal = { background = offTexture, textColor = new Color(0.72f, 0.72f, 0.78f) },
                hover = { background = offTextureHover, textColor = new Color(0.85f, 0.85f, 0.90f) },
                active = { background = offTexturePressed, textColor = new Color(0.6f, 0.6f, 0.66f) },
                fontSize = Scaled(12, scale),
                fixedHeight = Scaled(26, scale),
            };

            Card = new GUIStyle
            {
                normal = { background = cardTexture },
                margin = new RectOffset(0, 0, 0, 8),
                padding = new RectOffset(12, 12, 10, 10),
            };
            WarningCard = new GUIStyle(Card)
            {
                normal = { background = warningCardTexture },
            };
            Divider = new GUIStyle
            {
                normal = { background = MakeSolidTexture(new Color(1f, 1f, 1f, 0.08f)) },
                fixedHeight = 1,
                margin = new RectOffset(0, 0, 8, 8),
            };

            Sidebar = new GUIStyle
            {
                normal = { background = MakeSolidTexture(new Color(0.045f, 0.05f, 0.065f, theme.Alpha)) },
                padding = new RectOffset(0, 0, 14, 10),
            };

            SidebarItem = new GUIStyle(GUI.skin.button)
            {
                normal = { textColor = new Color(0.58f, 0.58f, 0.66f), background = MakeSolidTexture(new Color(0, 0, 0, 0)) },
                hover = { textColor = new Color(0.85f, 0.85f, 0.92f), background = MakeSolidTexture(new Color(1f, 1f, 1f, 0.04f)) },
                active = { textColor = Color.white, background = MakeSolidTexture(new Color(1f, 1f, 1f, 0.07f)) },
                alignment = TextAnchor.MiddleLeft,
                fontSize = Scaled(13, scale),
                padding = new RectOffset(16, 10, 0, 0),
                margin = new RectOffset(0, 0, 0, 2),
            };
            SidebarItemActive = new GUIStyle(SidebarItem)
            {
                normal = { textColor = Color.white, background = MakeSolidTexture(new Color(1f, 1f, 1f, 0.09f)) },
                hover = { textColor = Color.white, background = MakeSolidTexture(new Color(1f, 1f, 1f, 0.11f)) },
                fontStyle = FontStyle.Bold,
            };

            TopBar = new GUIStyle
            {
                normal = { background = MakeSolidTexture(new Color(0.09f, 0.10f, 0.125f, theme.Alpha)) },
                padding = new RectOffset(20, 16, 0, 0),
            };

            Chip = new GUIStyle(GUI.skin.button)
            {
                normal = { background = MakeSolidTexture(new Color(0.18f, 0.18f, 0.22f, 1f)), textColor = new Color(0.85f, 0.85f, 0.9f) },
                hover = { background = MakeSolidTexture(new Color(0.30f, 0.16f, 0.30f, 1f)), textColor = Color.white },
                fontSize = Scaled(11, scale),
                padding = new RectOffset(10, 8, 4, 4),
            };
            ChipRemove = new GUIStyle(Chip) { fontStyle = FontStyle.Bold };

            Keycap = new GUIStyle(GUI.skin.box)
            {
                normal = { background = offTexture, textColor = new Color(0.85f, 0.85f, 0.9f) },
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                fontSize = Scaled(11, scale),
                padding = new RectOffset(8, 8, 4, 4),
            };

            _lastIndex = theme.Index;
            _lastAlpha = theme.Alpha;
            _lastUIScale = theme.UIScale;
            _hasBuilt = true;
        }

        private static int Scaled(int baseSize, float scale) => Mathf.RoundToInt(baseSize * scale);

        private static Color Brighten(Color c, float amount) =>
            new Color(Mathf.Clamp01(c.r + amount), Mathf.Clamp01(c.g + amount), Mathf.Clamp01(c.b + amount), c.a);

        private static Color Darken(Color c, float amount) =>
            new Color(Mathf.Clamp01(c.r - amount), Mathf.Clamp01(c.g - amount), Mathf.Clamp01(c.b - amount), c.a);
    }
}
