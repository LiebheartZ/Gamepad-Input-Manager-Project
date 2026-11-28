using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using GDM.Models;
using Microsoft.Toolkit.Uwp.Notifications;

namespace GDM.Services;

/// <summary>
/// Notificaciones toast de Windows para apps de escritorio sin empaquetar.
/// El toolkit registra el AppUserModelID automáticamente mediante un acceso
/// directo del menú Inicio la primera vez que se usa.
/// Windows no muestra toasts de procesos elevados, así que en ese caso se
/// delega en una instancia no elevada del propio ejecutable (hardlink
/// GDM.Notify.exe) lanzada a través de explorer.exe.
/// </summary>
public sealed class ToastNotificationService
{
    private const int BalloonTextMaxLength = 240;
    private const int MaxQueuedNotifications = 50;
    private const string NotifyHelperFileName = "GDM.Notify.exe";
    private const string PendingToastPrefix = "pending-toast-";
    private const string PendingToastSuffix = ".json";

    private readonly Func<bool> _isWindowVisible;
    private readonly Func<string> _notificationMode;
    private readonly Func<string, bool>? _isEventEnabled;
    private readonly Action<string, string>? _balloonFallback;
    private readonly Action<string>? _diagnosticLog;
    private readonly Func<int>? _queueIntervalMs;
    private readonly Action? _onToastActivated;
    private readonly bool _isElevated;
    private bool _initialized;
    private readonly Dictionary<string, long> _lastShownAtMs = new(StringComparer.Ordinal);
    private readonly object _queueSync = new();
    private readonly Queue<PendingNotification> _notificationQueue = new();
    private System.Threading.Timer? _queueTimer;
    private bool _queueTimerArmed;

