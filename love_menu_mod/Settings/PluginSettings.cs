using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;
using FlyMod.Core;
using FlyMod.Features;
using FlyMod.Inputs;
using FlyMod.UI;

namespace FlyMod.Settings
{
    // The only place that talks to BepInEx's ConfigFile - everything else
    // just holds live state and gets read from / written into here.
    //
    // Save() runs every couple of seconds (see LoveMenuPlugin). A ConfigEntry
    // only writes the file when its value actually changes, so an unchanged
    // save costs nothing - and a crash no longer loses what was set since the
    // menu was last closed.
    internal class PluginSettings
    {
        private readonly ConfigFile _configFile;
        private readonly Keybinds _keybinds;
        private readonly FeatureHotkeys _hotkeys;
        private readonly FlyController _flyController;
        private readonly SpeedBoostController _speedBoostController;
        private readonly TeleportController _teleportController;
        private readonly KnockbackImmunityController _knockbackImmunityController;
        private readonly BodyRotationLockController _bodyRotationLockController;
        private readonly CameraController _cameraController;
        private readonly CrashWorkaroundController _crashWorkaroundController;
        private readonly PromoPopupController _promoPopupController;
        private readonly PerformanceController _performanceController;
        private readonly WingsHiderController _wingsHiderController;
        private readonly MiniOverlay _overlay;
        private readonly WelcomeScreen _welcome;
        private readonly MenuUI _menuUI;
        private Theme Theme => _menuUI.Theme;

        // Graphics values the player never touched are stored as this, and
        // left to the game on load.
        private const float GameDefault = -1f;

        private ConfigEntry<KeyCode> _menuKeyEntry, _flyUpKeyEntry, _flyDownKeyEntry;
        private ConfigEntry<float> _flySpeedEntry, _speedMultiplierEntry, _transparencyEntry, _uiScaleEntry, _fovEntry;
        private ConfigEntry<float> _autoFreeThresholdEntry, _shadowDistanceEntry, _lodBiasEntry, _windowXEntry, _windowYEntry;
        private ConfigEntry<int> _themeIndexEntry, _overlayCornerEntry, _textureLimitEntry, _sectionEntry;
        private ConfigEntry<string> _waypointsEntry, _crashWorkaroundsEntry, _featureHotkeysEntry, _dismissedUpdateEntry;
        private ConfigEntry<bool> _knockbackImmunityEnabledEntry, _bodyRotationLockEnabledEntry, _hidePromoPopupsEntry;
        private ConfigEntry<bool> _autoScaleEntry, _hideAvatarInScreenshotsEntry, _autoFreeEntry, _hideWingsEntry;
        private ConfigEntry<bool> _overlayEnabledEntry, _overlayFpsEntry, _overlayRamEntry, _overlayActiveEntry;
        private ConfigEntry<bool> _detailedLoggingEntry, _welcomeSeenEntry, _quizHelperEntry;
        private ConfigEntry<int> _showPeopleEntry;
        private ConfigEntry<string> _fortuneBallsEntry;
        private ConfigEntry<bool> _fortuneNotifyEntry, _friendNoticesEntry, _friendLeftNoticesEntry;

        public PluginSettings(ConfigFile configFile, Keybinds keybinds, FeatureHotkeys hotkeys, FlyController flyController,
            SpeedBoostController speedBoostController, TeleportController teleportController,
            KnockbackImmunityController knockbackImmunityController, BodyRotationLockController bodyRotationLockController,
            CameraController cameraController, CrashWorkaroundController crashWorkaroundController,
            PromoPopupController promoPopupController, PerformanceController performanceController,
            WingsHiderController wingsHiderController, MiniOverlay overlay, WelcomeScreen welcome, MenuUI menuUI)
        {
            _configFile = configFile;
            _keybinds = keybinds;
            _hotkeys = hotkeys;
            _flyController = flyController;
            _speedBoostController = speedBoostController;
            _teleportController = teleportController;
            _knockbackImmunityController = knockbackImmunityController;
            _bodyRotationLockController = bodyRotationLockController;
            _cameraController = cameraController;
            _crashWorkaroundController = crashWorkaroundController;
            _promoPopupController = promoPopupController;
            _performanceController = performanceController;
            _wingsHiderController = wingsHiderController;
            _overlay = overlay;
            _welcome = welcome;
            _menuUI = menuUI;
        }

        public void Load()
        {
            BindAllConfigEntries();
            ApplyLoadedValuesToControllers();
        }

