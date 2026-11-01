using System.Runtime.InteropServices;

namespace Proyecto_GDM_C_;

internal static class ControllerDetector
{
    private const uint JoySuccess = 0;

    [DllImport("winmm.dll", EntryPoint = "joyGetNumDevs")]
    private static extern uint JoyGetNumDevs();

    [DllImport("winmm.dll", EntryPoint = "joyGetDevCapsW", CharSet = CharSet.Unicode)]
    private static extern uint JoyGetDevCaps(UIntPtr deviceId, out JoyCaps capabilities, uint size);

    [DllImport("winmm.dll", EntryPoint = "joyGetPos")]
    private static extern uint JoyGetPos(uint deviceId, out JoyInfo position);

    internal static List<ControllerInfo> GetConnectedControllers()
    {
        var controllers = new List<ControllerInfo>();
        var capabilitiesSize = (uint)Marshal.SizeOf<JoyCaps>();

        for (uint deviceId = 0; deviceId < JoyGetNumDevs(); deviceId++)
        {
            if (JoyGetDevCaps(new UIntPtr(deviceId), out var capabilities, capabilitiesSize) != JoySuccess)
            {
                continue;
            }

            if (JoyGetPos(deviceId, out _) != JoySuccess)
            {
                continue;
            }

            controllers.Add(new ControllerInfo(
                deviceId,
                string.IsNullOrWhiteSpace(capabilities.ProductName)
                    ? $"Mando {deviceId + 1}"
                    : capabilities.ProductName,
                capabilities.AxisCount,
                capabilities.ButtonCount));
        }

        return controllers;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct JoyCaps
    {
        public ushort ManufacturerId;
        public ushort ProductId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string ProductName;

        public uint XMin;
        public uint XMax;
        public uint YMin;
        public uint YMax;
        public uint ZMin;
        public uint ZMax;
        public uint ButtonCount;
        public uint PeriodMin;
        public uint PeriodMax;
        public uint RMin;
        public uint RMax;
        public uint UMin;
        public uint UMax;
        public uint VMin;
        public uint VMax;
        public uint Capabilities;
        public uint MaxAxes;
        public uint AxisCount;
        public uint MaxButtons;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string RegistryKey;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string OemVxd;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JoyInfo
    {
        public uint X;
        public uint Y;
        public uint Z;
        public uint Buttons;
    }
}

internal sealed record ControllerInfo(uint DeviceId, string Name, uint AxisCount, uint ButtonCount);
