namespace GDM.Models;

public sealed record ConnectedGamepad
{
    public required string DeviceKey { get; init; }
    public required string Name { get; init; }
    public ushort? VendorId { get; init; }
    public ushort? ProductId { get; init; }
    public string? SerialNumber { get; init; }
    public string? DevicePath { get; init; }
    public string HardwareIdLabel =>
        VendorId is ushort vendorId && ProductId is ushort productId
            ? $"VID: {vendorId:X4} · PID: {productId:X4}"
            : "Identificadores de hardware no disponibles";
}
