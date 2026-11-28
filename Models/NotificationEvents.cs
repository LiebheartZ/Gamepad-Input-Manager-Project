namespace GDM.Models;

/// <summary>
/// Catálogo de eventos que pueden mostrar notificación toast. Cada evento
/// tiene un identificador estable que se guarda en los ajustes
/// (<see cref="AppSettings.NotificationEventStates"/>) para que el usuario
/// pueda activarlo o desactivarlo desde Ajustes → Interfaz.
/// </summary>
public static class NotificationEvents
{
    public sealed record Definition(string Id, string Label, bool DefaultEnabled = true);

    public const string ControllerConnected = "controller-connected";
    public const string ControllerDisconnected = "controller-disconnected";
    public const string BluetoothOff = "bluetooth-off";
    public const string PairingRequested = "pairing-requested";
    public const string DevicePaired = "device-paired";
    public const string ControllerInfo = "controller-info";
    public const string ControllerSaved = "controller-saved";
    public const string VirtualTypeChanged = "virtual-type-changed";
    public const string LowBattery = "low-battery";
    public const string VirtualOn = "virtual-on";
    public const string VirtualOff = "virtual-off";

    public static readonly IReadOnlyList<Definition> All =
    [
        new(ControllerConnected, "Conexión de mando"),
        new(ControllerDisconnected, "Desconexión de mando"),
        new(BluetoothOff, "Bluetooth desactivado"),
        // El aviso «Emparejamiento solicitado» es ruidoso (sale en cada
        // intento automático); por defecto viene desactivado.
        new(PairingRequested, "Emparejamiento solicitado", DefaultEnabled: false),
        new(DevicePaired, "Mando emparejado"),
        new(ControllerInfo, "Información de mando por combo"),
        new(ControllerSaved, "Mando guardado"),
        new(VirtualTypeChanged, "Tipo de mando virtual"),
        new(LowBattery, "Batería baja"),
        new(VirtualOn, "Mando virtual encendido"),
        new(VirtualOff, "Mando virtual apagado")
    ];

    /// <summary>
    /// Indica si el evento está activado según los estados guardados; si el
    /// usuario nunca lo tocó se usa el valor por defecto del catálogo.
    /// </summary>
    public static bool IsEnabled(IReadOnlyDictionary<string, bool>? states, string eventId)
    {
        if (states is not null && states.TryGetValue(eventId, out bool value))
        {
            return value;
        }

        return All.FirstOrDefault(definition => definition.Id == eventId)?.DefaultEnabled ?? true;
    }

    /// <summary>Copia los estados guardados aplicando el nuevo valor.</summary>
    public static Dictionary<string, bool> WithState(
        IReadOnlyDictionary<string, bool>? states, string eventId, bool enabled)
    {
        var copy = states is null
            ? new Dictionary<string, bool>(StringComparer.Ordinal)
            : new Dictionary<string, bool>(states, StringComparer.Ordinal);
        copy[eventId] = enabled;
        return copy;
    }
}
