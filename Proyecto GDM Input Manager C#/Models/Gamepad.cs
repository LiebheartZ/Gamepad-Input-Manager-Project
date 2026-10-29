namespace GDMInputManager.Models;

/// <summary>
/// Modelo de datos que representa un mando de juego
/// Estructura: Clase POCO (Plain Old CLR Object) con propiedades para toda la información del mando
/// Propósito: Representar toda la información relevante de un mando en un solo objeto
/// Flujo de datos:
///   - Detectado por IGamepadDetector → Creado como objeto Gamepad
///   - Guardado en JSON por GamepadDatabaseService
///   - Mostrado en la UI por MandosControl
/// Campos:
///   - Identificadores: ID, VID, PID, MAC, SerialNumber
///   - Información: Name, Type, ModelName, ConnectionType
///   - Estado: IsConnected, LastUsed
///   - Configuración: ActiveProfileId, EnabledFeatures, Settings
/// </summary>
public class Gamepad
{
    /// <summary>
    /// Identificador único del mando (UUID generado automáticamente)
    /// Estructura: String que contiene un GUID (Globally Unique Identifier)
    /// Propósito: Identificar de forma única cada mando en la base de datos
    /// Valor inicial: Generado automáticamente con Guid.NewGuid().ToString()
    /// Uso: Como nombre de archivo (mando_{id}.json) y clave de búsqueda
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Nombre amigable del mando personalizable por el usuario
    /// Estructura: String que el usuario puede editar
    /// Propósito: Mostrar un nombre legible en la interfaz (ej: "DualSense de Juan", "Mando principal")
    /// Valor inicial: String vacío (se llena con el nombre detectado por SDL2)
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Tipo de mando según su clasificación
    /// Estructura: Enum del tipo GamepadType
    /// Propósito: Clasificar el mando para aplicar configuraciones específicas según el tipo
    /// Valores posibles: PlayStationDualShock4, PlayStationDualSense, XboxOne, XboxSeries, etc.
    /// Uso: Para mostrar iconos diferentes, aplicar mapeos específicos, etc.
    /// </summary>
    public GamepadType Type { get; set; }

    /// <summary>
    /// Tipo de conexión actual del mando
    /// Estructura: Enum del tipo ConnectionType
    /// Propósito: Indicar cómo está conectado el mando al sistema
    /// Valores posibles: USB, Bluetooth, WirelessProprietary, Unknown
    /// Uso: Para mostrar información al usuario, aplicar configuraciones específicas de conexión
    /// </summary>
    public ConnectionType ConnectionType { get; set; }

    /// <summary>
    /// Vendor ID (identificador del fabricante en USB)
    /// Estructura: Entero opcional (nullable int)
    /// Propósito: Identificar el fabricante del mando (ej: 0x054C = Sony, 0x045E = Microsoft)
    /// Valor inicial: null (se llena cuando se detecta el mando)
    /// Uso: Para identificar el tipo de mando, para buscar en la base de datos
    /// Nota: VID y PID juntos identifican un modelo específico de dispositivo USB
    /// </summary>
    public int? Vid { get; set; }

    /// <summary>
    /// Product ID (identificador del producto en USB)
    /// Estructura: Entero opcional (nullable int)
    /// Propósito: Identificar el modelo específico del mando dentro del fabricante
    /// Valor inicial: null (se llena cuando se detecta el mando)
    /// Uso: Combinado con VID, identifica exactamente qué modelo de mando es
    /// </summary>
    public int? Pid { get; set; }

    /// <summary>
    /// Dirección MAC (Media Access Control) para Bluetooth
    /// Estructura: String opcional
    /// Propósito: Identificar el mando cuando está conectado por Bluetooth
    /// Valor inicial: null (se llena si el mando es Bluetooth)
    /// Uso: Para identificar mandos Bluetooth, para emparejamiento
    /// Nota: La MAC es única por dispositivo Bluetooth
    /// </summary>
    public string? MacAddress { get; set; }

    /// <summary>
    /// Número de serie del mando (si está disponible)
    /// Estructura: String opcional
    /// Propósito: Identificador único proporcionado por el fabricante
    /// Valor inicial: null (no todos los mandos exponen número de serie)
    /// Uso: Para identificación adicional, para fines de soporte
    /// Nota: Algunos mandos Xbox exponen número de serie, otros no
    /// </summary>
    public string? SerialNumber { get; set; }

