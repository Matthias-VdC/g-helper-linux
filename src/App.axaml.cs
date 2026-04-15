using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using GHelper.Linux.Gpu;
using GHelper.Linux.Helpers;
using GHelper.Linux.Mode;
using GHelper.Linux.Platform;
using GHelper.Linux.Platform.Linux;
using GHelper.Linux.UI.Views;
using GHelper.Linux.USB;
using GHelper.Linux.I18n;

namespace GHelper.Linux;

public class App : Application
{
    // Global service instances (mirrors G-Helper's Program.acpi pattern)
    public static IAsusWmi? Wmi { get; private set; }
    public static IPowerManager? Power { get; private set; }
    public static ISystemIntegration? System { get; private set; }
    public static IInputHandler? Input { get; private set; }
    public static IAudioControl? Audio { get; private set; }
    public static IDisplayControl? Display { get; private set; }
    public static IGpuControl? GpuControl { get; private set; }

    // GPU mode switching controller (safety checks, driver detection, reboot scheduling)
    public static GpuModeController? GpuModeCtrl { get; private set; }

    // Business logic orchestrator
    public static ModeControl? Mode { get; private set; }

    public static MainWindow? MainWindowInstance { get; set; }
    public static TrayIcon? TrayIconInstance { get; set; }

    // Single-instance lock that prevents duplicate tray icons
    private static FileStream? _lockFile;

    // Legacy event codes for non-configurable keys
    private const int EventKbBrightnessUp = 196;   // Fn+F3
    private const int EventKbBrightnessDown = 197;  // Fn+F2

    /// <summary>
    /// Available actions for configurable key bindings (app-internal only).
    /// Keys = action ID stored in config, Values = display name for UI.
    /// </summary>
    public static readonly Dictionary<string, string> AvailableKeyActions = new()
    {
        { "none",            "None" },
        { "ghelper",         "Toggle G-Helper" },
        { "performance",     "Cycle Performance Mode" },
        { "aura",            "Cycle Aura Mode" },
        { "brightness_up",   "Keyboard Brightness Up" },
        { "brightness_down", "Keyboard Brightness Down" },
        { "micmute",         "Toggle Microphone Mute" },
        { "mute",            "Toggle Speaker Mute" },
        { "gpu_eco",         "Toggle GPU Eco Mode" },
        { "screen_refresh",  "Cycle Screen Refresh Rate" },
        { "overdrive",       "Toggle Panel Overdrive" },
        { "miniled",         "Toggle MiniLED" },
        { "camera",          "Toggle Camera" },
        { "touchpad",        "Toggle Touchpad" },
    };

    /// <summary>Default actions for each configurable key (matches Windows G-Helper).</summary>
    private static readonly Dictionary<string, string> DefaultKeyActions = new()
    {
        { "m4",   "ghelper" },     // ROG/M5 key → toggle window
        { "fnf4", "aura" },        // Fn+F4 → cycle aura mode
        { "fnf5", "performance" }, // Fn+F5 / M4 → cycle performance mode
    };

    /// <summary>Human-readable names for configurable keys (for UI labels).</summary>
    public static readonly Dictionary<string, string> ConfigurableKeyNames = new()
    {
        { "m4",   "ROG / M5 Key" },
        { "fnf4", "Fn+F4 (Aura)" },
        { "fnf5", "Fn+F5 / M4 (Performance)" },
    };

    public static string GetKeyActionDisplayName(string actionId)
    {
        return actionId switch
        {
            "none" => Labels.Get("action_none"),
            "ghelper" => Labels.Get("action_ghelper"),
            "performance" => Labels.Get("action_performance"),
            "aura" => Labels.Get("action_aura"),
            "brightness_up" => Labels.Get("action_brightness_up"),
            "brightness_down" => Labels.Get("action_brightness_down"),
            "micmute" => Labels.Get("action_micmute"),
            "mute" => Labels.Get("action_mute"),
            "gpu_eco" => Labels.Get("action_gpu_eco"),
            "screen_refresh" => Labels.Get("action_screen_refresh"),
            "overdrive" => Labels.Get("action_overdrive"),
            "miniled" => Labels.Get("action_miniled"),
            "camera" => Labels.Get("action_camera"),
            "touchpad" => Labels.Get("action_touchpad"),
            _ => actionId
        };
    }

