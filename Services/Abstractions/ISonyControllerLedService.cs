using GDM.Models;

namespace GDM.Services.Abstractions;

public enum SonyLedApplyResult
{
    Applied,
    UnsupportedDevice,
    Failed
}

public interface ISonyControllerLedService
{
    string? LastFailureDetail { get; }

    Task<SonyLedApplyResult> TrySetLightBarAsync(
        ConnectedGamepad device,
        byte red,
        byte green,
        byte blue,
        byte brightness,
        int? playerNumber,
        CancellationToken cancellationToken = default);
}