        private void BindAllConfigEntries()
        {
            _menuKeyEntry = _configFile.Bind("Keybinds", "MenuKey", KeyCode.F7);
            _flyUpKeyEntry = _configFile.Bind("Keybinds", "FlyUpKey", KeyCode.Space);
            _flyDownKeyEntry = _configFile.Bind("Keybinds", "FlyDownKey", KeyCode.LeftControl);
            _featureHotkeysEntry = _configFile.Bind("Keybinds", "FeatureHotkeys", "");
            _flySpeedEntry = _configFile.Bind("Movement", "FlySpeed", 6f);
            _speedMultiplierEntry = _configFile.Bind("Movement", "SpeedMultiplier", 1.5f);
            _knockbackImmunityEnabledEntry = _configFile.Bind("Movement", "KnockbackImmunity", false);
            _bodyRotationLockEnabledEntry = _configFile.Bind("Movement", "BodyRotationLock", false);
            _fovEntry = _configFile.Bind("Camera", "FieldOfView", CameraController.DefaultFov);
            _hideAvatarInScreenshotsEntry = _configFile.Bind("Camera", "HideAvatarInScreenshots", false);
            _autoFreeEntry = _configFile.Bind("Performance", "AutoFree", false);
            _autoFreeThresholdEntry = _configFile.Bind("Performance", "AutoFreeThresholdMB", 6000f);
            _hideWingsEntry = _configFile.Bind("Performance", "HideWings", false);
            _textureLimitEntry = _configFile.Bind("Performance", "TextureLimit", (int)GameDefault, "-1 = leave it to the game");
            _shadowDistanceEntry = _configFile.Bind("Performance", "ShadowDistance", GameDefault, "-1 = leave it to the game");
            _lodBiasEntry = _configFile.Bind("Performance", "LodBias", GameDefault, "-1 = leave it to the game");
            _transparencyEntry = _configFile.Bind("Appearance", "Transparency", 0.96f);
            _autoScaleEntry = _configFile.Bind("Appearance", "AutoScale", true);
            _uiScaleEntry = _configFile.Bind("Appearance", "UIScale", 1f);
            _themeIndexEntry = _configFile.Bind("Appearance", "Theme", 0);
            _hidePromoPopupsEntry = _configFile.Bind("Appearance", "HidePromoPopups", false);
            _windowXEntry = _configFile.Bind("Menu", "WindowX", -10000f, "-10000 = not placed yet");
            _windowYEntry = _configFile.Bind("Menu", "WindowY", 0f);
            _sectionEntry = _configFile.Bind("Menu", "Page", 0);
            _welcomeSeenEntry = _configFile.Bind("Menu", "WelcomeSeen", false);
            _overlayEnabledEntry = _configFile.Bind("Overlay", "Enabled", false);
            _overlayCornerEntry = _configFile.Bind("Overlay", "Corner", (int)MiniOverlay.Corner.TopRight);
            _overlayFpsEntry = _configFile.Bind("Overlay", "ShowFps", true);
            _overlayRamEntry = _configFile.Bind("Overlay", "ShowRam", true);
            _overlayActiveEntry = _configFile.Bind("Overlay", "ShowActiveFeatures", false);
            _waypointsEntry = _configFile.Bind("Teleports", "Waypoints", "");
            _crashWorkaroundsEntry = _configFile.Bind("Crashes", "EnabledWorkarounds", "");
            _detailedLoggingEntry = _configFile.Bind("Crashes", "DetailedLogging", false);
            _quizHelperEntry = _configFile.Bind("Teleports", "QuizHelper", true);
            _showPeopleEntry = _configFile.Bind("Rendering", "ShowPeople", 0);
            _fortuneBallsEntry = _configFile.Bind("Fortune", "Balls", "");
            _fortuneNotifyEntry = _configFile.Bind("Fortune", "NotifyWhenFree", true);
            _friendNoticesEntry = _configFile.Bind("Teleports", "FriendArrivalNotices", true);
            _friendLeftNoticesEntry = _configFile.Bind("Teleports", "FriendLeftNotices", false);
            _dismissedUpdateEntry = _configFile.Bind("Updates", "DismissedVersion", "");
            RemoveChatSettingsFromOlderVersions();
        }

        // Older versions stored chat history and ignore words here. BepInEx
        // keeps unbound entries in the file forever, so bind them once and
        // remove them to actually delete that data.
        private void RemoveChatSettingsFromOlderVersions()
        {
            var removedChatSettings = new[] { new ConfigDefinition("Chat", "IgnoreTriggers"), new ConfigDefinition("Chat", "History") };
            bool removedAny = false;
            foreach (ConfigDefinition definition in removedChatSettings)
            {
                _configFile.Bind(definition, "");
                removedAny |= _configFile.Remove(definition);
            }
            if (removedAny)
                _configFile.Save();
        }

