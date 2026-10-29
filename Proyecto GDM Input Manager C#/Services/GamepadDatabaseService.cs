using GDMInputManager.Models;
using GDMInputManager.Data;

namespace GDMInputManager.Services;

/// <summary>
/// Servicio para gestionar la base de datos de mandos usando archivos JSON individuales
/// Estructura: Servicio que interactúa con JsonDataManager para persistir mandos
/// Propósito: Proporcionar operaciones CRUD (Create, Read, Update, Delete) para mandos
/// Estrategia de almacenamiento:
///   - Cada mando se guarda en su propio archivo JSON: mando_{id}.json
///   - Archivos organizados en carpeta "Gamepads" dentro del directorio de datos
/// Ventajas:
///   - Fácil agregar/eliminar mandos individuales
///   - Fácil respaldar mandos específicos
///   - Menos propenso a corrupción (si un archivo falla, no afecta a los demás)
/// </summary>
public class GamepadDatabaseService
{
    // Instancia de JsonDataManager para operaciones de lectura/escritura
    private readonly JsonDataManager _dataManager;

    // Ruta del directorio donde se guardan los archivos de mandos
    private readonly string _gamepadsDirectory;

    /// <summary>
    /// Constructor del servicio
    /// Estructura: Inicializa el JsonDataManager y crea el directorio de mandos si no existe
    /// Propósito: Preparar el servicio para su uso, asegurando que el directorio de almacenamiento exista
    /// Proceso:
    ///   1. Guarda la instancia de JsonDataManager recibida
    ///   2. Construye la ruta del directorio "Gamepads" dentro del directorio de datos
    ///   3. Si el directorio no existe, lo crea
    /// Parámetros:
    ///   - dataManager: Instancia de JsonDataManager para operaciones de E/S
    /// </summary>
    public GamepadDatabaseService(JsonDataManager dataManager)
    {
        _dataManager = dataManager;
        _gamepadsDirectory = Path.Combine(_dataManager.GetDataDirectory(), "Gamepads");

        if (!Directory.Exists(_gamepadsDirectory))
        {
            Directory.CreateDirectory(_gamepadsDirectory);
        }
    }

    /// <summary>
    /// Guarda o actualiza un mando en la base de datos
    /// Estructura: Serializa el objeto Gamepad a JSON y lo guarda en un archivo
    /// Propósito: Persistir la información de un mando (nuevo o existente)
    /// Proceso:
    ///   1. Construye el nombre del archivo: mando_{id}.json
    ///   2. Actualiza la fecha de último uso del mando
    ///   3. Usa JsonDataManager para guardar el objeto como JSON
    /// Nota: Si el archivo ya existe, se sobrescribe (update if exists, insert if not)
    /// Parámetros:
    ///   - gamepad: Objeto Gamepad con toda la información del mando
    /// </summary>
    public void SaveGamepad(Gamepad gamepad)
    {
        string fileName = $"mando_{gamepad.Id}.json";
        string filePath = Path.Combine(_gamepadsDirectory, fileName);

        // Actualizar fecha de último uso para saber cuándo se usó el mando por última vez
        gamepad.LastUsed = DateTime.Now;

        _dataManager.SaveToFile(fileName, gamepad);
    }

    /// <summary>
    /// Carga un mando por su ID
    /// Estructura: Lee el archivo JSON correspondiente y deserializa a un objeto Gamepad
    /// Propósito: Recuperar la información de un mando específico desde la base de datos
    /// Proceso:
    ///   1. Construye el nombre del archivo usando el ID: mando_{id}.json
    ///   2. Usa JsonDataManager para leer y deserializar el archivo
    ///   3. Retorna el objeto Gamepad o null si no existe
    /// Parámetros:
    ///   - id: ID único del mando a cargar
    /// Retorna: Objeto Gamepad si existe, null si no se encuentra
    /// </summary>
    public Gamepad? LoadGamepad(string id)
    {
        string fileName = $"mando_{id}.json";
        return _dataManager.LoadFromFile<Gamepad>(fileName);
    }

