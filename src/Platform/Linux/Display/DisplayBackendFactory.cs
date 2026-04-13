namespace GHelper.Linux.Platform.Linux.Display;

/// <summary>
/// Auto-detects the best display backend for the current session.
///
/// Probe order:
///   Wayland: wlr-randr → gdctl (GNOME 48+) → kscreen-doctor (KDE) → xrandr (XWayland fallback)
///   X11:     xrandr
///
/// Each backend is probed once at startup. The first one that succeeds is used
/// for the lifetime of the session.
/// </summary>
public static class DisplayBackendFactory
{
    /// <summary>
    /// Detect the session type and probe for the best available backend.
    /// Returns null only if no backend works at all.
    /// </summary>
    public static IDisplayBackend? Create()
    {
        bool isWayland = IsWaylandSession();

        if (isWayland)
            return CreateWayland();
        else
            return CreateX11();
    }

    private static IDisplayBackend? CreateWayland()
    {
        // 1. Try wlr-randr (widest Wayland support)
        var wlrPath = Helpers.NativeLibExtractor.FindTool("wlr-randr");
        if (wlrPath != null)
        {
            Helpers.Logger.WriteLine($"Display: probing wlr-randr at {wlrPath}...");
            if (WlrRandrBackend.Probe(wlrPath))
            {
                Helpers.Logger.WriteLine($"Display: Wayland session, using wlr-randr");
                return new WlrRandrBackend(wlrPath);
            }
            Helpers.Logger.WriteLine("Display: wlr-randr found but compositor doesn't support wlr-output-management");
        }

        string? compositor = DetectCompositor();

        // 2. Try gdctl (GNOME 48+, uses org.gnome.Mutter.DisplayConfig D-Bus)
        if (compositor == "gnome-shell" || compositor == null)
        {
            Helpers.Logger.WriteLine("Display: probing gdctl...");
            if (GdctlBackend.Probe())
            {
                Helpers.Logger.WriteLine("Display: Wayland session, using gdctl (GNOME)");
                return new GdctlBackend();
            }
            if (compositor == "gnome-shell")
                Helpers.Logger.WriteLine("Display: gdctl unavailable (GNOME < 48 or mutter-common-bin not installed)");
        }

        // 3. Try kscreen-doctor (KDE Wayland, including Plasma 5.x)
        if (compositor == "kwin" || compositor == null)
        {
            Helpers.Logger.WriteLine("Display: probing kscreen-doctor...");
            if (KScreenBackend.Probe())
            {
                Helpers.Logger.WriteLine("Display: Wayland session, using kscreen-doctor (KDE)");
                return new KScreenBackend();
            }
            if (compositor == "kwin")
                Helpers.Logger.WriteLine("Display: kscreen-doctor unavailable on KDE Wayland");
        }

        // 4. Fallback: xrandr via XWayland (may work for reads on some compositors)
        Helpers.Logger.WriteLine("Display: probing xrandr via XWayland...");
        if (XrandrBackend.Probe())
        {
            Helpers.Logger.WriteLine("Display: Wayland session, falling back to xrandr (XWayland)");
            return new XrandrBackend();
        }

        Helpers.Logger.WriteLine("Display: WARNING — no display backend available on this Wayland session");
        return null;
    }

    private static IDisplayBackend? CreateX11()
    {
        Helpers.Logger.WriteLine("Display: X11 session, probing xrandr...");
        if (XrandrBackend.Probe())
        {
            Helpers.Logger.WriteLine("Display: X11 session, using xrandr");
            return new XrandrBackend();
        }

        Helpers.Logger.WriteLine("Display: WARNING — xrandr not available on this X11 session");
        return null;
    }

    // ── Session detection helpers ──

    /// <summary>Detect if we're running on a Wayland session.</summary>
    public static bool IsWaylandSession()
    {
        var xdgType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        if (xdgType != null && xdgType.Equals("wayland", StringComparison.OrdinalIgnoreCase))
            return true;

        var waylandDisplay = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        return !string.IsNullOrEmpty(waylandDisplay);
    }

    /// <summary>
    /// Detect the active Wayland compositor.
    /// Returns: "kwin", "sway", "hyprland", "gnome-shell", "niri", "river", "wayfire", "labwc", "cosmic", or null.
    /// </summary>
    private static string? DetectCompositor()
    {
        // XDG_CURRENT_DESKTOP is the most reliable
        var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")?.ToLowerInvariant();
        if (desktop != null)
        {
            if (desktop.Contains("kde") || desktop.Contains("plasma"))
                return "kwin";
            if (desktop.Contains("gnome"))
                return "gnome-shell";
            if (desktop.Contains("sway"))
                return "sway";
            if (desktop.Contains("hyprland"))
                return "hyprland";
            if (desktop.Contains("niri"))
                return "niri";
            if (desktop.Contains("cosmic"))
                return "cosmic";
        }

        // HYPRLAND_INSTANCE_SIGNATURE is set by Hyprland
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE")))
            return "hyprland";

        // SWAYSOCK is set by Sway
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SWAYSOCK")))
            return "sway";

        return null;
    }
}
