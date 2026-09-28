using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VWW.Clients.Curio.Avatar;

namespace FlyMod.Features
{
    // AvControl.InternalFixedUpdate sets transform.rotation directly to face
    // the camera whenever right-click is held with no WASD input - this is
    // unconditional and bypasses MotorControl.CharacterFollowsCamera
    // entirely, so toggling that field (the original approach) never had any
    // effect. Captures rotation before the tick and restores it after, but
    // only when locked and not actually walking (AxisH/AxisV both zero), so
    // normal WASD-direction turning is untouched.
    [HarmonyPatch(typeof(AvControl), nameof(AvControl.InternalFixedUpdate))]
    internal static class BodyRotationLockPatch
    {
        public static bool Enabled;

        private static readonly FieldInfo AxisHField = AccessTools.Field(typeof(AvControl), "AxisH");
        private static readonly FieldInfo AxisVField = AccessTools.Field(typeof(AvControl), "AxisV");

        private static Quaternion _rotationBeforeTick;
        private static bool _shouldRestoreThisTick;

        private static void Prefix(AvControl __instance)
        {
            _shouldRestoreThisTick = false;
            if (!Enabled)
                return;

            float axisH = (float)AxisHField.GetValue(__instance);
            float axisV = (float)AxisVField.GetValue(__instance);
            if (axisH != 0f || axisV != 0f)
                return; // actively walking - let normal movement-facing rotation happen

            _rotationBeforeTick = __instance.transform.rotation;
            _shouldRestoreThisTick = true;
        }

        private static void Postfix(AvControl __instance)
        {
            if (_shouldRestoreThisTick)
                __instance.transform.rotation = _rotationBeforeTick;
        }
    }
}
