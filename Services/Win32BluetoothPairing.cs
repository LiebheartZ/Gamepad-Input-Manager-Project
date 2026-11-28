using System.Runtime.InteropServices;
using Windows.Devices.Enumeration;

namespace GDM.Services;

/// <summary>
/// Emparejamiento por la API Win32 clásica de Bluetooth (bthprops.cpl): la
/// misma vía que usa el Panel de control de Windows. Se usa como último
/// recurso cuando DeviceInformation.Pairing (WinRT) es rechazado por el
/// sistema — ocurre en algunos equipos donde PairAsync falla al instante en
/// todos los niveles de protección, tanto elevado como no elevado.
/// </summary>
internal static class Win32BluetoothPairing
{
    private const uint ERROR_SUCCESS = 0;
    private const uint ERROR_CANCELLED = 1223;
    private const int BLUETOOTH_MAX_NAME_SIZE = 248;

    // AUTHENTICATION_REQUIREMENTS (bluetoothapis.h)
    private const uint MITMProtectionNotRequired = 0x00;
    private const uint MITMProtectionNotRequiredBonding = 0x02;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEMTIME
    {
        public ushort wYear;
        public ushort wMonth;
        public ushort wDayOfWeek;
        public ushort wDay;
        public ushort wHour;
        public ushort wMinute;
        public ushort wSecond;
        public ushort wMilliseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLUETOOTH_DEVICE_SEARCH_PARAMS
    {
        public uint dwSize;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnAuthenticated;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnRemembered;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnUnknown;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnConnected;
        [MarshalAs(UnmanagedType.Bool)] public bool fIssueInquiry;
        public byte cTimeoutMultiplier;
        public IntPtr hRadio;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BLUETOOTH_DEVICE_INFO
    {
        public uint dwSize;
        public ulong Address;
        public uint ulClassofDevice;
        [MarshalAs(UnmanagedType.Bool)] public bool fConnected;
        [MarshalAs(UnmanagedType.Bool)] public bool fRemembered;
        [MarshalAs(UnmanagedType.Bool)] public bool fAuthenticated;
        public SYSTEMTIME stLastSeen;
        public SYSTEMTIME stLastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = BLUETOOTH_MAX_NAME_SIZE)]
        public string szName;
    }

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstDevice(
        ref BLUETOOTH_DEVICE_SEARCH_PARAMS pbtsp,
        ref BLUETOOTH_DEVICE_INFO pbtdi);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern bool BluetoothFindNextDevice(
        IntPtr hFind,
        ref BLUETOOTH_DEVICE_INFO pbtdi);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern bool BluetoothFindDeviceClose(IntPtr hFind);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern uint BluetoothAuthenticateDeviceEx(
        IntPtr hwndParent,
        IntPtr hRadio,
        ref BLUETOOTH_DEVICE_INFO pbtdi,
        IntPtr pbtOobData,
        uint authenticationRequirement);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern uint BluetoothRegisterForAuthenticationEx(
        ref BLUETOOTH_DEVICE_INFO pbtdi,
        out IntPtr phRegHandle,
        AuthCallbackDelegate pfnCallback,
        IntPtr pvParam);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern uint BluetoothUnregisterAuthentication(IntPtr hRegHandle);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern uint BluetoothSendAuthenticationResponseEx(
        IntPtr hRadio,
        ref BLUETOOTH_AUTHENTICATE_RESPONSE pAuthResponse);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstRadio(
        ref BLUETOOTH_FIND_RADIO_PARAMS pbtfrp);

