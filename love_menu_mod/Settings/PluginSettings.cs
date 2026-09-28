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
        private readonly FlyController _flyController;
        private readonly SpeedBoostController _speedBoostController;
        private readonly TeleportController _teleportController;
        private readonly KnockbackImmunityController _knockbackImmunityController;
        private readonly BodyRotationLockController _bodyRotationLockController;
        private readonly CrashWorkaroundController _crashWorkaroundController;
        private readonly PromoPopupController _promoPopupController;
        private readonly Theme _theme;

        private ConfigEntry<KeyCode> _menuKeyEntry, _flyUpKeyEntry, _flyDownKeyEntry;
        private ConfigEntry<float> _flySpeedEntry, _speedMultiplierEntry, _transparencyEntry, _uiScaleEntry;
        private ConfigEntry<int> _themeIndexEntry;
        private ConfigEntry<string> _waypointsEntry;
        private ConfigEntry<bool> _knockbackImmunityEnabledEntry;
        private ConfigEntry<bool> _bodyRotationLockEnabledEntry;
        private ConfigEntry<string> _crashWorkaroundsEntry;
        private ConfigEntry<bool> _hidePromoPopupsEntry;

        public PluginSettings(ConfigFile configFile, Keybinds keybinds, FlyController flyController, SpeedBoostController speedBoostController,
            TeleportController teleportController, KnockbackImmunityController knockbackImmunityController,
            BodyRotationLockController bodyRotationLockController, CrashWorkaroundController crashWorkaroundController,
            PromoPopupController promoPopupController, Theme theme)
        {
            _configFile = configFile;
            _keybinds = keybinds;
            _flyController = flyController;
            _speedBoostController = speedBoostController;
            _teleportController = teleportController;
            _knockbackImmunityController = knockbackImmunityController;
            _bodyRotationLockController = bodyRotationLockController;
            _crashWorkaroundController = crashWorkaroundController;
            _promoPopupController = promoPopupController;
            _theme = theme;
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
            _flySpeedEntry = _configFile.Bind("Movement", "FlySpeed", 6f);
            _speedMultiplierEntry = _configFile.Bind("Movement", "SpeedMultiplier", 1.5f);
            _transparencyEntry = _configFile.Bind("Appearance", "Transparency", 0.96f);
            _uiScaleEntry = _configFile.Bind("Appearance", "UIScale", 1f);
            _themeIndexEntry = _configFile.Bind("Appearance", "Theme", 0);
            _waypointsEntry = _configFile.Bind("Teleports", "Waypoints", "");
            _knockbackImmunityEnabledEntry = _configFile.Bind("Movement", "KnockbackImmunity", false);
            _bodyRotationLockEnabledEntry = _configFile.Bind("Movement", "BodyRotationLock", false);
            _crashWorkaroundsEntry = _configFile.Bind("Crashes", "EnabledWorkarounds", "");
            _hidePromoPopupsEntry = _configFile.Bind("Appearance", "HidePromoPopups", false);
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
            _flyController.Speed = _flySpeedEntry.Value;
            _speedBoostController.Multiplier = _speedMultiplierEntry.Value;
            _theme.Alpha = _transparencyEntry.Value;
            _theme.UIScale = _uiScaleEntry.Value;
            _theme.Index = _themeIndexEntry.Value;

            _teleportController.DecodeWaypoints(_waypointsEntry.Value);

            _knockbackImmunityController.Enabled = _knockbackImmunityEnabledEntry.Value;
            _bodyRotationLockController.SetEnabled(_bodyRotationLockEnabledEntry.Value);
            _promoPopupController.Enabled = _hidePromoPopupsEntry.Value;

            // Stores just the names that were ON - anything not listed
            // defaults to off, which is also the safe default for a new
            // workaround added after this was last saved.
            string[] enabledNames = _crashWorkaroundsEntry.Value.Split(';');
            foreach (CrashWorkaround workaround in _crashWorkaroundController.Workarounds)
                if (enabledNames.Contains(workaround.Name))
                    _crashWorkaroundController.SetEnabled(workaround, true);
        }

        public void Save()
        {
            bool loadHasNotRunYet = _menuKeyEntry == null;
            if (loadHasNotRunYet)
                return;

            _menuKeyEntry.Value = _keybinds.MenuKey;
            _flyUpKeyEntry.Value = _keybinds.FlyUpKey;
            _flyDownKeyEntry.Value = _keybinds.FlyDownKey;
            _flySpeedEntry.Value = _flyController.Speed;
            _speedMultiplierEntry.Value = _speedBoostController.Multiplier;
            _transparencyEntry.Value = _theme.Alpha;
            _uiScaleEntry.Value = _theme.UIScale;
            _themeIndexEntry.Value = _theme.Index;
            _waypointsEntry.Value = _teleportController.EncodeWaypoints();
            _knockbackImmunityEnabledEntry.Value = _knockbackImmunityController.Enabled;
            _bodyRotationLockEnabledEntry.Value = _bodyRotationLockController.Enabled;
            _crashWorkaroundsEntry.Value = string.Join(";",
                _crashWorkaroundController.Workarounds.Where(w => w.Enabled).Select(w => w.Name));
            _hidePromoPopupsEntry.Value = _promoPopupController.Enabled;
        }
    }
}
