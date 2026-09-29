using UnityEngine;

namespace FlyMod.UI
{
    // Every colour the menu draws with, plus the user's appearance choices.
    // The palette matches the approved 1.3 mockup; only the accent and the
    // background opacity are user-controlled.
    internal class Theme
    {
        public float Alpha = 0.96f;          // window background opacity
        public int Index;                    // accent colour index
        public float UIScale = 1f;           // manual menu size, used when AutoScale is off
        public bool AutoScale = true;

        public static readonly string[] AccentNames = { "Violet", "Pink", "Blue", "Teal" };

        private static readonly Color[] Accents =
        {
            Hex(0x7c3aed),
            Hex(0xdb2777),
            Hex(0x2563eb),
            Hex(0x0f766e),
        };

        public static Color AccentAt(int index) => Accents[Mathf.Clamp(index, 0, Accents.Length - 1)];
        public Color Accent => AccentAt(Index);

        // Designed at 1080p. Auto size follows the screen height so the menu
        // is the same physical size on 1440p and 4K instead of shrinking.
        public float Scale => AutoScale
            ? Mathf.Clamp(Screen.height / 1080f, 0.85f, 2.5f)
            : Mathf.Clamp(UIScale, 0.75f, 2.5f);

        public static readonly Color WindowBackground = Hex(0x15131c);
        public static readonly Color SidebarBackground = Hex(0x100e16);
        public static readonly Color CardBackground = Hex(0x1c1925);
        public static readonly Color CardBorder = Hex(0x2b2738);
        public static readonly Color Divider = Hex(0x231f2e);
        public static readonly Color InputBackground = Hex(0x13111a);
        public static readonly Color ControlBorder = Hex(0x3a3548);
        public static readonly Color SecondaryButton = Hex(0x221e2d);
        public static readonly Color Selected = Hex(0x2a2438);
        public static readonly Color Hover = Hex(0x1f1b2a);
        public static readonly Color Track = Hex(0x3a3548);

        public static readonly Color TextStrong = Hex(0xf5f2fb);
        public static readonly Color Text = Hex(0xefecf5);
        public static readonly Color TextSoft = Hex(0xcfc9dc);
        public static readonly Color TextSecondary = Hex(0xaaa4ba);
        public static readonly Color AccentText = Hex(0xb9a3ff);
        public static readonly Color AccentTextSoft = Hex(0xd6c9ff);

        public static readonly Color WarningBackground = Hex(0x3a2a12);
        public static readonly Color WarningText = Hex(0xf5b35e);
        public static readonly Color SuccessBackground = Hex(0x173524);
        public static readonly Color SuccessText = Hex(0x6ee7a0);
        public static readonly Color Online = Hex(0x4ade80);
        public static readonly Color DangerBorder = Hex(0x5a2a36);
        public static readonly Color DangerText = Hex(0xf7a1b2);
        public static readonly Color RamLine = Hex(0xf0a35e);

        public static Color Hex(int rgb, float alpha = 1f) =>
            new Color(((rgb >> 16) & 0xff) / 255f, ((rgb >> 8) & 0xff) / 255f, (rgb & 0xff) / 255f, alpha);
    }
}
