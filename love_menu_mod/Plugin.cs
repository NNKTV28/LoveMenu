using System;
using System.Collections.Generic;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using FlyMod.Core;
using FlyMod.Features;
using FlyMod.Inputs;
using FlyMod.Settings;
using FlyMod.UI;

namespace FlyMod
{
    // Thin orchestrator: owns the controllers and wires their Tick/Draw
    // calls into Unity's lifecycle. Feature logic lives in Features/, menu
    // rendering in UI/, persistence in Settings/.
    [BepInPlugin("local.flymod", "Love Menu", MenuUI.Version)]
    public class LoveMenuPlugin : BaseUnityPlugin
    {
        private readonly PlayerContext _playerContext = new PlayerContext();
        private readonly Keybinds _keybinds = new Keybinds();
        private readonly FeatureHotkeys _hotkeys = new FeatureHotkeys();

        private FlyController _flyController;
        private SpeedBoostController _speedBoostController;
        private TeleportController _teleportController;
        private KnockbackImmunityController _knockbackImmunityController;
        private BodyRotationLockController _bodyRotationLockController;
        private CameraController _cameraController;
        private CrashWorkaroundController _crashWorkaroundController;
        private SystemStatsController _systemStatsController;
        private CrashDumpController _crashDumpController;
        private PromoPopupController _promoPopupController;
        private UiDebugController _uiDebugController;
        private PerformanceController _performanceController;
        private WingsHiderController _wingsHiderController;
        private MiniOverlay _overlay;
        private UpdateChecker _updateChecker;
        private MenuUI _menuUI;
        private WelcomeScreen _welcome;
        private PluginSettings _pluginSettings;

        // Settings are saved every couple of seconds rather than only when the
        // menu closes, so a crash doesn't lose them. Unchanged values don't
        // touch the file.
        private const float AutoSaveSeconds = 2f;
        private float _nextAutoSave;

        // The launcher drops this file right before starting the game. A
        // normal Steam launch never writes it, so the menu stays out of the
        // way unless the player deliberately started it through the launcher.
        private const string LaunchMarkerFileName = "LoveMenu.launch";
        private static readonly TimeSpan LaunchMarkerMaxAge = TimeSpan.FromMinutes(5);

        private void Awake()
        {
            if (!WasStartedFromLauncher())
            {
                Logger.LogInfo("Not started from the Love Menu launcher - staying inactive.");
                enabled = false;
                return;
            }

            DebugLog.Init(Logger);
            ConsoleQuickEditGuard.Apply();
            DynamicBonesCrashGuardPatch.Init(Logger);
            ApplyHarmonyPatches();
            CreateControllers();
        }

        // Consumes the marker so a later plain Steam launch doesn't pick it up.
        private bool WasStartedFromLauncher()
        {
            string markerPath = System.IO.Path.Combine(Paths.BepInExRootPath, LaunchMarkerFileName);
            try
            {
                if (!System.IO.File.Exists(markerPath))
                    return false;
                DateTime writtenAt = System.IO.File.GetLastWriteTimeUtc(markerPath);
                System.IO.File.Delete(markerPath);
                return DateTime.UtcNow - writtenAt < LaunchMarkerMaxAge;
            }
            catch (Exception exception)
            {
                Logger.LogWarning("Could not read launch marker: " + exception.Message);
                return false;
            }
        }

        private void ApplyHarmonyPatches()
        {
            try
            {
                new Harmony("local.flymod").PatchAll();
            }
            catch (Exception exception)
            {
                Logger.LogWarning("Harmony patch failed: " + exception.Message);
            }
        }

