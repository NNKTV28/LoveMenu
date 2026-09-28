using System;
using System.Reflection;
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
    [BepInPlugin("local.flymod", "Love Menu", "1.1.1")]
    public class LoveMenuPlugin : BaseUnityPlugin
    {
        private readonly PlayerContext _playerContext = new PlayerContext();
        private readonly Keybinds _keybinds = new Keybinds();

        private FlyController _flyController;
        private SpeedBoostController _speedBoostController;
        private TeleportController _teleportController;
        private KnockbackImmunityController _knockbackImmunityController;
        private BodyRotationLockController _bodyRotationLockController;
        private CrashWorkaroundController _crashWorkaroundController;
        private SystemStatsController _systemStatsController;
        private CrashDumpController _crashDumpController;
        private PromoPopupController _promoPopupController;
        private UiDebugController _uiDebugController;
        private PerformanceController _performanceController;
        private WingsHiderController _wingsHiderController;
        private MenuUI _menuUI;
        private PluginSettings _pluginSettings;

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
            _crashWorkaroundController = new CrashWorkaroundController();
            _systemStatsController = new SystemStatsController();
            _crashDumpController = new CrashDumpController();
            _crashDumpController.Init(Logger, System.IO.Path.Combine(Paths.GameRootPath, "BepInEx", "CrashDumps"));
            _promoPopupController = new PromoPopupController(Logger);
            _uiDebugController = new UiDebugController(Logger);
            _performanceController = new PerformanceController(Logger, _systemStatsController);
            _wingsHiderController = new WingsHiderController(Logger);
            RegisterCrashWorkarounds();
            _menuUI = new MenuUI(_playerContext, _keybinds, _flyController,
                _speedBoostController, _teleportController, _knockbackImmunityController,
                _bodyRotationLockController, _crashWorkaroundController, _systemStatsController,
                _promoPopupController, _uiDebugController, _performanceController, _wingsHiderController,
                _crashDumpController.DumpFolder, SavePluginSettings);
            _pluginSettings = new PluginSettings(Config, _keybinds, _flyController, _speedBoostController,
                _teleportController, _knockbackImmunityController, _bodyRotationLockController,
                _crashWorkaroundController, _promoPopupController, _menuUI.Theme);
        }

        private void SavePluginSettings() => _pluginSettings.Save();

        private void RegisterCrashWorkarounds()
        {
            _crashWorkaroundController.Workarounds.Add(new CrashWorkaround
            {
                Name = "DynamicBones JSON guard",
                Description = "Experimental - suppresses the DynamicBones JSON error seen right before 2 confirmed crashes.",
                Enabled = false,
                OnToggle = enabled => DynamicBonesCrashGuardPatch.Enabled = enabled,
            });

            _crashWorkaroundController.ConfirmedFixes.Add(new ConfirmedCrashFix
            {
                Name = "Menu texture leak",
                Description = "MenuStyles rebuilt ~16 new textures every frame the menu was open, never destroying " +
                    "the old ones - exhausted Unity's D3D11 resource table over a session (\"Resource ID out of " +
                    "range\", d3d11 out-of-memory in the player log). Fixed: styles now only rebuild, and only " +
                    "replace their textures, when the theme actually changes.",
            });

            _crashWorkaroundController.ConfirmedFixes.Add(new ConfirmedCrashFix
            {
                Name = "Console click freeze",
                Description = "Clicking inside the BepInEx console window starts a text selection, and Windows " +
                    "blocks every write to that console until it ends - the game logs there from the main thread, " +
                    "so it froze (hang dump: main thread stuck in WriteFile). Fixed: QuickEdit is turned off on " +
                    "startup, so a click can no longer start a selection.",
            });
        }

        private void Start()
        {
            _pluginSettings.Load();
            LogAccountManagerPublicMembers();
        }

        // One-off probe: AccountManager exposes no account data via public
        // members (only inherited Unity component boilerplate), so a
        // player-name/VIP display has to come from elsewhere. Logged for
        // reference rather than wired up to anything yet.
        private void LogAccountManagerPublicMembers()
        {
            try
            {
                Type accountManagerType = Type.GetType("VWW.Clients.Curio.GUI.CurioUI.AccountManager, Assembly-CSharp");
                if (accountManagerType == null)
                    return;

                object accountManagerInstance = GetStaticInstance(accountManagerType);
                if (accountManagerInstance == null)
                {
                    Logger.LogInfo("AccountManager.Instance is null (not logged in yet?)");
                    return;
                }

                LogPublicMembersOf(accountManagerType);
            }
            catch (Exception exception)
            {
                Logger.LogWarning("identity probe failed: " + exception.Message);
            }
        }

        private static object GetStaticInstance(Type type)
        {
            PropertyInfo instanceProperty = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            return instanceProperty?.GetValue(null);
        }

        private void LogPublicMembersOf(Type type)
        {
            Logger.LogInfo("AccountManager members:");
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                Logger.LogInfo("  prop " + property.PropertyType.Name + " " + property.Name);
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                Logger.LogInfo("  field " + field.FieldType.Name + " " + field.Name);
        }

        private void Update()
        {
            _crashDumpController.Heartbeat();

            if (_keybinds.CaptureIfRebinding())
                return;

            HandleMenuToggleKey();

            _playerContext.UpdateForThisFrame();

            TickAllFeatures();
        }

        private void HandleMenuToggleKey()
        {
            if (!Input.GetKeyDown(_keybinds.MenuKey))
                return;

            bool wasMenuOpenBeforeToggle = _menuUI.Open;
            _menuUI.ToggleOpen();
            if (!wasMenuOpenBeforeToggle && _playerContext.PlayerName == "—")
                LogAccountManagerPublicMembers();
        }

        private void TickAllFeatures()
        {
            _flyController.Tick(_playerContext.TypingInChat, _keybinds.FlyUpKey, _keybinds.FlyDownKey);
            _knockbackImmunityController.Tick(_flyController.Flying);
            _speedBoostController.Tick(_playerContext.TypingInChat, _flyController.Flying);
            _systemStatsController.Tick();
            _promoPopupController.Tick(Time.deltaTime, _playerContext.Avatar != null, _playerContext.TypingInChat);
            _uiDebugController.Tick(Time.deltaTime);
            _performanceController.Tick(Time.deltaTime);
            _wingsHiderController.Tick(Time.deltaTime);
        }

        private void OnGUI() => _menuUI.Draw();

        private void OnDestroy()
        {
            // Awake bailed out before creating anything.
            if (_menuUI == null)
                return;

            _flyController.SetFlying(false);
            _speedBoostController.SetEnabled(false);
            _bodyRotationLockController.SetEnabled(false);
            _pluginSettings.Save();
            _crashDumpController.Shutdown();
            _systemStatsController.Shutdown();
        }
    }
}
