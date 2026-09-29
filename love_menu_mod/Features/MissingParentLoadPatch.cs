using HarmonyLib;
using UnityEngine;
using VWW.Clients.Curio;
using FlyMod.Core;

namespace FlyMod.Features
{
    // Experimental. Found in the game's own Player.log: objects that attach
    // to a bone of another object (lights on an avatar or pet, "Light_Chest",
    // "Light_Tail") sometimes load before the object they attach to. The game
    // then calls DOMHelper.SearchForBone with no parent, which throws a
    // NullReferenceException, and the object is dropped:
    //   Loading failed for object "DOMLight 'Light_Chest'" -- AbortedLoad
    // With this on, the search returns "not found" instead, and the game
    // uses its own fallback ("Failed to find bone ... attaching to parent
    // instead"), so the object loads. It may sit at its parent rather than on
    // the bone. Off by default; the Crashes tab turns it on.
    [HarmonyPatch(typeof(DOMHelper), nameof(DOMHelper.SearchForBone))]
    internal static class MissingParentLoadPatch
    {
        public static bool Enabled;
        public static int Caught;

        static bool Prefix(Transform parentBone, string boneName, ref Transform __result)
        {
            if (!Enabled || parentBone != null)
                return true;
            Caught++;
            DebugLog.Detail("Missing-parent load fix: \"" + boneName + "\" had no parent yet, using the game's fallback");
            __result = null;
            return false;
        }
    }
}
