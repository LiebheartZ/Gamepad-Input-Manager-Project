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

        var bluetoothDevices = devices
            .Where(device => device.Provider == "Windows Bluetooth")
            .ToList();
        var sdlDevices = devices.Where(device => device.Provider == "SDL2").ToList();
        var enriched = new List<GamepadDevice>(devices.Count);

        foreach (GamepadDevice device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (device.Provider != "SDL2")
            {
                enriched.Add(device);
                continue;
            }

            var bluetoothMatches = bluetoothDevices
                .Where(candidate => GetFamilyKey(candidate) == GetFamilyKey(device))
                .ToList();
            int sameFamilySdlDevices = sdlDevices.Count(candidate => GetFamilyKey(candidate) == GetFamilyKey(device));
            bool hasUniqueBluetoothMatch = bluetoothMatches.Count == 1 && sameFamilySdlDevices == 1;
            bool hasUsbEvidence = usbIds != null &&
                                  device.Vid.HasValue &&
                                  device.Pid.HasValue &&
                                  usbIds.Contains((device.Vid.Value, device.Pid.Value)) &&
                                  sdlDevices.Count(candidate =>
                                      candidate.Vid == device.Vid && candidate.Pid == device.Pid) == 1;

            if (hasUsbEvidence)
            {
                enriched.Add(hasUniqueBluetoothMatch
                    ? device with
                    {
                        Identity = device.Identity with
                        {
                            BluetoothAddress = bluetoothMatches[0].Identity.BluetoothAddress
                        },
                        ConnectionType = ConnectionType.USB,
                        IsPaired = bluetoothMatches[0].IsPaired,
                        CanUnpair = bluetoothMatches[0].CanUnpair
                    }
                    : device with { ConnectionType = ConnectionType.USB });
            }
            else if (hasUniqueBluetoothMatch && usbIds != null)
            {
                GamepadDevice bluetoothDevice = bluetoothMatches[0];
                enriched.Add(device with
                {
                    Identity = device.Identity with
                    {
                        BluetoothAddress = bluetoothDevice.Identity.BluetoothAddress
                    },
                    ConnectionType = bluetoothDevice.IsConnected
                        ? ConnectionType.Bluetooth
                        : ConnectionType.Unknown,
                    IsConnected = bluetoothDevice.IsConnected,
                    IsPaired = bluetoothDevice.IsPaired,
                    CanUnpair = bluetoothDevice.CanUnpair
                });
            }
            else
            {
                LoggerService.Instance.Info(
                    hasUniqueBluetoothMatch
                        ? "No se clasificó el transporte SDL2 porque Windows no pudo consultar los dispositivos USB."
                        : "No se asoció el dispositivo Bluetooth con SDL2: la identidad no es única.");
                enriched.Add(device);
            }
        }

        return new GamepadEnrichmentResult(enriched, warnings);
    }

    private static string GetFamilyKey(GamepadDevice device)
    {
        return device.GamepadType switch
        {
            GamepadType.PlayStationDualShock4 => "PS4",
            GamepadType.PlayStationDualSense => "PS5",
            GamepadType.XboxOne or GamepadType.XboxSeries or GamepadType.Xbox360 => "XBOX",
            _ => NormalizeName(device.Name)
        };
    }

    private static string NormalizeName(string name)
    {
        return string.Concat(name.Where(char.IsLetterOrDigit)).ToUpperInvariant();
    }
}
