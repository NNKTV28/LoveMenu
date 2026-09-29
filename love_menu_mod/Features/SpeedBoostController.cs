using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using FlyMod.Core;

namespace FlyMod.Features
{
    // Root-motion-driven walk animations don't respond to the MotorBase
    // speed fields (confirmed the field write lands, but ground distance
    // covered doesn't change) - so ground movement gets a direct
    // supplemental nudge in the walk direction, the same technique that
    // made fly actually move the avatar. The MotorBase fields are still
    // written too, in case some other system does read them.
    internal class SpeedBoostController
    {
        private readonly PlayerContext _playerContext;

        public bool Enabled { get; private set; }
        public float Multiplier = 1.5f;

        private const float GroundBoostBaseSpeed = 4f;
        private readonly Dictionary<string, float> _originalSpeedByFieldName = new Dictionary<string, float>();

        private static readonly string[] MotorSpeedFieldNames =
        {
            "RunForwardSpeed", "RunBackwardSpeed", "RunStrafingSpeed",
            "ForwardSpeed", "BackwardSpeed", "StrafingSpeed",
        };

        public SpeedBoostController(PlayerContext playerContext)
        {
            _playerContext = playerContext;
        }

        public void SetEnabled(bool enable)
        {
            Enabled = enable;
            ApplyMultiplier();
        }

        public void ApplyMultiplier()
        {
            object currentMotor = Reflect.GetCurrentMotor();
            if (currentMotor == null)
                return;

            Type motorFieldOwnerType = ResolveMotorBaseType(currentMotor);
            foreach (string fieldName in MotorSpeedFieldNames)
                ApplyMultiplierToField(currentMotor, motorFieldOwnerType, fieldName);
        }

        // Speed fields are declared on MotorBase, but the live object is a
        // subclass (e.g. MotorBiped) - GetType().BaseType gets us back to
        // the type that actually declares them.
        private static Type ResolveMotorBaseType(object motorInstance)
        {
            Type baseType = motorInstance.GetType().BaseType;
            return baseType != null && baseType.Name == "MotorBase" ? baseType : motorInstance.GetType();
        }

        private void ApplyMultiplierToField(object motorInstance, Type motorFieldOwnerType, string fieldName)
        {
            FieldInfo speedField = motorFieldOwnerType.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (speedField == null)
                return;

            if (!_originalSpeedByFieldName.ContainsKey(fieldName))
                _originalSpeedByFieldName[fieldName] = (float)speedField.GetValue(motorInstance);

            float baseSpeed = _originalSpeedByFieldName[fieldName];
            speedField.SetValue(motorInstance, Enabled ? baseSpeed * Multiplier : baseSpeed);
        }

        public void Tick(bool typingInChat, bool flying)
        {
            if (!Enabled || flying || typingInChat)
                return;
            if (_playerContext.Avatar == null || Camera.main == null)
                return;

            Vector3 movement = ReadGroundMovementInput(Camera.main);
            if (movement == Vector3.zero)
                return;

            float extraSpeed = GroundBoostBaseSpeed * (Multiplier - 1f);
            _playerContext.MoveAvatarBy(movement.normalized * extraSpeed * Time.deltaTime);
        }

        private static Vector3 ReadGroundMovementInput(Camera activeCamera)
        {
            Vector3 cameraForward = activeCamera.transform.forward; cameraForward.y = 0f; cameraForward.Normalize();
            Vector3 cameraRight = activeCamera.transform.right; cameraRight.y = 0f; cameraRight.Normalize();

            float forwardInput = 0f, strafeInput = 0f;
            if (Input.GetKey(KeyCode.W)) forwardInput += 1f;
            if (Input.GetKey(KeyCode.S)) forwardInput -= 1f;
            if (Input.GetKey(KeyCode.D)) strafeInput += 1f;
            if (Input.GetKey(KeyCode.A)) strafeInput -= 1f;

            return cameraForward * forwardInput + cameraRight * strafeInput;
        }

        // For the debug readout in the UI.
        public object DebugMotor() => Reflect.GetCurrentMotor();

        public float? DebugForwardSpeedLive()
        {
            object currentMotor = Reflect.GetCurrentMotor();
            FieldInfo forwardSpeedField = currentMotor?.GetType().BaseType?.GetField("ForwardSpeed", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return forwardSpeedField != null ? (float?)forwardSpeedField.GetValue(currentMotor) : null;
        }

        public float? DebugForwardSpeedBase() =>
            _originalSpeedByFieldName.TryGetValue("ForwardSpeed", out float cachedValue) ? (float?)cachedValue : null;
    }
}
