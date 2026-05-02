using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using GHelper.Linux.I18n;

namespace GHelper.Linux.UI.Views;

/// <summary>
/// Fan curve editor and power limits window.
/// Linux port of G-Helper's Fans form.
/// </summary>
public partial class FansWindow : Window
{
    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.Parse("#4CC2FF"));
    private static readonly IBrush TransparentBrush = Brushes.Transparent;

    private readonly DispatcherTimer _sensorTimer;
    private bool _updatingAdvanced;

    public FansWindow()
    {
        InitializeComponent();

        Labels.LanguageChanged += ApplyLabels;
        ApplyLabels();

        // Wire up curve change events
        chartCPU.CurveChanged += (_, curve) => OnCurveChanged(0, curve);
        chartGPU.CurveChanged += (_, curve) => OnCurveChanged(1, curve);
        chartMid.CurveChanged += (_, curve) => OnCurveChanged(2, curve);

        _sensorTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _sensorTimer.Tick += (_, _) => RefreshSensors();

        Loaded += (_, _) =>
        {
            LoadFanCurves();
            LoadAdvanced();
            RefreshSensors();
            _sensorTimer.Start();
        };

        // Refresh on performance-mode change (silent/balanced/turbo or auto AC/DC).
        // ModeApplied fires from a background thread once the new mode is fully
        // landed, so we marshal to the UI thread before touching widgets.
        if (App.Mode != null)
            App.Mode.ModeApplied += OnModeApplied;

        Closing += (_, _) =>
        {
            _sensorTimer.Stop();
            if (App.Mode != null)
                App.Mode.ModeApplied -= OnModeApplied;
        };
    }

    private void OnModeApplied(int mode)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try
            {
                LoadFanCurves();
                LoadAdvanced();
            }
            catch (Exception ex)
            {
                Helpers.Logger.WriteLine("FansWindow OnModeApplied refresh failed", ex);
            }
        });
    }

    // Monitor

    private MonitorWindow? _monitorWindow;

    private void ButtonMonitor_Click(object? sender, RoutedEventArgs e)
    {
        if (_monitorWindow == null || !_monitorWindow.IsVisible)
        {
            _monitorWindow = new MonitorWindow();
            if (Helpers.AppConfig.Is("topmost"))
                _monitorWindow.Topmost = true;
            Helpers.WindowPositioner.CenterOfMainWindowOrPrimaryMonitor(_monitorWindow);
            _monitorWindow.Show();
        }
        else
        {
            _monitorWindow.Activate();
        }
    }

    private void ApplyLabels()
    {
        Title = Labels.Get("fans_title");
        headerFanCurves.Text = Labels.Get("fan_curves");
        labelMonitorButton.Text = Labels.Get("monitor_button");
        buttonApplyFans.Content = Labels.Get("apply");
        buttonReset.Content = Labels.Get("reset");
        buttonDisable.Content = Labels.Get("disable");
        checkApplyFans.Content = Labels.Get("auto_apply");
        chartCPU.FanLabel = Labels.Get("cpu_fan");
        chartGPU.FanLabel = Labels.Get("gpu_fan");
        chartMid.FanLabel = Labels.Get("mid_fan");
        headerAdvanced.Text = Labels.Get("advanced_header");
        labelModeCmd.Text = Labels.Get("mode_command_label");
        labelModeCmdHint.Text = Labels.Get("mode_command_hint");
        labelReapply.Text = Labels.Get("reapply_power_label");
        labelReapplyUnit.Text = Labels.Get("reapply_power_unit");
        labelReapplyHint.Text = Labels.Get("reapply_power_hint");
    }

    // Fan Curves

    private void LoadFanCurves()
    {
        var wmi = App.Wmi;
        if (wmi == null)
            return;

        // Try reading current curves from hardware
        byte[]? cpuCurve = wmi.GetFanCurve(0);
        byte[]? gpuCurve = wmi.GetFanCurve(1);

        // Fall back to config or defaults if hardware returned no usable data
        if (!IsValidCurve(cpuCurve))
        {
            cpuCurve = Helpers.AppConfig.GetFanConfig(0);
            if (!IsValidCurve(cpuCurve))
                cpuCurve = Helpers.AppConfig.GetDefaultCurve(0);
        }

        if (!IsValidCurve(gpuCurve))
        {
            gpuCurve = Helpers.AppConfig.GetFanConfig(1);
            if (!IsValidCurve(gpuCurve))
                gpuCurve = Helpers.AppConfig.GetDefaultCurve(1);
        }

        chartCPU.CurveData = cpuCurve;
        chartGPU.CurveData = gpuCurve;

        // Mid fan detection - show chart if curve is valid or RPM is readable
        // (matches Windows G-Helper's InitFans logic)
        byte[]? midCurve = wmi.GetFanCurve(2);
        bool hasMidFan = IsValidCurve(midCurve) || wmi.GetFanRpm(2) > 0;

        if (hasMidFan)
        {
            if (!IsValidCurve(midCurve))
            {
                midCurve = Helpers.AppConfig.GetFanConfig(2);
                if (!IsValidCurve(midCurve))
                    midCurve = Helpers.AppConfig.GetDefaultCurve(2);
            }

            chartMid.CurveData = midCurve;
            chartMid.IsVisible = true;
            // Change third row from Auto to Star so all 3 charts share space equally
            chartGrid.RowDefinitions[2].Height = new Avalonia.Controls.GridLength(1, Avalonia.Controls.GridUnitType.Star);
            this.Height = 820;

            Helpers.AppConfig.Set("mid_fan", 1);
        }
        else
        {
            Helpers.AppConfig.Set("mid_fan", 0);
        }

        // Update mode label
        int mode = App.Wmi?.GetThrottleThermalPolicy() ?? -1;
        string modeName = mode switch
        {
            0 => Labels.Get("mode_balanced"),
            1 => Labels.Get("mode_turbo"),
            2 => Labels.Get("mode_silent"),
            _ => Labels.Get("mode_unknown")
        };
        labelMode.Text = Labels.Format("mode_prefix", modeName);

        checkApplyFans.IsChecked = Helpers.AppConfig.IsMode("auto_apply_fans");

        UpdateDisabledState();
    }

    private void OnCurveChanged(int fanIndex, byte[] curve)
    {
        // Save to config
        Helpers.AppConfig.SetFanConfig(fanIndex, curve);

        // Auto-apply if enabled
        if (checkApplyFans.IsChecked == true)
        {
            App.Wmi?.SetFanCurve(fanIndex, curve);
        }
    }

    private void ButtonApplyFans_Click(object? sender, RoutedEventArgs e)
    {
        var wmi = App.Wmi;
        if (wmi == null)
            return;

        if (chartCPU.CurveData is { Length: 16 })
        {
            wmi.SetFanCurve(0, chartCPU.CurveData);
            Helpers.AppConfig.SetFanConfig(0, chartCPU.CurveData);
        }

        if (chartGPU.CurveData is { Length: 16 })
        {
            wmi.SetFanCurve(1, chartGPU.CurveData);
            Helpers.AppConfig.SetFanConfig(1, chartGPU.CurveData);
        }

        if (chartMid.IsVisible && chartMid.CurveData is { Length: 16 })
        {
            wmi.SetFanCurve(2, chartMid.CurveData);
            Helpers.AppConfig.SetFanConfig(2, chartMid.CurveData);
        }

        UpdateDisabledState();
        Helpers.Logger.WriteLine("Fan curves applied");
    }

    private void ButtonReset_Click(object? sender, RoutedEventArgs e)
    {
        var wmi = App.Wmi;

        // Phase 1: Reset ALL fans to factory defaults (pwm_enable=3).
        // Must do all resets before any re-apply because the kernel quirk
        // causes pwm_enable=3 on one fan to reset ALL fans.
        byte[]? cpuCurve = wmi?.ResetFanCurveToDefaults(0);
        byte[]? gpuCurve = wmi?.ResetFanCurveToDefaults(1);
        byte[]? midCurve = chartMid.IsVisible ? wmi?.ResetFanCurveToDefaults(2) : null;

        // Fall back to hardcoded defaults if kernel reset unsupported
        if (!IsValidCurve(cpuCurve))
            cpuCurve = Helpers.AppConfig.GetDefaultCurve(0);
        if (!IsValidCurve(gpuCurve))
            gpuCurve = Helpers.AppConfig.GetDefaultCurve(1);
        if (chartMid.IsVisible && !IsValidCurve(midCurve))
            midCurve = Helpers.AppConfig.GetDefaultCurve(2);

        // Phase 2: Update UI and save config
        chartCPU.CurveData = cpuCurve;
        chartGPU.CurveData = gpuCurve;
        Helpers.AppConfig.SetFanConfig(0, cpuCurve!);
        Helpers.AppConfig.SetFanConfig(1, gpuCurve!);

        if (chartMid.IsVisible)
        {
            chartMid.CurveData = midCurve;
            Helpers.AppConfig.SetFanConfig(2, midCurve!);
        }

        // Phase 3: Re-apply ALL curves as active custom curves (pwm_enable=1).
        // Done after all resets so no subsequent pwm_enable=3 undoes them.
        if (cpuCurve is { Length: 16 })
            wmi?.SetFanCurve(0, cpuCurve);
        if (gpuCurve is { Length: 16 })
            wmi?.SetFanCurve(1, gpuCurve);
        if (chartMid.IsVisible && midCurve is { Length: 16 })
            wmi?.SetFanCurve(2, midCurve);

        UpdateDisabledState();
        Helpers.Logger.WriteLine("Fan curves reset to firmware defaults and re-applied");
    }

    private void ButtonDisable_Click(object? sender, RoutedEventArgs e)
    {
        var wmi = App.Wmi;
        if (wmi == null)
            return;

        wmi.DisableFanCurve(0);
        wmi.DisableFanCurve(1);
        if (chartMid.IsVisible)
            wmi.DisableFanCurve(2);
        UpdateDisabledState();

        Helpers.Logger.WriteLine("Custom fan curves disabled, using firmware defaults");
    }

    private void UpdateDisabledState()
    {
        var wmi = App.Wmi;
        bool cpuEnabled = wmi?.IsFanCurveEnabled(0) ?? false;
        bool gpuEnabled = wmi?.IsFanCurveEnabled(1) ?? false;
        bool midEnabled = !chartMid.IsVisible || (wmi?.IsFanCurveEnabled(2) ?? false);
        bool anyDisabled = !cpuEnabled || !gpuEnabled || !midEnabled;

        chartCPU.Disabled = !cpuEnabled;
        chartGPU.Disabled = !gpuEnabled;
        if (chartMid.IsVisible)
            chartMid.Disabled = !midEnabled;

        // Toggle button visual - accent border when disabled (active state)
        buttonDisable.BorderBrush = anyDisabled ? AccentBrush : TransparentBrush;
        buttonDisable.BorderThickness = new Avalonia.Thickness(2);
    }

    private void CheckApplyFans_Changed(object? sender, RoutedEventArgs e)
    {
        bool enabled = checkApplyFans.IsChecked ?? false;
        Helpers.AppConfig.SetMode("auto_apply_fans", enabled ? 1 : 0);
    }

    // Sensor refresh

    private void RefreshSensors()
    {
        try
        {
            var wmi = App.Wmi;
            if (wmi == null)
                return;

            int cpuTemp = wmi.DeviceGet(0x00120094);
            int gpuTemp = wmi.DeviceGet(0x00120097);
            int cpuFan = wmi.GetFanRpm(0);
            int gpuFan = wmi.GetFanRpm(1);
            int midFan = wmi.GetFanRpm(2);

            // GPU load: only show when dGPU is active (not in Eco mode)
            string gpuLoadStr = "";
            bool isEcoMode = wmi.GetGpuEco();
            if (!isEcoMode && App.GpuControl?.IsAvailable() == true)
            {
                try
                {
                    int? gpuLoad = App.GpuControl.GetGpuUse();
                    if (gpuLoad.HasValue && gpuLoad.Value >= 0)
                        gpuLoadStr = $" Load: {gpuLoad.Value}%";
                }
                catch (Exception)
                {
                    // Silently ignore GPU query errors during transitions
                    Helpers.Logger.WriteLine("FansWindow: GPU load query failed");
                }
            }

            string info = $"CPU: {(cpuTemp > 0 ? $"{cpuTemp}°C" : "--")} / {(cpuFan > 0 ? $"{cpuFan} RPM" : "--")}   " +
                          $"GPU: {(gpuTemp > 0 ? $"{gpuTemp}°C" : "--")}{gpuLoadStr} / {(gpuFan > 0 ? $"{gpuFan} RPM" : "--")}";

            if (midFan > 0)
                info += $"   Mid: {midFan} RPM";

            labelSensors.Text = info;
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine("FansWindow sensor refresh error", ex);
        }
    }

    /// <summary>
    /// Validate a fan curve read from hardware or config.
    /// Rejects null, wrong length, and completely-zero curves.
    /// Matches Windows G-Helper's IsEmptyCurve: a curve is invalid only if ALL 16 bytes are 0.
    /// Note: CPU/GPU fan curves from the Linux kernel often have all-zero temperatures but
    /// valid PWM duty cycles - these are valid curves (GetFanCurve synthesizes a temp ramp).
    /// </summary>
    private static bool IsValidCurve(byte[]? curve)
    {
        if (curve == null || curve.Length != 16)
            return false;

        // Reject only if every byte is zero (no useful data at all)
        for (int i = 0; i < 16; i++)
        {
            if (curve[i] > 0)
                return true;
        }
        return false;
    }

    // Advanced: per-mode shell hook + reapply timer

    private void LoadAdvanced()
    {
        _updatingAdvanced = true;
        try
        {
            int mode = Mode.Modes.GetCurrent();
            textModeCommand.Text = Helpers.AppConfig.GetString($"mode_command_{mode}") ?? "";
            int reapply = Helpers.AppConfig.Get("reapply_time", 0);
            if (reapply < 0)
                reapply = 0;
            numReapplyTime.Value = reapply;
        }
        finally
        {
            _updatingAdvanced = false;
        }
    }

    private void TextModeCommand_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_updatingAdvanced)
            return;
        int mode = Mode.Modes.GetCurrent();
        string val = textModeCommand.Text ?? "";
        Helpers.AppConfig.Set($"mode_command_{mode}", val);
    }

    private void NumReapplyTime_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_updatingAdvanced)
            return;
        int v = (int)(e.NewValue ?? 0);
        if (v < 0)
            v = 0;
        Helpers.AppConfig.Set("reapply_time", v);
        App.Mode?.RefreshReapplyTimer();
    }
}
