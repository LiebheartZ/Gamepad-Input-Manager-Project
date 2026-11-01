namespace GDMInputManager.Models;

public enum GamepadConnectionState
{
    Connected,
    Disconnected,
    Unknown
}

public sealed record GamepadListItem
{
    public Guid? RecordId { get; init; }
    public required string DisplayName { get; init; }
    public string ModelName { get; init; } = string.Empty;
    public GamepadType GamepadType { get; init; } = GamepadType.Unknown;
    public ConnectionType ConnectionType { get; init; } = ConnectionType.Unknown;
    public GamepadConnectionState ConnectionStatus { get; init; } = GamepadConnectionState.Unknown;
    public bool IsConnected => ConnectionStatus == GamepadConnectionState.Connected;
    public bool IsSaved => RecordId.HasValue;
    public string? DeviceKey { get; init; }
    public string? BluetoothAddress { get; init; }
    public string? SerialNumber { get; init; }
    public DateTimeOffset? LastSeenUtc { get; init; }
    public string? ActiveProfileId { get; init; }
    public bool IsPaired { get; init; }
    public bool CanUnpair { get; init; }
}
