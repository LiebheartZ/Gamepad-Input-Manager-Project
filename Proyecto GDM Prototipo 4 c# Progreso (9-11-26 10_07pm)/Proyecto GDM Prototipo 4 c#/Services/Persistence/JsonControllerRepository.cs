using System.IO;
using GDM.Models;
using GDM.Services.Abstractions;

namespace GDM.Services.Persistence;

public sealed class JsonControllerRepository : IControllerRepository
{
    private readonly IStoragePathService _paths;
    private readonly JsonFileStore _fileStore = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonControllerRepository(IStoragePathService paths)
    {
        _paths = paths;
    }

    public async Task<IReadOnlyList<ControllerRecord>> LoadAllAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ControllerIndex index = await LoadIndexAsync(cancellationToken);
            var records = new List<ControllerRecord>(index.Controllers.Count);

            foreach (ControllerIndexEntry entry in index.Controllers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string expectedFileName = GetFileName(entry.ControllerId);
                if (!string.Equals(entry.FileName, expectedFileName, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"La ruta del mando '{entry.ControllerId}' en el catálogo no es válida.");
                }

                ControllerRecord record = await _fileStore.ReadAsync<ControllerRecord>(
                    _paths.GetControllerFilePath(entry.ControllerId),
                    cancellationToken);
                Validate(record);

                if (record.ControllerId != entry.ControllerId)
                {
                    throw new InvalidDataException(
                        $"El archivo del mando '{entry.ControllerId}' contiene un ID diferente.");
                }

                records.Add(record);
            }

            return records;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        ControllerRecord controller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controller);
        Validate(controller);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ControllerIndex index = await LoadIndexAsync(cancellationToken);
            string fileName = GetFileName(controller.ControllerId);

            await _fileStore.WriteAtomicallyAsync(
                _paths.GetControllerFilePath(controller.ControllerId),
                controller,
                cancellationToken);

            var updatedEntries = index.Controllers
                .Where(entry => entry.ControllerId != controller.ControllerId)
                .Append(new ControllerIndexEntry
                {
                    ControllerId = controller.ControllerId,
                    DisplayName = controller.DisplayName,
                    FileName = fileName,
                    CreatedAtUtc = controller.CreatedAtUtc,
                    UpdatedAtUtc = controller.UpdatedAtUtc
                })
                .OrderBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            await _fileStore.WriteAtomicallyAsync(
                _paths.ControllersIndexFilePath,
                new ControllerIndex { Controllers = updatedEntries },
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(
        Guid controllerId,
        CancellationToken cancellationToken = default)
    {
        if (controllerId == Guid.Empty)
        {
            throw new ArgumentException("El identificador del mando no puede estar vacío.", nameof(controllerId));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ControllerIndex index = await LoadIndexAsync(cancellationToken);
            ControllerIndexEntry? entry = index.Controllers
                .SingleOrDefault(item => item.ControllerId == controllerId);
            if (entry is null)
            {
                return;
            }

            string expectedFileName = GetFileName(controllerId);
            if (!string.Equals(entry.FileName, expectedFileName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"La ruta del mando '{controllerId}' en el catálogo no es válida.");
            }

            ControllerIndex updatedIndex = new()
            {
                Controllers = index.Controllers
                    .Where(item => item.ControllerId != controllerId)
                    .ToArray()
            };

            await _fileStore.WriteAtomicallyAsync(
                _paths.ControllersIndexFilePath,
                updatedIndex,
                cancellationToken);

            string controllerPath = _paths.GetControllerFilePath(controllerId);
            if (File.Exists(controllerPath))
            {
                File.Delete(controllerPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ControllerIndex> LoadIndexAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.ControllersIndexFilePath))
        {
            return new ControllerIndex();
        }

        ControllerIndex index = await _fileStore.ReadAsync<ControllerIndex>(
            _paths.ControllersIndexFilePath,
            cancellationToken);
        Validate(index);
        return index;
    }

    private static string GetFileName(Guid controllerId) =>
        $"controller_{controllerId:N}.json";

    private static void Validate(ControllerRecord record)
    {
        if (record.SchemaVersion != ControllerRecord.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"La versión {record.SchemaVersion} del archivo de mando no es compatible.");
        }

        if (record.ControllerId == Guid.Empty)
        {
            throw new InvalidDataException("El ID interno del mando no puede estar vacío.");
        }

        if (string.IsNullOrWhiteSpace(record.DisplayName))
        {
            throw new InvalidDataException("El nombre visible del mando no puede estar vacío.");
        }

        if (record.Identity is null ||
            record.Identity.Evidence is null ||
            record.ButtonMappingOverrides is null ||
            record.AxisMappingOverrides is null ||
            record.DeadZoneOverrides is null ||
            record.Settings is null)
        {
            throw new InvalidDataException("El archivo del mando contiene campos obligatorios nulos.");
        }

        if (record.DeadZoneOverrides.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || pair.Value is < 0 or > 1))
        {
            throw new InvalidDataException("El archivo del mando contiene zonas muertas no válidas.");
        }
    }

    private static void Validate(ControllerIndex index)
    {
        if (index.SchemaVersion != ControllerIndex.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"La versión {index.SchemaVersion} del catálogo de mandos no es compatible.");
        }

        if (index.Controllers is null)
        {
            throw new InvalidDataException("El catálogo no contiene una lista de mandos válida.");
        }

        if (index.Controllers.Any(entry =>
                entry is null ||
                entry.ControllerId == Guid.Empty ||
                string.IsNullOrWhiteSpace(entry.DisplayName) ||
                string.IsNullOrWhiteSpace(entry.FileName)) ||
            index.Controllers.Select(entry => entry.ControllerId).Distinct().Count() != index.Controllers.Count)
        {
            throw new InvalidDataException("El catálogo contiene entradas incompletas o IDs duplicados.");
        }
    }

    private sealed record ControllerIndex
    {
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; init; } = CurrentSchemaVersion;
        public IReadOnlyList<ControllerIndexEntry> Controllers { get; init; } = [];
    }

    private sealed record ControllerIndexEntry
    {
        public Guid ControllerId { get; init; }
        public string DisplayName { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
        public DateTimeOffset CreatedAtUtc { get; init; }
        public DateTimeOffset UpdatedAtUtc { get; init; }
    }
}
