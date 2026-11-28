using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using GDM.Models;
using GDM.Services;

namespace GDM.ViewModels;

/// <summary>
/// ViewModel de la pestaña Bluetooth: lista de dispositivos detectados,
/// emparejamiento manual y ajuste de auto-conexión de mandos.
/// </summary>
public sealed class BluetoothViewModel : INotifyPropertyChanged
{
    private readonly BluetoothDeviceService _service;
    private readonly Dispatcher _dispatcher;
    private readonly SettingsViewModel? _settings;
    private readonly Action<string, string, string> _notify;
    private readonly Action<string> _log;
    private readonly Dictionary<string, BluetoothDeviceItem> _allDevices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _unpairedPending = new(StringComparer.OrdinalIgnoreCase);

    // Tras un desemparejado manual la tarjeta se oculta durante este tiempo:
    // Windows puede seguir reportando el AEP como presente mientras el mando
    // esté encendido y la tarjeta «Detectado» quedaría fantasma. Pasado el
    // plazo, si el aparato sigue al alcance vuelve a aparecer (es honestamente
    // descubrible). «Buscar» limpia la supresión antes para re-emparejar ya.
    private static readonly TimeSpan UnpairedHideDuration = TimeSpan.FromMinutes(5);

    private bool _isBluetoothAvailable;
    private string _bluetoothStatusText = "Comprobando Bluetooth…";
    private bool _autoConnectEnabled;
    private bool _autoPairLoop;
    private bool _showAllDevices;
    private string _statusMessage = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public BluetoothViewModel(
        BluetoothDeviceService service,
        SettingsViewModel? settings,
        Dispatcher dispatcher,
        Action<string, string, string> notify,
        Action<string> log)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _settings = settings;
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _notify = notify ?? throw new ArgumentNullException(nameof(notify));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        _autoConnectEnabled = settings?.BluetoothAutoConnect ?? false;
        _autoPairLoop = settings?.BluetoothAutoPairLoop ?? false;
        _showAllDevices = settings?.BluetoothShowAllDevices ?? false;
        _service.IsAutoConnectEnabled = _autoConnectEnabled;
        _service.SetAutoPairExcludedDevices(settings?.BluetoothAutoConnectExcluded ?? []);

