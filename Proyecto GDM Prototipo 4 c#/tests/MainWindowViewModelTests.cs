using GDM.ViewModels;
using GDM.Services;
using GDM.Models;

namespace GDM.Tests;

public class MainWindowViewModelTests
{
    [Fact]
    public void StartsOnHomeAndDoesNotClaimControllerDetectionIsAvailable()
    {
        var viewModel = new MainWindowViewModel();

        Assert.Equal("Inicio", viewModel.CurrentSection);
        Assert.Contains("implementarán", viewModel.StatusMessage);
    }

    [Fact]
    public void NavigatingToControllersWithoutAProviderShowsAnExplicitErrorState()
    {
        var viewModel = new MainWindowViewModel();

        viewModel.NavigateCommand.Execute("Mandos");

        Assert.Equal("Mandos", viewModel.CurrentSection);
        Assert.Equal("Detección no configurada", viewModel.StatusTitle);
        Assert.Contains("no está disponible", viewModel.StatusMessage);
    }

    [Fact]
    public void RepeatedControllerTabNavigationCanStartAndStopMonitoring()
    {
        var viewModel = new MainWindowViewModel(new FakeGamepadDetectionService());

        for (int i = 0; i < 100; i++)
        {
            viewModel.NavigateCommand.Execute("Mandos");
            viewModel.NavigateCommand.Execute("Inicio");
        }

        Assert.Equal("Inicio", viewModel.CurrentSection);
        viewModel.Dispose();
    }

    [Fact]
    public async Task RefreshReplacesTheConnectedDeviceList()
    {
        var detection = new FakeGamepadDetectionService();
        var viewModel = new MainWindowViewModel(detection);
        detection.Devices =
        [
            new GDM.Models.ConnectedGamepad
            {
                DeviceKey = "SDL2:1",
                Name = "Test gamepad",
                VendorId = 0x1234,
                ProductId = 0x5678
            }
        ];

        await viewModel.RefreshDevicesAsync();
        Assert.Single(viewModel.ConnectedGamepads);
        Assert.Equal("VID: 1234 · PID: 5678", viewModel.ConnectedGamepads[0].HardwareIdLabel);

        detection.Devices = [];
        await viewModel.RefreshDevicesAsync();

        Assert.Empty(viewModel.ConnectedGamepads);
        Assert.Equal("SDL2 no ve mandos accesibles", viewModel.StatusTitle);
        Assert.Contains("HidHide", viewModel.StatusMessage);
        Assert.Contains("no hace falta desactivar", viewModel.StatusMessage);
    }

    [Fact]
    public async Task ExplicitRefreshRescansBeforeEnumerating()
    {
        var detection = new FakeGamepadDetectionService();
        var viewModel = new MainWindowViewModel(detection);

        await viewModel.RefreshDevicesAsync(forceRescan: true);

        Assert.Equal(1, detection.RefreshCount);
        Assert.Equal(1, detection.EnumerationCount);
        viewModel.Dispose();
    }

    [Fact]
    public async Task RefreshLogsControllerIdentityWhenTheDetectedListChanges()
    {
        using var storage = new TemporaryStorage();
        var detection = new FakeGamepadDetectionService
        {
            Devices =
            [
                new GDM.Models.ConnectedGamepad
                {
                    DeviceKey = "SDL2:10",
                    Name = "Xbox One Controller",
                    VendorId = 0x045E,
                    ProductId = 0x02D1
                }
            ]
        };
        var log = new GDM.Services.Persistence.AppLogService(storage.Paths);
        var viewModel = new MainWindowViewModel(detection, log);

        await viewModel.RefreshDevicesAsync();

        Assert.Contains("Xbox One Controller", Assert.Single(log.GetRecentEntries()).Message);
        Assert.Contains("SDL2:10", log.GetRecentEntries()[0].Message);
        viewModel.Dispose();
    }

