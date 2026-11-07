using GDMInputManager.Interfaces;
using GDMInputManager.Models;
using GDMInputManager.Services;

namespace GDMInputManager.Infrastructure.Windows;

public sealed class WindowsGamepadConnectionMetadataProvider : IGamepadConnectionEnricher
{
    private readonly WindowsUsbConnectionService _usbService = new();

    public async Task<GamepadEnrichmentResult> EnrichAsync(
        IReadOnlyList<GamepadDevice> devices,
        CancellationToken cancellationToken)
    {
        HashSet<(int Vid, int Pid)>? usbIds = null;
        var warnings = new List<string>();
        try
        {
            usbIds = await Task.Run(_usbService.GetConnectedUsbGamepadIds, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            string warning = $"Windows no pudo obtener transporte USB: {ex.Message}";
            warnings.Add(warning);
            LoggerService.Instance.Warning(warning);
        }

        LoggerService.Instance.Info(
            usbIds == null
                ? "No se pudo determinar el número de dispositivos USB durante el enriquecimiento."
                : $"Windows informó {usbIds.Count} identificador(es) VID/PID USB presente(s).");

        var sdlDevices = devices.Where(device => device.Provider == "SDL2").ToList();
        LoggerService.Instance.Info(
            $"Enriquecimiento de conexión: {sdlDevices.Count} dispositivo(s) SDL2.");
        var enriched = new List<GamepadDevice>(devices.Count);

        foreach (GamepadDevice device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (device.Provider != "SDL2")
            {
                enriched.Add(device);
                continue;
            }

            bool hasUsbEvidence = usbIds != null &&
                                  device.Vid.HasValue &&
                                  device.Pid.HasValue &&
                                  usbIds.Contains((device.Vid.Value, device.Pid.Value)) &&
                                  sdlDevices.Count(candidate =>
                                      candidate.Vid == device.Vid && candidate.Pid == device.Pid) == 1;

            if (hasUsbEvidence)
            {
                enriched.Add(device with { ConnectionType = ConnectionType.USB });
            }
            else
            {
                enriched.Add(device);
            }
        }

        return new GamepadEnrichmentResult(enriched, warnings);
    }

}
