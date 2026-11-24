using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using GDM.Models;
using GDM.Services.Abstractions;

namespace GDM.ViewModels;

public sealed class HidHideViewModel : INotifyPropertyChanged
{
    private readonly IHidHideService _service;
    private readonly Action<DiagnosticLogLevel, string, Exception?> _writeLog;
    private HidHideStatus? _status;
    private string _statusMessage = "Comprueba el estado de HidHide.";
    private bool _isRefreshing;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<HidHideControllerItem> Devices { get; } = [];
    public bool ShowEmptyDevicesMessage => Devices.Count == 0;
    public ICommand RefreshCommand { get; }
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string DriverStatus => _status switch
    {
        null => "Estado sin comprobar",
        { IsAvailable: false } => "Controlador HidHide no disponible",
        { IsEnabled: false } => "HidHide instalado, ocultamiento desactivado",
        { IsInverseWhitelist: true } => "Modo de lista invertida no compatible",
        _ => "HidHide activo"
    };

    public bool CanManageDevices =>
        !_isRefreshing &&
        _status is { IsAvailable: true, IsEnabled: true, IsInverseWhitelist: false };

    public HidHideViewModel(
        IHidHideService service,
        Action<DiagnosticLogLevel, string, Exception?>? writeLog = null)
    {
        _service = service;
        _writeLog = writeLog ?? ((_, _, _) => { });
        RefreshCommand = new AsyncCommand(RefreshStatusAsync, () => !_isRefreshing);
    }