    [Fact]
    public async Task RefreshDoesNotRebuildOrLogAnUnchangedDeviceList()
    {
        using var storage = new TemporaryStorage();
        var detection = new FakeGamepadDetectionService
        {
            Devices =
            [
                new GDM.Models.ConnectedGamepad
                {
                    DeviceKey = "SDL2:1",
                    Name = "Test gamepad",
                    VendorId = 0x1234,
                    ProductId = 0x5678
                }
            ]
        };
        var log = new GDM.Services.Persistence.AppLogService(storage.Paths);
        var viewModel = new MainWindowViewModel(detection, log);
        int collectionChanges = 0;
        viewModel.ConnectedGamepads.CollectionChanged += (_, _) => collectionChanges++;

        await viewModel.RefreshDevicesAsync();
        int changesAfterInitialRefresh = collectionChanges;
        await viewModel.RefreshDevicesAsync();

        Assert.Equal(changesAfterInitialRefresh, collectionChanges);
        Assert.Single(log.GetRecentEntries());
        viewModel.Dispose();
    }

    [Fact]
    public async Task SavingControllerPersistsItAndReusesItsRecordAfterReconnect()
    {
        using var storage = new TemporaryStorage();
        var repository = new GDM.Services.Persistence.JsonControllerRepository(storage.Paths);
        var detection = new FakeGamepadDetectionService
        {
            Devices = [CreateConnectedGamepad("SDL2:1", "serial-abc")]
        };
        var viewModel = new MainWindowViewModel(
            detection,
            controllerRepository: repository);
        await viewModel.InitializeAsync();
        await viewModel.RefreshDevicesAsync();

        await viewModel.SaveControllerAsync(Assert.Single(viewModel.ControllerItems));

        ControllerRecord savedRecord = Assert.Single(await repository.LoadAllAsync());
        Assert.Equal("Test gamepad", savedRecord.DisplayName);
        Assert.Single(viewModel.ControllerItems);
        Assert.Equal("Conectado · Guardado", viewModel.ControllerItems[0].ConnectionText);

        detection.Devices = [];
        await viewModel.RefreshDevicesAsync();
        Assert.Single(viewModel.ControllerItems);
        Assert.Equal("Desconectado · Guardado", viewModel.ControllerItems[0].ConnectionText);

        detection.Devices = [CreateConnectedGamepad("SDL2:8", "serial-abc")];
        await viewModel.RefreshDevicesAsync();
        Assert.Single(viewModel.ControllerItems);
        Assert.Equal(savedRecord.ControllerId, viewModel.ControllerItems[0].SavedRecord?.ControllerId);
        Assert.Equal("Conectado · Guardado", viewModel.ControllerItems[0].ConnectionText);
        Assert.Single(await repository.LoadAllAsync());
        viewModel.Dispose();
    }

    [Fact]
    public async Task LoadingSavedControllerDoesNotMatchByNameOrHardwareModelAlone()
    {
        using var storage = new TemporaryStorage();
        var repository = new GDM.Services.Persistence.JsonControllerRepository(storage.Paths);
        ControllerRecord savedRecord = CreateSavedController("Test gamepad", "serial-original");
        await repository.SaveAsync(savedRecord);
        var detection = new FakeGamepadDetectionService
        {
            Devices = [CreateConnectedGamepad("SDL2:2", serialNumber: null)]
        };
        var viewModel = new MainWindowViewModel(
            detection,
            controllerRepository: repository);

        await viewModel.InitializeAsync();
        await viewModel.RefreshDevicesAsync();

        Assert.Equal(2, viewModel.ControllerItems.Count);
        Assert.Contains(viewModel.ControllerItems, item =>
            item.SavedRecord?.ControllerId == savedRecord.ControllerId &&
            item.ConnectionText == "Desconectado · Guardado");
        Assert.Contains(viewModel.ControllerItems, item =>
            item.ConnectedDevice is not null &&
            item.SavedRecord is null &&
            item.ConnectionText == "Conectado");
        viewModel.Dispose();
    }

    [Fact]
    public async Task RemovingSavedControllerRequiresConfirmationAndDeletesItsJsonRecord()
    {
        using var storage = new TemporaryStorage();
        var repository = new GDM.Services.Persistence.JsonControllerRepository(storage.Paths);
        ControllerRecord record = CreateSavedController("Test gamepad", "serial-delete");
        await repository.SaveAsync(record);
        var viewModel = new MainWindowViewModel(
            controllerRepository: repository,
            confirmRemoveSavedController: _ => false);
        await viewModel.InitializeAsync();
        ControllerListItem item = Assert.Single(viewModel.ControllerItems);

        await viewModel.RemoveSavedControllerAsync(item);
        Assert.Single(await repository.LoadAllAsync());

        viewModel.Dispose();
        var confirmedViewModel = new MainWindowViewModel(
            controllerRepository: repository,
            confirmRemoveSavedController: _ => true);
        await confirmedViewModel.InitializeAsync();
        await confirmedViewModel.RemoveSavedControllerAsync(
            Assert.Single(confirmedViewModel.ControllerItems));

        Assert.Empty(await repository.LoadAllAsync());
        Assert.False(File.Exists(storage.Paths.GetControllerFilePath(record.ControllerId)));
        confirmedViewModel.Dispose();
    }

