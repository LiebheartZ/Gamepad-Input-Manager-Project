using GDM.Models;

namespace GDM.Services.Abstractions;

public enum ControllerPowerOffStatus
{
    PoweredOff,
    NotConnected,
    UnsupportedDevice,
    Failed
}

public sealed record ControllerPowerOffResult(
    ControllerPowerOffStatus Status,
    string? Detail = null);

public interface IControllerPowerOffService
{
    Task<bool> SupportsPowerOffAsync(
        ConnectedGamepad device,
        CancellationToken cancellationToken = default);

    Task<ControllerPowerOffResult> TryPowerOffAsync(
        ConnectedGamepad device,
        CancellationToken cancellationToken = default);
}