    public void UpdateDevices(IEnumerable<ConnectedGamepad> devices)
    {
        var existing = Devices.ToDictionary(item => item.Device.DeviceKey, StringComparer.Ordinal);
        ConnectedGamepad[] connectedDevices = devices.ToArray();
        var connectedKeys = connectedDevices
            .Select(device => device.DeviceKey)
            .ToHashSet(StringComparer.Ordinal);
        var connectedPaths = connectedDevices
            .Select(device => device.DevicePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool canManage = CanManageDevices;

        var targetStates = new List<(ConnectedGamepad Device, bool Hidden, HidHideControllerItem? Item)>(
            connectedDevices.Length);
        foreach (ConnectedGamepad device in connectedDevices)
        {
            bool hidden = false;
            if (!string.IsNullOrWhiteSpace(device.DevicePath))
            {
                try
                {
                    hidden = _service.IsHiddenByGdm(device.DevicePath);
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or
                    System.ComponentModel.Win32Exception or
                    IOException)
                {
                    _writeLog(
                        DiagnosticLogLevel.Warning,
                        $"No se pudo consultar el estado oculto de «{device.Name}».",
                        exception);
                }
            }

            ConnectedGamepad displayDevice = device with { IsHiddenByGdm = hidden };
            existing.TryGetValue(device.DeviceKey, out HidHideControllerItem? item);
            targetStates.Add((displayDevice, hidden, item));
        }

        foreach (HidHideControllerItem item in existing.Values)
        {
            if (connectedKeys.Contains(item.Device.DeviceKey) ||
                string.IsNullOrWhiteSpace(item.Device.DevicePath) ||
                connectedPaths.Contains(item.Device.DevicePath))
            {
                continue;
            }

            bool stillHidden;
            try
            {
                stillHidden = _service.IsHiddenByGdm(item.Device.DevicePath);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or
                System.ComponentModel.Win32Exception or
                IOException)
            {
                continue;
            }

            if (stillHidden)
            {
                targetStates.Add((item.Device, true, item));
            }
        }

        if (Devices.Count == targetStates.Count &&
            Devices.Select((entry, index) =>
                {
                    (ConnectedGamepad device, bool hidden, _) = targetStates[index];
                    return entry.Device == device &&
                        entry.IsHiddenByGdm == hidden;
                }).All(equal => equal))
        {
            return;
        }

        Devices.Clear();
        foreach ((ConnectedGamepad displayDevice, bool hidden, HidHideControllerItem? item) in targetStates)
        {
            if (item is not null)
            {
                item.Update(displayDevice, hidden, canManage);
                Devices.Add(item);
            }
            else
            {
                Devices.Add(new HidHideControllerItem(
                    displayDevice,
                    hidden,
                    canManage,
                    ToggleDeviceAsync));
            }
        }

        OnPropertyChanged(nameof(ShowEmptyDevicesMessage));
    }

    public IReadOnlyList<ConnectedGamepad> GetHiddenDevices() =>
        Devices
            .Where(item => item.IsHiddenByGdm && !item.Device.IsRedundantInterface)
            .Select(item => item.Device with { IsHiddenByGdm = true })
            .ToArray();

    public async Task<bool> HideAutomaticallyAsync(ConnectedGamepad device)
    {
        if (string.IsNullOrWhiteSpace(device.DevicePath))
        {
            return false;
        }

        if (_service.IsHiddenByGdm(device.DevicePath))
        {
            return true;
        }

        try
        {
            await _service.HideUntilGdmClosesAsync(device.DevicePath);
            UpdateDevices(Devices.Select(item => item.Device)
                .Append(device)
                .DistinctBy(item => item.DeviceKey));
            _writeLog(DiagnosticLogLevel.Information, "Ocultamiento automático aplicado tras iniciar la salida virtual.", null);
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception or
            InvalidDataException or
            IOException or
            UnauthorizedAccessException)
        {
            StatusMessage = $"No se pudo ocultar automáticamente «{device.Name}»: {exception.Message}";
            _writeLog(DiagnosticLogLevel.Error, "No se pudo aplicar el ocultamiento automático.", exception);
            return false;
        }
    }

    public async Task<bool> ShowAutomaticallyAsync(ConnectedGamepad device)
    {
        if (string.IsNullOrWhiteSpace(device.DevicePath) ||
            !_service.IsHiddenByGdm(device.DevicePath))
        {
            return true;
        }

        try
        {
            await _service.ShowDeviceAsync(device.DevicePath);
            UpdateDevices(Devices.Select(item => item.Device)
                .Append(device)
                .DistinctBy(item => item.DeviceKey));
            _writeLog(DiagnosticLogLevel.Information, "Se mostró el mando tras detener su salida virtual automática.", null);
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception or
            InvalidDataException or
            IOException or
            UnauthorizedAccessException)
        {
            StatusMessage = $"No se pudo mostrar automáticamente «{device.Name}»: {exception.Message}";
            _writeLog(DiagnosticLogLevel.Error, "No se pudo restaurar el mando después de detener su salida.", exception);
            return false;
        }
    }

    public async Task RefreshStatusAsync()
    {
        if (_isRefreshing)
        {
            return;
        }

        _isRefreshing = true;
        OnStatusChanged();
        try
        {
            _status = await _service.GetStatusAsync();
            StatusMessage = _status switch
            {
                { IsAvailable: false } =>
                    "No se encontró el controlador de HidHide activo. Instálalo por separado desde la fuente oficial.",
                { IsEnabled: false } =>
                    "HidHide está disponible, pero su ocultamiento global está desactivado. Actívalo desde la configuración oficial; GDM no cambiará este ajuste.",
                { IsInverseWhitelist: true } =>
                    "La lista de aplicaciones está invertida. Cambia ese modo desde HidHide antes de ocultar mandos para que GDM conserve acceso.",
                _ =>
                    "GDM restaurará al cerrar las entradas de lista negra que haya agregado. Si GDM se cierra inesperadamente, las recuperará al siguiente inicio."
            };
        }
        catch (InvalidOperationException exception)
        {
            _status = new HidHideStatus(false, false, false);
            StatusMessage = exception.Message;
            _writeLog(DiagnosticLogLevel.Error, "No se pudo consultar el controlador HidHide.", exception);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            _status = new HidHideStatus(false, false, false);
            StatusMessage = $"No se pudo consultar HidHide: {exception.Message}";
            _writeLog(DiagnosticLogLevel.Error, "No se pudo consultar el controlador HidHide.", exception);
        }
        finally
        {
            _isRefreshing = false;
            OnStatusChanged();
            UpdateDevices(Devices.Select(item => item.Device).ToArray());
        }
    }

    private async Task ToggleDeviceAsync(HidHideControllerItem item)
    {
        if (!CanManageDevices || string.IsNullOrWhiteSpace(item.Device.DevicePath))
        {
            return;
        }

        item.SetBusy(true);
        try
        {
            if (item.IsHiddenByGdm)
            {
                await _service.ShowDeviceAsync(item.Device.DevicePath);
                item.SetHidden(false);
                StatusMessage = $"«{item.Device.Name}» se mostró de nuevo.";
                _writeLog(DiagnosticLogLevel.Information, "Se quitó de la lista negra la entrada agregada por GDM.", null);
            }
            else
            {
                await _service.HideUntilGdmClosesAsync(item.Device.DevicePath);
                item.SetHidden(true);
                StatusMessage = $"«{item.Device.Name}» está oculto para otras aplicaciones.";
                _writeLog(DiagnosticLogLevel.Information, "Se agregó el mando a la lista negra persistente de HidHide.", null);
            }
        }
        catch (InvalidOperationException exception)
        {
            StatusMessage = exception.Message;
            _writeLog(DiagnosticLogLevel.Error, "No se pudo cambiar la lista negra de HidHide.", exception);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            StatusMessage = $"HidHide no pudo completar la operación: {exception.Message}";
            _writeLog(DiagnosticLogLevel.Error, "No se pudo cambiar la lista negra de HidHide.", exception);
        }
        catch (InvalidDataException exception)
        {
            StatusMessage = $"HidHide devolvió datos no válidos: {exception.Message}";
            _writeLog(DiagnosticLogLevel.Error, "HidHide devolvió una lista de dispositivos no válida.", exception);
        }
        catch (IOException exception)
        {
            StatusMessage = $"No se pudo resolver el dispositivo HID: {exception.Message}";
            _writeLog(DiagnosticLogLevel.Error, "No se pudo cambiar la lista negra de HidHide.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            StatusMessage = $"No hay permisos para actualizar el diario de recuperación de HidHide: {exception.Message}";
            _writeLog(DiagnosticLogLevel.Error, "No se pudo actualizar el diario de recuperación de HidHide.", exception);
        }
        finally
        {
            item.SetBusy(false);
            bool isHidden = _service.IsHiddenByGdm(item.Device.DevicePath);
            item.Update(item.Device, isHidden, CanManageDevices);
        }
    }

    public async Task RestoreManagedChangesAsync(
        IReadOnlyDictionary<string, bool>? hiddenDeviceExitPolicies = null,
        bool keepUnlistedDevicesHidden = false)
    {
        try
        {
            if (hiddenDeviceExitPolicies is null)
            {
                await _service.RestoreManagedChangesAsync();
            }
            else
            {
                await _service.RestoreManagedChangesAsync(
                    hiddenDeviceExitPolicies,
                    keepUnlistedDevicesHidden);
            }
        }
        catch (Exception exception) when (
            exception is IOException or
            InvalidDataException or
            UnauthorizedAccessException or
            InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
            StatusMessage =
                $"No se pudo restaurar HidHide. GDM seguirá abierto para evitar dejar el mando oculto: {exception.Message}";
            _writeLog(DiagnosticLogLevel.Error, "Falló la restauración de la lista negra al cerrar GDM.", exception);
            throw;
        }
    }

    private void OnStatusChanged()
    {
        OnPropertyChanged(nameof(DriverStatus));
        OnPropertyChanged(nameof(CanManageDevices));
        foreach (HidHideControllerItem item in Devices)
        {
            item.Update(item.Device, item.IsHiddenByGdm, CanManageDevices);
        }

        if (RefreshCommand is AsyncCommand command)
        {
            command.NotifyCanExecuteChanged();
        }
    }

    private bool SetProperty<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class AsyncCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => canExecute();
        public async void Execute(object? parameter) => await execute();
        public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class HidHideControllerItem : INotifyPropertyChanged
{
    private readonly Func<HidHideControllerItem, Task> _toggle;
    private bool _canManage;
    private bool _isBusy;
    private bool _isHiddenByGdm;
    private ConnectedGamepad _device;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ConnectedGamepad Device => _device;
    public string Name => _device.IsRedundantInterface
        ? $"{_device.Name} (interfaz secundaria)"
        : _device.Name;
    public string HardwareIdLabel => _device.HardwareIdLabel;
    public bool CanManage => _canManage && !string.IsNullOrWhiteSpace(_device.DevicePath) && !_isBusy;
    public bool IsHiddenByGdm => _isHiddenByGdm;
    public string ToggleButtonText => _isHiddenByGdm ? "Mostrar mando" : "Ocultar mando";
    public string StateText => string.IsNullOrWhiteSpace(_device.DevicePath)
        ? "SDL no proporcionó la ruta HID"
        : _isHiddenByGdm
            ? "Oculto por GDM"
            : "Visible";
    public ICommand ToggleCommand { get; }

    public HidHideControllerItem(
        ConnectedGamepad device,
        bool isHidden,
        bool canManage,
        Func<HidHideControllerItem, Task> toggle)
    {
        _device = device;
        _isHiddenByGdm = isHidden;
        _canManage = canManage;
        _toggle = toggle;
        ToggleCommand = new AsyncCommand(() => _toggle(this), () => CanManage);
    }

    public void Update(ConnectedGamepad device, bool isHidden, bool canManage)
    {
        _device = device;
        _isHiddenByGdm = isHidden;
        _canManage = canManage;
        OnPropertyChanged(nameof(Device));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(HardwareIdLabel));
        OnPropertyChanged(nameof(CanManage));
        OnPropertyChanged(nameof(IsHiddenByGdm));
        OnPropertyChanged(nameof(ToggleButtonText));
        OnPropertyChanged(nameof(StateText));
        if (ToggleCommand is AsyncCommand command)
        {
            command.NotifyCanExecuteChanged();
        }
    }

    public void SetHidden(bool hidden)
    {
        _isHiddenByGdm = hidden;
        OnPropertyChanged(nameof(IsHiddenByGdm));
        OnPropertyChanged(nameof(ToggleButtonText));
        OnPropertyChanged(nameof(StateText));
    }

    public void SetBusy(bool busy)
    {
        _isBusy = busy;
        OnPropertyChanged(nameof(CanManage));
        if (ToggleCommand is AsyncCommand command)
        {
            command.NotifyCanExecuteChanged();
        }
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class AsyncCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => canExecute();
        public async void Execute(object? parameter) => await execute();
        public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
