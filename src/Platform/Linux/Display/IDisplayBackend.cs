namespace GHelper.Linux.Platform.Linux.Display;

/// <summary>
/// Backend interface for display refresh rate, gamma, and display info.
/// Each implementation wraps a specific tool/protocol (xrandr, wlr-randr, kscreen-doctor, etc.).
/// Brightness is NOT part of this interface, it's handled via sysfs in LinuxDisplayControl.
/// </summary>
public interface IDisplayBackend
{
    /// <summary>Human-readable backend name for logging (e.g. "xrandr", "wlr-randr", "kscreen-doctor").</summary>
    string Name { get; }

    /// <summary>True if this backend supports gamma adjustment.</summary>
    bool SupportsGamma { get; }

    /// <summary>Get the current refresh rate in Hz, or -1 if unavailable.</summary>
    int GetRefreshRate();

    /// <summary>Get all available refresh rates for the laptop panel at current resolution, highest first.</summary>
    List<int> GetAvailableRefreshRates();

    /// <summary>Set the refresh rate in Hz (keeps current resolution).</summary>
    void SetRefreshRate(int hz);

    /// <summary>Set display gamma (1.0 = normal). No-op if SupportsGamma is false.</summary>
    void SetGamma(float r, float g, float b);

    /// <summary>Get the display name/description, or null if unavailable.</summary>
    string? GetDisplayName();
}
