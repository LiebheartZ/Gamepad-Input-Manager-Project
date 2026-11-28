using System.Diagnostics;
using System.IO;
using GDM.Models;
using GDM.Services.Abstractions;

namespace GDM.Services.Persistence;

public sealed class AppLogService : IAppLogService
{
    private const int MaximumRetainedEntries = 1000;
    private const long MaximumLogFileBytes = 2 * 1024 * 1024;
    private readonly object _sync = new();
    private readonly Queue<DiagnosticLogEntry> _entries = new();
    private readonly string _logFilePath;

    public string LogFilePath => _logFilePath;
    public event Action<DiagnosticLogEntry>? EntryAdded;

    public AppLogService(IStoragePathService paths)
    {
        _logFilePath = paths.LogFilePath;
    }

    public IReadOnlyList<DiagnosticLogEntry> GetRecentEntries(int maximumCount = 500)
    {
        if (maximumCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        lock (_sync)
        {
            return _entries.TakeLast(maximumCount).Reverse().ToArray();
        }
    }

    public void Write(
        DiagnosticLogLevel level,
        string source,
        string message,
        Exception? exception = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var entry = new DiagnosticLogEntry
        {
            Timestamp = DateTimeOffset.Now,
            Level = level,
            Source = source,
            Message = message,
            Details = exception?.ToString()
        };

        lock (_sync)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > MaximumRetainedEntries)
            {
                _entries.Dequeue();
            }

            try
            {
                string? directory = Path.GetDirectoryName(_logFilePath);
                if (directory is null)
                {
                    throw new InvalidOperationException("La ruta del registro no contiene una carpeta.");
                }

                Directory.CreateDirectory(directory);

                // Rotación simple: al superar 2 MB el archivo actual pasa a
                // gdm.old.log (sobrescribiendo el anterior) y se empieza uno
                // nuevo. Evita que el registro crezca indefinidamente en
                // sesiones largas con la app en bandeja.
                var fileInfo = new FileInfo(_logFilePath);
                if (fileInfo.Exists && fileInfo.Length > MaximumLogFileBytes)
                {
                    string oldPath = $"{_logFilePath}.old";
                    if (File.Exists(oldPath))
                    {
                        File.Delete(oldPath);
                    }

                    File.Move(_logFilePath, oldPath);
                }

                File.AppendAllText(_logFilePath, Format(entry) + Environment.NewLine);
            }
            catch (IOException logException)
            {
                Trace.WriteLine($"No se pudo escribir el registro de GDM: {logException}");
            }
            catch (UnauthorizedAccessException logException)
            {
                Trace.WriteLine($"No se pudo escribir el registro de GDM: {logException}");
            }
        }

        EntryAdded?.Invoke(entry);
    }

    private static string Format(DiagnosticLogEntry entry)
    {
        string line =
            $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{entry.Level.ToString().ToUpperInvariant()}] [{entry.Source}] {entry.Message}";

        return string.IsNullOrWhiteSpace(entry.Details)
            ? line
            : $"{line}{Environment.NewLine}{entry.Details}";
    }
}
