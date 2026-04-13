using System.Globalization;
using System.Text.Json;

namespace GHelper.Linux.Platform.Linux.Display;

/// <summary>
/// Display backend using wlr-randr (Wayland).
/// Uses the wlr-output-management-unstable-v1 protocol.
/// Should work on: Hyprland, Sway, KDE 6.6+, GNOME 49+, niri, river, Labwc, COSMIC, Wayfire.
/// </summary>
public class WlrRandrBackend : IDisplayBackend
{
    private readonly string _binaryPath;

    public WlrRandrBackend(string binaryPath)
    {
        _binaryPath = binaryPath;
    }

    public string Name => "wlr-randr";
    public bool SupportsGamma => false;

    public int GetRefreshRate()
    {
        try
        {
            var json = RunJson();
            if (json == null) return -1;

            using var doc = JsonDocument.Parse(json);
            var output = FindLaptopOutput(doc);
            if (output == null) return -1;

            var mode = FindCurrentMode(output.Value);
            if (mode == null) return -1;

            return (int)Math.Round(mode.Value.GetProperty("refresh").GetDouble());
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine("WlrRandrBackend.GetRefreshRate failed", ex);
            return -1;
        }
    }

    public List<int> GetAvailableRefreshRates()
    {
        var rates = new List<int>();
        try
        {
            var json = RunJson();
            if (json == null) return rates;

            using var doc = JsonDocument.Parse(json);
            var output = FindLaptopOutput(doc);
            if (output == null) return rates;

            var curMode = FindCurrentMode(output.Value);
            int curW = 0, curH = 0;
            if (curMode != null)
            {
                curW = curMode.Value.GetProperty("width").GetInt32();
                curH = curMode.Value.GetProperty("height").GetInt32();
            }

            foreach (var mode in output.Value.GetProperty("modes").EnumerateArray())
            {
                int w = mode.GetProperty("width").GetInt32();
                int h = mode.GetProperty("height").GetInt32();

                if (curW > 0 && (w != curW || h != curH))
                    continue;

                int hz = (int)Math.Round(mode.GetProperty("refresh").GetDouble());
                if (hz > 0 && !rates.Contains(hz))
                    rates.Add(hz);
            }

            rates.Sort();
            rates.Reverse();
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine("WlrRandrBackend.GetAvailableRefreshRates failed", ex);
        }
        return rates;
    }

    public void SetRefreshRate(int hz)
    {
        try
        {
            Helpers.Logger.WriteLine($"WlrRandr.SetRefreshRate: requesting {hz}Hz");

            var json = RunJson();
            if (json == null)
            {
                Helpers.Logger.WriteLine("WlrRandr.SetRefreshRate: wlr-randr --json returned null (command failed or compositor unsupported)");
                return;
            }

            using var doc = JsonDocument.Parse(json);
            var output = FindLaptopOutput(doc);
            if (output == null)
            {
                Helpers.Logger.WriteLine("WlrRandr.SetRefreshRate: no laptop output (eDP) found in wlr-randr output");
                return;
            }

            var outputName = output.Value.GetProperty("name").GetString();
            if (outputName == null)
            {
                Helpers.Logger.WriteLine("WlrRandr.SetRefreshRate: output has no name property");
                return;
            }

            var curMode = FindCurrentMode(output.Value);
            if (curMode == null)
            {
                Helpers.Logger.WriteLine($"WlrRandr.SetRefreshRate: no current mode found for {outputName}");
                return;
            }

            int curW = curMode.Value.GetProperty("width").GetInt32();
            int curH = curMode.Value.GetProperty("height").GetInt32();

            // Find the exact rate — wlr-randr matches in mHz internally
            double bestRate = 0;
            double bestDist = double.MaxValue;

            foreach (var mode in output.Value.GetProperty("modes").EnumerateArray())
            {
                int w = mode.GetProperty("width").GetInt32();
                int h = mode.GetProperty("height").GetInt32();
                if (w != curW || h != curH) continue;

                double rate = mode.GetProperty("refresh").GetDouble();
                double dist = Math.Abs(rate - hz);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestRate = rate;
                }
            }

            if (bestRate <= 0)
            {
                Helpers.Logger.WriteLine($"WlrRandr.SetRefreshRate: no matching mode for {curW}x{curH}@{hz}Hz on {outputName}");
                return;
            }

            var modeStr = string.Format(CultureInfo.InvariantCulture,
                "{0}x{1}@{2:F3}Hz", curW, curH, bestRate);

            Helpers.Logger.WriteLine($"WlrRandr.SetRefreshRate: running: {_binaryPath} --output {outputName} --mode {modeStr}");

            var result = SysfsHelper.RunCommandWithTimeout(_binaryPath,
                new[] { "--output", outputName, "--mode", modeStr }, 5000);

            if (result != null)
                Helpers.Logger.WriteLine($"WlrRandr.SetRefreshRate: success — {outputName} -> {modeStr}");
            else
                Helpers.Logger.WriteLine($"WlrRandr.SetRefreshRate: wlr-randr command failed (non-zero exit or timeout)");
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine("WlrRandr.SetRefreshRate failed", ex);
        }
    }

    public void SetGamma(float r, float g, float b)
    {
        // wlr-randr has no gamma support
        Helpers.Logger.WriteLine("SetGamma: not available via wlr-randr");
    }

    public string? GetDisplayName()
    {
        try
        {
            var json = RunJson();
            if (json == null) return null;

            using var doc = JsonDocument.Parse(json);
            var output = FindLaptopOutput(doc);
            if (output == null) return null;

            if (output.Value.TryGetProperty("description", out var desc))
            {
                var d = desc.GetString();
                if (!string.IsNullOrEmpty(d)) return d;
            }

            return output.Value.GetProperty("name").GetString();
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine("WlrRandrBackend.GetDisplayName failed", ex);
            return null;
        }
    }

    // ── Probing ──

    /// <summary>
    /// Test if wlr-randr works with the current compositor.
    /// Returns true only if the binary runs AND the compositor supports the protocol.
    /// </summary>
    public static bool Probe(string binaryPath)
    {
        var result = SysfsHelper.RunCommandWithTimeout(binaryPath, new[] { "--json" }, 5000);
        return result != null;
    }

    // ── Internal helpers ──

    private string? RunJson()
    {
        return SysfsHelper.RunCommandWithTimeout(_binaryPath, new[] { "--json" }, 5000);
    }

    /// <summary>Find the laptop panel (eDP preferred, then first enabled).</summary>
    internal static JsonElement? FindLaptopOutput(JsonDocument doc)
    {
        foreach (var output in doc.RootElement.EnumerateArray())
        {
            var name = output.GetProperty("name").GetString() ?? "";
            if (name.StartsWith("eDP", StringComparison.OrdinalIgnoreCase))
                return output;
        }
        foreach (var output in doc.RootElement.EnumerateArray())
        {
            if (output.TryGetProperty("enabled", out var en) && en.GetBoolean())
                return output;
        }
        return null;
    }

    /// <summary>Find the mode with "current": true.</summary>
    internal static JsonElement? FindCurrentMode(JsonElement output)
    {
        foreach (var mode in output.GetProperty("modes").EnumerateArray())
        {
            if (mode.TryGetProperty("current", out var cur) && cur.GetBoolean())
                return mode;
        }
        return null;
    }
}
