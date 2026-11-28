using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Windows.Devices.Enumeration;

namespace GDM.Services;

/// <summary>
/// Emparejamiento Bluetooth delegado a un proceso no elevado del propio
/// ejecutable (hardlink GDM.Pair.exe lanzado vía explorer.exe). En Windows 10
/// el emparejamiento de mandos es silencioso, pero PairAsync falla cuando lo
/// llama un proceso elevado; el helper corre con el token normal del usuario
/// y el diálogo de Windows puede aparecer si el sistema lo pide.
/// </summary>
public static class BluetoothPairHelper
{
    public const string HelperFileName = "GDM.Pair.exe";
    private const string RequestFileName = "pair-request.json";
    private const string ResultFilePrefix = "pair-result-";
    private const string ResultFileSuffix = ".txt";

    public sealed record PairRequest(string DeviceId, string RequestId);

    public static string WorkDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GDM");

    public static string RequestPath => Path.Combine(WorkDirectory, RequestFileName);

    public static string ResultPath(string requestId) => Path.Combine(
        WorkDirectory, $"{ResultFilePrefix}{requestId}{ResultFileSuffix}");

    /// <summary>
    /// True cuando el proceso actual arrancó como GDM.Pair.exe: modo helper
    /// que solo ejecuta el emparejamiento y escribe el resultado en archivo.
    /// </summary>
    public static bool IsPairHelperProcess =>
        string.Equals(
            Path.GetFileName(Environment.ProcessPath),
            HelperFileName,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Borra restos de emparejamientos anteriores (arranque de GDM).</summary>
    public static void CleanupStaleFiles()
    {
        try
        {
            string directory = WorkDirectory;
            if (!Directory.Exists(directory))
            {
                return;
            }

            File.Delete(RequestPath);
            foreach (string file in Directory.GetFiles(directory, $"{ResultFilePrefix}*{ResultFileSuffix}"))
            {
                File.Delete(file);
            }
        }
        catch
        {
            // Limpieza best-effort; los archivos se sobrescriben después.
        }
    }

    private static string HelperLogPath => Path.Combine(WorkDirectory, "pair-helper.log");

    private static void HelperLog(string message)
    {
        try
        {
            Directory.CreateDirectory(WorkDirectory);
            File.AppendAllText(HelperLogPath,
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    /// <summary>
    /// Cuerpo del modo helper: lee la petición, ejecuta el emparejamiento con
    /// la misma escalera de niveles que el servicio y escribe el estado final
    /// en el archivo de resultado. Devuelve el código de salida del proceso.
    /// </summary>
    public static async Task<int> RunAsync()
    {
        string? requestId = null;
        try
        {
            if (!File.Exists(RequestPath))
            {
                HelperLog("Sin archivo de petición; saliendo.");
                return 2;
            }

            PairRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<PairRequest>(
                    await File.ReadAllTextAsync(RequestPath).ConfigureAwait(false));
            }
            finally
            {
                try { File.Delete(RequestPath); } catch { }
            }

            if (request is null || string.IsNullOrWhiteSpace(request.DeviceId))
            {
                return 3;
            }

            requestId = request.RequestId;
            HelperLog($"Petición recibida: device={request.DeviceId}");

            string status;
            try
            {
                status = await PerformPairAsync(request.DeviceId).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                HelperLog($"PerformPairAsync lanzó: {exception}");
                status = nameof(DevicePairingResultStatus.Failed);
            }

            Directory.CreateDirectory(WorkDirectory);
            await File.WriteAllTextAsync(ResultPath(request.RequestId), status)
                .ConfigureAwait(false);
            HelperLog($"Resultado escrito: {status}");
            return 0;
        }
        catch (Exception exception)
        {
            HelperLog($"Error general del helper: {exception}");
            if (requestId is not null)
            {
                try
                {
                    Directory.CreateDirectory(WorkDirectory);
                    await File.WriteAllTextAsync(
                        ResultPath(requestId),
                        nameof(DevicePairingResultStatus.Failed)).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            return 1;
        }
    }

    private static async Task<string> PerformPairAsync(string deviceId)
    {
        DeviceInformation? device;
        try
        {
            // Debe resolverse como AssociationEndpoint (AEP): con el overload
            // simple el objeto resultante no completa el emparejamiento.
            device = await DeviceInformation.CreateFromIdAsync(
                deviceId,
                Array.Empty<string>(),
                DeviceInformationKind.AssociationEndpoint).AsTask().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            HelperLog($"CreateFromIdAsync lanzó: {exception.Message}");
            device = null;
        }

        if (device is null)
        {
            HelperLog($"CreateFromIdAsync no resolvió {deviceId}");
            return nameof(DevicePairingResultStatus.NotReadyToPair);
        }

        HelperLog($"Dispositivo resuelto: {device.Name}; IsPaired={device.Pairing.IsPaired}; CanPair={device.Pairing.CanPair}");
        if (device.Pairing.IsPaired)
        {
            return nameof(DevicePairingResultStatus.AlreadyPaired);
        }

        // En apps de escritorio PairAsync estándar no está soportado por
        // Windows (falla al instante); la vía oficial es el emparejamiento
        // personalizado con todos los tipos de ceremonia.
        DevicePairingResultStatus status = await TryCustomPairAsync(device)
            .ConfigureAwait(false);
        HelperLog($"Personalizado → {status}");

        if (!IsSuccess(status))
        {
            status = await TryLevelAsync(
                device, DevicePairingProtectionLevel.EncryptionAndAuthentication)
                .ConfigureAwait(false);
            HelperLog($"EncryptionAndAuthentication → {status}");
        }

        if (!IsSuccess(status))
        {
            await Task.Delay(800).ConfigureAwait(false);
            status = await TryLevelAsync(device, DevicePairingProtectionLevel.Encryption)
                .ConfigureAwait(false);
            HelperLog($"Encryption → {status}");
        }

        if (!IsSuccess(status))
        {
            await Task.Delay(800).ConfigureAwait(false);
            status = await TryLevelAsync(device, DevicePairingProtectionLevel.None)
                .ConfigureAwait(false);
            HelperLog($"None → {status}");
        }

        if (!IsSuccess(status))
        {
            try
            {
                DeviceInformationCustomPairing customPairing = device.Pairing.Custom;
                customPairing.PairingRequested += (_, args) =>
                {
                    try { args.Accept(); } catch { }
                };
                DevicePairingResult customResult = await customPairing.PairAsync(
                    DevicePairingKinds.ConfirmOnly,
                    DevicePairingProtectionLevel.None).AsTask().ConfigureAwait(false);
                status = customResult.Status;
                HelperLog($"Personalizado (ConfirmOnly) → {status}");
            }
            catch (Exception exception)
            {
                HelperLog($"Emparejamiento personalizado no disponible: {exception.Message}");
            }
        }

        // Último recurso: la API Win32 clásica (bthprops.cpl), la misma que
        // usa el Panel de control. Cuando WinRT PairAsync es rechazado al
        // instante por el sistema, este camino puede completar igualmente.
        if (!IsSuccess(status))
        {
            string? mac = Win32BluetoothPairing.MacFromDeviceId(deviceId);
            if (mac is not null)
            {
                DevicePairingResultStatus? win32 = await Task.Run(
                    () => Win32BluetoothPairing.TryAuthenticate(mac, HelperLog))
                    .ConfigureAwait(false);
                if (win32 is not null)
                {
                    status = win32.Value;
                    HelperLog($"Vía Win32 → {status}");
                }
            }
        }

        return status.ToString();
    }

    /// <summary>
    /// Emparejamiento personalizado con todos los tipos de ceremonia que un
    /// mando puede pedir (ConfirmOnly, ProvidePin, ConfirmPinMatch). Es la
    /// única vía de Pairing que Windows soporta oficialmente en apps de
    /// escritorio; el PairAsync estándar siempre falla en este contexto.
    /// </summary>
    private static async Task<DevicePairingResultStatus> TryCustomPairAsync(DeviceInformation device)
    {
        DeviceInformationCustomPairing? customPairing;
        try
        {
            customPairing = device.Pairing.Custom;
        }
        catch (Exception exception)
        {
            HelperLog($"Pairing.Custom no disponible: {exception.Message}");
            return DevicePairingResultStatus.Failed;
        }

        void OnPairingRequested(DeviceInformationCustomPairing sender, DevicePairingRequestedEventArgs args)
        {
            try
            {
                HelperLog($"Ceremonia de emparejamiento: {args.PairingKind}");
                // ProvidePin requiere entregar el PIN como parámetro de
                // Accept; el resto de ceremonias se acepta directamente.
                if (args.PairingKind == DevicePairingKinds.ProvidePin)
                {
                    args.Accept("0000");
                }
                else
                {
                    args.Accept();
                }
            }
            catch (Exception exception)
            {
                HelperLog($"Error respondiendo la ceremonia ({args.PairingKind}): {exception.Message}");
            }
        }

        customPairing.PairingRequested += OnPairingRequested;
        try
        {
            DevicePairingResult result = await customPairing.PairAsync(
                DevicePairingKinds.ConfirmOnly |
                DevicePairingKinds.DisplayPin |
                DevicePairingKinds.ProvidePin |
                DevicePairingKinds.ConfirmPinMatch,
                DevicePairingProtectionLevel.None).AsTask().ConfigureAwait(false);
            return result.Status;
        }
        catch (Exception exception)
        {
            HelperLog($"Pairing.Custom.PairAsync lanzó: {exception.Message}");
            return DevicePairingResultStatus.Failed;
        }
        finally
        {
            customPairing.PairingRequested -= OnPairingRequested;
        }
    }

    private static async Task<DevicePairingResultStatus> TryLevelAsync(
        DeviceInformation device, DevicePairingProtectionLevel level)
    {
        try
        {
            return (await device.Pairing.PairAsync(level).AsTask().ConfigureAwait(false)).Status;
        }
        catch
        {
            return DevicePairingResultStatus.HardwareFailure;
        }
    }

    private static bool IsSuccess(DevicePairingResultStatus status) =>
        status is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired;
}
