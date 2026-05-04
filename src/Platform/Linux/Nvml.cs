using System.Runtime.InteropServices;

namespace GHelper.Linux.Platform.Linux;

/// <summary>
/// P/Invoke wrapper around libnvidia-ml.so.1 for clock-offset overclocking.
///
/// Driver 555+ exposes nvmlDeviceSetClockOffsets via a versioned struct.
/// nvmlDeviceSetGpcClkVfOffset / nvmlDeviceSetMemClkVfOffset are marked
/// deprecated upstream but still functional in current drivers - used as a
/// fallback when the modern symbol is missing.
///
/// Setting clock offsets requires root. Intended to be invoked from the
/// pkexec-elevated worker spawned by LinuxNvidiaGpuControl.ApplyGpuSettings:
///   pkexec bash -c '&lt;binary&gt; --apply-gpu-oc CORE_MHZ MEM_MHZ'
/// </summary>
public static class Nvml
{
    private const string NvmlLib = "libnvidia-ml.so.1";

    // nvmlClockType_t
    private const int NvmlClockGraphics = 0;
    private const int NvmlClockMem = 2;

    // nvmlPstates_t. Only P-state 0 actually accepts an offset in current drivers
    // (NVIDIA dev forum thread #318332 - other pstates are silently ignored).
    private const int NvmlPstate0 = 0;

    // nvmlReturn_t
    private const int NvmlSuccess = 0;
    private const int NvmlErrorFunctionNotFound = 13;

    /// <summary>
    /// Mirrors nvmlClockOffset_v1_t in NVIDIA's nvml.h (6 fields, 24 bytes).
    /// MinClockOffsetMhz/MaxClockOffsetMhz are out parameters the driver fills with
    /// the valid offset range for the requested clockType+pstate; we leave them at 0
    /// when calling Set, but they must still be present so the version-encoded size
    /// matches what the driver expects (otherwise NVML returns ARGUMENT_VERSION_MISMATCH).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlClockOffset
    {
        public uint Version;
        public int Type;     // nvmlClockType_t
        public int Pstate;   // nvmlPstates_t
        public int ClockOffsetMhz;
        public int MinClockOffsetMhz;
        public int MaxClockOffsetMhz;
    }

    [DllImport(NvmlLib, EntryPoint = "nvmlInit_v2")]
    private static extern int NvmlInit();

    [DllImport(NvmlLib, EntryPoint = "nvmlShutdown")]
    private static extern int NvmlShutdown();

