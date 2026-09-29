using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VWW.Clients.Curio.Avatar;
using VWW.Clients.Curio.Scene.Links;
using VWW.CoreLibs.DOM;
using FlyMod.Core;

namespace FlyMod.Features
{
    // Measured live: a snowball / thrown-ball hit is not a teleport or a
    // physics collision. The avatar slides over several frames (~4.5 u/s,
    // rigidbody velocity non-zero, root motion on) while the player presses
    // nothing. Earlier attempts watched for one-frame jumps and for server
    // position updates and saw neither.
    //
    // So immunity works from the player's intent instead of the cause:
    // while there is no movement input, the avatar's horizontal position is
    // held. Instant jumps (teleports) re-anchor instead of being fought.
    internal class KnockbackImmunityController
    {
        public bool Enabled
        {
            get => KnockbackImmunityPatch.Enabled;
            set
            {
                if (value != KnockbackImmunityPatch.Enabled)
                    DebugLog.Info("Knockback immunity " + (value ? "ON" : "OFF"));
                KnockbackImmunityPatch.Enabled = value;
            }
        }

        // The guard lives on the GameObject that owns the avatar's Rigidbody,
        // so it runs in the same physics step that moves it.
        public void Tick(bool weAreDrivingMovementOurselves)
        {
            KnockbackIdleAnchor.OurMovementActive = weAreDrivingMovementOurselves;

            Rigidbody avatarRigidbody = null;
            try
            {
                avatarRigidbody = AvControl.AvatarRigidbody;
            }
            catch
            {
                // avatar not spawned yet
            }
            if (avatarRigidbody != null && avatarRigidbody.GetComponent<KnockbackIdleAnchor>() == null)
                avatarRigidbody.gameObject.AddComponent<KnockbackIdleAnchor>();
        }
    }

    internal class KnockbackIdleAnchor : MonoBehaviour
    {
        public static bool OurMovementActive;

        // Anything bigger in one step is a teleport (portal, waypoint,
        // unstick), not a shove.
        private const float TeleportDistance = 1.5f;
        private const float HoldTolerance = 0.02f;
        // Walking decelerates for a moment after the keys are released.
        private const float GraceAfterInputSeconds = 0.4f;

        private static readonly FieldInfo AxisHField = AccessTools.Field(typeof(AvControl), "AxisH");
        private static readonly FieldInfo AxisVField = AccessTools.Field(typeof(AvControl), "AxisV");
        private static readonly FieldInfo AxisUField = AccessTools.Field(typeof(AvControl), "AxisU");

        private Rigidbody _rigidbody;
        private Vector3 _anchor;
        private float _lastInputTime;
        private float _nextLogTime;
        private string _lastSkipReason = "";
        private float _driftWindowEnd;
        private Vector3 _driftWindowStart;

        private void Awake()
        {
            _rigidbody = GetComponent<Rigidbody>();
            _anchor = _rigidbody.position;
        }

        private void FixedUpdate()
        {
            if (!ShouldHold())
            {
                _anchor = _rigidbody.position;
                return;
            }

            Vector3 position = _rigidbody.position;
            Vector3 horizontalOffset = new Vector3(position.x - _anchor.x, 0f, position.z - _anchor.z);
            if (horizontalOffset.magnitude > TeleportDistance)
            {
                _anchor = position;
                return;
            }
            if (horizontalOffset.magnitude < HoldTolerance)
                return;

            _rigidbody.position = new Vector3(_anchor.x, position.y, _anchor.z);
            Vector3 velocity = _rigidbody.linearVelocity;
            _rigidbody.linearVelocity = new Vector3(0f, velocity.y, 0f);

            if (horizontalOffset.magnitude > 0.1f && Time.unscaledTime >= _nextLogTime)
            {
                _nextLogTime = Time.unscaledTime + 1f;
                DebugLog.Info("Knockback immunity: held position against a push of " +
                    horizontalOffset.magnitude.ToString("0.00") + " units");
            }
        }

        private bool ShouldHold()
        {
            string skipReason = SkipReason();
            if (skipReason != _lastSkipReason)
            {
                // Temporary diagnostic: which state the avatar is in when a hit lands.
                DebugLog.Info("Knockback immunity state: " + (skipReason.Length == 0 ? "holding" : "not holding - " + skipReason));
                _lastSkipReason = skipReason;
            }
            return skipReason.Length == 0;
        }