    /// <summary>
    /// Nombre del modelo del mando (nombre específico del producto)
    /// Estructura: String
    /// Propósito: Mostrar el nombre oficial del modelo (ej: "DualSense", "Xbox Series Controller")
    /// Valor inicial: String vacío (se llena con el nombre detectado por SDL2)
    /// Uso: Para mostrar información detallada al usuario
    /// </summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>
    /// Fecha y hora del último uso del mando
    /// Estructura: DateTime
    /// Propósito: Rastrear cuándo se usó el mando por última vez
    /// Valor inicial: DateTime.Now (se actualiza cada vez que se detecta el mando)
    /// Uso: Para mostrar historial, para ordenar mandos por uso reciente
    /// </summary>
    public DateTime LastUsed { get; set; } = DateTime.Now;

    /// <summary>
    /// ID del perfil de configuración activo
    /// Estructura: String opcional
    /// Propósito: Referenciar qué perfil de configuración está aplicado al mando
    /// Valor inicial: null (sin perfil activo)
    /// Uso: Para aplicar configuraciones específicas (mapeo de botones, sensibilidad, etc.)
    /// Nota: Los perfiles se gestionan en la pestaña "Perfiles"
    /// </summary>
    public string? ActiveProfileId { get; set; }

    /// <summary>
    /// Lista de funciones especiales activadas para este mando
    /// Estructura: List<string>
    /// Propósito: Rastrear qué funciones adicionales están habilitadas
    /// Valor inicial: Lista vacía
    /// Uso: Para habilitar/deshabilitar características como "vibración", "led personalizado", etc.
    /// </summary>
    public List<string> EnabledFeatures { get; set; } = new List<string>();

    /// <summary>
    /// Configuraciones adicionales en formato diccionario (clave-valor)
    /// Estructura: Dictionary<string, object>
    /// Propósito: Almacenar configuraciones específicas que no tienen su propio campo
    /// Valor inicial: Diccionario vacío
    /// Uso: Para configuraciones flexibles, para agregar nuevas configuraciones sin cambiar el modelo
    /// Ejemplo: {"sensibilidad": 0.8, "deadzone": 0.1, "inversion_eje_y": true}
    /// </summary>
    public Dictionary<string, object> Settings { get; set; } = new Dictionary<string, object>();

    /// <summary>
    /// Indica si el mando está actualmente conectado al sistema
    /// Estructura: Boolean
    /// Propósito: Rastrear el estado de conexión del mando
    /// Valor inicial: true (se asume conectado al crearlo)
    /// Uso: Para mostrar en la UI con colores diferentes, para filtrar mandos activos
    /// </summary>
    public bool IsConnected { get; set; } = true;
}

/// <summary>
/// Enumeración de tipos de mandos soportados
/// Estructura: Enum con valores para cada tipo de mando reconocido
/// Propósito: Clasificar mandos para aplicar comportamientos específicos según el tipo
/// Valores:
///   - PlayStationDualShock4: Mando PlayStation 4
///   - PlayStationDualSense: Mando PlayStation 5
///   - XboxOne: Mando Xbox One
///   - XboxSeries: Mando Xbox Series X/S
///   - Xbox360: Mando Xbox 360
///   - GenericXInput: Mando genérico que emula Xbox 360
///   - GenericDirectInput: Mando genérico DirectInput (antiguo)
///   - GenericBluetooth: Mando Bluetooth genérico
///   - Unknown: Tipo no reconocido
/// </summary>
public enum GamepadType
{
    PlayStationDualShock4,
    PlayStationDualSense,
    XboxOne,
    XboxSeries,
    Xbox360,
    GenericXInput,
    GenericDirectInput,
    GenericBluetooth,
    Unknown
}

/// <summary>
/// Enumeración de tipos de conexión
/// Estructura: Enum con valores para cada tipo de conexión soportado
/// Propósito: Indicar cómo está conectado el mando al sistema
/// Valores:
///   - USB: Conexión por cable USB
///   - Bluetooth: Conexión inalámbrica Bluetooth
///   - WirelessProprietary: Conexión inalámbrica propietaria (ej: adaptador Xbox)
///   - Unknown: Tipo de conexión no reconocido
/// </summary>
public enum ConnectionType
{
    USB,
    Bluetooth,
    WirelessProprietary,
    Unknown
}