    [DllImport("bthprops.cpl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindRadioClose(IntPtr hFind);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern uint BluetoothEnumerateInstalledServices(
        IntPtr hRadio,
        ref BLUETOOTH_DEVICE_INFO pbtdi,
        out uint pcServices,
        IntPtr pGuidServices);

    [StructLayout(LayoutKind.Sequential)]
    private struct BLUETOOTH_FIND_RADIO_PARAMS
    {
        public uint dwSize;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int AuthCallbackDelegate(IntPtr pvParam, IntPtr pParams);

    // Se mantiene una referencia estática para que el GC no recolecte el
    // delegado mientras Windows lo tiene registrado.
    private static readonly AuthCallbackDelegate AuthenticationCallback = OnAuthenticationRequested;

    private static Action<string>? _log;

    [StructLayout(LayoutKind.Sequential)]
    private struct BLUETOOTH_AUTHENTICATION_CALLBACK_PARAMS
    {
        public BLUETOOTH_DEVICE_INFO deviceInfo;
        public int authenticationMethod;
        public int ioCapability;
        public int authenticationRequirements;
        public uint numericValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLUETOOTH_AUTHENTICATE_RESPONSE
    {
        public ulong bthAddressRemote;
        public int authMethod;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] authData;
        public byte negativeResponse;
    }

    /// <summary>
    /// Responde las solicitudes de autenticación del stack Bluetooth sin
    /// mostrar UI (Just Works / Numeric Comparison): sin este callback la
    /// autenticación intenta abrir el asistente de Windows, que no puede
    /// aparecer desde el proceso helper oculto y termina en timeout.
    /// </summary>
    private static int OnAuthenticationRequested(IntPtr pvParam, IntPtr pParams)
    {
        try
        {
            var callbackParams = Marshal.PtrToStructure<BLUETOOTH_AUTHENTICATION_CALLBACK_PARAMS>(pParams);
            _log?.Invoke($"Win32: solicitud de autenticación method={callbackParams.authenticationMethod}, io={callbackParams.ioCapability}, valor={callbackParams.numericValue}; aceptando.");

            var response = new BLUETOOTH_AUTHENTICATE_RESPONSE
            {
                bthAddressRemote = callbackParams.deviceInfo.Address,
                authMethod = callbackParams.authenticationMethod,
                authData = new byte[32],
                negativeResponse = 0
            };

            // En Numeric Comparison (Just Works) y Passkey el primer ULONG de
            // la unión debe ser el valor que entregó Windows; responder con
            // cero completa la autenticación pero deja una clave de enlace
            // defectuosa y el dispositivo se cae al conectar.
            byte[] valueBytes = BitConverter.GetBytes(callbackParams.numericValue);
            Buffer.BlockCopy(valueBytes, 0, response.authData, 0, 4);

            uint result = BluetoothSendAuthenticationResponseEx(IntPtr.Zero, ref response);
            _log?.Invoke($"Win32: respuesta de autenticación → {result}");
            return 1;
        }
        catch (Exception exception)
        {
            _log?.Invoke($"Win32: error en callback de autenticación: {exception.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Fuerza la conexión SDP con el dispositivo ya autenticado para que
    /// Windows descubra sus servicios. Sin este paso el vínculo queda creado
    /// pero la función HID nunca se instala y el mando se cae al conectar.
    /// </summary>
    private static void EnumerateDeviceServices(BLUETOOTH_DEVICE_INFO info, Action<string> log)
    {
        try
        {
            var radioParams = new BLUETOOTH_FIND_RADIO_PARAMS
            {
                dwSize = (uint)Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>()
            };
            IntPtr radio = BluetoothFindFirstRadio(ref radioParams);
            if (radio == IntPtr.Zero || radio == new IntPtr(-1))
            {
                log($"Win32: BluetoothFindFirstRadio falló (error={Marshal.GetLastWin32Error()}).");
                return;
            }

            try
            {
                var device = info;
                uint result = BluetoothEnumerateInstalledServices(
                    radio, ref device, out uint count, IntPtr.Zero);
                log($"Win32: BluetoothEnumerateInstalledServices → {result}, servicios={count}");
            }
            finally
            {
                BluetoothFindRadioClose(radio);
            }
        }
        catch (Exception exception)
        {
            log($"Win32: enumeración de servicios lanzó: {exception.Message}");
        }
    }

    /// <summary>
    /// Intenta emparejar el dispositivo con la dirección Bluetooth indicada
    /// mediante la API Win32. Devuelve el estado equivalente de WinRT, o null
    /// si el dispositivo no apareció en la búsqueda.
    /// </summary>
    public static DevicePairingResultStatus? TryAuthenticate(
        string macAddress, Action<string> log)
    {
        byte[]? bytes = ParseMac(macAddress);
        if (bytes is null)
        {
            log($"Win32: no se pudo interpretar la dirección «{macAddress}».");
            return null;
        }

        // Windows guarda la dirección como entero de 48 bits; el orden de los
        // octetos varía según la capa, así que se prueban ambas interpretaciones.
        ulong bigEndian = 0;
        ulong littleEndian = 0;
        for (int i = 0; i < 6; i++)
        {
            bigEndian = (bigEndian << 8) | bytes[i];
            littleEndian |= (ulong)bytes[i] << (8 * i);
        }

        // Primero se buscan los dispositivos ya recordados (instantáneo) y,
        // si no aparece, se lanza una búsqueda activa (inquiry) por si el
        // mando está anunciándose en modo emparejamiento.
        foreach (bool issueInquiry in new[] { false, true })
        {
            var searchParams = new BLUETOOTH_DEVICE_SEARCH_PARAMS
            {
                dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_SEARCH_PARAMS>(),
                fReturnAuthenticated = true,
                fReturnRemembered = true,
                fReturnUnknown = true,
                fReturnConnected = true,
                fIssueInquiry = issueInquiry,
                cTimeoutMultiplier = issueInquiry ? (byte)2 : (byte)0,
                hRadio = IntPtr.Zero
            };

            var info = new BLUETOOTH_DEVICE_INFO();
            info.dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>();

            IntPtr find = BluetoothFindFirstDevice(ref searchParams, ref info);
            if (find == IntPtr.Zero || find == new IntPtr(-1))
            {
                log($"Win32: BluetoothFindFirstDevice sin resultados (inquiry={issueInquiry}, error={Marshal.GetLastWin32Error()}).");
                continue;
            }

            try
            {
                int seen = 0;
                do
                {
                    seen++;
                    if (info.Address != bigEndian && info.Address != littleEndian)
                    {
                        continue;
                    }

                    log($"Win32: dispositivo encontrado «{info.szName}» (recordado={info.fRemembered}, autenticado={info.fAuthenticated}, conectado={info.fConnected}).");

                    // Se registra el callback de autenticación para que la
                    // ceremonia SSP se complete sin UI: sin él, Windows intenta
                    // mostrar su asistente y el helper oculto no puede.
                    _log = log;
                    uint registerResult = BluetoothRegisterForAuthenticationEx(
                        ref info, out IntPtr regHandle, AuthenticationCallback, IntPtr.Zero);
                    log($"Win32: BluetoothRegisterForAuthenticationEx → {registerResult}");

                    try
                    {
                        // Mandos sin pantalla no pueden hacer MITM: se pide
                        // bonding sin protección MITM y, si falla, sin
                        // requirements extra.
                        foreach (uint requirement in new[]
                        {
                            MITMProtectionNotRequiredBonding,
                            MITMProtectionNotRequired
                        })
                        {
                            var copy = info;
                            uint result = BluetoothAuthenticateDeviceEx(
                                IntPtr.Zero, IntPtr.Zero, ref copy, IntPtr.Zero, requirement);
                            log($"Win32: BluetoothAuthenticateDeviceEx(requirement={requirement}) → {result}");

                            if (result == ERROR_SUCCESS)
                            {
                                // La enumeración SDP conecta con el dispositivo
                                // pero puede bloquearse si este no responde;
                                // se le da un margen y se continúa igual: el
                                // vínculo ya quedó creado.
                                try
                                {
                                    Task.Run(() => EnumerateDeviceServices(copy, log))
                                        .Wait(TimeSpan.FromSeconds(15));
                                }
                                catch
                                {
                                    // La enumeración es best-effort; el
                                    // emparejamiento ya está hecho.
                                }
                                return DevicePairingResultStatus.Paired;
                            }

                            if (result == ERROR_CANCELLED)
                            {
                                return DevicePairingResultStatus.AccessDenied;
                            }
                        }

                        return DevicePairingResultStatus.Failed;
                    }
                    finally
                    {
                        _log = null;
                        if (regHandle != IntPtr.Zero)
                        {
                            BluetoothUnregisterAuthentication(regHandle);
                        }
                    }
                }
                while (BluetoothFindNextDevice(find, ref info));

                log($"Win32: {seen} dispositivo(s) en la búsqueda, ninguno coincide con {macAddress} (inquiry={issueInquiry}).");
            }
            finally
            {
                BluetoothFindDeviceClose(find);
            }
        }

        return null;
    }

    /// <summary>Extrae «aa:bb:cc:dd:ee:ff» del final del id AEP de Bluetooth.</summary>
    public static string? MacFromDeviceId(string deviceId)
    {
        int separator = deviceId.LastIndexOf('-');
        if (separator < 0 || separator == deviceId.Length - 1)
        {
            return null;
        }

        string candidate = deviceId[(separator + 1)..];
        return ParseMac(candidate) is null ? null : candidate;
    }

    private static byte[]? ParseMac(string mac)
    {
        string[] parts = mac.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 6)
        {
            return null;
        }

        var bytes = new byte[6];
        for (int i = 0; i < 6; i++)
        {
            if (parts[i].Length != 2 ||
                !byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out bytes[i]))
            {
                return null;
            }
        }

        return bytes;
    }
}
