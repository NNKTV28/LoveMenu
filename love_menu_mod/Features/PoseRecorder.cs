using System;
using HarmonyLib;
using UnityEngine;
using VWW.Clients.Curio.Scene.Links;
using FlyMod.Core;

namespace FlyMod.Features
{
    // For the "dancing inside each other" report. Poses and couple dances
    // aren't a world script: the server attaches your avatar to the pose
    // (DOMController.TransformParentID) and your game places you from it.
    // This writes a line to the log whenever your avatar is attached or
    // released, and every second while attached: where you are, where the
    // pose point is, and the nearest other avatar and how far away - so
    // "inside each other" shows up as numbers.
    internal class PoseRecorder
    {
        public static readonly PoseRecorder Instance = new PoseRecorder();

        public bool Enabled;
        public string LastLine = "";

        private long _lastParent = -1;
        private float _nextSample;

        public void Tick(PlayerContext player)
        {
            if (!Enabled || player.Avatar == null || Time.unscaledTime < _nextSample)
                return;
            _nextSample = Time.unscaledTime + 1f;
            try
            {
                DOMControllerLink me = player.Avatar.GetComponentInParent<DOMControllerLink>() ?? player.Avatar.GetComponentInChildren<DOMControllerLink>();
                object controller = me?.LinkedObject;
                if (controller == null)
                    return;
                var traverse = Traverse.Create(controller);
                long parent = Convert.ToInt64(traverse.Property("TransformParentID").GetValue() ?? 0L);
                object parentObject = traverse.Property("TransformParent").GetValue();
                string parentTitle = parentObject != null ? Traverse.Create(parentObject).Property("Title").GetValue() as string ?? "?" : "none";

                Vector3 mine = player.Avatar.transform.position;
                string nearest = NearestAvatar(mine, me);
                string what = parent != _lastParent
                    ? (parent == 0 ? "left pose" : "entered pose")
                    : parent == 0 ? null : "in pose";
                _lastParent = parent;
                if (what == null)
                    return;
                LastLine = "[pose] " + what + " | attached to " + parent + " '" + parentTitle + "' | me at " + Format(mine) +
                    " | local " + Format(player.Avatar.transform.localPosition) + " | " + nearest;
                DebugLog.Info(LastLine);
            }
            catch (Exception exception)
            {
                DebugLog.Detail("Pose recorder failed: " + exception.Message);
            }
        }

        private static string NearestAvatar(Vector3 mine, DOMControllerLink me)
        {
            DOMControllerLink best = null;
            float bestDistance = float.MaxValue;
            foreach (DOMControllerLink other in UnityEngine.Object.FindObjectsOfType<DOMControllerLink>())
            {
                if (other == null || other == me || !other.IsPersona)
                    continue;
                float distance = Vector3.Distance(other.transform.position, mine);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = other;
                }
            }
            if (best == null)
                return "nobody near";
            return "nearest avatar '" + TeleportController.NpcName(best) + "' at " + Format(best.transform.position) +
                ", " + bestDistance.ToString("0.00") + " m away";
        }

        private static string Format(Vector3 v) => "(" + v.x.ToString("0.00") + ", " + v.y.ToString("0.00") + ", " + v.z.ToString("0.00") + ")";
    }
}
