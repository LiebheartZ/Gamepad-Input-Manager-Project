using GDM.Models;

namespace GDM.Services.Abstractions;

public interface IGamepadInputService
{
    Task<GamepadInputSnapshot?> ReadInputAsync(
        string deviceKey,
        CancellationToken cancellationToken = default);

    Task<bool> RumbleAsync(
        string deviceKey,
        int lowMotor,
        int highMotor,
        int durationMilliseconds,
        CancellationToken cancellationToken = default);
}