    [Fact]
    public void RegistrationTabDisplaysExistingAndNewApplicationLogEntries()
    {
        using var storage = new TemporaryStorage();
        var log = new GDM.Services.Persistence.AppLogService(storage.Paths);
        log.Write(
            GDM.Models.DiagnosticLogLevel.Error,
            "Test",
            "Fallo de prueba",
            new InvalidOperationException("Detalle de prueba"));
        var viewModel = new MainWindowViewModel(appLogService: log);
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        viewModel.NavigateCommand.Execute("Registro");

        Assert.True(viewModel.IsLogSection);
        Assert.False(viewModel.ShowStatusCard);
        Assert.Contains(nameof(viewModel.IsLogSection), changedProperties);
        Assert.Contains(nameof(viewModel.ShowStatusCard), changedProperties);
        Assert.Single(viewModel.LogEntries);
        Assert.Equal("Fallo de prueba", viewModel.LogEntries[0].Message);

        log.Write(GDM.Models.DiagnosticLogLevel.Warning, "Test", "Aviso posterior");

        Assert.Equal(2, viewModel.LogEntries.Count);
        Assert.Equal("Aviso posterior", viewModel.LogEntries[0].Message);
        viewModel.Dispose();
    }

    [Fact]
    public async Task Sdl2ProviderInitializesAndEnumeratesWithoutRequiringAController()
    {
        using var detection = new Sdl2GamepadDetectionService();

        await detection.InitializeAsync();
        IReadOnlyList<GDM.Models.ConnectedGamepad> beforeRefresh =
            await detection.EnumerateConnectedAsync();
        await detection.RefreshAsync();
        IReadOnlyList<GDM.Models.ConnectedGamepad> afterRefresh =
            await detection.EnumerateConnectedAsync();

        Assert.Equal(
            beforeRefresh.Select(device => device.DeviceKey),
            afterRefresh.Select(device => device.DeviceKey));
    }

    private sealed class FakeGamepadDetectionService : GDM.Services.Abstractions.IGamepadDetectionService
    {
        public IReadOnlyList<GDM.Models.ConnectedGamepad> Devices { get; set; } = [];
        public int RefreshCount { get; private set; }
        public int EnumerationCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            RefreshCount++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<GDM.Models.ConnectedGamepad>> EnumerateConnectedAsync(
            CancellationToken cancellationToken = default)
        {
            EnumerationCount++;
            return Task.FromResult(Devices);
        }

        public void Dispose()
        {
        }
    }

    private static GDM.Models.ConnectedGamepad CreateConnectedGamepad(
        string deviceKey,
        string? serialNumber) =>
        new()
        {
            DeviceKey = deviceKey,
            Name = "Test gamepad",
            VendorId = 0x1234,
            ProductId = 0x5678,
            SerialNumber = serialNumber
        };

    private static ControllerRecord CreateSavedController(string name, string serialNumber) =>
        new()
        {
            ControllerId = Guid.NewGuid(),
            DisplayName = name,
            Identity = new ControllerIdentity
            {
                Model = name,
                Evidence =
                [
                    new ControllerIdentityEvidence
                    {
                        Source = "SDL2",
                        Kind = "VendorId",
                        Value = "1234"
                    },
                    new ControllerIdentityEvidence
                    {
                        Source = "SDL2",
                        Kind = "ProductId",
                        Value = "5678"
                    },
                    new ControllerIdentityEvidence
                    {
                        Source = "SDL2",
                        Kind = "SerialNumber",
                        Value = serialNumber
                    }
                ]
            },
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

    private sealed class TemporaryStorage : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "GDM.Tests",
            Guid.NewGuid().ToString("N"));

        public GDM.Services.Persistence.LocalStoragePathService Paths { get; }

        public TemporaryStorage()
        {
            Paths = new GDM.Services.Persistence.LocalStoragePathService(_directory);
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
