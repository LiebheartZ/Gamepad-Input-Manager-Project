namespace GDM.Models;

public static class BluetoothGamepadCatalog
{
    private static readonly string[] KnownGamepadPatterns =
    [
        "DualSense",
        "DualShock",
        "Wireless Controller",
        "Xbox",
        "Switch Pro",
        "Gamepad",
        "8BitDo",
        "Hori",
        "Razer",
        "SCUF",
        "Nintendo",
        "Controller",
        "Joystick",
        "Game Controller",
        "PS5",
        "PS4",
        "PlayStation",
        "XInput",
        "Dual",
        "Edge",
        "Elite",
        "Adaptive",
        "Pro Controller",
        "Joy-Con",
        "GameCube",
        "N64",
        "SNES",
        "Mega Drive",
        "Genesis",
        "Stadia",
        "Luna",
        "Nimbus",
        "Stratus",
        "SteelSeries",
        "Logitech",
        "Thrustmaster",
        "Fanatec",
        "MOGA",
        "PowerA",
        "PDP",
        "Bigben",
        "Nacon",
        "Ryochan7",
        "ViGEm",
        "DS4",
        "DS5"
    ];

    public static bool IsRecognizedGamepad(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return false;
        }

        string name = deviceName.ToLowerInvariant();
        return KnownGamepadPatterns.Any(pattern =>
            name.Contains(pattern.ToLowerInvariant()));
    }
}
