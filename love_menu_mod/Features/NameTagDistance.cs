using System.Collections.Generic;
using UnityEngine;
using VWW.Clients.Curio.Avatar;
using FlyMod.Core;

namespace FlyMod.Features
{
    // Name tags (name, clan, "Married to", badges) are UI drawn above every
    // avatar and NPC, rebuilt and drawn every frame - a lot of work in a
    // packed club. This hides the tags further than a chosen distance from
    // the camera, or all of them. Only the tags: the avatars stay.
    internal class NameTagDistance
    {
        public static readonly NameTagDistance Instance = new NameTagDistance();

        public static readonly string[] Names = { "All", "40 m", "25 m", "10 m", "None" };
        private static readonly float[] Distances = { float.PositiveInfinity, 40f, 25f, 10f, 0f };

        private const string Reason = "nametag";
        private const float ScanSeconds = 0.5f;

        private int _choice;
        private float _nextScan;
        private readonly List<(DOMTitleText Tag, Canvas[] Canvases)> _tags = new List<(DOMTitleText, Canvas[])>();
        private float _nextTagRefresh;

        public int Choice
        {
            get => _choice;
            set
            {
                _choice = Mathf.Clamp(value, 0, Names.Length - 1);
                _nextScan = 0f;
                if (_choice == 0)
                    CanvasHider.ShowAllExcept(Reason, null);
            }
        }

        public void Tick()
        {
            if (_choice == 0 || Time.unscaledTime < _nextScan)
                return;
            _nextScan = Time.unscaledTime + ScanSeconds;
            Camera camera = Camera.main;
            if (camera == null)
                return;

            // The list of tags changes as people come and go.
            if (Time.unscaledTime >= _nextTagRefresh)
            {
                _nextTagRefresh = Time.unscaledTime + 3f;
                _tags.Clear();
                foreach (DOMTitleText tag in Object.FindObjectsOfType<DOMTitleText>(true))
                {
                    var canvases = new List<Canvas>(tag.GetComponentsInChildren<Canvas>(true));
                    Canvas parent = tag.GetComponentInParent<Canvas>(true);
                    if (parent != null && !canvases.Contains(parent))
                        canvases.Add(parent);
                    _tags.Add((tag, canvases.ToArray()));
                }
            }

            float limit = Distances[_choice];
            float limitSquared = limit * limit;
            Vector3 eye = camera.transform.position;
            var hidden = new HashSet<Canvas>();
            foreach (var entry in _tags)
            {
                if (entry.Tag == null)
                    continue;
                bool hide = limit <= 0f || (entry.Tag.transform.position - eye).sqrMagnitude > limitSquared;
                if (!hide)
                    continue;
                foreach (Canvas canvas in entry.Canvases)
                {
                    CanvasHider.Hide(canvas, Reason);
                    hidden.Add(canvas);
                }
            }
            CanvasHider.ShowAllExcept(Reason, hidden);
        }
    }
}