        RefreshCommand = new AsyncCommand(RefreshAsync, () => true);
        OpenWindowsSettingsCommand = new RelayCommand(OpenWindowsBluetoothSettings);
        PairCommand = new AsyncParameterCommand(
            parameter => parameter is BluetoothDeviceItem item ? PairAsync(item) : Task.CompletedTask,
            parameter => parameter is BluetoothDeviceItem { IsBusy: false });
        UnpairCommand = new AsyncParameterCommand(
            parameter => parameter is BluetoothDeviceItem item ? UnpairAsync(item) : Task.CompletedTask,
            parameter => parameter is BluetoothDeviceItem { IsBusy: false });
    }

    public ObservableCollection<BluetoothDeviceItem> Devices { get; } = [];

    public ICommand RefreshCommand { get; }
    public ICommand OpenWindowsSettingsCommand { get; }
    public ICommand PairCommand { get; }
    public ICommand UnpairCommand { get; }

    public bool IsBluetoothAvailable
    {
        get => _isBluetoothAvailable;
        private set
        {
            if (SetProperty(ref _isBluetoothAvailable, value))
            {
                OnPropertyChanged(nameof(ShowBluetoothUnavailable));
                OnPropertyChanged(nameof(ShowBluetoothContent));
            }
        }
    }

    public bool ShowBluetoothUnavailable => !_isBluetoothAvailable;
    public bool ShowBluetoothContent => _isBluetoothAvailable;

    public string BluetoothStatusText
    {
        get => _bluetoothStatusText;
        private set => SetProperty(ref _bluetoothStatusText, value);
    }

    public string UnavailableReasonText => _service.RadioState switch
    {
        BluetoothRadioState.Off => "El Bluetooth está desactivado en Windows.",
        BluetoothRadioState.Unknown => "Este equipo no tiene Bluetooth o no se pudo detectar.",
        _ => "El Bluetooth no está disponible."
    };

    public bool AutoConnectEnabled
    {
        get => _autoConnectEnabled;
        set
        {
            if (_autoConnectEnabled == value)
            {
                return;
            }

            _autoConnectEnabled = value;
            _service.IsAutoConnectEnabled = value;
            OnPropertyChanged();
            _ = PersistAutoConnectAsync(value);
        }
    }

    public bool AutoPairLoopEnabled
    {
        get => _autoPairLoop;
        set
        {
            if (_autoPairLoop == value)
            {
                return;
            }

            _autoPairLoop = value;
            OnPropertyChanged();
            _ = PersistAutoPairLoopAsync(value);
        }
    }

    public bool ShowAllDevices
    {
        get => _showAllDevices;
        set
        {
            if (_showAllDevices == value)
            {
                return;
            }

            _showAllDevices = value;
            OnPropertyChanged();
            RebuildVisibleDevices();
            _ = PersistShowAllAsync(value);
        }
    }

    public bool HasVisibleDevices => Devices.Count > 0;
    public bool ShowEmptyMessage => _isBluetoothAvailable && Devices.Count == 0;
    public string EmptyMessage => _showAllDevices
        ? "No se detectaron dispositivos Bluetooth. Pon un mando en modo emparejamiento y pulsa Buscar."
        : "No se detectaron mandos. Activa «Mostrar todos los dispositivos» para ver cualquier aparato Bluetooth, o pon el mando en modo emparejamiento.";

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public void Initialize()
    {
        _service.DeviceAdded += OnDeviceAdded;
        _service.DeviceUpdated += OnDeviceUpdated;
        _service.DeviceRemoved += OnDeviceRemoved;
        _service.RadioStateChanged += OnRadioStateChanged;
        _service.AutoPairHandler = AutoPairFromDetection;
        ApplyRadioState(_service.RadioState);
        _service.Start();
    }

    public void Dispose()
    {
        _service.DeviceAdded -= OnDeviceAdded;
        _service.DeviceUpdated -= OnDeviceUpdated;
        _service.DeviceRemoved -= OnDeviceRemoved;
        _service.RadioStateChanged -= OnRadioStateChanged;
        _service.Stop();
    }

    private void ApplyRadioState(BluetoothRadioState state)
    {
        bool available = state == BluetoothRadioState.On;
        IsBluetoothAvailable = available;
        BluetoothStatusText = state switch
        {
            BluetoothRadioState.On => "Bluetooth activado",
            BluetoothRadioState.Off => "Bluetooth desactivado",
            _ => "Bluetooth no detectado"
        };
        OnPropertyChanged(nameof(UnavailableReasonText));
        OnPropertyChanged(nameof(ShowEmptyMessage));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    private void OnRadioStateChanged(BluetoothRadioState state) =>
        _dispatcher.BeginInvoke(() => ApplyRadioState(state));

    private void OnDeviceAdded(BluetoothDeviceInfo info) =>
        _dispatcher.BeginInvoke(() => UpsertDevice(info));

    private void OnDeviceUpdated(BluetoothDeviceInfo info) =>
        _dispatcher.BeginInvoke(() => UpsertDevice(info));

    private void OnDeviceRemoved(string id) =>
        _dispatcher.BeginInvoke(() => RemoveDevice(id));

    private BluetoothDeviceItem CreateItem(BluetoothDeviceInfo info)
    {
        var item = new BluetoothDeviceItem(info, PairCommand, UnpairCommand)
        {
            AutoConnectBlocked = _service.IsAutoPairExcluded(info.Address, info.Id)
        };
        item.AutoConnectBlockedChanged = changed => _ = OnAutoConnectBlockedChangedAsync(changed);
        return item;
    }

    private void UpsertDevice(BluetoothDeviceInfo info)
    {
        // Tras un desemparejado manual, el vigilante puede seguir reportando
        // el estado «Emparejado» durante unos segundos; se fuerza el estado
        // real para que la tarjeta no vuelva a mostrar el estado obsoleto.
        // La entrada dura UnpairedHideDuration: sirve tanto para corregir el
        // estado como para ocultar la tarjeta mientras Windows asienta el
        // cambio (un AEP «recordado» puede seguir presente un rato).
        if (_unpairedPending.TryGetValue(info.Id, out DateTime since))
        {
            TimeSpan age = DateTime.UtcNow - since;
            // Un IsPaired confirmado pasados 30 s se cree: es un
            // re-emparejado real (propio o desde Ajustes de Windows), no un
            // resto obsoleto del desemparejado anterior.
            if (age > UnpairedHideDuration ||
                (info.IsPaired && age > TimeSpan.FromSeconds(30)))
            {
                _unpairedPending.Remove(info.Id);
            }
            else
            {
                info = info with { IsPaired = false, IsConnected = false };
            }
        }

        if (!_allDevices.TryGetValue(info.Id, out BluetoothDeviceItem? item))
        {
            item = CreateItem(info);
            _allDevices[info.Id] = item;
        }
        else
        {
            item.Update(info);
        }

        RebuildVisibleDevices();
    }

    private void RemoveDevice(string id)
    {
        _allDevices.Remove(id);
        _unpairedPending.Remove(id);
        RebuildVisibleDevices();
    }

    /// <summary>
    /// True si el dispositivo acaba de ser desemparejado manualmente y su
    /// tarjeta debe permanecer oculta (ver UnpairedHideDuration).
    /// </summary>
    private bool IsUnpairedPending(BluetoothDeviceItem item)
    {
        if (!_unpairedPending.TryGetValue(item.Id, out DateTime since))
        {
            return false;
        }

        if (DateTime.UtcNow - since > UnpairedHideDuration)
        {
            _unpairedPending.Remove(item.Id);
            return false;
        }

        return true;
    }

    // Algunos mandos (Xbox One) exponen dos identidades Bluetooth con la
    // misma dirección: la clásica de juego y otra de accesorios. Windows
    // las enumera como AEPs separados y la lista mostraría el mismo mando
    // dos veces; se agrupan por dirección y se muestra solo la mejor
    // entrada (conectada > emparejada).
    private static string? NormalizeAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var builder = new System.Text.StringBuilder(address.Length);
        foreach (char c in address)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToUpperInvariant(c));
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private void RebuildVisibleDevices()
    {
        Devices.Clear();
        foreach (BluetoothDeviceItem item in _allDevices.Values
                     // Los no emparejados solo se muestran si están presentes:
                     // los AEP «recordados» que Windows sigue enumerando tras
                     // un desemparejado ocultarían la desaparición de la tarjeta.
                     .Where(item => (_showAllDevices || item.LooksLikeGamepad) &&
                                   (item.IsPaired ||
                                    (item.IsPresent && !IsUnpairedPending(item))))
                     .GroupBy(item => NormalizeAddress(item.Address) ?? item.Id)
                     .Select(group => group
                         .OrderByDescending(entry => entry.IsConnected)
                         .ThenByDescending(entry => entry.IsPaired)
                         .First())
                     .OrderByDescending(item => item.LooksLikeGamepad)
                     .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Devices.Add(item);
        }

        OnPropertyChanged(nameof(HasVisibleDevices));
        OnPropertyChanged(nameof(ShowEmptyMessage));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    /// <summary>
    /// Marca como «recién desemparejado» el dispositivo y sus identidades
    /// hermanas (algunos mandos exponen dos AEPs con la misma dirección MAC;
    /// desemparejar uno deja al otro visible como tarjeta fantasma).
    /// </summary>
    private void MarkUnpairedPending(BluetoothDeviceItem item)
    {
        string? groupKey = NormalizeAddress(item.Address);
        foreach (BluetoothDeviceItem sibling in _allDevices.Values)
        {
            if (sibling.Id == item.Id ||
                (groupKey is not null && NormalizeAddress(sibling.Address) == groupKey))
            {
                _unpairedPending[sibling.Id] = DateTime.UtcNow;
            }
        }
    }

    private async Task RefreshAsync()
    {
        // «Buscar» es la vía deliberada de volver a ver dispositivos recién
        // desemparejados: levanta la supresión de sus tarjetas.
        _unpairedPending.Clear();
        RebuildVisibleDevices();
        StatusMessage = "Buscando dispositivos…";
        _service.Rescan();
        await Task.Delay(1200);
        StatusMessage = Devices.Count == 0
            ? "Sin dispositivos. Si el mando no aparece, activa «Mostrar todos los dispositivos» o espera a que entre en modo emparejamiento."
            : $"{Devices.Count} dispositivo(s) visible(s).";
    }

    private void OpenWindowsBluetoothSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:bluetooth")
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            _log($"No se pudo abrir la configuración de Bluetooth de Windows: {exception.Message}");
            StatusMessage = "No se pudo abrir la configuración de Bluetooth de Windows.";
        }
    }

    private Task PairAsync(BluetoothDeviceItem item) => PairDeviceAsync(item);

    /// <summary>
    /// Emparejamiento automático disparado por el servicio al detectar un
    /// mando. Usa exactamente el mismo método que el botón manual. Sin bucle
    /// hace un único intento; con «Reintentar emparejamiento» activo repite
    /// cada 5 segundos hasta emparejar o hasta que el dispositivo desaparezca.
    /// </summary>
    private async Task AutoPairFromDetection(BluetoothDeviceInfo info)
    {
        BluetoothDeviceItem item = await _dispatcher.InvokeAsync(() =>
        {
            if (!_allDevices.TryGetValue(info.Id, out BluetoothDeviceItem? existing) || existing is null)
            {
                existing = CreateItem(info);
                _allDevices[info.Id] = existing;
            }

            RebuildVisibleDevices();
            return existing;
        });

        // Windows pide confirmación la primera vez que se empareja un
        // dispositivo; avisamos para que el usuario sepa que debe aceptar
        // el diálogo del sistema si aparece.
        _notify(
            NotificationEvents.PairingRequested,
            "Emparejamiento solicitado",
            $"«{item.Name}» está en modo emparejamiento. Si Windows muestra un diálogo de confirmación, acéptalo.");

        // Sin bucle: un único intento. Con bucle activo: se reintenta cada
        // 5 segundos hasta emparejar, hasta que el dispositivo salga de la
        // lista, se desactive la auto-conexión o se suprima manualmente.
        int attempt = 0;
        while (true)
        {
            attempt++;
            BluetoothPairResult result = await PairDeviceAsync(item, attempt);
            if (result is BluetoothPairResult.Paired or BluetoothPairResult.AlreadyPaired)
            {
                return;
            }

            if (!_autoPairLoop ||
                !_autoConnectEnabled ||
                _service.IsAutoPairSuppressed(info.Id) ||
                !await _dispatcher.InvokeAsync(() => _allDevices.ContainsKey(info.Id)))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    }

    private async Task<BluetoothPairResult> PairDeviceAsync(
        BluetoothDeviceItem item, int? autoAttempt = null)
    {
        item.IsBusy = true;
        StatusMessage = autoAttempt is int attempt
            ? $"Autoemparejando «{item.Name}»… intento {attempt}"
            : $"Emparejando «{item.Name}»…";
        BluetoothPairResult result;
        try
        {
            // La operación de emparejamiento debe crearse en el hilo de UI:
            // si no, Windows no puede mostrar su diálogo de confirmación
            // y la llamada falla (afecta al emparejamiento automático,
            // que llega desde un hilo de fondo).
            Task<BluetoothPairResult> pairTask = await _dispatcher.InvokeAsync(
                () => _service.PairAsync(item.Id));
            result = await pairTask;
            if (result is not (BluetoothPairResult.Paired or BluetoothPairResult.AlreadyPaired) &&
                await _service.IsDevicePairedAsync(item.Id))
            {
                // Windows pudo completar el emparejamiento por su propio
                // flujo aunque nuestra llamada haya devuelto error.
                result = BluetoothPairResult.AlreadyPaired;
            }

            StatusMessage = result switch
            {
                BluetoothPairResult.Paired => $"«{item.Name}» emparejado correctamente.",
                BluetoothPairResult.AlreadyPaired => $"«{item.Name}» ya estaba emparejado.",
                BluetoothPairResult.NotReady => $"«{item.Name}» no está listo para emparejar. Pulsa su botón de emparejamiento y reintenta.",
                BluetoothPairResult.HardwareUnavailable => "El adaptador Bluetooth no está disponible.",
                BluetoothPairResult.NotAllowed => "Windows no permite emparejar este dispositivo.",
                BluetoothPairResult.Rejected => $"Windows rechazó el emparejamiento de «{item.Name}».",
                BluetoothPairResult.Canceled => $"Emparejamiento de «{item.Name}» cancelado.",
                _ => $"No se pudo emparejar «{item.Name}»."
            };
            if (result is BluetoothPairResult.Paired or BluetoothPairResult.AlreadyPaired)
            {
                _unpairedPending.Remove(item.Id);
                _notify(NotificationEvents.DevicePaired, "Mando emparejado", $"«{item.Name}» se emparejó por Bluetooth.");
            }
        }
        finally
        {
            item.IsBusy = false;
        }

        return result;
    }

    private async Task UnpairAsync(BluetoothDeviceItem item)
    {
        item.IsBusy = true;
        StatusMessage = $"Desemparejando «{item.Name}»…";
        try
        {
            bool ok = await _service.UnpairAsync(item.Id);
            if (!ok && await _service.IsDevicePairedAsync(item.Id) == false)
            {
                // Windows pudo completar el desemparejado por su propio flujo
                // aunque la llamada haya devuelto error.
                ok = true;
            }

            if (ok)
            {
                item.MarkUnpaired();
                MarkUnpairedPending(item);
                // La tarjeta desaparece al instante; sin esto seguiría
                // visible como «Detectado» mientras Windows reporte el AEP
                // como presente (el mando encendido sigue anunciando).
                RebuildVisibleDevices();
                // Margen para que Windows asiente el cambio de pairing antes
                // de re-enumerar; si no, el vigilante puede reportar el
                // dispositivo con el estado de emparejado aún obsoleto.
                await Task.Delay(1000);
                _service.Rescan();
            }

            StatusMessage = ok
                ? $"«{item.Name}» desemparejado."
                : $"No se pudo desemparejar «{item.Name}».";
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    /// <summary>
    /// Persiste la exclusión del dispositivo del auto-connect cuando el
    /// usuario toca su casija. La clave es la dirección normalizada, así
    /// que cubre ambas identidades Bluetooth del mismo mando físico.
    /// </summary>
    private async Task OnAutoConnectBlockedChangedAsync(BluetoothDeviceItem item)
    {
        string key = BluetoothDeviceService.AutoPairExcludeKey(item.Address, item.Id);
        var excluded = (_settings?.BluetoothAutoConnectExcluded ?? [])
            .Where(existing => !string.Equals(existing, key, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (item.AutoConnectBlocked)
        {
            excluded.Add(key);
        }

        _service.SetAutoPairExcludedDevices(excluded);
        await PersistAutoConnectExcludedAsync(excluded);
    }

    private async Task PersistAutoConnectExcludedAsync(IReadOnlyList<string> excludedDevices)
    {
        try
        {
            if (_settings is not null)
            {
                await _settings.SetBluetoothAutoConnectExcludedAsync(excludedDevices);
            }
        }
        catch (Exception exception)
        {
            _log($"No se pudo guardar BluetoothAutoConnectExcluded: {exception.Message}");
        }
    }

    private async Task PersistAutoConnectAsync(bool value)
    {
        try
        {
            if (_settings is not null)
            {
                await _settings.SetBluetoothAutoConnectAsync(value);
            }
        }
        catch (Exception exception)
        {
            _log($"No se pudo guardar BluetoothAutoConnect: {exception.Message}");
        }
    }

    private async Task PersistAutoPairLoopAsync(bool value)
    {
        try
        {
            if (_settings is not null)
            {
                await _settings.SetBluetoothAutoPairLoopAsync(value);
            }
        }
        catch (Exception exception)
        {
            _log($"No se pudo guardar BluetoothAutoPairLoop: {exception.Message}");
        }
    }

    private async Task PersistShowAllAsync(bool value)
    {
        try
        {
            if (_settings is not null)
            {
                await _settings.SetBluetoothShowAllDevicesAsync(value);
            }
        }
        catch (Exception exception)
        {
            _log($"No se pudo guardar BluetoothShowAllDevices: {exception.Message}");
        }
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class AsyncCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => canExecute();
        public async void Execute(object? parameter) => await execute();
    }

    private sealed class AsyncParameterCommand(
        Func<object?, Task> execute,
        Func<object?, bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => canExecute(parameter);
        public async void Execute(object? parameter) => await execute(parameter);
    }

    private sealed class RelayCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
    }
}
