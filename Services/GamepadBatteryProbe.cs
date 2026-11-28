using System.Runtime.InteropServices;
using System.Text;
using GDM.Models;

namespace GDM.Services;

/// <summary>
/// Resultado del sondeo de batería: nivel semántico, porcentaje y carga.
/// </summary>
public sealed record BatteryReading(
    GamepadBatteryLevel Level,
    int? Percent,
    bool Charging,
    bool Wired,
    bool HasData);

/// <summary>
/// Lee la batería por el método nativo más fiable según el dispositivo:
/// XInput para mandos Xbox, HID de DualShock 4 para Sony, y el nivel SDL
/// como respaldo para el resto (DualSense, genéricos).
/// </summary>
public static class GamepadBatteryProbe
{
    private const int XInputDevTypeGamepad = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputBatteryInformation
    {
        public byte BatteryType;
        public uint BatteryLevel;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation", CallingConvention = CallingConvention.Winapi)]
    private static extern uint XInputGetBatteryInformationNative(
        uint dwUserIndex,
        byte devType,
        out XInputBatteryInformation pBatteryInformation);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetInputReport(IntPtr hidDeviceObject, byte[] lpReportBuffer, uint reportBufferLength);

    private const uint FileGenericRead = 0x80000000;
    private const uint FileGenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private static readonly IntPtr InvalidHandleValue = unchecked((IntPtr)(-1));

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Mapea el valor crudo de SDL_JoystickCurrentPowerLevel al enum del modelo.
    /// SDL_JOYSTICK_POWER_UNKNOWN=-1, EMPTY=0, LOW=1, MEDIUM=2, FULL=3, WIRED=4.
    /// (SDL no tiene estado de carga: WIRED lo cubre.)
    /// </summary>
    public static GamepadBatteryLevel MapSdlPowerLevel(int raw) => raw switch
    {
        -1 => GamepadBatteryLevel.Unknown,
        0 => GamepadBatteryLevel.Empty,
        1 => GamepadBatteryLevel.Low,
        2 => GamepadBatteryLevel.Medium,
        3 => GamepadBatteryLevel.Full,
        4 => GamepadBatteryLevel.Wired,
        _ => GamepadBatteryLevel.Unknown
    };

    /// <summary>
    /// Sonda la batería de un dispositivo abierto. <paramref name="sdlPowerLevel"/>
    /// es el nivel crudo actual de SDL; se combina con las lecturas nativas.
    /// <paramref name="xinputUserIndex"/> es el índice de jugador si aplica (-1 si no).
    /// </summary>
    public static BatteryReading Probe(
        IntPtr joystick,
        ConnectedGamepad device,
        int xinputUserIndex)
    {
        GamepadBatteryLevel sdlLevel = MapSdlPowerLevel(ReadSdlPowerLevel(joystick));

        // Xbox por Bluetooth: SDL3 es la fuente fiable (estado GIP del mando,
        // niveles 1/25/50/100 + carga). XInput por BT es intermitente — a
        // veces devuelve porcentaje fino y a veces «sin batería» — así que
        // consultar XInput primero hacía alternar el valor mostrado entre
        // sondeos. Se prefiere SDL3 y XInput queda como último recurso.
        bool isBluetoothXbox =
            device.VendorId == 0x045E &&
            device.ConnectionTransport == GamepadConnectionTransport.Bluetooth;
        if (isBluetoothXbox)
        {
            BatteryReading? btReading = TryReadSdl3PowerInfo(joystick);
            if (btReading is { } btResult)
            {
                return btResult;
            }
        }

        // Xbox por XInput: porcentaje exacto (0-255) y tipo de batería/cable.
        if (device.VendorId == 0x045E && xinputUserIndex >= 0 &&
            XInputGetBatteryInformationNative((uint)xinputUserIndex, XInputDevTypeGamepad, out XInputBatteryInformation info) == 0)
        {
            int percent = (int)Math.Clamp(info.BatteryLevel * 100u / 255u, 0, 100);
            bool disconnected = info.BatteryType == 0; // XINPUT_BATTERY_TYPE_DISCONNECTED
            bool wired = info.BatteryType == 1;        // XINPUT_BATTERY_TYPE_WIRED
            bool hasBattery = info.BatteryType is 2 or 3 or 4 or 5; // ALKALINE/NIMH/UNKNOWN/LITHIUM

            // Por Bluetooth, XInput puede devolver «sin batería» o 0 % aunque
            // el mando tenga carga: en ese caso se prefiere SDL3, que sí
            // reporta el estado GIP real (niveles y carga).
            bool xinputUnreliable =
                device.ConnectionTransport == GamepadConnectionTransport.Bluetooth &&
                (disconnected || (!wired && info.BatteryLevel == 0));
            if (xinputUnreliable)
            {
                // Por Bluetooth ya se probó SDL3 al inicio de Probe.
                return new BatteryReading(
                    sdlLevel,
                    null,
                    false,
                    sdlLevel == GamepadBatteryLevel.Wired,
                    sdlLevel != GamepadBatteryLevel.Unknown);
            }

            if (disconnected)
            {
                return new BatteryReading(GamepadBatteryLevel.Empty, 0, false, true, true);
            }

            GamepadBatteryLevel level = wired
                ? GamepadBatteryLevel.Wired
                : hasBattery || sdlLevel == GamepadBatteryLevel.Unknown
                    ? PercentToLevel(percent)
                    : sdlLevel;

            // Con cable, XInput sigue dando nivel; mostramos el estado cableado.
            return new BatteryReading(level, wired ? null : percent, false, wired, true);
        }

        // DualShock 4 por HID: el byte 30 del reporte de estado contiene el
        // nivel (nibble bajo 0-10) y el bit 0x10 de "cargando/con cable".
        if (device.VendorId == 0x054C && IsDs4ProductId(device.ProductId) &&
            TryReadDs4Battery(device.DevicePath, device.ConnectionTransport, out int ds4Percent, out bool ds4Charging))
        {
            GamepadBatteryLevel level = ds4Charging
                ? GamepadBatteryLevel.Charging
                : PercentToLevel(ds4Percent);
            return new BatteryReading(level, ds4Percent, ds4Charging, false, true);
        }

        BatteryReading? sdl3 = TryReadSdl3PowerInfo(joystick);
        if (sdl3 is { } sdl3Reading)
        {
            return sdl3Reading;
        }

        return new BatteryReading(
            sdlLevel,
            null,
            false,
            sdlLevel == GamepadBatteryLevel.Wired,
            sdlLevel != GamepadBatteryLevel.Unknown);
    }

    /// <summary>
    /// Lee la información de energía vía SDL3 directamente. El handle SDL2 es
    /// el mismo puntero SDL_Joystick de SDL3 (SDL2-compat no envuelve el
    /// objeto), así que se puede llamar a SDL_GetJoystickPowerInfo, que
    /// devuelve porcentaje real donde SDL2 solo expone un nivel grueso —
    /// p. ej. mandos Xbox por Bluetooth vía GIP (1/25/50/100 + carga).
    /// </summary>
    private static BatteryReading? TryReadSdl3PowerInfo(IntPtr joystick)
    {
        try
        {
            int percent = -1;
            int state = Sdl3GetJoystickPowerInfo(joystick, out percent);
            // SDL_PowerState: ERROR=-1, UNKNOWN=0, ON_BATTERY=1,
            // NO_BATTERY=2, CHARGING=3, CHARGED=4.
            return state switch
            {
                2 => new BatteryReading(GamepadBatteryLevel.Wired, null, false, true, true),
                3 or 4 => new BatteryReading(
                    GamepadBatteryLevel.Charging,
                    percent >= 0 ? percent : null,
                    true,
                    false,
                    true),
                1 when percent >= 0 => new BatteryReading(
                    PercentToLevel(percent),
                    percent,
                    false,
                    false,
                    true),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool IsDs4ProductId(ushort? productId) =>
        productId is 0x05C4 or 0x09CC or 0x0BA0;

    // Los niveles GIP del mando reportan 25% para «batería baja»: el corte en
    // 30% hace que ese valor semántico caiga en Low y dispare la alerta.
    private static GamepadBatteryLevel PercentToLevel(int percent) => percent switch
    {
        <= 5 => GamepadBatteryLevel.Empty,
        <= 30 => GamepadBatteryLevel.Low,
        <= 70 => GamepadBatteryLevel.Medium,
        _ => GamepadBatteryLevel.Full
    };

    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_JoystickCurrentPowerLevel")]
    private static extern int SdlCurrentPowerLevel(IntPtr joystick);

    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_GetJoystickPowerInfo")]
    private static extern int Sdl3GetJoystickPowerInfo(IntPtr joystick, out int percent);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState", CallingConvention = CallingConvention.Winapi)]
    private static extern uint XInputGetStateNative(uint dwUserIndex, out XInputState state);

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    /// <summary>Índices de jugador XInput con mando conectado (0-3).</summary>
    public static List<int> GetConnectedXInputUserIndices()
    {
        var slots = new List<int>(4);
        for (uint i = 0; i < 4; i++)
        {
            try
            {
                if (XInputGetStateNative(i, out _) == 0)
                {
                    slots.Add((int)i);
                }
            }
            catch { }
        }
        return slots;
    }

    /// <summary>
    /// Devuelve el slot XInput del único mando inalámbrico con batería
    /// reportando datos (tipos ALKALINE/NIMH/UNKNOWN/LITHIUM). Los mandos
    /// virtuales y los conectados por cable reportan WIRED/DISCONNECTED y
    /// quedan excluidos, así que funciona aunque haya salidas virtuales
    /// activas ocupando slots XInput. -1 si no hay exactamente uno.
    /// </summary>
    public static int FindSingleBatteryXInputSlot()
    {
        int found = -1;
        int candidates = 0;
        for (uint i = 0; i < 4; i++)
        {
            try
            {
                if (XInputGetStateNative(i, out _) != 0 ||
                    XInputGetBatteryInformationNative(i, XInputDevTypeGamepad, out XInputBatteryInformation info) != 0)
                {
                    continue;
                }

                if (info.BatteryType is >= 2 and <= 5)
                {
                    found = (int)i;
                    candidates++;
                }
            }
            catch { }
        }

        return candidates == 1 ? found : -1;
    }
    private static int ReadSdlPowerLevel(IntPtr joystick)
    {
        try
        {
            return SdlCurrentPowerLevel(joystick);
        }
        catch
        {
            return -1;
        }
    }

    private static bool TryReadDs4Battery(string? devicePath, GamepadConnectionTransport transport, out int percent, out bool charging)
    {
        percent = 0;
        charging = false;
        if (string.IsNullOrWhiteSpace(devicePath))
        {
            return false;
        }

        IntPtr handle = CreateFileW(
            devicePath,
            FileGenericRead | FileGenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (handle == InvalidHandleValue || handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            // El reporte de entrada del DS4 tiene 78 bytes (ID + datos).
            var buffer = new byte[78];
            if (!HidD_GetInputReport(handle, buffer, (uint)buffer.Length))
            {
                return false;
            }

            byte reportId = buffer[0];
            bool usbReport = reportId == 0x01;
            bool btReport = reportId is >= 17 and <= 25;
            if (buffer.Length < 31 || (!usbReport && !(transport == GamepadConnectionTransport.Bluetooth && btReport)))
            {
                return false;
            }

            byte raw = buffer[30];
            int level = raw & 0x0F;
            if (level > 10)
            {
                return false;
            }

            percent = level * 10;
            charging = (raw & 0x10) != 0;
            return true;
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}

/// <summary>
/// Resuelve el transporte real (USB/Bluetooth/desconocido) de un dispositivo
/// siguiendo la cadena de padres del devnode PnP: las rutas DevicePath de SDL
/// pueden venir como "hidclass" sin VID/PID ni marcadores de bus.
/// </summary>
public static class DeviceTransportResolver
{
    private const uint CmLocateDevNodeNormal = 0x00000000;
    private const uint CrSuccess = 0;
    private const int MaxAncestorDepth = 16;
    private const uint DigcfAllClasses = 0x00000004;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const int ErrorInsufficientBuffer = 122;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out IntPtr pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out IntPtr pdnDevInst, IntPtr devInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_IDW(IntPtr devInst, IntPtr buffer, int bufferLen, int ulFlags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(
        IntPtr classGuid,
        IntPtr enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiOpenDeviceInterfaceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiOpenDeviceInterfaceW(
        IntPtr deviceInfoSet,
        string devicePath,
        uint flags,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailSizeW(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        IntPtr deviceInfoData);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        ref SpDevinfoData deviceInfoData);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceIdW(
        IntPtr deviceInfoSet,
        ref SpDevinfoData deviceInfoData,
        StringBuilder deviceInstanceId,
        int deviceInstanceIdSize,
        out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        public int cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    /// <summary>
    /// Determina si el dispositivo está conectado por Bluetooth, USB o desconocido.
    /// Nunca lanza: cualquier fallo devuelve Unknown para no romper la enumeración.
    /// </summary>
    public static GamepadConnectionTransport Resolve(string? devicePath, int vendorId = 0, int productId = 0)
    {
        try
        {
            GamepadConnectionTransport quick = DetectFromPath(devicePath);
            if (quick != GamepadConnectionTransport.Unknown)
            {
                return quick;
            }

            string? instanceId = ResolveDeviceInstanceId(devicePath);
            if (!string.IsNullOrEmpty(instanceId))
            {
                GamepadConnectionTransport detected = DetectFromAncestorChain(instanceId);
                if (detected != GamepadConnectionTransport.Unknown)
                {
                    return detected;
                }
            }
        }
        catch
        {
            // La detección de transporte nunca debe interrumpir la enumeración.
        }

        return (vendorId, productId) switch
        {
            (0x054C, 0x05C4 or 0x09CC or 0x0BA0) => GamepadConnectionTransport.Bluetooth,
            _ => GamepadConnectionTransport.Unknown
        };
    }

    private static GamepadConnectionTransport DetectFromAncestorChain(string instanceId)
    {
        string? current = instanceId;
        for (int depth = 0; depth < MaxAncestorDepth && !string.IsNullOrEmpty(current); depth++)
        {
            GamepadConnectionTransport detected = DetectFromPath(current);
            if (detected != GamepadConnectionTransport.Unknown)
            {
                return detected;
            }

            string? parent = GetDeviceInstanceParentId(current);
            if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }

        return GamepadConnectionTransport.Unknown;
    }

    private static string? GetDeviceInstanceParentId(string instanceId)
    {
        try
        {
            if (CM_Locate_DevNodeW(out IntPtr devInst, instanceId, CmLocateDevNodeNormal) != CrSuccess)
            {
                return null;
            }

            if (CM_Get_Parent(out IntPtr parent, devInst, 0) != CrSuccess)
            {
                return null;
            }

            return GetDeviceInstanceId(parent);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetDeviceInstanceId(IntPtr devInst)
    {
        var buffer = new char[1024];
        GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            uint result = CM_Get_Device_IDW(devInst, handle.AddrOfPinnedObject(), buffer.Length, 0);
            return result == CrSuccess ? new string(buffer).TrimEnd('\0') : null;
        }
        finally
        {
            handle.Free();
        }
    }

    private static GamepadConnectionTransport DetectFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return GamepadConnectionTransport.Unknown;
        }

        if (path.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("BTHLE", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("BTHPORT", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("00001124-0000-1000-8000-00805F9B34FB", StringComparison.OrdinalIgnoreCase))
        {
            return GamepadConnectionTransport.Bluetooth;
        }

        if (path.Contains("USB#", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("USB\\", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("VID_", StringComparison.OrdinalIgnoreCase))
        {
            return GamepadConnectionTransport.Usb;
        }

        return GamepadConnectionTransport.Unknown;
    }

    /// <summary>
    /// Relaciona la ruta DevicePath de SDL con el Device Instance ID de PnP
    /// usando el mismo patrón que HidHideService: abrir la interfaz por ruta.
    /// </summary>
    private static string? ResolveDeviceInstanceId(string? devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
        {
            return null;
        }

        IntPtr infoSet = SetupDiGetClassDevsW(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, DigcfAllClasses | DigcfDeviceInterface);
        if (infoSet == IntPtr.Zero || infoSet == new IntPtr(-1))
        {
            return null;
        }

        try
        {
            var interfaceData = new SpDeviceInterfaceData { cbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
            if (!SetupDiOpenDeviceInterfaceW(infoSet, devicePath.TrimEnd('\0'), 0, ref interfaceData))
            {
                return null;
            }

            if (!SetupDiGetDeviceInterfaceDetailSizeW(infoSet, ref interfaceData, IntPtr.Zero, 0, out uint requiredSize, IntPtr.Zero) ||
                requiredSize == 0)
            {
                return null;
            }

            IntPtr detailBuffer = Marshal.AllocHGlobal(checked((int)requiredSize));
            try
            {
                Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 6);
                var devInfo = new SpDevinfoData { cbSize = Marshal.SizeOf<SpDevinfoData>() };
                if (!SetupDiGetDeviceInterfaceDetailW(infoSet, ref interfaceData, detailBuffer, requiredSize, out _, ref devInfo))
                {
                    return null;
                }

                var instanceId = new StringBuilder(1024);
                if (!SetupDiGetDeviceInstanceIdW(infoSet, ref devInfo, instanceId, instanceId.Capacity, out _))
                {
                    return null;
                }

                return instanceId.ToString();
            }
            finally
            {
                Marshal.FreeHGlobal(detailBuffer);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(infoSet);
        }
    }
}