using System.Text.RegularExpressions;

namespace GHelper.Linux.Platform.Linux.Display;

/// <summary>
/// GNOME Wayland backend using gdctl (GNOME 48+, mutter 48+).
/// Uses org.gnome.Mutter.DisplayConfig D-Bus API via the gdctl CLI.
///
/// gdctl show -v output uses a tree format:
///   Monitors:
///   ├--Monitor eDP-1 (Display Name)
///   │  ├--Modes (2)
///   │  │  ├--1920x1200@165.003
///   │  │  │  └--Properties: (2)
///   │  │  │     ├--is-current ⇒  yes
///   │  │  │     └--is-preferred ⇒  yes
///   │  │  └--1920x1200@60.001
///
/// gdctl set -L --primary -M eDP-1 -m 1920x1200@60.001
/// </summary>
public class GdctlBackend : IDisplayBackend
{
    public string Name => "gdctl";
    public bool SupportsGamma => false;

    private static readonly Regex ModeNameRegex = new(@"^\d+x\d+@[\d.]+$");
    private static readonly Regex MonitorRegex = new(@"^Monitor\s+(\S+)");
    private static readonly Regex RefreshRegex = new(@"@([\d.]+)$");

    public int GetRefreshRate()
    {
        try
        {
            var info = FetchInfo();
            if (info == null)
                return -1;

            var output = FindLaptopOutput(info);
            if (output == null)
            {
                Helpers.Logger.WriteLine("Gdctl.GetRefreshRate: no laptop output found");
                return -1;
            }

            var currentMode = FindCurrentMode(info, output);
            if (currentMode == null)
            {
                Helpers.Logger.WriteLine("Gdctl.GetRefreshRate: no current mode found");
                return -1;
            }

            return ExtractHz(currentMode);
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"Gdctl.GetRefreshRate failed: {ex.Message}");
            return -1;
        }
    }

    public List<int> GetAvailableRefreshRates()
    {
        var rates = new List<int>();
        try
        {
            var info = FetchInfo();
            if (info == null)
                return rates;

            var output = FindLaptopOutput(info);
            if (output == null)
                return rates;

            foreach (var mode in FindAllModes(info, output))
            {
                int hz = ExtractHz(mode);
                if (hz > 0 && !rates.Contains(hz))
                    rates.Add(hz);
            }

            rates.Sort((a, b) => b.CompareTo(a));
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"Gdctl.GetAvailableRefreshRates failed: {ex.Message}");
        }
        return rates;
    }

    public void SetRefreshRate(int hz)
    {
        try
        {
            Helpers.Logger.WriteLine($"Gdctl.SetRefreshRate: requesting {hz}Hz");

            var info = FetchInfo();
            if (info == null)
            {
                Helpers.Logger.WriteLine("Gdctl.SetRefreshRate: gdctl show returned null");
                return;
            }

            var output = FindLaptopOutput(info);
            if (output == null)
            {
                Helpers.Logger.WriteLine("Gdctl.SetRefreshRate: no laptop output found");
                return;
            }

            var modes = FindAllModes(info, output);
            string? targetMode = FindModeForHz(modes, hz);
            if (targetMode == null)
            {
                Helpers.Logger.WriteLine($"Gdctl.SetRefreshRate: no mode matching {hz}Hz");
                return;
            }

            var result = SysfsHelper.RunCommand("gdctl",
                $"set --logical-monitor --primary --monitor {output} --mode {targetMode}");
            Helpers.Logger.WriteLine($"Gdctl.SetRefreshRate: {output} → {targetMode} {(result != null ? "OK" : "FAILED")}");
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"Gdctl.SetRefreshRate failed: {ex.Message}");
        }
    }

    public void SetGamma(float r, float g, float b) { }

    public string? GetDisplayName()
    {
        var info = FetchInfo();
        return info != null ? FindLaptopOutput(info) : null;
    }

    // Probing

    /// <summary>Test if gdctl is available and returns monitor info.</summary>
    public static bool Probe()
    {
        var output = SysfsHelper.RunCommand("gdctl", "show");
        return output != null && output.Contains("Monitor");
    }

    // Internal helpers

    /// <summary>Run gdctl show -v and return output. Returns null on failure.</summary>
    private static string? FetchInfo()
    {
        return SysfsHelper.RunCommand("gdctl", "show -v");
    }

    /// <summary>Strip tree-drawing characters from a line.</summary>
    private static string CleanLine(string line)
        => line.Replace("│", "").Replace("├", "").Replace("└", "").Replace("-", "").Trim();

    /// <summary>
    /// Find the laptop panel connector name. Priority: eDP > LVDS > first.
    /// </summary>
    private static string? FindLaptopOutput(string info)
    {
        string? first = null;
        foreach (var line in info.Split('\n'))
        {
            var clean = CleanLine(line);
            var match = MonitorRegex.Match(clean);
            if (!match.Success)
                continue;

            string name = match.Groups[1].Value;

            if (name.StartsWith("eDP", StringComparison.OrdinalIgnoreCase))
                return name;
            if (name.StartsWith("LVDS", StringComparison.OrdinalIgnoreCase))
                first ??= name;

            first ??= name;
        }

        return first;
    }

    /// <summary>
    /// Find all mode names (e.g. "1920x1200@165.003") for a given output connector.
    /// </summary>
    private static List<string> FindAllModes(string info, string connector)
    {
        var modes = new List<string>();
        bool inMonitor = false;

        foreach (var line in info.Split('\n'))
        {
            var clean = CleanLine(line);

            // Track which monitor section we're in
            var monMatch = MonitorRegex.Match(clean);
            if (monMatch.Success)
            {
                inMonitor = monMatch.Groups[1].Value == connector;
                continue;
            }

            if (!inMonitor)
                continue;

            // Mode names are standalone lines like "1920x1200@165.003"
            if (ModeNameRegex.IsMatch(clean))
                modes.Add(clean);
        }

        return modes;
    }

    /// <summary>
    /// Find the current mode name for a connector by looking for "is-current" property.
    /// </summary>
    private static string? FindCurrentMode(string info, string connector)
    {
        bool inMonitor = false;
        string? lastMode = null;

        foreach (var line in info.Split('\n'))
        {
            var clean = CleanLine(line);

            var monMatch = MonitorRegex.Match(clean);
            if (monMatch.Success)
            {
                inMonitor = monMatch.Groups[1].Value == connector;
                lastMode = null;
                continue;
            }

            if (!inMonitor)
                continue;

            if (ModeNameRegex.IsMatch(clean))
                lastMode = clean;

            // "is-current ⇒  yes" appears in the properties of the current mode
            if (clean.Contains("is-current") && lastMode != null)
                return lastMode;
        }

        return null;
    }

    /// <summary>Find the mode name whose refresh rate rounds to the target Hz.</summary>
    private static string? FindModeForHz(List<string> modes, int targetHz)
    {
        string? best = null;
        double bestDiff = double.MaxValue;

        foreach (var mode in modes)
        {
            var match = RefreshRegex.Match(mode);
            if (!match.Success)
                continue;

            if (!double.TryParse(match.Groups[1].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double rate))
                continue;

            double diff = Math.Abs(rate - targetHz);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = mode;
            }
        }

        return best;
    }

    /// <summary>Extract integer Hz from a mode name like "1920x1200@165.003".</summary>
    private static int ExtractHz(string modeName)
    {
        var match = RefreshRegex.Match(modeName);
        if (!match.Success)
            return -1;

        if (double.TryParse(match.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double rate))
            return (int)Math.Round(rate);

        return -1;
    }
}
