using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using GDM.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace GDM.Services;

public enum BluetoothTransport
{
    Unknown,
    Classic,
    LowEnergy
}

public sealed record BluetoothDeviceInfo(
    string Id,
    string Name,
    string? Address,
    BluetoothTransport Transport,
    bool IsPaired,
    bool IsConnected,
    bool CanPair,
    bool LooksLikeGamepad,
    bool IsPresent);

/// <summary>
/// Descubre dispositivos Bluetooth (clásico y BLE) con un DeviceWatcher de
/// WinRT, permite emparejarlos/desemparejarlos y, si está activado, lanza el
/// emparejamiento automáticamente cuando aparece un mando.
/// </summary>
public sealed class BluetoothDeviceService : IDisposable
{
    // AEP Bluetooth clásico (BR/EDR) y BLE. DevObjectType 5 = AssociationEndpoint.
    private const string Selector =
        "System.Devices.DevObjectType:=5 AND " +
        "(System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\" OR " +
        "System.Devices.Aep.ProtocolId:=\"{bb7bb05e-5972-42b5-94fc-76eaa7084d49}\")";

    private static readonly TimeSpan RadioPollInterval = TimeSpan.FromSeconds(15);
    // Ventana de supresión tras un desemparejamiento manual: durante 20 s
    // desde el unpair el dispositivo no puede auto-emparejarse, aunque
    // desaparezca y reaparezca en la lista (el propio desemparejado puede
    // hacerlo). El contador empieza al desemparejar, no al desaparecer.
    private static readonly TimeSpan SuppressionDuration = TimeSpan.FromSeconds(20);

    private static readonly string[] AdditionalProperties =
    [
        "System.Devices.Aep.DeviceAddress",
        "System.Devices.Aep.IsConnected",
        "System.Devices.Aep.IsPresent",
        "System.Devices.Aep.ProtocolId",
        "System.Devices.Aep.Bluetooth.Le.AddressType",
        "System.Devices.Aep.Bluetooth.Le.Appearance",
        "System.Devices.Aep.Bluetooth.Cod.Major",
        "System.Devices.Aep.Bluetooth.Cod.Minor",
        "System.ItemNameDisplay"
    ];

    // Servicio GATT HID-over-GATT: todo mando BLE debe exponerlo; es la
    // señal inequívoca que usa el sondeo de servicios como último recurso.
    private static readonly Guid HidOverGattServiceUuid =
        new("00001812-0000-1000-8000-00805f9b34fb");
    private static readonly TimeSpan ServiceProbeRetryDelay = TimeSpan.FromSeconds(90);

    private static readonly string[] GamepadNameHints =
    [
        "xbox", "xinput", "wireless controller", "dualsense", "dualshock",
        "dual shock", "sony", "playstation", "nintendo", "switch pro",
        "pro controller", "joy-con", "joycon", "8bitdo", "gamesir",
        "ipega", "flydigi", "gamepad", "game controller", "controller",
        "mocute", "redthunder", "gamevice", "backbone"
    ];

    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceInformation> _devices = new(StringComparer.OrdinalIgnoreCase);
    // Dispositivos que ya dispararon su auto-emparejamiento en esta aparición:
    // se limpia cuando el dispositivo sale de la lista para que una nueva
    // aparición (modo emparejamiento) vuelva a intentarlo.
    private readonly HashSet<string> _autoPairTriggered = new(StringComparer.OrdinalIgnoreCase);
    // Supresión por desemparejamiento manual: id → tick del unpair. Expira
    // a los SuppressionDuration milisegundos.
    private readonly Dictionary<string, long> _autoPairSuppressed = new(StringComparer.OrdinalIgnoreCase);
    // Dispositivos marcados por el usuario como «no conectar
    // automáticamente»: se excluyen del auto-emparejamiento de forma
    // permanente (a diferencia de la supresión temporal por unpair).
    private readonly HashSet<string> _autoPairExcluded = new(StringComparer.OrdinalIgnoreCase);
    // Mandos confirmados por señales de protocolo (CoD/Appearance/GATT):
    // su nombre no coincide con las pistas pero el dispositivo sí lo es.
    private readonly HashSet<string> _serviceConfirmedGamepads = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _serviceProbeRunning = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _serviceProbeFailedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.Timer _radioTimer;
    private readonly SemaphoreSlim _pairGate = new(1, 1);
    private DeviceWatcher? _watcher;
    private BluetoothRadioState _radioState = BluetoothRadioState.Unknown;
    private int _radioDownStreak;
    private bool _disposed;

