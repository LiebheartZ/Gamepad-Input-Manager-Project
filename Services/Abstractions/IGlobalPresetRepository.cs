using GDM.Models;

namespace GDM.Services.Abstractions;

public interface IGlobalPresetRepository
{
    Task<GlobalPreset> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(GlobalPreset preset, CancellationToken cancellationToken = default);
}
