using System.IO;
using GDM.Models;
using GDM.Services.Abstractions;

namespace GDM.Services.Persistence;

public sealed class JsonProfileRepository : IProfileRepository
{
    private readonly IStoragePathService _paths;
    private readonly JsonFileStore _fileStore = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonProfileRepository(IStoragePathService paths)
    {
        _paths = paths;
    }

    public async Task<IReadOnlyList<ControllerProfile>> LoadAllAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ProfileIndex index = await LoadIndexAsync(cancellationToken);
            var profiles = new List<ControllerProfile>(index.Profiles.Count);

            foreach (ProfileIndexEntry entry in index.Profiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(entry.FileName, GetFileName(entry.ProfileId), StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"La ruta del perfil '{entry.ProfileId}' en el catálogo no es válida.");
                }

                ControllerProfile profile = await _fileStore.ReadAsync<ControllerProfile>(
                    _paths.GetProfileFilePath(entry.ProfileId),
                    cancellationToken);
                Validate(profile);
                if (profile.ProfileId != entry.ProfileId)
                {
                    throw new InvalidDataException(
                        $"El archivo del perfil '{entry.ProfileId}' contiene un ID diferente.");
                }

                profiles.Add(profile);
            }

            return profiles
                .OrderBy(profile => profile.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        ControllerProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Validate(profile);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ProfileIndex index = await LoadIndexAsync(cancellationToken);
            ControllerProfile[] existingProfiles = await LoadProfilesAsync(index, cancellationToken);
            if (existingProfiles.Any(existing =>
                    existing.ProfileId != profile.ProfileId &&
                    string.Equals(existing.DisplayName, profile.DisplayName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("Ya existe un perfil con ese nombre.");
            }

            await _fileStore.WriteAtomicallyAsync(
                _paths.GetProfileFilePath(profile.ProfileId),
                profile,
                cancellationToken);

            ProfileIndexEntry[] entries = index.Profiles
                .Where(entry => entry.ProfileId != profile.ProfileId)
                .Append(new ProfileIndexEntry
                {
                    ProfileId = profile.ProfileId,
                    DisplayName = profile.DisplayName,
                    FileName = GetFileName(profile.ProfileId),
                    CreatedAtUtc = profile.CreatedAtUtc,
                    UpdatedAtUtc = profile.UpdatedAtUtc
                })
                .OrderBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            await _fileStore.WriteAtomicallyAsync(
                _paths.ProfilesIndexFilePath,
                new ProfileIndex { Profiles = entries },
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("El identificador del perfil no puede estar vacío.", nameof(profileId));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ProfileIndex index = await LoadIndexAsync(cancellationToken);
            ProfileIndexEntry? entry = index.Profiles.SingleOrDefault(item => item.ProfileId == profileId);
            if (entry is null)
            {
                return;
            }

            if (!string.Equals(entry.FileName, GetFileName(profileId), StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"La ruta del perfil '{profileId}' en el catálogo no es válida.");
            }

            await _fileStore.WriteAtomicallyAsync(
                _paths.ProfilesIndexFilePath,
                new ProfileIndex
                {
                    Profiles = index.Profiles.Where(item => item.ProfileId != profileId).ToArray()
                },
                cancellationToken);

            string profilePath = _paths.GetProfileFilePath(profileId);
            if (File.Exists(profilePath))
            {
                File.Delete(profilePath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ControllerProfile[]> LoadProfilesAsync(
        ProfileIndex index,
        CancellationToken cancellationToken)
    {
        var profiles = new List<ControllerProfile>(index.Profiles.Count);
        foreach (ProfileIndexEntry entry in index.Profiles)
        {
            if (!string.Equals(entry.FileName, GetFileName(entry.ProfileId), StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"La ruta del perfil '{entry.ProfileId}' en el catálogo no es válida.");
            }

            ControllerProfile existing = await _fileStore.ReadAsync<ControllerProfile>(
                _paths.GetProfileFilePath(entry.ProfileId),
                cancellationToken);
            Validate(existing);
            if (existing.ProfileId != entry.ProfileId)
            {
                throw new InvalidDataException(
                    $"El archivo del perfil '{entry.ProfileId}' contiene un ID diferente.");
            }

            profiles.Add(existing);
        }

        return profiles.ToArray();
    }

    private async Task<ProfileIndex> LoadIndexAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.ProfilesIndexFilePath))
        {
            return new ProfileIndex();
        }

        ProfileIndex index = await _fileStore.ReadAsync<ProfileIndex>(
            _paths.ProfilesIndexFilePath,
            cancellationToken);
        Validate(index);
        return index;
    }

    private static string GetFileName(Guid profileId) => $"profile_{profileId:N}.json";

    private static void Validate(ControllerProfile profile)
    {
        if (profile.SchemaVersion != ControllerProfile.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"La versión {profile.SchemaVersion} del archivo de perfil no es compatible.");
        }

        if (profile.ProfileId == Guid.Empty)
        {
            throw new InvalidDataException("El ID interno del perfil no puede estar vacío.");
        }

        if (string.IsNullOrWhiteSpace(profile.DisplayName) || profile.DisplayName.Length > 80)
        {
            throw new InvalidDataException("El nombre del perfil debe contener entre 1 y 80 caracteres.");
        }

        if (profile.Description is null || profile.Description.Length > 500)
        {
            throw new InvalidDataException("La descripción del perfil no puede superar 500 caracteres.");
        }

        if (profile.VirtualGamepadType is null ||
            (!VirtualGamepadTypes.IsSupported(profile.VirtualGamepadType) &&
             !VirtualGamepadTypes.IsAuto(profile.VirtualGamepadType)))
        {
            throw new InvalidDataException(
                "El tipo de mando virtual del perfil no es compatible.");
        }

        if (profile.ButtonMappings is null ||
            profile.AxisMappings is null ||
            profile.AxisInversions is null ||
            profile.DeadZones is null ||
            profile.Combos is null ||
            profile.ButtonMappings.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) ||
            profile.AxisMappings.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) ||
            profile.AxisInversions.Any(pair => string.IsNullOrWhiteSpace(pair.Key)) ||
            profile.DeadZones.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) ||
                !double.IsFinite(pair.Value) ||
                pair.Value is < 0 or > 1))
        {
            throw new InvalidDataException("El perfil contiene asignaciones o zonas muertas no válidas.");
        }

        if (profile.Combos.Any(combo =>
                combo is null ||
                combo.ComboId == Guid.Empty ||
                combo.DisplayName is null ||
                combo.DisplayName.Length > 80 ||
                combo.Buttons is null ||
                combo.TargetButtons is null ||
                combo.Buttons.Any(string.IsNullOrWhiteSpace) ||
                combo.TargetButtons.Any(string.IsNullOrWhiteSpace) ||
                !ComboActionTypes.IsSupported(combo.ActionType) ||
                combo.HoldMilliseconds is < 0 or > 10000))
        {
            throw new InvalidDataException("El perfil contiene combos no válidos.");
        }

        if (profile.TypeMappings is null ||
            profile.TypeMappings.Any(pair =>
                pair.Value is null ||
                !VirtualGamepadTypes.IsSupported(pair.Key) ||
                !IsValidMapping(pair.Value)))
        {
            throw new InvalidDataException(
                "El perfil contiene configuraciones de remapeo por tipo no válidas.");
        }
    }

    private static bool IsValidMapping(ControllerProfileMapping mappings) =>
        mappings.ButtonMappings is not null &&
        mappings.AxisMappings is not null &&
        mappings.AxisInversions is not null &&
        mappings.DeadZones is not null &&
        mappings.ButtonMappings.All(pair =>
            !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value)) &&
        mappings.AxisMappings.All(pair =>
            !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value)) &&
        mappings.AxisInversions.All(pair => !string.IsNullOrWhiteSpace(pair.Key)) &&
        mappings.DeadZones.All(pair =>
            !string.IsNullOrWhiteSpace(pair.Key) &&
            double.IsFinite(pair.Value) &&
            pair.Value is >= 0 and <= 1);

    private static void Validate(ProfileIndex index)
    {
        if (index.SchemaVersion != ProfileIndex.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"La versión {index.SchemaVersion} del catálogo de perfiles no es compatible.");
        }

        if (index.Profiles is null ||
            index.Profiles.Any(entry =>
                entry is null ||
                entry.ProfileId == Guid.Empty ||
                string.IsNullOrWhiteSpace(entry.DisplayName) ||
                string.IsNullOrWhiteSpace(entry.FileName)) ||
            index.Profiles.Select(entry => entry.ProfileId).Distinct().Count() != index.Profiles.Count)
        {
            throw new InvalidDataException("El catálogo contiene entradas incompletas o IDs duplicados.");
        }
    }

    private sealed record ProfileIndex
    {
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; init; } = CurrentSchemaVersion;
        public IReadOnlyList<ProfileIndexEntry> Profiles { get; init; } = [];
    }

    private sealed record ProfileIndexEntry
    {
        public Guid ProfileId { get; init; }
        public string DisplayName { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
        public DateTimeOffset CreatedAtUtc { get; init; }
        public DateTimeOffset UpdatedAtUtc { get; init; }
    }
}
