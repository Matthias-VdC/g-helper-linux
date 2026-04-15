using HidSharp;
using HidSharp.Reports;

namespace GHelper.Linux.USB;

/// <summary>
/// Linux port of G-Helper's AsusHid.cs.
/// Handles HID device discovery and communication for ASUS AURA keyboards.
///
/// On Linux, HidSharpCore talks to /dev/hidraw* devices - but ONLY those
/// with a USB parent device. I2C-HID devices (like the FA608PP keyboard)
/// are invisible to HidSharp. When no USB device is found, we fall back
/// to HidrawHelper which scans /dev/hidraw* via native ioctl regardless
/// of bus type.
///
/// Requires udev rules for non-root access:
///   SUBSYSTEM=="hidraw", ATTRS{idVendor}=="0b05", MODE="0666"
/// </summary>
public static class AsusHid
{
    public const int ASUS_ID = 0x0B05;
    public const byte INPUT_ID = 0x5A;
    public const byte AURA_ID = 0x5D;

    private static readonly int[] DeviceIds =
    {
        0x1A30, 0x1854, 0x1869, 0x1866, 0x19B6, 0x1822, 0x1837,
        0x184A, 0x183D, 0x8502, 0x1807, 0x17E0, 0x18C6, 0x1ABE,
        0x1B4C, 0x1B6E, 0x1B2C, 0x8854
    };

    private static HidStream? _auraStream;

    // Track whether we're using I2C-HID fallback (for logging / behavior)
    private static bool? _usingI2cFallback;

    /// <summary>
    /// Whether we are using the native I2C-HID hidraw path instead of HidSharp.
    /// This is the case for keyboards like the FA608PP that connect via I2C.
    /// </summary>
    public static bool UsingI2cHidraw
    {
        get
        {
            if (_usingI2cFallback == null)
            {
                // Trigger evaluation: check if HidSharp sees any USB AURA device
                bool hasUsb = false;
                try
                { hasUsb = FindDevices(AURA_ID).Any(); }
                catch { }
                _usingI2cFallback = !hasUsb && HidrawHelper.HasAsusAuraDevice();
            }
            return _usingI2cFallback.Value;
        }
    }

    /// <summary>
    /// Find all ASUS HID devices that support a given report ID.
    /// This only finds USB-HID devices (HidSharp limitation on Linux).
    /// For I2C-HID devices, use HidrawHelper directly.
    /// </summary>
    public static IEnumerable<HidDevice> FindDevices(byte reportId)
    {
        IEnumerable<HidDevice> allDevices;
        try
        {
            allDevices = DeviceList.Local.GetHidDevices(ASUS_ID);
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"Error enumerating HID devices: {ex.Message}");
            yield break;
        }

        var filteredDevices = new List<HidDevice>();
        foreach (var device in allDevices)
        {
            try
            {
                if (DeviceIds.Contains(device.ProductID) &&
                    device.CanOpen &&
                    device.GetMaxFeatureReportLength() > 0)
                {
                    filteredDevices.Add(device);
                }
            }
            catch (Exception ex)
            {
                Helpers.Logger.WriteLine($"Error checking HID device {device.ProductID:X}: {ex.Message}");
            }
        }

