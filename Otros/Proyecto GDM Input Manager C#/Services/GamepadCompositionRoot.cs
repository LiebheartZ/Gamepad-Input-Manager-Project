using GDMInputManager.Data;
using GDMInputManager.Infrastructure.Persistence;
using GDMInputManager.Infrastructure.SDL2;
using GDMInputManager.Infrastructure.Windows;
using GDMInputManager.Interfaces;

namespace GDMInputManager.Services;

public static class GamepadCompositionRoot
{
    public static IGamepadManager CreateDefault()
    {
        var dataManager = new JsonDataManager();
        var providers = new IGamepadProvider[]
        {
            new SDL2GamepadProvider(),
            new WindowsBluetoothGamepadProvider()
        };
        return new GamepadManager(
            providers,
            new JsonGamepadRepository(dataManager),
            new WindowsGamepadConnectionMetadataProvider(),
            new WindowsDeviceChangeMonitor(),
            new WindowsBluetoothDeviceActions());
    }
}
