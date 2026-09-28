using HarmonyLib;
using UnityEngine;
using FlyMod.UI;

namespace FlyMod.Core
{
    // IMGUI doesn't consume input the rest of the game reads, so scrolling
    // over an open Love Menu window still reaches the camera's own zoom
    // script unless its Input polls are zeroed directly here.
    [HarmonyPatch(typeof(Input), nameof(Input.GetAxis), new[] { typeof(string) })]
    internal static class ScrollAxisBlockPatch
    {
        private static bool Prefix(string axisName, ref float __result)
        {
            if (axisName != "Mouse ScrollWheel" || !MenuUI.IsMouseOverOpenMenu)
                return true;
            __result = 0f;
            return false;
        }
    }

    [HarmonyPatch(typeof(Input), nameof(Input.GetAxisRaw), new[] { typeof(string) })]
    internal static class ScrollAxisRawBlockPatch
    {
        private static bool Prefix(string axisName, ref float __result)
        {
            if (axisName != "Mouse ScrollWheel" || !MenuUI.IsMouseOverOpenMenu)
                return true;
            __result = 0f;
            return false;
        }
    }

    [HarmonyPatch(typeof(Input), nameof(Input.mouseScrollDelta), MethodType.Getter)]
    internal static class ScrollDeltaBlockPatch
    {
        private static bool Prefix(ref Vector2 __result)
        {
            if (!MenuUI.IsMouseOverOpenMenu)
                return true;
            __result = Vector2.zero;
            return false;
        }
    }
}
