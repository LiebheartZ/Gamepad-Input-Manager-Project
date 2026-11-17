using GDMInputManager.Models;

namespace GDMInputManager.Interfaces;

public interface IGamepadProvider : IDisposable
{
    string Name { get; }
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<GamepadDevice>> EnumerateAsync(CancellationToken cancellationToken);
}

public interface IOptionalGamepadProvider
{
    Task<IReadOnlyList<GamepadDevice>> EnumerateAsync(
        bool includeAvailableDevices,
        CancellationToken cancellationToken);
}

public interface IGamepadConnectionEnricher
{
    Task<GamepadEnrichmentResult> EnrichAsync(
        IReadOnlyList<GamepadDevice> devices,
        CancellationToken cancellationToken);
}
