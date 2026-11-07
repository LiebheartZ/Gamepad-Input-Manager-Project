using GDM.Models;

namespace GDM.Services.Abstractions;

public interface IAppLogService
{
    string LogFilePath { get; }
    event Action<DiagnosticLogEntry>? EntryAdded;
    IReadOnlyList<DiagnosticLogEntry> GetRecentEntries(int maximumCount = 500);
    void Write(DiagnosticLogLevel level, string source, string message, Exception? exception = null);
}
