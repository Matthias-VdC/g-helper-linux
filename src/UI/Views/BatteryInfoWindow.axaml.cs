using Avalonia.Controls;
using Avalonia.Threading;
using GHelper.Linux.I18n;
using GHelper.Linux.Platform.Linux;

namespace GHelper.Linux.UI.Views;

/// <summary>
/// Battery information window — shows detailed battery health, energy, and hardware data
/// from /sys/class/power_supply/BAT*.
/// </summary>
public partial class BatteryInfoWindow : Window
{
    private readonly string? _batteryDir;
    private readonly DispatcherTimer _refreshTimer;

    public BatteryInfoWindow()
    {
        InitializeComponent();
        _batteryDir = SysfsHelper.FindBattery();

        Labels.LanguageChanged += ApplyLabels;
        ApplyLabels();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => RefreshLive();

        Loaded += (_, _) =>
        {
            RefreshStatic();
            RefreshLive();
            _refreshTimer.Start();
        };

        Closing += (_, _) => _refreshTimer.Stop();
    }

    private void ApplyLabels()
    {
        Title = Labels.Get("battery_info_title");
        headerHealth.Text = Labels.Get("health_header");
        labelHealthLabel.Text = Labels.Get("health");
        labelCycleCountLabel.Text = Labels.Get("cycle_count");
        labelStatusLabel.Text = Labels.Get("status");
        labelCapLevelLabel.Text = Labels.Get("capacity_level");
        headerEnergy.Text = Labels.Get("energy_header");
        labelRemainingLabel.Text = Labels.Get("remaining");
        labelFullChargeLabel.Text = Labels.Get("full_charge");
        labelDesignCapLabel.Text = Labels.Get("design_capacity");
        labelPowerDrawLabel.Text = Labels.Get("power_draw");
        labelVoltageLabel.Text = Labels.Get("voltage");
        headerHardware.Text = Labels.Get("hardware_header");
        labelManufacturerLabel.Text = Labels.Get("manufacturer");
        labelModelLabel.Text = Labels.Get("model");
        labelTechnologyLabel.Text = Labels.Get("technology");
        labelDesignVoltageLabel.Text = Labels.Get("design_voltage");
    }

    /// <summary>Read values that don't change while the window is open.</summary>
    private void RefreshStatic()
    {
        if (_batteryDir == null)
        {
            labelHealth.Text = Labels.Get("no_battery");
            return;
        }

        // Manufacturer / model / technology
        labelManufacturer.Text = ReadAttr("manufacturer") ?? Labels.Get("unknown");
        labelModel.Text = ReadAttr("model_name") ?? Labels.Get("unknown");
        labelTechnology.Text = ReadAttr("technology") ?? Labels.Get("unknown");

        // Design voltage
        int vDesign = ReadInt("voltage_min_design");
        labelDesignVoltage.Text = vDesign > 0
            ? $"{vDesign / 1_000_000.0:F3}V"
            : "--";

        // Design capacity
        int energyDesign = ReadInt("energy_full_design");
        labelEnergyDesign.Text = energyDesign > 0
            ? $"{energyDesign / 1_000_000.0:F2} Wh"
            : "--";

        // Cycle count
        int cycles = ReadInt("cycle_count");
        labelCycles.Text = cycles >= 0 ? cycles.ToString() : Labels.Get("n_a");
    }

    /// <summary>Read values that change in real-time.</summary>
    private void RefreshLive()
    {
        if (_batteryDir == null) return;

        // Health
        int energyFull = ReadInt("energy_full");
        int energyDesign = ReadInt("energy_full_design");
        if (energyFull > 0 && energyDesign > 0)
        {
            double health = energyFull * 100.0 / energyDesign;
            labelHealth.Text = $"{health:F1}%  ({energyFull / 1_000_000.0:F2} / {energyDesign / 1_000_000.0:F2} Wh)";
        }

        // Full charge capacity
        labelEnergyFull.Text = energyFull > 0
            ? $"{energyFull / 1_000_000.0:F2} Wh"
            : "--";

        // Energy now
        int energyNow = ReadInt("energy_now");
        int capacity = ReadInt("capacity");
        if (energyNow > 0)
            labelEnergyNow.Text = $"{energyNow / 1_000_000.0:F2} Wh ({(capacity >= 0 ? $"{capacity}%" : "")})";
        else if (capacity >= 0)
            labelEnergyNow.Text = $"{capacity}%";

        // Status
        labelStatus.Text = ReadAttr("status") ?? "--";

        // Capacity level
        labelCapLevel.Text = ReadAttr("capacity_level") ?? "--";

        // Power draw
        int powerUw = ReadInt("power_now");
        string? status = ReadAttr("status");
        if (powerUw > 0)
        {
            double powerW = powerUw / 1_000_000.0;
            string dir = status == "Discharging" ? Labels.Get("discharging") : Labels.Get("charging");
            labelPowerDraw.Text = $"{powerW:F1}W ({dir})";
        }
        else
        {
            labelPowerDraw.Text = "0W";
        }

        // Voltage now
        int vNow = ReadInt("voltage_now");
        labelVoltage.Text = vNow > 0
            ? $"{vNow / 1_000_000.0:F3}V"
            : "--";
    }

    // ── Helpers ──

    private string? ReadAttr(string name)
    {
        if (_batteryDir == null) return null;
        return SysfsHelper.ReadAttribute(Path.Combine(_batteryDir, name))?.Trim();
    }

    private int ReadInt(string name)
    {
        if (_batteryDir == null) return -1;
        return SysfsHelper.ReadInt(Path.Combine(_batteryDir, name), -1);
    }
}
