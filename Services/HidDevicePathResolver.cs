using System.Runtime.InteropServices;
using System.Text;

namespace GDM.Services;

/// <summary>
/// Cuando SDL reporta una ruta que no es una interfaz de dispositivo
/// (por ejemplo "XInput#0" para mandos Xbox gestionados por el driver XInput
/// de SDL3), resuelve la ruta HID real del mismo dispositivo físico
/// enumerando las interfaces HID presentes y filtrando por VID/PID.
/// Así DevicePath sigue siendo una ruta \\?\HID#... utilizable por HidHide,
/// el reinicio PnP y la sonda de batería.
/// </summary>
internal static class HidDevicePathResolver
{
    private const uint DigcfPresent = 0x02;
    private const uint DigcfDeviceInterface = 0x10;

    private static readonly Guid HidClassGuid = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    /// <summary>
    /// Devuelve la primera ruta de interfaz HID que contiene el VID/PID dado,
    /// o null si no hay ninguna o la enumeración falla.
    /// </summary>
    public static string? ResolveByVidPid(int vendorId, int productId)
    {
        if (vendorId <= 0 || vendorId > 0xFFFF || productId <= 0 || productId > 0xFFFF)
        {
            return null;
        }

        string vidToken = $"VID_{vendorId:X4}";
        string pidToken = $"PID_{productId:X4}";
        var matches = new List<string>();

        IntPtr classGuidPtr = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.StructureToPtr(HidClassGuid, classGuidPtr, false);
            IntPtr infoSet = NativeMethods.SetupDiGetClassDevs(
                classGuidPtr,
                IntPtr.Zero,
                IntPtr.Zero,
                DigcfDeviceInterface | DigcfPresent);
            if (infoSet == IntPtr.Zero || infoSet == new IntPtr(-1))
            {
                return null;
            }

            try
            {
                var interfaceData = new SpDeviceInterfaceData
                {
                    cbSize = Marshal.SizeOf<SpDeviceInterfaceData>()
                };

                for (uint index = 0; ; index++)
                {
                    if (!NativeMethods.SetupDiEnumDeviceInterfaces(
                            infoSet, IntPtr.Zero, classGuidPtr, index, ref interfaceData))
                    {
                        break;
                    }

                    string? path = GetInterfacePath(infoSet, ref interfaceData);
                    if (path is not null &&
                        path.Contains(vidToken, StringComparison.OrdinalIgnoreCase) &&
                        path.Contains(pidToken, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(path);
                    }
                }
            }
            finally
            {
                NativeMethods.SetupDiDestroyDeviceInfoList(infoSet);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(classGuidPtr);
        }

        if (matches.Count == 0)
        {
            return null;
        }

        // Preferir la colección de gamepad (IG_00) cuando el dispositivo
        // expone varias interfaces HID con el mismo VID/PID.
        return matches
            .OrderBy(path => path.Contains("&IG_00", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    private static string? GetInterfacePath(IntPtr infoSet, ref SpDeviceInterfaceData interfaceData)
    {
        _ = NativeMethods.SetupDiGetDeviceInterfaceDetail(
            infoSet,
            ref interfaceData,
            IntPtr.Zero,
            0,
            out uint requiredSize,
            IntPtr.Zero);
        if (requiredSize == 0)
        {
            return null;
        }

        IntPtr detailBuffer = Marshal.AllocHGlobal(checked((int)requiredSize));
        try
        {
            Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 6);
            if (!NativeMethods.SetupDiGetDeviceInterfaceDetail(
                    infoSet,
                    ref interfaceData,
                    detailBuffer,
                    requiredSize,
                    out _,
                    IntPtr.Zero))
            {
                return null;
            }

            // SP_DEVICE_INTERFACE_DETAIL_DATA: DWORD cbSize seguido de la
            // ruta WCHAR. Probamos los dos offsets observados (4 y 8) y
            // aceptamos el que devuelve una ruta \\?\ válida.
            foreach (int offset in new[] { 4, IntPtr.Size })
            {
                string? candidate = Marshal.PtrToStringUni(
                    IntPtr.Add(detailBuffer, offset));
                if (candidate is not null &&
                    candidate.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
                {
                    return candidate.TrimEnd('\0');
                }
            }

            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(detailBuffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    private static class NativeMethods
    {
        [DllImport("setupapi.dll", SetLastError = true)]
        internal static extern IntPtr SetupDiGetClassDevs(
            IntPtr classGuid,
            IntPtr enumerator,
            IntPtr parent,
            uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr deviceInfoSet,
            IntPtr deviceInfoData,
            IntPtr interfaceClassGuid,
            uint memberIndex,
            ref SpDeviceInterfaceData deviceInterfaceData);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr deviceInfoSet,
            ref SpDeviceInterfaceData deviceInterfaceData,
            IntPtr deviceInterfaceDetailData,
            uint deviceInterfaceDetailDataSize,
            out uint requiredSize,
            IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
    }
}
