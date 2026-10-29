namespace GDMInputManager.Services;

/// <summary>
/// Niveles de severidad para los mensajes de log
/// Estructura: Enumeración con 4 niveles de importancia
/// Propósito: Clasificar los mensajes según su severidad para aplicar colores diferentes
/// Valores:
///   - Info: Información general (blanco)
///   - Success: Operación exitosa (cian/azul claro)
///   - Warning: Advertencia (amarillo)
///   - Error: Error crítico (rojo)
/// </summary>
public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>
/// Servicio global de logging usando patrón Singleton
/// Estructura: Clase estática con una única instancia accesible globalmente
/// Propósito: Centralizar todos los logs de la aplicación en un solo lugar
/// Ventajas:
///   - Todas las pestañas pueden enviar logs sin necesidad de referencias directas
///   - Fácil de acceder desde cualquier parte del código (LoggerService.Instance)
///   - Permite cambiar el sistema de logging sin modificar el código que lo usa
/// Flujo de datos:
///   - Cualquier parte del código llama a LoggerService.Instance.Info/Error/etc
///   - LoggerService dispara el evento OnLogMessage
///   - RegistroControl está suscrito al evento y muestra el mensaje en la consola
/// </summary>
public class LoggerService
{
    // Instancia única del servicio (Singleton)
    private static LoggerService? _instance;

    /// <summary>
    /// Propiedad para acceder a la instancia única
    /// Estructura: Si _instance es null, crea una nueva instancia; si no, retorna la existente
    /// Propósito: Garantizar que solo exista una instancia del logger
    /// Patró: Lazy initialization (se crea solo cuando se necesita por primera vez)
    /// </summary>
    public static LoggerService Instance => _instance ??= new LoggerService();

    /// <summary>
    /// Evento que se dispara cuando se agrega un mensaje de log
    /// Estructura: Evento de tipo Action<string, LogLevel>
    /// Propósito: Permitir que múltiples componentes (como RegistroControl) se suscriban y reciban logs
    /// Parámetros del evento:
    ///   - string: El mensaje formateado con timestamp
    ///   - LogLevel: El nivel de severidad del mensaje
    /// Uso: Los suscriptores (ej: RegistroControl) usan este evento para mostrar los logs en la UI
    /// </summary>
    public event Action<string, LogLevel>? OnLogMessage;

    /// <summary>
    /// Constructor privado (patrón Singleton)
    /// Estructura: Constructor privado para impedir instanciación externa
    /// Propósito: Asegurar que solo se pueda crear una instancia a través de Instance
    /// </summary>
    private LoggerService()
    {
    }

    /// <summary>
    /// Registra un mensaje de información (blanco en la consola)
    /// Estructura: Método de conveniencia que llama a Log con nivel Info
    /// Propósito: Proveer un método fácil para registrar información general
    /// Uso típico: Mensajes de estado, información de progreso, eventos informativos
    /// </summary>
    public void Info(string message)
    {
        Log(message, LogLevel.Info);
    }

    /// <summary>
    /// Registra un mensaje de éxito (cian en la consola)
    /// Estructura: Método de conveniencia que llama a Log con nivel Success
    /// Propósito: Proveer un método fácil para registrar operaciones exitosas
    /// Uso típico: Operaciones completadas exitosamente, inicializaciones correctas
    /// </summary>
    public void Success(string message)
    {
        Log(message, LogLevel.Success);
    }

    /// <summary>
    /// Registra una advertencia (amarillo en la consola)
    /// Estructura: Método de conveniencia que llama a Log con nivel Warning
    /// Propósito: Proveer un método fácil para registrar advertencias
    /// Uso típico: Situaciones que requieren atención pero no son errores críticos
    /// </summary>
    public void Warning(string message)
    {
        Log(message, LogLevel.Warning);
    }

    /// <summary>
    /// Registra un error (rojo en la consola)
    /// Estructura: Método de conveniencia que llama a Log con nivel Error
    /// Propósito: Proveer un método fácil para registrar errores
    /// Uso típico: Excepciones, fallos de inicialización, errores críticos
    /// </summary>
    public void Error(string message)
    {
        Log(message, LogLevel.Error);
    }

    /// <summary>
    /// Registra un mensaje con el nivel especificado
    /// Estructura: Método principal de logging, al que llaman los demás métodos
    /// Propósito: Formatear el mensaje con timestamp y disparar el evento OnLogMessage
    /// Proceso:
    ///   1. Genera un timestamp en formato "yyyy-MM-dd HH:mm:ss"
    ///   2. Formatea el mensaje como "[timestamp] [nivel] mensaje"
    ///   3. Dispara el evento OnLogMessage con el mensaje formateado y el nivel
    /// Parámetros:
    ///   - message: El texto del mensaje a registrar
    ///   - level: El nivel de severidad (por defecto Info)
    /// </summary>
    public void Log(string message, LogLevel level = LogLevel.Info)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string formattedMessage = $"[{timestamp}] [{level}] {message}";

        OnLogMessage?.Invoke(formattedMessage, level);
    }
}
