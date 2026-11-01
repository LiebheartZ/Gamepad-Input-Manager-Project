using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using GDMInputManager.Interfaces;
using GDMInputManager.Models;
using GDMInputManager.Services;
using SDL2;

namespace GDMInputManager.Infrastructure.SDL2;

public sealed class SDL2GamepadProvider : IGamepadProvider
{
    private readonly BlockingCollection<Action> _workQueue = new();
    private readonly Thread _ownerThread;
    private bool _initialized;
    private bool _disposed;

    public string Name => "SDL2";

    public SDL2GamepadProvider()
    {
        _ownerThread = new Thread(ProcessWorkQueue)
        {
            IsBackground = true,
            Name = "GDM SDL2 device owner"
        };
        _ownerThread.Start();
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
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
                throw new InvalidOperationException("SDL2 no pudo inicializar los subsistemas de joystick y game controller.");
            }

            _initialized = true;
            LoggerService.Instance.Info("El provider SDL2 se inicializó en su hilo propietario.");
            return true;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<GamepadDevice>> EnumerateAsync(CancellationToken cancellationToken)
    {
        return InvokeAsync<IReadOnlyList<GamepadDevice>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_initialized)
            {
                throw new InvalidOperationException("El provider SDL2 no está inicializado.");
            }

            var devices = new List<GamepadDevice>();
            SDL.SDL_PumpEvents();
            int count = SDL.SDL_NumJoysticks();
            for (int index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IntPtr joystick = SDL.SDL_JoystickOpen(index);
                if (joystick == IntPtr.Zero)
                {
                    LoggerService.Instance.Warning($"SDL2 no pudo abrir el joystick del índice {index}.");
                    continue;
                }

                try
                {
                    int instanceId = SDLNative.SDL_JoystickInstanceID(joystick);
                    if (instanceId < 0)
                    {
                        LoggerService.Instance.Warning($"SDL2 no devolvió un instance ID válido para el joystick {index}.");
                        continue;
                    }

                    ushort vendorId = SDL.SDL_JoystickGetVendor(joystick);
                    ushort productId = SDL.SDL_JoystickGetProduct(joystick);
                    string name = SDL.SDL_JoystickName(joystick) ?? "Mando desconocido";
                    string? serialNumber = GetOptionalString(joystick, SDLNative.SDL_JoystickGetSerial);
                    string? instancePath = GetOptionalString(joystick, SDLNative.SDL_JoystickPath);
                    DateTimeOffset now = DateTimeOffset.UtcNow;

                    devices.Add(new GamepadDevice
                    {
                        DeviceKey = $"{Name}:{instanceId}",
                        Provider = Name,
                        ProviderIndex = index,
                        Name = name,
                        Vid = vendorId == 0 ? null : vendorId,
                        Pid = productId == 0 ? null : productId,
                        Identity = new GamepadIdentity
                        {
                            SerialNumber = Normalize(serialNumber),
                            InstancePath = Normalize(instancePath)
                        },
                        GamepadType = Classify(name, vendorId, productId),
                        ConnectionType = ConnectionType.Unknown,
                        IsConnected = true,
                        FirstSeenUtc = now,
                        LastSeenUtc = now
                    });
                }
                finally
                {
                    SDL.SDL_JoystickClose(joystick);
                }
            }

            return devices;
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
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SDL2GamepadProvider));
        }

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
            catch (Exception ex)
            {
                completion.TrySetException(ex);
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

    private static string? GetOptionalString(IntPtr joystick, Func<IntPtr, IntPtr> getter)
    {
        try
        {
            IntPtr value = getter(joystick);
            return value == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(value);
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static GamepadType Classify(string name, ushort vendorId, ushort productId)
    {
        string normalizedName = new(name.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        if (vendorId == 0x054C && productId == 0x05C4 ||
            normalizedName is "PS4CONTROLLER" or "WIRELESSCONTROLLER" or "DUALSHOCK4WIRELESSCONTROLLER")
        {
            return GamepadType.PlayStationDualShock4;
        }

        if (vendorId == 0x054C && productId == 0x0CE6 ||
            name.Contains("DualSense", StringComparison.OrdinalIgnoreCase))
        {
            return GamepadType.PlayStationDualSense;
        }

        if (vendorId == 0x045E)
        {
            if (productId is 0x0B12 or 0x0B13 ||
                name.Contains("Series", StringComparison.OrdinalIgnoreCase))
            {
                return GamepadType.XboxSeries;
            }

            return productId is 0x028E or 0x028F
                ? GamepadType.Xbox360
                : GamepadType.XboxOne;
        }

        if (normalizedName.Contains("XBOX", StringComparison.Ordinal))
        {
            return normalizedName.Contains("SERIES", StringComparison.Ordinal)
                ? GamepadType.XboxSeries
                : GamepadType.XboxOne;
        }

        return name.Contains("Controller", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Gamepad", StringComparison.OrdinalIgnoreCase)
            ? GamepadType.GenericDirectInput
            : GamepadType.Unknown;
    }

    private static class SDLNative
    {
        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_JoystickInstanceID")]
        internal static extern int SDL_JoystickInstanceID(IntPtr joystick);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_JoystickGetSerial")]
        internal static extern IntPtr SDL_JoystickGetSerial(IntPtr joystick);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_JoystickPath")]
        internal static extern IntPtr SDL_JoystickPath(IntPtr joystick);
    }
}