        private string SkipReason()
        {
            if (!KnockbackImmunityPatch.Enabled)
                return "off";
            if (OurMovementActive)
                return "flying";
            if (_rigidbody.isKinematic)
                return "rigidbody kinematic";

            AvControl avatar = AvControl.Self;
            if (avatar == null)
                return "no avatar";
            // Sitting, emotes and scripted poses place the avatar on purpose.
            if (avatar.PositionLocked)
                return "position locked";

            if (HasMovementInput(avatar))
                _lastInputTime = Time.time;
            if (Time.time - _lastInputTime <= GraceAfterInputSeconds)
                return "movement input";
            return "";
        }

        // Temporary diagnostic: any real displacement, whatever the state.
        private void LateUpdate()
        {
            if (Time.unscaledTime < _driftWindowEnd)
                return;
            Vector3 position = transform.position;
            float drift = new Vector3(position.x - _driftWindowStart.x, 0f, position.z - _driftWindowStart.z).magnitude;
            // Only moves that could be a push matter here: flying, walking
            // and immunity being off are expected movement, not worth a line.
            bool expectedMovement = _lastSkipReason == "flying" || _lastSkipReason == "off" || _lastSkipReason == "movement input";
            if (drift > 0.5f && !expectedMovement)
                DebugLog.Info("Knockback immunity: moved " + drift.ToString("0.00") + " units in 0.25s (state: " +
                    (_lastSkipReason.Length == 0 ? "holding" : _lastSkipReason) + ", velocity " +
                    _rigidbody.linearVelocity.magnitude.ToString("0.00") + ", rigidbody at " + _rigidbody.position + ", transform at " + position + ")");
            _driftWindowStart = position;
            _driftWindowEnd = Time.unscaledTime + 0.25f;
        }

        private static bool HasMovementInput(AvControl avatar)
        {
            if (AxisHField == null || AxisVField == null || AxisUField == null)
                return true; // can't tell - never fight the player
            return (float)AxisHField.GetValue(avatar) != 0f
                || (float)AxisVField.GetValue(avatar) != 0f
                || (float)AxisUField.GetValue(avatar) != 0f;
        }
    }

    // Second layer: a server script can also move our avatar by sending it a
    // new "Position", which DOMTransformLink.ApplyUpdate applies instantly
    // (the player avatar never interpolates). Small moves are undone;
    // larger ones are teleports and are allowed.
    [HarmonyPatch(typeof(DOMTransformLink), "ApplyUpdate")]
    internal static class KnockbackImmunityPatch
    {
        public static bool Enabled;

        private const float MaxKnockbackDistance = 10f;
        private const float MinKnockbackDistance = 0.05f;

        private static Vector3 _positionBeforeUpdate;
        private static bool _guardingThisUpdate;

        private static void Prefix(DOMTransformLink __instance, DOMPropertyItem update)
        {
            _guardingThisUpdate = false;
            if (!Enabled || update == null || update.Name != "Position" || !update.Sender.HasValue)
                return;
            if (!(__instance is DOMControllerLink controllerLink) || !controllerLink.IsPlayerAvatar)
                return;

            AvControl avatar = AvControl.Self;
            if (avatar == null || avatar.PositionLocked)
                return;

            _positionBeforeUpdate = avatar.transform.position;
            _guardingThisUpdate = true;
        }

        private static void Postfix()
        {
            if (!_guardingThisUpdate)
                return;
            _guardingThisUpdate = false;

            AvControl avatar = AvControl.Self;
            if (avatar == null)
                return;

            float distanceMoved = Vector3.Distance(_positionBeforeUpdate, avatar.transform.position);
            if (distanceMoved < MinKnockbackDistance || distanceMoved > MaxKnockbackDistance)
                return;

            DebugLog.Info("Knockback immunity: blocked server push of " + distanceMoved.ToString("0.00") + " units");
            Rigidbody avatarRigidbody = null;
            try
            {
                avatarRigidbody = AvControl.AvatarRigidbody;
            }
            catch
            {
                // helper not ready - fall through to the transform write
            }
            if (avatarRigidbody != null)
                avatarRigidbody.position = _positionBeforeUpdate;
            avatar.transform.position = _positionBeforeUpdate;
        }
    }
}
