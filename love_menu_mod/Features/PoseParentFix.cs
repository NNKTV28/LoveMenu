using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using VWW.Clients.Curio.ClientUI;
using VWW.Clients.Curio.Scene.Links;
using VWW.Clients.Curio.Scene.Loaders;
using VWW.CoreLibs.DOM;
using FlyMod.Core;

namespace FlyMod.Features
{
    // "Poses and couple dances look like the players are inside each other -
    // only when you join a room where people are already posing."
    //
    // A posing avatar is attached to its partner or to the pose point (its
    // TransformParentID), and its position is stored relative to that. When
    // you join, the room loads object by object. If an avatar loads before
    // what it's attached to, the game can't find the parent
    // (DOMTransformLoader.FindParent returns nothing, or
    // DOMTransformLink.UpdateTransformParent logs "Failed to find transform
    // parent" and gives up), places it with its pose offset relative to the
    // wrong thing, and never tries again. People who were already in the
    // room had everything loaded when the pose started, so they see it right.
    //
    // This remembers every object whose transform parent was missing and,
    // as soon as that parent has loaded, attaches it and puts back its
    // position and rotation relative to it.
    internal static class PoseParentFix
    {
        public static bool Enabled;
        public static int Repaired { get; private set; }

        private class Pending
        {
            public ViewLoader View;
            public DOMTransform Object;
            public long ParentId;
            public float Since;
        }

        private const float GiveUpSeconds = 90f;
        private static readonly List<Pending> Waiting = new List<Pending>();
        private static float _nextCheck;

        public static void Remember(ViewLoader view, DOMTransform obj)
        {
            if (!Enabled || view == null || obj == null || obj.TransformParentID == 0L)
                return;
            foreach (Pending pending in Waiting)
                if (pending.Object == obj)
                {
                    pending.ParentId = obj.TransformParentID;
                    return;
                }
            Waiting.Add(new Pending { View = view, Object = obj, ParentId = obj.TransformParentID, Since = Time.unscaledTime });
            DebugLog.Detail("Pose load fix: '" + obj.Title + "' is waiting for its transform parent " + obj.TransformParentID);
        }

        public static void Tick()
        {
            if (Waiting.Count == 0 || Time.unscaledTime < _nextCheck)
                return;
            _nextCheck = Time.unscaledTime + 0.25f;
            for (int i = Waiting.Count - 1; i >= 0; i--)
            {
                Pending pending = Waiting[i];
                try
                {
                    if (!Enabled || pending.View == null || pending.Object.TransformParentID != pending.ParentId ||
                        Time.unscaledTime - pending.Since > GiveUpSeconds)
                    {
                        Waiting.RemoveAt(i);        // the pose ended, changed, or never arrived
                        continue;
                    }
                    GameObject parent = pending.View.FindObject(pending.ParentId);
                    GameObject child = pending.View.FindObject(pending.Object.ID);
                    if (parent == null || child == null)
                        continue;
                    DOMTransformLink link = child.GetComponent<DOMTransformLink>();
                    Transform pivot = link != null && link.PivotTransform != null ? link.PivotTransform : child.transform;
                    if (pivot.parent != parent.transform)
                        pivot.SetParent(parent.transform, worldPositionStays: false);
                    pivot.localPosition = Conversion.FromVec3(pending.Object.Position);
                    pivot.localEulerAngles = Conversion.FromVec3(pending.Object.Rotation);
                    Repaired++;
                    DebugLog.Info("Pose load fix: attached '" + pending.Object.Title + "' to its pose parent '" +
                        TeleportController.CleanObjectName(parent.name) + "'");
                    Waiting.RemoveAt(i);
                }
                catch (Exception exception)
                {
                    DebugLog.Detail("Pose load fix failed for one object: " + exception.Message);
                    Waiting.RemoveAt(i);
                }
            }
        }
    }

    // Loading: the parent wasn't there yet.
    [HarmonyPatch(typeof(DOMTransformLoader), nameof(DOMTransformLoader.FindParent))]
    internal static class PoseParentFindPatch
    {
        static void Postfix(DOMTransformLoader __instance, Transform __result)
        {
            if (!PoseParentFix.Enabled || __result != null)
                return;
            DOMTransform obj = __instance.LoadingObject;
            if (obj != null && obj.TransformParentID != 0L && string.IsNullOrEmpty(obj.AttachToBone))
                PoseParentFix.Remember(__instance.ViewLoader, obj);
        }
    }

    // A pose starting (or changing) while its parent isn't loaded yet.
    [HarmonyPatch(typeof(DOMTransformLink), "UpdateTransformParent")]
    internal static class PoseParentUpdatePatch
    {
        static void Postfix(DOMTransformLink __instance, long transformParentID)
        {
            if (!PoseParentFix.Enabled || transformParentID == 0L || __instance.ViewLoader == null)
                return;
            if (__instance.ViewLoader.FindObject(transformParentID) == null)
                PoseParentFix.Remember(__instance.ViewLoader, __instance.LinkedObject);
        }
    }
}
