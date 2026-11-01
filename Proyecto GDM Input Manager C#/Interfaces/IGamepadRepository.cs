using GDMInputManager.Models;

namespace GDMInputManager.Interfaces;

public interface IGamepadRepository
{
    event Action<string>? ReadError;
    Task<IReadOnlyList<GamepadRecord>> GetAllAsync(CancellationToken cancellationToken);
    Task<GamepadRecord?> GetByIdAsync(Guid recordId, CancellationToken cancellationToken);
    Task AddAsync(GamepadRecord record, CancellationToken cancellationToken);
    Task UpdateAsync(GamepadRecord record, CancellationToken cancellationToken);
    Task DeleteAsync(Guid recordId, CancellationToken cancellationToken);
}
