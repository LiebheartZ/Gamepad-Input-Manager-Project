using System.Runtime.InteropServices;
using System.Text;
using GDMInputManager.Models;

namespace GDMInputManager.Services;

/// <summary>
/// Detecta los identificadores de mandos HID que Windows conecta a través de USB.
/// </summary>
public sealed class WindowsUsbConnectionService
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfAllClasses = 0x00000004;
    private const uint ErrorNoMoreItems = 259;
    private const uint CrSuccess = 0;
    private const int MaximumParentDepth = 32;

    public HashSet<(int Vid, int Pid)> GetConnectedUsbGamepadIds()
    {
        IntPtr deviceInfoSet = SetupDiGetClassDevs(
            IntPtr.Zero,
            null,
            IntPtr.Zero,
            DigcfPresent | DigcfAllClasses);

        if (deviceInfoSet == new IntPtr(-1))
        {
            throw new InvalidOperationException(
                $"Windows no pudo enumerar los dispositivos Plug and Play (código {Marshal.GetLastWin32Error()}).");
        }

        try
        {
            var deviceIds = new HashSet<(int Vid, int Pid)>();

            for (uint index = 0; ; index++)
            {
                var deviceInfo = new DeviceInfoData
                {
                    Size = Marshal.SizeOf<DeviceInfoData>()
                };

                if (!SetupDiEnumDeviceInfo(deviceInfoSet, index, ref deviceInfo))
                {
                    int error = Marshal.GetLastWin32Error();
                    if ((uint)error == ErrorNoMoreItems)
                    {
                        break;
                    }

                    throw new InvalidOperationException(
                        $"Windows no pudo leer el dispositivo Plug and Play {index} (código {error}).");
                }

                string instanceId = GetDeviceInstanceId(deviceInfoSet, ref deviceInfo);
                if (!TryGetHardwareIds(instanceId, out int vid, out int pid))
                {
                    continue;
                }

                if (HasUsbParent(deviceInfo.DevInst))
                {
                    deviceIds.Add((vid, pid));
                }
            }

            return deviceIds;
        }
        finally
        {
            if (!SetupDiDestroyDeviceInfoList(deviceInfoSet))
            {
                LoggerService.Instance.Warning(
                    $"Windows no pudo cerrar la enumeración Plug and Play (código {Marshal.GetLastWin32Error()}).");
            }
        }
    }

    private static string GetDeviceInstanceId(IntPtr deviceInfoSet, ref DeviceInfoData deviceInfo)
    {
        var buffer = new StringBuilder(512);
        if (!SetupDiGetDeviceInstanceId(deviceInfoSet, ref deviceInfo, buffer, buffer.Capacity, out _))
        {
            int error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"Windows no pudo leer el identificador de un dispositivo Plug and Play (código {error}).");
        }

        return buffer.ToString();
    }

    private static bool TryGetHardwareIds(string instanceId, out int vid, out int pid)
    {
        vid = 0;
        pid = 0;

        int vidIndex = instanceId.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        int pidIndex = instanceId.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
        if (vidIndex < 0 || pidIndex < 0 ||
            instanceId.Length < vidIndex + 8 || instanceId.Length < pidIndex + 8)
        {
            return false;
        }

        return int.TryParse(instanceId.AsSpan(vidIndex + 4, 4), System.Globalization.NumberStyles.HexNumber,
                   System.Globalization.CultureInfo.InvariantCulture, out vid) &&
               int.TryParse(instanceId.AsSpan(pidIndex + 4, 4), System.Globalization.NumberStyles.HexNumber,
                   System.Globalization.CultureInfo.InvariantCulture, out pid);
    }

    private static bool HasUsbParent(uint devInst)
    {
        uint currentDevInst = devInst;
        for (int depth = 0; depth < MaximumParentDepth; depth++)
        {
            if (CM_Get_Parent(out uint parentDevInst, currentDevInst, 0) != CrSuccess)
            {
                return false;
            }

            var parentId = new StringBuilder(512);
            if (CM_Get_Device_ID(parentDevInst, parentId, parentId.Capacity, 0) != CrSuccess)
            {
                return false;
            }

            if (parentId.ToString().StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            currentDevInst = parentDevInst;
        }

        return false;
    }

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        IntPtr classGuid,
        string? enumerator,
        IntPtr parentWindow,
        uint flags);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiEnumDeviceInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet,
        uint memberIndex,
        ref DeviceInfoData deviceInfoData);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceId(
        IntPtr deviceInfoSet,
        ref DeviceInfoData deviceInfoData,
        StringBuilder deviceInstanceId,
        int deviceInstanceIdSize,
        out int requiredSize);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiDestroyDeviceInfoList", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Parent", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Parent(out uint parentDevInst, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID(uint devInst, StringBuilder deviceId, int bufferLength, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoData
    {
        public int Size;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }
}