        private void ApplyLoadedValuesToControllers()
        {
            _keybinds.MenuKey = _menuKeyEntry.Value;
            _keybinds.FlyUpKey = _flyUpKeyEntry.Value;
            _keybinds.FlyDownKey = _flyDownKeyEntry.Value;
            _hotkeys.ResetToDefaults();
            _hotkeys.Decode(_featureHotkeysEntry.Value);
            _flyController.Speed = _flySpeedEntry.Value;
            _speedBoostController.Multiplier = _speedMultiplierEntry.Value;
            _cameraController.Fov = _fovEntry.Value;
            _cameraController.HideOwnAvatarInScreenshots = _hideAvatarInScreenshotsEntry.Value;

            _performanceController.AutoFreeEnabled = _autoFreeEntry.Value;
            _performanceController.AutoFreeThresholdMB = _autoFreeThresholdEntry.Value;
            _wingsHiderController.Enabled = _hideWingsEntry.Value;
            if (_textureLimitEntry.Value >= 0)
                _performanceController.TextureQualityLimit = _textureLimitEntry.Value;
            if (_shadowDistanceEntry.Value >= 0f)
                _performanceController.ShadowDistance = _shadowDistanceEntry.Value;
            if (_lodBiasEntry.Value >= 0f)
                _performanceController.LodBias = _lodBiasEntry.Value;

            Theme.Alpha = _transparencyEntry.Value;
            Theme.AutoScale = _autoScaleEntry.Value;
            Theme.UIScale = _uiScaleEntry.Value;
            Theme.Index = _themeIndexEntry.Value;
            _menuUI.WindowPosition = new Vector2(_windowXEntry.Value, _windowYEntry.Value);
            _menuUI.SectionIndex = _sectionEntry.Value;
            _welcome.Seen = _welcomeSeenEntry.Value;
            _overlay.Enabled = _overlayEnabledEntry.Value;
            _overlay.Position = (MiniOverlay.Corner)Mathf.Clamp(_overlayCornerEntry.Value, 0, 3);
            _overlay.ShowFps = _overlayFpsEntry.Value;
            _overlay.ShowRam = _overlayRamEntry.Value;
            _overlay.ShowActiveFeatures = _overlayActiveEntry.Value;
            _menuUI.DismissedUpdateVersion = _dismissedUpdateEntry.Value;
            DebugLog.Verbose = _detailedLoggingEntry.Value;
            QuizHelper.Enabled = _quizHelperEntry.Value;
            PeopleFilter.Instance.Current = (PeopleFilter.Mode)Mathf.Clamp(_showPeopleEntry.Value, 0, 2);
            FortuneTracker.Instance.Decode(_fortuneBallsEntry.Value);
            FortuneTracker.Instance.NotifyWhenFree = _fortuneNotifyEntry.Value;
            FriendNotifier.Instance.Enabled = _friendNoticesEntry.Value;
            FriendNotifier.Instance.NotifyLeaving = _friendLeftNoticesEntry.Value;

            _teleportController.DecodeWaypoints(_waypointsEntry.Value);

            _knockbackImmunityController.Enabled = _knockbackImmunityEnabledEntry.Value;
            _bodyRotationLockController.SetEnabled(_bodyRotationLockEnabledEntry.Value);
            _promoPopupController.Enabled = _hidePromoPopupsEntry.Value;

            // Stores just the names that were ON - anything not listed
            // defaults to off, which is also the safe default for a new
            // workaround added after this was last saved.
            string[] enabledNames = _crashWorkaroundsEntry.Value.Split(';');
            foreach (CrashWorkaround workaround in _crashWorkaroundController.Workarounds)
                _crashWorkaroundController.SetEnabled(workaround, enabledNames.Contains(workaround.Name));
        }

        // Settings > Reset all settings. Waypoints are the player's own data,
        // and the welcome tour has already been seen, so both survive.
        public void ResetToDefaults()
        {
            if (_menuKeyEntry == null)
                return;
            if (_performanceController.GraphicsChangedByPlayer)
                _performanceController.ResetGraphicsToGameDefaults();
            var keep = new HashSet<ConfigEntryBase> { _waypointsEntry, _welcomeSeenEntry, _windowXEntry, _windowYEntry };
            foreach (ConfigEntryBase entry in AllEntries())
                if (!keep.Contains(entry))
                    entry.BoxedValue = entry.DefaultValue;
            ApplyLoadedValuesToControllers();
        }

