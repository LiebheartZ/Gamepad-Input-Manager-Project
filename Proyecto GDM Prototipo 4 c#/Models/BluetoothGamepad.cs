using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GDM.Models;

public sealed class BluetoothGamepad : INotifyPropertyChanged
{
    private bool _isPaired;
    private bool _isConnected;
    private bool _isPairing;
    private bool _isUnpairing;

    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public ulong BluetoothAddress { get; init; }
    public bool IsRecognizedGamepad { get; init; }

    public bool IsPaired
    {
        get => _isPaired;
        private set
        {
            if (SetProperty(ref _isPaired, value))
            {
                OnPropertyChanged(nameof(StatusLabel));
            }
        }
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
            {
                OnPropertyChanged(nameof(StatusLabel));
            }
        }
    }

    public bool IsPairing
    {
        get => _isPairing;
        private set
        {
            if (SetProperty(ref _isPairing, value))
            {
                OnPropertyChanged(nameof(CanPair));
                OnPropertyChanged(nameof(CanUnpair));
                OnPropertyChanged(nameof(StatusLabel));
            }
        }
    }

    public bool IsUnpairing
    {
        get => _isUnpairing;
        private set
        {
            if (SetProperty(ref _isUnpairing, value))
            {
                OnPropertyChanged(nameof(CanPair));
                OnPropertyChanged(nameof(CanUnpair));
                OnPropertyChanged(nameof(StatusLabel));
            }
        }
    }

    public bool CanPair => !IsPaired && !IsPairing && !IsUnpairing;
    public bool CanUnpair => IsPaired && !IsPairing && !IsUnpairing;

    public string StatusLabel
    {
        get
        {
            if (IsPairing) return "Emparejando...";
            if (IsUnpairing) return "Desemparejando...";
            if (IsConnected) return "Conectado";
            if (IsPaired) return "Emparejado";
            return "Disponible";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateStatus(bool isPaired, bool isConnected)
    {
        IsPaired = isPaired;
        IsConnected = isConnected;
    }

    public void SetPairing(bool isPairing)
    {
        IsPairing = isPairing;
    }

    public void SetUnpairing(bool isUnpairing)
    {
        IsUnpairing = isUnpairing;
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