    public ToastNotificationService(
        Func<bool> isWindowVisible,
        Func<string> notificationMode,
        Action<string, string>? balloonFallback = null,
        Action<string>? diagnosticLog = null,
        Func<int>? queueIntervalMs = null,
        Action? onToastActivated = null,
        Func<string, bool>? isEventEnabled = null)
    {
        _isWindowVisible = isWindowVisible;
        _notificationMode = notificationMode;
        _isEventEnabled = isEventEnabled;
        _balloonFallback = balloonFallback;
        _diagnosticLog = diagnosticLog;
        _queueIntervalMs = queueIntervalMs;
        _onToastActivated = onToastActivated;
        _isElevated = new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Muestra un toast si el modo de notificaciones lo permite.
    /// Cada elemento de <paramref name="lines"/> se muestra como una línea
    /// de texto bajo el título.
    /// </summary>
    public void Show(string title, params string[] lines)
    {
        string mode = _notificationMode();
        _diagnosticLog?.Invoke(
            $"Notificación «{title}»: modo={mode}, elevado={_isElevated}, lineas={lines.Length}.");
        if (string.Equals(mode, NotificationModes.Off, StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(mode, NotificationModes.WindowOnly, StringComparison.Ordinal) &&
            !_isWindowVisible())
        {
            _diagnosticLog?.Invoke("Notificación suprimida: la ventana no está visible.");
            return;
        }

        // Anti-ráfagas: no se repite el mismo aviso (título + primera línea)
        // en menos de 4 segundos. Evita inundar el centro de notificaciones
        // cuando un evento oscila (p. ej. reconexiones del mando).
        string floodKey = lines.Length > 0 ? $"{title}|{lines[0]}" : title;
        long nowMs = Environment.TickCount64;
        if (_lastShownAtMs.TryGetValue(floodKey, out long lastShown) &&
            nowMs - lastShown < 4000)
        {
            _diagnosticLog?.Invoke($"Notificación «{title}» suprimida por anti-ráfagas.");
            return;
        }
        _lastShownAtMs[floodKey] = nowMs;
        EnqueueNotification(title, lines);
    }

    /// <summary>
    /// Igual que <see cref="Show"/>, pero solo se muestra si el usuario tiene
    /// activado el evento en Ajustes → Interfaz (ver
    /// <see cref="Models.NotificationEvents"/>).
    /// </summary>
    public void ShowEvent(string eventId, string title, params string[] lines)
    {
        if (_isEventEnabled is not null && !_isEventEnabled(eventId))
        {
            _diagnosticLog?.Invoke($"Notificación «{title}» suprimida: evento «{eventId}» desactivado.");
            return;
        }

        Show(title, lines);
    }

    /// <summary>
    /// Encola la notificación: se muestra una cada intervalo configurable
    /// para que varias a la vez no se pisen en el centro de notificaciones.
    /// </summary>
    private void EnqueueNotification(string title, string[] lines)
    {
        PendingNotification? dispatchNow = null;
        lock (_queueSync)
        {
            if (_notificationQueue.Count >= MaxQueuedNotifications)
            {
                _notificationQueue.Dequeue();
                _diagnosticLog?.Invoke("Cola de notificaciones llena: se descartó la más antigua.");
            }

            _notificationQueue.Enqueue(new PendingNotification(title, lines));
            _queueTimer ??= new System.Threading.Timer(
                _ => DispatchNextNotification(),
                null,
                Timeout.Infinite,
                Timeout.Infinite);

            if (_notificationQueue.Count == 1 && !_queueTimerArmed)
            {
                // La cola estaba vacía: la primera notificación sale al
                // momento; solo las siguientes esperan el intervalo.
                dispatchNow = _notificationQueue.Dequeue();
            }
            else if (!_queueTimerArmed)
            {
                ArmQueueTimer();
            }
        }

        if (dispatchNow is { } immediate)
        {
            DispatchNotification(immediate.Title, immediate.Lines);
        }
    }

    private void ArmQueueTimer()
    {
        int interval = Math.Clamp(_queueIntervalMs?.Invoke() ?? 3000, 500, 60000);
        _queueTimer?.Change(interval, Timeout.Infinite);
        _queueTimerArmed = true;
    }

    private void DispatchNextNotification()
    {
        PendingNotification? item = null;
        lock (_queueSync)
        {
            if (_notificationQueue.Count > 0)
            {
                item = _notificationQueue.Dequeue();
            }

            if (_notificationQueue.Count == 0)
            {
                _queueTimerArmed = false;
            }
            else
            {
                ArmQueueTimer();
            }
        }

        if (item is { } pending)
        {
            DispatchNotification(pending.Title, pending.Lines);
        }
    }

    private void DispatchNotification(string title, string[] lines)
    {
        if (_isElevated)
        {
            // Windows no muestra toasts desde procesos elevados. Se delega en
            // una instancia no elevada del propio ejecutable (hardlink
            // GDM.Notify.exe) lanzada a través de explorer.exe, que ejecuta
            // con privilegios normales y lee el payload de un archivo.
            // Si el lanzamiento falla, se usa el globo de bandeja.
            if (!TryShowViaHelperProcess(title, lines))
            {
                ShowBalloon(title, lines);
            }
            return;
        }

        try
        {
            EnsureInitialized();
            BuildToastContent(title, lines).Show();
            _diagnosticLog?.Invoke("Toast enviado a Windows (revisar si está activado en Configuración > Sistema > Notificaciones).");
        }
        catch (Exception exception)
        {
            _diagnosticLog?.Invoke(
                $"No se pudo mostrar la notificación toast «{title}»: {exception.Message}");
        }
    }

    /// <summary>
    /// Escribe el payload en %LocalAppData%\GDM\pending-toast-*.json y lanza
    /// una copia no elevada del ejecutable (hardlink GDM.Notify.exe) a través
    /// de explorer.exe para que muestre el toast con privilegios normales.
    /// Devuelve false si no se pudo lanzar.
    /// </summary>
    private bool TryShowViaHelperProcess(string title, string[] lines)
    {
        try
        {
            string? executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                return false;
            }

            string? directory = Path.GetDirectoryName(executablePath);
            if (string.IsNullOrEmpty(directory))
            {
                return false;
            }

            string helperPath = Path.Combine(directory, NotifyHelperFileName);
            if (!EnsureHelperLink(helperPath, executablePath))
            {
                return false;
            }

            string pendingDirectory = PendingToastDirectory;
            Directory.CreateDirectory(pendingDirectory);
            string payloadPath = Path.Combine(
                pendingDirectory,
                $"{PendingToastPrefix}{Guid.NewGuid():N}{PendingToastSuffix}");
            File.WriteAllText(payloadPath, JsonSerializer.Serialize(
                new[] { title }.Concat(lines).ToArray()));

            // explorer.exe ignora su propia elevación y lanza el destino con el
            // token de integridad media del usuario: el toast sí se mostrará.
            // No admite argumentos, por eso el helper se distingue por nombre.
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{helperPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            _diagnosticLog?.Invoke($"Toast delegado a proceso no elevado para «{title}».");
            return true;
        }
        catch (Exception exception)
        {
            _diagnosticLog?.Invoke(
                $"No se pudo lanzar el proceso de notificaciones: {exception.Message}");
            return false;
        }
    }

    private static string PendingToastDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GDM");

    /// <summary>
    /// Crea (o refresca) el hardlink GDM.Notify.exe apuntando al ejecutable
    /// actual. Un hardlink no ocupa espacio extra y ejecuta el mismo binario;
    /// al arrancar, GDM se reconoce como helper por su nombre de archivo.
    /// </summary>
    internal static bool EnsureHelperLink(string helperPath, string executablePath)
    {
        // Si el helper anterior sigue vivo (arrancó hace un instante), el
        // borrado del hardlink puede fallar con IOException: se reintenta
        // unas veces antes de rendirse y perder la notificación.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (File.Exists(helperPath))
                {
                    FileInfo helperInfo = new(helperPath);
                    FileInfo exeInfo = new(executablePath);
                    if (helperInfo.Length == exeInfo.Length &&
                        helperInfo.LastWriteTimeUtc == exeInfo.LastWriteTimeUtc)
                    {
                        return true;
                    }

                    File.Delete(helperPath);
                }

                if (CreateHardLink(helperPath, executablePath, IntPtr.Zero))
                {
                    return true;
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
            }

            Thread.Sleep(200);
        }

        return false;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(
        string lpFileName,
        string lpExistingFileName,
        IntPtr lpSecurityAttributes);

    /// <summary>Último error al mostrar un toast en modo helper (diagnóstico).</summary>
    public static string? LastToastError { get; private set; }

    /// <summary>
    /// True cuando el proceso actual arrancó como GDM.Notify.exe, es decir,
    /// como instancia auxiliar no elevada dedicada a mostrar toasts.
    /// </summary>
    public static bool IsNotifyHelperProcess =>
        string.Equals(
            Path.GetFileName(Environment.ProcessPath),
            NotifyHelperFileName,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Borra los payloads pendientes de sesiones anteriores. Solo los invoca
    /// la instancia principal al arrancar: si GDM se cerró sin que un helper
    /// los consumiera, quedarían huérfanos hasta la próxima notificación.
    /// </summary>
    public static void CleanupStalePendingToasts()
    {
        try
        {
            string directory = PendingToastDirectory;
            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (string filePath in Directory.GetFiles(directory, $"{PendingToastPrefix}*"))
            {
                try
                {
                    File.Delete(filePath);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Muestra todos los toasts pendientes y borra sus archivos. Lo invoca el
    /// modo helper al arrancar; devuelve cuántos se mostraron. Tras vaciar la
    /// carpeta sigue sondeando unos segundos por si llegan payloads de
    /// notificaciones encoladas justo después, evitando lanzar otro proceso.
    /// </summary>
    public static int ShowPendingToasts()
    {
        int shown = 0;
        long deadline = Environment.TickCount64 + 6000;
        int emptyScans = 0;
        while (Environment.TickCount64 < deadline && emptyScans < 3)
        {
            if (DrainPendingToastFiles(ref shown) > 0)
            {
                emptyScans = 0;
            }
            else
            {
                emptyScans++;
                Thread.Sleep(400);
            }
        }

        return shown;
    }

    private static int DrainPendingToastFiles(ref int shown)
    {
        int processed = 0;
        try
        {
            string directory = PendingToastDirectory;
            if (!Directory.Exists(directory))
            {
                return 0;
            }

            foreach (string filePath in Directory.GetFiles(
                directory,
                $"{PendingToastPrefix}*{PendingToastSuffix}"))
            {
                // Los payloads con más de 2 minutos se consideran obsoletos:
                // suelen ser resto de una ráfaga (p. ej. bucles de eventos) y
                // mostrarlos tarde confunde con notificaciones incorrectas.
                try
                {
                    if (File.GetLastWriteTimeUtc(filePath) <
                        DateTime.UtcNow.AddMinutes(-2))
                    {
                        File.Delete(filePath);
                        continue;
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }

                // Reclamación atómica: se renombra a *.busy (fuera del patrón
                // pending-toast-*.json) antes de leerlo, así dos helpers que
                // arranquen a la vez no muestran el mismo toast dos veces.
                string busyPath = filePath + ".busy";
                try
                {
                    File.Move(filePath, busyPath);
                    processed++;
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                try
                {
                    string[]? parts = JsonSerializer.Deserialize<string[]>(
                        File.ReadAllText(busyPath));
                    if (parts is { Length: > 0 } &&
                        !string.IsNullOrWhiteSpace(parts[0]))
                    {
                        string? error = ShowRawToast(
                            parts[0],
                            parts.Skip(1).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray());
                        if (error is null)
                        {
                            shown++;
                        }
                        else
                        {
                            LastToastError = $"{parts[0]} -> {error}";
                        }
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or JsonException or UnauthorizedAccessException)
                {
                }

                try
                {
                    File.Delete(busyPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }

        return processed;
    }

    /// <summary>
    /// Muestra un toast sin comprobaciones de modo ni elevación. Lo usa el
    /// proceso helper GDM.Notify.exe, que siempre corre sin privilegios.
    /// Devuelve null si se mostró o el mensaje de error si falló.
    /// </summary>
    public static string? ShowRawToast(string title, params string[] lines)
    {
        try
        {
            BuildToastContent(title, lines).Show();
            return null;
        }
        catch (Exception exception)
        {
            return $"{exception.GetType().Name}: {exception.Message}";
        }
    }

    /// <summary>
    /// Construye el contenido del toast. Windows limita el número de elementos
    /// &lt;text&gt; por toast (~4), así que las líneas de detalle se unen en un
    /// único texto con saltos de línea en lugar de un AddText por línea.
    /// </summary>
    private static ToastContentBuilder BuildToastContent(string title, string[] lines)
    {
        var builder = new ToastContentBuilder().AddText(title);
        string body = string.Join("\n", lines.Where(line => !string.IsNullOrWhiteSpace(line)));
        if (!string.IsNullOrEmpty(body))
        {
            builder.AddText(body);
        }

        return builder;
    }

    private void ShowBalloon(string title, string[] lines)
    {
        if (_balloonFallback is null)
        {
            _diagnosticLog?.Invoke($"Notificación «{title}» sin vía de globo de bandeja disponible.");
            return;
        }

        string text = string.Join(" · ", lines.Where(line => !string.IsNullOrWhiteSpace(line)));
        if (text.Length > BalloonTextMaxLength)
        {
            text = text[..BalloonTextMaxLength];
        }

        try
        {
            _balloonFallback(title, text);
            _diagnosticLog?.Invoke($"Globo de bandeja solicitado para «{title}».");
        }
        catch (Exception exception)
        {
            _diagnosticLog?.Invoke(
                $"No se pudo mostrar la notificación de bandeja «{title}»: {exception.Message}");
        }
    }

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        // Para apps sin empaquetar, el toolkit crea el acceso directo con el
        // AppUserModelID la primera vez; las pulsaciones lanzan una nueva
        // instancia que SingleInstanceCoordinator redirige a la ventana activa.
        ToastNotificationManagerCompat.OnActivated += _ => _onToastActivated?.Invoke();
        _initialized = true;
    }

    private sealed record PendingNotification(string Title, string[] Lines);
}
