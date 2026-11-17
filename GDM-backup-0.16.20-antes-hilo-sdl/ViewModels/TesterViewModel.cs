using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using GDM.Models;
using GDM.Services.Abstractions;

namespace GDM.ViewModels;

public sealed class TesterViewModel : INotifyPropertyChanged
{
    private readonly IGamepadInputService? _inputService;
    private readonly IVirtualGamepadService? _virtualGamepadService;
    private readonly Func<ConnectedGamepad, bool>? _requestElevatedVirtualGamepad;
    private readonly Func<string>? _defaultVirtualGamepadType;
    private readonly Action<string>? _logInputError;
    private readonly AsyncCommand _selectDeviceCommand;
    private readonly AsyncCommand _startVirtualGamepadCommand;
    private readonly AsyncCommand _stopVirtualGamepadCommand;
    private ConnectedGamepad? _selectedDevice;
    private GamepadInputSnapshot? _inputSnapshot;
    private string _inputStatus = "Selecciona un mando conectado para leer sus entradas.";
    private string _virtualGamepadStatus = "La salida virtual está detenida.";
    private string? _lastErrorDeviceKey;
    private bool _isReading;
    private bool _isStartingVirtualGamepad;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ConnectedGamepad> Devices { get; } = [];
    public ICommand SelectDeviceCommand => _selectDeviceCommand;
    public ICommand StartVirtualGamepadCommand => _startVirtualGamepadCommand;
    public ICommand StopVirtualGamepadCommand => _stopVirtualGamepadCommand;
    public ConnectedGamepad? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                InputSnapshot = null;
                OnPropertyChanged(nameof(HasSelectedDevice));
                OnPropertyChanged(nameof(ShowNoSelectedDevice));
                OnPropertyChanged(nameof(IsSelectedDeviceVirtualGamepadRunning));
                OnPropertyChanged(nameof(CanStopVirtualGamepad));
                OnPropertyChanged(nameof(CanStartVirtualGamepad));
                _inputStatus = value is null
                    ? "Selecciona un mando conectado para leer sus entradas."
                    : _inputService is null
                        ? "La lectura de entradas SDL no está disponible."
                        : "Esperando datos de entrada...";
                OnPropertyChanged(nameof(InputStatus));
                _selectDeviceCommand.NotifyCanExecuteChanged();
                _startVirtualGamepadCommand.NotifyCanExecuteChanged();
                _stopVirtualGamepadCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public GamepadInputSnapshot? InputSnapshot
    {
        get => _inputSnapshot;
        private set
        {
            if (SetProperty(ref _inputSnapshot, value))
            {
                OnPropertyChanged(nameof(HasInputSnapshot));
                OnPropertyChanged(nameof(IsStandardGameController));
                OnPropertyChanged(nameof(CanStartVirtualGamepad));
                OnPropertyChanged(nameof(IsRawJoystick));
                OnPropertyChanged(nameof(ShowMappedDiagram));
                OnPropertyChanged(nameof(ControllerAnalysisText));
                OnPropertyChanged(nameof(PhysicalAxisCount));
                OnPropertyChanged(nameof(PhysicalButtonCount));
                OnPropertyChanged(nameof(MappedAxisCount));
                OnPropertyChanged(nameof(MappedButtonCount));
                OnPropertyChanged(nameof(HasTouchpadOrExtraButton));
                OnPropertyChanged(nameof(AxisListHeading));
                OnPropertyChanged(nameof(ButtonListHeading));
                OnPropertyChanged(nameof(LeftStickX));
                OnPropertyChanged(nameof(LeftStickY));
                OnPropertyChanged(nameof(RightStickX));
                OnPropertyChanged(nameof(RightStickY));
                OnPropertyChanged(nameof(LeftTriggerLevel));
                OnPropertyChanged(nameof(RightTriggerLevel));
                OnPropertyChanged(nameof(LeftTriggerVisualWidth));
                OnPropertyChanged(nameof(RightTriggerVisualWidth));
                OnPropertyChanged(nameof(LeftStickDotLeft));
                OnPropertyChanged(nameof(LeftStickDotTop));
                OnPropertyChanged(nameof(RightStickDotLeft));
                OnPropertyChanged(nameof(RightStickDotTop));
                OnPropertyChanged(nameof(IsFaceSouthPressed));
                OnPropertyChanged(nameof(IsFaceEastPressed));
                OnPropertyChanged(nameof(IsFaceWestPressed));
                OnPropertyChanged(nameof(IsFaceNorthPressed));
                OnPropertyChanged(nameof(IsLeftStickPressed));
                OnPropertyChanged(nameof(IsRightStickPressed));
                OnPropertyChanged(nameof(IsLeftTriggerPressed));
                OnPropertyChanged(nameof(IsRightTriggerPressed));
                OnPropertyChanged(nameof(IsTouchpadPressed));
                OnPropertyChanged(nameof(IsSelectPressed));
                OnPropertyChanged(nameof(IsStartPressed));
                OnPropertyChanged(nameof(IsHomePressed));
                OnPropertyChanged(nameof(IsDpadUpPressed));
                OnPropertyChanged(nameof(IsDpadDownPressed));
                OnPropertyChanged(nameof(IsDpadLeftPressed));
                OnPropertyChanged(nameof(IsDpadRightPressed));
                OnPropertyChanged(nameof(IsLeftShoulderPressed));
                OnPropertyChanged(nameof(IsRightShoulderPressed));
            }
        }
    }

