namespace GDMInputManager.Services;

/// <summary>
/// Servicio para obtener información de batería de controladores
/// </summary>
public interface IControllerBatteryService
{
    /// <summary>
    /// Obtiene el nivel de batería de un dispositivo
    /// Retorna null si no es compatible o no se conoce
    /// </summary>
    Task<int?> GetBatteryAsync(string deviceId, CancellationToken cancellationToken = default);
}
