using System;
using System.Text.RegularExpressions;
using UnityEngine.Networking;

namespace FlyMod.Core
{
    // Asks GitHub once per session for the newest release, so Home can say
    // when a newer Love Menu exists. One read-only request, no data sent.
    internal class UpdateChecker
    {
        private const string LatestReleaseApi = "https://api.github.com/repos/NNKTV28/LoveMenu/releases/latest";
        public const string ReleasesPage = "https://github.com/NNKTV28/LoveMenu/releases/latest";

        private readonly Version _currentVersion;
        private UnityWebRequest _request;

        public string LatestVersion { get; private set; }
        public bool UpdateAvailable { get; private set; }

        public UpdateChecker(string currentVersion)
        {
            _currentVersion = ParseVersion(currentVersion) ?? new Version(0, 0);
        }

        public void Begin()
        {
            try
            {
                _request = UnityWebRequest.Get(LatestReleaseApi);
                _request.SetRequestHeader("User-Agent", "LoveMenu");
                _request.SetRequestHeader("Accept", "application/vnd.github+json");
                _request.timeout = 15;
                _request.SendWebRequest();
            }
            catch (Exception exception)
            {
                DebugLog.Warn("Update check could not start: " + exception.Message);
                _request = null;
            }
        }

        public void Tick()
        {
            if (_request == null || !_request.isDone)
                return;

            try
            {
                if (_request.result == UnityWebRequest.Result.Success)
                {
                    Match tag = Regex.Match(_request.downloadHandler.text, "\"tag_name\"\\s*:\\s*\"v?([0-9][0-9.]*)\"");
                    Version latest = tag.Success ? ParseVersion(tag.Groups[1].Value) : null;
                    if (latest != null)
                    {
                        LatestVersion = tag.Groups[1].Value;
                        UpdateAvailable = latest > _currentVersion;
                        DebugLog.Info("Update check: latest " + LatestVersion + (UpdateAvailable ? " (newer than this build)" : " (up to date)"));
                    }
                }
                else
                {
                    DebugLog.Info("Update check failed: " + _request.error);
                }
            }
            finally
            {
                _request.Dispose();
                _request = null;
            }
        }

        private static Version ParseVersion(string text)
        {
            text = (text ?? "").TrimStart('v', 'V');
            return Version.TryParse(text, out Version version) ? version : null;
        }
    }
}
