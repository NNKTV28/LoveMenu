using HarmonyLib;
using VWW.Clients.Curio.GUI;
using FlyMod.UI;

namespace FlyMod.Core
{
    // The game stops reading movement keys while one of its own text boxes
    // has focus (GUIManager.InputFieldActive, refreshed from CheckInputFocus
    // every frame). It can't see the Love Menu's text boxes, so typing a
    // search or a waypoint name walked the avatar around. Report "a text box
    // has focus" while the player is typing in the menu too.
    [HarmonyPatch(typeof(GUIManager), nameof(GUIManager.CheckInputFocus))]
    internal static class MenuTypingBlockPatch
    {
        private static void Postfix(ref bool __result)
        {
            if (MenuUI.TypingInMenu)
                __result = true;
        }
    }
}
