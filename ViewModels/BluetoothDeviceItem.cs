using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using GDM.Services;

namespace GDM.ViewModels;

public sealed class BluetoothDeviceItem : INotifyPropertyChanged
{
    private bool _isPaired;
    private bool _isConnected;
    private bool _isBusy;
    private bool _autoConnectBlocked;
    private bool _isPresent;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }
    public string Name { get; }
    public string? Address { get; }
    public string TransportLabel { get; }
    public bool LooksLikeGamepad { get; }
    public ICommand? PairCommand { get; set; }
    public ICommand? UnpairCommand { get; set; }

    /// <summary>Se invoca cuando el usuario cambia la casilla de exclusión.</summary>
    public Action<BluetoothDeviceItem>? AutoConnectBlockedChanged { get; set; }

    public bool IsPaired
    {
        get => _isPaired;
        set
        {
            if (_isPaired == value)
            {
                return;
            }

            _isPaired = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(ActionLabel));
        }
    }

    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            if (_isConnected == value)
            {
                return;
            }

            _isConnected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public bool AutoConnectBlocked
    {
        get => _autoConnectBlocked;
        set
        {
            if (_autoConnectBlocked == value)
            {
                return;
            }

            _autoConnectBlocked = value;
            OnPropertyChanged();
            AutoConnectBlockedChanged?.Invoke(this);
        }
    }

    /// <summary>
    /// True si el AEP está presente ahora mismo (anunciando o conectable).
    /// Los dispositivos no emparejados que no están presentes se ocultan:
    /// son restos «recordados» que Windows sigue enumerando tras un
    /// desemparejado y harían que la tarjeta tarde en desaparecer.
    /// </summary>
    public bool IsPresent
    {
        get => _isPresent;
        private set
        {
            if (_isPresent == value)
            {
                return;
            }

            _isPresent = value;
            OnPropertyChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy == value)
            {
                return;
            }

            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActionLabel));
        }
    }

    public string StatusText =>
        IsPaired
            ? IsConnected ? "Emparejado · conectado" : "Emparejado"
            : "Detectado";

    public string ActionLabel =>
        IsBusy ? "Procesando…" : IsPaired ? "Desemparejar" : "Emparejar";

    public BluetoothDeviceItem(BluetoothDeviceInfo info, ICommand? pairCommand, ICommand? unpairCommand)
    {
        Id = info.Id;
        Name = info.Name;
        Address = info.Address;
        TransportLabel = info.Transport switch
        {
            BluetoothTransport.LowEnergy => "Bluetooth LE",
            BluetoothTransport.Classic => "Bluetooth",
            _ => "Bluetooth"
        };
        LooksLikeGamepad = info.LooksLikeGamepad;
        _isPaired = info.IsPaired;
        _isConnected = info.IsConnected;
        _isPresent = info.IsPresent;
        PairCommand = pairCommand;
        UnpairCommand = unpairCommand;
    }

    public void Update(BluetoothDeviceInfo info)
    {
        IsPaired = info.IsPaired;
        IsConnected = info.IsConnected;
        IsPresent = info.IsPresent;
    }

    public void MarkUnpaired()
    {
        IsPaired = false;
        IsConnected = false;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
