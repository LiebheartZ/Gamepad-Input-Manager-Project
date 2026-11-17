using System.IO;
using System.IO.Pipes;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Windows;

namespace GDM.Services;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private static readonly TimeSpan HandoffTimeout = TimeSpan.FromMinutes(2);
    private const int SwRestore = 9;
    private const int SwShow = 5;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNoTopmost = new(-2);
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpShowWindow = 0x0040;
    private const uint FlashwAll = 3;
    private const uint FlashwTimerNoForeground = 12;
    private readonly string _mutexName;
    private readonly string _pipeName;
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _listenerCancellation = new();
    private bool _ownsMutex;
    private bool _isDisposed;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr state);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr windowHandle, StringBuilder text, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr windowHandle, int command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool BringWindowToTop(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool FlashWindowEx(ref FlashWindowInfo info);

    private delegate bool EnumWindowsCallback(IntPtr windowHandle, IntPtr state);

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public IntPtr WindowHandle;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    public SingleInstanceCoordinator()
    {
        string identity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        string identityHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
        string suffix = $"{identityHash}-{ProcessSessionId()}";
        _mutexName = $@"Local\GDM-{suffix}";
        _pipeName = $"GDM-{suffix}";
        _mutex = new Mutex(initiallyOwned: false, _mutexName);
    }

    public bool TryBecomePrimary()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        try
        {
            _ownsMutex = _mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }

        return _ownsMutex;
    }

    public void WaitForPrimaryHandoff()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        try
        {
            _ownsMutex = _mutex.WaitOne(HandoffTimeout);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }

        if (!_ownsMutex)
        {
            throw new TimeoutException(
                "La instancia anterior de GDM no terminó a tiempo para completar el inicio elevado.");
        }
    }

    public Task StartListeningAsync(Action activatePrimary, Action<Exception> reportError)
    {
        ArgumentNullException.ThrowIfNull(activatePrimary);
        ArgumentNullException.ThrowIfNull(reportError);
        if (!_ownsMutex)
        {
            throw new InvalidOperationException(
                "Solo la instancia principal de GDM puede iniciar el canal de activación.");
        }

        return ListenForActivationAsync(activatePrimary, reportError, _listenerCancellation.Token);
    }

    public static async Task<bool> NotifyPrimaryAsync()
    {
        string identity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        string identityHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
        string pipeName = $"GDM-{identityHash}-{ProcessSessionId()}";

        for (int attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                await using var client = new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                await client.ConnectAsync(250);
                byte[] processIdBytes = new byte[sizeof(int)];
                await client.ReadExactlyAsync(processIdBytes);
                uint primaryProcessId = BinaryPrimitives.ReadUInt32LittleEndian(processIdBytes);
                if (primaryProcessId == 0)
                {
                    throw new IOException("La instancia principal devolvió un identificador de proceso no válido.");
                }

                _ = AllowSetForegroundWindow(primaryProcessId);
                await client.WriteAsync(new byte[] { 1 });
                await client.FlushAsync();
                byte[] acknowledgment = new byte[1];
                await client.ReadExactlyAsync(acknowledgment);
                if (acknowledgment[0] == 1)
                {
                    if (ActivateWindow(primaryProcessId))
                    {
                        return true;
                    }
                }
            }
            catch (TimeoutException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            await Task.Delay(150);
        }

        return ActivateExistingWindow();
    }

    private async Task ListenForActivationAsync(
        Action activatePrimary,
        Action<Exception> reportError,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var server = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await server.WaitForConnectionAsync(cancellationToken);
                byte[] processIdBytes = new byte[sizeof(int)];
                BinaryPrimitives.WriteUInt32LittleEndian(
                    processIdBytes,
                    checked((uint)Environment.ProcessId));
                await server.WriteAsync(processIdBytes, cancellationToken);
                await server.FlushAsync(cancellationToken);
                byte[] request = new byte[1];
                if (await server.ReadAsync(request, cancellationToken) == 1)
                {
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(activatePrimary);
                    await server.WriteAsync(new byte[] { 1 }, cancellationToken);
                    await server.FlushAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException exception)
            {
                reportError(exception);
            }
        }
    }

    private static int ProcessSessionId() =>
        System.Diagnostics.Process.GetCurrentProcess().SessionId;

    private static bool ActivateExistingWindow()
    {
        return ActivateWindow(processId: null);
    }

    private static bool ActivateWindow(uint? processId)
    {
        IntPtr windowHandle = IntPtr.Zero;
        uint windowProcessId = 0;
        _ = EnumWindows((candidate, state) =>
        {
            _ = state;
            if (GetWindowThreadProcessId(candidate, out uint candidateProcessId) == 0 ||
                (processId.HasValue && candidateProcessId != processId.Value))
            {
                return true;
            }

            if (processId.HasValue)
            {
                windowHandle = candidate;
                windowProcessId = candidateProcessId;
                return false;
            }

            var title = new StringBuilder(256);
            _ = GetWindowText(candidate, title, title.Capacity);
            if (string.Equals(title.ToString(), "GDM — Gamepad Device Manager", StringComparison.Ordinal))
            {
                windowHandle = candidate;
                windowProcessId = candidateProcessId;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        _ = AllowSetForegroundWindow(windowProcessId);
        _ = ShowWindow(windowHandle, SwRestore);
        _ = ShowWindow(windowHandle, SwShow);
        _ = SetWindowPos(
            windowHandle,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpShowWindow);
        _ = SetWindowPos(
            windowHandle,
            HwndNoTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpShowWindow);
        _ = BringWindowToTop(windowHandle);
        if (!SetForegroundWindow(windowHandle))
        {
            var flashInfo = new FlashWindowInfo
            {
                Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
                WindowHandle = windowHandle,
                Flags = FlashwAll | FlashwTimerNoForeground,
                Count = uint.MaxValue
            };
            _ = FlashWindowEx(ref flashInfo);
        }

        return true;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _listenerCancellation.Cancel();
        if (_ownsMutex)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
        _listenerCancellation.Dispose();
    }
}
