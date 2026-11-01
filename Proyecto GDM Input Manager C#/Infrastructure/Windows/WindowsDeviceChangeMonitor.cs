using System.Runtime.InteropServices;
using System.Windows.Forms;
using GDMInputManager.Interfaces;
using GDMInputManager.Services;

namespace GDMInputManager.Infrastructure.Windows;

public sealed class WindowsDeviceChangeMonitor : IDeviceChangeMonitor
{
    private const int WmClose = 0x0010;
    private const int WmDeviceChange = 0x0219;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDeviceRemoveComplete = 0x8004;
    private const int DbtDevicesChanged = 0x0007;
    private const int DeviceNotifyWindowHandle = 0;
    private static readonly Guid HidDeviceInterfaceGuid = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    private readonly object _lifecycleGate = new();
    private readonly System.Threading.Timer _debounceTimer;
    private Thread? _messageThread;
    private TaskCompletionSource<bool>? _started;
    private IntPtr _windowHandle;
    private bool _disposed;

    public event EventHandler? DevicesChanged;

    public WindowsDeviceChangeMonitor()
    {
        _debounceTimer = new System.Threading.Timer(_ => DevicesChanged?.Invoke(this, EventArgs.Empty));
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lifecycleGate)
        {
            if (_messageThread is { IsAlive: true })
            {
                return;
            }

            _started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _messageThread = new Thread(RunMessageLoop)
            {
                IsBackground = true,
                Name = "GDM Windows device monitor"
            };
            _messageThread.SetApartmentState(ApartmentState.STA);
            _messageThread.Start();
        }

        _started.Task.GetAwaiter().GetResult();
    }

    public void Stop()
    {
        Thread? messageThread;
        IntPtr windowHandle;
        lock (_lifecycleGate)
        {
            messageThread = _messageThread;
            windowHandle = _windowHandle;
        }

        if (messageThread == null)
        {
            return;
        }

        if (windowHandle != IntPtr.Zero && !PostMessage(windowHandle, WmClose, IntPtr.Zero, IntPtr.Zero))
        {
            LoggerService.Instance.Warning(
                $"No se pudo detener el monitor de dispositivos (código {Marshal.GetLastWin32Error()}).");
        }

        if (messageThread != Thread.CurrentThread)
        {
            messageThread.Join();
        }

        lock (_lifecycleGate)
        {
            _messageThread = null;
            _windowHandle = IntPtr.Zero;
        }

        _debounceTimer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _debounceTimer.Dispose();
        _disposed = true;
    }

    private void RunMessageLoop()
    {
        MonitorWindow? window = null;
        IntPtr notificationHandle = IntPtr.Zero;
        try
        {
            window = new MonitorWindow(this);
            window.CreateHandle(new CreateParams
            {
                Caption = $"GDM device monitor {Guid.NewGuid():N}",
                Style = 0
            });

            var filter = new DeviceBroadcastDeviceInterface
            {
                Size = Marshal.SizeOf<DeviceBroadcastDeviceInterface>(),
                DeviceType = 0x00000005,
                ClassGuid = HidDeviceInterfaceGuid
            };
            IntPtr filterPointer = Marshal.AllocHGlobal(filter.Size);
            try
            {
                Marshal.StructureToPtr(filter, filterPointer, false);
                notificationHandle = RegisterDeviceNotification(
                    window.Handle,
                    filterPointer,
                    DeviceNotifyWindowHandle);
            }
            finally
            {
                Marshal.FreeHGlobal(filterPointer);
            }

            if (notificationHandle == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(
                    $"Windows no pudo registrar los cambios de mandos HID (código {error}).");
            }

            lock (_lifecycleGate)
            {
                _windowHandle = window.Handle;
            }

            _started?.TrySetResult(true);
            LoggerService.Instance.Info("Monitor de cambios de dispositivos iniciado.");
            Application.Run(new ApplicationContext());
        }
        catch (Exception ex)
        {
            _started?.TrySetException(ex);
        }
        finally
        {
            if (notificationHandle != IntPtr.Zero && !UnregisterDeviceNotification(notificationHandle))
            {
                LoggerService.Instance.Warning(
                    $"Windows no pudo cancelar el registro HID (código {Marshal.GetLastWin32Error()}).");
            }

            window?.DestroyHandle();
            lock (_lifecycleGate)
            {
                _windowHandle = IntPtr.Zero;
            }
        }
    }

    private void OnDeviceChange(int changeType)
    {
        if (changeType is DbtDeviceArrival or DbtDeviceRemoveComplete or DbtDevicesChanged)
        {
            string changeName = changeType switch
            {
                DbtDeviceArrival => "llegada",
                DbtDeviceRemoveComplete => "retirada",
                _ => "cambio"
            };
            LoggerService.Instance.Info(
                $"Monitor de dispositivos recibió una notificación de {changeName}.");
            _debounceTimer.Change(350, Timeout.Infinite);
        }
    }

    private void RequestStop()
    {
        Application.ExitThread();
    }

    [DllImport("user32.dll", EntryPoint = "RegisterDeviceNotificationW", SetLastError = true)]
    private static extern IntPtr RegisterDeviceNotification(
        IntPtr recipient,
        IntPtr notificationFilter,
        int flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterDeviceNotification(IntPtr handle);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DeviceBroadcastDeviceInterface
    {
        public int Size;
        public int DeviceType;
        public int Reserved;
        public Guid ClassGuid;
        public char Name;
    }

    private sealed class MonitorWindow(WindowsDeviceChangeMonitor owner) : NativeWindow
    {
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmDeviceChange)
            {
                owner.OnDeviceChange(message.WParam.ToInt32());
            }
            else if (message.Msg == WmClose)
            {
                owner.RequestStop();
                DestroyHandle();
                return;
            }

            base.WndProc(ref message);
        }
    }
}