    public static string GetKeyDisplayName(string bindingName)
    {
        return bindingName switch
        {
            "m4" => Labels.Get("key_m4"),
            "fnf4" => Labels.Get("key_fnf4"),
            "fnf5" => Labels.Get("key_fnf5"),
            _ => bindingName
        };
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Exit if another ghelper is already running
        if (!TryAcquireSingleInstanceLock())
        {
            Console.Error.WriteLine("g-helper: another instance is already running — exiting");
            Environment.Exit(0);
            return;
        }

        // Initialize Linux platform backends
        InitializePlatform();

        // Initialize i18n before any UI
        Labels.Initialize();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Keep running when window is closed (tray icon keeps app alive)
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            MainWindowInstance = new MainWindow();
            if (AppConfig.Is("topmost")) MainWindowInstance.Topmost = true;

            // Show main window on startup unless "Start minimized to tray" is enabled
            if (!AppConfig.Is("silent_start"))
            {
                desktop.MainWindow = MainWindowInstance;
            }

            // Set up tray icon (secondary access method)
            SetupTrayIcon(desktop);

            // Start hotkey listener
            StartHotkeyListener();

            // Apply saved performance mode on startup
            Mode?.SetPerformanceMode();

            // Re-apply saved battery charge limit on startup
            int savedChargeLimit = AppConfig.Get("charge_limit");
            if (savedChargeLimit > 0 && savedChargeLimit < 100)
            {
                Logger.WriteLine($"Startup: re-applying charge limit {savedChargeLimit}%");
                Wmi?.SetBatteryChargeLimit(savedChargeLimit);
            }

            // Warn if udev rules are not installed (sysfs writes will fail)
            if (!File.Exists("/etc/udev/rules.d/90-ghelper.rules"))
            {
                Logger.WriteLine("WARNING: udev rules not installed — sysfs writes will fail. Run install.sh for full functionality.");
                System?.ShowNotification(Labels.Get("setup_required"),
                    Labels.Get("udev_not_installed"),
                    "dialog-warning");
            }

            // Initialize AURA hardware (RGB) on background thread regardless of window visibility.
            // When done, post RefreshKeyboard to UI thread so the Aura panel/colors update
            // (fixes race where Loaded event fires before HID handshake completes).
            Task.Run(() =>
            {
                MainWindow.InitAuraHardware();
                Avalonia.Threading.Dispatcher.UIThread.Post(() => MainWindowInstance?.RefreshKeyboard());
            });

            // Ensure autostart .desktop file matches config preference and current binary path
            bool autostart = AppConfig.IsNotFalse("autostart");
            System?.SetAutostart(autostart);

            // Update tray icon to match current mode
            UpdateTrayIcon();

            // Start power state monitoring for auto GPU mode and auto performance
            Power?.StartPowerMonitoring();
            if (Power != null)
            {
                Power.PowerStateChanged += OnPowerStateChanged;
            }

            // Apply pending GPU mode from config (e.g., Eco scheduled for reboot)
            // Then apply auto GPU mode if Optimized is enabled
            // Run on background thread — SetGpuEco can block for 30-60 seconds
            Task.Run(() =>
            {
                // Check for boot recovery marker (impossible state was fixed during boot)
                const string RecoveryMarkerPath = "/etc/ghelper/last-recovery";
                try
                {
                    if (File.Exists(RecoveryMarkerPath))
                    {
                        string reason = File.ReadAllText(RecoveryMarkerPath).Trim();
                        Logger.WriteLine($"Boot recovery detected: {reason}");
                        System?.ShowNotification(Labels.Get("gpu_mode"),
                            Labels.Get("gpu_reset_standard"),
                            "dialog-warning");
                        try { File.Delete(RecoveryMarkerPath); }
                        catch (Exception delEx)
                        {
                            Logger.WriteLine($"Could not delete recovery marker: {delEx.Message}");
                            // Non-fatal — marker will be shown again next launch but that's acceptable
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"Recovery marker check failed: {ex.Message}");
                }

                // First: try to apply any pending mode from config
                if (GpuModeCtrl != null)
                {
                    var pendingResult = GpuModeCtrl.ApplyPendingOnStartup();
                    if (pendingResult == GpuSwitchResult.Applied)
                    {
                        System?.ShowNotification(Labels.Get("gpu_mode"),
                            Labels.Get("gpu_eco_applied"), "video-display");
                    }
                    else if (pendingResult == GpuSwitchResult.DriverBlocking)
                    {
                        System?.ShowNotification(Labels.Get("gpu_mode"),
                            Labels.Get("gpu_eco_driver_pending"), "dialog-warning");
                    }
                }

                // Then: auto GPU mode (Optimized) based on current power state
                if (GpuModeCtrl != null && AppConfig.Is("gpu_auto"))
                {
                    var autoResult = GpuModeCtrl.AutoGpuSwitch();
                    if (autoResult == GpuSwitchResult.Applied)
                    {
                        bool onAc = Power?.IsOnAcPower() ?? true;
                        System?.ShowNotification(Labels.Get("gpu_mode"),
                            onAc ? Labels.Get("gpu_optimized_ac") : Labels.Get("gpu_optimized_battery"),
                            "video-display");
                    }
                }

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    MainWindowInstance?.RefreshGpuModePublic());
            });

            // Restore clamshell mode if it was enabled
            if (AppConfig.Is("toggle_clamshell_mode"))
                UI.Views.ExtraWindow.StartClamshellInhibit();

            // Register Unix signal handlers for clean shutdown on SIGTERM/SIGINT
            // This prevents KDE/GNOME from hanging on logout/reboot
            RegisterSignalHandlers(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void InitializePlatform()
    {
        Wmi = new LinuxAsusWmi();
        Power = new LinuxPowerManager();
        System = new LinuxSystemIntegration();
        Input = new LinuxInputHandler();
        Audio = new LinuxAudioControl();
        Display = new LinuxDisplayControl();

        // Create mode controller (uses App.Wmi, App.Power, etc.)
        Mode = new ModeControl();

        // Create GPU mode switching controller
        if (Wmi != null && Power != null)
            GpuModeCtrl = new GpuModeController(Wmi, Power);

        // Initialize GPU control (nvidia-smi / amdgpu sysfs for temp/load)
        InitializeGpuControl();

        Logger.WriteLine($"G-Helper Linux initialized");
        Logger.WriteLine($"Model: {System.GetModelName()}");
        Logger.WriteLine($"BIOS: {System.GetBiosVersion()}");

        // Log which sysfs backend each attribute resolved to (legacy vs firmware-attributes)
        SysfsHelper.LogResolvedAttributes();

        // Log detected features
        LogFeatureDetection();
    }

    private void LogFeatureDetection()
    {
        var features = new (AttrDef attr, string name)[]
        {
            (AsusAttributes.ThrottleThermalPolicy, "Performance modes"),
            (AsusAttributes.DgpuDisable, "GPU Eco mode"),
            (AsusAttributes.GpuMuxMode, "MUX switch"),
            (AsusAttributes.PanelOd, "Panel overdrive"),
            (AsusAttributes.MiniLedMode, "Mini LED"),
            (AsusAttributes.PptPl1Spl, "PL1 power limit"),
            (AsusAttributes.PptPl2Sppt, "PL2 power limit"),
            (AsusAttributes.NvDynamicBoost, "NVIDIA dynamic boost"),
            (AsusAttributes.NvTempTarget, "NVIDIA temp target"),
        };

        foreach (var (attr, name) in features)
        {
            bool supported = Wmi?.IsFeatureSupported(attr) ?? false;
            Logger.WriteLine($"  {name}: {(supported ? "YES" : "no")}");
        }

        // Raw WMI: probe all GPU ACPI endpoints in a single pkexec call (only when enabled)
        if (Helpers.AppConfig.Is("raw_wmi"))
        {
            Logger.WriteLine("  Raw WMI mode: ENABLED (user opt-in)");
            if (AsusWmiDebugfs.IsAvailable())
            {
                AsusWmiDebugfs.ProbeAll();      // 1 pkexec call — probes + caches all device IDs
                AsusWmiDebugfs.LogProbeResults(); // reads from cache, no pkexec
            }
            else
            {
                Logger.WriteLine("  Raw WMI debugfs: not available");
            }
        }
    }

    private void InitializeGpuControl()
    {
        try
        {
            // Try NVIDIA first
            var nvidia = new LinuxNvidiaGpuControl();
            if (nvidia.IsAvailable())
            {
                GpuControl = nvidia;
                Logger.WriteLine($"GPU Control: NVIDIA - {nvidia.GetGpuName() ?? "Unknown"}");
                return;
            }

            // Try AMD
            var amd = new LinuxAmdGpuControl();
            if (amd.IsAvailable())
            {
                GpuControl = amd;
                Logger.WriteLine($"GPU Control: AMD - {amd.GetGpuName() ?? "Unknown"}");
                return;
            }

            Logger.WriteLine("GPU Control: No dGPU detected");
        }
        catch (Exception ex)
        {
            Logger.WriteLine("GPU Control initialization failed", ex);
        }
    }

    private void StartHotkeyListener()
    {
        if (Input == null) return;

        Input.HotkeyPressed += OnHotkeyPressed;
        Input.KeyBindingPressed += OnKeyBindingPressed;
        Input.StartListening();
    }

    /// <summary>Handle non-configurable hotkey events (brightness, etc.).</summary>
    private void OnHotkeyPressed(int eventCode)
    {
        Logger.WriteLine($"Hotkey event: {eventCode}");

        switch (eventCode)
        {
            case EventKbBrightnessUp:
                CycleKeyboardBrightness(up: true);
                break;

            case EventKbBrightnessDown:
                CycleKeyboardBrightness(up: false);
                break;
        }
    }

    /// <summary>
    /// Handle configurable key binding events.
    /// Reads the assigned action from config, falls back to default.
    /// </summary>
    private void OnKeyBindingPressed(string bindingName)
    {
        // Read configured action, fall back to default
        string? action = AppConfig.GetString(bindingName);
        if (string.IsNullOrEmpty(action) || !AvailableKeyActions.ContainsKey(action))
        {
            DefaultKeyActions.TryGetValue(bindingName, out action);
            action ??= "none";
        }

        Logger.WriteLine($"Key binding: {bindingName} → action={action}");
        ExecuteKeyAction(action);
    }

    /// <summary>Get the current action for a configurable key binding.</summary>
    public static string GetKeyAction(string bindingName)
    {
        string? action = AppConfig.GetString(bindingName);
        if (string.IsNullOrEmpty(action) || !AvailableKeyActions.ContainsKey(action))
        {
            DefaultKeyActions.TryGetValue(bindingName, out action);
            action ??= "none";
        }
        return action;
    }

    /// <summary>Execute a key action by its action ID.</summary>
    private void ExecuteKeyAction(string action)
    {
        switch (action)
        {
            case "none":
                break;

            case "ghelper":
                Avalonia.Threading.Dispatcher.UIThread.Post(() => ToggleMainWindow());
                break;

            case "performance":
                Mode?.CyclePerformanceMode();
                UpdateTrayIcon();
                // Refresh main window if visible
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    MainWindowInstance?.RefreshPerformanceMode());
                break;

            case "aura":
                string modeName = Aura.CycleAuraMode();
                System?.ShowNotification(Labels.Get("aura"), modeName, "preferences-desktop-color");
                // Refresh main window keyboard section if visible
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    MainWindowInstance?.RefreshKeyboard());
                break;

            case "brightness_up":
                CycleKeyboardBrightness(up: true);
                break;

            case "brightness_down":
                CycleKeyboardBrightness(up: false);
                break;

            case "micmute":
                Audio?.ToggleMicMute();
                bool micMuted = Audio?.IsMicMuted() ?? false;
                System?.ShowNotification(Labels.Get("microphone"),
                    micMuted ? Labels.Get("muted") : Labels.Get("unmuted"),
                    micMuted ? "microphone-sensitivity-muted" : "microphone-sensitivity-high");
                break;

            case "mute":
                Audio?.ToggleSpeakerMute();
                bool spkMuted = Audio?.IsSpeakerMuted() ?? false;
                System?.ShowNotification(Labels.Get("speaker"),
                    spkMuted ? Labels.Get("muted") : Labels.Get("unmuted"),
                    spkMuted ? "audio-volume-muted" : "audio-volume-high");
                break;

            case "gpu_eco":
                // Toggle between Eco and Standard via GpuModeController
                var currentMode = GpuModeCtrl?.GetCurrentMode() ?? GpuMode.Standard;
                var toggleTarget = (currentMode == GpuMode.Eco) ? GpuMode.Standard : GpuMode.Eco;
                TrayGpuModeSwitch(toggleTarget);
                break;

            case "screen_refresh":
                CycleScreenRefreshRate();
                break;

            case "overdrive":
                bool currentOd = Wmi?.GetPanelOverdrive() ?? false;
                Wmi?.SetPanelOverdrive(!currentOd);
                System?.ShowNotification(Labels.Get("panel_overdrive"),
                    !currentOd ? Labels.Get("enabled") : Labels.Get("disabled"),
                    "preferences-desktop-display");
                break;

            case "miniled":
                int currentMiniLed = Wmi?.GetMiniLedMode() ?? 0;
                int nextMiniLed = currentMiniLed == 0 ? 1 : 0;
                Wmi?.SetMiniLedMode(nextMiniLed);
                System?.ShowNotification(Labels.Get("mini_led"),
                    nextMiniLed == 1 ? Labels.Get("enabled") : Labels.Get("disabled"),
                    "preferences-desktop-display");
                break;

            case "camera":
                bool camOn = LinuxSystemIntegration.IsCameraEnabled();
                LinuxSystemIntegration.SetCameraEnabled(!camOn);
                System?.ShowNotification(Labels.Get("camera"),
                    !camOn ? Labels.Get("enabled") : Labels.Get("disabled"),
                    !camOn ? "camera-on" : "camera-off");
                break;

            case "touchpad":
                bool? tpOn = LinuxSystemIntegration.IsTouchpadEnabled();
                if (tpOn.HasValue)
                {
                    LinuxSystemIntegration.SetTouchpadEnabled(!tpOn.Value);
                    System?.ShowNotification(Labels.Get("touchpad"),
                        !tpOn.Value ? Labels.Get("enabled") : Labels.Get("disabled"),
                        !tpOn.Value ? "input-touchpad-on" : "input-touchpad-off");
                }
                break;
        }
    }

