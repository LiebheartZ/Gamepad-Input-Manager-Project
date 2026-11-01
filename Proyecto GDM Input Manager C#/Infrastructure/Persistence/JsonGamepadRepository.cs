using System.Text.Json;
using System.Text.Json.Serialization;
using GDMInputManager.Data;
using GDMInputManager.Interfaces;
using GDMInputManager.Models;
using GDMInputManager.Services;

namespace GDMInputManager.Infrastructure.Persistence;

public sealed class JsonGamepadRepository : IGamepadRepository
{
    private const int CurrentSchemaVersion = 2;
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _reportedReadErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public event Action<string>? ReadError;

    public JsonGamepadRepository(JsonDataManager dataManager)
    {
        ArgumentNullException.ThrowIfNull(dataManager);
        _directory = Path.Combine(dataManager.GetDataDirectory(), "Gamepads");
        Directory.CreateDirectory(_directory);
    }

    public async Task<IReadOnlyList<GamepadRecord>> GetAllAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = new List<GamepadRecord>();
            foreach (string filePath in Directory.EnumerateFiles(_directory, "mando_*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await using FileStream stream = File.OpenRead(filePath);
                    StoredGamepadRecord? stored = await JsonSerializer.DeserializeAsync<StoredGamepadRecord>(
                        stream,
                        _jsonOptions,
                        cancellationToken).ConfigureAwait(false);
                    GamepadRecord? record = TryConvert(stored);
                    if (record != null)
                    {
                        records.Add(record);
                    }
                    else
                    {
                        ReportReadError(filePath, "El registro no contiene un ID GUID y un nombre válidos.");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
                {
                    ReportReadError(filePath, ex.Message);
                }
            }

            return records;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<GamepadRecord?> GetByIdAsync(Guid recordId, CancellationToken cancellationToken)
    {
        if (recordId == Guid.Empty)
        {
            return null;
        }

        IReadOnlyList<GamepadRecord> records = await GetAllAsync(cancellationToken).ConfigureAwait(false);
        return records.FirstOrDefault(record => record.RecordId == recordId);
    }

    public async Task AddAsync(GamepadRecord record, CancellationToken cancellationToken)
    {
        ValidateRecord(record);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = GetRecordPath(record.RecordId);
            if (File.Exists(path))
            {
                throw new InvalidOperationException($"Ya existe el registro de mando '{record.RecordId}'.");
            }

            await WriteRecordAsync(path, record, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateAsync(GamepadRecord record, CancellationToken cancellationToken)
    {
        ValidateRecord(record);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = GetRecordPath(record.RecordId);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"No se encontró el registro de mando '{record.RecordId}'.", path);
            }

            await WriteRecordAsync(path, record, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(Guid recordId, CancellationToken cancellationToken)
    {
        if (recordId == Guid.Empty)
        {
            throw new ArgumentException("El ID del registro no es válido.", nameof(recordId));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = GetRecordPath(recordId);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"No se encontró el registro de mando '{recordId}'.", path);
            }

            File.Delete(path);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WriteRecordAsync(
        string path,
        GamepadRecord record,
        CancellationToken cancellationToken)
    {
        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var stored = new StoredGamepadRecord
            {
                SchemaVersion = CurrentSchemaVersion,
                Id = record.RecordId.ToString(),
                Name = record.DisplayName.Trim(),
                Type = record.GamepadType,
                ConnectionType = ConnectionType.Unknown,
                Vid = record.VendorId,
                Pid = record.ProductId,
                MacAddress = NormalizeBluetoothAddress(record.Identity.BluetoothAddress),
                SerialNumber = Normalize(record.Identity.SerialNumber),
                InstancePath = Normalize(record.Identity.InstancePath),
                ModelName = record.ModelName,
                CreatedAtUtc = record.CreatedAtUtc,
                LastSeenUtc = record.LastSeenAtUtc,
                LastUsed = record.LastSeenAtUtc?.LocalDateTime ?? record.CreatedAtUtc.LocalDateTime,
                ActiveProfileId = record.ActiveProfileId,
                EnabledFeatures = record.EnabledFeatures,
                Settings = record.Settings,
                IsConnected = false
            };

            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, stored, _jsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(path))
            {
                PreserveLegacyBackup(path);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void PreserveLegacyBackup(string path)
    {
        string backupPath = $"{path}.legacy.bak";
        if (!File.Exists(backupPath))
        {
            File.Copy(path, backupPath);
            LoggerService.Instance.Info($"Se creó una copia de seguridad de un registro legacy en '{Path.GetFileName(backupPath)}'.");
        }
    }

    private GamepadRecord? TryConvert(StoredGamepadRecord? stored)
    {
        string id = stored?.RecordId ?? stored?.Id ?? string.Empty;
        string displayName = (stored?.DisplayName ?? stored?.Name ?? string.Empty).Trim();
        if (!Guid.TryParse(id, out Guid recordId) || recordId == Guid.Empty || displayName.Length == 0)
        {
            return null;
        }

        DateTimeOffset createdAtUtc = stored?.CreatedAtUtc is { } created
            ? created.ToUniversalTime()
            : DateTimeOffset.UtcNow;
        DateTimeOffset? lastSeenUtc = stored?.LastSeenUtc?.ToUniversalTime();
        if (lastSeenUtc == null && stored?.LastUsed is { } lastUsed && lastUsed != default)
        {
            DateTime localLastUsed = DateTime.SpecifyKind(lastUsed, DateTimeKind.Local);
            lastSeenUtc = new DateTimeOffset(localLastUsed).ToUniversalTime();
        }

        GamepadIdentity storedIdentity = stored?.Identity ?? new GamepadIdentity();
        return new GamepadRecord
        {
            RecordId = recordId,
            DisplayName = displayName,
            GamepadType = stored?.Type ?? GamepadType.Unknown,
            ModelName = stored?.ModelName ?? string.Empty,
            VendorId = stored?.Vid is >= 0 and <= ushort.MaxValue ? (ushort)stored.Vid.Value : null,
            ProductId = stored?.Pid is >= 0 and <= ushort.MaxValue ? (ushort)stored.Pid.Value : null,
            Identity = new GamepadIdentity
            {
                SerialNumber = Normalize(storedIdentity.SerialNumber ?? stored?.SerialNumber),
                BluetoothAddress = NormalizeBluetoothAddress(
                    storedIdentity.BluetoothAddress ?? stored?.MacAddress),
                InstancePath = Normalize(storedIdentity.InstancePath ?? stored?.InstancePath)
            },
            CreatedAtUtc = createdAtUtc,
            LastSeenAtUtc = lastSeenUtc,
            ActiveProfileId = stored?.ActiveProfileId,
            EnabledFeatures = stored?.EnabledFeatures ?? new List<string>(),
            Settings = stored?.Settings ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static void ValidateRecord(GamepadRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.RecordId == Guid.Empty)
        {
            throw new ArgumentException("El ID del registro no es válido.", nameof(record));
        }

        if (string.IsNullOrWhiteSpace(record.DisplayName) || record.DisplayName.Trim().Length > 120)
        {
            throw new ArgumentException("El nombre debe contener entre 1 y 120 caracteres.", nameof(record));
        }
    }

    private string GetRecordPath(Guid recordId)
    {
        return Path.Combine(_directory, $"mando_{recordId:D}.json");
    }

    private void ReportReadError(string filePath, string message)
    {
        if (!_reportedReadErrors.Add(filePath))
        {
            return;
        }

        string diagnostic = $"No se pudo leer el registro '{Path.GetFileName(filePath)}': {message}";
        LoggerService.Instance.Error(diagnostic);
        ReadError?.Invoke(diagnostic);
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? NormalizeBluetoothAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        string normalized = string.Concat(address.Where(Uri.IsHexDigit)).ToUpperInvariant();
        return normalized.Length == 12 ? normalized : null;
    }

    private sealed class StoredGamepadRecord
    {
        public int? SchemaVersion { get; set; }
        public string? RecordId { get; set; }
        public string? Id { get; set; }
        public string? DisplayName { get; set; }
        public string? Name { get; set; }
        public GamepadType? Type { get; set; }
        public ConnectionType? ConnectionType { get; set; }
        public int? Vid { get; set; }
        public int? Pid { get; set; }
        public string? MacAddress { get; set; }
        public string? SerialNumber { get; set; }
        public string? InstancePath { get; set; }
        public string? ModelName { get; set; }
        public DateTime? LastUsed { get; set; }
        public DateTimeOffset? CreatedAtUtc { get; set; }
        public DateTimeOffset? LastSeenUtc { get; set; }
        public string? ActiveProfileId { get; set; }
        public List<string>? EnabledFeatures { get; set; }
        public Dictionary<string, object?>? Settings { get; set; }
        public bool? IsConnected { get; set; }
        public GamepadIdentity? Identity { get; set; }
    }
}