        private void CreateControllers()
        {
            _flyController = new FlyController(_playerContext);
            _speedBoostController = new SpeedBoostController(_playerContext);
            _teleportController = new TeleportController(_playerContext);
            _knockbackImmunityController = new KnockbackImmunityController();
            _bodyRotationLockController = new BodyRotationLockController();
            _cameraController = new CameraController(_playerContext);
            _crashWorkaroundController = new CrashWorkaroundController();
            _systemStatsController = new SystemStatsController();
            _crashDumpController = new CrashDumpController();
            _crashDumpController.Init(Logger, System.IO.Path.Combine(Paths.GameRootPath, "BepInEx", "CrashDumps"));
            _promoPopupController = new PromoPopupController(Logger);
            _uiDebugController = new UiDebugController(Logger);
            _performanceController = new PerformanceController(Logger, _systemStatsController);
            _wingsHiderController = new WingsHiderController(Logger);
            _overlay = new MiniOverlay(_systemStatsController, ActiveFeatureNames);
            _updateChecker = new UpdateChecker(MenuUI.Version);
            RegisterCrashWorkarounds();
            RegisterFeatureHotkeys();
            _menuUI = new MenuUI(_playerContext, _keybinds, _hotkeys, _flyController,
                _speedBoostController, _teleportController, _knockbackImmunityController,
                _bodyRotationLockController, _cameraController, _crashWorkaroundController, _systemStatsController,
                _promoPopupController, _performanceController, _wingsHiderController,
                _overlay, _updateChecker, _crashDumpController.DumpFolder, SavePluginSettings, ResetAllSettings);
            _welcome = new WelcomeScreen(_menuUI.Theme, _keybinds, _overlay, () => _playerContext.PlayerName,
                () => _playerContext.Avatar != null, () => _menuUI.SetOpen(true));
            _menuUI.OnShowWelcome = _welcome.Show;
            _pluginSettings = new PluginSettings(Config, _keybinds, _hotkeys, _flyController, _speedBoostController,
                _teleportController, _knockbackImmunityController, _bodyRotationLockController, _cameraController,
                _crashWorkaroundController, _promoPopupController, _performanceController, _wingsHiderController,
                _overlay, _welcome, _menuUI);
        }

        private void SavePluginSettings() => _pluginSettings.Save();

        private void ResetAllSettings()
        {
            _pluginSettings.ResetToDefaults();
            _pluginSettings.Save();
        }

        private void RegisterFeatureHotkeys()
        {
            _hotkeys.Register("fly", "Fly", KeyCode.None, () =>
            {
                _flyController.Toggle();
                Toasts.Show(_flyController.Flying ? "Fly on" : "Fly off");
            });
            _hotkeys.Register("speed", "Movement speed", KeyCode.None, () =>
            {
                _speedBoostController.SetEnabled(!_speedBoostController.Enabled);
                Toasts.Show(_speedBoostController.Enabled ? "Movement speed on" : "Movement speed off");
            });
            _hotkeys.Register("knockback", "Knockback immunity", KeyCode.None, () =>
            {
                _knockbackImmunityController.Enabled = !_knockbackImmunityController.Enabled;
                Toasts.Show(_knockbackImmunityController.Enabled ? "Knockback immunity on" : "Knockback immunity off");
            });
            _hotkeys.Register("wings", "Hide wings", KeyCode.None, () =>
            {
                _wingsHiderController.Enabled = !_wingsHiderController.Enabled;
                Toasts.Show(_wingsHiderController.Enabled ? "Hide wings on" : "Hide wings off");
            });
            _hotkeys.Register("screenshot", "Screenshot mode", KeyCode.F9,
                () => _cameraController.SetScreenshotMode(!_cameraController.ScreenshotMode));
        }

        private IEnumerable<string> ActiveFeatureNames()
        {
            if (_flyController.Flying) yield return "Fly";
            if (_speedBoostController.Enabled) yield return "Speed " + _speedBoostController.Multiplier.ToString("0.0") + "x";
            if (_knockbackImmunityController.Enabled) yield return "Knockback";
            if (_bodyRotationLockController.Enabled) yield return "Body lock";
            if (_wingsHiderController.Enabled) yield return "No wings";
        }

