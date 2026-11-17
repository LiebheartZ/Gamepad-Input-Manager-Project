using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using GDM.Models;
using GDM.Services.Abstractions;

namespace GDM.Services;

public sealed class ControllerPowerOffService : IControllerPowerOffService
{
    private const uint IoctlBthDisconnectDevice = 0x41000C;
    private const int ErrorElementNotFound = 1167;
    private static readonly TimeSpan SupportCacheTtl = TimeSpan.FromSeconds(30);
    private static readonly Regex BluetoothAddressPattern = new(
        @"(?:DEV_|&0&|REV&[0-9A-Fa-f]{4}_)([0-9A-Fa-f]{12})(?![0-9A-Fa-f])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly Dictionary<string, (bool Supported, DateTimeOffset CheckedAtUtc)> _supportCache =
        new(StringComparer.Ordinal);

    public Task<bool> SupportsPowerOffAsync(
        ConnectedGamepad device,
        CancellationToken cancellationToken = default)
    {
        string? path = device.DevicePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult(false);
        }

        if (_supportCache.TryGetValue(path, out (bool Supported, DateTimeOffset CheckedAtUtc) cached) &&
            DateTimeOffset.UtcNow - cached.CheckedAtUtc < SupportCacheTtl)
        {
            return Task.FromResult(cached.Supported);
        }

        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool supported = TryResolveBluetoothAddress(device, out _, out _);
                _supportCache[path] = (supported, DateTimeOffset.UtcNow);
                return supported;
            },
            cancellationToken);
    }

    public Task<ControllerPowerOffResult> TryPowerOffAsync(
        ConnectedGamepad device,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => PowerOffCore(device, cancellationToken), cancellationToken);

    private static ControllerPowerOffResult PowerOffCore(
        ConnectedGamepad device,
        CancellationToken cancellationToken)
    {
        if (!TryResolveBluetoothAddress(device, out ulong address, out string? detail))
        {
            return new ControllerPowerOffResult(ControllerPowerOffStatus.UnsupportedDevice, detail);
        }

        var radioParams = new BluetoothFindRadioParams { dwSize = BluetoothFindRadioParams.Size };
        IntPtr radioHandle;
        IntPtr findHandle = BluetoothFindFirstRadio(ref radioParams, out radioHandle);
        if (findHandle == IntPtr.Zero)
        {
            return new ControllerPowerOffResult(
                ControllerPowerOffStatus.Failed,
                $"no se encontró una radio Bluetooth (error {Marshal.GetLastWin32Error()})");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] payload = BitConverter.GetBytes(address);
            bool success = DeviceIoControl(
                radioHandle,
                IoctlBthDisconnectDevice,
                payload,
                payload.Length,
                IntPtr.Zero,
                0,
                out _,
                IntPtr.Zero);
            if (success)
            {
                return new ControllerPowerOffResult(ControllerPowerOffStatus.PoweredOff);
            }

            int error = Marshal.GetLastWin32Error();
            return error == ErrorElementNotFound
                ? new ControllerPowerOffResult(
                    ControllerPowerOffStatus.NotConnected,
                    "el mando no tiene un enlace Bluetooth activo")
                : new ControllerPowerOffResult(
                    ControllerPowerOffStatus.Failed,
                    $"DeviceIoControl error {error}");
        }
        finally
        {
            BluetoothFindRadioClose(findHandle);
            CloseHandle(radioHandle);
        }
    }

    private static bool TryResolveBluetoothAddress(
        ConnectedGamepad device,
        out ulong address,
        out string? detail)
    {
        address = 0;
        detail = null;
        if (!TryGetHidInstanceId(device.DevicePath, out string instanceId))
        {
            detail = "la ruta del dispositivo no corresponde a un mando HID";
            return false;
        }

        if (!TryFindDevInst(instanceId, out int devInst))
        {
            detail = "no se encontró el dispositivo en el árbol de Windows";
            return false;
        }

        int current = devInst;
        for (int depth = 0; depth < 8; depth++)
        {
            if (CM_Get_Parent(out int parent, current, 0) != 0 || parent <= 0)
            {
                break;
            }

            current = parent;
            string? ancestorId = GetDeviceInstanceId(current);
            if (ancestorId is null ||
                !(ancestorId.StartsWith("BTHENUM\\", StringComparison.OrdinalIgnoreCase) ||
                  ancestorId.StartsWith("BTHLEDEVICE\\", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            Match match = BluetoothAddressPattern.Match(ancestorId);
            if (match.Success &&
                ulong.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out address))
            {
                return true;
            }
        }

        detail = "el mando no está conectado por Bluetooth";
        return false;
    }

    private static bool TryGetHidInstanceId(string? devicePath, out string instanceId)
    {
        instanceId = string.Empty;
        if (string.IsNullOrWhiteSpace(devicePath))
        {
            return false;
        }

        string trimmed = devicePath.Trim();
        const string prefix = @"\\?\";
        if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[prefix.Length..];
        }

        string[] parts = trimmed.Split('#');
        if (parts.Length < 3 ||
            !string.Equals(parts[0], "HID", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int endIndex = parts[^1].StartsWith('{') ? parts.Length - 1 : parts.Length;
        if (endIndex < 3)
        {
            return false;
        }

        var builder = new StringBuilder("HID\\");
        for (int index = 1; index < endIndex; index++)
        {
            if (index > 1)
            {
                builder.Append('\\');
            }

            builder.Append(parts[index]);
        }

        if (builder.Length <= 4)
        {
            return false;
        }

        instanceId = builder.ToString();
        return true;
    }

    private static bool TryFindDevInst(string instanceId, out int devInst)
    {
        devInst = 0;
        IntPtr deviceInfoSet = SetupDiGetClassDevsW(
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            DigcfAllClasses | DigcfPresent);
        if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == InvalidHandleValue)
        {
            return false;
        }

        try
        {
            var deviceInfoData = new SpDeviceInfoData
            {
                cbSize = Marshal.SizeOf<SpDeviceInfoData>()
            };
            var buffer = new StringBuilder(1024);
            for (uint index = 0;
                 SetupDiEnumDeviceInfo(deviceInfoSet, index, ref deviceInfoData);
                 index++)
            {
                buffer.Clear();
                if (SetupDiGetDeviceInstanceIdW(
                        deviceInfoSet,
                        ref deviceInfoData,
                        buffer,
                        buffer.Capacity,
                        out _) &&
                    string.Equals(buffer.ToString(), instanceId, StringComparison.OrdinalIgnoreCase))
                {
                    devInst = deviceInfoData.DevInst;
                    return true;
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        return false;
    }

    private static string? GetDeviceInstanceId(int devInst)
    {
        var buffer = new StringBuilder(1024);
        return CM_Get_Device_IDW(devInst, buffer, buffer.Capacity, 0) == 0
            ? buffer.ToString()
            : null;
    }

    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfAllClasses = 0x00000004;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInfoData
    {
        public int cbSize;
        public Guid ClassGuid;
        public int DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BluetoothFindRadioParams
    {
        internal const int Size = 4;
        public uint dwSize;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(
        IntPtr classGuid,
        IntPtr enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet,
        uint memberIndex,
        ref SpDeviceInfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceIdW(
        IntPtr deviceInfoSet,
        ref SpDeviceInfoData deviceInfoData,
        StringBuilder deviceInstanceId,
        int deviceInstanceIdSize,
        out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(
        out int parentDevInst,
        int devInst,
        int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_IDW(
        int devInst,
        StringBuilder buffer,
        int bufferLength,
        int flags);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstRadio(
        ref BluetoothFindRadioParams parameters,
        out IntPtr radioHandle);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern bool BluetoothFindRadioClose(IntPtr findHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr device,
        uint ioControlCode,
        byte[] inputBuffer,
        int inputBufferSize,
        IntPtr outputBuffer,
        int outputBufferSize,
        out int bytesReturned,
        IntPtr overlapped);
}
