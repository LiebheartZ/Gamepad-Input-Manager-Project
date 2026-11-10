using GDM.Models;

namespace GDM.Services.Abstractions;

public interface IGamepadInputService
{
    Task<GamepadInputSnapshot?> ReadInputAsync(
        string deviceKey,
        CancellationToken cancellationToken = default);
}
