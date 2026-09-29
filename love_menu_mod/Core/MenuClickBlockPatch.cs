using HarmonyLib;
using VWW.Clients.Curio;
using FlyMod.UI;

namespace FlyMod.Core
{
    // The game decides whether a click is "on UI" or "on the world" by
    // raycasting its own interface, which knows nothing about the Love Menu
    // (IMGUI). So pressing on the menu while walking counted as dragging the
    // world: the game steered the camera and moved the cursor, and the menu
    // window, following that cursor, flew into the screen corner. Reporting
    // "over UI" while the mouse is over the open menu keeps the game out of it,
    // the same way it ignores clicks on its own windows.
    [HarmonyPatch(typeof(UIRaycastManager), nameof(UIRaycastManager.IsOverUI), MethodType.Getter)]
    internal static class MenuClickBlockPatch
    {
        private static void Postfix(ref bool __result)
        {
            if (MenuUI.IsMouseOverOpenMenu)
                __result = true;
        }
    }
}
