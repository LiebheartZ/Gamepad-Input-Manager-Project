using System.Globalization;

namespace GDM.Models;

public sealed record VirtualGamepadTypeOption(string Id, string DisplayName);

public static class VirtualGamepadTypes
{
    public const string Xbox360 = "xbox360";
    public const string Auto = "auto";
    public const string DualShock4 = "dualshock4";
    public const string Default = Xbox360;

    public const string Xbox360VariantWired = "wired";
    public const string Xbox360VariantType2 = "type2";
    public const string Xbox360VariantDefault = Xbox360VariantWired;

    public static IReadOnlyList<VirtualGamepadTypeOption> Options { get; } =
    [
        new(Auto, "Automático"),
        new(Xbox360, "Xbox 360"),
        new(DualShock4, "DualShock 4")
    ];

    public static IReadOnlyList<VirtualGamepadTypeOption> Xbox360VariantOptions { get; } =
    [
        new(Xbox360VariantWired, "xbox-360-wired (actual)"),
        new(Xbox360VariantType2, "xbox-360-type2 (alternativo)")
    ];

    public static string NormalizeXbox360Variant(string? variant) =>
        string.Equals(variant, Xbox360VariantType2, StringComparison.Ordinal)
            ? Xbox360VariantType2
            : Xbox360VariantDefault;

    public static bool IsSupported(string? type) =>
        string.Equals(type, Xbox360, StringComparison.Ordinal) ||
        string.Equals(type, DualShock4, StringComparison.Ordinal);

    public static bool IsAuto(string? type) =>
        string.Equals(type, Auto, StringComparison.Ordinal);

    public static string Normalize(string? type) =>
        IsSupported(type) ? type! : Default;

    public static string Other(string type)
    {
        string normalized = Normalize(type);
        return normalized == Xbox360 ? DualShock4 : Xbox360;
    }

    public static string DisplayName(string? type) => type switch
    {
        Xbox360 => "Xbox 360",
        DualShock4 => "DualShock 4",
        Auto => "Automático",
        _ => "Desconocido"
    };

    public static string? InferFromController(ControllerRecord? record)
    {
        if (record is null)
        {
            return null;
        }

        if (TryGetVendorId(record, out ushort vendorId))
        {
            if (vendorId == 0x045E)
            {
                return Xbox360;
            }

            if (vendorId == 0x054C)
            {
                return DualShock4;
            }
        }

        string name = $"{record.Identity.Model} {record.DisplayName}";
        if (name.Contains("xbox", StringComparison.OrdinalIgnoreCase))
        {
            return Xbox360;
        }

        if (name.Contains("sony", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("dualshock", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("dualsense", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("ps4", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("ps5", StringComparison.OrdinalIgnoreCase))
        {
            return DualShock4;
        }

        return null;
    }

    public static string? InferFromConnectedGamepad(ConnectedGamepad? device)
    {
        if (device is null)
        {
            return null;
        }

        if (device.VendorId == 0x045E)
        {
            return Xbox360;
        }

        if (device.VendorId == 0x054C)
        {
            return DualShock4;
        }

        string name = device.Name ?? string.Empty;
        if (name.Contains("xbox", StringComparison.OrdinalIgnoreCase))
        {
            return Xbox360;
        }

        if (name.Contains("sony", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("dualshock", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("dualsense", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("ps4", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("ps5", StringComparison.OrdinalIgnoreCase))
        {
            return DualShock4;
        }

        return null;
    }

    private static bool TryGetVendorId(ControllerRecord record, out ushort vendorId)
    {
        vendorId = 0;
        string? value = record.Identity.Evidence
            .FirstOrDefault(evidence =>
                evidence.Source == "SDL2" && evidence.Kind == "VendorId")
            ?.Value;
        return value is not null &&
            ushort.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vendorId);
    }
    public static string HidMaestroProfileId(string type) =>
        HidMaestroProfileId(type, Xbox360VariantDefault);

    public static string HidMaestroProfileId(string type, string? xbox360Variant)
    {
        if (!IsSupported(type))
        {
            throw new ArgumentOutOfRangeException(
                nameof(type),
                type,
                "Tipo de mando virtual no compatible.");
        }

        return type switch
        {
            Xbox360 => NormalizeXbox360Variant(xbox360Variant) == Xbox360VariantType2
                ? "xbox-360-type2"
                : "xbox-360-wired",
            DualShock4 => "dualshock-4-v2",
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };
    }
}
