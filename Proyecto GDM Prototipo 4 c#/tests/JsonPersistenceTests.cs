using GDM.Models;
using GDM.Services.Persistence;

namespace GDM.Tests;

public sealed class JsonPersistenceTests
{
    [Fact]
    public async Task MissingSettingsAreCreatedWithDefaultsAndSurviveReload()
    {
        using var storage = new TemporaryStorage();
        var repository = new JsonAppSettingsRepository(storage.Paths);

        AppSettings defaults = await repository.LoadAsync();
        await repository.SaveAsync(defaults with { Theme = "Dark" });
        AppSettings reloaded = await repository.LoadAsync();

        Assert.Equal("System", defaults.Theme);
        Assert.Equal(800, defaults.WindowWidth);
        Assert.Equal(600, defaults.WindowHeight);
        Assert.Equal("Dark", reloaded.Theme);
        Assert.True(File.Exists(storage.Paths.SettingsFilePath));
    }

    [Fact]
    public async Task WindowSizeIsPersistedAndRestored()
    {
        using var storage = new TemporaryStorage();
        var repository = new JsonAppSettingsRepository(storage.Paths);
        await repository.SaveAsync(new AppSettings { WindowWidth = 1024, WindowHeight = 720 });

        AppSettings reloaded = await repository.LoadAsync();

        Assert.Equal(1024, reloaded.WindowWidth);
        Assert.Equal(720, reloaded.WindowHeight);
    }

    [Fact]
    public async Task InvalidWindowSizeIsRejectedWithoutReplacingTheStoredSettings()
    {
        using var storage = new TemporaryStorage();
        var repository = new JsonAppSettingsRepository(storage.Paths);
        const string invalidSettings =
            """{"schemaVersion":1,"theme":"System","language":"es","windowWidth":500,"windowHeight":600}""";
        Directory.CreateDirectory(Path.GetDirectoryName(storage.Paths.SettingsFilePath)!);
        await File.WriteAllTextAsync(storage.Paths.SettingsFilePath, invalidSettings);

        await Assert.ThrowsAsync<InvalidDataException>(() => repository.LoadAsync());

        Assert.Equal(
            invalidSettings,
            await File.ReadAllTextAsync(storage.Paths.SettingsFilePath));
    }

    [Fact]
    public async Task UnsupportedSettingsSchemaIsReportedWithoutOverwritingTheFile()
    {
        using var storage = new TemporaryStorage();
        var repository = new JsonAppSettingsRepository(storage.Paths);
        Directory.CreateDirectory(Path.GetDirectoryName(storage.Paths.SettingsFilePath)!);
        await File.WriteAllTextAsync(
            storage.Paths.SettingsFilePath,
            """{"schemaVersion":999,"theme":"Dark","language":"es"}""");

        await Assert.ThrowsAsync<InvalidDataException>(() => repository.LoadAsync());

        string content = await File.ReadAllTextAsync(storage.Paths.SettingsFilePath);
        Assert.Contains("999", content);
    }

    [Fact]
    public async Task SavingControllersCreatesIndividualFilesAndDoesNotDuplicateCatalogEntries()
    {
        using var storage = new TemporaryStorage();
        var repository = new JsonControllerRepository(storage.Paths);
        Guid id = Guid.NewGuid();
        ControllerRecord controller = CreateController(id);

        await repository.SaveAsync(controller);
        await repository.SaveAsync(controller with { DisplayName = "Renamed controller" });
        IReadOnlyList<ControllerRecord> loaded = await repository.LoadAllAsync();

        Assert.Single(loaded);
        Assert.Equal("Renamed controller", loaded[0].DisplayName);
        Assert.True(File.Exists(storage.Paths.GetControllerFilePath(id)));
        Assert.True(File.Exists(storage.Paths.ControllersIndexFilePath));
    }

    [Fact]
    public async Task DeletingControllerRemovesItsCatalogEntryAndFile()
    {
        using var storage = new TemporaryStorage();
        var repository = new JsonControllerRepository(storage.Paths);
        Guid id = Guid.NewGuid();

        await repository.SaveAsync(CreateController(id));
        await repository.DeleteAsync(id);

        Assert.Empty(await repository.LoadAllAsync());
        Assert.False(File.Exists(storage.Paths.GetControllerFilePath(id)));
    }

    [Fact]
    public async Task CorruptControllerCatalogIsReportedAndPreserved()
    {
        using var storage = new TemporaryStorage();
        var repository = new JsonControllerRepository(storage.Paths);
        Directory.CreateDirectory(Path.GetDirectoryName(storage.Paths.ControllersIndexFilePath)!);
        const string corruptContent = "{ not valid json";
        await File.WriteAllTextAsync(storage.Paths.ControllersIndexFilePath, corruptContent);

        await Assert.ThrowsAsync<InvalidDataException>(() => repository.LoadAllAsync());

        Assert.Equal(corruptContent, await File.ReadAllTextAsync(storage.Paths.ControllersIndexFilePath));
    }

    [Fact]
    public void ApplicationLogIsPersistedAndIncludedInRecentEntries()
    {
        using var storage = new TemporaryStorage();
        var log = new AppLogService(storage.Paths);
        var exception = new InvalidOperationException("Detalle del fallo");

        log.Write(GDM.Models.DiagnosticLogLevel.Error, "Pruebas", "Error de diagnóstico", exception);

        DiagnosticLogEntry entry = Assert.Single(log.GetRecentEntries());
        Assert.Equal(DiagnosticLogLevel.Error, entry.Level);
        Assert.Contains("Detalle del fallo", entry.Details);
        string fileContent = File.ReadAllText(storage.Paths.LogFilePath);
        Assert.Contains("Error de diagnóstico", fileContent);
        Assert.Contains("InvalidOperationException", fileContent);
    }

    private static ControllerRecord CreateController(Guid id) =>
        new()
        {
            ControllerId = id,
            DisplayName = "Test controller",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Identity = new ControllerIdentity
            {
                Evidence =
                [
                    new ControllerIdentityEvidence
                    {
                        Source = "Test",
                        Kind = "TestIdentifier",
                        Value = id.ToString("N")
                    }
                ]
            },
            ButtonMappingOverrides = new Dictionary<string, string> { ["south"] = "A" },
            AxisMappingOverrides = new Dictionary<string, string>(),
            DeadZoneOverrides = new Dictionary<string, double> { ["leftStick"] = 0.1 }
        };

    private sealed class TemporaryStorage : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "GDM.Tests",
            Guid.NewGuid().ToString("N"));

        public LocalStoragePathService Paths { get; }

        public TemporaryStorage()
        {
            Paths = new LocalStoragePathService(_directory);
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
