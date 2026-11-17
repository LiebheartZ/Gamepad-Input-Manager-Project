using GDMInputManager.Models;

namespace GDMInputManager.Services;

/// <summary>
/// Implementación del servicio de lista de controladores
/// Coordina múltiples fuentes y gestiona la lista combinada
/// </summary>
public class ControllerListService : IControllerListService
{
    private readonly IControllerDiscoveryService _discoveryService;
    private readonly IControllerRepository _repository;

    public ControllerListService(
        IControllerDiscoveryService discoveryService,
        IControllerRepository repository)
    {
        _discoveryService = discoveryService;
        _repository = repository;
    }

    public async Task<IList<ControllerDevice>> RefreshAsync(CancellationToken cancellationToken = default)
    {
        // Obtener dispositivos de todas las fuentes
        var connectedTask = _discoveryService.EnumerateConnectedAsync(cancellationToken);
        var savedTask = _repository.GetSavedControllersAsync(cancellationToken);

        await Task.WhenAll(connectedTask, savedTask);

        var connected = await connectedTask;
        var saved = await savedTask;

        // Combinar todas las fuentes
        var allDevices = new List<ControllerDevice>(connected);
        allDevices.AddRange(saved);

        // Fusionar y ordenar
        return MergeAndSort(allDevices);
    }

    public async Task<IList<ControllerDevice>> SearchAvailableAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        // Iniciar búsqueda Bluetooth
        var bluetoothDevices = new List<ControllerDevice>();

        await _discoveryService.ScanBluetoothAsync(duration, cancellationToken, device =>
        {
            bluetoothDevices.Add(device);
        });

        // Obtener dispositivos actuales
        var currentDevices = await RefreshAsync(cancellationToken);

        // Agregar dispositivos descubiertos
        foreach (var device in bluetoothDevices)
        {
            currentDevices.Add(device);
        }

        return MergeAndSort(currentDevices);
    }

    public IList<ControllerDevice> MergeAndSort(IList<ControllerDevice> sources)
    {
        var merged = new Dictionary<string, ControllerDevice>();

        foreach (var device in sources)
        {
            if (merged.ContainsKey(device.StableId))
            {
                // Fusionar dispositivos existentes
                var existing = merged[device.StableId];
                existing.IsConnected = existing.IsConnected || device.IsConnected;
                existing.IsPaired = existing.IsPaired || device.IsPaired;
                existing.IsSaved = existing.IsSaved || device.IsSaved;
                existing.IsAvailableForPairing = existing.IsAvailableForPairing || device.IsAvailableForPairing;

                // Actualizar datos si el nuevo tiene más información
                if (!string.IsNullOrEmpty(device.DisplayName))
                    existing.DisplayName = device.DisplayName;
                if (device.BatteryPercentage.HasValue)
                    existing.BatteryPercentage = device.BatteryPercentage;
                if (!string.IsNullOrEmpty(device.ProfileName))
                    existing.ProfileName = device.ProfileName;

                existing.LastUpdated = DateTimeOffset.UtcNow;
            }
            else
            {
                merged[device.StableId] = device;
            }
        }

        // Ordenar: conectados primero, luego emparejados/guardados, al final disponibles
        var sorted = merged.Values.OrderByDescending(d => d.IsConnected)
                                   .ThenByDescending(d => d.IsPaired || d.IsSaved)
                                   .ThenByDescending(d => d.IsAvailableForPairing)
                                   .ThenBy(d => d.DisplayName)
                                   .ToList();

        return sorted;
    }
}
