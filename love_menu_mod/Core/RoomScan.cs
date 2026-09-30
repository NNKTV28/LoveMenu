using System;
using System.Collections.Generic;
using UnityEngine;
using VWW.Clients.Curio;
using VWW.Clients.Curio.Scene.Links;
using VWW.CoreLibs.Shared;
using FlyMod.Features;

namespace FlyMod.Core
{
    // Who is in the room, shared by the minimap, the people list and auto
    // crowd mode: every avatar and NPC with its position, name, clan and
    // whether it's a friend. Rescanned twice a second while something asks.
    internal class RoomScan
    {
        public enum Kind { Me, Friend, Clan, Player, Npc }

        public class Entry
        {
            public Kind Kind;
            public string Name;
            public string Clan;
            public Vector3 Position;
            public Guid Owner;
        }

        public static readonly RoomScan Instance = new RoomScan();

        private const float ScanSeconds = 0.5f;
        private const float FriendsRefreshSeconds = 15f;
        private const float NamesRefreshSeconds = 5f;
        private const float IdleAfterSeconds = 3f;

        public readonly List<Entry> Entries = new List<Entry>();
        public int PlayerCount { get; private set; }
        public string MyClan { get; private set; } = "";

        private float _nextScan, _nextFriends, _nextNames, _lastWanted = -100f;
        private HashSet<Guid> _friends = new HashSet<Guid>();
        private readonly Dictionary<int, (string Name, string Clan)> _nameCache = new Dictionary<int, (string, string)>();

        // Scanning only runs while a feature has asked for it recently.
        public void Want() => _lastWanted = Time.unscaledTime;

        public void Tick()
        {
            if (Time.unscaledTime - _lastWanted > IdleAfterSeconds || Time.unscaledTime < _nextScan)
                return;
            _nextScan = Time.unscaledTime + ScanSeconds;
            try
            {
                Scan();
            }
            catch (Exception exception)
            {
                DebugLog.Detail("Room scan failed: " + exception.Message);
            }
        }

        private void Scan()
        {
            if (Time.unscaledTime >= _nextFriends)
            {
                _nextFriends = Time.unscaledTime + FriendsRefreshSeconds;
                _friends = PeopleFilter.FriendIds();
            }
            bool refreshNames = Time.unscaledTime >= _nextNames;
            if (refreshNames)
            {
                _nextNames = Time.unscaledTime + NamesRefreshSeconds;
                _nameCache.Clear();
            }

            Entries.Clear();
            int players = 0;
            foreach (DOMControllerLink controller in UnityEngine.Object.FindObjectsOfType<DOMControllerLink>())
            {
                if (controller == null)
                    continue;
                bool person = PeopleFilter.IsPersonDocument(controller);
                if (person && !controller.IsPersona)
                    continue;               // pets and rides follow their owner
                int id = controller.GetInstanceID();
                if (!_nameCache.TryGetValue(id, out var label))
                {
                    label = (Label(controller, person), person ? PeopleFilter.ClanOf(controller) : "");
                    _nameCache[id] = label;
                }
                if (!person && TeleportController.IsProp(label.Name))
                    continue;

                var entry = new Entry
                {
                    Name = label.Name,
                    Clan = label.Clan,
                    Position = controller.transform.position,
                    Owner = person ? PeopleFilter.OwnerOf(controller) : Guid.Empty,
                };
                if (controller.IsPlayerAvatar)
                {
                    entry.Kind = Kind.Me;
                    MyClan = label.Clan;
                }
                else if (!person)
                    entry.Kind = Kind.Npc;
                else
                {
                    players++;
                    entry.Kind = _friends.Contains(entry.Owner) ? Kind.Friend : Kind.Player;
                }
                Entries.Add(entry);
            }
            foreach (Entry entry in Entries)
                if (entry.Kind == Kind.Player && MyClan.Length > 0 && entry.Clan == MyClan)
                    entry.Kind = Kind.Clan;
            PlayerCount = players;
        }

        private static string Label(DOMControllerLink controller, bool person)
        {
            if (person)
            {
                try
                {
                    Guid owner = PeopleFilter.OwnerOf(controller);
                    string name = Singleton<ClientAPI>.Current?.SocialManager?.GetInfo(owner)?.Name;
                    if (!string.IsNullOrEmpty(name))
                        return name;
                }
                catch
                {
                    // fall back to the name tag
                }
            }
            string tag = TeleportController.NpcName(controller);
            int dot = tag.IndexOf(" · ", StringComparison.Ordinal);
            return dot > 0 ? tag.Substring(0, dot) : tag;
        }
    }
}
