namespace GDM.Models;

public sealed record GamepadInputSnapshot
{
    public required bool IsStandardGameController { get; init; }
    public required IReadOnlyList<GamepadAxisState> Axes { get; init; }
    public required IReadOnlyList<GamepadButtonState> Buttons { get; init; }
    public int PhysicalAxisCount { get; init; }
    public int PhysicalButtonCount { get; init; }
}

public sealed record GamepadAxisState(string Name, double Value, string DisplayValue);

public sealed record GamepadButtonState(string Name, bool IsPressed);