    private void CycleScreenRefreshRate()
    {
        var display = Display;
        if (display == null) return;

        // Hotkey cycle disables auto mode (manual override)
        AppConfig.Set("screen_auto", 0);

        var rates = display.GetAvailableRefreshRates();
        if (rates.Count < 2) return;

        int current = display.GetRefreshRate();
        rates.Sort();

        // Find next rate (cycle: 60 → 120 → 165 → 60...)
        int nextRate = rates[0];
        for (int i = 0; i < rates.Count; i++)
        {
            if (rates[i] > current)
            {
                nextRate = rates[i];
                break;
            }
        }

        display.SetRefreshRate(nextRate);
        System?.ShowNotification(Labels.Get("display"), Labels.Format("refresh_rate_format", nextRate), "video-display");

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            MainWindowInstance?.RefreshScreenPublic());
    }

    /// <summary>
    /// Auto-switch screen refresh rate based on AC/battery power.
    /// AC → max refresh + overdrive ON, Battery → 60Hz + overdrive OFF.
    /// Called on power state change and when user enables auto mode.
    /// </summary>
    public void AutoScreen()
    {
        if (!AppConfig.Is("screen_auto")) return;

        var display = Display;
        if (display == null) return;

        var rates = display.GetAvailableRefreshRates();
        bool onAc = Power?.IsOnAcPower() ?? true;

        if (onAc)
        {
            int maxHz = rates.Count > 0 ? rates[0] : 120;
            display.SetRefreshRate(maxHz);
            Wmi?.SetPanelOverdrive(true);
            Logger.WriteLine($"AutoScreen: AC power → {maxHz}Hz + overdrive ON");
            System?.ShowNotification(Labels.Get("display"), Labels.Format("auto_screen_ac", maxHz), "video-display");
        }
        else
        {
            display.SetRefreshRate(60);
            Wmi?.SetPanelOverdrive(false);
            Logger.WriteLine("AutoScreen: Battery → 60Hz + overdrive OFF");
            System?.ShowNotification(Labels.Get("display"), Labels.Get("auto_screen_battery"), "video-display");
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            MainWindowInstance?.RefreshScreenPublic());
    }

    private void CycleKeyboardBrightness(bool up)
    {
        int next;
        if (Wmi is Platform.Linux.LinuxAsusWmi lwmi && lwmi.HasKbdBrightnessHwChanged)
        {
            // Kernel already changed brightness in sysfs — just read the new value
            next = lwmi.GetKeyboardBrightness();
            if (next < 0) next = 0;
        }
        else
        {
            // Kernel doesn't handle it, we must increment and write
            int current = Wmi?.GetKeyboardBrightness() ?? 0;
            next = up ? Math.Min(current + 1, 3) : Math.Max(current - 1, 0);
            Wmi?.SetKeyboardBrightness(next);
        }
        string level = next switch
        {
            0 => Labels.Get("kbd_off"),
            1 => Labels.Get("kbd_low"),
            2 => Labels.Get("kbd_medium"),
            3 => Labels.Get("kbd_high"),
            _ => Labels.Format("kbd_level", next)
        };
        System?.ShowNotification(Labels.Get("keyboard"), level, "keyboard-brightness");

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            MainWindowInstance?.RefreshKeyboard();
            MainWindowInstance?.RefreshExtraKeyboardBrightness();
        });
    }

    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // Tray icons on Linux use D-Bus StatusNotifierItem (SNI) protocol.
        // This requires a valid DBUS_SESSION_BUS_ADDRESS — running with plain
        // 'sudo' breaks this. Use udev rules for non-root access instead,
        // or run with: sudo -E ./ghelper
        var dbusAddr = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        if (string.IsNullOrEmpty(dbusAddr))
        {
            Logger.WriteLine("WARNING: DBUS_SESSION_BUS_ADDRESS not set — tray icon will not appear.");
            Logger.WriteLine("  Tip: Install udev rules to run without sudo, or use: sudo -E ./ghelper");
        }

        try
        {
            var trayIcon = new TrayIcon
            {
                ToolTipText = Labels.Format("tray_tooltip", Modes.GetCurrentName()),
                IsVisible = true,
                Menu = CreateTrayMenu(desktop)
            };

            // Load tray icon from embedded assets
            try
            {
                string iconName = AppConfig.IsBWIcon() ? "dark-standard.ico" : "standard.ico";
                var uri = new Uri($"avares://ghelper/UI/Assets/{iconName}");
                trayIcon.Icon = new WindowIcon(AssetLoader.Open(uri));
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Could not load tray icon image", ex);
            }

            trayIcon.Clicked += (_, _) => ToggleMainWindow();
            TrayIconInstance = trayIcon;

            Labels.LanguageChanged += () =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (TrayIconInstance != null)
                    {
                        TrayIconInstance.Menu = CreateTrayMenu(desktop);
                        UpdateTrayIcon();
                    }
                });
            };

            Logger.WriteLine("Tray icon created successfully");
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Tray icon setup failed (D-Bus/SNI unavailable)", ex);
        }
    }

    /// <summary>Update tray icon and tooltip to reflect current performance mode.</summary>
    public static void UpdateTrayIcon()
    {
        if (TrayIconInstance == null) return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            int mode = Modes.GetCurrent();
            int baseMode = Modes.GetBase(mode);
            string name = Modes.GetName(mode);

            TrayIconInstance.ToolTipText = Labels.Format("tray_tooltip", name);

            bool bw = AppConfig.IsBWIcon();

            // Select icon based on base mode and B&W preference
            string iconName = baseMode switch
            {
                0 => bw ? "dark-standard.ico" : "standard.ico",   // Balanced
                1 => bw ? "dark-standard.ico" : "ultimate.ico",   // Turbo (no dark-ultimate, use dark-standard)
                2 => bw ? "dark-eco.ico"      : "eco.ico",        // Silent
                _ => bw ? "dark-standard.ico" : "standard.ico"
            };

            try
            {
                var uri = new Uri($"avares://ghelper/UI/Assets/{iconName}");
                TrayIconInstance.Icon = new WindowIcon(AssetLoader.Open(uri));
            }
            catch
            {
                // Ignore — icon may not exist
            }
        });
    }

    /// <summary>Apply Topmost setting to all currently open windows.</summary>
    public static void SetTopmostAll(bool topmost)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                foreach (var window in desktop.Windows)
                {
                    window.Topmost = topmost;
                }
            }
        });
    }

    private NativeMenu CreateTrayMenu(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var menu = new NativeMenu();

        // Performance modes
        var silent = new NativeMenuItem(Labels.Get("mode_silent"));
        silent.Click += (_, _) => { Mode?.SetPerformanceMode(2, true); UpdateTrayIcon(); MainWindowInstance?.RefreshPerformanceMode(); };
        menu.Add(silent);

        var balanced = new NativeMenuItem(Labels.Get("mode_balanced"));
        balanced.Click += (_, _) => { Mode?.SetPerformanceMode(0, true); UpdateTrayIcon(); MainWindowInstance?.RefreshPerformanceMode(); };
        menu.Add(balanced);

        var turbo = new NativeMenuItem(Labels.Get("mode_turbo"));
        turbo.Click += (_, _) => { Mode?.SetPerformanceMode(1, true); UpdateTrayIcon(); MainWindowInstance?.RefreshPerformanceMode(); };
        menu.Add(turbo);

        menu.Add(new NativeMenuItemSeparator());

        // GPU modes — show if GPU Eco is available (sysfs or raw WMI debugfs).
        // All writes run in Task.Run via GpuModeController
        // (dgpu_disable writes can block in the kernel for 30-60 seconds)
        if (Wmi?.IsGpuEcoAvailable() == true)
        {
            var eco = new NativeMenuItem(Labels.Get("tray_gpu_eco"));
            eco.Click += (_, _) => TrayGpuModeSwitch(GpuMode.Eco);
            menu.Add(eco);

            var standard = new NativeMenuItem(Labels.Get("tray_gpu_standard"));
            standard.Click += (_, _) => TrayGpuModeSwitch(GpuMode.Standard);
            menu.Add(standard);

            var optimized = new NativeMenuItem(Labels.Get("tray_gpu_optimized"));
            optimized.Click += (_, _) => TrayGpuModeSwitch(GpuMode.Optimized);
            menu.Add(optimized);

            // Ultimate (MUX switch) — only on models with gpu_mux_mode support
            if (Wmi?.IsFeatureSupported(AsusAttributes.GpuMuxMode) == true)
            {
                var ultimate = new NativeMenuItem(Labels.Get("tray_gpu_ultimate"));
                ultimate.Click += (_, _) => TrayGpuModeSwitch(GpuMode.Ultimate);
                menu.Add(ultimate);
            }

            menu.Add(new NativeMenuItemSeparator());
        }

        // Settings
        var settings = new NativeMenuItem(Labels.Get("settings"));
        settings.Click += (_, _) => ToggleMainWindow();
        menu.Add(settings);

        menu.Add(new NativeMenuItemSeparator());

        // Quit
        var quit = new NativeMenuItem(Labels.Get("quit"));
        quit.Click += (_, _) =>
        {
            Shutdown(desktop);
        };
        menu.Add(quit);

        return menu;
    }

    private void ToggleMainWindow()
    {
        // Window may have been disposed by closing (KDE logout, user clicking X).
        // Recreate it if needed — app stays alive via ShutdownMode.OnExplicitShutdown.
        if (MainWindowInstance == null || MainWindowInstance.PlatformImpl == null)
        {
            MainWindowInstance = new MainWindow();
            if (AppConfig.Is("topmost")) MainWindowInstance.Topmost = true;
            MainWindowInstance.Show();
            MainWindowInstance.Activate();
            return;
        }

        if (MainWindowInstance.IsVisible)
        {
            MainWindowInstance.Hide();
        }
        else
        {
            MainWindowInstance.Show();
            MainWindowInstance.Activate();
        }
    }

    /// <summary>
    /// Tray menu GPU mode switch — runs GpuModeController on background thread.
    /// Tray menu cannot show dialogs, so DriverBlocking → auto-schedule for reboot.
    /// </summary>
    private static void TrayGpuModeSwitch(GpuMode target)
    {
        Task.Run(() =>
        {
            if (GpuModeCtrl == null) return;

            var result = GpuModeCtrl.RequestModeSwitch(target);

            switch (result)
            {
                case GpuSwitchResult.Applied:
                    string text = target switch
                    {
                        GpuMode.Eco => Labels.Get("gpu_notify_eco"),
                        GpuMode.Standard => Labels.Get("gpu_notify_standard"),
                        GpuMode.Optimized => Labels.Get("gpu_notify_optimized"),
                        GpuMode.Ultimate => Labels.Get("gpu_notify_ultimate"),
                        _ => Labels.Get("gpu_notify_changed")
                    };
                    System?.ShowNotification(Labels.Get("gpu_mode"), text, "video-display");
                    break;

                case GpuSwitchResult.RebootRequired:
                    string rebootText = target switch
                    {
                        GpuMode.Ultimate => Labels.Get("gpu_reboot_ultimate"),
                        GpuMode.Standard => Labels.Get("gpu_reboot_standard"),
                        GpuMode.Optimized => Labels.Get("gpu_reboot_optimized"),
                        GpuMode.Eco => Labels.Get("gpu_reboot_eco"),
                        _ => Labels.Format("gpu_mode_reboot_format", target)
                    };
                    System?.ShowNotification(Labels.Get("gpu_mode"), rebootText, "system-reboot");
                    break;

                case GpuSwitchResult.EcoBlocked:
                    System?.ShowNotification(Labels.Get("gpu_mode"),
                        Labels.Get("gpu_eco_blocked_detail"),
                        "dialog-warning");
                    break;

                case GpuSwitchResult.DriverBlocking:
                    // Tray menu can't show a dialog — auto-schedule for reboot
                    GpuModeCtrl.ScheduleModeForReboot(target);
                    System?.ShowNotification(Labels.Get("gpu_mode"),
                        Labels.Get("gpu_driver_scheduled"), "system-reboot");
                    break;

                case GpuSwitchResult.Deferred:
                    System?.ShowNotification(Labels.Get("gpu_mode"),
                        Labels.Get("gpu_eco_after_reboot"), "system-reboot");
                    break;

                case GpuSwitchResult.Failed:
                    System?.ShowNotification(Labels.Get("gpu_mode"),
                        Labels.Get("gpu_switch_failed"), "dialog-error");
                    break;
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                MainWindowInstance?.RefreshGpuModePublic());
        });
    }

    /// <summary>Debounce guard, skip power events within 3 seconds of the last one.</summary>
    private long _lastPowerChangeMs;

    /// <summary>
    /// Handle power state change (AC plugged/unplugged).
    /// Triggers auto GPU mode switch and auto performance mode.
    /// </summary>
    private void OnPowerStateChanged(bool onAc)
    {
        long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        if (Math.Abs(now - _lastPowerChangeMs) < 3000) return;
        _lastPowerChangeMs = now;

        Logger.WriteLine($"Power state changed: AC={onAc}");

        // Auto GPU mode (Optimized = auto Eco/Standard based on AC power)
        // Run on background thread — SetGpuEco can block for 30-60 seconds
        Task.Run(() =>
        {
            if (GpuModeCtrl != null)
            {
                var result = GpuModeCtrl.AutoGpuSwitch();
                if (result == GpuSwitchResult.Applied)
                {
                    string msg = onAc
                        ? Labels.Get("gpu_optimized_ac")
                        : Labels.Get("gpu_optimized_battery");
                    System?.ShowNotification(Labels.Get("gpu_mode"), msg, "video-display");
                }
                else if (result == GpuSwitchResult.DriverBlocking)
                {
                    System?.ShowNotification(Labels.Get("gpu_mode"),
                        Labels.Get("gpu_driver_staying"), "dialog-warning");
                }
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                MainWindowInstance?.RefreshGpuModePublic();
                MainWindowInstance?.RefreshBatteryPublic();
            });
        });

        // Auto performance mode (if configured)
        Mode?.AutoPerformance(powerChanged: true);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            MainWindowInstance?.RefreshPerformanceMode());

        // Auto screen refresh rate (if configured)
        AutoScreen();
    }

    // Unix signal handlers for clean shutdown on SIGTERM/SIGINT (logout/reboot)
    private static List<PosixSignalRegistration>? _signalRegistrations;

    private void RegisterSignalHandlers(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        try
        {
            _signalRegistrations = new();

            // SIGTERM: sent by KDE/GNOME during logout/reboot
            _signalRegistrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ =>
            {
                Logger.WriteLine("Received SIGTERM - initiating shutdown");
                ShutdownFromSignal(desktop);
            }));

            // SIGINT: Ctrl+C in terminal
            _signalRegistrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGINT, _ =>
            {
                Logger.WriteLine("Received SIGINT - initiating shutdown");
                ShutdownFromSignal(desktop);
            }));

            Logger.WriteLine("Unix signal handlers registered (SIGTERM, SIGINT)");
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"Failed to register signal handlers: {ex.Message}");
        }
    }

    private void ShutdownFromSignal(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // Signal handler runs on a threadpool thread.
        // Don't rely on UI thread — it may already be blocked during session shutdown.
        Logger.WriteLine("Signal shutdown: cleaning up...");

        // Best-effort: apply pending Eco mode before shutdown
        // (system is going down — display stack is closing, driver may be releasing)
        try { GpuModeCtrl?.ApplyPendingOnShutdown(); } catch { }

        try { Power?.StopPowerMonitoring(); } catch { }
        try { UI.Views.ExtraWindow.StopClamshellInhibit(); } catch { }
        try { Input?.Dispose(); } catch { }
        try { Wmi?.Dispose(); } catch { }

        Logger.WriteLine("Signal shutdown: exiting process");
        Environment.Exit(0);
    }

    private void Shutdown(IClassicDesktopStyleApplicationLifetime desktop)
    {
        Logger.WriteLine("Shutting down...");

        // Cleanup
        Power?.StopPowerMonitoring();
        UI.Views.ExtraWindow.StopClamshellInhibit();
        Input?.Dispose();
        Wmi?.Dispose();

        desktop.Shutdown();
    }

    /// <summary>
    /// Try to acquire a single-instance lock file. Returns false if another
    /// instance is already running. Uses XDG_RUNTIME_DIR (per-user) to avoid
    /// multi-user conflicts. The OS releases the lock automatically on exit/crash.
    /// </summary>
    private static bool TryAcquireSingleInstanceLock()
    {
        string lockDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "/tmp";
        string lockPath = Path.Combine(lockDir, "ghelper.lock");

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                _lockFile = new FileStream(lockPath, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (IOException)
            {
                if (attempt == 0)
                    Thread.Sleep(500); // retry once — covers app restart race
            }
        }
        return false;
    }
}
