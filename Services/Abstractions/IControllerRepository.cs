using GDM.Models;

namespace GDM.Services.Abstractions;

public interface IControllerRepository
{
    Task<IReadOnlyList<ControllerRecord>> LoadAllAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ControllerRecord controller, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid controllerId, CancellationToken cancellationToken = default);
}
