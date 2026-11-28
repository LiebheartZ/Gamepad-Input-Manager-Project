using GDM.Models;

namespace GDM.ViewModels;

public sealed record ControllerListItem(
    ConnectedGamepad? ConnectedDevice,
    ControllerRecord? SavedRecord)
{
    public string Name => ConnectedDevice?.Name ?? SavedRecord!.DisplayName;

    public string HardwareIdLabel =>
        ConnectedDevice?.HardwareIdLabel ?? GetSavedHardwareIdLabel(SavedRecord!);

    public string ConnectionText => ConnectedDevice?.IsVirtualGamepad == true
        ? "Mando virtual"
        : ConnectedDevice?.IsHiddenByGdm == true
            ? SavedRecord is null
                ? "Oculto por GDM"
                : "Oculto por GDM · Guardado"
            : SavedRecord is null
                ? "Conectado"
                : ConnectedDevice is null
                    ? "Desconectado · Guardado"
                    : "Conectado · Guardado";

    public string? BatteryInfoText => ConnectedDevice?.BatteryInfoText;
    public bool HasBatteryInfo => ConnectedDevice?.HasBatteryInfo == true;
    /// <summary>
    /// Color LED que GDM tiene aplicado ahora mismo en el mando (según su
    /// último color escrito). Null = el mando no tiene LED controlable o no
    /// está conectado; la vista lo muestra como cuadro blanco con raya roja.
    /// </summary>
    public System.Windows.Media.Brush? LedColorBrush { get; init; }
    public bool HasLedColor => LedColorBrush is not null;
    public string? ConnectionTransportText => ConnectedDevice?.ConnectionTransportText;
    public string? DeviceKey => ConnectedDevice?.DeviceKey;
    public bool IsConnected => ConnectedDevice is not null;
    public bool IsVirtualGamepad => ConnectedDevice?.IsVirtualGamepad == true;
    public bool CanSave =>
        ConnectedDevice is { IsHiddenByGdm: false, IsVirtualGamepad: false } && SavedRecord is null;
    public bool CanRemoveSaved => SavedRecord is not null;

    private static string GetSavedHardwareIdLabel(ControllerRecord record)
    {
        string? vendor = GetEvidence(record, "VendorId");
        string? product = GetEvidence(record, "ProductId");
        return ushort.TryParse(
                vendor,
                System.Globalization.NumberStyles.HexNumber,
                null,
                out ushort vendorId) &&
            ushort.TryParse(
                product,
                System.Globalization.NumberStyles.HexNumber,
                null,
                out ushort productId)
                ? $"VID: {vendorId:X4} · PID: {productId:X4}"
                : "Identificadores de hardware no disponibles";
    }

    private static string? GetEvidence(ControllerRecord record, string kind) =>
        record.Identity.Evidence
            .FirstOrDefault(evidence =>
                evidence.Source == "SDL2" &&
                evidence.Kind == kind)
            ?.Value;
}
