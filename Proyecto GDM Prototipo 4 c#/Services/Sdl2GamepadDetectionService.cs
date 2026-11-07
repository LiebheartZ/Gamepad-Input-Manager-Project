using System.Runtime.InteropServices;
using GDM.Models;
using GDM.Services.Abstractions;
using SDL2;

namespace GDM.Services;

public sealed class Sdl2GamepadDetectionService : IGamepadDetectionService
{
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly Dictionary<int, OpenJoystick> _openJoysticks = [];
    private bool _initialized;
    private bool _disposed;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        VerifyOwnerThread();
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_initialized)
        {
            return Task.CompletedTask;
        }

        if (SDL.SDL_Init(SDL.SDL_INIT_JOYSTICK | SDL.SDL_INIT_GAMECONTROLLER) < 0)
        {
            throw new InvalidOperationException(
                $"SDL2 no pudo inicializar la detección de mandos: {SDL.SDL_GetError()}");
        }

        _initialized = true;
        NativeMethods.SDL_JoystickEventState(NativeMethods.SDL_ENABLE);
        SDL.SDL_PumpEvents();
        return Task.CompletedTask;
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        VerifyOwnerThread();
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_initialized)
        {
            PumpEvents();
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ConnectedGamepad>> EnumerateConnectedAsync(
        CancellationToken cancellationToken = default)
    {
        VerifyOwnerThread();
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
        {
            throw new InvalidOperationException("SDL2 aún no está inicializado.");
        }

        PumpEvents();
        return Task.FromResult(UpdateDeviceList(cancellationToken));
    }

    public void Dispose()
    {
        VerifyOwnerThread();
        if (_disposed)
        {
            return;
        }

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
    }

    private IReadOnlyList<ConnectedGamepad> UpdateDeviceList(CancellationToken cancellationToken)
    {
        NativeMethods.SDL_JoystickUpdate();

        int count = SDL.SDL_NumJoysticks();
        var currentInstanceIds = new HashSet<int>();
        for (int index = 0; index < count; index++)
        {
            int instanceId = NativeMethods.SDL_JoystickGetDeviceInstanceID(index);
            if (instanceId >= 0)
            {
                currentInstanceIds.Add(instanceId);
            }
        }

        foreach ((int instanceId, OpenJoystick openJoystick) in _openJoysticks.ToArray())
        {
            if (currentInstanceIds.Contains(instanceId) &&
                NativeMethods.SDL_JoystickGetAttached(openJoystick.Handle) != 0)
            {
                continue;
            }

            SDL.SDL_JoystickClose(openJoystick.Handle);
            _openJoysticks.Remove(instanceId);
        }

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

    private void PumpEvents()
    {
        VerifyOwnerThread();
        SDL.SDL_PumpEvents();
    }

    private void VerifyOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException(
                "Las operaciones de SDL2 deben ejecutarse en el hilo que creó el servicio.");
        }
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
        internal const int SDL_ENABLE = 1;

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SDL_JoystickEventState(int state);

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
