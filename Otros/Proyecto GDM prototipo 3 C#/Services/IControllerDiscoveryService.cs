using GDMInputManager.Models;

namespace GDMInputManager.Services;

/// <summary>
/// Servicio para descubrir y enumerar dispositivos de controlador
/// </summary>
public interface IControllerDiscoveryService
{
    /// <summary>
    /// Enumera los dispositivos conectados actualmente
    /// </summary>
    Task<IList<ControllerDevice>> EnumerateConnectedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Enumera los dispositivos emparejados por Windows
    /// </summary>
    Task<IList<ControllerDevice>> EnumeratePairedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Inicia una búsqueda Bluetooth temporal de dispositivos disponibles
    /// </summary>
    /// <param name="duration">Duración de la búsqueda</param>
    /// <param name="cancellationToken">Token de cancelación</param>
    /// <param name="onDeviceFound">Callback cuando se encuentra un dispositivo</param>
    Task ScanBluetoothAsync(TimeSpan duration, CancellationToken cancellationToken, Action<ControllerDevice>? onDeviceFound = null);
}
