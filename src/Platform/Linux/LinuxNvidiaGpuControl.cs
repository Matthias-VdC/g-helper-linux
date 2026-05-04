using System.Globalization;
using System.Text.RegularExpressions;

namespace GHelper.Linux.Platform.Linux;

/// <summary>
/// Linux NVIDIA GPU control using:
///   - nvidia-smi CLI for monitoring, power limit, and clock lock
///   - /sys/class/hwmon/ nvidia hwmon for temperature
///   - NVML (libnvidia-ml.so.1) via the --apply-gpu-oc worker for clock offsets
///
/// Requires nvidia proprietary driver to be installed.
/// nvidia-smi is the most reliable cross-version approach on Linux.
///
/// Clock offsets need NVML rather than nvidia-settings because nvidia-settings
/// is X11-only - it does not work under Wayland.
/// </summary>
public class LinuxNvidiaGpuControl : IGpuControl
{
    private string? _hwmonDir;
    private string? _gpuName;
    private bool _available;

    public string Vendor => "NVIDIA";

    public LinuxNvidiaGpuControl()
    {
        _hwmonDir = SysfsHelper.FindHwmonByName("nvidia");
        _available = CheckAvailability();

        if (_available)
        {
            _gpuName = QueryGpuName();
            Helpers.Logger.WriteLine($"NVIDIA GPU found: {_gpuName ?? "unknown"}");
        }
        else
        {
            Helpers.Logger.WriteLine("NVIDIA GPU not available (nvidia-smi not found or no GPU)");
        }
    }

    public bool IsAvailable() => _available;

    public string? GetGpuName() => _gpuName;

    // Temperature

    public int? GetCurrentTemp()
    {
        // Method 1: hwmon sysfs (fastest, no process spawn)
        if (_hwmonDir != null)
        {
            int temp = SysfsHelper.ReadInt(Path.Combine(_hwmonDir, "temp1_input"), -1);
            if (temp > 0)
                return temp / 1000;
        }

        // Method 2: nvidia-smi
        var output = RunNvidiaSmi("--query-gpu=temperature.gpu", "--format=csv,noheader,nounits");
        if (output != null && int.TryParse(output.Trim(), out int smiTemp))
            return smiTemp;

        return null;
    }

    // Utilization

    public int? GetGpuUse()
    {
        var output = RunNvidiaSmi("--query-gpu=utilization.gpu", "--format=csv,noheader,nounits");
        if (output != null && int.TryParse(output.Trim(), out int usage))
            return usage;

        return null;
    }

    // Clocks

    public int? GetCurrentClock()
    {
        var output = RunNvidiaSmi("--query-gpu=clocks.current.graphics", "--format=csv,noheader,nounits");
        if (output != null && int.TryParse(output.Trim(), out int clock))
            return clock;

        return null;
    }

    public int? GetCurrentMemoryClock()
    {
        var output = RunNvidiaSmi("--query-gpu=clocks.current.memory", "--format=csv,noheader,nounits");
        if (output != null && int.TryParse(output.Trim(), out int clock))
            return clock;

        return null;
    }

    // Power

    public int? GetCurrentPower()
    {
        var output = RunNvidiaSmi("--query-gpu=power.draw", "--format=csv,noheader,nounits");
        if (output != null && double.TryParse(output.Trim(), CultureInfo.InvariantCulture, out double watts))
            return (int)Math.Round(watts);

        return null;
    }

    // Clock Offsets
    //
    // The IGpuControl interface requires SetCoreClockOffset / SetMemoryClockOffset, but
    // applying offsets on Linux Nvidia needs root + NVML. The atomic single-pkexec path
    // is ApplyGpuSettings(powerW, clockLockMhz, coreOffsetMhz, memOffsetMhz) which chains
    // a self-respawn into the privileged --apply-gpu-oc worker (Program.cs -> Nvml.ApplyOffsets).
    // These per-call setters would require a separate pkexec prompt each, so they are
    // intentionally no-ops.

    public void SetCoreClockOffset(int offsetMhz)
    {
    }

    public void SetMemoryClockOffset(int offsetMhz)
    {
    }

    /// <summary>
    /// Query GPU power limits: (defaultW, minW, maxW, enforcedW).
    /// Returns null if unavailable.
    /// </summary>
    public (int defaultW, int minW, int maxW, int enforcedW)? GetPowerLimits()
    {
        var output = RunNvidiaSmi(
            "--query-gpu=power.default_limit,power.min_limit,power.max_limit,enforced.power.limit",
            "--format=csv,noheader,nounits");
        if (output == null)
            return null;

        var parts = output.Split(',');
        if (parts.Length < 4)
            return null;

        double ParseW(string s)
        {
            s = s.Trim();
            if (double.TryParse(s, CultureInfo.InvariantCulture, out double v))
                return v;
            return -1;
        }

        var def = ParseW(parts[0]);
        var min = ParseW(parts[1]);
        var max = ParseW(parts[2]);
        var enf = ParseW(parts[3]);

        if (def < 0 || min < 0 || max < 0)
            return null;
        return ((int)Math.Round(def), (int)Math.Round(min), (int)Math.Round(max), (int)Math.Round(enf));
    }

