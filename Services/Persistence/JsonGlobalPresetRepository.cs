using System.IO;
using GDM.Models;
using GDM.Services.Abstractions;

namespace GDM.Services.Persistence;

public sealed class JsonGlobalPresetRepository : IGlobalPresetRepository
{
    private readonly IStoragePathService _paths;
    private readonly JsonFileStore _fileStore = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonGlobalPresetRepository(IStoragePathService paths)
    {
        _paths = paths;
    }

    public async Task<GlobalPreset> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_paths.GlobalPresetFilePath))
            {
                return GlobalPreset.Default;
            }

            GlobalPreset preset = await _fileStore.ReadAsync<GlobalPreset>(
                _paths.GlobalPresetFilePath,
                cancellationToken);
            Validate(preset);
            return preset;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(GlobalPreset preset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preset);
        Validate(preset);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _fileStore.WriteAtomicallyAsync(
                _paths.GlobalPresetFilePath,
                preset,
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void Validate(GlobalPreset preset)
    {
        if (preset.SchemaVersion != GlobalPreset.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"La versión {preset.SchemaVersion} del ajuste global no es compatible.");
        }

        if (preset.Settings is null)
        {
            throw new InvalidDataException("El ajuste global no contiene una configuración válida.");
        }
    }
}