    public string InputStatus
    {
        get => _inputStatus;
        private set => SetProperty(ref _inputStatus, value);
    }

    public string VirtualGamepadStatus
    {
        get => _virtualGamepadStatus;
        private set => SetProperty(ref _virtualGamepadStatus, value);
    }

    public bool IsVirtualGamepadRunning => _virtualGamepadService?.IsRunning == true;
    public bool IsSelectedDeviceVirtualGamepadRunning =>
        SelectedDevice is { } device && _virtualGamepadService?.IsRunningFor(device.DeviceKey) == true;
    public bool IsStartingVirtualGamepad => _isStartingVirtualGamepad;
    public bool CanSelectDevice => !IsStartingVirtualGamepad;
    public bool CanStartVirtualGamepad =>
        SelectedDevice is not null &&
        _inputService is not null &&
        _virtualGamepadService is not null &&
        !IsSelectedDeviceVirtualGamepadRunning &&
        !IsStartingVirtualGamepad;
    public bool CanStopVirtualGamepad => IsSelectedDeviceVirtualGamepadRunning;
    public bool HasSelectedDevice => SelectedDevice is not null;
    public bool ShowNoSelectedDevice => SelectedDevice is null;
    public bool HasInputSnapshot => InputSnapshot is not null;
    public bool IsStandardGameController => InputSnapshot?.IsStandardGameController == true;
    public bool IsRawJoystick => InputSnapshot is { IsStandardGameController: false };
    public bool ShowMappedDiagram => IsStandardGameController;
    public int PhysicalAxisCount => InputSnapshot?.PhysicalAxisCount ?? 0;
    public int PhysicalButtonCount => InputSnapshot?.PhysicalButtonCount ?? 0;
    public int MappedAxisCount => InputSnapshot?.Axes.Count ?? 0;
    public int MappedButtonCount => InputSnapshot?.Buttons.Count ?? 0;
    public string ControllerAnalysisText => InputSnapshot is not { } snapshot
        ? string.Empty
        : snapshot.IsStandardGameController
            ? $"Análisis SDL: {snapshot.PhysicalAxisCount} ejes y {snapshot.PhysicalButtonCount} botones físicos; {snapshot.Axes.Count} ejes y {snapshot.Buttons.Count} controles con mapeo."
            : $"Análisis SDL: {snapshot.PhysicalAxisCount} ejes y {snapshot.PhysicalButtonCount} botones físicos detectados.";
    public bool HasTouchpadOrExtraButton => InputSnapshot?.Buttons.Any(button =>
        button.Name is "Panel táctil (clic)" or "Botón extra (Misc 1)") == true;
    public string AxisListHeading => $"Ejes detectados ({MappedAxisCount})";
    public string ButtonListHeading => $"Botones detectados ({MappedButtonCount}; {PhysicalButtonCount} físicos)";
    public double LeftStickX => GetAxisValue("Stick izq. X");
    public double LeftStickY => GetAxisValue("Stick izq. Y");
    public double RightStickX => GetAxisValue("Stick der. X");
    public double RightStickY => GetAxisValue("Stick der. Y");
    public double LeftTriggerLevel => Math.Clamp(GetAxisValue("Gatillo izq."), 0d, 1d);
    public double RightTriggerLevel => Math.Clamp(GetAxisValue("Gatillo der."), 0d, 1d);
    public double LeftTriggerVisualWidth => LeftTriggerLevel * 90d;
    public double RightTriggerVisualWidth => RightTriggerLevel * 90d;
    public double LeftStickDotLeft => 166 + LeftStickX * 24;
    public double LeftStickDotTop => 180 + LeftStickY * 24;
    public double RightStickDotLeft => 282 + RightStickX * 24;
    public double RightStickDotTop => 180 + RightStickY * 24;
    public bool IsFaceSouthPressed => IsButtonPressed("A / Cruz");
    public bool IsFaceEastPressed => IsButtonPressed("B / Círculo");
    public bool IsFaceWestPressed => IsButtonPressed("X / Cuadrado");
    public bool IsFaceNorthPressed => IsButtonPressed("Y / Triángulo");
    public bool IsLeftStickPressed =>
        IsButtonPressed("Stick izq. (L3)") || IsButtonPressed("Stick izq.");
    public bool IsRightStickPressed =>
        IsButtonPressed("Stick der. (R3)") || IsButtonPressed("Stick der.");
    public bool IsLeftTriggerPressed => LeftTriggerLevel > 0.05d;
    public bool IsRightTriggerPressed => RightTriggerLevel > 0.05d;
    public bool IsTouchpadPressed =>
        IsButtonPressed("Panel táctil (clic)") || IsButtonPressed("Botón extra (Misc 1)");
    public bool IsSelectPressed => IsButtonPressed("Atrás / Share");
    public bool IsHomePressed => IsButtonPressed("Guía / PS") || IsButtonPressed("Guía");
    public bool IsStartPressed => IsButtonPressed("Inicio / Options");
    public bool IsDpadUpPressed => IsButtonPressed("Cruceta arriba");
    public bool IsDpadDownPressed => IsButtonPressed("Cruceta abajo");
    public bool IsDpadLeftPressed => IsButtonPressed("Cruceta izquierda");
    public bool IsDpadRightPressed => IsButtonPressed("Cruceta derecha");
    public bool IsLeftShoulderPressed =>
        IsButtonPressed("Hombro izq. (L1)") || IsButtonPressed("Hombro izq.");
    public bool IsRightShoulderPressed =>
        IsButtonPressed("Hombro der. (R1)") || IsButtonPressed("Hombro der.");

