using GDMInputManager.Models;
using GDMInputManager.Services;
using SDL2;
using System.Runtime.InteropServices;

namespace GDMInputManager.Infrastructure;

/// <summary>
/// Servicio de descubrimiento de controladores usando SDL2
/// Responsable de enumerar gamepads conectados y traducirlos a ControllerDevice
/// </summary>
public class SdlControllerDiscoveryService : IControllerDiscoveryService, IDisposable
{
    private bool _isInitialized;
    private bool _disposed;

    public SdlControllerDiscoveryService()
    {
        InitializeSdl();
    }

    private void InitializeSdl()
    {
        if (_isInitialized) return;

        // Inicializar SDL2
        if (SDL.SDL_Init(SDL.SDL_INIT_GAMECONTROLLER | SDL.SDL_INIT_JOYSTICK) < 0)
        {
            throw new InvalidOperationException($"No se pudo inicializar SDL: {SDL.SDL_GetError()}");
        }

        _isInitialized = true;
    }

    public async Task<IList<ControllerDevice>> EnumerateConnectedAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var devices = new List<ControllerDevice>();

            if (!_isInitialized)
                return devices;

            // Usar SDL_NumJoysticks para obtener el número de joysticks conectados
            int numJoysticks = SDL.SDL_NumJoysticks();

            for (int i = 0; i < numJoysticks; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                var device = CreateControllerDeviceFromIndex(i);
                if (device != null)
                {
                    devices.Add(device);
                }
            }

            return devices;
        }, cancellationToken);
    }

    private ControllerDevice? CreateControllerDeviceFromIndex(int index)
    {
        // Obtener el nombre del dispositivo
        string name = SDL.SDL_JoystickNameForIndex(index) ?? "Unknown";

        // Usar el índice + nombre como identificador estable (GUID simplificado)
        string stableId = $"sdl_{index}_{name.GetHashCode():X8}";

        // Crear el dispositivo
        var device = new ControllerDevice
        {
            StableId = stableId,
            DisplayName = name,
            ConnectionType = DetermineConnectionType(index),
            IsConnected = true,
            SdlInstanceId = index,
            LastUpdated = DateTimeOffset.UtcNow
        };

        return device;
    }

    private ControllerConnectionType DetermineConnectionType(int index)
    {
        // SDL2 no proporciona directamente el tipo de conexión en todas las versiones
        // Por defecto asumimos Unknown
        return ControllerConnectionType.Unknown;
    }

    public async Task<IList<ControllerDevice>> EnumeratePairedAsync(CancellationToken cancellationToken = default)
    {
        // SDL2 no proporciona información de dispositivos emparejados
        // Esta función es responsabilidad del servicio Windows
        return await Task.FromResult<IList<ControllerDevice>>(new List<ControllerDevice>());
    }

    public async Task ScanBluetoothAsync(TimeSpan duration, CancellationToken cancellationToken, Action<ControllerDevice>? onDeviceFound = null)
    {
        // SDL2 no tiene capacidades de escaneo Bluetooth
        // Esta función es responsabilidad del servicio Windows
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;

        if (_isInitialized)
        {
            SDL.SDL_Quit();
            _isInitialized = false;
        }

        _disposed = true;
    }
}