    public BluetoothDeviceService(Action<string>? diagnosticLog = null)
    {
        Log = diagnosticLog;
        BluetoothPairHelper.CleanupStaleFiles();
        _radioTimer = new System.Threading.Timer(
            _ => RefreshRadioState(),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    private Action<string>? Log { get; }

    public BluetoothRadioState RadioState
    {
        get { lock (_gate) { return _radioState; } }
    }

    public bool IsAutoConnectEnabled { get; set; }

    /// <summary>
    /// Clave estable de un dispositivo para la lista de excluidos del
    /// auto-connect: la dirección normalizada si existe, si no el Id.
    /// </summary>
    public static string AutoPairExcludeKey(string? address, string id) =>
        NormalizeKey(address) ?? id;

    private static string? NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new System.Text.StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToUpperInvariant(c));
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    /// <summary>Reemplaza la lista de dispositivos excluidos del auto-connect.</summary>
    public void SetAutoPairExcludedDevices(IEnumerable<string> keys)
    {
        lock (_gate)
        {
            _autoPairExcluded.Clear();
            foreach (string key in keys)
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    _autoPairExcluded.Add(key.Trim());
                }
            }
        }
    }

    public bool IsAutoPairExcluded(string? address, string id)
    {
        lock (_gate)
        {
            return _autoPairExcluded.Contains(AutoPairExcludeKey(address, id));
        }
    }

    /// <summary>
    /// Handler que ejecuta el emparejamiento automático. Lo asigna el
    /// ViewModel para que el auto-emparejamiento use exactamente el mismo
    /// flujo que el botón manual (estado, mensajes y notificaciones).
    /// </summary>
    public Func<BluetoothDeviceInfo, Task>? AutoPairHandler { get; set; }

    public event Action<BluetoothDeviceInfo>? DeviceAdded;
    public event Action<BluetoothDeviceInfo>? DeviceUpdated;
    public event Action<string>? DeviceRemoved;
    public event Action<BluetoothRadioState>? RadioStateChanged;

    public void Start()
    {
        if (_disposed)
        {
            return;
        }

        _radioTimer.Change(TimeSpan.Zero, RadioPollInterval);
    }

    public void Stop()
    {
        _radioTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        StopWatcher();
    }

    /// <summary>Reinicia el DeviceWatcher para forzar una nueva enumeración.</summary>
    public void Rescan()
    {
        StopWatcher();
        if (RadioState == BluetoothRadioState.On)
        {
            EnsureWatcher();
            _ = EnumerateOnceAsync();
        }
    }

    /// <summary>
    /// Enumeración única de todos los AssociationEndpoint Bluetooth
    /// (emparejados o no). Complementa al DeviceWatcher, que puede omitir
    /// dispositivos emparejados que no estén anunciando en ese momento.
    /// </summary>
    private async Task EnumerateOnceAsync()
    {
        IReadOnlyList<DeviceInformation> found;
        try
        {
            found = await DeviceInformation.FindAllAsync(
                Selector,
                AdditionalProperties,
                DeviceInformationKind.AssociationEndpoint).AsTask().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log?.Invoke($"No se pudo enumerar dispositivos Bluetooth: {exception.Message}");
            return;
        }

        foreach (DeviceInformation device in found)
        {
            BluetoothDeviceInfo? info = BuildInfo(device);
            if (info is null)
            {
                continue;
            }

            bool isNew;
            lock (_gate)
            {
                isNew = !_devices.ContainsKey(info.Id);
                _devices[info.Id] = device;
            }

            if (isNew)
            {
                // La enumeración solo alimenta la lista: el auto-emparejamiento
                // lo dispara exclusivamente el DeviceWatcher, que reporta el
                // dispositivo cuando realmente se anuncia (modo emparejamiento).
                // Así un AEP conocido pero inactivo no puede lanzar PairAsync.
                DeviceAdded?.Invoke(info);
            }
            else
            {
                DeviceUpdated?.Invoke(info);
            }

            MaybeProbeServices(info);
        }
    }

    private int _radioPollRunning;

    private void RefreshRadioState()
    {
        // La enumeración PnP puede tardar; se evita que los ticks del timer
        // se solapen y produzcan lecturas/estados inconsistentes.
        if (Interlocked.Exchange(ref _radioPollRunning, 1) == 1)
        {
            return;
        }

        try
        {
            RefreshRadioStateCore();
        }
        finally
        {
            Interlocked.Exchange(ref _radioPollRunning, 0);
        }
    }

    private void RefreshRadioStateCore()
    {
        BluetoothRadioState state;
        try
        {
            state = BluetoothRadioService.GetState();
        }
        catch (Exception exception)
        {
            Log?.Invoke($"No se pudo consultar el estado del radio Bluetooth: {exception.Message}");
            return;
        }

        bool changed;
        lock (_gate)
        {
            changed = _radioState != state;
            _radioState = state;
        }

        if (changed)
        {
            RadioStateChanged?.Invoke(state);
        }

        if (state == BluetoothRadioState.On)
        {
            _radioDownStreak = 0;
            EnsureWatcher();
        }
        else
        {
            // El estado PnP puede dar lecturas transitorias; solo se detiene
            // el vigilante tras tres consultas consecutivas sin radio activo
            // para no vaciar la lista de dispositivos por un parpadeo.
            _radioDownStreak++;
            if (_radioDownStreak >= 3)
            {
                StopWatcher();
            }
        }
    }

    private void EnsureWatcher()
    {
        lock (_gate)
        {
            if (_watcher is not null || _disposed)
            {
                return;
            }

            try
            {
                _watcher = DeviceInformation.CreateWatcher(
                    Selector,
                    AdditionalProperties,
                    DeviceInformationKind.AssociationEndpoint);
                _watcher.Added += OnWatcherAdded;
                _watcher.Updated += OnWatcherUpdated;
                _watcher.Removed += OnWatcherRemoved;
                _watcher.Start();
                Log?.Invoke("Vigilante Bluetooth iniciado.");
                // El watcher solo reporta dispositivos presentes/anunciando;
                // la enumeración completa añade los ya emparejados aunque
                // estén apagados (p. ej. un segundo mando idéntico).
                _ = EnumerateOnceAsync();
            }
            catch (Exception exception)
            {
                _watcher = null;
                Log?.Invoke($"No se pudo iniciar la búsqueda Bluetooth: {exception.Message}");
            }
        }
    }

    private void StopWatcher()
    {
        DeviceWatcher? watcher;
        lock (_gate)
        {
            watcher = _watcher;
            _watcher = null;
            _devices.Clear();
            // Al reiniciar el vigilante todos los dispositivos se reportan
            // como nuevos; se rearma el auto-emparejamiento para que una
            // reaparición pueda volver a intentarlo.
            _autoPairTriggered.Clear();
        }

        if (watcher is null)
        {
            return;
        }

        try
        {
            watcher.Added -= OnWatcherAdded;
            watcher.Updated -= OnWatcherUpdated;
            watcher.Removed -= OnWatcherRemoved;
            watcher.Stop();
        }
        catch (Exception exception)
        {
            Log?.Invoke($"No se pudo detener el vigilante Bluetooth: {exception.Message}");
        }
    }

    private void OnWatcherAdded(DeviceWatcher sender, DeviceInformation device)
    {
        BluetoothDeviceInfo? info = BuildInfo(device);
        if (info is null)
        {
            return;
        }

        lock (_gate)
        {
            _devices[info.Id] = device;
        }

        DeviceAdded?.Invoke(info);
        MaybeAutoPair(info);
        MaybeProbeServices(info);
    }

    private void OnWatcherUpdated(DeviceWatcher sender, DeviceInformationUpdate update) =>
        _ = HandleUpdateAsync(update);

    private async Task HandleUpdateAsync(DeviceInformationUpdate update)
    {
        DeviceInformation? device;
        try
        {
            device = await DeviceInformation.CreateFromIdAsync(
                update.Id,
                AdditionalProperties,
                DeviceInformationKind.AssociationEndpoint).AsTask().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log?.Invoke($"No se pudo resolver el dispositivo Bluetooth {update.Id}: {exception.Message}");
            return;
        }

        if (device is null)
        {
            return;
        }

        BluetoothDeviceInfo? info = BuildInfo(device);
        if (info is null)
        {
            return;
        }

        lock (_gate)
        {
            _devices[info.Id] = device;
        }

        DeviceUpdated?.Invoke(info);
        MaybeAutoPair(info);
        MaybeProbeServices(info);
    }

    private void OnWatcherRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        lock (_gate)
        {
            _devices.Remove(update.Id);
            // Nueva aparición = nuevo intento de auto-emparejamiento.
            _autoPairTriggered.Remove(update.Id);
        }

        DeviceRemoved?.Invoke(update.Id);
    }

    private static string DescribeDeviceForLog(DeviceInformation device)
    {
        string? address = device.Properties.TryGetValue(
            "System.Devices.Aep.DeviceAddress", out object? addr)
            ? addr?.ToString()
            : null;
        string transport = device.Id.Contains("BluetoothLE", StringComparison.OrdinalIgnoreCase)
            ? "BLE"
            : device.Id.Contains("BTH", StringComparison.OrdinalIgnoreCase)
                ? "classic"
                : "?";
        return $"{transport}; addr={address ?? "?"}; id={device.Id}";
    }

    private BluetoothDeviceInfo? BuildInfo(DeviceInformation device)
    {
        if (string.IsNullOrWhiteSpace(device.Id))
        {
            return null;
        }

        string displayName = string.IsNullOrWhiteSpace(device.Name)
            ? "Dispositivo Bluetooth"
            : device.Name.Trim();
        string? address = GetDeviceProperty(device, "System.Devices.Aep.DeviceAddress") as string;
        bool isConnected = GetDeviceProperty(device, "System.Devices.Aep.IsConnected") is true;
        // AEP «recordado» pero ausente: sigue enumerado aunque el aparato
        // ya no esté al alcance. Los no emparejados se ocultan en ese caso.
        bool isPresent = GetDeviceProperty(device, "System.Devices.Aep.IsPresent") is not false;

        BluetoothTransport transport = device.Id.Contains("BluetoothLE", StringComparison.OrdinalIgnoreCase)
            ? BluetoothTransport.LowEnergy
            : device.Id.Contains("BTH", StringComparison.OrdinalIgnoreCase)
                ? BluetoothTransport.Classic
                : BluetoothTransport.Unknown;

        // Un mando es tal si el nombre lo sugiere, si las propiedades de
        // protocolo lo indican (CoD/Appearance) o si un sondeo GATT previo
        // confirmó el servicio HID. Así mandos con nombre genérico (p. ej.
        // «T3») no dependen del texto visible.
        bool looksLikeGamepad = LooksLikeGamepad(displayName) ||
            HasGamepadProtocolSignals(device) ||
            _serviceConfirmedGamepads.Contains(device.Id);

        return new BluetoothDeviceInfo(
            device.Id,
            displayName,
            address,
            transport,
            device.Pairing.IsPaired,
            isConnected,
            device.Pairing.CanPair,
            looksLikeGamepad,
            isPresent);
    }

    private static object? GetDeviceProperty(DeviceInformation device, string key) =>
        device.Properties.TryGetValue(key, out object? value) ? value : null;

    /// <summary>
    /// Señales de protocolo que no requieren conexión: la clase de
    /// dispositivo (CoD) de Bluetooth clásico y la «appearance» BLE.
    /// </summary>
    private static bool HasGamepadProtocolSignals(DeviceInformation device)
    {
        // CoD clásico: major 5 = Peripheral, minor 1 = Joystick, 2 = Gamepad.
        object? major = GetDeviceProperty(device, "System.Devices.Aep.Bluetooth.Cod.Major");
        object? minor = GetDeviceProperty(device, "System.Devices.Aep.Bluetooth.Cod.Minor");
        if (major is not null && minor is not null &&
            ToInt32(major) == 5 && ToInt32(minor) is >= 1 and <= 2)
        {
            return true;
        }

        // BLE appearance: 0x03C3 Joystick, 0x03C4 Gamepad.
        object? appearance = GetDeviceProperty(device, "System.Devices.Aep.Bluetooth.Le.Appearance");
        return appearance is not null && ToInt32(appearance) is 0x03C3 or 0x03C4;
    }

    private static int ToInt32(object value)
    {
        try
        {
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (
            exception is InvalidCastException or FormatException or OverflowException)
        {
            return -1;
        }
    }

    public static bool LooksLikeGamepad(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        string lower = name.ToLowerInvariant();
        return GamepadNameHints.Any(hint => lower.Contains(hint, StringComparison.Ordinal));
    }

    private static void OnCustomPairingRequested(
        DeviceInformationCustomPairing sender,
        DevicePairingRequestedEventArgs args)
    {
        try
        {
            // ProvidePin requiere entregar el PIN en Accept; el resto de
            // ceremonias se acepta directamente.
            if (args.PairingKind == DevicePairingKinds.ProvidePin)
            {
                args.Accept("0000");
            }
            else
            {
                args.Accept();
            }
        }
        catch
        {
            // La petición de emparejamiento expiró o fue rechazada.
        }
    }

    /// <summary>
    /// Si un AEP presente no parece mando ni por nombre ni por CoD/
    /// Appearance, se sondean sus servicios GATT: todo mando BLE expone el
    /// servicio HID (0x1812). Solo se sondean dispositivos no emparejados
    /// y no conectados para no interferir con una sesión HID activa.
    /// </summary>
    private void MaybeProbeServices(BluetoothDeviceInfo info)
    {
        if (info.LooksLikeGamepad ||
            info.IsPaired ||
            info.IsConnected ||
            !info.IsPresent ||
            info.Transport != BluetoothTransport.LowEnergy)
        {
            return;
        }

        lock (_gate)
        {
            if (_serviceProbeRunning.Contains(info.Id) ||
                _serviceConfirmedGamepads.Contains(info.Id) ||
                (_serviceProbeFailedAt.TryGetValue(info.Id, out long failedAt) &&
                 Environment.TickCount64 - failedAt < ServiceProbeRetryDelay.TotalMilliseconds))
            {
                return;
            }

            _serviceProbeRunning.Add(info.Id);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await ProbeBleServicesAsync(info.Id).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Log?.Invoke($"Sondeo de servicios Bluetooth falló para {info.Name}: {exception.Message}");
            }
            finally
            {
                lock (_gate)
                {
                    _serviceProbeRunning.Remove(info.Id);
                }
            }
        });
    }

    private async Task ProbeBleServicesAsync(string deviceId)
    {
        DeviceInformation? device;
        lock (_gate)
        {
            _devices.TryGetValue(deviceId, out device);
        }

        if (device is null)
        {
            return;
        }

        string? address = GetDeviceProperty(device, "System.Devices.Aep.DeviceAddress") as string;
        BluetoothAddressType addressType =
            GetDeviceProperty(device, "System.Devices.Aep.Bluetooth.Le.AddressType") is byte typeByte
                ? (BluetoothAddressType)typeByte
                : BluetoothAddressType.Unspecified;
        if (!TryParseBluetoothAddress(address, out ulong addressValue))
        {
            MarkServiceProbeFailed(deviceId);
            return;
        }

        // BluetoothAddress puede interpretar el ulong en orden de bytes
        // distinto según la fuente; se prueban ambas variantes.
        BluetoothLEDevice? bleDevice = null;
        foreach (ulong candidate in CandidateAddresses(addressValue))
        {
            try
            {
                bleDevice = await BluetoothLEDevice
                    .FromBluetoothAddressAsync(candidate, addressType)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(10))
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException)
            {
                // Se intenta la siguiente variante de orden de bytes.
            }

            if (bleDevice is not null)
            {
                break;
            }
        }

        if (bleDevice is null)
        {
            MarkServiceProbeFailed(deviceId);
            return;
        }

        bool isGamepad;
        using (bleDevice)
        {
            GattDeviceServicesResult result = await bleDevice
                .GetGattServicesAsync(BluetoothCacheMode.Uncached)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(15))
                .ConfigureAwait(false);
            isGamepad = result.Status == GattCommunicationStatus.Success &&
                result.Services.Any(service => service.Uuid == HidOverGattServiceUuid);
        }

        lock (_gate)
        {
            if (!_devices.ContainsKey(deviceId))
            {
                return;
            }

            if (isGamepad)
            {
                _serviceConfirmedGamepads.Add(deviceId);
                _serviceProbeFailedAt.Remove(deviceId);
            }
            else
            {
                _serviceProbeFailedAt[deviceId] = Environment.TickCount64;
            }
        }

        if (isGamepad)
        {
            Log?.Invoke($"Sondeo GATT: {device.Name} expone el servicio HID; se tratará como mando.");
            BluetoothDeviceInfo? updated = BuildInfo(device);
            if (updated is not null)
            {
                DeviceUpdated?.Invoke(updated);
                MaybeAutoPair(updated);
            }
        }
    }

    private void MarkServiceProbeFailed(string deviceId)
    {
        lock (_gate)
        {
            _serviceProbeFailedAt[deviceId] = Environment.TickCount64;
        }
    }

    private static IEnumerable<ulong> CandidateAddresses(ulong address)
    {
        yield return address;
        // Variante con los 6 octetos en orden inverso.
        ulong swapped = 0;
        for (int i = 0; i < 6; i++)
        {
            swapped = (swapped << 8) | ((address >> (8 * i)) & 0xFF);
        }

        yield return swapped;
    }

    private static bool TryParseBluetoothAddress(string? text, out ulong value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var builder = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (Uri.IsHexDigit(c))
            {
                builder.Append(char.ToUpperInvariant(c));
            }
        }

        return builder.Length == 12 &&
            ulong.TryParse(builder.ToString(), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out value);
    }

    private void MaybeAutoPair(BluetoothDeviceInfo info)
    {
        if (!info.LooksLikeGamepad)
        {
            return;
        }

        if (!IsAutoConnectEnabled)
        {
            return;
        }

        if (info.IsPaired)
        {
            Log?.Invoke(
                $"Autoemparejamiento omitido para {info.Name}: ya está emparejado " +
                $"(conectado={info.IsConnected}).");
            return;
        }

        if (!info.CanPair)
        {
            Log?.Invoke($"Autoemparejamiento omitido para {info.Name}: Windows indica CanPair=false.");
            return;
        }

        if (IsAutoPairExcluded(info.Address, info.Id))
        {
            return;
        }

        // Un solo auto-emparejamiento por aparición: el reintento continuo
        // (si el usuario lo activa) lo gestiona el ViewModel; aquí solo se
        // dispara una vez hasta que el dispositivo salga de la lista.
        lock (_gate)
        {
            if (_autoPairTriggered.Contains(info.Id) ||
                IsSuppressedLocked(info.Id))
            {
                return;
            }

            _autoPairTriggered.Add(info.Id);
        }

        if (AutoPairHandler is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await AutoPairHandler(info).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Log?.Invoke($"Error en autoemparejamiento de {info.Name}: {exception.Message}");
            }
        });
    }

    // Debe llamarse dentro de lock (_gate).
    private bool IsSuppressedLocked(string deviceId)
    {
        if (!_autoPairSuppressed.TryGetValue(deviceId, out long unpairedAt))
        {
            return false;
        }

        if (Environment.TickCount64 - unpairedAt >= SuppressionDuration.TotalMilliseconds)
        {
            _autoPairSuppressed.Remove(deviceId);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Indica si el dispositivo está suprimido del auto-emparejamiento.
    /// La supresión dura SuppressionDuration desde el desemparejamiento
    /// manual y luego expira sola.
    /// </summary>
    public bool IsAutoPairSuppressed(string deviceId)
    {
        lock (_gate)
        {
            return IsSuppressedLocked(deviceId);
        }
    }

    /// <summary>
    /// Vuelve a resolver el DeviceInformation descartando la caché y
    /// devuelve si el dispositivo está emparejado ahora mismo. Sirve para
    /// detectar emparejamientos completados por el flujo propio de Windows.
    /// </summary>
    public async Task<bool> IsDevicePairedAsync(string deviceId)
    {
        lock (_gate)
        {
            _devices.Remove(deviceId);
        }

        DeviceInformation? device = await GetDeviceAsync(deviceId).ConfigureAwait(false);
        return device?.Pairing.IsPaired == true;
    }

    public async Task<DeviceInformation?> GetDeviceAsync(string deviceId)
    {
        lock (_gate)
        {
            if (_devices.TryGetValue(deviceId, out DeviceInformation? cached))
            {
                return cached;
            }
        }

        try
        {
            DeviceInformation? info = await DeviceInformation.CreateFromIdAsync(
                deviceId,
                AdditionalProperties,
                DeviceInformationKind.AssociationEndpoint).AsTask().ConfigureAwait(false);
            if (info is not null)
            {
                lock (_gate)
                {
                    _devices[deviceId] = info;
                }
            }

            return info;
        }
        catch (Exception exception)
        {
            Log?.Invoke($"No se pudo resolver el dispositivo Bluetooth {deviceId}: {exception.Message}");
            return null;
        }
    }

    public async Task<BluetoothPairResult> PairAsync(string deviceId)
    {
        // Un solo emparejamiento a la vez: el helper y la vía directa no
        // deben competir por el stack Bluetooth.
        await _pairGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Primero se intenta desde un proceso no elevado (GDM.Pair.exe):
            // en Windows 10 el PairAsync de un proceso elevado falla sin
            // llegar a mostrar el diálogo del sistema.
            BluetoothPairResult? helperResult = await TryPairViaHelperAsync(deviceId)
                .ConfigureAwait(false);
            if (helperResult is not null &&
                helperResult.Value is not BluetoothPairResult.Failed)
            {
                return helperResult.Value;
            }

            // Si el helper falló (o no existe) se intenta la vía directa:
            // en equipos donde el proceso elevado sí puede emparejar esto
            // conserva el comportamiento anterior como respaldo.
            if (helperResult == BluetoothPairResult.Failed)
            {
                Log?.Invoke("El helper no pudo emparejar; se intenta la vía directa como respaldo.");
            }

            return await PairAsyncInProcessAsync(deviceId).ConfigureAwait(false);
        }
        finally
        {
            _pairGate.Release();
        }
    }

    /// <summary>
    /// Lanza GDM.Pair.exe sin privilegios a través de explorer.exe y espera
    /// el estado que escribe en disco. Devuelve null si la infraestructura
    /// del helper no está disponible (entonces se usa la vía directa).
    /// </summary>
    private async Task<BluetoothPairResult?> TryPairViaHelperAsync(string deviceId)
    {
        try
        {
            string? executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                return null;
            }

            string? directory = Path.GetDirectoryName(executablePath);
            if (string.IsNullOrEmpty(directory))
            {
                return null;
            }

            string helperPath = Path.Combine(directory, BluetoothPairHelper.HelperFileName);
            if (!ToastNotificationService.EnsureHelperLink(helperPath, executablePath))
            {
                return null;
            }

            Directory.CreateDirectory(BluetoothPairHelper.WorkDirectory);
            string requestId = Guid.NewGuid().ToString("N");
            string resultPath = BluetoothPairHelper.ResultPath(requestId);
            try { File.Delete(resultPath); } catch { }

            await File.WriteAllTextAsync(
                BluetoothPairHelper.RequestPath,
                JsonSerializer.Serialize(new BluetoothPairHelper.PairRequest(deviceId, requestId)))
                .ConfigureAwait(false);

            DateTime helperLaunchedUtc = DateTime.UtcNow;
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{helperPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            Log?.Invoke($"Emparejamiento de {deviceId} delegado a proceso no elevado.");

            // El usuario puede tardar en confirmar el diálogo de Windows;
            // se espera hasta 2 minutos el archivo de resultado.
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(resultPath))
                {
                    string statusText = (await File.ReadAllTextAsync(resultPath)
                        .ConfigureAwait(false)).Trim();
                    try { File.Delete(resultPath); } catch { }

                    if (Enum.TryParse<DevicePairingResultStatus>(statusText, out DevicePairingResultStatus parsed))
                    {
                        Log?.Invoke($"Emparejamiento delegado (helper no elevado) → {parsed}");
                        return MapPairingStatus(parsed);
                    }

                    Log?.Invoke($"Emparejamiento delegado devolvió estado ilegible: «{statusText}»");
                    return BluetoothPairResult.Failed;
                }

                await Task.Delay(500).ConfigureAwait(false);
            }

            // Timeout: el helper puede seguir colgado dentro de PairAsync.
            // Process.Start devuelve explorer.exe (que ya salió), así que el
            // GDM.Pair.exe real se localiza por nombre y fecha de arranque;
            // se termina para que no complete un emparejamiento duplicado
            // mientras la vía directa ya está en marcha.
            try
            {
                foreach (Process orphan in Process.GetProcessesByName("GDM.Pair"))
                {
                    try
                    {
                        if (orphan.StartTime.ToUniversalTime() >= helperLaunchedUtc.AddSeconds(-2))
                        {
                            orphan.Kill();
                            Log?.Invoke("Helper de emparejamiento colgado: terminado tras el timeout.");
                        }
                    }
                    catch
                    {
                        // El proceso puede haber salido entre la enumeración y el kill.
                    }
                    finally
                    {
                        orphan.Dispose();
                    }
                }
            }
            catch (Exception exception)
            {
                Log?.Invoke($"No se pudo terminar el helper de emparejamiento: {exception.Message}");
            }

            Log?.Invoke("El helper de emparejamiento no respondió a tiempo.");
            return BluetoothPairResult.Failed;
        }
        catch (Exception exception)
        {
            Log?.Invoke($"No se pudo usar el helper de emparejamiento: {exception.Message}");
            return null;
        }
    }

    private static BluetoothPairResult MapPairingStatus(DevicePairingResultStatus status) =>
        status switch
        {
            DevicePairingResultStatus.Paired => BluetoothPairResult.Paired,
            DevicePairingResultStatus.AlreadyPaired => BluetoothPairResult.AlreadyPaired,
            DevicePairingResultStatus.NotReadyToPair => BluetoothPairResult.NotReady,
            DevicePairingResultStatus.NotPaired or
            DevicePairingResultStatus.ConnectionRejected or
            DevicePairingResultStatus.AuthenticationNotAllowed => BluetoothPairResult.NotAllowed,
            DevicePairingResultStatus.AccessDenied => BluetoothPairResult.NotAllowed,
            _ => BluetoothPairResult.Failed
        };

    private async Task<BluetoothPairResult> PairAsyncInProcessAsync(string deviceId)
    {
        DeviceInformation? device = await GetDeviceAsync(deviceId).ConfigureAwait(false);
        if (device is null)
        {
            return BluetoothPairResult.NotFound;
        }

        // Si Windows ya lo considera emparejado no tiene sentido insistir:
        // los intentos extra fallan y pueden dejar el stack Bluetooth
        // ocupado (p. ej. con entradas AEP en estado inconsistente).
        if (device.Pairing.IsPaired)
        {
            Log?.Invoke($"Emparejamiento de {device.Name} omitido: Windows ya lo reporta emparejado.");
            return BluetoothPairResult.AlreadyPaired;
        }

        try
        {
            DevicePairingResultStatus status;
            try
            {
                status = (await device.Pairing.PairAsync(
                    DevicePairingProtectionLevel.EncryptionAndAuthentication)
                    .AsTask().ConfigureAwait(false)).Status;
            }
            catch
            {
                status = DevicePairingResultStatus.HardwareFailure;
            }

            // Algunos dispositivos (DS4, Xbox) rechazan el nivel más estricto;
            // si no quedó emparejado se reintenta con el nivel básico.
            if (status is not (DevicePairingResultStatus.Paired or
                DevicePairingResultStatus.AlreadyPaired))
            {
                Log?.Invoke($"Emparejamiento de {device.Name} con EncryptionAndAuthentication: {status}; reintentando con Encryption.");
                await Task.Delay(800).ConfigureAwait(false);
                try
                {
                    status = (await device.Pairing.PairAsync(
                        DevicePairingProtectionLevel.Encryption)
                        .AsTask().ConfigureAwait(false)).Status;
                }
                catch
                {
                    // Se conserva el resultado del primer intento.
                }
            }

            // Tercer intento con la API estándar sin cifrado: en algunos
            // equipos el nivel Encryption también es rechazado pero None
            // completa (Windows muestra su diálogo de confirmación igual).
            if (status is not (DevicePairingResultStatus.Paired or
                DevicePairingResultStatus.AlreadyPaired))
            {
                Log?.Invoke($"Emparejamiento de {device.Name} sigue fallando ({status}); reintentando sin cifrado.");
                await Task.Delay(800).ConfigureAwait(false);
                try
                {
                    status = (await device.Pairing.PairAsync(
                        DevicePairingProtectionLevel.None)
                        .AsTask().ConfigureAwait(false)).Status;
                }
                catch
                {
                    // Se conserva el resultado del intento anterior.
                }
            }

            // Último recurso: emparejamiento personalizado con ceremonia
            // ConfirmOnly y sin cifrado. Los periféricos que inician el SSP
            // desde su lado (DS4, mandos Xbox) suelen requerir este flujo.
            // Ojo: acceder a Pairing.Custom lanza si el dispositivo no
            // admite emparejamiento personalizado, por eso va dentro del try.
            if (status is not (DevicePairingResultStatus.Paired or
                DevicePairingResultStatus.AlreadyPaired))
            {
                Log?.Invoke($"Emparejamiento de {device.Name} sigue fallando ({status}); probando emparejamiento personalizado.");
                try
                {
                    DeviceInformationCustomPairing customPairing = device.Pairing.Custom;
                    customPairing.PairingRequested += OnCustomPairingRequested;
                    try
                    {
                        DevicePairingResult customResult = await customPairing.PairAsync(
                            DevicePairingKinds.ConfirmOnly |
                            DevicePairingKinds.DisplayPin |
                            DevicePairingKinds.ProvidePin |
                            DevicePairingKinds.ConfirmPinMatch,
                            DevicePairingProtectionLevel.None).AsTask().ConfigureAwait(false);
                        Log?.Invoke($"Emparejamiento personalizado de {device.Name}: {customResult.Status}");
                        status = customResult.Status;
                    }
                    finally
                    {
                        customPairing.PairingRequested -= OnCustomPairingRequested;
                    }
                }
                catch (Exception exception)
                {
                    Log?.Invoke($"Emparejamiento personalizado de {device.Name} no disponible: {exception.Message}");
                }
            }

            Log?.Invoke(
                $"Emparejamiento de {device.Name} [{DescribeDeviceForLog(device)}]: {status}");
            return MapPairingStatus(status);
        }
        catch (Exception exception)
        {
            Log?.Invoke($"Error al emparejar {device.Name}: {exception.Message}");
            return BluetoothPairResult.Failed;
        }
    }

    public async Task<bool> UnpairAsync(string deviceId)
    {
        // Se descarta la caché antes de resolver: un DeviceInformation
        // obsoleto puede hacer que Windows rechace el desemparejado
        // (p. ej. entradas "zombi" que figuran emparejadas sin estarlo).
        lock (_gate)
        {
            _devices.Remove(deviceId);
        }

        DeviceInformation? device = await GetDeviceAsync(deviceId).ConfigureAwait(false);
        if (device is null)
        {
            return false;
        }

        try
        {
            DeviceUnpairingResult result = await device.Pairing.UnpairAsync().AsTask().ConfigureAwait(false);
            Log?.Invoke(
                $"Desemparejamiento de {device.Name} [{DescribeDeviceForLog(device)}]: {result.Status}");
            if (result.Status is DeviceUnpairingResultStatus.Unpaired or
                DeviceUnpairingResultStatus.AlreadyUnpaired)
            {
                // El DeviceInformation cacheado conserva el estado de pairing
                // antiguo; se descarta para que la siguiente operación lo
                // vuelva a resolver desde cero. Además se suprime el
                // auto-emparejamiento durante SuppressionDuration desde este
                // momento: si el usuario lo quitó a propósito no debe
                // re-emparejarse solo mientras siga en rango.
                lock (_gate)
                {
                    _devices.Remove(deviceId);
                    _autoPairSuppressed[deviceId] = Environment.TickCount64;
                }
            }

            return result.Status is DeviceUnpairingResultStatus.Unpaired or
                DeviceUnpairingResultStatus.AlreadyUnpaired;
        }
        catch (Exception exception)
        {
            Log?.Invoke($"Error al desemparejar {device.Name}: {exception.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _radioTimer.Dispose();
        StopWatcher();
    }
}

public enum BluetoothPairResult
{
    Paired,
    AlreadyPaired,
    NotReady,
    HardwareUnavailable,
    NotAllowed,
    Rejected,
    Canceled,
    NotFound,
    Failed
}
