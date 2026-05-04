using Avalonia;
using Avalonia.Skia;
using Avalonia.X11;
using GHelper.Linux;
using GHelper.Linux.Helpers;
using GHelper.Linux.Platform.Linux;

// G-Helper for Linux - single binary ASUS laptop control
// Port of https://github.com/seerge/g-helper

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Privileged NVML worker mode: re-invoked as root via pkexec from
        // LinuxNvidiaGpuControl.ApplyGpuSettings to apply core/memory clock offsets.
        // Exits before any Avalonia / GUI code runs.
        if (args.Length >= 3 && args[0] == "--apply-gpu-oc")
        {
            if (!int.TryParse(args[1], out int coreMhz) || !int.TryParse(args[2], out int memMhz))
            {
                Console.Error.WriteLine("ghelper --apply-gpu-oc: expected integer CORE_MHZ MEM_MHZ");
                Environment.Exit(2);
                return;
            }
            Environment.Exit(Nvml.ApplyOffsets(coreMhz, memMhz) ? 0 : 1);
            return;
        }

        // Extract and preload embedded native libraries (libSkiaSharp.so, libHarfBuzzSharp.so)
        // from the binary's embedded resources to ~/.cache/ghelper/libs/ before any
        // Avalonia/SkiaSharp code runs. Cached across launches, invalidated on version change.
        NativeLibExtractor.ExtractAndLoad();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseX11()
            .UseSkia()
            .UseHarfBuzz()
            .LogToTrace();
}
