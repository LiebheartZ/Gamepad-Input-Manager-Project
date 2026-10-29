namespace GDMInputManager.Models;

/// <summary>
/// Modelo de configuraciones generales de la aplicación
/// Estructura: Clase POCO con propiedades para configuraciones globales
/// Propósito: Almacenar configuraciones que afectan a toda la aplicación
/// Uso típico:
///   - Versión de la aplicación
///   - Preferencias del usuario
///   - Configuraciones de la interfaz
///   - Configuraciones que se comparten entre pestañas
/// Estado actual: Modelo básico, se expandirá según se agreguen funcionalidades
/// Flujo de datos:
///   - Cargado al iniciar la aplicación
///   - Guardado cuando el usuario cambia configuraciones
///   - Persistido en un archivo JSON (configuraciones.json)
/// </summary>
public class AppSettings
{
    /// <summary>
    /// Versión de la aplicación
    /// Estructura: String con formato semántico (ej: "1.0.0")
    /// Propósito: Rastrear la versión de la configuración para migraciones futuras
    /// Valor inicial: "1.0.0"
    /// </summary>
    public string Version { get; set; } = "1.0.0";

    /// <summary>
    /// Fecha y hora de la última modificación de las configuraciones
    /// Estructura: DateTime
    /// Propósito: Rastrear cuándo se modificaron las configuraciones por última vez
    /// Valor inicial: DateTime.Now (ahora)
    /// Uso: Para mostrar al usuario, para sincronización, para backups
    /// </summary>
    public DateTime LastModified { get; set; } = DateTime.Now;
}
