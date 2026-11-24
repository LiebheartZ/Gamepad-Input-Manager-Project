namespace GDM.Models;

public sealed record ConnectedGamepad
{
    public required string DeviceKey { get; init; }
    public required string Name { get; init; }
    public ushort? VendorId { get; init; }
    public ushort? ProductId { get; init; }
    public string? SerialNumber { get; init; }
    public string? DevicePath { get; init; }
    public string? SdlGuid { get; init; }
    public int PhysicalAxisCount { get; init; }
    public int PhysicalButtonCount { get; init; }
    public bool HasStandardMapping { get; init; }
    public bool IsRedundantInterface { get; init; }
    public bool IsHiddenByGdm { get; init; }
    public bool IsVirtualGamepad { get; init; }
    public GamepadConnectionTransport ConnectionTransport { get; init; } = GamepadConnectionTransport.Unknown;
    public GamepadBatteryLevel BatteryLevel { get; init; } = GamepadBatteryLevel.Unknown;
    public int? BatteryPercent { get; init; }
    public bool BatteryCharging { get; init; }
    public string? VirtualLabel => IsVirtualGamepad ? "Mando virtual" : null;
    public string? ConnectionTransportText => ConnectionTransport switch
    {
        GamepadConnectionTransport.Usb => "USB",
        GamepadConnectionTransport.Bluetooth => "Bluetooth",
        _ => null
    };

    public string BatteryInfoText
    {
        get
        {
            string? transport = ConnectionTransportText;
            if (BatteryPercent is int percent)
            {
                string core = BatteryCharging
                    ? $"cargando {percent}%"
                    : BatteryLevel == GamepadBatteryLevel.Wired
                        ? "con cable"
                        : $"{percent}%";
                return transport is null
                    ? $"Batería: {core}"
                    : $"Batería: {core} · {transport}";
            }

            string levelText = BatteryLevel switch
            {
                GamepadBatteryLevel.Empty => "vacía",
                GamepadBatteryLevel.Low => "baja",
                GamepadBatteryLevel.Medium => "media",
                GamepadBatteryLevel.Full => "llena",
                GamepadBatteryLevel.Charging => "cargando",
                GamepadBatteryLevel.Wired => "con cable",
                _ => "sin datos"
            };
            if (BatteryLevel == GamepadBatteryLevel.Unknown && transport is null)
            {
                return "Batería: sin datos";
            }

            return transport is null
                ? $"Batería: {levelText}"
                : $"Batería: {levelText} · {transport}";
        }
    }

    public bool HasBatteryInfo =>
        BatteryLevel != GamepadBatteryLevel.Unknown ||
        BatteryPercent is not null ||
        ConnectionTransportText is not null;

    public static string BatteryLevelDisplayName(GamepadBatteryLevel level) => level switch
    {
        GamepadBatteryLevel.Empty => "Vacío",
        GamepadBatteryLevel.Low => "Baja",
        GamepadBatteryLevel.Medium => "Media",
        GamepadBatteryLevel.Full => "Llena",
        GamepadBatteryLevel.Charging => "Cargando",
        GamepadBatteryLevel.Wired => "Con cable",
        _ => "Desconocido"
    };
    public string HardwareIdLabel =>
        VendorId is ushort vendorId && ProductId is ushort productId
            ? $"VID: {vendorId:X4} · PID: {productId:X4}"
            : "Identificadores de hardware no disponibles";
}
