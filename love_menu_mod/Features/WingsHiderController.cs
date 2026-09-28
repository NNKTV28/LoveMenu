using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace FlyMod.Features
{
    // Hides avatar wing attachments, which are some of the heaviest things
    // in a crowded room: multi-LOD skinned meshes, often with particles,
    // worn by many players at once.
    //
    // Names come from the player log rather than guesswork - it complains
    // about "Renderer 'Wings_LOD0' is registered with more than one
    // LODGroup ('[DOMRenderable: N] HF_BodyAttachment_FairyWings' and
    // 'WingsL')", so the real renderer names are Wings_LOD0/1/2, WingsL,
    // WingsR under a *BodyAttachment*Wings* parent.
    //
    // Only renderers are disabled, never objects destroyed, so toggling it
    // back off restores every one it touched.
    internal class WingsHiderController
    {
        private const float ScanIntervalSeconds = 2f;

        private readonly List<Renderer> _hiddenRenderers = new List<Renderer>();
        private readonly HashSet<string> _loggedNames = new HashSet<string>();
        private readonly ManualLogSource _log;
        private bool _enabled;
        private float _timeSinceLastScan;

        public WingsHiderController(ManualLogSource log)
        {
            _log = log;
        }

        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value)
                    return;
                _enabled = value;
                if (_enabled)
                    HideWingRenderers();
                else
                    RestoreHiddenRenderers();
            }
        }

        public int HiddenCount => _hiddenRenderers.Count;

        public void Tick(float deltaTime)
        {
            if (!_enabled)
                return;
            _timeSinceLastScan += deltaTime;
            if (_timeSinceLastScan < ScanIntervalSeconds)
                return;
            _timeSinceLastScan = 0f;
            HideWingRenderers(); // avatars stream in and out, so keep sweeping
        }

        private void HideWingRenderers()
        {
            int matched = 0;
            foreach (Renderer renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (renderer == null || !renderer.enabled || !IsWingRenderer(renderer))
                    continue;

                matched++;
                // First sighting of each distinct name goes to the log, so
                // what's actually being matched (or missed) is visible
                // instead of guessed at from screenshots.
                if (_loggedNames.Add(renderer.gameObject.name))
                    _log.LogInfo("[wings] hiding '" + renderer.gameObject.name + "' (" + renderer.GetType().Name +
                        ", parent '" + (renderer.transform.parent != null ? renderer.transform.parent.name : "none") + "')");

                renderer.enabled = false;
                _hiddenRenderers.Add(renderer);
            }

            if (matched == 0 && _loggedNames.Count == 0)
                LogWingCandidatesOnce();
        }

        // Nothing matched: dump every skinned renderer whose name looks
        // like an attachment so the real wing naming can be read off the
        // log rather than guessed at again.
        private void LogWingCandidatesOnce()
        {
            if (!_loggedNames.Add("__candidates_dumped__"))
                return;

            int logged = 0;
            foreach (SkinnedMeshRenderer renderer in UnityEngine.Object.FindObjectsOfType<SkinnedMeshRenderer>())
            {
                if (renderer == null || logged >= 60)
                    continue;
                string name = renderer.gameObject.name;
                string parentName = renderer.transform.parent != null ? renderer.transform.parent.name : "none";
                _log.LogInfo("[wings] candidate skinned renderer: '" + name + "' parent='" + parentName + "'");
                logged++;
            }
            _log.LogInfo("[wings] no wing match found - logged " + logged + " skinned renderer names above");
        }

        private void RestoreHiddenRenderers()
        {
            foreach (Renderer renderer in _hiddenRenderers)
                if (renderer != null)
                    renderer.enabled = true;
            _hiddenRenderers.Clear();
        }

        // "Wing" anywhere in the name, on the renderer itself or anywhere up
        // its parent chain. The earlier version only accepted names
        // starting with "Wings" or carrying "Attachment", which matched the
        // FairyWings in the log but missed everything else players wear.
        //
        // The false-positive guard is the renderer type rather than the
        // name: worn wings deform with the avatar, so they're
        // SkinnedMeshRenderer (or a particle effect parented to one). Solid
        // level geometry - a building's west wing - is a plain
        // MeshRenderer and is left alone.
        private static bool IsWingRenderer(Renderer renderer)
        {
            bool isAvatarAttachmentKind = renderer is SkinnedMeshRenderer || renderer is ParticleSystemRenderer;
            if (!isAvatarAttachmentKind)
                return false;

            if (ContainsWing(renderer.gameObject.name))
                return true;

            Transform current = renderer.transform.parent;
            for (int depth = 0; depth < 4 && current != null; depth++)
            {
                if (ContainsWing(current.name))
                    return true;
                current = current.parent;
            }
            return false;
        }

        private static bool ContainsWing(string name) =>
            !string.IsNullOrEmpty(name) && name.IndexOf("wing", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
