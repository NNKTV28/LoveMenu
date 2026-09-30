using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;
using VWW.Clients.Curio.Scene.Links;
using FlyMod.Core;

namespace FlyMod.Features
{
    // Logs what the player collects (seashells, flowers, keys, ...).
    //
    // There's no "picked up" event to listen to, so it watches instead:
    // server objects near the player are tracked, and when one vanishes
    // while the player stands right next to it, that's a pickup. It works
    // for any quest item without knowing its name - which also makes it the
    // easiest way to learn what a new quest's pickups are called.
    //
    // Ignored: things attached to avatars (pets, wings, accessories),
    // effects, and room changes (many objects vanishing at once).
    internal class CollectionLogger
    {
        private const float PickupRange = 6f;
        private const float FullScanSeconds = 2f;
        private const float CheckSeconds = 0.2f;
        private const int RoomChangeThreshold = 4;

        private class Tracked
        {
            public Transform Transform;     // the "[DOM...: id] Name" object
            public string Name;
            public Vector3 LastPosition;
            public bool Known;              // a known quest pickup (key, shell...)
            public bool CountedWhileHidden;
        }

        public class Entry
        {
            public DateTime When;
            public string Name;
            public string Type;
        }

        private readonly PlayerContext _playerContext;
        private readonly Dictionary<int, Tracked> _tracked = new Dictionary<int, Tracked>();
        private readonly HashSet<int> _ignored = new HashSet<int>();
        private float _nextFullScan, _nextCheck;
        private DateTime _countsDate;

        public readonly List<Entry> Recent = new List<Entry>();
        public readonly SortedDictionary<string, int> TodayCounts = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public string LogPath { get; }

        public CollectionLogger(PlayerContext playerContext, string logFolder)
        {
            _playerContext = playerContext;
            LogPath = Path.Combine(logFolder, "collected.log");
            LoadToday();
        }

        public int TodayTotal
        {
            get
            {
                int total = 0;
                foreach (int count in TodayCounts.Values)
                    total += count;
                return total;
            }
        }

        public void Tick()
        {
            if (_playerContext.Avatar == null)
                return;
            float now = Time.unscaledTime;
            if (now >= _nextFullScan)
            {
                _nextFullScan = now + FullScanSeconds;
                TrackServerObjects();
            }
            if (now >= _nextCheck)
            {
                _nextCheck = now + CheckSeconds;
                CheckForPickups();
            }
        }

        private void TrackServerObjects()
        {
            foreach (Renderer renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                int rendererId = renderer.gameObject.GetInstanceID();
                if (_ignored.Contains(rendererId))
                    continue;

                // The pickup is the server object ("[DOMRenderable: 5] Key_Gold");
                // its visible mesh is often a child with a plain name.
                Transform server = ServerObjectOf(renderer.transform);
                if (server == null)
                {
                    _ignored.Add(rendererId);
                    continue;
                }
                int id = server.gameObject.GetInstanceID();
                if (_tracked.ContainsKey(id) || _ignored.Contains(id))
                    continue;

                string name = TeleportController.CleanObjectName(server.name);
                if (!LooksLikeItem(name) || server.GetComponentInParent<DOMControllerLink>() != null)
                {
                    _ignored.Add(id);
                    continue;
                }
                _tracked[id] = new Tracked { Transform = server, Name = name, LastPosition = server.position, Known = IsKnownItem(name) };
            }
        }

        // The nearest "[DOM...]" object at or above this one (up to 4 levels).
        private static Transform ServerObjectOf(Transform transform)
        {
            for (int depth = 0; depth < 5 && transform != null; depth++)
            {
                if (transform.name.StartsWith("[DOM", StringComparison.Ordinal))
                    return transform;
                transform = transform.parent;
            }
            return null;
        }

        // A known pickup counts as picked up when it's hidden, not only when
        // it's deleted: Key Hunter keys stay in the room, just hidden.
        private static bool IsHidden(Transform transform)
        {
            if (!transform.gameObject.activeInHierarchy)
                return true;
            foreach (Renderer renderer in transform.GetComponentsInChildren<Renderer>())
                if (renderer.enabled && !renderer.forceRenderingOff)
                    return false;
            return true;
        }

