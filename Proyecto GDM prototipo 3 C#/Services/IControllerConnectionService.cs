namespace GDMInputManager.Services;

/// <summary>
/// Servicio para conectar y desconectar dispositivos de controlador
/// </summary>
public interface IControllerConnectionService
{
    /// <summary>
    /// Conecta un dispositivo por su identificador
    /// </summary>
    Task<bool> ConnectAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Desconecta un dispositivo por su identificador
    /// </summary>
    Task<bool> DisconnectAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Empareja un dispositivo Bluetooth (si la API y permisos lo permiten)
    /// </summary>
    Task<bool> PairAsync(string deviceId, CancellationToken cancellationToken = default);
}
