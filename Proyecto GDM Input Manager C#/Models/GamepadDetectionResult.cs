namespace GDMInputManager.Models;

public sealed record GamepadDetectionResult(
    IReadOnlyList<GamepadDevice> Devices,
    IReadOnlyList<string> Warnings);