        foreach (var device in filteredDevices)
        {
            bool isValid = false;
            try
            {
                isValid = device.GetReportDescriptor().TryGetReport(ReportType.Feature, reportId, out _);
            }
            catch { }

            if (isValid)
                yield return device;
        }
    }

    /// <summary>
    /// Find and open an HID stream for the given report ID.
    /// </summary>
    public static HidStream? FindHidStream(byte reportId)
    {
        try
        {
            var devices = FindDevices(reportId);
            if (devices == null)
                return null;

            foreach (var device in devices)
                Helpers.Logger.WriteLine($"HID available: {device.DevicePath} {device.ProductID:X} len={device.GetMaxFeatureReportLength()}");

            return devices.FirstOrDefault()?.Open();
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"Error accessing HID device: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Write data to INPUT_ID devices via SetFeature.
    /// </summary>
    public static void WriteInput(byte[] data, string? log = "USB")
    {
        // Try HidSharp (USB) first
        bool wroteAny = false;
        foreach (var device in FindDevices(INPUT_ID))
        {
            try
            {
                using var stream = device.Open();
                var payload = new byte[device.GetMaxFeatureReportLength()];
                Array.Copy(data, payload, Math.Min(data.Length, payload.Length));
                stream.SetFeature(payload);
                wroteAny = true;
                if (log != null)
                    Helpers.Logger.WriteLine($"{log} {device.ProductID:X}|{device.GetMaxFeatureReportLength()}: {BitConverter.ToString(data)}");
            }
            catch (Exception ex)
            {
                Helpers.Logger.WriteLine($"Error setting feature {device.DevicePath}: {BitConverter.ToString(data)} {ex.Message}");
            }
        }

        // Fallback: I2C-HID via native hidraw
        if (!wroteAny && HidrawHelper.HasAsusAuraDevice())
        {
            HidrawHelper.WriteAll(INPUT_ID, data, log);
        }
    }

    /// <summary>
    /// Write a single message to all AURA_ID devices.
    /// </summary>
    public static void Write(byte[] data, string? log = "USB")
    {
        Write(new List<byte[]> { data }, log);
    }

    /// <summary>
    /// Write multiple messages to all AURA_ID devices.
    /// Falls back to native hidraw for I2C-HID devices.
    /// </summary>
    public static void Write(List<byte[]> dataList, string? log = "USB")
    {
        bool wroteToAny = false;

        // Try HidSharp (USB-HID) path first
        foreach (var device in FindDevices(AURA_ID))
        {
            try
            {
                using var stream = device.Open();
                foreach (var data in dataList)
                {
                    try
                    {
                        stream.Write(data);
                        wroteToAny = true;
                        if (log != null)
                            Helpers.Logger.WriteLine($"{log} {device.ProductID:X}: {BitConverter.ToString(data)}");
                    }
                    catch (Exception ex)
                    {
                        if (log != null)
                            Helpers.Logger.WriteLine($"Error writing {log} {device.ProductID:X}: {ex.Message} {BitConverter.ToString(data)}");
                    }
                }
            }
            catch (Exception ex)
            {
                if (log != null)
                    Helpers.Logger.WriteLine($"Error opening {log} {device.ProductID:X}: {ex.Message}");
            }
        }

        // Fallback: I2C-HID via native hidraw (for devices like FA608PP)
        if (!wroteToAny)
        {
            if (HidrawHelper.WriteAll(AURA_ID, dataList, log))
            {
                if (log != null)
                    Helpers.Logger.WriteLine($"{log} [I2C-HID]: wrote {dataList.Count} messages via hidraw");
            }
        }
    }

    /// <summary>
    /// Write data via persistent AURA stream (used for direct RGB / per-key updates).
    /// Retries once if stream is stale. Falls back to hidraw for I2C-HID.
    /// </summary>
    public static void WriteAura(byte[] data, bool retry = true)
    {
        // If we're on I2C-HID, use hidraw directly (no persistent stream)
        if (UsingI2cHidraw)
        {
            HidrawHelper.WriteAll(AURA_ID, data, null);
            return;
        }

        if (_auraStream == null)
            _auraStream = FindHidStream(AURA_ID);

        if (_auraStream == null)
        {
            // Last resort: try I2C-HID
            if (HidrawHelper.HasAsusAuraDevice())
            {
                HidrawHelper.WriteAll(AURA_ID, data, null);
                return;
            }
            Helpers.Logger.WriteLine("Aura stream not found");
            return;
        }

        try
        {
            _auraStream.Write(data);
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"Error writing to Aura HID: {ex.Message} {BitConverter.ToString(data)}");
            _auraStream.Dispose();
            _auraStream = null;
            if (retry)
                WriteAura(data, false);
        }
    }

    /// <summary>
    /// Check if any AURA HID device is available (USB or I2C-HID).
    /// </summary>
    public static bool IsAvailable()
    {
        try
        {
            // Check USB-HID via HidSharp
            if (FindDevices(AURA_ID).Any())
                return true;
        }
        catch { }

        // Check I2C-HID via native hidraw
        try
        {
            return HidrawHelper.HasAsusAuraDevice();
        }
        catch { return false; }
    }
}
