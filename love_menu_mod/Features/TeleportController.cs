using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using VWW.Clients.Curio;
using VWW.Clients.Curio.Avatar;
using VWW.Clients.Curio.ClientUI;
using VWW.Clients.Curio.Scene.Links;
using VWW.CoreLibs.Shared;
using FlyMod.Core;

namespace FlyMod.Features
{
    internal class Waypoint
    {
        public string Name;
        public Vector3 Position;
    }

    // Waypoints are fully self-contained (your own saved spots, nothing to
    // do with anyone else). Friend teleport takes your friends from the
    // game's social service and finds their avatars in the current room by
    // persona ID - only friends already in the same room, and only ever
    // friends (not arbitrary players), matching the game's own
    // friends-only boundary rather than extending past it.
    internal class TeleportController
    {
        private readonly PlayerContext _playerContext;

        public readonly List<Waypoint> Waypoints = new List<Waypoint>();
        public readonly List<(string Name, Vector3 Position)> NearbyFriends = new List<(string, Vector3)>();
        public string FriendSearchStatus = "";


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
            _playerContext.MoveAvatarTo(targetPosition);
            Reflect.ZeroVerticalVelocity();
        }

        public void GoToFriend(Vector3 friendPosition) => GoToPosition(friendPosition + Vector3.left * 1.5f);

        // --- NPCs ----------------------------------------------------------------
        // Quest givers and other characters the server runs. Every character
        // is a DOMControllerLink; player avatars are "personas", so anything
        // that isn't one (and isn't the player) is an NPC. The name shown is
        // the one on its name tag.
        public readonly List<(string Name, Vector3 Position)> NearbyNpcs = new List<(string, Vector3)>();
        public string NpcSearchStatus = "";

        public void RefreshNearbyNpcs()
        {
            NearbyNpcs.Clear();
            foreach (DOMControllerLink character in UnityEngine.Object.FindObjectsOfType<DOMControllerLink>())
            {
                if (character == null || character.IsPlayerAvatar || character.IsPersona)
                    continue;
                NearbyNpcs.Add((NpcName(character), character.transform.position));
            }
            NearbyNpcs.Sort((a, b) => DistanceFromPlayer(a.Position).CompareTo(DistanceFromPlayer(b.Position)));
            NpcSearchStatus = NearbyNpcs.Count == 0 ? "No NPCs in this room." : NearbyNpcs.Count + " NPC(s) here, nearest first.";
        }

        public void GoToNpc(Vector3 npcPosition) => GoToPosition(npcPosition + Vector3.back * 1.5f);

        // The NPC that hands out the daily collect quests (seashells,
        // letters, flowers) on the Love Angeles beach.
        private const string QuestGiverName = "Sex_Male_Diego";

