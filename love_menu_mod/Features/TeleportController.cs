using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using VWW.Clients.Curio.Avatar;
using VWW.Clients.Curio.ClientUI;
using FlyMod.Core;

namespace FlyMod.Features
{
    internal class Waypoint
    {
        public string Name;
        public Vector3 Position;
    }

    // Waypoints are fully self-contained (your own saved spots, nothing to
    // do with anyone else). Friend teleport matches names from your open
    // Friends list against nametags of avatars actually visible in the
    // current scene - only works for friends already in the same room, and
    // only ever surfaces friends (not arbitrary players), matching the
    // game's own friends-only boundary rather than extending past it.
    internal class TeleportController
    {
        private readonly PlayerContext _playerContext;

        public readonly List<Waypoint> Waypoints = new List<Waypoint>();
        public readonly List<(string Name, Vector3 Position)> NearbyFriends = new List<(string, Vector3)>();
        public string FriendSearchStatus = "";

        private static readonly FieldInfo FriendNameTextField =
            typeof(FriendItemBehaviour).GetField("m_NameText", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo NametagParentGameObjectField =
            typeof(DOMTitleText).GetField("<ParentGameObject>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);

        public TeleportController(PlayerContext playerContext)
        {
            _playerContext = playerContext;
        }

        public void SaveWaypoint(string requestedName)
        {
            if (_playerContext.Avatar == null)
                return;
            string finalName = string.IsNullOrWhiteSpace(requestedName)
                ? "Waypoint " + (Waypoints.Count + 1)
                : requestedName.Trim();
            Waypoints.Add(new Waypoint { Name = finalName, Position = _playerContext.Avatar.transform.position });
        }

        public void GoToPosition(Vector3 targetPosition)
        {
            if (_playerContext.Avatar == null)
                return;
            _playerContext.Avatar.transform.position = targetPosition;
            Reflect.ZeroVerticalVelocity();
            _playerContext.MarkIntentionalTeleport();
        }

        public void GoToFriend(Vector3 friendPosition) => GoToPosition(friendPosition + Vector3.left * 1.5f);

        // Quest collectibles ("Collect the bottles" etc). Matched on the
        // object name because that's what the scene actually exposes -
        // there's no quest-target list to read from, so the search terms
        // are editable rather than hardcoded to one quest.
        public readonly List<(string Name, Vector3 Position)> NearbyCollectibles = new List<(string, Vector3)>();
        public string CollectibleSearchStatus = "";
        public string CollectibleSearchTerm = "bottle";

        // Real quest pickups are plain alphanumeric names (BottleBar4g,
        // WestBottle188t). The noise is engine/art naming: underscores
        // ("BottleChampagne_Mesh" is scenery) and brackets or spaces
        // ("[DOMRenderable: 4055] [Ability] Bottle RFI" is an effect).
        // Filtering on that shape is what separates them.
        public bool StrictNames = true;

        public void RefreshNearbyCollectibles()
        {
            NearbyCollectibles.Clear();

            string term = (CollectibleSearchTerm ?? "").Trim();
            if (term.Length < 2)
            {
                CollectibleSearchStatus = "Enter at least 2 characters to search for.";
                return;
            }

            Vector3 playerPosition = _playerContext.Avatar != null
                ? _playerContext.Avatar.transform.position
                : Vector3.zero;

            // Keyed by name so an object split across LOD renderers shows
            // up once, not three times.
            var nearestByName = new Dictionary<string, Vector3>(StringComparer.Ordinal);
            foreach (Renderer renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                GameObject candidate = renderer.gameObject;
                string name = candidate.name;
                if (name.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (StrictNames && !LooksLikeQuestPickup(name))
                    continue;

                Vector3 position = candidate.transform.position;
                if (nearestByName.TryGetValue(name, out Vector3 existing) &&
                    Vector3.SqrMagnitude(existing - playerPosition) <= Vector3.SqrMagnitude(position - playerPosition))
                    continue;
                nearestByName[name] = position;
            }

            foreach (KeyValuePair<string, Vector3> entry in nearestByName)
                NearbyCollectibles.Add((entry.Key, entry.Value));

            NearbyCollectibles.Sort((a, b) =>
                Vector3.SqrMagnitude(a.Position - playerPosition).CompareTo(
                Vector3.SqrMagnitude(b.Position - playerPosition)));

            const int maxResults = 40;
            if (NearbyCollectibles.Count > maxResults)
                NearbyCollectibles.RemoveRange(maxResults, NearbyCollectibles.Count - maxResults);

            CollectibleSearchStatus = NearbyCollectibles.Count == 0
                ? "Nothing matching \"" + term + "\"" + (StrictNames ? " - try turning Strict off." : " in this room.")
                : NearbyCollectibles.Count + " found, nearest first.";
        }

        private static bool LooksLikeQuestPickup(string name)
        {
            foreach (char character in name)
                if (!char.IsLetterOrDigit(character))
                    return false;
            return true;
        }

        public float DistanceFromPlayer(Vector3 position)
        {
            if (_playerContext.Avatar == null)
                return 0f;
            return Vector3.Distance(_playerContext.Avatar.transform.position, position);
        }

        public void RefreshNearbyFriends()
        {
            NearbyFriends.Clear();

            HashSet<string> friendNames = CollectOpenFriendsListNames();
            if (friendNames.Count == 0)
            {
                FriendSearchStatus = "No friends found - open the Friends panel once first.";
                return;
            }

            CollectVisibleFriendsInScene(friendNames);
            FriendSearchStatus = NearbyFriends.Count == 0
                ? "No friends visible in this room right now."
                : NearbyFriends.Count + " friend(s) found here.";
        }

        private static HashSet<string> CollectOpenFriendsListNames()
        {
            var friendNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FriendItemBehaviour friendListItem in UnityEngine.Object.FindObjectsOfType<FriendItemBehaviour>())
                if (FriendNameTextField?.GetValue(friendListItem) is Text nameText && !string.IsNullOrWhiteSpace(nameText.text))
                    friendNames.Add(nameText.text.Trim());
            return friendNames;
        }

        private void CollectVisibleFriendsInScene(HashSet<string> friendNames)
        {
            var alreadyAdded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DOMTitleText nametag in UnityEngine.Object.FindObjectsOfType<DOMTitleText>())
            {
                if (!(NametagParentGameObjectField?.GetValue(nametag) is GameObject avatarGameObject) || avatarGameObject == null)
                    continue;

                string avatarName = avatarGameObject.transform.root.name;
                bool isKnownFriend = !string.IsNullOrEmpty(avatarName) && friendNames.Contains(avatarName);
                bool isSelf = avatarName == _playerContext.PlayerName;
                if (!isKnownFriend || isSelf || !alreadyAdded.Add(avatarName))
                    continue;

                NearbyFriends.Add((avatarName, avatarGameObject.transform.root.position));
            }
        }

        public string EncodeWaypoints()
        {
            var encodedWaypoints = new List<string>();
            foreach (Waypoint waypoint in Waypoints)
                encodedWaypoints.Add(EncodeWaypoint(waypoint));
            return string.Join(";", encodedWaypoints);
        }

        private static string EncodeWaypoint(Waypoint waypoint)
        {
            string safeName = waypoint.Name.Replace(",", "").Replace(";", "");
            return safeName + "," +
                waypoint.Position.x.ToString(CultureInfo.InvariantCulture) + "," +
                waypoint.Position.y.ToString(CultureInfo.InvariantCulture) + "," +
                waypoint.Position.z.ToString(CultureInfo.InvariantCulture);
        }

        public void DecodeWaypoints(string encoded)
        {
            Waypoints.Clear();
            if (string.IsNullOrEmpty(encoded))
                return;
            foreach (string encodedWaypoint in encoded.Split(';'))
                if (TryDecodeWaypoint(encodedWaypoint, out Waypoint waypoint))
                    Waypoints.Add(waypoint);
        }

        private static bool TryDecodeWaypoint(string encodedWaypoint, out Waypoint waypoint)
        {
            waypoint = null;
            if (string.IsNullOrEmpty(encodedWaypoint))
                return false;

            string[] fields = encodedWaypoint.Split(',');
            if (fields.Length != 4)
                return false;

            float x = 0f, y = 0f, z = 0f;
            bool parsedOk =
                float.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
                float.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out y) &&
                float.TryParse(fields[3], NumberStyles.Float, CultureInfo.InvariantCulture, out z);
            if (!parsedOk)
                return false;

            waypoint = new Waypoint { Name = fields[0], Position = new Vector3(x, y, z) };
            return true;
        }
    }
}
