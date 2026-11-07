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
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, IReadOnlyList<GamepadDevice>>? previousProviderSnapshots = null)
    {
        var providerSnapshots = new Dictionary<string, IReadOnlyList<GamepadDevice>>(
            StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var failedProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                providerSnapshots[provider.Name] = validDevices;
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
                failedProviders.Add(provider.Name);
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

        if (previousProviderSnapshots != null)
        {
            foreach (string failedProvider in failedProviders)
            {
                if (previousProviderSnapshots.TryGetValue(failedProvider, out IReadOnlyList<GamepadDevice>? previous))
                {
                    providerSnapshots[failedProvider] = previous;
                }
            }
        }

        IReadOnlyList<GamepadDevice> normalized = RemoveDuplicateObservations(
            providerSnapshots.Values.SelectMany(snapshot => snapshot));
        GamepadEnrichmentResult enrichment = await _connectionEnricher
            .EnrichAsync(normalized, cancellationToken)
            .ConfigureAwait(false);
        warnings.AddRange(enrichment.Warnings);

        return new GamepadDetectionResult(
            enrichment.Devices,
            warnings,
            failedProviders,
            providerSnapshots);
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

}

public sealed class GamepadDetectionException(string message, Exception innerException)
    : Exception(message, innerException);
