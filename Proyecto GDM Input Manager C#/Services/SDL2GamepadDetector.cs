using SDL2;
using GDMInputManager.Models;

namespace GDMInputManager.Services;

/// <summary>
/// Implementación de detector de mandos usando SDL2-CS (Sayers.SDL2.Core)
/// Estructura: Implementa la interfaz IGamepadDetector usando la biblioteca SDL2
/// Propósito: Detectar mandos conectados al sistema usando SDL2 (multiplataforma)
/// Ventajas: Buena compatibilidad con USB y Bluetooth, soporta muchos tipos de mandos
/// </summary>
public class SDL2GamepadDetector : IGamepadDetector
{
    // Campo privado para rastrear si SDL2 fue inicializado correctamente
    private bool _isInitialized = false;
    private readonly WindowsBluetoothConnectionService _bluetoothConnectionService = new();
    private readonly WindowsUsbConnectionService _usbConnectionService = new();

    /// <summary>
    /// Nombre del detector
    /// Propósito: Identificar que este detector usa SDL2
    /// </summary>
    public string Name => "SDL2";

    /// <summary>
    /// Inicializa SDL2
    /// Estructura: Llama a SDL_Init con los subsistemas de joystick y gamecontroller
    /// Propósito: Preparar SDL2 para detectar dispositivos de entrada
    /// Proceso:
    ///   1. Intenta inicializar SDL con subsistemas de joystick y gamecontroller
    ///   2. Si la inicialización falla (retorna < 0), retorna false
    ///   3. Si tiene éxito, marca como inicializado y retorna true
    /// Retorna: true si SDL2 se inicializó correctamente, false en caso contrario
    /// </summary>
    public bool Initialize()
    {
        try
        {
            // Inicializar SDL2 con subsistemas de joystick y gamecontroller
            // SDL_INIT_JOYSTICK: Detección básica de joysticks
            // SDL_INIT_GAMECONTROLLER: Detección de gamepads modernos con mapeo
            if (SDL.SDL_Init(SDL.SDL_INIT_JOYSTICK | SDL.SDL_INIT_GAMECONTROLLER) < 0)
            {
                LoggerService.Instance.Error("SDL2 no pudo inicializar los subsistemas de joystick y gamecontroller.");
                return false;
            }

            _isInitialized = true;
            return true;
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Error($"Excepción al inicializar SDL2: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Obtiene la lista de mandos conectados
    /// Estructura: Escanea todos los joysticks detectados por SDL2 y crea objetos Gamepad
    /// Propósito: Detectar y crear representaciones de todos los mandos conectados
    /// Proceso:
    ///   1. Verifica que SDL2 esté inicializado
    ///   2. Obtiene el número de joysticks conectados
    ///   3. Para cada joystick:
    ///      - Abre el joystick con SDL_JoystickOpen
    ///      - Extrae información (VID, PID, nombre)
    ///      - Crea un objeto Gamepad
    ///      - Cierra el joystick para liberar recursos
    ///   4. Retorna la lista de mandos detectados
    /// Retorna: Lista de objetos Gamepad (vacía si no hay mandos o hay error)
    /// </summary>
    public List<Gamepad> GetConnectedGamepads()
    {
        var gamepads = new List<Gamepad>();

        if (!_isInitialized)
        {
            return gamepads;
        }

        try
        {
            List<string> bluetoothDeviceNames;
            HashSet<(int Vid, int Pid)> connectedUsbGamepadIds;
            bool bluetoothStateAvailable = true;
            try
            {
                bluetoothDeviceNames = _bluetoothConnectionService.GetConnectedDeviceNames();
            }
            catch (Exception ex)
            {
                LoggerService.Instance.Error($"No se pudo consultar el estado Bluetooth de Windows: {ex.Message}");
                bluetoothDeviceNames = new List<string>();
                bluetoothStateAvailable = false;
            }

            try
            {
                connectedUsbGamepadIds = _usbConnectionService.GetConnectedUsbGamepadIds();
            }
            catch (Exception ex)
            {
                LoggerService.Instance.Error($"No se pudo consultar el bus USB de Windows: {ex.Message}");
                connectedUsbGamepadIds = new HashSet<(int Vid, int Pid)>();
            }

            SDL.SDL_PumpEvents();

            // Obtener número de joysticks conectados al sistema
            int numJoysticks = SDL.SDL_NumJoysticks();

            // Iterar sobre cada joystick
            for (int i = 0; i < numJoysticks; i++)
            {
                // Abrir el joystick para obtener su información
                IntPtr joystickPtr = SDL.SDL_JoystickOpen(i);
                if (joystickPtr == IntPtr.Zero)
                {
                    continue; // No se pudo abrir, saltar al siguiente
                }

                try
                {
                    // Crear objeto Gamepad a partir de la información de SDL2
                    var gamepad = CreateGamepadFromSDL(joystickPtr, i);
                    if (gamepad != null)
                    {
                        bool isConnectedByUsb = IsConnectedByUsb(gamepad, connectedUsbGamepadIds);
                        bool isConnectedByBluetooth = IsConnectedByBluetooth(gamepad, gamepads, bluetoothDeviceNames);

                        if (bluetoothStateAvailable &&
                            RequiresWindowsBluetoothState(gamepad) &&
                            !isConnectedByUsb &&
                            !isConnectedByBluetooth)
                        {
                            continue;
                        }

                        gamepad.ConnectionType = ResolveConnectionType(
                            gamepad,
                            isConnectedByBluetooth,
                            isConnectedByUsb);
                        gamepads.Add(gamepad);
                    }
                }
                finally
                {
                    // Siempre cerrar el joystick para liberar recursos
                    SDL.SDL_JoystickClose(joystickPtr);
                }
            }
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Error($"Error al enumerar mandos mediante SDL2: {ex.Message}");
        }

        return gamepads;
    }

    /// <summary>
    /// Identifica un mando por sus identificadores de hardware
    /// Estructura: Crea un objeto Gamepad basado en VID/PID/MAC y lo clasifica
    /// Propósito: Clasificar un mando específico cuando solo se conocen sus identificadores
    /// Proceso:
    ///   1. Crea un nuevo objeto Gamepad con los identificadores proporcionados
    ///   2. Usa IdentifyGamepadType para determinar el tipo de mando (PlayStation, Xbox, etc.)
    ///   3. Usa GetModelName para obtener el nombre del modelo específico
    ///   4. Determina el tipo de conexión (USB si no hay MAC, Bluetooth si hay MAC)
    /// Parámetros:
    ///   - vid: Vendor ID del mando (hexadecimal)
    ///   - pid: Product ID del mando (hexadecimal)
    ///   - macAddress: Dirección MAC opcional (para mandos Bluetooth)
    /// Retorna: Objeto Gamepad con la información identificada
    /// </summary>
    public Gamepad? IdentifyGamepad(int vid, int pid, string? macAddress = null)
    {
        var gamepad = new Gamepad
        {
            Vid = vid,
            Pid = pid,
            MacAddress = macAddress
        };

        // Identificar tipo de mando por VID/PID
        gamepad.Type = IdentifyGamepadType(vid, pid);
        gamepad.ModelName = GetModelName(vid, pid);
        gamepad.ConnectionType = string.IsNullOrEmpty(macAddress) ? ConnectionType.USB : ConnectionType.Bluetooth;

        return gamepad;
    }

    /// <summary>
    /// Limpia recursos de SDL2
    /// Estructura: Llama a SDL_Quit si SDL2 fue inicializado
    /// Propósito: Liberar memoria y recursos nativos de SDL2 al cerrar la aplicación
    /// Proceso:
    ///   1. Verifica si SDL2 fue inicializado
    ///   2. Si lo fue, llama a SDL_Quit para cerrar SDL2
    ///   3. Marca como no inicializado
    /// </summary>
    public void Dispose()
    {
        if (_isInitialized)
        {
            SDL.SDL_Quit();
            _isInitialized = false;
        }
    }

    /// <summary>
    /// Crea un objeto Gamepad a partir de un joystick SDL2
    /// Estructura: Extrae información del joystick SDL2 y la mapea a un objeto Gamepad
    /// Propósito: Convertir la estructura nativa de SDL2 a nuestro modelo de datos
    /// Proceso:
    ///   1. Obtiene VID (Vendor ID) del joystick
    ///   2. Obtiene PID (Product ID) del joystick
    ///   3. Obtiene el nombre del joystick
    ///   4. Identifica el tipo de mando usando los VID/PID
    ///   5. Obtiene el nombre del modelo específico
    ///   6. Marca la conexión como desconocida porque SDL2 no informa el transporte en esta detección
    /// Parámetros:
    ///   - joystickPtr: Puntero al joystick SDL2
    ///   - deviceIndex: Índice del dispositivo en SDL2
    /// Retorna: Objeto Gamepad con la información extraída, o null si hay error
    /// </summary>
    private Gamepad? CreateGamepadFromSDL(IntPtr joystickPtr, int deviceIndex)
    {
        try
        {
            // Obtener VID/PID (identificadores de hardware)
            ushort vid = SDL.SDL_JoystickGetVendor(joystickPtr);
            ushort pid = SDL.SDL_JoystickGetProduct(joystickPtr);

            // Obtener nombre del dispositivo
            string name = SDL.SDL_JoystickName(joystickPtr);

            var gamepad = new Gamepad
            {
                Name = name ?? "Mando desconocido",
                Vid = vid,
                Pid = pid,
                Type = IdentifyGamepadType(vid, pid),
                ModelName = GetModelName(vid, pid),
                ConnectionType = ConnectionType.Unknown
            };

            return gamepad;
        }

        catch (Exception ex)
        {
            LoggerService.Instance.Error($"Error al leer el mando SDL2 del índice {deviceIndex}: {ex.Message}");
            return null;
        }
    }

    private static ConnectionType ResolveConnectionType(
        Gamepad gamepad,
        bool isConnectedByBluetooth,
        bool isConnectedByUsb)
    {
        if (isConnectedByUsb)
        {
            return ConnectionType.USB;
        }

        if (isConnectedByBluetooth && IsXboxBluetoothFamily(gamepad.Type))
        {
            return ConnectionType.Bluetooth;
        }

        if (isConnectedByBluetooth)
        {
            return ConnectionType.Bluetooth;
        }

        return ConnectionType.Unknown;
    }

    private static bool IsConnectedByUsb(
        Gamepad gamepad,
        IReadOnlySet<(int Vid, int Pid)> connectedUsbGamepadIds)
    {
        return gamepad.Vid.HasValue &&
               gamepad.Pid.HasValue &&
               connectedUsbGamepadIds.Contains((gamepad.Vid.Value, gamepad.Pid.Value));
    }

    private static bool IsConnectedByBluetooth(
        Gamepad gamepad,
        IReadOnlyCollection<Gamepad> previouslyDetected,
        IReadOnlyCollection<string> connectedBluetoothDeviceNames)
    {
        string deviceKey = GetBluetoothDeviceKey(gamepad.Name, gamepad.Type);
        if (string.IsNullOrEmpty(deviceKey))
        {
            return false;
        }

        int matchingBluetoothDevices = connectedBluetoothDeviceNames.Count(
            name => GetBluetoothDeviceKey(name, GamepadType.Unknown) == deviceKey);
        int matchingGamepads = previouslyDetected.Count(
            detected => GetBluetoothDeviceKey(detected.Name, detected.Type) == deviceKey) + 1;

        if (matchingBluetoothDevices == matchingGamepads)
        {
            return true;
        }

        return matchingBluetoothDevices == 1 &&
               deviceKey is "DUALSHOCK4" or "XBOXCONTROLLER";
    }

    private static bool IsDualShock4(Gamepad gamepad)
    {
        return GetBluetoothDeviceKey(gamepad.Name, gamepad.Type) == "DUALSHOCK4";
    }

    private static bool IsXboxBluetoothFamily(GamepadType gamepadType)
    {
        return gamepadType is GamepadType.XboxOne or GamepadType.XboxSeries;
    }

    private static bool RequiresWindowsBluetoothState(Gamepad gamepad)
    {
        return IsDualShock4(gamepad) || IsXboxBluetoothFamily(gamepad.Type);
    }

    private static string GetBluetoothDeviceKey(string name, GamepadType gamepadType)
    {
        string normalizedName = new(name.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        if (gamepadType == GamepadType.PlayStationDualShock4 ||
            normalizedName is "PS4CONTROLLER" or "WIRELESSCONTROLLER" or "DUALSHOCK4WIRELESSCONTROLLER")
        {
            return "DUALSHOCK4";
        }

        if (IsXboxBluetoothFamily(gamepadType) ||
            normalizedName is "XBOXWIRELESSCONTROLLER" or "XBOXONECONTROLLER" or
                "XBOXSERIESXCONTROLLER" or "XBOXSERIESSCONTROLLER" or "XBOXCONTROLLER")
        {
            return "XBOXCONTROLLER";
        }

        return normalizedName;
    }

    /// <summary>
    /// Identifica el tipo de mando basándose en VID/PID
    /// Estructura: Usa sentencias if para comparar VID/PID con valores conocidos
    /// Propósito: Clasificar el mando en una categoría (PlayStation, Xbox, Genérico, etc.)
    /// Proceso:
    ///   1. Compara VID/PID con valores conocidos de PlayStation (DualShock 4, DualSense)
    ///   2. Compara con valores de Xbox (One, Series, 360)
    ///   3. Si es de Microsoft pero no reconocido, asume XInput genérico
    ///   4. Si no coincide con ninguno, retorna Unknown
    /// Nota: VID/PID son identificadores únicos asignados por USB-IF
    /// Retorna: Enum GamepadType que representa el tipo de mando
    /// </summary>
    private GamepadType IdentifyGamepadType(int vid, int pid)
    {
        // PlayStation DualShock 4 (VID: 0x054C = Sony, PID: 0x05C4)
        if (vid == 0x054C && pid == 0x05C4)
            return GamepadType.PlayStationDualShock4;

        // PlayStation DualSense (VID: 0x054C = Sony, PID: 0x0CE6)
        if (vid == 0x054C && pid == 0x0CE6)
            return GamepadType.PlayStationDualSense;

        // Xbox One (VID: 0x045E = Microsoft, varios PIDs según versión)
        if (vid == 0x045E && (pid == 0x02D1 || pid == 0x02DD || pid == 0x02E0))
            return GamepadType.XboxOne;

        // Xbox Series (VID: 0x045E = Microsoft, PID: 0x0B12/0x0B13)
        if (vid == 0x045E && (pid == 0x0B12 || pid == 0x0B13))
            return GamepadType.XboxSeries;

        // Xbox 360 (VID: 0x045E = Microsoft, PID: 0x028E/0x028F)
        if (vid == 0x045E && (pid == 0x028E || pid == 0x028F))
            return GamepadType.Xbox360;

        // Genérico XInput (cualquier mando de Microsoft no reconocido específicamente)
        if (vid == 0x045E)
            return GamepadType.GenericXInput;

        return GamepadType.Unknown;
    }

    /// <summary>
    /// Obtiene el nombre del modelo basándose en VID/PID
    /// Estructura: Usa expresión switch para mapear VID/PID a nombres legibles
    /// Propósito: Proporcionar un nombre amigable para el usuario en lugar de VID/PID
    /// Proceso:
    ///   1. Mapea combinaciones específicas de VID/PID a nombres de modelos
    ///   2. Incluye variantes específicas (firmware 2015, BLE, Elite, etc.)
    ///   3. Si no coincide, retorna "Mando Genérico"
    /// Retorna: String con el nombre del modelo del mando
    /// </summary>
    private string GetModelName(int vid, int pid)
    {
        return (vid, pid) switch
        {
            (0x054C, 0x05C4) => "DualShock 4",
            (0x054C, 0x0CE6) => "DualSense",
            (0x045E, 0x02D1) => "Xbox One Controller",
            (0x045E, 0x02DD) => "Xbox One Controller (Firmware 2015)",
            (0x045E, 0x02E0) => "Xbox One Elite Controller",
            (0x045E, 0x0B12) => "Xbox Series Controller",
            (0x045E, 0x0B13) => "Xbox Series Controller (BLE)",
            (0x045E, 0x028E) => "Xbox 360 Controller",
            (0x045E, 0x028F) => "Xbox 360 Wireless Receiver",
            _ => "Mando Genérico"
        };
    }
}
