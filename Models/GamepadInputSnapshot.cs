namespace GDM.Models;

public sealed record GamepadInputSnapshot
{
    public required bool IsStandardGameController { get; init; }
    public required IReadOnlyList<GamepadAxisState> Axes { get; init; }
    public required IReadOnlyList<GamepadButtonState> Buttons { get; init; }
    public int PhysicalAxisCount { get; init; }
    public int PhysicalButtonCount { get; init; }
    public MotionSensorData? Motion { get; init; }
    public TouchpadState? Touchpad { get; init; }
    public GamepadBatteryLevel BatteryLevel { get; init; } = GamepadBatteryLevel.Unknown;
    public int? BatteryPercent { get; init; }
    public bool BatteryCharging { get; init; }
}

public enum GamepadBatteryLevel
{
    Unknown = 0,
    Empty = 1,
    Low = 2,
    Medium = 3,
    Full = 4,
    Charging = 5,
    Wired = 6
}

public enum GamepadConnectionTransport
{
    Unknown = 0,
    Usb = 1,
    Bluetooth = 2
}

public sealed record MotionSensorData
{
    public bool HasAccelerometer { get; init; }
    public bool HasGyroscope { get; init; }
    public bool AccelerometerEnabled { get; init; }
    public bool GyroscopeEnabled { get; init; }
    public double AccelXMs2 { get; init; }
    public double AccelYMs2 { get; init; }
    public double AccelZMs2 { get; init; }
    public double GyroPitchRadS { get; init; }
    public double GyroYawRadS { get; init; }
    public double GyroRollRadS { get; init; }
}

public sealed record TouchpadFingerState
{
    public bool Active { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public int Id { get; init; }
    public float Pressure { get; init; }
}

public sealed record TouchpadState
{
    public bool HasTouchpad { get; init; }
    public TouchpadFingerState? Finger0 { get; init; }
    public TouchpadFingerState? Finger1 { get; init; }
    public string? Diagnostics { get; init; }
}

public sealed record GamepadAxisState(string Name, double Value)
{
    public string DisplayValue =>
        Value.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record GamepadButtonState(string Name, bool IsPressed);
