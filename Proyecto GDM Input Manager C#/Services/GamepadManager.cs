using System.Collections.ObjectModel;
using GDMInputManager.Interfaces;
using GDMInputManager.Models;

namespace GDMInputManager.Services;

public sealed class GamepadManager : IGamepadManager
{
    private readonly IReadOnlyList<IGamepadProvider> _providers;
    private readonly IGamepadRepository _repository;
    private readonly IGamepadConnectionEnricher _connectionEnricher;
    private readonly IDeviceChangeMonitor _deviceChangeMonitor;
    private readonly IBluetoothDeviceActions _bluetoothActions;
    private readonly GamepadIdentityService _identityService = new();
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, Guid> _sessionAssociations = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _connectedRecordIds = new();
    private IReadOnlyList<IGamepadProvider> _initializedProviders = Array.Empty<IGamepadProvider>();
    private GamepadDetectionService? _detectionService;
    private List<GamepadRecord> _records = new();
    private IReadOnlyList<GamepadDevice> _devices = Array.Empty<GamepadDevice>();
    private GamepadListItem[] _currentItems = Array.Empty<GamepadListItem>();
    private bool _initialized;
    private volatile bool _disposed;
    private bool _includeAvailableBluetooth;
    private bool _hasCompletedSnapshot;
    private int _refreshQueued;
    private int _queuedIncludeAvailableBluetooth;

    public IReadOnlyList<GamepadListItem> CurrentItems =>
        new ReadOnlyCollection<GamepadListItem>(Volatile.Read(ref _currentItems));

    public event EventHandler<GamepadListChangedEventArgs>? ListChanged;
    public event EventHandler<GamepadErrorEventArgs>? ErrorOccurred;
    public event EventHandler<GamepadRefreshCompletedEventArgs>? RefreshCompleted;

    public GamepadManager(
        IEnumerable<IGamepadProvider> providers,
        IGamepadRepository repository,
        IGamepadConnectionEnricher connectionEnricher,
        IDeviceChangeMonitor deviceChangeMonitor,
        IBluetoothDeviceActions bluetoothActions)
    {
        _providers = providers.ToList();
        _repository = repository;
        _connectionEnricher = connectionEnricher;
        _deviceChangeMonitor = deviceChangeMonitor;
        _bluetoothActions = bluetoothActions;
        _repository.ReadError += Repository_ReadError;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using CancellationTokenSource linkedCancellation = CreateLinkedCancellation(cancellationToken);
        cancellationToken = linkedCancellation.Token;
        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool initializeCompleted = false;
        var errors = new List<GamepadErrorEventArgs>();
        try
        {
            if (_initialized)
            {
                return;
            }

            try
            {
                _records = (await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false)).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add(new GamepadErrorEventArgs(
                    "Persistencia",
                    "No se pudieron cargar los mandos guardados. Se conservarán los datos existentes.",
                    ex));
            }

            var successfulProviders = new List<IGamepadProvider>();
            foreach (IGamepadProvider provider in _providers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await provider.InitializeAsync(cancellationToken).ConfigureAwait(false);
                    successfulProviders.Add(provider);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add(new GamepadErrorEventArgs(
                        provider.Name,
                        $"No se pudo iniciar el detector '{provider.Name}'.",
                        ex));
                }
            }

            _initializedProviders = successfulProviders;
            _detectionService = new GamepadDetectionService(_initializedProviders, _connectionEnricher);
            _deviceChangeMonitor.DevicesChanged += DeviceChangeMonitor_DevicesChanged;
            try
            {
                _deviceChangeMonitor.Start();
            }
            catch (Exception ex)
            {
                errors.Add(new GamepadErrorEventArgs(
                    "Monitor de dispositivos",
                    "La actualización automática por cambios de hardware no está disponible.",
                    ex));
            }

