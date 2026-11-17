namespace GDMInputManager.Models;

public sealed record GamepadEnrichmentResult(
    IReadOnlyList<GamepadDevice> Devices,
    IReadOnlyList<string> Warnings);
