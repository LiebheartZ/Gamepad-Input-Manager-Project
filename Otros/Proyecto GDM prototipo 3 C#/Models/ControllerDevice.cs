namespace GDMInputManager.Models;

/// <summary>
/// Tipo de conexión del controlador
/// </summary>
public enum ControllerConnectionType
{
    Unknown,
    Bluetooth,
    USB,
    WirelessReceiver
}

/// <summary>
/// Modelo de dispositivo de controlador (mando)
/// Representa un mando con todos sus estados independientes
/// </summary>
public class ControllerDevice
{
    /// <summary>
    /// Identificador normalizado utilizado para fusionar registros
    /// Se basa en identificador estable del dispositivo (Device Instance ID, dirección Bluetooth, etc.)
    /// </summary>
    public string StableId { get; set; } = string.Empty;

    /// <summary>
    /// Identificador de dispositivo de Windows (si aplica)
    /// </summary>
    public string? WindowsDeviceId { get; set; }

    /// <summary>
    /// Nombre legible para la interfaz
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Fabricante del dispositivo (opcional)
    /// </summary>
    public string? Manufacturer { get; set; }

    /// <summary>
    /// Nombre del producto (opcional)
    /// </summary>
    public string? ProductName { get; set; }

    /// <summary>
    /// Tipo de conexión del dispositivo
    /// </summary>
    public ControllerConnectionType ConnectionType { get; set; }

    /// <summary>
    /// Indica si el dispositivo está actualmente conectado y operativo
    /// Estado en tiempo real de la detección
    /// </summary>
    public bool IsConnected { get; set; }

    /// <summary>
    /// Indica si Windows conserva la relación de emparejamiento (ej. Bluetooth)
    /// No implica que el mando esté encendido o conectado
    /// </summary>
    public bool IsPaired { get; set; }

    /// <summary>
    /// Indica si existe un registro del mando en la base de datos de GDM
    /// Con perfil, preferencias u otros datos guardados
    /// </summary>
    public bool IsSaved { get; set; }

    /// <summary>
    /// Indica si el dispositivo Bluetooth está siendo anunciado/descubierto
    /// y puede ser candidato a conexión
    /// Temporal durante búsqueda
    /// </summary>
    public bool IsAvailableForPairing { get; set; }

    /// <summary>
    /// Nivel de batería como porcentaje (0-100)
    /// Null si se desconoce o no es compatible
    /// </summary>
    public int? BatteryPercentage { get; set; }

    /// <summary>
    /// ID del perfil asignado (si aplica)
    /// </summary>
    public string? ProfileId { get; set; }

    /// <summary>
    /// Nombre del perfil asignado
    /// "Por defecto" cuando no hay perfil específico
    /// </summary>
    public string? ProfileName { get; set; }

    /// <summary>
    /// Última vez que se detectó el dispositivo
    /// </summary>
    public DateTimeOffset? LastSeen { get; set; }

    /// <summary>
    /// Última actualización de los datos del dispositivo
    /// </summary>
    public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Identificador técnico (Device Instance Path, dirección Bluetooth, etc.)
    /// Se usa para diagnóstico y referencia, pero se muestra enmascarado en la UI
    /// </summary>
    public string? DeviceInstancePath { get; set; }

    /// <summary>
    /// Identificador SDL del dispositivo (si aplica)
    /// </summary>
    public int? SdlInstanceId { get; set; }

    /// <summary>
    /// GUID del dispositivo SDL (si aplica)
    /// </summary>
    public string? SdlGuid { get; set; }

    /// <summary>
    /// Retorna el identificador enmascarado para mostrar en la UI
    /// </summary>
    public string GetMaskedIdentifier()
    {
        if (string.IsNullOrEmpty(DeviceInstancePath))
            return "N/A";

        var parts = DeviceInstancePath.Split('\\', '#');
        if (parts.Length >= 2)
        {
            return $"{parts[0]}\\{parts[1]}\\..."; // Muestra solo los primeros dos segmentos
        }

        return "N/A";
    }

    /// <summary>
    /// Retorna el estado principal para mostrar en la UI
    /// </summary>
    public string GetDisplayStatus()
    {
        if (IsConnected)
            return "Conectado";
        if (IsAvailableForPairing)
            return "Disponible";
        if (IsPaired)
            return "Emparejado";
        if (IsSaved)
            return "Guardado";
        return "Desconectado";
    }

    /// <summary>
    /// Retorna el texto de batería para mostrar en la UI
    /// </summary>
    public string GetBatteryDisplay()
    {
        if (BatteryPercentage.HasValue && BatteryPercentage.Value >= 0 && BatteryPercentage.Value <= 100)
            return $"{BatteryPercentage.Value}%";
        return "N/A";
    }
}
