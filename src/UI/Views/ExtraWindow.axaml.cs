using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using GHelper.Linux.I18n;
using GHelper.Linux.Platform.Linux;
using GHelper.Linux.USB;

namespace GHelper.Linux.UI.Views;

/// <summary>
/// Extra settings window - keyboard backlight power zones,
/// display, power management, system info, advanced options.
/// Linux port of G-Helper's Extra form.
/// </summary>
public partial class ExtraWindow : Window
{
    private bool _suppressEvents = true;

    /// <summary>PID of the systemd-inhibit process for clamshell mode, or -1 if inactive.</summary>
    private static int _clamshellInhibitPid = -1;

    /// <summary>Polls display brightness sysfs every 2s to catch external changes (physical Fn keys).</summary>
    private Avalonia.Threading.DispatcherTimer? _brightnessTimer;

    public ExtraWindow()
    {
        InitializeComponent();
        Labels.LanguageChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _suppressEvents = true;
            ApplyLabels();
            _suppressEvents = false;
        });
        Loaded += (_, _) =>
        {
            _suppressEvents = true;
            InitLanguage();
            InitKeyboardBacklight();
            InitKeyBindings();
            RefreshDisplay();
            RefreshGpuTuning();
            RefreshOther();
            RefreshPower();
            RefreshSystemInfo();
            RefreshAdvanced();
            ApplyLabels();
            _suppressEvents = false;

            StartBrightnessPolling();
        };
        Closed += (_, _) => _brightnessTimer?.Stop();
    }

    // LANGUAGE

    private void InitLanguage()
    {
        comboLanguage.Items.Clear();

        // First item: Auto (system locale)
        comboLanguage.Items.Add(new ComboBoxItem { Content = Labels.Get("language_auto"), Tag = "" });

        // All available languages
        int selectedIdx = 0;
        int idx = 1;
        string? savedLang = Helpers.AppConfig.GetString("language");

        foreach (var (code, name) in Labels.AvailableLanguages)
        {
            comboLanguage.Items.Add(new ComboBoxItem { Content = name, Tag = code });
            if (!string.IsNullOrEmpty(savedLang) && code == savedLang)
                selectedIdx = idx;
            idx++;
        }

        comboLanguage.SelectedIndex = selectedIdx;
    }

    private void ComboLanguage_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents)
            return;
        if (comboLanguage.SelectedItem is not ComboBoxItem item)
            return;

        string code = item.Tag as string ?? "";

        if (string.IsNullOrEmpty(code))
            Labels.ResetToAuto();
        else
            Labels.SetLanguage(code);
    }

    private void ApplyLabels()
    {
        Title = Labels.Get("extra_title");

        // Language section
        labelLanguageHeader.Text = Labels.Get("language_header");
        labelLanguageLabel.Text = Labels.Get("language_header");

        // Keyboard Backlight
        headerKbdBacklight.Text = Labels.Get("kbd_backlight_header");
        labelKbdBrightnessLabel.Text = Labels.Get("brightness");
        labelAnimSpeed.Text = Labels.Get("animation_speed");
        labelPowerZones.Text = Labels.Get("power_zones");
        labelColAwake.Text = Labels.Get("awake");
        labelColBoot.Text = Labels.Get("boot");
        labelColSleep.Text = Labels.Get("sleep");
        labelColShutdown.Text = Labels.Get("shutdown");
        labelColBattery.Text = Labels.Get("battery");
        labelRowKeyboard.Text = Labels.Get("kbd_keyboard");
        labelRowLogo.Text = Labels.Get("kbd_logo");
        labelRowLightbar.Text = Labels.Get("kbd_lightbar");
        labelRowLid.Text = Labels.Get("kbd_lid");

        // Display
        headerDisplay.Text = Labels.Get("display_header");
        labelControllerLabel.Text = Labels.Get("controller");
        labelDisplayBrightnessLabel.Text = Labels.Get("brightness");
        checkOverdrive.Content = Labels.Get("panel_overdrive_check");
        labelGammaLabel.Text = Labels.Get("gamma");

        // GPU Tuning
        headerGpuTuning.Text = Labels.Get("gpu_tuning_header");
        labelPowerLimitLabel.Text = Labels.Get("power_limit");
        labelClockLockLabel.Text = Labels.Get("clock_lock");
        buttonGpuApply.Content = Labels.Get("apply_gpu_settings");

        // Other
        headerOther.Text = Labels.Get("other_header");
        checkBootSound.Content = Labels.Get("boot_sound");
        checkPerKeyRGB.Content = Labels.Get("per_key_rgb");
        checkTopmost.Content = Labels.Get("window_topmost");
        checkBWIcon.Content = Labels.Get("bw_tray_icon");
        checkClamshell.Content = Labels.Get("clamshell_mode");
        checkSilentStart.Content = Labels.Get("start_minimized");
        checkCamera.Content = Labels.Get("camera");
        checkTouchpad.Content = Labels.Get("touchpad");
        checkTouchscreen.Content = Labels.Get("touchscreen");

        // Key Bindings
        headerKeyBindings.Text = Labels.Get("key_bindings_header");
        labelKeyM4.Text = Labels.Get("key_rog_m5");
        labelKeyFnF4.Text = Labels.Get("key_fnf4_aura");
        labelKeyFnF5.Text = Labels.Get("key_fnf5_m4");

        // Power Management
        headerPowerMgmt.Text = Labels.Get("power_mgmt_header");
        labelPlatformProfileLabel.Text = Labels.Get("platform_profile");
        labelAspmLabel.Text = Labels.Get("pcie_aspm");
        labelBatteryDetails.Text = Labels.Get("details");

        // System Info
        headerSystemInfo.Text = Labels.Get("system_info_header");

        // Advanced
        headerAdvanced.Text = Labels.Get("advanced_header");
        checkAutoApplyPower.Content = Labels.Get("auto_apply_power");
        checkRawWmi.Content = Labels.Get("raw_wmi_mode");
        labelRawWmiHint.Text = Labels.Get("raw_wmi_hint");
        checkScreenAuto.Content = Labels.Get("auto_switch_refresh");
        labelCpuCoresLabel.Text = Labels.Get("cpu_cores");
        buttonOpenLog.Content = Labels.Get("open_log");

        // Rebuild combo items with new language strings
        RefreshSpeedCombo();
        RefreshKeyBindingCombos();
        RefreshLanguageComboAuto();

        // Refresh dynamic content with new labels
        RefreshSystemInfo();
        RefreshPower();
        RefreshGpuTuning();
    }

    /// <summary>Rebuild keyboard speed combo with current language strings.</summary>
    private void RefreshSpeedCombo()
    {
        int savedSpeed = comboKbdSpeed.SelectedItem is ComboBoxItem sel && sel.Tag is int s ? s : (int)Aura.Speed;
        comboKbdSpeed.Items.Clear();
        int selectedIdx = 0, idx = 0;
        foreach (var kv in Aura.GetSpeeds())
        {
            comboKbdSpeed.Items.Add(new ComboBoxItem { Content = kv.Value, Tag = (int)kv.Key });
            if ((int)kv.Key == savedSpeed)
                selectedIdx = idx;
            idx++;
        }
        comboKbdSpeed.SelectedIndex = selectedIdx;
    }

    /// <summary>Rebuild key binding combos with current language strings.</summary>
    private void RefreshKeyBindingCombos()
    {
        foreach (var (combo, bindingName) in _keyBindingCombos)
            PopulateKeyBindingCombo(combo, bindingName);
    }

    /// <summary>Update the "Auto (system)" label in the language combo.</summary>
    private void RefreshLanguageComboAuto()
    {
        if (comboLanguage.Items.Count > 0 && comboLanguage.Items[0] is ComboBoxItem first)
            first.Content = Labels.Get("language_auto");
    }

    // KEYBOARD BACKLIGHT

    private void InitKeyboardBacklight()
    {
        // Brightness (0-3)
        int brightness = App.Wmi?.GetKeyboardBrightness() ?? 3;
        sliderKbdBrightness.Value = brightness;
        labelKbdBrightness.Text = brightness.ToString();

        // Speed combo
        var speeds = Aura.GetSpeeds();
        comboKbdSpeed.Items.Clear();
        int selectedSpeedIdx = 0;
        int idx = 0;
        foreach (var kv in speeds)
        {
            comboKbdSpeed.Items.Add(new ComboBoxItem { Content = kv.Value, Tag = (int)kv.Key });
            if (kv.Key == Aura.Speed)
                selectedSpeedIdx = idx;
            idx++;
        }
        comboKbdSpeed.SelectedIndex = selectedSpeedIdx;

        // Power zones
        bool hasZones = Helpers.AppConfig.IsBacklightZones();
        bool isLimited = Helpers.AppConfig.IsStrixLimitedRGB() || Helpers.AppConfig.IsARCNM();

        // Keyboard
        checkAwake.IsChecked = Helpers.AppConfig.IsNotFalse("keyboard_awake");
        checkBoot.IsChecked = Helpers.AppConfig.IsNotFalse("keyboard_boot");
        checkSleep.IsChecked = Helpers.AppConfig.IsNotFalse("keyboard_sleep");
        checkShutdown.IsChecked = Helpers.AppConfig.IsNotFalse("keyboard_shutdown");
        checkBattery.IsChecked = Helpers.AppConfig.IsOnBattery("keyboard_awake");

        // Logo
        checkAwakeLogo.IsChecked = Helpers.AppConfig.IsNotFalse("keyboard_awake_logo");
        checkBootLogo.IsChecked = Helpers.AppConfig.IsNotFalse("keyboard_boot_logo");
        checkSleepLogo.IsChecked = Helpers.AppConfig.IsNotFalse("keyboard_sleep_logo");
        checkShutdownLogo.IsChecked = Helpers.AppConfig.IsNotFalse("keyboard_shutdown_logo");
        checkBatteryLogo.IsChecked = Helpers.AppConfig.IsOnBattery("keyboard_awake_logo");

        // Lightbar
        checkAwakeBar.IsChecked = Helpers.AppConfig.IsNotFalse("keyboard_awake_bar");
        checkBootBar.IsChecked = Helpers.AppConfig.IsNotFalse("keyboard_boot_bar");
        checkSleepBar.IsChecked = Helpers.AppConfig.IsNotFalse("keyboard_sleep_bar");
        checkShutdownBar.IsChecked = Helpers.AppConfig.IsNotFalse("keyboard_shutdown_bar");
        checkBatteryBar.IsChecked = Helpers.AppConfig.IsOnBattery("keyboard_awake_bar");

        // Lid
        checkAwakeLid.IsChecked = Helpers.AppConfig.IsNotFalse("keyboard_awake_lid");
        checkBootLid.IsChecked = Helpers.AppConfig.IsNotFalse("keyboard_boot_lid");
        checkSleepLid.IsChecked = Helpers.AppConfig.IsNotFalse("keyboard_sleep_lid");
        checkShutdownLid.IsChecked = Helpers.AppConfig.IsNotFalse("keyboard_shutdown_lid");
        checkBatteryLid.IsChecked = Helpers.AppConfig.IsOnBattery("keyboard_awake_lid");

        // Visibility rules from original
        if (!hasZones || isLimited)
        {
            if (!Helpers.AppConfig.IsStrixLimitedRGB())
            {
                rowPowerBar.IsVisible = false;
                rowPowerKeyboard.FindControl<CheckBox>("checkBattery")!.IsVisible =
                    Helpers.AppConfig.IsBacklightZones();
            }

            rowPowerLid.IsVisible = false;
            rowPowerLogo.IsVisible = false;
        }

        if (Helpers.AppConfig.IsZ13())
        {
            rowPowerBar.IsVisible = false;
            rowPowerLid.IsVisible = false;
        }
    }

    /// <summary>Update keyboard brightness slider from external change (physical Fn key press).</summary>
    public void RefreshKeyboardBrightness()
    {
        int brightness = App.Wmi?.GetKeyboardBrightness() ?? -1;
        if (brightness < 0)
            return;

        _suppressEvents = true;
        sliderKbdBrightness.Value = brightness;
        labelKbdBrightness.Text = brightness.ToString();
        _suppressEvents = false;
    }

    /// <summary>Poll display + keyboard brightness every 2s to catch external changes.</summary>
    private void StartBrightnessPolling()
    {
        _brightnessTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _brightnessTimer.Tick += (_, _) =>
        {
            // Display brightness
            var display = App.Display;
            if (display != null && rowBrightnessSlider.IsVisible)
            {
                int brightness = display.GetBrightness();
                if (brightness >= 0 && brightness != (int)sliderBrightness.Value)
                {
                    _suppressEvents = true;
                    sliderBrightness.Value = Math.Max(brightness, LinuxDisplayControl.MinBrightnessPercent);
                    labelBrightness.Text = $"{brightness}%";
                    _suppressEvents = false;
                }
            }

            // Keyboard brightness
            int kbdBrightness = App.Wmi?.GetKeyboardBrightness() ?? -1;
            if (kbdBrightness >= 0 && kbdBrightness != (int)sliderKbdBrightness.Value)
            {
                _suppressEvents = true;
                sliderKbdBrightness.Value = kbdBrightness;
                labelKbdBrightness.Text = kbdBrightness.ToString();
                _suppressEvents = false;
            }
        };
        _brightnessTimer.Start();
    }

    private void SliderKbdBrightness_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressEvents)
            return;
        int level = (int)e.NewValue;
        labelKbdBrightness.Text = level.ToString();
        Helpers.AppConfig.Set("keyboard_brightness", level);
        Aura.ApplyBrightness(level, "KbdSlider");
        App.MainWindowInstance?.RefreshKeyboard();
    }

    private void ComboKbdSpeed_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents)
            return;
        if (comboKbdSpeed.SelectedItem is ComboBoxItem item && item.Tag is int speedVal)
        {
            Helpers.AppConfig.Set("aura_speed", speedVal);
            Aura.Speed = (AuraSpeed)speedVal;
            Task.Run(() => Aura.ApplyAura());
        }
    }

    private void CheckPower_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;

        // Save all power zone states
        Helpers.AppConfig.Set("keyboard_awake", (checkAwake.IsChecked ?? false) ? 1 : 0);
        Helpers.AppConfig.Set("keyboard_boot", (checkBoot.IsChecked ?? false) ? 1 : 0);
        Helpers.AppConfig.Set("keyboard_sleep", (checkSleep.IsChecked ?? false) ? 1 : 0);
        Helpers.AppConfig.Set("keyboard_shutdown", (checkShutdown.IsChecked ?? false) ? 1 : 0);

        Helpers.AppConfig.Set("keyboard_awake_bar", (checkAwakeBar.IsChecked ?? false) ? 1 : 0);
        Helpers.AppConfig.Set("keyboard_boot_bar", (checkBootBar.IsChecked ?? false) ? 1 : 0);
        Helpers.AppConfig.Set("keyboard_sleep_bar", (checkSleepBar.IsChecked ?? false) ? 1 : 0);
        Helpers.AppConfig.Set("keyboard_shutdown_bar", (checkShutdownBar.IsChecked ?? false) ? 1 : 0);

        Helpers.AppConfig.Set("keyboard_awake_lid", (checkAwakeLid.IsChecked ?? false) ? 1 : 0);
        Helpers.AppConfig.Set("keyboard_boot_lid", (checkBootLid.IsChecked ?? false) ? 1 : 0);
        Helpers.AppConfig.Set("keyboard_sleep_lid", (checkSleepLid.IsChecked ?? false) ? 1 : 0);
        Helpers.AppConfig.Set("keyboard_shutdown_lid", (checkShutdownLid.IsChecked ?? false) ? 1 : 0);

        Helpers.AppConfig.Set("keyboard_awake_logo", (checkAwakeLogo.IsChecked ?? false) ? 1 : 0);
        Helpers.AppConfig.Set("keyboard_boot_logo", (checkBootLogo.IsChecked ?? false) ? 1 : 0);
        Helpers.AppConfig.Set("keyboard_sleep_logo", (checkSleepLogo.IsChecked ?? false) ? 1 : 0);
        Helpers.AppConfig.Set("keyboard_shutdown_logo", (checkShutdownLogo.IsChecked ?? false) ? 1 : 0);

        // Battery variants
        if (Helpers.AppConfig.IsBacklightZones())
        {
            Helpers.AppConfig.Set("keyboard_awake_bat", (checkBattery.IsChecked ?? false) ? 1 : 0);
            Helpers.AppConfig.Set("keyboard_awake_bar_bat", (checkBatteryBar.IsChecked ?? false) ? 1 : 0);
            Helpers.AppConfig.Set("keyboard_awake_lid_bat", (checkBatteryLid.IsChecked ?? false) ? 1 : 0);
            Helpers.AppConfig.Set("keyboard_awake_logo_bat", (checkBatteryLogo.IsChecked ?? false) ? 1 : 0);
        }

        // Apply via HID
        Task.Run(() => Aura.ApplyPower());
    }

    // KEY BINDINGS

    /// <summary>Maps combo box controls to their config key names.</summary>
    private readonly Dictionary<ComboBox, string> _keyBindingCombos = new();

    private void InitKeyBindings()
    {
        _keyBindingCombos[comboKeyM4] = "m4";
        _keyBindingCombos[comboKeyFnF4] = "fnf4";
        _keyBindingCombos[comboKeyFnF5] = "fnf5";

        foreach (var (combo, bindingName) in _keyBindingCombos)
        {
            PopulateKeyBindingCombo(combo, bindingName);
        }
    }

    private void PopulateKeyBindingCombo(ComboBox combo, string bindingName)
    {
        combo.Items.Clear();

        string currentAction = App.GetKeyAction(bindingName);
        int selectedIdx = 0;
        int idx = 0;

        foreach (var (actionId, _) in App.AvailableKeyActions)
        {
            combo.Items.Add(new ComboBoxItem { Content = App.GetKeyActionDisplayName(actionId), Tag = actionId });
            if (actionId == currentAction)
                selectedIdx = idx;
            idx++;
        }

        combo.SelectedIndex = selectedIdx;
    }

    private void ComboKeyBinding_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents)
            return;
        if (sender is not ComboBox combo)
            return;
        if (!_keyBindingCombos.TryGetValue(combo, out string? bindingName))
            return;
        if (combo.SelectedItem is not ComboBoxItem item || item.Tag is not string actionId)
            return;

        Helpers.AppConfig.Set(bindingName, actionId);
        Helpers.Logger.WriteLine($"Key binding: {bindingName} → {actionId}");
    }

    // DISPLAY

    /// <summary>Module name stored when the enable-backlight button is shown.</summary>
    private string? _pendingBacklightModule;

    private void RefreshDisplay()
    {
        var display = App.Display as LinuxDisplayControl;
        if (display == null)
            return;

        // Populate backlight controller dropdown
        var backlights = LinuxDisplayControl.GetAvailableBacklights();
        if (backlights.Count > 1)
        {
            _suppressEvents = true;
            comboBacklight.ItemsSource = backlights;
            var active = display.ActiveBacklightName;
            if (active != null && backlights.Contains(active))
                comboBacklight.SelectedItem = active;
            _suppressEvents = false;
            rowBacklightSelector.IsVisible = true;
        }
        else
        {
            rowBacklightSelector.IsVisible = false;
        }

        if (display.HasBacklight)
        {
            // Backlight available - show slider
            rowBrightnessSlider.IsVisible = true;
            labelBacklightHint.IsVisible = false;

            int brightness = display.GetBrightness();
            if (brightness >= 0)
            {
                sliderBrightness.Value = Math.Max(brightness, LinuxDisplayControl.MinBrightnessPercent);
                labelBrightness.Text = $"{brightness}%";
            }

            // Even with a backlight, offer module load if nvidia is active but nvidia backlight is missing
            _pendingBacklightModule = LinuxDisplayControl.GetMissingBacklightModule();
            if (_pendingBacklightModule != null)
            {
                buttonEnableBacklight.Content = Labels.Format("load_module", _pendingBacklightModule);
                buttonEnableBacklight.IsVisible = true;
                buttonEnableBacklight.IsEnabled = true;
            }
            else
            {
                buttonEnableBacklight.IsVisible = false;
            }
        }
        else
        {
            // No backlight - hide slider, check if we can offer a fix
            rowBrightnessSlider.IsVisible = false;

            _pendingBacklightModule = LinuxDisplayControl.GetMissingBacklightModule();
            if (_pendingBacklightModule != null)
            {
                buttonEnableBacklight.Content = Labels.Format("enable_backlight_load", _pendingBacklightModule);
                buttonEnableBacklight.IsVisible = true;
                buttonEnableBacklight.IsEnabled = true;
                labelBacklightHint.IsVisible = false;
            }
            else
            {
                buttonEnableBacklight.IsVisible = false;
                labelBacklightHint.Text = LinuxDisplayControl.GetBacklightHint();
                labelBacklightHint.IsVisible = true;
            }
        }

        bool overdrive = App.Wmi?.GetPanelOverdrive() ?? false;
        checkOverdrive.IsChecked = overdrive;

        // Gamma only works on X11 (xrandr). Hide on Wayland backends.
        bool supportsGamma = display.Backend?.SupportsGamma ?? false;
        rowGamma.IsVisible = supportsGamma;
        if (supportsGamma)
            sliderGamma.Value = 100;
    }

    private void ButtonEnableBacklight_Click(object? sender, RoutedEventArgs e)
    {
        if (_pendingBacklightModule == null)
            return;

        string module = _pendingBacklightModule;
        buttonEnableBacklight.Content = Labels.Get("loading");
        buttonEnableBacklight.IsEnabled = false;

        // Run modprobe on background thread (pkexec shows password dialog)
        Task.Run(() =>
        {
            var display = App.Display as LinuxDisplayControl;
            bool success = display?.TryLoadBacklightModule(module) ?? false;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _suppressEvents = true;
                RefreshDisplay();
                _suppressEvents = false;

                if (!success)
                {
                    buttonEnableBacklight.IsVisible = false;
                    labelBacklightHint.Text = Labels.Get("backlight_failed") + "\n" + LinuxDisplayControl.GetBacklightHint();
                    labelBacklightHint.IsVisible = true;
                }
            });
        });
    }

    private void ComboBacklight_SelectionChanged(object? sender,
        Avalonia.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressEvents)
            return;
        var display = App.Display as LinuxDisplayControl;
        if (display == null)
            return;

        var selected = comboBacklight.SelectedItem as string;
        if (selected == null || selected == display.ActiveBacklightName)
            return;

        display.SetActiveBacklight(selected);

        // Refresh slider to show brightness from the new controller
        _suppressEvents = true;
        int brightness = display.GetBrightness();
        if (brightness >= 0)
        {
            sliderBrightness.Value = Math.Max(brightness, LinuxDisplayControl.MinBrightnessPercent);
            labelBrightness.Text = $"{brightness}%";
        }
        _suppressEvents = false;
    }

    private void SliderBrightness_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressEvents)
            return;
        int percent = (int)e.NewValue;
        labelBrightness.Text = $"{percent}%";
        App.Display?.SetBrightness(percent);
    }

    private void CheckOverdrive_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;
        bool enabled = checkOverdrive.IsChecked ?? false;
        App.Wmi?.SetPanelOverdrive(enabled);
    }

    private void SliderGamma_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressEvents)
            return;
        float gamma = (float)(e.NewValue / 100.0);
        labelGamma.Text = $"{gamma:F2}";
        App.Display?.SetGamma(gamma, gamma, gamma);
    }

    // OTHER

    private void RefreshOther()
    {
        // Boot sound
        int bootSound = Helpers.AppConfig.Get("boot_sound", 0);
        checkBootSound.IsChecked = bootSound == 1;

        // Per-key RGB (only visible if 4-zone is possible)
        checkPerKeyRGB.IsVisible = Helpers.AppConfig.IsPossible4ZoneRGB();
        checkPerKeyRGB.IsChecked = Helpers.AppConfig.Is("per_key_rgb");

        // Window always on top
        checkTopmost.IsChecked = Helpers.AppConfig.Is("topmost");
        if (Helpers.AppConfig.Is("topmost"))
            this.Topmost = true;

        // B&W tray icon
        checkBWIcon.IsChecked = Helpers.AppConfig.IsBWIcon();

        // Clamshell mode
        checkClamshell.IsChecked = Helpers.AppConfig.Is("toggle_clamshell_mode");

        // Silent start (minimized to tray)
        checkSilentStart.IsChecked = Helpers.AppConfig.Is("silent_start");

        // Camera
        checkCamera.IsChecked = LinuxSystemIntegration.IsCameraEnabled();

        // Touchpad (hide if not found)
        var touchpadState = LinuxSystemIntegration.IsTouchpadEnabled();
        if (touchpadState == null)
        {
            checkTouchpad.IsVisible = false;
        }
        else
        {
            checkTouchpad.IsVisible = true;
            checkTouchpad.IsChecked = touchpadState.Value;
        }

        // Touchscreen (hide if not found)
        var touchscreenState = LinuxSystemIntegration.IsTouchscreenEnabled();
        if (touchscreenState == null)
        {
            checkTouchscreen.IsVisible = false;
        }
        else
        {
            checkTouchscreen.IsVisible = true;
            checkTouchscreen.IsChecked = touchscreenState.Value;
        }
    }

    private void CheckBootSound_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;
        int val = (checkBootSound.IsChecked ?? false) ? 1 : 0;
        Helpers.AppConfig.Set("boot_sound", val);

        // Try to set via sysfs (asus-nb-wmi or asus-armoury firmware-attributes)
        try
        {
            var path = Platform.Linux.SysfsHelper.ResolveAttrPath(Platform.Linux.AsusAttributes.BootSound);
            if (path != null)
                Platform.Linux.SysfsHelper.WriteAttribute(path, val.ToString());
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"Boot sound write failed: {ex.Message}");
        }

        Helpers.Logger.WriteLine($"Boot sound → {val}");
    }

    private void CheckPerKeyRGB_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;
        Helpers.AppConfig.Set("per_key_rgb", (checkPerKeyRGB.IsChecked ?? false) ? 1 : 0);
        Helpers.Logger.WriteLine($"Per-key RGB → {checkPerKeyRGB.IsChecked}");
        // Re-apply aura so the mode change takes effect immediately
        Task.Run(() => Aura.ApplyAura());
    }

    private void CheckTopmost_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;
        bool on = checkTopmost.IsChecked ?? false;
        Helpers.AppConfig.Set("topmost", on ? 1 : 0);

        // Apply to ALL open windows, not just this one
        App.SetTopmostAll(on);
    }

    private void CheckBWIcon_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;
        Helpers.AppConfig.Set("bw_icon", (checkBWIcon.IsChecked ?? false) ? 1 : 0);
        Helpers.Logger.WriteLine($"B&W tray icon → {checkBWIcon.IsChecked}");
        // Update the tray icon immediately
        App.UpdateTrayIcon();
    }

    private void CheckClamshell_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;
        bool on = checkClamshell.IsChecked ?? false;
        Helpers.AppConfig.Set("toggle_clamshell_mode", on ? 1 : 0);

        // Toggle lid switch handling via systemd-inhibit (runs as current user, no root needed)
        try
        {
            if (on)
            {
                StartClamshellInhibit();
            }
            else
            {
                StopClamshellInhibit();
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"Clamshell mode toggle failed: {ex.Message}");
        }
    }

    private void CheckSilentStart_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;
        Helpers.AppConfig.Set("silent_start", (checkSilentStart.IsChecked ?? false) ? 1 : 0);
    }

    /// <summary>Start a systemd-inhibit process that prevents lid-close suspend.</summary>
    public static void StartClamshellInhibit()
    {
        StopClamshellInhibit(); // Kill any existing one first

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "systemd-inhibit",
                Arguments = "--what=handle-lid-switch --who=\"G-Helper\" --why=\"Clamshell mode\" sleep infinity",
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true,
            };

            var proc = Process.Start(psi);
            if (proc != null)
            {
                _clamshellInhibitPid = proc.Id;
                Helpers.Logger.WriteLine($"Clamshell mode ON (inhibit PID {_clamshellInhibitPid})");
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"Failed to start systemd-inhibit: {ex.Message}");
        }
    }

    /// <summary>Kill the systemd-inhibit process, restoring normal lid behavior.</summary>
    public static void StopClamshellInhibit()
    {
        if (_clamshellInhibitPid > 0)
        {
            try
            {
                var proc = Process.GetProcessById(_clamshellInhibitPid);
                proc.Kill();
                proc.WaitForExit(2000);
                Helpers.Logger.WriteLine($"Clamshell mode OFF (killed PID {_clamshellInhibitPid})");
            }
            catch
            {
                // Process already exited
            }

            _clamshellInhibitPid = -1;
        }
    }

    private void CheckCamera_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;
        bool enabled = checkCamera.IsChecked ?? true;
        LinuxSystemIntegration.SetCameraEnabled(enabled);
        App.System?.ShowNotification(Labels.Get("camera"),
            enabled ? Labels.Get("enabled") : Labels.Get("disabled"),
            enabled ? "camera-on" : "camera-off");
    }

    private void CheckTouchpad_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;
        bool enabled = checkTouchpad.IsChecked ?? true;
        LinuxSystemIntegration.SetTouchpadEnabled(enabled);
        App.System?.ShowNotification(Labels.Get("touchpad"),
            enabled ? Labels.Get("enabled") : Labels.Get("disabled"),
            enabled ? "input-touchpad-on" : "input-touchpad-off");
    }

    private void CheckTouchscreen_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;
        bool enabled = checkTouchscreen.IsChecked ?? true;
        LinuxSystemIntegration.SetTouchscreenEnabled(enabled);
        App.System?.ShowNotification(Labels.Get("touchscreen"),
            enabled ? Labels.Get("enabled") : Labels.Get("disabled"),
            "preferences-desktop-touchscreen");
    }


    private LinuxNvidiaGpuControl? _nvidiaGpu;

    private void RefreshGpuTuning()
    {
        _nvidiaGpu = App.GpuControl as LinuxNvidiaGpuControl;
        if (_nvidiaGpu == null || !_nvidiaGpu.IsAvailable())
        {
            panelGpuTuning.IsVisible = false;
            return;
        }

        panelGpuTuning.IsVisible = true;
        labelGpuTuningInfo.Text = _nvidiaGpu.GetGpuName() ?? Labels.Get("nvidia_gpu");

        var limits = _nvidiaGpu.GetPowerLimits();
        if (limits != null)
        {
            var (defW, minW, maxW, enfW) = limits.Value;
            sliderGpuPowerLimit.Minimum = minW;
            sliderGpuPowerLimit.Maximum = maxW;
            sliderGpuPowerLimit.Value = enfW > 0 ? enfW : defW;
            labelGpuPowerLimit.Text = $"{(int)sliderGpuPowerLimit.Value}W";
            labelGpuTuningInfo.Text += Labels.Format("gpu_info_format", defW, minW, maxW);
        }

        checkGpuClockLock.IsChecked = false;
        sliderGpuClockLock.IsEnabled = false;
        labelGpuClockLock.Text = Labels.Get("off");
    }

    private void SliderGpuPowerLimit_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressEvents)
            return;
        labelGpuPowerLimit.Text = $"{(int)e.NewValue}W";
    }

    private void CheckGpuClockLock_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;
        bool enabled = checkGpuClockLock.IsChecked ?? false;
        sliderGpuClockLock.IsEnabled = enabled;
        labelGpuClockLock.Text = enabled ? $"{(int)sliderGpuClockLock.Value} MHz" : Labels.Get("off");
    }

    private void SliderGpuClockLock_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressEvents)
            return;
        labelGpuClockLock.Text = $"{(int)e.NewValue} MHz";
    }

    private void ButtonGpuApply_Click(object? sender, RoutedEventArgs e)
    {
        if (_nvidiaGpu == null)
            return;

        buttonGpuApply.IsEnabled = false;
        buttonGpuApply.Content = Labels.Get("applying");

        int powerW = (int)sliderGpuPowerLimit.Value;
        bool clockLock = checkGpuClockLock.IsChecked ?? false;
        int clockMhz = (int)sliderGpuClockLock.Value;

        Task.Run(() =>
        {
            _nvidiaGpu.ApplyGpuSettings(powerW, clockLock ? clockMhz : 0);

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                buttonGpuApply.Content = Labels.Get("apply_gpu_settings");
                buttonGpuApply.IsEnabled = true;
                App.System?.ShowNotification(Labels.Get("gpu_tuning_notify"),
                    Labels.Format("gpu_power_format", powerW) + (clockLock ? Labels.Format("gpu_clock_format", clockMhz) : ""),
                    "dialog-information");
            });
        });
    }

    // POWER MANAGEMENT

    private void RefreshPower()
    {
        var power = App.Power;
        if (power == null)
            return;

        // Platform profile
        string profile = power.GetPlatformProfile();
        for (int i = 0; i < comboPlatformProfile.Items.Count; i++)
        {
            if (comboPlatformProfile.Items[i] is ComboBoxItem item &&
                item.Content?.ToString() == profile)
            {
                comboPlatformProfile.SelectedIndex = i;
                break;
            }
        }

        // ASPM
        string aspm = power.GetAspmPolicy();
        for (int i = 0; i < comboAspm.Items.Count; i++)
        {
            if (comboAspm.Items[i] is ComboBoxItem item &&
                item.Content?.ToString() == aspm)
            {
                comboAspm.SelectedIndex = i;
                break;
            }
        }

        // Battery health
        int health = power.GetBatteryHealth();
        if (health >= 0)
            labelBatteryHealth.Text = Labels.Format("battery_health_format", health);
        else
            labelBatteryHealth.Text = Labels.Get("battery_health_unknown");

        int drain = power.GetBatteryDrainRate();
        if (drain != 0)
            labelPowerDraw.Text = drain > 0
                ? Labels.Format("power_draw_discharge", drain)
                : Labels.Format("power_draw_charge", -drain);
        else
            labelPowerDraw.Text = Labels.Get("power_draw_unknown");
    }

    private void ComboPlatformProfile_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents)
            return;
        if (comboPlatformProfile.SelectedItem is ComboBoxItem item && item.Content is string profile)
        {
            App.Power?.SetPlatformProfile(profile);
            Helpers.Logger.WriteLine($"Platform profile → {profile}");
            App.MainWindowInstance?.RefreshPerformanceMode();
        }
    }

    private void ComboAspm_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents)
            return;
        if (comboAspm.SelectedItem is ComboBoxItem item && item.Content is string policy)
        {
            App.Power?.SetAspmPolicy(policy);
            Helpers.Logger.WriteLine($"ASPM policy → {policy}");
        }
    }

    private BatteryInfoWindow? _batteryInfoWindow;

    private void ButtonBatteryInfo_Click(object? sender, RoutedEventArgs e)
    {
        if (_batteryInfoWindow == null || !_batteryInfoWindow.IsVisible)
        {
            _batteryInfoWindow = new BatteryInfoWindow();
            if (Helpers.AppConfig.Is("topmost"))
                _batteryInfoWindow.Topmost = true;
            _batteryInfoWindow.Show();
        }
        else
        {
            _batteryInfoWindow.Activate();
        }
    }

    // SYSTEM INFO

    private void RefreshSystemInfo()
    {
        var sys = App.System;
        if (sys == null)
            return;

        labelModel.Text = Labels.Format("model_prefix", sys.GetModelName());
        labelBios.Text = Labels.Format("bios_prefix", sys.GetBiosVersion());
        labelKernel.Text = Labels.Format("kernel_prefix", sys.GetKernelVersion());

        bool wmiLoaded = sys.IsAsusWmiLoaded();
        labelAsusWmi.Text = wmiLoaded ? Labels.Get("asus_wmi_loaded") : Labels.Get("asus_wmi_not_loaded");

        // Feature detection
        var features = new List<string>();
        var wmi = App.Wmi;
        if (wmi != null)
        {
            if (wmi.IsFeatureSupported(AsusAttributes.ThrottleThermalPolicy))
                features.Add(Labels.Get("feature_perf_modes"));
            if (wmi.IsFeatureSupported(AsusAttributes.DgpuDisable))
                features.Add(Labels.Get("feature_gpu_eco"));
            if (wmi.IsFeatureSupported(AsusAttributes.GpuMuxMode))
                features.Add(Labels.Get("feature_mux"));
            if (wmi.IsFeatureSupported(AsusAttributes.PanelOd))
                features.Add(Labels.Get("feature_overdrive"));
            if (wmi.IsFeatureSupported(AsusAttributes.MiniLedMode))
                features.Add(Labels.Get("feature_miniled"));
            if (wmi.IsFeatureSupported(AsusAttributes.PptPl1Spl))
                features.Add(Labels.Get("feature_ppt"));
            if (wmi.IsFeatureSupported(AsusAttributes.NvDynamicBoost))
                features.Add(Labels.Get("feature_nv_boost"));
        }

        labelFeatures.Text = features.Count > 0
            ? Labels.Format("features_prefix", string.Join(", ", features))
            : Labels.Get("no_features");

        // Kernel version check
        var kernelVer = sys.GetKernelVersionParsed();
        if (kernelVer < new Version(6, 2))
        {
            labelFeatures.Text += "\n" + Labels.Get("kernel_warning");
        }
    }

    // ADVANCED

    private void RefreshAdvanced()
    {
        checkAutoApplyPower.IsChecked = Helpers.AppConfig.IsMode("auto_apply_power");
        checkScreenAuto.IsChecked = Helpers.AppConfig.Is("screen_auto");
        checkRawWmi.IsChecked = Helpers.AppConfig.Is("raw_wmi");

        // CPU cores
        int total = LinuxSystemIntegration.GetCpuCount();
        int online = LinuxSystemIntegration.GetOnlineCpuCount();

        if (total > 1)
        {
            panelCpuCores.IsVisible = true;
            sliderCpuCores.Maximum = total;
            sliderCpuCores.Value = online;
            labelCpuCores.Text = Labels.Format("cpu_cores_format", online, total);
            labelCpuCoresInfo.Text = Labels.Format("cpu_cores_info", online, total);
        }
        else
        {
            panelCpuCores.IsVisible = false;
        }
    }

    private void CheckAutoApplyPower_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;
        bool enabled = checkAutoApplyPower.IsChecked ?? false;
        Helpers.AppConfig.SetMode("auto_apply_power", enabled ? 1 : 0);
    }

    private void CheckScreenAuto_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;
        bool enabled = checkScreenAuto.IsChecked ?? false;
        Helpers.AppConfig.Set("screen_auto", enabled ? 1 : 0);
        Helpers.Logger.WriteLine($"Screen auto refresh → {enabled}");
        if (enabled)
            (App.Current as App)?.AutoScreen();
        App.MainWindowInstance?.RefreshScreenPublic();
    }

    private void CheckRawWmi_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;
        bool enabled = checkRawWmi.IsChecked ?? false;

        Helpers.AppConfig.Set("raw_wmi", enabled ? 1 : 0);
        Helpers.AppConfig.Flush();
        Helpers.Logger.WriteLine($"Raw WMI mode → {enabled}, restarting app");

        // Restart app so the new setting takes effect immediately
        var exePath = Environment.ProcessPath;
        if (exePath != null)
        {
            System.Diagnostics.Process.Start(exePath);
            Environment.Exit(0);
        }
    }

    private void SliderCpuCores_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressEvents)
            return;
        int target = (int)e.NewValue;
        int total = LinuxSystemIntegration.GetCpuCount();
        labelCpuCores.Text = Labels.Format("cpu_cores_format", target, total);
        labelCpuCoresInfo.Text = Labels.Format("cpu_cores_info", target, total);

        // Apply in background to avoid UI stall
        Task.Run(() => LinuxSystemIntegration.SetOnlineCpuCount(target));
    }

    private void ButtonOpenLog_Click(object? sender, RoutedEventArgs e)
    {
        // Logger is stdout-only; open a terminal showing the app's output
        try
        {
            // Try to find the config dir for any saved logs
            var configDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "ghelper");
            if (Directory.Exists(configDir))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    Arguments = configDir,
                    UseShellExecute = false,
                });
            }
            else
            {
                Helpers.Logger.WriteLine("Logs are written to stdout - run the app from a terminal to see output");
                App.System?.ShowNotification(Labels.Get("ghelper"), Labels.Get("log_stdout"), "dialog-information");
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine("Failed to open config dir", ex);
        }
    }
}
