using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;
using VWW.Clients.Curio;
using VWW.Clients.Curio.Avatar;
using VWW.Clients.Curio.Scene.Links;
using VWW.CoreLibs.DOM;
using VWW.CoreLibs.Shared;
using FlyMod.Core;

namespace FlyMod.Features
{
    // "Show people: Everyone | My clan | Friends". In a packed club most of
    // the frame goes on drawing avatars: every outfit piece, hair, pet and
    // name tag is its own draw, plus shadows. This stops drawing everyone
    // outside the chosen group - their avatar, pets and name tag - and lets
    // their animation stop while hidden. It's only on this PC: they still
    // see you, chat works, and you can still bump into them.
    //
    // Who is who: every avatar (and its pets) belongs to a document owned
    // by that person's persona (Document.ContextID), the same ID the
    // friends list uses. Clans come from the "Clan: ..." line of each name
    // tag, compared with the line on your own avatar.
    internal class PeopleFilter
    {
        public enum Mode { Everyone, Clan, Friends }
        public static readonly PeopleFilter Instance = new PeopleFilter();
        public static readonly string[] Names = { "Everyone", "My clan", "Friends" };

        private const float ScanSeconds = 1f;
        private const float FriendsRefreshSeconds = 10f;
        private static readonly Regex Tags = new Regex("<.*?>");

        private Mode _mode = Mode.Everyone;
        private float _nextScan, _nextFriendsRefresh;
        private HashSet<Guid> _friends = new HashSet<Guid>();
        private readonly Dictionary<Renderer, bool> _hiddenRenderers = new Dictionary<Renderer, bool>();
        private readonly Dictionary<Canvas, bool> _hiddenCanvases = new Dictionary<Canvas, bool>();
        private readonly Dictionary<Animator, AnimatorCullingMode> _culledAnimators = new Dictionary<Animator, AnimatorCullingMode>();

        public int PeopleHere { get; private set; }
        public int PeopleHidden { get; private set; }
        public string MyClan { get; private set; } = "";

        public Mode Current
        {
            get => _mode;
            set
            {
                if (_mode == value)
                    return;
                _mode = value;
                _nextScan = 0f;
                if (value == Mode.Everyone)
                    ShowEveryone();
            }
        }

        public void Tick()
        {
            if (_mode == Mode.Everyone || Time.unscaledTime < _nextScan)
                return;
            _nextScan = Time.unscaledTime + ScanSeconds;
            try
            {
                Scan();
            }
            catch (Exception exception)
            {
                DebugLog.Detail("People filter scan failed: " + exception.Message);
            }
        }

        private void Scan()
        {
            if (_mode == Mode.Friends && Time.unscaledTime >= _nextFriendsRefresh)
            {
                _nextFriendsRefresh = Time.unscaledTime + FriendsRefreshSeconds;
                _friends = FriendIds();
            }

            DOMControllerLink[] controllers = UnityEngine.Object.FindObjectsOfType<DOMControllerLink>();

            // Who is shown, per persona, decided from their main avatar.
            Guid me = Guid.Empty;
            var clanOf = new Dictionary<Guid, string>();
            foreach (DOMControllerLink controller in controllers)
            {
                if (controller == null || !controller.IsPersona)
                    continue;
                Guid owner = OwnerOf(controller);
                if (owner == Guid.Empty)
                    continue;
                string clan = ClanOf(controller);
                if (controller.IsPlayerAvatar)
                {
                    me = owner;
                    MyClan = clan;
                }
                clanOf[owner] = clan;
            }

            var shownRenderers = new HashSet<Renderer>();
            var shownCanvases = new HashSet<Canvas>();
            var shownAnimators = new HashSet<Animator>();
            int people = 0, hidden = 0;
            foreach (DOMControllerLink controller in controllers)
            {
                if (controller == null || controller.IsPlayerAvatar)
                    continue;
                Guid owner = OwnerOf(controller);
                if (owner == Guid.Empty || owner == me || !IsPersonDocument(controller))
                    continue;
                bool show = ShouldShow(owner, clanOf);
                if (controller.IsPersona)
                {
                    people++;
                    if (!show)
                        hidden++;
                }
                if (show)
                {
                    foreach (Renderer renderer in controller.GetComponentsInChildren<Renderer>(true))
                        shownRenderers.Add(renderer);
                    foreach (Canvas canvas in controller.GetComponentsInChildren<Canvas>(true))
                        shownCanvases.Add(canvas);
                    foreach (Animator animator in controller.GetComponentsInChildren<Animator>(true))
                        shownAnimators.Add(animator);
                    continue;
                }
                // New outfit pieces and pets keep loading, so this runs every scan.
                foreach (Renderer renderer in controller.GetComponentsInChildren<Renderer>(true))
                {
                    if (!_hiddenRenderers.ContainsKey(renderer))
                        _hiddenRenderers[renderer] = renderer.forceRenderingOff;
                    renderer.forceRenderingOff = true;
                }
                foreach (Canvas canvas in controller.GetComponentsInChildren<Canvas>(true))
                {
                    if (!_hiddenCanvases.ContainsKey(canvas))
                        _hiddenCanvases[canvas] = canvas.enabled;
                    canvas.enabled = false;
                }
                foreach (Animator animator in controller.GetComponentsInChildren<Animator>(true))
                {
                    if (!_culledAnimators.ContainsKey(animator))
                        _culledAnimators[animator] = animator.cullingMode;
                    animator.cullingMode = AnimatorCullingMode.CullCompletely;
                }
            }
            PeopleHere = people;
            PeopleHidden = hidden;

            // Anyone who became visible (joined your clan, a friend, or the
            // rules changed) gets their original state back.
            RestoreWhere(r => r == null || shownRenderers.Contains(r));
            RestoreCanvasesWhere(c => c == null || shownCanvases.Contains(c));
            RestoreAnimatorsWhere(a => a == null || shownAnimators.Contains(a));
        }

