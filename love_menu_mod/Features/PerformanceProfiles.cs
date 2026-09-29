using System.Collections.Generic;

namespace FlyMod.Features
{
    // One-click presets for the Rendering switches, Show people and texture
    // resolution, so nobody has to judge seven switches one by one.
    // "Custom" is shown when the current settings match no preset.
    internal static class PerformanceProfiles
    {
        public enum Profile { Quality, Balanced, Potato }

        public static readonly string[] Names = { "Quality", "Balanced", "Potato" };

        public static readonly string[] Summaries =
        {
            "Everything as the game draws it. All rendering switches off, full textures, everyone shown.",
            "Fixes the stutters and hidden costs, keeps the look: smooth loading, no reflection hitch, cheaper mirrors, decals for the main view, only the sun casts shadows.",
            "Everything off that can be: all rendering switches on, only friends shown, quarter textures. For weak PCs and packed clubs.",
        };

        private static readonly string[] AllSwitches =
            { "smooth-loading", "lamp-shadows", "avatar-distance", "mirrors", "probe-refresh", "decals", "sky-effects" };

        private static readonly Dictionary<Profile, string[]> SwitchesOn = new Dictionary<Profile, string[]>
        {
            { Profile.Quality, new string[0] },
            { Profile.Balanced, new[] { "smooth-loading", "lamp-shadows", "mirrors", "probe-refresh", "decals" } },
            { Profile.Potato, AllSwitches },
        };

        private static PeopleFilter.Mode PeopleFor(Profile profile) =>
            profile == Profile.Potato ? PeopleFilter.Mode.Friends : PeopleFilter.Mode.Everyone;

        private static int TexturesFor(Profile profile) => profile == Profile.Potato ? 2 : 0;

        public static void Apply(Profile profile, CrashWorkaroundController switches, PerformanceController performance)
        {
            var on = new HashSet<string>(SwitchesOn[profile]);
            foreach (string id in AllSwitches)
            {
                CrashWorkaround workaround = switches.Find(id);
                if (workaround != null && workaround.Enabled != on.Contains(id))
                    switches.SetEnabled(workaround, on.Contains(id));
            }
            PeopleFilter.Instance.Current = PeopleFor(profile);
            if (performance.TextureQualityLimit != TexturesFor(profile))
                performance.TextureQualityLimit = TexturesFor(profile);
        }

        // The preset the current settings match, or -1 for custom.
        public static int Matching(CrashWorkaroundController switches, PerformanceController performance)
        {
            for (int i = 0; i < Names.Length; i++)
            {
                var profile = (Profile)i;
                var on = new HashSet<string>(SwitchesOn[profile]);
                bool match = PeopleFilter.Instance.Current == PeopleFor(profile) && performance.TextureQualityLimit == TexturesFor(profile);
                foreach (string id in AllSwitches)
                {
                    CrashWorkaround workaround = switches.Find(id);
                    if (workaround != null && workaround.Enabled != on.Contains(id))
                        match = false;
                }
                if (match)
                    return i;
            }
            return -1;
        }
    }
}
