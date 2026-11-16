using System.IO;
using GDM.Models;
using GDM.Services.Abstractions;

namespace GDM.Services.Persistence;

public sealed class JsonAppSettingsRepository : IAppSettingsRepository
{
    private readonly IStoragePathService _paths;
    private readonly JsonFileStore _fileStore = new();

    public JsonAppSettingsRepository(IStoragePathService paths)
    {
        _paths = paths;
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.SettingsFilePath))
        {
            var defaults = new AppSettings();
            await SaveAsync(defaults, cancellationToken);
            return defaults;
        }

        AppSettings settings = await _fileStore.ReadAsync<AppSettings>(
            _paths.SettingsFilePath,
            cancellationToken);
        Validate(settings);
        return settings;
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Validate(settings);
        return _fileStore.WriteAtomicallyAsync(
            _paths.SettingsFilePath,
            settings,
            cancellationToken);
    }

    private static void Validate(AppSettings settings)
    {
        if (settings.SchemaVersion != AppSettings.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"La versión {settings.SchemaVersion} de app-settings.json no es compatible.");
        }

        if (settings.Theme is not ("System" or "Light" or "Dark"))
        {
            throw new InvalidDataException(
                "El tema de la aplicación debe ser System, Light o Dark.");
        }

        if (!VirtualGamepadTypes.IsSupported(settings.DefaultVirtualGamepadType))
        {
            throw new InvalidDataException(
                "El tipo de mando virtual predeterminado no es compatible.");
        }

        if (settings.DefaultProfileId == Guid.Empty)
        {
            throw new InvalidDataException(
                "El perfil predeterminado no es válido.");
        }

        if (string.IsNullOrWhiteSpace(settings.Language))
        {
            throw new InvalidDataException("El idioma de la aplicación no puede estar vacío.");
        }

        if (!double.IsFinite(settings.WindowWidth) ||
            settings.WindowWidth is < 640 or > 10000)
        {
            throw new InvalidDataException("El ancho guardado de la ventana no es válido.");
        }

        if (!double.IsFinite(settings.WindowHeight) ||
            settings.WindowHeight is < 480 or > 10000)
        {
            throw new InvalidDataException("El alto guardado de la ventana no es válido.");
        }
    }
}