        private void RegisterCrashWorkarounds()
        {
            _crashWorkaroundController.Workarounds.Add(new CrashWorkaround
            {
                Name = "DynamicBones JSON guard",
                Description = "Hides the DynamicBones JSON error seen right before two confirmed crashes.",
                Enabled = false,
                OnToggle = enabled => DynamicBonesCrashGuardPatch.Enabled = enabled,
            });

            _crashWorkaroundController.ConfirmedFixes.Add(new ConfirmedCrashFix
            {
                Name = "Console click freeze",
                Description = "Clicking the BepInEx console window started a text selection, and Windows paused " +
                    "the game's logging - and with it the whole game - until it ended. QuickEdit is now turned " +
                    "off at startup.",
            });

            _crashWorkaroundController.ConfirmedFixes.Add(new ConfirmedCrashFix
            {
                Name = "Menu texture leak",
                Description = "The menu created new textures every frame and never freed them, until the GPU " +
                    "ran out (\"Resource ID out of range\"). Textures are now only rebuilt when the look changes.",
            });
        }

        private void Start()
        {
            _pluginSettings.Load();
            _updateChecker.Begin();
        }

        private void Update()
        {
            _crashDumpController.Heartbeat();

            if (_keybinds.CaptureIfRebinding())
                return;
            if (_hotkeys.CaptureIfListening(_keybinds.MenuKey))
                return;

            HandleMenuToggleKey();

            _playerContext.UpdateForThisFrame();
            _hotkeys.Tick(blocked: Typing || _welcome.Visible);
            _welcome.Tick();

            TickAllFeatures();

            if (Time.unscaledTime >= _nextAutoSave)
            {
                _nextAutoSave = Time.unscaledTime + AutoSaveSeconds;
                _pluginSettings.Save();
            }
        }

        // Typing in the game's chat or in one of the menu's text boxes: no
        // movement keys, no hotkeys.
        private bool Typing => _playerContext.TypingInChat || _menuUI.IsTyping;

        // In screenshot mode the menu key brings everything back instead of
        // opening the menu on top of the clean view. During the welcome tour
        // the tour takes the key (its "try it" step).
        private void HandleMenuToggleKey()
        {
            if (!Input.GetKeyDown(_keybinds.MenuKey) || _menuUI.IsTyping)
                return;
            if (_welcome.TakeMenuKey())
                return;

            if (_cameraController.ScreenshotMode)
            {
                _cameraController.SetScreenshotMode(false);
                return;
            }
            _menuUI.ToggleOpen();
        }

        private void TickAllFeatures()
        {
            _flyController.Tick(Typing, _keybinds.FlyUpKey, _keybinds.FlyDownKey);
            _knockbackImmunityController.Tick(_flyController.Flying);
            _bodyRotationLockController.Tick();
            _speedBoostController.Tick(Typing, _flyController.Flying);
            _cameraController.Tick();
            _systemStatsController.Tick();
            _promoPopupController.Tick(Time.deltaTime, _playerContext.Avatar != null, _playerContext.TypingInChat);
            _uiDebugController.Tick(Time.deltaTime);
            _performanceController.Tick(Time.deltaTime);
            _wingsHiderController.Tick(Time.deltaTime);
            _updateChecker.Tick();
        }

        private void OnGUI()
        {
            if (_cameraController.ScreenshotMode)
            {
                if (_menuUI.Open)
                    _menuUI.SetOpen(false);
                return;
            }

            // The overlay shows whether or not the menu is open, drawn first
            // so the menu window sits on top of it.
            _menuUI.Styles.Rebuild(_menuUI.Theme);
            _overlay.Draw(_menuUI.Styles);
            _menuUI.Draw();
            _welcome.Draw(_menuUI.Styles);
            if (!_menuUI.Open)
                Toasts.Draw(_menuUI.Styles, null);
        }

        private void OnDestroy()
        {
            // Awake bailed out before creating anything.
            if (_menuUI == null)
                return;

            _flyController.SetFlying(false);
            _speedBoostController.SetEnabled(false);
            _bodyRotationLockController.SetEnabled(false);
            _cameraController.Shutdown();
            _pluginSettings.Save();
            _crashDumpController.Shutdown();
            _systemStatsController.Shutdown();
        }
    }
}
