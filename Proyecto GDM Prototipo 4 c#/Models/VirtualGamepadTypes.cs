namespace GDM.Models;

public sealed record VirtualGamepadTypeOption(string Id, string DisplayName);

public static class VirtualGamepadTypes
{
    public const string Xbox360 = "xbox360";
    public const string DualShock4 = "dualshock4";
    public const string Default = Xbox360;

    public static IReadOnlyList<VirtualGamepadTypeOption> Options { get; } =
    [
        new(Xbox360, "Xbox 360"),
        new(DualShock4, "DualShock 4")
    ];

    public static bool IsSupported(string? type) =>
        string.Equals(type, Xbox360, StringComparison.Ordinal) ||
        string.Equals(type, DualShock4, StringComparison.Ordinal);

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
        _ => "Desconocido"
    };

    public static string HidMaestroProfileId(string type)
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
            Xbox360 => "xbox-360-wired",
            DualShock4 => "dualshock-4-v2",
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };
    }
}
