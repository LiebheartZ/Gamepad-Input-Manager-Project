using GDMInputManager.Interfaces;
using GDMInputManager.Models;

namespace GDMInputManager.Services;

public sealed class GamepadDetectionService
{
    private readonly IReadOnlyList<IGamepadProvider> _providers;
    private readonly IGamepadConnectionEnricher _connectionEnricher;

    public GamepadDetectionService(
        IReadOnlyList<IGamepadProvider> providers,
        IGamepadConnectionEnricher connectionEnricher)
    {
        _providers = providers;
        _connectionEnricher = connectionEnricher;
    }

    public async Task<GamepadDetectionResult> EnumerateAsync(
        bool includeAvailableBluetooth,
        CancellationToken cancellationToken)
    {
        var devices = new List<GamepadDevice>();
        var warnings = new List<string>();
        int successfulProviders = 0;

        foreach (IGamepadProvider provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                IReadOnlyList<GamepadDevice> providerDevices = provider is IOptionalGamepadProvider optionalProvider
                    ? await optionalProvider.EnumerateAsync(includeAvailableBluetooth, cancellationToken).ConfigureAwait(false)
                    : await provider.EnumerateAsync(cancellationToken).ConfigureAwait(false);

                successfulProviders++;
                GamepadDevice[] validDevices = providerDevices.Where(device =>
                    !string.IsNullOrWhiteSpace(device.DeviceKey) &&
                    !string.IsNullOrWhiteSpace(device.Name)).ToArray();
                devices.AddRange(validDevices);
                LoggerService.Instance.Info(
                    $"Provider '{provider.Name}': {validDevices.Length} dispositivo(s), " +
                    $"{validDevices.Count(device => device.IsConnected)} conectado(s), " +
                    $"{validDevices.Count(device => device.IsPaired)} emparejado(s).");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                string warning = $"Falló el provider '{provider.Name}': {ex.Message}";
                warnings.Add(warning);
                LoggerService.Instance.Error(warning);
            }
        }

        if (successfulProviders == 0)
        {
            Exception cause = warnings.Count > 0
                ? new AggregateException(warnings.Select(warning => new InvalidOperationException(warning)))
                : new InvalidOperationException("No hay proveedores de detección disponibles.");
            throw new GamepadDetectionException(
                "No se pudo completar la detección de mandos.",
                cause);
        }

        IReadOnlyList<GamepadDevice> normalized = RemoveDuplicateObservations(devices);
        GamepadEnrichmentResult enrichment = await _connectionEnricher
            .EnrichAsync(normalized, cancellationToken)
            .ConfigureAwait(false);
        warnings.AddRange(enrichment.Warnings);

        return new GamepadDetectionResult(
            MergeSharedIdentities(enrichment.Devices),
            warnings);
    }

    private static IReadOnlyList<GamepadDevice> RemoveDuplicateObservations(
        IEnumerable<GamepadDevice> devices)
    {
        var result = new List<GamepadDevice>();
        var runtimeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (GamepadDevice device in devices)
        {
            string providerKey = $"{device.Provider}:{device.DeviceKey}";
            if (runtimeKeys.Add(providerKey))
            {
                result.Add(device);
            }
        }

        return result;
    }

    private static IReadOnlyList<GamepadDevice> MergeSharedIdentities(
        IReadOnlyList<GamepadDevice> devices)
    {
        var merged = new List<GamepadDevice>();
        var consumed = new HashSet<int>();

        for (int index = 0; index < devices.Count; index++)
        {
            if (!consumed.Add(index))
            {
                continue;
            }

            GamepadDevice current = devices[index];
            var matches = Enumerable.Range(index + 1, devices.Count - index - 1)
                .Where(candidateIndex =>
                    !consumed.Contains(candidateIndex) &&
                    devices[candidateIndex].Provider != current.Provider &&
                    HasSharedStrongIdentity(current.Identity, devices[candidateIndex].Identity))
                .ToList();

            if (matches.Count == 1)
            {
                int matchIndex = matches[0];
                consumed.Add(matchIndex);
                current = Merge(current, devices[matchIndex]);
            }

            merged.Add(current);
        }

        return merged;
    }

    private static bool HasSharedStrongIdentity(GamepadIdentity first, GamepadIdentity second)
    {
        return SameNonEmpty(first.SerialNumber, second.SerialNumber) ||
               SameMacAddress(first.BluetoothAddress, second.BluetoothAddress) ||
               SameNonEmpty(first.InstancePath, second.InstancePath);
    }

    private static GamepadDevice Merge(GamepadDevice first, GamepadDevice second)
    {
        GamepadDevice preferred = first.Provider == "SDL2" ? first : second.Provider == "SDL2" ? second : first;
        GamepadDevice supplemental = ReferenceEquals(preferred, first) ? second : first;
        return preferred with
        {
            Identity = new GamepadIdentity
            {
                SerialNumber = preferred.Identity.SerialNumber ?? supplemental.Identity.SerialNumber,
                BluetoothAddress = preferred.Identity.BluetoothAddress ?? supplemental.Identity.BluetoothAddress,
                InstancePath = preferred.Identity.InstancePath ?? supplemental.Identity.InstancePath
            },
            ConnectionType = preferred.ConnectionType != ConnectionType.Unknown
                ? preferred.ConnectionType
                : supplemental.ConnectionType,
            IsConnected = preferred.IsConnected || supplemental.IsConnected,
            IsPaired = preferred.IsPaired || supplemental.IsPaired,
            CanUnpair = preferred.CanUnpair || supplemental.CanUnpair
        };
    }

    private static bool SameNonEmpty(string? first, string? second)
    {
        return !string.IsNullOrWhiteSpace(first) &&
               !string.IsNullOrWhiteSpace(second) &&
               string.Equals(first.Trim(), second.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameMacAddress(string? first, string? second)
    {
        static string Normalize(string? address)
        {
            return string.Concat((address ?? string.Empty).Where(Uri.IsHexDigit)).ToUpperInvariant();
        }

        string normalizedFirst = Normalize(first);
        return normalizedFirst.Length == 12 && normalizedFirst == Normalize(second);
    }
}

public sealed class GamepadDetectionException(string message, Exception innerException)
    : Exception(message, innerException);