    public TesterViewModel(
        IGamepadInputService? inputService,
        Action<string>? logInputError = null,
        IVirtualGamepadService? virtualGamepadService = null,
        Func<ConnectedGamepad, bool>? requestElevatedVirtualGamepad = null,
        Func<string>? defaultVirtualGamepadType = null)
    {
        _inputService = inputService;
        _virtualGamepadService = virtualGamepadService;
        _requestElevatedVirtualGamepad = requestElevatedVirtualGamepad;
        _defaultVirtualGamepadType = defaultVirtualGamepadType;
        _logInputError = logInputError;
        if (_inputService is null)
        {
            _inputStatus = "La lectura de entradas SDL no está disponible.";
        }

        _selectDeviceCommand = new AsyncCommand(
            parameter =>
            {
                if (parameter is ConnectedGamepad device)
                {
                    SelectedDevice = device;
                }

                return Task.CompletedTask;
            },
            parameter => parameter is ConnectedGamepad device &&
                Devices.Any(candidate => candidate.DeviceKey == device.DeviceKey));
        _startVirtualGamepadCommand = new AsyncCommand(
            _ => StartVirtualGamepadAsync(),
            _ => CanStartVirtualGamepad);
        _stopVirtualGamepadCommand = new AsyncCommand(
            _ => StopVirtualGamepadAsync(),
            _ => CanStopVirtualGamepad);
    }

    public void UpdateDevices(IReadOnlyList<ConnectedGamepad> devices)
    {
        string? selectedKey = SelectedDevice?.DeviceKey;
        if (Devices.SequenceEqual(devices))
        {
            return;
        }

        Devices.Clear();
        foreach (ConnectedGamepad device in devices)
        {
            Devices.Add(device);
        }

        SelectedDevice = devices.FirstOrDefault(device => device.DeviceKey == selectedKey)
            ?? devices.FirstOrDefault();
        OnPropertyChanged(nameof(ShowNoDevices));
        _selectDeviceCommand.NotifyCanExecuteChanged();
    }

    public bool ShowNoDevices => Devices.Count == 0;