        private static bool LooksLikeItem(string name)
        {
            if (name.Length == 0 || name.StartsWith("[") || name.IndexOf("VFX", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Effect", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            foreach (string prefix in new[] { "Pet:", "Transport:", "Wings", "HM_", "HF_" })
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return false;
            return true;
        }

        private readonly List<int> _vanished = new List<int>();

        private void CheckForPickups()
        {
            Vector3 player = _playerContext.Avatar.transform.position;
            _vanished.Clear();
            // Removed objects count. Hidden ones count only for known pickups
            // (keys are hidden, not removed); other things hide when you walk
            // up (Cupon, GlassBallMesh, InteractiveArrow) and aren't pickups.
            var hiddenPickups = new List<string>();
            foreach (var pair in _tracked)
            {
                Tracked item = pair.Value;
                Transform transform = item.Transform;
                if (transform == null)
                {
                    _vanished.Add(pair.Key);
                    continue;
                }
                if (!item.Known)
                {
                    // Not counted, but written to the log once as "Other" when
                    // it's hidden right next to you - that's how a new quest
                    // item's name shows up.
                    if (transform.gameObject.activeInHierarchy)
                    {
                        item.LastPosition = transform.position;
                        item.CountedWhileHidden = false;
                    }
                    else if (!item.CountedWhileHidden)
                    {
                        item.CountedWhileHidden = true;
                        if (Vector3.Distance(item.LastPosition, player) <= PickupRange)
                            hiddenPickups.Add(item.Name);
                    }
                    continue;
                }
                if (!IsHidden(transform))
                {
                    item.LastPosition = transform.position;
                    item.CountedWhileHidden = false;
                }
                else if (!item.CountedWhileHidden)
                {
                    item.CountedWhileHidden = true;
                    if (Vector3.Distance(item.LastPosition, player) <= PickupRange)
                        hiddenPickups.Add(item.Name);
                }
            }
            foreach (string name in hiddenPickups)
                if (name.IndexOf("GiftboxAura", StringComparison.OrdinalIgnoreCase) < 0 || hiddenPickups.Count == 1)
                    Record(name);
            if (_vanished.Count == 0)
                return;

            // A room change removes everything at once; a pickup removes one.
            bool roomChange = _vanished.Count >= RoomChangeThreshold;
            var picked = new List<string>();
            foreach (int id in _vanished)
            {
                Tracked item = _tracked[id];
                _tracked.Remove(id);
                if (!roomChange && !item.CountedWhileHidden && Vector3.Distance(item.LastPosition, player) <= PickupRange)
                    picked.Add(item.Name);
            }

            // The glow around a pickup goes with it; log the glow only when it
            // is all that vanished (a pickup whose own name we didn't catch).
            bool somethingBesidesGlow = picked.Exists(name => name.IndexOf("GiftboxAura", StringComparison.OrdinalIgnoreCase) < 0);
            foreach (string name in picked)
                if (!somethingBesidesGlow || name.IndexOf("GiftboxAura", StringComparison.OrdinalIgnoreCase) < 0)
                    Record(name);
        }

        // Quest pickups known so far. Other things near you also disappear
        // (other players' pets, interactive zones, props that respawn); those
        // only go to collected.log as "Other", to help name new quest items,
        // and are left out of the counts and notices.
        private static readonly string[] KnownItems = { "Shell", "Letter", "Key", "GiftboxAura", "Flower" };

        // The word must start the name or follow a non-letter, so "Key_Gold"
        // and "Letter N 5" count but "Monkey" and "Turkey" don't.
        private static readonly System.Text.RegularExpressions.Regex KnownPattern =
            new System.Text.RegularExpressions.Regex("(^|[^a-z])(" + string.Join("|", KnownItems) + ")",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        public static bool IsKnownItem(string name) => KnownPattern.IsMatch(name ?? "");

        private const string OtherType = "Other";

        // "Shell_Pink (2)" -> "Shell", "Key_Blue3" -> "Key", "BottleBar4g" -> "BottleBar"
        public static string TypeOf(string name)
        {
            // The glow around a pickup vanishes with it; name it after what it marks.
            if (name.IndexOf("GiftboxAura", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Pickup";
            int end = 0;
            while (end < name.Length && char.IsLetter(name[end]))
                end++;
            return end > 0 ? name.Substring(0, end) : name;
        }

        private void Record(string name)
        {
            RollOverIfNewDay();
            DateTime now = DateTime.Now;
            bool known = IsKnownItem(name);
            string type = known ? TypeOf(name) : OtherType;
            int count = 0;
            if (known)
            {
                TodayCounts.TryGetValue(type, out count);
                TodayCounts[type] = count + 1;
                Recent.Insert(0, new Entry { When = now, Name = name, Type = type });
                if (Recent.Count > 20)
                    Recent.RemoveAt(Recent.Count - 1);
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                File.AppendAllText(LogPath, now.ToString("yyyy-MM-dd HH:mm:ss") + "\t" + type + "\t" + name + "\t" +
                    SceneManager.GetActiveScene().name + Environment.NewLine);
            }
            catch (Exception exception)
            {
                DebugLog.Warn("Could not write the collection log: " + exception.Message);
            }

            if (!known)
            {
                DebugLog.Detail("Vanished near you (not a known pickup): " + name);
                return;
            }
            DebugLog.Info("Collected " + name);
            FlyMod.UI.Toasts.Show("Collected " + name + " - " + (count + 1) + " " + type + " today");
        }

        private void RollOverIfNewDay()
        {
            if (_countsDate == DateTime.Today)
                return;
            _countsDate = DateTime.Today;
            TodayCounts.Clear();
        }

        // Today's counts survive restarts: read back today's lines.
        private void LoadToday()
        {
            _countsDate = DateTime.Today;
            try
            {
                if (!File.Exists(LogPath))
                    return;
                string today = DateTime.Today.ToString("yyyy-MM-dd");
                foreach (string line in File.ReadAllLines(LogPath))
                {
                    string[] parts = line.Split('\t');
                    // Lines from before 1.5.1 have no "Other" type, so check the name too.
                    if (parts.Length < 3 || !parts[0].StartsWith(today) || parts[1] == OtherType || !IsKnownItem(parts[2]))
                        continue;
                    TodayCounts.TryGetValue(parts[1], out int count);
                    TodayCounts[parts[1]] = count + 1;
                    if (DateTime.TryParse(parts[0], out DateTime when))
                        Recent.Insert(0, new Entry { When = when, Name = parts[2], Type = parts[1] });
                }
                if (Recent.Count > 20)
                    Recent.RemoveRange(20, Recent.Count - 20);
            }
            catch (Exception exception)
            {
                DebugLog.Warn("Could not read the collection log: " + exception.Message);
            }
        }

        public void OpenLogFolder()
        {
            try
            {
                string folder = Path.GetDirectoryName(LogPath);
                Directory.CreateDirectory(folder);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + folder + "\"");
            }
            catch
            {
                // best effort
            }
        }
    }
}