        private bool ShouldShow(Guid owner, Dictionary<Guid, string> clanOf)
        {
            switch (_mode)
            {
                case Mode.Friends:
                    return _friends.Contains(owner);
                case Mode.Clan:
                    return MyClan.Length > 0 && clanOf.TryGetValue(owner, out string clan) && clan == MyClan;
                default:
                    return true;
            }
        }

        private static Guid OwnerOf(DOMControllerLink controller)
        {
            try
            {
                return controller.LinkedObject?.Document?.ContextID ?? Guid.Empty;
            }
            catch
            {
                return Guid.Empty;
            }
        }

        // Avatars, and pets or rides attached to them, live in a persona's
        // document; NPCs and room objects don't.
        private static bool IsPersonDocument(DOMControllerLink controller)
        {
            try
            {
                return controller.LinkedObject.Document.ContextType == DOMDocumentContextType.Persona;
            }
            catch
            {
                return false;
            }
        }

        // "Clan: E N V Y" on the name tag -> "ENVY".
        private static string ClanOf(DOMControllerLink controller)
        {
            try
            {
                DOMTitleText nameTag = controller.GetComponentInChildren<DOMTitleText>(true);
                if (nameTag == null || !(Traverse.Create(nameTag).Field("m_DOMObjects").GetValue() is System.Collections.IList titles))
                    return "";
                // The clan entry is the one with a clan icon ("CM-ClanIcon");
                // its text is just the name - "Clan:" is only added when drawn.
                foreach (object title in titles)
                {
                    var traverse = Traverse.Create(title);
                    object properties = traverse.Property("Properties").GetValue();
                    if (properties == null || !Traverse.Create(properties).Method("ContainsKey", "CM-ClanIcon").GetValue<bool>())
                        continue;
                    string text = Tags.Replace(traverse.Property("Title").GetValue() as string ?? "", "");
                    int at = text.IndexOf("Clan:", StringComparison.OrdinalIgnoreCase);
                    if (at >= 0)
                        text = text.Substring(at + 5);
                    return Regex.Replace(text, @"\s+", "").ToUpperInvariant();
                }
            }
            catch
            {
                // no name tag yet
            }
            return "";
        }

        private static HashSet<Guid> FriendIds()
        {
            var ids = new HashSet<Guid>();
            try
            {
                var social = Singleton<VWW.Clients.Curio.ClientAPI>.Current?.SocialManager;
                if (social != null)
                    foreach (var friend in social.Friends)
                        ids.Add(friend.Key);
            }
            catch (Exception exception)
            {
                DebugLog.Warn("People filter could not read the friends list: " + exception.Message);
            }
            return ids;
        }

        private void ShowEveryone()
        {
            RestoreWhere(_ => true);
            RestoreCanvasesWhere(_ => true);
            RestoreAnimatorsWhere(_ => true);
            PeopleHidden = 0;
        }

        private void RestoreWhere(Func<Renderer, bool> match)
        {
            var done = new List<Renderer>();
            foreach (var pair in _hiddenRenderers)
            {
                if (!match(pair.Key))
                    continue;
                if (pair.Key != null)
                    pair.Key.forceRenderingOff = pair.Value;
                done.Add(pair.Key);
            }
            foreach (Renderer renderer in done)
                _hiddenRenderers.Remove(renderer);
        }

        private void RestoreCanvasesWhere(Func<Canvas, bool> match)
        {
            var done = new List<Canvas>();
            foreach (var pair in _hiddenCanvases)
            {
                if (!match(pair.Key))
                    continue;
                if (pair.Key != null)
                    pair.Key.enabled = pair.Value;
                done.Add(pair.Key);
            }
            foreach (Canvas canvas in done)
                _hiddenCanvases.Remove(canvas);
        }

        private void RestoreAnimatorsWhere(Func<Animator, bool> match)
        {
            var done = new List<Animator>();
            foreach (var pair in _culledAnimators)
            {
                if (!match(pair.Key))
                    continue;
                if (pair.Key != null)
                    pair.Key.cullingMode = pair.Value;
                done.Add(pair.Key);
            }
            foreach (Animator animator in done)
                _culledAnimators.Remove(animator);
        }
    }
}
