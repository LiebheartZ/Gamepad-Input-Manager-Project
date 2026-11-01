namespace GDMInputManager.Models;

public sealed record GamepadIdentity
{
    public string? SerialNumber { get; init; }
    public string? BluetoothAddress { get; init; }
    public string? InstancePath { get; init; }
}