    /// <summary>
    /// Busca un mando por sus identificadores de hardware (VID/PID/MAC)
    /// Estructura: Itera sobre todos los archivos de mandos buscando coincidencias
    /// Propósito: Encontrar un mando específico sin conocer su ID, solo sus identificadores de hardware
    /// Proceso:
    ///   1. Obtiene todos los archivos mando_*.json del directorio
    ///   2. Para cada archivo:
    ///      - Carga el Gamepad desde el JSON
    ///      - Compara VID/PID con los proporcionados
    ///      - Compara MAC Address con el proporcionado
    ///      - Si hay coincidencia, retorna el Gamepad
    ///   3. Si no encuentra coincidencia, retorna null
    /// Uso típico: Cuando se conecta un mando, buscar si ya está guardado en la BD
    /// Parámetros:
    ///   - vid: Vendor ID opcional
    ///   - pid: Product ID opcional
    ///   - macAddress: Dirección MAC opcional
    /// Retorna: Objeto Gamepad si se encuentra coincidencia, null en caso contrario
    /// </summary>
    public Gamepad? FindGamepadByHardwareId(int? vid, int? pid, string? macAddress)
    {
        if (!Directory.Exists(_gamepadsDirectory))
        {
            return null;
        }

        var files = Directory.GetFiles(_gamepadsDirectory, "mando_*.json");

        foreach (var file in files)
        {
            try
            {
                var gamepad = _dataManager.LoadFromFile<Gamepad>(Path.GetFileName(file));
                if (gamepad != null)
                {
                    // Verificar por VID/PID (para mandos USB)
                    if (vid.HasValue && pid.HasValue &&
                        gamepad.Vid == vid.Value && gamepad.Pid == pid.Value)
                    {
                        return gamepad;
                    }

                    // Verificar por MAC Address (para mandos Bluetooth)
                    if (!string.IsNullOrEmpty(macAddress) &&
                        gamepad.MacAddress == macAddress)
                    {
                        return gamepad;
                    }
                }
            }
            catch
            {
                // Error al leer archivo, continuar con el siguiente
            }
        }

        return null;
    }

    /// <summary>
    /// Obtiene todos los mandos guardados en la base de datos
    /// Estructura: Lee todos los archivos mando_*.json y los deserializa
    /// Propósito: Recuperar la lista completa de mandos guardados
    /// Proceso:
    ///   1. Obtiene todos los archivos mando_*.json del directorio
    ///   2. Para cada archivo:
    ///      - Carga el Gamepad desde el JSON
    ///      - Lo agrega a la lista de resultado
    ///   3. Retorna la lista completa
    /// Uso típico: Mostrar todos los mandos en la interfaz (conectados y desconectados)
    /// Retorna: Lista de todos los objetos Gamepad guardados
    /// </summary>
    public List<Gamepad> GetAllGamepads()
    {
        var gamepads = new List<Gamepad>();

        if (!Directory.Exists(_gamepadsDirectory))
        {
            return gamepads;
        }

        var files = Directory.GetFiles(_gamepadsDirectory, "mando_*.json");

        foreach (var file in files)
        {
            try
            {
                var gamepad = _dataManager.LoadFromFile<Gamepad>(Path.GetFileName(file));
                if (gamepad != null)
                {
                    gamepads.Add(gamepad);
                }
            }
            catch
            {
                // Error al leer archivo, continuar con el siguiente
            }
        }

        return gamepads;
    }

    /// <summary>
    /// Elimina un mando de la base de datos
    /// Estructura: Elimina el archivo JSON correspondiente al mando
    /// Propósito: Remover permanentemente un mando de la base de datos
    /// Proceso:
    ///   1. Construye el nombre del archivo usando el ID: mando_{id}.json
    ///   2. Usa JsonDataManager para eliminar el archivo
    /// Nota: Esta operación es irreversible
    /// Parámetros:
    ///   - id: ID único del mando a eliminar
    /// </summary>
    public void DeleteGamepad(string id)
    {
        string fileName = $"mando_{id}.json";
        _dataManager.DeleteFile(fileName);
    }

    /// <summary>
    /// Obtiene solo los mandos actualmente conectados
    /// Estructura: Filtra la lista de todos los mandos por el estado IsConnected
    /// Propósito: Obtener solo los mandos que están físicamente conectados al sistema
    /// Proceso:
    ///   1. Obtiene todos los mandos usando GetAllGamepads()
    ///   2. Filtra por la propiedad IsConnected == true
    ///   3. Retorna la lista filtrada
    /// Uso típico: Mostrar solo mandos activos, operaciones que requieren conexión
    /// Retorna: Lista de mandos con IsConnected == true
    /// </summary>
    public List<Gamepad> GetConnectedGamepads()
    {
        return GetAllGamepads().Where(g => g.IsConnected).ToList();
    }
}
