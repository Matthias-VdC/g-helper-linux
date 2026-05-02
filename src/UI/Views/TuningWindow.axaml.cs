using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using GHelper.Linux.I18n;
using GHelper.Linux.Platform.Linux;

namespace GHelper.Linux.UI.Views;

/// <summary>
/// Tuning window grouping CPU power limits + Ryzen Curve Optimizer (undervolt) on
/// one tab, and GPU power limit + clock lock on another. Tabs auto-hide when the
/// underlying hardware isn't available.
/// </summary>
public partial class TuningWindow : Window
{
    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.Parse("#4CC2FF"));
    private static readonly IBrush TransparentBrush = Brushes.Transparent;

    private bool _suppressEvents = true;
    private bool _updatingUV;
    private bool _updatingPLSliders;
    private System.Timers.Timer? _plDebounce;

    private LinuxNvidiaGpuControl? _nvidiaGpu;

    public TuningWindow()
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
            RefreshGpuTuning();
            LoadPowerLimits();
            RefreshBoostButton();
            LoadUV();
            ApplyLabels();
            SelectFirstVisibleTab();
            _suppressEvents = false;
        };

        // Power limits and CPU UV reflect the current performance mode's saved values,
        // so refresh when the mode changes (silent/balanced/turbo, manual or auto AC/DC).
        if (App.Mode != null)
            App.Mode.ModeApplied += OnModeApplied;

        Closing += (_, _) =>
        {
            if (App.Mode != null)
                App.Mode.ModeApplied -= OnModeApplied;
        };
    }

    private void OnModeApplied(int mode)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                LoadPowerLimits();
                RefreshBoostButton();
                LoadUV();
            }
            catch (Exception ex)
            {
                Helpers.Logger.WriteLine("TuningWindow OnModeApplied refresh failed", ex);
            }
        });
    }

    /// <summary>
    /// Default <see cref="TabControl"/> selection points at the first <see cref="TabItem"/>
    /// regardless of its IsVisible state. When the first tab(s) are hidden because the
    /// underlying hardware isn't present, the window opens with no content visible until
    /// the user manually clicks a different tab. Walk the tabs and select the first one
    /// that is actually visible.
    /// </summary>
    private void SelectFirstVisibleTab()
    {
        if (tabCpu.IsVisible)
            tabCpu.IsSelected = true;
        else if (tabGpu.IsVisible)
            tabGpu.IsSelected = true;
    }

    private void ApplyLabels()
    {
        Title = Labels.Get("tuning_header");

        labelCpuTab.Text = Labels.Get("cpu_tab");
        labelGpuTab.Text = Labels.Get("gpu_tab");

        // Power Limits (PPT)
        headerPowerLimits.Text = Labels.Get("power_limits");
        labelPL1Label.Text = Labels.Get("cpu_pl1");
        labelPL2Label.Text = Labels.Get("cpu_pl2");
        labelFpptLabel.Text = Labels.Get("cpu_fppt");
        labelCpuBoostLabel.Text = Labels.Get("cpu_boost");
        buttonBoostOff.Content = Labels.Get("off");
        buttonBoostOn.Content = Labels.Get("on");
        checkApplyPower.Content = Labels.Get("auto_apply_power_limits");

        // CPU UV
        headerUndervolt.Text = Labels.Get("undervolt_header");
        labelUndervoltDesc.Text = Labels.Get("undervolt_desc");
        labelUndervoltCpu.Text = Labels.Get("undervolt_cpu");
        buttonApplyUV.Content = Labels.Get("apply");
        buttonResetUV.Content = Labels.Get("reset");
        checkApplyUV.Content = Labels.Get("undervolt_auto_apply");

        // GPU Tuning
        headerGpuTuning.Text = Labels.Get("gpu_tuning_header");
        labelPowerLimitLabel.Text = Labels.Get("power_limit");
        labelClockLockLabel.Text = Labels.Get("clock_lock");
        buttonGpuApply.Content = Labels.Get("apply_gpu_settings");
    }

    // -----------------------------------------------------------------------
    // CPU Power Limits (PPT) + Boost
    // Mirrors the original FansWindow handlers verbatim, plus a panel-visibility
    // gate so the tab hides when the platform exposes no PPT controls.
    // -----------------------------------------------------------------------

    private void LoadPowerLimits()
    {
        var wmi = App.Wmi;
        if (wmi == null || !wmi.IsFeatureSupported(AsusAttributes.PptPl1Spl))
        {
            panelPowerLimits.IsVisible = false;
            UpdateCpuTabVisibility();
            return;
        }

        panelPowerLimits.IsVisible = true;
        UpdateCpuTabVisibility();

        _updatingPLSliders = true;

        // Read from hardware, fall back to saved config
        int pl1 = wmi.GetPptLimit(AsusAttributes.PptPl1Spl);
        if (pl1 <= 0)
            pl1 = Helpers.AppConfig.GetMode("limit_slow");

        int pl2 = wmi.GetPptLimit(AsusAttributes.PptPl2Sppt);
        if (pl2 <= 0)
            pl2 = Helpers.AppConfig.GetMode("limit_fast");

        if (pl1 > 0)
        {
            sliderPL1.Value = pl1;
            labelPL1.Text = $"{pl1}W";
        }

        if (pl2 > 0)
        {
            sliderPL2.Value = pl2;
            labelPL2.Text = $"{pl2}W";
        }

        // fPPT (fast boost) - only show if supported
        bool hasFppt = wmi.IsFeatureSupported(AsusAttributes.PptFppt);
        gridFppt.IsVisible = hasFppt;
        if (hasFppt)
        {
            int fppt = wmi.GetPptLimit(AsusAttributes.PptFppt);
            if (fppt <= 0)
                fppt = Helpers.AppConfig.GetMode("limit_fppt");
            if (fppt > 0)
            {
                sliderFppt.Value = fppt;
                labelFppt.Text = $"{fppt}W";
            }
        }

        _updatingPLSliders = false;
        checkApplyPower.IsChecked = Helpers.AppConfig.IsMode("auto_apply_power");
    }

    private void SliderPL1_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingPLSliders)
            return;
        labelPL1.Text = $"{(int)e.NewValue}W";
        // Enforce PL1 ≤ PL2 ≤ fPPT (matches Windows G-Helper coupling)
        if (sliderPL1.Value > sliderPL2.Value)
            sliderPL2.Value = sliderPL1.Value;
        if (sliderPL1.Value > sliderFppt.Value)
            sliderFppt.Value = sliderPL1.Value;
        SchedulePLWrite();
    }

    private void SliderPL2_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingPLSliders)
            return;
        labelPL2.Text = $"{(int)e.NewValue}W";
        if (sliderPL2.Value < sliderPL1.Value)
            sliderPL1.Value = sliderPL2.Value;
        if (sliderPL2.Value > sliderFppt.Value)
            sliderFppt.Value = sliderPL2.Value;
        SchedulePLWrite();
    }

    private void SliderFppt_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingPLSliders)
            return;
        labelFppt.Text = $"{(int)e.NewValue}W";
        if (sliderFppt.Value < sliderPL2.Value)
            sliderPL2.Value = sliderFppt.Value;
        if (sliderFppt.Value < sliderPL1.Value)
            sliderPL1.Value = sliderFppt.Value;
        SchedulePLWrite();
    }

    /// <summary>Debounce PL slider writes - only write 300ms after the user stops dragging.</summary>
    private void SchedulePLWrite()
    {
        _plDebounce?.Stop();
        _plDebounce ??= new System.Timers.Timer(300) { AutoReset = false };
        _plDebounce.Elapsed -= PLDebounce_Elapsed;
        _plDebounce.Elapsed += PLDebounce_Elapsed;
        _plDebounce.Start();
    }

    private void PLDebounce_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var wmi = App.Wmi;
            if (wmi == null)
                return;

            int pl1 = (int)sliderPL1.Value;
            int pl2 = (int)sliderPL2.Value;
            int fppt = (int)sliderFppt.Value;

            wmi.SetPptLimit(AsusAttributes.PptPl1Spl, pl1);
            Helpers.AppConfig.SetMode("limit_slow", pl1);

            wmi.SetPptLimit(AsusAttributes.PptPl2Sppt, pl2);
            Helpers.AppConfig.SetMode("limit_fast", pl2);

            if (gridFppt.IsVisible)
            {
                wmi.SetPptLimit(AsusAttributes.PptFppt, fppt);
                Helpers.AppConfig.SetMode("limit_fppt", fppt);
            }

            // Mirror to secondary PPT - prevents stale APU/Platform SPPT
            // from bottlenecking. Value = max(PL1, PL2).
            int ceiling = Math.Max(pl1, pl2);
            if (ceiling > 0)
            {
                if (wmi.IsFeatureSupported(AsusAttributes.PptApuSppt))
                    wmi.SetPptLimit(AsusAttributes.PptApuSppt, ceiling);
                if (wmi.IsFeatureSupported(AsusAttributes.PptPlatformSppt))
                    wmi.SetPptLimit(AsusAttributes.PptPlatformSppt, ceiling);
            }
        });
    }

    private void CheckApplyPower_Changed(object? sender, RoutedEventArgs e)
    {
        bool enabled = checkApplyPower.IsChecked ?? false;
        Helpers.AppConfig.SetMode("auto_apply_power", enabled ? 1 : 0);
    }

    // CPU Boost

    private void RefreshBoostButton()
    {
        var power = App.Power;
        if (power == null)
            return;

        bool boostEnabled = power.GetCpuBoost();
        SetBoostButtonState(boostEnabled);
    }

    private void SetBoostButtonState(bool boostOn)
    {
        buttonBoostOn.BorderBrush = boostOn ? AccentBrush : TransparentBrush;
        buttonBoostOn.BorderThickness = new Avalonia.Thickness(2);
        buttonBoostOff.BorderBrush = !boostOn ? AccentBrush : TransparentBrush;
        buttonBoostOff.BorderThickness = new Avalonia.Thickness(2);
    }

    private void ButtonBoostOn_Click(object? sender, RoutedEventArgs e)
    {
        App.Power?.SetCpuBoost(true);
        Helpers.AppConfig.SetMode("auto_boost", 1);
        SetBoostButtonState(true);
    }

    private void ButtonBoostOff_Click(object? sender, RoutedEventArgs e)
    {
        App.Power?.SetCpuBoost(false);
        Helpers.AppConfig.SetMode("auto_boost", 0);
        SetBoostButtonState(false);
    }

    // -----------------------------------------------------------------------
    // CPU UV (Ryzen Curve Optimizer)
    // Mirrors the original FansWindow handlers verbatim.
    // -----------------------------------------------------------------------

    private void LoadUV()
    {
        var smu = App.Smu;
        bool available = smu != null && smu.IsAvailable;
        panelUV.IsVisible = available;
        UpdateCpuTabVisibility();

        if (!available)
            return;

        _updatingUV = true;
        try
        {
            // Config stores negative cpu_uv (matches Windows); slider is 0..40 positive intensity.
            int cpuUV = Helpers.AppConfig.GetMode("cpu_uv", 0);
            cpuUV = Math.Clamp(cpuUV, RyzenSmu.MinCPUUV, RyzenSmu.MaxCPUUV);
            sliderCpuUV.Value = -cpuUV;
            labelCpuUV.Text = cpuUV.ToString();
            checkApplyUV.IsChecked = Helpers.AppConfig.IsMode("auto_uv");
        }
        finally
        {
            _updatingUV = false;
        }
    }

    /// <summary>The CPU tab hosts both Power Limits and Undervolt panels; show the tab
    /// when at least one is available.</summary>
    private void UpdateCpuTabVisibility()
    {
        tabCpu.IsVisible = panelPowerLimits.IsVisible || panelUV.IsVisible;
    }

    private void SliderCpuUV_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingUV)
            return;
        // Slider value is positive intensity (0..40); config stores negated (−40..0).
        int intensity = Math.Clamp((int)e.NewValue, 0, -RyzenSmu.MinCPUUV);
        int cpuUV = -intensity;
        labelCpuUV.Text = cpuUV.ToString();
        Helpers.AppConfig.SetMode("cpu_uv", cpuUV);
    }

    private void ButtonApplyUV_Click(object? sender, RoutedEventArgs e) => App.Mode?.SetRyzen();

    private void ButtonResetUV_Click(object? sender, RoutedEventArgs e)
    {
        _updatingUV = true;
        try
        {
            sliderCpuUV.Value = 0;
            labelCpuUV.Text = "0";
        }
        finally
        {
            _updatingUV = false;
        }
        Helpers.AppConfig.SetMode("cpu_uv", 0);
        App.Mode?.ResetRyzen();
    }

    private void CheckApplyUV_Changed(object? sender, RoutedEventArgs e)
    {
        if (_updatingUV)
            return;
        Helpers.AppConfig.SetMode("auto_uv", checkApplyUV.IsChecked == true ? 1 : 0);
        App.Mode?.AutoRyzen();
    }

    // -----------------------------------------------------------------------
    // GPU OC (Power Limit, Clock Lock, Core/Memory offsets)
    // Mirrors the original ExtraWindow handlers verbatim.
    // -----------------------------------------------------------------------

    private void RefreshGpuTuning()
    {
        _nvidiaGpu = App.GpuControl as LinuxNvidiaGpuControl;
        if (_nvidiaGpu == null || !_nvidiaGpu.IsAvailable())
        {
            tabGpu.IsVisible = false;
            return;
        }

        tabGpu.IsVisible = true;
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
                    Labels.Format("gpu_power_format", powerW) +
                    (clockLock ? Labels.Format("gpu_clock_format", clockMhz) : ""),
                    "dialog-information");
            });
        });
    }
}
