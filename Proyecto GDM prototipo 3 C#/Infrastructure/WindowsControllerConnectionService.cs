using GDMInputManager.Services;

namespace GDMInputManager.Infrastructure;

/// <summary>
/// Implementación del servicio de conexión usando Windows APIs
/// </summary>
public class WindowsControllerConnectionService : IControllerConnectionService
{
    public async Task<bool> ConnectAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        // Implementación futura: usar Windows APIs para conectar
        await Task.CompletedTask;
        return false;
    }

    public async Task<bool> DisconnectAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        // Implementación futura: usar Windows APIs para desconectar
        await Task.CompletedTask;
        return false;
    }

    public async Task<bool> PairAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        // Implementación futura: usar Windows Bluetooth APIs para emparejar
        await Task.CompletedTask;
        return false;
    }
}