    /// <summary>
    /// Apply power limit, clock lock, and core/memory clock offsets in a single pkexec call.
    /// clockLockMhz &lt;= 0 resets the clock lock. Offsets of 0 clear any prior offset
    /// (NVML treats offset=0 as "remove offset", so the OC step is a no-op when both are 0).
    /// The offset step re-invokes this same binary with --apply-gpu-oc CORE MEM as root.
    /// </summary>
    public void ApplyGpuSettings(int powerW, int clockLockMhz, int coreOffsetMhz, int memOffsetMhz)
    {
        if (!_available)
            return;

        string clockCmd = clockLockMhz > 0
            ? $"nvidia-smi -lgc 0,{Math.Clamp(clockLockMhz, 200, 3000)}"
            : "nvidia-smi -rgc";

        string selfPath = Environment.ProcessPath ?? "ghelper";
        string ocCmd = $"{ShellQuote(selfPath)} --apply-gpu-oc {coreOffsetMhz} {memOffsetMhz}";

        string? pkexecOutput = SysfsHelper.RunPkexecBash(
            $"nvidia-smi -pl {powerW} && {clockCmd} && {ocCmd}");

        string summary =
            $"power={powerW}W, " +
            $"lock={(clockLockMhz > 0 ? $"{clockLockMhz}MHz" : "unlocked")}, " +
            $"coreOffset={coreOffsetMhz}MHz, memOffset={memOffsetMhz}MHz";

        if (pkexecOutput == null)
        {
            Helpers.Logger.WriteLine(
                $"NVIDIA: Apply FAILED ({summary}) — pkexec chain returned null " +
                "(non-zero exit, timeout, or auth cancel)");
        }
        else
        {
            Helpers.Logger.WriteLine(
                $"NVIDIA: Applied {summary} (single pkexec)" +
                (string.IsNullOrWhiteSpace(pkexecOutput) ? "" : $" stdout={pkexecOutput.Trim()}"));
        }
    }

    /// <summary>POSIX shell single-quote escape: 'foo' bar' -> ''\''foo'\'' bar'\'''.</summary>
    private static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    // Extended queries (not in interface but useful)

    /// <summary>Get comprehensive GPU status in one call.</summary>
    public (int? temp, int? usage, int? clock, int? memClock, int? power, int? fanSpeed)?
        GetFullStatus()
    {
        // Single nvidia-smi call for all values (much faster than 5 separate calls)
        var output = RunNvidiaSmi(
            "--query-gpu=temperature.gpu,utilization.gpu,clocks.current.graphics,clocks.current.memory,power.draw,fan.speed",
            "--format=csv,noheader,nounits");

        if (output == null)
            return null;

        var parts = output.Split(',');
        if (parts.Length < 6)
            return null;

        int? ParsePart(string s)
        {
            s = s.Trim();
            if (s == "[N/A]" || s == "N/A" || s == "")
                return null;
            if (int.TryParse(s, out int v))
                return v;
            if (double.TryParse(s, CultureInfo.InvariantCulture, out double d))
                return (int)Math.Round(d);
            return null;
        }

        return (
            temp: ParsePart(parts[0]),
            usage: ParsePart(parts[1]),
            clock: ParsePart(parts[2]),
            memClock: ParsePart(parts[3]),
            power: ParsePart(parts[4]),
            fanSpeed: ParsePart(parts[5])
        );
    }

    // Private helpers

    private bool CheckAvailability()
    {
        // Check if nvidia-smi exists and returns successfully
        var output = RunNvidiaSmi("--query-gpu=name", "--format=csv,noheader");
        return output != null && output.Trim().Length > 0;
    }

    private string? QueryGpuName()
    {
        var output = RunNvidiaSmi("--query-gpu=name", "--format=csv,noheader");
        return output?.Trim();
    }

    private static string? RunNvidiaSmi(string query, string format = "")
    {
        var args = string.IsNullOrEmpty(format) ? query : $"{query} {format}";
        // Use shorter timeout (3 seconds) for GPU queries to prevent UI freeze
        return SysfsHelper.RunCommandWithTimeout("nvidia-smi", args, 3000);
    }
}