        public string GoToQuestGiver()
        {
            RefreshNearbyNpcs();
            foreach (var npc in NearbyNpcs)
            {
                if (npc.Name.IndexOf(QuestGiverName, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                GoToNpc(npc.Position);
                return "At the quest giver";
            }
            return "The quest giver isn't in this room (Love Angeles beach)";
        }

        private static string NpcName(DOMControllerLink character)
        {
            try
            {
                DOMTitleText nameTag = character.GetComponentInChildren<DOMTitleText>();
                if (nameTag != null && Traverse.Create(nameTag).Field("m_DOMObjects").GetValue() is System.Collections.IList titles)
                {
                    var parts = new List<string>();
                    foreach (object title in titles)
                    {
                        string text = Traverse.Create(title).Property("Title").GetValue() as string;
                        if (!string.IsNullOrWhiteSpace(text))
                            parts.Add(System.Text.RegularExpressions.Regex.Replace(text, "<.*?>", "").Trim());
                    }
                    if (parts.Count > 0)
                        return string.Join(" · ", parts.ToArray());
                }
            }
            catch
            {
                // fall back to the object name
            }
            string name = CleanObjectName(character.gameObject.name);
            return name.Length > 0 ? name : "NPC";
        }

        // Quest collectibles ("Collect the bottles" etc). Matched on the
        // object name because that's what the scene actually exposes -
        // there's no quest-target list to read from, so the search terms
        // are editable rather than hardcoded to one quest.
        // Key is the full object name, which is unique per server object
        // ("[DOMRenderable: 18326] Shell_..."); Name is the cleaned one shown.
        public readonly List<(string Name, Vector3 Position, string Key)> NearbyCollectibles = new List<(string, Vector3, string)>();
        public string CollectibleSearchStatus = "";
        public string CollectibleSearchTerm = "shell";

        // Quest pickups have used two naming styles: plain alphanumeric names
        // (BottleBar4g, WestBottle188t) and server objects named
        // "[DOMRenderable: 18326] Shell_..." (the seashell quest). The noise
        // is engine/art naming: "_Mesh"/"_LOD" parts of scenery, and effects
        // such as "[DOMRenderable: 4055] [Ability] Bottle RFI". Strict mode
        // keeps the two pickup shapes and drops the rest.
        public bool StrictNames = true;

        // Server objects carry their DOM type and id: "[DOMRenderable: 18326]",
        // "[DOMEffect: 18194]" (the FX_GiftboxAura glow around quest
        // pickups), and possibly others.
        private static readonly System.Text.RegularExpressions.Regex DomPrefix =
            new System.Text.RegularExpressions.Regex(@"^\[DOM[A-Za-z]*:\s*\d+\]\s*");

        // "[DOMRenderable: 18326] Shell_Pink" -> "Shell_Pink",
        // "[DOMEffect: 18194] FX_GiftboxAura" -> "FX_GiftboxAura"
        public static string CleanObjectName(string name) => DomPrefix.Replace(name ?? "", "");

        // Several words can be searched at once: "shell|flower" or "shell, flower".
        private static string[] SearchTerms(string text)
        {
            var terms = new List<string>();
            foreach (string part in (text ?? "").Split('|', ','))
                if (part.Trim().Length >= 2)
                    terms.Add(part.Trim());
            return terms.ToArray();
        }

        public void RefreshNearbyCollectibles()
        {
            NearbyCollectibles.Clear();

            string[] terms = SearchTerms(CollectibleSearchTerm);
            if (terms.Length == 0)
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
                string cleanName = CleanObjectName(name);
                if (!MatchesAny(cleanName, terms))
                    continue;
                if (StrictNames && !LooksLikeQuestPickup(name, cleanName))
                    continue;

                Vector3 position = candidate.transform.position;
                if (nearestByName.TryGetValue(name, out Vector3 existing) &&
                    Vector3.SqrMagnitude(existing - playerPosition) <= Vector3.SqrMagnitude(position - playerPosition))
                    continue;
                nearestByName[name] = position;
            }

            foreach (KeyValuePair<string, Vector3> entry in nearestByName)
                NearbyCollectibles.Add((CleanObjectName(entry.Key), entry.Value, entry.Key));

            NearbyCollectibles.Sort((a, b) =>
                Vector3.SqrMagnitude(a.Position - playerPosition).CompareTo(
                Vector3.SqrMagnitude(b.Position - playerPosition)));

            const int maxResults = 40;
            if (NearbyCollectibles.Count > maxResults)
                NearbyCollectibles.RemoveRange(maxResults, NearbyCollectibles.Count - maxResults);

            string searched = string.Join(", ", terms);
            CollectibleSearchStatus = NearbyCollectibles.Count == 0
                ? "Nothing matching \"" + searched + "\"" + (StrictNames ? " - try turning Exact names off." : " in this room.")
                : NearbyCollectibles.Count + " found, nearest first.";
        }

        // --- Go to next ------------------------------------------------------
        // One press teleports to the nearest match not visited yet; the
        // player picks it up themselves, then presses again. Picked-up items
        // disappear from the room, so each press also re-searches. Visited
        // items that are still there (skipped, or out of reach) are passed
        // over until everything here has been visited once.

        private readonly HashSet<string> _visitedCollectibles = new HashSet<string>(StringComparer.Ordinal);
        private string _visitedForSearch;

        public int CollectiblesLeft
        {
            get
            {
                int left = 0;
                foreach (var item in NearbyCollectibles)
                    if (!_visitedCollectibles.Contains(item.Key))
                        left++;
                return left;
            }
        }

        public void MarkCollectibleVisited(string key)
        {
            ForgetVisitedIfSearchChanged();
            _visitedCollectibles.Add(key);
        }

        private void ForgetVisitedIfSearchChanged()
        {
            if (_visitedForSearch == CollectibleSearchTerm)
                return;
            _visitedCollectibles.Clear();
            _visitedForSearch = CollectibleSearchTerm;
        }

        // Returns what happened, for the toast.
        public string GoToNextCollectible()
        {
            ForgetVisitedIfSearchChanged();
            RefreshNearbyCollectibles();
            if (NearbyCollectibles.Count == 0)
                return "Nothing matching \"" + CollectibleSearchTerm + "\" left in this room";

            foreach (var item in NearbyCollectibles)
            {
                if (_visitedCollectibles.Contains(item.Key))
                    continue;
                _visitedCollectibles.Add(item.Key);
                GoToPosition(item.Position);
                int left = CollectiblesLeft;
                return item.Name + (left > 0 ? " - " + left + " more here" : " - last one here");
            }

            // Everything still here was visited once already: start over.
            _visitedCollectibles.Clear();
            return "Visited all " + NearbyCollectibles.Count + " here - press again to go round once more";
        }

        // Key Hunter event: keys are deposited into a safe (carry limit 5).
        // The safe shows up as the server object "OpenSafeVFX"; anything
        // else with "Safe" in its server name is the fallback.
        public bool SearchingForKeys => (CollectibleSearchTerm ?? "").IndexOf("key", StringComparison.OrdinalIgnoreCase) >= 0;

        public string GoToSafe()
        {
            Vector3? safe = null;
            foreach (Renderer renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                string name = renderer.gameObject.name;
                string cleanName = CleanObjectName(name);
                bool serverObject = name.Length != cleanName.Length;
                if (string.Equals(cleanName, "OpenSafeVFX", StringComparison.OrdinalIgnoreCase))
                {
                    safe = renderer.transform.position;
                    break;
                }
                if (safe == null && serverObject && cleanName.IndexOf("Safe", StringComparison.OrdinalIgnoreCase) >= 0)
                    safe = renderer.transform.position;
            }

            if (safe == null)
                return "No safe in this room";
            GoToPosition(safe.Value);
            return "At the safe";
        }

        private static bool MatchesAny(string name, string[] terms)
        {
            foreach (string term in terms)
                if (name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        private static bool LooksLikeQuestPickup(string rawName, string cleanName)
        {
            // Effects and abilities are server objects too; never pickups.
            // ("[Ability] Bottle Beer" - the tag survives name cleaning.)
            if (cleanName.StartsWith("["))
                return false;
            // Mesh parts and LOD levels of scenery.
            if (cleanName.IndexOf("_Mesh", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cleanName.IndexOf("_LOD", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cleanName.IndexOf("LOD", StringComparison.Ordinal) == 0)
                return false;

            // Server objects ("[DOMRenderable: n] Shell_03 2", "Letter N 5")
            // are the quest pickups now, spaces and all. Plain scene objects
            // must still be letters and digits only (BottleBar4g).
            bool serverObject = rawName.Length != cleanName.Length;
            if (serverObject)
                return true;
            if (cleanName.IndexOf(' ') >= 0)
                return false;
            foreach (char character in cleanName)
                if (!char.IsLetterOrDigit(character))
                    return false;
            return true;
        }

        // Every distinct server-object name in the room, for finding what a
        // new quest's pickups are called. Quest items are server objects;
        // the thousands of plain scene objects (buildings, bushes, "_LOD"
        // parts) are left out so the list stays short enough to paste.
        //
        // Every call also remembers all names (scenery included), and the
        // next call first lists whatever appeared since - so pressing it
        // before and after accepting a quest shows exactly what the quest
        // spawned, even if the pickups turn out to be plain scene objects.
        private Dictionary<string, int> _previousAllNames;

        // Everything within a few metres, scenery included, nearest first -
        // stand next to a quest item and its name is at the top.
        public string DescribeObjectsNearPlayer(float radius)
        {
            if (_playerContext.Avatar == null)
                return "Your avatar isn't loaded yet.";
            Vector3 player = _playerContext.Avatar.transform.position;
            Transform ownRoot = _playerContext.Avatar.transform.root;

            var nearest = new Dictionary<string, (float Distance, bool Server)>(StringComparer.OrdinalIgnoreCase);
            foreach (Renderer renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (renderer.transform.root == ownRoot)
                    continue;
                float distance = Vector3.Distance(renderer.bounds.ClosestPoint(player), player);
                if (distance > radius)
                    continue;
                string name = renderer.gameObject.name;
                string cleanName = CleanObjectName(name);
                if (cleanName.Length == 0)
                    continue;
                if (!nearest.TryGetValue(cleanName, out var existing) || distance < existing.Distance)
                    nearest[cleanName] = (distance, name.Length != cleanName.Length);
            }

            var sorted = new List<KeyValuePair<string, (float Distance, bool Server)>>(nearest);
            sorted.Sort((a, b) => a.Value.Distance.CompareTo(b.Value.Distance));
            var text = new System.Text.StringBuilder();
            text.AppendLine("Objects within " + radius.ToString("0") + " m of you (" + sorted.Count + ", nearest first, * = server object):");
            foreach (var entry in sorted)
                text.AppendLine("  " + entry.Value.Distance.ToString("0.0") + " m  " + (entry.Value.Server ? "* " : "  ") + entry.Key);
            return text.ToString();
        }

        public string DescribeRoomObjectNames()
        {
            var serverNames = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var allNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int sceneObjects = 0;
            foreach (Renderer renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                string name = renderer.gameObject.name;
                string cleanName = CleanObjectName(name);
                if (cleanName.Length == 0)
                    continue;
                allNames.TryGetValue(cleanName, out int total);
                allNames[cleanName] = total + 1;
                if (name.Length == cleanName.Length)
                {
                    sceneObjects++;
                    continue;
                }
                serverNames.TryGetValue(cleanName, out int count);
                serverNames[cleanName] = count + 1;
            }

            var text = new System.Text.StringBuilder();
            if (_previousAllNames != null)
            {
                var appeared = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in allNames)
                {
                    _previousAllNames.TryGetValue(entry.Key, out int before);
                    if (entry.Value > before)
                        appeared[entry.Key] = entry.Value - before;
                }
                text.AppendLine("New since your last List names (" + appeared.Count + " names, scenery included):");
                foreach (var entry in appeared)
                    text.AppendLine("  " + entry.Key + (entry.Value > 1 ? "  +" + entry.Value : ""));
                text.AppendLine();
            }
            _previousAllNames = allNames;

            text.AppendLine("Server objects in this room (" + serverNames.Count + " names, " + sceneObjects + " scenery objects not listed):");
            foreach (var entry in serverNames)
                text.AppendLine("  " + entry.Key + (entry.Value > 1 ? "  x" + entry.Value : ""));
            return text.ToString();
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

            Dictionary<Guid, string> friends = FriendsById();
            if (friends.Count == 0)
            {
                FriendSearchStatus = "Your friends list hasn't loaded yet. Try again in a moment.";
                return;
            }

            CollectFriendsInThisRoom(friends);
            FriendSearchStatus = NearbyFriends.Count == 0
                ? "None of your friends are in this room right now."
                : NearbyFriends.Count + " friend(s) here.";
        }

        // The same list the game's Friends panel shows, straight from the
        // social service, so the panel doesn't have to be open.
        private static Dictionary<Guid, string> FriendsById()
        {
            var friends = new Dictionary<Guid, string>();
            try
            {
                var social = Singleton<ClientAPI>.Current?.SocialManager;
                if (social == null)
                    return friends;
                foreach (var friend in social.Friends)
                    if (friend.Value != null && !string.IsNullOrEmpty(friend.Value.Name))
                        friends[friend.Key] = friend.Value.Name;
            }
            catch (Exception exception)
            {
                DebugLog.Warn("Could not read the friends list: " + exception.Message);
            }
            return friends;
        }

        // Every avatar in the room is a DOMControllerLink whose document
        // belongs to its owner's persona - the same check the game uses to
        // spot the player's own avatar (DOMControllerLink.IsPlayerAvatar).
        private void CollectFriendsInThisRoom(Dictionary<Guid, string> friends)
        {
            var alreadyAdded = new HashSet<Guid>();
            foreach (DOMControllerLink avatar in UnityEngine.Object.FindObjectsOfType<DOMControllerLink>())
            {
                if (avatar == null || avatar.IsPlayerAvatar)
                    continue;
                Guid ownerId;
                try
                {
                    ownerId = avatar.LinkedObject.Document.ContextID;
                }
                catch
                {
                    continue;
                }
                if (!friends.TryGetValue(ownerId, out string name) || !alreadyAdded.Add(ownerId))
                    continue;
                NearbyFriends.Add((name, avatar.transform.position));
            }
            NearbyFriends.Sort((a, b) => DistanceFromPlayer(a.Position).CompareTo(DistanceFromPlayer(b.Position)));
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
