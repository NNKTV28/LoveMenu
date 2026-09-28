using HarmonyLib;
using VWW.CoreLibs.Shared;

namespace FlyMod.Features
{
    // ViewManager.UpdateView logs "Missing object: <id>" / "Missing parent:
    // <id> => ..." via DebugEvent.WriteError every time a DOMUpdate packet
    // references an object the client already despawned locally - a routine
    // race in busy rooms. The method already recovers on its own (it just
    // `continue`s to the next update either way), so the log call is pure
    // overhead: DebugEvent.WriteError builds a StackFrame via reflection and
    // raises an event to every log sink, which is genuinely expensive when
    // it fires repeatedly inside a tight per-property loop in a crowded
    // room. Suppresses only these two known-benign, self-recovering
    // messages; every other WriteError call is untouched.
    [HarmonyPatch(typeof(DebugEvent), nameof(DebugEvent.WriteError), typeof(string), typeof(object[]))]
    internal static class ViewManagerNoiseGuardPatch
    {
        private static bool Prefix(string message)
        {
            if (message == null)
                return true;
            if (message.StartsWith("Missing object:") || message.StartsWith("Missing parent:"))
                return false;
            return true;
        }
    }
}
