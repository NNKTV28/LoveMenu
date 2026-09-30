using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

using FlyMod.Core;

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
            // Your own wings stay: this is about other people in a crowd, and
            // hiding yours made new wings "disappear" in the dressing room.
            // Anything already hidden that turns out to be yours comes back.
            for (int i = _hiddenRenderers.Count - 1; i >= 0; i--)
            {
                Renderer hidden = _hiddenRenderers[i];
                if (hidden == null)
                    _hiddenRenderers.RemoveAt(i);
                else if (IsMine(hidden))
                {
                    hidden.enabled = true;
                    _hiddenRenderers.RemoveAt(i);
                }
            }

            int matched = 0;
            foreach (Renderer renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (renderer == null || !renderer.enabled || !IsWingRenderer(renderer) || IsMine(renderer))
                    continue;

                matched++;
                // First sighting of each distinct name goes to the log, so
                // what's actually being matched (or missed) is visible
                // instead of guessed at from screenshots.
                if (_loggedNames.Add(renderer.gameObject.name))
                    DebugLog.Detail("[wings] hiding '" + renderer.gameObject.name + "' (" + renderer.GetType().Name +
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
                DebugLog.Detail("[wings] candidate skinned renderer: '" + name + "' parent='" + parentName + "'");
                logged++;
            }
            DebugLog.Detail("[wings] no wing match found - logged " + logged + " skinned renderer names above");
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

            // Look up to the outfit piece this renderer belongs to
            // ("[DOMRenderable: N] HF_BodyAttachment_FairyWings") and no
            // further: past it is the avatar, and a "wing" there matched
            // every piece - the log showed hair and shoes being hidden.
            Transform current = renderer.transform.parent;
            for (int depth = 0; depth < 4 && current != null; depth++)
            {
                if (ContainsWing(current.name))
                    return true;
                if (current.name.StartsWith("[DOM", StringComparison.Ordinal))
                    return false;
                current = current.parent;
            }
            return false;
        }

        // On your avatar, or in a preview (dressing room, shop), which the
        // game draws on its own "MiniDOM" layers.
        private static int _miniLayer = -2, _miniSkinLayer = -2;

        private static bool IsMine(Renderer renderer)
        {
            if (_miniLayer == -2)
            {
                _miniLayer = LayerMask.NameToLayer("MiniDOM");
                _miniSkinLayer = LayerMask.NameToLayer("MiniDOMSkin");
            }
            int layer = renderer.gameObject.layer;
            if (layer == _miniLayer || layer == _miniSkinLayer)
                return true;
            var owner = renderer.GetComponentInParent<VWW.Clients.Curio.Scene.Links.DOMControllerLink>();
            return owner != null && owner.IsPlayerAvatar;
        }

        private static bool ContainsWing(string name) =>
            !string.IsNullOrEmpty(name) && name.IndexOf("wing", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
