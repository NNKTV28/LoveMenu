using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VWW.Clients.Curio.Avatar;
using VWW.Clients.Curio.Scene.Links;

namespace FlyMod.Features
{
    // Stops right-click camera drag from turning the avatar. The patch on
    // AvControl.InternalFixedUpdate (BodyRotationLockPatch) undoes the turn
    // made there, but the avatar still ended up facing the camera - the
    // game rotates it along more than one path. So a guard on the avatar
    // itself remembers which way it faces and puts that back after every
    // physics step and every frame, unless the player is actually walking
    // (WASD turns the avatar on purpose), sitting, or being walked somewhere.
    internal class BodyRotationLockController
    {
        public bool Enabled { get; private set; }

        public void SetEnabled(bool enable)
        {
            Enabled = enable;
            BodyRotationLockPatch.Enabled = enable;
        }

        // The guard sits on the object AvControl turns (it writes its own
        // transform.rotation when the camera is dragged).
        public void Tick()
        {
            AvControl avatar = AvControl.Self;
            if (avatar != null && avatar.GetComponent<BodyRotationGuard>() == null)
                avatar.gameObject.AddComponent<BodyRotationGuard>();
        }
    }

    internal class BodyRotationGuard : MonoBehaviour
    {
        private static readonly FieldInfo AxisHField = AccessTools.Field(typeof(AvControl), "AxisH");
        private static readonly FieldInfo AxisVField = AccessTools.Field(typeof(AvControl), "AxisV");

        private Rigidbody _rigidbody;
        private Quaternion _heldRotation;
        private float _nextLogTime;

        private void Awake()
        {
            _heldRotation = transform.rotation;
        }

        private Rigidbody Body
        {
            get
            {
                if (_rigidbody == null)
                {
                    try { _rigidbody = AvControl.AvatarRigidbody; } catch { }
                }
                return _rigidbody;
            }
        }

        private void FixedUpdate() => Enforce();
        private void LateUpdate() => Enforce();

        private void Enforce()
        {
            if (!ShouldHold())
            {
                _heldRotation = transform.rotation;
                return;
            }
            if (Quaternion.Angle(transform.rotation, _heldRotation) < 0.01f)
                return;
            if (Time.unscaledTime >= _nextLogTime)
            {
                _nextLogTime = Time.unscaledTime + 1f;
                FlyMod.Core.DebugLog.Detail("Body rotation lock: turned " + Quaternion.Angle(transform.rotation, _heldRotation).ToString("0.0") +
                    "° outside the movement tick, putting it back");
            }
            transform.rotation = _heldRotation;
            Rigidbody body = Body;
            if (body != null && body.transform == transform)
                body.rotation = _heldRotation;
        }

        private bool ShouldHold()
        {
            if (!BodyRotationLockPatch.Enabled)
                return false;
            AvControl avatar = AvControl.Self;
            if (avatar == null || avatar.PositionLocked)
                return false;
            if (DOMAnimLink.Self != null && DOMAnimLink.Self.IsPathing)
                return false;
            if (AxisHField == null || AxisVField == null)
                return false;
            return (float)AxisHField.GetValue(avatar) == 0f && (float)AxisVField.GetValue(avatar) == 0f;
        }
    }
}
