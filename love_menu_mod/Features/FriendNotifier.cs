using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using VWW.Clients.Curio;
using VWW.Clients.Curio.Scene.Links;
using VWW.CoreLibs.Shared;
using FlyMod.Core;
using FlyMod.UI;

namespace FlyMod.Features
{
    // "Allatu is here" when a friend's avatar appears in your room, and
    // optionally "... left". Friends already in a room you just entered are
    // listed once ("Friends here: ...") instead of one notice each.
    internal class FriendNotifier
    {
        public static readonly FriendNotifier Instance = new FriendNotifier();

        public bool Enabled = true;
        public bool NotifyLeaving;

        private const float ScanSeconds = 3f;
        private const float FriendsRefreshSeconds = 30f;
        private const float SettleSeconds = 8f;       // avatars keep loading after a room change

        private float _nextScan, _nextFriendsRefresh, _settleUntil;
        private string _scene;
        private bool _summaryDone;
        private Dictionary<Guid, string> _friends = new Dictionary<Guid, string>();
        private HashSet<Guid> _present = new HashSet<Guid>();

        public void Tick()
        {
            if (!Enabled || Time.unscaledTime < _nextScan)
                return;
            _nextScan = Time.unscaledTime + ScanSeconds;
            try
            {
                if (Time.unscaledTime >= _nextFriendsRefresh)
                {
                    _nextFriendsRefresh = Time.unscaledTime + FriendsRefreshSeconds;
                    _friends = FriendNames();
                }

                string scene = SceneManager.GetActiveScene().name;
                if (scene != _scene)
                {
                    _scene = scene;
                    _present.Clear();
                    _settleUntil = Time.unscaledTime + SettleSeconds;
                    _summaryDone = false;
                }
                if (_friends.Count == 0)
                    return;

                HashSet<Guid> now = FriendsInRoom();
                if (Time.unscaledTime < _settleUntil)
                {
                    _present = now;
                    return;
                }
                if (!_summaryDone)
                {
                    _summaryDone = true;
                    if (_present.Count > 0)
                        Toasts.Show("Friends here: " + NamesOf(_present));
                }

                foreach (Guid id in now)
                    if (!_present.Contains(id))
                        Toasts.Show(_friends[id] + " is here");
                if (NotifyLeaving)
                    foreach (Guid id in _present)
                        if (!now.Contains(id) && _friends.TryGetValue(id, out string name))
                            Toasts.Show(name + " left");
                _present = now;
            }
            catch (Exception exception)
            {
                DebugLog.Detail("Friend notices failed: " + exception.Message);
            }
        }

        private HashSet<Guid> FriendsInRoom()
        {
            var found = new HashSet<Guid>();
            foreach (DOMControllerLink avatar in UnityEngine.Object.FindObjectsOfType<DOMControllerLink>())
            {
                if (avatar == null || avatar.IsPlayerAvatar || !avatar.IsPersona)
                    continue;
                Guid owner;
                try { owner = avatar.LinkedObject.Document.ContextID; }
                catch { continue; }
                if (_friends.ContainsKey(owner))
                    found.Add(owner);
            }
            return found;
        }

        private string NamesOf(IEnumerable<Guid> ids)
        {
            var names = new List<string>();
            foreach (Guid id in ids)
                if (_friends.TryGetValue(id, out string name))
                    names.Add(name);
            return string.Join(", ", names.ToArray());
        }

        private static Dictionary<Guid, string> FriendNames()
        {
            var friends = new Dictionary<Guid, string>();
            try
            {
                var social = Singleton<ClientAPI>.Current?.SocialManager;
                if (social != null)
                    foreach (var friend in social.Friends)
                        if (friend.Value != null && !string.IsNullOrEmpty(friend.Value.Name))
                            friends[friend.Key] = friend.Value.Name;
            }
            catch (Exception exception)
            {
                DebugLog.Detail("Friend notices could not read the friends list: " + exception.Message);
            }
            return friends;
        }
    }
}
