using UnityEngine;
using VWW.Clients.Curio.Scene.Links;
using FlyMod.Core;

namespace FlyMod.Features
{
    // Drives world-space position directly rather than through the game's
    // own movement fields - AxisU turned out to be camera-relative swim
    // direction, not pure vertical, and root-motion walk animation didn't
    // respond to speed fields either. m_InWater is set to true for the
    // duration of a flight so the avatar is in swim state, which is what
    // stops this fighting normal ground collision the way a raw position
    // write did before swim state was involved.
    internal class FlyController
    {
        private readonly PlayerContext _playerContext;

        public bool Flying { get; private set; }
        public float Speed = 6f;

        private bool? _wasInWaterBeforeFlying;
        private Rigidbody _avatarRigidbody;
        private Vector3 _desiredPosition;

        public FlyController(PlayerContext playerContext)
        {
            _playerContext = playerContext;
        }

        public void Toggle() => SetFlying(!Flying);

        public void SetFlying(bool enable)
        {
            if (Flying == enable)
                return;
            Flying = enable;
            if (_playerContext.Avatar == null)
                return;

            if (_avatarRigidbody == null)
                _avatarRigidbody = _playerContext.Avatar.GetComponentInChildren<Rigidbody>();

            if (enable)
                EnterFlightState();
            else
                ExitFlightState();
        }

        private void EnterFlightState()
        {
            Transform avatarTransform = _playerContext.Avatar.transform;
            _desiredPosition = avatarTransform.position;

            _wasInWaterBeforeFlying = (bool)Reflect.InWaterField.GetValue(_playerContext.Avatar);
            Reflect.InWaterField.SetValue(_playerContext.Avatar, true);

            if (_avatarRigidbody != null)
                _avatarRigidbody.useGravity = false;
        }

        private void ExitFlightState()
        {
            Reflect.ZeroVerticalVelocity();

            if (_wasInWaterBeforeFlying.HasValue)
                Reflect.InWaterField.SetValue(_playerContext.Avatar, _wasInWaterBeforeFlying.Value);
            _wasInWaterBeforeFlying = null;

            // The game owns gravity: it turns it on whenever the avatar is
            // off the ground and out of water (DOMAnimLink.UpdateGravity).
            // Restoring the value from take-off (off, since the avatar was
            // standing then) left it floating with no gravity after landing,
            // stuck in the falling state. Let the game work it out again.
            if (DOMAnimLink.Self != null)
                DOMAnimLink.Self.UpdateGravity();
            else if (_avatarRigidbody != null)
                _avatarRigidbody.useGravity = true;

            bool inWaterAfterRestore = (bool)Reflect.InWaterField.GetValue(_playerContext.Avatar);
            DebugLog.Info("Fly off: m_InWater after restore = " + inWaterAfterRestore +
                (_avatarRigidbody != null ? ", rigidbody.useGravity = " + _avatarRigidbody.useGravity : ", no rigidbody"));

            DropToGroundBelow();
        }

        // Whatever pulls the avatar down normally didn't resume on its own
        // after being frozen mid-air - so land it ourselves: drop straight
        // down onto the first surface below.
        private void DropToGroundBelow()
        {
            Transform avatarTransform = _playerContext.Avatar.transform;
            bool foundGround = Physics.Raycast(avatarTransform.position, Vector3.down, out RaycastHit groundHit, 5000f);
            DebugLog.Info("Fly off: ground raycast from " + avatarTransform.position +
                (foundGround ? " hit " + groundHit.point + " on " + groundHit.collider.name : " found nothing"));

            if (foundGround)
                avatarTransform.position = groundHit.point + Vector3.up * 0.05f;
        }

        public void Unstick()
        {
            if (_playerContext.Avatar == null)
                return;
            Reflect.ZeroVerticalVelocity();
            _playerContext.Avatar.transform.position += Vector3.up * 3f;
        }

        public void Tick(bool typingInChat, KeyCode upKey, KeyCode downKey)
        {
            if (!Flying || _playerContext.Avatar == null)
                return;

            CancelAmbientFallingVelocity();

            Vector3 movementThisFrame = typingInChat
                ? Vector3.zero
                : ReadFlightMovementInput(upKey, downKey);

            if (movementThisFrame != Vector3.zero)
                _desiredPosition += movementThisFrame.normalized * Speed * Time.deltaTime;

            // Re-assert every frame, moving or not - our position is simply
            // the last word each frame while flying is on.
            _playerContext.Avatar.transform.position = _desiredPosition;
        }

        private void CancelAmbientFallingVelocity()
        {
            Reflect.ZeroVerticalVelocity();
            // The game sometimes makes the avatar kinematic; Unity ignores a
            // velocity write then and logs a warning for every frame of it.
            if (_avatarRigidbody != null && !_avatarRigidbody.isKinematic)
                _avatarRigidbody.linearVelocity = Vector3.zero;
        }

        private static Vector3 ReadFlightMovementInput(KeyCode upKey, KeyCode downKey)
        {
            float verticalDirection = 0f;
            if (Input.GetKey(upKey)) verticalDirection = 1f;
            else if (Input.GetKey(downKey)) verticalDirection = -1f;
            Vector3 movement = Vector3.up * verticalDirection;

            Camera activeCamera = Camera.main;
            if (activeCamera != null)
                movement += ReadCameraRelativeHorizontalInput(activeCamera);

            return movement;
        }

        private static Vector3 ReadCameraRelativeHorizontalInput(Camera activeCamera)
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
    }
}
