using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using GDM.Models;
using GDM.Services.Abstractions;

namespace GDM.Services;

public sealed class HidHideService : IHidHideService
{
    private const string ControlDevicePath = @"\\.\HidHide";
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 1;
    private const uint FileShareWrite = 2;
    private const uint FileShareDelete = 4;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x80;
    private const uint IoctlGetWhitelist = 2048;
    private const uint IoctlSetWhitelist = 2049;
    private const uint IoctlGetBlacklist = 2050;
    private const uint IoctlSetBlacklist = 2051;
    private const uint IoctlGetActive = 2052;
    private const uint IoctlGetWhitelistInverse = 2054;
    private const uint MaxMultiSzBytes = 1024 * 1024;
    private const uint DeviceType = 32769;
    private const uint FileReadData = 1;
    private const uint MethodBuffered = 0;

    private readonly string _recoveryFilePath;
    private readonly Action<DiagnosticLogLevel, string, Exception?>? _writeDiagnostic;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly Dictionary<string, string> _managedDevices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _deviceInstanceIdsByPath = new(StringComparer.OrdinalIgnoreCase);
    private bool _recoveryAttempted;

    public HidHideService(
        string? recoveryFilePath = null,
        Action<DiagnosticLogLevel, string, Exception?>? writeDiagnostic = null)
    {
        _recoveryFilePath = Path.GetFullPath(recoveryFilePath ??
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GDM",
                "HidHide",
                "recovery.json"));
        _writeDiagnostic = writeDiagnostic;
    }

    public Task<HidHideStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SafeFileHandle device = OpenDevice();
        return Task.FromResult(new HidHideStatus(
            IsAvailable: true,
            IsEnabled: GetBoolean(device, IoctlGetActive),
            IsInverseWhitelist: GetBoolean(device, IoctlGetWhitelistInverse)));
    }

    public async Task HideUntilGdmClosesAsync(
        string devicePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string instanceId = ResolveDeviceInstanceId(devicePath);
        _deviceInstanceIdsByPath[devicePath] = instanceId;
        WriteDiagnostic(
            DiagnosticLogLevel.Information,
            $"Solicitud de ocultamiento. rutaSDL=\"{devicePath}\"; instanciaHID=\"{instanceId}\".");
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureRecoveryAttemptedAsync(cancellationToken);
            using SafeFileHandle device = OpenDevice();
            EnsureCanHide(device);
            string executablePath = EnsureCurrentApplicationIsWhitelisted(device);

            RecoveryJournalData journal = await ReadRecoveryJournalAsync(cancellationToken);
            Dictionary<string, string> managedDevices = journal.ManagedDevices;
            string[] blacklist = ReadMultiSz(device, IoctlGetBlacklist);
            bool wasAlreadyBlacklisted = blacklist.Contains(
                instanceId,
                StringComparer.OrdinalIgnoreCase);
            WriteDiagnostic(
                DiagnosticLogLevel.Information,
                $"Validación antes de ocultar. ejecutable=\"{executablePath}\"; " +
                $"permitidoEnWhitelist=True; yaEnListaNegra={wasAlreadyBlacklisted}; " +
                $"cantidadWhitelist={ReadMultiSz(device, IoctlGetWhitelist).Length}; " +
                $"cantidadListaNegra={blacklist.Length}.");
            if (blacklist.Contains(instanceId, StringComparer.OrdinalIgnoreCase))
            {
                if (!managedDevices.ContainsKey(instanceId))
                {
                    throw new InvalidOperationException(
                        "Este mando ya estaba en la lista negra de HidHide antes de que GDM lo ocultara. " +
                        "Para no quitar una configuración ajena, GDM no cambiará esa entrada.");
                }

                _managedDevices[instanceId] = devicePath;
                return;
            }

            managedDevices[instanceId] = devicePath;
            await SaveRecoveryJournalAsync(
                managedDevices,
                journal.PersistentDeviceInstanceIds,
                cancellationToken);
            SendMultiSz(device, IoctlSetBlacklist, [.. blacklist, instanceId]);
            string[] updatedBlacklist = ReadMultiSz(device, IoctlGetBlacklist);
            if (!updatedBlacklist.Contains(instanceId, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "HidHide no confirmó que el mando se agregara a la lista negra.");
            }

            _managedDevices[instanceId] = devicePath;
            WriteDiagnostic(
                DiagnosticLogLevel.Information,
                $"HidHide confirmó la lista negra. instanciaHID=\"{instanceId}\"; " +
                $"cantidadListaNegra={updatedBlacklist.Length}.");
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task ShowDeviceAsync(
        string devicePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? managedInstanceId = _managedDevices
            .FirstOrDefault(pair => string.Equals(
                pair.Value,
                devicePath,
                StringComparison.OrdinalIgnoreCase))
            .Key;
        string instanceId = managedInstanceId ?? ResolveDeviceInstanceId(devicePath);
        WriteDiagnostic(
            DiagnosticLogLevel.Information,
            $"Solicitud para mostrar dispositivo. rutaSDL=\"{devicePath}\"; instanciaHID=\"{instanceId}\".");
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureRecoveryAttemptedAsync(cancellationToken);
            RecoveryJournalData journal = await ReadRecoveryJournalAsync(cancellationToken);
            Dictionary<string, string> managedDevices = journal.ManagedDevices;
            if (!managedDevices.ContainsKey(instanceId))
            {
                return;
            }

            using SafeFileHandle device = OpenDevice();
            string[] blacklist = ReadMultiSz(device, IoctlGetBlacklist);
            string[] updatedBlacklist = blacklist
                .Where(id => !string.Equals(id, instanceId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            SendMultiSz(device, IoctlSetBlacklist, updatedBlacklist);
            string[] verifiedBlacklist = ReadMultiSz(device, IoctlGetBlacklist);
            if (verifiedBlacklist.Contains(instanceId, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "HidHide no confirmó que el mando se quitara de la lista negra.");
            }

            managedDevices.Remove(instanceId);
            journal.PersistentDeviceInstanceIds.Remove(instanceId);
            await SaveRecoveryJournalAsync(
                managedDevices,
                journal.PersistentDeviceInstanceIds,
                cancellationToken);
            _managedDevices.Remove(instanceId);
            WriteDiagnostic(
                DiagnosticLogLevel.Information,
                $"HidHide confirmó que el dispositivo está visible. instanciaHID=\"{instanceId}\"; " +
                $"cantidadListaNegra={verifiedBlacklist.Length}.");
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task RecoverPendingChangesAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            await RecoverPendingChangesCoreAsync(cancellationToken);
            _recoveryAttempted = true;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<bool> EnsureCurrentApplicationIsWhitelistedAsync(
        CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            SafeFileHandle device;
            try
            {
                device = OpenDevice();
            }
            catch (InvalidOperationException exception) when (
                exception.InnerException is Win32Exception { NativeErrorCode: 2 or 3 })
            {
                return false;
            }

            using (device)
            {
                string executablePath = EnsureCurrentApplicationIsWhitelisted(device);
                WriteDiagnostic(
                    DiagnosticLogLevel.Information,
                    $"Aplicación verificada en la lista permitida de HidHide. " +
                    $"ejecutable=\"{executablePath}\"; cantidadWhitelist={ReadMultiSz(device, IoctlGetWhitelist).Length}; " +
                    $"ocultamientoActivo={GetBoolean(device, IoctlGetActive)}; " +
                    $"listaInvertida={GetBoolean(device, IoctlGetWhitelistInverse)}.");
            }

            return true;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public Task RestoreManagedChangesAsync(CancellationToken cancellationToken = default) =>
        RestoreManagedChangesAsync(
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
            cancellationToken: cancellationToken);

    public async Task RestoreManagedChangesAsync(
        IReadOnlyDictionary<string, bool> hiddenDeviceExitPolicies,
        bool keepUnlistedDevicesHidden = false,
        CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            RecoveryJournalData journal = await ReadRecoveryJournalAsync(cancellationToken);
            Dictionary<string, string> managedDevices = journal.ManagedDevices;
            if (managedDevices.Count == 0)
            {
                return;
            }

            HashSet<string> persistentIds = journal.PersistentDeviceInstanceIds;
            var explicitlyRestoredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string devicePath, bool keepHidden) in hiddenDeviceExitPolicies)
            {
                string instanceId = ResolveDeviceInstanceId(devicePath);
                if (!managedDevices.ContainsKey(instanceId))
                {
                    continue;
                }

                if (keepHidden)
                {
                    persistentIds.Add(instanceId);
                }
                else
                {
                    persistentIds.Remove(instanceId);
                    explicitlyRestoredIds.Add(instanceId);
                }
            }

            if (keepUnlistedDevicesHidden)
            {
                persistentIds.UnionWith(managedDevices.Keys.Where(
                    id => !explicitlyRestoredIds.Contains(id)));
            }

            string[] idsToRestore = managedDevices.Keys
                .Where(id => !persistentIds.Contains(id))
                .ToArray();
            if (idsToRestore.Length > 0)
            {
                using SafeFileHandle device = OpenDevice();
                string[] blacklist = ReadMultiSz(device, IoctlGetBlacklist);
                string[] updatedBlacklist = blacklist
                    .Where(id => !idsToRestore.Contains(id, StringComparer.OrdinalIgnoreCase))
                    .ToArray();
                SendMultiSz(device, IoctlSetBlacklist, updatedBlacklist);
            }

            foreach (string instanceId in idsToRestore)
            {
                managedDevices.Remove(instanceId);
            }

            persistentIds.IntersectWith(managedDevices.Keys);
            await SaveRecoveryJournalAsync(managedDevices, persistentIds, cancellationToken);
            _managedDevices.Clear();
            foreach (string instanceId in persistentIds)
            {
                _managedDevices[instanceId] = instanceId;
            }

            _recoveryAttempted = true;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public bool IsHiddenByGdm(string devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
        {
            return false;
        }

        if (!_deviceInstanceIdsByPath.TryGetValue(devicePath, out string? instanceId))
        {
            instanceId = ResolveDeviceInstanceId(devicePath);
            _deviceInstanceIdsByPath[devicePath] = instanceId;
        }

        return _managedDevices.ContainsKey(instanceId);
    }

    public bool IsPhysicalDevicePresent(string devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
        {
            return false;
        }

        if (!_deviceInstanceIdsByPath.TryGetValue(devicePath, out string? instanceId))
        {
            instanceId = ResolveDeviceInstanceId(devicePath);
            _deviceInstanceIdsByPath[devicePath] = instanceId;
        }

        uint result = NativeMethods.CmLocateDevNode(
            out _,
            instanceId,
            NativeMethods.CmLocateDevNodeNormal);
        return result switch
        {
            NativeMethods.ConfigRetSuccess => true,
            NativeMethods.ConfigRetNoSuchDevNode => false,
            _ => throw new InvalidOperationException(
                $"Windows no pudo comprobar la presencia del mando físico (Configuration Manager 0x{result:X8}).")
        };
    }

    private async Task EnsureRecoveryAttemptedAsync(CancellationToken cancellationToken)
    {
        if (!_recoveryAttempted)
        {
            await RecoverPendingChangesCoreAsync(cancellationToken);
            _recoveryAttempted = true;
        }
    }

    private async Task RecoverPendingChangesCoreAsync(CancellationToken cancellationToken)
    {
        RecoveryJournalData journal = await ReadRecoveryJournalAsync(cancellationToken);
        Dictionary<string, string> managedDevices = journal.ManagedDevices;
        HashSet<string> persistentIds = journal.PersistentDeviceInstanceIds;
        if (managedDevices.Count == 0)
        {
            return;
        }

        string[] idsToRestore = managedDevices.Keys
            .Where(id => !persistentIds.Contains(id))
            .ToArray();
        if (idsToRestore.Length > 0)
        {
            using SafeFileHandle device = OpenDevice();
            string[] blacklist = ReadMultiSz(device, IoctlGetBlacklist);
            string[] updatedBlacklist = blacklist
                .Where(id => !idsToRestore.Contains(id, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            SendMultiSz(device, IoctlSetBlacklist, updatedBlacklist);
        }

        foreach (string instanceId in idsToRestore)
        {
            managedDevices.Remove(instanceId);
        }

        persistentIds.IntersectWith(managedDevices.Keys);
        await SaveRecoveryJournalAsync(managedDevices, persistentIds, cancellationToken);
        _managedDevices.Clear();
        foreach (string instanceId in persistentIds)
        {
            _managedDevices[instanceId] = instanceId;
        }
    }

    private async Task<RecoveryJournalData> ReadRecoveryJournalAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_recoveryFilePath))
        {
            return new RecoveryJournalData();
        }

        string json = await File.ReadAllTextAsync(_recoveryFilePath, cancellationToken);
        RecoveryJournal journal;
        try
        {
            journal = JsonSerializer.Deserialize<RecoveryJournal>(json)
                ?? throw new InvalidDataException("El diario de recuperación de HidHide está vacío.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "El diario de recuperación de HidHide está dañado; no se modificó la lista negra.",
                exception);
        }

        if (journal.AddedDeviceInstanceIds is null)
        {
            throw new InvalidDataException(
                "El diario de recuperación de HidHide no contiene una lista de dispositivos válida.");
        }

        var devices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string instanceId in journal.AddedDeviceInstanceIds)
        {
            if (string.IsNullOrWhiteSpace(instanceId) ||
                !instanceId.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "El diario de recuperación contiene una identidad HID no válida; no se modificó la lista negra.");
            }

            devices[instanceId] = instanceId;
        }

        var persistentDevices = new HashSet<string>(
            journal.KeepHiddenDeviceInstanceIds ?? [],
            StringComparer.OrdinalIgnoreCase);
        if (!persistentDevices.IsSubsetOf(devices.Keys))
        {
            throw new InvalidDataException(
                "El diario de HidHide marca como persistente un dispositivo que no está en la lista administrada.");
        }

        return new RecoveryJournalData(devices, persistentDevices);
    }

    private async Task SaveRecoveryJournalAsync(
        IReadOnlyDictionary<string, string> devices,
        IReadOnlySet<string> persistentDeviceInstanceIds,
        CancellationToken cancellationToken)
    {
        if (devices.Count == 0)
        {
            if (File.Exists(_recoveryFilePath))
            {
                File.Delete(_recoveryFilePath);
            }

            return;
        }

        string? directory = Path.GetDirectoryName(_recoveryFilePath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("No se pudo determinar la carpeta del diario de HidHide.");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = $"{_recoveryFilePath}.{Guid.NewGuid():N}.tmp";
        string json = JsonSerializer.Serialize(new RecoveryJournal(
            devices.Keys.ToArray(),
            persistentDeviceInstanceIds
                .Where(devices.ContainsKey)
                .ToArray()));
        byte[] content = Encoding.UTF8.GetBytes(json);
        using (var stream = new FileStream(
                   temporaryPath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None,
                   bufferSize: 4096,
                   FileOptions.WriteThrough))
        {
            await stream.WriteAsync(content, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, _recoveryFilePath, overwrite: true);
    }

    private sealed record RecoveryJournal(
        string[] AddedDeviceInstanceIds,
        string[]? KeepHiddenDeviceInstanceIds = null);

    private sealed record RecoveryJournalData(
        Dictionary<string, string> ManagedDevices,
        HashSet<string> PersistentDeviceInstanceIds)
    {
        public RecoveryJournalData()
            : this(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase))
        {
        }
    }

    private static void EnsureCanHide(SafeFileHandle device)
    {
        if (!GetBoolean(device, IoctlGetActive))
        {
            throw new InvalidOperationException(
                "HidHide está instalado, pero el ocultamiento global está desactivado. Actívalo en la configuración oficial de HidHide; GDM no cambiará ese ajuste global.");
        }

        if (GetBoolean(device, IoctlGetWhitelistInverse))
        {
            throw new InvalidOperationException(
                "HidHide está configurado con la lista de aplicaciones invertida. Para proteger el acceso de GDM, cambia ese modo en la configuración oficial de HidHide antes de ocultar mandos.");
        }
    }

    private static string EnsureCurrentApplicationIsWhitelisted(SafeFileHandle device)
    {
        string? executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("No se pudo determinar la ruta del ejecutable de GDM.");
        }

        string[] applications = ReadMultiSz(device, IoctlGetWhitelist);
        if (!applications.Any(path => string.Equals(
                path,
                executablePath,
                StringComparison.OrdinalIgnoreCase)))
        {
            SendMultiSz(device, IoctlSetWhitelist, [.. applications, executablePath]);
        }

        string[] verifiedApplications = ReadMultiSz(device, IoctlGetWhitelist);
        if (!verifiedApplications.Any(path => string.Equals(
                path,
                executablePath,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "HidHide no confirmó que la ruta del ejecutable actual de GDM esté en su lista de aplicaciones permitidas.");
        }

        return executablePath;
    }

    private void WriteDiagnostic(
        DiagnosticLogLevel level,
        string message,
        Exception? exception = null) =>
        _writeDiagnostic?.Invoke(level, message, exception);

    private static string ResolveDeviceInstanceId(string devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
        {
            throw new InvalidOperationException(
                "SDL no proporcionó la ruta de hardware necesaria para ocultar este mando.");
        }

        using var deviceInfoSet = new SafeDeviceInfoSet(NativeMethods.SetupDiGetClassDevs(
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.DigcfAllClasses | NativeMethods.DigcfDeviceInterface));
        if (deviceInfoSet.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var interfaceData = new DeviceInterfaceData
        {
            Size = Marshal.SizeOf<DeviceInterfaceData>()
        };
        if (!NativeMethods.SetupDiOpenDeviceInterface(
                deviceInfoSet,
                devicePath,
                0,
                ref interfaceData))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows no pudo relacionar la ruta SDL del mando con su dispositivo HID.");
        }

        _ = NativeMethods.SetupDiGetDeviceInterfaceDetail(
            deviceInfoSet,
            ref interfaceData,
            IntPtr.Zero,
            0,
            out uint requiredSize,
            IntPtr.Zero);
        int error = Marshal.GetLastWin32Error();
        if (requiredSize == 0 || error != NativeMethods.ErrorInsufficientBuffer)
        {
            throw new Win32Exception(
                error,
                "Windows no pudo obtener la identidad del dispositivo HID.");
        }

        IntPtr detailBuffer = Marshal.AllocHGlobal(checked((int)requiredSize));
        try
        {
            Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 6);
            var deviceInfo = new DeviceInfoData
            {
                Size = Marshal.SizeOf<DeviceInfoData>()
            };
            if (!NativeMethods.SetupDiGetDeviceInterfaceDetail(
                    deviceInfoSet,
                    ref interfaceData,
                    detailBuffer,
                    requiredSize,
                    out _,
                    ref deviceInfo))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var instanceId = new StringBuilder(1024);
            if (!NativeMethods.SetupDiGetDeviceInstanceId(
                    deviceInfoSet,
                    ref deviceInfo,
                    instanceId,
                    instanceId.Capacity,
                    out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            string result = instanceId.ToString();
            if (!result.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "La ruta SDL no corresponde a una interfaz HID compatible con el filtro de HidHide.");
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(detailBuffer);
        }
    }

    private static SafeFileHandle OpenDevice()
    {
        SafeFileHandle device = NativeMethods.CreateFile(
            ControlDevicePath,
            GenericRead,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal,
            IntPtr.Zero);
        if (device.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            device.Dispose();
            if (error is 2 or 3)
            {
                throw new InvalidOperationException(
                    "No se encontró el controlador HidHide activo. Instala o habilita HidHide desde su fuente oficial y vuelve a intentarlo.",
                    new Win32Exception(error));
            }

            throw new Win32Exception(error, "No se pudo abrir el dispositivo de control de HidHide.");
        }

        return device;
    }

    private static bool GetBoolean(SafeFileHandle device, uint function)
    {
        byte[] output = new byte[1];
        SendIoControl(device, function, null, output);
        return output[0] != 0;
    }

    private static string[] ReadMultiSz(SafeFileHandle device, uint function)
    {
        uint requiredBytes = SendIoControl(device, function, null, null);
        if (requiredBytes < sizeof(char) ||
            requiredBytes % sizeof(char) != 0 ||
            requiredBytes > MaxMultiSzBytes)
        {
            throw new InvalidDataException(
                $"HidHide devolvió un tamaño de lista no válido ({requiredBytes} bytes).");
        }

        byte[] output = new byte[requiredBytes];
        uint bytesReturned = SendIoControl(device, function, null, output);
        if (bytesReturned != requiredBytes)
        {
            throw new InvalidDataException(
                $"La lista devuelta por HidHide cambió de tamaño durante la lectura ({bytesReturned} de {requiredBytes} bytes).");
        }

        string contents = Encoding.Unicode.GetString(output, 0, checked((int)bytesReturned));
        return contents.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static void SendMultiSz(SafeFileHandle device, uint function, IReadOnlyList<string> values)
    {
        string payload = values.Count == 0
            ? "\0\0"
            : string.Join('\0', values) + "\0\0";
        byte[] input = Encoding.Unicode.GetBytes(payload);
        SendIoControl(device, function, input, null);
    }

    private static uint SendIoControl(
        SafeFileHandle device,
        uint function,
        byte[]? input,
        byte[]? output)
    {
        uint bytesReturned;
        if (!NativeMethods.DeviceIoControl(
                device,
                MakeIoControlCode(function),
                input,
                (uint)(input?.Length ?? 0),
                output,
                (uint)(output?.Length ?? 0),
                out bytesReturned,
                IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(
                error,
                $"La operación IOCTL {function} de HidHide no se completó (código Win32 {error}).");
        }

        return bytesReturned;
    }

    private static uint MakeIoControlCode(uint function) =>
        (DeviceType << 16) | (FileReadData << 14) | (function << 2) | MethodBuffered;

    private sealed class SafeDeviceInfoSet : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeDeviceInfoSet(IntPtr handle) : base(true) => SetHandle(handle);

        protected override bool ReleaseHandle() => NativeMethods.SetupDiDestroyDeviceInfoList(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData
    {
        public int Size;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoData
    {
        public int Size;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    private static class NativeMethods
    {
        internal const uint DigcfDeviceInterface = 0x10;
        internal const uint DigcfAllClasses = 0x4;
        internal const int ErrorInsufficientBuffer = 122;
        internal const uint CmLocateDevNodeNormal = 0;
        internal const uint ConfigRetSuccess = 0;
        internal const uint ConfigRetNoSuchDevNode = 13;

        [DllImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", CharSet = CharSet.Unicode)]
        internal static extern uint CmLocateDevNode(
            out uint deviceInstance,
            string deviceInstanceId,
            uint flags);

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(
            SafeFileHandle device,
            uint controlCode,
            byte[]? inputBuffer,
            uint inputBufferSize,
            byte[]? outputBuffer,
            uint outputBufferSize,
            out uint bytesReturned,
            IntPtr overlapped);

        [DllImport("setupapi.dll", SetLastError = true)]
        internal static extern IntPtr SetupDiGetClassDevs(
            IntPtr classGuid,
            IntPtr enumerator,
            IntPtr parent,
            uint flags);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiOpenDeviceInterfaceW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiOpenDeviceInterface(
            SafeDeviceInfoSet deviceInfoSet,
            string devicePath,
            uint flags,
            ref DeviceInterfaceData deviceInterfaceData);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceInterfaceDetail(
            SafeDeviceInfoSet deviceInfoSet,
            ref DeviceInterfaceData deviceInterfaceData,
            IntPtr deviceInterfaceDetailData,
            uint deviceInterfaceDetailDataSize,
            out uint requiredSize,
            IntPtr deviceInfoData);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceInterfaceDetail(
            SafeDeviceInfoSet deviceInfoSet,
            ref DeviceInterfaceData deviceInterfaceData,
            IntPtr deviceInterfaceDetailData,
            uint deviceInterfaceDetailDataSize,
            out uint requiredSize,
            ref DeviceInfoData deviceInfoData);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceInstanceId(
            SafeDeviceInfoSet deviceInfoSet,
            ref DeviceInfoData deviceInfoData,
            StringBuilder deviceInstanceId,
            int deviceInstanceIdSize,
            out int requiredSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
    }
}
