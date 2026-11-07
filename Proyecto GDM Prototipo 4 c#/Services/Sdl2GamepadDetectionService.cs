using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using GDM.Models;
using GDM.Services.Abstractions;
using SDL2;

namespace GDM.Services;

public sealed class Sdl2GamepadDetectionService : IGamepadDetectionService
{
    private readonly BlockingCollection<Action> _workQueue = new();
    private readonly Dictionary<int, OpenJoystick> _openJoysticks = [];
    private readonly Thread _ownerThread;
    private volatile bool _initialized;
    private volatile bool _disposed;

    public Sdl2GamepadDetectionService()
    {
        _ownerThread = new Thread(ProcessWorkQueue)
        {
            IsBackground = true,
            Name = "GDM SDL2 device owner"
        };
        _ownerThread.Start();
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        return InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_initialized)
            {
                return true;
            }

            if (SDL.SDL_Init(SDL.SDL_INIT_JOYSTICK | SDL.SDL_INIT_GAMECONTROLLER) < 0)
            {
                throw new InvalidOperationException(
                    $"SDL2 no pudo inicializar la detección de mandos: {SDL.SDL_GetError()}");
            }

            _initialized = true;
            return true;
        }, cancellationToken);
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        return InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_initialized)
            {
                return true;
            }

            UpdateDeviceList(cancellationToken);
            return true;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<ConnectedGamepad>> EnumerateConnectedAsync(
        CancellationToken cancellationToken = default)
    {
        return InvokeAsync<IReadOnlyList<ConnectedGamepad>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_initialized)
            {
                throw new InvalidOperationException("SDL2 aún no está inicializado.");
            }

            return UpdateDeviceList(cancellationToken);
        }, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            InvokeAsync(() =>
            {
                if (_initialized)
                {
                    foreach (OpenJoystick openJoystick in _openJoysticks.Values)
                    {
                        SDL.SDL_JoystickClose(openJoystick.Handle);
                    }

                    _openJoysticks.Clear();
                    SDL.SDL_Quit();
                    _initialized = false;
                }

                _disposed = true;
                return true;
            }, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            _workQueue.CompleteAdding();
            _ownerThread.Join();
            _workQueue.Dispose();
        }
    }

    private Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _workQueue.Add(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                completion.TrySetResult(action());
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }, cancellationToken);

        return completion.Task;
    }

    private void ProcessWorkQueue()
    {
        foreach (Action action in _workQueue.GetConsumingEnumerable())
        {
            action();
        }
    }

    private IReadOnlyList<ConnectedGamepad> UpdateDeviceList(CancellationToken cancellationToken)
    {
        SDL.SDL_PumpEvents();
        NativeMethods.SDL_JoystickUpdate();

        foreach ((int instanceId, OpenJoystick openJoystick) in _openJoysticks.ToArray())
        {
            if (NativeMethods.SDL_JoystickGetAttached(openJoystick.Handle) != 0)
            {
                continue;
            }

            SDL.SDL_JoystickClose(openJoystick.Handle);
            _openJoysticks.Remove(instanceId);
        }

        int count = SDL.SDL_NumJoysticks();
        for (int index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int instanceId = NativeMethods.SDL_JoystickGetDeviceInstanceID(index);
            if (instanceId < 0 || _openJoysticks.ContainsKey(instanceId))
            {
                continue;
            }

            IntPtr joystick = SDL.SDL_JoystickOpen(index);
            if (joystick == IntPtr.Zero)
            {
                continue;
            }

            int openedInstanceId = NativeMethods.SDL_JoystickInstanceID(joystick);
            if (openedInstanceId < 0 ||
                openedInstanceId != instanceId ||
                NativeMethods.SDL_JoystickGetAttached(joystick) == 0)
            {
                SDL.SDL_JoystickClose(joystick);
                continue;
            }

            ushort vendorId = SDL.SDL_JoystickGetVendor(joystick);
            ushort productId = SDL.SDL_JoystickGetProduct(joystick);
            var device = new ConnectedGamepad
            {
                DeviceKey = $"SDL2:{openedInstanceId}",
                Name = SDL.SDL_JoystickName(joystick) ?? "Mando desconocido",
                VendorId = vendorId == 0 ? null : vendorId,
                ProductId = productId == 0 ? null : productId,
                SerialNumber = GetOptionalString(joystick, NativeMethods.SDL_JoystickGetSerial),
                DevicePath = GetOptionalString(joystick, NativeMethods.SDL_JoystickPath)
            };
            _openJoysticks.Add(openedInstanceId, new OpenJoystick(joystick, device));
        }

        return _openJoysticks
            .OrderBy(pair => pair.Key)
            .Select(pair => pair.Value.Device)
            .ToArray();
    }

    private static string? GetOptionalString(IntPtr joystick, Func<IntPtr, IntPtr> getter)
    {
        try
        {
            IntPtr value = getter(joystick);
            string? text = value == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(value);
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static class NativeMethods
    {
        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void SDL_JoystickUpdate();

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SDL_JoystickGetDeviceInstanceID(int deviceIndex);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SDL_JoystickGetAttached(IntPtr joystick);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SDL_JoystickInstanceID(IntPtr joystick);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr SDL_JoystickGetSerial(IntPtr joystick);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr SDL_JoystickPath(IntPtr joystick);
    }

    private sealed record OpenJoystick(IntPtr Handle, ConnectedGamepad Device);
}