    [DllImport(NvmlLib, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    private static extern int NvmlDeviceGetHandleByIndex(uint index, out IntPtr device);

    [DllImport(NvmlLib, EntryPoint = "nvmlDeviceSetClockOffsets")]
    private static extern int NvmlDeviceSetClockOffsets(IntPtr device, ref NvmlClockOffset info);

    [DllImport(NvmlLib, EntryPoint = "nvmlDeviceGetClockOffsets")]
    private static extern int NvmlDeviceGetClockOffsets(IntPtr device, ref NvmlClockOffset info);

    [DllImport(NvmlLib, EntryPoint = "nvmlDeviceSetGpcClkVfOffset")]
    private static extern int NvmlDeviceSetGpcClkVfOffset(IntPtr device, int offsetMhz);

    [DllImport(NvmlLib, EntryPoint = "nvmlDeviceSetMemClkVfOffset")]
    private static extern int NvmlDeviceSetMemClkVfOffset(IntPtr device, int offsetMhz);

    /// <summary>
    /// Apply core and memory clock offsets in MHz to GPU index 0, P-state 0.
    /// Pass 0 to clear an offset. Returns true only if both writes succeed.
    /// All NVML failures are logged via Helpers.Logger.
    /// </summary>
    public static bool ApplyOffsets(int coreOffsetMhz, int memOffsetMhz)
    {
        bool initialized = false;
        try
        {
            int rc = NvmlInit();
            if (rc != NvmlSuccess)
            {
                Helpers.Logger.WriteLine($"NVML: nvmlInit_v2 failed (rc={rc})");
                return false;
            }
            initialized = true;

            rc = NvmlDeviceGetHandleByIndex(0, out IntPtr device);
            if (rc != NvmlSuccess)
            {
                Helpers.Logger.WriteLine($"NVML: nvmlDeviceGetHandleByIndex_v2(0) failed (rc={rc})");
                return false;
            }

            bool coreOk = SetClockOffset(device, NvmlClockGraphics, coreOffsetMhz, isCore: true);
            bool memOk = SetClockOffset(device, NvmlClockMem, memOffsetMhz, isCore: false);
            return coreOk && memOk;
        }
        catch (DllNotFoundException ex)
        {
            Helpers.Logger.WriteLine("NVML: libnvidia-ml.so.1 not found - is the proprietary nvidia driver installed?", ex);
            return false;
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine("NVML: ApplyOffsets failed", ex);
            return false;
        }
        finally
        {
            if (initialized)
            {
                try { NvmlShutdown(); }
                catch { /* shutdown failure is non-fatal */ }
            }
        }
    }

    private static bool SetClockOffset(IntPtr device, int clockType, int offsetMhz, bool isCore)
    {
        string label = isCore ? "core" : "mem";

        var info = new NvmlClockOffset
        {
            Version = (uint)Marshal.SizeOf<NvmlClockOffset>() | (1U << 24),
            Type = clockType,
            Pstate = NvmlPstate0,
            ClockOffsetMhz = offsetMhz,
        };

        int rc;
        try
        {
            rc = NvmlDeviceSetClockOffsets(device, ref info);
        }
        catch (EntryPointNotFoundException)
        {
            // Pre-555 driver lacks the modern symbol entirely.
            rc = NvmlErrorFunctionNotFound;
        }

        if (rc == NvmlSuccess)
        {
            Helpers.Logger.WriteLine($"NVML: {label} offset set to {offsetMhz} MHz (nvmlDeviceSetClockOffsets)");
            return true;
        }

        if (rc == NvmlErrorFunctionNotFound)
        {
            try
            {
                int legacyRc = isCore
                    ? NvmlDeviceSetGpcClkVfOffset(device, offsetMhz)
                    : NvmlDeviceSetMemClkVfOffset(device, offsetMhz);
                if (legacyRc == NvmlSuccess)
                {
                    Helpers.Logger.WriteLine($"NVML: {label} offset set to {offsetMhz} MHz (legacy VF API)");
                    return true;
                }
                Helpers.Logger.WriteLine($"NVML: legacy {(isCore ? "GpcClkVfOffset" : "MemClkVfOffset")} failed (rc={legacyRc})");
            }
            catch (EntryPointNotFoundException ex)
            {
                Helpers.Logger.WriteLine($"NVML: neither nvmlDeviceSetClockOffsets nor legacy VF setter present", ex);
            }
            return false;
        }

        Helpers.Logger.WriteLine($"NVML: nvmlDeviceSetClockOffsets({label}) failed (rc={rc})");
        return false;
    }

    /// <summary>
    /// Read currently-applied core and memory clock offsets in MHz for GPU 0, P-state 0.
    /// Unlike ApplyOffsets, this does NOT require root - NVML query functions are
    /// unprivileged. Returns null components for any clock the driver cannot report
    /// (pre-555 driver missing nvmlDeviceGetClockOffsets, or transient NVML failure).
    /// Returns (null, null) entirely if NVML or the driver is unavailable.
    /// </summary>
    public static (int? core, int? mem) GetCurrentOffsets()
    {
        bool initialized = false;
        try
        {
            int rc = NvmlInit();
            if (rc != NvmlSuccess)
            {
                Helpers.Logger.WriteLine($"NVML(get): nvmlInit_v2 failed (rc={rc})");
                return (null, null);
            }
            initialized = true;

            rc = NvmlDeviceGetHandleByIndex(0, out IntPtr device);
            if (rc != NvmlSuccess)
            {
                Helpers.Logger.WriteLine($"NVML(get): nvmlDeviceGetHandleByIndex_v2(0) failed (rc={rc})");
                return (null, null);
            }

            return (ReadOneOffset(device, NvmlClockGraphics, "core"),
                    ReadOneOffset(device, NvmlClockMem, "mem"));
        }
        catch (DllNotFoundException ex)
        {
            Helpers.Logger.WriteLine("NVML(get): libnvidia-ml.so.1 not found", ex);
            return (null, null);
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine("NVML(get): GetCurrentOffsets failed", ex);
            return (null, null);
        }
        finally
        {
            if (initialized)
            {
                try { NvmlShutdown(); } catch { /* shutdown failure is non-fatal */ }
            }
        }
    }

    private static int? ReadOneOffset(IntPtr device, int clockType, string label)
    {
        var info = new NvmlClockOffset
        {
            Version = (uint)Marshal.SizeOf<NvmlClockOffset>() | (1U << 24),
            Type = clockType,
            Pstate = NvmlPstate0,
        };

        try
        {
            int rc = NvmlDeviceGetClockOffsets(device, ref info);
            if (rc == NvmlSuccess)
                return info.ClockOffsetMhz;

            Helpers.Logger.WriteLine($"NVML(get): nvmlDeviceGetClockOffsets({label}) failed (rc={rc})");
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            // Pre-555 driver: symbol absent. No legacy "Get" equivalent exists.
            // Silent to avoid spamming the log on every window open.
            return null;
        }
    }
}
