using GDM.Models;

namespace GDM.Services.Abstractions;

public interface IProfileRepository
{
    Task<IReadOnlyList<ControllerProfile>> LoadAllAsync(
        CancellationToken cancellationToken = default);
    Task SaveAsync(ControllerProfile profile, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default);
}