            _initialized = true;
            initializeCompleted = true;
        }
        finally
        {
            _initializationGate.Release();
        }

        foreach (GamepadErrorEventArgs error in errors)
        {
            RaiseError(error);
        }

        if (initializeCompleted)
        {
            LoggerService.Instance.Info(
                $"GamepadManager iniciado con {_initializedProviders.Count} provider(s) de detección.");
            await RefreshAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RefreshAsync(
        bool includeAvailableBluetooth = false,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using CancellationTokenSource linkedCancellation = CreateLinkedCancellation(cancellationToken);
        cancellationToken = linkedCancellation.Token;
        if (!_initialized)
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        _includeAvailableBluetooth = includeAvailableBluetooth;
        if (!await _refreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            Interlocked.Exchange(ref _refreshQueued, 1);
            if (includeAvailableBluetooth)
            {
                Interlocked.Exchange(ref _queuedIncludeAvailableBluetooth, 1);
            }

            LoggerService.Instance.Info("Se agrupó una solicitud de actualización de mandos ya en curso.");
            return;
        }

        IReadOnlyList<GamepadListItem>? changedItems = null;
        var errors = new List<GamepadErrorEventArgs>();
        var refreshWarnings = new List<string>();
        bool succeeded = false;
        try
        {
            bool includeAvailable = includeAvailableBluetooth;
            do
            {
                Interlocked.Exchange(ref _refreshQueued, 0);
                if (Interlocked.Exchange(ref _queuedIncludeAvailableBluetooth, 0) == 1)
                {
                    includeAvailable = true;
                }

                try
                {
                    LoggerService.Instance.Info(
                        $"Iniciando actualización de mandos (Bluetooth disponibles: {includeAvailable}).");
                    GamepadDetectionResult snapshot = await _detectionService!
                        .EnumerateAsync(includeAvailable, cancellationToken)
                        .ConfigureAwait(false);
                    errors.AddRange(snapshot.Warnings.Select(warning =>
                        new GamepadErrorEventArgs("Detección", warning)));
                    refreshWarnings.AddRange(snapshot.Warnings);
                    IReadOnlyList<GamepadListItem> newItems = await ReconcileAsync(
                        snapshot.Devices,
                        includeAvailable,
                        cancellationToken).ConfigureAwait(false);
                    _devices = snapshot.Devices;

                    bool visibleItemsChanged = !_hasCompletedSnapshot ||
                                               HaveVisibleChanges(_currentItems, newItems);
                    _currentItems = newItems.ToArray();
                    if (visibleItemsChanged)
                    {
                        changedItems = CurrentItems;
                    }

                    _hasCompletedSnapshot = true;
                    succeeded = true;
                    LoggerService.Instance.Info(
                        $"Enumeración de mandos completada: {snapshot.Devices.Count} dispositivo(s).");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    refreshWarnings.Add(ex.Message);
                    errors.Add(new GamepadErrorEventArgs(
                        "Detección",
                        "No se pudo actualizar la detección; se conserva la última lista válida.",
                        ex));
                }

                includeAvailable = false;
            }
            while (Interlocked.Exchange(ref _refreshQueued, 0) == 1);
        }
        finally
        {
            _refreshGate.Release();
        }

        if (_disposed)
        {
            return;
        }

        foreach (GamepadErrorEventArgs error in errors)
        {
            RaiseError(error);
        }

        if (changedItems != null)
        {
            ListChanged?.Invoke(this, new GamepadListChangedEventArgs(changedItems));
        }

        RefreshCompleted?.Invoke(
            this,
            new GamepadRefreshCompletedEventArgs(succeeded, refreshWarnings.AsReadOnly()));
    }

    public async Task<GamepadRecord> SaveDetectedDeviceAsync(
        string deviceKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceKey))
        {
            throw new ArgumentException("La clave del dispositivo no es válida.", nameof(deviceKey));
        }

        ThrowIfDisposed();
        using CancellationTokenSource linkedCancellation = CreateLinkedCancellation(cancellationToken);
        cancellationToken = linkedCancellation.Token;
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        GamepadRecord record;
        try
        {
            GamepadDevice device = _devices.FirstOrDefault(candidate =>
                string.Equals(candidate.DeviceKey, deviceKey, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("El mando ya no está disponible. Actualiza la lista e inténtalo de nuevo.");

            record = new GamepadRecord
            {
                DisplayName = device.Name.Trim(),
                GamepadType = device.GamepadType,
                ModelName = device.Name,
                VendorId = device.Vid,
                ProductId = device.Pid,
                Identity = device.Identity,
                LastSeenAtUtc = device.IsConnected ? device.LastSeenUtc : null
            };
            await _repository.AddAsync(record, cancellationToken).ConfigureAwait(false);
            _records.Add(record);
            _sessionAssociations[device.DeviceKey] = record.RecordId;
        }
        finally
        {
            _refreshGate.Release();
        }

        await RefreshAsync(_includeAvailableBluetooth, cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task RenameAsync(
        Guid recordId,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        string normalizedName = displayName?.Trim() ?? string.Empty;
        if (normalizedName.Length is 0 or > 120)
        {
            throw new ArgumentException("El nombre debe contener entre 1 y 120 caracteres.", nameof(displayName));
        }

        ThrowIfDisposed();
        using CancellationTokenSource linkedCancellation = CreateLinkedCancellation(cancellationToken);
        cancellationToken = linkedCancellation.Token;
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int recordIndex = _records.FindIndex(record => record.RecordId == recordId);
            if (recordIndex < 0)
            {
                throw new InvalidOperationException("El mando guardado ya no existe.");
            }

            GamepadRecord updated = _records[recordIndex] with { DisplayName = normalizedName };
            await _repository.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);
            _records[recordIndex] = updated;
        }
        finally
        {
            _refreshGate.Release();
        }

        await RefreshAsync(_includeAvailableBluetooth, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveRecordAsync(Guid recordId, CancellationToken cancellationToken = default)
    {
        if (recordId == Guid.Empty)
        {
            throw new ArgumentException("El ID del registro no es válido.", nameof(recordId));
        }

        ThrowIfDisposed();
        using CancellationTokenSource linkedCancellation = CreateLinkedCancellation(cancellationToken);
        cancellationToken = linkedCancellation.Token;
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int recordIndex = _records.FindIndex(record => record.RecordId == recordId);
            if (recordIndex < 0)
            {
                throw new InvalidOperationException("El mando guardado ya no existe.");
            }

            await _repository.DeleteAsync(recordId, cancellationToken).ConfigureAwait(false);
            _records.RemoveAt(recordIndex);
            _connectedRecordIds.Remove(recordId);
            foreach (string key in _sessionAssociations
                         .Where(association => association.Value == recordId)
                         .Select(association => association.Key)
                         .ToList())
            {
                _sessionAssociations.Remove(key);
            }
        }
        finally
        {
            _refreshGate.Release();
        }

        await RefreshAsync(_includeAvailableBluetooth, cancellationToken).ConfigureAwait(false);
    }

    public async Task UnpairBluetoothAsync(
        string bluetoothAddress,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using CancellationTokenSource linkedCancellation = CreateLinkedCancellation(cancellationToken);
        cancellationToken = linkedCancellation.Token;
        await _bluetoothActions.UnpairAsync(bluetoothAddress, cancellationToken).ConfigureAwait(false);
        await RefreshAsync(_includeAvailableBluetooth, cancellationToken).ConfigureAwait(false);
    }

    public async Task ShowBluetoothPickerAsync(
        IntPtr ownerHandle,
        Rectangle anchor,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using CancellationTokenSource linkedCancellation = CreateLinkedCancellation(cancellationToken);
        cancellationToken = linkedCancellation.Token;
        await _bluetoothActions.ShowPickerAsync(ownerHandle, anchor, cancellationToken).ConfigureAwait(true);
        await RefreshAsync(_includeAvailableBluetooth, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _deviceChangeMonitor.DevicesChanged -= DeviceChangeMonitor_DevicesChanged;
        _deviceChangeMonitor.Dispose();
        _repository.ReadError -= Repository_ReadError;
        foreach (IGamepadProvider provider in _providers)
        {
            provider.Dispose();
        }

    }

    private async Task<IReadOnlyList<GamepadListItem>> ReconcileAsync(
        IReadOnlyList<GamepadDevice> devices,
        bool includeAvailableBluetooth,
        CancellationToken cancellationToken)
    {
        var assignedRecords = new HashSet<Guid>();
        var devicesByRecord = new Dictionary<Guid, GamepadDevice>();
        var activeDeviceKeys = devices.Select(device => device.DeviceKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string staleKey in _sessionAssociations.Keys
                     .Where(key => !activeDeviceKeys.Contains(key))
                     .ToList())
        {
            _sessionAssociations.Remove(staleKey);
        }

        foreach (GamepadDevice device in devices)
        {
            GamepadRecord? record = null;
            if (_sessionAssociations.TryGetValue(device.DeviceKey, out Guid recordId))
            {
                record = _records.FirstOrDefault(candidate =>
                    candidate.RecordId == recordId && !assignedRecords.Contains(candidate.RecordId));
            }

            record ??= _identityService.FindUniqueMatch(device, _records, assignedRecords);
            if (record == null)
            {
                continue;
            }

            assignedRecords.Add(record.RecordId);
            devicesByRecord[record.RecordId] = device;
            if (device.IsConnected)
            {
                _sessionAssociations[device.DeviceKey] = record.RecordId;
            }
        }

        var connectedNow = new HashSet<Guid>();
        var items = new List<GamepadListItem>(_records.Count + devices.Count);
        foreach (GamepadRecord storedRecord in _records.ToArray())
        {
            GamepadRecord record = storedRecord;
            devicesByRecord.TryGetValue(record.RecordId, out GamepadDevice? device);
            bool isConnected = device?.IsConnected == true;
            if (isConnected)
            {
                connectedNow.Add(record.RecordId);
                if (!_connectedRecordIds.Contains(record.RecordId))
                {
                    LoggerService.Instance.Info(
                        $"El registro de mando {record.RecordId} cambió a conectado.");
                }

                GamepadIdentity identity = MergeIdentity(record.Identity, device!.Identity);
                if (!_connectedRecordIds.Contains(record.RecordId) ||
                    !Equals(identity, record.Identity) ||
                    record.LastSeenAtUtc == null)
                {
                    GamepadRecord updated = record with
                    {
                        Identity = identity,
                        LastSeenAtUtc = device.LastSeenUtc,
                        VendorId = device.Vid ?? record.VendorId,
                        ProductId = device.Pid ?? record.ProductId
                    };
                    try
                    {
                        await _repository.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);
                        int index = _records.FindIndex(candidate => candidate.RecordId == record.RecordId);
                        _records[index] = updated;
                        record = updated;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        LoggerService.Instance.Error(
                            $"No se pudo actualizar la última detección del mando '{record.DisplayName}': {ex.Message}");
                    }
                }
            }

            items.Add(new GamepadListItem
            {
                RecordId = record.RecordId,
                DisplayName = record.DisplayName,
                ModelName = record.ModelName,
                GamepadType = device?.GamepadType ?? record.GamepadType,
                ConnectionType = isConnected ? device!.ConnectionType : ConnectionType.Unknown,
                ConnectionStatus = isConnected
                    ? GamepadConnectionState.Connected
                    : GamepadConnectionState.Disconnected,
                DeviceKey = device?.DeviceKey,
                BluetoothAddress = device?.Identity.BluetoothAddress ?? record.Identity.BluetoothAddress,
                SerialNumber = device?.Identity.SerialNumber ?? record.Identity.SerialNumber,
                LastSeenUtc = record.LastSeenAtUtc,
                ActiveProfileId = record.ActiveProfileId,
                IsPaired = device?.IsPaired == true,
                CanUnpair = device?.CanUnpair == true
            });
        }

        foreach (GamepadDevice device in devices.Where(candidate =>
                     !devicesByRecord.Values.Any(associated =>
                         string.Equals(associated.DeviceKey, candidate.DeviceKey, StringComparison.OrdinalIgnoreCase)) &&
                     (includeAvailableBluetooth ||
                      candidate.Provider != "Windows Bluetooth" ||
                      candidate.IsConnected)))
        {
            items.Add(new GamepadListItem
            {
                DisplayName = device.Name,
                ModelName = device.Name,
                GamepadType = device.GamepadType,
                ConnectionType = device.IsConnected ? device.ConnectionType : ConnectionType.Unknown,
                ConnectionStatus = device.IsConnected
                    ? GamepadConnectionState.Connected
                    : device.Provider == "Windows Bluetooth"
                        ? GamepadConnectionState.Disconnected
                        : GamepadConnectionState.Unknown,
                DeviceKey = device.DeviceKey,
                BluetoothAddress = device.Identity.BluetoothAddress,
                SerialNumber = device.Identity.SerialNumber,
                LastSeenUtc = device.LastSeenUtc,
                IsPaired = device.IsPaired,
                CanUnpair = device.CanUnpair
            });
        }

        foreach (Guid disconnectedRecordId in _connectedRecordIds.Except(connectedNow))
        {
            LoggerService.Instance.Info(
                $"El registro de mando {disconnectedRecordId} cambió a desconectado.");
        }

        _connectedRecordIds.Clear();
        _connectedRecordIds.UnionWith(connectedNow);
        return items
            .OrderBy(item => item.ConnectionStatus switch
            {
                GamepadConnectionState.Connected => 0,
                GamepadConnectionState.Disconnected => 1,
                _ => 2
            })
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.RecordId)
            .ToList();
    }

    private void DeviceChangeMonitor_DevicesChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _ = RefreshFromDeviceChangeAsync();
    }

    private async Task RefreshFromDeviceChangeAsync()
    {
        try
        {
            await RefreshAsync(_includeAvailableBluetooth, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_disposed || _lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            RaiseError(new GamepadErrorEventArgs(
                "Detección",
                "No se pudo actualizar la lista después de un cambio de dispositivo.",
                ex));
        }
    }

    private void Repository_ReadError(string message)
    {
        RaiseError(new GamepadErrorEventArgs("Persistencia", message));
    }

    private void RaiseError(GamepadErrorEventArgs error)
    {
        if (_disposed)
        {
            return;
        }

        if (error.Exception == null)
        {
            LoggerService.Instance.Warning($"{error.Source}: {error.Message}");
        }
        else
        {
            LoggerService.Instance.Error($"{error.Source}: {error.Message} {error.Exception}");
        }

        ErrorOccurred?.Invoke(this, error);
    }

    private static GamepadIdentity MergeIdentity(GamepadIdentity current, GamepadIdentity observed)
    {
        return current with
        {
            SerialNumber = current.SerialNumber ?? observed.SerialNumber,
            BluetoothAddress = current.BluetoothAddress ?? observed.BluetoothAddress,
            InstancePath = current.InstancePath ?? observed.InstancePath
        };
    }

    private static bool HaveVisibleChanges(
        IReadOnlyList<GamepadListItem> current,
        IReadOnlyList<GamepadListItem> next)
    {
        if (current.Count != next.Count)
        {
            return true;
        }

        for (int index = 0; index < current.Count; index++)
        {
            GamepadListItem previous = current[index];
            GamepadListItem updated = next[index];
            if (previous.RecordId != updated.RecordId ||
                !string.Equals(previous.DisplayName, updated.DisplayName, StringComparison.Ordinal) ||
                !string.Equals(previous.ModelName, updated.ModelName, StringComparison.Ordinal) ||
                previous.GamepadType != updated.GamepadType ||
                previous.ConnectionType != updated.ConnectionType ||
                previous.ConnectionStatus != updated.ConnectionStatus ||
                !string.Equals(previous.DeviceKey, updated.DeviceKey, StringComparison.Ordinal) ||
                !string.Equals(previous.BluetoothAddress, updated.BluetoothAddress, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(previous.SerialNumber, updated.SerialNumber, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(previous.ActiveProfileId, updated.ActiveProfileId, StringComparison.Ordinal) ||
                previous.IsPaired != updated.IsPaired ||
                previous.CanUnpair != updated.CanUnpair)
            {
                return true;
            }
        }

        return false;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private CancellationTokenSource CreateLinkedCancellation(CancellationToken cancellationToken)
    {
        return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
    }
}
