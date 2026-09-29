using System;
using System.Collections.Generic;

namespace FlyMod.Features
{
    // Holds one toggle per *confirmed* crash cause - confirmed meaning we
    // found the actual trigger in Logs\bepinex_history.log or an archived
    // Unity player log (see LoveMenuLauncher), not guessed. Empty until a
    // real one turns up; the Crashes tab shows an honest "none identified
    // yet" message rather than fabricated toggles.
    internal class CrashWorkaround
    {
        public string Id;               // stable, for performance profiles
        public string Name;
        public string Description;
        public bool Enabled;
        // Shown on the Rendering page instead of the Crashes page.
        public bool OnRenderingPage;
        public Action<bool> OnToggle;
    }

    // A confirmed cause that's just permanently fixed in code - no tradeoff,
    // nothing to disable, so it doesn't belong in the toggleable Workarounds
    // list (that framing is for risky/experimental suppressions specifically).
    internal class ConfirmedCrashFix
    {
        public string Name;
        public string Description;
    }

    internal class CrashWorkaroundController
    {
        public readonly List<CrashWorkaround> Workarounds = new List<CrashWorkaround>();
        public readonly List<ConfirmedCrashFix> ConfirmedFixes = new List<ConfirmedCrashFix>();

        public CrashWorkaround Find(string id) => Workarounds.Find(w => w.Id == id);

        public void SetEnabled(CrashWorkaround workaround, bool enabled)
        {
            workaround.Enabled = enabled;
            workaround.OnToggle?.Invoke(enabled);
        }
    }
}
