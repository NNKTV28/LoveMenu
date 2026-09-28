using System;
using System.Collections.Generic;
using UnityEngine;

namespace FlyMod.Features
{
    // Detaches the camera for free-fly spectating; never touches the
    // avatar. Disables whatever MonoBehaviour scripts were driving the
    // camera (found by walking up from Camera.main and matching on type
    // name containing "Camera") and restores them on exit.
    internal class FreeCamController
    {
        public bool Active { get; private set; }

        private Vector3 _cameraPosition;
        private float _cameraYaw, _cameraPitch;

        // Where free cam started this activation, so Reset() can snap back
        // to it without needing to toggle off and back on.
        private Vector3 _startPosition;
        private float _startYaw, _startPitch;

        private readonly List<Behaviour> _disabledCameraBehaviours = new List<Behaviour>();

        public void Toggle() => SetActive(!Active);

        public void SetActive(bool enable)
        {
            if (Active == enable)
                return;
            Active = enable;

            Camera mainCamera = Camera.main;
            if (mainCamera == null)
                return;

            if (enable)
                EnterFreeCam(mainCamera);
            else
                ExitFreeCam();
        }

        private void EnterFreeCam(Camera mainCamera)
        {
            _cameraPosition = mainCamera.transform.position;
            _cameraYaw = mainCamera.transform.eulerAngles.y;
            _cameraPitch = mainCamera.transform.eulerAngles.x;

            _startPosition = _cameraPosition;
            _startYaw = _cameraYaw;
            _startPitch = _cameraPitch;

            _disabledCameraBehaviours.Clear();
            Transform currentTransform = mainCamera.transform;
            for (int parentDepth = 0; parentDepth < 3 && currentTransform != null; parentDepth++, currentTransform = currentTransform.parent)
                DisableCameraDrivingScriptsOn(currentTransform);
        }

        private void DisableCameraDrivingScriptsOn(Transform onTransform)
        {
            foreach (Behaviour behaviour in onTransform.GetComponents<Behaviour>())
            {
                if (behaviour is Camera) continue;
                if (behaviour.GetType().Name.IndexOf("Camera", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!behaviour.enabled) continue;
                behaviour.enabled = false;
                _disabledCameraBehaviours.Add(behaviour);
            }
        }

        private void ExitFreeCam()
        {
            foreach (Behaviour behaviour in _disabledCameraBehaviours)
                if (behaviour != null) behaviour.enabled = true;
            _disabledCameraBehaviours.Clear();
        }

        public void ResetToStart()
        {
            if (!Active)
                return;
            _cameraPosition = _startPosition;
            _cameraYaw = _startYaw;
            _cameraPitch = _startPitch;
        }

        public void Tick(KeyCode upKey, KeyCode downKey, float speed)
        {
            if (!Active)
                return;
            Camera mainCamera = Camera.main;
            if (mainCamera == null)
                return;

            Quaternion lookRotation = ApplyMouseLook();
            Vector3 movement = ReadFreeCamMovementInput(upKey, downKey, lookRotation);
            if (movement != Vector3.zero)
                _cameraPosition += movement.normalized * speed * Time.deltaTime;

            mainCamera.transform.SetPositionAndRotation(_cameraPosition, lookRotation);
        }

        private Quaternion ApplyMouseLook()
        {
            _cameraYaw += Input.GetAxis("Mouse X") * 3f;
            _cameraPitch -= Input.GetAxis("Mouse Y") * 3f;
            _cameraPitch = Mathf.Clamp(_cameraPitch, -89f, 89f);
            return Quaternion.Euler(_cameraPitch, _cameraYaw, 0f);
        }

        private static Vector3 ReadFreeCamMovementInput(KeyCode upKey, KeyCode downKey, Quaternion lookRotation)
        {
            float forwardInput = 0f, strafeInput = 0f, verticalInput = 0f;
            if (Input.GetKey(KeyCode.W)) forwardInput += 1f;
            if (Input.GetKey(KeyCode.S)) forwardInput -= 1f;
            if (Input.GetKey(KeyCode.D)) strafeInput += 1f;
            if (Input.GetKey(KeyCode.A)) strafeInput -= 1f;
            if (Input.GetKey(upKey)) verticalInput += 1f;
            if (Input.GetKey(downKey)) verticalInput -= 1f;

            return lookRotation * new Vector3(strafeInput, 0f, forwardInput) + Vector3.up * verticalInput;
        }
    }
}
