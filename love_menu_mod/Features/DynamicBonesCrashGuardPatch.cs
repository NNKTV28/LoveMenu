using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using VWW.Clients.Curio.Scene;

namespace FlyMod.Features
{
    // Experimental. Two independent real crashes (confirmed via Windows'
    // own Application Error log - same exact faulting offset in
    // ucrtbase.dll both times, STATUS_STACK_BUFFER_OVERRUN) each happened
    // immediately after this exact error:
    //   "Cannot deserialize the current JSON array ... into type
    //   DynamicBoneHelper+DynBone because the type requires a JSON object"
    // That is a correlation across 2 data points, not proven causation -
    // the crash itself is a native fault Harmony cannot see or patch
    // directly. What Harmony *can* do is stop the managed exception this
    // method throws from propagating into whatever native call happens
    // next, on the theory that's what leads to the abort. Off by default
    // since it is unproven; the Crashes tab is what turns it on.
    [HarmonyPatch(typeof(DynamicBoneHelper), nameof(DynamicBoneHelper.SetupDynamicBones),
        new[] { typeof(string), typeof(Transform), typeof(Guid?) })]
    internal static class DynamicBonesCrashGuardPatch
    {
        public static bool Enabled;
        private static ManualLogSource _log;

        public static void Init(ManualLogSource log)
        {
            _log = log;
        }

        // Harmony finalizer: runs after SetupDynamicBones whether or not it
        // threw. Returning null in place of the exception suppresses it
        // instead of letting it propagate to the caller.
        static Exception Finalizer(Exception __exception)
        {
            if (!Enabled || __exception == null)
                return __exception;

            _log?.LogWarning("DynamicBones crash guard caught and suppressed: " + __exception.Message);
            return null;
        }
    }
}
