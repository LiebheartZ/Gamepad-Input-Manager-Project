using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using GDM.Models;
using GDM.Services.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace GDM.Services;

public sealed class SonyControllerLedService : ISonyControllerLedService
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;

    private const int Ds4UsbReportId = 0x05;
    private const int Ds4BtReportId = 0x11;
    private const int DualSenseUsbReportId = 0x02;
    private const int DualSenseBtReportId = 0x31;
    private const int DualSenseBtTag = 0x10;
    private const byte DualSenseOutputCrcSeed = 0xA2;

    private static readonly int[] CrcTable = BuildCrcTable();

    private readonly ConcurrentDictionary<string, string> _lastApplied =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _bluetoothSequences =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _writeGate = new();

    public string? LastFailureDetail { get; private set; }

    public Task<SonyLedApplyResult> TrySetLightBarAsync(
        ConnectedGamepad device,
        byte red,
        byte green,
        byte blue,
        byte brightness,
        int? playerNumber,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(device.DevicePath))
        {
            return Task.FromResult(SonyLedApplyResult.UnsupportedDevice);
        }

        string signature = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{device.DevicePath}|{red:X2}{green:X2}{blue:X2}|{brightness}|{playerNumber?.ToString() ?? "-"}");
        if (!force &&
            _lastApplied.TryGetValue(device.DevicePath, out string? last) && last == signature)
        {
            return Task.FromResult(SonyLedApplyResult.Applied);
        }

        return Task.Run(
            () => Apply(device.DevicePath!, red, green, blue, brightness, playerNumber, signature),
            cancellationToken);
    }

    private SonyLedApplyResult Apply(
        string devicePath,
        byte red,
        byte green,
        byte blue,
        byte brightness,
        int? playerNumber,
        string signature)
    {
        lock (_writeGate)
        {
            try
            {
                using SafeFileHandle handle = CreateFileW(
                    devicePath,
                    GenericRead | GenericWrite,
                    FileShareReadWrite,
                    IntPtr.Zero,
                    OpenExisting,
                    0,
                    IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    LastFailureDetail = $"CreateFile error={Marshal.GetLastWin32Error()}";
                    return SonyLedApplyResult.Failed;
                }

                var attributes = new HIDD_ATTRIBUTES
                {
                    Size = Marshal.SizeOf<HIDD_ATTRIBUTES>()
                };
                if (!HidD_GetAttributes(handle, ref attributes) ||
                    !SonyControllerLedSupport.TryGetKind(
                        attributes.VendorID,
                        attributes.ProductID,
                        out SonyControllerLedKind kind) ||
                    kind == SonyControllerLedKind.None)
                {
                    return SonyLedApplyResult.UnsupportedDevice;
                }

                if (!HidD_GetPreparsedData(handle, out IntPtr preparsedData))
                {
                    LastFailureDetail = $"HidD_GetPreparsedData error={Marshal.GetLastWin32Error()}";
                    return SonyLedApplyResult.Failed;
                }

                HIDP_CAPS capabilities;
                try
                {
                    if (!HidP_GetCaps(preparsedData, out capabilities))
                    {
                        LastFailureDetail = $"HidP_GetCaps error={Marshal.GetLastWin32Error()}";
                        return SonyLedApplyResult.Failed;
                    }
                }
                finally
                {
                    HidD_FreePreparsedData(preparsedData);
                }

                bool isUsb = capabilities.InputReportByteLength == 64;
                byte[] report = BuildReport(
                    kind,
                    isUsb,
                    devicePath,
                    red,
                    green,
                    blue,
                    brightness,
                    playerNumber,
                    Math.Max(capabilities.OutputReportByteLength, MinimumReportLength(kind, isUsb)));

                bool written = isUsb
                    ? WriteOutputReport(handle, report)
                    : SetOutputReport(handle, report) || WriteOutputReport(handle, report);
                if (!written)
                {
                    _lastApplied.TryRemove(devicePath, out _);
                    LastFailureDetail =
                        $"Escritura de reporte LED fallida (usb={isUsb}) error={Marshal.GetLastWin32Error()}";
                    return SonyLedApplyResult.Failed;
                }

                LastFailureDetail = null;
                _lastApplied[devicePath] = signature;
                return SonyLedApplyResult.Applied;
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                System.ComponentModel.Win32Exception or
                InvalidOperationException)
            {
                _lastApplied.TryRemove(devicePath, out _);
                LastFailureDetail = $"Excepción al aplicar LED: {exception.Message}";
                return SonyLedApplyResult.Failed;
            }
        }
    }

    private static int MinimumReportLength(SonyControllerLedKind kind, bool isUsb) =>
        (kind, isUsb) switch
        {
            (SonyControllerLedKind.DualShock4, true) => 11,
            (SonyControllerLedKind.DualShock4, false) => 13,
            (SonyControllerLedKind.DualSense, true) => 48,
            _ => 78
        };

    private byte[] BuildReport(
        SonyControllerLedKind kind,
        bool isUsb,
        string devicePath,
        byte red,
        byte green,
        byte blue,
        byte brightness,
        int? playerNumber,
        int length)
    {
        return kind switch
        {
            SonyControllerLedKind.DualShock4 when isUsb => BuildDs4UsbReport(red, green, blue, length),
            SonyControllerLedKind.DualShock4 => BuildDs4BluetoothReport(red, green, blue, length),
            SonyControllerLedKind.DualSense when isUsb => BuildDualSenseUsbReport(
                red, green, blue, brightness, playerNumber, length),
            _ => BuildDualSenseBluetoothReport(
                devicePath, red, green, blue, brightness, playerNumber, length)
        };
    }

    private static byte[] BuildDs4UsbReport(byte red, byte green, byte blue, int length)
    {
        byte[] report = new byte[length];
        report[0] = Ds4UsbReportId;
        report[1] = 0xFF;
        report[6] = red;
        report[7] = green;
        report[8] = blue;
        return report;
    }

    private static byte[] BuildDs4BluetoothReport(byte red, byte green, byte blue, int length)
    {
        byte[] report = new byte[length];
        report[0] = Ds4BtReportId;
        report[1] = 0x80;
        report[3] = 0xFF;
        report[8] = red;
        report[9] = green;
        report[10] = blue;
        return report;
    }

    private static byte[] BuildDualSenseUsbReport(
        byte red,
        byte green,
        byte blue,
        byte brightness,
        int? playerNumber,
        int length)
    {
        byte[] report = new byte[length];
        report[0] = DualSenseUsbReportId;
        int o = 1;
        report[o + 1] = 0x14;
        report[o + 38] = 0x01;
        report[o + 41] = 0x01;
        report[o + 42] = brightness;
        report[o + 43] = PlayerLedMask(playerNumber);
        report[o + 44] = red;
        report[o + 45] = green;
        report[o + 46] = blue;
        return report;
    }

    private byte[] BuildDualSenseBluetoothReport(
        string devicePath,
        byte red,
        byte green,
        byte blue,
        byte brightness,
        int? playerNumber,
        int length)
    {
        int reportLength = Math.Max(length, 78);
        byte[] report = new byte[reportLength];
        report[0] = DualSenseBtReportId;
        byte sequence = _bluetoothSequences.AddOrUpdate(
            devicePath,
            _ => (byte)1,
            (_, current) => (byte)((current + 1) & 0x0F));
        report[1] = (byte)(sequence << 4);
        report[2] = DualSenseBtTag;
        int o = 3;
        report[o + 1] = 0x14;
        report[o + 38] = 0x01;
        report[o + 41] = 0x01;
        report[o + 42] = brightness;
        report[o + 43] = PlayerLedMask(playerNumber);
        report[o + 44] = red;
        report[o + 45] = green;
        report[o + 46] = blue;

        if (reportLength >= 78)
        {
            uint crc = ComputeOutputCrc32(report, 74);
            report[74] = (byte)crc;
            report[75] = (byte)(crc >> 8);
            report[76] = (byte)(crc >> 16);
            report[77] = (byte)(crc >> 24);
        }

        return report;
    }

    private static byte PlayerLedMask(int? playerNumber) =>
        playerNumber is >= 1 and <= 5 ? (byte)(1 << (playerNumber.Value - 1)) : (byte)0;

    private static uint ComputeOutputCrc32(byte[] report, int dataLength)
    {
        uint crc = 0xFFFFFFFF;
        crc = CrcStep(crc, DualSenseOutputCrcSeed);
        for (int i = 0; i < dataLength; i++)
        {
            crc = CrcStep(crc, report[i]);
        }

        return ~crc;
    }

    private static uint CrcStep(uint crc, byte value) =>
        (uint)CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);

    private static int[] BuildCrcTable()
    {
        var table = new int[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }

            table[i] = (int)crc;
        }

        return table;
    }

    private static bool WriteOutputReport(SafeFileHandle handle, byte[] report)
    {
        return WriteFile(
            handle,
            report,
            report.Length,
            out int written,
            IntPtr.Zero) && written == report.Length;
    }

    private static bool SetOutputReport(SafeFileHandle handle, byte[] report) =>
        HidD_SetOutputReport(handle, report, report.Length);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        int nNumberOfBytesToWrite,
        out int lpNumberOfBytesWritten,
        IntPtr lpOverlapped);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetAttributes(
        SafeFileHandle hidDeviceObject,
        ref HIDD_ATTRIBUTES attributes);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(
        SafeFileHandle hidDeviceObject,
        out IntPtr ppPreparsedData);

    [DllImport("hid.dll")]
    private static extern bool HidD_FreePreparsedData(IntPtr ppPreparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidP_GetCaps(
        IntPtr preparsedData,
        out HIDP_CAPS capabilities);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetOutputReport(
        SafeFileHandle hidDeviceObject,
        byte[] lpOutputReport,
        int reportLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }
}
