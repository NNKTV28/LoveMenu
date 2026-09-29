using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using VWW.Clients.Curio.Scene;
using VWW.Clients.Curio.Scene.Loaders;
using FlyMod.Core;

namespace FlyMod.Features
{
    // Why people arriving makes the game stutter, found in its code: new
    // avatars, pets and objects are unpacked (ResourceRequestManager) and
    // then built (DOMLoader) on the main thread, each with a budget of
    // 100 ms per frame once you are in the room - two budgets, so up to
    // 200 ms, a frame rate of 5. Each also always finishes at least four
    // objects per frame, however long they take.
    //
    // With this on, both budgets drop to BudgetMs and one object per frame
    // is the minimum. Frames stay smooth; people take a little longer to
    // appear. The 500 ms budget behind the loading screen is left alone.
    internal static class LoadSmoothing
    {
        public static bool Enabled;
        public const double BudgetMs = 8.0;

        private const double GameRunBudgetMs = 100.0;
        private const int GameMinObjectsPerFrame = 3;

        // Called from the game's code in place of its "3".
        public static int MinObjectsPerFrame() => Enabled ? 0 : GameMinObjectsPerFrame;

        internal static void CapBudget(ref double budget)
        {
            if (Enabled && budget <= GameRunBudgetMs)
                budget = BudgetMs;
        }

        // Swaps the "3" in "objects loaded this frame > 3" for a call to
        // MinObjectsPerFrame.
        internal static IEnumerable<CodeInstruction> ReplaceMinObjects(IEnumerable<CodeInstruction> instructions, string counterField)
        {
            var list = new List<CodeInstruction>(instructions);
            MethodInfo minObjects = AccessTools.Method(typeof(LoadSmoothing), nameof(MinObjectsPerFrame));
            int replaced = 0;
            for (int i = 1; i < list.Count; i++)
            {
                if (list[i].opcode == OpCodes.Ldc_I4_3 && list[i - 1].operand is FieldInfo field && field.Name == counterField)
                {
                    list[i] = new CodeInstruction(OpCodes.Call, minObjects).MoveLabelsFrom(list[i]);
                    replaced++;
                }
            }
            if (replaced != 1)
                DebugLog.Warn("Smooth loading: expected one per-frame minimum on " + counterField + ", found " + replaced);
            return list;
        }
    }

    [HarmonyPatch(typeof(DOMLoader), "MaxMillisecondsToLoadFor", MethodType.Getter)]
    internal static class DOMLoaderBudgetPatch
    {
        static void Postfix(ref double __result) => LoadSmoothing.CapBudget(ref __result);
    }

    [HarmonyPatch(typeof(ResourceRequestManager), "MaxMillisecondsToLoadFor", MethodType.Getter)]
    internal static class ResourceRequestBudgetPatch
    {
        static void Postfix(ref double __result) => LoadSmoothing.CapBudget(ref __result);
    }

    [HarmonyPatch]
    internal static class DOMLoaderMinObjectsPatch
    {
        static MethodBase TargetMethod() =>
            AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(DOMLoader), nameof(DOMLoader.LoadObjectInternal)));

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            LoadSmoothing.ReplaceMinObjects(instructions, "ms_ObjectsLoadedThisFrame");
    }

    [HarmonyPatch]
    internal static class ResourceRequestMinObjectsPatch
    {
        static MethodBase TargetMethod() =>
            AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(ResourceRequestManager), "WaitForRequest"));

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            LoadSmoothing.ReplaceMinObjects(instructions, "m_ObjectsLoadedThisFrame");
    }
}
