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

    /// <param name="force">
    /// Si es true, escribe el reporte aunque coincida con el último aplicado.
    /// Se usa en el refresco periódico: el mando puede perder el estado del
    /// LED (reconexión, suspensión, paquete descartado) y hay que re-afirmarlo.
    /// </param>
    Task<SonyLedApplyResult> TrySetLightBarAsync(
        ConnectedGamepad device,
        byte red,
        byte green,
        byte blue,
        byte brightness,
        int? playerNumber,
        bool force = false,
        CancellationToken cancellationToken = default);
}
