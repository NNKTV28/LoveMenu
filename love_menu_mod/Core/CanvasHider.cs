using System.Collections.Generic;
using UnityEngine;

namespace FlyMod.Core
{
    // Several features hide name tags (Show people, name tag distance).
    // A canvas is shown again only when no feature wants it hidden, and then
    // gets back the state it had before the first one hid it.
    internal static class CanvasHider
    {
        private class State
        {
            public bool Original;
            public readonly HashSet<string> Reasons = new HashSet<string>();
        }

        private static readonly Dictionary<Canvas, State> Hidden = new Dictionary<Canvas, State>();

        public static void Hide(Canvas canvas, string reason)
        {
            if (canvas == null)
                return;
            if (!Hidden.TryGetValue(canvas, out State state))
            {
                state = new State { Original = canvas.enabled };
                Hidden[canvas] = state;
            }
            state.Reasons.Add(reason);
            if (canvas.enabled)
                canvas.enabled = false;
        }

        public static void Show(Canvas canvas, string reason)
        {
            if (canvas == null || !Hidden.TryGetValue(canvas, out State state))
                return;
            state.Reasons.Remove(reason);
            if (state.Reasons.Count > 0)
                return;
            canvas.enabled = state.Original;
            Hidden.Remove(canvas);
        }

        // Shows every canvas this reason hid, except the ones in keep.
        public static void ShowAllExcept(string reason, HashSet<Canvas> keepHidden)
        {
            var release = new List<Canvas>();
            foreach (var pair in Hidden)
                if (pair.Value.Reasons.Contains(reason) && (pair.Key == null || keepHidden == null || !keepHidden.Contains(pair.Key)))
                    release.Add(pair.Key);
            foreach (Canvas canvas in release)
            {
                if (canvas == null)
                {
                    Hidden.Remove(canvas);
                    continue;
                }
                Show(canvas, reason);
            }
        }
    }
}
