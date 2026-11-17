using System.ComponentModel;
using System.Runtime.InteropServices;
using GDMInputManager.Models;

namespace GDMInputManager.Services;

public sealed record WindowsBluetoothDevice(
    string Name,
    ulong Address,
    bool IsConnected,
    bool IsRemembered,
    bool IsAuthenticated);

/// <summary>
/// Obtiene de Windows los dispositivos Bluetooth clásicos emparejados, disponibles y conectados.
/// </summary>
public sealed class WindowsBluetoothConnectionService
{
    private const int ErrorNoMoreItems = 259;
    private const int ErrorNotReady = 21;
    private const int ErrorServiceNotActive = 1062;
    private const int ErrorDeviceNotConnected = 1167;

    public List<string> GetConnectedDeviceNames()
    {
        return GetDevices(includeAvailableDevices: false)
            .Where(device => device.IsConnected)
            .Select(device => device.Name)
            .ToList();
    }

    public List<WindowsBluetoothDevice> GetDevices(bool includeAvailableDevices)
    {
        var searchParameters = new BluetoothDeviceSearchParameters
        {
            Size = (uint)Marshal.SizeOf<BluetoothDeviceSearchParameters>(),
            ReturnAuthenticated = true,
            ReturnRemembered = true,
            ReturnUnknown = includeAvailableDevices,
            ReturnConnected = true,
            IssueInquiry = includeAvailableDevices,
            TimeoutMultiplier = includeAvailableDevices ? (byte)2 : (byte)0
        };
        var deviceInfo = new BluetoothDeviceInfo
        {
            Size = (uint)Marshal.SizeOf<BluetoothDeviceInfo>(),
            Name = string.Empty
        };

        IntPtr searchHandle = BluetoothFindFirstDevice(ref searchParameters, ref deviceInfo);
        if (searchHandle == IntPtr.Zero || searchHandle == new IntPtr(-1))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorNoMoreItems)
            {
                return new List<WindowsBluetoothDevice>();
            }

            if (IsBluetoothUnavailable(error))
            {
                LoggerService.Instance.Info(
                    $"Se omitió la enumeración Bluetooth porque el adaptador no está disponible (código {error}).");
                return new List<WindowsBluetoothDevice>();
            }

            throw new Win32Exception(error, "Windows no pudo enumerar los dispositivos Bluetooth conectados.");
        }

        try
        {
            var devices = new Dictionary<ulong, WindowsBluetoothDevice>();
            do
            {
                if (!string.IsNullOrWhiteSpace(deviceInfo.Name))
                {
                    devices[deviceInfo.Address] = new WindowsBluetoothDevice(
                        deviceInfo.Name,
                        deviceInfo.Address,
                        deviceInfo.IsConnected,
                        deviceInfo.IsRemembered,
                        deviceInfo.IsAuthenticated);
                }

                deviceInfo = new BluetoothDeviceInfo
                {
                    Size = (uint)Marshal.SizeOf<BluetoothDeviceInfo>(),
                    Name = string.Empty
                };
            }
            while (BluetoothFindNextDevice(searchHandle, ref deviceInfo));

            int lastError = Marshal.GetLastWin32Error();
            if (lastError != ErrorNoMoreItems)
            {
                if (IsBluetoothUnavailable(lastError))
                {
                    LoggerService.Instance.Info(
                        $"La enumeración Bluetooth terminó porque el adaptador dejó de estar disponible (código {lastError}).");
                    return devices.Values.ToList();
                }

                throw new Win32Exception(lastError, "Windows no pudo completar la enumeración Bluetooth.");
            }

            return devices.Values.ToList();
        }
        finally
        {
            if (!BluetoothFindDeviceClose(searchHandle))
            {
                LoggerService.Instance.Warning(
                    $"Windows no pudo cerrar la búsqueda Bluetooth (código {Marshal.GetLastWin32Error()}).");
            }
        }
    }

    public void UnpairDevice(ulong address)
    {
        int result = BluetoothRemoveDevice(ref address);
        if (result != 0)
        {
            throw new Win32Exception(result, "Windows no pudo quitar el emparejamiento del dispositivo Bluetooth.");
        }
    }

    private static bool IsBluetoothUnavailable(int error)
    {
        return error is ErrorNotReady or ErrorServiceNotActive or ErrorDeviceNotConnected;
    }

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstDevice(
        ref BluetoothDeviceSearchParameters searchParameters,
        ref BluetoothDeviceInfo deviceInfo);

    [DllImport("bthprops.cpl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindNextDevice(
        IntPtr findHandle,
        ref BluetoothDeviceInfo deviceInfo);

    [DllImport("bthprops.cpl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindDeviceClose(IntPtr findHandle);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern int BluetoothRemoveDevice(ref ulong address);

    [StructLayout(LayoutKind.Sequential)]
    private struct BluetoothDeviceSearchParameters
    {
        public uint Size;
        [MarshalAs(UnmanagedType.Bool)]
        public bool ReturnAuthenticated;
        [MarshalAs(UnmanagedType.Bool)]
        public bool ReturnRemembered;
        [MarshalAs(UnmanagedType.Bool)]
        public bool ReturnUnknown;
        [MarshalAs(UnmanagedType.Bool)]
        public bool ReturnConnected;
        [MarshalAs(UnmanagedType.Bool)]
        public bool IssueInquiry;
        public byte TimeoutMultiplier;
        public IntPtr Radio;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BluetoothDeviceInfo
    {
        public uint Size;
        public ulong Address;
        public uint ClassOfDevice;
        [MarshalAs(UnmanagedType.Bool)]
        public bool IsConnected;
        [MarshalAs(UnmanagedType.Bool)]
        public bool IsRemembered;
        [MarshalAs(UnmanagedType.Bool)]
        public bool IsAuthenticated;
        private SystemTime LastSeen;
        private SystemTime LastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
        public string Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        public ushort Year;
        public ushort Month;
        public ushort DayOfWeek;
        public ushort Day;
        public ushort Hour;
        public ushort Minute;
        public ushort Second;
        public ushort Milliseconds;
    }
}