    public async Task ReadSelectedInputAsync(CancellationToken cancellationToken = default)
    {
        if (_isReading || SelectedDevice is not { } device || _inputService is null)
        {
            return;
        }

        _isReading = true;
        try
        {
            GamepadInputSnapshot? snapshot = await _inputService.ReadInputAsync(
                device.DeviceKey,
                cancellationToken);
            if (SelectedDevice?.DeviceKey != device.DeviceKey)
            {
                return;
            }

            if (snapshot is null)
            {
                InputSnapshot = null;
                InputStatus = device.IsHiddenByGdm
                    ? "El mando sigue conectado, pero SDL no expone sus entradas mientras está oculto por GDM."
                    : "El mando se desconectó. Actualizando la lista...";
                return;
            }

            InputSnapshot = snapshot;
            InputStatus = snapshot.IsStandardGameController
                ? "SDL reconoce este mando y normaliza sus controles."
                : "SDL no tiene un mapeo estándar para este mando. Se muestran ejes y botones físicos sin asignar nombres convencionales.";
            _lastErrorDeviceKey = null;
            if (_virtualGamepadService?.IsRunning == true)
            {
                try
                {
                    _virtualGamepadService.SubmitInputSnapshot(device, snapshot);
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or
                    IOException or
                    System.ComponentModel.Win32Exception)
                {
                    VirtualGamepadStatus = $"No se pudo actualizar la salida virtual: {exception.Message}";
                    _logInputError?.Invoke(VirtualGamepadStatus);
                    throw;
                }
            }
        }
        finally
        {
            _isReading = false;
        }
    }

    public async Task StartVirtualGamepadAsync()
    {
        if (_virtualGamepadService is null)
        {
            VirtualGamepadStatus = "El servicio HIDMaestro no está disponible.";
            return;
        }

        if (SelectedDevice is not { } device)
        {
            VirtualGamepadStatus = "Selecciona un mando conectado antes de iniciar la salida virtual.";
            return;
        }

        if (device.IsVirtualGamepad)
        {
            VirtualGamepadStatus = "No se puede usar un mando virtual como fuente de otra salida virtual.";
            return;
        }

        if (_inputService is null)
        {
            VirtualGamepadStatus = "No se puede iniciar: la lectura de entradas SDL no está disponible.";
            return;
        }

        if (IsStartingVirtualGamepad)
        {
            return;
        }

        _isStartingVirtualGamepad = true;
        OnPropertyChanged(nameof(IsStartingVirtualGamepad));
        NotifyVirtualGamepadCommands();
        try
        {
            GamepadInputSnapshot? snapshot = await _inputService.ReadInputAsync(device.DeviceKey);
            if (SelectedDevice?.DeviceKey != device.DeviceKey)
            {
                VirtualGamepadStatus = "La selección del mando cambió; vuelve a iniciar la emulación.";
                return;
            }

            if (snapshot is null)
            {
                InputSnapshot = null;
                VirtualGamepadStatus = "El mando seleccionado se desconectó. Actualiza la lista e inténtalo de nuevo.";
                return;
            }

            InputSnapshot = snapshot;
            if (!snapshot.IsStandardGameController)
            {
                VirtualGamepadStatus =
                    "No se puede iniciar: SDL no tiene un mapeo estándar para este mando.";
                return;
            }

            string virtualGamepadType = VirtualGamepadTypes.Normalize(
                _defaultVirtualGamepadType?.Invoke());
            await _virtualGamepadService.StartAsync(device, virtualGamepadType);
            UpdateVirtualGamepadState(
                $"Mando virtual {VirtualGamepadTypes.DisplayName(virtualGamepadType)} activo. El mando físico sigue conectado.");
        }
        catch (DllNotFoundException exception)
        {
            VirtualGamepadStatus = $"No se encontró SDL2 o una dependencia: {exception.Message}";
            _logInputError?.Invoke(VirtualGamepadStatus);
        }
        catch (BadImageFormatException exception)
        {
            VirtualGamepadStatus = $"La arquitectura de una dependencia no es compatible: {exception.Message}";
            _logInputError?.Invoke(VirtualGamepadStatus);
        }
        catch (EntryPointNotFoundException exception)
        {
            VirtualGamepadStatus = $"Falta una función requerida de SDL2: {exception.Message}";
            _logInputError?.Invoke(VirtualGamepadStatus);
        }
        catch (UnauthorizedAccessException exception)
        {
            if (!_virtualGamepadService.IsAdministrator)
            {
                bool relaunched = _requestElevatedVirtualGamepad?.Invoke(device) == true;
                VirtualGamepadStatus = relaunched
                    ? "GDM se volverá a abrir como administrador para iniciar el mando virtual."
                    : "No se concedieron permisos de administrador; la salida virtual no se inició.";
            }
            else
            {
                VirtualGamepadStatus =
                    $"Windows denegó una operación del controlador incluso con permisos de administrador: {exception.Message}";
                _logInputError?.Invoke(VirtualGamepadStatus);
            }
        }
        catch (InvalidOperationException exception)
        {
            VirtualGamepadStatus = exception.Message;
            _logInputError?.Invoke($"Salida virtual: {exception.Message}");
        }
        catch (IOException exception)
        {
            VirtualGamepadStatus = $"No se pudo iniciar HIDMaestro: {exception.Message}";
            _logInputError?.Invoke(VirtualGamepadStatus);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            VirtualGamepadStatus = $"Windows no pudo iniciar el controlador virtual: {exception.Message}";
            _logInputError?.Invoke(VirtualGamepadStatus);
        }
        finally
        {
            _isStartingVirtualGamepad = false;
            OnPropertyChanged(nameof(IsStartingVirtualGamepad));
            NotifyVirtualGamepadCommands();
        }
    }

