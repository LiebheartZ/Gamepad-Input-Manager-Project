namespace GDMInputManager.Models;

public sealed record GamepadRecord
{
    public Guid RecordId { get; init; } = Guid.NewGuid();
    public string DisplayName { get; init; } = string.Empty;
    public GamepadType GamepadType { get; init; } = GamepadType.Unknown;
    public string ModelName { get; init; } = string.Empty;
    public ushort? VendorId { get; init; }
    public ushort? ProductId { get; init; }
    public GamepadIdentity Identity { get; init; } = new();
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSeenAtUtc { get; init; }
    public string? ActiveProfileId { get; init; }
    public List<string> EnabledFeatures { get; init; } = new();
    public Dictionary<string, object?> Settings { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
