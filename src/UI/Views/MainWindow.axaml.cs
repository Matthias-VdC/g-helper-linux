using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using GHelper.Linux.Gpu;
using GHelper.Linux.I18n;
using GHelper.Linux.Platform.Linux;
using GHelper.Linux.USB;

namespace GHelper.Linux.UI.Views;

/// <summary>
/// Main settings window - Linux port of G-Helper's SettingsForm.
/// Mirrors the panel layout: Performance → GPU → Screen → Keyboard → Battery → Footer
/// </summary>
public partial class MainWindow : Window
{
    private readonly DispatcherTimer _refreshTimer;
    private int _batteryRefreshCounter;
    private int _lastKbdBrightness = -1;
    private int _currentPerfMode = -1;
    private int _currentGpuMode = -1;  // 0=Eco, 1=Standard, 2=Optimized (auto), 3=Ultimate (MUX=0)

    // Easter egg: click version label 7 times → arcade game
    private int _versionClickCount;
    private DateTime _versionClickStart;
    private ArcadeWindow? _arcadeWindow;

    // Donate button state
    private int _coinClickCount;
    private bool _coinMuted;
    private DispatcherTimer? _coinDebounceTimer;
    private DispatcherTimer? _coinShakeTimer;
    private int _coinShakeFrame;
    private TranslateTransform? _coinTransform;

