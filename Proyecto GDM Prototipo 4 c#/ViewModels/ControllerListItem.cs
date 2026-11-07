using GDM.Models;

namespace GDM.ViewModels;

public sealed record ControllerListItem(
    ConnectedGamepad? ConnectedDevice,
    ControllerRecord? SavedRecord)
{
    public string Name => ConnectedDevice?.Name ?? SavedRecord!.DisplayName;

    public string HardwareIdLabel =>
        ConnectedDevice?.HardwareIdLabel ?? GetSavedHardwareIdLabel(SavedRecord!);

    public string ConnectionText => SavedRecord is null
        ? "Conectado"
        : ConnectedDevice is null
            ? "Desconectado · Guardado"
            : "Conectado · Guardado";

    public string? DeviceKey => ConnectedDevice?.DeviceKey;
    public bool IsConnected => ConnectedDevice is not null;
    public bool CanSave => ConnectedDevice is not null && SavedRecord is null;
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
