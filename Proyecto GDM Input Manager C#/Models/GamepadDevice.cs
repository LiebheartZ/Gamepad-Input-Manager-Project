namespace GDMInputManager.Models;

public sealed record GamepadDevice
{
    public required string DeviceKey { get; init; }
    public required string Provider { get; init; }
    public int? ProviderIndex { get; init; }
    public required string Name { get; init; }
    public ushort? Vid { get; init; }
    public ushort? Pid { get; init; }
    public GamepadIdentity Identity { get; init; } = new();
    public ConnectionType ConnectionType { get; init; } = ConnectionType.Unknown;
    public GamepadType GamepadType { get; init; } = GamepadType.Unknown;
    public bool IsConnected { get; init; } = true;
    public bool IsPaired { get; init; }
    public bool CanUnpair { get; init; }
    public DateTimeOffset FirstSeenUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenUtc { get; init; } = DateTimeOffset.UtcNow;
}
