using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VWW.Clients.Curio.Avatar;
using FlyMod.Core;

namespace FlyMod.Features
{
    // AvControl.InternalFixedUpdate turns the avatar to face the camera
    // whenever right-click is held with no WASD input. Captures the rotation
    // before the tick and restores it after, but only when locked and not
    // actually walking (AxisH/AxisV both zero), so normal WASD-direction
    // turning is untouched.
    //
    // Both the transform and the Rigidbody are restored: the avatar is
    // physics-driven, and putting back only the transform left the body
    // turned - Rigidbody interpolation then rotated the visible avatar to
    // match on the next frame, so the lock appeared to do nothing.
    [HarmonyPatch(typeof(AvControl), nameof(AvControl.InternalFixedUpdate))]
    internal static class BodyRotationLockPatch
    {
        public static bool Enabled;

        private static readonly FieldInfo AxisHField = AccessTools.Field(typeof(AvControl), "AxisH");
        private static readonly FieldInfo AxisVField = AccessTools.Field(typeof(AvControl), "AxisV");

        private static Quaternion _transformRotationBefore;
        private static Quaternion _bodyRotationBefore;
        private static Rigidbody _body;
        private static bool _shouldRestoreThisTick;
        private static float _nextLogTime;

        private static void Prefix(AvControl __instance)
        {
            _shouldRestoreThisTick = false;
            if (!Enabled || __instance.PositionLocked)
                return;

            float axisH = (float)AxisHField.GetValue(__instance);
            float axisV = (float)AxisVField.GetValue(__instance);
            if (axisH != 0f || axisV != 0f)
                return; // actively walking - let normal movement-facing rotation happen

            _body = null;
            try { _body = AvControl.AvatarRigidbody; } catch { }
            _transformRotationBefore = __instance.transform.rotation;
            _bodyRotationBefore = _body != null ? _body.rotation : _transformRotationBefore;
            _shouldRestoreThisTick = true;
        }

        private static void Postfix(AvControl __instance)
        {
            if (!_shouldRestoreThisTick)
                return;

            float turned = Quaternion.Angle(__instance.transform.rotation, _transformRotationBefore);
            if (_body != null)
                turned = Mathf.Max(turned, Quaternion.Angle(_body.rotation, _bodyRotationBefore));

            __instance.transform.rotation = _transformRotationBefore;
            if (_body != null)
                _body.rotation = _bodyRotationBefore;

            if (turned > 0.5f && Time.unscaledTime >= _nextLogTime)
            {
                _nextLogTime = Time.unscaledTime + 1f;
                DebugLog.Detail("Body rotation lock: undid a " + turned.ToString("0.0") + "° turn (avatar object '" +
                    __instance.gameObject.name + "', body object '" + (_body != null ? _body.gameObject.name : "none") +
                    "', interpolation " + (_body != null ? _body.interpolation.ToString() : "-") + ")");
            }
        }
    }
}
