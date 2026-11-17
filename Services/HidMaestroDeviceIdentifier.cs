using System.Collections.Concurrent;
using System.IO;
using Microsoft.Win32;

namespace GDM.Services;

/// <summary>
/// Identifica mandos virtuales creados por HIDMaestro. El devnode HID del
/// dispositivo virtual incluye el hardware ID "HID\HIDMaestro", que se puede
/// leer en HKLM\SYSTEM\CurrentControlSet\Enum\&lt;instanceId&gt;\HardwareID.
/// El instance ID se reconstruye desde la ruta de dispositivo que expone SDL.
/// </summary>
internal static class HidMaestroDeviceIdentifier
{
    private const string EnumRegistryPath = @"SYSTEM\CurrentControlSet\Enum\";

    private static readonly ConcurrentDictionary<string, bool> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsVirtualGamepad(string? devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
        {
            return false;
        }

        return Cache.GetOrAdd(devicePath, ResolveIsVirtualGamepad);
    }

    private static bool ResolveIsVirtualGamepad(string devicePath)
    {
        if (devicePath.Contains("HIDMAESTRO", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string? instanceId = TryGetDeviceInstanceId(devicePath);
        if (instanceId is null)
        {
            return false;
        }

        try
        {
            object? value = Registry.LocalMachine
                .OpenSubKey(EnumRegistryPath + instanceId, writable: false)
                ?.GetValue("HardwareID");
            return value is string[] hardwareIds &&
                hardwareIds.Any(id =>
                    id.Contains("HIDMAESTRO", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (
            exception is System.Security.SecurityException or
            UnauthorizedAccessException or
            IOException)
        {
            return false;
        }
    }

    private static string? TryGetDeviceInstanceId(string devicePath)
    {
        // Formato esperado: \\?\HID#<enumerador>#<instancia>#{GUID}
        string trimmed = devicePath.Trim();
        const string prefix = @"\\?\";
        if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[prefix.Length..];
        }

        string[] segments = trimmed.Split('#');
        if (segments.Length < 3 ||
            !segments[0].Equals("HID", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"HID\\{segments[1]}\\{segments[2]}";
    }
}