    // Accent colors matching G-Helper's RForm.cs
    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.Parse("#4CC2FF"));
    private static readonly IBrush EcoBrush = new SolidColorBrush(Color.Parse("#06B48A"));
    private static readonly IBrush StandardBrush = new SolidColorBrush(Color.Parse("#3AAEEF"));
    private static readonly IBrush TurboBrush = new SolidColorBrush(Color.Parse("#FF2020"));
    private static readonly IBrush TransparentBrush = Brushes.Transparent;
    private static readonly IBrush CoinGoldBrush = new SolidColorBrush(Color.Parse("#FFD700"));
    private static readonly IBrush CoinDarkBrush = new SolidColorBrush(Color.Parse("#8B6914"));

    public MainWindow()
    {
        InitializeComponent();
        Labels.LanguageChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyLabels());
        InitDonate();

        // Refresh timer for live sensor data
        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _refreshTimer.Tick += (_, _) =>
        {
            RefreshSensorData();
            // Refresh battery every ~60s (30 ticks × 2s)
            if (++_batteryRefreshCounter >= 30)
            {
                _batteryRefreshCounter = 0;
                RefreshBattery();
            }
        };

        // On close: let the window actually close (dispose).
        // Don't cancel - this allows KDE logout/reboot to proceed.
        // The app stays alive via ShutdownMode.OnExplicitShutdown + tray icon.
        // A new MainWindow is created on tray click (see App.ToggleMainWindow).
        Closing += (_, _) =>
        {
            _refreshTimer.Stop();
            App.MainWindowInstance = null;
        };

        // Start sensor refresh timer immediately (works even in tray-only mode)
        _refreshTimer.Start();

        // Populate UI controls when the window becomes visible
        Loaded += (_, _) =>
        {
            RefreshAll();
        };
    }

    // Refresh / Init

    private void RefreshAll()
    {
        ApplyLabels();
        RefreshPerformanceMode();
        RefreshGpuMode();
        RefreshScreen();
        RefreshBattery();
        RefreshKeyboard();
        RefreshSensorData();
        RefreshFooter();
    }

    private void ApplyLabels()
    {
        // Button text (find the TextBlock children inside each button's StackPanel)
        SetButtonText(buttonSilent, Labels.Get("mode_silent"));
        SetButtonText(buttonBalanced, Labels.Get("mode_balanced"));
        SetButtonText(buttonTurbo, Labels.Get("mode_turbo"));
        SetButtonText(buttonFans, Labels.Get("fans_power"));
        SetButtonText(buttonEco, Labels.Get("gpu_eco"));
        SetButtonText(buttonStandard, Labels.Get("gpu_standard"));
        SetButtonText(buttonUltimate, Labels.Get("gpu_ultimate"));
        SetButtonText(buttonOptimized, Labels.Get("gpu_optimized"));
        SetButtonText(buttonKeyboard, Labels.Get("backlight"));
        SetButtonText(buttonExtra, Labels.Get("extra"));
        SetButtonText(buttonUpdates, Labels.Get("updates"));
        SetButtonText(buttonQuit, Labels.Get("quit"));
        labelDonateText.Text = Labels.Get("donate");

        buttonColor1.SetValue(Avalonia.Controls.ToolTip.TipProperty, Labels.Get("color_primary"));
        buttonColor2.SetValue(Avalonia.Controls.ToolTip.TipProperty, Labels.Get("color_secondary"));

        labelKeyboard.Text = Labels.Get("keyboard_header");
        checkStartup.Content = Labels.Get("run_on_startup");

        // Refresh dynamic labels
        RefreshPerformanceMode();
        RefreshGpuMode();
        RefreshScreen();
        RefreshBattery();
        RefreshKeyboard();
        RefreshFooter();
        RefreshAuraCombos();
    }

    /// <summary>Helper: set text of the last TextBlock inside a Button's StackPanel content.</summary>
    private static void SetButtonText(Button button, string text)
    {
        // Buttons use StackPanel > TextBlock (icon) + TextBlock (text)
        // We want the LAST TextBlock child
        if (button.Content is StackPanel sp)
        {
            var textBlock = sp.Children.OfType<Avalonia.Controls.TextBlock>().LastOrDefault();
            if (textBlock != null)
                textBlock.Text = text;
        }
        else if (button.Content is Avalonia.Controls.TextBlock tb)
        {
            tb.Text = text;
        }
    }

    private void RefreshSensorData()
    {
        try
        {
            var wmi = App.Wmi;
            if (wmi == null)
                return;

            int cpuTemp = wmi.DeviceGet(0x00120094); // Temp_CPU
            int gpuTemp = wmi.DeviceGet(0x00120097); // Temp_GPU
            int cpuFan = wmi.GetFanRpm(0);
            int gpuFan = wmi.GetFanRpm(1);

            string cpuTempStr = cpuTemp > 0 ? $"{cpuTemp}°C" : "--";
            string cpuFanStr = cpuFan > 0 ? $"{cpuFan}RPM" : "0RPM";

            // GPU fan: might be RPM or percentage from nvidia-smi
            string gpuFanStr;
            if (gpuFan > 0)
                gpuFanStr = $"{gpuFan}RPM";
            else if (gpuFan <= -2)
            {
                // Encoded percentage: -2 - percent
                int percent = -(gpuFan + 2);
                gpuFanStr = $"{percent}%";
            }
            else
                gpuFanStr = "0RPM";

            // Match Windows layout: "CPU: 32°C Fan: 0RPM" on the right
            labelCPUFan.Text = Labels.Format("cpu_fan_info", cpuTempStr, cpuFanStr);

            // GPU fan info - compact for right-aligned display
            string gpuTempStr = gpuTemp > 0 ? $"{gpuTemp}°C" : "";

            // GPU load: only show when dGPU is active (not in Eco mode)
            string gpuLoadStr = "";
            bool isEcoMode = wmi.GetGpuEco();
            if (!isEcoMode && App.GpuControl?.IsAvailable() == true)
            {
                try
                {
                    int? gpuLoad = App.GpuControl.GetGpuUse();
                    if (gpuLoad.HasValue && gpuLoad.Value >= 0)
                        gpuLoadStr = $" {gpuLoad.Value}%";
                }
                catch (Exception)
                {
                    Helpers.Logger.WriteLine("GPU load query failed (GPU may be transitioning)");
                }
            }

            labelGPUFan.Text = gpuTempStr.Length > 0
                ? Labels.Format("gpu_fan_full_info", gpuTempStr, gpuLoadStr, gpuFanStr)
                : Labels.Format("gpu_fan_only", gpuFanStr);

            // Mid fan if available
            int midFan = wmi.GetFanRpm(2);
            if (midFan > 0)
                labelMidFan.Text = Labels.Format("mid_fan_info", $"{midFan}RPM");

            // Keyboard brightness, detect external changes (physical keys, kernel driver)
            int kbdBrightness = wmi.GetKeyboardBrightness();
            if (kbdBrightness >= 0 && kbdBrightness != _lastKbdBrightness)
            {
                _lastKbdBrightness = kbdBrightness;
                RefreshKeyboard();
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine("RefreshSensorData error", ex);
        }
    }

    // Performance Mode

    public void RefreshPerformanceMode()
    {
        var wmi = App.Wmi;
        if (wmi == null)
            return;

        _currentPerfMode = wmi.GetThrottleThermalPolicy();

        string modeName = _currentPerfMode switch
        {
            0 => Labels.Get("mode_balanced"),
            1 => Labels.Get("mode_turbo"),
            2 => Labels.Get("mode_silent"),
            _ => Labels.Get("mode_unknown")
        };

        // Combined header: "Mode: Balanced" (matches Windows layout)
        labelPerf.Text = Labels.Format("mode_prefix", modeName);
        labelPerfMode.Text = modeName;
        UpdatePerfButtons();
    }

    private void UpdatePerfButtons()
    {
        SetButtonActive(buttonSilent, _currentPerfMode == 2);
        SetButtonActive(buttonBalanced, _currentPerfMode == 0);
        SetButtonActive(buttonTurbo, _currentPerfMode == 1);
    }

    private void SetPerformanceMode(int mode)
    {
        App.Mode?.SetPerformanceMode(mode);
        _currentPerfMode = mode;
        RefreshPerformanceMode();
        App.UpdateTrayIcon();
    }

    private void ButtonSilent_Click(object? sender, RoutedEventArgs e) => SetPerformanceMode(2);
    private void ButtonBalanced_Click(object? sender, RoutedEventArgs e) => SetPerformanceMode(0);
    private void ButtonTurbo_Click(object? sender, RoutedEventArgs e) => SetPerformanceMode(1);

    private FansWindow? _fansWindow;

    private void ButtonFans_Click(object? sender, RoutedEventArgs e)
    {
        if (_fansWindow == null || !_fansWindow.IsVisible)
        {
            _fansWindow = new FansWindow();
            if (Helpers.AppConfig.Is("topmost"))
                _fansWindow.Topmost = true;
            _fansWindow.Show();
        }
        else
        {
            _fansWindow.Activate();
        }
    }

    // GPU Mode

    /// <summary>Public wrappers for refresh methods - called from App.cs on power state changes.</summary>
    public void RefreshGpuModePublic() => RefreshGpuMode();
    public void RefreshBatteryPublic() => RefreshBattery();
    public void RefreshScreenPublic() => RefreshScreen();

    /// <summary>Forward keyboard brightness refresh to ExtraWindow if open.</summary>
    public void RefreshExtraKeyboardBrightness()
        => _extraWindow?.RefreshKeyboardBrightness();

    private void RefreshGpuMode()
    {
        var wmi = App.Wmi;
        if (wmi == null)
            return;

        // No GPU Eco support → hide entire GPU panel
        if (!wmi.IsGpuEcoAvailable())
        {
            panelGPU.IsVisible = false;
            return;
        }
        panelGPU.IsVisible = true;

        var gpu = App.GpuModeCtrl;

        if (gpu != null)
        {
            var mode = gpu.GetCurrentMode();
            _currentGpuMode = (int)mode;
        }
        else
        {
            // Fallback if controller not initialized
            bool gpuAuto = Helpers.AppConfig.Is("gpu_auto");
            bool ecoEnabled = wmi.GetGpuEco();
            int muxMode = wmi.GetGpuMuxMode();

            if (muxMode == 0)
                _currentGpuMode = 3;
            else if (gpuAuto)
                _currentGpuMode = 2;
            else if (ecoEnabled)
                _currentGpuMode = 0;
            else
                _currentGpuMode = 1;
        }

        string modeName = _currentGpuMode switch
        {
            0 => Labels.Get("gpu_eco"),
            1 => Labels.Get("gpu_standard"),
            2 => Labels.Get("gpu_optimized"),
            3 => Labels.Get("gpu_ultimate"),
            _ => Labels.Get("gpu_unknown")
        };

        labelGPU.Text = Labels.Format("gpu_mode_prefix", modeName);
        labelGPUMode.Text = modeName;
        UpdateGpuButtons();

        // GPU tip - check for pending reboot first
        if (gpu?.IsPendingReboot() == true)
        {
            string? pending = Helpers.AppConfig.GetString("gpu_mode");
            labelTipGPU.Text = Labels.Format("gpu_pending_reboot", pending?.ToUpperInvariant() ?? Labels.Get("gpu_pending_mode"));
        }
        else
        {
            labelTipGPU.Text = _currentGpuMode switch
            {
                0 => Labels.Get("gpu_tip_eco"),
                1 => Labels.Get("gpu_tip_standard"),
                2 => Labels.Get("gpu_tip_optimized"),
                3 => Labels.Get("gpu_tip_ultimate"),
                _ => ""
            };
        }

        buttonUltimate.IsVisible = wmi.IsFeatureSupported(AsusAttributes.GpuMuxMode);
    }

    private void UpdateGpuButtons()
    {
        SetButtonActive(buttonEco, _currentGpuMode == 0);
        SetButtonActive(buttonStandard, _currentGpuMode == 1);
        SetButtonActive(buttonOptimized, _currentGpuMode == 2);
        SetButtonActive(buttonUltimate, _currentGpuMode == 3);
    }

    /// <summary>
    /// Lock GPU mode buttons during a switch operation (like Windows G-Helper's LockGPUModes).
    /// Writing dgpu_disable can block in the kernel for 30-60 seconds while the GPU powers down.
    /// </summary>
    private void LockGpuButtons(string statusText)
    {
        buttonEco.IsEnabled = false;
        buttonStandard.IsEnabled = false;
        buttonOptimized.IsEnabled = false;
        buttonUltimate.IsEnabled = false;
        labelTipGPU.Text = statusText;
    }

    private void UnlockGpuButtons()
    {
        buttonEco.IsEnabled = true;
        buttonStandard.IsEnabled = true;
        buttonOptimized.IsEnabled = true;
        buttonUltimate.IsEnabled = true;
    }

    /// <summary>
    /// Common handler for all 4 GPU mode buttons.
    /// Locks buttons, calls GpuModeController on background thread,
    /// handles the result on UI thread.
    /// </summary>
    private void RequestGpuModeSwitch(GpuMode target, string switchingText)
    {
        var gpu = App.GpuModeCtrl;
        if (gpu == null)
            return;

        if (gpu.IsSwitchInProgress)
        {
            // A hardware switch is blocking (buttons should be locked, but be defensive).
            // Save the user's latest choice so it wins after reboot.
            gpu.ScheduleModeForReboot(target);
            _currentGpuMode = (int)target;
            UpdateGpuButtons();
            return;
        }

        // Optimistic UI: highlight the target button immediately
        _currentGpuMode = (int)target;
        UpdateGpuButtons();
        LockGpuButtons(switchingText);

        Task.Run(() =>
        {
            var result = gpu.RequestModeSwitch(target);

            Dispatcher.UIThread.Post(() =>
            {
                UnlockGpuButtons();
                HandleGpuSwitchResult(result, target);
            });
        });
    }

    /// <summary>
    /// Handle GpuSwitchResult on the UI thread - show notifications, update tips, show dialogs.
    /// </summary>
    private void HandleGpuSwitchResult(GpuSwitchResult result, GpuMode target)
    {
        RefreshGpuMode();

        switch (result)
        {
            case GpuSwitchResult.Applied:
                string appliedText = target switch
                {
                    GpuMode.Eco => Labels.Get("gpu_notify_eco"),
                    GpuMode.Standard => Labels.Get("gpu_notify_standard"),
                    GpuMode.Optimized => Labels.Get("gpu_notify_optimized"),
                    GpuMode.Ultimate => Labels.Get("gpu_notify_ultimate"),
                    _ => Labels.Get("gpu_notify_changed")
                };
                App.System?.ShowNotification(Labels.Get("gpu_mode"), appliedText, "video-display");
                break;

            case GpuSwitchResult.AlreadySet:
                // No notification needed
                break;

            case GpuSwitchResult.RebootRequired:
                string rebootText = target switch
                {
                    GpuMode.Ultimate => Labels.Get("gpu_reboot_ultimate"),
                    GpuMode.Standard => Labels.Get("gpu_reboot_standard"),
                    GpuMode.Optimized => Labels.Get("gpu_reboot_optimized"),
                    GpuMode.Eco => Labels.Get("gpu_reboot_eco"),
                    _ => Labels.Get("gpu_reboot_generic")
                };
                labelTipGPU.Text = target == GpuMode.Optimized
                    ? Labels.Get("gpu_mux_reboot_auto")
                    : Labels.Get("gpu_mux_reboot");
                App.System?.ShowNotification(Labels.Get("gpu_mode"), rebootText, "system-reboot");
                break;

            case GpuSwitchResult.EcoBlocked:
                labelTipGPU.Text = Labels.Get("gpu_eco_blocked");
                App.System?.ShowNotification(Labels.Get("gpu_mode"),
                    Labels.Get("gpu_eco_blocked_detail"),
                    "dialog-warning");
                break;

            case GpuSwitchResult.DriverBlocking:
                ShowDriverBlockingDialog(target);
                break;

            case GpuSwitchResult.Deferred:
                labelTipGPU.Text = Labels.Get("gpu_eco_pending");
                App.System?.ShowNotification(Labels.Get("gpu_mode"),
                    Labels.Get("gpu_eco_after_reboot"), "system-reboot");
                break;

            case GpuSwitchResult.Failed:
                App.System?.ShowNotification(Labels.Get("gpu_mode"),
                    Labels.Get("gpu_switch_failed"), "dialog-error");
                break;
        }
    }

    /// <summary>
    /// Show the "GPU Driver Active" confirmation dialog with three choices.
    /// All button properties set directly - no CSS classes - for full control
    /// over styling. The ghelper class is designed for grid-stretched main window
    /// buttons and fights with dialog layout (HorizontalAlignment=Stretch, hover
    /// state overrides accent color).
    /// </summary>
    private void ShowDriverBlockingDialog(GpuMode target)
    {
        var dialog = new Window
        {
            Title = Labels.Get("gpu_driver_title"),
            Width = 490,
            Height = 310,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            WindowDecorations = WindowDecorations.Full,
        };

        // Content card - matches main window panel style (#262626)
        var card = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#262626")),
            CornerRadius = new Avalonia.CornerRadius(8),
            Padding = new Avalonia.Thickness(20, 16),
            Margin = new Avalonia.Thickness(0, 0, 0, 18),
        };

        var titleIcon = new Avalonia.Controls.Image
        {
            Source = new Avalonia.Media.Imaging.Bitmap(
                Avalonia.Platform.AssetLoader.Open(new Uri("avares://ghelper/UI/Assets/Icons/warning.png"))),
            Width = 22,
            Height = 22,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(0, 0, 10, 0),
        };
        Avalonia.Media.RenderOptions.SetBitmapInterpolationMode(titleIcon, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);

        var titleText = new TextBlock
        {
            Text = Labels.Get("gpu_driver_title"),
            FontSize = 15,
            FontWeight = Avalonia.Media.FontWeight.Bold,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        var titleRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Margin = new Avalonia.Thickness(0, 0, 0, 12),
        };
        titleRow.Children.Add(titleIcon);
        titleRow.Children.Add(titleText);

        var body = new TextBlock
        {
            Text = Labels.Get("gpu_driver_body"),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            FontSize = 13,
            LineHeight = 20,
            Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")),
        };

        var cardContent = new StackPanel();
        cardContent.Children.Add(titleRow);
        cardContent.Children.Add(body);
        card.Child = cardContent;

        // Buttons - all properties set directly, no CSS class
        // Shared properties applied via helper
        Button MakeDialogButton(string text, string bg, string fg, bool bold = false)
        {
            return new Button
            {
                Content = text,
                Background = new SolidColorBrush(Color.Parse(bg)),
                Foreground = new SolidColorBrush(Color.Parse(fg)),
                FontWeight = bold ? Avalonia.Media.FontWeight.Bold : Avalonia.Media.FontWeight.Normal,
                FontSize = 13,
                MinWidth = 130,
                MinHeight = 38,
                Padding = new Avalonia.Thickness(14, 8),
                CornerRadius = new Avalonia.CornerRadius(5),
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                BorderThickness = new Avalonia.Thickness(0),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };
        }

        var btnSwitchNow = MakeDialogButton(Labels.Get("gpu_driver_switch_now"), "#4CC2FF", "#000000", bold: true);
        var btnAfterReboot = MakeDialogButton(Labels.Get("gpu_driver_after_reboot"), "#373737", "#F0F0F0");
        var btnCancel = MakeDialogButton(Labels.Get("cancel"), "#2A2A2A", "#888888");

        btnSwitchNow.Margin = new Avalonia.Thickness(0, 0, 8, 0);
        btnAfterReboot.Margin = new Avalonia.Thickness(0, 0, 8, 0);

        // Button click handlers
        btnSwitchNow.Click += (_, _) =>
        {
            dialog.Close();
            LockGpuButtons(Labels.Get("gpu_releasing_driver"));

            Task.Run(() =>
            {
                var gpu = App.GpuModeCtrl;
                var result = gpu?.TryReleaseAndSwitch() ?? GpuSwitchResult.Failed;

                Dispatcher.UIThread.Post(() =>
                {
                    UnlockGpuButtons();

                    if (result == GpuSwitchResult.Deferred)
                    {
                        // Driver release failed (rmmod failed, pkexec cancelled, etc.)
                        labelTipGPU.Text = Labels.Get("gpu_eco_pending");
                        RefreshGpuMode();
                        App.System?.ShowNotification(Labels.Get("gpu_mode"),
                            Labels.Get("gpu_driver_eco_scheduled"),
                            "dialog-warning");
                        return;
                    }

                    HandleGpuSwitchResult(result, target);
                });
            });
        };

        btnAfterReboot.Click += (_, _) =>
        {
            dialog.Close();
            App.GpuModeCtrl?.ScheduleModeForReboot(target);
            labelTipGPU.Text = Labels.Get("gpu_eco_pending");
            RefreshGpuMode();
            App.System?.ShowNotification(Labels.Get("gpu_mode"),
                Labels.Get("gpu_eco_after_reboot"), "system-reboot");
        };

        btnCancel.Click += (_, _) =>
        {
            dialog.Close();
            RefreshGpuMode();
        };

        // Button row
        var buttonPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        };
        buttonPanel.Children.Add(btnSwitchNow);
        buttonPanel.Children.Add(btnAfterReboot);
        buttonPanel.Children.Add(btnCancel);

        // Footer help text
        var footer = new TextBlock
        {
            Text = Labels.Get("gpu_driver_footer"),
            Foreground = new SolidColorBrush(Color.Parse("#666666")),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            FontSize = 11,
            LineHeight = 16,
            Margin = new Avalonia.Thickness(2, 14, 0, 0),
        };

        // Layout
        var outerStack = new StackPanel { Margin = new Avalonia.Thickness(24, 20, 24, 16) };
        outerStack.Children.Add(card);
        outerStack.Children.Add(buttonPanel);
        outerStack.Children.Add(footer);

        dialog.Content = outerStack;
        dialog.ShowDialog(this);
    }

    private void ButtonEco_Click(object? sender, RoutedEventArgs e)
        => RequestGpuModeSwitch(GpuMode.Eco, Labels.Get("gpu_switching_eco"));

    private void ButtonStandard_Click(object? sender, RoutedEventArgs e)
        => RequestGpuModeSwitch(GpuMode.Standard, Labels.Get("gpu_switching_standard"));

    private void ButtonOptimized_Click(object? sender, RoutedEventArgs e)
        => RequestGpuModeSwitch(GpuMode.Optimized, Labels.Get("gpu_switching_generic"));

    private void ButtonUltimate_Click(object? sender, RoutedEventArgs e)
        => RequestGpuModeSwitch(GpuMode.Ultimate, Labels.Get("gpu_switching_ultimate"));

    // Screen

    private void RefreshScreen()
    {
        var display = App.Display;
        if (display == null)
            return;

        int hz = display.GetRefreshRate();
        var rates = display.GetAvailableRefreshRates();
        int maxHz = rates.Count > 0 ? rates[0] : 120;
        bool isAuto = Helpers.AppConfig.Is("screen_auto");

        if (hz > 0)
        {
            labelScreen.Text = Labels.Format(isAuto ? "screen_prefix_auto" : "screen_prefix", hz);
            labelScreenHz.Text = $"{hz} Hz";
        }

        // Update max refresh button label
        labelHighRefresh.Text = $"{maxHz}Hz";

        // Highlight active button
        SetButtonActive(buttonScreenAuto, isAuto);
        SetButtonActive(button60Hz, !isAuto && hz == 60);
        SetButtonActive(button120Hz, !isAuto && hz > 60);

        // Check for MiniLED support
        bool hasMiniLed = App.Wmi?.IsFeatureSupported(AsusAttributes.MiniLedMode) ?? false;
        buttonMiniled.IsVisible = hasMiniLed;
    }

    private void ButtonScreenAuto_Click(object? sender, RoutedEventArgs e)
    {
        bool wasAuto = Helpers.AppConfig.Is("screen_auto");
        if (wasAuto)
        {
            // Toggle off, keep current rate, just disable auto
            Helpers.AppConfig.Set("screen_auto", 0);
            Helpers.Logger.WriteLine("Screen auto: disabled");
        }
        else
        {
            // Enable auto and apply immediately
            Helpers.AppConfig.Set("screen_auto", 1);
            (App.Current as App)?.AutoScreen();
        }
        RefreshScreen();
    }

    private void Button60Hz_Click(object? sender, RoutedEventArgs e)
    {
        Helpers.AppConfig.Set("screen_auto", 0);
        App.Display?.SetRefreshRate(60);
        RefreshScreen();
    }

    private void Button120Hz_Click(object? sender, RoutedEventArgs e)
    {
        Helpers.AppConfig.Set("screen_auto", 0);
        var rates = App.Display?.GetAvailableRefreshRates();
        if (rates != null && rates.Count > 0)
            App.Display?.SetRefreshRate(rates[0]); // Use max available
        else
            App.Display?.SetRefreshRate(120);
        RefreshScreen();
    }

    private void ButtonMiniled_Click(object? sender, RoutedEventArgs e)
    {
        var wmi = App.Wmi;
        if (wmi == null)
            return;

        int current = wmi.GetMiniLedMode();
        int next = current switch
        {
            0 => 1,
            1 => 2,
            _ => 0
        };
        wmi.SetMiniLedMode(next);
        Helpers.Logger.WriteLine($"MiniLED mode → {next}");
    }

    // Keyboard / AURA

    private bool _auraInitialized = false;
    private static bool _auraHardwareInitialized = false;
    private bool _suppressAuraEvents = false;
    private bool _suppressEvents = false;

    public void RefreshKeyboard()
    {
        var wmi = App.Wmi;
        if (wmi == null)
            return;

        int brightness = wmi.GetKeyboardBrightness();
        if (brightness >= 0)
        {
            string level = brightness switch
            {
                0 => Labels.Get("backlight_off"),
                1 => Labels.Get("backlight_low"),
                2 => Labels.Get("backlight_medium"),
                3 => Labels.Get("backlight_high"),
                _ => Labels.Format("backlight_level", brightness)
            };
            labelBacklight.Text = Labels.Format("backlight_prefix", level);
        }

        InitAura();
        RefreshAuraCombos();
        UpdateColorButtons();
    }

    /// <summary>
    /// Initialize AURA hardware - HID handshake, load config, apply RGB.
    /// </summary>
    public static bool InitAuraHardware()
    {
        if (_auraHardwareInitialized)
            return Aura.IsAvailable();
        _auraHardwareInitialized = true;

        if (!Aura.IsAvailable())
        {
            Helpers.Logger.WriteLine("No AURA HID device found - RGB controls hidden");
            return false;
        }

        Helpers.Logger.WriteLine("AURA HID device found - initializing RGB controls");

        // Send AURA HID init handshake (wake up the LED controller).
        // This is critical for I2C-HID keyboards (e.g., FA608PP) that need
        // the handshake before they respond to any RGB commands.
        // On Windows, G-Helper's Aura.Init() does this in InputDispatcher.AutoKeyboard().
        Aura.Init();

        // Load saved values into static fields BEFORE applying to hardware
        Aura.Mode = (AuraMode)Helpers.AppConfig.Get("aura_mode");
        Aura.Speed = (AuraSpeed)Helpers.AppConfig.Get("aura_speed");
        Aura.SetColor(Helpers.AppConfig.Get("aura_color", unchecked((int)0xFFFFFFFF)));
        Aura.SetColor2(Helpers.AppConfig.Get("aura_color2", 0));

        // Apply saved power state + mode so the keyboard lights up on startup
        Aura.ApplyPower();
        Aura.ApplyAura();

        return true;
    }

    private void InitAura()
    {
        if (_auraInitialized)
            return;

        // Run hardware init if not already done (normal startup path).
        // Don't set _auraInitialized until hardware is confirmed available
        // if the background InitAuraHardware() hasn't finished yet, we'll retry
        // when it posts RefreshKeyboard() back to the UI thread.
        bool hasAura = InitAuraHardware();
        panelAura.IsVisible = hasAura;
        if (!hasAura)
            return;

        _auraInitialized = true;

        Aura.Mode = (AuraMode)Helpers.AppConfig.Get("aura_mode");
        Aura.Speed = (AuraSpeed)Helpers.AppConfig.Get("aura_speed");
        Aura.SetColor(Helpers.AppConfig.Get("aura_color", unchecked((int)0xFFFFFFFF)));
        Aura.SetColor2(Helpers.AppConfig.Get("aura_color2", 0));

        _suppressAuraEvents = true;

        // Populate mode combo
        var modes = Aura.GetModes();
        comboAuraMode.Items.Clear();
        int selectedModeIdx = 0;
        int idx = 0;
        foreach (var kv in modes)
        {
            comboAuraMode.Items.Add(new ComboBoxItem { Content = kv.Value, Tag = (int)kv.Key });
            if (kv.Key == Aura.Mode)
                selectedModeIdx = idx;
            idx++;
        }
        comboAuraMode.SelectedIndex = selectedModeIdx;

        // Populate speed combo
        var speeds = Aura.GetSpeeds();
        comboAuraSpeed.Items.Clear();
        int selectedSpeedIdx = 0;
        idx = 0;
        foreach (var kv in speeds)
        {
            comboAuraSpeed.Items.Add(new ComboBoxItem { Content = kv.Value, Tag = (int)kv.Key });
            if (kv.Key == Aura.Speed)
                selectedSpeedIdx = idx;
            idx++;
        }
        comboAuraSpeed.SelectedIndex = selectedSpeedIdx;

        _suppressAuraEvents = false;

        // Update color button backgrounds and second color visibility
        UpdateColorButtons();
    }

    private void UpdateColorButtons()
    {
        buttonColor1.Background = new SolidColorBrush(
            Color.FromRgb(Aura.ColorR, Aura.ColorG, Aura.ColorB));
        buttonColor2.Background = new SolidColorBrush(
            Color.FromRgb(Aura.Color2R, Aura.Color2G, Aura.Color2B));
        buttonColor2.IsVisible = Aura.HasSecondColor();

        // Hide color buttons for modes that don't use color
        buttonColor1.IsVisible = Aura.UsesColor();
    }

    /// <summary>Rebuild Aura mode/speed combo items with current language strings.</summary>
    private void RefreshAuraCombos()
    {
        if (!_auraInitialized)
            return;

        _suppressAuraEvents = true;

        // Mode combo, select based on current Aura.Mode (always up-to-date)
        int savedMode = (int)Aura.Mode;
        comboAuraMode.Items.Clear();
        int selectedIdx = 0, idx = 0;
        foreach (var kv in Aura.GetModes())
        {
            comboAuraMode.Items.Add(new ComboBoxItem { Content = kv.Value, Tag = (int)kv.Key });
            if ((int)kv.Key == savedMode)
                selectedIdx = idx;
            idx++;
        }
        comboAuraMode.SelectedIndex = selectedIdx;

        // Speed combo, select based on current Aura.Speed (always up-to-date)
        int savedSpeed = (int)Aura.Speed;
        comboAuraSpeed.Items.Clear();
        selectedIdx = 0;
        idx = 0;
        foreach (var kv in Aura.GetSpeeds())
        {
            comboAuraSpeed.Items.Add(new ComboBoxItem { Content = kv.Value, Tag = (int)kv.Key });
            if ((int)kv.Key == savedSpeed)
                selectedIdx = idx;
            idx++;
        }
        comboAuraSpeed.SelectedIndex = selectedIdx;

        _suppressAuraEvents = false;
    }

    private void ComboAuraMode_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressAuraEvents)
            return;
        if (comboAuraMode.SelectedItem is ComboBoxItem item && item.Tag is int modeVal)
        {
            Helpers.Logger.WriteLine($"AURA mode changed → {(AuraMode)modeVal}");
            Helpers.AppConfig.Set("aura_mode", modeVal);
            Aura.Mode = (AuraMode)modeVal;
            UpdateColorButtons();
            ApplyAuraAsync();
        }
    }

    private void ComboAuraSpeed_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressAuraEvents)
            return;
        if (comboAuraSpeed.SelectedItem is ComboBoxItem item && item.Tag is int speedVal)
        {
            Helpers.AppConfig.Set("aura_speed", speedVal);
            Aura.Speed = (AuraSpeed)speedVal;
            ApplyAuraAsync();
        }
    }

    private void ButtonColor1_Click(object? sender, RoutedEventArgs e)
    {
        ShowColorPicker("aura_color", Aura.ColorR, Aura.ColorG, Aura.ColorB, (r, g, b) =>
        {
            Aura.ColorR = r;
            Aura.ColorG = g;
            Aura.ColorB = b;
            Helpers.AppConfig.Set("aura_color", Aura.GetColorArgb());
            UpdateColorButtons();
            ApplyAuraAsync();
        });
    }

    private void ButtonColor2_Click(object? sender, RoutedEventArgs e)
    {
        ShowColorPicker("aura_color2", Aura.Color2R, Aura.Color2G, Aura.Color2B, (r, g, b) =>
        {
            Aura.Color2R = r;
            Aura.Color2G = g;
            Aura.Color2B = b;
            Helpers.AppConfig.Set("aura_color2", Aura.GetColor2Argb());
            UpdateColorButtons();
            ApplyAuraAsync();
        });
    }

    /// <summary>
    /// Shows a simple color picker window.
    /// Avalonia doesn't have a built-in color dialog, so we use a popup with sliders.
    /// </summary>
    private void ShowColorPicker(string configKey, byte initR, byte initG, byte initB, Action<byte, byte, byte> onColorSet)
    {
        var pickerWindow = new Window
        {
            Title = Labels.Get("pick_color"),
            Width = 320,
            Height = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#1C1C1C")),
            CanResize = false,
            WindowDecorations = WindowDecorations.Full,
        };

        var preview = new Border
        {
            Width = 280,
            Height = 50,
            CornerRadius = new Avalonia.CornerRadius(6),
            Background = new SolidColorBrush(Color.FromRgb(initR, initG, initB)),
            Margin = new Avalonia.Thickness(0, 8, 0, 8),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        };

        var sliderR = new Slider { Minimum = 0, Maximum = 255, Value = initR, Foreground = new SolidColorBrush(Color.FromRgb(255, 80, 80)) };
        var sliderG = new Slider { Minimum = 0, Maximum = 255, Value = initG, Foreground = new SolidColorBrush(Color.FromRgb(80, 255, 80)) };
        var sliderB = new Slider { Minimum = 0, Maximum = 255, Value = initB, Foreground = new SolidColorBrush(Color.FromRgb(80, 80, 255)) };

        var labelR = new TextBlock { Text = $"R: {initR}", Foreground = Brushes.White, Margin = new Avalonia.Thickness(4, 2, 0, 0) };
        var labelG = new TextBlock { Text = $"G: {initG}", Foreground = Brushes.White, Margin = new Avalonia.Thickness(4, 2, 0, 0) };
        var labelB = new TextBlock { Text = $"B: {initB}", Foreground = Brushes.White, Margin = new Avalonia.Thickness(4, 2, 0, 0) };

        // Hex color input
        var hexLabel = new TextBlock { Text = Labels.Get("hex_label"), Foreground = Brushes.White, Margin = new Avalonia.Thickness(4, 6, 0, 0), FontSize = 11 };
        var hexInput = new TextBox
        {
            Text = $"#{initR:X2}{initG:X2}{initB:X2}",
            Width = 100,
            Height = 28,
            FontSize = 12,
            Margin = new Avalonia.Thickness(4, 2, 0, 0),
            Background = new SolidColorBrush(Color.Parse("#262626")),
            Foreground = Brushes.White,
        };
        bool _suppressHexUpdate = false;

        void UpdatePreview()
        {
            byte r = (byte)sliderR.Value;
            byte g = (byte)sliderG.Value;
            byte b = (byte)sliderB.Value;
            preview.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
            labelR.Text = $"R: {r}";
            labelG.Text = $"G: {g}";
            labelB.Text = $"B: {b}";
            if (!_suppressHexUpdate)
                hexInput.Text = $"#{r:X2}{g:X2}{b:X2}";
        }

        sliderR.PropertyChanged += (_, e) => { if (e.Property.Name == "Value") UpdatePreview(); };
        sliderG.PropertyChanged += (_, e) => { if (e.Property.Name == "Value") UpdatePreview(); };
        sliderB.PropertyChanged += (_, e) => { if (e.Property.Name == "Value") UpdatePreview(); };

        // Parse hex input when user types
        hexInput.TextChanged += (_, _) =>
        {
            var text = hexInput.Text?.Trim() ?? "";
            if (!text.StartsWith("#"))
                text = "#" + text;
            if (text.Length == 7)
            {
                try
                {
                    var c = Color.Parse(text);
                    _suppressHexUpdate = true;
                    sliderR.Value = c.R;
                    sliderG.Value = c.G;
                    sliderB.Value = c.B;
                    _suppressHexUpdate = false;
                    preview.Background = new SolidColorBrush(c);
                    labelR.Text = $"R: {c.R}";
                    labelG.Text = $"G: {c.G}";
                    labelB.Text = $"B: {c.B}";
                }
                catch { }
            }
        };

        var btnOk = new Button
        {
            Content = Labels.Get("apply"),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Margin = new Avalonia.Thickness(0, 12, 0, 0),
            MinWidth = 120,
            MinHeight = 34,
            Background = new SolidColorBrush(Color.Parse("#4CC2FF")),
            Foreground = Brushes.Black,
            FontWeight = Avalonia.Media.FontWeight.Bold,
        };
        btnOk.Click += (_, _) =>
        {
            onColorSet((byte)sliderR.Value, (byte)sliderG.Value, (byte)sliderB.Value);
            pickerWindow.Close();
        };

        // Quick preset colors
        var presetPanel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, Spacing = 4, Margin = new Avalonia.Thickness(0, 4) };
        var presets = new (byte R, byte G, byte B)[]
        {
            (255, 255, 255), (255, 0, 0), (0, 255, 0), (0, 0, 255),
            (255, 255, 0), (0, 255, 255), (255, 0, 255), (255, 128, 0),
        };
        foreach (var (pr, pg, pb) in presets)
        {
            var btn = new Button
            {
                Width = 28,
                Height = 28,
                Background = new SolidColorBrush(Color.FromRgb(pr, pg, pb)),
                Margin = new Avalonia.Thickness(1),
                BorderThickness = new Avalonia.Thickness(1),
                BorderBrush = new SolidColorBrush(Color.Parse("#555555")),
            };
            byte cr = pr, cg = pg, cb = pb;
            btn.Click += (_, _) =>
            {
                sliderR.Value = cr;
                sliderG.Value = cg;
                sliderB.Value = cb;
            };
            presetPanel.Children.Add(btn);
        }

        var hexRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Margin = new Avalonia.Thickness(0, 4) };
        hexRow.Children.Add(hexLabel);
        hexRow.Children.Add(hexInput);

        var stack = new StackPanel { Margin = new Avalonia.Thickness(16, 8) };
        stack.Children.Add(preview);
        stack.Children.Add(presetPanel);
        stack.Children.Add(hexRow);
        stack.Children.Add(labelR);
        stack.Children.Add(sliderR);
        stack.Children.Add(labelG);
        stack.Children.Add(sliderG);
        stack.Children.Add(labelB);
        stack.Children.Add(sliderB);
        stack.Children.Add(btnOk);

        pickerWindow.Content = stack;
        pickerWindow.ShowDialog(this);
    }

    private void ApplyAuraAsync()
    {
        Task.Run(() =>
        {
            try
            {
                Aura.ApplyAura();
            }
            catch (Exception ex)
            {
                Helpers.Logger.WriteLine("ApplyAura error", ex);
            }
        });
    }

    private void ButtonKeyboard_Click(object? sender, RoutedEventArgs e)
    {
        var wmi = App.Wmi;
        if (wmi == null)
            return;

        int current = wmi.GetKeyboardBrightness();
        int next = (current + 1) % 4; // Cycle 0→1→2→3→0
        wmi.SetKeyboardBrightness(next);
        RefreshKeyboard();
    }

    // Battery

    private void RefreshBattery()
    {
        var wmi = App.Wmi;
        if (wmi == null)
            return;

        // For models that only accept 60/80/100, snap slider to valid values
        if (Helpers.AppConfig.IsChargeLimit6080())
        {
            sliderBattery.TickFrequency = 20;
            sliderBattery.IsSnapToTickEnabled = true;
        }

        int limit = wmi.GetBatteryChargeLimit();
        if (limit > 0)
        {
            _updatingBatterySlider = true;
            sliderBattery.Value = limit;
            _updatingBatterySlider = false;
            labelBatteryLimit.Text = $"{limit}%";
            // Combined header: "Battery Charge Limit: 80%" (matches Windows)
            labelBattery.Text = Labels.Format("battery_limit_prefix", limit);
        }

        // Show discharge/charge rate in battery section header (right side)
        // and charge percentage in footer (like Windows "Charge: 71.5%")
        var power = App.Power;
        if (power != null)
        {
            int level = power.GetBatteryPercentage();
            bool acPlugged = power.IsOnAcPower();
            int drainMw = power.GetBatteryDrainRate();

            // Discharge/charge rate in battery header right column
            if (drainMw != 0)
            {
                double watts = Math.Abs(drainMw) / 1000.0;
                string rateStr = drainMw > 0
                    ? Labels.Format("discharging_watts", $"{watts:F1}")
                    : Labels.Format("charging_watts", $"{watts:F1}");
                labelCharge.Text = rateStr;
            }
            else if (acPlugged)
            {
                labelCharge.Text = Labels.Get("plugged_in");
            }

            // Charge level in footer
            if (level >= 0)
            {
                labelChargeFooter.Text = Labels.Format("charge_prefix", level);
            }
        }
    }

    private bool _updatingBatterySlider;
    private System.Timers.Timer? _batteryDebounce;

    private void SliderBattery_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingBatterySlider)
            return;

        int limit = (int)e.NewValue;

        // Update labels immediately (responsive UI)
        labelBatteryLimit.Text = $"{limit}%";
        labelBattery.Text = Labels.Format("battery_limit_prefix", limit);

        // Debounce the sysfs write - only write 300ms after the user stops dragging
        _batteryDebounce?.Stop();
        _batteryDebounce ??= new System.Timers.Timer(300) { AutoReset = false };
        _batteryDebounce.Elapsed -= BatteryDebounce_Elapsed;
        _batteryDebounce.Elapsed += BatteryDebounce_Elapsed;
        _batteryDebounce.Start();
    }

    private void BatteryDebounce_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        // Timer fires on thread pool - read slider value and write to sysfs
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            int limit = (int)sliderBattery.Value;
            bool ok = App.Wmi?.SetBatteryChargeLimit(limit) ?? false;

            if (ok)
            {
                // Re-read actual (firmware may clamp on 6080 models)
                int actual = App.Wmi?.GetBatteryChargeLimit() ?? limit;
                labelBatteryLimit.Text = $"{actual}%";
                labelBattery.Text = Labels.Format("battery_limit_prefix", actual);
                Helpers.AppConfig.Set("charge_limit", actual);

                if (actual != limit && actual > 0)
                {
                    _updatingBatterySlider = true;
                    sliderBattery.Value = actual;
                    _updatingBatterySlider = false;
                }
            }
            else
            {
                // Write failed - save user intent to config anyway
                Helpers.AppConfig.Set("charge_limit", limit);
            }
        });
    }

    private void SetBatteryLimit(int percent)
    {
        _batteryDebounce?.Stop(); // Cancel any pending debounce
        _updatingBatterySlider = true;
        sliderBattery.Value = percent;
        _updatingBatterySlider = false;
        App.Wmi?.SetBatteryChargeLimit(percent);
        Helpers.AppConfig.Set("charge_limit", percent);
        RefreshBattery();
    }

    private void ButtonBattery60_Click(object? sender, RoutedEventArgs e)
    {
        SetBatteryLimit(60);
    }

    private void ButtonBattery80_Click(object? sender, RoutedEventArgs e)
    {
        SetBatteryLimit(80);
    }

    private void ButtonBattery100_Click(object? sender, RoutedEventArgs e)
    {
        SetBatteryLimit(100);
    }

    // Footer

    private void RefreshFooter()
    {
        var sys = App.System;
        if (sys == null)
            return;

        string model = sys.GetModelName() ?? Labels.Get("unknown_asus");

        // Show model in window title (like Windows G-Helper)
        Title = Labels.Format("title_prefix", model);

        // Version + model in footer
        labelVersion.Text = Labels.Format("version_prefix", Helpers.AppConfig.AppVersion, model);

        // Check autostart status from config (suppress to avoid re-writing .desktop file)
        _suppressEvents = true;
        checkStartup.IsChecked = Helpers.AppConfig.IsNotFalse("autostart");
        _suppressEvents = false;

        // System info (same as ExtraWindow)
        labelSysModel.Text = Labels.Format("model_prefix", model);
        labelSysBios.Text = Labels.Format("bios_prefix", sys.GetBiosVersion());
        labelSysKernel.Text = Labels.Format("kernel_prefix", sys.GetKernelVersion());

        bool wmiLoaded = sys.IsAsusWmiLoaded();
        labelSysWmi.Text = wmiLoaded ? Labels.Get("asus_wmi_loaded") : Labels.Get("asus_wmi_not_loaded");

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

        labelSysFeatures.Text = features.Count > 0
            ? Labels.Format("features_prefix", string.Join(", ", features))
            : Labels.Get("no_features");
    }

    private void CheckStartup_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;
        bool enabled = checkStartup.IsChecked ?? false;
        Helpers.AppConfig.Set("autostart", enabled ? 1 : 0);
        App.System?.SetAutostart(enabled);
    }

    private ExtraWindow? _extraWindow;

    private void ButtonExtra_Click(object? sender, RoutedEventArgs e)
    {
        if (_extraWindow == null || !_extraWindow.IsVisible)
        {
            _extraWindow = new ExtraWindow();
            if (Helpers.AppConfig.Is("topmost"))
                _extraWindow.Topmost = true;
            _extraWindow.Show();
        }
        else
        {
            _extraWindow.Activate();
        }
    }


    private const string DonateUrl = "https://buymeacoffee.com/utajum";

    private void InitDonate()
    {
        Helpers.CoinSound.EnsureReady();

        _coinMuted = Helpers.AppConfig.Get("donate_muted", 1) == 1;
        UpdateMuteVisual();

        _coinTransform = new TranslateTransform();
        buttonDonate.RenderTransform = _coinTransform;

        // Easter egg: 7 clicks on version label within 3 seconds
        labelVersion.PointerPressed += (_, _) =>
        {
            var now = DateTime.UtcNow;
            if ((now - _versionClickStart).TotalSeconds > 3)
            {
                _versionClickCount = 0;
                _versionClickStart = now;
            }
            _versionClickCount++;
            if (_versionClickCount == 5)
                Helpers.Logger.WriteLine("Easter egg: 🎮 getting close...");
            if (_versionClickCount >= 7)
            {
                _versionClickCount = 0;
                if (_arcadeWindow == null || !_arcadeWindow.IsVisible)
                {
                    _arcadeWindow = new ArcadeWindow();
                    _arcadeWindow.Show();
                }
                else
                {
                    _arcadeWindow.Activate();
                }
            }
        };

        // Hover events
        buttonDonate.PointerEntered += ButtonDonate_PointerEntered;
        buttonDonate.PointerExited += ButtonDonate_PointerExited;

        // Mute toggle - intercept click on the mute badge (Border + TextBlock)
        borderCoinMute.PointerPressed += LabelCoinMute_PointerPressed;
        labelCoinMute.PointerPressed += LabelCoinMute_PointerPressed;

        // Debounce timer: fires 2s after last click → opens URL
        _coinDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _coinDebounceTimer.Tick += CoinDebounce_Tick;

        // Shake timer: runs at ~30ms for oscillation frames
        _coinShakeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _coinShakeTimer.Tick += CoinShake_Tick;
    }

    private void ButtonDonate_Click(object? sender, RoutedEventArgs e)
    {
        _coinClickCount++;
        labelCoinCount.Text = $"\u00D7{_coinClickCount}";

        if (!_coinMuted)
            Helpers.CoinSound.Play();
        StartCoinShake();

        // Restart 2-second debounce
        _coinDebounceTimer?.Stop();
        _coinDebounceTimer?.Start();
    }

    private void ButtonDonate_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (!_coinMuted)
            Helpers.CoinSound.Play();
        StartCoinShake();
    }

    private void ButtonDonate_PointerExited(object? sender, PointerEventArgs e)
    {
        StopCoinShake();
    }

    private void LabelCoinMute_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true; // Don't trigger parent button click
        _coinMuted = !_coinMuted;
        UpdateMuteVisual();
        Helpers.AppConfig.Set("donate_muted", _coinMuted ? 1 : 0);
    }

    private void UpdateMuteVisual()
    {
        labelCoinMute.Text = _coinMuted ? "M" : "\u266A";
        labelCoinMute.Foreground = _coinMuted ? CoinDarkBrush : CoinGoldBrush;
    }

    private void CoinDebounce_Tick(object? sender, EventArgs e)
    {
        _coinDebounceTimer?.Stop();

        try
        {
            string url = $"{DonateUrl}?coins={_coinClickCount}";
            Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
            Helpers.Logger.WriteLine($"Donate: opened {url}");
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"Donate: failed to open URL: {ex.Message}");
        }

        _coinClickCount = 0;
        labelCoinCount.Text = "";
    }

    private void StartCoinShake()
    {
        _coinShakeFrame = 0;
        _coinShakeTimer?.Start();
    }

    private void StopCoinShake()
    {
        _coinShakeTimer?.Stop();
        if (_coinTransform != null)
            _coinTransform.X = 0;
    }

    // Shake pattern: decaying oscillation over ~10 frames (300ms)
    private static readonly double[] ShakeOffsets = { 3, -3, 2.5, -2.5, 2, -2, 1.5, -1.5, 1, 0 };

    private void CoinShake_Tick(object? sender, EventArgs e)
    {
        if (_coinTransform == null || _coinShakeFrame >= ShakeOffsets.Length)
        {
            StopCoinShake();
            return;
        }
        _coinTransform.X = ShakeOffsets[_coinShakeFrame++];
    }

    // Updates + Quit

    private UpdatesWindow? _updatesWindow;

    private void ButtonUpdates_Click(object? sender, RoutedEventArgs e)
    {
        if (_updatesWindow == null || !_updatesWindow.IsVisible)
        {
            _updatesWindow = new UpdatesWindow();
            if (Helpers.AppConfig.Is("topmost"))
                _updatesWindow.Topmost = true;
            _updatesWindow.Show();
        }
        else
        {
            _updatesWindow.Activate();
        }
    }

    private void ButtonQuit_Click(object? sender, RoutedEventArgs e)
    {
        // Clean shutdown
        App.Input?.Dispose();
        App.Wmi?.Dispose();

        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    // UI Helpers

    private static void SetButtonActive(Button button, bool active)
    {
        if (active)
        {
            button.BorderBrush = AccentBrush;
            button.BorderThickness = new Avalonia.Thickness(2);
        }
        else
        {
            button.BorderBrush = TransparentBrush;
            button.BorderThickness = new Avalonia.Thickness(2);
        }
    }
}
