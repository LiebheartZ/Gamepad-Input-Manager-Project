namespace GDM.Models;

/// <summary>
/// Detecta mandos físicos que exponen la API XInput. HidHide solo oculta
/// interfaces HID; XInput es una API distinta que los juegos pueden leer
/// aunque la interfaz HID del mando esté oculta. En títulos como EA SPORTS
/// FC eso produce el bug «ghost controller»: el juego ve el mando físico
/// (vía XInput) y el virtual a la vez, con doble entrada y spam de botones.
/// </summary>
public static class XInputExposure
{
    // VID 0x045E (Microsoft) identifica mandos Xbox y compatibles que
    // siempre exponen XInput, tanto por USB como por Bluetooth.
    private const int MicrosoftVendorId = 0x045E;

    public static bool IsLikelyXInput(ushort? vendorId) =>
        vendorId == MicrosoftVendorId;
}
