using UnityEngine;
using VWW.Clients.Curio.Avatar;
using FlyMod.Core;

namespace FlyMod.Features
{
    // Other players' snowballs (and possibly other effects) knock the
    // avatar back via a direct position teleport, not a physics
    // impulse - confirmed live: Rigidbody velocity, MotorControl.MoveVector
    // and VerticalVel all read zero at the exact frame position jumped
    // several units. With no force channel to cancel, immunity works the
    // same way Fly does: notice the position changed more than a frame of
    // normal movement could explain, and simply set it back.
    //
    // PlayerContext.WasTeleportedByUsThisFrame distinguishes an external
    // knock-back from our own deliberate teleports (waypoints, friend
    // teleport, unstick, landing after fly) so this doesn't fight them.
    internal class KnockbackImmunityController
    {
        // Compared against speed (units/second), not raw per-frame distance
        // - a distance-only check inflates during any frame-rate hitch
        // (bigger Time.deltaTime = more distance for perfectly normal
        // movement), which was wrongly snapping the avatar back on lag
        // spikes. The real snowball knockback measured live was ~3.47 units
        // in a single ~1/60s frame - roughly 200 u/s, an order of magnitude
        // above anything legitimate movement (even 4x speed boost) reaches.
        private const float SuspiciousSpeedUnitsPerSecond = 45f;

        private readonly PlayerContext _playerContext;
        public bool Enabled;

        private Vector3? _lastKnownGoodPosition;
        private AvControlReference _lastSeenAvatar;

        // Wraps the avatar reference so we can detect "this is a different
        // avatar instance than last frame" (e.g. after a world change) and
        // reset our baseline instead of treating the new spawn point as a
        // knock-back.
        private struct AvControlReference
        {
            public readonly object Value;
            public AvControlReference(object value) => Value = value;
            public static bool operator ==(AvControlReference a, AvControlReference b) => Equals(a.Value, b.Value);
            public static bool operator !=(AvControlReference a, AvControlReference b) => !Equals(a.Value, b.Value);
            public override bool Equals(object obj) => obj is AvControlReference other && Equals(Value, other.Value);
            public override int GetHashCode() => Value?.GetHashCode() ?? 0;
        }

        public KnockbackImmunityController(PlayerContext playerContext)
        {
            _playerContext = playerContext;
        }

        public void Tick(bool weAreDrivingMovementOurselves)
        {
            if (_playerContext.Avatar == null)
                return;

            Transform avatarTransform = _playerContext.Avatar.transform;
            var currentAvatarRef = new AvControlReference(_playerContext.Avatar);

            if (AvatarChangedSinceLastFrame(currentAvatarRef) || !_lastKnownGoodPosition.HasValue)
            {
                ResetBaseline(currentAvatarRef, avatarTransform.position);
                return;
            }

            bool exemptThisFrame = weAreDrivingMovementOurselves || _playerContext.WasTeleportedByUsThisFrame;
            if (exemptThisFrame)
            {
                _lastKnownGoodPosition = avatarTransform.position;
                return;
            }

            RevertIfKnockedBack(avatarTransform);
        }

        private bool AvatarChangedSinceLastFrame(AvControlReference currentAvatarRef) => currentAvatarRef != _lastSeenAvatar;

        private void ResetBaseline(AvControlReference avatarRef, Vector3 currentPosition)
        {
            _lastSeenAvatar = avatarRef;
            _lastKnownGoodPosition = currentPosition;
        }

        private void RevertIfKnockedBack(Transform avatarTransform)
        {
            float distanceMoved = Vector3.Distance(_lastKnownGoodPosition.Value, avatarTransform.position);
            float speed = distanceMoved / Mathf.Max(Time.deltaTime, 0.0001f);
            bool looksLikeKnockback = speed > SuspiciousSpeedUnitsPerSecond;

            if (looksLikeKnockback && Enabled)
            {
                DebugLog.Info("Knockback immunity reverting: " + distanceMoved.ToString("0.00") +
                    " units in " + Time.deltaTime.ToString("0.000") + "s (" + speed.ToString("0") + " u/s)");
                MoveAvatarBackTo(avatarTransform, _lastKnownGoodPosition.Value);
            }
            else
            {
                _lastKnownGoodPosition = avatarTransform.position;
            }
        }

        // The avatar is driven by a Rigidbody, so writing transform.position
        // alone gets overwritten from the body's own pose on the next
        // physics step and the revert silently does nothing. Writing the
        // Rigidbody's position (and killing the velocity that's still
        // carrying the shove) is what actually sticks.
        private static void MoveAvatarBackTo(Transform avatarTransform, Vector3 position)
        {
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
            {
                avatarRigidbody.position = position;
                avatarRigidbody.linearVelocity = Vector3.zero;
            }
            avatarTransform.position = position;
        }
    }
}
