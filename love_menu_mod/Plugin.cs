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
        private readonly RenderTweaks _renderTweaks = new RenderTweaks();
        private MiniOverlay _overlay;
        private UpdateChecker _updateChecker;
        private MenuUI _menuUI;
        private WelcomeScreen _welcome;
        private CollectionLogger _collectionLogger;
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
            ErrorLogger.Start(System.IO.Path.Combine(Paths.BepInExRootPath, "LoveMenu"));
            GraphicsApiSetting.Load(System.IO.Path.Combine(Paths.BepInExRootPath, "LoveMenu"));
            ChatLog.Instance.Folder = System.IO.Path.Combine(Paths.BepInExRootPath, "LoveMenu", "chat");
            LagRecorder.Instance.Folder = System.IO.Path.Combine(Paths.BepInExRootPath, "LoveMenu");
            ConsoleQuickEditGuard.Apply();
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
            Minimap.Instance.SetTeleport(_teleportController.GoToMapPoint);
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
            _collectionLogger = new CollectionLogger(_playerContext, System.IO.Path.Combine(Paths.BepInExRootPath, "LoveMenu"));
            _menuUI.Collections = _collectionLogger;
            WorldScriptCapture.Folder = System.IO.Path.Combine(Paths.BepInExRootPath, "LoveMenu", "captured-scripts");
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
            _hotkeys.Register("nextpickup", "Go to next item", KeyCode.None,
                () => Toasts.Show(_teleportController.GoToNextCollectible()));
            _hotkeys.Register("questgiver", "Go to quest giver", KeyCode.None,
                () => Toasts.Show(_teleportController.GoToQuestGiver()));
            _hotkeys.Register("gotosafe", "Go to safe", KeyCode.None,
                () => Toasts.Show(_teleportController.GoToSafe()));
            _hotkeys.Register("back", "Back (undo teleport)", KeyCode.None,
                () => Toasts.Show(_teleportController.GoBack()));
            _hotkeys.Register("minimap", "Minimap", KeyCode.None,
                () => { Minimap.Instance.Enabled = !Minimap.Instance.Enabled; Toasts.Show(Minimap.Instance.Enabled ? "Minimap on" : "Minimap off"); });
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
                Name = "Pose load fix",
                Page = WorkaroundPage.Glitches,
                OnByDefault = true,
                Description = "Joining a room where people are already posing or dancing showed them inside each other: they loaded " +
                    "before their pose partner and the game never attached them. Now they're attached as soon as the partner loads.",
                Enabled = false,
                OnToggle = enabled => PoseParentFix.Enabled = enabled,
            });

            _crashWorkaroundController.Workarounds.Add(new CrashWorkaround
            {
                Name = "Chat overlap fix",
                Page = WorkaroundPage.Glitches,
                OnByDefault = true,
                Description = "Two chat channels drawn on top of each other: a channel added later (a group, a private message, " +
                    "channels re-joined after a room change) showed its messages over the one you were reading. Now only " +
                    "the channel you picked is shown. The chat loads at login, so this applies after restarting the game.",
                Enabled = false,
                OnToggle = enabled => ScriptFixes.ChatOverlapFix = enabled,
            });

            _crashWorkaroundController.Workarounds.Add(new CrashWorkaround
            {
                Name = "Chat link click fix",
                Page = WorkaroundPage.Glitches,
                OnByDefault = true,
                Description = "Clicking a link in chat sometimes put the sender's name in the chat box instead of opening it: the " +
                    "game measured the text differently from how it's drawn. Now the link under the mouse is found from the " +
                    "drawn text itself.",
                Enabled = false,
                OnToggle = enabled => ChatLinkClickFix.Enabled = enabled,
            });

            _crashWorkaroundController.Workarounds.Add(new CrashWorkaround
            {
                Name = "Clothing remove fix",
                Page = WorkaroundPage.Glitches,
                OnByDefault = true,
                Description = "\"Items won't come off\": unticking an accessory in a different Accessories tab than the one it's " +
                    "worn in removed nothing, and it couldn't be put on again. Now it comes off from the slot it's really in. " +
                    "Applies the next time the dressing room opens.",
                Enabled = false,
                OnToggle = enabled => ScriptFixes.ClothingRemoveFix = enabled,
            });

            _crashWorkaroundController.Workarounds.Add(new CrashWorkaround
            {
                Name = "DynamicBones settings fix",
                Description = "Some avatars send their hair and cloth physics settings in a nested list the game " +
                    "rejects (\"Cannot deserialize the current JSON array\"), so they get none. The list is now " +
                    "flattened first. This error came right before two confirmed crashes.",
                Enabled = false,
                Page = WorkaroundPage.Glitches,
                OnToggle = enabled => DynamicBonesJsonFixPatch.Enabled = enabled,
            });

            _crashWorkaroundController.Workarounds.Add(new CrashWorkaround
            {
                Name = "Wrong-asset load fix",
                Description = "Some objects (Scripted_menu_Bulava) failed to load because the game picked their " +
                    "animator instead of their model from the download. It now falls back to the model.",
                Enabled = false,
                Page = WorkaroundPage.Glitches,
                OnToggle = enabled => WrongMainAssetFixPatch.Enabled = enabled,
            });

            _crashWorkaroundController.Workarounds.Add(new CrashWorkaround
            {
                Name = "Missing-parent load fix",
                Description = "Some lights and effects on avatars and pets (Light_Chest, Light_Tail) failed to load " +
                    "with a NullReferenceException when they arrived before their avatar. They now load using the " +
                    "game's own fallback, though they may sit slightly off.",
                Enabled = false,
                Page = WorkaroundPage.Glitches,
                OnToggle = enabled => MissingParentLoadPatch.Enabled = enabled,
            });

            _crashWorkaroundController.Workarounds.Add(new CrashWorkaround
            {
                Id = "smooth-loading",
                Name = "Smooth loading",
                Description = "When people arrive, the game spends up to 200 ms per frame building their avatars, " +
                    "a stutter every time someone joins. It now spends at most " + LoadSmoothing.BudgetMs.ToString("0") +
                    " ms per frame. People take a little longer to appear.",
                Enabled = false,
                Page = WorkaroundPage.Rendering,
                OnToggle = enabled => LoadSmoothing.Enabled = enabled,
            });

            _crashWorkaroundController.Workarounds.Add(new CrashWorkaround
            {
                Id = "lamp-shadows",
                Name = "Only the sun casts shadows",
                Description = "Lamps and spotlights stop casting shadows. Each one that does draws the room six " +
                    "more times, so indoor rooms with many lamps gain the most.",
                Enabled = false,
                Page = WorkaroundPage.Rendering,
                OnToggle = enabled => { _renderTweaks.LampShadowsOff = enabled; _renderTweaks.ApplyNow(); },
            });

            _crashWorkaroundController.Workarounds.Add(new CrashWorkaround
            {
                Id = "avatar-distance",
                Name = "Avatar draw distance " + RenderTweaks.AvatarDrawDistance.ToString("0") + " m",
                Description = "Avatars and NPCs further than " + RenderTweaks.AvatarDrawDistance.ToString("0") +
                    " m from the camera aren't drawn. Helps in crowded hubs. Zooming out further hides your own avatar too.",
                Enabled = false,
                Page = WorkaroundPage.Rendering,
                OnToggle = enabled => { _renderTweaks.AvatarDistance = enabled; _renderTweaks.ApplyNow(); },
            });

            _crashWorkaroundController.Workarounds.Add(new CrashWorkaround
            {
                Id = "mirrors",
                Name = "Cheaper mirrors",
                Description = "Mirrors redraw the whole room every frame. They now update every other frame, " +
                    "without shadows in the reflection.",
                Enabled = false,
                Page = WorkaroundPage.Rendering,
                OnToggle = enabled => RenderTweaks.CheaperMirrors = enabled,
            });

            _crashWorkaroundController.Workarounds.Add(new CrashWorkaround
            {
                Id = "probe-refresh",
                Name = "No reflection refresh hitch",
                Description = "Some rooms redraw their reflections (six views of the room each) once a minute, " +
                    "a short stutter. They are now drawn once when the room loads.",
                Enabled = false,
                Page = WorkaroundPage.Rendering,
                OnToggle = enabled => RenderTweaks.NoProbeRefresh = enabled,
            });

            _crashWorkaroundController.Workarounds.Add(new CrashWorkaround
            {
                Id = "decals",
                Name = "Decals for the main view only",
                Description = "In rooms with decals, every camera (mirrors, water reflections) redrew the room one " +
                    "to three extra times for them. Now only your main view does. Decals in reflections may look off.",
                Enabled = false,
                Page = WorkaroundPage.Rendering,
                OnToggle = enabled => DecalsMainCameraOnlyPatch.Enabled = enabled,
            });

            _crashWorkaroundController.Workarounds.Add(new CrashWorkaround
            {
                Id = "sky-effects",
                Name = "Sky effects off",
                Description = "Turns off the sky's sun shafts, sky blur and temporal reprojection passes, which run " +
                    "on top of the game's own post-processing.",
                Enabled = false,
                Page = WorkaroundPage.Rendering,
                OnToggle = enabled => { _renderTweaks.SkyEffectsOff = enabled; _renderTweaks.ApplyNow(); },
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
            ErrorLogger.Tick();
            LagRecorder.Instance.Tick();
            PoseRecorder.Instance.Tick(_playerContext);
            PoseParentFix.Tick();
            GraphicsApiSetting.Tick();

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
            long __t;
            __t = LagRecorder.Begin();
            _flyController.Tick(Typing, _keybinds.FlyUpKey, _keybinds.FlyDownKey);
            LagRecorder.End("fly", __t);
            __t = LagRecorder.Begin();
            _knockbackImmunityController.Tick(_flyController.Flying);
            LagRecorder.End("knockbackImmunity", __t);
            __t = LagRecorder.Begin();
            _bodyRotationLockController.Tick();
            LagRecorder.End("bodyRotationLock", __t);
            __t = LagRecorder.Begin();
            _speedBoostController.Tick(Typing, _flyController.Flying);
            LagRecorder.End("speedBoost", __t);
            __t = LagRecorder.Begin();
            _cameraController.Tick();
            LagRecorder.End("camera", __t);
            __t = LagRecorder.Begin();
            _systemStatsController.Tick();
            LagRecorder.End("systemStats", __t);
            __t = LagRecorder.Begin();
            _promoPopupController.Tick(Time.deltaTime, _playerContext.Avatar != null, _playerContext.TypingInChat);
            LagRecorder.End("promoPopup", __t);
            __t = LagRecorder.Begin();
            _uiDebugController.Tick(Time.deltaTime);
            LagRecorder.End("uiDebug", __t);
            __t = LagRecorder.Begin();
            _performanceController.Tick(Time.deltaTime);
            LagRecorder.End("performance", __t);
            __t = LagRecorder.Begin();
            _wingsHiderController.Tick(Time.deltaTime);
            LagRecorder.End("wingsHider", __t);
            __t = LagRecorder.Begin();
            _renderTweaks.Tick();
            LagRecorder.End("renderTweaks", __t);
            __t = LagRecorder.Begin();
            FpsBenchmark.Tick(Time.unscaledDeltaTime);
            LagRecorder.End("FpsBenchmark", __t);
            __t = LagRecorder.Begin();
            FriendNotifier.Instance.Tick();
            LagRecorder.End("FriendNotifier", __t);
            __t = LagRecorder.Begin();
            RoomScan.Instance.Tick();
            LagRecorder.End("RoomScan", __t);
            __t = LagRecorder.Begin();
            Minimap.Instance.Tick(_playerContext);
            LagRecorder.End("Minimap", __t);
            __t = LagRecorder.Begin();
            NameTagDistance.Instance.Tick();
            LagRecorder.End("NameTagDistance", __t);
            __t = LagRecorder.Begin();
            AutoCrowd.Instance.Tick();
            LagRecorder.End("AutoCrowd", __t);
            __t = LagRecorder.Begin();
            ChatLog.Instance.Tick(_playerContext.PlayerName);
            LagRecorder.End("ChatLog", __t);
            __t = LagRecorder.Begin();
            PeopleFilter.Instance.Tick();
            LagRecorder.End("PeopleFilter", __t);
            __t = LagRecorder.Begin();
            FortuneTracker.Instance.Tick();
            LagRecorder.End("FortuneTracker", __t);
            __t = LagRecorder.Begin();
            _collectionLogger.Tick();
            LagRecorder.End("collectionLogger", __t);
            __t = LagRecorder.Begin();
            QuizHelper.Tick();
            LagRecorder.End("QuizHelper", __t);
            __t = LagRecorder.Begin();
            _updateChecker.Tick();
            LagRecorder.End("updateChecker", __t);
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
            long __draw = LagRecorder.Begin();
            _menuUI.Styles.Rebuild(_menuUI.Theme);
            _overlay.Draw(_menuUI.Styles);
            Minimap.Instance.Draw(_menuUI.Styles, _playerContext);
            QuizHelper.Draw(_menuUI.Styles);
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
