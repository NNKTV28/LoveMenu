using System.Reflection;
using VWW.Clients.Curio.Avatar;

namespace FlyMod.Core
{
    // Shared reflection handles for private fields/backing fields the game
    // doesn't expose publicly. Centralised so every feature reads the same
    // cached FieldInfo instead of re-resolving it by name everywhere.
    internal static class Reflect
    {
        public static readonly FieldInfo InWaterField =
            typeof(AvControl).GetField("m_InWater", BindingFlags.NonPublic | BindingFlags.Instance);

        public static readonly FieldInfo VerticalVelocityField =
            typeof(MotorControl).GetField("<VerticalVel>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo CurrentMotorField =
            typeof(MotorControl).GetField("<CurrentMotor>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);

        public static void ZeroVerticalVelocity()
        {
            if (MotorControl.Self != null)
                VerticalVelocityField?.SetValue(MotorControl.Self, 0f);
        }

        public static object GetCurrentMotor()
        {
            return MotorControl.Self == null ? null : CurrentMotorField?.GetValue(MotorControl.Self);
        }
    }
}
