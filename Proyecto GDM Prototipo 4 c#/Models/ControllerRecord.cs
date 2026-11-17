namespace GDM.Models;

public sealed record ControllerRecord
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public Guid ControllerId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public ControllerIdentity Identity { get; init; } = new();
    public Guid? ProfileId { get; init; }
    public Dictionary<string, string> ButtonMappingOverrides { get; init; } = [];
    public Dictionary<string, string> AxisMappingOverrides { get; init; } = [];
    public Dictionary<string, double> DeadZoneOverrides { get; init; } = [];
    public ControllerSettings Settings { get; init; } = new();
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record ControllerIdentity
{
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? BluetoothMac { get; init; }
    public string? SdlGuid { get; init; }
    public IReadOnlyList<ControllerIdentityEvidence> Evidence { get; init; } = [];
}

public sealed record ControllerSettings
{
    public bool VibrationEnabled { get; init; } = true;
    public bool AutoStartVirtualGamepad { get; init; }
    public bool AutoHidePhysicalGamepad { get; init; }
    public bool KeepHiddenOnExit { get; init; }
    public ControllerLedSettings Led { get; init; } = ControllerLedSettings.Default;
}

public sealed record ControllerIdentityEvidence
{
    public string Source { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
}

public static class ControllerLedModes
{
    public const string Off = "off";
    public const string Player = "player";
    public const string Manual = "manual";
    public const string VirtualOutput = "virtual";

    public static bool IsSupported(string? value) =>
        value is Off or Player or Manual or VirtualOutput;

    public static string Normalize(string? value) =>
        value is Player or Manual or VirtualOutput ? value : Off;
}

public enum SonyControllerLedKind
{
    None,
    DualShock4,
    DualSense
}

public static class SonyControllerLedSupport
{
    public static bool TryGetKind(ushort? vendorId, ushort? productId, out SonyControllerLedKind kind)
    {
        kind = vendorId == 0x054C && productId is 0x05C4 or 0x09CC
            ? SonyControllerLedKind.DualShock4
            : vendorId == 0x054C && productId is 0x0CE6 or 0x0DF2
                ? SonyControllerLedKind.DualSense
                : SonyControllerLedKind.None;
        return kind != SonyControllerLedKind.None;
    }

    public static string Describe(SonyControllerLedKind kind) => kind switch
    {
        SonyControllerLedKind.DualShock4 => "DualShock 4",
        SonyControllerLedKind.DualSense => "DualSense",
        _ => "sin soporte de LED"
    };
}

public sealed record ControllerLedSettings
{
    public const int MinBrightness = 0;
    public const int MaxBrightness = 255;
    public const int MinPlayerNumber = 1;
    public const int MaxPlayerNumber = 4;

    public static ControllerLedSettings Default { get; } = new()
    {
        PlayerColors = DefaultPlayerColors()
    };

    public string Mode { get; init; } = ControllerLedModes.Off;
    public string? ManualColor { get; init; }
    public int Brightness { get; init; } = 200;
    public int PlayerNumber { get; init; } = 1;
    public Dictionary<int, string> PlayerColors { get; init; } = [];

    public static Dictionary<int, string> DefaultPlayerColors() => new()
    {
        [1] = "1565C0",
        [2] = "C62828",
        [3] = "2E7D32",
        [4] = "AD1457"
    };

    public static ControllerLedSettings Normalize(ControllerLedSettings? settings)
    {
        settings ??= Default;
        string mode = ControllerLedModes.Normalize(settings.Mode);
        string? manualColor = ControllerLedColor.NormalizeHex(settings.ManualColor) ?? "FFFFFF";
        var playerColors = new Dictionary<int, string>();
        foreach ((int player, string? hex) in settings.PlayerColors ?? [])
        {
            if (player is >= MinPlayerNumber and <= MaxPlayerNumber &&
                ControllerLedColor.NormalizeHex(hex) is { } validHex)
            {
                playerColors[player] = validHex;
            }
        }

        foreach ((int player, string hex) in DefaultPlayerColors())
        {
            playerColors.TryAdd(player, hex);
        }

        return new ControllerLedSettings
        {
            Mode = mode,
            ManualColor = manualColor,
            Brightness = Math.Clamp(settings.Brightness, MinBrightness, MaxBrightness),
            PlayerNumber = Math.Clamp(settings.PlayerNumber, MinPlayerNumber, MaxPlayerNumber),
            PlayerColors = playerColors
        };
    }
}

public static class ControllerLedColor
{
    public static string? NormalizeHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim().TrimStart('#');
        return trimmed.Length == 6 &&
               trimmed.All(Uri.IsHexDigit)
            ? trimmed.ToUpperInvariant()
            : null;
    }

    public static bool TryParse(string? hex, out byte red, out byte green, out byte blue)
    {
        red = green = blue = 0;
        string? normalized = NormalizeHex(hex);
        if (normalized is null)
        {
            return false;
        }

        red = Convert.ToByte(normalized[..2], 16);
        green = Convert.ToByte(normalized.Substring(2, 2), 16);
        blue = Convert.ToByte(normalized[4..], 16);
        return true;
    }

    public static string ToHex(byte red, byte green, byte blue) =>
        $"{red:X2}{green:X2}{blue:X2}";

    public static string ToHex(System.Drawing.Color color) =>
        ToHex(color.R, color.G, color.B);
}
