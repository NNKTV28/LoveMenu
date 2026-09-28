namespace FlyMod.Features
{
    // Thin wrapper over BodyRotationLockPatch's static toggle - the actual
    // enforcement happens in a Harmony patch on AvControl.InternalFixedUpdate,
    // the real place right-click-drag body rotation comes from.
    internal class BodyRotationLockController
    {
        public bool Enabled { get; private set; }

        public void SetEnabled(bool enable)
        {
            Enabled = enable;
            BodyRotationLockPatch.Enabled = enable;
        }
    }
}
