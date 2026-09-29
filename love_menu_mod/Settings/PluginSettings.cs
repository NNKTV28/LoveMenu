using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;
using FlyMod.Features;
using FlyMod.Inputs;
using FlyMod.UI;

namespace FlyMod.Settings
{
    // The only place that talks to BepInEx's ConfigFile - everything else
    // just holds live state and gets read from / written into here.
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
        private readonly MiniOverlay _overlay;
        private readonly MenuUI _menuUI;
        private Theme Theme => _menuUI.Theme;

        private ConfigEntry<KeyCode> _menuKeyEntry, _flyUpKeyEntry, _flyDownKeyEntry;
        private ConfigEntry<float> _flySpeedEntry, _speedMultiplierEntry, _transparencyEntry, _uiScaleEntry, _fovEntry;
        private ConfigEntry<int> _themeIndexEntry, _overlayCornerEntry;
        private ConfigEntry<string> _waypointsEntry, _crashWorkaroundsEntry, _featureHotkeysEntry, _dismissedUpdateEntry;
        private ConfigEntry<bool> _knockbackImmunityEnabledEntry, _bodyRotationLockEnabledEntry, _hidePromoPopupsEntry;
        private ConfigEntry<bool> _autoScaleEntry, _hideAvatarInScreenshotsEntry;
        private ConfigEntry<bool> _overlayEnabledEntry, _overlayFpsEntry, _overlayRamEntry, _overlayActiveEntry;

        public PluginSettings(ConfigFile configFile, Keybinds keybinds, FeatureHotkeys hotkeys, FlyController flyController,
            SpeedBoostController speedBoostController, TeleportController teleportController,
            KnockbackImmunityController knockbackImmunityController, BodyRotationLockController bodyRotationLockController,
            CameraController cameraController, CrashWorkaroundController crashWorkaroundController,
            PromoPopupController promoPopupController, MiniOverlay overlay, MenuUI menuUI)
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
            _overlay = overlay;
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
            _transparencyEntry = _configFile.Bind("Appearance", "Transparency", 0.96f);
            _autoScaleEntry = _configFile.Bind("Appearance", "AutoScale", true);
            _uiScaleEntry = _configFile.Bind("Appearance", "UIScale", 1f);
            _themeIndexEntry = _configFile.Bind("Appearance", "Theme", 0);
            _hidePromoPopupsEntry = _configFile.Bind("Appearance", "HidePromoPopups", false);
            _overlayEnabledEntry = _configFile.Bind("Overlay", "Enabled", false);
            _overlayCornerEntry = _configFile.Bind("Overlay", "Corner", (int)MiniOverlay.Corner.TopRight);
            _overlayFpsEntry = _configFile.Bind("Overlay", "ShowFps", true);
            _overlayRamEntry = _configFile.Bind("Overlay", "ShowRam", true);
            _overlayActiveEntry = _configFile.Bind("Overlay", "ShowActiveFeatures", false);
            _waypointsEntry = _configFile.Bind("Teleports", "Waypoints", "");
            _crashWorkaroundsEntry = _configFile.Bind("Crashes", "EnabledWorkarounds", "");
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
            Theme.Alpha = _transparencyEntry.Value;
            Theme.AutoScale = _autoScaleEntry.Value;
            Theme.UIScale = _uiScaleEntry.Value;
            Theme.Index = _themeIndexEntry.Value;
            _overlay.Enabled = _overlayEnabledEntry.Value;
            _overlay.Position = (MiniOverlay.Corner)Mathf.Clamp(_overlayCornerEntry.Value, 0, 3);
            _overlay.ShowFps = _overlayFpsEntry.Value;
            _overlay.ShowRam = _overlayRamEntry.Value;
            _overlay.ShowActiveFeatures = _overlayActiveEntry.Value;
            _menuUI.DismissedUpdateVersion = _dismissedUpdateEntry.Value;

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
        // not a setting, so they survive a reset.
        public void ResetToDefaults()
        {
            if (_menuKeyEntry == null)
                return;
            var keep = new HashSet<ConfigEntryBase> { _waypointsEntry };
            foreach (ConfigEntryBase entry in AllEntries())
                if (!keep.Contains(entry))
                    entry.BoxedValue = entry.DefaultValue;
            ApplyLoadedValuesToControllers();
        }

        private IEnumerable<ConfigEntryBase> AllEntries() => new ConfigEntryBase[]
        {
            _menuKeyEntry, _flyUpKeyEntry, _flyDownKeyEntry, _featureHotkeysEntry, _flySpeedEntry, _speedMultiplierEntry,
            _knockbackImmunityEnabledEntry, _bodyRotationLockEnabledEntry, _fovEntry, _hideAvatarInScreenshotsEntry,
            _transparencyEntry, _autoScaleEntry, _uiScaleEntry, _themeIndexEntry, _hidePromoPopupsEntry,
            _overlayEnabledEntry, _overlayCornerEntry, _overlayFpsEntry, _overlayRamEntry, _overlayActiveEntry,
            _waypointsEntry, _crashWorkaroundsEntry, _dismissedUpdateEntry,
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
            _transparencyEntry.Value = Theme.Alpha;
            _autoScaleEntry.Value = Theme.AutoScale;
            _uiScaleEntry.Value = Theme.UIScale;
            _themeIndexEntry.Value = Theme.Index;
            _overlayEnabledEntry.Value = _overlay.Enabled;
            _overlayCornerEntry.Value = (int)_overlay.Position;
            _overlayFpsEntry.Value = _overlay.ShowFps;
            _overlayRamEntry.Value = _overlay.ShowRam;
            _overlayActiveEntry.Value = _overlay.ShowActiveFeatures;
            _dismissedUpdateEntry.Value = _menuUI.DismissedUpdateVersion;
            _waypointsEntry.Value = _teleportController.EncodeWaypoints();
            _knockbackImmunityEnabledEntry.Value = _knockbackImmunityController.Enabled;
            _bodyRotationLockEnabledEntry.Value = _bodyRotationLockController.Enabled;
            _crashWorkaroundsEntry.Value = string.Join(";",
                _crashWorkaroundController.Workarounds.Where(w => w.Enabled).Select(w => w.Name));
            _hidePromoPopupsEntry.Value = _promoPopupController.Enabled;
        }
    }
}
