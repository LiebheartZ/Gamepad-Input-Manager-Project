namespace GDMInputManager.Models;

public sealed record GamepadDetectionResult(
    IReadOnlyList<GamepadDevice> Devices,
    IReadOnlyList<string> Warnings,
    IReadOnlySet<string> FailedProviders,
    IReadOnlyDictionary<string, IReadOnlyList<GamepadDevice>> ProviderSnapshots);