        private IEnumerable<ConfigEntryBase> AllEntries() => new ConfigEntryBase[]
        {
            _menuKeyEntry, _flyUpKeyEntry, _flyDownKeyEntry, _featureHotkeysEntry, _flySpeedEntry, _speedMultiplierEntry,
            _knockbackImmunityEnabledEntry, _bodyRotationLockEnabledEntry, _fovEntry, _hideAvatarInScreenshotsEntry,
            _autoFreeEntry, _autoFreeThresholdEntry, _hideWingsEntry, _textureLimitEntry, _shadowDistanceEntry, _lodBiasEntry,
            _transparencyEntry, _autoScaleEntry, _uiScaleEntry, _themeIndexEntry, _hidePromoPopupsEntry,
            _windowXEntry, _windowYEntry, _sectionEntry, _welcomeSeenEntry,
            _overlayEnabledEntry, _overlayCornerEntry, _overlayFpsEntry, _overlayRamEntry, _overlayActiveEntry,
            _waypointsEntry, _crashWorkaroundsEntry, _detailedLoggingEntry, _quizHelperEntry, _showPeopleEntry, _fortuneNotifyEntry, _friendNoticesEntry, _friendLeftNoticesEntry, _dismissedUpdateEntry,
        };

        public void Save()
        {
            bool loadHasNotRunYet = _menuKeyEntry == null;
            if (loadHasNotRunYet)
                return;

            _menuKeyEntry.Value = _keybinds.MenuKey;
            _flyUpKeyEntry.Value = _keybinds.FlyUpKey;
            _flyDownKeyEntry.Value = _keybinds.FlyDownKey;
            _featureHotkeysEntry.Value = _hotkeys.Encode();
            _flySpeedEntry.Value = _flyController.Speed;
            _speedMultiplierEntry.Value = _speedBoostController.Multiplier;
            _fovEntry.Value = _cameraController.Fov;
            _hideAvatarInScreenshotsEntry.Value = _cameraController.HideOwnAvatarInScreenshots;

            _autoFreeEntry.Value = _performanceController.AutoFreeEnabled;
            _autoFreeThresholdEntry.Value = _performanceController.AutoFreeThresholdMB;
            _hideWingsEntry.Value = _wingsHiderController.Enabled;
            bool graphicsChanged = _performanceController.GraphicsChangedByPlayer;
            _textureLimitEntry.Value = graphicsChanged ? _performanceController.TextureQualityLimit : (int)GameDefault;
            _shadowDistanceEntry.Value = graphicsChanged ? _performanceController.ShadowDistance : GameDefault;
            _lodBiasEntry.Value = graphicsChanged ? _performanceController.LodBias : GameDefault;

            _transparencyEntry.Value = Theme.Alpha;
            _autoScaleEntry.Value = Theme.AutoScale;
            _uiScaleEntry.Value = Theme.UIScale;
            _themeIndexEntry.Value = Theme.Index;
            if (_menuUI.WindowPlaced)
            {
                _windowXEntry.Value = Mathf.Round(_menuUI.WindowPosition.x);
                _windowYEntry.Value = Mathf.Round(_menuUI.WindowPosition.y);
            }
            _sectionEntry.Value = _menuUI.SectionIndex;
            _welcomeSeenEntry.Value = _welcome.Seen;
            _overlayEnabledEntry.Value = _overlay.Enabled;
            _overlayCornerEntry.Value = (int)_overlay.Position;
            _overlayFpsEntry.Value = _overlay.ShowFps;
            _overlayRamEntry.Value = _overlay.ShowRam;
            _overlayActiveEntry.Value = _overlay.ShowActiveFeatures;
            _dismissedUpdateEntry.Value = _menuUI.DismissedUpdateVersion;
            _detailedLoggingEntry.Value = DebugLog.Verbose;
            _quizHelperEntry.Value = QuizHelper.Enabled;
            _showPeopleEntry.Value = (int)PeopleFilter.Instance.Current;
            _fortuneNotifyEntry.Value = FortuneTracker.Instance.NotifyWhenFree;
            _friendNoticesEntry.Value = FriendNotifier.Instance.Enabled;
            _friendLeftNoticesEntry.Value = FriendNotifier.Instance.NotifyLeaving;
            if (FortuneTracker.Instance.Changed)
            {
                FortuneTracker.Instance.Changed = false;
                _fortuneBallsEntry.Value = FortuneTracker.Instance.Encode();
            }
            _waypointsEntry.Value = _teleportController.EncodeWaypoints();
            _knockbackImmunityEnabledEntry.Value = _knockbackImmunityController.Enabled;
            _bodyRotationLockEnabledEntry.Value = _bodyRotationLockController.Enabled;
            _crashWorkaroundsEntry.Value = string.Join(";",
                _crashWorkaroundController.Workarounds.Where(w => w.Enabled).Select(w => w.Name));
            _hidePromoPopupsEntry.Value = _promoPopupController.Enabled;
        }
    }
}