    public async Task StopVirtualGamepadAsync()
    {
        if (_virtualGamepadService is null)
        {
            return;
        }

        try
        {
            if (SelectedDevice is not { } device)
            {
                return;
            }

            await _virtualGamepadService.StopAsync(device.DeviceKey);
            UpdateVirtualGamepadState(_virtualGamepadService.IsRunning
                ? "Se detuvo la salida virtual de este mando; las demás salidas siguen activas."
                : "La salida virtual está detenida.");
        }
        catch (IOException exception)
        {
            VirtualGamepadStatus = $"No se pudo detener correctamente el mando virtual: {exception.Message}";
            _logInputError?.Invoke(VirtualGamepadStatus);
        }
        catch (InvalidOperationException exception)
        {
            VirtualGamepadStatus = $"No se pudo detener correctamente el mando virtual: {exception.Message}";
            _logInputError?.Invoke(VirtualGamepadStatus);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            VirtualGamepadStatus = $"Windows no pudo retirar el mando virtual: {exception.Message}";
            _logInputError?.Invoke(VirtualGamepadStatus);
        }
        catch (UnauthorizedAccessException exception)
        {
            VirtualGamepadStatus = $"Windows denegó el cierre del mando virtual: {exception.Message}";
            _logInputError?.Invoke(VirtualGamepadStatus);
        }
        finally
        {
            NotifyVirtualGamepadCommands();
        }
    }

    public void ReportVirtualGamepadStatus(string status) =>
        VirtualGamepadStatus = status;

    public void Dispose()
    {
        if (_virtualGamepadService is null)
        {
            return;
        }

        _virtualGamepadService.Dispose();
    }

    public void ReportInputError(Exception exception)
    {
        string? deviceKey = SelectedDevice?.DeviceKey;
        InputStatus = $"No se pudieron leer las entradas del mando. {exception.Message}";
        if (deviceKey is not null && _lastErrorDeviceKey != deviceKey)
        {
            _lastErrorDeviceKey = deviceKey;
            _logInputError?.Invoke(InputStatus);
        }
    }

    private void UpdateVirtualGamepadState(string status)
    {
        VirtualGamepadStatus = status;
        NotifyVirtualGamepadCommands();
    }

    private void NotifyVirtualGamepadCommands()
    {
        OnPropertyChanged(nameof(IsVirtualGamepadRunning));
        OnPropertyChanged(nameof(IsSelectedDeviceVirtualGamepadRunning));
        OnPropertyChanged(nameof(IsStartingVirtualGamepad));
        OnPropertyChanged(nameof(CanSelectDevice));
        OnPropertyChanged(nameof(CanStartVirtualGamepad));
        OnPropertyChanged(nameof(CanStopVirtualGamepad));
        _startVirtualGamepadCommand.NotifyCanExecuteChanged();
        _stopVirtualGamepadCommand.NotifyCanExecuteChanged();
        _selectDeviceCommand.NotifyCanExecuteChanged();
    }

    private double GetAxisValue(string name) =>
        InputSnapshot?.Axes.FirstOrDefault(axis => axis.Name == name)?.Value ?? 0;

    private bool IsButtonPressed(string name) =>
        InputSnapshot?.Buttons.FirstOrDefault(button => button.Name == name)?.IsPressed ?? false;

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

    private sealed class AsyncCommand(
        Func<object?, Task> execute,
        Func<object?, bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => canExecute(parameter);
        public async void Execute(object? parameter) => await execute(parameter);
        public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
